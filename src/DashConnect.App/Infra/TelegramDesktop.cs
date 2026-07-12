using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Microsoft.Win32;
using DashConnect.Core.Logging;

namespace DashConnect.App.Infra;

/// <summary>
/// Drives Telegram Desktop for the hands-off proxy flow: launches it if it's closed, opens the
/// tg:// proxy link, and — best-effort — clicks Telegram's own "enable proxy" confirmation via UI
/// Automation so the user needn't touch anything.
///
/// This is deliberately the ONLY automation we do against Telegram: it invokes a button BY LOCALIZED
/// NAME and touches no files, so it can never affect the user's login/session (unlike editing tdata).
/// If the button isn't found (Telegram's Qt widgets don't always expose to UIA), it does nothing and
/// the one-tap manual confirm remains — never a blind click or key-press that could hit "Cancel".
/// </summary>
public static class TelegramDesktop
{
    // Confirm ("connect/enable this proxy") button captions across locales. Cancel/close is never listed.
    private static readonly string[] ConfirmNames =
    {
        "Подключить прокси", "Использовать прокси", "Включить прокси",
        "Enable proxy", "Connect proxy", "Use proxy",
        "Подключить", "Включить", "Enable", "Connect",
    };

    public static bool IsRunning => Process.GetProcessesByName("Telegram").Length > 0;

    /// <summary>Opens the tg:// proxy link, launching Telegram first if needed, then best-effort
    /// auto-confirms the prompt. Returns true if the confirmation was auto-clicked.</summary>
    public static async Task<bool> ApplyProxyLinkAsync(string tgLink, CancellationToken ct = default)
    {
        bool launched = EnsureRunning();
        if (launched)
        {
            // Give a cold start time to bring up its window before the link arrives.
            try { await Task.Delay(2500, ct); } catch { return false; }
        }

        try { Process.Start(new ProcessStartInfo(tgLink) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("tg", $"tg link: {ex.Message}"); return false; }

        return await TryConfirmProxyDialogAsync(ct);
    }

    /// <summary>Launches Telegram Desktop if it isn't already running. Returns true if it started it.</summary>
    public static bool EnsureRunning()
    {
        if (IsRunning) return false;
        var exe = ResolvePath();
        if (exe is null) { Log.Debug("tg", "Telegram.exe не найден — открою ссылку через обработчик tg://"); return false; }
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); return true; }
        catch (Exception ex) { Log.Warn("tg", $"launch Telegram: {ex.Message}"); return false; }
    }

    /// <summary>Finds Telegram.exe via the tg:// URL handler, then common install locations.</summary>
    private static string? ResolvePath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\tg\shell\open\command");
            if (key?.GetValue(null) is string cmd)
            {
                var m = Regex.Match(cmd, "\"([^\"]+?Telegram\\.exe)\"", RegexOptions.IgnoreCase);
                if (m.Success && File.Exists(m.Groups[1].Value)) return m.Groups[1].Value;
            }
        }
        catch { }

        foreach (var p in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Telegram Desktop", "Telegram.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Telegram Desktop", "Telegram.exe"),
        })
            if (File.Exists(p)) return p;

        return null;
    }

    /// <summary>Polls briefly for Telegram's proxy prompt and invokes the confirm button by name.
    /// Best-effort and login-safe; returns true only if it actually clicked the confirm control.</summary>
    private static async Task<bool> TryConfirmProxyDialogAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(7);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try { if (ClickConfirm()) { Log.Info("tg", "прокси подтверждён автоматически"); return true; } }
            catch { /* UIA can throw on transient window states — just retry */ }
            try { await Task.Delay(450, ct); } catch { return false; }
        }
        return false;
    }

    private static bool ClickConfirm()
    {
        foreach (var proc in Process.GetProcessesByName("Telegram"))
        {
            var h = proc.MainWindowHandle;
            if (h == IntPtr.Zero) continue;

            AutomationElement? root;
            try { root = AutomationElement.FromHandle(h); } catch { continue; }
            if (root is null) continue;

            var buttons = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

            foreach (AutomationElement b in buttons)
            {
                string name;
                try { name = b.Current.Name ?? ""; } catch { continue; }
                if (name.Length == 0) continue;

                if (ConfirmNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    && b.TryGetCurrentPattern(InvokePattern.Pattern, out var pat))
                {
                    try { ((InvokePattern)pat).Invoke(); return true; } catch { }
                }
            }
        }
        return false;
    }
}
