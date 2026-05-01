// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// Reverts all external system modifications made by EQSwitch.
/// Used by both the Settings GUI "Uninstall" button, the standalone uninstall.bat
/// (via the C# helper invoked from the EXE), and the one-time startup cleanup
/// in TrayManager. Single source of truth — keep it that way.
///
/// Does NOT touch eqclient.ini settings (user can restore from .bak files) or
/// EQSwitch's own config/log/backup files.
/// </summary>
public static class UninstallHelper
{
    /// <summary>
    /// Run all cleanup steps and return a list of human-readable actions taken.
    /// Empty list = nothing to clean up.
    /// </summary>
    public static List<string> CleanUp(AppConfig config)
    {
        var actions = new List<string>();

        if (!string.IsNullOrEmpty(config.EQPath))
            actions.AddRange(RestoreLegacyDlls(config.EQPath));

        actions.AddRange(RemoveShortcuts());
        actions.AddRange(RemoveLegacyRegistryEntry());

        return actions;
    }

    /// <summary>
    /// Clean up DLL artifacts left behind by older EQSwitch versions.
    ///
    /// Three eras of artifacts are handled — see CHANGELOG for full history:
    ///   1. Chain-load era: Dalaya's MQ2 dinput8.dll renamed to dinput8_dalaya.dll
    ///      with our proxy occupying the dinput8.dll slot. Restore the rename.
    ///   2. Proxy era: dinput8.dll in EQ folder is our ~148KB proxy (Dalaya's is ~1.3MB).
    ///      Size-detect and remove. Also handles legacy dinput8.dll.old backups.
    ///   3. Suspended-process era (current): no artifacts in EQ folder, but a
    ///      legacy dinput8.dll may still sit in EQSwitch's own app folder.
    ///
    /// CRITICAL: never delete a >200KB dinput8.dll — that's Dalaya's MQ2 core
    /// (server hash-validated; deleting it forces the user to re-run Dalaya's
    /// patcher to restore connectivity).
    /// </summary>
    public static List<string> RestoreLegacyDlls(string eqPath)
    {
        var actions = new List<string>();

        if (string.IsNullOrEmpty(eqPath) || !Directory.Exists(eqPath))
        {
            // Still clean up the app-folder legacy DLL even if EQ path is unset.
            actions.AddRange(RemoveAppFolderLegacyDinput8());
            return actions;
        }

        var dinput8Path = Path.Combine(eqPath, "dinput8.dll");
        var dalayaPath = Path.Combine(eqPath, "dinput8_dalaya.dll");
        var oldBackup = Path.Combine(eqPath, "dinput8.dll.old");

        // 1. Chain-load era: restore Dalaya's MQ2 if we renamed it.
        try
        {
            if (File.Exists(dalayaPath))
            {
                if (!File.Exists(dinput8Path))
                {
                    File.Move(dalayaPath, dinput8Path);
                    actions.Add("Restored Dalaya's dinput8.dll from chain-load rename");
                    FileLogger.Info("Uninstall: restored dinput8_dalaya.dll → dinput8.dll");
                }
                else
                {
                    File.Delete(dalayaPath);
                    actions.Add("Removed stale dinput8_dalaya.dll from EQ folder");
                    FileLogger.Info($"Uninstall: deleted {dalayaPath} (dinput8.dll already present)");
                }
            }
        }
        catch (Exception ex)
        {
            var msg = $"Could not handle dinput8_dalaya.dll: {ex.Message}";
            actions.Add(msg);
            FileLogger.Warn($"Uninstall: {msg}");
        }

        // 2. Proxy era: size-detect and remove legacy proxy in EQ folder.
        // Dalaya's MQ2 dinput8.dll is ~1.3MB; our old proxies were 141-148KB.
        // The 200KB threshold is generous — anything bigger is presumed legitimate.
        try
        {
            if (File.Exists(dinput8Path) && !File.Exists(dalayaPath))
            {
                var info = new FileInfo(dinput8Path);
                if (info.Length < 200_000)
                {
                    File.Delete(dinput8Path);
                    actions.Add($"Removed legacy proxy dinput8.dll from EQ folder ({info.Length:N0} bytes)");
                    FileLogger.Info($"Uninstall: deleted legacy proxy {dinput8Path} ({info.Length} bytes)");
                }
            }
        }
        catch (Exception ex)
        {
            var msg = $"Could not check dinput8.dll size: {ex.Message}";
            actions.Add(msg);
            FileLogger.Warn($"Uninstall: {msg}");
        }

        // 3. Legacy .old backup from pre-injection era — never useful, always safe to remove.
        try
        {
            if (File.Exists(oldBackup))
            {
                File.Delete(oldBackup);
                actions.Add("Removed legacy dinput8.dll.old backup from EQ folder");
                FileLogger.Info($"Uninstall: deleted {oldBackup}");
            }
        }
        catch (Exception ex)
        {
            var msg = $"Could not remove dinput8.dll.old: {ex.Message}";
            actions.Add(msg);
            FileLogger.Warn($"Uninstall: {msg}");
        }

        // 4. Legacy dinput8.dll in EQSwitch's own app folder (no longer shipped).
        actions.AddRange(RemoveAppFolderLegacyDinput8());

        return actions;
    }

    /// <summary>
    /// Remove a legacy dinput8.dll from EQSwitch's app folder. Pre-v3.4.3 builds
    /// shipped this; current builds don't. Idempotent.
    /// </summary>
    private static List<string> RemoveAppFolderLegacyDinput8()
    {
        var actions = new List<string>();
        try
        {
            var appDinput8 = Path.Combine(AppContext.BaseDirectory, "dinput8.dll");
            if (File.Exists(appDinput8))
            {
                File.Delete(appDinput8);
                actions.Add("Removed legacy dinput8.dll from EQSwitch app folder");
                FileLogger.Info($"Uninstall: deleted {appDinput8}");
            }
        }
        catch (Exception ex)
        {
            var msg = $"Could not remove app-folder dinput8.dll: {ex.Message}";
            actions.Add(msg);
            FileLogger.Warn($"Uninstall: {msg}");
        }
        return actions;
    }

    /// <summary>
    /// Remove startup and desktop shortcuts. Idempotent.
    /// </summary>
    public static List<string> RemoveShortcuts()
    {
        var actions = new List<string>();

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

    /// <summary>
    /// Remove the registry-based startup entry from pre-shortcut versions.
    /// Defense-in-depth: this is normally cleaned up by StartupManager.MigrateFromRegistry
    /// on first launch of any post-migration build, but if a user installs a new build,
    /// never runs it interactively, edits config manually, then runs Uninstall via
    /// uninstall.bat — the registry entry would otherwise persist.
    /// </summary>
    public static List<string> RemoveLegacyRegistryEntry()
    {
        var actions = new List<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key?.GetValue("EQSwitch") != null)
            {
                key.DeleteValue("EQSwitch", throwOnMissingValue: false);
                actions.Add("Removed legacy registry startup entry");
                FileLogger.Info("Uninstall: removed HKCU\\...\\Run\\EQSwitch");
            }
        }
        catch (Exception ex)
        {
            actions.Add($"Could not remove registry startup entry: {ex.Message}");
            FileLogger.Warn($"Uninstall: registry cleanup failed: {ex.Message}");
        }
        return actions;
    }
}
