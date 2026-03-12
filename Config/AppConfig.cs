using System.Text.Json.Serialization;

namespace EQSwitch.Config;

/// <summary>
/// Root configuration object. Stored as eqswitch-config.json alongside the exe.
/// Replaces the old INI-based config — no more type comparison bugs.
/// </summary>
public class AppConfig
{
    public bool IsFirstRun { get; set; } = true;
    public string EQPath { get; set; } = @"C:\EverQuest";
    public string EQProcessName { get; set; } = "eqgame";

    // Window layout
    public WindowLayout Layout { get; set; } = new();

    // CPU affinity
    public AffinityConfig Affinity { get; set; } = new();

    // Hotkeys
    public HotkeyConfig Hotkeys { get; set; } = new();

    // Characters
    public List<CharacterProfile> Characters { get; set; } = new();

    // Misc
    public bool ShowTooltipErrors { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public int PollingIntervalMs { get; set; } = 500;
}

public class WindowLayout
{
    public int Columns { get; set; } = 2;
    public int Rows { get; set; } = 2;
    public bool RemoveTitleBars { get; set; } = false;
    public bool SnapToMonitor { get; set; } = true;
    public int TargetMonitor { get; set; } = 0; // 0 = primary
}

public class AffinityConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Bitmask for the active/foreground EQ client.
    /// Default: P-cores on a 12th+ gen Intel (cores 0-7 = 0xFF)
    /// </summary>
    public long ActiveMask { get; set; } = 0xFF;

    /// <summary>
    /// Bitmask for background EQ clients.
    /// Default: E-cores on a 12th+ gen Intel (cores 8-15 = 0xFF00)
    /// </summary>
    public long BackgroundMask { get; set; } = 0xFF00;
}

public class HotkeyConfig
{
    /// <summary>
    /// Hotkey strings use the format: "Modifier+Key" e.g. "Alt+1", "Ctrl+F1"
    /// Parsed by HotkeyManager at registration time.
    /// </summary>
    public string CycleNextClient { get; set; } = "Alt+Tab";
    public string CyclePrevClient { get; set; } = "Alt+Shift+Tab";
    public List<string> DirectSwitchKeys { get; set; } = new() { "Alt+1", "Alt+2", "Alt+3", "Alt+4", "Alt+5", "Alt+6" };
    public string ArrangeWindows { get; set; } = "Alt+G";
    public string ToggleNotes { get; set; } = "MButton"; // Middle-click
}

public class CharacterProfile
{
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string Notes { get; set; } = "";
    public int SlotIndex { get; set; } = 0;

    /// <summary>
    /// Optional per-character affinity override.
    /// Null = use global affinity settings.
    /// </summary>
    public long? AffinityOverride { get; set; } = null;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(Class) ? Name : $"{Name} ({Class})";
}
