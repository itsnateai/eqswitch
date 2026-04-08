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

                // Future migrations:
                // case 1:
                //     MigrateV1ToV2(root);
                //     break;

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
}
