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

    // Use shared DarkTheme palette

    /// <summary>
    /// Model settings with display names and INI key names.
    /// Grouped: global toggles first, then by race.
    /// </summary>
    private static readonly (string Key, string Label)[] ModelSettings =
    {
        // Global toggles
        ("LoadSocialAnimations", "Load Social Animations"),
        ("AllLuclinPcModelsOff", "All Luclin PC Models Off"),
        ("LoadVeliousArmorsWithLuclin", "Load Velious Armors with Luclin"),
        ("UseLuclinElementals", "Use Luclin Elementals"),
        // Human
        ("UseLuclinHumanMale", "Human Male"),
        ("UseLuclinHumanFemale", "Human Female"),
        // Barbarian
        ("UseLuclinBarbarianMale", "Barbarian Male"),
        ("UseLuclinBarbarianFemale", "Barbarian Female"),
        // Erudite
        ("UseLuclinEruditeMale", "Erudite Male"),
        ("UseLuclinEruditeFemale", "Erudite Female"),
        // Wood Elf
        ("UseLuclinWoodElfMale", "Wood Elf Male"),
        ("UseLuclinWoodElfFemale", "Wood Elf Female"),
        // High Elf
        ("UseLuclinHighElfMale", "High Elf Male"),
        ("UseLuclinHighElfFemale", "High Elf Female"),
        // Dark Elf
        ("UseLuclinDarkElfMale", "Dark Elf Male"),
        ("UseLuclinDarkElfFemale", "Dark Elf Female"),
        // Half Elf
        ("UseLuclinHalfElfMale", "Half Elf Male"),
        ("UseLuclinHalfElfFemale", "Half Elf Female"),
        // Dwarf
        ("UseLuclinDwarfMale", "Dwarf Male"),
        ("UseLuclinDwarfFemale", "Dwarf Female"),
        // Troll
        ("UseLuclinTrollMale", "Troll Male"),
        ("UseLuclinTrollFemale", "Troll Female"),
        // Ogre
        ("UseLuclinOgreMale", "Ogre Male"),
        ("UseLuclinOgreFemale", "Ogre Female"),
        // Halfling
        ("UseLuclinHalflingMale", "Halfling Male"),
        ("UseLuclinHalflingFemale", "Halfling Female"),
        // Gnome
        ("UseLuclinGnomeMale", "Gnome Male"),
        ("UseLuclinGnomeFemale", "Gnome Female"),
        // Iksar
        ("UseLuclinIksarMale", "Iksar Male"),
        ("UseLuclinIksarFemale", "Iksar Female"),
        // Vah Shir
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
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Luclin Model Settings", new Size(460, 600));
        StartPosition = FormStartPosition.CenterParent;

        // Scrollable panel for all the checkboxes
        var panel = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(440, 510),
            AutoScroll = true,
            BackColor = DarkTheme.BgDark
        };

        int y = 10;

        var header = new Label
        {
            Text = "\uD83C\uDFAD  Luclin Model Overrides",
            Location = new Point(15, y),
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = DarkTheme.FgWhite
        };
        panel.Controls.Add(header);

        var hint = new Label
        {
            Text = "Check = use Luclin model, Uncheck = use classic model.\nRead from eqclient.ini on open, saved on Save.",
            Location = new Point(15, y += 25),
            Size = new Size(400, 32),
            ForeColor = DarkTheme.FgGray,
            Font = new Font("Segoe UI", 8, FontStyle.Italic)
        };
        panel.Controls.Add(hint);
        y += 40;

        // Quick buttons
        var btnAll = DarkTheme.MakeButton("All Luclin", DarkTheme.BgMedium, 15, y);
        btnAll.Size = new Size(90, 25);
        btnAll.Click += (_, _) => SetAllRaceModels(true);
        panel.Controls.Add(btnAll);

        var btnNone = DarkTheme.MakeButton("All Classic", DarkTheme.BgMedium, 115, y);
        btnNone.Size = new Size(90, 25);
        btnNone.Click += (_, _) => SetAllRaceModels(false);
        panel.Controls.Add(btnNone);
        y += 35;

        // Generate checkboxes — two columns for race models
        int col1X = 20;
        int col2X = 230;
        bool useCol2 = false;
        int globalEnd = 4; // first 4 are global toggles, full width

        for (int i = 0; i < ModelSettings.Length; i++)
        {
            var (key, label) = ModelSettings[i];

            bool savedValue = _config.EQClientIni.ModelOverrides.TryGetValue(key, out bool v) && v;

            var chk = new CheckBox
            {
                Text = label,
                AutoSize = true,
                ForeColor = DarkTheme.FgWhite,
                Checked = savedValue
            };

            if (i < globalEnd)
            {
                // Global toggles — full width, single column
                chk.Location = new Point(col1X, y);
                y += 25;
            }
            else
            {
                // Race models — two columns
                if (!useCol2)
                {
                    chk.Location = new Point(col1X, y);
                    useCol2 = true;
                }
                else
                {
                    chk.Location = new Point(col2X, y);
                    useCol2 = false;
                    y += 25;
                }
            }

            panel.Controls.Add(chk);
            _checkboxes[key] = chk;
        }
        // Handle odd number of race entries
        if (useCol2) y += 25;

        Controls.Add(panel);

        // Buttons at bottom
        int btnY = 520;

        var btnSave = DarkTheme.MakePrimaryButton("Save", 150, btnY);
        btnSave.Click += (_, _) => { SaveSettings(); Close(); };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 240, btnY);
        btnApply.Click += (_, _) => { SaveSettings(); };

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 330, btnY);
        btnCancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { btnSave, btnApply, btnCancel });
    }

    private void SetAllRaceModels(bool value)
    {
        // Skip the first 4 global toggles
        for (int i = 4; i < ModelSettings.Length; i++)
        {
            if (_checkboxes.TryGetValue(ModelSettings[i].Key, out var chk))
                chk.Checked = value;
        }
    }

    /// <summary>
    /// Read current values from eqclient.ini [Defaults] section.
    /// </summary>
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

                if (_checkboxes.TryGetValue(key, out var chk))
                    chk.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            }

            // Snapshot initial state — we'll only write keys the user actually changes
            foreach (var (key, chk) in _checkboxes)
                _initialValues[key] = chk.Checked;
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQModels: load error", ex);
        }
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
}
