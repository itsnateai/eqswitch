// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Chat spam filter settings (all [Options], 1/0). Phase 2 of the EQ Client Settings overhaul: this
/// form is now on the shared schema engine — display is a LIVE read of eqclient.ini, Save writes ONLY
/// the boxes the user changed (touch-gated; it used to write all 22 every save), and it is no longer
/// re-stamped at launch (eqgame wins after first set). The grouped key-lists below are display layout
/// only; each key's label, polarity, and default live once in EqClientIniSchema.
/// </summary>
public class EQChatSpamForm : EqSwitchForm
{
    private readonly AppConfig _config;
    private readonly string _iniPath;
    private readonly List<EqClientBinding> _bindings = new();

    private static readonly Dictionary<string, IniSetting> SchemaByKey =
        EqClientIniSchema.All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    // Display grouping + order only (4 cards × 2 columns). Labels/defaults/polarity come from the schema.
    private static readonly string[] CombatKeys =
        { "CriticalSpells", "CriticalMelee", "SpellDamage", "DotDamage", "HideDamageShield", "Strikethrough", "Stun" };
    private static readonly string[] PetKeys =
        { "PetAttacks", "PetMisses", "PetSpells", "SwarmPetDeath" };
    private static readonly string[] SpellKeys =
        { "PCSpells", "NPCSpells", "FocusEffects", "HealOverTimeSpells" };
    private static readonly string[] SocialKeys =
        { "BadWord", "Spam", "FellowshipChat", "MercenaryMessages", "ItemSpeech", "Achievements", "PvPMessages" };

    public EQChatSpamForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "EQSwitch — Chat Spam Filters — EXPERIMENTAL", new Size(480, 530));
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = true;

        int y = 8;

        // ─── Filter cards (grouping/order only — schema owns label/default/polarity) ──────────────
        y = AddFilterCard(this, "⚔", "Combat & Melee", DarkTheme.CardRed, y, CombatKeys);
        y = AddFilterCard(this, "🐾", "Pets", DarkTheme.CardGreen, y, PetKeys);
        y = AddFilterCard(this, "✨", "Spells & Effects", DarkTheme.CardPurple, y, SpellKeys);
        y = AddFilterCard(this, "💬", "Social & Misc", DarkTheme.CardGold, y, SocialKeys);

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

        // Size the form to its content so the button bar sits a consistent gap below the Social card.
        FitClientHeightToContent();
    }

    /// <summary>Add a card with two columns of schema-bound filter checkboxes. Returns next Y.</summary>
    private int AddFilterCard(Control parent, string emoji, string title, Color titleColor, int y, string[] keys)
    {
        int rows = (keys.Length + 1) / 2;
        int cardH = 30 + rows * 22 + 6;
        var card = DarkTheme.MakeCard(parent, emoji, title, titleColor, 10, y, 440, cardH);
        int cy = 30;

        for (int i = 0; i < keys.Length; i += 2)
        {
            AddBoundCheck(card, keys[i], 10, cy);
            if (i + 1 < keys.Length)
                AddBoundCheck(card, keys[i + 1], 220, cy);
            cy += 22;
        }

        return y + cardH + 8;
    }

    /// <summary>Create a checkbox labelled from the schema and register it as a touch-gated binding.</summary>
    private void AddBoundCheck(Panel card, string key, int x, int y)
    {
        var setting = SchemaByKey[key];
        var chk = DarkTheme.AddCardCheckBox(card, setting.Label, x, y);
        _bindings.Add(new EqClientBinding(setting, chk, null));
    }

    private void LoadFromIni()
    {
        // Live read of eqclient.ini via the shared schema engine; snapshots each value for touch-gating.
        try
        {
            EqClientBindings.LoadInto(_bindings, _iniPath);
            FileLogger.Info("EQChatSpam: loaded current values from eqclient.ini (schema-driven)");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQChatSpam: load error", ex);
        }
    }

    private void SaveSettings()
    {
        // Touch-gated, schema-driven write — only the filters the user changed are written to [Options],
        // one canonical section per key. Untouched + unmanaged keys are left exactly as eqgame/the user
        // left them (point D: no clobber). No launch re-stamp — eqgame wins after.
        try
        {
            if (!File.Exists(_iniPath))
            {
                FileLogger.Info($"EQChatSpam: eqclient.ini not found at {_iniPath}");
                return;
            }

            int changed = EqClientBindings.SaveChanged(_bindings, _iniPath);
            FileLogger.Info(changed > 0
                ? $"EQChatSpam: wrote {changed} changed filter(s) to eqclient.ini (touch-gated)"
                : "EQChatSpam: no changes to save");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQChatSpam: save error", ex);
            ThemedMessageDialog.Show(this, $"Failed to update eqclient.ini: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }
}
