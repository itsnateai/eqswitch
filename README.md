# EQSwitch v2.4.0 — C# Port

EverQuest multiboxing window manager for Shards of Dalaya.
Ported from AHK v2 to C# (.NET 8) for better VirusTotal compatibility, type safety, and debuggability.

## Download

**[Download EQSwitch.exe (latest release)](https://github.com/itsnateai/eqswitch_port/releases/latest)**

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
- **Background FPS Throttling**: Duty-cycle suspend/resume to reduce GPU/CPU usage on background clients
- **PiP Overlay**: Live DWM thumbnail previews of background clients (click-through, Ctrl+drag to move)
- **Borderless Fullscreen**: Fill screen without exclusive fullscreen — preserves Alt+Tab and PiP
- **Launching**: Staggered multi-client launch with auto-arrange after initialization
- **Settings GUI**: 8-tab dark-themed settings (General, Hotkeys, Layout, Affinity, Launch, PiP, Paths, Characters)
- **EQ Client Settings**: 6 sub-forms for editing eqclient.ini (Defaults, Client, Video Mode, Key Mappings, Spells, Models)
- **Video Settings**: Resolution presets, custom presets, windowed mode configuration
- **Configurable Tray Actions**: Single/double/triple/middle-click actions are fully customizable
- **Dark Themed Context Menus**: Medieval-themed emoji icons throughout
- **Character Profiles**: Per-character affinity overrides, export/import
- **Custom Icons**: Two built-in icon styles (Dark/Stone) + custom .ico support
- **File Operations**: Quick access to log files, eqclient.ini, GINA, and notes
- **Run at Startup**: Startup folder shortcut toggle (off by default)
- **Migration**: Auto-import from AHK eqswitch.cfg on first run

## Project Structure

```
EQSwitch.csproj          # Project config, single-file publish settings
EQSwitch.sln             # Solution (main + test project)
Program.cs               # Entry point, single-instance mutex, migration check
Config/
  AppConfig.cs           # Strongly-typed config model (replaces INI)
  ConfigManager.cs       # JSON load/save with auto-backup (keeps last 10)
  ConfigMigration.cs     # AHK eqswitch.cfg → JSON importer
Core/
  NativeMethods.cs       # All Win32 P/Invoke declarations (50+ imports)
  IWindowsApi.cs         # Testable Win32 interface abstraction
  WindowsApi.cs          # Production IWindowsApi implementation
  FileLogger.cs          # Persistent file logging (Info/Warn/Error)
  ProcessManager.cs      # EQ process detection and tracking
  WindowManager.cs       # Window positioning, grid arrange, swap, title bars
  AffinityManager.cs     # CPU affinity + priority management
  HotkeyManager.cs       # Global hotkeys via RegisterHotKey
  KeyboardHookManager.cs # Low-level keyboard hook (WH_KEYBOARD_LL)
  ThrottleManager.cs     # Background FPS throttling (suspend/resume duty cycle)
  LaunchManager.cs       # Staggered EQ client launching
Models/
  EQClient.cs            # Running EQ client data model
UI/
  TrayManager.cs         # System tray icon, menu, and main orchestration
  FirstRunDialog.cs      # First-run EQ path setup
  SettingsForm.cs        # 8-tab dark-themed settings GUI
  PipOverlay.cs          # DWM thumbnail PiP overlay (zero CPU — GPU composited)
  ProcessManagerForm.cs  # Live process manager with DataGridView
  DarkTheme.cs           # Shared dark theme colors and control factories
  DarkMenuRenderer.cs    # Dark-themed ToolStripProfessionalRenderer
  FloatingTooltip.cs     # Auto-dismiss borderless tooltip overlay
  HelpForm.cs            # Help window with hotkey reference
  FileOperations.cs      # File access helpers (log, config, GINA)
  StartupManager.cs      # Run-at-startup via Startup folder shortcut
  EQClientSettingsForm.cs # eqclient.ini [Defaults] editor
  EQVideoModeForm.cs     # eqclient.ini [VideoMode] editor
  EQKeymapsForm.cs       # eqclient.ini key mappings viewer
  VideoSettingsForm.cs   # Resolution presets and windowed mode
assets/
  eqswitch.svg           # Icon source (SVG)
  eqswitch-dark.ico      # Dark style icon (black bg, gold EQ + swords)
  eqswitch-stone.ico     # Stone style icon (lighter bg, gold EQ + swords)
EQSwitch.Tests/          # xUnit tests (Moq-based)
```

## Config

Config is stored as `eqswitch-config.json` alongside the exe (portable).
Auto-backups are created in a `backups/` subfolder (last 10 kept).

### Default Hotkeys

| Hotkey | Action |
|--------|--------|
| `\` | Swap to last active EQ client (EQ must be focused) |
| `]` | Global switch — cycle next / bring EQ to front |
| Alt+1 through Alt+6 | Switch to client by slot |
| Alt+G | Arrange all windows in grid |
| Alt+M | Toggle single-screen / multi-monitor mode |

### Tray Icon Actions (defaults, all configurable)

| Action | Result |
|--------|--------|
| Right-click | Context menu |
| Double-click | Launch one EQ client |
| Middle-click | Toggle PiP overlay |

### Custom Icons

EQSwitch ships with two icon styles selectable in Settings → General:

- **Dark** (default): Black background, gold EQ lettering with crossed swords
- **Stone**: Lighter stone background, same gold EQ + swords design

**Want your own icon?** Place a file named `eqswitch-custom.ico` next to the exe and it will be used automatically — no settings change needed.

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

## Troubleshooting

| Issue | Solution |
|-------|----------|
| **Hotkeys not working** | Run EQSwitch as Administrator. Some games need elevated privileges for global hotkeys to reach them. |
| **Antivirus flags the exe** | Single-file .NET publish is sometimes flagged by heuristic scanners. Add an exclusion for `EQSwitch.exe` or build from source. The exe has 0/70 detections on VirusTotal. |
| **EQ path not detected** | On first run, EQSwitch looks for `eqgame.exe` in the configured path. Use Settings → Paths to set your EQ installation directory. |
| **PiP overlay not showing** | PiP requires at least 2 EQ clients running. Middle-click the tray icon to toggle. Check Settings → PiP for size/opacity. |
| **CPU affinity not applying** | EQ resets its own affinity shortly after launch. EQSwitch retries 3 times at 2-second intervals. Use tray menu → Force Apply Affinity to re-apply manually. |
| **Window arrangement looks wrong** | Check Settings → Layout for grid columns/rows and monitor selection. Use the "Identify Monitors" button to see which monitor is which. Alt+G to re-arrange. |
| **Config lost after moving exe** | Config is stored as `eqswitch-config.json` next to the exe. Move them together. Backups are in the `backups/` subfolder. |
| **`]` key not typing in other apps** | Fixed in v2.1.1. Update to the latest release. The `]` key is only captured when EQ clients are running. |

## License

[MIT](LICENSE)
