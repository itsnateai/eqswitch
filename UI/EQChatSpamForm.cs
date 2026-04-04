using System.Text;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Manages eqclient.ini chat spam filter settings.
/// All settings are in [Options] section, values are 0 or 1.
/// </summary>
public class EQChatSpamForm : Form
{
    private readonly AppConfig _config;
    private readonly string _iniPath;
    private readonly Dictionary<string, CheckBox> _checkBoxes = new();

    // Define all chat spam settings with their display names and default INI values
    private static readonly (string Key, string Label, int DefaultValue)[] SpamSettings =
    {
        ("BadWord", "Bad Word Filter", 1),
        ("PCSpells", "PC Spells", 0),
        ("NPCSpells", "NPC Spells", 0),
        ("CriticalSpells", "Critical Spells", 0),
        ("CriticalMelee", "Critical Melee", 0),
        ("SpellDamage", "Spell Damage", 0),
        ("HideDamageShield", "Hide Damage Shield", 1),
        ("DotDamage", "DoT Damage", 0),
        ("PetAttacks", "Pet Attacks", 0),
        ("PetMisses", "Pet Misses", 1),
        ("FocusEffects", "Focus Effects", 0),
        ("PetSpells", "Pet Spells", 0),
        ("HealOverTimeSpells", "Heal Over Time Spells", 0),
        ("ItemSpeech", "Item Speech", 0),
        ("Strikethrough", "Strikethrough", 0),
        ("Stun", "Stun Messages", 0),
        ("SwarmPetDeath", "Swarm Pet Death", 0),
        ("FellowshipChat", "Fellowship Chat", 0),
        ("MercenaryMessages", "Mercenary Messages", 1),
        ("Spam", "Spam Filter", 1),
        ("Achievements", "Achievements", 0),
        ("PvPMessages", "PvP Messages", 1),
    };

    public EQChatSpamForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Chat Spam Filters", new Size(380, 580));

        int y = 12;
        y = DarkTheme.AddSectionHeader(this, "\uD83D\uDCAC  Chat Spam Filter Settings", 15, y);
        DarkTheme.AddHint(this, "Toggle which chat messages appear in your EQ chat window.\nChecked = enabled/shown, Unchecked = hidden/filtered.", 15, y);
        y += 40;

        // Two columns of checkboxes
        int col1X = 20;
        int col2X = 200;
        int halfCount = (SpamSettings.Length + 1) / 2;

        for (int i = 0; i < SpamSettings.Length; i++)
        {
            var (key, label, defaultVal) = SpamSettings[i];
            int x = i < halfCount ? col1X : col2X;
            int row = i < halfCount ? i : i - halfCount;

            var chk = new CheckBox
            {
                Text = label,
                Location = new Point(x, y + row * 24),
                AutoSize = true,
                ForeColor = DarkTheme.FgWhite,
                Checked = _config.EQClientIni.ChatSpamOverrides.TryGetValue(key, out int val)
                    ? val == 1
                    : defaultVal == 1
            };
            Controls.Add(chk);
            _checkBoxes[key] = chk;
        }

        y += halfCount * 24 + 15;

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

                if (!currentSection.Equals("[Options]", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = trimmed.Split('=', 2);
                if (parts.Length != 2) continue;

                string key = parts[0].Trim();
                string val = parts[1].Trim();

                // Check if this is one of our spam settings
                if (_checkBoxes.TryGetValue(key, out var chk))
                {
                    chk.Checked = val == "1";
                }
            }

            FileLogger.Info("EQChatSpam: loaded current values from eqclient.ini");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQChatSpam: load error", ex);
        }
    }

    private void SaveSettings()
    {
        // Save to config
        foreach (var (key, chk) in _checkBoxes)
        {
            _config.EQClientIni.ChatSpamOverrides[key] = chk.Checked ? 1 : 0;
        }
        ConfigManager.Save(_config);

        // Apply to eqclient.ini
        ApplyToIni();
    }

    private void ApplyToIni()
    {
        if (!File.Exists(_iniPath))
        {
            FileLogger.Info($"EQChatSpam: eqclient.ini not found at {_iniPath}");
            return;
        }

        try
        {
            var lines = File.ReadAllLines(_iniPath, Encoding.Default).ToList();

            foreach (var (key, chk) in _checkBoxes)
            {
                EQClientSettingsForm.SetIniValue(lines, "Options", key, chk.Checked ? "1" : "0");
            }

            File.WriteAllLines(_iniPath, lines, Encoding.Default);
            FileLogger.Info("EQChatSpam: applied chat spam settings to eqclient.ini");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQChatSpam: apply error", ex);
            MessageBox.Show($"Failed to update eqclient.ini: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }

    /// <summary>
    /// Static helper: enforce all chat spam overrides in eqclient.ini.
    /// Called by EnforceOverrides in EQClientSettingsForm.
    /// </summary>
    public static void EnforceOverrides(AppConfig config, List<string> lines)
    {
        foreach (var (key, value) in config.EQClientIni.ChatSpamOverrides)
        {
            EQClientSettingsForm.SetIniValue(lines, "Options", key, value != 0 ? "1" : "0");
        }
    }
}
