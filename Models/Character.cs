namespace EQSwitch.Models;

/// <summary>
/// A specific play target on an Account. Launching a Character from the tray
/// logs into the backing Account, selects this character, and enters world.
/// References its Account by (AccountUsername, AccountServer) — Username alone
/// is not sufficient when the same login is reused across servers.
/// </summary>
public class Character
{
    public string Name { get; set; } = "";
    public string AccountUsername { get; set; } = "";
    public string AccountServer { get; set; } = "Dalaya";

    /// <summary>
    /// 0 = auto (heap-scan name match — preferred). 1-10 = explicit slot
    /// (legacy fallback for characters whose names can't be read from heap).
    /// </summary>
    public int CharacterSlot { get; set; } = 0;

    public string DisplayLabel { get; set; } = "";
    public string ClassHint { get; set; } = "";
    public string Notes { get; set; } = "";
}
