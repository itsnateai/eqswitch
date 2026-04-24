// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Launches an EQ client and auto-types credentials via foreground flash + SendInput.
/// Briefly brings each EQ window to the foreground during its typing phase, uses
/// SendInput for reliable keystroke delivery, then restores the previous window.
/// Runs the wait/type sequence on a background thread.
/// </summary>
public class AutoLoginManager
{
    private readonly AppConfig _config;

    /// <summary>PIDs currently in the login sequence — DLL injection should be deferred for these.</summary>
    private readonly ConcurrentDictionary<int, byte> _activeLoginPids = new();

    /// <summary>
    /// PID → the Character or Account name this PID was launched for. Populated
    /// at process-create time, read by TrayManager on ClientDiscovered to stamp
    /// <see cref="EQClient.BoundCharacterName"/>. Static so consumers can look
    /// up without holding an AutoLoginManager reference. Lives for the life of
    /// the launched process — TrayManager calls ClearBoundName on ClientLost.
    /// </summary>
    private static readonly ConcurrentDictionary<int, string> _pidBoundName = new();

    /// <summary>Look up the character/account name a PID was launched for. Returns
    /// false (and empty string) for externally-launched or post-cleanup PIDs.</summary>
    public static bool TryGetBoundName(int pid, out string name)
    {
        if (_pidBoundName.TryGetValue(pid, out var n)) { name = n; return true; }
        name = string.Empty;
        return false;
    }

    /// <summary>Drop the PID→name binding. Call from ClientLost so a recycled PID
    /// doesn't inherit a stale name.</summary>
    public static void ClearBoundName(int pid) => _pidBoundName.TryRemove(pid, out _);

    /// <summary>Serializes login sequences — only one login types credentials at a time.
    /// Multiple logins are queued and run sequentially to avoid timing issues under CPU load.</summary>
    // No serialization — logins run concurrently. Focus-faking is kept to
    // brief windows (activate → type → deactivate) to avoid conflicts.

    /// <summary>UI thread sync context — captured at construction for marshaling events back to the UI thread.</summary>
    private readonly SynchronizationContext? _syncContext;

    /// <summary>Callback to enforce eqclient.ini overrides before launch (injected to avoid Core→UI dependency).</summary>
    private readonly Action<AppConfig>? _enforceOverrides;

    public event EventHandler<string>? StatusUpdate;

    /// <summary>Fires just before the login sequence starts (use to pause guard timers).</summary>
    public event EventHandler<int>? LoginStarting;

    /// <summary>Fires when a login sequence completes (success or failure) with the PID.</summary>
    public event EventHandler<int>? LoginComplete;

    /// <summary>True if the given PID is currently running through the login sequence.</summary>
    public bool IsLoginActive(int pid) => _activeLoginPids.ContainsKey(pid);

    /// <summary>
    /// Callback invoked after process creation but before ResumeThread.
    /// Use for DLL injection into the suspended process.
    /// </summary>
    public Action<SuspendedProcess>? PreResumeCallback { get; set; }

    public AutoLoginManager(AppConfig config, Action<AppConfig>? enforceOverrides = null)
    {
        _config = config;
        _syncContext = SynchronizationContext.Current;
        _enforceOverrides = enforceOverrides;
    }

    /// <summary>
    /// Launch EQ, auto-type credentials, and stop at character select so the user
    /// can pick manually. Non-blocking — returns a Task that completes when the
    /// full sequence reaches char select (or fails).
    ///
    /// enterWorldOverride forces intent. true on an Account-only target is logged
    /// and downgraded to charselect (no Character target to select). false and
    /// null both stop at charselect (null = use type default which is charselect).
    /// </summary>
    public Task LoginToCharselect(Account account, bool? enterWorldOverride = null)
    {
        if (enterWorldOverride == true)
        {
            FileLogger.Warn($"AutoLogin: LoginToCharselect({account.Name}) called with enterWorldOverride=true — no Character target, staying at charselect");
            // Don't pass the override through — without a Character target, enter-world
            // would downgrade inside RunLoginSequence anyway. Pass null for cleaner logs.
            return BeginLogin(account, character: null, enterWorldOverride: null);
        }
        return BeginLogin(account, character: null, enterWorldOverride);
    }

    /// <summary>
    /// Launch EQ, auto-type credentials, select the given Character, and enter world.
    /// Resolves the backing Account via (AccountUsername, AccountServer) on the live
    /// _config.Accounts list. Non-blocking.
    ///
    /// enterWorldOverride forces intent. false stops at charselect without selecting
    /// the character (team-level override to skip a member). null defaults to enter-world
    /// (the type-system intent of clicking a Character).
    /// </summary>
    public Task LoginAndEnterWorld(Character character, bool? enterWorldOverride = null)
    {
        var key = character.AccountKey;
        var account = _config.Accounts.FirstOrDefault(key.Matches);
        if (account == null)
        {
            // Include the full FK in both log AND StatusUpdate — balloon tooltip is
            // the user's only easy diagnostic when FK drift happens (rename an Account
            // username without updating dependent Characters).
            var msg = $"Character '{character.Name}' points at Account {key}, which is not in the accounts list";
            FileLogger.Error($"AutoLogin: {msg}");
            StatusUpdate?.Invoke(this, $"Error: {msg}");
            return Task.CompletedTask;
        }
        return BeginLogin(account, character, enterWorldOverride);
    }

    /// <summary>
    /// Legacy entry point from the v3.x Tray menu. Synthesizes Account + optional
    /// Character from the combined LoginAccount type, then delegates to BeginLogin.
    /// The routing matches v3.9.x semantics exactly: AutoEnterWorld=true + non-empty
    /// CharacterName → enter world as that character; otherwise stop at charselect.
    ///
    /// Phase 3+ will move Tray callers off this method; removed in Phase 6 / v3.11.0.
    /// </summary>
    [Obsolete("Use LoginToCharselect(Account) or LoginAndEnterWorld(Character) for intent-explicit routing. Removed in v3.11.0.")]
    public Task LoginAccount(LoginAccount legacyAccount, bool? teamAutoEnter = null)
    {
        var account = new Account
        {
            Name = legacyAccount.Name,
            Username = legacyAccount.Username,
            EncryptedPassword = legacyAccount.EncryptedPassword,
            Server = legacyAccount.Server,
            UseLoginFlag = legacyAccount.UseLoginFlag,
        };

        // v3.9.x rule: enter-world if (team override says so) OR (per-account flag says so).
        // Team null means "use account default". Team non-null forces the decision.
        bool wantsEnterWorld = teamAutoEnter ?? legacyAccount.AutoEnterWorld;

        if (wantsEnterWorld && !string.IsNullOrEmpty(legacyAccount.CharacterName))
        {
            var character = new Character
            {
                Name = legacyAccount.CharacterName,
                AccountUsername = legacyAccount.Username,
                AccountServer = legacyAccount.Server,
                CharacterSlot = legacyAccount.CharacterSlot,
            };
            // Pass teamAutoEnter through so team-false can force charselect even on a Character target.
            return BeginLogin(account, character, teamAutoEnter);
        }
        // Account-only path. teamAutoEnter=true on an Account-only row is logged inside
        // RunLoginSequence as "enter-world requested but no Character target".
        return BeginLogin(account, character: null, teamAutoEnter);
    }

    /// <summary>
    /// Launch EQ with the given Account credentials and (optionally) a specific
    /// Character to select + enter world. Non-blocking — runs the login sequence
    /// on a background thread. Returns a Task that completes when the full
    /// sequence finishes.
    ///
    /// Intent routing:
    ///   character == null                           → stop at character select
    ///   character != null                           → select that character + enter world
    ///   enterWorldOverride != null (team override) → forces the decision regardless of the
    ///                                                 character-presence default
    /// </summary>
    private Task BeginLogin(Account account, Character? character, bool? enterWorldOverride)
    {
        string password;
        try
        {
            password = CredentialManager.Decrypt(account.EncryptedPassword);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // DPAPI unprotect failed — typically happens after cross-user config import
            // (DPAPI scope is CurrentUser on this machine). Surface the root cause so the
            // user knows to re-enter their password rather than assuming the app is broken.
            FileLogger.Error($"AutoLogin: DPAPI decrypt failed for '{account.Name}' — likely encrypted on a different Windows user. Re-enter password in Settings.", ex);
            StatusUpdate?.Invoke(this,
                $"Password for '{account.Name}' was encrypted on a different Windows user. Re-enter it in Settings \u2192 Accounts.");
            return Task.CompletedTask;
        }
        catch (FormatException ex)
        {
            // Stored blob is not valid Base64 — config file corruption.
            FileLogger.Error($"AutoLogin: stored password for '{account.Name}' is not valid Base64 — config corruption.", ex);
            StatusUpdate?.Invoke(this,
                $"Stored password for '{account.Name}' is corrupted. Re-enter it in Settings \u2192 Accounts.");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"AutoLogin: unexpected decrypt failure for '{account.Name}'", ex);
            StatusUpdate?.Invoke(this, $"Unexpected error decrypting password for '{account.Name}': {ex.Message}");
            return Task.CompletedTask;
        }

        // Write server to EQ INI files so server select is pre-filled
        WriteServerToIni(account.Server);

        // Build launch args
        var exePath = Path.Combine(_config.EQPath, _config.Launch.ExeName);
        if (!File.Exists(exePath))
        {
            FileLogger.Error($"AutoLogin: exe not found at {exePath}");
            StatusUpdate?.Invoke(this, "Error: eqgame.exe not found");
            return Task.CompletedTask;
        }

        var args = _config.Launch.Arguments;
        if (account.UseLoginFlag && !string.IsNullOrEmpty(account.Username))
            args += $" /login:{account.Username}";

        StatusUpdate?.Invoke(this, $"Launching {account.Name}...");

        int pid = -1;
        try
        {
            // Write eqclient.ini overrides before launch
            if (_enforceOverrides != null)
                _enforceOverrides(_config);
            else
                FileLogger.Warn("AutoLogin: no enforceOverrides callback registered, skipping INI overrides");

            // Launch with CREATE_SUSPENDED, then resume to let the loader init before injection
            var commandLine = string.IsNullOrEmpty(args)
                ? new StringBuilder($"\"{exePath}\"")
                : new StringBuilder($"\"{exePath}\" {args}");

            var si = new NativeMethods.STARTUPINFOA
            {
                cb = Marshal.SizeOf<NativeMethods.STARTUPINFOA>()
            };

            bool created = NativeMethods.CreateProcessA(
                null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                NativeMethods.CREATE_SUSPENDED, IntPtr.Zero, _config.EQPath,
                ref si, out var pi);

            if (!created)
            {
                var error = Marshal.GetLastWin32Error();
                FileLogger.Error($"AutoLogin: CreateProcessA failed (error {error})");
                StatusUpdate?.Invoke(this, "Error: failed to start process");
                return Task.CompletedTask;
            }

            pid = pi.dwProcessId;

            // Register as login-active BEFORE resume so ProcessManager's ClientDiscovered
            // handler sees IsLoginActive=true and defers window manipulation.
            _activeLoginPids.TryAdd(pid, 0);

            // Stamp the PID→bound-name mapping so TrayManager can render accurate
            // {CHAR} titles without relying on positional LegacyAccounts indexing
            // (which mis-maps team slots, e.g. team1Account2="backup" → "flotte").
            var boundName = !string.IsNullOrEmpty(character?.Name) ? character!.Name : account.Name;
            if (!string.IsNullOrEmpty(boundName))
                _pidBoundName[pid] = boundName;

            LoginStarting?.Invoke(this, pid);

            // Ensure handles are always closed, even if injection or resume throws
            try
            {
                FileLogger.Info($"AutoLogin: created suspended PID {pid} for {account.Name}");

                // Resume the main thread so the Windows loader initializes (loads kernel32, etc.)
                // Without this, EnumProcessModulesEx finds no modules and cross-arch injection fails.
                uint resumeResult = NativeMethods.ResumeThread(pi.hThread);
                if (resumeResult == 0xFFFFFFFF)
                {
                    var err = Marshal.GetLastWin32Error();
                    FileLogger.Error($"AutoLogin: ResumeThread failed (error {err}) — terminating zombie PID {pid}");
                    NativeMethods.TerminateProcess(pi.hProcess, 1);
                    _activeLoginPids.TryRemove(pid, out _);
                    _pidBoundName.TryRemove(pid, out _); // no ClientLost will fire — clear the stamp here
                    StatusUpdate?.Invoke(this, "Error: failed to resume process");
                    return Task.CompletedTask;
                }
                FileLogger.Info($"AutoLogin: resumed PID {pid}, waiting for loader...");

                // Wait for the loader to map kernel32.dll — injection needs it for LoadLibraryA
                if (!DllInjector.WaitForLoader(pi.hProcess, pid, timeoutMs: 5000))
                {
                    FileLogger.Warn($"AutoLogin: loader timeout for PID {pid} — injecting anyway");
                }

                // Inject DLLs now that the loader has initialized
                try
                {
                    var sp = new SuspendedProcess(pid, pi.hProcess, pi.hThread);
                    if (PreResumeCallback != null)
                        PreResumeCallback.Invoke(sp);
                    else
                        FileLogger.Warn("AutoLogin: PreResumeCallback not set — launching without DLL injection");
                }
                catch (Exception ex2)
                {
                    FileLogger.Warn($"AutoLogin: injection error: {ex2.Message}");
                }
            }
            finally
            {
                NativeMethods.CloseHandle(pi.hThread);
                NativeMethods.CloseHandle(pi.hProcess);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error("AutoLogin: launch failed", ex);
            if (pid > 0)
            {
                _activeLoginPids.TryRemove(pid, out _);
                _pidBoundName.TryRemove(pid, out _); // no ClientLost will fire
            }
            StatusUpdate?.Invoke(this, $"Error: {ex.Message}");
            return Task.CompletedTask;
        }

        // Run the login sequence on a background thread.
        var capturedAccount = account;
        var capturedCharacter = character;
        var capturedOverride = enterWorldOverride;
        return Task.Run(() => RunLoginSequence(pid, capturedAccount, capturedCharacter, password, capturedOverride));
    }

    private void RunLoginSequence(int pid, Account account, Character? character, string password, bool? enterWorldOverride)
    {
        // Snapshot config values — RunLoginSequence runs on a background thread
        // while ReloadConfig can mutate _config on the UI thread.
        int loginScreenDelayMs = _config.LoginScreenDelayMs;
        string eqPath = _config.EQPath;

        // Parallel-safe background login via brief activation windows.
        // Focus-faking (WndProc subclass + IAT hooks) is ONLY active during
        // keystroke bursts (~2s each). Between bursts it's OFF so multiple
        // logins don't fight for foreground.
        var writer = new KeyInputWriter();
        var charSelect = new CharSelectReader();
        LoginShmWriter? loginShm = null;
        try
        {
            // Open SHM early so DLL discovers it during EQ startup
            if (!writer.Open(pid))
            {
                Report("Error: failed to create DirectInput shared memory");
                return;
            }
            if (!charSelect.Open(pid))
            {
                // Hotfix v6b (Agent 2 F2.4): prior behavior was Warn + proceed,
                // which led to a 30s MQ2-bridge wait followed by the hotfix-v4
                // "MQ2 didn't initialize" abort — misleading the user because
                // the actual root cause was SHM creation (likely name collision
                // or permission), not MQ2. Match the writer.Open failure handling
                // above: surface the real error immediately.
                Report($"Error: failed to create character-select shared memory for {account.Name}");
                FileLogger.Error($"AutoLogin: CharSelectReader SHM open failed for PID {pid} — aborting login");
                return;
            }

            // Open LoginShm for in-process login (v7 Phase 4).
            // DLL's ActivateThread lazily discovers it via OpenFileMappingA.
            loginShm = new LoginShmWriter();
            if (!loginShm.Open(pid))
            {
                FileLogger.Warn($"AutoLogin: LoginShm open failed for PID {pid} — will use keyboard injection");
                loginShm.Dispose();
                loginShm = null;
            }

            // ── Wait for EQ window ──
            Report("Waiting for EQ window...");
            var hwnd = WaitForWindow(pid, TimeSpan.FromSeconds(30));
            if (hwnd == IntPtr.Zero) { Report("Timeout: EQ window did not appear"); return; }

            // ══════════════════════════════════════════════════════════════
            // PATH A: In-process login via LoginShm (zero keyboard injection)
            // DISABLED — native widget discovery needs a dedicated RE session.
            // GiveTime detour + LoginStateMachine work, but FindWindowByName
            // can't locate login widgets (all MQ2 exports null during login,
            // GetChildItem non-functional, heap scan offset unreliable).
            // See memory: project_eqswitch_v7_phase4_csharp.md
            // ══════════════════════════════════════════════════════════════
            if (loginShm != null)
            {
                bool shouldEnter = enterWorldOverride ?? (character != null);
                if (shouldEnter && character == null)
                {
                    FileLogger.Warn($"AutoLogin: {account.Name} requested enter-world but no Character target — staying at charselect (LoginShm path)");
                    shouldEnter = false;
                }

                bool handled = TryLoginViaShm(pid, loginShm, account, character,
                    password, shouldEnter, ref hwnd);
                if (handled)
                    return;

                // LoginShm path didn't complete — fall through to keyboard injection
                FileLogger.Info($"AutoLogin: LoginShm path failed for PID {pid}, falling back to keyboard injection");
            }

            // ══════════════════════════════════════════════════════════════
            // PATH B: Keyboard injection fallback (legacy)
            // ══════════════════════════════════════════════════════════════

            // ── Wait for login screen ──
            Report("Waiting for login screen...");
            Thread.Sleep(loginScreenDelayMs);

            // ══════════════════════════════════════════════════════════
            // BURST 1: Type credentials + submit (~3 seconds active)
            // ══════════════════════════════════════════════════════════
            Report("Typing credentials...");
            writer.Activate(pid, suppress: true);
            Thread.Sleep(500); // let DLL switch coop + blast activation
            FileLogger.Info($"AutoLogin: BURST 1 activated for PID {pid}");

            // NOTE: a pre-flight Enter before typing is NOT idempotent on Dalaya
            // patchme — empty-password Enter raises a "you need to enter a
            // username and password" modal that steals focus from the password
            // field, causing BURST 1 to type into the modal and Enter to click
            // OK instead of submit. Tested + reverted 2026-04-24.

            if (!account.UseLoginFlag && !string.IsNullOrEmpty(account.Username))
            {
                CombinedPressKey(writer, pid, hwnd, 0x09); // Tab to username
                Thread.Sleep(100);
                CombinedTypeString(writer, pid, hwnd, account.Username);
                Thread.Sleep(100);
            }
            if (!account.UseLoginFlag)
            {
                CombinedPressKey(writer, pid, hwnd, 0x09); // Tab to password
                Thread.Sleep(100);
            }
            CombinedTypeString(writer, pid, hwnd, password);
            Thread.Sleep(100);

            Report("Submitting login...");
            CombinedPressKey(writer, pid, hwnd, 0x0D); // Enter = submit
            Thread.Sleep(500);
            writer.Deactivate(pid); // ← OFF immediately after typing
            FileLogger.Info($"AutoLogin: BURST 1 deactivated for PID {pid}");

            // ── Wait for server response (no focus-faking) ──
            Thread.Sleep(3000);
            hwnd = RefreshHandle(pid, hwnd);
            if (hwnd == IntPtr.Zero) { Report($"{account.Name}: lost EQ window after login (crashed or closed)"); return; }

            // ══════════════════════════════════════════════════════════
            // BURST 2: Confirm server select (~1 second active)
            // ══════════════════════════════════════════════════════════
            Report("Confirming server...");
            writer.Activate(pid, suppress: true);
            Thread.Sleep(300);
            FileLogger.Info($"AutoLogin: BURST 2 activated for PID {pid}");
            CombinedPressKey(writer, pid, hwnd, 0x0D); // Enter = confirm
            Thread.Sleep(500);
            writer.Deactivate(pid); // ← OFF
            FileLogger.Info($"AutoLogin: BURST 2 deactivated for PID {pid}");

            // ── Wait for charselect load (no focus-faking, 5-60+ seconds) ──
            Report("Loading character select...");
            var transitionSw = System.Diagnostics.Stopwatch.StartNew();
            hwnd = WaitForScreenTransition(pid, hwnd, 90000);
            transitionSw.Stop();
            bool hitTimeout = transitionSw.ElapsedMilliseconds >= 90000 - 500;

            // Hotfix 2026-04-24: stale-session auto-recovery.
            // On Dalaya, if the login server still holds a prior session for
            // this account (e.g. previous run crashed, was killed hard, or a
            // fresh hotkey fire within ~30-45s of the previous login), EQ
            // shows a modal "Connection to the server could not be reached"
            // dialog with a focused OK button. The dialog blocks the login
            // flow — WaitForScreenTransition hits its 90s timeout and we
            // bail without recovery. Dismiss the dialog (Enter clicks the
            // focused OK button), wait for the server to release, and
            // re-fire the credential burst. One retry, then surface failure.
            if (hitTimeout && hwnd != IntPtr.Zero)
            {
                Report($"{account.Name}: transition timed out — dismissing any modal dialog + retrying login");
                FileLogger.Warn($"AutoLogin: {account.Name} hit 90s transition timeout — attempting stale-session recovery (one-shot retry)");

                // Enter clicks OK on any modal dialog; benign on a live login form.
                writer.Activate(pid, suppress: true);
                Thread.Sleep(300);
                CombinedPressKey(writer, pid, hwnd, 0x0D);
                Thread.Sleep(300);
                writer.Deactivate(pid);

                // Server-release wait — empirically Dalaya releases stale sessions in ~30-45s.
                Thread.Sleep(30000);
                hwnd = RefreshHandle(pid, hwnd);
                if (hwnd == IntPtr.Zero) { Report($"{account.Name}: lost EQ window during recovery wait"); return; }

                // Re-fire BURST 1 (credentials + submit).
                Report("Retry: typing credentials...");
                writer.Activate(pid, suppress: true);
                Thread.Sleep(500);
                FileLogger.Info($"AutoLogin: RETRY BURST 1 activated for PID {pid}");
                if (!account.UseLoginFlag && !string.IsNullOrEmpty(account.Username))
                {
                    CombinedPressKey(writer, pid, hwnd, 0x09); // Tab to username
                    Thread.Sleep(100);
                    CombinedTypeString(writer, pid, hwnd, account.Username);
                    Thread.Sleep(100);
                }
                if (!account.UseLoginFlag)
                {
                    CombinedPressKey(writer, pid, hwnd, 0x09); // Tab to password
                    Thread.Sleep(100);
                }
                CombinedTypeString(writer, pid, hwnd, password);
                Thread.Sleep(100);
                Report("Retry: submitting login...");
                CombinedPressKey(writer, pid, hwnd, 0x0D); // Enter = submit
                Thread.Sleep(500);
                writer.Deactivate(pid);
                FileLogger.Info($"AutoLogin: RETRY BURST 1 deactivated for PID {pid}");

                Thread.Sleep(3000);
                hwnd = RefreshHandle(pid, hwnd);
                if (hwnd == IntPtr.Zero) { Report($"{account.Name}: lost EQ window during retry submit"); return; }

                // Re-fire BURST 2 (server confirm).
                Report("Retry: confirming server...");
                writer.Activate(pid, suppress: true);
                Thread.Sleep(300);
                FileLogger.Info($"AutoLogin: RETRY BURST 2 activated for PID {pid}");
                CombinedPressKey(writer, pid, hwnd, 0x0D);
                Thread.Sleep(500);
                writer.Deactivate(pid);
                FileLogger.Info($"AutoLogin: RETRY BURST 2 deactivated for PID {pid}");

                // Re-wait for screen transition — 60s cap (already burned 90+30+burst).
                Report("Retry: loading character select...");
                transitionSw.Restart();
                hwnd = WaitForScreenTransition(pid, hwnd, 60000);
                transitionSw.Stop();
                hitTimeout = transitionSw.ElapsedMilliseconds >= 60000 - 500;
            }

            if (hitTimeout)
            {
                Report($"{account.Name}: char select didn't load (90s + retry) — check password / server / network");
                FileLogger.Error($"AutoLogin: WaitForScreenTransition timeout even after stale-session recovery for {account.Name} — aborting login");
                return;
            }
            if (hwnd == IntPtr.Zero) { Report($"{account.Name}: lost EQ window during charselect load (crashed or closed)"); return; }
            FileLogger.Info($"AutoLogin: charselect ready, hwnd=0x{hwnd:X} for PID {pid}");

            // ── Enter World gate ──
            // Default intent from type: Character target = enter world, Account-only = stop here.
            // enterWorldOverride (team Team{N}AutoEnter) can force the decision either way.
            // An enter-world request with no Character target is logged and downgraded to charselect
            // (we have no character name/slot to select, so entering world would land on EQ's default).
            bool shouldEnterWorld = enterWorldOverride ?? (character != null);
            if (shouldEnterWorld && character == null)
            {
                FileLogger.Warn($"AutoLogin: {account.Name} requested enter-world but no Character target — staying at charselect");
                shouldEnterWorld = false;
            }
            if (!shouldEnterWorld)
            {
                Report($"{account.Name} reached character select.");
                FileLogger.Info($"AutoLogin: {account.Name} stopped at char select (enterWorldOverride={enterWorldOverride?.ToString() ?? "null"}, character={character?.Name ?? "<none>"})");
                return;
            }

            // ── Character selection via MQ2 bridge (no focus-faking needed) ──
            // Priority: slot > name > default. character != null here (enforced by gate above).
            Thread.Sleep(2000); // let MQ2 bridge init

            bool wantSelection = character!.CharacterSlot > 0 || !string.IsNullOrEmpty(character.Name);
            if (wantSelection)
            {
                bool charListReady = false;
                // Wait up to 30s for char list — Dalaya's CXWndManager populates
                // ~25s after charselect screen appears (pinstCCharacterSelect timing)
                for (int wait = 0; wait < 60; wait++)
                {
                    if (charSelect.IsMQ2Available(pid) && charSelect.ReadCharCount(pid) > 0)
                    { charListReady = true; break; }

                    // Abort if user already entered the game (manual or other)
                    hwnd = RefreshHandle(pid, hwnd);
                    if (hwnd != IntPtr.Zero && IsInGame(charSelect, pid, hwnd))
                    {
                        FileLogger.Info("AutoLogin: already in-game during charlist wait, skipping selection");
                        break;
                    }

                    Thread.Sleep(500);
                }

                if (charListReady)
                {
                    // Snapshot character list ONCE from ReadAllCharNames. `charCount` used by
                    // bounds checks below must equal the scan snapshot — ReadCharCount re-reads
                    // SHM and can diverge from charNames.Length if the DLL refreshes between
                    // the two reads (feature-dev review finding M1a). Using charNames.Length
                    // ensures the abort-on-out-of-range check aligns with what Decide scanned.
                    var charNames = charSelect.ReadAllCharNames(pid);
                    int charCount = charNames.Length;
                    FileLogger.Info($"AutoLogin: {charCount} characters found: {string.Join(", ", charNames)}");

                    var (resolvedSlot, resolvedByName, decisionLog) = CharacterSelector.Decide(
                        character.CharacterSlot, character.Name, charNames);
                    FileLogger.Info($"AutoLogin: selector → {decisionLog}");

                    bool selected = false;
                    bool abortWrongCharacter = false;

                    if (resolvedSlot == 0)
                    {
                        // Unified safety abort — name-based target didn't resolve, OR malformed
                        // input (slot=0 + name=""). Entering world on EQ's default selection
                        // risks landing on the wrong character, which is a key-feature regression
                        // (the whole point of auto-login is to pick the right character). Users
                        // who need to auto-login while MQ2 is in slot-mode must use an explicit
                        // CharacterSlot (>0) which routes through Decide Case 3 and never reaches
                        // this branch. Promoted from a Phase 6 deferral during Phase 5b's cross-
                        // cutting review (feature-dev finding I2).
                        bool isSlotMode = charNames.Length > 0
                            && charNames[0].StartsWith("Slot ", StringComparison.Ordinal);
                        string cause = isSlotMode
                            ? $"MQ2 heap in slot-mode ({charNames.Length} placeholder slot(s)) — character names unavailable"
                            : $"character '{character.Name}' not found in account '{account.Name}'";
                        FileLogger.Error($"AutoLogin: {cause} — stopping at charselect to avoid wrong-character enter-world");
                        Report($"{account.Name}: {cause} — stopped at char select");
                        abortWrongCharacter = true;
                    }
                    else if (resolvedSlot > charCount)
                    {
                        // Slot out of range — same wrong-character guard as pre-extraction.
                        FileLogger.Error($"AutoLogin: slot {resolvedSlot} exceeds char count {charCount} — stopping at charselect to avoid wrong-character enter-world");
                        Report($"{account.Name}: slot {resolvedSlot} out of range (only {charCount} characters) — stopped at char select");
                        abortWrongCharacter = true;
                    }
                    else
                    {
                        charSelect.RequestSelectionBySlot(pid, resolvedSlot);
                        FileLogger.Info($"AutoLogin: requested slot {resolvedSlot} for PID {pid} (byName={resolvedByName})");
                        selected = true;
                    }

                    if (abortWrongCharacter)
                        return;

                    if (selected)
                    {
                        bool acked = false;
                        for (int ack = 0; ack < 50; ack++)  // 50x200ms = 10s
                        {
                            if (charSelect.IsSelectionAcknowledged(pid))
                            { acked = true; break; }
                            Thread.Sleep(200);
                        }
                        if (!acked)
                        {
                            // Hotfix v6b (Agent 2 F2.2, Agent 3 F3.2): pre-v6b behavior
                            // logged a Warn and fell through to Enter World. If the DLL
                            // never acked, SetCurSel's TIMERPROC never ran, so EQ's
                            // charselect is still on whatever slot was default (typically
                            // slot 0). Clicking Enter World then lands on the WRONG
                            // CHARACTER — exactly the regression Phase 5b's unified abort
                            // was designed to prevent. The ack-timeout path was an
                            // unguarded hole in that design. Abort instead.
                            FileLogger.Error($"AutoLogin: DLL did not ack selection for slot {resolvedSlot} in 10s — stopping at charselect to avoid wrong-character enter-world");
                            Report($"{account.Name}: character selection not confirmed — stopped at char select");
                            return;
                        }
                        Thread.Sleep(200); // SetCurSel fires synchronously on game thread via TIMERPROC
                    }
                }
                else
                {
                    // Hotfix v4 (HIGH-A): MQ2 bridge never came up after 30s wait. The
                    // pre-fix fallthrough would pulse Enter on EQ's DEFAULT-selected character
                    // — directly contradicts the "unified abort" design from Phase 5b's
                    // CharacterSelector work. Abort with a user-visible Report instead of
                    // phantom-entering world on the wrong character.
                    FileLogger.Error($"AutoLogin: MQ2 bridge not ready after 30s for PID {pid} — stopping at char select to avoid wrong-character enter-world");
                    Report($"{account.Name}: MQ2 bridge didn't initialize — stopped at char select");
                    return;
                }
            }

            // ── Enter World ──────────────────────────────────────────
            // Primary: in-process CLW_EnterWorldButton click via SHM (no focus-faking needed).
            // Fallback: PulseKey3D keyboard Enter (requires focus-faking).
            // Verified: CXWndManager is live at charselect with 630+ windows on Dalaya ROF2.
            Report("Entering world...");
            bool entered = false;

            // Primary path: SHM RequestEnterWorld (in-process button click)
            if (charSelect.IsMQ2Available(pid))
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    // Check if already in-game
                    hwnd = RefreshHandle(pid, hwnd);
                    if (hwnd == IntPtr.Zero) { Report($"{account.Name}: lost EQ window during enter-world (crashed or closed)"); return; }
                    if (IsInGame(charSelect, pid, hwnd))
                    {
                        entered = true;
                        FileLogger.Info($"AutoLogin: already in-game before SHM attempt {attempt + 1} (gameState=5 or title)");
                        break;
                    }

                    charSelect.RequestEnterWorld(pid);

                    // Wait for DLL ack (up to 5s)
                    bool acked = false;
                    for (int w = 0; w < 25; w++)
                    {
                        if (charSelect.IsEnterWorldAcknowledged(pid)) { acked = true; break; }
                        Thread.Sleep(200);
                    }

                    if (!acked)
                    {
                        FileLogger.Warn($"AutoLogin: DLL did not ack enter-world request (attempt {attempt + 1})");
                        continue;
                    }

                    int result = charSelect.ReadEnterWorldResult(pid);
                    if (result == -2)
                    {
                        // DLL detected we're already in-game (gameState=5). The user beat us
                        // to it (manual Enter, or a prior request already landed). Do NOT
                        // retry, do NOT fall back to PulseKey3D — that would phantom-click
                        // in-game UI. Treat as success.
                        entered = true;
                        FileLogger.Info($"AutoLogin: enter-world request dropped by DLL (already in-game, attempt {attempt + 1})");
                        break;
                    }
                    if (result == -4)
                    {
                        // Hotfix v6c (Agent 2 F2.5): SEH fault during
                        // CLW_EnterWorldButton click. The client's UI stack is in
                        // an unknown state — retrying or falling back to
                        // PulseKey3D could deepen the fault or hang the client.
                        // Abort cleanly with a user-visible message instead.
                        FileLogger.Error($"AutoLogin: EQ client faulted during Enter World click (SEH in game, attempt {attempt + 1}) — stopping to avoid further damage");
                        Report($"{account.Name}: EQ client faulted during Enter World — please restart the client");
                        return;
                    }
                    if (result != 1)
                    {
                        FileLogger.Warn($"AutoLogin: enter-world result={result} (attempt {attempt + 1}), button may not exist yet");
                        Thread.Sleep(2000);
                        continue;
                    }

                    FileLogger.Info($"AutoLogin: CLW_EnterWorldButton clicked via SHM (attempt {attempt + 1})");

                    // Wait for zone-load transition (Dalaya loads can take 5-90s).
                    // Primary: IsHungAppWindow hung→responsive pattern (authoritative
                    // on Dalaya where gameState stays 0 and title stays custom across
                    // char-select→in-world). Fallback: native " - " title flip.
                    if (WaitForEnterWorldTransition(pid, ref hwnd, 90))
                    {
                        entered = true;
                    }
                    // Button was confirmed clicked — never re-click (could cause disconnect)
                    break;
                }
            }

            // Fallback: PulseKey3D keyboard Enter (if SHM path failed).
            // Re-check title first — if user manually entered world while SHM was retrying,
            // the keyboard Enter would land in-game and trigger UI actions (phantom click).
            if (!entered)
            {
                hwnd = RefreshHandle(pid, hwnd);
                if (hwnd != IntPtr.Zero && IsInGame(charSelect, pid, hwnd))
                {
                    entered = true;
                    FileLogger.Info("AutoLogin: in-game detected before PulseKey3D fallback -- skipping (gameState=5 or title)");
                }
            }
            if (!entered)
            {
                if (charSelect.IsMQ2Available(pid))
                    FileLogger.Warn("AutoLogin: SHM enter-world failed, falling back to PulseKey3D");
                else
                    FileLogger.Info("AutoLogin: MQ2 not available, using PulseKey3D for enter-world");

                for (int attempt = 0; attempt < 3 && !entered; attempt++)
                {
                    hwnd = RefreshHandle(pid, hwnd);
                    if (hwnd == IntPtr.Zero) { Report($"{account.Name}: lost EQ window during PulseKey3D enter-world fallback (crashed or closed)"); return; }
                    if (IsInGame(charSelect, pid, hwnd))
                    {
                        entered = true;
                        FileLogger.Info($"AutoLogin: already in-game before PulseKey3D attempt {attempt + 1}");
                        break;
                    }

                    writer.Activate(pid, suppress: true);
                    Thread.Sleep(500);
                    PulseKey3D(writer, pid, hwnd, 0x0D);
                    Thread.Sleep(500);
                    writer.Deactivate(pid);

                    // Wait for zone-load transition. Same IsHungAppWindow-based
                    // detector as the SHM path; 60s cap (PulseKey3D is the
                    // fallback — if the first attempt's keystroke didn't land
                    // we still have 2 more attempts).
                    if (WaitForEnterWorldTransition(pid, ref hwnd, 60))
                    {
                        entered = true;
                    }
                }
            }

            if (entered)
            {
                Report($"{account.Name} logged in!");
                FileLogger.Info($"AutoLogin: {account.Name} login complete (PID {pid})");
            }
            else
            {
                Report($"{account.Name}: reached char select but Enter World didn't register");
                FileLogger.Warn($"AutoLogin: {account.Name} enter-world failed after all attempts (PID {pid})");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"AutoLogin: sequence failed for {account.Name}", ex);
            Report($"Error: {ex.Message}");
        }
        finally
        {
            // Deactivate FIRST — ensure focus-faking stops before handles close.
            // Hotfix v3 (MED-1): log instead of silently swallowing — a failed
            // Deactivate means SHM could be left active with stale keys, which
            // is exactly the phantom-keys regression the hotfix prevents.
            try { writer.Deactivate(pid); }
            catch (Exception ex) { FileLogger.Warn($"AutoLogin: finally Deactivate failed for PID {pid}: {ex.Message}"); }
            try { charSelect.Close(pid); }
            catch (Exception ex) { FileLogger.Warn($"AutoLogin: finally charSelect.Close failed for PID {pid}: {ex.Message}"); }
            try { charSelect.Dispose(); }
            catch (Exception ex) { FileLogger.Warn($"AutoLogin: finally charSelect.Dispose failed: {ex.Message}"); }
            try { writer.Close(pid); } catch (Exception ex) { FileLogger.Warn($"AutoLogin: Close failed: {ex.Message}"); }
            try { writer.Dispose(); } catch (Exception ex) { FileLogger.Warn($"AutoLogin: Dispose failed: {ex.Message}"); }
            if (loginShm != null)
            {
                try { loginShm.Close(pid); } catch (Exception ex) { FileLogger.Warn($"AutoLogin: loginShm.Close failed for PID {pid}: {ex.Message}"); }
                try { loginShm.Dispose(); } catch (Exception ex) { FileLogger.Warn($"AutoLogin: loginShm.Dispose failed: {ex.Message}"); }
            }
            // Remove PID and fire LoginComplete atomically on the UI thread.
            // If TryRemove runs here (background thread) before Post, there's a gap
            // where IsLoginActive returns false but LoginComplete hasn't fired yet —
            // ClientDiscovered could bypass the "don't touch window during login" guard.
            if (_syncContext != null)
            {
                _syncContext.Post(_ =>
                {
                    _activeLoginPids.TryRemove(pid, out byte _);
                    LoginComplete?.Invoke(this, pid);
                }, null);
            }
            else
            {
                _activeLoginPids.TryRemove(pid, out byte _);
                LoginComplete?.Invoke(this, pid);
            }
        }
    }

    // ─── LoginShm Path (v7 Phase 4) ──────────────────────────────────
    //
    // In-process login via the DLL's LoginStateMachine. The DLL reads
    // credentials from LoginShm and calls CXWnd::SetWindowText on EQ's
    // edit fields + SendWndNotification(XWM_LCLICK) on buttons. Zero
    // keyboard injection, zero focus-faking.
    //
    // Returns true if the login flow was fully handled (charselect or
    // in-world). Returns false if the DLL didn't respond or errored —
    // caller should fall back to the keyboard injection path.

    private bool TryLoginViaShm(int pid, LoginShmWriter loginShm, Account account,
        Character? character, string password, bool shouldEnterWorld,
        ref IntPtr hwnd)
    {
        // Determine character name for the DLL's name-based selection.
        // Empty string = DLL skips character selection (C# sends CANCEL at charselect).
        string charName = shouldEnterWorld && character != null
            ? character.Name ?? "" : "";

        // Send LOGIN command with credentials
        if (!loginShm.SendLoginCommand(pid, account.Username, password,
                account.Server, charName))
        {
            FileLogger.Warn($"AutoLogin: LoginShm SendLoginCommand failed for PID {pid}");
            return false;
        }

        // Wait for DLL to acknowledge command (up to 15s).
        // The DLL opens LoginShm lazily every ~5s — first ack may take a cycle.
        bool acked = false;
        for (int wait = 0; wait < 75; wait++) // 75 * 200ms = 15s
        {
            if (loginShm.IsCommandAcknowledged(pid)) { acked = true; break; }
            Thread.Sleep(200);
        }
        if (!acked)
        {
            FileLogger.Warn($"AutoLogin: DLL did not acknowledge LoginShm command in 15s for PID {pid}");
            loginShm.SendCancelCommand(pid);
            return false;
        }

        FileLogger.Info($"AutoLogin: DLL acknowledged LoginShm command for PID {pid}");

        // Monitor phase transitions (200ms poll, 14s overall timeout).
        // 14s matches the pre-fix implicit YESNO-SetError fast-fail timing;
        // DLL in-process credential entry is ABI-broken on Dalaya (b2d2d69),
        // so this outer timeout is the backstop that routes to keyboard
        // injection when the DLL progresses past phase-1 but can't actually
        // type. Was 120s — caused ~100s wall-clock regression when the
        // YESNO-SetError implicit fast-fail was removed. See handoff
        // _.comms/handoff-eqswitch-next-20260424-pm.md.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LoginPhase lastReported = LoginPhase.Idle;

        while (sw.ElapsedMilliseconds < 14_000)
        {
            var phase = loginShm.ReadPhase(pid);

            // Report phase transitions to user
            if (phase != lastReported)
            {
                string status = LoginShmWriter.PhaseName(phase);
                Report($"{account.Name}: {status}");
                lastReported = phase;
                FileLogger.Info($"AutoLogin: LoginShm phase={phase} for PID {pid} at {sw.ElapsedMilliseconds}ms");
            }

            switch (phase)
            {
                case LoginPhase.Error:
                    var error = loginShm.ReadError(pid);
                    FileLogger.Error($"AutoLogin: LoginShm error for {account.Name}: {error}");
                    Report($"{account.Name}: {error}");
                    return false; // Fall back to keyboard path

                case LoginPhase.WaitLoginScreen:
                    // Fast-fail: DLL stuck at phase 1 (widget discovery failed).
                    // Works together with the outer 14s overall-timeout: this
                    // 3s handles the "DLL never advances past phase 1" case,
                    // the 14s handles the "DLL advances but can't complete
                    // credentials due to ABI mismatch" case. Both route to
                    // keyboard injection (the actual credential-entry path).
                    if (sw.ElapsedMilliseconds > 3_000)
                    {
                        FileLogger.Warn($"AutoLogin: LoginShm stuck at WaitLoginScreen for 3s — DLL widget discovery not working, falling back to keyboard");
                        loginShm.SendCancelCommand(pid);
                        return false;
                    }
                    break;

                case LoginPhase.CharSelect:
                    return HandleCharSelectViaShm(pid, loginShm, account, character,
                        shouldEnterWorld, ref hwnd, sw);

                case LoginPhase.Complete:
                    Report($"{account.Name} logged in!");
                    FileLogger.Info($"AutoLogin: {account.Name} login complete via LoginShm ({sw.ElapsedMilliseconds}ms, PID {pid})");
                    return true;
            }

            // Check if EQ process died
            hwnd = RefreshHandle(pid, hwnd);
            if (hwnd == IntPtr.Zero)
            {
                Report($"{account.Name}: EQ process died during login");
                return true; // Don't fall back — process is gone
            }

            Thread.Sleep(200);
        }

        // Overall timeout — routes to keyboard injection when DLL advances
        // past phase 1 but can't complete (ABI-broken in-process credentials).
        FileLogger.Error($"AutoLogin: LoginShm overall timeout (14s) for {account.Name}");
        loginShm.SendCancelCommand(pid);
        return false;
    }

    /// <summary>
    /// Handle the PHASE_CHAR_SELECT transition from LoginShm.
    /// If !shouldEnterWorld: send CANCEL to stop DLL, report charselect reached.
    /// If shouldEnterWorld: validate character via CharacterSelector.Decide,
    /// then let DLL continue to enter-world or abort on safety failure.
    /// </summary>
    private bool HandleCharSelectViaShm(int pid, LoginShmWriter loginShm,
        Account account, Character? character, bool shouldEnterWorld,
        ref IntPtr hwnd, System.Diagnostics.Stopwatch sw)
    {
        if (!shouldEnterWorld)
        {
            // Send CANCEL before DLL's 500ms debounce expires and auto-advances
            loginShm.SendCancelCommand(pid);
            Report($"{account.Name} reached character select.");
            FileLogger.Info($"AutoLogin: {account.Name} stopped at char select via LoginShm ({sw.ElapsedMilliseconds}ms, PID {pid})");
            return true;
        }

        // ── shouldEnterWorld = true, character != null (enforced by caller) ──
        // Validate character selection using C#'s CharacterSelector.Decide.
        // The DLL will also do its own name matching, but C# gates on safety
        // (slot-mode detection, wrong-character abort) first.
        var charNames = loginShm.ReadAllCharNames(pid);
        int charCount = charNames.Length;
        FileLogger.Info($"AutoLogin: LoginShm charselect — {charCount} characters: {string.Join(", ", charNames)}");

        var (resolvedSlot, resolvedByName, decisionLog) = CharacterSelector.Decide(
            character!.CharacterSlot, character.Name, charNames);
        FileLogger.Info($"AutoLogin: LoginShm selector → {decisionLog}");

        // ── Safety gate (mirrors keyboard path's unified abort) ──
        if (resolvedSlot == 0)
        {
            bool isSlotMode = charNames.Length > 0
                && charNames[0].StartsWith("Slot ", StringComparison.Ordinal);
            string cause = isSlotMode
                ? $"MQ2 heap in slot-mode ({charNames.Length} placeholder slot(s)) — character names unavailable"
                : $"character '{character.Name}' not found in account '{account.Name}'";
            FileLogger.Error($"AutoLogin: LoginShm {cause} — sending CANCEL to prevent wrong-character enter-world");
            loginShm.SendCancelCommand(pid);
            Report($"{account.Name}: {cause} — stopped at char select");
            return true; // Handled (safety abort) — don't fall back to keyboard
        }

        if (resolvedSlot > charCount)
        {
            FileLogger.Error($"AutoLogin: LoginShm slot {resolvedSlot} exceeds char count {charCount} — sending CANCEL");
            loginShm.SendCancelCommand(pid);
            Report($"{account.Name}: slot {resolvedSlot} out of range (only {charCount} characters) — stopped at char select");
            return true;
        }

        // Validation passed — let the DLL continue to ENTERING_WORLD.
        // The DLL's PHASE_CHAR_SELECT handler will match the character name
        // we wrote to LoginShm and select it via MQ2Bridge::SelectCharacter.
        FileLogger.Info($"AutoLogin: LoginShm validation passed — DLL will enter world as '{character.Name}' (slot {resolvedSlot})");

        // Monitor until PHASE_COMPLETE, PHASE_ERROR, or timeout
        while (sw.ElapsedMilliseconds < 120_000)
        {
            var phase = loginShm.ReadPhase(pid);

            switch (phase)
            {
                case LoginPhase.Complete:
                    Report($"{account.Name} logged in!");
                    FileLogger.Info($"AutoLogin: {account.Name} login complete via LoginShm ({sw.ElapsedMilliseconds}ms, PID {pid})");
                    return true;

                case LoginPhase.Error:
                    var error = loginShm.ReadError(pid);
                    FileLogger.Error($"AutoLogin: LoginShm enter-world error for {account.Name}: {error}");
                    Report($"{account.Name}: Enter World failed — {error}");
                    // DLL's enter-world failed (CLW_EnterWorldButton not found, etc.)
                    // Fall back to keyboard path's PulseKey3D enter-world
                    return false;

                case LoginPhase.EnteringWorld:
                    // Still working — also check for in-game transition
                    // (DLL's PHASE_COMPLETE detection may lag behind actual in-game).
                    // Uses gameState=5 as primary signal, title " - " as fallback.
                    hwnd = RefreshHandle(pid, hwnd);
                    if (hwnd != IntPtr.Zero && IsInGame(loginShm.ReadGameState(pid), hwnd))
                    {
                        Report($"{account.Name} logged in!");
                        FileLogger.Info($"AutoLogin: {account.Name} in-game detected before DLL PHASE_COMPLETE ({sw.ElapsedMilliseconds}ms, PID {pid})");
                        return true;
                    }
                    break;
            }

            // Check if EQ process died
            hwnd = RefreshHandle(pid, hwnd);
            if (hwnd == IntPtr.Zero)
            {
                Report($"{account.Name}: EQ process died during enter-world");
                return true;
            }

            Thread.Sleep(500);
        }

        FileLogger.Error($"AutoLogin: LoginShm enter-world timeout for {account.Name}");
        return false; // Fall back to keyboard enter-world
    }

    // ─── Combined Background Typing (SHM + PostMessage) ─────────────
    //
    // Two-layer approach for true background login:
    // 1. DLL hooks (GetForegroundWindow/GetAsyncKeyState) — SHM makes EQ think
    //    it's the active window and provides synthetic key state
    // 2. PostMessage (WM_KEYDOWN/WM_CHAR) — delivers actual key events to the
    //    message queue for login text field processing
    // Both layers are needed because EQ checks focus before processing input.

    private static IntPtr MakeKeyDownLParam(uint scanCode)
        => (IntPtr)(1 | ((int)scanCode << 16));

    private static IntPtr MakeKeyUpLParam(uint scanCode)
        => (IntPtr)(1 | ((int)scanCode << 16) | (1 << 30) | unchecked((int)(1u << 31)));

    /// <summary>PostMessage wrapper that logs on failure (window may have closed mid-login).</summary>
    private static bool Post(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (NativeMethods.PostMessage(hwnd, msg, wParam, lParam)) return true;
        FileLogger.Warn($"AutoLogin: PostMessage failed (hwnd=0x{hwnd:X}, msg=0x{msg:X}), window may have closed");
        return false;
    }

    /// <summary>PostMessage with retry. No SHM reactivation — if PostMessage fails,
    /// the window is likely dead. Retrying with brief pauses handles transient OS hiccups.</summary>
    private static bool PostR(KeyInputWriter writer, int pid, IntPtr hwnd,
        uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Post(hwnd, msg, wParam, lParam)) return true;
        // Window might be closing — check validity before retry
        if (!NativeMethods.IsWindow(hwnd)) return false;
        Thread.Sleep(50);
        return Post(hwnd, msg, wParam, lParam);
    }

    /// <summary>Press a key via SHM (for DLL hooks) AND PostMessage (for message queue).</summary>
    private static void CombinedPressKey(KeyInputWriter writer, int pid, IntPtr hwnd, ushort vk)
    {
        uint scan = NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);

        if (scan > 0 && scan < 256) writer.SetKey(pid, (byte)scan, true);
        PostR(writer, pid, hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)vk, MakeKeyDownLParam(scan));
        Thread.Sleep(80);

        if (scan > 0 && scan < 256) writer.SetKey(pid, (byte)scan, false);
        PostR(writer, pid, hwnd, NativeMethods.WM_KEYUP, (IntPtr)vk, MakeKeyUpLParam(scan));
        Thread.Sleep(80);
    }

    /// <summary>Pulse a key 3 times with 500ms holds — for the 3D char select where
    /// GetDeviceData polling is very infrequent.</summary>
    private static void PulseKey3D(KeyInputWriter writer, int pid, IntPtr hwnd, ushort vk)
    {
        uint scan = NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);
        for (int p = 0; p < 3; p++)
        {
            if (scan > 0 && scan < 256) writer.SetKey(pid, (byte)scan, true);
            PostR(writer, pid, hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)vk, MakeKeyDownLParam(scan));
            Thread.Sleep(500);

            if (scan > 0 && scan < 256) writer.SetKey(pid, (byte)scan, false);
            PostR(writer, pid, hwnd, NativeMethods.WM_KEYUP, (IntPtr)vk, MakeKeyUpLParam(scan));
            Thread.Sleep(150);
        }
    }

    /// <summary>Type a string via SHM + PostMessage. Posts WM_KEYDOWN + WM_CHAR + WM_KEYUP.</summary>
    private static void CombinedTypeString(KeyInputWriter writer, int pid, IntPtr hwnd, string text)
    {
        foreach (char c in text)
        {
            short vkScan = NativeMethods.VkKeyScanW(c);
            if (vkScan == -1)
            {
                // Hotfix v6b (Agent 1 F1.5): mirror the AltGr-skip warning so an
                // unmappable character surfaces in the log. Previously this
                // branch was a silent `continue` — a password character that
                // VkKeyScanW can't map on the user's keyboard layout was
                // dropped without a log line, and EQ returned "wrong password"
                // with zero diagnostic guidance.
                FileLogger.Warn($"AutoLogin: CombinedTypeString skipping char '{c}' (U+{(int)c:X4}) — unmappable on current keyboard layout");
                continue;
            }

            byte modifiers = (byte)(vkScan >> 8);
            if ((modifiers & ~0x01) != 0)
            {
                // Hotfix v4 (L1): log skipped chars so European-layout passwords that
                // need AltGr (e.g. @, {, }, [, ] on DE/FR/ES layouts) surface visibly
                // instead of silently producing a mystery login failure.
                FileLogger.Warn($"AutoLogin: CombinedTypeString skipping char '{c}' (U+{(int)c:X4}) — requires modifier 0x{modifiers:X} (Ctrl/Alt/AltGr)");
                continue;
            }

            ushort vk = (ushort)(vkScan & 0xFF);
            bool needShift = (modifiers & 0x01) != 0;
            uint scan = NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);
            uint shiftScan = NativeMethods.MapVirtualKeyW(0x10, NativeMethods.MAPVK_VK_TO_VSC);

            // Shift down (both layers)
            if (needShift)
            {
                if (shiftScan > 0 && shiftScan < 256) writer.SetKey(pid, (byte)shiftScan, true);
                PostR(writer, pid, hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)0x10, MakeKeyDownLParam(shiftScan));
                Thread.Sleep(20);
            }

            // Key down (both layers) + WM_CHAR for text field
            if (scan > 0 && scan < 256) writer.SetKey(pid, (byte)scan, true);
            PostR(writer, pid, hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)vk, MakeKeyDownLParam(scan));
            PostR(writer, pid, hwnd, (uint)NativeMethods.WM_CHAR, (IntPtr)c, MakeKeyDownLParam(scan));
            Thread.Sleep(80);

            // Key up (both layers)
            if (scan > 0 && scan < 256) writer.SetKey(pid, (byte)scan, false);
            PostR(writer, pid, hwnd, NativeMethods.WM_KEYUP, (IntPtr)vk, MakeKeyUpLParam(scan));

            // Shift up
            if (needShift)
            {
                Thread.Sleep(40);
                if (shiftScan > 0 && shiftScan < 256) writer.SetKey(pid, (byte)shiftScan, false);
                PostR(writer, pid, hwnd, NativeMethods.WM_KEYUP, (IntPtr)0x10, MakeKeyUpLParam(shiftScan));
            }
            Thread.Sleep(50);
        }
    }

    private void Report(string message)
    {
        FileLogger.Info($"AutoLogin: {message}");
        if (_syncContext != null)
            _syncContext.Post(_ => StatusUpdate?.Invoke(this, message), null);
        else
            StatusUpdate?.Invoke(this, message);
    }

    /// <summary>
    /// Poll for an eqgame window belonging to the given PID.
    /// </summary>
    private static IntPtr WaitForWindow(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                var hwnd = proc.MainWindowHandle;
                if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd))
                    return hwnd;
            }
            catch (ArgumentException)
            {
                return IntPtr.Zero; // Process exited
            }
            Thread.Sleep(500);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Wait for a screen transition to complete by detecting when EQ's window goes
    /// unresponsive (loading 3D scene) and becomes responsive again. Falls back on
    /// window rect instability detection if the window never fully hangs.
    /// Returns the (possibly refreshed) window handle, or IntPtr.Zero if the process died.
    /// </summary>
    private static IntPtr WaitForScreenTransition(int pid, IntPtr hwnd, int maxWaitMs = 90000)
    {
        var sw = Stopwatch.StartNew();
        bool sawHung = false;
        bool sawRectChange = false;
        NativeMethods.GetWindowRect(hwnd, out var initialRect);
        var lastRect = initialRect;
        long lastRectChangeMs = 0;

        // Give EQ a moment to start the transition before polling
        Thread.Sleep(1000);

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            // Refresh handle — EQ may recreate its window during transition
            hwnd = RefreshHandle(pid, hwnd);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;

            bool hung = NativeMethods.IsHungAppWindow(hwnd);

            if (hung)
            {
                sawHung = true;
                FileLogger.Info($"AutoLogin: EQ window hung (loading), elapsed={sw.ElapsedMilliseconds}ms");
            }
            else if (sawHung)
            {
                // Was hung, now responsive — transition complete
                FileLogger.Info($"AutoLogin: EQ responsive after loading, elapsed={sw.ElapsedMilliseconds}ms");
                Thread.Sleep(1000); // brief settle time for render
                return RefreshHandle(pid, hwnd);
            }

            // Fallback: track window rect changes (distortion + snap-back pattern).
            // Ignore user drags — a drag changes Left/Top but not Width/Height. The EQ
            // transition produces SIZE changes (re-render at different resolution / mode).
            if (NativeMethods.GetWindowRect(hwnd, out var currentRect))
            {
                bool sizeChanged = currentRect.Width != lastRect.Width ||
                                   currentRect.Height != lastRect.Height;
                bool posChanged  = currentRect.Left != lastRect.Left ||
                                   currentRect.Top != lastRect.Top;
                bool rectChanged = sizeChanged || posChanged;
                // Only treat size changes as transition signal; pos-only changes are user drags.
                if (sizeChanged)
                {
                    sawRectChange = true;
                    lastRectChangeMs = sw.ElapsedMilliseconds;
                    lastRect = currentRect;
                }
                else if (rectChanged)
                {
                    // Position-only change (user drag) — update tracker but don't arm fallback
                    lastRect = currentRect;
                }
                else if (sawRectChange && !sawHung &&
                         sw.ElapsedMilliseconds - lastRectChangeMs > 3000)
                {
                    // Rect changed and has been stable for 3s without a hung period —
                    // the transition completed without fully hanging the message pump
                    FileLogger.Info($"AutoLogin: rect stable after change, elapsed={sw.ElapsedMilliseconds}ms");
                    Thread.Sleep(500);
                    return RefreshHandle(pid, hwnd);
                }
            }

            Thread.Sleep(500);
        }

        // Timeout — proceed anyway (better than hanging forever)
        FileLogger.Error($"AutoLogin: screen transition timeout after {maxWaitMs}ms — char select may not have loaded (possible bad password, dead server, or network issue)");
        return RefreshHandle(pid, hwnd);
    }

    /// <summary>
    /// Is this PID in-game? Primary signal is the DLL's gameState sensor
    /// (written to the SHM every poll tick); gameState=5 reliably means
    /// in-world on Dalaya ROF2. Secondary signal is the window title
    /// containing " - " (classic "EverQuest - CharName" pattern), used as
    /// a fallback when MQ2 exports aren't yet resolved or on non-Dalaya EQ
    /// variants. The title-alone check misfires on Dalaya patchme because
    /// the client keeps the title "EverQuest" even after successful Enter
    /// World — caused the "enter-world failed after all attempts"
    /// noise-log that this helper fixes (2026-04-24).
    /// </summary>
    private static bool IsInGame(CharSelectReader charSelect, int pid, IntPtr hwnd)
        => IsInGame(charSelect.ReadGameState(pid), hwnd);

    /// <summary>
    /// Overload for callers that already have a fresh gameState reading
    /// from another SHM source (e.g. LoginShmWriter during the LoginShm path).
    /// Strict — returns true only on signals that CANNOT fire pre-in-world:
    /// gameState=5 (non-Dalaya) and title containing " - " (native EQ flip).
    /// Does NOT consider custom WindowTitleTemplate output as in-world —
    /// EQSwitch's WindowManager.SetWindowTitle fires at launch regardless of
    /// game phase (confirmed 2026-04-24 live: title "1076 , natedogg , 1"
    /// visible at char-select, causing a false-positive "already in-game"
    /// that skipped the Enter World click). In-world detection on Dalaya
    /// uses the zone-load IsHungAppWindow transition pattern instead —
    /// see the post-click loops in the enter-world logic.
    /// </summary>
    private static bool IsInGame(int gameState, IntPtr hwnd)
    {
        if (gameState == 5) return true;
        if (hwnd != IntPtr.Zero)
        {
            var tb = new StringBuilder(256);
            NativeMethods.GetWindowText(hwnd, tb, tb.Capacity);
            if (tb.ToString().Contains(" - ")) return true;
        }
        return false;
    }

    /// <summary>
    /// Post-Enter-World zone-load detection. Polls for the IsHungAppWindow
    /// hung→responsive transition that fires when EQ's 3D engine loads the
    /// player's zone. Authoritative in-world signal on Dalaya ROF2 where
    /// title and gameState both stay constant across the char-select→in-world
    /// boundary. Also catches the native title flip (" - ") as a belt-and-
    /// braces fallback for non-Dalaya variants.
    /// Returns true if a transition was observed, false on timeout / process
    /// death. hwnd is refreshed in place.
    /// </summary>
    private static bool WaitForEnterWorldTransition(int pid, ref IntPtr hwnd, int maxWaitSec)
    {
        bool sawHung = false;
        for (int i = 0; i < maxWaitSec; i++)
        {
            Thread.Sleep(1000);
            hwnd = RefreshHandle(pid, hwnd);
            if (hwnd == IntPtr.Zero) return false;

            // Native title flip — works on non-Dalaya EQ variants.
            var tb = new StringBuilder(256);
            NativeMethods.GetWindowText(hwnd, tb, tb.Capacity);
            if (tb.ToString().Contains(" - "))
            {
                FileLogger.Info($"AutoLogin: zone-load confirmed via title flip after {i + 1}s (title: {tb})");
                return true;
            }

            bool hung = NativeMethods.IsHungAppWindow(hwnd);
            if (hung)
            {
                sawHung = true;
            }
            else if (sawHung)
            {
                // Was hung (3D scene loading), now responsive — zone loaded.
                FileLogger.Info($"AutoLogin: zone-load confirmed via IsHungAppWindow transition after {i + 1}s");
                Thread.Sleep(1500); // settle time for render
                hwnd = RefreshHandle(pid, hwnd);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Re-resolve window handle for a PID. EQ recreates its window on screen transitions
    /// (login → server select → character select). Returns the new handle, or the original
    /// if the process still owns it. Returns IntPtr.Zero if the process exited.
    /// </summary>
    private static IntPtr RefreshHandle(int pid, IntPtr currentHwnd)
    {
        // If current handle is still valid, keep it (avoids unnecessary Process lookup)
        if (NativeMethods.IsWindow(currentHwnd)) return currentHwnd;

        try
        {
            using var proc = Process.GetProcessById(pid);
            var newHwnd = proc.MainWindowHandle;
            if (newHwnd != IntPtr.Zero && NativeMethods.IsWindow(newHwnd))
            {
                FileLogger.Info($"AutoLogin: window handle refreshed for PID {pid} (0x{currentHwnd:X} → 0x{newHwnd:X})");
                return newHwnd;
            }
        }
        catch (ArgumentException)
        {
            FileLogger.Warn($"AutoLogin: process {pid} exited during login sequence");
        }

        return IntPtr.Zero;
    }

    // ─── Foreground Flash + SendInput Helpers ──────────────────────
    //
    // EQ's login screen uses standard Windows message input (WM_CHAR/WM_KEYDOWN)
    // for text fields, NOT DirectInput. SendInput synthesizes keystrokes at the
    // kernel level — works with any input method, but requires foreground focus.
    // We briefly flash each EQ window to the front during its typing phase.

    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    /// <summary>
    /// Send a single key down or up event via SendInput.
    /// </summary>
    private static void SendKeyEvent(ushort vk, bool down)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC),
                    dwFlags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        NativeMethods.SendInput(1, [input], InputSize);
    }

    /// <summary>
    /// Press and release a single key via SendInput.
    /// </summary>
    private static void SendPressKey(ushort vk)
    {
        SendKeyEvent(vk, true);
        Thread.Sleep(60);
        SendKeyEvent(vk, false);
        Thread.Sleep(60);
    }

    /// <summary>
    /// Type a string via SendInput. Handles Shift for uppercase and symbols.
    /// </summary>
    private static void SendTypeString(string text)
    {
        foreach (char c in text)
        {
            short vkScan = NativeMethods.VkKeyScanW(c);
            if (vkScan == -1) continue; // unmappable character

            byte modifiers = (byte)(vkScan >> 8);
            if ((modifiers & ~0x01) != 0) continue; // skip chars needing Ctrl/Alt

            ushort vk = (ushort)(vkScan & 0xFF);
            bool needShift = (modifiers & 0x01) != 0;

            if (needShift)
                SendKeyEvent(0x10, true); // VK_SHIFT down
            SendKeyEvent(vk, true);
            Thread.Sleep(50);
            SendKeyEvent(vk, false);
            if (needShift)
                SendKeyEvent(0x10, false); // VK_SHIFT up
            Thread.Sleep(40);
        }
    }

    /// <summary>
    /// Bring a window to the foreground using the AttachThreadInput trick.
    /// Returns true if the window is now the foreground window.
    /// </summary>
    private static bool BringToForeground(IntPtr hwnd)
    {
        var curForeground = NativeMethods.GetForegroundWindow();
        if (curForeground == hwnd) return true;

        var foreThread = NativeMethods.GetWindowThreadProcessId(curForeground, out _);
        var curThread = NativeMethods.GetCurrentThreadId();
        bool attached = false;

        // Attach to the foreground thread so Windows allows us to steal focus
        if (foreThread != curThread)
            attached = NativeMethods.AttachThreadInput(curThread, foreThread, true);

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);

        if (attached)
            NativeMethods.AttachThreadInput(curThread, foreThread, false);

        // Verify focus arrived
        Thread.Sleep(100);
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// Restore a previously saved foreground window. Best-effort — if the
    /// window was closed in the meantime, this is a no-op.
    /// </summary>
    private static void RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
        NativeMethods.SetForegroundWindow(hwnd);
    }

    // ─── EQ INI Helpers ──────────────────────────────────────────────

    /// <summary>
    /// Write LastServerName to all eqlsPlayerData*.ini files so the server
    /// is pre-selected on the server select screen.
    /// </summary>
    private void WriteServerToIni(string server)
    {
        if (string.IsNullOrEmpty(server)) return;

        try
        {
            var files = Directory.GetFiles(_config.EQPath, "eqlsPlayerData*.ini");
            foreach (var file in files)
            {
                WriteIniValue(file, "MISC", "LastServerName", server);
                FileLogger.Info($"AutoLogin: wrote LastServerName={server} to {Path.GetFileName(file)}");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"AutoLogin: failed to write server to INI: {ex.Message}");
        }
    }

    /// <summary>
    /// Simple INI writer — finds [section] and updates key=value, or appends.
    /// EQ INI files use ANSI encoding.
    /// </summary>
    private static void WriteIniValue(string path, string section, string key, string value)
    {
        var lines = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path, Encoding.Default))
            : new List<string>();

        string sectionHeader = $"[{section}]";
        int sectionIdx = -1;
        int keyIdx = -1;
        int nextSectionIdx = lines.Count;

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                sectionIdx = i;
                continue;
            }
            if (sectionIdx >= 0 && trimmed.StartsWith('['))
            {
                nextSectionIdx = i;
                break;
            }
            if (sectionIdx >= 0 && trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                keyIdx = i;
            }
        }

        string entry = $"{key}={value}";
        if (keyIdx >= 0)
        {
            lines[keyIdx] = entry;
        }
        else if (sectionIdx >= 0)
        {
            lines.Insert(sectionIdx + 1, entry);
        }
        else
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add(sectionHeader);
            lines.Add(entry);
        }

        File.WriteAllLines(path, lines, Encoding.Default);
    }
}
