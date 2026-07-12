using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DashConnect.App.Infra;
using DashConnect.Core.Config;
using DashConnect.Core.Diagnostics;
using DashConnect.Core.Logging;
using DashConnect.Core.Models;
using DashConnect.Core.Network;
using DashConnect.Core.Services;
using DashConnect.Core.Singbox;
using DashConnect.Core.Util;
using DashConnect.Core.Zapret;

namespace DashConnect.App.ViewModels;

/// <summary>The dashboard view model. Binds the orchestrator to the UI and marshals engine events
/// onto the WPF dispatcher thread.</summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly AppOrchestrator _orchestrator;
    private readonly AppConfig _config;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private const int MaxLogLines = 400;

    private string _statusText = "Готов к работе";
    private EngineState _state = EngineState.Stopped;
    private string _activeStrategyName = "—";
    private string _newDomainInput = "";
    private string _newAppInput = "";
    private DateTime? _connectedAt;
    private string _connectionTime = "00:00:00";
    private int _activeView;
    private SingboxProfile? _selectedProfile;

    public MainViewModel(AppOrchestrator orchestrator, AppConfig config, Dispatcher dispatcher)
    {
        _orchestrator = orchestrator;
        _config = config;
        _dispatcher = dispatcher;

        foreach (var t in ConnectivityTester.DefaultTargets)
            Diagnostics.Add(new HostChipViewModel(t.Label, t.Host));

        foreach (var p in SubscriptionManager.Load()) Profiles.Add(p);

        try { foreach (var n in StrategyProvider.ListNames(_config.ZapretRoot)) Presets.Add(n); }
        catch { /* invalid Zapret path — picker just stays empty */ }
        _selectedProfile = Profiles.FirstOrDefault(p => p.Name == _config.SelectedProfileName)
                           ?? Profiles.FirstOrDefault();

        try { foreach (var a in GameRoutesStore.LoadUserApps()) VpnApps.Add(a); }
        catch { /* first run — empty list */ }

        ToggleConnectCommand = new RelayCommand(ToggleConnect);
        RunDiagnosticsCommand = new AsyncRelayCommand(RunDiagnosticsAsync, () => !IsBusy);
        RefreshSubscriptionCommand = new AsyncRelayCommand(RefreshSubscriptionAsync, () => !IsBusy);
        AddDomainCommand = new RelayCommand(AddDomain, () => !string.IsNullOrWhiteSpace(NewDomainInput));
        FixTelegramCommand = new AsyncRelayCommand(FixTelegramAsync, () => !IsBusy);
        AddVpnAppCommand = new RelayCommand(AddVpnApp, () => !string.IsNullOrWhiteSpace(NewAppInput));
        PickVpnAppCommand = new RelayCommand(PickVpnApp);
        RemoveVpnAppCommand = new RelayCommand(RemoveVpnApp);
        OpenDataFolderCommand = new RelayCommand(() => OpenPath(Paths.AppDataDir));
        OpenZapretFolderCommand = new RelayCommand(() => OpenPath(_config.ZapretRoot));
        ClearLogCommand = new RelayCommand(() => LogLines.Clear());
        SetProxyModeCommand = new RelayCommand(() => VpnFull = false);
        SetTunnelModeCommand = new RelayCommand(() => VpnFull = true);
        ShowViewCommand = new RelayCommand(o => { if (int.TryParse(o?.ToString(), out var v)) ActiveView = v; });
        PinSettingsCommand = new RelayCommand(PinSettings);
        ClearPinCommand = new RelayCommand(ClearPin);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateTimer();

        _orchestrator.StatusChanged += OnStatus;
        _orchestrator.ProgressChanged += OnProgress;
        _orchestrator.StateChanged += OnState;
        _orchestrator.DiagnosticsUpdated += OnDiagnostics;
        _orchestrator.StrategySelected += OnStrategy;
        Log.Entry += OnLog;
    }

    // ---- Commands ----
    public ICommand ToggleConnectCommand { get; }
    public ICommand RunDiagnosticsCommand { get; }
    public ICommand RefreshSubscriptionCommand { get; }
    public ICommand AddDomainCommand { get; }
    public ICommand FixTelegramCommand { get; }
    public ICommand AddVpnAppCommand { get; }
    public ICommand PickVpnAppCommand { get; }
    public ICommand RemoveVpnAppCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenZapretFolderCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand SetProxyModeCommand { get; }
    public ICommand SetTunnelModeCommand { get; }
    public ICommand ShowViewCommand { get; }
    public ICommand PinSettingsCommand { get; }
    public ICommand ClearPinCommand { get; }

    public string PinnedInfo => string.IsNullOrEmpty(_config.PreferredStrategy)
        ? "Не закреплено — подбирает пресет при запуске"
        : $"Закреплено: {_config.PreferredStrategy} (запуск без сканирования)";

    // ---- Collections ----
    public ObservableCollection<HostChipViewModel> Diagnostics { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<SingboxProfile> Profiles { get; } = new();
    public ObservableCollection<string> VpnApps { get; } = new();

    /// <summary>App version shown in the sidebar (e.g. "v1.0.11").</summary>
    public string AppVersion => "v" + DashConnect.Core.Update.UpdateChecker.CurrentVersion;

    // ---- Navigation (0 = Подключение, 1 = Настройки, 2 = Журнал) ----
    public int ActiveView
    {
        get => _activeView;
        set
        {
            if (Set(ref _activeView, value))
            {
                OnPropertyChanged(nameof(IsConnectView));
                OnPropertyChanged(nameof(IsSettingsView));
                OnPropertyChanged(nameof(IsLogView));
            }
        }
    }
    public bool IsConnectView => ActiveView == 0;
    public bool IsSettingsView => ActiveView == 1;
    public bool IsLogView => ActiveView == 2;

    // ---- Status ----
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string ActiveStrategyName { get => _activeStrategyName; private set => Set(ref _activeStrategyName, value); }
    public string ConnectionTime { get => _connectionTime; private set => Set(ref _connectionTime, value); }

    private double _progress;
    /// <summary>Strategy-selection progress, 0..1 (shown as a bar while testing).</summary>
    public double ProgressValue { get => _progress; private set => Set(ref _progress, value); }

    public EngineState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                if (value == EngineState.Running) { _connectedAt = DateTime.Now; _timer.Start(); }
                else { _timer.Stop(); _connectedAt = null; ConnectionTime = "00:00:00"; }

                if (value is not (EngineState.Starting or EngineState.Testing)) ProgressValue = 0;

                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(ConnectButtonGlyph));
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(CanEditSettings));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsConnected => State == EngineState.Running;
    public bool IsBusy => State is EngineState.Starting or EngineState.Testing;
    public bool CanEditSettings => !IsBusy;

    public string ConnectButtonGlyph => State switch
    {
        EngineState.Running => "⏸",
        EngineState.Starting or EngineState.Testing => "…",
        _ => "▶",
    };

    public string StateLabel => State switch
    {
        EngineState.Running => "Подключено",
        EngineState.Starting => "Запуск…",
        EngineState.Testing => "Проверка…",
        EngineState.Error => "Ошибка",
        _ => "Отключено",
    };

    public Brush StateBrush => State switch
    {
        EngineState.Running => Palette.Green,
        EngineState.Starting or EngineState.Testing => Palette.Amber,
        EngineState.Error => Palette.Red,
        _ => Palette.Muted,
    };

    // ---- Config-bound settings ----
    public bool DpiBypassEnabled
    {
        get => _config.DpiBypassEnabled;
        set { if (_config.DpiBypassEnabled != value) { _config.DpiBypassEnabled = value; OnPropertyChanged(); Save(); } }
    }

    public bool GameDpiEnabled
    {
        get => _config.GameDpiEnabled;
        set { if (_config.GameDpiEnabled != value) { _config.GameDpiEnabled = value; OnPropertyChanged(); Save(); } }
    }

    public bool TelegramFixEnabled
    {
        get => _config.TelegramFixEnabled;
        set { if (_config.TelegramFixEnabled != value) { _config.TelegramFixEnabled = value; OnPropertyChanged(); Save(); } }
    }

    public bool VpnEnabled
    {
        get => _config.VpnEnabled;
        set { if (_config.VpnEnabled != value) { _config.VpnEnabled = value; OnPropertyChanged(); Save(); } }
    }

    public bool VpnFull
    {
        get => _config.VpnFull;
        set
        {
            if (_config.VpnFull != value)
            {
                _config.VpnFull = value; OnPropertyChanged(); Save();
                OnPropertyChanged(nameof(ProxyModeBrush));
                OnPropertyChanged(nameof(TunnelModeBrush));
            }
        }
    }

    public Brush ProxyModeBrush => !VpnFull ? Palette.SegActive : Brushes.Transparent;
    public Brush TunnelModeBrush => VpnFull ? Palette.SegActive : Brushes.Transparent;

    public bool AutoSelect
    {
        get => _config.AutoSelect;
        set { if (_config.AutoSelect != value) { _config.AutoSelect = value; OnPropertyChanged(); Save(); } }
    }

    /// <summary>All available preset names (for the picker).</summary>
    public ObservableCollection<string> Presets { get; } = new();

    public string? SelectedPreset
    {
        get => _config.PreferredStrategy;
        set
        {
            if (_config.PreferredStrategy != value)
            {
                _config.PreferredStrategy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PinnedInfo));
                Save();
            }
        }
    }

    public string ZapretRoot
    {
        get => _config.ZapretRoot;
        set { if (_config.ZapretRoot != value) { _config.ZapretRoot = value; OnPropertyChanged(); Save(); } }
    }

    public string SubscriptionUrl
    {
        get => _config.SubscriptionUrl;
        set { if (_config.SubscriptionUrl != value) { _config.SubscriptionUrl = value; OnPropertyChanged(); Save(); } }
    }

    public SingboxProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (Set(ref _selectedProfile, value))
            {
                _config.SelectedProfileName = value?.Name;
                Save();
            }
        }
    }

    public bool MinimizeToTrayOnClose
    {
        get => _config.MinimizeToTrayOnClose;
        set { if (_config.MinimizeToTrayOnClose != value) { _config.MinimizeToTrayOnClose = value; OnPropertyChanged(); Save(); } }
    }

    public string NewDomainInput
    {
        get => _newDomainInput;
        set { if (Set(ref _newDomainInput, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string NewAppInput
    {
        get => _newAppInput;
        set { if (Set(ref _newAppInput, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    // ---- Command handlers ----
    private void ToggleConnect()
    {
        if (State is EngineState.Running or EngineState.Starting or EngineState.Testing)
            _ = _orchestrator.DisconnectAsync();
        else { Save(); _ = ConnectAsync(); }
    }

    private bool _connecting;

    private async Task ConnectAsync()
    {
        if (_connecting) return; // ignore re-entrant clicks while a connect is spinning up
        _connecting = true;
        // Run the whole connect sequence on a background thread so the UI never blocks.
        try
        {
            await Task.Run(() => _orchestrator.ConnectAsync(_config));
            Save(); // persist a freshly generated Telegram proxy secret, if any

            // First connect ever: hand Telegram the proxy link once, fully in the background — launch
            // Telegram if it's closed and best-effort auto-confirm its prompt. Afterwards Telegram
            // remembers the proxy and every connect brings it back with nothing to do.
            if (_config.TelegramFixEnabled && !_config.TelegramConfigured && _orchestrator.TelegramProxyActive)
            {
                var link = _orchestrator.TelegramProxyLink;
                _config.TelegramConfigured = true;
                Save();
                OnUi(() => StatusText = "Настраиваю Telegram в фоне…");
                bool auto = await Task.Run(() => TelegramDesktop.ApplyProxyLinkAsync(link));
                OnUi(() => StatusText = auto
                    ? "Готово — Telegram подключён автоматически."
                    : "Почти готово: в Telegram один раз нажми «Подключить прокси» — дальше он сам.");
            }
        }
        catch (Exception ex) { Log.Error("ui", "connect failed", ex); }
        finally { _connecting = false; }
    }

    private async Task RunDiagnosticsAsync()
    {
        try { await _orchestrator.RunDiagnosticsAsync(); }
        catch (Exception ex) { Log.Error("ui", "diagnostics failed", ex); }
    }

    private async Task RefreshSubscriptionAsync()
    {
        var url = SubscriptionUrl.Trim();
        if (url.Length == 0) { StatusText = "Укажите ссылку-подписку"; return; }
        StatusText = "Загружаю подписку…";
        try
        {
            var profiles = await SubscriptionManager.FetchAsync(url);
            SubscriptionManager.Save(profiles);
            OnUi(() =>
            {
                Profiles.Clear();
                foreach (var p in profiles) Profiles.Add(p);
                SelectedProfile = Profiles.FirstOrDefault(p => p.Name == _config.SelectedProfileName)
                                  ?? Profiles.FirstOrDefault();
                StatusText = profiles.Count > 0
                    ? $"Загружено серверов: {profiles.Count}"
                    : "Серверы не найдены в подписке";
            });
        }
        catch (Exception ex)
        {
            OnUi(() => StatusText = $"Подписка недоступна: {ex.Message}");
        }
    }

    private async Task FixTelegramAsync()
    {
        try
        {
            StatusText = "Настраиваю Telegram…";
            var result = await Task.Run(() => _orchestrator.FixTelegramAsync(_config, s => OnUi(() => StatusText = s)));
            Save(); // persist the freshly generated proxy secret
            if (!string.IsNullOrEmpty(result.TgLink))
            {
                _config.TelegramConfigured = true;
                Save();
                bool auto = await Task.Run(() => TelegramDesktop.ApplyProxyLinkAsync(result.TgLink!));
                OnUi(() => StatusText = auto ? "Telegram подключён автоматически." : result.Message);
            }
            else OnUi(() => StatusText = result.Message);
        }
        catch (Exception ex)
        {
            Log.Error("ui", "telegram fix failed", ex);
            OnUi(() => StatusText = $"Telegram: ошибка — {ex.Message}");
        }
    }

    private void AddDomain()
    {
        var domain = NewDomainInput.Trim();
        if (domain.Length == 0) return;
        int added = HostlistManager.AppendDomains(_config.ZapretRoot, new[] { domain });
        StatusText = added > 0
            ? $"Добавлено «{domain}» — переподключитесь, чтобы применить"
            : $"«{domain}» уже есть в списке";
        NewDomainInput = "";
    }

    private void AddVpnApp()
    {
        var app = NewAppInput.Trim();
        if (app.Length == 0) return;
        if (!app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app += ".exe";
        if (!VpnApps.Any(a => a.Equals(app, StringComparison.OrdinalIgnoreCase)))
        {
            VpnApps.Add(app);
            GameRoutesStore.SaveUserApps(VpnApps);
            StatusText = $"«{app}» добавлена в VPN — переподключитесь, чтобы применить";
        }
        NewAppInput = "";
    }

    private void PickVpnApp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите программу (.exe)",
            Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*",
            CheckFileExists = true,
        };
        try { dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); } catch { }

        if (dlg.ShowDialog() != true) return;
        var name = System.IO.Path.GetFileName(dlg.FileName);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!VpnApps.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            VpnApps.Add(name);
            GameRoutesStore.SaveUserApps(VpnApps);
            StatusText = $"«{name}» добавлена в VPN — переподключитесь, чтобы применить";
        }
    }

    private void RemoveVpnApp(object? app)
    {
        if (app is string s && VpnApps.Remove(s))
        {
            GameRoutesStore.SaveUserApps(VpnApps);
            StatusText = $"«{s}» убрана из VPN — переподключитесь, чтобы применить";
        }
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("ui", $"open '{path}': {ex.Message}"); }
    }

    private void PinSettings()
    {
        if (!IsConnected) { StatusText = "Сначала подключитесь"; return; }
        var name = ActiveStrategyName;
        if (string.IsNullOrEmpty(name) || name == "—")
        {
            StatusText = "Нет активной стратегии для сохранения";
            return;
        }
        _config.PreferredStrategy = name;
        Save();
        OnPropertyChanged(nameof(PinnedInfo));
        OnPropertyChanged(nameof(SelectedPreset));
        StatusText = $"Сохранено — при запуске сразу «{name}»";
    }

    private void ClearPin()
    {
        _config.PreferredStrategy = null;
        Save();
        OnPropertyChanged(nameof(PinnedInfo));
        StatusText = "Закрепление сброшено — будет подбор при запуске";
    }

    private void Save() => ConfigStore.Save(_config);

    private void UpdateTimer()
    {
        if (_connectedAt is { } t)
            ConnectionTime = (DateTime.Now - t).ToString(@"hh\:mm\:ss");
    }

    // ---- Orchestrator events (marshalled to UI thread) ----
    private void OnStatus(string s) => OnUi(() => StatusText = s);
    private void OnProgress(double f) => OnUi(() => ProgressValue = f);
    private void OnState(EngineState s) => OnUi(() => State = s);
    private void OnStrategy(ZapretStrategy? s) => OnUi(() => ActiveStrategyName = s?.Name ?? "—");

    private void OnDiagnostics(DiagnosticsReport report) => OnUi(() =>
    {
        foreach (var r in report.Results)
            Diagnostics.FirstOrDefault(c => c.Label == r.Label)?.Apply(r);
    });

    private void OnLog(LogEntry entry) => OnUi(() =>
    {
        LogLines.Add(entry.ToString());
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    });

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }
}
