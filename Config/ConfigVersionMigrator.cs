using System.Text.Json;
using System.Text.Json.Nodes;
using EQSwitch.Core;

namespace EQSwitch.Config;

/// <summary>
/// Versioned config migration — transforms raw JSON before deserialization.
/// Add a new MigrateVxToVy method when making breaking config changes
/// (property renames, type changes, structural moves). Simple additions
/// with defaults don't need a migration — Validate() handles those.
/// </summary>
public static class ConfigVersionMigrator
{
    /// <summary>
    /// Migrates JSON config string to the latest schema version.
    /// Returns the (possibly transformed) JSON string and whether any migration ran.
    /// </summary>
    public static (string json, bool migrated) MigrateIfNeeded(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject();
        }
        catch (JsonException ex)
        {
            FileLogger.Warn($"ConfigMigrator: couldn't parse JSON for migration: {ex.Message}");
            return (json, false);
        }

        if (root == null)
            return (json, false);

        var version = root["configVersion"]?.GetValue<int>() ?? 0;
        var startVersion = version;

        // Apply migrations sequentially
        while (version < AppConfig.CurrentConfigVersion)
        {
            switch (version)
            {
                case 0:
                    MigrateV0ToV1(root);
                    break;

                case 1:
                    MigrateV1ToV2(root);
                    break;

                case 2:
                    MigrateV2ToV3(root);
                    break;

                case 3:
                    MigrateV3ToV4(root);
                    break;

                default:
                    // Unknown version ahead of us — don't touch it
                    FileLogger.Warn($"ConfigMigrator: config version {version} is newer than supported ({AppConfig.CurrentConfigVersion})");
                    return (json, false);
            }

            version++;
            FileLogger.Info($"ConfigMigrator: migrated v{version - 1} → v{version}");
        }

        if (version == startVersion)
            return (json, false);

        // Stamp the new version and serialize back
        root["configVersion"] = version;
        var options = new JsonSerializerOptions { WriteIndented = true };
        return (root.ToJsonString(options), true);
    }

    // ─── Migration Methods ──────────────────────────────────────

    /// <summary>
    /// v0 → v1: Initial migration for configs created before versioning.
    /// No structural changes needed — just stamps the version.
    /// Add property renames/moves here when v1 ships with breaking changes.
    /// </summary>
    private static void MigrateV0ToV1(JsonObject root)
    {
        // Example patterns for future use:
        //
        // Rename a property:
        //   if (root.ContainsKey("oldName"))
        //   {
        //       root["newName"] = root["oldName"]!.DeepClone();
        //       root.Remove("oldName");
        //   }
        //
        // Change type (e.g. string → int):
        //   if (root["someValue"]?.GetValue<string>() is string s && int.TryParse(s, out var i))
        //       root["someValue"] = i;
        //
        // Move nested property:
        //   if (root["layout"]?.AsObject() is { } layout && layout.ContainsKey("oldKey"))
        //   {
        //       root["newSection"] ??= new JsonObject();
        //       root["newSection"]!.AsObject()["newKey"] = layout["oldKey"]!.DeepClone();
        //       layout.Remove("oldKey");
        //   }
        //
        // Remove dead property:
        //   root.Remove("deprecatedSetting");

        // No structural changes for v0→v1 — just establishing the version field.
    }

    /// <summary>
    /// v1 → v2: Autologin Teams migration.
    /// "LoginAll" tray action now fires Team 1 (not all QuickLogin slots).
    /// Auto-populate Team 1/2 from QuickLogin slots so existing users
    /// keep their behavior. Team 1 = slots 1+2, Team 2 = slots 3+4.
    /// </summary>
    private static void MigrateV1ToV2(JsonObject root)
    {
        var ql1 = root["quickLogin1"]?.GetValue<string>() ?? "";
        var ql2 = root["quickLogin2"]?.GetValue<string>() ?? "";
        var ql3 = root["quickLogin3"]?.GetValue<string>() ?? "";
        var ql4 = root["quickLogin4"]?.GetValue<string>() ?? "";

        // Only populate teams if user had quick login slots configured
        if (!string.IsNullOrEmpty(ql1) || !string.IsNullOrEmpty(ql2))
        {
            root["team1Account1"] = ql1;
            root["team1Account2"] = ql2;
            FileLogger.Info($"ConfigMigrator v1→v2: Team 1 populated from QuickLogin 1+2");
        }

        if (!string.IsNullOrEmpty(ql3) || !string.IsNullOrEmpty(ql4))
        {
            root["team2Account1"] = ql3;
            root["team2Account2"] = ql4;
            FileLogger.Info($"ConfigMigrator v1→v2: Team 2 populated from QuickLogin 3+4");
        }
    }

    /// <summary>
    /// v2 → v3: Rename "LaunchTwo" action to "LaunchAll" in TrayClickConfig.
    /// "LaunchTwo" was a misleading name for bare multi-client launch.
    /// The old duplicate "LaunchAll" (Team 1 login) case was removed from
    /// ExecuteTrayAction — any persisted "LaunchAll" now correctly resolves
    /// to bare multi-client launch under the new semantics.
    /// </summary>
    private static void MigrateV2ToV3(JsonObject root)
    {
        if (root["trayClick"]?.AsObject() is not { } trayClick)
            return;

        var migrated = false;
        foreach (var prop in new[] { "singleClick", "doubleClick", "tripleClick", "middleClick", "middleDoubleClick" })
        {
            var value = trayClick[prop]?.GetValue<string>();
            if (value == "LaunchTwo")
            {
                trayClick[prop] = "LaunchAll";
                migrated = true;
            }
        }

        if (migrated)
            FileLogger.Info("ConfigMigrator v2→v3: renamed LaunchTwo → LaunchAll in TrayClickConfig");
    }

    /// <summary>
    /// v3 → v4: split LoginAccount into Account (creds) + Character (play target).
    /// Populates new JSON keys: accountsV4, charactersV4, characterAliases, plus
    /// hotkeys.accountHotkeys / hotkeys.characterHotkeys arrays.
    ///
    /// Legacy v3 keys (accounts, characters, quickLogin1-4, hotkeys.autoLogin1-4)
    /// remain in JSON unchanged so consumers compiled in Phase 1 (still reading
    /// legacy fields) keep working. v4→v5 cleanup migration in Phase 6 will remove
    /// the legacy keys after consumers migrate over Phases 2-5.
    ///
    /// Migration is idempotent on its own output: if accountsV4 / charactersV4 already
    /// exist (e.g. mid-development re-run), Step 1 short-circuits.
    /// </summary>
    private static void MigrateV3ToV4(JsonObject root)
    {
        // Step 1 — Account + Character split from legacy "accounts" array
        var v3Accounts = root["accounts"]?.AsArray() ?? new JsonArray();
        var newAccounts = new JsonArray();
        var newCharacters = new JsonArray();
        var accountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accountKeyToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Capture (Username, Server, AutoEnterWorld, CharacterName) per v3 row for hotkey + team rebinds later.
        var v3Rows = new List<(string Name, string Username, string Server, string CharacterName, bool AutoEnterWorld)>();

        foreach (var node in v3Accounts)
        {
            if (node is not JsonObject row) continue;

            var name = row["name"]?.GetValue<string>() ?? "";
            var username = row["username"]?.GetValue<string>() ?? "";
            var encryptedPassword = row["encryptedPassword"]?.GetValue<string>() ?? "";
            var server = row["server"]?.GetValue<string>() ?? "Dalaya";
            var characterName = row["characterName"]?.GetValue<string>() ?? "";
            var characterSlot = row["characterSlot"]?.GetValue<int>() ?? 0;
            var autoEnterWorld = row["autoEnterWorld"]?.GetValue<bool>() ?? false;
            var useLoginFlag = row["useLoginFlag"]?.GetValue<bool>() ?? true;

            v3Rows.Add((name, username, server, characterName, autoEnterWorld));

            // Account dedup by (Username, Server). Drops AutoEnterWorld — that intent migrates
            // via hotkey/team rules below or via type discriminator (Character = enter world).
            var key = $"{username}\u0001{server}";
            if (!string.IsNullOrEmpty(username) && accountKeys.Add(key))
            {
                var accountName = string.IsNullOrEmpty(name) ? username : name;
                accountKeyToName[key] = accountName;
                newAccounts.Add(new JsonObject
                {
                    ["name"] = accountName,
                    ["username"] = username,
                    ["encryptedPassword"] = encryptedPassword,
                    ["server"] = server,
                    ["useLoginFlag"] = useLoginFlag,
                });
            }

            // Character creation. Empty CharacterName → no Character (Account alone is enough
            // to reach charselect, where the user picks manually).
            if (!string.IsNullOrEmpty(characterName))
            {
                newCharacters.Add(new JsonObject
                {
                    ["name"] = characterName,
                    ["accountUsername"] = username,
                    ["accountServer"] = server,
                    ["characterSlot"] = characterSlot,
                    ["displayLabel"] = "",
                    ["classHint"] = "",
                    ["notes"] = "",
                });
            }
            else
            {
                FileLogger.Info($"ConfigMigrator v3→v4: account '{username}@{server}' has no CharacterName — Account-only entry, no Character generated");
            }
        }

        root["accountsV4"] = newAccounts;
        root["charactersV4"] = newCharacters;
        FileLogger.Info($"ConfigMigrator v3→v4: split {v3Rows.Count} v3 LoginAccount(s) into {newAccounts.Count} Account(s) + {newCharacters.Count} Character(s)");

        // Step 2 — Hotkey migration (two-step: combo from hotkeys.autoLoginN, target from quickLoginN)
        var hotkeysObj = root["hotkeys"]?.AsObject();
        var accountHotkeys = new JsonArray();
        var characterHotkeys = new JsonArray();

        for (int slot = 1; slot <= 4; slot++)
        {
            var combo = hotkeysObj?[$"autoLogin{slot}"]?.GetValue<string>() ?? "";
            var target = root[$"quickLogin{slot}"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(combo) || string.IsNullOrEmpty(target))
                continue;

            // Resolve target the same way runtime does: CharacterName-first, Username-fallback.
            // Routes to AccountHotkeys[slot-1] or CharacterHotkeys[slot-1] based on the source row's
            // AutoEnterWorld + CharacterName presence.
            var matchByChar = v3Rows.FirstOrDefault(r => r.CharacterName == target);
            var matchByUser = matchByChar.Username == null
                ? v3Rows.FirstOrDefault(r => r.Username == target)
                : matchByChar;
            var resolved = matchByChar.Username != null ? matchByChar : matchByUser;

            if (resolved.Username == null)
            {
                FileLogger.Warn($"ConfigMigrator v3→v4: AutoLogin{slot} target '{target}' did not resolve to any v3 account — binding dropped");
                continue;
            }

            var accountKey = $"{resolved.Username}\u0001{resolved.Server}";
            var accountName = accountKeyToName.TryGetValue(accountKey, out var n) ? n : resolved.Username;

            // Pad the right list up to slot index
            void EnsureSize(JsonArray arr, int targetSize)
            {
                while (arr.Count < targetSize)
                    arr.Add(new JsonObject { ["combo"] = "", ["targetName"] = "" });
            }

            if (matchByChar.Username != null && resolved.AutoEnterWorld)
            {
                // CharacterName match + AutoEnterWorld=true → Character family
                EnsureSize(characterHotkeys, slot);
                characterHotkeys[slot - 1] = new JsonObject
                {
                    ["combo"] = combo,
                    ["targetName"] = resolved.CharacterName,
                };
                FileLogger.Info($"ConfigMigrator v3→v4: AutoLogin{slot} '{combo}' → CharacterHotkey[{slot - 1}]={resolved.CharacterName} (enter world)");
            }
            else
            {
                // CharacterName match + AutoEnterWorld=false, OR Username-only match → Account family
                EnsureSize(accountHotkeys, slot);
                accountHotkeys[slot - 1] = new JsonObject
                {
                    ["combo"] = combo,
                    ["targetName"] = accountName,
                };
                FileLogger.Info($"ConfigMigrator v3→v4: AutoLogin{slot} '{combo}' → AccountHotkey[{slot - 1}]={accountName} (charselect)");
            }
        }

        if (accountHotkeys.Count > 0 || characterHotkeys.Count > 0)
        {
            hotkeysObj ??= new JsonObject();
            hotkeysObj["accountHotkeys"] = accountHotkeys;
            hotkeysObj["characterHotkeys"] = characterHotkeys;
            root["hotkeys"] = hotkeysObj;
        }

        // Step 3 — Team field rebinding (Character.Name preferred, Account.Name fallback)
        for (int teamN = 1; teamN <= 4; teamN++)
        {
            for (int slotM = 1; slotM <= 2; slotM++)
            {
                var key = $"team{teamN}Account{slotM}";
                var raw = root[key]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(raw)) continue;

                // Find the v3 row matching this raw target (CharacterName first, Username fallback)
                var matchByChar = v3Rows.FirstOrDefault(r => r.CharacterName == raw);
                var resolved = matchByChar.Username != null
                    ? matchByChar
                    : v3Rows.FirstOrDefault(r => r.Username == raw);

                if (resolved.Username == null)
                {
                    FileLogger.Warn($"ConfigMigrator v3→v4: Team {teamN} Slot {slotM}: target '{raw}' did not resolve — leaving blank");
                    root[key] = "";
                    continue;
                }

                if (!string.IsNullOrEmpty(resolved.CharacterName))
                {
                    // Prefer Character.Name (member will enter world per type semantics)
                    root[key] = resolved.CharacterName;
                    if (raw != resolved.CharacterName)
                        FileLogger.Info($"ConfigMigrator v3→v4: Team {teamN} Slot {slotM} rebound '{raw}' → '{resolved.CharacterName}' (Character)");
                }
                else
                {
                    // Account-only fallback (member stops at charselect unless team override)
                    var accountKey = $"{resolved.Username}\u0001{resolved.Server}";
                    var accountName = accountKeyToName.TryGetValue(accountKey, out var n) ? n : resolved.Username;
                    root[key] = accountName;
                    FileLogger.Warn($"ConfigMigrator v3→v4: Team {teamN} Slot {slotM} resolved to Account '{accountName}' (no character) — this member will stop at charselect unless Team{teamN}AutoEnter overrides");
                }
            }
        }

        // Step 4 — Team{N}AutoEnter preservation: no-op (already in correct shape on root)
        // Step 5 — TrayClickConfig action strings: no-op (ExecuteTrayAction dispatcher handles routing in Phase 3+)

        // Move legacy "characters" (CharacterProfile data) to "characterAliases" preserving original
        // shape; CharacterAlias has identical fields (Name, Class, Notes, SlotIndex, PriorityOverride).
        // Keep "characters" key populated too — Phase 1 LegacyCharacterProfiles still reads from it.
        // The new "characters" key (v4 launch targets) was already written above as charactersV4.
        if (root["characters"] is JsonNode oldChars)
        {
            root["characterAliases"] = oldChars.DeepClone();
            FileLogger.Info($"ConfigMigrator v3→v4: copied {(oldChars.AsArray()).Count} character profile(s) to characterAliases");
        }
    }
}
