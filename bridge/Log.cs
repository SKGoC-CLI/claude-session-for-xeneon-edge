namespace ClaudeMeter;

/// <summary>
/// Tiny thread-safe daily-file logger — a separate copy of Claude Meter's Log.cs
/// so the two processes never contend for one log file. Never throws, never
/// logs tokens or other secrets.
/// </summary>
static class Log
{
    static readonly object Lock = new();

    static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeEdgeBridge", "logs");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(
                    Path.Combine(Dir, $"bridge-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the app
        }
    }
}
