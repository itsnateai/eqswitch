// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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

        // Always call RestoreLegacyDlls — it has a deliberate empty-path branch
        // that still cleans up legacy DLLs in EQSwitch's own app folder.
        actions.AddRange(RestoreLegacyDlls(config.EQPath ?? string.Empty));

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
        // INVARIANT: never delete a >=200KB dinput8.dll — that's Dalaya's MQ2 core.
        // The size-check must run on Step 1's coexistence branch too, not just Step 2 —
        // otherwise Step 1 would mutate the precondition Step 2's safety check relies on.
        try
        {
            if (File.Exists(dalayaPath))
            {
                if (!File.Exists(dinput8Path))
                {
                    // Only dalayaPath exists — straightforward rename-back.
                    File.Move(dalayaPath, dinput8Path);
                    actions.Add("Restored Dalaya's dinput8.dll from chain-load rename");
                    FileLogger.Info("Uninstall: restored dinput8_dalaya.dll → dinput8.dll");
                }
                else
                {
                    // Both files coexist — chain-load steady state. The small one is
                    // our proxy; the >=200KB one is Dalaya's live MQ2. Size-check
                    // dinput8.dll to decide which file is canonical.
                    var info = new FileInfo(dinput8Path);
                    if (info.Length < 200_000)
                    {
                        // dinput8.dll is our proxy; dalayaPath is the real MQ2.
                        // Delete the proxy, then rename the MQ2 back to dinput8.dll.
                        File.Delete(dinput8Path);
                        File.Move(dalayaPath, dinput8Path);
                        actions.Add($"Restored Dalaya's dinput8.dll over legacy proxy ({info.Length:N0} bytes)");
                        FileLogger.Info($"Uninstall: deleted proxy {dinput8Path} ({info.Length} bytes), restored {dalayaPath} → dinput8.dll");
                    }
                    else
                    {
                        // dinput8.dll is already legitimate MQ2 (>=200KB).
                        // dalayaPath is the stale orphan — safe to delete.
                        File.Delete(dalayaPath);
                        actions.Add("Removed stale dinput8_dalaya.dll from EQ folder");
                        FileLogger.Info($"Uninstall: deleted stale orphan {dalayaPath} (dinput8.dll {info.Length} bytes is presumed Dalaya MQ2)");
                    }
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
        // (Step 1 may already have handled the dalayaPath case above; this branch
        // catches the proxy-without-dalaya case.)
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

        // 4. Native DLL logs in EQ folder. eqswitch-di8.dll writes
        //    eqswitch-dinput8-{pid}.log per-process (Native/eqswitch-di8.cpp), and
        //    eqswitch-hook.dll writes eqswitch-hook.log. These accumulate one per
        //    eqgame.exe PID until uninstall — current versions don't rotate them.
        //
        // v3.22.53 post-round-5 fix (T3 Sonnet + T3 Opus convergent IMPORTANT):
        // also track failed deletes (file locked by a running eqgame.exe) and
        // surface them in the actions list. The Settings → Paths → Uninstall
        // path returns this list to the user as the visible summary, so a
        // silent FileLogger.Warn here means the user sees "Removed N log
        // files" while another M are still in the EQ folder — exactly the
        // class of silent failure uninstall.bat's [!!] message was hardened
        // against in round-2. Parity restored: both paths now report
        // "N locked — close eqgame.exe and retry" instead of dropping it.
        try
        {
            var logFiles = Directory.GetFiles(eqPath, "eqswitch-*.log");
            int removed = 0;
            int failed = 0;
            foreach (var log in logFiles)
            {
                try
                {
                    File.Delete(log);
                    removed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    FileLogger.Warn($"Uninstall: could not delete {log}: {ex.Message}");
                }
            }
            if (removed > 0)
            {
                actions.Add($"Removed {removed} native log file(s) from EQ folder (eqswitch-*.log)");
                FileLogger.Info($"Uninstall: deleted {removed} eqswitch-*.log files from {eqPath}");
            }
            if (failed > 0)
            {
                actions.Add($"{failed} log file(s) locked — close all eqgame.exe clients then re-run uninstall");
                FileLogger.Warn($"Uninstall: {failed} eqswitch-*.log files locked in {eqPath} (likely running eqgame.exe)");
            }
        }
        catch (Exception ex)
        {
            var msg = $"Could not enumerate eqswitch-*.log in EQ folder: {ex.Message}";
            actions.Add(msg);
            FileLogger.Warn($"Uninstall: {msg}");
        }

        // 5. Legacy dinput8.dll in EQSwitch's own app folder (no longer shipped).
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

        // Sweep every shortcut filename we've ever shipped with on the desktop.
        // Order: current name first (Dalaya.exe.lnk), then legacy. Keeps the
        // uninstall summary readable when only one exists.
        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var desktopShortcutNames = new[] { "Dalaya.exe.lnk", "EQSwitch.lnk", "EQSwitch.exe.lnk" };
        foreach (var name in desktopShortcutNames)
        {
            var desktopPath = Path.Combine(desktopDir, name);
            if (!File.Exists(desktopPath)) continue;
            try
            {
                File.Delete(desktopPath);
                actions.Add($"Removed desktop shortcut ({name})");
                FileLogger.Info($"Uninstall: deleted {desktopPath}");
            }
            catch (Exception ex)
            {
                actions.Add($"Could not remove desktop shortcut '{name}': {ex.Message}");
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
