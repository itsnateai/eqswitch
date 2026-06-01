# EQSwitch multimonitor taskbar-flicker fix — surgery brief

## ▶ NEW SESSION — START HERE (cold-start resume)

> **HARD CONSTRAINTS (Nate, restated 2026-05-31):**
> - **Surgical + elegant.** Smallest diff that fixes the *mechanism* — no refactors, no new abstractions beyond the one z-order plant + its default-false flag. If the tree moved enough that the plant no longer fits cleanly, re-fit it minimally; don't force it and don't expand scope.
> - **RE-READ ALL THE CODE FRESH FIRST.** Substantial new work landed *after* this brief was written. Treat every code snippet / line ref below as **INTENT (what to do)**, NOT a literal patch — surrounding code, signatures, and line numbers have moved. Verify each symbol against current source before editing.

1. Confirm the eqswitch tree is free now (no concurrent session editing `Core/WindowManager.cs` / `UI/TrayManager.cs`). If still busy, stay read-only.
2. **Re-read the full relevant surface fresh** — not just the anchors: `ArrangeMultiMonitor` (whole method), `ArrangeWindows`, `SwapWindows`, `OnSwitchKey` + `OnGlobalSwitchKey` multimon branches, `eqswitch-hook.cpp` SetWindowPos path, current `<Version>`. Lots changed overnight.
3. Read the **Root-cause research** (linked below) for the *why* — the fix design is locked; you do not need to re-derive the mechanism.
4. Run the **Pre-cut checklist** (bottom of this file): confirm the 4 symbol anchors + the Win32 constants still exist where expected.
5. Implement **Task 1** (3 edits + 1 test) as the minimal diff. Build → `--test-swap-cover` → **live dual-monitor dual-client smoke** (the real gate). Do NOT claim done off the unit test.
6. Only if a residual peek remains after live smoke → Task 2, then Task 3.
7. Ship: version bump + CHANGELOG + conventional commit, no Claude attribution.

> **Context:** dual-session day (2026-05-31). Another session shipped v3.22.90 and was actively editing the two target files, so this was researched + planned read-only and HELD for morning execution. Everything needed is in this file + the research doc — no conversation context required.

> **Status:** HELD — execute against accurate code once the other session releases the eqswitch tree.
> Symbol-anchored on purpose (no line numbers): the two target files are being edited concurrently; expect drift, re-read before cutting.
> **Root-cause research (read first):** `X:/_Projects/_docs/research/deepresearch-eqswitch-taskbar-flicker-2026-05-31.md`
> **Target destination when tree is free:** copy into `eqswitch/docs/specs/`.

## Goal
Kill the one-frame taskbar peek on `\` / `]` multimonitor client swaps. Surgical + elegant: one z-order plant on the swap path, no new windows, no native COM unless the plant proves insufficient.

## Root cause (one line)
Windows' shell rude-window manager re-asserts the taskbar to topmost when, for one frame mid-swap, the **primary monitor has no fullscreen window covering it** (the outgoing client's fullscreen-exit `0x36` fires the recalc before the incoming client is registered as the new top-fullscreen). NOT a DeferWindowPos atomicity problem — that frame was the wrong axis (two prior patches `a9cf455`/`72da7ea` failed).

## The fix — incoming-first HWND_TOP plant (Task 1, ship this alone if smoke passes)
Keep the primary monitor's top-of-Z-order window fullscreen at **every** instant: before the batch moves the outgoing client off primary, plant the **incoming** (slot-0) client at `HWND_TOP` covering the primary bounds. The recalc then always sees a covering top window → no peek. Mirrors the AHK build that "didn't flicker," and is this codebase's own deferred "step 3" from `reference_eqswitch_v3_22_22_backlog.md` Item 1.

### Change set (symbol-anchored — 3 edits + 1 test)
1. **`WindowManager.ArrangeWindows`** — add trailing param `bool coverPrimaryFirst = false`; forward it to `ArrangeMultiMonitor`. (Default-false ⇒ Fix-Windows / ApplyDeferredCosmetics callers unaffected.) **Keep its `(int Iconic, int Other)` return type** and all existing callers' destructuring intact — only the param list grows. `ArrangeMultiMonitor` is `private`; it stays private (see test note — drive it via the public `ArrangeWindows`).
2. **`WindowManager.ArrangeMultiMonitor`** — add same param. In the existing per-client build loop, when `slotIdx == 0`, capture the primary target: `(IntPtr hwnd, int x, int y, int w, int h)? primaryPlant`. Immediately **before** `BeginDeferWindowPos`:
   ```csharp
   if (coverPrimaryFirst && primaryPlant.HasValue)
   {
       var p = primaryPlant.Value;
       // Incoming-first coverage: plant the primary-bound client at HWND_TOP
       // covering primary BEFORE the batch moves the outgoing client off it,
       // so the shell's rude-window recalc never sees an uncovered primary
       // (taskbar-flicker fix — see docs/specs/...-taskbar-flicker.md).
       // HWND_TOP (not HWND_TOPMOST): EQ clients are non-topmost, so top-of-
       // normal-band is enough; HWND_TOPMOST would itself be a documented
       // flicker trigger (DisplayFusion "Disallow TopMost Calls").
       _api.SetWindowPos(p.hwnd, NativeMethods.HWND_TOP, p.x, p.y, p.w, p.h,
                         NativeMethods.SWP_NOACTIVATE);
       FileLogger.Info($"ArrangeMultiMonitor: incoming-first cover — planted slot-0 hwnd {p.hwnd} at HWND_TOP over primary before batch (taskbar-flicker fix)");
   }
   ```
   The subsequent batch re-applies the same primary coords with `SWP_NOZORDER` (preserves the plant's z-order) and moves the outgoing client — harmless redundancy on the primary window.
3. **`TrayManager.OnSwitchKey` + `OnGlobalSwitchKey`** — at the multimon `ArrangeWindows` call, pass `coverPrimaryFirst: true`. (Two sites; both already inside `isMultiMon`.)

### Why no hook conflict
`eqswitch-hook.dll` hooks `SetWindowPos` **inside eqgame.exe** (eqgame's own move attempts). Our plant is a cross-process `SetWindowPos` call executing in **EQSwitch.exe** — not intercepted. No fight.

### Test (regression guard — follows the `--test-*` static-RunAll + FakeWindowsApi pattern)
New `Core/SwapCoverOrderingTests.cs`, wired to a `--test-swap-cover` flag in `Program.cs`. A **recording** `IWindowsApi` fake logs each `SetWindowPos`/`EndDeferWindowPos` with a sequence number. Two `EQClient`s (`new EQClient(100, 0xA, 0)`, `new EQClient(200, 0xB, 1)`), `monitorSlotByPid = {100→1, 200→0}` ⇒ `0xB` is primary-bound. Fake returns 2 monitors, `IsWindow/ IsClientResponsive=true`, `IsIconic/IsHung=false`. Set config `Layout.Mode = "multimonitor"` and drive through the **public** entry `ArrangeWindows(clients, slots, coverPrimaryFirst: true)` — `ArrangeMultiMonitor` is `private`, so call it indirectly (exactly as `WindowManagerClampTests` exercises the public `ComputeSlimTitlebarOuterRect`, not the private arrange methods). **Assert** exactly one recorded `SetWindowPos` with `after==HWND_TOP && cx>0 && (flags & SWP_NOZORDER)==0 && (flags & SWP_NOACTIVATE)!=0`, that its `hwnd == 0xB`, and its seq `<` the first `EndDeferWindowPos` seq. (Frame-change calls are excluded by `cx>0 && !SWP_NOZORDER`.) Add a negative case: `coverPrimaryFirst: false` ⇒ zero such plant calls.

**Build scoping (don't skip — mirrors every other `*Tests.cs`):** guard the `--test-swap-cover` branch in `Program.cs` with `#if DEBUG` (Release blocks `--test-*` except `--test-autologin` and exits 2), and exclude `Core/SwapCoverOrderingTests.cs` from Release via the same csproj `Condition` the other test files use. Otherwise the Release build either fails to compile or silently skips the test.

### Acceptance gate (the real one — unit test can't see a taskbar flash)
LIVE smoke: 2 monitors, 2 in-world clients, slim multimonitor mode. Press `\` and `]` repeatedly; watch the primary taskbar edge — **no peek**. Confirm `eqswitch.log` shows the "incoming-first cover — planted slot-0 hwnd …" line per swap. Per `feedback_verify_live_before_claiming_done_eqswitch_snap` — do NOT claim done off the unit test alone.

### Ship
Bump `EQSwitch.csproj` `<Version>` to the next free version — **READ the current `<Version>` first; never hardcode.** (At brief-time v3.23.0 was already shipped — Quick Login slots — and v3.22.91 was consumed by the Settings-tidy ship, so next free is ≥ v3.23.1. It will have advanced further overnight.) CHANGELOG entry; conventional commit `fix(window): incoming-first z-order plant kills multimon swap taskbar flicker`; **no Claude attribution**. Stakes = source code (not Native) ⇒ normal completion-checkpoint tier.

## Fallbacks — ONLY if Task 1 live smoke still shows residual peek
- **Task 2 — `ITaskbarList2::MarkFullscreenWindow(incoming, TRUE)`.** Cheapest first: call it from **C# on the eqgame HWND** and smoke — if the taskbar yields cross-process, done. If not, implement in-process in `eqswitch-hook.cpp` (`CoCreateInstance(CLSID_TaskbarList)` → `MarkFullscreenWindow(hwnd, TRUE)` after it positions the slim rect). Native ⇒ **high-stakes** completion-checkpoint + swarmaudit.
- **Task 3 — transition curtain.** New `UI/TransitionCurtain.cs`: borderless `TopMost` Form, `ShowInTaskbar=false`, black, `CreateParams.ExStyle |= WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW|WS_EX_LAYERED`, `ShowWithoutActivation=true`. `Show(unionOfAffectedMonitors)` before arrange, `Hide()` after the incoming covers primary. A topmost fullscreen window keeps the monitor rude for its lifetime (RudeWindowFixer) ⇒ zero peek, and masks DX churn. Owned by EQSwitch.exe ⇒ no cross-proc concern. Heaviest; last resort.

## Pre-cut checklist (when tree frees up)
1. Re-read `ArrangeMultiMonitor` build loop + `BeginDeferWindowPos` site, and the two `OnSwitchKey`/`OnGlobalSwitchKey` multimon branches — confirm symbols/signatures haven't changed.
2. Confirm `NativeMethods.HWND_TOP` / `SWP_NOACTIVATE` / `SWP_NOZORDER` still present (they are as of this brief).
3. **Read the current `EQSwitch.csproj` `<Version>`** — it was already v3.23.0 at brief-time and will have advanced overnight. The fix ships at next-free; the "v3.22.91" anywhere in your memory is stale (that ship happened 2026-05-31).
4. Confirm `ArrangeMultiMonitor` is still `private` and `ArrangeWindows` still returns `(int Iconic, int Other)` — the test drives the public `ArrangeWindows`, not the private method.
5. Update `reference_eqswitch_v3_22_22_backlog.md` Item 1 hypothesis to the rude-window mechanism (its sequential-vs-batched A/B is superseded).
