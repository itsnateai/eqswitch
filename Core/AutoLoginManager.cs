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
    /// <summary>
    /// Per-burst typing-validation result. <see cref="CombinedTypeString"/> returns
    /// this so callers can detect when characters were dropped due to keyboard-layout
    /// incompatibility (Skipped) or when the typing loop exited mid-flight (Typed +
    /// Skipped != Expected — should never happen barring exceptions, defensive).
    /// </summary>
    private readonly record struct TypingResult(int Typed, int Skipped, int Expected)
    {
        /// <summary>True iff every input character was either typed or explicitly skipped.</summary>
        public bool IsComplete => Typed + Skipped == Expected;
        /// <summary>True iff at least one character was layout-skipped (user-action item).</summary>
        public bool HasLayoutSkips => Skipped > 0;
    }

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

    // Dedup set for "process N exited during login sequence" warnings.
    // Why: callers that don't break on RefreshHandle returning IntPtr.Zero
    // (e.g. the MQ2-bridge readiness loop) will re-enter RefreshHandle every
    // 500ms, flooding the log with one identical warn line per tick. Run 1
    // of the 2026-04-25 dual-box log produced 60+ duplicate lines for a
    // single dead PID. TryAdd makes the warn fire once per (PID, lifetime).
    private static readonly ConcurrentDictionary<int, byte> _loggedExitPids = new();

    // Iter-3 fix-round-2 (2026-05-17 verifier T2-Opus C3 + T2-Sonnet C2):
    // Cross-PID arbitration for the PulseKey3D writer.Activate sequence in
    // StepEnteringWorld. KeyInputWriter mappings are per-PID, but the focus-
    // faking IAT hooks in eqswitch-di8.cpp are process-global per-EQ-process.
    // If two state-machine tasks reach PulseKey3D simultaneously (both PIDs
    // on Dalaya skip path) their Activate→Sleep→PulseKey3D→Sleep→Deactivate
    // sequences interleave — one PID's Deactivate clears the focus-fake hook
    // while the other is mid-keystroke, landing keystrokes in the wrong
    // window. Gate the whole 1.5s sequence with this static SemaphoreSlim so
    // only ONE PID owns focus-faking at a time. Legacy RunLoginSequence
    // (line 2477+) has the same race; the SM path is the deployed surface
    // so the legacy race is unmitigated by design (Iter-4 backport plan
    // is permanently retired per the project-final-release closeout).
    private static readonly System.Threading.SemaphoreSlim _focusFakeMutex = new(1, 1);

    // Logins run concurrently (one Task per BeginLogin call, no global gate).
    // Focus-faking is kept to brief windows (activate → type → deactivate) so
    // overlapping logins on different PIDs don't fight over foreground state.
    // An older queueing implementation existed but was removed; the prior XML
    // doc-comment claiming "serializes login sequences" was stale and was
    // dropped in v3.15.2 to match runtime behavior.

    /// <summary>UI thread sync context for marshaling events. Captured at construction
    /// as a best-effort fallback, then replaced by TrayManager.Initialize() via
    /// <see cref="SetUiContext"/> AFTER NotifyIcon creation guarantees the WinForms
    /// sync context is installed on this thread. Without the late-bind, events fired
    /// from background login threads (LoginStarting / LoginCredentialsSent / LoginComplete /
    /// StatusUpdate) fall into the synchronous-fire branch and race UI-thread state in
    /// the subscribers (notably TrayManager._injectedPids HashSet).</summary>
    private SynchronizationContext? _syncContext;

    /// <summary>Callback to enforce eqclient.ini overrides before launch (injected to avoid Core→UI dependency).</summary>
    private readonly Action<AppConfig>? _enforceOverrides;

    public event EventHandler<string>? StatusUpdate;

    /// <summary>Fires just before the login sequence starts (use to pause guard timers).</summary>
    public event EventHandler<int>? LoginStarting;

    /// <summary>Fires after credential submission is dispatched to EQ. Timing
    /// depends on path:
    /// <list type="bullet">
    /// <item>Legacy <see cref="RunLoginSequence"/>: after BURST 1 keystrokes
    /// finished and Enter was submitted (~T+7s).</item>
    /// <item>State-machine (v3.22.0+, <c>useStateMachine=true</c>): right after
    /// SHM <c>SendLoginCommand</c> returns success (~T+0s — Native commits
    /// credentials via Combo G + ClickButton asynchronously after this).</item>
    /// </list>
    /// Both paths fire BEFORE the login server response is observed — auth may
    /// still fail downstream. Earlier than <see cref="LoginComplete"/>
    /// (~T+30-60s) so subscribers can resume cosmetic work that was deferred
    /// during login (slim-titlebar guard, hook config refresh) without waiting
    /// for the full charselect-ready signal. Cosmetic ONLY — never generate
    /// input or steal focus from this handler, and the work must be
    /// no-op-safe against a subsequently-failed login. Current subscriber
    /// (<c>TrayManager.ApplyDeferredCosmetics</c>) applies slim-titlebar +
    /// hook-config refresh only; window-title is wired on <c>ClientDiscovered</c>,
    /// not on this event.</summary>
    public event EventHandler<int>? LoginCredentialsSent;

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
        // Best-effort capture — may be null if constructed before WinForms is up
        // (the typical Program.cs path). TrayManager.Initialize() calls SetUiContext
        // post-NotifyIcon to install the real WindowsFormsSynchronizationContext.
        _syncContext = SynchronizationContext.Current;
        _enforceOverrides = enforceOverrides;
    }

    /// <summary>Install the WinForms UI sync context. TrayManager calls this from
    /// Initialize() after NotifyIcon creation (which forces WinForms to install the
    /// sync context on the UI thread). Required for event handlers and StatusUpdate
    /// to marshal cleanly to the UI thread instead of running on background threads
    /// and racing UI-thread state.</summary>
    public void SetUiContext(SynchronizationContext context)
    {
        _syncContext = context ?? throw new ArgumentNullException(nameof(context));
        FileLogger.Info($"AutoLogin: UI sync context installed ({context.GetType().Name})");
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
        // v3.22.27 R2 (T2-Sonnet + T2-Opus convergent HIGH): LoginAndEnter-
        // World is called from FireTeam's Task.Run threadpool lambda
        // (TrayManager.cs:2628) — NOT necessarily the UI thread. Without
        // the lock, a concurrent UI-thread ApplySettings → ReloadConfigCore
        // swap of _config.Accounts can torn-read this lookup. Lock released
        // before any heavy work / await (the rest of the method body holds
        // no _config list iteration).
        var key = character.AccountKey;
        Account? account;
        lock (ConfigManager.ConfigMutationLock)
        {
            account = _config.Accounts.FirstOrDefault(key.Matches);
        }
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
    /// Phase 3+ will move Tray callers off this method; planned removal once
    /// TrayManager.FireAccountLogin is migrated to the intent-explicit API.
    /// </summary>
    [Obsolete("Use LoginToCharselect(Account) or LoginAndEnterWorld(Character) for intent-explicit routing. Slated for removal once TrayManager.FireAccountLogin is migrated.")]
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
            // Diagnostic: surface decrypt outcome (degenerate cases ONLY, NEVER the
            // value AND NEVER the length on success). Empty / suspiciously-short
            // here = silent config corruption from a prior Settings save (DPAPI
            // happily encrypts ""), which produces "Enter pressed on empty
            // password field" symptom. v3.15.2 (T3 Opus callout): the success
            // path no longer logs `length=N`. Even the password length is a
            // small information leak that aids brute-force ranging.
            if (string.IsNullOrEmpty(password))
                FileLogger.Warn($"AutoLogin: decrypted password for '{account.Name}' is EMPTY — config may be corrupted; re-enter password in Settings → Accounts");
            else if (password.Length < 2)
                FileLogger.Warn($"AutoLogin: decrypted password for '{account.Name}' is suspiciously short — re-check Settings → Accounts");
            else
                FileLogger.Info($"AutoLogin: decrypted password for '{account.Name}' OK");
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

        // v3.15.12 (2026-05-10): the WriteUsernameToIni(account.Username) call
        // that lived here was the team-launch regression. The race window is
        // wider than the original comment claimed:
        //   T=0:    BeginLogin1 writes INI=User1 → launches eqgame1
        //   T=3s:   BeginLogin2 writes INI=User2 (CLOBBERS) → launches eqgame2
        //   T=5.6s: eqgame1's eqmain.dll loads → reads INI → User2 ✗
        //   T=8.6s: eqgame2's eqmain.dll loads → reads INI → User2 ✓
        // The eqlsPlayerData*.ini is read by eqmain.dll at login-screen render
        // time (~T+5.6s per dinput8 log), NOT by eqgame.exe at startup. The
        // 3s LaunchDelayMs is much smaller than the 5.6s eqmain-load window,
        // so the second client's write reliably clobbers the first client's
        // pre-fill. Result: client 1 types correct password against wrong
        // pre-filled username → server rejects.
        //
        // Reverted call. The /login:USERNAME launch arg + BURST 1's Tab-and-type
        // username path (when account.UseLoginFlag=false) already cover username
        // delivery correctly per-PID. If the legacy INI pre-fill needs to land
        // for a specific account, the proper place is a per-PID write done
        // AFTER eqmain.dll loads (signaled via loginShm.gameState != 0) and
        // BEFORE BURST 1 fires — i.e., inside RunLoginSequence, not BeginLogin.
        // WriteUsernameToIni() helper kept defined for that future per-PID use.

        // Build launch args
        var exePath = Path.Combine(_config.EQPath, _config.Launch.ExeName);
        if (!File.Exists(exePath))
        {
            FileLogger.Error($"AutoLogin: exe not found at {exePath}");
            StatusUpdate?.Invoke(this, "Error: eqgame.exe not found");
            return Task.CompletedTask;
        }

        var args = _config.Launch.Arguments ?? string.Empty;
        // Skip the /login:USERNAME append if the user has already wired /login: into
        // their custom Launch.Arguments — eqgame's argv parser silently drops the
        // duplicate, but which one wins is undocumented; cleaner to never emit two.
        if (account.UseLoginFlag && !string.IsNullOrEmpty(account.Username) &&
            !args.Contains("/login:", StringComparison.OrdinalIgnoreCase))
            args = string.IsNullOrEmpty(args) ? $"/login:{account.Username}" : $"{args} /login:{account.Username}";

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

            FileLogger.Info($"AutoLogin: launching: {FileLogger.RedactLogin(commandLine.ToString())}");

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
        // v3.15.2 (T3-Opus security callout, refined by T3-Sonnet round 2):
        // try/finally drops the LOCAL alias to the password string when the
        // lambda exits. Note this is a soft fix — strings in .NET are
        // immutable, so we can't overwrite the bytes in-place, AND the closure-
        // captured copy in the lambda's compiler-synthesized closure object
        // remains until the lambda itself is GC'd. Net effect: still no
        // semantic guarantee of bounded password residence in heap; the only
        // structural fix is a SecureString or char[] migration. Kept as a
        // defense-in-depth nudge plus an explicit place to hang the comment
        // for the future migration. Concurrent BeginLogin calls each get
        // their own closure, so there is no cross-call race.
        var capturedAccount = account;
        var capturedCharacter = character;
        var capturedOverride = enterWorldOverride;
        var capturedPassword = password;
        // Opt-in routing to the state machine. AppConfig default is false; deployed
        // installs flip via per-install eqswitch-config.json. Snapshot the flag here
        // so a mid-flight Settings change (ReloadConfig) can't bait one PID into the
        // new path and another into the legacy path within the same team launch.
        bool useStateMachine = _config.Launch.UseStateMachine;
        return Task.Run(() =>
        {
            try
            {
                if (useStateMachine)
                    RunLoginStateMachine(pid, capturedAccount, capturedCharacter, capturedPassword, capturedOverride);
                else
                    RunLoginSequence(pid, capturedAccount, capturedCharacter, capturedPassword, capturedOverride);
            }
            finally
            {
                // ReSharper disable once RedundantAssignment — drops the strong reference
                // (we can't truly scrub a .NET string; this is best-effort GC eligibility).
                capturedPassword = string.Empty;
            }
        });
    }

    /// <summary>
    /// Enter the password into EQ's login screen and submit.
    ///
    /// Conceptually two phases bundled into one method (because they're tightly
    /// coupled by the load-bearing warmup contract — see chesterton-fence note
    /// at memory/feedback_chesterton_fence_load_bearing_bugs.md):
    ///
    ///   1. **Warmup ritual** via SHM LOGIN command. The DLL walks the
    ///      SidlManager widget tree to find the live password CEditWnd and
    ///      writes via Combo G's SetEditWndText. On Dalaya the write
    ///      silent-no-ops (wrong buffer — EQ renders/submits from a
    ///      different field), but the WIDGET DISCOVERY ACTIVITY warms up
    ///      EQ's input pump and gives DI8 cooperative-level negotiation
    ///      wall-clock to settle into BACKGROUND mode. Phase advances to
    ///      ClickingConnect (=3) ~3-5s after SendLoginCommand on Dalaya.
    ///
    ///   2. **BURST 1 keystrokes** — the actual workhorse. DI8 SendInput
    ///      types the password (+ Tab/Tab/username if not /login: flag)
    ///      then Enter to submit.
    ///
    /// Between (1) and (2) we dwell for WarmupDwellMs (default 4s) — the
    /// DLL keeps retrying ClickButton in the background during this dwell,
    /// providing additional widget-tree activity to keep EQ's pump warm.
    /// SendCancelCommand fires BEFORE BURST 1 (one mid-dev iteration tried
    /// AFTER and caused 4-of-6 char truncation — DLL's ClickButton retry loop
    /// contended with C# typing for EQ's message pump). Cancelling pre-Activate
    /// gives the DLL ~500ms to observe LOGIN_CMD_CANCEL on its next tick and
    /// stop polling, so BURST 1 types into a quiet pump.
    ///
    /// Total wall-clock from method entry to BURST 1 deactivate, ASSUMING
    /// gameState ready (DLL boot ~2s after EQ window appears, separate gate):
    ///   ~2-5s (phase advance) + WarmupDwellMs (~4s default) + ~0.7s (typing
    ///   at 25/15/15ms inter-key) = ~7-10s typical, vs ~21s in v3.11.3.
    /// Per-char typing went from ~130ms (v3.11.3) to ~40ms in v3.12.0 — looks
    /// paste-like at 60fps.
    /// </summary>
    private void RunCredentialEntry(int pid, IntPtr hwnd, KeyInputWriter writer,
        LoginShmWriter? loginShm, Account account, string password,
        int loginScreenDelayMs, int warmupDwellMs,
        string targetCharacterName = "",
        CharSelectReader? charSelect = null)
    {
        // v3.17.0 R3 fix: snapshot config values consumed BELOW. The R2 snapshot
        // at RunLoginSequence header doesn't propagate into this method, and
        // when called from the retry path during a 30s recovery sleep, a user-
        // initiated Settings change would race these reads. Caught by T2 verifier
        // pair (Sonnet+Opus convergent finding 2026-05-14). Snapshot here so
        // every call (primary + retry) gets a consistent view per invocation.
        bool skipNativeWarmup = _config.Launch.SkipNativeWarmup;
        int burst1ActivationSettleMs = _config.Launch.Burst1ActivationSettleMs;
        int burst1PostSubmitMs = _config.Launch.Burst1PostSubmitMs;

        // ── Phase 1: SHM warmup ritual ──
        bool warmupRan = false;
        // v3.15.12 (2026-05-10): empirical Dalaya regression fix. The warmup
        // ritual triggers the DLL's LoginStateMachine to do a 5-7s heap-walk
        // PER iteration (HeapScanForWidget + LIVE-WIDGET HEAP ENUM 259 pages
        // + HEAP CROSS-REF + TranslateDefToLive 523 nodes ×2) ON THE EQ GAME
        // THREAD via the GiveTime detour. EQ's input pump is blocked during
        // scans → BURST 1 keystrokes get dropped/coalesced (4 of 7 chars
        // landing observed). The pre-BURST CANCEL only halts the NEXT
        // iteration; in-flight scans still block the pump.
        //
        // Bypassing the warmup matches the v3.4.x baseline DLL behavior
        // (verified 2026-05-10 via temporary baseline-DLL deploy: BURST 1
        // typed 7/7 chars cleanly, both clients reached charselect, gotquiz1
        // logged in). The "warm-up the input pump" justification is obsolete
        // on Dalaya — the heavier widget discovery in eqmain_widgets +
        // eqmain_widgets_mq2style (+900 lines since 04/24) made it net-
        // negative.
        //
        // MQ2 parity: MQ2 (StateMachine.cpp:271-273) uses SetEditWndText for
        // direct widget write — elegant on retail but writes to wrong buffer
        // on Dalaya per CHANGELOG. Our BURST 1 keystroke path is the
        // Dalaya-correct fallback; the warmup ritual that tries (and fails)
        // the Combo G structural write is pure overhead AND breaks BURST 1.
        if (loginShm != null && !skipNativeWarmup)
        {
            Report($"{account.Name}: warmup...");
            // Track B v3 (2026-05-05): pass target character name into LoginShm so the
            // bridge's anchor-scan path (mq2_bridge.cpp HeapScanForTargetName) can find
            // single-char accounts via name-anchor instead of failing the threshold-5
            // full-array heap scan. Empty string preserves the legacy "no target" mode
            // for char-select-only flows (no enter-world).
            if (loginShm.SendLoginCommand(pid, account.Username, password, account.Server, targetCharacterName ?? ""))
            {
                // Wait for phase >= ClickingConnect (=3) — proves login-screen
                // widgets are discoverable AND Combo G actually wrote a CXStr
                // (Fix 2's read-back guard would have left phase at Error if
                // the write went to a stale ptr). Empirically advances in
                // ~3-5s on Dalaya.
                //
                // We deliberately do NOT wait for WaitConnectResponse — the
                // DLL's PHASE_CLICKING_CONNECT loop tries MQ2Bridge::ClickButton
                // on 'LOGIN_ConnectButton' which returns a CXMLDataPtr def
                // (Fix 1's IsEQMainButtonWidget rejects it), then retries for
                // ~25s before SetError. We don't need that signal — C# fires
                // BURST 1 instead.
                const int phaseCeilingMs = 12000;
                var phaseSw = System.Diagnostics.Stopwatch.StartNew();
                while (phaseSw.ElapsedMilliseconds < phaseCeilingMs)
                {
                    var phase = loginShm.ReadPhase(pid);
                    if (phase >= LoginPhase.ClickingConnect && phase != LoginPhase.Error)
                    {
                        warmupRan = true;
                        FileLogger.Info($"AutoLogin: warmup phase advanced to {phase} after {phaseSw.ElapsedMilliseconds}ms (PID {pid})");
                        break;
                    }
                    if (phase == LoginPhase.Error)
                    {
                        var err = loginShm.ReadError(pid);
                        FileLogger.Warn($"AutoLogin: warmup phase errored ({err}) — falling back to flat sleep");
                        break;
                    }
                    Thread.Sleep(50);
                }
                if (!warmupRan && phaseSw.ElapsedMilliseconds >= phaseCeilingMs)
                {
                    FileLogger.Warn($"AutoLogin: warmup phase didn't advance to ClickingConnect within {phaseCeilingMs}ms — falling back to flat sleep");
                }
            }
            else
            {
                FileLogger.Warn($"AutoLogin: SendLoginCommand failed for PID {pid} — falling back to flat sleep");
            }
        }

        // ── Dwell: DI8 cooperative-level settle window ──
        // When warmup ran, dwell for WarmupDwellMs (default 4s) — DLL retries
        // ClickButton in the background during this period. When warmup didn't
        // run, fall back to LoginScreenDelayMs (default 5s) without DLL activity.
        int dwellMs = warmupRan ? warmupDwellMs : loginScreenDelayMs;
        if (dwellMs > 0)
        {
            Report(warmupRan ? "Warmup done — settling..." : "Waiting for login screen...");
            Thread.Sleep(dwellMs);
        }

        // ── Pre-BURST cancel: silence the DLL before typing ──
        // The DLL's PHASE_CLICKING_CONNECT loop polls MQ2Bridge::ClickButton
        // every ~500ms on EQ's game thread, doing SEH-wrapped widget heap-
        // walks + WndNotification calls. If left running through BURST 1's
        // typing window, those heap-walks contend with EQ's message pump and
        // cause keystroke truncation (verified 2026-04-25 dual-box: 4-of-6
        // chars on client 1, 0-of-6 on client 2). Cancelling BEFORE Activate
        // gives the DLL ~500ms to observe LOGIN_CMD_CANCEL on its next tick
        // and stop polling, so BURST 1 types into a quiet pump.
        if (loginShm != null && warmupRan)
        {
            loginShm.SendCancelCommand(pid);
            FileLogger.Info($"AutoLogin: warmup cancel sent pre-BURST 1 for PID {pid}");
        }

        // ── Phase 2: BURST 1 keystrokes ──
        // v3.20.0 (2026-05-15) REVERTED Fix 1's typing-skip gate. The
        // 2026-05-15 PM smoke (PIDs 20836, 36156) showed the visible
        // password field stayed EMPTY when BURST 1 skipped typing — the
        // user's eyes are ground truth ("neither character entered any
        // password at all"), and the DLL log confirmed the failure mode:
        // `FindChildByName('connect','LOGIN_PasswordEdit')` returns NULL
        // on current Dalaya, so the structural password lookup falls back
        // to a hardcoded XMLIndex=0x00220001 → manager-walk match. Combo G
        // writes 7 bytes to that widget's +0x1A8 and the read-back confirms,
        // but the widget is NOT the visible password edit. The read-back
        // is verification too close to the action.
        //
        // This is precisely the failure mode the v3.15.13 commit message
        // documented: "revert v3.15.12 BURST 1 gate — Combo G doesn't reach
        // EQ submit buffer". Today's Fix 1 was v3.15.12 redux with an SHM
        // flag, and re-discovered the same bug. v3.16.0's ScreenMode=3
        // swap was supposed to close it, but on Dalaya the wrong-widget
        // problem makes the swap moot — we write the right BYTES to the
        // wrong WIDGET.
        //
        // BURST 1 keystrokes are the proven safety net: per the doc kicked
        // off this session ("BURST 1 KEYSTROKE fallback is what actually
        // carries the load"). Force BURST 1 to ALWAYS type — the native
        // side still does Combo G (logged in DLL for diagnostic) but C#
        // ignores the comboGWriteOk signal. The flag remains in SHM for
        // future use if a working structural write path is found (i.e.,
        // when FindChildByName for the password widget actually succeeds
        // v3.20.3 (2026-05-15) RE-ENGAGED the Fix 1 gate. Combo G's widget
        // lookup is now fixed via FindEmptyEditGlobal's proximity-to-username
        // heuristic (Native/eqmain_widgets_mq2style.cpp): the 17:56 smoke
        // confirmed Combo G writes to the widget exactly 0x5D0 below the
        // username-bearing CEditWnd, which is the visible password edit
        // (allocated adjacent to username in the SIDL screen's allocation
        // cluster). With Combo G now hitting the right field, the original
        // double-write bug (v3.15.13: Combo G + BURST 1 both typing → ~14
        // chars in field → login server rejection) is back unless BURST 1's
        // typing is suppressed. The Fix 1 gate does that — and now that
        // Combo G is reliable, the gate is finally correct.
        //
        // BURST 1 still fires Activate + Enter (submit) when this flag is
        // true — Enter is what EQ treats as "click Connect" with the
        // password field focused. Only the primer Backspace + retype is
        // skipped.
        bool comboGWritePassword = false;
        if (loginShm != null && warmupRan)
        {
            comboGWritePassword = loginShm.ReadComboGWriteOk(pid);
            if (comboGWritePassword)
            {
                FileLogger.Info($"AutoLogin: Combo G success signal observed for PID {pid} " +
                    "— skipping BURST 1 primer + password retype (proximity-heuristic widget, v3.20.3)");
            }
        }

        Report("Typing credentials...");
        writer.Activate(pid, suppress: true);
        // v3.15.2: tunable via Launch.Burst1ActivationSettleMs (default 500).
        Thread.Sleep(burst1ActivationSettleMs); // let DLL switch coop + blast activation
        FileLogger.Info($"AutoLogin: BURST 1 activated for PID {pid}");

        if (comboGWritePassword)
        {
            // Combo G already populated the field. Fire Enter only — that's
            // the equivalent of "click Connect" when the password edit has
            // focus. No primer (would Backspace one char from the password),
            // no retype (would double-write into the field).
            Report("Submitting login...");
            CombinedPressKey(writer, pid, hwnd, 0x0D); // Enter = submit
            Thread.Sleep(burst1PostSubmitMs);
            writer.Deactivate(pid);
            FileLogger.Info($"AutoLogin: BURST 1 deactivated for PID {pid} " +
                "(Combo G path: Activate + Enter only, no primer/typing)");
            return;
        }

        // ── PRIMER: absorb first-keystroke drop ──
        // EQ's GetDeviceData polling lags after the SHM-active flip — the FIRST
        // 1-3 keystrokes are dropped before EQ's input pump catches up. Pure
        // dwell-tuning (warmupDwellMs) only narrows this window; it doesn't close
        // it (verified 2026-05-04: dwell=4s drops 2 chars, dwell=8s drops 1 char,
        // dwell=12s pushes EQ into idle and drops ALL — peak is ~8s). Sending
        // Backspace first deterministically absorbs whatever EQ drops:
        //   - if EQ drops the primer (expected): no harm, password lands intact
        //   - if EQ catches it on empty field: no-op (Backspace on empty is no-op)
        // VK_BACK (0x08) chosen because it cannot insert a char or change focus
        // even if it lands and the field is somehow non-empty.
        //
        // ─── HISTORY (DO NOT REMOVE — informs the v3.19.1 gate above) ───
        // v3.15.13 (2026-05-10) REVERTED v3.15.12's `if (!warmupRan)` skip-typing
        // gate after a live-test regression: EMPTY field at submit because
        // Combo G's CXStr write at InputText+0x1A8 was NOT reaching EQ's
        // render/submit buffer. Ground truth: eqswitch.log [2026-05-10 09:22:07].
        //
        // v3.16.0 (2026-05-14) added the ScreenMode=3 swap during Combo G writes
        // (Native/eqmain_cxstr.cpp::WriteEditTextDirect) — the swap forces EQ
        // into "fullscreen-UI-input" mode for the duration of the assignment,
        // bypassing the input filter that was eating the structural write.
        // Read-back at +0x1A8 + verifier evidence confirms Combo G now reaches
        // the submit buffer. Hypothesis B-1 from the MQ2 RoF2-emu walkthrough
        // §3.2 closed.
        //
        // v3.19.1 (2026-05-15) RE-ENABLES the gate via the SHM v5 comboGWriteOk
        // signal (above, lines ~636-664). The v3.15.12 reversion's underlying
        // cause (write didn't propagate) was fixed by v3.16.0; the gate is
        // safe to re-instate. Critical difference from v3.15.12: the gate now
        // reads a SUCCESS SIGNAL from native (comboGWriteOk only set after
        // WriteEditTextDirect's read-back validation passes) instead of the
        // proxy `warmupRan` (which only proved the SHM ritual happened, not
        // that the write succeeded).
        //
        // If a future regression brings back the empty-field-at-submit symptom,
        // SUSPECT v3.16.0 ScreenMode swap regression FIRST (probe ScreenMode
        // RVA via _.eqswitch-re/probe_screenmode.py + verify swap fires) before
        // re-reverting the gate. The keystroke fallback below remains intact
        // for the non-Combo-G path (warmup didn't fire OR comboGWriteOk=0).
        CombinedPressKey(writer, pid, hwnd, 0x08);
        Thread.Sleep(50);

        // NOTE: a pre-flight Enter before typing is NOT idempotent on Dalaya
        // patchme — empty-password Enter raises a "you need to enter a username
        // and password" modal that steals focus from the password field, causing
        // BURST 1 to type into the modal and Enter to click OK instead of submit.
        // Tested + reverted 2026-04-24.

        if (!account.UseLoginFlag && !string.IsNullOrEmpty(account.Username))
        {
            CombinedPressKey(writer, pid, hwnd, 0x09); // Tab to username
            Thread.Sleep(100);
            var userResult = CombinedTypeString(writer, pid, hwnd, account.Username, charSelect);
            LogTypingValidation("BURST 1 username", userResult, pid);
            Thread.Sleep(100);
        }
        if (!account.UseLoginFlag)
        {
            CombinedPressKey(writer, pid, hwnd, 0x09); // Tab to password
            Thread.Sleep(100);
        }
        var passResult = CombinedTypeString(writer, pid, hwnd, password, charSelect);
        LogTypingValidation("BURST 1 password", passResult, pid);
        Thread.Sleep(100);

        Report("Submitting login...");
        CombinedPressKey(writer, pid, hwnd, 0x0D); // Enter = submit
        // v3.15.2: tunable via Launch.Burst1PostSubmitMs (default 500).
        Thread.Sleep(burst1PostSubmitMs);
        writer.Deactivate(pid);
        FileLogger.Info($"AutoLogin: BURST 1 deactivated for PID {pid}");
    }

    /// <summary>
    /// Tick-driven state machine dispatch (v3.22.0 design). Opt-in via
    /// <see cref="LaunchConfig.UseStateMachine"/>; deployed installs enable it
    /// via per-install <c>eqswitch-config.json</c>. Replaces the linear
    /// time-budgeted <see cref="RunLoginSequence"/> with a ~250ms-tick observer
    /// that reads the v3.21.0 widget probes + Native phase + gameState and
    /// transitions states on signal rather than sleep.
    ///
    /// Full state coverage as of v3.22.x: <c>WaitLoginScreen</c> →
    /// <c>TypingCredentials</c> → <c>WaitConnectResponse</c> →
    /// <c>ServerSelect</c> → <c>WaitServerLoad</c> → <c>CharSelect</c> →
    /// <c>EnteringWorld</c> → <c>Complete</c>. <c>StepServerSelect</c> and
    /// <c>StepWaitServerLoad</c> are DEAD-ON-DALAYA — see their summaries.
    ///
    /// Plan-doc archive: <c>X:/_Projects/_.claude/_comms/plan-eqswitch-v3.22.0.md</c>.
    /// </summary>
    private void RunLoginStateMachine(int pid, Account account, Character? character, string password, bool? enterWorldOverride)
    {
        // Locked decisions (plan-doc § Decisions, 2026-05-16):
        //   #1 Tick interval — 250ms (~15 Native probe ticks per C# tick).
        //   #2 gameState source — SHM exclusive via LoginShmWriter.ReadGameState.
        //   #3 Cancellation — three layers (top-of-tick check + token-aware delay +
        //      SendCancelCommand on Error/cancel exit so Native stops mid-command).
        // Add-on A: read nativePhase via SHM every tick (gates state transitions).
        // Add-on B: log widgetTickSeq deltas (5s threshold tuned through smoke).
        const int TickIntervalMs = 250;
        // 180_000ms overall budget covers login pipeline (~48s typical on Dalaya —
        // Combo G + LOGIN_ConnectButton click + connect response) + char-list warmup
        // (~5-15s — bridge heap-scan after pinst transition) + selection ack (~1-3s)
        // + zone-load (5-90s — Dalaya 3D scene hangs IsHungAppWindow during render)
        // + safety margin. An earlier 90s budget killed the smoke at WaitConnectResponse
        // before CharSelect transitioned in; 180s is the tuned ceiling. Don't grow
        // unbounded — promote-to-config rejected per the v3.22.10 final closeout
        // (more knobs Nate has to know exist + risk of misconfig breaking autologin).
        const int OverallTimeoutMs = 180_000;
        const int TickSeqStaleThresholdMs = 5_000;

        // Interaction warning: if Launch.SkipNativeWarmup=true (default), the DLL bypasses
        // its widget-discovery warmup loop after SendLoginCommand and may not advance
        // nativePhase past WaitLoginScreen on its own — RunCredentialEntry's BURST 1 in
        // the legacy path is what drives Native forward. The state machine doesn't fire
        // BURST 1 yet (Iter-2 territory), so with both flags on, the Iter-1 smoke is
        // expected to stall at WaitLoginScreen rather than reaching TypingCredentials.
        // For a meaningful Iter-1 smoke target, set SkipNativeWarmup=false in config so
        // Native's own warmup-completion advances nativePhase.
        FileLogger.Info($"AutoLogin-SM: starting state-machine dispatch for PID {pid} ({account.Name}, char='{character?.Name ?? string.Empty}', " +
            $"enterWorldOverride={enterWorldOverride?.ToString() ?? "null"}, SkipNativeWarmup={_config.Launch.SkipNativeWarmup})");

        using var cts = new CancellationTokenSource(OverallTimeoutMs);
        var sw = Stopwatch.StartNew();

        // v3.22.24 fix7 fast-fail tracker — PER-SM local (one per Task).
        // Earlier static-field design was REJECT'd by 4 of 8 verifier
        // agents: team1 launches both clients concurrently, each runs its
        // own RunLoginStateMachine Task; a shared static would (1) corrupt
        // streak state across SMs and (2) produce nonsense ages by mixing
        // different `sw` time origins. Passed by `ref` into
        // StepWaitConnectResponse so the dispatcher closure mutates the
        // same backing slot every tick. Dies with the SM — no cross-run
        // pollution surface.
        long okDialogFirstSeenMs = -1;

        LoginShmWriter? loginShm = null;
        // Iter-3 (2026-05-17): CharSelectReader for character selection + Enter
        // World RPC (the production-working path per EQSwitch CLAUDE.md AUTOLOGIN
        // SPEC). KeyInputWriter for the PulseKey3D fallback used on Dalaya where
        // CLW_EnterWorldButton isn't in the CXWnd tree at charselect-ready.
        // hwnd is the per-tick-refreshed EQ window handle — EQ recreates its
        // window on login→server→char-select transitions, so RefreshHandle is
        // called at the top of each StepCharSelect/StepEnteringWorld tick.
        CharSelectReader? charSelect = null;
        KeyInputWriter? writer = null;
        IntPtr hwnd = IntPtr.Zero;
        bool sentCancelOnExit = false;
        // v3.22.7/v3.22.8: LastLoginResult bookkeeping for the SM path.
        // Mirrors the legacy RunLoginSequence writes at lines 2685/2704.
        // Pre-v3.22.7 the SM path never wrote LastLoginResult, so on
        // deployments running UseStateMachine=true (the operationally-active
        // path — set via per-install eqswitch-config.json; the AppConfig
        // default remains false as a safety baseline) the Settings UI Flag
        // glyph stayed at whatever the prior session's value was. See
        // bug_eqswitch_login_complete_flag_stays_x.md.
        //
        // v3.22.8: regression-fix on v3.22.7's tick-time SaveImmediate.
        // v3.22.7 called ConfigManager.SaveImmediate from inside the
        // dispatch tick at the CharSelect transition — on multi-client
        // smoke, BOTH SMs hit that line within seconds of each other and
        // contended on _saveLock + backup rotation, stalling the tick
        // long enough that StepCharSelect failed to fire char-selection
        // → both clients stopped at char-select visible in EQ. The fix:
        // mark `pendingResult` in-memory only during the tick loop, and
        // defer the actual SaveImmediate to the SM's finally block where
        // each client's save is naturally desynchronized by its own
        // exit timing rather than the synchronized transition moment.
        bool okWritten = false;
        string? pendingResult = null;
        var current = LoginPhase.WaitLoginScreen;
        // tickSeq staleness gate uses an "observed at least once" sentinel rather than
        // gating on nonzero — the DLL can legitimately publish 0 indefinitely if its
        // probe loop never starts, and `lastTickSeq != 0` would skip the staleness
        // branch forever in that case (180s overall-timeout fallback instead of 5s
        // staleness catch). Verifier-flagged convergent fix 2026-05-16 (T2-S/T2-O/T3-O).
        bool tickSeqObserved = false;
        uint lastTickSeq = 0;
        long lastTickSeqAdvanceMs = 0;
        int tickCount = 0;
        // Path A diagnostic (Iter-1.5, 2026-05-16) — sentinels force a first-tick observation
        // log entry. The OBS log fires on ANY change to nativePhase / gameState / widget
        // visibility regardless of C# state transition, so during Iter-1's TypingCredentials
        // stub-stall it traces what Native does on its own. Gates Iter-2 thin-orchestrator
        // vs C#-driver decision. See plan-doc § "Iter-1 Smoke Results".
        LoginPhase lastObservedPhase = (LoginPhase)uint.MaxValue;  // sentinel: real phases are 0..99
        int lastObservedGameState = int.MinValue;
        string lastObservedWidgetSig = "__init__";

        try
        {
            loginShm = new LoginShmWriter();
            if (!loginShm.Open(pid))
            {
                FileLogger.Error($"AutoLogin-SM: LoginShmWriter.Open failed for PID {pid}");
                Report($"{account.Name}: SHM open failed");
                current = LoginPhase.Error;
                return;
            }

            // SetAutoLoginActive(true) prevents eqswitch-di8.cpp's pre-login kPromptWindows
            // dismiss machinery from concurrent widget-clicks while the state machine drives
            // the flow. Same pattern as RunLoginSequence (see SetAutoLoginActive XML doc).
            if (!loginShm.SetAutoLoginActive(pid, true))
                FileLogger.Warn($"AutoLogin-SM: SetAutoLoginActive(true) failed for PID {pid} — continuing");

            // Iter-3 (2026-05-17): open CharSelectReader + KeyInputWriter for the
            // Iter-3 StepCharSelect / StepEnteringWorld dispatch. Both must be open
            // before the dispatch loop reaches CharSelect; opened here at entry so
            // the lifecycle parallels loginShm (single try/finally pair). Matches
            // legacy RunLoginSequence lines 1274-1303 ordering.
            charSelect = new CharSelectReader();
            if (!charSelect.Open(pid))
            {
                FileLogger.Error($"AutoLogin-SM: CharSelectReader.Open failed for PID {pid}");
                Report($"{account.Name}: char-select SHM open failed");
                current = LoginPhase.Error;
                return;
            }

            writer = new KeyInputWriter();
            if (!writer.Open(pid))
            {
                FileLogger.Error($"AutoLogin-SM: KeyInputWriter.Open failed for PID {pid}");
                Report($"{account.Name}: key-input SHM open failed");
                current = LoginPhase.Error;
                return;
            }

            // Iter-3 fix-round-2 (verifier T3-Sonnet HIGH-3 + T3-Opus HIGH-2):
            // initialize hwnd at entry instead of leaving IntPtr.Zero until the
            // first RefreshHandle inside a Step method. RefreshHandle on Zero
            // works by-incident (IsWindow(Zero)==false → Process lookup) but is
            // fragile if the process died before our first Step tick. Look up
            // once at entry so subsequent ticks see a known-good handle.
            try
            {
                using var entryProc = System.Diagnostics.Process.GetProcessById(pid);
                hwnd = entryProc.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                    FileLogger.Warn($"AutoLogin-SM: entry hwnd lookup returned 0 for PID {pid} (window not created yet; RefreshHandle will retry per-tick)");
            }
            catch (ArgumentException)
            {
                FileLogger.Error($"AutoLogin-SM: PID {pid} not found at entry — process never started or exited before SM dispatch");
                Report($"{account.Name}: PID {pid} not found at entry");
                current = LoginPhase.Error;
                return;
            }

            // Iter-3 fix-round-2 (verifier T2-Opus REJECT C1 + T2-Sonnet C3 +
            // T3-Opus LOW-4 convergent): honor enterWorldOverride parameter.
            // Pre-fix the SM path ignored it — caller-supplied "stop at char
            // select" intent was silently overridden. Matches legacy
            // RunLoginSequence:2496 semantics.
            bool shouldEnterWorld = enterWorldOverride ?? (character != null);
            if (shouldEnterWorld && character == null)
            {
                FileLogger.Warn($"AutoLogin-SM: enterWorldOverride=true but character=null — downgrading to char-select-only (PID {pid})");
                shouldEnterWorld = false;
            }
            FileLogger.Info($"AutoLogin-SM: shouldEnterWorld={shouldEnterWorld} (enterWorldOverride={enterWorldOverride?.ToString() ?? "null"}, char={(character != null ? "set" : "null")}, PID {pid})");

            // Issue SendLoginCommand ONCE on entry. The state machine observes Native's
            // resulting phase progression rather than driving it tick-by-tick.
            if (!loginShm.SendLoginCommand(pid, account.Username, password, account.Server, character?.Name ?? string.Empty))
            {
                FileLogger.Error($"AutoLogin-SM: SendLoginCommand failed for PID {pid}");
                Report($"{account.Name}: SendLoginCommand failed");
                current = LoginPhase.Error;
                return;
            }

            // v3.22.0: fire LoginCredentialsSent so TrayManager subscribers
            // (slim-titlebar / hook-config / window-title) can apply at T+~0s on the
            // SM path — SendLoginCommand here is the SM-path equivalent of legacy's
            // post-BURST-1 credential commit (~T+7s; see fire site at line ~2014).
            // LoginComplete remains the idempotent end-of-sequence; both call into
            // TrayManager.ApplyDeferredCosmetics(pid).
            try
            {
                if (_syncContext != null)
                    _syncContext.Post(_ => LoginCredentialsSent?.Invoke(this, pid), null);
                else
                    LoginCredentialsSent?.Invoke(this, pid);
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"AutoLogin-SM: LoginCredentialsSent handler threw for PID {pid}: {ex.Message}");
            }

            // v3.22.17: init balloon removed per user feedback — "state machine" was
            // implementation jargon that users wouldn't understand. The subsequent
            // "reached char select" / "Entering world..." / "logged in!" balloons
            // already provide user-facing progress milestones with plain language.
            while (current != LoginPhase.Complete && current != LoginPhase.Error)
            {
                // Cancellation Layer 1 — explicit check at top of tick.
                if (cts.IsCancellationRequested)
                {
                    FileLogger.Warn($"AutoLogin-SM: cancellation/timeout at state {current} after {sw.ElapsedMilliseconds}ms (PID {pid})");
                    current = LoginPhase.Error;
                    break;
                }

                tickCount++;

                // SHM snapshot — read widgets + gameState + nativePhase per-tick (single source of truth).
                // Decision #2: gameState comes from SHM exclusively. tickSeq staleness (>5s) is the
                // dead-DLL safety net (Add-on B).
                if (!loginShm.TryReadWidgetState(pid, out var widgets))
                {
                    FileLogger.Warn($"AutoLogin-SM: TryReadWidgetState failed at state {current} after {sw.ElapsedMilliseconds}ms (PID {pid})");
                    current = LoginPhase.Error;
                    break;
                }
                int gameState = loginShm.ReadGameState(pid);
                LoginPhase nativePhase = loginShm.ReadPhase(pid);
                // v3.22.0 (2026-05-16): read OkDisplay snapshot per-tick so
                // StepWaitConnectResponse can detect Fatal classification (e.g.
                // "Invalid Password") and bail to Error rather than spin-wait until
                // the 180s overall timeout. Snapshot pattern (class+text torn-read
                // guarded) is cheaper than separate calls — see LoginShmWriter.cs.
                var (okClass, okText) = loginShm.ReadOkDisplaySnapshot(pid);

                // Staleness defense — only ARMS after the first NONZERO TickSeq observation.
                // Real-world DLL boot timing (Iter-1 round-2 smoke 2026-05-16): eqmain.dll
                // load + DLL game-thread tick + first PollWidgetVisibilityToShm runs takes
                // ~20s post-eqgame-launch. Until then, TickSeq=0 is "probe not started yet",
                // NOT "probe dead" — the overall 180s CTS timeout (OverallTimeoutMs) covers
                // the never-starts case. Staleness defense is specifically for the "probe
                // was alive, then died mid-flight" case, which it still catches because
                // lastTickSeqAdvanceMs gets updated on every TickSeq change after arming.
                if (widgets.TickSeq != 0 && !tickSeqObserved)
                {
                    tickSeqObserved = true;
                    lastTickSeq = widgets.TickSeq;
                    lastTickSeqAdvanceMs = sw.ElapsedMilliseconds;
                    FileLogger.Info($"AutoLogin-SM: first nonzero widgetTickSeq={widgets.TickSeq} observed at t={sw.ElapsedMilliseconds}ms — staleness defense armed (PID {pid})");
                }
                else if (tickSeqObserved && widgets.TickSeq != lastTickSeq)
                {
                    lastTickSeq = widgets.TickSeq;
                    lastTickSeqAdvanceMs = sw.ElapsedMilliseconds;
                }
                else if (tickSeqObserved && sw.ElapsedMilliseconds - lastTickSeqAdvanceMs > TickSeqStaleThresholdMs)
                {
                    FileLogger.Error($"AutoLogin-SM: widgetTickSeq stalled at {widgets.TickSeq} for >{TickSeqStaleThresholdMs}ms after arming — DLL probe died (PID {pid})");
                    current = LoginPhase.Error;
                    break;
                }

                // Path A observability — emit an OBS entry on any change to nativePhase,
                // gameState, or widget visibility (DiagSummary covers connect/ss/ok/yn/cd + TickSeq).
                // Independent of the state-transition log below, so the Iter-1 stub-stall trace
                // captures Native's autonomous progression instead of just C# decisions.
                string widgetSig = widgets.DiagSummary();
                if (nativePhase != lastObservedPhase || gameState != lastObservedGameState || widgetSig != lastObservedWidgetSig)
                {
                    FileLogger.Info($"AutoLogin-SM-OBS: tick={tickCount} t={sw.ElapsedMilliseconds}ms cstate={current} nativePhase={nativePhase} gameState={gameState} {widgetSig} (PID {pid})");
                    lastObservedPhase = nativePhase;
                    lastObservedGameState = gameState;
                    lastObservedWidgetSig = widgetSig;
                }

                // Per-tick dispatch covers all phases (WaitLoginScreen through Complete).
                // StepCharSelect is the minimal-escalation stub (Native Error propagation
                // only) per verifier-round convergent finding (T2-Sonnet + T2-Opus
                // 2026-05-16): an earlier `current` self-loop made CharSelect an absorbing
                // state with no Error escape — fixed in the same round.
                // Iter-3 (2026-05-17): CharSelect + EnteringWorld dispatched outside the
                // switch — they need `ref IntPtr hwnd` (RefreshHandle rewrites it on
                // login→server→char-select window-recreation transitions), and switch
                // expressions don't pass ref. Both methods are INSTANCE (not static) since
                // they call ShouldSkipShmEnterWorld which uses _config, and they marshal
                // the per-PID context (charSelect/writer/account/character/hwnd) that the
                // observation-only Iter-2B Step methods don't need.
                // Iter-3 fix-round-2 (verifier T2-Opus REJECT C2): cts.Token threaded
                // into both Steps so their inner Thread.Sleep loops can break out on
                // cancellation/timeout instead of running their full ~30s+90s budgets
                // after the OverallTimeoutMs fires.
                LoginPhase next;
                if (current == LoginPhase.CharSelect)
                {
                    next = StepCharSelect(charSelect!, account, character, ref hwnd, pid, nativePhase, shouldEnterWorld, cts.Token);
                }
                else if (current == LoginPhase.EnteringWorld)
                {
                    next = StepEnteringWorld(charSelect!, writer!, account, ref hwnd, pid, cts.Token);
                }
                else
                {
                    next = current switch
                    {
                        LoginPhase.WaitLoginScreen     => StepWaitLoginScreen(widgets, gameState, nativePhase),
                        LoginPhase.TypingCredentials   => StepTypingCredentials(widgets, gameState, nativePhase),
                        LoginPhase.ClickingConnect     => StepClickingConnect(widgets, gameState, nativePhase),
                        LoginPhase.WaitConnectResponse => StepWaitConnectResponse(widgets, gameState, nativePhase, okClass, okText, sw.ElapsedMilliseconds, ref okDialogFirstSeenMs),
                        LoginPhase.ServerSelect        => StepServerSelect(widgets, gameState, nativePhase),
                        LoginPhase.WaitServerLoad      => StepWaitServerLoad(widgets, gameState, nativePhase),
                        _                              => current
                    };
                }

                if (next != current)
                {
                    // Phase-skip detection — StepWaitLoginScreen uses `nativePhase >= TypingCredentials`
                    // which silently swallows ServerSelect/WaitConnectResponse/etc into the
                    // TypingCredentials Iter-1 stub-stall. Log when Native is already ahead of where
                    // the C# transition lands; smoke output makes the desync loudly visible.
                    bool nativeAhead = next == LoginPhase.TypingCredentials && nativePhase > LoginPhase.TypingCredentials;
                    string aheadTag = nativeAhead ? $" [native-ahead: nativePhase={nativePhase}, C# stalling at {next}]" : string.Empty;
                    FileLogger.Info($"AutoLogin-SM: {current} → {next} (tick={tickCount}, t={sw.ElapsedMilliseconds}ms, " +
                        $"{widgets.DiagSummary()}, gameState={gameState}, nativePhase={nativePhase}){aheadTag}");
                    // v3.22.8: mark pendingResult in-memory only — actual write
                    // + SaveImmediate happens in the finally block at SM exit
                    // (avoids tick-time disk I/O + lock contention with sibling
                    // SMs hitting this same line at the synchronized CharSelect
                    // transition moment). The "first transition into CharSelect
                    // is the password-accepted boundary" semantic is preserved
                    // — okWritten latches once and prevents downgrade. See SM-
                    // entry comment for the v3.22.7→v3.22.8 regression history.
                    if (next == LoginPhase.CharSelect && !okWritten)
                    {
                        pendingResult = "ok";
                        okWritten = true;
                        FileLogger.Info($"AutoLogin-SM: marked LastLoginResult=ok pending SM-exit save for {account.Name} (charselect reached at t={sw.ElapsedMilliseconds}ms)");
                    }
                    current = next;
                }

                // Cancellation Layer 2 — token-aware delay so the inter-tick gap honors cancellation.
                try
                {
                    Task.Delay(TickIntervalMs, cts.Token).Wait(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    FileLogger.Warn($"AutoLogin-SM: cancelled during tick delay at state {current} (PID {pid})");
                    current = LoginPhase.Error;
                    break;
                }
                catch (AggregateException aex) when (aex.InnerException is OperationCanceledException)
                {
                    FileLogger.Warn($"AutoLogin-SM: cancelled (Aggregate) during tick delay at state {current} (PID {pid})");
                    current = LoginPhase.Error;
                    break;
                }
            }

            FileLogger.Info($"AutoLogin-SM: terminal state {current} after {sw.ElapsedMilliseconds}ms, {tickCount} ticks (PID {pid})");

            if (current == LoginPhase.Error)
            {
                // v3.22.11: dump okDisplay snapshot at terminal-Error entry. Closes
                // the diagnostic gap surfaced by the 2026-05-17 17:39:48 smoke event
                // (gotquiz1 stuck 138s in WaitConnectResponse → terminal Error with
                // no native log capture). Without this read, the cause class
                // (bad-password / server-queue / login-token-stale / SM bug) is
                // unknowable from the C# log alone. Snapshot may be empty if EQ
                // never published an OK dialog (pure connect timeout / link drop).
                try
                {
                    var okAtError = loginShm?.ReadOkDisplaySnapshot(pid) ?? (OkDisplayClass.None, "");
                    FileLogger.Info($"AutoLogin-SM: okDisplay at terminal Error: class={okAtError.Class} text=\"{okAtError.Text}\" (PID {pid})");
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"AutoLogin-SM: failed to read okDisplay at terminal Error (PID {pid}): {ex.Message}");
                }

                // Cancellation Layer 3 — tell Native to stop in case it's mid-command.
                // Defense against the "ghost typing" failure mode where C# bails but
                // Native's keystroke queue keeps firing into a focused field.
                try
                {
                    // v3.22.12: null-conditional mirrors the v3.22.11 okDisplay
                    // read three lines up. Safe by control-flow invariant
                    // (line 911-918 early-returns on writer.Open failure,
                    // bypassing the loop entirely — by the time we reach this
                    // terminal-Error block, loginShm cannot be null), but
                    // explicit ?. removes the fragility-by-future-change concern
                    // T3 Sonnet flagged.
                    //
                    // INVARIANT (T3 Sonnet+Opus re-verify 2026-05-17): if
                    // loginShm is somehow null here, the ?. silently no-ops
                    // and sentCancelOnExit still flips true — finally block
                    // will then skip the redundant cancel attempt. This is
                    // intentional: sentCancelOnExit tracks "we attempted
                    // cancel on this exit path", not "cancel was received by
                    // Native." Per reference_loud_runtime_silent_rest, if
                    // loginShm being null violates the control-flow
                    // invariant, the LOUD signal is the lack of any prior
                    // log lines for this PID — not this specific call site.
                    loginShm?.SendCancelCommand(pid);
                    sentCancelOnExit = true;
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"AutoLogin-SM: SendCancelCommand on Error exit failed: {ex.Message}");
                }
                // v3.22.8: mark pendingResult in-memory only — actual write
                // happens in finally block. See SM-entry comment for v3.22.7→
                // v3.22.8 rationale. Skip if okWritten (downgrade-protect:
                // post-charselect failure is plumbing, not login). Skip if EQ
                // process is gone (crash / kill not the password's fault, per
                // RunLoginSequence:2673-2676 risk register).
                if (!okWritten)
                {
                    bool eqAlive = false;
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(pid);
                        eqAlive = !proc.HasExited;
                    }
                    catch (ArgumentException) { /* process already gone */ }
                    catch (InvalidOperationException) { /* race between GetProcessById and HasExited */ }
                    if (eqAlive)
                    {
                        pendingResult = "fail";
                        FileLogger.Info($"AutoLogin-SM: marked LastLoginResult=fail pending SM-exit save for {account.Name} (terminal Error, PID alive)");
                    }
                    else
                    {
                        FileLogger.Info($"AutoLogin-SM: skipped LastLoginResult=fail for {account.Name} — EQ process gone (not the password's fault)");
                    }
                }
                Report($"{account.Name}: autologin failed");
            }
            // v3.22.17: complete balloon removed per user feedback — the "logged
            // in!" balloon at the Enter-World success site (line ~1972) already
            // marks completion in plain language. Saying "state machine completed"
            // immediately after was redundant + implementation jargon.
        }
        catch (Exception ex)
        {
            FileLogger.Error($"AutoLogin-SM: unhandled exception at state {current} after {sw.ElapsedMilliseconds}ms (PID {pid})", ex);
            Report($"{account.Name}: autologin error: {ex.Message}");
            if (loginShm != null && !sentCancelOnExit)
            {
                // Parity with the Error-exit-path catch (line ~970) — log so a double-failure
                // is visible in the log rather than silently swallowed. T3-S/T3-O flagged the
                // inconsistency.
                try { loginShm.SendCancelCommand(pid); }
                catch (Exception cex) { FileLogger.Warn($"AutoLogin-SM: SendCancelCommand on exception-path exit failed: {cex.Message}"); }
            }
        }
        finally
        {
            // v3.22.8: deferred LastLoginResult SaveImmediate. v3.22.7 fired this
            // INSIDE the dispatch tick at the CharSelect transition, which on
            // multi-client smoke contended on _saveLock + backup rotation
            // simultaneously across both SMs and stalled StepCharSelect →
            // regression where both clients stopped at char-select. Moving the
            // disk write here naturally desynchronizes the saves (each SM exits
            // at its own time, not its own CharSelect-transition time) AND
            // removes disk I/O from the tick path entirely. Try/catch so a save
            // failure can't break the rest of the finally chain (loginShm
            // Dispose, FireLoginComplete, etc.).
            if (pendingResult != null)
            {
                // v3.22.25: serialize the entire re-resolve → write → SaveImmediate
                // sequence against TrayManager.ReloadConfig (which swaps _config.Accounts
                // wholesale). Canonical Monitor.Enter(lock, ref tookLock) pattern —
                // if Enter throws, tookLock stays false and the finally branch
                // does NOT call Exit on a never-acquired lock (which would itself
                // throw SynchronizationLockException). The diagnostic log for
                // "save deferred — Settings dialog open" fires when TryEnter(0)
                // returns false BEFORE we promote to the blocking Enter call.
                // See ConfigManager.ConfigMutationLock for the full contract.
                bool tookLock = false;
                try
                {
                    if (!Monitor.TryEnter(ConfigManager.ConfigMutationLock, 0))
                    {
                        FileLogger.Info($"AutoLogin-SM: save deferred — Settings dialog ApplySettings/ReloadConfig in progress; waiting for ConfigMutationLock for {account.Name}");
                        Monitor.Enter(ConfigManager.ConfigMutationLock, ref tookLock);
                    }
                    else
                    {
                        tookLock = true;
                    }

                    // Re-resolve to the live Account in _config.Accounts by
                    // (Username, Server). SettingsForm.ApplySettings replaces
                    // _config.Accounts with a list of new Account instances on
                    // every save — pre-v3.22.25 the captured `account` ref
                    // could become an orphan mid-save. The ConfigMutationLock
                    // above now ensures ApplySettings/ReloadConfig either
                    // finished before we entered (fresh re-resolve sees the
                    // new list) or has not yet run (we hold the lock so its
                    // swap waits). Re-resolve still required because we may
                    // have entered AFTER ApplySettings ran.
                    // Match key matches SettingsForm.OnLoginComplete's
                    // reverse-sync (Username + Server, case-insensitive).
                    // Fallback to `account` covers the case where the
                    // account was renamed or removed in Settings during
                    // autologin — at least the orphan still gets the value,
                    // even if SaveImmediate then doesn't persist it.
                    // Snapshot `_config.Accounts` to a local for the contract
                    // symmetry — under the lock the field is stable, but
                    // keeping the snapshot makes the re-resolve self-contained.
                    var accounts = _config.Accounts;
                    var match = accounts.FirstOrDefault(a =>
                        string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(a.Server, account.Server, StringComparison.OrdinalIgnoreCase));
                    var live = match ?? account;
                    if (match == null)
                    {
                        // Null fallback path — account was renamed (Username or
                        // Server changed) or removed from Settings while this SM
                        // was running. The orphan write below lands somewhere
                        // outside _config.Accounts and SaveImmediate won't
                        // persist it. Warn loudly so the silent-failure surface
                        // is visible in logs even though renames are unfixable
                        // by the lock (the rename itself changed identity, so
                        // there is no "right" match).
                        FileLogger.Warn($"AutoLogin-SM: live Account for {account.Name} (Username='{account.Username}', Server='{account.Server}') not found in _config.Accounts — likely renamed or removed mid-autologin. LastLoginResult={pendingResult} will not persist this run.");
                    }
                    else if (!ReferenceEquals(live, account))
                    {
                        FileLogger.Info($"AutoLogin-SM: re-resolved orphan Account ref for {account.Name} (Settings saved mid-autologin)");
                    }
                    // Write order: LastLoginAt FIRST, LastLoginResult LAST.
                    // The invariant we want: a UI-thread reader observing a
                    // non-default LastLoginResult also sees the matching
                    // LastLoginAt. Plain field writes in C# can be reordered
                    // by the JIT (no language-level guarantee even on x64 with
                    // CLR memory model), so a Thread.MemoryBarrier() between
                    // the two writes is required to enforce store-store order.
                    // SaveImmediate bypasses ConfigManager.Save's Windows.Forms.Timer
                    // which can't tick on background threads.
                    live.LastLoginAt = DateTime.UtcNow;
                    Thread.MemoryBarrier();
                    live.LastLoginResult = pendingResult;
                    // Mirror onto the SM-held orphan so any LoginComplete
                    // subscriber that holds the captured ref also sees the
                    // outcome. UI subscribers re-resolve via OnLoginComplete
                    // but defense-in-depth costs us two field writes. Same
                    // store-store barrier discipline as the live writes above.
                    if (!ReferenceEquals(live, account))
                    {
                        account.LastLoginAt = live.LastLoginAt;
                        Thread.MemoryBarrier();
                        account.LastLoginResult = live.LastLoginResult;
                    }
                    ConfigManager.SaveImmediate(_config);
                    FileLogger.Info($"AutoLogin-SM: deferred SaveImmediate({pendingResult}) for {account.Name} at SM-exit");
                }
                catch (Exception saveEx)
                {
                    FileLogger.Warn($"AutoLogin-SM: deferred SaveImmediate failed for {account.Name}: {saveEx.Message}");
                }
                finally
                {
                    // Guard against Monitor.Enter having thrown before tookLock was
                    // set true — Exit on a never-acquired lock throws SyncLockException
                    // which would mask the real exception in flight.
                    if (tookLock) Monitor.Exit(ConfigManager.ConfigMutationLock);
                }
            }
            // Iter-3 fix-round-2 (verifier T3-Opus HIGH-5): SendCancelCommand-on-Error
            // moved into finally so it fires for ALL Error exit paths — including the
            // early-return paths in entry initialization (loginShm.Open / charSelect.Open
            // / writer.Open / Process.GetProcessById failures). Pre-fix these paths set
            // `current = LoginPhase.Error; return;` and bypassed the post-loop Cancel
            // block, leaving Native running with a stale LoginCommand that would keep
            // trying to type credentials into a window we just abandoned. Catch handler
            // already has a similar guard for the exception path (sentCancelOnExit flag).
            if (loginShm != null && current == LoginPhase.Error && !sentCancelOnExit)
            {
                try { loginShm.SendCancelCommand(pid); sentCancelOnExit = true; }
                catch (Exception cex) { FileLogger.Warn($"AutoLogin-SM: SendCancelCommand on Error-from-finally failed: {cex.Message}"); }
            }
            // Iter-3 (2026-05-17): deactivate writer BEFORE closing SHMs — ensure
            // focus-faking stops while key-input SHM is still alive (writer.Deactivate
            // reads the SHM to clear active-flag). Mirrors legacy RunLoginSequence
            // finally ordering (line 2516-2521).
            if (writer != null)
            {
                try { writer.Deactivate(pid); }
                catch (Exception ex) { FileLogger.Warn($"AutoLogin-SM: writer.Deactivate failed: {ex.Message}"); }
                try { writer.Dispose(); }
                catch (Exception ex) { FileLogger.Warn($"AutoLogin-SM: writer.Dispose failed: {ex.Message}"); }
            }
            if (charSelect != null)
            {
                try { charSelect.Close(pid); }
                catch (Exception ex) { FileLogger.Warn($"AutoLogin-SM: charSelect.Close failed: {ex.Message}"); }
                try { charSelect.Dispose(); }
                catch (Exception ex) { FileLogger.Warn($"AutoLogin-SM: charSelect.Dispose failed: {ex.Message}"); }
            }
            if (loginShm != null)
            {
                // Nested try so Dispose ALWAYS runs even if SetAutoLoginActive (or its
                // catch handler — e.g. FileLogger during disk-full) throws. T2-O flagged
                // the prior ordering as a Dispose-skip risk.
                try
                {
                    try { loginShm.SetAutoLoginActive(pid, false); }
                    catch (Exception ex) { FileLogger.Warn($"AutoLogin-SM: SetAutoLoginActive(false) failed: {ex.Message}"); }
                }
                finally
                {
                    loginShm.Dispose();
                }
            }
            // Iter-3 fix-round-2 (verifier T3-Sonnet HIGH-2): TryRemove is now INSIDE
            // FireLoginComplete's Post lambda (atomic with LoginComplete invocation),
            // matching legacy RunLoginSequence:2963-2975. Don't call TryRemove here —
            // would re-introduce the race window where IsLoginActive=false but
            // LoginComplete hasn't fired yet.
            try { FireLoginComplete(pid); } catch (Exception ex) { FileLogger.Warn($"AutoLogin-SM: LoginComplete handler threw: {ex.Message}"); }
        }
    }

    /// <summary>
    /// WaitLoginScreen → TypingCredentials transition: fires once Native publishes
    /// PHASE_TYPING_CREDENTIALS (or later) AND the connect widget is visible. The
    /// double-gate prevents premature transition when Native phase advances ahead
    /// of the visible login screen (e.g. transient phase ticks during DLL init).
    /// Plan-doc table row: <c>LoginScreen → TypingCredentials on warmup-complete + cmd issued</c>.
    /// </summary>
    private static LoginPhase StepWaitLoginScreen(WidgetState widgets, int gameState, LoginPhase nativePhase)
    {
        // Native phase advanced past WaitLoginScreen means SendLoginCommand has been
        // observed and Combo G warmup has started. Error phase is a terminal Native
        // failure — propagate to C# Error so the outer loop's cleanup runs.
        if (nativePhase == LoginPhase.Error)
            return LoginPhase.Error;

        if (nativePhase >= LoginPhase.TypingCredentials && widgets.ConnectVisible)
            return LoginPhase.TypingCredentials;

        return LoginPhase.WaitLoginScreen;
    }

    /// <summary>
    /// TypingCredentials → ClickingConnect on Native phase advance. Native
    /// autonomously handles the Combo G password write (`SetEditWndText` to
    /// LOGIN_PasswordEdit at +0x1A8 with the v3.16.0 ScreenMode-swap wrapper);
    /// C# observes the resulting Native phase progression rather than driving
    /// keystrokes itself.
    ///
    /// **No skip-ahead to CharSelect** here even though `CharSelectAvailable`
    /// could flip during a single C# tick — Iter-2B verifier-round (T2-Opus +
    /// T2-Sonnet + T3-Sonnet 2026-05-16) converged on a real Fatal-bypass risk:
    /// if EQ briefly creates CharSelect window then tears it down + raises a
    /// Fatal OkDialog (kick-session race), skipping ahead from here bypasses
    /// StepWaitConnectResponse's Fatal-classification check. Natural progression
    /// through ClickingConnect → WaitConnectResponse keeps the Fatal-detection
    /// path live for every transition. Native phases 1-4 are reliable on Dalaya
    /// (Path A 2026-05-16 confirmed) — the 250ms tick interval comfortably
    /// catches each intermediate phase.
    /// </summary>
    private static LoginPhase StepTypingCredentials(WidgetState widgets, int gameState, LoginPhase nativePhase)
    {
        if (nativePhase == LoginPhase.Error)
            return LoginPhase.Error;

        if (nativePhase >= LoginPhase.ClickingConnect)
            return LoginPhase.ClickingConnect;

        return LoginPhase.TypingCredentials;
    }

    /// <summary>
    /// ClickingConnect → WaitConnectResponse on Native phase advance. Native
    /// handles the LOGIN_ConnectButton click via the structural ConnectWnd-rooted
    /// resolution (`eqmain_widgets_mq2style::FindConnectButtonStructural`).
    /// Skip-aheads removed per the 2026-05-16 verifier round (see
    /// StepTypingCredentials).
    /// </summary>
    private static LoginPhase StepClickingConnect(WidgetState widgets, int gameState, LoginPhase nativePhase)
    {
        if (nativePhase == LoginPhase.Error)
            return LoginPhase.Error;

        if (nativePhase >= LoginPhase.WaitConnectResponse)
            return LoginPhase.WaitConnectResponse;

        return LoginPhase.ClickingConnect;
    }

    /// <summary>
    /// WaitConnectResponse → {Error | ServerSelect | CharSelect}. The load-bearing
    /// transition for v3.22.0 — Native stalls at PHASE_WAIT_CONNECT_RESP on Dalaya
    /// because its gameState-gated PHASE_SERVER_SELECT transition never fires
    /// (gGameState never advances past 0; Path A finding 2026-05-16). C# routes
    /// around this via:
    ///   - CharSelectAvailable (v8 SHM, Dalaya PRIMARY path — Native publishes 1
    ///     when pinstCCharacterSelect → non-null, ~t=48s)
    ///   - ServerSelectVisible (non-Dalaya defensive path — server-select widget
    ///     becomes visible)
    ///   - OkDialog Fatal (Invalid Password / etc. — bail to Error rather than
    ///     spin-wait the 180s overall timeout)
    ///
    /// Recoverable dialog handling is delegated to the SM dispatch dwell on
    /// WaitConnectResponse — the okText/okClass snapshot is inspected on every
    /// tick by the caller, and Fatal classifications bail to Error immediately.
    /// Non-Fatal Recoverable dialogs (rare on Dalaya) effectively retry by
    /// staying in this phase until the 180s OverallTimeoutMs fires Error.
    /// </summary>
    // v3.22.24 fix6 fast-fail constants. Extracted to named const per
    // Code-Opus verifier convergent finding (drift trap if 12s tuning ever
    // needed). Sibling of OverallTimeoutMs (180_000) and the native 120s
    // per-phase budget — see CHANGELOG fix6 rationale for derivation.
    private const long OkDialogVisibleBudgetMs = 12_000;

    private static LoginPhase StepWaitConnectResponse(WidgetState widgets, int gameState, LoginPhase nativePhase,
        OkDisplayClass okClass, string okText, long nowMs, ref long okDialogFirstSeenMs)
    {
        if (nativePhase == LoginPhase.Error)
            return LoginPhase.Error;

        // Fatal classification = unrecoverable login error (Invalid Password,
        // banned account, etc.). Bail fast — retries don't help.
        if (widgets.OkDialogVisible && okClass == OkDisplayClass.Fatal)
        {
            FileLogger.Warn($"AutoLogin-SM: Fatal OkDisplay at WaitConnectResponse: \"{okText}\"");
            return LoginPhase.Error;
        }

        // v3.22.24 fast-fail-on-visible-only safety net (Nate, 2026-05-21):
        //
        // OkDialogVisible IS the "didn't reach serverselect" signal — the OK
        // error dialog appears precisely when the login server rejected the
        // credentials, and its presence blocks the connect→serverselect
        // transition that defines a successful login. Same gate, just with
        // a more reliable trigger than dialog-text classification.
        //
        // Why okClass classification can't always be trusted: native-side
        // text extraction depends on a Dalaya-specific CXStr at a structural
        // widget offset (main+0x25C → +0x30C, see Native/login_state_machine.cpp
        // fix4 comment block). The +0x30C anchor was confirmed working on PID
        // 33768 but null on PID 24400 — Dalaya stores dialog text in different
        // widgets per-PID or per-dialog-instance. Without a stable text path
        // every dialog instance, okClass falls back to None and the Fatal
        // short-circuit above doesn't fire.
        //
        // 12-second visibility gate is safe against slow-server false-positives
        // because the 138s slow-Dalaya case (per CLAUDE.md AUTOLOGIN SPEC →
        // Slow-server tolerance) does NOT produce an OK dialog — connect is
        // still pending upstream, no rejection has been signaled. OkDialog
        // visible >= 12s = definitively a server-side rejection, never just
        // server slowness.
        //
        // 12s budget rationale: dialog typically appears within ~500ms of
        // entering WaitConnectResponse (observed on PID 24400: t=9779ms first
        // ok=1, phase entered at t=9255ms). An extra 11s window allows for the
        // C# observer tick cadence (~250ms), any one-tick SHM torn reads to
        // recover, and a brief settling period in case the dialog auto-closes
        // (e.g. transient server-busy that fixes itself). Replaces the prior
        // 120s per-phase budget on bad-pass attempts.
        // Gate only fires for okClass != Recoverable. Recoverable patterns
        // (kPromptServer_Busy, kPromptTryAgain, queue-position) legitimately
        // can take >12s on Dalaya peak-load; v3.22.23 behavior was to wait
        // them out, and fix6 preserves that. None / Fatal (when text
        // extraction failed) and unclassified all trip the visibility gate.
        //
        // No `else { reset }` branch: a single torn-read tick (ok=true→
        // false→true between two SHM reads) would otherwise wipe an
        // accumulated streak — Gap-Opus verifier flag. The local var is
        // already per-SM (declared in RunLoginStateMachine scope) so it
        // dies with the SM; no cross-run pollution to defend against. A
        // dialog that genuinely disappears mid-WaitConnectResponse and is
        // never followed by another visible-tick will simply not trip the
        // gate this run, which is the desired behavior.
        if (widgets.OkDialogVisible && okClass != OkDisplayClass.Recoverable)
        {
            if (okDialogFirstSeenMs < 0)
                okDialogFirstSeenMs = nowMs;
            long visibleAgeMs = nowMs - okDialogFirstSeenMs;
            if (visibleAgeMs > OkDialogVisibleBudgetMs)
            {
                FileLogger.Warn(
                    $"AutoLogin-SM: bail on long-visible OkDialog at WaitConnectResponse "
                    + $"(visibleAge={visibleAgeMs}ms, okClass={okClass}, textLen={okText?.Length ?? 0})");
                return LoginPhase.Error;
            }
        }

        // T3-Sonnet/Opus 2026-05-16 verifier finding: ReadOkDisplaySnapshot can
        // return (None, "") on a torn read between its two class reads, even
        // when the dialog widget is visible. Surface this rare-but-possible
        // false-negative-on-Fatal case in the smoke trace so it is diagnosable.
        // Next tick (250ms later) recovers with a coherent read; the overall
        // 180s timeout catches the worst case anyway.
        if (widgets.OkDialogVisible && okClass == OkDisplayClass.None)
            FileLogger.Warn($"AutoLogin-SM: OkDialog visible but okClass=None — torn read or unclassified (text=\"{okText}\")");

        // Dalaya PRIMARY path: Native published charSelectAvailable=1 (EQ created
        // the CCharacterSelect window). Skip server-select entirely.
        if (widgets.CharSelectAvailable)
            return LoginPhase.CharSelect;

        // Non-Dalaya defensive: server-select widget appeared.
        if (widgets.ServerSelectVisible)
            return LoginPhase.ServerSelect;

        return LoginPhase.WaitConnectResponse;
    }

    /// <summary>
    /// ServerSelect → {Error | WaitServerLoad | CharSelect}. **DEAD-ON-DALAYA
    /// — defensive non-Dalaya scaffolding only.** Dalaya's QUICK-CONNECT button
    /// (`eqmain_widgets_mq2style:FindConnectButtonStructural slot=+0x34`) skips
    /// server-select entirely; ServerSelectVisible never flips true. T2-Opus +
    /// T3-Sonnet 2026-05-16 verifier round confirmed this state is unreachable on
    /// the live target. Retained as cheap insurance against a non-Dalaya retarget;
    /// the state observes Native + widgets and forwards to CharSelect as soon as
    /// CharSelectAvailable lights up.
    /// </summary>
    private static LoginPhase StepServerSelect(WidgetState widgets, int gameState, LoginPhase nativePhase)
    {
        if (nativePhase == LoginPhase.Error)
            return LoginPhase.Error;

        if (widgets.CharSelectAvailable)
            return LoginPhase.CharSelect;

        if (nativePhase >= LoginPhase.WaitServerLoad)
            return LoginPhase.WaitServerLoad;

        return LoginPhase.ServerSelect;
    }

    /// <summary>
    /// WaitServerLoad → CharSelect. **DEAD-ON-DALAYA — defensive non-Dalaya
    /// scaffolding only** (entered only from StepServerSelect's `nativePhase >=
    /// WaitServerLoad` branch which can't fire on Dalaya because Native is stuck
    /// at PHASE_WAIT_CONNECT_RESP). Retained as cheap insurance against a
    /// non-Dalaya retarget; the CharSelect signal works regardless of how we
    /// got here.
    /// </summary>
    private static LoginPhase StepWaitServerLoad(WidgetState widgets, int gameState, LoginPhase nativePhase)
    {
        if (nativePhase == LoginPhase.Error)
            return LoginPhase.Error;

        if (widgets.CharSelectAvailable)
            return LoginPhase.CharSelect;

        return LoginPhase.WaitServerLoad;
    }

    /// <summary>
    /// Character selection dispatch.
    ///
    /// Direct port of the legacy <see cref="RunLoginSequence"/> char-select block
    /// (AutoLoginManager.cs:2150-2326 in the legacy path): wait for char list ready,
    /// run <see cref="CharacterSelector.Decide"/>, request selection via
    /// <see cref="CharSelectReader.RequestSelectionBySlot"/>, wait for ack.
    /// Safety aborts on resolvedSlot=0 (no name match / malformed), slot
    /// out-of-range, or ack-timeout (matches legacy hotfix v6b — without DLL ack,
    /// SetCurSel's TIMERPROC never ran and EQ stays on default slot 0; Enter
    /// World then logs in the WRONG character).
    ///
    /// **Blocking** (~5-30s typical, up to ~40s with bridge warmup retries). This
    /// VIOLATES plan-doc § "Per-state action mapping" Rule 4 ("States ONLY decide
    /// transitions") in favor of EQSwitch CLAUDE.md "if it's already working,
    /// don't fix it" — verbatim port of safety semantics that prevent
    /// wrong-character logins. The 250ms dispatch-tick cadence is suspended
    /// during this Step; the OverallTimeoutMs (180s) and explicit RefreshHandle
    /// checks are the safety nets. Non-blocking sub-state refactor: not planned
    /// — current shape is feature-complete and stable across all v3.22.x smokes.
    ///
    /// Returns <see cref="LoginPhase.EnteringWorld"/> on selection ack confirmed,
    /// <see cref="LoginPhase.Error"/> on any abort condition,
    /// <see cref="LoginPhase.Complete"/> if char-select-only intent (character==null)
    /// or already-in-game observed during char-list wait.
    /// </summary>
    private LoginPhase StepCharSelect(
        CharSelectReader charSelect,
        Account account,
        Character? character,
        ref IntPtr hwnd,
        int pid,
        LoginPhase nativePhase,
        bool shouldEnterWorld,
        System.Threading.CancellationToken ct)
    {
        if (nativePhase == LoginPhase.Error)
            return LoginPhase.Error;
        if (ct.IsCancellationRequested)
            return LoginPhase.Error;

        // LoginToCharselect path — no character target. Reaching CharSelect IS success.
        if (character == null)
        {
            FileLogger.Info($"AutoLogin-SM: char-select-only path, no character target (PID {pid}) — Complete");
            Report($"{account.Name} reached char select.");
            return LoginPhase.Complete;
        }

        // Wait for char list to be populated. CharSelectAvailable (the SHM signal
        // that brought us to this state) fires when pinstCCharacterSelect != NULL,
        // but the bridge's heap scan for character names takes ~5-15s after that.
        // Match legacy timing — up to ~30s with 500ms polls. Within-loop:
        // RefreshHandle on every iter (EQ recreates window at this transition),
        // already-in-game short-circuit (user could beat us if they manually
        // clicked Enter World during the wait).
        const int kMaxCharListWaitMs = 30_000;
        var listSw = Stopwatch.StartNew();
        bool charListReady = false;
        while (listSw.ElapsedMilliseconds < kMaxCharListWaitMs)
        {
            // Iter-3 fix-round-2 (verifier T2-Opus REJECT C2): cancellation check.
            // Without this, an OverallTimeoutMs fire at t=180s while we're 5s into
            // a 30s char-list wait would block for 25s past the intended cutoff.
            if (ct.IsCancellationRequested)
            {
                FileLogger.Warn($"AutoLogin-SM: cancellation during char-list wait at t={listSw.ElapsedMilliseconds}ms (PID {pid})");
                return LoginPhase.Error;
            }
            if (charSelect.ReadCharCount(pid) > 0 || charSelect.IsCharSelectReady(pid))
            {
                charListReady = true;
                break;
            }
            hwnd = RefreshHandle(pid, hwnd);
            if (hwnd == IntPtr.Zero)
            {
                FileLogger.Warn($"AutoLogin-SM: lost EQ window during char-list wait (PID {pid})");
                Report($"{account.Name}: lost EQ window during char-list wait (crashed or closed)");
                return LoginPhase.Error;
            }
            if (IsInGame(charSelect, pid, hwnd))
            {
                FileLogger.Info($"AutoLogin-SM: already in-game during char-list wait — treating as success (PID {pid})");
                Report($"{account.Name} already in-game.");
                return LoginPhase.Complete;
            }
            Thread.Sleep(500);
        }

        if (!charListReady)
        {
            // Hotfix v4 (HIGH-A) parity: MQ2 bridge never came up. Pre-fix fallthrough
            // would pulse Enter on EQ's DEFAULT-selected character — abort instead.
            FileLogger.Error($"AutoLogin-SM: MQ2 bridge didn't populate char list after {kMaxCharListWaitMs}ms (PID {pid}) — stopping at char select to avoid wrong-character enter-world");
            Report($"{account.Name}: MQ2 bridge didn't initialize — stopped at char select");
            return LoginPhase.Error;
        }

        // Mirror legacy lines 2175-2178: if latch tripped but charCount=0 (cache-
        // invalidation between bridge anchor scans), give bridge ~2s to republish.
        // Iter-3 fix-round-2 (T2-Opus C2): per-iter cancellation check.
        for (int retry = 0; retry < 4 && charSelect.ReadCharCount(pid) == 0; retry++)
        {
            if (ct.IsCancellationRequested) return LoginPhase.Error;
            Thread.Sleep(500);
        }

        // Snapshot character list ONCE — bounds checks below MUST use this snapshot
        // (re-reading SHM via ReadCharCount can diverge if the DLL refreshes
        // between reads; feature-dev review finding M1a on legacy). Use
        // charNames.Length as authoritative.
        var charNames = charSelect.ReadAllCharNames(pid);
        int charCount = charNames.Length;
        FileLogger.Info($"AutoLogin-SM: char-list ready: {charCount} characters — {string.Join(", ", charNames)} (PID {pid})");

        // Iter-3 fix-round-2 (smoke 1 surfaced this — natedogg account at 21:37:31
        // hit "MQ2 heap in slot-mode (1 placeholder slot(s))"). Port legacy
        // single-char structural fallback (RunLoginSequence:2192-2203): if the
        // bridge published exactly 1 character AND it's a "Slot N" placeholder
        // (heap-read couldn't pull the real name string), slot 1 is the target by
        // elimination — there's literally no other slot to be wrong about.
        // Bypass Decide for this case; Decide would return 0 because the
        // placeholder won't match character.Name.
        int resolvedSlot;
        bool resolvedByName;
        string decisionLog;
        if (charCount == 1 && charNames[0].StartsWith("Slot ", StringComparison.Ordinal))
        {
            resolvedSlot = 1;
            resolvedByName = false;
            decisionLog = $"single-char structural fallback → slot 1 = '{character.Name}' (bridge in slot-mode)";
        }
        else
        {
            (resolvedSlot, resolvedByName, decisionLog) = CharacterSelector.Decide(
                character.CharacterSlot, character.Name, charNames);
        }
        FileLogger.Info($"AutoLogin-SM: selector → {decisionLog} (PID {pid})");

        // Safety abort #1: resolvedSlot=0 — no name match, or malformed request.
        // Entering world on EQ's default selection is a regression (Phase 5b's
        // unified abort, promoted from feature-dev finding I2). Slot-mode
        // detection mirrors legacy lines 2224-2228.
        if (resolvedSlot == 0)
        {
            bool isSlotMode = charNames.Length > 0
                && charNames[0].StartsWith("Slot ", StringComparison.Ordinal);
            string cause = isSlotMode
                ? $"MQ2 heap in slot-mode ({charNames.Length} placeholder slot(s)) — character names unavailable"
                : $"character '{character.Name}' not found in account '{account.Name}'";
            FileLogger.Error($"AutoLogin-SM: {cause} — stopping at char select to avoid wrong-character enter-world (PID {pid})");
            Report($"{account.Name}: {cause} — stopped at char select");
            return LoginPhase.Error;
        }

        // Safety abort #2: slot out of range — same guard as legacy lines 2233-2238.
        if (resolvedSlot > charCount)
        {
            FileLogger.Error($"AutoLogin-SM: slot {resolvedSlot} exceeds char count {charCount} — stopping at char select to avoid wrong-character enter-world (PID {pid})");
            Report($"{account.Name}: slot {resolvedSlot} out of range (only {charCount} characters) — stopped at char select");
            return LoginPhase.Error;
        }

        // Fire selection — DLL's MQ2Bridge::SelectCharacter handles the actual
        // pCharCharacterListWnd->SetCurSel call on the game thread via TIMERPROC.
        charSelect.RequestSelectionBySlot(pid, resolvedSlot);
        FileLogger.Info($"AutoLogin-SM: requested slot {resolvedSlot} (byName={resolvedByName}, PID {pid})");

        // Wait for DLL ack — up to 24s at 50ms granularity (v3.22.89 raised the
        // cap from the legacy 200 iter × 50ms = 10s to 480 × 50ms = 24s; see the
        // ack-starvation reasoning at the maxAckIters declaration below). Hard
        // abort on timeout — see legacy hotfix v6b reasoning at lines 2298-2308. Cheap first-read
        // bypass mirrors legacy lines 2267-2280: the native two-tier throttle
        // often acks before C# even starts the Sleep loop.
        var ackSw = Stopwatch.StartNew();
        bool acked = charSelect.IsSelectionAcknowledged(pid);
        long firstReadUs = ackSw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        int totalIters = 1;
        if (!acked)
        {
            // v3.22.89 — was 200 iters (10s). The DLL runs SetCurSel on the GAME
            // thread via TIMERPROC; during char-select scene load that thread can stay
            // busy >10s, starving the TIMERPROC so the ack lands late. 2026-05-31
            // natedogg smoke: the bridge poll went quiet at the selection point and the
            // game thread didn't free until ~13s later (ApplySlimTitlebar succeeded at
            // T+13s → the window was responsive, i.e. busy not hung), but C# had already
            // given up at 10s and dead-stopped at char select. The request persists in
            // SHM, so a longer budget lets the DLL ack once the thread frees. Still
            // ack-gated → no wrong-character enter-world risk.
            const int maxAckIters = 480;   // 480 × 50ms = 24s (was 200 = 10s)
            for (int ack = 0; ack < maxAckIters; ack++)
            {
                // Iter-3 fix-round-2 (T2-Opus C2): cancellation check at every ack-poll iter.
                if (ct.IsCancellationRequested)
                {
                    FileLogger.Warn($"AutoLogin-SM: cancellation during selection-ack wait after {ack * 50}ms (PID {pid})");
                    return LoginPhase.Error;
                }
                Thread.Sleep(50);
                totalIters = ack + 2;
                if (charSelect.IsSelectionAcknowledged(pid)) { acked = true; break; }
            }
        }
        ackSw.Stop();

        if (!acked)
        {
            FileLogger.Error($"AutoLogin-SM: DLL did not ack selection for slot {resolvedSlot} in 24s — stopping at char select to avoid wrong-character enter-world (PID {pid})");
            Report($"{account.Name}: character selection not confirmed — stopped at char select");
            return LoginPhase.Error;
        }

        FileLogger.Info($"AutoLogin-SM: selection ack observed after {totalIters} iter(s) / {ackSw.ElapsedMilliseconds}ms (firstRead={(totalIters == 1)}, firstReadUs={firstReadUs}, PID {pid})");

        // 100ms post-ack settle for downstream UI bookkeeping (legacy line 2310-2313).
        // The DLL's SetCurSel ran synchronously via TIMERPROC; this is buffer for
        // any UI-thread follow-up (selection-changed event handlers, etc).
        Thread.Sleep(100);

        // Iter-3 fix-round-2 (verifier T2-Opus REJECT C1 + T2-Sonnet C3 +
        // T3-Opus LOW-4 convergent): honor enterWorldOverride. If the caller
        // requested char-select-only (shouldEnterWorld=false), report the
        // selection completion and exit Complete — don't fall through to
        // StepEnteringWorld. Matches legacy RunLoginSequence:2502-2509 semantics.
        if (!shouldEnterWorld)
        {
            FileLogger.Info($"AutoLogin-SM: char-select-only intent honored — slot {resolvedSlot} selected, stopping before Enter World (PID {pid})");
            Report($"{account.Name} reached char select (slot {resolvedSlot}).");
            return LoginPhase.Complete;
        }

        return LoginPhase.EnteringWorld;
    }

    /// <summary>
    /// Enter World dispatch.
    ///
    /// Direct port of legacy <see cref="RunLoginSequence"/> enter-world block
    /// (AutoLoginManager.cs:2329-2492 in the legacy path). Branches on
    /// <see cref="ShouldSkipShmEnterWorld"/>:
    ///
    /// <list type="bullet">
    /// <item>Dalaya (default skip=true): goes directly to PulseKey3D fallback —
    /// CLW_EnterWorldButton isn't in the CXWnd tree by the time charselect-
    /// ready is signaled, so all SHM attempts would return -1 and waste
    /// ~2-2.5s before fallback (v3.15.11 Target 2 Option A short-circuit).</item>
    /// <item>Non-Dalaya: up to 4× SHM <see cref="CharSelectReader.RequestEnterWorld"/>
    /// with result-code handling (-2 already-in-game, -4 SEH abort, !=1 retry,
    /// =1 success). PulseKey3D fallback if all SHM attempts fail.</item>
    /// </list>
    ///
    /// Either path waits for <see cref="WaitForEnterWorldTransition"/> to confirm
    /// zone-load. On Dalaya the detection signal is IsHungAppWindow hung→responsive
    /// transition (3D scene-load briefly hangs render thread); on non-Dalaya the
    /// signal is window title containing " - ". gameState gate is broken on Dalaya
    /// so cannot be used here.
    ///
    /// **Blocking** (5-90s SHM path / 5-60s × 3 PulseKey3D fallback). Same
    /// architectural caveat as <see cref="StepCharSelect"/> — verbatim port of
    /// the legacy semantics; non-blocking sub-state refactor not planned.
    ///
    /// Returns <see cref="LoginPhase.Complete"/> on zone-load confirmed,
    /// <see cref="LoginPhase.Error"/> on all-attempts-failed or window lost.
    /// </summary>
    private LoginPhase StepEnteringWorld(
        CharSelectReader charSelect,
        KeyInputWriter writer,
        Account account,
        ref IntPtr hwnd,
        int pid,
        System.Threading.CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return LoginPhase.Error;
        Report("Entering world...");
        bool entered = false;
        bool skipShmEnterWorld = ShouldSkipShmEnterWorld(account);

        // Only log the skip when MQ2 was actually available — otherwise we'd be
        // using PulseKey3D regardless and the "skipping" log line would be
        // misleading. Mirrors legacy line 2345-2349.
        if (skipShmEnterWorld && charSelect.IsMQ2Available(pid))
            FileLogger.Info($"AutoLogin-SM: skipping SHM Enter World on Dalaya (PID {pid}, account {account.Name}) — using PulseKey3D directly");

        // ── Primary path: SHM RequestEnterWorld (non-Dalaya) ────────────
        // v3.15.9 tuning: 4 attempts × 500ms inter-retry. Mirrors legacy
        // lines 2351-2443. Per-attempt: ack wait (5s × 50ms = 100 polls),
        // result-code handling, zone-load verification.
        if (charSelect.IsMQ2Available(pid) && !skipShmEnterWorld)
        {
            const int kMaxEnterWorldAttempts = 4;
            for (int attempt = 0; attempt < kMaxEnterWorldAttempts; attempt++)
            {
                // Iter-3 fix-round-2 (T2-Opus C2): cancellation per attempt.
                if (ct.IsCancellationRequested) return LoginPhase.Error;
                hwnd = RefreshHandle(pid, hwnd);
                if (hwnd == IntPtr.Zero)
                {
                    Report($"{account.Name}: lost EQ window during enter-world (crashed or closed)");
                    return LoginPhase.Error;
                }
                if (IsInGame(charSelect, pid, hwnd))
                {
                    entered = true;
                    FileLogger.Info($"AutoLogin-SM: already in-game before SHM attempt {attempt + 1} (gameState=5 or title)");
                    break;
                }

                charSelect.RequestEnterWorld(pid);

                bool acked = false;
                for (int w = 0; w < 100; w++)
                {
                    if (ct.IsCancellationRequested) return LoginPhase.Error;
                    if (charSelect.IsEnterWorldAcknowledged(pid)) { acked = true; break; }
                    Thread.Sleep(50);
                }

                if (!acked)
                {
                    FileLogger.Warn($"AutoLogin-SM: DLL did not ack enter-world request (attempt {attempt + 1})");
                    continue;
                }

                int result = charSelect.ReadEnterWorldResult(pid);
                if (result == -2)
                {
                    // Already in-game (user beat us, or prior request landed). Do NOT
                    // retry or fall back — would phantom-click in-game UI. Legacy lines
                    // 2394-2403.
                    entered = true;
                    FileLogger.Info($"AutoLogin-SM: enter-world request dropped by DLL (already in-game, attempt {attempt + 1})");
                    break;
                }
                if (result == -4)
                {
                    // Hotfix v6c (Agent 2 F2.5): SEH fault during CLW_EnterWorldButton
                    // click. Client UI stack is in unknown state — retrying or PulseKey3D
                    // could deepen the fault. Abort cleanly. Legacy lines 2404-2414.
                    FileLogger.Error($"AutoLogin-SM: EQ client faulted during Enter World click (SEH in game, attempt {attempt + 1}) — stopping to avoid further damage");
                    Report($"{account.Name}: EQ client faulted during Enter World — please restart the client");
                    return LoginPhase.Error;
                }
                if (result != 1)
                {
                    FileLogger.Warn($"AutoLogin-SM: enter-world result={result} (attempt {attempt + 1}), button may not exist yet");
                    if (attempt < kMaxEnterWorldAttempts - 1)
                        Thread.Sleep(500);
                    continue;
                }

                FileLogger.Info($"AutoLogin-SM: CLW_EnterWorldButton clicked via SHM (attempt {attempt + 1})");

                // Wait for zone-load. Primary: IsHungAppWindow hung→responsive
                // (authoritative on Dalaya where gameState stays 0 and title stays
                // custom across char-select→in-world). Fallback: native " - " title
                // flip. 90s cap per legacy.
                if (WaitForEnterWorldTransition(pid, ref hwnd, 90))
                    entered = true;
                break; // button confirmed clicked — never re-click (could cause disconnect)
            }
        }

        // ── Fallback: PulseKey3D keyboard Enter ──────────────────────
        // Re-check in-game first — if user manually entered world while SHM was
        // retrying, the keyboard Enter would land in-game and trigger UI actions
        // (phantom click). Legacy lines 2446-2456.
        if (!entered)
        {
            hwnd = RefreshHandle(pid, hwnd);
            if (hwnd != IntPtr.Zero && IsInGame(charSelect, pid, hwnd))
            {
                entered = true;
                FileLogger.Info("AutoLogin-SM: in-game detected before PulseKey3D fallback — skipping (gameState=5 or title)");
            }
        }
        if (!entered)
        {
            if (charSelect.IsMQ2Available(pid) && skipShmEnterWorld)
                FileLogger.Info("AutoLogin-SM: PulseKey3D enter-world (SHM skipped per Launch.SkipShmEnterWorldOnDalaya)");
            else if (charSelect.IsMQ2Available(pid))
                FileLogger.Warn("AutoLogin-SM: SHM enter-world failed, falling back to PulseKey3D");
            else
                FileLogger.Info("AutoLogin-SM: MQ2 not available, using PulseKey3D for enter-world");

            for (int attempt = 0; attempt < 3 && !entered; attempt++)
            {
                // Iter-3 fix-round-2 (T2-Opus C2): cancellation per attempt.
                if (ct.IsCancellationRequested) return LoginPhase.Error;
                hwnd = RefreshHandle(pid, hwnd);
                if (hwnd == IntPtr.Zero)
                {
                    Report($"{account.Name}: lost EQ window during PulseKey3D enter-world fallback (crashed or closed)");
                    return LoginPhase.Error;
                }
                if (IsInGame(charSelect, pid, hwnd))
                {
                    entered = true;
                    FileLogger.Info($"AutoLogin-SM: already in-game before PulseKey3D attempt {attempt + 1}");
                    break;
                }

                // Iter-3 fix-round-2 (verifier T2-Opus REJECT C3 + T2-Sonnet C2):
                // SemaphoreSlim arbitration around the focus-faking sequence. Only
                // one PID can own focus-faking at a time — without this gate, a
                // dual-box Dalaya smoke (both PIDs reach PulseKey3D fallback in
                // the same ~1s window) would interleave Activate→Sleep→Pulse→
                // Sleep→Deactivate calls, and one PID's Deactivate would clear
                // the IAT-hook spoofing in the other PID's mid-keystroke window.
                // Acquire honors cancellation — if cts fires while we're waiting
                // for the other PID's Deactivate, we bail cleanly.
                //
                // Iter-3 fix-round-3 (verifier T3-Opus HIGH): acquire-once pattern.
                // Wait outside the held-block's try; if Wait succeeds (return
                // value, not exception), we OWN the mutex unconditionally and the
                // held-block's try/finally guarantees Release. No `gotMutex` flag
                // needed — eliminates the theoretical "exception between Wait
                // and flag assignment leaks the mutex" gap, AND removes a future-
                // maintenance trap if anyone adds code between the two statements.
                //
                // Iter-3 fix-round-3 (verifier T2-Opus MINOR-2): inner try/finally
                // around writer.Activate→Deactivate. If PulseKey3D throws (SHM
                // torn-write etc.), writer.Deactivate runs in the inner finally
                // BEFORE the mutex is released, ensuring focus-faking is cleared
                // for the next PID. Pre-fix an exception orphaned the Activate
                // state, leaving the IAT-hook spoofing on for whichever PID won
                // the mutex next.
                try { _focusFakeMutex.Wait(ct); }
                catch (OperationCanceledException) { return LoginPhase.Error; }

                try
                {
                    writer.Activate(pid, suppress: true);
                    try
                    {
                        Thread.Sleep(500);
                        PulseKey3D(writer, pid, hwnd, 0x0D);
                        Thread.Sleep(500);
                    }
                    finally
                    {
                        // Iter-3 fix-round-3 (verifier T2-Opus MINOR-2): guaranteed
                        // Deactivate even if PulseKey3D or a Sleep throws — focus-
                        // faking state CANNOT leak across the mutex boundary.
                        try { writer.Deactivate(pid); }
                        catch (Exception dex) { FileLogger.Warn($"AutoLogin-SM: writer.Deactivate in PulseKey3D-finally threw: {dex.Message}"); }
                    }
                }
                finally
                {
                    // Iter-3 fix-round-3 (verifier T2-Sonnet MINOR-2): guard
                    // SemaphoreFullException — theoretically only possible via a
                    // double-release bug in a future edit, but defensive log is
                    // cheap and makes the assert visible without crashing.
                    try { _focusFakeMutex.Release(); }
                    catch (SemaphoreFullException) { FileLogger.Error($"AutoLogin-SM: _focusFakeMutex.Release double-fired — possible double-release bug (PID {pid})"); }
                }

                // 60s cap per legacy line 2487 — PulseKey3D is the fallback, if the
                // first attempt's keystroke didn't land we still have 2 more attempts.
                if (WaitForEnterWorldTransition(pid, ref hwnd, 60))
                    entered = true;
            }
        }

        if (entered)
        {
            Report($"{account.Name} logged in!");
            FileLogger.Info($"AutoLogin-SM: {account.Name} login complete (PID {pid})");
            return LoginPhase.Complete;
        }

        Report($"{account.Name}: reached char select but Enter World didn't register");
        FileLogger.Warn($"AutoLogin-SM: {account.Name} enter-world failed after all attempts (PID {pid})");
        return LoginPhase.Error;
    }

    /// <summary>
    /// v3.22.25: Locked LastLoginAt + LastLoginResult write + SaveImmediate for the
    /// legacy <see cref="RunLoginSequence"/> path. Takes
    /// <see cref="ConfigManager.ConfigMutationLock"/> for the duration of the write +
    /// JsonSerializer call so a concurrent UI-thread ApplySettings/ReloadConfig cannot
    /// torn-write the JSON nor swap <c>_config.Accounts</c> mid-serialization.
    /// Emits a "save deferred — Settings dialog open" log line when the lock is
    /// contended (TryEnter-then-Enter pattern; semantics unchanged).
    /// <para>Unlike the SM-finally path, this helper does NOT re-resolve the
    /// captured <paramref name="account"/> reference against <c>_config.Accounts</c>:
    /// the legacy path is not the deployed surface (UseStateMachine=true is the
    /// operationally-active path) and re-resolve would be a separate refactor.
    /// Orphan-ref risk if Settings swapped the list between SM launch and here is
    /// accepted for the legacy path only.</para>
    /// </summary>
    private void SaveLastLoginResultLocked(Account account, string result, string logTag)
    {
        // v3.22.25 verifier-round-2 fix: canonical Monitor.Enter(lock, ref tookLock)
        // pattern. If Monitor.Enter throws (e.g. ThreadAbort, OOM), tookLock stays
        // false and the finally branch skips Monitor.Exit on a never-acquired lock
        // (which would itself throw SynchronizationLockException, masking the
        // original exception in flight).
        bool tookLock = false;
        try
        {
            if (!Monitor.TryEnter(ConfigManager.ConfigMutationLock, 0))
            {
                FileLogger.Info($"{logTag}: save deferred — Settings dialog ApplySettings/ReloadConfig in progress; waiting for ConfigMutationLock for {account.Name}");
                Monitor.Enter(ConfigManager.ConfigMutationLock, ref tookLock);
            }
            else
            {
                tookLock = true;
            }
            account.LastLoginAt = DateTime.UtcNow;
            Thread.MemoryBarrier();
            account.LastLoginResult = result;
            // SaveImmediate: thread-safe synchronous write. Save() uses a
            // System.Windows.Forms.Timer whose Tick fires on the creating thread —
            // calling Save from this background worker creates a Timer with no
            // message pump, and the write never reaches disk until app shutdown
            // drain (or never, on a hard kill). SaveImmediate bypasses the timer.
            ConfigManager.SaveImmediate(_config);
        }
        finally
        {
            if (tookLock) Monitor.Exit(ConfigManager.ConfigMutationLock);
        }
    }

    /// <summary>
    /// Marshals the LoginComplete event to the captured UI sync context (when available),
    /// matching the dispatch pattern RunLoginSequence uses for its terminal event firing.
    /// Falls back to synchronous invoke when no sync context is captured.
    ///
    /// Iter-3 fix-round-2 (verifier T3-Sonnet HIGH-2): does the
    /// <see cref="_activeLoginPids"/> TryRemove ATOMICALLY with the
    /// LoginComplete invocation — when the sync context is available, both
    /// happen inside the same Post lambda. Pre-fix, the SM path did TryRemove
    /// on the background thread BEFORE FireLoginComplete posted, creating a
    /// race window where a ClientDiscovered handler firing between the two
    /// would see IsLoginActive=false and resume window manipulation on a PID
    /// that hadn't fired its LoginComplete yet. Matches the legacy
    /// RunLoginSequence pattern at line 2963-2975.
    /// </summary>
    private void FireLoginComplete(int pid)
    {
        var ctx = _syncContext;
        if (ctx != null)
        {
            ctx.Post(_ =>
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

    private void RunLoginSequence(int pid, Account account, Character? character, string password, bool? enterWorldOverride)
    {
        // Snapshot config values — RunLoginSequence runs on a background thread
        // while ReloadConfig can mutate _config on the UI thread.
        int loginScreenDelayMs = _config.LoginScreenDelayMs;
        int warmupDwellMs = _config.WarmupDwellMs;
        string eqPath = _config.EQPath;
        // v3.17.0: snapshot the new retry-related tunables. The retry loop reads
        // these during its iterations — without snapshot, a user opening Settings
        // and hitting Apply during a 30s recovery sleep could swap values mid-
        // iteration (one retry uses 30s wait, next uses 5s, etc.). The same
        // ReloadConfig race applies to all tunables read inside the retry loop.
        int connectRetryCount = _config.Launch.ConnectRetryCount;
        int staleSessionWaitMs = _config.Launch.StaleSessionWaitMs;
        int staleSessionPollIntervalMs = _config.Launch.StaleSessionPollIntervalMs;
        int postBurst2QuickFailCheckMs = _config.Launch.PostBurst2QuickFailCheckMs;
        int postBurst1WaitMs = _config.Launch.PostBurst1WaitMs;
        int burst2ActivationSettleMs = _config.Launch.Burst2ActivationSettleMs;
        int burst2PostKeystrokeMs = _config.Launch.Burst2PostKeystrokeMs;

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
            else
            {
                // v3.15.7 (2026-05-09): mark autologin-active for this PID so
                // eqswitch-di8.cpp's kPromptWindows[] dismiss machinery stands
                // down. Without this, the native dismiss iterates every poll
                // tick from gameState=0 through in-game and can click transient
                // widgets at server-select / charselect-load, closing the EQ
                // process (root cause of the 2026-05-09 team1 regression: 4
                // consecutive failures with both EQ procs self-exiting ~7s
                // after BURST 2). Cleared in the finally block below on every
                // exit path.
                loginShm.SetAutoLoginActive(pid, true);
                FileLogger.Info($"AutoLogin: SetAutoLoginActive(true) for PID {pid} — native dismiss suppressed");
            }

            // ── Wait for EQ window ──
            Report("Waiting for EQ window...");
            var hwnd = WaitForWindow(pid, TimeSpan.FromSeconds(30));
            if (hwnd == IntPtr.Zero) { Report("Timeout: EQ window did not appear"); return; }
            LogWidgetSnapshot(loginShm, pid, "login-screen-ready");

            // ══════════════════════════════════════════════════════════════
            // PATH A: In-process login via LoginShm (zero keyboard injection)
            //
            // CURRENT STATUS (2026-04-25): PATH A's intended in-process login
            // is broken on Dalaya — Combo G's password write works (verified
            // in DLL log: "set password via Combo G"), but the DLL's
            // PHASE_WAIT_CONNECT_RESP detection in
            // login_state_machine.cpp:399-415 polls for a `gameState` change
            // that never advances on Dalaya (gameState/title both lie — see
            // memory reference_eqswitch_dalaya_signals.md). PATH A therefore
            // always times out at 45s and falls through to PATH B.
            //
            // ⚠ LOAD-BEARING SIDE EFFECT — DO NOT NAIVELY DISABLE ⚠
            // The 45s PATH A timeout is the warmup PATH B's keystroke
            // injection requires. Disabling PATH A and going straight to
            // PATH B at T+10s causes EQ to drop the first ~3 keystrokes of
            // BURST 1 (verified 2026-04-25 — password truncated 6→3 chars,
            // login failed). PATH A's wasted 45s is incidentally giving
            // EQ's DirectInput cooperative-level negotiation enough wall-
            // clock to settle before BURST 1 fires. Memory:
            // feedback_chesterton_fence_load_bearing_bugs.md.
            //
            // ══════════════════════════════════════════════════════════════
            // To skip PATH A safely you need EITHER (a) a real DLL
            // post-connect detection signal (the "D" task) so PATH A
            // actually completes and reports success, OR (b) a non-time-
            // based readiness gate before PATH B's BURST 1 (e.g. wait for
            // password-field focus, first scene render, or DI8 cooperative
            // level transition). A flat loginScreenDelayMs bump just trades
            // the 45s back for a different fixed wait.
            // ══════════════════════════════════════════════════════════════
            // PATH A disabled for agent investigation 2026-04-25 — truncation symptom
            // if (loginShm != null)
            // {
            //     bool shouldEnter = enterWorldOverride ?? (character != null);
            //     if (shouldEnter && character == null)
            //     {
            //         FileLogger.Warn($"AutoLogin: {account.Name} requested enter-world but no Character target — staying at charselect (LoginShm path)");
            //         shouldEnter = false;
            //     }
            //
            //     bool handled = TryLoginViaShm(pid, loginShm, account, character,
            //         password, shouldEnter, ref hwnd);
            //     if (handled)
            //         return;
            //
            //     // LoginShm path didn't complete — fall through to keyboard injection
            //     FileLogger.Info($"AutoLogin: LoginShm path failed for PID {pid}, falling back to keyboard injection");
            // }

            // ══════════════════════════════════════════════════════════════
            // Single credential-entry method:
            //   warmup (SHM widget discovery + Combo G silent-no-op) →
            //   dwell (DI8 cooperative-level settle) →
            //   BURST 1 keystrokes (the actual password typing) →
            //   cancel SHM (stop DLL retry loop)
            //
            // 2026-04-25: silent injection abandoned for now. Combo G's CXStr
            // write at +0x1A8 doesn't reach EQ's render/submit buffer (read-
            // back at +0x14 confirms we wrote somewhere, but EQ reads from a
            // different field). The SHM LOGIN attempt is kept as warmup ritual
            // ONLY — its widget-discovery activity warms up EQ's input pump and
            // gives DI8 cooperative-level negotiation wall-clock to settle.
            // Tunable via WarmupDwellMs config (default 4s post-phase-advance).
            //
            // Future stretch: fix Combo G to write the right widget/buffer
            // (live render-side EditWnd), then BURST 1 can be removed entirely.
            // ══════════════════════════════════════════════════════════════
            LogWidgetSnapshot(loginShm, pid, "pre-burst1");
            RunCredentialEntry(pid, hwnd, writer, loginShm, account, password,
                loginScreenDelayMs, warmupDwellMs,
                targetCharacterName: character?.Name ?? "",
                charSelect: charSelect);
            LogWidgetSnapshot(loginShm, pid, "post-burst1");

            // Fire LoginCredentialsSent — TrayManager uses this to apply slim-
            // titlebar + hook config + window title NOW (T+~7s) instead of
            // waiting for charselect-ready (T+~30s).
            try
            {
                if (_syncContext != null)
                    _syncContext.Post(_ => LoginCredentialsSent?.Invoke(this, pid), null);
                else
                    LoginCredentialsSent?.Invoke(this, pid);
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"AutoLogin: LoginCredentialsSent handler threw for PID {pid}: {ex.Message}");
            }

            // ── Wait for server response (no focus-faking) ──
            // v3.15.2: tunable via Launch.PostBurst1WaitMs (default 3000).
            // R3 fix (verifier T2-S 2026-05-15): use snapshotted value, not
            // live _config — every other PostBurst1WaitMs read elsewhere in
            // this method already uses postBurst1WaitMs (see line 1203).
            Thread.Sleep(postBurst1WaitMs);
            hwnd = RefreshHandle(pid, hwnd);
            if (hwnd == IntPtr.Zero) { Report($"{account.Name}: lost EQ window after login (crashed or closed)"); return; }

            // ══════════════════════════════════════════════════════════
            // Diff 4 (v3.18+, 2026-05-15): JoinServerDirect — bypass BURST 2
            //
            // MQ2 RoF2-emu's StateMachine.cpp:773 calls
            // g_pLoginServerAPI->JoinServer((int)server->ID) directly to
            // advance from server-select to char-select. EQSwitch's pre-Diff-4
            // path uses VK_RETURN PostMessage (BURST 2) which assumes Dalaya
            // is already highlighted. Diff 4's direct call is more
            // deterministic AND removes the only BURST surviving the
            // Diff 2/3 structural button click flip (kMQ2StyleWidgetLookup
            // re-enabled 2026-05-15).
            //
            // Routing:
            //   - JoinServerId == 0     → wire disabled, fall through to BURST 2
            //   - loginShm == null      → SHM open failed earlier, fall through
            //   - outcome == Success    → skip BURST 2 entirely
            //   - outcome != Success    → log + fall through to BURST 2 fallback
            //
            // Failure modes Diff 4 tolerates (all silent fall-through to BURST 2):
            //   - LoginServerAPI not yet populated (timing race; native gates SEH)
            //   - vtable mismatch (Dalaya patch shifted layout)
            //   - prologue patch (anti-cheat hooked the function entry)
            //   - SEH inside the JoinServer call itself
            //   - 2-second ack timeout (DLL stalled or not running)
            int joinServerId = _config.Launch.JoinServerId;
            bool joinServerSucceeded = TryJoinServerDirectOrFallback(
                loginShm, pid, joinServerId, account, isRetry: false);

            if (!joinServerSucceeded)
            {
                // ══════════════════════════════════════════════════════════
                // BURST 2: Confirm server select (~1 second active)
                // ══════════════════════════════════════════════════════════
                Report("Confirming server...");
                writer.Activate(pid, suppress: true);
                // v3.15.2: tunable via Launch.Burst2ActivationSettleMs (default 300).
                // v3.20.0 R2 (verifier-driven Sonnet T2/T3 convergent 2026-05-15):
                // use snapshotted `burst2ActivationSettleMs` (line ~758) instead
                // of live `_config` read. Retry path already does this correctly
                // (line ~1273); primary path was the surviving v3.17.0 R3 race
                // — Settings → Apply during a live login swapped values mid-flow.
                Thread.Sleep(burst2ActivationSettleMs);
                FileLogger.Info($"AutoLogin: BURST 2 activated for PID {pid}");
                CombinedPressKey(writer, pid, hwnd, 0x0D); // Enter = confirm
                // v3.15.2: post-keystroke dwell tunable via Launch.Burst2PostKeystrokeMs (default 500).
                // v3.20.0 R2: snapshotted value (matches activation-settle fix above).
                Thread.Sleep(burst2PostKeystrokeMs);
                writer.Deactivate(pid); // ← OFF
                FileLogger.Info($"AutoLogin: BURST 2 deactivated for PID {pid}");
            }

            // ── v3.17.0 (2026-05-14): post-BURST-2 fast-failure detection ──
            // Before sinking into the 90s WaitForScreenTransition wait, poll for
            // ~10s for ANY sign EQ is advancing past the login screen (gameState
            // bump or window-rect size change). If no signal, BURST 1 credentials
            // were almost certainly rejected (truncation, user-input collision,
            // bad password, account already in-use) — fast-fail to the retry loop
            // instead of waiting the full 90s. Addresses Nate's stated symptom
            // 2026-05-14: "i have not seen it retry typing a password if the first
            // try only typed 4 chars". Tunable via Launch.PostBurst2QuickFailCheckMs
            // (default 10000; set 0 to disable + restore legacy 90s-only behavior).
            bool quickFailDetected = false;
            if (postBurst2QuickFailCheckMs > 0 && hwnd != IntPtr.Zero)
            {
                Report("Verifying login response...");
                quickFailDetected = !PollForLoginAdvance(charSelect, pid, hwnd, postBurst2QuickFailCheckMs);
                if (quickFailDetected)
                {
                    FileLogger.Warn($"AutoLogin: {account.Name} fast-fail detected post-BURST-2 — short-circuiting 90s screen wait to 5s confirm");
                }
            }

            // ── Wait for charselect load (no focus-faking, 5-60+ seconds) ──
            // Adaptive timeout: 90s default, or 5s if fast-fail signal already
            // told us the login didn't advance (just need to confirm before
            // entering retry loop).
            //
            // v3.20.9 SHM short-circuit (post-smoke fix 2026-05-15): when the
            // char-select SHM signal is already active (mq2Available + ReadCharCount
            // > 0), we KNOW we're past login. Skip the rect-based WaitForScreenTransition
            // entirely — on Dalaya gameState stays at 0 across login → server-select
            // → char-select, and the EQ window doesn't reliably change size at the
            // login→charselect boundary. Both signals WaitForScreenTransition relies
            // on (hung→responsive transition, rect size change) routinely miss the
            // transition, so the 90s timeout fires, the retry path kicks in, fires
            // Enter at char-select (where it becomes Enter World on the default-
            // highlighted slot 0 character), and the wrong char enters world. This
            // is exactly the failure mode v3.20.7's PollForLoginAdvance was meant to
            // prevent — but PollForLoginAdvance only short-circuited the post-BURST-2
            // verify probe (line above), not the subsequent WaitForScreenTransition.
            // Same canonical "we're at char-select" signal applied here closes the gap.
            //
            // Observed 2026-05-15 smoke: PollForLoginAdvance saw the SHM signal at
            // 17171ms → logged "char-select SHM advance detected" → WaitForScreenTransition
            // then ran for 90000ms → retry → Enter World fired on slot 0 instead of
            // the configured `Backup` (slot 1). Row-anchor fix in HandleSelectionRequest
            // (v3.20.8) never ran because RequestSelectionBySlot was never called.
            Report("Loading character select...");
            int initialTimeoutMs = quickFailDetected ? 5000 : 90000;
            var transitionSw = System.Diagnostics.Stopwatch.StartNew();
            bool charSelectShmActive = charSelect.IsMQ2Available(pid)
                                        && charSelect.ReadCharCount(pid) > 0;
            if (charSelectShmActive)
            {
                FileLogger.Info($"AutoLogin: {account.Name}: char-select SHM signal active — skipping WaitForScreenTransition (PID {pid}, charCount={charSelect.ReadCharCount(pid)})");
                Thread.Sleep(_config.Launch.WaitTransitionSettleMs);
                hwnd = RefreshHandle(pid, hwnd);
            }
            else
            {
                hwnd = WaitForScreenTransition(pid, hwnd, initialTimeoutMs);
            }
            LogWidgetSnapshot(loginShm, pid, "post-wst-primary");
            transitionSw.Stop();
            bool hitTimeout = transitionSw.ElapsedMilliseconds >= initialTimeoutMs - 500;

            // ── v3.17.0: bounded retry loop (was: single inline if-block) ──
            // Retry-loop architecture replaces the v3.15.x one-shot recovery
            // block. Tunable via Launch.ConnectRetryCount (default 1 = matches
            // v3.15.x behavior). Each iteration:
            //   1. State-aware modal dismiss — only press Enter if EQ is STILL
            //      on the login screen (gameState ≤ 1). If gameState advanced
            //      but timeout hit anyway (rare), skip the Enter — blind Enter
            //      into wrong screens has killed EQ (2026-05-10 incident:
            //      gotquiz1 EQ died 3s after retry submit; per
            //      feedback_eqswitch_no_yesno_in_patchme: "Dalaya's EULA
            //      defaults Enter focus to DECLINE — closed the game on every
            //      test").
            //   2. Cancellable recovery sleep — Thread.Sleep(30000) was
            //      undetectable as a 30s window where EQ could die without C#
            //      noticing (2026-05-10 incident: gotquiz EQ exited DURING the
            //      sleep, C# slept on a corpse). Now polls Process.HasExited.
            //   3. Re-fire BURST 1 via RunCredentialEntry — parity with primary
            //      path (Combo G + ScreenMode swap via warmup path is skipped
            //      here by passing loginShm: null; pure-keystroke retry is the
            //      empirically-working Dalaya path per the BURST 1 comment
            //      block). Inherits future RunCredentialEntry improvements.
            //   4. BURST 2 (server-confirm Enter) — kept inline; trivial.
            //   5. Re-wait WaitForScreenTransition with 60s cap.
            //   6. Loop until hitTimeout==false OR retry budget exhausted.
            int retryCap = Math.Max(0, connectRetryCount);
            int retryAttempt = 0;
            while (hitTimeout && hwnd != IntPtr.Zero && retryAttempt < retryCap)
            {
                retryAttempt++;
                Report($"{account.Name}: transition timed out — retry {retryAttempt}/{retryCap}");
                FileLogger.Warn($"AutoLogin: {account.Name} retry {retryAttempt}/{retryCap} starting (initial timeout = {initialTimeoutMs}ms, elapsed = {transitionSw.ElapsedMilliseconds}ms)");

                // v3.18.0: read LIVE OK_Display SHM mirror at retry-loop entry.
                // The native always-on probe (login_state_machine.cpp's
                // PollOkDisplayToShm) publishes dialog text + classification on
                // every poll regardless of state-machine phase, so this read is
                // valid even though today's PATH B keystroke retry runs with
                // the DLL state machine at PHASE_IDLE. None when no dialog up
                // (DLL clears the field) or when LoginShm wasn't opened
                // (loginShm == null bare-fallback case — handled by the
                // null-coalesce below).
                //
                // Snapshot at retry-loop entry for race safety, alongside the
                // R3-snapshotted tunables at the RunLoginSequence header. The
                // text + class can change tick-to-tick if EQ dismisses the
                // dialog mid-recovery, but we make the dispatch decision once
                // per retry iteration based on the entry-snapshot value.
                //
                // R2 (v3.18.0 verifier T2-O #14 + T3-O P3): use the atomic
                // ReadOkDisplaySnapshot helper instead of two separate reads.
                // It performs a class-text-class read and detects torn writes
                // (class differs across the bracketing reads), returning None
                // on detected race. Without this, "class=Fatal text=''" or
                // "class=None text='Invalid Password'" pairings were possible.
                var okSnapshot = loginShm?.ReadOkDisplaySnapshot(pid) ?? (OkDisplayClass.None, "");
                OkDisplayClass okClass = okSnapshot.Class;
                string okText = okSnapshot.Text;
                // R2 (v3.18.0 verifier T3-S MED + T3-O P1 #5): redact log
                // line. EQ may surface dialogs that echo username back (e.g.,
                // "User <name> entered an invalid password" — speculative for
                // Dalaya but real for some EQ servers). Mirror the v3.15.6
                // native-side credential-redaction stance. Log class + text
                // length; full text remains accessible via the SHM mirror for
                // the in-process classification logic that already saw it.
                //
                // R3 (v3.18.0 verifier T3-O LOW): gate the log on non-trivial
                // snapshot — class==None && length==0 is the common happy-path
                // "no dialog up" tick and produces ≤connectRetryCount info
                // lines per login that convey nothing. Skip those.
                if (okClass != OkDisplayClass.None || okText.Length > 0)
                {
                    FileLogger.Info($"AutoLogin: retry {retryAttempt} OK_Display snapshot — class={okClass}, text.length={okText.Length}");
                }

                // Fatal classification → no further retries can help. Re-typing
                // doesn't fix "Invalid Password" or "you need to enter a username
                // and password". Break out of the retry budget entirely so the
                // user gets the actual error surfaced instead of N retries
                // burning ~30s each before the same fatal dialog appears.
                //
                // R3 (v3.18.0 verifier T2-S REJECT): redact okText at ALL three
                // sites. R2 only covered the info-level snapshot log above; the
                // Fatal branch's FileLogger.Error and user-facing Report still
                // surfaced verbatim text. Per the v3.15.6 redaction stance: if
                // future EQ dialogs echo creds back, we want zero leak surface.
                // Log a length-only diagnostic; surface a generic class-driven
                // message to the user. The classification (Fatal) is itself
                // sufficient to drive the abort — the WHY is debug-only.
                if (okClass == OkDisplayClass.Fatal)
                {
                    FileLogger.Error($"AutoLogin: {account.Name} retry {retryAttempt} aborted — fatal OK_Display class (text.length={okText.Length})");
                    Report($"{account.Name}: login rejected — credentials problem (check eqswitch.log for diagnostic)");
                    break;
                }

                // (1) State-aware modal dismiss — gate on observable EQ state.
                //
                // ⚠️ DALAYA gameState VALUES ARE PARTIALLY UNKNOWN. Native/login_state_machine.cpp
                // lines 30-36 explicitly say: "Known from DLL log: login screen = 0,
                // charselect = ?, ingame = ?" and "Strategy: don't gate on gameState
                // for login screen — gate on widget presence." mq2_bridge.cpp may
                // report gameState=0 for BOTH login AND charselect (Dalaya RoF2 mapping
                // differs from modern MQ2). Therefore the `<= 1` gate cannot reliably
                // distinguish "on login screen" from "on charselect" — both may be 0.
                //
                // Practical implication: when we reach this retry path, WaitForScreenTransition
                // *just timed out* without observing the charselect-load signal (hung→responsive
                // OR window-rect size change). On Dalaya, that signal IS load-bearing for
                // charselect detection — if we got here, we're almost certainly still on the
                // login screen. The gameState check is a SUPPLEMENTAL safety: refuse dismiss
                // on a definitively-not-login state (gameState=5 in-game).
                //
                // Additionally, ReadGameState returns -1 as the "PID not mapped / SHM not
                // ready" sentinel — treat that as "don't know, fail safe to NOT dismiss".
                // Honest acknowledgement: this gate doesn't protect against the EULA case
                // (EULA likely has gameState=0 same as login). The right structural fix
                // is a native widget-name probe (deferred to v3.18 + SHM v3).
                int currentState = charSelect.ReadGameState(pid);
                bool safeDismiss = currentState >= 0 && currentState <= 1;
                if (safeDismiss)
                {
                    writer.Activate(pid, suppress: true);
                    Thread.Sleep(300);
                    CombinedPressKey(writer, pid, hwnd, 0x0D);
                    Thread.Sleep(300);
                    writer.Deactivate(pid);
                    FileLogger.Info($"AutoLogin: retry {retryAttempt} modal-dismiss Enter sent (PID {pid}, gameState={currentState})");
                }
                else
                {
                    FileLogger.Info($"AutoLogin: retry {retryAttempt} SKIPPED modal-dismiss (PID {pid}, gameState={currentState} — sentinel or post-login state; blind Enter would risk EQ exit / wrong-screen submission)");
                }

                // (2) Cancellable stale-session wait. Short-circuit on EQ death.
                //
                // v3.18.0 (2026-05-15): tune the wait based on the OK_Display
                // text snapshot taken at retry-loop entry (above). The v3.17.0
                // code path always slept staleSessionWaitMs (default 30s) and
                // explicitly noted that distinguishing required this SHM bump.
                //
                // Tuning rules (case-insensitive substring match):
                //   "stale" / "still logged in" / "in use" → keep full
                //     staleSessionWaitMs (30s default) — server is holding
                //     the session slot and needs time to release.
                //   "truncated" / "incomplete" → 1000ms — credentials were
                //     mangled (e.g. layout-skip), short wait suffices.
                //   anything else (Recoverable but unrecognized text, or no
                //     dialog at all) → fall back to staleSessionWaitMs.
                //
                // Success class falls through to the default — the "Logging
                // in to the server" message means EQ is mid-handshake and
                // re-typing immediately would just collide with an in-flight
                // login attempt.
                int tunedRecoveryWaitMs = staleSessionWaitMs;
                string tuningReason = "default (no text classification)";
                // v3.20.11: gate credential retype on this flag (audit gap #3 — MQ2
                // never retypes creds on retry; it only clicks buttons. Retype is
                // only safe when server explicitly classifies creds as truncated/
                // incomplete — i.e. the server PROVES the wire wasn't right, so
                // re-sending with field-clear actually helps. Every other case
                // (stale session, unknown recoverable, no dialog) shouldn't retype
                // — re-typing the same correct password at the same login screen
                // doesn't change the outcome, and any race where chars entered
                // world mid-retry leaks password chars as in-game keystrokes.
                bool credsTruncated = false;
                if (okClass == OkDisplayClass.Recoverable && !string.IsNullOrEmpty(okText))
                {
                    string lower = okText.ToLowerInvariant();
                    // v3.20.11 (T2 Sonnet+Opus conf 85 convergent finding 2026-05-15):
                    // tighten substring match to require co-occurrence of
                    // "password"/"credential" + "truncated"/"incomplete". Prevents
                    // false-positive matches against unrelated server messages
                    // like "Your connection was truncated" or "character file
                    // appears incomplete" that have nothing to do with creds —
                    // a spurious retype is exactly the keystroke-leak vector this
                    // release closes. Trade-off: false-negative (real truncated-
                    // creds dialog uses different wording than what we anchor on)
                    // is safer than false-positive — autologin gives up cleanly
                    // instead of typing passwords into wrong contexts.
                    bool hasPasswordRef = lower.Contains("password") || lower.Contains("credential");
                    bool hasTruncationRef = lower.Contains("truncated") || lower.Contains("incomplete");
                    if (hasPasswordRef && hasTruncationRef)
                    {
                        tunedRecoveryWaitMs = 1000;
                        tuningReason = "truncated/incomplete creds";
                        credsTruncated = true;
                    }
                    else if (lower.Contains("stale") || lower.Contains("still logged") || lower.Contains("in use"))
                    {
                        tunedRecoveryWaitMs = staleSessionWaitMs;
                        tuningReason = "stale session held";
                    }
                    else
                    {
                        // R3 (v3.18.0 verifier T2-S REJECT): don't embed okText
                        // verbatim. Log length only — the diagnostic value of
                        // "we got a recoverable dialog we don't recognize" is
                        // captured by the length + the fact that the bucket
                        // fell through. Future expansion of patterns is the
                        // right fix path (CHANGELOG "deferred for empirical
                        // capture"), not leaking unrecognized text via logs.
                        tuningReason = $"recoverable (unrecognized text, length={okText.Length})";
                    }
                }
                FileLogger.Info($"AutoLogin: retry {retryAttempt} recovery wait = {tunedRecoveryWaitMs}ms — {tuningReason}");
                // v3.20.10: pass charSelect so the helper can poll gameState==5 mid-wait
                // and exit early on in-world. Saves the remainder of tunedRecoveryWaitMs
                // when chars enter world during the sleep (common path when step-1's
                // modal-dismiss Enter landed Enter World on the default-selected char).
                var recoveryOutcome = CancellableSleepUntilProcessDies(
                    pid, tunedRecoveryWaitMs, staleSessionPollIntervalMs, charSelect);
                if (recoveryOutcome == RecoveryWaitOutcome.ProcessDied)
                {
                    FileLogger.Warn($"AutoLogin: {account.Name} EQ process exited during retry {retryAttempt} recovery wait — aborting");
                    Report($"{account.Name}: EQ exited during recovery wait — aborting");
                    return;
                }
                hwnd = RefreshHandle(pid, hwnd);
                if (hwnd == IntPtr.Zero)
                {
                    FileLogger.Warn($"AutoLogin: {account.Name} lost EQ window post-recovery-sleep on retry {retryAttempt} — aborting");
                    Report($"{account.Name}: lost EQ window during retry {retryAttempt} recovery");
                    return;
                }

                // v3.20.9 in-game keystroke gate (post-smoke fix 2026-05-15):
                // if the character is already in-world (because the modal-dismiss
                // Enter at step (1) above happened to land Enter World on the
                // default-highlighted slot-0 character at char-select, then the
                // 30s recovery sleep let EQ fully load the zone), abort the
                // retry now — re-typing credentials in-game fires the password
                // characters as keybind keystrokes, hitting whatever EQ keybinds
                // they map to (Nate 2026-05-15: passwords containing 'd' triggered
                // DUCK on both clients). IsInGame checks gameState==5 OR window
                // title containing " - " (the post-EnterWorld "EverQuest - CharName"
                // pattern) — both signals fire post-zone-load even on Dalaya where
                // gameState stays at 0 through char-select. Defense in depth: the
                // SHM short-circuit at line ~1015 should prevent retry from running
                // at all on Dalaya now, but if it ever does, this gate stops the
                // keystroke leak before BURST 1 / BURST 2 retyping.
                //
                // v3.20.10: triggers on EITHER the sleep-helper's gameState==5
                // mid-wait detection OR the post-sleep IsInGame check (which adds
                // the title-flip " - " signal as a fallback for any case where
                // gameState lags behind the title — e.g., non-Dalaya servers).
                if (recoveryOutcome == RecoveryWaitOutcome.InGame || IsInGame(charSelect, pid, hwnd))
                {
                    string source = recoveryOutcome == RecoveryWaitOutcome.InGame
                        ? "during recovery wait (sleep-helper gameState==5)"
                        : "post-recovery-wait (IsInGame title/gameState check)";
                    FileLogger.Info($"AutoLogin: {account.Name} retry {retryAttempt} aborted — character already in-world {source}. Would fire passwords as in-game keystrokes (DUCK / SwitchKey / etc.); treating retry as success.");
                    Report($"{account.Name} already in-game during retry — retry skipped");
                    hitTimeout = false;
                    break;
                }

                // (3) Re-fire BURST 1 via shared RunCredentialEntry. Pass loginShm: null
                // to skip the Combo G warmup ritual (already attempted on primary;
                // its result is whatever it was — retry leans on keystroke path).
                // Pass loginScreenDelayMs=0 / warmupDwellMs=0 — already on the
                // login screen, no further dwell needed. Burst1ActivationSettleMs
                // is the inner settle and covers the SHM/coop window.
                //
                // ⚠️ FIELD-CLEAR BEFORE RETRY TYPING: if primary's Combo G structural
                // write to `CEditBaseWnd::InputText+0x1A8` actually landed (v3.16.0
                // ScreenMode swap path), the password field already holds N chars.
                // RunCredentialEntry's PRIMER backspace clears ONE char only — re-
                // typing would concatenate, producing `<N-1 leftover><password>` and
                // a definitively-wrong submission. Pre-flush with 16 Backspaces.
                //
                // Coverage: 16 Backspaces clears up to 16 chars from cursor-back.
                // Passwords up to 16 chars are fully covered. Per Native/login_shm.h
                // line 45, LOGIN_PASS_LEN=128 is the upper bound — passwords above 16
                // chars where Combo G structural write succeeded would still concatenate
                // on retry (filed as v3.17.1 follow-up; in practice Dalaya passwords
                // are typically <16 chars).
                //
                // Two-field clear for UseLoginFlag=false: when LaunchManager doesn't
                // auto-populate username via eqlsPlayerData.ini, BURST 1 types BOTH
                // username and password. On retry, both fields may have stale chars.
                // After clearing the currently-focused field, Tab to the other and
                // clear it too. UseLoginFlag=true case (Nate's primary setup): only
                // password field needs clearing — username is auto-populated by
                // LaunchManager and clearing it would break the auto-populate.
                // (T2-Opus R3 verifier catch 2026-05-14.)
                //
                // v3.20.11 (audit gap #3 — MQ2-canonical retry, 2026-05-15):
                // Only retype credentials if the server EXPLICITLY classified them
                // as truncated/incomplete (set by the okClass tuning block above).
                // For every other retry case — stale session, unknown recoverable
                // dialog, no dialog at all — skip the retype entirely. MQ2 never
                // retypes creds on retry (StateMachine.cpp:344-369); it only
                // clicks OK_OKButton. Same password at same screen produces same
                // outcome; the only thing retype does is open a keystroke-leak
                // window (if chars enter world during typing, password chars fire
                // as in-game keybinds — Nate 2026-05-15 observed 'd'→DUCK, 'x'→
                // bound action). The v3.20.9 IsInGame gate at line ~1275 protects
                // against confirmed in-world entries; the v3.20.11 per-keystroke
                // gate in CombinedTypeString protects against mid-typing transition;
                // this block-level skip eliminates the entire vector for the
                // common case.
                if (!credsTruncated)
                {
                    FileLogger.Info($"AutoLogin: {account.Name} retry {retryAttempt} — skipping credential retype (okClass={okClass}, credsTruncated=false). MQ2-canonical: re-typing same creds at same screen produces same outcome; keystroke-leak vector eliminated. Modal-dismiss Enter (step 1) + BURST 2 server-confirm Enter (step 4) carry the retry work.");
                    Thread.Sleep(postBurst1WaitMs);
                    // v3.20.11 (T3 Opus conf 85 finding 2026-05-15): post-skip
                    // IsInGame check. If chars entered world during the
                    // postBurst1WaitMs sleep (typical when modal-dismiss Enter
                    // at step 1 landed Enter World on default-highlighted char),
                    // exit retry as success now — preventing BURST 2's bare
                    // Enter at step 4 from firing in-world (mostly benign for
                    // Enter, but matches the defense-in-depth invariant: once
                    // in-world, send NO further keystrokes).
                    if (IsInGame(charSelect, pid, hwnd))
                    {
                        FileLogger.Info($"AutoLogin: {account.Name} retry {retryAttempt} skip-path detected in-world after postBurst1WaitMs sleep — exiting retry as success before BURST 2.");
                        hitTimeout = false;
                        goto postRetypeBlock;
                    }
                }
                else
                {
                    Report($"Retry {retryAttempt}: re-firing credentials...");
                    writer.Activate(pid, suppress: true);
                    Thread.Sleep(200);
                    for (int i = 0; i < 16; i++)
                    {
                        // v3.20.11: per-iteration in-game gate. If chars entered
                        // world during the field-clear (unlikely but possible on
                        // a fast laptop where the modal-dismiss Enter at step 1
                        // already advanced past char-select), abort the field
                        // clear — the Backspace keystrokes themselves don't fire
                        // EQ keybinds, but continuing to RunCredentialEntry would
                        // type password chars that DO fire keybinds. Check every
                        // 4 iterations to keep overhead minimal (4× SHM reads per
                        // field-clear vs zero before).
                        if ((i & 0x3) == 0 && IsInGame(charSelect, pid, hwnd))
                        {
                            FileLogger.Warn($"AutoLogin: {account.Name} retry {retryAttempt} field-clear ABORTED mid-loop at Backspace #{i} — detected in-world (gameState==5 OR title-has-dash). {16 - i} Backspaces NOT sent. Skipping credential retype to prevent keystroke leak.");
                            writer.Deactivate(pid);
                            hitTimeout = false;
                            goto postRetypeBlock;
                        }
                        CombinedPressKey(writer, pid, hwnd, 0x08); // Backspace
                        Thread.Sleep(15);
                    }
                    if (!account.UseLoginFlag)
                    {
                        // Tab to the OTHER field and clear it. Direction (username↔password)
                        // depends on which field had focus first — either way, after a Tab,
                        // we're in the other one. A trailing Tab to return to the original
                        // field isn't needed because RunCredentialEntry's flow Shift-resets
                        // focus via its own Tab sequence.
                        CombinedPressKey(writer, pid, hwnd, 0x09); // Tab
                        Thread.Sleep(100);
                        for (int i = 0; i < 16; i++)
                        {
                            if ((i & 0x3) == 0 && IsInGame(charSelect, pid, hwnd))
                            {
                                FileLogger.Warn($"AutoLogin: {account.Name} retry {retryAttempt} second-field-clear ABORTED mid-loop at Backspace #{i} — detected in-world. {16 - i} Backspaces NOT sent.");
                                writer.Deactivate(pid);
                                hitTimeout = false;
                                goto postRetypeBlock;
                            }
                            CombinedPressKey(writer, pid, hwnd, 0x08); // Backspace
                            Thread.Sleep(15);
                        }
                    }
                    writer.Deactivate(pid);
                    int clearCount = account.UseLoginFlag ? 16 : 32;
                    FileLogger.Info($"AutoLogin: retry {retryAttempt} pre-typing field-clear ({clearCount}x Backspace, UseLoginFlag={account.UseLoginFlag}) for PID {pid}");

                    // v3.20.11: belt-and-braces in-game check before RunCredentialEntry.
                    // CombinedTypeString has its own per-keystroke gate (passed via
                    // charSelect param), but checking here too lets us short-circuit
                    // the entire BURST 1 / RunCredentialEntry call.
                    if (IsInGame(charSelect, pid, hwnd))
                    {
                        FileLogger.Warn($"AutoLogin: {account.Name} retry {retryAttempt} BURST 1 retype ABORTED before RunCredentialEntry — detected in-world post-field-clear.");
                        hitTimeout = false;
                        goto postRetypeBlock;
                    }

                    RunCredentialEntry(pid, hwnd, writer, loginShm: null, account, password,
                        loginScreenDelayMs: 0, warmupDwellMs: 0,
                        targetCharacterName: character?.Name ?? "",
                        charSelect: charSelect);

                    Thread.Sleep(postBurst1WaitMs);
                    hwnd = RefreshHandle(pid, hwnd);
                    if (hwnd == IntPtr.Zero)
                    {
                        FileLogger.Warn($"AutoLogin: {account.Name} lost EQ window mid-retry-{retryAttempt} BURST 1 — aborting");
                        Report($"{account.Name}: lost EQ window during retry {retryAttempt} BURST 1");
                        return;
                    }
                }

                postRetypeBlock:
                // Common exit path. If hitTimeout==false (set by any in-game
                // detection above), the outer while loop will exit on the next
                // iteration check. Otherwise fall through to step (4) BURST 2 +
                // step (5) WaitForScreenTransition.
                if (!hitTimeout)
                {
                    Report($"{account.Name} already in-game during retry — retry skipped");
                    break;
                }

                // (4) Re-fire server confirm — Diff 4 first, BURST 2 fallback.
                // Verifier-fix 2026-05-15 (T2-S/T2-O/T3-O convergent):
                // pre-fix the retry path was inline BURST 2 only, bypassing
                // JoinServerDirect entirely. Asymmetric reliability between
                // primary (Diff 4) and retry (BURST 2). Now both paths share
                // TryJoinServerDirectOrFallback — identical contract.
                //
                // v3.20.11 (T2 Sonnet conf 90 + T3 Sonnet I3 conf 80 convergent
                // finding 2026-05-15): use R3-snapshotted `joinServerId` (line
                // ~965) instead of live `_config.Launch.JoinServerId`. The retry
                // path runs inside the same RunLoginSequence scope and inherits
                // the snapshot discipline established by v3.17.0 — a mid-retry
                // Settings → Apply could otherwise swap JoinServerId between
                // primary and retry paths. Pre-existing bug; fixed while in
                // verifier-driven hardening pass.
                int retryJoinServerId = joinServerId;
                bool retryJoinSucceeded = TryJoinServerDirectOrFallback(
                    loginShm, pid, retryJoinServerId, account, isRetry: true);
                if (!retryJoinSucceeded)
                {
                    Report($"Retry {retryAttempt}: confirming server...");
                    writer.Activate(pid, suppress: true);
                    Thread.Sleep(burst2ActivationSettleMs);
                    FileLogger.Info($"AutoLogin: RETRY {retryAttempt} BURST 2 activated for PID {pid}");
                    CombinedPressKey(writer, pid, hwnd, 0x0D);
                    Thread.Sleep(burst2PostKeystrokeMs);
                    writer.Deactivate(pid);
                    FileLogger.Info($"AutoLogin: RETRY {retryAttempt} BURST 2 deactivated for PID {pid}");
                }

                // (5) Re-wait for screen transition — 60s cap. Post-retry also
                // gets the fast-fail probe so a second-retry-truncation hits the
                // next retry iteration quickly instead of waiting another 60s.
                //
                // v3.20.10 SHM short-circuit (mirror of the primary path's check
                // at ~line 1015): if the char-select SHM signal is already active
                // when we reach this step, skip WaitForScreenTransition's 60s
                // rect-based wait — on Dalaya that wait would time out unconditionally
                // because gameState stays at 0 and the window doesn't reliably
                // change size. Symmetric with the primary-path fix from v3.20.9.
                bool retryQuickFail = false;
                if (postBurst2QuickFailCheckMs > 0)
                {
                    retryQuickFail = !PollForLoginAdvance(charSelect, pid, hwnd, postBurst2QuickFailCheckMs);
                }
                Report($"Retry {retryAttempt}: loading character select...");
                int retryTimeoutMs = retryQuickFail ? 5000 : 60000;
                transitionSw.Restart();
                bool retryCharSelectShmActive = charSelect.IsMQ2Available(pid)
                                                 && charSelect.ReadCharCount(pid) > 0;
                LogWidgetSnapshot(loginShm, pid, $"retry{retryAttempt}-pre-wst");
                if (retryCharSelectShmActive)
                {
                    FileLogger.Info($"AutoLogin: {account.Name}: char-select SHM signal active on retry {retryAttempt} — skipping WaitForScreenTransition (PID {pid}, charCount={charSelect.ReadCharCount(pid)})");
                    Thread.Sleep(_config.Launch.WaitTransitionSettleMs);
                    hwnd = RefreshHandle(pid, hwnd);
                }
                else
                {
                    hwnd = WaitForScreenTransition(pid, hwnd, retryTimeoutMs);
                }
                transitionSw.Stop();
                hitTimeout = transitionSw.ElapsedMilliseconds >= retryTimeoutMs - 500;
                // Note: quickFailDetected is no longer read after this point —
                // each retry iteration computes its own retryQuickFail fresh.
                // The v0 version assigned `quickFailDetected = retryQuickFail`
                // here with a "propagate" comment, but that propagation was
                // dead code — no later read consumed it (verifier T1-Opus
                // 2026-05-14). Removed.

                if (!hitTimeout)
                {
                    FileLogger.Info($"AutoLogin: {account.Name} retry {retryAttempt}/{retryCap} succeeded — charselect loaded after {transitionSw.ElapsedMilliseconds}ms");
                }
            }

            if (hitTimeout)
            {
                string retryDesc = retryCap == 0 ? "no retries configured" : $"{retryAttempt}/{retryCap} retr{(retryCap == 1 ? "y" : "ies")} exhausted";
                Report($"{account.Name}: char select didn't load ({retryDesc}) — check password / server / network");
                FileLogger.Error($"AutoLogin: WaitForScreenTransition timeout — {retryDesc} for {account.Name} — aborting login");
                // Login-status indicator: AutoLoginManager-owned timeout. The hwnd-zero
                // path below is process death (EQ crashed), which we deliberately don't
                // mark — it's not the password's fault. See risk register in
                // memory/project_eqswitch_account_login_status_tracking.md.
                //
                // Write ORDER: LastLoginAt first, LastLoginResult last. The UI thread
                // reads LastLoginResult to decide the glyph and only then dereferences
                // LastLoginAt for the tooltip. Nullable<DateTime> is 16 bytes on x64
                // (not atomic); writing it BEFORE the atomic string-reference assignment
                // means a UI-thread reader that sees a non-default LastLoginResult is
                // guaranteed (under x64 memory ordering) to see the matching LastLoginAt.
                // v3.22.25: locked against ApplySettings/ReloadConfig for torn-JSON
                // defense. Note: legacy path lacks the SM-path's re-resolve — orphan-ref
                // risk if Settings swapped _config.Accounts between SM launch and here.
                // Legacy is not the deployed path (UseStateMachine=true everywhere),
                // so re-resolve is intentionally deferred to a separate fix if ever needed.
                SaveLastLoginResultLocked(account, "fail", "AutoLogin-legacy-fail");
                return;
            }
            if (hwnd == IntPtr.Zero) { Report($"{account.Name}: lost EQ window during charselect load (crashed or closed)"); return; }
            FileLogger.Info($"AutoLogin: charselect ready, hwnd=0x{hwnd:X} for PID {pid}");
            LogWidgetSnapshot(loginShm, pid, "charselect-reached");
            // Login-status indicator: WaitForScreenTransition succeeded → password
            // was accepted by the login server AND char-select rendered. Everything
            // past this point is character-selection / enter-world plumbing, not
            // login. Mark ok now so a downstream MQ2-bridge timeout doesn't downgrade
            // a successful login to ✗. Write ordering + SaveImmediate rationale:
            // see the matching "fail" branch above.
            SaveLastLoginResultLocked(account, "ok", "AutoLogin-legacy-ok");

            // ── Enter World gate ──
            // Default intent from type: Character target = enter world, Account-only = stop here.
            // enterWorldOverride is kept as a parameter for future extension; current
            // callers always pass null (use the Character's default) — team-level
            // overrides were removed when destination became kind-only.
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
            // v3.15.8: tunable via Launch.BridgeInitWaitMs (default 1ms, was 2000 pre-v3.15.8).
            // Vestigial settle pause — the wait loop below is the actual bridge-readiness
            // gate (its first iteration polls without delay and its inter-poll 500ms sleep
            // absorbs any genuine bridge lag). Cutting 2000→1 saves ~2s in the success path
            // with no functional change; failure path is identical.
            Thread.Sleep(_config.Launch.BridgeInitWaitMs);

            bool wantSelection = character!.CharacterSlot > 0 || !string.IsNullOrEmpty(character.Name);
            if (wantSelection)
            {
                bool charListReady = false;
                bool singleCharSlotFallback = false;
                bool alreadyInGame = false;
                // Wait up to 30s for char list — Dalaya's CXWndManager populates
                // ~25s after charselect screen appears (pinstCCharacterSelect timing).
                //
                // v3.15.x gate: PRIMARY = `charSelectReady` latch AND charCount > 0.
                // SECONDARY (single-char fallback) = charCount == 1 AND wait >= 20.
                //
                // Why two paths:
                //  - charCount > 0 alone trips on Path B2's "Slot N" placeholder
                //    (Path B2 sets count=1 and writes shm->charCount=1 BEFORE the
                //    in-poll Path C anchor scan finishes; C# could read "Slot 1"
                //    before bridge overwrites with the real name).
                //  - The latch is set ONLY at the 5 real-name publish sites
                //    (Path A struct, Path C heap-array, Path C anchor, standalone
                //    heap-array, standalone anchor). Never on Path B2 placeholders.
                //  - Single-char Dalaya accounts have flaky heap visibility for
                //    the name string (anchor scan budget-exceeds without finding
                //    'Natedogg' in some sessions). Path B2's SetCurSel/GetCurSel
                //    slot probe is structurally reliable — count=1 means the
                //    server says exactly one character on this account. After
                //    10s grace (wait >= 20) for the anchor to land, fall back to
                //    slot 1 by elimination IF user has a name target and no
                //    explicit slot binding.
                for (int wait = 0; wait < 60; wait++)
                {
                    if (charSelect.IsMQ2Available(pid))
                    {
                        int count = charSelect.ReadCharCount(pid);
                        bool latch = charSelect.IsCharSelectReady(pid);
                        if (count > 0 && latch) { charListReady = true; break; }
                        // Single-char structural fallback: see comment above.
                        if (count == 1 && wait >= 20 &&
                            character.CharacterSlot == 0 &&
                            !string.IsNullOrEmpty(character.Name))
                        {
                            charListReady = true;
                            singleCharSlotFallback = true;
                            FileLogger.Warn($"AutoLogin: single-char structural fallback — bridge couldn't confirm real names from heap after {wait * 500}ms (latch=0), but Path B2 slot probe says exactly 1 character. Treating slot 1 as target '{character.Name}' for PID {pid}.");
                            break;
                        }
                    }

                    // User already in-game (manual Enter, prior session race, etc.)
                    // — exit the wait loop with a flag so the post-loop branch
                    // treats this as success (not as the "MQ2 bridge not ready"
                    // abort). Without `alreadyInGame`, control falls through to
                    // the else branch and the user gets a misleading error
                    // message even though we're actually in the world (R-final
                    // verifier convergent finding, T2-S/T2-O/T3-S/T3-O).
                    hwnd = RefreshHandle(pid, hwnd);
                    if (hwnd != IntPtr.Zero && IsInGame(charSelect, pid, hwnd))
                    {
                        FileLogger.Info($"AutoLogin: {account.Name}: already in-game during charlist wait — treating as success");
                        Report($"{account.Name} already in-game.");
                        alreadyInGame = true;
                        break;
                    }

                    Thread.Sleep(500);
                }

                if (alreadyInGame)
                {
                    // User reached world before our wait completed — nothing
                    // left to do for this PID.
                    return;
                }

                if (charListReady)
                {
                    // If the latch tripped the gate but charCount is transiently 0
                    // (bridge between cache-invalidation and next anchor scan), give
                    // the bridge ~1s to republish names. Each retry is one bridge
                    // poll-throttle cycle (500ms) so 4 retries ≈ 2s — generous.
                    for (int retry = 0; retry < 4 && charSelect.ReadCharCount(pid) == 0; retry++)
                    {
                        Thread.Sleep(500);
                    }

                    // Snapshot character list ONCE from ReadAllCharNames. `charCount` used by
                    // bounds checks below must equal the scan snapshot — ReadCharCount re-reads
                    // SHM and can diverge from charNames.Length if the DLL refreshes between
                    // the two reads (feature-dev review finding M1a). Using charNames.Length
                    // ensures the abort-on-out-of-range check aligns with what Decide scanned.
                    var charNames = charSelect.ReadAllCharNames(pid);
                    int charCount = charNames.Length;
                    FileLogger.Info($"AutoLogin: {charCount} characters found: {string.Join(", ", charNames)}");

                    int resolvedSlot;
                    bool resolvedByName;
                    string decisionLog;
                    if (singleCharSlotFallback && charCount == 1)
                    {
                        // Single-char structural fallback: Path B2 confirmed exactly
                        // one character on the account. Bridge couldn't read the
                        // name string from heap, but slot 1 is the target by
                        // elimination. Bypass CharacterSelector.Decide (it would
                        // return 0 because charNames[0] is a "Slot N" placeholder
                        // that won't match character.Name).
                        resolvedSlot = 1;
                        resolvedByName = false;
                        decisionLog = $"single-char structural fallback → slot 1 = '{character.Name}'";
                    }
                    else
                    {
                        (resolvedSlot, resolvedByName, decisionLog) = CharacterSelector.Decide(
                            character.CharacterSlot, character.Name, charNames);
                    }
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
                        // v3.15.9: poll granularity 200ms → 50ms (cap was 10s then;
                        // v3.24.7 raised it to 24s — see the loop bound below).
                        // The DLL writes the ack flag during EQ's game-thread tick (~16ms);
                        // 200ms granularity meant we noticed the ack up to 200ms after it
                        // actually fired. 50ms catches it 4× faster on average — saves
                        // ~150ms per ack-tick observed (typical ack arrives in 1-15 ticks
                        // = 200ms-3s on busy charselect-render frames).
                        //
                        // v3.15.11 instrumentation (2026-05-09): the native two-tier
                        // throttle made the bridge ack near-instant per its own log,
                        // but C# observed ~1.23s gap from request to "Entering world..."
                        // — diagnosing whether it's Windows Sleep granularity or
                        // cross-process SHM visibility. Stopwatch + iter counter +
                        // first-read ackSeq value tell us which.
                        var ackSw = System.Diagnostics.Stopwatch.StartNew();
                        bool ackedAtFirstRead = charSelect.IsSelectionAcknowledged(pid);
                        // firstReadUs: sub-millisecond cost of the SHM read alone. If
                        // ackedAtFirstRead==true AND firstReadUs is small, the bridge
                        // had already written ackSeq before C# even started polling —
                        // i.e., the fast-path bypass round-tripped within the time it
                        // took C# to call RequestSelection + start the Stopwatch.
                        long firstReadUs = ackSw.ElapsedTicks * 1_000_000L
                            / System.Diagnostics.Stopwatch.Frequency;
                        int totalIters = 0;
                        if (ackedAtFirstRead)
                        {
                            acked = true;
                            totalIters = 1;
                        }
                        else
                        {
                            // v3.24.7 — was 200 (10s). Propagates the v3.22.89 SM-path
                            // ack-starvation fix to the legacy path: the DLL runs SetCurSel
                            // on the GAME thread via TIMERPROC, which can stay busy >10s
                            // during char-select scene load and starve the ack — a
                            // game-client property that hits BOTH paths identically. Still
                            // ack-gated → no wrong-character enter-world risk; a longer
                            // budget just lets a late ack land instead of dead-stopping.
                            for (int ack = 0; ack < 480; ack++)  // 480x50ms = 24s
                            {
                                Thread.Sleep(50);
                                totalIters = ack + 2; // +2: 1 pre-loop read + (ack+1) in-loop iterations
                                if (charSelect.IsSelectionAcknowledged(pid))
                                { acked = true; break; }
                            }
                        }
                        ackSw.Stop();
                        if (acked)
                        {
                            FileLogger.Info($"AutoLogin: selection ack observed after {totalIters} iter(s) / {ackSw.ElapsedMilliseconds}ms (firstRead={ackedAtFirstRead}, firstReadUs={firstReadUs}, PID {pid})");
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
                            FileLogger.Error($"AutoLogin: DLL did not ack selection for slot {resolvedSlot} in 24s — stopping at charselect to avoid wrong-character enter-world");
                            Report($"{account.Name}: character selection not confirmed — stopped at char select");
                            return;
                        }
                        // v3.15.9: post-ack settle 200ms → 100ms. SetCurSel fires synchronously
                        // on game thread via TIMERPROC; the ack means TIMERPROC has already
                        // run, so 100ms is plenty for any downstream UI bookkeeping.
                        Thread.Sleep(100);
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

            // v3.15.11 (Target 2 Option A): empirical short-circuit on Dalaya.
            // CLW_EnterWorldButton isn't in the CXWnd tree by the time
            // charselect-ready is signaled, so all 4 SHM attempts return -1
            // and PulseKey3D fallback fires every time (~2-2.5s wasted on
            // failed retries). Gate is opt-out via Launch.SkipShmEnterWorld-
            // OnDalaya (default true). Other servers stay on the SHM-primary
            // path. Structural fix (bridge writes buttonReady flag) deferred
            // as Option B.
            bool skipShmEnterWorld = ShouldSkipShmEnterWorld(account);
            // Only log the skip when MQ2 was actually available — otherwise
            // we'd be using PulseKey3D regardless and the "skipping" log
            // line would be misleading.
            if (skipShmEnterWorld && charSelect.IsMQ2Available(pid))
                FileLogger.Info($"AutoLogin: skipping SHM Enter World on Dalaya (PID {pid}, account {account.Name}) — using PulseKey3D directly");

            // Primary path: SHM RequestEnterWorld (in-process button click)
            if (charSelect.IsMQ2Available(pid) && !skipShmEnterWorld)
            {
                // v3.15.9: attempts 2 → 4 + retry sleep 2000 → 500. result=-1 means
                // CLW_EnterWorldButton isn't in the CXWnd tree yet (charselect UI
                // still building after SetCurSel). Inter-attempt Sleep is gated
                // (see post-result-≠-1 branch), so 4 attempts produces 3 sleeps
                // = 1500ms of inter-retry budget (down from 2×2000=4000ms pre-fix
                // because the prior code wasted a 2000ms sleep AFTER the last
                // attempt). Net: 4× the polls AND ~2.5s less total SHM-path
                // wall-clock — strict win on the failure path.
                const int kMaxEnterWorldAttempts = 4;
                for (int attempt = 0; attempt < kMaxEnterWorldAttempts; attempt++)
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

                    // Wait for DLL ack (up to 5s).
                    // v3.15.9: granularity 200ms → 50ms (cap unchanged at 5s).
                    // The DLL acks on its next tick (~16ms); 50ms catches it 4× faster.
                    bool acked = false;
                    for (int w = 0; w < 100; w++)
                    {
                        if (charSelect.IsEnterWorldAcknowledged(pid)) { acked = true; break; }
                        Thread.Sleep(50);
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
                        // v3.15.9: retry sleep 2000 → 500, AND gate behind "more attempts left"
                        // — pre-fix the loop slept 2000ms after the LAST attempt before falling
                        // through to the PulseKey3D fallback (pure waste). Verified in v3.15.8
                        // log: attempt 2 ended at 50.789, fallback fired at 52.790 = exactly
                        // 2000ms wasted. With 4 attempts the gate produces 3 sleeps × 500ms =
                        // 1500ms inter-retry budget (down from 2×2000ms = 4000ms pre-fix), AND
                        // 4 polls for the button instead of 1.
                        if (attempt < kMaxEnterWorldAttempts - 1)
                            Thread.Sleep(500);
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
                if (charSelect.IsMQ2Available(pid) && skipShmEnterWorld)
                    FileLogger.Info("AutoLogin: PulseKey3D enter-world (SHM skipped per Launch.SkipShmEnterWorldOnDalaya)");
                else if (charSelect.IsMQ2Available(pid))
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
                // Clear autoLoginActive BEFORE closing the SHM so the native
                // DLL sees the flag drop on its next poll tick. Without this,
                // the SHM stays alive (DLL still has its own handle) but the
                // C# clear never fires — kPromptWindows would remain
                // suppressed for the rest of the process lifetime, defeating
                // EULA dismiss on any subsequent native re-entry.
                try { loginShm.SetAutoLoginActive(pid, false); }
                catch (Exception ex) { FileLogger.Warn($"AutoLogin: SetAutoLoginActive(false) failed for PID {pid}: {ex.Message}"); }
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
        // Iter 15.1: poll every 50ms instead of 200ms. The DLL's lazy SHM
        // open is now eager (iter 14.1) and the throttle-bypass-on-eqmain-LOAD
        // (iter 15) means the DLL acks within ~10ms of being able to. Tighter
        // poll picks up the ack faster instead of waiting up to 200ms.
        bool acked = false;
        for (int wait = 0; wait < 300; wait++) // 300 * 50ms = 15s
        {
            if (loginShm.IsCommandAcknowledged(pid)) { acked = true; break; }
            Thread.Sleep(50);
        }
        if (!acked)
        {
            FileLogger.Warn($"AutoLogin: DLL did not acknowledge LoginShm command in 15s for PID {pid}");
            loginShm.SendCancelCommand(pid);
            return false;
        }

        FileLogger.Info($"AutoLogin: DLL acknowledged LoginShm command for PID {pid}");

        // Monitor phase transitions (50ms poll, 45s overall timeout).
        // Iter 15.2 (2026-04-25): timeout extended 14s → 45s. Combo G now
        // writes the password successfully (verified via DLL log "set password
        // via Combo G"), but the 14s outer timeout was firing BEFORE the DLL
        // could observe the gameState transition through PHASE_WAIT_CONNECT_RESP
        // (login server response can take 10-20s on Dalaya). C# would CANCEL,
        // fall back to BURST 1 keystroke, and the user would see VISIBLE typing
        // even though Combo G had already filled the field. With Combo G
        // working reliably, 45s gives enough headroom for slow server response
        // without triggering the keystroke fallback.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LoginPhase lastReported = LoginPhase.Idle;

        while (sw.ElapsedMilliseconds < 45_000)
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
                    // Widget discovery via HeapScanForWidget legitimately takes
                    // ~5-6s on Dalaya — heap scan walks ~238 pages × 3 widgets.
                    // The prior 3s timeout was ALWAYS losing this race (verified
                    // 2026-04-24 across both b142afe baseline and post-Combo-G
                    // smoke test: CANCEL fired 16-50ms after widgets found in
                    // both runs). Extended to 10s so Combo G's
                    // WriteEditTextDirect actually gets a chance to run before
                    // C# falls back. The outer 14s timeout still catches the
                    // "DLL advanced past phase 1 but can't complete" case.
                    if (sw.ElapsedMilliseconds > 10_000)
                    {
                        FileLogger.Warn($"AutoLogin: LoginShm stuck at WaitLoginScreen for 10s — DLL widget discovery not working, falling back to keyboard");
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

            Thread.Sleep(50); // iter 15.1: was 200ms — tighter phase polling
        }

        // Overall timeout — routes to keyboard injection when DLL advances
        // past phase 1 but can't complete. The structural InputText write path
        // (Native/eqmain_cxstr.cpp WriteEditTextDirect) exists and is RoF2-emu
        // ABI-compatible on Dalaya, but is currently DORMANT — not wired into
        // login_state_machine.cpp. Until it ships, password entry falls through
        // to keystroke BURST 1.
        FileLogger.Error($"AutoLogin: LoginShm overall timeout (45s) for {account.Name}");
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

    /// <summary>
    /// Diff 4 (2026-05-15): consolidated JoinServerDirect dispatch shared
    /// by primary path (RunLoginSequence pre-BURST-2) AND retry path
    /// (RunLoginSequence retry loop pre-BURST-2-retry). Both paths now have
    /// identical Diff-4-first / BURST-2-fallback semantics.
    ///
    /// Returns true if BURST 2 should be SKIPPED (JoinServerDirect succeeded
    /// AND fnResult == 0 = network dispatch OK). Returns false to signal
    /// "fall back to BURST 2" — covers:
    ///   - JoinServerId == 0 (wire disabled by config)
    ///   - loginShm == null (SHM open failed earlier)
    ///   - outcome != Success (LoginServerAPI null, vtable mismatch, prologue
    ///     patch, SEH inside JoinServer call, 2s ack timeout, gated)
    ///   - outcome == Success but fnResult != 0 (EQ-side error code returned;
    ///     verifier-fix 2026-05-15: T2-O+T3-O convergent — non-zero fnResult
    ///     indicates the API dispatched but EQ refused the join, e.g. bad
    ///     server ID or server-not-in-list — BURST 2 fallback is the
    ///     correct compensation, not skipping it)
    /// </summary>
    private bool TryJoinServerDirectOrFallback(LoginShmWriter? loginShm, int pid,
        int joinServerId, Account account, bool isRetry)
    {
        if (loginShm == null || joinServerId <= 0)
        {
            // Wire disabled — silently fall through (no log spam; this is
            // the configured-disable path, not an error).
            return false;
        }

        string label = isRetry ? "Retry: " : "";

        // ── Fix 2 (v6 SHM, 2026-05-15) — LoginServerAPI-ready gate ─────
        //
        // BEFORE dispatching JoinServerDirect, wait for native to observe
        // pinstLoginServerAPI populated + vtable matching eqmain+0x1002D0
        // for >= 3 consecutive Ticks (stability counter inside native).
        //
        // The 2026-05-15 PM smoke showed JoinServerDirect dispatched on a
        // fixed PostBurst1WaitMs=3000ms timer returned fnResult=0x00000003
        // ("no auth session"). The auth handshake hadn't completed when
        // the dispatch fired — racing wall-clock against EQ's network
        // round-trip. This gate replaces wall-clock with auth-state.
        //
        // Timeout: 30000ms covers slow-auth scenarios on Dalaya's emu
        // login server. Empirical data from 2026-05-15 19:00 dual-box
        // smoke + probe_serverselectwnd.py at 19:08 (7 min later):
        // pinstLoginServerAPI was NULL at 19:01-19:02 (5s + 5s retry
        // window) and populated by 19:08. Actual ready-time appears
        // to be ~30-60s post-LOGIN-click on Dalaya, much slower than
        // the 1-3s historical estimate. The DLL's 3-tick stability
        // gate fires within ~48ms once pAPI populates, so an extended
        // C# poll lets the gate publish before timeout.
        //
        // v3.20.7 (2026-05-15): reverted to 5s. With QUICK CONNECT
        // (the new default structural button), auth + server-join are
        // submitted atomically by the button itself — there's no
        // distinct "wait for LoginServerAPI then JoinServerDirect"
        // step. pinstLoginServerAPI may never populate on the
        // shortened path (QUICK CONNECT lands at char-select directly).
        // Fast-fail to BURST 2 / PollForLoginAdvance is correct —
        // PollForLoginAdvance now has the char-select SHM signal that
        // recognizes "we're past login" without needing LSAPI ready.
        const int loginServerAPIReadyTimeoutMs = 5000;
        Report($"{label}Waiting for auth (LoginServerAPI ready)...");
        var readySw = System.Diagnostics.Stopwatch.StartNew();
        bool authReady = loginShm.WaitForLoginServerAPIReady(pid, loginServerAPIReadyTimeoutMs);
        readySw.Stop();
        if (!authReady)
        {
            FileLogger.Warn($"AutoLogin: {account.Name} LoginServerAPI not ready after " +
                $"{readySw.ElapsedMilliseconds}ms (timeout {loginServerAPIReadyTimeoutMs}ms) — " +
                $"skipping JoinServerDirect, falling back to BURST 2 (retry={isRetry})");
            return false;
        }
        FileLogger.Info($"AutoLogin: {account.Name} LoginServerAPI ready after " +
            $"{readySw.ElapsedMilliseconds}ms — dispatching JoinServerDirect");

        Report($"{label}Joining server (direct call)...");
        var (outcome, fnResult) = loginShm.TryJoinServerDirect(pid, (uint)joinServerId);

        if (outcome != LoginShmWriter.JoinServerOutcome.Success)
        {
            FileLogger.Warn($"AutoLogin: {account.Name} JoinServerDirect outcome={outcome} " +
                $"(serverID={joinServerId}, retry={isRetry}) — falling back to BURST 2");
            return false;
        }

        if (fnResult != 0)
        {
            // Native dispatched cleanly but EQ-side returned a non-zero error
            // code. Per login_shm.h:182-188, fnResult is JoinServer's actual
            // return value — 0 = network dispatch OK, non-zero = EQ-side
            // error (bad server ID, server-not-in-list, server full, etc.).
            // BURST 2 fallback gives EQ a second chance via the UI path that
            // doesn't depend on knowing the right server ID.
            FileLogger.Warn($"AutoLogin: {account.Name} JoinServerDirect dispatched but " +
                $"fnResult=0x{fnResult:X8} != 0 (serverID={joinServerId}, retry={isRetry}) " +
                $"— falling back to BURST 2");
            return false;
        }

        FileLogger.Info($"AutoLogin: {account.Name} JoinServerDirect succeeded " +
            $"(serverID={joinServerId}, fnResult=0, retry={isRetry}) — skipping BURST 2");
        return true;
    }

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
    private static TypingResult CombinedTypeString(KeyInputWriter writer, int pid, IntPtr hwnd, string text,
        CharSelectReader? charSelect = null)
    {
        // Diagnostic counters: tell us whether typing actually executed. Catches
        // the failure mode where DI8 SHM only sees the trailing Enter — if typed=0
        // here despite text.Length > 0, every char failed VkKeyScan / modifier
        // checks. If typed > 0 but the DI8 log shows no scancode injections,
        // the SHM write path is broken downstream (writer state / DLL / EQ pump).
        int typedCount = 0;
        int skippedCount = 0;
        FileLogger.Info($"AutoLogin: CombinedTypeString PID={pid} hwnd=0x{hwnd.ToInt64():X} input.Length={text.Length}");
        foreach (char c in text)
        {
            // v3.20.11 (MQ2-canonical gap #4 — per-keystroke in-game gate, audit
            // 2026-05-15): abort if chars transitioned to in-world mid-typing.
            // MQ2's OnPulse dispatcher (`MQ2AutoLogin.cpp:1149-1156`) branches on
            // `GetGameState() == GAMESTATE_INGAME` BEFORE ever dispatching login-
            // screen keystrokes — making the in-game gate structurally impossible
            // to bypass. Our per-burst gate (the v3.20.9 check before BURST 1
            // and the v3.20.10 mid-sleep gameState==5 check inside
            // CancellableSleepUntilProcessDies) leaves a window between the gate
            // check and the actual VK_KEYDOWN posts. If EQ enters world during
            // that window, password chars fire as in-game keybinds: 'd' → DUCK,
            // 'x' → bound action, etc. (Nate 2026-05-15).
            //
            // gameState==5 is the Dalaya-reliable in-world signal — gameState
            // stays at 0 through login → server-select → char-select on Dalaya,
            // and only flips to 5 once the zone-load completes. Reading SHM is
            // ~microsecond cost; per-char polling is cheap (≤50us total for a
            // 16-char password) vs the cost of leaked keystrokes.
            //
            // charSelect is optional — pre-v3.20.11 callers passing null preserve
            // the old behavior (no in-game gate). RunCredentialEntry and the
            // retry path pass it.
            //
            // v3.20.11 (T2 Opus Rule 7 finding): use IsInGame() which adds the
            // post-Enter-World " - " title-check fallback alongside gameState==5.
            // Belt-and-braces against the partially-unknown Dalaya gameState
            // semantics flagged in login_state_machine.cpp:30-36 — title flip
            // is a structurally-different signal from gameState that fires on
            // zone-load even when gameState lags.
            if (charSelect != null && IsInGame(charSelect, pid, hwnd))
            {
                int charsSent = typedCount;
                int charsRemaining = text.Length - typedCount - skippedCount;
                FileLogger.Warn($"AutoLogin: CombinedTypeString PID={pid} ABORTED mid-typing — detected in-world (gameState==5) after char #{charsSent}/{text.Length}. Remaining {charsRemaining} chars NOT sent — would have hit in-game keybinds. Caller should treat as keystroke-leak-prevented abort, not a retypeable error.");
                // Surface as incomplete to LogTypingValidation downstream (the
                // existing INCOMPLETE Error log will fire, plus this Warn for
                // explicit in-game-abort attribution).
                break;
            }
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
                skippedCount++;
                continue;
            }

            byte modifiers = (byte)(vkScan >> 8);
            if ((modifiers & ~0x01) != 0)
            {
                // Hotfix v4 (L1): log skipped chars so European-layout passwords that
                // need AltGr (e.g. @, {, }, [, ] on DE/FR/ES layouts) surface visibly
                // instead of silently producing a mystery login failure.
                FileLogger.Warn($"AutoLogin: CombinedTypeString skipping char '{c}' (U+{(int)c:X4}) — requires modifier 0x{modifiers:X} (Ctrl/Alt/AltGr)");
                skippedCount++;
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
            // v3.15.12 attempted to bump key-hold 25→80ms reasoning EQ polled
            // DI8 at 30Hz so 25ms could miss a poll. EMPIRICALLY DISPROVEN
            // 2026-05-10: post-deploy log shows EQ login-screen GetDeviceData
            // polls at ~4Hz (~240ms/poll) — much slower than 30Hz, so the
            // 80ms hold makes things WORSE not better. With ~4Hz polling and
            // 130ms-per-char typing (80 hold + 50 gap), most down→up cycles
            // complete entirely between polls and the SHM mask-state-only
            // protocol shows no transitions to inject. Result: 7 chars sent
            // → 2 chars seen in field (PID 27284, 07:17 run, eqswitch-dinput8.log).
            // Reverted to the 25ms key-hold timing that worked end-to-end on
            // 2026-05-09. The actual mechanism that makes typing reliable on
            // Dalaya is the WM_CHAR PostMessage path above (queued to EQ's
            // message thread, not subject to DI8 poll-cadence loss); fast
            // 25/15/15 keeps DI8 SHM from going round-trip on something the
            // login screen doesn't read for text input anyway. Per-char now
            // ~40ms; 7-char password ~280ms; looks paste-like.
            Thread.Sleep(25);

            // Key up (both layers)
            if (scan > 0 && scan < 256) writer.SetKey(pid, (byte)scan, false);
            PostR(writer, pid, hwnd, NativeMethods.WM_KEYUP, (IntPtr)vk, MakeKeyUpLParam(scan));

            // Shift up
            if (needShift)
            {
                Thread.Sleep(15); // matches working 2026-05-09 timing
                if (shiftScan > 0 && shiftScan < 256) writer.SetKey(pid, (byte)shiftScan, false);
                PostR(writer, pid, hwnd, NativeMethods.WM_KEYUP, (IntPtr)0x10, MakeKeyUpLParam(shiftScan));
            }
            Thread.Sleep(15); // inter-char gap — matches working 2026-05-09 timing
            typedCount++;
        }
        FileLogger.Info($"AutoLogin: CombinedTypeString PID={pid} done — typed={typedCount} skipped={skippedCount} input.Length={text.Length}");
        return new TypingResult(typedCount, skippedCount, text.Length);
    }

    /// <summary>
    /// Log warnings/errors based on a <see cref="CombinedTypeString"/> result.
    /// Layout-skips are user-action items (switch keyboard layout, change pw);
    /// incompleteness (Typed+Skipped &lt; Expected) means the typing loop exited
    /// mid-flight, which only happens via exception — surface loudly.
    /// </summary>
    private static void LogTypingValidation(string label, TypingResult r, int pid)
    {
        if (!r.IsComplete)
        {
            FileLogger.Error($"AutoLogin: {label} typing INCOMPLETE for PID {pid} — typed={r.Typed} skipped={r.Skipped} expected={r.Expected} (typing loop exited mid-flight; downstream submit will receive truncated input)");
        }
        if (r.HasLayoutSkips)
        {
            FileLogger.Warn($"AutoLogin: {label} typing layout-skipped {r.Skipped}/{r.Expected} char(s) for PID {pid} — login server will reject. Switch to a keyboard layout that maps every password char without modifiers (AltGr/Ctrl), or pick an ASCII-only password.");
        }
    }

    /// <summary>
    /// v3.20.10: tri-state result for <see cref="CancellableSleepUntilProcessDies"/>.
    /// Replaces the prior bool return so the caller can distinguish "EQ died" from
    /// "chars reached in-world during the wait" — the latter is a retry-success
    /// short-circuit (chars are already in-game, no need to keep retrying credentials).
    /// </summary>
    private enum RecoveryWaitOutcome
    {
        /// <summary>Full duration elapsed without process death or in-game detection.</summary>
        Completed,
        /// <summary>EQ process exited mid-wait — abort the retry path.</summary>
        ProcessDied,
        /// <summary>Chars reached in-world mid-wait — treat retry as success.</summary>
        InGame
    }

    /// <summary>
    /// Sleep for <paramref name="totalMs"/>, polling every <paramref name="pollIntervalMs"/>
    /// for either process death or (when <paramref name="charSelect"/> is provided)
    /// in-world detection. Originally added 2026-05-10 to address the gotquiz
    /// incident where EQ died DURING the 30s StaleSessionWaitMs sleep and C#
    /// sat idle for ~28s on a corpse PID. v3.20.10 added the in-world detection
    /// branch so retry recovery waits don't burn the full duration when chars
    /// already advanced — preventing the keystroke-leak race Nate observed
    /// 2026-05-15 (password chars hitting in-game characters as keybinds).
    /// Returns <see cref="RecoveryWaitOutcome.Completed"/> on full elapse,
    /// <see cref="RecoveryWaitOutcome.ProcessDied"/> on process exit/recycled,
    /// or <see cref="RecoveryWaitOutcome.InGame"/> when gameState==5 observed
    /// (Dalaya-reliable in-world signal). Passing <c>charSelect: null</c>
    /// preserves the pre-v3.20.10 bool-equivalent semantics — only the
    /// Completed and ProcessDied outcomes can fire, never InGame.
    /// </summary>
    private static RecoveryWaitOutcome CancellableSleepUntilProcessDies(int pid, int totalMs,
        int pollIntervalMs = 500, CharSelectReader? charSelect = null)
    {
        if (totalMs <= 0) return RecoveryWaitOutcome.Completed;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < totalMs)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited) return RecoveryWaitOutcome.ProcessDied;
            }
            catch (ArgumentException)
            {
                FileLogger.Info($"AutoLogin: CancellableSleepUntilProcessDies PID {pid} — process record gone at {sw.ElapsedMilliseconds}ms");
                return RecoveryWaitOutcome.ProcessDied;
            }
            catch (InvalidOperationException)
            {
                FileLogger.Info($"AutoLogin: CancellableSleepUntilProcessDies PID {pid} — process exited between query and HasExited at {sw.ElapsedMilliseconds}ms");
                return RecoveryWaitOutcome.ProcessDied;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // access-denied — PID recycled to protected process; treat as gone.
                FileLogger.Warn($"AutoLogin: CancellableSleepUntilProcessDies PID {pid} — Win32Exception at {sw.ElapsedMilliseconds}ms ({ex.Message}) — PID may have been recycled");
                return RecoveryWaitOutcome.ProcessDied;
            }

            // v3.20.10: adaptive in-game exit. When chars reach in-world mid-wait
            // (typical when step-1 modal-dismiss Enter landed on the default char
            // and zone-load completed during this sleep), exit the wait early so
            // the retry path can abort and skip credential re-typing — which would
            // otherwise fire password chars as in-game keystrokes (`d` → DUCK,
            // `\` → SwitchKey, etc.). gameState==5 is the Dalaya-reliable in-world
            // signal (per memory: gameState 0→5 only flips at zone-load). Skip
            // when charSelect is null (caller didn't opt into the check) to keep
            // backward compat with non-retry use cases.
            if (charSelect != null && charSelect.ReadGameState(pid) == 5)
            {
                FileLogger.Info($"AutoLogin: CancellableSleepUntilProcessDies PID {pid} — gameState==5 (in-world) at {sw.ElapsedMilliseconds}ms — exiting wait early");
                return RecoveryWaitOutcome.InGame;
            }

            int remaining = totalMs - (int)sw.ElapsedMilliseconds;
            if (remaining <= 0) break;
            Thread.Sleep(Math.Min(pollIntervalMs, remaining));
        }
        return RecoveryWaitOutcome.Completed;
    }

    /// <summary>
    /// After BURST 2 fires, poll for evidence EQ has actually advanced past the
    /// login screen. Returns true if a transition signal is observed within
    /// <paramref name="maxWaitMs"/>, false if EQ stayed put (likely credential
    /// rejection from BURST 1 — caller should fast-retry instead of waiting
    /// the full 90s screen-transition timeout).
    ///
    /// Signals (in order of reliability on Dalaya):
    ///   (a) window-rect SIZE change — load-bearing on Dalaya. The charselect-
    ///       load actually resizes/redraws the EQ window. PRIMARY signal.
    ///   (b) gameState change from initial reading — SUPPLEMENTAL on Dalaya.
    ///       Per Native/login_state_machine.cpp lines 30-36, "Known from DLL
    ///       log: login screen = 0, charselect = ?, ingame = ?". gameState may
    ///       not bump on the login→charselect transition (both could be 0).
    ///       The check is kept as a fast-positive for cases where it DOES
    ///       bump, but rect-change is the load-bearing signal.
    ///
    /// Returns false on process death.
    ///
    /// Window-minimize false-positive guard: if the EQ window is minimized
    /// mid-poll, GetWindowRect returns icon-strip dimensions (small width/
    /// height) that are STILL different from the initial fullscreen rect.
    /// IsIconic skips the rect-change check while minimized — rect-change
    /// only registers when the window returns to non-minimized state at a
    /// different size. (Verifier T2-Sonnet caught this 2026-05-14.)
    ///
    /// Addresses Nate's stated symptom 2026-05-14: when BURST 1 typing is
    /// truncated to 4-of-7 chars (first-keystroke-drop or user-input
    /// collision), the login server rejects within ~1-2s but EQSwitch waits
    /// the full 90s before retry. This shrinks that window to ~10s.
    /// </summary>
    private bool PollForLoginAdvance(CharSelectReader charSelect, int pid, IntPtr hwnd, int maxWaitMs)
    {
        if (maxWaitMs <= 0) return true; // disabled — preserve legacy 90s-wait behavior
        var sw = Stopwatch.StartNew();
        bool initialRectValid = NativeMethods.GetWindowRect(hwnd, out var initialRect);
        int initialState = charSelect.ReadGameState(pid);

        // Safety: if EQ has ALREADY advanced past login at probe entry (gameState
        // > 0; rare but possible when server response races the C# call OR when
        // initialState is the stale value from a prior session pre-restart),
        // treat as already-advanced. Without this, the gameState-change check
        // (state != initialState && state > initialState) can never fire and
        // we'd falsely conclude "no advance" → short-circuit charselect-load
        // wait to 5s before charselect actually renders.
        if (initialState > 0)
        {
            FileLogger.Info($"AutoLogin: PollForLoginAdvance PID {pid} — entry gameState={initialState} > 0, login already advanced; skipping probe");
            return true;
        }
        // Safety: if we couldn't get a valid initial rect (hwnd stale), the
        // rect-change check would false-positive on the first valid sample.
        // Bail and let WaitForScreenTransition handle this with its own retry.
        if (!initialRectValid)
        {
            FileLogger.Warn($"AutoLogin: PollForLoginAdvance PID {pid} — GetWindowRect failed at entry; skipping probe (legacy 90s wait will run)");
            return true;
        }
        FileLogger.Info($"AutoLogin: PollForLoginAdvance PID {pid} — initial gameState={initialState}, rect={initialRect.Width}x{initialRect.Height}, budget={maxWaitMs}ms");

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited)
                {
                    FileLogger.Warn($"AutoLogin: PollForLoginAdvance PID {pid} — process exited at {sw.ElapsedMilliseconds}ms");
                    return false;
                }
            }
            catch (ArgumentException)
            {
                FileLogger.Warn($"AutoLogin: PollForLoginAdvance PID {pid} — process record missing at {sw.ElapsedMilliseconds}ms (PID gone)");
                return false;
            }
            catch (InvalidOperationException)
            {
                FileLogger.Warn($"AutoLogin: PollForLoginAdvance PID {pid} — process exited between query and HasExited at {sw.ElapsedMilliseconds}ms");
                return false;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // Win32Exception can fire on access-denied if the PID was recycled
                // to a protected process. Treat same as ArgumentException — process
                // we cared about is effectively gone. Log so the failure is visible
                // (T3-Sonnet R3 verifier observability catch 2026-05-14).
                FileLogger.Warn($"AutoLogin: PollForLoginAdvance PID {pid} — Win32Exception at {sw.ElapsedMilliseconds}ms ({ex.Message}) — PID may have been recycled to protected process");
                return false;
            }

            int state = charSelect.ReadGameState(pid);
            if (state != initialState && state > initialState)
            {
                FileLogger.Info($"AutoLogin: PollForLoginAdvance PID {pid} — advance detected (gameState {initialState}→{state}) at {sw.ElapsedMilliseconds}ms");
                return true;
            }

            // Char-select SHM signal (v3.20.6, 2026-05-15) — load-bearing on
            // Dalaya when QUICK CONNECT was clicked. QUICK CONNECT skips
            // server-select entirely, so neither gameState NOR window-rect
            // changes (Dalaya keeps gameState at 0 across login/server-
            // select/char-select). The DLL publishes IsMQ2Available + char
            // count via SHM as SOON as pinstCCharacterSelect populates —
            // that's char-select reached. Without this check, C# falsely
            // declares "credentials likely rejected" and retries credential
            // typing, knocking the client OUT of char-select.
            if (charSelect.IsMQ2Available(pid) && charSelect.ReadCharCount(pid) > 0)
            {
                int charCount = charSelect.ReadCharCount(pid);
                FileLogger.Info($"AutoLogin: PollForLoginAdvance PID {pid} — char-select SHM advance detected (mq2Available + {charCount} chars) at {sw.ElapsedMilliseconds}ms");
                return true;
            }

            // Rect-change check is the LOAD-BEARING signal on Dalaya. Gate on
            // !IsIconic — a minimized window returns icon-strip dimensions that
            // differ from initial but indicate a user minimize, not a charselect
            // transition. Without this guard, user-minimize during probe falsely
            // returns "advance detected" → no retry fires even on real failure.
            if (!NativeMethods.IsIconic(hwnd) && NativeMethods.GetWindowRect(hwnd, out var current))
            {
                if (current.Width != initialRect.Width || current.Height != initialRect.Height)
                {
                    FileLogger.Info($"AutoLogin: PollForLoginAdvance PID {pid} — rect SIZE change ({initialRect.Width}x{initialRect.Height} → {current.Width}x{current.Height}) at {sw.ElapsedMilliseconds}ms");
                    return true;
                }
            }
            Thread.Sleep(500);
        }
        FileLogger.Warn($"AutoLogin: PollForLoginAdvance PID {pid} — no advance signal within {maxWaitMs}ms; credentials likely rejected, fast-failing to retry");
        return false;
    }

    private static void LogWidgetSnapshot(LoginShmWriter? loginShm, int pid, string checkpoint)
    {
        if (loginShm == null) return;
        if (loginShm.TryReadWidgetState(pid, out var widgets))
        {
            FileLogger.Info($"[widgets@{checkpoint} pid={pid}] {widgets.DiagSummary()}");
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
        // Iter 15.1 (2026-04-25): poll every 50ms instead of 500ms. The window
        // handle becomes available at an unpredictable point during eqgame.exe
        // boot (anywhere from 3-8s after spawn); a 500ms poll could land up
        // to 500ms after the actual ready moment. Tightening to 50ms means
        // we send the LOGIN command within ~50ms of the window being ready,
        // which is the upstream gate for the entire native-side login flow.
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
            Thread.Sleep(50);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Wait for a screen transition to complete by detecting when EQ's window goes
    /// unresponsive (loading 3D scene) and becomes responsive again. Falls back on
    /// window rect instability detection if the window never fully hangs.
    /// Returns the (possibly refreshed) window handle, or IntPtr.Zero if the process died.
    /// </summary>
    // v3.15.2: was static; now reads timing knobs from _config.Launch (defaults
    // preserve prior 1000/1000/500ms behavior, so the visibility change is benign
    // for callers that haven't tuned the config).
    private IntPtr WaitForScreenTransition(int pid, IntPtr hwnd, int maxWaitMs = 90000)
    {
        var sw = Stopwatch.StartNew();
        bool sawHung = false;
        bool sawRectChange = false;
        NativeMethods.GetWindowRect(hwnd, out var initialRect);
        var lastRect = initialRect;
        long lastRectChangeMs = 0;

        // Give EQ a moment to start the transition before polling.
        // v3.15.2: tunable via Launch.WaitTransitionInitialDelayMs (default 1000).
        Thread.Sleep(_config.Launch.WaitTransitionInitialDelayMs);

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            // Refresh handle — EQ may recreate its window during transition
            hwnd = RefreshHandle(pid, hwnd);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;

            bool hung = NativeMethods.IsHungAppWindow(hwnd);

            if (hung)
            {
                // Edge-detect: log only on transition into hung. Without this,
                // the 500ms poll interval times the ~22s charselect load
                // produces ~44 identical lines per PID per login.
                if (!sawHung)
                {
                    FileLogger.Info($"AutoLogin: EQ window hung (loading) PID {pid}, elapsed={sw.ElapsedMilliseconds}ms");
                    sawHung = true;
                }
            }
            else if (sawHung)
            {
                // Was hung, now responsive — transition complete
                FileLogger.Info($"AutoLogin: EQ responsive after loading PID {pid}, elapsed={sw.ElapsedMilliseconds}ms");
                // v3.15.2: settle tunable via Launch.WaitTransitionSettleMs (default 1000).
                Thread.Sleep(_config.Launch.WaitTransitionSettleMs);
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

            // v3.15.2: poll cadence tunable via Launch.WaitTransitionPollIntervalMs (default 500).
            Thread.Sleep(_config.Launch.WaitTransitionPollIntervalMs);
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
            if (_loggedExitPids.TryAdd(pid, 0))
                FileLogger.Warn($"AutoLogin: process {pid} exited during login sequence");
        }

        return IntPtr.Zero;
    }

    // ─── Per-server policy helpers (v3.15.11) ────────────────────────

    /// <summary>
    /// Returns true if the SHM-based Enter World path should be skipped for
    /// this account, falling straight through to PulseKey3D. Currently only
    /// Dalaya qualifies (per Launch.SkipShmEnterWorldOnDalaya, default true).
    /// Empirically on Dalaya, CLW_EnterWorldButton isn't constructed by the
    /// time charselect-ready is signaled — every SHM attempt returns -1 and
    /// PulseKey3D fallback fires after ~2-2.5s of failed retries. This gate
    /// eliminates that wasted retry budget. The structural fix (bridge
    /// writes a buttonReady SHM flag and C# polls it) is filed as Option B
    /// follow-up. Settings dropdown is locked to "Dalaya" in v3.14.7+ so
    /// effectively all v3.15.11 users get the fast path; the flag is the
    /// power-user opt-out for hypothetical other-server use via direct
    /// edit of eqswitch-config.json.
    /// </summary>
    private bool ShouldSkipShmEnterWorld(Account account)
    {
        if (!_config.Launch.SkipShmEnterWorldOnDalaya) return false;
        return string.Equals(account.Server?.Trim(), "Dalaya",
            StringComparison.OrdinalIgnoreCase);
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
    /// Write Username to the [PLAYER] section of all eqlsPlayerData*.ini files
    /// so EQ pre-fills the LOGIN screen username field with this account.
    ///
    /// ⚠ DO NOT CALL FROM BeginLogin OR ANY PRE-LAUNCH PATH ⚠
    /// The original integration in BeginLogin (reverted 2026-05-10) caused
    /// dual-box team launch to fail. The race window is wider than initially
    /// believed: eqlsPlayerData*.ini is read by eqmain.dll at LOGIN-SCREEN
    /// RENDER time (~T+5.6s after process resume per
    /// eqswitch-dinput8-PID.log), NOT by eqgame.exe at process start. With
    /// the default 3000ms LaunchDelayMs, client 2's BeginLogin fires its
    /// INI write at T+3s, clobbering client 1's pre-fill BEFORE client 1's
    /// eqmain.dll has rendered the login screen and read the value. Result:
    /// both clients pre-fill with the second account's username; for
    /// UseLoginFlag=true accounts (which skip BURST 1's Tab+username retype)
    /// this means client 1 types the right password against the wrong
    /// pre-filled username and the server rejects.
    ///
    /// Safe call sites (per-PID, post-eqmain-load):
    /// - Inside RunLoginSequence after the DLL signals gameState != 0
    ///   (eqmain has loaded for THIS PID, has already read its INI).
    /// - Anywhere AFTER the relevant client's eqmain.dll-loaded event.
    /// - Single-client (non-team) launches at any pre-burst point.
    ///
    /// Helper kept defined for the per-PID future use case. The
    /// IsNullOrWhiteSpace + control-char guards below stay relevant.
    ///
    /// Background: EQ writes the LAST USERNAME TYPED to this INI on exit.
    /// Without our intervention, the field pre-fills with whichever account
    /// most recently exited. For UseLoginFlag=true accounts on Dalaya, the
    /// /login:USERNAME launch arg is the primary delivery mechanism. If a
    /// future Dalaya patch breaks /login: parsing, this helper becomes the
    /// safety net — but ONLY when called per-PID after eqmain load.
    /// </summary>
    private void WriteUsernameToIni(string username)
    {
        // Verifier-flagged guards (v3.15.11 follow-up):
        // - IsNullOrWhiteSpace catches "   " which would write a blank-looking
        //   field that pre-fills three spaces (silent UI failure).
        // - Newline rejection prevents INI injection — a Username containing
        //   "\nUsername=other\n" would inject a second line when WriteAllLines
        //   serializes the entry. Usernames are alphanumeric in practice but
        //   the AccountEditDialog doesn't validate, so config-edit attacks
        //   could craft one. Cheap to reject.
        if (string.IsNullOrWhiteSpace(username)) return;
        if (username.IndexOfAny(new[] { '\n', '\r', '[', ']' }) >= 0)
        {
            FileLogger.Warn($"AutoLogin: refusing to write Username with control/section chars to INI: '{username}'");
            return;
        }

        try
        {
            var files = Directory.GetFiles(_config.EQPath, "eqlsPlayerData*.ini");
            foreach (var file in files)
            {
                WriteIniValue(file, "PLAYER", "Username", username);
                FileLogger.Info($"AutoLogin: wrote Username={username} to {Path.GetFileName(file)}");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"AutoLogin: failed to write username to INI: {ex.Message}");
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
