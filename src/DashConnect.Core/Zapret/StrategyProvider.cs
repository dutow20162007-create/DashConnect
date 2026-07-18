using System.Text;
using DashConnect.Core.Config;
using DashConnect.Core.Logging;
using DashConnect.Core.Models;

namespace DashConnect.Core.Zapret;

/// <summary>
/// Discovers Zapret preset <c>.bat</c> files in the distribution root and parses each one into a
/// fully-resolved <see cref="ZapretStrategy"/> (the exact argv handed to <c>winws.exe</c>).
///
/// The presets ARE the strategy database. We never invent flags — we faithfully reproduce the
/// batch-file substitutions (%BIN%, %LISTS%, %GameFilter*%) and escaping (^!) that cmd.exe would
/// perform, so launching winws.exe directly is byte-for-byte equivalent to double-clicking the .bat.
/// </summary>
public static class StrategyProvider
{
    // Presets probed first during a fast scan (broad-coverage, low-latency in practice).
    private static readonly string[] FastOrder =
    {
        "general (Dash Connect)",       // custom — strongest (FAKE TLS AUTO base), tried first
        "general (Dash Connect ALT)",   // custom — fakedsplit base
        "general",
        "general (ALT)",
        "general (ALT2)",
        "general (FAKE TLS AUTO)",
        "general (SIMPLE FAKE)",
    };

    /// <summary>Fallback preset launched when auto-select is off and nothing is pinned.</summary>
    public const string DefaultStrategyName = "general (Dash Connect)";

    public static IReadOnlyList<string> FastSubset => FastOrder;

    /// <summary>Just the preset names, ordered — for the UI picker.</summary>
    public static IReadOnlyList<string> ListNames(string root)
        => LoadAll(root, GameFilterMode.Disabled).Select(s => s.Name).ToList();

    public static string BinDir(string root) => Path.Combine(root, "bin");
    public static string ListsDir(string root) => Path.Combine(root, "lists");
    public static string WinwsPath(string root) => Path.Combine(root, "bin", "winws.exe");

    public static bool IsValidRoot(string root)
        => Directory.Exists(root) && File.Exists(WinwsPath(root)) && Directory.Exists(ListsDir(root));

    private static readonly object CacheGate = new();
    private static (string Root, GameFilterMode Filter, long Stamp, IReadOnlyList<ZapretStrategy> Items)? _cache;

    /// <summary>
    /// Cheap fingerprint of the preset folder: file paths + write times, no file CONTENT read. Used to
    /// invalidate the parse cache, so editing/adding a .bat is still picked up without re-parsing all
    /// ~50 presets on every call.
    /// </summary>
    private static long FolderStamp(string root)
    {
        long stamp = 17;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.bat", SearchOption.TopDirectoryOnly))
            {
                stamp = stamp * 31 + f.GetHashCode();
                try { stamp = stamp * 31 + File.GetLastWriteTimeUtc(f).Ticks; } catch { }
            }
        }
        catch { return -1; } // unreadable folder — never serve a cache hit
        return stamp;
    }

    /// <summary>
    /// Parse all presets. Ordered fast-subset-first, then the rest alphabetically.
    ///
    /// Results are CACHED. Parsing reads and regexes every .bat in the folder, and this used to run on
    /// the UI thread at startup (via <see cref="ListNames"/>) plus up to twice more on every connect —
    /// enough to stall the window visibly. <see cref="ZapretStrategy"/> is immutable, so handing out the
    /// same instances is safe.
    /// </summary>
    public static IReadOnlyList<ZapretStrategy> LoadAll(string root, GameFilterMode gameFilter)
    {
        var stamp = FolderStamp(root);
        if (stamp != -1)
        {
            lock (CacheGate)
            {
                if (_cache is { } c && c.Stamp == stamp && c.Filter == gameFilter &&
                    string.Equals(c.Root, root, StringComparison.OrdinalIgnoreCase))
                    return c.Items;
            }
        }

        var parsed = LoadAllUncached(root, gameFilter);
        if (stamp != -1)
            lock (CacheGate) { _cache = (root, gameFilter, stamp, parsed); }
        return parsed;
    }

    private static IReadOnlyList<ZapretStrategy> LoadAllUncached(string root, GameFilterMode gameFilter)
    {
        var result = new List<ZapretStrategy>();
        if (!Directory.Exists(root))
        {
            Log.Error("strategy", $"Zapret root not found: {root}");
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.bat", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(Path.GetFileName(file), "service.bat", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var strategy = ParseFile(file, root, gameFilter);
                if (strategy is not null) result.Add(strategy);
            }
            catch (Exception ex)
            {
                Log.Warn("strategy", $"parse '{name}' failed: {ex.Message}");
            }
        }

        result.Sort(CompareByPriority);
        Log.Info("strategy", $"loaded {result.Count} strategies from {root}");
        return result;
    }

    private static int CompareByPriority(ZapretStrategy a, ZapretStrategy b)
    {
        int ia = Array.IndexOf(FastOrder, a.Name);
        int ib = Array.IndexOf(FastOrder, b.Name);
        if (ia < 0) ia = int.MaxValue;
        if (ib < 0) ib = int.MaxValue;
        return ia != ib ? ia.CompareTo(ib) : string.CompareOrdinal(a.Name, b.Name);
    }

    public static ZapretStrategy? ParseFile(string batFile, string root, GameFilterMode gameFilter)
    {
        var lines = File.ReadAllLines(batFile);
        var command = ExtractCommand(lines);
        if (command is null) return null;

        var argsSection = StripLauncherPrefix(command);
        var tokens = Tokenize(argsSection);

        var (gfAll, gfTcp, gfUdp) = ResolveGameFilter(gameFilter);
        var binDir = BinDir(root) + Path.DirectorySeparatorChar;
        var listsDir = ListsDir(root) + Path.DirectorySeparatorChar;
        var rootDir = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;

        var resolved = new List<string>(tokens.Count);
        foreach (var raw in tokens)
        {
            var t = raw
                .Replace("%GameFilterTCP%", gfTcp, StringComparison.Ordinal)
                .Replace("%GameFilterUDP%", gfUdp, StringComparison.Ordinal)
                .Replace("%GameFilter%", gfAll, StringComparison.Ordinal)
                .Replace("%BIN%", binDir, StringComparison.Ordinal)
                .Replace("%LISTS%", listsDir, StringComparison.Ordinal)
                .Replace("%~dp0", rootDir, StringComparison.Ordinal)
                .Replace("^!", "!", StringComparison.Ordinal);

            if (t.Length > 0) resolved.Add(t);
        }

        if (resolved.Count == 0) return null;

        return new ZapretStrategy
        {
            Name = Path.GetFileNameWithoutExtension(batFile),
            SourceFile = batFile,
            Arguments = resolved,
        };
    }

    private static (string all, string tcp, string udp) ResolveGameFilter(GameFilterMode mode) => mode switch
    {
        GameFilterMode.All => ("1024-65535", "1024-65535", "1024-65535"),
        GameFilterMode.Tcp => ("1024-65535", "1024-65535", "12"),
        GameFilterMode.Udp => ("1024-65535", "12", "1024-65535"),
        _ => ("12", "12", "12"), // Disabled — port 12 is an inert placeholder, matching stock Zapret
    };

    /// <summary>
    /// Collects the (possibly multi-line, caret-continued) command that starts winws.exe.
    /// </summary>
    private static string? ExtractCommand(string[] lines)
    {
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("winws.exe", StringComparison.OrdinalIgnoreCase)) { start = i; break; }
        }
        if (start < 0) return null;

        var sb = new StringBuilder();
        for (int i = start; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            bool continued = line.EndsWith('^');
            if (continued) line = line[..^1].TrimEnd();
            sb.Append(line);
            sb.Append(' ');
            if (!continued) break;
        }
        return sb.ToString().Trim();
    }

    /// <summary>Removes the "start "..." /min "%BIN%winws.exe"" launcher prefix.</summary>
    private static string StripLauncherPrefix(string command)
    {
        int idx = command.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return command;
        int after = idx + "winws.exe".Length;
        // skip the closing quote of "%BIN%winws.exe"
        while (after < command.Length && (command[after] == '"' || command[after] == ' '))
            after++;
        return command[after..].Trim();
    }

    /// <summary>
    /// Splits an argument string into tokens, honoring double-quoted spans (quotes are removed).
    /// This mirrors how cmd.exe/CreateProcess groups quoted paths.
    /// </summary>
    public static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        bool started = false;

        foreach (var ch in s)
        {
            if (ch == '"') { inQuotes = !inQuotes; started = true; continue; }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (started) { tokens.Add(sb.ToString()); sb.Clear(); started = false; }
                continue;
            }
            sb.Append(ch);
            started = true;
        }
        if (started) tokens.Add(sb.ToString());
        return tokens;
    }
}
