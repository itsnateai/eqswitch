# EQSwitch — Roadmap

> Post-v2.2.0 feature roadmap. Items marked `[x]` are complete; `[~]` are in progress.

---

## v2.3.0 — Performance & Fullscreen

### P2-04: Background FPS Throttling `[x]`
**Status:** Implemented

Duty-cycle background EQ processes via `NtSuspendProcess`/`NtResumeProcess` to reduce GPU/CPU usage when multiboxing. Active (foreground) client is never throttled.

**Files:**
- `Core/ThrottleManager.cs` — suspend/resume duty cycle engine
- `Core/NativeMethods.cs` — `NtSuspendProcess`, `NtResumeProcess`, `PROCESS_SUSPEND_RESUME`
- `Config/AppConfig.cs` — `ThrottleConfig` (Enabled, ThrottlePercent 0-90, CycleIntervalMs)
- `UI/SettingsForm.cs` — controls on Affinity tab
- `UI/TrayManager.cs` — wired into affinity timer tick + config reload

**How it works:**
- Two alternating timers: suspend phase and resume phase
- `ThrottlePercent` controls duty cycle (50% = suspend half the time = ~half FPS)
- Active client is immediately resumed on foreground switch
- All processes resumed on shutdown/disable (fail-safe)
- Default: disabled, 50% throttle, 100ms cycle

---

### P2-03: Borderless Fullscreen Mode `[~]`
**Status:** Research / Planning

Make EQ fill the entire screen without true exclusive fullscreen, preserving the ability to Alt+Tab and overlay PiP windows.

**Background:**
- EQ (eqgame.exe) runs in windowed mode with `WindowedMode=TRUE` in `eqclient.ini`
- The AHK version of EQSwitch tried to implement this but couldn't get it working
- WinEQ/WinEQ2 (by Lavishsoft, often misattributed to "Lavasoft") achieved this by:
  1. Removing `WS_CAPTION` and `WS_THICKFRAME` (title bar + resize border)
  2. Resizing the window to cover the full monitor (not just work area)
  3. Offsetting the window Y position by -1 pixel to trick Windows into hiding the taskbar
  4. Using `HWND_TOPMOST` briefly then removing it to force Z-order above taskbar

**Proposed implementation:**
```
1. Remove title bar: GetWindowLongPtr → clear WS_CAPTION | WS_THICKFRAME → SetWindowLongPtr
2. Get full monitor bounds (rcMonitor, NOT rcWork — includes taskbar area)
3. Position window at (monitor.Left, monitor.Top - 1) with size (monitor.Width, monitor.Height + 1)
   The -1 Y offset is the WinEQ trick that makes the taskbar auto-hide
4. SetWindowPos with SWP_FRAMECHANGED to apply style changes
5. Optional: brief HWND_TOPMOST → HWND_NOTOPMOST cycle to establish Z-order
```

**Win32 APIs needed (already available):**
- `GetWindowLongPtr` / `SetWindowLongPtr` — style manipulation (have)
- `SetWindowPos` — positioning (have)
- `GetAllMonitorWorkAreas` → needs `rcMonitor` variant (currently returns `rcWork`)

**New APIs needed:**
- `IWindowsApi.GetAllMonitorBounds()` — returns `rcMonitor` (full screen including taskbar)

**Config additions:**
- `WindowLayout.BorderlessFullscreen` (bool, default false)
- When enabled + RemoveTitleBars, arrange uses full monitor bounds with Y-1 offset

**Risks:**
- EQ may fight the window positioning (it has its own window management code)
- Different behavior across Windows 10/11 taskbar modes
- May need per-client toggle (main client fullscreen, alts in grid)

**Research needed:**
- Test the Y-1 offset trick on Windows 11 with new taskbar
- Check if ISBoxer uses a different approach
- Verify DWM composition interaction with the offset trick

---

### P2-05: Interactive PiP (Click to Focus) `[ ]`
**Status:** Deferred

Currently PiP overlays are permanently click-through (`WS_EX_TRANSPARENT`). Goal: allow clicking a PiP window to switch focus to that EQ client.

**Approach:**
- Replace `WS_EX_TRANSPARENT` with `WM_NCHITTEST` override
- Default state: click-through (return `HTTRANSPARENT`)
- When Ctrl held or specific modifier: return `HTCLIENT` and handle click to switch
- Use the existing `GetAsyncKeyState(VK_CONTROL)` pattern from PiP drag

**Complexity:** Medium — requires careful WndProc override and state management

---

## Removed Items

The following items from the original v2.2.0 deferred list have been removed:

- ~~P2-06: Saveable layout presets~~ — Removed from roadmap
- ~~P4-02: Focus-follow-mouse mode~~ — Removed from roadmap
- ~~P4-03: PiP zoom-on-hover~~ — Removed from roadmap
