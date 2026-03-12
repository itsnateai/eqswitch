# EQSwitch v2.0 — C# Port

EverQuest multiboxing window manager for Shards of Dalaya.  
Ported from AHK v2 to C# (.NET 8) for better VirusTotal compatibility, type safety, and debuggability.

## Why the Port?

- **VirusTotal**: AHK-compiled `.exe` files trigger false positives from heuristic scanners. .NET single-file publish uses Microsoft's toolchain and virtually never gets flagged.
- **Type Safety**: No more INI type comparison bugs or stale COM pointers.
- **Debugging**: Real breakpoints, call stacks, and exception handling instead of ToolTip feedback.
- **Same Portability**: Still compiles to a single `.exe` with no installer.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for building)
- Windows 10/11 (runtime)

## Build

```bash
# Debug build (for development)
dotnet build

# Release build (portable single-file exe)
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Output: bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe
```

## Project Structure

```
EQSwitch.csproj          # Project config, single-file publish settings
Program.cs               # Entry point, single-instance mutex, init
Config/
  AppConfig.cs           # Strongly-typed config model (replaces INI)
  ConfigManager.cs       # JSON load/save with auto-backup
Core/
  NativeMethods.cs       # All Win32 P/Invoke declarations
  ProcessManager.cs      # EQ process detection and tracking
  WindowManager.cs       # Window positioning, switching, title bars
  AffinityManager.cs     # CPU affinity (P-core/E-core management)
  HotkeyManager.cs       # Global hotkey registration
Models/
  EQClient.cs            # Running EQ client data model
UI/
  TrayManager.cs         # System tray icon, menu, orchestration
  FirstRunDialog.cs      # First-run EQ path setup
```

## Config

Config is stored as `eqswitch-config.json` alongside the exe (portable).
Auto-backups are created in a `backups/` subfolder (last 10 kept).

### Default Hotkeys

| Hotkey | Action |
|--------|--------|
| Alt+Tab | Cycle to next EQ client |
| Alt+Shift+Tab | Cycle to previous EQ client |
| Alt+1 through Alt+6 | Switch to client by slot |
| Alt+G | Arrange all windows in grid |

### CPU Affinity Defaults (Intel 12th+ gen hybrid)

- **Active client**: P-cores (mask `0xFF` = cores 0-7)
- **Background clients**: E-cores (mask `0xFF00` = cores 8-15)
- Per-character overrides supported in config

## Migration Notes (AHK → C#)

| AHK v2 Feature | C# Equivalent |
|----------------|---------------|
| `WinActivate` | `SetForegroundWindow` via P/Invoke |
| `SetTimer` | `System.Windows.Forms.Timer` |
| `Hotkey` command | `RegisterHotKey` + hidden NativeWindow |
| `ProcessSetPriority` | `SetProcessAffinityMask` via P/Invoke |
| INI read/write | `System.Text.Json` serialization |
| `A_Tray` menu | `NotifyIcon` + `ContextMenuStrip` |
| `ToolTip` | `BalloonTipText` notifications |
| Compiled via Ahk2Exe | `dotnet publish` single-file |

## TODO

- [ ] Settings dialog (WinForms) for editing config without hand-editing JSON
- [ ] Per-monitor DPI awareness
- [ ] Optional: overlay/OSD showing which client is active
- [ ] Optional: WPF migration for richer settings UI
- [ ] Middle-click notes feature (port from AHK version)
