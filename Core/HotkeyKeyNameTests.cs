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

        // Round-trip cases pin the EXACT VK (not just non-zero) so a future hex typo in
        // KeyNameToVK can't slip through (v3.24.23 — closes the verifier "test only checks !=0" gap).

        // Bare switch keys — the literal keys the user could not enter, plus the defaults.
        failures += AssertRoundTrip("backslash (OemPipe)",       Keys.OemPipe,          "\\", 0xDC);
        failures += AssertRoundTrip("backslash (OemBackslash)",  Keys.OemBackslash,     "\\", 0xDC);
        failures += AssertRoundTrip("close bracket",             Keys.OemCloseBrackets, "]",  0xDD);
        failures += AssertRoundTrip("open bracket",              Keys.OemOpenBrackets,  "[",  0xDB);
        failures += AssertRoundTrip("backquote / tilde",         Keys.Oemtilde,         "`",  0xC0);

        // Number row — arrives as Keys.D0..D9; resolver wants "0".."9" (the latent bug).
        failures += AssertRoundTrip("number row 0", Keys.D0, "0", 0x30);
        failures += AssertRoundTrip("number row 1", Keys.D1, "1", 0x31);
        failures += AssertRoundTrip("number row 9", Keys.D9, "9", 0x39);

        // Letters, function keys, numpad — pin so a future edit can't silently break them.
        failures += AssertRoundTrip("letter G",    Keys.G,       "G",       0x47);
        failures += AssertRoundTrip("letter Z",    Keys.Z,       "Z",       0x5A);
        failures += AssertRoundTrip("function F9", Keys.F9,      "F9",      0x78);
        failures += AssertRoundTrip("function F1", Keys.F1,      "F1",      0x70);
        failures += AssertRoundTrip("numpad 5",    Keys.NumPad5, "NumPad5", 0x65);
        failures += AssertRoundTrip("Tab",         Keys.Tab,     "Tab",     0x09);
        failures += AssertRoundTrip("Space",       Keys.Space,   "Space",   0x20);

        // v3.24.22 — OEM punctuation, arrows/nav, extended F-keys.
        failures += AssertRoundTrip("semicolon",    Keys.OemSemicolon, ";",    0xBA);
        failures += AssertRoundTrip("equals",       Keys.Oemplus,      "=",    0xBB);
        failures += AssertRoundTrip("comma",        Keys.Oemcomma,     ",",    0xBC);
        failures += AssertRoundTrip("minus",        Keys.OemMinus,     "-",    0xBD);
        failures += AssertRoundTrip("period",       Keys.OemPeriod,    ".",    0xBE);
        failures += AssertRoundTrip("slash",        Keys.OemQuestion,  "/",    0xBF);
        failures += AssertRoundTrip("apostrophe",   Keys.OemQuotes,    "'",    0xDE);
        failures += AssertRoundTrip("arrow Left",   Keys.Left,         "Left", 0x25);
        failures += AssertRoundTrip("arrow Down",   Keys.Down,         "Down", 0x28);
        failures += AssertRoundTrip("Home",         Keys.Home,         "Home", 0x24);
        failures += AssertRoundTrip("function F13", Keys.F13,          "F13",  0x7C);
        failures += AssertRoundTrip("function F24", Keys.F24,          "F24",  0x87);

        // v3.24.23 #4 — PrintScreen + numpad operators (own VKs, not NumLock-dependent).
        failures += AssertRoundTrip("PrintScreen",   Keys.PrintScreen, "PrintScreen", 0x2C);
        failures += AssertRoundTrip("numpad *",      Keys.Multiply,    "Multiply",    0x6A);
        failures += AssertRoundTrip("numpad +",      Keys.Add,         "Add",         0x6B);
        failures += AssertRoundTrip("numpad -",      Keys.Subtract,    "Subtract",    0x6D);
        failures += AssertRoundTrip("numpad /",      Keys.Divide,      "Divide",      0x6F);
        failures += AssertRoundTrip("numpad .",      Keys.Decimal,     "Decimal",     0x6E);

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

        // v3.24.22: '/' now resolves (VK_OEM_2) so it's a VALID bare switch key.
        failures += AssertAccepts("bare / on switch box", bareKey: true, false, false, false, Keys.OemQuestion, "/");

        // A key the resolver still can't map is refused — would register as VK 0 and never fire.
        // The Windows key (LWin) has no entry and shouldn't (OS-reserved). Verifies the loud-refusal
        // for BOTH a bare switch key AND a modifier combo (the v3.24.22 universal-refusal change).
        failures += AssertRejects("bare LWin (unresolvable) on switch box", bareKey: true, false, false, false, Keys.LWin);
        failures += AssertRejects("Ctrl+LWin (unresolvable) on action box", bareKey: false, true, false, false, Keys.LWin);

        // ── v3.24.23 #2 — NumLock-independent numpad. A numpad-origin nav VK (extended flag CLEAR)
        // normalizes back to its NumPad VK so the binding fires regardless of NumLock; a DEDICATED
        // nav key (extended SET) is left alone so it can't trigger a numpad binding.
        failures += AssertNormalize("numpad8 (Up, NumLock off)",    0x26, extended: false, 0x68);
        failures += AssertNormalize("numpad5 (Clear, NumLock off)", 0x0C, extended: false, 0x65);
        failures += AssertNormalize("numpad0 (Insert, NumLock off)",0x2D, extended: false, 0x60);
        failures += AssertNormalize("numpad. (Delete, NumLock off)",0x2E, extended: false, 0x6E);
        failures += AssertNormalize("dedicated Up arrow stays Up",  0x26, extended: true,  0x26);
        failures += AssertNormalize("dedicated Home stays Home",    0x24, extended: true,  0x24);
        failures += AssertNormalize("numpad8 NumLock-on passes thru",0x68, extended: false, 0x68);
        failures += AssertNormalize("backslash (non-nav) unaffected",0xDC, extended: false, 0xDC);

        Console.WriteLine(failures == 0
            ? "HotkeyKeyNameTests: all cases PASSED"
            : $"HotkeyKeyNameTests: {failures} assertion failure(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Asserts the display name matches AND resolves to the EXACT expected VK (the round-trip).
    /// Pinning the VK (not just non-zero) catches a hex typo in KeyNameToVK that would bind the
    /// wrong physical key. Either half failing means the user-entered key would misfire or not fire.
    /// </summary>
    private static int AssertRoundTrip(string name, Keys keyCode, string expectedName, uint expectedVk)
    {
        string actualName = SettingsForm.FormatHotkeyKeyName(keyCode);
        if (actualName != expectedName)
        {
            Console.WriteLine($"    FAIL: {name} display name (expected '{expectedName}', got '{actualName}')");
            return 1;
        }

        uint vk = HotkeyManager.ResolveVK(actualName);
        if (vk != expectedVk)
        {
            Console.WriteLine($"    FAIL: {name} '{actualName}' resolved to VK 0x{vk:X2} (expected 0x{expectedVk:X2})");
            return 1;
        }

        Console.WriteLine($"    ok: {name} -> '{actualName}' -> VK 0x{vk:X2}");
        return 0;
    }

    /// <summary>
    /// Asserts the NumLock-independent numpad normalization: a numpad-origin nav VK (extended
    /// flag CLEAR) maps to its NumPad VK; a dedicated nav key (extended SET) and any non-nav VK
    /// pass through unchanged.
    /// </summary>
    private static int AssertNormalize(string name, uint vkIn, bool extended, uint expected)
    {
        uint flags = extended ? 0x01u : 0x00u;
        uint actual = KeyboardHookManager.NormalizeNumpadVk(vkIn, flags);
        if (actual != expected)
        {
            Console.WriteLine($"    FAIL: {name} normalized 0x{vkIn:X2} -> 0x{actual:X2} (expected 0x{expected:X2})");
            return 1;
        }
        Console.WriteLine($"    ok: {name} -> 0x{actual:X2}");
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
