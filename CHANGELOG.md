# Changelog

## v3.12.1 — iter-12 MQ2-style structural lookup foundation (dormant) (2026-04-26)

### Changed
- No user-visible behavior change vs v3.12.0. Both clients enter password and reach in-world cleanly, ~63s end-to-end (verified dual-box 2026-04-26).

### Added (dormant, default-off)
- `Native/eqmain_widgets_mq2style.{h,cpp}` — MQ2-style structural recursion through `CXWnd`'s TListNode + TList multiple-inheritance layout. `FindLiveScreenByName` + `RecurseAndFindName` + `FindChildByName` with heuristic `CStrRep` CXStr name match. Wired through `FindLivePasswordCEditWnd` (in `eqmain_widgets.cpp`) and the `LOGIN_ConnectButton` lookup (in `login_state_machine.cpp`) with legacy heap-cross-ref fallback.
- `kMQ2StyleWidgetLookup = false` master toggle in the new header. Both call sites skip MQ2-style entirely; behavior matches v3.12.0 baseline.

### Pinned offsets (foundation for future)
- `CXWnd::pNext` `+0x08` (TListNode<CXWnd> base, runtime-validated).
- `CXWnd::pFirstChild` `+0x10` (TList<CXWnd> base, runtime-validated).
- `CXWnd::dShow` `+0x196` (slot 68/69 ICF body — `IsVisible() && !IsMinimized()`).
- `CXWnd::Minimized` `+0x1CE` (free byproduct of slot 68/69).
- `CSidlManagerBase::XMLDataMgr` `+0x144` (CXMLDataManager-base offset within the contained `CXMLParamManager`).

### Known (do not enable kMQ2StyleWidgetLookup without redesign)
- iter-12's MQ2-style walks invoke `IterateAllWindowsPublic` from `LoginStateMachine::Tick` (via the `LoginController::GiveTime` detour), which runs on EQ's game thread. That same thread services `IDirectInputDevice8::GetDeviceState`, the path delivering SHM-injected BURST keystrokes. With the toggle on, the background client's `GetDeviceState` polling stalled while the walk was in flight, dropping password keystrokes. Foreground client has a Win32 keyboard fallback path that bypasses `GetDeviceState`, so it landed clean — hence deterministic foreground-OK / background-fail. Confirmed by 4 dual-box test runs and 3 independent code-review agents at 75% confidence. Toggle off restores v3.12.0 behavior.
- Future v6 design (Combo G primary): direct memory write to `InputText` CXStr at `+0x1A8`, skip BURST keystrokes entirely. Foundation laid by these pinned offsets + the dormant MQ2-style code.

## v3.11.3 — Combo G read-back + ConnectButton vtable gate + ~6s autologin (2026-04-25)

### New
- **Combo G CStrRep_Dalaya layout corrected** (`Native/eqmain_cxstr.h`) — utf8 verified at +0x14 via runtime hex dump; introduced `ownerPtr` field at +0x10 to document the eqmain-internal pointer that lives there. Live recon supersedes the 2013 disassembly comment.
- **`WriteEditTextDirect` read-back verification** (`Native/eqmain_cxstr.cpp`) — after `ConstructFromCStr` succeeds, the written CStrRep's `length` and first utf8 byte are verified against what was requested. Returns false (callers fall back to keystroke) on any mismatch. **Caught a real silent-success bug** where the function reported success while writing into the wrong widget memory.
- **`PHASE_CLICKING_CONNECT` vtable gate** (`Native/login_state_machine.cpp`) — `MQ2Bridge::FindWindowByName` returns a CXMLDataPtr def (vtable = eqmain DOS header) when no live `LOGIN_ConnectButton` widget exists. Pre-fix, the DLL called `MQ2Bridge::ClickButton` on the def, which silently early-returned, and the state machine advanced phase regardless. Now gated on `EQMainOffsets::IsEQMainButtonWidget`; if not a real `CButtonWnd`, retry up to 50 times then `SetError` so C# falls back loudly. Counter resets in `InvalidateWidgets` so a fresh login attempt starts clean.
- **C# SHM credentials warmup ritual** (`Core/AutoLoginManager.cs::RunLoginSequence`) — sends `LOGIN` SHM command and waits up to 15s for `phase >= WaitConnectResponse`. On Dalaya phase never advances past `ClickingConnect` (no live button), so the 15s timeout always fires — but the DLL's widget-discovery activity during that window warms up EQ's input subsystem so BURST 1 keystrokes land cleanly. Then BURST 1 runs unconditionally.
- **`g_password` redacted from DLL log** (`Native/eqmain_cxstr.cpp`) — `WriteEditTextDirect` now logs `textLen=N` + first-byte hex only, never the full string. Earlier diagnostic logged `text="Exodus"` (real password) into the DI8 log file.

### Performance
- Autologin landed at ~6s wait → in-world per dual-box test 2026-04-25. (Was timeout-bound around 35-50s previously.)

### Known limitations (next session)
- Combo G writes to `+0x1A8 InputText` successfully, but EQ renders/submits from a different buffer — direct SHM password injection still doesn't work end-to-end on Dalaya. BURST 1 keystrokes are the actual workhorse.
- Two parallel autologin paths (SHM warmup ritual + BURST 1 keystrokes) is confusing; warmup needs to be repurposed or replaced with a non-credential-attempt mechanism.

## v3.11.2 — autologin documentation honesty + load-bearing-warmup discovery (2026-04-25)

### Fixed
- Stale `LoginShm overall timeout (14s)` log message corrected to `(45s)` —
  the timeout was bumped to 45s in iter 15.2 but the message text was never
  updated, leading to false impressions when reading logs.
- Stale `// PATH A: ... DISABLED — native widget discovery needs a dedicated
  RE session.` comment block replaced with current reality + a
  ⚠ LOAD-BEARING SIDE EFFECT ⚠ warning. Combo G fixed widget discovery; the
  broken piece is now the DLL's post-connect detection. **PATH A's 45s
  timeout, although the "intended" login flow never completes, is incidentally
  serving as the warmup that PATH B's keystroke injection requires** — without
  it, BURST 1 fires at T+10s and EQ drops the first ~3 keystrokes (verified
  2026-04-25 by attempting C# disable, password truncated 6→3 chars, login
  failed, rolled back).

### Notes
- No behavior change vs v3.11.1 — only comments and one log message.
- The "skip PATH A entirely" win identified during analysis turned out to
  need a non-time-based BURST-1 readiness gate, not just commenting-out the
  if-block. Tracked as a future "D" task.

## v3.11.1 — `\` switch key now EQ-window-only (2026-04-25)

### Fixed
- **`\` (SwitchKey) was firing globally** — any press in chat, Discord, browsers, etc. was being swallowed by the keyboard hook. Now scoped to "EQ client window must be foreground" via the existing `processFilter` path. `]` (GlobalSwitchKey) remains genuinely global, as designed.
- Removed the cold-start "no EQ focused → focus first client" branch from the primary path of `OnSwitchKey` (left in as a defensive no-op). The previous EQ-only filter had been temporarily removed on 2026-04-24 to work around a broken-autologin foreground race; that race is gone now that autologin lands EQ as foreground end-to-end.

## v3.10.0 — GPL-2.0-or-later + Native v7/v8 login path (2026-04-18)

### Changed
- **License changed from GPL-3.0 to GPL-2.0-or-later** — ecosystem alignment with MacroQuest (MQ2) and the broader EverQuest tool community, which is uniformly GPLv2-only. GPL-2.0-or-later is upward-compatible with GPLv3 for anyone who wants it, while unlocking legitimate code-sharing with MQ2-derived work. Prior releases (v3.9.3 and earlier) remain GPL-3.0 forever. Tag `v3.9.3-last-gplv3` marks the relicense boundary.
- **README License section** — formal attributions added for **Stonemite** (DirectInput proxy approach studied, no code taken) and **MacroQuest** (character-select facts referenced, no source compiled in). SHM boundary between EQSwitch and MQ2 DLL reaffirmed.
- **README fan-made disclaimer** strengthened — EQSwitch is free, educational, independent, and not sold.
- **CONTRIBUTING.md** — contributor license grant updated to GPL-2.0-or-later.

### Native login reliability (v7)
- **GiveTime detour** replaces SetTimer-based polling — login state machine now rides the game's own game-loop tick (50–130 Hz), matching the MQ2 pattern for stable, high-frequency polling.
- **`LoginController*` fast-path** — when the controller pointer is already resolved, subsequent logins skip the full scan.
- **Charselect robustness** — detects `eqmain.dll` unload at character select and resumes the background poll instead of bailing.
- `LoginShmWriter` wired into the native path.

### Native widget discovery (v8, internal foundation)
- MQ2-style `eqmain.dll` detection with widget-ownership tracking.
- Corrected `SetWindowText` vtable slot + exact-vtable class gate for login-widget match.
- `HeapScanForWidget` — locates login widgets by SIDL name on the heap.
- Live `CXWnd` discovery: tree walk, heap cross-reference, label search.
- Improved `CXWndManager` diagnostics.

### Rationale (relicense)
Sole-author relicensing (verified via `git log`). No external contributors held copyright on any EQSwitch code. No user-visible behavior change from the relicense itself.

---

## v3.9.3 — Release Polish (2026-04-13)

### Changed
- Version bump for public release following v3.9.2 security hardening.

---

## v3.9.2 — Native Upgrade, WinGet Compat, Security Hardening (2026-04-13)

### Added
- **SHM-driven enter-world** — in-process `CLW_EnterWorldButton` click via shared-memory request/ack handshake (replaces earlier PostMessage approach for this step).
- **Charselect slot probe** — runtime validation that the resolved slot matches the user's intended character before commit.
- **Vtable guard around `GetChildItem`** — defensive check before calling into MQ2-exported thunks.

### Changed
- **WinGet-compatible distribution** — packaging adjustments for smooth WinGet manifest submission.
- **Security hardening for distribution** — installer / update path review, string scrubbing, no secrets in binaries.

### Fixed
- **Log-spam reduction** in native `NetDebug` output during charselect polling.

---

## v3.9.0 / v3.9.1 — Per-Account AutoEnterWorld + Naming Cleanup (2026-04-12 / 2026-04-13)

### Added
- **Per-account and per-team `AutoEnterWorld` flag** — granular control over which accounts auto-commit to character select vs. stop at the character screen.
- **DLL verification report** (`Native/VERIFICATION.md`) — independent reverse-engineering evidence for MQ2 export offsets used on Dalaya.
- **Volatile cross-thread fields** in native login state machine (C++ memory-model correctness fix surfaced by 9-agent audit that fixed 8 bugs).

### Changed
- **`LaunchTwo` → `LaunchAll`** terminology cleanup. Config migrated v2 → v3 (duplicate `LaunchTwo` removed).

---

## v3.6.0 — UI Polish, Log Trimming, Account Backup (2026-04-10)

### Added
- **Log trimming** — async stream-based trimmer with archive to `Logs/archive/`. Default threshold 50 MB; configurable.
- **Account backup / import** — DPAPI blobs portable across imports on the same Windows user.
- **Team submenu rework** — `LaunchTwo` for bare clients, Launch Team restored, all 4 teams in tray submenu.

### Changed
- **Hotkeys / Video / Accounts tabs** — card padding, conflict warnings, Windowed Mode relocated to Window Style card.

### Fixed
- **Native vtable validation** before `GetChildItem` call (prevents crash on Dalaya's variant EQ client).
- **Retry counter** for charselect window search with bounded cap (was unbounded busy-wait).
- **Lazy MQ2 init** — replaces blocking `Sleep(2000)` startup delay.
- **MemoryBarrier before SHM charCount write** — ordering fix for cross-thread reads.

---

## v3.5.0 — Background Input & 3-Layer Activation Defense (2026-04-09)

### Fixed
- **Background auto-login works end-to-end while EQ is unfocused.** Root cause was an inline `GetForegroundWindow` hook in `iat_hook.cpp` that only spoofed for callers within `eqgame.exe`'s address range — EQ's game loop calls from loaded DLLs fell outside that range. Three-layer fix:
  1. Inline hooks skip the caller check when SHM is active, so `GetForegroundWindow` / `GetFocus` / `GetActiveWindow` all return EQ's HWND.
  2. Persistent WndProc subclass blocks `WM_ACTIVATEAPP(FALSE)` / `WM_ACTIVATE(WA_INACTIVE)` / `WM_KILLFOCUS` / `WM_NCACTIVATE` with a 16 ms re-install timer.
  3. Activation blast on re-install after EQ's 3D char select overwrites the subclass.
- **Unconditional 200 ms re-post** of `WM_ACTIVATEAPP(1)` while SHM active (old self-check was defeated by the hook's own spoofing).
- `CallWindowProcA` → `CallWindowProcW` for Unicode compatibility.

---

## v3.4.3 — Suspended-Process Injection Architecture (2026-04-08)

### Changed
- **Replaced `dinput8.dll` proxy with CREATE_SUSPENDED process injection.** EQSwitch now injects `eqswitch-di8.dll` and `eqswitch-hook.dll` directly into `eqgame.exe` after resuming the loader (~50 ms). Dalaya's 1.3 MB MQ2 `dinput8.dll` stays untouched — no patcher conflicts, no server hash validation failures.

### Added
- **Character select Enter World** uses 250 ms key holds with 3 retry attempts and real title-change verification.
- **`ActivateThread`** continuously re-posts `WM_ACTIVATEAPP(1)` while SHM active to defend against focus loss.
- **Adaptive `WaitForScreenTransition`** — replaces fixed 3 s post-server-select sleep; polls `IsHungAppWindow` + `GetWindowRect` stability and handles any load time (5 – 90 s).

### Removed
- Dead proxy files (prior `dinput8.dll` proxy architecture).

---

## v3.4.2 — Self-Updater Completeness + Config Migration Framework (2026-04-08)

### Added
- **Self-updater handles all shipped files** — `EQSwitch.exe`, `eqswitch-hook.dll`, and `dinput8.dll` (previously missed `dinput8.dll`).
- **`--test-update` CLI flag** for simulating full update flow locally without a GitHub release.
- **Post-update toast notification** on relaunch.
- **`ConfigVersionMigrator`** — versioned framework that transforms raw JSON before deserialization; preserves user settings across property renames and type changes.

### Fixed
- **Retry logic for `.old` artifact cleanup** (race with memory-mapped exe).
- **CTS dispose race** in `UpdateDialog`.

---

## v3.4.0 — CI / Release Pipeline Cleanup (2026-04-05)

### Changed
- **Removed broken framework-dependent build from CI** — Release artifacts are self-contained single-file only.
- **Native DLLs bundled into release zip** — `eqswitch-hook.dll` (and later `eqswitch-di8.dll`) ship alongside the exe.

---

## v3.3.1 — Config Baseline & Defaults Overhaul (2026-04-04)

### Fixed
- **AppConfig defaults baselined to EQ's eqclient.ini** — nuclear reset now matches a fresh EQ install (22 booleans, 3 clip planes, mouse sensitivity, sound volume)
- **INI section targeting corrected** — ChatSpam writes to [Options] not [Defaults], Keymaps writes to [KeyMaps] not [Defaults], Particles routes FogScale/LODBias/SameResolution to [Options]
- **11 phantom [Defaults] writes removed** — Sky, BardSongs, Anonymous, ClipPlane, MouseSensitivity, ShadowClipPlane, ActorClipPlane and 4 others were being injected into [Defaults] where they don't belong; now write only to [Options]
- **LoadFromIni reads [Options] section** — settings that live in [Options] (EQ's runtime-authoritative section) are now read correctly on form open
- **SlowSkyUpdates EnforceOverrides** — now restores EQ default (3000ms) when unchecked instead of leaving 60000
- **SkyUpdateInterval ApplyToIni** — falls back to 3000 when no original value was captured
- **MouseSensitivity/SoundVolume LoadFromIni clamp** — minimum changed from 0 to -1 to preserve sentinel values
- **ForceWindowedMode** — now reads from [Defaults] in addition to [VideoMode]
- **DisableEQLog** — moved from AppConfig to EQClientIniConfig; LoadFromIni now reads Log key; ApplyToIni now writes it
- **ConfiguredKeys sentinel tracking** — numeric fields at sentinel values (-1 or 0) are removed from ConfiguredKeys instead of being tracked but never enforced
- **Maximized ConfiguredKeys gap** — now tracked when saved from SettingsForm
- **ProcessManagerForm FPS ConfiguredKeys** — MaxFPS/MaxBGFPS now tracked when saved from Process Manager
- **ChatSpamForm EnforceOverrides** — safe int serialization (`value != 0 ? "1" : "0"`) instead of raw `value.ToString()`
- **ModelsForm phantom writes on load failure** — _initialValues snapshot moved outside try block
- **Snapshot early-return bypass** — all 4 sub-forms restructured from `if (!exists) return` to `if (exists) { try...catch }` so snapshots run unconditionally
- **VideoModeForm XOffset/YOffset defaults** — changed from 1 to 0 (EQ default)
- **GDI font leak coverage** — added DisposeControlFonts to EQChatSpamForm, EQVideoModeForm, EQClientSettingsForm, FirstRunDialog, ProcessManagerForm, SettingsForm

### Changed
- ChatServerPort writes to [Options] (was [Defaults], key doesn't exist in fresh ini)
- Doc comments updated to accurately describe EQ defaults and section locations

## v3.1.0 — DirectInput Proxy, Background Auto-Login & Hook Upgrades (2026-04-01)

### Added
- **DirectInput proxy DLL** (`Native/dinput8.dll`) — IAT hook proxy that intercepts `GetForegroundWindow`, `GetAsyncKeyState`, and `GetKeyboardState` inside eqgame.exe. Per-PID shared memory injects scan codes into EQ's DirectInput keyboard device without stealing focus.
- **Background auto-login** — Types passwords into background EQ windows via DirectInput shared memory injection. True one-click multi-account login with no focus stealing.

- **SetWindowTextA hook** — Custom window titles now persist through zone transitions, login, and character select. The injected DLL intercepts EQ's own SetWindowTextA calls and substitutes the configured title. Same approach as WinEQ2.
- **ShowWindow hook** — Blocks EQ from minimizing itself on focus loss during DirectX init. Fixes the Maximize-on-Launch + no-Slim-Titlebar crash where EQ would get stuck minimized.
- **Auto hook injection** — Hook DLL now injects whenever any hook feature is needed (custom window title, maximize protection, or slim titlebar), not just when slim titlebar + hook is toggled on.
- **Video Settings description** — Added page description and "Monitor Selection" section title for clarity.
- **Resolution hint** — Yellow hint in Window Style card when slim titlebar is disabled, reminding to set EQ resolution to fit above the taskbar.
- **Help form auto-login section** — Documents background login status and dinput8.dll requirement.

### Fixed
- **Auto-login typing** — Switched from VK+scancode to KEYEVENTF_UNICODE for reliable text entry on EQ's login screen. FocusAndSendKey re-focuses before each keystroke to survive focus theft.
- **Hook injection during login** — DLL injection and slim titlebar guard are deferred until login sequence completes, preventing focus theft mid-login.
- **Window title not applied on discovery** — Titles now appear immediately when EQ is detected, not just during explicit arrange operations.
- **Build: TestInput sub-project conflict** — Excluded TestInput/ from default compile globbing to prevent duplicate assembly attribute errors.

### Changed
- Hook DLL shared memory struct extended with `blockMinimize` flag and 256-byte `windowTitle` buffer (284 bytes total, up from 24).
- **License changed to GPL-3.0** — Matches Stonemite's license (studied their DirectInput proxy approach).

---

## v3.0.1 — Per-Process Hook Shared Memory & Audit Fixes (2026-04-01)

### Changed
- **Per-process shared memory** — Each injected eqgame.exe gets its own memory-mapped file (`EQSwitchHookCfg_{PID}`) instead of a single global mapping. Hook DLL now works in both single and multimonitor modes with correct per-window positioning.
- **Atomic config writes** — `ConfigManager.FlushSave` writes to a temp file then `File.Move` to prevent config corruption on crash.

### Fixed
- **Hook configs not updated after ArrangeWindows/ToggleMultiMonitor** — Hook DLL would snap windows back to stale positions after "Fix Windows" or mode toggle.
- **Hook configs not updated after SwapWindows** — Multimonitor swap would be immediately undone by the hook.
- **DllInjector handle leaks** — `hThread` handles in both `Inject()` and `Eject()` moved into `finally` blocks.
- **DllInjector.Eject dead code** — Removed unused `allocAddr`/`VirtualFreeEx` and stale `ResolveLoadLibraryA` call.
- **GetExportRva missing guards** — Added `-1` checks after `RvaToFileOffset` calls for clearer PE parse errors.
- **HookConfigWriter resource leak** — `Open()` catch path now disposes both `MemoryMappedFile` and `ViewAccessor` on failure.
- **HookConfigWriter.Disable zeroed geometry** — Now read-modify-writes to only flip the `Enabled` flag.
- **Dead-PID injection race** — Timer tick guards against injecting into a process that died during the 2s delay.
- **Missing client early-return** — `UpdateHookConfigForPid` logs and returns when PID not in client list.
- **Stream leak in LoadIcon** — `GetManifestResourceStream` now disposed with `using`.
- **SettingsForm font leaks** — Inline fonts on labels and DataGridView header tracked and disposed.
- **Double-dispose of foreground debounce timer** — Removed redundant dispose in `TrayManager.Dispose`.
- **PID naming contract** — Cast to `uint` for shared memory name to match C++ `%lu` formatting.

---

## v3.0.0 — DLL Hook Injection, Auto-Login & PiP Overhaul (2026-04-01)

### Added
- **DLL hook injection** (`Core/DllInjector.cs`, `Native/eqswitch-hook.dll`) — Injects a native MinHook-based DLL into eqgame.exe that hooks `SetWindowPos` and `MoveWindow`. Enforces window position/style via shared memory-mapped config (`HookConfigWriter.cs`). Prevents EQ from fighting window management.
- **DPAPI-encrypted auto-login** (`Core/AutoLoginManager.cs`, `Core/CredentialManager.cs`) — Account presets with username, encrypted password, server, character name, and slot. Full enter-world automation via `SendInput` on a background thread. Credentials encrypted with `DataProtectionScope.CurrentUser` — only the same Windows user on the same machine can decrypt.
- **Login Accounts model** (`Models/LoginAccount.cs`) — Stored account presets for auto-login with name, username, encrypted password, server, character, slot, and login flag toggle.
- **PiP orientation support** — PiP overlays adapt to window orientation and layout changes.
- **Hook config shared memory** (`Core/HookConfigWriter.cs`) — Memory-mapped file (`EQSwitchHookCfg`) shared between C# host and injected DLL. Struct-matched layout (packed, sequential ints) for target position, style, and enable flag.
- **Native hook source** (`Native/`) — Full MinHook source (buffer, trampoline, HDE32/64 disassembler) plus `eqswitch-hook.cpp` with build scripts for MSVC and MinGW.

### Changed
- **Settings expanded** — New Auto-Login tab with account management, credential encryption, and launch integration.
- **PipOverlay enhanced** — 87 lines added for orientation-aware thumbnail rendering.
- **TrayManager expanded** (982 → 1549 lines) — Auto-login menu integration, DLL injection lifecycle, hook config management.
- **SettingsForm expanded** (734 → 1211 lines) — Auto-login account editor, DLL hook controls, PiP orientation settings.
- **README updated** with new feature descriptions.

### Removed
- **Unit test project** (`EQSwitch.Tests/`) — Removed during architecture transition. Tests covered v2.x patterns that no longer apply post-DLL injection.
- **Solution file** — Simplified to single-project build.
- **PLAN_DLL_HOOK.md** — Planning doc removed after implementation.

---

## v2.9.1 — Settings & Launch Cleanup (2026-03-30)

### Changed
- **Tray clicks simplified** — Removed triple-click entirely. Left button: single + double click. Middle button: single + triple (via click counting — `MouseDoubleClick` doesn't fire for middle on `NotifyIcon`).
- **Launch is bare-bones** — Removed `EnforceOverrides`, `EnforceWindowedModeIfBorderless`, `PositionOnTargetMonitor`, and post-launch `ArrangeWindows`. Launch just starts `eqgame.exe` with staggered delay. Added restore-if-minimized after 3s.
- **Settings UI cleanup** — Removed CtrlHoverHelp (unreliable in overflow tray). Human-readable switch mode labels ("Swap Last" / "Cycle All"). Tray Click Actions card redesigned. Preferences card alignment fixed.
- **Paths tab auto-open** — Clicking GINA or Dalaya Patcher in launcher menu opens Settings → Paths tab if path not set.
- **Tooltip Delay** — Renamed, supports 0 = disabled.
- **Multi-Monitor Mode checkbox** in Video Settings synced with config.

### Fixed
- **eqclient.ini corruption** — `EnforceOverrides` was writing `Maximized=1` and offsets=-8 on every launch, causing windows to minimize. Removed from launch path entirely.
- Hotkeys tab overlapping labels in Actions card — clean 2-column grid.

---

## v2.9.0 — UI Consolidation & Multi-Monitor (2026-03-30)

### Added
- **Multi-monitor video settings** — Monitor picker, per-monitor resolution, position preview.
- **Config backup restore** — Restore from any of the 10 backup rotations.

### Changed
- **Tabs consolidated** — Merged Performance + Launch into Hotkeys tab. Reduced from 8 to 6 tabs.
- **Stacked fullscreen as default layout** — Clients stack on top of each other, arranged in stacked mode.
- **FPS writes to [Options] section** — Correct INI section for MaxFPS/MaxBGFPS.
- **Priority default changed to AboveNormal** (was High).
- **Process Manager redesigned** — Priority card moved to top, CPU thread mapping card, grid refresh paused during edits.
- **Video Settings overhaul** — Reordered submenu, preset sizes fixed.

### Fixed
- **DefaultFont crash** — Null reference on systems without default font.
- **Launch positioning** — Don't force window offsets on every launch, respect user INI edits.
- **PiP anchor** — Fixed anchor point for overlay positioning.
- **Dalaya patcher** path handling.
- Direct switch hotkeys (Alt+1-6) disabled by default to avoid conflicts.
- Hotkey conflict warning appearing on every Settings close.
- PiP max windows label layout and custom size capped to 960×720.

### Removed
- **Swap Windows** feature — removed (stacked mode replaces it).
- **CharacterEditDialog** — removed (per-character overrides simplified).

---

## v2.8.0 — Slim Titlebar / WinEQ2 Mode (2026-03-30)

### Added
- **Slim titlebar mode** (WinEQ2 style) — Strips `WS_THICKFRAME` (resize border) while keeping `WS_CAPTION` (thin title bar). Positions window at full `rcMonitor` bounds to overlap taskbar. Replaces both "borderless" and "remove title bars" options with a single unified mode.
- **Auto-apply slim titlebar** — Guard timer re-applies style when EQ fights the window decoration changes.
- **EQClientSettingsForm expanded** — Additional eqclient.ini toggle controls.

### Changed
- **WindowManager rewritten** (280+ lines changed) — Unified slim titlebar logic, monitor bounds calculation, style manipulation.
- **Settings Layout tab** — Slim titlebar checkbox replaces borderless + remove-title-bar checkboxes.
- **LaunchManager simplified** — Removed post-launch window positioning (slim titlebar handles it).

### Removed
- **ROADMAP.md** — Removed from project (tracked in root `Roadmap_master.md`).
- **Borderless fullscreen mode** — Superseded by slim titlebar mode.
- **Remove Title Bars option** — Superseded by slim titlebar mode.

---

## v2.7.0 — Process Manager Consolidation (2026-03-28)

### Added
- **Consolidated Process Manager** — 3 clear cards: Windows Priority, Core Assignment, FPS Limits
- **INI-based Core Assignment** — 6 NumericUpDown slot pickers for CPUAffinity0-5, writes directly to eqclient.ini
- **Ghost FPS label** — shows current eqclient.ini MaxFPS/MaxBGFPS values alongside the editor

### Changed
- **FPS defaults** changed to 80/80 (was 0 = unlimited, which crashes EQ)
- **Priority defaults** changed to High/High (prevents virtual desktop crashes + enables autofollow)
- **Settings "Affinity" tab renamed to "Performance"** — stripped to enable toggle + retry settings
- **Submenu directions** — Video Settings, Settings, Launcher all open upward (AboveRight)
- **CharacterEditDialog simplified** — priority override only (core assignment now global via eqclient.ini)

### Removed
- **ThrottleManager** — process suspension was causing "Suspended" in Task Manager
- **CPU Affinity submenu** from tray menu — Process Manager is the one-stop shop
- **Per-character AffinityOverride** — replaced by global eqclient.ini CPUAffinity0-5 slots

---

## v2.6.0 — Per-Character Overrides (2026-03-20)

### Added
- **Per-character CPU affinity** — assign different core masks to individual characters. Characters with `AffinityOverride` use their custom mask instead of the global active/background masks.
- **Per-character process priority** — set individual characters to Normal, AboveNormal, or High priority. Characters with `PriorityOverride` use their custom priority instead of the global setting.
- **Character Edit dialog** — double-click a character in Settings → Characters tab to edit affinity mask and priority overrides. Checkbox toggles override on/off, hex mask input with validation.
- **Process Manager "Source" column** — shows "Custom" (cyan) for clients using per-character overrides, "Global" for clients using default settings.
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
