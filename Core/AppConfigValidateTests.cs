// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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

        // Case 4 (v3.14.10): empty-Name FK repair. v3.14.7's broken Note→Name
        // dialog could leave Name="" when the user saved with an empty Note
        // field; v3.14.8's migration didn't repair these (its !IsNullOrEmpty
        // guard skipped them). Validate() now restores the auto-shadow
        // invariant: Name="" + Username!="" → Name = Username.
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 4,
                Accounts = new List<Account>
                {
                    new() { Name = "", Username = "gotquiz", EncryptedPassword = "x" },
                    new() { Name = "nate", Username = "gotquiz1", EncryptedPassword = "y" },
                },
            };
            cfg.Validate();
            failures += Assert("empty-name shadowed to Username", cfg.Accounts[0].Name, "gotquiz");
            failures += Assert("non-empty Name preserved", cfg.Accounts[1].Name, "nate");
        }

        // Case 5 (v3.14.10): empty-Name repair is idempotent. Running Validate()
        // twice on the same config must not double-mutate or drift.
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 4,
                Accounts = new List<Account>
                {
                    new() { Name = "", Username = "solo", EncryptedPassword = "x" },
                },
            };
            cfg.Validate();
            cfg.Validate();
            failures += Assert("idempotent shadow", cfg.Accounts[0].Name, "solo");
        }

        // Case 6 (v3.14.10): defensive — empty Name + empty Username does
        // NOT shadow (would create an account whose Name='' equals
        // Username='', defeating the FK invariant). The repair only fires
        // when there's something useful to copy.
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 4,
                Accounts = new List<Account>
                {
                    new() { Name = "", Username = "", EncryptedPassword = "" },
                },
            };
            cfg.Validate();
            failures += Assert("no-shadow when Username also empty", cfg.Accounts[0].Name, "");
        }

        // Case 7 (v3.15.2): clamp out-of-range LaunchConfig timing knobs.
        // Hand-edited JSON could set Burst1ActivationSettleMs=-1 (would block
        // Thread.Sleep indefinitely) or PostBurst1WaitMs=999999 (would stall
        // autologin for 16+ minutes). Validate() must clamp to the documented
        // safe ranges before AutoLoginManager reads the values.
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 4,
                Launch = new LaunchConfig
                {
                    WaitTransitionInitialDelayMs = -1,
                    WaitTransitionSettleMs       = 0,
                    WaitTransitionPollIntervalMs = 99999,
                    Burst1ActivationSettleMs     = -100,
                    Burst1PostSubmitMs           = 0,
                    Burst2ActivationSettleMs     = 50,
                    Burst2PostKeystrokeMs        = -1,
                    PostBurst1WaitMs             = 0,
                    BridgeInitWaitMs             = 0,
                    StaleSessionWaitMs           = 100,    // below 10000 floor
                },
            };
            cfg.Validate();
            failures += Assert("clamp WaitTransitionInitialDelayMs floor",
                cfg.Launch.WaitTransitionInitialDelayMs, 100);
            failures += Assert("clamp WaitTransitionSettleMs floor",
                cfg.Launch.WaitTransitionSettleMs, 100);
            failures += Assert("clamp WaitTransitionPollIntervalMs ceiling",
                cfg.Launch.WaitTransitionPollIntervalMs, 5000);
            failures += Assert("clamp Burst1ActivationSettleMs floor",
                cfg.Launch.Burst1ActivationSettleMs, 100);
            failures += Assert("clamp Burst1PostSubmitMs floor",
                cfg.Launch.Burst1PostSubmitMs, 100);
            failures += Assert("clamp Burst2ActivationSettleMs floor",
                cfg.Launch.Burst2ActivationSettleMs, 100);
            failures += Assert("clamp Burst2PostKeystrokeMs floor",
                cfg.Launch.Burst2PostKeystrokeMs, 100);
            failures += Assert("clamp PostBurst1WaitMs floor",
                cfg.Launch.PostBurst1WaitMs, 500);
            // v3.15.8: floor lowered 500 → 0 (the char-list wait loop in
            // AutoLoginManager is the actual bridge-readiness gate; this Sleep
            // is now a vestigial settle pause). Test input was 0, expected
            // value follows the new floor.
            failures += Assert("clamp BridgeInitWaitMs floor",
                cfg.Launch.BridgeInitWaitMs, 0);
            failures += Assert("clamp StaleSessionWaitMs floor",
                cfg.Launch.StaleSessionWaitMs, 10000);
        }

        // Case 8 (v3.15.2): in-range LaunchConfig values pass through unchanged.
        // Critical for the dual-box-validated tuning workflow — the user adjusts
        // a knob within range, the value must survive Validate() unmodified.
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 4,
                Launch = new LaunchConfig
                {
                    Burst1ActivationSettleMs = 250,
                    Burst2PostKeystrokeMs    = 350,
                    PostBurst1WaitMs         = 1500,
                    StaleSessionWaitMs       = 45000,
                },
            };
            cfg.Validate();
            failures += Assert("in-range Burst1ActivationSettleMs preserved",
                cfg.Launch.Burst1ActivationSettleMs, 250);
            failures += Assert("in-range Burst2PostKeystrokeMs preserved",
                cfg.Launch.Burst2PostKeystrokeMs, 350);
            failures += Assert("in-range PostBurst1WaitMs preserved",
                cfg.Launch.PostBurst1WaitMs, 1500);
            failures += Assert("in-range StaleSessionWaitMs preserved",
                cfg.Launch.StaleSessionWaitMs, 45000);
        }

        // Case 9 (v3.15.2): null-entry guards remove literal-null entries from
        // List<T> properties. Hand-edited JSON like `"directSwitchKeys": [null, "F1"]`
        // must not survive Validate() — downstream consumers don't always null-check.
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 4,
                CharacterAliases = new List<CharacterAlias> { null!, new() { Name = "Real" } },
                CustomVideoPresets = new List<string> { null!, "Preset1", null! },
            };
            cfg.Hotkeys.DirectSwitchKeys = new List<string> { null!, "Alt+1" };
            cfg.Hotkeys.AccountHotkeys = new List<HotkeyBinding>
            {
                null!,
                new() { Combo = "Ctrl+1", TargetName = "X" },
            };
            cfg.Validate();
            failures += Assert("CharacterAliases null removed", cfg.CharacterAliases.Count, 1);
            failures += Assert("CustomVideoPresets nulls removed", cfg.CustomVideoPresets.Count, 1);
            failures += Assert("DirectSwitchKeys null removed", cfg.Hotkeys.DirectSwitchKeys.Count, 1);
            failures += Assert("AccountHotkeys null removed", cfg.Hotkeys.AccountHotkeys.Count, 1);
        }

        Console.WriteLine(failures == 0
            ? "AppConfigValidateTests: all 9 cases PASSED"
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
