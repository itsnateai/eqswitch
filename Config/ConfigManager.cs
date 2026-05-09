// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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

            // Run versioned migrations on raw JSON before deserialization
            var (migratedJson, didMigrate) = ConfigVersionMigrator.MigrateIfNeeded(json);

            var config = JsonSerializer.Deserialize<AppConfig>(migratedJson, JsonOptions) ?? new AppConfig();
            bool validateMutated = config.Validate();

            // Persist migrated config so migration doesn't re-run.
            // v3.15.10 follow-up: stage write under _saveLock for symmetry
            // with Save() / FlushSave() / SaveImmediate(). Load() is called
            // single-threaded at startup before any background work spins
            // up, so the lock is uncontended here in practice — but the
            // explicit cross-thread contract is "every _pendingSave write
            // happens under _saveLock", and breaking that invariant in one
            // call site silently invalidates it everywhere.
            if (didMigrate || validateMutated)
            {
                lock (_saveLock)
                {
                    _pendingSave = config;
                }
                FlushSave();
            }

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

    // Serializes file I/O across SaveImmediate (background-thread callers) and
    // FlushSave (UI-thread coalesced flush). Without this, a background
    // SaveImmediate could collide with a UI-thread FlushSave mid-write,
    // producing a torn temp-file or a corrupted backup. Held only across the
    // critical section of read-pending + write-disk + reset-pending.
    private static readonly object _saveLock = new();

    /// <summary>
    /// Save config to disk. Coalesces rapid calls — the actual write happens
    /// after a 250ms quiet period so rapid toggles don't stall the UI.
    /// </summary>
    public static void Save(AppConfig config)
    {
        // v3.15.10: stage _pendingSave under _saveLock for symmetry with
        // FlushSave / SaveImmediate. In current callers `config` is always
        // the live `_config` reference, so the prior unlocked write was
        // benign — but a future caller passing a different config object
        // could collide with a concurrent SaveImmediate from AutoLoginManager.
        // The lock makes the cross-thread contract explicit.
        lock (_saveLock)
        {
            _pendingSave = config;
        }

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

        AppConfig? config;
        lock (_saveLock)
        {
            config = _pendingSave;
            _pendingSave = null;
        }
        if (config == null) return true;

        bool ok = WriteToDisk(config);

        // If Save() was called during the write, re-queue so it's not lost.
        // _saveTimer access is UI-thread-only by contract; FlushSave is invoked
        // from the timer Tick (UI thread) or from Shutdown (UI thread), so this
        // is safe.
        if (_pendingSave != null)
        {
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }
        return ok;
    }

    /// <summary>
    /// Thread-safe synchronous save. Bypasses the WinForms-Timer coalescing path
    /// in <see cref="Save"/>, which is unsafe to call from background threads
    /// (the Timer's Tick fires on the thread that called Start, so a background
    /// caller would create a Timer that never ticks because the worker thread
    /// has no message pump). Use this for any save originating off the UI
    /// thread (e.g. AutoLoginManager status writes from the login worker).
    /// Synchronous file I/O — call sparingly.
    /// </summary>
    public static bool SaveImmediate(AppConfig config)
    {
        lock (_saveLock)
        {
            // If a UI-thread Save() had queued a pending write that hasn't
            // flushed yet, dropping our caller's config in front of it would
            // lose those changes. Instead, install our config as the pending
            // write and let WriteToDisk flush it; any UI-thread caller racing
            // us will queue behind the lock and re-flush on next tick.
            _pendingSave = config;
            var toWrite = _pendingSave;
            _pendingSave = null;
            return WriteToDisk(toWrite);
        }
    }

    /// <summary>
    /// Atomic write of the JSON to disk via temp + Move. Caller is responsible
    /// for any synchronization. Updates LastSaveError + raises SaveFailed on
    /// failure. Returns true on success.
    /// </summary>
    private static bool WriteToDisk(AppConfig config)
    {
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
