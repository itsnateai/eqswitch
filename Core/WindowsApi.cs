// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Runtime.InteropServices;

namespace EQSwitch.Core;

/// <summary>
/// Production implementation of IWindowsApi — delegates to NativeMethods.
/// </summary>
public class WindowsApi : IWindowsApi
{
    public bool IsWindow(IntPtr hwnd) => NativeMethods.IsWindow(hwnd);
    public bool IsIconic(IntPtr hwnd) => NativeMethods.IsIconic(hwnd);
    public bool IsHungAppWindow(IntPtr hwnd) => NativeMethods.IsHungAppWindow(hwnd);

    public bool IsClientResponsive(IntPtr hwnd, out int lastErr)
    {
        IntPtr ok = NativeMethods.SendMessageTimeout(
            hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK,
            100, out _);
        lastErr = (ok == IntPtr.Zero) ? Marshal.GetLastWin32Error() : 0;
        return ok != IntPtr.Zero;
    }

    public bool ShowWindow(IntPtr hwnd, int nCmdShow) => NativeMethods.ShowWindow(hwnd, nCmdShow);
    public bool SetForegroundWindow(IntPtr hwnd) => NativeMethods.SetForegroundWindow(hwnd);
    public bool BringWindowToTop(IntPtr hwnd) => NativeMethods.BringWindowToTop(hwnd);

    /// <summary>
    /// Force a window to the foreground even when our process doesn't own the foreground lock.
    /// Uses AttachThreadInput to borrow the foreground thread's input queue, then brings the window up.
    /// This is the standard workaround for Windows' SetForegroundWindow restrictions.
    /// </summary>
    public void ForceForegroundWindow(IntPtr hwnd)
    {
        var fgHwnd = NativeMethods.GetForegroundWindow();
        uint currentThread = NativeMethods.GetCurrentThreadId();
        uint fgThread = NativeMethods.GetWindowThreadProcessId(fgHwnd, out _);

        bool attached = false;
        if (currentThread != fgThread)
        {
            attached = NativeMethods.AttachThreadInput(currentThread, fgThread, true);
        }

        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(currentThread, fgThread, false);
        }
    }

    public bool SetWindowPos(IntPtr hwnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags)
        => NativeMethods.SetWindowPos(hwnd, hWndInsertAfter, x, y, cx, cy, flags);

    public bool GetWindowRect(IntPtr hwnd, out WinRect rect)
    {
        bool result = NativeMethods.GetWindowRect(hwnd, out var nativeRect);
        rect = new WinRect
        {
            Left = nativeRect.Left,
            Top = nativeRect.Top,
            Right = nativeRect.Right,
            Bottom = nativeRect.Bottom
        };
        return result;
    }

    public bool GetClientScreenRect(IntPtr hwnd, out WinRect rect)
    {
        rect = default;
        if (!NativeMethods.GetClientRect(hwnd, out var cr)) return false;
        // GetClientRect is window-relative (0,0)-(w,h). Map the top-left and
        // bottom-right corners to screen coords so the result is directly
        // comparable to GetWindowRect (also screen coords).
        var tl = new NativeMethods.POINT { X = cr.Left, Y = cr.Top };
        var br = new NativeMethods.POINT { X = cr.Right, Y = cr.Bottom };
        if (!NativeMethods.ClientToScreen(hwnd, ref tl)) return false;
        if (!NativeMethods.ClientToScreen(hwnd, ref br)) return false;
        rect = new WinRect { Left = tl.X, Top = tl.Y, Right = br.X, Bottom = br.Y };
        return true;
    }

    public bool AdjustWindowRectEx(ref WinRect rect, uint style, bool hasMenu, uint exStyle)
    {
        var nativeRect = new NativeMethods.RECT
        {
            Left = rect.Left,
            Top = rect.Top,
            Right = rect.Right,
            Bottom = rect.Bottom
        };
        bool result = NativeMethods.AdjustWindowRectEx(ref nativeRect, style, hasMenu, exStyle);
        // v3.22.45 post-T3-Opus MEDIUM: capture last-error IMMEDIATELY after the
        // P/Invoke. CLR work between the native return and a later
        // Marshal.GetLastWin32Error() call (allocations, property setters) can
        // clobber the TEB last-error slot, so the failure-path log in the caller
        // would routinely surface 0 instead of the real Win32 error. Log here
        // where the wrapper is small enough that no intervening managed work
        // happens between the P/Invoke and this capture.
        if (!result)
        {
            int lastErr = Marshal.GetLastWin32Error();
            FileLogger.Warn($"WindowsApi.AdjustWindowRectEx failed: style=0x{style:X8} exStyle=0x{exStyle:X8} lastErr={lastErr}");
        }
        rect = new WinRect
        {
            Left = nativeRect.Left,
            Top = nativeRect.Top,
            Right = nativeRect.Right,
            Bottom = nativeRect.Bottom
        };
        return result;
    }

    public IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex)
        => NativeMethods.GetWindowLongPtr(hwnd, nIndex);

    public IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong)
        => NativeMethods.SetWindowLongPtr(hwnd, nIndex, dwNewLong);

    public bool SetWindowText(IntPtr hwnd, string text)
        => NativeMethods.SetWindowText(hwnd, text);

    public IntPtr BeginDeferWindowPos(int nNumWindows)
        => NativeMethods.BeginDeferWindowPos(nNumWindows);

    public IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hwnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags)
        => NativeMethods.DeferWindowPos(hWinPosInfo, hwnd, hWndInsertAfter, x, y, cx, cy, flags);

    public bool EndDeferWindowPos(IntPtr hWinPosInfo)
        => NativeMethods.EndDeferWindowPos(hWinPosInfo);

    public List<WinRect> GetAllMonitorWorkAreas()
    {
        var monitors = new List<WinRect>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFO
                {
                    cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
                };
                NativeMethods.GetMonitorInfo(hMonitor, ref info);
                monitors.Add(new WinRect
                {
                    Left = info.rcWork.Left,
                    Top = info.rcWork.Top,
                    Right = info.rcWork.Right,
                    Bottom = info.rcWork.Bottom
                });
                return true;
            }, IntPtr.Zero);
        // Sort left-to-right so monitor 0 is always the leftmost physical screen
        monitors.Sort((a, b) => a.Left.CompareTo(b.Left));
        return monitors;
    }

    public List<WinRect> GetAllMonitorBounds()
    {
        var monitors = new List<WinRect>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFO
                {
                    cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
                };
                NativeMethods.GetMonitorInfo(hMonitor, ref info);
                monitors.Add(new WinRect
                {
                    Left = info.rcMonitor.Left,
                    Top = info.rcMonitor.Top,
                    Right = info.rcMonitor.Right,
                    Bottom = info.rcMonitor.Bottom
                });
                return true;
            }, IntPtr.Zero);
        // Sort left-to-right so monitor 0 is always the leftmost physical screen
        monitors.Sort((a, b) => a.Left.CompareTo(b.Left));
        return monitors;
    }

    public bool SetProcessPriority(int processId, uint priorityClass)
    {
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_SET_INFORMATION, false, processId);
            if (hProcess == IntPtr.Zero) return false;
            return NativeMethods.SetPriorityClass(hProcess, priorityClass);
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }

    public (long processMask, long systemMask) GetProcessAffinity(int processId)
        => AffinityManager.GetProcessAffinity(processId);

    public uint GetProcessPriorityClass(int processId)
    {
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION, false, processId);
            if (hProcess == IntPtr.Zero) return 0;
            return NativeMethods.GetPriorityClass(hProcess);
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }
}
