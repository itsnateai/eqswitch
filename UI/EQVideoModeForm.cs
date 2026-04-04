using System.Text;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Manages eqclient.ini [VideoMode] section settings.
/// Experimental sub-form for advanced/troubleshooting video mode configuration.
/// </summary>
public class EQVideoModeForm : Form
{
    private readonly AppConfig _config;
    private readonly string _iniPath;
    private readonly Dictionary<string, NumericUpDown> _numerics = new();
    private readonly Dictionary<string, string> _initialValues = new();

    private static readonly (string Key, string Label, int Default, int Min, int Max)[] VideoSettings =
    {
        ("Width", "Width", 1920, 640, 7680),
        ("Height", "Height", 1080, 480, 4320),
        ("FullscreenRefreshRate", "Fullscreen Refresh Rate", 0, 0, 360),
        ("FullscreenBitsPerPixel", "Fullscreen Bits Per Pixel", 32, 16, 32),
        ("WindowedWidth", "Windowed Width", 1920, 640, 7680),
        ("WindowedHeight", "Windowed Height", 1080, 480, 4320),
        ("WinEQWidth", "WinEQ Width", 1920, 640, 7680),
        ("WinEQHeight", "WinEQ Height", 1200, 480, 4320),
        ("WindowedModeXOffset", "Windowed X Offset", 0, -9999, 9999),
        ("WindowedModeYOffset", "Windowed Y Offset", 0, -9999, 9999),
        ("YOffset", "Y Offset", 0, -9999, 9999),
        ("XOffset", "X Offset", 0, -9999, 9999),
    };

    public EQVideoModeForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Video Mode (Experimental)", new Size(400, 520));
        StartPosition = FormStartPosition.CenterParent;

        int y = 12;
        y = DarkTheme.AddSectionHeader(this, "\uD83D\uDCFA  [VideoMode] Settings", 15, y);
        DarkTheme.AddHint(this, "Advanced video mode settings from eqclient.ini.\nOnly changed values are written on Save.\nChanges take effect on next EQ launch — not running clients.", 15, y);
        y += 52;

        foreach (var (key, label, def, min, max) in VideoSettings)
        {
            DarkTheme.AddLabel(this, label + ":", 15, y + 3);
            var nud = new NumericUpDown
            {
                Location = new Point(230, y), Size = new Size(100, 24),
                BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
                Minimum = min, Maximum = max,
                Value = def
            };
            Controls.Add(nud);
            _numerics[key] = nud;
            y += 28;
        }

        y += 15;

        var btnSave = DarkTheme.MakePrimaryButton("Save", 60, y);
        btnSave.Click += (_, _) => { SaveSettings(); Close(); };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 150, y);
        btnApply.Click += (_, _) => { SaveSettings(); };

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 240, y);
        btnCancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { btnSave, btnApply, btnCancel });
    }

    private void LoadFromIni()
    {
        if (File.Exists(_iniPath))
        {
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

                    if (!currentSection.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parts = trimmed.Split('=', 2);
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim();
                    string val = parts[1].Trim();

                    if (_numerics.TryGetValue(key, out var nud))
                    {
                        if (int.TryParse(val, out int v))
                            nud.Value = Math.Clamp(v, (int)nud.Minimum, (int)nud.Maximum);
                    }
                }

                FileLogger.Info("EQVideoMode: loaded current values from eqclient.ini");
            }
            catch (Exception ex)
            {
                FileLogger.Error("EQVideoMode: load error", ex);
            }
        }

        // Snapshot unconditionally — runs even if file missing or load failed
        foreach (var (key, nud) in _numerics)
            _initialValues[key] = ((int)nud.Value).ToString();
    }

    private void SaveSettings()
    {
        // Find changed keys
        var changed = new Dictionary<string, string>();
        foreach (var (key, nud) in _numerics)
        {
            string val = ((int)nud.Value).ToString();
            if (!_initialValues.TryGetValue(key, out string? init) || init != val)
                changed[key] = val;
        }

        if (changed.Count == 0)
        {
            FileLogger.Info("EQVideoMode: no changes to save");
            return;
        }

        // Save to config
        foreach (var (key, val) in changed)
            _config.EQClientIni.VideoModeOverrides[key] = val;
        ConfigManager.Save(_config);

        // Apply to eqclient.ini
        if (!File.Exists(_iniPath)) return;

        try
        {
            var lines = File.ReadAllLines(_iniPath, Encoding.Default).ToList();

            foreach (var (key, val) in changed)
                EQClientSettingsForm.SetIniValue(lines, "VideoMode", key, val);

            File.WriteAllLines(_iniPath, lines, Encoding.Default);
            FileLogger.Info($"EQVideoMode: saved {changed.Count} changed setting(s) to eqclient.ini");

            // Update snapshot
            foreach (var (key, nud) in _numerics)
                _initialValues[key] = ((int)nud.Value).ToString();
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQVideoMode: save error", ex);
            MessageBox.Show($"Failed to save: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }

    /// <summary>
    /// Static helper: enforce all video mode overrides in eqclient.ini.
    /// Called by EnforceOverrides in EQClientSettingsForm.
    /// </summary>
    public static void EnforceOverrides(AppConfig config, List<string> lines)
    {
        foreach (var (key, value) in config.EQClientIni.VideoModeOverrides)
            EQClientSettingsForm.SetIniValue(lines, "VideoMode", key, value);
    }
}
