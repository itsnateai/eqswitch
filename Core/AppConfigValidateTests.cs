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

        // Case 10 (v3.15.10): ceiling clamps for LaunchConfig timing knobs +
        // top-level LoginScreenDelayMs / WarmupDwellMs. Verifier-flagged gap —
        // Case 7 covered floor clamps but no ceiling assertion existed for
        // these knobs. Out-of-range hand-edits could otherwise survive
        // Validate() and produce absurd timeouts in production.
        {
            var cfg = new AppConfig
            {
                LoginScreenDelayMs = 999_999,
                WarmupDwellMs      = 999_999,
                Launch = new LaunchConfig
                {
                    WaitTransitionInitialDelayMs = 999_999,
                    WaitTransitionSettleMs       = 999_999,
                    WaitTransitionPollIntervalMs = 999_999,
                    Burst1ActivationSettleMs     = 999_999,
                    Burst1PostSubmitMs           = 999_999,
                    Burst2ActivationSettleMs     = 999_999,
                    Burst2PostKeystrokeMs        = 999_999,
                    PostBurst1WaitMs             = 999_999,
                    BridgeInitWaitMs             = 999_999,
                    StaleSessionWaitMs           = 999_999,
                    NumClients                   = 999,
                    LaunchDelayMs                = 999_999,
                    FixDelayMs                   = 999_999,
                },
            };
            cfg.Validate();
            // Top-level
            failures += Assert("clamp LoginScreenDelayMs ceiling",
                cfg.LoginScreenDelayMs, 10000);
            failures += Assert("clamp WarmupDwellMs ceiling",
                cfg.WarmupDwellMs, 15000);
            // LaunchConfig
            failures += Assert("clamp WaitTransitionInitialDelayMs ceiling",
                cfg.Launch.WaitTransitionInitialDelayMs, 10000);
            failures += Assert("clamp WaitTransitionSettleMs ceiling",
                cfg.Launch.WaitTransitionSettleMs, 10000);
            failures += Assert("clamp WaitTransitionPollIntervalMs ceiling",
                cfg.Launch.WaitTransitionPollIntervalMs, 5000);
            failures += Assert("clamp Burst1ActivationSettleMs ceiling",
                cfg.Launch.Burst1ActivationSettleMs, 5000);
            failures += Assert("clamp Burst1PostSubmitMs ceiling",
                cfg.Launch.Burst1PostSubmitMs, 5000);
            failures += Assert("clamp Burst2ActivationSettleMs ceiling",
                cfg.Launch.Burst2ActivationSettleMs, 5000);
            failures += Assert("clamp Burst2PostKeystrokeMs ceiling",
                cfg.Launch.Burst2PostKeystrokeMs, 5000);
            failures += Assert("clamp PostBurst1WaitMs ceiling",
                cfg.Launch.PostBurst1WaitMs, 30000);
            failures += Assert("clamp BridgeInitWaitMs ceiling",
                cfg.Launch.BridgeInitWaitMs, 30000);
            failures += Assert("clamp StaleSessionWaitMs ceiling",
                cfg.Launch.StaleSessionWaitMs, 120000);
            failures += Assert("clamp NumClients ceiling",
                cfg.Launch.NumClients, 6);
            failures += Assert("clamp LaunchDelayMs ceiling",
                cfg.Launch.LaunchDelayMs, 30000);
            failures += Assert("clamp FixDelayMs ceiling",
                cfg.Launch.FixDelayMs, 120000);

            // Floor coverage for the two top-level knobs (Case 7 only
            // covered LaunchConfig floors).
            var floorCfg = new AppConfig
            {
                LoginScreenDelayMs = 0,
                WarmupDwellMs      = -100,
            };
            floorCfg.Validate();
            failures += Assert("clamp LoginScreenDelayMs floor",
                floorCfg.LoginScreenDelayMs, 5000);
            failures += Assert("clamp WarmupDwellMs floor",
                floorCfg.WarmupDwellMs, 0);

            // In-range pass-through for the two top-level knobs (Case 8
            // covers LaunchConfig in-range but missed these). A mutation
            // that accidentally clamped a valid 7000ms value to the floor
            // would otherwise survive both the floor and ceiling tests.
            var inRangeCfg = new AppConfig
            {
                LoginScreenDelayMs = 7000,
                WarmupDwellMs      = 8000,
            };
            inRangeCfg.Validate();
            failures += Assert("in-range LoginScreenDelayMs preserved",
                inRangeCfg.LoginScreenDelayMs, 7000);
            failures += Assert("in-range WarmupDwellMs preserved",
                inRangeCfg.WarmupDwellMs, 8000);
        }

        // v3.22.91 Case: WindowMode default is Windowed on a fresh config (flipped
        // from Fullscreen — Windowed is the preferred multibox shape).
        {
            var cfg = new AppConfig();
            failures += Assert("windowMode default", cfg.Layout.WindowMode, WindowMode.Windowed);
        }

        // v3.22.91 Case: out-of-range WindowMode (corrupt/hand-edited JSON) → Windowed
        // (was Fullscreen; the reset target follows the new default).
        {
            var cfg = new AppConfig();
            cfg.Layout.WindowMode = (WindowMode)99;
            cfg.Validate();
            failures += Assert("windowMode invalid clamps", cfg.Layout.WindowMode, WindowMode.Windowed);
        }

        // v3.22.81 Case (Phase 2): WindowMode.Windowed is now IMPLEMENTED — the
        // Phase-1 pin-to-Fullscreen clamp is removed, so Validate preserves it.
        {
            var cfg = new AppConfig();
            cfg.Layout.WindowMode = WindowMode.Windowed;
            cfg.Validate();
            failures += Assert("windowMode Windowed preserved (Phase 2)", cfg.Layout.WindowMode, WindowMode.Windowed);
        }

        // v3.22.80 Case: a main-card WindowMode forces SlimTitlebar true (legacy
        // non-slim config migrates to the WindowMode look; card never lies about
        // the rendered window).
        {
            var cfg = new AppConfig();
            cfg.Layout.WindowMode = WindowMode.Fullscreen;
            cfg.Layout.SlimTitlebar = false;
            cfg.Validate();
            failures += Assert("slim resync from windowMode", cfg.Layout.SlimTitlebar, true);
        }

        // v3.22.81 Case (Phase 2): a hand-edited Windowed + SlimTitlebar=false
        // config preserves Windowed AND forces SlimTitlebar true (both modes are
        // slim-managed; only the resync block fires now — the clamp is gone).
        {
            var cfg = new AppConfig();
            cfg.Layout.WindowMode = WindowMode.Windowed;
            cfg.Layout.SlimTitlebar = false;
            cfg.Validate();
            failures += Assert("Windowed+nonSlim → Windowed preserved", cfg.Layout.WindowMode, WindowMode.Windowed);
            failures += Assert("Windowed+nonSlim → slim true", cfg.Layout.SlimTitlebar, true);
        }

        // v3.22.91 Case: ForceWindowedMode is a pinned invariant — Validate forces it
        // true even if a config (or a seeded eqclient.ini) had it false. Window
        // management requires it; an exclusive-fullscreen client can't be managed.
        {
            var cfg = new AppConfig();
            cfg.EQClientIni.ForceWindowedMode = false;
            cfg.Validate();
            failures += Assert("forceWindowedMode pinned true", cfg.EQClientIni.ForceWindowedMode, true);
        }

        // Case 16 (v3.23.1): QuickLogin slot normalization — trim whitespace + drop a
        // prefix-with-empty-name back to unassigned; clean/empty values pass through.
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 4,
                QuickLogin1 = "  char:Natedogg  ",
                QuickLogin2 = "char:",
                QuickLogin3 = "acct:gotquiz",
                QuickLogin4 = "",
            };
            cfg.Validate();
            failures += Assert("quicklogin trim", cfg.QuickLogin1, "char:Natedogg");
            failures += Assert("quicklogin empty-prefix reset", cfg.QuickLogin2, "");
            failures += Assert("quicklogin clean preserved", cfg.QuickLogin3, "acct:gotquiz");
            failures += Assert("quicklogin empty preserved", cfg.QuickLogin4, "");
        }

        // Case 17 (v3.23.2): interior-whitespace name-trim + mutated flag. A hand-edited
        // "char:  Nate  " must normalize to "char:Nate" (else dispatch looks up "  Nate"
        // and misses), and Validate() must report it mutated so the fix persists to disk.
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 4,
                QuickLogin1 = "char:  Nate  ",
            };
            bool mutated = cfg.Validate();
            failures += Assert("quicklogin interior-whitespace trimmed", cfg.QuickLogin1, "char:Nate");
            failures += Assert("quicklogin normalization sets mutated", mutated, true);
        }

        // Case 18 (v3.24.15): MultiMonTaskbarMode default is ShowTaskbars on a fresh config —
        // the "Show taskbars" toggle was removed; multimonitor always shows the 2nd taskbar.
        {
            var cfg = new AppConfig();
            failures += Assert("multimon taskbar default ShowTaskbars",
                cfg.Layout.MultiMonTaskbarMode, MultiMonTaskbarMode.ShowTaskbars);
        }

        // Case 19 (v3.24.15): a saved / hand-edited CoverAll config is force-migrated to
        // ShowTaskbars (the retired mode is unreachable from any config path) and Validate()
        // reports it mutated so the migration persists to disk.
        {
            var cfg = new AppConfig();
            cfg.Layout.MultiMonTaskbarMode = MultiMonTaskbarMode.CoverAll;
            bool mutated = cfg.Validate();
            failures += Assert("CoverAll migrated to ShowTaskbars",
                cfg.Layout.MultiMonTaskbarMode, MultiMonTaskbarMode.ShowTaskbars);
            failures += Assert("CoverAll migration sets mutated", mutated, true);
        }

        // Case 20 (v3.24.15): an out-of-range (corrupt) MultiMonTaskbarMode enum clamps to
        // ShowTaskbars — the reset target follows the new default, not the retired CoverAll.
        {
            var cfg = new AppConfig();
            cfg.Layout.MultiMonTaskbarMode = (MultiMonTaskbarMode)99;
            cfg.Validate();
            failures += Assert("out-of-range multimon clamps to ShowTaskbars",
                cfg.Layout.MultiMonTaskbarMode, MultiMonTaskbarMode.ShowTaskbars);
        }

        // Case 21 (v3.24.15): team slots get the SAME typed-value normalization as QuickLogin1-4 —
        // a hand-edited "char:  Eisley " trims to "char:Eisley" (else TeamSlotResolver looks up a
        // space-padded name and silently misses), an empty-name prefix drops to "", a legacy-bare
        // value is trimmed but kept bare, and Validate() reports mutated.
        {
            var cfg = new AppConfig
            {
                ConfigVersion = 4,
                Team1Account1 = "char:  Eisley ",
                Team1Account2 = "acct:",
                Team2Account1 = "Eisley",          // legacy-bare — trimmed, prefix-free, preserved
                Team2Account2 = "",
            };
            bool mutated = cfg.Validate();
            failures += Assert("team slot interior-whitespace trimmed", cfg.Team1Account1, "char:Eisley");
            failures += Assert("team slot empty-prefix reset", cfg.Team1Account2, "");
            failures += Assert("team slot legacy-bare preserved", cfg.Team2Account1, "Eisley");
            failures += Assert("team slot empty preserved", cfg.Team2Account2, "");
            failures += Assert("team slot normalization sets mutated", mutated, true);
        }

        Console.WriteLine(failures == 0
            ? "AppConfigValidateTests: all 21 cases PASSED"
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
