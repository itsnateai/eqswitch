// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Consolidated process manager with clear settings sections:
/// 1. Windows Priority — keeps EQ from getting starved (prevents VD crashes, enables autofollow)
/// 2. Running Clients — the live process grid for per-process kill / status
/// 3. Core Assignment — spreads EQ across cores so it doesn't single-thread on core 0
/// 4. FPS Limits — MaxFPS / MaxBGFPS from eqclient.ini
///
/// DPI: rebuilt on the <see cref="CardStack"/>/<see cref="Card"/>/<see cref="Fields"/>/<see cref="Bars"/>
/// layout containers (the v3.24.33 SettingsForm pattern) — correct-by-construction at 100% and 150%
/// with no absolute pixel coordinates. The only Bounds literal is the grid's Height (scaled ×f in Load,
/// since AutoScaleMode.Dpi leaves it alone); fixed-width fit fields are sized once by DpiScale.SizeFitFields.
/// </summary>
public class ProcessManagerForm : EqSwitchForm
{
    // Remembers last-open location across opens within a session. Static so
    // all instances share it; falls back to CenterScreen on first open
    // (this is a top-level utility form, not a parent-modal dialog).
    // Process lifetime only; cross-session persistence would need config.
    private static Point? _lastLocation;

    private const int RefreshIntervalMs = 2000;

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

    // Card 1: Priority preset (applies to all clients, or "None" for per-row manual)
    private ComboBox _cboPriority = null!;
    private NumericUpDown _nudLaunchRetries = null!;
    private NumericUpDown _nudLaunchRetryDelay = null!;

    // Card 3: Core Assignment (6 slots matching eqclient.ini CPUAffinity0-5)
    private NumericUpDown[] _slotPickers = null!;

    // Card 4: FPS
    private NumericUpDown _nudMaxFPS = null!;
    private NumericUpDown _nudMaxBGFPS = null!;
    private Font _ghostFont = null!;
    private Font _boldHintFont = null!;

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
        // Restore last-open position if available; otherwise center in Load (once Width is final).
        if (_lastLocation.HasValue)
        {
            StartPosition = FormStartPosition.Manual;
            Location = _lastLocation.Value;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }
        FormClosing += (_, _) => _lastLocation = Location;
        BackColor = DarkTheme.BgDark;
        ForeColor = DarkTheme.FgWhite;
        Font = DarkTheme.FontUI9;

        _tooltip = new ToolTip { InitialDelay = 300, ReshowDelay = 200 };

        var (coreCount, _) = AffinityManager.DetectCores();

        // ─── Card stack (scrolls if it outgrows the window) ──────────────
        // Added to the form FIRST so it sits at the bottom of the z-order → the footer
        // (added last, Dock=Bottom) docks first and the stack Fills the remaining area.
        var stack = new CardStack(this);

        // ─── Card 1: CPU Priority Handling ───────────────────────────────
        var cardPriority = stack.NewCard("⚡", "CPU Priority Handling", DarkTheme.CardGold);

        _chkAffinityEnabled = new CheckBox
        {
            Text = "  Enable CPU Priority Handling",
            AutoSize = true,
            ForeColor = _config.Affinity.Enabled ? DarkTheme.CardGreen : DarkTheme.FgGray,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            BackColor = DarkTheme.BgPanel,   // match the card body so the owner-draw g.Clear blends
            Checked = _config.Affinity.Enabled,
            Appearance = Appearance.Normal,
        };
        // Owner-draw a DPI-scaled 16dp checkbox: gray/green border, white checkmark. Paint geometry
        // is NOT auto-scaled — every literal is derived from the control's device DPI so the glyph
        // tracks the (already-scaled) control.
        _chkAffinityEnabled.Paint += (sender, e) =>
        {
            var cb = (CheckBox)sender!;
            var g = e.Graphics;
            g.Clear(cb.BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int boxSize = cb.LogicalToDeviceUnits(16);
            int boxY = (cb.Height - boxSize) / 2;
            var boxRect = new Rectangle(0, boxY, boxSize, boxSize);

            using var fillBrush = new SolidBrush(cb.Checked ? Color.FromArgb(30, 70, 40) : Color.FromArgb(40, 40, 45));
            g.FillRectangle(fillBrush, boxRect);
            using var borderPen = new Pen(cb.Checked ? DarkTheme.CardGreen : DarkTheme.FgGray, 1.5f);
            g.DrawRectangle(borderPen, boxRect);

            if (cb.Checked)
            {
                using var checkPen = new Pen(Color.White, cb.LogicalToDeviceUnits(2));
                g.DrawLine(checkPen, cb.LogicalToDeviceUnits(3),  boxY + cb.LogicalToDeviceUnits(8),
                                     cb.LogicalToDeviceUnits(6),  boxY + cb.LogicalToDeviceUnits(12));
                g.DrawLine(checkPen, cb.LogicalToDeviceUnits(6),  boxY + cb.LogicalToDeviceUnits(12),
                                     cb.LogicalToDeviceUnits(13), boxY + cb.LogicalToDeviceUnits(4));
            }

            int textGap = cb.LogicalToDeviceUnits(6);
            var textRect = new Rectangle(boxSize + textGap, 0, cb.Width - boxSize - textGap, cb.Height);
            TextRenderer.DrawText(g, cb.Text, cb.Font, textRect, cb.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        _chkAffinityEnabled.CheckedChanged += (_, _) =>
        {
            _toggleAffinity(_chkAffinityEnabled.Checked);
            _chkAffinityEnabled.ForeColor = _chkAffinityEnabled.Checked
                ? DarkTheme.CardGreen : DarkTheme.FgGray;
            _chkAffinityEnabled.Invalidate();
        };
        cardPriority.Check(_chkAffinityEnabled);

        // Priority preset
        _cboPriority = Fields.Combo(125, "None", "High", "AboveNormal", "Normal", "BelowNormal");
        _cboPriority.SelectedItem = Priorities.Contains(_config.Affinity.ActivePriority)
            ? _config.Affinity.ActivePriority : "None";
        cardPriority.RowFit("Priority Preset:", _cboPriority);
        cardPriority.Hint("Enforces a CPU priority on every client — High is recommended.");

        // Launch retry — EQ resets its own priority shortly after starting.
        cardPriority.Hint("On launch EQ resets its priority to Normal — EQSwitch re-applies it:");
        _nudLaunchRetries = Fields.Numeric(3, 7, _config.Affinity.LaunchRetryCount, 40);
        // Shown in seconds; stored as ms in LaunchRetryDelayMs (schema/validator unchanged).
        _nudLaunchRetryDelay = Fields.Numeric(0.5m, 10m, _config.Affinity.LaunchRetryDelayMs / 1000m, 60);
        _nudLaunchRetryDelay.DecimalPlaces = 1;
        _nudLaunchRetryDelay.Increment = 0.5m;
        cardPriority.FlowRow("Retry:", _nudLaunchRetries, Inline("times,"), _nudLaunchRetryDelay, Inline("sec apart"));

        // ─── Card 2: Running Clients (live grid + status) ────────────────
        var cardClients = stack.NewCard("🖥", "Running Clients", DarkTheme.CardBlue);
        _grid = BuildProcessGrid();
        cardClients.Full(_grid);
        _statusLabel = new Label
        {
            Text = "No clients detected",
            AutoSize = true,
            ForeColor = DarkTheme.FgDimGray,
            Font = new Font("Consolas", 8.25f),
        };
        cardClients.Full(_statusLabel);

        // ─── Card 3: CPU Affinity Thread Mapping ─────────────────────────
        // 6 slots matching eqclient.ini CPUAffinity0-5 — a 3×2 grid of "Thread N → [core]" pairs.
        var cardCores = stack.NewCard("🧠", "CPU Affinity Thread Mapping", DarkTheme.CardCyan);
        cardCores.Hint("EQ uses 6 internal threads — assign each to a CPU core to spread the load.");

        var slots = _config.EQClientIni.CPUAffinitySlots;
        _slotPickers = new NumericUpDown[6];
        var coreGrid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 6,
            RowCount = 2,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        for (int c = 0; c < 6; c++) coreGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        coreGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coreGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int i = 0; i < 6; i++)
        {
            int col = i % 3, row = i / 3;
            var lbl = new Label
            {
                Text = $"Thread {i + 1} →",
                AutoSize = true,
                ForeColor = DarkTheme.FgGray,
                Font = DarkTheme.FontUI9,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(col == 0 ? 0 : 18, 5, 6, 3),
            };
            int coreVal = i < slots.Length ? Math.Clamp(slots[i], 0, coreCount - 1) : i;
            _slotPickers[i] = Fields.Numeric(0, coreCount - 1, coreVal, 55);
            _slotPickers[i].Anchor = AnchorStyles.Left;
            _slotPickers[i].Margin = new Padding(0, 2, 6, 3);
            _tooltip.SetToolTip(_slotPickers[i], $"CPU core for EQ thread {i + 1} (CPUAffinity{i} in eqclient.ini)");
            coreGrid.Controls.Add(lbl, col * 2, row);
            coreGrid.Controls.Add(_slotPickers[i], col * 2 + 1, row);
        }
        cardCores.Full(coreGrid);
        cardCores.Hint($"{coreCount} cores detected  ·  shared by all EQ clients  ·  core 0 = first");

        // ─── Card 4: FPS Limits ──────────────────────────────────────────
        var cardFps = stack.NewCard("🎮", "FPS Limits", DarkTheme.CardGreen);
        _nudMaxFPS = Fields.Numeric(10, 99, FpsForDisplay(_config.EQClientIni.MaxFPS), 55);
        _nudMaxFPS.Increment = 5;
        _nudMaxFPS.ReadOnly = true;   // spinner-only: stays on the 10-99 step-5 grid
        _nudMaxBGFPS = Fields.Numeric(10, 99, FpsForDisplay(_config.EQClientIni.MaxBGFPS), 55);
        _nudMaxBGFPS.Increment = 5;
        _nudMaxBGFPS.ReadOnly = true;
        cardFps.FlowRow("Active FPS:", _nudMaxFPS, Inline("Background FPS:"), _nudMaxBGFPS);

        // Ghost line: current eqclient.ini values (dim label + green value, monospace) — built as
        // tight adjacent segments so it reads as continuous text at any DPI.
        var (iniFps, iniBgFps) = ReadIniFpsValues();
        _ghostFont = new Font("Consolas", 7.5f, FontStyle.Italic);
        Label GhostSeg(string text, Color color) => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = color,
            Font = _ghostFont,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        var ghostFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        ghostFlow.Controls.Add(GhostSeg("MaxFPS=", DarkTheme.FgDimGray));
        ghostFlow.Controls.Add(GhostSeg(iniFps, DarkTheme.CardGreen));
        ghostFlow.Controls.Add(GhostSeg("   MaxBGFPS=", DarkTheme.FgDimGray));
        ghostFlow.Controls.Add(GhostSeg(iniBgFps, DarkTheme.CardGreen));
        cardFps.FlowRow("Current ini:", ghostFlow);

        // Bold the consequence: opening this page and clicking Save rewrites the user's local
        // eqclient.ini, so make that loud.
        _boldHintFont = new Font(DarkTheme.FontUI75, FontStyle.Bold);
        var warnFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        warnFlow.Controls.Add(new Label { Text = "Default 80.  ", AutoSize = true, ForeColor = DarkTheme.FgDimGray, Font = DarkTheme.FontUI75, Margin = Padding.Empty });
        warnFlow.Controls.Add(new Label { Text = "Written to eqclient.ini on Save.", AutoSize = true, ForeColor = DarkTheme.FgGray, Font = _boldHintFont, Margin = Padding.Empty });
        cardFps.FlowRow("", warnFlow);

        // ─── Footer (Save / Apply / Reset / Close) — docked bottom, always visible ───
        var btnSave = Fields.Primary("Save");
        btnSave.Click += (_, _) =>
        {
            ApplyAllSettings();
            ConfigManager.Save(_config);
            ConfigManager.FlushSave();
            Close();
        };
        var btnApply = Fields.Button("Apply");
        btnApply.Click += (_, _) =>
        {
            ApplyAllSettings();
            ConfigManager.Save(_config);
            ConfigManager.FlushSave();
            RefreshList();
        };
        var btnReset = Fields.Button("Reset");
        btnReset.Click += (_, _) => ResetToInitial();
        var btnClose = Fields.Button("Close");
        btnClose.Click += (_, _) => Close();

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.BgDark,
            Padding = new Padding(12, 6, 12, 8),
        };
        footer.Controls.Add(Bars.Split(
            new Control[] { btnSave, btnApply, btnReset },
            new Control[] { btnClose }));
        Controls.Add(footer);

        // ─── Refresh timer ───────────────────────────────────────────────
        _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMs };
        _refreshTimer.Tick += (_, _) => RefreshList();
        _refreshTimer.Start();
        FormClosed += (_, _) =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        };

        // ─── DPI sizing (layout-container rebuild) ───────────────────────
        // Everything else is font-driven AutoSize → correct by construction. Two things the
        // framework won't derive: fixed-width fit numerics/combo (SizeFitFields, once) and the
        // DataGridView's Height (a Bounds dim AutoScale leaves alone → ×f manually). Then fit the
        // window to its content width+height, clamped to the work area (the stack scrolls the rest).
        Load += (_, _) =>
        {
            DpiScale.SizeFitFields(this);
            double f = DeviceDpi / 96.0;
            var wa = Screen.FromControl(this).WorkingArea;
            if (f > 1.001)
                _grid.Height = (int)Math.Round(_grid.Height * f);

            var inner = stack.Host.Controls.Count > 0 ? stack.Host.Controls[0] : stack.Host;
            int width = Math.Min(
                Math.Max((int)Math.Round(520 * f), inner.PreferredSize.Width + LogicalToDeviceUnits(28)),
                wa.Width);
            ClientSize = new Size(width, ClientSize.Height);   // set width first so PreferredSize reflects it
            int contentH = inner.PreferredSize.Height + footer.Height + LogicalToDeviceUnits(6);
            ClientSize = new Size(width, Math.Min(contentH, wa.Height - LogicalToDeviceUnits(48)));

            if (!_lastLocation.HasValue)
                Location = new Point(
                    wa.Left + Math.Max(0, (wa.Width - Width) / 2),
                    wa.Top + Math.Max(0, (wa.Height - Height) / 2));

            RefreshList();
        };
    }

    private DataGridView BuildProcessGrid()
    {
        var grid = new DataGridView
        {
            // No absolute Location/Size — the card docks it (Full → Dock.Top) full-width; its Height
            // is the one Bounds dim the rebuild can't font-derive, so it's a base value ×f'd in Load.
            // No explicit cell Font / RowTemplate.Height / ColumnHeadersHeight: cells inherit the
            // (AutoScale-grown) grid font and rows/header auto-size to it → DPI-correct. Columns use
            // AllCells (DPI-independent header+content fit); the Character name column Fills the slack.
            Height = 130,   // ~header + 4 rows @96; ×f in Load
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            EditMode = DataGridViewEditMode.EditOnEnter,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = DarkTheme.Border,
            BackgroundColor = DarkTheme.BgDark,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = DarkTheme.BgDark,
                ForeColor = DarkTheme.FgWhite,
                SelectionBackColor = DarkTheme.GridSelection,
                SelectionForeColor = DarkTheme.FgWhite,
                Padding = new Padding(6, 3, 6, 3),
            },
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = DarkTheme.BgPanel,
                ForeColor = DarkTheme.FgWhite,
                SelectionBackColor = DarkTheme.GridSelection,
                SelectionForeColor = DarkTheme.FgWhite,
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = DarkTheme.BgInput,
                ForeColor = DarkTheme.CardCyan,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
            },
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            EnableHeadersVisualStyles = false,
            ScrollBars = ScrollBars.Vertical,
        };

        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Slot", HeaderText = "#", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PID", HeaderText = "PID", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Character", HeaderText = "Character", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Priority", HeaderText = "Priority", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Affinity", HeaderText = "Affinity", ReadOnly = true });
        grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Kill",
            HeaderText = "Kill",
            Text = "✖",
            UseColumnTextForButtonValue = true,
            FlatStyle = FlatStyle.Flat,
        });

        // DPI-independent column sizing: fixed columns fit header+content at any scale; the Character
        // name column Fills the remaining width so the grid spans the card. Do NOT hand-scale widths.
        foreach (DataGridViewColumn col in grid.Columns)
        {
            col.SortMode = DataGridViewColumnSortMode.NotSortable;
            col.Resizable = DataGridViewTriState.False;
            col.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        }
        grid.Columns["Character"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        grid.Columns["Character"]!.MinimumWidth = 120;

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
                // v3.22.68: use EQClient.DisplayName instead of hardcoded
                // "Client N". Resolves to the actual character once the EQ
                // title carries "EverQuest - <Name>", then to the autologin
                // BoundCharacterName pre-charselect, finally to the placeholder.
                var name = client.DisplayName;

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

            _grid.ClearSelection();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>
    /// Maps a stored FPS value onto the Process Manager's displayable 10-99 range.
    /// A stored 0 (legacy "don't set / leave eqclient.ini alone") falls back to the
    /// 80 default — never the 10 floor — so a prior "don't set" config doesn't
    /// silently become an unplayable 10 FPS; other out-of-range values clamp into
    /// 10-99. The "leave eqclient.ini alone" (0 = don't set) escape hatch still
    /// lives in the advanced EQ Client Settings form.
    /// </summary>
    private static int FpsForDisplay(int stored) => stored <= 0 ? 80 : Math.Clamp(stored, 10, 99);

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
        _config.Affinity.LaunchRetryDelayMs = (int)(_nudLaunchRetryDelay.Value * 1000m);

        // Core Assignment — write slot values to config
        for (int i = 0; i < 6; i++)
            _config.EQClientIni.CPUAffinitySlots[i] = (int)_slotPickers[i].Value;

        // Card 4: FPS
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
        _nudMaxFPS.Value = FpsForDisplay(_initialMaxFPS);
        _nudMaxBGFPS.Value = FpsForDisplay(_initialMaxBGFPS);
    }

    /// <summary>A plain inline label for FlowRow sentences (FlowRow re-sets its Margin + Anchor).</summary>
    private static Label Inline(string text) => new()
    { Text = text, AutoSize = true, ForeColor = DarkTheme.FgGray, Font = DarkTheme.FontUI9 };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
            _tooltip?.Dispose();
            // Dispose GDI Font handles — WinForms doesn't own control fonts.
            // _ghostFont/_boldHintFont are field-owned and SHARED across several labels, so dispose
            // them once here (the tree walk below would otherwise hit them once per sharing label).
            // Single-owner control fonts (the Enable checkbox + status label fonts) are intentionally
            // NOT disposed here — DarkTheme.DisposeControlFonts(this) walks the control tree and
            // disposes each non-shared Control.Font exactly once (it skips DarkTheme's shared statics).
            // DataGridViewCellStyle.Font is NOT a Control.Font, so the grid's cell-style fonts are
            // unreachable by that walk and must be disposed directly.
            _ghostFont?.Dispose();
            _boldHintFont?.Dispose();
            _grid?.DefaultCellStyle?.Font?.Dispose();
            _grid?.ColumnHeadersDefaultCellStyle?.Font?.Dispose();
            DarkTheme.DisposeControlFonts(this);
        }
        base.Dispose(disposing);
    }
}
