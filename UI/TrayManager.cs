using System.Reflection;
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
    private readonly SynchronizationContext? _uiContext;

    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _contextMenu;
    private ToolStripMenuItem? _clientsMenu;
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

    // DLL hook injection — hooks SetWindowPos/MoveWindow inside eqgame.exe
    private HookConfigWriter? _hookConfig;
    private readonly HashSet<int> _injectedPids = new();
    private readonly HashSet<int> _di8InjectedPids = new();


    public TrayManager(AppConfig config, ProcessManager processManager)
    {
        _config = config;
        _processManager = processManager;
        _uiContext = SynchronizationContext.Current;
        _windowManager = new WindowManager(config);
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
        _autoLoginManager.LoginComplete += (_, pid) =>
        {
            // Resume guard timer
            _slimTitlebarGuard?.Start();
            FileLogger.Info("AutoLogin: resumed slim titlebar guard timer");

            // Now that login is done, apply everything that was deferred
            var client = _processManager.Clients.FirstOrDefault(c => c.ProcessId == pid);
            if (client == null) return;

            if (_config.Layout.SlimTitlebar)
            {
                _windowManager.ApplySlimTitlebar(
                    client.WindowHandle,
                    _windowManager.GetTargetMonitorBounds(),
                    _config.Layout.TitlebarOffset);
            }
            // Hook DLL injection is now handled pre-resume (CREATE_SUSPENDED),
            // so no need to inject here. Just ensure config is fresh.
            if (_injectedPids.Contains(pid))
                UpdateHookConfigForPid(pid);
        };
    }

    public void Initialize()
    {
        // One-time cleanup: remove legacy proxy DLL files from game directory
        CleanupLegacyProxyFiles();

        _trayIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "EQSwitch - 0 clients",
            Visible = true
        };

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

        // Listen for TaskbarCreated to recover tray icon after explorer.exe restarts
        _taskbarMessageWindow = new TaskbarMessageWindow(() =>
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Visible = true;
                FileLogger.Info("Explorer restarted — tray icon re-registered");
            }
        });

        BuildContextMenu();

        _processManager.ClientListChanged += (_, _) =>
        {
            UpdateClientMenu();
            UpdateTrayText();
            _keyboardHook.UpdateFilteredPids(_processManager.Clients.Select(c => c.ProcessId));

            // Start/stop slim titlebar position guard based on client count.
            // When hook injection is active, the DLL handles positioning from inside
            // the process, so we increase the guard interval (backup only).
            if (_config.Layout.SlimTitlebar && _processManager.Clients.Count > 0)
            {
                bool hookActive = _injectedPids.Count > 0;
                int guardInterval = hookActive ? 5000 : 500;

                if (_slimTitlebarGuard == null)
                {
                    _slimTitlebarGuard = new System.Windows.Forms.Timer { Interval = guardInterval };
                    _slimTitlebarGuard.Tick += (_, _) =>
                        _windowManager.ApplySlimTitlebarToAll(_processManager.Clients, _injectedPids);
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
            // NO tooltip here — creating TopMost windows during EQ's DirectX init
            // causes the game to lose foreground and minimize itself
            _affinityManager.ScheduleRetry(c);

            // Defer all window manipulation when auto-login is active —
            // SetWindowPos/SetWindowLongPtr/CreateRemoteThread can interfere
            // with the DirectInput login sequence.
            if (_autoLoginManager.IsLoginActive(c.ProcessId))
                return;

            // Apply slim titlebar immediately so the window covers the taskbar
            // from the moment it's discovered — don't wait for the guard timer
            if (_config.Layout.SlimTitlebar)
            {
                _windowManager.ApplySlimTitlebar(
                    c.WindowHandle,
                    _windowManager.GetTargetMonitorBounds(),
                    _config.Layout.TitlebarOffset);
            }

            // For EQSwitch-launched clients, both DLLs are already injected pre-resume.
            // For manually-launched clients (detected by ProcessManager poll), inject
            // eqswitch-hook.dll only — DirectInput hooking requires pre-resume injection.
            if (_injectedPids.Contains(c.ProcessId))
            {
                // Already injected pre-resume — just refresh config
                UpdateHookConfigForPid(c.ProcessId);
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
            if (_contextMenu?.Visible != true)
                ShowBalloon($"Lost: {c}");
            _affinityManager.CancelRetry(c.ProcessId);

            // Clean up injection tracking and per-process shared memory
            _injectedPids.Remove(c.ProcessId);
            _di8InjectedPids.Remove(c.ProcessId);
            _hookConfig?.Close(c.ProcessId);

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
                        _windowManager.ArrangeWindows(clients);
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
        TryRegister(hk.LaunchOne, OnLaunchOne, "LaunchOne");
        TryRegister(hk.LaunchAll, () => ExecuteTrayAction("LaunchTwo"), "LaunchAll");
        TryRegister(hk.AutoLogin1, () => ExecuteTrayAction("AutoLogin1"), "AutoLogin1");
        TryRegister(hk.AutoLogin2, () => ExecuteTrayAction("AutoLogin2"), "AutoLogin2");
        TryRegister(hk.AutoLogin3, () => ExecuteTrayAction("AutoLogin3"), "AutoLogin3");
        TryRegister(hk.AutoLogin4, () => ExecuteTrayAction("AutoLogin4"), "AutoLogin4");
        TryRegister(hk.TeamLogin1, () => ExecuteTrayAction("LoginAll"), "TeamLogin1");
        TryRegister(hk.TeamLogin2, () => ExecuteTrayAction("LoginAll2"), "TeamLogin2");
        TryRegister(hk.TeamLogin3, () => ExecuteTrayAction("LoginAll3"), "TeamLogin3");
        TryRegister(hk.TeamLogin4, () => ExecuteTrayAction("LoginAll4"), "TeamLogin4");

        FileLogger.Info($"RegisterHotKey: {registered} registered, {failed} failed" +
            (failedKeys.Count > 0 ? $" [{string.Join(", ", failedKeys)}]" : ""));
        if (failedKeys.Count > 0)
            ShowWarning($"Hotkey conflict: {string.Join(", ", failedKeys)}\nAnother app may be using these keys.");

        // Low-level keyboard hook for single-key hotkeys
        if (_keyboardHook.Install())
        {
            // Switch Key (default '\') — only when EQ is focused
            if (!string.IsNullOrEmpty(hk.SwitchKey))
            {
                uint vk = HotkeyManager.ResolveVK(hk.SwitchKey);
                if (vk != 0)
                {
                    _keyboardHook.Register(vk, OnSwitchKey, _config.EQProcessName);
                    FileLogger.Info($"Hook: SwitchKey '{hk.SwitchKey}' (VK 0x{vk:X2}) — EQ-only");
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
            _windowManager.SwitchToClient(client);
            FileLogger.Info($"Direct switch to slot {slot + 1}: {client}");
        }
        else
        {
            FileLogger.Info($"Direct switch: no client in slot {slot + 1}");
        }
    }

    /// <summary>
    /// Switch Key ('\'):  Swap between last two clients (default) or cycle all.
    /// Only fires when an EQ window is already focused (enforced by KeyboardHookManager filter).
    /// </summary>
    private void OnSwitchKey()
    {
        var current = _processManager.GetActiveClient();
        var clients = _processManager.Clients;
        if (clients.Count < 2)
        {
            FileLogger.Info("SwitchKey: fewer than 2 clients, nothing to switch");
            return;
        }

        // In multimonitor mode, swap positions then resize to fit new monitors
        bool isMultiMon = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        if (isMultiMon)
        {
            try
            {
                _windowManager.SwapWindows(clients);
                _windowManager.ResizeToCurrentMonitors(clients);
                UpdateHookConfig(); // Hook configs must follow swapped positions
            }
            catch (Exception ex)
            {
                FileLogger.Error("SwitchKey: multimonitor swap+resize failed", ex);
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
                    _windowManager.SwitchToClient(target);
                    FileLogger.Info($"SwitchKey: {(isMultiMon ? "swapped positions + " : "")}swapped to last active {target}");
                    return;
                }
            }
            // Fallback to cycle if no previous client tracked
            var next = _windowManager.CycleNext(clients, current);
            if (next != null)
                FileLogger.Info($"SwitchKey: {(isMultiMon ? "swapped positions + " : "")}cycled (no previous tracked) to {next}");
        }
        else
        {
            // Cycle through all clients round-robin
            var next = _windowManager.CycleNext(clients, current);
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
                _windowManager.SwapWindows(clients);
                _windowManager.ResizeToCurrentMonitors(clients);
                UpdateHookConfig(); // Hook configs must follow swapped positions
            }
            catch (Exception ex)
            {
                FileLogger.Error("GlobalSwitchKey: multimonitor swap+resize failed", ex);
            }
        }

        if (current != null)
        {
            // EQ is focused — cycle to next
            var next = _windowManager.CycleNext(clients, current);
            if (next != null)
                FileLogger.Info($"GlobalSwitchKey: {(isMultiMon ? "swapped positions + " : "")}cycled to {next}");
        }
        else
        {
            // EQ is NOT focused — bring first client to front
            var first = clients[0];
            _windowManager.SwitchToClient(first);
            FileLogger.Info($"GlobalSwitchKey: focused {first}");
        }
    }

    /// <summary>
    /// Arrange all EQ windows (Fix Windows). Hotkey configurable in Settings.
    /// </summary>
    private void OnArrangeWindows()
    {
        var clients = _processManager.Clients;
        if (clients.Count == 0)
        {
            FileLogger.Info("ArrangeWindows: no clients to arrange");
            ShowBalloon("No EQ clients running");
            return;
        }

        _windowManager.ArrangeWindows(clients);
        UpdateHookConfig();
        FileLogger.Info($"ArrangeWindows: arranged {clients.Count} client(s)");
        string mode = _config.Layout.SlimTitlebar ? "slim titlebar" : "stacked";
        ShowBalloon($"Fixed {clients.Count} window(s) ({mode})");
    }

    /// <summary>
    /// Alt+M: Toggle multi-monitor / single-screen layout mode.
    /// 500ms debounce to prevent rapid re-triggering while windows are moving.
    /// </summary>
    private void OnToggleMultiMonitor()
    {
        // Hotkey locked until user has tried multimonitor at least once via Settings
        if (!_config.Hotkeys.MultiMonitorEnabled)
        {
            FileLogger.Info("ToggleMultiMonitor: not yet unlocked — enable in Settings first");
            ShowBalloon("Enable Multi-Monitor mode in Settings first");
            return;
        }

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
            _windowManager.ArrangeWindows(clients);
            UpdateHookConfig();
        }
    }

    /// <summary>
    /// Launch one EQ client.
    /// </summary>
    private void OnLaunchOne()
    {
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

    private void BuildContextMenu()
    {
        // Dispose old menu and all its items before rebuilding (prevents leak if called multiple times)
        _contextMenu?.Dispose();
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Renderer = new DarkMenuRenderer();
        _contextMenu.Closed += (_, _) =>
        {
            if (_clientMenuDirty) UpdateClientMenu();
        };

        var hk = _config.Hotkeys;
        string HkSuffix(string key) => string.IsNullOrEmpty(key) ? "" : $"\t{key}";

        _boldMenuFont?.Dispose();
        _boldMenuFont = new Font(_contextMenu.Font, FontStyle.Bold);

        // Title bar
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var titleItem = new ToolStripMenuItem($"\u2694  EQ Switch v{version}  \u2694") { Enabled = false, Font = _boldMenuFont };
        _contextMenu.Items.Add(titleItem);
        _contextMenu.Items.Add(new ToolStripSeparator());

        var launchOneItem = new ToolStripMenuItem($"\u2694  Launch Client{HkSuffix(hk.LaunchOne)}") { Font = _boldMenuFont };
        launchOneItem.Click += (_, _) => OnLaunchOne();
        _contextMenu.Items.Add(launchOneItem);

        var launchTeamItem = new ToolStripMenuItem($"\uD83C\uDFAE  Launch Team{HkSuffix(hk.TeamLogin1)}") { Font = _boldMenuFont };
        launchTeamItem.Click += (_, _) => ExecuteTrayAction("LoginAll");
        _contextMenu.Items.Add(launchTeamItem);

        // Login submenu (always visible, like Clients menu)
        var loginMenu = new ToolStripMenuItem("\uD83D\uDD11  Accounts") { Font = _boldMenuFont };
        if (_config.Accounts.Count > 0)
        {
            foreach (var account in _config.Accounts)
            {
                var label = string.IsNullOrEmpty(account.CharacterName)
                    ? account.Username
                    : account.CharacterName;
                loginMenu.DropDownItems.Add($"\uD83D\uDC64  {label}", null, (_, _) =>
                {
                    try { _ = _autoLoginManager.LoginAccount(account); }
                    catch (Exception ex)
                    {
                        FileLogger.Error($"AutoLogin CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
                        if (ex.InnerException != null)
                            FileLogger.Error($"AutoLogin CRASH inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}");
                    }
                });
            }
            // Team 1 omitted — "Launch Team" on root menu handles it
            var teams = new[]
            {
                (Has: !string.IsNullOrEmpty(_config.Team2Account1) || !string.IsNullOrEmpty(_config.Team2Account2), Label: "Auto-Login Team 2", Action: "LoginAll2"),
                (Has: !string.IsNullOrEmpty(_config.Team3Account1) || !string.IsNullOrEmpty(_config.Team3Account2), Label: "Auto-Login Team 3", Action: "LoginAll3"),
                (Has: !string.IsNullOrEmpty(_config.Team4Account1) || !string.IsNullOrEmpty(_config.Team4Account2), Label: "Auto-Login Team 4", Action: "LoginAll4"),
            };
            if (teams.Any(t => t.Has))
            {
                loginMenu.DropDownItems.Add(new ToolStripSeparator());
                foreach (var t in teams.Where(t => t.Has))
                    loginMenu.DropDownItems.Add($"\uD83D\uDE80  {t.Label}", null, (_, _) => ExecuteTrayAction(t.Action));
            }
            loginMenu.DropDownItems.Add(new ToolStripSeparator());
        }
        loginMenu.DropDownItems.Add("\u2699  Manage Accounts...", null, (_, _) => ShowSettings(2));
        _contextMenu.Items.Add(loginMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());

        _clientsMenu = new ToolStripMenuItem("\uD83D\uDC64  Clients");
        _clientsMenu.DropDownItems.Add("(scanning...)");
        _contextMenu.Items.Add(_clientsMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());

        _contextMenu.Items.Add("\u26A1  Process Manager", null, (_, _) => ShowProcessManager());

        // Video Settings submenu
        var videoMenu = new ToolStripMenuItem("\uD83D\uDCFA  Video Settings") { DropDownDirection = ToolStripDropDownDirection.Right };
        videoMenu.DropDownItems.Add("\uD83D\uDCDD  Video Settings...", null, (_, _) =>
        {
            ShowSettings(1); // Video tab
        });
        videoMenu.DropDownItems.Add(new ToolStripSeparator());
        var pipItem = new ToolStripMenuItem(
            $"{(_config.Pip.Enabled ? "\u2705" : "\u2B1C")}  Picture in Picture");
        pipItem.Click += (_, _) =>
        {
            TogglePip();
            BuildContextMenu();
        };
        videoMenu.DropDownItems.Add(pipItem);
        videoMenu.DropDownItems.Add(new ToolStripSeparator());
        videoMenu.DropDownItems.Add($"Fix Windows  \uD83D\uDD27{HkSuffix(hk.ArrangeWindows)}", null, (_, _) => OnArrangeWindows());
        videoMenu.DropDownItems.Add("Swap Windows  \uD83D\uDD00", null, (_, _) => ExecuteTrayAction("SwapWindows"));
        bool isMultiMon = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        var multiMonItem = new ToolStripMenuItem(
            $"{(isMultiMon ? "\u2705" : "\u2B1C")}  Multi-Monitor Mode");
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
        linksMenu.DropDownItems.Add("\uD83C\uDFE0  Dalaya", null, (_, _) => FileOperations.OpenUrl("https://dalaya.org/"));
        linksMenu.DropDownItems.Add(new ToolStripSeparator());
        linksMenu.DropDownItems.Add("\uD83D\uDDE1  Shards Wiki", null, (_, _) => FileOperations.OpenUrl("https://wiki.shardsofdalaya.com/wiki/Main_Page"));
        linksMenu.DropDownItems.Add("\uD83D\uDCD6  Dalaya Wiki", null, (_, _) => FileOperations.OpenUrl("https://wiki.dalaya.org/"));
        linksMenu.DropDownItems.Add("\uD83C\uDFC6  Fomelo Dalaya", null, (_, _) => FileOperations.OpenUrl("https://dalaya.org/fomelo/"));
        linksMenu.DropDownItems.Add("\uD83D\uDCDC  Dalaya Listsold", null, (_, _) => FileOperations.OpenUrl("https://dalaya.org/listsold.php"));
        launcherMenu.DropDownItems.Add("\uD83D\uDD27  Dalaya Patcher", null, (_, _) => FileOperations.OpenDalayaPatcher(_config, ShowBalloon, () => ShowSettings(5)));
        launcherMenu.DropDownItems.Add("\uD83D\uDCAC  Dalaya Discord", null, (_, _) => FileOperations.OpenUrl("discord://discord.com/channels/1233224126353768490/1249250739918864446"));
        launcherMenu.DropDownItems.Add(new ToolStripSeparator());
        launcherMenu.DropDownItems.Add("✂  Trim Log Files", null, (_, _) => FileOperations.TrimLogFiles(_config, ShowBalloon));
        launcherMenu.DropDownItems.Add("\uD83D\uDCDC  Open Log File...", null, (_, _) => FileOperations.OpenLogFile(_config, ShowBalloon));
        launcherMenu.DropDownItems.Add("\uD83D\uDCC4  Open eqclient.ini", null, (_, _) => FileOperations.OpenEqClientIni(_config, ShowBalloon));
        launcherMenu.DropDownItems.Add(new ToolStripSeparator());
        launcherMenu.DropDownItems.Add(linksMenu);
        launcherMenu.DropDownItems.Add(new ToolStripSeparator());
        launcherMenu.DropDownItems.Add("\uD83C\uDFAF  Open GINA", null, (_, _) => FileOperations.OpenGina(_config, ShowBalloon, () => ShowSettings(5)));
        launcherMenu.DropDownItems.Add("\uD83D\uDCDD  Open Notes", null, (_, _) => FileOperations.OpenNotes(_config, ShowBalloon));
        _contextMenu.Items.Add(launcherMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("\u2716  Exit", null, (_, _) => Shutdown());

        // ContextMenuStrip is toggled on/off in MouseDown to prevent left-click
        // from showing the menu (which steals focus and breaks double-click).
        _trayIcon!.ContextMenuStrip = null;
    }

    private bool _clientMenuDirty;

    private void UpdateClientMenu()
    {
        if (_clientsMenu == null) return;

        // Don't rebuild while the menu is open — it causes the menu to close
        if (_contextMenu?.Visible == true)
        {
            _clientMenuDirty = true;
            return;
        }
        _clientMenuDirty = false;

        // Dispose old menu items to prevent GDI/memory leaks (called on every client change)
        for (int i = _clientsMenu.DropDownItems.Count - 1; i >= 0; i--)
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
            var item = new ToolStripMenuItem($"[{client.SlotIndex + 1}] {client}");
            item.Click += (_, _) => _windowManager.SwitchToClient(c);
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
            return;
        }

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
        if (_config.TooltipDurationMs <= 0) return;
        // Marshal to UI thread if called from a background thread (e.g. FireTeamLogin)
        if (_uiContext != null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => ShowBalloon(message), null);
            return;
        }
        // Defer to next message loop iteration so context menu handlers
        // fully complete before we create the tooltip window.
        DeferToNextTick(() => FloatingTooltip.Show(message, _config.TooltipDurationMs));
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
            lines.Add($"  [{hk.LaunchAll}]  Launch two clients");

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
        "LaunchTwo" => "Launch two clients",
        "LaunchAll" => "Launch Team 1",
        "LoginAll" => "Auto-login Team 1",
        "LoginAll2" => "Auto-login Team 2",
        "LoginAll3" => "Auto-login Team 3",
        "LoginAll4" => "Auto-login Team 4",
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

    private void ShowSettings(int tabIndex = 0)
    {
        // Prevent multiple settings windows
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.BringToFront();
            return;
        }

        // Suspend hotkeys while Settings is open so keys like ] can be typed into fields
        _hotkeyManager.UnregisterAll();
        _keyboardHook.Reset();

        _settingsForm = new SettingsForm(_config, ReloadConfig, tabIndex, ShowProcessManager, UpdateHookConfig);
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
        switch (action)
        {
            case "FixWindows":
                OnArrangeWindows();
                ShowBalloon("Fix Windows");
                break;
            case "TogglePiP":
                TogglePip();
                break;
            case "LaunchOne":
                ShowBalloon("Launching client...");
                OnLaunchOne();
                break;
            case "LaunchAll":
                FireTeamLogin(
                    new[] { (_config.Team1Account1, "Team 1 Slot 1"), (_config.Team1Account2, "Team 1 Slot 2") },
                    "Team 1");
                break;
            case "LaunchTwo":
                ShowBalloon("Launching two clients...");
                OnLaunchAll();
                break;
            case "AutoLogin1":
                _ = ExecuteQuickLogin(_config.QuickLogin1, "Quick Login 1");
                break;
            case "AutoLogin2":
                _ = ExecuteQuickLogin(_config.QuickLogin2, "Quick Login 2");
                break;
            case "AutoLogin3":
                _ = ExecuteQuickLogin(_config.QuickLogin3, "Quick Login 3");
                break;
            case "AutoLogin4":
                _ = ExecuteQuickLogin(_config.QuickLogin4, "Quick Login 4");
                break;
            case "LoginAll":
                FireTeamLogin(
                    new[] { (_config.Team1Account1, "Team 1 Slot 1"), (_config.Team1Account2, "Team 1 Slot 2") },
                    "Team 1");
                break;
            case "LoginAll2":
                FireTeamLogin(
                    new[] { (_config.Team2Account1, "Team 2 Slot 1"), (_config.Team2Account2, "Team 2 Slot 2") },
                    "Team 2");
                break;
            case "LoginAll3":
                FireTeamLogin(
                    new[] { (_config.Team3Account1, "Team 3 Slot 1"), (_config.Team3Account2, "Team 3 Slot 2") },
                    "Team 3");
                break;
            case "LoginAll4":
                FireTeamLogin(
                    new[] { (_config.Team4Account1, "Team 4 Slot 1"), (_config.Team4Account2, "Team 4 Slot 2") },
                    "Team 4");
                break;
            case "Settings":
                ShowSettings();
                break;
            case "SwapWindows":
                var swapClients = _processManager.Clients;
                if (swapClients.Count >= 2)
                {
                    _windowManager.SwapWindows(swapClients);
                    _windowManager.ResizeToCurrentMonitors(swapClients);
                    UpdateHookConfig();
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


    private Task ExecuteQuickLogin(string username, string slotName)
    {
        if (string.IsNullOrEmpty(username))
        {
            ShowBalloon($"{slotName}: no account assigned");
            return Task.CompletedTask;
        }
        var account = _config.Accounts.FirstOrDefault(a => a.Username == username);
        if (account == null)
        {
            ShowBalloon($"{slotName}: account '{username}' not found");
            return Task.CompletedTask;
        }
        var label = string.IsNullOrEmpty(account.CharacterName) ? account.Username : account.CharacterName;
        ShowBalloon($"Logging in {label}...");
        try { return _autoLoginManager.LoginAccount(account); }
        catch (Exception ex)
        {
            FileLogger.Error($"AutoLogin CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
            ShowBalloon($"Login error: {ex.Message}");
            return Task.CompletedTask;
        }
    }

    private void FireTeamLogin((string username, string label)[] slots, string teamName)
    {
        int fired = 0;
        foreach (var (user, name) in slots)
        {
            if (!string.IsNullOrEmpty(user))
            {
                _ = ExecuteQuickLogin(user, name);
                fired++;
            }
        }
        if (fired == 0)
        {
            ShowBalloon($"No accounts assigned to {teamName} — configure in Settings → Accounts");
            FileLogger.Warn($"FireTeamLogin: {teamName} has no accounts assigned");
        }
    }

    // ─── Config Reload ─────────────────────────────────────────────

    /// <summary>
    /// Re-apply config changes without restarting the app.
    /// Called after the Settings GUI saves new values.
    /// </summary>
    public void ReloadConfig(AppConfig newConfig)
    {
        _reloading = true;
        // Update the config reference (AppConfig is a class, so updating fields in-place)
        _config.EQPath = newConfig.EQPath;
        _config.EQProcessName = newConfig.EQProcessName;
        _config.Layout.SnapToMonitor = newConfig.Layout.SnapToMonitor;
        _config.Layout.TargetMonitor = newConfig.Layout.TargetMonitor;
        _config.Layout.SecondaryMonitor = newConfig.Layout.SecondaryMonitor;
        _config.Layout.TopOffset = newConfig.Layout.TopOffset;
        _config.Layout.SlimTitlebar = newConfig.Layout.SlimTitlebar;
        _config.Layout.TitlebarOffset = newConfig.Layout.TitlebarOffset;
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
        _config.Launch.NumClients = newConfig.Launch.NumClients;
        _config.Launch.LaunchDelayMs = newConfig.Launch.LaunchDelayMs;
        _config.Launch.FixDelayMs = newConfig.Launch.FixDelayMs;
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
        _config.NotesPath = newConfig.NotesPath;
        _config.DalayaPatcherPath = newConfig.DalayaPatcherPath;
        _config.Characters = newConfig.Characters;
        _config.Accounts = newConfig.Accounts;
        _config.QuickLogin1 = newConfig.QuickLogin1;
        _config.QuickLogin2 = newConfig.QuickLogin2;
        _config.QuickLogin3 = newConfig.QuickLogin3;
        _config.QuickLogin4 = newConfig.QuickLogin4;
        _config.Team1Account1 = newConfig.Team1Account1;
        _config.Team1Account2 = newConfig.Team1Account2;
        _config.Team2Account1 = newConfig.Team2Account1;
        _config.Team2Account2 = newConfig.Team2Account2;
        _config.Team3Account1 = newConfig.Team3Account1;
        _config.Team3Account2 = newConfig.Team3Account2;
        _config.Team4Account1 = newConfig.Team4Account1;
        _config.Team4Account2 = newConfig.Team4Account2;
        _config.AutoEnterWorld = newConfig.AutoEnterWorld;
        _config.LoginScreenDelayMs = newConfig.LoginScreenDelayMs;
        _config.TooltipDurationMs = newConfig.TooltipDurationMs;
        _config.ShowTooltipErrors = newConfig.ShowTooltipErrors;
        _config.MinimizeToTray = newConfig.MinimizeToTray;
        _config.RunAtStartup = newConfig.RunAtStartup;
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

        // Re-register hotkeys if they changed
        _hotkeyManager.UnregisterAll();
        _keyboardHook.Reset();
        RegisterHotkeys();

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
            bool hookActive = _injectedPids.Count > 0;
            _slimTitlebarGuard = new System.Windows.Forms.Timer { Interval = hookActive ? 5000 : 500 };
            _slimTitlebarGuard.Tick += (_, _) =>
                _windowManager.ApplySlimTitlebarToAll(_processManager.Clients, _injectedPids);
            if (!_processManager.Clients.Any(c => _autoLoginManager.IsLoginActive(c.ProcessId)))
                _slimTitlebarGuard.Start();
        }

        // Auto-arrange when multimonitor mode is toggled on
        bool isMultiMon = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        if (isMultiMon && _processManager.Clients.Count > 0)
        {
            _windowManager.ArrangeWindows(_processManager.Clients);
            FileLogger.Info("ReloadConfig: auto-arranged for multimonitor mode");
        }

        // Update hook configs for all injected processes (per-PID shared memory
        // supports both single and multimonitor modes)
        UpdateHookConfig();

        _reloading = false;
        FileLogger.Info("Config reloaded and applied");
        ShowBalloon("Settings applied");
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

        // Write this process's config before injection so the DLL reads correct values on attach
        UpdateHookConfigForPid(pid);

        // Find the DLL next to our exe
        var exeDir = AppContext.BaseDirectory;
        var dllPath = Path.Combine(exeDir, "eqswitch-hook.dll");
        if (!File.Exists(dllPath))
        {
            FileLogger.Warn($"InjectHookDll: DLL not found at {dllPath}");
            _injectedPids.Remove(pid);
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
                FileLogger.Warn($"InjectHookDll: injection failed for PID {pid}, falling back to guard timer");
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Write slim titlebar positions to shared memory for all injected processes.
    /// In multimonitor mode, each process gets a different position based on its monitor.
    /// </summary>
    private void UpdateHookConfig()
    {
        if (_hookConfig == null || !_hookConfig.HasMappings) return;

        foreach (var pid in _injectedPids)
            UpdateHookConfigForPid(pid);
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

        // ─── Position enforcement (slim titlebar) ───
        bool posEnabled = _config.Layout.SlimTitlebar;
        int x = 0, y = 0, w = 0, h = 0;
        if (posEnabled)
        {
            WinRect monBounds;
            bool isMM = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
            monBounds = isMM ? GetMonitorForClientIndex(clientIndex) : _windowManager.GetTargetMonitorBounds();

            x = monBounds.Left;
            y = monBounds.Top - _config.Layout.TitlebarOffset;
            w = monBounds.Right - monBounds.Left;
            h = (monBounds.Bottom - monBounds.Top) + _config.Layout.TitlebarOffset;
        }

        // ─── Window title ───
        string title = "";
        var template = _config.Layout.WindowTitleTemplate;
        if (!string.IsNullOrEmpty(template))
        {
            // Resolve placeholders
            var client = clients[clientIndex];
            var charName = "";
            if (clientIndex < _config.Accounts.Count)
            {
                var accountName = _config.Accounts[clientIndex].CharacterName;
                if (!string.IsNullOrEmpty(accountName))
                    charName = accountName;
            }
            title = template
                .Replace("{CHAR}", charName)
                .Replace("{SLOT}", (clientIndex + 1).ToString())
                .Replace("{PID}", pid.ToString())
                .Trim();
        }

        // ─── Minimize blocking ───
        bool blockMin = _config.EQClientIni.MaximizeWindow;

        _hookConfig.WriteConfig(pid, x, y, w, h,
            enabled: posEnabled, stripThickFrame: posEnabled,
            blockMinimize: blockMin, windowTitle: title);

        var features = new System.Collections.Generic.List<string>();
        if (posEnabled) features.Add($"pos=({x},{y}) {w}x{h}");
        if (!string.IsNullOrEmpty(title)) features.Add($"title=\"{title}\"");
        if (blockMin) features.Add("blockMin");
        FileLogger.Info($"UpdateHookConfig: PID {pid} → {string.Join(", ", features)}");
    }

    /// <summary>
    /// Get the monitor bounds for a client based on its index in the client list.
    /// Mirrors the assignment logic in WindowManager.ArrangeMultiMonitor.
    /// </summary>
    private WinRect GetMonitorForClientIndex(int clientIndex)
    {
        var monitors = _windowManager.GetAllMonitorFullBounds();
        if (monitors.Count == 0)
            return new WinRect { Right = 1920, Bottom = 1080 };

        var primaryIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, monitors.Count - 1);
        int secondaryIdx;
        if (_config.Layout.SecondaryMonitor >= 0 && _config.Layout.SecondaryMonitor < monitors.Count)
            secondaryIdx = _config.Layout.SecondaryMonitor;
        else
            secondaryIdx = primaryIdx == 0 && monitors.Count > 1 ? 1 : 0;

        var monitorOrder = new List<WinRect> { monitors[primaryIdx] };
        if (monitors.Count > 1)
            monitorOrder.Add(monitors[secondaryIdx]);

        return monitorOrder[clientIndex % monitorOrder.Count];
    }

    /// <summary>
    /// Remove legacy proxy DLL files from the game directory and app directory.
    /// Restores Dalaya's original dinput8.dll if we renamed it during chain-load era.
    /// Each operation is independent — failure of one doesn't block the others.
    /// </summary>
    private void CleanupLegacyProxyFiles()
    {
        var eqPath = _config.EQPath;
        if (string.IsNullOrEmpty(eqPath) || !Directory.Exists(eqPath)) return;

        var dinput8Path = Path.Combine(eqPath, "dinput8.dll");
        var dalayaPath = Path.Combine(eqPath, "dinput8_dalaya.dll");

        // 1. If we renamed Dalaya's DLL to dinput8_dalaya.dll, restore it
        try
        {
            if (File.Exists(dalayaPath))
            {
                if (!File.Exists(dinput8Path))
                {
                    File.Move(dalayaPath, dinput8Path);
                    FileLogger.Info("Cleanup: restored dinput8_dalaya.dll → dinput8.dll");
                }
                else
                {
                    File.Delete(dalayaPath);
                    FileLogger.Info("Cleanup: removed stale dinput8_dalaya.dll");
                }
            }
        }
        catch (Exception ex) { FileLogger.Warn($"Cleanup: dalaya DLL restore failed: {ex.Message}"); }

        // 2. If dinput8.dll in game dir is our old proxy (~141-148KB), remove it
        try
        {
            if (File.Exists(dinput8Path) && !File.Exists(dalayaPath))
            {
                var info = new FileInfo(dinput8Path);
                if (info.Length < 200_000) // Ours is ~148KB, Dalaya's is ~1.3MB
                {
                    File.Delete(dinput8Path);
                    FileLogger.Info($"Cleanup: removed legacy proxy dinput8.dll from game folder ({info.Length:N0} bytes)");
                }
            }
        }
        catch (Exception ex) { FileLogger.Warn($"Cleanup: game dir proxy removal failed: {ex.Message}"); }

        // 3. Remove legacy dinput8.dll from app directory (no longer shipped)
        try
        {
            var appDinput8 = Path.Combine(AppContext.BaseDirectory, "dinput8.dll");
            if (File.Exists(appDinput8))
            {
                File.Delete(appDinput8);
                FileLogger.Info("Cleanup: removed legacy dinput8.dll from app folder");
            }
        }
        catch (Exception ex) { FileLogger.Warn($"Cleanup: app dir proxy removal failed: {ex.Message}"); }
    }

    private void CleanupHookInjection()
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
        CleanupHookInjection();
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
        StopForegroundHook();
        CleanupHookInjection();
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _launchManager.Dispose();
        _leftClickTimer?.Stop();
        _leftClickTimer?.Dispose();
        _middleClickTimer?.Stop();
        _middleClickTimer?.Dispose();
        _deferTimer?.Stop();
        _deferTimer?.Dispose();
        // _foregroundDebounceTimer already disposed by StopForegroundHook() above
        _foregroundHookProc = null; // safe to release now — message pump fully drained at shutdown
        _boldMenuFont?.Dispose();
        _pipOverlay?.Dispose();
        _hotkeyManager.Dispose();
        _keyboardHook.Dispose();
        _taskbarMessageWindow?.DestroyHandle();
        _trayIcon?.Dispose();
        _contextMenu?.Dispose();
        _processManager.Dispose();
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
    private static readonly Color MenuBg = DarkTheme.BgDark;           // (32, 28, 42)
    private static readonly Color MenuBorder = DarkTheme.Border;       // (64, 56, 78)
    private static readonly Color ItemHover = DarkTheme.BgHover;       // (64, 56, 78)
    private static readonly Color ItemText = DarkTheme.FgWhite;        // (235, 232, 240)
    private static readonly Color DisabledText = DarkTheme.FgDimGray;  // (120, 112, 135)
    private static readonly Color SepColor = DarkTheme.Border;         // (64, 56, 78)
    private static readonly Color CheckBg = DarkTheme.AccentGreen;     // (0, 140, 80)
    private static readonly Color MarginBg = DarkTheme.BgPanel;        // (38, 33, 48)

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

        e.Item.ForeColor = e.Item.Enabled ? ItemText : DisabledText;
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
