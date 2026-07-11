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
            MessageBox.Show(
                "Dash Connect нужно запускать от имени администратора.\n\nWinDivert (обход DPI) и TUN-адаптер (игровой роутинг) требуют повышенных прав.",
                "Нужны права администратора", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        _orchestrator = new AppOrchestrator();
        var vm = new MainViewModel(_orchestrator, _config, Dispatcher);

        _tray = new TrayIconManager();
        _tray.OpenRequested += ShowMainWindow;
        _tray.ToggleRequested += () => vm.ToggleConnectCommand.Execute(null);
        _tray.ExitRequested += ExitApp;

        _orchestrator.StateChanged += _ => Dispatcher.Invoke(UpdateTray);
        _orchestrator.StatusChanged += _ => Dispatcher.Invoke(UpdateTray);

        _window = new MainWindow { DataContext = vm };
        _window.Icon = IconFactory.CreateImageSource(active: false);
        _window.Show();
        UpdateTray();

        _ = CheckForUpdatesAsync();
    }

    /// <summary>Silently checks GitHub for a newer release; offers a one-click update if found.</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await UpdateChecker.CheckAsync();
            if (info is null) return;

            var prompt = $"Доступна новая версия Dash Connect {info.Version}.\n\n" +
                         (string.IsNullOrWhiteSpace(info.Notes) ? "" : info.Notes.Trim() + "\n\n") +
                         "Скачать и установить сейчас?";
            if (MessageBox.Show(prompt, "Доступно обновление",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;

            var file = await UpdateChecker.DownloadAsync(info.DownloadUrl);
            if (file is null || !file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                // Couldn't fetch the MSI directly — open the releases page instead.
                try { Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesPage) { UseShellExecute = true }); } catch { }
                return;
            }

            // Launch the installer (it will replace files) and quit so they aren't locked.
            Process.Start(new ProcessStartInfo("msiexec", $"/i \"{file}\"") { UseShellExecute = true });
            ExitApp();
        }
        catch (Exception ex) { Log.Debug("update", $"update flow: {ex.Message}"); }
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
