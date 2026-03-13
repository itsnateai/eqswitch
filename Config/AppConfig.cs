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

    // Launching
    public LaunchConfig Launch { get; set; } = new();

    // Background FPS Throttling
    public ThrottleConfig Throttle { get; set; } = new();

    // Picture-in-Picture
    public PipConfig Pip { get; set; } = new();

    // Characters
    public List<CharacterProfile> Characters { get; set; } = new();

    // Paths
    public string GinaPath { get; set; } = "";
    public string NotesPath { get; set; } = "";

    // Misc
    public bool ShowTooltipErrors { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool RunAtStartup { get; set; } = false;
    public int PollingIntervalMs { get; set; } = 500;

    /// <summary>
    /// Clamp all numeric values to safe ranges. Call after deserialization
    /// or before applying settings from the GUI.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EQProcessName)) EQProcessName = "eqgame";
        PollingIntervalMs = Math.Clamp(PollingIntervalMs, 100, 10000);

        Layout.Columns = Math.Clamp(Layout.Columns, 1, 4);
        Layout.Rows = Math.Clamp(Layout.Rows, 1, 4);
        Layout.TargetMonitor = Math.Clamp(Layout.TargetMonitor, 0, 8);
        Layout.TopOffset = Math.Clamp(Layout.TopOffset, -200, 200);

        if (Affinity.ActiveMask <= 0) Affinity.ActiveMask = 0xFF;
        if (Affinity.BackgroundMask <= 0) Affinity.BackgroundMask = 0xFF00;
        Affinity.LaunchRetryCount = Math.Clamp(Affinity.LaunchRetryCount, 0, 20);
        Affinity.LaunchRetryDelayMs = Math.Clamp(Affinity.LaunchRetryDelayMs, 500, 30000);

        Launch.NumClients = Math.Clamp(Launch.NumClients, 1, 8);
        Launch.LaunchDelayMs = Math.Clamp(Launch.LaunchDelayMs, 500, 30000);
        Launch.FixDelayMs = Math.Clamp(Launch.FixDelayMs, 1000, 120000);

        Pip.Opacity = Math.Clamp(Pip.Opacity, (byte)10, (byte)255);
        Pip.MaxWindows = Math.Clamp(Pip.MaxWindows, 1, 3);
        Pip.CustomWidth = Math.Clamp(Pip.CustomWidth, 100, 1920);
        Pip.CustomHeight = Math.Clamp(Pip.CustomHeight, 75, 1080);

        Throttle.ThrottlePercent = Math.Clamp(Throttle.ThrottlePercent, 0, 90);
        Throttle.CycleIntervalMs = Math.Clamp(Throttle.CycleIntervalMs, 50, 1000);
    }
}

public class WindowLayout
{
    public int Columns { get; set; } = 2;
    public int Rows { get; set; } = 2;
    public bool RemoveTitleBars { get; set; } = false;
    public bool SnapToMonitor { get; set; } = true;
    public int TargetMonitor { get; set; } = 0; // 0 = primary

    /// <summary>
    /// Pixel offset added to Y position when arranging windows.
    /// Equivalent to AHK's FIX_TOP_OFFSET — adjusts for taskbar/title bars/bezels.
    /// </summary>
    public int TopOffset { get; set; } = 0;

    /// <summary>
    /// Current layout mode: "single" (all on one monitor) or "multimonitor" (one per monitor).
    /// </summary>
    public string Mode { get; set; } = "single";
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

    /// <summary>
    /// Process priority for the active EQ client.
    /// Values: "Normal", "AboveNormal", "High"
    /// </summary>
    public string ActivePriority { get; set; } = "AboveNormal";

    /// <summary>
    /// Process priority for background EQ clients.
    /// </summary>
    public string BackgroundPriority { get; set; } = "Normal";

    /// <summary>
    /// Number of retry attempts when applying affinity to a newly launched client.
    /// EQ resets its affinity shortly after startup, so we re-apply.
    /// </summary>
    public int LaunchRetryCount { get; set; } = 3;

    /// <summary>
    /// Delay in ms between retry attempts.
    /// </summary>
    public int LaunchRetryDelayMs { get; set; } = 2000;
}

public class HotkeyConfig
{
    /// <summary>
    /// Hotkey strings use the format: "Modifier+Key" e.g. "Alt+1", "Ctrl+F1"
    /// Single keys (no modifier) like "\" or "]" use a low-level keyboard hook instead.
    /// </summary>

    /// <summary>
    /// Cycle to next EQ client — only fires when EQ is focused (like AHK HotIfWinActive).
    /// Uses low-level keyboard hook since it's a single key with no modifier.
    /// </summary>
    public string SwitchKey { get; set; } = @"\";

    /// <summary>
    /// Global switch — if EQ is focused, cycles next. If not, brings EQ to front.
    /// Uses low-level keyboard hook since it's a single key with no modifier.
    /// </summary>
    public string GlobalSwitchKey { get; set; } = "]";

    /// <summary>
    /// Alt+1 through Alt+6 — jump directly to a client by slot number.
    /// Uses RegisterHotKey (modifier-based).
    /// </summary>
    public List<string> DirectSwitchKeys { get; set; } = new() { "Alt+1", "Alt+2", "Alt+3", "Alt+4", "Alt+5", "Alt+6" };

    /// <summary>Arrange all EQ windows in a grid layout.</summary>
    public string ArrangeWindows { get; set; } = "Alt+G";

    /// <summary>Toggle single-screen / multi-monitor mode.</summary>
    public string ToggleMultiMonitor { get; set; } = "Alt+M";

    /// <summary>Launch one EQ client.</summary>
    public string LaunchOne { get; set; } = "";

    /// <summary>Launch all configured EQ clients.</summary>
    public string LaunchAll { get; set; } = "";

    public bool MultiMonitorEnabled { get; set; } = false;
}

public class LaunchConfig
{
    /// <summary>EQ executable name (usually "eqgame.exe").</summary>
    public string ExeName { get; set; } = "eqgame.exe";

    /// <summary>Command-line arguments for the EQ client (e.g. "patchme").</summary>
    public string Arguments { get; set; } = "patchme";

    /// <summary>Number of clients to launch with "Launch All" (1-8).</summary>
    public int NumClients { get; set; } = 2;

    /// <summary>Delay in ms between launching each client.</summary>
    public int LaunchDelayMs { get; set; } = 3000;

    /// <summary>Delay in ms after all clients launched before arranging windows.</summary>
    public int FixDelayMs { get; set; } = 15000;
}

public class PipConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Size preset: "Small", "Medium", "Large", "XL", "XXL", "Custom"</summary>
    public string SizePreset { get; set; } = "Medium";

    /// <summary>Custom width (used when SizePreset = "Custom").</summary>
    public int CustomWidth { get; set; } = 320;

    /// <summary>Custom height (used when SizePreset = "Custom").</summary>
    public int CustomHeight { get; set; } = 240;

    /// <summary>Opacity (0-255). 255 = fully opaque.</summary>
    public byte Opacity { get; set; } = 200;

    /// <summary>Show colored border around PiP windows.</summary>
    public bool ShowBorder { get; set; } = true;

    /// <summary>Border color name: "Green", "Blue", "Red", "Black".</summary>
    public string BorderColor { get; set; } = "Green";

    /// <summary>Max number of PiP windows to show (1-3).</summary>
    public int MaxWindows { get; set; } = 3;

    /// <summary>Saved positions (X,Y pairs per slot).</summary>
    public List<int[]> SavedPositions { get; set; } = new();

    public (int w, int h) GetSize() => SizePreset switch
    {
        "Small" => (200, 150),
        "Medium" => (320, 240),
        "Large" => (400, 300),
        "XL" => (480, 360),
        "XXL" => (640, 480),
        _ => (CustomWidth, CustomHeight)
    };

    public Color GetBorderColor() => BorderColor switch
    {
        "Green" => Color.FromArgb(0, 255, 0),
        "Blue" => Color.FromArgb(0, 128, 255),
        "Red" => Color.FromArgb(255, 0, 0),
        "Black" => Color.Black,
        _ => Color.FromArgb(0, 255, 0)
    };
}

public class ThrottleConfig
{
    /// <summary>
    /// Enable background FPS throttling via process suspension.
    /// When enabled, background EQ clients are duty-cycled (suspended/resumed)
    /// to reduce GPU/CPU usage.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Percentage of time background processes are suspended (0-90).
    /// Higher = more throttling = lower background FPS.
    /// 0 = no throttling, 50 = half FPS, 75 = quarter FPS, 90 = ~10% FPS.
    /// </summary>
    public int ThrottlePercent { get; set; } = 50;

    /// <summary>
    /// Base cycle interval in ms for the suspend/resume duty cycle.
    /// Lower = smoother but more overhead. Default 100ms.
    /// </summary>
    public int CycleIntervalMs { get; set; } = 100;
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
