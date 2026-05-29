# Window Mode Toggles — Fullscreen vs Windowed (slim titlebar)

> **Status:** Approved design (Nate, 2026-05-29). Implementation split into Phase 1 (pin Fullscreen) + Phase 2 (build Windowed).
> **Baseline version:** v3.22.79. Phase 1 target: v3.22.80. Phase 2 target: v3.22.81.
> **Authoritative version source:** `EQSwitch.csproj` `<Version>` — do not hardcode elsewhere.

## 1. Problem

The Video tab's **Window Style** card has four checkboxes whose labels describe
*implementation plumbing*, not *what the user sees*. The result is a
self-contradicting UI: with the current shipped defaults, **both** "Windowed
Mode" **and** "Fullscreen Window" are checked, yet the game renders as a
borderless, flush-on-all-sides, no-titlebar window. A user reading the card
cannot tell what mode they are in or what each toggle does.

Ground truth of the current controls (`UI/SettingsForm.cs:3368-3384`):

| UI label | Backing flag | What it actually does |
|---|---|---|
| **Windowed Mode** | `EQClientIni.ForceWindowedMode` → `WindowedMode=TRUE` in eqclient.ini (`EQClientSettingsForm.cs:805-807`) | Forces EQ to run as a window instead of exclusive-fullscreen DirectX. **Mandatory prerequisite** for all window management. NOT a visual mode. |
| **Fullscreen Window (WinEQ2 mode)** | `Layout.SlimTitlebar` (`SettingsForm.cs:1588`) | The current borderless look. As of v3.22.76 strips `WS_THICKFRAME｜WS_CAPTION｜WS_SYSMENU` and sets `WS_POPUP` (`WindowManager.cs:548-551`). |
| **Maximize on Launch** | `EQClientIni.MaximizeWindow` → `Maximized=1` | Standard maximized window with a normal title bar. Force-off + disabled when slim is on (`SettingsForm.cs:3437`). |
| **Dark Titlebar** | `Layout.DarkTitlebar` | Cosmetic DWM immersive dark caption. Unchanged by this work. |

**The fix is to rename the controls to match reality and expose exactly two
mutually-exclusive visual modes.**

## 2. Goals / Non-goals

**Goals**
- Two user-facing, mutually-exclusive window modes with honest names:
  - **Fullscreen mode** — today's borderless `WS_POPUP` look (pinned, unchanged).
  - **Windowed mode** — a slim-titlebar window: thin caption peeking at the top
    (draggable), flush sides, **no font distortion**.
- Demote the always-required `WindowedMode=TRUE` plumbing out of the main card.
- Park (not delete) `Maximize on Launch` while its usefulness is evaluated.
- All five launch paths honor the selected mode with no per-path code.

**Non-goals**
- No change to autologin, injection, hotkeys, affinity, or PiP.
- No change to the Fullscreen-mode rendering pipeline (it already produces crisp
  native-resolution output — that path is frozen).
- No new multi-monitor *arrangement* features; Windowed-mode multi-monitor
  behavior is a defined risk addressed in §7, not a new layout feature.

## 3. The font-issue root cause (load-bearing for Windowed mode)

Per the v3.22.76 changelog, the glyph-distortion seam in the old caption-based
slim titlebar was **not** caused by the caption being visible. It was caused by
writing a **non-native resolution** to `eqclient.ini`:

- Old slim style `WS_CAPTION` reports an **8px L / 31px T / 8px R** DWM frame
  bleed on Win11 (`AdjustWindowRectEx`). `WS_POPUP` reports **0/0/0**.
- The old slim INI math wrote the *visible client area minus caption* as the
  render resolution — e.g. `1912×1062` (`EQClientSettingsForm.cs:836-851`,
  `SlimTitlebarVisibleClientSize`).
- EQ rendered its 3D + bitmap UI at that non-standard resolution; the DX swap
  chain then scaled it into the window → vertical glyph seam.
- v3.22.76 switched to `WS_POPUP` so client = monitor = **native** res →
  crisp. That removed the seam **by removing the caption**.

**Conclusion:** "slim titlebar with no font issues" is achievable by keeping the
caption but writing **native** resolution and letting the window overflow the
screen edges (the "WinEQ2 method" already referenced at
`EQClientSettingsForm.cs:813-815`): caption peeks above the top edge, the bottom
few px hang below the screen, and the client renders at native res. The old
code's mistake was shrinking the resolution to the visible area instead of
overflowing.

## 4. The two-mode model (locked)

Both modes always set `WindowedMode=TRUE` and `Maximized=0` under the hood.

| Mode | Window style | INI resolution | Look | Source |
|---|---|---|---|---|
| **Fullscreen mode** | strip `WS_THICKFRAME｜WS_CAPTION｜WS_SYSMENU`, set `WS_POPUP` | `WindowedWidth/Height = monW/monH` (native) | borderless, flush all sides, no titlebar | **exists — frozen** (`WindowManager.cs:548-578`) |
| **Windowed mode** | strip `WS_THICKFRAME`, **keep** `WS_CAPTION` | `WindowedWidth/Height = monW/monH` (native, overflow method) | slim titlebar peeking at top (draggable), flush sides, bottom off-screen | **re-introduce** |

## 5. Config & control decisions (locked with Nate 2026-05-29)

- **Control type:** two checkboxes — `Windowed Mode` and `Fullscreen mode` —
  mutually exclusive (checking one unchecks the other; exactly one is always on).
- **Old `Fullscreen Window (WinEQ2 mode)` checkbox:** **replaced** by the new
  `Fullscreen mode` control (same `WS_POPUP` behavior, honest name). No duplicate.
- **Parked into ⚙ Advanced (Wrapper) dialog:** `Maximize on Launch`
  (`MaximizeWindow`) and the `WindowedMode=TRUE` plumbing toggle
  (`ForceWindowedMode`, demoted to expert override, defaults on).
- **New config field:** `Layout.WindowMode` enum `{ Fullscreen, Windowed }`,
  default `Fullscreen`. `Layout.SlimTitlebar` is derived from / migrated to this
  (see §6 migration). `Dark Titlebar` and the Advanced low-level knobs
  (`TitlebarOffset`, `BottomOffset`, `UseHook`, `HorizontalNudgePx`) are unchanged.

Final card layout:

```
🪟 Window Style                              [⚙ Advanced...]
  ☐ Windowed Mode      slim titlebar, draggable      (disabled in Phase 1)
  ☑ Fullscreen mode    borderless, flush all sides
  ☑ Dark Titlebar      DWM dark caption (Win10 1809+/11)
```

## 6. Phase 1 — Pin "Fullscreen mode" (UI reorg + config; zero pixel change)

Phase 1 is provably safe: every change resolves to the same
`SlimTitlebar=true → WS_POPUP` path that ships today. If the rendered look
changes at all, Phase 1 has a bug.

### 6.1 Config (`Config/AppConfig.cs`, `Config/ConfigManager.cs`)
- Add `WindowLayout.WindowMode` enum `{ Fullscreen, Windowed }`, default
  `Fullscreen`.
- **Migration** (`ConfigVersionMigrator` / `Validate`): a config with no
  `WindowMode` key maps from the existing `SlimTitlebar` bool —
  `SlimTitlebar=true → Fullscreen`, `SlimTitlebar=false → ` (legacy non-slim;
  see note). Persist the canonical enum on first load.
- `SlimTitlebar` is retained as the internal "EQSwitch manages the window style"
  signal and kept in sync with `WindowMode` (both Fullscreen and Windowed are
  slim-managed styles). The styling branch selects `WS_POPUP` vs `WS_CAPTION`
  by `WindowMode`.
- Add the six-flag `ConfigWrite:` audit coverage for `WindowMode` to match the
  existing load-bearing-flag audit pattern (`ConfigManager.LogConfigWriteAudit`).

> Legacy `SlimTitlebar=false` ("normal frame, work-area" path,
> `WindowManager.cs:580-607`) is not one of the two new user-facing modes. It is
> kept reachable only via the Advanced plumbing override for now; the main card
> always selects Fullscreen or Windowed.

### 6.2 UI (`UI/SettingsForm.cs` Window Style card, ~`3349-3454`)
- Rename `_chkSlimTitlebar` label `Fullscreen Window (WinEQ2 mode)` →
  `Fullscreen mode`. Keep the field name to minimize churn; update the hint.
- Add `_chkWindowedMode` (new) labeled `Windowed Mode`, **disabled** with hint
  `(next version)`, positioned above `Fullscreen mode`.
- Mutual-exclusivity handler: checking `Fullscreen mode` unchecks
  `Windowed Mode` and vice-versa; never both off (re-check the last-on one).
- Remove the old `Maximize on Launch` checkbox from the card; move it into the
  Advanced/Wrapper dialog (`ShowWrapperDialog`, ~`3558`).
- Move the `ForceWindowedMode` user control into the Advanced dialog as an
  expert override (default on); the main card no longer exposes it.
- Update `_lblStyleDisabledHint` logic for the new control set.
- Load (`SettingsForm.cs:1588-1632`), save
  (`BuildAppConfig`/`ApplySettings` ~`1864`, `2106`), and Reset-Defaults
  (~`3907-3930`) updated for `WindowMode`. Card height / `y` advance re-checked
  (the card was 152px for 4 rows; new layout is 3 rows + the Advanced button —
  recompute to avoid the `FixedSingle` border clip class of bug seen in
  v3.22.78).

### 6.3 INI writer (`UI/EQClientSettingsForm.cs::EnforceOverrides`, ~`805-862`)
- Drive the slim cascade off `WindowMode == Fullscreen` exactly as it drives off
  `SlimTitlebar` today. No math change in Phase 1 — Fullscreen keeps the native
  `WS_POPUP` write.

### 6.4 Verification (Phase 1)
- Build + smoke: launch via each of the 5 paths (Launch Client, Launch Team,
  Accounts, Characters, Team submenu) and confirm the window looks **identical**
  to v3.22.79 (borderless, flush, no titlebar), screenshot-verified.
- Confirm `eqswitch-config.json` migrates a pre-existing config without changing
  the rendered result; `ConfigWrite:` audit shows `WindowMode` transitions.
- Confirm `Windowed Mode` checkbox is visible but disabled.

## 7. Phase 2 — Build "Windowed mode" (native work; the "no font issues" part)

### 7.1 Window styling (`Core/WindowManager.cs`)
- Add a `WS_CAPTION` slim branch alongside the `WS_POPUP` branch in both
  `ApplySlimTitlebar` and `ArrangeMultiMonitor` (`~548-578`): strip
  `WS_THICKFRAME`, **keep** `WS_CAPTION` (decide `WS_SYSMENU` retention — keep
  for the window icon/close affordance).
- Re-enable `ComputeSlimTitlebarOuterRect` / `TitlebarOffset` geometry for the
  caption-peek positioning (no-op since v3.22.76). Restore
  `SlimTitlebarCaptionVisible` to return the real caption sliver for the
  `WS_CAPTION` case while staying 0 for `WS_POPUP`.
- `ApplySlimTitlebarToAll` guard timer: keep the v3.22.76 center-on-configured-
  monitor early-exit so user-moved windows stick.

### 7.2 INI writer (`UI/EQClientSettingsForm.cs::EnforceOverrides`)
- For `WindowMode == Windowed`: write **native** `WindowedWidth/Height =
  monW/monH` and position via overflow (caption above top, bottom below screen),
  **not** the old `gameW/gameH = visible-minus-caption` shrink. This is the
  font-seam fix. Single-monitor case overflows freely.

### 7.3 Native hook (`Native/eqswitch-hook.cpp`) — **stays on Opus**
- The hook one-way strips `WS_THICKFRAME` to fight EQ's restyle. Confirm it does
  **not** strip `WS_CAPTION` in Windowed mode (per-PID hook config carries the
  mode). Adjust `HookConfigWriter` struct + the C++ side together if a mode flag
  is needed (struct layout must match exactly — see CLAUDE.md injection safety).

### 7.4 Multi-monitor risk (defined, not deferred)
- On a monitor edge that abuts another monitor, the window cannot overflow
  (it would bleed onto the neighbor's client), so adjacency-clipping forces a
  non-native res on that edge → the seam can return. Decision for the plan:
  Windowed mode either (a) restricts overflow to non-adjacent edges and accepts
  the caption-only offset on adjacent edges, or (b) is single-monitor-primary
  with documented multi-monitor caveats. Resolve during Phase 2 planning with a
  screenshot test on the dual-monitor rig.

### 7.5 Enable + verify
- Enable the `Windowed Mode` checkbox.
- Screenshot-verify on the live client: caption peeks/draggable, sides flush,
  **fonts crisp** (compare glyph edges against Fullscreen-mode screenshot).
- Re-run all 5 launch paths in Windowed mode.

## 8. Why all 5 launch paths come for free

All five entry points converge on a single styling point and a single INI
writer (verified 2026-05-29):

- **Styling:** `ProcessManager.ClientDiscovered` → `WindowManager.ApplySlimTitlebar()`
  / `ArrangeWindows()` (`UI/TrayManager.cs:567`, `~625-632`).
- **INI:** `EQClientSettingsForm.EnforceOverrides()` (`~747`), called before
  every launch (`LaunchManager` ~`182`, `AutoLoginManager` ~`368`).

Branching those two on `WindowMode` makes all five paths
(Launch Client → `OnLaunchOne`; Launch Team / Team submenu → `FireTeam`;
Accounts → `FireAccountLogin` → `LoginToCharselect`; Characters →
`FireCharacterLogin` → `LoginAndEnterWorld`) inherit the mode with no per-path
code.

## 9. Risks

- **Font seam regression** (Phase 2) — the entire reason WS_CAPTION was removed.
  Mitigation: native-res overflow write (§7.2) + screenshot glyph comparison gate.
- **Native hook struct mismatch** (Phase 2) — C#/C++ `HookConfig` layout must
  stay byte-identical. Mitigation: change both sides in one commit; smoke-test.
- **Multi-monitor adjacency** (§7.4) — non-native res on abutting edges.
- **Mutual-exclusivity edge cases** — Reset Defaults, config migration, and the
  Advanced plumbing override interacting. Mitigation: explicit load/save tests.

## 10. Out of scope / future
- Consolidating the legacy `SlimTitlebar=false` normal-frame path or removing it.
- Per-client (vs global) mode selection.
- Re-evaluating whether `Maximize on Launch` is still needed (parked decision).
