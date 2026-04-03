using System.Text;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Reads and writes the [VideoMode] section of eqclient.ini.
/// Provides resolution presets, window offset controls, and windowed mode toggle.
/// </summary>
public class VideoSettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly string _iniPath;
    private bool _suppressSync; // prevent SyncPresetToCustom during programmatic changes

    private ComboBox _cboPreset = null!;
    private NumericUpDown _nudWidth = null!;
    private NumericUpDown _nudHeight = null!;
    private NumericUpDown _nudOffsetX = null!;
    private NumericUpDown _nudOffsetY = null!;
    private CheckBox _chkWindowed = null!;
    private CheckBox _chkMultiMon = null!;
    private ComboBox _cboPrimaryMon = null!;
    private ComboBox _cboSecondaryMon = null!;
    private NumericUpDown _nudTopOffset = null!;

    // Resolution presets
    private static readonly (string Name, int W, int H)[] Presets =
    {
        ("1920x1080", 1920, 1080),
        ("1920x1200", 1920, 1200),
        ("1920x1020 (above taskbar)", 1920, 1020),
        ("2560x1440", 2560, 1440),
        ("3840x2160 (4K)", 3840, 2160),
        ("1280x720", 1280, 720),
        ("1600x900", 1600, 900),
        ("1366x768", 1366, 768),
        ("Custom", 0, 0)
    };

    public VideoSettingsForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Video Settings", new Size(480, 490));

        int y = 12;
        const int L = 15, col2 = 245;

        // ─── Page description ───────────────────────────────────
        var lblDesc = new Label
        {
            Text = "EQ's in-game resolution (eqclient.ini). Use a preset or set custom dimensions.",
            Location = new Point(L, y),
            AutoSize = true,
            ForeColor = DarkTheme.FgDimGray,
            Font = new Font("Segoe UI", 8f)
        };
        Controls.Add(lblDesc);
        y += 22;

        // ─── Row 1: Preset + Windowed checkbox ──────────────────
        AddLabel("Preset:", L, y + 2);
        _cboPreset = new ComboBox
        {
            Location = new Point(65, y),
            Size = new Size(150, 25),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat
        };
        PopulatePresets();
        _cboPreset.SelectedIndexChanged += CboPreset_SelectedIndexChanged;
        Controls.Add(_cboPreset);

        _chkWindowed = new CheckBox
        {
            Text = "Windowed Mode",
            Location = new Point(col2, y + 2),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.ForceWindowedMode
        };
        Controls.Add(_chkWindowed);

        // ─── Row 2: Width / Height / Offsets (all on one row) ───
        y += 32;
        AddLabel("Width:", L, y + 2);
        _nudWidth = AddNumeric(65, y, 70, 1920, 320, 7680);
        _nudWidth.ValueChanged += (_, _) => SyncPresetToCustom();

        AddLabel("Height:", 145, y + 2);
        _nudHeight = AddNumeric(195, y, 70, 1080, 200, 4320);
        _nudHeight.ValueChanged += (_, _) => SyncPresetToCustom();

        // ─── Row 3: All three offsets on one line ───────────────
        y += 32;
        AddLabel("Offset X:", L, y + 2);
        _nudOffsetX = AddNumeric(80, y, 55, 0, -5000, 5000);

        AddLabel("Y:", 145, y + 2);
        _nudOffsetY = AddNumeric(162, y, 55, 0, -5000, 5000);

        AddLabel("Top:", 230, y + 2);
        _nudTopOffset = AddNumeric(260, y, 55, _config.Layout.TopOffset, -100, 200);
        DarkTheme.AddHint(this, "px down from top edge", 320, y + 4);

        // ─── Row 4: Multi-Monitor checkbox ──────────────────────
        y += 38;
        _chkMultiMon = new CheckBox
        {
            Text = "Multi-Monitor Mode",
            Location = new Point(L, y),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase)
        };
        Controls.Add(_chkMultiMon);

        // ─── Monitor Selection ─────────────────────────────────
        y += 26;
        var lblMonSel = new Label
        {
            Text = "Monitor Selection",
            Location = new Point(L, y),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        Controls.Add(lblMonSel);
        y += 22;
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ToArray();
        var monItems = new string[screens.Length];
        for (int i = 0; i < screens.Length; i++)
        {
            var primary = screens[i].Primary ? " (primary)" : "";
            monItems[i] = $"{i + 1}: {screens[i].Bounds.Width}x{screens[i].Bounds.Height}{primary}";
        }

        AddLabel("Primary:", L + 10, y + 2);
        _cboPrimaryMon = new ComboBox
        {
            Location = new Point(95, y), Size = new Size(200, 25),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat
        };
        _cboPrimaryMon.Items.AddRange(monItems);
        _cboPrimaryMon.SelectedIndex = Math.Clamp(_config.Layout.TargetMonitor, 0, screens.Length - 1);
        Controls.Add(_cboPrimaryMon);
        DarkTheme.WrapWithBorder(_cboPrimaryMon);

        y += 28;
        AddLabel("Secondary:", L + 10, y + 2);
        var secItems = new string[screens.Length + 1];
        secItems[0] = "Auto (first non-primary)";
        for (int i = 0; i < monItems.Length; i++) secItems[i + 1] = monItems[i];
        _cboSecondaryMon = new ComboBox
        {
            Location = new Point(95, y), Size = new Size(200, 25),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat
        };
        _cboSecondaryMon.Items.AddRange(secItems);
        var secIdx = _config.Layout.SecondaryMonitor < 0 ? 0 : _config.Layout.SecondaryMonitor + 1;
        _cboSecondaryMon.SelectedIndex = Math.Clamp(secIdx, 0, secItems.Length - 1);
        Controls.Add(_cboSecondaryMon);
        DarkTheme.WrapWithBorder(_cboSecondaryMon);

        // ─── Hint + Buttons ─────────────────────────────────────
        y += 35;
        DarkTheme.AddHint(this, "Changes require EQ restart to take effect.", L, y);

        // Row 1: Backup + Restore
        y += 22;
        var btnBackup = DarkTheme.MakeButton("\uD83D\uDCBE Backup", DarkTheme.BgMedium, 130, y);
        btnBackup.Click += (_, _) => BackupIni();

        var btnRestore = DarkTheme.MakeButton("\uD83D\uDCC2 Restore", DarkTheme.BgMedium, 250, y);
        btnRestore.Click += (_, _) => RestoreIni();

        // Row 2: Reset, Save, Apply, Cancel
        y += 36;
        var btnReset = DarkTheme.MakeButton("\uD83D\uDD04 Reset", DarkTheme.BgMedium, 30, y);
        btnReset.Click += (_, _) => ResetDefaults();

        var btnSave = DarkTheme.MakePrimaryButton("Save", 140, y);
        btnSave.Click += (_, _) => { SaveToIni(); Close(); };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 250, y);
        btnApply.Click += (_, _) => { SaveToIni(); };

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 360, y);
        btnCancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { btnBackup, btnRestore, btnReset, btnSave, btnApply, btnCancel });
    }

    /// <summary>
    /// When user manually changes width/height, switch preset dropdown to "Custom".
    /// </summary>
    private void SyncPresetToCustom()
    {
        if (_suppressSync) return;

        int w = (int)_nudWidth.Value;
        int h = (int)_nudHeight.Value;

        // Check if current values match any preset
        foreach (var p in Presets)
        {
            if (p.W == w && p.H == h) return; // matches a preset, don't change dropdown
        }

        // Check custom presets in the dropdown
        for (int i = 0; i < _cboPreset.Items.Count; i++)
        {
            string? item = _cboPreset.Items[i]?.ToString();
            if (item == $"{w}x{h}") { _cboPreset.SelectedIndex = i; return; }
        }

        // No match — select "Custom"
        for (int i = 0; i < _cboPreset.Items.Count; i++)
        {
            if (_cboPreset.Items[i]?.ToString() == "Custom") { _cboPreset.SelectedIndex = i; return; }
        }
    }

    private void LoadFromIni()
    {
        if (!File.Exists(_iniPath))
        {
            FileLogger.Info($"VideoSettings: eqclient.ini not found at {_iniPath}");
            _cboPreset.SelectedIndex = 0;
            return;
        }

        _suppressSync = true;
        try
        {
            var lines = File.ReadAllLines(_iniPath, Encoding.Default);
            string currentSection = "";

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("["))
                {
                    currentSection = trimmed;
                    continue;
                }

                var parts = trimmed.Split('=', 2);
                if (parts.Length != 2) continue;

                string key = parts[0].Trim();
                string val = parts[1].Trim();

                if (currentSection.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "width":
                            if (int.TryParse(val, out int w)) _nudWidth.Value = Math.Clamp(w, 320, 7680);
                            break;
                        case "height":
                            if (int.TryParse(val, out int h)) _nudHeight.Value = Math.Clamp(h, 200, 4320);
                            break;
                        case "windowedmode":
                            _chkWindowed.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "xoffset":
                            if (int.TryParse(val, out int ox)) _nudOffsetX.Value = ox;
                            break;
                        case "yoffset":
                            if (int.TryParse(val, out int oy)) _nudOffsetY.Value = oy;
                            break;
                    }
                }
                else if (currentSection.Equals("[Defaults]", StringComparison.OrdinalIgnoreCase))
                {
                    // Log toggle moved to EQ Client Settings form
                }
            }

            // Match to preset (built-in or custom)
            int width = (int)_nudWidth.Value;
            int height = (int)_nudHeight.Value;
            int presetIdx = Array.FindIndex(Presets, p => p.W == width && p.H == height);
            if (presetIdx >= 0)
            {
                _cboPreset.SelectedIndex = presetIdx;
            }
            else
            {
                // Check custom presets
                string customKey = $"{width}x{height}";
                int customIdx = _cboPreset.Items.IndexOf(customKey);
                _cboPreset.SelectedIndex = customIdx >= 0 ? customIdx : _cboPreset.Items.Count - 1;
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error("VideoSettings: load error", ex);
            _cboPreset.SelectedIndex = 0;
        }
        finally { _suppressSync = false; }
    }

    /// <summary>
    /// Reset form controls to safe EQ defaults (matches AHK version ResetVMDefaults).
    /// Does not write to disk — user must click Save or Apply to persist.
    /// </summary>
    private void ResetDefaults()
    {
        _cboPreset.SelectedIndex = 0; // "1920x1080 (Full HD)" — triggers width/height update via event
        _nudOffsetX.Value = 0;
        _nudOffsetY.Value = 0;
        _chkWindowed.Checked = true;
        _chkMultiMon.Checked = false;
        _nudTopOffset.Value = 0;
    }

    private void BackupIni()
    {
        if (!File.Exists(_iniPath))
        {
            MessageBox.Show("eqclient.ini not found.", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            // Find next available .bak number
            int bakNum = 1;
            while (File.Exists($"{_iniPath}.bak{bakNum}") && bakNum < 99)
                bakNum++;

            string bakPath = $"{_iniPath}.bak{bakNum}";
            File.Copy(_iniPath, bakPath, overwrite: false);
            FileLogger.Info($"VideoSettings: backed up eqclient.ini → {Path.GetFileName(bakPath)}");
            MessageBox.Show($"Backed up to:\n{Path.GetFileName(bakPath)}", "Backup Created",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            FileLogger.Error("VideoSettings: backup error", ex);
            MessageBox.Show($"Backup failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestoreIni()
    {
        var dir = Path.GetDirectoryName(_iniPath) ?? ".";
        using var dlg = new OpenFileDialog
        {
            Title = "Restore eqclient.ini from Backup",
            Filter = "Backup Files (*.bak*)|*.bak*|All Files (*.*)|*.*",
            InitialDirectory = dir,
            FileName = ""
        };

        if (dlg.ShowDialog() != DialogResult.OK)
            return;

        try
        {
            File.Copy(dlg.FileName, _iniPath, overwrite: true);
            FileLogger.Info($"VideoSettings: restored eqclient.ini from {Path.GetFileName(dlg.FileName)}");

            // Reload form values from restored file
            LoadFromIni();
            MessageBox.Show($"Restored from:\n{Path.GetFileName(dlg.FileName)}", "Restore Complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            FileLogger.Error("VideoSettings: restore error", ex);
            MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveToIni()
    {
        try
        {
            // Save TopOffset, log toggle, and custom preset to config
            _config.Layout.TopOffset = (int)_nudTopOffset.Value;
            _config.Layout.TargetMonitor = _cboPrimaryMon.SelectedIndex;
            _config.Layout.SecondaryMonitor = _cboSecondaryMon.SelectedIndex <= 0 ? -1 : _cboSecondaryMon.SelectedIndex - 1;
            _config.EQClientIni.ForceWindowedMode = _chkWindowed.Checked;
            _config.Layout.Mode = _chkMultiMon.Checked ? "multimonitor" : "single";
            // Once enabled, permanently unlock the Alt+M hotkey
            if (_chkMultiMon.Checked)
                _config.Hotkeys.MultiMonitorEnabled = true;
            SaveCustomPreset();
            ConfigManager.Save(_config);

            if (!File.Exists(_iniPath))
            {
                FileLogger.Info($"VideoSettings: cannot save — {_iniPath} not found");
                return;
            }

            var lines = File.ReadAllLines(_iniPath, Encoding.Default).ToList();
            int sectionStart = -1;
            int sectionEnd = lines.Count;

            // Find [VideoMode] section bounds
            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase))
                {
                    sectionStart = i;
                }
                else if (sectionStart >= 0 && trimmed.StartsWith("["))
                {
                    sectionEnd = i;
                    break;
                }
            }

            // Update or insert values
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Width"] = ((int)_nudWidth.Value).ToString(),
                ["Height"] = ((int)_nudHeight.Value).ToString(),
                ["WindowedMode"] = _chkWindowed.Checked ? "TRUE" : "FALSE",
                ["Maximized"] = _config.EQClientIni.MaximizeWindow ? "1" : "0",
                ["XOffset"] = ((int)_nudOffsetX.Value).ToString(),
                ["YOffset"] = ((int)_nudOffsetY.Value).ToString()
            };

            if (sectionStart >= 0)
            {
                // Update existing keys in section
                for (int i = sectionStart + 1; i < sectionEnd; i++)
                {
                    var parts = lines[i].Split('=', 2);
                    if (parts.Length == 2 && settings.ContainsKey(parts[0].Trim()))
                    {
                        lines[i] = $"{parts[0].Trim()}={settings[parts[0].Trim()]}";
                        settings.Remove(parts[0].Trim());
                    }
                }

                // Insert any remaining keys before sectionEnd
                foreach (var kv in settings)
                    lines.Insert(sectionEnd, $"{kv.Key}={kv.Value}");
            }
            else
            {
                // Append new section
                lines.Add("");
                lines.Add("[VideoMode]");
                foreach (var kv in settings)
                    lines.Add($"{kv.Key}={kv.Value}");
            }

            // Also update WindowedMode in [Defaults] — EQ reads from there,
            // not [VideoMode]. Both must stay in sync.
            string wmVal = _chkWindowed.Checked ? "TRUE" : "FALSE";
            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("WindowedMode=", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("WindowedModeX", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("WindowedModeY", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"WindowedMode={wmVal}";
                }
            }

            WriteWithRetry(_iniPath, lines);
            FileLogger.Info("VideoSettings: saved to eqclient.ini");
        }
        catch (Exception ex)
        {
            FileLogger.Error("VideoSettings: save error", ex);
            MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Update or create the Log= key in the [Defaults] section of eqclient.ini.
    /// </summary>
    // UpdateDefaultsSection removed — Log toggle moved to EQClientSettingsForm

    /// <summary>
    /// Write file with retry — EQ may hold a lock on eqclient.ini briefly.
    /// </summary>
    private static void WriteWithRetry(string path, List<string> lines, int maxRetries = 2, int delayMs = 500)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                File.WriteAllLines(path, lines, Encoding.Default);
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                FileLogger.Warn($"VideoSettings: file locked, retry {attempt + 1}/{maxRetries}");
                Thread.Sleep(delayMs);
            }
        }
    }

    // ─── Preset Management ────────────────────────────────────────

    /// <summary>
    /// Populate combo box with built-in presets, saved custom presets, and "Custom".
    /// Custom presets that duplicate a built-in preset are skipped.
    /// </summary>
    private void PopulatePresets()
    {
        _cboPreset.Items.Clear();

        // Built-in presets (all except the "Custom" sentinel)
        for (int i = 0; i < Presets.Length - 1; i++)
            _cboPreset.Items.Add(Presets[i].Name);

        // Saved custom presets (up to 3, skip duplicates with built-ins)
        var builtInSet = new HashSet<string>(Presets.Select(p => $"{p.W}x{p.H}"));
        foreach (var custom in _config.CustomVideoPresets)
        {
            if (!builtInSet.Contains(custom))
                _cboPreset.Items.Add(custom);
        }

        // "Custom" always last
        _cboPreset.Items.Add("Custom");
    }

    private void CboPreset_SelectedIndexChanged(object? sender, EventArgs e)
    {
        string? selected = _cboPreset.SelectedItem?.ToString();
        if (selected == null || selected == "Custom") return;

        _suppressSync = true;
        try
        {
            // Check built-in presets first
            var preset = Array.Find(Presets, p => p.Name == selected);
            if (preset.W > 0)
            {
                _nudWidth.Value = preset.W;
                _nudHeight.Value = preset.H;
                return;
            }

            // Custom preset format: "WxH"
            var dims = selected.Split('x');
            if (dims.Length == 2 && int.TryParse(dims[0], out int w) && int.TryParse(dims[1], out int h))
            {
                _nudWidth.Value = Math.Clamp(w, 320, 7680);
                _nudHeight.Value = Math.Clamp(h, 200, 4320);
            }
        }
        finally { _suppressSync = false; }
    }

    /// <summary>
    /// Save the current resolution as a custom preset if it doesn't match any built-in preset.
    /// Keeps max 3 custom presets (FIFO — oldest dropped when full).
    /// </summary>
    private void SaveCustomPreset()
    {
        int w = (int)_nudWidth.Value;
        int h = (int)_nudHeight.Value;
        string key = $"{w}x{h}";

        // Skip if matches a built-in preset
        if (Array.Exists(Presets, p => p.W == w && p.H == h))
            return;

        // Skip if already saved
        if (_config.CustomVideoPresets.Contains(key))
            return;

        _config.CustomVideoPresets.Add(key);

        // Keep max 3 — drop oldest
        while (_config.CustomVideoPresets.Count > 3)
            _config.CustomVideoPresets.RemoveAt(0);
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private void AddLabel(string text, int x, int y)
    {
        DarkTheme.AddLabel(this, text, x, y);
    }

    private NumericUpDown AddNumeric(int x, int y, int width, decimal defaultVal, decimal min, decimal max)
    {
        return DarkTheme.AddNumeric(this, x, y, width, defaultVal, min, max);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }
}
