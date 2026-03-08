# Changelog

All notable changes to EQ Switch are documented here.

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
- **Character profiles** — save named groups of characters (e.g., "Raid Duo", "Farm Team") for quick batch backup/restore. Profiles are stored in the config file and persist between sessions
  - **Load** — populates the character dropdown with the profile's characters
  - **Save As...** — saves your current recent characters as a new named profile
  - **Delete** — removes a saved profile
  - **Backup All in Profile** — backs up all characters in the selected profile to Desktop
  - **Restore All in Profile** — restores all characters in the selected profile from Desktop

## v1.2 — 2026-03-08

### New Features
- **Launch N Clients** — configurable client count (default 2), replaces hardcoded "Launch Both"
- **Restore from Backup** — restore character files from Desktop with confirmation dialog
- **Configurable FixWindows** — choose maximize, restore, or side-by-side window arrangement
- **Run at Startup** — checkbox in Settings to auto-launch with Windows
- **Sound feedback** — optional beep on window switch (off by default)
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
