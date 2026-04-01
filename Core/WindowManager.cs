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
    /// Single-screen mode: arrange all windows on the target monitor.
    /// In 1x1 (stacked) mode, windows keep their own size from eqclient.ini.
    /// Slim Titlebar mode pushes the titlebar off-screen (WinEQ2 method).
    /// </summary>
    private void ArrangeSingleScreen(IReadOnlyList<EQClient> clients)
    {
        bool slimTitlebar = _config.Layout.SlimTitlebar;
        var monitor = GetTargetMonitor(slimTitlebar);
        var layout = _config.Layout;

        FileLogger.Info($"ArrangeSingleScreen: monitor bounds L={monitor.Left} T={monitor.Top} R={monitor.Right} B={monitor.Bottom} ({monitor.Width}x{monitor.Height})");

        for (int i = 0; i < clients.Count; i++)
        {
            var client = clients[i];
            if (!_api.IsWindow(client.WindowHandle)) continue;
            if (_api.IsHungAppWindow(client.WindowHandle))
            {
                FileLogger.Info($"ArrangeWindows: skipping hung window {client}");
                continue;
            }

            if (_api.IsIconic(client.WindowHandle))
                _api.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

            SetWindowTitle(client, i);

            if (slimTitlebar)
            {
                ApplySlimTitlebar(client.WindowHandle, monitor, layout.TitlebarOffset);
            }
            else
            {
                // Move to target monitor origin without resizing — EQ keeps its own window size
                int sx = monitor.Left;
                int sy = monitor.Top + layout.TopOffset;
                _api.SetWindowPos(
                    client.WindowHandle,
                    IntPtr.Zero,
                    sx, sy, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                FileLogger.Info($"ArrangeSingleScreen: {client} → stacked at ({sx},{sy})");
            }
        }

        string mode = slimTitlebar ? "slim titlebar" : "stacked";
        FileLogger.Info($"ArrangeSingleScreen: {clients.Count} window(s) in {mode}");
    }

    /// <summary>
    /// Multi-monitor mode: distribute windows across physical monitors.
    /// Each window fills its assigned monitor. Cycles through monitors if
    /// there are more windows than screens.
    /// </summary>
    private void ArrangeMultiMonitor(IReadOnlyList<EQClient> clients)
    {
        bool slimTitlebar = _config.Layout.SlimTitlebar;
        var monitors = slimTitlebar ? _api.GetAllMonitorBounds() : _api.GetAllMonitorWorkAreas();
        if (monitors.Count == 0) return;

        // Build ordered monitor list: primary first, then secondary
        var primaryIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, monitors.Count - 1);
        int secondaryIdx;
        if (_config.Layout.SecondaryMonitor >= 0 && _config.Layout.SecondaryMonitor < monitors.Count)
            secondaryIdx = _config.Layout.SecondaryMonitor;
        else
            secondaryIdx = primaryIdx == 0 && monitors.Count > 1 ? 1 : 0;

        var monitorOrder = new List<WinRect> { monitors[primaryIdx] };
        if (monitors.Count > 1)
            monitorOrder.Add(monitors[secondaryIdx]);

        for (int i = 0; i < clients.Count; i++)
        {
            var client = clients[i];
            if (!_api.IsWindow(client.WindowHandle)) continue;
            if (_api.IsHungAppWindow(client.WindowHandle))
            {
                FileLogger.Info($"ArrangeMultiMonitor: skipping hung window {client}");
                continue;
            }

            var mon = monitorOrder[i % monitorOrder.Count];

            if (_api.IsIconic(client.WindowHandle))
                _api.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

            SetWindowTitle(client, i);

            if (slimTitlebar)
            {
                ApplySlimTitlebar(client.WindowHandle, mon, _config.Layout.TitlebarOffset);
            }
            else
            {
                _api.SetWindowPos(
                    client.WindowHandle,
                    IntPtr.Zero,
                    mon.Left, mon.Top + _config.Layout.TopOffset, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }

            string monLabel = i % monitorOrder.Count == 0 ? "primary" : "secondary";
            FileLogger.Info($"ArrangeMultiMonitor: {client} → {monLabel} monitor ({mon.Left},{mon.Top}) {mon.Width}x{mon.Height}" +
                (slimTitlebar ? " (slim titlebar)" : ""));
        }

        string modeLabel = slimTitlebar ? " (slim titlebar)" : "";
        FileLogger.Info($"ArrangeMultiMonitor: {clients.Count} window(s), primary={primaryIdx} secondary={secondaryIdx}{modeLabel}");
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

        // Restore minimized windows first
        foreach (var client in clients)
            if (_api.IsIconic(client.WindowHandle))
                _api.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

        // Atomic batch move — all windows reposition in a single
        // screen update, eliminating the desktop flash between moves.
        var hdwp = _api.BeginDeferWindowPos(clients.Count);
        if (hdwp == IntPtr.Zero)
        {
            FileLogger.Warn("SwapWindows: BeginDeferWindowPos failed, falling back to sequential");
            goto sequential;
        }

        for (int i = 0; i < clients.Count; i++)
        {
            int nextIdx = (i + 1) % clients.Count;
            var nextPos = positions[nextIdx];

            hdwp = _api.DeferWindowPos(
                hdwp, clients[i].WindowHandle, IntPtr.Zero,
                nextPos.Left, nextPos.Top,
                nextPos.Width, nextPos.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

            if (hdwp == IntPtr.Zero)
            {
                FileLogger.Warn($"SwapWindows: DeferWindowPos failed at index {i}, falling back to sequential");
                goto sequential;
            }
        }

        _api.EndDeferWindowPos(hdwp);
        FileLogger.Info($"SwapWindows: rotated {clients.Count} window positions (atomic)");
        return;

    sequential:
        for (int i = 0; i < clients.Count; i++)
        {
            int nextIdx = (i + 1) % clients.Count;
            var nextPos = positions[nextIdx];

            _api.SetWindowPos(
                clients[i].WindowHandle,
                IntPtr.Zero,
                nextPos.Left, nextPos.Top,
                nextPos.Width, nextPos.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        FileLogger.Info($"SwapWindows: rotated {clients.Count} window positions (sequential fallback)");
    }

    /// <summary>
    /// After a swap, resize each window to fill whichever monitor it's currently on.
    /// Does NOT change position — just adjusts size to match the monitor dimensions.
    /// </summary>
    public void ResizeToCurrentMonitors(IReadOnlyList<EQClient> clients)
    {
        var monitors = _api.GetAllMonitorWorkAreas();
        if (monitors.Count == 0) return;

        int yOffset = _config.Layout.TopOffset;

        foreach (var client in clients)
        {
            if (!_api.IsWindow(client.WindowHandle)) continue;

            // Find which monitor this window is currently on
            _api.GetWindowRect(client.WindowHandle, out var rect);
            int centerX = rect.Left + rect.Width / 2;
            int centerY = rect.Top + rect.Height / 2;

            WinRect? bestMon = null;
            foreach (var mon in monitors)
            {
                if (centerX >= mon.Left && centerX < mon.Right &&
                    centerY >= mon.Top && centerY < mon.Bottom)
                {
                    bestMon = mon;
                    break;
                }
            }
            if (bestMon == null) continue;
            var m = bestMon.Value;

            // SW_RESTORE first in case EQ locked its size in a maximized/minimized state
            if (_api.IsIconic(client.WindowHandle))
                _api.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

            _api.SetWindowPos(
                client.WindowHandle, IntPtr.Zero,
                m.Left, m.Top + yOffset, m.Width, m.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);

            FileLogger.Info($"ResizeToCurrentMonitors: {client} → ({m.Left},{m.Top + yOffset}) {m.Width}x{m.Height}");
        }
    }

    // ─── Window Style Management ──────────────────────────────────

    /// <summary>
    /// Lightweight slim titlebar apply for all clients — just repositions windows
    /// without the full arrange logic. Used by foreground hook and auto-apply.
    /// </summary>
    public void ApplySlimTitlebarToAll(IReadOnlyList<EQClient> clients)
    {
        if (!_config.Layout.SlimTitlebar) return;
        var monitor = GetTargetMonitor(true);
        int offset = _config.Layout.TitlebarOffset;

        for (int i = 0; i < clients.Count; i++)
        {
            var client = clients[i];
            if (!_api.IsWindow(client.WindowHandle)) continue;

            // Re-apply custom window title if EQ overwrote it
            SetWindowTitle(client, i);

            // Check if already positioned correctly — avoid unnecessary repositioning
            _api.GetWindowRect(client.WindowHandle, out var rect);
            int expectedY = monitor.Top - offset;
            if (rect.Top == expectedY) continue;

            ApplySlimTitlebar(client.WindowHandle, monitor, offset);
        }
    }

    /// <summary>
    /// Apply slim titlebar mode: position window so the titlebar is partially hidden
    /// above the top edge of the monitor, and oversize the window height to compensate.
    /// The game fills the full monitor height while a thin titlebar strip remains visible.
    /// This is the WinEQ2 method — no style modification needed, just positioning.
    /// </summary>
    public void ApplySlimTitlebar(IntPtr hwnd, WinRect monitor, int titlebarOffset)
    {
        // Strip WS_THICKFRAME for thin border, KEEP WS_CAPTION for draggable titlebar
        long style = _api.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~NativeMethods.WS_THICKFRAME;
        _api.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, (IntPtr)style);

        // Step 1: Apply style change only (no move, no resize).
        // Use NOACTIVATE instead of SHOWWINDOW — SHOWWINDOW triggers EQ's
        // focus-loss handler during initialization, causing the game to minimize.
        _api.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

        // Step 2: Position and size the window to cover the full monitor.
        // We MUST set explicit size — stripping WS_THICKFRAME shrinks the window
        // (thick frame borders ~7px each side are gone), and the bottomOffset in
        // the INI reduces the client area further. Without explicit sizing, the
        // window is too short to cover the taskbar.
        // Height = monitorHeight + titlebarOffset ensures the window spans from
        // y (above the monitor) to exactly the monitor's bottom edge.
        int x = monitor.Left;
        int y = monitor.Top - titlebarOffset;
        int w = monitor.Right - monitor.Left;
        int h = (monitor.Bottom - monitor.Top) + titlebarOffset;
        _api.SetWindowPos(
            hwnd, IntPtr.Zero, x, y, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        FileLogger.Info($"ApplySlimTitlebar: hwnd={hwnd} → ({x},{y}) {w}x{h}, offset={titlebarOffset}px hidden");
    }

    /// <summary>
    /// Set a custom window title using the template from config.
    /// Supports placeholders: {CHAR} = character name, {SLOT} = slot number, {PID} = process ID.
    /// </summary>
    public void SetWindowTitle(EQClient client, int slotIndex)
    {
        var template = _config.Layout.WindowTitleTemplate;
        if (string.IsNullOrEmpty(template)) return;
        if (!_api.IsWindow(client.WindowHandle)) return;

        // Resolve character name: prefer account preset, fall back to EQ window title
        var charName = "";

        // Look up by slot index in auto-login accounts
        if (slotIndex < _config.Accounts.Count)
        {
            var accountName = _config.Accounts[slotIndex].CharacterName;
            if (!string.IsNullOrEmpty(accountName))
                charName = accountName;
        }

        // Fall back to EQ's native window title if no account match
        if (string.IsNullOrEmpty(charName))
        {
            int len = NativeMethods.GetWindowTextLength(client.WindowHandle);
            if (len > 0)
            {
                var sb = new System.Text.StringBuilder(len + 1);
                NativeMethods.GetWindowText(client.WindowHandle, sb, sb.Capacity);
                var currentNative = sb.ToString();
                if (currentNative.StartsWith("EverQuest", StringComparison.Ordinal))
                    client.OriginalTitle = currentNative;
            }

            if (!string.IsNullOrEmpty(client.OriginalTitle) && client.OriginalTitle.Contains(" - "))
                charName = client.OriginalTitle.Split(" - ", 2)[1];
        }

        var title = template
            .Replace("{CHAR}", charName)
            .Replace("{SLOT}", (slotIndex + 1).ToString())
            .Replace("{PID}", client.ProcessId.ToString())
            .Trim();

        // Skip if already set — avoids unnecessary Win32 call on every guard tick
        if (client.WindowTitle == title) return;

        _api.SetWindowText(client.WindowHandle, title);
        client.WindowTitle = title;
        FileLogger.Info($"SetWindowTitle: {client} → \"{title}\"");
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

    /// <summary>
    /// Get the full monitor bounds for the target monitor (including taskbar area).
    /// Used by TrayManager to write hook config with correct coordinates.
    /// </summary>
    public WinRect GetTargetMonitorBounds() => GetTargetMonitor(fullBounds: true);
}
