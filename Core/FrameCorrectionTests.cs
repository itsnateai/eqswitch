// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;

namespace EQSwitch.Core;

/// <summary>
/// v3.22.84 — unit tests for the WinEQ2 "measure, don't predict" frame correction.
/// Mirror of the C++ correction in Native/eqswitch-hook.cpp (ComputeCorrectedGeoRect):
/// the hook measures eqgame's REAL non-client frame and shifts each edge of the
/// C#-predicted SHM outer rect by the per-edge prediction error so the visible
/// client lands flush on the monitor. This suite is the testable SPEC of that
/// math (the C++ side can't run in the C# harness). KEEP IN SYNC with the .cpp.
///
/// Invoked via --test-frame-correction from Program.cs. 0 = all pass, 1 = failure.
/// </summary>
public static class FrameCorrectionTests
{
    public static int RunAll()
    {
        int failures = 0;

        // ── Case 1 — the live bug (char natedogg, 100% DPI, 1920×1080, 2026-05-30) ──
        // C# predicted 8/31/8/8 and wrote outer (-8,-13) 1936×1101 to SHM; eqgame's
        // real frame is 3/26/3/3 → client overshot to 1930×1072. The correction must
        // shrink the outer rect 5px/edge so the client lands flush (0,18)-(1920,1080).
        {
            var (x, y, w, h) = WindowManager.ComputeFrameCorrectedRect(
                shm: (-8, -13, 1936, 1101),
                predicted: (8, 31, 8, 8),
                measured: (3, 26, 3, 3));
            failures += Assert("natedogg corrected x", x, -3);
            failures += Assert("natedogg corrected y", y, -8);
            failures += Assert("natedogg corrected w", w, 1926);
            failures += Assert("natedogg corrected h", h, 1091);

            // The invariant that matters: apply the MEASURED frame to the corrected
            // outer rect → client must be exactly the monitor (flush L/R/B, peek 18).
            int mL = 3, mT = 26, mR = 3, mB = 3;
            failures += Assert("natedogg client.Left == monitor.Left", x + mL, 0);
            failures += Assert("natedogg client.Right == monitor.Right", x + w - mR, 1920);
            failures += Assert("natedogg client.Top == monitor.Top + captionVisible(18)", y + mT, 18);
            failures += Assert("natedogg client.Bottom == monitor.Bottom (flush)", y + h - mB, 1080);
        }

        // ── Case 2 — prediction correct (Win10 / frame == predicted): exact no-op ──
        // Idempotency: when there's no prediction error, the corrected rect must equal
        // the SHM rect so re-applying every message can't drift.
        {
            var (x, y, w, h) = WindowManager.ComputeFrameCorrectedRect(
                shm: (0, 0, 1920, 1080),
                predicted: (8, 31, 8, 8),
                measured: (8, 31, 8, 8));
            failures += Assert("noop x", x, 0);
            failures += Assert("noop y", y, 0);
            failures += Assert("noop w", w, 1920);
            failures += Assert("noop h", h, 1080);
        }

        // ── Case 3 — bad measurement is bounded (±MaxFrameCorrectionPx) ──
        // A garbage GetWindowInfo read (frame huge → large negative error) must NOT
        // fling the window; each edge correction clamps to ±20px.
        {
            var (x, y, w, h) = WindowManager.ComputeFrameCorrectedRect(
                shm: (-8, -13, 1936, 1101),
                predicted: (8, 31, 8, 8),
                measured: (100, 100, 100, 100));  // raw errors -92/-69/-92/-92 → clamp -20
            failures += Assert("clamp x (=-8 + -20)", x, -28);
            failures += Assert("clamp y (=-13 + -20)", y, -33);
            failures += Assert("clamp w (=1936 - (-20) - (-20))", w, 1976);
            failures += Assert("clamp h (=1101 - (-20) - (-20))", h, 1141);
        }

        // ── Case 4 — Fullscreen (WS_POPUP, 0 frame): exact no-op ──
        // Defense-in-depth: even if the correction were reached in Fullscreen, a
        // 0/0/0/0 predicted bleed and 0/0/0/0 measured frame → no change.
        {
            var (x, y, w, h) = WindowManager.ComputeFrameCorrectedRect(
                shm: (0, 0, 1920, 1080),
                predicted: (0, 0, 0, 0),
                measured: (0, 0, 0, 0));
            failures += Assert("fullscreen noop x", x, 0);
            failures += Assert("fullscreen noop w", w, 1920);
            failures += Assert("fullscreen noop h", h, 1080);
        }

        Console.WriteLine(failures == 0
            ? "FrameCorrectionTests: ALL PASS"
            : $"FrameCorrectionTests: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int Assert(string name, int actual, int expected)
    {
        if (actual == expected) { Console.WriteLine($"    ok: {name}"); return 0; }
        Console.WriteLine($"    FAIL: {name} (expected '{expected}', got '{actual}')");
        return 1;
    }
}
