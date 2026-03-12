using System.Diagnostics;
using System.Text.Json;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Settings GUI with tabbed layout. Dark theme matching EQSwitch aesthetic.
/// Replaces AHK's vertically-stacked settings panel with a proper TabControl.
/// </summary>
public class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly Action<AppConfig> _onApply;

    // Dark theme colors
    private static readonly Color BgDark = Color.FromArgb(30, 30, 30);
    private static readonly Color BgMedium = Color.FromArgb(45, 45, 45);
    private static readonly Color BgInput = Color.FromArgb(50, 50, 50);
    private static readonly Color FgWhite = Color.White;
    private static readonly Color FgGray = Color.FromArgb(180, 180, 180);
    private static readonly Color AccentGreen = Color.FromArgb(0, 120, 80);

    // ─── General tab controls
    private TextBox _txtEQPath = null!;
    private TextBox _txtExeName = null!;
    private TextBox _txtArgs = null!;
    private TextBox _txtProcessName = null!;
    private NumericUpDown _nudPollingInterval = null!;

    // ─── Hotkeys tab controls
    private TextBox _txtSwitchKey = null!;
    private TextBox _txtGlobalSwitchKey = null!;
    private TextBox _txtArrangeWindows = null!;
    private TextBox _txtToggleMultiMon = null!;
    private TextBox _txtLaunchOne = null!;
    private TextBox _txtLaunchAll = null!;
    private CheckBox _chkMultiMonEnabled = null!;

    // ─── Layout tab controls
    private NumericUpDown _nudColumns = null!;
    private NumericUpDown _nudRows = null!;
    private NumericUpDown _nudTargetMonitor = null!;
    private NumericUpDown _nudTopOffset = null!;
    private ComboBox _cboLayoutMode = null!;
    private CheckBox _chkRemoveTitleBars = null!;

    // ─── Affinity tab controls
    private TextBox _txtActiveMask = null!;
    private TextBox _txtBackgroundMask = null!;
    private ComboBox _cboActivePriority = null!;
    private ComboBox _cboBackgroundPriority = null!;
    private CheckBox _chkAffinityEnabled = null!;
    private NumericUpDown _nudRetryCount = null!;
    private NumericUpDown _nudRetryDelay = null!;

    // ─── Launch tab controls
    private NumericUpDown _nudNumClients = null!;
    private NumericUpDown _nudLaunchDelay = null!;
    private NumericUpDown _nudFixDelay = null!;

    // ─── Paths tab controls
    private TextBox _txtGinaPath = null!;
    private TextBox _txtNotesPath = null!;

    // ─── PiP tab controls
    private CheckBox _chkPipEnabled = null!;
    private ComboBox _cboPipSize = null!;
    private NumericUpDown _nudPipWidth = null!;
    private NumericUpDown _nudPipHeight = null!;
    private NumericUpDown _nudPipOpacity = null!;
    private CheckBox _chkPipBorder = null!;
    private ComboBox _cboPipBorderColor = null!;
    private NumericUpDown _nudPipMaxWindows = null!;

    // ─── Characters tab controls
    private ListView _charListView = null!;
    private List<CharacterProfile> _pendingCharacters = null!;

    public SettingsForm(AppConfig config, Action<AppConfig> onApply)
    {
        _config = config;
        _pendingCharacters = new List<CharacterProfile>(config.Characters);
        _onApply = onApply;
        InitializeForm();
    }

    private void InitializeForm()
    {
        Text = "EQSwitch Settings";
        Size = new Size(500, 480);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgDark;
        ForeColor = FgWhite;
        Font = new Font("Segoe UI", 9);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(8, 4)
        };

        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildHotkeysTab());
        tabs.TabPages.Add(BuildLayoutTab());
        tabs.TabPages.Add(BuildAffinityTab());
        tabs.TabPages.Add(BuildLaunchTab());
        tabs.TabPages.Add(BuildPipTab());
        tabs.TabPages.Add(BuildPathsTab());
        tabs.TabPages.Add(BuildCharactersTab());

        // Button panel at bottom
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            BackColor = BgDark
        };

        var btnSave = MakeButton("Save", AccentGreen, 200, 10);
        btnSave.Click += (_, _) => { ApplySettings(); ConfigManager.Save(_config); Close(); };

        var btnApply = MakeButton("Apply", BgMedium, 290, 10);
        btnApply.Click += (_, _) => { ApplySettings(); ConfigManager.Save(_config); };

        var btnClose = MakeButton("Close", BgMedium, 380, 10);
        btnClose.Click += (_, _) => Close();

        buttonPanel.Controls.AddRange(new Control[] { btnSave, btnApply, btnClose });

        Controls.Add(tabs);
        Controls.Add(buttonPanel);

        PopulateFromConfig();
    }

    // ─── Tab Builders ─────────────────────────────────────────────

    private TabPage BuildGeneralTab()
    {
        var page = MakeTabPage("General");
        int y = 15;

        AddLabel(page, "EverQuest Path:", 15, y);
        _txtEQPath = AddTextBox(page, 15, y += 22, 350);
        var btnBrowse = MakeButton("Browse...", BgMedium, 375, y - 2);
        btnBrowse.Size = new Size(80, 26);
        btnBrowse.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { Description = "Select EverQuest folder", InitialDirectory = _txtEQPath.Text };
            if (fbd.ShowDialog() == DialogResult.OK) _txtEQPath.Text = fbd.SelectedPath;
        };
        page.Controls.Add(btnBrowse);

        AddLabel(page, "Executable Name:", 15, y += 35);
        _txtExeName = AddTextBox(page, 15, y += 22, 200);

        AddLabel(page, "Launch Arguments:", 230, y - 22);
        _txtArgs = AddTextBox(page, 230, y, 225);

        AddLabel(page, "Process Name (for detection):", 15, y += 35);
        _txtProcessName = AddTextBox(page, 15, y += 22, 200);

        AddLabel(page, "Polling Interval (ms):", 230, y - 22);
        _nudPollingInterval = AddNumeric(page, 230, y, 100, 500, 100, 5000);

        return page;
    }

    private TabPage BuildHotkeysTab()
    {
        var page = MakeTabPage("Hotkeys");
        int y = 15;

        AddLabel(page, "Switch Key (EQ-only, single key):", 15, y);
        _txtSwitchKey = AddTextBox(page, 15, y += 22, 120);
        AddHint(page, "e.g. \\ ] [", 145, y + 3);

        AddLabel(page, "Global Switch Key (any app, single key):", 15, y += 40);
        _txtGlobalSwitchKey = AddTextBox(page, 15, y += 22, 120);

        AddLabel(page, "Arrange Windows:", 250, 15);
        _txtArrangeWindows = AddTextBox(page, 250, 37, 120);
        AddHint(page, "e.g. Alt+G", 380, 40);

        AddLabel(page, "Toggle Multi-Monitor:", 250, 77);
        _txtToggleMultiMon = AddTextBox(page, 250, 99, 120);

        _chkMultiMonEnabled = AddCheckBox(page, "Multi-Monitor Hotkey Enabled", 250, 130);

        AddLabel(page, "Launch One:", 15, y += 40);
        _txtLaunchOne = AddTextBox(page, 15, y += 22, 120);

        AddLabel(page, "Launch All:", 250, y - 22);
        _txtLaunchAll = AddTextBox(page, 250, y, 120);

        AddHint(page, "Leave blank to disable. Format: Alt+Key, Ctrl+Key", 15, y + 35);

        return page;
    }

    private TabPage BuildLayoutTab()
    {
        var page = MakeTabPage("Layout");
        int y = 15;

        AddLabel(page, "Layout Mode:", 15, y);
        _cboLayoutMode = AddComboBox(page, 15, y += 22, 150, new[] { "single", "multimonitor" });

        AddLabel(page, "Grid Columns:", 200, 15);
        _nudColumns = AddNumeric(page, 200, 37, 80, 2, 1, 4);

        AddLabel(page, "Grid Rows:", 310, 15);
        _nudRows = AddNumeric(page, 310, 37, 80, 2, 1, 4);

        AddLabel(page, "Target Monitor (0 = primary):", 15, y += 40);
        _nudTargetMonitor = AddNumeric(page, 15, y += 22, 80, 0, 0, 8);

        AddLabel(page, "Top Offset (pixels):", 200, y - 22);
        _nudTopOffset = AddNumeric(page, 200, y, 80, 0, -100, 200);

        _chkRemoveTitleBars = AddCheckBox(page, "Remove Title Bars on Arrange", 15, y += 40);

        return page;
    }

    private TabPage BuildAffinityTab()
    {
        var page = MakeTabPage("Affinity");
        int y = 15;

        _chkAffinityEnabled = AddCheckBox(page, "Enable CPU Affinity Management", 15, y);

        AddLabel(page, "Active Client Mask (hex):", 15, y += 35);
        _txtActiveMask = AddTextBox(page, 15, y += 22, 120);
        AddHint(page, "e.g. FF (P-cores 0-7)", 145, y + 3);

        AddLabel(page, "Background Mask (hex):", 15, y += 35);
        _txtBackgroundMask = AddTextBox(page, 15, y += 22, 120);
        AddHint(page, "e.g. FF00 (E-cores 8-15)", 145, y + 3);

        var priorities = new[] { "Idle", "BelowNormal", "Normal", "AboveNormal", "High" };

        AddLabel(page, "Active Priority:", 15, y += 40);
        _cboActivePriority = AddComboBox(page, 15, y += 22, 150, priorities);

        AddLabel(page, "Background Priority:", 230, y - 22);
        _cboBackgroundPriority = AddComboBox(page, 230, y, 150, priorities);

        // All / Clear buttons for masks
        var btnAllCores = MakeButton("All Cores", BgMedium, 300, 72);
        btnAllCores.Size = new Size(80, 26);
        btnAllCores.Click += (_, _) =>
        {
            var (_, sysMask) = AffinityManager.DetectCores();
            _txtActiveMask.Text = sysMask.ToString("X");
            _txtBackgroundMask.Text = sysMask.ToString("X");
        };
        page.Controls.Add(btnAllCores);

        var btnClearCores = MakeButton("Clear", BgMedium, 390, 72);
        btnClearCores.Size = new Size(60, 26);
        btnClearCores.Click += (_, _) =>
        {
            _txtActiveMask.Text = "1";
            _txtBackgroundMask.Text = "1";
        };
        page.Controls.Add(btnClearCores);

        AddLabel(page, "Launch Retry Count:", 15, y += 40);
        _nudRetryCount = AddNumeric(page, 15, y += 22, 80, 3, 0, 10);

        AddLabel(page, "Retry Delay (ms):", 200, y - 22);
        _nudRetryDelay = AddNumeric(page, 200, y, 100, 2000, 500, 10000);

        return page;
    }

    private TabPage BuildLaunchTab()
    {
        var page = MakeTabPage("Launch");
        int y = 15;

        AddLabel(page, "Number of Clients (Launch All):", 15, y);
        _nudNumClients = AddNumeric(page, 15, y += 22, 80, 2, 1, 8);

        AddLabel(page, "Delay Between Launches (ms):", 15, y += 40);
        _nudLaunchDelay = AddNumeric(page, 15, y += 22, 100, 3000, 500, 30000);

        AddLabel(page, "Window Fix Delay (ms):", 15, y += 40);
        _nudFixDelay = AddNumeric(page, 15, y += 22, 100, 15000, 1000, 60000);
        AddHint(page, "Wait time after all clients launched before arranging windows", 125, y + 3);

        return page;
    }

    private TabPage BuildPathsTab()
    {
        var page = MakeTabPage("Paths");
        int y = 15;

        AddLabel(page, "GINA Path:", 15, y);
        _txtGinaPath = AddTextBox(page, 15, y += 22, 330);
        var btnBrowseGina = MakeButton("Browse...", BgMedium, 355, y - 2);
        btnBrowseGina.Size = new Size(80, 26);
        btnBrowseGina.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select GINA executable",
                Filter = "Executables|*.exe|All Files|*.*",
                InitialDirectory = Path.GetDirectoryName(_txtGinaPath.Text) ?? ""
            };
            if (ofd.ShowDialog() == DialogResult.OK) _txtGinaPath.Text = ofd.FileName;
        };
        page.Controls.Add(btnBrowseGina);

        AddLabel(page, "Notes File:", 15, y += 45);
        _txtNotesPath = AddTextBox(page, 15, y += 22, 330);
        var btnBrowseNotes = MakeButton("Browse...", BgMedium, 355, y - 2);
        btnBrowseNotes.Size = new Size(80, 26);
        btnBrowseNotes.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select notes file",
                Filter = "Text Files|*.txt|All Files|*.*",
                InitialDirectory = Path.GetDirectoryName(_txtNotesPath.Text) ?? ""
            };
            if (ofd.ShowDialog() == DialogResult.OK) _txtNotesPath.Text = ofd.FileName;
        };
        page.Controls.Add(btnBrowseNotes);

        AddHint(page, "Leave blank for defaults. GINA launches the app; Notes opens a text file.", 15, y + 40);

        return page;
    }

    private TabPage BuildPipTab()
    {
        var page = MakeTabPage("PiP");
        int y = 15;

        _chkPipEnabled = AddCheckBox(page, "Enable PiP Overlay", 15, y);

        AddLabel(page, "Size Preset:", 15, y += 35);
        _cboPipSize = AddComboBox(page, 15, y += 22, 150, new[] { "Small", "Medium", "Large", "XL", "XXL", "Custom" });
        _cboPipSize.SelectedIndexChanged += (_, _) =>
        {
            bool isCustom = _cboPipSize.SelectedItem?.ToString() == "Custom";
            _nudPipWidth.Enabled = isCustom;
            _nudPipHeight.Enabled = isCustom;
        };

        AddLabel(page, "Custom Width:", 200, y - 22);
        _nudPipWidth = AddNumeric(page, 200, y, 80, 320, 100, 1920);
        _nudPipWidth.Enabled = false;

        AddLabel(page, "Custom Height:", 310, y - 22);
        _nudPipHeight = AddNumeric(page, 310, y, 80, 240, 100, 1080);
        _nudPipHeight.Enabled = false;

        AddLabel(page, "Opacity (0-255):", 15, y += 40);
        _nudPipOpacity = AddNumeric(page, 15, y += 22, 80, 200, 0, 255);

        _chkPipBorder = AddCheckBox(page, "Show Border", 200, y);
        _chkPipBorder.CheckedChanged += (_, _) =>
        {
            _cboPipBorderColor.Enabled = _chkPipBorder.Checked;
        };

        AddLabel(page, "Border Color:", 15, y += 40);
        _cboPipBorderColor = AddComboBox(page, 15, y += 22, 120, new[] { "Green", "Blue", "Red", "Black" });

        AddLabel(page, "Max PiP Windows:", 200, y - 22);
        _nudPipMaxWindows = AddNumeric(page, 200, y, 60, 3, 1, 3);

        return page;
    }

    private TabPage BuildCharactersTab()
    {
        var page = MakeTabPage("Characters");
        int y = 15;

        _charListView = new ListView
        {
            Location = new Point(15, y),
            Size = new Size(440, 250),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = FgWhite,
            BorderStyle = BorderStyle.FixedSingle,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        _charListView.Columns.Add("Name", 130);
        _charListView.Columns.Add("Class", 100);
        _charListView.Columns.Add("Slot", 50);
        _charListView.Columns.Add("Affinity", 100);
        page.Controls.Add(_charListView);

        var btnExport = MakeButton("Export...", BgMedium, 15, 275);
        btnExport.Size = new Size(90, 30);
        btnExport.Click += (_, _) => ExportCharacters();
        page.Controls.Add(btnExport);

        var btnImport = MakeButton("Import...", BgMedium, 115, 275);
        btnImport.Size = new Size(90, 30);
        btnImport.Click += (_, _) => ImportCharacters();
        page.Controls.Add(btnImport);

        AddHint(page, "Export/Import character profiles as JSON files", 220, 283);

        return page;
    }

    private void ExportCharacters()
    {
        if (_pendingCharacters.Count == 0)
        {
            MessageBox.Show("No character profiles to export.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = "Export Character Profiles",
            Filter = "JSON Files|*.json",
            FileName = "eqswitch-characters.json"
        };

        if (sfd.ShowDialog() == DialogResult.OK)
        {
            try
            {
                var json = JsonSerializer.Serialize(_pendingCharacters, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(sfd.FileName, json);
                Debug.WriteLine($"Exported {_pendingCharacters.Count} characters to {sfd.FileName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Export failed: {ex.Message}");
                MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void ImportCharacters()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Import Character Profiles",
            Filter = "JSON Files|*.json"
        };

        if (ofd.ShowDialog() == DialogResult.OK)
        {
            try
            {
                var json = File.ReadAllText(ofd.FileName);
                var imported = JsonSerializer.Deserialize<List<CharacterProfile>>(json);
                if (imported != null && imported.Count > 0)
                {
                    _pendingCharacters = imported;
                    RefreshCharacterList();
                    Debug.WriteLine($"Imported {imported.Count} characters from {ofd.FileName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Import failed: {ex.Message}");
                MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void RefreshCharacterList()
    {
        _charListView.Items.Clear();
        foreach (var c in _pendingCharacters)
        {
            var item = new ListViewItem(c.Name);
            item.SubItems.Add(c.Class);
            item.SubItems.Add((c.SlotIndex + 1).ToString());
            item.SubItems.Add(c.AffinityOverride.HasValue ? $"0x{c.AffinityOverride.Value:X}" : "(default)");
            _charListView.Items.Add(item);
        }
    }

    // ─── Config I/O ───────────────────────────────────────────────

    private void PopulateFromConfig()
    {
        // General
        _txtEQPath.Text = _config.EQPath;
        _txtExeName.Text = _config.Launch.ExeName;
        _txtArgs.Text = _config.Launch.Arguments;
        _txtProcessName.Text = _config.EQProcessName;
        _nudPollingInterval.Value = Math.Clamp(_config.PollingIntervalMs, (int)_nudPollingInterval.Minimum, (int)_nudPollingInterval.Maximum);

        // Hotkeys
        _txtSwitchKey.Text = _config.Hotkeys.SwitchKey;
        _txtGlobalSwitchKey.Text = _config.Hotkeys.GlobalSwitchKey;
        _txtArrangeWindows.Text = _config.Hotkeys.ArrangeWindows;
        _txtToggleMultiMon.Text = _config.Hotkeys.ToggleMultiMonitor;
        _txtLaunchOne.Text = _config.Hotkeys.LaunchOne;
        _txtLaunchAll.Text = _config.Hotkeys.LaunchAll;
        _chkMultiMonEnabled.Checked = _config.Hotkeys.MultiMonitorEnabled;

        // Layout
        _cboLayoutMode.SelectedItem = _config.Layout.Mode;
        _nudColumns.Value = ClampNud(_nudColumns, _config.Layout.Columns);
        _nudRows.Value = ClampNud(_nudRows, _config.Layout.Rows);
        _nudTargetMonitor.Value = ClampNud(_nudTargetMonitor, _config.Layout.TargetMonitor);
        _nudTopOffset.Value = ClampNud(_nudTopOffset, _config.Layout.TopOffset);
        _chkRemoveTitleBars.Checked = _config.Layout.RemoveTitleBars;

        // Affinity
        _chkAffinityEnabled.Checked = _config.Affinity.Enabled;
        _txtActiveMask.Text = _config.Affinity.ActiveMask.ToString("X");
        _txtBackgroundMask.Text = _config.Affinity.BackgroundMask.ToString("X");
        _cboActivePriority.SelectedItem = _config.Affinity.ActivePriority;
        _cboBackgroundPriority.SelectedItem = _config.Affinity.BackgroundPriority;
        _nudRetryCount.Value = ClampNud(_nudRetryCount, _config.Affinity.LaunchRetryCount);
        _nudRetryDelay.Value = ClampNud(_nudRetryDelay, _config.Affinity.LaunchRetryDelayMs);

        // Launch
        _nudNumClients.Value = ClampNud(_nudNumClients, _config.Launch.NumClients);
        _nudLaunchDelay.Value = ClampNud(_nudLaunchDelay, _config.Launch.LaunchDelayMs);
        _nudFixDelay.Value = ClampNud(_nudFixDelay, _config.Launch.FixDelayMs);

        // Paths
        _txtGinaPath.Text = _config.GinaPath;
        _txtNotesPath.Text = _config.NotesPath;

        // PiP
        _chkPipEnabled.Checked = _config.Pip.Enabled;
        _cboPipSize.SelectedItem = _config.Pip.SizePreset;
        _nudPipWidth.Value = Math.Clamp(_config.Pip.CustomWidth, 100, 1920);
        _nudPipHeight.Value = Math.Clamp(_config.Pip.CustomHeight, 100, 1080);
        _nudPipOpacity.Value = _config.Pip.Opacity;
        _chkPipBorder.Checked = _config.Pip.ShowBorder;
        _cboPipBorderColor.SelectedItem = _config.Pip.BorderColor;
        _nudPipMaxWindows.Value = Math.Clamp(_config.Pip.MaxWindows, 1, 3);
        _nudPipWidth.Enabled = _config.Pip.SizePreset == "Custom";
        _nudPipHeight.Enabled = _config.Pip.SizePreset == "Custom";
        _cboPipBorderColor.Enabled = _config.Pip.ShowBorder;

        // Characters
        RefreshCharacterList();
    }

    private void ApplySettings()
    {
        // Build a new config from form values
        var newConfig = new AppConfig
        {
            IsFirstRun = false,
            EQPath = _txtEQPath.Text.Trim(),
            EQProcessName = _txtProcessName.Text.Trim(),
            PollingIntervalMs = (int)_nudPollingInterval.Value,
            Layout = new WindowLayout
            {
                Mode = _cboLayoutMode.SelectedItem?.ToString() ?? "single",
                Columns = (int)_nudColumns.Value,
                Rows = (int)_nudRows.Value,
                TargetMonitor = (int)_nudTargetMonitor.Value,
                TopOffset = (int)_nudTopOffset.Value,
                RemoveTitleBars = _chkRemoveTitleBars.Checked
            },
            Affinity = new AffinityConfig
            {
                Enabled = _chkAffinityEnabled.Checked,
                ActiveMask = ParseHexMask(_txtActiveMask.Text, _config.Affinity.ActiveMask),
                BackgroundMask = ParseHexMask(_txtBackgroundMask.Text, _config.Affinity.BackgroundMask),
                ActivePriority = _cboActivePriority.SelectedItem?.ToString() ?? "AboveNormal",
                BackgroundPriority = _cboBackgroundPriority.SelectedItem?.ToString() ?? "Normal",
                LaunchRetryCount = (int)_nudRetryCount.Value,
                LaunchRetryDelayMs = (int)_nudRetryDelay.Value
            },
            Hotkeys = new HotkeyConfig
            {
                SwitchKey = _txtSwitchKey.Text.Trim(),
                GlobalSwitchKey = _txtGlobalSwitchKey.Text.Trim(),
                ArrangeWindows = _txtArrangeWindows.Text.Trim(),
                ToggleMultiMonitor = _txtToggleMultiMon.Text.Trim(),
                LaunchOne = _txtLaunchOne.Text.Trim(),
                LaunchAll = _txtLaunchAll.Text.Trim(),
                MultiMonitorEnabled = _chkMultiMonEnabled.Checked,
                DirectSwitchKeys = _config.Hotkeys.DirectSwitchKeys
            },
            Launch = new LaunchConfig
            {
                ExeName = _txtExeName.Text.Trim(),
                Arguments = _txtArgs.Text.Trim(),
                NumClients = (int)_nudNumClients.Value,
                LaunchDelayMs = (int)_nudLaunchDelay.Value,
                FixDelayMs = (int)_nudFixDelay.Value
            },
            Pip = new PipConfig
            {
                Enabled = _chkPipEnabled.Checked,
                SizePreset = _cboPipSize.SelectedItem?.ToString() ?? "Medium",
                CustomWidth = (int)_nudPipWidth.Value,
                CustomHeight = (int)_nudPipHeight.Value,
                Opacity = (byte)_nudPipOpacity.Value,
                ShowBorder = _chkPipBorder.Checked,
                BorderColor = _cboPipBorderColor.SelectedItem?.ToString() ?? "Green",
                MaxWindows = (int)_nudPipMaxWindows.Value,
                SavedPositions = _config.Pip.SavedPositions // preserve existing positions
            },
            GinaPath = _txtGinaPath.Text.Trim(),
            NotesPath = _txtNotesPath.Text.Trim(),
            Characters = _pendingCharacters
        };

        _onApply(newConfig);
        Debug.WriteLine("Settings applied");
    }

    private static long ParseHexMask(string hex, long fallback)
    {
        hex = hex.Trim().TrimStart('0', 'x', 'X');
        if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long result))
            return result;
        return fallback;
    }

    private static decimal ClampNud(NumericUpDown nud, decimal value) =>
        Math.Clamp(value, nud.Minimum, nud.Maximum);

    // ─── Control Factories ────────────────────────────────────────

    private static TabPage MakeTabPage(string title)
    {
        return new TabPage(title) { BackColor = BgDark, ForeColor = FgWhite };
    }

    private static void AddLabel(Control parent, string text, int x, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgWhite
        });
    }

    private static void AddHint(Control parent, string text, int x, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgGray,
            Font = new Font("Segoe UI", 8)
        });
    }

    private static TextBox AddTextBox(Control parent, int x, int y, int width)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 25),
            BackColor = BgInput,
            ForeColor = FgWhite,
            BorderStyle = BorderStyle.FixedSingle
        };
        parent.Controls.Add(tb);
        return tb;
    }

    private static NumericUpDown AddNumeric(Control parent, int x, int y, int width, decimal defaultVal, decimal min, decimal max)
    {
        var nud = new NumericUpDown
        {
            Location = new Point(x, y),
            Size = new Size(width, 25),
            BackColor = BgInput,
            ForeColor = FgWhite,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(defaultVal, min, max),
            BorderStyle = BorderStyle.FixedSingle
        };
        parent.Controls.Add(nud);
        return nud;
    }

    private static ComboBox AddComboBox(Control parent, int x, int y, int width, string[] items)
    {
        var cb = new ComboBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 25),
            BackColor = BgInput,
            ForeColor = FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat
        };
        cb.Items.AddRange(items);
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        parent.Controls.Add(cb);
        return cb;
    }

    private static CheckBox AddCheckBox(Control parent, string text, int x, int y)
    {
        var cb = new CheckBox
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgWhite
        };
        parent.Controls.Add(cb);
        return cb;
    }

    private static Button MakeButton(string text, Color bgColor, int x, int y)
    {
        return new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(80, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = bgColor,
            ForeColor = FgWhite
        };
    }
}
