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
    /// Launch EQ and auto-login with the given account.
    /// Non-blocking — runs the login sequence on a background thread.
    /// Returns a Task that completes when the full login sequence finishes.
    /// </summary>
    public Task LoginAccount(LoginAccount account, bool? teamAutoEnter = null)
    {
        string password;
        try
        {
            password = CredentialManager.Decrypt(account.EncryptedPassword);
        }
        catch (Exception ex)
        {
            FileLogger.Error("AutoLogin: failed to decrypt password", ex);
            StatusUpdate?.Invoke(this, "Error: failed to decrypt password");
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
            if (pid > 0) _activeLoginPids.TryRemove(pid, out _);
            StatusUpdate?.Invoke(this, $"Error: {ex.Message}");
            return Task.CompletedTask;
        }

        // Run the login sequence on a background thread.
        var loginAccount = account;
        var teamEnter = teamAutoEnter;
        return Task.Run(() => RunLoginSequence(pid, loginAccount, password, teamEnter));
    }

    private void RunLoginSequence(int pid, LoginAccount account, string password, bool? teamAutoEnter = null)
    {
        // Parallel-safe background login via brief activation windows.
        // Focus-faking (WndProc subclass + IAT hooks) is ONLY active during
        // keystroke bursts (~2s each). Between bursts it's OFF so multiple
        // logins don't fight for foreground.
        var writer = new KeyInputWriter();
        var charSelect = new CharSelectReader();
        try
        {
            // Open SHM early so DLL discovers it during EQ startup
            if (!writer.Open(pid))
            {
                Report("Error: failed to create DirectInput shared memory");
                return;
            }
            if (!charSelect.Open(pid))
                FileLogger.Warn($"AutoLogin: CharSelectReader SHM open failed for PID {pid}");

            // ── Wait for EQ window ──
            Report("Waiting for EQ window...");
            var hwnd = WaitForWindow(pid, TimeSpan.FromSeconds(30));
            if (hwnd == IntPtr.Zero) { Report("Timeout: EQ window did not appear"); return; }

            // ── Wait for login screen ──
            Report("Waiting for login screen...");
            Thread.Sleep(_config.LoginScreenDelayMs);

            // ══════════════════════════════════════════════════════════
            // BURST 1: Type credentials + submit (~3 seconds active)
            // ══════════════════════════════════════════════════════════
            Report("Typing credentials...");
            writer.Activate(pid);
            Thread.Sleep(500); // let DLL switch coop + blast activation
            FileLogger.Info($"AutoLogin: BURST 1 activated for PID {pid}");

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
            if (hwnd == IntPtr.Zero) { Report("Error: lost EQ window after login"); return; }

            // ══════════════════════════════════════════════════════════
            // BURST 2: Confirm server select (~1 second active)
            // ══════════════════════════════════════════════════════════
            Report("Confirming server...");
            writer.Activate(pid);
            Thread.Sleep(300);
            FileLogger.Info($"AutoLogin: BURST 2 activated for PID {pid}");
            CombinedPressKey(writer, pid, hwnd, 0x0D); // Enter = confirm
            Thread.Sleep(500);
            writer.Deactivate(pid); // ← OFF
            FileLogger.Info($"AutoLogin: BURST 2 deactivated for PID {pid}");

            // ── Wait for charselect load (no focus-faking, 5-60+ seconds) ──
            Report("Loading character select...");
            hwnd = WaitForScreenTransition(pid, hwnd);
            if (hwnd == IntPtr.Zero) { Report("Error: lost EQ window during charselect load"); return; }
            FileLogger.Info($"AutoLogin: charselect ready, hwnd=0x{hwnd:X} for PID {pid}");

            // ── Auto Enter World gate ──
            // Priority: team setting > per-account setting > global fallback
            bool shouldEnterWorld = teamAutoEnter ?? account.AutoEnterWorld;
            if (!shouldEnterWorld)
            {
                Report($"{account.Name} reached character select.");
                FileLogger.Info($"AutoLogin: {account.Name} stopped at char select (AutoEnterWorld={shouldEnterWorld}, team={teamAutoEnter}, acct={account.AutoEnterWorld})");
                return;
            }

            // ── Character selection via MQ2 bridge (no focus-faking needed) ──
            // Priority: slot > name > default
            Thread.Sleep(2000); // let MQ2 bridge init

            bool wantSelection = account.CharacterSlot > 0 || !string.IsNullOrEmpty(account.CharacterName);
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
                    if (hwnd != IntPtr.Zero)
                    {
                        var tb = new StringBuilder(256);
                        NativeMethods.GetWindowText(hwnd, tb, tb.Capacity);
                        if (tb.ToString().Contains(" - "))
                        {
                            FileLogger.Info("AutoLogin: already in-game during charlist wait, skipping selection");
                            break;
                        }
                    }

                    Thread.Sleep(500);
                }

                if (charListReady)
                {
                    // Snapshot charCount — don't re-read from SHM (DLL can reset to 0 between polls)
                    int charCount = charSelect.ReadCharCount(pid);
                    var charNames = charSelect.ReadAllCharNames(pid);
                    FileLogger.Info($"AutoLogin: {charCount} characters found: {string.Join(", ", charNames)}");

                    bool selected = false;
                    if (account.CharacterSlot > 0)
                    {
                        // Slot-based selection (1-10) — direct index, no name lookup
                        if (account.CharacterSlot <= charCount)
                        {
                            charSelect.RequestSelectionBySlot(pid, account.CharacterSlot);
                            FileLogger.Info($"AutoLogin: requested slot {account.CharacterSlot} for PID {pid}");
                            selected = true;
                        }
                        else
                            FileLogger.Warn($"AutoLogin: slot {account.CharacterSlot} exceeds char count {charCount}");
                    }
                    else if (!string.IsNullOrEmpty(account.CharacterName))
                    {
                        // Name-based selection — search and select
                        int selIdx = charSelect.RequestSelectionByName(pid, account.CharacterName);
                        selected = selIdx >= 0;
                        if (!selected)
                            FileLogger.Warn($"AutoLogin: character '{account.CharacterName}' not found, using default");
                    }

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
                            FileLogger.Warn($"AutoLogin: DLL did not ack character selection");
                        Thread.Sleep(200); // SetCurSel fires synchronously on game thread via TIMERPROC
                    }
                }
                else
                    FileLogger.Warn($"AutoLogin: MQ2 bridge not ready, entering world with default");
            }

            // ── Enter World via PulseKey3D (keyboard Enter) ──────────
            // In-process CLW_EnterWorldButton click doesn't work on Dalaya —
            // eqgame's CXWndManager is empty at charselect (widget not findable).
            // PulseKey3D with brief activation windows is the proven approach.
            Report("Entering world...");
            bool entered = false;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                // Check BEFORE pressing — avoid sending Enter into an already-loaded game
                hwnd = RefreshHandle(pid, hwnd);
                if (hwnd == IntPtr.Zero) { Report("Error: lost EQ window"); return; }
                {
                    var tb = new StringBuilder(256);
                    NativeMethods.GetWindowText(hwnd, tb, tb.Capacity);
                    if (tb.ToString().Contains(" - "))
                    {
                        entered = true;
                        FileLogger.Info($"AutoLogin: already in-game before attempt {attempt + 1} (title: {tb})");
                        break;
                    }
                }

                writer.Activate(pid);
                Thread.Sleep(500);
                PulseKey3D(writer, pid, hwnd, 0x0D);
                Thread.Sleep(500);
                writer.Deactivate(pid);

                // Wait for world load — poll title every second (Dalaya loads can take 5-90s)
                for (int loadWait = 0; loadWait < 20; loadWait++)
                {
                    Thread.Sleep(1000);
                    hwnd = RefreshHandle(pid, hwnd);
                    if (hwnd == IntPtr.Zero) break;
                    var tb = new StringBuilder(256);
                    NativeMethods.GetWindowText(hwnd, tb, tb.Capacity);
                    if (tb.ToString().Contains(" - "))
                    {
                        entered = true;
                        FileLogger.Info($"AutoLogin: enter-world confirmed after {loadWait + 1}s (title: {tb})");
                        break;
                    }
                }
                if (entered) break;

                FileLogger.Info($"AutoLogin: enter-world attempt {attempt + 1} — not in-game yet, retrying...");
            }

            if (entered)
            {
                Report($"{account.Name} logged in!");
                FileLogger.Info($"AutoLogin: {account.Name} login complete (PID {pid})");
            }
            else
            {
                Report($"{account.Name}: reached char select but Enter World didn't register");
                FileLogger.Warn($"AutoLogin: {account.Name} enter-world failed after 5 attempts (PID {pid})");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"AutoLogin: sequence failed for {account.Name}", ex);
            Report($"Error: {ex.Message}");
        }
        finally
        {
            // Deactivate FIRST — ensure focus-faking stops before handles close
            try { writer.Deactivate(pid); } catch { }
            try { charSelect.Close(pid); } catch { }
            try { charSelect.Dispose(); } catch { }
            try { writer.Close(pid); } catch (Exception ex) { FileLogger.Warn($"AutoLogin: Close failed: {ex.Message}"); }
            try { writer.Dispose(); } catch (Exception ex) { FileLogger.Warn($"AutoLogin: Dispose failed: {ex.Message}"); }
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
            if (vkScan == -1) continue;

            byte modifiers = (byte)(vkScan >> 8);
            if ((modifiers & ~0x01) != 0) continue;

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

            // Fallback: track window rect changes (distortion + snap-back pattern)
            if (NativeMethods.GetWindowRect(hwnd, out var currentRect))
            {
                bool rectChanged = currentRect.Left != lastRect.Left ||
                                   currentRect.Top != lastRect.Top ||
                                   currentRect.Width != lastRect.Width ||
                                   currentRect.Height != lastRect.Height;
                if (rectChanged)
                {
                    sawRectChange = true;
                    lastRectChangeMs = sw.ElapsedMilliseconds;
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
        FileLogger.Warn($"AutoLogin: screen transition timeout ({maxWaitMs}ms), proceeding anyway");
        return RefreshHandle(pid, hwnd);
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
