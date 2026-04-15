using System.Text.Json.Serialization;

namespace EQSwitch.Models;

/// <summary>
/// Cosmetic and runtime metadata for a character: display class for tooltips,
/// notes, and an optional per-character priority override consumed by
/// AffinityManager. Unrelated to the launch-target Character type — this is
/// the renamed v3 CharacterProfile, kept as a distinct concept because
/// affinity/display data is orthogonal to launch intent.
/// </summary>
public class CharacterAlias
{
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string Notes { get; set; } = "";
    public int SlotIndex { get; set; } = 0;

    /// <summary>
    /// Optional per-character priority override.
    /// Null = use global priority settings. Values: "Normal", "AboveNormal", "High".
    /// </summary>
    public string? PriorityOverride { get; set; } = null;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(Class) ? Name : $"{Name} ({Class})";
}
