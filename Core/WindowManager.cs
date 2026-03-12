using System.Diagnostics;
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

    // ─── Focus Switching ──────────────────────────────────────────

    /// <summary>
    /// Switch focus to a specific EQ client. Sets it as foreground window
    /// and optionally restores it if minimized.
    /// </summary>
    public bool SwitchToClient(EQClient client)
    {
        if (!NativeMethods.IsWindow(client.WindowHandle))
            return false;

        if (NativeMethods.IsHungAppWindow(client.WindowHandle))
        {
            Debug.WriteLine($"SwitchToClient: skipping hung window {client}");
            return false;
        }

        try
        {
            NativeMethods.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(client.WindowHandle);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SwitchToClient failed: {ex.Message}");
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

        int currentIndex = -1;
        if (current != null)
        {
            for (int j = 0; j < clients.Count; j++)
                if (clients[j] == current) { currentIndex = j; break; }
        }
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

        int currentIndex = 0;
        if (current != null)
        {
            for (int j = 0; j < clients.Count; j++)
                if (clients[j] == current) { currentIndex = j; break; }
        }
        int prevIndex = (currentIndex - 1 + clients.Count) % clients.Count;

        var prev = clients[prevIndex];
        return SwitchToClient(prev) ? prev : null;
    }

    // ─── Window Arrangement ───────────────────────────────────────

    /// <summary>
    /// Arrange all EQ client windows based on the current layout mode.
    /// In "multimonitor" mode: one window per physical monitor, full-screen.
    /// In "single" mode: grid layout on the target monitor.
    /// </summary>
    public void ArrangeWindows(IReadOnlyList<EQClient> clients)
    {
        if (clients.Count == 0) return;

        if (_config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase))
            ArrangeMultiMonitor(clients);
        else
            ArrangeSingleScreen(clients);
    }

    /// <summary>
    /// Single-screen mode: arrange all windows in a grid on the target monitor.
    /// </summary>
    private void ArrangeSingleScreen(IReadOnlyList<EQClient> clients)
    {
        var monitor = GetTargetMonitor();
        var layout = _config.Layout;
        int cols = layout.Columns;
        int rows = layout.Rows;
        int yOffset = layout.TopOffset;

        int cellWidth = monitor.Width / cols;
        int cellHeight = monitor.Height / rows;

        for (int i = 0; i < clients.Count && i < cols * rows; i++)
        {
            var client = clients[i];
            if (!NativeMethods.IsWindow(client.WindowHandle)) continue;
            if (NativeMethods.IsHungAppWindow(client.WindowHandle))
            {
                Debug.WriteLine($"ArrangeWindows: skipping hung window {client}");
                continue;
            }

            int col = i % cols;
            int row = i / cols;
            int x = monitor.Left + (col * cellWidth);
            int y = monitor.Top + (row * cellHeight) + yOffset;

            // Restore if minimized
            NativeMethods.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

            if (layout.RemoveTitleBars)
                RemoveTitleBar(client.WindowHandle);

            NativeMethods.SetWindowPos(
                client.WindowHandle,
                IntPtr.Zero,
                x, y, cellWidth, cellHeight,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);
        }

        Debug.WriteLine($"ArrangeSingleScreen: {clients.Count} window(s) in {cols}x{rows} grid");
    }

    /// <summary>
    /// Multi-monitor mode: distribute windows across physical monitors.
    /// Each window fills its assigned monitor. Cycles through monitors if
    /// there are more windows than screens.
    /// </summary>
    private void ArrangeMultiMonitor(IReadOnlyList<EQClient> clients)
    {
        var monitors = GetAllMonitors();
        if (monitors.Count == 0) return;

        int yOffset = _config.Layout.TopOffset;

        for (int i = 0; i < clients.Count; i++)
        {
            var client = clients[i];
            if (!NativeMethods.IsWindow(client.WindowHandle)) continue;
            if (NativeMethods.IsHungAppWindow(client.WindowHandle))
            {
                Debug.WriteLine($"ArrangeMultiMonitor: skipping hung window {client}");
                continue;
            }

            // Cycle through monitors: window 0 → monitor 0, window 1 → monitor 1, etc.
            var mon = monitors[i % monitors.Count];

            NativeMethods.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

            if (_config.Layout.RemoveTitleBars)
                RemoveTitleBar(client.WindowHandle);

            NativeMethods.SetWindowPos(
                client.WindowHandle,
                IntPtr.Zero,
                mon.Left, mon.Top + yOffset, mon.Width, mon.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);
        }

        Debug.WriteLine($"ArrangeMultiMonitor: {clients.Count} window(s) across {monitors.Count} monitor(s)");
    }

    /// <summary>
    /// Rotate window positions: each window moves to the next window's position.
    /// Window 1→2, 2→3, ..., N→1. Replicates AHK SwapWindows.
    /// </summary>
    public void SwapWindows(IReadOnlyList<EQClient> clients)
    {
        if (clients.Count < 2) return;

        // Check for hung windows — abort if any are unresponsive
        foreach (var client in clients)
        {
            if (NativeMethods.IsHungAppWindow(client.WindowHandle))
            {
                Debug.WriteLine($"SwapWindows: aborting — hung window {client}");
                return;
            }
        }

        // Capture current positions
        var positions = new List<NativeMethods.RECT>();
        foreach (var client in clients)
        {
            if (!NativeMethods.IsWindow(client.WindowHandle))
            {
                Debug.WriteLine($"SwapWindows: window gone for {client}");
                return;
            }
            NativeMethods.GetWindowRect(client.WindowHandle, out var rect);
            positions.Add(rect);
        }

        // Restore all windows first (un-maximize)
        foreach (var client in clients)
            NativeMethods.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

        // Rotate: each window moves to the next window's position
        for (int i = 0; i < clients.Count; i++)
        {
            int nextIdx = (i + 1) % clients.Count;
            var nextPos = positions[nextIdx];

            NativeMethods.SetWindowPos(
                clients[i].WindowHandle,
                IntPtr.Zero,
                nextPos.Left, nextPos.Top,
                nextPos.Width, nextPos.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
        }

        Debug.WriteLine($"SwapWindows: rotated {clients.Count} window positions");
    }

    // ─── Title Bar Management ─────────────────────────────────────

    /// <summary>
    /// Remove the title bar and borders from a window.
    /// </summary>
    public void RemoveTitleBar(IntPtr hwnd)
    {
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~NativeMethods.WS_CAPTION;
        style &= ~NativeMethods.WS_THICKFRAME;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, (IntPtr)style);

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
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style |= NativeMethods.WS_CAPTION;
        style |= NativeMethods.WS_THICKFRAME;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, (IntPtr)style);

        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    // ─── Monitor Helpers ──────────────────────────────────────────

    /// <summary>
    /// Get the work area of the target monitor (for single-screen mode).
    /// Falls back to monitor 0 if target doesn't exist.
    /// </summary>
    private NativeMethods.RECT GetTargetMonitor()
    {
        var monitors = GetAllMonitors();
        int targetIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, Math.Max(0, monitors.Count - 1));
        return monitors.Count > 0 ? monitors[targetIdx] : new NativeMethods.RECT { Right = 1920, Bottom = 1080 };
    }

    /// <summary>
    /// Enumerate all connected monitors and return their work areas (excludes taskbar).
    /// </summary>
    private static List<NativeMethods.RECT> GetAllMonitors()
    {
        var monitors = new List<NativeMethods.RECT>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFO
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
                };
                NativeMethods.GetMonitorInfo(hMonitor, ref info);
                monitors.Add(info.rcWork);
                return true;
            }, IntPtr.Zero);

        return monitors;
    }
}
