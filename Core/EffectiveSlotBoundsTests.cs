// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// v3.24.3 — pure-static regression guard for <see cref="WindowManager.EffectiveSlotBounds"/>,
/// THE single multimonitor sizing authority that ArrangeMultiMonitor (sizes the window), the
/// hook-config pin, the Windowed read-back, and the eqclient.ini backbuffer all derive from.
/// <para>
/// Covers the cross-hardware config matrix the brief requires: single physical display,
/// matched monitors, mismatched monitors (Nate's 1080 + 1200), wildly-mismatched (4K + 1080p)
/// degrade-to-native, primary-bigger-than-secondary degrade, slot wrap for 3+ clients, AND the
/// CoverAll-vs-ShowTaskbars full-vs-work axis. The load-bearing invariant under test:
/// when locked, BOTH slots get the SAME W×H (symmetric → swap never resizes a client → no DX
/// rebuild → no smoosh/peek/black-bar). Invoked via --test-effective-bounds. 0 = all pass.
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
            failures += AssertFalse("single: slot0 not locked", l0);
            // Even an odd slot collapses to primary when there's no second monitor.
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, null, null, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("single: slot1 falls back to primary", b1, 0, 0, 1920, 1080);
            failures += AssertFalse("single: slot1 not locked", l1);
        }

        // ── Case 2 — MATCHED 1920×1080, CoverAll → both lock to 1920×1080 (same size) ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1080);
            var sw = Mon(1920, 0, 1920, 1040);
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("matched/CoverAll: slot0", b0, 0, 0, 1920, 1080);
            failures += AssertRect("matched/CoverAll: slot1 (sec origin, primary dims)", b1, 1920, 0, 3840, 1080);
            failures += AssertTrue("matched/CoverAll: locked", l1);
            failures += AssertTrue("matched/CoverAll: SAME size (symmetric swap)",
                b0.Width == b1.Width && b0.Height == b1.Height);
        }

        // ── Case 3 — MISMATCHED 1080 + 1200, CoverAll → both 1920×1080; secondary band ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1200);
            var sw = Mon(1920, 0, 1920, 1160);
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("mismatch/CoverAll: slot0 primary full", b0, 0, 0, 1920, 1080);
            failures += AssertRect("mismatch/CoverAll: slot1 locked to 1080 (120px band)", b1, 1920, 0, 3840, 1080);
            failures += AssertTrue("mismatch/CoverAll: locked", l1);
            failures += AssertTrue("mismatch/CoverAll: SAME size (symmetric swap)",
                b0.Height == b1.Height && b0.Height == 1080);
        }

        // ── Case 4 — MISMATCHED 1080 + 1200, ShowTaskbars → both lock to primary WORK (1040) ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1200);
            var sw = Mon(1920, 0, 1920, 1160);
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            failures += AssertRect("mismatch/ShowTaskbars: slot0 primary WORK", b0, 0, 0, 1920, 1040);
            failures += AssertRect("mismatch/ShowTaskbars: slot1 locked to work-dims 1040", b1, 1920, 0, 3840, 1040);
            failures += AssertTrue("mismatch/ShowTaskbars: locked", l1);
            failures += AssertTrue("mismatch/ShowTaskbars: SAME size (symmetric swap)",
                b0.Height == b1.Height && b0.Height == 1040);
        }

        // ── Case 5 — 4K + 1080p, CoverAll → Δ>200 ⇒ NOT lockable, slot1 native (degrade) ──
        {
            var pf = Mon(0, 0, 3840, 2160);
            var pw = Mon(0, 0, 3840, 2120);
            var sf = Mon(3840, 0, 1920, 1080);
            var sw = Mon(3840, 0, 1920, 1040);
            var (_, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (b1, _) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertFalse("4K+1080p/CoverAll: NOT locked (degrade)", l1);
            failures += AssertRect("4K+1080p/CoverAll: slot1 native secondary", b1, 3840, 0, 5760, 1080);
        }

        // ── Case 6 — primary BIGGER than secondary (primary 1200, secondary 1080) ⇒ degrade ──
        // ShouldLockToPrimaryDims requires the primary to FIT inside the secondary; it doesn't
        // (1200 > 1080), so locking would extend the window past the secondary's edge → native.
        {
            var pf = Mon(0, 0, 1920, 1200);
            var pw = Mon(0, 0, 1920, 1160);
            var sf = Mon(1920, 0, 1920, 1080);
            var sw = Mon(1920, 0, 1920, 1040);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertFalse("primary-bigger/CoverAll: NOT locked (primary doesn't fit)", l1);
            failures += AssertRect("primary-bigger/CoverAll: slot1 native secondary", b1, 1920, 0, 3840, 1080);
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
            failures += AssertRect("slot3 wraps to secondary (locked)", b3, 1920, 0, 3840, 1080);
        }

        // ── Case 8 — CoverAll vs ShowTaskbars pick FULL vs WORK on the primary slot ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var (cover, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, null, null, MultiMonTaskbarMode.CoverAll);
            var (show, _)  = WindowManager.EffectiveSlotBounds(0, pf, pw, null, null, MultiMonTaskbarMode.ShowTaskbars);
            failures += AssertTrue("CoverAll primary uses FULL height (1080)", cover.Height == 1080);
            failures += AssertTrue("ShowTaskbars primary uses WORK height (1040)", show.Height == 1040);
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
