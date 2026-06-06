// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Globalization;

namespace EQSwitch.Config;

/// <summary>
/// Bucket — the write lifecycle of an eqclient.ini setting EQSwitch manages.
/// See docs/specs/2026-06-06-eqclient-settings-overhaul.md §1.
/// </summary>
public enum Bucket
{
    /// <summary>EQSwitch owns it; written at launch (needs live monitor geometry). Window-manager keys.</summary>
    Operational,
    /// <summary>User owns it; written ONLY on Save (touch-gated); never re-enforced at launch. eqgame wins after.</summary>
    UserPref,
    /// <summary>EQSwitch pushes once per install, then behaves as UserPref. Sane multibox defaults for noobs.</summary>
    HardPush,
}

/// <summary>Discriminator for how a setting's value is encoded in eqclient.ini.</summary>
public enum IniKind
{
    /// <summary>Two-state value chosen by <see cref="IniSetting.On"/> / <see cref="IniSetting.Off"/> (bool checkbox).</summary>
    Toggle,
    /// <summary>Numeric (int when Decimals==0, float otherwise). Slider / NumericUpDown.</summary>
    Number,
    /// <summary>EQ DirectInput key code (large int with modifier flags in high bits).</summary>
    KeyCode,
}

/// <summary>
/// ONE eqclient.ini setting EQSwitch manages — the single source of truth for its key name,
/// canonical section, polarity, ship default, and bucket. The display read, the Save write,
/// the launch enforce, and the config seed all derive from this — replacing the four hand-synced
/// parsers (LoadFromIni / SeedFromIni / ApplyToIni / EnforceOverrides) that used to drift apart.
///
/// Polarity lives in exactly one place (<see cref="On"/> / <see cref="Off"/>), so a setting can
/// never be read one way and written another — the AANoConfirm-style inversion class is
/// structurally impossible. See docs/specs/2026-06-06-eqclient-settings-overhaul.md §3.
/// </summary>
public sealed class IniSetting
{
    public string Key { get; }
    /// <summary>Canonical section — the ONE place this key is read from and written to.</summary>
    public string Section { get; }
    /// <summary>Extra sections to ALSO write (write-only mirrors, e.g. MaxFPS → [Defaults]). Never read.</summary>
    public IReadOnlyList<string> MirrorSections { get; }
    public Bucket Bucket { get; }
    public IniKind Kind { get; }
    /// <summary>Display label for the UI (single source — sub-form arrays migrate to this).</summary>
    public string Label { get; }
    /// <summary>Ship-baseline value, exactly as written to eqclient.ini (the eqclient_master.ini intent).</summary>
    public string Default { get; }
    /// <summary>Human note: what the key does in EQ + any flags.</summary>
    public string EqMeaning { get; }

    // ── Toggle polarity ──
    /// <summary>Value written when the control is "on" (checked).</summary>
    public string On { get; }
    /// <summary>Value written when the control is "off" (unchecked).</summary>
    public string Off { get; }

    // ── Numeric meta ──
    public decimal Min { get; }
    public decimal Max { get; }
    /// <summary>0 = integer; &gt;0 = float with this many places (matches EQ's "1.000000" format).</summary>
    public int Decimals { get; }
    /// <summary>Sentinel: don't write if the value is below this (e.g. SoundVolume -1 = "don't set").</summary>
    public decimal? SkipBelow { get; }

    private IniSetting(string key, string section, Bucket bucket, IniKind kind, string label,
        string def, string eqMeaning, string on, string off,
        decimal min, decimal max, int decimals, decimal? skipBelow, IReadOnlyList<string>? mirrors)
    {
        Key = key; Section = section; Bucket = bucket; Kind = kind; Label = label;
        Default = def; EqMeaning = eqMeaning; On = on; Off = off;
        Min = min; Max = max; Decimals = decimals; SkipBelow = skipBelow;
        MirrorSections = mirrors ?? Array.Empty<string>();
    }

    public static IniSetting Toggle(string key, string section, Bucket bucket,
        string on, string off, string def, string label, string meaning = "",
        IReadOnlyList<string>? mirrors = null)
        => new(key, section, bucket, IniKind.Toggle, label, def, meaning, on, off, 0m, 0m, 0, null, mirrors);

    public static IniSetting Number(string key, string section, Bucket bucket,
        decimal min, decimal max, int decimals, string def, string label,
        decimal? skipBelow = null, string meaning = "", IReadOnlyList<string>? mirrors = null)
        => new(key, section, bucket, IniKind.Number, label, def, meaning, "", "", min, max, decimals, skipBelow, mirrors);

    public static IniSetting KeyCode(string key, string def, string label, string meaning = "")
        => new(key, "KeyMaps", Bucket.UserPref, IniKind.KeyCode, label, def, meaning, "", "", 0m, 2_000_000_000m, 0, null, null);

    // ── Pure conversions (no WinForms dependency — unit-testable headless) ──

    /// <summary>INI string → control "on" state. checked ⟺ value matches <see cref="On"/>.</summary>
    public bool ToggleFromIni(string? iniValue)
        => string.Equals((iniValue ?? string.Empty).Trim(), On, StringComparison.OrdinalIgnoreCase);

    /// <summary>Control "on" state → INI string.</summary>
    public string ToggleToIni(bool on) => on ? On : Off;

    /// <summary>INI string → clamped numeric value (falls back to the parsed Default on garbage).</summary>
    public decimal ParseNumber(string? iniValue)
        => decimal.TryParse((iniValue ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? Math.Clamp(d, Min, Max)
            : DefaultNumber;

    /// <summary>The Default parsed as a number (Min if Default is non-numeric — only happens for Toggle/KeyCode misuse).</summary>
    public decimal DefaultNumber
        => decimal.TryParse(Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : Min;

    /// <summary>Numeric value → INI string, using EQ's format (F-Decimals for floats, integer otherwise).</summary>
    public string NumberToIni(decimal v)
        => Decimals > 0
            ? v.ToString("F" + Decimals, CultureInfo.InvariantCulture)
            : ((long)v).ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether a numeric value should be written (false = sentinel "don't set", e.g. -1 / 0).</summary>
    public bool ShouldWriteNumber(decimal v) => SkipBelow is null || v >= SkipBelow.Value;
}

/// <summary>
/// The complete declarative table of every eqclient.ini setting the 6 EQ Client Settings windows
/// manage — 125 rows. This is point (E) "every value accounted for" made executable, and the
/// foundation the read/write/enforce engine consumes (Phases 1-7). Values verified 2026-06-06
/// against the live eqclient.ini and each form's code. Section/polarity transcribed from each
/// form's ApplyToIni. Defaults = eqclient_master.ini baseline.
/// </summary>
public static class EqClientIniSchema
{
    /// <summary>
    /// The ONE FPS ceiling for MaxFPS/MaxBGFPS — referenced by the schema rows below, both UI NUDs
    /// (EQ Client Settings + Process Manager), AppConfig.Validate, and SeedFromIni. Fresh EQ ships
    /// MaxFPS=100, so the historical 99 cap mis-clamped the stock default; v3.24.48 raised it in only
    /// 2 of 6 places, so a round-trip still snapped 100→99. One definition kills that §8-F drift class.
    /// EQ tolerates far higher; 999 is a sane UI ceiling.
    /// </summary>
    public const int MaxFpsCap = 999;

    /// <summary>Sentinel used for the dual-section MaxFPS/MaxBGFPS (canonical [Options], mirror [Defaults]).</summary>
    private static readonly string[] MirrorDefaults = { "Defaults" };

    public static readonly IReadOnlyList<IniSetting> All = new IniSetting[]
    {
        // ───────────────────────── MAIN FORM — Gameplay (9) ─────────────────────────
        IniSetting.Toggle("Anonymous", "Options", Bucket.UserPref, on: "1", off: "0", def: "1", "Anonymous", "/anon flag set at login"),
        IniSetting.Toggle("RaidInviteConfirm", "Options", Bucket.UserPref, on: "1", off: "0", def: "1", "Raid Invite Confirm", "prompt before joining a raid"),
        IniSetting.Toggle("ChatServerPort", "Options", Bucket.HardPush, on: "0", off: "7003", def: "0", "Disable Chat Server", "0 disables the chat server (lag/multibox). HARD-PUSH target."),
        IniSetting.Toggle("Log", "Defaults", Bucket.UserPref, on: "FALSE", off: "TRUE", def: "FALSE", "Disable EQ Logging", "Log=FALSE disables /log file output"),
        IniSetting.Toggle("AANoConfirm", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "AA No Confirm", "⚠ POLARITY FIX vs legacy (was checked⟺0). EQ: 1 = skip AA purchase confirm. VERIFY in Phase 1 before go-live."),
        IniSetting.Toggle("ShowInspectMessage", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "TRUE", "Show Inspect Message", "announce when inspected"),
        IniSetting.Toggle("AttackOnAssist", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "TRUE", "Attack on Assist", "auto-attack when /assist"),
        IniSetting.Toggle("LootAllConfirm", "Options", Bucket.UserPref, on: "0", off: "1", def: "0", "Disable Loot All Confirm", "0 = no confirm on Loot All"),
        IniSetting.Toggle("InspectOthers", "Defaults", Bucket.UserPref, on: "FALSE", off: "TRUE", def: "FALSE", "Disable Inspect Others", "InspectOthers=FALSE blocks others inspecting you"),

        // ───────────────────────── MAIN FORM — Sound (6) ─────────────────────────
        IniSetting.Toggle("Sound", "Defaults", Bucket.UserPref, on: "FALSE", off: "TRUE", def: "FALSE", "Disable Sound", "Sound=FALSE = all sound off"),
        IniSetting.Toggle("Music", "Defaults", Bucket.UserPref, on: "0", off: "1", def: "0", "Disable Music", "Music=0 = music off"),
        IniSetting.Number("SoundVolume", "Defaults", Bucket.UserPref, min: -1m, max: 100m, decimals: 0, def: "0", "Volume", skipBelow: 0m, meaning: "-1 = don't set"),
        IniSetting.Toggle("EnvSounds", "Defaults", Bucket.UserPref, on: "0", off: "1", def: "0", "Disable Env Sounds", "EnvSounds=0 = environment sounds off"),
        IniSetting.Toggle("CombatMusic", "Defaults", Bucket.UserPref, on: "0", off: "1", def: "0", "Disable Combat Music", "CombatMusic=0 = combat music off"),
        IniSetting.Toggle("AllowAutoDuck", "Defaults", Bucket.UserPref, on: "0", off: "1", def: "0", "Disable Auto-Duck", "AllowAutoDuck=0 = Windows won't duck EQ audio"),

        // ───────────────────────── MAIN FORM — Graphics (13) ─────────────────────────
        IniSetting.Toggle("SkyUpdateInterval", "Defaults", Bucket.UserPref, on: "60000", off: "3000", def: "60000", "Slow Sky Updates", "60000ms = slow sky (perf); 3000 = EQ default"),
        IniSetting.Toggle("Sky", "Options", Bucket.UserPref, on: "0", off: "1", def: "0", "Disable Sky", "Sky=0 = no sky render"),
        IniSetting.Toggle("ShowGrass", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "TRUE", "Show Grass", ""),
        IniSetting.Toggle("BardSongs", "Options", Bucket.UserPref, on: "1", off: "0", def: "1", "Persistent Bard Songs", "keep bard song lines visible"),
        IniSetting.Toggle("BardSongsOnPets", "Options", Bucket.UserPref, on: "1", off: "0", def: "1", "Bard Songs on Pets", ""),
        IniSetting.Toggle("TargetGroupBuff", "Defaults", Bucket.UserPref, on: "1", off: "0", def: "1", "Target Group Buff", ""),
        IniSetting.Toggle("MipMapping", "Defaults", Bucket.UserPref, on: "FALSE", off: "TRUE", def: "FALSE", "Disable Mip-Mapping", "MipMapping=FALSE = off"),
        IniSetting.Toggle("TextureCache", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "TRUE", "Texture Cache", ""),
        IniSetting.Toggle("UseD3DTextureCompression", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "TRUE", "D3D Texture Compression", ""),
        IniSetting.Toggle("ShowDynamicLights", "Defaults", Bucket.UserPref, on: "FALSE", off: "TRUE", def: "FALSE", "Disable Dynamic Lights", "ShowDynamicLights=FALSE = off"),
        IniSetting.Toggle("UseLitBatches", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "TRUE", "Use Lit Batches", ""),
        IniSetting.Toggle("NetStat", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "TRUE", "Ping Bar", "NetStat = network/ping bar"),
        IniSetting.Toggle("TrackAutoUpdate", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "TRUE", "Track Auto-Update", ""),

        // ───────────────────────── MAIN FORM — Performance (6) ─────────────────────────
        // MaxFPS/MaxBGFPS: canonical [Options] (EQ runtime), mirror [Defaults] (legacy). Also edited in ProcessManager (§8 F).
        IniSetting.Number("MaxFPS", "Options", Bucket.UserPref, min: 0m, max: MaxFpsCap, decimals: 0, def: "80", "MaxFPS", skipBelow: 1m, meaning: "foreground FPS cap; 0 = don't set. Cap = MaxFpsCap — fresh EQ ships MaxFPS=100, so a 99 cap mis-displayed the ship default.", mirrors: MirrorDefaults),
        IniSetting.Number("MaxBGFPS", "Options", Bucket.UserPref, min: 0m, max: MaxFpsCap, decimals: 0, def: "80", "MaxBGFPS", skipBelow: 1m, meaning: "background FPS cap; 0 = don't set. Cap = MaxFpsCap (was 99).", mirrors: MirrorDefaults),
        IniSetting.Number("MouseSensitivity", "Options", Bucket.UserPref, min: -1m, max: 100m, decimals: 0, def: "5", "Mouse", skipBelow: 0m, meaning: "-1 = don't set"),
        IniSetting.Number("ClipPlane", "Options", Bucket.UserPref, min: 0m, max: 999m, decimals: 0, def: "14", "Clip", skipBelow: 1m, meaning: "view distance; EQ default 14"),
        IniSetting.Number("ShadowClipPlane", "Options", Bucket.UserPref, min: 0m, max: 999m, decimals: 0, def: "35", "Shadow", skipBelow: 1m, meaning: "EQ default 35"),
        IniSetting.Number("ActorClipPlane", "Options", Bucket.UserPref, min: 0m, max: 999m, decimals: 0, def: "67", "Actor", skipBelow: 1m, meaning: "EQ default 67"),

        // ───────────────────────── MODELS (32) — all [Defaults], TRUE/FALSE, default FALSE ─────────────────────────
        // Global toggles
        IniSetting.Toggle("LoadSocialAnimations", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Load Social Animations"),
        IniSetting.Toggle("AllLuclinPcModelsOff", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "All Luclin PC Models Off"),
        IniSetting.Toggle("LoadVeliousArmorsWithLuclin", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Load Velious Armors with Luclin"),
        IniSetting.Toggle("UseLuclinElementals", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Use Luclin Elementals"),
        // Race × gender (checked = Luclin model, unchecked = classic)
        IniSetting.Toggle("UseLuclinHumanMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Human Male"),
        IniSetting.Toggle("UseLuclinHumanFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Human Female"),
        IniSetting.Toggle("UseLuclinBarbarianMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Barbarian Male"),
        IniSetting.Toggle("UseLuclinBarbarianFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Barbarian Female"),
        IniSetting.Toggle("UseLuclinEruditeMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Erudite Male"),
        IniSetting.Toggle("UseLuclinEruditeFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Erudite Female"),
        IniSetting.Toggle("UseLuclinWoodElfMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Wood Elf Male"),
        IniSetting.Toggle("UseLuclinWoodElfFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Wood Elf Female"),
        IniSetting.Toggle("UseLuclinHighElfMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "High Elf Male"),
        IniSetting.Toggle("UseLuclinHighElfFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "High Elf Female"),
        IniSetting.Toggle("UseLuclinDarkElfMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Dark Elf Male"),
        IniSetting.Toggle("UseLuclinDarkElfFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Dark Elf Female"),
        IniSetting.Toggle("UseLuclinHalfElfMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Half Elf Male"),
        IniSetting.Toggle("UseLuclinHalfElfFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Half Elf Female"),
        IniSetting.Toggle("UseLuclinDwarfMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Dwarf Male"),
        IniSetting.Toggle("UseLuclinDwarfFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Dwarf Female"),
        IniSetting.Toggle("UseLuclinTrollMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Troll Male"),
        IniSetting.Toggle("UseLuclinTrollFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Troll Female"),
        IniSetting.Toggle("UseLuclinOgreMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Ogre Male"),
        IniSetting.Toggle("UseLuclinOgreFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Ogre Female"),
        IniSetting.Toggle("UseLuclinHalflingMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Halfling Male"),
        IniSetting.Toggle("UseLuclinHalflingFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Halfling Female"),
        IniSetting.Toggle("UseLuclinGnomeMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Gnome Male"),
        IniSetting.Toggle("UseLuclinGnomeFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Gnome Female"),
        IniSetting.Toggle("UseLuclinIksarMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Iksar Male"),
        IniSetting.Toggle("UseLuclinIksarFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Iksar Female"),
        IniSetting.Toggle("UseLuclinVahShirMale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Vah Shir Male"),
        IniSetting.Toggle("UseLuclinVahShirFemale", "Defaults", Bucket.UserPref, on: "TRUE", off: "FALSE", def: "FALSE", "Vah Shir Female"),

        // ───────────────────────── CHAT SPAM (22) — all [Options], 1/0 ─────────────────────────
        // Combat & Melee
        IniSetting.Toggle("CriticalSpells", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Critical Spells"),
        IniSetting.Toggle("CriticalMelee", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Critical Melee"),
        IniSetting.Toggle("SpellDamage", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Spell Damage"),
        IniSetting.Toggle("DotDamage", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "DoT Damage"),
        IniSetting.Toggle("HideDamageShield", "Options", Bucket.UserPref, on: "1", off: "0", def: "1", "Hide Damage Shield"),
        IniSetting.Toggle("Strikethrough", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Strikethrough"),
        IniSetting.Toggle("Stun", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Stun Messages"),
        // Pets
        IniSetting.Toggle("PetAttacks", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Pet Attacks"),
        IniSetting.Toggle("PetMisses", "Options", Bucket.UserPref, on: "1", off: "0", def: "1", "Pet Misses"),
        IniSetting.Toggle("PetSpells", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Pet Spells"),
        IniSetting.Toggle("SwarmPetDeath", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Swarm Pet Death"),
        // Spells & Effects
        IniSetting.Toggle("PCSpells", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "PC Spells"),
        IniSetting.Toggle("NPCSpells", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "NPC Spells"),
        IniSetting.Toggle("FocusEffects", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Focus Effects"),
        IniSetting.Toggle("HealOverTimeSpells", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Heal Over Time"),
        // Social & Misc
        IniSetting.Toggle("BadWord", "Options", Bucket.UserPref, on: "1", off: "0", def: "1", "Bad Word Filter"),
        IniSetting.Toggle("Spam", "Options", Bucket.UserPref, on: "1", off: "0", def: "1", "Spam Filter"),
        IniSetting.Toggle("FellowshipChat", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Fellowship Chat"),
        IniSetting.Toggle("MercenaryMessages", "Options", Bucket.UserPref, on: "1", off: "0", def: "1", "Mercenary Messages"),
        IniSetting.Toggle("ItemSpeech", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Item Speech"),
        IniSetting.Toggle("Achievements", "Options", Bucket.UserPref, on: "1", off: "0", def: "0", "Achievements"),
        IniSetting.Toggle("PvPMessages", "Options", Bucket.UserPref, on: "1", off: "0", def: "1", "PvP Messages"),

        // ───────────────────────── PARTICLES (16) ─────────────────────────
        // Opacity (Defaults, float 0.000000-1.000000, shown 0-100%)
        IniSetting.Number("SpellParticleOpacity", "Defaults", Bucket.UserPref, min: 0m, max: 1m, decimals: 6, def: "1.000000", "Spell Particles Opacity"),
        IniSetting.Number("EnvironmentParticleOpacity", "Defaults", Bucket.UserPref, min: 0m, max: 1m, decimals: 6, def: "1.000000", "Environment Opacity"),
        IniSetting.Number("ActorParticleOpacity", "Defaults", Bucket.UserPref, min: 0m, max: 1m, decimals: 6, def: "1.000000", "Actor Opacity"),
        // Density (Defaults, float)
        IniSetting.Number("SpellParticleDensity", "Defaults", Bucket.UserPref, min: 0m, max: 1m, decimals: 6, def: "0.000000", "Spell Particles Density"),
        IniSetting.Number("EnvironmentParticleDensity", "Defaults", Bucket.UserPref, min: 0m, max: 1m, decimals: 6, def: "0.000000", "Environment Density"),
        IniSetting.Number("ActorParticleDensity", "Defaults", Bucket.UserPref, min: 0m, max: 1m, decimals: 6, def: "0.000000", "Actor Density"),
        // Near clip planes (Defaults, float)
        IniSetting.Number("SpellParticleNearClipPlane", "Defaults", Bucket.UserPref, min: 0m, max: 999m, decimals: 6, def: "2.000000", "Spell Near Clip"),
        IniSetting.Number("EnvironmentParticleNearClipPlane", "Defaults", Bucket.UserPref, min: 0m, max: 999m, decimals: 6, def: "2.000000", "Env Near Clip"),
        IniSetting.Number("ActorParticleNearClipPlane", "Defaults", Bucket.UserPref, min: 0m, max: 999m, decimals: 6, def: "2.000000", "Actor Near Clip"),
        // Cast / armor filters (Defaults, int)
        IniSetting.Number("SpellParticleCastFilter", "Defaults", Bucket.UserPref, min: 0m, max: 999m, decimals: 0, def: "1", "Spell Cast Filter"),
        IniSetting.Number("EnvironmentParticleCastFilter", "Defaults", Bucket.UserPref, min: 0m, max: 999m, decimals: 0, def: "24", "Env Cast Filter"),
        IniSetting.Number("ActorParticleCastFilter", "Defaults", Bucket.UserPref, min: 0m, max: 999m, decimals: 0, def: "1", "Actor Cast Filter"),
        IniSetting.Number("ActorNewArmorFilter", "Defaults", Bucket.UserPref, min: 0m, max: 999m, decimals: 0, def: "24", "Actor Armor Filter"),
        // Misc (Options)
        IniSetting.Number("FogScale", "Options", Bucket.UserPref, min: 0m, max: 100m, decimals: 6, def: "2.800000", "FogScale"),
        IniSetting.Number("LODBias", "Options", Bucket.UserPref, min: 0m, max: 100m, decimals: 0, def: "10", "LODBias"),
        IniSetting.Toggle("SameResolution", "Options", Bucket.UserPref, on: "1", off: "0", def: "1", "Same Resolution"),

        // ───────────────────────── VIDEO MODE (12) — all [VideoMode], int ─────────────────────────
        // Geometry dims are Operational: EnforceOverrides writes them at launch under slim-titlebar.
        // They collide with this (experimental) form's edits → §8 G: gate/hide.
        IniSetting.Number("Width", "VideoMode", Bucket.Operational, min: 640m, max: 7680m, decimals: 0, def: "1920", "Width", meaning: "⚠ slim-titlebar owns this at launch (§8 G)"),
        IniSetting.Number("Height", "VideoMode", Bucket.Operational, min: 480m, max: 4320m, decimals: 0, def: "1080", "Height", meaning: "⚠ slim-titlebar owns this at launch (§8 G)"),
        IniSetting.Number("WindowedWidth", "VideoMode", Bucket.Operational, min: 640m, max: 7680m, decimals: 0, def: "1920", "Windowed Width", meaning: "⚠ slim-titlebar owns this at launch (§8 G)"),
        IniSetting.Number("WindowedHeight", "VideoMode", Bucket.Operational, min: 480m, max: 4320m, decimals: 0, def: "1067", "Windowed Height", meaning: "⚠ slim-titlebar owns this at launch (§8 G)"),
        IniSetting.Number("WinEQWidth", "VideoMode", Bucket.UserPref, min: 640m, max: 7680m, decimals: 0, def: "1920", "WinEQ Width"),
        IniSetting.Number("WinEQHeight", "VideoMode", Bucket.UserPref, min: 480m, max: 4320m, decimals: 0, def: "1200", "WinEQ Height"),
        IniSetting.Number("WindowedModeXOffset", "VideoMode", Bucket.Operational, min: -9999m, max: 9999m, decimals: 0, def: "0", "Windowed X Offset", meaning: "⚠ window manager owns this at launch"),
        IniSetting.Number("WindowedModeYOffset", "VideoMode", Bucket.Operational, min: -9999m, max: 9999m, decimals: 0, def: "0", "Windowed Y Offset", meaning: "⚠ window manager owns this at launch"),
        IniSetting.Number("XOffset", "VideoMode", Bucket.UserPref, min: -9999m, max: 9999m, decimals: 0, def: "0", "X Offset"),
        IniSetting.Number("YOffset", "VideoMode", Bucket.UserPref, min: -9999m, max: 9999m, decimals: 0, def: "0", "Y Offset"),
        IniSetting.Number("FullscreenRefreshRate", "VideoMode", Bucket.UserPref, min: 0m, max: 360m, decimals: 0, def: "0", "Refresh Rate"),
        IniSetting.Number("FullscreenBitsPerPixel", "VideoMode", Bucket.UserPref, min: 16m, max: 32m, decimals: 0, def: "32", "Bits Per Pixel"),

        // ───────────────────────── KEYMAPS (9) — [KeyMaps], DirectInput scan codes ─────────────────────────
        IniSetting.KeyCode("KEYMAPPING_TARGETNPC_2", "209", "Target NPC (Alt)"),
        IniSetting.KeyCode("KEYMAPPING_CONSIDER_2", "83", "Consider (Alt)"),
        IniSetting.KeyCode("KEYMAPPING_CYCLENPCTARGETS_2", "79", "Cycle NPC Targets (Alt)"),
        IniSetting.KeyCode("KEYMAPPING_TOGGLETWOTARGETS_1", "82", "Toggle Two Targets"),
        IniSetting.KeyCode("KEYMAPPING_TOGGLETWOTARGETS_2", "0", "Toggle Two Targets (Alt)"),
        IniSetting.KeyCode("KEYMAPPING_AUTOPRIM_2", "211", "Auto-Primary (Alt)"),
        IniSetting.KeyCode("KEYMAPPING_POTION_SLOT_3_1", "0", "Potion Slot 3"),
        IniSetting.KeyCode("KEYMAPPING_CMD_CLIPBOARD_PASTE_1", "536870959", "Clipboard Paste"),
        IniSetting.KeyCode("KEYMAPPING_CMD_TOGGLE_AUDIO_TRIGGER_WINDOW_1", "268435486", "Audio Triggers"),
    };
}
