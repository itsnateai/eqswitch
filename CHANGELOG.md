# Changelog

## v2.5.0 — Quality of Life (in progress)

### Added
- **Reset Defaults button in Video Settings form** — resets Width/Height (1920×1080), offsets (0,0), Windowed Mode (on), Disable Log (off), Title Bar Offset (0). Matches AHK v2.4 `ResetVMDefaults`. Requires Save or Apply to write to disk.

---

## v2.4.0 — Tray UX Overhaul (2026-03-14)

### Added
- **Configurable tray click actions** — Settings → General tab lets users bind single/double/triple/middle-click to specific actions (Launch One, Fix Windows, Open Settings, etc.)
- **Custom video presets** — Save up to 3 custom resolutions in Video Settings (FIFO eviction, duplicates skipped)
- **Dark-themed context menus** — `DarkMenuRenderer` applies dark background/foreground to all tray menu items
- **FloatingTooltip** — replaces `MessageBox.Show` "already running" popup with a non-blocking floating tooltip

### Changed
- **Tray context menu reorganized** into grouped submenus (Video Settings, Settings, Launcher)
- **Medieval emoji/icon prefixes** restored on all tray menu items (matches AHK v2.4 style)
- **CPU Affinity submenu simplified** — removed per-core checkboxes, shows info labels only
- **Process Manager** restyled with dark DataGridView theme
- **First-run** now auto-opens Settings instead of requiring manual navigation

---

## v2.3.0 — Performance & Fullscreen (2026-03-13)

### Added
- **Background FPS throttling** (`Core/ThrottleManager.cs`) — duty-cycles background EQ clients via `NtSuspendProcess`/`NtResumeProcess`. Configurable throttle percent (0-90%) and cycle interval. Active client is never throttled. Settings on Affinity tab.
- **Borderless fullscreen mode** — WinEQ Y+1 offset trick: strips window decorations and positions at `(monitor.Left, monitor.Top+1)` using `rcMonitor` bounds. Preserves Alt+Tab and PiP window overlay. Checkbox on Layout tab.

---

## v2.2.0 — Production Hardening (2026-03-12)

### Added
- **Persistent file logging** (`Core/FileLogger.cs`): Info/Warn/Error with timestamp, 1MB rotation, thread-safe
- **Input validation** (`AppConfig.Validate()`): Clamps all numeric config fields to safe ranges on load and save
- **IWindowsApi interface** (`Core/IWindowsApi.cs`): Abstraction layer for Win32 calls, enables unit testing
- **79 unit tests** across 7 test files: AppConfig, WindowManager, AffinityManager, ConfigManager, ConfigMigration, HotkeyManager, EQClient
- **Solution file** (`EQSwitch.sln`): Main project + xUnit test project with Moq

### Fixed
- **P2-02**: Context menu client labels now update when window titles change
- **Concurrency**: KeyboardHookManager uses `ImmutableHashSet<int>` (lock-free) instead of `HashSet<int>` with lock
- **Concurrency**: ProcessManager fires events outside lock block, uses specific exception catches
- **Concurrency**: AffinityManager snapshots retry counters before iterating
- **Resource leak**: LaunchManager implements IDisposable, cancels launches on dispose
- **Resource leak**: ProcessManagerForm stops refresh timer in Dispose()
- **Backup pruning**: ConfigManager sorts by file write time instead of filename string
- **Hotkey ID overflow**: `_nextId` resets to 1 on `UnregisterAll()` (P4-01)
- **Exception hardening**: DWM HRESULT mapped to readable messages in PipOverlay
- **Exception hardening**: VideoSettingsForm retries file I/O (2x, 500ms)
- **Magic numbers**: Named constants replace all magic numbers across 6 files

### Changed
- All diagnostic logging migrated from `Debug.WriteLine` to `FileLogger`
- WindowManager and AffinityManager accept optional `IWindowsApi` for dependency injection

---

## v2.1.1 — Post-Release Audit Fixes (2026-03-12)

### Fixed
- **P0-01**: Hook callback dispatched async via SynchronizationContext.Post() — prevents Windows killing the LL hook on slow callbacks
- **P0-02**: Global switch key `]` no longer swallowed when zero EQ clients running (requireClients guard + cached PID check)
- **P0-03**: PiP overlay Ctrl+drag works — replaced WS_EX_TRANSPARENT with dynamic WM_NCHITTEST (HTTRANSPARENT default, HTCLIENT when Ctrl held)
- **P0-04**: eqclient.ini read/write uses ANSI encoding instead of UTF-8 to prevent corruption
- **P1-01**: Eliminated Process.GetProcessById() in hook callback — cached PID HashSet with GetWindowThreadProcessId
- **P1-02**: Screen.PrimaryScreen null-safe fallback for headless/RDP disconnect
- **P1-03**: All eqclient.ini file operations use Encoding.Default consistently
- **P1-04**: Triple-click tray detection resets timestamp on every click
- **P1-05**: Run-at-startup registry path validated on launch — auto-corrects if exe moved
- **P1-06**: LaunchManager timers cancelled on config reload and shutdown
- **P1-07**: Minimum 500ms enforced between staggered launches
- **P1-08**: ContextMenuStrip disposed in Shutdown path
- **P1-09**: Previous custom icon disposed on LoadIcon reload

---

## v2.1.0 — Deferred Features (2026-03-11)

### Added
- **Process Manager GUI**: Live view of all EQ clients with PID, character name, priority, and affinity mask. Auto-refreshes every second. Includes Force Apply button.
- **PiP Settings tab**: Configure PiP size preset, custom dimensions, opacity, border color, and max windows from Settings GUI
- **Characters tab**: View character profiles with Export/Import buttons for JSON backup
- **All Cores / Clear buttons**: Quick-select on Affinity tab to set masks to system max or minimum
- **Force Apply Affinity**: Tray menu item to re-apply affinity rules to all clients immediately
- **Triple-click tray**: Triple-click the tray icon within 500ms to arrange all windows
- **Desktop shortcut**: Create Desktop Shortcut menu item via WScript.Shell COM

### Changed
- "Process Info" balloon replaced with full Process Manager window
- PiP config now persists through Settings GUI (was only configurable via JSON)
- ReloadConfig now includes PiP settings for hot-reload

---

## v2.0.0 — C# Port (2026-03-11)

Complete rewrite from AutoHotkey v2 to C# (.NET 8 WinForms).

### Added
- **Settings GUI**: 6-tab dark-themed settings dialog (General, Hotkeys, Layout, Affinity, Launch, Paths)
- **Video Settings**: Read/write eqclient.ini [VideoMode] section with resolution presets
- **PiP Overlay**: DWM thumbnail-based live previews of background EQ clients
  - Click-through overlay (won't steal focus)
  - Ctrl+drag repositioning with position persistence
  - Auto-hide when fewer than 2 clients
- **Window Swap**: Rotate window positions (1→2, 2→3, N→1)
- **Hung window detection**: Skip unresponsive windows during arrange/swap operations
- **Files submenu**: Quick access to log files, eqclient.ini, GINA, notes
- **Links submenu**: Dalaya Wiki, Shards Wiki, Fomelo
- **Help window**: Full hotkey reference and feature guide
- **Run at Startup**: Registry-based toggle in tray menu
- **Tray interactions**: Double-click to launch, middle-click for PiP
- **Hotkey suffixes**: Keyboard shortcuts shown in tray menu items
- **Config migration**: Auto-import from AHK eqswitch.cfg on first run

### Changed
- Config format: INI → JSON (eqswitch-config.json)
- Config backups: automatic rotation (keeps last 10)
- Hotkey system: RegisterHotKey + WH_KEYBOARD_LL (was AHK native)
- Window arrangement: proper multi-monitor support via EnumDisplayMonitors
- CPU affinity: configurable retry on launch (EQ resets affinity after startup)
- Build output: .NET single-file publish (no AV false positives)

### Removed
- AutoHotkey dependency
- Flash suppress / auto-minimize (superseded by PiP + affinity management)
