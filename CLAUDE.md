# EQSwitch — CLAUDE.md

## Overview
EQ Switch is a Windows tray utility for EverQuest multi-boxing. Single AHK v2 script, single .exe, no installation.

## Stack
- AutoHotkey v2
- Win32 API (DWM thumbnails, process management, CPU affinity)
- INI config (eqswitch.cfg)

## Build
```bash
MSYS_NO_PATHCONV=1 "X:/_Projects/_tools/Ahk/Ahk2Exe.exe" /in eqswitch.ahk /out eqswitch.exe /icon eqswitch.ico /compress 0 /silent
```

## Key Files
| File | Purpose |
|------|---------|
| EQSwitch.ahk | Main source (~2,640 lines) |
| eqswitch.ico | Tray/exe icon (embedded via @Ahk2Exe-AddResource) |
| eqswitch.cfg | User config (gitignored, INI format) |

## Architecture
- Single-file AHK v2 script with sections: Helpers, Process Management, Config, Hotkeys, Tray Menu, Launch, Fix Windows, Settings GUI, Video Settings, Process Manager, PiP, Help
- Config via INI with ReadKey() helper and auto-migration from old section names
- DWM thumbnails for PiP overlay (DwmRegisterThumbnail/DwmUpdateThumbnailProperties)
- WS_EX_TRANSPARENT click-through with Ctrl-toggle for drag repositioning
- Middle-click tray detection via WM_MBUTTONUP (Win11 compatible)

## Status

**v2.1 — Final release (shipped 2026-03-11)**

All audit items resolved (30/30). Tracking files cleared. See FINAL_REPORT.md for summary.
