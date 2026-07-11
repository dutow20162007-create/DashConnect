namespace DashConnect.Core.Models;

/// <summary>
/// A single, fully-resolved Zapret DPI-desync strategy: the exact argument vector that will
/// be passed to <c>winws.exe</c>. Produced by parsing a Zapret preset <c>.bat</c> file.
/// </summary>
public sealed class ZapretStrategy
{
    public required string Name { get; init; }          // e.g. "general (ALT)"
    public required string SourceFile { get; init; }    // absolute path to the .bat preset
    public required IReadOnlyList<string> Arguments { get; init; } // resolved argv for winws.exe

    /// <summary>A shorter tag used in compact UI (first meaningful desync mode).</summary>
    public string ShortTag
    {
        get
        {
            var arg = Arguments.FirstOrDefault(a => a.StartsWith("--dpi-desync=", StringComparison.Ordinal));
            return arg is null ? Name : arg["--dpi-desync=".Length..];
        }
    }

    public string DisplayArguments => string.Join(' ', Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    public override string ToString() => Name;
}
