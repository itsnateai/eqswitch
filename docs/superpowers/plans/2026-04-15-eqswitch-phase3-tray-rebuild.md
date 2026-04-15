# EQSwitch Phase 3 Tray Rebuild — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the EQSwitch tray context menu into three intent-explicit submenus (Accounts, Characters, Teams) backed by the v4 data model shipped in Phase 1, eliminating one of the two `[Obsolete]` warnings and delivering the v3.10.0 user-visible surface.

**Architecture:** Add label/lookup helpers on the `Account`/`Character` models and `AppConfig`, then introduce `TrayManager` submenu + fire helpers alongside the existing `BuildContextMenu` (scaffolding phase), then switch `BuildContextMenu` and `ExecuteTrayAction` over to the new helpers and rename `FireTeamLogin` → `FireTeam`. New submenu helpers take their lists as arguments so they have no hidden `_config` reach. `ExecuteQuickLogin` is deliberately left untouched as the team-launch path — it remains the one site of the `[Obsolete] LoginAccount` wrapper call until Phase 5 replaces the whole team dispatch.

**Tech Stack:** C# 12 / .NET 8 WinForms, `ContextMenuStrip` + `ToolStripMenuItem` with custom `DarkMenuRenderer`, System.Text.Json for config persistence, `jq` + bash for migration fixture assertions.

**Spec:** [`docs/superpowers/specs/2026-04-14-eqswitch-phase3-tray-rebuild-design.md`](../specs/2026-04-14-eqswitch-phase3-tray-rebuild-design.md)

**Parent plan:** [`PLAN_account_character_split.md`](../../../PLAN_account_character_split.md)

---

## File structure

| File | Role | Change |
|---|---|---|
| `Config/ConfigVersionMigrator.cs` | v3→v4 schema migration | +2 lines of invariant comment (Task 1) |
| `_tests/migration/run_fixtures.sh` | Fixture harness | +~12 assertion lines, +1 new fixture block (Task 1) |
| `_tests/migration/fixture_i_team_mixed.json` | NEW — team rebinding coverage | Create (Task 1) |
| `Models/Account.cs` | Login credentials type | +6 lines (Task 2) — `EffectiveLabel`, `Tooltip` |
| `Models/Character.cs` | Launch-target type | +10 lines (Task 2) — `EffectiveLabel`, `LabelWithClass` |
| `Config/AppConfig.cs` | Root config with lookup helpers | +6 lines (Task 3) — `FindAccountByName`, `FindCharacterByName` |
| `UI/TrayManager.cs` | Tray orchestration hub | +~180 lines net (Tasks 4+5) — new helpers, rewritten `BuildContextMenu`, renamed `FireTeam`, updated `ExecuteTrayAction` cases |

Everything else stays untouched. Native DLLs, `AutoLoginManager`, `ExecuteQuickLogin`, and the Phase 1 migration logic are all by-design no-change.

---

## Conventions

- Every file edit shows the exact text to search for and replace. C# code uses literal backslash-u escapes for emoji (`\uD83D\uDD11`) to match existing TrayManager style.
- Every commit stages specific files (never `git add -A`).
- Conventional commit messages under 72 char titles.
- Expected build state after each task: `0 errors, N warnings` where N is specified per task.
- Expected fixture state: `bash _tests/migration/run_fixtures.sh` prints `Migration fixtures: M passed, 0 failed` where M is specified per task.
- Every commit footer includes `Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>`.

---

## Task 1: Pre-Phase-3 patch — BUG-1 invariant docs + fixture assertions + team-mixed fixture

**Files:**
- Modify: `Config/ConfigVersionMigrator.cs:313-318` (add invariant comment)
- Modify: `_tests/migration/run_fixtures.sh` (add length/content assertions for fixtures d, e, g, a + new fixture_i block)
- Create: `_tests/migration/fixture_i_team_mixed.json`

- [ ] **Step 1.1: Add invariant comment above `EnsureSize`**

Open `Config/ConfigVersionMigrator.cs`. Find the block at lines 313-318:

```csharp
            // Pad the right list up to slot index
            void EnsureSize(JsonArray arr, int targetSize)
            {
                while (arr.Count < targetSize)
                    arr.Add(new JsonObject { ["combo"] = "", ["targetName"] = "" });
            }
```

Replace with:

```csharp
            // Pad the right list up to slot index.
            //
            // INVARIANT: array index N-1 always represents v3 slot N. When only slot 2+ is
            // bound, the entries before it are empty-string placeholders (combo == "" &&
            // targetName == ""). This is semantically required because Phase 5 dispatches
            // positionally via `id - 1000 = slot index` (see PLAN_account_character_split.md
            // line 729-745). DO NOT remove the padding without also revising that contract.
            //
            // Phase 5 CONSUMER CONTRACT: when iterating AccountHotkeys/CharacterHotkeys for
            // hotkey registration, MUST skip entries where Combo or TargetName is empty —
            // those are positional placeholders, not real bindings.
            void EnsureSize(JsonArray arr, int targetSize)
            {
                while (arr.Count < targetSize)
                    arr.Add(new JsonObject { ["combo"] = "", ["targetName"] = "" });
            }
```

- [ ] **Step 1.2: Add length assertions to fixtures d, e, g in `run_fixtures.sh`**

Open `_tests/migration/run_fixtures.sh`. Find the fixture_d block around line 134:

```bash
# ── Fixture (d): QuickLogin1=Backup + AutoEnterWorld=true → CharacterHotkeys[0] ──
start_fixture "fixture_d_charname_hotkey_enterworld"
F="$(migrate "$SCRIPT_DIR/fixture_d_charname_hotkey_enterworld.json")"
assert "CharacterHotkeys[0].targetName=Backup" "$(jq -r '.hotkeys.characterHotkeys[0].targetName' "$F")" "Backup"
assert "CharacterHotkeys[0].combo=Alt+1" "$(jq -r '.hotkeys.characterHotkeys[0].combo' "$F")" "Alt+1"
assert "AccountHotkeys did not get this binding" "$(jq -r '(.hotkeys.accountHotkeys // []) | length' "$F")" "0"
end_fixture "fixture_d"
```

Add one line before `end_fixture "fixture_d"`:

```bash
assert "CharacterHotkeys has exactly 1 entry (slot 1 only bound)" "$(jq -r '.hotkeys.characterHotkeys | length' "$F")" "1"
```

Find the fixture_e block around line 141:

```bash
# ── Fixture (e): QuickLogin2=bare_user (Username only) → AccountHotkeys[1] ──
start_fixture "fixture_e_username_hotkey_charselect"
F="$(migrate "$SCRIPT_DIR/fixture_e_username_hotkey_charselect.json")"
assert "AccountHotkeys[1].targetName=BareAccount" "$(jq -r '.hotkeys.accountHotkeys[1].targetName' "$F")" "BareAccount"
assert "AccountHotkeys[1].combo=Alt+2" "$(jq -r '.hotkeys.accountHotkeys[1].combo' "$F")" "Alt+2"
assert "CharacterHotkeys empty (Account-only target)" "$(jq -r '(.hotkeys.characterHotkeys // []) | length' "$F")" "0"
end_fixture "fixture_e"
```

Add three lines before `end_fixture "fixture_e"`:

```bash
assert "AccountHotkeys has exactly 2 entries (slot 1 phantom + slot 2 real)" "$(jq -r '.hotkeys.accountHotkeys | length' "$F")" "2"
assert "AccountHotkeys[0].combo is empty (positional phantom)" "$(jq -r '.hotkeys.accountHotkeys[0].combo' "$F")" ""
assert "AccountHotkeys[0].targetName is empty (positional phantom)" "$(jq -r '.hotkeys.accountHotkeys[0].targetName' "$F")" ""
```

Find the fixture_g block around line 159:

```bash
start_fixture "fixture_g_username_target_enterworld"
F="$(migrate "$SCRIPT_DIR/fixture_g_username_target_enterworld.json")"
assert "CharacterHotkeys[0].targetName=Natechar (not 'nate')" "$(jq -r '.hotkeys.characterHotkeys[0].targetName' "$F")" "Natechar"
assert "CharacterHotkeys[0].combo=Alt+1" "$(jq -r '.hotkeys.characterHotkeys[0].combo' "$F")" "Alt+1"
assert "AccountHotkeys empty (H1 was routing here incorrectly)" "$(jq -r '(.hotkeys.accountHotkeys // []) | length' "$F")" "0"
end_fixture "fixture_g"
```

Add one line before `end_fixture "fixture_g"`:

```bash
assert "CharacterHotkeys has exactly 1 entry (slot 1 only)" "$(jq -r '.hotkeys.characterHotkeys | length' "$F")" "1"
```

- [ ] **Step 1.3: Add length assertion to fixture a**

Find the fixture_a block around line 98-110 (first fixture, one account + one character + QuickLogin1 bound to CharacterName):

Check if it already has hotkey assertions. If fixture_a's existing asserts don't cover `characterHotkeys | length`, add one line before `end_fixture "fixture_a"`:

```bash
assert "CharacterHotkeys has exactly 1 entry (slot 1 only)" "$(jq -r '(.hotkeys.characterHotkeys // []) | length' "$F")" "1"
```

(Skip this step if fixture_a has no hotkey bindings in its input — fixture_a is about Account+Character split, not hotkey migration. Inspect the fixture JSON first and decide.)

Run: `cat _tests/migration/fixture_a_one_account_one_character.json | jq '.hotkeys'`
Expected: if output is `null`, skip this assertion. If output shows `{"autoLogin1": "..."}`, add the assertion above.

- [ ] **Step 1.4: Create `fixture_i_team_mixed.json`**

Create the file at `_tests/migration/fixture_i_team_mixed.json` with these contents:

```json
{
  "configVersion": 3,
  "accounts": [
    {
      "name": "Main",
      "username": "mainuser",
      "encryptedPassword": "ZmFrZS1jaXBoZXJ0ZXh0",
      "server": "Dalaya",
      "characterName": "Healer",
      "characterSlot": 0,
      "autoEnterWorld": true,
      "useLoginFlag": true
    },
    {
      "name": "Bare",
      "username": "bareuser",
      "encryptedPassword": "ZmFrZS1jaXBoZXJ0ZXh0",
      "server": "Dalaya",
      "characterName": "",
      "characterSlot": 0,
      "autoEnterWorld": false,
      "useLoginFlag": true
    }
  ],
  "team1Account1": "Healer",
  "team1Account2": "bareuser",
  "team1AutoEnter": true,
  "team2Account1": "Ghost",
  "team2Account2": "",
  "team2AutoEnter": false
}
```

This fixture exercises:
- Team 1 Slot 1 → CharacterName `"Healer"` — should resolve to Character with name `Healer`.
- Team 1 Slot 2 → Username `"bareuser"` (no CharacterName in that v3 row) — should resolve to Account with name `Bare` (fallback).
- Team 2 Slot 1 → `"Ghost"` (matches neither Character nor Account) — should resolve to blank with warn.
- `Team1AutoEnter=true` round-trip.
- `Team2AutoEnter=false` round-trip.

- [ ] **Step 1.5: Add fixture_i block to `run_fixtures.sh`**

Open `_tests/migration/run_fixtures.sh`. After the `end_fixture "fixture_h"` line (around line 177), insert:

```bash
# ── Fixture (i): Step 3 team rebinding — mixed Character/Account/unresolved slots ──
start_fixture "fixture_i_team_mixed"
F="$(migrate "$SCRIPT_DIR/fixture_i_team_mixed.json")"
assert "Team1Account1 rebound to Character 'Healer'" "$(jq -r '.team1Account1' "$F")" "Healer"
assert "Team1Account2 rebound to Account 'Bare' (Account-fallback for no-Character row)" "$(jq -r '.team1Account2' "$F")" "Bare"
assert "Team1AutoEnter preserved as true" "$(jq -r '.team1AutoEnter' "$F")" "true"
assert "Team2Account1 blanked (unresolved target)" "$(jq -r '.team2Account1' "$F")" ""
assert "Team2Account2 stays empty" "$(jq -r '.team2Account2' "$F")" ""
assert "Team2AutoEnter preserved as false" "$(jq -r '.team2AutoEnter' "$F")" "false"
end_fixture "fixture_i"
```

- [ ] **Step 1.6: Run fixtures and verify all 9 pass**

Run: `cd X:/_Projects/EQSwitch && bash _tests/migration/run_fixtures.sh`

Expected output ends with:
```
==================================================
  Migration fixtures: 9 passed, 0 failed
==================================================
```

If any fixture fails, inspect the output above the summary, fix the assertion or the fixture JSON, and re-run.

- [ ] **Step 1.7: Commit**

```bash
cd X:/_Projects/EQSwitch && \
  git add Config/ConfigVersionMigrator.cs _tests/migration/run_fixtures.sh _tests/migration/fixture_i_team_mixed.json && \
  git commit -m "$(cat <<'EOF'
fix(migration): document EnsureSize invariant + add hotkey length assertions + team-mixed fixture

BUG-1 in ConfigVersionMigrator.EnsureSize padding: Phase 5 consumers
iterating AccountHotkeys/CharacterHotkeys must skip empty-combo
positional placeholders. Adding an invariant comment at the padding
site documents the contract; adding length + content assertions to
fixtures d/e/g pins down the emitted shape so a future refactor can't
silently change it.

GAP-3: Step 3 team rebinding (Character-preferred, Account-fallback,
unresolved-warn) had no fixture coverage. fixture_i_team_mixed covers
all three rebinding branches plus TeamN AutoEnter round-trip.

9 fixtures pass. Pre-Phase-3 foundation patch.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Model label helpers (`Account`, `Character`)

**Files:**
- Modify: `Models/Account.cs`
- Modify: `Models/Character.cs`

- [ ] **Step 2.1: Add `EffectiveLabel` and `Tooltip` to `Account`**

Open `Models/Account.cs`. Find the closing `}` of the `Account` class. Before the closing brace, add:

```csharp

    /// <summary>User-facing display label. Falls back Name → Username → literal placeholder.
    /// Never empty — WinForms menu items with empty Text are unclickable.</summary>
    public string EffectiveLabel
    {
        get
        {
            if (!string.IsNullOrEmpty(Name)) return Name;
            if (!string.IsNullOrEmpty(Username)) return Username;
            return "(unnamed account)";
        }
    }

    /// <summary>Disambiguating tooltip for the tray menu. Distinguishes Accounts that share
    /// the same display Name across servers.</summary>
    public string Tooltip => $"{Username}@{Server}";
```

- [ ] **Step 2.2: Add `EffectiveLabel` and `LabelWithClass` to `Character`**

Open `Models/Character.cs`. Find the closing `}` of the `Character` class. Before the closing brace, add:

```csharp

    /// <summary>User-facing display label. Falls back DisplayLabel → Name → literal placeholder.
    /// Never empty.</summary>
    public string EffectiveLabel
    {
        get
        {
            if (!string.IsNullOrEmpty(DisplayLabel)) return DisplayLabel;
            if (!string.IsNullOrEmpty(Name)) return Name;
            return "(unnamed character)";
        }
    }

    /// <summary>Tray-menu label with optional class hint in parentheses (e.g. "Backup (Cleric)").
    /// Single space before the paren. Falls back to EffectiveLabel when ClassHint is empty.</summary>
    public string LabelWithClass =>
        string.IsNullOrEmpty(ClassHint) ? EffectiveLabel : $"{EffectiveLabel} ({ClassHint})";
```

- [ ] **Step 2.3: Build verify**

Run: `cd X:/_Projects/EQSwitch && dotnet build 2>&1 | tail -8`

Expected: `Build succeeded. 0 Error(s)` with exactly 2 `[Obsolete]` warnings at `TrayManager.cs:817` and `:1330`. If any new warning or error, fix inline before committing.

- [ ] **Step 2.4: Commit**

```bash
cd X:/_Projects/EQSwitch && \
  git add Models/Account.cs Models/Character.cs && \
  git commit -m "$(cat <<'EOF'
feat(models): add EffectiveLabel/Tooltip/LabelWithClass label helpers

Codifies the tray-label fallback chains on the models so TrayManager,
Settings (Phase 4), and AutoLoginTeamsDialog (Phase 5) share one source
of truth. Account.EffectiveLabel falls back Name→Username→placeholder;
Character.EffectiveLabel falls back DisplayLabel→Name→placeholder;
Character.LabelWithClass adds optional "(ClassHint)" suffix.

Never-empty guarantee prevents unclickable blank ToolStripMenuItems.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: AppConfig lookup helpers

**Files:**
- Modify: `Config/AppConfig.cs`

- [ ] **Step 3.1: Add `FindAccountByName` and `FindCharacterByName`**

Open `Config/AppConfig.cs`. Find the `Validate()` method closing brace (it's the last method in the `AppConfig` class — scroll to around line 225-230).

Insert these two methods immediately before the closing brace of `Validate()`'s enclosing class (NOT inside `Validate()`):

```csharp

    /// <summary>
    /// Look up an Account by its user-facing Name. Ordinal comparison — matches v3
    /// ExecuteQuickLogin semantics at TrayManager.cs:1321-1322. Returns null if
    /// name is empty or no match found. Used by tray dispatch and Phase 5 hotkey
    /// registration.
    /// </summary>
    public Account? FindAccountByName(string name) =>
        string.IsNullOrEmpty(name) ? null : Accounts.FirstOrDefault(a => a.Name == name);

    /// <summary>
    /// Look up a Character by its in-game Name. Ordinal comparison. Returns null if
    /// name is empty or no match found.
    /// </summary>
    public Character? FindCharacterByName(string name) =>
        string.IsNullOrEmpty(name) ? null : Characters.FirstOrDefault(c => c.Name == name);
```

Verify `AppConfig.cs` has `using System.Linq;` at the top — required for `FirstOrDefault`. If not present, add it.

Run: `grep '^using System.Linq' Config/AppConfig.cs`
Expected: exactly one match.

- [ ] **Step 3.2: Build verify**

Run: `cd X:/_Projects/EQSwitch && dotnet build 2>&1 | tail -6`

Expected: `Build succeeded. 0 Error(s) 2 Warning(s)`.

- [ ] **Step 3.3: Commit**

```bash
cd X:/_Projects/EQSwitch && \
  git add Config/AppConfig.cs && \
  git commit -m "$(cat <<'EOF'
feat(config): AppConfig.FindAccountByName + FindCharacterByName

Ordinal name-keyed lookup helpers for tray dispatch and Phase 5 hotkey
registration. Ordinal matches v3 ExecuteQuickLogin semantics and keeps
AccountKey (v2.7) case-handling consistent. Returns null on empty-name
or no-match — callers balloon an error rather than crash.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: TrayManager scaffolding — new helpers alongside old code

**Files:**
- Modify: `UI/TrayManager.cs`

All new helpers land in this single commit. Old code (`BuildContextMenu`, `ExecuteTrayAction` cases, `FireTeamLogin`) is NOT touched yet — it stays functional alongside the new scaffolding. Build remains at 2 warnings.

- [ ] **Step 4.1: Add the `LegacyHotkeyLookup` nested class**

Open `UI/TrayManager.cs`. Find the end of the class — the final closing brace before the namespace closes (around the last 30 lines of the file, look for `DarkMenuRenderer` class that's already nested).

Immediately **before** the `private class DarkMenuRenderer : ToolStripProfessionalRenderer` line (around `TrayManager.cs:2001`), add the new nested class:

```csharp
    /// <summary>
    /// Phase-3-only legacy hotkey indexer. Maps `QuickLoginN` target strings to their
    /// bound `HotkeyConfig.AutoLoginN` combos so the new Accounts/Characters submenus
    /// can show the user's existing Alt+N bindings during the Phase 3 → Phase 5
    /// transition. Removed in Phase 5 when AccountHotkeys[] / CharacterHotkeys[]
    /// family tables replace the QuickLoginN pair scheme.
    /// </summary>
    private sealed class LegacyHotkeyLookup
    {
        private readonly Dictionary<string, string> _comboByTarget = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _slotByTarget = new(StringComparer.Ordinal);

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
            _comboByTarget[target] = combo;  // last binding wins on duplicate target
            _slotByTarget[target] = slot;
        }

        /// <summary>Returns the bound combo for this Account/Character Name, or "" if unbound.</summary>
        public string GetCombo(string name) =>
            !string.IsNullOrEmpty(name) && _comboByTarget.TryGetValue(name, out var c) ? c : "";

        /// <summary>Returns the QuickLoginN slot index (1-4) this target came from, or null.</summary>
        public int? GetSlot(string name) =>
            !string.IsNullOrEmpty(name) && _slotByTarget.TryGetValue(name, out var s) ? s : null;
    }
```

- [ ] **Step 4.2: Add `_legacySlotDeprecationLogged` field and `LogFirstFire` helper**

Find an existing private field declaration near the top of `TrayManager` class (e.g., `_boldMenuFont` around line 60-80). Add this field near other private state:

```csharp
    // Per-session dedup for the one-shot legacy-routing log line (plan nuance #12).
    private readonly HashSet<int> _legacySlotDeprecationLogged = new();
```

Then add `LogFirstFire` near other small helpers (e.g., near `HkSuffix`'s definition site later in the file — searchable by `string HkSuffix`):

```csharp
    /// <summary>
    /// One-shot-per-slot-per-session log line documenting where a legacy QuickLoginN hotkey
    /// routed through the new API. Surfaced on first fire of each slot so the user can audit
    /// the routing before Phase 5 replaces the QuickLoginN scheme with AccountHotkeys[]/CharacterHotkeys[].
    /// </summary>
    private void LogFirstFire(int slot, string family, string label)
    {
        if (_legacySlotDeprecationLogged.Add(slot))
        {
            FileLogger.Info($"Legacy QuickLogin{slot} routed via new API → {family} '{label}' (this mapping moves to {family}Hotkeys in Phase 5)");
        }
    }
```

- [ ] **Step 4.3: Add `FireAccountLogin` and `FireCharacterLogin`**

Find the existing `ExecuteQuickLogin` method (around `TrayManager.cs:1313`). Immediately above its method signature (before the `private Task ExecuteQuickLogin(...)` line), add:

```csharp
    /// <summary>Click handler for Accounts-submenu items. Explicit intent balloon + new API call.</summary>
    private void FireAccountLogin(Account account)
    {
        try
        {
            ShowBalloon($"Logging in {account.EffectiveLabel} \u2014 stopping at charselect");
            _ = _autoLoginManager.LoginToCharselect(account);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"FireAccountLogin CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
            ShowBalloon($"Login error: {ex.Message}");
        }
    }

    /// <summary>Click handler for Characters-submenu items. Explicit intent balloon + new API call.</summary>
    private void FireCharacterLogin(Character character)
    {
        try
        {
            ShowBalloon($"Logging in {character.EffectiveLabel} \u2014 entering world");
            _ = _autoLoginManager.LoginAndEnterWorld(character);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"FireCharacterLogin CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
            ShowBalloon($"Login error: {ex.Message}");
        }
    }
```

(`\u2014` is the em-dash character; matches CLAUDE.md "em-dash in prose" convention.)

- [ ] **Step 4.4: Add `FireLegacyQuickLoginSlot`**

Directly below the two `Fire*Login` methods from step 4.3, add:

```csharp
    /// <summary>
    /// Phase-3 dispatcher for legacy tray-click/hotkey `AutoLoginN` actions. Reads
    /// QuickLoginN, resolves Character-first then Account (matches v3 semantics +
    /// migration preference), logs the routing decision once per session, and
    /// delegates to FireAccountLogin/FireCharacterLogin which use the NEW API.
    /// Replaces ExecuteQuickLogin for non-team callers — ExecuteQuickLogin is now
    /// team-only and still routes through [Obsolete] LoginAccount until Phase 5.
    /// </summary>
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

        // Character-first resolution mirrors v3 migration preference and
        // TrayManager.cs:1321-1322 (the existing ExecuteQuickLogin two-step match).
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
```

- [ ] **Step 4.5: Add `ResolveTeamConfig`**

Find the existing `FireTeamLogin` method around `TrayManager.cs:1339`. Immediately above its signature, add:

```csharp
    /// <summary>
    /// Resolves `_config.Team{N}*` fields into the tuple shape FireTeam needs.
    /// Pure function over `_config`; called from FireTeam and from BuildTeamsSubmenu
    /// tooltip rendering.
    /// </summary>
    private (IReadOnlyList<(string user, string slotLabel)> slots, bool autoEnter, string teamName)
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

- [ ] **Step 4.6: Add `BuildCharacterTooltip` and `BuildTeamTooltip`**

Directly below `ResolveTeamConfig`, add:

```csharp
    /// <summary>
    /// Character tray-item tooltip: "→ Account 'Main' · slot auto" or fallback
    /// "username@server (unresolved)" when the FK has drifted.
    /// </summary>
    private static string BuildCharacterTooltip(
        Character character,
        IReadOnlyDictionary<AccountKey, Account> accountsByKey)
    {
        var accountLabel = accountsByKey.TryGetValue(character.AccountKey, out var acc)
            ? acc.EffectiveLabel
            : $"{character.AccountUsername}@{character.AccountServer} (unresolved)";
        var slot = character.CharacterSlot == 0 ? "auto" : character.CharacterSlot.ToString();
        return $"\u2192 Account '{accountLabel}' \u00B7 slot {slot}";
    }

    /// <summary>
    /// Team tray-item tooltip: multi-line per-slot preview of what each slot resolves to,
    /// plus "[force enter world]" hint when the team-level AutoEnter override is true.
    /// </summary>
    private string BuildTeamTooltip(int teamIndex)
    {
        var (slots, autoEnter, _) = ResolveTeamConfig(teamIndex);
        string ResolveForTooltip(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "(empty)";
            var ch = _config.FindCharacterByName(raw);
            if (ch != null) return $"{ch.LabelWithClass} \u2192 enter world";
            var acc = _config.FindAccountByName(raw);
            if (acc != null) return $"{acc.EffectiveLabel} \u2192 charselect";
            return $"{raw} (unresolved)";
        }
        var lines = new List<string>();
        for (int i = 0; i < slots.Count; i++)
            lines.Add($"Slot {i + 1}: {ResolveForTooltip(slots[i].user)}");
        if (autoEnter) lines.Add("[force enter world]");
        return string.Join("\n", lines);
    }
```

(`\u2192` is `→`, `\u00B7` is `·`.)

- [ ] **Step 4.7: Add `BuildAccountsSubmenu`**

Find `BuildContextMenu` (starts around line 775). Immediately above the `private void BuildContextMenu()` line, add:

```csharp
    /// <summary>
    /// Phase 3 Accounts submenu: 🔑 parent → 👤 per-account rows + separator + "⚙ Manage Accounts...".
    /// Each row fires LoginToCharselect on click. Empty-state row teaches what the submenu is for.
    /// Takes the list as an arg so rendering has no hidden _config reach.
    /// </summary>
    private ToolStripMenuItem BuildAccountsSubmenu(IReadOnlyList<Account> accounts, LegacyHotkeyLookup hkLookup)
    {
        var menu = new ToolStripMenuItem("\uD83D\uDD11  Accounts")
        {
            Font = _boldMenuFont,
            ToolTipText = "Login and stop at character select"
        };

        if (accounts.Count == 0)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem("No accounts yet \u2014 click Manage Accounts...")
            {
                Enabled = false
            });
        }
        else
        {
            foreach (var acc in accounts)
            {
                var captured = acc; // explicit capture for closure
                var label = $"\uD83D\uDC64  {captured.EffectiveLabel}{HkSuffix(hkLookup.GetCombo(captured.Name))}";
                var item = new ToolStripMenuItem(label) { ToolTipText = captured.Tooltip };
                item.Click += (_, _) => FireAccountLogin(captured);
                menu.DropDownItems.Add(item);
            }
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add("\u2699  Manage Accounts...", null, (_, _) => ShowSettings(2));
        return menu;
    }
```

- [ ] **Step 4.8: Add `BuildCharactersSubmenu`**

Directly below `BuildAccountsSubmenu`, add:

```csharp
    /// <summary>
    /// Phase 3 Characters submenu: 🧙 parent → 🧙 per-character rows + "⚙ Manage Characters...".
    /// Each row fires LoginAndEnterWorld on click. Tooltip resolves the backing Account label
    /// via accountsByKey lookup (falls back to username@server on FK drift).
    /// </summary>
    private ToolStripMenuItem BuildCharactersSubmenu(
        IReadOnlyList<Character> characters,
        IReadOnlyDictionary<AccountKey, Account> accountsByKey,
        LegacyHotkeyLookup hkLookup)
    {
        var menu = new ToolStripMenuItem("\uD83E\uDDD9  Characters")
        {
            Font = _boldMenuFont,
            ToolTipText = "Login and enter world"
        };

        if (characters.Count == 0)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem(
                "No characters yet \u2014 characters added here will auto-enter-world")
            {
                Enabled = false
            });
        }
        else
        {
            foreach (var ch in characters)
            {
                var captured = ch;
                var label = $"\uD83E\uDDD9  {captured.LabelWithClass}{HkSuffix(hkLookup.GetCombo(captured.Name))}";
                var tooltip = BuildCharacterTooltip(captured, accountsByKey);
                var item = new ToolStripMenuItem(label) { ToolTipText = tooltip };
                item.Click += (_, _) => FireCharacterLogin(captured);
                menu.DropDownItems.Add(item);
            }
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add("\u2699  Manage Characters...", null, (_, _) => ShowSettings(2));
        return menu;
    }
```

- [ ] **Step 4.9: Add `BuildTeamsSubmenu`**

Directly below `BuildCharactersSubmenu`, add:

```csharp
    /// <summary>
    /// Phase 3 Teams submenu: 👥 parent → 🚀 populated teams + "⚙ Manage Teams...".
    /// Teams are populated if either slot string is non-empty. Empty-state row renders
    /// when all four teams are unpopulated. Click fires ExecuteTrayAction("LoginAll[N]"),
    /// which still routes through FireTeam + ExecuteQuickLogin + [Obsolete] LoginAccount
    /// until Phase 5 rewires the team path.
    /// </summary>
    private ToolStripMenuItem BuildTeamsSubmenu(AppConfig cfg, LegacyHotkeyLookup hkLookup)
    {
        var menu = new ToolStripMenuItem("\uD83D\uDC65  Teams")
        {
            Font = _boldMenuFont,
            ToolTipText = "Launch multiple clients in parallel"
        };
        var hk = cfg.Hotkeys;
        var teams = new[]
        {
            (Num: 1, Slot1: cfg.Team1Account1, Slot2: cfg.Team1Account2, Combo: hk.TeamLogin1, Action: "LoginAll"),
            (Num: 2, Slot1: cfg.Team2Account1, Slot2: cfg.Team2Account2, Combo: hk.TeamLogin2, Action: "LoginAll2"),
            (Num: 3, Slot1: cfg.Team3Account1, Slot2: cfg.Team3Account2, Combo: hk.TeamLogin3, Action: "LoginAll3"),
            (Num: 4, Slot1: cfg.Team4Account1, Slot2: cfg.Team4Account2, Combo: hk.TeamLogin4, Action: "LoginAll4"),
        };
        var populated = teams.Where(t => !string.IsNullOrEmpty(t.Slot1) || !string.IsNullOrEmpty(t.Slot2)).ToList();

        if (populated.Count == 0)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem("No teams configured \u2014 click Manage Teams...")
            {
                Enabled = false
            });
        }
        else
        {
            foreach (var t in populated)
            {
                var action = t.Action;  // capture for closure
                var label = $"\uD83D\uDE80  Auto-Login Team {t.Num}{HkSuffix(t.Combo)}";
                var tooltip = BuildTeamTooltip(t.Num);
                var item = new ToolStripMenuItem(label) { ToolTipText = tooltip };
                item.Click += (_, _) => ExecuteTrayAction(action);
                menu.DropDownItems.Add(item);
            }
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add("\u2699  Manage Teams...", null, (_, _) => ShowSettings(2));
        return menu;
    }
```

- [ ] **Step 4.10: Build verify (scaffolding)**

Run: `cd X:/_Projects/EQSwitch && dotnet build 2>&1 | tail -8`

Expected: `Build succeeded. 0 Error(s) 2 Warning(s)` — the existing 2 `[Obsolete]` warnings at `TrayManager.cs:817` and `:1330`. No new warnings; no unused-method warnings for the new helpers (they're private and unused until Task 5 switches over, but C# doesn't warn on unused private methods).

- [ ] **Step 4.11: Commit scaffolding**

```bash
cd X:/_Projects/EQSwitch && \
  git add UI/TrayManager.cs && \
  git commit -m "$(cat <<'EOF'
refactor(tray): Phase 3 submenu + fire helper scaffolding

Adds LegacyHotkeyLookup, Fire{Account,Character,LegacyQuickLoginSlot},
Build{Accounts,Characters,Teams}Submenu, Build{Character,Team}Tooltip,
ResolveTeamConfig, and LogFirstFire alongside the existing menu code.
BuildContextMenu and ExecuteTrayAction still reference the old paths —
switchover lands in the next commit.

Helpers take their lists/configs as arguments (feature-dev:code-explorer
rec) so rendering has no hidden _config reach. Private members are
unused until the switchover; C# does not warn for unused private
methods.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: TrayManager switchover — rewrite `BuildContextMenu`, rename `FireTeamLogin` → `FireTeam`, retarget `ExecuteTrayAction`

**Files:**
- Modify: `UI/TrayManager.cs`

This commit flips the tray menu over. The `[Obsolete]` warning at `:817` disappears; the one at `:1330` stays (deliberate, per spec).

- [ ] **Step 5.1: Rename `FireTeamLogin` to `FireTeam(int teamIndex)`**

Find the existing `FireTeamLogin` method around `TrayManager.cs:1339`:

```csharp
    private void FireTeamLogin((string username, string label)[] slots, string teamName, bool teamAutoEnter = false)
    {
        int fired = 0;
        foreach (var (user, name) in slots)
        {
            if (!string.IsNullOrEmpty(user))
            {
                _ = ExecuteQuickLogin(user, name, teamAutoEnter);
                fired++;
            }
        }
        if (fired == 0)
        {
            ShowWarning($"No accounts assigned to {teamName} — configure in Settings → Accounts");
            FileLogger.Warn($"FireTeamLogin: {teamName} has no accounts assigned");
        }
    }
```

Replace with:

```csharp
    /// <summary>
    /// Fires all populated slots for the given team in parallel (fire-and-forget via
    /// discard-assignment to ExecuteQuickLogin). Preserves v3 timing semantics —
    /// DO NOT switch to sequential await (plan line 371 is emphatic).
    /// </summary>
    private void FireTeam(int teamIndex)
    {
        var (slots, teamAutoEnter, teamName) = ResolveTeamConfig(teamIndex);
        int fired = 0;
        foreach (var (user, slotLabel) in slots)
        {
            if (!string.IsNullOrEmpty(user))
            {
                _ = ExecuteQuickLogin(user, slotLabel, teamAutoEnter);  // PARALLEL — no await
                fired++;
            }
        }
        if (fired == 0)
        {
            ShowWarning($"No accounts assigned to {teamName} \u2014 configure in Settings \u2192 Accounts");
            FileLogger.Warn($"FireTeam: {teamName} has no accounts assigned");
        }
    }
```

- [ ] **Step 5.2: Update `ExecuteTrayAction` — replace `AutoLogin` + `LoginAll` cases**

Find `ExecuteTrayAction` around `TrayManager.cs:1236`. Within its switch statement, find these eight cases:

```csharp
            case "AutoLogin1":
                _ = ExecuteQuickLogin(_config.QuickLogin1, "Quick Login 1");
                break;
            case "AutoLogin2":
                _ = ExecuteQuickLogin(_config.QuickLogin2, "Quick Login 2");
                break;
            case "AutoLogin3":
                _ = ExecuteQuickLogin(_config.QuickLogin3, "Quick Login 3");
                break;
            case "AutoLogin4":
                _ = ExecuteQuickLogin(_config.QuickLogin4, "Quick Login 4");
                break;
            case "LoginAll":
                FireTeamLogin(
                    new[] { (_config.Team1Account1, "Team 1 Slot 1"), (_config.Team1Account2, "Team 1 Slot 2") },
                    "Team 1", _config.Team1AutoEnter);
                break;
            case "LoginAll2":
                FireTeamLogin(
                    new[] { (_config.Team2Account1, "Team 2 Slot 1"), (_config.Team2Account2, "Team 2 Slot 2") },
                    "Team 2", _config.Team2AutoEnter);
                break;
            case "LoginAll3":
                FireTeamLogin(
                    new[] { (_config.Team3Account1, "Team 3 Slot 1"), (_config.Team3Account2, "Team 3 Slot 2") },
                    "Team 3", _config.Team3AutoEnter);
                break;
            case "LoginAll4":
                FireTeamLogin(
                    new[] { (_config.Team4Account1, "Team 4 Slot 1"), (_config.Team4Account2, "Team 4 Slot 2") },
                    "Team 4", _config.Team4AutoEnter);
                break;
```

Replace with:

```csharp
            case "AutoLogin1": FireLegacyQuickLoginSlot(1); break;
            case "AutoLogin2": FireLegacyQuickLoginSlot(2); break;
            case "AutoLogin3": FireLegacyQuickLoginSlot(3); break;
            case "AutoLogin4": FireLegacyQuickLoginSlot(4); break;
            case "LoginAll":  FireTeam(1); break;
            case "LoginAll2": FireTeam(2); break;
            case "LoginAll3": FireTeam(3); break;
            case "LoginAll4": FireTeam(4); break;
```

- [ ] **Step 5.3: Rewrite `BuildContextMenu` body**

Find `BuildContextMenu` starting at `UI/TrayManager.cs:775`. The current body includes the combined Accounts submenu (lines 806-842). Replace ONLY the block from after the `_contextMenu.Items.Add(launchTeamItem);` line (around line 804) down to the `_contextMenu.Items.Add(new ToolStripSeparator());` line immediately before the `_clientsMenu` block (around line 844).

Find this block (lines 806-844):

```csharp
        // Login submenu (always visible, like Clients menu)
        var loginMenu = new ToolStripMenuItem("\uD83D\uDD11  Accounts") { Font = _boldMenuFont };
        if (_config.LegacyAccounts.Count > 0)
        {
            foreach (var account in _config.LegacyAccounts)
            {
                var label = string.IsNullOrEmpty(account.CharacterName)
                    ? account.Username
                    : account.CharacterName;
                loginMenu.DropDownItems.Add($"\uD83D\uDC64  {label}", null, (_, _) =>
                {
                    try { _ = _autoLoginManager.LoginAccount(account); }
                    catch (Exception ex)
                    {
                        FileLogger.Error($"AutoLogin CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
                        if (ex.InnerException != null)
                            FileLogger.Error($"AutoLogin CRASH inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}");
                    }
                });
            }
            // Team 1 omitted — "Launch Team" on root menu handles it
            var teams = new[]
            {
                (Has: !string.IsNullOrEmpty(_config.Team2Account1) || !string.IsNullOrEmpty(_config.Team2Account2), Label: "Auto-Login Team 2", Action: "LoginAll2"),
                (Has: !string.IsNullOrEmpty(_config.Team3Account1) || !string.IsNullOrEmpty(_config.Team3Account2), Label: "Auto-Login Team 3", Action: "LoginAll3"),
                (Has: !string.IsNullOrEmpty(_config.Team4Account1) || !string.IsNullOrEmpty(_config.Team4Account2), Label: "Auto-Login Team 4", Action: "LoginAll4"),
            };
            if (teams.Any(t => t.Has))
            {
                loginMenu.DropDownItems.Add(new ToolStripSeparator());
                foreach (var t in teams.Where(t => t.Has))
                    loginMenu.DropDownItems.Add($"\uD83D\uDE80  {t.Label}", null, (_, _) => ExecuteTrayAction(t.Action));
            }
            loginMenu.DropDownItems.Add(new ToolStripSeparator());
        }
        loginMenu.DropDownItems.Add("\u2699  Manage Accounts...", null, (_, _) => ShowSettings(2));
        _contextMenu.Items.Add(loginMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());
```

Replace with:

```csharp
        // Phase 3 — three intent-explicit submenus (Accounts → charselect, Characters → enter world, Teams → parallel).
        _contextMenu.Items.Add(new ToolStripSeparator());

        var hkLookup = new LegacyHotkeyLookup(_config);

        // Defensive: ToDictionary would throw on duplicate AccountKey (hand-edited
        // config corner case). Migration enforces uniqueness, but we must not crash
        // the tray on bad data — first-wins dedup + warn is correct.
        var accountsByKey = new Dictionary<AccountKey, Account>();
        foreach (var acc in _config.Accounts)
        {
            var key = new AccountKey(acc.Username, acc.Server);
            if (!accountsByKey.TryAdd(key, acc))
                FileLogger.Warn($"BuildContextMenu: duplicate AccountKey {key} in Accounts list — using first occurrence (hand-edited config?)");
        }

        _contextMenu.Items.Add(BuildAccountsSubmenu(_config.Accounts, hkLookup));
        _contextMenu.Items.Add(BuildCharactersSubmenu(_config.Characters, accountsByKey, hkLookup));
        _contextMenu.Items.Add(BuildTeamsSubmenu(_config, hkLookup));

        _contextMenu.Items.Add(new ToolStripSeparator());
```

Note: the new code adds a separator **before** the three submenus (matches the spec mockup), whereas the old code had no pre-submenu separator. This is intentional.

- [ ] **Step 5.4: Add tooltip to root `Launch Client` and `Launch Team` buttons**

Still within `BuildContextMenu`, find the two root-button declarations:

```csharp
        var launchOneItem = new ToolStripMenuItem($"\u2694  Launch Client{HkSuffix(hk.LaunchOne)}") { Font = _boldMenuFont };
        launchOneItem.Click += (_, _) => OnLaunchOne();
        _contextMenu.Items.Add(launchOneItem);

        var launchTeamItem = new ToolStripMenuItem($"\uD83C\uDFAE  Launch Team{HkSuffix(hk.TeamLogin1)}") { Font = _boldMenuFont };
        launchTeamItem.Click += (_, _) => ExecuteTrayAction("LoginAll");
        _contextMenu.Items.Add(launchTeamItem);
```

Replace with:

```csharp
        var launchOneItem = new ToolStripMenuItem($"\u2694  Launch Client{HkSuffix(hk.LaunchOne)}")
        {
            Font = _boldMenuFont,
            ToolTipText = "Launch bare eqgame.exe patchme"
        };
        launchOneItem.Click += (_, _) => OnLaunchOne();
        _contextMenu.Items.Add(launchOneItem);

        var launchTeamItem = new ToolStripMenuItem($"\uD83C\uDFAE  Launch Team{HkSuffix(hk.TeamLogin1)}")
        {
            Font = _boldMenuFont,
            ToolTipText = "Launch Team 1 (one-click default)"
        };
        launchTeamItem.Click += (_, _) => ExecuteTrayAction("LoginAll");
        _contextMenu.Items.Add(launchTeamItem);
```

- [ ] **Step 5.5: Build verify — warning count drops 2→1**

Run: `cd X:/_Projects/EQSwitch && dotnet build 2>&1 | tail -10`

Expected:
- `Build succeeded.`
- `0 Error(s)`
- `1 Warning(s)`
- The remaining warning is at `UI/TrayManager.cs:1330` on `ExecuteQuickLogin`'s call to `LoginAccount` — this is intentional and stays until Phase 5.

If warning count ≠ 1:
- More than 1: some call site was missed. Grep `grep -n "LoginAccount(" UI/TrayManager.cs` — should show exactly one call on line ~1330. Find and fix any other direct calls.
- Zero: the `:1330` path unexpectedly resolved. Re-check `ExecuteQuickLogin` wasn't accidentally deleted or retargeted.

- [ ] **Step 5.6: Run migration fixtures — still 9 green**

Run: `cd X:/_Projects/EQSwitch && bash _tests/migration/run_fixtures.sh`

Expected: `Migration fixtures: 9 passed, 0 failed`.

- [ ] **Step 5.7: Commit switchover**

```bash
cd X:/_Projects/EQSwitch && \
  git add UI/TrayManager.cs && \
  git commit -m "$(cat <<'EOF'
feat(tray): Phase 3 three-submenu rebuild — Accounts/Characters/Teams

Replaces the combined-Accounts-and-Teams submenu with three intent-
explicit submenus backed by the v4 data model. Account rows fire
LoginToCharselect; Character rows fire LoginAndEnterWorld; Team rows
fire through FireTeam (renamed from FireTeamLogin — same semantics,
same parallel fire-and-forget).

ExecuteTrayAction AutoLogin1-4 cases now route via FireLegacyQuickLoginSlot
(new API, Character-first resolve). LoginAll[N] cases route via FireTeam.
Root Launch Client and Launch Team get informative tooltips.

Warning delta: [Obsolete] at TrayManager.cs:817 eliminated (direct
LoginAccount call removed). :1330 warning stays until Phase 5 replaces
the team path.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Parallel agent review fanout

Pre-implementation state: Tasks 1-5 shipped locally, 1 `[Obsolete]` warning, 9 fixtures green.

- [ ] **Step 6.1: Dispatch three review agents in parallel**

Send these three as a single message with three Agent tool invocations (parallel). Each prompt is self-contained.

Agent A — `pr-review-toolkit:code-reviewer`:

> Review the EQSwitch Phase 3 tray-menu rebuild against CLAUDE.md conventions and general correctness.
>
> Repository: X:/_Projects/EQSwitch, branch main. Spec at `docs/superpowers/specs/2026-04-14-eqswitch-phase3-tray-rebuild-design.md`. Plan at `docs/superpowers/plans/2026-04-15-eqswitch-phase3-tray-rebuild.md`.
>
> Focus files (the diff since commit `24510ab`):
> - Models/Account.cs, Models/Character.cs
> - Config/AppConfig.cs
> - Config/ConfigVersionMigrator.cs (comment only)
> - UI/TrayManager.cs (the bulk)
> - _tests/migration/run_fixtures.sh + fixture_i_team_mixed.json
>
> Look for: DarkTheme factories used vs raw Color.FromArgb (should be zero raw colors outside DarkTheme.cs), no new DllImport outside NativeMethods.cs, conventional commits already landed, no emoji in code/comments/commits (tray-menu emoji strings are explicitly allowed per plan line 211), `using var` on Process objects, correct HkSuffix usage, correct emoji surrogate pair escapes.
>
> Run `dotnet build 2>&1 | tail -10` and verify exactly 1 [Obsolete] warning at TrayManager.cs:1330. Run `bash _tests/migration/run_fixtures.sh` and verify 9/9 pass.
>
> Return confidence-filtered findings (high-priority only) as BUGS / RISKS / NITPICKS with file:line citations. Under 500 words.

Agent B — `pr-review-toolkit:silent-failure-hunter`:

> Audit the EQSwitch Phase 3 tray-menu rebuild for silent failures, inadequate error handling, and inappropriate fallback behavior.
>
> Repository: X:/_Projects/EQSwitch, branch main. Diff since `24510ab`.
>
> Focus on: tray click handlers (FireAccountLogin, FireCharacterLogin, FireLegacyQuickLoginSlot, FireTeam), empty-state rendering edges, the LegacyHotkeyLookup dedup-on-duplicate-target behavior, BuildCharacterTooltip FK-drift fallback to "(unresolved)", BuildTeamTooltip rendering on a fully-empty team, the BuildContextMenu rebuild lifecycle and Dispose cascade (plan nuance #8), and the three `ShowSettings(2)` Manage-X entries.
>
> Flag: any try/catch that swallows without logging, any `if (null) return;` that should balloon the user, any enum/switch with a silent default, any `?? ""` that masks data corruption, any task discarded with `_ =` where the exception would be unobservable.
>
> Verify `_activeLoginPids` drain is not affected (AutoLoginManager unchanged but trace through the new call paths).
>
> Return findings as CRITICAL / HIGH / MEDIUM with reasoning. Under 500 words.

Agent C — `feature-dev:code-reviewer`:

> Independent second-opinion review of the EQSwitch Phase 3 tray-menu rebuild, focused on the tray surface.
>
> Repository: X:/_Projects/EQSwitch, branch main. Diff since `24510ab`. Spec and plan at the paths in Agent A's prompt.
>
> Focus on: parallel fire-and-forget preservation in FireTeam (plan line 371: sequential await would re-expose races — verify no `await` inside the foreach), phantom-click defenses intact (grep "gameState == 5" Native/mq2_bridge.cpp → 2 matches; grep "result == -2" Core/AutoLoginManager.cs → at least 1 match), the LegacyHotkeyLookup last-binding-wins semantics (if v3 had two slots pointing at the same target, which wins? Is that sensible?), menu-item disposal under rebuild (ContextMenuStrip.Dispose cascade assumption), and the BuildCharactersSubmenu accountsByKey dict — is it safe if two accounts share an AccountKey (shouldn't happen, but verify no crash).
>
> Also check: does any new code path break the config reload lifecycle at TrayManager.ReloadConfig? Does the new ExecuteTrayAction routing preserve every semantic the old one had (including the balloon-per-click behavior)?
>
> Return HIGH / MEDIUM findings with concrete fixes proposed. Under 500 words.

Wait for all three to complete before proceeding.

- [ ] **Step 6.2: Triage findings**

Read the three reports. Group findings by severity:
- CRITICAL/HIGH → fix before publish (Task 7 landing).
- MEDIUM/risk → decide case-by-case. Either fix or add to the handoff's deferred-after-Phase-3 block.
- NITPICK/LOW → ignore or batch at end of Phase 3.

If findings contradict each other (e.g., one says "add try/catch", another says "let it throw"), reconcile using the spec as tiebreaker.

---

## Task 7: Fold agent findings (conditional)

**Files:** variable depending on findings — likely `UI/TrayManager.cs`.

- [ ] **Step 7.1: Fix each HIGH/CRITICAL finding**

For each finding:
- Make the smallest possible edit.
- Verify `dotnet build` still shows 1 warning.
- Verify `bash _tests/migration/run_fixtures.sh` still green (9/9).

- [ ] **Step 7.2: Commit fixes (one or more commits)**

Each commit should address one coherent finding group. Title pattern: `fix(tray): <what>` with finding-source in body (e.g., "from pr-review-toolkit:silent-failure-hunter audit").

Skip Task 7 entirely if all three reviews come back clean.

---

## Task 8: Publish + deploy

**Files:** none modified; deploys `bin/Release/...` to `C:/Users/nate/proggy/Everquest/EQSwitch/`.

- [ ] **Step 8.1: Clean prior Release output**

Run: `cd X:/_Projects/EQSwitch && rm -rf bin/Release/`

Expected: command completes without error (directory may not exist yet — `rm -rf` tolerates missing).

- [ ] **Step 8.2: Publish single-file self-contained exe**

Run: `cd X:/_Projects/EQSwitch && dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true 2>&1 | tail -8`

Expected: `Build succeeded. 0 Error(s) 1 Warning(s)` with `EQSwitch.exe` (~155 MB) at `bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe`.

- [ ] **Step 8.3: Deploy to Nate's EQ folder**

Run:
```bash
cd X:/_Projects/EQSwitch && \
  cp bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe \
     bin/Release/net8.0-windows/win-x64/publish/eqswitch-hook.dll \
     bin/Release/net8.0-windows/win-x64/publish/eqswitch-di8.dll \
     C:/Users/nate/proggy/Everquest/EQSwitch/
```

Verify the copy succeeded:
```bash
ls -la C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe \
       C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-hook.dll \
       C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-di8.dll
```

Expected: three files, mtimes within the last minute.

- [ ] **Step 8.4: Phantom-click non-regression grep**

Run these three greps — all must match (per spec verification plan):
```bash
grep -c "gameState == 5" X:/_Projects/EQSwitch/Native/mq2_bridge.cpp
# Expected: 2

grep -c "result == -2" X:/_Projects/EQSwitch/Core/AutoLoginManager.cs
# Expected: >= 1
```

If either mismatches, STOP — phantom-click defense has regressed. Investigate before letting Nate run the smoke test.

---

## Task 9: Nate smoke test + memory file update + sign-off gate

**Files:** `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v3_10_0_account_split.md`.

- [ ] **Step 9.1: Tell Nate to run the smoke test**

Paste this to Nate:

> Phase 3 deployed. Please:
>
> 1. Right-click tray icon. Confirm three submenus visible: 🔑 Accounts, 🧙 Characters, 👥 Teams — each with tooltip on hover.
> 2. Click `Accounts ▸ natedogg` (or any Account row). Expect balloon `Logging in natedogg — stopping at charselect` and EQ stops at the character select screen.
> 3. Close EQ. Click `Characters ▸ backup`. Expect balloon `Logging in backup — entering world` and EQ enters world as backup.
> 4. Close EQ. Click `Teams ▸ Auto-Login Team 1`. Expect both slots launch in parallel.
> 5. Close EQ. Press your existing Alt+1 hotkey. Expect the same routing as the matching submenu click, plus a one-shot INFO log line in `eqswitch.log`: `Legacy QuickLogin1 routed via new API → Character/Account 'X'`.
> 6. Close EQ. Run: `tail -20 C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-dinput8.log | grep -A2 "gameState -> 5"` — expect empty output (no phantom click).
>
> Report any unexpected balloon, missing submenu, or phantom-click log entries.

- [ ] **Step 9.2: Wait for Nate's signoff**

Do NOT proceed to step 9.3 until Nate reports the smoke test passes.

If Nate reports issues, create a new commit series:
1. Reproduce each issue.
2. Fix with smallest edit.
3. `dotnet build`, `run_fixtures.sh`, re-publish, re-deploy.
4. Ask Nate to retest.
5. Loop until green.

- [ ] **Step 9.3: Update the memory file**

Open `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v3_10_0_account_split.md` and append a status line for Phase 3:

```markdown
- **Phase 3 shipped 2026-04-15** — three-submenu tray rebuild (Accounts/Characters/Teams), [Obsolete] warning dropped 2→1, 9 migration fixtures, teaching-forward empty-state copy. Deployed to C:/Users/nate/proggy/Everquest/EQSwitch/ and smoke-tested by Nate. Phase 4 (Settings dual-section UI) deferred for explicit signoff.
```

No commit needed for the memory file — it lives outside the repo.

- [ ] **Step 9.4: Push to origin**

Run:
```bash
cd X:/_Projects/EQSwitch && git log origin/main..HEAD --oneline
```

Expected: 4-7 commits (Task 1 + Task 2 + Task 3 + Task 4 + Task 5 + optional Task 7 fixups).

Show Nate the list. If approved, push:
```bash
cd X:/_Projects/EQSwitch && git push origin main
```

- [ ] **Step 9.5: STOP for Phase 4 sign-off**

Do not begin Phase 4 (SettingsForm dual-section UI) without explicit user approval per handoff "Required workflow" step 8.

---

## Self-review notes

- **Spec coverage:** every spec section maps to a task. Pre-Phase-3 patch = Task 1. Model additions = Task 2. AppConfig helpers = Task 3. TrayManager scaffolding = Task 4. Switchover (BuildContextMenu rewrite + rename + ExecuteTrayAction updates) = Task 5. Agent fanout = Task 6. Findings fold = Task 7. Publish + deploy = Task 8. Smoke test + memory + sign-off = Task 9.
- **Type consistency:** `FireAccountLogin(Account)`, `FireCharacterLogin(Character)`, `FireLegacyQuickLoginSlot(int)`, `FireTeam(int)` — names consistent across all task references. `LegacyHotkeyLookup.GetCombo(string)` and `GetSlot(string)` — consistent. `BuildCharacterTooltip` is static (takes dict as arg), `BuildTeamTooltip` is instance (reads `_config`). `ResolveTeamConfig` returns `(IReadOnlyList<(string user, string slotLabel)>, bool, string)` and is consumed identically in FireTeam and BuildTeamTooltip.
- **Commit count target:** 5 mandatory commits (Tasks 1, 2, 3, 4, 5) + 0-3 optional (Task 7 fixups).
- **No placeholders:** every code block is literal and complete. Every `grep` command and expected output is stated. No "handle edge cases" or "similar to above" shortcuts.
