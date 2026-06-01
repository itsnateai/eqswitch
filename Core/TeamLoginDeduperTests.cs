// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Linq;
using EQSwitch.Models;
using D = EQSwitch.Core.TeamLoginDeduper.Decision;

namespace EQSwitch.Core;

/// <summary>
/// Unit tests for <see cref="TeamLoginDeduper"/> — the launch-time same-login dedup that
/// backstops <c>TrayManager.FireTeam</c> (v3.23.4). Pure value logic, no WinForms.
/// Invoked via the --test-team-dedup CLI flag from Program.cs. RunAll() returns 0 on all
/// passes, 1 on any assertion failure; Program.cs maps unhandled exceptions to exit 2.
///
/// Guards the contract that two team slots resolving to the same login (Username, Server)
/// do not both fire — EQ kicks the duplicate session. Mirrors the OrdinalIgnoreCase
/// username/server semantics of the AutoLoginTeamsDialog Save-time guard. <see cref="Step"/>
/// is exercised directly (it is the primitive FireTeam calls per slot), as is the
/// <see cref="TeamLoginDeduper.Decide"/> batch form built on it.
/// </summary>
public static class TeamLoginDeduperTests
{
    public static int RunAll()
    {
        int failures = 0;

        AccountKey? Got1 = new AccountKey("gotquiz1", "Dalaya");
        AccountKey? Eis  = new AccountKey("eisley", "Dalaya");
        AccountKey? Null = null;

        // ── Decide (batch) ───────────────────────────────────────────────
        // The reproduced bug: Natedogg + Ohyoudidntknow both resolve to gotquiz1 — the
        // second must be dropped, not dual-fired.
        failures += AssertSeq("same-login pair (the Ohyoudidntknow bug)",
            new[] { Got1, Got1 }, D.Fire, D.SkipDuplicate);

        // Two distinct logins both fire.
        failures += AssertSeq("distinct logins",
            new[] { Got1, Eis }, D.Fire, D.Fire);

        // Case-insensitive on username — EQ usernames are case-insensitive server-side.
        failures += AssertSeq("case-variant username dedups",
            new[] { new AccountKey?(new("gotquiz1", "Dalaya")), new("GOTQUIZ1", "Dalaya") },
            D.Fire, D.SkipDuplicate);

        // Case-insensitive on server too.
        failures += AssertSeq("case-variant server dedups",
            new[] { new AccountKey?(new("gotquiz1", "Dalaya")), new("gotquiz1", "dalaya") },
            D.Fire, D.SkipDuplicate);

        // Unresolved (null) slot is skipped as unresolved and does not affect dedup of
        // the resolvable slots around it.
        failures += AssertSeq("null between same logins",
            new[] { Got1, Null, Got1 }, D.Fire, D.SkipUnresolved, D.SkipDuplicate);

        // Empty-username key is treated as unresolved (no backing login).
        failures += AssertSeq("empty-username key is unresolved",
            new[] { new AccountKey?(new("", "Dalaya")) }, D.SkipUnresolved);

        // No false collision: a separator-joined key would risk ("ab","c") == ("a","bc");
        // the tuple key must keep them distinct.
        failures += AssertSeq("no boundary collision (ab|c vs a|bc)",
            new[] { new AccountKey?(new("ab", "c")), new("a", "bc") }, D.Fire, D.Fire);

        // First occurrence wins; a third copy is still a duplicate.
        failures += AssertSeq("triple same login",
            new[] { Got1, Got1, Got1 }, D.Fire, D.SkipDuplicate, D.SkipDuplicate);

        // Empty input is a valid no-op.
        failures += AssertSeq("empty team", Array.Empty<AccountKey?>());

        // ── Step (per-slot primitive FireTeam calls directly) ────────────
        // Drive Step against a running set exactly as FireTeam does, slot by slot.
        {
            var seen = new HashSet<(string, string)>();
            failures += AssertEq("step: first occurrence fires",
                TeamLoginDeduper.Step(seen, new AccountKey("gotquiz1", "Dalaya")), D.Fire);
            failures += AssertEq("step: case-variant repeat is duplicate",
                TeamLoginDeduper.Step(seen, new AccountKey("GOTQUIZ1", "dalaya")), D.SkipDuplicate);
            failures += AssertEq("step: null login is unresolved",
                TeamLoginDeduper.Step(seen, null), D.SkipUnresolved);
            failures += AssertEq("step: distinct login fires",
                TeamLoginDeduper.Step(seen, new AccountKey("eisley", "Dalaya")), D.Fire);
            // The first Fire must have added gotquiz1 to the set (side effect FireTeam relies on).
            failures += AssertEq("step: set now holds both fired logins", seen.Count, 2);
        }

        Console.WriteLine(failures == 0
            ? "TeamLoginDeduperTests: ALL PASSED"
            : $"TeamLoginDeduperTests: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int AssertSeq(string label, IReadOnlyList<AccountKey?> input, params D[] expected)
    {
        var actual = TeamLoginDeduper.Decide(input);
        if (actual.Count == expected.Length && actual.SequenceEqual(expected)) return 0;
        Console.WriteLine($"  FAIL {label}: expected [{string.Join(",", expected)}], " +
                          $"got [{string.Join(",", actual)}]");
        return 1;
    }

    private static int AssertEq<T>(string label, T actual, T expected)
    {
        if (Equals(actual, expected)) return 0;
        Console.WriteLine($"  FAIL {label}: expected '{expected}', got '{actual}'");
        return 1;
    }
}
