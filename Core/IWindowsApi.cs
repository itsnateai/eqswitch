namespace EQSwitch.Core;

/// <summary>
/// Simple rectangle for monitor/window positions, decoupled from NativeMethods.
/// </summary>
public struct WinRect
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

/// <summary>
/// Abstraction over Win32 API calls used by WindowManager and AffinityManager.
/// Enables unit testing with mock implementations.
/// </summary>
public interface IWindowsApi
{
    // ─── Window Operations ───────────────────────────────────────────
    bool IsWindow(IntPtr hwnd);
    bool IsHungAppWindow(IntPtr hwnd);
    bool ShowWindow(IntPtr hwnd, int nCmdShow);
    bool SetForegroundWindow(IntPtr hwnd);
    bool BringWindowToTop(IntPtr hwnd);
    void ForceForegroundWindow(IntPtr hwnd);
    bool SetWindowPos(IntPtr hwnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    bool GetWindowRect(IntPtr hwnd, out WinRect rect);
    IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);
    IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    // ─── Monitor Operations ──────────────────────────────────────────
    List<WinRect> GetAllMonitorWorkAreas();
    List<WinRect> GetAllMonitorBounds();

    // ─── Process Operations ──────────────────────────────────────────
    bool SetProcessAffinity(int processId, long affinityMask);
    bool SetProcessPriority(int processId, uint priorityClass);
    (long processMask, long systemMask) GetProcessAffinity(int processId);
    uint GetProcessPriorityClass(int processId);
}
