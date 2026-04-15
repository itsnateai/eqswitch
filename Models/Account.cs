namespace EQSwitch.Models;

/// <summary>
/// Login credentials for an EverQuest account. Unique by (Username, Server).
/// Holds DPAPI-encrypted password (base64). One Account can back zero or more
/// Characters (each Character is a specific play target on this Account).
/// Launching an Account from the tray logs in and stops at character select.
/// </summary>
public class Account
{
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";
    public string Server { get; set; } = "Dalaya";
    public bool UseLoginFlag { get; set; } = true;

    /// <summary>User-facing display label. Falls back Name → Username → literal placeholder.
    /// Never empty — WinForms menu items with empty Text are unclickable.</summary>
    public string EffectiveLabel
    {
        get
        {
            if (!string.IsNullOrEmpty(Name)) return Name;
            if (!string.IsNullOrEmpty(Username)) return Username;
            return "(unnamed account)";
        }
    }

    /// <summary>Disambiguating tooltip for the tray menu. Distinguishes Accounts that share
    /// the same display Name across servers.</summary>
    public string Tooltip => $"{Username}@{Server}";
}
