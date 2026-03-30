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

    // Use shared DarkTheme palette

    private ComboBox _cboPreset = null!;
    private NumericUpDown _nudWidth = null!;
    private NumericUpDown _nudHeight = null!;
    private NumericUpDown _nudOffsetX = null!;
    private NumericUpDown _nudOffsetY = null!;
    private CheckBox _chkWindowed = null!;
    private CheckBox _chkDisableLog = null!;
    private NumericUpDown _nudTopOffset = null!;

    // Resolution presets
    private static readonly (string Name, int W, int H)[] Presets =
    {
        ("1920x1080 (Full HD)", 1920, 1080),
        ("1920x1200 (WUXGA)", 1920, 1200),
        ("1920x1020", 1920, 1020),
        ("2560x1440 (QHD)", 2560, 1440),
        ("3840x2160 (4K)", 3840, 2160),
        ("1280x720 (HD)", 1280, 720),
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
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Video Settings", new Size(460, 440));

        int y = 15;

        AddLabel("Resolution Preset:", 15, y);
        _cboPreset = new ComboBox
        {
            Location = new Point(15, y += 22),
            Size = new Size(250, 25),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat
        };
        PopulatePresets();
        _cboPreset.SelectedIndexChanged += CboPreset_SelectedIndexChanged;
        Controls.Add(_cboPreset);

        AddLabel("Width:", 15, y += 35);
        _nudWidth = AddNumeric(15, y += 22, 80, 1920, 320, 7680);

        AddLabel("Height:", 110, y - 22);
        _nudHeight = AddNumeric(110, y, 80, 1080, 200, 4320);

        AddLabel("Window Offset X:", 15, y += 45);
        _nudOffsetX = AddNumeric(15, y += 22, 60, 0, -5000, 5000);

        AddLabel("Window Offset Y:", 130, y - 22);
        _nudOffsetY = AddNumeric(130, y, 60, 0, -5000, 5000);

        _chkWindowed = new CheckBox
        {
            Text = "Windowed Mode",
            Location = new Point(15, y += 40),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.ForceWindowedMode
        };
        Controls.Add(_chkWindowed);

        _chkDisableLog = new CheckBox
        {
            Text = "Disable EQ Logging (Log=FALSE)",
            Location = new Point(15, y += 25),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.DisableEQLog
        };
        Controls.Add(_chkDisableLog);

        AddLabel("Title Bar Offset (FIX_TOP_OFFSET):", 15, y += 35);
        _nudTopOffset = AddNumeric(15, y += 22, 60, _config.Layout.TopOffset, -100, 200);

        // Hint
        DarkTheme.AddHint(this, "Changes require EQ restart to take effect.", 15, y += 40);

        // Buttons
        y += 20;
        var btnBackup = DarkTheme.MakeButton("\uD83D\uDCBE Backup", DarkTheme.BgMedium, 15, y);
        btnBackup.Click += (_, _) => BackupIni();

        var btnReset = DarkTheme.MakeButton("\uD83D\uDD04 Reset", DarkTheme.BgMedium, 100, y);
        btnReset.Click += (_, _) => ResetDefaults();

        var btnSave = DarkTheme.MakePrimaryButton("Save", 185, y);
        btnSave.Click += (_, _) => { SaveToIni(); Close(); };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 270, y);
        btnApply.Click += (_, _) => { SaveToIni(); };

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 355, y);
        btnCancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { btnBackup, btnReset, btnSave, btnApply, btnCancel });
    }

    private void LoadFromIni()
    {
        if (!File.Exists(_iniPath))
        {
            FileLogger.Info($"VideoSettings: eqclient.ini not found at {_iniPath}");
            _cboPreset.SelectedIndex = 0;
            return;
        }

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
                    if (key.Equals("Log", StringComparison.OrdinalIgnoreCase))
                        _chkDisableLog.Checked = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
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
        _chkDisableLog.Checked = false;
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

    private void SaveToIni()
    {
        try
        {
            // Save TopOffset, log toggle, and custom preset to config
            _config.Layout.TopOffset = (int)_nudTopOffset.Value;
            _config.EQClientIni.ForceWindowedMode = _chkWindowed.Checked;
            _config.DisableEQLog = _chkDisableLog.Checked;
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
                ["Maximized"] = "0",
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

            // Update [Defaults] section — set Log=TRUE/FALSE
            UpdateDefaultsSection(lines);

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
    private void UpdateDefaultsSection(List<string> lines)
    {
        string logValue = _chkDisableLog.Checked ? "FALSE" : "TRUE";
        int sectionStart = -1;
        int sectionEnd = lines.Count;

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Equals("[Defaults]", StringComparison.OrdinalIgnoreCase))
                sectionStart = i;
            else if (sectionStart >= 0 && trimmed.StartsWith("["))
            {
                sectionEnd = i;
                break;
            }
        }

        if (sectionStart >= 0)
        {
            // Look for existing Log= key
            bool found = false;
            for (int i = sectionStart + 1; i < sectionEnd; i++)
            {
                var parts = lines[i].Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim().Equals("Log", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"Log={logValue}";
                    found = true;
                    break;
                }
            }
            if (!found)
                lines.Insert(sectionEnd, $"Log={logValue}");
        }
        else
        {
            // Append [Defaults] section
            lines.Add("");
            lines.Add("[Defaults]");
            lines.Add($"Log={logValue}");
        }
    }

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
}
