// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// v3.22.83 follow-up (2026-05-30) — guards the MODE-DEPENDENT adjacency-clamp
/// behavior in <see cref="WindowManager.ComputeSlimTitlebarOuterRect(WinRect,int,long,long)"/>,
/// the one coverage gap the post-ship verifier gap-audit flagged.
///
/// <para>
/// OuterRectMathTests is pure-static and can only test the helpers; it cannot
/// reach the mode branch (Windowed SKIPS <see cref="WindowManager.ClampBleedsForAdjacency"/>;
/// Fullscreen APPLIES it). That branch is the load-bearing fix for the Windowed
/// right-edge sliver — a refactor that re-applied the clamp in Windowed would
/// silently restore the sliver and pass every pure-static test. This suite closes
/// that hole by injecting a <see cref="FakeWindowsApi"/> (constant bleeds + a
/// right-abutting neighbor) and asserting the two modes diverge as intended:
///   • Windowed   → bleed NOT clamped → client.Right == monitor.Right (flush onto neighbor)
///   • Fullscreen → bleed clamped to 0 → client.Right == monitor.Right - rightBleed (gap)
/// Both use the SAME probe style so ONLY the mode-branch is under test.
/// </para>
///
/// Invoked via the --test-window-clamp CLI flag from Program.cs.
/// </summary>
public static class WindowManagerClampTests
{
    public static int RunAll()
    {
        int failures = 0;

        // Primary 1920×1080 with a monitor abutting its RIGHT edge. Constant
        // 8/31/8/8 bleed (Win11 WS_CAPTION). Same inputs for both modes — the
        // ONLY independent variable is WindowMode.
        var primary = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var rightNeighbor = new WinRect { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 };
        var monitors = new List<WinRect> { primary, rightNeighbor };
        const long style = WindowManager.WINDOWED_TITLEBAR_STYLE;  // WS_CAPTION probe → 8/31/8/8 in the fake
        const int rightBleed = 8;
        const int titlebarOffset = 18;

        // ── Windowed: adjacency clamp SKIPPED → flush right edge ──
        {
            var api = new FakeWindowsApi(8, 31, 8, 8, monitors);
            var cfg = MakeConfig(WindowMode.Windowed, titlebarOffset);
            var wm = new WindowManager(cfg, api);

            var (x, y, w, h) = wm.ComputeSlimTitlebarOuterRect(primary, titlebarOffset, style, 0);
            int clientRight = x + w - rightBleed;
            failures += Assert("windowed: right bleed NOT clamped (flush) → client.Right == monitor.Right",
                clientRight, primary.Right);
            // Sanity: left edge always flush in both modes (no left neighbor).
            int clientLeft = x + /*leftBleed*/8;
            failures += Assert("windowed: client.Left == monitor.Left", clientLeft, primary.Left);
        }

        // ── Fullscreen: adjacency clamp APPLIED → 8px gap on the abutting edge ──
        {
            var api = new FakeWindowsApi(8, 31, 8, 8, monitors);
            var cfg = MakeConfig(WindowMode.Fullscreen, titlebarOffset);
            var wm = new WindowManager(cfg, api);

            var (x, y, w, h) = wm.ComputeSlimTitlebarOuterRect(primary, titlebarOffset, style, 0);
            int clientRight = x + w - rightBleed;
            failures += Assert("fullscreen: right bleed clamped to 0 → client.Right == monitor.Right - 8",
                clientRight, primary.Right - rightBleed);
        }

        // ── The discriminator: the two modes MUST differ on the abutting edge.
        // If a refactor makes them equal (clamp applied to Windowed too), the
        // right-edge sliver is back. This is the assertion the pure-static tests
        // could never make. ──
        {
            var apiW = new FakeWindowsApi(8, 31, 8, 8, monitors);
            var wmW = new WindowManager(MakeConfig(WindowMode.Windowed, titlebarOffset), apiW);
            var (xw, _, ww, _) = wmW.ComputeSlimTitlebarOuterRect(primary, titlebarOffset, style, 0);
            int windowedRight = xw + ww - rightBleed;

            var apiF = new FakeWindowsApi(8, 31, 8, 8, monitors);
            var wmF = new WindowManager(MakeConfig(WindowMode.Fullscreen, titlebarOffset), apiF);
            var (xf, _, wf, _) = wmF.ComputeSlimTitlebarOuterRect(primary, titlebarOffset, style, 0);
            int fullscreenRight = xf + wf - rightBleed;

            failures += AssertTrue("windowed flush > fullscreen clamped on abutting edge (modes diverge)",
                windowedRight == primary.Right && fullscreenRight == primary.Right - rightBleed
                && windowedRight != fullscreenRight);
        }

        Console.WriteLine(failures == 0
            ? "WindowManagerClampTests: ALL PASS"
            : $"WindowManagerClampTests: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static AppConfig MakeConfig(WindowMode mode, int titlebarOffset)
    {
        var cfg = new AppConfig();
        cfg.Layout.WindowMode = mode;
        cfg.Layout.TitlebarOffset = titlebarOffset;
        cfg.Layout.HorizontalNudgePx = 0;  // isolate the clamp branch — no nudge
        return cfg;
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
    /// Minimal IWindowsApi fake. Only AdjustWindowRectEx (constant bleed) and
    /// GetAllMonitorBounds (caller-supplied list) carry behavior — everything
    /// else returns benign defaults, because ComputeSlimTitlebarOuterRect only
    /// calls those two. If a future change makes it call more, the default
    /// returns keep the test from throwing on unrelated paths.
    /// </summary>
    private sealed class FakeWindowsApi : IWindowsApi
    {
        private readonly int _l, _t, _r, _b;
        private readonly List<WinRect> _monitors;
        public FakeWindowsApi(int l, int t, int r, int b, List<WinRect> monitors)
        { _l = l; _t = t; _r = r; _b = b; _monitors = monitors; }

        public bool AdjustWindowRectEx(ref WinRect rect, uint style, bool hasMenu, uint exStyle)
        {
            // Mirror Win32: expand the client rect outward by the frame on each edge.
            rect.Left -= _l; rect.Top -= _t; rect.Right += _r; rect.Bottom += _b;
            return true;
        }
        public List<WinRect> GetAllMonitorBounds() => _monitors;
        public List<WinRect> GetAllMonitorWorkAreas() => _monitors;

        // ─── benign stubs (not exercised by ComputeSlimTitlebarOuterRect) ───
        public bool IsWindow(IntPtr hwnd) => false;
        public bool IsIconic(IntPtr hwnd) => false;
        public bool IsHungAppWindow(IntPtr hwnd) => false;
        public bool IsClientResponsive(IntPtr hwnd, out int lastErr) { lastErr = 0; return true; }
        public bool ShowWindow(IntPtr hwnd, int nCmdShow) => false;
        public bool SetForegroundWindow(IntPtr hwnd) => false;
        public bool BringWindowToTop(IntPtr hwnd) => false;
        public void ForceForegroundWindow(IntPtr hwnd) { }
        public bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags) => true;
        public bool GetWindowRect(IntPtr hwnd, out WinRect rect) { rect = default; return false; }
        public bool GetClientScreenRect(IntPtr hwnd, out WinRect rect) { rect = default; return false; }
        public IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex) => IntPtr.Zero;
        public IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong) => IntPtr.Zero;
        public bool SetWindowText(IntPtr hwnd, string text) => true;
        public IntPtr BeginDeferWindowPos(int n) => IntPtr.Zero;
        public IntPtr DeferWindowPos(IntPtr h, IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags) => IntPtr.Zero;
        public bool EndDeferWindowPos(IntPtr h) => true;
        public bool SetProcessPriority(int processId, uint priorityClass) => true;
        public (long processMask, long systemMask) GetProcessAffinity(int processId) => (0, 0);
        public uint GetProcessPriorityClass(int processId) => 0;
        public uint GetSystemDpi() => 96;
    }
}
