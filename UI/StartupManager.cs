// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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
    /// Desktop shortcut name. Matches the DalayaPatcher-style "looks like
    /// a Dalaya launcher" convention so users with both shortcuts see a
    /// consistent naming pattern. Windows hides the .lnk in Explorer, so
    /// users see "Dalaya.exe" — the .exe is part of the visible name, not
    /// the target. Target still points at EQSwitch.exe.
    /// </summary>
    internal const string DesktopShortcutName = "Dalaya.exe.lnk";

    /// <summary>
    /// Legacy desktop shortcut filenames swept up on every create so users
    /// don't end up with two side-by-side shortcuts after upgrading. Order
    /// matters only for log clarity. Update when adding new variants —
    /// keep older entries forever (anyone who never re-clicks Create still
    /// gets a clean rename on uninstall via UninstallHelper).
    /// </summary>
    private static readonly string[] LegacyDesktopShortcutNames =
    {
        "EQSwitch.lnk",
        "EQSwitch.exe.lnk",
    };

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
            var shortcutPath = Path.Combine(desktopPath, DesktopShortcutName);

            // Sweep legacy shortcut names. Done unconditionally so the user
            // sees a single Dalaya.exe.lnk even if they had an older
            // EQSwitch.lnk/EQSwitch.exe.lnk lying around from a prior install.
            int swept = 0;
            foreach (var legacy in LegacyDesktopShortcutNames)
            {
                var legacyPath = Path.Combine(desktopPath, legacy);
                if (File.Exists(legacyPath))
                {
                    try
                    {
                        File.Delete(legacyPath);
                        swept++;
                        FileLogger.Info($"CreateDesktopShortcut: removed legacy '{legacy}'");
                    }
                    catch (Exception sweepEx)
                    {
                        // Non-fatal — log and keep going. The new shortcut
                        // still lands; the user may need to manually delete
                        // the legacy one (e.g. read-only attribute, AV lock).
                        FileLogger.Warn($"CreateDesktopShortcut: failed to remove legacy '{legacy}': {sweepEx.Message}");
                    }
                }
            }

            if (File.Exists(shortcutPath))
            {
                showBalloon(swept > 0
                    ? $"Desktop shortcut already exists (removed {swept} legacy)"
                    : "Desktop shortcut already exists");
                return;
            }

            CreateShortcut(shortcutPath, "Dalaya — EQSwitch EverQuest Window Manager");

            FileLogger.Info($"Desktop shortcut created: {shortcutPath} (swept {swept} legacy)");
            showBalloon(swept > 0
                ? $"Desktop shortcut created (replaced {swept} legacy)"
                : "Desktop shortcut created");
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
