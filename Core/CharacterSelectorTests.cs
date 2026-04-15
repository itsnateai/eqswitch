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

        // Case 5: malformed input (slot=0 + empty name) → Case 4 of Decide.
        // Pins the spec's intentional behavior drift for this edge.
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                0, "", new[] { "Foo", "bar" });
            failures += Assert("case5 slot", slot, 0);
            failures += Assert("case5 byName", byName, false);
            failures += AssertStartsWith("case5 log", log, "no slot or name requested");
        }

        // Case 6: name match is Ordinal (case-sensitive) — regression guard
        // against drift to OrdinalIgnoreCase. 'FOO' must NOT match 'foo'.
        {
            var (slot, byName, log) = CharacterSelector.Decide(
                0, "FOO", new[] { "foo" });
            failures += Assert("case6 slot", slot, 0);
            failures += Assert("case6 byName", byName, false);
            failures += AssertStartsWith("case6 log", log, "name 'FOO' not in heap");
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

        Console.WriteLine(failures == 0
            ? "CharacterSelectorTests: all 7 cases PASSED"
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
