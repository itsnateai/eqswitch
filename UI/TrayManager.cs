// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices; // Marshal.GetLastWin32Error in PopulateForceKillMenu (v3.22.27 Item 4)
using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

public class TrayManager : IDisposable
{
    // ─── Constants ───────────────────────────────────────────────────
    private const int MultiMonToggleDebounceMs = 500;
    private const int AffinityPollIntervalMs = 250;
    // Left: all clicks counted via MouseUp. This interval lets rapid clicks
    // accumulate before resolving single/double/triple.
    private static readonly int LeftClickResolveMs = SystemInformation.DoubleClickTime + 50;
    // Middle: counted via MouseUp (fires reliably for every click, unlike MouseDown
    // which Windows skips on the 2nd press, converting it to DBLCLK instead).
    private static readonly int MiddleClickResolveMs = SystemInformation.DoubleClickTime + 100;

    private readonly AppConfig _config;
    private readonly ProcessManager _processManager;
    private readonly WindowManager _windowManager;
    private readonly HotkeyManager _hotkeyManager;
    private readonly KeyboardHookManager _keyboardHook;
    private readonly AffinityManager _affinityManager;
    private readonly LaunchManager _launchManager;
    private readonly AutoLoginManager _autoLoginManager;
    private SynchronizationContext? _uiContext;

    private NotifyIcon? _trayIcon;

    // v3.22.61: cached eqgame.exe icon for posting WM_SETICON to each
    // discovered EQ window. Dalaya's eqgame.exe ships with a valid 32x32
    // icon resource but never calls SetClassLong(GCLP_HICON) or sends
    // WM_SETICON to its own windows, so Windows falls back to a generic
    // placeholder in the taskbar / alt-tab / window menu (visible as a
    // blank white square in the taskbar preview). Extracted once on first
    // ClientDiscovered, reused for every subsequent discovery. Disposed
    // in TrayManager.Dispose() to release the GDI HICON.
    private System.Drawing.Icon? _eqgameWindowIcon;
    private ContextMenuStrip? _contextMenu;
    private ToolStripMenuItem? _clientsMenu;

    // ── v3.22.72 "Lost client" balloon coalesce + menu-aware suppression ────
    // Pre-v3.22.72: ClientLost fired ShowBalloon immediately. FloatingTooltip
    // creates a WS_EX_NOACTIVATE | WS_EX_TOPMOST window, but ContextMenuStrip's
    // modal pump treats ANY new top-level window appearing in its z-band as a
    // menu-cancel trigger (regardless of NOACTIVATE). Result: closing both EQ
    // clients then right-clicking the tray menu got the menu closed by the
    // first balloon, then closed again by the second balloon on the next menu
    // open. Reported by Nate 2026-05-28: "the tooltips depop the right click
    // menu". Fix: queue lost-client labels in a list + 1.5s coalesce timer
    // (rapid co-exits collapse to one balloon), defer firing while the menu
    // is open, drain the queue on _contextMenu.Closed.
    private readonly List<string> _pendingLostClients = new();
    private System.Windows.Forms.Timer? _lostClientsCoalesceTimer;
    // v3.22.44 r2 (T3-Sonnet F7 / T3-Opus #5 MEDIUM): cache the Detach Hooks
    // item so UpdateClientMenu can refresh its Enabled state on every
    // ClientListChanged event. Round-1 only computed Enabled at
    // BuildContextMenu time, so the item was stuck disabled after first-run
    // (no clients yet) until the user happened to toggle MultiMonitor / PiP
    // / Settings — meaning the new opt-in eject path was effectively
    // unreachable from the tray menu right after launching clients.
    // v3.22.54: field retired — Detach Hooks menu item removed. Kept
    // declared (initialized to null) so the 10 RefreshDetachMenuState
    // callers stay valid without churn; the method early-returns and
    // does nothing now. Re-introducing the menu item would just
    // re-assign this field inside BuildContextMenu.
    private ToolStripMenuItem? _detachItem = null;
    // v3.22.53: cache the Force-Kill submenu so UpdateClientMenu can rebuild
    // the per-client items below it without disposing it (would orphan the
    // DropDownOpening handler and the cached reference).
    private ToolStripMenuItem? _forceKillMenu;
    // v3.22.53: cache the separator that sits between admin items (Force-kill,
    // Detach) and the per-client list so we don't have to recompute its slot
    // on every rebuild.
    private ToolStripSeparator? _clientsAdminSeparator;
    // v3.22.53: count of admin items at the top of the Clients submenu that
    // UpdateClientMenu must preserve (do NOT dispose). Bump if you add more
    // pre-list items. v3.22.54: dropped 3 → 2 after Detach Hooks menu item
    // removed (forceKill + separator now).
    private const int ClientsMenuAdminItemCount = 2;
    private Font? _boldMenuFont;

    // Hidden window to receive TaskbarCreated message (explorer.exe restart recovery)
    private TaskbarMessageWindow? _taskbarMessageWindow;

    // Event-driven foreground change detection (replaces polling timer)
    private IntPtr _foregroundHook;
    private NativeMethods.WinEventDelegate? _foregroundHookProc; // prevent GC collection
    // Timer for affinity retry attempts on newly launched clients
    private System.Windows.Forms.Timer? _retryTimer;

    // Debounce foreground changes — rapid Alt+Tab through windows fires dozens of
    // WinEvent callbacks per second. We only need to react once things settle.
    private System.Windows.Forms.Timer? _foregroundDebounceTimer;
    private const int ForegroundDebounceMs = 50;

    // Slim titlebar position guard — checks every 2s if EQ reset its window
    // position (happens on screen transitions like login → char select)
    private System.Windows.Forms.Timer? _slimTitlebarGuard;

    // Debounce timestamp for multi-monitor toggle (500ms)
    private long _lastMultiMonToggle;

    // PiP overlay
    private PipOverlay? _pipOverlay;

    // Process Manager (single-instance)
    private ProcessManagerForm? _processManagerForm;

    // Tray click detection — both left and middle use MouseUp counting
    private int _leftClickCount;
    private System.Windows.Forms.Timer? _leftClickTimer;
    private int _middleClickCount;
    private System.Windows.Forms.Timer? _middleClickTimer;


    // Track last two active clients for swap-last-two mode (by PID, not handle — handles can change)
    private int _lastActivePid;
    private int _previousActivePid;

    // Guard against stale timer ticks during ReloadConfig() — foreground debounce
    // callbacks queued on the message pump can fire between Stop/Start cycles.
    private bool _reloading;
    // v3.22.33 (T2 Opus Gap 5 MEDIUM): shutdown guard for ad-hoc one-shot
    // timers (recoveryTimer, detectTimer, arrangeTimer, etc.) that may be
    // queued in the WinForms message pump after Dispose() runs. Without
    // this, the tick handler reads _processManager / _windowManager which
    // were just disposed → NRE → swallowed by the surrounding try/catch
    // as a misleading "post-close taskbar-recovery failed" Error log line.
    // Set first thing in Dispose().
    private volatile bool _disposed;

    // DLL hook injection — hooks SetWindowPos/MoveWindow inside eqgame.exe
    private HookConfigWriter? _hookConfig;
    private readonly HashSet<int> _injectedPids = new();
    private readonly HashSet<int> _di8InjectedPids = new();

    // v3.22.46: dedupe RaiseClientsAboveTaskbar post-login fires. Pre-v3.22.46
    // ApplyDeferredCosmetics ran the TOPMOST↔NOTOPMOST dance from BOTH
    // LoginCredentialsSent (T+~7s) AND LoginComplete (T+~45s deferred) on
    // ALL clients — for a 2-client team launch that's 4 dance events, each
    // visibly nudging every client's z-band. Nate's 2026-05-25 report:
    // "happens a lot after the char enters the game ... 2nd client background
    // dances it might steal focus". The dance is still needed once per PID
    // to trigger Win11's taskbar-collapse, but firing repeatedly is the user-
    // facing problem. Tracking arrival-once-per-PID lets a session-spanning
    // re-launch (PID died → respawned, new PID) still get its initial dance;
    // entries are cleared on ClientLost.
    //
    // v3.22.46 post-T3-Sonnet thread-safety: lock-guarded. ClientLost's
    // Remove runs on the WinForms-timer UI thread (ProcessManager polls via
    // System.Windows.Forms.Timer), but the Add sites fire from
    // ApplyDeferredCosmetics → LoginCredentialsSent / LoginComplete handlers
    // in AutoLoginManager, which Post via _syncContext when set BUT have
    // bare-fallback Invoke paths at AutoLoginManager.cs:1024 / 2231 / 2396 /
    // 3533 that fire on the background thread when _syncContext is null
    // (early-init race). Concurrent HashSet<int> mutation is undefined
    // behavior in .NET — the lock cost is negligible (~3 fires per session
    // per PID) and the storm risk is real per
    // [[feedback_eqswitch_autologinmanager_static_field_race]].
    private readonly HashSet<int> _taskbarRaisedAfterLoginPids = new();
    private readonly object _taskbarRaisedLock = new();

    /// <summary>
    /// v3.22.46 thread-safe wrapper for the dedupe HashSet.Add. Returns true
    /// only on the FIRST insertion of <paramref name="pid"/> in this session.
    /// </summary>
    private bool TryClaimTaskbarRaise(int pid)
    {
        lock (_taskbarRaisedLock)
        {
            return _taskbarRaisedAfterLoginPids.Add(pid);
        }
    }

    /// <summary>
    /// v3.22.46 thread-safe wrapper for the dedupe HashSet.Remove, called
    /// from ClientLost so a re-launched PID gets its first-arrival dance back.
    /// </summary>
    private void DropTaskbarRaiseClaim(int pid)
    {
        lock (_taskbarRaisedLock)
        {
            _taskbarRaisedAfterLoginPids.Remove(pid);
        }
    }

    // v3.22.20: per-PID monitor slot (0=primary, 1=secondary, ...). Replaces
    // the legacy clientIndex-positional assignment so SwitchKey can actually
    // rotate which client owns which monitor — UpdateHookConfigForPid and
    // ArrangeMultiMonitor both consult this map, so the hook DLL and the
    // C# arrange path move in lockstep. Lifecycle: assigned on ClientDiscovered
    // (next free slot, scan-based), removed on ClientLost.
    // v3.22.21: assignment switched from `Count`-based (PID-recycle dupe-slot
    // bug — 4-way verifier convergence) to free-slot scan via AssignNextFreeSlot.
    private readonly Dictionary<int, int> _monitorSlotByPid = new();

    // v3.22.21: dedup overflow warn-log so 3+ clients on 2 monitors logs once
    // per new overflow level instead of per-arrange. Keys: overflow count
    // (post-add clients minus monitorCount). Never cleared — re-logging the
    // same overflow level on the second occurrence within a session adds
    // noise without information (overflow is a steady-state config issue,
    // not an urgent one).
    private readonly HashSet<int> _overflowCountsLogged = new();

    // Per-session dedup for the one-shot legacy-routing log line (plan nuance #12).
    private readonly HashSet<int> _legacySlotDeprecationLogged = new();


    public TrayManager(AppConfig config, ProcessManager processManager)
    {
        _config = config;
        _processManager = processManager;
        _uiContext = SynchronizationContext.Current;
        // v3.22.88 — inject the measured-frame cache so the first-paint SHM rect is
        // built from a prior run's MEASURED frame (flush on first paint, no zone-in snap).
        // FrameCache.Default loads eqswitch-frame-cache.json next to the exe; the live
        // read-back stays the self-healing source of truth if the cache is cold/stale.
        _windowManager = new WindowManager(config, frameCache: FrameCache.Default);
        _hotkeyManager = new HotkeyManager();
        _keyboardHook = new KeyboardHookManager();
        _affinityManager = new AffinityManager(config);
        _launchManager = new LaunchManager(config, _affinityManager, EQClientSettingsForm.EnforceOverrides);
        _launchManager.PreResumeCallback = InjectPreResume;
        _autoLoginManager = new AutoLoginManager(config, EQClientSettingsForm.EnforceOverrides);
        _autoLoginManager.PreResumeCallback = InjectPreResume;
        _autoLoginManager.StatusUpdate += (_, msg) => ShowBalloon(msg);
        ConfigManager.SaveFailed += msg => ShowBalloon($"⚠ Settings save failed: {msg}");
        _autoLoginManager.LoginStarting += (_, _) =>
        {
            // Pause guard timer during auto-login to prevent focus theft
            _slimTitlebarGuard?.Stop();
            FileLogger.Info("AutoLogin: paused slim titlebar guard timer");
        };
        _autoLoginManager.LoginCredentialsSent += (_, pid) =>
        {
            // BURST 1 done — apply cosmetic work NOW (T+~7s) instead of
            // waiting for LoginComplete (T+~30s after charselect-ready).
            // Guard timer stays paused; LoginComplete resumes it once the
            // charselect-load transition settles.
            FileLogger.Info($"AutoLogin: BURST 1 done for PID {pid} — applying deferred cosmetic work early");
            ApplyDeferredCosmetics(pid);
        };
        _autoLoginManager.LoginComplete += (_, pid) =>
        {
            // Resume guard timer
            _slimTitlebarGuard?.Start();
            FileLogger.Info("AutoLogin: resumed slim titlebar guard timer");

            // Re-apply (idempotent) — covers the case where LoginCredentialsSent
            // didn't fire (early abort) or EQ fought back during the charselect-
            // load transition and drifted off slim-titlebar.
            //
            // v3.22.22 round-4 (2026-05-20 smoke crash diagnosis): defer the
            // re-arrange by 15s instead of firing immediately. The Enter World
            // event triggers EQ's zone-load DX device reset which blocks the
            // window's message pump for 5-15s. If we fire ArrangeWindows during
            // that window — and ArrangeWindows necessarily touches this very
            // client's HWND (it's in `_processManager.Clients`) — pass-1's
            // SetWindowLongPtr blocks until the pump unsticks (14,471 ms
            // observed 2026-05-20 PID 24672 → crash). BURST 1 has already
            // applied the slim-titlebar at T+~7s before any Enter World fires,
            // so this safety-net re-apply only matters if EQ fought back
            // during the charselect-load transition; an extra 15s wait is
            // imperceptible and gives the DX swap-chain reconfig room to
            // finish before we touch the window again. WindowManager also has
            // a SendMessageTimeout probe (defense in depth) but the deferral
            // eliminates the race entirely instead of merely fast-failing it.
            int capturedPid = pid;
            var deferTimer = new System.Windows.Forms.Timer { Interval = 15000 };
            deferTimer.Tick += (s, e) =>
            {
                // v3.22.22 round-5 (R4 T3-Sonnet SEV-1): Stop+Dispose BEFORE
                // the try so a throw inside the body can't leave the timer
                // running and re-fire forever. Stop() is idempotent and never
                // throws under normal conditions; same for Dispose.
                deferTimer.Stop();
                deferTimer.Dispose();
                try
                {
                    // v3.22.60 (2026-05-27 smoke "phantom swap" 14-15s after
                    // autologin complete): was ApplyDeferredCosmetics(capturedPid).
                    // That call ran ArrangeWindows (MM mode) → ApplySlimTitlebar
                    // on EVERY client including the foreground one →
                    // SetWindowLongPtr + SetWindowPos(SWP_FRAMECHANGED) → brief
                    // frame redraw the user perceives as a swap, even though
                    // nothing actually changes (style/position already match).
                    //
                    // The slim-titlebar guard timer (resumed on LoginComplete,
                    // fires every 500ms-5s) already keeps slim-titlebar applied
                    // against EQ fighting back during charselect-load — making
                    // the deferred ApplySlimTitlebar redundant by T+45s. The
                    // RaiseClientsAboveTaskbar dance is already deduped per-PID
                    // (v3.22.46) so it would no-op here anyway.
                    //
                    // Keep only UpdateHookConfigForPid — the hook DLL's shared-
                    // memory config refresh is visually no-op (the DLL only
                    // reads SM when EQ calls SetWindowPos) and covers the
                    // potential edge case where EQ recreated its main HWND
                    // during world-entry.
                    if (_injectedPids.Contains(capturedPid))
                    {
                        FileLogger.Info($"AutoLogin: deferred hook-config refresh PID {capturedPid} (cosmetic re-apply skipped — v3.22.60 phantom-swap fix)");
                        UpdateHookConfigForPid(capturedPid);
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Error($"AutoLogin: deferred hook-config refresh failed (PID {capturedPid})", ex);
                }
            };
            deferTimer.Start();
        };
    }

    /// <summary>Apply the cosmetic work that was deferred during the auto-login
    /// sequence — slim-titlebar + hook config refresh. Idempotent: safe to call
    /// from both LoginCredentialsSent (early, T+~7s) and LoginComplete (late,
    /// T+~30s). Caller controls the slim-titlebar guard lifecycle separately.
    ///
    /// v3.22.20: in multi-monitor mode, runs the full ArrangeWindows so each
    /// client lands on its assigned monitor (slot map). The previous single-
    /// monitor `ApplySlimTitlebar(GetTargetMonitorBounds())` slammed the
    /// second client onto primary right at world-entry — visible as stacked
    /// windows in the 2026-05-19 smoke log. ArrangeWindows is idempotent so
    /// per-PID invocation doesn't churn correctly-placed clients.</summary>
    private void ApplyDeferredCosmetics(int pid)
    {
        var client = _processManager.Clients.FirstOrDefault(c => c.ProcessId == pid);
        if (client == null) return;

        bool isMM = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        if (isMM)
        {
            _windowManager.ArrangeWindows(_processManager.Clients, _monitorSlotByPid);
        }
        else if (_config.Layout.SlimTitlebar)
        {
            _windowManager.ApplySlimTitlebar(
                client.WindowHandle,
                _windowManager.GetTargetMonitorBounds(),
                _config.Layout.TitlebarOffset);
        }
        // Hook DLL injection is handled pre-resume (CREATE_SUSPENDED) — only
        // refresh shared-memory config (position + title template).
        if (_injectedPids.Contains(pid))
            UpdateHookConfigForPid(pid);

        // v3.22.38: z-order recovery for the autologin-completion path.
        // v3.22.32 added RaiseClientsAboveTaskbar to OnArrangeWindows (manual
        // Fix Window) and the sibling-close recovery path, but missed this
        // ApplyDeferredCosmetics path that runs from LoginCredentialsSent
        // (T+~7s) and LoginComplete (T+~30s). Result: clients reaching
        // in-world via autologin had correct slim-titlebar bounds but no
        // z-order recovery — taskbar (WS_EX_TOPMOST) visually sliced through
        // EQ's bottom edge. Skipped when slim is off (normal titlebar = no
        // taskbar overlap to recover).
        //
        // v3.22.46: gated by two new conditions in response to Nate's
        // 2026-05-25 "dance happens a lot ... background dance steals focus"
        // report.
        //
        //   (1) Dedupe-per-PID via _taskbarRaisedAfterLoginPids: the dance
        //       fires AT MOST ONCE per PID per session. Pre-v3.22.46 the
        //       dance fired from LoginCredentialsSent (T+~7s) AND LoginComplete
        //       (T+~45s deferred) for EVERY client — 4 fires for a 2-client
        //       team. The Win11 taskbar-collapse trigger only needs one dance
        //       per arrival to take effect; subsequent fires churn z-order
        //       visibly without changing outcome. Manual Fix Windows and
        //       ClientLost sibling-close recovery still bypass this dedupe
        //       (different call sites, distinct purposes — user-driven
        //       repair vs. autologin one-shot).
        //
        //   (2) Active-EQ-other-PID skip: when a DIFFERENT EQ client is the
        //       current foreground, suppress the dance entirely. The dance
        //       briefly puts the just-completed background PID into the
        //       TOPMOST band, which (even with SWP_NOACTIVATE) USER32 paints
        //       above the foreground client for one or two frames — visible
        //       as a flicker that disturbs the user actively playing on the
        //       other EQ window. The background PID's taskbar coverage will
        //       be re-applied the next time the user switches to it (via
        //       SwitchToClient or click) since the slim-titlebar guard timer
        //       and ApplyDeferredCosmetics's slim re-apply still run.
        if (_config.Layout.SlimTitlebar)
        {
            var activeClient = _processManager.GetActiveClient();
            bool eqAlreadyForeground = activeClient != null;
            bool foregroundIsDifferentEQ = activeClient != null && activeClient.ProcessId != pid;

            if (foregroundIsDifferentEQ)
            {
                FileLogger.Info($"ApplyDeferredCosmetics: PID {pid} background-completed while {activeClient} is foreground — skipping RaiseClientsAboveTaskbar to avoid disturbing user (v3.22.46)");
            }
            else if (!TryClaimTaskbarRaise(pid))
            {
                FileLogger.Info($"ApplyDeferredCosmetics: PID {pid} already raised once this session — skipping repeat dance (v3.22.46 dedupe)");
            }
            else
            {
                // v3.22.39: pass ALL clients, not just the one that fired
                // LoginComplete. B's dance raised B above A, which dropped A
                // below the taskbar; raising every responsive client restores
                // each client's coverage in one pass.
                RaiseClientsAboveTaskbar(_processManager.Clients, foregroundActive: eqAlreadyForeground);
            }
        }
    }

    public void Initialize()
    {
        // One-time cleanup: remove legacy proxy DLL files from game directory + app folder.
        // Single source of truth lives in UninstallHelper so the Uninstall button and
        // startup cleanup can never drift apart. Actions are logged via FileLogger from
        // inside the helper; we discard the human-readable list here.
        _ = UninstallHelper.RestoreLegacyDlls(_config.EQPath ?? string.Empty);

        // Win11 tray-icon hygiene (snippet:
        // _.claude/_templates/snippets/csharp/tray-icon-promoter.md). Sweep first
        // (zombies from prior versioned WinGet installs / single-file extraction
        // caches), then capture the baseline so Phase-2 orphan claim has a
        // "before NIM_ADD" reference. Existing Text seed below is non-empty, so
        // Shell_NotifyIcon will pass NIF_TIP and Explorer writes the full schema.
        TrayIconPromoter.SweepStaleEntries(
            ourExeName: Path.GetFileName(Application.ExecutablePath),
            currentExePath: Application.ExecutablePath);
        var trayBaseline = TrayIconPromoter.CaptureBaseline();

        _trayIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "EQSwitch - 0 clients",
            Visible = true
        };

        // Promote our subkey to visible-in-taskbar (vs hidden-in-overflow). Ticks
        // for up to 10 s while Explorer populates the schema, then self-disposes.
        // Runs entirely independently of the auto-login state machine.
        StartTrayIconPromotion(trayBaseline);

        // Late-bind the WinForms UI sync context. NotifyIcon's hidden message-only
        // window forces WindowsFormsSynchronizationContext to be installed on this
        // thread (if it wasn't already by Application.Run or earlier WinForms work).
        // Constructor-time capture (TrayManager ctor + AutoLoginManager ctor) may
        // have grabbed null on a normal launch path, leaving background-thread
        // events firing synchronously and racing _injectedPids and other UI state.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
        _uiContext = SynchronizationContext.Current;
        if (_uiContext != null)
        {
            _autoLoginManager.SetUiContext(_uiContext);
            FileLogger.Info($"TrayManager: UI sync context installed ({_uiContext.GetType().Name})");
        }
        else
        {
            FileLogger.Warn("TrayManager: SynchronizationContext.Current still null after install attempt — events may race");
        }

        // Assign context menu only on right-click, remove on left/middle to prevent
        // Windows from showing the menu and stealing focus from click detection
        _trayIcon.MouseDown += (_, e) =>
        {
            _trayIcon.ContextMenuStrip = e.Button == MouseButtons.Right ? _contextMenu : null;
        };
        // Count all clicks via MouseUp — it fires after EVERY click including
        // when Windows converts the 2nd press into WM_xBUTTONDBLCLK (which skips
        // MouseDown entirely, losing the click). MouseUp always fires.
        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _leftClickCount++;
                FileLogger.Info($"TrayClick: left click #{_leftClickCount}");
                EnsureTimer(ref _leftClickTimer, LeftClickResolveMs, OnLeftResolved);
            }
            else if (e.Button == MouseButtons.Middle)
            {
                _middleClickCount++;
                FileLogger.Info($"TrayClick: middle click #{_middleClickCount}");
                EnsureTimer(ref _middleClickTimer, MiddleClickResolveMs, OnMiddleResolved);
            }
        };

        // Listen for TaskbarCreated to recover tray icon after explorer.exe restarts.
        // Re-promote after re-show so a fresh Explorer doesn't exile us to overflow.
        // Capture a fresh baseline first; Explorer's per-icon registry cache got
        // nuked by the restart so subkeys may transit the orphan state again.
        _taskbarMessageWindow = new TaskbarMessageWindow(() =>
        {
            if (_trayIcon != null)
            {
                var recoveryBaseline = TrayIconPromoter.CaptureBaseline();
                _trayIcon.Visible = false;
                _trayIcon.Visible = true;
                FileLogger.Info("Explorer restarted — tray icon re-registered");
                StartTrayIconPromotion(recoveryBaseline);
            }
        });

        BuildContextMenu();

        _processManager.ClientListChanged += (_, _) =>
        {
            UpdateClientMenu();
            UpdateTrayText();
            _keyboardHook.UpdateFilteredPids(_processManager.Clients.Select(c => c.ProcessId));

            // Start/stop slim titlebar position guard based on client count.
            // v3.22.47: guard interval is 500 ms always, regardless of injection
            // state. Pre-v3.22.47 used 5000 ms when the hook DLL was injected,
            // on the assumption that the in-process hook would catch every
            // resize EQ tried. In practice, EQ's world-load DX device reset
            // triggers resizes via APIs the hook misses (style changes, direct
            // WM_SIZE dispatches), and the user would see a ~6 s delay before
            // the window snapped back to slim — bad UX, WinEQ2 doesn't have
            // this (Nate's 2026-05-25 report). The rect-compare guard inside
            // ApplySlimTitlebarToAll makes the loop a no-op when the hook DLL
            // kept things aligned, so 500 ms costs ~3 cross-process kernel
            // reads per client per tick = ~36 reads/sec for a 6-client team =
            // negligible. When the hook DOES miss, C# catches it within 500 ms.
            if (_config.Layout.SlimTitlebar && _processManager.Clients.Count > 0)
            {
                bool hookActive = _injectedPids.Count > 0;
                int guardInterval = 500;

                if (_slimTitlebarGuard == null)
                {
                    _slimTitlebarGuard = new System.Windows.Forms.Timer { Interval = guardInterval };
                    _slimTitlebarGuard.Tick += (_, _) => SlimTitlebarGuardTick();
                    // Don't start if a login is in progress — LoginComplete handler will resume it
                    if (!_processManager.Clients.Any(c => _autoLoginManager.IsLoginActive(c.ProcessId)))
                        _slimTitlebarGuard.Start();
                }
                else if (_slimTitlebarGuard.Interval != guardInterval)
                {
                    _slimTitlebarGuard.Interval = guardInterval;
                }
            }
            else if (_slimTitlebarGuard != null)
            {
                _slimTitlebarGuard.Stop();
                _slimTitlebarGuard.Dispose();
                _slimTitlebarGuard = null;
            }

            // Update shared memory config for all injected processes
            UpdateHookConfig();

            // Auto-show PiP overlay when enabled and 2+ clients are present
            if (_config.Pip.Enabled && _processManager.Clients.Count >= 2
                && (_pipOverlay == null || _pipOverlay.IsDisposed))
            {
                TogglePip();
            }
            // Update PiP sources when client list changes
            else if (_pipOverlay != null && !_pipOverlay.IsDisposed)
            {
                _pipOverlay.UpdateSources(_processManager.Clients, _processManager.GetActiveClient());
            }
        };
        _processManager.ClientDiscovered += (_, c) =>
        {
            // Stamp the bound character/account name from the autologin launch
            // record — the hook-config and window-title paths prefer this over
            // positional LegacyAccounts[i] indexing for accurate team-slot rendering.
            if (AutoLoginManager.TryGetBoundName(c.ProcessId, out var bound))
                c.BoundCharacterName = bound;

            // v3.22.61: post eqgame.exe's embedded icon to the discovered window
            // so Windows has something to render in the taskbar + alt-tab +
            // window-menu surfaces. See _eqgameWindowIcon field comment for why.
            ApplyEqgameWindowIcon(c.WindowHandle);

            // v3.22.20: assign initial monitor slot if unmapped. v3.22.21:
            // free-slot scan (was `_monitorSlotByPid.Count`) — fixes the
            // PID-recycle dupe-slot bug. AssignNextFreeSlot handles the
            // overflow case + warn-log. Lifecycle removal happens in ClientLost.
            if (!_monitorSlotByPid.ContainsKey(c.ProcessId))
                AssignNextFreeSlot(c.ProcessId, "initial assignment");

            // NO tooltip here — creating TopMost windows during EQ's DirectX init
            // causes the game to lose foreground and minimize itself
            _affinityManager.ScheduleRetry(c);

            // v3.22.55: split the prior unconditional autologin-skip into two
            // tiers. Pre-v3.22.55 the entire block (slim-titlebar style+pos,
            // hook-config refresh, and the topmost dance) early-returned when
            // autologin was active — leaving the login-screen window sized to
            // the slim outer rect's visible-client values (eqclient.ini per
            // v3.22.45/46) but with NORMAL titlebar non-client adornments, so
            // the user saw 7+ seconds of "big gap right, slightly off-screen
            // left" until LoginCredentialsSent (T+~7s) → ApplyDeferredCosmetics
            // re-applied slim. Nate's 2026-05-26 right-click → Launch client
            // screenshot.
            //
            // Slim-titlebar (SetWindowLongPtr style + SetWindowPos position
            // with SWP_NOACTIVATE) is safe during autologin: v3.5.0's 3-layer
            // focus defense (inline GetForegroundWindow spoof + WndProc
            // subclass blocking WM_KILLFOCUS/WM_ACTIVATEAPP(FALSE) + activation
            // blast) catches any focus loss the style change might trigger, and
            // ApplyDeferredCosmetics already runs the same code path at T+~7s
            // mid-credential-typing without issues. The topmost dance +
            // SwitchToClient foreground transfer ARE the parts that disturb
            // DirectInput cooperative level handoff (z-band reorder while
            // dinput8 is mid-cred-input → spoof can race), so those still
            // defer to ApplyDeferredCosmetics's gated path.
            bool autologinActive = _autoLoginManager.IsLoginActive(c.ProcessId);

            // Apply slim titlebar immediately so the window covers the taskbar
            // from the moment it's discovered — don't wait for the guard timer.
            // v3.22.20 fix: in multi-monitor mode, run the full arrange so the
            // second client lands on the secondary monitor right away instead
            // of being slammed onto primary by GetTargetMonitorBounds().
            if (_config.Layout.SlimTitlebar)
            {
                bool isMM = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
                if (isMM)
                {
                    _windowManager.ArrangeWindows(_processManager.Clients, _monitorSlotByPid);
                }
                else
                {
                    _windowManager.ApplySlimTitlebar(
                        c.WindowHandle,
                        _windowManager.GetTargetMonitorBounds(),
                        _config.Layout.TitlebarOffset);
                }

                // v3.22.40: taskbar-coverage parity for the manual-launch path.
                // The autologin path is covered by ApplyDeferredCosmetics
                // (v3.22.38/39); a manually-launched eqgame.exe discovered by
                // ProcessManager's poll runs through here and previously got
                // the slim bounds without the z-order recovery dance.
                //
                // v3.22.46: same dedupe + active-EQ-other-PID skip as
                // ApplyDeferredCosmetics — manual launch is one of the two
                // first-arrival paths, so a manual launch followed by an
                // autologin LoginComplete for the same PID shouldn't fire two
                // dances. _taskbarRaisedAfterLoginPids is checked here too.
                //
                // v3.22.55: skip when autologin is active — ApplyDeferredCosmetics
                // at LoginCredentialsSent (T+~7s) owns the dance for the autologin
                // path, and running it here too would either (a) fire the dance
                // mid-credential-typing (DI cooperative-level handoff risk) or
                // (b) burn the PID's TryClaimTaskbarRaise dedupe slot so the
                // ApplyDeferredCosmetics fire silently no-ops.
                if (autologinActive)
                {
                    FileLogger.Info($"ClientDiscovered: PID {c.ProcessId} autologin active — slim-titlebar applied, taskbar dance deferred to ApplyDeferredCosmetics (v3.22.55)");
                }
                else
                {
                    var activeClient = _processManager.GetActiveClient();
                    bool eqAlreadyForeground = activeClient != null;
                    bool foregroundIsDifferentEQ = activeClient != null && activeClient.ProcessId != c.ProcessId;

                    if (foregroundIsDifferentEQ)
                    {
                        FileLogger.Info($"ClientDiscovered manual-launch: {c} background-discovered while {activeClient} is foreground — skipping RaiseClientsAboveTaskbar (v3.22.46)");
                    }
                    else if (!TryClaimTaskbarRaise(c.ProcessId))
                    {
                        FileLogger.Info($"ClientDiscovered manual-launch: PID {c.ProcessId} already raised once this session — skipping repeat dance (v3.22.46 dedupe)");
                    }
                    else
                    {
                        RaiseClientsAboveTaskbar(_processManager.Clients, foregroundActive: eqAlreadyForeground);
                    }
                }
            }

            // v3.22.55: the rest of this handler (hook-config refresh, manual
            // hook injection for externally-launched eqgame, fallback title
            // setter) was previously gated by the unconditional autologin
            // early-return.
            //
            // v3.22.56 (post-v3.22.55 verifier swarm CRITICAL — T2 Sonnet +
            // T2 Opus convergent REJECT): the bulk wrapper `UpdateHookConfig()`
            // (no-arg) has an internal IsLoginActive gate that filters autologin
            // PIDs out of its iteration — but the call site BELOW invokes
            // `UpdateHookConfigForPid(pid)` DIRECTLY (per-PID worker, no internal
            // gate). v3.22.55's CHANGELOG + the pre-v3.22.56 version of this
            // comment falsely claimed the call was "internally gated" — it was
            // not. Pre-v3.22.55 the call was unreachable for autologin clients
            // via the early-return; v3.22.55 removed that and (incorrectly)
            // relied on a non-existent internal gate. The smoke for v3.22.55
            // didn't trigger an observable race (shared-memory writes are fast
            // and the eqswitch-hook.dll vs eqswitch-di8.dll surfaces are
            // independent memory-mapped files for different hook classes), but
            // the unguarded call site at T+~1.5 s during DI cooperative-level
            // handoff has no proven-safe history — restore the gate explicitly
            // to match the original pre-v3.22.55 behavior for this specific
            // call. The InjectHookDll branch below still only fires for
            // externally-launched eqgame (not in `_injectedPids`), which the
            // autologin path doesn't produce, so that conditional needs no
            // autologin gate.
            //
            // Note on line-number citations: by-symbol refs are used instead of
            // by-line refs (file growth shifts numbers). See `UpdateHookConfig()`
            // and `UpdateHookConfigForPid(int)` declarations later in this file.

            // For EQSwitch-launched clients, both DLLs are already injected pre-resume.
            // For manually-launched clients (detected by ProcessManager poll), inject
            // eqswitch-hook.dll only — DirectInput hooking requires pre-resume injection.
            if (_injectedPids.Contains(c.ProcessId))
            {
                // v3.22.56: skip refresh when autologin is active. Matches
                // pre-v3.22.55 behavior for this specific call (the only call
                // path that was actually proven safe). `UpdateHookConfigForPid`
                // (the per-PID worker, by symbol) has no internal IsLoginActive
                // gate — only the bulk `UpdateHookConfig()` wrapper does, and
                // we don't call the wrapper here.
                if (autologinActive)
                {
                    FileLogger.Info($"ClientDiscovered: PID {c.ProcessId} autologin active — UpdateHookConfigForPid deferred (gates DI cooperative-level handoff at T+~1.5s; ApplyDeferredCosmetics at T+~7s will refresh) (v3.22.56)");
                }
                else
                {
                    // Already injected pre-resume — just refresh config
                    UpdateHookConfigForPid(c.ProcessId);
                }
            }
            else if (ShouldInjectHook())
            {
                // Manually-launched EQ — inject window management hooks only
                FileLogger.Info($"ClientDiscovered: PID {c.ProcessId} not launched by EQSwitch — injecting hook DLL only (no DirectInput)");
                InjectHookDll(c.ProcessId);
            }
            // If hook not injected, apply title externally as fallback
            else if (!string.IsNullOrEmpty(_config.Layout.WindowTitleTemplate))
            {
                _windowManager.SetWindowTitle(c, c.SlotIndex);
            }
        };
        _processManager.ClientLost += (_, c) =>
        {
            // v3.22.72: queue + coalesce instead of firing immediately. See
            // _pendingLostClients field-block above for rationale. Replaces
            // the v3.22.46-era "_contextMenu?.Visible != true" guard which
            // only caught the case where menu was open at event-fire time —
            // ProcessManager polls at 10s, so the common race was "client
            // closed → poll → ClientLost handler runs → balloon queued via
            // DeferToNextTick → user opens menu → tooltip materializes →
            // menu cancels". Coalescing handles the "2 clients exit at the
            // same poll" case; Closed-handler drain handles the "menu
            // opened between queue and dispatch" case.
            QueueLostClientBalloon(c.ToString() ?? "?");
            _affinityManager.CancelRetry(c.ProcessId);

            // Clean up injection tracking and per-process shared memory
            _injectedPids.Remove(c.ProcessId);
            _di8InjectedPids.Remove(c.ProcessId);
            _hookConfig?.Close(c.ProcessId);
            // v3.22.46: drop the dedupe entry so a recycled PID (or a fresh
            // launch of the same character later in the session) gets its
            // first-arrival dance again. The set is purely a per-session,
            // per-PID one-shot — clearing on PID death is the correct
            // lifecycle hook. Thread-safe wrapper guards against the bare-
            // fallback event paths in AutoLoginManager racing this Remove.
            DropTaskbarRaiseClaim(c.ProcessId);
            // v3.22.44 r3.5 (R3-T2-Sonnet Finding B MEDIUM): refresh the
            // Detach Hooks menu Enabled state. Round-3 added the refresh on
            // the Add sites but missed this Remove site, leaving the item
            // stuck enabled with the stale "Removes the EQSwitch hooks..."
            // tooltip after the last client exits — until ProcessManager's
            // next 10s ClientListChanged tick.
            RefreshDetachMenuState();

            // v3.22.20: drop monitor-slot binding so a recycled PID doesn't
            // inherit a stale slot. New PIDs re-enter via ClientDiscovered's
            // sequential assignment.
            _monitorSlotByPid.Remove(c.ProcessId);

            // Drop the PID→name binding so a recycled PID doesn't inherit a stale name.
            AutoLoginManager.ClearBoundName(c.ProcessId);

            // v3.22.32: recover taskbar coverage on the remaining clients.
            // When the closing window was sized over the taskbar (slim mode),
            // Windows raises the WS_EX_TOPMOST taskbar back above the remaining
            // sibling's z-band as soon as our window disappears. The sibling's
            // bounds are still right but the taskbar visually slices through
            // its bottom edge. SetWindowPos with SWP_NOZORDER (the only thing
            // the slim-titlebar guard timer + hook DLL use) can't undo this.
            // Schedule a delayed re-arrange so the OS finishes its post-close
            // z-order shuffle first; 250ms is empirically enough on Win11.
            // Filter out autologin-active and hung clients, mirroring
            // OnArrangeWindows' gates so we don't disturb a mid-credential-
            // type sequence on a sibling.
            if (_config.Layout.SlimTitlebar
                && _processManager.Clients.Count > 0)
            {
                var recoveryTimer = new System.Windows.Forms.Timer { Interval = 250 };
                recoveryTimer.Tick += (_, _) =>
                {
                    recoveryTimer.Stop();
                    recoveryTimer.Dispose();
                    // v3.22.33 (T2 Opus Gap 5 MEDIUM): bail out if TrayManager
                    // is mid-Dispose. Without this guard the tick reads
                    // _processManager / _windowManager which were already
                    // disposed → NRE → swallowed by the catch below as a
                    // misleading "recovery failed" Error log line.
                    if (_disposed) return;
                    try { RaiseRemainingClientsAboveTaskbar(); }
                    catch (Exception ex)
                    {
                        FileLogger.Error("ClientLost: post-close taskbar-recovery failed", ex);
                    }
                };
                recoveryTimer.Start();
            }
        };

        // Immediately detect newly launched clients so slim titlebar applies without
        // waiting for the 10s poll interval. Short delay lets EQ create its window first.
        _launchManager.ClientLaunched += (_, pid) =>
        {
            var detectTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            detectTimer.Tick += (_, _) =>
            {
                detectTimer.Stop();
                detectTimer.Dispose();
                _processManager.RefreshClients();
            };
            detectTimer.Start();

            // EULA-dismiss happens NATIVE-side now: eqswitch-di8.cpp's polling
            // tick calls EQMainWidgetsMQ2::FindChildByName('EulaWindow',
            // 'EULA_AcceptButton') + MQ2Bridge::ClickButton — direct widget
            // click via WndNotification(XWM_LCLICK), gated on gameState != 5.
            // No keystroke involvement (Dalaya EULA defaults focus to DECLINE,
            // confirmed 2026-05-08 — VK_RETURN keystroke closes the game).
        };

        // No tooltip for launch progress — TopMost windows during EQ init cause minimize
        _launchManager.ProgressUpdate += (_, msg) => FileLogger.Info($"LaunchProgress: {msg}");
        _launchManager.LaunchSequenceComplete += (_, _) =>
        {
            // Only auto-arrange in multimonitor mode after launch —
            // single screen lets EQ use its own eqclient.ini positioning
            if (_config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase))
            {
                FileLogger.Info("Multi-Monitor mode — arranging after delay...");
                var fixDelay = Math.Max(_config.Launch.FixDelayMs, 5000);
                var arrangeTimer = new System.Windows.Forms.Timer { Interval = fixDelay };
                arrangeTimer.Tick += (_, _) =>
                {
                    arrangeTimer.Stop();
                    arrangeTimer.Dispose();
                    var clients = _processManager.Clients;
                    if (clients.Count > 0)
                        _windowManager.ArrangeWindows(clients, _monitorSlotByPid);
                };
                arrangeTimer.Start();
            }
        };

        _processManager.StartPolling();
        _processManager.RefreshClients(); // Populate PIDs immediately so keyboard hook filters work
        RegisterHotkeys();
        StartForegroundHook();
        StartRetryTimer();
        StartupManager.MigrateFromRegistry();
        StartupManager.ValidateStartupPath(_config);

        // Log core detection at startup
        var (cores, sysMask) = AffinityManager.DetectCores();
        FileLogger.Info($"Startup: {cores} cores detected, system mask 0x{sysMask:X}");

        FileLogger.Info("EQSwitch started. Watching for EQ clients...");
    }

    /// <summary>
    /// Opens Settings form after a short delay. Called from Program.cs on first run.
    /// </summary>
    public void OpenSettingsAfterDelay(int delayMs = 1500)
    {
        var timer = new System.Windows.Forms.Timer { Interval = delayMs };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            ShowSettings();
        };
        timer.Start();
    }

    // ─── Hotkey Registration ─────────────────────────────────────────

    private void RegisterHotkeys()
    {
        var hk = _config.Hotkeys;
        int registered = 0;
        int failed = 0;
        var failedKeys = new List<string>();

        // Helper: register and track failures
        bool TryRegister(string key, Action callback, string label)
        {
            if (string.IsNullOrEmpty(key)) return false; // not configured
            int id = _hotkeyManager.Register(key, callback);
            if (id > 0) { registered++; return true; }
            failed++;
            failedKeys.Add($"{label} ({key})");
            FileLogger.Warn($"RegisterHotKey FAILED for {label}: '{key}' — may be in use by another app");
            return false;
        }

        // Alt+1 through Alt+6 — direct switch to client by slot
        for (int i = 0; i < hk.DirectSwitchKeys.Count; i++)
        {
            int slot = i; // capture for closure
            TryRegister(hk.DirectSwitchKeys[i], () => OnDirectSwitch(slot), $"Slot{i + 1}");
        }

        TryRegister(hk.ArrangeWindows, OnArrangeWindows, "FixWindows");
        TryRegister(hk.ToggleMultiMonitor, OnToggleMultiMonitor, "MultiMon");
        TryRegister(hk.ShowMenu, OnShowTrayMenu, "ShowMenu");
        TryRegister(hk.LaunchOne, OnLaunchOne, "LaunchOne");
        TryRegister(hk.LaunchAll, () => ExecuteTrayAction("LaunchAll"), "LaunchAll");
        TryRegister(hk.TogglePip, () => ExecuteTrayAction("TogglePiP"), "TogglePiP");
        TryRegister(hk.AutoLogin1, () => ExecuteTrayAction("AutoLogin1"), "AutoLogin1");
        TryRegister(hk.AutoLogin2, () => ExecuteTrayAction("AutoLogin2"), "AutoLogin2");
        TryRegister(hk.AutoLogin3, () => ExecuteTrayAction("AutoLogin3"), "AutoLogin3");
        TryRegister(hk.AutoLogin4, () => ExecuteTrayAction("AutoLogin4"), "AutoLogin4");
        TryRegister(hk.TeamLogin1, () => ExecuteTrayAction("LoginAll"), "TeamLogin1");
        TryRegister(hk.TeamLogin2, () => ExecuteTrayAction("LoginAll2"), "TeamLogin2");
        TryRegister(hk.TeamLogin3, () => ExecuteTrayAction("LoginAll3"), "TeamLogin3");
        TryRegister(hk.TeamLogin4, () => ExecuteTrayAction("LoginAll4"), "TeamLogin4");

        // Phase 5a: family-table dispatch. AccountHotkeys -> LoginToCharselect (via
        // FireAccountLogin), CharacterHotkeys -> LoginAndEnterWorld (via FireCharacterLogin).
        // Skip padding entries (empty Combo OR empty TargetName — Phase 1 migration contract).
        // Name-based registration reuses the existing HotkeyManager.Register API.
        foreach (var binding in hk.AccountHotkeys)
        {
            if (!HotkeyBindingUtil.IsPopulated(binding)) continue;
            var capturedName = binding.TargetName;
            TryRegister(binding.Combo,
                () => FireAccountHotkeyByName(capturedName),
                $"AccountHK:{capturedName}");
        }

        foreach (var binding in hk.CharacterHotkeys)
        {
            if (!HotkeyBindingUtil.IsPopulated(binding)) continue;
            var capturedName = binding.TargetName;
            TryRegister(binding.Combo,
                () => FireCharacterHotkeyByName(capturedName),
                $"CharacterHK:{capturedName}");
        }

        FileLogger.Info($"RegisterHotKey: {registered} registered, {failed} failed" +
            (failedKeys.Count > 0 ? $" [{string.Join(", ", failedKeys)}]" : ""));
        if (failedKeys.Count > 0)
            ShowWarning($"Hotkey conflict: {string.Join(", ", failedKeys)}\nAnother app may be using these keys.");

        // Low-level keyboard hook for single-key hotkeys
        if (_keyboardHook.Install())
        {
            // Switch Key (default '\') — fires ONLY when an EQ client window
            // is foregrounded. Lets the user type '\' freely in chat/Discord/
            // browsers without it being swallowed. The "from any app" key is
            // GlobalSwitchKey (']') below.
            if (!string.IsNullOrEmpty(hk.SwitchKey))
            {
                uint vk = HotkeyManager.ResolveVK(hk.SwitchKey);
                if (vk != 0)
                {
                    _keyboardHook.Register(vk, OnSwitchKey, processFilter: "eqgame", requireClients: true);
                    FileLogger.Info($"Hook: SwitchKey '{hk.SwitchKey}' (VK 0x{vk:X2}) — EQ-foreground only");
                }
            }

            // Global Switch Key (default ']') — works from any app, but only when EQ clients exist
            if (!string.IsNullOrEmpty(hk.GlobalSwitchKey))
            {
                uint vk = HotkeyManager.ResolveVK(hk.GlobalSwitchKey);
                if (vk != 0)
                {
                    _keyboardHook.Register(vk, OnGlobalSwitchKey, requireClients: true);
                    FileLogger.Info($"Hook: GlobalSwitchKey '{hk.GlobalSwitchKey}' (VK 0x{vk:X2}) — global (requires clients)");
                }
            }
        }
        else
        {
            FileLogger.Warn("Keyboard hook install failed — SwitchKey and GlobalSwitchKey disabled");
        }
    }

    // ─── Hotkey Callbacks ────────────────────────────────────────────

    /// <summary>
    /// Alt+N: Switch directly to client in slot N.
    /// </summary>
    private void OnDirectSwitch(int slot)
    {
        var client = _processManager.GetClientBySlot(slot);
        if (client != null)
        {
            _windowManager.SwitchToClient(client, _autoLoginManager.IsLoginActive);
            FileLogger.Info($"Direct switch to slot {slot + 1}: {client}");
        }
        else
        {
            FileLogger.Info($"Direct switch: no client in slot {slot + 1}");
        }
    }

    /// <summary>
    /// Switch Key ('\'):
    /// - If EQ is focused: swap between last two clients (default) or cycle all.
    /// - If EQ is NOT focused: bring the first EQ client to front (cold-start path).
    /// Fires whenever EQ clients exist (no process filter — see registration for rationale).
    /// </summary>
    private void OnSwitchKey()
    {
        // v3.22.59: entry-point diagnostic for phantom-swap investigation
        // (2026-05-27 smoke: `\` swap fired ~8s after last keypress). Pair with
        // KeyboardHook POSTED/INVOKED logs to identify the trigger source.
        FileLogger.Info("OnSwitchKey: ENTRY");
        var current = _processManager.GetActiveClient();
        var clients = _processManager.Clients;
        if (clients.Count == 0)
        {
            FileLogger.Info("SwitchKey: no clients, nothing to do");
            return;
        }

        // Defensive: with the EQ-foreground process filter on '\', this branch
        // should be unreachable in steady state. Kept in case the cached PID
        // set hasn't caught up to a brand-new client yet.
        if (current == null)
        {
            var first = clients[0];
            _windowManager.SwitchToClient(first, _autoLoginManager.IsLoginActive);
            FileLogger.Info($"SwitchKey: no EQ focused — focused {first}");
            return;
        }

        if (clients.Count < 2)
        {
            FileLogger.Info("SwitchKey: only 1 client, nothing to swap");
            return;
        }

        // In multimonitor mode, swap positions then resize to fit new monitors
        bool isMultiMon = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        if (isMultiMon)
        {
            try
            {
                // v3.22.20: rotate per-PID monitor slot assignments, then
                // re-arrange + refresh hook config. Previously called
                // SwapWindows + ResizeToCurrentMonitors + UpdateHookConfig,
                // which fought itself: UpdateHookConfig re-targeted PIDs by
                // unchanged clientIndex, so the hook DLL dragged windows
                // back to their original monitors — visible as the
                // bouncing-stacked pattern in the 2026-05-19 smoke log.
                //
                // v3.22.21 smoke-2 (Nate 2026-05-20): added phase timing for
                // taskbar-flicker diagnosis. Captures wall-clock between
                // rotate / arrange / hook-config so v3.22.22 can isolate
                // which phase loses primary-monitor coverage during the
                // cross-process cross-monitor swap. AHK reference at
                // _.src/.oursrcarchive/eqswitch_ahk/EQSwitch.ahk:414-441
                // uses sequential WinMove (no DeferWindowPos batching);
                // worth A/B testing in v3.22.22.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                RotateMonitorSlots();
                long tRotate = sw.ElapsedMilliseconds;
                _windowManager.ArrangeWindows(clients, _monitorSlotByPid);
                long tArrange = sw.ElapsedMilliseconds;
                UpdateHookConfig();
                long tHook = sw.ElapsedMilliseconds;
                FileLogger.Info($"SwitchKey-swap-timing: rotate={tRotate}ms, arrange={tArrange - tRotate}ms, hookConfig={tHook - tArrange}ms, total={tHook}ms");
            }
            catch (Exception ex)
            {
                FileLogger.Error("SwitchKey: multimonitor slot rotation failed", ex);
            }
        }

        if (_config.Hotkeys.SwitchKeyMode == "swapLast")
        {
            // Alt+Tab style: swap to the previous active client (matched by PID — handles can change)
            if (_previousActivePid != 0)
            {
                EQClient? target = null;
                for (int i = 0; i < clients.Count; i++)
                {
                    if (clients[i].ProcessId == _previousActivePid) { target = clients[i]; break; }
                }
                if (target != null)
                {
                    _windowManager.SwitchToClient(target, _autoLoginManager.IsLoginActive);
                    FileLogger.Info($"SwitchKey: {(isMultiMon ? "swapped positions + " : "")}swapped to last active {target}");
                    return;
                }
            }
            // Fallback to cycle if no previous client tracked
            var next = _windowManager.CycleNext(clients, current, _autoLoginManager.IsLoginActive);
            if (next != null)
                FileLogger.Info($"SwitchKey: {(isMultiMon ? "swapped positions + " : "")}cycled (no previous tracked) to {next}");
        }
        else
        {
            // Cycle through all clients round-robin
            var next = _windowManager.CycleNext(clients, current, _autoLoginManager.IsLoginActive);
            if (next != null)
                FileLogger.Info($"SwitchKey: {(isMultiMon ? "swapped positions + " : "")}cycled to {next}");
        }
    }

    /// <summary>
    /// Global Switch Key (']'):
    /// - If EQ is focused: cycle to next client
    /// - If EQ is NOT focused: bring the first EQ client to front
    /// </summary>
    private void OnGlobalSwitchKey()
    {
        // v3.22.59: entry-point diagnostic — see OnSwitchKey for context.
        FileLogger.Info("OnGlobalSwitchKey: ENTRY");
        var current = _processManager.GetActiveClient();
        var clients = _processManager.Clients;

        if (clients.Count == 0)
        {
            FileLogger.Info("GlobalSwitchKey: no clients detected");
            return;
        }

        // In multimonitor mode, swap positions then resize to fit new monitors
        bool isMultiMon = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        if (isMultiMon && clients.Count >= 2)
        {
            try
            {
                // v3.22.20: see OnSwitchKey comment — real slot rotation
                // instead of physical-swap-then-clientIndex-revert.
                // v3.22.21 smoke-2: phase timing (mirrors OnSwitchKey).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                RotateMonitorSlots();
                long tRotate = sw.ElapsedMilliseconds;
                _windowManager.ArrangeWindows(clients, _monitorSlotByPid);
                long tArrange = sw.ElapsedMilliseconds;
                UpdateHookConfig();
                long tHook = sw.ElapsedMilliseconds;
                FileLogger.Info($"GlobalSwitchKey-swap-timing: rotate={tRotate}ms, arrange={tArrange - tRotate}ms, hookConfig={tHook - tArrange}ms, total={tHook}ms");
            }
            catch (Exception ex)
            {
                FileLogger.Error("GlobalSwitchKey: multimonitor slot rotation failed", ex);
            }
        }

        if (current != null)
        {
            // EQ is focused — cycle to next
            var next = _windowManager.CycleNext(clients, current, _autoLoginManager.IsLoginActive);
            if (next != null)
                FileLogger.Info($"GlobalSwitchKey: {(isMultiMon ? "swapped positions + " : "")}cycled to {next}");
        }
        else
        {
            // EQ is NOT focused — bring first client to front
            var first = clients[0];
            _windowManager.SwitchToClient(first, _autoLoginManager.IsLoginActive);
            FileLogger.Info($"GlobalSwitchKey: focused {first}");
        }
    }

    /// <summary>
    /// Arrange all EQ windows (Fix Windows). Hotkey configurable in Settings.
    /// <para>
    /// v3.22.21 (refined post-verifier-round-2): clients mid-autologin are
    /// skipped from the entire arrange path — ArrangeWindows, UpdateHookConfig
    /// (per-PID), AND ForceDxReinit. Matches the existing `ClientDiscovered`
    /// gate at L346 ("SetWindowPos/SetWindowLongPtr/CreateRemoteThread can
    /// interfere with the DirectInput login sequence"). Window arrangement
    /// for autologin clients fires automatically via `ApplyDeferredCosmetics`
    /// on `LoginCredentialsSent` (T+~7s) — user-triggered Fix Windows during
    /// that window respects the same boundary.
    /// </para>
    /// <para>
    /// v3.22.21: also fires <see cref="ForceDxReinit"/> on every non-autologin,
    /// non-iconic client. Recovery hatch for cross-monitor smooshed windows
    /// (DX swap-chain stuck at the wrong monitor's resolution).
    /// </para>
    /// </summary>
    private void OnArrangeWindows()
    {
        var allClients = _processManager.Clients;
        if (allClients.Count == 0)
        {
            FileLogger.Info("ArrangeWindows: no clients to arrange");
            ShowBalloon("No EQ clients running");
            return;
        }

        // v3.22.21 round-2 (T3-Opus HIGH): filter out clients mid-autologin
        // from the entire arrange path. Per-client gate, not all-or-nothing —
        // user might fire Fix Windows on a multi-client config where some
        // are in-world and one is still logging in.
        var clientsToArrange = new List<EQClient>(allClients.Count);
        int skippedAutologin = 0;
        foreach (var c in allClients)
        {
            if (_autoLoginManager.IsLoginActive(c.ProcessId))
            {
                FileLogger.Info($"ArrangeWindows: skipping PID {c.ProcessId} entirely — autologin in progress");
                skippedAutologin++;
                continue;
            }
            clientsToArrange.Add(c);
        }

        if (clientsToArrange.Count == 0)
        {
            FileLogger.Info($"ArrangeWindows: all {allClients.Count} client(s) mid-autologin, nothing to arrange");
            ShowBalloon($"Fix Windows skipped — autologin still in progress. Retry once login completes.");
            return;
        }

        // v3.22.44 r3 (4-way HIGH convergent: T2-Sonnet Gap G + T2-Opus
        // Finding 1 + T4-Sonnet Item 1 + T4-Opus F1): capture iconic-skip
        // count so the user-facing balloon below reports actual arranged
        // count rather than the over-counted attempted total. Round-2 added
        // IsIconic skips inside ArrangeMultiMonitor / ArrangeSingleScreen
        // but the void return type left OnArrangeWindows blind to them →
        // balloon said "Fixed 2 windows" when 1 was iconic and untouched.
        // v3.22.44 r3.5 (R3-T3-Opus F1 HIGH / R3-T3-Sonnet C2 / R3-T2-Opus A1
        // 3-way convergent): also capture non-iconic skips (IsWindow=false,
        // IsHungAppWindow, IsClientResponsive=false) so the balloon's
        // "Fixed N" arithmetic is correct in those cases too. Round-3
        // reintroduced the same overcounting bug shape for the non-iconic
        // silent skips.
        var skips = _windowManager.ArrangeWindows(clientsToArrange, _monitorSlotByPid);
        int skippedIconic = skips.Iconic;
        int skippedOther = skips.Other;
        // UpdateHookConfig() iterates _injectedPids. Autologin clients ARE
        // in that set (added during CREATE_SUSPENDED PreResume, before
        // autologin starts) — the round-3 gate inside UpdateHookConfig
        // (TrayManager.cs:UpdateHookConfig) filters them via IsLoginActive
        // so we don't rewrite their hook-config shared memory while
        // credentials are being typed. We still call UpdateHookConfig
        // unconditionally here: the in-world clients still need their
        // configs refreshed to reflect the new slot map.
        UpdateHookConfig();

        // v3.22.32: explicit user-action Fix Window button path includes the
        // taskbar-coverage recovery dance. ArrangeWindows → ArrangeSingleScreen
        // → ApplySlimTitlebar sizes EQ to span the full monitor including the
        // taskbar's rect, but uses SWP_NOZORDER so z-order is unchanged. If
        // the taskbar (WS_EX_TOPMOST) was previously raised above EQ — common
        // after a sibling client closed or after Alt+Tab through Explorer —
        // Fix Window pre-fix would leave the taskbar visually slicing through
        // EQ's bottom edge despite the size being correct. The topmost dance
        // (HWND_TOPMOST → HWND_NOTOPMOST) pushes EQ above the taskbar's
        // z-band without leaving it permanently always-on-top, then
        // ForceForegroundWindow on the active client makes Windows commit to
        // the new foreground state. Skipped when slim is off (taskbar should
        // be visible) or in autologin (Fix Window already skipped those).
        if (_config.Layout.SlimTitlebar)
            RaiseClientsAboveTaskbar(clientsToArrange, foregroundActive: true);

        // v3.22.44 r3.5: extend skippedLabel to include BOTH iconic and other
        // non-iconic skip counts; arrange-count subtracts both so the balloon's
        // "Fixed N" reflects what actually moved.
        string skippedLabel = "";
        if (skippedAutologin > 0)
            skippedLabel += $" ({skippedAutologin} skipped — autologin active)";
        if (skippedIconic > 0)
            skippedLabel += $" ({skippedIconic} minimized — restore manually)";
        if (skippedOther > 0)
            skippedLabel += $" ({skippedOther} unresponsive — transient)";
        int arranged = clientsToArrange.Count - skippedIconic - skippedOther;
        FileLogger.Info($"ArrangeWindows: arranged {arranged}/{allClients.Count} client(s){skippedLabel}");

        // v3.22.21: force DX swap-chain reinit. PostMessage(WM_NCLBUTTONDBLCLK,
        // HTCAPTION) hits the same WndProc path as a real user titlebar
        // double-click → CResolutionHandler::ToggleScreenMode → DX device reset.
        // ForceDxReinit itself re-gates on IsLoginActive + IsIconic before the
        // second click (T2-Opus + T3-Sonnet round-2 convergence) — defense in
        // depth against state changes during the 250ms inter-click window.
        foreach (var c in clientsToArrange)
            ForceDxReinit(c);

        string mode = _config.Layout.SlimTitlebar ? "slim titlebar" : "stacked";
        ShowBalloon($"Fixed {arranged} window(s) ({mode}){skippedLabel}");
    }

    /// <summary>
    /// v3.22.21: force a DirectX swap-chain re-init on <paramref name="client"/>
    /// by posting WM_NCLBUTTONDBLCLK(HTCAPTION) twice with ~250ms between.
    /// Same code path as the user manually double-clicking the titlebar →
    /// EQ WndProc → CResolutionHandler::ToggleScreenMode (windowed-fullscreen
    /// ↔ windowed). Fixes the cross-monitor smoosh symptom (font/UI distortion
    /// when SwitchKey moves a client to a differently-sized monitor and the
    /// DX backbuffer doesn't resize).
    /// <para>
    /// Skips hung windows (IsHungAppWindow). Uses WinForms Timer for the
    /// inter-message delay so the UI thread never blocks. The second
    /// post-message re-checks IsWindow/IsHungAppWindow before firing —
    /// covers the case where the client crashes between posts.
    /// </para>
    /// <para>
    /// Architectural note: PostMessage of the NC double-click is functionally
    /// equivalent to a direct call into CResolutionHandler::ToggleScreenMode
    /// (planned for v3.22.22 via Ghidra-confirmed RVA + Native detour). The
    /// PostMessage variant routes through WndProc indirection which adds a
    /// small amount of latency but is reliable today without Native-side work.
    /// </para>
    /// </summary>
    private void ForceDxReinit(EQClient client)
    {
        var hwnd = client.WindowHandle;
        var pid = client.ProcessId;
        if (!NativeMethods.IsWindow(hwnd))
        {
            FileLogger.Info($"ForceDxReinit: window gone for PID {pid}, skipping");
            return;
        }
        if (NativeMethods.IsHungAppWindow(hwnd))
        {
            FileLogger.Info($"ForceDxReinit: PID {pid} is hung, skipping");
            return;
        }
        // Iconic windows have no NC area to receive the double-click; the
        // PostMessage would be a no-op (silent failure). Skip with log so
        // the user knows the recovery hatch didn't reach a minimized client.
        // CAVEAT in log: don't recommend restoring blindly. Restoring a
        // minimized EQ window while it's in windowed-fullscreen state crashes
        // the DX device — the deferred minimize-crash issue. The safe path
        // is to enable Settings → Video → "Maximize on launch" (sets
        // EQClientIni.MaximizeWindow=true → hook blockMinimize=1) FIRST,
        // then the window won't have been allowed to minimize in the
        // first place. Don't tell the user "restore window first" without
        // that prerequisite — they'll crash the client.
        if (NativeMethods.IsIconic(hwnd))
        {
            // v3.22.21 round-3 (T3-Sonnet): WARNING-level message belongs at
            // Warn, not Info — log filters at Warn threshold would otherwise
            // drop the most actionable safety advice in the method.
            FileLogger.Warn($"ForceDxReinit: PID {pid} is minimized, skipping. Restoring a minimized EQ window may crash the DX device unless Settings → Video → \"Maximize on launch\" is enabled first.");
            return;
        }

        // Compose lParam from screen coords inside the window's titlebar area.
        // WM_NCLBUTTONDBLCLK's documented lParam encoding: low word = X, high
        // word = Y (both screen coordinates). EQ's WndProc doesn't actually
        // gate on the coords (HTCAPTION via wParam is the routing signal)
        // but we pass a coherent value so anything downstream that does check
        // sees a plausible titlebar click.
        NativeMethods.GetWindowRect(hwnd, out var rect);
        int x = rect.Left + 10;       // a few px inside the left edge
        int y = rect.Top + 5;          // a few px below the window's top edge
        IntPtr lParam = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        IntPtr wParam = (IntPtr)NativeMethods.HTCAPTION;

        bool ok1 = NativeMethods.PostMessage(hwnd, NativeMethods.WM_NCLBUTTONDBLCLK, wParam, lParam);
        FileLogger.Info($"ForceDxReinit: PID {pid} click 1 posted at ({x},{y}) ok={ok1}");

        // Second click 250ms later — flips ScreenMode back so EQ ends in its
        // original windowed state with a freshly initialized swap chain.
        // WinForms Timer marshals the Tick to the UI thread (no Thread.Sleep,
        // no UI freeze, no race against other input handlers).
        //
        // v3.22.21 round-2 (T2-Opus CRITICAL + T3-Sonnet MEDIUM convergence):
        // Re-check ALL FOUR gates (IsWindow + IsHungAppWindow + IsIconic +
        // IsLoginActive) before click 2. The pre-click-1 path checked all
        // four; the second click MUST mirror them or state changes during
        // the 250ms window (autologin start, window minimize, app shutdown)
        // can leak a stale PostMessage into a wrong-state window. The
        // CHANGELOG claim "second click re-checks before firing" depended
        // on this — pre-fix the claim was factually wrong (only 2 of 4
        // gates re-checked).
        var t = new System.Windows.Forms.Timer { Interval = 250 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            if (!NativeMethods.IsWindow(hwnd))
            {
                FileLogger.Info($"ForceDxReinit: PID {pid} click 2 skipped — window gone");
                return;
            }
            if (NativeMethods.IsHungAppWindow(hwnd))
            {
                FileLogger.Info($"ForceDxReinit: PID {pid} click 2 skipped — window hung");
                return;
            }
            if (NativeMethods.IsIconic(hwnd))
            {
                FileLogger.Info($"ForceDxReinit: PID {pid} click 2 skipped — window minimized between clicks");
                return;
            }
            if (_autoLoginManager.IsLoginActive(pid))
            {
                FileLogger.Info($"ForceDxReinit: PID {pid} click 2 skipped — autologin started between clicks");
                return;
            }
            bool ok2 = NativeMethods.PostMessage(hwnd, NativeMethods.WM_NCLBUTTONDBLCLK, wParam, lParam);
            FileLogger.Info($"ForceDxReinit: PID {pid} click 2 posted ok={ok2}");
        };
        t.Start();
    }

    /// <summary>
    /// v3.22.32: raise EQ client windows above the taskbar's z-band via the
    /// HWND_TOPMOST → HWND_NOTOPMOST dance. Recovers slim-titlebar coverage
    /// after the OS has popped the taskbar above EQ — typically after a
    /// sibling EQ client closed or after focus moved through Explorer.
    /// <para>
    /// Filters out hung windows, autologin-active clients, and unresponsive
    /// pumps (the same gates ArrangeWindows applies). Each surviving client
    /// gets two SetWindowPos calls with SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE:
    /// first to HWND_TOPMOST (forces above WS_EX_TOPMOST taskbar), then
    /// immediately back to HWND_NOTOPMOST (drops out of always-on-top so EQ
    /// doesn't cover unrelated topmost windows like overlays). The window
    /// ends up at the top of the non-topmost z-band — above the taskbar but
    /// below any user-pinned topmost apps. SWP_NOACTIVATE avoids focus theft
    /// during the dance itself.
    /// </para>
    /// <para>
    /// When <paramref name="foregroundActive"/> is true, the active client
    /// (per ProcessManager.GetActiveClient) is brought to foreground after
    /// the dance via ForceForegroundWindow. This is the load-bearing step
    /// for "taskbar yields" — Windows only auto-hides the taskbar for a
    /// genuinely-foreground full-monitor window. The Fix Window button path
    /// passes true (the user's explicit press is implicit consent to focus
    /// the EQ window); the ClientLost recovery path also passes true
    /// because the sibling-close already shifted focus.
    /// </para>
    /// </summary>
    private void RaiseClientsAboveTaskbar(IReadOnlyList<EQClient> clients, bool foregroundActive)
    {
        if (clients.Count == 0) return;
        const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        int raised = 0;
        int skippedUnresponsive = 0;
        int skippedIconic = 0;
        foreach (var c in clients)
        {
            var hwnd = c.WindowHandle;
            if (!NativeMethods.IsWindow(hwnd)) continue;
            if (NativeMethods.IsHungAppWindow(hwnd)) continue;
            if (_autoLoginManager.IsLoginActive(c.ProcessId)) continue;
            // v3.22.44 Gate #2 — DON'T touch iconic clients. The topmost dance
            // (TOPMOST↔NOTOPMOST) on a minimized EQ window is the suspect for
            // the "minimized client A crashes when sibling B camps" symptom.
            // Even with SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE, USER32 still
            // dispatches WM_WINDOWPOSCHANGING synchronously into EQ's
            // WndProc; on Dalaya the D3D9 device is released-on-minimize, so
            // the WndProc's downstream handling collides with the device-
            // lost recovery path that the renderer thread is concurrently
            // running. The CanForegroundCandidate helper at L1298 already
            // has this same IsIconic exclusion ("SW_RESTORE DX crash risk")
            // — extending the rule to the dance loop closes the matching gap.
            // Iconic clients are recovered on the user's next manual restore.
            if (NativeMethods.IsIconic(hwnd))
            {
                skippedIconic++;
                continue;
            }
            // v3.22.33 (T4 Opus sev-3): match the convention every other z-order
            // primitive in the codebase uses — IsClientResponsive 100ms probe
            // before cross-process SetWindowPos. The taskbar dance is two
            // SetWindowPos calls with z-order change; USER32 dispatches
            // WM_WINDOWPOSCHANGING synchronously to the target, and a hung
            // pump there is the exact failure surface that motivated the
            // v3.22.22-era IsClientResponsive helper. Skipping fast-fail is
            // strictly better than waiting for IsHungAppWindow's 5s kernel
            // threshold (the proxy gate above).
            if (!IsClientHwndResponsive(hwnd, out int lastErr))
            {
                skippedUnresponsive++;
                FileLogger.Warn($"RaiseClientsAboveTaskbar: skipping non-responsive window {c} (SendMessageTimeout WM_NULL > 100ms — likely mid-zone-load DX reset; lastErr={lastErr})");
                continue;
            }
            // The topmost dance reorders z-band without moving/sizing the
            // window. Either call can fail benignly on a transient pump
            // block; we log + continue rather than abort the whole loop.
            bool up = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0, flags);
            bool down = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST,
                0, 0, 0, 0, flags);
            if (up && down) raised++;
            else
                FileLogger.Warn($"RaiseClientsAboveTaskbar: {c} dance partial (up={up} down={down}) — taskbar may still slice through this window's bottom edge until next focus event");
        }

        if (foregroundActive)
        {
            // v3.22.33 (T3 Sonnet MEDIUM Issue 4): iterate for the next
            // non-iconic, non-hung, non-autologin, responsive candidate
            // instead of giving up at the first iconic active candidate.
            // Topology "minimized foreground EQ + non-minimized background
            // EQ" was a Bug-B re-failure surface in v3.22.32 — taskbar yield
            // requires a foreground window in the non-topmost band, and the
            // active-only check abandoned foreground entirely. Active still
            // wins when responsive — it's the user's actual focus. The
            // fallback walk only kicks in when active is unusable, and only
            // through `clients` (not the wider _processManager.Clients) so
            // we never foreground something the caller didn't already
            // include in the dance.
            EQClient? candidate = null;
            string? skipReason = null;
            var active = _processManager.GetActiveClient();
            if (active != null && CanForegroundCandidate(active, out skipReason))
            {
                candidate = active;
            }
            else
            {
                if (active != null && skipReason != null)
                    FileLogger.Info($"RaiseClientsAboveTaskbar: active client {active} not foregroundable ({skipReason}) — trying next candidate from {clients.Count} arranged");
                foreach (var c in clients)
                {
                    if (active != null && c.ProcessId == active.ProcessId) continue;
                    if (CanForegroundCandidate(c, out _))
                    {
                        candidate = c;
                        break;
                    }
                }
            }

            if (candidate != null)
            {
                // v3.22.41: skip foreground transfer when candidate is ALREADY
                // the current foreground window. ApplyDeferredCosmetics fires
                // up to 4 times during a 2-client autologin (each client's
                // LoginCredentialsSent at T+~7s + LoginComplete at T+~30s);
                // each raise's SwitchToClient runs ForceForegroundWindow's
                // AttachThreadInput + BringWindowToTop + SetForegroundWindow
                // chain — visible as a focus-grab + minor resize flash even
                // when candidate IS the current foreground. The topmost dance
                // above is the load-bearing z-order work; the foreground
                // transfer is only needed when foreground is a non-EQ window
                // or a DIFFERENT EQ client. Nate's 2026-05-23 13:38 team1
                // smoke at char-select: "both windows looked like they resized
                // and stole focus a few times" — caused by ~4 raises ×
                // redundant SwitchToClient on the already-foreground candidate.
                // GetForegroundWindow is fast (no IPC, kernel-cached) so the
                // check is cheap enough to run unconditionally.
                var currentFg = NativeMethods.GetForegroundWindow();
                if (currentFg == candidate.WindowHandle)
                {
                    FileLogger.Info($"RaiseClientsAboveTaskbar: candidate {candidate} already foreground — skipping SwitchToClient ({raised}/{clients.Count} raised, {skippedUnresponsive} skipped)");
                }
                else
                {
                    try
                    {
                        _windowManager.SwitchToClient(candidate, _autoLoginManager.IsLoginActive);
                        FileLogger.Info($"RaiseClientsAboveTaskbar: foregrounded {candidate} after topmost dance ({raised}/{clients.Count} raised, skippedIconic={skippedIconic} skippedUnresponsive={skippedUnresponsive})");
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Warn($"RaiseClientsAboveTaskbar: ForceForegroundWindow on {candidate} threw — {ex.Message}");
                    }
                }
            }
            else
            {
                FileLogger.Warn($"RaiseClientsAboveTaskbar: no foregroundable candidate (all iconic/hung/autologin/unresponsive); raised={raised}/{clients.Count}, skippedIconic={skippedIconic}, skippedUnresponsive={skippedUnresponsive}. Taskbar may still slice through windows until the user manually focuses one.");
            }
        }
        else
        {
            FileLogger.Info($"RaiseClientsAboveTaskbar: raised {raised}/{clients.Count} client(s) (no foreground change, skippedIconic={skippedIconic} skippedUnresponsive={skippedUnresponsive})");
        }
    }

    /// <summary>
    /// v3.22.33: inline 100ms pump-responsiveness probe matching the
    /// `WindowsApi.IsClientResponsive` implementation. Inlined here rather
    /// than routed through the WindowManager's IWindowsApi seam because
    /// TrayManager doesn't hold an `_api` reference — the WindowManager
    /// owns the test seam for its own SetWindowPos / SetWindowLongPtr calls.
    /// Inlining keeps the dependency surface from TrayManager to the
    /// Core/NativeMethods.cs P/Invoke layer, where the rest of TrayManager's
    /// Win32 calls already live.
    /// </summary>
    private static bool IsClientHwndResponsive(IntPtr hwnd, out int lastErr)
    {
        IntPtr ok = NativeMethods.SendMessageTimeout(
            hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK,
            100, out _);
        lastErr = (ok == IntPtr.Zero) ? System.Runtime.InteropServices.Marshal.GetLastWin32Error() : 0;
        return ok != IntPtr.Zero;
    }

    /// <summary>
    /// v3.22.33: shared "is this client a safe foreground target?" predicate
    /// for the topmost-dance recovery path. A candidate is foregroundable
    /// when it's a live window, not hung (per IsHungAppWindow's 5s threshold),
    /// not iconic (SW_RESTORE would risk DX crash on minimized EQ), not
    /// autologin-active (ForceForegroundWindow during credential typing
    /// would disrupt the DirectInput shared-memory injection), and the EQ
    /// pump is responsive (IsClientResponsive 100ms — fast-fail).
    /// </summary>
    private bool CanForegroundCandidate(EQClient c, out string? skipReason)
    {
        var hwnd = c.WindowHandle;
        if (!NativeMethods.IsWindow(hwnd)) { skipReason = "window gone"; return false; }
        if (NativeMethods.IsHungAppWindow(hwnd)) { skipReason = "hung"; return false; }
        if (NativeMethods.IsIconic(hwnd)) { skipReason = "minimized (SW_RESTORE DX crash risk)"; return false; }
        if (_autoLoginManager.IsLoginActive(c.ProcessId)) { skipReason = "autologin active"; return false; }
        if (!IsClientHwndResponsive(hwnd, out _)) { skipReason = "pump non-responsive"; return false; }
        skipReason = null;
        return true;
    }

    /// <summary>
    /// v3.22.32: post-ClientLost taskbar-recovery entry point. Called from a
    /// 250ms delayed timer in the ClientLost handler so the OS finishes its
    /// post-close z-order shuffle before we re-arrange. Re-applies the slim-
    /// titlebar position to every responsive non-autologin client (matching
    /// the single-screen ApplyDeferredCosmetics path) and then runs the
    /// topmost dance to push them above the resurfaced taskbar.
    /// </summary>
    private void RaiseRemainingClientsAboveTaskbar()
    {
        if (_disposed) return;  // v3.22.33: shutdown guard
        if (!_config.Layout.SlimTitlebar) return;
        var clients = _processManager.Clients;
        if (clients.Count == 0) return;

        // Build the responsive subset using the same gates ArrangeWindows applies,
        // PLUS the v3.22.44 Gate #2 iconic exclusion. ApplySlimTitlebar /
        // ArrangeWindows below call cross-process SetWindowPos with explicit
        // geometry and SWP_FRAMECHANGED — issuing those against a minimized
        // EQ window where Dalaya has released the D3D9 device race-collides
        // with the next user-initiated SW_RESTORE's device-recreate handler
        // and can crash A when sibling B exits (the original Scenario A
        // field report). Iconic A is re-positioned when the user restores
        // it themselves; the slim-titlebar guard timer (and the
        // ApplyDeferredCosmetics path on LoginComplete) re-asserts the
        // bounds on the next focus event.
        var responsive = new List<EQClient>(clients.Count);
        int skippedIconic = 0;
        foreach (var c in clients)
        {
            if (_autoLoginManager.IsLoginActive(c.ProcessId)) continue;
            if (!NativeMethods.IsWindow(c.WindowHandle)) continue;
            if (NativeMethods.IsHungAppWindow(c.WindowHandle)) continue;
            if (NativeMethods.IsIconic(c.WindowHandle))
            {
                skippedIconic++;
                continue;
            }
            responsive.Add(c);
        }
        if (responsive.Count == 0)
        {
            FileLogger.Info($"RaiseRemainingClientsAboveTaskbar: no responsive non-iconic non-autologin clients to recover (clients={clients.Count}, skippedIconic={skippedIconic})");
            return;
        }

        // Re-apply slim positioning on each client. The hook DLL maintains
        // position from inside the EQ process at every intercepted SetWindowPos,
        // but the sibling-close event doesn't trigger an EQ-side SetWindowPos —
        // so the hook never re-asserts after the close. Re-applying from outside
        // re-issues the SetWindowPos cross-process; the hook intercepts and
        // confirms its own config, the visible result is the desired bounds.
        bool isMM = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        if (isMM)
        {
            _windowManager.ArrangeWindows(responsive, _monitorSlotByPid);
        }
        else
        {
            var monitor = _windowManager.GetTargetMonitorBounds();
            int offset = _config.Layout.TitlebarOffset;
            foreach (var c in responsive)
                _windowManager.ApplySlimTitlebar(c.WindowHandle, monitor, offset);
        }

        // Refresh hook config so the per-PID shared memory reflects current
        // state (no slot rotation happened, but defensive).
        UpdateHookConfig();

        // Z-order recovery — the load-bearing part of this method.
        //
        // v3.22.32 verifier-convergent (T2 Opus + T3 Sonnet HIGH): foreground
        // only when EQ already owns the foreground. The sibling-close path
        // fires regardless of which app the user is currently in; passing
        // `true` unconditionally would yank focus from Discord / browser /
        // any non-EQ app at any time a background EQ client closes. Checking
        // GetActiveClient() != null gates the foreground call on "EQ is what
        // the user is currently looking at" — if true, taking foreground to
        // a surviving EQ is a visual continuity win (taskbar yields); if
        // false, the user has moved on and stealing focus is hostile.
        bool eqAlreadyForeground = _processManager.GetActiveClient() != null;
        RaiseClientsAboveTaskbar(responsive, foregroundActive: eqAlreadyForeground);

        FileLogger.Info($"RaiseRemainingClientsAboveTaskbar: re-arranged + raised {responsive.Count}/{clients.Count} client(s) after sibling close (eqAlreadyForeground={eqAlreadyForeground})");
    }

    /// <summary>
    /// Alt+M: Toggle multi-monitor / single-screen layout mode.
    /// 500ms debounce to prevent rapid re-triggering while windows are moving.
    /// </summary>
    private void OnToggleMultiMonitor()
    {
        // Phase 5a: the first-time-use gate was removed. Any bound combo fires the
        // toggle directly — the hotkey-conflict modal (P3.5-D) already blocks duplicate
        // bindings at config time, and every other hotkey operates this way. The
        // MultiMonitorEnabled bool stays on HotkeyConfig for downgrade safety but is
        // no longer consulted at dispatch time. Phase 6 deletes the field.

        long now = Environment.TickCount64;
        if (now - _lastMultiMonToggle < MultiMonToggleDebounceMs)
            return;
        _lastMultiMonToggle = now;

        // Full toggle — identical to the Settings checkbox
        bool isMulti = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        _config.Layout.Mode = isMulti ? "single" : "multimonitor";

        string label = isMulti ? "Single Screen" : "Multi-Monitor";
        FileLogger.Info($"ToggleMultiMonitor: switched to {label}");
        ShowBalloon($"Layout: {label}");

        ConfigManager.Save(_config);
        BuildContextMenu();

        // Re-arrange windows with the new mode
        var clients = _processManager.Clients;
        if (clients.Count > 0)
        {
            // v3.22.20: backfill slot map for any PIDs that exist but were
            // never assigned (e.g. PIDs discovered before this method first
            // fired). v3.22.21: free-slot scan via AssignNextFreeSlot.
            foreach (var c in clients)
            {
                if (!_monitorSlotByPid.ContainsKey(c.ProcessId))
                    AssignNextFreeSlot(c.ProcessId, "backfill on toggle");
            }
            _windowManager.ArrangeWindows(clients, _monitorSlotByPid);
            UpdateHookConfig();

            // v3.22.40: taskbar-coverage parity. Toggling MM mode while slim is
            // active rearranges via ArrangeWindows (SWP_NOZORDER) which doesn't
            // restore z-order if the taskbar slipped above EQ. Same pattern as
            // v3.22.38/39 ApplyDeferredCosmetics + v3.22.32 OnArrangeWindows.
            if (_config.Layout.SlimTitlebar)
            {
                bool eqAlreadyForeground = _processManager.GetActiveClient() != null;
                RaiseClientsAboveTaskbar(clients, foregroundActive: eqAlreadyForeground);
            }
        }
    }

    /// <summary>
    /// v3.22.90: set the window mode from the tray's Video Settings submenu,
    /// mirroring the Settings → Video "Window Style" checkboxes (shared source of
    /// truth: <see cref="WindowLayout.WindowMode"/>). Restyles running clients live
    /// just as Settings → Apply does — both modes render at native resolution, so
    /// only the frame differs and no relaunch is needed. Mirrors
    /// <see cref="OnToggleMultiMonitor"/>: mutate _config → Save → re-apply → rebuild.
    /// </summary>
    private void SetWindowMode(EQSwitch.Config.WindowMode mode)
    {
        if (_config.Layout.WindowMode == mode) return; // no-op if already set

        // Write the invariant before the mode so a concurrent AutoLogin
        // SaveImmediate serialize never sees WindowMode flipped while
        // SlimTitlebar lags (both modes are slim-managed; AppConfig.Validate
        // enforces this too).
        _config.Layout.SlimTitlebar = true;
        _config.Layout.WindowMode = mode;

        string label = mode == EQSwitch.Config.WindowMode.Windowed ? "Windowed" : "Fullscreen";
        FileLogger.Info($"SetWindowMode: switched to {label}");
        ShowBalloon($"Window mode: {label}");

        ConfigManager.Save(_config);
        BuildContextMenu(); // refresh the ●/○ markers

        // Live restyle — the subset ReloadConfigCore runs on Settings → Apply:
        // ApplySlimTitlebarToAll restyles non-injected clients per WindowMode;
        // UpdateHookConfig pushes the new style to injected (autologin) clients.
        var clients = _processManager.Clients;
        if (clients.Count > 0)
        {
            _windowManager.ApplySlimTitlebarToAll(clients, _injectedPids);
            UpdateHookConfig();
            RaiseClientsAboveTaskbar(clients, foregroundActive: _processManager.GetActiveClient() != null);
        }
    }

    /// <summary>
    /// Pop the tray context menu above the system clock on the primary monitor —
    /// or dismiss it if it's already open (toggle). Bypasses the Win11 z-band
    /// ceiling that lets Start cover normal tray UI by stealing foreground
    /// before Show(), so the menu wins the same-band activation race against
    /// e.g. an EQ slim-titlebar TOPMOST window.
    ///
    /// v3.22.49 hardening over v3.22.48 (field-reported by Nate: needed
    /// ~4 presses for the menu to appear when EQ was foreground; second
    /// press didn't dismiss):
    ///   • Toggle: re-press while visible closes the menu (matches tray UX).
    ///   • Activation: SetForegroundWindow on the menu's HWND after Show()
    ///     forces same-band winning over EQ's TOPMOST slim-titlebar.
    ///   • Logging: FileLogger.Info on entry so field reports of "did the
    ///     hotkey fire?" can be answered from the log.
    ///   • Disposed-snapshot guard: BuildContextMenu disposes + reassigns
    ///     _contextMenu; snapshot to local so we never call .Show() on a
    ///     disposed instance during the rebuild window.
    /// </summary>
    private void OnShowTrayMenu()
    {
        var menu = _contextMenu;
        if (menu == null || menu.IsDisposed)
        {
            FileLogger.Info("OnShowTrayMenu: menu null/disposed, no-op");
            return;
        }

        // Toggle: re-press dismisses (matches tray right-click UX).
        if (menu.Visible)
        {
            FileLogger.Info("OnShowTrayMenu: visible → closing");
            menu.Close();
            return;
        }

        var screen = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
        if (screen == null)
        {
            FileLogger.Warn("OnShowTrayMenu: no screen available (headless/RDP), aborting");
            return;
        }
        var workArea = screen.WorkingArea;
        // Anchor at the bottom-right of the working area — the clock's territory.
        // ToolStripDropDownDirection.AboveLeft makes the menu grow up-and-left
        // from that anchor, matching where Explorer pops a real tray right-click.
        // WinForms auto-flips the direction if it would go off-screen, so this
        // also works on edge taskbar configurations.
        var anchor = new Point(workArea.Right, workArea.Bottom);
        FileLogger.Info($"OnShowTrayMenu: showing at {anchor}");

        // v3.22.51: hotkey-only submenu-bleed fix. The hotkey path always
        // anchors at the primary monitor's bottom-right, so default-direction
        // submenus (or explicitly Right-set ones like videoMenu/launcherMenu)
        // bleed onto the secondary monitor. Right-click path uses the cursor
        // anchor (wherever the user clicked), where original directions are
        // fine — don't touch them. Snapshot here, override to Left for the
        // duration of THIS show, restore on Closed so the next right-click
        // sees pristine directions.
        var savedDirections = new Dictionary<ToolStripMenuItem, ToolStripDropDownDirection>();
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripMenuItem mi && mi.DropDownItems.Count > 0)
            {
                savedDirections[mi] = mi.DropDownDirection;
                mi.DropDownDirection = ToolStripDropDownDirection.Left;
            }
        }
        void RestoreOnce(object? s, ToolStripDropDownClosedEventArgs e)
        {
            menu.Closed -= RestoreOnce;
            foreach (var kvp in savedDirections)
                kvp.Key.DropDownDirection = kvp.Value;
            FileLogger.Info("OnShowTrayMenu: restored original submenu directions");
        }
        menu.Closed += RestoreOnce;

        menu.Show(anchor, ToolStripDropDownDirection.AboveLeft);

        // ToolStripDropDown.Show internally uses SW_SHOWNOACTIVATE, so the menu
        // joins the TOPMOST z-band but doesn't take foreground. If EQ is also
        // TOPMOST (slim-titlebar mode), its prior activation wins paint order
        // within the same band and our menu renders invisible underneath.
        // Promote the menu's own HWND to foreground to flip that order.
        if (menu.Handle != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(menu.Handle);
    }

    /// <summary>
    /// Launch one EQ client.
    /// <para>
    /// v3.22.53: If <see cref="LaunchConfig.DefaultLaunchOneAccount"/> resolves
    /// to an Account in the user's <see cref="AppConfig.Accounts"/> list, route
    /// through AutoLoginManager so the single-click LaunchOne path matches the
    /// team1 launch path — EULA auto-dismissed, credentials typed via
    /// dinput8 SHM, window arranged after the login screen settles. Without
    /// that opt-in, falls back to the historical plain-launch behavior:
    /// process start with <c>Launch.Arguments</c> ("patchme" by default), no
    /// input automation. The user has to type their own password at the login
    /// screen.
    /// </para>
    /// <para>
    /// **Why patchme alone isn't enough to bypass EULA/Sony screens:** Dalaya's
    /// dinput8.dll patcher ("Edge") explicitly disables the patchme arg shortly
    /// after process start (string <c>"disabling patchme"</c> at VA
    /// <c>0x100f7fc0</c>, confirmed in the 2026-05-21 Edge audit). The
    /// autologin path side-steps this because Edge's MQ2-derived connection
    /// management is compatible with concurrent multi-client launches when
    /// each client uses unique credentials — but it requires credentials.
    /// </para>
    /// </summary>
    private void OnLaunchOne()
    {
        var defaultAccountName = _config.Launch.DefaultLaunchOneAccount?.Trim();
        if (!string.IsNullOrEmpty(defaultAccountName))
        {
            Account? account;
            lock (ConfigManager.ConfigMutationLock)
            {
                account = _config.FindAccountByName(defaultAccountName);
            }
            if (account != null)
            {
                FileLogger.Info($"OnLaunchOne: routing via AutoLoginManager (account '{account.EffectiveLabel}')");
                FireAccountLogin(account);
                return;
            }
            // Fall through to plain launch. Logged at Warn so the user sees a
            // hint if their named account got renamed/deleted out from under
            // them.
            FileLogger.Warn($"OnLaunchOne: configured DefaultLaunchOneAccount='{defaultAccountName}' did not resolve to an Account — falling back to plain launch");
            ShowBalloon($"LaunchOne default account '{defaultAccountName}' not found — launching without autologin");
        }
        _launchManager.LaunchOne();
    }

    /// <summary>
    /// Launch all configured EQ clients with staggered delays.
    /// </summary>
    private void OnLaunchAll()
    {
        _launchManager.LaunchAll();
    }

    // ─── Affinity Management ────────────────────────────────────────

    /// <summary>
    /// Install a WinEvent hook for EVENT_SYSTEM_FOREGROUND.
    /// Fires instantly when any window becomes foreground — zero latency vs. polling.
    /// The callback runs on the UI thread (WINEVENT_OUTOFCONTEXT requires a message pump).
    /// </summary>
    private void StartForegroundHook()
    {
        // Guard against double-start (would leak the previous hook handle)
        if (_foregroundHook != IntPtr.Zero) return;

        // Store delegate as a field to prevent GC collection (same pattern as keyboard hook)
        _foregroundHookProc = OnForegroundChanged;
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _foregroundHookProc,
            0, 0, // all processes, all threads
            NativeMethods.WINEVENT_OUTOFCONTEXT);
            // Note: NOT using WINEVENT_SKIPOWNPROCESS — we need events when
            // EQSwitch windows gain focus so PiP correctly detects that
            // no EQ client is active (GetActiveClient returns null).

        if (_foregroundHook == IntPtr.Zero)
        {
            FileLogger.Warn("SetWinEventHook failed — falling back to polling timer");
            StartForegroundHookFallback();
            return;
        }

        FileLogger.Info("Foreground event hook installed (instant detection)");
    }

    /// <summary>
    /// Fallback polling timer in case SetWinEventHook fails (shouldn't happen, but defensive).
    /// </summary>
    private System.Windows.Forms.Timer? _affinityFallbackTimer;
    private void StartForegroundHookFallback()
    {
        _affinityFallbackTimer = new System.Windows.Forms.Timer { Interval = AffinityPollIntervalMs };
        _affinityFallbackTimer.Tick += (_, _) => OnForegroundChangedCore();
        _affinityFallbackTimer.Start();
        FileLogger.Warn($"Foreground polling fallback started ({AffinityPollIntervalMs}ms)");
    }

    /// <summary>
    /// WinEvent callback — fires on the UI thread when any window becomes foreground.
    /// Debounced to avoid doing expensive work (affinity, PiP) on every
    /// intermediate window during rapid Alt+Tab cycling.
    /// </summary>
    private void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (_foregroundDebounceTimer == null)
            {
                _foregroundDebounceTimer = new System.Windows.Forms.Timer { Interval = ForegroundDebounceMs };
                var timer = _foregroundDebounceTimer; // capture for closure — survives field nulling
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    try { OnForegroundChangedCore(); }
                    catch (Exception ex2) { FileLogger.Error("Foreground hook callback error", ex2); }
                };
            }
            // Reset timer on each event — only fires after input settles
            _foregroundDebounceTimer.Stop();
            _foregroundDebounceTimer.Start();
        }
        catch (Exception ex)
        {
            FileLogger.Error("Foreground hook callback error", ex);
        }
    }

    /// <summary>
    /// Core logic for foreground change — shared by event hook and fallback timer.
    /// </summary>
    private void OnForegroundChangedCore()
    {
        if (_reloading) return; // stale tick during ReloadConfig — managers are partially reset
        var active = _processManager.GetActiveClient();
        var clients = _processManager.Clients;

        // Re-apply slim titlebar when an EQ window comes to foreground.
        // EQ resets its window position during screen transitions (login → char select).
        if (_config.Layout.SlimTitlebar && active != null)
        {
            _windowManager.ApplySlimTitlebarToAll(clients, _injectedPids);
        }

        if (_config.Affinity.Enabled)
        {
            _affinityManager.ApplyAffinityRules(clients, active);
        }

        // Track last two active clients for swap-last-two mode
        if (active != null && active.ProcessId != _lastActivePid)
        {
            _previousActivePid = _lastActivePid;
            _lastActivePid = active.ProcessId;


        }

        // Update PiP sources when foreground changes
        if (_pipOverlay != null && !_pipOverlay.IsDisposed)
        {
            if (clients.Count < 1)
            {
                _pipOverlay.Close();
                _pipOverlay.Dispose();
                _pipOverlay = null;
            }
            else
            {
                _pipOverlay.UpdateSources(clients, active);
            }
        }
    }

    private void StartRetryTimer()
    {
        if (!_config.Affinity.Enabled) return;

        // Retry timer runs at the configured interval (default 2s)
        _retryTimer = new System.Windows.Forms.Timer
        {
            Interval = _config.Affinity.LaunchRetryDelayMs
        };
        _retryTimer.Tick += (_, _) =>
        {
            _affinityManager.ProcessRetries(_processManager.Clients);
        };
        _retryTimer.Start();

        FileLogger.Info($"Affinity retry timer started (every {_config.Affinity.LaunchRetryDelayMs}ms)");
    }

    // ─── Tray UI ─────────────────────────────────────────────────────

    /// <summary>
    /// Phase 3 Accounts submenu: parent item with per-account rows, separator, and "Manage Accounts..." footer.
    /// Each row fires LoginToCharselect on click. Empty-state row teaches what the submenu is for.
    /// Takes the list as an arg so rendering has no hidden _config reach.
    /// </summary>
    private ToolStripMenuItem BuildAccountsSubmenu(IReadOnlyList<Account> accounts, LegacyHotkeyLookup hkLookup)
    {
        var menu = new ToolStripMenuItem("\uD83D\uDD11  Accounts")
        {
            Font = _boldMenuFont,
        };

        if (accounts.Count == 0)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem("No accounts yet \u2014 click Manage Accounts...")
            {
                Enabled = false
            });
        }
        else
        {
            foreach (var acc in accounts)
            {
                var captured = acc; // explicit capture for closure
                // ShortcutKeyDisplayString is the WinForms idiom for the hotkey column —
                // renders right-aligned with proper padding. Applied to every menu item
                // with a hotkey (root, submenu, video) for visual consistency.
                // Accounts = login username (per v4 split spec). Account.Name is a
                // user-editable label and on legacy-migrated rows often holds the
                // character name, colliding with the Characters submenu. Show Username
                // directly so the Accounts menu reflects the unique login identity;
                // tooltip already shows Username@Server. Fallback to EffectiveLabel
                // only if Username is empty (defensive \u2014 shouldn't happen post-split).
                var displayName = string.IsNullOrEmpty(captured.Username)
                    ? captured.EffectiveLabel
                    : captured.Username;
                var label = $"\uD83D\uDC64  {displayName}";
                var item = new ToolStripMenuItem(label)
                {
                    ToolTipText = captured.Tooltip,
                    ShortcutKeyDisplayString = hkLookup.GetCombo(captured.Name),
                    // AccountItemMarker \u2192 DarkMenuRenderer paints the row in
                    // FgAccountOrange (the hotkey column stays white per
                    // OnRenderItemText's shortcut-pass override).
                    Tag = DarkMenuRenderer.AccountItemMarker,
                };
                item.Click += (_, _) => FireAccountLogin(captured);
                menu.DropDownItems.Add(item);
            }
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add("\u2699  Manage Accounts...", null, (_, _) => ShowSettings(2));
        return menu;
    }

    /// <summary>
    /// Phase 3 Characters submenu: parent item with per-character rows and "Manage Characters..." footer.
    /// Each row fires LoginAndEnterWorld on click. Tooltip resolves the backing Account label
    /// via accountsByKey lookup (falls back to username@server on FK drift).
    /// </summary>
    private ToolStripMenuItem BuildCharactersSubmenu(
        IReadOnlyList<Character> characters,
        IReadOnlyDictionary<AccountKey, Account> accountsByKey,
        LegacyHotkeyLookup hkLookup)
    {
        var menu = new ToolStripMenuItem("\uD83E\uDDD9  Characters")
        {
            Font = _boldMenuFont,
        };

        if (characters.Count == 0)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem(
                "No characters yet \u2014 characters added here will auto-enter-world")
            {
                Enabled = false
            });
        }
        else
        {
            foreach (var ch in characters)
            {
                var captured = ch;
                // Phase 4: orphan Characters (no Account FK — Unlink outcome) render
                // dim with a warning prefix and a corrective tooltip. Click surfaces
                // an actionable balloon rather than the AutoLoginManager raw error.
                bool isOrphan = string.IsNullOrEmpty(captured.AccountUsername);
                var label = isOrphan
                    ? $"\u26A0  {captured.LabelWithClass}  (no account)"
                    : $"\uD83E\uDDD9  {captured.LabelWithClass}";
                var tooltip = isOrphan
                    ? $"\u26A0 '{captured.Name}' has no Account linked. Open Settings \u2192 Accounts \u2192 Edit this Character to assign one."
                    : BuildCharacterTooltip(captured, accountsByKey);
                var item = new ToolStripMenuItem(label)
                {
                    ToolTipText = tooltip,
                    ShortcutKeyDisplayString = hkLookup.GetCombo(captured.Name),
                };
                if (isOrphan)
                {
                    // OrphanItemMarker → DarkMenuRenderer routes ForeColor to
                    // DisabledText every repaint. Setting Item.ForeColor at
                    // build time (the prior approach) silently lost to the
                    // renderer's per-paint overwrite — orphan rows had been
                    // showing white, not dim. Tag-routing fixes that
                    // structurally for both the primary label AND the hotkey
                    // column (OnRenderItemText's shortcut-pass branch keeps
                    // orphan rows uniformly dim).
                    item.Tag = DarkMenuRenderer.OrphanItemMarker;
                    item.Click += (_, _) => ShowBalloon(
                        $"Character '{captured.Name}' has no Account linked — open Settings to assign one before launching.");
                }
                else
                {
                    item.Click += (_, _) => FireCharacterLogin(captured);
                }
                menu.DropDownItems.Add(item);
            }
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add("\u2699  Manage Characters...", null, (_, _) => ShowSettings(2));
        return menu;
    }

    /// <summary>
    /// Phase 3 Teams submenu: parent item with populated team rows and "Manage Teams..." footer.
    /// Teams are populated if either slot string is non-empty. Empty-state row renders
    /// when all twelve teams are unpopulated (v3.22.58 grew from 6 → 12). Click fires
    /// ExecuteTrayAction("LoginAll[N]") → FireTeam → LoginAndEnterWorld/LoginToCharselect
    /// per slot kind.
    /// </summary>
    private ToolStripMenuItem BuildTeamsSubmenu(AppConfig cfg, LegacyHotkeyLookup hkLookup)
    {
        var menu = new ToolStripMenuItem("\uD83D\uDC65  Teams")
        {
            Font = _boldMenuFont,
        };
        var hk = cfg.Hotkeys;
        var teams = new[]
        {
            (Num: 1, Slot1: cfg.Team1Account1, Slot2: cfg.Team1Account2, Combo: hk.TeamLogin1, Action: "LoginAll"),
            (Num: 2, Slot1: cfg.Team2Account1, Slot2: cfg.Team2Account2, Combo: hk.TeamLogin2, Action: "LoginAll2"),
            (Num: 3, Slot1: cfg.Team3Account1, Slot2: cfg.Team3Account2, Combo: hk.TeamLogin3, Action: "LoginAll3"),
            (Num: 4, Slot1: cfg.Team4Account1, Slot2: cfg.Team4Account2, Combo: hk.TeamLogin4, Action: "LoginAll4"),
            // Teams 5/6 deliberately have no hotkey binding — user opted out of
            // the General-tab dropdown for them. Tray right-click is the only
            // launch surface; Combo is empty so ShortcutKeyDisplayString stays blank.
            (Num: 5, Slot1: cfg.Team5Account1, Slot2: cfg.Team5Account2, Combo: "", Action: "LoginAll5"),
            (Num: 6, Slot1: cfg.Team6Account1, Slot2: cfg.Team6Account2, Combo: "", Action: "LoginAll6"),
            // v3.22.58: Teams 7-12 added. No hotkey binding (TeamHotkeysDialog
            // stays at 4 rows by design) and no trayclick action dropdown entry
            // — the tray right-click submenu is the only launch surface.
            (Num:  7, Slot1: cfg.Team7Account1,  Slot2: cfg.Team7Account2,  Combo: "", Action: "LoginAll7"),
            (Num:  8, Slot1: cfg.Team8Account1,  Slot2: cfg.Team8Account2,  Combo: "", Action: "LoginAll8"),
            (Num:  9, Slot1: cfg.Team9Account1,  Slot2: cfg.Team9Account2,  Combo: "", Action: "LoginAll9"),
            (Num: 10, Slot1: cfg.Team10Account1, Slot2: cfg.Team10Account2, Combo: "", Action: "LoginAll10"),
            (Num: 11, Slot1: cfg.Team11Account1, Slot2: cfg.Team11Account2, Combo: "", Action: "LoginAll11"),
            (Num: 12, Slot1: cfg.Team12Account1, Slot2: cfg.Team12Account2, Combo: "", Action: "LoginAll12"),
        };
        var populated = teams.Where(t => !string.IsNullOrEmpty(t.Slot1) || !string.IsNullOrEmpty(t.Slot2)).ToList();

        if (populated.Count == 0)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem("No teams configured \u2014 click Manage Teams...")
            {
                Enabled = false
            });
        }
        else
        {
            foreach (var t in populated)
            {
                var action = t.Action;  // capture for closure
                // Show what the team actually launches \u2014 slot values resolved to
                // Character.Name first (preferred for in-game identity), then
                // Account.Username, then raw fallback. "natedogg / acpots" beats
                // "Auto-Login Team 1" for at-a-glance recognition. The team number
                // stays discoverable via the hotkey display + the tooltip.
                //
                // SlotSource is carried through so the renderer can color
                // Account-resolved names orange and Character-resolved names
                // white inside one row (DarkMenuRenderer.OnRenderItemText
                // walks the TeamRowSegments stored in Item.Tag). The Item.Text
                // we set below MUST equal the concatenated visible string \u2014
                // ToolStrip sizes the row from Text, and the segment-draw
                // walks e.TextRectangle assuming that sizing.
                var slotResults = new[] { t.Slot1, t.Slot2 }
                    .Select(ResolveTeamSlotDisplay)
                    .Where(r => !string.IsNullOrEmpty(r.Name))
                    .ToList();
                const string emojiPrefix = "\uD83D\uDE80  ";
                string label;
                DarkMenuRenderer.TeamRowSegments? segments = null;
                if (slotResults.Count > 0)
                {
                    label = $"{emojiPrefix}{string.Join(" / ", slotResults.Select(r => r.Name))}";
                    var segs = new List<DarkMenuRenderer.TeamRowSegment>(2 * slotResults.Count + 1)
                    {
                        new(emojiPrefix, IsAccount: false),
                    };
                    for (int i = 0; i < slotResults.Count; i++)
                    {
                        if (i > 0) segs.Add(new DarkMenuRenderer.TeamRowSegment(" / ", IsAccount: false));
                        segs.Add(new DarkMenuRenderer.TeamRowSegment(
                            slotResults[i].Name,
                            IsAccount: slotResults[i].Source == SlotSource.Account));
                    }
                    segments = new DarkMenuRenderer.TeamRowSegments(segs);
                }
                else
                {
                    // No slots resolved \u2014 fall back to plain label, no segments,
                    // renders in standard ItemText (white) via base path.
                    label = $"{emojiPrefix}Auto-Login Team {t.Num}";
                }
                var tooltip = BuildTeamTooltip(t.Num);
                var item = new ToolStripMenuItem(label)
                {
                    ToolTipText = tooltip,
                    ShortcutKeyDisplayString = t.Combo,
                };
                if (segments != null) item.Tag = segments;
                item.Click += (_, _) => ExecuteTrayAction(action);
                menu.DropDownItems.Add(item);
            }
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add("\u2699  Manage Teams...", null, (_, _) => ShowSettings(2, openTeamsDialog: true));
        return menu;
    }

    private void BuildContextMenu()
    {
        // Dispose old menu and all its items before rebuilding (prevents leak if called multiple times)
        _contextMenu?.Dispose();
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Renderer = new DarkMenuRenderer();
        // Gate menu hover tooltips on the same Show Tooltips toggle that
        // gates balloon toasts (Settings → Video → Preferences). Lets users
        // hide the helpful-on-first-run hover hints once they know the menu.
        // ContextMenuStrip rebuilds on every ReloadConfig so the toggle takes
        // effect immediately on Save in Settings.
        _contextMenu.ShowItemToolTips = _config.ShowTooltips;
        _contextMenu.Closed += (_, _) =>
        {
            if (_clientMenuDirty) UpdateClientMenu();
            // v3.22.72: drain any lost-client balloons that the coalesce timer
            // deferred while the menu was open. Now safe to surface the toast
            // without cancelling a menu the user is actively interacting with.
            TryDispatchLostClientBalloon();
        };

        // v3.22.27 Item 1: BuildContextMenu is reached from non-ReloadConfigCore
        // paths (PiP toggle, multi-monitor toggle, account-hotkey-change Apply)
        // where the outer ConfigMutationLock is NOT held. Submenu builders
        // iterate the live _config.* refs internally (BuildAccountsSubmenu,
        // BuildCharactersSubmenu, BuildTeamsSubmenu) so the lock must extend
        // through their calls. Reentrant — the ReloadConfigCore call-path
        // that already holds the outer lock won't deadlock. Hold time:
        // ms-scale UI-thread menu construction. Symmetric with PopulateFromConfig.
        lock (ConfigManager.ConfigMutationLock)
        {
        var hk = _config.Hotkeys;

        _boldMenuFont?.Dispose();
        _boldMenuFont = new Font(_contextMenu.Font, FontStyle.Bold);

        // Title bar
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var titleItem = new ToolStripMenuItem($"\u2694  Dalaya v{version}  \u2694") { Enabled = false, Font = _boldMenuFont };
        _contextMenu.Items.Add(titleItem);
        _contextMenu.Items.Add(new ToolStripSeparator());

        var launchOneItem = new ToolStripMenuItem("\u2694  Launch Client")
        {
            Font = _boldMenuFont,
            // Hotkey suffix omitted — root menu width budget. Hotkey still fires globally.
        };
        launchOneItem.Click += (_, _) => OnLaunchOne();
        _contextMenu.Items.Add(launchOneItem);

        var launchTeamItem = new ToolStripMenuItem("\uD83C\uDFAE  Launch Team")
        {
            Font = _boldMenuFont,
            // Hotkey suffix omitted — root menu width budget. Hotkey still fires globally.
        };
        launchTeamItem.Click += (_, _) => ExecuteTrayAction("LoginAll");
        _contextMenu.Items.Add(launchTeamItem);

        // Phase 3 — three intent-explicit submenus (Accounts → charselect, Characters → enter world, Teams → parallel).
        _contextMenu.Items.Add(new ToolStripSeparator());

        var hkLookup = new LegacyHotkeyLookup(_config);

        // Defensive: ToDictionary would throw on duplicate AccountKey (hand-edited
        // config corner case). Migration enforces uniqueness, but we must not crash
        // the tray on bad data — first-wins dedup + warn is correct.
        var accountsByKey = new Dictionary<AccountKey, Account>();
        foreach (var acc in _config.Accounts)
        {
            var key = new AccountKey(acc.Username, acc.Server);
            if (!accountsByKey.TryAdd(key, acc))
                FileLogger.Warn($"BuildContextMenu: duplicate AccountKey {key} in Accounts list — using first occurrence (hand-edited config?)");
        }

        _contextMenu.Items.Add(BuildAccountsSubmenu(_config.Accounts, hkLookup));
        _contextMenu.Items.Add(BuildCharactersSubmenu(_config.Characters, accountsByKey, hkLookup));
        _contextMenu.Items.Add(BuildTeamsSubmenu(_config, hkLookup));

        _contextMenu.Items.Add(new ToolStripSeparator());

        _clientsMenu = new ToolStripMenuItem("\uD83D\uDC64  Clients");

        // v3.22.53: Force-Kill + Detach Hooks moved INTO the Clients submenu
        // at the top (with a separator under them) per Nate 2026-05-26 \u2014
        // they were previously top-level peers of Clients/Process Manager and
        // crowded the right-click menu. Both items target the same set of
        // running eqgame.exe processes the Clients submenu lists, so this
        // groups them logically. Constructed as fields so UpdateClientMenu
        // can rebuild the per-client list below them without disposing
        // them (would orphan the DropDownOpening handler on Force-Kill and
        // the _detachItem reference used by RefreshDetachMenuState).

        // Force-Kill Stuck Client \u2014 Task Manager fallback for hung eqgame.exe
        // (DX reset deadlock, WndProc loop). Submenu lazily populates on open
        // so it always reflects the current eqgame.exe set.
        _forceKillMenu = new ToolStripMenuItem("\uD83D\uDC80  Force-Kill Stuck Client")
        {
            DropDownDirection = ToolStripDropDownDirection.Right
        };
        _forceKillMenu.DropDownOpening += (_, _) => PopulateForceKillMenu(_forceKillMenu);
        // Seed with one item so the chevron renders; PopulateForceKillMenu
        // replaces it the moment the submenu opens.
        _forceKillMenu.DropDownItems.Add("(scanning...)").Enabled = false;
        _clientsMenu.DropDownItems.Add(_forceKillMenu);

        // v3.22.54: Detach Hooks menu item removed per Nate 2026-05-26 —
        // "i tried the detach hook again and the game was working but was
        // minimized and it still crashed. so i dont think we need to provide
        // the detach hook feature tbh". The underlying EjectFromAllInjectedClients
        // method + _injectedPids tracking are kept (load-bearing for the
        // hook DLL lifecycle: CleanupHookConfigOnly, the post-process-exit
        // cleanup, etc.) but no user-facing surface invokes Eject anymore.
        // Separator under the Force-Kill item is also gone since there's
        // only one admin item left to separate from the client list.

        // Separator between the single admin item (Force-Kill) and the
        // per-client list below.
        _clientsAdminSeparator = new ToolStripSeparator();
        _clientsMenu.DropDownItems.Add(_clientsAdminSeparator);

        // Placeholder seeded below the admin items so the submenu renders a
        // chevron even before the first UpdateClientMenu pass. UpdateClientMenu
        // replaces everything from index ClientsMenuAdminItemCount onward.
        _clientsMenu.DropDownItems.Add("(scanning...)");

        // v3.22.53: when the Clients submenu opens, default keyboard focus
        // should land on the FIRST CLIENT entry (or the Refresh row if no
        // clients), NOT on the Force-Kill item at the top. Force-Kill is
        // recovery, not the routine action — Nate wants the routine action
        // pre-selected so up/down keys traverse clients immediately.
        _clientsMenu.DropDownOpened += (_, _) => SelectClientsMenuDefault();

        _contextMenu.Items.Add(_clientsMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());

        _contextMenu.Items.Add("⚡  Process Manager", null, (_, _) => ShowProcessManager());

        // Video Settings submenu
        var videoMenu = new ToolStripMenuItem("\uD83D\uDCFA  Video Settings") { DropDownDirection = ToolStripDropDownDirection.Right };
        videoMenu.DropDownItems.Add("\uD83D\uDCDD  Video Settings...", null, (_, _) =>
        {
            ShowSettings(1); // Video tab
        });
        videoMenu.DropDownItems.Add(new ToolStripSeparator());

        // Window-mode radio (v3.22.90) \u2014 two views of one field
        // (_config.Layout.WindowMode); mirrors the Settings \u2192 Video "Window
        // Style" checkboxes. \u25CF = active, \u25CB = inactive.
        ToolStripMenuItem WindowModeRadio(EQSwitch.Config.WindowMode m, string text)
        {
            var item = new ToolStripMenuItem($"{(_config.Layout.WindowMode == m ? "\u25CF" : "\u25CB")}  {text}");
            item.Click += (_, _) => SetWindowMode(m);
            return item;
        }
        videoMenu.DropDownItems.Add(WindowModeRadio(EQSwitch.Config.WindowMode.Fullscreen, "Fullscreen mode"));
        videoMenu.DropDownItems.Add(WindowModeRadio(EQSwitch.Config.WindowMode.Windowed, "Windowed mode"));
        videoMenu.DropDownItems.Add(new ToolStripSeparator());

        var pipItem = new ToolStripMenuItem(
            $"{(_config.Pip.Enabled ? "\u2705" : "\u2B1C")}  Picture in Picture");
        if (!string.IsNullOrEmpty(hk.TogglePip))
            pipItem.ShortcutKeyDisplayString = hk.TogglePip;
        pipItem.Click += (_, _) => TogglePip();  // TogglePip rebuilds the context menu internally
        videoMenu.DropDownItems.Add(pipItem);
        videoMenu.DropDownItems.Add(new ToolStripSeparator());
        var fixWindowsItem = new ToolStripMenuItem("Fix Windows  \uD83D\uDD27")
        {
            ShortcutKeyDisplayString = hk.ArrangeWindows
        };
        fixWindowsItem.Click += (_, _) => OnArrangeWindows();
        videoMenu.DropDownItems.Add(fixWindowsItem);
        videoMenu.DropDownItems.Add("Swap Windows  \uD83D\uDD00", null, (_, _) => ExecuteTrayAction("SwapWindows"));
        bool isMultiMon = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        var multiMonItem = new ToolStripMenuItem(
            $"{(isMultiMon ? "\u2705" : "\u2B1C")}  Multi-Monitor Mode")
        {
            ShortcutKeyDisplayString = hk.ToggleMultiMonitor
        };
        multiMonItem.Click += (_, _) =>
        {
            _config.Hotkeys.MultiMonitorEnabled = true; // ensure toggle works from menu
            OnToggleMultiMonitor();
            BuildContextMenu(); // rebuild to update checkmark
        };
        videoMenu.DropDownItems.Add(multiMonItem);
        _contextMenu.Items.Add(videoMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());

        _contextMenu.Items.Add("\u2699  Settings...", null, (_, _) => ShowSettings());

        // Launcher submenu (files + links)
        var launcherMenu = new ToolStripMenuItem("\uD83D\uDCC2  Launcher") { DropDownDirection = ToolStripDropDownDirection.Right };
        var linksMenu = new ToolStripMenuItem("\uD83C\uDF10  Links");
        linksMenu.DropDownItems.Add("\uD83C\uDFE0  Dalaya", null, (_, _) => FileOperations.OpenUrl("https://dalaya.org/", ShowBalloon));
        linksMenu.DropDownItems.Add(new ToolStripSeparator());
        linksMenu.DropDownItems.Add("\uD83D\uDDE1  Shards Wiki", null, (_, _) => FileOperations.OpenUrl("https://wiki.shardsofdalaya.com/wiki/Main_Page", ShowBalloon));
        linksMenu.DropDownItems.Add("\uD83D\uDCD6  Dalaya Wiki", null, (_, _) => FileOperations.OpenUrl("https://wiki.dalaya.org/", ShowBalloon));
        linksMenu.DropDownItems.Add("\uD83C\uDFC6  Fomelo Dalaya", null, (_, _) => FileOperations.OpenUrl("https://dalaya.org/fomelo/", ShowBalloon));
        linksMenu.DropDownItems.Add("\uD83D\uDCDC  Dalaya Listsold", null, (_, _) => FileOperations.OpenUrl("https://dalaya.org/listsold.php", ShowBalloon));
        launcherMenu.DropDownItems.Add("\uD83D\uDD27  Dalaya Patcher", null, (_, _) => FileOperations.OpenDalayaPatcher(_config, ShowBalloon, () => ShowSettings(5)));
        launcherMenu.DropDownItems.Add("\uD83D\uDCAC  Dalaya Discord", null, (_, _) => FileOperations.OpenUrl("discord://discord.com/channels/1233224126353768490/1249250739918864446", ShowBalloon));
        launcherMenu.DropDownItems.Add(new ToolStripSeparator());
        launcherMenu.DropDownItems.Add("✂  Trim Log Files", null, (_, _) => FileOperations.TrimLogFiles(_config, ShowBalloon));
        launcherMenu.DropDownItems.Add("\uD83D\uDCDC  Open Log File...", null, (_, _) => FileOperations.OpenLogFile(_config, ShowBalloon, () => ShowSettings(0)));
        launcherMenu.DropDownItems.Add("\uD83D\uDCC4  Open eqclient.ini", null, (_, _) => FileOperations.OpenEqClientIni(_config, ShowBalloon, () => ShowSettings(0)));
        launcherMenu.DropDownItems.Add(new ToolStripSeparator());
        launcherMenu.DropDownItems.Add(linksMenu);
        launcherMenu.DropDownItems.Add(new ToolStripSeparator());
        launcherMenu.DropDownItems.Add("\uD83D\uDCC8  Open EQLogParser", null, (_, _) => FileOperations.OpenEqLogParser(_config, ShowBalloon, () => ShowSettings(5)));
        launcherMenu.DropDownItems.Add("\uD83D\uDCCA  Open Gamparse", null, (_, _) => FileOperations.OpenGamparse(_config, ShowBalloon, () => ShowSettings(5)));
        launcherMenu.DropDownItems.Add("\uD83C\uDFAF  Open GINA", null, (_, _) => FileOperations.OpenGina(_config, ShowBalloon, () => ShowSettings(5)));
        launcherMenu.DropDownItems.Add("\uD83D\uDCDD  Open Notes", null, (_, _) => FileOperations.OpenNotes(_config, ShowBalloon));
        _contextMenu.Items.Add(launcherMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("\u2716  Exit", null, (_, _) => Shutdown());

        // v3.22.50\u2192v3.22.51: NO global submenu-direction sweep here. The
        // right-click path preserves the original per-item direction (some
        // submenus \u2014 videoMenu, launcherMenu, forceKillMenu \u2014 explicitly set
        // `Right` and Nate's muscle-memory expects that). Only the Ctrl+Alt+M
        // hotkey path applies a Left override (in OnShowTrayMenu) with a
        // matched Closed-event restore so the next right-click sees pristine
        // directions.

        // ContextMenuStrip is toggled on/off in MouseDown to prevent left-click
        // from showing the menu (which steals focus and breaks double-click).
        _trayIcon!.ContextMenuStrip = null;
        } // end v3.22.27 Item 1 ConfigMutationLock block — extends to method
          // end so hk + _config.Pip + _config.Layout reads in the back half
          // of menu construction are covered. Click-handler closures capture
          // `hk` and fire later (after lock release), but the writes inside
          // those closures (e.g. _config.Hotkeys.MultiMonitorEnabled = true)
          // are latent-safe today: SM doesn't write Hotkeys. Same SM-write-
          // trigger condition as Item 1's other latent sites.
    }

    private bool _clientMenuDirty;

    private void UpdateClientMenu()
    {
        // v3.22.44 r3 (T3-Opus F4 / T3-Sonnet F7 MEDIUM): delegate to shared
        // helper. Round 2 had the refresh inline here; round 3 extracts it so
        // InjectPreResume and InjectHookDll can also call it directly without
        // waiting for the next ProcessManager 10s poll (which is what fires
        // ClientListChanged → UpdateClientMenu). Without this, the user could
        // launch a team via the hotkey and see the Detach Hooks menu stuck
        // disabled for up to 10 seconds even though _injectedPids was already
        // populated by InjectPreResume.
        RefreshDetachMenuState();

        if (_clientsMenu == null) return;

        // Don't rebuild while the menu is open — it causes the menu to close
        if (_contextMenu?.Visible == true)
        {
            _clientMenuDirty = true;
            return;
        }
        _clientMenuDirty = false;

        // v3.22.53: PRESERVE the admin items at the top (Force-Kill submenu,
        // Detach item, separator). They're cached fields whose handlers (and
        // _detachItem reference used by RefreshDetachMenuState) must outlive
        // the rebuild. Only dispose entries below ClientsMenuAdminItemCount.
        //
        // Dispose old per-client items to prevent GDI/memory leaks
        // (called on every client change).
        for (int i = _clientsMenu.DropDownItems.Count - 1; i >= ClientsMenuAdminItemCount; i--)
        {
            var item = _clientsMenu.DropDownItems[i];
            _clientsMenu.DropDownItems.RemoveAt(i);
            item.Dispose();
        }

        var clients = _processManager.Clients;
        if (clients.Count == 0)
        {
            _clientsMenu.DropDownItems.Add("(no clients detected)");
            _clientsMenu.DropDownItems.Add(new ToolStripSeparator());
            _clientsMenu.DropDownItems.Add("\uD83D\uDD04  Refresh", null, (_, _) =>
            {
                _processManager.RefreshClients();
                ShowBalloon($"Found {_processManager.ClientCount} EQ client(s)");
            });
            return;
        }

        foreach (var client in clients)
        {
            var c = client; // capture for closure
            // v3.22.68: was `{client}` → ToString() → "Client N (PID: X)". Now
            // surfaces the resolved character name (EQClient.DisplayName parses
            // "EverQuest - <Name>" out of OriginalTitle, falls back to the
            // autologin BoundCharacterName, then to the placeholder). PID kept
            // alongside the name so the submenu remains useful for picking the
            // right client to switch to when two share a character name.
            var item = new ToolStripMenuItem($"[{client.SlotIndex + 1}] {client.DisplayName}  (PID {client.ProcessId})");
            item.Click += (_, _) => _windowManager.SwitchToClient(c, _autoLoginManager.IsLoginActive);
            _clientsMenu.DropDownItems.Add(item);
        }

        // Separator + Refresh at bottom
        _clientsMenu.DropDownItems.Add(new ToolStripSeparator());
        _clientsMenu.DropDownItems.Add("\uD83D\uDD04  Refresh", null, (_, _) =>
        {
            _processManager.RefreshClients();
            ShowBalloon($"Found {_processManager.ClientCount} EQ client(s)");
        });
    }

    private void UpdateTrayText()
    {
        if (_trayIcon == null) return;
        int count = _processManager.ClientCount;
        _trayIcon.Text = $"EQSwitch - {count} client{(count != 1 ? "s" : "")}";
    }

    /// <summary>
    /// v3.22.53 — When the Clients submenu opens, pre-select the first
    /// per-client entry (or the Refresh row if no clients) instead of the
    /// Force-Kill admin item at the top. Force-Kill is recovery; the routine
    /// action is "pick a client" — so up/down keys should traverse clients
    /// immediately and Enter should fire SwitchToClient by default.
    ///
    /// Fires from <c>_clientsMenu.DropDownOpened</c>. We use DropDownOpened
    /// (not DropDownOpening) so the items have actually been laid out and
    /// are selectable. Items below <see cref="ClientsMenuAdminItemCount"/>
    /// are skipped — that's where the admin items sit.
    /// </summary>
    private void SelectClientsMenuDefault()
    {
        if (_clientsMenu == null) return;
        var items = _clientsMenu.DropDownItems;
        for (int i = ClientsMenuAdminItemCount; i < items.Count; i++)
        {
            // First selectable, non-separator, enabled item wins. The
            // "(no clients detected)" placeholder is a plain ToolStripMenuItem
            // and IS selectable — that's fine; selecting it puts focus in the
            // right region of the submenu and the user can arrow down to
            // Refresh which is what they want next.
            if (items[i] is ToolStripMenuItem mi && mi.Enabled && mi.CanSelect)
            {
                mi.Select();
                return;
            }
        }
    }

    /// <summary>
    /// Lazily fills the Force-Kill submenu with the current eqgame.exe PIDs.
    /// Called on DropDownOpening so the list is always current.
    /// </summary>
    private void PopulateForceKillMenu(ToolStripMenuItem menu)
    {
        // Dispose old items in reverse — same pattern as UpdateClientMenu
        for (int i = menu.DropDownItems.Count - 1; i >= 0; i--)
        {
            var old = menu.DropDownItems[i];
            menu.DropDownItems.RemoveAt(i);
            old.Dispose();
        }

        var procs = Process.GetProcessesByName("eqgame");
        try
        {
            if (procs.Length == 0)
            {
                var empty = new ToolStripMenuItem("(no eqgame.exe running)") { Enabled = false };
                menu.DropDownItems.Add(empty);
                return;
            }

            foreach (var p in procs)
            {
                int pid = p.Id; // capture by value before disposal
                string label;
                try
                {
                    // Hang detection — matches the v3.22.22 ArrangeMultiMonitor
                    // pattern at NativeMethods.cs:131-138. Three options were
                    // considered:
                    //   1. Process.Responding — blocks ~5s per hung proc
                    //   2. IsHungAppWindow — non-blocking BUT has documented 5s
                    //      kernel-threshold latency (false during the first 5s
                    //      of a non-pumping pump → mis-labels fresh hangs)
                    //   3. SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 100ms)
                    //      — blocks 100ms per hung proc, instant true positive,
                    //      no 5s warm-up
                    // (3) wins: 100ms × 6 hung procs = 0.6s worst case, well
                    // under 1s, no false negatives for sub-5s hangs. Cached
                    // p.MainWindowHandle access (per-Process cache, populated
                    // by an EnumWindows walk on first read — fast on
                    // responsive processes, non-blocking on the target's pump).
                    IntPtr hwnd = p.MainWindowHandle;
                    bool hung = false;
                    if (hwnd != IntPtr.Zero)
                    {
                        IntPtr smRes = NativeMethods.SendMessageTimeout(
                            hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero,
                            NativeMethods.SMTO_ABORTIFHUNG, 100, out _);
                        hung = smRes == IntPtr.Zero;
                        if (hung)
                        {
                            // v3.22.27 Item 4: disambiguate timed-out vs hung-aborted in
                            // the log. ERROR_TIMEOUT (1460) = target's pump didn't respond
                            // within 100ms (slow but maybe alive). Other (e.g.
                            // ERROR_INVALID_WINDOW_HANDLE 1400) = SMTO_ABORTIFHUNG fired
                            // because the kernel marked the target hung, OR the hwnd
                            // died between MainWindowHandle and the call. User-facing
                            // tray label stays "HUNG" for both — same Force-Kill intent —
                            // but the log preserves the distinction for post-mortem.
                            //
                            // SMTO_BLOCK intentionally NOT added (WindowManager precedent
                            // uses it for synchronous arrange; this is a tray-menu
                            // DropDownOpening handler — blocking the UI thread on N hung
                            // clients would freeze the menu open).
                            int lastErr = Marshal.GetLastWin32Error();
                            string errClass = lastErr switch
                            {
                                1460 => "ERROR_TIMEOUT",
                                1400 => "ERROR_INVALID_WINDOW_HANDLE",
                                _ => $"err{lastErr}"
                            };
                            FileLogger.Info($"PopulateForceKillMenu: PID {pid} flagged hung via SendMessageTimeout (lastErr={errClass})");
                        }
                    }
                    if (hung)
                    {
                        label = $"⚠ HUNG — PID {pid}";
                    }
                    else
                    {
                        string title = string.IsNullOrEmpty(p.MainWindowTitle) ? "(no window title)" : p.MainWindowTitle;
                        label = $"PID {pid} — {title}";
                    }
                }
                catch (Exception ex)
                {
                    // Loud over silent: log so any unexpected failure mode
                    // (Win32Exception from MainWindowHandle on protected
                    // processes, etc.) surfaces in eqswitch.log instead of
                    // silently mis-labeling.
                    FileLogger.Warn($"PopulateForceKillMenu: PID {pid} info unavailable — {ex.Message}");
                    label = $"PID {pid} — (info unavailable)";
                }
                var item = new ToolStripMenuItem(label);
                item.Click += (_, _) => ForceKillClient(pid, label);
                menu.DropDownItems.Add(item);
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    /// <summary>
    /// Force-kill an eqgame.exe by PID. Defends against two races:
    /// (a) the process exiting between menu-open and click (ArgumentException)
    /// (b) Windows reusing the PID for a different process between menu-open
    ///     and click — checked via ProcessName before Kill() so we never
    ///     accidentally terminate svchost / explorer / EQSwitch itself.
    /// </summary>
    private void ForceKillClient(int pid, string label)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // PID-reuse defense (verifier-flagged 2026-05-22 by T2 + T3 pairs).
            // Refuse to kill anything that isn't an eqgame.exe — Windows recycles
            // PIDs aggressively and a slow menu-click can land on a reused PID.
            if (!string.Equals(p.ProcessName, "eqgame", StringComparison.OrdinalIgnoreCase))
            {
                FileLogger.Warn($"ForceKill: PID {pid} was reused by '{p.ProcessName}' — refusing to kill ({label})");
                ShowBalloon($"⚠ PID {pid} is now {p.ProcessName} (not eqgame). Refused — re-open menu.");
                return;
            }
            p.Kill();
            FileLogger.Info($"ForceKill: killed PID {pid} ({label})");
            ShowBalloon($"Force-killed PID {pid}");
        }
        catch (ArgumentException)
        {
            // GetProcessById throws ArgumentException when PID no longer exists —
            // benign race: the process exited between menu-open and click.
            FileLogger.Info($"ForceKill: PID {pid} already exited");
            ShowBalloon($"PID {pid} already exited");
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"ForceKill: failed to kill PID {pid} — {ex.Message}");
            ShowBalloon($"⚠ Force-kill failed for PID {pid}: {ex.Message}");
        }
    }

    private void TogglePip()
    {
        FileLogger.Info($"TogglePip: called, clients={_processManager.Clients.Count}, overlay={_pipOverlay != null}, enabled={_config.Pip.Enabled}");

        // Toggle the enabled state
        _config.Pip.Enabled = !_config.Pip.Enabled;
        ConfigManager.Save(_config);

        if (!_config.Pip.Enabled)
        {
            // Disable — destroy overlay if showing
            if (_pipOverlay != null && !_pipOverlay.IsDisposed)
            {
                _pipOverlay.Close();
                _pipOverlay.Dispose();
                _pipOverlay = null;
            }
            ShowBalloon("PiP overlay disabled");
        }
        else
        {
            // Enable — create overlay if clients exist, otherwise auto-show will handle it later
            var clients = _processManager.Clients;
            if (clients.Count >= 1)
            {
                _pipOverlay = new PipOverlay(_config);
                _pipOverlay.Show();
                _pipOverlay.UpdateSources(clients, _processManager.GetActiveClient());
            }
            ShowBalloon("PiP overlay enabled");
        }

        // Refresh tray menu so the Picture-in-Picture item's checkbox emoji (\u2705 vs \u2B1C)
        // reflects the new state. All call paths benefit: hotkey (ExecuteTrayAction), middle-click
        // (TrayClick.TogglePiP), menu click, and the auto-show path on client list change.
        BuildContextMenu();
    }

    // Reusable deferred timer for ShowBalloon/ShowHelpTooltip — avoids allocating
    // a new Timer + closure on every call. Queued message overwrites any pending one.
    private System.Windows.Forms.Timer? _deferTimer;
    private Action? _deferredAction;

    private void DeferToNextTick(Action action)
    {
        _deferredAction = action;
        if (_deferTimer == null)
        {
            _deferTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _deferTimer.Tick += OnDeferTimerTick;
        }
        else
        {
            _deferTimer.Stop();
        }
        _deferTimer.Start();
    }

    private void OnDeferTimerTick(object? sender, EventArgs e)
    {
        _deferTimer!.Stop();
        _deferredAction?.Invoke();
        _deferredAction = null;
    }

    private void ShowBalloon(string message)
    {
        if (!_config.ShowTooltips) return;
        if (_config.TooltipDurationMs <= 0) return;
        // Marshal to UI thread if called from a background thread (e.g. FireTeam)
        if (_uiContext != null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => ShowBalloon(message), null);
            return;
        }
        // Defer to next message loop iteration so context menu handlers
        // fully complete before we create the tooltip window.
        DeferToNextTick(() => FloatingTooltip.Show(message, _config.TooltipDurationMs));
    }

    /// <summary>
    /// Adds a client label to the deferred "Lost:" balloon queue and (re)starts
    /// a 1.5s coalesce timer. Burst exits (e.g. user closes both EQ windows in
    /// the same poll cycle, or autologin error path drops two clients) collapse
    /// into a single balloon instead of N separate FloatingTooltip pops that
    /// would each cancel an open ContextMenuStrip. v3.22.72 — replaces the
    /// pre-v3.22.72 immediate ShowBalloon($"Lost: {c}") call site.
    /// </summary>
    // v3.22.73 (T2-Sonnet + T2-Opus convergent gap): hard cap on the queue
    // so a flapping ProcessManager detector (rapid spawn/die loop) can't grow
    // _pendingLostClients without bound. NumClients clamps to 6 in normal use
    // so 20 is generous — picks up the worst-case "user launches a team,
    // closes them all, launches another team, closes those" inside one
    // coalesce window. At-cap, we dispatch immediately instead of waiting
    // for the timer, which surfaces the toast sooner AND empties the list
    // before the next add can push past the cap.
    private const int LostClientsQueueCap = 20;

    private void QueueLostClientBalloon(string clientLabel)
    {
        if (_disposed) return;
        _pendingLostClients.Add(clientLabel);

        // First-use lazy init. UI-thread-only by contract: ClientLost fires
        // from ProcessManager's WinForms.Timer.Tick which is UI-thread, so a
        // single static timer is race-free against itself.
        if (_lostClientsCoalesceTimer == null)
        {
            _lostClientsCoalesceTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _lostClientsCoalesceTimer.Tick += (_, _) => TryDispatchLostClientBalloon();
        }

        // v3.22.75 (T2-Opus + T3-Opus convergent CRITICAL): at hard cap,
        // trim the queue IN-PLACE before dispatching. The v3.22.73 fix
        // called TryDispatchLostClientBalloon which returns without clearing
        // when the menu is open — so during a sustained menu-open + flapping-
        // ProcessManager scenario, the queue would grow past 20 unbounded
        // (cap is "soft" while menu open, real cap is dispatch's clear).
        // v3.22.75 fix: drop the oldest half on cap-hit and TRY dispatch.
        // If dispatch fires (menu closed), great — queue clears. If dispatch
        // defers (menu open), the queue is at least bounded to ~LostClientsQueueCap
        // entries — we lost the oldest 10 labels (acceptable: they coalesce
        // into a single "N clients lost" balloon anyway, label accuracy
        // matters less than not growing unbounded). The cap is now a hard
        // ceiling, not a dispatch trigger.
        if (_pendingLostClients.Count >= LostClientsQueueCap)
        {
            int halfCap = LostClientsQueueCap / 2;
            _pendingLostClients.RemoveRange(0, _pendingLostClients.Count - halfCap);
            TryDispatchLostClientBalloon();
            return;
        }

        // Stop+Start resets the elapsed window — the timer fires 1.5s after
        // the LAST queued client, not 1.5s after the first. Lets a 2-client
        // exit pair (typically 50-300ms apart from the ProcessManager poll's
        // sequential ClientLost invocations) coalesce reliably.
        _lostClientsCoalesceTimer.Stop();
        _lostClientsCoalesceTimer.Start();
    }

    /// <summary>
    /// If the context menu is open, no-ops (the menu's Closed handler will
    /// re-call us). Otherwise drains _pendingLostClients into a single
    /// "Lost: a, b, c" balloon. Called from the coalesce timer tick AND from
    /// the _contextMenu.Closed handler — the two-source dispatch ensures the
    /// balloon eventually surfaces even if the menu was open across the
    /// coalesce window.
    /// </summary>
    private void TryDispatchLostClientBalloon()
    {
        _lostClientsCoalesceTimer?.Stop();
        if (_disposed) return;
        if (_pendingLostClients.Count == 0) return;
        // Menu still open: defer. The _contextMenu.Closed handler will retry.
        // No need to restart the timer here — Closed is guaranteed to fire
        // (ContextMenuStrip lifecycle invariant) and will drain us then.
        if (_contextMenu?.Visible == true) return;

        string msg = _pendingLostClients.Count == 1
            ? $"Lost: {_pendingLostClients[0]}"
            : $"Lost {_pendingLostClients.Count} clients: {string.Join(", ", _pendingLostClients)}";
        _pendingLostClients.Clear();
        ShowBalloon(msg);
    }

    /// <summary>Show a warning tooltip that stays visible longer (5s) regardless of user tooltip setting.</summary>
    private void ShowWarning(string message)
    {
        DeferToNextTick(() => FloatingTooltip.Show(message, 5000));
    }

    /// <summary>
    /// Show a rich help tooltip displaying all configured hotkeys and click actions.
    /// </summary>
    private void ShowHelpTooltip()
    {
        var hk = _config.Hotkeys;
        var tc = _config.TrayClick;
        var lines = new List<string>();

        lines.Add("⚔  EQSwitch Hotkeys & Actions  ⚔");
        lines.Add("─────────────────────────────");

        // Keyboard hotkeys
        lines.Add("");
        lines.Add("⌨  KEYBOARD");
        if (!string.IsNullOrEmpty(hk.SwitchKey))
            lines.Add($"  [{hk.SwitchKey}]  Switch client ({(hk.SwitchKeyMode == "swapLast" ? "swap last two" : "cycle all")})");
        if (!string.IsNullOrEmpty(hk.GlobalSwitchKey))
            lines.Add($"  [{hk.GlobalSwitchKey}]  Global switch (focus EQ / cycle)");
        if (!string.IsNullOrEmpty(hk.ArrangeWindows))
            lines.Add($"  [{hk.ArrangeWindows}]  Fix Windows");
        if (!string.IsNullOrEmpty(hk.ToggleMultiMonitor))
            lines.Add($"  [{hk.ToggleMultiMonitor}]  Toggle multi-monitor");
        if (!string.IsNullOrEmpty(hk.LaunchOne))
            lines.Add($"  [{hk.LaunchOne}]  Launch one client");
        if (!string.IsNullOrEmpty(hk.LaunchAll))
            lines.Add($"  [{hk.LaunchAll}]  Launch all clients");

        // Direct switch keys
        for (int i = 0; i < hk.DirectSwitchKeys.Count; i++)
        {
            if (!string.IsNullOrEmpty(hk.DirectSwitchKeys[i]))
                lines.Add($"  [{hk.DirectSwitchKeys[i]}]  Switch to client {i + 1}");
        }

        // Tray click actions
        lines.Add("");
        lines.Add("🖱  TRAY CLICKS");
        AddClickLine(lines, "Left single", tc.SingleClick);
        AddClickLine(lines, "Left double", tc.DoubleClick);
        AddClickLine(lines, "Left triple", tc.TripleClick);
        AddClickLine(lines, "Middle single", tc.MiddleClick);
        AddClickLine(lines, "Middle triple", tc.MiddleDoubleClick);

        // Status
        lines.Add("");
        lines.Add($"📊  {_processManager.ClientCount} client(s) detected");
        if (_config.Affinity.Enabled)
            lines.Add("⚙  CPU affinity: ON");
        var helpText = string.Join("\n", lines);
        DeferToNextTick(() => FloatingTooltip.Show(helpText, _config.TooltipDurationMs * 2));
    }

    private static void AddClickLine(List<string> lines, string label, string action)
    {
        if (action != "None" && !string.IsNullOrEmpty(action))
            lines.Add($"  {label}: {FormatActionName(action)}");
    }

    internal static string FormatActionName(string action) => action switch
    {
        "FixWindows" => "Arrange windows",
        "SwapWindows" => "Swap positions",
        "TogglePiP" => "Toggle PiP",
        "LaunchOne" => "Launch one",
        "LaunchAll" => "Launch all clients",
        "LoginAll"   => "Auto-login Team 1",
        "LoginAll2"  => "Auto-login Team 2",
        "LoginAll3"  => "Auto-login Team 3",
        "LoginAll4"  => "Auto-login Team 4",
        "LoginAll5"  => "Auto-login Team 5",
        "LoginAll6"  => "Auto-login Team 6",
        "LoginAll7"  => "Auto-login Team 7",
        "LoginAll8"  => "Auto-login Team 8",
        "LoginAll9"  => "Auto-login Team 9",
        "LoginAll10" => "Auto-login Team 10",
        "LoginAll11" => "Auto-login Team 11",
        "LoginAll12" => "Auto-login Team 12",
        "AutoLogin1" => "Quick Login 1",
        "AutoLogin2" => "Quick Login 2",
        "AutoLogin3" => "Quick Login 3",
        "AutoLogin4" => "Quick Login 4",
        "Settings" => "Open settings",
        "ShowHelp" => "Show this help",
        _ => action
    };

    private void SetAffinityEnabled(bool enabled)
    {
        _config.Affinity.Enabled = enabled;
        ConfigManager.Save(_config);



        if (enabled)
        {
            _retryTimer?.Stop();
            _retryTimer?.Dispose();
            StartRetryTimer();
            _affinityManager.ForceApplyAffinityRules(_processManager.Clients, _processManager.GetActiveClient());
            ShowBalloon("CPU affinity enabled");
        }
        else
        {
            _retryTimer?.Stop();
            _retryTimer?.Dispose();
            _retryTimer = null;
            ShowBalloon("CPU affinity disabled");
        }
    }

    private SettingsForm? _settingsForm;

    private void ShowSettings(int tabIndex = 0, bool openTeamsDialog = false)
    {
        // Prevent multiple settings windows
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.BringToFront();
            // v3.22.10: if the user reaches us via "Manage Teams..." while Settings
            // was already open, honor the deep-link by surfacing the Configure Teams
            // subwindow too. Previously the openTeamsDialog flag was silently dropped
            // on the BringToFront path.
            if (openTeamsDialog)
            {
                _settingsForm.OpenTeamsDialogNow();
            }
            return;
        }

        // Suspend hotkeys while Settings is open so keys like ] can be typed into fields
        _hotkeyManager.UnregisterAll();
        _keyboardHook.Reset();

        _settingsForm = new SettingsForm(_config, ReloadConfig, tabIndex, ShowProcessManager, UpdateHookConfig, _autoLoginManager, openTeamsDialog);
        // v3.22.78: OnSameNameCollision subscription removed — the "consider
        // renaming for tray-menu clarity" balloon is obsolete now that the
        // tray menu colors Account vs Character rows distinctly (see
        // DarkMenuRenderer Tag-routed coloring + per-segment Teams rendering).
        _settingsForm.FormClosed += (_, _) =>
        {
            bool reopen = _settingsForm?.ReopenAfterClose == true;
            _settingsForm = null;
            _hotkeyManager.UnregisterAll();
            _keyboardHook.Reset();
            RegisterHotkeys();
            if (reopen) OpenSettingsAfterDelay(200);
        };
        _settingsForm.Show();
    }

    // MouseClick and MouseDoubleClick are no longer used — all click detection
    // is handled via MouseUp counting (see Initialize).

    private void EnsureTimer(ref System.Windows.Forms.Timer? timer, int intervalMs, EventHandler handler)
    {
        if (timer == null)
        {
            timer = new System.Windows.Forms.Timer { Interval = intervalMs };
            timer.Tick += handler;
        }
        else
        {
            timer.Stop();
        }
        timer.Start();
    }

    private void OnLeftResolved(object? sender, EventArgs e)
    {
        _leftClickTimer!.Stop();
        int clicks = _leftClickCount;
        _leftClickCount = 0;
        // Guard: a stale Tick can fire after Stop() if it was already queued
        // on the UI message pump. Ignore ticks with zero clicks.
        if (clicks == 0) return;
        string action = clicks >= 3
            ? _config.TrayClick.TripleClick
            : clicks >= 2
                ? _config.TrayClick.DoubleClick
                : _config.TrayClick.SingleClick;
        FileLogger.Info($"TrayClick: resolved {clicks} left click(s) → {action}");
        ExecuteTrayAction(action);
    }

    private void OnMiddleResolved(object? sender, EventArgs e)
    {
        _middleClickTimer!.Stop();
        int clicks = _middleClickCount;
        _middleClickCount = 0;
        // Guard: a stale Tick can fire after Stop() if it was already queued
        // on the UI message pump. Ignore ticks with zero clicks.
        if (clicks == 0) return;
        string action = clicks >= 2
            ? _config.TrayClick.MiddleDoubleClick
            : _config.TrayClick.MiddleClick;
        FileLogger.Info($"TrayClick: resolved {clicks} middle click(s) → {action}");
        ExecuteTrayAction(action);
    }

    private void ExecuteTrayAction(string action)
    {
        // Phase 3.5-A: no tray dispatch while Settings dialog is open. Defense-in-depth
        // against any ReloadConfig-style race that leaves hotkeys registered.
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            FileLogger.Info($"ExecuteTrayAction({action}): suppressed — Settings dialog is open");
            return;
        }

        switch (action)
        {
            case "FixWindows":
                // v3.22.21 round-3 (T3-Sonnet): OnArrangeWindows now emits its
                // own terminal balloon on every exit path ("No EQ clients",
                // "Skipped — all mid-autologin", "Fixed N (mode)(skipped)").
                // Don't double-balloon.
                OnArrangeWindows();
                break;
            case "TogglePiP":
                TogglePip();
                break;
            case "LaunchOne":
                ShowBalloon("Launching client...");
                OnLaunchOne();
                break;
            case "LaunchAll":
                ShowBalloon("Launching all clients...");
                OnLaunchAll();
                break;
            case "AutoLogin1": FireLegacyQuickLoginSlot(1); break;
            case "AutoLogin2": FireLegacyQuickLoginSlot(2); break;
            case "AutoLogin3": FireLegacyQuickLoginSlot(3); break;
            case "AutoLogin4": FireLegacyQuickLoginSlot(4); break;
            case "LoginAll":   FireTeam(1);  break;
            case "LoginAll2":  FireTeam(2);  break;
            case "LoginAll3":  FireTeam(3);  break;
            case "LoginAll4":  FireTeam(4);  break;
            case "LoginAll5":  FireTeam(5);  break;
            case "LoginAll6":  FireTeam(6);  break;
            case "LoginAll7":  FireTeam(7);  break;
            case "LoginAll8":  FireTeam(8);  break;
            case "LoginAll9":  FireTeam(9);  break;
            case "LoginAll10": FireTeam(10); break;
            case "LoginAll11": FireTeam(11); break;
            case "LoginAll12": FireTeam(12); break;
            case "Settings":
                ShowSettings();
                break;
            case "SwapWindows":
                var swapClients = _processManager.Clients;
                if (swapClients.Count >= 2)
                {
                    bool swapMM = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
                    if (swapMM)
                    {
                        // v3.22.20: tray-menu "Swap Windows" honors the same
                        // slot-rotation path as the SwitchKey hotkey in
                        // multi-monitor mode.
                        // v3.22.21 round-5 (T3-S5 catch): mirror the phase
                        // timing instrumentation from OnSwitchKey / OnGlobalSwitchKey
                        // so the tray-menu path captures the same diagnostic
                        // data when exercised. v3.22.22 reads these logs.
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        RotateMonitorSlots();
                        long tRotate = sw.ElapsedMilliseconds;
                        _windowManager.ArrangeWindows(swapClients, _monitorSlotByPid);
                        long tArrange = sw.ElapsedMilliseconds;
                        UpdateHookConfig();
                        long tHook = sw.ElapsedMilliseconds;
                        FileLogger.Info($"TraySwap-swap-timing: rotate={tRotate}ms, arrange={tArrange - tRotate}ms, hookConfig={tHook - tArrange}ms, total={tHook}ms");
                    }
                    else
                    {
                        // v3.22.44 r3 (3-way HIGH convergent: T2-Opus Finding 2 + T4-Sonnet
                        // Item 2 + T4-Opus F2): branch on SwapResult so a swap aborted on
                        // iconic clients surfaces a balloon instead of silently no-opping.
                        // Pre-r3 the tray "Swap Windows" action returned void from
                        // SwapWindows; user pressed it with one minimized client and saw
                        // nothing happen, no feedback. Now: AbortedIconic → balloon
                        // "restore manually". Other aborts (hung/not-responsive) stay
                        // silent — they're rare and the user can't act on them.
                        var swapResult = _windowManager.SwapWindows(swapClients);
                        if (swapResult == Core.SwapResult.AbortedIconic)
                        {
                            ShowBalloon("Swap skipped — at least one client is minimized. Restore from the taskbar, then re-press swap.");
                            return;
                        }
                        _windowManager.ResizeToCurrentMonitors(swapClients);
                        UpdateHookConfig();

                        // v3.22.42: SwapWindows + ResizeToCurrentMonitors both
                        // use SWP_NOZORDER, and ResizeToCurrentMonitors targets
                        // work-area bounds (taskbar visible). When SlimTitlebar
                        // is on, the guard timer's next tick re-applies
                        // full-monitor bounds — but with no z-order recovery
                        // the taskbar's WS_EX_TOPMOST keeps slicing EQ's
                        // bottom edge until next focus event. Apply slim
                        // bounds + raise immediately. Same foreground-gating
                        // as the ReloadConfig single-screen branch: don't
                        // yank focus on a background tray-menu invocation.
                        if (_config.Layout.SlimTitlebar)
                        {
                            _windowManager.ApplySlimTitlebarToAll(swapClients, _injectedPids);
                            bool eqAlreadyForeground = _processManager.GetActiveClient() != null;
                            RaiseClientsAboveTaskbar(swapClients, foregroundActive: eqAlreadyForeground);
                            FileLogger.Info("TraySwap: single-screen slim — applied bounds + raised");
                        }
                    }
                }
                break;
            case "RefreshClients":
                _processManager.RefreshClients();
                ShowBalloon($"Found {_processManager.ClientCount} EQ client(s)");
                break;
            case "ShowHelp":
                HelpForm.Show(_config);
                break;
            case "None":
            default:
                break;
        }
    }


    /// <summary>Click handler for Accounts-submenu items. Explicit intent balloon + new API call.</summary>
    private void FireAccountLogin(Account account)
    {
        try
        {
            ShowBalloon($"Logging in {account.EffectiveLabel} \u2014 stopping at charselect");
            _autoLoginManager.LoginToCharselect(account).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var inner = t.Exception!.Flatten().InnerException;
                    FileLogger.Error($"FireAccountLogin async fault: {inner?.GetType().Name}: {inner?.Message}", t.Exception);
                    ShowBalloon($"Login error: {inner?.Message ?? "unknown"}");
                }
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"FireAccountLogin CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
            ShowBalloon($"Login error: {ex.Message}");
        }
    }

    // Per-target stale-fire timestamps so rapid combo mashes don't flood balloons/logs.
    private readonly Dictionary<string, long> _lastStaleFireTicks = new(StringComparer.Ordinal);
    private const int StaleBalloonCooldownMs = 2000;

    /// <summary>
    /// Phase 5a: dispatch entry point for AccountHotkeys[]. Resolves the binding's
    /// TargetName to an Account at fire time — if the Account was deleted between
    /// Save and keypress, surfaces an actionable balloon pointing the user at the
    /// Hotkeys dialog. Balloon + log throttled to once per 2 s per target to avoid
    /// spam from a user mashing a combo. No throw.
    /// </summary>
    private void FireAccountHotkeyByName(string name)
    {
        // Phase 3.5-A parity: no dispatch while Settings is open. Bypasses
        // ExecuteTrayAction so the gate has to be duplicated here.
        if (_settingsForm != null && !_settingsForm.IsDisposed) return;

        // v3.22.29 Orphan-1: snapshot all three _config reads under
        // ConfigMutationLock. Pre-v3.22.29 each line touched a list-typed
        // _config field outside the lock, racing TrayManager.ReloadConfigCore's
        // mid-Settings-Apply swap of those lists. Snapshot-then-release lets
        // the heavy FireAccountLogin / FireCharacterLogin dispatch run lock-free.
        Account? account;
        LoginAccount? legacyRow;
        Character? legacyToCharacter;
        lock (ConfigManager.ConfigMutationLock)
        {
            account = _config.FindAccountByName(name);
            legacyRow = _config.LegacyAccounts.FirstOrDefault(a =>
                a.CharacterName.Equals(name, StringComparison.OrdinalIgnoreCase) && a.AutoEnterWorld);
            legacyToCharacter = legacyRow != null ? _config.FindCharacterByName(name) : null;
        }

        if (account == null)
        {
            var key = "A:" + name;
            long now = Environment.TickCount64;
            if (!_lastStaleFireTicks.TryGetValue(key, out var last) || now - last >= StaleBalloonCooldownMs)
            {
                _lastStaleFireTicks[key] = now;
                ShowBalloon($"Account Hotkey: '{name}' not found. Open Settings \u2192 Hotkeys \u2192 Configure Accounts to rebind.");
                FileLogger.Warn($"AccountHotkey fired for missing target '{name}' — user should rebind in the Account Hotkeys dialog");
            }
            return;
        }

        // Smart-routing bridge: if the user's v3 config had this name as a Character
        // with AutoEnterWorld=true (the pre-v3.10.0 "log me in and enter world" intent),
        // AND a v4 Character row with this name exists, honor the legacy intent by
        // routing to LoginAndEnterWorld instead of stopping at charselect. This catches
        // the migration glitch where v3 → v4 split created AccountHotkey bindings for
        // names that semantically meant "enter world as character X". User can override
        // by explicitly rebinding in Settings → Hotkeys → Account vs Character family.
        if (legacyRow != null)
        {
            var character = legacyToCharacter;
            if (character != null)
            {
                var key = "A2C:" + name;
                long now = Environment.TickCount64;
                if (!_lastStaleFireTicks.TryGetValue(key, out var last) || now - last >= StaleBalloonCooldownMs)
                {
                    _lastStaleFireTicks[key] = now;
                    FileLogger.Info($"AccountHotkey '{name}' auto-routed to CharacterHotkey (legacy AutoEnterWorld=true + v4 Character exists) — rebind in Settings to make this explicit");
                }
                FireCharacterLogin(character);
                return;
            }
        }

        FireAccountLogin(account);
    }

    /// <summary>Click handler for Characters-submenu items. Explicit intent balloon + new API call.</summary>
    private void FireCharacterLogin(Character character)
    {
        try
        {
            ShowBalloon($"Logging in {character.EffectiveLabel} \u2014 entering world");
            _autoLoginManager.LoginAndEnterWorld(character).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var inner = t.Exception!.Flatten().InnerException;
                    FileLogger.Error($"FireCharacterLogin async fault: {inner?.GetType().Name}: {inner?.Message}", t.Exception);
                    ShowBalloon($"Login error: {inner?.Message ?? "unknown"}");
                }
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"FireCharacterLogin CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
            ShowBalloon($"Login error: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 5a: dispatch entry point for CharacterHotkeys[]. Same null-guard pattern
    /// as FireAccountHotkeyByName — if the Character was deleted between Save and
    /// keypress, balloon points the user at the Hotkeys dialog.
    /// </summary>
    private void FireCharacterHotkeyByName(string name)
    {
        if (_settingsForm != null && !_settingsForm.IsDisposed) return;   // Phase 3.5-A parity

        // v3.22.29 Orphan-1: snapshot under ConfigMutationLock. Same fix-class
        // as FireAccountHotkeyByName.
        Character? character;
        lock (ConfigManager.ConfigMutationLock)
        {
            character = _config.FindCharacterByName(name);
        }
        if (character == null)
        {
            var key = "C:" + name;
            long now = Environment.TickCount64;
            if (!_lastStaleFireTicks.TryGetValue(key, out var last) || now - last >= StaleBalloonCooldownMs)
            {
                _lastStaleFireTicks[key] = now;
                ShowBalloon($"Character Hotkey: '{name}' not found. Open Settings \u2192 Hotkeys \u2192 Configure Characters to rebind.");
                FileLogger.Warn($"CharacterHotkey fired for missing target '{name}' — user should rebind in the Character Hotkeys dialog");
            }
            return;
        }
        FireCharacterLogin(character);
    }

    /// <summary>
    /// Phase-3 dispatcher for legacy tray-click/hotkey <c>AutoLoginN</c> actions. Reads
    /// QuickLoginN, resolves the v3 LegacyAccount row to recover the original intent
    /// (the v3 <c>AutoEnterWorld</c> flag is the source of truth), then routes to
    /// FireAccountLogin or FireCharacterLogin using the NEW API. Phase 5 replaces this
    /// with the AccountHotkeys[]/CharacterHotkeys[] family tables which encode intent
    /// in the binding family — no LegacyAccount consultation needed.
    ///
    /// Why not Character-first-by-name? Because the same name often exists as both
    /// an Account.Name and a Character.Name (e.g. "natedogg" is both a v4 Account and
    /// a v4 Character in typical single-character-per-account configs). Character-first
    /// would always enter-world, regressing users whose v3 row had AutoEnterWorld=false.
    /// </summary>
    private void FireLegacyQuickLoginSlot(int slot)
    {
        if (slot < 1 || slot > 4)
        {
            FileLogger.Warn($"FireLegacyQuickLoginSlot: slot {slot} out of range (expected 1-4)");
            return;
        }
        string targetName = slot switch
        {
            1 => _config.QuickLogin1,
            2 => _config.QuickLogin2,
            3 => _config.QuickLogin3,
            4 => _config.QuickLogin4,
            _ => throw new UnreachableException($"slot {slot} passed guard but hit switch default")
        };
        if (string.IsNullOrEmpty(targetName))
        {
            // v4 fallback: walk CharacterHotkeys ∪ AccountHotkeys (populated only)
            // and fire the slot-th entry. Lets users with no v3 QuickLogin slots
            // still bind tray AutoLoginN to their v4 hotkey-list entries.
            if (TryFireV4QuickLoginFallback(slot)) return;

            // Always log — diagnostic trail for empty-slot fires. Balloon is rate-limited
            // to avoid tray-notification spam if a user holds down or rapidly repeats the hotkey.
            // First press ALWAYS balloons (rate-limit only kicks in on rapid repeat).
            FileLogger.Info($"FireLegacyQuickLoginSlot: slot {slot} fired but QuickLogin{slot} empty AND no v4 hotkey-list entry at index {slot}");
            if (!ShouldSuppressEmptySlotBalloon(slot))
                ShowBalloon($"Quick Login {slot}: no account assigned");
            return;
        }

        // Find the v3 LegacyAccount row so we can honor its AutoEnterWorld intent.
        // v3 matched CharacterName first, Username as fallback — preserve that order.
        // v3.22.27 Item 1: each LINQ query wrapped in ConfigMutationLock for
        // torn-read protection. Lock released before heavy Fire*Login dispatch.
        LoginAccount? legacyRow;
        lock (ConfigManager.ConfigMutationLock)
        {
            legacyRow = _config.LegacyAccounts.FirstOrDefault(a => a.CharacterName == targetName)
                    ?? _config.LegacyAccounts.FirstOrDefault(a => a.Username == targetName);
        }
        if (legacyRow == null)
        {
            // v4 fallback for non-empty v3 slot whose target doesn't match any
            // LegacyAccount row (post-migration drift). Try v4 Character.Name
            // first (enter-world intent), then v4 Account.Name (charselect).
            // Case-insensitive on this fallback ONLY: the v3 QuickLoginN strings
            // were entered freehand and can drift in case from the v4 entity Name.
            // (The v4 hotkey-list path stays ordinal — TargetName there is set
            // by dialogs using the entity's exact Name, so no drift to forgive.)
            Character? v4Char;
            lock (ConfigManager.ConfigMutationLock)
            {
                v4Char = _config.Characters.FirstOrDefault(c =>
                    c.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            }
            if (v4Char != null)
            {
                LogFirstFire(slot, "Character (v3-target / v4-resolved)", v4Char.EffectiveLabel);
                FireCharacterLogin(v4Char);
                return;
            }
            Account? v4Account;
            lock (ConfigManager.ConfigMutationLock)
            {
                v4Account = _config.Accounts.FirstOrDefault(a =>
                    a.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            }
            if (v4Account != null)
            {
                LogFirstFire(slot, "Account (v3-target / v4-resolved)", v4Account.EffectiveLabel);
                FireAccountLogin(v4Account);
                return;
            }

            ShowBalloon($"Quick Login {slot}: '{targetName}' not found");
            FileLogger.Warn($"Legacy QuickLogin{slot}: target '{targetName}' does not resolve to any LegacyAccount row OR v4 Character/Account");
            return;
        }

        // v3 intent: enter world iff the legacy row had AutoEnterWorld=true AND a CharacterName.
        // Otherwise stop at charselect.
        bool enterWorld = legacyRow.AutoEnterWorld && !string.IsNullOrEmpty(legacyRow.CharacterName);

        if (enterWorld)
        {
            // v3.22.27 R1 (T3-Opus convergent): FindCharacterByName iterates
            // _config.Characters internally — same fix-class as the three
            // FirstOrDefault locks above. Lock releases before FireCharacterLogin.
            Character? character;
            lock (ConfigManager.ConfigMutationLock)
            {
                character = _config.FindCharacterByName(legacyRow.CharacterName);
            }
            if (character != null)
            {
                LogFirstFire(slot, "Character", character.EffectiveLabel);
                FireCharacterLogin(character);
                return;
            }
            FileLogger.Warn($"Legacy QuickLogin{slot}: legacy row '{legacyRow.Name}' wants enter-world but no v4 Character named '{legacyRow.CharacterName}' found — falling back to charselect");
        }

        // Account-only path (or enter-world intent with missing v4 Character — fail safe to charselect).
        // Look up the v4 Account by (Username, Server) rather than Name — migration dedup means
        // multiple legacy rows (e.g. "backup" + "acpots" sharing Username "gotquiz") can collapse
        // into one v4 Account whose Name matches only the first-seen legacy row. Name-based lookup
        // would miss subsequent legacy rows; AccountKey-based lookup catches them all.
        var accountKey = new AccountKey(legacyRow.Username, legacyRow.Server);
        // v3.22.27 R1 (originally deferred to v3.22.28 in CHANGELOG, folded back
        // in per DO-over-DEFER directive): same single-line fix-class as the
        // four locks above.
        Account? account;
        lock (ConfigManager.ConfigMutationLock)
        {
            account = _config.Accounts.FirstOrDefault(a => accountKey.Matches(a));
        }
        if (account != null)
        {
            LogFirstFire(slot, "Account", account.EffectiveLabel);
            FireAccountLogin(account);
            return;
        }

        ShowBalloon($"Quick Login {slot}: v4 data missing for '{targetName}' (migration issue?)");
        FileLogger.Warn($"Legacy QuickLogin{slot}: resolved legacy row '{legacyRow.Name}' (key {accountKey}) but no v4 Account matches");
    }

    /// <summary>
    /// v4 fallback for tray AutoLogin{N}: walks CharacterHotkeys then AccountHotkeys
    /// (populated entries only), positionally indexed by slot. Characters first because
    /// they enter world (higher-intent action); accounts after charselect-only.
    /// Returns true if a binding was found and dispatched (caller skips empty-slot balloon).
    /// </summary>
    private bool TryFireV4QuickLoginFallback(int slot)
    {
        // v3.22.27 R1 (T2-Sonnet + T2-Opus convergent): snapshot the combined
        // bindings list + the resolved entity lookups under the lock, then
        // dispatch outside. Same pattern as FireLegacyQuickLoginSlot's
        // three-tiny-locks. Re-entrant on the ApplySettings → BuildContextMenu
        // call-path (which already holds the lock).
        List<(HotkeyBinding Binding, bool IsCharacter)> combined;
        lock (ConfigManager.ConfigMutationLock)
        {
            var hk = _config.Hotkeys;
            combined = hk.CharacterHotkeys.Select(b => (Binding: b, IsCharacter: true))
                .Concat(hk.AccountHotkeys.Select(b => (Binding: b, IsCharacter: false)))
                .Where(t => HotkeyBindingUtil.IsPopulated(t.Binding))
                .ToList();
        }
        if (slot < 1 || slot > combined.Count) return false;

        var (binding, isCharacter) = combined[slot - 1];
        if (isCharacter)
        {
            Character? character;
            lock (ConfigManager.ConfigMutationLock)
            {
                character = _config.FindCharacterByName(binding.TargetName);
            }
            if (character != null)
            {
                LogFirstFire(slot, "Character (v4 fallback)", character.EffectiveLabel);
                FireCharacterLogin(character);
                return true;
            }
        }
        else
        {
            Account? account;
            lock (ConfigManager.ConfigMutationLock)
            {
                account = _config.Accounts.FirstOrDefault(a =>
                    a.Name.Equals(binding.TargetName, StringComparison.OrdinalIgnoreCase));
            }
            if (account != null)
            {
                LogFirstFire(slot, "Account (v4 fallback)", account.EffectiveLabel);
                FireAccountLogin(account);
                return true;
            }
        }

        // Stale binding — TargetName doesn't resolve to any v4 entity. Surface it
        // explicitly rather than silently falling through to "no account assigned".
        ShowBalloon($"Quick Login {slot}: hotkey target '{binding.TargetName}' is stale (deleted)");
        FileLogger.Warn($"v4 fallback for slot {slot}: TargetName '{binding.TargetName}' (kind={(isCharacter ? "Character" : "Account")}) does not resolve");
        return true;  // we DID handle it (with an error balloon) — caller shouldn't double-balloon.
    }

    // Empty-slot balloon rate-limiter — tracks last fire per slot. Guards against
    // hotkey repeat/spam triggering N tray notifications in rapid succession.
    private readonly Dictionary<int, DateTime> _emptySlotLastFired = new();
    private const int EmptySlotBalloonCooldownSeconds = 3;

    private bool ShouldSuppressEmptySlotBalloon(int slot)
    {
        var now = DateTime.UtcNow;
        if (_emptySlotLastFired.TryGetValue(slot, out var last) &&
            (now - last).TotalSeconds < EmptySlotBalloonCooldownSeconds)
        {
            return true;
        }
        _emptySlotLastFired[slot] = now;
        return false;
    }

    /// <summary>
    /// One-shot-per-slot-per-session log line documenting where a legacy QuickLoginN hotkey
    /// routed through the new API. Surfaced on first fire of each slot so the user can audit
    /// the routing before Phase 5 replaces the QuickLoginN scheme with AccountHotkeys[]/CharacterHotkeys[].
    /// </summary>
    private void LogFirstFire(int slot, string family, string label)
    {
        if (_legacySlotDeprecationLogged.Add(slot))
        {
            FileLogger.Info($"Legacy QuickLogin{slot} routed via new API \u2192 {family} '{label}' (this mapping moves to {family}Hotkeys in Phase 5)");
        }
    }

    /// <summary>
    /// Resolves <c>_config.Team{N}*</c> fields into the tuple shape FireTeam needs.
    /// Pure function over <c>_config</c>; called from FireTeam and from BuildTeamsSubmenu
    /// tooltip rendering. Per-team Enter World flag was removed — destination is now
    /// dictated by slot kind alone (Character → enters world, Account → charselect).
    /// </summary>
    private (IReadOnlyList<(string user, string slotLabel)> slots, string teamName)
        ResolveTeamConfig(int teamIndex)
    {
        if (teamIndex < 1 || teamIndex > 12)
        {
            FileLogger.Warn($"ResolveTeamConfig: teamIndex {teamIndex} out of range (expected 1-12)");
            return (Array.Empty<(string, string)>(), $"Team {teamIndex}");
        }
        return teamIndex switch
        {
            1  => (new[] { (_config.Team1Account1,  "Team 1 Slot 1"),  (_config.Team1Account2,  "Team 1 Slot 2")  }, "Team 1"),
            2  => (new[] { (_config.Team2Account1,  "Team 2 Slot 1"),  (_config.Team2Account2,  "Team 2 Slot 2")  }, "Team 2"),
            3  => (new[] { (_config.Team3Account1,  "Team 3 Slot 1"),  (_config.Team3Account2,  "Team 3 Slot 2")  }, "Team 3"),
            4  => (new[] { (_config.Team4Account1,  "Team 4 Slot 1"),  (_config.Team4Account2,  "Team 4 Slot 2")  }, "Team 4"),
            5  => (new[] { (_config.Team5Account1,  "Team 5 Slot 1"),  (_config.Team5Account2,  "Team 5 Slot 2")  }, "Team 5"),
            6  => (new[] { (_config.Team6Account1,  "Team 6 Slot 1"),  (_config.Team6Account2,  "Team 6 Slot 2")  }, "Team 6"),
            7  => (new[] { (_config.Team7Account1,  "Team 7 Slot 1"),  (_config.Team7Account2,  "Team 7 Slot 2")  }, "Team 7"),
            8  => (new[] { (_config.Team8Account1,  "Team 8 Slot 1"),  (_config.Team8Account2,  "Team 8 Slot 2")  }, "Team 8"),
            9  => (new[] { (_config.Team9Account1,  "Team 9 Slot 1"),  (_config.Team9Account2,  "Team 9 Slot 2")  }, "Team 9"),
            10 => (new[] { (_config.Team10Account1, "Team 10 Slot 1"), (_config.Team10Account2, "Team 10 Slot 2") }, "Team 10"),
            11 => (new[] { (_config.Team11Account1, "Team 11 Slot 1"), (_config.Team11Account2, "Team 11 Slot 2") }, "Team 11"),
            12 => (new[] { (_config.Team12Account1, "Team 12 Slot 1"), (_config.Team12Account2, "Team 12 Slot 2") }, "Team 12"),
            _ => throw new UnreachableException($"teamIndex {teamIndex} passed guard but hit switch default")
        };
    }

    /// <summary>
    /// Character tray-item tooltip: "→ Account 'Main' · slot auto" or fallback
    /// "username@server (unresolved)" when the FK has drifted.
    /// </summary>
    private static string BuildCharacterTooltip(
        Character character,
        IReadOnlyDictionary<AccountKey, Account> accountsByKey)
    {
        var accountLabel = accountsByKey.TryGetValue(character.AccountKey, out var acc)
            ? acc.EffectiveLabel
            : $"{character.AccountUsername}@{character.AccountServer} (unresolved)";
        var slot = character.CharacterSlot == 0 ? "auto" : character.CharacterSlot.ToString();
        return $"\u2192 Account '{accountLabel}' \u00B7 slot {slot}";
    }

    /// <summary>
    /// Origin of a team-slot label resolution — drives the per-segment color
    /// in the Teams submenu (Account → orange, Character → white). Raw is the
    /// passthrough fallback when neither lookup succeeds; treated visually as
    /// Character (white) since orange is reserved for confirmed Account
    /// identities, not unresolved string fragments.
    /// </summary>
    private enum SlotSource { Account, Character, Raw }

    /// <summary>
    /// Resolve a team slot's stored string (Character.Name or Account.Name) to
    /// the display name used in the Teams submenu label AND the source kind
    /// so the renderer can color Account-resolved names distinctly.
    /// Character.Name preferred — Username fallback for Account-only slots.
    /// Returns ("", Raw) for empty slots so the caller can filter. Mirrors
    /// BuildTeamTooltip's resolution but compact (no arrows).
    /// </summary>
    private (string Name, SlotSource Source) ResolveTeamSlotDisplay(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return ("", SlotSource.Raw);
        // v3.22.29 Orphan-1: snapshot both lookups under ConfigMutationLock.
        // Called from per-team tooltip / submenu builds on UI thread; ReloadConfig
        // swaps Characters/Accounts mid-call would torn-read otherwise.
        Character? ch;
        Account? acc;
        lock (ConfigManager.ConfigMutationLock)
        {
            ch = _config.FindCharacterByName(raw);
            acc = ch == null ? _config.FindAccountByName(raw) : null;
        }
        if (ch != null) return (ch.Name, SlotSource.Character);
        if (acc != null && !string.IsNullOrEmpty(acc.Username)) return (acc.Username, SlotSource.Account);
        return (raw, SlotSource.Raw);
    }

    /// <summary>
    /// Team tray-item tooltip: multi-line per-slot preview of what each slot resolves to.
    /// Destination is per-slot (Character → enters world, Account → charselect) — no
    /// team-level override anymore.
    /// </summary>
    private string BuildTeamTooltip(int teamIndex)
    {
        var (slots, _) = ResolveTeamConfig(teamIndex);
        // v3.22.29 Orphan-1: per-slot lookups under ConfigMutationLock so a
        // ReloadConfig mid-tooltip-build can't torn-read Characters/Accounts.
        // NOTE: lock is acquired per-slot (inside the lambda), not per-call.
        // For typical 4-6 slot teams that's 4-6 Monitor.Enter/Exit cycles —
        // negligible cost (microseconds). Per-slot scoping keeps the lock
        // held only for the FindCharacterByName + FindAccountByName lookup
        // pair, avoiding holding it across the EQSwitch.Models accessor reads
        // (LabelWithClass, EffectiveLabel) that happen after the snapshot.
        string ResolveForTooltip(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "(empty)";
            Character? ch;
            Account? acc;
            lock (ConfigManager.ConfigMutationLock)
            {
                ch = _config.FindCharacterByName(raw);
                acc = ch == null ? _config.FindAccountByName(raw) : null;
            }
            if (ch != null) return $"{ch.LabelWithClass} \u2192 enter world";
            if (acc != null) return $"{acc.EffectiveLabel} \u2192 charselect";
            return $"{raw} (unresolved)";
        }
        var lines = new List<string>();
        for (int i = 0; i < slots.Count; i++)
            lines.Add($"Slot {i + 1}: {ResolveForTooltip(slots[i].user)}");
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Fires all populated slots for the given team in parallel (fire-and-forget via
    /// discard-assignment — NO await inside the foreach loop). Preserves v3 timing
    /// semantics around _activeLoginPids — plan line 371 is emphatic.
    ///
    /// Destination is per-slot, dictated by kind (no team-level override):
    ///   Character slot → LoginAndEnterWorld → enters game world.
    ///   Account slot   → LoginToCharselect  → stops at character select.
    /// Mixed teams just get a mix (Character enters, Account stops). Want a
    /// character to stop at charselect? Put the backing Account in that slot.
    /// </summary>
    private void FireTeam(int teamIndex)
    {
        var (slots, teamName) = ResolveTeamConfig(teamIndex);
        int delayMs = Math.Max(_config.Launch.LaunchDelayMs, 500);

        // Honor Settings -> Video -> Client Launch Delay between team slots so
        // concurrent autologins don't hit Dalaya's auth gate within ~ms of
        // each other (caused "connection error" rejections in dual-box test
        // 2026-04-25 — both BURST 1 submits landed within 31ms). Run on a
        // background thread to keep the UI responsive — each LoginAndEnter*
        // call is itself non-blocking (Task.Run inside).
        _ = Task.Run(async () =>
        {
        try
        {

        int fired = 0;
        bool first = true;
        foreach (var (user, slotLabel) in slots)
        {
            if (string.IsNullOrEmpty(user)) continue;

            if (!first)
            {
                FileLogger.Info($"FireTeam({teamIndex}): waiting {delayMs}ms before next slot (LaunchDelayMs)");
                await Task.Delay(delayMs);
            }
            first = false;

            // Character-first resolve (preferred team-slot content).
            // v3.22.27 R2 (T2-Sonnet + T2-Opus convergent HIGH): this lambda
            // runs on a Task.Run threadpool thread, NOT the UI thread.
            // Between Task.Delay iterations, ApplySettings (UI thread) can
            // swap _config.Characters / _config.Accounts via ReloadConfigCore
            // assignments. Without this lock the threadpool reader sees a
            // torn FirstOrDefault. Same ConfigMutationLock pattern.
            Character? character;
            lock (ConfigManager.ConfigMutationLock)
            {
                character = _config.FindCharacterByName(user);
            }
            if (character != null)
            {
                // Always null override -> Character's default behavior (enter world).
                bool? enterWorldOverride = null;
                FileLogger.Info($"FireTeam({teamIndex}): {slotLabel} '{user}' \u2192 Character '{character.EffectiveLabel}' \u2192 enter world");
                _ = _autoLoginManager.LoginAndEnterWorld(character, enterWorldOverride);  // PARALLEL — no await
                fired++;
                continue;
            }

            // Account-only slot. Always charselect — no character target to enter world with.
            // v3.22.27 R2: same threadpool-thread lock-protection as the
            // Character branch above.
            Account? account;
            lock (ConfigManager.ConfigMutationLock)
            {
                account = _config.FindAccountByName(user);
            }
            if (account != null)
            {
                    FileLogger.Info($"FireTeam({teamIndex}): {slotLabel} '{user}' \u2192 Account '{account.EffectiveLabel}' \u2192 charselect");
                _ = _autoLoginManager.LoginToCharselect(account);  // PARALLEL — no await
                fired++;
                continue;
            }

            FileLogger.Warn($"FireTeam({teamIndex}): {slotLabel} '{user}' not found in Accounts or Characters \u2014 skipping");
        }
        if (fired == 0)
        {
            FileLogger.Warn($"FireTeam: {teamName} has no slots assigned");
            // ShowWarning -> DeferToNextTick -> WinForms.Timer construction MUST
            // happen on the UI thread (timer's hidden window owns the message
            // pump). We're inside Task.Run on threadpool \u2014 marshal explicitly.
            var capturedTeamName = teamName;
            _uiContext?.Post(_ => ShowWarning($"No accounts assigned to {capturedTeamName} \u2014 configure in Settings \u2192 Accounts"), null);
        }
        }
        catch (Exception ex)
        {
            // Synchronous WinForms tray would have surfaced this via the
            // ThreadException handler. The Task.Run wrapper would otherwise
            // swallow it silently \u2014 log + balloon so the user sees the failure.
            FileLogger.Error($"FireTeam({teamIndex}) crashed", ex);
            _uiContext?.Post(_ => ShowWarning($"Team {teamIndex} launch failed: {ex.Message}"), null);
        }
        });  // end Task.Run
    }

    // ─── Config Reload ─────────────────────────────────────────────

    /// <summary>
    /// Re-apply config changes without restarting the app.
    /// Called after the Settings GUI saves new values.
    /// </summary>
    public void ReloadConfig(AppConfig newConfig)
    {
        // v3.22.25: serialize against any in-flight AutoLoginManager SaveImmediate.
        // Without this lock, the ~50 field-by-field mutations in
        // ReloadConfigCore (notably the _config.Accounts list-swap) race with
        // the SM finally-block's JsonSerializer.Serialize(_config), producing
        // torn JSON writes OR orphan-ref LastLoginResult writes that never
        // persist. See ConfigManager.ConfigMutationLock XML doc for full
        // contract. Reentrant on UI thread when called via
        // SettingsForm.ApplySettings (which also takes the lock around its
        // build-newConfig + _onApply call) — C# lock is recursive per thread.
        lock (ConfigManager.ConfigMutationLock)
        {
            FileLogger.Info("ReloadConfig: acquired ConfigMutationLock (blocking any background SaveImmediate)");
            ReloadConfigCore(newConfig);
        }
    }

    private void ReloadConfigCore(AppConfig newConfig)
    {
        // v3.22.25 verifier-round-2 fix: wrap body in try/finally so _reloading
        // is ALWAYS cleared, even if any field-copy or UI-rebuild line throws
        // (e.g. LoadIcon on a bad ICO can throw OOM from GDI+, ArrangeWindows
        // can fail on a stale window handle). Pre-fix a throw would leave
        // _reloading=true permanently, silently dropping every subsequent
        // foreground-debounce timer tick (line 1146 guard) and freezing the
        // app's responsiveness to focus changes until restart.
        _reloading = true;

        // v3.22.79: stop+dispose the lost-client coalesce timer at the top of
        // every reload so any pending balloon doesn't fire mid-reload against
        // a partially-reinitialized manager. The timer is lazily recreated by
        // QueueLostClientBalloon on the next client-loss event. Symmetric with
        // Dispose() at the file foot.
        _lostClientsCoalesceTimer?.Stop();
        _lostClientsCoalesceTimer?.Dispose();
        _lostClientsCoalesceTimer = null;
        _pendingLostClients.Clear();

        try
        {
        // Update the config reference (AppConfig is a class, so updating fields in-place)
        _config.EQPath = newConfig.EQPath;
        _config.EQProcessName = newConfig.EQProcessName;
        _config.Layout.SnapToMonitor = newConfig.Layout.SnapToMonitor;
        _config.Layout.TargetMonitor = newConfig.Layout.TargetMonitor;
        _config.Layout.SecondaryMonitor = newConfig.Layout.SecondaryMonitor;
        _config.Layout.TopOffset = newConfig.Layout.TopOffset;
        // v3.22.80: WindowMode is the user-facing window-style source of truth
        // (SlimTitlebar is derived from it). Propagate it here too — the third
        // of the three required sites (AppConfig field + BuildAppConfig pickup +
        // this ReloadConfigCore propagation) per the dual-propagation bug class.
        // In Phase 1 it's always Fullscreen (Validate clamps Windowed), so this
        // is a no-op today; it's load-bearing the moment Phase 2 wires WindowMode
        // to rendering. See [[reference_settings_apply_dual_propagation_bug]].
        _config.Layout.WindowMode = newConfig.Layout.WindowMode;
        _config.Layout.SlimTitlebar = newConfig.Layout.SlimTitlebar;
        // v3.22.19 BUGFIX (verifier T3 Sonnet): without this, changes to
        // the per-monitor secondary override via Settings → Apply would not
        // take effect until restart. Mirror the SlimTitlebar copy.
        _config.Layout.SlimTitlebarSecondary = newConfig.Layout.SlimTitlebarSecondary;
        _config.Layout.TitlebarOffset = newConfig.Layout.TitlebarOffset;
        // v3.22.54: horizontal nudge propagation (slim-mode 1-px DPI sliver fix).
        // Live-applies on the next slim-titlebar guard-timer tick or foreground hook.
        _config.Layout.HorizontalNudgePx = newConfig.Layout.HorizontalNudgePx;
        // v3.22.53: dark titlebar opt-in. Propagated here so a Settings →
        // Apply round-trip reflects without restart. The slim-titlebar guard
        // timer fires every 500–2000 ms so live clients pick it up on the
        // next tick via ApplySlimTitlebarToAll → ApplySlimTitlebar →
        // ApplyDarkTitlebarIfRequested. See [[reference_settings_apply_dual_propagation_bug]]
        // for why all three sites (AppConfig field, BuildAppConfig pickup,
        // this ReloadConfigCore propagation) are required.
        _config.Layout.DarkTitlebar = newConfig.Layout.DarkTitlebar;
        _config.Layout.BottomOffset = newConfig.Layout.BottomOffset;
        _config.Layout.WindowTitleTemplate = newConfig.Layout.WindowTitleTemplate;
        _config.Layout.Mode = newConfig.Layout.Mode;
        _config.Layout.UseHook = newConfig.Layout.UseHook;
        _config.Affinity.Enabled = newConfig.Affinity.Enabled;
        _config.Affinity.ActivePriority = newConfig.Affinity.ActivePriority;
        _config.Affinity.BackgroundPriority = newConfig.Affinity.BackgroundPriority;
        _config.Affinity.LaunchRetryCount = newConfig.Affinity.LaunchRetryCount;
        _config.Affinity.LaunchRetryDelayMs = newConfig.Affinity.LaunchRetryDelayMs;
        _config.Hotkeys.SwitchKey = newConfig.Hotkeys.SwitchKey;
        _config.Hotkeys.GlobalSwitchKey = newConfig.Hotkeys.GlobalSwitchKey;
        _config.Hotkeys.ArrangeWindows = newConfig.Hotkeys.ArrangeWindows;
        _config.Hotkeys.ToggleMultiMonitor = newConfig.Hotkeys.ToggleMultiMonitor;
        _config.Hotkeys.LaunchOne = newConfig.Hotkeys.LaunchOne;
        _config.Hotkeys.LaunchAll = newConfig.Hotkeys.LaunchAll;
        _config.Hotkeys.TogglePip = newConfig.Hotkeys.TogglePip;
        // v3.22.52: ShowMenu propagation. Added in v3.22.48 but the propagation
        // line was omitted — same regression class as the v3.15.2 launch-knob
        // and v3.22.26 UseStateMachine bugs documented at line 3520+. Without
        // this, BuildAppConfig correctly carries the new value into newConfig
        // but the live _config keeps the old value, then ConfigManager.Save
        // serializes _config to disk → user's hotkey edit silently reverts on
        // reopen of Settings. Lesson: every new HotkeyConfig field needs an
        // entry in BOTH BuildAppConfig AND here.
        _config.Hotkeys.ShowMenu = newConfig.Hotkeys.ShowMenu;
        // Phase 5a family tables — sync both, defense-in-depth matching the TogglePip pattern.
        _config.Hotkeys.AccountHotkeys = newConfig.Hotkeys.AccountHotkeys;
        _config.Hotkeys.CharacterHotkeys = newConfig.Hotkeys.CharacterHotkeys;
        _config.Hotkeys.MultiMonitorEnabled = newConfig.Hotkeys.MultiMonitorEnabled;
        _config.Hotkeys.DirectSwitchKeys = newConfig.Hotkeys.DirectSwitchKeys;
        _config.Hotkeys.SwitchKeyMode = newConfig.Hotkeys.SwitchKeyMode;
        _config.Hotkeys.AutoLogin1 = newConfig.Hotkeys.AutoLogin1;
        _config.Hotkeys.AutoLogin2 = newConfig.Hotkeys.AutoLogin2;
        _config.Hotkeys.AutoLogin3 = newConfig.Hotkeys.AutoLogin3;
        _config.Hotkeys.AutoLogin4 = newConfig.Hotkeys.AutoLogin4;
        _config.Hotkeys.TeamLogin1 = newConfig.Hotkeys.TeamLogin1;
        _config.Hotkeys.TeamLogin2 = newConfig.Hotkeys.TeamLogin2;
        _config.Hotkeys.TeamLogin3 = newConfig.Hotkeys.TeamLogin3;
        _config.Hotkeys.TeamLogin4 = newConfig.Hotkeys.TeamLogin4;
        _config.Launch.ExeName = newConfig.Launch.ExeName;
        _config.Launch.Arguments = newConfig.Launch.Arguments;
        // v3.22.53: propagate the new LaunchOne autologin opt-in so Settings →
        // Apply takes effect without restart.
        _config.Launch.DefaultLaunchOneAccount = newConfig.Launch.DefaultLaunchOneAccount;
        _config.Launch.NumClients = newConfig.Launch.NumClients;
        _config.Launch.LaunchDelayMs = newConfig.Launch.LaunchDelayMs;
        _config.Launch.FixDelayMs = newConfig.Launch.FixDelayMs;
        // v3.15.2 (Round 3 verifier T4 catch): the 10 timing tunables added in
        // v3.15.2 must propagate from `newConfig` to the live `_config` here,
        // otherwise SettingsForm Save would persist them to disk but
        // AutoLoginManager keeps reading the pre-Apply values until process
        // restart. Round 1+2 missed this because the cleanup pass focused on
        // serialization (BuildAppConfig) without touching the live-mutate path.
        _config.Launch.WaitTransitionInitialDelayMs = newConfig.Launch.WaitTransitionInitialDelayMs;
        _config.Launch.WaitTransitionSettleMs       = newConfig.Launch.WaitTransitionSettleMs;
        _config.Launch.WaitTransitionPollIntervalMs = newConfig.Launch.WaitTransitionPollIntervalMs;
        _config.Launch.Burst1ActivationSettleMs     = newConfig.Launch.Burst1ActivationSettleMs;
        _config.Launch.Burst1PostSubmitMs           = newConfig.Launch.Burst1PostSubmitMs;
        _config.Launch.Burst2ActivationSettleMs     = newConfig.Launch.Burst2ActivationSettleMs;
        _config.Launch.Burst2PostKeystrokeMs        = newConfig.Launch.Burst2PostKeystrokeMs;
        _config.Launch.PostBurst1WaitMs             = newConfig.Launch.PostBurst1WaitMs;
        _config.Launch.BridgeInitWaitMs             = newConfig.Launch.BridgeInitWaitMs;
        _config.Launch.StaleSessionWaitMs           = newConfig.Launch.StaleSessionWaitMs;
        // v3.22.26 (R1 T3-Sonnet catch + audit-widening sweep): the v3.17.0+
        // JSON-only Launch tunables. BuildAppConfig started carrying these on
        // 2026-05-15 (v3.17.0 timing-knob audit pass) but the propagation
        // block here was never updated — same regression class as the v3.15.2
        // 10-knob catch above. The R1 verifier flagged UseStateMachine
        // specifically; widened to the full batch on inspection because
        // every field added to BuildAppConfig.Launch since v3.17.0 has the
        // same Settings→Apply→clobber-to-default risk path.
        _config.Launch.StaleSessionPollIntervalMs   = newConfig.Launch.StaleSessionPollIntervalMs;
        _config.Launch.ConnectRetryCount            = newConfig.Launch.ConnectRetryCount;
        _config.Launch.PostBurst2QuickFailCheckMs   = newConfig.Launch.PostBurst2QuickFailCheckMs;
        _config.Launch.SkipShmEnterWorldOnDalaya    = newConfig.Launch.SkipShmEnterWorldOnDalaya;
        _config.Launch.SkipNativeWarmup             = newConfig.Launch.SkipNativeWarmup;
        _config.Launch.JoinServerId                 = newConfig.Launch.JoinServerId;
        _config.Launch.UseStateMachine              = newConfig.Launch.UseStateMachine;
        _config.Pip.Enabled = newConfig.Pip.Enabled;
        _config.Pip.SizePreset = newConfig.Pip.SizePreset;
        _config.Pip.CustomWidth = newConfig.Pip.CustomWidth;
        _config.Pip.CustomHeight = newConfig.Pip.CustomHeight;
        _config.Pip.Opacity = newConfig.Pip.Opacity;
        _config.Pip.Orientation = newConfig.Pip.Orientation;
        _config.Pip.ShowBorder = newConfig.Pip.ShowBorder;
        _config.Pip.BorderColor = newConfig.Pip.BorderColor;
        _config.Pip.BorderThickness = newConfig.Pip.BorderThickness;
        _config.Pip.MaxWindows = newConfig.Pip.MaxWindows;
        _config.TrayClick.SingleClick = newConfig.TrayClick.SingleClick;
        _config.TrayClick.DoubleClick = newConfig.TrayClick.DoubleClick;
        _config.TrayClick.TripleClick = newConfig.TrayClick.TripleClick;
        _config.TrayClick.MiddleClick = newConfig.TrayClick.MiddleClick;
        _config.TrayClick.MiddleDoubleClick = newConfig.TrayClick.MiddleDoubleClick;
        _config.GinaPath = newConfig.GinaPath;
        _config.GamparsePath = newConfig.GamparsePath;
        _config.EqLogParserPath = newConfig.EqLogParserPath;
        _config.NotesPath = newConfig.NotesPath;
        _config.DalayaPatcherPath = newConfig.DalayaPatcherPath;
        _config.LegacyCharacterProfiles = newConfig.LegacyCharacterProfiles;
        _config.LegacyAccounts = newConfig.LegacyAccounts;
        // v4 lists swap together with their legacy counterparts so the live config
        // stays in sync after Save → Reload. Phases 3/4 will populate these from UI;
        // Phase 1 just preserves the migration's output.
        _config.Accounts = newConfig.Accounts;
        _config.Characters = newConfig.Characters;
        _config.CharacterAliases = newConfig.CharacterAliases;
        _config.QuickLogin1 = newConfig.QuickLogin1;
        _config.QuickLogin2 = newConfig.QuickLogin2;
        _config.QuickLogin3 = newConfig.QuickLogin3;
        _config.QuickLogin4 = newConfig.QuickLogin4;
        _config.Team1Account1  = newConfig.Team1Account1;
        _config.Team1Account2  = newConfig.Team1Account2;
        _config.Team2Account1  = newConfig.Team2Account1;
        _config.Team2Account2  = newConfig.Team2Account2;
        _config.Team3Account1  = newConfig.Team3Account1;
        _config.Team3Account2  = newConfig.Team3Account2;
        _config.Team4Account1  = newConfig.Team4Account1;
        _config.Team4Account2  = newConfig.Team4Account2;
        _config.Team5Account1  = newConfig.Team5Account1;
        _config.Team5Account2  = newConfig.Team5Account2;
        _config.Team6Account1  = newConfig.Team6Account1;
        _config.Team6Account2  = newConfig.Team6Account2;
        _config.Team7Account1  = newConfig.Team7Account1;
        _config.Team7Account2  = newConfig.Team7Account2;
        _config.Team8Account1  = newConfig.Team8Account1;
        _config.Team8Account2  = newConfig.Team8Account2;
        _config.Team9Account1  = newConfig.Team9Account1;
        _config.Team9Account2  = newConfig.Team9Account2;
        _config.Team10Account1 = newConfig.Team10Account1;
        _config.Team10Account2 = newConfig.Team10Account2;
        _config.Team11Account1 = newConfig.Team11Account1;
        _config.Team11Account2 = newConfig.Team11Account2;
        _config.Team12Account1 = newConfig.Team12Account1;
        _config.Team12Account2 = newConfig.Team12Account2;
        // Team{N}AutoEnter removed — destination dictated by slot kind (Character → enter world,
        // Account → charselect). No per-team override anymore.
        _config.AutoEnterWorld = newConfig.AutoEnterWorld;
        _config.LoginScreenDelayMs = newConfig.LoginScreenDelayMs;
        // v3.15.2 (Round 3 verifier T4 catch): WarmupDwellMs has been consumed by
        // AutoLoginManager since v3.12.0 but was missing from this hand-copy block.
        // Same regression class as the 10 new Launch knobs above.
        _config.WarmupDwellMs = newConfig.WarmupDwellMs;
        _config.TooltipDurationMs = newConfig.TooltipDurationMs;
        _config.ShowTooltips = newConfig.ShowTooltips;
        // v3.15.10: ShowTooltipErrors / MinimizeToTray removed — both were
        // round-tripped here but had no consumer reading them. See AppConfig.cs
        // comment near the deleted properties.
        _config.RunAtStartup = newConfig.RunAtStartup;
        // v3.22.26 (R1 T3-Sonnet catch): LogTrimThresholdMB is set from the
        // Settings → General → Log File Trimming NumericUpDown (SettingsForm
        // line 503/1864) but was never re-read by FileOperations.TrimLogFiles
        // after Apply because this propagation line was missing. Mid-session
        // log-trim threshold changes silently lost until next restart.
        _config.LogTrimThresholdMB = newConfig.LogTrimThresholdMB;
        _config.HotkeysLegacyBannerDismissed = newConfig.HotkeysLegacyBannerDismissed;
        _config.CustomVideoPresets = newConfig.CustomVideoPresets;
        _config.EQClientIni = newConfig.EQClientIni;

        // Update icon if path changed
        var iconChanged = _config.CustomIconPath != newConfig.CustomIconPath;
        _config.CustomIconPath = newConfig.CustomIconPath;
        if (iconChanged && _trayIcon != null)
        {
            var oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = LoadIcon();
            // Dispose AFTER assignment so _trayIcon.Icon never points to a freed handle
            if (oldIcon != null && oldIcon != SystemIcons.Application)
                try { oldIcon.Dispose(); } catch { }
        }

        // Cancel any in-flight launch sequence before reload
        _launchManager.CancelLaunch();

        // Re-register hotkeys if they changed.
        // Phase 3.5-A: when Settings calls ReloadConfig via Apply, global hotkeys
        // must stay suspended until FormClosed re-registers. Otherwise keystrokes
        // into rebind fields fire the old hotkeys mid-edit.
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _hotkeyManager.UnregisterAll();
            _keyboardHook.Reset();
            RegisterHotkeys();
        }

        // Rebuild context menu so hotkey labels and client count reflect new config
        BuildContextMenu();
        UpdateClientMenu();

        // Sync PiP overlay with config
        if (_pipOverlay != null && !_pipOverlay.IsDisposed)
        {
            _pipOverlay.Close();
            _pipOverlay.Dispose();
            _pipOverlay = null;
        }

        if (_config.Pip.Enabled && _processManager.Clients.Count >= 1)
        {
            _pipOverlay = new PipOverlay(_config);
            _pipOverlay.Show();
            _pipOverlay.UpdateSources(_processManager.Clients, _processManager.GetActiveClient());
        }

        // Re-install foreground hook (in case it was lost) and restart retry timer
        StopForegroundHook();
        StartForegroundHook();
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _retryTimer = null;
        StartRetryTimer();

        // Restart slim titlebar guard if clients are already present — StopForegroundHook
        // disposed the old guard and it won't be recreated until the next ClientListChanged.
        if (_config.Layout.SlimTitlebar && _processManager.Clients.Count > 0 && _slimTitlebarGuard == null)
        {
            // v3.22.47: 500 ms always — see the matching change in ClientListChanged
            // around line ~466 for the full rationale (world-load DX reset can resize
            // EQ's window via APIs the hook DLL misses; C# safety net at 500 ms catches
            // it within one tick instead of waiting on foreground event).
            _slimTitlebarGuard = new System.Windows.Forms.Timer { Interval = 500 };
            _slimTitlebarGuard.Tick += (_, _) => SlimTitlebarGuardTick();
            if (!_processManager.Clients.Any(c => _autoLoginManager.IsLoginActive(c.ProcessId)))
                _slimTitlebarGuard.Start();
        }

        // Auto-arrange when multimonitor mode is toggled on
        bool isMultiMon = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        if (isMultiMon && _processManager.Clients.Count > 0)
        {
            // v3.22.20: backfill slot map (same as OnToggleMultiMonitor).
            // v3.22.21: free-slot scan via AssignNextFreeSlot.
            foreach (var c in _processManager.Clients)
            {
                if (!_monitorSlotByPid.ContainsKey(c.ProcessId))
                    AssignNextFreeSlot(c.ProcessId, "backfill on ReloadConfig");
            }
            _windowManager.ArrangeWindows(_processManager.Clients, _monitorSlotByPid);
            FileLogger.Info("ReloadConfig: auto-arranged for multimonitor mode");

            // v3.22.40: taskbar-coverage parity with v3.22.38's ApplyDeferredCosmetics
            // fix and v3.22.39's all-clients correction. ReloadConfig fires when
            // Settings Apply toggles layout while slim is active; ArrangeWindows
            // uses SWP_NOZORDER so the taskbar (WS_EX_TOPMOST) can slice through
            // EQ's bottom edge if it was raised above EQ before the toggle.
            // Foreground-gated like the sibling-close path so a background apply
            // doesn't yank focus from non-EQ apps.
            if (_config.Layout.SlimTitlebar)
            {
                bool eqAlreadyForeground = _processManager.GetActiveClient() != null;
                RaiseClientsAboveTaskbar(_processManager.Clients, foregroundActive: eqAlreadyForeground);
            }
        }
        else if (_config.Layout.SlimTitlebar && _processManager.Clients.Count > 0)
        {
            // v3.22.42: single-screen slim parity with the MM branch above.
            // Settings Apply is the primary user surface for SlimTitlebar;
            // without this branch the guard timer's first tick (500ms or 5s
            // depending on hookActive) re-applies bounds, but no raise
            // follows so the taskbar (WS_EX_TOPMOST) keeps slicing EQ's
            // bottom edge until the next focus event. Apply bounds
            // immediately and raise — same foreground-gating as the MM path.
            // Fires on every non-MM Apply with slim enabled (not strictly
            // "toggle-on") to mirror the MM branch's unconditional fire-when-
            // gated semantic. The bound-apply is cheap: injected clients
            // (typical post-autologin case) hit the injectedPids skip at
            // WindowManager.cs:686 and exit before any geometry work;
            // non-injected clients fall through to the rect-match early-exit
            // at WindowManager.cs:692 (if rect.Top == expectedY).
            _windowManager.ApplySlimTitlebarToAll(_processManager.Clients, _injectedPids);
            bool eqAlreadyForeground = _processManager.GetActiveClient() != null;
            RaiseClientsAboveTaskbar(_processManager.Clients, foregroundActive: eqAlreadyForeground);
            FileLogger.Info("ReloadConfig: single-screen slim active — applied bounds + raised");
        }

        // Update hook configs for all injected processes (per-PID shared memory
        // supports both single and multimonitor modes)
        UpdateHookConfig();

        FileLogger.Info("Config reloaded and applied");
        ShowBalloon("Settings applied");
        } // try
        finally
        {
            // ALWAYS clear _reloading, even on exception in the field-copy or
            // UI-rebuild blocks above. The C# `lock` in the public ReloadConfig
            // wrapper releases ConfigMutationLock automatically; this finally
            // is for the _reloading flag specifically.
            _reloading = false;
        }
    }

    private void ShowProcessManager()
    {
        if (_processManagerForm != null && !_processManagerForm.IsDisposed)
        {
            _processManagerForm.BringToFront();
            return;
        }

        _processManagerForm = new ProcessManagerForm(
            () => _processManager.Clients,
            () => _processManager.GetActiveClient(),
            () => _affinityManager.ForceApplyAffinityRules(_processManager.Clients, _processManager.GetActiveClient()),
            SetAffinityEnabled,
            _config
        );
        _processManagerForm.FormClosed += (_, _) => _processManagerForm = null;
        _processManagerForm.Show();
    }

    // ─── DLL Hook Injection ─────────────────────────────────────────

    /// <summary>
    /// Inject DLLs into a suspended process before its main thread starts.
    /// Called by LaunchManager and AutoLoginManager via their PreResumeCallback.
    /// </summary>
    private void InjectPreResume(SuspendedProcess sp)
    {
        var exeDir = AppContext.BaseDirectory;

        // 1. Always inject eqswitch-di8.dll (DirectInput hooking for background input)
        var di8Path = Path.Combine(exeDir, "eqswitch-di8.dll");
        if (File.Exists(di8Path))
        {
            if (DllInjector.Inject(sp.Pid, di8Path))
            {
                _di8InjectedPids.Add(sp.Pid);
                RefreshDetachMenuState();  // v3.22.44 r3 — close 10s poll-detection gap
                FileLogger.Info($"PreResume: injected eqswitch-di8.dll into PID {sp.Pid}");
            }
            else
            {
                FileLogger.Warn($"PreResume: eqswitch-di8.dll injection failed for PID {sp.Pid}");
            }
        }
        else
        {
            FileLogger.Warn($"PreResume: eqswitch-di8.dll not found at {di8Path}");
        }

        // 2. Inject eqswitch-hook.dll if any hook feature is enabled (window management)
        if (ShouldInjectHook())
        {
            _hookConfig ??= new HookConfigWriter();
            if (_hookConfig.Open(sp.Pid))
            {
                _injectedPids.Add(sp.Pid);
                RefreshDetachMenuState();  // v3.22.44 r3 — close 10s poll-detection gap
                UpdateHookConfigForPid(sp.Pid);

                var hookPath = Path.Combine(exeDir, "eqswitch-hook.dll");
                if (File.Exists(hookPath))
                {
                    if (DllInjector.Inject(sp.Pid, hookPath))
                    {
                        FileLogger.Info($"PreResume: injected eqswitch-hook.dll into PID {sp.Pid}");
                    }
                    else
                    {
                        _injectedPids.Remove(sp.Pid);
                        RefreshDetachMenuState();  // v3.22.44 r3.6 (4-way convergent rollback gap)
                        FileLogger.Warn($"PreResume: eqswitch-hook.dll injection failed for PID {sp.Pid}");
                    }
                }
            }
            else
            {
                FileLogger.Warn($"PreResume: shared memory unavailable for PID {sp.Pid}");
            }
        }
    }

    /// <summary>
    /// Inject eqswitch-hook.dll into a target EQ process.
    /// Opens per-process shared memory first so the DLL can read config on attach.
    /// Used for manually-launched EQ processes detected by ProcessManager (post-launch).
    /// </summary>
    private void InjectHookDll(int pid)
    {
        if (_injectedPids.Contains(pid)) return;

        // Open per-PID shared memory before injection — the DLL opens "EQSwitchHookCfg_{PID}"
        _hookConfig ??= new HookConfigWriter();
        if (!_hookConfig.Open(pid))
        {
            FileLogger.Warn($"InjectHookDll: shared memory unavailable for PID {pid}, skipping injection");
            return;
        }

        // Track immediately so UpdateHookConfig() includes this PID during the
        // injection delay — shared memory is open and configured, just awaiting DLL load
        _injectedPids.Add(pid);
        RefreshDetachMenuState();  // v3.22.44 r3 — close 10s poll-detection gap

        // Write this process's config before injection so the DLL reads correct values on attach
        UpdateHookConfigForPid(pid);

        // Find the DLL next to our exe
        var exeDir = AppContext.BaseDirectory;
        var dllPath = Path.Combine(exeDir, "eqswitch-hook.dll");
        if (!File.Exists(dllPath))
        {
            FileLogger.Warn($"InjectHookDll: DLL not found at {dllPath}");
            _injectedPids.Remove(pid);
            RefreshDetachMenuState();  // v3.22.44 r3.6 (4-way convergent rollback gap)
            return;
        }

        // Delay injection slightly — EQ needs time to initialize its window
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();

            // Guard: if the process died during the delay, ClientLost already cleaned up
            if (!_injectedPids.Contains(pid)) return;

            if (DllInjector.Inject(pid, dllPath))
            {
                FileLogger.Info($"InjectHookDll: successfully injected into PID {pid}");
            }
            else
            {
                _injectedPids.Remove(pid);
                RefreshDetachMenuState();  // v3.22.44 r3.6 (4-way convergent rollback gap)
                FileLogger.Warn($"InjectHookDll: injection failed for PID {pid}, falling back to guard timer");
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Write slim titlebar positions to shared memory for all injected processes.
    /// In multimonitor mode, each process gets a different position based on its monitor.
    /// <para>
    /// v3.22.21 round-3 (T2-Opus convergence): skip autologin-active PIDs.
    /// `_injectedPids` is populated pre-resume (CREATE_SUSPENDED architecture)
    /// before autologin even starts, so it includes mid-credential-write
    /// clients. Rewriting their hook-config shared memory could push them
    /// onto a different monitor on the next intercepted `SetWindowPos`/
    /// `MoveWindow` — disruptive during DirectInput credential typing. The
    /// gate is symmetrical with `OnArrangeWindows`'s per-client filter.
    /// </para>
    /// <para>
    /// Note: callers of `UpdateHookConfigForPid(pid)` directly bypass this
    /// gate. The gate is an *autologin-contract* concern — direct callers
    /// only need to satisfy the contract if autologin is in flight for the
    /// target PID. Three categories of direct callers exist:
    /// (1) Pre-resume autologin — `InjectPreResume` writes the initial hook
    ///     config while the process is still CREATE_SUSPENDED. EQ's main
    ///     thread hasn't started executing, so there is no DI cooperative-
    ///     level handoff in flight and no `SetWindowPos`/`MoveWindow` for
    ///     the hook to intercept yet. Safe by architecture (process inert).
    /// (2) Post-credentials autologin — `LoginCredentialsSent`/`LoginComplete`
    ///     handlers in `ApplyDeferredCosmetics` fire AFTER credentials are
    ///     sent, when a hook config refresh is the explicit goal and DI
    ///     cred-typing is complete (T+~7 s for LoginCredentialsSent or
    ///     T+~22 s for the 15 s-deferred LoginComplete fire).
    /// (3) Non-autologin (manual launch / externally-launched eqgame) —
    ///     `InjectHookDll` fires from `ClientDiscovered`'s `ShouldInjectHook()`
    ///     branch for eqgame processes EQSwitch didn't launch. Autologin
    ///     isn't running for these PIDs (not in `_activeLoginPids`), so the
    ///     contract is vacuously satisfied — there's no DI sequence to race.
    /// Autologin direct callers OUTSIDE patterns (1) and (2) — i.e. mid-
    /// execution, mid-cred-typing, T+~1.5 s into ClientDiscovered — violate
    /// the contract. v3.22.55 inadvertently added one such caller at
    /// `ClientDiscovered`; v3.22.56 gated it with an explicit
    /// `if (autologinActive)` check at the call site. Verified by post-
    /// v3.22.56 verifier swarm (T2-Opus + T2-Sonnet convergent REJECT).
    /// </para>
    /// </summary>
    private void UpdateHookConfig()
    {
        if (_hookConfig == null || !_hookConfig.HasMappings) return;

        // v3.22.21 round-4 (T3-O4 HIGH): snapshot _injectedPids before
        // iterating. _injectedPids is a HashSet<int>; ClientLost (UI thread)
        // mutates it. Today both this loop and ClientLost run on the UI
        // thread so a mid-iteration mutation can't happen — BUT if
        // _autoLoginManager.IsLoginActive ever pumps messages (e.g. via a
        // balloon, dialog, or future Forms.Idle dispatch) the loop could
        // re-enter and observe a mutated collection. Defensive .ToArray()
        // snapshot makes this safe by construction; cost is one O(N) copy
        // per Fix Windows / ReloadConfig (N ≤ ~6 in practice).
        foreach (var pid in _injectedPids.ToArray())
        {
            if (_autoLoginManager.IsLoginActive(pid))
            {
                FileLogger.Info($"UpdateHookConfig: skipping PID {pid} — autologin in progress");
                continue;
            }
            UpdateHookConfigForPid(pid);
        }
    }

    /// <summary>
    /// Whether any hook feature is currently enabled (slim titlebar + hook,
    /// custom window title, or maximize-on-launch protection).
    /// </summary>
    private bool ShouldInjectHook()
    {
        if (_config.Layout.SlimTitlebar && _config.Layout.UseHook) return true;
        if (!string.IsNullOrEmpty(_config.Layout.WindowTitleTemplate)) return true;
        if (_config.EQClientIni.MaximizeWindow) return true;
        return false;
    }

    /// <summary>
    /// <summary>
    /// v3.22.61: post eqgame.exe's embedded icon to a discovered EQ window
    /// via WM_SETICON. Cached on first call (process-lifetime).
    /// See <see cref="_eqgameWindowIcon"/> for the rationale.
    /// </summary>
    private void ApplyEqgameWindowIcon(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (_eqgameWindowIcon == null)
            {
                var eqPath = _config.EQPath ?? string.Empty;
                if (string.IsNullOrEmpty(eqPath))
                {
                    FileLogger.Warn("ApplyEqgameWindowIcon: _config.EQPath is empty — cannot locate eqgame.exe; skipping");
                    return;
                }
                var exePath = System.IO.Path.Combine(eqPath, "eqgame.exe");
                if (!System.IO.File.Exists(exePath))
                {
                    FileLogger.Warn($"ApplyEqgameWindowIcon: eqgame.exe not found at '{exePath}' — skipping");
                    return;
                }
                _eqgameWindowIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (_eqgameWindowIcon == null)
                {
                    FileLogger.Warn($"ApplyEqgameWindowIcon: ExtractAssociatedIcon returned null for '{exePath}' — skipping");
                    return;
                }
                FileLogger.Info($"ApplyEqgameWindowIcon: cached eqgame.exe icon ({_eqgameWindowIcon.Width}x{_eqgameWindowIcon.Height}) from '{exePath}'");
            }

            // WM_SETICON message constants — not in NativeMethods.cs as none of
            // the other Win32 paths in eqswitch send this message.
            const uint WM_SETICON = 0x0080;
            const int ICON_SMALL = 0; // 16x16 — caption/alt-tab
            const int ICON_BIG   = 1; // 32x32 — taskbar
            // v3.22.62 (verifier T3-Opus HIGH + T2-Sonnet MEDIUM convergent): use
            // SendMessageTimeout with SMTO_ABORTIFHUNG, NOT bare SendMessage.
            // ClientDiscovered can fire while EQ's window message pump is still
            // asleep during DirectX init — bare SendMessage would block the
            // EQSwitch UI thread for the full pump-wakeup interval (up to
            // ~14s observed elsewhere). 500ms timeout is generous for an
            // already-pumping window and short enough that a hung window only
            // burns 1s of UI time (2 messages × 500ms). Same pattern as
            // NativeMethods.cs:143 IsClientResponsive probe. Return value
            // unused — WM_SETICON returns previous HICON which we discard.
            const uint SMTO_ABORTIFHUNG = 0x0002;
            NativeMethods.SendMessageTimeout(hwnd, WM_SETICON, (IntPtr)ICON_BIG,   _eqgameWindowIcon.Handle, SMTO_ABORTIFHUNG, 500, out _);
            NativeMethods.SendMessageTimeout(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, _eqgameWindowIcon.Handle, SMTO_ABORTIFHUNG, 500, out _);
            FileLogger.Info($"ApplyEqgameWindowIcon: posted WM_SETICON to hwnd 0x{hwnd.ToInt64():X}");
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"ApplyEqgameWindowIcon: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Write hook config for a specific process. Handles all hook features:
    /// slim titlebar positioning, window title override, and minimize blocking.
    /// </summary>
    private void UpdateHookConfigForPid(int pid)
    {
        if (_hookConfig == null || !_hookConfig.IsOpen(pid)) return;

        var clients = _processManager.Clients;
        int clientIndex = -1;
        for (int i = 0; i < clients.Count; i++)
        {
            if (clients[i].ProcessId == pid) { clientIndex = i; break; }
        }
        if (clientIndex < 0)
        {
            FileLogger.Warn($"UpdateHookConfigForPid: PID {pid} not in client list, skipping");
            return;
        }

        // ─── Position enforcement ───
        // v3.22.19: per-PID slim flag in multi-monitor mode.
        //   Primary monitor (clientIndex % 2 == 0)    → Layout.SlimTitlebar
        //   Secondary monitor                         → Layout.SlimTitlebarSecondary
        // Single-screen mode always uses Layout.SlimTitlebar (legacy semantics).
        // When the chosen slim flag is FALSE, hook STILL enforces a position —
        // but using work-area bounds (taskbar visible) instead of full monitor.
        // This keeps the secondary window stable against EQ's self-positioning
        // without stripping the frame, matching the v3.22.19 north-star
        // "laptop = normal frame + taskbar visible, main = slim coverage".
        bool isMM = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        bool slimForThisPid;
        if (isMM)
        {
            // v3.22.20: read slot from _monitorSlotByPid (PID-keyed) instead
            // of clientIndex (list-position). Otherwise SwitchKey-driven
            // rotation would re-flip slim assignments per-press as PIDs
            // rotated through slot positions — leaking back to the v3.22.19
            // bouncing pattern.
            int slot = _monitorSlotByPid.TryGetValue(pid, out int s) ? s : (clientIndex % 2);
            bool isPrimaryMonitor = (slot == 0);
            slimForThisPid = isPrimaryMonitor ? _config.Layout.SlimTitlebar : _config.Layout.SlimTitlebarSecondary;
        }
        else
        {
            slimForThisPid = _config.Layout.SlimTitlebar;
        }

        bool posEnabled;
        bool stripFrame;
        int x = 0, y = 0, w = 0, h = 0;

        if (isMM)
        {
            // Multi-monitor: hook enforces position for BOTH clients regardless
            // of slim flag — slim uses full bounds + frame strip, non-slim uses
            // work-area + frame retained.
            // v3.22.20: bounds lookup now goes through GetMonitorForPid /
            // GetWorkAreaForPid (slot-map-aware) so the hook config follows
            // the rotated slot assignment instead of the fixed clientIndex.
            if (slimForThisPid)
            {
                var monBounds = GetMonitorForPid(pid, clientIndex);
                // v3.22.45: route hook-DLL target dims through the same
                // AdjustWindowRectEx-based math as ApplySlimTitlebar so the
                // hook (which forces these exact dims on every EQ-side
                // SetWindowPos call) lands the OUTER rect with the bleed
                // off-screen — visible client area = monitor edge-to-edge,
                // DX swap chain matches client area exactly (no smoosh).
                (x, y, w, h) = _windowManager.ComputeSlimTitlebarOuterRect(monBounds, _config.Layout.TitlebarOffset);
                posEnabled = true;
                stripFrame = true;
            }
            else
            {
                var workArea = GetWorkAreaForPid(pid, clientIndex);
                x = workArea.Left;
                y = workArea.Top + _config.Layout.TopOffset;
                w = workArea.Right - workArea.Left;
                h = (workArea.Bottom - workArea.Top) - _config.Layout.TopOffset;
                posEnabled = true;
                stripFrame = false;
            }
        }
        else
        {
            // Single-screen: unchanged from v3.22.18 — hook only enforces
            // position when slim is on; non-slim leaves EQ to its own INI size.
            posEnabled = slimForThisPid;
            stripFrame = slimForThisPid;
            if (posEnabled)
            {
                var monBounds = _windowManager.GetTargetMonitorBounds();
                // v3.22.45: same Win11-DWM-bleed fix as the multi-monitor
                // branch above. Single-screen hook config now lands the OUTER
                // rect with bleed off-screen so the in-process hook DLL stops
                // EQ from re-sliver'ing the window on every SetWindowPos.
                (x, y, w, h) = _windowManager.ComputeSlimTitlebarOuterRect(monBounds, _config.Layout.TitlebarOffset);
            }
        }

        // ─── Window title ───
        string title = "";
        var template = _config.Layout.WindowTitleTemplate;
        if (!string.IsNullOrEmpty(template))
        {
            // Resolve placeholders
            var client = clients[clientIndex];
            var charName = "";

            // Authoritative source: the name AutoLogin stamped when it launched
            // this PID. Resolves team1Account2="backup" → "backup" instead of
            // LegacyAccounts[1]="flotte" (positional-index mis-mapping).
            if (!string.IsNullOrEmpty(client.BoundCharacterName))
                charName = client.BoundCharacterName;
            else if (clientIndex < _config.LegacyAccounts.Count)
            {
                // Fallback for externally-launched clients (no AutoLogin stamp):
                // chain CharacterName → Username → Name so the slot renders the
                // login identity Nate recognizes; Name is the legacy FK shadow
                // and may hold a custom string on pre-v3.14.8 accounts.
                var acct = _config.LegacyAccounts[clientIndex];
                if (!string.IsNullOrEmpty(acct.CharacterName)) charName = acct.CharacterName;
                else if (!string.IsNullOrEmpty(acct.Username)) charName = acct.Username;
                else if (!string.IsNullOrEmpty(acct.Name)) charName = acct.Name;
            }
            title = template
                .Replace("{CHAR}", charName)
                .Replace("{SLOT}", (clientIndex + 1).ToString())
                .Replace("{PID}", pid.ToString())
                .Trim();
        }

        // ─── Minimize blocking ───
        bool blockMin = _config.EQClientIni.MaximizeWindow;

        // ─── v3.22.81 Windowed-mode geometry pin ───
        // In Windowed mode the hook DLL installs an in-process WndProc subclass
        // (GeoWndProc) that pins the window synchronously per WM message —
        // replacing the C# guard timer's read-modify-write reposition, which
        // raced DWM into runaway growth on a 2nd-monitor boundary + a sliver.
        // Gated on `stripFrame` so only slim-managed windows are pinned (a MM
        // non-slim secondary keeps its normal frame and is NOT subclassed).
        // Fullscreen leaves this 0 → the legacy hook + C# guard path is untouched.
        bool pinGeometry = stripFrame
            && _config.Layout.WindowMode == EQSwitch.Config.WindowMode.Windowed;

        // v3.22.19 BUGFIX (verifier T1+T3 Opus + T3 Sonnet convergence):
        // pre-this-fix the call was `stripThickFrame: posEnabled` which
        // always set the hook's strip-flag true in multi-monitor mode,
        // because posEnabled is true for both slim AND non-slim secondary.
        // Result: WindowManager.ArrangeMultiMonitor would restore
        // WS_THICKFRAME via SetWindowLongPtr, then the hook DLL's next
        // SetWindowPos/MoveWindow interception would re-strip it (per
        // eqswitch-hook.cpp:159-163, 183-187 one-way strip). The local
        // `stripFrame` (set false for non-slim secondary) was computed but
        // never reached the hook. Net effect on secondary: no resize border
        // even though C# tried to restore it — directly defeating the
        // v3.22.19 north star. Fix: pass `stripFrame` (which respects the
        // per-monitor slim flag) instead of `posEnabled`.
        _hookConfig.WriteConfig(pid, x, y, w, h,
            enabled: posEnabled, stripThickFrame: stripFrame,
            blockMinimize: blockMin, windowTitle: title,
            pinGeometry: pinGeometry);

        var features = new System.Collections.Generic.List<string>();
        if (posEnabled) features.Add($"pos=({x},{y}) {w}x{h}");
        features.Add(stripFrame ? "stripFrame=1" : "stripFrame=0");
        if (pinGeometry) features.Add("pinGeometry=1(Windowed subclass)");
        if (!string.IsNullOrEmpty(title)) features.Add($"title=\"{title}\"");
        if (blockMin) features.Add("blockMin");
        FileLogger.Info($"UpdateHookConfig: PID {pid} → {string.Join(", ", features)}");
    }

    /// <summary>
    /// v3.22.84 — slim-titlebar guard tick. Re-applies slim style/position to
    /// NON-injected clients via <see cref="WindowManager.ApplySlimTitlebarToAll"/>
    /// (a no-op for injected clients, whose geometry the hook owns) AND runs the
    /// Windowed frame-measure read-back correction for injected clients
    /// (<see cref="CorrectInjectedWindowedGeometry"/>). Shared by both guard-timer
    /// wiring sites so they stay in lockstep.
    /// </summary>
    private void SlimTitlebarGuardTick()
    {
        _windowManager.ApplySlimTitlebarToAll(_processManager.Clients, _injectedPids);
        CorrectInjectedWindowedGeometry();
    }

    /// <summary>
    /// v3.22.84 — WinEQ2 "measure, don't predict" read-back correction for injected
    /// Windowed clients. The hook applies EQSwitch's per-PID SHM rect VERBATIM, but
    /// that rect is <c>AdjustWindowRectEx</c>-PREDICTED (~8/31/8/8 on Win11) while
    /// eqgame's REAL non-client frame is only ~3/26/3/3 — so the visible client
    /// overshoots the monitor ~5px/edge (live-measured 2026-05-30, char natedogg).
    /// <para>
    /// ApplySlimTitlebarToAll SKIPS injected clients (the hook owns their geometry),
    /// and a static in-world window never repositions itself — so the predicted,
    /// overshooting rect just sits there with nothing to correct it. This measures
    /// each injected Windowed slim client's LIVE frame; when the client overshoots,
    /// it rewrites the SHM rect to the flush-corrected value FIRST (so the hook's
    /// verbatim enforcement / GeoWndProc pin agree) and THEN repositions the window
    /// so it lands on the monitor. Idempotent — a flush window re-measures to the
    /// same constant frame → same rect → no-op (no SetWindowPos). Fullscreen
    /// (WS_POPUP, 0 frame) is gated out inside TryComputeReadbackCorrection.
    /// </para>
    /// </summary>
    private void CorrectInjectedWindowedGeometry()
    {
        if (_hookConfig == null) return;
        if (_config.Layout.WindowMode != EQSwitch.Config.WindowMode.Windowed) return;

        int offset = _config.Layout.TitlebarOffset;
        bool isMM = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        var clients = _processManager.Clients;
        for (int i = 0; i < clients.Count; i++)
        {
            var c = clients[i];
            if (!_injectedPids.Contains(c.ProcessId)) continue;  // hook-managed clients only
            if (!_hookConfig.IsOpen(c.ProcessId)) continue;

            // Resolve the per-PID slim flag + target monitor exactly as
            // UpdateHookConfigForPid does, so we only touch slim-managed windows.
            WinRect monitor;
            if (isMM)
            {
                int slot = _monitorSlotByPid.TryGetValue(c.ProcessId, out int s) ? s : (i % 2);
                bool slim = (slot == 0) ? _config.Layout.SlimTitlebar : _config.Layout.SlimTitlebarSecondary;
                if (!slim) continue;  // non-slim secondary keeps its normal frame — leave it
                monitor = GetMonitorForPid(c.ProcessId, i);
            }
            else
            {
                if (!_config.Layout.SlimTitlebar) continue;
                monitor = _windowManager.GetTargetMonitorBounds();
            }

            if (_windowManager.TryComputeReadbackCorrection(c.WindowHandle, monitor, offset, out var r))
            {
                // SHM FIRST — the hook applies the SHM rect verbatim, so it must hold
                // the corrected value before we move the window, else the hook/GeoWndProc
                // would re-pin the overshooting rect and fight the correction.
                _hookConfig.UpdateRect(c.ProcessId, r.x, r.y, r.w, r.h);
                _windowManager.RepositionWindow(c.WindowHandle, r.x, r.y, r.w, r.h);
                FileLogger.Info($"ReadbackCorrect: PID {c.ProcessId} → flush ({r.x},{r.y}) {r.w}x{r.h} (corrected predicted-frame overshoot)");
            }
        }
    }

    /// <summary>
    /// v3.22.20: rotate per-PID monitor-slot assignments by one. With N PIDs
    /// ordered by ProcessId ascending, PID[i] receives the slot that PID[i+1]
    /// previously held, with PID[N-1] receiving PID[0]'s old slot. Used by
    /// SwitchKey in multi-monitor mode to truly rotate which client owns
    /// which monitor — the next ArrangeWindows + UpdateHookConfig pass then
    /// moves each window to its new slot's monitor, and the hook DLL holds
    /// it there. Stable ordering (by PID) ensures repeated calls follow a
    /// predictable cycle.
    /// </summary>
    private void RotateMonitorSlots()
    {
        if (_monitorSlotByPid.Count < 2) return;
        var pids = _monitorSlotByPid.Keys.OrderBy(p => p).ToList();
        var oldSlots = pids.Select(p => _monitorSlotByPid[p]).ToList();
        for (int i = 0; i < pids.Count; i++)
            _monitorSlotByPid[pids[i]] = oldSlots[(i + 1) % oldSlots.Count];
        FileLogger.Info($"RotateMonitorSlots: {string.Join(", ", pids.Select(p => $"PID {p}→slot {_monitorSlotByPid[p]}"))}");
    }

    /// <summary>
    /// v3.22.21: count of monitor slots the arrange path actually uses
    /// (primary + optional secondary). Mirrors the monitorOrder construction
    /// in WindowManager.ArrangeMultiMonitor — 1 if only one monitor exists,
    /// 2 if 2+ monitors exist. The slot range for a multi-monitor config
    /// is always [0, GetMonitorOrderCount()).
    /// </summary>
    private int GetMonitorOrderCount()
    {
        int count = _windowManager.GetAllMonitorFullBounds().Count;
        if (count <= 0) return 1;
        return count >= 2 ? 2 : 1;
    }

    /// <summary>
    /// v3.22.21: assign the next free monitor slot to <paramref name="pid"/>.
    /// Scans [0, monitorCount) for the first slot not present in
    /// <c>_monitorSlotByPid.Values</c>. Replaces the v3.22.20 <c>Count</c>-based
    /// assignment which collided on PID-recycle (PID A→slot 0, PID B→slot 1;
    /// PID A dies, Count=1; new PID C → slot 1 → duplicate with B). 4-way
    /// verifier convergence flagged this as the critical v3.22.21 fix.
    /// Overflow (3+ clients on 2 monitors): all slots taken → fall back to
    /// <c>Count % monitorCount</c> for modulo distribution + one-shot warn
    /// per new overflow level. <paramref name="source"/> is the call-site
    /// label used in the log line.
    /// </summary>
    private int AssignNextFreeSlot(int pid, string source)
    {
        int monitorCount = GetMonitorOrderCount();
        // Build the set of currently-occupied slots in [0, monitorCount).
        // O(N) where N = current client count — small in practice (≤6).
        var used = new HashSet<int>();
        foreach (var s in _monitorSlotByPid.Values)
            used.Add(s);

        int slot = -1;
        for (int s = 0; s < monitorCount; s++)
        {
            if (!used.Contains(s)) { slot = s; break; }
        }

        if (slot < 0)
        {
            // Overflow: more clients than monitors — preserve the v3.22.20
            // modulo-stacking-by-design behavior. Slot picks based on map
            // size keeps the distribution balanced across monitors.
            slot = _monitorSlotByPid.Count % monitorCount;
            int newOverflow = (_monitorSlotByPid.Count + 1) - monitorCount;
            if (_overflowCountsLogged.Add(newOverflow))
            {
                FileLogger.Warn($"MonitorSlot: PID {pid} → slot {slot} ({source}) — overflow: {_monitorSlotByPid.Count + 1} clients on {monitorCount} monitor(s), stacking by design (modulo distribution). This is the v3.22.20 documented 3+ client behavior.");
            }
            else
            {
                FileLogger.Info($"MonitorSlot: PID {pid} → slot {slot} ({source}) — overflow, modulo distribution");
            }
        }
        else
        {
            FileLogger.Info($"MonitorSlot: PID {pid} → slot {slot} ({source})");
        }

        _monitorSlotByPid[pid] = slot;
        return slot;
    }

    /// <summary>
    /// v3.22.20: look up this PID's assigned monitor slot from the slot map.
    /// Falls back to <paramref name="clientIndexFallback"/> if the PID isn't
    /// in the map yet (transient client-discovery race). Returns a slot index
    /// in [0, monitorOrder.Count). Pure helper — no side effects.
    /// </summary>
    private int ResolveSlotForPid(int pid, int clientIndexFallback)
    {
        if (_monitorSlotByPid.TryGetValue(pid, out int s))
            return s;
        return clientIndexFallback;
    }

    /// <summary>
    /// Get the monitor (full) bounds for a PID based on its assigned monitor
    /// slot. v3.22.20: PID-keyed (was clientIndex-keyed) so SwitchKey-driven
    /// slot rotation flows through to hook config without index mismatch.
    /// Falls back to <paramref name="clientIndexFallback"/> if slot not mapped.
    /// </summary>
    private WinRect GetMonitorForPid(int pid, int clientIndexFallback)
    {
        var monitors = _windowManager.GetAllMonitorFullBounds();
        if (monitors.Count == 0)
            return new WinRect { Right = 1920, Bottom = 1080 };

        var primaryIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, monitors.Count - 1);
        // v3.22.19: shared smart-pick — matches ArrangeMultiMonitor's choice.
        int secondaryIdx = WindowManager.ResolveSecondaryMonitorIdx(_config.Layout.SecondaryMonitor, primaryIdx, monitors);

        var monitorOrder = new List<WinRect> { monitors[primaryIdx] };
        if (monitors.Count > 1)
            monitorOrder.Add(monitors[secondaryIdx]);

        int slot = ResolveSlotForPid(pid, clientIndexFallback);
        return monitorOrder[slot % monitorOrder.Count];
    }

    /// <summary>
    /// v3.22.19/20: work-area bounds (excludes taskbar) for a PID, slot-aware.
    /// Resolves secondaryIdx against full-bounds (canonical for arrange path)
    /// to keep hook config and arrange targeting the same physical monitor.
    /// </summary>
    private WinRect GetWorkAreaForPid(int pid, int clientIndexFallback)
    {
        // v3.22.19 round-2 verifier (T3 Sonnet + T2 Opus convergence):
        // Resolve secondaryIdx against FULL bounds (canonical for ArrangeMultiMonitor
        // + GetMonitorForPid) — then look up the work-area rect at the
        // SAME index. Previously passed workAreas to the resolver, which could
        // yield a different secondaryIdx on vertical-taskbar monitors (rcWork
        // width ≠ rcMonitor width), silently misaligning hook config against
        // arrange. With this fix, the per-PID hook config and the arrange
        // position both target the same physical monitor.
        var fullBounds = _windowManager.GetAllMonitorFullBounds();
        var workAreas = _windowManager.GetAllMonitorWorkAreas();
        if (workAreas.Count == 0)
            return new WinRect { Right = 1920, Bottom = 1040 };
        // Defense in depth — if enumerations diverge (theoretically impossible,
        // both walk EnumDisplayMonitors), fall back to legacy single-list logic
        // rather than picking wrong-monitor coords.
        if (fullBounds.Count != workAreas.Count)
        {
            FileLogger.Error($"GetWorkAreaForPid: monitor enumeration count mismatch — fullBounds={fullBounds.Count} workAreas={workAreas.Count}, falling back to work-area-only resolution");
            var primaryIdxFallback = Math.Clamp(_config.Layout.TargetMonitor, 0, workAreas.Count - 1);
            int secondaryIdxFallback = WindowManager.ResolveSecondaryMonitorIdx(_config.Layout.SecondaryMonitor, primaryIdxFallback, workAreas);
            var orderFallback = new List<WinRect> { workAreas[primaryIdxFallback] };
            if (workAreas.Count > 1) orderFallback.Add(workAreas[secondaryIdxFallback]);
            int slotFb = ResolveSlotForPid(pid, clientIndexFallback);
            return orderFallback[slotFb % orderFallback.Count];
        }

        var primaryIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, fullBounds.Count - 1);
        // Resolve against fullBounds for index consistency with the arrange path
        int secondaryIdx = WindowManager.ResolveSecondaryMonitorIdx(_config.Layout.SecondaryMonitor, primaryIdx, fullBounds);

        var monitorOrder = new List<WinRect> { workAreas[primaryIdx] };
        if (workAreas.Count > 1)
            monitorOrder.Add(workAreas[secondaryIdx]);

        int slot = ResolveSlotForPid(pid, clientIndexFallback);
        return monitorOrder[slot % monitorOrder.Count];
    }

    /// <summary>
    /// v3.22.44 Gate #1 — Dispose/Shutdown path. Closes the C#-side per-PID
    /// memory-mapped file handles + clears tracking dictionaries, but leaves
    /// the injected DLLs RESIDENT in every live eqgame.exe. The kernel keeps
    /// the named-mapping objects alive as long as the DLLs hold mapped views,
    /// so a future EQSwitch instance can re-attach by re-opening the same
    /// names.
    /// <para>
    /// Why no eject here: <c>DllInjector.Eject</c> does
    /// <c>CreateRemoteThread(eqgame, FreeLibrary, dllBase)</c>. FreeLibrary
    /// triggers <c>DllMain(DLL_PROCESS_DETACH)</c> which runs
    /// <c>MH_DisableHook</c> + <c>MH_Uninitialize</c> in both eqswitch-hook.dll
    /// and eqswitch-di8.dll, plus IAT-restore in iat_hook.cpp. Any EQ thread
    /// currently executing inside one of those detour bodies / IAT-redirected
    /// wrappers holds a stack frame whose return address points into a code
    /// page that's about to be unmapped — the thread returns into freed
    /// memory and eqgame.exe access-violates. This is the root cause of the
    /// "EQSwitch tray closes → all eqgames crash hard" symptom Nate has been
    /// hitting in field reports. MacroQuest's loader-exit pattern at
    /// <c>MacroQuest.cpp:2066-2087</c> does the same thing — it leaves
    /// mq2main.dll resident in running eqgame.exe processes; the DLL only
    /// cleans up via its own DLL_PROCESS_DETACH when eqgame itself exits.
    /// </para>
    /// <para>
    /// User-driven eject still exists at <see cref="EjectFromAllInjectedClients"/>
    /// — wired to the "Detach from running clients" tray menu item. That path
    /// is also unsafe without Gate #4's detour critical section in Native,
    /// but at least it's an explicit user choice rather than an automatic
    /// consequence of closing the tray.
    /// </para>
    /// </summary>
    private void CleanupHookConfigOnly()
    {
        _injectedPids.Clear();
        _di8InjectedPids.Clear();
        _hookConfig?.Dispose();
        _hookConfig = null;
    }

    /// <summary>
    /// v3.22.44 Gate #1 — user-initiated detach via tray menu only. Ejects
    /// both hook DLLs from every live eqgame.exe by calling
    /// <c>CreateRemoteThread(FreeLibrary)</c>. Still racy without Gate #4's
    /// Native-side detour critical section — call sites should warn users
    /// that this can crash running clients. Use case: an EQSwitch upgrade
    /// where the user wants to swap in a new hook DLL before relaunching
    /// EQSwitch, OR a user who explicitly wants to remove EQSwitch's window
    /// manipulation from running clients.
    /// </summary>
    private void EjectFromAllInjectedClients()
    {
        foreach (var pid in _injectedPids.ToArray())
            DllInjector.Eject(pid, "eqswitch-hook.dll");
        foreach (var pid in _di8InjectedPids.ToArray())
            DllInjector.Eject(pid, "eqswitch-di8.dll");
        _injectedPids.Clear();
        _di8InjectedPids.Clear();
        _hookConfig?.Dispose();
        _hookConfig = null;
    }

    /// <summary>
    /// v3.22.44 Gate #1 — tray menu handler that confirms before calling
    /// <see cref="EjectFromAllInjectedClients"/>. The confirmation isn't
    /// theater — Eject is still inherently racy until Gate #4's Native-side
    /// detour critical section ships, so user consent is the load-bearing
    /// safety net on the C# side.
    /// </summary>
    /// <summary>
    /// v3.22.44 r3 — refreshes the "Detach Hooks" tray menu item's Enabled
    /// state + tooltip. Called from UpdateClientMenu (on ClientListChanged)
    /// AND directly from InjectPreResume / InjectHookDll so the menu reflects
    /// injection state as soon as it changes, not after the next 10-second
    /// ProcessManager poll. T3-Opus F4 / T3-Sonnet F7 fix.
    /// <para>
    /// v3.22.44 r3.5 (R3-T3-Sonnet C4 HIGH + R3-T4-Sonnet Item 4 MEDIUM
    /// convergent): the AutoLogin path calls this from a Task.Run threadpool
    /// thread (FireTeam → BeginLogin → LaunchSuspendedAndInject →
    /// PreResumeCallback.Invoke → InjectPreResume). Writing to
    /// ToolStripMenuItem.Enabled / ToolTipText from a non-UI thread is a
    /// WinForms cross-thread violation. Mirror ShowBalloon's marshal at
    /// L2306-2309 to bounce back to the UI thread when the caller isn't
    /// already on it. LaunchManager's WinForms Timer path is already UI-
    /// thread and the marshal is a no-op there.
    /// </para>
    /// </summary>
    private void RefreshDetachMenuState()
    {
        if (_uiContext != null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => RefreshDetachMenuState(), null);
            return;
        }
        if (_detachItem == null) return;
        bool anyInjected = _injectedPids.Count > 0 || _di8InjectedPids.Count > 0;
        _detachItem.Enabled = anyInjected;
        // v3.22.53: match BuildContextMenu's new clean-exit wording so the
        // refresh path can't desync the tooltip with the active label.
        _detachItem.ToolTipText = anyInjected
            ? "Lets EQSwitch fully release its window/input hooks from every running eqgame.exe so you can close EQSwitch without also closing the game.\nNote: this is a clean-exit tool, not a crash-prevention tool. If EQ is mid-render through one of our hooks the client may briefly stutter."
            : "No injected eqgame.exe processes.";
    }

    // v3.22.54: OnDetachHooksMenuItem deleted — there's no menu item left
    // to fire it. EjectFromAllInjectedClients is kept (could be wired to a
    // future programmatic exit path, sandbox smoke-test, or re-introduced
    // menu surface) but currently has no callers. _detachItem stays nullable
    // so the 10 RefreshDetachMenuState call sites scattered through the file
    // safely no-op without churning every injection site. (Chesterton: those
    // sites mark "moments when injected-PID set changes" which is useful
    // information even without a UI consumer.)

    private void StopForegroundHook()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        // NOTE: _foregroundHookProc is intentionally NOT nulled here.
        // With WINEVENT_OUTOFCONTEXT, callbacks are dispatched via PostMessage.
        // UnhookWinEvent stops future events but can't cancel already-queued ones.
        // Nulling the delegate lets GC collect it before the pump drains → AccessViolation.
        // The delegate is only nulled in Dispose() after full shutdown.
        _foregroundDebounceTimer?.Stop();
        _foregroundDebounceTimer?.Dispose();
        _foregroundDebounceTimer = null;
        _slimTitlebarGuard?.Stop();
        _slimTitlebarGuard?.Dispose();
        _slimTitlebarGuard = null;
        _affinityFallbackTimer?.Stop();
        _affinityFallbackTimer?.Dispose();
        _affinityFallbackTimer = null;
    }

    private void Shutdown()
    {
        StopForegroundHook();
        CleanupHookConfigOnly();
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _launchManager.CancelLaunch();
        _launchManager.Dispose();
        _leftClickTimer?.Stop();
        _leftClickTimer?.Dispose();
        _middleClickTimer?.Stop();
        _middleClickTimer?.Dispose();
        _deferTimer?.Stop();
        _deferTimer?.Dispose();
        _taskbarMessageWindow?.DestroyHandle();
        _pipOverlay?.Dispose();
        _pipOverlay = null;
        _boldMenuFont?.Dispose();
        _boldMenuFont = null;
        _hotkeyManager.Dispose();
        _keyboardHook.Dispose();
        _processManager.Dispose();
        _contextMenu?.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        Application.Exit();
    }

    /// <summary>
    /// Drive the TrayIconPromoter retry timer until our subkey is identified or
    /// the 10 s budget elapses. Idempotent — Phase 1 no-ops once IsPromoted=1.
    /// Snippet: _.claude/_templates/snippets/csharp/tray-icon-promoter.md.
    /// Independent of the auto-login state machine — promoter never blocks
    /// or interacts with the EQ login flow.
    /// </summary>
    private void StartTrayIconPromotion(HashSet<string>? baseline)
    {
        var promoteTimer = new System.Windows.Forms.Timer { Interval = 500 };
        int attempts = 0;
        const int maxAttempts = 20;   // 500 ms * 20 = 10 s cap
        promoteTimer.Tick += (_, _) =>
        {
            attempts++;
            bool done = TrayIconPromoter.TryPromote(Application.ExecutablePath, baseline)
                        || attempts >= maxAttempts;
            if (done)
            {
                promoteTimer.Stop();
                promoteTimer.Dispose();
            }
        };
        promoteTimer.Start();
    }

    /// <summary>
    /// Load the tray icon. Does NOT dispose the previous icon — caller must handle that
    /// to avoid a freed-GDI-handle gap where _trayIcon.Icon points to a disposed icon.
    /// </summary>
    private Icon LoadIcon()
    {
        try
        {
            // Priority 1: User-selected custom icon path from settings
            if (!string.IsNullOrEmpty(_config.CustomIconPath) && File.Exists(_config.CustomIconPath))
            {
                FileLogger.Info($"Icon: loaded custom icon from {_config.CustomIconPath}");
                return new Icon(_config.CustomIconPath, 32, 32);
            }

            // Priority 2: Default embedded Stone icon (eqswitch.ico — the primary embedded icon)
            using var stream = typeof(TrayManager).Assembly.GetManifestResourceStream("EQSwitch.eqswitch.ico");
            if (stream != null)
                return new Icon(stream, 32, 32);

            // Priority 3: Fall back to file on disk (dev/debug builds)
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var iconPath = Path.Combine(baseDir, "eqswitch-alt.ico");
            if (!File.Exists(iconPath))
                iconPath = Path.Combine(baseDir, "eqswitch.ico");
            return File.Exists(iconPath) ? new Icon(iconPath, 32, 32) : SystemIcons.Application;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Icon loading failed, using default: {ex.Message}");
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        // v3.22.33 (T2 Opus Gap 5): flag flips first so any ad-hoc one-shot
        // timer ticks queued in the WinForms message pump bail out cleanly
        // rather than NRE-ing on disposed managers. Idempotent on re-entry.
        if (_disposed) return;
        _disposed = true;
        StopForegroundHook();
        CleanupHookConfigOnly();
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _launchManager.Dispose();
        _leftClickTimer?.Stop();
        _leftClickTimer?.Dispose();
        _middleClickTimer?.Stop();
        _middleClickTimer?.Dispose();
        _deferTimer?.Stop();
        _deferTimer?.Dispose();
        // v3.22.72: drop any pending "Lost:" balloon at shutdown — the user
        // is exiting EQSwitch, surfacing a balloon now would race the tray
        // icon's own destruction and risk an orphan FloatingTooltip window.
        _lostClientsCoalesceTimer?.Stop();
        _lostClientsCoalesceTimer?.Dispose();
        _pendingLostClients.Clear();
        // _foregroundDebounceTimer already disposed by StopForegroundHook() above
        _foregroundHookProc = null; // safe to release now — message pump fully drained at shutdown
        _boldMenuFont?.Dispose();
        _pipOverlay?.Dispose();
        _hotkeyManager.Dispose();
        _keyboardHook.Dispose();
        _taskbarMessageWindow?.DestroyHandle();
        _trayIcon?.Dispose();
        _contextMenu?.Dispose();
        // v3.22.62 (verifier T2-Opus + T3-Opus convergent CRITICAL): do NOT
        // Dispose _eqgameWindowIcon. Per MSDN, WM_SETICON does NOT take
        // ownership — the receiving eqgame window stores the raw HICON value
        // (not a duplicate), so DestroyIcon (which Icon.Dispose calls) would
        // invalidate eqgame's still-live handle. The icon is bounded by
        // EQSwitch's process lifetime; Windows reclaims the GDI handle on
        // process exit. Leaving the Icon undisposed is a process-lifetime
        // "leak" of one GDI handle, which is correct vs. dangling-handle
        // corruption in eqgame.
        // (Original Icon ref intentionally kept alive — held by _eqgameWindowIcon
        // field for the EQSwitch process duration.)
        _processManager.Dispose();
    }

    /// <summary>
    /// Phase-3-only legacy hotkey indexer. Maps <c>QuickLoginN</c> target strings to their
    /// bound <c>HotkeyConfig.AutoLoginN</c> combos so the new Accounts/Characters submenus
    /// can show the user's existing Alt+N bindings during the Phase 3 → Phase 5
    /// transition. Removed in Phase 5 when AccountHotkeys[] / CharacterHotkeys[]
    /// family tables replace the QuickLoginN pair scheme.
    /// </summary>
    private sealed class LegacyHotkeyLookup
    {
        private readonly Dictionary<string, string> _comboByTarget = new(StringComparer.Ordinal);

        public LegacyHotkeyLookup(AppConfig config)
        {
            var hk = config.Hotkeys;
            Register(config.QuickLogin1, hk.AutoLogin1);
            Register(config.QuickLogin2, hk.AutoLogin2);
            Register(config.QuickLogin3, hk.AutoLogin3);
            Register(config.QuickLogin4, hk.AutoLogin4);
            foreach (var b in hk.AccountHotkeys)   Register(b.TargetName, b.Combo);
            foreach (var b in hk.CharacterHotkeys) Register(b.TargetName, b.Combo);
        }

        private void Register(string target, string combo)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(combo)) return;
            if (_comboByTarget.ContainsKey(target))
            {
                FileLogger.Warn($"LegacyHotkeyLookup: duplicate QuickLogin target '{target}' — overwriting existing combo '{_comboByTarget[target]}' with '{combo}'");
            }
            _comboByTarget[target] = combo;
        }

        /// <summary>Returns the bound combo for this Account/Character Name, or "" if unbound.</summary>
        public string GetCombo(string name) =>
            !string.IsNullOrEmpty(name) && _comboByTarget.TryGetValue(name, out var c) ? c : "";
    }
}

/// <summary>
/// Hidden message-only window that listens for TaskbarCreated.
/// When explorer.exe restarts, Windows broadcasts this message so tray apps can re-register.
/// </summary>
internal class TaskbarMessageWindow : NativeWindow
{
    private static readonly uint WM_TASKBARCREATED = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
    private readonly Action _onTaskbarCreated;

    public TaskbarMessageWindow(Action onTaskbarCreated)
    {
        _onTaskbarCreated = onTaskbarCreated;
        var cp = new CreateParams { Parent = new IntPtr(-3) }; // HWND_MESSAGE
        CreateHandle(cp);
    }

    protected override void WndProc(ref Message m)
    {
        if (WM_TASKBARCREATED != 0 && m.Msg == (int)WM_TASKBARCREATED)
        {
            _onTaskbarCreated();
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// Dark-themed renderer for ContextMenuStrip. Matches the app's dark UI.
/// </summary>
internal class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    // Unified with DarkTheme palette (medieval purple tones)
    private static readonly Color MenuBg = DarkTheme.BgDark;             // (32, 28, 42)
    private static readonly Color MenuBorder = DarkTheme.Border;         // (64, 56, 78)
    private static readonly Color ItemHover = DarkTheme.BgHover;         // (64, 56, 78)
    private static readonly Color ItemText = DarkTheme.FgWhite;          // (235, 232, 240)
    private static readonly Color DisabledText = DarkTheme.FgDimGray;    // (120, 112, 135)
    private static readonly Color SepColor = DarkTheme.Border;           // (64, 56, 78)
    private static readonly Color CheckBg = DarkTheme.AccentGreen;       // (0, 140, 80)
    private static readonly Color MarginBg = DarkTheme.BgPanel;          // (38, 33, 48)
    private static readonly Color AccountText = DarkTheme.FgAccountOrange; // (255, 159, 0)

    // ─── Tag-routed color markers ──────────────────────────────────────
    // Set ToolStripMenuItem.Tag to one of these singletons at build time;
    // OnRenderMenuItemBackground routes ForeColor accordingly each repaint.
    // ReferenceEquals compare is allocation-free and typo-immune (vs a string
    // tag). Used by BuildAccountsSubmenu and BuildCharactersSubmenu.
    internal static readonly object AccountItemMarker = new();
    internal static readonly object OrphanItemMarker = new();

    // Per-segment label spec for Teams rows that mix Account- and
    // Character-resolved slot names. Drawn by OnRenderItemText so a single
    // row can render "🚀  natedogg / acpots" with the Account-sourced name
    // in orange and the Character-sourced name in white. Tag the item with
    // a TeamRowSegments instance to opt into this rendering. The item's
    // primary Text MUST still contain the visible string concatenated —
    // ToolStrip layout sizes the row from Item.Text, not from segments.
    internal sealed record TeamRowSegments(IReadOnlyList<TeamRowSegment> Segments);
    internal sealed record TeamRowSegment(string Text, bool IsAccount);

    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics;
        var rect = new Rectangle(Point.Empty, e.Item.Size);

        if (e.Item.Selected && e.Item.Enabled)
        {
            using var brush = new SolidBrush(ItemHover);
            g.FillRectangle(brush, rect);
            using var pen = new Pen(MenuBorder);
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }
        else
        {
            using var brush = new SolidBrush(MenuBg);
            g.FillRectangle(brush, rect);
        }

        // Tag-routed ForeColor — disabled wins first, then the explicit Tag
        // markers, else standard ItemText. The renderer rewrites ForeColor
        // every repaint, so setting Item.ForeColor at build time is *not* a
        // valid path (renderer clobbers it). All color choices live here.
        e.Item.ForeColor = !e.Item.Enabled ? DisabledText
            : ReferenceEquals(e.Item.Tag, AccountItemMarker) ? AccountText
            : ReferenceEquals(e.Item.Tag, OrphanItemMarker) ? DisabledText
            : ItemText;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // ToolStripProfessionalRenderer raises OnRenderItemText TWICE per
        // ToolStripMenuItem: once with e.Text == Item.Text (primary label,
        // left-aligned) and once with e.Text == ShortcutKeyDisplayString
        // (right-aligned hotkey column). We branch on which pass this is.
        if (e.Item is ToolStripMenuItem mi
            && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString)
            && string.Equals(e.Text, mi.ShortcutKeyDisplayString, StringComparison.Ordinal))
        {
            // Hotkey column stays uniform regardless of the row's body color —
            // orange Accounts rows keep a white "Alt+1", segmented Teams rows
            // keep a white "Ctrl+Alt+Shift+F9". Orphan rows render dim end-to-
            // end so the row reads as a single muted entry.
            var saved = e.Item.ForeColor;
            try
            {
                e.Item.ForeColor = !e.Item.Enabled || ReferenceEquals(e.Item.Tag, OrphanItemMarker)
                    ? DisabledText
                    : ItemText;
                base.OnRenderItemText(e);
            }
            finally
            {
                e.Item.ForeColor = saved;
            }
            return;
        }

        // Primary-label pass for segmented Teams rows: draw each segment in
        // its own color so Account-resolved names render orange and
        // Character-resolved names render white inside the same row.
        if (e.Item.Enabled && e.Item.Tag is TeamRowSegments segs)
        {
            DrawTeamRowSegments(e, segs);
            return;
        }

        // Fallthrough: standard text render. ForeColor was already routed in
        // OnRenderMenuItemBackground (orange for AccountItemMarker, dim for
        // OrphanItemMarker, default ItemText otherwise).
        base.OnRenderItemText(e);
    }

    private static void DrawTeamRowSegments(ToolStripItemTextRenderEventArgs e, TeamRowSegments segs)
    {
        // Walk segments left-to-right inside e.TextRectangle. NoPadding on
        // both measure and draw makes inter-segment butt-joins pixel-accurate
        // (TextRenderer otherwise adds ~3-6px GlyphOverhang per call which
        // would visibly gap "natedogg" from " / "). The row's overall width
        // was sized by ToolStrip layout from Item.Text — which BuildTeamsSubmenu
        // sets to the same visible string our segments concatenate to — so
        // clipping is not expected; the `remaining > 0` guard is defense.
        var rect = e.TextRectangle;
        var format = e.TextFormat | TextFormatFlags.NoPadding;
        int x = rect.X;
        int remaining = rect.Width;

        foreach (var seg in segs.Segments)
        {
            if (string.IsNullOrEmpty(seg.Text) || remaining <= 0) continue;
            var color = seg.IsAccount ? AccountText : ItemText;
            var size = TextRenderer.MeasureText(
                e.Graphics, seg.Text, e.TextFont, new Size(remaining, rect.Height), format);
            int drawWidth = Math.Min(size.Width, remaining);
            var segRect = new Rectangle(x, rect.Y, drawWidth, rect.Height);
            TextRenderer.DrawText(e.Graphics, seg.Text, e.TextFont, segRect, color, format);
            x += drawWidth;
            remaining -= drawWidth;
        }
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        int y = e.Item.Height / 2;
        using var pen = new Pen(SepColor);
        g.DrawLine(pen, 28, y, e.Item.Width - 4, y);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(MenuBg);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(MenuBorder);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(MarginBg);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        var rect = e.ImageRectangle;
        rect.Inflate(2, 2);
        using var brush = new SolidBrush(CheckBg);
        g.FillRectangle(brush, rect);
        // Draw checkmark
        using var pen = new Pen(DarkTheme.FgWhite, 2);
        int x = rect.X + 3;
        int y = rect.Y + rect.Height / 2;
        g.DrawLines(pen, new[] {
            new Point(x, y), new Point(x + 3, y + 3), new Point(x + 9, y - 3)
        });
    }

    private class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => DarkTheme.Border;
        public override Color MenuItemBorder => DarkTheme.Border;
        public override Color MenuItemSelected => DarkTheme.BgHover;
        public override Color MenuStripGradientBegin => DarkTheme.BgDark;
        public override Color MenuStripGradientEnd => DarkTheme.BgDark;
        public override Color MenuItemSelectedGradientBegin => DarkTheme.BgHover;
        public override Color MenuItemSelectedGradientEnd => DarkTheme.BgHover;
        public override Color MenuItemPressedGradientBegin => DarkTheme.BgMedium;
        public override Color MenuItemPressedGradientEnd => DarkTheme.BgMedium;
        public override Color ImageMarginGradientBegin => DarkTheme.BgPanel;
        public override Color ImageMarginGradientMiddle => DarkTheme.BgPanel;
        public override Color ImageMarginGradientEnd => DarkTheme.BgPanel;
        public override Color SeparatorDark => DarkTheme.Border;
        public override Color SeparatorLight => DarkTheme.Border;
        public override Color ToolStripDropDownBackground => DarkTheme.BgDark;
    }
}
