// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

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
    /// Clean up any legacy DLL artifacts from the game directory.
    /// With suspended-process injection, we no longer deploy to the game folder —
    /// but old installs may have left dinput8.dll.old or dinput8_dalaya.dll behind.
    /// Never delete dinput8.dll itself — that's Dalaya's MQ2 core (server-validated).
    /// </summary>
    public static List<string> RestoreDinput8(string eqPath)
    {
        var actions = new List<string>();

        try
        {
            // Remove legacy .old backup if present (from pre-injection era)
            var oldBackup = Path.Combine(eqPath, "dinput8.dll.old");
            if (File.Exists(oldBackup))
            {
                File.Delete(oldBackup);
                actions.Add("Removed legacy dinput8.dll.old from EQ folder");
                FileLogger.Info($"Uninstall: deleted {oldBackup}");
            }

            // Remove chain-load renamed DLL if present (from chain-load era)
            var dalayaRenamed = Path.Combine(eqPath, "dinput8_dalaya.dll");
            if (File.Exists(dalayaRenamed))
            {
                File.Delete(dalayaRenamed);
                actions.Add("Removed dinput8_dalaya.dll from EQ folder");
                FileLogger.Info($"Uninstall: deleted {dalayaRenamed}");
            }
        }
        catch (Exception ex)
        {
            var msg = $"Could not clean up legacy DLL artifacts: {ex.Message}";
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
