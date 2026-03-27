using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Live process manager showing PID, character, priority, and affinity for all EQ clients.
/// Priority column is an inline dropdown — change it per-process directly in the grid.
/// Core checkboxes in the control card let you toggle individual CPU cores per-process.
/// </summary>
public class ProcessManagerForm : Form
{
    private const int RefreshIntervalMs = 2000;
    private const int FormW = 560;
    private const int GridW = 520;
    private const int Pad = 15;

    private static readonly string[] Priorities = { "High", "AboveNormal", "Normal", "BelowNormal" };

    private readonly Func<IReadOnlyList<EQClient>> _getClients;
    private readonly Func<EQClient?> _getActiveClient;
    private readonly Action _forceApply;
    private readonly Action<bool> _toggleAffinity;
    private readonly AppConfig _config;

    private DataGridView _grid = null!;
    private Label _statusLabel = null!;
    private Label _lblSelectedName = null!;
    private CheckBox _chkAffinityEnabled = null!;
    private CheckBox[] _coreChecks = null!;
    private Label _lblMask = null!;
    private ToolTip _tooltip = null!;
    private System.Windows.Forms.Timer _refreshTimer = null!;
    private bool _isRefreshing;
    private bool _suppressCoreApply;
    private bool _forceNextRefresh;

    // Track selected PID across refreshes so selection survives auto-refresh
    private int _selectedPid;

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
        InitializeForm();
    }

    private void InitializeForm()
    {
        Text = "EQSwitch — Process Manager";
        Size = new Size(FormW, 550);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = DarkTheme.BgDark;
        ForeColor = DarkTheme.FgWhite;
        Font = new Font("Segoe UI", 9);

        _tooltip = new ToolTip { InitialDelay = 300, ReshowDelay = 200 };

        int y = 10;

        // ─── Header bar ──────────────────────────────────────────
        var (coreCount, systemMask) = AffinityManager.DetectCores();
        var headerPanel = new Panel
        {
            Location = new Point(Pad, y),
            Size = new Size(GridW, 30),
            BackColor = DarkTheme.BgPanel
        };
        headerPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(DarkTheme.Border, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, headerPanel.Width - 1, headerPanel.Height - 1);
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

        // ─── Process grid (Priority column is an inline dropdown) ─
        _grid = new DataGridView
        {
            Location = new Point(Pad, y),
            Size = new Size(GridW, 240),
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
                SelectionBackColor = Color.FromArgb(50, 44, 70),
                SelectionForeColor = DarkTheme.FgWhite,
                Font = new Font("Consolas", 9),
                Padding = new Padding(6, 3, 6, 3)
            },
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = DarkTheme.BgPanel,
                ForeColor = DarkTheme.FgWhite,
                SelectionBackColor = Color.FromArgb(50, 44, 70),
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

        // Read-only text columns
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Slot", HeaderText = "#", ReadOnly = true, Width = 32 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PID", HeaderText = "PID", ReadOnly = true, Width = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Character", HeaderText = "Character", ReadOnly = true, Width = 140 });

        // Priority = inline ComboBox dropdown
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
        _grid.Columns.Add(priorityCol);

        // More read-only text columns
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Affinity", HeaderText = "Affinity", ReadOnly = true, Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Source", ReadOnly = true, Width = 60 });

        // Kill button column
        var killCol = new DataGridViewButtonColumn
        {
            Name = "Kill",
            HeaderText = "Kill",
            Text = "\u2716",
            UseColumnTextForButtonValue = true,
            Width = 32,
            FlatStyle = FlatStyle.Flat
        };
        _grid.Columns.Add(killCol);

        foreach (DataGridViewColumn col in _grid.Columns)
        {
            col.SortMode = DataGridViewColumnSortMode.NotSortable;
            col.Resizable = DataGridViewTriState.False;
        }

        // Commit dropdown changes immediately (don't wait for focus loss)
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell?.OwningColumn.Name == "Priority")
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        // Apply priority when the dropdown value changes — both live + saved to config
        _grid.CellValueChanged += (_, e) =>
        {
            if (_isRefreshing || e.RowIndex < 0) return;
            if (_grid.Columns[e.ColumnIndex].Name != "Priority") return;

            var row = _grid.Rows[e.RowIndex];
            if (!int.TryParse(row.Cells["PID"].Value?.ToString(), out int pid)) return;
            var priority = row.Cells["Priority"].Value?.ToString();
            if (string.IsNullOrEmpty(priority)) return;

            // Apply to the live process immediately
            AffinityManager.SetProcessPriority(pid, priority);

            // Persist as per-slot override
            if (int.TryParse(row.Cells["Slot"].Value?.ToString(), out int slot))
            {
                int slotIdx = slot - 1; // grid shows 1-based, config is 0-based
                var profile = _config.Characters.Find(c => c.SlotIndex == slotIdx);
                if (profile != null)
                {
                    profile.PriorityOverride = priority;
                }
                else
                {
                    _config.Characters.Add(new CharacterProfile
                    {
                        Name = row.Cells["Character"].Value?.ToString() ?? $"Slot {slot}",
                        SlotIndex = slotIdx,
                        PriorityOverride = priority
                    });
                }
                ConfigManager.Save(_config);
            }
        };

        // Handle Kill button click
        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Kill") return;
            var row = _grid.Rows[e.RowIndex];
            if (!int.TryParse(row.Cells["PID"].Value?.ToString(), out int pid)) return;
            var name = row.Cells["Character"].Value?.ToString() ?? "PID " + pid;

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

        _grid.SelectionChanged += (_, _) => SyncCoreCheckboxes();
        Controls.Add(_grid);
        y += 248;

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
        y += 24;

        // ─── Control card: core affinity ─────────────────────────
        var cardControl = DarkTheme.MakeCard(this, "\u2694", "Core Affinity", DarkTheme.CardGold, Pad, y, GridW, 115);

        DarkTheme.AddCardLabel(cardControl, "Selected:", 10, 8);
        _lblSelectedName = new Label
        {
            Text = "click a row above",
            Location = new Point(75, 8),
            AutoSize = true,
            ForeColor = DarkTheme.FgDimGray,
            Font = new Font("Consolas", 9f, FontStyle.Italic)
        };
        cardControl.Controls.Add(_lblSelectedName);

        _lblMask = new Label
        {
            Text = "",
            Location = new Point(GridW - 80, 8),
            AutoSize = true,
            ForeColor = DarkTheme.FgDimGray,
            Font = new Font("Consolas", 8.5f)
        };
        cardControl.Controls.Add(_lblMask);

        // Core checkboxes with number labels below
        DarkTheme.AddCardLabel(cardControl, "Cores:", 10, 38);
        _coreChecks = new CheckBox[coreCount];
        int coreX = 55;
        int coreY = 34;
        int perRow = Math.Min(coreCount, 20);
        int checkW = Math.Max(22, (GridW - 65) / perRow);
        for (int i = 0; i < coreCount; i++)
        {
            var coreType = i < 8 ? "P-core (Performance)" : "E-core (Efficiency)";
            int cx = coreX + (i % perRow) * checkW;
            int cy = coreY + (i / perRow) * 36;
            var chk = new CheckBox
            {
                Text = "",
                Location = new Point(cx, cy),
                Size = new Size(checkW, 18),
                BackColor = Color.Transparent,
                Enabled = false
            };
            _tooltip.SetToolTip(chk, $"CPU {i} — {coreType}");
            chk.CheckedChanged += (_, _) => ApplyAffinityFromCheckboxes();
            cardControl.Controls.Add(chk);
            _coreChecks[i] = chk;

            var lbl = new Label
            {
                Text = i.ToString(),
                Location = new Point(cx + 2, cy + 17),
                Size = new Size(checkW, 13),
                ForeColor = i < 8 ? DarkTheme.CardGreen : DarkTheme.CardBlue,
                Font = new Font("Consolas", 7f),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.TopLeft
            };
            cardControl.Controls.Add(lbl);
        }

        y += 125;

        // ─── Bottom buttons ──────────────────────────────────────
        var btnSave = DarkTheme.MakePrimaryButton("Save", Pad, y);
        btnSave.Click += (_, _) =>
        {
            _forceApply();
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
            _forceApply();
            ConfigManager.Save(_config);
            ConfigManager.FlushSave();
        };
        Controls.Add(btnApply);

        var btnClose = DarkTheme.MakeButton("Close", DarkTheme.BgMedium, FormW - Pad - 90 - 12, y);
        btnClose.FlatAppearance.BorderColor = DarkTheme.Border;
        btnClose.FlatAppearance.BorderSize = 1;
        btnClose.Click += (_, _) => Close();
        Controls.Add(btnClose);

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

                // Check per-character overrides
                var hasAffinityOvr = false;
                var hasPriorityOvr = false;
                if (!string.IsNullOrEmpty(client.CharacterName))
                {
                    foreach (var cp in _config.Characters)
                    {
                        if (cp.Name.Equals(client.CharacterName, StringComparison.OrdinalIgnoreCase))
                        {
                            hasAffinityOvr = cp.AffinityOverride.HasValue;
                            hasPriorityOvr = cp.PriorityOverride != null;
                            break;
                        }
                    }
                }
                var source = (hasAffinityOvr || hasPriorityOvr) ? "Custom" : "Global";

                int rowIdx = _grid.Rows.Add(
                    (client.SlotIndex + 1).ToString(),
                    client.ProcessId.ToString(),
                    name,
                    priority,
                    $"0x{procMask:X}",
                    source);

                if (hasAffinityOvr || hasPriorityOvr)
                    _grid.Rows[rowIdx].Cells["Source"].Style.ForeColor = DarkTheme.CardCyan;

                if (client == active)
                {
                    var row = _grid.Rows[rowIdx];
                    foreach (DataGridViewCell cell in row.Cells)
                    {
                        cell.Style.BackColor = Color.FromArgb(20, 80, 50);
                        cell.Style.SelectionBackColor = Color.FromArgb(20, 80, 50);
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
                1 => $"1 client  |  active mask 0x{_config.Affinity.ActiveMask:X}  bg mask 0x{_config.Affinity.BackgroundMask:X}",
                _ => $"{count} clients  |  active mask 0x{_config.Affinity.ActiveMask:X}  bg mask 0x{_config.Affinity.BackgroundMask:X}"
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

    private void SyncCoreCheckboxes()
    {
        if (_isRefreshing) return;

        if (_grid.SelectedRows.Count > 0)
        {
            var row = _grid.SelectedRows[0];
            var name = row.Cells["Character"].Value?.ToString() ?? "?";

            if (int.TryParse(row.Cells["PID"].Value?.ToString(), out int pid))
                _selectedPid = pid;

            _lblSelectedName.Text = name;
            _lblSelectedName.ForeColor = DarkTheme.CardCyan;
            _lblSelectedName.Font = new Font("Consolas", 9f, FontStyle.Bold);

            // Sync core checkboxes
            var (procMask, _) = AffinityManager.GetProcessAffinity(pid);
            _suppressCoreApply = true;
            for (int i = 0; i < _coreChecks.Length; i++)
            {
                _coreChecks[i].Checked = (procMask & (1L << i)) != 0;
                _coreChecks[i].Enabled = true;
            }
            _suppressCoreApply = false;
            _lblMask.Text = $"0x{procMask:X}";
            _lblMask.ForeColor = DarkTheme.FgGray;
        }
        else
        {
            _selectedPid = 0;
            _lblSelectedName.Text = "click a row above";
            _lblSelectedName.ForeColor = DarkTheme.FgDimGray;
            _lblSelectedName.Font = new Font("Consolas", 9f, FontStyle.Italic);
            _suppressCoreApply = true;
            for (int i = 0; i < _coreChecks.Length; i++)
            {
                _coreChecks[i].Checked = false;
                _coreChecks[i].Enabled = false;
            }
            _suppressCoreApply = false;
            _lblMask.Text = "";
        }
    }

    private void ApplyAffinityFromCheckboxes()
    {
        if (_suppressCoreApply) return;
        if (_grid.SelectedRows.Count == 0) return;

        var row = _grid.SelectedRows[0];
        if (!int.TryParse(row.Cells["PID"].Value?.ToString(), out int pid)) return;

        long mask = 0;
        for (int i = 0; i < _coreChecks.Length; i++)
        {
            if (_coreChecks[i].Checked)
                mask |= 1L << i;
        }
        if (mask == 0) return;

        AffinityManager.SetProcessAffinity(pid, mask);
        row.Cells["Affinity"].Value = $"0x{mask:X}";
        _lblMask.Text = $"0x{mask:X}";

        // Persist as per-slot override
        if (int.TryParse(row.Cells["Slot"].Value?.ToString(), out int slot))
        {
            int slotIdx = slot - 1;
            var profile = _config.Characters.Find(c => c.SlotIndex == slotIdx);
            if (profile != null)
            {
                profile.AffinityOverride = mask;
            }
            else
            {
                _config.Characters.Add(new CharacterProfile
                {
                    Name = row.Cells["Character"].Value?.ToString() ?? $"Slot {slot}",
                    SlotIndex = slotIdx,
                    AffinityOverride = mask
                });
            }
            ConfigManager.Save(_config);
        }
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
