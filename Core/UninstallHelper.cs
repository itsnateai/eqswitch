using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// Reverts all external system modifications made by EQSwitch.
/// Used by both the Settings GUI "Uninstall" button and the standalone uninstall.bat.
/// Does NOT touch eqclient.ini settings (user can restore from .bak files) or
/// EQSwitch's own config/log/backup files.
/// </summary>
public static class UninstallHelper
{
    /// <summary>
    /// Run all cleanup steps and return a list of actions taken.
    /// </summary>
    public static List<string> CleanUp(AppConfig config)
    {
        var actions = new List<string>();

        if (!string.IsNullOrEmpty(config.EQPath))
            actions.AddRange(RestoreDinput8(config.EQPath));

        actions.AddRange(RemoveShortcuts());

        return actions;
    }

    /// <summary>
    /// Restore dinput8.dll.old → dinput8.dll if backup exists, otherwise delete our deployed copy.
    /// </summary>
    public static List<string> RestoreDinput8(string eqPath)
    {
        var actions = new List<string>();
        var dllPath = Path.Combine(eqPath, "dinput8.dll");
        var backupPath = dllPath + ".old";

        if (!File.Exists(dllPath) && !File.Exists(backupPath))
            return actions;

        try
        {
            if (File.Exists(backupPath))
            {
                // Restore the original DLL that was backed up when we deployed ours
                File.Copy(backupPath, dllPath, overwrite: true);
                File.Delete(backupPath);
                actions.Add($"Restored original dinput8.dll from backup in {eqPath}");
                FileLogger.Info($"Uninstall: restored dinput8.dll from .old in {eqPath}");
            }
            else if (File.Exists(dllPath))
            {
                File.Delete(dllPath);
                actions.Add($"Removed dinput8.dll from {eqPath}");
                FileLogger.Info($"Uninstall: deleted dinput8.dll from {eqPath}");
            }
        }
        catch (Exception ex)
        {
            var msg = $"Could not clean up dinput8.dll: {ex.Message}";
            actions.Add(msg);
            FileLogger.Warn($"Uninstall: {msg}");
        }

        return actions;
    }

    /// <summary>
    /// Remove startup and desktop shortcuts.
    /// </summary>
    public static List<string> RemoveShortcuts()
    {
        var actions = new List<string>();

        // Startup shortcut
        var startupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "EQSwitch.lnk");
        if (File.Exists(startupPath))
        {
            try
            {
                File.Delete(startupPath);
                actions.Add("Removed startup shortcut");
                FileLogger.Info($"Uninstall: deleted {startupPath}");
            }
            catch (Exception ex)
            {
                actions.Add($"Could not remove startup shortcut: {ex.Message}");
            }
        }

        // Desktop shortcut
        var desktopPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "EQSwitch.lnk");
        if (File.Exists(desktopPath))
        {
            try
            {
                File.Delete(desktopPath);
                actions.Add("Removed desktop shortcut");
                FileLogger.Info($"Uninstall: deleted {desktopPath}");
            }
            catch (Exception ex)
            {
                actions.Add($"Could not remove desktop shortcut: {ex.Message}");
            }
        }

        return actions;
    }
}
