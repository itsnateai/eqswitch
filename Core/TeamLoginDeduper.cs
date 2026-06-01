// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Collections.Generic;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Launch-time same-login dedup for a team's slots (v3.23.4).
///
/// EQ allows only one concurrent session per account, so firing two team slots that
/// resolve to the same login (Username, Server) gets the second client kicked. The
/// Save-time guard in <c>AutoLoginTeamsDialog</c> blocks this when the team is saved —
/// but team slots persist as bare name strings resolved late (Character-first), so a
/// previously-valid team can silently become a same-login collision when the
/// Account/Character lists mutate after Save (FK edit, account rename/delete, splitter
/// re-run). This helper is the launch-boundary backstop: it dedups against the live
/// resolved logins at the moment <c>TrayManager.FireTeam</c> fires.
///
/// Pure value logic, no WinForms / no _config — unit-tested via the --test-team-dedup
/// CLI flag (Core/TeamLoginDeduperTests).
/// </summary>
public static class TeamLoginDeduper
{
    public enum Decision { Fire, SkipUnresolved, SkipDuplicate }

    /// <summary>
    /// Canonical case-insensitive dedup key for a login: the (Username, Server) pair
    /// upper-cased so two slots whose logins differ only by case dedup together. EQ
    /// usernames are case-insensitive server-side, matching the Save-time guard's
    /// OrdinalIgnoreCase compare. A tuple (not a separator-joined string) means no
    /// separator char is needed and ("ab","c") can never collide with ("a","bc").
    /// </summary>
    public static (string Username, string Server) KeyOf(AccountKey login) =>
        (login.Username.ToUpperInvariant(), login.Server.ToUpperInvariant());

    /// <summary>
    /// The per-slot dedup decision primitive. Used by BOTH the live launch loop
    /// (<c>TrayManager.FireTeam</c>, which calls Step once per slot as it resolves the
    /// slot to a login) and <see cref="Decide"/> (the batch form the unit tests drive) —
    /// so the tested path and the production path are the same implementation.
    ///
    /// A null login (or one with an empty Username) is <see cref="Decision.SkipUnresolved"/>.
    /// A login not yet in <paramref name="seen"/> is <see cref="Decision.Fire"/> and is
    /// added to the set; a login already in it is <see cref="Decision.SkipDuplicate"/>.
    /// </summary>
    public static Decision Step(HashSet<(string, string)> seen, AccountKey? login)
    {
        if (login is not { } key || string.IsNullOrEmpty(key.Username))
            return Decision.SkipUnresolved;
        return seen.Add(KeyOf(key)) ? Decision.Fire : Decision.SkipDuplicate;
    }

    /// <summary>
    /// Batch form: one <see cref="Decision"/> per input slot, in order — a thin loop over
    /// <see cref="Step"/>. The testable proxy for FireTeam's per-slot Step calls. The first
    /// slot resolving to a given (Username, Server) — case-insensitive — Fires; any later
    /// slot resolving to the same login SkipsDuplicate; null/empty logins SkipUnresolved.
    /// </summary>
    public static IReadOnlyList<Decision> Decide(IReadOnlyList<AccountKey?> resolvedLogins)
    {
        var seen = new HashSet<(string, string)>();
        var decisions = new List<Decision>(resolvedLogins.Count);
        foreach (var login in resolvedLogins)
            decisions.Add(Step(seen, login));
        return decisions;
    }
}
