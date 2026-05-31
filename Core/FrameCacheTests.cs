// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.IO;
using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// v3.22.88 — unit tests for the measured-frame cache (<see cref="FrameCache"/>) and
/// its two wiring points in <see cref="WindowManager"/>:
///   • READ  — the no-HWND <see cref="WindowManager.ComputeSlimTitlebarOuterRect(WinRect,int)"/>
///             overload (the first-paint SHM-rect builder) uses a cached MEASURED frame
///             when one exists for the current DPI, else falls back to the AdjustWindowRectEx
///             PREDICTION path (today's behavior).
///   • WRITE — <see cref="WindowManager.TryComputeReadbackCorrection"/> persists the live
///             measured frame (write-on-change) so the NEXT launch reads it.
///
/// Drives a fake <see cref="IWindowsApi"/> with a pinned DPI (96) + a WS_POPUP-aware
/// AdjustWindowRectEx (8/31/8/8 for WS_CAPTION, 0/0/0/0 for WS_POPUP) so the prediction
/// fallback is deterministic. Live frame measured here = 3/26/3/3 (the 2026-05-30 value).
///
/// Invoked via --test-frame-cache from Program.cs. 0 = all pass, 1 = failure.
/// </summary>
public static class FrameCacheTests
{
    private static readonly WinRect Monitor = new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    private const int Offset = 13;          // v3.22.86 default TitlebarOffset
    private const int Dpi = 96;             // 100% — what FakeApi reports
    // The live-measured eqgame Windowed frame @ 100% DPI (2026-05-30, natedogg).
    private static readonly FrameCache.Frame MeasuredFrame = new(3, 26, 3, 3);

    public static int RunAll()
    {
        int failures = 0;
        failures += Test_CacheHit_BuildsFromMeasuredFrame_FlushFirstPaint();
        failures += Test_CacheMiss_FallsBackToPrediction();
        failures += Test_WrongDpi_FallsBackToPrediction();
        failures += Test_Fullscreen_IgnoresCache();
        failures += Test_NullCache_UsesPrediction();
        failures += Test_Write_OnSaneMeasurement();
        failures += Test_NoWrite_OnInsaneMeasurement();
        failures += Test_Set_WriteOnChange();
        failures += Test_Set_RejectsInsane();
        failures += Test_DiskRoundTrip();
        failures += Test_InsaneOnDisk_RejectedOnLoad();

        Console.WriteLine(failures == 0
            ? "FrameCacheTests: ALL PASS"
            : $"FrameCacheTests: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ── READ: warm cache → the SHM rect is built from the MEASURED frame, which is
    //    exactly what the read-back converges to → flush on first paint, no snap. ──
    private static int Test_CacheHit_BuildsFromMeasuredFrame_FlushFirstPaint()
    {
        int f = 0;
        var cache = new FrameCache(null);
        cache.Set(Dpi, MeasuredFrame);
        var wm = MakeWm(WindowMode.Windowed, cache);

        var got = wm.ComputeSlimTitlebarOuterRect(Monitor, Offset);
        var want = WindowManager.ComputeOuterRectFromBleeds(Monitor, Offset, 3, 26, 3, 3);
        f += AssertRect("cache hit → rect built from MEASURED frame (== read-back target)", got, want);

        // The invariant that matters: applying the measured frame to this outer rect
        // lands the client flush on the monitor with an Offset-px caption peek.
        f += AssertEq("hit: client.Left == monitor.Left", got.x + 3, 0);
        f += AssertEq("hit: client.Right == monitor.Right", got.x + got.w - 3, 1920);
        f += AssertEq("hit: client.Top == monitor.Top + peek", got.y + 26, Offset);
        f += AssertEq("hit: client.Bottom == monitor.Bottom (flush)", got.y + got.h - 3, 1080);
        return f;
    }

    // ── READ: empty cache → the PREDICTION path (today's behavior). ──
    private static int Test_CacheMiss_FallsBackToPrediction()
    {
        int f = 0;
        var wm = MakeWm(WindowMode.Windowed, new FrameCache(null));   // empty
        var got = wm.ComputeSlimTitlebarOuterRect(Monitor, Offset);
        // Windowed skips the adjacency clamp + nudge=0, so the prediction reduces to
        // ComputeOuterRectFromBleeds with the fake's WS_CAPTION bleed (8/31/8/8).
        var predicted = WindowManager.ComputeOuterRectFromBleeds(Monitor, Offset, 8, 31, 8, 8);
        f += AssertRect("cache miss → prediction path (8/31/8/8)", got, predicted);
        return f;
    }

    // ── READ: cache populated for a DIFFERENT DPI → miss → prediction. ──
    private static int Test_WrongDpi_FallsBackToPrediction()
    {
        int f = 0;
        var cache = new FrameCache(null);
        cache.Set(120, MeasuredFrame);          // 125% entry; FakeApi reports 96
        var wm = MakeWm(WindowMode.Windowed, cache);
        var got = wm.ComputeSlimTitlebarOuterRect(Monitor, Offset);
        var predicted = WindowManager.ComputeOuterRectFromBleeds(Monitor, Offset, 8, 31, 8, 8);
        f += AssertRect("wrong-DPI cache → prediction path", got, predicted);
        return f;
    }

    // ── READ: Fullscreen NEVER consults the cache (WS_POPUP has a 0 frame). ──
    private static int Test_Fullscreen_IgnoresCache()
    {
        int f = 0;
        var cache = new FrameCache(null);
        cache.Set(Dpi, MeasuredFrame);          // would be a HIT if mode-gating were wrong
        var wm = MakeWm(WindowMode.Fullscreen, cache);
        var got = wm.ComputeSlimTitlebarOuterRect(Monitor, Offset);
        // Fullscreen prediction: WS_POPUP → 0 bleed (the fake honors WS_POPUP).
        var fullscreenPredicted = WindowManager.ComputeOuterRectFromBleeds(Monitor, Offset, 0, 0, 0, 0);
        f += AssertRect("Fullscreen → cache ignored, WS_POPUP 0-frame prediction", got, fullscreenPredicted);
        var cachedRect = WindowManager.ComputeOuterRectFromBleeds(Monitor, Offset, 3, 26, 3, 3);
        f += AssertTrue("Fullscreen result is NOT the cached-frame rect",
            !(got.x == cachedRect.x && got.y == cachedRect.y && got.w == cachedRect.w && got.h == cachedRect.h));
        return f;
    }

    // ── READ: null cache (the 2-arg test/legacy ctor) → prediction path. ──
    private static int Test_NullCache_UsesPrediction()
    {
        int f = 0;
        var wm = new WindowManager(MakeConfig(WindowMode.Windowed), new FakeApi());  // no cache arg
        var got = wm.ComputeSlimTitlebarOuterRect(Monitor, Offset);
        var predicted = WindowManager.ComputeOuterRectFromBleeds(Monitor, Offset, 8, 31, 8, 8);
        f += AssertRect("null cache → prediction path", got, predicted);
        return f;
    }

    // ── WRITE: a sane live measurement is persisted (rides the read-back). ──
    private static int Test_Write_OnSaneMeasurement()
    {
        int f = 0;
        var cache = new FrameCache(null);
        // Overshoot case (read-back returns true): win/cli imply frame 3/26/3/3.
        var api = new FakeApi
        {
            Win = new WinRect { Left = -8, Top = -13, Right = 1928, Bottom = 1088 },
            Cli = new WinRect { Left = -5, Top = 13, Right = 1925, Bottom = 1085 },
        };
        var wm = new WindowManager(MakeConfig(WindowMode.Windowed), api, cache);
        bool hit = wm.TryComputeReadbackCorrection(IntPtr.Zero, Monitor, Offset, out _);
        f += AssertTrue("overshoot → correction warranted", hit);
        f += AssertTrue("measurement cached", cache.TryGet(Dpi, out var stored));
        f += AssertTrue("cached frame == measured 3/26/3/3", stored == MeasuredFrame);
        return f;
    }

    // ── WRITE: an insane (torn/minimized) measurement is NOT cached. ──
    private static int Test_NoWrite_OnInsaneMeasurement()
    {
        int f = 0;
        var cache = new FrameCache(null);
        var api = new FakeApi
        {
            Win = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            Cli = new WinRect { Left = 100, Top = 100, Right = 1820, Bottom = 980 }, // frame 100 each → insane
        };
        var wm = new WindowManager(MakeConfig(WindowMode.Windowed), api, cache);
        bool hit = wm.TryComputeReadbackCorrection(IntPtr.Zero, Monitor, Offset, out _);
        f += AssertTrue("insane frame → no correction", !hit);
        f += AssertTrue("insane frame → nothing cached", !cache.TryGet(Dpi, out _));
        f += AssertEq("cache empty after insane measurement", cache.Count, 0);
        return f;
    }

    // ── FrameCache.Set: write-on-change (repeat of same value is a no-op). ──
    private static int Test_Set_WriteOnChange()
    {
        int f = 0;
        var cache = new FrameCache(null);
        cache.Set(Dpi, MeasuredFrame);
        cache.Set(Dpi, MeasuredFrame);
        f += AssertEq("repeated identical Set → single entry", cache.Count, 1);
        cache.Set(Dpi, new FrameCache.Frame(4, 27, 4, 4));
        f += AssertTrue("changed Set → value updated", cache.TryGet(Dpi, out var v) && v == new FrameCache.Frame(4, 27, 4, 4));
        return f;
    }

    // ── FrameCache.Set: an insane frame is never stored. ──
    private static int Test_Set_RejectsInsane()
    {
        int f = 0;
        var cache = new FrameCache(null);
        cache.Set(Dpi, new FrameCache.Frame(3, 26, 3, 999));   // 999 > MaxMeasuredFramePx
        f += AssertEq("insane Set rejected → empty", cache.Count, 0);
        cache.Set(Dpi, new FrameCache.Frame(-1, 26, 3, 3));    // negative
        f += AssertEq("negative Set rejected → empty", cache.Count, 0);
        return f;
    }

    // ── Persistence: Set → reload from disk → TryGet returns the same frame. ──
    private static int Test_DiskRoundTrip()
    {
        int f = 0;
        string path = Path.Combine(Path.GetTempPath(), "eqswitch-frame-cache-test-roundtrip.json");
        try
        {
            File.Delete(path);
            var c1 = new FrameCache(path);
            c1.Set(Dpi, MeasuredFrame);
            var c2 = new FrameCache(path);   // fresh load from disk
            f += AssertTrue("disk round-trip: entry survives reload", c2.TryGet(Dpi, out var v));
            f += AssertTrue("disk round-trip: value intact", v == MeasuredFrame);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAIL: disk round-trip threw {ex.GetType().Name}: {ex.Message}");
            f += 1;
        }
        finally { try { File.Delete(path); } catch { /* best-effort */ } }
        return f;
    }

    // ── Robustness: an insane entry written directly to disk is dropped on load. ──
    private static int Test_InsaneOnDisk_RejectedOnLoad()
    {
        int f = 0;
        string path = Path.Combine(Path.GetTempPath(), "eqswitch-frame-cache-test-insane.json");
        try
        {
            File.WriteAllText(path, "{\"96\":{\"Left\":3,\"Top\":26,\"Right\":3,\"Bottom\":999}}");
            var c = new FrameCache(path);
            f += AssertTrue("insane on-disk entry → not returned", !c.TryGet(Dpi, out _));
            f += AssertEq("insane on-disk entry → dropped on load", c.Count, 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAIL: insane-on-disk threw {ex.GetType().Name}: {ex.Message}");
            f += 1;
        }
        finally { try { File.Delete(path); } catch { /* best-effort */ } }
        return f;
    }

    // ─── helpers ───────────────────────────────────────────────────────

    private static WindowManager MakeWm(WindowMode mode, FrameCache cache)
        => new(MakeConfig(mode), new FakeApi(), cache);

    private static AppConfig MakeConfig(WindowMode mode)
    {
        var cfg = new AppConfig();
        cfg.Layout.WindowMode = mode;
        cfg.Layout.Mode = "single";
        cfg.Layout.TitlebarOffset = Offset;
        cfg.Layout.HorizontalNudgePx = 0;
        return cfg;
    }

    private static int AssertRect(string name, (int x, int y, int w, int h) got, (int x, int y, int w, int h) want)
    {
        if (got == want) { Console.WriteLine($"    ok: {name}"); return 0; }
        Console.WriteLine($"    FAIL: {name} (expected {want}, got {got})");
        return 1;
    }

    private static int AssertEq(string name, int actual, int expected)
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
    /// Fake IWindowsApi: pinned DPI (96); WS_POPUP-aware AdjustWindowRectEx (0 bleed for
    /// Fullscreen, 8/31/8/8 for Windowed WS_CAPTION); configurable Win/Cli rects for the
    /// read-back write tests. Everything else is a benign stub.
    /// </summary>
    private sealed class FakeApi : IWindowsApi
    {
        public WinRect Win = new() { Left = -8, Top = -13, Right = 1928, Bottom = 1088 };
        public WinRect Cli = new() { Left = -5, Top = 13, Right = 1925, Bottom = 1085 };
        private const uint WS_POPUP = 0x80000000;

        public uint GetSystemDpi() => Dpi;

        public bool AdjustWindowRectEx(ref WinRect rect, uint style, bool hasMenu, uint exStyle)
        {
            if ((style & WS_POPUP) != 0) return true;           // WS_POPUP → 0 frame (Fullscreen)
            rect.Left -= 8; rect.Top -= 31; rect.Right += 8; rect.Bottom += 8;  // WS_CAPTION → 8/31/8/8
            return true;
        }

        public bool GetWindowRect(IntPtr h, out WinRect r) { r = Win; return true; }
        public bool GetClientScreenRect(IntPtr h, out WinRect r) { r = Cli; return true; }
        public bool IsIconic(IntPtr h) => false;
        public bool IsClientResponsive(IntPtr h, out int lastErr) { lastErr = 0; return true; }
        public List<WinRect> GetAllMonitorBounds() => new() { Monitor };
        public List<WinRect> GetAllMonitorWorkAreas() => new() { Monitor };

        // ─── benign stubs ───
        public bool IsWindow(IntPtr h) => true;
        public bool IsHungAppWindow(IntPtr h) => false;
        public bool ShowWindow(IntPtr h, int n) => true;
        public bool SetForegroundWindow(IntPtr h) => true;
        public bool BringWindowToTop(IntPtr h) => true;
        public void ForceForegroundWindow(IntPtr h) { }
        public bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f) => true;
        public IntPtr GetWindowLongPtr(IntPtr h, int i) => IntPtr.Zero;
        public IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v) => IntPtr.Zero;
        public bool SetWindowText(IntPtr h, string t) => true;
        public IntPtr BeginDeferWindowPos(int n) => IntPtr.Zero;
        public IntPtr DeferWindowPos(IntPtr a, IntPtr b, IntPtr c, int x, int y, int cx, int cy, uint f) => IntPtr.Zero;
        public bool EndDeferWindowPos(IntPtr h) => true;
        public bool SetProcessPriority(int p, uint c) => true;
        public (long processMask, long systemMask) GetProcessAffinity(int p) => (0, 0);
        public uint GetProcessPriorityClass(int p) => 0;
    }
}
