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
**Status:** Research complete, ready to implement

Make EQ fill the entire screen without true exclusive fullscreen, preserving the ability to Alt+Tab and overlay PiP windows.

**Background:**
- EQ (eqgame.exe) runs in windowed mode with `WindowedMode=TRUE` in `eqclient.ini`
- The AHK version of EQSwitch tried to implement this but couldn't get it working
- WinEQ/WinEQ2 (by Lavishsoft) and ISBoxer both use the same core technique

**Research findings (3 proven approaches, ranked):**

#### Approach 1: Style Removal + Y-Offset Trick (Recommended — WinEQ method)
The WinEQ2/ISBoxer technique. When a borderless window is at Y=0, Windows renders
the taskbar ON TOP of it. When offset to Y=1, Windows renders the taskbar BEHIND it.
This is an undocumented shell heuristic, not a special API.

```
1. Remove styles: clear WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX
2. Clear extended styles: WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE
3. Get FULL monitor bounds (rcMonitor, NOT rcWork — includes taskbar area)
4. Position at (monitor.Left, monitor.Top + 1) with full monitor.Width × monitor.Height
   The +1 Y offset triggers "taskbar passive" — it stays behind the game
5. SetWindowPos with SWP_FRAMECHANGED | SWP_SHOWWINDOW
```

**Why +1 not -1:** Windows' shell uses window position heuristics to detect fullscreen.
A window exactly at (0,0) with exact screen dimensions triggers "standard fullscreen" behavior
where the taskbar fights for Z-order. Offsetting by +1 pixel avoids this detection, causing
the taskbar to go passive (behind the game, but accessible by moving mouse to edge).

#### Approach 2: EQ-Native eqclient.ini Offsets (No code needed)
EQ itself supports window offset parameters:
```ini
WindowedModeXOffset=-8
WindowedModeYOffset=-31
WindowedWidth=1924
WindowedHeight=1058
```
This pushes the title bar off-screen and oversizes the window. Achieves pseudo-borderless
without any external tool. Could be set via the existing VideoSettingsForm. Alternative
values: `XOffset=-3`, `YOffset=-26`, `Width=1924`, `Height=1058`.

#### Approach 3: Two-Step SetWindowPos (DisplayFusion Pattern)
```
1. SetWindowPos to (width-1, height-1) — slightly smaller
2. Immediately MoveWindow to (width+1, height+1) — slightly larger
```
The two-step dance tricks the window manager into accepting fullscreen dimensions
without triggering the standard fullscreen detection heuristic.

**Implementation plan (Approach 1):**

*Already available in NativeMethods.cs:*
- `GetWindowLongPtr` / `SetWindowLongPtr`, `SetWindowPos`
- `MONITORINFO` with both `rcMonitor` and `rcWork`
- `WS_CAPTION`, `WS_THICKFRAME`, `GWL_STYLE`, `GWL_EXSTYLE`

*Need to add to NativeMethods.cs:*
```csharp
public const long WS_SYSMENU     = 0x00080000L;
public const long WS_MINIMIZEBOX = 0x00020000L;
public const long WS_MAXIMIZEBOX = 0x00010000L;
public const int WS_EX_DLGMODALFRAME = 0x00000001;
public const int WS_EX_CLIENTEDGE    = 0x00000200;
public const int WS_EX_STATICEDGE    = 0x00020000;
public static readonly IntPtr HWND_TOPMOST   = new IntPtr(-1);
public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
public const uint SWP_NOOWNERZORDER = 0x0200;
```

*Need to add to IWindowsApi / WindowsApi:*
- `GetAllMonitorBounds()` — returns `rcMonitor` (full screen including taskbar)
  (current `GetAllMonitorWorkAreas()` returns `rcWork` which excludes taskbar)

*Config additions:*
- `WindowLayout.BorderlessFullscreen` (bool, default false)
- When enabled, arrange uses `rcMonitor` + Y+1 offset instead of `rcWork`

*WindowManager changes:*
- New `ApplyBorderlessFullscreen(IntPtr hwnd, WinRect monitorBounds)` method
- `ArrangeSingleScreen` / `ArrangeMultiMonitor` check `BorderlessFullscreen` flag
- New `RestoreBorderlessFullscreen(IntPtr hwnd)` to undo on mode change

*SettingsForm changes:*
- Checkbox on Layout tab: "Borderless Fullscreen"
- Hint: "Fills screen without exclusive fullscreen — preserves Alt+Tab and PiP"

**Risks & mitigations:**
- EQ may fight the window positioning → apply after a short delay (similar to affinity retry)
- Windows 10 vs 11 taskbar differences → make Y offset configurable (default +1)
- Per-client toggle may be needed → defer to v2.4.0 if requested

**Sources:**
- WinEQ2 FAQ (Lavishsoft wiki) — Y-offset taskbar trick
- ISBoxer forum — same Inner Space engine as WinEQ2
- RedGuides — EQFullScreenWindow borderless v1.0, eqclient.ini offset method
- DisplayFusion scripted functions — two-step SetWindowPos pattern
- Microsoft Learn — SetWindowPos, ABM_SETSTATE, MONITORINFO

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
