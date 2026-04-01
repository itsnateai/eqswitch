namespace EQSwitch.Models;

/// <summary>
/// Stored account for auto-login. Password is DPAPI-encrypted (base64).
/// </summary>
public class LoginAccount
{
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";
    public string Server { get; set; } = "Dalaya";
    public string CharacterName { get; set; } = "";
    public int CharacterSlot { get; set; } = 1;
    public bool UseLoginFlag { get; set; } = true;
}
