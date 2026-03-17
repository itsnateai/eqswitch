# Changelog

All notable changes to EQ Switch are documented here.

## v2.4 — 2026-03-17

### New Features
- **MaxFPS / MaxBGFPS controls** in Video Settings GUI — Edit+UpDown (0-99), writes to eqclient.ini `[Defaults]` section. 0 = no override. Matches C# port behavior.

## v2.3 — 2026-03-15

### New Features
- **Reset Defaults button** in Video Settings GUI — resets resolution to 1920×1009, offsets to 0, WindowedMode on, cleans stale .tmp files
- **Reset Defaults button** in Process Manager GUI — resets priority to Normal, affinity to all cores

### Bug Fixes
- **Minimized windows recovered** — SwitchWindow and FocusEQ hotkeys now WinRestore minimized EQ windows before activating them, preventing stuck-in-taskbar issues
- **Sticky tooltips** — launch flow tooltips ("Launching client...", "Waiting for windows to settle...") no longer stay on screen forever; switched to auto-dismissing ShowTip

## v2.2 — 2026-03-12

### Fixed
- **Uninitialized global** — `GetClientsByTitle` was reading a global variable before it was assigned, causing silent null behaviour on first call. Explicit initialization added.

### Removed
- **Dead code cleanup** — removed `HidePiPBorder` and unused `FIX_BOTTOM_OFFSET` global that were left over from earlier refactoring.

## v2.1 — 2026-03-11

### Changed
- **Settings label** — multimon hotkey label renamed from "Multimon key:" to "Toggle on:" for clarity.
- **README** — updated to be game-agnostic (removed Shards of Dalaya reference, generalized wording).

## v2.0 — 2026-03-10

### New Features
- **Middle-click PiP toggle** — single + triple-click paradigm on tray middle button. Single click = primary action (Notes or PiP), triple click = secondary. Actions swappable in Settings dropdown
- **Global Switch hotkey** (default: `]`) — pulls EQ to front from any app, cycles through clients on repeat presses. Works even when EQ isn't focused
- **WindowedMode toggle** — checkbox in Video Settings writes WindowedMode=TRUE/FALSE to eqclient.ini. Fullscreen not tested (disclaimer shown)
- **Title Bar Offset** — configurable pixel offset in Video Settings pushes game window down to hide the title bar. Default: 0px
- **Sticky resolution presets** — Video Settings dropdown stashes custom values before switching; selecting "Custom" restores them. Added 1920×1009 preset
- **PiP border** — 1px thin colored border around PiP overlay for visibility. Toggle + color picker in Settings
- **Embedded tray icon** — eqswitch.ico compiled into the exe via `@Ahk2Exe-AddResource`, with fallback chain for uncompiled scripts

### Settings GUI Redesign
- **Compact EverQuest section** — Launch args moved inline with EQ Executable label. Process Manager + Video Settings buttons merged into EverQuest section (removed standalone Process Settings section)
- **Screen resolution label** — "Screen resolution, offsets, and window mode" description next to Video Settings button
- **EQ Switch Key** — renamed from "Active Key" for clarity
- **PiP controls** — W/H edit boxes widened for readability; "Ctrl+drag to move PiP" hint moved to Opacity line

### Help Window
- **Cyberpunk style** — `─── SECTION NAME ─────` Unicode box-drawing dividers with `•` bullet points
- **Scrollable & resizable** — single Edit control with ReadOnly +VScroll, singleton pattern prevents duplicate windows
- **Template saved** — `_templates/templates/helpmenu_template.md` for reuse across projects

### Removed
- **Window Presets** — save/load/delete window layout presets and tray submenu. Feature saw no real use
- **Fullscreen windowed toggle** — broke EQ on launch; removed entirely
- **Taskbar toggle** — tray menu item removed
- **Auto-minimize** — entire FeatureRefresh timer system removed (didn't work in multi-monitor)
- **Active window border highlight** — removed (broken with maximized windows)

### Bug Fixes
- **PiPHitTest race condition** — wrapped `.Hwnd` access in try; OnMessage no longer crashes during PiP destruction
- **Double-click fix** — triple-click detection ignores the double-click's own button-up to prevent LaunchBoth firing
- **Startup shortcut** — uses shell Startup folder path via `APPDATA` env var instead of `A_Startup`; shortcut now includes the app icon
- **Middle-click reliability** — uses WM_MBUTTONUP (0x208) only; Win11 tray doesn't reliably send WM_MBUTTONDOWN (0x207). 1.2s cooldown prevents re-triggering
- **AHK Hotkey control limitation** — bare keys like `\` or `]` return empty on `.Value` read; fix preserves old binding instead of clearing

## v1.7 — 2026-03-10

### New Features
- **Process Priority Management** — auto-set eqgame.exe to High (or AboveNormal) priority on launch. Configurable dropdown in Settings. Replaces need for Process Lasso
- **CPU Affinity Control** — configure which CPU cores eqgame.exe can use via the Process Manager. Useful since EQ defaults to single-core, causing bottlenecks
- **Process Manager** — dedicated window (tray menu or Settings) showing all running EQ processes with PID, priority, and affinity. Apply settings to already-running clients or configure for future launches
- **Picture-in-Picture overlay** — live preview of alt EQ windows overlaid on your screen using DWM thumbnails. Toggle via tray menu. Click-through overlay positioned in bottom-right corner
- **PiP zoom on hover** — hovering over a PiP thumbnail shows a 2× magnified popup to the left. Uses a second DWM thumbnail at double resolution. Toggle in Settings
- **Active window highlight border** — colored border overlay highlights which EQ window is currently focused. Configurable hex color (default: green). Toggle from tray or Settings
- **Auto-minimize inactive EQ clients** — automatically minimizes background EQ windows when switching between clients. Reduces GPU/CPU load on lower-end hardware. Toggle from tray or Settings
- **Taskbar flash suppression** — stops background EQ windows from flashing their taskbar buttons. Toggle from tray or Settings
- **Window Presets** — save/restore named window layouts (position + size per client). Save current layout, load presets from tray menu or Settings
- **FixWindows offset tuning** — configurable top/bottom pixel offsets for fine-tuning window positioning
- **Open Log File redesign** — custom GUI with ComboBox of recent characters and inline error display
- **Multi-monitor enable/disable** — checkbox to enable or disable the multi-monitor toggle hotkey
- **Desktop Shortcut button** — one-click button to create an EQSwitch shortcut on your Desktop

### Settings GUI Redesign
- **Compact layout** — reduced vertical height with side-by-side layouts and tighter spacing
- **Args + Server side-by-side** — launch arguments and server name share a single row
- **Gina + Notes side-by-side** — Gina path and Notes file share a row with inline Browse buttons
- **Merged sections** — "Launch Options" and "Tray Icon" combined into "Launch & Tray Options"
- **Window Extras section** — 4 toggles (border, auto-minimize, flash suppress, PiP zoom) and border color config
- **Tray toggle checkmarks** — Active Border, Auto-Minimize, and Flash Suppress show check marks in tray menu

### Removed
- **Profile system** — removed character profiles, profile quick-launch, per-profile eqclient.ini, and batch backup/restore. EQ doesn't support command-line character selection, so the profile launch feature never worked
- **Beep on window switch** — caused audio device issues (added in v1.2)

### Performance
- **Flash suppress + auto-minimize on switch only** — now only fire on active window change instead of every 250ms tick
- **GetVisibleEqWindows() cache** — 200ms TTL cache eliminates duplicate `WinGetList` + sort between timers
- **Unified feature timer engine** — single 250ms timer handles all window features; no overhead when all features are off
- **ShowTip reusable function object** — single function at startup instead of allocating a new lambda per call

### Bug Fixes
- **PiP zoom cleanup on DWM failure** — zoom GUI destroyed if `DwmRegisterThumbnail` fails, preventing black rectangles
- **WinGetStyle race condition** — window closing mid-enumeration no longer crashes the script
- **Border color validation** — invalid hex colors fall back to green instead of error-looping
- **Window preset menu closure bug** — fixed AHK v2 closure variable capture with `.Bind()`
- **Tray toggle + Settings conflict** — tray toggles now blocked while Settings is open to prevent silent reverts
- **ToolTip auto-dismiss** — multi-monitor "OFF" tooltip no longer persists forever
- **LaunchOne re-entry guard** — 3-second debounce prevents rapid double-click spawning extra clients
- **Process Manager Close button** — X button now properly destroys the GUI
- **Settings changes mid-launch** — launch snapshots settings at start so mid-launch changes don't affect behavior
- **ShowTip dismiss timer** — rapid calls no longer stack timers causing premature dismissal
- **PiP flicker fix** — swaps DWM thumbnail sources in-place instead of recreating the overlay

### Robustness
- **OnExit cleanup handler** — properly cleans up PiP overlays, border GUIs, and timers on exit
- **Character name validation** — validates against `[A-Za-z0-9_-]` to prevent path traversal
- **Hotkey rollback on bind failure** — previous hotkey restored if new one fails to bind
- **Process handle safety** — try-finally blocks guarantee process handles are closed on exceptions
- **DWM thumbnail error handling** — all call sites check return values and clean up on failure
- **PiP zoom source tracking** — zoom targets correct source window after window changes
- **PiP reposition on display change** — repositions if monitor work area changed
- **Process Manager error recovery** — GUI errors properly clear the open flag
- **LaunchOne settings snapshot** — uses captured values instead of live globals during launch

### Code Quality
- **GetRecentCharList() helper** — extracted repeated `RECENT_CHARS` parsing into shared helper
- **ShowTip helper** — extracted ~38 repeated ToolTip+SetTimer patterns into `ShowTip(msg, ms?)`
- **Profile cleanup** — removed profile dropdown, load/save/delete buttons, custom ini path, batch operations from Settings
- **.gitignore cleanup** — replaced individual exe entries with `*.exe` wildcard

## v1.5 — 2026-03-08

### Bug Fixes
- **SwitchWindow crash** — `WinActivate` now wrapped in `try` to prevent crash if an EQ window closes mid-switch (race condition)
- **FixWindows crash on monitor disconnect** — `MonitorGetWorkArea` calls now inside `try/catch` in both `sidebyside` and `multimonitor` modes, preventing crash if monitor topology changes
- **ToggleMultiMon crash on monitor disconnect** — same `MonitorGetWorkArea` protection applied to the multi-monitor toggle hotkey
- **OpenGina working directory** — Gina now launches with its own directory as the working directory, preventing it from failing to find config/data files
- **Client count cap** — `NUM_CLIENTS` is now capped at 8 (was unlimited), preventing accidental launch of hundreds of clients
- **FixWindows consistency** — now uses `GetVisibleEqWindows()` instead of raw `WinGetList`, consistent with all other window operations and avoiding hidden windows
- **Silent hotkey binding failure** — `BindHotkey` and `BindMultiMonHotkey` now return success/failure; Settings shows a warning dialog if a hotkey couldn't be bound (invalid key or already in use)

## v1.4 — 2026-03-08

### Bug Fixes
- **SwapWindows crash** — fixed unhandled exception if a window closes mid-swap (`WinGetPos` now wrapped in try/catch)
- **LaunchBoth crash on malformed config** — `Integer()` calls now catch non-numeric values in `NUM_CLIENTS`, `LAUNCH_DELAY`, and `LAUNCH_FIX_DELAY` instead of crashing
- **Client count validation** — Settings now validates the number of clients is a positive integer; invalid values are corrected on save

### Code Quality
- **SwitchWindow refactor** — replaced 17 lines of duplicated window-finding and sorting logic with the existing `GetVisibleEqWindows()` helper
- **README accuracy** — updated opening description from "two game clients" to "multiple game clients"; added `/compress 0` flag to compile command example

## v1.3 — 2026-03-08

### New Features
- **Multi-monitor window arrangement** — new `multimonitor` FixWindows mode distributes one EQ window per monitor, maximized. Cycles through monitors if you have more windows than screens. Ideal for dual-monitor boxing
- **Multi-monitor toggle hotkey** — configurable global hotkey (default: Right Alt + M) that cycles through multi-monitor states: spread windows across monitors, swap which client is on which monitor, and stack back on primary. Handles resolution differences between monitors automatically
- **Swap Windows** — new tray menu item that rotates all EQ window positions (swaps which client is on which monitor/position)
- ~~**Character profiles**~~ — _removed in v1.8 (profile system depended on EQ supporting command-line character selection, which it doesn't)_

## v1.2 — 2026-03-08

### New Features
- **Launch N Clients** — configurable client count (default 2), replaces hardcoded "Launch Both"
- **Restore from Backup** — restore character files from Desktop with confirmation dialog
- **Configurable FixWindows** — choose maximize, restore, or side-by-side window arrangement
- **Run at Startup** — checkbox in Settings to auto-launch with Windows
- ~~**Sound feedback**~~ — _beep on window switch (removed in v1.7)_
- **Server name** — configurable server name for log/ini file paths (default: dalaya)
- **Progress tooltips** — visual feedback during multi-client launch sequence
- **Version display** — version shown in tray tooltip and Settings title bar

### Bug Fixes & Hardening
- **Config migration** — automatically migrates settings from old `[EQ2Box]` section to `[EQSwitch]`
- **Launch error handling** — validates EQ executable path before launching; shows helpful message
- **Backup error handling** — file copy failures now show specific error instead of crashing
- **Settings validation** — warns on invalid EQ path; prevents crash on empty hotkey field
- **Empty hotkey safety** — clearing the hotkey no longer throws an exception

### Code Quality
- Extract `GetEqDir()` helper — replaces 5 duplicated regex patterns with `SplitPath`
- Menu bold uses text matching instead of fragile hardcoded positions
- ComboBox refresh uses AHK v2 built-ins instead of raw Win32 SendMessage
- Standardized tooltip auto-dismiss timing via `TOOLTIP_MS` constant
- Launch delays now configurable via `LAUNCH_DELAY` and `LAUNCH_FIX_DELAY` config keys

## v1.1 — 2026-03-08

- Add Dalaya Fomelo link to tray context menu
- Add AV false positive FAQ to README
- Remove tracked .exe binary from repo; distribute via GitHub Releases
- Add MIT LICENSE file
- Clean up .gitignore

## v1.0 — 2026-03-06

- Initial release
- Window switching via configurable hotkey (scoped to EQ window only)
- Tray menu: Launch Client, Launch Both, Fix Windows
- Open Log File, eqclient.ini, Gina, and Notes from tray
- Dalaya Wiki and Shards Wiki links
- Settings GUI with hotkey picker, EQ path, Gina path, notes file
- Character file backup to Desktop with recent names dropdown
- Tray icon customization (double-click launch, middle-click notes)
- First-run auto-open Settings with welcome tooltip
- Config stored in portable INI file next to the exe
