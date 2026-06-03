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
/// v3.24.10 LOCK-SIZE + BOTTOM-ANCHOR semantics: BOTH windows take the PRIMARY's SIZE (so one
/// shared DX backbuffer matches both → crisp, AND a swap is a pure MOVE that never resizes →
/// no ResetDevice → no distortion / no crash — the load-bearing stability invariant). The primary
/// is top-anchored at its own origin (covers the main taskbar); the secondary keeps that SAME SIZE
/// but is BOTTOM-ANCHORED so its bottom edge meets the 2nd monitor's WORK bottom (ShowTaskbars →
/// game butts the 2nd taskbar, no gap) or FULL bottom (CoverAll → covers it); the leftover band
/// sits at the TOP. Degrades to the 2nd's native bounds when the monitors are too far apart to lock
/// (4K+1080p) or the primary doesn't fit the 2nd. The <c>locked</c> flag is true whenever the
/// secondary is locked to the primary's size (→ swap is move-only). Invoked via
/// --test-effective-bounds. 0 = all pass.
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
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, null, null, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("single: slot1 falls back to primary full", b1, 0, 0, 1920, 1080);
            failures += AssertFalse("single: slot1 not locked", l1);
        }

        // ── Case 2 — MATCHED 1920×1080, CoverAll → both same size, 2nd covers (no band) ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1080);
            var sw = Mon(1920, 0, 1920, 1040);
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("matched/CoverAll: slot0 primary full", b0, 0, 0, 1920, 1080);
            failures += AssertRect("matched/CoverAll: slot1 (matched → top=0, covers)", b1, 1920, 0, 3840, 1080);
            failures += AssertTrue("matched/CoverAll: locked", l1);
            failures += AssertTrue("matched/CoverAll: SAME size (swap is move-only)",
                b0.Width == b1.Width && b0.Height == b1.Height);
        }

        // ── Case 3 — MISMATCHED 1080 + 1200, CoverAll → 2nd bottom-anchored to FULL bottom (covers) ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1200);
            var sw = Mon(1920, 0, 1920, 1160);
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("mismatch/CoverAll: slot0 primary full (covers main taskbar)", b0, 0, 0, 1920, 1080);
            // size = primary (1920x1080); bottom-anchored so bottom = 1200 (full), top = 1200-1080 = 120
            failures += AssertRect("mismatch/CoverAll: slot1 size=primary, bottom-anchored to FULL (covers taskbar)", b1, 1920, 120, 3840, 1200);
            failures += AssertTrue("mismatch/CoverAll: locked", l1);
            failures += AssertTrue("mismatch/CoverAll: SAME size as primary (swap move-only, no crash)",
                b1.Width == b0.Width && b1.Height == b0.Height);
            failures += AssertTrue("mismatch/CoverAll: 2nd bottom == FULL bottom (covers taskbar)", b1.Bottom == sf.Bottom);
        }

        // ── Case 4 — MISMATCHED 1080 + 1200, ShowTaskbars → 2nd butts the taskbar (no gap) ──
        // Nate's best-case default: game's bottom = 2nd work bottom, taskbar visible, no gap; main FULL.
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1200);
            var sw = Mon(1920, 0, 1920, 1160);   // 2nd work bottom = 1160 (40px taskbar)
            var (b0, _) = WindowManager.EffectiveSlotBounds(0, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            failures += AssertRect("mismatch/ShowTaskbars: slot0 primary FULL (covers main taskbar)", b0, 0, 0, 1920, 1080);
            // size = primary (1080); bottom-anchored to WORK bottom 1160 → top = 1160-1080 = 80
            failures += AssertRect("mismatch/ShowTaskbars: slot1 size=primary, butts 2nd taskbar (no gap)", b1, 1920, 80, 3840, 1160);
            failures += AssertTrue("mismatch/ShowTaskbars: locked", l1);
            failures += AssertTrue("mismatch/ShowTaskbars: SAME size as primary (swap move-only)",
                b1.Width == b0.Width && b1.Height == b0.Height);
            // No-gap invariant: the 2nd window's bottom == the 2nd work-area bottom (= taskbar top).
            failures += AssertTrue("mismatch/ShowTaskbars: 2nd bottom == 2nd work bottom (NO GAP)", b1.Bottom == sw.Bottom);
        }

        // ── Case 5 — 4K + 1080p, CoverAll → too far apart → degrade to 2nd native ──
        {
            var pf = Mon(0, 0, 3840, 2160);
            var pw = Mon(0, 0, 3840, 2120);
            var sf = Mon(3840, 0, 1920, 1080);
            var sw = Mon(3840, 0, 1920, 1040);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertFalse("4K+1080p/CoverAll: NOT locked (degrade)", l1);
            failures += AssertRect("4K+1080p/CoverAll: slot1 = 2nd native", b1, 3840, 0, 5760, 1080);
        }

        // ── Case 6 — primary BIGGER than secondary (primary 1200, secondary 1080) → degrade ──
        // Primary doesn't fit the smaller 2nd → degrade to 2nd native (rare; documented swap caveat).
        {
            var pf = Mon(0, 0, 1920, 1200);
            var pw = Mon(0, 0, 1920, 1160);
            var sf = Mon(1920, 0, 1920, 1080);
            var sw = Mon(1920, 0, 1920, 1040);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertFalse("primary-bigger/CoverAll: NOT locked (primary doesn't fit 2nd)", l1);
            failures += AssertRect("primary-bigger/CoverAll: slot1 = 2nd native", b1, 1920, 0, 3840, 1080);
        }

        // ── Case 7 — slot wrap for 3+ clients (slot%2): even→primary, odd→secondary ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1920, 1080);
            var sw = Mon(1920, 0, 1920, 1040);
            var (b2, _) = WindowManager.EffectiveSlotBounds(2, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            var (b3, l3) = WindowManager.EffectiveSlotBounds(3, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertRect("slot2 wraps to primary", b2, 0, 0, 1920, 1080);
            failures += AssertRect("slot3 wraps to secondary (locked, matched)", b3, 1920, 0, 3840, 1080);
            failures += AssertTrue("slot3 locked", l3);
        }

        // ── Case 8 — mode picks the 2nd ANCHOR bottom; primary is FULL in BOTH modes ──
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
            failures += AssertTrue("CoverAll 2nd bottom = FULL (1200, covers 2nd taskbar)", cover1.Bottom == 1200);
            failures += AssertTrue("ShowTaskbars 2nd bottom = WORK (1160, butts 2nd taskbar)", show1.Bottom == 1160);
            failures += AssertTrue("both modes: 2nd height == primary height (1080, stable)",
                cover1.Height == 1080 && show1.Height == 1080);
        }

        // ── Case 9 — ShowTaskbars with AUTO-HIDE taskbar (work == full on the 2nd) ──
        // Auto-hidden taskbar → rcWork == rcMonitor → bottom-anchor to full bottom, covers full, no crash.
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1080);   // work == full (auto-hide)
            var sf = Mon(1920, 0, 1920, 1200);
            var sw = Mon(1920, 0, 1920, 1200); // work == full (auto-hide)
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            failures += AssertRect("auto-hide/ShowTaskbars: slot1 size=primary, bottom-anchored to 1200", b1, 1920, 120, 3840, 1200);
            failures += AssertTrue("auto-hide/ShowTaskbars: locked", l1);
        }

        // ── Case 10 — secondary narrower on ONE axis (1800×1200) → primary doesn't fit → degrade ──
        {
            var pf = Mon(0, 0, 1920, 1080);
            var pw = Mon(0, 0, 1920, 1040);
            var sf = Mon(1920, 0, 1800, 1200);
            var sw = Mon(1920, 0, 1800, 1160);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.CoverAll);
            failures += AssertFalse("asymmetric-fit/CoverAll: NOT locked (primary wider than 2nd)", l1);
            failures += AssertRect("asymmetric-fit/CoverAll: slot1 = 2nd native", b1, 1920, 0, 3720, 1200);
        }

        // ── Case 11 — primary BIGGER + ShowTaskbars → degrade (primary doesn't fit smaller 2nd) ──
        {
            var pf = Mon(0, 0, 1920, 1200);
            var pw = Mon(0, 0, 1920, 1160);
            var sf = Mon(1920, 0, 1920, 1080);
            var sw = Mon(1920, 0, 1920, 1040);
            var (b1, l1) = WindowManager.EffectiveSlotBounds(1, pf, pw, sf, sw, MultiMonTaskbarMode.ShowTaskbars);
            failures += AssertFalse("primary-bigger/ShowTaskbars: NOT locked (degrade)", l1);
            failures += AssertRect("primary-bigger/ShowTaskbars: slot1 = 2nd native", b1, 1920, 0, 3840, 1080);
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
