using DashConnect.Core.Logging;

namespace DashConnect.Core.Zapret;

/// <summary>
/// Manages the user-editable Zapret hostlists. New domains are appended to
/// <c>lists\list-general-user.txt</c> (the file every general preset already loads), so additions
/// take effect on the next winws launch without editing any preset.
/// </summary>
public static class HostlistManager
{
    private const string UserList = "list-general-user.txt";

    public static string UserListPath(string root) => Path.Combine(root, "lists", UserList);

    public static IReadOnlyList<string> ReadUserDomains(string root)
    {
        var path = UserListPath(root);
        if (!File.Exists(path)) return Array.Empty<string>();
        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
    }

    /// <summary>Appends new, de-duplicated domains. Returns how many were actually added.</summary>
    public static int AppendDomains(string root, IEnumerable<string> domains)
    {
        var path = UserListPath(root);
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
        {
            Log.Warn("hostlist", $"lists dir missing for {root}");
            return 0;
        }

        var existing = new HashSet<string>(
            File.Exists(path) ? File.ReadAllLines(path).Select(l => l.Trim()) : Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var toAdd = domains
            // Split first: a pasted multi-line string must never inject extra hostlist lines.
            .SelectMany(d => d.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(Normalize)
            .Where(d => d.Length > 0 && existing.Add(d))
            .ToList();

        if (toAdd.Count == 0) return 0;

        File.AppendAllLines(path, toAdd);
        Log.Info("hostlist", $"added {toAdd.Count} domain(s) to {UserList}");
        return toAdd.Count;
    }

    private static string Normalize(string raw)
    {
        var d = raw.Trim().ToLowerInvariant();
        // strip scheme / path if a URL was pasted
        d = d.Replace("https://", "").Replace("http://", "");
        int slash = d.IndexOf('/');
        if (slash >= 0) d = d[..slash];
        // drop any whitespace/control characters that survived (defence in depth)
        d = new string(d.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
        return d;
    }
}
