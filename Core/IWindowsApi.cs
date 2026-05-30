// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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
    bool IsIconic(IntPtr hwnd);
    bool IsHungAppWindow(IntPtr hwnd);

    /// <summary>
    /// Pump-responsiveness probe via SendMessageTimeout(WM_NULL,
    /// SMTO_ABORTIFHUNG|SMTO_BLOCK, 100ms). Returns false when the target's
    /// thread is kernel-marked hung OR not dispatching messages within 100ms —
    /// tighter than IsHungAppWindow's 5s threshold (v3.22.22 round-5).
    /// On zero-return, lastErr captures GetLastWin32Error so callers can
    /// disambiguate timeout (lastErr=0) from window-gone
    /// (ERROR_INVALID_WINDOW_HANDLE=1400) from access-denied
    /// (ERROR_ACCESS_DENIED=5) in warn logs (v3.22.29 Orphan-2).
    /// </summary>
    bool IsClientResponsive(IntPtr hwnd, out int lastErr);

    bool ShowWindow(IntPtr hwnd, int nCmdShow);
    bool SetForegroundWindow(IntPtr hwnd);
    bool BringWindowToTop(IntPtr hwnd);
    void ForceForegroundWindow(IntPtr hwnd);
    bool SetWindowPos(IntPtr hwnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    bool GetWindowRect(IntPtr hwnd, out WinRect rect);

    /// <summary>
    /// v3.22.84 — the window's CLIENT area in SCREEN coordinates (GetClientRect
    /// + ClientToScreen). Paired with <see cref="GetWindowRect"/>, the per-edge
    /// deltas give eqgame's REAL non-client frame, which the Windowed frame-measure
    /// correction uses to land the visible client flush on the monitor (WinEQ2
    /// "measure, don't predict"). Returns false if the window is gone.
    /// </summary>
    bool GetClientScreenRect(IntPtr hwnd, out WinRect rect);

    /// <summary>
    /// v3.22.45 — given a desired CLIENT-area rect + window style, returns the
    /// OUTER-window rect that wraps it. Used by ComputeSlimTitlebarOuterRect
    /// to size the slim-titlebar window so its visible client area covers the
    /// full monitor on Win11 (where DWM keeps ~8 px invisible frame on each
    /// edge regardless of WS_THICKFRAME). The input rect is mutated in place
    /// to the outer dims, matching the underlying Win32 AdjustWindowRectEx
    /// contract.
    /// </summary>
    bool AdjustWindowRectEx(ref WinRect rect, uint style, bool hasMenu, uint exStyle);
    IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);
    IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);
    bool SetWindowText(IntPtr hwnd, string text);

    // ─── Deferred Window Positioning ────────────────────────────────
    IntPtr BeginDeferWindowPos(int nNumWindows);
    IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hwnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    bool EndDeferWindowPos(IntPtr hWinPosInfo);

    // ─── Monitor Operations ──────────────────────────────────────────
    List<WinRect> GetAllMonitorWorkAreas();
    List<WinRect> GetAllMonitorBounds();

    // ─── Process Operations ──────────────────────────────────────────
    bool SetProcessPriority(int processId, uint priorityClass);
    (long processMask, long systemMask) GetProcessAffinity(int processId);
    uint GetProcessPriorityClass(int processId);
}
