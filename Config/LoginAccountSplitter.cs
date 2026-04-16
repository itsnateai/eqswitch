// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using EQSwitch.Models;

namespace EQSwitch.Config;

/// <summary>
/// Splits a flat list of v3 LoginAccount rows into the v4 shape
/// (deduped Account list + Character list per row with a non-empty CharacterName).
///
/// This is the object-level mirror of ConfigVersionMigrator.MigrateV3ToV4 Step 1.
/// Kept separate because the migrator operates at the JsonObject tree level
/// (pre-deserialization), while this helper operates on deserialized objects —
/// used by SettingsForm.ApplySettings() to keep v4 Accounts/Characters in sync
/// with the legacy _pendingAccounts list when the user saves Settings.
///
/// Both paths MUST produce equivalent output for the same input. The migrator
/// is covered by _tests/migration fixtures; changes here should be mirrored
/// into the migrator (and vice versa). This duplication goes away in Phase 6
/// when the legacy fields are removed.
/// </summary>
public static class LoginAccountSplitter
{
    /// <summary>
    /// Splits legacy LoginAccount rows into deduped v4 Account + Character lists.
    /// Dedup key is (Username, Server) — case-insensitive, matching the migrator.
    /// Empty-Username rows are skipped (no account can exist without a username).
    /// A LoginAccount with a non-empty CharacterName always produces a Character
    /// regardless of AutoEnterWorld — the v4 model encodes enter-world intent
    /// in the launch type (Character = enter world), not a per-row flag.
    /// </summary>
    public static (List<Account> Accounts, List<Character> Characters) Split(
        IReadOnlyList<LoginAccount> legacy)
    {
        var accounts = new List<Account>();
        var characters = new List<Character>();
        // See ConfigVersionMigrator.MigrateV3ToV4 for the same pattern: dedup key is
        // case-insensitive but canonical FK casing must be preserved so runtime
        // AccountKey.Matches (Ordinal) can resolve Character → Account reliably.
        var accountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keyToCanonicalUsername = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keyToCanonicalServer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var la in legacy)
        {
            if (string.IsNullOrEmpty(la.Username))
                continue;

            // Account dedup — first row wins for (Username, Server).
            var key = $"{la.Username}\u0001{la.Server}";
            if (accountKeys.Add(key))
            {
                var name = string.IsNullOrEmpty(la.Name) ? la.Username : la.Name;
                keyToCanonicalUsername[key] = la.Username;
                keyToCanonicalServer[key] = la.Server;
                accounts.Add(new Account
                {
                    Name = name,
                    Username = la.Username,
                    EncryptedPassword = la.EncryptedPassword,
                    Server = la.Server,
                    UseLoginFlag = la.UseLoginFlag,
                });
            }

            // Character creation — every row with a character name becomes a Character.
            // Multiple legacy rows sharing an (Username, Server) each generate their
            // own Character, all pointing at the single deduped Account. FK uses canonical
            // casing from the dedup winner, not la.Username — critical for case-drift
            // configs (e.g. "MyAcct" and "myacct" rows dedup to one Account but Characters
            // from the second row would otherwise orphan at runtime).
            if (!string.IsNullOrEmpty(la.CharacterName))
            {
                characters.Add(new Character
                {
                    Name = la.CharacterName,
                    AccountUsername = keyToCanonicalUsername[key],
                    AccountServer = keyToCanonicalServer[key],
                    CharacterSlot = la.CharacterSlot,
                });
            }
        }

        return (accounts, characters);
    }
}
