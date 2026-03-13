using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

public class TrayManager : IDisposable
{
    // ─── Constants ───────────────────────────────────────────────────
    private const int MultiMonToggleDebounceMs = 500;
    private const int AffinityPollIntervalMs = 250;
    private const int TripleClickWindowMs = 500;

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

    // Triple-click detection
    private int _trayClickCount;
    private long _trayFirstClickTick;

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

        // Tray click events
        _trayIcon.MouseClick += OnTrayMouseClick;
        _trayIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OnLaunchOne();
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
        StartupManager.ValidateRegistryPath(_config);

        // Log core detection at startup
        var (cores, sysMask) = AffinityManager.DetectCores();
        FileLogger.Info($"Startup: {cores} cores detected, system mask 0x{sysMask:X}");

        ShowBalloon("EQSwitch started. Watching for EQ clients...");
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
    /// Switch Key ('\'):  Cycle to the next EQ client.
    /// Only fires when an EQ window is already focused (enforced by KeyboardHookManager filter).
    /// </summary>
    private void OnSwitchKey()
    {
        var current = _processManager.GetActiveClient();
        var clients = _processManager.Clients;
        if (clients.Count < 2)
        {
            FileLogger.Info("SwitchKey: fewer than 2 clients, nothing to cycle");
            return;
        }

        var next = _windowManager.CycleNext(clients, current);
        if (next != null)
            FileLogger.Info($"SwitchKey: cycled to {next}");
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
            var active = _processManager.GetActiveClient();
            var clients = _processManager.Clients;
            _affinityManager.ApplyAffinityRules(clients, active);
            _throttleManager.UpdateClients(clients, active);

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

        var hk = _config.Hotkeys;
        string HkSuffix(string key) => string.IsNullOrEmpty(key) ? "" : $"\t{key}";

        _contextMenu.Items.Add($"Fix Windows{HkSuffix(hk.ArrangeWindows)}", null, (_, _) => OnArrangeWindows());
        _contextMenu.Items.Add("Swap Windows", null, (_, _) =>
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
        _contextMenu.Items.Add(new ToolStripSeparator());

        var launchOneItem = new ToolStripMenuItem($"Launch Client{HkSuffix(hk.LaunchOne)}") { Font = new Font(_contextMenu.Font, FontStyle.Bold) };
        launchOneItem.Click += (_, _) => OnLaunchOne();
        _contextMenu.Items.Add(launchOneItem);

        var launchAllItem = new ToolStripMenuItem($"Launch All ({_config.Launch.NumClients}){HkSuffix(hk.LaunchAll)}") { Font = new Font(_contextMenu.Font, FontStyle.Bold) };
        launchAllItem.Click += (_, _) => OnLaunchAll();
        _contextMenu.Items.Add(launchAllItem);

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Toggle PiP", null, (_, _) => TogglePip());
        _contextMenu.Items.Add("Refresh Clients", null, (_, _) =>
        {
            _processManager.RefreshClients();
            ShowBalloon($"Found {_processManager.ClientCount} EQ client(s)");
        });
        _contextMenu.Items.Add(new ToolStripSeparator());

        _clientsMenu = new ToolStripMenuItem("Clients");
        _clientsMenu.DropDownItems.Add("(scanning...)");
        _contextMenu.Items.Add(_clientsMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Process Manager", null, (_, _) => ShowProcessManager());
        _contextMenu.Items.Add("Force Apply Affinity", null, (_, _) =>
        {
            _affinityManager.ForceApplyAffinityRules(_processManager.Clients, _processManager.GetActiveClient());
            ShowBalloon("Affinity rules re-applied to all clients");
        });
        _contextMenu.Items.Add(new ToolStripSeparator());

        // Files submenu
        var filesMenu = new ToolStripMenuItem("Files");
        filesMenu.DropDownItems.Add("Open Log File...", null, (_, _) => FileOperations.OpenLogFile(_config, ShowBalloon));
        filesMenu.DropDownItems.Add("Open eqclient.ini", null, (_, _) => FileOperations.OpenEqClientIni(_config, ShowBalloon));
        filesMenu.DropDownItems.Add(new ToolStripSeparator());
        filesMenu.DropDownItems.Add("Open GINA", null, (_, _) => FileOperations.OpenGina(_config, ShowBalloon));
        filesMenu.DropDownItems.Add("Open Notes", null, (_, _) => FileOperations.OpenNotes(_config, ShowBalloon));
        _contextMenu.Items.Add(filesMenu);

        _contextMenu.Items.Add("Settings", null, (_, _) => ShowSettings());
        _contextMenu.Items.Add("Video Settings", null, (_, _) =>
        {
            using var form = new VideoSettingsForm(_config);
            form.ShowDialog();
        });
        _contextMenu.Items.Add(new ToolStripSeparator());

        // Run at startup toggle
        var startupItem = new ToolStripMenuItem("Run at Startup")
        {
            Checked = _config.RunAtStartup,
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) =>
        {
            _config.RunAtStartup = startupItem.Checked;
            StartupManager.SetRunAtStartup(startupItem.Checked);
            ConfigManager.Save(_config);
        };
        _contextMenu.Items.Add(startupItem);
        _contextMenu.Items.Add("Create Desktop Shortcut", null, (_, _) => StartupManager.CreateDesktopShortcut(ShowBalloon));

        // Links submenu
        var linksMenu = new ToolStripMenuItem("Links");
        linksMenu.DropDownItems.Add("Dalaya Wiki", null, (_, _) => FileOperations.OpenUrl("http://wiki.shardsofdalaya.com"));
        linksMenu.DropDownItems.Add("Shards Wiki", null, (_, _) => FileOperations.OpenUrl("https://shards.wiki"));
        linksMenu.DropDownItems.Add("Fomelo", null, (_, _) => FileOperations.OpenUrl("http://fomelo.shardsofdalaya.com"));
        _contextMenu.Items.Add(linksMenu);

        _contextMenu.Items.Add("Help", null, (_, _) => HelpForm.Show(_config));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Exit", null, (_, _) => Shutdown());

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
            return;
        }

        foreach (var client in clients)
        {
            var c = client; // capture for closure
            var item = new ToolStripMenuItem($"[{client.SlotIndex + 1}] {client}");
            item.Click += (_, _) => _windowManager.SwitchToClient(c);
            _clientsMenu.DropDownItems.Add(item);
        }
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
        if (_trayIcon == null) return;
        _trayIcon.BalloonTipTitle = "EQSwitch";
        _trayIcon.BalloonTipText = message;
        _trayIcon.ShowBalloonTip(2000);
    }

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
            TogglePip();
        }
        else if (e.Button == MouseButtons.Left)
        {
            long now = Environment.TickCount64;
            if (now - _trayFirstClickTick > TripleClickWindowMs)
            {
                _trayClickCount = 1;
            }
            else
            {
                _trayClickCount++;
            }
            _trayFirstClickTick = now;

            if (_trayClickCount >= 3)
            {
                _trayClickCount = 0;
                OnArrangeWindows();
            }
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
        _config.GinaPath = newConfig.GinaPath;
        _config.NotesPath = newConfig.NotesPath;
        _config.Characters = newConfig.Characters;

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

        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eqswitch.ico");
        Icon newIcon;
        if (File.Exists(iconPath))
        {
            try { newIcon = new Icon(iconPath); }
            catch { newIcon = SystemIcons.Application; }
        }
        else
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
        _pipOverlay?.Dispose();
        _hotkeyManager.Dispose();
        _keyboardHook.Dispose();
        _trayIcon?.Dispose();
        _contextMenu?.Dispose();
        _processManager.Dispose();
    }
}
