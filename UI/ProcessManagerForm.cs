using System.Diagnostics;
using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Live process manager showing PID, character, priority, and affinity for all EQ clients.
/// Auto-refreshes every second. Highlights active (foreground) client.
/// </summary>
public class ProcessManagerForm : Form
{
    private const int RefreshIntervalMs = 1000;

    // Unified with DarkTheme palette
    private static readonly Color BgDark = DarkTheme.BgDark;
    private static readonly Color BgMedium = DarkTheme.BgMedium;
    private static readonly Color BgRow = DarkTheme.BgDark;
    private static readonly Color BgRowAlt = DarkTheme.BgPanel;
    private static readonly Color BgHeader = DarkTheme.BgInput;
    private static readonly Color BgActive = DarkTheme.AccentGreen;
    private static readonly Color FgWhite = DarkTheme.FgWhite;
    private static readonly Color FgGray = DarkTheme.FgGray;
    private static readonly Color FgCyan = DarkTheme.CardCyan;
    private static readonly Color AccentGreen = DarkTheme.AccentGreen;
    private static readonly Color GridLine = DarkTheme.Border;

    private readonly Func<IReadOnlyList<EQClient>> _getClients;
    private readonly Func<EQClient?> _getActiveClient;
    private readonly Action _forceApply;

    private DataGridView _grid = null!;
    private Label _systemInfoLabel = null!;
    private Label _statusLabel = null!;
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
        Size = new Size(540, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgDark;
        ForeColor = FgWhite;
        Font = new Font("Consolas", 9);

        // System info header
        var (coreCount, systemMask) = AffinityManager.DetectCores();
        _systemInfoLabel = new Label
        {
            Text = $"[ {coreCount} cores | mask 0x{systemMask:X} ]",
            Location = new Point(15, 10),
            AutoSize = true,
            ForeColor = FgCyan,
            Font = new Font("Consolas", 9)
        };
        Controls.Add(_systemInfoLabel);

        // DataGridView — proper dark mode styling
        _grid = new DataGridView
        {
            Location = new Point(15, 35),
            Size = new Size(500, 240),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = GridLine,
            BackgroundColor = BgDark,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = BgRow,
                ForeColor = FgWhite,
                SelectionBackColor = Color.FromArgb(40, 50, 80),
                SelectionForeColor = FgWhite,
                Font = new Font("Consolas", 9),
                Padding = new Padding(4, 2, 4, 2)
            },
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = BgRowAlt,
                ForeColor = FgWhite,
                SelectionBackColor = Color.FromArgb(40, 50, 80),
                SelectionForeColor = FgWhite
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = BgHeader,
                ForeColor = FgCyan,
                Font = new Font("Consolas", 9, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0)
            },
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 28,
            RowTemplate = { Height = 26 },
            EnableHeadersVisualStyles = false,
            ScrollBars = ScrollBars.Vertical
        };

        _grid.Columns.Add("Slot", "Slot");
        _grid.Columns.Add("PID", "PID");
        _grid.Columns.Add("Character", "Character");
        _grid.Columns.Add("Priority", "Priority");
        _grid.Columns.Add("Affinity", "Affinity");

        _grid.Columns["Slot"]!.Width = 45;
        _grid.Columns["PID"]!.Width = 65;
        _grid.Columns["Character"]!.Width = 160;
        _grid.Columns["Priority"]!.Width = 100;
        _grid.Columns["Affinity"]!.Width = 115;

        foreach (DataGridViewColumn col in _grid.Columns)
        {
            col.SortMode = DataGridViewColumnSortMode.NotSortable;
            col.Resizable = DataGridViewTriState.False;
        }

        Controls.Add(_grid);

        // Status label
        _statusLabel = new Label
        {
            Text = "> no clients detected",
            Location = new Point(15, 282),
            AutoSize = true,
            ForeColor = FgGray,
            Font = new Font("Consolas", 8.5f)
        };
        Controls.Add(_statusLabel);

        // Buttons
        var btnRefresh = CreateButton("Refresh", 15, 305, BgMedium);
        btnRefresh.Click += (_, _) => RefreshList();

        var btnForceApply = CreateButton("Force Apply", 115, 305, AccentGreen);
        btnForceApply.Size = new Size(100, 30);
        btnForceApply.Click += (_, _) =>
        {
            _forceApply();
            RefreshList();
        };

        var btnClose = CreateButton("Close", 425, 305, BgMedium);
        btnClose.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { btnRefresh, btnForceApply, btnClose });

        _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMs };
        _refreshTimer.Tick += (_, _) => RefreshList();
        _refreshTimer.Start();

        FormClosed += (_, _) => _refreshTimer.Stop();

        RefreshList();
    }

    private void RefreshList()
    {
        var clients = _getClients();
        var active = _getActiveClient();

        _grid.Rows.Clear();

        foreach (var client in clients)
        {
            var (procMask, _) = AffinityManager.GetProcessAffinity(client.ProcessId);
            var priority = AffinityManager.GetProcessPriorityName(client.ProcessId);
            var name = client.CharacterName ?? client.WindowTitle;
            if (string.IsNullOrEmpty(name)) name = $"Client {client.SlotIndex + 1}";

            int rowIdx = _grid.Rows.Add(
                (client.SlotIndex + 1).ToString(),
                client.ProcessId.ToString(),
                name,
                priority,
                $"0x{procMask:X}");

            // Highlight active client row
            if (client == active)
            {
                var row = _grid.Rows[rowIdx];
                foreach (DataGridViewCell cell in row.Cells)
                {
                    cell.Style.BackColor = BgActive;
                    cell.Style.SelectionBackColor = BgActive;
                }
            }
        }

        // Update status
        int count = clients.Count;
        _statusLabel.Text = count switch
        {
            0 => "> no clients detected",
            1 => "> 1 client running",
            _ => $"> {count} clients running"
        };

        // Clear selection — looks cleaner
        _grid.ClearSelection();
    }

    private static Button CreateButton(string text, int x, int y, Color bgColor)
    {
        return new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = bgColor,
            ForeColor = FgWhite,
            Cursor = Cursors.Hand
        };
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
