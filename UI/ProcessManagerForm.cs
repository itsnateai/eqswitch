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
    private NumericUpDown _nudLaunchRetries = null!;
    private NumericUpDown _nudLaunchRetryDelay = null!;

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
        Font = DarkTheme.FontUI9;

        _tooltip = new ToolTip { InitialDelay = 300, ReshowDelay = 200 };

        var (coreCount, systemMask) = AffinityManager.DetectCores();
        int y = 10;

        // ─── Card 1: CPU Affinity Handling ───────────────────────
        var cardPriority = DarkTheme.MakeCard(this, "\u26A1", "CPU Affinity/Priority Handling", DarkTheme.CardGold, Pad, y, GridW, 105);

        _chkAffinityEnabled = new CheckBox
        {
            Text = "  Enable CPU Affinity/Priority Handling",
            Location = new Point(15, 26),
            AutoSize = true,
            ForeColor = _config.Affinity.Enabled ? DarkTheme.CardGreen : DarkTheme.FgGray,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Checked = _config.Affinity.Enabled,
            Appearance = Appearance.Normal
        };
        // Owner-draw a 16x16 checkbox with contrast: gray border, white checkmark
        _chkAffinityEnabled.Paint += (sender, e) =>
        {
            var cb = (CheckBox)sender!;
            var g = e.Graphics;
            g.Clear(cb.BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int boxSize = 16;
            int boxY = (cb.Height - boxSize) / 2;
            var boxRect = new Rectangle(0, boxY, boxSize, boxSize);

            // Box: dark fill with subtle border
            using var fillBrush = new SolidBrush(cb.Checked ? Color.FromArgb(30, 70, 40) : Color.FromArgb(40, 40, 45));
            g.FillRectangle(fillBrush, boxRect);
            using var borderPen = new Pen(cb.Checked ? DarkTheme.CardGreen : DarkTheme.FgGray, 1.5f);
            g.DrawRectangle(borderPen, boxRect);

            // Checkmark: bright white for contrast
            if (cb.Checked)
            {
                using var checkPen = new Pen(Color.White, 2f);
                g.DrawLine(checkPen, 3, boxY + 8, 6, boxY + 12);
                g.DrawLine(checkPen, 6, boxY + 12, 13, boxY + 4);
            }

            // Draw text
            var textRect = new Rectangle(boxSize + 6, 0, cb.Width - boxSize - 6, cb.Height);
            TextRenderer.DrawText(g, cb.Text, cb.Font, textRect, cb.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        _chkAffinityEnabled.CheckedChanged += (_, _) =>
        {
            _toggleAffinity(_chkAffinityEnabled.Checked);
            _chkAffinityEnabled.ForeColor = _chkAffinityEnabled.Checked
                ? DarkTheme.CardGreen : DarkTheme.FgGray;
            _chkAffinityEnabled.Invalidate();
        };
        cardPriority.Controls.Add(_chkAffinityEnabled);

        // Priority preset — right-aligned, prominent
        DarkTheme.AddCardLabel(cardPriority, "Priority Preset:", 320, 32);
        var priorityOptions = new[] { "None", "High", "AboveNormal", "Normal", "BelowNormal" };
        _cboPriority = DarkTheme.AddCardComboBox(cardPriority, 420, 30, 125, priorityOptions);
        _cboPriority.SelectedItem = Priorities.Contains(_config.Affinity.ActivePriority)
            ? _config.Affinity.ActivePriority : "None";

        DarkTheme.AddCardHint(cardPriority, "None = per-client in grid  |  High = prevents VD crashes + autofollow", 10, 50);

        // Retry on launch — EQ resets its own priority after starting
        DarkTheme.AddCardHint(cardPriority, "On launch: EQ resets priority to Normal — Try", 10, 74);
        _nudLaunchRetries = DarkTheme.AddCardNumeric(cardPriority, 248, 70, 40, _config.Affinity.LaunchRetryCount, 0, 10);
        DarkTheme.AddCardHint(cardPriority, "times, rest", 293, 74);
        _nudLaunchRetryDelay = DarkTheme.AddCardNumeric(cardPriority, 348, 70, 60, _config.Affinity.LaunchRetryDelayMs, 500, 10000);
        DarkTheme.AddCardHint(cardPriority, "ms after launch", 413, 74);

        y += 113;

        // ─── Process grid ────────────────────────────────────────
        _grid = BuildProcessGrid(y);
        Controls.Add(_grid);
        y += 30 + 28 * 4 + 2 + 8;  // grid height + padding

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

        DarkTheme.AddCardHint(cardCores, $"{coreCount} cores detected  |  Shared by all EQ clients  |  Core 0 = first", 10, 108);

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
            Font = DarkTheme.FontUI75,
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
            Size = new Size(GridW, 30 + 28 * 4 + 2),  // header + 4 rows + border
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
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Priority", HeaderText = "Priority", ReadOnly = true, Width = 105 });
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

        // Priority is read-only — use the Preset dropdown + Apply to change

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
            catch (ArgumentException) { /* process already exited */ }
            catch (Exception ex) { FileLogger.Warn($"Kill process failed for PID {pid}: {ex.Message}"); }
        };

        // Suppress DataGridView error dialogs — log instead of crashing
        grid.DataError += (_, e) =>
        {
            FileLogger.Warn($"Grid data error at [{e.RowIndex},{e.ColumnIndex}]: {e.Exception?.Message}");
            e.ThrowException = false;
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
                var rawPriority = AffinityManager.GetProcessPriorityName(client.ProcessId);
                // Clamp to known values so the combo cell doesn't crash on edit
                var priority = Priorities.Contains(rawPriority) ? rawPriority : "Normal";
                var name = $"Client {client.SlotIndex + 1}";

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
        // Priority preset — save to config, but only apply to live processes when enabled
        var preset = _cboPriority.SelectedItem?.ToString() ?? "None";
        if (preset != "None")
        {
            _config.Affinity.ActivePriority = preset;
            _config.Affinity.BackgroundPriority = preset;
            if (_config.Affinity.Enabled)
            {
                foreach (var client in _getClients())
                    AffinityManager.SetProcessPriority(client.ProcessId, preset);
            }
        }

        // Launch retry settings
        _config.Affinity.LaunchRetryCount = (int)_nudLaunchRetries.Value;
        _config.Affinity.LaunchRetryDelayMs = (int)_nudLaunchRetryDelay.Value;

        // Core Assignment — write slot values to config
        for (int i = 0; i < 6; i++)
            _config.EQClientIni.CPUAffinitySlots[i] = (int)_slotPickers[i].Value;

        // Card 3: FPS
        _config.EQClientIni.MaxFPS = (int)_nudMaxFPS.Value;
        _config.EQClientIni.MaxBGFPS = (int)_nudMaxBGFPS.Value;

        // Track in ConfiguredKeys so EnforceOverrides re-enforces on launch
        if (_config.EQClientIni.MaxFPS > 0) _config.EQClientIni.ConfiguredKeys.Add("MaxFPS");
        if (_config.EQClientIni.MaxBGFPS > 0) _config.EQClientIni.ConfiguredKeys.Add("MaxBGFPS");

        // Write FPS + CPUAffinity to eqclient.ini
        if (!string.IsNullOrEmpty(_config.EQPath))
        {
            try { EQClientSettingsForm.ApplyProcessManagerToIni(_config); }
            catch (Exception ex) { FileLogger.Warn($"ProcessManager: failed to write INI settings: {ex.Message}"); }
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
        catch (Exception ex) { FileLogger.Warn($"ProcessManager: failed to read FPS from INI: {ex.Message}"); return ("?", "?"); }
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
            // Dispose GDI Font handles — WinForms doesn't own control fonts
            _chkAffinityEnabled?.Font?.Dispose();
            _statusLabel?.Font?.Dispose();
            _grid?.DefaultCellStyle?.Font?.Dispose();
            _grid?.ColumnHeadersDefaultCellStyle?.Font?.Dispose();
            DarkTheme.DisposeControlFonts(this);
        }
        base.Dispose(disposing);
    }
}
