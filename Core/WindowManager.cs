// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// v3.22.44 r3 — result of WindowManager.SwapWindows. Pre-r3 returned void,
/// so the tray-action / hotkey caller had no way to surface a balloon when
/// the swap was aborted. Now callers branch on the result to show "Swap
/// skipped — N clients minimized — restore manually first" or equivalent.
/// </summary>
public enum SwapResult
{
    Swapped,
    TooFew,                 // clients.Count < 2 — no-op, no balloon needed
    AbortedIconic,          // at least one client is iconic — user needs to restore + retry
    AbortedNotResponsive,   // hung / non-responsive / window-gone — no balloon needed
}

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
    /// and restores it if minimized.
    /// <para>
    /// v3.22.44 Gate #3 — hardened with the same four-gate check the
    /// <c>CanForegroundCandidate</c> helper in <see cref="UI.TrayManager"/>
    /// already uses for the auto-recovery path. Pre-this version, the cycle
    /// hotkeys (Alt+`, Alt+], etc.) called this with only
    /// <c>IsWindow</c>+<c>IsHungAppWindow</c> coverage, then naively fired
    /// <c>SW_RESTORE</c>+<c>ForceForegroundWindow</c>. If the target was
    /// mid-zone-load (DirectX device reset in progress because a sibling
    /// process exit just flushed the graphics driver), the unguarded
    /// <c>SW_RESTORE</c> collided with EQ's device-lost recovery handler
    /// and crashed the surviving client. <c>IsHungAppWindow</c> alone has a
    /// 5-second kernel-threshold latency before it returns true — too slow
    /// to catch a 100–500 ms transient. The
    /// <c>IsClientResponsive</c> <c>SendMessageTimeout(WM_NULL, 100ms,
    /// SMTO_ABORTIFHUNG|SMTO_BLOCK)</c> probe fast-fails inside that window.
    /// </para>
    /// <para>
    /// <paramref name="isLoginActive"/> is an optional predicate the caller
    /// supplies if they have access to <c>AutoLoginManager.IsLoginActive</c>.
    /// When non-null and returns true for the client's PID,
    /// <c>SwitchToClient</c> short-circuits — taking foreground during
    /// DirectInput credential injection disrupts the SHM-driven typing
    /// sequence. Callers that already pre-filtered (e.g.
    /// <c>RaiseClientsAboveTaskbar</c>'s post-dance foreground transfer)
    /// can pass null.
    /// </para>
    /// </summary>
    public bool SwitchToClient(EQClient client, Func<int, bool>? isLoginActive = null)
    {
        var hwnd = client.WindowHandle;

        // Gate 1: window-validity. Cheap, no IPC. Eliminates the "EQ
        // recreated its HWND during gameplay and ProcessManager hasn't
        // refreshed yet" case.
        if (!_api.IsWindow(hwnd)) return false;

        // Gate 2: kernel-level hung detection. 5s threshold but cheap.
        // Filters genuinely-frozen clients up front.
        if (_api.IsHungAppWindow(hwnd))
        {
            FileLogger.Info($"SwitchToClient: skipping hung window {client}");
            return false;
        }

        // Gate 3: optional autologin filter. Caller passes null when they
        // don't have access to the predicate or already filtered.
        if (isLoginActive != null && isLoginActive(client.ProcessId))
        {
            FileLogger.Info($"SwitchToClient: skipping {client} — autologin in progress (taking foreground would disrupt DirectInput credential typing)");
            return false;
        }

        // Gate 4: 100 ms pump-responsiveness probe. Catches transient
        // mid-zone-load DX-reset blocks that IsHungAppWindow's 5s
        // threshold lets through. Matches the convention used by every
        // other cross-process SetWindowPos site in the codebase
        // (WindowManager.ArrangeSingleScreen/MultiMonitor, SwapWindows,
        // ApplySlimTitlebar, ResizeToCurrentMonitors).
        if (!_api.IsClientResponsive(hwnd, out int lastErr))
        {
            FileLogger.Warn($"SwitchToClient: skipping non-responsive window {client} (SendMessageTimeout WM_NULL > 100ms — likely mid-zone-load DX reset; SW_RESTORE would race the device-lost recovery; lastErr={lastErr})");
            return false;
        }

        try
        {
            // SW_RESTORE is safe now: we've already verified the pump is
            // alive within the last ~100 ms. The window is either non-iconic
            // (no-op) or iconic and ready to accept the state transition.
            _api.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            _api.ForceForegroundWindow(hwnd);
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
    /// Returns the client that was switched to, or null if all N-1 candidates failed.
    /// <para>
    /// v3.22.44 r2 (T4-Sonnet+Opus Item 4 MEDIUM convergent): ring-walk
    /// fallback. Round 1 returned null on the FIRST SwitchToClient failure,
    /// leaving the user stuck on the current client when one sibling was
    /// mid-zone-load. If clients = [A, B, C] and user is on A, pressing
    /// cycle while B is mid-zone-load returned null forever (B's
    /// IsClientResponsive probe always fails during zone-load, B is always
    /// the next-from-A in the ring). Now we walk up to N-1 candidates before
    /// giving up — A→B fails → try C → succeeds → user lands on C, skipping
    /// the loading client. Bounded loop (clients.Count iterations max) so
    /// no infinite spin.
    /// </para>
    /// </summary>
    public EQClient? CycleNext(IReadOnlyList<EQClient> clients, EQClient? current, Func<int, bool>? isLoginActive = null)
    {
        if (clients.Count == 0) return null;

        int currentIndex = -1;
        if (current != null)
        {
            for (int j = 0; j < clients.Count; j++)
                if (clients[j] == current) { currentIndex = j; break; }
        }

        // v3.22.44 r3 (T3-Opus F3 MEDIUM): special-case clients.Count == 1 with
        // no current selection. Round-1 behavior tried clients[0] in that case;
        // round-2's ring-walk loop `for (offset = 1; offset < 1; ...)` never
        // executes, so the function returned null and the cycle hotkey silently
        // did nothing. Restore the round-1 single-client cold-start by trying
        // clients[0] explicitly when current is null.
        if (clients.Count == 1)
        {
            if (current != null) return null;  // nothing to cycle to
            return SwitchToClient(clients[0], isLoginActive) ? clients[0] : null;
        }

        // Try up to N-1 candidates starting from (currentIndex + 1). Bounded
        // by clients.Count, so worst-case linear in client count (~1-6).
        for (int offset = 1; offset < clients.Count; offset++)
        {
            int candidateIndex = (currentIndex + offset) % clients.Count;
            var candidate = clients[candidateIndex];
            if (SwitchToClient(candidate, isLoginActive)) return candidate;
        }

        FileLogger.Info($"CycleNext: all {clients.Count - 1} candidates failed (likely zone-load/login/iconic); user stays on current");
        return null;
    }

    /// <summary>
    /// Cycle to the previous EQ client in the list.
    /// v3.22.44 r2: ring-walk fallback — see CycleNext for rationale.
    /// </summary>
    public EQClient? CyclePrev(IReadOnlyList<EQClient> clients, EQClient? current, Func<int, bool>? isLoginActive = null)
    {
        if (clients.Count == 0) return null;

        int currentIndex = 0;
        if (current != null)
        {
            for (int j = 0; j < clients.Count; j++)
                if (clients[j] == current) { currentIndex = j; break; }
        }

        // v3.22.44 r3 (T3-Opus F3 MEDIUM): match CycleNext's single-client
        // special case.
        if (clients.Count == 1)
        {
            if (current != null) return null;
            return SwitchToClient(clients[0], isLoginActive) ? clients[0] : null;
        }

        for (int offset = 1; offset < clients.Count; offset++)
        {
            // Walk backwards: (currentIndex - offset) mod N. Add N once to
            // keep the dividend non-negative; offset is bounded to clients.Count-1
            // so a single addition suffices.
            int candidateIndex = (currentIndex - offset + clients.Count) % clients.Count;
            var candidate = clients[candidateIndex];
            if (SwitchToClient(candidate, isLoginActive)) return candidate;
        }

        FileLogger.Info($"CyclePrev: all {clients.Count - 1} candidates failed (likely zone-load/login/iconic); user stays on current");
        return null;
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
    /// <summary>
    /// v3.22.44 r3 (T2-Sonnet Gap G HIGH / T2-Opus Finding 1 / T4-Opus F1 4-way convergent):
    /// returns per-reason skip counts so callers (OnArrangeWindows balloon)
    /// can surface "Fixed N of M (K minimized — restore manually)" instead
    /// of the round-2 silent omission.
    /// <para>
    /// v3.22.44 r3.5 (R3-T3-Opus F1 HIGH + R3-T3-Sonnet C2 MEDIUM convergent):
    /// changed return from `int skippedIconic` to a tuple `(int Iconic, int Other)`.
    /// Round-3 captured only iconic skips; ArrangeSingleScreen/ArrangeMultiMonitor
    /// also silently skip non-iconic clients (IsWindow=false, IsHungAppWindow,
    /// IsClientResponsive=false) which weren't counted, so the balloon's
    /// `arranged = clientsToArrange.Count - skippedIconic` arithmetic
    /// over-claimed "Fixed N" by the number of silently-skipped non-iconic
    /// clients. Same class of bug round-3 set out to fix, just in a different
    /// shape. Tuple return lets the caller distinguish "iconic — user can
    /// restore" from "other — transient, no user action".
    /// </para>
    /// </summary>
    public (int Iconic, int Other) ArrangeWindows(IReadOnlyList<EQClient> clients, IReadOnlyDictionary<int, int>? monitorSlotByPid = null)
    {
        if (clients.Count == 0) return (0, 0);

        if (_config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase))
            return ArrangeMultiMonitor(clients, monitorSlotByPid);
        else
            return ArrangeSingleScreen(clients);
    }

    /// <summary>
    /// Single-screen mode: arrange all windows on the target monitor.
    /// In 1x1 (stacked) mode, windows keep their own size from eqclient.ini.
    /// Slim Titlebar mode pushes the titlebar off-screen (WinEQ2 method).
    /// </summary>
    private (int Iconic, int Other) ArrangeSingleScreen(IReadOnlyList<EQClient> clients)
    {
        bool slimTitlebar = _config.Layout.SlimTitlebar;
        var monitor = GetTargetMonitor(slimTitlebar);
        var layout = _config.Layout;
        int skippedIconic = 0;
        int skippedOther = 0;  // v3.22.44 r3.5 — IsWindow=false / IsHungAppWindow / IsClientResponsive=false

        FileLogger.Info($"ArrangeSingleScreen: monitor bounds L={monitor.Left} T={monitor.Top} R={monitor.Right} B={monitor.Bottom} ({monitor.Width}x{monitor.Height})");

        for (int i = 0; i < clients.Count; i++)
        {
            var client = clients[i];
            // v3.22.44 r3.5: count non-iconic skips so the balloon math
            // doesn't over-claim "Fixed N" by silent skips.
            if (!_api.IsWindow(client.WindowHandle)) { skippedOther++; continue; }
            if (_api.IsHungAppWindow(client.WindowHandle))
            {
                FileLogger.Info($"ArrangeWindows: skipping hung window {client}");
                skippedOther++;
                continue;
            }
            // v3.22.22 round-5 (R4 T2 verifier CRITICAL): same pump-responsiveness
            // probe used by ArrangeMultiMonitor. ArrangeSingleScreen hits the same
            // cross-process SetWindowLongPtr stall when LoginComplete fires with
            // a client mid-zone-load (DX device reset blocks pump). Without the
            // probe, single-screen mode would crash the same way as the
            // 2026-05-20 PID 24672 multi-monitor incident.
            if (!_api.IsClientResponsive(client.WindowHandle, out int lastErr))
            {
                FileLogger.Warn($"ArrangeSingleScreen: skipping non-responsive window {client} (SendMessageTimeout WM_NULL > 100ms — likely mid-zone-load DX reset or transient pump block; lastErr={lastErr})");
                skippedOther++;
                continue;
            }

            // v3.22.44 r2 (T2-Opus HIGH Item B): skip iconic clients. Cross-
            // process ShowWindow(SW_RESTORE) + SetWindowPos(SWP_FRAMECHANGED)
            // on a minimized EQ window races EQ's D3D9 device-lost recovery
            // (Dalaya releases the device on minimize). Same crash class as
            // Gate #2's RaiseClientsAboveTaskbar fix — extended here because
            // ArrangeSingleScreen fires from ApplyDeferredCosmetics, sibling-
            // close recovery, ClientDiscovered, ReloadConfig, and the
            // user-initiated Fix Windows hotkey. The user can manually restore
            // an iconic client (taskbar click); EQ's own restore-path
            // SetWindowPos is intercepted by the in-process hook DLL which
            // enforces slim-titlebar bounds. So slim-titlebar is preserved
            // on the next manual restore.
            if (_api.IsIconic(client.WindowHandle))
            {
                FileLogger.Info($"ArrangeSingleScreen: skipping iconic {client} (v3.22.44 r2: don't cross-process SW_RESTORE iconic clients)");
                skippedIconic++;
                continue;
            }

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
        FileLogger.Info($"ArrangeSingleScreen: {clients.Count} window(s) in {mode}, skippedIconic={skippedIconic} skippedOther={skippedOther}");
        return (skippedIconic, skippedOther);
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
    private (int Iconic, int Other) ArrangeMultiMonitor(IReadOnlyList<EQClient> clients, IReadOnlyDictionary<int, int>? monitorSlotByPid = null)
    {
        int skippedIconic = 0;
        int skippedOther = 0;  // v3.22.44 r3.5 — non-iconic silent skips
        // v3.22.19: per-monitor slim override. Primary uses SlimTitlebar,
        // secondary uses SlimTitlebarSecondary. Need BOTH full-bounds and
        // work-area lists so each client can pick the right one without a
        // second EnumDisplayMonitors round-trip.
        bool primarySlim = _config.Layout.SlimTitlebar;
        bool secondarySlim = _config.Layout.SlimTitlebarSecondary;
        var fullBounds = _api.GetAllMonitorBounds();
        var workAreas = _api.GetAllMonitorWorkAreas();
        if (fullBounds.Count == 0 || workAreas.Count == 0) return (skippedIconic, skippedOther);
        // Defensive: both enumerations should return the same count in the
        // same order (both walk EnumDisplayMonitors). If they ever diverge,
        // log loud and bail rather than picking wrong-monitor bounds.
        if (fullBounds.Count != workAreas.Count)
        {
            FileLogger.Error($"ArrangeMultiMonitor: monitor enumeration count mismatch — fullBounds={fullBounds.Count} workAreas={workAreas.Count}, aborting arrange");
            return (skippedIconic, skippedOther);
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
            // v3.22.44 r3.5: count non-iconic silent skips for accurate balloon math.
            if (!_api.IsWindow(client.WindowHandle)) { skippedOther++; continue; }
            if (_api.IsHungAppWindow(client.WindowHandle))
            {
                FileLogger.Info($"ArrangeMultiMonitor: skipping hung window {client}");
                skippedOther++;
                continue;
            }
            // v3.22.22 round-4 / round-5 (R4 T3-Opus MEDIUM): use shared
            // IsClientResponsive helper. Tighter than IsHungAppWindow's 5s
            // kernel threshold — catches transient mid-zone-load pump blocks
            // at 100ms. Round-5 adds SMTO_BLOCK to the probe to prevent
            // reentrant arrange dispatch during the probe wait.
            if (!_api.IsClientResponsive(client.WindowHandle, out int lastErr))
            {
                FileLogger.Warn($"ArrangeMultiMonitor: skipping non-responsive window {client} (SendMessageTimeout WM_NULL > 100ms — likely mid-zone-load DX reset or transient pump block; pre-empts the v3.22.21 14.5s pass-1 block that crashed PID 24672; lastErr={lastErr})");
                skippedOther++;
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

            // v3.22.44 r2 (T2-Opus HIGH Item B): skip iconic clients — same
            // rationale as ArrangeSingleScreen above. ApplyDeferredCosmetics
            // (LoginCredentialsSent / LoginComplete) is the highest-risk
            // caller: client B finishes autologin while A is minimized in
            // background, and the cross-process restore on A is exactly the
            // crash class users reported.
            if (_api.IsIconic(client.WindowHandle))
            {
                FileLogger.Info($"ArrangeMultiMonitor: skipping iconic {client} (v3.22.44 r2: don't cross-process SW_RESTORE iconic clients)");
                skippedIconic++;
                continue;
            }

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
            FileLogger.Info($"ArrangeMultiMonitor: no eligible clients to arrange (all hung/invalid/iconic), skippedIconic={skippedIconic} skippedOther={skippedOther}");
            return (skippedIconic, skippedOther);
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
        FileLogger.Info($"ArrangeMultiMonitor: pass2-stages [{string.Join(",", pass2Stages)}]ms, skippedIconic={skippedIconic} skippedOther={skippedOther}");
        return (skippedIconic, skippedOther);
    }

    /// <summary>
    /// Rotate window positions: each window moves to the next window's position.
    /// Window 1→2, 2→3, ..., N→1. Replicates AHK SwapWindows.
    /// <para>
    /// v3.22.44 r3 (T4-Opus F2 / T2-Opus Finding 2 / T4-Sonnet Item 2 3-way
    /// convergent HIGH): returns a status enum so callers can surface a
    /// balloon when the swap was aborted on iconic clients. Pre-r3 returned
    /// void with only `FileLogger.Info` — user pressed Alt+\` with one
    /// minimized client and nothing visibly happened.
    /// </para>
    /// </summary>
    public SwapResult SwapWindows(IReadOnlyList<EQClient> clients)
    {
        if (clients.Count < 2) return SwapResult.TooFew;

        // Check for hung windows — abort if any are unresponsive.
        // v3.22.22 round-5 (R4 T2 verifier CRITICAL): IsHungAppWindow alone
        // has a 5s kernel-threshold latency. SwapWindows runs on user
        // SwitchKey hotkey — if a teammate is mid-zone-load (DX device reset,
        // pump blocked), GetWindowRect/BeginDeferWindowPos below would stall
        // for 14.5s and crash EQ (same class as the 2026-05-20 PID 24672
        // ArrangeMultiMonitor incident). The shared IsClientResponsive probe
        // (100ms SendMessageTimeout with SMTO_ABORTIFHUNG | SMTO_BLOCK) fast-
        // fails inside the kernel's hung-threshold window.
        foreach (var client in clients)
        {
            if (_api.IsHungAppWindow(client.WindowHandle))
            {
                FileLogger.Info($"SwapWindows: aborting — hung window {client}");
                return SwapResult.AbortedNotResponsive;
            }
            if (!_api.IsClientResponsive(client.WindowHandle, out int lastErr))
            {
                FileLogger.Warn($"SwapWindows: aborting — non-responsive window {client} (SendMessageTimeout WM_NULL > 100ms — likely mid-zone-load DX reset; lastErr={lastErr})");
                return SwapResult.AbortedNotResponsive;
            }
        }

        // Capture current positions
        var positions = new List<WinRect>();
        foreach (var client in clients)
        {
            if (!_api.IsWindow(client.WindowHandle))
            {
                FileLogger.Info($"SwapWindows: window gone for {client}");
                return SwapResult.AbortedNotResponsive;
            }
            _api.GetWindowRect(client.WindowHandle, out var rect);
            positions.Add(rect);
        }

        // v3.22.44 r2 (T2-Opus HIGH Item B): if any client is iconic, abort
        // the swap entirely. Same SW_RESTORE-on-iconic D3D9 race as the
        // Arrange paths. User can manually restore the iconic client(s) then
        // re-press the swap hotkey. Note this is stricter than the earlier
        // partial-skip approach because SwapWindows captures ALL client
        // positions and rotates — skipping individual iconic clients would
        // produce an asymmetric rotation, so we abort cleanly.
        // v3.22.44 r3: count iconic clients so the SwapResult carries enough
        // info for the caller to render a meaningful balloon.
        int iconicCount = 0;
        foreach (var client in clients)
        {
            if (_api.IsIconic(client.WindowHandle))
            {
                FileLogger.Info($"SwapWindows: aborting — iconic {client} (v3.22.44 r2: restore manually then re-press swap)");
                iconicCount++;
            }
        }
        if (iconicCount > 0) return SwapResult.AbortedIconic;

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
        return SwapResult.Swapped;

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
        return SwapResult.Swapped;
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

            // v3.22.44 r2 (T2-Sonnet B1 MEDIUM): IsClientResponsive probe FIRST,
            // THEN any SW_RESTORE. Round-1 ordering had SW_RESTORE before the
            // probe — if the client was iconic AND mid-zone-load, the
            // unconditional cross-process SW_RESTORE fired before we knew the
            // client was non-responsive, racing the device-lost recovery.
            // ArrangeSingleScreen and ArrangeMultiMonitor already use this
            // order (probe-then-conditional); ResizeToCurrentMonitors is
            // brought into alignment here.
            //
            // v3.22.44 r2 (T2-Opus HIGH Item B): additionally, skip iconic
            // clients entirely — same rationale as the Arrange paths.
            if (!_api.IsClientResponsive(client.WindowHandle, out int lastErr))
            {
                FileLogger.Warn($"ResizeToCurrentMonitors: skipping non-responsive window {client} (SendMessageTimeout WM_NULL > 100ms — likely mid-zone-load DX reset; SetWindowPos would stall; lastErr={lastErr})");
                continue;
            }
            if (_api.IsIconic(client.WindowHandle))
            {
                FileLogger.Info($"ResizeToCurrentMonitors: skipping iconic {client} (v3.22.44 r2: don't cross-process SW_RESTORE iconic clients)");
                continue;
            }

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
        // v3.22.22 round-6 (R5 T2 Sonnet+Opus CRITICAL convergence): leaf-level
        // pump-responsiveness probe so EVERY caller of this method benefits —
        // direct calls (TrayManager.cs:404 ClientDiscovered single-screen,
        // TrayManager.cs:209 ApplyDeferredCosmetics BURST 1 single-screen),
        // ApplySlimTitlebarToAll guard-timer loop (fires every 500ms-5s),
        // ArrangeSingleScreen, and indirectly ArrangeMultiMonitor's own
        // per-client pass-1 work. SetWindowLongPtr below is the exact cross-
        // process primitive that stalled 14.5s in the 2026-05-20 PID 24672
        // smoke crash. Skipping a non-responsive HWND here means a brief miss
        // of the slim-titlebar style — the slim-titlebar guard timer
        // (TrayManager._slimTitlebarGuard) re-fires every 500ms and will
        // re-apply once the pump recovers.
        if (!_api.IsClientResponsive(hwnd, out int lastErr))
        {
            FileLogger.Warn($"ApplySlimTitlebar: skipping non-responsive window (hwnd=0x{hwnd.ToInt64():X} SendMessageTimeout WM_NULL > 100ms — likely mid-zone-load DX reset; guard timer will retry; lastErr={lastErr})");
            return;
        }

        // v3.22.44 r2 (T4-Opus Item 2 sub-finding, T2-Sonnet B2): leaf-level
        // IsIconic skip. ApplySlimTitlebar is called from many sites including
        // the slim-titlebar guard timer (500ms tick) on non-injected clients.
        // SetWindowLongPtr(GWL_STYLE) + SetWindowPos(SWP_FRAMECHANGED) on a
        // minimized EQ window with a released D3D9 device is the same crash
        // class as the Arrange paths. The hook DLL inside eqgame intercepts
        // EQ's own restore-path SetWindowPos and enforces slim bounds on
        // manual restore, so iconic clients still end up correctly slimmed
        // when the user brings them back into view.
        //
        // v3.22.44 r3 (T2-Opus LOW Finding 3): route through _api.IsIconic
        // instead of NativeMethods.IsIconic directly — round-2 broke the
        // IWindowsApi abstraction here. Every other iconic skip in r2 went
        // through _api, this one bypassed unit-test mockability.
        if (_api.IsIconic(hwnd))
        {
            FileLogger.Info($"ApplySlimTitlebar: skipping iconic window (hwnd=0x{hwnd.ToInt64():X}; v3.22.44 r2)");
            return;
        }

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
                // v3.22.29 Orphan-1: snapshot under ConfigMutationLock. Fires
                // from WinForms timer (UI thread) but ReloadConfig swap of
                // _config.Characters can still race.
                Character? character;
                lock (ConfigManager.ConfigMutationLock)
                {
                    character = _config.FindCharacterByName(boundName);
                }
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
