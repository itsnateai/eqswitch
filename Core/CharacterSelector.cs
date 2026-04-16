// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System;

namespace EQSwitch.Core;

/// <summary>
/// Pure decision helper for auto-login character selection.
///
/// <see cref="Decide"/> answers "which 1-based slot should I click?" given the
/// user's intent (explicit slot or name to match) and the MQ2 heap-scanned
/// character list. Safety policies (bounds checks, slot-mode fallback,
/// wrong-character abort) stay at the caller — this helper is pure.
/// </summary>
public static class CharacterSelector
{
    /// <summary>
    /// Decide which character slot to select during auto-login.
    ///
    /// <paramref name="requestedSlot"/>: 0 = auto-by-name; 1-10 = explicit slot.
    /// <paramref name="requestedName"/>: name to match when <paramref name="requestedSlot"/> is 0.
    /// <paramref name="charNamesInHeap"/>: MQ2-scanned character list order
    /// (null or empty = heap not yet populated; the explicit
    /// <paramref name="requestedSlot"/> is returned unchanged as a fallback).
    /// </summary>
    /// <returns>
    ///   <c>resolvedSlot</c> (1-10) = slot to click. A return of <c>0</c> is a
    ///   hard abort signal — callers MUST NOT proceed to enter-world on
    ///   default selection; see <c>AutoLoginManager.RunLoginSequence</c>'s
    ///   unified abort branch.
    ///   <c>resolvedByName</c> = true when the heap scan matched; false otherwise.
    ///   <c>decisionLog</c> = one-line summary for <c>FileLogger</c>.
    /// </returns>
    public static (int resolvedSlot, bool resolvedByName, string decisionLog) Decide(
        int requestedSlot, string? requestedName, string[]? charNamesInHeap)
    {
        // Case 1: heap not ready → honor the caller's explicit requested slot.
        // If requestedSlot is 0 the caller has nothing to fall back to; return 0.
        if (charNamesInHeap == null || charNamesInHeap.Length == 0)
            return (requestedSlot, false,
                $"heap empty, fall back to requested slot {requestedSlot}");

        // Case 2: auto-by-name — scan the heap for a case-insensitive match
        // using StringComparison.OrdinalIgnoreCase.
        // Heap-read names come from MQ2's live-game memory scrape where casing
        // can drift vs. the user's saved Character.Name (server renames, scrape
        // artifacts, historical config edits). Pre-extraction the DLL-side
        // RequestSelectionByName used OrdinalIgnoreCase for exactly this reason.
        // Other codebase compares (FindCharacterByName, AccountKey.Matches) use
        // Ordinal because they operate on config-to-config data where casing is
        // stable by construction — this function operates on config-to-heap and
        // must be tolerant.
        if (requestedSlot == 0 && !string.IsNullOrEmpty(requestedName))
        {
            for (int i = 0; i < charNamesInHeap.Length; i++)
            {
                if (string.Equals(charNamesInHeap[i], requestedName,
                    StringComparison.OrdinalIgnoreCase))
                    return (i + 1, true, $"name match '{requestedName}' at slot {i + 1}");
            }
            return (0, false,
                $"name '{requestedName}' not in heap ({string.Join(",", charNamesInHeap)})");
        }

        // Case 3: explicit slot requested. Caller's resolvedSlot > charCount bounds
        // check handles out-of-range (matches pre-extraction error path).
        if (requestedSlot >= 1)
            return (requestedSlot, false, $"explicit slot {requestedSlot}");

        // Case 4: malformed request (slot=0 + empty name).
        return (0, false, "no slot or name requested");
    }
}
