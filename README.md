# EQSwitch v2.1.0 — C# Port

EverQuest multiboxing window manager for Shards of Dalaya.
Ported from AHK v2 to C# (.NET 8) for better VirusTotal compatibility, type safety, and debuggability.

## Download

**[Download EQSwitch.exe (latest release)](https://github.com/itsnateai/eqswitch-port/releases/latest)**

1. Download `EQSwitch.exe` from the latest release
2. Place it in any folder (Desktop, EQ folder, wherever you like)
3. Run it — no installation needed

Self-contained portable exe. No .NET runtime required. No installer. Just run it.

> Migrating from the AHK version? Place `EQSwitch.exe` next to your old `eqswitch.cfg` and it will auto-import your settings on first run.

## Why the Port?

- **VirusTotal**: AHK-compiled `.exe` files trigger false positives from heuristic scanners. .NET single-file publish uses Microsoft's toolchain and gets 0/70 detections.
- **Type Safety**: No more INI type comparison bugs or stale COM pointers.
- **Debugging**: Real breakpoints, call stacks, and exception handling instead of ToolTip feedback.
- **Same Portability**: Still compiles to a single `.exe` with no installer.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (only needed for building from source)

## Build from Source

```bash
# Debug build (for development)
dotnet build

# Release build (portable single-file exe)
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Output: bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe
```

## Features

- **Window Switching**: Cycle EQ clients with hotkeys (keyboard hook for single keys, RegisterHotKey for combos)
- **Window Arrangement**: Grid layout (single monitor) or one-per-monitor (multi-monitor mode)
- **CPU Affinity**: Active client on P-cores, background on E-cores (Intel hybrid CPU optimized)
- **PiP Overlay**: Live DWM thumbnail previews of background clients (click-through, Ctrl+drag to move)
- **Launching**: Staggered multi-client launch with auto-arrange after initialization
- **Settings GUI**: 8-tab dark-themed settings (General, Hotkeys, Layout, Affinity, Launch, PiP, Paths, Characters)
- **Video Settings**: Read/write eqclient.ini resolution and windowed mode
- **File Operations**: Quick access to log files, eqclient.ini, GINA, and notes
- **Run at Startup**: Registry-based Windows startup toggle
- **Migration**: Auto-import from AHK eqswitch.cfg on first run

## Project Structure

```
EQSwitch.csproj          # Project config, single-file publish settings
Program.cs               # Entry point, single-instance mutex, migration check
Config/
  AppConfig.cs           # Strongly-typed config model (replaces INI)
  ConfigManager.cs       # JSON load/save with auto-backup (keeps last 10)
  ConfigMigration.cs     # AHK eqswitch.cfg → JSON importer
Core/
  NativeMethods.cs       # All Win32 P/Invoke declarations (50+ imports)
  ProcessManager.cs      # EQ process detection and tracking
  WindowManager.cs       # Window positioning, grid arrange, swap, title bars
  AffinityManager.cs     # CPU affinity + priority management
  HotkeyManager.cs       # Global hotkeys via RegisterHotKey
  KeyboardHookManager.cs # Low-level keyboard hook (WH_KEYBOARD_LL)
  LaunchManager.cs       # Staggered EQ client launching
Models/
  EQClient.cs            # Running EQ client data model
UI/
  TrayManager.cs         # System tray icon, menu, and main orchestration
  FirstRunDialog.cs      # First-run EQ path setup
  SettingsForm.cs        # Tabbed settings GUI
  VideoSettingsForm.cs   # eqclient.ini [VideoMode] editor
  PipOverlay.cs          # DWM thumbnail PiP overlay
```

## Config

Config is stored as `eqswitch-config.json` alongside the exe (portable).
Auto-backups are created in a `backups/` subfolder (last 10 kept).

### Default Hotkeys

| Hotkey | Action |
|--------|--------|
| `\` | Cycle to next EQ client (EQ must be focused) |
| `]` | Global switch — cycle next / bring EQ to front |
| Alt+1 through Alt+6 | Switch to client by slot |
| Alt+G | Arrange all windows in grid |
| Alt+M | Toggle single-screen / multi-monitor mode |

### Tray Icon Actions

| Action | Result |
|--------|--------|
| Right-click | Context menu |
| Double-click | Launch one EQ client |
| Middle-click | Toggle PiP overlay |

### CPU Affinity Defaults (Intel 12th+ gen hybrid)

- **Active client**: P-cores (mask `0xFF` = cores 0-7), AboveNormal priority
- **Background clients**: E-cores (mask `0xFF00` = cores 8-15), Normal priority
- Auto-applies on window switch (250ms polling)
- Per-character affinity overrides supported

## Migration from AHK Version

On first run, EQSwitch checks for `eqswitch.cfg` alongside the exe and automatically imports settings. The migrator handles:

- EQ path and launch arguments
- Hotkey mappings (AHK syntax → standard format)
- Layout mode and monitor settings
- CPU affinity masks and process priority
- PiP size, opacity, border, and position
- GINA path and notes file path

| AHK v2 Feature | C# Equivalent |
|----------------|---------------|
| `WinActivate` | `SetForegroundWindow` via P/Invoke |
| `SetTimer` | `System.Windows.Forms.Timer` |
| `Hotkey` command | `RegisterHotKey` + hidden NativeWindow |
| `ProcessSetPriority` | `SetProcessAffinityMask` via P/Invoke |
| INI read/write | `System.Text.Json` serialization |
| `A_Tray` menu | `NotifyIcon` + `ContextMenuStrip` |
| `ToolTip` | `BalloonTipText` notifications |
| `DllCall("dwmapi\DwmRegister...")` | `DwmRegisterThumbnail` P/Invoke |
| Compiled via Ahk2Exe | `dotnet publish` single-file |

## License

[MIT](LICENSE)
