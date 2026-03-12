using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// System tray icon and context menu — the main UI surface for EQSwitch.
/// Also orchestrates the interaction between ProcessManager, WindowManager,
/// AffinityManager, and HotkeyManager.
/// </summary>
public class TrayManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly ProcessManager _processManager;
    private readonly WindowManager _windowManager;
    private readonly AffinityManager _affinityManager;
    private readonly HotkeyManager _hotkeyManager;

    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _contextMenu;

    // Track the last active client for affinity switching
    private EQClient? _lastActiveClient;

    public TrayManager(
        AppConfig config,
        ProcessManager processManager,
        WindowManager windowManager,
        AffinityManager affinityManager,
        HotkeyManager hotkeyManager)
    {
        _config = config;
        _processManager = processManager;
        _windowManager = windowManager;
        _affinityManager = affinityManager;
        _hotkeyManager = hotkeyManager;
    }

    public void Initialize()
    {
        // Create tray icon
        _trayIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "EQSwitch - 0 clients",
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => ArrangeAllWindows();

        // Build context menu
        BuildContextMenu();

        // Register hotkeys
        RegisterHotkeys();

        // Wire up process events
        _processManager.ClientListChanged += OnClientListChanged;
        _processManager.ClientDiscovered += OnClientDiscovered;
        _processManager.ClientLost += OnClientLost;

        // Start polling for EQ processes
        _processManager.StartPolling();

        // Set up a timer to track foreground window changes for affinity
        var affinityTimer = new System.Windows.Forms.Timer { Interval = 250 };
        affinityTimer.Tick += (_, _) => CheckAffinitySwitch();
        affinityTimer.Start();

        ShowTooltip("EQSwitch started. Watching for EQ clients...");
    }

    private void BuildContextMenu()
    {
        _contextMenu = new ContextMenuStrip();

        _contextMenu.Items.Add("Arrange Windows", null, (_, _) => ArrangeAllWindows());
        _contextMenu.Items.Add("Refresh Clients", null, (_, _) =>
        {
            _processManager.RefreshClients();
            ShowTooltip($"Found {_processManager.ClientCount} EQ client(s)");
        });
        _contextMenu.Items.Add(new ToolStripSeparator());

        // Client list (dynamically populated)
        var clientsMenu = new ToolStripMenuItem("Clients") { Name = "clientsMenu" };
        clientsMenu.DropDownItems.Add("(scanning...)");
        _contextMenu.Items.Add(clientsMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // Affinity toggle
        var affinityItem = new ToolStripMenuItem("CPU Affinity")
        {
            Checked = _config.Affinity.Enabled,
            CheckOnClick = true
        };
        affinityItem.CheckedChanged += (_, _) =>
        {
            _config.Affinity.Enabled = affinityItem.Checked;
            if (!affinityItem.Checked)
                _affinityManager.ResetAllAffinities(_processManager.Clients);
            ConfigManager.Save(_config);
        };
        _contextMenu.Items.Add(affinityItem);

        // Character backup
        _contextMenu.Items.Add("Export Characters...", null, (_, _) => ExportCharacters());
        _contextMenu.Items.Add("Import Characters...", null, (_, _) => ImportCharacters());

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Exit", null, (_, _) => Shutdown());

        _trayIcon!.ContextMenuStrip = _contextMenu;
    }

    private void RegisterHotkeys()
    {
        var hk = _config.Hotkeys;

        // Cycle next/prev
        _hotkeyManager.Register(hk.CycleNextClient, () =>
        {
            var active = _processManager.GetActiveClient();
            _windowManager.CycleNext(_processManager.Clients, active);
        });

        _hotkeyManager.Register(hk.CyclePrevClient, () =>
        {
            var active = _processManager.GetActiveClient();
            _windowManager.CyclePrev(_processManager.Clients, active);
        });

        // Direct switch keys (Alt+1 through Alt+6)
        for (int i = 0; i < hk.DirectSwitchKeys.Count; i++)
        {
            int slot = i; // Capture for closure
            _hotkeyManager.Register(hk.DirectSwitchKeys[i], () =>
            {
                var client = _processManager.GetClientBySlot(slot);
                if (client != null)
                    _windowManager.SwitchToClient(client);
                else
                    ShowTooltip($"No client in slot {slot + 1}");
            });
        }

        // Arrange windows
        _hotkeyManager.Register(hk.ArrangeWindows, ArrangeAllWindows);
    }

    private void CheckAffinitySwitch()
    {
        var active = _processManager.GetActiveClient();
        if (active != null && active != _lastActiveClient)
        {
            _lastActiveClient = active;
            _affinityManager.ApplyAffinityRules(_processManager.Clients, active);
        }
    }

    private void ArrangeAllWindows()
    {
        var clients = _processManager.Clients;
        if (clients.Count == 0)
        {
            ShowTooltip("No EQ clients found to arrange");
            return;
        }

        _windowManager.ArrangeWindows(clients);
        ShowTooltip($"Arranged {clients.Count} window(s) in {_config.Layout.Columns}x{_config.Layout.Rows} grid");
    }

    private void OnClientListChanged(object? sender, EventArgs e)
    {
        UpdateClientMenu();
        UpdateTrayText();
    }

    private void OnClientDiscovered(object? sender, EQClient client)
    {
        ShowTooltip($"Discovered: {client}");
    }

    private void OnClientLost(object? sender, EQClient client)
    {
        ShowTooltip($"Lost: {client}");
    }

    private void UpdateClientMenu()
    {
        if (_contextMenu == null) return;

        var clientsMenu = _contextMenu.Items["clientsMenu"] as ToolStripMenuItem;
        if (clientsMenu == null) return;

        clientsMenu.DropDownItems.Clear();

        var clients = _processManager.Clients;
        if (clients.Count == 0)
        {
            clientsMenu.DropDownItems.Add("(no clients detected)");
            return;
        }

        foreach (var client in clients)
        {
            var item = new ToolStripMenuItem($"[{client.SlotIndex + 1}] {client}");
            var capturedClient = client;
            item.Click += (_, _) => _windowManager.SwitchToClient(capturedClient);
            clientsMenu.DropDownItems.Add(item);
        }
    }

    private void UpdateTrayText()
    {
        if (_trayIcon == null) return;
        int count = _processManager.ClientCount;
        _trayIcon.Text = $"EQSwitch - {count} client{(count != 1 ? "s" : "")}";
    }

    private void ShowTooltip(string message)
    {
        if (!_config.ShowTooltipErrors || _trayIcon == null) return;
        _trayIcon.BalloonTipTitle = "EQSwitch";
        _trayIcon.BalloonTipText = message;
        _trayIcon.ShowBalloonTip(2000);
    }

    private void ExportCharacters()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = "json",
            FileName = $"eqswitch-characters_{DateTime.Now:yyyyMMdd}.json"
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                ConfigManager.ExportCharacters(_config, dialog.FileName);
                ShowTooltip($"Exported {_config.Characters.Count} character(s)");
            }
            catch (Exception ex)
            {
                ShowTooltip($"Export failed: {ex.Message}");
            }
        }
    }

    private void ImportCharacters()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = "json"
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                var imported = ConfigManager.ImportCharacters(dialog.FileName);
                _config.Characters = imported;
                ConfigManager.Save(_config);
                ShowTooltip($"Imported {imported.Count} character(s)");
            }
            catch (Exception ex)
            {
                ShowTooltip($"Import failed: {ex.Message}");
            }
        }
    }

    private void Shutdown()
    {
        // Clean up affinities before exiting
        _affinityManager.ResetAllAffinities(_processManager.Clients);
        _hotkeyManager.UnregisterAll();
        _processManager.StopPolling();

        ConfigManager.Save(_config);

        _trayIcon!.Visible = false;
        Application.Exit();
    }

    private static Icon LoadIcon()
    {
        // Try to load custom icon from disk
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eqswitch.ico");
        if (File.Exists(iconPath))
        {
            try { return new Icon(iconPath); } catch { }
        }

        // Fallback: use the application's embedded icon, or system default
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _contextMenu?.Dispose();
        _processManager.Dispose();
        _hotkeyManager.Dispose();
    }
}
