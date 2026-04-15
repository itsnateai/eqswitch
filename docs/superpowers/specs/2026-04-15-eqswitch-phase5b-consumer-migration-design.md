# EQSwitch Phase 5b — Consumer Migration Refactor

**Status:** Approved design, ready for implementation-plan drafting
**Created:** 2026-04-15
**Parent plan:** [`PLAN_account_character_split.md`](../../../PLAN_account_character_split.md)
**Prior phase:** Phase 5a shipped — HEAD `aa1b6ae`, 42 commits on main. Phase 5a smoke test passed.
**Handoff:** [`PLAN_account_character_split_HANDOFF_phase5b.md`](../../../PLAN_account_character_split_HANDOFF_phase5b.md)
**Prior spec:** [`2026-04-15-eqswitch-phase5a-hotkey-families-design.md`](2026-04-15-eqswitch-phase5a-hotkey-families-design.md)

## Purpose

Phase 5b closes the runtime-consumer side of the v3 → v4 Account/Character split migration. The v4 data model (`AccountsV4`, `CharactersV4`, `CharacterAliases`) has been populated since Phase 1 and is now the source-of-truth for Settings, tray menus, and hotkey dispatch, but three code sites still read from the v3 `LegacyAccounts` / `LegacyCharacterProfiles` lists at runtime. Phase 5b swaps those reads to v4 and extracts one pure selection helper so Phase 6 (v3.11.0) can delete the legacy fields without runtime fallout.

This is a mechanical phase. No user-visible UX changes. No native-side edits. No new config keys.

## Goals

- `WindowManager.SetWindowTitle` resolves `{CHAR}` placeholders through the v4 `Characters` list instead of `LegacyAccounts`.
- `AffinityManager.FindSlotPriorityOverride` reads `CharacterAliases` instead of `LegacyCharacterProfiles`.
- `AutoLoginManager.RunLoginSequence` delegates the "which slot do I click?" decision to a pure `CharacterSelector.Decide()` helper with 4 unit tests that run from the existing `--test-*` CLI harness.
- Phantom-click defenses stay exactly as they are (`gameState == 5` ×2, `result == -2` ×1).
- 9 migration fixtures keep passing.
- The single expected `[Obsolete]` warning at `TrayManager.cs:~1726` is the only warning in the build output.

## Non-goals (deferred to Phase 6 / v3.11.0)

- Deleting `AppConfig.LegacyAccounts`, `AppConfig.LegacyCharacterProfiles`, `AppConfig.QuickLogin1-4`, `HotkeyConfig.AutoLogin1-4`.
- Deleting `Models/LoginAccount.cs`, `CharacterProfile`, `Config/LoginAccountSplitter.cs`.
- Deleting `[Obsolete] AutoLoginManager.LoginAccount(LoginAccount, bool?)` wrapper and the `TrayManager.ExecuteQuickLogin` / `FireLegacyQuickLoginSlot` helpers.
- Replacing `QuickLogin{slotIndex+1}` as the slot → character indirection for window titles (the indirection stays v3-shaped; only the resolved data source shifts to v4). Phase 6 will rewire the indirection itself when `QuickLogin1-4` is deleted.
- `MigrateV4ToV5` JSON key rename (`accountsV4` → `accounts`, `charactersV4` → `characters`).

## Phase 5b design

### 1. `WindowManager.SetWindowTitle` → v4 lookup

**File:** `Core/WindowManager.cs:427-475` (the `SetWindowTitle` method). The legacy read is lines 437-442.

**Today:**
```csharp
// Look up by slot index in auto-login accounts
if (slotIndex < _config.LegacyAccounts.Count)
{
    var accountName = _config.LegacyAccounts[slotIndex].CharacterName;
    if (!string.IsNullOrEmpty(accountName))
        charName = accountName;
}
```

`LegacyAccounts[slotIndex].CharacterName` is the v3 pattern: each `LoginAccount` row held both credentials and a `CharacterName` string, and runtime consumers indexed it by slot order.

**Phase 5b:**
```csharp
// Phase 5b: resolve {CHAR} through the v4 Characters list via the slot->name
// binding carried in QuickLogin{N}. The QuickLogin{N} indirection itself is
// Phase 6-deletion-slated; only the resolved-name data source moves to v4 here.
string? boundName = slotIndex switch
{
    0 => _config.QuickLogin1,
    1 => _config.QuickLogin2,
    2 => _config.QuickLogin3,
    3 => _config.QuickLogin4,
    _ => null
};
if (!string.IsNullOrEmpty(boundName))
{
    var character = _config.FindCharacterByName(boundName);
    if (character != null && !string.IsNullOrEmpty(character.Name))
        charName = character.Name;
}
```

Behavior is identical for every Phase-4-migrated config: `MigrateV3ToV4` seeded `QuickLogin{N}` with the character name that v3's `LegacyAccounts[slotIndex].CharacterName` held, and `FindCharacterByName` resolves that name against the v4 Characters list the same migration built. The fall-through to the EQ native window title (lines 444-458) is untouched.

**Why route through `FindCharacterByName` instead of returning `boundName` directly?**
Two reasons. First, it verifies the v4 catalog still contains the bound character — if a user renamed or deleted a Character after the bind was saved, we fall back to the native EQ title rather than showing a stale name. Second, it forces the `{CHAR}` renderer to read from the v4 source-of-truth, which is the whole point of the phase.

**Lines changed:** ~8 insertions, 6 deletions in one method.

### 2. `AffinityManager.FindSlotPriorityOverride` → `CharacterAliases`

**File:** `Core/AffinityManager.cs:131-141`.

**Today:**
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

**Phase 5b:**
```csharp
private string? FindSlotPriorityOverride(int slotIndex)
{
    var aliases = _config.CharacterAliases;
    for (int i = 0; i < aliases.Count; i++)
    {
        if (aliases[i].SlotIndex == slotIndex && aliases[i].PriorityOverride != null)
            return aliases[i].PriorityOverride;
    }
    return null;
}
```

`CharacterAlias` (`Models/CharacterAlias.cs`) carries identical `SlotIndex: int` and `PriorityOverride: string?` fields to the legacy `CharacterProfile`. The Phase 1 migration already populated `CharacterAliases` from `LegacyCharacterProfiles` and both lists stay in sync via Settings saves until Phase 6 drops the legacy field.

**Lines changed:** 2.

### 3. Extract `Core/CharacterSelector.Decide()`

**Site:** `Core/AutoLoginManager.cs:441-508` inside `RunLoginSequence`. The selection block today:

- Reads `character.CharacterSlot` and `character.Name` off the target `Character`.
- Reads the heap-scanned name list via `charSelect.ReadAllCharNames(pid)`.
- Branches into (a) slot-based with `charCount` bounds check, (b) name-based via the native `RequestSelectionByName`, (c) abort-on-ambiguity guard.

`RequestSelectionByName` does a second heap scan inside the DLL and returns a slot index or `-1`. Since the C# side already holds `charNames`, that second scan is redundant — Phase 5b moves the name → slot resolution to C# and the DLL contract collapses to slot-only via `RequestSelectionBySlot`.

**New file:** `Core/CharacterSelector.cs`
```csharp
using System;

namespace EQSwitch.Core;

/// <summary>
/// Pure decision helper for auto-login character selection.
///
/// <see cref="Decide"/> answers "which 1-based slot should I click?" given the
/// user's intent (explicit slot or name to match) and the MQ2 heap-scanned
/// character list. Safety policies (bounds checks, slot-mode fallback,
/// wrong-character abort) stay at the caller — this helper is pure.
/// </summary>
public static class CharacterSelector
{
    /// <summary>
    /// Decide which character slot to select during auto-login.
    ///
    /// <paramref name="requestedSlot"/>: 0 = auto-by-name; 1-10 = explicit slot.
    /// <paramref name="requestedName"/>: name to match when <paramref name="requestedSlot"/> is 0.
    /// <paramref name="charNamesInHeap"/>: MQ2-scanned character list order
    /// (null or empty = heap not yet populated).
    /// </summary>
    /// <returns>
    ///   <c>resolvedSlot</c> (1-10) = slot to click, or 0 = no actionable decision.
    ///   <c>resolvedByName</c> = true when the heap scan matched; false otherwise.
    ///   <c>decisionLog</c> = one-line summary for <c>FileLogger</c>.
    /// </returns>
    public static (int resolvedSlot, bool resolvedByName, string decisionLog) Decide(
        int requestedSlot, string? requestedName, string[]? charNamesInHeap)
    {
        // Case 1: heap not ready → honor the caller's explicit requested slot.
        // If requestedSlot is 0 the caller has nothing to fall back to; return 0.
        if (charNamesInHeap == null || charNamesInHeap.Length == 0)
            return (requestedSlot, false,
                $"heap empty, fall back to requested slot {requestedSlot}");

        // Case 2: auto-by-name — scan the heap for an Ordinal match.
        if (requestedSlot == 0 && !string.IsNullOrEmpty(requestedName))
        {
            for (int i = 0; i < charNamesInHeap.Length; i++)
            {
                if (string.Equals(charNamesInHeap[i], requestedName,
                    StringComparison.Ordinal))
                    return (i + 1, true, $"name match '{requestedName}' at slot {i + 1}");
            }
            return (0, false,
                $"name '{requestedName}' not in heap ({string.Join(",", charNamesInHeap)})");
        }

        // Case 3: explicit slot requested.
        if (requestedSlot >= 1 && requestedSlot <= 10)
            return (requestedSlot, false, $"explicit slot {requestedSlot}");

        // Case 4: malformed request (slot=0 + empty name).
        return (0, false, "no slot or name requested");
    }
}
```

**Name comparison:** `StringComparison.Ordinal`, matching `AccountKey.Matches` and `FindAccountByName` / `FindCharacterByName`. The handoff's draft used `OrdinalIgnoreCase` — overridden here because the rest of the codebase compares character names case-sensitive.

**Caller rewrite (`AutoLoginManager.RunLoginSequence`, ~30 lines around lines 448-490):**
```csharp
var (resolvedSlot, resolvedByName, decisionLog) = CharacterSelector.Decide(
    character.CharacterSlot, character.Name, charNames);
FileLogger.Info($"AutoLogin: selector → {decisionLog}");

bool selected = false;
bool abortWrongCharacter = false;

if (resolvedSlot == 0)
{
    // No actionable slot. If heap is in slot-mode ("Slot 1".."Slot N") and we
    // couldn't find the name, the documented fallback is default selection.
    // Otherwise abort to avoid entering world on the wrong character.
    bool isSlotMode = charNames.Length > 0
        && charNames[0].StartsWith("Slot ", StringComparison.Ordinal);
    if (isSlotMode)
    {
        FileLogger.Info($"AutoLogin: slot-based mode — name '{character.Name}' unavailable, using default selection");
    }
    else
    {
        FileLogger.Error($"AutoLogin: character '{character.Name}' not found in account '{account.Name}' — stopping at charselect to avoid wrong-character enter-world");
        Report($"{account.Name}: character '{character.Name}' not found — stopped at char select");
        abortWrongCharacter = true;
    }
}
else if (resolvedSlot > charCount)
{
    // Slot out of range — same wrong-character guard as the pre-extraction code.
    FileLogger.Error($"AutoLogin: slot {resolvedSlot} exceeds char count {charCount} — stopping at charselect to avoid wrong-character enter-world");
    Report($"{account.Name}: slot {resolvedSlot} out of range (only {charCount} characters) — stopped at char select");
    abortWrongCharacter = true;
}
else
{
    charSelect.RequestSelectionBySlot(pid, resolvedSlot);
    FileLogger.Info($"AutoLogin: requested slot {resolvedSlot} for PID {pid} (byName={resolvedByName})");
    selected = true;
}

if (abortWrongCharacter)
    return;
```

**Behavior differences vs. pre-extraction (intentional):**

1. The DLL-side `RequestSelectionByName` call disappears. All selections now route through `RequestSelectionBySlot` — C# does the name → slot resolution once against the heap list it already read. Net: one fewer native scan per login. No behavior change in the matched path; the mismatched-name path that used to return `-1` from the DLL now returns `(0, false, "…not in heap…")` from `Decide`, handled identically by the caller.
2. The `isSlotMode` fallback triggers on `resolvedSlot == 0 && charNames[0].StartsWith("Slot ")`. Pre-extraction it triggered on `RequestSelectionByName returning -1 && charNames[0].StartsWith("Slot ")`. Same set of inputs, same decision.
3. **Malformed target (`CharacterSlot == 0 && Name == ""`) now aborts** with `abortWrongCharacter = true` where pre-extraction silently fell through with `selected = false`. Rationale: the pre-extraction path took no branch, leaving the login sequence to proceed past the selection block with no character selected — `selected = false` meant the acknowledgment wait was skipped but enter-world still fired on whatever slot was currently highlighted (typically slot 1). That was silent wrong-character behavior; the abort is the same safety guarantee as the named-character-missing abort in Case 2. Callers with a legitimate "no target, just enter on default" intent should use `requestedSlot = 1` explicitly.

### 4. Tests

**Harness:** `Core/CharacterSelectorTests.cs` exposing `public static int RunAll()` returning 0 / 1. Invoked from a new `--test-character-selector` CLI flag in `Program.cs`, matching the existing `--test-migrate` / `--test-split` pattern.

**4 cases (per master plan line 380-382):**

| # | Call | Expected |
|---|---|---|
| 1 | `Decide(0, "backup", ["Foo", "backup", "bar"])` | `(2, true, starts with "name match 'backup'…")` |
| 2 | `Decide(0, "zzz", ["Foo", "backup"])` | `(0, false, starts with "name 'zzz' not in heap")` |
| 3 | `Decide(3, "", [])` | `(3, false, starts with "heap empty")` |
| 4 | `Decide(3, "backup", ["Foo", "backup"])` | `(3, false, starts with "explicit slot 3")` |

Each case asserts all three tuple members. `decisionLog` is asserted on prefix only — the message is human-facing, not a contract.

**CLI flag wiring (`Program.cs`):**
```csharp
else if (args.Length >= 1 && args[0] == "--test-character-selector")
{
    return Core.CharacterSelectorTests.RunAll();
}
```

Exits 0 on pass, 1 on any assertion failure. Script invocation:
```bash
EQSwitch.exe --test-character-selector
```

## Commit plan

Three atomic commits on `main`, committed in order. Each passes the full gate before the next starts.

1. **`refactor(window): v4 Character lookup for {CHAR} title template`** — Section 1. Files: `Core/WindowManager.cs`.
2. **`refactor(affinity): read CharacterAliases (Phase 5b v4 migration)`** — Section 2. Files: `Core/AffinityManager.cs`.
3. **`feat(login): extract CharacterSelector.Decide() pure function`** — Section 3 + Section 4 together. Files: `Core/CharacterSelector.cs` (new), `Core/CharacterSelectorTests.cs` (new), `Core/AutoLoginManager.cs`, `Program.cs`.

Commit 3 is a single commit rather than split across "add pure function" + "use it" because the function without a caller fails the "only add code you use" smell and because tests belong with the unit under test. The pure function + tests + caller refactor form one coherent change.

## Verification gate (must pass before review + publish)

After each of the 3 commits and once more after all 3:

- `dotnet build --no-incremental` → 0 errors, exactly 1 warning (the expected `[Obsolete]` at `TrayManager.cs:~1726`).
- `bash _tests/migration/run_fixtures.sh` → 9 passed, 0 failed.
- `grep -c "gameState == 5" Native/mq2_bridge.cpp` → 2.
- `grep -c "result == -2" Core/AutoLoginManager.cs` → 1.
- `EQSwitch.exe --test-character-selector` → exit 0, all 4 cases print `ok:`.

After all 3 commits, additionally:

- `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` — must succeed.
- Deploy: kill running `EQSwitch.exe`, copy `bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe` + `eqswitch-hook.dll` + `dinput8.dll` to `C:/Users/nate/proggy/Everquest/EQSwitch/`.
- Nate-driven smoke test (sign-off gate for Phase 6 kickoff):
  - Launch from tray → `{CHAR}` renders correctly on all visible windows.
  - Priority override still fires for any configured per-character priority.
  - Auto-login via Account hotkey lands on the correct character by name.
  - Auto-login via Character hotkey lands on the correct slot.

## Review pass (before publish)

Dispatch three review agents **in parallel** after commit 3 and before deploy:

1. `pr-review-toolkit:code-reviewer` — scope: Phase 5b diff vs. `aa1b6ae`. Project conventions, DarkTheme purity, NativeMethods discipline, CLAUDE.md rules.
2. `pr-review-toolkit:silent-failure-hunter` — scope: Phase 5b diff. Flag any swallowed exception, missing null/empty guard, or fallback that could mask a real failure.
3. `feature-dev:code-reviewer` — scope: Phase 5b diff. Independent bug / logic / security review.

Fold every `HIGH` finding. Fold `MEDIUM` findings unless explicitly dismissed with a one-line reason in the commit message. `LOW` findings collected for optional follow-up.

## Hard rules (carry forward from Phase 5a handoff)

- Never break v3 config of an existing user. Backup tested on every schema touch.
- Phantom-click defenses unchanged (`gameState == 5` ×2, `result == -2` ×1).
- Stage specific files. No `git add -A`. Conventional commit titles under 72 chars.
- No emojis in code, comments, or commit messages (except the existing tray-menu surrogate-pair escapes).
- `StringComparison.Ordinal` for name compares.
- Native C++ untouched. If `mq2_bridge.cpp` gets edited, STOP and re-read this spec.
- Memory file update: append a Phase 5b status line to `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v3_10_0_account_split.md` when Phase 5b verification passes.

## Risk register

| Risk | Impact | Mitigation |
|---|---|---|
| `QuickLogin{N}` empty for a user who never set slots 1-4 | Title falls back to EQ native title (same as pre-Phase-5b when `LegacyAccounts[slotIndex].CharacterName` was empty) | Same observable behavior — no action needed. |
| `FindCharacterByName` returns null because user renamed a Character after binding | Title falls back to EQ native title | Explicit — v4 source-of-truth says the name isn't current. |
| `CharacterAliases` out of sync with `LegacyCharacterProfiles` after a manual JSON edit | Wrong priority override applied | `AppConfig.Validate()` v4-resync block (Phase 2.6) reconciles on load. |
| `Decide()` behavior drift from pre-extraction code | Wrong character selected on auto-login | 4 unit tests + 9 migration fixtures + Nate smoke test. |
| Redundant native heap scan removal hides a native-side bug | Auto-login regression on slot-mode account | Existing `isSlotMode` fallback preserved at caller; Nate smoke test covers slot-mode and named-mode. |

## After Phase 5b

Phase 5b closes the loop on consumer migration. Phase 6 (v3.11.0) is pure subtraction:

- Delete `AppConfig.LegacyAccounts`, `AppConfig.LegacyCharacterProfiles`, `AppConfig.QuickLogin1-4`, `HotkeyConfig.AutoLogin1-4`, `HotkeyConfig.MultiMonitorEnabled`, `AppConfig.HotkeysLegacyBannerDismissed`.
- Delete `Models/LoginAccount.cs`, `CharacterProfile`, `Config/LoginAccountSplitter.cs`.
- Delete `[Obsolete] AutoLoginManager.LoginAccount` wrapper (removes the final build warning).
- Delete `TrayManager.ExecuteQuickLogin`, `TrayManager.FireLegacyQuickLoginSlot`.
- Delete `SettingsForm._pendingAccounts ReverseMapToLegacy` helper.
- Delete `AppConfig.Validate()` v4-resync defense-in-depth block.
- Implement `MigrateV4ToV5(JsonObject)` — rename `accountsV4` → `accounts`, `charactersV4` → `characters`, drop leftover v3 keys.
- Bump `EQSwitch.csproj` to `3.11.0`. Tag release. Update `README.md` with v4 architecture.
- Document the add-then-rename migration pattern at `_templates/references/migration-framework.md`.

Phase 6 kickoff requires Nate's explicit sign-off after the Phase 5b smoke test passes.
