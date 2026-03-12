using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Handles window positioning, switching, arrangement, and style manipulation.
/// All Win32 calls go through NativeMethods — no raw DllImport here.
/// </summary>
public class WindowManager
{
    private readonly AppConfig _config;

    public WindowManager(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Switch focus to a specific EQ client. Sets it as foreground window
    /// and optionally restores it if minimized.
    /// </summary>
    public bool SwitchToClient(EQClient client)
    {
        if (!NativeMethods.IsWindow(client.WindowHandle))
            return false;

        try
        {
            // Restore if minimized
            NativeMethods.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

            // Bring to front
            NativeMethods.SetForegroundWindow(client.WindowHandle);

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SwitchToClient failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Cycle to the next EQ client in the list.
    /// Returns the client that was switched to, or null if failed.
    /// </summary>
    public EQClient? CycleNext(IReadOnlyList<EQClient> clients, EQClient? current)
    {
        if (clients.Count == 0) return null;

        int currentIndex = current != null ? clients.IndexOf(current) : -1;
        int nextIndex = (currentIndex + 1) % clients.Count;

        var next = clients[nextIndex];
        return SwitchToClient(next) ? next : null;
    }

    /// <summary>
    /// Cycle to the previous EQ client in the list.
    /// </summary>
    public EQClient? CyclePrev(IReadOnlyList<EQClient> clients, EQClient? current)
    {
        if (clients.Count == 0) return null;

        int currentIndex = current != null ? clients.IndexOf(current) : 0;
        int prevIndex = (currentIndex - 1 + clients.Count) % clients.Count;

        var prev = clients[prevIndex];
        return SwitchToClient(prev) ? prev : null;
    }

    /// <summary>
    /// Arrange all EQ client windows in a grid layout on the target monitor.
    /// </summary>
    public void ArrangeWindows(IReadOnlyList<EQClient> clients)
    {
        if (clients.Count == 0) return;

        var monitor = GetTargetMonitor();
        var layout = _config.Layout;
        int cols = layout.Columns;
        int rows = layout.Rows;

        int cellWidth = monitor.Width / cols;
        int cellHeight = monitor.Height / rows;

        for (int i = 0; i < clients.Count && i < cols * rows; i++)
        {
            var client = clients[i];
            if (!NativeMethods.IsWindow(client.WindowHandle)) continue;

            int col = i % cols;
            int row = i / cols;
            int x = monitor.Left + (col * cellWidth);
            int y = monitor.Top + (row * cellHeight);

            // Remove title bar if configured
            if (layout.RemoveTitleBars)
                RemoveTitleBar(client.WindowHandle);

            NativeMethods.SetWindowPos(
                client.WindowHandle,
                IntPtr.Zero,
                x, y, cellWidth, cellHeight,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);
        }
    }

    /// <summary>
    /// Remove the title bar and borders from a window.
    /// </summary>
    public void RemoveTitleBar(IntPtr hwnd)
    {
        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        style &= ~NativeMethods.WS_CAPTION;
        style &= ~NativeMethods.WS_THICKFRAME;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style);

        // Force the window to redraw with the new style
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Restore the title bar on a window.
    /// </summary>
    public void RestoreTitleBar(IntPtr hwnd)
    {
        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        style |= NativeMethods.WS_CAPTION;
        style |= NativeMethods.WS_THICKFRAME;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style);

        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Get the work area of the target monitor.
    /// </summary>
    private NativeMethods.RECT GetTargetMonitor()
    {
        var monitors = new List<NativeMethods.RECT>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                NativeMethods.GetMonitorInfo(hMonitor, ref info);
                monitors.Add(info.rcWork); // rcWork excludes taskbar
                return true;
            }, IntPtr.Zero);

        int targetIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, monitors.Count - 1);
        return monitors.Count > 0 ? monitors[targetIdx] : new NativeMethods.RECT { Right = 1920, Bottom = 1080 };
    }
}
