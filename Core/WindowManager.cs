using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Handles window positioning, switching, arrangement, and style manipulation.
/// Win32 calls go through IWindowsApi for testability.
/// </summary>
public class WindowManager
{
    private readonly AppConfig _config;
    private readonly IWindowsApi _api;

    public WindowManager(AppConfig config, IWindowsApi? api = null)
    {
        _config = config;
        _api = api ?? new WindowsApi();
    }

    // ─── Focus Switching ──────────────────────────────────────────

    /// <summary>
    /// Switch focus to a specific EQ client. Sets it as foreground window
    /// and optionally restores it if minimized.
    /// </summary>
    public bool SwitchToClient(EQClient client)
    {
        if (!_api.IsWindow(client.WindowHandle))
            return false;

        if (_api.IsHungAppWindow(client.WindowHandle))
        {
            FileLogger.Info($"SwitchToClient: skipping hung window {client}");
            return false;
        }

        try
        {
            _api.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);
            _api.ForceForegroundWindow(client.WindowHandle);
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"SwitchToClient failed", ex);
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
    /// When BorderlessFullscreen is enabled, uses full monitor bounds with Y+1 offset
    /// to cover the taskbar (WinEQ method).
    /// </summary>
    private void ArrangeSingleScreen(IReadOnlyList<EQClient> clients)
    {
        bool borderless = _config.Layout.BorderlessFullscreen;
        var monitor = GetTargetMonitor(borderless);
        var layout = _config.Layout;
        int cols = layout.Columns;
        int rows = layout.Rows;
        int yOffset = borderless ? 1 : layout.TopOffset; // +1 Y offset for borderless

        int cellWidth = monitor.Width / cols;
        int cellHeight = monitor.Height / rows;

        FileLogger.Info($"ArrangeSingleScreen: monitor bounds L={monitor.Left} T={monitor.Top} R={monitor.Right} B={monitor.Bottom} ({monitor.Width}x{monitor.Height}), grid {cols}x{rows}, cell {cellWidth}x{cellHeight}");

        for (int i = 0; i < clients.Count && i < cols * rows; i++)
        {
            var client = clients[i];
            if (!_api.IsWindow(client.WindowHandle)) continue;
            if (_api.IsHungAppWindow(client.WindowHandle))
            {
                FileLogger.Info($"ArrangeWindows: skipping hung window {client}");
                continue;
            }

            int col = i % cols;
            int row = i / cols;
            int x = monitor.Left + (col * cellWidth);
            int y = monitor.Top + (row * cellHeight) + yOffset;

            // Restore if minimized
            _api.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

            if (borderless)
                ApplyBorderlessStyle(client.WindowHandle);
            else if (layout.RemoveTitleBars)
                RemoveTitleBar(client.WindowHandle);

            _api.SetWindowPos(
                client.WindowHandle,
                IntPtr.Zero,
                x, y, cellWidth, cellHeight,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);

            FileLogger.Info($"ArrangeSingleScreen: {client} → pos ({x},{y}) size ({cellWidth}x{cellHeight})");
        }

        string mode = borderless ? "borderless fullscreen" : $"{cols}x{rows} grid";
        FileLogger.Info($"ArrangeSingleScreen: {clients.Count} window(s) in {mode}");
    }

    /// <summary>
    /// Multi-monitor mode: distribute windows across physical monitors.
    /// Each window fills its assigned monitor. Cycles through monitors if
    /// there are more windows than screens.
    /// When BorderlessFullscreen is enabled, uses full monitor bounds with Y+1 offset.
    /// </summary>
    private void ArrangeMultiMonitor(IReadOnlyList<EQClient> clients)
    {
        bool borderless = _config.Layout.BorderlessFullscreen;
        var monitors = borderless ? _api.GetAllMonitorBounds() : _api.GetAllMonitorWorkAreas();
        if (monitors.Count == 0) return;

        int yOffset = borderless ? 1 : _config.Layout.TopOffset;

        for (int i = 0; i < clients.Count; i++)
        {
            var client = clients[i];
            if (!_api.IsWindow(client.WindowHandle)) continue;
            if (_api.IsHungAppWindow(client.WindowHandle))
            {
                FileLogger.Info($"ArrangeMultiMonitor: skipping hung window {client}");
                continue;
            }

            // Cycle through monitors: window 0 → monitor 0, window 1 → monitor 1, etc.
            var mon = monitors[i % monitors.Count];

            _api.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

            if (borderless)
                ApplyBorderlessStyle(client.WindowHandle);
            else if (_config.Layout.RemoveTitleBars)
                RemoveTitleBar(client.WindowHandle);

            _api.SetWindowPos(
                client.WindowHandle,
                IntPtr.Zero,
                mon.Left, mon.Top + yOffset, mon.Width, mon.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);
        }

        FileLogger.Info($"ArrangeMultiMonitor: {clients.Count} window(s) across {monitors.Count} monitor(s)" +
            (borderless ? " (borderless)" : ""));
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
            if (_api.IsHungAppWindow(client.WindowHandle))
            {
                FileLogger.Info($"SwapWindows: aborting — hung window {client}");
                return;
            }
        }

        // Capture current positions
        var positions = new List<WinRect>();
        foreach (var client in clients)
        {
            if (!_api.IsWindow(client.WindowHandle))
            {
                FileLogger.Info($"SwapWindows: window gone for {client}");
                return;
            }
            _api.GetWindowRect(client.WindowHandle, out var rect);
            positions.Add(rect);
        }

        // Restore all windows first (un-maximize)
        foreach (var client in clients)
            _api.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

        // Rotate: each window moves to the next window's position
        for (int i = 0; i < clients.Count; i++)
        {
            int nextIdx = (i + 1) % clients.Count;
            var nextPos = positions[nextIdx];

            _api.SetWindowPos(
                clients[i].WindowHandle,
                IntPtr.Zero,
                nextPos.Left, nextPos.Top,
                nextPos.Width, nextPos.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
        }

        FileLogger.Info($"SwapWindows: rotated {clients.Count} window positions");
    }

    // ─── Title Bar & Borderless Management ──────────────────────────

    /// <summary>
    /// Remove the title bar and borders from a window.
    /// </summary>
    public void RemoveTitleBar(IntPtr hwnd)
    {
        long style = _api.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~NativeMethods.WS_CAPTION;
        style &= ~NativeMethods.WS_THICKFRAME;
        _api.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, (IntPtr)style);

        _api.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Apply full borderless style — removes all window chrome including
    /// extended styles. Used for borderless fullscreen mode (WinEQ method).
    /// Strips: WS_CAPTION, WS_THICKFRAME, WS_SYSMENU, WS_MINIMIZEBOX, WS_MAXIMIZEBOX
    /// Extended: WS_EX_DLGMODALFRAME, WS_EX_CLIENTEDGE, WS_EX_STATICEDGE
    /// </summary>
    public void ApplyBorderlessStyle(IntPtr hwnd)
    {
        // Remove standard styles
        long style = _api.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~NativeMethods.WS_CAPTION;
        style &= ~NativeMethods.WS_THICKFRAME;
        style &= ~NativeMethods.WS_SYSMENU;
        style &= ~NativeMethods.WS_MINIMIZEBOX;
        style &= ~NativeMethods.WS_MAXIMIZEBOX;
        _api.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, (IntPtr)style);

        // Remove extended styles (border chrome remnants)
        long exStyle = _api.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle &= ~NativeMethods.WS_EX_DLGMODALFRAME;
        exStyle &= ~NativeMethods.WS_EX_CLIENTEDGE;
        exStyle &= ~NativeMethods.WS_EX_STATICEDGE;
        _api.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)exStyle);

        // Apply style changes
        _api.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Restore the title bar on a window.
    /// </summary>
    public void RestoreTitleBar(IntPtr hwnd)
    {
        long style = _api.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style |= NativeMethods.WS_CAPTION;
        style |= NativeMethods.WS_THICKFRAME;
        _api.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, (IntPtr)style);

        _api.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    // ─── Immediate Positioning ─────────────────────────────────────

    /// <summary>
    /// Immediately position a single client window on the target monitor,
    /// filling the work area. Used during launch to snap each window to
    /// the correct monitor as soon as it's discovered — prevents Windows
    /// from placing it across monitor boundaries.
    /// </summary>
    public void PositionOnTargetMonitor(EQClient client)
    {
        if (!_api.IsWindow(client.WindowHandle)) return;

        var monitor = GetTargetMonitor(false);

        _api.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

        _api.SetWindowPos(
            client.WindowHandle,
            IntPtr.Zero,
            monitor.Left, monitor.Top + _config.Layout.TopOffset,
            monitor.Width, monitor.Height - _config.Layout.TopOffset,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);

        FileLogger.Info($"PositionOnTargetMonitor: {client} → ({monitor.Left},{monitor.Top}) {monitor.Width}x{monitor.Height}");
    }

    // ─── Monitor Helpers ──────────────────────────────────────────

    /// <summary>
    /// Get the target monitor area for single-screen mode.
    /// When fullBounds is true, returns rcMonitor (includes taskbar area).
    /// When false, returns rcWork (excludes taskbar).
    /// Falls back to monitor 0 if target doesn't exist.
    /// </summary>
    private WinRect GetTargetMonitor(bool fullBounds = false)
    {
        var monitors = fullBounds ? _api.GetAllMonitorBounds() : _api.GetAllMonitorWorkAreas();
        int targetIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, Math.Max(0, monitors.Count - 1));
        return monitors.Count > 0 ? monitors[targetIdx] : new WinRect { Right = 1920, Bottom = 1080 };
    }
}
