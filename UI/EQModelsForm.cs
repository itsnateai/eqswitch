// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Luclin model settings (all [Defaults], TRUE/FALSE). Phase 3 of the EQ Client Settings overhaul:
/// on the shared schema engine — live-read display, touch-gated save (only the models the user
/// changed), and no launch re-stamp (eqgame wins after first set). The grouped key-lists below are
/// display layout only; each key's label, polarity, and default live once in EqClientIniSchema.
/// </summary>
public class EQModelsForm : EqSwitchForm
{
    private readonly AppConfig _config;
    private readonly string _iniPath;
    private readonly List<EqClientBinding> _bindings = new();

    private static readonly Dictionary<string, IniSetting> SchemaByKey =
        EqClientIniSchema.All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] GlobalKeys =
        { "LoadSocialAnimations", "AllLuclinPcModelsOff", "LoadVeliousArmorsWithLuclin", "UseLuclinElementals" };

    private static readonly string[] RaceKeys =
    {
        "UseLuclinHumanMale", "UseLuclinHumanFemale", "UseLuclinBarbarianMale", "UseLuclinBarbarianFemale",
        "UseLuclinEruditeMale", "UseLuclinEruditeFemale", "UseLuclinWoodElfMale", "UseLuclinWoodElfFemale",
        "UseLuclinHighElfMale", "UseLuclinHighElfFemale", "UseLuclinDarkElfMale", "UseLuclinDarkElfFemale",
        "UseLuclinHalfElfMale", "UseLuclinHalfElfFemale", "UseLuclinDwarfMale", "UseLuclinDwarfFemale",
        "UseLuclinTrollMale", "UseLuclinTrollFemale", "UseLuclinOgreMale", "UseLuclinOgreFemale",
        "UseLuclinHalflingMale", "UseLuclinHalflingFemale", "UseLuclinGnomeMale", "UseLuclinGnomeFemale",
        "UseLuclinIksarMale", "UseLuclinIksarFemale", "UseLuclinVahShirMale", "UseLuclinVahShirFemale",
    };

    private static readonly HashSet<string> RaceKeySet = new(RaceKeys, StringComparer.OrdinalIgnoreCase);

    public EQModelsForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "EQSwitch — Luclin Model Settings — EXPERIMENTAL", new Size(480, 620));
        StartPosition = FormStartPosition.CenterParent;

        // Scrollable content panel
        var scrollPanel = new Panel
        {
            Location = new Point(0, 0),
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = DarkTheme.BgDark
        };

        int y = 8;

        // ─── Global Toggles card ──────────────────────────────────
        int globalH = 30 + GlobalKeys.Length * 22 + 8;
        var cardGlobal = DarkTheme.MakeCard(scrollPanel, "⚙", "Global Toggles", DarkTheme.CardGold, 10, y, 430, globalH);
        int cy = 30;

        foreach (var key in GlobalKeys)
        {
            AddBoundCheck(cardGlobal, key, 10, cy);
            cy += 22;
        }

        y += globalH + 8;

        // ─── Race Models card ─────────────────────────────────────
        int raceRows = (RaceKeys.Length + 1) / 2;
        int raceH = 30 + raceRows * 22 + 38; // extra space for quick buttons
        var cardRace = DarkTheme.MakeCard(scrollPanel, "🎭", "Race Models", DarkTheme.CardPurple, 10, y, 430, raceH);
        cy = 30;

        // Quick buttons
        var btnAll = DarkTheme.AddCardButton(cardRace, "All Luclin", 10, cy, 90);
        btnAll.Click += (_, _) => SetAllRaceModels(true);

        var btnNone = DarkTheme.AddCardButton(cardRace, "All Classic", 110, cy, 90);
        btnNone.Click += (_, _) => SetAllRaceModels(false);

        DarkTheme.AddCardHint(cardRace, "Check = Luclin model, Uncheck = classic", 210, cy + 5);
        cy += 30;

        // Two columns of checkboxes
        for (int i = 0; i < RaceKeys.Length; i += 2)
        {
            AddBoundCheck(cardRace, RaceKeys[i], 10, cy);
            if (i + 1 < RaceKeys.Length)
                AddBoundCheck(cardRace, RaceKeys[i + 1], 220, cy);
            cy += 22;
        }

        Controls.Add(scrollPanel);

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

        // Size the form to the scroll panel's content so the button bar sits a consistent gap below
        // the Race Models card. Content lives in scrollPanel (Dock=Fill), so it — not `this` — is the host.
        FitClientHeightToContent(scrollPanel);
    }

    /// <summary>Create a checkbox labelled from the schema and register it as a touch-gated binding.</summary>
    private void AddBoundCheck(Panel card, string key, int x, int y)
    {
        var setting = SchemaByKey[key];
        var chk = DarkTheme.AddCardCheckBox(card, setting.Label, x, y);
        _bindings.Add(new EqClientBinding(setting, chk, null));
    }

    private void SetAllRaceModels(bool value)
    {
        foreach (var b in _bindings)
            if (b.Check != null && RaceKeySet.Contains(b.Setting.Key))
                b.Check.Checked = value;
    }

    private void LoadFromIni()
    {
        // Live read of eqclient.ini [Defaults] via the shared schema engine; snapshots each value for touch-gating.
        try
        {
            EqClientBindings.LoadInto(_bindings, _iniPath);
            FileLogger.Info("EQModels: loaded current values from eqclient.ini (schema-driven)");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQModels: load error", ex);
        }
    }

    private void SaveSettings()
    {
        // Touch-gated, schema-driven write — only the models the user changed are written to [Defaults].
        // Untouched + unmanaged keys are left exactly as eqgame/the user left them. No launch re-stamp.
        try
        {
            if (!File.Exists(_iniPath))
            {
                FileLogger.Info($"EQModels: eqclient.ini not found at {_iniPath}");
                return;
            }

            int changed = EqClientBindings.SaveChanged(_bindings, _iniPath);
            FileLogger.Info(changed > 0
                ? $"EQModels: wrote {changed} changed model setting(s) to eqclient.ini (touch-gated)"
                : "EQModels: no changes to save");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQModels: save error", ex);
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
