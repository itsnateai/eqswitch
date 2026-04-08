<h1 align="center">EQSwitch</h1>

<p align="center">
  EverQuest multiboxing window manager for <a href="https://dalaya.org">Shards of Dalaya</a>
</p>

<p align="center">
  <a href="https://github.com/itsnateai/eqswitch/releases/latest"><img src="https://img.shields.io/github/v/release/itsnateai/eqswitch?style=flat-square&color=34c060" alt="Release"></a>
  <img src="https://img.shields.io/badge/.NET-8.0-512bd4?style=flat-square&logo=dotnet" alt=".NET 8">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4?style=flat-square&logo=windows" alt="Windows">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/itsnateai/eqswitch?style=flat-square&color=34c060" alt="GPL-3.0 License"></a>
</p>

---

> **Fan-made project.** EQSwitch is an independent, open-source utility and is not affiliated with, endorsed by, or connected to Daybreak Game Company, Darkpaw Games, or the Shards of Dalaya team. EverQuest is a registered trademark of Daybreak Game Company LLC. This tool does not distribute, modify, or host any game content — it is a client-side window manager only.

A lightweight EverQuest multiboxer — hotkey switching, encrypted auto-login, PiP overlays, CPU affinity, multi-monitor support, and zero-telemetry privacy for Shards of Dalaya.

## Download

**[Download from Releases](https://github.com/itsnateai/eqswitch/releases/latest)**

| File | Size | Notes |
|------|------|-------|
| **EQSwitch.exe** | ~179 MB | Self-contained — no runtime needed, click and go |
| **eqswitch-hook.dll** | ~132 KB | Ships with exe — hooks SetWindowPos/MoveWindow inside eqgame.exe |
| **dinput8.dll** | ~132 KB | Ships with exe — deployed to EQ directory for background auto-login |

> [!TIP]
> Place in any folder and run — no installation needed. Config is stored as `eqswitch-config.json` next to the exe.

## Screenshots

<!-- TODO: Add screenshots — tray menu, settings GUI, PiP overlay, grid layout -->

*Screenshots coming soon*

## Features

- **Window Switching** — Cycle EQ clients with hotkeys (keyboard hook for single keys, RegisterHotKey for combos)
- **Fullscreen Window** — WinEQ2-style borderless mode that hides the titlebar above the screen edge
- **DLL Hook Injection** — Hooks SetWindowPos/MoveWindow inside eqgame.exe for zero-flicker window positioning
- **Multi-Monitor** — One client per physical monitor with automatic arrangement
- **Process Priority** — Active client on High, background on AboveNormal (configurable per-character)
- **CPU Core Assignment** — CPUAffinity0-5 slots written to eqclient.ini for per-client core pinning
- **PiP Overlay** — Live DWM thumbnail previews (zero CPU, GPU composited). Vertical or horizontal layout, 7 size presets + custom
- **Staggered Launch** — Multi-client launch with configurable delay and auto-arrange
- **Settings GUI** — Dark-themed tabbed settings (General, Hotkeys, Layout, PiP, Paths, Characters)
- **EQ Client Settings** — Sub-forms for editing eqclient.ini (Video, Key Mappings, Chat, Particles, Models)
- **Video Settings** — Resolution presets, custom presets, windowed mode enforcement
- **Configurable Tray Actions** — Left/double/middle click actions are fully customizable
- **Character Profiles** — Per-character priority overrides, export/import
- **Custom Tray Icon** — Custom .ico support
- **Auto-Login** — Encrypted account presets with one-click launch, login, server select, and character enter world
- **Background Auto-Login** — Types passwords in background windows via DirectInput shared memory injection — no focus stealing, true one-click multi-login
- **DirectInput Proxy** — dinput8.dll hooks IAT for focus faking (GetForegroundWindow, GetAsyncKeyState, etc.) and injects keyboard state via per-PID shared memory
- **Portable** — Single exe + 2 DLLs, config stored next to it, no installer needed
- **Privacy First** — Zero telemetry, no network calls, no data collection. Saved passwords are encrypted with Windows DPAPI and can only be decrypted by your Windows account on your machine

## Requirements

- Windows 10/11

## Build from Source

```bash
git clone https://github.com/itsnateai/eqswitch.git
cd eqswitch

# Build the native hook DLL (requires MSVC Build Tools, 32-bit x86 target)
bash Native/build.sh

# Build the DirectInput proxy DLL (32-bit x86)
bash Native/build-dinput8.sh

# Self-contained single-file (~179MB, no runtime needed)
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Output: `bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe` + `eqswitch-hook.dll` + `dinput8.dll`

## Default Hotkeys

| Hotkey | Action |
|--------|--------|
| `\` | Swap to last active EQ client (EQ must be focused) |
| `]` | Global switch — cycle next / bring EQ to front |
| Alt+N | Toggle single-screen / multi-monitor mode |

### Tray Icon Actions (all configurable)

| Action | Result |
|--------|--------|
| Right-click | Context menu |
| Double-click | Launch one EQ client |
| Middle-click | Toggle PiP overlay |
| Middle-triple-click | Open Settings |

## Config

Config is stored as `eqswitch-config.json` alongside the exe (portable). Auto-backups are created in a `backups/` subfolder (last 10 kept).

### Custom Icons

EQSwitch ships with two icon styles selectable in Settings:
- **Dark** (default): Black background, gold EQ lettering with crossed swords
- **Stone**: Lighter stone background, same design

Place a file named `eqswitch-custom.ico` next to the exe to use your own icon.

### Process Priority Defaults

- **Active client**: High priority
- **Background clients**: High priority
- Per-character priority overrides in Settings → Characters
- CPU core assignment via eqclient.ini CPUAffinity0-5 slots (Settings → Process Manager)

## Project Structure

| Path | Description |
|------|-------------|
| `Program.cs` | Entry point — single-instance mutex, first-run setup |
| `Core/` | Win32 interop, process management, hotkeys, DLL injection, shared memory |
| `Config/` | JSON config model, load/save, AHK migration |
| `Models/` | EQ client data model |
| `UI/` | Tray manager, settings GUI, PiP overlay, dark theme, process manager |
| `Native/` | C++ DLL sources — eqswitch-hook (MinHook) + dinput8 proxy (DirectInput + IAT hooks), 32-bit x86 |

## Background Auto-Login

When **Background Login** is enabled (Settings → Accounts), EQSwitch types passwords into background EQ windows without stealing focus. This allows true one-click multi-account login.

**How it works:**
1. `dinput8.dll` is deployed to your EQ directory before launching
2. The DLL intercepts DirectInput and hooks keyboard state APIs (GetAsyncKeyState, GetForegroundWindow, etc.)
3. Per-PID shared memory injects scan codes directly into EQ's DirectInput keyboard device
4. EQ processes the keystrokes as if it were the focused window

> [!NOTE]
> EQ must be restarted after enabling background login for the first time — the proxy DLL loads at startup.

> [!WARNING]
> Windows Defender may flag `dinput8.dll` as suspicious (DirectInput hooking is a common game-tool technique). Add your EQ folder to Defender's exclusion list if the DLL is blocked or quarantined.

## Auto-Login Security

Passwords are encrypted using **Windows DPAPI** (Data Protection API) and stored locally in your config file. Here's what that means:

- **AES-256 encryption** — DPAPI derives an encryption key from your Windows login credentials (password hash + machine SID + user SID) using PBKDF2, then encrypts with AES-256
- **User-scoped** — Only your Windows user account on your machine can decrypt the passwords. Other users on the same PC (even administrators) cannot read them
- **Zero network traffic** — Encryption and decryption happen entirely on your machine. Passwords are never transmitted anywhere
- **No master password** — Your Windows login IS the master key. The encrypted master key is stored in `%APPDATA%\Microsoft\Protect\{SID}\`
- **Stored as base64** — The encrypted blob is base64-encoded in `eqswitch-config.json`. Even if someone reads the file, they see gibberish without your Windows credentials

> [!IMPORTANT]
> If you reinstall Windows or create a new user account, stored passwords cannot be recovered. You'll need to re-enter them in Settings.

## First-Run Config Seeding

On first launch, EQSwitch reads your actual `eqclient.ini` and uses those values as its starting defaults. This means the settings form always shows your real configuration — not generic defaults that could silently overwrite your manual INI edits.

Settings are **not enforced** until you explicitly click Save in the EQ Client Settings form.

## Uninstall / Clean Up

EQSwitch can be fully removed without leaving traces. Two options:

**From the GUI:** Settings → General tab → **Uninstall** button

**From the command line:** Run `uninstall.bat` (ships next to `EQSwitch.exe`)

Both options revert all external changes:
- Restores original `dinput8.dll` from backup (if one was created)
- Removes startup shortcut
- Removes desktop shortcut

Your `eqclient.ini` settings and EQSwitch config files are **not** modified — restore from `.bak` files in your EQ folder if needed.

> [!TIP]
> After running uninstall, you can delete the entire EQSwitch folder to complete the removal.

## Troubleshooting

| Issue | Solution |
|-------|----------|
| **Hotkeys not working** | Run as Administrator — some games need elevated privileges for global hotkeys |
| **EQ path not detected** | Use Settings → Paths to set your EQ installation directory |
| **PiP not showing** | Requires 2+ EQ clients running. Middle-click tray icon to toggle |
| **CPU affinity not applying** | EQ resets affinity after launch — EQSwitch retries automatically. Use tray menu → Force Apply |
| **Config lost after moving exe** | Move `eqswitch-config.json` with the exe. Backups in `backups/` subfolder |
| **dinput8.dll blocked** | Add your EQ folder to Windows Defender exclusions |

## License

[GPL-3.0](LICENSE)
