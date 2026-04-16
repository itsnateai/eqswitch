# New-Session Handoff — EQSwitch v3.10.0 Phase 5b (consumer migration refactor)

Paste the prompt below into a fresh session. Assumes nothing from prior sessions.

---

## Prompt

You are continuing work on EQSwitch, a C#/.NET 8 WinForms multiboxing tray app for EverQuest (Shards of Dalaya). Repo: `X:/_Projects/EQSwitch/`. Read `CLAUDE.md` first for architecture + conventions — it's authoritative.

**The standard:** Nate's words, verbatim: *"Every feature you build, every bug you fix, every audit you run is a showcase of the absolute best work we can produce. Every UI element, every interaction, every implementation detail, every verification should reflect top-tier craftsmanship. Don't stop at 'good enough.'"* That's the bar.

**Your task:** Phase 5b — mechanical consumer migration. Three independent refactors, ~3-4 atomic commits, minimal UX surface. Ship, 3-agent review, publish, smoke, STOP for Phase 6 sign-off.

**Required reading before coding:**
- `X:/_Projects/EQSwitch/CLAUDE.md` — architecture
- `X:/_Projects/EQSwitch/PLAN_account_character_split.md` — master plan (focus "Phase 5 — Hotkey families + Team rebinding" section + "Deferred Phase 5" block)
- `X:/_Projects/EQSwitch/PLAN_account_character_split_HANDOFF_phase4.md` — Phase 4 handoff (carries forward hard rules)
- `X:/_Projects/EQSwitch/docs/superpowers/specs/2026-04-15-eqswitch-phase5a-hotkey-families-design.md` — Phase 5a design (just-shipped context)
- This handoff — your primary briefing.

---

### Current state (committed + pushed to `itsnateai/eqswitch:main`, HEAD `aa1b6ae`)

**42 impl commits on main. Phase 5a fully shipped + deployed.**

| Range | Phase |
|---|---|
| `ca66aee..4e05007` | pre-Phase-1 groundwork |
| `65b8b34..fde26a7` | Phases 1-3 (v4 migration + tray rebuild) |
| `2ff4762..01d05ed` | Phase 3.5 + Phase 4 (hotkeys polish + Settings dual-section UI) |
| `8263164..aa1b6ae` | Phase 5a (hotkey family tables + Hotkeys-tab redesign + Alt+M gate removal) |

**Build:** 0 errors, **1 expected** `[Obsolete]` warning at `TrayManager.cs:~1726` (`ExecuteQuickLogin → LoginAccount`). Phase 6 deletes it.

**9 migration fixtures pass:** `bash _tests/migration/run_fixtures.sh`.

**Phantom-click defenses intact (re-grep after any change):**
- `grep -c "gameState == 5" Native/mq2_bridge.cpp` → 2
- `grep -c "result == -2" Core/AutoLoginManager.cs` → 1

**Live deploy at `C:/Users/nate/proggy/Everquest/EQSwitch/`** byte-identical to `bin/Release/net8.0-windows/win-x64/publish/`.

---

### Phase 5b scope (3-4 atomic commits)

Three independent refactors. Order within the phase doesn't matter — each is self-contained. Suggest committing 1→2→3; each commit re-verifies gates + fixtures.

**5b-1 — `WindowManager.cs:437-442` legacy read → v4 lookup**

Current code in `SetWindowTitle`:
```csharp
// Look up by slot index in auto-login accounts
if (slotIndex < _config.LegacyAccounts.Count)
{
    var accountName = _config.LegacyAccounts[slotIndex].CharacterName;
    if (!string.IsNullOrEmpty(accountName))
        charName = accountName;
}
```

The method resolves a character name for the `{CHAR}` placeholder in window-title templates. `LegacyAccounts[slotIndex].CharacterName` is the v3 pattern; v4's source-of-truth is `_config.Characters[slotIndex].Name` (position-ordered) OR a more explicit lookup via `_config.QuickLoginN` (which carries the intended target name per slot).

**Design decision:** use `_config.QuickLogin{slotIndex+1}` (for slotIndex 0..3) as the binding → resolve via `_config.FindCharacterByName` → Character.Name. Falls back to the native window title on miss. Preserves the slot→character mapping behavior from v3 while moving the data source to v4.

Alternative: iterate `_config.Characters` by position. Less intent-explicit since Characters list isn't inherently slot-ordered.

**File touched:** `Core/WindowManager.cs` only. ~8 lines changed.

**5b-2 — `AffinityManager.cs:131-141` `LegacyCharacterProfiles` → `CharacterAliases`**

Current code in `FindSlotPriorityOverride`:
```csharp
private string? FindSlotPriorityOverride(int slotIndex)
{
    // Reads legacy CharacterProfile data — Phase 5 will swap to CharacterAliases.
    var characters = _config.LegacyCharacterProfiles;
    for (int i = 0; i < characters.Count; i++)
    {
        if (characters[i].SlotIndex == slotIndex && characters[i].PriorityOverride != null)
            return characters[i].PriorityOverride;
    }
    return null;
}
```

`CharacterProfile` (legacy) and `CharacterAlias` (v4) are the same shape — same `SlotIndex` + `PriorityOverride` fields. Pure mechanical rename. Verify before editing:

```bash
grep -n "SlotIndex\|PriorityOverride" Models/CharacterAlias.cs Models/CharacterProfile.cs
```

If `CharacterAlias.cs` has the same property names, the swap is `LegacyCharacterProfiles` → `CharacterAliases` and delete the comment. If names drift, adjust accordingly.

**File touched:** `Core/AffinityManager.cs` only. 1-2 lines changed.

**5b-3 — Extract `Core/CharacterSelector.Decide()` pure function**

Master plan line 380-382: *"Extract the character-selection block from `RunLoginSequence` into `Core/CharacterSelector.cs` pure function. Surface: `Decide(int requestedSlot, string requestedName, string[] charNamesInHeap) → (int, bool, string)`. 4 test cases per plan."*

Current site: `AutoLoginManager.RunLoginSequence` around line 471 (search anchor `RequestSelectionByName`). The block decides whether to select by slot index or by name against the heap-scanned character list.

**Proposed extraction:**

```csharp
// Core/CharacterSelector.cs (NEW)
namespace EQSwitch.Core;

public static class CharacterSelector
{
    /// <summary>
    /// Decide which character slot to select during auto-login.
    ///
    /// Inputs:
    ///   requestedSlot: 0 = auto-by-name, 1-10 = explicit slot.
    ///   requestedName: character name to match when requestedSlot == 0.
    ///   charNamesInHeap: MQ2-scanned character-list order (null = not yet ready).
    ///
    /// Output (resolvedSlot, resolvedByName, decisionLog):
    ///   resolvedSlot: 1-10 slot index to click, or 0 if no decision possible.
    ///   resolvedByName: true if heap-scan matched, false if fell back to requestedSlot.
    ///   decisionLog: one-line summary for FileLogger.
    /// </summary>
    public static (int resolvedSlot, bool resolvedByName, string decisionLog) Decide(
        int requestedSlot, string requestedName, string[] charNamesInHeap)
    {
        // Case 1: heap not ready → use requested slot as-is (user's explicit intent).
        if (charNamesInHeap == null || charNamesInHeap.Length == 0)
            return (requestedSlot, false, $"heap empty, fall back to requested slot {requestedSlot}");

        // Case 2: auto-by-name with a target → scan heap for match.
        if (requestedSlot == 0 && !string.IsNullOrEmpty(requestedName))
        {
            for (int i = 0; i < charNamesInHeap.Length; i++)
            {
                if (charNamesInHeap[i].Equals(requestedName, StringComparison.OrdinalIgnoreCase))
                    return (i + 1, true, $"name match '{requestedName}' at slot {i + 1}");
            }
            return (0, false, $"name '{requestedName}' not in heap ({string.Join(",", charNamesInHeap)})");
        }

        // Case 3: explicit slot requested → use it.
        if (requestedSlot >= 1 && requestedSlot <= 10)
            return (requestedSlot, false, $"explicit slot {requestedSlot}");

        // Case 4: malformed (requestedSlot=0 + empty name) → caller must handle.
        return (0, false, "no slot or name requested");
    }
}
```

Then `AutoLoginManager.RunLoginSequence` calls `CharacterSelector.Decide(...)` and routes on the result.

**4 test cases** (add to a new `Core/CharacterSelectorTests.cs` using simple asserts in a console main, OR extend the migration-fixtures bash harness):
1. `Decide(0, "backup", ["Foo", "backup", "bar"])` → `(2, true, ...)`.
2. `Decide(0, "zzz", ["Foo", "backup"])` → `(0, false, "name 'zzz' not in heap ...")`.
3. `Decide(3, "", [])` → `(3, false, "heap empty ...")`.
4. `Decide(3, "backup", ["Foo", "backup"])` → `(3, false, "explicit slot 3")`.

**Files touched:** `Core/CharacterSelector.cs` (NEW, ~60 lines), `Core/AutoLoginManager.cs` (~30 lines changed around the extraction site), optionally `_tests/CharacterSelectorTests.cs`.

---

### Phase 5b verification gate

- `dotnet build --no-incremental` → 0 errors, 1 `[Obsolete]` warning.
- `bash _tests/migration/run_fixtures.sh` → 9 passed, 0 failed.
- Phantom-click gates unchanged (2 / 1).
- Live smoke test: launch from tray → window title renders `{CHAR}` correctly → per-slot priority override still fires → character selection via heap-name-match still works.
- CharacterSelector tests pass (4 cases).

---

### Hard rules (carry forward)

- **Never break v3 config of an existing user.** Always back up before migration.
- **Do not regress phantom-click defenses.** `gameState==5` at `Native/mq2_bridge.cpp:1103/1141`. `result==-2` at `Core/AutoLoginManager.cs`.
- **Stage specific files, never `git add -A`.** Conventional commits, titles under 72 chars.
- **No emojis in code/comments/commits** except the existing tray-menu surrogate-pair escapes.
- **`StringComparison.Ordinal` for names** (matches `AccountKey.Matches` + `FindAccountByName`/`FindCharacterByName`).
- **Native side: unchanged.** If you edit `mq2_bridge.cpp`, STOP and re-read the plan.
- **Memory file:** append a Phase 5b status line to `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v3_10_0_account_split.md` when the phase closes.

---

### Required workflow

1. Read plan + this handoff.
2. Invoke `superpowers:brainstorming` — quick since mechanical (3-4 sentences each, most decisions are pre-made).
3. Invoke `superpowers:writing-plans` — granular task breakdown.
4. Atomic commits. 3 refactors, each self-contained.
5. **Dispatch parallel review agents:** `pr-review-toolkit:code-reviewer`, `pr-review-toolkit:silent-failure-hunter`, `feature-dev:code-reviewer`. Fold findings before publish.
6. `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` → deploy to `C:/Users/nate/proggy/Everquest/EQSwitch/`. Kill running `EQSwitch.exe` first.
7. Nate-driven smoke test.
8. **STOP after Phase 5b verification gate passes.** Do not start Phase 6 (v3.11.0 cleanup) without explicit sign-off.

---

### Current environment state (do not re-derive)

- **EQSwitch.exe** at `C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe` matches `aa1b6ae`'s publish output.
- **Live config** at `C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-config.json`:
  - `configVersion: 4`
  - `accountsV4` + `charactersV4` populated (Phase 4 edits)
  - `hotkeys.accountHotkeys` + `hotkeys.characterHotkeys` populated (Phase 5a)
  - `hotkeys.togglePip` populated (Phase 3.5)
  - `hotkeysLegacyBannerDismissed` reflects Nate's dismiss state
- **Backups preserved:** `eqswitch-config.json.v3-backup` + Phase 2 variants.

---

### Deferred for Phase 6 / v3.11.0

Don't rediscover these; fold at the right phase.

- Delete `AppConfig.LegacyAccounts` + `AppConfig.LegacyCharacterProfiles` fields.
- Delete `Models/LoginAccount.cs` + `CharacterProfile` class.
- Delete `Config/LoginAccountSplitter.cs`.
- Delete `[Obsolete] AutoLoginManager.LoginAccount(LoginAccount, bool?)` wrapper (last `[Obsolete]` warning goes with it).
- Delete `TrayManager.ExecuteQuickLogin` + `FireLegacyQuickLoginSlot`.
- Delete `AppConfig.QuickLogin1-4` + `HotkeyConfig.AutoLogin1-4`.
- Delete `HotkeyConfig.MultiMonitorEnabled` (no longer consulted at runtime after Phase 5a).
- Delete `AppConfig.HotkeysLegacyBannerDismissed` (legacy banner itself goes away).
- Delete `SettingsForm._pendingAccounts ReverseMapToLegacy` helper.
- Delete the `AppConfig.Validate()` v4-resync defense-in-depth block (Phase 2.6).
- Implement `MigrateV4ToV5(JsonObject)` — rename `accountsV4` → `accounts`, `charactersV4` → `characters`, drop the v3 `accounts`/`characters` keys. Bump `CurrentConfigVersion = 5`.
- Bump `EQSwitch.csproj` version to `3.11.0`. Tag release. Update `README.md` with v4 architecture screenshots.
- Document the add-then-rename migration pattern in `_templates/references/migration-framework.md`.

---

**Bar one more time:** *Every UI element, every interaction, every implementation detail, every verification should reflect top-tier craftsmanship. Don't stop at "good enough."*

Phase 5b closes the loop on the Account/Character split migration. Phase 6 is pure cleanup after Phase 5b smoke-tests. The user-visible surface is frozen — you're cleaning up the last v3 legacy reads from the runtime so v3.11.0 ships a clean v4-only data model.
