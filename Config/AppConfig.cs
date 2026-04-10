using System.Text;
using System.Text.Json.Serialization;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.Config;

/// <summary>
/// Root configuration object. Stored as eqswitch-config.json alongside the exe.
/// Replaces the old INI-based config — no more type comparison bugs.
/// </summary>
public class AppConfig
{
    /// <summary>Schema version for config migration. Bump when making breaking changes.</summary>
    public const int CurrentConfigVersion = 2;

    public int ConfigVersion { get; set; } = CurrentConfigVersion;
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

    // Login Accounts (auto-login presets)
    public List<LoginAccount> Accounts { get; set; } = new();

    /// <summary>Delay in ms after EQ window appears before typing credentials (default 5s).</summary>
    public int LoginScreenDelayMs { get; set; } = 5000;

    /// <summary>When true, auto-login continues past character select into the world.
    /// When false, stops at the character select screen.</summary>
    public bool AutoEnterWorld { get; set; } = false;

    /// <summary>Username of the account bound to Quick Login slot 1 (empty = unbound).</summary>
    public string QuickLogin1 { get; set; } = "";

    /// <summary>Username of the account bound to Quick Login slot 2 (empty = unbound).</summary>
    public string QuickLogin2 { get; set; } = "";

    /// <summary>Username of the account bound to Quick Login slot 3 (empty = unbound).</summary>
    public string QuickLogin3 { get; set; } = "";

    /// <summary>Username of the account bound to Quick Login slot 4 (empty = unbound).</summary>
    public string QuickLogin4 { get; set; } = "";

    // Autologin Teams
    public string Team1Account1 { get; set; } = "";
    public string Team1Account2 { get; set; } = "";
    public string Team2Account1 { get; set; } = "";
    public string Team2Account2 { get; set; } = "";
    public string Team3Account1 { get; set; } = "";
    public string Team3Account2 { get; set; } = "";
    public string Team4Account1 { get; set; } = "";
    public string Team4Account2 { get; set; } = "";

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

    /// <summary>Duration in ms for floating tooltips (default 1000ms).</summary>
    public int TooltipDurationMs { get; set; } = 1000;

    /// <summary>Show help tooltip when Ctrl+hovering the tray icon.</summary>
    public bool CtrlHoverHelp { get; set; } = true;

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
        Accounts ??= new();
        Accounts.RemoveAll(a => a == null!);
        Characters.RemoveAll(c => c == null!);
        TooltipDurationMs = Math.Clamp(TooltipDurationMs, 100, 30000);

        Layout.TargetMonitor = Math.Clamp(Layout.TargetMonitor, 0, 8);
        Layout.SecondaryMonitor = Math.Clamp(Layout.SecondaryMonitor, -1, 8);
        Layout.TopOffset = Math.Clamp(Layout.TopOffset, -200, 200);
        Layout.TitlebarOffset = Math.Clamp(Layout.TitlebarOffset, 0, 40);
        Layout.BottomOffset = Math.Clamp(Layout.BottomOffset, 0, 100);

        Affinity.LaunchRetryCount = Math.Clamp(Affinity.LaunchRetryCount, 0, 20);
        Affinity.LaunchRetryDelayMs = Math.Clamp(Affinity.LaunchRetryDelayMs, 500, 30000);

        Launch.NumClients = Math.Clamp(Launch.NumClients, 1, 6);
        Launch.LaunchDelayMs = Math.Clamp(Launch.LaunchDelayMs, 500, 30000);
        Launch.FixDelayMs = Math.Clamp(Launch.FixDelayMs, 1000, 120000);

        LoginScreenDelayMs = Math.Clamp(LoginScreenDelayMs, 1000, 15000);

        Pip.Opacity = Math.Clamp(Pip.Opacity, (byte)10, (byte)255);
        Pip.MaxWindows = Math.Clamp(Pip.MaxWindows, 1, 3);
        Pip.CustomWidth = Math.Clamp(Pip.CustomWidth, 100, 3840);
        Pip.CustomHeight = Math.Clamp(Pip.CustomHeight, 75, 2160);
        // Migrate old 4:3 default (320x240) to 16:9 (480x270)
        if (Pip.CustomWidth == 320 && Pip.CustomHeight == 240)
        {
            Pip.CustomWidth = 480;
            Pip.CustomHeight = 270;
        }

        // String-enum validation — fall back to defaults on garbage values from hand-edited JSON
        if (Layout.Mode is not ("single" or "multimonitor")) Layout.Mode = "single";
        if (Pip.SizePreset is not ("Small" or "Medium" or "Large" or "Custom")) Pip.SizePreset = "Large";
        if (Pip.Orientation is not ("Horizontal" or "Vertical")) Pip.Orientation = "Vertical";
        if (Affinity.ActivePriority is not ("Normal" or "AboveNormal" or "High" or "BelowNormal"))
            Affinity.ActivePriority = "AboveNormal";
        if (Affinity.BackgroundPriority is not ("Normal" or "AboveNormal" or "High" or "BelowNormal"))
            Affinity.BackgroundPriority = "AboveNormal";
        if (Hotkeys.SwitchKeyMode is not ("swapLast" or "cycleFocused" or "cycleAll"))
            Hotkeys.SwitchKeyMode = "swapLast";

        // Array shape validation
        if (SettingsWindowPos is { Length: not (0 or 2) }) SettingsWindowPos = Array.Empty<int>();
        if (EQClientIni.CPUAffinitySlots is not { Length: 6 }) EQClientIni.CPUAffinitySlots = new[] { 1, 2, 3, 1, 2, 3 };
        for (int i = 0; i < 6; i++)
            EQClientIni.CPUAffinitySlots[i] = Math.Clamp(EQClientIni.CPUAffinitySlots[i], 0, 31);

        // LoginAccount field validation
        foreach (var a in Accounts)
            a.CharacterSlot = Math.Clamp(a.CharacterSlot, 1, 10);
    }
}

public class WindowLayout
{
    public bool SnapToMonitor { get; set; } = true;
    public int TargetMonitor { get; set; } = 0; // 0 = primary

    /// <summary>
    /// Secondary monitor index for multimonitor mode. Background client goes here.
    /// </summary>
    public int SecondaryMonitor { get; set; } = -1; // -1 = auto (first non-primary)

    /// <summary>
    /// Pixel offset added to Y position when arranging windows.
    /// Equivalent to AHK's FIX_TOP_OFFSET — adjusts for taskbar/title bars/bezels.
    /// </summary>
    public int TopOffset { get; set; } = 0;

    /// <summary>
    /// Current layout mode: "single" (all on one monitor) or "multimonitor" (one per monitor).
    /// </summary>
    public string Mode { get; set; } = "single";

    /// <summary>
    /// Slim titlebar mode: keeps WS_CAPTION but positions the window with a negative Y offset
    /// so the titlebar is partially hidden above the monitor edge. The game window is oversized
    /// by the offset amount so the visible game area fills the full monitor height.
    /// This is the WinEQ2 method for covering the taskbar while keeping a draggable titlebar.
    /// </summary>
    public bool SlimTitlebar { get; set; } = true;

    /// <summary>
    /// How many pixels of the titlebar to hide above the monitor edge (default 18).
    /// A standard Windows titlebar is ~30px. Hiding 18px leaves ~12px visible.
    /// Set to 0 for full titlebar, or up to 30 to hide it completely.
    /// </summary>
    public int TitlebarOffset { get; set; } = 13;

    /// <summary>
    /// How many pixels to subtract from the bottom of the game window height.
    /// Creates a gap at the bottom edge (useful for taskbar or chat box visibility).
    /// Only used when SlimTitlebar is enabled. Default 21.
    /// </summary>
    public int BottomOffset { get; set; } = 21;

    /// <summary>
    /// Custom window title template for EQ windows. Supports placeholders:
    /// {CHAR} = character name, {SLOT} = slot number (1-based), {PID} = process ID.
    /// Empty = don't modify window titles.
    /// </summary>
    public string WindowTitleTemplate { get; set; } = "";

    /// <summary>
    /// Inject eqswitch-hook.dll into eqgame.exe to hook SetWindowPos/MoveWindow from inside
    /// the process. Eliminates window position flicker during screen transitions.
    /// Only active when SlimTitlebar is also enabled. Falls back to guard timer if injection fails.
    /// </summary>
    public bool UseHook { get; set; } = true;
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
    public List<string> DirectSwitchKeys { get; set; } = new();

    /// <summary>Arrange all EQ windows in a grid layout.</summary>
    public string ArrangeWindows { get; set; } = "";

    /// <summary>Toggle single-screen / multi-monitor mode.</summary>
    public string ToggleMultiMonitor { get; set; } = "Alt+N";

    /// <summary>Launch one EQ client.</summary>
    public string LaunchOne { get; set; } = "";

    /// <summary>Launch all configured EQ clients.</summary>
    public string LaunchAll { get; set; } = "";

    /// <summary>Auto-login Quick Login slot 1.</summary>
    public string AutoLogin1 { get; set; } = "";

    /// <summary>Auto-login Quick Login slot 2.</summary>
    public string AutoLogin2 { get; set; } = "";

    /// <summary>Auto-login Quick Login slot 3.</summary>
    public string AutoLogin3 { get; set; } = "";

    /// <summary>Auto-login Quick Login slot 4.</summary>
    public string AutoLogin4 { get; set; } = "";

    /// <summary>Auto-login Team 1.</summary>
    public string TeamLogin1 { get; set; } = "";
    /// <summary>Auto-login Team 2.</summary>
    public string TeamLogin2 { get; set; } = "";
    /// <summary>Auto-login Team 3.</summary>
    public string TeamLogin3 { get; set; } = "";
    /// <summary>Auto-login Team 4.</summary>
    public string TeamLogin4 { get; set; } = "";

    /// <summary>
    /// Set true once the user has enabled multimonitor mode at least once.
    /// Unlocks Alt+M hotkey permanently. Won't work until user tries
    /// multimonitor via the Settings checkbox first.
    /// </summary>
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

    /// <summary>Number of clients to launch with "Launch All" (1-6).</summary>
    public int NumClients { get; set; } = 2;

    /// <summary>Delay in ms between launching each client.</summary>
    public int LaunchDelayMs { get; set; } = 1000;

    /// <summary>Delay in ms after all clients launched before arranging windows.</summary>
    public int FixDelayMs { get; set; } = 15000;
}

public class PipConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Size preset: "Small", "Medium", "Large", "XL", "XXL", "XXXL", "Custom"</summary>
    public string SizePreset { get; set; } = "Large";

    /// <summary>Custom width (used when SizePreset = "Custom").</summary>
    public int CustomWidth { get; set; } = 512;

    /// <summary>Custom height (used when SizePreset = "Custom").</summary>
    public int CustomHeight { get; set; } = 288;

    /// <summary>Stacking orientation: "Vertical" (top-to-bottom) or "Horizontal" (left-to-right).</summary>
    public string Orientation { get; set; } = "Vertical";

    /// <summary>Opacity (0-255). 255 = fully opaque.</summary>
    public byte Opacity { get; set; } = 220;

    /// <summary>Show colored border around PiP windows.</summary>
    public bool ShowBorder { get; set; } = true;

    /// <summary>Border color name: "Blue", "Green", "Red".</summary>
    public string BorderColor { get; set; } = "Blue";

    /// <summary>Border thickness in pixels (1-10). Default 3.</summary>
    public int BorderThickness { get; set; } = 3;

    /// <summary>Max number of PiP windows to show (1-3).</summary>
    public int MaxWindows { get; set; } = 2;

    /// <summary>Saved positions (X,Y pairs per slot).</summary>
    public List<int[]> SavedPositions { get; set; } = new();

    public (int w, int h) GetSize() => SizePreset switch
    {
        "Small" => (256, 144),
        "Medium" => (384, 216),
        "Large" => (512, 288),
        "XL" => (768, 432),
        "XXL" => (1024, 576),
        "XXXL" => (1600, 900),
        _ => (CustomWidth, CustomHeight)
    };

    public bool IsHorizontal => Orientation.Equals("Horizontal", StringComparison.OrdinalIgnoreCase);

    public Color GetBorderColor() => BorderColor switch
    {
        "Blue" => Color.FromArgb(15, 30, 80),
        "Green" => Color.FromArgb(10, 60, 25),
        "Red" => Color.FromArgb(50, 5, 5),
        _ => Color.FromArgb(15, 30, 80)
    };
}

public class TrayClickConfig
{
    /// <summary>
    /// Action for single left-click on tray icon.
    /// Values: "None", "AutoLogin1", "AutoLogin2", "AutoLogin3", "AutoLogin4", "LoginAll", "LoginAll2", "FixWindows", "SwapWindows", "TogglePiP", "LaunchOne", "LaunchAll", "Settings", "ShowHelp"
    /// </summary>
    public string SingleClick { get; set; } = "LaunchOne";

    /// <summary>
    /// Action for double left-click on tray icon.
    /// </summary>
    public string DoubleClick { get; set; } = "None";

    /// <summary>
    /// Action for triple left-click on tray icon.
    /// </summary>
    public string TripleClick { get; set; } = "None";

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
    /// <summary>Disable all EQ sound (Sound=FALSE in [Defaults]). EQ default: TRUE (sound disabled).</summary>
    public bool DisableSound { get; set; } = true;

    /// <summary>Disable music (Music=0 in [Defaults]). EQ default: TRUE (music off).</summary>
    public bool DisableMusic { get; set; } = true;

    /// <summary>Sound volume (SoundVolume in [Defaults]). EQ default: 0. -1 = don't override.</summary>
    public int SoundVolume { get; set; } = 0;

    /// <summary>Disable environment sounds (EnvSounds=0 in [Defaults]). EQ default: TRUE (env sounds off).</summary>
    public bool DisableEnvSounds { get; set; } = true;

    /// <summary>Disable combat music (CombatMusic=0 in [Defaults]). EQ default: TRUE (combat music off).</summary>
    public bool DisableCombatMusic { get; set; } = true;

    /// <summary>Disable Windows auto-ducking of EQ audio (AllowAutoDuck=0 in [Defaults]). EQ default: TRUE (auto-duck off).</summary>
    public bool DisableAutoDuck { get; set; } = true;

    /// <summary>Set sky update interval in ms (SkyUpdateInterval=60000 in [Defaults]). EQ default: TRUE (60000ms = slow).</summary>
    public bool SlowSkyUpdates { get; set; } = true;

    /// <summary>Disable sky rendering (Sky=0 in [Defaults]). EQ default: TRUE (sky off).</summary>
    public bool DisableSky { get; set; } = true;

    /// <summary>Enable persistent bard songs (BardSongs=1 in [Defaults]). EQ default: TRUE.</summary>
    public bool BardSongs { get; set; } = true;

    /// <summary>Enable bard songs on pets (BardSongsOnPets=1 in [Defaults]). EQ default: TRUE.</summary>
    public bool BardSongsOnPets { get; set; } = true;

    /// <summary>Shadow clip plane distance (ShadowClipPlane in [Defaults]). EQ default: 35.</summary>
    public int ShadowClipPlane { get; set; } = 35;

    /// <summary>Actor clip plane distance (ActorClipPlane in [Defaults]). EQ default: 67.</summary>
    public int ActorClipPlane { get; set; } = 67;

    /// <summary>Auto-attack when assisting (AttackOnAssist=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool AttackOnAssist { get; set; } = true;

    /// <summary>Show inspect message (ShowInspectMessage=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool ShowInspectMessage { get; set; } = true;

    /// <summary>Show grass (ShowGrass=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool ShowGrass { get; set; } = true;

    /// <summary>Show ping bar / network stats (NetStat=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool NetStat { get; set; } = true;

    /// <summary>Auto-update tracking window position (TrackAutoUpdate=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool TrackAutoUpdate { get; set; } = true;

    /// <summary>Target Group Buff (TargetGroupBuff=1 in [Defaults]). EQ default: TRUE.</summary>
    public bool TargetGroupBuff { get; set; } = true;

    /// <summary>Disable mip-mapping (MipMapping=FALSE in [Defaults]). EQ default: TRUE (mip-mapping off).</summary>
    public bool DisableMipMapping { get; set; } = true;

    /// <summary>Enable texture cache (TextureCache=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool TextureCache { get; set; } = true;

    /// <summary>Use D3D texture compression (UseD3DTextureCompression=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool UseD3DTextureCompression { get; set; } = true;

    /// <summary>Disable dynamic lights (ShowDynamicLights=FALSE in [Defaults]). EQ default: TRUE (lights off).</summary>
    public bool DisableDynamicLights { get; set; } = true;

    /// <summary>Use lit batches (UseLitBatches=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool UseLitBatches { get; set; } = true;

    /// <summary>Disable inspect others (InspectOthers=FALSE in [Defaults]). EQ default: TRUE (inspect off).</summary>
    public bool DisableInspectOthers { get; set; } = true;

    /// <summary>Anonymous mode (Anonymous=1 in [Defaults]). EQ default: TRUE.</summary>
    public bool Anonymous { get; set; } = true;

    /// <summary>Clip plane distance (ClipPlane in [Defaults]). EQ default: 14.</summary>
    public int ClipPlane { get; set; } = 14;

    /// <summary>Mouse sensitivity (MouseSensitivity in [Defaults]). EQ default: 5. -1 = don't override.</summary>
    public int MouseSensitivity { get; set; } = 5;

    /// <summary>Disable loot all confirmation (LootAllConfirm=0 in [Defaults]). EQ default: TRUE (confirm off).</summary>
    public bool DisableLootAllConfirm { get; set; } = true;

    /// <summary>Confirm raid invites (RaidInviteConfirm=1 in [Defaults]). EQ default: TRUE.</summary>
    public bool RaidInviteConfirm { get; set; } = true;

    /// <summary>Disable AA confirmation dialog (AANoConfirm=0 in [Defaults]). EQ default: FALSE.</summary>
    [JsonPropertyName("aaNoConfirm")]
    public bool AANoConfirm { get; set; } = false;

    /// <summary>Disable chat server (ChatServerPort=0 in [Options]). EQSwitch default: TRUE (chat server disabled for multiboxing).</summary>
    public bool DisableChatServer { get; set; } = true;

    /// <summary>Force windowed mode (WindowedMode=TRUE in [VideoMode]).</summary>
    public bool ForceWindowedMode { get; set; } = true;

    /// <summary>Start EQ maximized in windowed mode (Maximized=0 in [Defaults] + [VideoMode]). EQ default: FALSE.</summary>
    public bool MaximizeWindow { get; set; } = false;

    /// <summary>Disable EQ logging (Log=FALSE in [Defaults]). EQ default: FALSE (logging enabled).</summary>
    public bool DisableEQLog { get; set; } = false;

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
    [JsonPropertyName("cpuAffinitySlots")]
    public int[] CPUAffinitySlots { get; set; } = { 1, 2, 3, 1, 2, 3 };

    /// <summary>
    /// Read the user's actual eqclient.ini and return an EQClientIniConfig seeded from it.
    /// Called on first launch so AppConfig reflects reality instead of hardcoded defaults.
    /// Does not touch ConfiguredKeys, sub-form dictionaries, or CPUAffinitySlots.
    /// Returns a default instance if the ini file doesn't exist.
    /// </summary>
    public static EQClientIniConfig SeedFromIni(string iniPath)
    {
        var cfg = new EQClientIniConfig();
        if (!File.Exists(iniPath)) return cfg;

        try
        {
            var lines = File.ReadAllLines(iniPath, Encoding.Default);
            string currentSection = "";

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("["))
                {
                    currentSection = trimmed;
                    continue;
                }

                var parts = trimmed.Split('=', 2);
                if (parts.Length != 2) continue;

                string key = parts[0].Trim();
                string val = parts[1].Trim();

                if (currentSection.Equals("[Defaults]", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "sound":
                            cfg.DisableSound = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "music":
                            cfg.DisableMusic = val == "0";
                            break;
                        case "soundvolume":
                            if (int.TryParse(val, out int svol))
                                cfg.SoundVolume = Math.Clamp(svol, -1, 100);
                            break;
                        case "envsounds":
                            cfg.DisableEnvSounds = val == "0";
                            break;
                        case "combatmusic":
                            cfg.DisableCombatMusic = val == "0";
                            break;
                        case "allowautoduck":
                            cfg.DisableAutoDuck = val == "0";
                            break;
                        case "skyupdateinterval":
                            cfg.SlowSkyUpdates = val == "60000";
                            break;
                        case "attackonassist":
                            cfg.AttackOnAssist = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "showinspectmessage":
                            cfg.ShowInspectMessage = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "showgrass":
                            cfg.ShowGrass = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "netstat":
                            cfg.NetStat = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "trackautoupdate":
                            cfg.TrackAutoUpdate = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "targetgroupbuff":
                            cfg.TargetGroupBuff = val == "1";
                            break;
                        case "mipmapping":
                            cfg.DisableMipMapping = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "texturecache":
                            cfg.TextureCache = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "used3dtexturecompression":
                            cfg.UseD3DTextureCompression = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "showdynamiclights":
                            cfg.DisableDynamicLights = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "uselitbatches":
                            cfg.UseLitBatches = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "inspectothers":
                            cfg.DisableInspectOthers = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "sky":
                            cfg.DisableSky = val == "0";
                            break;
                        case "bardsongs":
                            cfg.BardSongs = val == "1";
                            break;
                        case "bardsongsonpets":
                            cfg.BardSongsOnPets = val == "1";
                            break;
                        case "anonymous":
                            cfg.Anonymous = val == "1";
                            break;
                        case "raidinviteconfirm":
                            cfg.RaidInviteConfirm = val == "1";
                            break;
                        case "aanoconfirm":
                            cfg.AANoConfirm = val == "0";
                            break;
                        case "chatserverport":
                            cfg.DisableChatServer = val == "0";
                            break;
                        case "lootallconfirm":
                            cfg.DisableLootAllConfirm = val == "0";
                            break;
                        case "clipplane":
                            if (int.TryParse(val, out int cp))
                                cfg.ClipPlane = Math.Clamp(cp, 0, 999);
                            break;
                        case "mousesensitivity":
                            if (int.TryParse(val, out int ms))
                                cfg.MouseSensitivity = Math.Clamp(ms, -1, 100);
                            break;
                        case "shadowclipplane":
                            if (int.TryParse(val, out int scp))
                                cfg.ShadowClipPlane = Math.Clamp(scp, 0, 999);
                            break;
                        case "actorclipplane":
                            if (int.TryParse(val, out int acp))
                                cfg.ActorClipPlane = Math.Clamp(acp, 0, 999);
                            break;
                        case "maxfps":
                            if (int.TryParse(val, out int fps))
                                cfg.MaxFPS = Math.Clamp(fps, 0, 99);
                            break;
                        case "maxbgfps":
                            if (int.TryParse(val, out int bgfps))
                                cfg.MaxBGFPS = Math.Clamp(bgfps, 0, 99);
                            break;
                        case "maximized":
                            cfg.MaximizeWindow = val == "1";
                            break;
                        case "log":
                            cfg.DisableEQLog = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                    }
                }
                else if (currentSection.Equals("[Options]", StringComparison.OrdinalIgnoreCase))
                {
                    // [Options] is runtime-authoritative — overrides [Defaults] for shared keys
                    switch (key.ToLowerInvariant())
                    {
                        case "sky":
                            cfg.DisableSky = val == "0";
                            break;
                        case "bardsongs":
                            cfg.BardSongs = val == "1";
                            break;
                        case "bardsongsonpets":
                            cfg.BardSongsOnPets = val == "1";
                            break;
                        case "anonymous":
                            cfg.Anonymous = val == "1";
                            break;
                        case "clipplane":
                            if (int.TryParse(val, out int optCp))
                                cfg.ClipPlane = Math.Clamp(optCp, 0, 999);
                            break;
                        case "mousesensitivity":
                            if (int.TryParse(val, out int optMs))
                                cfg.MouseSensitivity = Math.Clamp(optMs, -1, 100);
                            break;
                        case "shadowclipplane":
                            if (int.TryParse(val, out int optScp))
                                cfg.ShadowClipPlane = Math.Clamp(optScp, 0, 999);
                            break;
                        case "actorclipplane":
                            if (int.TryParse(val, out int optAcp))
                                cfg.ActorClipPlane = Math.Clamp(optAcp, 0, 999);
                            break;
                        case "maxfps":
                            if (int.TryParse(val, out int optFps))
                                cfg.MaxFPS = Math.Clamp(optFps, 0, 99);
                            break;
                        case "maxbgfps":
                            if (int.TryParse(val, out int optBgfps))
                                cfg.MaxBGFPS = Math.Clamp(optBgfps, 0, 99);
                            break;
                        case "lootallconfirm":
                            cfg.DisableLootAllConfirm = val == "0";
                            break;
                        case "raidinviteconfirm":
                            cfg.RaidInviteConfirm = val == "1";
                            break;
                        case "aanoconfirm":
                            cfg.AANoConfirm = val == "0";
                            break;
                        case "chatserverport":
                            cfg.DisableChatServer = val == "0";
                            break;
                    }
                }
                else if (currentSection.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "windowedmode":
                            cfg.ForceWindowedMode = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "maximized":
                            cfg.MaximizeWindow = val == "1";
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"SeedFromIni: failed to read {iniPath}: {ex.Message}");
        }

        return cfg;
    }
}
