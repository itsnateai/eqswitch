# PLAN — Account/Character Split + Tray Menu Restructure

**Status:** Design / not started
**Created:** 2026-04-14
**Verified against codebase:** 2026-04-14 — audit found 16 gaps, all patched below. Original gaps list preserved in commit history.
**Re-verified 2026-04-14:** Phase A spot-check passed all 9 patched claims. Surfaced one additional internal inconsistency: original Phase 1 step 2 (swap `List<LoginAccount>` → `List<Account>`) and step 5 (don't touch consumers) cannot coexist — six C# files read `LoginAccount`-only fields off `_config.Accounts`. Resolved with **add-then-rename** transition pattern (see "Add-then-rename transition" section below). Full migration retained per Nate's call so DPAPI passwords survive — building reusable migration framework for future versions.
**Owner:** EQSwitch v3.10.0
**Driver:** Conceptual cleanup of the launch model. Today every entry in the Accounts list conflates account credentials and a specific character. Splitting these into two first-class entities lets the tray expose three distinct launch intents and removes the per-character duplication of credentials.

---

## Goal

Restructure the tray-menu launch surface and underlying config to expose **three intentional launch modes**, each with one-click access from the system tray:

1. **Launch Client (bare)** — `eqgame.exe patchme`. No login. (Already exists — keep as-is.)
2. **Accounts ▸ {account}** — Auto-type credentials, stop at charselect. User picks character manually.
3. **Characters ▸ {character}** — Auto-type credentials AND enter world as that specific character.

Plus: keep **Teams ▸** as it works today (multi-client coordinated auto-login).

The data model behind it splits the current `LoginAccount` (which currently means "an account with a specific character") into two entities:

- **Account** = login credentials. Unique by `(Username, Server)`. Holds `EncryptedPassword`.
- **Character** = a character to play. References an Account. Holds `CharacterName`, `CharacterSlot`, optional metadata.

---

## Why this matters (don't skip)

- The current model forces a separate `LoginAccount` entry per character, with the same DPAPI ciphertext copied across each. Editing the password requires touching every entry.
- "Auto-Enter-World" is a per-row flag on `LoginAccount` today — but the user's actual mental model is "Account = stop at charselect, Character = go all the way in." Encoding that intent in the type system (not a checkbox the user has to remember) eliminates a whole class of "I forgot to tick the box" bugs and makes the tray menu's three sections semantically correct by construction.
- The phantom-click work in `4bc13c9` + `ca66aee` already showed that the failure modes around the autologin flow are subtle. Reducing the surface area (one launch path per intent) makes future testing tractable.

---

## Scope

### In scope
- New `Account` and `Character` data models in `Models/`.
- Config schema v3 → v4 migration that splits the existing `Accounts: List<LoginAccount>` into `Accounts: List<Account>` + `Characters: List<Character>`. The existing `Characters: List<CharacterProfile>` (a different lightweight display/priority type) gets renamed to `CharacterAliases: List<CharacterAlias>` so the `Characters` name is free for the new launch-target type. See "Naming collision" below.
- Tray menu rebuild in `UI/TrayManager.cs`: three submenus (Accounts, Characters, Teams) with consistent visual treatment.
- Settings → Accounts tab redesign: two-section layout with Accounts (top) and Characters (bottom), or master-detail tree. Final shape TBD in implementation phase.
- New AutoLoginManager API: `LoginToCharselect(account)` and `LoginAndEnterWorld(character)` replace the overloaded `LoginAccount(LoginAccount)`.
- Hotkey config restructure: split into four families (Action, Account, Character, Team) with `HotkeyBinding` rows that reference Account or Character names by family. See "Hotkey families" section for full schema + migration of existing `AutoLoginN` slots.
- Teams config migration: `Team{N}Account{M}` string fields keep their name but the resolved target is a Character (not a LoginAccount). Existing team strings must round-trip.
- Backwards-compat config loader: detect a v3 config and run the split inline before deserializing. **All existing user data preserved.**

### Out of scope (deliberately)
- Race/class/level discovery in tray labels (parked in `project_eqswitch_chararray_intel.md`).
- The "Enter World on currently-selected" tray item from `TODO_LIST.md` — superseded by this plan (`Characters ▸` IS the one-click enter-world button, indexed by character not by current selection).
- Discord/IPC integration for tray-menu actions.
- Visual theming changes outside what's needed to keep the new menu sections consistent.

---

## Naming collision with existing `CharacterProfile`

`Config/AppConfig.cs:474` already defines `CharacterProfile` (`Name`, `Class`, `Notes`, `SlotIndex`, **`PriorityOverride`**). It is consumed by:
- Tray tooltips and window-title decoration (cosmetic).
- **`AffinityManager` for per-character CPU priority overrides** (functional coupling, NOT tooltip-only).

**Decision: Option A — rename `CharacterProfile` → `CharacterAlias`.** The new `Character` entity takes the clean name.

Concrete preservation rules for the rename + property/JSON split:
- `CharacterAlias` keeps all existing fields: `Name`, `Class`, `Notes`, `SlotIndex`, `PriorityOverride`, `DisplayName` getter.
- `AppConfig.CharacterAliases: List<CharacterAlias>` — NEW C# property name, serializes as JSON key `"characterAliases"`.
- `AppConfig.Characters: List<Character>` — SAME C# property name as v3 but now holds the new launch-target type. Serializes as JSON key `"characters"` (same as v3 — the key is reclaimed for the new type).
- `AffinityManager` updated to read `PriorityOverride` from `_config.CharacterAliases` (was `_config.Characters`). Pure rename, no behavior change.
- All tray-tooltip/window-title code that iterates `_config.Characters` gets updated to iterate `_config.CharacterAliases`.

**JSON migration** (inside `MigrateV3ToV4`):
```csharp
// Move the v3 "characters" list (CharacterProfile data) to "characterAliases"
if (root["characters"] is JsonNode oldChars)
{
    root["characterAliases"] = oldChars.DeepClone();
}
// Replace "characters" with an empty array that will be populated from the LoginAccount split
root["characters"] = new JsonArray();
```
Step 1 of the migration then populates the new `"characters"` array from the LoginAccount split.

---

## Schema (v4)

```csharp
// Models/Account.cs (new)
public class Account
{
    public string Name { get; set; } = "";          // user-friendly label, e.g. "Main Account"
    public string Username { get; set; } = "";       // login username
    public string EncryptedPassword { get; set; } = "";  // DPAPI ciphertext, base64
    public string Server { get; set; } = "Dalaya";
    public bool UseLoginFlag { get; set; } = true;
}

// Models/Character.cs (new)
public class Character
{
    public string Name { get; set; } = "";          // EQ character name (matches in-game name)
    public string AccountUsername { get; set; } = ""; // FK to Account.Username
    public string AccountServer { get; set; } = "Dalaya"; // FK part 2 — disambiguates if Username repeats across servers
    public int CharacterSlot { get; set; } = 0;     // 0 = auto (heap-scan name match), 1-10 = explicit slot
    public string DisplayLabel { get; set; } = "";  // optional override for menu display
    public string ClassHint { get; set; } = "";     // optional, for tooltips only
    public string Notes { get; set; } = "";
}

// AppConfig final state (after Phase 6 cleanup):
public List<Account> Accounts { get; set; } = new();              // JSON key "accounts" (clean state, JsonPropertyName attribute reclaims the key in Phase 6)
public List<Character> Characters { get; set; } = new();          // JSON key "characters" (clean state, reclaims the key in Phase 6)
public List<CharacterAlias> CharacterAliases { get; set; } = new(); // JSON key "characterAliases" (final from Phase 1)
```

**During the v3.10.0 transition (Phases 1-5)**, AppConfig holds BOTH the old fields (renamed to `LegacyAccounts` / `LegacyCharacterProfiles`, retaining their original JSON keys via `[JsonPropertyName]`) AND the new fields (under transient JSON keys `"accountsV4"` / `"charactersV4"`). See "Add-then-rename transition" section below for the full layout and rationale.

---

## Add-then-rename transition (Phase 1 schema)

The cleanest way to swap the v3 `LoginAccount` type for `Account` + `Character` without breaking compilation across consumer files is to **add the new fields alongside the old**, rename the old C# fields with a `Legacy` prefix (preserving their JSON keys), and then progressively migrate consumers in Phases 2-5. Phase 6 deletes the legacy fields.

### Phase 1 AppConfig layout

```csharp
public class AppConfig
{
    public int ConfigVersion { get; set; } = 4;

    // ── Legacy v3 fields (renamed, JSON keys preserved, removed in Phase 6) ──
    [JsonPropertyName("accounts")]
    public List<LoginAccount> LegacyAccounts { get; set; } = new();          // was: Accounts

    [JsonPropertyName("characters")]
    public List<CharacterProfile> LegacyCharacterProfiles { get; set; } = new(); // was: Characters

    // ── New v4 fields (transient JSON keys for v3.10.0; canonical names for code) ──
    [JsonPropertyName("accountsV4")]
    public List<Account> Accounts { get; set; } = new();                     // NEW launch-target accounts

    [JsonPropertyName("charactersV4")]
    public List<Character> Characters { get; set; } = new();                 // NEW launch-target characters

    [JsonPropertyName("characterAliases")]
    public List<CharacterAlias> CharacterAliases { get; set; } = new();      // RENAMED from CharacterProfile (cosmetic/priority metadata)

    // ... all other AppConfig fields unchanged ...
}
```

### Why this layout

- **C# names match the final state from Phase 1 onwards.** Code written in Phases 2-5 uses `_config.Accounts` (Account) and `_config.Characters` (Character) — the same names they'll have after Phase 6. No second rename needed.
- **JSON keys for the new fields use a `V4` suffix** to avoid colliding with the legacy data (still in JSON under `"accounts"` / `"characters"` for downgrade safety and consumer-read continuity).
- **Legacy C# fields are renamed to `LegacyAccounts` / `LegacyCharacterProfiles`** so the new clean names are free. Their JSON keys stay the same via `[JsonPropertyName]`.
- **Phase 1 mechanically updates every `_config.Accounts` → `_config.LegacyAccounts` reference** in TrayManager, AutoLoginManager, SettingsForm, WindowManager, AutoLoginTeamsDialog (~30 sites). Same for `_config.Characters` → `_config.LegacyCharacterProfiles` in AffinityManager and the two config-swap sites. **No logic changes — pure rename.** Build passes after Phase 1.
- **App behavior post-Phase-1 is identical to v3.9.3.** Same tray menu, same settings, same flow. Just a cleaner internal data model with the new fields populated and ready for Phases 2-5 to consume.

### Phase 6 cleanup migration (v4 → v5)

```csharp
private static void MigrateV4ToV5(JsonObject root)
{
    // Move new fields back to canonical JSON keys, drop legacy data
    if (root["accountsV4"] is JsonNode v4Accounts)
    {
        root["accounts"] = v4Accounts.DeepClone();
        root.Remove("accountsV4");
    }
    if (root["charactersV4"] is JsonNode v4Chars)
    {
        root["characters"] = v4Chars.DeepClone();
        root.Remove("charactersV4");
    }
    // characterAliases keeps its key
}
```

Plus C# changes: delete `LegacyAccounts`, `LegacyCharacterProfiles`, `LoginAccount.cs`, the inline `CharacterProfile` class. Remove `[JsonPropertyName("accountsV4")]` and `[JsonPropertyName("charactersV4")]` attributes (default camelCase naming gives `"accounts"` / `"characters"`).

### Reusable migration framework

The v3→v4 migration code is the **template** for all future schema changes. The pattern (rename old field with `Legacy` prefix + `[JsonPropertyName]`, add new field with transient suffix, mechanical consumer migration over phases, final-phase consolidation) is reusable for any future breaking change. Document this pattern in `_templates/references/` after Phase 6 ships.

---

## Migration (v3 → v4)

Adds `MigrateV3ToV4(JsonObject root)` to `Config/ConfigVersionMigrator.cs` — same pattern as existing `MigrateV0ToV1 / V1ToV2 / V2ToV3`. Runs pre-deserialization on the raw JSON tree so schema changes are transparent to `AppConfig`. **(NOT `ConfigMigration.cs` — that file is dedicated to AHK `.cfg` → JSON import and is unrelated to versioned migrations.)**

### Step 1 — Account + Character split from `Accounts` list

For each old `LoginAccount` entry:

1. **Account dedup.** Compute key `(Username, Server)`. If no `Account` with that key exists yet, create one — carry `Name`, `Username`, `EncryptedPassword`, `Server`, `UseLoginFlag`. **Drop** the `AutoEnterWorld` flag here (it migrates via the hotkey and team rules below, not onto the Account itself).
2. **Character creation.** If `CharacterName` is non-empty, create a `Character` referencing the Account by `(AccountUsername, AccountServer)`. Carry `CharacterSlot` and `CharacterName` (Character.Name). `DisplayLabel` defaults to empty; `ClassHint`/`Notes` empty. If the same Account had multiple v3 entries with different CharacterNames (common multibox pattern), each generates its own Character row — all pointing to the single deduped Account.
3. **Edge case — no CharacterName.** If the v3 entry has `CharacterName = ""`, no Character is created. The Account alone is enough to log in and stop at charselect. Logged at INFO.

### Step 2 — Hotkey migration (two-step lookup)

v3 stored each quick-login hotkey as **two separate fields** that must be joined:

- `HotkeyConfig.AutoLoginN` — the key combo string (e.g. `"Alt+1"`).
- `AppConfig.QuickLoginN` — the username/charname of the bound account (top-level field, not inside HotkeyConfig).

Migration, per slot N in 1..4:

1. Read `combo = HotkeyConfig.AutoLoginN` and `target = QuickLoginN`. If either is empty, skip.
2. Resolve `target` against the v3 Accounts list, same way the runtime does: `Accounts.FirstOrDefault(a => a.CharacterName == target) ?? Accounts.FirstOrDefault(a => a.Username == target)`. This order matters — CharacterName wins on ambiguity, matching today's `ExecuteQuickLogin` at [TrayManager.cs:1320](EQSwitch/UI/TrayManager.cs:1320).
3. Route by source intent:
   - **CharacterName match + `AutoEnterWorld = true`** → bind to `CharacterHotkeys[N-1]` with `TargetName = CharacterName`.
   - **CharacterName match + `AutoEnterWorld = false`** → bind to `AccountHotkeys[N-1]` with `TargetName = Account.Name` (**not** username, since Account.Name is the new canonical identifier).
   - **Username match** (CharacterName was empty in v3) → bind to `AccountHotkeys[N-1]` with `TargetName = Account.Name`.
4. The v3 `HotkeyConfig.AutoLogin1-4` and `AppConfig.QuickLogin1-4` fields are **preserved in the JSON** (via `[JsonInclude]` retention) for one release as downgrade safety. Removed in v3.11.0.
5. Log each decision at INFO: `"AutoLogin1 migrated: combo='Alt+1' target='Backup' -> CharacterHotkey[0]={Backup} (enter world)"`.

### Step 3 — Team field rebinding

`Team{N}Account{M}` string fields (8 total: N=1..4, M=1..2 — teams have exactly 2 slots) currently store a username or charname. Migration resolves each the same way as Step 2:

1. Try `Accounts.First(a => a.CharacterName == field) ?? Accounts.First(a => a.Username == field)`.
2. **Prefer Character** — if a Character was generated for that LoginAccount in Step 1, rebind the team field to `Character.Name`.
3. **Fall back to Account** — if no Character exists (v3 entry had empty CharacterName), rebind to `Account.Name`. Log warning: `"Team 1 Slot 1: resolved to Account 'Main' (no character) — this member will stop at charselect unless you bind a Character"`.
4. Total resolution failures: leave the field blank and log at WARN.

### Step 4 — Team-level AutoEnterWorld preservation (decided)

v3 has per-team override flags: `Team1AutoEnter`, `Team2AutoEnter`, `Team3AutoEnter`, `Team4AutoEnter`. These are **preserved as-is** in v4. See "Teams — semantics" section below for how they interact with Character targets in the new world.

### Step 5 — TrayClickConfig action strings

`TrayClickConfig.SingleClick/DoubleClick/TripleClick/MiddleClick/MiddleDoubleClick` can hold action strings like `"AutoLogin1"`, `"LoginAll"`, `"LoginAll2".."LoginAll4"`, etc. These are **semantic action names, not v3 slot references** — they survive the hotkey restructure unchanged. The dispatcher (`ExecuteTrayAction`) will be updated to route `"AutoLogin1"` through the new `AccountHotkeys[0]` dispatch path (if populated) or log a warning. No JSON migration needed for this field.

### Migration test fixtures

Seed a v3 `eqswitch-config.json` with five patterns — all round-trip without data loss:

- **(a)** One LoginAccount with CharacterName set, AutoEnterWorld=true → 1 Account + 1 Character.
- **(b)** One LoginAccount, three entries sharing the same (Username, Server) but different CharacterNames → 1 Account + 3 Characters.
- **(c)** One LoginAccount with empty CharacterName → 1 Account, 0 Characters.
- **(d)** QuickLogin1 set to a CharacterName with AutoLogin1="Alt+1" → CharacterHotkey[0] bound.
- **(e)** QuickLogin2 set to a Username (no matching CharacterName in v3) → AccountHotkey[1] bound.

Also verify: DPAPI password round-trip after migration (decrypt with `CredentialManager.Decrypt` produces the same plaintext as before). Silent ciphertext corruption here would brick autologin for every account.

---

## Tray menu — new structure

```
⚔  EQ Switch v3.10.0  ⚔
─────────────────────────
⚔  Launch Client                        Hotkey         (bare eqgame.exe patchme)
🎮  Launch Team                          Ctrl+Alt+1    (one-click Team 1 default)
─────────────────────────
🔑  Accounts ▸                            (login + stop at charselect)
    👤  Main Account               Ctrl+1
    👤  Alt Account                Ctrl+2
    ─────────────
    ⚙  Manage Accounts...
🧙  Characters ▸                          (login + enter world)
    🧙  Backup       (Cleric)      Alt+1
    🧙  Healpots     (Cleric)      Alt+2
    🧙  Acpots       (Rogue)       Alt+3
    🧙  Staxue       (Ranger)
    ─────────────
    ⚙  Manage Characters...
👥  Teams ▸                               (multi-client, parallel launch)
    🚀  Auto-Login Team 1          Ctrl+Alt+1    (also root button above)
    🚀  Auto-Login Team 2          Ctrl+Alt+2
    🚀  Auto-Login Team 3          Ctrl+Alt+3
    🚀  Auto-Login Team 4          Ctrl+Alt+4
    ─────────────
    ⚙  Manage Teams...
─────────────────────────
[existing Clients submenu, Process Manager, Video Settings, Settings, Help, Exit]
```

Visual rules:
- Account icon `🔑` / Character icon `🧙` / Teams icon `👥` — distinct enough at glance that there's zero ambiguity about which mode you're in. (These are tray-menu UI strings, not code — emoji use here is intentional and consistent with existing menu items like `⚔ Launch Client`.)
- Optional class hint in Character row label `(Cleric)` / `(Rogue)` — pulled from `Character.ClassHint`. If empty, just the name.
- Account row label fallback chain: `account.Name → account.Username`. Never empty. Tooltip always shows `username@server` for disambiguation.
- Character row label fallback chain: `character.DisplayLabel → character.Name`. Never empty. Tooltip shows `→ Account 'AccountName' · slot auto/N`.
- Root "Launch Team" button stays — fires Team 1 as a one-click default, preserving today's behavior at [TrayManager.cs:802-804](EQSwitch/UI/TrayManager.cs:802). Team 1 is also reachable via Teams submenu; both routes end in the same `FireTeam(1)` call.

---

## File-by-file change list

### Models
- **NEW** `Models/Account.cs` — schema above.
- **NEW** `Models/Character.cs` — schema above.
- **NEW** `Models/HotkeyBinding.cs` — `{ Combo, TargetName }` row used by Account/Character hotkey families.
- **KEEP then obsolete** `Models/LoginAccount.cs` — needed during migration as the deserialization target for the v3 `accounts` JSON array before it's split. Mark `[Obsolete("v3 compat only; removed in v3.11.0")]`. Removed in v3.11.0 after one release of downgrade safety.
- Rename existing `CharacterProfile` to `CharacterAlias` — in its own file `Models/CharacterAlias.cs` for cleanliness. **Preserve all existing fields** (`Name`, `Class`, `Notes`, `SlotIndex`, `PriorityOverride`, `DisplayName` getter). Update all callers — notably `AffinityManager` (reads `PriorityOverride`) and tray tooltip code.

### Config
- `Config/AppConfig.cs`:
  - Replace `List<LoginAccount> Accounts` with `List<Account> Accounts` (same JSON key `"accounts"`).
  - Replace `List<CharacterProfile> Characters` with `List<CharacterAlias> CharacterAliases` (NEW JSON key `"characterAliases"` — migration moves the node).
  - Add `List<Character> Characters` (NEW launch-target type, reclaims the `"characters"` JSON key).
  - Bump `CurrentConfigVersion` to `4`.
  - The v3.9.0 `AutoEnterWorld` propagation block in `Validate()` becomes a no-op for v4 — `MigrateV3ToV4` handles the AutoEnterWorld intent routing before `Validate()` ever runs on v4 data.
- `Config/ConfigVersionMigrator.cs` — add `MigrateV3ToV4(JsonObject root)` as a new `case 3:` in the switch, following the existing `MigrateV2ToV3` pattern. The migration is pure JSON tree manipulation; no strongly-typed deserialization inside the migrator.
- `Config/ConfigMigration.cs` — **UNTOUCHED.** Dedicated to AHK `.cfg` import only; no changes.

### Core
- `Core/AutoLoginManager.cs`:
  - Add two public methods that take the new types:
    - `Task LoginToCharselect(Account account, bool? enterWorldOverride = null)` — types credentials, waits for charselect. If `enterWorldOverride == true`, logs a warning and does NOT enter world (Account has no character to select); returns at charselect.
    - `Task LoginAndEnterWorld(Character character, bool? enterWorldOverride = null)` — resolves the backing Account via `Characters` → `(AccountUsername, AccountServer)` → `Accounts`. Runs full chain ending with Enter World. `enterWorldOverride == false` short-circuits at charselect; `null` means "use default (enter world)".
  - Internal `Task RunLoginSequence(Account, Character?, bool enterWorld)` does the heavy lifting; both public methods are thin wrappers that build the right parameters then delegate.
  - Keep existing `Task LoginAccount(LoginAccount account, bool? teamAutoEnter = null)` as `[Obsolete("Use LoginToCharselect or LoginAndEnterWorld")]` wrapper that routes to the right new method based on `account.CharacterName` + `teamAutoEnter`. Remove in Phase 6.
  - Preserve all the phantom-click defenses: `gameState==5` gate in the native side, `result==-2` handling in [AutoLoginManager.cs:417](EQSwitch/Core/AutoLoginManager.cs:417), drag-aware rect tracking. Re-grep after every phase to confirm.

### UI
- `UI/TrayManager.cs`:
  - Rewrite `BuildContextMenu()` (starts at [line 775](EQSwitch/UI/TrayManager.cs:775)) to produce the three-submenu structure:
    - Root "Launch Client" button stays.
    - Root "Launch Team" button stays — **fires Team 1 as a one-click default** (matches current behavior at [line 802-804](EQSwitch/UI/TrayManager.cs:802)).
    - New `Accounts` submenu (replaces the current mixed-Accounts-and-Teams submenu at [line 807](EQSwitch/UI/TrayManager.cs:807)).
    - New `Characters` submenu.
    - New `Teams` submenu (contains all 4 teams — Team 1 still also accessible via root button for one-click).
  - New helper methods: `BuildAccountsSubmenu()`, `BuildCharactersSubmenu()`, `BuildTeamsSubmenu()`.
  - Rename `FireTeamLogin` → `FireTeam` for symmetry with the new API; preserve parallel fire-and-forget semantics (do NOT switch to sequential await).
  - Both "Manage Accounts..." and "Manage Characters..." items call `ShowSettings(2)` (the literal Accounts tab index per [line 841](EQSwitch/UI/TrayManager.cs:841)) with an optional inner-section focus parameter for future scroll-to-section.
  - Account/Character menu label fallback: `item.DisplayLabel ?? item.Name ?? item.Username` (Accounts) / `character.DisplayLabel ?? character.Name` (Characters). Never fall back to empty.
- `UI/SettingsForm.cs`:
  - Accounts tab redesign: header "Accounts" + DataGridView of Account rows (Name, Username, Server, Flag, Actions); below it, header "Characters" + DataGridView of Character rows (Name, Account FK shown as Account.Name, Slot, ClassHint, HK, Actions). Both sections styled identically using existing `DarkTheme` factories.
  - Add/Edit dialogs: separate `AccountEditDialog` and `CharacterEditDialog`. Both modal, both use `DarkTheme.MakeButton`/`AddNumeric`/`AddComboBox`/`StyleForm`. Character dialog has an Account picker (combo of existing accounts; cannot save without one).
  - Staging fields follow the **existing `_pending*` convention** (see [line 192-211](EQSwitch/UI/SettingsForm.cs:192)). Add `_pendingAccountsV4: List<Account>` and `_pendingCharacters: List<Character>` (or simply `_pendingAccounts` once `LoginAccount` is gone). Existing `_pendingTeam1A..Team4B` and `_pendingTeam{N}AutoEnter` stay.
  - `ApplySettings()` validates cross-section relationships (orphan Character → show dialog), then atomically swaps both lists into the new config object.
  - On save, dedup logic enforces unique `(Username, Server)` per Account, unique `Name` per Account, unique `Name` per Character.
- `UI/AutoLoginTeamsDialog.cs`:
  - Per-slot resolution status indicator (OK/WARN/FAIL). Computed on dialog open and after every edit.
  - Slot dropdowns list **both Accounts and Characters** (clearly labeled). User can point a team slot at either.
  - `Team{N}AutoEnter` checkbox semantics preserved — override whether the team forces enter world.

### Native
- **No changes required.** SHM contract is unchanged. Heap-scan name lookup still works — the `Character.Name` matches the in-game name that the heap scan reports, so `CharSelectReader.RequestSelectionByName` gets exactly what it needs. This is by design — the split is a UI/data-model concern only. Re-verify after every phase with a grep for `XWM_LCLICK` in native code.

### Hotkeys
- `Config/AppConfig.cs.HotkeyConfig`: add `AccountHotkeys: List<HotkeyBinding>` and `CharacterHotkeys: List<HotkeyBinding>`. Keep existing `AutoLogin1-4` string fields as `[Obsolete]` during v3.10.0 (downgrade safety); remove in v3.11.0. Keep `TeamLogin1-4` unchanged. Keep `LaunchOne` / `LaunchAll` / all switch/arrange hotkeys unchanged. `DirectSwitchKeys: List<string>` **unaffected** — those are slot-jump keys for already-running clients, orthogonal to login.
- `AppConfig.cs`: keep `QuickLogin1-4` string fields as `[Obsolete]` for one release, same reason. Remove in v3.11.0.
- `Core/HotkeyManager.cs`:
  - **Verify existing API before committing to dynamic IDs.** Current `HotkeyManager.Register` is called by string name (see [TrayManager.cs:382-389](EQSwitch/UI/TrayManager.cs:382)) — it assigns IDs internally. If the public API doesn't expose arbitrary-ID registration, add a new overload `RegisterWithId(int id, string combo, Action callback, string name)` that lets callers control the ID. Do this before relying on the 1000-1099 / 1100-1199 ranges.
  - Dispatch the new ranges in `HotkeyManager.WndProc` (or wherever the WM_HOTKEY handler lives — verify during Phase 5).
- `UI/TrayManager.cs`: update `RegisterGlobalHotkeys` (~line 382) to register the new Account and Character hotkey ranges in addition to the existing ones. Old `AutoLogin1-4` registrations become dead code during v3.10.0 — keep them wired to `ExecuteTrayAction("AutoLogin1")` etc. which now routes to the new dispatch (or logs a deprecation warning if unbound).
- Migration: per the "Step 2" hotkey migration rule above — v3 `(HotkeyConfig.AutoLoginN, QuickLoginN)` pair → single new `HotkeyBinding` in the right family list, based on the source LoginAccount's `AutoEnterWorld` + `CharacterName`.

### Teams
- Field names stay (`Team1Account1`, `Team1Account2`, ..., `Team4Account2` — **exactly 2 slots per team × 4 teams = 8 fields**). Team-level AutoEnter flags (`Team1AutoEnter`..`Team4AutoEnter`) stay. Internally resolve each slot string against `Characters` first, then `Accounts`.
- `TrayClickConfig.SingleClick/DoubleClick/TripleClick/MiddleClick/MiddleDoubleClick` — unchanged. Action strings (`"AutoLogin1"`, `"LoginAll"`, etc.) are semantic names routed by `ExecuteTrayAction`, not data-schema references. Dispatcher may need a minor update to route `"AutoLoginN"` through the new hotkey dispatch path during the deprecation window.

---

## Nuances to watch

1. **Concurrent-edit safety in SettingsForm.** The `_pending*` staging convention must carry through — `_pendingAccounts: List<Account>` and `_pendingCharacters: List<Character>` replace today's `_pendingAccounts: List<LoginAccount>`. Validation on Apply (not on every cell edit) so cross-section edits stage cleanly. Bug-prone area: a Character referencing an Account the user just deleted — show a modal "Delete 2 dependent Characters?" rather than silent cascade.
2. **DPAPI scope unchanged.** `EncryptedPassword` migration is a copy — same DPAPI scope (CurrentUser), same machine. No re-encrypt needed. Verify round-trip explicitly in a migration test (migration fixture (a)) — silent corruption would brick autologin for every account.
3. **`LoginAccount(account, teamAutoEnter)` call sites.** Current code has it called from: [TrayManager.cs:817](EQSwitch/UI/TrayManager.cs:817) (tray menu direct click) and [TrayManager.cs:1330](EQSwitch/UI/TrayManager.cs:1330) (via ExecuteQuickLogin). Every call site must route through the new methods during Phase 2. Keep the `[Obsolete]` wrapper until Phase 6 so compile errors surface any missed call site.
4. **Single-instance + lifecycle.** `AutoLoginManager._activeLoginPids` is keyed on PID not on account, so parallel team launches don't clash by construction. The split doesn't change this, but verify with a manual trace — PID dedup must survive the new API.
5. **WaitForScreenTransition behavior is unchanged for both modes.** It returns once charselect is rendered. `LoginToCharselect` returns at that point. `LoginAndEnterWorld` continues with the Enter World request. The flow split happens after `WaitForScreenTransition`, before the EnterWorld block.
6. **Character slot validity.** If a user recreates a character on a different slot, the heap-scan name lookup (`RequestSelectionByName`) finds the correct row by name regardless of slot index. Keep `Character.CharacterSlot` as legacy/fallback only; prefer name-based selection.
7. **Tray menu rebuild cost.** `BuildContextMenu()` runs on every config reload. With 20+ characters the menu becomes long. Consider grouping characters by Account in the submenu (`Account A ▸ Char1, Char2; Account B ▸ Char3`) when count > 10. Decide in Phase 3 after measuring a real user's character list.
8. **Memory ownership.** `ToolStripMenuItem` and `Font` instances must be disposed when the menu rebuilds. Existing code does `_contextMenu?.Dispose()` at [line 778](EQSwitch/UI/TrayManager.cs:778) — `ContextMenuStrip.Dispose()` disposes all contained items per the WinForms contract. `_boldMenuFont?.Dispose()` is also called explicitly. No additional work needed.
9. **Hotkey stale binding after target delete.** If a Character bound to an AccountHotkey/CharacterHotkey is deleted, show a red "Target deleted — rebind" indicator in the Hotkeys tab. Don't silently drop the binding — user may want to rebind to a differently-named replacement.
10. **Affinity overrides use `CharacterAlias.PriorityOverride`, NOT `Character.PriorityOverride`.** The new `Character` launch-target type has no priority field (launching is orthogonal to per-process priority). Per-character priority stays on the renamed `CharacterAlias` type. Migration is a pure rename — no data movement between lists. Clarify in AffinityManager that "character profile" = display/priority metadata, "character" = launch target.
11. **Team launch parallelism.** Current `FireTeamLogin` fires all slots in parallel via `_ = ExecuteQuickLogin(...)`. The new `FireTeam` MUST preserve this — do not accidentally add `await` inside the foreach loop. Sequential waits would change inter-client launch timing and potentially re-expose race conditions that the current parallel fire-and-forget avoids.
12. **TrayClickConfig action string deprecation.** `"AutoLogin1".."AutoLogin4"` action strings in `TrayClickConfig.SingleClick` etc. still need to dispatch correctly during v3.10.0. Dispatcher should route these to the new `AccountHotkeys[0-3]`/`CharacterHotkeys[0-3]` targets based on which list the migration put them into. Add log line on first fire so user knows which new family it routed through.

---

## Build sequence (suggested)

1. **Phase 1 — Models + migration + mechanical consumer rename** (no behavior change). Add `Account.cs`, `Character.cs`, `HotkeyBinding.cs`, `CharacterAlias.cs` (replaces `CharacterProfile`). Update `AppConfig.cs` per the **add-then-rename layout** above (legacy fields renamed with `Legacy` prefix + `[JsonPropertyName]`; new fields added with `V4` JSON-key suffix). Bump `CurrentConfigVersion = 4`. Implement `MigrateV3ToV4(JsonObject root)` in `ConfigVersionMigrator.cs` following the existing pattern. Add scripted-fixture migration tests for the 5 patterns above. Keep `LoginAccount.cs` UNCHANGED (used by `LegacyAccounts` field). **Mechanically rename `_config.Accounts` → `_config.LegacyAccounts` and `_config.Characters` → `_config.LegacyCharacterProfiles` everywhere** (pure search/replace — TrayManager, AutoLoginManager, SettingsForm, WindowManager, AutoLoginTeamsDialog, AffinityManager, ~30 sites total). **No logic changes.** Build passes; app behavior identical to v3.9.3 with new fields populated and ready for consumption in Phases 2-5.
2. **Phase 2 — AutoLoginManager API split.** Add `LoginToCharselect(Account, bool?)` and `LoginAndEnterWorld(Character, bool?)`. Extract common body into private `RunLoginSequence`. Keep `LoginAccount(LoginAccount, bool?)` as `[Obsolete]` wrapper — routes to the right new method based on `account.CharacterName` + `teamAutoEnter`. Update no call sites yet. Regression check: existing autologin from the tray still works exactly as before.
3. **Phase 3 — Tray menu.** Rebuild `BuildContextMenu()` into the three-submenu structure. Keep root "Launch Team" as Team 1 quick-default. Rename `FireTeamLogin` → `FireTeam` (preserve parallel fire-and-forget). Update `BuildAccountsSubmenu`, `BuildCharactersSubmenu`, `BuildTeamsSubmenu`. Tray menu now reads from new `_config.Accounts` / `_config.Characters` (Account/Character types). Update tray-click action dispatch to route through new hotkey family tables. Verify all three launch paths via manual smoke test.
4. **Phase 4 — SettingsForm dual-section UI + AutoLoginTeamsDialog.** Build `AccountEditDialog`, `CharacterEditDialog`. Wire up `_pendingAccounts`/`_pendingCharacters` staging. Dedup + validation on Apply. Settings now reads/writes new `_config.Accounts` / `_config.Characters` instead of `_config.LegacyAccounts`. Add team-slot resolution indicator (OK/WARN/FAIL) to AutoLoginTeamsDialog. Update affinity form to use `_config.CharacterAliases` instead of `_config.LegacyCharacterProfiles`.
5. **Phase 5 — Hotkey families + Team rebinding.** Verify `HotkeyManager` API first — decide ID-range vs name-based dispatch. Implement `AccountHotkeys` / `CharacterHotkeys` lists, register the chosen dispatch scheme. Update Hotkeys tab UI to show the 4 family sections. Migrate v3 `(HotkeyConfig.AutoLoginN, QuickLoginN)` pairs per Step 2 rule. Verify `Team{N}Account{M}` + `Team{N}AutoEnter` round-trips and resolves correctly. Phantom-click defenses verified end-to-end via hotkey-fired launches — `grep XWM_LCLICK eqswitch-dinput8.log | grep -A2 "gameState -> 5"` empty.
6. **Phase 6 — Cleanup + v4→v5 consolidation.** Implement `MigrateV4ToV5(JsonObject root)` per the cleanup migration in "Add-then-rename transition" — moves `accountsV4` → `accounts`, `charactersV4` → `characters` in JSON. Remove `[JsonPropertyName("accountsV4")]` / `[JsonPropertyName("charactersV4")]` C# attributes. Delete `LegacyAccounts`, `LegacyCharacterProfiles` C# fields. Delete `Models/LoginAccount.cs`. Delete `CharacterProfile` class. Remove `[Obsolete] LoginAccount(LoginAccount)` wrapper from AutoLoginManager. Remove `[Obsolete]` `HotkeyConfig.AutoLogin1-4` and `AppConfig.QuickLogin1-4`. Bump `CurrentConfigVersion = 5`. Tag release v3.10.0. Update `README.md` with new tray structure screenshots. **Document the add-then-rename pattern** in `_templates/references/migration-framework.md` for reuse on future schema changes.

---

## Verification gates

Each phase must pass before the next starts:

- **Phase 1:**
  - All 5 migration fixtures (a-e) round-trip without data loss through save → reload → save (no JSON diff).
  - DPAPI password decrypts after migration — `CredentialManager.Decrypt(account.EncryptedPassword)` produces the same plaintext.
  - `configVersion == 4` in the saved file.
  - `accounts` JSON key still holds the v3 `LoginAccount[]` data (legacy, untouched for downgrade safety).
  - `characters` JSON key still holds the v3 `CharacterProfile[]` data (legacy, untouched).
  - `accountsV4` JSON key holds the new `Account[]` data (deduped).
  - `charactersV4` JSON key holds the new `Character[]` data (one row per v3 LoginAccount with a CharacterName).
  - `characterAliases` JSON key holds the migrated `CharacterAlias[]` (copy of v3 `characters` data — renamed type, no schema change).
  - `Team{N}Account{M}` fields resolved correctly post-migration (Character-preferred, Account-fallback).
  - `Team{N}AutoEnter` bool values preserved exactly.
  - `_config.Accounts` (new Account list) and `_config.Characters` (new Character list) both populated and accessible from C# after deserialization.
  - Existing build compiles cleanly — no errors. Mechanical-rename of `_config.Accounts` → `_config.LegacyAccounts` (and `_config.Characters` → `_config.LegacyCharacterProfiles`) confirmed across all 6 consumer files.
  - App behavior identical to v3.9.3 — same tray menu items, same login flow, same settings dialog. Smoke test: launch one account from the tray, confirm character select and (if applicable) enter world both work.
- **Phase 2:**
  - Existing autologin from tray click on an account still works — regression baseline.
  - `[Obsolete] LoginAccount(LoginAccount, bool?)` routes correctly to `LoginToCharselect` vs `LoginAndEnterWorld` based on CharacterName presence + teamAutoEnter override.
  - `_activeLoginPids` drains after each login (no leaks from the new wrapper).
- **Phase 3:**
  - Each of the three launch modes fires correctly from tray: bare → no login, Account → stops at charselect, Character → enters world.
  - Root "Launch Team" button still fires Team 1 as before.
  - New Teams submenu launches Teams 1-4 correctly.
  - Phantom-click gate: `grep XWM_LCLICK eqswitch-dinput8.log | grep -A2 "gameState -> 5"` is empty after all menu-driven launches.
- **Phase 4:**
  - Add / edit / delete an Account and a Character via SettingsForm. Save + reload preserves edits.
  - Cross-section validation: deleting an Account with dependent Characters shows modal "Delete N dependent Characters?" — both buttons work.
  - AutoLoginTeamsDialog shows OK/WARN/FAIL indicators correctly for populated, Account-only, and unresolved team slots.
- **Phase 5:**
  - Each family's hotkeys fire correctly per the "Hotkey families verification gate" section: Action launches bare, Account stops at charselect, Character enters world, Team launches all members in parallel with correct per-team AutoEnter override.
  - Phantom-click gate survives a hotkey-fired launch while already in-game — `eqswitch-dinput8.log` shows `dropped stale Enter World request`, no phantom click.
  - Conflict detection hard-blocks same combo across families.
  - Stale-binding indicator (red "Target deleted") appears after deleting a bound Character/Account.
- **Phase 6:**
  - Code search for `LoginAccount(` returns zero hits (no non-legacy calls).
  - Code search for `QuickLogin[1-4]` returns zero hits.
  - Code search for `HotkeyConfig.AutoLogin[1-4]` returns zero hits.
  - Build + publish single-file self-contained EXE (`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`).
  - Deploy to `C:/Users/nate/proggy/Everquest/EQSwitch/` with v3 config backup intact.
  - README updated with new tray structure screenshots.

---

## SettingsForm Accounts tab — table restructure (detailed)

The current Accounts tab is built around a single DataGridView of `LoginAccount` rows where every row is a character. Splitting the model demands a real GUI restructure, not a column shuffle.

### Layout

Two stacked sections inside the existing Accounts tab. Both use the project's `DarkTheme` factories — no hardcoded colors.

```
┌─ Accounts ─────────────────────────────────────────────────┐
│ ┌────────────┬───────────────┬──────────┬──────┬─────────┐ │
│ │ Name       │ Username      │ Server   │ Flag │ Actions │ │
│ ├────────────┼───────────────┼──────────┼──────┼─────────┤ │
│ │ Main       │ nate1         │ Dalaya   │  ✓   │ ✏  🗑   │ │
│ │ Alt        │ nate2         │ Dalaya   │  ✓   │ ✏  🗑   │ │
│ └────────────┴───────────────┴──────────┴──────┴─────────┘ │
│ [+ Add Account]   [Test Login]   [Import...]   [Export...] │
├────────────────────────────────────────────────────────────┤
│ Characters                                                  │
│ ┌──────────┬─────────┬──────┬──────────┬──────┬──────────┐ │
│ │ Name     │ Account │ Slot │ Class    │ HK   │ Actions  │ │
│ ├──────────┼─────────┼──────┼──────────┼──────┼──────────┤ │
│ │ Backup   │ Main    │ auto │ Cleric   │ F1   │ ✏  🗑   │ │
│ │ Healpots │ Main    │ auto │ Cleric   │ F2   │ ✏  🗑   │ │
│ │ Acpots   │ Main    │ auto │ Rogue    │ F3   │ ✏  🗑   │ │
│ │ Staxue   │ Alt     │ auto │ Ranger   │  —   │ ✏  🗑   │ │
│ └──────────┴─────────┴──────┴──────────┴──────┴──────────┘ │
│ [+ Add Character]   [Bulk import from char list...]         │
└────────────────────────────────────────────────────────────┘
```

### Column behaviors

**Accounts grid:**
- `Name` — user-friendly label. Must be unique. Shown in tray menu under "Accounts ▸".
- `Username` — login. Read-only after creation (changing username = new account; force user to delete + re-add).
- `Server` — combo of known servers (default "Dalaya"). Editable.
- `Flag` (UseLoginFlag) — checkbox column.
- `Actions` — Edit (opens AccountEditDialog), Delete (with confirmation; if any Characters reference this account, prompt to delete dependents or cancel).

**Characters grid:**
- `Name` — EQ in-game character name. Must be unique within Characters list. Shown in tray menu under "Characters ▸".
- `Account` — combo populated from Accounts list. Required. Auto-fills server. Renders as the Account `Name`, not username.
- `Slot` — numeric (0=auto, 1-10). 0 = use heap-scan name match. Tooltip explaining the difference.
- `Class` — free-text or combo of EQ classes. Used for tooltips and tray label decoration only.
- `HK` (hotkey) — read-only; populated from `HotkeyConfig.AutoLoginN` slots. Click cell to jump to Hotkeys tab with the right row pre-selected.
- `Actions` — Edit (CharacterEditDialog), Delete (clears any hotkey binding referencing this character; warns if it's used in a Team).

### Edit dialogs

Both `AccountEditDialog` and `CharacterEditDialog` are modal `Form` subclasses using `DarkTheme.StyleForm` and the standard layout grid (`L=10, I=120, I2=310, BRW=370, R=28` per CLAUDE.md).

**AccountEditDialog fields:**
- Name (required, unique check)
- Username (required, unique within Accounts on `(Username, Server)`)
- Password (PasswordChar='*', stored DPAPI-encrypted via `CredentialManager`; Reveal toggle)
- Server (combo, default "Dalaya")
- UseLoginFlag (checkbox)
- [Test Login] button — launches a single bare client and types credentials, then exits or stops at charselect for visual confirmation. Optional, gate on existing Phase 1 working.
- [Save] [Cancel]

**CharacterEditDialog fields:**
- Name (required, unique within Characters)
- Account (combo, required)
- Slot (numeric 0-10, default 0=auto)
- Class hint (free-text, optional)
- Display label override (optional, defaults to Name)
- Notes (multiline, optional)
- [Save] [Cancel]

### Empty states

- No accounts: Accounts grid shows a single placeholder row "No accounts yet — click [+ Add Account] to add one." Characters section is collapsed/disabled with a hint "Add an account first."
- Accounts exist, no characters: Characters grid shows "No characters yet — characters added here will auto-enter-world when launched from the tray."

### Validation rules (run on Apply)

1. Account names unique.
2. Account `(Username, Server)` unique.
3. Character names unique.
4. Every Character's `AccountUsername`+`AccountServer` resolves to an existing Account.
5. Hotkeys bound to deleted Characters get the binding cleared with a one-line warning toast.
6. Team slots referencing deleted Characters/Accounts get cleared with warning (see Teams section).

### Staging pattern (must preserve)

The current Accounts tab uses the project-wide staging pattern — controls don't mutate `_config` directly; `ApplySettings()` ([line 1167](EQSwitch/UI/SettingsForm.cs:1167)) builds a new `AppConfig` from control state and the form raises `SettingsChanged`. The existing convention uses `_pending*` prefixed fields (see `_pendingAccounts`, `_pendingTeam1A..Team4B`, `_pendingTeam{N}AutoEnter` at [line 192-211](EQSwitch/UI/SettingsForm.cs:192)). The dual-section redesign **must not break this** and **must follow the existing naming convention**.

Internal staging buffers for v4:
- `_pendingAccounts: List<Account>` — replaces the current `_pendingAccounts: List<LoginAccount>`.
- `_pendingCharacters: List<Character>` — new.
- `_pendingTeam1A..Team4B: string` — unchanged.
- `_pendingTeam1AutoEnter..Team4AutoEnter: bool` — unchanged.

Both grids bind to the respective lists. `ApplySettings()` validates cross-section relationships, then atomically swaps both lists into the new config object. **Never write to one section's underlying list while the other is mid-edit.**

### Search/filter (defer)

Skip search/filter for v3.10.0. Add only if the user's character count crosses ~30 (current is 10).

### Tooltips

- Account row: tooltip shows `username@server`. Distinguishes accounts that share the same display Name.
- Character row: tooltip shows full account info + slot info ("Slot: auto" or "Slot: 3").
- Hotkey cell: tooltip shows the bound key combo + a hint to click to edit on Hotkeys tab.

---

## Teams — semantics + integration

Teams launch multiple clients in coordinated sequence. v3 data model: each `Team{N}Account{M}` string (N=1..4, M=1..2 — **teams have exactly 2 slots each, 8 total**) holds a username or charname; per-team `Team{N}AutoEnter` bool overrides whether the team forces Enter World regardless of per-account `AutoEnterWorld`.

### Verified call graph (2026-04-14)

Current flow, traced through code:

```
HotkeyConfig.TeamLogin{N} ("Ctrl+Alt+1")
  -> HotkeyManager fires "TeamLogin1"
  -> ExecuteTrayAction("LoginAll" | "LoginAll2" | "LoginAll3" | "LoginAll4")
  -> FireTeamLogin(slots[], teamName, teamAutoEnter = Team{N}AutoEnter)
  -> foreach slot: _ = ExecuteQuickLogin(user, name, teamAutoEnter)   // PARALLEL (fire-and-forget)
  -> _autoLoginManager.LoginAccount(account, teamAutoEnter)             // AutoLoginManager.cs:63
```

Key confirmations:
- **Current `LoginAccount` signature takes a second parameter:** `Task LoginAccount(LoginAccount account, bool? teamAutoEnter = null)`. The team-level flag overrides `account.AutoEnterWorld` when non-null. The plan's API split **must preserve this override semantic** — it's not a concept to delete.
- **Phantom-click defenses apply to team launches** — same `LoginAccount` entry point, same `gameState==5` gate in [mq2_bridge.cpp:1103/1141](EQSwitch/Native/mq2_bridge.cpp:1103) and `result==-2` handling in [AutoLoginManager.cs:417](EQSwitch/Core/AutoLoginManager.cs:417).
- **Launches are PARALLEL** — `FireTeamLogin` uses `_ = ExecuteQuickLogin(...)` (fire-and-forget). `AutoLoginManager._activeLoginPids` is keyed on PID so parallel launches don't clash by construction. Any change to sequential launches is a **timing regression risk** — flag before making it.

### New behavior (post-split)

Each `Team{N}Account{M}` slot resolves to **either a Character or an Account**, depending on what migrated:

- Character target → full `LoginAndEnterWorld(character, teamEnterWorldOverride)` path. The per-team flag is passed as an override: `true` forces enter world (even if somehow the Character's default was set to stop), `false` short-circuits to stop-at-charselect for that member, `null` means "use Character's default (enter world)".
- Account-only target → `LoginToCharselect(account, teamEnterWorldOverride)`. Same override semantics — `true` on an Account target logs a warning but proceeds to enter world via the legacy fallback (since Accounts don't encode a character name to select, this implies "stop at charselect then user drives" — same as `null`).

The `Team{N}AutoEnter` flags are **preserved in the v4 config** unchanged. They override the type-system default on a per-team basis.

### TeamLauncher API (post-split)

```csharp
public async Task FireTeam(int teamIndex)   // replaces FireTeamLogin; still parallel by default
{
    var slots = ResolveTeamSlots(teamIndex);   // List<TeamSlot>, length 2
    var overrideFlag = _config.GetTeamAutoEnter(teamIndex);  // bool? — null if unset, true/false from v3 flag

    foreach (var slot in slots)   // launch parallel — don't await
    {
        if (slot.Character != null)
            _ = _autoLoginManager.LoginAndEnterWorld(slot.Character, overrideFlag);
        else if (slot.Account != null)
            _ = _autoLoginManager.LoginToCharselect(slot.Account, overrideFlag);
        else
            FileLogger.Warn($"Team {teamIndex}: slot '{slot.RawName}' did not resolve");
    }
}

private record TeamSlot(string RawName, Character? Character, Account? Account);
```

**Preserves parallel fire-and-forget semantics** — timing matches v3. `ResolveTeamSlots` is a pure function over `(_config.Accounts, _config.Characters, team fields)`, tested separately.

### Hotkey teams

Team launch hotkeys (`HotkeyConfig.TeamLogin1-4`) — **no schema change**. Handler still calls the same underlying team-fire routine. Phantom-click gate preserved end-to-end.

### Stale-binding indicator

The Teams UI gets a per-slot resolution status. Lives in **both** `UI/AutoLoginTeamsDialog.cs` (the dedicated teams dialog) and the Accounts tab of SettingsForm where teams are edited inline:

- `OK` green: resolves to a Character (default: enter world).
- `WARN` yellow: resolves to an Account only (default: stop at charselect, unless `Team{N}AutoEnter=true` overrides).
- `FAIL` red: doesn't resolve to anything.

Status computed lazily when the dialog opens; refreshed on every Apply.

### Verification gate before Phase 6 closes

Manual: for each of the 4 teams, launch and confirm:
1. All populated slots launch their clients.
2. Each client ends up in the correct state (world vs charselect) per the `Team{N}AutoEnter` flag AND the target type.
3. No phantom clicks (`grep XWM_LCLICK eqswitch-dinput8.log | grep "after gameState=5"` empty).
4. No orphaned in-flight PIDs after completion (`AutoLoginManager._activeLoginPids` drains within 60s).

---

## Hotkey families — mirror the launch modes

The hotkey surface gets reorganized to mirror the three launch modes so each family of shortcut has one obvious meaning. No more "AutoLogin hotkey that does different things depending on which checkbox you ticked three months ago."

### Final hotkey family layout

| Family | Slots | Behavior | Example use |
|--------|------:|----------|-------------|
| **Action** | `LaunchOne`, `LaunchAll` | Bare client(s), no login | F12 launches a fresh patchme client |
| **Account** | `AccountHotkeys[0..N]` | `LoginToCharselect(account)` | Ctrl+1: log in to "Main", pick char manually |
| **Character** | `CharacterHotkeys[0..N]` | `LoginAndEnterWorld(character)` | Alt+1: log in + enter world as Backup |
| **Team** | `TeamLogin1..4` | `FireTeam(N)` — multi-client, `Team{N}AutoEnter` override | Ctrl+Alt+1: launch Team 1 |

`N` for Account and Character defaults to 4 each (matches today's slot count) but is plumbed as a list so growing later is a config-only change. Action names match the existing fields in `HotkeyConfig` — `LaunchOne` for single bare client, `LaunchAll` for multi-bare (was renamed from `LaunchTwo` in v2→v3 migration).

### Schema change

```csharp
public class HotkeyConfig
{
    // Action hotkeys (unchanged)
    public string LaunchOne { get; set; } = "";
    public string LaunchTwo { get; set; } = "";

    // Account family — login + stop at charselect
    public List<HotkeyBinding> AccountHotkeys { get; set; } = new();

    // Character family — login + enter world (single client)
    public List<HotkeyBinding> CharacterHotkeys { get; set; } = new();

    // Team family — multi-client, always enter world
    public string TeamLogin1 { get; set; } = "";
    public string TeamLogin2 { get; set; } = "";
    public string TeamLogin3 { get; set; } = "";
    public string TeamLogin4 { get; set; } = "";
}

public class HotkeyBinding
{
    public string Combo { get; set; } = "";   // e.g. "Alt+1"; empty = unbound
    public string TargetName { get; set; } = "";  // Account.Name or Character.Name
}
```

By having Account and Character hotkeys in **separate lists with separate types**, you can never accidentally fire enter-world on something that was meant to stop at charselect. The type system carries the intent; no runtime kind discriminator needed.

### Migration

See the full rule in the "Migration (v3 → v4) — Step 2" section at the top of this plan. Summary of the two-step lookup:

- v3 stores the key combo in `HotkeyConfig.AutoLoginN` (`"Alt+1"`) and the bound target (username or charname) in top-level `AppConfig.QuickLoginN` — two separate fields.
- Migration reads both, resolves `QuickLoginN` against the v3 `Accounts` list using the same CharacterName-first, Username-fallback order as [TrayManager.cs:1320](EQSwitch/UI/TrayManager.cs:1320), then routes:
  - CharacterName match + `AutoEnterWorld=true` → `CharacterHotkeys[N-1]`.
  - Otherwise → `AccountHotkeys[N-1]` with `TargetName = Account.Name`.
- Both v3 fields (`HotkeyConfig.AutoLogin1-4` and `AppConfig.QuickLogin1-4`) are preserved as `[Obsolete]` for one release; removed in v3.11.0.
- Each decision logged at INFO for user audit.

### SettingsForm Hotkeys tab

Restructure the tab into four collapsible sections matching the families above. Each section shows the bound key combo + target picker per row. Add/Remove buttons let the user grow Account/Character hotkey rows beyond the default 4.

Visual hierarchy:
```
[Action Hotkeys]
  Launch Client      [F12        ]
  Launch Two         [Ctrl+F12   ]

[Account Hotkeys]   (login + stop at charselect)
  1.  [Ctrl+1     ]  Account: [Main          ▼]   [✗]
  2.  [Ctrl+2     ]  Account: [Alt           ▼]   [✗]
  [+ Add]

[Character Hotkeys]   (login + enter world)
  1.  [Alt+1      ]  Character: [Backup (Cleric) ▼]   [✗]
  2.  [Alt+2      ]  Character: [Healpots (Cleric)▼]  [✗]
  3.  [Alt+3      ]  Character: [Acpots (Rogue)   ▼]  [✗]
  4.  [Alt+4      ]  Character: [Staxue (Ranger)  ▼]  [✗]
  [+ Add]

[Team Hotkeys]   (multi-client, always enter world)
  Team 1   [Ctrl+Alt+1]
  Team 2   [Ctrl+Alt+2]
  Team 3   [Ctrl+Alt+3]
  Team 4   [Ctrl+Alt+4]
```

- Per-row target combo is filtered to the matching family (Account-section combo only shows Accounts; Character-section only shows Characters). No way to mis-bind by category.
- Deleting a Character/Account that's bound to a hotkey: the row turns red with "Target deleted — rebind" on next load. Don't auto-clear silently.
- Conflict detection: same key combo across any two slots in any family is hard-blocked; same target across multiple slots within a family is allowed (user may want primary + alternate combos).

### Tray menu integration

Tray submenu items show their bound hotkey via the existing `HkSuffix` helper:
- Accounts ▸ Main ............. Ctrl+1
- Characters ▸ Backup (Cleric) . Alt+1

When iterating Accounts/Characters to build the submenus, look up the matching family's bindings by `TargetName` and append the suffix. O(N+M) lookup is fine — these lists are tiny.

### Hotkey handler dispatch

**Prerequisite — verify `HotkeyManager` public API before coding this.** Current `HotkeyManager.Register(string combo, Action callback, string name)` auto-assigns IDs internally. The 1000-1099 / 1100-1199 range scheme below requires either:
- A new overload `RegisterWithId(int id, ...)` exposing arbitrary-ID registration, **or**
- Registering by name only and letting the handler look up the binding by the `name` field at dispatch time (no ID ranges needed).

Pick the simpler path during Phase 5 implementation after reading `Core/HotkeyManager.cs`. The behavior below assumes the ID-range approach; adapt if the name-based approach wins.

The existing `HotkeyManager.WndProc` switch on hotkey ID gets new cases for the dynamic Account/Character slots. Use a stable ID range per family (e.g., 1000-1099 = AccountHotkeys, 1100-1199 = CharacterHotkeys) so registrations are addressable. Each handler resolves the bound target via the section's list, then dispatches:

```csharp
case int id when id >= 1000 && id < 1100:
{
    var binding = _config.Hotkeys.AccountHotkeys.ElementAtOrDefault(id - 1000);
    if (binding == null || string.IsNullOrEmpty(binding.TargetName)) break;
    var account = _config.Accounts.FirstOrDefault(a => a.Name == binding.TargetName);
    if (account != null) _ = _autoLoginManager.LoginToCharselect(account);
    break;
}

case int id when id >= 1100 && id < 1200:
{
    var binding = _config.Hotkeys.CharacterHotkeys.ElementAtOrDefault(id - 1100);
    if (binding == null || string.IsNullOrEmpty(binding.TargetName)) break;
    var character = _config.Characters.FirstOrDefault(c => c.Name == binding.TargetName);
    if (character != null) _ = _autoLoginManager.LoginAndEnterWorld(character);
    break;
}
```

(Each `case` wrapped in its own block scope — the two `binding` locals would otherwise collide under a shared case block.)

### Verification gate

For each family, press at least one hotkey while at desktop with no EQ running:
- Action: bare client launches, no login attempt.
- Account: EQ launches, types creds, stops at charselect, no Enter World click. Verify in `eqswitch-dinput8.log` that no `clicked CLW_EnterWorldButton` appears.
- Character: EQ launches, types creds, enters world. Verify with the live monitor on the DLL log.
- Team: all team members launch, all enter world.
- Press a Character hotkey while the user is already in-game: gameState=5 gate fires, log shows `dropped stale Enter World request`, no phantom click.

---

## Open questions — resolved decisions (override if disagreeing)

All items below marked **(proposed)** — Nate's call before Phase 3+ implementation. Defaults below reflect the current best reading of the code + audit findings.

**Phase 4 (SettingsForm):**
- Account dialog "Test login" button — **(proposed: defer to v3.10.1)** Useful but adds a new code path. Phase 1-6 ship without it; revisit if real use shows password-edit errors are common.
- Delete a Character with a Hotkey bound — **(proposed: three-button modal — "Clear binding", "Cancel", "Delete anyway and leave broken")** Third option is intentional so power users can delete + re-add under the same name without losing their hotkey.
- DisplayLabel override on Character — **(proposed: keep)** Low cost; useful when two characters share `Name` (not on Dalaya but common enough on servers with alts).

**Phase 3 (Tray menu):**
- Group Characters by Account when count > 10 — **(proposed: yes)** Auto-switch at threshold; sub-submenu per Account. Measure threshold in Phase 3 implementation; 10 is the starting hypothesis.
- Show class hint inline in the menu label — **(proposed: inline if `ClassHint` is set, tooltip-only otherwise)** Matches the mockup.
- Root "Launch Team" button — **(resolved: stays)** Fires Team 1 as one-click default. Full teams list lives in the new "Teams ▸" submenu. Both coexist; Team 1 is accessible from both.

**Phase 5 (Hotkeys):**
- Default slot count per family — **(proposed: 4 for Account, 4 for Character)** Matches today's `AutoLogin1-4` slot count. [+ Add] button grows the list. No hard cap.
- Same key combo across different families — **(proposed: hard-block)** Same combo always fires the same intent. `HotkeyManager` conflict-check runs across all families, not within each.
- Team launch with broken member — **(proposed: continue with logged warning)** User sees partial work and can fix config. Matches current fire-and-forget semantics.
- `Team{N}AutoEnter` override — **(resolved: preserved)** v3 flags carry forward unchanged. Teams can force enter world (true) or force stop-at-charselect (false) or use target-type default (null/default false).

**Phase 1 (Migration):**
- JSON key rename for existing `CharacterProfile` list — **(resolved: `characters` → `characterAliases`)** Migration moves the node. New `Character` launch-target list reclaims `characters`.
- v3 `HotkeyConfig.AutoLogin1-4` + `AppConfig.QuickLogin1-4` fields — **(resolved: preserve with `[Obsolete]` for v3.10.0, remove in v3.11.0)** One release of downgrade safety.

**Phase 2 (AutoLoginManager split):**
- `[Obsolete] LoginAccount(LoginAccount, bool?)` wrapper — **(resolved: keep through Phase 5)** Routes to the right new method based on `account.CharacterName` + `teamAutoEnter`. Removed in Phase 6 once all call sites migrate.
