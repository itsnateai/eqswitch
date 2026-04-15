# EQSwitch Phase 3 — Tray Menu Rebuild Design

**Status:** Approved design, ready for implementation-plan drafting
**Created:** 2026-04-14
**Parent plan:** `PLAN_account_character_split.md` (lines 250-286, 319-329, 376-382, 405-413)
**Handoff:** `PLAN_account_character_split_HANDOFF_phase3.md`
**Foundation:** commit `beb2862` (Phase 1 + 2 + 2.5 + 2.6 + 2.7 shipped and agent-reviewed)

## Purpose

Rebuild `UI/TrayManager.cs.BuildContextMenu()` from its current single-submenu mixed-intent shape into three intent-explicit submenus — Accounts, Characters, Teams — that read directly from the v4 data model shipped in Phase 1. Phase 3 is the release where v3.10.0 becomes user-visible.

The tray menu is EQSwitch's most-interacted-with surface. Phase 3 must reflect top-tier craftsmanship at every step.

## Goals

1. Click an Account row → login, stop at character select (`LoginToCharselect`).
2. Click a Character row → login and enter world (`LoginAndEnterWorld`).
3. Click a Team row (or the root `Launch Team` button) → fire N clients in parallel.
4. Bare `Launch Client` root button → unchanged.
5. Preserve every existing hotkey dispatch path (`HotkeyConfig.AutoLogin1-4`, `TeamLogin1-4`) via transitional routing through new API where possible.
6. Preserve phantom-click defenses bit-for-bit.
7. Obsolete the `LoginAccount` call site at `UI/TrayManager.cs:817`. Leave `:1330` alone — it belongs to Phase 5.

## Non-goals (deferred by handoff)

- Character grouping by Account. Flat list while `Characters.Count <= 10`. Phase 4+ measures and implements grouping.
- Phase 4 Settings dual-section UI (Account/Character edit dialogs, cross-section validation).
- Phase 5 Account/Character hotkey family tables (`AccountHotkeys[]`, `CharacterHotkeys[]`).
- Direct invocation of `AutoLoginTeamsDialog` from "Manage Teams..." — `ShowSettings(2)` is the Phase 3 target for all three Manage entries.

---

## Pre-Phase-3 patch (own commit, lands first)

Agent audit (`2026-04-14`) surfaced one foundation defect plus one test-coverage gap that must close before Phase 3 starts. Small, atomic, own commit.

> **Note — BUG-2 already resolved in commit `4e05007`** (`fix(config): drop dead cycleFocused from SwitchKeyMode validator`). Dead allowlist entry with no UI/runtime/migration anywhere else in the codebase; 1-line removal. SwitchKey behavior unchanged. Not part of this pre-Phase-3 patch.

### BUG-1 — Phantom hotkey binding at `AccountHotkeys[0]` / `CharacterHotkeys[0]`

**File:** `Config/ConfigVersionMigrator.cs:314-318` (`EnsureSize` helper).

**Symptom:** When v3 only binds `autoLogin2` (slot 2), migration emits `accountHotkeys: [{combo:"", targetName:""}, {combo:"Alt+2", targetName:"..."}]` — a phantom empty binding at index 0 (slot 1).

**Impact on Phase 3:** none. Phase 3 does not consume `AccountHotkeys`/`CharacterHotkeys`.

**Impact on Phase 5:** if the dispatcher iterates naively, it attempts to register a hotkey with an empty combo string → either throws in `HotkeyManager.Register` or silently no-ops.

**Fix (defensive — documents the contract without changing schema):**

The emitted shape `[{combo:"",targetName:""}, …, {real}]` is semantically necessary: Phase 5 dispatches positionally (`id - 1000 = slot index` per plan line 729-745), so array index `N-1` must mean "v3 slot `N`." The phantom at index 0 when only slot 2+ is bound is correct padding, not a bug — the real risk is Phase 5 dispatcher iterating naively and trying to register a hotkey with `combo == ""`.

1. Add an invariant comment above `EnsureSize`: *"Padded entries are positional placeholders — index `N-1` always represents v3 slot `N`. Phase 5 consumers MUST skip entries where `Combo` or `TargetName` is empty. Do not remove padding without also revising the position-to-slot contract in the Phase 5 plan (line 729-745)."*
2. Add fixture length assertions so the emitted shape is pinned down by tests:
   - `fixture_e`: `accountHotkeys | length == 2` (slot 1 phantom + slot 2 real) — proves the pad-before-write semantics.
   - `fixture_g`: `accountHotkeys | length == 1` (slot 1 real, no phantoms needed).
   - `fixture_a` / `fixture_d`: `characterHotkeys | length == 1`.
3. Add a sibling assertion that `accountHotkeys[0].combo` in fixture_e is exactly the empty string (not null, not absent) — locks the phantom shape Phase 5 must recognize.
4. Cross-phase invariant: the HANDOFF's "During Phase 5" deferred block gets one new bullet: *"AccountHotkeys/CharacterHotkeys dispatcher must `if (IsEmpty(binding)) continue` in its registration loop — positional padding from Phase 1 migration uses empty-string entries as unbound-slot placeholders."*

### GAP-3 — No fixture covers Step 3 (team rebinding)

**Add `fixture_i_team_mixed.json`:**
- Team 1 Slot 1 = CharacterName that resolves to a Character (default case)
- Team 1 Slot 2 = Username with no CharacterName (Account fallback; warn logged)
- Team 2 Slot 1 = string that resolves to nothing (blank + warn)
- Team 1 AutoEnter = true, Team 2 AutoEnter = false

Assertions: correct rebinding of all three slots; `Team1AutoEnter == true` round-trip; one `"did not resolve"` warn line in output.

### Commit

`fix(migration): document empty-slot padding + add length/team-rebind fixtures`

---

## Phase 3 components

### Model additions

Per handoff #7 (label fallback chains on the models so tray, settings, AutoLoginTeamsDialog share one source).

**`Models/Account.cs`** — add:

```csharp
public string EffectiveLabel
{
    get
    {
        if (!string.IsNullOrEmpty(Name)) return Name;
        if (!string.IsNullOrEmpty(Username)) return Username;
        return "(unnamed account)";
    }
}

public string Tooltip => $"{Username}@{Server}";
```

**`Models/Character.cs`** — add:

```csharp
public string EffectiveLabel
{
    get
    {
        if (!string.IsNullOrEmpty(DisplayLabel)) return DisplayLabel;
        if (!string.IsNullOrEmpty(Name)) return Name;
        return "(unnamed character)";
    }
}

public string LabelWithClass =>
    string.IsNullOrEmpty(ClassHint) ? EffectiveLabel : $"{EffectiveLabel} ({ClassHint})";
```

Migration invariant: Character rows only exist when v3 had a non-empty `CharacterName`, so the third fallback is dead code. It exists as defense-in-depth against hand-edited configs. Same for Account.

### AppConfig lookup helpers

Per handoff #8 (one place for name-based lookup; future Phase 5 dispatcher needs the same helpers).

**`Config/AppConfig.cs`** — add:

```csharp
public Account? FindAccountByName(string name) =>
    string.IsNullOrEmpty(name) ? null : Accounts.FirstOrDefault(a => a.Name == name);

public Character? FindCharacterByName(string name) =>
    string.IsNullOrEmpty(name) ? null : Characters.FirstOrDefault(c => c.Name == name);
```

Ordinal comparison. Matches v3 `ExecuteQuickLogin` semantics at `TrayManager.cs:1321-1322`. Phase 4's Accounts-tab validator will enforce case consistency at the data layer.

### TrayManager new helpers

All helpers take their inputs as parameters (not `_config`-reaching). Feature-dev:code-explorer's recommendation, locked in.

**`LegacyHotkeyLookup`** (private nested class in `TrayManager`):

```csharp
private sealed class LegacyHotkeyLookup
{
    private readonly Dictionary<string, string> _byTarget = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _targetToSlot = new(StringComparer.Ordinal);

    public LegacyHotkeyLookup(AppConfig config)
    {
        var hk = config.Hotkeys;
        Register(1, config.QuickLogin1, hk.AutoLogin1);
        Register(2, config.QuickLogin2, hk.AutoLogin2);
        Register(3, config.QuickLogin3, hk.AutoLogin3);
        Register(4, config.QuickLogin4, hk.AutoLogin4);
    }

    private void Register(int slot, string target, string combo)
    {
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(combo)) return;
        _byTarget[target] = combo;     // last binding wins on duplicate target
        _targetToSlot[target] = slot;  // same — last wins
    }

    public string GetCombo(string name) =>
        !string.IsNullOrEmpty(name) && _byTarget.TryGetValue(name, out var c) ? c : "";

    public int? GetSlot(string name) =>
        !string.IsNullOrEmpty(name) && _targetToSlot.TryGetValue(name, out var s) ? s : null;
}
```

Built once per `BuildContextMenu()` call. `GetSlot` supports the first-fire deprecation log (see Menu Structure § AutoLoginN dispatch).

**Submenu builders:**

```csharp
private ToolStripMenuItem BuildAccountsSubmenu(
    IReadOnlyList<Account> accounts,
    LegacyHotkeyLookup hkLookup);

private ToolStripMenuItem BuildCharactersSubmenu(
    IReadOnlyList<Character> characters,
    IReadOnlyDictionary<AccountKey, Account> accountsByKey,
    LegacyHotkeyLookup hkLookup);

private ToolStripMenuItem BuildTeamsSubmenu(
    AppConfig cfg,
    LegacyHotkeyLookup hkLookup);
```

Each returns a fully-populated `ToolStripMenuItem` with children, separator, and `"⚙  Manage X..."` entry. Added to `_contextMenu.Items` by `BuildContextMenu`.

**Fire helpers** (thin, self-contained, try/catch at the click boundary):

```csharp
private void FireAccountLogin(Account account)
{
    try
    {
        ShowBalloon($"Logging in {account.EffectiveLabel} — stopping at charselect");
        _ = _autoLoginManager.LoginToCharselect(account);
    }
    catch (Exception ex)
    {
        FileLogger.Error($"FireAccountLogin CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
        ShowBalloon($"Login error: {ex.Message}");
    }
}

private void FireCharacterLogin(Character character)
{
    try
    {
        ShowBalloon($"Logging in {character.EffectiveLabel} — entering world");
        _ = _autoLoginManager.LoginAndEnterWorld(character);
    }
    catch (Exception ex)
    {
        FileLogger.Error($"FireCharacterLogin CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
        ShowBalloon($"Login error: {ex.Message}");
    }
}
```

**`FireLegacyQuickLoginSlot(int slot)`** — replaces `ExecuteQuickLogin` for tray-click and hotkey `AutoLoginN` dispatch (not team dispatch):

```csharp
private readonly HashSet<int> _legacySlotDeprecationLogged = new();

private void FireLegacyQuickLoginSlot(int slot)
{
    string targetName = slot switch
    {
        1 => _config.QuickLogin1,
        2 => _config.QuickLogin2,
        3 => _config.QuickLogin3,
        4 => _config.QuickLogin4,
        _ => ""
    };
    if (string.IsNullOrEmpty(targetName))
    {
        ShowBalloon($"Quick Login {slot}: no account assigned");
        return;
    }

    // Character-first resolution (mirrors v3 migration preference).
    var character = _config.FindCharacterByName(targetName);
    if (character != null)
    {
        LogFirstFire(slot, "Character", character.EffectiveLabel);
        FireCharacterLogin(character);
        return;
    }

    var account = _config.FindAccountByName(targetName);
    if (account != null)
    {
        LogFirstFire(slot, "Account", account.EffectiveLabel);
        FireAccountLogin(account);
        return;
    }

    ShowBalloon($"Quick Login {slot}: '{targetName}' not found");
    FileLogger.Warn($"Legacy QuickLogin{slot}: target '{targetName}' does not resolve to any Account or Character");
}

private void LogFirstFire(int slot, string family, string label)
{
    if (_legacySlotDeprecationLogged.Add(slot))
    {
        FileLogger.Info($"Legacy QuickLogin{slot} routed via new API → {family} '{label}' (this mapping moves to {family}Hotkeys in Phase 5)");
    }
}
```

One-shot log per slot per session per plan line 372.

**`FireTeam(int teamIndex)`** — renamed from `FireTeamLogin`. Preserves parallel fire-and-forget verbatim. Takes team index; internally resolves team config.

```csharp
private void FireTeam(int teamIndex)
{
    var (slots, teamAutoEnter, teamName) = ResolveTeamConfig(teamIndex);
    int fired = 0;
    foreach (var (user, slotLabel) in slots)
    {
        if (!string.IsNullOrEmpty(user))
        {
            _ = ExecuteQuickLogin(user, slotLabel, teamAutoEnter);  // parallel — NO await
            fired++;
        }
    }
    if (fired == 0)
    {
        ShowWarning($"No accounts assigned to {teamName} — configure in Settings → Accounts");
        FileLogger.Warn($"FireTeam: {teamName} has no accounts assigned");
    }
}

private (IReadOnlyList<(string user, string label)> slots, bool autoEnter, string teamName)
    ResolveTeamConfig(int teamIndex) => teamIndex switch
{
    1 => (new[] { (_config.Team1Account1, "Team 1 Slot 1"), (_config.Team1Account2, "Team 1 Slot 2") },
          _config.Team1AutoEnter, "Team 1"),
    2 => (new[] { (_config.Team2Account1, "Team 2 Slot 1"), (_config.Team2Account2, "Team 2 Slot 2") },
          _config.Team2AutoEnter, "Team 2"),
    3 => (new[] { (_config.Team3Account1, "Team 3 Slot 1"), (_config.Team3Account2, "Team 3 Slot 2") },
          _config.Team3AutoEnter, "Team 3"),
    4 => (new[] { (_config.Team4Account1, "Team 4 Slot 1"), (_config.Team4Account2, "Team 4 Slot 2") },
          _config.Team4AutoEnter, "Team 4"),
    _ => (Array.Empty<(string, string)>(), false, $"Team {teamIndex}")
};
```

`ExecuteQuickLogin` remains **unchanged** — only `FireTeam` calls it now. The `:1330` `[Obsolete]` warning stays until Phase 5 replaces the whole team path with `ResolveTeamSlots` + new API.

### Menu structure

Per plan mockup line 252-279. Tray menu top-to-bottom:

```
⚔  EQ Switch v3.10.0        (disabled, bold title)
─────────
⚔  Launch Client            Alt+F1         (bold, root button)
🎮  Launch Team              Ctrl+Alt+1    (bold, root button)
─────────                                   ← NEW separator
🔑  Accounts ▸               (bold)          tooltip: "Login and stop at character select"
    👤  Main Account         Alt+1         tooltip: "nate1@Dalaya"
    👤  Alt Account          Alt+2
    ─────────
    ⚙  Manage Accounts...
🧙  Characters ▸             (bold)          tooltip: "Login and enter world"
    🧙  Backup (Cleric)      Alt+3         tooltip: "→ Account 'Main Account' · slot auto"
    🧙  Healpots (Cleric)
    🧙  Acpots (Rogue)
    🧙  Staxue (Ranger)
    ─────────
    ⚙  Manage Characters...
👥  Teams ▸                  (bold)          tooltip: "Launch multiple clients in parallel"
    🚀  Auto-Login Team 1    Ctrl+Alt+1    tooltip: multi-line slot preview
    ─────────
    ⚙  Manage Teams...
─────────
👤  Clients ▸                (existing, unchanged)
─────────
⚡  Process Manager
📺  Video Settings ▸
─────────
⚙  Settings...
📁  Launcher ▸
─────────
✖  Exit
```

Icons are `\u`-escaped surrogate pairs per current convention (see `TrayManager.cs:794, 802, 807` for precedent).

**Empty-state rows** (teaching-forward copy per handoff #2 and #3):

| Submenu | When | Disabled row copy |
|---|---|---|
| Accounts | `_config.Accounts.Count == 0` | `No accounts yet — click Manage Accounts...` |
| Characters | `_config.Characters.Count == 0` | `No characters yet — characters added here will auto-enter-world` |
| Teams | All four `TeamNAccount1` and `TeamNAccount2` empty | `No teams configured — click Manage Teams...` |

Each followed by separator + `⚙  Manage X...` entry. The "Manage X..." entries always render regardless of populated state.

**`ShowSettings(2)` — Accounts tab** — all three Manage entries call the same method. Phase 4 will add an inner-section focus parameter; for Phase 3 the tab opens and the user navigates inside.

**Teams submenu filtering:** only populated teams render as clickable rows (a team is populated if either `TeamNAccount1` or `TeamNAccount2` is non-empty). When zero teams are populated, the empty-state row renders. This matches the current behavior at `TrayManager.cs:827-837` and handoff #4.

**Submenu ordering:** insertion order (the v4 config's `accounts` and `characters` list order) — same as v3.

### Tooltips

`ToolStripMenuItem.ToolTipText` — hover-only, non-stealing, respects Windows tooltip delay.

| Element | Tooltip |
|---|---|
| Parent `🔑 Accounts` | `Login and stop at character select` |
| Parent `🧙 Characters` | `Login and enter world` |
| Parent `👥 Teams` | `Launch multiple clients in parallel` |
| Account item | `{Username}@{Server}` via `Account.Tooltip` |
| Character item | `→ Account '{account.EffectiveLabel}' · slot {auto/N}` via `BuildCharacterTooltip(character, accountsByKey)`. Falls back to `{AccountUsername}@{AccountServer} (unresolved)` if FK drift has broken the link. |
| Team item | Multi-line: `Slot 1: {resolved → destination}\nSlot 2: {resolved → destination}\n[force enter world]` (last line only when `TeamNAutoEnter == true`). `{resolved → destination}` is `{Character.LabelWithClass} → enter world` or `{Account.EffectiveLabel} → charselect` or `(empty)` or `{raw} (unresolved)`. |
| Root `⚔ Launch Client` | `Launch bare eqgame.exe patchme` |
| Root `🎮 Launch Team` | `Launch Team 1 (one-click default)` |

### Balloons on click

`ShowBalloon(...)` — 4-second ephemeral system tray balloon. Intent-explicit copy so mis-clicks are recognisable.

| Click | Balloon |
|---|---|
| Account submenu item | `Logging in {EffectiveLabel} — stopping at charselect` |
| Character submenu item | `Logging in {EffectiveLabel} — entering world` |
| Team submenu item | (unchanged — `ExecuteQuickLogin` already balloons per slot) |
| Legacy tray-click `AutoLogin1..4` | Same as whichever submenu the resolution lands in |

---

## Call-graph after rewrite

```
Accounts ▸ Main                → FireAccountLogin(acc)       → LoginToCharselect            [new API]
Characters ▸ Backup            → FireCharacterLogin(ch)      → LoginAndEnterWorld           [new API]
Teams ▸ Team 1                 → ExecuteTrayAction("LoginAll")
                                → FireTeam(1)
                                → ExecuteQuickLogin (×2)
                                → LoginAccount (Obsolete)     [deliberate until Phase 5]
Root "Launch Team"             → ExecuteTrayAction("LoginAll") → FireTeam(1)
Tray-icon SingleClick=AutoLogin1 → ExecuteTrayAction("AutoLogin1")
                                 → FireLegacyQuickLoginSlot(1)
                                 → Character-first → Fire*Login → [new API]
Hotkey AutoLogin1              → same as SingleClick
Hotkey TeamLogin1              → same as Teams ▸ Team 1
```

**Warning delta:**

- `TrayManager.cs:817` — gone (`LoginAccount` direct call in the old combined submenu removed).
- `TrayManager.cs:1330` — stays (`ExecuteQuickLogin` → `LoginAccount` in the team path).

Count: 2 → 1.

---

## Disposal invariant

`BuildContextMenu()` runs on every `ReloadConfig()`. Plan nuance #8 is authoritative:

1. `_contextMenu?.Dispose()` at `:778` — `ContextMenuStrip.Dispose()` cascades to every `ToolStripMenuItem` it owns, including nested submenus. My new `BuildAccountsSubmenu`/`BuildCharactersSubmenu`/`BuildTeamsSubmenu` return items that are attached to `_contextMenu.Items` via `Add()` — dispose covers them.
2. `_boldMenuFont?.Dispose()` at `:789` happens *after* `_contextMenu` is rebuilt but before any new bold items are created — the order is correct but fragile. Do not reorder.

No additional disposal work needed. The `_legacySlotDeprecationLogged` HashSet survives menu rebuilds (intentional — session-level dedup).

---

## Migration of `ExecuteTrayAction` cases

Current dispatch table (`TrayManager.cs:1236-1309`) mutates as follows:

```csharp
case "AutoLogin1": FireLegacyQuickLoginSlot(1); break;   // was: _ = ExecuteQuickLogin(_config.QuickLogin1, "Quick Login 1")
case "AutoLogin2": FireLegacyQuickLoginSlot(2); break;
case "AutoLogin3": FireLegacyQuickLoginSlot(3); break;
case "AutoLogin4": FireLegacyQuickLoginSlot(4); break;
case "LoginAll":  FireTeam(1); break;                     // was: FireTeamLogin(...)
case "LoginAll2": FireTeam(2); break;
case "LoginAll3": FireTeam(3); break;
case "LoginAll4": FireTeam(4); break;
// all other cases unchanged
```

`ExecuteQuickLogin` stays exactly as-is — its only caller is now `FireTeam`.

---

## Verification plan

### Phase 3 verification gate (per plan line 405-413 and handoff line 103-108)

1. **Launch mode fan-out — each fires correctly from the tray menu:**
   - Root `⚔ Launch Client` → `eqgame.exe patchme` alone, no login attempt. Existing path, regression check.
   - `Accounts ▸ {any}` → EQ launches, types credentials, stops at character select (no Enter-World click). Verify `eqswitch-dinput8.log` shows no `clicked CLW_EnterWorldButton` line after typing completes.
   - `Characters ▸ {any}` → EQ launches, types credentials, selects character, enters world. Verify `eqswitch-dinput8.log` shows the `clicked CLW_EnterWorldButton` and subsequent `gameState → 5` transition.
   - Root `🎮 Launch Team` → Team 1 fires; ≥1 client launched (or warn balloon if zero slots populated).
   - `Teams ▸ Team N` (N ∈ {1, 2, 3, 4}) → all populated slots fire in parallel.

2. **Phantom-click gate (non-regression):**
   - `grep "gameState == 5" Native/mq2_bridge.cpp | wc -l` → `2` (bytes unchanged vs `beb2862`).
   - `grep "result == -2" Core/AutoLoginManager.cs | wc -l` → `≥1`.
   - After each of the 5 launch modes above, re-run: `grep XWM_LCLICK C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-dinput8.log | grep -A2 "gameState -> 5"` → empty.

3. **Build:** `dotnet build` — 0 errors, exactly **1** `[Obsolete]` warning (at `TrayManager.cs:~1330`; the `:817` site is gone).

4. **Migration fixtures:** `bash _tests/migration/run_fixtures.sh` — 9 fixtures pass (8 original + new `fixture_i_team_mixed`). All length assertions from pre-patch pass.

5. **Live-config round-trip:** open Settings, change one cosmetic field, save. Reload. Verify:
   - `_config.Accounts.Count == 3` (Nate's dataset: natedogg, flotte, acpots).
   - `_config.Characters.Count == 4` (natedogg, flotte, acpots, backup).
   - No JSON diff in `accountsV4`, `charactersV4` vs pre-save.

### Agent fanout post-implementation

Run these three in parallel after the Phase 3 implementation commit lands locally, before publish + deploy:

- **`pr-review-toolkit:code-reviewer`** — general correctness + CLAUDE.md conventions (DarkTheme factories, no raw `DllImport`, conventional commits, etc).
- **`pr-review-toolkit:silent-failure-hunter`** — tray click handlers, empty-state edges, error balloon UX, exception-swallowing in Fire* helpers.
- **`feature-dev:code-reviewer`** — independent second opinion on the tray menu rebuild surface, focused on the call-graph correctness.

Each agent receives a self-contained prompt referencing this spec by path. Findings are folded back into the implementation before publish.

### Nate smoke test (user-driven, after deploy)

Deploy order: `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` → copy `bin/Release/net8.0-windows/win-x64/publish/*` to `C:/Users/nate/proggy/Everquest/EQSwitch/`.

Script for Nate:

1. Right-click tray icon → inspect menu. Confirm three submenus visible (🔑 Accounts, 🧙 Characters, 👥 Teams) + "Manage X..." entries.
2. Hover each parent submenu — confirm tooltips ("Login and stop at character select", etc).
3. Click `Accounts ▸ natedogg` → balloon `Logging in natedogg — stopping at charselect` → EQ launches → stops at charselect. Confirm no enter-world click.
4. Close EQ. Click `Characters ▸ backup` → balloon `Logging in backup — entering world` → EQ launches → enters world as backup.
5. Close EQ. Click `Teams ▸ Auto-Login Team 1` → both slots fire in parallel.
6. Close EQ. Press the existing `Alt+1` hotkey (whatever QuickLogin1 was bound to) → confirm same routing as the submenu click.
7. Open `%APPDATA%/../Local/eqswitch-dinput8.log` (or wherever the log lives) → confirm no phantom-click after any of the above.

Signoff criterion: all 7 steps complete without error balloons, unexpected behavior, or phantom-click log entries.

---

## Risks & rollback

| Risk | Likelihood | Mitigation |
|---|---|---|
| `LegacyHotkeyLookup` miss on hand-edited case-drifted `QuickLoginN` | Low (Phase 2.7 fixed M1; Nate's config is clean) | Ordinal match matches v3 behavior exactly. Deferred-list item covers Phase 4 case validator. |
| `FireLegacyQuickLoginSlot` null-resolve on orphaned `QuickLoginN` target | Medium (possible if user manually edited config pre-v4) | Explicit balloon + WARN log. No crash. |
| `FireTeam` accidental `await` insertion during refactor | Low | Unit-obvious pattern: `_ = ExecuteQuickLogin(...)` discard-assign. Agent review will flag any `await` introduction. Plan line 371 + 484 emphasize this. |
| `ContextMenuStrip.Dispose()` order regression | Low | Existing precedent at `:778` + `:789` preserved verbatim. No new Dispose site introduced. |
| Teams submenu filter hides all teams accidentally | Low | Fallback to empty-state row even when all unpopulated; never a missing-submenu state. |
| Emoji rendering regression (fonts unavailable) | Negligible | Same Unicode surrogate-pair escapes already in use at `:794, :802, :807`. Segoe UI Emoji is a Windows 11 system font. |

**Rollback story:** the Phase 3 commit is pure UI + model + config helpers. No migration changes, no native changes. `git revert <phase-3-commit>` restores the Phase 2.7 tray verbatim. `git revert` of the pre-Phase-3 patch is equally clean (doc + fixture additions only).

---

## Implementation sequence (hand-off to writing-plans)

The implementation plan (to be produced by `superpowers:writing-plans`) should sequence as:

1. **Pre-Phase-3 patch** — BUG-1 comment + trim + fixture assertions + `fixture_i_team_mixed`. Own commit. Verify `run_fixtures.sh` passes.
2. **Model + AppConfig additions** — `Account.EffectiveLabel`, `Account.Tooltip`, `Character.EffectiveLabel`, `Character.LabelWithClass`, `AppConfig.FindAccountByName`, `AppConfig.FindCharacterByName`. Own commit. Build + migration fixtures still green.
3. **TrayManager rewrite — scaffolding** — add `LegacyHotkeyLookup`, `BuildAccountsSubmenu`, `BuildCharactersSubmenu`, `BuildTeamsSubmenu`, `FireAccountLogin`, `FireCharacterLogin`, `FireLegacyQuickLoginSlot`, `LogFirstFire`, `ResolveTeamConfig`. Do not yet rewire `BuildContextMenu` — new helpers exist alongside old code. Build green.
4. **TrayManager rewrite — switch over** — rewrite `BuildContextMenu` to use the new helpers; rename `FireTeamLogin` → `FireTeam`; update `ExecuteTrayAction` cases for `AutoLogin1-4` and `LoginAll*`. Build: warning count 2 → 1.
5. **Agent fanout (parallel)** — three review agents.
6. **Fold in agent findings** — one or more commits based on severity.
7. **Publish + deploy.**
8. **Nate smoke test.**
9. **Memory file update** — append Phase 3 status line to `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v3_10_0_account_split.md`.
10. **STOP for sign-off.** Do not start Phase 4 without explicit approval.

Each commit under 72-char title, conventional format (`feat(tray):`, `fix(migration):`, `refactor(tray):`, etc.).

---

## Open questions / resolved decisions

All resolved in brainstorming (2026-04-14):

- ✅ Hotkey suffix during Phase 3 → Phase 5 window: show legacy `AutoLogin1-4` bindings via `LegacyHotkeyLookup`.
- ✅ BUG-1 handling: fix now as pre-Phase-3 patch (own commit).
- ✅ Empty-state copy: teaching-forward per handoff #2/#3 (not terse parenthetical).
- ✅ Grouping threshold: flat while `Characters.Count <= 10`; Phase 4 implements grouping.
- ✅ `"Manage Teams..."` target: `ShowSettings(2)` (same as Accounts/Characters). Direct `AutoLoginTeamsDialog` invocation is Phase 4+.
- ✅ Submenu intent headers: dropped in favor of parent tooltips. Avoids clutter.

---

**End of design. Next step: `superpowers:writing-plans` produces the task-level implementation plan from this spec.**
