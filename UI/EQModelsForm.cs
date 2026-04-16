// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System.Text;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Manages Luclin model settings in eqclient.ini [Defaults] section.
/// Allows toggling individual race/gender models on/off.
/// </summary>
public class EQModelsForm : Form
{
    private readonly AppConfig _config;
    private readonly string _iniPath;
    private readonly Dictionary<string, CheckBox> _checkboxes = new();
    // Snapshot of values loaded from INI — only write keys that changed
    private readonly Dictionary<string, bool> _initialValues = new();

    /// <summary>
    /// Model settings with display names and INI key names.
    /// Grouped: global toggles first, then by race.
    /// </summary>
    private static readonly (string Key, string Label)[] GlobalSettings =
    {
        ("LoadSocialAnimations", "Load Social Animations"),
        ("AllLuclinPcModelsOff", "All Luclin PC Models Off"),
        ("LoadVeliousArmorsWithLuclin", "Load Velious Armors with Luclin"),
        ("UseLuclinElementals", "Use Luclin Elementals"),
    };

    private static readonly (string Key, string Label)[] RaceSettings =
    {
        ("UseLuclinHumanMale", "Human Male"),
        ("UseLuclinHumanFemale", "Human Female"),
        ("UseLuclinBarbarianMale", "Barbarian Male"),
        ("UseLuclinBarbarianFemale", "Barbarian Female"),
        ("UseLuclinEruditeMale", "Erudite Male"),
        ("UseLuclinEruditeFemale", "Erudite Female"),
        ("UseLuclinWoodElfMale", "Wood Elf Male"),
        ("UseLuclinWoodElfFemale", "Wood Elf Female"),
        ("UseLuclinHighElfMale", "High Elf Male"),
        ("UseLuclinHighElfFemale", "High Elf Female"),
        ("UseLuclinDarkElfMale", "Dark Elf Male"),
        ("UseLuclinDarkElfFemale", "Dark Elf Female"),
        ("UseLuclinHalfElfMale", "Half Elf Male"),
        ("UseLuclinHalfElfFemale", "Half Elf Female"),
        ("UseLuclinDwarfMale", "Dwarf Male"),
        ("UseLuclinDwarfFemale", "Dwarf Female"),
        ("UseLuclinTrollMale", "Troll Male"),
        ("UseLuclinTrollFemale", "Troll Female"),
        ("UseLuclinOgreMale", "Ogre Male"),
        ("UseLuclinOgreFemale", "Ogre Female"),
        ("UseLuclinHalflingMale", "Halfling Male"),
        ("UseLuclinHalflingFemale", "Halfling Female"),
        ("UseLuclinGnomeMale", "Gnome Male"),
        ("UseLuclinGnomeFemale", "Gnome Female"),
        ("UseLuclinIksarMale", "Iksar Male"),
        ("UseLuclinIksarFemale", "Iksar Female"),
        ("UseLuclinVahShirMale", "Vah Shir Male"),
        ("UseLuclinVahShirFemale", "Vah Shir Female"),
    };

    public EQModelsForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Luclin Model Settings", new Size(480, 620));
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
        int globalH = 30 + GlobalSettings.Length * 22 + 8;
        var cardGlobal = DarkTheme.MakeCard(scrollPanel, "\u2699", "Global Toggles", DarkTheme.CardGold, 10, y, 430, globalH);
        int cy = 30;

        foreach (var (key, label) in GlobalSettings)
        {
            bool savedValue = _config.EQClientIni.ModelOverrides.TryGetValue(key, out bool v) && v;
            var chk = DarkTheme.AddCardCheckBox(cardGlobal, label, 10, cy);
            chk.Checked = savedValue;
            _checkboxes[key] = chk;
            cy += 22;
        }

        y += globalH + 8;

        // ─── Race Models card ─────────────────────────────────────
        int raceRows = (RaceSettings.Length + 1) / 2;
        int raceH = 30 + raceRows * 22 + 38; // extra space for quick buttons
        var cardRace = DarkTheme.MakeCard(scrollPanel, "\uD83C\uDFAD", "Race Models", DarkTheme.CardPurple, 10, y, 430, raceH);
        cy = 30;

        // Quick buttons
        var btnAll = DarkTheme.AddCardButton(cardRace, "All Luclin", 10, cy, 90);
        btnAll.Click += (_, _) => SetAllRaceModels(true);

        var btnNone = DarkTheme.AddCardButton(cardRace, "All Classic", 110, cy, 90);
        btnNone.Click += (_, _) => SetAllRaceModels(false);

        DarkTheme.AddCardHint(cardRace, "Check = Luclin model, Uncheck = classic", 210, cy + 5);
        cy += 30;

        // Two columns of checkboxes
        for (int i = 0; i < RaceSettings.Length; i += 2)
        {
            var (key1, label1) = RaceSettings[i];
            bool saved1 = _config.EQClientIni.ModelOverrides.TryGetValue(key1, out bool v1) && v1;
            var chk1 = DarkTheme.AddCardCheckBox(cardRace, label1, 10, cy);
            chk1.Checked = saved1;
            _checkboxes[key1] = chk1;

            if (i + 1 < RaceSettings.Length)
            {
                var (key2, label2) = RaceSettings[i + 1];
                bool saved2 = _config.EQClientIni.ModelOverrides.TryGetValue(key2, out bool v2) && v2;
                var chk2 = DarkTheme.AddCardCheckBox(cardRace, label2, 220, cy);
                chk2.Checked = saved2;
                _checkboxes[key2] = chk2;
            }

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
    }

    private void SetAllRaceModels(bool value)
    {
        foreach (var (key, _) in RaceSettings)
        {
            if (_checkboxes.TryGetValue(key, out var chk))
                chk.Checked = value;
        }
    }

    /// <summary>
    /// Read current values from eqclient.ini [Defaults] section.
    /// </summary>
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

                    if (!currentSection.Equals("[Defaults]", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parts = trimmed.Split('=', 2);
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim();
                    string val = parts[1].Trim();

                    if (_checkboxes.TryGetValue(key, out var chk))
                        chk.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error("EQModels: load error", ex);
            }
        }

        // Snapshot initial state unconditionally — runs even if file missing or load failed
        foreach (var (key, chk) in _checkboxes)
            _initialValues[key] = chk.Checked;
    }

    private void SaveSettings()
    {
        // Find which keys the user actually changed
        var changedKeys = new List<string>();
        foreach (var (key, chk) in _checkboxes)
        {
            bool initial = _initialValues.TryGetValue(key, out bool v) && v;
            if (chk.Checked != initial)
                changedKeys.Add(key);
        }

        if (changedKeys.Count == 0)
        {
            FileLogger.Info("EQModels: no changes to save");
            return;
        }

        // Save to config — only store explicitly changed values
        foreach (var key in changedKeys)
            _config.EQClientIni.ModelOverrides[key] = _checkboxes[key].Checked;
        ConfigManager.Save(_config);

        // Apply only changed keys to eqclient.ini
        if (!File.Exists(_iniPath)) return;

        try
        {
            var lines = File.ReadAllLines(_iniPath, Encoding.Default).ToList();

            foreach (var key in changedKeys)
            {
                string value = _checkboxes[key].Checked ? "TRUE" : "FALSE";
                EQClientSettingsForm.SetIniValue(lines, "Defaults", key, value);
            }

            File.WriteAllLines(_iniPath, lines, Encoding.Default);
            FileLogger.Info($"EQModels: saved {changedKeys.Count} changed model setting(s) to eqclient.ini");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQModels: save error", ex);
            MessageBox.Show($"Failed to save: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Static helper: enforce all model overrides in eqclient.ini.
    /// Called by EnforceOverrides in EQClientSettingsForm.
    /// </summary>
    public static void EnforceOverrides(AppConfig config, List<string> lines)
    {
        foreach (var (key, value) in config.EQClientIni.ModelOverrides)
            EQClientSettingsForm.SetIniValue(lines, "Defaults", key, value ? "TRUE" : "FALSE");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }
}
