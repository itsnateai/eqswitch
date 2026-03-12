using System.Diagnostics;
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
/// </summary>
public class KeyboardHookManager : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    // Must be stored as a field to prevent GC from collecting the delegate
    private readonly NativeMethods.LowLevelKeyboardProc _hookProc;
    private readonly Dictionary<uint, HookBinding> _bindings = new();
    private bool _disposed;

    public KeyboardHookManager()
    {
        _hookProc = HookCallback;
    }

    /// <summary>
    /// Install the low-level keyboard hook. Call once at startup.
    /// </summary>
    public bool Install()
    {
        if (_hookId != IntPtr.Zero) return true; // Already installed

        var hMod = NativeMethods.GetModuleHandle(null);
        _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookProc, hMod, 0);

        if (_hookId == IntPtr.Zero)
        {
            Debug.WriteLine($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        Debug.WriteLine("Low-level keyboard hook installed");
        return true;
    }

    /// <summary>
    /// Register a key to be intercepted by the hook.
    /// </summary>
    /// <param name="vkCode">Virtual key code (e.g. VK_OEM_5 for '\')</param>
    /// <param name="callback">Action to invoke on keypress</param>
    /// <param name="processFilter">If set, only fires when this process name has foreground focus</param>
    public void Register(uint vkCode, Action callback, string? processFilter = null)
    {
        _bindings[vkCode] = new HookBinding(callback, processFilter);
        Debug.WriteLine($"Hook registered: VK 0x{vkCode:X2}" +
            (processFilter != null ? $" (filter: {processFilter})" : " (global)"));
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
        if (nCode >= 0 && wParam == NativeMethods.WM_KEYDOWN)
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            if (_bindings.TryGetValue(hookStruct.vkCode, out var binding))
            {
                // Check process filter
                if (binding.ProcessFilter != null)
                {
                    if (!IsForegroundProcess(binding.ProcessFilter))
                    {
                        // Not the right process — let the key pass through
                        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }
                }

                try
                {
                    binding.Callback.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Hook callback error (VK 0x{hookStruct.vkCode:X2}): {ex.Message}");
                }

                // Swallow the key — don't pass to the focused application
                return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Check if the foreground window belongs to a specific process.
    /// </summary>
    private static bool IsForegroundProcess(string processName)
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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

    private record HookBinding(Action Callback, string? ProcessFilter);
}
