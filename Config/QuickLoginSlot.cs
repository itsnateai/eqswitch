// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;

namespace EQSwitch.Config;

/// <summary>
/// Owns the storage format for the four Quick Login slots (<c>AppConfig.QuickLogin1-4</c>),
/// fired by the tray-click "AutoLogin 1-4" actions and their hotkeys.
///
/// v3.23.0 introduced <b>typed</b> slot values so the dispatch can route deterministically
/// instead of guessing character-vs-account from a bare name:
///   <list type="bullet">
///     <item><c>char:&lt;Name&gt;</c> → a v4 Character — enters world.</item>
///     <item><c>acct:&lt;Name&gt;</c> → a v4 Account — stops at character select.</item>
///     <item>empty → unassigned.</item>
///   </list>
/// Bare (un-prefixed) values are pre-v3.23 / hand-edited configs; they parse as
/// <see cref="Kind.LegacyBare"/> and the dispatcher falls through to the legacy
/// name-resolution path for back-compat (<see cref="UI.TrayManager"/>.FireLegacyQuickLoginSlot).
///
/// Why typed and not a bare name: the legacy resolver matches a bare name against the
/// v3 LegacyAccount rows and inherits <c>AutoEnterWorld</c> from the matched row. An
/// account whose backing LegacyAccount row carries <c>AutoEnterWorld=true</c> would enter
/// world even when the user explicitly picked the Account (char-select) entry — exactly the
/// distinction the dialog's orange/white coloring advertises. The prefix removes the guess.
///
/// This is a pure value helper (no WinForms / no config dependency) so it is unit-testable
/// in isolation — see <c>Core/QuickLoginSlotTests.cs</c>.
/// </summary>
public static class QuickLoginSlot
{
    public const string CharPrefix = "char:";
    public const string AcctPrefix = "acct:";

    public enum Kind { Empty, Character, Account, LegacyBare }

    /// <summary>Build the stored value for a Character slot (enters world).</summary>
    public static string ForCharacter(string name) => CharPrefix + (name ?? "");

    /// <summary>Build the stored value for an Account slot (char-select only).</summary>
    public static string ForAccount(string name) => AcctPrefix + (name ?? "");

    /// <summary>
    /// Split a stored slot value into its kind and bare name. Never throws; an empty
    /// or null value returns <see cref="Kind.Empty"/> with an empty name.
    /// </summary>
    public static (Kind Kind, string Name) Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return (Kind.Empty, "");
        if (value.StartsWith(CharPrefix, StringComparison.Ordinal))
            return (Kind.Character, value.Substring(CharPrefix.Length));
        if (value.StartsWith(AcctPrefix, StringComparison.Ordinal))
            return (Kind.Account, value.Substring(AcctPrefix.Length));
        return (Kind.LegacyBare, value);
    }

    /// <summary>
    /// Human-readable name for a slot value, for read-only display (e.g. the Settings
    /// "AutoLogin slots:" readout). Returns "—" for an empty slot.
    /// </summary>
    public static string DisplayName(string? value)
    {
        var (kind, name) = Parse(value);
        return kind == Kind.Empty ? "—" : name;
    }
}
