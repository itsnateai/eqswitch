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
    // Left: MouseDoubleClick handles it, so this just delays single-click resolution.
    private static readonly int LeftClickResolveMs = SystemInformation.DoubleClickTime + 50;
    // Middle: no MouseDoubleClick event, so we count clicks. Longer window for reliability.
    private static readonly int MiddleClickResolveMs = SystemInformation.DoubleClickTime * 2;

    private readonly AppConfig _config;
    private readonly ProcessManager _processManager;
    private readonly WindowManager _windowManager;
    private readonly HotkeyManager _hotkeyManager;
    private readonly KeyboardHookManager _keyboardHook;
    private readonly AffinityManager _affinityManager;
    private readonly LaunchManager _launchManager;

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

    // Debounce timestamp for multi-monitor toggle (500ms)
    private long _lastMultiMonToggle;

    // PiP overlay
    private PipOverlay? _pipOverlay;

    // Process Manager (single-instance)
    private ProcessManagerForm? _processManagerForm;

    // Tray click detection
    private System.Windows.Forms.Timer? _leftClickTimer;
    // Middle button: MouseDoubleClick doesn't fire, so we count clicks manually
    private int _middleClickCount;
    private System.Windows.Forms.Timer? _middleClickTimer;


    // Track last two active clients for swap-last-two mode (by PID, not handle — handles can change)
    private int _lastActivePid;
    private int _previousActivePid;

    public TrayManager(AppConfig config, ProcessManager processManager)
    {
        _config = config;
        _processManager = processManager;
        _windowManager = new WindowManager(config);
        _hotkeyManager = new HotkeyManager();
        _keyboardHook = new KeyboardHookManager();
        _affinityManager = new AffinityManager(config);
        _launchManager = new LaunchManager(config, _affinityManager);
    }

    public void Initialize()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "EQSwitch - 0 clients",
            Visible = true
        };

        // Assign context menu only on right-click, remove on left/middle to prevent
        // Windows from showing the menu and stealing focus from double-click detection
        _trayIcon.MouseDown += (_, e) =>
        {
            _trayIcon.ContextMenuStrip = e.Button == MouseButtons.Right ? _contextMenu : null;
            // Handle middle button on MouseDown — MouseClick swallows rapid
            // middle clicks on the tiny tray icon because any slight movement
            // between press and release kills the Click event.
            if (e.Button == MouseButtons.Middle)
            {
                _middleClickCount++;
                FileLogger.Info($"TrayClick: middle click #{_middleClickCount}");
                EnsureTimer(ref _middleClickTimer, MiddleClickResolveMs, OnMiddleResolved);
            }
        };
        _trayIcon.MouseClick += OnTrayMouseClick;
        _trayIcon.MouseDoubleClick += OnTrayMouseDoubleClick;

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
            // Update cached PIDs for keyboard hook process filter
            _keyboardHook.UpdateFilteredPids(_processManager.Clients.Select(c => c.ProcessId));
        };
        _processManager.ClientDiscovered += (_, c) =>
        {
            // NO tooltip here — creating TopMost windows during EQ's DirectX init
            // causes the game to lose foreground and minimize itself
            _affinityManager.ScheduleRetry(c);
        };
        _processManager.ClientLost += (_, c) =>
        {
            if (_contextMenu?.Visible != true)
                ShowBalloon($"Lost: {c}");
            _affinityManager.CancelRetry(c.ProcessId);
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
        RegisterHotkeys();
        StartForegroundHook();
        StartRetryTimer();
        StartupManager.MigrateFromRegistry();
        StartupManager.ValidateStartupPath(_config);

        // Log core detection at startup
        var (cores, sysMask) = AffinityManager.DetectCores();
        FileLogger.Info($"Startup: {cores} cores detected, system mask 0x{sysMask:X}");

        ShowBalloon("EQSwitch started. Watching for EQ clients...");
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
        TryRegister(hk.LaunchAll, OnLaunchAll, "LaunchAll");

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
    /// Alt+G: Arrange all EQ windows in a grid on the target monitor.
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
        FileLogger.Info($"ArrangeWindows: arranged {clients.Count} client(s)");
        ShowBalloon($"Arranged {clients.Count} window(s) in grid");
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
            _windowManager.ArrangeWindows(clients);
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
                _foregroundDebounceTimer.Tick += (_, _) =>
                {
                    _foregroundDebounceTimer.Stop();
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
        var active = _processManager.GetActiveClient();
        var clients = _processManager.Clients;

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
            if (clients.Count < 2)
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

        var launchAllItem = new ToolStripMenuItem($"\uD83C\uDFAE  Launch All ({_config.Launch.NumClients}){HkSuffix(hk.LaunchAll)}") { Font = _boldMenuFont };
        launchAllItem.Click += (_, _) => OnLaunchAll();
        _contextMenu.Items.Add(launchAllItem);

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
            using var form = new VideoSettingsForm(_config);
            form.ShowDialog();
        });
        videoMenu.DropDownItems.Add(new ToolStripSeparator());
        videoMenu.DropDownItems.Add("Toggle PiP  \uD83D\uDC41", null, (_, _) => TogglePip());
        videoMenu.DropDownItems.Add(new ToolStripSeparator());
        videoMenu.DropDownItems.Add($"Fix Windows  \uD83D\uDD27{HkSuffix(hk.ArrangeWindows)}", null, (_, _) => OnArrangeWindows());
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

        // Settings submenu
        var settingsMenu = new ToolStripMenuItem("\u2699  Settings") { DropDownDirection = ToolStripDropDownDirection.Right };
        settingsMenu.DropDownItems.Add("\u2753  Help", null, (_, _) => HelpForm.Show(_config));
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        settingsMenu.DropDownItems.Add("Create Desktop Shortcut", null, (_, _) => StartupManager.CreateDesktopShortcut(ShowBalloon));
        var startupItem = new ToolStripMenuItem(_config.RunAtStartup ? "\u2705  Run at Startup" : "\u2B1C  Run at Startup")
        {
            Checked = _config.RunAtStartup,
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) =>
        {
            _config.RunAtStartup = startupItem.Checked;
            startupItem.Text = startupItem.Checked ? "\u2705  Run at Startup" : "\u2B1C  Run at Startup";
            StartupManager.SetRunAtStartup(startupItem.Checked);
            ConfigManager.Save(_config);
        };
        settingsMenu.DropDownItems.Add(startupItem);
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        settingsMenu.DropDownItems.Add("\u2699  Settings...", null, (_, _) => ShowSettings());
        _contextMenu.Items.Add(settingsMenu);

        // Launcher submenu (files + links)
        var launcherMenu = new ToolStripMenuItem("\uD83D\uDCC2  Launcher") { DropDownDirection = ToolStripDropDownDirection.Right };
        var linksMenu = new ToolStripMenuItem("\uD83C\uDF10  Links");
        linksMenu.DropDownItems.Add("\uD83C\uDFE0  Dalaya", null, (_, _) => FileOperations.OpenUrl("https://dalaya.org/"));
        linksMenu.DropDownItems.Add(new ToolStripSeparator());
        linksMenu.DropDownItems.Add("\uD83D\uDDE1  Shards Wiki", null, (_, _) => FileOperations.OpenUrl("https://wiki.shardsofdalaya.com/wiki/Main_Page"));
        linksMenu.DropDownItems.Add("\uD83D\uDCD6  Dalaya Wiki", null, (_, _) => FileOperations.OpenUrl("https://wiki.dalaya.org/"));
        linksMenu.DropDownItems.Add("\uD83C\uDFC6  Fomelo Dalaya", null, (_, _) => FileOperations.OpenUrl("https://dalaya.org/fomelo/"));
        linksMenu.DropDownItems.Add("\uD83D\uDCDC  Dalaya Listsold", null, (_, _) => FileOperations.OpenUrl("https://dalaya.org/listsold.php"));
        launcherMenu.DropDownItems.Add("\uD83D\uDD27  Dalaya Patcher", null, (_, _) => FileOperations.OpenDalayaPatcher(_config, ShowBalloon, () => ShowSettings(4)));
        launcherMenu.DropDownItems.Add(new ToolStripSeparator());
        launcherMenu.DropDownItems.Add("\uD83D\uDCDC  Open Log File...", null, (_, _) => FileOperations.OpenLogFile(_config, ShowBalloon));
        launcherMenu.DropDownItems.Add("\uD83D\uDCC4  Open eqclient.ini", null, (_, _) => FileOperations.OpenEqClientIni(_config, ShowBalloon));
        launcherMenu.DropDownItems.Add(new ToolStripSeparator());
        launcherMenu.DropDownItems.Add(linksMenu);
        launcherMenu.DropDownItems.Add(new ToolStripSeparator());
        launcherMenu.DropDownItems.Add("\uD83C\uDFAF  Open GINA", null, (_, _) => FileOperations.OpenGina(_config, ShowBalloon, () => ShowSettings(4)));
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
        FileLogger.Info($"TogglePip: called, clients={_processManager.Clients.Count}, overlay={_pipOverlay != null}");
        if (_pipOverlay != null && !_pipOverlay.IsDisposed)
        {
            _pipOverlay.Close();
            _pipOverlay.Dispose();
            _pipOverlay = null;
            ShowBalloon("PiP overlay hidden");
            return;
        }

        var clients = _processManager.Clients;
        if (clients.Count < 2)
        {
            ShowBalloon("Need 2+ clients for PiP overlay");
            return;
        }

        _pipOverlay = new PipOverlay(_config);
        _pipOverlay.Show();
        _pipOverlay.UpdateSources(clients, _processManager.GetActiveClient());
        ShowBalloon("PiP overlay shown");
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
            lines.Add($"  [{hk.ArrangeWindows}]  Arrange windows in grid");
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

    private static string FormatActionName(string action) => action switch
    {
        "FixWindows" => "Arrange windows",
        "SwapWindows" => "Swap positions",
        "TogglePiP" => "Toggle PiP",
        "LaunchOne" => "Launch one",
        "LaunchAll" => "Launch all",
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

        _settingsForm = new SettingsForm(_config, ReloadConfig, tabIndex, ShowProcessManager);
        _settingsForm.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            _hotkeyManager.UnregisterAll();
            _keyboardHook.Reset();
            RegisterHotkeys();
        };
        _settingsForm.Show();
    }

    /// <summary>
    /// Left: single click starts a timer. MouseDoubleClick cancels it.
    /// Middle: MouseDoubleClick doesn't fire, so we count clicks with a timer.
    /// </summary>
    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            FileLogger.Info("TrayClick: left click");
            EnsureTimer(ref _leftClickTimer, LeftClickResolveMs, OnLeftSingleResolved);
        }
        // Middle button handled in MouseDown for reliability — see comment there
    }

    /// <summary>
    /// Double-click: cancels pending single-click timer. Only fires for left button.
    /// </summary>
    private void OnTrayMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _leftClickTimer?.Stop();
            FileLogger.Info($"TrayClick: left double → {_config.TrayClick.DoubleClick}");
            ExecuteTrayAction(_config.TrayClick.DoubleClick);
        }
        else if (e.Button == MouseButtons.Middle)
        {
            // Windows converts the 2nd middle click into MouseDoubleClick
            // instead of a 2nd MouseDown — count it here too
            _middleClickCount++;
            FileLogger.Info($"TrayClick: middle click #{_middleClickCount} (via dblclick)");
            EnsureTimer(ref _middleClickTimer, MiddleClickResolveMs, OnMiddleResolved);
        }
    }

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

    private void OnLeftSingleResolved(object? sender, EventArgs e)
    {
        _leftClickTimer!.Stop();
        FileLogger.Info($"TrayClick: left single → {_config.TrayClick.SingleClick}");
        ExecuteTrayAction(_config.TrayClick.SingleClick);
    }

    private void OnMiddleResolved(object? sender, EventArgs e)
    {
        _middleClickTimer!.Stop();
        int clicks = _middleClickCount;
        _middleClickCount = 0;
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
                ShowBalloon($"Launching {_config.Launch.NumClients} clients...");
                OnLaunchAll();
                break;
            case "Settings":
                ShowSettings();
                break;
            case "RefreshClients":
                _processManager.RefreshClients();
                ShowBalloon($"Found {_processManager.ClientCount} EQ client(s)");
                break;
            case "ShowHelp":
                ShowHelpTooltip();
                break;
            case "None":
            default:
                break;
        }
    }


    // ─── Config Reload ─────────────────────────────────────────────

    /// <summary>
    /// Re-apply config changes without restarting the app.
    /// Called after the Settings GUI saves new values.
    /// </summary>
    public void ReloadConfig(AppConfig newConfig)
    {
        // Update the config reference (AppConfig is a class, so updating fields in-place)
        _config.EQPath = newConfig.EQPath;
        _config.EQProcessName = newConfig.EQProcessName;
        _config.Layout.Columns = newConfig.Layout.Columns;
        _config.Layout.Rows = newConfig.Layout.Rows;
        _config.Layout.SnapToMonitor = newConfig.Layout.SnapToMonitor;
        _config.Layout.TargetMonitor = newConfig.Layout.TargetMonitor;
        _config.Layout.SecondaryMonitor = newConfig.Layout.SecondaryMonitor;
        _config.Layout.TopOffset = newConfig.Layout.TopOffset;
        _config.Layout.SlimTitlebar = newConfig.Layout.SlimTitlebar;
        _config.Layout.TitlebarOffset = newConfig.Layout.TitlebarOffset;
        _config.Layout.WindowTitleTemplate = newConfig.Layout.WindowTitleTemplate;
        _config.Layout.Mode = newConfig.Layout.Mode;
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
        _config.Pip.ShowBorder = newConfig.Pip.ShowBorder;
        _config.Pip.BorderColor = newConfig.Pip.BorderColor;
        _config.Pip.MaxWindows = newConfig.Pip.MaxWindows;
        _config.TrayClick.SingleClick = newConfig.TrayClick.SingleClick;
        _config.TrayClick.DoubleClick = newConfig.TrayClick.DoubleClick;
        _config.TrayClick.MiddleClick = newConfig.TrayClick.MiddleClick;
        _config.TrayClick.MiddleDoubleClick = newConfig.TrayClick.MiddleDoubleClick;
        _config.GinaPath = newConfig.GinaPath;
        _config.NotesPath = newConfig.NotesPath;
        _config.DalayaPatcherPath = newConfig.DalayaPatcherPath;
        _config.Characters = newConfig.Characters;
        _config.TooltipDurationMs = newConfig.TooltipDurationMs;
        _config.ShowTooltipErrors = newConfig.ShowTooltipErrors;
        _config.MinimizeToTray = newConfig.MinimizeToTray;
        _config.RunAtStartup = newConfig.RunAtStartup;
        _config.DisableEQLog = newConfig.DisableEQLog;
        _config.CustomVideoPresets = newConfig.CustomVideoPresets;
        _config.EQClientIni = newConfig.EQClientIni;

        // Update icon if path changed
        var iconChanged = _config.CustomIconPath != newConfig.CustomIconPath;
        _config.CustomIconPath = newConfig.CustomIconPath;
        if (iconChanged && _trayIcon != null)
        {
            _trayIcon.Icon = LoadIcon();
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

        // Re-install foreground hook (in case it was lost) and restart retry timer
        StopForegroundHook();
        StartForegroundHook();
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _retryTimer = null;
        StartRetryTimer();

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

    private void StopForegroundHook()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        _foregroundHookProc = null;
        _foregroundDebounceTimer?.Stop();
        _foregroundDebounceTimer?.Dispose();
        _foregroundDebounceTimer = null;
        _affinityFallbackTimer?.Stop();
        _affinityFallbackTimer?.Dispose();
        _affinityFallbackTimer = null;
    }

    private void Shutdown()
    {
        StopForegroundHook();
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _launchManager.CancelLaunch();
        _pipOverlay?.Dispose();
        _pipOverlay = null;
        _boldMenuFont?.Dispose();
        _boldMenuFont = null;
        _hotkeyManager.Dispose();
        _keyboardHook.Dispose();
        _processManager.StopPolling();
        _contextMenu?.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        Application.Exit();
    }

    private Icon LoadIcon()
    {
        // Dispose previous custom icon to prevent handle leak on reload
        var oldIcon = _trayIcon?.Icon;

        Icon newIcon;
        try
        {
            // Priority 1: User-selected custom icon path from settings
            if (!string.IsNullOrEmpty(_config.CustomIconPath) && File.Exists(_config.CustomIconPath))
            {
                newIcon = new Icon(_config.CustomIconPath, 32, 32);
                FileLogger.Info($"Icon: loaded custom icon from {_config.CustomIconPath}");
            }
            else
            {
                // Priority 2: Default embedded Stone icon (eqswitch.ico — the primary embedded icon)
                var stream = typeof(TrayManager).Assembly.GetManifestResourceStream("EQSwitch.eqswitch.ico");
                if (stream != null)
                {
                    newIcon = new Icon(stream, 32, 32);
                }
                else
                {
                    // Priority 3: Fall back to file on disk (dev/debug builds)
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var iconPath = Path.Combine(baseDir, "eqswitch-alt.ico");
                    if (!File.Exists(iconPath))
                        iconPath = Path.Combine(baseDir, "eqswitch.ico");
                    newIcon = File.Exists(iconPath) ? new Icon(iconPath, 32, 32) : SystemIcons.Application;
                }
            }
        }
        catch
        {
            newIcon = SystemIcons.Application;
        }

        // Only dispose if it was a custom icon (not a system icon)
        if (oldIcon != null && oldIcon != SystemIcons.Application)
        {
            try { oldIcon.Dispose(); } catch { }
        }

        return newIcon;
    }

    public void Dispose()
    {
        StopForegroundHook();
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _launchManager.Dispose();
        _leftClickTimer?.Stop();
        _leftClickTimer?.Dispose();
        _middleClickTimer?.Stop();
        _middleClickTimer?.Dispose();
        _deferTimer?.Stop();
        _deferTimer?.Dispose();
        _foregroundDebounceTimer?.Stop();
        _foregroundDebounceTimer?.Dispose();
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
