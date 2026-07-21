using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DashConnect.Core.Config;

/// <summary>
/// Transparently encrypts a string property at rest in config.json using Windows DPAPI
/// (per-user scope), so secrets — VPN subscription links, the raw AmneziaWG private key, the
/// MTProto secret — are never stored as clear text. The in-memory value is always plain: encryption
/// happens only when (de)serializing.
///
/// Wire format is <c>dpapi:&lt;base64&gt;</c>. On read, a value WITHOUT that prefix is returned
/// verbatim (backward-compatible with configs written before this, and a graceful fallback on
/// non-Windows / when DPAPI is unavailable — e.g. the Linux SelfTest run).
/// </summary>
public sealed class DpapiStringConverter : JsonConverter<string>
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DashConnect.config.v1");

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString() ?? "";
        if (!raw.StartsWith(Prefix, StringComparison.Ordinal)) return raw; // plaintext / legacy
        if (!OperatingSystem.IsWindows()) return "";                       // can't decrypt here
        try
        {
            var cipher = Convert.FromBase64String(raw[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return ""; // corrupt blob or written under a different user account — treat as unset
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value) || !OperatingSystem.IsWindows())
        {
            writer.WriteStringValue(value); // nothing to protect, or DPAPI unavailable
            return;
        }
        try
        {
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            writer.WriteStringValue(Prefix + Convert.ToBase64String(cipher));
        }
        catch
        {
            writer.WriteStringValue(value); // never lose the setting if DPAPI throws
        }
    }
}
