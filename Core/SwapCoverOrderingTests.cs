// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Linq;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// v3.24.1 — regression guard for the multimonitor swap taskbar-flicker fix
/// (incoming-first HWND_TOP plant in <see cref="WindowManager.ArrangeMultiMonitor"/>).
///
/// <para>
/// Mechanism under test: on the swap path the caller passes
/// <c>coverPrimaryFirst: true</c>; ArrangeMultiMonitor must plant the primary-bound
/// (slot-0) client at <c>HWND_TOP</c> covering primary BEFORE the DeferWindowPos
/// batch moves the outgoing client off primary — so the shell's rude-window recalc
/// (fired by the outgoing client's fullscreen-exit) never sees an uncovered primary
/// and never flashes the taskbar. With <c>coverPrimaryFirst: false</c> (every other
/// caller) the plant must NOT fire — behavior is unchanged.
/// </para>
/// <para>
/// A recording <see cref="IWindowsApi"/> fake stamps each call with a monotonic
/// sequence number. The plant is identified by a compound predicate, NOT by
/// <c>hWndInsertAfter</c> alone: <c>HWND_TOP == IntPtr.Zero</c>, the same value the
/// slim-style frame-change <c>SetWindowPos</c> uses as its insert-after. The plant is
/// the only such call with <c>cx &gt; 0</c>, no <c>SWP_NOZORDER</c>, and
/// <c>SWP_NOACTIVATE</c> set — the frame-change call has <c>cx == 0</c> and sets
/// <c>SWP_NOZORDER</c>, so it's excluded.
/// </para>
/// <para>
/// This unit test can ONLY verify the call ORDERING + parameters — it CANNOT see a
/// taskbar flash. The real acceptance gate is a live dual-monitor / dual-client swap
/// smoke (press <c>\</c> and <c>]</c>, watch the primary taskbar edge for a peek).
/// </para>
/// Invoked via --test-swap-cover from Program.cs. 0 = all pass, 1 = failure.
/// </summary>
public static class SwapCoverOrderingTests
{
    public static int RunAll()
    {
        int failures = 0;

        // Two identical 1920×1080 monitors side by side. Identical size keeps the
        // lock-to-primary-dims policy deterministic; it doesn't affect the plant
        // (the primary slot always uses its own bounds).
        var monitors = new List<WinRect>
        {
            new WinRect { Left = 0,    Top = 0, Right = 1920, Bottom = 1080 }, // primary
            new WinRect { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 }, // secondary
        };

        // Slot map: PID 200 (hwnd 0xB) is bound to slot 0 ⇒ primary ⇒ the plant target.
        // PID 100 (hwnd 0xA) is bound to slot 1 ⇒ secondary (the outgoing client).
        var clients = new List<EQClient>
        {
            new EQClient(100, new IntPtr(0xA), 0),
            new EQClient(200, new IntPtr(0xB), 1),
        };
        var slots = new Dictionary<int, int> { { 100, 1 }, { 200, 0 } };

        // ── Case 1 — coverPrimaryFirst: true ⇒ exactly one HWND_TOP plant on 0xB,
        //              before the batch commit ──
        {
            var api = new RecordingApi(monitors);
            var wm = new WindowManager(MakeMmConfig(), api);
            wm.ArrangeWindows(clients, slots, coverPrimaryFirst: true);

            var plants = api.Calls.Where(IsPlant).ToList();
            failures += Assert("exactly one HWND_TOP plant", plants.Count, 1);
            if (plants.Count == 1)
            {
                failures += AssertTrue("plant targets the primary-bound client 0xB",
                    plants[0].Hwnd == new IntPtr(0xB));
                int firstEndSeq = api.Calls.First(c => c.Kind == CallKind.End).Seq;
                failures += AssertTrue("plant fires BEFORE the batch commit (EndDeferWindowPos)",
                    plants[0].Seq < firstEndSeq);
                failures += AssertTrue("plant covers primary (x==0,y==0,cx==1920,cy==1080)",
                    plants[0].X == 0 && plants[0].Y == 0 && plants[0].Cx == 1920 && plants[0].Cy == 1080);
            }
        }

        // ── Case 2 — coverPrimaryFirst: false ⇒ zero plants (every non-swap caller) ──
        {
            var api = new RecordingApi(monitors);
            var wm = new WindowManager(MakeMmConfig(), api);
            wm.ArrangeWindows(clients, slots, coverPrimaryFirst: false);

            failures += Assert("coverPrimaryFirst:false ⇒ no HWND_TOP plant",
                api.Calls.Count(IsPlant), 0);
        }

        // ── Case 3 — single physical monitor (multimon MODE, one display) ⇒ no plant
        //              even with coverPrimaryFirst:true. The plant fixes the CROSS-monitor
        //              rude-window recalc; with one monitor every client resolves to
        //              slotIdx 0 and there is no second monitor to leave uncovered, so the
        //              monitorOrder.Count > 1 guard must suppress the plant. ──
        {
            var oneMonitor = new List<WinRect>
            {
                new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            };
            var api = new RecordingApi(oneMonitor);
            var wm = new WindowManager(MakeMmConfig(), api);
            wm.ArrangeWindows(clients, slots, coverPrimaryFirst: true);

            failures += Assert("single monitor ⇒ no plant (multi-monitor-only scope)",
                api.Calls.Count(IsPlant), 0);
        }

        Console.WriteLine(failures == 0
            ? "SwapCoverOrderingTests: ALL PASS"
            : $"SwapCoverOrderingTests: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The plant predicate. HWND_TOP is IntPtr.Zero — same as the slim frame-change
    /// SetWindowPos's insert-after — so we MUST also require cx>0, no SWP_NOZORDER, and
    /// SWP_NOACTIVATE to exclude the frame-change call (cx==0, SWP_NOZORDER set).
    /// </summary>
    private static bool IsPlant(Rec c) =>
        c.Kind == CallKind.SetWindowPos
        && c.After == NativeMethods.HWND_TOP
        && c.Cx > 0
        && (c.Flags & NativeMethods.SWP_NOZORDER) == 0
        && (c.Flags & NativeMethods.SWP_NOACTIVATE) != 0;

    private static AppConfig MakeMmConfig()
    {
        var cfg = new AppConfig();
        cfg.Layout.Mode = "multimonitor";
        cfg.Layout.SlimTitlebar = true;           // slim full-bounds windows = the flicker scenario
        cfg.Layout.SlimTitlebarSecondary = true;
        cfg.Layout.TargetMonitor = 0;
        cfg.Layout.SecondaryMonitor = 1;
        cfg.Layout.TitlebarOffset = 18;
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

    private enum CallKind { SetWindowPos, Begin, Defer, End }

    private struct Rec
    {
        public CallKind Kind;
        public IntPtr Hwnd;
        public IntPtr After;
        public int X, Y, Cx, Cy;
        public uint Flags;
        public int Seq;
    }

    /// <summary>
    /// Recording IWindowsApi fake. Every positioning call is appended to <see cref="Calls"/>
    /// with a monotonic sequence number. BeginDeferWindowPos / DeferWindowPos return a
    /// non-zero handle so ArrangeMultiMonitor takes the atomic-batch path (not the
    /// sequential fallback), which is what the swap path uses in production. All gates
    /// report eligible (IsWindow/IsClientResponsive true; IsIconic/IsHungAppWindow false)
    /// so both clients reach the build loop.
    /// </summary>
    private sealed class RecordingApi : IWindowsApi
    {
        public readonly List<Rec> Calls = new();
        private int _seq;
        private readonly List<WinRect> _monitors;

        public RecordingApi(List<WinRect> monitors) { _monitors = monitors; }

        public bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags)
        {
            Calls.Add(new Rec { Kind = CallKind.SetWindowPos, Hwnd = h, After = after, X = x, Y = y, Cx = cx, Cy = cy, Flags = flags, Seq = _seq++ });
            return true;
        }
        public IntPtr BeginDeferWindowPos(int n)
        {
            Calls.Add(new Rec { Kind = CallKind.Begin, Seq = _seq++ });
            return new IntPtr(1); // non-zero ⇒ batch path
        }
        public IntPtr DeferWindowPos(IntPtr info, IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags)
        {
            Calls.Add(new Rec { Kind = CallKind.Defer, Hwnd = h, After = after, X = x, Y = y, Cx = cx, Cy = cy, Flags = flags, Seq = _seq++ });
            return new IntPtr(1); // non-zero ⇒ keep batching
        }
        public bool EndDeferWindowPos(IntPtr info)
        {
            Calls.Add(new Rec { Kind = CallKind.End, Seq = _seq++ });
            return true;
        }

        public List<WinRect> GetAllMonitorBounds() => new(_monitors);
        public List<WinRect> GetAllMonitorWorkAreas() => new(_monitors);

        // ─── gates: all clients eligible ───
        public bool IsWindow(IntPtr h) => true;
        public bool IsIconic(IntPtr h) => false;
        public bool IsHungAppWindow(IntPtr h) => false;
        public bool IsClientResponsive(IntPtr h, out int lastErr) { lastErr = 0; return true; }

        // ─── benign stubs (not exercised, or no behavior needed) ───
        public bool ShowWindow(IntPtr h, int n) => true;
        public bool SetForegroundWindow(IntPtr h) => true;
        public bool BringWindowToTop(IntPtr h) => true;
        public void ForceForegroundWindow(IntPtr h) { }
        public bool GetWindowRect(IntPtr h, out WinRect r) { r = default; return true; }
        public bool GetClientScreenRect(IntPtr h, out WinRect r) { r = default; return true; }
        public bool AdjustWindowRectEx(ref WinRect rect, uint style, bool hasMenu, uint exStyle) => true;
        public IntPtr GetWindowLongPtr(IntPtr h, int i) => IntPtr.Zero;
        public IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v) => IntPtr.Zero;
        public bool SetWindowText(IntPtr h, string t) => true;
        public bool SetProcessPriority(int p, uint c) => true;
        public (long processMask, long systemMask) GetProcessAffinity(int p) => (0, 0);
        public uint GetProcessPriorityClass(int p) => 0;
        public uint GetSystemDpi() => 96;
    }
}
