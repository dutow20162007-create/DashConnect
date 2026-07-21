using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using DashConnect.Core.Logging;
using DashConnect.Core.Update;

namespace DashConnect.App;

/// <summary>
/// dash-os style update prompt: shows the new version + changelog with Skip / Install. Install
/// downloads the MSI in the background (progress bar), then launches a silent install and relaunches
/// the app — all without further clicks.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private bool _busy;

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        VersionText.Text = $"Dash Connect {info.Version}";
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
            ? "Улучшения и исправления."
            : info.Notes.Trim();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            try { DragMove(); } catch { }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return; // don't cancel mid-download
        Close();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SkipBtn.IsEnabled = false;
        InstallBtn.IsEnabled = false;
        StatusText.Visibility = Visibility.Visible;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "Скачивание…";

        var progress = new Progress<double>(p => Progress.Value = p);
        var file = await UpdateChecker.DownloadAsync(_info.DownloadUrl, progress, _info.Sha256Url);

        if (file is null || !file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback — couldn't get the MSI; open the releases page and close.
            StatusText.Text = "Не удалось скачать — открываю страницу загрузки…";
            try { Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesPage) { UseShellExecute = true }); } catch { }
            Close();
            return;
        }

        StatusText.Text = "Установка…";
        Progress.IsIndeterminate = true;

        // Background install: wait for this app to fully exit (so the exe unlocks), run msiexec
        // silently, then relaunch the freshly installed app. cmd chains it so it survives our exit.
        var installedExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Dash Connect", "DashConnect.exe");
        var cmd = $"/c ping -n 4 127.0.0.1 >nul & msiexec /i \"{file}\" /qn /norestart & start \"\" \"{installedExe}\"";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            Log.Warn("update", $"install launch failed: {ex.Message}");
            StatusText.Text = "Не удалось запустить установку.";
            _busy = false;
            SkipBtn.IsEnabled = true;
            InstallBtn.IsEnabled = true;
            return;
        }

        // Clean-exit the app (stops winws, restores DNS) so the installer can replace files.
        ((App)System.Windows.Application.Current).ExitApp();
    }
}
