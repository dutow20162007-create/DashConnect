using System.Text;

namespace DashConnect.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public readonly record struct LogEntry(DateTime TimestampLocal, LogLevel Level, string Source, string Message)
{
    public override string ToString()
        => $"{TimestampLocal:HH:mm:ss} [{Level.ToString().ToUpperInvariant(),-5}] {Source}: {Message}";
}

/// <summary>
/// Process-wide, thread-safe logger. Raises <see cref="Entry"/> for UI subscribers and
/// (optionally) appends to a rolling file. Never throws to callers.
/// </summary>
public static class Log
{
    public static event Action<LogEntry>? Entry;

    private static readonly object _sync = new();
    private static string? _filePath;

    public static void ConfigureFile(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _filePath = path;
        }
        catch { /* logging must never crash the app */ }
    }

    public static void Debug(string source, string message) => Emit(LogLevel.Debug, source, message);
    public static void Info(string source, string message) => Emit(LogLevel.Info, source, message);
    public static void Warn(string source, string message) => Emit(LogLevel.Warn, source, message);
    public static void Error(string source, string message) => Emit(LogLevel.Error, source, message);

    public static void Error(string source, string message, Exception ex)
        => Emit(LogLevel.Error, source, $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Emit(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message);
        try { Entry?.Invoke(entry); } catch { /* subscriber faults are not our problem */ }

        var path = _filePath;
        if (path is null) return;
        try
        {
            lock (_sync)
            {
                File.AppendAllText(path, entry + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* disk errors are non-fatal for logging */ }
    }
}
