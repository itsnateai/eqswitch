// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace EQSwitch.Core;

/// <summary>
/// Low-level keyboard hook for single-key hotkeys (no modifier required).
///
/// RegisterHotKey requires a modifier (Alt/Ctrl/Shift) or globally eats the key.
/// AHK uses low-level hooks for context-sensitive keys like "\" (EQ-only) and "]" (global).
/// This class replicates that behavior.
///
/// Each registered key can be:
///   - Context-sensitive: only fires when a specific process is focused
///   - Global: fires regardless of which window is focused
///
/// IMPORTANT: The hook callback must return within ~300ms or Windows silently
/// removes it. All callbacks are posted to the UI thread asynchronously.
/// </summary>
public class KeyboardHookManager : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    // Must be stored as a field to prevent GC from collecting the delegate
    private readonly NativeMethods.LowLevelKeyboardProc _hookProc;
    // v3.22.53 post-verifier-fix: ConcurrentDictionary so Register/Unregister
    // from background threads (currently UI-only, but no compiler guard)
    // doesn't race with HookCallback's TryGetValue. Same rationale as
    // _lastFireTickByVk below.
    private readonly ConcurrentDictionary<uint, HookBinding> _bindings = new();
    private SynchronizationContext? _syncContext;
    private bool _disposed;

    // Lock-free PID set for process filter checks — swapped atomically
    private volatile ImmutableHashSet<int> _filteredPids = ImmutableHashSet<int>.Empty;

    // v3.22.53: per-VK last-fire timestamps for debouncing rapid duplicate
    // keydowns. Address two real-world causes Nate hit pressing `\` to swap:
    //   1) Hardware key-bounce — some mechanical keyboards generate 2 fast
    //      WM_KEYDOWNs from one physical press (~10–40 ms apart).
    //   2) OS auto-repeat — holding `\` for >~400 ms triggers Windows
    //      typematic repeat (~30 Hz). Each repeat is a real WM_KEYDOWN; the
    //      LL hook can't distinguish "user held it" from "user tapped 5x".
    // KBDLLHOOKSTRUCT has no autorepeat flag (unlike WM_KEYDOWN lParam
    // bit 30), so a timestamp window is the only reliable filter. 80 ms is
    // tight enough that deliberate fast tapping (60–80 ms/press cadence is
    // already faster than most humans sustain) is preserved, while bounces
    // and the slowest autorepeat tick are absorbed.
    private const int RepeatDebounceMs = 80;
    // v3.22.53 post-verifier-fix: ConcurrentDictionary closes the race-doubt
    // T2 verifier pair flagged. WH_KEYBOARD_LL callbacks fire on the thread
    // that called SetWindowsHookEx (Install() requires the UI thread) so in
    // practice the access is single-threaded today — but if a future caller
    // ever invokes Reset()/Register()/Unregister() from a background thread
    // during a hook callback, the plain Dictionary would corrupt mid-rehash.
    // ConcurrentDictionary costs one extra interlocked op per access; that's
    // free compared to the OS-side cost of a keyboard event reaching us.
    private readonly ConcurrentDictionary<uint, long> _lastFireTickByVk = new();

    // v3.22.53 post-round-3 fix (T3 Opus IMPORTANT): per-VK "we swallowed the
    // matching KEYDOWN" tracker. The symmetric KEYUP swallow added in
    // round-2 closed the Alt-chord SYSKEYUP orphan, but introduced a new
    // failure mode for the case where a user holds a bound VK BEFORE
    // EQSwitch installs the hook — the eventual physical release fires a
    // WM_KEYUP we never saw the KEYDOWN for, and unconditionally swallowing
    // it leaves the focused app's state machine convinced the key is still
    // pressed. Only swallow KEYUP when the matching KEYDOWN was eaten. Set
    // by the KEYDOWN path on both the fire branch AND the debounce branch,
    // cleared by the KEYUP swallow itself so a stuck flag can't survive.
    private readonly ConcurrentDictionary<uint, bool> _downSwallowedByVk = new();

    public KeyboardHookManager()
    {
        _hookProc = HookCallback;
    }

    /// <summary>
    /// Install the low-level keyboard hook. Call once at startup.
    /// Must be called from the UI thread (captures SynchronizationContext).
    /// </summary>
    public bool Install()
    {
        if (_hookId != IntPtr.Zero) return true; // Already installed

        _syncContext = SynchronizationContext.Current;
        if (_syncContext == null)
        {
            FileLogger.Error("KeyboardHookManager.Install() called without a SynchronizationContext — must be called from the UI thread");
            throw new InvalidOperationException("KeyboardHookManager.Install() must be called from the UI thread");
        }

        var hMod = NativeMethods.GetModuleHandle(null);
        _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookProc, hMod, 0);

        if (_hookId == IntPtr.Zero)
        {
            FileLogger.Error($"SetWindowsHookEx failed with error {Marshal.GetLastWin32Error()}");
            return false;
        }

        FileLogger.Info("Low-level keyboard hook installed");
        return true;
    }

    /// <summary>
    /// Update the cached set of PIDs for process-filtered bindings.
    /// Call this from ProcessManager when client list changes.
    /// Lock-free: atomically swaps the immutable set.
    /// </summary>
    public void UpdateFilteredPids(IEnumerable<int> pids)
    {
        _filteredPids = pids.ToImmutableHashSet();
    }

    /// <summary>
    /// Register a key to be intercepted by the hook.
    /// </summary>
    /// <param name="vkCode">Virtual key code (e.g. VK_OEM_5 for '\')</param>
    /// <param name="callback">Action to invoke on keypress</param>
    /// <param name="processFilter">If set, only fires when this process name has foreground focus</param>
    /// <param name="requireClients">If true, key passes through when no filtered PIDs are registered</param>
    public void Register(uint vkCode, Action callback, string? processFilter = null, bool requireClients = false)
    {
        _bindings[vkCode] = new HookBinding(callback, processFilter, requireClients);
        FileLogger.Info($"Hook registered: VK 0x{vkCode:X2}" +
            (processFilter != null ? $" (filter: {processFilter})" : " (global)") +
            (requireClients ? " (requires clients)" : ""));
    }

    /// <summary>
    /// Unregister a key binding.
    /// </summary>
    public void Unregister(uint vkCode)
    {
        _bindings.TryRemove(vkCode, out _);
        // v3.22.53 post-verifier-fix: also drop the per-VK debounce
        // timestamp. Otherwise an Unregister + immediate Register of the
        // same VK could inherit the prior fire-window and falsely suppress
        // the first legitimate press after rebind.
        _lastFireTickByVk.TryRemove(vkCode, out _);
        // v3.22.53 post-round-3 fix (T3 Opus IMPORTANT): drop the
        // "down-was-swallowed" tracker too. Stale flag would cause the next
        // Register + keypress sequence to start with the down-marker pre-set
        // from the prior binding, leading to a spurious KEYUP swallow.
        _downSwallowedByVk.TryRemove(vkCode, out _);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

        // v3.22.53 post-verifier-fix (T2 Sonnet IMPORTANT): also swallow
        // the paired KEYUP/SYSKEYUP for any VK we intercepted on KEYDOWN.
        // Otherwise an Alt+<bound-key> chord (which fires WM_SYSKEYDOWN +
        // WM_SYSKEYUP rather than WM_KEYDOWN + WM_KEYUP) drops a stray
        // SYSKEYUP on the focused EQ window once the KEYDOWN is consumed.
        // EQ's WndProc can treat the orphan as a partial Alt+key chord and
        // emit a menu-bar beep or worse. Same filter conditions as the
        // KEYDOWN path so we don't swallow keys we didn't intercept.
        if (wParam == (IntPtr)NativeMethods.WM_KEYUP || wParam == (IntPtr)NativeMethods.WM_SYSKEYUP)
        {
            var upStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            // v3.22.53 KEYUP swallow policy:
            //
            // If we swallowed the matching KEYDOWN earlier, the focused
            // window never saw the down — so we MUST also swallow the up
            // to keep the down/up pairing consistent. Filter state at
            // KEYDOWN time is what mattered; the KEYUP symmetrically
            // completes the swallowed pair regardless of whether
            // RequireClients/ProcessFilter still matches now. (Round-3
            // tried re-checking filters here and round-4 found that the
            // re-check re-introduced the orphan-KEYUP failure the symmetric
            // swallow was supposed to fix — focused app sees KEYUP with no
            // matching KEYDOWN.)
            //
            // Round-5 collapse (T2 Sonnet/Opus + T3 Sonnet/Opus convergent):
            // single atomic TryRemove instead of ContainsKey+TryRemove. Same
            // behavior, one ConcurrentDictionary op instead of two, and no
            // TOCTOU window if a future caller mutates from another thread.
            // The "leave flag for OS retry" rationale from the round-4 comment
            // was unrealizable — the OS never re-fires WM_SYSKEYUP for the
            // same physical release, so a single-shot swallow IS correct
            // semantics. Comment now matches code.
            if (_downSwallowedByVk.TryRemove(upStruct.vkCode, out _))
                return (IntPtr)1;
        }

        if (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN)
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            if (_bindings.TryGetValue(hookStruct.vkCode, out var binding))
            {
                // Don't swallow the key if no EQ clients are running
                if (binding.RequireClients && _filteredPids.IsEmpty)
                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

                // Check process filter using cached PIDs (no Process.GetProcessById — fast)
                if (binding.ProcessFilter != null && !IsForegroundFiltered())
                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

                // v3.22.53: per-VK debounce — catches hardware bounce + OS
                // typematic autorepeat. Still SWALLOW the duplicate so the
                // focused window doesn't see the second `\` either; only the
                // callback is suppressed. Using Environment.TickCount64 is
                // cheap and monotonic; KBDLLHOOKSTRUCT.time is the source
                // hardware timestamp but isn't guaranteed to start at 0 nor
                // to match TickCount, so we'd need a per-VK delta anyway.
                long nowTicks = Environment.TickCount64;
                if (_lastFireTickByVk.TryGetValue(hookStruct.vkCode, out long lastTick)
                    && nowTicks - lastTick < RepeatDebounceMs)
                {
                    // Swallow without firing the callback. Logged at Info so
                    // the SwitchKey "extra swap" diagnostics path stays
                    // visible without spamming Warn during normal typing.
                    FileLogger.Info($"KeyboardHook: debounced VK 0x{hookStruct.vkCode:X2} ({nowTicks - lastTick}ms since last fire, <{RepeatDebounceMs}ms threshold)");
                    // Still mark the down as swallowed so the matching KEYUP
                    // (which arrives outside the 80 ms window for any held
                    // key release) gets swallowed too. Otherwise a debounced
                    // KEYDOWN leaks its paired KEYUP.
                    _downSwallowedByVk[hookStruct.vkCode] = true;
                    return (IntPtr)1;
                }
                _lastFireTickByVk[hookStruct.vkCode] = nowTicks;
                _downSwallowedByVk[hookStruct.vkCode] = true;

                // Post callback to UI thread asynchronously — return immediately
                // to avoid blocking the hook (Windows kills hooks that take >300ms)
                _syncContext?.Post(_ =>
                {
                    try { binding.Callback.Invoke(); }
                    catch (Exception ex)
                    {
                        FileLogger.Error($"Hook callback error (VK 0x{hookStruct.vkCode:X2})", ex);
                    }
                }, null);

                // Swallow the key — don't pass to the focused application
                return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Check if the foreground window belongs to a filtered process using cached PIDs.
    /// No Process.GetProcessById — just GetWindowThreadProcessId + ImmutableHashSet lookup.
    /// Lock-free: reads the volatile reference to the immutable set.
    /// </summary>
    private bool IsForegroundFiltered()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return _filteredPids.Contains((int)pid);
    }

    /// <summary>
    /// Unhook and clear bindings, but allow re-installation via Install().
    /// </summary>
    public void Reset()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _bindings.Clear();
        // v3.22.53 post-verifier-fix (T2 Sonnet+Opus convergent CRITICAL):
        // also clear the debounce timestamps so a config-reload-driven
        // Reset() → Install() → Register() sequence doesn't inherit stale
        // timestamps from before the reload. Without this, the first press
        // after a Settings → Apply could be falsely suppressed if the user
        // had pressed the same VK within the prior 80 ms.
        _lastFireTickByVk.Clear();
        // v3.22.53 post-round-3 fix (T3 Opus IMPORTANT): same rationale for
        // the down-swallowed tracker — Reset clears the keyboard hook, so
        // anything we'd remembered about "we ate this VK's down" is no
        // longer load-bearing.
        _downSwallowedByVk.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }

    private record HookBinding(Action Callback, string? ProcessFilter, bool RequireClients);
}
