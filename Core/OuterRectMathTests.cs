// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;

namespace EQSwitch.Core;

/// <summary>
/// v3.22.45 — unit tests for the slim-titlebar outer-rect math that fixed
/// the Win11 DWM-bleed "vertical seam + desktop sliver" bug.
///
/// Drives <see cref="WindowManager.ComputeOuterRectFromBleeds"/> (the pure
/// math half of <see cref="WindowManager.ComputeSlimTitlebarOuterRect"/>)
/// against three scenarios: Win10 zero-bleed (regression guard so the fix
/// doesn't break the Win10 baseline), Win11 8 px bleed (the actual smoosh
/// scenario observed in the wild), and exotic mixed-bleed values (catches
/// arithmetic flips like swapping leftBleed/rightBleed).
///
/// For every scenario, asserts the four geometric invariants that make the
/// fix correct:
///   1. visible client.Left  == monitor.Left
///   2. visible client.Right == monitor.Right
///   3. visible client.Bottom == monitor.Bottom
///   4. visible client.Top   == monitor.Top + captionVisible
/// Plus titlebarOffset clamping to [0, topBleed].
///
/// Invoked via the --test-outer-rect-math CLI flag from Program.cs.
/// RunAll() returns 0 on all passes, 1 on any assertion failure.
/// </summary>
public static class OuterRectMathTests
{
    public static int RunAll()
    {
        int failures = 0;

        // Case 1 — Win10 baseline (no DWM frame bleed). Slim style on Win10
        // produced a window where stripping WS_THICKFRAME left only the caption
        // (~22 px) as non-client area. Pre-v3.22.45 math worked here, so the
        // fix must reproduce the same outer rect under zero bleed.
        {
            var monitor = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            int titlebarOffset = 22;
            int lB = 0, tB = 22, rB = 0, bB = 0;

            var (x, y, w, h) = WindowManager.ComputeOuterRectFromBleeds(
                monitor, titlebarOffset, lB, tB, rB, bB);

            failures += Assert("win10 x", x, 0);
            failures += Assert("win10 y", y, 0);  // -22 + 22 = 0
            failures += Assert("win10 w", w, 1920);
            failures += Assert("win10 h", h, 1080);  // 1080 + 22 + 0 - 22 = 1080

            // Invariants — visible client should perfectly fill the monitor
            // (titlebarOffset == topBleed means caption fully INSIDE monitor
            // but with zero pixels above and zero below: collapsed). Client.Top
            // = outer.Top + topBleed = 0 + 22 = 22, but only because Win10
            // showed the full caption visible. Acceptable trade in the Win10
            // base case — same as pre-fix.
            int clientLeft = x + lB;
            int clientRight = x + w - rB;
            int clientTop = y + tB;
            int clientBottom = y + h - bB;
            failures += Assert("win10 client.Left == monitor.Left", clientLeft, monitor.Left);
            failures += Assert("win10 client.Right == monitor.Right", clientRight, monitor.Right);
            failures += Assert("win10 client.Bottom == monitor.Bottom", clientBottom, monitor.Bottom);
            failures += Assert("win10 client.Top == monitor.Top + captionVisible", clientTop, monitor.Top + 22);
        }

        // Case 2 — Win11 actual: 8 px frame bleed each side, ~31 px caption.
        // This is the scenario that produced the user-reported "sliver of
        // desktop + 1 px vertical seam" bug. Outer rect must extend BEYOND
        // the monitor on all sides by the bleed amount so the bleed sits
        // off-screen and the visible client area lines up edge-to-edge.
        {
            var monitor = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            int titlebarOffset = 13;
            int lB = 8, tB = 31, rB = 8, bB = 8;

            var (x, y, w, h) = WindowManager.ComputeOuterRectFromBleeds(
                monitor, titlebarOffset, lB, tB, rB, bB);

            // x = 0 - 8 = -8 (left bleed off-screen)
            // y = 0 - 31 + 13 = -18 (18 px of caption above screen, 13 visible)
            // w = 1920 + 8 + 8 = 1936
            // h = 1080 + 31 + 8 - 13 = 1106
            failures += Assert("win11 x", x, -8);
            failures += Assert("win11 y", y, -18);
            failures += Assert("win11 w", w, 1936);
            failures += Assert("win11 h", h, 1106);

            // The four load-bearing invariants: visible client perfectly
            // covers monitor edges except top (which loses captionVisible px).
            int clientLeft = x + lB;
            int clientRight = x + w - rB;
            int clientTop = y + tB;
            int clientBottom = y + h - bB;
            failures += Assert("win11 client.Left == monitor.Left (no left sliver)", clientLeft, monitor.Left);
            failures += Assert("win11 client.Right == monitor.Right (no right sliver)", clientRight, monitor.Right);
            failures += Assert("win11 client.Bottom == monitor.Bottom (no bottom sliver)", clientBottom, monitor.Bottom);
            failures += Assert("win11 client.Top == monitor.Top + captionVisible(13)", clientTop, monitor.Top + 13);
        }

        // Case 3 — titlebarOffset == 0 (user wants max game area, no drag
        // strip). Caption should sit ENTIRELY above the screen edge so the
        // game gets the full monitor height.
        {
            var monitor = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            int titlebarOffset = 0;
            int lB = 8, tB = 31, rB = 8, bB = 8;

            var (x, y, w, h) = WindowManager.ComputeOuterRectFromBleeds(
                monitor, titlebarOffset, lB, tB, rB, bB);

            int clientTop = y + tB;
            int clientBottom = y + h - bB;
            failures += Assert("tOff=0 client.Top == monitor.Top (no caption)", clientTop, monitor.Top);
            failures += Assert("tOff=0 client.Bottom == monitor.Bottom", clientBottom, monitor.Bottom);
            failures += Assert("tOff=0 client.Height == monitor.Height", clientBottom - clientTop, monitor.Bottom - monitor.Top);
        }

        // Case 4 — titlebarOffset > topBleed: clamp to topBleed (above the
        // caption height there's no caption left to show). Without the clamp,
        // a config-supplied 50 would push the outer rect 19 px DOWNWARD on
        // a Win11 31 px caption — chopping 19 px of game off the top.
        {
            var monitor = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            int titlebarOffset = 50;  // > topBleed
            int lB = 8, tB = 31, rB = 8, bB = 8;

            var (x, y, w, h) = WindowManager.ComputeOuterRectFromBleeds(
                monitor, titlebarOffset, lB, tB, rB, bB);

            // captionVisible clamped to 31. Outer rect identical to "user
            // asked for full caption visible".
            int clientTop = y + tB;
            failures += Assert("clamp client.Top == monitor.Top + 31 (clamped, not +50)", clientTop, monitor.Top + 31);
        }

        // Case 5 — titlebarOffset < 0: clamp to 0 (negative would push outer
        // upward + grow height past monitor bottom). Defends against config
        // corruption / hand-edited JSON.
        {
            var monitor = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            int titlebarOffset = -5;
            int lB = 8, tB = 31, rB = 8, bB = 8;

            var (x, y, w, h) = WindowManager.ComputeOuterRectFromBleeds(
                monitor, titlebarOffset, lB, tB, rB, bB);

            int clientTop = y + tB;
            int clientBottom = y + h - bB;
            failures += Assert("neg clamp client.Top == monitor.Top (no caption)", clientTop, monitor.Top);
            failures += Assert("neg clamp client.Bottom == monitor.Bottom", clientBottom, monitor.Bottom);
        }

        // Case 6 — asymmetric bleeds. Catches arithmetic flips (swapping
        // leftBleed/rightBleed) that would pass symmetric tests.
        {
            var monitor = new WinRect { Left = 100, Top = 200, Right = 1300, Bottom = 1000 };
            int titlebarOffset = 5;
            int lB = 3, tB = 28, rB = 11, bB = 7;

            var (x, y, w, h) = WindowManager.ComputeOuterRectFromBleeds(
                monitor, titlebarOffset, lB, tB, rB, bB);

            // x = 100 - 3 = 97
            // y = 200 - 28 + 5 = 177
            // w = 1200 + 3 + 11 = 1214
            // h = 800 + 28 + 7 - 5 = 830
            failures += Assert("asym x", x, 97);
            failures += Assert("asym y", y, 177);
            failures += Assert("asym w", w, 1214);
            failures += Assert("asym h", h, 830);

            // Invariants on non-origin monitor with asymmetric bleeds.
            int clientLeft = x + lB;
            int clientRight = x + w - rB;
            int clientTop = y + tB;
            int clientBottom = y + h - bB;
            failures += Assert("asym client.Left == monitor.Left", clientLeft, monitor.Left);
            failures += Assert("asym client.Right == monitor.Right", clientRight, monitor.Right);
            failures += Assert("asym client.Bottom == monitor.Bottom", clientBottom, monitor.Bottom);
            failures += Assert("asym client.Top == monitor.Top + 5", clientTop, monitor.Top + 5);
        }

        // Case 8 — high-DPI scenario (200% scaling simulation). On a Win11
        // monitor at 200% DPI, AdjustWindowRectEx (when called per-monitor-
        // DPI-aware via AdjustWindowRectExForDpi) returns scaled-up bleed
        // values: lB=tB=rB=bB roughly double. EQSwitch is currently
        // HighDpiMode.SystemAware (see Program.cs) so it always sees system-
        // DPI bleed, but lock the math against future-proof per-monitor-DPI
        // change — the pure math has no DPI awareness of its own and must
        // continue to work correctly when bleed inputs are larger.
        {
            var monitor = new WinRect { Left = 0, Top = 0, Right = 3840, Bottom = 2160 };
            int titlebarOffset = 26;  // doubled from 13
            int lB = 16, tB = 62, rB = 16, bB = 16;  // doubled from 8/31/8/8

            var (x, y, w, h) = WindowManager.ComputeOuterRectFromBleeds(
                monitor, titlebarOffset, lB, tB, rB, bB);

            int clientLeft = x + lB;
            int clientRight = x + w - rB;
            int clientTop = y + tB;
            int clientBottom = y + h - bB;
            failures += Assert("hidpi client.Left == monitor.Left", clientLeft, monitor.Left);
            failures += Assert("hidpi client.Right == monitor.Right", clientRight, monitor.Right);
            failures += Assert("hidpi client.Bottom == monitor.Bottom", clientBottom, monitor.Bottom);
            failures += Assert("hidpi client.Top == monitor.Top + 26", clientTop, monitor.Top + 26);
            failures += Assert("hidpi client.Height == monitor.Height - captionVisible", clientBottom - clientTop, monitor.Bottom - monitor.Top - 26);
        }

        // Case 9 — WS_EX_CLIENTEDGE bleed scenario (live Win11 probe shows
        // exStyle=WS_EX_CLIENTEDGE shifts bleed 8→10 / 31→33 / 8→10 / 8→10).
        // Guards the v3.22.45 post-T3-Opus exStyle-aware fix: even if EQ's
        // actual exStyle returns non-default bleed values, the helper must
        // still land the client edge-to-edge with the monitor.
        {
            var monitor = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            int titlebarOffset = 13;
            int lB = 10, tB = 33, rB = 10, bB = 10;  // WS_EX_CLIENTEDGE delta

            var (x, y, w, h) = WindowManager.ComputeOuterRectFromBleeds(
                monitor, titlebarOffset, lB, tB, rB, bB);

            int clientLeft = x + lB;
            int clientRight = x + w - rB;
            int clientBottom = y + h - bB;
            failures += Assert("clientedge client.Left == monitor.Left", clientLeft, monitor.Left);
            failures += Assert("clientedge client.Right == monitor.Right", clientRight, monitor.Right);
            failures += Assert("clientedge client.Bottom == monitor.Bottom", clientBottom, monitor.Bottom);
        }

        // Case 7 — negative-origin monitor (secondary monitor positioned to
        // the left of primary, common multi-monitor layout). Catches sign
        // errors that would only surface on non-primary monitors.
        {
            var monitor = new WinRect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
            int titlebarOffset = 13;
            int lB = 8, tB = 31, rB = 8, bB = 8;

            var (x, y, w, h) = WindowManager.ComputeOuterRectFromBleeds(
                monitor, titlebarOffset, lB, tB, rB, bB);

            int clientLeft = x + lB;
            int clientRight = x + w - rB;
            int clientBottom = y + h - bB;
            failures += Assert("neg-origin client.Left == -1920", clientLeft, -1920);
            failures += Assert("neg-origin client.Right == 0", clientRight, 0);
            failures += Assert("neg-origin client.Bottom == 1080", clientBottom, 1080);
        }

        Console.WriteLine(failures == 0
            ? $"OuterRectMathTests: ALL PASS"
            : $"OuterRectMathTests: {failures} FAILURE(S)");

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
}
