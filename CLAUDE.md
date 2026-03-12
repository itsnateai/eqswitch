# EQSwitch — CLAUDE.md

## Overview
EQ Switch is a Windows tray utility for EverQuest multi-boxing. Single AHK v2 script, single .exe, no installation.

## Stack
- AutoHotkey v2 (strict mode — no assume-global, every nested function must declare globals explicitly)
- Win32 API via DllCall (DWM thumbnails, process management, CPU affinity)
- INI config (eqswitch.cfg, single `[EQSwitch]` section)

## Build
```bash
MSYS_NO_PATHCONV=1 "X:/_Projects/_tools/Ahk/Ahk2Exe.exe" /in eqswitch.ahk /out eqswitch.exe /icon eqswitch.ico /compress 0 /silent
```
`/compress 0` is required — compressed AHK executables trigger Windows Defender false positives.

## Key Files
| File | Purpose |
|------|---------|
| EQSwitch.ahk | Main source (~2,640 lines) |
| eqswitch.ico | Tray/exe icon (embedded via @Ahk2Exe-AddResource) |
| eqswitch.cfg | User config (gitignored, INI format) |
| CHANGELOG.md | Full version history v1.0 — v2.0 |
| FINAL_REPORT.md | Project summary and audit results |

## Architecture

Single-file AHK v2 script. Sections appear in this order:

1. **Globals & Helpers** (~1-95) — version, constants, `ShowTip()`, `GetVisibleEqWindows()` with 200ms cache
2. **Process Management** (~96-205) — priority, affinity, core count via Win32
3. **Config** (~206-366) — `LoadConfig()`/`SaveConfig()` with `ReadKey()` helper, auto-migration from old `[EQ2Box]` section
4. **Hotkeys** (~367-524) — `BindHotkey()`, `SwitchWindow()`, `FocusEQ()`, launch/multimon hotkey binding, rollback on bind failure
5. **Menu Helpers** (~525-577) — bold menu items via Win32 MENUITEMINFOW, recent char list parsing
6. **Tray Menu** (~578-722) — tray click handler (double/triple/middle), menu construction, submenus
7. **Fix Windows** (~723-867) — single screen and multimonitor layout, `SwapWindows()`, `ToggleMultiMon()`
8. **Open File Dialogs** (~868-988) — log file, eqclient.ini, Gina, Notes
9. **Settings GUI** (~989-1806) — section builders (`BuildHotkeySection`, etc.), `OpenSettings()`, `ApplySettings()`
10. **PiP Overlay** (~1807-2195) — DWM thumbnail registration, click-through, Ctrl+drag, border bars
11. **Process Manager GUI** (~2196-2371) — ListView of running EQ processes, priority/affinity controls
12. **Video Settings GUI** (~2372-2505) — eqclient.ini editor, resolution presets, WindowedMode toggle
13. **Launch** (~2506-2637) — `LaunchOne()` with debounce, `LaunchBoth()` with async timer chain

### Key Patterns

- **Global state**: All config values are globals (e.g., `EQ_HOTKEY`, `PIP_WIDTH`). Nested functions must use explicit `global` declarations — AHK v2 does not assume-global, and missing declarations silently create locals.
- **GUI singletons**: Settings, Process Manager, Video Settings each use a boolean flag (`SETTINGS_OPEN`, `g_pmOpen`, `g_vmOpen`) to prevent duplicate windows. Always reset the flag in Close/Escape/catch handlers.
- **Hotkey rollback**: When rebinding hotkeys in Settings, the old hotkey is saved first. If the new one fails to bind, the old one is restored. The AHK Hotkey GUI control cannot represent bare keys like `\` or `]` — `.Value` returns empty. The fix: if the control returns empty, preserve the existing binding rather than clearing it.
- **Launch snapshot**: `LaunchOne`/`LaunchBoth` snapshot config values (exe path, priority, affinity, fix mode) at call time so mid-launch Settings changes don't affect behavior.
- **PiP source swap**: When the active EQ window changes, PiP swaps DWM thumbnail sources in-place (unregister old, register new on same GUI) to avoid flicker. Full GUI rebuild only happens when the number of alt windows changes.
- **Timer cleanup**: PiP uses two timers (`g_pipTimer` for refresh, `g_pipCtrlWatch` for Ctrl key). Both must be stopped in `DestroyPiP()`. Launch uses timer chains for async sequencing.

### Win32 / DllCall Conventions
- Process handles: always `try/finally` with `CloseHandle`
- `DWM_THUMBNAIL_PROPERTIES`: 48-byte struct. Key offsets: dwFlags@0, rcDestination@4-19, fVisible@40, fSourceClientAreaOnly@44
- `MENUITEMINFOW`: cbSize is 80 (64-bit) or 48 (32-bit), determined by `A_PtrSize`
- Affinity masks use `UPtr` (pointer-sized unsigned) for 64-bit safety
- `IsHungAppWindow` used to skip unresponsive windows in FixWindows/SwapWindows

### AHK v2 Gotchas (learned the hard way)
- **Nested function scoping**: A nested function that reads a global without `global` declaration will silently create a local. This caused bugs with `TARGET_MONITOR`, `g_pipCtrlWatch`, and `g_dblClickUpIgnore` during development.
- **Hotkey control limitation**: The built-in Hotkey GUI control can't display bare keys (`\`, `]`). `.Value` returns `""`. Must detect empty and preserve the old binding.
- **SetTimer with function objects**: `SetTimer(fn, 0)` cancels. `SetTimer(fn, -ms)` runs once. Passing a new BoundFunc each call creates a new timer instead of updating the old one — always reuse the same function reference.
- **Loop with negative count**: `Loop -1` and `Loop 0` both execute zero iterations (safe).
- **String/number coercion**: Config values are strings from IniRead. AHK v2 auto-coerces in arithmetic but `Integer()` throws on non-numeric input — always wrap in try/catch.
- **Win11 tray**: `WM_MBUTTONDOWN` (0x207) is unreliable. Must use `WM_MBUTTONUP` (0x208) exclusively for middle-click detection.
- **64-bit struct alignment**: APPBARDATA lParam is at offset 40 (not 36) due to HWND alignment padding. Always verify struct layouts against 64-bit Win32 headers, not 32-bit examples.

### eqclient.ini Quirks
- `WindowedModeYOffset` only takes effect when toggling window mode (windowed <-> fullscreen and back). Must flip `WindowedMode=FALSE` then back to `TRUE` for offset changes to apply.
- X offset of 0 is stable; -8 shifts too far left.
- Resolution values go in `[VideoMode]`; `WindowedMode` goes in `[Defaults]`.

## Config Keys (eqswitch.cfg)

All keys live in `[EQSwitch]` section. Defaults in parentheses:

| Key | Default | Notes |
|-----|---------|-------|
| EQ_EXE | C:\EverQuest\eqgame.exe | Full path to executable |
| EQ_ARGS | -patchme | Launch arguments |
| EQ_HOTKEY | \ | Window switch key (scoped to EQ) |
| FOCUS_HOTKEY | ] | Global switch key (works from any app) |
| MULTIMON_HOTKEY | >!m | RAlt+M for multi-monitor toggle |
| MULTIMON_ENABLED | 1 | Enable/disable multimon hotkey |
| NUM_CLIENTS | 2 | Launch Both client count (1-8) |
| FIX_MODE | single screen | "single screen" or "multimonitor" |
| TARGET_MONITOR | 2 | Default monitor for single screen mode |
| FIX_TOP_OFFSET | 0 | Pixels to push window down (hide title bar) |
| PROCESS_PRIORITY | Normal | Normal, AboveNormal, or High |
| CPU_AFFINITY | (empty) | Decimal bitmask, empty = all cores |
| PIP_WIDTH | 320 | PiP overlay width |
| PIP_HEIGHT | 180 | PiP overlay height |
| PIP_OPACITY | 200 | PiP transparency (50-255) |
| PIP_X, PIP_Y | (empty) | Saved PiP position, empty = bottom-right |
| PIP_BORDER_ENABLED | 1 | Show colored border around PiP |
| BORDER_COLOR | 00FF00 | Hex color for PiP border |
| LAUNCH_DELAY | 3000 | ms between client launches (config-only) |
| LAUNCH_FIX_DELAY | 15000 | ms before auto-fix after launch (config-only) |
| MIDCLICK_MODE | notes_pip | "off", "notes_pip", or "pip_notes" |
| EQ_SERVER | dalaya | Server name for log/ini file paths |

## Status

**v2.2 — Final release (shipped 2026-03-12)**

All audit items resolved. Tracking files cleared. See FINAL_REPORT.md for summary.
