# EQSwitch

EverQuest multiboxing window manager for Shards of Dalaya.

A system tray utility for managing multiple EverQuest clients — window switching, arrangement, CPU affinity, PiP overlays, and more. Ported from AHK v2 to C# (.NET 8 WinForms).

## Download

**[Download from Releases](https://github.com/itsnateai/eqswitch_port/releases/latest)**

| File | Size | Notes |
|------|------|-------|
| **EQSwitch.exe** | ~171 MB | Self-contained — no runtime needed, click and go |
| **EQSwitch-fd.exe** | ~790 KB | Requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

Place in any folder and run — no installation needed. Config is stored as `eqswitch-config.json` next to the exe.

> Migrating from the AHK version? Place the exe next to your old `eqswitch.cfg` and it will auto-import your settings on first run.

## Features

- **Window Switching**: Cycle EQ clients with hotkeys (keyboard hook for single keys, RegisterHotKey for combos)
- **Window Arrangement**: Grid layout (single monitor) or one-per-monitor (multi-monitor mode)
- **CPU Affinity**: Active client on P-cores, background on E-cores (Intel hybrid CPU optimized)
- **Background FPS Throttling**: Duty-cycle suspend/resume to reduce GPU/CPU usage on background clients
- **PiP Overlay**: Live DWM thumbnail previews of background clients (click-through, Ctrl+drag to move)
- **Borderless Fullscreen**: Fill screen without exclusive fullscreen — preserves Alt+Tab and PiP
- **Launching**: Staggered multi-client launch with auto-arrange after initialization
- **Settings GUI**: 8-tab dark-themed settings (General, Hotkeys, Layout, Affinity, Launch, PiP, Paths, Characters)
- **EQ Client Settings**: Sub-forms for editing eqclient.ini (Defaults, Video Mode, Key Mappings, Spells, Models)
- **Video Settings**: Resolution presets, custom presets, windowed mode, Reset Defaults
- **Configurable Tray Actions**: Single/double/triple/middle-click actions are fully customizable
- **Dark Themed Context Menus**: Medieval-themed emoji icons throughout
- **Character Profiles**: Per-character affinity overrides, export/import
- **Custom Icons**: Two built-in styles (Dark/Stone) + custom .ico support
- **File Operations**: Quick access to log files, eqclient.ini, GINA, and notes
- **Run at Startup**: Startup folder shortcut toggle
- **Migration**: Auto-import from AHK eqswitch.cfg on first run

## Requirements

- Windows 10/11

## Build from Source

```bash
git clone https://github.com/itsnateai/eqswitch_port.git
cd eqswitch_port

# Framework-dependent (~260KB, requires .NET 8 runtime)
dotnet publish EQSwitch.csproj -c Release --no-self-contained -p:PublishSingleFile=false

# Self-contained single-file (~171MB, no runtime needed)
dotnet publish EQSwitch.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Default Hotkeys

| Hotkey | Action |
|--------|--------|
| `\` | Swap to last active EQ client (EQ must be focused) |
| `]` | Global switch — cycle next / bring EQ to front |
| Alt+1 through Alt+6 | Switch to client by slot |
| Alt+M | Toggle single-screen / multi-monitor mode |

### Tray Icon Actions (all configurable)

| Action | Result |
|--------|--------|
| Right-click | Context menu |
| Double-click | Launch one EQ client |
| Middle-click | Toggle PiP overlay |

## Config

Config is stored as `eqswitch-config.json` alongside the exe (portable). Auto-backups are created in a `backups/` subfolder (last 10 kept).

### Custom Icons

EQSwitch ships with two icon styles selectable in Settings:
- **Dark** (default): Black background, gold EQ lettering with crossed swords
- **Stone**: Lighter stone background, same design

Place a file named `eqswitch-custom.ico` next to the exe to use your own icon.

### CPU Affinity Defaults (Intel 12th+ gen hybrid)

- **Active client**: P-cores (mask `0xFF` = cores 0-7), AboveNormal priority
- **Background clients**: E-cores (mask `0xFF00` = cores 8-15), Normal priority
- Auto-applies on window switch (250ms polling)
- Per-character affinity overrides supported

## Migration from AHK Version

On first run, EQSwitch checks for `eqswitch.cfg` alongside the exe and automatically imports: EQ path, hotkey mappings, layout mode, CPU affinity masks, PiP settings, GINA path, and notes file.

## Project Structure

| Path | Description |
|------|-------------|
| `EQSwitch.csproj` | .NET 8 project file |
| `Program.cs` | Entry point — single-instance mutex, migration |
| `Core/` | Win32 interop, process management, hotkeys, throttling |
| `Config/` | JSON config model, load/save, AHK migration |
| `Models/` | EQ client data model |
| `UI/` | Tray manager, settings GUI, PiP overlay, dark theme |
| `assets/` | Icon source files |
| `EQSwitch.Tests/` | xUnit tests |

## Troubleshooting

| Issue | Solution |
|-------|----------|
| **Hotkeys not working** | Run as Administrator — some games need elevated privileges for global hotkeys |
| **EQ path not detected** | Use Settings → Paths to set your EQ installation directory |
| **PiP not showing** | Requires 2+ EQ clients running. Middle-click tray icon to toggle |
| **CPU affinity not applying** | EQ resets affinity after launch — EQSwitch retries automatically. Use tray menu → Force Apply |
| **Config lost after moving exe** | Move `eqswitch-config.json` with the exe. Backups in `backups/` subfolder |

## License

[MIT](LICENSE)
