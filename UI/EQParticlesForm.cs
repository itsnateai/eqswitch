// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Particle / opacity settings. Phase 4 of the EQ Client Settings overhaul: on the shared schema
/// engine — live-read display, touch-gated save (only the controls the user changed), and no launch
/// re-stamp (eqgame wins after first set). Opacity/density TrackBars bind through the engine's slider
/// path (slider 0-100 ↔ the schema's 0..1 float, scale 100). Most keys are [Defaults];
/// FogScale/LODBias/SameResolution are [Options] — each key's section/polarity/default lives once in
/// EqClientIniSchema. Display labels stay short here (the card title supplies the context).
/// </summary>
public class EQParticlesForm : EqSwitchForm
{
    private readonly AppConfig _config;
    private readonly string _iniPath;
    private readonly List<EqClientBinding> _bindings = new();

    private static readonly Dictionary<string, IniSetting> SchemaByKey =
        EqClientIniSchema.All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    // Short display labels (the card title gives context); key+section+default live in the schema.
    private static readonly (string Key, string Label)[] OpacitySettings =
    {
        ("SpellParticleOpacity", "Spell Particles"),
        ("EnvironmentParticleOpacity", "Environment"),
        ("ActorParticleOpacity", "Actor"),
    };

    private static readonly (string Key, string Label)[] DensitySettings =
    {
        ("SpellParticleDensity", "Spell Particles"),
        ("EnvironmentParticleDensity", "Environment"),
        ("ActorParticleDensity", "Actor"),
    };

    private static readonly (string Key, string Label)[] ClipSettings =
    {
        ("SpellParticleNearClipPlane", "Spell Near Clip"),
        ("EnvironmentParticleNearClipPlane", "Env Near Clip"),
        ("ActorParticleNearClipPlane", "Actor Near Clip"),
    };

    private static readonly (string Key, string Label)[] FilterSettings =
    {
        ("SpellParticleCastFilter", "Spell Cast Filter"),
        ("EnvironmentParticleCastFilter", "Env Cast Filter"),
        ("ActorParticleCastFilter", "Actor Cast Filter"),
        ("ActorNewArmorFilter", "Actor Armor Filter"),
    };

    public EQParticlesForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "EQSwitch — Particles & Opacity — EXPERIMENTAL", new Size(480, 700));
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = true;

        int y = 8;

        // ─── Opacity card ─────────────────────────────────────────
        // +22 leaves a row for the trailing "0% = no particles, 100% = full" hint below the last slider.
        var cardOpacity = DarkTheme.MakeCard(this, "✨", "Particle Opacity", DarkTheme.CardPurple, 10, y, 440, 30 + OpacitySettings.Length * 30 + 22);
        int cy = 30;
        foreach (var (key, label) in OpacitySettings)
        {
            AddBoundSlider(cardOpacity, key, label, cy);
            cy += 30;
        }
        DarkTheme.AddCardHint(cardOpacity, "0% = no particles, 100% = full", 10, cy);
        y += cardOpacity.Height + 8;

        // ─── Density card ─────────────────────────────────────────
        var cardDensity = DarkTheme.MakeCard(this, "🌫", "Particle Density", DarkTheme.CardBlue, 10, y, 440, 30 + DensitySettings.Length * 30 + 4);
        cy = 30;
        foreach (var (key, label) in DensitySettings)
        {
            AddBoundSlider(cardDensity, key, label, cy);
            cy += 30;
        }
        y += cardDensity.Height + 8;

        // ─── Clip Planes & Filters card ───────────────────────────
        int clipCardH = 30 + (ClipSettings.Length + FilterSettings.Length) * 26 + 8;
        var cardClip = DarkTheme.MakeCard(this, "✂", "Clip Planes & Filters", DarkTheme.CardGreen, 10, y, 440, clipCardH);
        cy = 30;
        foreach (var (key, label) in ClipSettings)
        {
            DarkTheme.AddCardLabel(cardClip, label, 10, cy + 2);
            var nud = AddBoundNumeric(cardClip, key, 180, cy, 70);
            nud.DecimalPlaces = 1;
            nud.Increment = 0.5m;
            cy += 26;
        }
        foreach (var (key, label) in FilterSettings)
        {
            DarkTheme.AddCardLabel(cardClip, label, 10, cy + 2);
            AddBoundNumeric(cardClip, key, 180, cy, 70);
            cy += 26;
        }
        y += cardClip.Height + 8;

        // ─── Misc card ────────────────────────────────────────────
        var cardMisc = DarkTheme.MakeCard(this, "⚙", "Misc", DarkTheme.CardCyan, 10, y, 440, 100);
        cy = 30;

        DarkTheme.AddCardLabel(cardMisc, "FogScale:", 10, cy + 2);
        var nudFog = AddBoundNumeric(cardMisc, "FogScale", 120, cy, 80);
        nudFog.DecimalPlaces = 2;
        nudFog.Increment = 0.1m;

        DarkTheme.AddCardLabel(cardMisc, "LODBias:", 230, cy + 2);
        AddBoundNumeric(cardMisc, "LODBias", 310, cy, 70);

        cy += 28;
        var chkSameRes = DarkTheme.AddCardCheckBox(cardMisc, "Same Resolution", 10, cy);
        _bindings.Add(new EqClientBinding(SchemaByKey["SameResolution"], chkSameRes, null));
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

        // Size the form to its content so the button bar sits a consistent gap below the Misc card.
        FitClientHeightToContent();
    }

    /// <summary>Add a label + a 0-100 TrackBar (with live % readout) bound to a schema float (scale 100).</summary>
    private void AddBoundSlider(Panel card, string key, string label, int cy)
    {
        var setting = SchemaByKey[key];
        DarkTheme.AddCardLabel(card, label, 10, cy + 2);

        var pctLabel = DarkTheme.AddCardHint(card, "", 370, cy + 2);
        pctLabel.AutoSize = false;
        pctLabel.Size = new Size(45, 16);

        var slider = new TrackBar
        {
            Location = new Point(140, cy - 3),
            Size = new Size(220, 30),
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp((int)(setting.DefaultNumber * 100m), 0, 100),
            TickFrequency = 25,
            BackColor = DarkTheme.BgPanel
        };
        pctLabel.Text = $"{slider.Value}%";
        slider.ValueChanged += (_, _) => pctLabel.Text = $"{slider.Value}%";
        card.Controls.Add(slider);

        _bindings.Add(new EqClientBinding(setting, slider, 100m));
    }

    /// <summary>Add a NumericUpDown bound to a schema numeric (def/min/max from the descriptor). Returns it for per-field tuning.</summary>
    private NumericUpDown AddBoundNumeric(Panel card, string key, int x, int cy, int w)
    {
        var setting = SchemaByKey[key];
        var nud = DarkTheme.AddCardNumeric(card, x, cy, w, setting.DefaultNumber, setting.Min, setting.Max);
        _bindings.Add(new EqClientBinding(setting, null, nud));
        return nud;
    }

    private void LoadFromIni()
    {
        // Live read of eqclient.ini via the shared schema engine; snapshots each value for touch-gating.
        try
        {
            EqClientBindings.LoadInto(_bindings, _iniPath);
            FileLogger.Info("EQParticles: loaded current values from eqclient.ini (schema-driven)");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQParticles: load error", ex);
        }
    }

    private void SaveSettings()
    {
        // Touch-gated, schema-driven write — only the controls the user changed are written, each to
        // its canonical section. Untouched + unmanaged keys are left exactly as eqgame/the user left
        // them (point D: no clobber). No launch re-stamp — eqgame wins after.
        try
        {
            if (!File.Exists(_iniPath))
            {
                FileLogger.Info($"EQParticles: eqclient.ini not found at {_iniPath}");
                return;
            }

            int changed = EqClientBindings.SaveChanged(_bindings, _iniPath);
            FileLogger.Info(changed > 0
                ? $"EQParticles: wrote {changed} changed setting(s) to eqclient.ini (touch-gated)"
                : "EQParticles: no changes to save");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQParticles: save error", ex);
            ThemedMessageDialog.Show(this, $"Failed to save: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }
}
