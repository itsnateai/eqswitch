using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Consolidated process manager with three clear settings sections:
/// 1. Windows Priority — keeps EQ from getting starved (prevents VD crashes, enables autofollow)
/// 2. Core Assignment — spreads EQ across cores so it doesn't single-thread on core 0
/// 3. FPS Limits — MaxFPS / MaxBGFPS from eqclient.ini
/// Plus the live process grid for per-process overrides.
/// </summary>
public class ProcessManagerForm : Form
{
    private const int RefreshIntervalMs = 2000;
    private const int FormW = 600;
    private const int GridW = 560;
    private const int Pad = 15;

    private static readonly string[] Priorities = { "High", "AboveNormal", "Normal", "BelowNormal" };

    private readonly Func<IReadOnlyList<EQClient>> _getClients;
    private readonly Func<EQClient?> _getActiveClient;
    private readonly Action _forceApply;
    private readonly Action<bool> _toggleAffinity;
    private readonly AppConfig _config;

    private DataGridView _grid = null!;
    private Label _statusLabel = null!;
    private CheckBox _chkAffinityEnabled = null!;
    private ToolTip _tooltip = null!;
    private System.Windows.Forms.Timer _refreshTimer = null!;
    private bool _isRefreshing;
    private bool _forceNextRefresh;

    // Card 1: Priority preset (applies to all clients, or "None" for per-row manual)
    private ComboBox _cboPriority = null!;

    // Card 2: Core Assignment (6 slots matching eqclient.ini CPUAffinity0-5)
    private NumericUpDown[] _slotPickers = null!;

    // Card 3: FPS
    private NumericUpDown _nudMaxFPS = null!;
    private NumericUpDown _nudMaxBGFPS = null!;

    // Track selected PID across refreshes so selection survives auto-refresh
    private int _selectedPid;

    // Snapshot of config at form open — for Reset
    private readonly string _initialPriority;
    private readonly int[] _initialSlots;
    private readonly int _initialMaxFPS;
    private readonly int _initialMaxBGFPS;

    public ProcessManagerForm(
        Func<IReadOnlyList<EQClient>> getClients,
        Func<EQClient?> getActiveClient,
        Action forceApply,
        Action<bool> toggleAffinity,
        AppConfig config)
    {
        _getClients = getClients;
        _getActiveClient = getActiveClient;
        _forceApply = forceApply;
        _toggleAffinity = toggleAffinity;
        _config = config;

        // Snapshot current config for Reset
        _initialPriority = config.Affinity.ActivePriority;
        _initialSlots = (int[])config.EQClientIni.CPUAffinitySlots.Clone();
        _initialMaxFPS = config.EQClientIni.MaxFPS;
        _initialMaxBGFPS = config.EQClientIni.MaxBGFPS;

        InitializeForm();
    }

    private void InitializeForm()
    {
        DarkTheme.RepairDefaultFont();
        Text = "EQSwitch — Process Manager";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = DarkTheme.BgDark;
        ForeColor = DarkTheme.FgWhite;
        Font = new Font("Segoe UI", 9);

        _tooltip = new ToolTip { InitialDelay = 300, ReshowDelay = 200 };

        var (coreCount, systemMask) = AffinityManager.DetectCores();
        int y = 10;

        // ─── Header bar ──────────────────────────────────────────
        var headerPanel = new Panel
        {
            Location = new Point(Pad, y),
            Size = new Size(GridW, 30),
            BackColor = DarkTheme.BgPanel
        };
        headerPanel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            using var pen = new Pen(DarkTheme.Border, 1);
            g.DrawRectangle(pen, 0, 0, headerPanel.Width - 1, headerPanel.Height - 1);
            // Accent left-bar matching card visual language
            using var accentBrush = new SolidBrush(DarkTheme.CardCyan);
            g.FillRectangle(accentBrush, 0, 0, 3, headerPanel.Height);
        };

        var lblHeader = new Label
        {
            Text = $"\u2699  {coreCount} cores  |  system mask 0x{systemMask:X}",
            Location = new Point(10, 6),
            AutoSize = true,
            ForeColor = DarkTheme.CardCyan,
            Font = new Font("Consolas", 9f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        headerPanel.Controls.Add(lblHeader);

        _chkAffinityEnabled = new CheckBox
        {
            Text = "Affinity Active",
            Location = new Point(headerPanel.Width - 125, 5),
            AutoSize = true,
            ForeColor = _config.Affinity.Enabled ? DarkTheme.CardGreen : DarkTheme.FgGray,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Checked = _config.Affinity.Enabled
        };
        _chkAffinityEnabled.CheckedChanged += (_, _) =>
        {
            _toggleAffinity(_chkAffinityEnabled.Checked);
            _chkAffinityEnabled.ForeColor = _chkAffinityEnabled.Checked
                ? DarkTheme.CardGreen : DarkTheme.FgGray;
        };
        headerPanel.Controls.Add(_chkAffinityEnabled);
        Controls.Add(headerPanel);
        y += 38;

        // ─── Process grid ────────────────────────────────────────
        _grid = BuildProcessGrid(y);
        Controls.Add(_grid);
        y += 168;

        // ─── Status bar ──────────────────────────────────────────
        _statusLabel = new Label
        {
            Text = "No clients detected",
            Location = new Point(Pad + 2, y),
            AutoSize = true,
            ForeColor = DarkTheme.FgDimGray,
            Font = new Font("Consolas", 8.25f)
        };
        Controls.Add(_statusLabel);
        y += 22;

        // ─── Card 1: Windows Priority ────────────────────────────
        var cardPriority = DarkTheme.MakeCard(this, "\u26A1", "Windows Priority", DarkTheme.CardGold, Pad, y, GridW, 65);

        DarkTheme.AddCardLabel(cardPriority, "Preset:", 10, 32);
        var priorityOptions = new[] { "None", "High", "AboveNormal", "Normal", "BelowNormal" };
        _cboPriority = DarkTheme.AddCardComboBox(cardPriority, 60, 30, 120, priorityOptions);
        // Show current config value if it matches, otherwise "None"
        _cboPriority.SelectedItem = Priorities.Contains(_config.Affinity.ActivePriority)
            ? _config.Affinity.ActivePriority : "None";

        DarkTheme.AddCardHint(cardPriority, "None = set per-client in grid  |  High = prevents VD crashes + autofollow", 195, 34);

        y += 73;

        // ─── Card 2: Core Assignment ─────────────────────────────
        // 6 slots matching eqclient.ini CPUAffinity0-5
        var cardCores = DarkTheme.MakeCard(this, "\uD83E\uDDE0", "CPU Thread Mapping", DarkTheme.CardCyan, Pad, y, GridW, 125);

        DarkTheme.AddCardHint(cardCores, "EQ uses 6 internal threads — assign each to a CPU core to spread the load", 10, 28);

        var slots = _config.EQClientIni.CPUAffinitySlots;
        _slotPickers = new NumericUpDown[6];
        for (int i = 0; i < 6; i++)
        {
            int col = i % 3;
            int row = i / 3;
            int lx = 10 + col * 185;
            int ly = 48 + row * 30;

            DarkTheme.AddCardLabel(cardCores, $"Thread {i + 1}  \u2192", lx, ly + 2);
            int coreVal = i < slots.Length ? Math.Clamp(slots[i], 0, coreCount - 1) : i;
            _slotPickers[i] = DarkTheme.AddCardNumeric(cardCores, lx + 75, ly, 55, coreVal, 0, coreCount - 1);
            _tooltip.SetToolTip(_slotPickers[i], $"CPU core for EQ thread {i + 1} (CPUAffinity{i} in eqclient.ini)");
        }

        DarkTheme.AddCardHint(cardCores, $"{coreCount} cores available  |  Shared by all EQ clients  |  Core 0 = first", 10, 108);

        y += 133;

        // ─── Card 3: FPS Limits ──────────────────────────────────
        var cardFps = DarkTheme.MakeCard(this, "\uD83C\uDFAE", "FPS Limits", DarkTheme.CardGreen, Pad, y, GridW, 85);

        DarkTheme.AddCardLabel(cardFps, "Active FPS:", 10, 32);
        _nudMaxFPS = DarkTheme.AddCardNumeric(cardFps, 85, 30, 55, Math.Clamp(_config.EQClientIni.MaxFPS, 0, 999), 0, 999);

        DarkTheme.AddCardLabel(cardFps, "Background FPS:", 165, 32);
        _nudMaxBGFPS = DarkTheme.AddCardNumeric(cardFps, 275, 30, 55, Math.Clamp(_config.EQClientIni.MaxBGFPS, 0, 999), 0, 999);

        DarkTheme.AddCardHint(cardFps, "0 = unlimited  |  Default 80  |  Written to eqclient.ini on Save", 10, 56);

        // Read current values from eqclient.ini and show as ghost hint
        var (iniFps, iniBgFps) = ReadIniFpsValues();
        var iniLabel = new Label
        {
            Text = "Current Settings:",
            Location = new Point(340, 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgDimGray,
            Font = new Font("Segoe UI", 7.5f),
            BackColor = Color.Transparent
        };
        cardFps.Controls.Add(iniLabel);
        var iniHint = new Label
        {
            Text = $"eqclient.ini: MaxFPS={iniFps}  MaxBGFPS={iniBgFps}",
            Location = new Point(340, 44),
            AutoSize = true,
            ForeColor = DarkTheme.FgDimGray,
            Font = new Font("Consolas", 7.5f, FontStyle.Italic),
            BackColor = Color.Transparent
        };
        cardFps.Controls.Add(iniHint);

        y += 93;

        // ─── Bottom buttons ──────────────────────────────────────
        var btnSave = DarkTheme.MakePrimaryButton("Save", Pad, y);
        btnSave.Click += (_, _) =>
        {
            ApplyAllSettings();
            ConfigManager.Save(_config);
            ConfigManager.FlushSave();
            Close();
        };
        Controls.Add(btnSave);

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, Pad + 100, y);
        btnApply.FlatAppearance.BorderColor = DarkTheme.Border;
        btnApply.FlatAppearance.BorderSize = 1;
        btnApply.Click += (_, _) =>
        {
            ApplyAllSettings();
            ConfigManager.Save(_config);
            ConfigManager.FlushSave();
            _forceNextRefresh = true;
            RefreshList();
        };
        Controls.Add(btnApply);

        var btnReset = DarkTheme.MakeButton("Reset", DarkTheme.BgMedium, Pad + 200, y);
        btnReset.FlatAppearance.BorderColor = DarkTheme.Border;
        btnReset.FlatAppearance.BorderSize = 1;
        btnReset.Click += (_, _) => ResetToInitial();
        Controls.Add(btnReset);

        var btnClose = DarkTheme.MakeButton("Close", DarkTheme.BgMedium, FormW - Pad - 90 - 12, y);
        btnClose.FlatAppearance.BorderColor = DarkTheme.Border;
        btnClose.FlatAppearance.BorderSize = 1;
        btnClose.Click += (_, _) => Close();
        Controls.Add(btnClose);

        y += 45;
        ClientSize = new Size(FormW - 16, y);

        // ─── Timer ───────────────────────────────────────────────
        _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMs };
        _refreshTimer.Tick += (_, _) => RefreshList();
        _refreshTimer.Start();

        FormClosed += (_, _) =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        };

        RefreshList();
    }

    private DataGridView BuildProcessGrid(int y)
    {
        var grid = new DataGridView
        {
            Location = new Point(Pad, y),
            Size = new Size(GridW, 160),
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            EditMode = DataGridViewEditMode.EditOnEnter,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = DarkTheme.Border,
            BackgroundColor = DarkTheme.BgDark,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = DarkTheme.BgDark,
                ForeColor = DarkTheme.FgWhite,
                SelectionBackColor = DarkTheme.GridSelection,
                SelectionForeColor = DarkTheme.FgWhite,
                Font = new Font("Consolas", 9),
                Padding = new Padding(6, 3, 6, 3)
            },
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = DarkTheme.BgPanel,
                ForeColor = DarkTheme.FgWhite,
                SelectionBackColor = DarkTheme.GridSelection,
                SelectionForeColor = DarkTheme.FgWhite
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = DarkTheme.BgInput,
                ForeColor = DarkTheme.CardCyan,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0)
            },
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 30,
            RowTemplate = { Height = 28 },
            EnableHeadersVisualStyles = false,
            ScrollBars = ScrollBars.Vertical
        };

        // Columns
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Slot", HeaderText = "#", ReadOnly = true, Width = 32 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PID", HeaderText = "PID", ReadOnly = true, Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Character", HeaderText = "Character", ReadOnly = true, Width = 140 });
        // Priority = inline ComboBox dropdown per-process
        var priorityCol = new DataGridViewComboBoxColumn
        {
            Name = "Priority",
            HeaderText = "Priority",
            Width = 105,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        priorityCol.Items.AddRange(Priorities);
        grid.Columns.Add(priorityCol);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Affinity", HeaderText = "Affinity", ReadOnly = true, Width = 80 });

        // Kill button column
        var killCol = new DataGridViewButtonColumn
        {
            Name = "Kill",
            HeaderText = "Kill",
            Text = "\u2716",
            UseColumnTextForButtonValue = true,
            Width = 45,
            FlatStyle = FlatStyle.Flat
        };
        grid.Columns.Add(killCol);

        foreach (DataGridViewColumn col in grid.Columns)
        {
            col.SortMode = DataGridViewColumnSortMode.NotSortable;
            col.Resizable = DataGridViewTriState.False;
        }

        // Pause refresh while editing a dropdown to prevent the grid rebuild
        // from destroying the active combo cell mid-interaction (the "blink" bug)
        grid.EditingControlShowing += (_, e) =>
        {
            _refreshTimer.Stop();
            if (e.Control is ComboBox cb)
                cb.DropDownClosed += (_, _) => _refreshTimer.Start();
        };
        grid.CellEndEdit += (_, _) => _refreshTimer.Start();

        // Commit dropdown changes immediately (don't wait for focus loss)
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell?.OwningColumn.Name == "Priority")
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        // Apply priority when the per-row dropdown value changes.
        // Only affects the live process — does NOT change the global setting.
        // Use the Card 1 "All EQ Clients" combo + Save/Apply for global changes.
        grid.CellValueChanged += (_, e) =>
        {
            if (_isRefreshing || e.RowIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "Priority") return;

            var row = grid.Rows[e.RowIndex];
            if (!int.TryParse(row.Cells["PID"].Value?.ToString(), out int pid)) return;
            var priority = row.Cells["Priority"].Value?.ToString();
            if (string.IsNullOrEmpty(priority)) return;

            AffinityManager.SetProcessPriority(pid, priority);
        };

        // Handle Kill button click
        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Kill") return;
            var row = grid.Rows[e.RowIndex];
            if (!int.TryParse(row.Cells["PID"].Value?.ToString(), out int pid)) return;

            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                proc.Kill();
                _selectedPid = 0;
                _forceNextRefresh = true;
                RefreshList();
            }
            catch { /* process already gone */ }
        };


        return grid;
    }

    private void RefreshList()
    {
        if (_isRefreshing) return;
        if (_selectedPid > 0 && !_forceNextRefresh) return;
        _forceNextRefresh = false;
        _isRefreshing = true;
        try
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

                if (client == active)
                {
                    var row = _grid.Rows[rowIdx];
                    foreach (DataGridViewCell cell in row.Cells)
                    {
                        cell.Style.BackColor = DarkTheme.ActiveRowBg;
                        cell.Style.SelectionBackColor = DarkTheme.ActiveRowBg;
                        cell.Style.ForeColor = DarkTheme.CardGreen;
                        cell.Style.SelectionForeColor = DarkTheme.CardGreen;
                    }
                }
            }

            // Status
            int count = clients.Count;
            _statusLabel.Text = count switch
            {
                0 => "No clients detected",
                1 => $"1 client  |  priority {_config.Affinity.ActivePriority}",
                _ => $"{count} clients  |  priority {_config.Affinity.ActivePriority}"
            };
            _statusLabel.ForeColor = count > 0 ? DarkTheme.FgGray : DarkTheme.FgDimGray;

            // Restore selection by PID
            if (_selectedPid > 0)
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.Cells["PID"].Value?.ToString() == _selectedPid.ToString())
                    {
                        row.Selected = true;
                        return;
                    }
                }
            }
            _grid.ClearSelection();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>
    /// Writes all card settings back to config and eqclient.ini.
    /// </summary>
    private void ApplyAllSettings()
    {
        // Priority preset — apply to all live processes if not "None"
        var preset = _cboPriority.SelectedItem?.ToString() ?? "None";
        if (preset != "None")
        {
            _config.Affinity.ActivePriority = preset;
            _config.Affinity.BackgroundPriority = preset;
            foreach (var client in _getClients())
                AffinityManager.SetProcessPriority(client.ProcessId, preset);
        }

        // Core Assignment — write slot values to config
        for (int i = 0; i < 6; i++)
            _config.EQClientIni.CPUAffinitySlots[i] = (int)_slotPickers[i].Value;

        // Card 3: FPS
        _config.EQClientIni.MaxFPS = (int)_nudMaxFPS.Value;
        _config.EQClientIni.MaxBGFPS = (int)_nudMaxBGFPS.Value;

        // Write FPS + CPUAffinity to eqclient.ini
        if (!string.IsNullOrEmpty(_config.EQPath))
        {
            try { EQClientSettingsForm.ApplyProcessManagerToIni(_config); }
            catch { /* non-critical — will apply next launch */ }
        }
    }

    private (string fps, string bgFps) ReadIniFpsValues()
    {
        try
        {
            var iniPath = Path.Combine(_config.EQPath, "eqclient.ini");
            if (!File.Exists(iniPath)) return ("?", "?");

            var lines = File.ReadAllLines(iniPath, System.Text.Encoding.Default);
            string fps = "not set", bgFps = "not set";
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("MaxFPS=", StringComparison.OrdinalIgnoreCase))
                    fps = trimmed.Split('=')[1].Trim();
                else if (trimmed.StartsWith("MaxBGFPS=", StringComparison.OrdinalIgnoreCase))
                    bgFps = trimmed.Split('=')[1].Trim();
            }
            return (fps, bgFps);
        }
        catch { return ("?", "?"); }
    }

    private void ResetToInitial()
    {
        // Priority preset
        _cboPriority.SelectedItem = Priorities.Contains(_initialPriority) ? _initialPriority : "None";

        // Core slots
        for (int i = 0; i < 6; i++)
            _slotPickers[i].Value = i < _initialSlots.Length ? _initialSlots[i] : i;

        // FPS
        _nudMaxFPS.Value = Math.Clamp(_initialMaxFPS, 0, 999);
        _nudMaxBGFPS.Value = Math.Clamp(_initialMaxBGFPS, 0, 999);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
            _tooltip?.Dispose();
        }
        base.Dispose(disposing);
    }
}
