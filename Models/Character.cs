// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System.Text.Json.Serialization;

namespace EQSwitch.Models;

/// <summary>
/// A specific play target on an Account. Launching a Character from the tray
/// logs into the backing Account, selects this character, and enters world.
/// References its Account by (AccountUsername, AccountServer) — Username alone
/// is not sufficient when the same login is reused across servers.
/// </summary>
public class Character
{
    public string Name { get; set; } = "";
    public string AccountUsername { get; set; } = "";
    public string AccountServer { get; set; } = "Dalaya";

    /// <summary>
    /// 0 = auto (heap-scan name match — preferred). 1-10 = explicit slot
    /// (legacy fallback for characters whose names can't be read from heap).
    /// </summary>
    public int CharacterSlot { get; set; } = 0;

    public string DisplayLabel { get; set; } = "";
    public string ClassHint { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>
    /// Typed FK to the backing Account. Computed from the serialized
    /// (AccountUsername, AccountServer) pair — not round-tripped separately
    /// in JSON. Use with AccountKey.Matches(account) or direct equality.
    /// </summary>
    [JsonIgnore]
    public AccountKey AccountKey => new(AccountUsername, AccountServer);

    /// <summary>User-facing display label. Falls back DisplayLabel → Name → literal placeholder.
    /// Never empty.</summary>
    public string EffectiveLabel
    {
        get
        {
            if (!string.IsNullOrEmpty(DisplayLabel)) return DisplayLabel;
            if (!string.IsNullOrEmpty(Name)) return Name;
            return "(unnamed character)";
        }
    }

    /// <summary>Tray-menu label with optional class hint in parentheses (e.g. "Backup (Cleric)").
    /// Single space before the paren. Falls back to EffectiveLabel when ClassHint is empty.</summary>
    public string LabelWithClass =>
        string.IsNullOrEmpty(ClassHint) ? EffectiveLabel : $"{EffectiveLabel} ({ClassHint})";
}
