# EQSwitch v2.5.0 Upgrade Notes

**From**: Claude audit session (2026-03-19)
**Branch**: `claude/audit-x-codebase-e2XRf`

---

## Fixes Applied This Session

### 1. Borderless Fullscreen Gamma Fix
**Problem**: Enabling borderless fullscreen caused EQ to trigger gamma errors ingame.
**Root cause**: EQ was launching in exclusive DirectX fullscreen mode. The borderless trick only works if EQ starts as a *window* first — EQSwitch strips the chrome and stretches it to cover the screen with a Y+1 offset.
**Fix**: `LaunchManager` now writes `WindowedMode=TRUE` and `Maximized=0` to `eqclient.ini` before every launch when borderless is enabled. Also wired up `EnforceOverrides` (was dead code) so all user-configured eqclient.ini overrides are applied before each launch.
**Files**: `Core/LaunchManager.cs`, `UI/SettingsForm.cs`

### 2. PiP Goes Black When Throttle Is Enabled
**Problem**: PiP overlay thumbnails would go black/frozen during the throttle suspend phase.
**Root cause**: `ThrottleManager` suspends background EQ processes via `NtSuspendProcess`. DWM can't composite thumbnails for suspended processes.
**Fix**: PiP source PIDs are now passed to `ThrottleManager.SetExemptPids()` so they keep running at full speed. Exemptions are cleared when PiP is toggled off.
**Files**: `Core/ThrottleManager.cs`, `UI/PipOverlay.cs`, `UI/TrayManager.cs`

### 3. StringBuilder Allocation in Hot Path
**Problem**: `ProcessManager.GetWindowTitle()` allocated a new `StringBuilder` every 500ms per window. Over 72h with 4 windows = ~2M allocations.
**Fix**: Uses a `ThreadStatic` cached `StringBuilder` instance.
**Files**: `Core/ProcessManager.cs`

### 4. UI Responsiveness — Foreground Hook Debounce
**Problem**: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` fires on every window focus change across the entire desktop. Rapid Alt+Tab triggered expensive affinity re-apply + PiP rebuild + throttle update dozens of times per second.
**Fix**: 50ms debounce timer — only does work once input settles.
**Files**: `UI/TrayManager.cs`

### 5. UI Responsiveness — Config Save Coalescing
**Problem**: `ConfigManager.Save()` did synchronous JSON serialize + backup rotation + file write on the UI thread. Fired on every PiP drag release, affinity toggle, etc.
**Fix**: 250ms coalescing timer batches rapid saves into one write. `FlushSave()` on shutdown ensures nothing is lost.
**Files**: `Config/ConfigManager.cs`, `Program.cs`

---

## Gotchas to Know About

These are non-obvious pitfalls. Read these before making changes.

### Window/Process Handle Staleness
- EQ can recreate its window during gameplay. Between `IsWindow()` and `SetWindowPos()`, the handle can go stale. All Win32 calls on window handles should tolerate silent failure.
- Don't cache window handles across timer ticks. ProcessManager refreshes them every poll cycle.
- `OpenProcess()` handles can also go stale if the process exits between snapshot and use. `SetProcessAffinity`/`SetProcessPriority` returning false is expected, not a bug.

### Throttle State Machine
- Two alternating timers (suspend -> resume -> suspend...). If one is disposed mid-cycle, processes stay frozen.
- **Never set ThrottleManager to null without calling Stop() first.** `Dispose()` calls `Stop()`, but bypassing it leaves EQ processes permanently suspended.
- `Stop()` always calls `ResumeAllSuspended()` as a fail-safe.

### Hotkey Reload Gap
- During config reload, hotkeys are unregistered then re-registered. Brief millisecond gap where they don't work.
- If `RegisterHotKey` fails for one key, the others still register. Partial registration is logged but not shown to user. Check `eqswitch.log`.

### Config Serialization
- JSON uses camelCase (`"activePriority"` not `"ActivePriority"`). Hand-editing with wrong case = silently ignored.
- Null collections in JSON (`"characters": null`) can cause NullReferenceException. Always null-check.
- `ConfigManager.Save()` is coalesced (250ms delay). If app crashes before timer fires, last save is lost. Use `ConfigManager.FlushSave()` for critical saves (first-run, migration).

### Priority String Matching
- `ParsePriorityClass` uses `ToLowerInvariant()`. Unknown values silently default to `NORMAL_PRIORITY_CLASS`. If adding new priority options, update both the parser in `AffinityManager` and the Settings form dropdown.

### ProcessManager Snapshots
- `ProcessManager.Clients` returns a copy-on-write snapshot, but the `EQClient` objects inside are mutable. `CharacterName` can change between polls when a player logs in. Stale name for up to 500ms is by design.

### Borderless Fullscreen
- Requires `WindowedMode=TRUE` in eqclient.ini. LaunchManager now enforces this automatically.
- `SetWindowPos` with `SWP_FRAMECHANGED` on an already-borderless window causes brief flicker. Set style via `SetWindowLongPtr` first, then `SetWindowPos` once.

### PiP + Throttle
- Throttle suspends background processes. PiP needs those processes running to show thumbnails. PiP source PIDs are exempt from throttling. If adding features that interact with background clients, check for throttle conflicts.

### Debounce Everything on the UI Thread
- This is a single-threaded app. Any new WinEvent hook, system callback, or high-frequency handler MUST be debounced.
- `GetProcessesByName()` takes 10-30ms. Don't add work to that path.
- `DwmRegisterThumbnail` is a DWM compositor call — not free. PipOverlay has an early-exit fast path.

---

## Test Checklist Before Release

- [ ] Launch 4+ clients with borderless enabled — no gamma errors
- [ ] Enable PiP + throttle together — thumbnails stay live, not black
- [ ] Rapid Alt+Tab between clients — no UI stutter
- [ ] Open/close Settings repeatedly — no memory growth
- [ ] Leave running 24+ hours — check Task Manager for handle/memory creep
- [ ] Kill eqgame.exe while EQSwitch is running — no crash, graceful cleanup
- [ ] Toggle affinity on/off rapidly — no stuck processes
