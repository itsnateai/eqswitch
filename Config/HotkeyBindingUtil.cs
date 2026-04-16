// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System;
using System.Collections.Generic;
using System.Linq;
using EQSwitch.Models;

namespace EQSwitch.Config;

/// <summary>
/// Phase 5a helpers for the AccountHotkeys / CharacterHotkeys family tables.
/// Counts + classifies bindings so the Hotkeys-tab Direct Bindings card and the
/// AccountHotkeysDialog / CharacterHotkeysDialog share one definition of "live"
/// vs "stale" vs "unbound (migration padding)."
///
/// Contract (per ConfigVersionMigrator.EnsureSize invariant):
///   - Empty Combo OR empty TargetName = migration padding — positional placeholder.
///     NOT counted as stale or live; skip during registration.
///   - Non-empty Combo + TargetName that resolves to an existing Account/Character = live.
///   - Non-empty Combo + TargetName that does NOT resolve = stale (user action required).
/// </summary>
public static class HotkeyBindingUtil
{
    /// <summary>True if the binding has both a Combo and a TargetName. Padding returns false.</summary>
    public static bool IsPopulated(HotkeyBinding b) =>
        !string.IsNullOrEmpty(b.Combo) && !string.IsNullOrEmpty(b.TargetName);

    /// <summary>Count of populated AccountHotkeys whose TargetName resolves to an Account.</summary>
    public static int CountLiveAccountBindings(AppConfig cfg) =>
        cfg.Hotkeys.AccountHotkeys.Count(b =>
            IsPopulated(b) &&
            cfg.Accounts.Any(a => a.Name.Equals(b.TargetName, StringComparison.Ordinal)));

    /// <summary>Count of populated CharacterHotkeys whose TargetName resolves to a Character.</summary>
    public static int CountLiveCharacterBindings(AppConfig cfg) =>
        cfg.Hotkeys.CharacterHotkeys.Count(b =>
            IsPopulated(b) &&
            cfg.Characters.Any(c => c.Name.Equals(b.TargetName, StringComparison.Ordinal)));

    /// <summary>Count of populated AccountHotkeys whose TargetName doesn't resolve.</summary>
    public static int CountStaleAccountBindings(AppConfig cfg) =>
        cfg.Hotkeys.AccountHotkeys.Count(b =>
            IsPopulated(b) &&
            !cfg.Accounts.Any(a => a.Name.Equals(b.TargetName, StringComparison.Ordinal)));

    /// <summary>Count of populated CharacterHotkeys whose TargetName doesn't resolve.</summary>
    public static int CountStaleCharacterBindings(AppConfig cfg) =>
        cfg.Hotkeys.CharacterHotkeys.Count(b =>
            IsPopulated(b) &&
            !cfg.Characters.Any(c => c.Name.Equals(b.TargetName, StringComparison.Ordinal)));

    /// <summary>
    /// Enumerate all populated bindings across both families with a human label suitable
    /// for conflict-detection modals. Used by SettingsForm.ApplySettings + dialogs' Save
    /// paths to extend the P3.5-D scan to family-table entries.
    /// </summary>
    public static IEnumerable<(string label, string combo)> EnumeratePopulatedLabeled(AppConfig cfg)
    {
        foreach (var b in cfg.Hotkeys.AccountHotkeys)
            if (IsPopulated(b))
                yield return ($"Account '{b.TargetName}'", b.Combo);
        foreach (var b in cfg.Hotkeys.CharacterHotkeys)
            if (IsPopulated(b))
                yield return ($"Character '{b.TargetName}'", b.Combo);
    }
}
