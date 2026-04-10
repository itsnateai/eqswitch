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

    // Grouped settings for card layout
    private static readonly (string Key, string Label, int DefaultValue)[] CombatSettings =
    {
        ("CriticalSpells", "Critical Spells", 0),
        ("CriticalMelee", "Critical Melee", 0),
        ("SpellDamage", "Spell Damage", 0),
        ("DotDamage", "DoT Damage", 0),
        ("HideDamageShield", "Hide Damage Shield", 1),
        ("Strikethrough", "Strikethrough", 0),
        ("Stun", "Stun Messages", 0),
    };

    private static readonly (string Key, string Label, int DefaultValue)[] PetSettings =
    {
        ("PetAttacks", "Pet Attacks", 0),
        ("PetMisses", "Pet Misses", 1),
        ("PetSpells", "Pet Spells", 0),
        ("SwarmPetDeath", "Swarm Pet Death", 0),
    };

    private static readonly (string Key, string Label, int DefaultValue)[] SpellSettings =
    {
        ("PCSpells", "PC Spells", 0),
        ("NPCSpells", "NPC Spells", 0),
        ("FocusEffects", "Focus Effects", 0),
        ("HealOverTimeSpells", "Heal Over Time", 0),
    };

    private static readonly (string Key, string Label, int DefaultValue)[] SocialSettings =
    {
        ("BadWord", "Bad Word Filter", 1),
        ("Spam", "Spam Filter", 1),
        ("FellowshipChat", "Fellowship Chat", 0),
        ("MercenaryMessages", "Mercenary Messages", 1),
        ("ItemSpeech", "Item Speech", 0),
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
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Chat Spam Filters", new Size(480, 530));
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = true;

        int y = 8;

        // ─── Combat card ──────────────────────────────────────────
        y = AddFilterCard(this, "\u2694", "Combat & Melee", DarkTheme.CardRed, y, CombatSettings);

        // ─── Pets card ────────────────────────────────────────────
        y = AddFilterCard(this, "\uD83D\uDC3E", "Pets", DarkTheme.CardGreen, y, PetSettings);

        // ─── Spells card ──────────────────────────────────────────
        y = AddFilterCard(this, "\u2728", "Spells & Effects", DarkTheme.CardPurple, y, SpellSettings);

        // ─── Social card ──────────────────────────────────────────
        y = AddFilterCard(this, "\uD83D\uDCAC", "Social & Misc", DarkTheme.CardGold, y, SocialSettings);

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

    /// <summary>Add a card with two columns of filter checkboxes. Returns next Y.</summary>
    private int AddFilterCard(Control parent, string emoji, string title, Color titleColor, int y,
        (string Key, string Label, int DefaultValue)[] settings)
    {
        int rows = (settings.Length + 1) / 2;
        int cardH = 30 + rows * 22 + 6;
        var card = DarkTheme.MakeCard(parent, emoji, title, titleColor, 10, y, 440, cardH);
        int cy = 30;

        for (int i = 0; i < settings.Length; i += 2)
        {
            var (key1, label1, def1) = settings[i];
            var chk1 = DarkTheme.AddCardCheckBox(card, label1, 10, cy);
            chk1.Checked = _config.EQClientIni.ChatSpamOverrides.TryGetValue(key1, out int val1)
                ? val1 == 1
                : def1 == 1;
            _checkBoxes[key1] = chk1;

            if (i + 1 < settings.Length)
            {
                var (key2, label2, def2) = settings[i + 1];
                var chk2 = DarkTheme.AddCardCheckBox(card, label2, 220, cy);
                chk2.Checked = _config.EQClientIni.ChatSpamOverrides.TryGetValue(key2, out int val2)
                    ? val2 == 1
                    : def2 == 1;
                _checkBoxes[key2] = chk2;
            }

            cy += 22;
        }

        return y + cardH + 8;
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

                if (_checkBoxes.TryGetValue(key, out var chk))
                    chk.Checked = val == "1";
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
            _config.EQClientIni.ChatSpamOverrides[key] = chk.Checked ? 1 : 0;
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
                EQClientSettingsForm.SetIniValue(lines, "Options", key, chk.Checked ? "1" : "0");

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
            EQClientSettingsForm.SetIniValue(lines, "Options", key, value != 0 ? "1" : "0");
    }
}
