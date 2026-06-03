// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Linq;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Unit tests for <see cref="TeamSlotResolver"/> — the typed/legacy-bare team-slot routing shared
/// by <c>TrayManager.FireTeam</c> and the three Teams display paths (v3.24.15). Pure value logic,
/// no WinForms. Invoked via the --test-team-slot-resolver CLI flag from Program.cs. RunAll()
/// returns 0 on all passes, 1 on any assertion failure; Program.cs maps unhandled exceptions to 2.
///
/// Guards the fix for "eisley account hidden in Configure Teams": an Account must stay
/// independently selectable (acct:Name → charselect) even when a Character shares its name
/// (char:Name → enter world), and pre-v3.24.15 bare slots must still resolve Character-first.
/// </summary>
public static class TeamSlotResolverTests
{
    public static int RunAll()
    {
        int failures = 0;

        // Mirrors Nate's live config: Character "Eisley" backed by account "eisley", plus the
        // "eisley" Account itself — the pair that used to collapse into one entry. "onlyacct" is an
        // account with no same-name character.
        var characters = new List<Character>
        {
            new() { Name = "Eisley", AccountUsername = "eisley" },
            new() { Name = "Solo",   AccountUsername = "solo" },
        };
        var accounts = new List<Account>
        {
            new() { Name = "eisley",   Username = "eisley" },
            new() { Name = "onlyacct", Username = "onlyacct" },
        };
        Character? FindChar(string n) => characters.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        Account?   FindAcct(string n) => accounts.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        (Character? c, Account? a) R(string? v) => TeamSlotResolver.Resolve(v, FindChar, FindAcct);

        // ── typed: char: → Character only ────────────────────────────────
        {
            var (c, a) = R(QuickLoginSlot.ForCharacter("Eisley"));
            failures += AssertTrue("char:Eisley → Character Eisley", c?.Name == "Eisley" && a == null);
        }
        // ── typed: acct: → Account only (THE BUG FIX — not shadowed by the same-name char) ──
        {
            var (c, a) = R(QuickLoginSlot.ForAccount("eisley"));
            failures += AssertTrue("acct:eisley → Account eisley (not the char)", a?.Name == "eisley" && c == null);
        }
        // char: never returns an Account even when a same-name account exists.
        {
            var (c, a) = R(QuickLoginSlot.ForCharacter("eisley"));
            failures += AssertTrue("char:eisley resolves char, never account", c?.Name == "Eisley" && a == null);
        }
        // acct: never returns a Character even when a same-name character exists.
        {
            var (c, a) = R(QuickLoginSlot.ForAccount("Eisley"));
            failures += AssertTrue("acct:Eisley resolves account, never char", a?.Name == "eisley" && c == null);
        }

        // ── legacy bare: Character-first, Account fallback (unchanged behavior) ──
        {
            var (c, a) = R("Eisley");
            failures += AssertTrue("bare Eisley → Character-first", c?.Name == "Eisley" && a == null);
        }
        {
            var (c, a) = R("onlyacct");
            failures += AssertTrue("bare onlyacct → Account fallback", a?.Name == "onlyacct" && c == null);
        }

        // ── unresolved typed targets → neither ───────────────────────────
        {
            var (c, a) = R(QuickLoginSlot.ForCharacter("Ghost"));
            failures += AssertTrue("char:Ghost (no such char) → neither", c == null && a == null);
        }
        {
            var (c, a) = R(QuickLoginSlot.ForAccount("Ghost"));
            failures += AssertTrue("acct:Ghost (no such acct) → neither", c == null && a == null);
        }

        // ── empty / null → neither ───────────────────────────────────────
        {
            var (c, a) = R("");
            failures += AssertTrue("empty → neither", c == null && a == null);
        }
        {
            var (c, a) = R(null);
            failures += AssertTrue("null → neither", c == null && a == null);
        }

        Console.WriteLine(failures == 0
            ? "TeamSlotResolverTests: ALL PASSED"
            : $"TeamSlotResolverTests: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int AssertTrue(string label, bool ok)
    {
        if (ok) return 0;
        Console.WriteLine($"  FAIL {label}");
        return 1;
    }
}
