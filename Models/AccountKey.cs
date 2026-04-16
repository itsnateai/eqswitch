// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

namespace EQSwitch.Models;

/// <summary>
/// Strongly-typed identifier for an Account. Unique key = (Username, Server).
/// A Character references its backing Account via this key; using a value type
/// here lets the compiler flag places that treat Username-or-Server as a free
/// string, and gives FK resolution a single logging-friendly ToString() form.
/// Case-sensitive — matches how the JSON config stores the fields verbatim.
/// </summary>
public readonly record struct AccountKey(string Username, string Server)
{
    public static AccountKey From(Account account) => new(account.Username, account.Server);

    public bool Matches(Account account) =>
        string.Equals(account.Username, Username, StringComparison.Ordinal) &&
        string.Equals(account.Server, Server, StringComparison.Ordinal);

    public override string ToString() => $"{Username}@{Server}";
}
