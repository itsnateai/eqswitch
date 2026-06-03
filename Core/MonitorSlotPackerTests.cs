// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Linq;

namespace EQSwitch.Core;

/// <summary>
/// v3.24.12 — regression guard for orphan-rescue slot compaction
/// (<see cref="MonitorSlotPacker.Compact"/>).
///
/// <para>
/// Mechanism under test: when a lower-slot client closes, the survivors must re-pack into
/// the lowest slots with no gaps, preserving relative order (nearest-primary stays
/// nearest), tie-broken by PID, wrapping modulo monitorCount for the 3+-clients-on-2-
/// monitors overflow case. The headline case is the field bug: a single survivor left on
/// slot 1 (secondary) after the slot-0 (primary) client closed must compact to slot 0.
/// </para>
/// <para>
/// This unit test can ONLY verify the pure map permutation — it CANNOT see a window move.
/// The real acceptance gate is a live dual-monitor smoke: close the primary-monitor client
/// and watch the surviving client jump to the primary (and Fix Windows do the same on
/// demand).
/// </para>
/// Invoked via --test-compact-slots from Program.cs. 0 = all pass, 1 = failure.
/// </summary>
public static class MonitorSlotPackerTests
{
    public static int RunAll()
    {
        int failures = 0;

        // ── Case 1 — THE field bug: lone survivor stranded on slot 1 (secondary) after the
        //              slot-0 (primary) client closed ⇒ compacts to slot 0 (primary). ──
        {
            var packed = MonitorSlotPacker.Compact(new Dictionary<int, int> { { 27924, 1 } }, 2);
            failures += Assert("orphan slot 1 → slot 0", Slot(packed, 27924), 0);
            failures += Assert("orphan map size unchanged", packed.Count, 1);
        }

        // ── Case 2 — already-compact 2-client map ⇒ unchanged (no needless churn). ──
        {
            var packed = MonitorSlotPacker.Compact(new Dictionary<int, int> { { 100, 0 }, { 200, 1 } }, 2);
            failures += Assert("compact A stays slot 0", Slot(packed, 100), 0);
            failures += Assert("compact B stays slot 1", Slot(packed, 200), 1);
        }

        // ── Case 3 — relative order preserved: lower current slot stays lower after pack. ──
        //   {B:0, A:1} is already compact; the meaningful order check is that the lower-slot
        //   PID keeps the lower packed slot regardless of PID magnitude.
        {
            var packed = MonitorSlotPacker.Compact(new Dictionary<int, int> { { 999, 0 }, { 1, 1 } }, 2);
            failures += AssertTrue("lower current slot (PID 999) keeps lower packed slot than PID 1",
                Slot(packed, 999) < Slot(packed, 1));
        }

        // ── Case 4 — tie-break by PID when two clients share a slot (overflow remnant). ──
        //   Both on slot 0; deterministic result = lower PID gets the lower packed slot.
        {
            var packed = MonitorSlotPacker.Compact(new Dictionary<int, int> { { 100, 0 }, { 50, 0 } }, 2);
            failures += Assert("tie-break: lower PID 50 → slot 0", Slot(packed, 50), 0);
            failures += Assert("tie-break: higher PID 100 → slot 1", Slot(packed, 100), 1);
        }

        // ── Case 5 — overflow: 3 clients on 2 monitors ⇒ slots wrap modulo 2 (0,1,0),
        //              mirroring AssignNextFreeSlot's documented stacking. ──
        {
            var packed = MonitorSlotPacker.Compact(
                new Dictionary<int, int> { { 10, 0 }, { 20, 1 }, { 30, 0 } }, 2);
            // ordered by (slot, pid): 10(0), 30(0), 20(1) → indices 0,1,2 → slots 0,1,0
            failures += Assert("overflow PID 10 → slot 0", Slot(packed, 10), 0);
            failures += Assert("overflow PID 30 → slot 1", Slot(packed, 30), 1);
            failures += Assert("overflow PID 20 → slot 0 (modulo wrap)", Slot(packed, 20), 0);
        }

        // ── Case 6 — single physical monitor (monitorCount 1) ⇒ everything collapses to 0. ──
        {
            var packed = MonitorSlotPacker.Compact(new Dictionary<int, int> { { 1, 0 }, { 2, 7 } }, 1);
            failures += Assert("1 monitor: PID 1 → slot 0", Slot(packed, 1), 0);
            failures += Assert("1 monitor: PID 2 → slot 0", Slot(packed, 2), 0);
        }

        // ── Case 7 — degenerate guards: empty map and monitorCount<1 don't throw. ──
        {
            var empty = MonitorSlotPacker.Compact(new Dictionary<int, int>(), 2);
            failures += Assert("empty map → empty result", empty.Count, 0);
            var clamped = MonitorSlotPacker.Compact(new Dictionary<int, int> { { 5, 3 } }, 0);
            failures += Assert("monitorCount<1 clamped to 1 ⇒ slot 0", Slot(clamped, 5), 0);
        }

        // ── Case 8 — input map is not mutated (Compact returns a fresh map). ──
        {
            var input = new Dictionary<int, int> { { 42, 9 } };
            MonitorSlotPacker.Compact(input, 2);
            failures += Assert("input map left untouched", input[42], 9);
        }

        Console.WriteLine(failures == 0
            ? "MonitorSlotPackerTests: ALL PASS"
            : $"MonitorSlotPackerTests: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int Slot(IReadOnlyDictionary<int, int> map, int pid) => map[pid];

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
}
