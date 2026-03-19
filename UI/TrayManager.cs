using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

public class TrayManager : IDisposable
{
    // ─── Constants ───────────────────────────────────────────────────
    private const int MultiMonToggleDebounceMs = 500;
    private const int AffinityPollIntervalMs = 250;
    private const int ClickResolveDelayMs = 350; // Wait this long after last click to resolve action

    private readonly AppConfig _config;
    private readonly ProcessManager _processManager;
    private readonly WindowManager _windowManager;
    private readonly HotkeyManager _hotkeyManager;
    private readonly KeyboardHookManager _keyboardHook;
    private readonly AffinityManager _affinityManager;
    private readonly ThrottleManager _throttleManager;
    private readonly LaunchManager _launchManager;

    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _contextMenu;
    private ToolStripMenuItem? _clientsMenu;
    private Font? _boldMenuFont;

    // Timer that checks foreground window changes and applies affinity rules
    private System.Windows.Forms.Timer? _affinityTimer;
    // Timer for affinity retry attempts on newly launched clients
    private System.Windows.Forms.Timer? _retryTimer;

    // Debounce timestamp for multi-monitor toggle (500ms)
    private long _lastMultiMonToggle;

    // PiP overlay
    private PipOverlay? _pipOverlay;

    // Process Manager (single-instance)
    private ProcessManagerForm? _processManagerForm;

    // Tray click detection (single/double/triple with delayed resolution)
    private int _trayClickCount;
    private int _trayMiddleClickCount;
    private System.Windows.Forms.Timer? _clickResolveTimer;
    private System.Windows.Forms.Timer? _middleClickResolveTimer;

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
        _throttleManager = new ThrottleManager(config);
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

        // Tray click events — delayed resolution for clean single/double/triple
        _trayIcon.MouseClick += OnTrayMouseClick;

        // Ctrl+hover help — event-driven, zero CPU when not hovering
        _trayIcon.MouseMove += (_, e) =>
        {
            if (_config.CtrlHoverHelp && (Control.ModifierKeys & Keys.Control) != 0)
            {
                ShowHelpTooltip();
            }
        };

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
            ShowBalloon($"Discovered: {c}");
            _affinityManager.ScheduleRetry(c);
        };
        _processManager.ClientLost += (_, c) =>
        {
            ShowBalloon($"Lost: {c}");
            _affinityManager.CancelRetry(c.ProcessId);
        };

        _launchManager.ProgressUpdate += (_, msg) => ShowBalloon(msg);
        _launchManager.LaunchSequenceComplete += (_, _) =>
        {
            // Arrange windows after all clients launched
            var clients = _processManager.Clients;
            if (clients.Count > 0)
                _windowManager.ArrangeWindows(clients);
        };

        _processManager.StartPolling();
        RegisterHotkeys();
        StartAffinityTimer();
        _throttleManager.Start();
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

        // Alt+1 through Alt+6 — direct switch to client by slot
        for (int i = 0; i < hk.DirectSwitchKeys.Count; i++)
        {
            int slot = i; // capture for closure
            int id = _hotkeyManager.Register(hk.DirectSwitchKeys[i], () => OnDirectSwitch(slot));
            if (id > 0) registered++; else failed++;
        }

        // Alt+G — arrange windows in grid
        if (!string.IsNullOrEmpty(hk.ArrangeWindows))
        {
            int id = _hotkeyManager.Register(hk.ArrangeWindows, OnArrangeWindows);
            if (id > 0) registered++; else failed++;
        }

        // Alt+M — toggle multimonitor mode
        if (!string.IsNullOrEmpty(hk.ToggleMultiMonitor))
        {
            int id = _hotkeyManager.Register(hk.ToggleMultiMonitor, OnToggleMultiMonitor);
            if (id > 0) registered++; else failed++;
        }

        // Launch hotkeys (register only if configured)
        if (!string.IsNullOrEmpty(hk.LaunchOne))
        {
            int id = _hotkeyManager.Register(hk.LaunchOne, OnLaunchOne);
            if (id > 0) registered++; else failed++;
        }
        if (!string.IsNullOrEmpty(hk.LaunchAll))
        {
            int id = _hotkeyManager.Register(hk.LaunchAll, OnLaunchAll);
            if (id > 0) registered++; else failed++;
        }

        FileLogger.Info($"RegisterHotKey: {registered} registered, {failed} failed");

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

        if (_config.Hotkeys.SwitchKeyMode == "swapLast")
        {
            // Alt+Tab style: swap to the previous active client (matched by PID — handles can change)
            if (_previousActivePid != 0)
            {
                var target = clients.FirstOrDefault(c => c.ProcessId == _previousActivePid);
                if (target != null)
                {
                    _windowManager.SwitchToClient(target);
                    FileLogger.Info($"SwitchKey: swapped to last active {target}");
                    return;
                }
            }
            // Fallback to cycle if no previous client tracked
            var next = _windowManager.CycleNext(clients, current);
            if (next != null)
                FileLogger.Info($"SwitchKey: cycled (no previous tracked) to {next}");
        }
        else
        {
            // Cycle through all clients round-robin
            var next = _windowManager.CycleNext(clients, current);
            if (next != null)
                FileLogger.Info($"SwitchKey: cycled to {next}");
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

        if (current != null)
        {
            // EQ is focused — cycle to next
            var next = _windowManager.CycleNext(clients, current);
            if (next != null)
                FileLogger.Info($"GlobalSwitchKey: cycled to {next}");
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
        if (!_config.Hotkeys.MultiMonitorEnabled)
        {
            FileLogger.Info("ToggleMultiMonitor: disabled in config");
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastMultiMonToggle < MultiMonToggleDebounceMs)
            return;
        _lastMultiMonToggle = now;

        // Toggle the mode
        bool isMulti = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        _config.Layout.Mode = isMulti ? "single" : "multimonitor";

        string label = isMulti ? "Single Screen" : "Multi-Monitor";
        FileLogger.Info($"ToggleMultiMonitor: switched to {label}");
        ShowBalloon($"Layout: {label}");

        // Save the mode change
        ConfigManager.Save(_config);

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

    private void StartAffinityTimer()
    {
        if (!_config.Affinity.Enabled) return;

        _affinityTimer = new System.Windows.Forms.Timer { Interval = AffinityPollIntervalMs };
        _affinityTimer.Tick += (_, _) =>
        {
            var clients = _processManager.Clients; // snapshot once per tick
            var active = _processManager.GetActiveClient();
            _affinityManager.ApplyAffinityRules(clients, active);
            _throttleManager.UpdateClients(clients, active);

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
                    // Auto-destroy PiP when fewer than 2 windows
                    _pipOverlay.Close();
                    _pipOverlay.Dispose();
                    _pipOverlay = null;
                }
                else
                {
                    _pipOverlay.UpdateSources(clients, active);
                }
            }
        };
        _affinityTimer.Start();

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

        FileLogger.Info("Affinity timers started (250ms check, retry every " +
            $"{_config.Affinity.LaunchRetryDelayMs}ms)");
    }

    // ─── Tray UI ─────────────────────────────────────────────────────

    private void BuildContextMenu()
    {
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Renderer = new DarkMenuRenderer();

        var hk = _config.Hotkeys;
        string HkSuffix(string key) => string.IsNullOrEmpty(key) ? "" : $"\t{key}";

        _boldMenuFont?.Dispose();
        _boldMenuFont = new Font(_contextMenu.Font, FontStyle.Bold);

        // Title bar
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
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
        var videoMenu = new ToolStripMenuItem("\uD83D\uDCFA  Video Settings");
        videoMenu.DropDownItems.Add($"\uD83E\uDE9F  Fix Windows{HkSuffix(hk.ArrangeWindows)}", null, (_, _) => OnArrangeWindows());
        videoMenu.DropDownItems.Add("\uD83D\uDD04  Swap Windows", null, (_, _) =>
        {
            var clients = _processManager.Clients;
            if (clients.Count < 2)
            {
                ShowBalloon("Need 2+ windows to swap");
                return;
            }
            _windowManager.SwapWindows(clients);
            ShowBalloon($"Swapped {clients.Count} window positions");
        });
        videoMenu.DropDownItems.Add("\uD83D\uDCFA  Toggle PiP", null, (_, _) => TogglePip());
        videoMenu.DropDownItems.Add(new ToolStripSeparator());
        videoMenu.DropDownItems.Add("\uD83D\uDCDD  Edit eqclient.ini...", null, (_, _) =>
        {
            using var form = new VideoSettingsForm(_config);
            form.ShowDialog();
        });
        _contextMenu.Items.Add(videoMenu);

        // CPU Affinity submenu
        var (coreCount, _) = AffinityManager.DetectCores();
        var affinityMenu = new ToolStripMenuItem("\uD83E\uDDE0  CPU Affinity");
        var affinityEnabledItem = new ToolStripMenuItem(_config.Affinity.Enabled ? "\u2705  Enabled" : "\u2B1C  Disabled")
        {
            Checked = _config.Affinity.Enabled,
            CheckOnClick = true
        };
        affinityEnabledItem.CheckedChanged += (_, _) =>
        {
            _config.Affinity.Enabled = affinityEnabledItem.Checked;
            affinityEnabledItem.Text = affinityEnabledItem.Checked ? "\u2705  Enabled" : "\u2B1C  Disabled";
            ConfigManager.Save(_config);
            if (affinityEnabledItem.Checked)
            {
                _affinityManager.ForceApplyAffinityRules(_processManager.Clients, _processManager.GetActiveClient());
                ShowBalloon("CPU affinity enabled");
            }
            else
            {
                ShowBalloon("CPU affinity disabled");
            }
        };
        affinityMenu.DropDownItems.Add(affinityEnabledItem);
        affinityMenu.DropDownItems.Add(new ToolStripSeparator());

        // Active priority selector
        var activePriorityMenu = new ToolStripMenuItem("Active Priority");
        foreach (var priority in new[] { "High", "AboveNormal", "Normal" })
        {
            var p = priority;
            var item = new ToolStripMenuItem(p)
            {
                Checked = _config.Affinity.ActivePriority.Equals(p, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (sender, _) =>
            {
                // Update checkmarks
                foreach (ToolStripMenuItem sibling in activePriorityMenu.DropDownItems)
                    sibling.Checked = false;
                ((ToolStripMenuItem)sender!).Checked = true;

                _config.Affinity.ActivePriority = p;
                ConfigManager.Save(_config);
                _affinityManager.ForceApplyAffinityRules(_processManager.Clients, _processManager.GetActiveClient());
                ShowBalloon($"Active priority: {p}");
            };
            activePriorityMenu.DropDownItems.Add(item);
        }
        affinityMenu.DropDownItems.Add(activePriorityMenu);

        // Background priority selector
        var bgPriorityMenu = new ToolStripMenuItem("Background Priority");
        foreach (var priority in new[] { "Normal", "BelowNormal", "Idle" })
        {
            var p = priority;
            var item = new ToolStripMenuItem(p)
            {
                Checked = _config.Affinity.BackgroundPriority.Equals(p, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (sender, _) =>
            {
                // Update checkmarks
                foreach (ToolStripMenuItem sibling in bgPriorityMenu.DropDownItems)
                    sibling.Checked = false;
                ((ToolStripMenuItem)sender!).Checked = true;

                _config.Affinity.BackgroundPriority = p;
                ConfigManager.Save(_config);
                _affinityManager.ForceApplyAffinityRules(_processManager.Clients, _processManager.GetActiveClient());
                ShowBalloon($"Background priority: {p}");
            };
            bgPriorityMenu.DropDownItems.Add(item);
        }
        affinityMenu.DropDownItems.Add(bgPriorityMenu);

        affinityMenu.DropDownItems.Add(new ToolStripSeparator());

        // Info labels showing current masks (edit in Settings → Affinity tab)
        var activeMaskLabel = new ToolStripMenuItem($"Active Cores: 0x{_config.Affinity.ActiveMask:X}") { Enabled = false };
        var bgMaskLabel = new ToolStripMenuItem($"Background Cores: 0x{_config.Affinity.BackgroundMask:X}") { Enabled = false };
        affinityMenu.DropDownItems.Add(activeMaskLabel);
        affinityMenu.DropDownItems.Add(bgMaskLabel);

        affinityMenu.DropDownItems.Add(new ToolStripSeparator());
        affinityMenu.DropDownItems.Add("Force Re-Apply", null, (_, _) =>
        {
            _affinityManager.ForceApplyAffinityRules(_processManager.Clients, _processManager.GetActiveClient());
            ShowBalloon("Affinity rules re-applied to all clients");
        });
        _contextMenu.Items.Add(affinityMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // Settings submenu
        var settingsMenu = new ToolStripMenuItem("\u2699  Settings");
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
        var launcherMenu = new ToolStripMenuItem("\uD83D\uDCC2  Launcher");
        var linksMenu = new ToolStripMenuItem("\uD83C\uDF10  Links");
        linksMenu.DropDownItems.Add("\uD83D\uDDE1  Shards Wiki", null, (_, _) => FileOperations.OpenUrl("https://wiki.shardsofdalaya.com/wiki/Main_Page"));
        linksMenu.DropDownItems.Add("\uD83D\uDCD6  Dalaya Wiki", null, (_, _) => FileOperations.OpenUrl("https://wiki.dalaya.org/"));
        linksMenu.DropDownItems.Add("\uD83C\uDFC6  Fomelo Dalaya", null, (_, _) => FileOperations.OpenUrl("https://dalaya.org/fomelo/"));
        launcherMenu.DropDownItems.Add(linksMenu);
        launcherMenu.DropDownItems.Add(new ToolStripSeparator());
        launcherMenu.DropDownItems.Add("\uD83D\uDCDC  Open Log File...", null, (_, _) => FileOperations.OpenLogFile(_config, ShowBalloon));
        launcherMenu.DropDownItems.Add("\uD83D\uDCC4  Open eqclient.ini", null, (_, _) => FileOperations.OpenEqClientIni(_config, ShowBalloon));
        launcherMenu.DropDownItems.Add(new ToolStripSeparator());
        launcherMenu.DropDownItems.Add("\uD83C\uDFAF  Open GINA", null, (_, _) => FileOperations.OpenGina(_config, ShowBalloon));
        launcherMenu.DropDownItems.Add("\uD83D\uDCDD  Open Notes", null, (_, _) => FileOperations.OpenNotes(_config, ShowBalloon));
        _contextMenu.Items.Add(launcherMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("\u2716  Exit", null, (_, _) => Shutdown());

        _trayIcon!.ContextMenuStrip = _contextMenu;
    }

    private void UpdateClientMenu()
    {
        if (_clientsMenu == null) return;

        _clientsMenu.DropDownItems.Clear();

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

    private void ShowBalloon(string message)
    {
        // Defer to next message loop iteration so context menu handlers
        // fully complete before we create the tooltip window.
        // Short timer avoids disposed-object crashes from synchronous Show.
        var t = new System.Windows.Forms.Timer { Interval = 50 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            FloatingTooltip.Show(message, _config.TooltipDurationMs);
        };
        t.Start();
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
        AddClickLine(lines, "Left triple", tc.TripleClick);
        AddClickLine(lines, "Middle single", tc.MiddleClick);
        AddClickLine(lines, "Middle double", tc.MiddleDoubleClick);
        AddClickLine(lines, "Middle triple", tc.MiddleTripleClick);

        // Status
        lines.Add("");
        lines.Add($"📊  {_processManager.ClientCount} client(s) detected");
        if (_config.Affinity.Enabled)
            lines.Add("⚙  CPU affinity: ON");
        if (_config.Throttle.Enabled)
            lines.Add($"⚡  Throttle: {_config.Throttle.ThrottlePercent}%");

        var t = new System.Windows.Forms.Timer { Interval = 50 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            FloatingTooltip.Show(string.Join("\n", lines), _config.TooltipDurationMs * 2);
        };
        t.Start();
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

    private SettingsForm? _settingsForm;

    private void ShowSettings()
    {
        // Prevent multiple settings windows
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.BringToFront();
            return;
        }

        _settingsForm = new SettingsForm(_config, ReloadConfig);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
    }

    /// <summary>
    /// Handle tray icon clicks:
    /// Double-click → launch one client
    /// Middle-click → toggle PiP
    /// </summary>
    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Middle)
        {
            _trayMiddleClickCount++;

            _middleClickResolveTimer?.Stop();
            _middleClickResolveTimer?.Dispose();
            _middleClickResolveTimer = new System.Windows.Forms.Timer { Interval = ClickResolveDelayMs };
            _middleClickResolveTimer.Tick += (_, _) =>
            {
                _middleClickResolveTimer!.Stop();
                _middleClickResolveTimer.Dispose();
                _middleClickResolveTimer = null;

                int clicks = _trayMiddleClickCount;
                _trayMiddleClickCount = 0;

                string action = clicks switch
                {
                    1 => _config.TrayClick.MiddleClick,
                    2 => _config.TrayClick.MiddleDoubleClick,
                    _ => _config.TrayClick.MiddleTripleClick
                };
                ExecuteTrayAction(action);
            };
            _middleClickResolveTimer.Start();
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        _trayClickCount++;

        // Reset/restart the resolve timer on each click
        _clickResolveTimer?.Stop();
        _clickResolveTimer?.Dispose();
        _clickResolveTimer = new System.Windows.Forms.Timer { Interval = ClickResolveDelayMs };
        _clickResolveTimer.Tick += (_, _) =>
        {
            _clickResolveTimer!.Stop();
            _clickResolveTimer.Dispose();
            _clickResolveTimer = null;

            int clicks = _trayClickCount;
            _trayClickCount = 0;

            string action = clicks switch
            {
                1 => _config.TrayClick.SingleClick,
                2 => _config.TrayClick.DoubleClick,
                _ => _config.TrayClick.TripleClick // 3+
            };
            ExecuteTrayAction(action);
        };
        _clickResolveTimer.Start();
    }

    private void ExecuteTrayAction(string action)
    {
        switch (action)
        {
            case "FixWindows":
                OnArrangeWindows();
                break;
            case "SwapWindows":
                var clients = _processManager.Clients;
                if (clients.Count < 2) { ShowBalloon("Need 2+ windows to swap"); return; }
                _windowManager.SwapWindows(clients);
                ShowBalloon($"Swapped {clients.Count} window positions");
                break;
            case "TogglePiP":
                TogglePip();
                break;
            case "LaunchOne":
                OnLaunchOne();
                break;
            case "LaunchAll":
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
        _config.PollingIntervalMs = newConfig.PollingIntervalMs;
        _config.Layout.Columns = newConfig.Layout.Columns;
        _config.Layout.Rows = newConfig.Layout.Rows;
        _config.Layout.RemoveTitleBars = newConfig.Layout.RemoveTitleBars;
        _config.Layout.BorderlessFullscreen = newConfig.Layout.BorderlessFullscreen;
        _config.Layout.SnapToMonitor = newConfig.Layout.SnapToMonitor;
        _config.Layout.TargetMonitor = newConfig.Layout.TargetMonitor;
        _config.Layout.TopOffset = newConfig.Layout.TopOffset;
        _config.Layout.Mode = newConfig.Layout.Mode;
        _config.Affinity.Enabled = newConfig.Affinity.Enabled;
        _config.Affinity.ActiveMask = newConfig.Affinity.ActiveMask;
        _config.Affinity.BackgroundMask = newConfig.Affinity.BackgroundMask;
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
        _config.Throttle.Enabled = newConfig.Throttle.Enabled;
        _config.Throttle.ThrottlePercent = newConfig.Throttle.ThrottlePercent;
        _config.Throttle.CycleIntervalMs = newConfig.Throttle.CycleIntervalMs;
        _config.TrayClick.SingleClick = newConfig.TrayClick.SingleClick;
        _config.TrayClick.DoubleClick = newConfig.TrayClick.DoubleClick;
        _config.TrayClick.TripleClick = newConfig.TrayClick.TripleClick;
        _config.TrayClick.MiddleClick = newConfig.TrayClick.MiddleClick;
        _config.TrayClick.MiddleDoubleClick = newConfig.TrayClick.MiddleDoubleClick;
        _config.TrayClick.MiddleTripleClick = newConfig.TrayClick.MiddleTripleClick;
        _config.GinaPath = newConfig.GinaPath;
        _config.NotesPath = newConfig.NotesPath;
        _config.Characters = newConfig.Characters;
        _config.TooltipDurationMs = newConfig.TooltipDurationMs;
        _config.CtrlHoverHelp = newConfig.CtrlHoverHelp;

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

        // Restart or stop affinity timer based on new config
        _affinityTimer?.Stop();
        _affinityTimer?.Dispose();
        _affinityTimer = null;
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _retryTimer = null;
        if (_config.Affinity.Enabled)
        {
            StartAffinityTimer();
        }

        // Restart throttle manager with new settings
        _throttleManager.Stop();
        _throttleManager.Start();

        // Update polling interval
        _processManager.UpdatePollingInterval(_config.PollingIntervalMs);

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
            () => _affinityManager.ForceApplyAffinityRules(_processManager.Clients, _processManager.GetActiveClient())
        );
        _processManagerForm.FormClosed += (_, _) => _processManagerForm = null;
        _processManagerForm.Show();
    }

    private void Shutdown()
    {
        _affinityTimer?.Stop();
        _affinityTimer?.Dispose();
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _launchManager.CancelLaunch();
        _throttleManager.Stop();
        _pipOverlay?.Dispose();
        _pipOverlay = null;
        _boldMenuFont?.Dispose();
        _boldMenuFont = null;
        _affinityManager.ResetAllAffinities(_processManager.Clients);
        _hotkeyManager.Dispose();
        _keyboardHook.Dispose();
        _processManager.StopPolling();
        _contextMenu?.Dispose();
        _trayIcon!.Visible = false;
        _trayIcon.Dispose();
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
        _affinityTimer?.Stop();
        _affinityTimer?.Dispose();
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _throttleManager.Dispose();
        _launchManager.Dispose();
        _clickResolveTimer?.Stop();
        _clickResolveTimer?.Dispose();
        _middleClickResolveTimer?.Stop();
        _middleClickResolveTimer?.Dispose();
        _boldMenuFont?.Dispose();
        _pipOverlay?.Dispose();
        _hotkeyManager.Dispose();
        _keyboardHook.Dispose();
        _trayIcon?.Dispose();
        _contextMenu?.Dispose();
        _processManager.Dispose();
    }
}

/// <summary>
/// Dark-themed renderer for ContextMenuStrip.
/// Uses the same medieval purple palette as DarkTheme (Settings/Help forms).
/// All GDI objects cached as static fields — zero allocations per render call.
/// </summary>
internal class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    // Unified palette from DarkTheme — purple-tinted grays instead of neutral grays
    private static readonly Color MenuBg = DarkTheme.BgDark;              // RGB(32, 28, 42)
    private static readonly Color MenuBorder = DarkTheme.Border;           // RGB(64, 56, 78)
    private static readonly Color ItemHover = DarkTheme.BgHover;           // RGB(64, 56, 78)
    private static readonly Color ItemText = DarkTheme.FgWhite;            // RGB(235, 232, 240)
    private static readonly Color DisabledText = DarkTheme.FgDimGray;      // RGB(120, 112, 135)
    private static readonly Color SepColor = DarkTheme.Border;             // RGB(64, 56, 78)
    private static readonly Color CheckBg = DarkTheme.AccentGreen;         // RGB(0, 140, 80)
    private static readonly Color MarginBg = DarkTheme.BgPanel;            // RGB(38, 33, 48)
    private static readonly Color PressedBg = DarkTheme.BgMedium;          // RGB(44, 38, 56)

    // Cached GDI objects — eliminates Brush/Pen allocation on every render
    private static readonly SolidBrush BrushMenuBg = new(MenuBg);
    private static readonly SolidBrush BrushHover = new(ItemHover);
    private static readonly SolidBrush BrushMargin = new(MarginBg);
    private static readonly SolidBrush BrushCheck = new(CheckBg);
    private static readonly Pen PenBorder = new(MenuBorder);
    private static readonly Pen PenSep = new(SepColor);
    private static readonly Pen PenCheck = new(Color.White, 2);

    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var rect = new Rectangle(Point.Empty, e.Item.Size);

        if (e.Item.Selected && e.Item.Enabled)
        {
            e.Graphics.FillRectangle(BrushHover, rect);
            e.Graphics.DrawRectangle(PenBorder, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }
        else
        {
            e.Graphics.FillRectangle(BrushMenuBg, rect);
        }

        e.Item.ForeColor = e.Item.Enabled ? ItemText : DisabledText;
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(PenSep, 28, y, e.Item.Width - 4, y);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.FillRectangle(BrushMenuBg, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(PenBorder, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        e.Graphics.FillRectangle(BrushMargin, e.AffectedBounds);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var rect = e.ImageRectangle;
        rect.Inflate(2, 2);
        e.Graphics.FillRectangle(BrushCheck, rect);
        int x = rect.X + 3;
        int y = rect.Y + rect.Height / 2;
        e.Graphics.DrawLines(PenCheck, new[] {
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
