// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Linq;
using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// Phase 1 guard for the EQ Client Settings overhaul: proves <see cref="EqClientIniDocument"/>
/// reads/writes the correct section, scopes Get to a section, updates a key in place (never
/// duplicates), inserts a NEW key inside its section (never at EOF — the placement bug Nate
/// flagged from the fresh-INI dig), creates a missing section, and mirrors dual-section keys.
/// Runs against an embedded fixture so it's self-contained / CI-safe. CLI: --test-eqclient-inidoc.
/// </summary>
public static class EqClientIniDocumentTests
{
    public static int RunAll()
    {
        var errors = new List<string>();

        // Mirrors the real eqclient.ini shape: keys in distinct sections, in order.
        string[] fixture =
        {
            "[Defaults]",
            "Sound=TRUE",
            "MipMapping=FALSE",
            "[Options]",
            "Sky=1",
            "MaxFPS=100",
            "[VideoMode]",
            "Width=1920",
        };

        var doc = new EqClientIniDocument(fixture);

        // Get: present / absent / section-scoped.
        Expect(errors, doc.Get("Defaults", "Sound") == "TRUE", "Get returns present value");
        Expect(errors, doc.Get("Defaults", "Missing") == null, "Get returns null for absent key");
        Expect(errors, doc.Get("Defaults", "Sky") == null, "Get is section-scoped (Sky lives in [Options])");
        Expect(errors, doc.Get("Options", "Sky") == "1", "Get reads from the correct section");

        // Set: update in place, no duplicate line.
        doc.Set("Defaults", "Sound", "FALSE");
        Expect(errors, doc.Get("Defaults", "Sound") == "FALSE", "Set updates an existing key in place");
        Expect(errors, doc.Lines.Count(l => l.TrimStart().StartsWith("Sound=", StringComparison.OrdinalIgnoreCase)) == 1,
            "Set does not duplicate the key");

        // Set: a NEW key lands inside its section (before the next header), NOT at EOF.
        doc.Set("Defaults", "EnvSounds", "0");
        int envIdx = IndexOf(doc, "EnvSounds=0");
        int optHeaderIdx = IndexOf(doc, "[Options]");
        Expect(errors, envIdx >= 0 && optHeaderIdx >= 0 && envIdx < optHeaderIdx,
            "new key inserts inside [Defaults] (before [Options]), not at EOF");

        // Set: a brand-new section is created when absent.
        doc.Set("KeyMaps", "KEYMAPPING_TEST", "5");
        Expect(errors, doc.Get("KeyMaps", "KEYMAPPING_TEST") == "5", "Set creates a missing section");

        // Write: dual-section mirror — MaxFPS is canonical [Options] with a [Defaults] mirror.
        var maxFps = EqClientIniSchema.All.FirstOrDefault(s => s.Key == "MaxFPS");
        if (maxFps == null)
        {
            errors.Add("schema is missing MaxFPS (mirror test cannot run)");
        }
        else
        {
            doc.Write(maxFps, "60");
            Expect(errors, doc.Get("Options", "MaxFPS") == "60", "mirror Write hits canonical [Options]");
            Expect(errors, doc.Get("Defaults", "MaxFPS") == "60", "mirror Write hits the [Defaults] mirror");
        }

        if (errors.Count == 0)
        {
            Console.WriteLine("EqClientIniDocumentTests: PASS — section-aware get/set/insert/create/mirror all correct.");
            return 0;
        }

        Console.Error.WriteLine($"EqClientIniDocumentTests: FAIL — {errors.Count} problem(s):");
        foreach (var e in errors)
            Console.Error.WriteLine("  - " + e);
        return 1;
    }

    private static void Expect(List<string> errors, bool condition, string what)
    {
        if (!condition) errors.Add(what);
    }

    private static int IndexOf(EqClientIniDocument doc, string needleTrimmed)
    {
        for (int i = 0; i < doc.Lines.Count; i++)
            if (doc.Lines[i].Trim().Equals(needleTrimmed, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
