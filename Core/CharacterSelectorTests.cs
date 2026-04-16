// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System;

namespace EQSwitch.Core;

/// <summary>
/// Unit tests for CharacterSelector.Decide(). Invoked via the
/// --test-character-selector CLI flag from Program.cs. RunAll() returns 0
/// on all passes, 1 on any assertion failure. The Program.cs handler maps
/// unhandled exceptions to exit code 2.
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

        // Case 5: malformed input (slot=0 + empty name) → Case 4 of Decide.
        // Pins the spec's intentional behavior drift for this edge.
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                0, "", new[] { "Foo", "bar" });
            failures += Assert("case5 slot", slot, 0);
            failures += Assert("case5 byName", byName, false);
            failures += AssertStartsWith("case5 log", log, "no slot or name requested");
        }

        // Case 6: name match is OrdinalIgnoreCase — regression guard against
        // drift BACK to Ordinal. Heap-read names can case-vary vs. config, so
        // 'FOO' MUST match 'foo' at slot 1. This matches pre-extraction
        // CharSelectReader.RequestSelectionByName semantics.
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                0, "FOO", new[] { "foo" });
            failures += Assert("case6 slot", slot, 1);
            failures += Assert("case6 byName", byName, true);
            failures += AssertStartsWith("case6 log", log, "name match 'FOO'");
        }

        // Case 7: requestedSlot above nominal range (11+) still routes through
        // Case 3 so the caller's resolvedSlot > charCount bounds check produces
        // the pre-extraction 'slot N exceeds char count N' error message.
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                11, "", new[] { "Foo", "bar" });
            failures += Assert("case7 slot", slot, 11);
            failures += Assert("case7 byName", byName, false);
            failures += AssertStartsWith("case7 log", log, "explicit slot 11");
        }

        // Case 8 (GAP-1): negative requestedSlot is out-of-contract input.
        // Falls through to Case 4 ("no slot or name requested") because neither
        // Case 2 (requires requestedSlot==0) nor Case 3 (requires requestedSlot>=1)
        // matches. Pins the safe fall-through against a refactor to
        // `requestedSlot != 0` that would silently pass -5 to RequestSelectionBySlot.
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                -5, "foo", new[] { "foo" });
            failures += Assert("case8 slot", slot, 0);
            failures += Assert("case8 byName", byName, false);
            failures += AssertStartsWith("case8 log", log, "no slot or name requested");
        }

        // Case 9 (GAP-2): explicit slot wins cleanly even when the name ALSO
        // matches the heap at the same slot. Pins byName=false contract — the
        // caller uses this flag for logging to distinguish name-resolved from
        // slot-explicit selection. A refactor that set byName=true whenever
        // the name happened to match would silently change caller log output.
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                1, "foo", new[] { "foo", "bar" });
            failures += Assert("case9 slot", slot, 1);
            failures += Assert("case9 byName", byName, false);
            failures += AssertStartsWith("case9 log", log, "explicit slot 1");
        }

        // Case 10 (GAP-3): null requestedName (JSON deserializer can produce null
        // if config has "name": null) is treated identically to empty string via
        // string.IsNullOrEmpty. Pins the null-safety contract against a refactor
        // to `== ""` or `.Length == 0` that would NullReferenceException.
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                0, null, new[] { "Foo", "bar" });
            failures += Assert("case10 slot", slot, 0);
            failures += Assert("case10 byName", byName, false);
            failures += AssertStartsWith("case10 log", log, "no slot or name requested");
        }

        Console.WriteLine(failures == 0
            ? "CharacterSelectorTests: all 10 cases PASSED"
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
