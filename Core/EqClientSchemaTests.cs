// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// Phase 0 guard for the EQ Client Settings overhaul
/// (docs/specs/2026-06-06-eqclient-settings-overhaul.md). Validates the
/// <see cref="EqClientIniSchema"/> descriptor table's internal consistency so a typo in the
/// 125-row table fails the build instead of corrupting a live eqclient.ini once it's wired in.
///
/// Invoked via the <c>--test-eqclient-schema</c> CLI flag from Program.cs (DEBUG only, like the
/// other Core tests). RunAll() returns 0 if every invariant holds, 1 if any row is malformed,
/// 2 on an unexpected crash (set by the Program.cs dispatcher).
///
/// What it does NOT check: semantic correctness of polarity/section against EQ runtime behaviour
/// (e.g. "is AANoConfirm really 1=skip?"). That is verified per-window during wiring (Phases 1-6)
/// plus live smoke — this test only proves the table is structurally sound and round-trips.
/// </summary>
public static class EqClientSchemaTests
{
    // Sanity anchor: the documented total across all 6 windows (34+32+22+16+12+9).
    private const int ExpectedCount = 125;

    public static int RunAll()
    {
        var all = EqClientIniSchema.All;
        var errors = new List<string>();

        // 1. Count matches the documented inventory (catches an accidental drop/dupe of a row).
        if (all.Count != ExpectedCount)
            errors.Add($"expected {ExpectedCount} settings, found {all.Count}");

        // 2. No two settings collide on (Section, Key) — that would make read/write ambiguous.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in all)
        {
            string id = $"{s.Section}::{s.Key}";
            if (!seen.Add(id))
                errors.Add($"duplicate (section,key): {id}");
        }

        // 3. Per-setting invariants.
        foreach (var s in all)
        {
            string where = $"{s.Section}::{s.Key}";

            if (string.IsNullOrWhiteSpace(s.Key))
                errors.Add($"{where}: empty Key");
            if (string.IsNullOrWhiteSpace(s.Section))
                errors.Add($"{where}: empty Section");
            if (string.IsNullOrWhiteSpace(s.Label))
                errors.Add($"{where}: empty Label");

            switch (s.Kind)
            {
                case IniKind.Toggle:
                    ValidateToggle(s, where, errors);
                    break;
                case IniKind.Number:
                case IniKind.KeyCode:
                    ValidateNumber(s, where, errors);
                    break;
                default:
                    errors.Add($"{where}: unknown Kind {s.Kind}");
                    break;
            }
        }

        if (errors.Count == 0)
        {
            Console.WriteLine($"EqClientSchemaTests: PASS — {all.Count} settings, all invariants hold "
                + $"({all.Count(x => x.Kind == IniKind.Toggle)} toggles, "
                + $"{all.Count(x => x.Kind == IniKind.Number)} numbers, "
                + $"{all.Count(x => x.Kind == IniKind.KeyCode)} keycodes; "
                + $"{all.Count(x => x.Bucket == Bucket.Operational)} operational, "
                + $"{all.Count(x => x.Bucket == Bucket.HardPush)} hard-push).");
            return 0;
        }

        Console.Error.WriteLine($"EqClientSchemaTests: FAIL — {errors.Count} problem(s):");
        foreach (var e in errors)
            Console.Error.WriteLine("  - " + e);
        return 1;
    }

    private static void ValidateToggle(IniSetting s, string where, List<string> errors)
    {
        if (string.IsNullOrEmpty(s.On) || string.IsNullOrEmpty(s.Off))
        {
            errors.Add($"{where}: toggle is missing On/Off");
            return;
        }
        if (string.Equals(s.On, s.Off, StringComparison.OrdinalIgnoreCase))
            errors.Add($"{where}: toggle On==Off ('{s.On}')");

        // The ship Default must be a value the toggle can actually produce — otherwise the control
        // could never display the default state, and "off restores default" would be impossible.
        bool defaultIsWritable =
            string.Equals(s.Default, s.On, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Default, s.Off, StringComparison.OrdinalIgnoreCase);
        if (!defaultIsWritable)
            errors.Add($"{where}: Default '{s.Default}' is neither On('{s.On}') nor Off('{s.Off}')");

        // Round-trip: reading the Default then writing it back must be identity. This is the
        // structural defense against the inversion-bug class — read and write share On/Off.
        string roundTrip = s.ToggleToIni(s.ToggleFromIni(s.Default));
        if (!string.Equals(roundTrip, s.Default, StringComparison.OrdinalIgnoreCase) && defaultIsWritable)
            errors.Add($"{where}: toggle round-trip '{s.Default}' -> '{roundTrip}'");
    }

    private static void ValidateNumber(IniSetting s, string where, List<string> errors)
    {
        if (s.Min > s.Max)
            errors.Add($"{where}: Min({s.Min}) > Max({s.Max})");
        if (s.Decimals < 0)
            errors.Add($"{where}: negative Decimals");

        if (!decimal.TryParse(s.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
        {
            errors.Add($"{where}: Default '{s.Default}' is not numeric");
            return;
        }
        if (d < s.Min || d > s.Max)
            errors.Add($"{where}: Default {d} outside [{s.Min}, {s.Max}]");

        // Round-trip: parse(Default) -> format must equal format(Default-as-decimal). Catches a
        // Default whose written form differs from its canonical form (e.g. "2.8" vs "2.800000").
        string normViaParse = s.NumberToIni(s.ParseNumber(s.Default));
        string normDirect = s.NumberToIni(d);
        if (normViaParse != normDirect)
            errors.Add($"{where}: number round-trip '{s.Default}' -> '{normViaParse}' (expected '{normDirect}')");

        // A Default written verbatim should match its canonical form so eqclient_master.ini and the
        // descriptor agree byte-for-byte (no spurious rewrite when nothing changed).
        if (normDirect != s.Default)
            errors.Add($"{where}: Default '{s.Default}' is not in canonical form ('{normDirect}')");
    }
}
