using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EQSwitch.Core;

/// <summary>
/// Captures keyboard input via WH_KEYBOARD_LL and broadcasts key states
/// to target EQ processes through per-process shared memory (KeyInputWriter).
///
/// Flow: Physical key event → LL hook → write to all target PIDs' shared memory
///       → dinput8.dll proxy reads shared memory and injects into DirectInput.
///
/// Key broadcasting is only active when enabled and at least one target exists.
/// The focused EQ window is excluded from broadcast targets (it gets real input).
/// </summary>
public class KeyBroadcastManager : IDisposable
{
    private readonly KeyInputWriter _writer;
    private readonly HashSet<int> _targetPids = new();
    private IntPtr _hookHandle;
    private NativeMethods.LowLevelKeyboardProc? _hookProc;
    private bool _enabled;
    private bool _disposed;

    // Track which scan codes are currently pressed (for full-state writes)
    private readonly byte[] _keyState = new byte[256];

    public KeyBroadcastManager(KeyInputWriter writer)
    {
        _writer = writer;
    }

    /// <summary>Whether key broadcasting is currently active.</summary>
    public bool Enabled => _enabled;

    /// <summary>Number of target processes currently registered.</summary>
    public int TargetCount => _targetPids.Count;

    /// <summary>
    /// Add an EQ process as a broadcast target. Opens shared memory for it.
    /// </summary>
    public void AddTarget(int pid)
    {
        if (_targetPids.Add(pid))
        {
            _writer.Open(pid);
            if (_enabled) _writer.Activate(pid);
            FileLogger.Info($"KeyBroadcast: added target PID {pid} ({_targetPids.Count} targets)");
        }
    }

    /// <summary>
    /// Remove an EQ process from broadcast targets. Deactivates and closes shared memory.
    /// </summary>
    public void RemoveTarget(int pid)
    {
        if (_targetPids.Remove(pid))
        {
            _writer.Deactivate(pid);
            _writer.Close(pid);
            FileLogger.Info($"KeyBroadcast: removed target PID {pid} ({_targetPids.Count} targets)");
        }
    }

    /// <summary>
    /// Enable key broadcasting. Installs the keyboard hook and activates all targets.
    /// </summary>
    public void Enable()
    {
        if (_enabled) return;
        _enabled = true;

        InstallHook();
        foreach (var pid in _targetPids)
            _writer.Activate(pid);

        FileLogger.Info($"KeyBroadcast: enabled ({_targetPids.Count} targets)");
    }

    /// <summary>
    /// Disable key broadcasting. Removes the keyboard hook and deactivates all targets.
    /// </summary>
    public void Disable()
    {
        if (!_enabled) return;
        _enabled = false;

        UninstallHook();
        Array.Clear(_keyState, 0, 256);
        foreach (var pid in _targetPids)
            _writer.Deactivate(pid);

        FileLogger.Info("KeyBroadcast: disabled");
    }

    /// <summary>
    /// Set the focused EQ PID. This process is excluded from broadcast targets
    /// (it receives real keyboard input directly).
    /// </summary>
    public void SetFocusedPid(int pid)
    {
        // Deactivate the focused process, activate others
        foreach (var targetPid in _targetPids)
        {
            if (targetPid == pid)
                _writer.Deactivate(targetPid);
            else if (_enabled)
                _writer.Activate(targetPid);
        }
    }

    private void InstallHook()
    {
        if (_hookHandle != IntPtr.Zero) return;

        // Must store delegate as field to prevent GC collection
        _hookProc = KeyboardHookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _hookProc,
            NativeMethods.GetModuleHandle(curModule.ModuleName),
            0);

        if (_hookHandle == IntPtr.Zero)
            FileLogger.Warn("KeyBroadcast: SetWindowsHookEx failed");
        else
            FileLogger.Info("KeyBroadcast: keyboard hook installed");
    }

    private void UninstallHook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            FileLogger.Info("KeyBroadcast: keyboard hook removed");
        }
        _hookProc = null;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled && _targetPids.Count > 0)
        {
            var kbd = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();

            // Convert virtual key to scan code
            uint scanCode = kbd.scanCode;
            if (scanCode == 0)
                scanCode = NativeMethods.MapVirtualKeyW(kbd.vkCode, 0); // MAPVK_VK_TO_VSC

            if (scanCode > 0 && scanCode < 256)
            {
                bool pressed = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                _keyState[scanCode] = pressed ? (byte)0x80 : (byte)0x00;

                // Write to all active (non-focused) targets
                foreach (var pid in _targetPids)
                {
                    _writer.SetKey(pid, (byte)scanCode, pressed);
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disable();
        _targetPids.Clear();
    }
}
