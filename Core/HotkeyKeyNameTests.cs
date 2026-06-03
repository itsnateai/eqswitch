// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Windows.Forms;
using EQSwitch.Core;
using EQSwitch.UI;

namespace EQSwitch.Core;

/// <summary>
/// Guards the hotkey display↔resolve round-trip. Invoked via the --test-hotkey-keyname
/// CLI flag from Program.cs. RunAll() returns 0 on all passes, 1 on any failure.
///
/// The bug this locks down (reported 2026-06-03): the Switch Key boxes rejected bare
/// keys like "\" / "]", and number-row keys serialized as "D1" — which
/// <see cref="HotkeyManager.ResolveVK"/> can't parse, so the hotkey registered as VK 0
/// and silently never fired. The invariant: for every key a user can press,
/// <c>ResolveVK(SettingsForm.FormatHotkeyKeyName(key))</c> must be non-zero, and the
/// display string must be the canonical name the resolver expects.
/// </summary>
public static class HotkeyKeyNameTests
{
    public static int RunAll()
    {
        int failures = 0;

        // Bare switch keys — the literal keys the user could not enter, plus the defaults.
        failures += AssertRoundTrip("backslash (OemPipe)",       Keys.OemPipe,          "\\");
        failures += AssertRoundTrip("backslash (OemBackslash)",  Keys.OemBackslash,     "\\");
        failures += AssertRoundTrip("close bracket",             Keys.OemCloseBrackets, "]");
        failures += AssertRoundTrip("open bracket",              Keys.OemOpenBrackets,  "[");
        failures += AssertRoundTrip("backquote / tilde",         Keys.Oemtilde,         "`");

        // Number row — arrives as Keys.D0..D9; resolver wants "0".."9" (the latent bug).
        failures += AssertRoundTrip("number row 0", Keys.D0, "0");
        failures += AssertRoundTrip("number row 1", Keys.D1, "1");
        failures += AssertRoundTrip("number row 9", Keys.D9, "9");

        // Letters, function keys, numpad — already round-tripped; pin them so a future
        // edit to FormatHotkeyKeyName can't silently break them.
        failures += AssertRoundTrip("letter G",   Keys.G,       "G");
        failures += AssertRoundTrip("letter Z",   Keys.Z,       "Z");
        failures += AssertRoundTrip("function F9", Keys.F9,     "F9");
        failures += AssertRoundTrip("function F1", Keys.F1,     "F1");
        failures += AssertRoundTrip("numpad 5",   Keys.NumPad5, "NumPad5");
        failures += AssertRoundTrip("Tab",        Keys.Tab,     "Tab");
        failures += AssertRoundTrip("Space",      Keys.Space,   "Space");

        // ── Gate logic (the reported bug): bare keys on a Switch Key box, modifiers elsewhere.
        // bareKey=true mirrors a Switch Key / Global Switch Key box; bareKey=false an action box.

        // The exact failure the user hit: bare "\" / "]" must now be ACCEPTED on a switch box.
        failures += AssertAccepts("bare \\ on switch box", bareKey: true, false, false, false, Keys.OemPipe, "\\");
        failures += AssertAccepts("bare ] on switch box",  bareKey: true, false, false, false, Keys.OemCloseBrackets, "]");
        failures += AssertAccepts("bare letJ on switch box", bareKey: true, false, false, false, Keys.J, "J");

        // What used to be the ONLY thing that worked must keep working.
        failures += AssertAccepts("Ctrl+\\ on switch box", bareKey: true, true, false, false, Keys.OemPipe, "Ctrl+\\");

        // Action boxes (RegisterHotKey, global) must STILL reject bare keys — don't blend.
        failures += AssertRejects("bare \\ on action box", bareKey: false, false, false, false, Keys.OemPipe);
        failures += AssertRejects("bare G on action box",  bareKey: false, false, false, false, Keys.G);

        // Action box modifier combos still build correctly.
        failures += AssertAccepts("Ctrl+Alt+N on action box", bareKey: false, true, true, false, Keys.N, "Ctrl+Alt+N");

        // A bare key the resolver can't turn into a VK is refused (would register as VK 0,
        // never fire) — '/' (OemQuestion) is not in the resolver's table.
        failures += AssertRejects("bare / (unresolvable) on switch box", bareKey: true, false, false, false, Keys.OemQuestion);

        Console.WriteLine(failures == 0
            ? "HotkeyKeyNameTests: all cases PASSED"
            : $"HotkeyKeyNameTests: {failures} assertion failure(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Asserts the display name matches AND resolves to a non-zero VK (the round-trip).
    /// Either half failing means a user-entered key would not actually fire as a hotkey.
    /// </summary>
    private static int AssertRoundTrip(string name, Keys keyCode, string expectedName)
    {
        string actualName = SettingsForm.FormatHotkeyKeyName(keyCode);
        if (actualName != expectedName)
        {
            Console.WriteLine($"    FAIL: {name} display name (expected '{expectedName}', got '{actualName}')");
            return 1;
        }

        uint vk = HotkeyManager.ResolveVK(actualName);
        if (vk == 0)
        {
            Console.WriteLine($"    FAIL: {name} '{actualName}' does not resolve to a VK — hotkey would never fire");
            return 1;
        }

        Console.WriteLine($"    ok: {name} -> '{actualName}' -> VK 0x{vk:X2}");
        return 0;
    }

    /// <summary>Asserts the keypress is accepted and builds the expected binding string.</summary>
    private static int AssertAccepts(string name, bool bareKey, bool ctrl, bool alt, bool shift,
        Keys keyCode, string expected)
    {
        bool ok = SettingsForm.TryBuildHotkeyString(bareKey, ctrl, alt, shift, keyCode, out string result);
        if (!ok)
        {
            Console.WriteLine($"    FAIL: {name} was REJECTED (expected accept -> '{expected}')");
            return 1;
        }
        if (result != expected)
        {
            Console.WriteLine($"    FAIL: {name} built '{result}' (expected '{expected}')");
            return 1;
        }
        Console.WriteLine($"    ok: {name} -> accepted '{result}'");
        return 0;
    }

    /// <summary>Asserts the keypress is ignored (no binding produced).</summary>
    private static int AssertRejects(string name, bool bareKey, bool ctrl, bool alt, bool shift, Keys keyCode)
    {
        bool ok = SettingsForm.TryBuildHotkeyString(bareKey, ctrl, alt, shift, keyCode, out string result);
        if (ok)
        {
            Console.WriteLine($"    FAIL: {name} was ACCEPTED as '{result}' (expected reject)");
            return 1;
        }
        Console.WriteLine($"    ok: {name} -> rejected");
        return 0;
    }
}
