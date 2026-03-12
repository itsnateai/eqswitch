using System.Diagnostics;
using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Live process manager showing PID, character, priority, and affinity for all EQ clients.
/// </summary>
public class ProcessManagerForm : Form
{
    private static readonly Color BgDark = Color.FromArgb(30, 30, 30);
    private static readonly Color BgMedium = Color.FromArgb(45, 45, 45);
    private static readonly Color FgWhite = Color.White;
    private static readonly Color AccentGreen = Color.FromArgb(0, 120, 80);

    private readonly Func<IReadOnlyList<EQClient>> _getClients;
    private readonly Func<EQClient?> _getActiveClient;
    private readonly Action _forceApply;

    private ListView _listView = null!;
    private Label _systemInfoLabel = null!;
    private System.Windows.Forms.Timer _refreshTimer = null!;

    public ProcessManagerForm(
        Func<IReadOnlyList<EQClient>> getClients,
        Func<EQClient?> getActiveClient,
        Action forceApply)
    {
        _getClients = getClients;
        _getActiveClient = getActiveClient;
        _forceApply = forceApply;
        InitializeForm();
    }

    private void InitializeForm()
    {
        Text = "EQSwitch — Process Manager";
        Size = new Size(560, 360);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgDark;
        ForeColor = FgWhite;
        Font = new Font("Segoe UI", 9);

        // System info header
        var (coreCount, systemMask) = AffinityManager.DetectCores();
        _systemInfoLabel = new Label
        {
            Text = $"System: {coreCount} cores — Mask: 0x{systemMask:X}",
            Location = new Point(15, 10),
            AutoSize = true,
            ForeColor = Color.FromArgb(180, 180, 180),
            Font = new Font("Segoe UI", 8.5f)
        };
        Controls.Add(_systemInfoLabel);

        // ListView
        _listView = new ListView
        {
            Location = new Point(15, 35),
            Size = new Size(520, 230),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = FgWhite,
            BorderStyle = BorderStyle.FixedSingle,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };

        _listView.Columns.Add("Slot", 50);
        _listView.Columns.Add("PID", 70);
        _listView.Columns.Add("Character", 150);
        _listView.Columns.Add("Priority", 100);
        _listView.Columns.Add("Affinity", 130);

        Controls.Add(_listView);

        // Button panel
        var btnRefresh = new Button
        {
            Text = "Refresh",
            Location = new Point(15, 280),
            Size = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = BgMedium,
            ForeColor = FgWhite
        };
        btnRefresh.Click += (_, _) => RefreshList();

        var btnForceApply = new Button
        {
            Text = "Force Apply",
            Location = new Point(115, 280),
            Size = new Size(100, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = AccentGreen,
            ForeColor = FgWhite
        };
        btnForceApply.Click += (_, _) =>
        {
            _forceApply();
            RefreshList();
        };

        var btnClose = new Button
        {
            Text = "Close",
            Location = new Point(445, 280),
            Size = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = BgMedium,
            ForeColor = FgWhite
        };
        btnClose.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { btnRefresh, btnForceApply, btnClose });

        // Auto-refresh timer (1 second)
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshList();
        _refreshTimer.Start();

        FormClosed += (_, _) => _refreshTimer.Stop();

        RefreshList();
    }

    private void RefreshList()
    {
        var clients = _getClients();
        var active = _getActiveClient();

        _listView.BeginUpdate();
        _listView.Items.Clear();

        foreach (var client in clients)
        {
            var (procMask, _) = AffinityManager.GetProcessAffinity(client.ProcessId);
            var priority = AffinityManager.GetProcessPriorityName(client.ProcessId);
            var name = client.CharacterName ?? client.WindowTitle;
            if (string.IsNullOrEmpty(name)) name = $"Client {client.SlotIndex + 1}";

            var item = new ListViewItem((client.SlotIndex + 1).ToString());
            item.SubItems.Add(client.ProcessId.ToString());
            item.SubItems.Add(name);
            item.SubItems.Add(priority);
            item.SubItems.Add($"0x{procMask:X}");

            // Highlight active client
            if (client == active)
            {
                item.BackColor = Color.FromArgb(0, 60, 40);
            }

            _listView.Items.Add(item);
        }

        _listView.EndUpdate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
