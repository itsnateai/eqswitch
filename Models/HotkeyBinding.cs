namespace EQSwitch.Models;

/// <summary>
/// One row in the AccountHotkeys / CharacterHotkeys lists. Combo is the
/// platform key string (e.g. "Alt+1"); empty means unbound. TargetName is
/// resolved against the matching list (Account.Name for AccountHotkeys,
/// Character.Name for CharacterHotkeys) at dispatch time.
/// </summary>
public class HotkeyBinding
{
    public string Combo { get; set; } = "";
    public string TargetName { get; set; } = "";
}
