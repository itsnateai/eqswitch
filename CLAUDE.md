# EQSwitch v3.4.3 — Claude Code Context

## What This Is
C# (.NET 8 WinForms) EverQuest multiboxing window manager for the Shards of Dalaya emulator. Features DLL hook injection, DPAPI-encrypted auto-login, slim titlebar mode, PiP overlays, and comprehensive eqclient.ini management. ~30 C# files + native C++ hook DLL, ~12,000 lines.

**Repo**: `itsnateai/eqswitch` (private) | **Branch**: master

## Build Commands
```bash
# Debug build
dotnet build

# Release — single-file portable exe (~155MB, self-contained)
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Output: bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe

# Native hook DLL (requires MSVC or MinGW)
cd Native && build.cmd   # MSVC
cd Native && ./build.sh  # MinGW
# Output: Native/eqswitch-hook.dll (ships alongside EQSwitch.exe)
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
| **NativeMethods.cs** (385 lines) | All Win32 P/Invoke declarations | **THE** single source for all DllImport. Never scatter DllImport elsewhere. Uses 64-bit safe `GetWindowLongPtrW`/`SetWindowLongPtrW`. WS_* constants are `long` not `int`. Includes `CreateRemoteThread`, `VirtualAllocEx`, `WriteProcessMemory` for DLL injection. |
| **ProcessManager.cs** | Polls for `eqgame.exe`, fires events on client discovery/loss | Timer-based (fixed 10s). Process[] from GetProcessesByName disposed in finally block. Events fire outside lock. Single-thread (UI thread only). |
| **WindowManager.cs** (452 lines) | Window positioning, slim titlebar, grid arrangement | Slim titlebar: strips `WS_THICKFRAME`, keeps `WS_CAPTION`, positions at `rcMonitor` bounds. Guard timer re-applies style. EnumDisplayMonitors for multi-monitor. Grid layout and stacked mode. |
| **AffinityManager.cs** | Process priority management | Per-character priority overrides. Retry logic for post-launch (EQ resets priority). CPU core assignment via eqclient.ini `CPUAffinity0-5`. |
| **HotkeyManager.cs** | Global hotkeys via RegisterHotKey | Hidden message-only NativeWindow (`HWND_MESSAGE`). MOD_NOREPEAT on all hotkeys. Parses "Modifier+Key" format strings. |
| **KeyboardHookManager.cs** | Low-level keyboard hook for single-key hotkeys | WH_KEYBOARD_LL for keys without modifiers. Context-sensitive process filter. Swallows matched keys. |
| **LaunchManager.cs** | Staggered EQ client launching | Bare-bones: just starts `eqgame.exe patchme` with staggered delay. Restore-if-minimized after 3s. No post-launch window manipulation. |
| **DllInjector.cs** (207 lines) | Native DLL injection into eqgame.exe | `CreateRemoteThread` + `LoadLibraryA` pattern. Injects `eqswitch-hook.dll` to hook `SetWindowPos`/`MoveWindow`. Also handles ejection via `FreeLibrary`. |
| **HookConfigWriter.cs** | Per-process shared memory for hook config | Per-PID memory-mapped files (`EQSwitchHookCfg_{PID}`) — each injected process gets its own mapping. Struct-matched layout (packed sequential ints) for position, style, enable flag. Supports both single and multimonitor modes. |
| **AutoLoginManager.cs** | DPAPI-encrypted auto-login | Launches EQ and auto-types credentials via DirectInput shared memory (dinput8.dll proxy). Full enter-world automation: username → password → server select → character slot. |
| **CredentialManager.cs** | DPAPI encryption wrapper | `DataProtectionScope.CurrentUser` — only same Windows user on same machine can decrypt. Base64-encoded ciphertext stored in config. |
| **FileLogger.cs** | Persistent file logging (Info/Warn/Error) | 1MB rotation, thread-safe. |
| **IWindowsApi.cs** / **WindowsApi.cs** | Testable Win32 interface + production impl | Abstraction layer for dependency injection in tests. |

### Config Layer (`Config/`)
| File | Purpose | Key Nuances |
|------|---------|-------------|
| **AppConfig.cs** (509 lines) | Strongly-typed JSON model | All settings in one file. Nested classes: WindowLayout, AffinityConfig, HotkeyConfig, LaunchConfig, PipConfig, CharacterProfile. LoginAccounts list for auto-login. |
| **ConfigManager.cs** | JSON load/save with backup rotation | Config at `eqswitch-config.json` alongside exe (portable). Auto-backup on save (keeps last 10). Coalesced writes (250ms). `FlushSave()` for critical saves. |
| **ConfigMigration.cs** | AHK config importer | Reads `eqswitch.cfg` (AHK INI format) with `Encoding.Default`. Runs automatically on first launch. |

### UI Layer (`UI/`)
| File | Purpose | Key Nuances |
|------|---------|-------------|
| **TrayManager.cs** (1549 lines) | Main orchestration hub | System tray icon, context menu, owns all managers (Process, Window, Affinity, Hotkey, KeyboardHook, Launch, AutoLogin, DllInjector, HookConfig). Config reload lifecycle. DLL injection on client discovery. Auto-login menu integration. |
| **DarkTheme.cs** (551 lines) | Centralized dark theme system | ALL colors, fonts, and control factories. Card panels with hover glow + accent left-bars. Semantic colors: CardWarn, BgOverlay, GridSelection, ActiveRowBg. Factory methods: MakeButton, MakePrimaryButton, MakeCard, AddNumeric, AddComboBox, AddCheckBox, AddLabel, AddHint, StyleForm. Zero hardcoded colors outside this file. |
| **SettingsForm.cs** (~1780 lines) | 6-tab dark settings GUI | Tabs: General, Video, Accounts, PiP, Hotkeys, Paths. Medieval purple theme. Video tab writes to eqclient.ini (own Save button) and includes Window Style settings, other tabs write to AppConfig JSON. |
| **ProcessManagerForm.cs** (559 lines) | Live process manager with auto-refresh | DataGridView with priority cards, CPU thread mapping, FPS limits. |
| **PipOverlay.cs** (363 lines) | DWM thumbnail PiP overlays | GPU-composited via `DwmRegisterThumbnail`. Draggable, click-through. Orientation-aware. Position saved on drag end. |
| **EQClientSettingsForm.cs** (1036 lines) | eqclient.ini toggles | Sound, graphics, gameplay, and extended settings. |
| **EQKeymapsForm.cs** | DirectInput key mapping editor | |
| **EQParticlesForm.cs** | Particle opacity/density sliders | |
| **EQModelsForm.cs** | Race/gender model toggles | |
| **EQVideoModeForm.cs** | [VideoMode] numeric settings | |
| **EQChatSpamForm.cs** | Chat spam filter toggles | |
| **FirstRunDialog.cs** | One-time EQ path setup | |
| **HelpForm.cs** | Read-only hotkey/feature reference | |
| **FloatingTooltip.cs** | Non-activating cursor tooltip | |
| **FileOperations.cs** | File I/O helpers for UI | |
| **StartupManager.cs** | Run-at-startup registry management | |

### Models (`Models/`)
- **EQClient.cs** — Running EQ client instance. Character name from window title.
- **LoginAccount.cs** — Stored account for auto-login. DPAPI-encrypted password (base64).

### Native Hook (`Native/`)
- **eqswitch-hook.cpp** — MinHook-based DLL that hooks `SetWindowPos` and `MoveWindow` inside eqgame.exe. Reads target position/style from shared memory-mapped file. Prevents EQ from fighting window management.
- **eqswitch-hook.dll** — Pre-built binary, ships alongside EQSwitch.exe.
- **MinHook source** — buffer.c, hook.c, trampoline.c, HDE32/64 disassembler. MIT license.
- **build.cmd / build.sh** — Build scripts for MSVC and MinGW.

## Key Design Decisions

### Why WinForms (not WPF)?
Lightweight system tray app. No complex UI, no data binding, no MVVM needed. WinForms is simpler, smaller, and starts faster for a background utility.

### Why Single-file Publish?
Matches the original AHK philosophy: one exe, drag anywhere, runs. No installer, no Program Files, no registry (except optional run-at-startup). Config JSON sits next to the exe.

### Hotkey Architecture (Two Systems)
1. **RegisterHotKey** (HotkeyManager) — For modifier-based hotkeys like Alt+1, Alt+G, Alt+M. Requires a hidden window to receive WM_HOTKEY. Uses MOD_NOREPEAT.
2. **SetWindowsHookEx WH_KEYBOARD_LL** (KeyboardHookManager) — For single keys without modifiers (backslash `\`, close bracket `]`). Can be context-sensitive (only fire when EQ is focused) or global.

### DLL Hook Injection Architecture
EQ's game engine actively repositions and resizes its window, fighting any external window management. The hook DLL solves this:
1. **C# host** detects new eqgame.exe via ProcessManager
2. **DllInjector** uses `CreateRemoteThread` + `LoadLibraryA` to inject `eqswitch-hook.dll`
3. **Hook DLL** (MinHook) intercepts `SetWindowPos` and `MoveWindow` inside the process
4. **HookConfigWriter** writes target position/style to a per-process memory-mapped file (`EQSwitchHookCfg_{PID}`)
5. **Hook DLL** reads shared memory on every hooked call, enforcing EQSwitch's layout

### DPAPI Auto-Login Security
Credentials are encrypted with Windows DPAPI (`DataProtectionScope.CurrentUser`). The encrypted blob is stored in `eqswitch-config.json` as base64. Only the same Windows user account on the same machine can decrypt. The login sequence uses DirectInput shared memory injection via `dinput8.dll` to type credentials into background EQ windows without stealing focus.

### Slim Titlebar (WinEQ2 Mode)
The killer differentiating feature. Strips `WS_THICKFRAME` (resize border) while keeping `WS_CAPTION` (thin title bar). Positions at full monitor bounds (`rcMonitor`, not `rcWork`) so the window overlaps the taskbar. A guard timer re-applies the style when EQ fights it.

### Event-Driven Affinity (SetWinEventHook)
Uses `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` for instant foreground change detection — zero latency vs. the old 250ms polling timer. Debounced at 50ms. Falls back to 250ms polling if the hook fails.

### Config Portability
Config at `eqswitch-config.json` next to the exe, not in `%AppData%`. Supports Syncthing/USB scenarios. Backup rotation (10 max) in a `backups/` subfolder.

## Gotchas & Pitfalls

### P/Invoke Safety
- **Always use `GetWindowLongPtr`/`SetWindowLongPtr`** (the "Ptr" variants). The non-Ptr versions are 32-bit only. WS_* constants must be `long` not `int`.
- **Always dispose Process objects** from `GetProcessById()` and `GetProcessesByName()`. Use `using var proc =`.
- **COM objects need explicit cleanup** — `Marshal.FinalReleaseComObject()` in a finally block.
- **Keyboard hook delegates must be stored as fields** — GC collection silently kills the hook.

### DLL Injection Safety
- **eqswitch-hook.dll must be next to EQSwitch.exe** — DllInjector uses the exe's directory to find it.
- **Injection requires matching architecture** — eqswitch-hook.dll and dinput8.dll are x86 (matching eqgame.exe). EQSwitch.exe is x64 but uses cross-arch injection via WoW64 PE parsing.
- **Memory-mapped file struct must match exactly** — The `HookConfig` struct in C# and C++ must have identical layout (packed, sequential). Any mismatch causes silent corruption.
- **Ejection before process exit** — DllInjector ejects the hook DLL when a client is lost to prevent leaks.

### Config Reload Flow
When settings change, `TrayManager.ReloadConfig()` must:
1. Stop and **dispose** old timers (not just Stop — leaked timers accumulate)
2. Unregister all hotkeys + unhook keyboard hook
3. Copy new config values to the live config object
4. Re-register hotkeys + re-install hook
5. Start new timers
6. Update DLL hook configs for injected processes

### SettingsForm Staging Pattern
Changes to controls don't modify live config. `ApplySettings()` builds a new AppConfig from control values. `SettingsChanged` event tells TrayManager to reload. **Never modify `_config` directly in the Settings form.**

### UI Thread Responsiveness
Single-threaded WinForms app. Two critical patterns:
1. **Debounce high-frequency callbacks.** `SetWinEventHook` fires on every desktop focus change. The `_foregroundDebounceTimer` (50ms) ensures we only do work once input settles.
2. **Coalesce file I/O.** `ConfigManager.Save()` uses a 250ms coalescing timer. `FlushSave()` on shutdown.

### Window Handle Staleness
EQ can recreate its window during gameplay. All Win32 calls on window handles should **tolerate silent failure**. Never cache window handles across timer ticks.

### EQ-specific
- Process name defaults to `eqgame`. Window title: `"EverQuest"` at login, `"EverQuest - CharName"` once logged in.
- `eqclient.ini` uses ANSI encoding — writing UTF-8 corrupts it.
- EQ resets CPU affinity shortly after launch — retry mechanism (3 retries at 2s intervals) re-applies.
- **Never write to eqclient.ini on launch** — EQ reads its own INI. Write only when user explicitly saves in Video Settings.

## Conventions
- All Win32 calls go through `NativeMethods.cs` — never scatter DllImport.
- Config is portable JSON alongside the exe, not in AppData.
- Use `FileLogger.Info/Warn/Error()` for diagnostics, `ShowBalloon()` for user-facing messages.
- Graceful degradation: if a Win32 call fails, log and continue, don't crash.
- Process objects: always `using var proc = ...` to prevent handle leaks.
- **All colors in `DarkTheme.cs`** — never use `Color.FromArgb()` outside DarkTheme.cs.
- **All controls via DarkTheme factories** — `MakeButton`, `AddNumeric`, `AddComboBox`, etc.
- **Settings alignment grid**: L=10, I=120, I2=310, BRW=370, R=28.

## File Layout
```
eqswitch/
  Program.cs                    # Entry point, mutex, migration
  EQSwitch.csproj               # .NET 8 WinForms, v3.4.3
  Core/
    NativeMethods.cs             # All P/Invoke (385 lines)
    ProcessManager.cs            # EQ process detection
    WindowManager.cs             # Window positioning, slim titlebar
    AffinityManager.cs           # Process priority management
    HotkeyManager.cs             # RegisterHotKey wrapper
    KeyboardHookManager.cs       # Low-level keyboard hook
    LaunchManager.cs             # Staggered EQ launching
    DllInjector.cs               # CreateRemoteThread DLL injection
    HookConfigWriter.cs          # Per-process shared memory for hook config
    AutoLoginManager.cs          # DPAPI auto-login automation
    CredentialManager.cs         # DPAPI encrypt/decrypt wrapper
    FileLogger.cs                # Persistent file logging
    IWindowsApi.cs               # Testable Win32 interface
    WindowsApi.cs                # Production IWindowsApi impl
  Config/
    AppConfig.cs                 # JSON config model (509 lines)
    ConfigManager.cs             # Load/save with backup rotation
    ConfigMigration.cs           # AHK config importer
  Models/
    EQClient.cs                  # Running EQ client model
    LoginAccount.cs              # Auto-login account preset
  Native/
    eqswitch-hook.cpp            # MinHook-based SetWindowPos/MoveWindow hook
    eqswitch-hook.dll            # Pre-built x86 hook DLL (matches eqgame.exe)
    MinHook.h                    # MinHook header
    hook.c / buffer.c / trampoline.c  # MinHook implementation
    hde32.c/h / hde64.c/h       # Hacker Disassembly Engine
    build.cmd / build.sh         # MSVC / MinGW build scripts
  UI/
    DarkTheme.cs                 # Centralized theme (551 lines)
    TrayManager.cs               # Main orchestration (1549 lines)
    SettingsForm.cs              # 6-tab settings GUI (~1780 lines, Video tab writes eqclient.ini)
    ProcessManagerForm.cs        # Live process manager (559 lines)
    PipOverlay.cs                # DWM thumbnail PiP overlay (363 lines)
    EQClientSettingsForm.cs      # eqclient.ini toggles (1036 lines)
    EQKeymapsForm.cs             # DirectInput key mapping editor
    EQParticlesForm.cs           # Particle opacity/density sliders
    EQModelsForm.cs              # Race/gender model toggles
    EQVideoModeForm.cs           # [VideoMode] numeric settings
    EQChatSpamForm.cs            # Chat spam filter toggles
    FirstRunDialog.cs            # First-run EQ path picker
    HelpForm.cs                  # Hotkey/feature reference
    FloatingTooltip.cs           # Non-activating cursor tooltip
    FileOperations.cs            # File I/O helpers
    StartupManager.cs            # Run-at-startup registry
```

## Features Summary
Window switching (hotkeys + keyboard hook), grid/stacked/multi-monitor arrangement, slim titlebar (WinEQ2 mode), DLL hook injection (prevents EQ from fighting window management), DPAPI-encrypted auto-login with enter-world automation, process priority management, eqclient.ini CPU affinity slots, PiP DWM thumbnails with orientation support, staggered launch, 6-tab settings GUI, comprehensive eqclient.ini editor, config migration from AHK, config backup/restore, character profiles, process manager, desktop shortcut creation, run-at-startup.

## Status

**v3.5.0 — Suspended-Process Injection Architecture (2026-04-09)**

Replaced dinput8.dll proxy with CREATE_SUSPENDED process injection. EQSwitch now injects eqswitch-di8.dll (148KB) and eqswitch-hook.dll (133KB) directly into eqgame.exe after resuming the loader (~50ms). Dalaya's 1.3MB MQ2 dinput8.dll stays untouched — no patcher conflicts, no server hash validation failures. Char select Enter World now uses 250ms key holds with 3 retry attempts and real title-change verification. ActivateThread continuously re-posts WM_ACTIVATEAPP(1) while SHM active to defend against focus loss. Dead proxy files removed, README updated.

**v3.4.3 — Auto-Login Past Character Select (2026-04-08)**

Auto-login now fully completes through Enter World. Two fixes: (1) dinput8.dll proxy forces BACKGROUND|NONEXCLUSIVE on mid-login SetCooperativeLevel re-calls when SHM is active, (2) C# AutoLoginManager replaces fixed 3s sleep after server select with adaptive WaitForScreenTransition() — polls IsHungAppWindow + GetWindowRect stability, handles any load time (5s–90s). Both verified in-game with dual-box login.

**v3.4.2 — Self-Update Completeness & Config Migration (2026-04-08)**

Self-updater now handles all shipped files: EQSwitch.exe, eqswitch-hook.dll, AND dinput8.dll (was missing). Added --test-update CLI flag for simulating full update flow locally. Post-update toast notification on relaunch. Retry logic for .old artifact cleanup (race with memory-mapped exe). Versioned config migration framework (ConfigVersionMigrator) — transforms raw JSON before deserialization so property renames/type changes don't lose user settings. CTS dispose race fix in UpdateDialog.
