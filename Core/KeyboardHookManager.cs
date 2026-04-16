// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

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
    private readonly Dictionary<uint, HookBinding> _bindings = new();
    private SynchronizationContext? _syncContext;
    private bool _disposed;

    // Lock-free PID set for process filter checks — swapped atomically
    private volatile ImmutableHashSet<int> _filteredPids = ImmutableHashSet<int>.Empty;

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
        _bindings.Remove(vkCode);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN))
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }

    private record HookBinding(Action Callback, string? ProcessFilter, bool RequireClients);
}
