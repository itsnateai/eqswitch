namespace EQSwitch.Core;

/// <summary>
/// Minimal persistent file logger. Zero external dependencies.
/// Writes to eqswitch.log alongside the exe. Thread-safe via lock.
/// Falls back to %TEMP% if the primary log path is inaccessible.
/// </summary>
public static class FileLogger
{
    private static StreamWriter? _writer;
    private static readonly object _lock = new();
    private static volatile bool _initialized;
    private static int _consecutiveWriteFailures;

    private const long MaxLogSize = 1_048_576; // 1MB
    private const int MaxConsecutiveFailures = 10;

    /// <summary>
    /// Initialize the logger. Call once at startup before any logging.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eqswitch.log");

        if (TryInitializeAt(logPath))
            return;

        // Fallback to %TEMP% if primary path fails (read-only dir, antivirus, disk full)
        var tempLog = Path.Combine(Path.GetTempPath(), "eqswitch.log");
        if (TryInitializeAt(tempLog))
        {
            Info($"Logger fallback to {tempLog} (primary path unavailable)");
            return;
        }

        // Truly no logging available — write to stderr (visible from console launch) + Debug
        Console.Error.WriteLine("EQSwitch: FileLogger failed to initialize at both primary and temp paths");
        System.Diagnostics.Debug.WriteLine("FileLogger: failed to initialize at both primary and temp paths");
    }

    private static bool TryInitializeAt(string logPath)
    {
        try
        {
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
            _consecutiveWriteFailures = 0;
            Info("Logger initialized");
            return true;
        }
        catch
        {
            return false;
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
            try
            {
                _writer.WriteLine(line);
                _consecutiveWriteFailures = 0;
            }
            catch
            {
                _consecutiveWriteFailures++;
                if (_consecutiveWriteFailures >= MaxConsecutiveFailures)
                {
                    _initialized = false;
                    System.Diagnostics.Debug.WriteLine(
                        $"FileLogger: {MaxConsecutiveFailures} consecutive write failures, disabling");
                }
            }
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
