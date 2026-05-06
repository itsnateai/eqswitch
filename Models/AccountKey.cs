// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

namespace EQSwitch.Models;

/// <summary>
/// Strongly-typed identifier for an Account. Unique key = (Username, Server).
/// A Character references its backing Account via this key; using a value type
/// here lets the compiler flag places that treat Username-or-Server as a free
/// string, and gives FK resolution a single logging-friendly ToString() form.
/// Matching is case-insensitive (v3.15.2): EQ usernames are case-insensitive
/// server-side, and v3.15.1 aligned (Username, Server) uniqueness checks to
/// OrdinalIgnoreCase. Note that the auto-generated record equality remains
/// case-sensitive (preserving stable GetHashCode for any dictionary keying);
/// this method exists specifically for FK-style lookup-against-Account.
/// </summary>
public readonly record struct AccountKey(string Username, string Server)
{
    public static AccountKey From(Account account) => new(account.Username, account.Server);

    public bool Matches(Account account) =>
        string.Equals(account.Username, Username, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(account.Server, Server, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Username}@{Server}";
}
