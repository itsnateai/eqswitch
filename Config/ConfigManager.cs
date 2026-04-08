using System.Text.Json;
using System.Text.Json.Serialization;
using EQSwitch.Core;

namespace EQSwitch.Config;

/// <summary>
/// Handles loading, saving, and backing up the JSON config file.
/// Config lives alongside the exe for portability — no AppData pollution.
/// </summary>
public static class ConfigManager
{
    private static readonly string ConfigDir = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "eqswitch-config.json");
    private static readonly string BackupDir = Path.Combine(ConfigDir, "backups");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Load config from disk. Returns defaults if file doesn't exist or is corrupt.
    /// Validates all values after loading.
    /// </summary>
    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var defaults = new AppConfig();
                defaults.Validate();
                return defaults;
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            config.Validate();
            return config;
        }
        catch (Exception ex)
        {
            // Config is corrupt — back it up and start fresh
            TryBackupCorruptConfig();
            FileLogger.Error("Config load failed, using defaults", ex);
            var defaults = new AppConfig();
            defaults.Validate();
            return defaults;
        }
    }

    // Coalescing save — rapid Save() calls (e.g. PiP drag, repeated toggles)
    // are batched into a single write after a short delay. This prevents
    // blocking the UI thread with synchronous file I/O on every interaction.
    private static System.Windows.Forms.Timer? _saveTimer;
    private static AppConfig? _pendingSave;

    /// <summary>
    /// Save config to disk. Coalesces rapid calls — the actual write happens
    /// after a 250ms quiet period so rapid toggles don't stall the UI.
    /// </summary>
    public static void Save(AppConfig config)
    {
        _pendingSave = config;

        if (_saveTimer == null)
        {
            _saveTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _saveTimer.Tick += (_, _) => FlushSave();
        }

        // Reset the timer on each call to coalesce rapid saves
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// Drain pending saves and dispose the coalescing timer. Safe to call multiple times.
    /// </summary>
    public static void Shutdown()
    {
        FlushSave();           // drain any pending save
        _saveTimer?.Stop();
        _saveTimer?.Dispose();
        _saveTimer = null;
        FlushSave();           // catch any save queued between first flush and timer disposal
    }

    /// <summary>
    /// Flush any pending save immediately. Returns true on success.
    /// Guards against lost saves: if Save() is called during the write, the new pending
    /// config is detected and re-queued after the write completes.
    /// </summary>
    public static bool FlushSave()
    {
        _saveTimer?.Stop();

        var config = _pendingSave;
        _pendingSave = null;
        if (config == null) return true;

        try
        {
            if (File.Exists(ConfigPath))
                CreateBackup();

            var json = JsonSerializer.Serialize(config, JsonOptions);
            var tempPath = ConfigPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLogger.Error("Config save failed", ex);
            LastSaveError = ex.Message;
            SaveFailed?.Invoke(ex.Message);
            return false;
        }

        LastSaveError = null;

        // If Save() was called during the write, re-queue so it's not lost.
        if (_pendingSave != null)
        {
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }
        return true;
    }

    /// <summary>Error message from the last failed save, or null if last save succeeded.</summary>
    public static string? LastSaveError { get; private set; }

    /// <summary>Raised when FlushSave fails. Subscribers can show user-facing warnings.</summary>
    public static event Action<string>? SaveFailed;

    /// <summary>
    /// Create a timestamped backup of the current config.
    /// Keeps the last 10 backups sorted by write time to avoid filling disk.
    /// </summary>
    public static void CreateBackup()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            Directory.CreateDirectory(BackupDir);

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var backupPath = Path.Combine(BackupDir, $"eqswitch-config_{timestamp}.json");
            File.Copy(ConfigPath, backupPath, overwrite: true);

            // Prune old backups (keep last 10 by write time — robust against filename changes)
            var backups = Directory.GetFiles(BackupDir, "eqswitch-config_*.json")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Skip(10);

            foreach (var old in backups)
            {
                try { File.Delete(old); }
                catch (Exception ex) { FileLogger.Info($"Backup prune skipped ({Path.GetFileName(old)}): {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Backup failed: {ex.Message}");
        }
    }

    private static void TryBackupCorruptConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            Directory.CreateDirectory(BackupDir);
            var corruptPath = Path.Combine(BackupDir, $"CORRUPT_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.Move(ConfigPath, corruptPath);
            FileLogger.Warn($"Corrupt config backed up to {corruptPath}");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"TryBackupCorruptConfig: failed to move corrupt config: {ex.Message}");
            // Last resort: delete the corrupt file so Load() doesn't hit it on every launch
            try { File.Delete(ConfigPath); }
            catch (Exception ex2) { FileLogger.Error($"TryBackupCorruptConfig: also failed to delete: {ex2.Message}"); }
        }
    }
}
