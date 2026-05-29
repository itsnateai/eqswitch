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
///   1. client.Left  == monitor.Left  (sides flush)
///   2. client.Right == monitor.Right (sides flush)
///   3. client renders at NATIVE monH — caption peeks at top, bottom overflows
///      off-screen by captionVisible (the WinEQ2 method, v3.22.81)
///   4. client.Top   == monitor.Top + captionVisible
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
            failures += Assert("win10 h", h, 1102);  // v3.22.81: 1080 + 22 + 0 = 1102 (native client + bottom overflow)

            // v3.22.81 invariants — client renders at NATIVE size; the caption
            // peeks captionVisible px at the top and the client overflows the
            // bottom edge by the same amount (WinEQ2 method) so EQ renders 1:1.
            int clientLeft = x + lB;
            int clientRight = x + w - rB;
            int clientTop = y + tB;
            int clientBottom = y + h - bB;
            failures += Assert("win10 client.Left == monitor.Left", clientLeft, monitor.Left);
            failures += Assert("win10 client.Right == monitor.Right", clientRight, monitor.Right);
            failures += Assert("win10 client.Top == monitor.Top + captionVisible", clientTop, monitor.Top + 22);
            failures += Assert("win10 client.Bottom overflows by captionVisible", clientBottom, monitor.Bottom + 22);
            failures += Assert("win10 client height == native monH", clientBottom - clientTop, monitor.Bottom - monitor.Top);
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
            // h = 1080 + 31 + 8 = 1119 (v3.22.81: native client + bottom overflow)
            failures += Assert("win11 x", x, -8);
            failures += Assert("win11 y", y, -18);
            failures += Assert("win11 w", w, 1936);
            failures += Assert("win11 h", h, 1119);

            // v3.22.81 invariants: sides flush, caption peeks 13px at top, client
            // renders at NATIVE size (overflows the bottom by captionVisible) so
            // EQ's DX swap chain is 1:1 → crisp fonts.
            int clientLeft = x + lB;
            int clientRight = x + w - rB;
            int clientTop = y + tB;
            int clientBottom = y + h - bB;
            failures += Assert("win11 client.Left == monitor.Left (no left sliver)", clientLeft, monitor.Left);
            failures += Assert("win11 client.Right == monitor.Right (no right sliver)", clientRight, monitor.Right);
            failures += Assert("win11 client.Top == monitor.Top + captionVisible(13)", clientTop, monitor.Top + 13);
            failures += Assert("win11 client.Bottom overflows by captionVisible(13)", clientBottom, monitor.Bottom + 13);
            failures += Assert("win11 client height == native monH", clientBottom - clientTop, monitor.Bottom - monitor.Top);
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
            // h = 800 + 28 + 7 = 835 (v3.22.81: native client + bottom overflow)
            failures += Assert("asym x", x, 97);
            failures += Assert("asym y", y, 177);
            failures += Assert("asym w", w, 1214);
            failures += Assert("asym h", h, 835);

            // Invariants on non-origin monitor with asymmetric bleeds.
            int clientLeft = x + lB;
            int clientRight = x + w - rB;
            int clientTop = y + tB;
            int clientBottom = y + h - bB;
            failures += Assert("asym client.Left == monitor.Left", clientLeft, monitor.Left);
            failures += Assert("asym client.Right == monitor.Right", clientRight, monitor.Right);
            failures += Assert("asym client.Top == monitor.Top + 5", clientTop, monitor.Top + 5);
            failures += Assert("asym client.Bottom overflows by captionVisible(5)", clientBottom, monitor.Bottom + 5);
            failures += Assert("asym client height == native monH", clientBottom - clientTop, monitor.Bottom - monitor.Top);
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
            failures += Assert("hidpi client.Top == monitor.Top + 26", clientTop, monitor.Top + 26);
            failures += Assert("hidpi client.Bottom overflows by captionVisible(26)", clientBottom, monitor.Bottom + 26);
            failures += Assert("hidpi client.Height == native monH", clientBottom - clientTop, monitor.Bottom - monitor.Top);
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
            failures += Assert("clientedge client.Bottom overflows by captionVisible(13)", clientBottom, monitor.Bottom + 13);
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
            failures += Assert("neg-origin client.Bottom overflows by captionVisible(13)", clientBottom, 1080 + 13);
        }

        // v3.22.46 — ClampBleedsForAdjacency: bleed extension goes to 0 on
        // edges where another monitor abuts. Pre-v3.22.46 the outer rect
        // extended past every edge by `bleed`, which on a multi-monitor
        // setup landed visibly on the adjacent monitor (Nate's 2026-05-25
        // "bleeds onto right monitor" report). The new helper clips the
        // extension to 0 on adjacent edges, returning the effective bleed
        // values that ComputeOuterRectFromBleeds gets.

        // Case A — single monitor (no neighbors): all bleeds pass through.
        {
            var primary = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var all = new List<WinRect> { primary };
            var (effL, effR, effB) = WindowManager.ClampBleedsForAdjacency(primary, all, 8, 8, 8);
            failures += Assert("adj single-monitor effLeft passthrough", effL, 8);
            failures += Assert("adj single-monitor effRight passthrough", effR, 8);
            failures += Assert("adj single-monitor effBottom passthrough", effB, 8);
        }

        // Case B — secondary monitor to the RIGHT of primary (Nate's setup
        // per the 2026-05-25 report). Right bleed clipped, left/bottom
        // untouched.
        {
            var primary = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var rightNeighbor = new WinRect { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 };
            var all = new List<WinRect> { primary, rightNeighbor };
            var (effL, effR, effB) = WindowManager.ClampBleedsForAdjacency(primary, all, 8, 8, 8);
            failures += Assert("adj right-neighbor effLeft passthrough", effL, 8);
            failures += Assert("adj right-neighbor effRight clipped to 0", effR, 0);
            failures += Assert("adj right-neighbor effBottom passthrough", effB, 8);
        }

        // Case C — secondary monitor to the LEFT of primary. Left bleed
        // clipped.
        {
            var primary = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var leftNeighbor = new WinRect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
            var all = new List<WinRect> { primary, leftNeighbor };
            var (effL, effR, effB) = WindowManager.ClampBleedsForAdjacency(primary, all, 8, 8, 8);
            failures += Assert("adj left-neighbor effLeft clipped to 0", effL, 0);
            failures += Assert("adj left-neighbor effRight passthrough", effR, 8);
            failures += Assert("adj left-neighbor effBottom passthrough", effB, 8);
        }

        // Case D — three monitors with primary in the middle (left + right
        // both adjacent). Both side bleeds clipped, bottom passthrough.
        // Visible client width loses 2 × bleed; pre-v3.22.45 baseline (no
        // text smear once WindowedWidth follows the smaller visible client).
        {
            var primary = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var leftN  = new WinRect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
            var rightN = new WinRect { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 };
            var all = new List<WinRect> { primary, leftN, rightN };
            var (effL, effR, effB) = WindowManager.ClampBleedsForAdjacency(primary, all, 8, 8, 8);
            failures += Assert("adj 3-monitor effLeft clipped to 0", effL, 0);
            failures += Assert("adj 3-monitor effRight clipped to 0", effR, 0);
            failures += Assert("adj 3-monitor effBottom passthrough", effB, 8);
        }

        // Case E — no vertical overlap = NOT adjacent. A monitor whose
        // bottom is at y=-100 (entirely above target's top edge) is on the
        // same axis but doesn't touch the target's left edge even if its
        // Right == target.Left. Defends against the naive "Right == Left"
        // check that would false-positive on offset-stack layouts.
        {
            var primary = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var aboveLeftN = new WinRect { Left = -1920, Top = -1080, Right = 0, Bottom = -100 };
            var all = new List<WinRect> { primary, aboveLeftN };
            var (effL, effR, effB) = WindowManager.ClampBleedsForAdjacency(primary, all, 8, 8, 8);
            failures += Assert("adj non-overlapping neighbor effLeft passthrough", effL, 8);
        }

        // Case F — vertical overlap at the boundary (neighbor.Bottom ==
        // target.Top). The half-open interval check (m.Bottom > target.Top)
        // correctly treats touching-at-a-corner as NON-adjacent on the left/
        // right edges (it's a TOP adjacency, not a LEFT adjacency).
        {
            var primary = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var cornerN = new WinRect { Left = -1920, Top = -1080, Right = 0, Bottom = 0 };
            var all = new List<WinRect> { primary, cornerN };
            var (effL, effR, effB) = WindowManager.ClampBleedsForAdjacency(primary, all, 8, 8, 8);
            failures += Assert("adj corner-touch neighbor effLeft passthrough", effL, 8);
        }

        // Case G — bottom adjacency: neighbor sits directly below primary
        // (e.g., portrait-oriented secondary stacked under primary). Bottom
        // bleed clipped, sides untouched.
        {
            var primary = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var belowN = new WinRect { Left = 0, Top = 1080, Right = 1920, Bottom = 2160 };
            var all = new List<WinRect> { primary, belowN };
            var (effL, effR, effB) = WindowManager.ClampBleedsForAdjacency(primary, all, 8, 8, 8);
            failures += Assert("adj below-neighbor effLeft passthrough", effL, 8);
            failures += Assert("adj below-neighbor effRight passthrough", effR, 8);
            failures += Assert("adj below-neighbor effBottom clipped to 0", effB, 0);
        }

        // Case H — composition: the v3.22.46 outer-rect pipeline. Given a
        // primary monitor with a right neighbor (Nate's setup), confirm the
        // full ComputeOuterRectFromBleeds(...) call using the clipped
        // bleeds produces the expected outer rect (-8, -18, 1928, 1106) on
        // a 1920×1080 monitor with default 13 px titlebarOffset. Pre-fix
        // this would have been (-8, -18, 1936, 1106) — the extra 8 px width
        // is what bled onto the right monitor.
        {
            var primary = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var rightN = new WinRect { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 };
            var all = new List<WinRect> { primary, rightN };
            int titlebarOffset = 13;
            int lB = 8, tB = 31, rB = 8, bB = 8;
            var (effL, effR, effB) = WindowManager.ClampBleedsForAdjacency(primary, all, lB, rB, bB);
            var (x, y, w, h) = WindowManager.ComputeOuterRectFromBleeds(primary, titlebarOffset, effL, tB, effR, effB);
            failures += Assert("compose right-neighbor x", x, -8);
            failures += Assert("compose right-neighbor y", y, -18);
            failures += Assert("compose right-neighbor w (no right extension)", w, 1928);
            failures += Assert("compose right-neighbor h", h, 1119);  // v3.22.81: native client + bottom overflow

            // Visible client edges: left at monitor.Left, right is 8 px INSIDE
            // monitor (the desktop gap that replaces what would've been a
            // bleed onto the secondary monitor).
            int clientLeft = x + lB;
            int clientRight = x + w - rB;
            failures += Assert("compose right-neighbor client.Left == monitor.Left", clientLeft, 0);
            failures += Assert("compose right-neighbor client.Right == monitor.Right - 8", clientRight, 1912);
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
