// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System;
using System.Collections.Generic;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Unit tests for AppConfig.Validate()'s defense-in-depth resync blocks.
/// Invoked via the --test-config-validate CLI flag from Program.cs. RunAll()
/// returns 0 on all passes, 1 on any assertion failure. Program.cs maps
/// unhandled exceptions to exit code 2.
/// </summary>
public static class AppConfigValidateTests
{
    public static int RunAll()
    {
        int failures = 0;

        // Case 1: CharacterAliases resync — when a v4 config has populated
        // LegacyCharacterProfiles but empty CharacterAliases (hand-edit, partial
        // migration, or downgrade-then-upgrade drift), Validate() re-derives all
        // five fields (Name, Class, Notes, SlotIndex, PriorityOverride) into
        // CharacterAliases so AffinityManager.FindSlotPriorityOverride keeps
        // working after Phase 5b's swap to the v4 field.
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 4,
                LegacyCharacterProfiles = new List<CharacterProfile>
                {
                    new() { Name = "Backup", Class = "Cleric", Notes = "main heal",
                            SlotIndex = 2, PriorityOverride = "AboveNormal" },
                },
                CharacterAliases = new List<CharacterAlias>(),
            };
            cfg.Validate();
            failures += Assert("resync count", cfg.CharacterAliases.Count, 1);
            failures += Assert("resync name", cfg.CharacterAliases[0].Name, "Backup");
            failures += Assert("resync class", cfg.CharacterAliases[0].Class, "Cleric");
            failures += Assert("resync notes", cfg.CharacterAliases[0].Notes, "main heal");
            failures += Assert("resync slotIndex", cfg.CharacterAliases[0].SlotIndex, 2);
            failures += Assert("resync priorityOverride",
                cfg.CharacterAliases[0].PriorityOverride, "AboveNormal");
        }

        // Case 2: no-resync when CharacterAliases is already populated. Guards
        // against a future refactor that re-derives aliases unconditionally
        // (which would overwrite user edits).
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 4,
                LegacyCharacterProfiles = new List<CharacterProfile>
                {
                    new() { Name = "LegacyName", SlotIndex = 1 },
                },
                CharacterAliases = new List<CharacterAlias>
                {
                    new() { Name = "V4Name", SlotIndex = 9, PriorityOverride = "High" },
                },
            };
            cfg.Validate();
            failures += Assert("no-resync count", cfg.CharacterAliases.Count, 1);
            failures += Assert("no-resync name kept", cfg.CharacterAliases[0].Name, "V4Name");
            failures += Assert("no-resync slotIndex kept", cfg.CharacterAliases[0].SlotIndex, 9);
            failures += Assert("no-resync priority kept",
                cfg.CharacterAliases[0].PriorityOverride, "High");
        }

        // Case 3: no-resync when ConfigVersion < 4 (old v3 configs must not
        // trigger the v4 defense block — those configs go through
        // MigrateV3ToV4 first, which seeds CharacterAliases correctly).
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 3,
                LegacyCharacterProfiles = new List<CharacterProfile>
                {
                    new() { Name = "ShouldNotResync", SlotIndex = 1 },
                },
                CharacterAliases = new List<CharacterAlias>(),
            };
            cfg.Validate();
            failures += Assert("v3-no-resync count", cfg.CharacterAliases.Count, 0);
        }

        Console.WriteLine(failures == 0
            ? "AppConfigValidateTests: all 3 cases PASSED"
            : $"AppConfigValidateTests: {failures} assertion failure(s)");
        return failures == 0 ? 0 : 1;
    }

    private static int Assert<T>(string name, T actual, T expected)
    {
        if (Equals(actual, expected))
        {
            Console.WriteLine($"    ok: {name}");
            return 0;
        }
        Console.WriteLine($"    FAIL: {name} (expected '{expected}', got '{actual}')");
        return 1;
    }
}
