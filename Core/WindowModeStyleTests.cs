// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// v3.22.81 — unit tests for WindowManager.DesiredSlimStyle, the WindowMode →
/// GWL_STYLE mapping that splits Fullscreen (WS_POPUP, no caption) from Windowed
/// (WS_CAPTION kept, thin border). Invoked via --test-window-mode-style.
/// RunAll() returns 0 on all passes, 1 on any failure.
/// </summary>
public static class WindowModeStyleTests
{
    public static int RunAll()
    {
        int failures = 0;
        long baseStyle = NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU;

        // Fullscreen → strip caption+thickframe+sysmenu, set WS_POPUP.
        long fs = WindowManager.DesiredSlimStyle(baseStyle, WindowMode.Fullscreen);
        failures += Assert("fs has WS_POPUP", (fs & NativeMethods.WS_POPUP) != 0, true);
        failures += Assert("fs no WS_CAPTION", (fs & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION, false);
        failures += Assert("fs no WS_THICKFRAME", (fs & NativeMethods.WS_THICKFRAME) != 0, false);

        // Windowed → strip thickframe only, KEEP caption+sysmenu, NO WS_POPUP.
        long w = WindowManager.DesiredSlimStyle(baseStyle, WindowMode.Windowed);
        failures += Assert("win keeps WS_CAPTION", (w & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION, true);
        failures += Assert("win keeps WS_SYSMENU", (w & NativeMethods.WS_SYSMENU) != 0, true);
        failures += Assert("win no WS_THICKFRAME", (w & NativeMethods.WS_THICKFRAME) != 0, false);
        failures += Assert("win no WS_POPUP", (w & NativeMethods.WS_POPUP) != 0, false);

        // ProbeStyleFor returns the right canonical style per mode.
        failures += Assert("probe windowed has caption",
            (WindowManager.ProbeStyleFor(WindowMode.Windowed) & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION, true);
        failures += Assert("probe fullscreen has popup",
            (WindowManager.ProbeStyleFor(WindowMode.Fullscreen) & NativeMethods.WS_POPUP) != 0, true);

        Console.WriteLine(failures == 0
            ? "WindowModeStyleTests: all PASSED"
            : $"WindowModeStyleTests: {failures} assertion failure(s)");
        return failures == 0 ? 0 : 1;
    }

    private static int Assert<T>(string name, T actual, T expected)
    {
        if (Equals(actual, expected)) { Console.WriteLine($"    ok: {name}"); return 0; }
        Console.WriteLine($"    FAIL: {name} (expected '{expected}', got '{actual}')");
        return 1;
    }
}
