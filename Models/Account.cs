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
}
