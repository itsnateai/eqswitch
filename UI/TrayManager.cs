using System.Diagnostics;
using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

public class TrayManager : IDisposable
{
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
        ValidateStartupRegistryPath();

        // Log core detection at startup
        var (cores, sysMask) = AffinityManager.DetectCores();
        Debug.WriteLine($"Startup: {cores} cores detected, system mask 0x{sysMask:X}");

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

        Debug.WriteLine($"RegisterHotKey: {registered} registered, {failed} failed");

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
                    Debug.WriteLine($"Hook: SwitchKey '{hk.SwitchKey}' (VK 0x{vk:X2}) — EQ-only");
                }
            }

            // Global Switch Key (default ']') — works from any app, but only when EQ clients exist
            if (!string.IsNullOrEmpty(hk.GlobalSwitchKey))
            {
                uint vk = HotkeyManager.ResolveVK(hk.GlobalSwitchKey);
                if (vk != 0)
                {
                    _keyboardHook.Register(vk, OnGlobalSwitchKey, requireClients: true);
                    Debug.WriteLine($"Hook: GlobalSwitchKey '{hk.GlobalSwitchKey}' (VK 0x{vk:X2}) — global (requires clients)");
                }
            }
        }
        else
        {
            Debug.WriteLine("WARNING: Keyboard hook install failed — SwitchKey and GlobalSwitchKey disabled");
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
            Debug.WriteLine($"Direct switch to slot {slot + 1}: {client}");
        }
        else
        {
            Debug.WriteLine($"Direct switch: no client in slot {slot + 1}");
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
            Debug.WriteLine("SwitchKey: fewer than 2 clients, nothing to cycle");
            return;
        }

        var next = _windowManager.CycleNext(clients, current);
        if (next != null)
            Debug.WriteLine($"SwitchKey: cycled to {next}");
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
            Debug.WriteLine("GlobalSwitchKey: no clients detected");
            return;
        }

        if (current != null)
        {
            // EQ is focused — cycle to next
            var next = _windowManager.CycleNext(clients, current);
            if (next != null)
                Debug.WriteLine($"GlobalSwitchKey: cycled to {next}");
        }
        else
        {
            // EQ is NOT focused — bring first client to front
            var first = clients[0];
            _windowManager.SwitchToClient(first);
            Debug.WriteLine($"GlobalSwitchKey: focused {first}");
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
            Debug.WriteLine("ArrangeWindows: no clients to arrange");
            return;
        }

        _windowManager.ArrangeWindows(clients);
        Debug.WriteLine($"ArrangeWindows: arranged {clients.Count} client(s)");
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
            Debug.WriteLine("ToggleMultiMonitor: disabled in config");
            return;
        }

        // 500ms debounce
        long now = Environment.TickCount64;
        if (now - _lastMultiMonToggle < 500)
            return;
        _lastMultiMonToggle = now;

        // Toggle the mode
        bool isMulti = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        _config.Layout.Mode = isMulti ? "single" : "multimonitor";

        string label = isMulti ? "Single Screen" : "Multi-Monitor";
        Debug.WriteLine($"ToggleMultiMonitor: switched to {label}");
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

        // 250ms timer to check foreground window and apply affinity rules
        _affinityTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _affinityTimer.Tick += (_, _) =>
        {
            var active = _processManager.GetActiveClient();
            var clients = _processManager.Clients;
            _affinityManager.ApplyAffinityRules(clients, active);

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

        Debug.WriteLine("Affinity timers started (250ms check, retry every " +
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
        filesMenu.DropDownItems.Add("Open Log File...", null, (_, _) => OpenLogFile());
        filesMenu.DropDownItems.Add("Open eqclient.ini", null, (_, _) => OpenEqClientIni());
        filesMenu.DropDownItems.Add(new ToolStripSeparator());
        filesMenu.DropDownItems.Add("Open GINA", null, (_, _) => OpenGina());
        filesMenu.DropDownItems.Add("Open Notes", null, (_, _) => OpenNotes());
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
            SetRunAtStartup(startupItem.Checked);
            ConfigManager.Save(_config);
        };
        _contextMenu.Items.Add(startupItem);
        _contextMenu.Items.Add("Create Desktop Shortcut", null, (_, _) => CreateDesktopShortcut());

        // Links submenu
        var linksMenu = new ToolStripMenuItem("Links");
        linksMenu.DropDownItems.Add("Dalaya Wiki", null, (_, _) => OpenUrl("http://wiki.shardsofdalaya.com"));
        linksMenu.DropDownItems.Add("Shards Wiki", null, (_, _) => OpenUrl("https://shards.wiki"));
        linksMenu.DropDownItems.Add("Fomelo", null, (_, _) => OpenUrl("http://fomelo.shardsofdalaya.com"));
        _contextMenu.Items.Add(linksMenu);

        _contextMenu.Items.Add("Help", null, (_, _) => ShowHelp());
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
            // Triple-click detection: each click must be within 500ms of the previous
            long now = Environment.TickCount64;
            if (now - _trayFirstClickTick > 500)
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

    private void ShowHelp()
    {
        var helpForm = new Form
        {
            Text = "EQSwitch — Help",
            Size = new Size(520, 500),
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9),
            MaximizeBox = true,
            MinimizeBox = false
        };

        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 10),
            Text = GetHelpText()
        };

        helpForm.Controls.Add(rtb);
        helpForm.Show();
    }

    private string GetHelpText()
    {
        var hk = _config.Hotkeys;
        return $@"EQSwitch — EverQuest Window Manager
====================================

HOTKEYS:
  Switch Key ({hk.SwitchKey})     — Cycle to next EQ client (EQ must be focused)
  Global Switch ({hk.GlobalSwitchKey})  — Cycle next / bring EQ to front from any app
  {hk.ArrangeWindows}            — Arrange all windows in grid layout
  {hk.ToggleMultiMonitor}            — Toggle single-screen / multi-monitor mode
  Alt+1..6          — Switch directly to client by slot number
  {(string.IsNullOrEmpty(hk.LaunchOne) ? "(not set)" : hk.LaunchOne)}           — Launch one EQ client
  {(string.IsNullOrEmpty(hk.LaunchAll) ? "(not set)" : hk.LaunchAll)}           — Launch all configured clients

TRAY ICON:
  Right-click       — Context menu
  Double-click      — Launch one EQ client
  Middle-click      — Toggle PiP overlay

LAYOUT MODES:
  Single Screen     — Grid layout (Columns × Rows) on target monitor
  Multi-Monitor     — One window per physical monitor

PIP (PICTURE-IN-PICTURE):
  Live preview of background EQ windows
  Ctrl+Drag to reposition
  Auto-hides when fewer than 2 clients

CPU AFFINITY:
  Active client  → P-cores (high priority)
  Background     → E-cores (normal priority)
  Auto-applies on window switch (250ms check)

CONFIG:
  eqswitch-config.json (alongside exe)
  Auto-backup on save (keeps last 10)
";
    }

    // ─── File Operations ──────────────────────────────────────────

    /// <summary>
    /// Open an EQ log file. Shows a picker if multiple characters exist,
    /// otherwise opens the Logs folder in the EQ directory.
    /// </summary>
    private void OpenLogFile()
    {
        var logsDir = Path.Combine(_config.EQPath, "Logs");
        if (!Directory.Exists(logsDir))
        {
            ShowBalloon("Logs folder not found");
            return;
        }

        // Find log files matching eqlog_*_*.txt pattern
        var logFiles = Directory.GetFiles(logsDir, "eqlog_*.txt")
            .OrderByDescending(File.GetLastWriteTime)
            .ToArray();

        if (logFiles.Length == 0)
        {
            // Just open the folder
            Process.Start("explorer.exe", logsDir);
            return;
        }

        if (logFiles.Length == 1)
        {
            OpenInNotepad(logFiles[0]);
            return;
        }

        // Multiple logs — show picker with most recent files
        var menu = new ContextMenuStrip();
        foreach (var logFile in logFiles.Take(10))
        {
            var name = Path.GetFileNameWithoutExtension(logFile);
            var lastWrite = File.GetLastWriteTime(logFile);
            var path = logFile; // capture
            menu.Items.Add($"{name} ({lastWrite:g})", null, (_, _) => OpenInNotepad(path));
        }
        if (logFiles.Length > 10)
            menu.Items.Add($"({logFiles.Length - 10} more — open folder)", null, (_, _) =>
                Process.Start("explorer.exe", logsDir));

        menu.Show(Cursor.Position);
    }

    /// <summary>
    /// Open eqclient.ini in the default text editor.
    /// </summary>
    private void OpenEqClientIni()
    {
        var iniPath = Path.Combine(_config.EQPath, "eqclient.ini");
        if (!File.Exists(iniPath))
        {
            ShowBalloon("eqclient.ini not found");
            return;
        }
        OpenInNotepad(iniPath);
    }

    /// <summary>
    /// Launch GINA from the configured path.
    /// </summary>
    private void OpenGina()
    {
        if (string.IsNullOrEmpty(_config.GinaPath) || !File.Exists(_config.GinaPath))
        {
            ShowBalloon("GINA path not configured or file not found.\nSet it in Settings.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _config.GinaPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenGina failed: {ex.Message}");
            ShowBalloon($"Failed to launch GINA: {ex.Message}");
        }
    }

    /// <summary>
    /// Open the notes file in the default text editor.
    /// Creates a default notes.txt if no path configured.
    /// </summary>
    private void OpenNotes()
    {
        var notesPath = _config.NotesPath;

        if (string.IsNullOrEmpty(notesPath))
        {
            // Default: notes.txt alongside the exe
            notesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eqswitch-notes.txt");
            _config.NotesPath = notesPath;
            ConfigManager.Save(_config);
        }

        if (!File.Exists(notesPath))
        {
            try
            {
                File.WriteAllText(notesPath, "# EQSwitch Notes\n\n");
                Debug.WriteLine($"Created notes file: {notesPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OpenNotes: failed to create {notesPath} — {ex.Message}");
                ShowBalloon($"Failed to create notes file: {ex.Message}");
                return;
            }
        }

        OpenInNotepad(notesPath);
    }

    /// <summary>
    /// If run-at-startup is enabled, ensure the registry path matches the current exe location.
    /// </summary>
    private void ValidateStartupRegistryPath()
    {
        if (!_config.RunAtStartup) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: false);
            var registeredPath = key?.GetValue("EQSwitch") as string;
            var currentPath = $"\"{Application.ExecutablePath}\"";
            if (registeredPath != null && !registeredPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"Startup: registry path stale ({registeredPath}), updating to {currentPath}");
                SetRunAtStartup(true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ValidateStartupRegistryPath failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Add or remove EQSwitch from Windows startup via Registry.
    /// </summary>
    private static void SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Application.ExecutablePath;
                key.SetValue("EQSwitch", $"\"{exePath}\"");
                Debug.WriteLine($"Startup: added registry entry for {exePath}");
            }
            else
            {
                key.DeleteValue("EQSwitch", throwOnMissingValue: false);
                Debug.WriteLine("Startup: removed registry entry");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetRunAtStartup failed: {ex.Message}");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenUrl failed for {url}: {ex.Message}");
        }
    }

    private static void OpenInNotepad(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true // Opens with default text editor
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenInNotepad failed for {path}: {ex.Message}");
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

        // Update polling interval
        _processManager.UpdatePollingInterval(_config.PollingIntervalMs);

        Debug.WriteLine("Config reloaded and applied");
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

    private void CreateDesktopShortcut()
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var shortcutPath = Path.Combine(desktopPath, "EQSwitch.lnk");

            if (File.Exists(shortcutPath))
            {
                ShowBalloon("Desktop shortcut already exists");
                return;
            }

            // Use WScript.Shell COM object to create shortcut
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                ShowBalloon("Failed to create shortcut — WScript.Shell not available");
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                try
                {
                    shortcut.TargetPath = Application.ExecutablePath;
                    shortcut.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    shortcut.Description = "EQSwitch — EverQuest Window Manager";
                    shortcut.IconLocation = Application.ExecutablePath + ",0";
                    shortcut.Save();
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }

            Debug.WriteLine($"Desktop shortcut created: {shortcutPath}");
            ShowBalloon("Desktop shortcut created");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CreateDesktopShortcut failed: {ex.Message}");
            ShowBalloon($"Failed to create shortcut: {ex.Message}");
        }
    }

    private void Shutdown()
    {
        _affinityTimer?.Stop();
        _affinityTimer?.Dispose();
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _launchManager.CancelLaunch();
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
        _pipOverlay?.Dispose();
        _hotkeyManager.Dispose();
        _keyboardHook.Dispose();
        _trayIcon?.Dispose();
        _contextMenu?.Dispose();
        _processManager.Dispose();
    }
}
