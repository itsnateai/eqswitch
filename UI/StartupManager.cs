using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Handles Windows startup shortcut and desktop shortcut creation.
/// Uses Startup folder (not registry) for portability.
/// </summary>
public static class StartupManager
{
    private static readonly string StartupFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    private static readonly string StartupShortcutPath =
        Path.Combine(StartupFolder, "EQSwitch.lnk");

    /// <summary>
    /// If run-at-startup is enabled, ensure the shortcut target matches the current exe location.
    /// </summary>
    public static void ValidateStartupPath(AppConfig config)
    {
        if (!config.RunAtStartup) return;

        if (!File.Exists(StartupShortcutPath))
        {
            // Shortcut missing but config says enabled — recreate it
            FileLogger.Info("Startup: shortcut missing, recreating");
            SetRunAtStartup(true);
            return;
        }

        // Check if shortcut points to the right exe
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(StartupShortcutPath);
                try
                {
                    var targetPath = (string)shortcut.TargetPath;
                    var currentPath = Application.ExecutablePath;
                    if (!targetPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        FileLogger.Info($"Startup: shortcut target stale ({targetPath}), updating to {currentPath}");
                        SetRunAtStartup(true);
                    }
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"ValidateStartupPath failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Add or remove EQSwitch from Windows startup via Startup folder shortcut.
    /// </summary>
    public static void SetRunAtStartup(bool enable)
    {
        try
        {
            if (enable)
            {
                CreateShortcut(StartupShortcutPath, "EQSwitch — EverQuest Window Manager");
                FileLogger.Info($"Startup: created shortcut at {StartupShortcutPath}");
            }
            else
            {
                if (File.Exists(StartupShortcutPath))
                    File.Delete(StartupShortcutPath);
                FileLogger.Info("Startup: removed startup shortcut");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"SetRunAtStartup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Migrate from registry-based startup to shortcut-based startup.
    /// Call once at startup to clean up old registry entries.
    /// </summary>
    public static void MigrateFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key?.GetValue("EQSwitch") != null)
            {
                key.DeleteValue("EQSwitch", throwOnMissingValue: false);
                FileLogger.Info("Startup: migrated from registry entry (removed)");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"MigrateFromRegistry failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Create a desktop shortcut using WScript.Shell COM object.
    /// </summary>
    public static void CreateDesktopShortcut(Action<string> showBalloon)
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var shortcutPath = Path.Combine(desktopPath, "EQSwitch.lnk");

            if (File.Exists(shortcutPath))
            {
                showBalloon("Desktop shortcut already exists");
                return;
            }

            CreateShortcut(shortcutPath, "EQSwitch — EverQuest Window Manager");

            FileLogger.Info($"Desktop shortcut created: {shortcutPath}");
            showBalloon("Desktop shortcut created");
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"CreateDesktopShortcut failed: {ex.Message}");
            showBalloon($"Failed to create shortcut: {ex.Message}");
        }
    }

    private static void CreateShortcut(string shortcutPath, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell not available");

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = Application.ExecutablePath;
                shortcut.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                shortcut.Description = description;
                shortcut.IconLocation = Application.ExecutablePath + ",0";
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
