// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.Config;

/// <summary>
/// Root configuration object. Stored as eqswitch-config.json alongside the exe.
/// Replaces the old INI-based config — no more type comparison bugs.
/// </summary>
public class AppConfig
{
    /// <summary>Schema version for config migration. Bump when making breaking changes.</summary>
    public const int CurrentConfigVersion = 5;

    public int ConfigVersion { get; set; } = CurrentConfigVersion;
    public bool IsFirstRun { get; set; } = true;
    public string EQPath { get; set; } = @"C:\EverQuest";
    public string EQProcessName { get; set; } = "eqgame";

    // Window layout
    public WindowLayout Layout { get; set; } = new();

    // CPU affinity
    public AffinityConfig Affinity { get; set; } = new();

    // Hotkeys
    public HotkeyConfig Hotkeys { get; set; } = new();

    // Launching
    public LaunchConfig Launch { get; set; } = new();

    // Background FPS Throttling

    // Picture-in-Picture
    public PipConfig Pip { get; set; } = new();

    // ── v3 legacy character profiles (renamed in v4 transition; consumed by AffinityManager until Phase 5) ──
    [JsonPropertyName("characters")]
    public List<CharacterProfile> LegacyCharacterProfiles { get; set; } = new();

    // ── v4 launch-target characters (NEW; populated by MigrateV3ToV4; consumed from Phase 3 onwards) ──
    [JsonPropertyName("charactersV4")]
    public List<Character> Characters { get; set; } = new();

    // ── v4 cosmetic/priority metadata (NEW; populated by migration as a copy of legacy character profiles) ──
    [JsonPropertyName("characterAliases")]
    public List<CharacterAlias> CharacterAliases { get; set; } = new();

    // Video
    public List<string> CustomVideoPresets { get; set; } = new();

    // Paths
    public string GinaPath { get; set; } = "";
    public string GamparsePath { get; set; } = "";
    public string EqLogParserPath { get; set; } = "";
    public string DalayaPatcherPath { get; set; } = "";
    public string NotesPath { get; set; } = "";

    // ── v3 legacy login accounts (renamed in v4 transition; consumed by tray/settings/autologin until Phases 2-4) ──
    [JsonPropertyName("accounts")]
    public List<LoginAccount> LegacyAccounts { get; set; } = new();

    // ── v4 launch-target accounts (NEW; populated by MigrateV3ToV4; consumed from Phase 2 onwards) ──
    [JsonPropertyName("accountsV4")]
    public List<Account> Accounts { get; set; } = new();

    /// <summary>Pre-BURST settle wallclock (ms, default 5s) used as the fallback
    /// when the SHM warmup ritual didn't run for a given launch. AutoLoginManager
    /// chooses WarmupDwellMs when warmup succeeded, this value otherwise. The
    /// v3.14.9 PATH D refactor narrowed the consumption — the comment that
    /// previously claimed this was "no longer consumed" was stale; the
    /// `dwellMs = warmupRan ? warmupDwellMs : loginScreenDelayMs` branch at
    /// AutoLoginManager line ~526 still reads it. Fixed in v3.15.2.</summary>
    public int LoginScreenDelayMs { get; set; } = 5500;

    /// <summary>Pre-BURST DI8 settle window in ms (default 4s). PATH D: after
    /// the DLL publishes a non-zero gameState, RunCredentialEntry sleeps this
    /// long before flipping KeyShm::Active and firing BURST 1. The wallclock
    /// gives EQ time to stabilize its own DI8 state (login-screen widget
    /// init, default cooperative-level setup) before our IAT hooks + DI8
    /// proxy coerce BACKGROUND mode. Empirical baseline: 4s sufficed for
    /// dual-box logins on the user's hardware in v3.12.0–v3.14.8 (despite
    /// the SHM warmup ritual that surrounded it adding intermittent
    /// modal-collision risk). Bump to 5000–6000 if BURST 1's first chars get
    /// dropped on slower hardware; drop toward 2000 if logins stay clean.</summary>
    public int WarmupDwellMs { get; set; } = 4000;

    /// <summary>When true, auto-login continues past character select into the world.
    /// When false, stops at the character select screen.</summary>
    public bool AutoEnterWorld { get; set; } = false;

    /// <summary>Log file trim threshold in MB. Files over this size get trimmed to this size.</summary>
    public int LogTrimThresholdMB { get; set; } = 50;

    /// <summary>Username of the account bound to Quick Login slot 1 (empty = unbound).</summary>
    public string QuickLogin1 { get; set; } = "";

    /// <summary>Username of the account bound to Quick Login slot 2 (empty = unbound).</summary>
    public string QuickLogin2 { get; set; } = "";

    /// <summary>Username of the account bound to Quick Login slot 3 (empty = unbound).</summary>
    public string QuickLogin3 { get; set; } = "";

    /// <summary>Username of the account bound to Quick Login slot 4 (empty = unbound).</summary>
    public string QuickLogin4 { get; set; } = "";

    // Autologin Teams — 12 teams × 2 slots. Teams 1-4 are bindable to global hotkeys
    // (Hotkeys.TeamLogin1-4) and Teams 1-6 are exposed in the trayclick action dropdown;
    // Teams 7-12 are tray-right-click-submenu only by design (v3.22.58, 2026-05-27).
    public string Team1Account1 { get; set; } = "";
    public string Team1Account2 { get; set; } = "";
    public string Team2Account1 { get; set; } = "";
    public string Team2Account2 { get; set; } = "";
    public string Team3Account1 { get; set; } = "";
    public string Team3Account2 { get; set; } = "";
    public string Team4Account1 { get; set; } = "";
    public string Team4Account2 { get; set; } = "";
    public string Team5Account1 { get; set; } = "";
    public string Team5Account2 { get; set; } = "";
    public string Team6Account1 { get; set; } = "";
    public string Team6Account2 { get; set; } = "";
    public string Team7Account1 { get; set; } = "";
    public string Team7Account2 { get; set; } = "";
    public string Team8Account1 { get; set; } = "";
    public string Team8Account2 { get; set; } = "";
    public string Team9Account1 { get; set; } = "";
    public string Team9Account2 { get; set; } = "";
    public string Team10Account1 { get; set; } = "";
    public string Team10Account2 { get; set; } = "";
    public string Team11Account1 { get; set; } = "";
    public string Team11Account2 { get; set; } = "";
    public string Team12Account1 { get; set; } = "";
    public string Team12Account2 { get; set; } = "";

    // Per-team Enter World toggle — BINARY interpretation per user 2026-04-15:
    //   true  = team enters world on launch (default, for Character teams)
    //   false = team stops at charselect (for crafter teams)
    // Existing v4 configs that predate this semantic shift keep their stored value;
    // users with stale "false" flags on Character teams can toggle on via Autologin Teams dialog.
    // Team{N}AutoEnter removed — destination is dictated by slot kind alone.
    // Old configs containing these JSON keys are silently ignored by System.Text.Json
    // (unknown members) on load; next save drops the keys from the file.

    // Tray Click Actions
    public TrayClickConfig TrayClick { get; set; } = new();


    /// <summary>
    /// Custom tray icon path. Empty = use built-in Stone icon (default).
    /// Users can browse to any .ico file on their system.
    /// </summary>
    public string CustomIconPath { get; set; } = "";

    // Misc
    // ShowTooltipErrors / MinimizeToTray removed v3.15.10 — verifier audit found
    // both fields were declared and round-tripped via ReloadConfig but had ZERO
    // actual consumers. SettingsForm.BuildAppConfig also dropped them on every
    // Apply (silent reset to default), but the reset was harmless because
    // nothing read the value. Existing configs with these JSON keys deserialize
    // cleanly — System.Text.Json ignores unknown properties by default.
    public bool RunAtStartup { get; set; } = false;

    /// <summary>
    /// Phase 5a: once true, the one-time "Quick Login slots are now under Direct
    /// Bindings" deprecation banner on the Hotkeys tab stays hidden. Flipped by the
    /// banner's Dismiss button. Ignored by runtime dispatch. Persists across sessions.
    /// </summary>
    public bool HotkeysLegacyBannerDismissed { get; set; } = false;


    /// <summary>Last Settings window position [x, y]. Empty = center screen.</summary>
    public int[] SettingsWindowPos { get; set; } = Array.Empty<int>();

    /// <summary>Duration in ms for floating tooltips (default 700ms).</summary>
    public int TooltipDurationMs { get; set; } = 700;

    /// <summary>
    /// Master switch for status tooltips fired via TrayManager.ShowBalloon
    /// (login progress, "Fix Windows", "PiP enabled", etc). When false the
    /// helper short-circuits before scheduling FloatingTooltip.Show, so
    /// AutoLoginManager.StatusUpdate stops appearing on screen. Warnings
    /// (ShowWarning) intentionally bypass this flag — real problems still
    /// surface. Settings UI: Paths tab → Startup card.
    /// </summary>
    public bool ShowTooltips { get; set; } = true;

    // CtrlHoverHelp removed v3.15.10 — was deprecated in CHANGELOG ~v3.x
    // ("Removed CtrlHoverHelp (unreliable in overflow tray)") but the field
    // declaration was left behind. No consumer in the codebase. Existing
    // configs with the JSON key deserialize cleanly (ignored as unknown).

    /// <summary>
    /// Persistent eqclient.ini overrides — applied on every save/launch.
    /// </summary>
    public EQClientIniConfig EQClientIni { get; set; } = new();

    /// <summary>Clamp numeric values to safe ranges and run one-time migrations.
    /// Call after deserialization or before applying settings from the GUI.
    /// Returns true if config was mutated.</summary>
    public bool Validate()
    {
        bool mutated = false;
        if (string.IsNullOrWhiteSpace(EQProcessName)) EQProcessName = "eqgame";
        // Security: EQProcessName must be a known game executable — prevents config-driven injection into arbitrary processes
        var allowedProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "eqgame" };
        if (!allowedProcessNames.Contains(EQProcessName))
        {
            EQProcessName = "eqgame";
        }
        // Guard against null nested objects from corrupt/hand-edited JSON
        Layout ??= new();
        Affinity ??= new();
        Launch ??= new();
        Pip ??= new();
        Hotkeys ??= new();
        TrayClick ??= new();
        EQClientIni ??= new();

        Characters ??= new();
        Accounts ??= new();
        CharacterAliases ??= new();
        LegacyAccounts ??= new();
        LegacyCharacterProfiles ??= new();
        Accounts.RemoveAll(a => a == null!);
        Characters.RemoveAll(c => c == null!);
        // v3.15.2: null-guards across all List<T> properties. A hand-edited
        // config with a literal `null` entry would otherwise NRE in any
        // consumer that iterates without its own null check. Round-1+2 only
        // covered Accounts/Characters/CharacterAliases; Round-3 verifier T2
        // caught the remaining 4 lists. CustomVideoPresets is sufficiently
        // late-bound (only consumed by SettingsForm video tab) but for
        // consistency it gets the same guard.
        CharacterAliases.RemoveAll(a => a == null!);
        Hotkeys.DirectSwitchKeys.RemoveAll(d => d == null!);
        Hotkeys.AccountHotkeys.RemoveAll(b => b == null!);
        Hotkeys.CharacterHotkeys.RemoveAll(b => b == null!);
        CustomVideoPresets.RemoveAll(p => p == null!);

        // v3.14.8 split: Account.Name became an internal FK shadow of Username
        // and Notes is the new user-facing free-form column. For accounts that
        // predate the split, the legacy "note" lived in Name. Copy it once into
        // Notes for display continuity but leave Name unchanged — any existing
        // hotkey / team-slot / tray binding still resolves by TargetName == Name.
        // Idempotent: only fires when Notes is empty AND Name diverges from
        // Username AND Name is non-empty, so a second pass is a no-op.
        //
        // v3.14.10 follow-up: v3.14.7's "Note column writes to Account.Name"
        // dialog (subsequently fixed in v3.14.8) left some accounts with
        // Name="" when the user saved with an empty Note field. The v3.14.8
        // migration above doesn't repair these — its `!IsNullOrEmpty(a.Name)`
        // guard skips them. Empty Name breaks every FK lookup (hotkey
        // TargetName, team-slot SlotOption.Value, tray-menu binding,
        // FindAccountByName at :333), so re-establish the v3.14.8 auto-shadow
        // invariant: when Name is empty AND Username is non-empty, set
        // Name = Username so bindings have a stable key from this load
        // forward. Idempotent (second pass is a no-op since Name is now
        // non-empty). Bindings that referenced the previously-empty Name
        // stayed unresolvable; bindings created post-migration resolve to
        // Username, matching what new accounts would create.
        foreach (var a in Accounts)
        {
            if (string.IsNullOrEmpty(a.Notes) &&
                !string.IsNullOrEmpty(a.Name) &&
                !a.Name.Equals(a.Username, StringComparison.Ordinal))
            {
                a.Notes = a.Name;
                mutated = true;
            }
            if (string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.Username))
            {
                a.Name = a.Username;
                mutated = true;
            }
        }

        // Defense-in-depth: if a v4 config arrives with empty v4 lists but populated
        // legacy data (hand-edit, dev build, or partial migration), re-derive v4 from
        // legacy. Otherwise Phase 3's tray would render empty Accounts/Characters
        // submenus while SettingsForm's Accounts tab shows data. Uses the same split
        // logic the migrator and SettingsForm.ApplySettings use.
        if (ConfigVersion >= 4 && LegacyAccounts.Count > 0 && Accounts.Count == 0 && Characters.Count == 0)
        {
            var (v4Accounts, v4Characters) = LoginAccountSplitter.Split(LegacyAccounts);
            Accounts = v4Accounts;
            Characters = v4Characters;
            FileLogger.Warn($"AppConfig.Validate: v4 lists were empty with {LegacyAccounts.Count} legacy accounts — re-derived {Accounts.Count} Account(s) + {Characters.Count} Character(s)");
            mutated = true;
        }

        // (Username, Server) uniqueness scan. AccountEditDialog already prevents
        // dialog-created duplicates inline; this catches hand-edited configs and
        // migration edge-cases (LoginAccountSplitter on legacy data with the same
        // username spread across multiple character rows). Tolerant on read —
        // log + count, do NOT auto-delete (risks losing the user's notes /
        // password). The strict enforcement still lives in AccountEditDialog
        // for any new Add/Edit. v3.15.x (post-ship): surfaced when duplicate
        // gotquiz config entries confused autologin lookup at session start.
        {
            // Case-insensitive: EQ usernames are server-side case-insensitive
            // (gotquiz == Gotquiz routes the same login). Matches the
            // LoginAccountSplitter's keyToCanonicalUsername behavior.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int dupCount = 0;
            foreach (var a in Accounts)
            {
                if (string.IsNullOrEmpty(a.Username)) continue;
                var key = $"{a.Username}@{a.Server}";
                if (!seen.Add(key))
                {
                    dupCount++;
                    FileLogger.Warn($"AppConfig.Validate: duplicate Account (Username,Server) = '{key}' — Account.Name='{a.Name}', Notes='{a.Notes}'");
                }
            }
            if (dupCount > 0)
            {
                FileLogger.Warn($"AppConfig.Validate: {dupCount} duplicate Account(s) detected — autologin selection may be ambiguous; resolve via Settings → Accounts.");
            }
        }

        // Same safety net for CharacterAliases: the Phase 1 migrator deep-clones
        // LegacyCharacterProfiles into CharacterAliases once, but nothing re-derives
        // it on save/reload. Hand-edits or migrator edge-cases that leave aliases
        // empty while legacy profiles populated would silently drop priority
        // overrides at AffinityManager.FindSlotPriorityOverride after Phase 5b.
        // Phase 6 drops LegacyCharacterProfiles; this block goes with it.
        if (ConfigVersion >= 4 && LegacyCharacterProfiles.Count > 0 && CharacterAliases.Count == 0)
        {
            CharacterAliases = LegacyCharacterProfiles
                .Select(p => new CharacterAlias
                {
                    Name = p.Name,
                    Class = p.Class,
                    Notes = p.Notes,
                    SlotIndex = p.SlotIndex,
                    PriorityOverride = p.PriorityOverride,
                })
                .ToList();
            FileLogger.Warn($"AppConfig.Validate: characterAliases was empty with {LegacyCharacterProfiles.Count} legacy profile(s) — re-derived {CharacterAliases.Count} alias(es)");
            mutated = true;
        }
        // Range matches SettingsForm NUD (300..5000ms). On/off lives in the
        // ShowTooltips checkbox — no zero-as-suppression backdoor here either.
        TooltipDurationMs = Math.Clamp(TooltipDurationMs, 300, 5000);

        Layout.TargetMonitor = Math.Clamp(Layout.TargetMonitor, 0, 8);
        Layout.SecondaryMonitor = Math.Clamp(Layout.SecondaryMonitor, -1, 8);
        Layout.TopOffset = Math.Clamp(Layout.TopOffset, -200, 200);
        Layout.TitlebarOffset = Math.Clamp(Layout.TitlebarOffset, 0, 40);
        Layout.BottomOffset = Math.Clamp(Layout.BottomOffset, 0, 100);
        // v3.22.54: clamp the horizontal nudge to a sane range. Values
        // beyond ±10 are almost certainly a config typo; the legitimate
        // Win11-DPI-sliver fix is ±1.
        Layout.HorizontalNudgePx = Math.Clamp(Layout.HorizontalNudgePx, -10, 10);

        Affinity.LaunchRetryCount = Math.Clamp(Affinity.LaunchRetryCount, 0, 20);
        Affinity.LaunchRetryDelayMs = Math.Clamp(Affinity.LaunchRetryDelayMs, 500, 30000);

        Launch.NumClients = Math.Clamp(Launch.NumClients, 1, 6);
        Launch.LaunchDelayMs = Math.Clamp(Launch.LaunchDelayMs, 2000, 30000);
        Launch.FixDelayMs = Math.Clamp(Launch.FixDelayMs, 1000, 120000);
        // v3.22.53 post-verifier-fix: normalize the new JSON-only opt-in.
        // Read site (TrayManager.OnLaunchOne) already calls .Trim() defensively
        // but the value persisted to disk wasn't normalized — a hand-edited
        // " gotquiz " round-tripped with the spaces still attached. Set
        // `mutated = true` only when the trim actually changed something so
        // the caller (ConfigManager.Save when Validate returns true) flushes
        // the normalized value back to disk. Distinct from the Math.Clamp
        // lines above, which silently clamp without setting `mutated` — those
        // are normalized cheap enough to re-apply on every load that
        // round-tripping doesn't pay back the extra save.
        var originalDefaultLaunchOne = Launch.DefaultLaunchOneAccount ?? string.Empty;
        var trimmedDefaultLaunchOne = originalDefaultLaunchOne.Trim();
        if (originalDefaultLaunchOne != trimmedDefaultLaunchOne)
        {
            Launch.DefaultLaunchOneAccount = trimmedDefaultLaunchOne;
            mutated = true;
        }
        else if (Launch.DefaultLaunchOneAccount == null)
        {
            // Handle the JSON-null deserialization case so downstream reads
            // never NRE on a hand-edited config with `"DefaultLaunchOneAccount": null`.
            Launch.DefaultLaunchOneAccount = string.Empty;
            mutated = true;
        }

        // v3.15.2: clamp the 10 new autologin-timing tunables. Floors are
        // calibrated against the comments next to each property in LaunchConfig
        // (e.g. "below ~300 risks deactivating before EQ consumes the keystroke").
        // Without these clamps a hand-edited JSON could set Thread.Sleep(-1)
        // (blocks indefinitely on .NET) or Thread.Sleep(0) (skips the dwell
        // entirely), producing failure modes that don't surface as crashes.
        Launch.WaitTransitionInitialDelayMs = Math.Clamp(Launch.WaitTransitionInitialDelayMs, 100, 10000);
        Launch.WaitTransitionSettleMs       = Math.Clamp(Launch.WaitTransitionSettleMs,       100, 10000);
        Launch.WaitTransitionPollIntervalMs = Math.Clamp(Launch.WaitTransitionPollIntervalMs, 100, 5000);
        Launch.Burst1ActivationSettleMs     = Math.Clamp(Launch.Burst1ActivationSettleMs,     100, 5000);
        Launch.Burst1PostSubmitMs           = Math.Clamp(Launch.Burst1PostSubmitMs,           100, 5000);
        Launch.Burst2ActivationSettleMs     = Math.Clamp(Launch.Burst2ActivationSettleMs,     100, 5000);
        Launch.Burst2PostKeystrokeMs        = Math.Clamp(Launch.Burst2PostKeystrokeMs,        100, 5000);
        Launch.PostBurst1WaitMs             = Math.Clamp(Launch.PostBurst1WaitMs,             500, 30000);
        // v3.15.8: floor lowered 500→0. The char-list wait loop in AutoLoginManager
        // (latch + ReadCharCount, 60×500ms cap) is the actual bridge-readiness gate;
        // this Sleep is a vestigial settle pause. Default is now 1ms — the loop's
        // first iteration polls without delay and absorbs any genuine bridge lag
        // via its 500ms inter-poll sleep. Users may still tune higher if their
        // setup needs a longer pre-poll buffer.
        Launch.BridgeInitWaitMs             = Math.Clamp(Launch.BridgeInitWaitMs,             0, 30000);
        // StaleSessionWaitMs floor is calibrated against Dalaya's empirical
        // 30-45s release window. Lowering risks the retry firing while the
        // server still holds the prior session, getting a second rejection.
        Launch.StaleSessionWaitMs           = Math.Clamp(Launch.StaleSessionWaitMs,           10000, 120000);
        Launch.StaleSessionPollIntervalMs   = Math.Clamp(Launch.StaleSessionPollIntervalMs,   100, 5000);
        Launch.ConnectRetryCount            = Math.Clamp(Launch.ConnectRetryCount,            0, 5);
        Launch.PostBurst2QuickFailCheckMs   = Math.Clamp(Launch.PostBurst2QuickFailCheckMs,   0, 90000);
        // Diff 4 (2026-05-15): JoinServerDirect server ID. 0 = disable wire
        // (force BURST 2 always); 1 = Dalaya default; up to 100 covers any
        // realistic eqlogin server-list index (EQ retail had ~75 servers at
        // peak — emu single-server private emus have IDs in the 1..10 range).
        // Negative values would write garbage into the SHM int32 field and
        // pass to JoinServer's `int serverID` parameter as a negative — clamp
        // floor to 0 to force the wire-disabled path on any out-of-range value.
        Launch.JoinServerId                 = Math.Clamp(Launch.JoinServerId,                 0, 100);

        LoginScreenDelayMs = Math.Clamp(LoginScreenDelayMs, 5000, 10000);
        WarmupDwellMs = Math.Clamp(WarmupDwellMs, 0, 15000);

        Pip.Opacity = Math.Clamp(Pip.Opacity, (byte)10, (byte)255);
        Pip.MaxWindows = Math.Clamp(Pip.MaxWindows, 1, 3);
        Pip.CustomWidth = Math.Clamp(Pip.CustomWidth, 100, 3840);
        Pip.CustomHeight = Math.Clamp(Pip.CustomHeight, 75, 2160);
        // Migrate old 4:3 default (320x240) to 16:9 (480x270)
        if (Pip.CustomWidth == 320 && Pip.CustomHeight == 240)
        {
            Pip.CustomWidth = 480;
            Pip.CustomHeight = 270;
        }

        // String-enum validation — fall back to defaults on garbage values from hand-edited JSON
        if (Layout.Mode is not ("single" or "multimonitor")) Layout.Mode = "single";
        // v3.22.74 + v3.22.75 (verifier convergent across passes): allowlist
        // matches the SizePreset field doc + PipConfig.GetSize() switch (which
        // handles XL=768x432, XXL=1024x576, XXXL=1600x900 as first-class
        // presets) AND normalizes case before comparison. v3.22.74 closed
        // the missing-XL/XXL/XXXL value gap; v3.22.75 closed the
        // case-sensitivity gap (T2-Sonnet + T2-Opus convergent CRITICAL):
        // pre-v3.22.75 a hand-edited "xl" or "large" got silently reset to
        // "Large" + triggered the v3.15.4 mutated-flag → spurious backup
        // write on every startup. Now: normalize to canonical PascalCase via
        // a case-insensitive map before the allowlist check, so "xl", "XL",
        // and "Xl" all settle on "XL" without firing the mutated flag.
        // Maintenance contract: if a new preset is added (e.g. XS, 4K), BOTH
        // GetSize() AND this map need the new value.
        var canonSizePreset = Pip.SizePreset switch
        {
            { } s when string.Equals(s, "Small",  StringComparison.OrdinalIgnoreCase) => "Small",
            { } s when string.Equals(s, "Medium", StringComparison.OrdinalIgnoreCase) => "Medium",
            { } s when string.Equals(s, "Large",  StringComparison.OrdinalIgnoreCase) => "Large",
            { } s when string.Equals(s, "XL",     StringComparison.OrdinalIgnoreCase) => "XL",
            { } s when string.Equals(s, "XXL",    StringComparison.OrdinalIgnoreCase) => "XXL",
            { } s when string.Equals(s, "XXXL",   StringComparison.OrdinalIgnoreCase) => "XXXL",
            { } s when string.Equals(s, "Custom", StringComparison.OrdinalIgnoreCase) => "Custom",
            _ => "Large",
        };
        if (!string.Equals(Pip.SizePreset, canonSizePreset, StringComparison.Ordinal))
        {
            Pip.SizePreset = canonSizePreset;
            // Set mutated=true so the canonical-case value gets persisted to
            // disk. Without this, next load re-reads the non-canonical form
            // (e.g. "xl") and re-normalizes in memory — but disk stays bad,
            // so the cycle repeats. One-time backup-and-save cost on the
            // first load after a hand-edit is acceptable; the persistent
            // disk value is the correctness primary.
            mutated = true;
        }
        if (Pip.Orientation is not ("Horizontal" or "Vertical")) Pip.Orientation = "Vertical";
        if (Affinity.ActivePriority is not ("Normal" or "AboveNormal" or "High" or "BelowNormal"))
            Affinity.ActivePriority = "AboveNormal";
        if (Affinity.BackgroundPriority is not ("Normal" or "AboveNormal" or "High" or "BelowNormal"))
            Affinity.BackgroundPriority = "AboveNormal";
        if (Hotkeys.SwitchKeyMode is not ("swapLast" or "cycleAll"))
            Hotkeys.SwitchKeyMode = "swapLast";

        // v3.22.53 post-round-3 fix (T2 Sonnet IMPORTANT): TrayClick string-
        // enum allowlist parity with the other 6 enums above. Hand-edited
        // garbage values fell through to ExecuteTrayAction's string switch
        // which silently no-ops on unknown values — no user-visible signal
        // that the binding is broken. Falling back to the class-initializer
        // defaults keeps the tray functional. The valid-value set is
        // duplicated from the doc comment at TrayClickConfig.SingleClick;
        // bump both together if a new action lands.
        // v3.22.53 post-round-5 fix (T2 Opus IMPORTANT): added LoginAll5 +
        // LoginAll6. ExecuteTrayAction in TrayManager handles those cases
        // and the AutoLoginTeams dialog binds them, so they're real,
        // user-reachable actions. The TrayClickConfig.SingleClick XML-doc
        // comment said "Teams 5/6 are tray-right-click-only by design — not
        // exposed as click bindings" — that intent is now obsolete (the
        // dialog exposes them), and with the round-4 mutated=true behavior
        // a hand-edited config assigning Team 5/6 to a click slot would
        // silently reset to "None"/"LaunchOne" AND persist the reset on
        // next save. Allowlist must match dispatch reality.
        const string TrayClickValid = "None|AutoLogin1|AutoLogin2|AutoLogin3|AutoLogin4|LoginAll|LoginAll2|LoginAll3|LoginAll4|LoginAll5|LoginAll6|FixWindows|SwapWindows|TogglePiP|LaunchOne|LaunchAll|Settings|ShowHelp";
        static bool IsTrayAction(string? s) => s is not null && ("|" + TrayClickValid + "|").Contains("|" + s + "|", StringComparison.Ordinal);
        // v3.22.53 post-round-4 fix (T2 Opus IMPORTANT): set mutated=true
        // when the fallback fires so the normalized value persists to disk
        // on the next ConfigManager.Save instead of re-falling-back every
        // load. Same pattern as the round-2 Launch.DefaultLaunchOneAccount
        // Trim normalization.
        if (!IsTrayAction(TrayClick.SingleClick))       { TrayClick.SingleClick       = "None";      mutated = true; }
        if (!IsTrayAction(TrayClick.DoubleClick))       { TrayClick.DoubleClick       = "LaunchOne"; mutated = true; }
        if (!IsTrayAction(TrayClick.TripleClick))       { TrayClick.TripleClick       = "None";      mutated = true; }
        if (!IsTrayAction(TrayClick.MiddleClick))       { TrayClick.MiddleClick       = "TogglePiP"; mutated = true; }
        if (!IsTrayAction(TrayClick.MiddleDoubleClick)) { TrayClick.MiddleDoubleClick = "Settings";  mutated = true; }

        // Array shape validation
        if (SettingsWindowPos is { Length: not (0 or 2) }) SettingsWindowPos = Array.Empty<int>();
        if (EQClientIni.CPUAffinitySlots is not { Length: 6 }) EQClientIni.CPUAffinitySlots = new[] { 1, 2, 3, 1, 2, 3 };
        for (int i = 0; i < 6; i++)
            EQClientIni.CPUAffinitySlots[i] = Math.Clamp(EQClientIni.CPUAffinitySlots[i], 0, 31);

        // LoginAccount field validation (legacy — operates on v3 LegacyAccounts list).
        // v4 Account type has no CharacterSlot field; per-character slot moves to
        // Character.CharacterSlot, validated wherever Characters lives.
        foreach (var a in LegacyAccounts)
            a.CharacterSlot = Math.Clamp(a.CharacterSlot, 0, 10); // 0 = auto (by name)

        // v3.9.0 migration: propagate global AutoEnterWorld to per-account flags.
        // Operates on legacy data only — v4 encodes enter-world intent in the
        // Character vs Account type discriminator, not a per-row flag.
        if (AutoEnterWorld && LegacyAccounts.Count > 0 && LegacyAccounts.All(a => !a.AutoEnterWorld))
        {
            foreach (var a in LegacyAccounts)
                a.AutoEnterWorld = true;
            AutoEnterWorld = false; // consumed — prevent re-firing every session
            mutated = true;
        }
        return mutated;
    }

    /// <summary>
    /// Look up an Account by its user-facing Name. Case-insensitive (v3.15.2 — matches
    /// the OrdinalIgnoreCase uniqueness check in Validate()). Returns null if name is
    /// empty or no match found. Used by tray dispatch and Phase 5 hotkey registration.
    /// </summary>
    public Account? FindAccountByName(string name) =>
        string.IsNullOrEmpty(name) ? null : Accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Look up a Character by its in-game Name. Case-insensitive (v3.15.2). Returns
    /// null if name is empty or no match found.
    /// </summary>
    public Character? FindCharacterByName(string name) =>
        string.IsNullOrEmpty(name) ? null : Characters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// User-facing window-style selector (v3.22.80). Fullscreen = borderless
/// WS_POPUP covering the monitor (the look shipped since v3.22.76). Windowed =
/// WS_CAPTION slim titlebar (re-introduced in Phase 2). Both are slim-managed
/// styles, so <see cref="WindowLayout.SlimTitlebar"/> stays true for either.
/// </summary>
public enum WindowMode
{
    Fullscreen = 0,
    Windowed = 1,
}

public class WindowLayout
{
    public bool SnapToMonitor { get; set; } = true;
    public int TargetMonitor { get; set; } = 0; // 0 = primary

    /// <summary>
    /// Secondary monitor index for multimonitor mode. Background client goes here.
    /// </summary>
    public int SecondaryMonitor { get; set; } = -1; // -1 = auto (first non-primary)

    /// <summary>
    /// Pixel offset added to Y position when arranging windows.
    /// Equivalent to AHK's FIX_TOP_OFFSET — adjusts for taskbar/title bars/bezels.
    /// </summary>
    public int TopOffset { get; set; } = 0;

    /// <summary>
    /// v3.22.54 — horizontal pixel nudge added to the slim-titlebar outer.X.
    /// On some Win11 multi-monitor setups the DPI rounding leaves a 1 px
    /// desktop sliver on one edge of the client area while the other edge
    /// sits flush against the monitor border. Setting this to +1 shifts the
    /// whole window one pixel right (gap moves from right edge to left edge);
    /// -1 shifts it left. Clamped to [-10, 10] in Validate. Default 0.
    /// Only applies in slim-titlebar mode.
    /// </summary>
    public int HorizontalNudgePx { get; set; } = 0;

    /// <summary>
    /// Current layout mode: "single" (all on one monitor) or "multimonitor" (one per monitor).
    /// </summary>
    public string Mode { get; set; } = "single";

    /// <summary>
    /// Internal "EQSwitch styles this window" signal read by WindowManager and
    /// EnforceOverrides. TRUE for both Fullscreen and Windowed window modes;
    /// the concrete style (WS_POPUP vs WS_CAPTION) is selected by
    /// <see cref="WindowMode"/>. Kept in sync by AppConfig.Validate. Legacy
    /// non-slim (false) is no longer reachable from the main card as of
    /// v3.22.80 — Validate migrates it to the WindowMode look.
    /// </summary>
    public bool SlimTitlebar { get; set; } = true;

    /// <summary>
    /// v3.22.80: the user-facing window-mode selector that drives the Window
    /// Style card. Default Fullscreen preserves the v3.22.76+ borderless look.
    /// </summary>
    public WindowMode WindowMode { get; set; } = WindowMode.Fullscreen;

    /// <summary>
    /// v3.22.19 (2026-05-18): per-monitor slim override for multi-monitor mode.
    /// When <c>Mode == "multimonitor"</c> the SECONDARY monitor's client uses
    /// this flag instead of <see cref="SlimTitlebar"/>. Single-screen mode is
    /// unaffected — only the primary flag applies there.
    ///
    /// **DEFAULT CHANGED 2026-05-19** to <c>true</c> per Nate's directive:
    /// "main monitor always needs to be our same window frame as we used
    /// yesterday for team" + "if trying to match other monitor DPI is bugging
    /// us then just goal on making the multimonitor constant and working
    /// flawless and dont worry about extending the 2nd monitor to cover it".
    /// With default <c>true</c>, BOTH monitors use the same slim treatment so
    /// the primary's frame is identical to single-screen mode AND identical
    /// to the secondary's frame — visually consistent. This matches v3.22.18
    /// behavior (when multi-monitor was first attempted in the C# port).
    ///
    /// Set <c>false</c> to opt into the "secondary keeps normal frame + work-
    /// area sized" experimental shape from earlier v3.22.19 (still has known
    /// rough edges around cross-DPI positioning).
    /// </summary>
    public bool SlimTitlebarSecondary { get; set; } = true;

    /// <summary>
    /// How many pixels of the titlebar to LEAVE VISIBLE inside the monitor.
    /// A standard Windows titlebar is ~31px on Win11. Default 18 reveals the
    /// title text and a sliver of the minimize/X buttons (WinEQ2-style look).
    /// Clamped to [0, 40] in Validate. Set to 0 to hide the caption completely
    /// (max game area, no drag target); raise to ~22–26 to expose more of the
    /// buttons. Bumped from 13 in v3.22.53 — at 13 the title text and buttons
    /// were both invisible.
    /// </summary>
    public int TitlebarOffset { get; set; } = 18;

    /// <summary>
    /// v3.22.53 — when true, request the dark-mode immersive titlebar from DWM
    /// (DWMWA_USE_IMMERSIVE_DARK_MODE, attribute 20). Cross-process: the call
    /// targets eqgame.exe's HWND but DWM is system-wide so the attribute is
    /// honored regardless of which process makes the call. Useful when slim
    /// titlebar exposes the caption inside the monitor — the dark caption sits
    /// less obtrusively over the dark fantasy chrome of EQ than the default
    /// white Windows caption.
    /// <para>
    /// v3.22.56 — default flipped from <c>false</c> to <c>true</c>. Nate's
    /// 2026-05-26 visual review after the v3.22.54 promotion to the Video
    /// tab: "make dark titlebar enabled by default, it looks good."
    /// Upgrade behavior: users whose config JSON has an explicit
    /// <c>darkTitlebar</c> key (anyone who opened Settings → Apply on
    /// v3.22.53 or later) keep their saved value via STJ deserialization.
    /// Users upgrading from v3.22.52 or earlier — OR v3.22.53+ users who
    /// never opened Settings — have no <c>darkTitlebar</c> key on disk;
    /// STJ fills the missing property with the new C# default
    /// (<c>true</c>), so they silently adopt the dark caption on next
    /// launch. This is the intended behavior (the directive was "default
    /// ON"), but documented here so the migration story is loud rather
    /// than implicit. No <c>MigrateV5ToV6</c> step is needed because the
    /// field's absence is semantically equivalent to "accept whatever the
    /// current default is."
    /// </para>
    /// </summary>
    public bool DarkTitlebar { get; set; } = true;

    /// <summary>
    /// How many pixels to subtract from the bottom of the game window height.
    /// Creates a gap at the bottom edge (useful for taskbar or chat box visibility).
    /// Only used when SlimTitlebar is enabled. Default 21.
    /// </summary>
    public int BottomOffset { get; set; } = 21;

    /// <summary>
    /// Custom window title template for EQ windows. Supports placeholders:
    /// {CHAR} = character name, {SLOT} = slot number (1-based), {PID} = process ID.
    /// Empty = don't modify window titles.
    /// </summary>
    public string WindowTitleTemplate { get; set; } = "";

    /// <summary>
    /// Inject eqswitch-hook.dll into eqgame.exe to hook SetWindowPos/MoveWindow from inside
    /// the process. Eliminates window position flicker during screen transitions.
    /// Only active when SlimTitlebar is also enabled. Falls back to guard timer if injection fails.
    /// </summary>
    public bool UseHook { get; set; } = true;
}

public class AffinityConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Process priority for all EQ clients.
    /// High required to prevent virtual desktop crashes and keep autofollow working.
    /// </summary>
    public string ActivePriority { get; set; } = "AboveNormal";

    /// <summary>
    /// Process priority for background EQ clients (kept in sync with ActivePriority).
    /// </summary>
    public string BackgroundPriority { get; set; } = "AboveNormal";

    /// <summary>
    /// Number of retry attempts when applying priority to a newly launched client.
    /// </summary>
    public int LaunchRetryCount { get; set; } = 3;

    /// <summary>
    /// Delay in ms between retry attempts.
    /// </summary>
    public int LaunchRetryDelayMs { get; set; } = 2000;
}

public class HotkeyConfig
{
    /// <summary>
    /// Hotkey strings use the format: "Modifier+Key" e.g. "Alt+1", "Ctrl+F1"
    /// Single keys (no modifier) like "\" or "]" use a low-level keyboard hook instead.
    /// </summary>

    /// <summary>
    /// Cycle to next EQ client — only fires when EQ is focused (like AHK HotIfWinActive).
    /// Uses low-level keyboard hook since it's a single key with no modifier.
    /// </summary>
    public string SwitchKey { get; set; } = @"\";

    /// <summary>
    /// Global switch — if EQ is focused, cycles next. If not, brings EQ to front.
    /// Uses low-level keyboard hook since it's a single key with no modifier.
    /// </summary>
    public string GlobalSwitchKey { get; set; } = "]";

    /// <summary>
    /// Alt+1 through Alt+6 — jump directly to a client by slot number.
    /// Uses RegisterHotKey (modifier-based).
    /// </summary>
    public List<string> DirectSwitchKeys { get; set; } = new();

    /// <summary>Arrange all EQ windows in a grid layout.</summary>
    public string ArrangeWindows { get; set; } = "";

    /// <summary>Toggle single-screen / multi-monitor mode.</summary>
    public string ToggleMultiMonitor { get; set; } = "Ctrl+Alt+N";

    /// <summary>Launch one EQ client.</summary>
    public string LaunchOne { get; set; } = "";

    /// <summary>Launch all configured EQ clients.</summary>
    public string LaunchAll { get; set; } = "";

    /// <summary>Auto-login Quick Login slot 1.</summary>
    public string AutoLogin1 { get; set; } = "";

    /// <summary>Auto-login Quick Login slot 2.</summary>
    public string AutoLogin2 { get; set; } = "";

    /// <summary>Auto-login Quick Login slot 3.</summary>
    public string AutoLogin3 { get; set; } = "";

    /// <summary>Auto-login Quick Login slot 4.</summary>
    public string AutoLogin4 { get; set; } = "";

    /// <summary>Auto-login Team 1.</summary>
    public string TeamLogin1 { get; set; } = "";
    /// <summary>Auto-login Team 2.</summary>
    public string TeamLogin2 { get; set; } = "";
    /// <summary>Auto-login Team 3.</summary>
    public string TeamLogin3 { get; set; } = "";
    /// <summary>Auto-login Team 4.</summary>
    public string TeamLogin4 { get; set; } = "";

    /// <summary>
    /// Pop the tray context menu above the system clock on the primary monitor.
    /// Lets users invoke the menu without opening Start (which sits in a higher
    /// Win11 z-band and covers any tray UI underneath it).
    /// </summary>
    public string ShowMenu { get; set; } = "Ctrl+Alt+M";

    /// <summary>
    /// v4 Account family hotkeys. Each binding fires LoginToCharselect on the
    /// matching Account (resolved by TargetName == Account.Name). Populated
    /// by MigrateV3ToV4 from v3 (AutoLoginN, QuickLoginN) pairs that resolved
    /// to an account-without-character or to a character with AutoEnterWorld=false.
    /// </summary>
    public List<HotkeyBinding> AccountHotkeys { get; set; } = new();

    /// <summary>
    /// v4 Character family hotkeys. Each binding fires LoginAndEnterWorld on the
    /// matching Character (resolved by TargetName == Character.Name). Populated
    /// by MigrateV3ToV4 from v3 (AutoLoginN, QuickLoginN) pairs that resolved
    /// to a character with AutoEnterWorld=true.
    /// </summary>
    public List<HotkeyBinding> CharacterHotkeys { get; set; } = new();

    /// <summary>Toggle PiP overlay (show/hide). Blank = unbound.</summary>
    public string TogglePip { get; set; } = "";

    /// <summary>
    /// Set true once the user has enabled multimonitor mode at least once.
    /// Unlocks Alt+M hotkey permanently. Won't work until user tries
    /// multimonitor via the Settings checkbox first.
    /// </summary>
    public bool MultiMonitorEnabled { get; set; } = false;

    /// <summary>
    /// Switch key behavior: "swapLast" (Alt+Tab style, swap between last two) or "cycleAll" (round-robin).
    /// </summary>
    public string SwitchKeyMode { get; set; } = "swapLast";
}

public class LaunchConfig
{
    /// <summary>EQ executable name (usually "eqgame.exe").</summary>
    public string ExeName { get; set; } = "eqgame.exe";

    /// <summary>Command-line arguments for the EQ client (e.g. "patchme").</summary>
    public string Arguments { get; set; } = "patchme";

    /// <summary>
    /// v3.22.53 — when set to a configured account Name, the LaunchOne path
    /// (tray single-click or LaunchOne hotkey) routes through AutoLoginManager
    /// using that account's stored DPAPI credentials, bypassing the manual
    /// login + server-select screens that LaunchOne otherwise shows. Empty
    /// (default) preserves the historical behavior: plain process start, user
    /// completes login themselves.
    ///
    /// **Why an opt-in:** an empty default keeps existing users' first-click
    /// experience unchanged. Once set, LaunchOne feels like team1 — same auto-
    /// dismiss of EULA, same window-position settle, no manual typing. The
    /// account Name must match an entry in <see cref="AppConfig.Accounts"/>;
    /// if the name no longer resolves we fall back to plain launch and log a
    /// warning so the user notices.
    /// </summary>
    public string DefaultLaunchOneAccount { get; set; } = "";

    /// <summary>Number of clients to launch with "Launch All" (1-6).</summary>
    public int NumClients { get; set; } = 2;

    /// <summary>Delay in ms between launching each client.</summary>
    public int LaunchDelayMs { get; set; } = 3000;

    /// <summary>Delay in ms after all clients launched before arranging windows.</summary>
    public int FixDelayMs { get; set; } = 15000;

    // ── v3.15.2 autologin timing tunables (defaults preserve v3.15.1 behavior) ──
    // Surfaced after the v3.15.1 perf audit identified ~1.5–2s of slack across the
    // hard-coded waits in WaitForScreenTransition + BURST 2. NOT changed in v3.15.2;
    // any tuning needs feedback_dual_box_test_before_autologin_tag.md (5+ runs across
    // reboots before tagging) because too-aggressive values miss-detect on slow
    // hardware / bad network days.

    /// <summary>
    /// Pre-poll grace period (ms) at the start of WaitForScreenTransition. Lets EQ
    /// begin the transition before we start polling IsHungAppWindow / GetWindowRect.
    /// Aggressive target ~200; default 1000 preserves v3.15.1 behavior. Reducing
    /// risks polling the pre-transition steady state and missing the transition edge.
    /// </summary>
    public int WaitTransitionInitialDelayMs { get; set; } = 1000;

    /// <summary>
    /// Settle period (ms) after WaitForScreenTransition detects the responsive edge,
    /// before returning. Brief render time. Aggressive target 300–500; default 1000.
    /// </summary>
    public int WaitTransitionSettleMs { get; set; } = 1000;

    /// <summary>
    /// Poll cadence (ms) inside WaitForScreenTransition's main loop. Aggressive
    /// target 250 (doubles polling rate); default 500. Tighter cadence increases
    /// CPU on the autologin thread but can shave detection latency.
    /// </summary>
    public int WaitTransitionPollIntervalMs { get; set; } = 500;

    /// <summary>
    /// Post-keystroke dwell (ms) at end of BURST 2 server-select Enter, before
    /// deactivating focus-faking. Aggressive target 300; default 500. Below ~300
    /// risks deactivating before EQ's input pump consumes the keystroke.
    /// </summary>
    public int Burst2PostKeystrokeMs { get; set; } = 500;

    // ── v3.15.2 second-pass autologin tunables (defaults preserve current behavior) ──
    // Same caveat as the four knobs above: any default change needs the dual-box
    // gate (feedback_dual_box_test_before_autologin_tag.md) before tagging.

    /// <summary>
    /// Settle time (ms) after activating focus-faking, before BURST 1 starts
    /// typing credentials. Gives the inline-hook + WndProc subclass time to
    /// install before keystrokes flow. Default 500.
    /// </summary>
    // v3.15.12 (2026-05-10): bumped 500→2000. The DLL's activation defense
    // (WndProc subclass install + DI8 SetCooperativeLevel BACKGROUND switch
    // + WM_ACTIVATEAPP blast) happens on the rising-edge of KeyShm active
    // in device_proxy.cpp::ActivateThread (16ms tick cadence). Empirically
    // 2026-05-10: 500ms wasn't enough — second-launched client (PID 28628)
    // got ZERO GetDeviceData injection events while first client got ~10
    // events. The race: KeyShm activates → ActivateThread next tick (up to
    // 16ms) → SetCoopLevel + WM_ACTIVATEAPP blast → EQ processes blast +
    // starts polling DI8 BACKGROUND. If g_realKeyboardDevice wasn't yet
    // captured (EQ hadn't called CreateDevice yet), the coop switch is
    // SKIPPED that tick. Bumping settle to 2000ms gives EQ time to finish
    // DI8 init before keys fire.
    public int Burst1ActivationSettleMs { get; set; } = 2000;

    /// <summary>
    /// Post-submit dwell (ms) at end of BURST 1 (after pressing Enter to submit
    /// credentials), before deactivating focus-faking. Default 500 (mirrors
    /// Burst2PostKeystrokeMs). Below ~300 risks deactivating before EQ consumes
    /// the Enter keystroke.
    /// </summary>
    public int Burst1PostSubmitMs { get; set; } = 500;

    /// <summary>
    /// Settle time (ms) after activating focus-faking, before BURST 2 sends
    /// the server-select Enter. Default 300 (BURST 2 has less work than BURST 1
    /// so the settle window can be tighter without losing reliability).
    /// </summary>
    public int Burst2ActivationSettleMs { get; set; } = 300;

    /// <summary>
    /// Wait (ms) after BURST 1 submit, before BURST 2 fires for server-select
    /// Enter. Covers login-server response time. Default 3000. Aggressive
    /// target ~1500 if server is local + fast. Below ~1000 risks BURST 2
    /// firing before login response arrives.
    /// </summary>
    public int PostBurst1WaitMs { get; set; } = 3000;

    /// <summary>
    /// Diff 4 (v3.18+, 2026-05-15): server ID for the JoinServerDirect
    /// in-process __thiscall on eqmain's LoginServerAPI vtable. When > 0 AND
    /// the native DLL successfully dispatches the call, BURST 2 (server-select
    /// Enter PostMessage) is SKIPPED — the structural call advances eqmain
    /// from server-select to char-select directly.
    ///
    /// Default 1 — empirical Dalaya server ID (single-server private emu;
    /// MQ2 RoF2-emu Companies.h has no Dalaya entry, so the ID was inferred
    /// from "first non-locked entry in ServerList" semantics). If wrong on
    /// a future Dalaya patch or a different emu, JoinServerDirect returns
    /// false (one of: API null, vtable mismatch, prologue patch, SEH inside
    /// the call), and C# falls back to BURST 2 — preserving v3.x behavior.
    ///
    /// Set to 0 to DISABLE the JoinServer wire entirely and force BURST 2
    /// for every login (parity with v3.17.x and earlier). Useful for
    /// bisecting if JoinServer dispatch causes problems on a future build.
    /// </summary>
    public int JoinServerId { get; set; } = 1;

    /// <summary>
    /// Wait (ms) after a 90s WaitForScreenTransition timeout before retrying
    /// the login (stale-session recovery). Dalaya releases stale sessions in
    /// ~30-45s — this default is calibrated against that window. DO NOT
    /// reduce below 30000 without a Dalaya-server confirmation that the
    /// release window has shortened. Default 30000.
    /// </summary>
    public int StaleSessionWaitMs { get; set; } = 30000;

    /// <summary>
    /// Poll interval (ms) inside the stale-session recovery wait. The wait
    /// is now cancellable — if the EQ process dies mid-sleep (observed
    /// 2026-05-10: gotquiz EQ exited during the 30s sleep after a blind
    /// modal-dismiss Enter killed the wrong screen), the recovery short-
    /// circuits instead of slogging through the full StaleSessionWaitMs.
    /// Default 500. Floor 100, ceiling 5000.
    /// </summary>
    public int StaleSessionPollIntervalMs { get; set; } = 500;

    /// <summary>
    /// Maximum number of login-retry attempts after WaitForScreenTransition
    /// times out. Mirrors MQ2AutoLogin's ConnectRetries semantics. Default 1
    /// (one retry, then surface failure — matches v3.15.x behavior). Set to
    /// 0 to disable retry entirely (90s timeout = hard fail). Floor 0,
    /// ceiling 5 (each retry adds ~30s recovery wait + ~60s screen wait,
    /// so 5 retries can push wall-clock past 8 minutes).
    /// </summary>
    public int ConnectRetryCount { get; set; } = 1;

    /// <summary>
    /// After BURST 2 fires, poll for evidence EQ has advanced past the login
    /// screen (gameState change or window-size change) within this window.
    /// If no advance is detected, the 90s screen-transition wait is short-
    /// circuited to 5s and the retry loop kicks in much sooner. Addresses
    /// the "BURST 1 typed only 4 chars and we wait 90s before retrying"
    /// failure class (truncation, user-input collision, simply wrong creds).
    /// Default 60000 (60s; v3.20.7 bump from 10000 — Dalaya char-select load
    /// takes 15-25s after QUICK CONNECT click, so the prior 10s budget
    /// timed out before the char-select SHM signal fired).
    /// Set to 0 to disable (legacy 90s-only behavior).
    /// Floor 0, ceiling 90000 (v3.20.7 raise from 30000).
    /// </summary>
    public int PostBurst2QuickFailCheckMs { get; set; } = 60000;

    /// <summary>
    /// Vestigial settle pause between WaitForScreenTransition reporting
    /// charselect-ready and the first MQ2 bridge poll for the char list.
    /// By this point the EQ process has been running 30–90s (login →
    /// server-select → charselect load), so the bridge has long since
    /// initialized — this is NOT a bridge-init wait. The char-list wait
    /// loop that follows (latch + ReadCharCount, 60×500ms cap) is the
    /// actual bridge-readiness gate; its first iteration polls without
    /// delay and its inter-poll 500ms sleep absorbs any genuine bridge
    /// lag, making this Sleep redundant. Default 1ms in v3.15.8 (was
    /// 2000 — pure tax in the success path, ~750ms saved per box vs
    /// the v3.15.8 conservative midpoint of 750). Floor 0, ceiling 30000.
    /// </summary>
    public int BridgeInitWaitMs { get; set; } = 1;

    // ── v3.15.11 Enter World fast-path knob ──
    // (Target 2 Option A in the v3.15.11 work brief: skip SHM EnterWorld on
    // Dalaya, fall straight to PulseKey3D. Empirical Dalaya behavior across
    // every dual-box test up to v3.15.10: all 4 SHM EnterWorld attempts
    // return -1 because CLW_EnterWorldButton isn't constructed by the time
    // charselect-ready is signaled, then PulseKey3D fallback fires and works
    // every time. Skipping the SHM path saves ~2-2.5s of failed retries per
    // box. The structural fix — bridge writes a buttonReady flag in SHM and
    // C# polls it — is filed as a follow-up; this knob is the empirical
    // Dalaya-specific shortcut.)

    /// <summary>
    /// Skip SHM-based Enter World (in-process CLW_EnterWorldButton click) on
    /// Dalaya and use the PulseKey3D keyboard fallback directly. Empirically,
    /// the button isn't constructed by the time charselect-ready is signaled
    /// on Dalaya — every SHM attempt returns -1 (button not found) and the
    /// PulseKey3D fallback takes over after ~2-2.5s of failed retries. This
    /// flag eliminates that wasted retry budget. Default true. Other servers
    /// (account.Server != "Dalaya", case-insensitive) ignore this flag and
    /// keep the SHM-primary path. Not exposed in Settings UI — power-user
    /// opt-out via direct edit of eqswitch-config.json only.
    /// </summary>
    public bool SkipShmEnterWorldOnDalaya { get; set; } = true;

    /// <summary>
    /// Skip the SHM warmup ritual (loginShm.SendLoginCommand) before BURST 1.
    /// Empirically (2026-05-10): the DLL's LoginStateMachine processes the
    /// LOGIN command by heap-walking for LOGIN_ConnectButton via
    /// HeapScanForWidget + LIVE-WIDGET HEAP ENUM (259 pages) + HEAP CROSS-REF
    /// + TranslateDefToLive (523 nodes ×2). On Dalaya this takes 5-7s PER
    /// PHASE_CLICKING_CONNECT iteration ON THE EQ GAME THREAD via the
    /// GiveTime detour. EQ's input pump is blocked while scanning →
    /// PostMessage'd BURST 1 keystrokes pile up and get coalesced/dropped
    /// (verified: 4 of 7 password chars landing). The pre-BURST-1 CANCEL
    /// only stops the NEXT iteration; an in-flight scan still blocks the
    /// pump for seconds after BURST 1 activates.
    ///
    /// When this flag is set, C# skips Phase 1 entirely — no SendLoginCommand,
    /// no warmup wait, no CANCEL race — and goes straight from "DLL gameState
    /// ready" → loginScreenDelayMs grace → BURST 1. The DLL never starts the
    /// heap-walk widget discovery, the game thread stays free, and all BURST 1
    /// keystrokes land cleanly. Confirmed working with the v3.4.x baseline DLL
    /// which lacks the structural-login widget discovery entirely.
    ///
    /// Default true. The "warm-up the input pump" rationale that justified the
    /// ritual was true with lightweight discovery (v3.4.x); the heavier widget
    /// code added since 04/24 (eqmain_widgets + eqmain_widgets_mq2style,
    /// +900 lines) makes it net-negative on Dalaya. Not exposed in Settings
    /// UI — power-user opt-out via direct eqswitch-config.json edit.
    /// </summary>
    public bool SkipNativeWarmup { get; set; } = true;

    /// <summary>
    /// Route BeginLogin through the tick-driven state machine
    /// (<c>AutoLoginManager.RunLoginStateMachine</c>) instead of the linear
    /// <c>RunLoginSequence</c>. The state machine reads the v3.21.0 widget probes +
    /// Native phase + gameState every 250ms and dispatches via state transitions
    /// rather than time-budgeted sleeps. Native owns the password write
    /// (<c>EQMainCXStr::WriteEditTextDirect</c> CXStr paste at
    /// <c>CEditBaseWnd::InputText +0x1A8</c>) — atomic, MQ2-style, no per-char
    /// WM_CHAR loop, no OS-side keystroke throttling surface.
    ///
    /// <para>v3.22.71 — AppConfig default flipped to <c>true</c>. Prior default
    /// (<c>false</c>) was a v3.22.0-era safety baseline carried forward by
    /// inertia; the bad-combo with <c>SkipNativeWarmup=true</c> (the de-facto
    /// production setting) makes the legacy <c>RunLoginSequence</c> path
    /// nonviable on Dalaya because the C# per-char WM_CHAR pump is subject to
    /// game-thread contention from Native polling and to OS-side WM_CHAR
    /// throttling that the SM path's atomic CXStr paste bypasses entirely.
    /// Drove the 2026-05-28 misdiagnosis: a silent autonomous flip of this
    /// flag to <c>false</c> on 5/27 16:27 reproduced the v3.22.10-era "5/7
    /// password chars landing" symptom on every smoke regardless of which
    /// EQSwitch binary was installed, presenting as an OS env-shift across
    /// 4 release versions. With the default flipped, fresh downloads and
    /// fresh configs both start in the working state.</para>
    ///
    /// <para>The legacy <c>RunLoginSequence</c> remains as a runtime escape
    /// hatch for environments where the SM path's SHM/widget dependencies
    /// aren't satisfied (e.g., non-Dalaya emu servers where the
    /// <c>CEditBaseWnd::InputText</c> offset hasn't been validated). Not
    /// exposed in the Settings UI: it's a power-user diagnostic flag, not a
    /// feature.</para>
    /// </summary>
    public bool UseStateMachine { get; set; } = true;
}

public class PipConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Size preset: "Small", "Medium", "Large", "XL", "XXL", "XXXL", "Custom"</summary>
    public string SizePreset { get; set; } = "Large";

    /// <summary>Custom width (used when SizePreset = "Custom").</summary>
    public int CustomWidth { get; set; } = 512;

    /// <summary>Custom height (used when SizePreset = "Custom").</summary>
    public int CustomHeight { get; set; } = 288;

    /// <summary>Stacking orientation: "Vertical" (top-to-bottom) or "Horizontal" (left-to-right).</summary>
    public string Orientation { get; set; } = "Vertical";

    /// <summary>Opacity (0-255). 255 = fully opaque.</summary>
    public byte Opacity { get; set; } = 220;

    /// <summary>Show colored border around PiP windows.</summary>
    public bool ShowBorder { get; set; } = true;

    /// <summary>Border color name: "Blue", "Green", "Red".</summary>
    public string BorderColor { get; set; } = "Blue";

    /// <summary>Border thickness in pixels (1-10). Default 3.</summary>
    public int BorderThickness { get; set; } = 3;

    /// <summary>Max number of PiP windows to show (1-3).</summary>
    public int MaxWindows { get; set; } = 2;

    /// <summary>Saved positions (X,Y pairs per slot).</summary>
    public List<int[]> SavedPositions { get; set; } = new();

    public (int w, int h) GetSize() => SizePreset switch
    {
        "Small" => (256, 144),
        "Medium" => (384, 216),
        "Large" => (512, 288),
        "XL" => (768, 432),
        "XXL" => (1024, 576),
        "XXXL" => (1600, 900),
        _ => (CustomWidth, CustomHeight)
    };

    public bool IsHorizontal => Orientation.Equals("Horizontal", StringComparison.OrdinalIgnoreCase);

    public Color GetBorderColor() => BorderColor switch
    {
        "Blue" => Color.FromArgb(15, 30, 80),
        "Green" => Color.FromArgb(10, 60, 25),
        "Red" => Color.FromArgb(50, 5, 5),
        _ => Color.FromArgb(15, 30, 80)
    };
}

public class TrayClickConfig
{
    /// <summary>
    /// Action for single left-click on tray icon.
    /// Values: "None", "AutoLogin1"–"AutoLogin4",
    /// "LoginAll", "LoginAll2"–"LoginAll6" (Teams 5/6 added in v3.22.53; Teams 7-12
    /// added in v3.22.58 are DELIBERATELY NOT in this allowlist — they're tray-
    /// right-click-submenu only by design, no trayclick action surface).
    /// "FixWindows", "SwapWindows", "TogglePiP", "LaunchOne", "LaunchAll", "Settings", "ShowHelp".
    /// Source of truth: <c>TrayClickValid</c> in <see cref="AppConfig.Validate"/>.
    /// </summary>
    public string SingleClick { get; set; } = "None";

    /// <summary>
    /// Action for double left-click on tray icon.
    /// </summary>
    public string DoubleClick { get; set; } = "LaunchOne";

    /// <summary>
    /// Action for triple left-click on tray icon.
    /// </summary>
    public string TripleClick { get; set; } = "None";

    /// <summary>
    /// Action for single middle-click on tray icon.
    /// </summary>
    public string MiddleClick { get; set; } = "TogglePiP";

    /// <summary>
    /// Action for double middle-click on tray icon.
    /// </summary>
    public string MiddleDoubleClick { get; set; } = "Settings";
}


public class CharacterProfile
{
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string Notes { get; set; } = "";
    public int SlotIndex { get; set; } = 0;

    /// <summary>
    /// Optional per-character priority override.
    /// Null = use global priority settings. Values: "Normal", "AboveNormal", "High".
    /// </summary>
    public string? PriorityOverride { get; set; } = null;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(Class) ? Name : $"{Name} ({Class})";
}

/// <summary>
/// Persistent eqclient.ini overrides.
/// When a setting is enabled here, EQSwitch enforces it in eqclient.ini on save.
/// </summary>
public class EQClientIniConfig
{
    /// <summary>Disable all EQ sound (Sound=FALSE in [Defaults]). EQ default: TRUE (sound disabled).</summary>
    public bool DisableSound { get; set; } = true;

    /// <summary>Disable music (Music=0 in [Defaults]). EQ default: TRUE (music off).</summary>
    public bool DisableMusic { get; set; } = true;

    /// <summary>Sound volume (SoundVolume in [Defaults]). EQ default: 0. -1 = don't override.</summary>
    public int SoundVolume { get; set; } = 0;

    /// <summary>Disable environment sounds (EnvSounds=0 in [Defaults]). EQ default: TRUE (env sounds off).</summary>
    public bool DisableEnvSounds { get; set; } = true;

    /// <summary>Disable combat music (CombatMusic=0 in [Defaults]). EQ default: TRUE (combat music off).</summary>
    public bool DisableCombatMusic { get; set; } = true;

    /// <summary>Disable Windows auto-ducking of EQ audio (AllowAutoDuck=0 in [Defaults]). EQ default: TRUE (auto-duck off).</summary>
    public bool DisableAutoDuck { get; set; } = true;

    /// <summary>Set sky update interval in ms (SkyUpdateInterval=60000 in [Defaults]). EQ default: TRUE (60000ms = slow).</summary>
    public bool SlowSkyUpdates { get; set; } = true;

    /// <summary>Disable sky rendering (Sky=0 in [Defaults]). EQ default: TRUE (sky off).</summary>
    public bool DisableSky { get; set; } = true;

    /// <summary>Enable persistent bard songs (BardSongs=1 in [Defaults]). EQ default: TRUE.</summary>
    public bool BardSongs { get; set; } = true;

    /// <summary>Enable bard songs on pets (BardSongsOnPets=1 in [Defaults]). EQ default: TRUE.</summary>
    public bool BardSongsOnPets { get; set; } = true;

    /// <summary>Shadow clip plane distance (ShadowClipPlane in [Defaults]). EQ default: 35.</summary>
    public int ShadowClipPlane { get; set; } = 35;

    /// <summary>Actor clip plane distance (ActorClipPlane in [Defaults]). EQ default: 67.</summary>
    public int ActorClipPlane { get; set; } = 67;

    /// <summary>Auto-attack when assisting (AttackOnAssist=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool AttackOnAssist { get; set; } = true;

    /// <summary>Show inspect message (ShowInspectMessage=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool ShowInspectMessage { get; set; } = true;

    /// <summary>Show grass (ShowGrass=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool ShowGrass { get; set; } = true;

    /// <summary>Show ping bar / network stats (NetStat=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool NetStat { get; set; } = true;

    /// <summary>Auto-update tracking window position (TrackAutoUpdate=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool TrackAutoUpdate { get; set; } = true;

    /// <summary>Target Group Buff (TargetGroupBuff=1 in [Defaults]). EQ default: TRUE.</summary>
    public bool TargetGroupBuff { get; set; } = true;

    /// <summary>Disable mip-mapping (MipMapping=FALSE in [Defaults]). EQ default: TRUE (mip-mapping off).</summary>
    public bool DisableMipMapping { get; set; } = true;

    /// <summary>Enable texture cache (TextureCache=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool TextureCache { get; set; } = true;

    /// <summary>Use D3D texture compression (UseD3DTextureCompression=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool UseD3DTextureCompression { get; set; } = true;

    /// <summary>Disable dynamic lights (ShowDynamicLights=FALSE in [Defaults]). EQ default: TRUE (lights off).</summary>
    public bool DisableDynamicLights { get; set; } = true;

    /// <summary>Use lit batches (UseLitBatches=TRUE in [Defaults]). EQ default: TRUE.</summary>
    public bool UseLitBatches { get; set; } = true;

    /// <summary>Disable inspect others (InspectOthers=FALSE in [Defaults]). EQ default: TRUE (inspect off).</summary>
    public bool DisableInspectOthers { get; set; } = true;

    /// <summary>Anonymous mode (Anonymous=1 in [Defaults]). EQ default: TRUE.</summary>
    public bool Anonymous { get; set; } = true;

    /// <summary>Clip plane distance (ClipPlane in [Defaults]). EQ default: 14.</summary>
    public int ClipPlane { get; set; } = 14;

    /// <summary>Mouse sensitivity (MouseSensitivity in [Defaults]). EQ default: 5. -1 = don't override.</summary>
    public int MouseSensitivity { get; set; } = 5;

    /// <summary>Disable loot all confirmation (LootAllConfirm=0 in [Defaults]). EQ default: TRUE (confirm off).</summary>
    public bool DisableLootAllConfirm { get; set; } = true;

    /// <summary>Confirm raid invites (RaidInviteConfirm=1 in [Defaults]). EQ default: TRUE.</summary>
    public bool RaidInviteConfirm { get; set; } = true;

    /// <summary>Disable AA confirmation dialog (AANoConfirm=0 in [Defaults]). EQ default: FALSE.</summary>
    [JsonPropertyName("aaNoConfirm")]
    public bool AANoConfirm { get; set; } = false;

    /// <summary>Disable chat server (ChatServerPort=0 in [Options]). EQSwitch default: TRUE (chat server disabled for multiboxing).</summary>
    public bool DisableChatServer { get; set; } = true;

    /// <summary>Force windowed mode (WindowedMode=TRUE in [VideoMode]).</summary>
    public bool ForceWindowedMode { get; set; } = true;

    /// <summary>Start EQ maximized in windowed mode (Maximized=0 in [Defaults] + [VideoMode]). EQ default: FALSE.</summary>
    public bool MaximizeWindow { get; set; } = false;

    /// <summary>Disable EQ logging (Log=FALSE in [Defaults]). EQ default: FALSE (logging enabled).</summary>
    public bool DisableEQLog { get; set; } = false;

    /// <summary>Max foreground FPS (MaxFPS in [Defaults]). Default 80.</summary>
    public int MaxFPS { get; set; } = 80;

    /// <summary>Max background FPS (MaxBGFPS in [Defaults]). Default 80.</summary>
    public int MaxBGFPS { get; set; } = 80;

    /// <summary>
    /// Luclin model overrides. Key = INI key name, Value = TRUE/FALSE.
    /// Stored in [Defaults] section of eqclient.ini.
    /// </summary>
    public Dictionary<string, bool> ModelOverrides { get; set; } = new();

    /// <summary>
    /// Chat spam filter overrides. Key = INI key name (e.g. "BadWord", "Spam"),
    /// Value = 0 or 1. Stored in [Defaults] section of eqclient.ini.
    /// </summary>
    public Dictionary<string, int> ChatSpamOverrides { get; set; } = new();

    /// <summary>
    /// Particle/opacity overrides. Key = INI key name, Value = string representation.
    /// Stored in [Defaults] section of eqclient.ini.
    /// Floats stored as "0.000000" format, ints as "1", bools as "true"/"false".
    /// </summary>
    public Dictionary<string, string> ParticleOverrides { get; set; } = new();

    /// <summary>
    /// Video mode overrides. Key = INI key name, Value = string representation.
    /// Stored in [VideoMode] section of eqclient.ini.
    /// </summary>
    public Dictionary<string, string> VideoModeOverrides { get; set; } = new();

    /// <summary>
    /// Tracks which main-form settings the user has explicitly saved.
    /// EnforceOverrides only writes keys in this set — prevents clobbering
    /// manual INI edits for settings the user never touched in EQSwitch.
    /// Empty on fresh install = nothing enforced until first Save.
    /// </summary>
    public HashSet<string> ConfiguredKeys { get; set; } = new();

    /// <summary>
    /// CPU core assignments for EQ's 6 affinity slots (CPUAffinity0-5 in eqclient.ini).
    /// Each value is a physical core number (0-based). Default: cores 1,2,3,1,2,3 (skip core 0 for OS).
    /// </summary>
    [JsonPropertyName("cpuAffinitySlots")]
    public int[] CPUAffinitySlots { get; set; } = { 1, 2, 3, 1, 2, 3 };

    /// <summary>
    /// Read the user's actual eqclient.ini and return an EQClientIniConfig seeded from it.
    /// Called on first launch so AppConfig reflects reality instead of hardcoded defaults.
    /// Does not touch ConfiguredKeys, sub-form dictionaries, or CPUAffinitySlots.
    /// Returns a default instance if the ini file doesn't exist.
    /// </summary>
    public static EQClientIniConfig SeedFromIni(string iniPath)
    {
        var cfg = new EQClientIniConfig();
        if (!File.Exists(iniPath)) return cfg;

        try
        {
            var lines = File.ReadAllLines(iniPath, Encoding.Default);
            string currentSection = "";

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("["))
                {
                    currentSection = trimmed;
                    continue;
                }

                var parts = trimmed.Split('=', 2);
                if (parts.Length != 2) continue;

                string key = parts[0].Trim();
                string val = parts[1].Trim();

                if (currentSection.Equals("[Defaults]", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "sound":
                            cfg.DisableSound = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "music":
                            cfg.DisableMusic = val == "0";
                            break;
                        case "soundvolume":
                            if (int.TryParse(val, out int svol))
                                cfg.SoundVolume = Math.Clamp(svol, -1, 100);
                            break;
                        case "envsounds":
                            cfg.DisableEnvSounds = val == "0";
                            break;
                        case "combatmusic":
                            cfg.DisableCombatMusic = val == "0";
                            break;
                        case "allowautoduck":
                            cfg.DisableAutoDuck = val == "0";
                            break;
                        case "skyupdateinterval":
                            cfg.SlowSkyUpdates = val == "60000";
                            break;
                        case "attackonassist":
                            cfg.AttackOnAssist = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "showinspectmessage":
                            cfg.ShowInspectMessage = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "showgrass":
                            cfg.ShowGrass = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "netstat":
                            cfg.NetStat = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "trackautoupdate":
                            cfg.TrackAutoUpdate = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "targetgroupbuff":
                            cfg.TargetGroupBuff = val == "1";
                            break;
                        case "mipmapping":
                            cfg.DisableMipMapping = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "texturecache":
                            cfg.TextureCache = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "used3dtexturecompression":
                            cfg.UseD3DTextureCompression = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "showdynamiclights":
                            cfg.DisableDynamicLights = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "uselitbatches":
                            cfg.UseLitBatches = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "inspectothers":
                            cfg.DisableInspectOthers = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "sky":
                            cfg.DisableSky = val == "0";
                            break;
                        case "bardsongs":
                            cfg.BardSongs = val == "1";
                            break;
                        case "bardsongsonpets":
                            cfg.BardSongsOnPets = val == "1";
                            break;
                        case "anonymous":
                            cfg.Anonymous = val == "1";
                            break;
                        case "raidinviteconfirm":
                            cfg.RaidInviteConfirm = val == "1";
                            break;
                        case "aanoconfirm":
                            cfg.AANoConfirm = val == "0";
                            break;
                        case "chatserverport":
                            cfg.DisableChatServer = val == "0";
                            break;
                        case "lootallconfirm":
                            cfg.DisableLootAllConfirm = val == "0";
                            break;
                        case "clipplane":
                            if (int.TryParse(val, out int cp))
                                cfg.ClipPlane = Math.Clamp(cp, 0, 999);
                            break;
                        case "mousesensitivity":
                            if (int.TryParse(val, out int ms))
                                cfg.MouseSensitivity = Math.Clamp(ms, -1, 100);
                            break;
                        case "shadowclipplane":
                            if (int.TryParse(val, out int scp))
                                cfg.ShadowClipPlane = Math.Clamp(scp, 0, 999);
                            break;
                        case "actorclipplane":
                            if (int.TryParse(val, out int acp))
                                cfg.ActorClipPlane = Math.Clamp(acp, 0, 999);
                            break;
                        case "maxfps":
                            if (int.TryParse(val, out int fps))
                                cfg.MaxFPS = Math.Clamp(fps, 0, 99);
                            break;
                        case "maxbgfps":
                            if (int.TryParse(val, out int bgfps))
                                cfg.MaxBGFPS = Math.Clamp(bgfps, 0, 99);
                            break;
                        case "maximized":
                            cfg.MaximizeWindow = val == "1";
                            break;
                        case "log":
                            cfg.DisableEQLog = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                    }
                }
                else if (currentSection.Equals("[Options]", StringComparison.OrdinalIgnoreCase))
                {
                    // [Options] is runtime-authoritative — overrides [Defaults] for shared keys
                    switch (key.ToLowerInvariant())
                    {
                        case "sky":
                            cfg.DisableSky = val == "0";
                            break;
                        case "bardsongs":
                            cfg.BardSongs = val == "1";
                            break;
                        case "bardsongsonpets":
                            cfg.BardSongsOnPets = val == "1";
                            break;
                        case "anonymous":
                            cfg.Anonymous = val == "1";
                            break;
                        case "clipplane":
                            if (int.TryParse(val, out int optCp))
                                cfg.ClipPlane = Math.Clamp(optCp, 0, 999);
                            break;
                        case "mousesensitivity":
                            if (int.TryParse(val, out int optMs))
                                cfg.MouseSensitivity = Math.Clamp(optMs, -1, 100);
                            break;
                        case "shadowclipplane":
                            if (int.TryParse(val, out int optScp))
                                cfg.ShadowClipPlane = Math.Clamp(optScp, 0, 999);
                            break;
                        case "actorclipplane":
                            if (int.TryParse(val, out int optAcp))
                                cfg.ActorClipPlane = Math.Clamp(optAcp, 0, 999);
                            break;
                        case "maxfps":
                            if (int.TryParse(val, out int optFps))
                                cfg.MaxFPS = Math.Clamp(optFps, 0, 99);
                            break;
                        case "maxbgfps":
                            if (int.TryParse(val, out int optBgfps))
                                cfg.MaxBGFPS = Math.Clamp(optBgfps, 0, 99);
                            break;
                        case "lootallconfirm":
                            cfg.DisableLootAllConfirm = val == "0";
                            break;
                        case "raidinviteconfirm":
                            cfg.RaidInviteConfirm = val == "1";
                            break;
                        case "aanoconfirm":
                            cfg.AANoConfirm = val == "0";
                            break;
                        case "chatserverport":
                            cfg.DisableChatServer = val == "0";
                            break;
                    }
                }
                else if (currentSection.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "windowedmode":
                            cfg.ForceWindowedMode = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "maximized":
                            cfg.MaximizeWindow = val == "1";
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"SeedFromIni: failed to read {iniPath}: {ex.Message}");
        }

        return cfg;
    }
}
