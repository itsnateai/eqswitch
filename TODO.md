# EQSwitch C# Port — Feature Tracking

Port of EQSwitch v2.1 (AHK v2) to C# (.NET 8 WinForms).

## Legend
- [ ] Not started
- [~] Partial / in progress
- [x] Done and tested

---

## Milestone 1: Tray Icon + Process Detection
- [x] Project scaffold compiles
- [x] App launches, shows tray icon with eqswitch.ico
- [x] Single-instance mutex (prevents duplicate)
- [x] Detect running eqgame.exe processes (polling timer)
- [x] List detected clients in tray context menu (PID, title, character name)
- [x] Tray tooltip shows client count ("EQSwitch - N clients")
- [x] Balloon tip on client discovered/lost
- [x] Exit menu item with clean shutdown

## Milestone 2: Global Hotkeys
- [x] RegisterHotKey via hidden message-only NativeWindow
- [x] Alt+1 through Alt+6 — switch to client by slot
- [x] EQ Switch Key (default `\`) — cycle when EQ is focused
- [x] Global Switch Key (default `]`) — pull EQ to front from any app
- [x] Alt+G — arrange windows in grid
- [x] Alt+M — toggle multimonitor mode (placeholder — full layout in M5)
- [x] Launch One / Launch All hotkeys (placeholder — full logic in M6)
- [x] MOD_NOREPEAT to prevent key-held spam
- [x] Log which hotkeys register successfully
- [x] Low-level keyboard hook (WH_KEYBOARD_LL) for single-key hotkeys
- [x] Context-sensitive SwitchKey (EQ-only via process filter)

## Milestone 3: CPU Affinity + Process Priority
- [x] Set process priority (Normal, AboveNormal, High) via SetPriorityClass
- [x] Set CPU affinity mask via SetProcessAffinityMask
- [x] Active client → P-cores (0xFF), background → E-cores (0xFF00)
- [x] Per-character affinity override
- [x] Affinity re-apply on launch (with retry — EQ resets it)
- [x] Process Manager GUI (ListView: PID, Title, Priority, Affinity)
- [x] Core count detection (GetProcessAffinityMask + Environment.ProcessorCount)
- [x] "All" / "None" quick-select for core checkboxes
- [x] Force Apply button (Process Manager + tray menu)
- [x] Diagnostic tray menu item showing current affinity masks
- [x] Timer-based foreground tracking (250ms) for automatic affinity switching
- [x] Affinity/priority reset to defaults on shutdown

## Milestone 4: Config Persistence + First Run
- [x] JSON config (eqswitch-config.json) load/save
- [x] Auto-backup rotation (keep last 10)
- [x] First-run dialog (EQ path picker with eqgame.exe validation)
- [x] Portable config (alongside exe, not AppData)
- [x] All 40+ config keys migrated from AHK INI format
- [x] Config hot-reload on settings apply
- [x] Character export/import (JSON)
- [x] Corrupt config auto-backup and recovery

## Milestone 5: Window Management
- [x] Fix Windows — arrange all EQ windows by layout mode
- [x] Single-screen mode: grid layout on target monitor
- [x] Multi-monitor mode: one window per monitor, full-screen
- [x] Swap Windows — rotate window positions (context menu + reusable method)
- [x] Remove/restore title bars
- [x] Y-offset for title bar adjustment (TopOffset config)
- [x] Hung window detection (IsHungAppWindow — skip in arrange, abort in swap)
- [x] Multi-monitor toggle (Alt+M) with 500ms debounce + config persistence

## Milestone 6: Launching
- [x] Launch One — single client with configured exe + args
- [x] Launch All — staggered multi-client launch (1-8, configurable)
- [x] Launch delay (LaunchDelayMs, default 3s between clients)
- [x] Fix delay (FixDelayMs, default 15s before arranging windows)
- [x] Auto-apply process priority + affinity after launch (via ProcessManager discovery events)
- [x] Affinity retry (already wired in M3 — ClientDiscovered → ScheduleRetry)
- [x] Balloon progress ("Launching client X of N", "Ready to play!")
- [x] Debounce (3s for LaunchOne, g_launchActive flag for LaunchAll)
- [x] Bold tray menu items for Launch Client / Launch All

## Milestone 7: Settings GUI
- [x] General tab (EQ path, exe name, args, process name, polling interval)
- [x] Hotkeys tab (Switch Key, Global Switch, Arrange, MultiMon, Launch One/All)
- [x] Layout tab (mode, grid, target monitor, top offset, title bars)
- [x] Affinity tab (enabled, masks as hex, priorities, retry count/delay)
- [x] Launch tab (num clients, launch delay, fix delay)
- [x] Save/Apply/Close buttons with config persistence
- [x] Settings menu item in tray (single-instance window)
- [x] Dark theme (matching FirstRunDialog style)
- [x] PiP settings tab (size, opacity, border, max windows)
- [x] Paths section — Gina, Notes (done in M10)
- [x] Character Backup section (export/import in Characters tab)
- [x] Desktop shortcut creation (tray menu item)

## Milestone 8: Picture-in-Picture (PiP) Overlay
- [x] DWM thumbnail registration (DwmRegisterThumbnail)
- [x] Live preview of up to 3 alt EQ windows
- [x] Click-through (WS_EX_TRANSPARENT + WS_EX_LAYERED + WS_EX_NOACTIVATE)
- [x] Ctrl+drag repositioning
- [x] Position persistence (save to config)
- [x] Auto-swap sources on window switch (250ms affinity timer)
- [x] Auto-destroy when <2 windows remain
- [x] Size presets (S/M/L/XL/XXL/Custom)
- [x] Opacity control (0-255 via DWM_TNP_OPACITY)
- [x] Optional colored border (Green/Blue/Red/Black)
- [x] 500ms refresh timer for stale source cleanup
- [x] Toggle PiP menu item in tray

## Milestone 9: Video Settings / eqclient.ini Editor
- [x] Read/write eqclient.ini [VideoMode] section
- [x] Resolution presets (7 including Custom)
- [x] Custom resolution values (width/height numerics)
- [x] Window offsets (X, Y)
- [x] WindowedMode toggle
- [x] Title Bar Offset (TopOffset) — saves to both config and eqclient.ini
- [x] Save/Close with restart hint
- [x] Video Settings menu item in tray

## Milestone 10: File Operations
- [x] Open Log File (recent-first picker from Logs folder, up to 10 entries)
- [x] Open eqclient.ini in default text editor
- [x] Open GINA (launch from configured path, error if not set)
- [x] Open Notes (auto-create eqswitch-notes.txt, default path alongside exe)
- [x] Files submenu in tray context menu
- [x] Paths tab in Settings GUI (GINA path, Notes path with Browse buttons)
- [x] Paths synced through ReloadConfig

## Milestone 11: Tray Behaviors + Polish
- [x] Double-click tray: launch one EQ client
- [x] Middle-click tray: toggle PiP overlay
- [x] Triple-click detection with cooldown (500ms window → arrange windows)
- [x] Run at Windows startup (Registry-based HKCU\Run)
- [x] Help window (scrollable, resizable, Consolas monospace, full hotkey reference)
- [x] Tray menu hotkey suffixes (tab-separated display on Fix Windows, Launch items)
- [x] Bold menu items for launch actions (already done in M6, confirmed)
- [x] Links submenu (Dalaya Wiki, Shards Wiki, Fomelo)
- [x] Run at Startup toggle in tray menu (checkbox, persists to config)

## Milestone 12: Migration + Release
- [x] Auto-import from AHK eqswitch.cfg (INI → JSON migration with hotkey syntax conversion)
- [x] Character export/import (JSON) — done in M4
- [x] Single-file publish command documented in README
- [ ] VirusTotal clean scan (after first publish)
- [x] README update — full feature list, project structure, migration table
- [x] CHANGELOG entry — v2.0.0 with all features listed
