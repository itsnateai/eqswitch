# PLAN — Account/Character Split + Tray Menu Restructure

**Status:** Design / not started
**Created:** 2026-04-14
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
- Config schema v3 → v4 migration that splits the existing `Accounts: List<LoginAccount>` into `Accounts: List<Account>` + `Characters: List<Character>` (the existing `Characters: List<CharacterProfile>` is a different lightweight type — see "Naming collision" below).
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

`Config/AppConfig.cs:474` already defines `CharacterProfile` (`Name`, `Class`, `Notes`, `SlotIndex`). This is used for tooltip/window-title decoration only — NOT autologin. The new `Character` model is distinct and richer. Two paths to resolve:

- **A.** Rename existing `CharacterProfile` → `CharacterAlias` (or `CharacterTag`), since "profile" was always a misnomer for what is essentially a label-mapping. Then the new entity gets the clean `Character` name.
- **B.** Reuse `CharacterProfile`, extending it with `AccountRef` + DPAPI-related fields. Single source of truth for "named character" but mixes display metadata with launch behavior.

**Recommendation: A.** Cleaner separation, no field-bloat on a type that's currently consumed by tray-tooltip code.

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

// AppConfig changes
public List<Account> Accounts { get; set; } = new();         // RENAMED type, same property name
public List<Character> Characters { get; set; } = new();     // RENAMED type (was CharacterProfile)
```

---

## Migration (v3 → v4)

Runs in `ConfigVersionMigrator` BEFORE deserialization so JSON changes are transparent.

For each old `LoginAccount` entry:

1. **Account dedup:** Compute key `(Username, Server)`. If no `Account` with that key yet, create one (carry `Name`, `EncryptedPassword`, `UseLoginFlag`).
2. **Character creation:** If `CharacterName` is non-empty, create a `Character` referencing the Account by `(Username, Server)`. Carry `CharacterSlot`. If the old entry had `AutoEnterWorld=true`, the conversion is intent-preserving by construction (Characters always enter world). If `AutoEnterWorld=false`, the old behavior was "log in and stop at charselect" — that's now exposed as the Account itself; no Character needed for that intent. Decision: still create the Character so it's visible in the Characters menu, since it's a known character. The user can delete it if they only want it as an Account.
3. **Hotkey rebinding:** For each populated v3 `AutoLoginN` slot:
   - Source LoginAccount had `AutoEnterWorld=true` AND a non-empty `CharacterName` → bind to **CharacterHotkeys[i-1]** (intent: enter world).
   - Otherwise → bind to **AccountHotkeys[i-1]** (intent: stop at charselect).
   - Old `AutoLoginN` strings retained as `[Obsolete]` shims for one release (downgrade safety); removed in v3.11.0. Each migration decision logged at INFO so user can audit.
4. **Team field rebinding:** `Team{N}Account{M}` strings currently store LoginAccount names. After the split, re-resolve each:
   - Prefer Character (if a Character exists with the same name) → team member enters world.
   - Fall back to Account (if no Character) → team member stops at charselect, with a logged warning that this changes prior behavior.
   - Total resolution failures get logged and the team slot is left blank.

See "Hotkey families" and "Teams — verification + integration" sections for the full destination-side behavior.

**Migration test fixtures:** seed a v3 `eqswitch-config.json` with three patterns — (a) one account, one character; (b) one account, three characters; (c) account with no character (rare/edge). All three round-trip without data loss.

---

## Tray menu — new structure

```
⚔  EQ Switch v3.10.0  ⚔
─────────────────────────
⚔  Launch Client                        Hotkey
🎮  Launch Team                          Hotkey
─────────────────────────
🔑  Accounts ▸                            (login + stop at charselect)
    👤  Main Account
    👤  Alt Account
    ⚙  Manage Accounts...
🧙  Characters ▸                          (login + enter world)
    🧙  Backup       (Cleric)
    🧙  Healpots     (Cleric)
    🧙  Acpots       (Rogue)
    🧙  Staxue       (Ranger)
    ─────────────
    ⚙  Manage Characters...
👥  Teams ▸                               (existing — multi-client)
    🚀  Auto-Login Team 1 (default)
    🚀  Auto-Login Team 2
    ...
─────────────────────────
[existing Clients submenu, Settings, Help, Exit]
```

Visual rules:
- Account icon `🔑` / Character icon `🧙` — distinct enough at glance that there's zero ambiguity about which mode you're in.
- Optional class hint in Character row label `(Cleric)` / `(Rogue)` — pulled from `Character.ClassHint`. If empty, just the name.
- Account rows show the account `Name` (user-friendly), not the username, with a tooltip showing username+server for disambiguation when multiple accounts share a label.

---

## File-by-file change list

### Models
- **NEW** `Models/Account.cs` — schema above.
- **NEW** `Models/Character.cs` — schema above.
- **NEW** `Models/HotkeyBinding.cs` — `{ Combo, TargetName }` row used by Account/Character hotkey families.
- **DELETE / MIGRATE** `Models/LoginAccount.cs` — kept as `LoginAccountV3` in a `_legacy/` sibling for migration only, then removed in a follow-up release.

### Config
- `Config/AppConfig.cs`:
  - Replace `List<LoginAccount> Accounts` with `List<Account> Accounts`.
  - Rename existing `CharacterProfile` → `CharacterAlias` (or extract to its own file).
  - Add `List<Character> Characters` (the new launch-target type).
  - Bump `ConfigVersion` to 4.
  - Update the v3.9.0 `AutoEnterWorld` migration block to be a no-op for v4 configs.
- `Config/ConfigMigration.cs` — add `MigrateV3ToV4` doing the dedup described above.

### Core
- `Core/AutoLoginManager.cs`:
  - Split `LoginAccount(LoginAccount)` into two public methods:
    - `LoginToCharselect(Account account)` — types credentials, waits for charselect, returns. Does NOT bump `enterWorldReq`.
    - `LoginAndEnterWorld(Character character)` — resolves Account from `(AccountUsername, AccountServer)`, runs full chain ending with the Enter World click.
  - Internal helper `RunLoginSequence(Account, Character?, EnterWorldMode mode)` does the heavy lifting; the two public methods are thin wrappers.
  - Preserve all the phantom-click defenses we just landed (gameState=5 gate, result=-2 handling, drag-aware rect tracking).

### UI
- `UI/TrayManager.cs`:
  - Rewrite `BuildContextMenu()` Accounts section into the three-submenu structure shown above.
  - New helper methods: `BuildAccountsSubmenu()`, `BuildCharactersSubmenu()`. (Teams logic stays as-is, just moved into its own submenu node.)
  - `Manage Accounts...` and `Manage Characters...` both jump to `ShowSettings(accountsTabIndex)` but pre-select the right inner section (see SettingsForm changes below).
- `UI/SettingsForm.cs`:
  - Accounts tab redesign: header "Accounts" + DataGridView of Account rows (Name, Username, Server, [Edit] [Delete]); below it, header "Characters" + DataGridView of Character rows (Name, Account FK shown as label, Slot, ClassHint, [Edit] [Delete]). Both sections styled identically using existing `DarkTheme` factories.
  - Add/Edit dialogs: separate `AccountEditDialog` and `CharacterEditDialog`. Both modal, both use `DarkTheme.MakeButton`/`AddNumeric`/`AddComboBox`. Character dialog has an Account picker (combo of existing accounts; cannot save without one).
  - On save, dedup logic enforces unique `(Username, Server)` per Account and unique `Name` per Character.

### Native
- **No changes required.** SHM contract is unchanged. Heap-scan name lookup still works — the `Character.Name` matches the in-game name that the heap scan reports, so `CharSelectReader.RequestSelectionByName` gets exactly what it needs. This is by design — the split is a UI/data-model concern only.

### Hotkeys
- `Config/AppConfig.cs.HotkeyConfig`: families restructured per "Hotkey families" section above. `AccountHotkeys: List<HotkeyBinding>` and `CharacterHotkeys: List<HotkeyBinding>` replace the v3 `AutoLoginN` strings. Action hotkeys (`LaunchOne`, `LaunchTwo`) and Team hotkeys (`TeamLogin1-4`) keep their names + semantics.
- `HotkeyManager`: register dynamic ID ranges (1000-1099 Account, 1100-1199 Character) so growing the lists doesn't require code changes.
- `HotkeyManager.WndProc`: dispatch by ID range (per "Hotkey handler dispatch" pseudo-code in the families section).
- Migration: each populated v3 `AutoLoginN` → AccountHotkey or CharacterHotkey based on the source LoginAccount's `AutoEnterWorld` + `CharacterName`.

### Teams
- Field names stay (`Team1Account1`, etc.) for config compat. Internally treat each string as a Character name lookup; if it resolves to an Account-only entry (no Character), call `LoginToCharselect(account)` instead.

---

## Nuances to watch

1. **Concurrent-edit safety in SettingsForm.** Today the Accounts tab uses the staging pattern (controls don't mutate `_config` directly; `ApplySettings()` rebuilds from controls). The new dual-section layout MUST preserve this — staging writes to a working `(List<Account>, List<Character>)` pair, and only the final Apply commits. Bug-prone area: a Character that references an Account the user just deleted. Validation must run on Apply, not on Save, so the user can stage cross-section edits.
2. **DPAPI scope unchanged.** `EncryptedPassword` migration is a copy operation — same DPAPI scope (CurrentUser), same machine. No re-encrypt needed. But verify the round-trip explicitly in a migration test — silent corruption here would brick autologin for every account.
3. **AutoLoginManager.LoginAccount(LoginAccount)** is currently called from at least: tray menu, hotkey handler, team-launch coordinator. All call sites must update. Search-and-verify; don't trust grep alone.
4. **Single-instance + lifecycle.** AutoLoginManager tracks "in-flight" PIDs to prevent double-fire. The split must not break that — the `_inFlightPids` set is keyed on PID, not account, so it's safe by construction, but worth a manual trace.
5. **WaitForScreenTransition behavior is unchanged for both modes.** It returns once charselect is rendered. `LoginToCharselect` returns at that point. `LoginAndEnterWorld` continues with the Enter World request. The flow split happens after WaitForScreenTransition, before the EnterWorld block.
6. **What if a Character's slot is no longer valid?** (User recreated a character on a different slot.) The heap-scan name lookup makes slot mostly redundant — `RequestSelectionByName` finds the correct row by name regardless of slot index. Keep `Character.CharacterSlot` for legacy/fallback only and prefer name-based selection.
7. **Tray menu rebuild cost.** `BuildContextMenu()` runs on every config reload. With 20+ characters the menu becomes long. Consider grouping characters by Account in the submenu (`Account A ▸ Char1, Char2; Account B ▸ Char3`) when there are >10 entries. Decide in implementation phase.
8. **Memory ownership.** `ToolStripMenuItem` and `Font` instances must be disposed when the menu rebuilds. Existing code does `_contextMenu?.Dispose()` — verify that disposing a strip also disposes its items (it does, but cite the doc).
9. **Hotkey conflict detection.** SettingsForm has conflict-warning logic for hotkeys. If a Character is deleted while a hotkey was bound to it, the hotkey row needs a "broken reference" indicator. Don't silently drop the binding.
10. **Per-account Affinity overrides.** `AffinityConfig` may key on account. If so, the migration must rebind those keys to either Account or Character (whichever the affinity setting was logically per).

---

## Build sequence (suggested)

1. **Phase 1 — Models + migration** (no UI). Add `Account.cs`, `Character.cs`. Bump version. Implement `MigrateV3ToV4`. Add unit tests for migration with the three fixtures. CI green; existing UI code still compiles by keeping `LoginAccount.cs` as a thin compat shim that delegates to `Account` + lookups.
2. **Phase 2 — AutoLoginManager API split.** Add `LoginToCharselect(Account)` and `LoginAndEnterWorld(Character)`. Keep `LoginAccount(LoginAccount)` as `[Obsolete]` wrapper that resolves to one or the other. Update no call sites yet.
3. **Phase 3 — Tray menu.** Rebuild `BuildContextMenu()` to use the new submenu structure, calling the new AutoLoginManager methods. Verify all three launch paths via manual smoke test.
4. **Phase 4 — SettingsForm dual-section UI.** Build Account/Character editors. Wire up dedup + validation.
5. **Phase 5 — Hotkey families + Team rebinding.** Implement `AccountHotkeys`/`CharacterHotkeys` lists, register dynamic ID ranges, dispatch by family. Migrate v3 `AutoLoginN` slots per the rule above. Verify Team{N}Account{M} round-trips and resolves to the right Character (or Account) per migration logic. Phantom-click defenses unchanged but re-verified end-to-end with hotkey-fired launches.
6. **Phase 6 — Cleanup.** Remove `[Obsolete]` `LoginAccount(LoginAccount)`. Delete `LoginAccount.cs` (or move to `_legacy/`). Bump to v3.10.0. Tag release.

---

## Verification gates

Each phase must pass before the next starts:

- **Phase 1:** v3 fixture loads, splits cleanly, round-trips through save → reload → save without diff. DPAPI password decrypts after migration.
- **Phase 2:** Existing autologin still works (regression check via the existing per-account flow).
- **Phase 3:** Manual: each of the three launch modes from tray. EQ launches, Account-mode stops at charselect, Character-mode enters world. No phantom clicks (verify `eqswitch-dinput8.log` has no `XWM_LCLICK` after `gameState -> 5`).
- **Phase 4:** Add/edit/delete an Account and a Character via UI. Save + reload preserves edits. Cross-section validation: deleting an Account that has Characters warns the user and offers to delete dependents or cancel.
- **Phase 5:** Each family's hotkeys verified per the "Verification gate" in the Hotkey families section: Action launches bare, Account stops at charselect, Character enters world, Team enters world for all members. Phantom-click gate (`gameState=5`) survives a hotkey-fired in-game press without firing. Conflict warnings fire when expected.
- **Phase 6:** Code search for `LoginAccount` returns zero hits in non-legacy code. Build + publish single-file EXE. Deploy to `proggy/`.

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

The current Accounts tab uses the project-wide staging pattern — controls don't mutate `_config` directly; `ApplySettings()` builds a new `AppConfig` from control state and the form raises `SettingsChanged`. The dual-section redesign **must not break this**. Internal staging buffers: `_stagedAccounts: List<Account>` and `_stagedCharacters: List<Character>`. Both grids bind to these. `ApplySettings()` validates cross-section relationships, then atomically swaps both lists into the new config object. **Never write to one section's underlying list while the other is mid-edit.**

### Search/filter (defer)

Skip search/filter for v3.10.0. Add only if the user's character count crosses ~30 (current is 10).

### Tooltips

- Account row: tooltip shows `username@server`. Distinguishes accounts that share the same display Name.
- Character row: tooltip shows full account info + slot info ("Slot: auto" or "Slot: 3").
- Hotkey cell: tooltip shows the bound key combo + a hint to click to edit on Hotkeys tab.

---

## Teams — verification + integration

Teams launch multiple clients in coordinated sequence. Currently each `Team{N}Account{M}` field is a string referencing a `LoginAccount.Name`. Per-account `AutoEnterWorld` determines whether each client enters world or stops at charselect.

### Verification needed (do this BEFORE writing Phase 1)

Open `Core/AutoLoginManager.cs` and the team-launch coordinator. Confirm:

1. **Today's behavior:** when "Auto-Login Team N" fires, does it call `LoginAccount(loginAccount)` for each member, and does the result respect each member's `AutoEnterWorld` flag? Or is there a team-level override?
2. **The phantom-click defenses introduced in `4bc13c9` and `ca66aee` (`gameState==5` gate, `result==-2` handling) — do they apply to team launches?** They should, because team launch goes through the same `AutoLoginManager.LoginAccount` entry point. Verify the call graph.
3. **Concurrency:** are team members launched in parallel or serially? If parallel, do they share state in `AutoLoginManager`? Check `_inFlightPids` and any `static` state.

If verification reveals teams DON'T respect AutoEnterWorld today, document the regression risk and design accordingly.

### New behavior (post-split)

**Teams always enter world** — this is now a property of the type system, not a flag. Each `Team{N}Account{M}` slot resolves to a **Character** (because Characters always enter world by definition). If a Team slot references an entry that only resolves to an Account (no Character bound), the launch warns and falls through to "log in and stop at charselect" rather than failing — user can fix the team config to point to a Character.

### Migration of Team{N}Account{M} fields

Current strings can resolve to either an Account name or a Character name (because LoginAccount conflates them). Migration logic:

1. For each team-slot string, look up matching `LoginAccount.Name` in v3 data.
2. After migration, that LoginAccount is now an Account + (optionally) a Character.
3. **Prefer Character.** If a Character exists with the same name as the old LoginAccount.Name, rebind the team slot to that Character name.
4. **Fall back to Account.** If no Character matches, rebind to the Account name. Log a warning that this team member will stop at charselect (since Account-mode = stop).
5. Round-trip through save → reload preserves the new value.

### TeamLauncher API changes

`AutoLoginManager` (or wherever team launch lives) currently iterates `Team{N}Account{M}` strings and calls `LoginAccount(LoginAccount)` for each. After the split:

```csharp
public async Task LaunchTeam(int teamIndex)
{
    var slots = ResolveTeamSlots(teamIndex);  // returns List<TeamSlot>
    foreach (var slot in slots)
    {
        if (slot.Character != null)
            await LoginAndEnterWorld(slot.Character);
        else if (slot.Account != null)
            await LoginToCharselect(slot.Account);
        else
            FileLogger.Warn($"Team {teamIndex}: slot '{slot.RawName}' did not resolve");
        // existing inter-launch delay preserved
    }
}

private record TeamSlot(string RawName, Character? Character, Account? Account);
```

`ResolveTeamSlots` does the lookup logic; tested separately.

### Hotkey teams

Team launch hotkeys (`TeamLogin1-4`) — no schema change. Their handler now calls `LaunchTeam(N)` which routes to the new path. Verify no static state leaks between team launches (e.g., `_isTeamLaunching` flag must reset on completion or exception).

### Stale-binding indicator in SettingsForm

The Teams tab (or wherever team slots are edited) gets a per-slot resolution status:
- ✓ green: resolves to a Character
- ⚠ yellow: resolves to an Account only (will stop at charselect)
- ✗ red: doesn't resolve (will be skipped)

This makes broken team configs visible without launching them.

### Verification gate before Phase 6 closes

Manual: launch each existing team, confirm all members enter world correctly, no phantom clicks, no orphaned in-flight PIDs.

---

## Hotkey families — mirror the launch modes

The hotkey surface gets reorganized to mirror the three launch modes so each family of shortcut has one obvious meaning. No more "AutoLogin hotkey that does different things depending on which checkbox you ticked three months ago."

### Final hotkey family layout

| Family | Slots | Behavior | Example use |
|--------|------:|----------|-------------|
| **Action** | `LaunchOne`, `LaunchTwo` | Bare client(s), no login | F12 launches a fresh patchme client |
| **Account** | `AccountHotkey1..N` | `LoginToCharselect(account)` | Ctrl+1: log in to "Main", pick char manually |
| **Character** | `CharacterHotkey1..N` | `LoginAndEnterWorld(character)` | Alt+1: log in + enter world as Backup |
| **Team** | `TeamHotkey1..4` | `LaunchTeam(N)` — multi-client, all enter world | Ctrl+Alt+1: launch Team 1 |

`N` for Account and Character defaults to 4 each (matches today's slot count) but is plumbed as a list so growing later is a config-only change.

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

The v3 `AutoLoginN` fields stored a single string referencing a LoginAccount name. After the split that LoginAccount becomes an Account + (maybe) a Character. Migration:

1. For each populated v3 `AutoLoginN` slot (1-4):
   - If the corresponding LoginAccount had `AutoEnterWorld=true` AND a non-empty `CharacterName`: bind to **CharacterHotkeys[i-1]** (intent: enter world). The user's old behavior was "press hotkey, character ends up in world."
   - Otherwise: bind to **AccountHotkeys[i-1]** (intent: stop at charselect). Old behavior was "press hotkey, end up at charselect."
2. Log each migration decision so the user can audit. Output a one-time INFO line at app start the first time the v4 config loads ("AutoLogin1 migrated to AccountHotkey1 → 'Main' (was: stop at charselect)").
3. The v3 `AutoLogin1-4` fields are left in the JSON for one release as `[Obsolete]` shims so a downgrade-then-upgrade doesn't lose data. Removed in v3.11.0.

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

The existing `HotkeyManager.WndProc` switch on hotkey ID gets new cases for the dynamic Account/Character slots. Use a stable ID range per family (e.g., 1000-1099 = AccountHotkeys, 1100-1199 = CharacterHotkeys) so registrations are addressable. Each handler resolves the bound target via the section's list, then dispatches:

```csharp
case int id when id >= 1000 && id < 1100:
    var binding = _config.Hotkeys.AccountHotkeys.ElementAtOrDefault(id - 1000);
    if (binding == null || string.IsNullOrEmpty(binding.TargetName)) break;
    var account = _config.Accounts.FirstOrDefault(a => a.Name == binding.TargetName);
    if (account != null) _ = _autoLoginManager.LoginToCharselect(account);
    break;

case int id when id >= 1100 && id < 1200:
    var binding = _config.Hotkeys.CharacterHotkeys.ElementAtOrDefault(id - 1100);
    if (binding == null || string.IsNullOrEmpty(binding.TargetName)) break;
    var character = _config.Characters.FirstOrDefault(c => c.Name == binding.TargetName);
    if (character != null) _ = _autoLoginManager.LoginAndEnterWorld(character);
    break;
```

### Verification gate

For each family, press at least one hotkey while at desktop with no EQ running:
- Action: bare client launches, no login attempt.
- Account: EQ launches, types creds, stops at charselect, no Enter World click. Verify in `eqswitch-dinput8.log` that no `clicked CLW_EnterWorldButton` appears.
- Character: EQ launches, types creds, enters world. Verify with the live monitor on the DLL log.
- Team: all team members launch, all enter world.
- Press a Character hotkey while the user is already in-game: gameState=5 gate fires, log shows `dropped stale Enter World request`, no phantom click.

---

## Open questions for Nate

Each phase's design choice — flag your call before the next session implements the wrong shape.

**Phase 4 (SettingsForm):**
- Account dialog: expose "Test login" button (fires type-credentials flow without entering world)? Useful for verifying password before saving. Recommendation: yes, as a Phase 4.5 follow-up.
- Delete a Character with a Hotkey bound: prompt to clear, reassign, or cancel? Recommendation: prompt with three buttons.
- DisplayLabel override on Character — keep or drop? Recommendation: keep, low cost, used by power users with similar character names.

**Phase 3 (Tray menu):**
- Group Characters by Account in the submenu when count > 10? Recommendation: yes, auto-switch at threshold; sub-submenu per Account.
- Show class hint inline in the menu label `Backup (Cleric)` or only in tooltip? Recommendation: inline if `ClassHint` is set, tooltip-only otherwise.

**Phase 5 (Hotkeys):**
- Default slot count per family — 4, 8, or unlimited from day one? Recommendation: 4 (matches today), with [+ Add] button to grow.
- Allow the same key combo across different families (e.g., Alt+1 = Account hotkey AND Character hotkey)? Recommendation: hard-block — same combo always fires the same intent.
- For team launch: stop the team if any member's target doesn't resolve, or skip the broken member and continue? Recommendation: continue with logged warning; user sees the team did partial work and can fix config.
