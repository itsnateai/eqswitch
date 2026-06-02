// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// v3.24.10 — pure-static regression guard for <see cref="WindowManager.EffectiveSlotBounds"/>,
/// THE single multimonitor sizing authority that ArrangeMultiMonitor (sizes the window), the
/// hook-config pin, the Windowed read-back, and the eqclient.ini backbuffer all derive from.
/// <para>
/// v3.24.10 PER-MONITOR FIT semantics (replaces v3.24.3 lock-to-primary): the PRIMARY slot is
/// ALWAYS its own FULL bounds (main always covers its taskbar, both modes). The SECONDARY slot
/// fits its OWN monitor — CoverAll → secondary FULL (covers 2nd taskbar), ShowTaskbars → secondary
/// WORK (game butts 2nd taskbar, no gap, taskbar visible). No locking to the primary, so "no gap"
/// holds on a taller/shorter 2nd monitor by construction and the primary-bigger case just fits the
/// secondary to itself (no overflow). The <c>locked</c> flag now reports "secondary is the SAME size
/// as primary" → a swap won't resize (matched monitors / CoverAll). Mismatched → swap resizes →
/// the native backbuffer resize rebuilds 1:1. Covers single / matched / mismatched(1080+1200) /
/// wildly-mismatched(4K+1080p) / primary-bigger / 3+ slot-wrap / auto-hide-taskbar / asymmetric-fit.
/// Invoked via --test-effective-bounds. 0 = all pass.
/// </para>
/// </summary>
public static class EffectiveSlotBoundsTests
{
    public static int RunAll()
    {
        int failures = 0;

        // Helper monitor rects. Work areas trim a 40px bottom taskbar.
        WinRect Mon(int left, int top, int w, int h) =>
            new WinRect { Left = left, Top = top, Right = left + w, Bottom = top + h };

        // ── Case 1 — single physical display (no secondary) ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var (b0, l0) = WindowManager.EffectiveSlotBounds(0, pf, pw, null, null, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("single: slot0 = primary full", b0, 0, 0, 1920, 1080);
            failures += AssertFalse("single: slot0 not 'locked'", l0);
            // Even an odd slot collapses to primary when there's no second monitor.
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, null, null, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("single: slot1 falls back to primary full", b1, 0, 0, 1920, 1080);
            failures += AssertFalse("single: slot1 not 'locked'", l1);
        }

        // ── Case 2 — MATCHED 1920×1080, CoverAll → both own FULL = same size (symmetric) ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1080);
            var sw = Mon(1920, 0, 1920, 1040);
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("matched/CoverAll: slot0 own full", b0, 0, 0, 1920, 1080);
            failures += AssertRect("matched/CoverAll: slot1 own full (sec origin)", b1, 1920, 0, 3840, 1080);
            failures += AssertTrue("matched/CoverAll: SAME size → locked/symmetric", l1);
            failures += AssertTrue("matched/CoverAll: same W×H (swap won't resize)",
                b0.Width == b1.Width && b0.Height == b1.Height);
        }

        // ── Case 3 — MISMATCHED 1080 + 1200, CoverAll → 2nd covers FULL (no gap), asymmetric ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1200);
            var sw = Mon(1920, 0, 1920, 1160);
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("mismatch/CoverAll: slot0 primary full (covers main taskbar)", b0, 0, 0, 1920, 1080);
            failures += AssertRect("mismatch/CoverAll: slot1 covers FULL 2nd monitor (no gap, no taskbar)", b1, 1920, 0, 3840, 1200);
            failures += AssertFalse("mismatch/CoverAll: NOT same size (asymmetric → native resize on swap)", l1);
            failures += AssertTrue("mismatch/CoverAll: heights differ (1080 vs 1200)", b0.Height != b1.Height);
        }

        // ── Case 4 — MISMATCHED 1080 + 1200, ShowTaskbars → 2nd = own WORK (no gap, taskbar shows) ──
        // This is Nate's best-case default: game butts the 2nd taskbar, no desktop gap, taskbar visible;
        // main stays FULL (covers its own taskbar).
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1200);
            var sw = Mon(1920, 0, 1920, 1160);   // 2nd work = 1160 (40px taskbar)
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            failures += AssertRect("mismatch/ShowTaskbars: slot0 primary FULL (covers main taskbar)", b0, 0, 0, 1920, 1080);
            failures += AssertRect("mismatch/ShowTaskbars: slot1 = own WORK (game butts 2nd taskbar, no gap)", b1, 1920, 0, 3840, 1160);
            failures += AssertFalse("mismatch/ShowTaskbars: NOT same size (asymmetric by design)", l1);
            // No-gap invariant: the 2nd window's bottom equals the 2nd work-area bottom (= taskbar top).
            failures += AssertTrue("mismatch/ShowTaskbars: 2nd window bottom == 2nd work-area bottom (no gap)", b1.Bottom == sw.Bottom);
        }

        // ── Case 5 — 4K + 1080p, CoverAll → secondary fits its OWN native 1080 ──
        {
            var pf = Mon(0, 0, 3840, 2160);
            var pw = Mon(0, 0, 3840, 2120);
            var sf = Mon(3840, 0, 1920, 1080);
            var sw = Mon(3840, 0, 1920, 1040);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertFalse("4K+1080p/CoverAll: NOT same size (asymmetric)", l1);
            failures += AssertRect("4K+1080p/CoverAll: slot1 own native secondary", b1, 3840, 0, 5760, 1080);
        }

        // ── Case 6 — primary BIGGER than secondary (primary 1200, secondary 1080), CoverAll ──
        // Per-monitor-fit just sizes the 2nd window to its OWN 1080 monitor — no overflow, no lock math.
        {
            var pf = Mon(0, 0, 1920, 1200);
            var pw = Mon(0, 0, 1920, 1160);
            var sf = Mon(1920, 0, 1920, 1080);
            var sw = Mon(1920, 0, 1920, 1040);
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("primary-bigger/CoverAll: slot0 own full 1200", b0, 0, 0, 1920, 1200);
            failures += AssertRect("primary-bigger/CoverAll: slot1 own full 1080 (no overflow)", b1, 1920, 0, 3840, 1080);
            failures += AssertFalse("primary-bigger/CoverAll: NOT same size", l1);
        }

        // ── Case 7 — slot wrap for 3+ clients (slot%2): even→primary, odd→secondary ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1080);
            var sw = Mon(1920, 0, 1920, 1040);
            var (b2, _) = WindowManager.EffectiveSlotBounds(2, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (b3, _) = WindowManager.EffectiveSlotBounds(3, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("slot2 wraps to primary", b2, 0, 0, 1920, 1080);
            failures += AssertRect("slot3 wraps to secondary", b3, 1920, 0, 3840, 1080);
        }

        // ── Case 8 — mode picks SECONDARY full-vs-work; PRIMARY is FULL in BOTH modes ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1200);
            var sw = Mon(1920, 0, 1920, 1160);
            var (cover0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (show0,  _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            var (cover1, _) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (show1,  _) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            failures += AssertTrue("primary FULL in CoverAll (1080)", cover0.Height == 1080);
            failures += AssertTrue("primary FULL in ShowTaskbars too (1080) — main always covers", show0.Height == 1080);
            failures += AssertTrue("CoverAll secondary = FULL (1200, covers 2nd taskbar)", cover1.Height == 1200);
            failures += AssertTrue("ShowTaskbars secondary = WORK (1160, leaves 2nd taskbar)", show1.Height == 1160);
        }

        // ── Case 9 — ShowTaskbars with AUTO-HIDE taskbar (work == full on the 2nd) ──
        // An auto-hidden taskbar reports rcWork == rcMonitor, so ShowTaskbars degenerates to covering
        // the full 2nd monitor — no crash, no negative band.
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1080);   // work == full (auto-hide)
            var sf = Mon(1920, 0, 1920, 1200);
            var sw = Mon(1920, 0, 1920, 1200); // work == full (auto-hide)
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            failures += AssertRect("auto-hide/ShowTaskbars: slot0 = primary full", b0, 0, 0, 1920, 1080);
            failures += AssertRect("auto-hide/ShowTaskbars: slot1 = own full (work==full, no negative band)", b1, 1920, 0, 3840, 1200);
            failures += AssertFalse("auto-hide/ShowTaskbars: NOT same size (1080 vs 1200)", l1);
        }

        // ── Case 10 — secondary narrower on ONE axis (1800×1200), CoverAll → fits its OWN bounds ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1800, 1200);
            var sw = Mon(1920, 0, 1800, 1160);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertFalse("asymmetric-fit/CoverAll: NOT same size", l1);
            failures += AssertRect("asymmetric-fit/CoverAll: slot1 own native secondary (no overflow)", b1, 1920, 0, 3720, 1200);
        }

        // ── Case 11 — primary BIGGER + ShowTaskbars (main 1200 covers, 2nd 1080 shows taskbar) ──
        {
            var pf = Mon(0, 0, 1920, 1200);
            var pw = Mon(0, 0, 1920, 1160);
            var sf = Mon(1920, 0, 1920, 1080);
            var sw = Mon(1920, 0, 1920, 1040);
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            failures += AssertRect("primary-bigger/ShowTaskbars: slot0 own full 1200 (covers main taskbar)", b0, 0, 0, 1920, 1200);
            failures += AssertRect("primary-bigger/ShowTaskbars: slot1 own work 1040 (2nd taskbar shows, no gap)", b1, 1920, 0, 3840, 1040);
            failures += AssertTrue("primary-bigger/ShowTaskbars: 2nd window bottom == 2nd work bottom (no gap)", b1.Bottom == sw.Bottom);
            failures += AssertFalse("primary-bigger/ShowTaskbars: NOT same size", l1);
        }

        Console.WriteLine(failures == 0
            ? "EffectiveSlotBoundsTests: ALL PASS"
            : $"EffectiveSlotBoundsTests: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int AssertRect(string name, WinRect r, int l, int t, int right, int b)
    {
        if (r.Left == l && r.Top == t && r.Right == right && r.Bottom == b)
        {
            Console.WriteLine($"    ok: {name}");
            return 0;
        }
        Console.WriteLine($"    FAIL: {name} (expected L{l} T{t} R{right} B{b}, got L{r.Left} T{r.Top} R{r.Right} B{r.Bottom})");
        return 1;
    }

    private static int AssertTrue(string name, bool cond)
    {
        if (cond) { Console.WriteLine($"    ok: {name}"); return 0; }
        Console.WriteLine($"    FAIL: {name}");
        return 1;
    }

    private static int AssertFalse(string name, bool cond) => AssertTrue(name, !cond);
}
