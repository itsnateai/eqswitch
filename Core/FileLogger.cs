namespace EQSwitch.Core;

/// <summary>
/// Minimal persistent file logger. Zero external dependencies.
/// Writes to eqswitch.log alongside the exe. Thread-safe via lock.
/// </summary>
public static class FileLogger
{
    private static StreamWriter? _writer;
    private static readonly object _lock = new();
    private static bool _initialized;

    private const long MaxLogSize = 1_048_576; // 1MB

    /// <summary>
    /// Initialize the logger. Call once at startup before any logging.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eqswitch.log");
            var bakPath = logPath + ".bak";

            // Rotate if log exceeds 1MB
            if (File.Exists(logPath))
            {
                var info = new FileInfo(logPath);
                if (info.Length > MaxLogSize)
                {
                    try { File.Copy(logPath, bakPath, overwrite: true); } catch { }
                    try { File.Delete(logPath); } catch { }
                }
            }

            _writer = new StreamWriter(
                new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };

            _initialized = true;
            Info("Logger initialized");
        }
        catch
        {
            // If we can't create the log file, continue without logging.
            // Better than crashing the app over diagnostics.
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        if (!_initialized || _writer == null) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";

        lock (_lock)
        {
            try { _writer.WriteLine(line); }
            catch { /* Don't crash the app over logging failures */ }
        }
    }

    /// <summary>
    /// Flush and close the log writer. Call on shutdown.
    /// </summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
                _initialized = false;
            }
            catch { }
        }
    }
}
