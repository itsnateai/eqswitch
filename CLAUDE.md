# EQSwitch v2.5.0 — Claude Code Context

## What This Is
C# (.NET 8 WinForms) port of EQSwitch, an EverQuest multiboxing window manager originally written in AHK v2. Targets the Shards of Dalaya emulator community. v2.5.0 — quality of life improvements in progress. ~27 files, ~5,700 lines.

**Repo**: `itsnateai/eqswitch_port` (private) | **Branch**: master

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

### Event-Driven Affinity (SetWinEventHook)
Uses `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` for instant foreground change detection — zero latency vs. the old 250ms polling timer. The callback runs on the UI thread via `WINEVENT_OUTOFCONTEXT` (requires a message pump, which WinForms provides). Delegate stored as a field to prevent GC collection. Falls back to 250ms polling if the hook fails to install.

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

### UI Thread Responsiveness Rules
This is a single-threaded WinForms app. Everything runs on the UI thread. Two patterns protect responsiveness:

1. **Debounce high-frequency callbacks.** `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` fires on *every* window focus change across the entire desktop — not just EQ windows. Without debounce, rapid Alt+Tab triggers expensive affinity re-apply + PiP rebuild + throttle update dozens of times per second. The `_foregroundDebounceTimer` (50ms) ensures we only do work once input settles. **Any new WinEvent or system hook callback must be debounced.**

2. **Coalesce file I/O.** `ConfigManager.Save()` uses a 250ms coalescing timer — rapid calls (PiP drag, toggle clicks) batch into one write. The actual I/O (JSON serialize + backup rotation + file write) only happens after a quiet period. `FlushSave()` on shutdown ensures nothing is lost. **Never add synchronous file I/O to a timer callback or event handler.**

Things to watch:
- `GetProcessesByName()` in the 500ms polling timer is inherently slow (~10-30ms). Don't add more work to that path.
- Any `OpenProcess()` + Win32 call per-client in a loop (affinity, throttle) scales linearly with client count. Keep these behind the debounce.
- `DwmRegisterThumbnail`/`DwmUpdateThumbnailProperties` are DWM compositor calls — not free. PipOverlay.UpdateSources() has an early-exit "no change" fast path to avoid unnecessary re-registration.

### PiP + Throttle Interaction
ThrottleManager suspends background EQ processes via `NtSuspendProcess`. Suspended processes can't render, so DWM thumbnails go black. PiP source PIDs are passed to `ThrottleManager.SetExemptPids()` so they keep running. **When adding new features that interact with background clients, check whether they conflict with throttle suspension.**

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

**v2.5.0 — In Progress (Quality of Life)**

New in v2.5.0:
- Reset Defaults button in Video Settings form

Released in v2.4.0 (2026-03-14):
- Reorganized tray context menu into grouped submenus (Video Settings, Settings, Launcher)
- Emoji/icon prefixes on all menu items (medieval theme)
- Dark themed context menus (DarkMenuRenderer)
- Configurable tray click actions with delayed resolution
- Custom video presets (up to 3), Process Manager dark restyle
- FloatingTooltip, first-run auto-opens Settings

Released in v2.3.0:
- Background FPS throttling (NtSuspendProcess/NtResumeProcess duty cycle)
- Borderless fullscreen mode (WinEQ Y+1 offset trick)

## Cloud Audit Instructions (Pre-Release)

**BEASTMODE ON. #deepresearch. You are the final senior coder before release.**

When Cloud runs the deep audit on this project, go beyond surface-level checks. This is a long-running tray app that users leave running for days/weeks. Activate full production audit v3 with extra focus on:

### Resource Leak Hunting (Priority 1)
- **Timer-path allocation audit**: Trace every function called from every timer callback recursively. Flag ANY object allocation in a hot path (250ms affinity timer, 500ms polling timer, throttle duty cycle timers, click resolve timer).
- **Handle accumulation**: Every `Process` object, GDI object (`Font`, `Brush`, `Pen`), COM object must be disposed in the same scope it's created. Check `using var` on ALL Process.GetProcessesByName/GetProcessById calls.
- **Event handler leaks**: Lambda event handlers on long-lived objects can prevent GC. Check for += without corresponding -=, especially in BuildContextMenu (called on reload).
- **Timer dispose**: Old timers must be Stop()'d AND Dispose()'d before replacement. Check ReloadConfig flow.
- **ContextMenuStrip rebuild**: When menu is rebuilt, old menu items and their event handlers must be cleaned up.

### Syntax & Logic Errors (Priority 1)
- **Null reference paths**: Trace every nullable field access. Especially `_trayIcon`, `_contextMenu`, `_clientsMenu`, `_pipOverlay`, `_clickResolveTimer`.
- **Race conditions**: UI thread assumption — verify nothing touches shared state from a non-UI thread.
- **Config serialization roundtrip**: Verify TrayClickConfig, CustomVideoPresets, and all new config classes serialize/deserialize correctly with System.Text.Json camelCase naming.
- **Enum string matching**: TrayClickConfig uses string matching ("LaunchOne", "FixWindows", etc.) — verify every case in ExecuteTrayAction matches exactly.

### 72-Hour Viability (Priority 1)
- Run the full 72-hour viability checklist from root CLAUDE.md
- Calculate: `(allocation size) × (frequency) × (uptime)` for every timer path
- Verify Explorer restart recovery (TaskbarCreated message handler)
- Verify graceful shutdown cleans up ALL timers, hooks, handles, COM objects

### New Code Since v2.3.0 (Priority 2)
- **DarkMenuRenderer**: Check for GDI object leaks in OnRender* overrides (Brush, Pen created with `using var`?)
- **Click resolve timer**: Created/disposed on every click — verify no leak if rapid clicking
- **FloatingTooltip**: Verify it self-disposes and doesn't accumulate windows
- **VideoSettingsForm custom presets**: Config persistence, FIFO eviction, duplicate detection
- **ProcessManagerForm DataGridView**: Font disposal, timer cleanup on close

### Branch Hygiene (MANDATORY)
- **BEFORE doing ANY work**: `git fetch origin && git log --oneline origin/master..HEAD` — check if you're behind remote. Pull first.
- **EVERY session start**: Check GitHub for the latest state: `gh api repos/itsnateai/eqswitch_port/commits/master --jq '.sha'` and compare to local HEAD.
- **NEVER work on a stale branch.** Nate pushes changes from live testing sessions. If you don't pull first, merging will suck. This is non-negotiable.
- **Check for stacked PRs**: `gh pr list --repo itsnateai/eqswitch_port` — if there are pending PRs, review and merge them FIRST before starting new work. Cloud sometimes stacks 7+ PRs that go stale. Clean the queue before adding to it.
- **After fixes**: Commit in small logical groups, push immediately. Don't batch 10 fixes into one mega-commit.

### Upgrade Opportunities
- Look for new .NET 8 patterns that could simplify existing code
- Check for deprecated API usage
- Identify any P0/P1 bugs introduced in the v2.4.0 changes
- Fix anything you find — don't just report, fix and commit

## Conventions
- All Win32 calls go through `NativeMethods.cs` — never scatter DllImport.
- Config is portable JSON alongside the exe, not in AppData.
- Use `FileLogger.Info/Warn/Error()` for diagnostic logging, `ShowBalloon()` for user-facing messages.
- Graceful degradation: if a Win32 call fails, log it and continue, don't crash.
- Process objects: always `using var proc = ...` to prevent handle leaks.
- Single-file publish for portability. No installer needed.

## File Layout
```
eqswitch_port/
  Program.cs                    # Entry point, mutex, migration
  EQSwitch.csproj               # .NET 8 WinForms, v2.5.0
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
