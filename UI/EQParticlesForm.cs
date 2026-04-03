using System.Text;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Manages particle/opacity settings in eqclient.ini [Defaults] section.
/// Experimental sub-form — sliders for opacity/density, numerics for filters/clip planes.
/// </summary>
public class EQParticlesForm : Form
{
    private readonly AppConfig _config;
    private readonly string _iniPath;
    private readonly Dictionary<string, TrackBar> _sliders = new();
    private readonly Dictionary<string, NumericUpDown> _numerics = new();
    private readonly Dictionary<string, string> _initialValues = new();

    // Opacity sliders (0.0 - 1.0 as 0-100%)
    private static readonly (string Key, string Label)[] OpacitySettings =
    {
        ("SpellParticleOpacity", "Spell Particle Opacity"),
        ("EnvironmentParticleOpacity", "Environment Particle Opacity"),
        ("ActorParticleOpacity", "Actor Particle Opacity"),
    };

    // Density sliders (0.0 - 1.0 as 0-100%)
    private static readonly (string Key, string Label)[] DensitySettings =
    {
        ("SpellParticleDensity", "Spell Particle Density"),
        ("EnvironmentParticleDensity", "Environment Particle Density"),
        ("ActorParticleDensity", "Actor Particle Density"),
    };

    // Near clip plane numerics (float values)
    private static readonly (string Key, string Label, decimal Default)[] ClipSettings =
    {
        ("SpellParticleNearClipPlane", "Spell Near Clip", 2.0m),
        ("EnvironmentParticleNearClipPlane", "Env Near Clip", 2.0m),
        ("ActorParticleNearClipPlane", "Actor Near Clip", 2.0m),
    };

    // Cast filter numerics (int values)
    private static readonly (string Key, string Label, int Default)[] FilterSettings =
    {
        ("SpellParticleCastFilter", "Spell Cast Filter", 1),
        ("EnvironmentParticleCastFilter", "Env Cast Filter", 24),
        ("ActorParticleCastFilter", "Actor Cast Filter", 1),
        ("ActorNewArmorFilter", "Actor Armor Filter", 24),
    };

    // Misc settings
    private NumericUpDown _nudFogScale = null!;
    private NumericUpDown _nudLODBias = null!;
    private CheckBox _chkSameResolution = null!;

    public EQParticlesForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Particles & Opacity (Experimental)", new Size(440, 700));
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = true;

        int y = 12;
        y = DarkTheme.AddSectionHeader(this, "\u2728  Particle Opacity (0% \u2013 100%)", 15, y);
        DarkTheme.AddHint(this, "0% = no particles, 100% = full. Read from eqclient.ini on open.", 15, y);
        y += 35;

        // Opacity sliders
        foreach (var (key, label) in OpacitySettings)
        {
            DarkTheme.AddLabel(this, label + ":", 15, y);
            var pctLabel = new Label
            {
                Text = "100%",
                Location = new Point(370, y),
                Size = new Size(45, 16),
                ForeColor = DarkTheme.FgGray,
                Font = new Font("Segoe UI", 8)
            };
            Controls.Add(pctLabel);

            var slider = new TrackBar
            {
                Location = new Point(200, y - 3),
                Size = new Size(170, 30),
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                TickFrequency = 25,
                BackColor = DarkTheme.BgDark
            };
            var pct = pctLabel; // capture for lambda
            slider.ValueChanged += (_, _) => pct.Text = $"{slider.Value}%";
            Controls.Add(slider);
            _sliders[key] = slider;
            y += 30;
        }

        y += 5;
        y = DarkTheme.AddSectionHeader(this, "Particle Density (0% \u2013 100%)", 15, y);
        y += 5;

        foreach (var (key, label) in DensitySettings)
        {
            DarkTheme.AddLabel(this, label + ":", 15, y);
            var pctLabel = new Label
            {
                Text = "0%",
                Location = new Point(370, y),
                Size = new Size(45, 16),
                ForeColor = DarkTheme.FgGray,
                Font = new Font("Segoe UI", 8)
            };
            Controls.Add(pctLabel);

            var slider = new TrackBar
            {
                Location = new Point(200, y - 3),
                Size = new Size(170, 30),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                TickFrequency = 25,
                BackColor = DarkTheme.BgDark
            };
            var pct = pctLabel;
            slider.ValueChanged += (_, _) => pct.Text = $"{slider.Value}%";
            Controls.Add(slider);
            _sliders[key] = slider;
            y += 30;
        }

        y += 5;
        y = DarkTheme.AddSectionHeader(this, "Clip Planes & Filters", 15, y);
        y += 5;

        foreach (var (key, label, def) in ClipSettings)
        {
            DarkTheme.AddLabel(this, label + ":", 15, y + 3);
            var nud = DarkTheme.AddNumeric(this, 200, y, 70, def, 0, 999);
            nud.DecimalPlaces = 1;
            nud.Increment = 0.5m;
            _numerics[key] = nud;
            y += 28;
        }

        foreach (var (key, label, def) in FilterSettings)
        {
            DarkTheme.AddLabel(this, label + ":", 15, y + 3);
            var nud = DarkTheme.AddNumeric(this, 200, y, 70, def, 0, 999);
            _numerics[key] = nud;
            y += 28;
        }

        y += 5;
        y = DarkTheme.AddSectionHeader(this, "Misc", 15, y);
        y += 5;

        DarkTheme.AddLabel(this, "FogScale:", 15, y + 3);
        _nudFogScale = DarkTheme.AddNumeric(this, 200, y, 80, 2.80m, 0, 100);
        _nudFogScale.DecimalPlaces = 2;
        _nudFogScale.Increment = 0.1m;

        DarkTheme.AddLabel(this, "LODBias:", 15, y += 28);
        _nudLODBias = DarkTheme.AddNumeric(this, 200, y - 3, 80, 10, 0, 100);

        _chkSameResolution = new CheckBox
        {
            Text = "Same Resolution  (SameResolution=1)",
            Location = new Point(15, y += 28),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = false
        };
        Controls.Add(_chkSameResolution);

        y += 40;

        var btnSave = DarkTheme.MakePrimaryButton("Save", 80, y);
        btnSave.Click += (_, _) => { SaveSettings(); Close(); };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 170, y);
        btnApply.Click += (_, _) => { SaveSettings(); };

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 260, y);
        btnCancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { btnSave, btnApply, btnCancel });
    }

    private void LoadFromIni()
    {
        if (!File.Exists(_iniPath)) return;

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

                if (!currentSection.Equals("[Defaults]", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = trimmed.Split('=', 2);
                if (parts.Length != 2) continue;

                string key = parts[0].Trim();
                string val = parts[1].Trim();

                // Opacity/density sliders (float 0.0-1.0 → slider 0-100)
                if (_sliders.TryGetValue(key, out var slider))
                {
                    if (double.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d))
                        slider.Value = Math.Clamp((int)(d * 100), 0, 100);
                }

                // Clip planes and filters
                if (_numerics.TryGetValue(key, out var nud))
                {
                    if (decimal.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out decimal dec))
                        nud.Value = Math.Clamp(dec, nud.Minimum, nud.Maximum);
                }

                // Misc
                switch (key)
                {
                    case "FogScale":
                        if (decimal.TryParse(val, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out decimal fog))
                            _nudFogScale.Value = Math.Clamp(fog, 0, 100);
                        break;
                    case "LODBias":
                        if (int.TryParse(val, out int lod))
                            _nudLODBias.Value = Math.Clamp(lod, 0, 100);
                        break;
                    case "SameResolution":
                        _chkSameResolution.Checked = val == "1";
                        break;
                }
            }

            // Snapshot initial state
            SnapshotValues();
            FileLogger.Info("EQParticles: loaded current values from eqclient.ini");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQParticles: load error", ex);
        }
    }

    private void SnapshotValues()
    {
        foreach (var (key, slider) in _sliders)
            _initialValues[key] = (slider.Value / 100.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (key, nud) in _numerics)
            _initialValues[key] = nud.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _initialValues["FogScale"] = _nudFogScale.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        _initialValues["LODBias"] = ((int)_nudLODBias.Value).ToString();
        _initialValues["SameResolution"] = _chkSameResolution.Checked ? "1" : "0";
    }

    private Dictionary<string, string> GetCurrentValues()
    {
        var values = new Dictionary<string, string>();
        foreach (var (key, slider) in _sliders)
            values[key] = (slider.Value / 100.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (key, nud) in _numerics)
        {
            // Clip planes are float, filters are int
            if (nud.DecimalPlaces > 0)
                values[key] = nud.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            else
                values[key] = ((int)nud.Value).ToString();
        }
        values["FogScale"] = _nudFogScale.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        values["LODBias"] = ((int)_nudLODBias.Value).ToString();
        values["SameResolution"] = _chkSameResolution.Checked ? "1" : "0";
        return values;
    }

    private void SaveSettings()
    {
        var current = GetCurrentValues();

        // Find changed keys
        var changed = new Dictionary<string, string>();
        foreach (var (key, val) in current)
        {
            if (!_initialValues.TryGetValue(key, out string? init) || init != val)
                changed[key] = val;
        }

        if (changed.Count == 0)
        {
            FileLogger.Info("EQParticles: no changes to save");
            return;
        }

        // Save to config
        foreach (var (key, val) in changed)
            _config.EQClientIni.ParticleOverrides[key] = val;
        ConfigManager.Save(_config);

        // Apply to eqclient.ini
        if (!File.Exists(_iniPath)) return;

        try
        {
            var lines = File.ReadAllLines(_iniPath, Encoding.Default).ToList();

            foreach (var (key, val) in changed)
                EQClientSettingsForm.SetIniValue(lines, "Defaults", key, val);

            File.WriteAllLines(_iniPath, lines, Encoding.Default);
            FileLogger.Info($"EQParticles: saved {changed.Count} changed setting(s) to eqclient.ini");

            // Update snapshot so Apply doesn't re-write
            SnapshotValues();
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQParticles: save error", ex);
            MessageBox.Show($"Failed to save: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Static helper: enforce all particle overrides in eqclient.ini.
    /// Called by EnforceOverrides in EQClientSettingsForm.
    /// </summary>
    public static void EnforceOverrides(AppConfig config, List<string> lines)
    {
        foreach (var (key, value) in config.EQClientIni.ParticleOverrides)
            EQClientSettingsForm.SetIniValue(lines, "Defaults", key, value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }
}
