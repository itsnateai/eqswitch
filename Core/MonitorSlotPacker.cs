// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Collections.Generic;
using System.Linq;

namespace EQSwitch.Core;

/// <summary>
/// Multi-monitor slot compaction (v3.24.12).
///
/// Slot ownership in the live PID→slot map (<c>TrayManager._monitorSlotByPid</c>) is
/// sticky: a client is assigned a slot once at discovery (<c>AssignNextFreeSlot</c>) and
/// nothing ever recomputes it. When a lower-slot client closes — e.g. the primary-monitor
/// (slot 0) client — its sibling stays stranded on the higher monitor, and Fix Windows
/// faithfully re-strands it (it just re-applies the same sticky map). Compaction re-packs
/// the survivors into the lowest slots [0, 1, …] with no gaps, PRESERVING their relative
/// order (the client nearest the primary stays nearest), so the orphan is pulled back
/// toward the primary. Slots wrap modulo <paramref name="monitorCount"/> to preserve the
/// documented 3+-clients-on-2-monitors overflow stacking that AssignNextFreeSlot uses.
///
/// Pure value logic, no WinForms / no _config — unit-tested via the --test-compact-slots
/// CLI flag (Core/MonitorSlotPackerTests). The real acceptance gate is a live smoke: close
/// the primary-monitor client and watch the survivor jump to the primary.
/// </summary>
public static class MonitorSlotPacker
{
    /// <summary>
    /// Compute the compacted slot map. Returns a NEW map; <paramref name="current"/> is
    /// not mutated. Survivors are ordered by their current slot (stable — preserves the
    /// existing left-to-right monitor arrangement), tie-broken by PID for a deterministic,
    /// repeatable result, then assigned slots <c>i % monitorCount</c> from the front.
    ///
    /// <para>Collision-free by construction: distinct ascending indices are handed out, so
    /// two survivors only ever share a slot in the intended overflow-stacking case (more
    /// clients than monitors), exactly mirroring <c>AssignNextFreeSlot</c>'s overflow path.</para>
    /// </summary>
    /// <param name="current">The live PID→slot map (e.g. just after a client was removed).</param>
    /// <param name="monitorCount">Slots in use (1 or 2); indices wrap modulo this. Clamped to ≥1.</param>
    public static Dictionary<int, int> Compact(IReadOnlyDictionary<int, int> current, int monitorCount)
    {
        if (monitorCount < 1) monitorCount = 1;
        var ordered = current.Keys
            .OrderBy(pid => current[pid])
            .ThenBy(pid => pid)
            .ToList();
        var packed = new Dictionary<int, int>(current.Count);
        for (int i = 0; i < ordered.Count; i++)
            packed[ordered[i]] = i % monitorCount;
        return packed;
    }
}
