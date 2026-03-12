using System.Diagnostics;
using EQSwitch.Config;

namespace EQSwitch.UI;

/// <summary>
/// Handles Windows startup registry entry and desktop shortcut creation.
/// </summary>
public static class StartupManager
{
    /// <summary>
    /// If run-at-startup is enabled, ensure the registry path matches the current exe location.
    /// </summary>
    public static void ValidateRegistryPath(AppConfig config)
    {
        if (!config.RunAtStartup) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: false);
            var registeredPath = key?.GetValue("EQSwitch") as string;
            var currentPath = $"\"{Application.ExecutablePath}\"";
            if (registeredPath != null && !registeredPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"Startup: registry path stale ({registeredPath}), updating to {currentPath}");
                SetRunAtStartup(true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ValidateRegistryPath failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Add or remove EQSwitch from Windows startup via Registry.
    /// </summary>
    public static void SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Application.ExecutablePath;
                key.SetValue("EQSwitch", $"\"{exePath}\"");
                Debug.WriteLine($"Startup: added registry entry for {exePath}");
            }
            else
            {
                key.DeleteValue("EQSwitch", throwOnMissingValue: false);
                Debug.WriteLine("Startup: removed registry entry");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetRunAtStartup failed: {ex.Message}");
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

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                showBalloon("Failed to create shortcut — WScript.Shell not available");
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                try
                {
                    shortcut.TargetPath = Application.ExecutablePath;
                    shortcut.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    shortcut.Description = "EQSwitch — EverQuest Window Manager";
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

            Debug.WriteLine($"Desktop shortcut created: {shortcutPath}");
            showBalloon("Desktop shortcut created");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CreateDesktopShortcut failed: {ex.Message}");
            showBalloon($"Failed to create shortcut: {ex.Message}");
        }
    }
}
