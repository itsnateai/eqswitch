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
    // v3.22.88 — measured-frame cache. null = no cache → the prediction path runs
    // (today's behavior). Production (TrayManager) injects a real one; the 2-arg
    // test ctor leaves it null so existing geometry tests stay on the prediction path.
    private readonly FrameCache? _frameCache;
    [ThreadStatic] private static System.Text.StringBuilder? _titleSb;

    public WindowManager(AppConfig config, IWindowsApi? api = null, FrameCache? frameCache = null)
    {
        _config = config;
        _api = api ?? new WindowsApi();
        _frameCache = frameCache;
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
    // v3.24.1 taskbar-flicker fix — coverPrimaryFirst (default false) is honored ONLY
    // on the multimonitor swap path; it flows to ArrangeMultiMonitor's incoming-first
    // HWND_TOP plant. Default false keeps every other caller (Fix-Windows, deferred
    // cosmetics, toggles, settings-apply) byte-for-byte unchanged.
    public (int Iconic, int Other) ArrangeWindows(IReadOnlyList<EQClient> clients, IReadOnlyDictionary<int, int>? monitorSlotByPid = null, bool coverPrimaryFirst = false)
    {
        if (clients.Count == 0) return (0, 0);

        if (_config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase))
            return ArrangeMultiMonitor(clients, monitorSlotByPid, coverPrimaryFirst);
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
    /// v3.24.2 — THE single source of truth for the lock-to-primary-dims POLICY DECISION.
    /// Both <see cref="ArrangeMultiMonitor"/> (which sizes the window) AND
    /// <see cref="UI.TrayManager"/>'s hook-config path (which pins the in-process hook rect)
    /// call this, so the two can no longer disagree on the secondary client's size. A
    /// disagreement WAS the v3.24.1 swap refit: arrange sized the secondary to the primary's
    /// dims (lock-to-primary), but the hook config sized it to the secondary monitor's NATIVE
    /// dims; the hook won and yanked the window on every swap → a DX backbuffer rebuild =
    /// taskbar peek + black-bar/smoosh. Active ⇒ both windows take the primary's dims so the
    /// DX swap-chain stays one constant size across swaps. Gated on: 2+ monitors, both the
    /// same slim flag, ≤200px delta on each axis, and primary fits within secondary.
    /// </summary>
    public static bool ShouldLockToPrimaryDims(WinRect primary, WinRect secondary, bool primarySlim, bool secondarySlim)
    {
        bool bothSameSlim = primarySlim == secondarySlim;
        int wDelta = Math.Abs(primary.Width - secondary.Width);
        int hDelta = Math.Abs(primary.Height - secondary.Height);
        bool primaryFits = primary.Width <= secondary.Width && primary.Height <= secondary.Height;
        return bothSameSlim && wDelta <= 200 && hDelta <= 200 && primaryFits;
    }

    /// <summary>
    /// v3.24.2 — under lock-to-primary-dims, a SECONDARY-slot client takes its OWN monitor's
    /// origin but the PRIMARY monitor's dimensions. (The primary slot is unchanged — it IS the
    /// source of dims.) Single source of truth shared by the arrange + hook-config paths.
    /// </summary>
    public static WinRect ApplyLockToPrimaryDims(WinRect secondaryMonitor, WinRect primary) => new WinRect
    {
        Left = secondaryMonitor.Left,
        Top = secondaryMonitor.Top,
        Right = secondaryMonitor.Left + primary.Width,
        Bottom = secondaryMonitor.Top + primary.Height,
    };

    /// <summary>
    /// v3.24.3 — THE single multimonitor per-slot sizing authority. Returns the effective
    /// target monitor-rect (origin + W×H) for <paramref name="slot"/> (0 = primary,
    /// 1 = secondary), applying <paramref name="mode"/>. EVERY place that needs a
    /// multimonitor window/backbuffer size derives it from here — <see cref="ArrangeMultiMonitor"/>
    /// (sizes the window), <c>TrayManager.GetEffectiveMonitorForPid</c> (pins the in-process
    /// hook rect AND drives the Windowed read-back), and <c>EQClientSettingsForm.EnforceOverrides</c>
    /// (writes the eqclient.ini backbuffer) — so they can NEVER disagree. A disagreement WAS
    /// the v3.24.1 swap refit (arrange said one size, the hook another → the hook yanked the
    /// window on every swap → DX backbuffer rebuild = taskbar peek + smoosh) AND the v3.24.2
    /// Windowed-vs-Fullscreen band inconsistency (the read-back used the secondary's NATIVE
    /// bounds, growing it past the taskbar in Windowed only).
    /// <para>
    /// Symmetric by construction: when the two monitors are close enough to lock, BOTH slots
    /// receive the SAME W×H (only the origin differs), so a swap never resizes a client and
    /// the DX swap-chain stays one constant size. <paramref name="mode"/> chooses which rect
    /// set is locked: <b>CoverAll</b> → the primary's FULL bounds (primary covers its taskbar;
    /// a taller secondary's leftover band shows that monitor's taskbar); <b>ShowTaskbars</b> →
    /// the primary's WORK area (every monitor leaves taskbar room). When the monitors differ
    /// too much to lock (4K + 1080p), <paramref name="locked"/> is false and each slot keeps
    /// its own native bounds (per-monitor-fit; the user presses Fix-Windows to re-init DX).
    /// </para>
    /// </summary>
    /// <param name="slot">Monitor slot — even (0, 2, …) = primary, odd = secondary.</param>
    /// <param name="primaryFull">Primary monitor full bounds (rcMonitor).</param>
    /// <param name="primaryWork">Primary monitor work area (rcWork — excludes taskbar).</param>
    /// <param name="secondaryFull">Secondary full bounds, or null for a single physical monitor.</param>
    /// <param name="secondaryWork">Secondary work area, or null for a single physical monitor.</param>
    public static (WinRect bounds, bool locked) EffectiveSlotBounds(
        int slot,
        WinRect primaryFull, WinRect primaryWork,
        WinRect? secondaryFull, WinRect? secondaryWork,
        Config.MultiMonTaskbarMode mode)
    {
        // CoverAll operates on full monitor bounds; ShowTaskbars on work areas. The
        // primary slot is always the source of dims, placed at its own monitor top-left.
        bool showTaskbars = mode == Config.MultiMonTaskbarMode.ShowTaskbars;
        WinRect primary = showTaskbars ? primaryWork : primaryFull;

        if ((slot % 2) == 0 || secondaryFull is null || secondaryWork is null)
            return (primary, false);

        WinRect secondary = showTaskbars ? secondaryWork.Value : secondaryFull.Value;
        // Lock the secondary to the primary's dims (its own origin) when the monitors are
        // close enough; otherwise hand back the secondary's native bounds (degrade). The
        // bothSameSlim gate is structurally satisfied in multimon (both slots are slim-
        // managed; the taskbar MODE — not the per-monitor slim flag — now controls
        // visibility), so pass true/true.
        return ShouldLockToPrimaryDims(primary, secondary, true, true)
            ? (ApplyLockToPrimaryDims(secondary, primary), true)
            : (secondary, false);
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
    private (int Iconic, int Other) ArrangeMultiMonitor(IReadOnlyList<EQClient> clients, IReadOnlyDictionary<int, int>? monitorSlotByPid = null, bool coverPrimaryFirst = false)
    {
        int skippedIconic = 0;
        int skippedOther = 0;  // v3.22.44 r3.5 — non-iconic silent skips
        // v3.24.3 — multimon is ALWAYS slim-managed (both slots); the taskbar-visibility
        // MODE (MultiMonTaskbarMode), NOT the per-monitor slim flag, now governs whether the
        // windows cover their taskbars. SlimTitlebar/SlimTitlebarSecondary stay true
        // (AppConfig.Validate pins them) so the legacy non-slim-secondary swap-smoosh trap
        // can't recur — a SlimTitlebarSecondary=false config is migrated to ShowTaskbars.
        // Need BOTH full-bounds and work-area lists so EffectiveSlotBounds can pick per mode.
        var taskbarMode = _config.Layout.MultiMonTaskbarMode;
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

        // Resolve the two physical slots. Secondary resolution skips tiny / portrait
        // monitors (ResolveSecondaryMonitorIdx, default min width 1000px).
        var primaryIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, fullBounds.Count - 1);
        int secondaryIdx = ResolveSecondaryMonitorIdx(_config.Layout.SecondaryMonitor, primaryIdx, fullBounds);
        bool hasSecondary = fullBounds.Count > 1;
        int slotCount = hasSecondary ? 2 : 1;

        // The per-slot native monitor rects (full + work) fed to the single sizing
        // authority. EffectiveSlotBounds (shared with the hook-config + read-back +
        // backbuffer paths) decides full-vs-work by mode and lock-vs-native by fit.
        WinRect primaryFullMon = fullBounds[primaryIdx];
        WinRect primaryWorkMon = workAreas[primaryIdx];
        WinRect? secondaryFullMon = hasSecondary ? fullBounds[secondaryIdx] : (WinRect?)null;
        WinRect? secondaryWorkMon = hasSecondary ? workAreas[secondaryIdx] : (WinRect?)null;

        // Summary lock decision (for the log only) — the per-client effective bounds come
        // straight from EffectiveSlotBounds in the build loop below, so this can never
        // drift from what's actually applied.
        bool lockToPrimaryDims = false;
        if (hasSecondary)
        {
            (_, lockToPrimaryDims) = EffectiveSlotBounds(1, primaryFullMon, primaryWorkMon, secondaryFullMon, secondaryWorkMon, taskbarMode);
            bool showTaskbars = taskbarMode == Config.MultiMonTaskbarMode.ShowTaskbars;
            var primForLog = showTaskbars ? primaryWorkMon : primaryFullMon;
            var secForLog  = showTaskbars ? secondaryWorkMon!.Value : secondaryFullMon!.Value;
            if (lockToPrimaryDims)
            {
                int hBand = secForLog.Height - primForLog.Height;
                int wBand = secForLog.Width - primForLog.Width;
                FileLogger.Info($"ArrangeMultiMonitor: lock-to-primary-dims ACTIVE (taskbarMode={taskbarMode}) — both windows {primForLog.Width}x{primForLog.Height}; secondary monitor has {wBand}px horizontal + {hBand}px vertical empty band (eliminates cross-monitor smoosh on SwitchKey swap)");
            }
            else
            {
                int wDelta = Math.Abs(primForLog.Width - secForLog.Width);
                int hDelta = Math.Abs(primForLog.Height - secForLog.Height);
                FileLogger.Warn($"ArrangeMultiMonitor: lock-to-primary-dims OFF (taskbarMode={taskbarMode}) — primary={primForLog.Width}x{primForLog.Height} secondary={secForLog.Width}x{secForLog.Height} wDelta={wDelta} hDelta={hDelta}. Using per-monitor-fit; cross-monitor SwitchKey swap may smoosh — press Fix Windows to force DX reinit");
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
        // v3.24.1 taskbar-flicker fix — the primary-bound (slot-0) client's target,
        // captured during this build loop so it can be planted at HWND_TOP over primary
        // BEFORE the pass-2 batch moves the outgoing client off primary (plant site is
        // just before BeginDeferWindowPos below). Null ⇒ nothing to plant.
        (IntPtr hwnd, int x, int y, int w, int h)? primaryPlant = null;

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
            int slotIdx = slot % slotCount;
            // v3.24.3 — multimon is always slim-managed; the taskbar MODE (not a
            // per-monitor slim flag) governs taskbar coverage inside EffectiveSlotBounds.
            bool useSlim = true;

            // THE single sizing authority: effective (origin + W×H) for this slot, applying
            // the taskbar mode + lock-to-primary. The IDENTICAL call backs the hook-config
            // pin (TrayManager.GetEffectiveMonitorForPid), the Windowed read-back, and the
            // eqclient.ini backbuffer — so all four agree on the secondary's size by
            // construction (no resize fight on swap, no Windowed-vs-Fullscreen band drift).
            var (effectiveBounds, _) = EffectiveSlotBounds(slotIdx,
                primaryFullMon, primaryWorkMon, secondaryFullMon, secondaryWorkMon, taskbarMode);

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
                // v3.22.81 — style per WindowMode (Fullscreen=WS_POPUP, Windowed=WS_CAPTION).
                long currentStyle = _api.GetWindowLongPtr(client.WindowHandle, NativeMethods.GWL_STYLE).ToInt64();
                long desiredStyle = DesiredSlimStyle(currentStyle, _config.Layout.WindowMode);
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
                // v3.22.45: route through the shared helper so multi-monitor
                // slim windows get the same Win11-DWM-bleed correction as
                // single-screen. Without this, the lock-to-primary-dims
                // policy would still leave 8 px desktop slivers on each side
                // of both monitors, AND DX swap-chain stretch artifacts
                // would appear on every client — exactly the same bug class
                // the lock-to-primary-dims was added to suppress on swap.
                long currentSlimStyle = DesiredSlimStyle(_api.GetWindowLongPtr(client.WindowHandle, NativeMethods.GWL_STYLE).ToInt64(), _config.Layout.WindowMode);
                long currentExStyle = _api.GetWindowLongPtr(client.WindowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
                (x, y, w, h) = ComputeSlimTitlebarOuterRect(effectiveBounds, titlebarOffset, currentSlimStyle, currentExStyle);
                swpFlags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
            }
            else
            {
                // v3.22.19: non-slim multi-monitor branch sizes to work-area.
                // Restore WS_THICKFRAME (resize border) if a prior slim
                // arrangement stripped it — eqswitch-hook.cpp only ever
                // STRIPS the flag (one-way), so C# has to restore it here
                // when transitioning slim → non-slim.
                // v3.22.76: restore WS_THICKFRAME + WS_CAPTION + WS_SYSMENU
                // and clear WS_POPUP when transitioning slim → non-slim.
                long style = _api.GetWindowLongPtr(client.WindowHandle, NativeMethods.GWL_STYLE).ToInt64();
                const long restoreMask = NativeMethods.WS_THICKFRAME | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU;
                long desiredRestore = (style & ~NativeMethods.WS_POPUP) | restoreMask;
                if (style != desiredRestore)
                {
                    _api.SetWindowLongPtr(client.WindowHandle, NativeMethods.GWL_STYLE, (IntPtr)desiredRestore);
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
                $"{client} → {monLabel} monitor ({effectiveBounds.Left},{effectiveBounds.Top}) {w}x{h}{slimLabel}{lockLabel} [slot={slot}]"));

            // v3.24.1 taskbar-flicker fix — remember the FIRST eligible slot-0
            // (primary-bound) client to plant before the batch. The dual-box swap has
            // exactly one; w>0/h>0 guards the degenerate no-fit case (a 0-size plant would
            // shrink the window). hasSecondary scopes the plant to genuine MULTI-monitor
            // swaps: the cross-monitor rude-window recalc is the only thing it fixes, so on
            // a single physical monitor (multimon MODE, one display — every client resolves
            // to slotIdx 0) the plant would be a pointless z-order raise on the first
            // client. Armed only when the caller opts in (swap path).
            if (coverPrimaryFirst && hasSecondary && slotIdx == 0 && w > 0 && h > 0 && !primaryPlant.HasValue)
                primaryPlant = (client.WindowHandle, x, y, w, h);
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

        // v3.24.1 taskbar-flicker fix — incoming-first coverage. Plant the primary-bound
        // client at HWND_TOP covering primary BEFORE the batch moves the outgoing client
        // off it, so the shell's rude-window recalc (fired by the outgoing client's
        // fullscreen-exit) always sees a covering top-of-Z window on primary → no
        // one-frame taskbar peek. HWND_TOP (not HWND_TOPMOST): EQ clients are non-topmost,
        // so top-of-normal-band is enough; HWND_TOPMOST is itself a documented flicker
        // trigger (DisplayFusion "Disallow TopMost Calls"). The pass-2 batch re-applies the
        // same primary coords with SWP_NOZORDER (preserves this z-order) — a harmless
        // redundant move on this one window. Our plant is a cross-process SetWindowPos in
        // EQSwitch.exe; eqswitch-hook.dll only hooks SetWindowPos INSIDE eqgame.exe, so it
        // is not intercepted — no fight. See docs/specs/2026-05-31-eqswitch-taskbar-flicker-fix.md.
        if (coverPrimaryFirst && primaryPlant.HasValue)
        {
            var p = primaryPlant.Value;
            _api.SetWindowPos(p.hwnd, NativeMethods.HWND_TOP, p.x, p.y, p.w, p.h,
                              NativeMethods.SWP_NOACTIVATE);
            FileLogger.Info($"ArrangeMultiMonitor: incoming-first cover — planted slot-0 hwnd 0x{p.hwnd.ToInt64():X} at HWND_TOP over primary ({p.x},{p.y}) {p.w}x{p.h} before batch (taskbar-flicker fix)");
        }

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

        // v3.22.53: apply dark-titlebar attribute to every arranged client.
        // ArrangeMultiMonitor doesn't go through ApplySlimTitlebar (inlines
        // its own SetWindowPos in pass-2 for atomic batching), so the helper
        // wouldn't otherwise fire on the multi-monitor path. Idempotent —
        // DWM no-ops when the attribute is already at the requested value.
        //
        // v3.22.53 post-verifier-fix: helper now writes BOTH 0 and 1 so the
        // OFF transition propagates without restart. We unconditionally call
        // it on every arrange (cost ~1 µs × N clients) so a user who toggles
        // DarkTitlebar off in Settings → Apply sees the light caption return
        // on the next arrange — not "stuck dark until eqgame restart". The
        // prior `if (DarkTitlebar)` gate suppressed the off-restore.
        foreach (var t in targets)
            ApplyDarkTitlebarIfRequested(t.hwnd);

        foreach (var t in targets)
            FileLogger.Info($"ArrangeMultiMonitor: {t.logLabel}");

        string modeLabel = $" (taskbarMode={taskbarMode})";
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

            // v3.22.45 (T2-Sonnet HIGH): branch on SlimTitlebar so post-swap
            // sizing uses the same Win11-DWM-bleed-corrected outer rect as
            // ApplySlimTitlebar / ArrangeMultiMonitor. Pre-v3.22.45 this path
            // unconditionally wrote the non-slim work-area math even when
            // slim mode was active — producing a one-frame Win11 sliver flash
            // after every SwapWindows (the subsequent ApplySlimTitlebarToAll
            // guard tick would re-correct, but injected PIDs were skipped by
            // that guard, leaving the wrong rect persistent on the hook side
            // until UpdateHookConfigForPid caught up).
            //
            // v3.22.45 post-T3-Sonnet MEDIUM (final round): only take the
            // slim path when ALL targets in the current mode want slim. In
            // multimonitor mode with mixed flags (primary slim, secondary
            // non-slim) we can't know from monitor center alone whether THIS
            // window's monitor is primary or secondary (the per-PID slot map
            // lives in TrayManager). Falling back to the pre-fix non-slim
            // sizing here is safe: ArrangeMultiMonitor + UpdateHookConfigForPid
            // own the per-monitor-correct slim math + bleed correction, and
            // they fire on every SwapWindows alongside this call.
            bool useSlim;
            if (_config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase))
            {
                useSlim = _config.Layout.SlimTitlebar && _config.Layout.SlimTitlebarSecondary;
            }
            else
            {
                useSlim = _config.Layout.SlimTitlebar;
            }

            int x, y, w, h;
            if (useSlim)
            {
                (x, y, w, h) = ComputeSlimTitlebarOuterRect(m, _config.Layout.TitlebarOffset);
            }
            else
            {
                x = m.Left;
                y = m.Top + yOffset;
                w = m.Width;
                h = m.Height;
            }

            _api.SetWindowPos(
                client.WindowHandle, IntPtr.Zero,
                x, y, w, h,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);

            FileLogger.Info($"ResizeToCurrentMonitors: {client} → ({x},{y}) {w}x{h} ({(_config.Layout.SlimTitlebar ? "slim" : "non-slim")})");
        }
    }

    // ─── Window Style Management ──────────────────────────────────

    /// <summary>
    /// Lightweight slim titlebar apply for all clients — just repositions windows
    /// without the full arrange logic. Used by foreground hook and auto-apply.
    /// </summary>
    public void ApplySlimTitlebarToAll(IReadOnlyList<EQClient> clients, IReadOnlySet<int>? injectedPids = null)
    {
        // v3.22.53 post-verifier-fix (T2 Opus + T3 Sonnet convergent CRITICAL):
        // dark-titlebar must apply even when SlimTitlebar is OFF, otherwise a
        // user who enables DarkTitlebar without SlimTitlebar gets zero DWM
        // calls and silently sees no effect. ApplyDarkTitlebarIfRequested is
        // idempotent (writes the CURRENT bool value — 0 to restore light, 1
        // to set dark) so it's safe to call unconditionally. Hoisted ABOVE
        // the SlimTitlebar/Multimonitor early-returns below so the dark
        // attribute reaches every EQ window on every foreground-hook /
        // guard-timer tick regardless of titlebar style.
        foreach (var c in clients)
        {
            if (_api.IsWindow(c.WindowHandle))
                ApplyDarkTitlebarIfRequested(c.WindowHandle);
        }

        if (!_config.Layout.SlimTitlebar) return;

        // v3.22.81 — this guard runs for BOTH window modes (Fullscreen AND
        // Windowed). An interim build briefly early-returned here for Windowed
        // on the theory that the hook DLL's GeoWndProc subclass fully replaced
        // it — WRONG, and reverted: the guard has TWO jobs, only one of which
        // GeoWndProc took over.
        //   1. Anti-growth (GeoWndProc's job now): the v3.22.80 growth came from
        //      this timer's read-modify-write reposition racing DWM with nothing
        //      clamping the result. GeoWndProc now forces wp->cx/cy to the fixed
        //      SHM size on EVERY WM_WINDOWPOSCHANGING, so the window is
        //      mechanically size-clamped — it CANNOT grow regardless of what this
        //      guard does. That removed the growth, not the guard-skip.
        //   2. Recreation recovery (still THIS guard's job): EQ destroys+recreates
        //      its top-level window at charselect→in-world as a NORMAL window
        //      (WS_THICKFRAME, work-area). ProcessManager doesn't re-fire
        //      ClientDiscovered (same PID), and GeoWndProc/the in-process hook
        //      don't reliably re-slim it until EQ happens to call SetWindowPos.
        //      Skipping this guard for Windowed left the in-world window stuck
        //      NORMAL (taskbar showing, full titlebar) until a manual titlebar
        //      double-click — Nate's 2026-05-29 smoke. This guard re-applies the
        //      slim style+position within a tick; GeoWndProc keeps it pinned.
        // So both modes use this guard for recovery; GeoWndProc makes the
        // Windowed re-applies growth-safe. v3.22.82 made the in-world re-slim
        // INSTANT (in-process, on EQ's window-show — eqswitch-hook.cpp
        // HookedShowWindow → EnsureGeoSubclass applies the slim style+rect
        // before the first paint), so for Windowed this guard is now pure
        // belt-and-suspenders recovery rather than the primary re-slim path.

        // v3.22.47 post-T2/T3 verifier: bail in multimonitor mode. This helper
        // sizes EVERY passed client to GetTargetMonitor(true) (the SINGLE
        // primary-or-configured target monitor), which is correct for
        // single-screen slim but disastrous in MM mode where each client lives
        // on a DIFFERENT monitor. With the v3.22.47 injected-clients-skip
        // removal, an MM-mode tick would compute expected_rect from primary
        // for the secondary client, the rect-compare would always fail, and
        // ApplySlimTitlebar would fire SetWindowPos targeting primary's rect
        // on a secondary-monitor client every 500 ms — fighting both
        // ArrangeMultiMonitor's per-client placement AND the hook DLL's
        // shared-memory enforcement. Pre-v3.22.47 the injected-skip masked
        // this latent bug; removing the skip exposed it. ArrangeMultiMonitor
        // (and UpdateHookConfigForPid + the hook DLL) own positioning in MM
        // mode; the C# guard belongs only to single-screen slim.
        if (_config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase))
            return;

        var monitor = GetTargetMonitor(true);
        int offset = _config.Layout.TitlebarOffset;

        for (int i = 0; i < clients.Count; i++)
        {
            var client = clients[i];
            if (!_api.IsWindow(client.WindowHandle)) continue;

            // Re-apply custom window title if EQ overwrote it
            SetWindowTitle(client, i);

            // v3.22.47: REMOVED the "skip injected clients" gate. Pre-v3.22.47
            // the C# guard skipped injected clients entirely on the assumption
            // that the in-process hook DLL would catch every resize EQ tried.
            // In practice, EQ's world-load DX device reset can trigger window
            // resizes via APIs the hook doesn't intercept (style changes,
            // direct WM_SIZE dispatches) — leaving the window in EQ's default
            // dimensions until the next foreground event or other trigger
            // (Nate's 2026-05-25 report: "6s ish before user has a working
            // screen ... wineq2.exe doesn't have this issue"). With the skip
            // removed, the rect-compare below (lines 967-970) makes the loop
            // a no-op for injected clients whose hook DLL kept them sized
            // correctly — so the cost in the happy path is just three cross-
            // process kernel reads per client per tick. When EQ DID resize
            // out from under the hook, the C# guard catches it within one
            // tick instead of waiting on an unrelated event.
            // (No comment from this line forward — the original perf-skip is
            //  superseded by the rect-compare which is cheaper anyway.)

            // Check if already positioned correctly — avoid unnecessary repositioning.
            //
            // v3.22.45 post-T3-Sonnet MEDIUM: compare ALL FOUR dims. The
            // pre-this version checked only rect.Top, which let a non-injected
            // client drift horizontally (external tool, user drag, EQ self-
            // positioning) without triggering re-apply — reintroducing the
            // left-edge sliver until the next style change. Width / height
            // drift is rarer but also possible (DPI change mid-session).
            //
            // v3.22.45 post-T3-Opus MEDIUM (final round): probe expected rect
            // using LIVE style + LIVE exStyle to match what ApplySlimTitlebar
            // will actually apply. The prior version hoisted the expected
            // rect using the canonical SLIM_TITLEBAR_STYLE — fast, but if EQ
            // ever has a non-zero exStyle (WS_EX_CLIENTEDGE shifts bleed
            // from 8/31/8/8 to 10/33/10/10), the hoisted canonical rect
            // never matches the live-style applied rect → guard always fails
            // → ApplySlimTitlebar re-fires every 500 ms cross-process. Per-
            // client probe is ~one kernel call per client per tick (~8 for
            // a 6-client setup) — negligible vs. the storm risk.
            // v3.22.81: project liveStyle through the same per-WindowMode strip+set
            // ApplySlimTitlebar will apply so the bleed probe matches.
            long rawStyle = _api.GetWindowLongPtr(client.WindowHandle, NativeMethods.GWL_STYLE).ToInt64();
            long liveStyle = DesiredSlimStyle(rawStyle, _config.Layout.WindowMode);
            _api.GetWindowRect(client.WindowHandle, out var rect);

            // v3.22.76 take-2 — SURGICAL STORM FIX. If the window's center is
            // OUTSIDE the configured monitor's bounds, the user (or EQ) moved
            // it to a different display. Forcing it back to (0,0,1920,1080)
            // every 500 ms produces a 2 Hz full-window SWP_FRAMECHANGED flash
            // (visible because WS_POPUP redraws the entire non-client surface,
            // not just the 18 px caption strip as WS_CAPTION did pre-v3.22.76).
            // The bug existed pre-v3.22.76 too — the WS_CAPTION caption-strip
            // redraw masked it. Smoke 2026-05-28 15:39 caught this storming for
            // ~3+ min until process kill. Skip the guard for off-monitor windows;
            // ApplyDarkTitlebar still fires per ApplySlimTitlebar's own path.
            int cx = rect.Left + (rect.Right - rect.Left) / 2;
            int cy = rect.Top + (rect.Bottom - rect.Top) / 2;
            bool centerOnConfiguredMonitor = cx >= monitor.Left && cx < monitor.Right
                                          && cy >= monitor.Top && cy < monitor.Bottom;
            if (!centerOnConfiguredMonitor)
            {
                continue;
            }

            // v3.22.81 re-verify fix (convergent swarm T2-Opus / T2-Sonnet / T3): for
            // Windowed mode the hook DLL's GeoWndProc subclass OWNS size + position
            // (it forces the SHM rect on every WM_WINDOWPOSCHANGING). This guard's
            // ONLY remaining Windowed job is recreation-recovery — re-slim EQ's window
            // after it's recreated NORMAL at charselect→in-world. Gate that on
            // STYLE-regression, NOT an exact 4-way rect compare: AdjustWindowRectEx
            // PREDICTS the outer rect (e.g. 1936×1119) but a WS_CAPTION-no-WS_THICKFRAME
            // window's real Win11 metrics can settle a few px smaller (~1926×1109), so
            // an exact compare would read rect != expected FOREVER while GeoWndProc
            // holds the size → a 2 Hz re-apply storm (same class as the v3.22.76
            // off-monitor storm above). Style-gating also moots the live-exStyle-vs-
            // SHM-exStyle=0 asymmetry — no rect is computed for the Windowed gate.
            if (_config.Layout.WindowMode == Config.WindowMode.Windowed)
            {
                // rawStyle == DesiredSlimStyle(rawStyle) ⇔ already slim (the projection
                // is idempotent) → GeoWndProc holds geometry, nothing to do. Re-apply
                // only when the style regressed to NORMAL (WS_THICKFRAME back / caption
                // lost) — i.e. EQ recreated the window; ApplySlimTitlebar restyles AND
                // repositions, then GeoWndProc resumes pinning.
                if (rawStyle == liveStyle) continue;
                ApplySlimTitlebar(client.WindowHandle, monitor, offset);
                continue;
            }

            // Fullscreen: WS_POPUP has 0 frame bleed so AdjustWindowRectEx is exact,
            // and there is NO subclass pin — keep the precise 4-way rect compare (the
            // v3.22.45 / v3.22.76 logic) that owns Fullscreen geometry. Live exStyle is
            // fetched here (Fullscreen-only) to match what ApplySlimTitlebar applies.
            long liveExStyle = _api.GetWindowLongPtr(client.WindowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
            var (expectedX, expectedY, expectedW, expectedH) = ComputeSlimTitlebarOuterRect(monitor, offset, liveStyle, liveExStyle);
            if (rect.Left == expectedX
                && rect.Top == expectedY
                && rect.Width == expectedW
                && rect.Height == expectedH) continue;

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

        // v3.22.81 — style per WindowMode. Fullscreen = WS_POPUP (0/0/0/0 bleed,
        // fills monitor). Windowed = keep WS_CAPTION|WS_SYSMENU, strip the resize
        // border (8/31/8 bleed → caption peeks, WinEQ2 look). DesiredSlimStyle is
        // symmetric so a live mode switch restyles correctly either direction.
        // Gated on "actually needed" so steady-state guard-timer ticks no-op.
        long style = _api.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        long desired = DesiredSlimStyle(style, _config.Layout.WindowMode);
        if (style != desired)
        {
            _api.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, (IntPtr)desired);

            // Step 1: Apply style change only (no move, no resize).
            // Use NOACTIVATE instead of SHOWWINDOW — SHOWWINDOW triggers EQ's
            // focus-loss handler during initialization, causing the game to minimize.
            _api.SetWindowPos(
                hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }
        style = desired;

        // Step 2: Position and size the window so the VISIBLE client area
        // covers the full monitor edge-to-edge. v3.22.45 fix — see
        // ComputeSlimTitlebarOuterRect for the Win11 DWM-bleed root cause.
        // Read live GWL_EXSTYLE (v3.22.45 post-T3-Opus MEDIUM) — pass it to
        // AdjustWindowRectEx so any WS_EX_CLIENTEDGE-style bits on the real
        // EQ window get reflected in the bleed calculation. Skipping this
        // step reintroduces a 4-px-per-side sliver if EQ ever has CLIENTEDGE.
        long exStyle = _api.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        // v3.22.88 — prefer the cached MEASURED frame so THIS first visible reposition lands
        // flush (no read-back snap). ApplySlimTitlebar is the guard-tick positioner that sets
        // the visible window during autologin while UpdateHookConfigForPid's SHM rect is still
        // deferred (~7s) — so THIS is the path that actually eliminates the snap (the first
        // v3.22.88 wired only the no-HWND SHM overload and missed this, which the live warm
        // smoke caught). On a cache miss / Fullscreen / wrong-DPI / insane / null cache it
        // falls back to the live-style AdjustWindowRectEx prediction (today's behavior), and
        // the read-back then self-corrects + populates the cache so the NEXT launch is flush.
        var (x, y, w, h) = TryCachedOuterRect(monitor, titlebarOffset, out var cachedRect)
            ? cachedRect
            : ComputeSlimTitlebarOuterRect(monitor, titlebarOffset, style, exStyle);
        _api.SetWindowPos(
            hwnd, IntPtr.Zero, x, y, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // v3.22.53: apply the dark-titlebar attribute on every slim apply.
        // Re-applying every tick is cheap (DWM compares and no-ops if
        // unchanged) and self-healing — if EQ flips the attribute back via
        // its own DWM call during a screen transition, the guard-timer's
        // next ApplySlimTitlebarToAll pass restores it.
        ApplyDarkTitlebarIfRequested(hwnd);

        // v3.22.81 — report the actual mode (was hardcoded "WS_POPUP slim" which
        // misdescribed Windowed-mode applies once the guard re-enable made this
        // path reachable for Windowed).
        string styleLabel = _config.Layout.WindowMode == Config.WindowMode.Windowed
            ? $"WS_CAPTION slim ({Math.Clamp(titlebarOffset, 0, 40)}px caption peek)"
            : "WS_POPUP slim (0px caption visible)";
        FileLogger.Info($"ApplySlimTitlebar: hwnd={hwnd} → ({x},{y}) {w}x{h}, offset={titlebarOffset}px requested → {styleLabel}");
    }

    /// <summary>
    /// v3.22.53 — apply <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> to the given EQ
    /// window, writing the current value of <see cref="WindowLayout.DarkTitlebar"/>
    /// (1 if on, 0 if off). Both transitions matter: writing 0 actively
    /// restores the light caption when the user disables the option in
    /// Settings → Apply, so the toggle is symmetric and live-effective
    /// without an eqgame restart.
    ///
    /// Cross-process safe: DWM is a system service so the attribute lookup
    /// uses the target HWND regardless of which process makes the call.
    /// Tries the modern attribute id (20) first and falls back to the Win10
    /// 1809–1909 legacy id (19) if DWM returns a failure code — ~1 µs cost
    /// avoids missing the old builds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HRESULT semantics: <c>0</c> = S_OK; negative = failure. We only retry
    /// the legacy attribute on a clear failure (HRESULT &lt; 0), not on
    /// S_FALSE (which means "applied, but didn't change anything").
    /// </para>
    /// <para>
    /// v3.22.53 post-verifier-fix (T3 Opus IMPORTANT): the previous shape
    /// early-returned on <c>!DarkTitlebar</c>, leaving any HWND that had been
    /// dark-applied stuck with the dark caption indefinitely until the
    /// eqgame process exited. The Settings → Apply flow promises live
    /// effect (see ReloadConfigCore plumbing for DarkTitlebar) so an
    /// asymmetric toggle is a real bug, not a feature. Now writes whichever
    /// value the live config holds.
    /// </para>
    /// </remarks>
    private void ApplyDarkTitlebarIfRequested(IntPtr hwnd)
    {
        try
        {
            int useDark = _config.Layout.DarkTitlebar ? 1 : 0;
            int hr = NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            if (hr < 0)
            {
                hr = NativeMethods.DwmSetWindowAttribute(
                    hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref useDark, sizeof(int));
                if (hr < 0)
                {
                    FileLogger.Warn($"ApplyDarkTitlebar: both attribute ids failed for hwnd=0x{hwnd.ToInt64():X} useDark={useDark} (HRESULT=0x{hr:X}); likely pre-1809 Windows");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // DWM call should never throw, but log defensively rather than
            // crash the guard-timer tick.
            FileLogger.Warn($"ApplyDarkTitlebar: unexpected exception: {ex.Message}");
        }
    }

    /// <summary>
    /// v3.22.45 — canonical post-WS_THICKFRAME-strip style for an EQ slim-
    /// titlebar window. Used by the no-HWND overload of
    /// <see cref="ComputeSlimTitlebarOuterRect(WinRect, int)"/> because
    /// AdjustWindowRectEx output depends only on style bits, not which
    /// exact window we're sizing.
    /// </summary>
    // v3.22.76 — WinEQ2 -frame none parity. Pre-v3.22.76 kept WS_CAPTION |
    // WS_SYSMENU for a thin draggable caption; WS_CAPTION on Win11 triggers
    // the 8/31/8/8 DWM bleed that motivated v3.22.45 / .46 / .54. Switching
    // to WS_POPUP eliminates the bleed at the source (0/0/0/0). Loss: no
    // native click-and-drag — multimonitor auto-positions via SetWindowPos
    // (style-agnostic) and the slim-guard re-positions every 500 ms anyway,
    // so click-drag was a no-op in normal multibox use.
    internal const long SLIM_TITLEBAR_STYLE =
        NativeMethods.WS_POPUP
        | NativeMethods.WS_CLIPSIBLINGS
        | NativeMethods.WS_CLIPCHILDREN
        | NativeMethods.WS_VISIBLE;

    // v3.22.81 — Windowed-mode probe style: pre-v3.22.76 thin draggable caption
    // (the WinEQ2 look — real titlebar + buttons, resize border stripped). On
    // Win11 AdjustWindowRectEx gives ~8/31/8/8 bleed → caption peeks at the top.
    internal const long WINDOWED_TITLEBAR_STYLE =
        NativeMethods.WS_CAPTION
        | NativeMethods.WS_SYSMENU
        | NativeMethods.WS_CLIPSIBLINGS
        | NativeMethods.WS_CLIPCHILDREN
        | NativeMethods.WS_VISIBLE;

    /// <summary>
    /// v3.22.81 — target GWL_STYLE for a client's current style under the given
    /// window mode. Fullscreen = WinEQ2 -frame none (WS_POPUP, no caption, fills
    /// monitor). Windowed = thin draggable caption (keep WS_CAPTION|WS_SYSMENU,
    /// strip the resize border). BOTH strip WS_THICKFRAME. The hook
    /// (eqswitch-hook.cpp) also strips WS_THICKFRAME, which is correct for both.
    /// </summary>
    public static long DesiredSlimStyle(long currentStyle, Config.WindowMode mode)
    {
        // Symmetric so live mode switches restyle correctly in BOTH directions
        // (a Fullscreen WS_POPUP window switched to Windowed must regain its
        // caption; a Windowed window switched to Fullscreen must lose it).
        if (mode == Config.WindowMode.Windowed)
            // strip resize border + popup, restore caption + sysmenu
            return (currentStyle & ~(NativeMethods.WS_THICKFRAME | NativeMethods.WS_POPUP))
                   | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU;
        // Fullscreen: strip caption+sysmenu+resize border, set popup
        const long stripMask = NativeMethods.WS_THICKFRAME | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU;
        return (currentStyle & ~stripMask) | NativeMethods.WS_POPUP;
    }

    /// <summary>
    /// Probe style (fed to AdjustWindowRectEx for the non-client bleed) for the
    /// given mode: WS_POPUP (0 bleed → fill) for Fullscreen, WS_CAPTION (8/31/8
    /// → caption peek) for Windowed.
    /// </summary>
    public static long ProbeStyleFor(Config.WindowMode mode)
        => mode == Config.WindowMode.Windowed ? WINDOWED_TITLEBAR_STYLE : SLIM_TITLEBAR_STYLE;

    /// <summary>
    /// Overload for callers that don't hold a live HWND: the TrayManager
    /// hook-config builder (which writes the SHM rect GeoWndProc pins to in
    /// Windowed mode), ArrangeMultiMonitor's lock-to-primary-dims policy, and
    /// ResizeToCurrentMonitors.
    /// <para>
    /// v3.22.81 — picks the probe style from the LIVE <see cref="WindowMode"/>:
    /// WS_POPUP (0/0/0/0 bleed → monitor-exact) for Fullscreen, WS_CAPTION
    /// (~8/31/8/8 bleed → caption-peek overflow rect) for Windowed. Pre-v3.22.81
    /// this hardcoded <c>SLIM_TITLEBAR_STYLE</c> (WS_POPUP) — correct when WS_POPUP
    /// was the only slim style, but for Windowed it yielded a monitor-exact rect
    /// (0 bleed) that, once written to SHM and pinned by the hook DLL's GeoWndProc
    /// subclass, re-introduced the right-edge sliver AND put the caption fully
    /// on-screen instead of peeking off the top. Now produces the same rect as
    /// the HWND-aware overload (which reads the live WS_CAPTION style), so the
    /// pinned SHM rect matches what ApplySlimTitlebar applies.
    /// </para>
    /// </summary>
    /// <summary>
    /// v3.22.88 — shared cache-read decision used by BOTH the no-HWND SHM-rect overload
    /// (<see cref="ComputeSlimTitlebarOuterRect(WinRect,int)"/>) AND the live-window
    /// positioner (<see cref="ApplySlimTitlebar"/>). When Windowed and a sane MEASURED
    /// frame is cached for the current DPI, build the outer rect from that measured frame
    /// via the existing <see cref="ComputeOuterRectFromBleeds"/> — identical to what
    /// <see cref="TryComputeReadbackCorrection"/> converges to (same helper, no nudge) —
    /// so the window lands flush on FIRST paint with NO read-back snap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE LIVE-SMOKE LESSON (2026-05-30): the first v3.22.88 wired the cache ONLY into the
    /// no-HWND overload (TrayManager.UpdateHookConfigForPid's SHM rect). But for autologin
    /// clients UpdateHookConfigForPid is DEFERRED ~7s, so the VISIBLE window during login is
    /// positioned by <see cref="ApplySlimTitlebar"/> (the ~500 ms guard tick), which used the
    /// AdjustWindowRectEx PREDICTION — so the window still appeared at the overshoot and the
    /// read-back still snapped it. The warm smoke showed ZERO snap-elimination (ReadbackCorrect
    /// still fired). Routing ApplySlimTitlebar through this SAME helper is what actually kills
    /// the snap: the first visible SetWindowPos lands flush → read-back measures flush → no-op.
    /// </para>
    /// <para>
    /// Returns false (caller falls back to the AdjustWindowRectEx prediction = today's exact
    /// behavior) on: non-Windowed (Fullscreen WS_POPUP 0-frame), null cache, cache miss,
    /// wrong-DPI, or an insane cached frame (FrameCache.TryGet self-validates). So the cache
    /// can only improve (flush from first paint) or be neutral.
    /// </para>
    /// </remarks>
    private bool TryCachedOuterRect(WinRect monitor, int titlebarOffset, out (int x, int y, int w, int h) rect)
    {
        rect = default;
        if (_config.Layout.WindowMode != Config.WindowMode.Windowed) return false;
        if (_frameCache == null) return false;
        if (!_frameCache.TryGet((int)_api.GetSystemDpi(), out var f)) return false;
        rect = ComputeOuterRectFromBleeds(monitor, titlebarOffset, f.Left, f.Top, f.Right, f.Bottom);
        return true;
    }

    internal (int x, int y, int w, int h) ComputeSlimTitlebarOuterRect(
        WinRect monitor, int titlebarOffset)
    {
        // v3.22.88 — warm frame-cache fast path for the FIRST-PAINT SHM rect
        // (TrayManager.UpdateHookConfigForPid). On a cache hit the client lands flush on
        // first paint with no read-back snap; any miss / Fullscreen / insane / wrong-DPI /
        // null cache falls through to the AdjustWindowRectEx prediction = today's behavior.
        // The nudge is intentionally skipped on the cached path (a MEASURED frame needs no
        // prediction-error correction; default HorizontalNudgePx is 0 anyway).
        if (TryCachedOuterRect(monitor, titlebarOffset, out var cached))
            return cached;
        return ComputeSlimTitlebarOuterRect(monitor, titlebarOffset, ProbeStyleFor(_config.Layout.WindowMode), 0);
    }

    /// <summary>
    /// v3.22.45 — compute the OUTER-window rect for a slim-titlebar window so
    /// its VISIBLE CLIENT area exactly covers the given monitor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pre-v3.22.45 the math assumed that stripping WS_THICKFRAME left no
    /// non-client area other than the caption, and that <c>titlebarOffset</c>
    /// equalled the caption height. Both were roughly true on Windows 10 —
    /// where a slim-styled (WS_CAPTION only) window had ~0 px frame bleed and
    /// a ~22 px caption — so setting <c>outer.W = monitor.W</c> yielded a
    /// client area exactly the monitor width.
    /// </para>
    /// <para>
    /// On Windows 11, DWM keeps an invisible ~8 px frame zone on the left,
    /// right and bottom of every window for rounded-corner masking + shadow
    /// hit-testing, regardless of WS_THICKFRAME. So <c>outer.W = 1920</c> on
    /// a 1920-wide monitor produces a client area of <b>1904 px</b>: an 8 px
    /// sliver of desktop is visible on each side AND EQ's DX swap chain
    /// (initialized from <c>eqclient.ini WindowedWidth=1920</c>) gets
    /// bilinearly stretched into the narrower client area, producing the
    /// reported 1-px-wide vertical text-smear seam near screen centre.
    /// </para>
    /// <para>
    /// The fix asks Win32 (via <c>AdjustWindowRectEx</c>) what the actual
    /// non-client bleed is for the post-WS_THICKFRAME-strip style and offsets
    /// the outer rect so the bleed sits OFF-SCREEN on every edge. Result:
    /// <c>client.Left == monitor.Left</c>, <c>client.Right == monitor.Right</c>,
    /// <c>client.Bottom == monitor.Bottom</c>, no visible desktop sliver,
    /// DX swap chain renders 1:1 into the client area.
    /// </para>
    /// <para>
    /// <c>titlebarOffset</c> semantics preserved exactly: <i>"pixels of caption
    /// to leave VISIBLE inside the monitor for dragging"</i>. With
    /// <c>titlebarOffset=0</c> the caption is fully off-screen (max game area,
    /// no peek — looks borderless); with <c>titlebarOffset=18</c> (the v3.22.82
    /// default) 18 px of caption peek at the top and the client is rendered 18 px
    /// shorter so the bottom stays FLUSH against the monitor edge (the WinEQ2
    /// method — crisp because EQ's DX backbuffer matches the reduced client). The
    /// arg is
    /// clamped to <c>[0, topBleed]</c> so a misconfigured value can never
    /// push the whole caption off-screen unintentionally.
    /// </para>
    /// <para>
    /// If <c>AdjustWindowRectEx</c> fails for any reason (style param garbage,
    /// kernel WTF), the method falls back to the pre-v3.22.45 math so a Win11
    /// regression in this code path produces "old broken sliver" rather than
    /// "window in random place".
    /// </para>
    /// </remarks>
    internal (int x, int y, int w, int h) ComputeSlimTitlebarOuterRect(
        WinRect monitor, int titlebarOffset, long style, long exStyle)
    {
        // FRAME NOTE — two distinct geometry caveats, both confirmed by live
        // eqgame measurement (do NOT "verify" these with a synthetic WinForms
        // probe — a WinForms client inset matches AdjustWindowRectEx; eqgame's
        // does NOT, so a proxy window hides caveat (1)):
        //  (1) eqgame's REAL non-client frame is ~3px/side, but AdjustWindowRectEx
        //      predicts ~8px for WS_CAPTION — so this outer rect (sized for 8px)
        //      leaves eqgame's CLIENT ~5px too wide on each edge. Live-measured
        //      2026-05-30: Windowed client 1930×1072 on a 1920×1080 monitor,
        //      ~5px overshoot L/R/B at 100% DPI. Real but cosmetically benign
        //      (extreme edge off-screen, sub-0.5% stretch). The genuine fix is a
        //      READ-BACK: after SetWindowPos, GetClientRect the live eqgame window
        //      and correct the outer rect (WinEQ2 "measure, don't predict"). TODO.
        //  (2) REQUIRES HighDpiMode.SystemAware (Program.cs): AdjustWindowRectEx
        //      returns SYSTEM-DPI metrics — fine on uniform DPI; on MIXED-DPI
        //      multimon a secondary at a different scale gets the primary's metrics
        //      → extra sliver on that monitor. Deferred (PerMonitorV2 regressed
        //      single-screen team-launch, reverted v3.22.19).
        //
        // v3.22.45 post-T3-Opus MEDIUM: exStyle is now a real arg. If the live
        // EQ window has WS_EX_CLIENTEDGE (live Win11 probe: shifts bleed from
        // 8/31/8/8 to 10/33/10/10), passing exStyle=0 under-sizes the outer
        // rect by 4 px each side and a smaller sliver returns. Dalaya's Edge-
        // DINPUT8 build hasn't been observed using any non-zero exStyle but
        // we let AdjustWindowRectEx decide.
        //
        // Probe the non-client bleed with a 100x100 sentinel rect — the
        // returned negative L/T and excess R/B are the bleed amounts.
        var probe = new WinRect { Left = 0, Top = 0, Right = 100, Bottom = 100 };
        bool ok = _api.AdjustWindowRectEx(ref probe, (uint)style, false, (uint)exStyle);
        // lastErr already captured + logged inside WindowsApi.AdjustWindowRectEx
        // wrapper (v3.22.45 post-T3-Opus MEDIUM fix) — don't reach for
        // Marshal.GetLastWin32Error() here because the rect-copy below would
        // clobber it. Keep this fallback branch simple.
        if (!ok)
        {
            // v3.22.54 round-2 fix (T3 Opus IMPORTANT): apply nudge in the
            // fallback path too. Skipping it here was silent — the user
            // configured HorizontalNudgePx to fix the exact "Win11 desktop
            // sliver" the fallback comment warns about, so dropping the
            // nudge here is exactly the wrong place to drop it. Loud-fail
            // contract per [[reference_loud_runtime_silent_rest]].
            // v3.22.76 — fallback returns plain monitor edges. WS_POPUP slim has
            // no caption to hang above-monitor; the pre-v3.22.45 trick
            // (`monitor.Top - titlebarOffset` + height extension) would push
            // the window off-screen with nothing to render.
            FileLogger.Warn($"ComputeSlimTitlebarOuterRect: AdjustWindowRectEx returned false — falling back to monitor edges (no caption peek; matches EQClientSettingsForm.SlimTitlebarCaptionVisible fallback=0)");
            return (
                monitor.Left,
                monitor.Top,
                monitor.Right - monitor.Left,
                monitor.Bottom - monitor.Top);
        }

        int leftBleed   = -probe.Left;
        int topBleed    = -probe.Top;
        int rightBleed  = probe.Right - 100;
        int bottomBleed = probe.Bottom - 100;

        // v3.22.46: clip outer-rect extension on edges where ANOTHER monitor
        // abuts the target monitor. Pre-v3.22.46 the outer rect extended past
        // the monitor by `bleed` on every side so the DWM frame zone sat off-
        // desktop (invisible). On a multi-monitor setup that "off-desktop"
        // space is actually another monitor — and the DWM frame-shadow strip
        // becomes visibly painted on the adjacent monitor (Nate's 2026-05-25
        // report: "the window now bleeds over onto the right monitor"). Drop
        // the extension to 0 on adjacent edges; visible client loses `bleed`
        // px there (~8 px gap of desktop visible on primary monitor's edge,
        // same as pre-v3.22.45 baseline) but no longer paints onto the other
        // monitor. WindowedWidth/Height in eqclient.ini compensates so the DX
        // swap chain stays 1:1 with the new visible client size — no text
        // smear returns. See ClampBleedsForAdjacency for the per-edge logic.
        // v3.22.81 — Windowed mode: DROP the adjacency clamp so the WS_CAPTION
        // frame bleed hangs off ALL edges (flush sides). ClampBleedsForAdjacency
        // zeroed the bleed on an edge abutting a 2nd monitor (to avoid an ~8px
        // shadow strip painting onto the neighbor), but that left the client 8px
        // short on that edge → the reported right-edge desktop sliver. WinEQ2
        // does NOT clamp — its anti-growth is the WndProc subclass (now ours:
        // GeoWndProc in the hook DLL), not bleed-avoidance. Tradeoff (accepted
        // per the WinEQ2 recipe + design-spec §7.4): an ~8px shadow strip may
        // paint onto an abutting neighbor monitor in Windowed mode. Fullscreen
        // (WS_POPUP, 0/0/0/0 bleed) is unaffected — the clamp is a no-op there.
        var allMonitors = _api.GetAllMonitorBounds();
        int effLeftBleed, effRightBleed, effBottomBleed;
        if (_config.Layout.WindowMode == Config.WindowMode.Windowed)
        {
            (effLeftBleed, effRightBleed, effBottomBleed) = (leftBleed, rightBleed, bottomBleed);
        }
        else
        {
            (effLeftBleed, effRightBleed, effBottomBleed) = ClampBleedsForAdjacency(
                monitor, allMonitors, leftBleed, rightBleed, bottomBleed);
        }

        var (x, y, w, h) = ComputeOuterRectFromBleeds(monitor, titlebarOffset, effLeftBleed, topBleed, effRightBleed, effBottomBleed);

        // v3.22.54: apply horizontal nudge. Win11 multi-monitor DPI rounding can
        // leave a 1-px desktop sliver on one edge of the client area while the
        // other edge sits flush against the monitor border. Setting
        // Layout.HorizontalNudgePx = +1 shifts the whole window right by 1
        // (gap moves from right edge to left edge); -1 shifts it left. Field
        // clamped to ±10 in Validate. Applied here (the single per-monitor
        // entry point for slim-titlebar outer-rect math) so single-screen,
        // multi-monitor, ApplySlimTitlebarToAll, ArrangeMultiMonitor, and
        // ResizeToCurrentMonitors all pick it up uniformly.
        //
        // v3.22.54 round-2 fix (T2 Opus + T3 Opus convergent IMPORTANT):
        // a naive `x += nudge` translates the WHOLE outer rect uniformly.
        // On the adjacent-monitor edge — where ClampBleedsForAdjacency
        // already set the bleed to 0 to keep the window from painting onto
        // the neighbor — translating past that boundary re-introduces the
        // exact v3.22.46 bleed-onto-adjacent-monitor regression. When the
        // nudge direction matches a clipped edge, NARROW w instead of
        // letting the outer rect overflow. Net effect: the window shifts
        // within the monitor (gap appears on the opposite edge as intended)
        // without crossing the adjacency boundary.
        int nudge = _config.Layout.HorizontalNudgePx;
        bool leftClipped  = effLeftBleed  != leftBleed;  // ClampBleedsForAdjacency zeroed it
        bool rightClipped = effRightBleed != rightBleed;
        // v3.22.76 — under WS_POPUP all bleeds are 0; the existing `else` branch
        // would translate `x += nudge` with no compensation, pushing the window
        // off-monitor. Suppress nudge entirely when there's no bleed to absorb it.
        if (leftBleed == 0 && topBleed == 0 && rightBleed == 0 && bottomBleed == 0)
        {
            // No-op — zero-bleed style → nudge has no meaningful target.
        }
        else if (nudge > 0 && rightClipped)
        {
            // Right adjacency: outer.Right is pinned at monitor.Right. Shift x
            // right by nudge AND narrow w by the same amount so outer.Right
            // stays at the boundary. Visible content shifts right; gap appears
            // on the left edge of the monitor.
            x += nudge;
            w -= nudge;
        }
        else if (nudge < 0 && leftClipped)
        {
            // Left adjacency: outer.Left is pinned at monitor.Left (effLeftBleed=0
            // means x is already at monitor.Left). To "shift left" visually
            // (gap on right edge), DON'T touch x — narrow w from the right
            // by |nudge| instead. Round-3 fix (T1 Sonnet + T1 Opus + T3 Opus
            // convergent CRITICAL): the previous shape did `x += nudge` AND
            // `w += nudge` which pushed outer.Left across monitor.Left into
            // the left neighbor — the exact v3.22.46 regression. The
            // right-clipped mirror narrows on the SAME side as the shift
            // because the shift moves AWAY from the clipped side there; here
            // the shift moves TOWARD the clipped side, so the analog is to
            // not shift at all and only narrow on the unclipped side.
            w += nudge;  // nudge < 0 narrows w by |nudge|; x unchanged
        }
        else
        {
            // No adjacency in the nudge direction — translate normally.
            // Reachable cases: nudge=0 (no-op), nudge>0 with no right
            // adjacency, nudge<0 with no left adjacency. Off-screen bleed
            // on the side away from a clipped edge is benign (the screen
            // clips it; no neighbor to paint onto).
            x += nudge;
        }

        return (x, y, w, h);
    }

    /// <summary>
    /// v3.22.46 — given a target monitor + the full monitor list, return
    /// effective bleed values for the left, right, and bottom edges where
    /// each is set to 0 if another monitor abuts that edge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Abuts" = the neighbor's edge exactly meets the target's edge on the
    /// orthogonal axis AND the two monitors overlap on the parallel axis.
    /// Win11 desktop arrangement always aligns abutting edges to exact pixel
    /// boundaries (the OS snaps during Display Settings configuration) so an
    /// exact-equality check is safe here; partial overlap (the monitor
    /// "doesn't quite reach" a corner) still counts as adjacency because the
    /// DWM-shadow strip would land on the overlap region.
    /// </para>
    /// <para>
    /// Top edge is intentionally NOT clipped: ApplySlimTitlebar's caption-
    /// visibility math (clamp <c>titlebarOffset</c> to <c>[0, topBleed]</c>)
    /// depends on the outer rect extending <c>topBleed</c> px above the
    /// monitor so the caption sits partially above-screen. Clipping top
    /// would force the caption to render INSIDE the monitor as 31 px of
    /// non-client area, eating game area. Setups with a monitor directly
    /// above primary are rare and the cost there (~18 px shadow strip) is
    /// accepted as a future enhancement.
    /// </para>
    /// </remarks>
    internal static (int leftBleed, int rightBleed, int bottomBleed) ClampBleedsForAdjacency(
        WinRect target, IReadOnlyList<WinRect> all,
        int leftBleed, int rightBleed, int bottomBleed)
    {
        bool hasLeft = false, hasRight = false, hasBottom = false;
        foreach (var m in all)
        {
            if (m.Left == target.Left && m.Top == target.Top && m.Right == target.Right && m.Bottom == target.Bottom)
                continue; // skip the target itself
            // Left adjacency: neighbor.Right == target.Left AND vertical overlap.
            if (m.Right == target.Left && m.Bottom > target.Top && m.Top < target.Bottom)
                hasLeft = true;
            // Right adjacency: neighbor.Left == target.Right AND vertical overlap.
            if (m.Left == target.Right && m.Bottom > target.Top && m.Top < target.Bottom)
                hasRight = true;
            // Bottom adjacency: neighbor.Top == target.Bottom AND horizontal overlap.
            if (m.Top == target.Bottom && m.Right > target.Left && m.Left < target.Right)
                hasBottom = true;
        }
        return (
            hasLeft ? 0 : leftBleed,
            hasRight ? 0 : rightBleed,
            hasBottom ? 0 : bottomBleed);
    }


    /// <summary>
    /// Pure-math companion to <see cref="ComputeSlimTitlebarOuterRect(WinRect, int, long)"/>
    /// — given pre-probed non-client bleed values, compute the outer rect.
    /// Split out for unit-testability (no Win32 dependency, no IWindowsApi
    /// mock surface required).
    /// </summary>
    /// <remarks>
    /// All four bleed args are positive pixel counts. <paramref name="titlebarOffset"/>
    /// is clamped to <c>[0, topBleed]</c> — above the caption height there's
    /// no caption left to show, and a negative value would push the outer
    /// rect downward (cutting game area off the top).
    /// </remarks>
    internal static (int x, int y, int w, int h) ComputeOuterRectFromBleeds(
        WinRect monitor, int titlebarOffset,
        int leftBleed, int topBleed, int rightBleed, int bottomBleed)
    {
        int captionVisible = Math.Clamp(titlebarOffset, 0, topBleed);
        int x = monitor.Left - leftBleed;
        int y = monitor.Top - topBleed + captionVisible;
        int w = (monitor.Right - monitor.Left) + leftBleed + rightBleed;
        // v3.22.82 — client height = monitor height MINUS the visible caption peek
        // (restores WinEQ2's actual method / our pre-v3.22.81 v3.22.45 geometry).
        // The caption peeks `captionVisible` px at the top and the client fills the
        // REST, so the bottom is FLUSH: client.Top = monTop + captionVisible, client
        // height = monH - captionVisible → client.Bottom = monBottom exactly.
        //
        // Crisp fonts come from EQ's DX backbuffer (eqclient.ini WindowedHeight,
        // written = monH - captionVisible in EQClientSettingsForm.EnforceOverrides)
        // matching THIS client height 1:1 — NOT from rendering at native monH. The
        // v3.22.81 "native height for crisp fonts" change was a misdiagnosis: the
        // real font seam was a WIDTH mismatch (client 1904 vs backbuffer 1920),
        // fixed separately by the flush-sides math + the GeoWndProc subclass; the
        // height never needed to be native, and forcing it there is what pushed the
        // bottom off-screen (the regression Nate caught 2026-05-30). A matched
        // backbuffer/client blit is crisp at ANY height. Fullscreen (WS_POPUP) has
        // captionVisible==0 → monH - 0 = monH, so its geometry is unchanged.
        int h = (monitor.Bottom - monitor.Top - captionVisible) + topBleed + bottomBleed;
        return (x, y, w, h);
    }

    /// <summary>
    /// v3.22.84 — generous upper bound on any single-edge non-client frame (px).
    /// A measured inset outside [0, this] is a torn / mid-transition / minimized
    /// read and is rejected so the correction can't fling the window.
    /// </summary>
    internal const int MaxMeasuredFramePx = 80;
    private static bool FrameSane(int f) => f >= 0 && f <= MaxMeasuredFramePx;

    /// <summary>
    /// v3.22.84 — WinEQ2 "measure, don't predict" read-back correction for Windowed
    /// mode. EQSwitch sizes the slim window with <see cref="ComputeSlimTitlebarOuterRect(WinRect,int,long,long)"/>,
    /// which PREDICTS eqgame's WS_CAPTION frame (~8/31/8/8 on Win11) via
    /// <c>AdjustWindowRectEx</c>; eqgame's REAL frame is only ~3/26/3/3, so the
    /// predicted outer rect leaves the visible client ~5px too wide per edge
    /// (live-measured 2026-05-30, char natedogg @ 100% DPI: client 1930×1072 on a
    /// 1920×1080 monitor). This MEASURES the live window's actual frame
    /// (<see cref="IWindowsApi.GetWindowRect"/> vs <see cref="IWindowsApi.GetClientScreenRect"/>)
    /// and recomputes the outer rect from the monitor + the MEASURED frame via the
    /// same <see cref="ComputeOuterRectFromBleeds"/> helper, so the client lands flush.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <c>true</c> with the corrected outer rect ONLY when a correction is
    /// warranted; <c>false</c> = leave the window alone. Gates (all required):
    /// Windowed mode (Fullscreen WS_POPUP has a 0 frame → nothing to correct, and
    /// the read-back is a structural no-op there); both rect reads succeed (window
    /// alive); the measured frame is sane (rejects torn/minimized reads); and the
    /// corrected rect differs from the CURRENT outer rect by &gt;1px on some edge.
    /// </para>
    /// <para>
    /// FIXED POINT / NO GROWTH: the corrected rect is derived from the MONITOR (fixed
    /// target) + the measured FRAME (constant for a given window/style) — NEVER from
    /// the live outer SIZE. So re-applying it converges in one pass and cannot
    /// accumulate. This is the structural difference from the reverted v3.22.81 C#
    /// guard, which read the DWM-bled OUTER rect and re-applied a bigger one. A
    /// window already flush re-measures to the same frame → same rect → returns false.
    /// </para>
    /// </remarks>
    internal bool TryComputeReadbackCorrection(
        IntPtr hwnd, WinRect monitor, int titlebarOffset,
        out (int x, int y, int w, int h) corrected)
    {
        corrected = default;
        if (_config.Layout.WindowMode != Config.WindowMode.Windowed) return false;

        // v3.22.85 — same liveness/iconic gates ApplySlimTitlebar uses (this is paid
        // for: a cross-process SetWindowPos via RepositionWindow fires only when this
        // returns true, and SetWindowPos on a HUNG client mid-zone-load DX reset is the
        // documented 14.5s pump-stall → crash class; on a MINIMIZED client the D3D9
        // device is released — same crash class). A minimized window's GetClientRect
        // also collapses to 0/0/0/0, which would otherwise pass FrameSane and fire a
        // bogus full-monitor move. Skip both — the next guard tick retries when live.
        if (_api.IsIconic(hwnd)) return false;
        if (!_api.IsClientResponsive(hwnd, out _)) return false;

        if (!_api.GetWindowRect(hwnd, out var win)) return false;
        if (!_api.GetClientScreenRect(hwnd, out var cli)) return false;

        int actL = cli.Left - win.Left;
        int actT = cli.Top - win.Top;
        int actR = win.Right - cli.Right;
        int actB = win.Bottom - cli.Bottom;
        if (!FrameSane(actL) || !FrameSane(actT) || !FrameSane(actR) || !FrameSane(actB))
            return false;

        // v3.24.3 — a SETTLED Windowed (WS_CAPTION) window always has a top caption frame
        // (actT ≈ 26px). A 0-top frame means the window is NOT yet in WS_CAPTION — a WS_POPUP
        // (Fullscreen) frame read mid Fullscreen→Windowed restyle (in multimon ApplySlimTitlebarToAll
        // bails, so the style flip lands a touch later than the synchronous single-screen path) or a
        // transitional state. FrameSane(0) is true, so without this a 0/0/0/0 frame would be cached
        // as the Windowed frame — poisoning the PERSISTED eqswitch-frame-cache.json (next cold launch
        // loads the bad 0-frame → captionless monitor-exact placement → the overshoot returns) AND
        // repositioning to a captionless rect. Skip; the next guard tick re-measures once the WS_CAPTION
        // restyle has settled. (Convergent completion-checkpoint verifier finding, 2026-06-01 — the
        // pre-warm in SetWindowMode is a new caller that measures only ~300ms after the toggle.)
        if (actT <= 0)
        {
            FileLogger.Info($"TryComputeReadbackCorrection: skip — 0-top frame (hwnd=0x{hwnd.ToInt64():X} L{actL} T{actT} R{actR} B{actB}); WS_CAPTION restyle not settled yet, guard tick will retry");
            return false;
        }

        // v3.22.88 — CACHE WRITE rides the existing measurement. The measured frame is a
        // stable per-DPI constant; persist it (write-on-change) so the NEXT launch's
        // first-paint SHM rect (the no-HWND ComputeSlimTitlebarOuterRect overload) is
        // built from the MEASURED frame → flush on first paint, no snap. Placed BEFORE the
        // idempotent early-return below so the COLD path (overshoot → correction) still
        // populates the cache. Only a frame that passed ALL the read-back gates above
        // (Windowed + !IsIconic + IsClientResponsive + FrameSane) is ever cached; the
        // ~500 ms guard tick re-measuring the same constant is a no-op (write-on-change).
        _frameCache?.Set((int)_api.GetSystemDpi(), new FrameCache.Frame(actL, actT, actR, actB));

        var (cx, cy, cw, ch) = ComputeOuterRectFromBleeds(monitor, titlebarOffset, actL, actT, actR, actB);

        // Idempotent: only correct when the current outer rect is off by >1px on
        // some edge. An already-flush window yields cx/cy/cw/ch == current → no-op.
        if (Math.Abs(cx - win.Left) <= 1 && Math.Abs(cy - win.Top) <= 1 &&
            Math.Abs((cx + cw) - win.Right) <= 1 && Math.Abs((cy + ch) - win.Bottom) <= 1)
            return false;

        corrected = (cx, cy, cw, ch);
        return true;
    }

    /// <summary>
    /// v3.22.84 — thin SetWindowPos wrapper (no z-order / no activate change) used
    /// by the Windowed read-back correction to snap a static injected client to the
    /// flush-corrected rect AFTER its SHM rect has been updated. Goes through
    /// <see cref="IWindowsApi"/> so it stays test-mockable and doesn't scatter Win32.
    /// </summary>
    public void RepositionWindow(IntPtr hwnd, int x, int y, int w, int h)
        => _api.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

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
            // QuickLogin{N} holds a typed char:/acct: slot value (v3.23.0). A char: bind
            // names a Character (enter-world); an acct: bind names an Account (charselect-
            // only) and intentionally falls through here — the EQ native title
            // ("EverQuest - CharName" once logged in) is the right render for a slot not
            // bound to a specific character. Phase 6 will rewire this whole indirection.
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
                // v3.23.0: extract the bare name from the typed slot value before lookup —
                // a "char:"/"acct:" prefix would never match FindCharacterByName.
                var slotName = QuickLoginSlot.Parse(boundName).Name;
                // v3.22.29 Orphan-1: snapshot under ConfigMutationLock. Fires
                // from WinForms timer (UI thread) but ReloadConfig swap of
                // _config.Characters can still race.
                Character? character;
                lock (ConfigManager.ConfigMutationLock)
                {
                    character = _config.FindCharacterByName(slotName);
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
        // v3.23.2: strip the typed char:/acct: prefix here too — boundName is the raw
        // slot value, so without Parse the title would render "char:Natedogg" verbatim.
        if (string.IsNullOrEmpty(charName) && !string.IsNullOrEmpty(boundName))
            charName = QuickLoginSlot.Parse(boundName).Name;

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
