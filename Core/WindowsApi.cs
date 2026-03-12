using System.Runtime.InteropServices;

namespace EQSwitch.Core;

/// <summary>
/// Production implementation of IWindowsApi — delegates to NativeMethods.
/// </summary>
public class WindowsApi : IWindowsApi
{
    public bool IsWindow(IntPtr hwnd) => NativeMethods.IsWindow(hwnd);
    public bool IsHungAppWindow(IntPtr hwnd) => NativeMethods.IsHungAppWindow(hwnd);
    public bool ShowWindow(IntPtr hwnd, int nCmdShow) => NativeMethods.ShowWindow(hwnd, nCmdShow);
    public bool SetForegroundWindow(IntPtr hwnd) => NativeMethods.SetForegroundWindow(hwnd);

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

    public IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex)
        => NativeMethods.GetWindowLongPtr(hwnd, nIndex);

    public IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong)
        => NativeMethods.SetWindowLongPtr(hwnd, nIndex, dwNewLong);

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
        return monitors;
    }

    public bool SetProcessAffinity(int processId, long affinityMask)
        => AffinityManager.SetProcessAffinity(processId, affinityMask);

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
