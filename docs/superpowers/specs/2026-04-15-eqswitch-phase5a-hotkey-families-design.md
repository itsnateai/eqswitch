# EQSwitch Phase 5a — Hotkey Family Tables + Hotkeys-Tab UI Redesign

**Status:** Approved design, ready for implementation-plan drafting
**Created:** 2026-04-15
**Parent plan:** [`PLAN_account_character_split.md`](../../../PLAN_account_character_split.md)
**Prior phase:** Phase 4 shipped — HEAD `01d05ed`, 32 commits on main. Phase 4 smoke test passed.
**Handoff:** [`PLAN_account_character_split_HANDOFF_phase4.md`](../../../PLAN_account_character_split_HANDOFF_phase4.md)
**Prior spec:** [`2026-04-15-eqswitch-phase4-settings-dual-section-design.md`](2026-04-15-eqswitch-phase4-settings-dual-section-design.md)

## Purpose

Phase 5 from the master plan splits into two sub-phases. **Phase 5a** (this spec) ships the user-facing hotkey-family UI — the Hotkeys tab gains direct-binding dialogs for Accounts and Characters, the Alt+M lock gate comes off, and runtime dispatch starts consuming `HotkeyConfig.AccountHotkeys` / `CharacterHotkeys` (already populated by the Phase 1 migration but sitting unused). **Phase 5b** (separate spec) is the mechanical refactor — `WindowManager` / `AffinityManager` v4-list migration and `CharacterSelector.Decide()` extraction.

This design keeps the shipped DarkTheme aesthetic verbatim — no chrome refresh. Phase 4's dialog pattern (`AccountEditDialog` / `CharacterEditDialog` / `CascadeDeleteDialog`) sets the template; the two new hotkey dialogs match it structurally.

## Goals

- User binds a hotkey directly to any Account or Character via `[ Configure… ]` buttons on the Hotkeys tab.
- Renaming or deleting an Account/Character surfaces a red stale-binding row inside the affected dialog with a one-click `[Rebind → ▾]` dropdown to retarget without losing the combo.
- Alt+M fires the multi-monitor toggle whenever bound — no first-time-use gate.
- Dispatch for `AccountHotkeys[]` routes to `FireAccountLogin` (charselect-only) and `CharacterHotkeys[]` routes to `FireCharacterLogin` (enter-world). Phase 1's migration already produced the binding entries; Phase 5a starts consuming them.
- Main Hotkeys tab stays fixed-height and glanceable — no vertical scroll inside it. Dialogs size to content and scroll internally only at degenerate entity counts (12+).
- Legacy `QuickLoginN` + `HotkeyConfig.AutoLoginN` paths keep working during v3.10.x (deprecation) — Phase 6 deletes them.

## Non-goals (deferred)

- **Phase 5b:** `WindowManager.cs:437-439` v4 migration, `AffinityManager.cs:134` swap to `CharacterAliases`, `Core/CharacterSelector.cs` extraction with 4 test cases.
- **Phase 6 / v3.11.0 cleanup:** delete `QuickLogin1-4`, `HotkeyConfig.AutoLogin1-4`, `LegacyAccounts`, `LoginAccountSplitter`, `Models/LoginAccount.cs`, `[Obsolete] AutoLoginManager.LoginAccount` wrapper, v4→v5 JSON rename migration.
- Dialog aesthetic refresh — shipped DarkTheme locked.
- `Team{N}Account{M}` reworking — Phase 4 already refreshed `AutoLoginTeamsDialog` with the v4 signature + OK/WARN/FAIL pills; Phase 5a leaves it alone.

## Phase 5a design

### 1. Alt+M lock gate removal

**File:** `UI/TrayManager.cs:584-617` (`OnToggleMultiMonitor`).

Today:
```csharp
private void OnToggleMultiMonitor()
{
    // Hotkey locked until user has tried multimonitor at least once via Settings
    if (!_config.Hotkeys.MultiMonitorEnabled)
    {
        FileLogger.Info("ToggleMultiMonitor: not yet unlocked — enable in Settings first");
        ShowBalloon("Enable Multi-Monitor mode in Settings first");
        return;
    }
    // ... toggle body
}
```

Drop the guard. Alt+M fires the toggle unconditionally when bound.

```csharp
private void OnToggleMultiMonitor()
{
    // Phase 5a: the first-time-use gate has been removed. Any bound combo fires the
    // toggle directly — the hotkey-conflict modal (P3.5-D) already blocks duplicate
    // bindings at config time, and every other hotkey operates this way.
    long now = Environment.TickCount64;
    if (now - _lastMultiMonToggle < MultiMonToggleDebounceMs) return;
    _lastMultiMonToggle = now;
    // ... toggle body unchanged
}
```

**`HotkeyConfig.MultiMonitorEnabled` field stays** on the model as a no-op (`[Obsolete]`-eligible but keeping the field during v3.10.x avoids a migration bump). Phase 6 deletes it. `SettingsForm` stops writing it — the OR expression at SettingsForm.cs:~1213 (`_chkVideoMultiMon.Checked || _config.Hotkeys.MultiMonitorEnabled`) simplifies to just `_chkVideoMultiMon.Checked`. Existing configs with `MultiMonitorEnabled: true` are harmless; configs with `false` now work for Alt+M.

### 2. Hotkeys tab layout

**File:** `UI/SettingsForm.cs` — `BuildHotkeysTab` (roughly lines 551-674 area).

Retain the existing Actions Launcher card (Phase 3.5 added PiP Toggle; no changes there). Replace the "Quick Individual Login Accounts" + "Auto-Login Hotkeys" cards with a new "Direct Bindings" card that exposes the two Configure buttons + counts + stale indicator. Teams card stays.

```
┌─ Actions Launcher (gold, 140px) ────────────┐
│  Fix Windows  [Alt+G]   Launch One  [Alt+F1] │
│  Multi-Mon    [Alt+M]   Launch All  [     ] │
│  PiP Toggle   [Alt+O]                        │
│  Hint: Press combo. Backspace/Del/Esc clear. │
└──────────────────────────────────────────────┘
┌─ Team Hotkeys (gold, 98px) ─────────────────┐
│  Team 1 [Alt+1]    Team 2 [Alt+2]            │
│  Team 3 [     ]    Team 4 [     ]            │
└──────────────────────────────────────────────┘
┌─ Direct Bindings (green, 130px) ────────────┐
│  Accounts   [ 2/3 bound ]    [ Configure… ] │
│  Characters [ 3/4 bound ]    [ Configure… ] │
│  ⚠ 1 stale binding — open Character Hotkeys │  (only when count > 0)
└──────────────────────────────────────────────┘
```

**Counts:** `{bound}/{total}` where bound is entries in `AccountHotkeys` / `CharacterHotkeys` with non-empty combo + resolving to an existing entity; total is `_config.Accounts.Count` / `_config.Characters.Count`.

**Configure buttons** open the respective new dialog modal.

**Stale summary row:** only rendered when `CountStale(_config) > 0`. Copy: `⚠ {N} stale binding{s} — open {Account|Character} Hotkeys to review`. If both families have stale entries: two rows. Click surfaces the respective dialog (same as the Configure button — no filtering needed; the dialog's stale rows auto-sort to top).

Legacy Quick Login slot combos + AutoLogin hotkey boxes stay registered in `RegisterHotkeys` during v3.10.x for backward compat — just not exposed in the Settings UI anymore. Users with bindings from v3 keep them; new bindings flow through the family-table dialogs.

**Deprecation banner** (one-time, dismissible): above the Direct Bindings card, if the legacy `QuickLogin1-4` + `HotkeyConfig.AutoLogin1-4` fields are populated and the user hasn't dismissed the banner, show:

```
ℹ Quick Login slots 1-4 are now under "Direct Bindings" — click Configure to see and rebind.
   Legacy hotkeys still work until v3.11.0.  [ Dismiss ]
```

Dismiss persists to `_config.HotkeysLegacyBannerDismissed` — a new top-level `bool` on `AppConfig` (default `false`). Follows the existing top-level-bool pattern (e.g., `IsFirstRun`, `RunAtStartup`) — no new nested type or migration bump.

### 3. `AccountHotkeysDialog` (new file `UI/AccountHotkeysDialog.cs`)

Modal `Form`. Matches Phase 4 dialog pattern. Target ~220 lines.

**Layout:**

```
╔══ Account Hotkeys ═══════════════════════════════╗
║                                                   ║
║  ╔═ 🔑 Direct Account Hotkeys ═════════════════╗ ║
║  ║  Natedogg         [ Alt+1 ]                  ║ ║
║  ║  Flotte           [       ]                  ║ ║
║  ║  Acpots           [ Alt+3 ]                  ║ ║
║  ║  ⚠ backup (deleted)  [Alt+4]  [Rebind → ▾]  ║ ║  ← stale row, red
║  ║                                               ║ ║
║  ║  Hint: Press combo. Backspace/Del/Esc clear. ║ ║
║  ╚═══════════════════════════════════════════════╝ ║
║                                                   ║
║                           [ Cancel ] [  Save   ] ║
╚═══════════════════════════════════════════════════╝
```

**Row generation:**
1. For every `Account` in `_config.Accounts`: render a live row with the Account.Name label and the current hotkey (from `HotkeyConfig.AccountHotkeys[].TargetName == Account.Name`, or empty if none).
2. For every `HotkeyBinding` in `_config.AccountHotkeys` whose `TargetName` does NOT resolve to any Account: render a stale row (sorted to top, red foreground, red warning icon, `Rebind → ▾` dropdown listing live Accounts + `(none — clear binding)`).

**Data model:**

Internal buffer `_pendingBindings: List<(string targetName, string combo, bool isStale)>`. Save flattens this into the new `HotkeyConfig.AccountHotkeys` list. Orphan-to-orphan is not a legitimate state — stale rows either get rebound (dropdown picks a live target) or cleared (`(none)` dropdown value drops the binding).

**Conflict detection:** same across-all-hotkey scan as Phase 3.5-D, but now run inside the dialog's own Save path rather than `SettingsForm.ApplySettings`. Collect every binding in:
- This dialog's pending Account bindings
- Current live Character bindings (not being edited here)
- Actions + Teams hotkeys from `_config.Hotkeys`
- All other dialogs' saved state

If any combo appears twice, block Save with the existing hotkey-conflict modal shape (listing each conflicting combo + its action labels).

**Save contract:** `DialogResult.OK` + public `List<HotkeyBinding> Result` property. Caller (`SettingsForm.ApplySettings` — or a direct commit path from the dialog's Save button) replaces `_config.Hotkeys.AccountHotkeys` atomically.

**Empty state:** if `_config.Accounts.Count == 0` AND no stale entries: dialog opens showing `No accounts yet — add one via Settings → Accounts first.` with a close-only button. Matches Phase 4 `CharacterEditDialog`'s empty-accounts fallback.

### 4. `CharacterHotkeysDialog` (new file `UI/CharacterHotkeysDialog.cs`)

Mirror of `AccountHotkeysDialog` against `_config.Characters` + `_config.Hotkeys.CharacterHotkeys`. Same layout, same stale-row treatment, same conflict detection.

**One semantic difference:** the stale row's `Rebind → ▾` dropdown lists **Characters only** (a Character hotkey fires `FireCharacterLogin`, which requires a resolved FK to an Account — rebinding to an Account would change the enter-world intent and belongs in `AccountHotkeysDialog`).

Orphan Characters (Unlink outcome, empty `AccountUsername`) render in the dropdown with a trailing `(no account)` tag — user can still bind a hotkey, it just won't successfully launch until the Character is linked. This matches the Phase 4 design decision to treat orphans as legitimate but flagged.

### 5. Stale-binding detection + Rebind dropdown

Helper added to `AppConfig.cs` (or a new `Config/HotkeyBindingUtil.cs` if AppConfig gets too crowded):

```csharp
public static class HotkeyBindingUtil
{
    public static int CountStaleAccountBindings(AppConfig cfg)
    {
        return cfg.Hotkeys.AccountHotkeys.Count(b =>
            !string.IsNullOrEmpty(b.Combo) &&
            !string.IsNullOrEmpty(b.TargetName) &&
            !cfg.Accounts.Any(a => a.Name.Equals(b.TargetName, StringComparison.Ordinal)));
    }

    public static int CountStaleCharacterBindings(AppConfig cfg)
    {
        return cfg.Hotkeys.CharacterHotkeys.Count(b =>
            !string.IsNullOrEmpty(b.Combo) &&
            !string.IsNullOrEmpty(b.TargetName) &&
            !cfg.Characters.Any(c => c.Name.Equals(b.TargetName, StringComparison.Ordinal)));
    }
}
```

Empty-`TargetName` entries are migration padding (Phase 1 `EnsureSize` contract — positional slot placeholders for v3 slot ordering). These are NOT stale — they're "slot unused." Only entries with a non-empty `TargetName` that fails to resolve count as stale.

The `Rebind → ▾` dropdown bound to a stale row uses the same canonical resolution: the dropdown's `DisplayMember = Account.Name` (or `Character.Name`), `ValueMember = Account.Name` (string identity since that's what the binding stores). `(none — clear binding)` is the first item and maps to empty-string `TargetName` on Save.

### 6. Dispatch wiring

**File:** `UI/TrayManager.cs` — `RegisterHotkeys` (line 356).

Add two new registration blocks, right after the existing `AutoLogin1-4` / `TeamLogin1-4` registrations, before the low-level keyboard hook block:

```csharp
// Phase 5a: family-table dispatch. AccountHotkeys → LoginToCharselect,
// CharacterHotkeys → LoginAndEnterWorld. TargetName empty = migration padding,
// skip. Name-based registration reuses the existing HotkeyManager.Register API —
// no arbitrary-ID overload needed since Phase 5a's mockups settled on per-entity
// rows (not positional slots).
foreach (var binding in hk.AccountHotkeys)
{
    if (string.IsNullOrEmpty(binding.Combo) || string.IsNullOrEmpty(binding.TargetName))
        continue;
    var capturedName = binding.TargetName;  // capture for closure
    TryRegister(binding.Combo,
        () => FireAccountHotkeyByName(capturedName),
        $"AccountHK:{capturedName}");
}

foreach (var binding in hk.CharacterHotkeys)
{
    if (string.IsNullOrEmpty(binding.Combo) || string.IsNullOrEmpty(binding.TargetName))
        continue;
    var capturedName = binding.TargetName;
    TryRegister(binding.Combo,
        () => FireCharacterHotkeyByName(capturedName),
        $"CharacterHK:{capturedName}");
}
```

**New dispatch helpers:**

```csharp
private void FireAccountHotkeyByName(string name)
{
    if (_settingsForm != null && !_settingsForm.IsDisposed) return;   // Phase 3.5-A gate
    var account = _config.FindAccountByName(name);
    if (account == null)
    {
        ShowBalloon($"Account Hotkey: '{name}' not found. Open Hotkeys → Configure → review stale rows.");
        FileLogger.Warn($"AccountHotkey fired for missing target '{name}' — user should rebind in Account Hotkeys dialog");
        return;
    }
    FireAccountLogin(account);   // Phase 3 helper already exists
}

private void FireCharacterHotkeyByName(string name)
{
    if (_settingsForm != null && !_settingsForm.IsDisposed) return;   // Phase 3.5-A gate
    var character = _config.FindCharacterByName(name);
    if (character == null)
    {
        ShowBalloon($"Character Hotkey: '{name}' not found. Open Hotkeys → Configure → review stale rows.");
        FileLogger.Warn($"CharacterHotkey fired for missing target '{name}' — user should rebind in Character Hotkeys dialog");
        return;
    }
    FireCharacterLogin(character);   // Phase 3 helper already exists
}
```

**Coexistence with legacy `AutoLoginN` dispatch:** `TrayManager.RegisterHotkeys` continues to register `hk.AutoLogin1-4` mapped to `ExecuteTrayAction("AutoLogin[N]")` which routes through `FireLegacyQuickLoginSlot`. Both paths coexist during v3.10.x. The hotkey-conflict check prevents a user from accidentally double-binding the same combo to a legacy slot and a new family-table entry.

### 7. Conflict detection augment

**File:** `UI/SettingsForm.cs` — `ApplySettings` P3.5-D block (roughly lines 1167-1210 area today) AND inside each new dialog's Save path.

Today's scan collects 13 hotkey strings from the Hotkeys-tab TextBoxes. Phase 5a adds the family-table bindings to the scan:

```csharp
// Phase 5a: extend the Phase 3.5-D scan with AccountHotkeys + CharacterHotkeys.
var familyHotkeys = new List<(string label, string combo)>();
foreach (var b in _config.Hotkeys.AccountHotkeys)
    if (!string.IsNullOrEmpty(b.Combo) && !string.IsNullOrEmpty(b.TargetName))
        familyHotkeys.Add(($"Account '{b.TargetName}'", b.Combo));
foreach (var b in _config.Hotkeys.CharacterHotkeys)
    if (!string.IsNullOrEmpty(b.Combo) && !string.IsNullOrEmpty(b.TargetName))
        familyHotkeys.Add(($"Character '{b.TargetName}'", b.Combo));

var allHotkeys = existingArray.Concat(familyHotkeys).ToArray();
// ... rest of the dupe scan unchanged
```

The dialogs' own Save scans mirror this, pulling current live values from the non-editing family table + the tab-level combos.

### 8. HotkeyManager API verification

The existing `HotkeyManager.Register(string key, Action callback)` returns an internally-assigned `int` id. Phase 5a's name-based dispatch doesn't need caller-controlled IDs — the registration is one-shot per binding on `ReloadConfig` and the ID is opaque. Master-plan concern about ID ranges (`1000-1099` / `1100-1199`) was tied to the earlier positional-slot plan; per-entity rows obsolete that requirement.

**No HotkeyManager API change required.**

## File delta summary

| File | Change | Approx. lines |
|---|---|---|
| `UI/TrayManager.cs` | Alt+M gate removal + family-table registration + 2 new Fire helpers | +~50 |
| `UI/SettingsForm.cs` | Hotkeys tab layout (drop Quick Individual Login Accounts + Auto-Login Hotkeys cards, add Direct Bindings card + legacy banner) + conflict scan extension | +~80, -~120 net |
| `UI/AccountHotkeysDialog.cs` | **NEW** | ~220 |
| `UI/CharacterHotkeysDialog.cs` | **NEW** | ~220 |
| `Config/HotkeyBindingUtil.cs` | **NEW** — stale-binding counters | ~40 |
| `Config/AppConfig.cs` | Add top-level `HotkeysLegacyBannerDismissed: bool = false` | +~3 |
| `Core/HotkeyManager.cs` | **No change** — existing API fits | 0 |

Net: ~+495 lines across 2 new files + 4 modified, ~120 lines deleted from SettingsForm (the legacy cards).

## Implementation sequence

Each bullet = one atomic commit.

1. `fix(tray): remove Alt+M first-time-use gate` — one-file edit in `TrayManager.OnToggleMultiMonitor`. SettingsForm also stops writing `MultiMonitorEnabled` (simplify OR-expression). Build green; Alt+M fires end-to-end without the config flag. Standalone enough to ship before the rest.
2. `feat(config): HotkeyBindingUtil + UiPrefs.HotkeysLegacyBannerDismissed` — new helper file + AppConfig field. Adds `CountStaleAccountBindings` / `CountStaleCharacterBindings`. No caller yet; unit-testable via existing build.
3. `feat(settings): AccountHotkeysDialog modal` — new file. Uses existing `MakeHotkeyBox`. Not yet wired from SettingsForm.
4. `feat(settings): CharacterHotkeysDialog modal` — new file. Mirror of #3 against Characters.
5. `feat(settings): Hotkeys tab Direct Bindings redesign` — SettingsForm drops the legacy Quick Login + Auto-Login cards, adds the Direct Bindings card with Configure buttons + counts + stale summary + legacy deprecation banner (respects `HotkeysLegacyBannerDismissed`). Wires the two new dialogs.
6. `feat(tray): family-table dispatch` — `RegisterHotkeys` loops over `AccountHotkeys` / `CharacterHotkeys` and registers them via name-based dispatch. New `FireAccountHotkeyByName` / `FireCharacterHotkeyByName` helpers. Existing legacy `AutoLogin1-4` path coexists.
7. `feat(settings): conflict-scan extension to family tables` — SettingsForm's `ApplySettings` + both new dialogs' Save paths pull family-table bindings into the duplicate-combo scan.
8. `chore(tray): stale-binding balloon on orphan hotkey fire` — if a family-table binding fires but its target doesn't resolve (deleted between Save and dispatch), show a balloon and log. Part of the Fire helpers; adds a last-mile safety net.

**Review + ship:**

9. Three review agents in parallel (`pr-review-toolkit:code-reviewer`, `pr-review-toolkit:silent-failure-hunter`, `feature-dev:code-reviewer`) pointed at `01d05ed..HEAD`.
10. One or more `fix(...)` commits folding findings.
11. `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` → deploy to `C:/Users/nate/proggy/Everquest/EQSwitch/` (kill running processes first).
12. Nate-driven smoke test.
13. Memory-file update. **STOP** for Phase 5b sign-off.

## Verification gate

### Build + non-regression
- `dotnet build --no-incremental` → 0 errors, 1 expected `[Obsolete]` warning at `TrayManager.cs` (the still-present `ExecuteQuickLogin` — Phase 6 deletes it).
- `bash _tests/migration/run_fixtures.sh` → 9 passed.
- `grep -c "gameState == 5" Native/mq2_bridge.cpp` → 2.
- `grep -c "result == -2" Core/AutoLoginManager.cs` → 1.

### Manual smoke test
- **Alt+M** fires multi-monitor toggle from a fresh config where `multiMonitorEnabled: false`. Balloon reads "Layout: Multi-Monitor" (not "Enable Multi-Monitor mode in Settings first").
- **Direct Bindings card** renders `{N}/{total} bound` counts correctly against the current config.
- **Configure Account Hotkeys** opens `AccountHotkeysDialog` showing one row per `_config.Accounts` entry.
- Bind `Alt+P` to a specific Account. Save. Confirm tray menu shows `Alt+P` next to the Account item. Press Alt+P → Account logs in, stops at charselect (phantom-click gate unviolated).
- **Configure Character Hotkeys** — same flow for a Character. Press the bound combo → character enters world.
- **Stale binding:** add an Account, bind Alt+T to it. Delete the Account via the Accounts tab (Cancel path). Reopen Hotkeys tab → stale summary `⚠ 1 stale binding`. Open Account Hotkeys → red row with `Rebind → ▾`. Pick a live Account from the dropdown. Save. Combo now routes to the new target.
- **Stale clear:** same scenario but pick `(none — clear binding)` from the dropdown. Save. Binding removed from config.
- **Conflict detection:** bind `Alt+T` to an Account AND a Character. Save either dialog → modal blocks with both labels.
- **Legacy deprecation banner:** config with populated `QuickLogin1-4` shows the banner. Dismiss → banner stays hidden across reopens + sessions.
- **Legacy coexistence:** old v3 config with `AutoLogin1 = "Alt+N"`, `QuickLogin1 = "natedogg"` still fires via the legacy path.

## Agent fanout

Three agents in parallel after all implementation commits land, before publish:

1. **`pr-review-toolkit:code-reviewer`** — conventions (no `Color.FromArgb` outside DarkTheme, all controls via factories, conventional commits). Focus on dialog style consistency with Phase 4 (`AccountEditDialog` etc.).
2. **`pr-review-toolkit:silent-failure-hunter`** — stale-binding edges (deleted-then-recreated-with-same-name, rebind-to-self, dropdown-cleared-then-save-without-rebind). Conflict-scan completeness (does it catch every combination of family + legacy + tab-level sources?). `FireAccountHotkeyByName` null paths.
3. **`feature-dev:code-reviewer`** — independent UX pass: does the Direct Bindings card glance read? Is the legacy deprecation banner dismissible in a way that survives config reloads? Does the stale-binding dropdown include orphan Characters correctly flagged?

## Risks + rollback

| Risk | Likelihood | Mitigation |
|---|---|---|
| Alt+M gate removal surprises users who liked the safety | Low | Alt+M is opt-in (bound in config); if unbound it's inert. The gate was preventing accidental fires on configured bindings — weak rationale. |
| Family-table dispatch collides with legacy AutoLogin dispatch | Medium | Hotkey-conflict scan extended in commit 7 blocks duplicate binds at Save time. Runtime: `RegisterHotKey` silently fails on the second registration, which users would see as "hotkey doesn't fire" — same symptom as today's v3 collision. |
| `HotkeyBindingUtil` stale counter misses the empty-padding contract | Low | Explicit `!string.IsNullOrEmpty(b.TargetName)` guard in both counters. Fixture assertions from Phase 3's BUG-1 patch still pin the emitted shape. |
| Legacy banner dismiss state lost on config load | Low | Persisted via `UiPrefs.HotkeysLegacyBannerDismissed: bool`, copied through `TrayManager.ReloadConfig`'s hand-copy block (same pattern as all other fields — miss this copy and we get the Phase 4 TogglePip regression all over again). |
| Rebind dropdown doesn't include orphan Characters | Low | `CharacterHotkeysDialog` dropdown iterates all `_config.Characters`, including empty-FK orphans; tagged with `(no account)` suffix. Tested in verification gate. |
| HotkeyManager.Register API turns out to need caller-controlled IDs after all | Low | Implementation plan Task 6 verifies first; if the assumption breaks, add `RegisterWithId(int id, string combo, Action callback, string name)` overload as a sub-task. |

**Rollback:** all commits touch `UI/*.cs`, `Config/AppConfig.cs`, `Config/HotkeyBindingUtil.cs`. Zero native changes. Zero migration schema changes. `git revert` of any Phase 5a commit is safe — existing `HotkeyConfig.AccountHotkeys` + `CharacterHotkeys` lists stay in the config but go unconsumed by the reverted tray. The legacy `QuickLogin1-4` + `AutoLogin1-4` path stays wired throughout; users lose nothing on revert.

## Open questions / resolved decisions

All resolved during brainstorming 2026-04-15:

- ✅ Hotkeys tab layout: **per-entity in modal dialogs**, main tab stays glanceable with Configure buttons + counts. No scroll on the tab itself.
- ✅ Dialog aesthetic: **shipped DarkTheme** (Option A in the v1 mockup) — no chrome refresh.
- ✅ Alt+M lock gate: **remove entirely** (Option A). Gate adds friction without meaningful safety.
- ✅ Stale-binding UI: **red row + inline Rebind → ▾ dropdown** (Option B). Matches AutoLoginTeamsDialog pattern; craftsmanship over minimum.
- ✅ HotkeyManager API: **existing name-based Register is sufficient**. No new overload.
- ✅ Legacy v3 `QuickLoginN` + `HotkeyConfig.AutoLoginN`: coexist during v3.10.x with deprecation banner. Phase 6 deletes.
- ✅ Phase 5b deferrals: `WindowManager` / `AffinityManager` / `CharacterSelector` extraction — separate phase, separate smoke test, separate sign-off.

---

**End of design. Next step: `superpowers:writing-plans` produces the task-level implementation plan. Phase 5a ships atomically; Phase 5b waits for Phase 5a sign-off.**
