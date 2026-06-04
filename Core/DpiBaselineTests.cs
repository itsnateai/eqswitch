// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace EQSwitch.Core;

/// <summary>
/// DPI regression guard: every concrete Form in the EQSwitch.UI namespace MUST inherit
/// <c>EqSwitchForm</c> (the 96-DPI AutoScale baseline) — except documented self-positioning
/// overlays. Type-level reflection (no instantiation) so it can never be flaky. Invoked via
/// the <c>--test-dpi-baseline</c> CLI flag from Program.cs, alongside the other Core tests.
/// RunAll() returns 0 if every form is baselined, 1 if any offender is found.
///
/// This is what makes the DPI retrofit a fix and not a one-time patch: a new form added later
/// that forgets <c>: EqSwitchForm</c> trips this test instead of silently clipping at 125%+.
///
/// LIMITATION: catches named Form CLASSES only. Inline <c>new Form()</c> instances (the monitor
/// overlay, Window Offsets, Wrapper Settings, Help) cannot be reflected as types — they route
/// through <c>DarkTheme.StyleForm</c>, which sets the same baseline, and are covered by code
/// review rather than this test. The EQSwitch.UI namespace is resolved by reference (EqSwitchForm
/// is looked up by string) so this Core test stays decoupled from the UI layer at compile time.
/// </summary>
public static class DpiBaselineTests
{
    // Self-positioning forms that intentionally bypass the baseline (screen-coordinate geometry
    // would break under auto-scaling). EqSwitchForm itself is the base, not a leaf.
    private static readonly string[] Excluded = { "PipOverlay", "TooltipForm", "CurtainForm", "EqSwitchForm" };

    public static int RunAll()
    {
        var asm = Assembly.GetExecutingAssembly();
        var baseType = asm.GetType("EQSwitch.UI.EqSwitchForm");
        if (baseType == null)
        {
            Console.Error.WriteLine("DpiBaselineTests: FAIL — EQSwitch.UI.EqSwitchForm type not found.");
            return 1;
        }

        var offenders = asm.GetTypes()
            .Where(t => t.Namespace == "EQSwitch.UI"
                     && !t.IsAbstract
                     && typeof(Form).IsAssignableFrom(t)
                     && !baseType.IsAssignableFrom(t)
                     && !Excluded.Contains(t.Name))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (offenders.Count == 0)
        {
            Console.WriteLine("DpiBaselineTests: PASS — every UI Form inherits EqSwitchForm.");
            return 0;
        }

        Console.Error.WriteLine("DpiBaselineTests: FAIL — these UI Forms must inherit EqSwitchForm "
                              + "(or be added to the Excluded self-positioning list):");
        foreach (var name in offenders)
            Console.Error.WriteLine("  - " + name);
        return 1;
    }
}
