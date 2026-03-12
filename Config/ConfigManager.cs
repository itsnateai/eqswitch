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
                return new AppConfig();

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
            return new AppConfig();
        }
    }

    /// <summary>
    /// Save config to disk. Creates a timestamped backup first.
    /// </summary>
    public static void Save(AppConfig config)
    {
        try
        {
            // Backup existing config before overwriting
            if (File.Exists(ConfigPath))
                CreateBackup();

            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            FileLogger.Error("Config save failed", ex);
            throw; // Let caller decide how to handle
        }
    }

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
                try { File.Delete(old); } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Backup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Export character profiles to a standalone backup file.
    /// </summary>
    public static void ExportCharacters(AppConfig config, string exportPath)
    {
        var characters = config.Characters;
        var json = JsonSerializer.Serialize(characters, JsonOptions);
        File.WriteAllText(exportPath, json);
    }

    /// <summary>
    /// Import character profiles from a backup file.
    /// </summary>
    public static List<CharacterProfile> ImportCharacters(string importPath)
    {
        var json = File.ReadAllText(importPath);
        return JsonSerializer.Deserialize<List<CharacterProfile>>(json, JsonOptions) ?? new();
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
        catch { /* best effort */ }
    }
}
