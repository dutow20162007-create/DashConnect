using System.Runtime.Versioning;
using System.Security.Principal;

namespace DashConnect.Core.Util;

public static class AdminGuard
{
    /// <summary>True when the current process holds the Administrators role token.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
