using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DashConnect.App.ViewModels;

namespace DashConnect.App;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private bool _wasConnected;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        StateChanged += OnWindowStateChanged;
    }

    /// <summary>Ask DWM (Windows 11) to round the borderless window's corners.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* pre-Win11: no rounding, harmless */ }
    }

    // ---- custom chrome ----
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { /* mid-gesture edge cases */ }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- data context wiring ----
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
        {
            oldVm.LogLines.CollectionChanged -= OnLogChanged;
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }
        if (e.NewValue is MainViewModel vm)
        {
            vm.LogLines.CollectionChanged += OnLogChanged;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsConnected) && sender is MainViewModel vm)
        {
            if (vm.IsConnected && !_wasConnected) Particles.Burst(); // whoosh up on connect
            _wasConnected = vm.IsConnected;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // NEVER call ScrollIntoView synchronously here: it forces a layout pass while the ListBox's
        // VirtualizingStackPanel is still mid-processing this CollectionChanged (Add + trim RemoveAt),
        // which throws "ItemsControl does not match its items source" — and that used to feed an
        // exception loop that froze the whole window. Defer it to Background priority + guard it.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            try { if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]); }
            catch { /* transient generator state — ignore */ }
        });
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && DataContext is MainViewModel { MinimizeToTrayOnClose: true })
            Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (!app.IsExiting && DataContext is MainViewModel { MinimizeToTrayOnClose: true })
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
