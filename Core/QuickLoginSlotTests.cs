// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// Unit tests for <see cref="QuickLoginSlot"/> — the typed storage format for the four
/// Quick Login slots (v3.23.0). Pure value logic, no WinForms. Invoked via the
/// --test-quicklogin CLI flag from Program.cs. RunAll() returns 0 on all passes, 1 on any
/// assertion failure; Program.cs maps unhandled exceptions to exit code 2.
///
/// Guards the contract the tray dispatch (TrayManager.FireLegacyQuickLoginSlot) and the
/// settings UI both depend on: char:/acct: round-trip, bare-value back-compat, and the
/// "—" empty display.
/// </summary>
public static class QuickLoginSlotTests
{
    public static int RunAll()
    {
        int failures = 0;

        // Build helpers produce the documented prefixes.
        failures += AssertEq("ForCharacter", QuickLoginSlot.ForCharacter("Natedogg"), "char:Natedogg");
        failures += AssertEq("ForAccount", QuickLoginSlot.ForAccount("gotquiz"), "acct:gotquiz");

        // Parse — typed values.
        {
            var (k, n) = QuickLoginSlot.Parse("char:Natedogg");
            failures += AssertEq("parse char kind", k, QuickLoginSlot.Kind.Character);
            failures += AssertEq("parse char name", n, "Natedogg");
        }
        {
            var (k, n) = QuickLoginSlot.Parse("acct:gotquiz");
            failures += AssertEq("parse acct kind", k, QuickLoginSlot.Kind.Account);
            failures += AssertEq("parse acct name", n, "gotquiz");
        }

        // Parse — empty / null → Empty.
        {
            var (k, n) = QuickLoginSlot.Parse("");
            failures += AssertEq("parse empty kind", k, QuickLoginSlot.Kind.Empty);
            failures += AssertEq("parse empty name", n, "");
        }
        {
            var (k, _) = QuickLoginSlot.Parse(null);
            failures += AssertEq("parse null kind", k, QuickLoginSlot.Kind.Empty);
        }

        // Parse — un-prefixed (pre-v3.23 / hand-edit) → LegacyBare, name preserved verbatim.
        {
            var (k, n) = QuickLoginSlot.Parse("Eisley");
            failures += AssertEq("parse legacy kind", k, QuickLoginSlot.Kind.LegacyBare);
            failures += AssertEq("parse legacy name", n, "Eisley");
        }

        // Round-trip: build → parse recovers kind + exact name (incl. names with a colon).
        {
            var (k, n) = QuickLoginSlot.Parse(QuickLoginSlot.ForCharacter("Na:te"));
            failures += AssertEq("roundtrip char kind", k, QuickLoginSlot.Kind.Character);
            failures += AssertEq("roundtrip char name (colon preserved)", n, "Na:te");
        }

        // DisplayName — empty shows the em-dash placeholder; typed shows the bare name;
        // legacy shows itself.
        failures += AssertEq("display empty", QuickLoginSlot.DisplayName(""), "—");
        failures += AssertEq("display char", QuickLoginSlot.DisplayName("char:Natedogg"), "Natedogg");
        failures += AssertEq("display acct", QuickLoginSlot.DisplayName("acct:gotquiz"), "gotquiz");
        failures += AssertEq("display legacy", QuickLoginSlot.DisplayName("Eisley"), "Eisley");

        Console.WriteLine(failures == 0
            ? "QuickLoginSlotTests: ALL PASSED"
            : $"QuickLoginSlotTests: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int AssertEq<T>(string label, T actual, T expected)
    {
        if (Equals(actual, expected)) return 0;
        Console.WriteLine($"  FAIL {label}: expected '{expected}', got '{actual}'");
        return 1;
    }
}
