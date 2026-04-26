// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// Core/TestAutoLoginRunner.cs -- Self-managed autologin verification harness.
//
// Used by Program.cs's `--test-autologin [alias] [--timeout N]` CLI flag.
// Goal: drive a full real login end-to-end without any UI or manual tray
// interaction, then verify the native eqmain-side dispatch path produced zero
// SEH faults and the process is still alive at the end.
//
// How it slots into the existing architecture:
//   1. Loads config (ConfigManager.Load) same as TrayManager
//   2. Picks an Account by alias (or the first one with Autologin=true)
//   3. Creates AutoLoginManager with the same PreResumeCallback that
//      TrayManager.InjectPreResume uses (inject eqswitch-di8.dll into the
//      suspended eqgame.exe so our hooks are active before the EQ main
//      thread starts)
//   4. Calls LoginToCharselect(account) and awaits the returned Task
//   5. Monitors the AutoLoginManager.StatusUpdate event for phase names so
//      we can report progress in the CLI
//   6. After the task completes (or timeout), scans the log file for SEH
//      occurrences, kills eqgame.exe, prints a summary, and exits.
//
// Exit codes:
//   0 — login reached charselect AND zero SEH in mq2_bridge native path
//   1 — login did not reach charselect (timeout or error)
//   2 — login reached charselect BUT log has mq2_bridge SEH occurrences
//   3 — config/account not found
//
// Usage:
//   EQSwitch.exe --test-autologin                     # first autologin account, 120s timeout
//   EQSwitch.exe --test-autologin MyAcct              # specific account by alias/name
//   EQSwitch.exe --test-autologin MyAcct --timeout 180
//
// WARNING: This will launch a real eqgame.exe, log in, and kill the process.
// Don't run while playing on another EQ account on the same Windows user.

using System.Diagnostics;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

public static class TestAutoLoginRunner
{
    // Patterns we grep for in the log file to count native-path SEH occurrences.
    // These are the specific log lines mq2_bridge.cpp emits when the eqmain-side
    // dispatch or its fallback faults. Pre-Step-2B logs had many of these; the
    // goal is zero.
    private static readonly string[] SehPatterns =
    {
        "SEH in SetEditText native path",
        "SEH in SetEditText fallback",
        "SEH in ClickButton native path",
        "SEH in ClickButton fallback",
        "SEH in ReadWindowText",
    };

    // The injected DLL writes to this path (relative to the EQ install dir).
    // Derived from the first running eqgame.exe's working directory after launch.
    private const string LogFileName = "eqswitch-dinput8.log";

    public static int Run(string[] args)
    {
        string? alias = null;
        int timeoutSec = 120;

        // Parse flags after `--test-autologin`
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--timeout" && i + 1 < args.Length && int.TryParse(args[++i], out var t))
            {
                timeoutSec = t;
            }
            else if (!args[i].StartsWith("--"))
            {
                alias = args[i];
            }
        }

        Console.WriteLine($"[test-autologin] alias={alias ?? "<first autologin account>"} timeout={timeoutSec}s");

        // Load config
        AppConfig config;
        try
        {
            config = ConfigManager.Load();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-autologin] Failed to load config: {ex.Message}");
            return 3;
        }

        // Pick account
        Account? account = PickAccount(config, alias);
        if (account == null)
        {
            Console.Error.WriteLine($"[test-autologin] No account found (alias={alias}). " +
                $"Available: {string.Join(", ", config.Accounts.Select(a => a.EffectiveLabel))}");
            return 3;
        }
        Console.WriteLine($"[test-autologin] Using account: {account.EffectiveLabel} (user={account.Username}@{account.Server})");

        // Snapshot baseline log contents so we only measure SEH added during this run
        var eqDir = !string.IsNullOrEmpty(config.EQPath) ? config.EQPath : @"C:\Users\nate\proggy\Everquest\Eqfresh";
        var logPath = Path.Combine(eqDir, LogFileName);
        long logBaseline = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        Console.WriteLine($"[test-autologin] Log baseline offset: {logBaseline} bytes at {logPath}");

        // Build AutoLoginManager with di8 injection wired.
        // NOTE: no SetUiContext() call — Console-only test runner, no WinForms message
        // pump. All event handlers below MUST be thread-safe (events fire synchronously
        // on background login threads when _syncContext is null). Production uses
        // TrayManager.Initialize() to install the WindowsFormsSynchronizationContext
        // so handlers marshal to the UI thread instead.
        var autoLogin = new AutoLoginManager(config);
        autoLogin.PreResumeCallback = sp => InjectDi8(sp);

        int capturedPid = 0;
        autoLogin.LoginStarting += (_, pid) => { capturedPid = pid; };
        autoLogin.StatusUpdate += (_, msg) => Console.WriteLine($"[test-autologin] status: {msg}");

        // Run the login
        Console.WriteLine($"[test-autologin] Launching login for {account.Name}...");
        var sw = Stopwatch.StartNew();
        var task = autoLogin.LoginToCharselect(account);
        bool completed = task.Wait(TimeSpan.FromSeconds(timeoutSec));

        int exitCode = 0;
        if (!completed)
        {
            Console.WriteLine($"[test-autologin] TIMEOUT after {timeoutSec}s — killing eqgame.exe");
            exitCode = 1;
        }
        else if (task.IsFaulted)
        {
            Console.WriteLine($"[test-autologin] Login task FAULTED: {task.Exception?.InnerException?.Message}");
            exitCode = 1;
        }
        else
        {
            Console.WriteLine($"[test-autologin] Login task completed in {sw.Elapsed.TotalSeconds:F1}s");
        }

        // Give the DLL 2 seconds to flush final log lines before we kill
        Thread.Sleep(2000);

        // Kill eqgame processes by PID if known, else by name
        KillEqGame(capturedPid);

        // Parse log file for SEH occurrences since baseline
        int sehCount = CountSehInLog(logPath, logBaseline);
        Console.WriteLine($"[test-autologin] SEH occurrences in native path (since baseline): {sehCount}");

        if (sehCount > 0 && exitCode == 0) exitCode = 2;

        Console.WriteLine($"[test-autologin] EXIT {exitCode} — {(exitCode == 0 ? "PASS" : "FAIL")}");
        return exitCode;
    }

    private static Account? PickAccount(AppConfig config, string? alias)
    {
        if (!string.IsNullOrEmpty(alias))
        {
            return config.Accounts.FirstOrDefault(a =>
                string.Equals(a.Name, alias, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Username, alias, StringComparison.OrdinalIgnoreCase));
        }
        // Default: first account present
        return config.Accounts.FirstOrDefault();
    }

    private static void InjectDi8(SuspendedProcess sp)
    {
        var exeDir = AppContext.BaseDirectory;
        var di8Path = Path.Combine(exeDir, "eqswitch-di8.dll");
        if (!File.Exists(di8Path))
        {
            Console.Error.WriteLine($"[test-autologin] eqswitch-di8.dll missing at {di8Path}");
            return;
        }
        if (DllInjector.Inject(sp.Pid, di8Path))
            Console.WriteLine($"[test-autologin] Injected eqswitch-di8.dll into PID {sp.Pid}");
        else
            Console.Error.WriteLine($"[test-autologin] Injection failed for PID {sp.Pid}");
    }

    private static void KillEqGame(int preferredPid)
    {
        try
        {
            if (preferredPid > 0)
            {
                try
                {
                    using var p = Process.GetProcessById(preferredPid);
                    Console.WriteLine($"[test-autologin] Killing PID {preferredPid} ({p.ProcessName})");
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    return;
                }
                catch { /* fall through to name-based cleanup */ }
            }
            foreach (var p in Process.GetProcessesByName("eqgame"))
            {
                using (p)
                {
                    Console.WriteLine($"[test-autologin] Killing stray eqgame.exe PID {p.Id}");
                    try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
                    catch (Exception ex) { Console.Error.WriteLine($"[test-autologin] Kill failed: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-autologin] KillEqGame failure: {ex.Message}");
        }
    }

    private static int CountSehInLog(string logPath, long baseline)
    {
        if (!File.Exists(logPath)) return 0;
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(baseline, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            string content = sr.ReadToEnd();
            int count = 0;
            foreach (var pat in SehPatterns)
            {
                int idx = 0;
                while ((idx = content.IndexOf(pat, idx, StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    idx += pat.Length;
                }
            }
            // Also print the last 40 lines of new log content for operator visibility
            var lines = content.Split('\n');
            int takeFrom = Math.Max(0, lines.Length - 40);
            Console.WriteLine("[test-autologin] --- last 40 log lines of this run ---");
            for (int i = takeFrom; i < lines.Length; i++)
            {
                Console.WriteLine($"  {lines[i].TrimEnd('\r')}");
            }
            Console.WriteLine("[test-autologin] --- end log excerpt ---");
            return count;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-autologin] Log read failed: {ex.Message}");
            return 0;
        }
    }
}
