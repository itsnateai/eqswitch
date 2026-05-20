// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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
    [ThreadStatic] private static System.Text.StringBuilder? _titleSb;

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
    ///
    /// v3.22.20: optional <paramref name="monitorSlotByPid"/> overrides the
    /// legacy clientIndex-based monitor assignment in multi-monitor mode.
    /// When provided, each client's monitor slot is read from the map (by
    /// ProcessId), enabling SwitchKey-driven slot rotation. Null falls back
    /// to clientIndex (matches v3.22.19 behavior).
    /// </summary>
    public void ArrangeWindows(IReadOnlyList<EQClient> clients, IReadOnlyDictionary<int, int>? monitorSlotByPid = null)
    {
        if (clients.Count == 0) return;

        if (_config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase))
            ArrangeMultiMonitor(clients, monitorSlotByPid);
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
    /// <para>
    /// v3.22.20: <paramref name="monitorSlotByPid"/> (when non-null) maps
    /// each ProcessId to its assigned monitor slot. Enables true rotation
    /// via SwitchKey instead of the legacy clientIndex-positional assignment.
    /// </para>
    /// <para>
    /// v3.22.21 refactor: two-pass design + lock-to-primary-dims policy.
    /// <list type="bullet">
    /// <item><b>Pass 1 (sequential, style)</b> — per-client WS_THICKFRAME
    /// strip/restore + SWP_FRAMECHANGED notify. Can't be batched: each
    /// style change needs its own non-client reflow.</item>
    /// <item><b>Pass 2 (batched, position)</b> — BeginDeferWindowPos /
    /// DeferWindowPos × N / EndDeferWindowPos. All windows reposition in a
    /// single DWM composite — eliminates the cascade flicker that v3.22.20
    /// showed during SwitchKey swap (windows visibly moved one-by-one).
    /// Falls back to sequential SetWindowPos on hdwp failure.</item>
    /// <item><b>Lock-to-primary-dims</b> — both windows sized to primary's
    /// bounds with each monitor's own origin. Eliminates the cross-monitor
    /// smoosh symptom: DX swap-chain stays at one constant size across
    /// SwitchKey swaps, so font/UI textures don't get stretched. Auto-
    /// degrades to per-monitor-fit when monitors differ too much
    /// (|Δ| &gt; 200px on either axis) or have mixed slim/non-slim flags
    /// — power users with 4K+1080p get per-monitor-fit and the v3.22.21
    /// "Fix Windows" hotkey for manual DX reinit.</item>
    /// </list>
    /// </para>
    /// </summary>
    private void ArrangeMultiMonitor(IReadOnlyList<EQClient> clients, IReadOnlyDictionary<int, int>? monitorSlotByPid = null)
    {
        // v3.22.19: per-monitor slim override. Primary uses SlimTitlebar,
        // secondary uses SlimTitlebarSecondary. Need BOTH full-bounds and
        // work-area lists so each client can pick the right one without a
        // second EnumDisplayMonitors round-trip.
        bool primarySlim = _config.Layout.SlimTitlebar;
        bool secondarySlim = _config.Layout.SlimTitlebarSecondary;
        var fullBounds = _api.GetAllMonitorBounds();
        var workAreas = _api.GetAllMonitorWorkAreas();
        if (fullBounds.Count == 0 || workAreas.Count == 0) return;
        // Defensive: both enumerations should return the same count in the
        // same order (both walk EnumDisplayMonitors). If they ever diverge,
        // log loud and bail rather than picking wrong-monitor bounds.
        if (fullBounds.Count != workAreas.Count)
        {
            FileLogger.Error($"ArrangeMultiMonitor: monitor enumeration count mismatch — fullBounds={fullBounds.Count} workAreas={workAreas.Count}, aborting arrange");
            return;
        }

        // Build ordered monitor list: primary first, then secondary.
        // v3.22.19: secondary resolution now uses ResolveSecondaryMonitorIdx
        // which skips tiny / portrait monitors (default min width 1000px).
        var primaryIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, fullBounds.Count - 1);
        int secondaryIdx = ResolveSecondaryMonitorIdx(_config.Layout.SecondaryMonitor, primaryIdx, fullBounds);

        // (chosen bounds, useSlim) per monitor slot. Bounds choice depends on
        // useSlim: slim → full monitor (covers taskbar); not slim → work area
        // (taskbar visible, normal frame).
        var monitorOrder = new List<(WinRect bounds, bool useSlim)>
        {
            (primarySlim ? fullBounds[primaryIdx] : workAreas[primaryIdx], primarySlim)
        };
        if (fullBounds.Count > 1)
            monitorOrder.Add((secondarySlim ? fullBounds[secondaryIdx] : workAreas[secondaryIdx], secondarySlim));

        // v3.22.21: lock-to-primary-dims policy gate. Requires:
        //   1. Two monitors (otherwise nothing to lock)
        //   2. Both monitors share the same slim flag (mixed slim/non-slim
        //      means user explicitly wants different rendering — respect it)
        //   3. Δ ≤ 200px on each axis (degrade for 4K+1080p mismatched configs)
        //   4. Primary fits within secondary on BOTH axes (otherwise locking
        //      would extend the window past secondary's edges)
        // When policy is ACTIVE, secondary client uses (secondary.origin) +
        // (primary.W × primary.H) — secondary monitor renders a stable-size
        // window, no DX swap-chain resize on SwitchKey swap.
        bool lockToPrimaryDims = false;
        WinRect primaryBounds = monitorOrder[0].bounds;
        if (monitorOrder.Count > 1)
        {
            bool bothSameSlim = monitorOrder[0].useSlim == monitorOrder[1].useSlim;
            var secBounds = monitorOrder[1].bounds;
            int wDelta = Math.Abs(primaryBounds.Width - secBounds.Width);
            int hDelta = Math.Abs(primaryBounds.Height - secBounds.Height);
            bool primaryFits = primaryBounds.Width <= secBounds.Width && primaryBounds.Height <= secBounds.Height;

            if (bothSameSlim && wDelta <= 200 && hDelta <= 200 && primaryFits)
            {
                lockToPrimaryDims = true;
                int hBand = secBounds.Height - primaryBounds.Height;
                int wBand = secBounds.Width - primaryBounds.Width;
                FileLogger.Info($"ArrangeMultiMonitor: lock-to-primary-dims ACTIVE — both windows {primaryBounds.Width}x{primaryBounds.Height}; secondary monitor has {wBand}px horizontal + {hBand}px vertical empty band (eliminates cross-monitor smoosh on SwitchKey swap)");
            }
            else
            {
                FileLogger.Warn($"ArrangeMultiMonitor: lock-to-primary-dims OFF — bothSameSlim={bothSameSlim} primary={primaryBounds.Width}x{primaryBounds.Height} secondary={secBounds.Width}x{secBounds.Height} wDelta={wDelta} hDelta={hDelta} primaryFits={primaryFits}. Using per-monitor-fit; cross-monitor SwitchKey swap may smoosh — press Fix Windows to force DX reinit");
            }
        }

        // Overflow logging (3+ clients on 2 monitors) is owned by
        // TrayManager.AssignNextFreeSlot — one-shot per new overflow level.
        // We deliberately do NOT mirror that log here: ArrangeMultiMonitor
        // runs on every SwitchKey swap, so a per-arrange log would spam
        // identical "client overflow" lines (T2-Sonnet + T3-Sonnet verifier
        // convergence).

        // Pass 1 — style work + compute target rects. Sequential because
        // each WS_THICKFRAME strip/restore needs its own non-client reflow
        // and can't share a DeferWindowPos batch with the move.
        // v3.22.21 smoke-2 (Nate 2026-05-20): wall-clock timing for taskbar-
        // flicker diagnosis (defer-and-log per Nate's choice).
        var swArrange = System.Diagnostics.Stopwatch.StartNew();
        int titlebarOffset = _config.Layout.TitlebarOffset;
        int topOffset = _config.Layout.TopOffset;
        var targets = new List<(IntPtr hwnd, int x, int y, int w, int h, uint flags, string logLabel)>(clients.Count);

        for (int i = 0; i < clients.Count; i++)
        {
            var client = clients[i];
            if (!_api.IsWindow(client.WindowHandle)) continue;
            if (_api.IsHungAppWindow(client.WindowHandle))
            {
                FileLogger.Info($"ArrangeMultiMonitor: skipping hung window {client}");
                continue;
            }

            // v3.22.20: per-PID slot lookup. If the caller (TrayManager)
            // supplied a slot map, this PID's assigned slot drives monitor
            // choice — otherwise fall back to clientIndex (legacy positional).
            // Lets SwitchKey rotate slot values and have ArrangeMultiMonitor
            // physically move each client without the hook DLL dragging back.
            int slot = (monitorSlotByPid != null && monitorSlotByPid.TryGetValue(client.ProcessId, out int mappedSlot))
                ? mappedSlot
                : i;
            int slotIdx = slot % monitorOrder.Count;
            var (mon, useSlim) = monitorOrder[slotIdx];

            // v3.22.21: lock-to-primary-dims. Secondary slot keeps its own
            // origin but inherits primary's W/H. Primary slot is unchanged
            // (it IS the source of dimensions). Synthetic bounds feed into
            // the same slim/non-slim formulae below.
            WinRect effectiveBounds;
            if (lockToPrimaryDims && slotIdx != 0)
            {
                effectiveBounds = new WinRect
                {
                    Left = mon.Left,
                    Top = mon.Top,
                    Right = mon.Left + primaryBounds.Width,
                    Bottom = mon.Top + primaryBounds.Height
                };
            }
            else
            {
                effectiveBounds = mon;
            }

            if (_api.IsIconic(client.WindowHandle))
                _api.ShowWindow(client.WindowHandle, NativeMethods.SW_RESTORE);

            SetWindowTitle(client, i);

            int x, y, w, h;
            uint swpFlags;

            if (useSlim)
            {
                // Step 1 of the original 2-step ApplySlimTitlebar: strip
                // WS_THICKFRAME so the window has a thin border.
                //
                // v3.22.21 smoke patch (Nate 2026-05-20): gate the style
                // change AND the SWP_FRAMECHANGED notify on "actually
                // needed". Steady-state SwitchKey rotation has windows
                // already in slim-style — pre-fix this unconditionally
                // fired SetWindowLongPtr (a no-op) + SetWindowPos with
                // SWP_FRAMECHANGED, which forces a non-client recompute
                // on every swap. Originally hypothesized as a SUSPECTED
                // contributor to the taskbar-flicker symptom; post-smoke
                // verification confirmed the flicker persists, so this
                // patch is NOT the root cause. Kept anyway: matches the
                // non-slim branch's pre-existing conditional guard
                // (symmetry), eliminates real WM_NCCALCSIZE traffic on a
                // cross-process window (non-free even when invisible).
                // Round-5 T3-Opus verdict: defensible standalone.
                long currentStyle = _api.GetWindowLongPtr(client.WindowHandle, NativeMethods.GWL_STYLE).ToInt64();
                long desiredStyle = currentStyle & ~NativeMethods.WS_THICKFRAME;
                if (currentStyle != desiredStyle)
                {
                    _api.SetWindowLongPtr(client.WindowHandle, NativeMethods.GWL_STYLE, (IntPtr)desiredStyle);

                    // Step 2 of the original 2-step: frame-change notify with
                    // SWP_NOMOVE|SWP_NOSIZE. Stays separate from the move
                    // (per project memory: focus-loss-prevention reason —
                    // don't collapse to one SetWindowPos). Now ONLY fires
                    // when the style actually changed.
                    _api.SetWindowPos(
                        client.WindowHandle, IntPtr.Zero, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
                }

                // Step 3 (the move) is queued for pass-2 batch.
                x = effectiveBounds.Left;
                y = effectiveBounds.Top - titlebarOffset;
                w = effectiveBounds.Right - effectiveBounds.Left;
                h = (effectiveBounds.Bottom - effectiveBounds.Top) + titlebarOffset;
                swpFlags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
            }
            else
            {
                // v3.22.19: non-slim multi-monitor branch sizes to work-area.
                // Restore WS_THICKFRAME (resize border) if a prior slim
                // arrangement stripped it — eqswitch-hook.cpp only ever
                // STRIPS the flag (one-way), so C# has to restore it here
                // when transitioning slim → non-slim.
                long style = _api.GetWindowLongPtr(client.WindowHandle, NativeMethods.GWL_STYLE).ToInt64();
                if ((style & NativeMethods.WS_THICKFRAME) == 0)
                {
                    style |= NativeMethods.WS_THICKFRAME;
                    _api.SetWindowLongPtr(client.WindowHandle, NativeMethods.GWL_STYLE, (IntPtr)style);
                    // SWP_FRAMECHANGED in the pass-2 batched move picks up
                    // the style change.
                }

                bool sizeToFit = effectiveBounds.Width > 0 && effectiveBounds.Height > topOffset;
                x = effectiveBounds.Left;
                y = effectiveBounds.Top + topOffset;
                w = sizeToFit ? effectiveBounds.Width : 0;
                h = sizeToFit ? effectiveBounds.Height - topOffset : 0;
                swpFlags = sizeToFit
                    ? NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED
                    : NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED;
            }

            string monLabel = slotIdx == 0 ? "primary" : "secondary";
            string slimLabel = useSlim ? " (slim titlebar)" : " (normal frame, work-area)";
            string lockLabel = lockToPrimaryDims ? " [locked-to-primary]" : "";
            targets.Add((client.WindowHandle, x, y, w, h, swpFlags,
                $"{client} → {monLabel} monitor ({mon.Left},{mon.Top}) {w}x{h}{slimLabel}{lockLabel} [slot={slot}]"));
        }

        if (targets.Count == 0)
        {
            FileLogger.Info($"ArrangeMultiMonitor: no eligible clients to arrange (all hung/invalid)");
            return;
        }
        long tPass1 = swArrange.ElapsedMilliseconds;

        // Pass 2 — atomic batched positioning. Replaces v3.22.20's per-client
        // sequential SetWindowPos cascade (each composite visible, taskbar
        // peek-through between moves). Pattern mirrors SwapWindows L308-333.
        //
        // v3.22.22 hypothesis (post-v3.22.21 smoke): DeferWindowPos may not
        // be fully atomic across processes + monitors. AHK reference at
        // X:/_Projects/_.src/.oursrcarchive/eqswitch_ahk/EQSwitch.ahk:414-441
        // uses sequential WinMove and reportedly does NOT flicker (caveat:
        // the AHK build did NOT inject eqswitch-hook.dll, so its swap
        // didn't fight any in-process hook-driven SetWindowPos replays —
        // this is a co-factor when comparing AHK-vs-C#). A/B test
        // sequential vs batched in v3.22.22 with the per-stage timing
        // logs added below (T2-Opus round-5 catch — aggregate pass2 timing
        // couldn't distinguish atomic-batch from sequential-within-batch).
        //
        // pass2Stages captures monotonic ms-since-arrange-start at each
        // DeferWindowPos boundary. For an N-client swap: [tBeforeBegin,
        // tAfterDefer_0, tAfterDefer_1, ..., tAfterEnd]. v3.22.22 reads
        // this to determine whether windows ARE batched atomically or
        // sequentially-within-the-batch.
        var pass2Stages = new List<long>(targets.Count + 2);
        bool batchOk = false;
        pass2Stages.Add(swArrange.ElapsedMilliseconds); // tBeforeBegin
        var hdwp = _api.BeginDeferWindowPos(targets.Count);
        if (hdwp != IntPtr.Zero)
        {
            batchOk = true;
            foreach (var t in targets)
            {
                hdwp = _api.DeferWindowPos(hdwp, t.hwnd, IntPtr.Zero, t.x, t.y, t.w, t.h, t.flags);
                pass2Stages.Add(swArrange.ElapsedMilliseconds); // tAfterDefer_i
                if (hdwp == IntPtr.Zero)
                {
                    FileLogger.Warn("ArrangeMultiMonitor: DeferWindowPos failed mid-batch — falling back to sequential SetWindowPos");
                    batchOk = false;
                    break;
                }
            }
            if (batchOk)
            {
                _api.EndDeferWindowPos(hdwp);
                pass2Stages.Add(swArrange.ElapsedMilliseconds); // tAfterEnd
            }
        }
        else
        {
            FileLogger.Warn("ArrangeMultiMonitor: BeginDeferWindowPos failed — falling back to sequential SetWindowPos");
        }

        if (!batchOk)
        {
            foreach (var t in targets)
            {
                _api.SetWindowPos(t.hwnd, IntPtr.Zero, t.x, t.y, t.w, t.h, t.flags);
                pass2Stages.Add(swArrange.ElapsedMilliseconds); // sequential fallback timings
            }
        }
        long tPass2 = swArrange.ElapsedMilliseconds;

        foreach (var t in targets)
            FileLogger.Info($"ArrangeMultiMonitor: {t.logLabel}");

        string modeLabel = $" (primary={(primarySlim ? "slim" : "normal")}, secondary={(secondarySlim ? "slim" : "normal")})";
        string batchLabel = batchOk ? "atomic batch" : "sequential fallback";
        string lockSummary = lockToPrimaryDims ? ", lock-to-primary" : "";
        FileLogger.Info($"ArrangeMultiMonitor: {targets.Count}/{clients.Count} window(s), primary={primaryIdx} secondary={secondaryIdx}{modeLabel}, positioned via {batchLabel}{lockSummary} — pass1={tPass1}ms pass2={tPass2 - tPass1}ms");
        // Per-stage pass-2 timing (v3.22.22 diagnostic — round-5 T2-Opus catch):
        // For N clients: [tBeforeBegin, tAfterDefer_0, ..., tAfterDefer_{N-1}, tAfterEnd].
        // Deltas between adjacent values reveal whether the DeferWindowPos
        // batch is truly atomic (all timestamps cluster) or sequential-within-
        // the-batch (timestamps spread across the swap latency).
        FileLogger.Info($"ArrangeMultiMonitor: pass2-stages [{string.Join(",", pass2Stages)}]ms");
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
    public void ApplySlimTitlebarToAll(IReadOnlyList<EQClient> clients, IReadOnlySet<int>? injectedPids = null)
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

            // Skip position enforcement if hook DLL is active in this process —
            // the DLL handles it from inside, no need for external repositioning
            if (injectedPids != null && injectedPids.Contains(client.ProcessId))
                continue;

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

        // Authoritative: the name AutoLogin stamped at launch time (team1Account2,
        // etc). Short-circuits all downstream resolution because it's the only
        // source that knows the actual team slot that produced this client —
        // QuickLogin{N} is positional and mis-maps team-launched slots.
        string? boundName = null;
        if (!string.IsNullOrEmpty(client.BoundCharacterName))
        {
            charName = client.BoundCharacterName;
        }
        else
        {
            // Phase 5b: resolve {CHAR} through the v4 Characters list via the slot->name
            // binding carried in QuickLogin{N}. The QuickLogin{N} indirection itself is
            // Phase 6-deletion-slated; only the resolved-name data source moves to v4 here.
            //
            // QuickLogin{N} can hold either a Character.Name (enter-world bind) or an
            // Account.Name (charselect-only bind) — RefreshQuickLoginCombos builds the
            // list from both. Account-only binds intentionally fall through here: the
            // EQ native window title ("EverQuest - CharName" once logged in) is the
            // appropriate render for a slot the user did not bind to a specific
            // character. Phase 6 will rewire this whole indirection.
            boundName = slotIndex switch
            {
                0 => _config.QuickLogin1,
                1 => _config.QuickLogin2,
                2 => _config.QuickLogin3,
                3 => _config.QuickLogin4,
                _ => null
            };
            if (!string.IsNullOrEmpty(boundName))
            {
                var character = _config.FindCharacterByName(boundName);
                if (character != null && !string.IsNullOrEmpty(character.Name))
                    charName = character.Name;
                else
                {
                    // Bound name exists but didn't resolve to a Character — either an
                    // Account-only QuickLogin bind (intentional — see comment above) or
                    // a Character that was renamed/deleted after the bind was saved.
                    // Log once at Info so triage distinguishes "never bound" (boundName
                    // empty) from "bound-but-unresolved" (this branch) (review finding M3).
                    FileLogger.Info($"SetWindowTitle: slot {slotIndex} bound to '{boundName}' but not in Characters list — falling through to native EQ title");
                }
            }
        }

        // Fall back to EQ's native window title if no account match
        if (string.IsNullOrEmpty(charName))
        {
            int len = NativeMethods.GetWindowTextLength(client.WindowHandle);
            if (len > 0)
            {
                _titleSb ??= new System.Text.StringBuilder(256);
                _titleSb.EnsureCapacity(len + 1);
                _titleSb.Clear();
                NativeMethods.GetWindowText(client.WindowHandle, _titleSb, _titleSb.Capacity);
                var sb = _titleSb;
                var currentNative = sb.ToString();
                if (currentNative.StartsWith("EverQuest", StringComparison.Ordinal))
                    client.OriginalTitle = currentNative;
            }

            if (!string.IsNullOrEmpty(client.OriginalTitle) && client.OriginalTitle.Contains(" - "))
                charName = client.OriginalTitle.Split(" - ", 2)[1];
        }

        // Final fallback: if no Character match and EQ hasn't exposed an
        // in-world title yet, use the bound QuickLogin value itself. For
        // account-only binds (e.g. "backup") this renders the user's chosen
        // label instead of an empty {CHAR} slot (handoff 2026-04-24 Open #3).
        if (string.IsNullOrEmpty(charName) && !string.IsNullOrEmpty(boundName))
            charName = boundName;

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
        if (monitors.Count == 0)
        {
            FileLogger.Warn("GetTargetMonitor: no monitors detected, falling back to 1920x1080");
            return new WinRect { Right = 1920, Bottom = 1080 };
        }
        return monitors[targetIdx];
    }

    /// <summary>
    /// Get the full monitor bounds for the target monitor (including taskbar area).
    /// Used by TrayManager to write hook config with correct coordinates.
    /// </summary>
    public WinRect GetTargetMonitorBounds() => GetTargetMonitor(fullBounds: true);

    /// <summary>
    /// Get full monitor bounds for all monitors. Used by TrayManager to calculate
    /// per-process hook config positions in multimonitor mode.
    /// </summary>
    public IReadOnlyList<WinRect> GetAllMonitorFullBounds() => _api.GetAllMonitorBounds();

    /// <summary>
    /// v3.22.19: resolve the secondary monitor index for multi-monitor mode.
    /// Smart-pick logic for the auto case (configIdx == -1): walks all monitors
    /// in enumeration order and picks the first non-primary whose width meets
    /// <paramref name="minWidthPx"/>. This skips tiny / portrait monitors that
    /// the user wouldn't want EQ on (e.g. a 1280×1920 portrait at the top of
    /// the desktop layout would otherwise be picked by the legacy "first
    /// non-primary" heuristic). Falls back to legacy behavior if no monitor
    /// meets the threshold. If the user has explicitly configured a too-narrow
    /// secondary, falls through to auto-pick with a loud log so accidental
    /// misconfiguration self-heals.
    /// </summary>
    public static int ResolveSecondaryMonitorIdx(int configIdx, int primaryIdx, IReadOnlyList<WinRect> monitors, int minWidthPx = 1000)
    {
        if (monitors.Count == 0) return 0;
        // v3.22.19 round-2 verifier (T2 Opus): guard against an explicit user
        // choice (or fallback) that resolves to the SAME monitor as primary —
        // would stack both EQ clients on primary with the secondary's INI
        // bounds, defeating multi-monitor mode entirely. Silently coerce to
        // legacy fallback below if this happens.
        // "Suitable for EQ secondary" = wide enough AND landscape-oriented.
        // Skips both tiny monitors AND portrait/rotated monitors (which would
        // letterbox EQ to a sliver or render at the wrong aspect). Nate's
        // 1280×1920 portrait at index 2 is wider than 1000 but its 1.5 H/W
        // ratio makes it landscape-hostile for full-screen EQ.
        static bool IsSuitable(WinRect m, int minWidth)
        {
            int w = m.Width, h = m.Height;
            if (w < minWidth) return false;
            if (h <= 0) return false;
            // Reject portrait orientation (height > 1.3 × width)
            if ((double)h / w > 1.3) return false;
            return true;
        }
        // Explicit user choice — but only if the target is actually viable
        // AND distinct from primary (v3.22.19 round-2 secondaryIdx==primaryIdx guard).
        if (configIdx >= 0 && configIdx < monitors.Count)
        {
            if (configIdx == primaryIdx)
            {
                FileLogger.Warn($"ResolveSecondaryMonitorIdx: configured SecondaryMonitor={configIdx} equals primary — falling back to smart auto-pick to avoid stacking both clients on one monitor");
            }
            else if (IsSuitable(monitors[configIdx], minWidthPx))
            {
                return configIdx;
            }
            else
            {
                FileLogger.Warn($"ResolveSecondaryMonitorIdx: configured SecondaryMonitor={configIdx} is not suitable ({monitors[configIdx].Width}x{monitors[configIdx].Height} — needs width≥{minWidthPx}px and landscape orientation) — falling back to smart auto-pick");
            }
        }
        // Auto-pick: first non-primary monitor that's wide enough and landscape
        for (int i = 0; i < monitors.Count; i++)
        {
            if (i == primaryIdx) continue;
            if (IsSuitable(monitors[i], minWidthPx))
            {
                FileLogger.Info($"ResolveSecondaryMonitorIdx: auto-picked monitor {i} ({monitors[i].Width}x{monitors[i].Height}) — first suitable non-primary");
                return i;
            }
        }
        // Last resort: legacy fallback (first non-primary, regardless of suitability)
        int fallback = primaryIdx == 0 && monitors.Count > 1 ? 1 : 0;
        FileLogger.Warn($"ResolveSecondaryMonitorIdx: no suitable secondary found (all candidates too narrow or portrait); using legacy fallback index {fallback}");
        return fallback;
    }

    /// <summary>
    /// v3.22.19: get work-area bounds (excludes taskbar) for all monitors. Used by
    /// TrayManager to compute per-PID hook config when a secondary monitor's client
    /// is in non-slim mode (taskbar should remain visible). Index order matches
    /// <see cref="GetAllMonitorFullBounds"/>.
    /// </summary>
    public IReadOnlyList<WinRect> GetAllMonitorWorkAreas() => _api.GetAllMonitorWorkAreas();
}
