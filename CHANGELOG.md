# Changelog

All notable changes to EQ Switch are documented here.

## v1.8 — 2026-03-09

### Removed
- **Profile system removed** — removed character profiles, profile quick-launch, per-profile eqclient.ini, and batch backup/restore. EQ doesn't support command-line character selection (`-args`), so the profile launch feature never worked. The eqclient.ini swap mechanism built on top of it was fragile and risked corrupting the user's original ini file
- **Crash recovery for ini swap** — removed startup recovery block that detected and restored orphaned eqclient.ini backups (no longer needed without the swap mechanism)

### Fixed
- **ToolTip auto-dismiss** — `ToggleMultiMon` "OFF" tooltip now uses `ShowTip()` so it auto-dismisses instead of persisting on screen indefinitely (P1-01)
- **LaunchOne re-entry guard** — `LaunchOne()` now blocks if `LaunchBoth()` is in progress, and has a 3-second debounce to prevent rapid double-click spawning extra clients (P1-03)
- **Process Manager Close button** — clicking the X button now properly destroys the GUI instead of just resetting the flag (P2-01)
- **Settings changes mid-launch** — `LaunchBoth()` now snapshots EQ path, args, priority, affinity, and window mode at launch start so changing Settings during the async launch chain won't affect behavior (P2-02)
- **Window preset count** — tooltip now shows "(N of M windows)" when a preset is loaded with fewer windows than saved (P2-03)

### Code Quality
- **Settings GUI cleanup** — simplified Character Config section (removed profile dropdown, load/save/delete buttons, custom ini path, batch operations)
- **Help dialog cleanup** — removed profile-related help text

## v1.7 — 2026-03-08

### New Features
- **Process Priority Management** — auto-set eqgame.exe to High (or AboveNormal) priority on launch. Configurable dropdown in Settings. Replaces need for Process Lasso
- **CPU Affinity Control** — configure which CPU cores eqgame.exe can use via the Process Manager. Useful since EQ defaults to single-core, causing bottlenecks
- **Process Manager** — dedicated window (tray menu or Settings) showing all running EQ processes with PID, priority, and affinity. Apply settings to already-running clients or configure for future launches
- ~~**Profile Quick-Launch**~~ — _removed in v1.8 (EQ doesn't support command-line character selection, making profile launch non-functional)_
- ~~**Per-Profile eqclient.ini**~~ — _removed in v1.8 (dependent on profile quick-launch)_
- **FixWindows offset tuning** — configurable top/bottom pixel offsets for fine-tuning window positioning. Top offset adjusts Y start, bottom offset extends past work area into taskbar zone
- **Window Presets** — save/restore named window layouts (position + size for each client). Save current layout, load presets from tray menu or Settings. Goes beyond FixWindows for custom arrangements
- **Picture-in-Picture overlay** — live preview of alt EQ windows overlaid on your screen using DWM thumbnails. Toggle via tray menu. Click-through overlay positioned in bottom-right corner

### Bug Fixes & Code Quality
- **Process Manager single-instance guard** — prevents opening multiple Process Manager windows simultaneously
- **PiP flicker fix** — swaps DWM thumbnail sources in-place instead of destroying and recreating the overlay on every window switch
- **LaunchOne timer consolidation** — priority and affinity now applied in a single deferred timer instead of two racing anonymous timers
- **Path validation on save** — Settings now warns if Gina or Notes paths don't exist (matching existing EQ exe validation)
- **ShowTip helper** — extracted repeated ToolTip+SetTimer dismiss pattern (~38 instances) into a `ShowTip(msg, ms?)` helper
- **Font consistency** — Character Config section now uses same `s9` font as the rest of Settings
- **Beep feature removed** — removed dead beep references; CHANGELOG updated to reflect removal
- **.gitignore cleanup** — replaced individual exe entries with `*.exe` wildcard
- **README compile path** — updated to reference bundled `./Ahk2Exe.exe` with Git Bash `MSYS_NO_PATHCONV=1` note

## v1.6 — 2026-03-08

### Settings GUI Redesign
- **Compact layout** — reduced vertical height significantly by using side-by-side layouts and tighter spacing
- **Args + Server side-by-side** — launch arguments and server name now share a single row
- **Gina + Notes side-by-side** — Gina path and Notes file share a row with inline Browse buttons
- **Merged sections** — "Launch Options" and "Tray Icon" combined into a single "Launch & Tray Options" section
- **Checkboxes paired** — beep/dbl-click and middle-click/startup are on shared rows
- **Larger Browse button** — EQ exe now has a proper "Browse..." button (w66) instead of tiny "..."
- **Backup section compact** — character rows use inline labels instead of stacked

### New Features
- **Open Log File redesign** — replaced Windows InputBox with a custom GUI using ComboBox of recent characters with inline error display
- **Multi-monitor enable/disable** — new checkbox to enable or disable the multi-monitor toggle hotkey; when unchecked, the hotkey field is grayed out and the hotkey is unbound
- **Desktop Shortcut button** — one-click button to create an EQSwitch shortcut on your Desktop
- ~~**Beep on window switch**~~ — _removed in v1.7 (added in v1.2, caused audio device issues)_

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
