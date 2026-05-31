// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// v3.22.84 — unit tests for the WinEQ2 "measure, don't predict" read-back
/// correction (<see cref="WindowManager.TryComputeReadbackCorrection"/>). Drives a
/// fake <see cref="IWindowsApi"/> whose GetWindowRect / GetClientScreenRect describe
/// a live eqgame window whose REAL frame (~3/26/3/3) is smaller than the predicted
/// frame the outer rect was sized for — so the client overshoots the monitor ~5px/edge.
/// Asserts the correction shrinks the outer rect so the (faked) client lands flush.
///
/// Numbers are the live measurement (char natedogg, 100% DPI, 1920×1080, 2026-05-30):
///   predicted outer (overshoot): window (-8,-13)-(1928,1088) = (-8,-13) 1936×1101
///   real client (overshoots):    client (-5,13)-(1925,1085) = 1930×1072
///   corrected outer:             (-3,-8) 1926×1091 → client (0,18)-(1920,1080) flush
///
/// Invoked via --test-frame-correction from Program.cs. 0 = all pass, 1 = failure.
/// </summary>
public static class FrameCorrectionTests
{
    public static int RunAll()
    {
        int failures = 0;
        var monitor = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        const int offset = 18;

        // ── Case 1 — the live bug: overshoot → corrected flush ──
        {
            // Window sized for the PREDICTED 8/31/8/8 frame; real frame is 3/26/3/3,
            // so the live client overshoots the monitor by 5px on L/R/B.
            var win = new WinRect { Left = -8, Top = -13, Right = 1928, Bottom = 1088 };
            var cli = new WinRect { Left = -5, Top = 13, Right = 1925, Bottom = 1085 };
            var wm = MakeWm(WindowMode.Windowed, win, cli);

            bool hit = wm.TryComputeReadbackCorrection(IntPtr.Zero, monitor, offset, out var c);
            failures += AssertTrue("overshoot: correction warranted", hit);
            failures += Assert("corrected x", c.x, -3);
            failures += Assert("corrected y", c.y, -8);
            failures += Assert("corrected w", c.w, 1926);
            failures += Assert("corrected h", c.h, 1091);

            // The invariant that matters: applying the MEASURED frame (3/26/3/3) to the
            // corrected outer rect lands the client exactly on the monitor (peek 18).
            int mL = 3, mT = 26, mR = 3, mB = 3;
            failures += Assert("client.Left == monitor.Left", c.x + mL, 0);
            failures += Assert("client.Right == monitor.Right", c.x + c.w - mR, 1920);
            failures += Assert("client.Top == monitor.Top + captionVisible(18)", c.y + mT, 18);
            failures += Assert("client.Bottom == monitor.Bottom (flush)", c.y + c.h - mB, 1080);
        }

        // ── Case 2 — already flush: idempotent no-op ──
        // Re-measuring a corrected window yields the same constant frame → same rect.
        {
            var win = new WinRect { Left = -3, Top = -8, Right = 1923, Bottom = 1083 }; // (-3,-8) 1926×1091
            var cli = new WinRect { Left = 0, Top = 18, Right = 1920, Bottom = 1080 };  // flush
            var wm = MakeWm(WindowMode.Windowed, win, cli);

            bool hit = wm.TryComputeReadbackCorrection(IntPtr.Zero, monitor, offset, out _);
            failures += AssertTrue("already-flush window → no correction (idempotent)", !hit);
        }

        // ── Case 3 — Fullscreen: structural no-op ──
        {
            var win = new WinRect { Left = -8, Top = -13, Right = 1928, Bottom = 1088 };
            var cli = new WinRect { Left = -5, Top = 13, Right = 1925, Bottom = 1085 };
            var wm = MakeWm(WindowMode.Fullscreen, win, cli);

            bool hit = wm.TryComputeReadbackCorrection(IntPtr.Zero, monitor, offset, out _);
            failures += AssertTrue("Fullscreen → no correction (gated out)", !hit);
        }

        // ── Case 4 — garbage measurement is rejected (not flung) ──
        // A torn / mid-transition read with an insane frame must be ignored.
        {
            var win = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var cli = new WinRect { Left = 100, Top = 100, Right = 1820, Bottom = 980 }; // frame 100 each
            var wm = MakeWm(WindowMode.Windowed, win, cli);

            bool hit = wm.TryComputeReadbackCorrection(IntPtr.Zero, monitor, offset, out _);
            failures += AssertTrue("insane frame → rejected (no correction)", !hit);
        }

        // ── Case 5 — minimized (iconic) client: no correction (v3.22.85 safety gate) ──
        // A cross-process SetWindowPos on a minimized client (released D3D9 device) is
        // the documented crash class; also its GetClientRect collapses to 0/0/0/0.
        {
            var win = new WinRect { Left = -8, Top = -13, Right = 1928, Bottom = 1088 };
            var cli = new WinRect { Left = -5, Top = 13, Right = 1925, Bottom = 1085 };
            var wm = MakeWm(WindowMode.Windowed, win, cli, iconic: true);
            failures += AssertTrue("iconic window → no correction (skip SetWindowPos)",
                !wm.TryComputeReadbackCorrection(IntPtr.Zero, monitor, offset, out _));
        }

        // ── Case 6 — non-responsive (mid-zone-load DX reset) client: no correction ──
        // SetWindowPos on a hung client is the 14.5s pump-stall → crash class.
        {
            var win = new WinRect { Left = -8, Top = -13, Right = 1928, Bottom = 1088 };
            var cli = new WinRect { Left = -5, Top = 13, Right = 1925, Bottom = 1085 };
            var wm = MakeWm(WindowMode.Windowed, win, cli, responsive: false);
            failures += AssertTrue("non-responsive window → no correction (skip SetWindowPos)",
                !wm.TryComputeReadbackCorrection(IntPtr.Zero, monitor, offset, out _));
        }

        Console.WriteLine(failures == 0
            ? "FrameCorrectionTests: ALL PASS"
            : $"FrameCorrectionTests: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static WindowManager MakeWm(WindowMode mode, WinRect win, WinRect cli, bool iconic = false, bool responsive = true)
    {
        var cfg = new AppConfig();
        cfg.Layout.WindowMode = mode;
        cfg.Layout.Mode = "single";  // single-screen path → GetTargetMonitorBounds unused here
        return new WindowManager(cfg, new FakeApi(win, cli, iconic, responsive));
    }

    private static int Assert(string name, int actual, int expected)
    {
        if (actual == expected) { Console.WriteLine($"    ok: {name}"); return 0; }
        Console.WriteLine($"    FAIL: {name} (expected '{expected}', got '{actual}')");
        return 1;
    }

    private static int AssertTrue(string name, bool cond)
    {
        if (cond) { Console.WriteLine($"    ok: {name}"); return 0; }
        Console.WriteLine($"    FAIL: {name}");
        return 1;
    }

    /// <summary>
    /// Minimal IWindowsApi fake — only GetWindowRect + GetClientScreenRect carry
    /// behavior (the two reads TryComputeReadbackCorrection makes); the rest are
    /// benign stubs.
    /// </summary>
    private sealed class FakeApi : IWindowsApi
    {
        private readonly WinRect _win, _cli;
        private readonly bool _iconic, _responsive;
        public FakeApi(WinRect win, WinRect cli, bool iconic = false, bool responsive = true)
        { _win = win; _cli = cli; _iconic = iconic; _responsive = responsive; }

        public bool GetWindowRect(IntPtr h, out WinRect r) { r = _win; return true; }
        public bool GetClientScreenRect(IntPtr h, out WinRect r) { r = _cli; return true; }
        public bool IsIconic(IntPtr h) => _iconic;
        public bool IsClientResponsive(IntPtr h, out int lastErr) { lastErr = 0; return _responsive; }

        // ─── benign stubs (not exercised by TryComputeReadbackCorrection) ───
        public bool IsWindow(IntPtr h) => true;
        public bool IsHungAppWindow(IntPtr h) => false;
        public bool ShowWindow(IntPtr h, int n) => true;
        public bool SetForegroundWindow(IntPtr h) => true;
        public bool BringWindowToTop(IntPtr h) => true;
        public void ForceForegroundWindow(IntPtr h) { }
        public bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f) => true;
        public bool AdjustWindowRectEx(ref WinRect rect, uint style, bool hasMenu, uint exStyle) => true;
        public IntPtr GetWindowLongPtr(IntPtr h, int i) => IntPtr.Zero;
        public IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v) => IntPtr.Zero;
        public bool SetWindowText(IntPtr h, string t) => true;
        public IntPtr BeginDeferWindowPos(int n) => IntPtr.Zero;
        public IntPtr DeferWindowPos(IntPtr a, IntPtr b, IntPtr c, int x, int y, int cx, int cy, uint f) => IntPtr.Zero;
        public bool EndDeferWindowPos(IntPtr h) => true;
        public List<WinRect> GetAllMonitorWorkAreas() => new();
        public List<WinRect> GetAllMonitorBounds() => new();
        public bool SetProcessPriority(int p, uint c) => true;
        public (long processMask, long systemMask) GetProcessAffinity(int p) => (0, 0);
        public uint GetProcessPriorityClass(int p) => 0;
        public uint GetSystemDpi() => 96;
    }
}
