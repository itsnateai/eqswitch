// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.UI;

namespace EQSwitch;

static class Program
{
    // Single-instance mutex to prevent multiple copies running
    private static Mutex? _mutex;

    [STAThread]
    static void Main(string[] args)
    {
        // --test-migrate <input-json> — run ConfigVersionMigrator on the file and write
        // <input>.migrated.json next to it. Exits without showing UI. Used by scripted
        // migration test fixtures under _tests/migration/.
        if (args.Length >= 2 && args[0] == "--test-migrate")
        {
            try
            {
                var inputPath = args[1];
                var inputJson = File.ReadAllText(inputPath);
                var (migratedJson, didMigrate) = ConfigVersionMigrator.MigrateIfNeeded(inputJson);
                var outputPath = inputPath + ".migrated.json";
                File.WriteAllText(outputPath, migratedJson);
                File.WriteAllText(inputPath + ".test-result.txt",
                    $"input={inputPath}\noutput={outputPath}\nmigrated={didMigrate}\n");
            }
            catch (Exception ex)
            {
                File.WriteAllText(args[1] + ".test-result.txt", $"ERROR: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            }
            return;
        }

        // --test-split <input-v3-json> — deserialize accounts[] into LoginAccount[],
        // run LoginAccountSplitter, and write the split result to <input>.split.json.
        // The fixture harness asserts this output matches the migrator's accountsV4 /
        // charactersV4 keys so the two code paths can't drift silently.
        if (args.Length >= 2 && args[0] == "--test-split")
        {
            try
            {
                var inputPath = args[1];
                var inputJson = File.ReadAllText(inputPath);
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                };
                var root = System.Text.Json.Nodes.JsonNode.Parse(inputJson)?.AsObject();
                var accountsArray = root?["accounts"]?.ToJsonString() ?? "[]";
                var legacyAccounts = System.Text.Json.JsonSerializer.Deserialize<List<EQSwitch.Models.LoginAccount>>(
                    accountsArray, options) ?? new List<EQSwitch.Models.LoginAccount>();

                var (v4Accounts, v4Characters) = EQSwitch.Config.LoginAccountSplitter.Split(legacyAccounts);

                var splitOutput = new System.Text.Json.Nodes.JsonObject
                {
                    ["accounts"] = System.Text.Json.Nodes.JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(v4Accounts, options)),
                    ["characters"] = System.Text.Json.Nodes.JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(v4Characters, options)),
                };

                File.WriteAllText(inputPath + ".split.json", splitOutput.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                File.WriteAllText(args[1] + ".test-result.txt", $"ERROR (split): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            }
            return;
        }

        // --test-autologin [alias] [--timeout N] — launch a real EQ client, drive
        // full auto-login, monitor phase, kill the process, and verify zero SEH
        // in the native mq2_bridge dispatch path. Used by the v8 Step 2B
        // verification loop and any future native-path change. Works in both
        // Debug and Release builds because a failing login is a shippable bug.
        //
        // Returns:
        //   0 = login completed + zero native-path SEH in log  (PASS)
        //   1 = login didn't reach charselect (timeout or fault)
        //   2 = login completed BUT log has SEH occurrences
        //   3 = config / account not found
        if (args.Length >= 1 && args[0] == "--test-autologin")
        {
            int exitCode;
            try
            {
                exitCode = Core.TestAutoLoginRunner.Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"TestAutoLoginRunner CRASHED: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                exitCode = 2;
            }
            Environment.Exit(exitCode);
            return;
        }

#if !DEBUG
        // v3.15.10: test CLI flags (--test-character-selector / --test-config-validate /
        // --test-key-input-writer / --test-shm-layout / --test-charselect-reader) are
        // Debug-only by design — the tests live in Core/*Tests.cs which the csproj
        // excludes from Release builds (avoids ~50KB of bloat and Console output
        // surface in the shipped 155MB single-file binary). `--test-autologin` above
        // is the exception: it ships in Release because a failing autologin is a
        // shippable bug a user would want to diagnose.
        //
        // Without this guard, `--test-foo` in Release would silently fall through to
        // the normal tray-app launch path — confusing for anyone running the flag
        // expecting a test runner. Exit cleanly with a distinct code instead so a
        // calling shell can tell the flag was rejected (vs the app launching).
        if (args.Length >= 1
            && args[0].StartsWith("--test-", StringComparison.Ordinal)
            && args[0] != "--test-autologin")
        {
            // No console attached in WinExe Release, so a Console.Error.WriteLine
            // wouldn't be visible — but the exit code is observable to the calling
            // shell ($LASTEXITCODE in PowerShell, $? in bash via && / ||).
            Environment.Exit(3);
            return;
        }
#endif

#if DEBUG
        // --test-character-selector — run Core/CharacterSelectorTests.RunAll() and
        // exit with its return code. Used to gate Phase 5b's pure decision helper.
        if (args.Length >= 1 && args[0] == "--test-character-selector")
        {
            int exitCode;
            try
            {
                exitCode = Core.CharacterSelectorTests.RunAll();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"CharacterSelectorTests CRASHED: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                exitCode = 2;
            }
            Environment.Exit(exitCode);
            return;
        }

        // --test-config-validate — run Core/AppConfigValidateTests.RunAll() and
        // exit with its return code.
        if (args.Length >= 1 && args[0] == "--test-config-validate")
        {
            int exitCode;
            try
            {
                exitCode = Core.AppConfigValidateTests.RunAll();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"AppConfigValidateTests CRASHED: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                exitCode = 2;
            }
            Environment.Exit(exitCode);
            return;
        }

        // --test-key-input-writer — run Core/KeyInputWriterTests.RunAll() and
        // exit with its return code. Guards the hotfix v3 MMF write-order contract.
        if (args.Length >= 1 && args[0] == "--test-key-input-writer")
        {
            int exitCode;
            try
            {
                exitCode = Core.KeyInputWriterTests.RunAll();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"KeyInputWriterTests CRASHED: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                exitCode = 2;
            }
            Environment.Exit(exitCode);
            return;
        }

        // --test-shm-layout — verify C# SharedKeyState struct layout matches
        // Native/key_shm.h. Fails fast if a refactor drifts either side.
        if (args.Length >= 1 && args[0] == "--test-shm-layout")
        {
            int exitCode;
            try
            {
                exitCode = Core.ShmLayoutTests.RunAll();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ShmLayoutTests CRASHED: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                exitCode = 2;
            }
            Environment.Exit(exitCode);
            return;
        }

        // --test-charselect-reader — exercise CharSelectReader against simulated
        // bridge writes (gate / latch / single-char fallback / recycled-PID safety).
        // No external eqgame.exe needed — uses fake PIDs + paired MemoryMappedFile views.
        if (args.Length >= 1 && args[0] == "--test-charselect-reader")
        {
            int exitCode;
            try
            {
                exitCode = Core.CharSelectReaderTests.RunAll();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"CharSelectReaderTests CRASHED: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                exitCode = 2;
            }
            Environment.Exit(exitCode);
            return;
        }
#endif

        // Enforce single instance
        const string mutexName = "EQSwitch_SingleInstance_SoD";
        _mutex = new Mutex(true, mutexName, out bool createdNew);

        // After self-update, the old instance may still be shutting down
        if (!createdNew && args.Contains("--after-update"))
        {
            for (int i = 0; i < 10 && !createdNew; i++)
            {
                Thread.Sleep(500);
                _mutex.Dispose();
                _mutex = new Mutex(true, mutexName, out createdNew);
            }
        }

        if (!createdNew)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FloatingTooltip.Show("EQSwitch is already running. Check your system tray.", 4000);
            // Brief delay so tooltip is visible before process exits
            Thread.Sleep(4200);
            return;
        }

        bool isAfterUpdate = args.Contains("--after-update");

        FileLogger.Initialize();
        CleanupUpdateArtifacts();

        // SystemAware DPI mode — restored 2026-05-19 after v3.22.19's
        // PerMonitorV2 experiment introduced regressions in single-screen
        // mode (windows bugged into Fullscreen mode on team launch) and
        // didn't fix the multi-monitor cross-DPI positioning anyway.
        // Per Nate's directive: "if trying to match other monitor DPI is
        // bugging us then just goal on making the multimonitor constant
        // and working flawless and dont worry about extending the 2nd
        // monitor to cover it". The per-monitor slim flag still ships as
        // an architectural framework for future revisits, but the runtime
        // behavior is now back to v3.22.18 baseline.
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Set an app-managed default font. Without this, controls use the
        // static Control.DefaultFont (SystemFonts.DefaultFont) which gets
        // invalidated during runtime — possibly by display change events,
        // DPI context switches, or GDI+ cleanup in this tray-only app.
        // Controls then throw "Parameter is not valid" on construction.
        Application.SetDefaultFont(new Font("Segoe UI", 9f));

        // Catch UI thread exceptions BEFORE WinForms tries to show ThreadExceptionDialog
        // (which itself crashes due to GDI+ font corruption, hiding the real error)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            FileLogger.Error("UI thread exception (original)", e.Exception);
            try
            {
                MessageBox.Show(
                    $"EQSwitch encountered an error:\n\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\n{e.Exception.StackTrace}",
                    "EQSwitch Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // If even MessageBox fails (GDI+ corruption), at least we logged it
            }
        };

        try
        {
            var config = ConfigManager.Load();

            // First-run: show EQ path picker
            bool isNewUser = false;
            if (config.IsFirstRun)
            {
                using var dialog = new FirstRunDialog();
                if (dialog.ShowDialog() != DialogResult.OK)
                    return; // User cancelled — don't start

                config.EQPath = dialog.SelectedEQPath;
                config.IsFirstRun = false;
                isNewUser = true;

                // Seed EQ client settings from actual ini so AppConfig reflects reality
                // instead of hardcoded defaults — prevents silent overwrites on first Save
                if (!string.IsNullOrEmpty(config.EQPath))
                {
                    var iniPath = Path.Combine(config.EQPath, "eqclient.ini");
                    config.EQClientIni = EQClientIniConfig.SeedFromIni(iniPath);
                }

                ConfigManager.Save(config);
                ConfigManager.FlushSave();
            }


            var processManager = new ProcessManager(config);
            var trayApp = new TrayManager(config, processManager);

            // Ensure cleanup on any exit path (not just tray menu Exit)
            Application.ApplicationExit += (_, _) => trayApp.Dispose();

            trayApp.Initialize();

            // Auto-open Settings on first run so new users can configure
            if (isNewUser)
                trayApp.OpenSettingsAfterDelay();

            // Show confirmation after successful self-update (delayed so tray is ready).
            // Intentionally calls FloatingTooltip.Show directly — bypasses
            // TrayManager.ShowBalloon and the AppConfig.ShowTooltips toggle so
            // the post-update confirmation always surfaces, even when the user
            // has muted status tooltips.
            if (isAfterUpdate)
            {
                var postUpdateTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                postUpdateTimer.Tick += (_, _) =>
                {
                    postUpdateTimer.Stop();
                    postUpdateTimer.Dispose();
                    var version = System.Reflection.Assembly.GetExecutingAssembly()
                        .GetName().Version?.ToString(3) ?? "?";
                    FloatingTooltip.Show($"✅ EQSwitch updated to v{version}!", 5000);
                };
                postUpdateTimer.Start();
            }

            // v3.22.29 Items 6+7: write .ok startup sentinel(s) once the tray
            // is up and the message pump has been ticking for a few seconds.
            // CleanupUpdateArtifacts gates .old removal on these sentinels, so
            // if the NEW binary crashes during init before this Timer fires,
            // .old persists across the next launch and the torn-state branch
            // can restore it. 5s gives the WinForms loop time to absorb any
            // first-tick GDI/COM exceptions that a sentinel-on-launch would
            // miss.
            var sentinelTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            sentinelTimer.Tick += (_, _) =>
            {
                sentinelTimer.Stop();
                sentinelTimer.Dispose();
                UpdateDialog.WriteStartupSentinel();
            };
            sentinelTimer.Start();

            // --test-update: simulate update flow without hitting GitHub
#if DEBUG
            if (args.Contains("--test-update"))
            {
                UpdateDialog.TestMode = true;
                var timer = new System.Windows.Forms.Timer { Interval = 500 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    using var dlg = new UpdateDialog();
                    dlg.ShowDialog();
                };
                timer.Start();
            }
#endif

            Application.Run();
        }
        catch (Exception ex)
        {
            FileLogger.Error("Fatal error", ex);
            MessageBox.Show(
                $"EQSwitch encountered a fatal error:\n\n{ex.Message}",
                "EQSwitch Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            ConfigManager.FlushSave();
            ConfigManager.Shutdown();
            FileLogger.Shutdown();
            if (createdNew) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
    }

    private static void CleanupUpdateArtifacts()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var exePath = Path.Combine(dir, "EQSwitch.exe");

        // Torn-state recovery: if exe is missing but .old exists, restore it.
        // This branch fires when an interrupted update left the system without
        // an EQSwitch.exe (e.g., power failure between the original→.old move
        // and the .new→original move). Restoring .old gets the user back to
        // the previous working version.
        //
        // v3.22.29 verifier-found gap (Opus T3 #6): the prior version only
        // restored EQSwitch.exe and left hook.dll / di8.dll in whatever torn
        // state the interrupt produced. Result: mismatched-version binaries.
        // This loop now restores ALL three updateables symmetrically — if any
        // of them is missing AND its .old sibling exists, restore it.
        if (!File.Exists(exePath))
        {
            foreach (var fname in new[] { "EQSwitch.exe", "eqswitch-hook.dll", "eqswitch-di8.dll" })
            {
                var fullPath = Path.Combine(dir, fname);
                var oldPath = fullPath + ".old";
                if (!File.Exists(fullPath) && File.Exists(oldPath))
                {
                    try
                    {
                        File.Move(oldPath, fullPath);
                        FileLogger.Warn($"Recovered {fname} from .old after interrupted update.");
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Error($"Failed to recover {fname} from .old: {ex.Message}");
                    }
                }
            }
            return;
        }

        // v3.22.29 Items 6+7: .old cleanup is now gated on the .ok startup
        // sentinel for each updateable file. The new binary writes the
        // sentinel ~5s after Application.Run starts (see Program.Main); if
        // the new binary crashed during init the sentinel was never written
        // and .old persists, giving us a recovery path that doesn't require
        // a user rebuilding from GitHub.
        //
        // Per-file pairing:
        //   EQSwitch.exe.old      kept until EQSwitch.exe.ok       exists
        //   eqswitch-hook.dll.old kept until eqswitch-hook.dll.ok  exists
        //   eqswitch-di8.dll.old  kept until eqswitch-di8.dll.ok   exists
        // .new and update.zip artifacts are always safe to remove (incomplete
        // download or interrupted extract — never the user's only good copy).
        var oldFiles = new[] { "EQSwitch.exe.old", "eqswitch-hook.dll.old", "eqswitch-di8.dll.old" };
        var alwaysCleanup = new[] { "EQSwitch.exe.new", "eqswitch-hook.dll.new", "eqswitch-di8.dll.new", "update.zip" };

        foreach (var oldName in oldFiles)
        {
            var oldFullPath = Path.Combine(dir, oldName);
            if (!File.Exists(oldFullPath)) continue;

            // Sentinel name: strip ".old" and append ".ok". So "EQSwitch.exe.old" → "EQSwitch.exe.ok".
            var originalName = oldName.Substring(0, oldName.Length - ".old".Length);
            var sentinelPath = Path.Combine(dir, originalName + ".ok");
            if (!File.Exists(sentinelPath))
            {
                FileLogger.Warn($"Keeping {oldName} — sentinel {originalName}.ok missing (new binary did not finish init last launch).");
                continue;
            }

            TryRemoveWithRetry(oldFullPath, oldName);
        }

        foreach (var name in alwaysCleanup)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            TryRemoveWithRetry(path, name);
        }

        // After the .old cleanup, also remove stale .ok sentinels whose .old
        // pairs no longer exist — they were already swept above. Leaving them
        // would mean a future update's freshly-moved .old would be auto-cleaned
        // before the new binary had a chance to prove itself (the swap step in
        // OnActionClick already removes .ok pre-swap; this is belt+suspenders).
        foreach (var fname in new[] { "EQSwitch.exe", "eqswitch-hook.dll", "eqswitch-di8.dll" })
        {
            var sentinelPath = Path.Combine(dir, fname + ".ok");
            var oldPath = Path.Combine(dir, fname + ".old");
            if (File.Exists(sentinelPath) && !File.Exists(oldPath))
            {
                // .ok lingers from a prior update; safe to remove now that .old is gone.
                try { File.Delete(sentinelPath); }
                catch (Exception ex) { FileLogger.Warn($"Failed to remove stale sentinel {fname}.ok: {ex.Message}"); }
            }
        }
    }

    /// <summary>Retry delete with 500ms backoff — covers the MMF-release race on .old.</summary>
    private static void TryRemoveWithRetry(string path, string displayName)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Delete(path);
                FileLogger.Info($"Cleaned up update artifact: {displayName}");
                return;
            }
            catch (Exception) when (attempt < 2)
            {
                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"Failed to clean up {displayName}: {ex.Message}");
            }
        }
    }
}
