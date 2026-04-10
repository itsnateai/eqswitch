using System.Text;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Manages particle/opacity settings in eqclient.ini.
/// Most settings are in [Defaults]; FogScale, LODBias, SameResolution are in [Options].
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
        ("SpellParticleOpacity", "Spell Particles"),
        ("EnvironmentParticleOpacity", "Environment"),
        ("ActorParticleOpacity", "Actor"),
    };

    // Density sliders (0.0 - 1.0 as 0-100%)
    private static readonly (string Key, string Label)[] DensitySettings =
    {
        ("SpellParticleDensity", "Spell Particles"),
        ("EnvironmentParticleDensity", "Environment"),
        ("ActorParticleDensity", "Actor"),
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
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Particles & Opacity", new Size(480, 700));
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = true;

        int y = 8;

        // ─── Opacity card ─────────────────────────────────────────
        var cardOpacity = DarkTheme.MakeCard(this, "\u2728", "Particle Opacity", DarkTheme.CardPurple, 10, y, 440, 30 + OpacitySettings.Length * 30 + 4);
        int cy = 30;

        foreach (var (key, label) in OpacitySettings)
        {
            DarkTheme.AddCardLabel(cardOpacity, label, 10, cy + 2);
            var pctLabel = DarkTheme.AddCardHint(cardOpacity, "100%", 370, cy + 2);
            pctLabel.AutoSize = false;
            pctLabel.Size = new Size(45, 16);

            var slider = new TrackBar
            {
                Location = new Point(140, cy - 3),
                Size = new Size(220, 30),
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                TickFrequency = 25,
                BackColor = DarkTheme.BgPanel
            };
            var pct = pctLabel;
            slider.ValueChanged += (_, _) => pct.Text = $"{slider.Value}%";
            cardOpacity.Controls.Add(slider);
            _sliders[key] = slider;
            cy += 30;
        }

        DarkTheme.AddCardHint(cardOpacity, "0% = no particles, 100% = full", 10, cy);
        y += cardOpacity.Height + 8;

        // ─── Density card ─────────────────────────────────────────
        var cardDensity = DarkTheme.MakeCard(this, "\uD83C\uDF2B", "Particle Density", DarkTheme.CardBlue, 10, y, 440, 30 + DensitySettings.Length * 30 + 4);
        cy = 30;

        foreach (var (key, label) in DensitySettings)
        {
            DarkTheme.AddCardLabel(cardDensity, label, 10, cy + 2);
            var pctLabel = DarkTheme.AddCardHint(cardDensity, "0%", 370, cy + 2);
            pctLabel.AutoSize = false;
            pctLabel.Size = new Size(45, 16);

            var slider = new TrackBar
            {
                Location = new Point(140, cy - 3),
                Size = new Size(220, 30),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                TickFrequency = 25,
                BackColor = DarkTheme.BgPanel
            };
            var pct = pctLabel;
            slider.ValueChanged += (_, _) => pct.Text = $"{slider.Value}%";
            cardDensity.Controls.Add(slider);
            _sliders[key] = slider;
            cy += 30;
        }

        y += cardDensity.Height + 8;

        // ─── Clip Planes & Filters card ───────────────────────────
        int clipCardH = 30 + (ClipSettings.Length + FilterSettings.Length) * 26 + 8;
        var cardClip = DarkTheme.MakeCard(this, "\u2702", "Clip Planes & Filters", DarkTheme.CardGreen, 10, y, 440, clipCardH);
        cy = 30;

        foreach (var (key, label, def) in ClipSettings)
        {
            DarkTheme.AddCardLabel(cardClip, label, 10, cy + 2);
            var nud = DarkTheme.AddCardNumeric(cardClip, 180, cy, 70, def, 0, 999);
            nud.DecimalPlaces = 1;
            nud.Increment = 0.5m;
            _numerics[key] = nud;
            cy += 26;
        }

        foreach (var (key, label, def) in FilterSettings)
        {
            DarkTheme.AddCardLabel(cardClip, label, 10, cy + 2);
            var nud = DarkTheme.AddCardNumeric(cardClip, 180, cy, 70, def, 0, 999);
            _numerics[key] = nud;
            cy += 26;
        }

        y += cardClip.Height + 8;

        // ─── Misc card ────────────────────────────────────────────
        var cardMisc = DarkTheme.MakeCard(this, "\u2699", "Misc", DarkTheme.CardCyan, 10, y, 440, 100);
        cy = 30;

        DarkTheme.AddCardLabel(cardMisc, "FogScale:", 10, cy + 2);
        _nudFogScale = DarkTheme.AddCardNumeric(cardMisc, 120, cy, 80, 2.80m, 0, 100);
        _nudFogScale.DecimalPlaces = 2;
        _nudFogScale.Increment = 0.1m;

        DarkTheme.AddCardLabel(cardMisc, "LODBias:", 230, cy + 2);
        _nudLODBias = DarkTheme.AddCardNumeric(cardMisc, 310, cy, 70, 10, 0, 100);

        cy += 28;
        _chkSameResolution = DarkTheme.AddCardCheckBox(cardMisc, "Same Resolution", 10, cy);
        DarkTheme.AddCardHint(cardMisc, "[Options] SameResolution=1", 160, cy + 2);

        y += cardMisc.Height + 8;

        // ─── Docked bottom panel with Save/Apply/Cancel ──────────
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            BackColor = DarkTheme.BgDark
        };

        var btnSave = DarkTheme.MakePrimaryButton("Save", 110, 10);
        btnSave.Click += (_, _) => { SaveSettings(); Close(); };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 200, 10);
        btnApply.Click += (_, _) => { SaveSettings(); };

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 290, 10);
        btnCancel.Click += (_, _) => Close();

        buttonPanel.Controls.AddRange(new Control[] { btnSave, btnApply, btnCancel });
        Controls.Add(buttonPanel);
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

                    bool isDefaults = currentSection.Equals("[Defaults]", StringComparison.OrdinalIgnoreCase);
                    bool isOptions = currentSection.Equals("[Options]", StringComparison.OrdinalIgnoreCase);
                    if (!isDefaults && !isOptions)
                        continue;

                    var parts = trimmed.Split('=', 2);
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim();
                    string val = parts[1].Trim();

                    // [Defaults] — opacity/density sliders and clip/filter numerics
                    if (isDefaults)
                    {
                        if (_sliders.TryGetValue(key, out var slider))
                        {
                            if (double.TryParse(val, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double d))
                                slider.Value = Math.Clamp((int)(d * 100), 0, 100);
                        }

                        if (_numerics.TryGetValue(key, out var nud))
                        {
                            if (decimal.TryParse(val, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out decimal dec))
                                nud.Value = Math.Clamp(dec, nud.Minimum, nud.Maximum);
                        }
                    }

                    // [Options] — FogScale, LODBias, SameResolution
                    if (isOptions)
                    {
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
                }

                FileLogger.Info("EQParticles: loaded current values from eqclient.ini");
            }
            catch (Exception ex)
            {
                FileLogger.Error("EQParticles: load error", ex);
            }
        }

        // Snapshot unconditionally — runs even if file missing or load failed
        SnapshotValues();
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
                EQClientSettingsForm.SetIniValue(lines, GetSection(key), key, val);

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
            EQClientSettingsForm.SetIniValue(lines, GetSection(key), key, value);
    }

    /// <summary>FogScale, LODBias, SameResolution live in [Options]; everything else in [Defaults].</summary>
    private static string GetSection(string key) =>
        key is "FogScale" or "LODBias" or "SameResolution" ? "Options" : "Defaults";

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }
}
