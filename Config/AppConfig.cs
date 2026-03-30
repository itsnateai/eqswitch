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

    // Picture-in-Picture
    public PipConfig Pip { get; set; } = new();

    // Characters
    public List<CharacterProfile> Characters { get; set; } = new();

    // Video
    public List<string> CustomVideoPresets { get; set; } = new();

    // Paths
    public string GinaPath { get; set; } = "";
    public string DalayaPatcherPath { get; set; } = "";
    public string NotesPath { get; set; } = "";

    // Tray Click Actions
    public TrayClickConfig TrayClick { get; set; } = new();

    /// <summary>
    /// Custom tray icon path. Empty = use built-in Stone icon (default).
    /// Users can browse to any .ico file on their system.
    /// </summary>
    public string CustomIconPath { get; set; } = "";

    // Misc
    public bool ShowTooltipErrors { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool RunAtStartup { get; set; } = false;


    /// <summary>Last Settings window position [x, y]. Empty = center screen.</summary>
    public int[] SettingsWindowPos { get; set; } = Array.Empty<int>();

    /// <summary>Duration in ms for floating tooltips (default 1500ms).</summary>
    public int TooltipDurationMs { get; set; } = 2000;

    /// <summary>Show help tooltip when Ctrl+hovering the tray icon.</summary>
    public bool CtrlHoverHelp { get; set; } = true;

    /// <summary>
    /// When true, sets Log=FALSE in eqclient.ini [Defaults] section.
    /// Prevents EQ from writing large log files to disk.
    /// </summary>
    public bool DisableEQLog { get; set; } = false;

    /// <summary>
    /// Persistent eqclient.ini overrides — applied on every save/launch.
    /// </summary>
    public EQClientIniConfig EQClientIni { get; set; } = new();

    /// <summary>
    /// Clamp all numeric values to safe ranges. Call after deserialization
    /// or before applying settings from the GUI.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EQProcessName)) EQProcessName = "eqgame";
        // Guard against null nested objects from corrupt/hand-edited JSON
        Layout ??= new();
        Affinity ??= new();
        Launch ??= new();
        Pip ??= new();
        Hotkeys ??= new();
        TrayClick ??= new();
        EQClientIni ??= new();
        Characters ??= new();

        Layout.Columns = Math.Clamp(Layout.Columns, 1, 4);
        Layout.Rows = Math.Clamp(Layout.Rows, 1, 4);
        Layout.TargetMonitor = Math.Clamp(Layout.TargetMonitor, 0, 8);
        Layout.TopOffset = Math.Clamp(Layout.TopOffset, -200, 200);

        Affinity.LaunchRetryCount = Math.Clamp(Affinity.LaunchRetryCount, 0, 20);
        Affinity.LaunchRetryDelayMs = Math.Clamp(Affinity.LaunchRetryDelayMs, 500, 30000);

        Launch.NumClients = Math.Clamp(Launch.NumClients, 1, 8);
        Launch.LaunchDelayMs = Math.Clamp(Launch.LaunchDelayMs, 500, 30000);
        Launch.FixDelayMs = Math.Clamp(Launch.FixDelayMs, 1000, 120000);

        Pip.Opacity = Math.Clamp(Pip.Opacity, (byte)10, (byte)255);
        Pip.MaxWindows = Math.Clamp(Pip.MaxWindows, 1, 3);
        Pip.CustomWidth = Math.Clamp(Pip.CustomWidth, 100, 1920);
        Pip.CustomHeight = Math.Clamp(Pip.CustomHeight, 75, 1080);
    }
}

public class WindowLayout
{
    public int Columns { get; set; } = 1;
    public int Rows { get; set; } = 1;
    public bool RemoveTitleBars { get; set; } = false;
    public bool BorderlessFullscreen { get; set; } = false;
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
    /// Process priority for all EQ clients.
    /// High required to prevent virtual desktop crashes and keep autofollow working.
    /// </summary>
    public string ActivePriority { get; set; } = "AboveNormal";

    /// <summary>
    /// Process priority for background EQ clients (kept in sync with ActivePriority).
    /// </summary>
    public string BackgroundPriority { get; set; } = "AboveNormal";

    /// <summary>
    /// Number of retry attempts when applying priority to a newly launched client.
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
    public string ArrangeWindows { get; set; } = "";

    /// <summary>Toggle single-screen / multi-monitor mode.</summary>
    public string ToggleMultiMonitor { get; set; } = "Alt+M";

    /// <summary>Launch one EQ client.</summary>
    public string LaunchOne { get; set; } = "";

    /// <summary>Launch all configured EQ clients.</summary>
    public string LaunchAll { get; set; } = "";

    public bool MultiMonitorEnabled { get; set; } = false;

    /// <summary>
    /// Switch key behavior: "swapLast" (Alt+Tab style, swap between last two) or "cycleAll" (round-robin).
    /// </summary>
    public string SwitchKeyMode { get; set; } = "swapLast";
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
    public byte Opacity { get; set; } = 245;

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
        "Green" => Color.FromArgb(34, 180, 85),
        "Blue" => Color.FromArgb(60, 140, 230),
        "Red" => Color.FromArgb(220, 50, 50),
        "Black" => Color.Black,
        _ => Color.FromArgb(34, 180, 85)
    };
}

public class TrayClickConfig
{
    /// <summary>
    /// Action for single left-click on tray icon.
    /// Values: "None", "FixWindows", "SwapWindows", "TogglePiP", "LaunchOne", "LaunchAll", "Settings", "ShowHelp"
    /// </summary>
    public string SingleClick { get; set; } = "LaunchOne";

    /// <summary>
    /// Action for double left-click on tray icon.
    /// </summary>
    public string DoubleClick { get; set; } = "None";

    /// <summary>
    /// Action for single middle-click on tray icon.
    /// </summary>
    public string MiddleClick { get; set; } = "TogglePiP";

    /// <summary>
    /// Action for double middle-click on tray icon.
    /// </summary>
    public string MiddleDoubleClick { get; set; } = "Settings";
}

public class CharacterProfile
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

/// <summary>
/// Persistent eqclient.ini overrides.
/// When a setting is enabled here, EQSwitch enforces it in eqclient.ini on save.
/// </summary>
public class EQClientIniConfig
{
    /// <summary>Disable all EQ sound (Sound=FALSE in [Defaults]).</summary>
    public bool DisableSound { get; set; } = false;

    /// <summary>Disable music (Music=0 in [Defaults]). 0 = off, 1 = on.</summary>
    public bool DisableMusic { get; set; } = false;

    /// <summary>Sound volume (SoundVolume in [Defaults]). 0 = mute, range 0-100. -1 = don't override.</summary>
    public int SoundVolume { get; set; } = -1;

    /// <summary>Disable environment sounds (EnvSounds=0 in [Defaults]).</summary>
    public bool DisableEnvSounds { get; set; } = false;

    /// <summary>Disable combat music (CombatMusic=0 in [Defaults]).</summary>
    public bool DisableCombatMusic { get; set; } = false;

    /// <summary>Disable Windows auto-ducking of EQ audio (AllowAutoDuck=0 in [Defaults]).</summary>
    public bool DisableAutoDuck { get; set; } = false;

    /// <summary>Set sky update interval in ms (SkyUpdateInterval=60000 in [Defaults]).</summary>
    public bool SlowSkyUpdates { get; set; } = false;

    /// <summary>Disable sky rendering (Sky=0 in [Defaults]). Performance boost.</summary>
    public bool DisableSky { get; set; } = false;

    /// <summary>Enable persistent bard songs (BardSongs=1 in [Defaults]).</summary>
    public bool BardSongs { get; set; } = false;

    /// <summary>Enable bard songs on pets (BardSongsOnPets=1 in [Defaults]).</summary>
    public bool BardSongsOnPets { get; set; } = false;

    /// <summary>Shadow clip plane distance (ShadowClipPlane in [Defaults]). 0 = don't override.</summary>
    public int ShadowClipPlane { get; set; } = 0;

    /// <summary>Actor clip plane distance (ActorClipPlane in [Defaults]). 0 = don't override.</summary>
    public int ActorClipPlane { get; set; } = 0;

    /// <summary>Auto-attack when assisting (AttackOnAssist=TRUE in [Defaults]).</summary>
    public bool AttackOnAssist { get; set; } = false;

    /// <summary>Show inspect message (ShowInspectMessage=TRUE in [Defaults]).</summary>
    public bool ShowInspectMessage { get; set; } = false;

    /// <summary>Show grass (ShowGrass=TRUE in [Defaults]).</summary>
    public bool ShowGrass { get; set; } = false;

    /// <summary>Show ping bar / network stats (NetStat=TRUE in [Defaults]).</summary>
    public bool NetStat { get; set; } = false;

    /// <summary>Auto-update tracking window position (TrackAutoUpdate=TRUE in [Defaults]).</summary>
    public bool TrackAutoUpdate { get; set; } = false;

    /// <summary>Target Group Buff (TargetGroupBuff=1 in [Defaults]).</summary>
    public bool TargetGroupBuff { get; set; } = false;

    /// <summary>Disable mip-mapping (MipMapping=FALSE in [Defaults]).</summary>
    public bool DisableMipMapping { get; set; } = false;

    /// <summary>Enable texture cache (TextureCache=TRUE in [Defaults]).</summary>
    public bool TextureCache { get; set; } = false;

    /// <summary>Use D3D texture compression (UseD3DTextureCompression=TRUE in [Defaults]).</summary>
    public bool UseD3DTextureCompression { get; set; } = false;

    /// <summary>Disable dynamic lights (ShowDynamicLights=FALSE in [Defaults]).</summary>
    public bool DisableDynamicLights { get; set; } = false;

    /// <summary>Use lit batches (UseLitBatches=TRUE in [Defaults]).</summary>
    public bool UseLitBatches { get; set; } = false;

    /// <summary>Disable inspect others (InspectOthers=FALSE in [Defaults]).</summary>
    public bool DisableInspectOthers { get; set; } = false;

    /// <summary>Anonymous mode (Anonymous=1 in [Defaults]).</summary>
    public bool Anonymous { get; set; } = false;

    /// <summary>Clip plane distance (ClipPlane in [Defaults]). 0 = don't override.</summary>
    public int ClipPlane { get; set; } = 0;

    /// <summary>Mouse sensitivity (MouseSensitivity in [Defaults]). -1 = don't override.</summary>
    public int MouseSensitivity { get; set; } = -1;

    /// <summary>Disable loot all confirmation (LootAllConfirm=0 in [Defaults]).</summary>
    public bool DisableLootAllConfirm { get; set; } = false;

    /// <summary>Confirm raid invites (RaidInviteConfirm=1 in [Defaults]).</summary>
    public bool RaidInviteConfirm { get; set; } = false;

    /// <summary>Disable AA confirmation dialog (AANoConfirm=0 in [Defaults]).</summary>
    public bool AANoConfirm { get; set; } = false;

    /// <summary>Disable chat server (ChatServerPort=0 in [Defaults]). Disables the built-in chat channel server.</summary>
    public bool DisableChatServer { get; set; } = false;

    /// <summary>Force windowed mode (WindowedMode=TRUE in [VideoMode]).</summary>
    public bool ForceWindowedMode { get; set; } = true;

    /// <summary>Max foreground FPS (MaxFPS in [Defaults]). Default 80.</summary>
    public int MaxFPS { get; set; } = 80;

    /// <summary>Max background FPS (MaxBGFPS in [Defaults]). Default 80.</summary>
    public int MaxBGFPS { get; set; } = 80;

    /// <summary>
    /// Luclin model overrides. Key = INI key name, Value = TRUE/FALSE.
    /// Stored in [Defaults] section of eqclient.ini.
    /// </summary>
    public Dictionary<string, bool> ModelOverrides { get; set; } = new();

    /// <summary>
    /// Chat spam filter overrides. Key = INI key name (e.g. "BadWord", "Spam"),
    /// Value = 0 or 1. Stored in [Defaults] section of eqclient.ini.
    /// </summary>
    public Dictionary<string, int> ChatSpamOverrides { get; set; } = new();

    /// <summary>
    /// Particle/opacity overrides. Key = INI key name, Value = string representation.
    /// Stored in [Defaults] section of eqclient.ini.
    /// Floats stored as "0.000000" format, ints as "1", bools as "true"/"false".
    /// </summary>
    public Dictionary<string, string> ParticleOverrides { get; set; } = new();

    /// <summary>
    /// Video mode overrides. Key = INI key name, Value = string representation.
    /// Stored in [VideoMode] section of eqclient.ini.
    /// </summary>
    public Dictionary<string, string> VideoModeOverrides { get; set; } = new();

    /// <summary>
    /// Tracks which main-form settings the user has explicitly saved.
    /// EnforceOverrides only writes keys in this set — prevents clobbering
    /// manual INI edits for settings the user never touched in EQSwitch.
    /// Empty on fresh install = nothing enforced until first Save.
    /// </summary>
    public HashSet<string> ConfiguredKeys { get; set; } = new();

    /// <summary>
    /// CPU core assignments for EQ's 6 affinity slots (CPUAffinity0-5 in eqclient.ini).
    /// Each value is a physical core number (0-based). Default: cores 1,2,3,1,2,3 (skip core 0 for OS).
    /// </summary>
    public int[] CPUAffinitySlots { get; set; } = { 1, 2, 3, 1, 2, 3 };
}
