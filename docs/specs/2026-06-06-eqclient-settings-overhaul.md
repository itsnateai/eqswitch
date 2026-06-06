# EQ Client Settings — Overhaul Design & Implementation Plan

**Date:** 2026-06-06
**Status:** Model approved by Nate; reality mapped against live `eqclient.ini`; ready for phased build.
**Scope:** The 6-window EQ Client Settings subsystem:
`EQClientSettingsForm` (main) + 5 sub-forms: `EQModelsForm`, `EQChatSpamForm`, `EQParticlesForm`, `EQVideoModeForm`, `EQKeymapsForm`.
**Why this doc exists:** point (H) — survive interruption. This is the resumable source of truth. If the build stops, restart from §6 using the phase checkboxes.

---

## 0. Charter (Nate's requirements A–K, verbatim intent)

- **A.** Define + verify ship defaults (enabled/disabled) and how passive initial settings loading works.
- **B.** Write to the correct value location; no duplicate entries.
- **C.** Verify toggles on Save change the correct intended value.
- **D.** Play nice with `eqclient.ini` — don't clobber user settings they didn't intend to add; passively read accurate current values for display.
- **E.** EVERY value and its function across all 6 windows accounted for.
- **F.** Identify duplicate values used elsewhere; keep them in sync.
- **G.** Identify values that should not be user-facing; consider action.
- **H.** Plan saved to disk before proceeding (this file).
- **I.** Enabled toggle ⇒ enabled in INI; disabled toggle ⇒ disabled in INI — no ghost settings.
- **J.** Confirm all 6 windows at 100% and 150% DPI at the very end.
- **K.** Code to *prevent* the issue class in the first place — surgical + elegant, plan and implementation.

---

## 1. The Ownership Contract (approved model)

EQSwitch and `eqgame.exe` BOTH write `eqclient.ini`. The contract that lets them coexist:

### Three buckets (each key belongs to exactly one)

| Bucket | Who owns it | Write lifecycle | Examples |
|---|---|---|---|
| **1 — Operational** | EQSwitch (by necessity) | Written at launch (`EnforceOverrides`); needs live monitor geometry | `WindowedWidth/Height`, `WindowedMode`, `Maximized`, `WindowedModeX/YOffset`, `Width/Height` (slim), Story-Window keymap unbind, CPU affinity |
| **2 — User preference** | User; **eqgame wins after first set** | Written **only on Save, only the keys the user changed**; **never re-enforced at launch** | every Gameplay/Sound/Graphics/Performance toggle + all 5 sub-forms' settings |
| **3 — Hard-push default** | EQSwitch, once | Pushed **once per install**, then demoted to Bucket-2 behaviour | `ChatServerPort` (Disable Chat Server) — confirmed; others TBD §8 |

### Three invariants (make the contract airtight by construction — point K)

1. **Display = live read of `eqclient.ini` on every window open.** Never a cached value. → A user who changed Models in-game sees the true state when they open the Models window (even if they never opened it before). Satisfies D + the "track in-game changes" requirement.
2. **Write = surgical + touch-gated.** `SetIniValue` only rewrites the single key (already correct). A key is only written if (a) the user changed it from the loaded baseline in a window they saved, or (b) it's a one-time hard-push. Read-section == write-section for every key. → no ghosts (I), no clobber of untouched/unknown keys (D).
3. **eqgame wins.** Bucket-2 keys are written once and never re-enforced at launch, so in-game changes that eqgame persists on exit survive forever. EQSwitch sets initial intent; it does not enforce.

### Hard-push semantics (approved: "override once, then hands-off")

On first run, a Bucket-3 key is written to the master-baseline value **even if the user's INI already has a different value** — but exactly once, tracked by a per-install flag. After that it is Bucket-2 (eqgame/user can change it and it sticks). A one-time popup telling the user what changed is **deferred to the end** (Nate: "leave that for the end").

---

## 2. Ground-Truth Reality Map (verified 2026-06-06)

**Baseline artifact:** `C:\Users\nate\proggy\Everquest\Eqfresh\eqclient_master.ini` — byte-exact `cp` copy of the live INI (ANSI preserved; the Write tool would corrupt it to UTF-8). This is the "what we ship with" baseline = eqgame defaults + accumulated settings; to be curated into a clean ship-baseline during Phase 8 (strip personal state: `ScreenshotNumber`, camera, `LastCharSel`, `[News]`, personal keymaps; keep structural defaults + hard-defaults).

**Headline:** the read path WORKS. 33 of 34 main-form controls exactly match the live INI — the "everything checked" screenshot reflects a genuinely broken-in multibox config, **not ghosts.** The problems are structural, not display-accuracy.

### Per-form behaviour matrix

| Form | Display (read) | Save (write) | Launch enforce | Per-key touch? |
|---|---|---|---|---|
| **Main** (`EQClientSettingsForm`) | live, `[Defaults]`→`[Options]` | `ApplyToIni` writes **all ~34 unconditionally**; read-2/write-1 | `EnforceOverrides` writes `ConfiguredKeys` **every launch** | ❌ writes all |
| **Models** | live `[Defaults]` | per-key-touch ✅ | writes all `ModelOverrides` every launch ❌ | ✅ |
| **ChatSpam** | live `[Options]` | writes **all** checkboxes ❌ | writes all every launch ❌ | ❌ |
| **Particles** | live `[Defaults]`+`[Options]` | per-key-touch ✅ | writes all `ParticleOverrides` every launch ❌ | ✅ |
| **VideoMode** | live `[VideoMode]` | per-key-touch + slim-scrub ✅ | writes `VideoModeOverrides` (slim-aware skip) every launch ❌ | ✅ |
| **Keymaps** | live `[KeyMaps]` | per-key-touch ✅ | **none** ✅ (already eqgame-wins) | ✅ |

**Takeaway:** 4 of 6 forms already implement the target per-key-touch pattern. The work is: (1) bring main + ChatSpam up to that pattern, (2) strip Bucket-2 from launch enforcement everywhere, (3) unify all six behind one descriptor table so they can't drift again.

---

## 3. Architecture — the Setting Descriptor Table (the elegant core)

Today there are **four** hand-maintained parsers that must agree but nothing forces them to:
`EQClientSettingsForm.LoadFromIni`, `EQClientIniConfig.SeedFromIni`, `EQClientSettingsForm.ApplyToIni`, `EQClientSettingsForm.EnforceOverrides` (~700 lines). The `AANoConfirm` inversion lives in two of them because it was hand-copied. **This is the drift hazard.**

**Replace them with one declarative table** that read, write, seed, and enforce all consume:

```csharp
enum Bucket { Operational, UserPref, HardPush }

sealed record SettingDescriptor(
    string  Key,            // INI key, e.g. "Sound"
    string  Section,        // canonical section — ONE per key: Defaults|Options|VideoMode|KeyMaps
    Bucket  Bucket,
    // --- bool toggles ---
    string? OnValue  = null,    // value written when control is "on"   (e.g. Disable Sound on  => "FALSE")
    string? OffValue = null,    // value written when control is "off"  (e.g. Disable Sound off => "TRUE")
    // --- numerics ---
    bool    IsNumeric = false,
    int     Min = 0, int Max = 0,
    int?    SkipBelow = null,    // sentinel: don't write if value < this (SoundVolume -1, ClipPlane 1, ...)
    // --- meta ---
    string  EqMeaning = ""       // human note, for the inventory + tooltips
);
```

**Polarity is defined ONCE** via `OnValue`/`OffValue`. Read = `checked := (iniValue == OnValue)`; Write = `iniValue := checked ? OnValue : OffValue`. The `AANoConfirm` inversion-bug class becomes structurally impossible — you can't read it one way and write it another, because both derive from the same row.

Three generic loops replace the four parsers:
- `LoadInto(controls, ini)` — live display read.
- `SaveChanged(controls, baseline, ini)` — per-key-touch write (Bucket-2/3 only).
- `EnforceOperational(config, ini)` — launch write (Bucket-1 only) + the existing slim-titlebar geometry block (unchanged — it genuinely needs runtime geometry).

How each charter point is satisfied **by construction**:
- **I (no ghosts):** one `Section` per key ⇒ read-section == write-section ⇒ impossible to leave a stale copy in another section.
- **D (no clobber):** keys not in the table are never read/written/deleted; Bucket-2 writes are touch-gated.
- **C (correct toggle):** `OnValue`/`OffValue` single source of polarity.
- **F (sync dupes):** dual-section keys (`MaxFPS`, `MaxBGFPS`, geometry) declared explicitly as multi-section descriptors; one writer.
- **K (prevent):** four drifting parsers → one table + three loops. Less code, no drift surface.

---

## 4. Complete Value Inventory (point E — all 125 settings)

Legend: **Sec** = canonical section. **On/Off** = value when checkbox on/off. **master** = value in `eqclient_master.ini`. ⚠ = needs attention.

### 4.1 Main form — Gameplay (9)
| Control | Key | Sec | On / Off | master | Bucket | Notes |
|---|---|---|---|---|---|---|
| Anonymous | `Anonymous` | Options | 1 / 0 | 1 | UserPref | |
| Raid Invite Confirm | `RaidInviteConfirm` | Options | 1 / 0 | 1 | UserPref | |
| Disable Chat Server | `ChatServerPort` | Options | 0 / 7003 | 7003 | **HardPush** | the one-time push target |
| Disable EQ Logging | `Log` | Defaults | FALSE / TRUE | FALSE | UserPref | |
| AA No Confirm | `AANoConfirm` | Options | **1 / 0** | 0 | UserPref | ⚠ **INVERTED today** (code uses checked⟺0). Fix polarity OR relabel — verify EQ semantics (1 = skip confirm). |
| Show Inspect Message | `ShowInspectMessage` | Defaults | TRUE / FALSE | TRUE | UserPref | |
| Attack on Assist | `AttackOnAssist` | Defaults | TRUE / FALSE | TRUE | UserPref | |
| Disable Loot All Confirm | `LootAllConfirm` | Options | 0 / 1 | 0 | UserPref | |
| Disable Inspect Others | `InspectOthers` | Defaults | FALSE / TRUE | FALSE | UserPref | |

### 4.2 Main form — Sound (6)
| Control | Key | Sec | On / Off | master | Notes |
|---|---|---|---|---|---|
| Disable Sound | `Sound` | Defaults | FALSE / TRUE | FALSE | |
| Disable Music | `Music` | Defaults | 0 / 1 | 0 | |
| Volume | `SoundVolume` | Defaults | int 0–100 | 0 | `SkipBelow=0` (−1 = don't set) |
| Disable Env Sounds | `EnvSounds` | Defaults | 0 / 1 | 0 | |
| Disable Combat Music | `CombatMusic` | Defaults | 0 / 1 | 0 | |
| Disable Auto-Duck | `AllowAutoDuck` | Defaults | 0 / 1 | 0 | |

### 4.3 Main form — Graphics (13)
| Control | Key | Sec | On / Off | master | Notes |
|---|---|---|---|---|---|
| Slow Sky Updates | `SkyUpdateInterval` | Defaults | 60000 / 3000 | 60000 | "off" restores original or 3000 |
| Disable Sky | `Sky` | Options | 0 / 1 | 0 | |
| Show Grass | `ShowGrass` | Defaults | TRUE / FALSE | TRUE | |
| Persistent Bard Songs | `BardSongs` | Options | 1 / 0 | 1 | |
| Bard Songs on Pets | `BardSongsOnPets` | Options | 1 / 0 | 1 | |
| Target Group Buff | `TargetGroupBuff` | Defaults | 1 / 0 | 1 | |
| Disable Mip-Mapping | `MipMapping` | Defaults | FALSE / TRUE | FALSE | |
| Texture Cache | `TextureCache` | Defaults | TRUE / FALSE | TRUE | |
| D3D Texture Compression | `UseD3DTextureCompression` | Defaults | TRUE / FALSE | TRUE | |
| Disable Dynamic Lights | `ShowDynamicLights` | Defaults | FALSE / TRUE | FALSE | |
| Use Lit Batches | `UseLitBatches` | Defaults | TRUE / FALSE | TRUE | |
| Ping Bar | `NetStat` | Defaults | TRUE / FALSE | TRUE | |
| Track Auto-Update | `TrackAutoUpdate` | Defaults | TRUE / FALSE | TRUE | |

### 4.4 Main form — Performance (6)
| Control | Key | Sec | Type | master | Notes |
|---|---|---|---|---|---|
| MaxFPS | `MaxFPS` | **Defaults + Options** | int 0–99 (`SkipBelow=1`) | 80/80 | ⚠ F: also in ProcessManager; dual-section |
| MaxBGFPS | `MaxBGFPS` | **Defaults + Options** | int 0–99 (`SkipBelow=1`) | 80/80 | ⚠ F: also in ProcessManager; dual-section |
| Mouse | `MouseSensitivity` | Options | int 0–100 (`SkipBelow=0`, −1=skip) | 5 | |
| Clip | `ClipPlane` | Options | int 1–999 (`SkipBelow=1`) | 14 | |
| Shadow | `ShadowClipPlane` | Options | int 1–999 | 35 | |
| Actor | `ActorClipPlane` | Options | int 1–999 | 67 | |

### 4.5 Models (32) — all `[Defaults]`, `TRUE/FALSE`, all master = FALSE
- **Global (4):** `LoadSocialAnimations`, `AllLuclinPcModelsOff`, `LoadVeliousArmorsWithLuclin`, `UseLuclinElementals`
- **Race × gender (28):** `UseLuclin{Race}{Male|Female}` for Human, Barbarian, Erudite, WoodElf, HighElf, DarkElf, HalfElf, Dwarf, Troll, Ogre, Halfling, Gnome, Iksar, VahShir.
- Polarity: checked = `TRUE` (use Luclin model), unchecked = `FALSE` (classic). All Bucket-2.

### 4.6 Chat Spam (22) — all `[Options]`, `1/0`
- **Combat (7):** `CriticalSpells`(0) `CriticalMelee`(0) `SpellDamage`(0) `DotDamage`(0) `HideDamageShield`(1) `Strikethrough`(0) `Stun`(0)
- **Pets (4):** `PetAttacks`(0) `PetMisses`(1) `PetSpells`(0) `SwarmPetDeath`(0)
- **Spells (4):** `PCSpells`(0) `NPCSpells`(0) `FocusEffects`(0) `HealOverTimeSpells`(0)
- **Social (7):** `BadWord`(1) `Spam`(1) `FellowshipChat`(0) `MercenaryMessages`(1) `ItemSpeech`(0) `Achievements`(0) `PvPMessages`(1)
- (defaults above are the form's hardcoded `DefaultValue`; reconcile against `eqclient_master.ini` in Phase 3 — all present in live INI and match.) All Bucket-2.

### 4.7 Particles (16)
- **Opacity sliders (3, `[Defaults]`, float 0.000000–1.000000 shown 0–100%):** `SpellParticleOpacity` `EnvironmentParticleOpacity` `ActorParticleOpacity` — master all `1.000000`
- **Density sliders (3, `[Defaults]`, float):** `SpellParticleDensity` `EnvironmentParticleDensity` `ActorParticleDensity` — master all `0.000000`
- **Near-clip numerics (3, `[Defaults]`, float):** `SpellParticleNearClipPlane` `EnvironmentParticleNearClipPlane` `ActorParticleNearClipPlane` — master `2.000000`
- **Cast/armor filters (4, `[Defaults]`, int):** `SpellParticleCastFilter`(1) `EnvironmentParticleCastFilter`(24) `ActorParticleCastFilter`(1) `ActorNewArmorFilter`(24)
- **Misc (3, `[Options]`):** `FogScale`(float 2.800000) `LODBias`(int 10) `SameResolution`(1/0). All Bucket-2.

### 4.8 Video Mode (12) — all `[VideoMode]`, int
- **Resolution (6):** `Width`(1920) `Height`(1080) `WindowedWidth`(1920) `WindowedHeight`(1067) `WinEQWidth`(1920) `WinEQHeight`(1200)
- **Offsets (4):** `WindowedModeXOffset`(0) `WindowedModeYOffset`(0) `XOffset`(0) `YOffset`(0)
- **Fullscreen (2):** `FullscreenRefreshRate`(0) `FullscreenBitsPerPixel`(32)
- ⚠ **G + F:** `Width/Height/WindowedWidth/WindowedHeight` collide with the slim-titlebar **Operational** bucket. Form is self-labelled "experimental." Candidate to hide or gate (see §8).

### 4.9 Keymaps (9) — all `[KeyMaps]`, DirectInput scan codes (modifier flags in high bits)
`KEYMAPPING_TARGETNPC_2`(209) `KEYMAPPING_CONSIDER_2`(83) `KEYMAPPING_CYCLENPCTARGETS_2`(79) `KEYMAPPING_TOGGLETWOTARGETS_1`(82) `KEYMAPPING_TOGGLETWOTARGETS_2`(0) `KEYMAPPING_AUTOPRIM_2`(211) `KEYMAPPING_POTION_SLOT_3_1`(0) `KEYMAPPING_CMD_CLIPBOARD_PASTE_1`(536870959) `KEYMAPPING_CMD_TOGGLE_AUDIO_TRIGGER_WINDOW_1`(268435486). All Bucket-2. Already correct (touch-gated, not enforced).

**Total: 34 + 32 + 22 + 16 + 12 + 9 = 125 settings.**

---

## 5. Bugs & Anomalies (found during the reality audit)

1. **`AANoConfirm` inverted** (B/C/I) — `checked⟺0` in `LoadFromIni`, `SeedFromIni`, `ApplyToIni`, `EnforceOverrides`. Label "AA No Confirm" implies checked = skip confirm = `1`. Fix: `OnValue="1"` in the descriptor (one place). **Verify EQ semantics first** (MQ2 / EQ docs) — high confidence but confirm before flipping.
2. **Universal launch clobber** (D) — `EnforceOverrides` re-writes Bucket-2 values every launch (main + Models + ChatSpam + Particles + VideoMode). Stomps in-game changes. Fix: enforce Operational bucket only.
3. **Main + ChatSpam write-all** (C/D) — no touch-gating; one Save claims every key. Fix: per-key-touch via descriptor baseline diff.
4. **Read-2-sections / write-1** (I) — main form reads `[Defaults]`+`[Options]`, writes one. Latent ghost generator. Fix: one canonical section per key.
5. **Dual parser** (F/K) — `SeedFromIni` ≈ `LoadFromIni`, hand-synced. Fix: both consume the table.
6. **VideoMode ↔ slim geometry collision** (F/G) — already needs slim-aware scrub hacks. Fix: move geometry keys to Operational, hide/gate them in the experimental form.
7. **`ConfiguredKeys` half-defeated** — `SaveSettings` unions ~28 keys every Save, so the anti-clobber set claims everything. Becomes vestigial once Bucket-2 leaves launch-enforce; remove or repurpose.
8. **`CPUAffinity6–9`** present in live INI but model manages only 0–5. Leave untouched (outside our set, per D) — note only.

---

## 6. Phased Implementation Plan (surgical, tab-by-tab, resumable)

Each phase ends GREEN (builds + its `--test` passes + the relevant window smoke-checked) before the next starts. Per Rule 10, describe state back after each.

- [x] **Phase 0 — Descriptor scaffold. ✅ DONE 2026-06-06.** `Config/EqClientIniSchema.cs`: `IniSetting` + `Bucket`/`IniKind` + factories + the 125-row `EqClientIniSchema.All` table (§4). `Core/EqClientSchemaTests.cs` + `--test-eqclient-schema` (no duplicate (section,key); every toggle On≠Off; Default writable + round-trips; numbers canonical + in-range). PASS: 125 settings (82 toggle / 34 number / 9 keycode; 6 operational, 1 hard-push). Generic Load/Save/Enforce loops deferred to Phase 1 (where their first consumer lives). Inert — no existing path rewired. EXPERIMENTAL added to all 6 window titles.
- [x] **Phase 1 — Main form onto the table. ✅ DONE 2026-06-06 (commits 908d8ea, ecd3e8e).** Added `Config/EqClientIniDocument.cs` (section-aware engine). `LoadFromIni` → live read via schema; `SaveSettings` → per-key-touch write via engine; `ApplyToIni` removed; AANoConfirm polarity fixed (schema row); `EnforceOverrides` narrowed to Operational (WindowedMode/Maximized + slim geometry) — Bucket-2 no longer re-stamped at launch. **Verified:** `--test-eqclient-inidoc` + `--test-eqclient-save` pass; `--diag-render-form` against the real eqclient.ini shows all 34 controls matching disk (AA No Confirm now correctly unchecked). Found+fixed: MaxFPS/MaxBGFPS capped at 99 but fresh EQ ships 100 → raised to 999. **Residual:** `EnforceOverrides` launch path is build-clean + logically a deletion, but not yet runtime-smoked — do a launch smoke (or an enforce unit test) before final ship.
- [x] **Phase 2 — ChatSpam onto the table. ✅ DONE 2026-06-06 (PR #16, branch `feat/eqclient-settings-phase2plus`).** Extracted the Phase-1 binding loop into a shared engine `UI/EqClientBindings` (`LoadInto`/`SaveChanged`) and migrated BOTH the main form and ChatSpam onto it (spec §3 realized — one engine, not N parsers). ChatSpam was the write-all offender: now live-read display + touch-gated save (only changed filters) + launch re-stamp dropped from `EnforceOverrides`; its (Key,Label,Default) arrays collapsed to grouped key-lists (label/default/polarity live once in the schema). New `--test-eqclient-chatspam-save` proves the touch-gate (absent/untouched keys NOT inserted = no write-all; unmanaged preserved; no ghost). Also on this PR, **v3.24.49 fix-forward** from STEP 0's Phase-1 verifier swarm: the MaxFPS/MaxBGFPS cap was raised 99→999 in only 2 of 6 sites in v3.24.48 (AppConfig.Validate + SeedFromIni×4 + ProcessManager still clamped 99) → unified to one `EqClientIniSchema.MaxFpsCap`.
- [ ] **Phase 3 — Models onto the table** (already touch-gated; drop launch enforce; reconcile defaults vs master).
- [ ] **Phase 4 — Particles onto the table** (float formatting preserved; drop launch enforce).
- [ ] **Phase 5 — VideoMode onto the table** (move geometry keys to Operational; G decision from §8 applied).
- [ ] **Phase 6 — Keymaps onto the table** (already correct — wire to table for consistency, no behaviour change).
- [ ] **Phase 7 — Launch writer = Operational only.** `EnforceOverrides` reduced to Bucket-1 + slim geometry. Retire/repurpose `ConfiguredKeys`. Verify in-game change → relaunch → change survives (eqgame-wins proof).
- [ ] **Phase 8 — Hard-push + master baseline.** One-time push of Bucket-3 (`ChatServerPort`→0) with per-install flag; curate `eqclient_master.ini` into the clean ship-baseline. (Popup notice deferred — stub the flag now.)
- [ ] **Phase 9 — G: non-user-facing.** Apply §8 decisions (hide/gate experimental VideoMode dims, etc.).
- [ ] **Phase 10 — DPI verify (J).** All 6 windows at 100% + 150% (Tiny11 lab `lab dpi 150` / `--test-dpi-baseline`). Final smoke + ship.

---

## 7. Verification Plan

- **Display-vs-disk diff** (per window): on open, every control == the live INI value (or master default if absent). New `--test-settings-roundtrip` exercises load→save→reload identity on a fixture INI.
- **No-ghost assertion:** after a save, grep the INI — each managed key appears in exactly ONE section with the expected value; no stale copy in another section.
- **eqgame-wins proof:** set a Bucket-2 value in-game, exit, relaunch via EQSwitch — value persists (Operational-only enforce).
- **Don't-clobber proof:** a hand-added unknown `[Options]` key survives a full save cycle untouched.
- **DPI (J):** 100% + 150% on all 6 windows, real-150% via Tiny11 lab (DeviceDpi=144), no clipping; `EQSwitch.exe --test-dpi-baseline` green.
- Per Rule 12: any skipped check is reported loudly, not silently passed.

---

## 8. Decisions Log / Open Items (resolve during review or in-phase)

- **Hard-push set:** `ChatServerPort`→`0` (Disable Chat Server) CONFIRMED. Candidate additions for Nate's yes/no (multibox-universal, lag-saving): `AllowAutoDuck`→0? `CombatMusic`→0? — propose during Phase 8, do NOT assume.
- **`AANoConfirm` polarity:** verify EQ/MQ2 semantics (expected: `1` = no-confirm) before flipping. Phase 1.
- **G hide-list:** experimental `EQVideoModeForm` resolution/offset dims (collide with slim geometry); raw scan-code Keymaps is power-user but harmless. Decision: gate VideoMode dims behind an "advanced" reveal, or make them read-only when slim-titlebar is on. Confirm with Nate.
- **Canonical section for dual-read keys:** defaulted to current write-section (table §4). `MaxFPS`/`MaxBGFPS` intentionally dual-section (EQ reads `[Options]` at runtime; `[Defaults]` kept for compatibility) — one writer via a multi-section descriptor.
- **Popup notice for hard-push:** deferred to end (Nate).
- **ProcessManager FPS overlap (F):** `MaxFPS`/`MaxBGFPS` edited in both main Performance and ProcessManager — make ProcessManager consume the same descriptor so they can't diverge.

---

## 9. Fresh-INI + Patcher dig (2026-06-06)

Inputs: Nate-provided clean `eqdefaults/eqclient.ini` + `dalaya_patcher.exe`.

1. **Patcher is INI-clean.** dalaya_patcher.exe string scan (216,920 strings): 0 `eqclient`, 0 `WritePrivateProfile`, 0 `[Defaults]/[Options]/[VideoMode]` headers — it patches dlls/exe (60 `.dll`, `patch`, `manifest`), not eqclient.ini. **EQSwitch ↔ patcher do not overlap on settings; no coordination needed.**
2. **Section placement is safe.** Fresh file has all four canonical sections in stable order (`[Defaults]→[HitsMode]→[Options]→[TextColors]→[VideoMode]→[News]→[KeyMaps]`), so `SetIniValue` inserts land in-section, never EOF. Keys absent in fresh that we add only when touched/pushed: `EnvSounds`, `AllowAutoDuck`, `SkyUpdateInterval`, `ChatServerPort`, `ActorClipPlane`, `WinEQWidth/Height`, `XOffset/YOffset` — each inserts into its existing section.
3. **⚠ Two keys sit in the wrong canonical section.** Fresh keeps `WindowedModeXOffset`/`WindowedModeYOffset` (plus `WindowedMode`, `Maximized`) in **[Defaults]**, but the VideoMode form + schema currently treat the offsets as [VideoMode]; EnforceOverrides already dual-writes them. → **Phase 5**: set offsets' canonical to [Defaults]; **Phase 7**: drop the redundant [VideoMode] copy. (§8-G operational/experimental keys — verify against live EQ during Phase 5 before changing.)
4. **AANoConfirm polarity CONFIRMED.** Fresh ships `AANoConfirm=1` (EQ-stock "skip AA confirm") → `1 = no-confirm`. Schema's `On="1"` is correct; the legacy `checked⟺0` was the bug. (Closes §5#1's "verify first".)
5. **Defaults are EQSwitch-opinionated, not EQ-stock.** Fresh differs from EQSwitch's historical defaults on ~20 toggles (fresh `Sound=TRUE`, `Sky=1`, `Anonymous=0`, `BardSongs=0`, `ShowDynamicLights=TRUE`, `CombatMusic=TRUE`, `TextureCache=FALSE`, `TrackAutoUpdate=FALSE`…). Under "don't force opinion except hard-push," each opinionated default must be re-decided at **Phase 8**: **hard-push** (force the multibox value on everyone) or **EQ-stock** (user-choice via live-read). Candidate hard-push set → confirm with Nate (§8). Schema `Default`s are provisional fallbacks — they only surface for keys *absent* from the user's INI; live-read covers every present key, so this does not block Phases 1-6.
6. **Baseline regenerated.** `eqclient_master.ini` ← fresh file (it had been a misleading copy of the broken-in INI). Master = EQ-stock fresh; the hard-defaults live as `Bucket.HardPush` descriptor rows, applied once at runtime — not pre-baked into master.

---

*End of plan. Resume point: §6 phase checkboxes.*
