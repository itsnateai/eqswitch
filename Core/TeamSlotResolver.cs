// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Resolves a team slot's stored value to a Character (enter-world) XOR an Account (charselect),
/// honoring the v3.24.15 typed-value scheme with a legacy-bare fallback.
///
/// <para>Extracted (v3.24.15) so the launch path (<c>TrayManager.FireTeam</c>) and the three
/// display paths (<c>ResolveTeamSlotDisplay</c>, <c>BuildTeamTooltip</c>,
/// <c>SettingsForm.BuildTeamSummaryRows</c>) share ONE branching impl. The Teams dialog used to
/// store bare names and dedup an Account whenever a Character shared its name — which hid the
/// "eisley" Account behind the "Eisley" Character. Quick Login had already solved this with typed
/// values (<see cref="QuickLoginSlot"/>); this helper carries that routing to every Teams path so
/// they can never drift apart again.</para>
///
/// <para>Routing:
/// <list type="bullet">
///   <item><c>char:&lt;Name&gt;</c> → Character only (never an Account).</item>
///   <item><c>acct:&lt;Name&gt;</c> → Account only (never a Character) — so an account is pickable
///         for charselect even when a same-name Character exists.</item>
///   <item>bare <c>&lt;Name&gt;</c> → Character-first, Account fallback (pre-v3.24.15 saved slots /
///         hand-edits, unchanged behavior).</item>
///   <item>empty → neither.</item>
/// </list></para>
///
/// <para>Lookups are caller-supplied so the live (<c>_config.Find*</c>) and staged
/// (<c>_pending*</c> list) callers reuse the same logic; the helper itself is pure and
/// unit-tested — see <c>Core/TeamSlotResolverTests</c> (--test-team-slot-resolver).</para>
/// </summary>
public static class TeamSlotResolver
{
    /// <summary>
    /// Resolve <paramref name="slotValue"/> to a Character XOR an Account. At most one element of
    /// the returned tuple is non-null. <paramref name="findCharacter"/> / <paramref name="findAccount"/>
    /// receive the BARE name (prefix already stripped) and return the matching entity or null.
    /// </summary>
    public static (Character? character, Account? account) Resolve(
        string? slotValue,
        Func<string, Character?> findCharacter,
        Func<string, Account?> findAccount)
    {
        var (kind, name) = QuickLoginSlot.Parse(slotValue);
        switch (kind)
        {
            case QuickLoginSlot.Kind.Character:
                return (findCharacter(name), null);
            case QuickLoginSlot.Kind.Account:
                return (null, findAccount(name));   // charselect — never fall through to a Character
            case QuickLoginSlot.Kind.LegacyBare:
                var ch = findCharacter(name);       // pre-typed slots: Character-first, Account fallback
                return ch != null ? (ch, null) : (null, findAccount(name));
            default: // Empty
                return (null, null);
        }
    }
}
