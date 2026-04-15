# EQSwitch Phase 3.5 + Phase 4 — Hotkeys Polish + Settings Dual-Section UI

**Status:** Approved design, ready for implementation-plan drafting
**Created:** 2026-04-15
**Parent plan:** [`PLAN_account_character_split.md`](../../../PLAN_account_character_split.md)
**Prior phase:** Phase 3 shipped — HEAD `1d67e37`, 20 commits on main
**Handoff:** [`PLAN_account_character_split_HANDOFF_phase4.md`](../../../PLAN_account_character_split_HANDOFF_phase4.md)
**Prior spec:** [`2026-04-14-eqswitch-phase3-tray-rebuild-design.md`](2026-04-14-eqswitch-phase3-tray-rebuild-design.md)

## Purpose

Two-part release covering deferred polish from the Phase 3 smoke test followed by the Phase 4 Settings redesign that turns Characters into first-class editable entities.

1. **Phase 3.5** — ship 4 deferred items from Nate's Phase 3 smoke test as two atomic commits so Phase 4's own smoke test isn't gummed up by a hotkey-fires-mid-rebind bug.
2. **Phase 4** — rebuild `UI/SettingsForm.cs` Accounts tab into a dual-section Account/Character layout with two dedicated edit dialogs, a 3-button cascade-delete modal, cross-section validation, `AutoLoginTeamsDialog` v4 signature update with OK/WARN/FAIL slot indicators, and reverse-map-on-save to keep legacy v3 fields in sync.

The Settings dialog is EQSwitch's configuration surface — second-most-interacted-with after the tray menu. Phase 4 is where users discover what v3.10.0 can do.

## Goals

### Phase 3.5
- Global hotkeys must not fire while the user is typing into a Settings rebind field, including after hitting Apply inside the form.
- The "Actions & Launcher" card header mnemonic bug (ampersand rendered as underscore accelerator) is fixed.
- PiP toggle is bindable to a user-chosen global hotkey via a new Hotkeys-tab row.
- Duplicate-hotkey detection blocks Save with a modal listing each conflict and its bound actions.

### Phase 4
- User adds / edits / deletes Accounts as first-class entities with DPAPI-encrypted password storage via a dedicated modal dialog.
- User adds / edits / deletes Characters as first-class entities (independent of Accounts) with DisplayLabel + Notes editable and preserved across save/reload.
- Deleting an Account with linked Characters prompts a 3-button modal (Cancel / Unlink / Delete All). Each path is destructive-safe.
- `AutoLoginTeamsDialog` constructor accepts v4 `(List<Account>, List<Character>)` and shows per-slot OK / WARN / FAIL resolution indicators.
- Cross-section validation on Save blocks duplicate names, broken FK references, and hotkey conflicts with actionable modals.
- All Phase 3 user-visible surface (three-submenu tray, hotkey-column display, Teams binary AutoEnter flag) continues to work unchanged.

## Non-goals (deferred by scope)

- Hotkey family tables (`AccountHotkeys[]`, `CharacterHotkeys[]` registration + Hotkeys-tab UI) — **Phase 5**.
- Deletion of `LegacyAccounts` field + `Models/LoginAccount.cs` + `LoginAccountSplitter.cs` + `[Obsolete]` wrappers — **Phase 6**.
- Test Login button in `AccountEditDialog` (launches a bare client and types credentials for visual confirmation) — deferred to v3.10.1 polish.
- Bulk import from in-game character list — deferred. Users add Characters one at a time via the dialog.
- Search / filter in the dual grids — skip unless character count crosses ~30.
- Scroll-to-section parameter on `ShowSettings(2, section:"Characters")` — defer to Phase 4+ polish (tray "Manage Characters..." still routes to the Accounts tab top).

## Phase 3.5 design

### Commit A — `fix(hotkeys): suppress global dispatch while Settings is open`

**Root cause.** `ShowSettings` (TrayManager.cs:1317) correctly unregisters hotkeys on open and re-registers on FormClosed (:1327). But when the user hits **Apply** inside Settings, `ApplySettings` → `ConfigManager.Save` → `SettingsChanged` event → `TrayManager.ReloadConfig` → :1870-1872 unconditionally calls `UnregisterAll` + `RegisterHotkeys`. After re-register, hotkeys are live — the next key the user types into a rebind field (Alt+M, say) fires the global handler and launches Team 1.

**Fix.** Skip re-register in `ReloadConfig` when Settings is open. FormClosed already handles the re-register path on close.

```csharp
// TrayManager.cs:1869-1872 — REPLACE:
//     _hotkeyManager.UnregisterAll();
//     _keyboardHook.Reset();
//     RegisterHotkeys();
// WITH:
//
// Phase 3.5-A: when the Settings dialog calls ReloadConfig via Apply, global
// hotkeys must stay suspended until FormClosed re-registers them. Otherwise
// keystrokes into rebind fields (e.g., Alt+M for TeamLogin1) fire the old
// hotkeys mid-edit and launch EQ while the user is trying to rebind.
if (_settingsForm == null || _settingsForm.IsDisposed)
{
    _hotkeyManager.UnregisterAll();
    _keyboardHook.Reset();
    RegisterHotkeys();
}
```

**Belt-and-suspenders.** Gate `ExecuteTrayAction` on Settings state. Even if a registration race leaks through, dispatch is dead:

```csharp
// TrayManager.cs — top of ExecuteTrayAction(string action):
if (_settingsForm != null && !_settingsForm.IsDisposed)
{
    FileLogger.Info($"ExecuteTrayAction({action}): suppressed — Settings dialog is open");
    return;
}
```

The log line is diagnostic only — user sees nothing if an accidental hotkey fires.

**Stage:** `git add UI/TrayManager.cs`.

### Commit B — `fix(settings): hotkeys tab polish (label, PiP binding, conflict gate)`

Bundle of P3.5-B + P3.5-C + P3.5-D.

#### P3.5-B — Ampersand mnemonic rendering bug

**File:** `UI/SettingsForm.cs:584`.

WinForms `Label.Text` treats `&` as a mnemonic accelerator prefix — `"Actions & Launcher"` renders as "Actions _ Launcher" with the `L` underlined. The handoff's grep for `actions _launcher` was pattern-matching on the *rendered* UI output.

```csharp
// Line 584 — drop the ampersand, bump card height for the new P3.5-C row:
var cardActions = DarkTheme.MakeCard(page, "🏰", "Actions Launcher", DarkTheme.CardGold, 10, y, 480, 135);
//                                                   ^^^^^^^^^^^^^^^^                       ^^^
//                                       "Actions Launcher" (no &)       110 → 135 for new row
```

All downstream `y += 120` spacings shift by +25.

#### P3.5-C — TogglePip hotkey binding

**Schema** — `Config/AppConfig.cs.HotkeyConfig`:

```csharp
/// <summary>Toggle PiP overlay (show/hide). Blank = unbound.</summary>
public string TogglePip { get; set; } = "";
```

Defaults to `""` — legacy configs load cleanly, no migration version bump needed.

**Settings UI** — new row in the Actions Launcher card (SettingsForm.cs:583-601 region):

```csharp
cy += R + 2;
DarkTheme.AddCardLabel(cardActions, "PiP Toggle:", L, cy);
_txtTogglePip = MakeHotkeyBox(cardActions, I, cy - 2);
// Hint label's cy shifts to cy += R + 2 below this row.
```

Field declaration alongside other hotkey boxes: `private TextBox _txtTogglePip = null!;`. Wired through `LoadSettings`, `CaptureSettings`, and `BuildAppConfig` following the existing `_txtLaunchOne` pattern.

**Tray registration** — `TrayManager.RegisterHotkeys()` (search for the existing `AutoLogin1` registration pattern):

```csharp
if (!string.IsNullOrEmpty(_config.Hotkeys.TogglePip))
    _hotkeyManager.Register(_config.Hotkeys.TogglePip, () => ExecuteTrayAction("TogglePiP"));
```

The `"TogglePiP"` case in `ExecuteTrayAction` (TrayManager.cs:1390-1391) already routes to `TogglePip()`. Zero changes there.

**Tray menu label** — if a hotkey is bound, show its display string on the "PiP" context-menu item via `ShortcutKeyDisplayString` (consistent with Phase 3's Multi-Monitor Mode handling at `fde26a7`).

#### P3.5-D — Pre-save hotkey conflict detection

**Signature change.** `ApplySettings` returns `bool` instead of `void` — false = save blocked.

```csharp
// SettingsForm.cs:1167 — change signature:
private bool ApplySettings()
{
    // ... existing body returns true at the end ...
    return true;
}

// SettingsForm.cs:277-280 — update callers:
btnSave.Click  += (_, _) => { if (ApplySettings()) { ConfigManager.Save(_config); Close(); } };
btnApply.Click += (_, _) => { if (ApplySettings()) { ConfigManager.Save(_config); } };
```

**Conflict scan** — runs early in `ApplySettings`, before config mutation:

```csharp
var allHotkeys = new[]
{
    ("Fix Windows",      _txtArrangeWindows.Text.Trim()),
    ("Launch One",       _txtLaunchOne.Text.Trim()),
    ("Launch All",       _txtLaunchAll.Text.Trim()),
    ("Multi-Mon Toggle", _txtToggleMultiMon.Text.Trim()),
    ("PiP Toggle",       _txtTogglePip.Text.Trim()),
    ("AutoLogin 1",      _txtAutoLogin1Hotkey.Text.Trim()),
    ("AutoLogin 2",      _txtAutoLogin2Hotkey.Text.Trim()),
    ("AutoLogin 3",      _txtAutoLogin3Hotkey.Text.Trim()),
    ("AutoLogin 4",      _txtAutoLogin4Hotkey.Text.Trim()),
    ("Team Login 1",     _txtTeamLogin1Hotkey.Text.Trim()),
    ("Team Login 2",     _txtTeamLogin2Hotkey.Text.Trim()),
    ("Team Login 3",     _txtTeamLogin3Hotkey.Text.Trim()),
    ("Team Login 4",     _txtTeamLogin4Hotkey.Text.Trim()),
};

var conflicts = allHotkeys
    .Where(t => !string.IsNullOrEmpty(t.Item2))
    .GroupBy(t => t.Item2, StringComparer.OrdinalIgnoreCase)
    .Where(g => g.Count() > 1)
    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
    .ToList();

if (conflicts.Count > 0)
{
    var lines = conflicts.Select(g =>
        $"  {g.Key}  \u2192  {string.Join(", ", g.Select(t => t.Item1))}");
    var msg = "Cannot save — the same key combo is bound to multiple actions:\n\n"
            + string.Join("\n", lines)
            + "\n\nUnbind duplicates, then try again.";
    MessageBox.Show(msg, "Hotkey Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    return false;
}
```

`SwitchKey` and `DirectSwitchKeys` excluded — those are in-game context-sensitive single-key hooks (not `RegisterHotKey`-based global bindings), so they live in a different namespace. Scope stays tight.

**Stage:** `git add Config/AppConfig.cs UI/SettingsForm.cs UI/TrayManager.cs`.

## Phase 4 design

### Accounts tab — dual-section layout

Two vertically-stacked cards inside the existing Accounts TabPage. Both use `DarkTheme.MakeCard` with distinct accent colors (Accounts = blue, Characters = purple). Master plan line 442-462 mockup:

```
┌─ Accounts ─────────────────────────────────────────────────────┐
│  Name            Username        Server    Flag   Actions       │
│  Main            nate1           Dalaya     ✓     ✏  🗑         │
│  Alt             nate2           Dalaya     ✓     ✏  🗑         │
│                                                                  │
│  [ + Add Account ]   [ Import… ]   [ Export… ]                  │
├─────────────────────────────────────────────────────────────────┤
│  Characters                                                       │
│  Name           Account        Slot    HK    Actions             │
│  Backup         Main           auto    F1    ✏  🗑              │
│  Healpots       Main           auto    F2    ✏  🗑              │
│  Acpots         (unassigned)   auto    —     ✏  🗑              │
│                                                                  │
│  [ + Add Character ]                                             │
└─────────────────────────────────────────────────────────────────┘
```

**ClassHint dropped.** No reliable source for EQ class data (heap scan doesn't surface class). Field remains on `Models/Character.cs` (defaults to `""`) for forward-compat with future heap-scan extensions, but it's not displayed in the Characters grid, not editable in `CharacterEditDialog`, and not shown in tray tooltips unless populated by a hand-edited config. Legacy v3 configs with `classHint` populated during migration round-trip the value silently.

**Column widths (both grids ~470 inner width).**

Accounts grid: Name 110, Username 130, Server 90, Flag 50 (checkbox column), Actions 90 (two icon buttons).

Characters grid: Name 130, Account 130, Slot 60, HK 70, Actions 80.

Both grids: `AutoSizeColumnMode = Fixed`, `SelectionMode = FullRowSelect`, `MultiSelect = false`, `ReadOnly = true` (edits happen in the dialog modals, not in-cell). `DarkTheme.StyleDataGridView` is the styling entry point (existing helper).

**Test Login button.** Not present today and not added in Phase 4 — deferred to v3.10.1 polish (see Non-goals). The Accounts section button row in Phase 4 is: `[ + Add Account ]  [ Import… ]  [ Export… ]`.

### Staging fields

Replace `_pendingAccounts: List<LoginAccount>` (SettingsForm.cs:81) with two v4 fields:

```csharp
private List<Account> _pendingAccounts = new();
private List<Character> _pendingCharacters = new();
```

Load block (SettingsForm.cs:192 area):

```csharp
_pendingAccounts = _config.Accounts.Select(a => new Account
{
    Name = a.Name,
    Username = a.Username,
    EncryptedPassword = a.EncryptedPassword,
    Server = a.Server,
    UseLoginFlag = a.UseLoginFlag,
}).ToList();

_pendingCharacters = _config.Characters.Select(c => new Character
{
    Name = c.Name,
    AccountUsername = c.AccountUsername,
    AccountServer = c.AccountServer,
    CharacterSlot = c.CharacterSlot,
    DisplayLabel = c.DisplayLabel,
    ClassHint = c.ClassHint,   // round-tripped silently, no UI
    Notes = c.Notes,
}).ToList();
```

Deep-copy semantics — SettingsForm never mutates `_config` directly. `ApplySettings` atomically swaps the new lists into `_config` at Save.

### AccountEditDialog (new file `UI/AccountEditDialog.cs`)

Modal `Form` subclass, `DarkTheme.StyleForm`. Target ~180 lines.

**Fields:**

| Field | Control | Default | Validation |
|---|---|---|---|
| Name | TextBox | (empty for new; current for edit) | required, unique in Accounts list (Ordinal) |
| Username | TextBox | (empty for new; current, **read-only for edit**) | required, (Username, Server) unique |
| Password | TextBox w/ `PasswordChar='*'` | (blank for new; blank for edit w/ hint) | required for new; optional for edit (blank = keep) |
| Reveal | Button (eye icon) | `PasswordChar='*'` (hidden) | toggles to `'\0'` on click |
| Server | ComboBox w/ default entries | "Dalaya" | required |
| UseLoginFlag | CheckBox | `false` for new; current for edit | n/a |

**Buttons:** `[ Save ]  [ Cancel ]` — right-aligned at bottom.

**Layout:** project standard grid (L=10, I=120, I2=310, BRW=370, R=28 per CLAUDE.md). Form size ~400×280.

**Password handling.**
- **Edit mode:** field opens blank with hint text *"Leave blank to keep existing password."* If user types, re-encrypt via `CredentialManager.Encrypt` and update `EncryptedPassword`. If user leaves blank, the dialog's `Result.EncryptedPassword` stays as the current value.
- **New mode:** field blank, required. Encrypt on Save.
- **Reveal toggle:** `_txtPassword.PasswordChar = _revealOn ? '\0' : '*'`. Button text/icon swaps between "Show" and "Hide" (or 👁 / 🙈 if Segoe UI Emoji renders cleanly in the button face).

**Username read-only on edit.** Changing a username is semantically "delete this account and create a new one" — force the user to do that explicitly. TextBox `ReadOnly = true` + dim ForeColor when in edit mode.

**Return contract.** `DialogResult.OK` + public `Account Result` property. Caller reads `Result` and updates `_pendingAccounts`.

### CharacterEditDialog (new file `UI/CharacterEditDialog.cs`)

Modal `Form` subclass, `DarkTheme.StyleForm`. Target ~160 lines.

**Fields:**

| Field | Control | Default | Validation |
|---|---|---|---|
| Name | TextBox | (empty for new; current for edit) | required, unique in Characters list (Ordinal) |
| Account | ComboBox | first Account (required) | required, bound to `_pendingAccounts` by `DisplayMember=Name` |
| Slot | NumericUpDown 0-10 | 0 | 0 = auto (name-based heap scan), 1-10 = explicit |
| DisplayLabel | TextBox | (optional, empty) | n/a |
| Notes | TextBox Multiline | (optional, empty) | n/a |

**Buttons:** `[ Save ]  [ Cancel ]`

**Layout:** standard grid. Form size ~400×320 (taller than Account dialog for Notes multiline).

**Account combo binding.** `DataSource = new BindingSource(_pendingAccounts, null)`. `DisplayMember = nameof(Account.Name)`. On Save, `Account selected = (Account)_cboAccount.SelectedItem` — dialog stores `selected.Username` + `selected.Server` in `Result.AccountUsername` + `Result.AccountServer`. Preserves canonical casing (no case-drift risk from free-text).

**Empty-state guard.** If `_pendingAccounts.Count == 0` at dialog open, show hint *"Add an account first — Characters require an account to launch into."* and disable Save. Caller re-invokes "+ Add Account" from the outer SettingsForm workflow.

**Slot tooltip.** "0 = match by name (preferred, recommended). 1-10 = explicit slot index (fallback when two characters share a name)."

**Return contract.** `DialogResult.OK` + public `Character Result` property.

### CascadeDeleteDialog (new file `UI/CascadeDeleteDialog.cs`)

Custom modal — not `MessageBox` — because three distinct button semantics don't map cleanly onto `MessageBoxButtons.YesNoCancel` and button labels need to be explicit. `DarkTheme.StyleForm`, ~100 lines.

**When shown.** User clicks delete icon on an Accounts-grid row whose `Name` is referenced by at least one Character in `_pendingCharacters` (via `AccountUsername`+`AccountServer` FK match).

**Layout:**

```
┌─ Delete Account 'Main'? ────────────────────────────────────┐
│                                                              │
│  3 characters are linked to this account:                   │
│                                                              │
│    • Backup                                                  │
│    • Healpots                                                │
│    • Acpots                                                  │
│                                                              │
│  What should happen to them?                                 │
│                                                              │
│              ┌────────┐  ┌──────────┐  ┌──────────────┐      │
│              │ Cancel │  │  Unlink  │  │  Delete All  │      │
│              └────────┘  └──────────┘  └──────────────┘      │
│                                                              │
│  Unlinked characters keep their data but can't login         │
│  until you assign a new account via Edit.                    │
└──────────────────────────────────────────────────────────────┘
```

**Semantics:**

- **Cancel** → no-op, close modal. (Default focus + ESC.)
- **Unlink** → remove Account from `_pendingAccounts`; for each dependent Character, set `AccountUsername = ""` + `AccountServer = ""`. Characters stay in `_pendingCharacters`; the grid renders their Account column as `(unassigned)` in italic dim gray.
- **Delete All** → remove Account + cascade-remove all dependent Characters from `_pendingCharacters`.

**Visual affordances.**
- Cancel: `DarkTheme.MakeButton` neutral.
- Unlink: `DarkTheme.MakeButton` neutral (same weight as Cancel).
- Delete All: `DarkTheme.MakePrimaryButton` with red accent (`DarkTheme.CardWarn` or equivalent) — signals destructiveness.

**No-dependents path.** If the Account being deleted has zero dependents, skip the cascade dialog and show a simple `MessageBox.Show($"Delete Account '{name}'?", …, YesNo)` instead. Keeps the rare-path UX low-friction.

**Return contract.** `DialogResult` variant set by the caller via a public `CascadeDeleteChoice Choice { Cancel, Unlink, DeleteAll }` enum. Tested for clarity over DialogResult-style overloading.

### Cross-section validation on Save

Runs at the top of `ApplySettings` in this order (structural errors before cosmetic warnings):

1. **Hotkey conflicts** (P3.5-D, already specified above).
2. **Account names unique** — `_pendingAccounts.GroupBy(a => a.Name, Ordinal)` → any count > 1 blocks Save with a modal listing each duplicate name and how many rows share it.
3. **Account (Username, Server) unique** — same pattern; modal mentions both fields.
4. **Character names unique** — same pattern; modal mentions the conflicting character name.
5. **Orphan Character FK** — for each `_pendingCharacters` entry, skip characters with empty AccountKey (`AccountUsername == ""` — Unlink intentional state, legitimate). For non-empty, look up `_pendingAccounts.FirstOrDefault(a => a.Username == c.AccountUsername && a.Server == c.AccountServer)` — missing account blocks Save with *"Character '{name}' references missing account '{username}@{server}'. Edit to fix, or delete the character."*

Validation failure returns `false` from `ApplySettings` → Save/Apply button click does not write config.

### DisplayLabel / Notes preservation

Today (Phase 1-3): SettingsForm edits `_pendingAccounts: List<LoginAccount>`, then on Save calls `LoginAccountSplitter.Split` to derive v4 `Accounts` + `Characters`. The splitter only populates `Name`, `AccountUsername`, `AccountServer`, `CharacterSlot` on Characters — any user-edited `DisplayLabel` / `Notes` would be wiped.

**Phase 4 fix.** Invert the data flow. SettingsForm writes `_pendingCharacters` directly to `_config.Characters`. The splitter no longer runs in `ApplySettings` — it lives on only inside `ConfigVersionMigrator.MigrateV3ToV4` (JsonObject-level, migration-time) for legacy config upgrade.

**Reverse-map back to LegacyAccounts.** `_config.LegacyAccounts` must stay in sync with v4 state for downgrade safety + `AppConfig.Validate()` defense-in-depth cooperation (Phase 2.6 commit `a2e5f65`). The reverse-map is the inverse of `LoginAccountSplitter.Split`:

```csharp
// SettingsForm.cs — new private helper:
private static List<LoginAccount> ReverseMapToLegacy(
    IReadOnlyList<Account> accounts,
    IReadOnlyList<Character> characters)
{
    var result = new List<LoginAccount>();
    foreach (var a in accounts)
    {
        var linked = characters
            .Where(c => c.AccountUsername.Equals(a.Username, StringComparison.Ordinal) &&
                        c.AccountServer.Equals(a.Server, StringComparison.Ordinal))
            .ToList();

        if (linked.Count == 0)
        {
            // Account with no Characters → one bare LoginAccount row.
            result.Add(new LoginAccount
            {
                Name = a.Name,
                Username = a.Username,
                EncryptedPassword = a.EncryptedPassword,
                Server = a.Server,
                UseLoginFlag = a.UseLoginFlag,
                CharacterName = "",
                AutoEnterWorld = false,
                CharacterSlot = 0,
            });
        }
        else
        {
            // Account with N Characters → N LoginAccount rows, shared creds, distinct CharacterName.
            foreach (var c in linked)
            {
                result.Add(new LoginAccount
                {
                    Name = a.Name,
                    Username = a.Username,
                    EncryptedPassword = a.EncryptedPassword,
                    Server = a.Server,
                    UseLoginFlag = a.UseLoginFlag,
                    CharacterName = c.Name,
                    AutoEnterWorld = true,   // Characters always enter world by v4 contract
                    CharacterSlot = c.CharacterSlot,
                });
            }
        }
    }
    // Orphan Characters (Unlinked, AccountUsername == "") are deliberately dropped
    // from the legacy list — v3 has no concept of "character without account".
    // They remain in _config.Characters for Phase 4+ UI retrieval.
    return result;
}
```

**ApplySettings write order:**

```csharp
_config.Accounts      = _pendingAccounts.Select(a => /* deep copy */).ToList();
_config.Characters    = _pendingCharacters.Select(c => /* deep copy */).ToList();
_config.LegacyAccounts = ReverseMapToLegacy(_pendingAccounts, _pendingCharacters);
// CharacterAliases + Teams + Hotkeys + Pip + Video + etc. unchanged
```

**`Validate()` interaction.** Phase 2.6's defense-in-depth resyncs v4 lists from LegacyAccounts if LegacyAccounts changed. Our reverse-map keeps LegacyAccounts in sync with v4 lists on every Save → `Validate()` detects no drift → resync never triggers → Phase-4-only edits survive. Orphan Characters (no LegacyAccount equivalent) ride in `_config.Characters` only; `Validate()`'s resync can't see them but also doesn't overwrite them (resync builds v4 lists from LegacyAccounts, leaving `_config.Characters` unchanged when no drift is detected).

### AutoLoginTeamsDialog update

**Signature change.** Constructor currently takes `List<LoginAccount>` at `UI/AutoLoginTeamsDialog.cs:30-33`. Update to:

```csharp
public AutoLoginTeamsDialog(
    IReadOnlyList<Account> accounts,
    IReadOnlyList<Character> characters,
    AppConfig config)
```

Caller (SettingsForm) passes `_pendingAccounts`, `_pendingCharacters`, and `_config` (for Team{N}Account{M} field read/write + Team{N}AutoEnter checkboxes).

**Slot dropdown content.** Each of the 8 slot combos lists, in order:
1. `(none)` — empty slot option.
2. Characters first (preferred target), rendered as `"\uD83E\uDDD9  Backup  \u2192  enter world"` (🧙 Backup → enter world).
3. Accounts second (fallback target), rendered as `"\uD83D\uDD11  Main Account  \u2192  charselect only"` (🔑 Main Account → charselect only).

Slot dropdowns bind to a flattened, tagged list; the dialog internally tracks which entries are Character vs. Account for the resolution indicator.

**Per-slot resolution indicator.** A small colored Label beside each slot combo, text always visible:

- 🟢 OK — non-empty slot that resolves to a Character (enter world) OR an Account (charselect-only, matches legacy v3 behavior).
- 🟡 WARN — slot resolves to Account only when a Character with the same display name exists in the Characters list. Tooltip: *"Resolves to Account — stops at charselect. Pick the matching Character to enter world instead."*
- 🔴 FAIL — slot non-empty but doesn't resolve to any Account or Character. Tooltip: *"Doesn't match any Account or Character. Unbind or pick a valid target."*
- ⚪ empty — slot is `(none)`. No indicator.

Indicators recompute on every dropdown change via a single `RefreshSlotIndicators()` method.

**Team{N}AutoEnter checkboxes unchanged** — Phase 3 binary-flag semantics stay (`true` → force Enter World regardless of per-account intent; `false` → stop at charselect for every slot in the team).

### Same-name balloon nudge

Non-blocking toast raised via existing `TrayManager.ShowBalloon`. SettingsForm publishes a new event `OnSameNameCollision(string namesCsv)` that TrayManager subscribes + balloons.

```csharp
// At end of successful ApplySettings, before return true:
var collisions = _pendingAccounts
    .Where(a => _pendingCharacters.Any(c => c.Name.Equals(a.Name, StringComparison.Ordinal)))
    .Select(a => a.Name)
    .ToList();

if (collisions.Count > 0)
{
    var details = string.Join(", ", collisions);
    var hash = details.GetHashCode();   // dedup nudge on repeat saves
    if (hash != _lastNameCollisionHash)
    {
        _lastNameCollisionHash = hash;
        OnSameNameCollision?.Invoke(details);
    }
}
```

Balloon text: `"Account(s) '{names}' share names with Characters of the same name. Consider renaming for tray-menu clarity."`

Fires only when the collision set *changes* across saves — Nate's current config has `natedogg`/`flotte`/`acpots` collisions; nudging on every Save would spam.

## File delta summary

| File | Change | Approx. lines |
|---|---|---|
| `Config/AppConfig.cs` | Add `HotkeyConfig.TogglePip` | +3 |
| `UI/TrayManager.cs` | ReloadConfig guard + ExecuteTrayAction gate + TogglePip register + menu label + SameName event subscribe | +~20 |
| `UI/SettingsForm.cs` | P3.5-B/C/D + Accounts tab redesign + ApplySettings rewrite + ReverseMapToLegacy helper + SameName event | +~450 net |
| `UI/AccountEditDialog.cs` | **NEW** | ~180 |
| `UI/CharacterEditDialog.cs` | **NEW** | ~160 |
| `UI/CascadeDeleteDialog.cs` | **NEW** | ~100 |
| `UI/AutoLoginTeamsDialog.cs` | v4 signature + slot content + OK/WARN/FAIL indicators | +~80 |
| `Models/Character.cs` | (no change) | 0 |
| `Config/LoginAccountSplitter.cs` | (no change — still used by migrator) | 0 |

Net: ~+1000 lines across 3 new files + 4 modified, 0 deletions. Phase 6 will delete `LoginAccountSplitter.cs` + `Models/LoginAccount.cs` + `_config.LegacyAccounts`, reclaiming ~200 lines.

## Implementation sequence

Each bullet = one atomic commit.

**Phase 3.5 (ship first):**
1. `fix(hotkeys): suppress global dispatch while Settings is open` — Commit A.
2. `fix(settings): hotkeys tab polish (label, PiP binding, conflict gate)` — Commit B.

**Phase 4 (post-3.5):**
3. `refactor(settings): staging fields to v4 Account/Character lists` — swap `_pendingAccounts: List<LoginAccount>` → `List<Account>` + new `List<Character>`, update load path. No UI change yet; single grid still renders from `_pendingAccounts` transitionally. Build green.
4. `feat(settings): AccountEditDialog modal` — new file + unit-testable-ish constructor.
5. `feat(settings): CharacterEditDialog modal` — new file.
6. `feat(settings): CascadeDeleteDialog modal` — new file.
7. `feat(settings): Accounts tab dual-section layout` — the big UI commit. Two DataGridViews, row-click handlers, cascade-delete wiring, hotkey-column lookup.
8. `feat(settings): cross-section validation + reverse-map on save` — `ApplySettings` rewrite with validation ordering + `ReverseMapToLegacy` helper.
9. `feat(teams): AutoLoginTeamsDialog v4 signature + resolution indicators` — constructor swap, OK/WARN/FAIL pills, combo content refresh.
10. `feat(settings): same-name balloon nudge on save` — collision detection + TrayManager subscription.

**Review + ship:**
11. Dispatch three review agents in parallel: `pr-review-toolkit:code-reviewer`, `pr-review-toolkit:silent-failure-hunter`, `feature-dev:code-reviewer`.
12. One or more `fix(...)` commits folding agent findings.
13. `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` → deploy to `C:/Users/nate/proggy/Everquest/EQSwitch/` (kill running processes first per handoff line 212).
14. Nate-driven smoke test per verification gate below.
15. Memory-file update (`project_eqswitch_v3_10_0_account_split.md`).
16. **STOP** for Phase 5 sign-off.

## Verification gate

### Phase 3.5 gate (after Commits A + B)

**Build + fixtures:**
- `dotnet build` → 0 errors, exactly 1 `[Obsolete]` warning at TrayManager.cs:1613 (ExecuteQuickLogin dead code, Phase 6 deletes).
- `bash _tests/migration/run_fixtures.sh` → 9 passed.

**Phantom-click non-regression:**
- `grep -c "gameState == 5" Native/mq2_bridge.cpp` → 2.
- `grep -c "result == -2" Core/AutoLoginManager.cs` → 1.

**Hotkey-dispatch mid-rebind test (manual):**
- Open Settings → Hotkeys tab. Focus Team 1 rebind field. Press Alt+M. Assert: no balloon, no EQ launch.
- Apply (stay in form). Focus Team 1 field again. Press Alt+M. Assert: still no launch.
- Close form. Press Alt+M. Assert: Team 1 fires normally.

**Label rendering:**
- Settings → Hotkeys tab → "Actions Launcher" header renders with no underline on any character.

**PiP hotkey:**
- Bind `Alt+P` to PiP Toggle. Save. In-game: press Alt+P. Assert: PiP overlay toggles.
- Tray menu shows "Toggle PiP  Alt+P" on the PiP item.

**Conflict detection:**
- Bind same `Alt+P` to two rows (AutoLogin 1 + PiP Toggle). Click Save. Assert: modal blocks Save with both action names listed.

### Phase 4 gate (after Phase 4 commits)

**Build + fixtures + phantom-click:** same as above.

**Round-trip CRUD per entity type:**
- Add an Account. Save. Reload Settings. Assert: Account in grid. Reopen Edit dialog; password field blank, hint shown. Type new password. Save. Verify login still works in-game.
- Edit Account Name. Save. Reload. Tray Accounts submenu shows new name.
- Add a Character bound to the new Account. Set DisplayLabel = "MyMain", Notes = "healer". Save. Reload. Reopen Character in Edit dialog. Assert: DisplayLabel = "MyMain", Notes = "healer".

**Cascade-delete modal paths:**
- Delete an Account with 0 dependent Characters. Simple YesNo modal shown; Yes deletes.
- Delete an Account with ≥1 Characters. 3-button modal shown. Test each button:
  - **Cancel** → modal closes, grid unchanged.
  - **Unlink** → Account gone, Characters remain, Account column shows `(unassigned)` italic dim.
  - **Delete All** → Account + Characters both gone.

**Cross-section validation blocks:**
- Rename an Account to match another's Name. Save → modal blocks with duplicate-name message.
- Manually edit a Character's AccountUsername to a value that no Account has. Save → modal blocks with orphan message.

**AutoLoginTeamsDialog indicators:**
- Team 1 Slot 1 = Character name (valid) → 🟢.
- Team 1 Slot 2 = Account-only (no Character of that name) → 🟡.
- Team 2 Slot 1 = unresolved string → 🔴.

**Tray regression check (Phase 3 smoke test):**
- Three submenus render: 🔑 Accounts, 🧙 Characters, 👥 Teams.
- Account-submenu click → stops at charselect.
- Character-submenu click → enters world.
- Team-submenu click → parallel slot fire.
- `eqswitch-dinput8.log` clean of phantom-click entries after each.

**Same-name nudge:**
- Save config with current `natedogg`/`flotte`/`acpots` collisions. Assert: balloon fires once per changed collision set.

## Agent fanout

Three agents run in parallel *after* all Phase 4 implementation commits land locally, before publish:

| Agent | Focus |
|---|---|
| **`pr-review-toolkit:code-reviewer`** | CLAUDE.md conventions, DarkTheme-only color usage, P/Invoke safety, async patterns, conventional commits, naming |
| **`pr-review-toolkit:silent-failure-hunter`** | Cascade-delete edge cases, DPAPI error paths (null `EncryptedPassword`, key rotation), combo-select-nothing on empty lists, ApplySettings validation ordering, hotkey conflict modal dismissability, Username-read-only bypass paths |
| **`feature-dev:code-reviewer`** | Second opinion on Settings UX + data invariants (reverse-map correctness, orphan legitimacy, Validate() interaction, same-name nudge dedup) |

Each prompt is self-contained, cites this spec by path, and lists specific files + commit SHAs. Findings fold into one or more follow-up commits before publish.

## Open questions / resolved decisions

All resolved during brainstorming on 2026-04-15:

- ✅ Cascade-delete: 3-button Cancel / Unlink / Delete All — **orphan-preserving**.
- ✅ Password reveal default: **OFF** (`PasswordChar='*'`, toggle to reveal).
- ✅ ClassHint: **dropped from UI** (no reliable source for EQ class data). Field retained on model for forward-compat; legacy `classHint` values round-trip silently.
- ✅ AutoLoginTeamsDialog: **full Phase 4 scope** — v4 signature change + OK/WARN/FAIL indicators.
- ✅ Tab reorder: none. Accounts stays at index 2. All three tray "Manage X..." entries keep routing to `ShowSettings(2)`.
- ✅ Same-name warning: non-blocking balloon, hash-deduped per save set (Nate's current config triggers it on first Phase 4 save; silent thereafter).
- ✅ Search / filter in grids: deferred (character count < 30).
- ✅ Bulk import from char list: deferred (one-at-a-time via dialog).
- ✅ Test Login button in AccountEditDialog: deferred to v3.10.1.

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Reverse-map loses Orphan Characters (Unlink state) on downgrade | Low (downgrade is a pre-release-only contingency) | Documented as intentional. Orphans live in `_config.Characters` only — downgrade to v3 drops them; acceptable since v3 has no concept of orphan Characters. |
| `ApplySettings` signature change breaks callers | Low | Only two callers (btnSave, btnApply at lines 277-280). Both updated in the same commit. Compiler catches any missed. |
| `CharacterEditDialog` Account combo loses canonical casing | Low | FK fields stored as `selectedAccount.Username` / `selectedAccount.Server` directly, not combo text. No drift possible. |
| Cascade-delete wording feels punitive | Low | User tested: Nate approved Cancel / Unlink / Delete All as distinct, escalating actions. |
| `Validate()` resync wipes Phase-4 edits if reverse-map drifts | Low | Round-trip invariant asserted via manual test (load → edit → save → reload → edit → save → no JSON diff on second save). Agent review focus area. |
| Same-name balloon spams every save | Mitigated | Hash-dedup on collision set (only fires when set changes). |
| New dialog files drift from DarkTheme factories | Low | Agent review checks for `Color.FromArgb()` outside DarkTheme.cs (existing CLAUDE.md rule). |
| `AutoLoginTeamsDialog` v4 signature break forces callers to update | Low | Only one caller (SettingsForm opens the dialog). Updated in the Teams dialog commit. |

## Rollback

All Phase 3.5 + Phase 4 commits touch only `Config/AppConfig.cs`, `UI/*.cs`, and new `UI/*EditDialog.cs` / `CascadeDeleteDialog.cs` files. No native-DLL changes, no migration schema changes, no config-format version bumps. `git revert` of any commit is safe; data in `_config.Accounts` / `_config.Characters` persists even if the Phase 4 UI is reverted — the next Settings open would read the v4 data but present it via whatever UI is current at that revision.

The Phase 2.6 `AppConfig.Validate()` defense-in-depth stays as a safety net: if a future revert leaves the config in an inconsistent state, Validate() resyncs v4 lists from LegacyAccounts on the next load.

---

**End of design. Next step: `superpowers:writing-plans` produces the task-level implementation plan from this spec. Phase 3.5 commits land first; Phase 4 commits land after the implementation plan is written.**
