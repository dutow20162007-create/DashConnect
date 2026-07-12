using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using DashConnect.App.Tray;
using DashConnect.App.ViewModels;
using DashConnect.Core.Config;
using DashConnect.Core.Logging;
using DashConnect.Core.Models;
using DashConnect.Core.Services;
using DashConnect.Core.Update;
using DashConnect.Core.Util;
using DashConnect.Core.Zapret;

namespace DashConnect.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppOrchestrator? _orchestrator;
    private TrayIconManager? _tray;
    private AppConfig? _config;
    private MainWindow? _window;

    /// <summary>True once the user has chosen to fully exit (so window-close no longer hides to tray).</summary>
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, "DashConnect.SingleInstance.9F2A", out bool created);
        if (!created)
        {
            MessageBox.Show("Dash Connect уже запущен — проверьте системный трей.",
                "Dash Connect", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Paths.EnsureDirectories();
        Log.ConfigureFile(Paths.LogFile);
        Log.Info("app", "starting Dash Connect 1.0");

        if (!AdminGuard.IsElevated())
        {
            // The manifest already forces elevation, but if we ever start unelevated (manifest
            // stripped, launched via an odd host) relaunch OURSELVES elevated through UAC and exit —
            // so the app always ends up running as administrator without the user doing anything.
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                    });
            }
            catch { /* user dismissed the UAC prompt — nothing to do but exit */ }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            // Write straight to the log FILE — never through Log's event, which re-enters the UI log
            // list and could create an exception feedback loop that freezes the window.
            try
            {
                System.IO.File.AppendAllText(Paths.LogFile,
                    $"{DateTime.Now:HH:mm:ss} [ERROR] app: UI exception :: {args.Exception.Message}{Environment.NewLine}");
            }
            catch { }
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("app", "unhandled exception", ex);
        };

        _config = ConfigStore.Load();

        // Crash recovery: a leftover DNS backup means a prior session applied clean DNS and never
        // reverted (e.g. it was killed). Restore the user's original resolver before anything else.
        if (DashConnect.Core.Network.DnsManager.HasPendingBackup)
            _ = DashConnect.Core.Network.DnsManager.RevertAsync();

        // Portable install: if the configured Zapret folder is gone (fresh MSI install on a new PC),
        // fall back to the copy shipped next to the exe (installed as <app>\zapret).
        if (!StrategyProvider.IsValidRoot(_config.ZapretRoot))
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "zapret");
            if (StrategyProvider.IsValidRoot(beside))
            {
                _config.ZapretRoot = beside;
                ConfigStore.Save(_config);
            }
        }

        EnsureDesktopShortcut();

        _orchestrator = new AppOrchestrator();
        var vm = new MainViewModel(_orchestrator, _config, Dispatcher);

        _tray = new TrayIconManager();
        _tray.OpenRequested += ShowMainWindow;
        _tray.ToggleRequested += () => vm.ToggleConnectCommand.Execute(null);
        _tray.ExitRequested += ExitApp;

        _orchestrator.StateChanged += s => Dispatcher.Invoke(() =>
        {
            UpdateTray();
            // Re-check for updates once we're connected: at startup the DPI bypass is off and
            // api/github.com may be blocked on the ISP, so the first check often silently fails.
            if (s == EngineState.Running && !_updateReChecked)
            {
                _updateReChecked = true;
                _ = CheckForUpdatesAsync();
            }
        });
        _orchestrator.StatusChanged += _ => Dispatcher.Invoke(UpdateTray);

        _window = new MainWindow { DataContext = vm };
        _window.Icon = IconFactory.CreateImageSource(active: false);
        _window.Show();
        UpdateTray();

        _ = CheckForUpdatesAsync();
    }

    /// <summary>Silently checks GitHub for a newer release; shows the dash-os update card if found.</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await UpdateChecker.CheckAsync();
            if (info is null || _window is null) return;
            new UpdateWindow(info) { Owner = _window }.Show();
        }
        catch (Exception ex) { Log.Debug("update", $"update flow: {ex.Message}"); }
    }

    /// <summary>
    /// Guarantees a desktop shortcut on the user's REAL desktop. A per-machine MSI drops its shortcut
    /// on the Public desktop, which OneDrive Desktop redirection can hide; <see cref="Environment.SpecialFolder.DesktopDirectory"/>
    /// resolves the actual (redirected) desktop, so the icon always shows. Created via WScript.Shell
    /// (late-bound COM, no extra reference) only when missing.
    /// </summary>
    private static void EnsureDesktopShortcut()
    {
        try
        {
            // Do this once. If the user later deletes the shortcut on purpose, don't fight them.
            var marker = System.IO.Path.Combine(Paths.AppDataDir, ".desktop-shortcut");
            if (System.IO.File.Exists(marker)) return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) return;
            var lnk = System.IO.Path.Combine(desktop, "Dash Connect.lnk");
            if (System.IO.File.Exists(lnk)) { Paths.EnsureDirectories(); System.IO.File.WriteAllText(marker, "1"); return; }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            var shell = Activator.CreateInstance(shellType);
            var sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var t = sc!.GetType();
            void Set(string prop, object val) => t.InvokeMember(prop, System.Reflection.BindingFlags.SetProperty, null, sc, new[] { val });
            Set("TargetPath", exe);
            Set("WorkingDirectory", System.IO.Path.GetDirectoryName(exe) ?? "");
            Set("IconLocation", exe + ",0");
            Set("Description", "Dash Connect — обход блокировок РФ без VPN");
            t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
            Paths.EnsureDirectories();
            System.IO.File.WriteAllText(marker, "1");
            Log.Info("app", "ярлык на рабочем столе создан");
        }
        catch (Exception ex) { Log.Debug("app", $"ярлык: {ex.Message}"); }
    }

    private void UpdateTray()
    {
        if (_orchestrator is null || _tray is null) return;
        var connected = _orchestrator.IsConnected;
        var label = _orchestrator.State switch
        {
            EngineState.Running => "защищено",
            EngineState.Starting or EngineState.Testing => "работаю…",
            EngineState.Error => "ошибка",
            _ => "отключено",
        };
        _tray.SetState(connected, label);
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private bool _exitStarted;
    private bool _updateReChecked;

    public void ExitApp()
    {
        if (_exitStarted) return;
        _exitStarted = true;
        IsExiting = true;
        Log.Info("app", "выход — останавливаю движки");
        _window?.Hide(); // feel instant; never block the UI thread with .Wait()

        var orch = _orchestrator;
        _ = Task.Run(async () =>
        {
            try
            {
                if (orch is not null)
                {
                    await orch.DisconnectAsync();
                    await orch.DisposeAsync();
                }
                await DashConnect.Core.Network.WarpManager.KillOrphansAsync(); // stop the WARP relay on exit
            }
            catch (Exception ex) { Log.Warn("app", $"очистка при выходе: {ex.Message}"); }
            finally
            {
                try { Dispatcher.Invoke(Shutdown); } catch { }
            }
        });

        // Safety net: force shutdown if cleanup ever stalls.
        var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        guard.Tick += (_, _) => { guard.Stop(); try { Shutdown(); } catch { } };
        guard.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
