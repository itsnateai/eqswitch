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

    // Dark theme
    private static readonly Color BgDark = Color.FromArgb(30, 30, 30);
    private static readonly Color BgInput = Color.FromArgb(50, 50, 50);
    private static readonly Color FgWhite = Color.White;
    private static readonly Color FgGray = Color.FromArgb(180, 180, 180);
    private static readonly Color AccentGreen = Color.FromArgb(0, 120, 80);

    private ComboBox _cboPreset = null!;
    private NumericUpDown _nudWidth = null!;
    private NumericUpDown _nudHeight = null!;
    private NumericUpDown _nudOffsetX = null!;
    private NumericUpDown _nudOffsetY = null!;
    private CheckBox _chkWindowed = null!;
    private NumericUpDown _nudTopOffset = null!;

    // Resolution presets
    private static readonly (string Name, int W, int H)[] Presets =
    {
        ("1920x1080 (Full HD)", 1920, 1080),
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
        Text = "EQSwitch — Video Settings";
        Size = new Size(420, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgDark;
        ForeColor = FgWhite;
        Font = new Font("Segoe UI", 9);

        int y = 15;

        AddLabel("Resolution Preset:", 15, y);
        _cboPreset = new ComboBox
        {
            Location = new Point(15, y += 22),
            Size = new Size(250, 25),
            BackColor = BgInput,
            ForeColor = FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat
        };
        foreach (var p in Presets) _cboPreset.Items.Add(p.Name);
        _cboPreset.SelectedIndexChanged += (_, _) =>
        {
            int idx = _cboPreset.SelectedIndex;
            if (idx >= 0 && idx < Presets.Length - 1) // Not "Custom"
            {
                _nudWidth.Value = Presets[idx].W;
                _nudHeight.Value = Presets[idx].H;
            }
        };
        Controls.Add(_cboPreset);

        AddLabel("Width:", 15, y += 35);
        _nudWidth = AddNumeric(15, y += 22, 100, 1920, 320, 7680);

        AddLabel("Height:", 130, y - 22);
        _nudHeight = AddNumeric(130, y, 100, 1080, 200, 4320);

        AddLabel("Window Offset X:", 15, y += 45);
        _nudOffsetX = AddNumeric(15, y += 22, 100, 0, -5000, 5000);

        AddLabel("Window Offset Y:", 130, y - 22);
        _nudOffsetY = AddNumeric(130, y, 100, 0, -5000, 5000);

        _chkWindowed = new CheckBox
        {
            Text = "Windowed Mode",
            Location = new Point(15, y += 40),
            AutoSize = true,
            ForeColor = FgWhite,
            Checked = true
        };
        Controls.Add(_chkWindowed);

        AddLabel("Title Bar Offset (FIX_TOP_OFFSET):", 15, y += 35);
        _nudTopOffset = AddNumeric(15, y += 22, 100, _config.Layout.TopOffset, -100, 200);

        // Hint
        var hint = new Label
        {
            Text = "Changes require EQ restart to take effect.",
            Location = new Point(15, y += 40),
            AutoSize = true,
            ForeColor = FgGray,
            Font = new Font("Segoe UI", 8, FontStyle.Italic)
        };
        Controls.Add(hint);

        // Buttons
        var btnSave = new Button
        {
            Text = "Save", Location = new Point(200, y += 30), Size = new Size(80, 30),
            FlatStyle = FlatStyle.Flat, BackColor = AccentGreen, ForeColor = FgWhite
        };
        btnSave.Click += (_, _) => { SaveToIni(); Close(); };

        var btnClose = new Button
        {
            Text = "Close", Location = new Point(290, y), Size = new Size(80, 30),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 60), ForeColor = FgWhite
        };
        btnClose.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { btnSave, btnClose });
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
            bool inVideoMode = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("["))
                {
                    inVideoMode = trimmed.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inVideoMode) continue;

                var parts = trimmed.Split('=', 2);
                if (parts.Length != 2) continue;

                string key = parts[0].Trim();
                string val = parts[1].Trim();

                switch (key.ToLowerInvariant())
                {
                    case "width":
                        if (int.TryParse(val, out int w)) _nudWidth.Value = Math.Clamp(w, 320, 7680);
                        break;
                    case "height":
                        if (int.TryParse(val, out int h)) _nudHeight.Value = Math.Clamp(h, 200, 4320);
                        break;
                    case "windowedmode":
                        _chkWindowed.Checked = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "xoffset":
                        if (int.TryParse(val, out int ox)) _nudOffsetX.Value = ox;
                        break;
                    case "yoffset":
                        if (int.TryParse(val, out int oy)) _nudOffsetY.Value = oy;
                        break;
                }
            }

            // Match to preset
            int width = (int)_nudWidth.Value;
            int height = (int)_nudHeight.Value;
            int presetIdx = Array.FindIndex(Presets, p => p.W == width && p.H == height);
            _cboPreset.SelectedIndex = presetIdx >= 0 ? presetIdx : Presets.Length - 1; // Custom
        }
        catch (Exception ex)
        {
            FileLogger.Error("VideoSettings: load error", ex);
            _cboPreset.SelectedIndex = 0;
        }
    }

    private void SaveToIni()
    {
        try
        {
            // Also save TopOffset to config
            _config.Layout.TopOffset = (int)_nudTopOffset.Value;
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
                ["WindowedMode"] = _chkWindowed.Checked ? "1" : "0",
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

    // ─── Helpers ──────────────────────────────────────────────────

    private void AddLabel(string text, int x, int y)
    {
        Controls.Add(new Label
        {
            Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = FgWhite
        });
    }

    private NumericUpDown AddNumeric(int x, int y, int width, decimal defaultVal, decimal min, decimal max)
    {
        var nud = new NumericUpDown
        {
            Location = new Point(x, y), Size = new Size(width, 25),
            BackColor = BgInput, ForeColor = FgWhite,
            Minimum = min, Maximum = max,
            Value = Math.Clamp(defaultVal, min, max),
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(nud);
        return nud;
    }
}
