// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

namespace EQSwitch.Models;

/// <summary>
/// Login credentials for an EverQuest account. Unique by (Username, Server).
/// Holds DPAPI-encrypted password (base64). One Account can back zero or more
/// Characters (each Character is a specific play target on this Account).
/// Launching an Account from the tray logs in and stops at character select.
/// </summary>
public class Account
{
    // Name is the persisted FK identity referenced by hotkey TargetName,
    // team-slot SlotOption.Value, tray-menu binding, and config FK lookups
    // (AccountHotkeysDialog / AutoLoginTeamsDialog / TrayManager / SettingsForm).
    // Treat as opaque — never surface for user edit. For new accounts
    // AccountEditDialog auto-shadows Name = Username so bindings have a stable
    // key from day one. Pre-v3.14.8 accounts may hold a legacy custom display
    // string here; AppConfig.Validate copies that string into Notes once for
    // display continuity but leaves Name unchanged so existing bindings resolve.
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";
    public string Server { get; set; } = "Dalaya";
    public bool UseLoginFlag { get; set; } = true;

    /// <summary>User-facing free-form note shown in the Accounts grid's "Notes"
    /// column. Decoupled from Name (the FK identity) since v3.14.8 — editing
    /// this never touches binding resolution.</summary>
    public string Notes { get; set; } = "";

    /// <summary>User-facing display label. Username-first since v3.14.8 — Name
    /// is now an internal FK shadow and may hold a legacy custom display string
    /// on pre-v3.14.8 accounts that the user no longer recognizes. Username is
    /// the stable login identity. Falls back Username → Name → placeholder.
    /// Never empty — WinForms menu items with empty Text are unclickable.</summary>
    public string EffectiveLabel
    {
        get
        {
            if (!string.IsNullOrEmpty(Username)) return Username;
            if (!string.IsNullOrEmpty(Name)) return Name;
            return "(unnamed account)";
        }
    }

    /// <summary>Disambiguating tooltip for the tray menu. Distinguishes Accounts that share
    /// the same display Name across servers.</summary>
    public string Tooltip => $"{Username}@{Server}";
}
