# EQSwitch v2.3.0 — Claude Code Context

## What This Is
C# (.NET 8 WinForms) port of EQSwitch, an EverQuest multiboxing window manager originally written in AHK v2. Targets the Shards of Dalaya emulator community. v2.3.0 — added background FPS throttling and borderless fullscreen. ~27 files, ~5,700 lines.

**Repo**: `itsnateai/eqswitch-port` (private) | **Branch**: master

## Build Commands
```bash
# Debug build
dotnet build

# Release — single-file portable exe (~155MB, self-contained)
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Output: bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe
```

### Build Environment Setup
If building on a fresh machine:
```bash
winget install Microsoft.DotNet.SDK.8
dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org
dotnet nuget locals all --clear   # if restore fails
dotnet restore
```

## Architecture

### Entry Point
- **Program.cs** — Single-instance enforcement via named Mutex (`EQSwitch_SingleInstance_SoD`). Loads config, runs AHK migration on first launch, shows FirstRunDialog if no migration found, then hands off to TrayManager. `Application.ApplicationExit` handler ensures cleanup on any exit path.

### Core Layer (`Core/`)
| File | Purpose | Key Nuances |
|------|---------|-------------|
| **NativeMethods.cs** | All Win32 P/Invoke declarations | **THE** single source for all DllImport. Never scatter DllImport elsewhere. Uses 64-bit safe `GetWindowLongPtrW`/`SetWindowLongPtrW` (not the 32-bit GetWindowLong). WS_* constants are `long` not `int`. |
| **ProcessManager.cs** | Polls for `eqgame.exe`, fires events on client discovery/loss | Timer-based (configurable, default 500ms). Process[] from GetProcessesByName disposed in finally block. Events fire under lock but safe due to single-thread (UI thread only — breaks if ever moved off-thread). |
| **WindowManager.cs** | Window positioning, grid arrangement, title bar removal | Uses `GetWindowLongPtr` for style manipulation. EnumDisplayMonitors for multi-monitor support. Grid layout: `(columns x rows)` on target monitor. Multi-monitor mode: one client per monitor. TopOffset config for taskbar/bezel adjustment. |
| **AffinityManager.cs** | CPU affinity + process priority for P-core/E-core optimization | Opens process with PROCESS_SET_INFORMATION + PROCESS_QUERY_INFORMATION. Sets active client to P-cores (default mask 0xFF), background to E-cores (0xFF00). ForceApplyAffinityRules() for manual re-apply from Process Manager UI. Retry logic for post-launch (EQ resets affinity on startup). |
| **HotkeyManager.cs** | Global hotkeys via RegisterHotKey | Hidden message-only NativeWindow (`HWND_MESSAGE` parent = IntPtr(-3)). MOD_NOREPEAT on all hotkeys. Parses "Modifier+Key" format strings. `ResolveVK()` public static helper for KeyboardHookManager. |
| **KeyboardHookManager.cs** | Low-level keyboard hook for single-key hotkeys | WH_KEYBOARD_LL for keys without modifiers (backslash, bracket). Context-sensitive: optional process filter (only fires when EQ focused). Swallows matched keys (returns 1). IsForegroundProcess uses `using var proc` to prevent handle leaks. |
| **ThrottleManager.cs** | Background FPS throttling via process suspension | Uses `NtSuspendProcess`/`NtResumeProcess` to duty-cycle background EQ clients. Two alternating timers: suspend phase + resume phase. Active client is never throttled. All processes resumed on shutdown (fail-safe). Config: ThrottlePercent (0-90%), CycleIntervalMs (50-1000ms). |
| **LaunchManager.cs** | Staggered EQ client launching | Launches `eqgame.exe patchme` from configured EQ path. Configurable delay between launches (default 3s) and post-launch arrange delay (15s). |

### Config Layer (`Config/`)
| File | Purpose | Key Nuances |
|------|---------|-------------|
| **AppConfig.cs** | Strongly-typed JSON model | All settings in one file. Nested classes: WindowLayout, AffinityConfig, HotkeyConfig, LaunchConfig, PipConfig, CharacterProfile. PipConfig has `GetSize()` switch expression and `GetBorderColor()`. CharacterProfile has optional `AffinityOverride` (null = use global). |
| **ConfigManager.cs** | JSON load/save with backup rotation | Config lives at `eqswitch-config.json` alongside exe (portable, not AppData). Auto-backup on every save (keeps last 10 in `backups/`). Corrupt config backed up with `CORRUPT_` prefix and reset to defaults. Uses `System.Text.Json` with camelCase naming. Character export/import as standalone JSON. |
| **ConfigMigration.cs** | AHK config importer | Reads `eqswitch.cfg` (AHK INI format) with `Encoding.Default` (not UTF-16). Maps AHK field names to C# config properties. Runs automatically on first launch. Translates AHK hotkey format to C# format. |

### UI Layer (`UI/`)
| File | Purpose | Key Nuances |
|------|---------|-------------|
| **TrayManager.cs** (982 lines) | Main orchestration hub | System tray icon, context menu, triple-click detection for "secret" manual refresh. Owns all managers (ProcessManager, WindowManager, AffinityManager, HotkeyManager, KeyboardHookManager, LaunchManager). Config reload: stops timers → disposes old timers → re-registers hotkeys → restarts timers. COM cleanup in CreateDesktopShortcut with `Marshal.FinalReleaseComObject`. |
| **SettingsForm.cs** (734 lines) | 8-tab dark settings GUI | Tabs: General, Hotkeys, Layout, Affinity, Launch, PiP, Paths, Characters. Dark theme: BackColor `#2D2D30`, ForeColor `#F1F1F1`. Uses `_pendingCharacters` field to stage character imports (not applied until Save). ClampNud helper for safe NumericUpDown population. ApplySettings builds new AppConfig and fires `SettingsChanged` event. |
| **ProcessManagerForm.cs** | Live process manager with auto-refresh | DataGridView with auto-refresh timer. Force Apply button re-applies affinity rules. Uses delegate injection for affinity actions. |
| **PipOverlay.cs** | DWM thumbnail PiP overlays | Uses `DwmRegisterThumbnail`/`DwmUpdateThumbnailProperties` for live window thumbnails (zero CPU — GPU composited). Draggable, click-through (`WS_EX_TRANSPARENT`). Position saved to config on drag end. Configurable size presets, opacity, border color. Max 3 PiP windows. |
| **VideoSettingsForm.cs** | eqclient.ini editor | Reads/writes EQ's settings file. Organized by category. Watch encoding: EQ uses ANSI, not UTF-8. |
| **FirstRunDialog.cs** | One-time EQ path setup | Folder browser dialog for selecting EQ installation directory. |

### Models (`Models/`)
- **EQClient.cs** — Running EQ client instance. Resolves character name from window title (`"EverQuest - CharName"`). `IsProcessAlive()` wrapped in `using var proc`. `ToString()` returns `"CharName (PID: N)"`.

## Key Design Decisions

### Why WinForms (not WPF)?
Lightweight system tray app. No complex UI, no data binding, no MVVM needed. WinForms is simpler, smaller, and starts faster for a background utility.

### Why Single-file Publish?
Matches the original AHK philosophy: one exe, drag anywhere, runs. No installer, no Program Files, no registry (except optional run-at-startup). Config JSON sits next to the exe.

### Hotkey Architecture (Two Systems)
1. **RegisterHotKey** (HotkeyManager) — For modifier-based hotkeys like Alt+1, Alt+G, Alt+M. Requires a hidden window to receive WM_HOTKEY. Uses MOD_NOREPEAT.
2. **SetWindowsHookEx WH_KEYBOARD_LL** (KeyboardHookManager) — For single keys without modifiers (backslash `\`, close bracket `]`). Can be context-sensitive (only fire when EQ is focused) or global. This is how the original AHK version worked.

### Why Timer-based Affinity?
No clean Win32 event for "foreground window changed to my monitored process." A 250ms timer polling `GetForegroundWindow()` is simple, reliable, and low-overhead. The AHK version did the same thing.

### Message-only Window Pattern
`HotkeyMessageWindow` uses `Parent = new IntPtr(-3)` (HWND_MESSAGE) to create a window that receives messages but is never visible. No mystery window flashes.

### Config Portability
Config at `eqswitch-config.json` next to the exe, not in `%AppData%`. Supports Syncthing/USB scenarios. Backup rotation (10 max) in a `backups/` subfolder.

## Gotchas & Pitfalls

### P/Invoke Safety
- **Always use `GetWindowLongPtr`/`SetWindowLongPtr`** (the "Ptr" variants). The non-Ptr versions are 32-bit only and will silently fail or truncate on 64-bit processes. WS_* constants must be `long` not `int`.
- **Always dispose Process objects** from `GetProcessById()` and `GetProcessesByName()`. They wrap OS handles. Use `using var proc =`. The polling code does this every 500ms — handle leaks add up fast.
- **COM objects need explicit cleanup** — `Marshal.FinalReleaseComObject()` in a finally block for WScript.Shell (desktop shortcut creation).
- **Keyboard hook delegates must be stored as fields** — If the GC collects the delegate, the hook silently stops working. `_hookProc` field in KeyboardHookManager prevents this.

### Config Reload Flow
When settings change, `TrayManager.ReloadConfig()` must:
1. Stop and **dispose** old timers (not just Stop — leaked timers accumulate)
2. Unregister all hotkeys + unhook keyboard hook
3. Copy new config values to the live config object
4. Re-register hotkeys + re-install hook
5. Start new timers

Missing step 1 (dispose) was a P1 bug. Missing DirectSwitchKeys copy was a P1 bug.

### SettingsForm Staging Pattern
Settings uses a pending/staged approach:
- Changes to controls don't modify live config
- `_pendingCharacters` stages character imports
- `ApplySettings()` builds a new AppConfig from control values
- `SettingsChanged` event tells TrayManager to reload
- **Never modify `_config` directly in the Settings form** (the character import bug was exactly this)

### Affinity Quirks
- EQ resets its own CPU affinity shortly after launch. The retry mechanism (3 retries at 2s intervals) re-applies after launch.
- Mask values are hex bitmasks. A mask of 0xFF = cores 0-7 (P-cores on 12th gen Intel). 0xFF00 = cores 8-15 (E-cores).
- `ForceApplyAffinityRules()` exists for the Process Manager UI's "Force Apply" button.

### PiP (DWM Thumbnails)
- DWM thumbnails are GPU-composited — essentially zero CPU overhead unlike screen capture.
- Click-through via `WS_EX_TRANSPARENT | WS_EX_LAYERED`.
- Must call `DwmUnregisterThumbnail` on dispose or the thumbnail leaks.
- Position persists to config via `ConfigManager.Save()` on drag end.

### EQ-specific
- Process name is configurable but defaults to `eqgame` (matches `eqgame.exe`).
- Window title format: `"EverQuest"` at login, `"EverQuest - CharName"` once logged in. Character name resolution splits on `" - "`.
- AHK config uses `Encoding.Default` (ANSI), not UTF-16.
- `eqclient.ini` also uses ANSI encoding — writing UTF-8 can corrupt it.

## Status

**v2.3.0 — Released 2026-03-12**

New in v2.3.0:
- P2-04: Background FPS throttling (NtSuspendProcess/NtResumeProcess duty cycle)
- P2-03: Borderless fullscreen mode (WinEQ Y+1 offset trick, rcMonitor bounds)

No deferred features remaining. All ideas reviewed and declined 2026-03-12.

## Conventions
- All Win32 calls go through `NativeMethods.cs` — never scatter DllImport.
- Config is portable JSON alongside the exe, not in AppData.
- Use `FileLogger.Info/Warn/Error()` for diagnostic logging, `ShowBalloon()` for user-facing messages.
- Graceful degradation: if a Win32 call fails, log it and continue, don't crash.
- Process objects: always `using var proc = ...` to prevent handle leaks.
- Single-file publish for portability. No installer needed.

## File Layout
```
eqswitch-port/
  Program.cs                    # Entry point, mutex, migration
  EQSwitch.csproj               # .NET 8 WinForms, v2.3.0
  EQSwitch.sln                  # Solution with main + test projects
  Core/
    FileLogger.cs                # Persistent file logging (Info/Warn/Error)
    IWindowsApi.cs               # Testable Win32 interface + WinRect struct
    WindowsApi.cs                # Production IWindowsApi implementation
    NativeMethods.cs             # All P/Invoke (224 lines)
    ProcessManager.cs            # EQ process detection
    WindowManager.cs             # Window positioning & arrangement
    AffinityManager.cs           # CPU affinity management
    HotkeyManager.cs             # RegisterHotKey wrapper
    KeyboardHookManager.cs       # Low-level keyboard hook
    ThrottleManager.cs           # Background FPS throttling (suspend/resume)
    LaunchManager.cs             # Staggered EQ launching
  Config/
    AppConfig.cs                 # JSON config model (225 lines)
    ConfigManager.cs             # Load/save with backup rotation
    ConfigMigration.cs           # AHK config importer
  Models/
    EQClient.cs                  # Running EQ client model
  UI/
    TrayManager.cs               # Main orchestration (982 lines)
    SettingsForm.cs              # 8-tab dark settings GUI (734 lines)
    ProcessManagerForm.cs        # Live process manager
    PipOverlay.cs                # DWM thumbnail PiP overlay
    VideoSettingsForm.cs         # eqclient.ini editor
    FirstRunDialog.cs            # First-run EQ path picker
```

## Features Summary
Window switching (hotkeys + keyboard hook), grid/multi-monitor arrangement, CPU affinity P-core/E-core, background FPS throttling (process suspension), PiP DWM thumbnails, staggered launch, 8-tab settings GUI, eqclient.ini editor, config migration from AHK, character profiles with export/import, process manager, desktop shortcut creation, run-at-startup registry entry.
