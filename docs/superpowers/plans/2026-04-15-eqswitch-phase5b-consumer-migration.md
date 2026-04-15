# EQSwitch Phase 5b — Consumer Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the v3 → v4 Account/Character split migration on the runtime-consumer side across three files, extract the character-selection decision into a pure testable helper, and deploy a verified build.

**Architecture:** Three independent refactors land as three atomic commits. (1) `WindowManager.SetWindowTitle` resolves `{CHAR}` through the v4 `Characters` list via the v3 `QuickLogin{N}` indirection. (2) `AffinityManager.FindSlotPriorityOverride` reads `CharacterAliases` instead of `LegacyCharacterProfiles`. (3) `AutoLoginManager.RunLoginSequence` delegates slot-decision logic to a new pure `CharacterSelector.Decide()` with four unit tests behind a new `--test-character-selector` CLI flag. Caller keeps safety policies (bounds check, slot-mode fallback, wrong-character abort). No native-side or UX changes.

**Tech Stack:** C# 12, .NET 8 Windows, WinForms, bash (fixtures), existing `--test-migrate` / `--test-split` CLI test pattern.

**Spec:** [`docs/superpowers/specs/2026-04-15-eqswitch-phase5b-consumer-migration-design.md`](../specs/2026-04-15-eqswitch-phase5b-consumer-migration-design.md)

---

## File Structure

**Modified files (3):**
- `Core/WindowManager.cs` — `SetWindowTitle` body, lines 437-442. One method touched, ~8 lines.
- `Core/AffinityManager.cs` — `FindSlotPriorityOverride` body, lines 131-141. One method touched, 2 lines.
- `Core/AutoLoginManager.cs` — `RunLoginSequence` selection block, lines ~448-490. ~30 lines rewritten.
- `Program.cs` — add `--test-character-selector` CLI flag handler before `Environment.Exit` / `Application.Run` path. ~6 lines.

**New files (2):**
- `Core/CharacterSelector.cs` — pure static helper with `Decide(int, string?, string[]?)` returning `(int, bool, string)`. ~60 lines.
- `Core/CharacterSelectorTests.cs` — `public static int RunAll()` with 4 assertions, returns 0 on pass, 1 on any fail. ~90 lines.

**Unchanged / verified-only:**
- `Config/AppConfig.cs` — already exposes `Characters`, `CharacterAliases`, `QuickLogin1-4`, `FindCharacterByName`.
- `Models/CharacterAlias.cs` — already has `SlotIndex: int` and `PriorityOverride: string?`.
- `Native/*` — no changes. Phantom-click defenses (`gameState == 5` ×2, `result == -2` ×1) must stay intact.

---

## Task 1: `WindowManager.SetWindowTitle` → v4 Character lookup

**Files:**
- Modify: `Core/WindowManager.cs:437-442`

- [ ] **Step 1: Re-verify the edit site matches the spec**

Run:
```bash
sed -n '436,442p' Core/WindowManager.cs
```
Expected exact output:
```csharp
        // Look up by slot index in auto-login accounts
        if (slotIndex < _config.LegacyAccounts.Count)
        {
            var accountName = _config.LegacyAccounts[slotIndex].CharacterName;
            if (!string.IsNullOrEmpty(accountName))
                charName = accountName;
        }
```
If it differs, STOP and reconcile with the spec before proceeding.

- [ ] **Step 2: Apply the edit**

Replace lines 436-442 verbatim with:
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

- [ ] **Step 3: Build, verify clean**

Run:
```bash
dotnet build --no-incremental 2>&1 | tail -20
```
Expected: `Build succeeded.` with `0 Error(s)` and exactly `1 Warning(s)` (the `[Obsolete]` warning at `TrayManager.cs:~1726`). Any new warning is a regression — STOP and fix before proceeding.

- [ ] **Step 4: Run migration fixtures**

Run:
```bash
bash _tests/migration/run_fixtures.sh 2>&1 | tail -5
```
Expected: `9 passed, 0 failed`. Any failure means the refactor broke migration — STOP.

- [ ] **Step 5: Verify phantom-click defense gates unchanged**

Run:
```bash
echo "gameState: $(grep -c 'gameState == 5' Native/mq2_bridge.cpp)"
echo "result: $(grep -c 'result == -2' Core/AutoLoginManager.cs)"
```
Expected:
```
gameState: 2
result: 1
```

- [ ] **Step 6: Commit**

Run:
```bash
git add Core/WindowManager.cs
git commit -m "$(cat <<'EOF'
refactor(window): v4 Character lookup for {CHAR} title template

SetWindowTitle previously read LegacyAccounts[slotIndex].CharacterName
to resolve the {CHAR} placeholder. Move the data source to the v4
Characters list via FindCharacterByName, keyed by the QuickLogin{N}
slot->name binding. The QuickLogin{N} indirection itself stays v3-
shaped and is Phase 6-deletion-slated; only the resolved-name source
moves here.

Behavior identical for migrated configs: Phase 1 MigrateV3ToV4 seeded
QuickLogin{N} with the same name string LegacyAccounts[N].CharacterName
held, and FindCharacterByName resolves it against the Characters list
that same migration built. Unresolved lookups fall through to EQ's
native window title, matching pre-refactor empty-name behavior.
EOF
)"
git log --oneline -1
```
Expected: new commit on `main`.

---

## Task 2: `AffinityManager.FindSlotPriorityOverride` → `CharacterAliases`

**Files:**
- Modify: `Core/AffinityManager.cs:131-141`

- [ ] **Step 1: Verify `CharacterAlias` still has the matching shape**

Run:
```bash
grep -n "SlotIndex\|PriorityOverride" Models/CharacterAlias.cs
```
Expected output includes:
```
17:    public int SlotIndex { get; set; } = 0;
23:    public string? PriorityOverride { get; set; } = null;
```
If the property names differ, STOP and reconcile with spec.

- [ ] **Step 2: Re-verify the edit site**

Run:
```bash
sed -n '131,141p' Core/AffinityManager.cs
```
Expected exact output:
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

- [ ] **Step 3: Apply the edit**

Replace the method body with:
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
(Note: the obsolete comment line is removed, and the local is renamed from `characters` to `aliases` for clarity.)

- [ ] **Step 4: Build, verify clean**

Run:
```bash
dotnet build --no-incremental 2>&1 | tail -20
```
Expected: `Build succeeded.` with `0 Error(s)` and exactly `1 Warning(s)`.

- [ ] **Step 5: Run migration fixtures**

Run:
```bash
bash _tests/migration/run_fixtures.sh 2>&1 | tail -5
```
Expected: `9 passed, 0 failed`.

- [ ] **Step 6: Verify phantom-click gates unchanged**

Run:
```bash
echo "gameState: $(grep -c 'gameState == 5' Native/mq2_bridge.cpp)"
echo "result: $(grep -c 'result == -2' Core/AutoLoginManager.cs)"
```
Expected:
```
gameState: 2
result: 1
```

- [ ] **Step 7: Commit**

Run:
```bash
git add Core/AffinityManager.cs
git commit -m "$(cat <<'EOF'
refactor(affinity): read CharacterAliases (Phase 5b v4 migration)

FindSlotPriorityOverride previously read LegacyCharacterProfiles to
resolve a per-slot priority-class override. CharacterAlias carries
identical SlotIndex + PriorityOverride fields and is populated from
the same source by AppConfig.Validate()'s v4-resync block, so the swap
is a pure field rename with no behavior change.

Drops the outdated "Phase 5 will swap to CharacterAliases" comment and
renames the local to aliases for clarity. Phase 6 will delete
LegacyCharacterProfiles entirely.
EOF
)"
git log --oneline -1
```
Expected: new commit on `main`.

---

## Task 3: Extract `CharacterSelector.Decide()` — Red / Green / Refactor

**Files:**
- Create: `Core/CharacterSelector.cs`
- Create: `Core/CharacterSelectorTests.cs`
- Modify: `Program.cs` (add CLI flag handler near the existing `--test-migrate` block)
- Modify: `Core/AutoLoginManager.cs:441-508` (refactor the selection block inside `RunLoginSequence`)

All changes ship as **one atomic commit** per the spec's commit plan.

### Red phase — write failing tests first

- [ ] **Step 1: Create the test harness with all 4 assertions**

Write `Core/CharacterSelectorTests.cs`:
```csharp
using System;

namespace EQSwitch.Core;

/// <summary>
/// Unit tests for CharacterSelector.Decide(). Invoked via the
/// --test-character-selector CLI flag from Program.cs. Returns 0 when
/// all 4 cases pass, 1 on any assertion failure.
/// </summary>
public static class CharacterSelectorTests
{
    public static int RunAll()
    {
        int failures = 0;

        // Case 1: auto-by-name matches mid-list entry → 1-based slot + byName flag.
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                0, "backup", new[] { "Foo", "backup", "bar" });
            failures += Assert("case1 slot", slot, 2);
            failures += Assert("case1 byName", byName, true);
            failures += AssertStartsWith("case1 log", log, "name match 'backup'");
        }

        // Case 2: auto-by-name with no heap match → 0-slot, informative log.
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                0, "zzz", new[] { "Foo", "backup" });
            failures += Assert("case2 slot", slot, 0);
            failures += Assert("case2 byName", byName, false);
            failures += AssertStartsWith("case2 log", log, "name 'zzz' not in heap");
        }

        // Case 3: heap not populated → honor requested slot as a fallback.
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                3, "", Array.Empty<string>());
            failures += Assert("case3 slot", slot, 3);
            failures += Assert("case3 byName", byName, false);
            failures += AssertStartsWith("case3 log", log, "heap empty");
        }

        // Case 4: explicit slot wins over name hint (name not consulted).
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                3, "backup", new[] { "Foo", "backup" });
            failures += Assert("case4 slot", slot, 3);
            failures += Assert("case4 byName", byName, false);
            failures += AssertStartsWith("case4 log", log, "explicit slot 3");
        }

        Console.WriteLine(failures == 0
            ? "CharacterSelectorTests: all 4 cases PASSED"
            : $"CharacterSelectorTests: {failures} assertion failure(s)");
        return failures == 0 ? 0 : 1;
    }

    private static int Assert<T>(string name, T actual, T expected)
    {
        if (Equals(actual, expected))
        {
            Console.WriteLine($"    ok: {name}");
            return 0;
        }
        Console.WriteLine($"    FAIL: {name} (expected '{expected}', got '{actual}')");
        return 1;
    }

    private static int AssertStartsWith(string name, string actual, string expectedPrefix)
    {
        if (actual != null && actual.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            Console.WriteLine($"    ok: {name}");
            return 0;
        }
        Console.WriteLine($"    FAIL: {name} (expected prefix '{expectedPrefix}', got '{actual}')");
        return 1;
    }
}
```

- [ ] **Step 2: Add the CLI flag handler in `Program.cs`**

In `Program.cs`, immediately after the closing brace of the `--test-split` block (which ends around line 80 with `return;`), insert the new handler:
```csharp
        // --test-character-selector — run Core/CharacterSelectorTests.RunAll() and
        // exit with its return code. Used to gate Phase 5b's pure decision helper.
        if (args.Length >= 1 && args[0] == "--test-character-selector")
        {
            Environment.Exit(Core.CharacterSelectorTests.RunAll());
            return;
        }
```

To locate the exact insertion point, run:
```bash
grep -n "test-split" Program.cs
```
Then open `Program.cs` and insert the new block after the `return;` that closes the `--test-split` handler and before the next existing code.

- [ ] **Step 3: Verify the build fails on missing `CharacterSelector` type**

Run:
```bash
dotnet build --no-incremental 2>&1 | grep -E "error|Error" | head -10
```
Expected: at least one `CS0103` or `CS0246` referencing `CharacterSelector`. This is the Red phase — the tests reference a type that does not exist yet. If the build succeeds, a stray file was added; STOP and investigate.

### Green phase — minimal implementation that makes tests pass

- [ ] **Step 4: Create `Core/CharacterSelector.cs` with the full implementation**

Write:
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

- [ ] **Step 5: Build, verify zero errors**

Run:
```bash
dotnet build --no-incremental 2>&1 | tail -10
```
Expected: `Build succeeded.` with `0 Error(s)` and `1 Warning(s)` (the known `[Obsolete]`).

- [ ] **Step 6: Run the 4 unit tests, verify all pass**

The test runner lives inside the built exe. Run:
```bash
./bin/Debug/net8.0-windows/win-x64/EQSwitch.exe --test-character-selector
echo "exit: $?"
```
Expected stdout includes:
```
    ok: case1 slot
    ok: case1 byName
    ok: case1 log
    ok: case2 slot
    ok: case2 byName
    ok: case2 log
    ok: case3 slot
    ok: case3 byName
    ok: case3 log
    ok: case4 slot
    ok: case4 byName
    ok: case4 log
CharacterSelectorTests: all 4 cases PASSED
exit: 0
```
If any `FAIL:` line appears or exit code is non-zero, STOP and fix `Decide` before proceeding.

### Refactor phase — wire `Decide` into `RunLoginSequence`

- [ ] **Step 7: Re-verify the caller site matches pre-extraction code**

Run:
```bash
sed -n '448,490p' Core/AutoLoginManager.cs
```
Expected exact output:
```csharp
                    bool selected = false;
                    bool abortWrongCharacter = false;
                    if (character.CharacterSlot > 0)
                    {
                        // Slot-based selection (1-10) — direct index, no name lookup
                        if (character.CharacterSlot <= charCount)
                        {
                            charSelect.RequestSelectionBySlot(pid, character.CharacterSlot);
                            FileLogger.Info($"AutoLogin: requested slot {character.CharacterSlot} for PID {pid}");
                            selected = true;
                        }
                        else
                        {
                            // Requested slot exceeds actual character count — entering world on
                            // whatever's selected would land on the wrong character. Abort safely.
                            FileLogger.Error($"AutoLogin: slot {character.CharacterSlot} exceeds char count {charCount} — stopping at charselect to avoid wrong-character enter-world");
                            Report($"{account.Name}: slot {character.CharacterSlot} out of range (only {charCount} characters) — stopped at char select");
                            abortWrongCharacter = true;
                        }
                    }
                    else if (!string.IsNullOrEmpty(character.Name))
                    {
                        // Name-based selection — search and select
                        int selIdx = charSelect.RequestSelectionByName(pid, character.Name);
                        selected = selIdx >= 0;
                        if (!selected)
                        {
                            // In slot-based mode (names are "Slot N"), name lookup always fails —
                            // this is the one case where entering on default is the documented fallback.
                            bool isSlotMode = charNames.Length > 0 && charNames[0].StartsWith("Slot ", StringComparison.Ordinal);
                            if (isSlotMode)
                                FileLogger.Info($"AutoLogin: slot-based mode — name '{character.Name}' unavailable, using default selection");
                            else
                            {
                                // Named-mode with the requested character missing (renamed, deleted,
                                // or on a different account). Entering on default would silently
                                // swap to slot 1 — dangerous for team configurations. Abort.
                                FileLogger.Error($"AutoLogin: character '{character.Name}' not found in account '{account.Name}' — stopping at charselect to avoid wrong-character enter-world");
                                Report($"{account.Name}: character '{character.Name}' not found — stopped at char select");
                                abortWrongCharacter = true;
                            }
                        }
                    }
```
If it differs, STOP and reconcile with the spec before rewriting.

- [ ] **Step 8: Replace the selection block with the `Decide`-based caller**

Replace the ~44 lines above with exactly this block (preserving the outer indentation — 20 spaces):
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
                        // Slot out of range — same wrong-character guard as pre-extraction.
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
```

- [ ] **Step 9: Build, verify clean**

Run:
```bash
dotnet build --no-incremental 2>&1 | tail -10
```
Expected: `Build succeeded.` with `0 Error(s)` and exactly `1 Warning(s)`.

- [ ] **Step 10: Re-run unit tests (defensive — caller refactor shouldn't break them)**

Run:
```bash
./bin/Debug/net8.0-windows/win-x64/EQSwitch.exe --test-character-selector
echo "exit: $?"
```
Expected: `all 4 cases PASSED`, exit 0.

- [ ] **Step 11: Run migration fixtures**

Run:
```bash
bash _tests/migration/run_fixtures.sh 2>&1 | tail -5
```
Expected: `9 passed, 0 failed`.

- [ ] **Step 12: Verify phantom-click gates unchanged**

Run:
```bash
echo "gameState: $(grep -c 'gameState == 5' Native/mq2_bridge.cpp)"
echo "result: $(grep -c 'result == -2' Core/AutoLoginManager.cs)"
```
Expected:
```
gameState: 2
result: 1
```

- [ ] **Step 13: Verify no unintended file changes**

Run:
```bash
git status --short
```
Expected exactly these 4 entries (order may vary):
```
 M Core/AutoLoginManager.cs
 M Program.cs
?? Core/CharacterSelector.cs
?? Core/CharacterSelectorTests.cs
```
Any other file listed → investigate and revert unintended changes before committing.

- [ ] **Step 14: Commit all 4 files atomically**

Run:
```bash
git add Core/CharacterSelector.cs Core/CharacterSelectorTests.cs Core/AutoLoginManager.cs Program.cs
git commit -m "$(cat <<'EOF'
feat(login): extract CharacterSelector.Decide() pure function

Pulls the "which slot do I click?" decision out of RunLoginSequence
into a new pure static helper in Core/CharacterSelector.cs with 4
unit tests gated by a new --test-character-selector CLI flag.

The DLL-side RequestSelectionByName call goes away — C# already
reads charNames via ReadAllCharNames, so resolving name->slot in
C# and routing through RequestSelectionBySlot drops one redundant
native scan per login. Caller keeps the charCount bounds check,
the isSlotMode fallback, and the wrong-character abort.

Intentional behavior drift: malformed targets (CharacterSlot == 0
AND Name empty) now abort instead of silently entering world on
the default slot. Documented in the Phase 5b spec.
EOF
)"
git log --oneline -1
```
Expected: new commit on `main`.

---

## Task 4: Parallel code review

After all 3 refactor commits have landed, dispatch three review agents in parallel. Each gets the Phase 5b diff range (`aa1b6ae..HEAD`).

- [ ] **Step 1: Note the review range**

Run:
```bash
git log --oneline aa1b6ae..HEAD
```
Expected: the 3 refactor commits from Tasks 1-3 plus the spec commit.

- [ ] **Step 2: Dispatch all 3 review agents in parallel (one tool message, 3 Agent calls)**

- Agent A: `pr-review-toolkit:code-reviewer`. Scope: "Review Phase 5b diff `aa1b6ae..HEAD` against EQSwitch conventions (CLAUDE.md, NativeMethods discipline, DarkTheme purity, no `git add -A`, `StringComparison.Ordinal` for names). Report HIGH/MEDIUM/LOW findings with file:line anchors."
- Agent B: `pr-review-toolkit:silent-failure-hunter`. Scope: "Review Phase 5b diff `aa1b6ae..HEAD` for silent failures: swallowed exceptions, missing null/empty guards, fallback paths that could mask real errors. Report by severity."
- Agent C: `feature-dev:code-reviewer`. Scope: "Independent bug / logic / security review of Phase 5b diff `aa1b6ae..HEAD`. No project-convention context needed — pure code-quality pass."

- [ ] **Step 3: Fold findings**

Policy:
- **HIGH** findings: always fold before publish.
- **MEDIUM** findings: fold unless each is dismissed with a one-line reason captured in the fold commit message.
- **LOW** findings: collect for optional follow-up, do not gate publish.

Fold all accepted findings in a single commit if possible, or one per logical cluster. Commit message prefix: `fix(phase5b): fold AGENT_LEVEL agent findings` (e.g. `fix(phase5b): fold HIGH agent findings`).

- [ ] **Step 4: Re-run the full gate after folding**

Run sequentially:
```bash
dotnet build --no-incremental 2>&1 | tail -5
bash _tests/migration/run_fixtures.sh 2>&1 | tail -5
echo "gameState: $(grep -c 'gameState == 5' Native/mq2_bridge.cpp)"
echo "result: $(grep -c 'result == -2' Core/AutoLoginManager.cs)"
./bin/Debug/net8.0-windows/win-x64/EQSwitch.exe --test-character-selector; echo "exit: $?"
```
Expected: 0 errors / 1 warning, 9/9 fixtures, 2/1 gates, all 4 selector tests pass.

---

## Task 5: Publish + deploy

- [ ] **Step 1: Kill any running EQSwitch.exe**

Run:
```bash
tasklist //FI "IMAGENAME eq EQSwitch.exe" 2>/dev/null | grep -i "EQSwitch.exe" && taskkill //IM EQSwitch.exe //F || echo "not running"
```
Expected: either `"SUCCESS: The process ... has been terminated"` or `"not running"`.

- [ ] **Step 2: Publish the release build**

Run:
```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true 2>&1 | tail -5
```
Expected: `Build succeeded.` and a path to `bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe`.

- [ ] **Step 3: Deploy to the live install directory**

Run:
```bash
cp bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe "C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe"
cp Native/eqswitch-hook.dll "C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-hook.dll"
cp Native/dinput8.dll "C:/Users/nate/proggy/Everquest/EQSwitch/dinput8.dll" 2>/dev/null || echo "dinput8.dll unchanged, skipping"
ls -la "C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe"
```
Expected: deployed exe has today's timestamp and is the same size (~155MB) as the publish output.

- [ ] **Step 4: Smoke-start the deployed exe and stop it**

Run:
```bash
start "" "C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe"
sleep 3
tasklist //FI "IMAGENAME eq EQSwitch.exe" | grep -i "EQSwitch.exe"
```
Expected: one `EQSwitch.exe` line with a PID. The exe launched cleanly (single-instance mutex held, tray icon visible).

Then:
```bash
taskkill //IM EQSwitch.exe //F
```

Confirms the published binary parses on the live machine before handing off to Nate.

---

## Task 6: Nate-driven smoke test

- [ ] **Step 1: Hand off to Nate with the checklist**

Tell Nate the deployed exe is ready and the 4 smoke-test items from the spec's verification gate:

> "Phase 5b built, reviewed, deployed to `C:/Users/nate/proggy/Everquest/EQSwitch/`. Please verify:
> 1. Launch EQ from the tray. `{CHAR}` in window titles renders the expected character name.
> 2. Any per-character priority override still fires (check Task Manager or ProcessManagerForm).
> 3. Auto-login via Account hotkey still lands on the right character by name.
> 4. Auto-login via Character hotkey still lands on the right slot.
>
> Report any surprise. I'll STOP here for Phase 6 sign-off."

- [ ] **Step 2: Wait for sign-off**

Do NOT proceed to Phase 6 or touch any deferred-cleanup code until Nate explicitly signs off on Phase 5b.

---

## Task 7: Memory + push

- [ ] **Step 1: Append a Phase 5b status line to the tracker**

Append one line to `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v3_10_0_account_split.md` summarizing what shipped. Example:
```
- Phase 5b: consumer migration (HEAD <sha>, 3 refactor + N fold commits, 4-case CharacterSelector tests green, deployed + smoke-tested).
```
Replace `<sha>` with the actual HEAD and `N` with the fold-commit count.

- [ ] **Step 2: Push all Phase 5b commits**

Run:
```bash
git push origin main
git log --oneline origin/main...HEAD
```
Expected second command output is empty (local HEAD == origin/main). Any non-empty output means the push didn't include everything — investigate.

- [ ] **Step 3: Final status line**

Report the phase-closing summary in chat: commit count, HEAD sha, fixtures/gates/selector-tests status, Nate's smoke-test result, and "STOP — Phase 6 requires explicit sign-off."

---

## Verification gate (summary — mirrors spec)

After Task 3, Task 4 (post-fold), and once more before deploy:
- `dotnet build --no-incremental` → 0 errors, 1 expected `[Obsolete]` warning.
- `bash _tests/migration/run_fixtures.sh` → 9 passed, 0 failed.
- `grep -c "gameState == 5" Native/mq2_bridge.cpp` → 2.
- `grep -c "result == -2" Core/AutoLoginManager.cs` → 1.
- `EQSwitch.exe --test-character-selector` → 4 `ok:` lines + exit 0.

After deploy, Nate-driven:
- `{CHAR}` placeholder renders correctly.
- Per-character priority override fires.
- Auto-login by Account hotkey lands on correct character.
- Auto-login by Character hotkey lands on correct slot.

STOP after Task 6 sign-off. Phase 6 (v3.11.0 cleanup) requires explicit go-ahead.
