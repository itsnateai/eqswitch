# EQSwitch Phase 3.5 + Phase 4 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Phase 3.5 hotkeys-tab polish (2 atomic commits) then Phase 4 Settings dual-section UI (8 atomic commits + 1-2 review-findings commits + publish + smoke test).

**Architecture:** Phase 3.5 blocks global hotkey dispatch while Settings is open and adds pre-save hotkey-conflict detection. Phase 4 inverts the SettingsForm Accounts-tab data flow — staging fields become `List<Account>` + `List<Character>` edited directly, with reverse-map-to-LegacyAccounts on Save for downgrade safety and `AppConfig.Validate()` defense-in-depth cooperation. Three new modal dialogs (`AccountEditDialog`, `CharacterEditDialog`, `CascadeDeleteDialog`) replace grid-embedded editing. `AutoLoginTeamsDialog` gains a v4 constructor signature and per-slot OK/WARN/FAIL resolution indicators.

**Tech Stack:** C# 12 / .NET 8 WinForms, existing `DarkTheme` factory system (no new color usage), System.Text.Json via `ConfigManager`, DPAPI via `CredentialManager`, existing `bash _tests/migration/run_fixtures.sh` harness for config-layer verification.

**Spec:** [`docs/superpowers/specs/2026-04-15-eqswitch-phase4-settings-dual-section-design.md`](../specs/2026-04-15-eqswitch-phase4-settings-dual-section-design.md)

**Parent plan:** [`PLAN_account_character_split.md`](../../../PLAN_account_character_split.md)

**Prior state:** HEAD `2ff4762` (spec commit) / `1d67e37` (last Phase 3 implementation commit). 20 impl commits on main. Build green: 0 errors, 1 expected `[Obsolete]` warning at `TrayManager.cs:1613`. 9 migration fixtures pass.

---

## File structure

| File | Role | Change |
|---|---|---|
| `Config/AppConfig.cs` | Root config + HotkeyConfig | +3 lines — `HotkeyConfig.TogglePip` field (Task 2) |
| `UI/TrayManager.cs` | Tray orchestration | +~30 lines — ReloadConfig guard, ExecuteTrayAction gate, TogglePip register, PiP menu-item hotkey label, same-name balloon subscribe (Tasks 1, 2, 10) |
| `UI/SettingsForm.cs` | 6-tab settings GUI | +~460 lines net — Phase 3.5-B/C/D polish + Accounts tab redesign + ApplySettings rewrite (Tasks 2, 3, 7, 8, 10) |
| `UI/AccountEditDialog.cs` | **NEW** — Account edit modal | ~180 lines (Task 4) |
| `UI/CharacterEditDialog.cs` | **NEW** — Character edit modal | ~160 lines (Task 5) |
| `UI/CascadeDeleteDialog.cs` | **NEW** — 3-button cascade modal | ~100 lines (Task 6) |
| `UI/AutoLoginTeamsDialog.cs` | Team slot editor | +~100 lines — v4 signature + indicators (Task 9) |
| `Models/Character.cs` | Launch-target type | 0 (DisplayLabel/ClassHint/Notes already present from Phase 1) |
| `Config/LoginAccountSplitter.cs` | v3→v4 splitter | 0 (still used by migrator only) |

Native DLLs, `AutoLoginManager`, `ExecuteQuickLogin`: no changes (Phase 5+ territory).

---

## Conventions

- Every edit shows exact line numbers OR a unique anchor string from the current code.
- C# emoji uses `\u`-escape surrogate pairs (e.g., `"\uD83D\uDD11"` for 🔑) matching the existing `TrayManager.cs` pattern at lines 794 / 802 / 807.
- Every commit stages specific files (never `git add -A`).
- Conventional-commit titles under 72 chars. Body under 72-char columns.
- Expected build state after each task: `0 Error(s)`, exactly 1 `[Obsolete]` warning at `TrayManager.cs:1613`. Any deviation is a bug — stop and investigate.
- Every commit footer ends with `Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>`.
- `ApplySettings` returns `bool` starting in Task 2 — `false` blocks Save. All call sites at `SettingsForm.cs:277-280` updated together.
- Test commands run from `X:/_Projects/EQSwitch/`.
- No changes to `gameState == 5` (Native/mq2_bridge.cpp) or `result == -2` (Core/AutoLoginManager.cs) — phantom-click gates stay intact and are re-grep-verified after every commit.

---

## Phase 3.5 — ship first

### Task 1: Commit A — ReloadConfig guard + ExecuteTrayAction gate

**Files:**
- Modify: `UI/TrayManager.cs:1869-1872` (ReloadConfig re-register block)
- Modify: `UI/TrayManager.cs` (top of `ExecuteTrayAction`)

- [ ] **Step 1.1: Locate ExecuteTrayAction opening line**

Run: `grep -n "private void ExecuteTrayAction" UI/TrayManager.cs`
Expected: one match, line number around 1236. Note the exact number (will be used in Step 1.3).

- [ ] **Step 1.2: Edit ReloadConfig re-register block**

Open `UI/TrayManager.cs` around line 1869. Find this exact block:

```csharp
        // Re-register hotkeys if they changed
        _hotkeyManager.UnregisterAll();
        _keyboardHook.Reset();
        RegisterHotkeys();
```

Replace with:

```csharp
        // Re-register hotkeys if they changed.
        // Phase 3.5-A: when Settings calls ReloadConfig via Apply, global hotkeys
        // must stay suspended until FormClosed re-registers. Otherwise keystrokes
        // into rebind fields fire the old hotkeys mid-edit.
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _hotkeyManager.UnregisterAll();
            _keyboardHook.Reset();
            RegisterHotkeys();
        }
```

- [ ] **Step 1.3: Edit ExecuteTrayAction opener**

At the `ExecuteTrayAction` opening line (from Step 1.1), insert the gate after the opening brace. The method currently looks like:

```csharp
    private void ExecuteTrayAction(string action)
    {
        switch (action)
        {
            ...
```

Change to:

```csharp
    private void ExecuteTrayAction(string action)
    {
        // Phase 3.5-A: no tray dispatch while Settings dialog is open. Defense-in-depth
        // against any ReloadConfig-style race that leaves hotkeys registered.
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            FileLogger.Info($"ExecuteTrayAction({action}): suppressed — Settings dialog is open");
            return;
        }

        switch (action)
        {
            ...
```

- [ ] **Step 1.4: Build and verify warning count unchanged**

Run:
```bash
cd X:/_Projects/EQSwitch && dotnet build 2>&1 | tail -20
```

Expected output contains `0 Error(s)`. Warning count exactly 1: `[CS0618] … 'AutoLoginManager.LoginAccount(LoginAccount, bool?)' is obsolete …` at `TrayManager.cs(1613, …)`. No other warnings.

- [ ] **Step 1.5: Verify phantom-click gates intact**

Run:
```bash
grep -c "gameState == 5" Native/mq2_bridge.cpp
grep -c "result == -2" Core/AutoLoginManager.cs
```

Expected: `2` and `1` respectively.

- [ ] **Step 1.6: Verify migration fixtures still pass**

Run: `bash _tests/migration/run_fixtures.sh`
Expected final line: `Migration fixtures: 9 passed, 0 failed`

- [ ] **Step 1.7: Commit**

Run:
```bash
git add UI/TrayManager.cs
git commit -m "$(cat <<'EOF'
fix(hotkeys): suppress global dispatch while Settings is open

ReloadConfig (called via Apply inside Settings) unconditionally re-
registered global hotkeys while Settings was still open. Next key
into a rebind field fired the old binding — typing Alt+M to rebind
Team 1 launched Team 1 mid-edit. Blocked rebinding any bound hotkey
without closing Settings first.

Fix: skip re-register in ReloadConfig when _settingsForm is alive.
FormClosed handler still re-registers on close.

Belt-and-suspenders: ExecuteTrayAction returns early with a diagnostic
log line if Settings is open, so even a registration race can't reach
a dispatch.

Resolves P3.5-A from HANDOFF_phase4.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

Expected: `[main <sha>] fix(hotkeys): suppress global dispatch while Settings is open`

---

### Task 2: Commit B — Hotkeys tab polish (label + PiP hotkey + conflict detection)

**Files:**
- Modify: `Config/AppConfig.cs` (HotkeyConfig — add `TogglePip` field)
- Modify: `UI/SettingsForm.cs`:
  - Line 584 — drop `&` in "Actions & Launcher", bump card height `110` → `135`
  - Class-field region — add `_txtTogglePip`
  - Actions card body — add PiP Toggle row
  - `LoadSettings` / `CaptureSettings` / `BuildAppConfig` paths — wire TogglePip
  - Line 1167 — change `ApplySettings` return type to `bool`; add conflict scan
  - Lines 277-280 — update btnSave / btnApply callers
- Modify: `UI/TrayManager.cs`:
  - `RegisterHotkeys` — register `_config.Hotkeys.TogglePip`
  - `BuildContextMenu` — if TogglePip bound, set `ShortcutKeyDisplayString` on the PiP menu item

- [ ] **Step 2.1: Add TogglePip field to HotkeyConfig**

Open `Config/AppConfig.cs`. Find the `public class HotkeyConfig` block (search for `MultiMonitorEnabled` — the TogglePip field goes right above it, after `CharacterHotkeys` and its comment). Insert:

```csharp
    /// <summary>Toggle PiP overlay (show/hide). Blank = unbound.</summary>
    public string TogglePip { get; set; } = "";

```

Keep the blank line between this and `MultiMonitorEnabled`.

- [ ] **Step 2.2: Add `_txtTogglePip` field declaration in SettingsForm**

Open `UI/SettingsForm.cs`. Search for `_txtLaunchOne` field declaration (it's near the top, around line 50-80). Add `_txtTogglePip` declaration in the same block:

```csharp
    private TextBox _txtTogglePip = null!;
```

Place it immediately after `_txtLaunchOne` so diff is minimal.

- [ ] **Step 2.3: Drop ampersand in Actions card header + bump card height**

At `UI/SettingsForm.cs:584`, find:

```csharp
        var cardActions = DarkTheme.MakeCard(page, "🏰", "Actions & Launcher", DarkTheme.CardGold, 10, y, 480, 110);
```

Replace with:

```csharp
        var cardActions = DarkTheme.MakeCard(page, "🏰", "Actions Launcher", DarkTheme.CardGold, 10, y, 480, 135);
```

Two changes: header text `"Actions & Launcher"` → `"Actions Launcher"` (drops `&` mnemonic), height `110` → `135` for new P3.5-C row.

- [ ] **Step 2.4: Add PiP Toggle row inside Actions card**

Find the existing row additions (around lines 588-598):

```csharp
        DarkTheme.AddCardLabel(cardActions, "Fix Windows:", L, cy);
        _txtArrangeWindows = MakeHotkeyBox(cardActions, I, cy - 2);
        DarkTheme.AddCardLabel(cardActions, "Launch One:", col2, cy);
        _txtLaunchOne = MakeHotkeyBox(cardActions, col2I, cy - 2);
        cy += R + 2;

        DarkTheme.AddCardLabel(cardActions, "Multi-Mon Toggle:", L, cy);
        _txtToggleMultiMon = MakeHotkeyBox(cardActions, I, cy - 2);
        DarkTheme.AddCardLabel(cardActions, "Launch All:", col2, cy);
        _txtLaunchAll = MakeHotkeyBox(cardActions, col2I, cy - 2);
        cy += R + 2;

        DarkTheme.AddCardHint(cardActions, "Press key combo to capture. Leave blank to disable. Backspace/Delete to clear.", L, cy);
```

Insert a new row between Multi-Mon Toggle / Launch All and the hint line. Result:

```csharp
        DarkTheme.AddCardLabel(cardActions, "Fix Windows:", L, cy);
        _txtArrangeWindows = MakeHotkeyBox(cardActions, I, cy - 2);
        DarkTheme.AddCardLabel(cardActions, "Launch One:", col2, cy);
        _txtLaunchOne = MakeHotkeyBox(cardActions, col2I, cy - 2);
        cy += R + 2;

        DarkTheme.AddCardLabel(cardActions, "Multi-Mon Toggle:", L, cy);
        _txtToggleMultiMon = MakeHotkeyBox(cardActions, I, cy - 2);
        DarkTheme.AddCardLabel(cardActions, "Launch All:", col2, cy);
        _txtLaunchAll = MakeHotkeyBox(cardActions, col2I, cy - 2);
        cy += R + 2;

        DarkTheme.AddCardLabel(cardActions, "PiP Toggle:", L, cy);
        _txtTogglePip = MakeHotkeyBox(cardActions, I, cy - 2);
        cy += R + 2;

        DarkTheme.AddCardHint(cardActions, "Press key combo to capture. Leave blank to disable. Backspace/Delete to clear.", L, cy);
```

Also: the `y += 120` line that follows (before the next card) must become `y += 145` to account for the extra row + card height bump. Find the next `y += 120` after `cardActions` setup (before the `slotsCard` setup on line ~604-605) and change it to `y += 145`.

- [ ] **Step 2.5: Wire TogglePip text load + save**

Search for the pattern where `_txtLaunchOne.Text` is assigned from `_config.Hotkeys.LaunchOne`. In the `LoadSettings` / constructor-body section of `BuildHotkeysTab` (near line 660-670), after the existing hotkey-box text assignments, add:

```csharp
        _txtTogglePip.Text = _config.Hotkeys.TogglePip ?? "";
```

Find the comparable save path in `ApplySettings` (near line 1200 — search `Hotkeys.LaunchOne = `). After the existing assignments add:

```csharp
            _config.Hotkeys.TogglePip = _txtTogglePip.Text.Trim();
```

- [ ] **Step 2.6: Change ApplySettings signature to bool + add conflict scan**

At `UI/SettingsForm.cs:1167`, find:

```csharp
    private void ApplySettings()
    {
```

Replace with:

```csharp
    private bool ApplySettings()
    {
        // Phase 3.5-D: hotkey conflict detection — same key combo bound to
        // multiple actions causes RegisterHotKey to silently fail on the
        // second registration. Block Save with an actionable modal.
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

Find the end of `ApplySettings` (before its closing `}`). Add `return true;` on the line before the closing brace. The end of the method should now look like:

```csharp
            ...
            // (existing tail of ApplySettings)

            return true;
        }
```

- [ ] **Step 2.7: Update btnSave / btnApply callers**

At `UI/SettingsForm.cs:277-280`, find:

```csharp
            btnSave.Click += (_, _) => { ApplySettings(); ConfigManager.Save(_config); Close(); };
            btnApply.Click += (_, _) => { ApplySettings(); ConfigManager.Save(_config); };
```

Replace with:

```csharp
            btnSave.Click += (_, _) => { if (ApplySettings()) { ConfigManager.Save(_config); Close(); } };
            btnApply.Click += (_, _) => { if (ApplySettings()) { ConfigManager.Save(_config); } };
```

- [ ] **Step 2.8: Register TogglePip hotkey in TrayManager**

Open `UI/TrayManager.cs`. Search for `RegisterHotkeys` method body (line ~356). Find the block that registers `LaunchOne`:

```csharp
        if (!string.IsNullOrEmpty(_config.Hotkeys.LaunchOne))
            _hotkeyManager.Register(_config.Hotkeys.LaunchOne, () => ExecuteTrayAction("LaunchOne"));
```

Below the existing registrations for `LaunchOne` / `LaunchAll` / `ArrangeWindows` / `ToggleMultiMonitor`, add:

```csharp
        if (!string.IsNullOrEmpty(_config.Hotkeys.TogglePip))
            _hotkeyManager.Register(_config.Hotkeys.TogglePip, () => ExecuteTrayAction("TogglePiP"));
```

- [ ] **Step 2.9: Wire TogglePip hotkey display on PiP menu item**

In `UI/TrayManager.cs`, search for the PiP context-menu item creation (grep `"Toggle PiP"` or `TogglePip();` caller at around line 1003). Find the current line:

```csharp
            _contextMenu.Items.Add("Toggle PiP", null, (_, _) => TogglePip());
```

Replace with:

```csharp
            var pipItem = new ToolStripMenuItem("Toggle PiP", null, (_, _) => TogglePip());
            if (!string.IsNullOrEmpty(_config.Hotkeys.TogglePip))
                pipItem.ShortcutKeyDisplayString = _config.Hotkeys.TogglePip;
            _contextMenu.Items.Add(pipItem);
```

If the exact pattern doesn't match (some context-menu items are added differently), apply the same transformation (create `ToolStripMenuItem`, set `ShortcutKeyDisplayString`, add to `_contextMenu.Items`) while preserving the click handler.

- [ ] **Step 2.10: Build and verify**

Run:
```bash
dotnet build 2>&1 | tail -20
```

Expected: `0 Error(s)`, 1 `[Obsolete]` warning at TrayManager.cs:1613. No new warnings.

Run:
```bash
grep -c "gameState == 5" Native/mq2_bridge.cpp  # expect 2
grep -c "result == -2" Core/AutoLoginManager.cs  # expect 1
bash _tests/migration/run_fixtures.sh  # expect "9 passed, 0 failed"
```

- [ ] **Step 2.11: Commit**

Run:
```bash
git add Config/AppConfig.cs UI/SettingsForm.cs UI/TrayManager.cs
git commit -m "$(cat <<'EOF'
fix(settings): hotkeys tab polish (label, PiP binding, conflict gate)

Three deferred items from Phase 3 smoke test:

- P3.5-B: 'Actions & Launcher' card header rendered 'Actions _ Launcher'
  because WinForms Label treats & as a mnemonic accelerator. Drop the
  ampersand.
- P3.5-C: new HotkeyConfig.TogglePip field + Hotkeys tab row. Dispatch
  routes through existing 'TogglePiP' case in ExecuteTrayAction; context
  menu's PiP item shows the bound combo via ShortcutKeyDisplayString.
- P3.5-D: ApplySettings returns bool. Pre-save scan groups 13 hotkey
  fields, blocks Save with a modal listing each conflict's bound actions
  when any duplicate is detected. Windows' RegisterHotKey silently fails
  on duplicates; this catches the problem at config-time instead.

Card height 110 -> 135 for the new PiP row; downstream y offsets shift.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

Expected: `[main <sha>] fix(settings): hotkeys tab polish (label, PiP binding, conflict gate)`

---

## Phase 4 — Settings dual-section UI (post-3.5)

### Task 3: Staging fields refactor — v4 Account/Character lists

Transitional commit. Renames `_pendingAccounts: List<LoginAccount>` to `List<Account>` and adds `_pendingCharacters: List<Character>`. The single Accounts-tab grid still renders from legacy data for now (via reverse-map); dual-section layout arrives in Task 7. Build must stay green.

**Files:**
- Modify: `UI/SettingsForm.cs`:
  - Field declarations (line 81 area)
  - Load block (line 192 area)
  - `ApplySettings` — keep LegacyAccounts synced via reverse-map; v4 lists replace the splitter output
  - Temporary: the existing Accounts-tab renderer keeps working by reverse-mapping `_pendingAccounts` + `_pendingCharacters` into a `List<LoginAccount>` at render time

**Key invariant:** nothing visible to the user changes in this task. Build green. Fixtures green. Phantom-click gates unchanged. Tray menu unchanged.

- [ ] **Step 3.1: Add ReverseMapToLegacy helper to SettingsForm**

Open `UI/SettingsForm.cs`. At the bottom of the class (before the closing `}`), add:

```csharp
    // Phase 4: v4 Accounts + Characters are the source of truth. LegacyAccounts
    // kept in sync via reverse-map for downgrade safety and Validate() cooperation.
    // Orphan Characters (AccountUsername == "") are deliberately dropped —
    // v3 has no concept of "character without account".
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
                        AutoEnterWorld = true,
                        CharacterSlot = c.CharacterSlot,
                    });
                }
            }
        }
        return result;
    }
```

- [ ] **Step 3.2: Change staging-field declarations**

At `UI/SettingsForm.cs:81`, find:

```csharp
    private List<LoginAccount> _pendingAccounts = new();
```

Replace with:

```csharp
    private List<Account> _pendingAccounts = new();
    private List<Character> _pendingCharacters = new();
```

- [ ] **Step 3.3: Update load block to populate v4 lists directly**

Find the load block at `UI/SettingsForm.cs:192` (the `.Select(a => new LoginAccount { ... }).ToList()` assignment). Replace with:

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
            ClassHint = c.ClassHint,
            Notes = c.Notes,
        }).ToList();
```

- [ ] **Step 3.4: Temporary shim — expose a legacy view for existing Accounts-tab renderer**

The existing `BuildAccountsTab` code (lines 1319-1600) still reads `_pendingAccounts` as `LoginAccount`. Add a read-only property to bridge:

```csharp
    // Temporary bridge (removed in Task 7). Reverse-maps v4 pending state into the
    // legacy view the existing grid code expects. Writes to _pendingAccounts via
    // AddAccountFromDialog go through AccountEditDialog once Task 4 lands.
    private List<LoginAccount> _legacyView => ReverseMapToLegacy(_pendingAccounts, _pendingCharacters);
```

Find every read-site where the existing `BuildAccountsTab` code references `_pendingAccounts` expecting `LoginAccount` semantics. Replace those reads (not writes — those get rewritten in Task 7) with `_legacyView`.

**Find/replace sweep for reads:**
- Any occurrence of `_pendingAccounts.FirstOrDefault(a => a.CharacterName ==` → `_legacyView.FirstOrDefault(a => a.CharacterName ==`
- Any occurrence of `_pendingAccounts.FirstOrDefault(a => a.Username ==` → stays (Username read matches v4 `Account` shape too)
- Any occurrence of `_pendingAccounts.Select(a => ... a.CharacterName ...)` → `_legacyView.Select(...)`
- Grid row enumeration reading `CharacterName` / `AutoEnterWorld` → `_legacyView`.

**Write sites** (list manipulation: `.Add()`, `.RemoveAt()`, `.FindIndex()` with intent to mutate) temporarily become:

```csharp
// OLD: _pendingAccounts.Add(newLoginAccount);
// NEW (transitional):
var legacyList = _legacyView;
legacyList.Add(newLoginAccount);
var (a4, c4) = Config.LoginAccountSplitter.Split(legacyList);
_pendingAccounts = a4;
_pendingCharacters = c4;
```

This transitional code is ugly on purpose — Task 7 deletes all of it and replaces with direct v4 edits via the new dialogs. Mark each transitional block with `// TRANSITIONAL — removed in Task 7 (dual-section rewrite)`.

**Pragmatic shortcut:** if the sweep gets hairy, inline the reverse-map read at each site instead of a single property. Either way, the goal is zero behavior change.

- [ ] **Step 3.5: Update ApplySettings — write v4 lists directly**

Find the current block in `ApplySettings` (around line 1172):

```csharp
        var (v4Accounts, v4Characters) = LoginAccountSplitter.Split(_pendingAccounts);
```

Replace the block through to the config assignment (around line 1253 `LegacyAccounts = _pendingAccounts`) with:

```csharp
        // Phase 4: v4 lists are the source of truth. Reverse-map back to
        // LegacyAccounts so downgrade / Validate() still see a consistent v3 view.
        var legacyAccountsForConfig = ReverseMapToLegacy(_pendingAccounts, _pendingCharacters);
```

Then find the `LegacyAccounts = _pendingAccounts,` line in the AppConfig assignment block and change to `LegacyAccounts = legacyAccountsForConfig,`. The v4 lists need assignment too — find `Accounts = v4Accounts,` and `Characters = v4Characters,` and change to:

```csharp
            Accounts = _pendingAccounts.Select(a => new Account
            {
                Name = a.Name,
                Username = a.Username,
                EncryptedPassword = a.EncryptedPassword,
                Server = a.Server,
                UseLoginFlag = a.UseLoginFlag,
            }).ToList(),

            Characters = _pendingCharacters.Select(c => new Character
            {
                Name = c.Name,
                AccountUsername = c.AccountUsername,
                AccountServer = c.AccountServer,
                CharacterSlot = c.CharacterSlot,
                DisplayLabel = c.DisplayLabel,
                ClassHint = c.ClassHint,
                Notes = c.Notes,
            }).ToList(),
```

- [ ] **Step 3.6: Build — resolve any remaining compiler errors from the shim sweep**

Run: `dotnet build 2>&1 | tail -40`

Expected: `0 Error(s)`, 1 `[Obsolete]` warning. Any errors mean a `_pendingAccounts` read-site was missed in Step 3.4 — the error message points to the line; replace that site with `_legacyView` access.

- [ ] **Step 3.7: Manual sanity check**

Launch the debug build: `dotnet run`. Open Settings → Accounts tab. Verify:
- All current accounts render in the grid (natedogg, flotte, acpots, etc.) exactly as before.
- Edit buttons work (open the existing inline edit dialog).
- Save writes config without error.
- After save, reload EQSwitch: grid still shows same accounts.

Close the debug instance.

- [ ] **Step 3.8: Verify migration fixtures + phantom-click gates**

Run:
```bash
bash _tests/migration/run_fixtures.sh  # 9 passed
grep -c "gameState == 5" Native/mq2_bridge.cpp  # 2
grep -c "result == -2" Core/AutoLoginManager.cs  # 1
```

- [ ] **Step 3.9: Commit**

Run:
```bash
git add UI/SettingsForm.cs
git commit -m "$(cat <<'EOF'
refactor(settings): staging fields to v4 Account/Character lists

Phase 4 transitional step — zero behavior change, zero UI change.

Invert _pendingAccounts from List<LoginAccount> to List<Account> and
add List<Character> _pendingCharacters. Existing Accounts-tab grid code
reads through a _legacyView property (reverse-maps v4 lists back to
legacy LoginAccount shape) so the UI renders unchanged while the data
model flips underneath.

Add ReverseMapToLegacy helper — mirror of LoginAccountSplitter.Split.
Used in ApplySettings to keep _config.LegacyAccounts in sync with v4
state so AppConfig.Validate() defense-in-depth never triggers a resync
that would wipe Phase-4-only Character edits.

Transitional _legacyView read shim + temporary write-path round-trips
(edit → legacy → Split → v4) are all marked 'TRANSITIONAL — removed in
Task 7' and deleted when the dual-section rewrite lands.

Build green; 9 fixtures pass; phantom-click gates unchanged.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: AccountEditDialog (new file)

**Files:**
- Create: `UI/AccountEditDialog.cs` (~180 lines)

- [ ] **Step 4.1: Create AccountEditDialog.cs with full dialog body**

Create file `UI/AccountEditDialog.cs`:

```csharp
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Modal dialog for creating or editing an Account (v4 first-class credentials).
/// Phase 4. Uses DarkTheme factories; no hardcoded colors.
/// </summary>
public class AccountEditDialog : Form
{
    private readonly TextBox _txtName;
    private readonly TextBox _txtUsername;
    private readonly TextBox _txtPassword;
    private readonly Button _btnRevealPassword;
    private readonly ComboBox _cboServer;
    private readonly CheckBox _chkUseLoginFlag;

    private readonly bool _isEdit;
    private readonly string? _existingEncryptedPassword;
    private bool _passwordRevealed;

    /// <summary>Result of the dialog. Null until DialogResult.OK.</summary>
    public Account? Result { get; private set; }

    /// <summary>
    /// Creates the dialog. Pass <paramref name="existing"/> to edit, null for a new Account.
    /// <paramref name="otherAccounts"/> is used for uniqueness validation.
    /// </summary>
    public AccountEditDialog(Account? existing, System.Collections.Generic.IReadOnlyList<Account> otherAccounts)
    {
        _isEdit = existing != null;
        _existingEncryptedPassword = existing?.EncryptedPassword;

        Text = _isEdit ? $"Edit Account — {existing!.Name}" : "Add Account";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 280);
        DarkTheme.StyleForm(this);

        const int L = 10, I = 130, R = 28;
        int y = 12;

        DarkTheme.AddLabel(this, "Name:", L, y + 4);
        _txtName = new TextBox { Location = new Point(I, y), Width = 270 };
        DarkTheme.StyleTextBox(_txtName);
        _txtName.Text = existing?.Name ?? "";
        Controls.Add(_txtName);
        y += R;

        DarkTheme.AddLabel(this, "Username:", L, y + 4);
        _txtUsername = new TextBox { Location = new Point(I, y), Width = 270 };
        DarkTheme.StyleTextBox(_txtUsername);
        _txtUsername.Text = existing?.Username ?? "";
        if (_isEdit)
        {
            _txtUsername.ReadOnly = true;
            _txtUsername.ForeColor = DarkTheme.FgDimGray;
        }
        Controls.Add(_txtUsername);
        y += R;

        DarkTheme.AddLabel(this, "Password:", L, y + 4);
        _txtPassword = new TextBox { Location = new Point(I, y), Width = 220, PasswordChar = '*' };
        DarkTheme.StyleTextBox(_txtPassword);
        Controls.Add(_txtPassword);

        _btnRevealPassword = new Button
        {
            Location = new Point(I + 225, y - 1),
            Size = new Size(45, 24),
            Text = "Show",
            TabStop = false,
        };
        DarkTheme.StyleButton(_btnRevealPassword);
        _btnRevealPassword.Click += (_, _) =>
        {
            _passwordRevealed = !_passwordRevealed;
            _txtPassword.PasswordChar = _passwordRevealed ? '\0' : '*';
            _btnRevealPassword.Text = _passwordRevealed ? "Hide" : "Show";
        };
        Controls.Add(_btnRevealPassword);
        y += R;

        if (_isEdit)
        {
            var hintLbl = DarkTheme.AddLabel(this, "Leave blank to keep existing password.", I, y + 2);
            hintLbl.ForeColor = DarkTheme.FgDimGray;
            y += 22;
        }

        DarkTheme.AddLabel(this, "Server:", L, y + 4);
        _cboServer = new ComboBox
        {
            Location = new Point(I, y),
            Width = 270,
            DropDownStyle = ComboBoxStyle.DropDown,
        };
        DarkTheme.StyleComboBox(_cboServer);
        _cboServer.Items.AddRange(new object[] { "Dalaya" });
        _cboServer.Text = existing?.Server ?? "Dalaya";
        Controls.Add(_cboServer);
        y += R;

        _chkUseLoginFlag = new CheckBox
        {
            Location = new Point(I, y + 2),
            Width = 270,
            Text = "Use login flag (pass -login to eqgame.exe)",
            Checked = existing?.UseLoginFlag ?? false,
        };
        DarkTheme.StyleCheckBox(_chkUseLoginFlag);
        Controls.Add(_chkUseLoginFlag);
        y += R + 4;

        var btnSave = new Button { Text = "Save", Size = new Size(90, 28), Location = new Point(220, y + 10) };
        DarkTheme.StylePrimaryButton(btnSave);
        btnSave.Click += (_, _) => OnSaveClicked(otherAccounts);
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = new Button { Text = "Cancel", Size = new Size(90, 28), Location = new Point(320, y + 10) };
        DarkTheme.StyleButton(btnCancel);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
    }

    private void OnSaveClicked(System.Collections.Generic.IReadOnlyList<Account> otherAccounts)
    {
        var name = _txtName.Text.Trim();
        var username = _txtUsername.Text.Trim();
        var server = (_cboServer.Text ?? "").Trim();
        if (string.IsNullOrEmpty(server)) server = "Dalaya";

        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Name is required.", "Invalid Account", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtName.Focus();
            return;
        }
        if (string.IsNullOrEmpty(username))
        {
            MessageBox.Show("Username is required.", "Invalid Account", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtUsername.Focus();
            return;
        }

        // Uniqueness: Name within otherAccounts; (Username, Server) within otherAccounts.
        // Edit mode excludes "self" via reference equality not applicable here — use the
        // existing Username as the identity key.
        var selfUsername = _isEdit ? _txtUsername.Text.Trim() : null;  // Username read-only on edit
        foreach (var a in otherAccounts)
        {
            if (_isEdit && a.Username.Equals(selfUsername, StringComparison.Ordinal) &&
                a.Server.Equals(server, StringComparison.Ordinal))
            {
                continue;  // same row being edited
            }
            if (a.Name.Equals(name, StringComparison.Ordinal))
            {
                MessageBox.Show($"An Account named '{name}' already exists.", "Duplicate Name",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtName.Focus();
                return;
            }
            if (a.Username.Equals(username, StringComparison.Ordinal) &&
                a.Server.Equals(server, StringComparison.Ordinal))
            {
                MessageBox.Show($"An Account with Username '{username}' on Server '{server}' already exists.",
                    "Duplicate Credentials", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtUsername.Focus();
                return;
            }
        }

        // Password handling
        string encryptedPassword;
        if (_isEdit && string.IsNullOrEmpty(_txtPassword.Text))
        {
            encryptedPassword = _existingEncryptedPassword ?? "";
        }
        else
        {
            if (string.IsNullOrEmpty(_txtPassword.Text))
            {
                MessageBox.Show("Password is required for new Accounts.", "Password Missing",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtPassword.Focus();
                return;
            }
            try
            {
                encryptedPassword = CredentialManager.Encrypt(_txtPassword.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to encrypt password: {ex.Message}", "DPAPI Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        Result = new Account
        {
            Name = name,
            Username = username,
            EncryptedPassword = encryptedPassword,
            Server = server,
            UseLoginFlag = _chkUseLoginFlag.Checked,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
```

- [ ] **Step 4.2: Verify DarkTheme factory method names match**

The file above calls `DarkTheme.AddLabel`, `DarkTheme.StyleTextBox`, `DarkTheme.StyleComboBox`, `DarkTheme.StyleCheckBox`, `DarkTheme.StyleButton`, `DarkTheme.StylePrimaryButton`, `DarkTheme.FgDimGray`. If any of these don't exist (the codebase uses different names like `MakeButton` / `AddCardLabel` etc.), the build will fail.

Run: `grep -E "public static.*Make|public static.*Style|public static.*Add" UI/DarkTheme.cs | head -30`

Cross-check every `DarkTheme.*` call in `AccountEditDialog.cs` against the actual signatures. Adjust the dialog to use the real factory names. Common remappings:
- `DarkTheme.StyleButton` → `DarkTheme.MakeButton(this, text, x, y, w, h, onClick)` (different signature)
- `DarkTheme.StylePrimaryButton` → `DarkTheme.MakePrimaryButton(...)`
- `DarkTheme.AddLabel` → `DarkTheme.AddLabel(parent, text, x, y)` OR `DarkTheme.AddCardLabel(...)`
- `DarkTheme.FgDimGray` → same or possibly `FgDim` / `FgSecondary`

Rewrite the dialog to match actual factory signatures. Preserve the overall field layout and validation logic — only the construction idiom changes.

- [ ] **Step 4.3: Build**

Run: `dotnet build 2>&1 | tail -20`

Expected: `0 Error(s)`. 1 `[Obsolete]` warning.

- [ ] **Step 4.4: Commit**

Run:
```bash
git add UI/AccountEditDialog.cs
git commit -m "$(cat <<'EOF'
feat(settings): AccountEditDialog modal

New modal Form subclass for creating / editing Account entities.
Dark-themed via existing DarkTheme factories. Fields: Name (required,
unique in Accounts), Username (required, (Username, Server) unique;
read-only in edit mode), Password (PasswordChar='*' default + Show/Hide
reveal toggle, DPAPI via CredentialManager, blank-on-edit keeps existing),
Server (combo, defaults Dalaya), UseLoginFlag.

Save validates uniqueness + password presence for new accounts; Edit
mode permits blank password to preserve existing. Encryption errors
surface via MessageBox without leaking plaintext.

Wired from SettingsForm Accounts tab in Task 7.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: CharacterEditDialog (new file)

**Files:**
- Create: `UI/CharacterEditDialog.cs` (~160 lines)

- [ ] **Step 5.1: Create CharacterEditDialog.cs**

Create `UI/CharacterEditDialog.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Modal dialog for creating or editing a Character (v4 first-class launch target).
/// Phase 4. Uses DarkTheme factories; no hardcoded colors. ClassHint dropped from UI
/// per 2026-04-15 design decision (no reliable source for EQ class data); field
/// remains on the Character model for forward-compat.
/// </summary>
public class CharacterEditDialog : Form
{
    private readonly TextBox _txtName;
    private readonly ComboBox _cboAccount;
    private readonly NumericUpDown _numSlot;
    private readonly TextBox _txtDisplayLabel;
    private readonly TextBox _txtNotes;

    private readonly bool _isEdit;

    public Character? Result { get; private set; }

    public CharacterEditDialog(
        Character? existing,
        IReadOnlyList<Account> availableAccounts,
        IReadOnlyList<Character> otherCharacters)
    {
        _isEdit = existing != null;

        Text = _isEdit ? $"Edit Character — {existing!.Name}" : "Add Character";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 320);
        DarkTheme.StyleForm(this);

        const int L = 10, I = 130, R = 28;
        int y = 12;

        // Guard: can't create a Character without Accounts.
        if (availableAccounts.Count == 0)
        {
            var lbl = DarkTheme.AddLabel(this, "Add an account first — Characters require an account to launch into.",
                L, y + 4);
            lbl.ForeColor = DarkTheme.FgDimGray;
            lbl.Size = new Size(400, 40);

            var btnClose = new Button
            {
                Text = "Close",
                Size = new Size(90, 28),
                Location = new Point(320, ClientSize.Height - 38),
            };
            DarkTheme.StyleButton(btnClose);
            btnClose.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnClose);
            CancelButton = btnClose;
            AcceptButton = btnClose;

            // Unused fields (set to non-null to satisfy null-safety without disabling the feature)
            _txtName = new TextBox();
            _cboAccount = new ComboBox();
            _numSlot = new NumericUpDown();
            _txtDisplayLabel = new TextBox();
            _txtNotes = new TextBox();
            return;
        }

        DarkTheme.AddLabel(this, "Name:", L, y + 4);
        _txtName = new TextBox { Location = new Point(I, y), Width = 270 };
        DarkTheme.StyleTextBox(_txtName);
        _txtName.Text = existing?.Name ?? "";
        Controls.Add(_txtName);
        y += R;

        DarkTheme.AddLabel(this, "Account:", L, y + 4);
        _cboAccount = new ComboBox
        {
            Location = new Point(I, y),
            Width = 270,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(Account.Name),
        };
        DarkTheme.StyleComboBox(_cboAccount);
        foreach (var a in availableAccounts)
            _cboAccount.Items.Add(a);
        if (existing != null)
        {
            var match = availableAccounts.FirstOrDefault(a =>
                a.Username.Equals(existing.AccountUsername, StringComparison.Ordinal) &&
                a.Server.Equals(existing.AccountServer, StringComparison.Ordinal));
            if (match != null) _cboAccount.SelectedItem = match;
            else _cboAccount.SelectedIndex = 0;
        }
        else
        {
            _cboAccount.SelectedIndex = 0;
        }
        Controls.Add(_cboAccount);
        y += R;

        DarkTheme.AddLabel(this, "Slot:", L, y + 4);
        _numSlot = new NumericUpDown
        {
            Location = new Point(I, y),
            Width = 70,
            Minimum = 0,
            Maximum = 10,
            Value = existing?.CharacterSlot ?? 0,
        };
        DarkTheme.StyleNumericUpDown(_numSlot);
        Controls.Add(_numSlot);

        var slotHint = DarkTheme.AddLabel(this, "0 = match by name (recommended)", I + 80, y + 4);
        slotHint.ForeColor = DarkTheme.FgDimGray;
        y += R;

        DarkTheme.AddLabel(this, "Display Label:", L, y + 4);
        _txtDisplayLabel = new TextBox { Location = new Point(I, y), Width = 270 };
        DarkTheme.StyleTextBox(_txtDisplayLabel);
        _txtDisplayLabel.Text = existing?.DisplayLabel ?? "";
        Controls.Add(_txtDisplayLabel);
        y += R;

        DarkTheme.AddLabel(this, "Notes:", L, y + 4);
        _txtNotes = new TextBox
        {
            Location = new Point(I, y),
            Width = 270,
            Height = 70,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
        };
        DarkTheme.StyleTextBox(_txtNotes);
        _txtNotes.Text = existing?.Notes ?? "";
        Controls.Add(_txtNotes);
        y += 78;

        var btnSave = new Button { Text = "Save", Size = new Size(90, 28), Location = new Point(220, y + 6) };
        DarkTheme.StylePrimaryButton(btnSave);
        btnSave.Click += (_, _) => OnSaveClicked(existing, otherCharacters);
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = new Button { Text = "Cancel", Size = new Size(90, 28), Location = new Point(320, y + 6) };
        DarkTheme.StyleButton(btnCancel);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
    }

    private void OnSaveClicked(Character? existing, IReadOnlyList<Character> otherCharacters)
    {
        var name = _txtName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Name is required.", "Invalid Character", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtName.Focus();
            return;
        }

        var selfName = existing?.Name;
        foreach (var c in otherCharacters)
        {
            if (_isEdit && c.Name.Equals(selfName, StringComparison.Ordinal)) continue;
            if (c.Name.Equals(name, StringComparison.Ordinal))
            {
                MessageBox.Show($"A Character named '{name}' already exists.", "Duplicate Name",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtName.Focus();
                return;
            }
        }

        var selectedAccount = _cboAccount.SelectedItem as Account;
        if (selectedAccount == null)
        {
            MessageBox.Show("Account is required.", "Invalid Character", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = new Character
        {
            Name = name,
            AccountUsername = selectedAccount.Username,
            AccountServer = selectedAccount.Server,
            CharacterSlot = (int)_numSlot.Value,
            DisplayLabel = _txtDisplayLabel.Text.Trim(),
            ClassHint = existing?.ClassHint ?? "",   // preserved from existing; not edited in UI
            Notes = _txtNotes.Text,                  // multiline — don't trim trailing newlines
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
```

- [ ] **Step 5.2: Align with actual DarkTheme factory names**

Same as Step 4.2. Grep DarkTheme signatures and adjust calls. Particular attention to `StyleNumericUpDown` — may not exist; if not, construct the numeric with explicit dark background colors from `DarkTheme.BgInput` / `DarkTheme.FgPrimary` etc.

- [ ] **Step 5.3: Build**

Run: `dotnet build 2>&1 | tail -20`
Expected: `0 Error(s)`, 1 `[Obsolete]` warning.

- [ ] **Step 5.4: Commit**

Run:
```bash
git add UI/CharacterEditDialog.cs
git commit -m "$(cat <<'EOF'
feat(settings): CharacterEditDialog modal

Modal Form for creating / editing Character entities. Dark-themed.
Fields: Name (required, unique in Characters list), Account (combo
bound to available Accounts, DisplayMember=Name, selection stores
Username+Server for FK), Slot (numeric 0-10, 0=auto-by-name),
DisplayLabel (optional tray decoration), Notes (multiline).

ClassHint dropped from UI per design decision — no reliable EQ class
source. Field persisted from existing record for forward-compat.

Guards against empty Accounts list with a hint and early close.
Account-combo selection stores canonical-cased Username+Server so
AccountKey.Matches (Ordinal) resolves at runtime.

Wired from SettingsForm Characters section in Task 7.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: CascadeDeleteDialog (new file)

**Files:**
- Create: `UI/CascadeDeleteDialog.cs` (~100 lines)

- [ ] **Step 6.1: Create CascadeDeleteDialog.cs**

Create `UI/CascadeDeleteDialog.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using EQSwitch.Models;

namespace EQSwitch.UI;

public enum CascadeDeleteChoice
{
    Cancel,
    Unlink,
    DeleteAll,
}

/// <summary>
/// Modal prompt shown when deleting an Account that has dependent Characters.
/// Three-button design: Cancel / Unlink (orphans) / Delete All (cascades).
/// Phase 4.
/// </summary>
public class CascadeDeleteDialog : Form
{
    public CascadeDeleteChoice Choice { get; private set; } = CascadeDeleteChoice.Cancel;

    public CascadeDeleteDialog(Account account, IReadOnlyList<Character> dependents)
    {
        Text = $"Delete Account '{account.Name}'?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, 260 + Math.Min(dependents.Count, 6) * 18);
        DarkTheme.StyleForm(this);

        int y = 14;

        var headerLbl = DarkTheme.AddLabel(this,
            $"{dependents.Count} character{(dependents.Count == 1 ? "" : "s")} linked to this account:",
            14, y);
        headerLbl.Size = new Size(450, 20);
        y += 24;

        // Show up to 6 dependents; "+N more" if longer.
        var shown = dependents.Count <= 6 ? dependents.Count : 6;
        for (int i = 0; i < shown; i++)
        {
            var bulletLbl = DarkTheme.AddLabel(this, $"   \u2022  {dependents[i].Name}", 20, y);
            Controls.Add(bulletLbl);
            y += 18;
        }
        if (dependents.Count > shown)
        {
            var moreLbl = DarkTheme.AddLabel(this, $"   \u2022  \u2026 and {dependents.Count - shown} more", 20, y);
            moreLbl.ForeColor = DarkTheme.FgDimGray;
            y += 18;
        }
        y += 10;

        var questionLbl = DarkTheme.AddLabel(this, "What should happen to them?", 14, y);
        questionLbl.Size = new Size(450, 20);
        y += 30;

        // Three buttons, right-aligned.
        var btnDeleteAll = new Button
        {
            Text = $"Delete All",
            Size = new Size(130, 30),
            Location = new Point(ClientSize.Width - 144, y),
        };
        DarkTheme.StyleDangerButton(btnDeleteAll);
        btnDeleteAll.Click += (_, _) => { Choice = CascadeDeleteChoice.DeleteAll; DialogResult = DialogResult.OK; Close(); };
        Controls.Add(btnDeleteAll);

        var btnUnlink = new Button
        {
            Text = "Unlink",
            Size = new Size(110, 30),
            Location = new Point(ClientSize.Width - 258, y),
        };
        DarkTheme.StyleButton(btnUnlink);
        btnUnlink.Click += (_, _) => { Choice = CascadeDeleteChoice.Unlink; DialogResult = DialogResult.OK; Close(); };
        Controls.Add(btnUnlink);

        var btnCancel = new Button
        {
            Text = "Cancel",
            Size = new Size(90, 30),
            Location = new Point(ClientSize.Width - 352, y),
        };
        DarkTheme.StyleButton(btnCancel);
        btnCancel.Click += (_, _) => { Choice = CascadeDeleteChoice.Cancel; DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
        AcceptButton = btnCancel;   // ESC + Enter both default to Cancel

        y += 42;

        var hintLbl = DarkTheme.AddLabel(this,
            "Unlinked characters keep their data but can't login until you assign a new account via Edit.",
            14, y);
        hintLbl.Size = new Size(450, 32);
        hintLbl.ForeColor = DarkTheme.FgDimGray;
    }
}
```

- [ ] **Step 6.2: Align DarkTheme factory names**

`DarkTheme.StyleDangerButton` may not exist — check `UI/DarkTheme.cs` for red-accent button factory. If absent, use `DarkTheme.StylePrimaryButton` and set `BackColor = DarkTheme.CardWarn` (or equivalent red/warn color already defined) manually after the Style call:

```csharp
DarkTheme.StyleButton(btnDeleteAll);
btnDeleteAll.BackColor = DarkTheme.CardWarn;  // or whatever the red-accent color is
btnDeleteAll.ForeColor = Color.White;
```

The exact red comes from `DarkTheme.cs` — `grep "CardWarn\|DangerRed\|ErrorBg" UI/DarkTheme.cs` to find the right constant.

- [ ] **Step 6.3: Build**

Run: `dotnet build 2>&1 | tail -20`
Expected: `0 Error(s)`, 1 `[Obsolete]` warning.

- [ ] **Step 6.4: Commit**

Run:
```bash
git add UI/CascadeDeleteDialog.cs
git commit -m "$(cat <<'EOF'
feat(settings): CascadeDeleteDialog modal

Three-button modal shown when deleting an Account with dependent
Characters. Cancel (default, ESC) leaves state unchanged. Unlink
clears AccountUsername/AccountServer on each dependent — characters
stay in config as orphans, surface with '(unassigned)' indicator in
the Characters grid. Delete All cascades, removing Account plus all
dependents.

Delete All button uses red danger accent to signal destructiveness;
Cancel + Unlink remain neutral. Dependent list truncates at 6 with
'+N more' for tall dependency graphs.

Used by SettingsForm Accounts grid row delete in Task 7.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Accounts tab dual-section layout

Largest commit. Replaces the existing `BuildAccountsTab` with the dual-section layout. Deletes all transitional `_legacyView` shims from Task 3. Wires `AccountEditDialog`, `CharacterEditDialog`, `CascadeDeleteDialog`.

**Files:**
- Modify: `UI/SettingsForm.cs`:
  - Delete transitional `_legacyView` property + all its call sites (marked `TRANSITIONAL`)
  - Rewrite `BuildAccountsTab` (was lines 1319-~1600)
  - New row-click / button-click handlers for Accounts and Characters grids
  - Hotkey-column lookup helper for the Characters grid (`HK` column)

- [ ] **Step 7.1: Remove transitional shims**

Search for all `// TRANSITIONAL` comments added in Task 3 and delete the surrounding wrapper code. The `_legacyView` property + any `ReverseMapToLegacy`-at-read-time calls in the Accounts-tab code go away. `ReverseMapToLegacy` the static helper stays (still used in `ApplySettings`).

- [ ] **Step 7.2: Replace BuildAccountsTab with dual-section layout**

Find `private TabPage BuildAccountsTab()` (was line 1319). Replace the entire method body with:

```csharp
    private DataGridView _dgvAccounts = null!;
    private DataGridView _dgvCharacters = null!;

    private TabPage BuildAccountsTab()
    {
        var page = new TabPage("Accounts");
        DarkTheme.StyleTabPage(page);

        int y = 10;

        // ──────────── Accounts section ────────────
        var lblAcctHeader = DarkTheme.AddLabel(page, "Accounts", 14, y);
        lblAcctHeader.Font = new Font("Segoe UI Semibold", 10f);
        y += 22;

        _dgvAccounts = new DataGridView
        {
            Location = new Point(10, y),
            Size = new Size(490, 160),
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fixed,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
        };
        DarkTheme.StyleDataGridView(_dgvAccounts);
        _dgvAccounts.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", Width = 110 });
        _dgvAccounts.Columns.Add(new DataGridViewTextBoxColumn { Name = "Username", HeaderText = "Username", Width = 130 });
        _dgvAccounts.Columns.Add(new DataGridViewTextBoxColumn { Name = "Server", HeaderText = "Server", Width = 90 });
        _dgvAccounts.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Flag", HeaderText = "Flag", Width = 50 });
        _dgvAccounts.Columns.Add(new DataGridViewButtonColumn { Name = "Edit", HeaderText = "Edit", Width = 50, Text = "\u270F", UseColumnTextForButtonValue = true });
        _dgvAccounts.Columns.Add(new DataGridViewButtonColumn { Name = "Delete", HeaderText = "Del", Width = 50, Text = "\uD83D\uDDD1", UseColumnTextForButtonValue = true });

        _dgvAccounts.CellClick += OnAccountsGridCellClick;
        page.Controls.Add(_dgvAccounts);
        y += 165;

        // Button row
        var btnAddAccount = new Button { Text = "+ Add Account", Size = new Size(130, 28), Location = new Point(10, y) };
        DarkTheme.StylePrimaryButton(btnAddAccount);
        btnAddAccount.Click += (_, _) => OnAddAccount();
        page.Controls.Add(btnAddAccount);

        var btnImport = new Button { Text = "Import...", Size = new Size(90, 28), Location = new Point(150, y) };
        DarkTheme.StyleButton(btnImport);
        btnImport.Click += (_, _) => OnImportAccounts();
        page.Controls.Add(btnImport);

        var btnExport = new Button { Text = "Export...", Size = new Size(90, 28), Location = new Point(250, y) };
        DarkTheme.StyleButton(btnExport);
        btnExport.Click += (_, _) => OnExportAccounts();
        page.Controls.Add(btnExport);
        y += 40;

        // ──────────── Characters section ────────────
        var lblCharHeader = DarkTheme.AddLabel(page, "Characters", 14, y);
        lblCharHeader.Font = new Font("Segoe UI Semibold", 10f);
        y += 22;

        _dgvCharacters = new DataGridView
        {
            Location = new Point(10, y),
            Size = new Size(490, 180),
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fixed,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
        };
        DarkTheme.StyleDataGridView(_dgvCharacters);
        _dgvCharacters.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", Width = 130 });
        _dgvCharacters.Columns.Add(new DataGridViewTextBoxColumn { Name = "Account", HeaderText = "Account", Width = 130 });
        _dgvCharacters.Columns.Add(new DataGridViewTextBoxColumn { Name = "Slot", HeaderText = "Slot", Width = 60 });
        _dgvCharacters.Columns.Add(new DataGridViewTextBoxColumn { Name = "HK", HeaderText = "HK", Width = 70 });
        _dgvCharacters.Columns.Add(new DataGridViewButtonColumn { Name = "Edit", HeaderText = "Edit", Width = 50, Text = "\u270F", UseColumnTextForButtonValue = true });
        _dgvCharacters.Columns.Add(new DataGridViewButtonColumn { Name = "Delete", HeaderText = "Del", Width = 50, Text = "\uD83D\uDDD1", UseColumnTextForButtonValue = true });

        _dgvCharacters.CellClick += OnCharactersGridCellClick;
        page.Controls.Add(_dgvCharacters);
        y += 185;

        var btnAddCharacter = new Button { Text = "+ Add Character", Size = new Size(140, 28), Location = new Point(10, y) };
        DarkTheme.StylePrimaryButton(btnAddCharacter);
        btnAddCharacter.Click += (_, _) => OnAddCharacter();
        page.Controls.Add(btnAddCharacter);

        RefreshAccountsGrid();
        RefreshCharactersGrid();

        return page;
    }
```

Then add the refresh + click-handler helpers (place just after the method):

```csharp
    private void RefreshAccountsGrid()
    {
        _dgvAccounts.Rows.Clear();
        foreach (var a in _pendingAccounts)
        {
            _dgvAccounts.Rows.Add(a.Name, a.Username, a.Server, a.UseLoginFlag, "\u270F", "\uD83D\uDDD1");
        }
    }

    private void RefreshCharactersGrid()
    {
        _dgvCharacters.Rows.Clear();
        foreach (var c in _pendingCharacters)
        {
            var acct = _pendingAccounts.FirstOrDefault(a =>
                a.Username.Equals(c.AccountUsername, StringComparison.Ordinal) &&
                a.Server.Equals(c.AccountServer, StringComparison.Ordinal));
            var acctDisplay = acct?.Name ?? (string.IsNullOrEmpty(c.AccountUsername) ? "(unassigned)" : $"{c.AccountUsername}@{c.AccountServer} (missing)");

            var slotDisplay = c.CharacterSlot == 0 ? "auto" : c.CharacterSlot.ToString();
            var hkDisplay = LookupHotkeyForCharacter(c.Name);  // empty string if none

            var row = _dgvCharacters.Rows[_dgvCharacters.Rows.Add(c.Name, acctDisplay, slotDisplay, hkDisplay, "\u270F", "\uD83D\uDDD1")];
            if (acct == null)
            {
                row.Cells["Account"].Style.ForeColor = DarkTheme.FgDimGray;
                row.Cells["Account"].Style.Font = new Font("Segoe UI", 9f, FontStyle.Italic);
            }
        }
    }

    private string LookupHotkeyForCharacter(string characterName)
    {
        // v3.10.0 bridge: QuickLogin1-4 + HotkeyConfig.AutoLogin1-4 still hold
        // character bindings until Phase 5 replaces with CharacterHotkeys[].
        if (_pendingAccounts == null) return "";
        var slots = new[]
        {
            (_config.QuickLogin1, _config.Hotkeys.AutoLogin1),
            (_config.QuickLogin2, _config.Hotkeys.AutoLogin2),
            (_config.QuickLogin3, _config.Hotkeys.AutoLogin3),
            (_config.QuickLogin4, _config.Hotkeys.AutoLogin4),
        };
        foreach (var (target, combo) in slots)
        {
            if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(combo) &&
                target.Equals(characterName, StringComparison.Ordinal))
                return combo;
        }
        return "";
    }

    private void OnAccountsGridCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _pendingAccounts.Count) return;
        var colName = _dgvAccounts.Columns[e.ColumnIndex].Name;
        if (colName == "Edit") OnEditAccount(e.RowIndex);
        else if (colName == "Delete") OnDeleteAccount(e.RowIndex);
    }

    private void OnCharactersGridCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _pendingCharacters.Count) return;
        var colName = _dgvCharacters.Columns[e.ColumnIndex].Name;
        if (colName == "Edit") OnEditCharacter(e.RowIndex);
        else if (colName == "Delete") OnDeleteCharacter(e.RowIndex);
    }

    private void OnAddAccount()
    {
        using var dlg = new AccountEditDialog(null, _pendingAccounts);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            _pendingAccounts.Add(dlg.Result);
            RefreshAccountsGrid();
            RefreshCharactersGrid();   // new Account may resolve orphans
        }
    }

    private void OnEditAccount(int idx)
    {
        var existing = _pendingAccounts[idx];
        var others = _pendingAccounts.Where((_, i) => i != idx).ToList();
        using var dlg = new AccountEditDialog(existing, others);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            // Preserve FK on dependents if Account Name changed (Characters reference by Username+Server,
            // not Name — no FK repair needed).
            _pendingAccounts[idx] = dlg.Result;
            RefreshAccountsGrid();
            RefreshCharactersGrid();
        }
    }

    private void OnDeleteAccount(int idx)
    {
        var acct = _pendingAccounts[idx];
        var dependents = _pendingCharacters.Where(c =>
            c.AccountUsername.Equals(acct.Username, StringComparison.Ordinal) &&
            c.AccountServer.Equals(acct.Server, StringComparison.Ordinal)).ToList();

        if (dependents.Count == 0)
        {
            if (MessageBox.Show($"Delete Account '{acct.Name}'?", "Delete Account",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _pendingAccounts.RemoveAt(idx);
        }
        else
        {
            using var dlg = new CascadeDeleteDialog(acct, dependents);
            dlg.ShowDialog(this);
            switch (dlg.Choice)
            {
                case CascadeDeleteChoice.Cancel:
                    return;
                case CascadeDeleteChoice.Unlink:
                    foreach (var c in dependents)
                    {
                        c.AccountUsername = "";
                        c.AccountServer = "";
                    }
                    _pendingAccounts.RemoveAt(idx);
                    break;
                case CascadeDeleteChoice.DeleteAll:
                    foreach (var c in dependents) _pendingCharacters.Remove(c);
                    _pendingAccounts.RemoveAt(idx);
                    break;
            }
        }

        RefreshAccountsGrid();
        RefreshCharactersGrid();
    }

    private void OnAddCharacter()
    {
        using var dlg = new CharacterEditDialog(null, _pendingAccounts, _pendingCharacters);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            _pendingCharacters.Add(dlg.Result);
            RefreshCharactersGrid();
        }
    }

    private void OnEditCharacter(int idx)
    {
        var existing = _pendingCharacters[idx];
        var others = _pendingCharacters.Where((_, i) => i != idx).ToList();
        using var dlg = new CharacterEditDialog(existing, _pendingAccounts, others);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            _pendingCharacters[idx] = dlg.Result;
            RefreshCharactersGrid();
        }
    }

    private void OnDeleteCharacter(int idx)
    {
        var c = _pendingCharacters[idx];
        if (MessageBox.Show($"Delete Character '{c.Name}'?", "Delete Character",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _pendingCharacters.RemoveAt(idx);
            RefreshCharactersGrid();
        }
    }

    // Existing OnImportAccounts / OnExportAccounts handlers: update to operate on
    // _pendingAccounts as List<Account> — password round-trip via CredentialManager.
    // Details in Task 7 sub-steps below.
```

- [ ] **Step 7.3: Update existing Import/Export handlers to use v4 Account type**

Find `OnImportAccounts()` and `OnExportAccounts()` in the current file (grep for `_pendingAccounts.Add` and `JsonSerializer.Serialize(_pendingAccounts,`). Both currently operate on `List<LoginAccount>`. Update:

- Export: serialize `_pendingAccounts` as `List<Account>` (simpler shape — no CharacterName/AutoEnterWorld). Consider adding a separate Characters export or bundling both into one JSON object. For Phase 4, keep scope tight: export Accounts only. Characters are typically re-derived from in-game data.
- Import: deserialize as `List<Account>`, merge into `_pendingAccounts` using `(Username, Server)` uniqueness.

```csharp
    private void OnImportAccounts()
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "EQSwitch Accounts (*.json)|*.json|All files (*.*)|*.*",
            Title = "Import Accounts",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var json = System.IO.File.ReadAllText(ofd.FileName);
            var imported = System.Text.Json.JsonSerializer.Deserialize<List<Account>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (imported == null) return;

            int added = 0, skipped = 0;
            foreach (var a in imported)
            {
                var exists = _pendingAccounts.Any(x =>
                    x.Username.Equals(a.Username, StringComparison.Ordinal) &&
                    x.Server.Equals(a.Server, StringComparison.Ordinal));
                if (exists) { skipped++; continue; }
                _pendingAccounts.Add(a);
                added++;
            }
            MessageBox.Show($"Imported {added} account(s); skipped {skipped} duplicate(s).",
                "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshAccountsGrid();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to import: {ex.Message}", "Import Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnExportAccounts()
    {
        if (_pendingAccounts.Count == 0)
        {
            MessageBox.Show("No accounts to export.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var sfd = new SaveFileDialog
        {
            Filter = "EQSwitch Accounts (*.json)|*.json",
            Title = "Export Accounts",
            FileName = "eqswitch-accounts.json",
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_pendingAccounts,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(sfd.FileName, json);
            MessageBox.Show($"Exported {_pendingAccounts.Count} account(s).\n\n"
                + "Passwords are DPAPI-encrypted — this file only works on the same Windows user account.",
                "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export: {ex.Message}", "Export Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
```

- [ ] **Step 7.4: Hunt down any remaining references to the old list shape**

Grep `UI/SettingsForm.cs` for patterns that still assume `_pendingAccounts` is `List<LoginAccount>`:
- `_pendingAccounts.Select(a => a.CharacterName)` — should now target `_pendingCharacters`.
- `.CharacterName` / `.AutoEnterWorld` reads on `_pendingAccounts` elements — now invalid.
- Any `CheckDuplicateSlotAccounts` or `RefreshQuickLoginCombos` methods read QuickLogin1-4 population. Those combos should now list from `_pendingCharacters` (preferred) and `_pendingAccounts` (fallback), not the old single list.

Fix each site. Build after each batch of fixes to keep compile errors manageable.

- [ ] **Step 7.5: Build + manual sanity check**

Run: `dotnet build 2>&1 | tail -30`
Expected: `0 Error(s)`, 1 `[Obsolete]` warning. No new warnings.

Launch debug build. Open Settings → Accounts tab. Verify:
- Two grids render (Accounts on top, Characters below).
- Current data populates: 3 Accounts (natedogg, flotte, acpots), 4 Characters.
- `+ Add Account` opens the new AccountEditDialog.
- `+ Add Character` opens the new CharacterEditDialog.
- Edit icon on an Accounts row opens AccountEditDialog with existing data.
- Delete icon on Accounts row: with 0 dependents → simple confirm; with ≥1 dependent → CascadeDeleteDialog.

Close debug.

- [ ] **Step 7.6: Commit**

```bash
git add UI/SettingsForm.cs
git commit -m "$(cat <<'EOF'
feat(settings): Accounts tab dual-section layout

Replace the single mixed-Account-and-Character grid with two dedicated
sections and a button row per section:

- Accounts grid (Name / Username / Server / Flag / Edit / Delete) +
  [+ Add Account] [Import...] [Export...]
- Characters grid (Name / Account / Slot / HK / Edit / Delete) +
  [+ Add Character]

Row edit + add go through AccountEditDialog / CharacterEditDialog
(Tasks 4 + 5). Delete of an Account with dependent Characters routes
through CascadeDeleteDialog (Task 6) with three semantics: Cancel,
Unlink (orphan), Delete All (cascade).

Unlinked Characters render their Account column as '(unassigned)' in
italic dim gray. Broken FK (Account missing post-edit) renders as
'{user}@{server} (missing)'.

Import/Export simplified to Account-only JSON; Characters continue to
be added in-app (users re-derive from in-game character list).

Removes all TRANSITIONAL shims from Task 3. _pendingAccounts is now
read/written as List<Account>; _pendingCharacters as List<Character>.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: Cross-section validation + reverse-map on save

**Files:**
- Modify: `UI/SettingsForm.cs` `ApplySettings` — add validation steps in order, ensure reverse-map and direct-write are final state

- [ ] **Step 8.1: Augment ApplySettings with validation order**

In `ApplySettings` (already `bool` from Task 2), after the hotkey conflict scan and before any config mutation, add the cross-section validation block:

```csharp
        // Phase 4: cross-section validation. Run in order — structural errors before
        // cosmetic ones. All validation failures return false (block Save).

        // 1. Account names unique
        var acctNameDupes = _pendingAccounts
            .GroupBy(a => a.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        if (acctNameDupes.Any())
        {
            var names = string.Join(", ", acctNameDupes.Select(g => $"'{g.Key}' ({g.Count()} times)"));
            MessageBox.Show($"Account names must be unique. Duplicates: {names}", "Duplicate Account Name",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // 2. Account (Username, Server) unique
        var acctCredDupes = _pendingAccounts
            .GroupBy(a => $"{a.Username}\u0001{a.Server}", StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        if (acctCredDupes.Any())
        {
            var keys = string.Join(", ", acctCredDupes.Select(g => g.Key.Replace("\u0001", "@")));
            MessageBox.Show($"Account (Username, Server) must be unique. Duplicates: {keys}",
                "Duplicate Credentials", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // 3. Character names unique
        var charNameDupes = _pendingCharacters
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        if (charNameDupes.Any())
        {
            var names = string.Join(", ", charNameDupes.Select(g => $"'{g.Key}' ({g.Count()} times)"));
            MessageBox.Show($"Character names must be unique. Duplicates: {names}", "Duplicate Character Name",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // 4. Character FK — every non-empty AccountUsername must resolve to an Account.
        //    Empty AccountUsername = Unlink state, legitimate orphan.
        foreach (var c in _pendingCharacters)
        {
            if (string.IsNullOrEmpty(c.AccountUsername)) continue;   // orphan, allowed
            bool resolved = _pendingAccounts.Any(a =>
                a.Username.Equals(c.AccountUsername, StringComparison.Ordinal) &&
                a.Server.Equals(c.AccountServer, StringComparison.Ordinal));
            if (!resolved)
            {
                MessageBox.Show(
                    $"Character '{c.Name}' references missing account '{c.AccountUsername}@{c.AccountServer}'. "
                  + "Edit the Character to fix or delete it.",
                    "Broken Character FK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }
```

Place this block immediately after the existing hotkey-conflict block (Task 2) and before any mutation of `_config`.

- [ ] **Step 8.2: Verify reverse-map is the final write path**

Confirm the ApplySettings tail reads:

```csharp
        _config.Accounts = _pendingAccounts.Select(a => new Account { ... }).ToList();
        _config.Characters = _pendingCharacters.Select(c => new Character { ... }).ToList();
        _config.LegacyAccounts = ReverseMapToLegacy(_pendingAccounts, _pendingCharacters);
        // ... other config field assignments (Hotkeys, Teams, Pip, etc.) unchanged
        return true;
```

If anything still calls `LoginAccountSplitter.Split` on the SettingsForm save path, delete that call — it's a leftover from Task 3. The splitter lives on only in `ConfigVersionMigrator` (JsonObject-level).

- [ ] **Step 8.3: Build + fixture + phantom-click**

Run:
```bash
dotnet build 2>&1 | tail -20
bash _tests/migration/run_fixtures.sh
grep -c "gameState == 5" Native/mq2_bridge.cpp
grep -c "result == -2" Core/AutoLoginManager.cs
```

All expected: build clean, 9 passed, 2, 1.

- [ ] **Step 8.4: Manual round-trip test**

Launch debug build. Edit a Character's `DisplayLabel` and `Notes`. Save. Close Settings. Reopen Settings → Accounts tab → find the edited Character → click Edit. Assert: DisplayLabel and Notes preserved.

- [ ] **Step 8.5: Commit**

```bash
git add UI/SettingsForm.cs
git commit -m "$(cat <<'EOF'
feat(settings): cross-section validation + reverse-map on save

ApplySettings now runs cross-section invariants before mutating config:
 1. Account names unique (Ordinal)
 2. Account (Username, Server) unique
 3. Character names unique
 4. Every Character's non-empty AccountKey resolves to an Account

Each failure blocks Save with an actionable modal. Orphan Characters
(AccountUsername == '') are legitimate — Unlink cascade-delete outcome.

On success: Accounts + Characters copy-written to _config directly.
LegacyAccounts rebuilt via ReverseMapToLegacy so downgrade + Validate()
defense-in-depth see a consistent v3 view. LoginAccountSplitter no
longer runs on Save — splitter lives only in the migrator.

DisplayLabel/Notes now survive round-trips: load → edit → save →
reload → re-edit preserves user-entered values.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: AutoLoginTeamsDialog — v4 signature + OK/WARN/FAIL indicators

**Files:**
- Modify: `UI/AutoLoginTeamsDialog.cs` — constructor signature, slot dropdown content, per-slot resolution pills
- Modify: `UI/SettingsForm.cs` — caller passes v4 lists

- [ ] **Step 9.1: Read current AutoLoginTeamsDialog to locate slot combo construction**

Run: `grep -n "public AutoLoginTeamsDialog\|_slot\|SlotDropdown\|TeamSlot" UI/AutoLoginTeamsDialog.cs`

Identify: the constructor signature (line ~30), the slot combo construction block, and the Save/Apply write path that pushes back to `_config.Team{N}Account{M}`.

- [ ] **Step 9.2: Update constructor signature**

```csharp
// OLD:
// public AutoLoginTeamsDialog(AppConfig config, List<LoginAccount> accounts)

// NEW:
public AutoLoginTeamsDialog(
    AppConfig config,
    IReadOnlyList<Account> accounts,
    IReadOnlyList<Character> characters)
{
    // existing body mostly unchanged; combo population changes per Step 9.3
    ...
}
```

Update the SettingsForm caller (search `new AutoLoginTeamsDialog(`):

```csharp
using var dlg = new AutoLoginTeamsDialog(_config, _pendingAccounts, _pendingCharacters);
```

- [ ] **Step 9.3: Populate slot combos with Characters + Accounts**

Find the block that adds items to each slot combo (likely `cboSlot.Items.Add(...)` patterns). Replace with a helper:

```csharp
private void PopulateSlotCombo(ComboBox cbo, IReadOnlyList<Account> accounts, IReadOnlyList<Character> characters)
{
    cbo.Items.Clear();
    cbo.Items.Add(new SlotOption("", "(none)", SlotKind.None));
    foreach (var c in characters)
    {
        cbo.Items.Add(new SlotOption(c.Name, $"\uD83E\uDDD9  {c.Name}  \u2192  enter world", SlotKind.Character));
    }
    foreach (var a in accounts)
    {
        cbo.Items.Add(new SlotOption(a.Name, $"\uD83D\uDD11  {a.Name}  \u2192  charselect only", SlotKind.Account));
    }
    cbo.DisplayMember = nameof(SlotOption.Display);
}

private enum SlotKind { None, Character, Account }

private sealed class SlotOption
{
    public string Value { get; }
    public string Display { get; }
    public SlotKind Kind { get; }
    public SlotOption(string value, string display, SlotKind kind)
    {
        Value = value;
        Display = display;
        Kind = kind;
    }
    public override string ToString() => Display;
}
```

Then call `PopulateSlotCombo(_cboTeam1Slot1, accounts, characters)` etc. for each of the 8 slot combos.

For each slot, pre-select the option whose `Value` matches `_config.Team{N}Account{M}` (case-insensitive; OrdinalIgnoreCase). If the raw value matches nothing, still allow it as a `(unresolved)` entry so the user can see and clear it.

- [ ] **Step 9.4: Add OK/WARN/FAIL indicator labels**

For each slot combo, add a sibling Label to the right:

```csharp
private readonly Label _lblTeam1Slot1Status = new() { Size = new Size(22, 22), TextAlign = ContentAlignment.MiddleCenter };
// ... 7 more for the other slots
```

Add a helper to recompute:

```csharp
private void RefreshSlotStatus(
    ComboBox cbo, Label status,
    IReadOnlyList<Account> accounts, IReadOnlyList<Character> characters)
{
    var opt = cbo.SelectedItem as SlotOption;
    if (opt == null || string.IsNullOrEmpty(opt.Value) || opt.Kind == SlotKind.None)
    {
        status.Text = "";
        status.BackColor = Color.Transparent;
        return;
    }

    // Resolution check (mirrors tray-submenu resolution).
    var ch = characters.FirstOrDefault(c => c.Name.Equals(opt.Value, StringComparison.Ordinal));
    var ac = accounts.FirstOrDefault(a => a.Name.Equals(opt.Value, StringComparison.Ordinal));

    if (ch != null)
    {
        status.Text = "\u2713";   // ✓
        status.BackColor = DarkTheme.StatusOk;
        status.ForeColor = Color.White;
        _toolTip.SetToolTip(status, "Resolves to Character — will enter world.");
    }
    else if (ac != null)
    {
        status.Text = "!";
        status.BackColor = DarkTheme.StatusWarn;
        status.ForeColor = Color.Black;
        _toolTip.SetToolTip(status, "Resolves to Account — will stop at charselect. Pick a Character for enter-world.");
    }
    else
    {
        status.Text = "\u2717";   // ✗
        status.BackColor = DarkTheme.StatusFail;
        status.ForeColor = Color.White;
        _toolTip.SetToolTip(status, "Doesn't match any Account or Character. Unbind or pick a valid target.");
    }
}
```

Subscribe `cbo.SelectedIndexChanged += (_, _) => RefreshSlotStatus(...)` for each slot + refresh once on open.

Add `DarkTheme.StatusOk` / `StatusWarn` / `StatusFail` to `UI/DarkTheme.cs` if not already defined. Suggested colors (add above the constructor area):

```csharp
public static readonly Color StatusOk   = Color.FromArgb(28, 110, 56);   // green
public static readonly Color StatusWarn = Color.FromArgb(210, 170, 40);  // amber
public static readonly Color StatusFail = Color.FromArgb(180, 40, 40);   // red
```

- [ ] **Step 9.5: Save path — write SlotOption.Value back into config**

Find the block that writes `_config.Team1Account1 = _cboTeam1Slot1.Text;` or similar. Change to:

```csharp
_config.Team1Account1 = ((SlotOption?)_cboTeam1Slot1.SelectedItem)?.Value ?? "";
```

Repeat for all 8 slot fields.

- [ ] **Step 9.6: Build + smoke check**

Run `dotnet build` — 0 errors. Launch debug. Settings → Accounts tab → `Manage Teams...` (from tray, not yet updated — use the existing path). Open AutoLoginTeamsDialog. Each slot combo should show Characters first, then Accounts. Status pill next to each slot.

- [ ] **Step 9.7: Commit**

```bash
git add UI/AutoLoginTeamsDialog.cs UI/SettingsForm.cs UI/DarkTheme.cs
git commit -m "$(cat <<'EOF'
feat(teams): AutoLoginTeamsDialog v4 signature + resolution indicators

Constructor signature: (AppConfig, IReadOnlyList<Account>, IReadOnlyList<Character>).
Callers pass _pendingAccounts + _pendingCharacters from SettingsForm.

Slot dropdowns list Characters first (preferred, enter-world) then
Accounts (charselect-only fallback). Each slot gets a colored status
pill:
 - green check  — slot resolves to a Character (enter world)
 - amber '!'    — slot resolves to an Account only (charselect)
 - red 'x'      — slot doesn't resolve to any target

Tooltips spell out what each pill means. Pills recompute on combo
change. Empty slots render no pill.

Team{N}AutoEnter checkboxes unchanged (Phase 3 binary semantics).
Save path pulls SlotOption.Value (canonical-cased) into TeamNAccountM
config fields.

New DarkTheme.StatusOk / StatusWarn / StatusFail colors for pill BG.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 10: Same-name balloon nudge

**Files:**
- Modify: `UI/SettingsForm.cs` — detect collisions in ApplySettings, raise event
- Modify: `UI/TrayManager.cs` — subscribe to the event, balloon on fire

- [ ] **Step 10.1: Add event + hash-dedup state to SettingsForm**

At the class-field section of `UI/SettingsForm.cs`, add:

```csharp
    private int _lastNameCollisionHash;

    /// <summary>
    /// Raised after a successful ApplySettings when Account.Name and Character.Name
    /// collide. Payload is a comma-separated list of collision names. Fires only
    /// when the collision set changes across saves (hash-deduped).
    /// </summary>
    public event Action<string>? OnSameNameCollision;
```

- [ ] **Step 10.2: Detect collisions at end of ApplySettings**

At the tail of `ApplySettings` (before `return true;`), add:

```csharp
        // Same-name nudge — non-blocking. Fires once per changed collision set.
        var collisions = _pendingAccounts
            .Where(a => _pendingCharacters.Any(c => c.Name.Equals(a.Name, StringComparison.Ordinal)))
            .Select(a => a.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (collisions.Count > 0)
        {
            var details = string.Join(", ", collisions);
            var hash = details.GetHashCode();
            if (hash != _lastNameCollisionHash)
            {
                _lastNameCollisionHash = hash;
                OnSameNameCollision?.Invoke(details);
            }
        }
        else
        {
            _lastNameCollisionHash = 0;
        }
```

- [ ] **Step 10.3: Subscribe in TrayManager.ShowSettings**

Open `UI/TrayManager.cs`. Find `ShowSettings` (line 1307). Between the `_settingsForm = new SettingsForm(...)` assignment and the `FormClosed +=` subscription, add:

```csharp
        _settingsForm.OnSameNameCollision += names =>
        {
            ShowBalloon(
                $"Account(s) '{names}' share names with Characters — consider renaming for tray-menu clarity.",
                BalloonKind.Warn);
        };
```

If `BalloonKind.Warn` doesn't exist, use the existing `ShowBalloon(string)` signature — grep `private void ShowBalloon` for the actual signature.

- [ ] **Step 10.4: Build + smoke**

Run `dotnet build` — 0 errors. Launch debug. Save Settings with current config (which has natedogg/flotte/acpots collisions). Expected: balloon fires once showing all three names. Save again without changes. Expected: no balloon (hash unchanged).

- [ ] **Step 10.5: Commit**

```bash
git add UI/SettingsForm.cs UI/TrayManager.cs
git commit -m "$(cat <<'EOF'
feat(settings): same-name balloon nudge on save

When an Account and Character share a Name (current config has three:
natedogg, flotte, acpots), the tray menu renders both under different
submenus with identical labels — confusing at a glance.

Phase 4 surfaces this as a non-blocking nudge after a successful Save.
SettingsForm raises OnSameNameCollision(namesCsv) only when the
collision set changes across saves (hash-deduped), so the balloon
doesn't spam every Apply.

TrayManager subscribes + balloons 'Account(s) X, Y, Z share names with
Characters — consider renaming for tray-menu clarity.'

User can rename either side via the respective Edit dialog; the nudge
disappears once the collision set clears.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase 4 review + ship

### Task 11: Dispatch three review agents in parallel

**Agents (all in parallel, single message with three Agent tool calls):**

1. **`pr-review-toolkit:code-reviewer`** — CLAUDE.md conventions, DarkTheme-only color usage, P/Invoke safety (no new DllImport), async patterns, conventional commits, naming. Focus files: Tasks 1-10 diff.
2. **`pr-review-toolkit:silent-failure-hunter`** — Cascade-delete edge cases (double-click Delete All while dialog is open, Cancel button doesn't close cleanly), DPAPI error paths (null EncryptedPassword, key rotation), combo-select-nothing on empty lists, ApplySettings validation ordering (does broken-FK fire before or after hotkey-conflict? the design says hotkey first, then structural), hotkey conflict modal dismissability, Username-read-only bypass paths, orphan-Character resurrection on reload.
3. **`feature-dev:code-reviewer`** — Second opinion on Settings UX + data invariants. Reverse-map correctness (round-trip invariant: load → save → reload produces identical config; load → edit → save → load → save produces stable JSON). Orphan state legitimacy (does the rest of the app handle `_config.Characters` with empty `AccountUsername` without crashing?). `Validate()` interaction.

Each prompt is self-contained, cites the spec path and the plan path, and lists the commit range (Phase 3.5 start to Phase 4 end).

- [ ] **Step 11.1: Compose self-contained agent prompts and dispatch in parallel**

Include this context in each prompt:
- Repo root: `X:/_Projects/EQSwitch/`
- Commit range: `git log HEAD~12..HEAD --oneline` (adjust number of commits after the fact)
- Spec: `docs/superpowers/specs/2026-04-15-eqswitch-phase4-settings-dual-section-design.md`
- Plan: `docs/superpowers/plans/2026-04-15-eqswitch-phase4-settings-dual-section.md`
- Out-of-scope: native/mq2_bridge.cpp (phantom-click gates must not change); migration fixtures (should still pass)
- Hard constraint: `grep -c "gameState == 5" Native/mq2_bridge.cpp` must stay at 2.

Run the three agents in parallel via a single message with three Agent tool calls.

### Task 12: Fold findings

**Files:** varies per finding.

- [ ] **Step 12.1: Triage findings by severity**

For each unique finding: assign severity (critical / high / medium / low). Critical + high must be folded pre-publish. Medium/low: either fold now or document as known issues for v3.10.1.

- [ ] **Step 12.2: Land fixes as atomic commits**

One commit per logical fix (grouping related fixes OK if they share a single root cause). Commit-title convention: `fix(settings): <issue summary>` or `chore(...)`. Each commit re-verifies build + fixtures + phantom-click gates.

### Task 13: Publish + deploy

**Files:** no source changes.

- [ ] **Step 13.1: Kill any running EQSwitch or eqgame processes that would lock the DLLs**

```bash
tasklist | grep -iE "eqgame|eqswitch"
# For each PID returned:
cmd.exe "/c taskkill /PID <pid> /F"
```

- [ ] **Step 13.2: Publish self-contained single-file release**

```bash
cd X:/_Projects/EQSwitch
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Expected output at `bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe` (~180 MB).

- [ ] **Step 13.3: Deploy to live directory**

```bash
cp bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe  C:/Users/nate/proggy/Everquest/EQSwitch/
cp bin/Release/net8.0-windows/win-x64/publish/eqswitch-hook.dll  C:/Users/nate/proggy/Everquest/EQSwitch/
cp bin/Release/net8.0-windows/win-x64/publish/eqswitch-di8.dll  C:/Users/nate/proggy/Everquest/EQSwitch/ 2>/dev/null || true
cp bin/Release/net8.0-windows/win-x64/publish/dinput8.dll  C:/Users/nate/proggy/Everquest/EQSwitch/ 2>/dev/null || true
```

Verify live exe matches publish output:
```bash
cmp bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe  C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe && echo "BYTE-IDENTICAL"
```

### Task 14: Nate-driven smoke test

**No source changes in this task.**

- [ ] **Step 14.1: Announce deploy + hand Nate the smoke-test checklist**

Launch live EQSwitch via direct exe invocation: `start C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe`.

Tell Nate the smoke test script:

1. Open Settings → Hotkeys tab. Confirm "Actions Launcher" header renders without the underlined `L`.
2. Focus Team 1 rebind field. Press Alt+M. Expected: no balloon, no launch.
3. Type a new combo for Team 1, hit Apply (stay in form). Focus Team 1 field again, press Alt+M. Expected: no launch.
4. Close Settings. Press the combo you bound to Team 1. Expected: Team 1 fires.
5. Open Settings → Hotkeys tab → bind `Alt+P` to PiP Toggle. Save. In-game: press Alt+P. Expected: PiP toggles.
6. Bind same `Alt+P` to AutoLogin 1 as well. Click Save. Expected: modal blocks save listing both bindings.
7. Open Settings → Accounts tab. Confirm two sections: Accounts (top), Characters (bottom).
8. Click `+ Add Account`. AccountEditDialog opens. Type a throwaway username + password. Save. Verify new row in Accounts grid.
9. Click Edit on the new row. Password field is blank with the "Leave blank to keep..." hint. Close without changes.
10. Click Delete on the new throwaway Account. Simple confirm modal. Confirm.
11. Click `+ Add Character`. CharacterEditDialog opens. Set Name = "test", pick any Account, DisplayLabel = "TestLabel", Notes = "hello". Save.
12. Close Settings. Reopen. Click Edit on "test". Assert: DisplayLabel = "TestLabel", Notes = "hello". Delete the test character.
13. Delete a real Account (e.g., create a throwaway one first linking to a dummy Account and use that). Cascade modal shows; test each button (Cancel, Unlink, Delete All) in separate runs.
14. From tray, right-click → confirm three submenus render (Accounts, Characters, Teams). Click one Account entry → verify balloon "stopping at charselect" and no phantom-click in `eqswitch-dinput8.log`.
15. Confirm `AutoLoginTeamsDialog` shows OK/WARN/FAIL pills correctly (contrive a slot with a Character name → green; a slot with an Account-only name → amber; a slot with a random string → red).

Nate signs off on each step. Any regression: stop and fix.

### Task 15: Memory file update + STOP

- [ ] **Step 15.1: Append Phase 4 status line to memory file**

Append to `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v3_10_0_account_split.md`:

```
- **2026-04-15 Phase 3.5 + Phase 4 shipped** — Hotkeys polish (ReloadConfig guard, PiP hotkey, conflict detection) + Settings dual-section UI (AccountEditDialog + CharacterEditDialog + CascadeDeleteDialog) + AutoLoginTeamsDialog v4 signature + OK/WARN/FAIL indicators + same-name balloon. N commits shipped (range XYZ..ABC). 3 agents reviewed; N findings folded. Build 0 errors + 1 expected Obsolete warning. Phantom-click gates at 2/1. 9 fixtures pass. Deploy at C:/Users/nate/proggy/Everquest/EQSwitch/ byte-identical to publish output. STOP for Phase 5 (hotkey families + team rebinding) sign-off.
```

Fill in the commit range from `git log` and the finding count from agent review.

- [ ] **Step 15.2: Write the 5-Phase-5 handoff stub**

Create `X:/_Projects/EQSwitch/PLAN_account_character_split_HANDOFF_phase5.md` — a short stub referencing the completed Phase 4 state and the deferred Phase 5 items from the master plan (WindowManager.cs:437-439 legacy read, AutoLoginTeamsDialog constructor signature migration if any legacy sites remain, AffinityManager.cs:134 CharacterAliases swap, CharacterSelector.cs extraction, AccountHotkeys/CharacterHotkeys dispatcher).

Use the same structure as `HANDOFF_phase4.md`:
- Current state (commit SHA + commit list + build/fixture/phantom-click state)
- Phase 5 scope (hotkey families + rebinding + WindowManager migration)
- Hard rules (carry forward)
- Required workflow

Commit: `docs(handoff): Phase 5 new-session prompt`.

- [ ] **Step 15.3: STOP**

No Phase 5 implementation until Nate explicitly signs off on the smoke-test results + gives a "go" signal.

Final tell-Nate message includes:
- List of shipped commits (copy from `git log --oneline main~N..main`)
- Link to the Phase 5 handoff file
- Final build + verification states: 0 errors, 1 Obsolete warning, 9 fixtures pass, phantom-click 2/1, deploy byte-identical.
- One-line summary: what changed + what's next.

---

## Self-Review (post-plan)

Before handing off to execution, check:

1. **Spec coverage:** Every section of the design spec maps to a Task. Phase 3.5 items P3.5-A/B/C/D → Tasks 1 + 2. Phase 4 file list → Tasks 3-10. Verification gate → Tasks 14 + agent review. Memory update → Task 15. ✓
2. **Placeholder scan:** Zero "TBD" / "add appropriate error handling" / "Similar to Task N". Each step shows actual code. ✓
3. **Type consistency:** `Account` / `Character` / `LoginAccount` / `CascadeDeleteChoice` used consistently across Tasks 3-10. `ApplySettings` returns `bool` from Task 2 onward, every caller updated. `_pendingAccounts` is `List<Account>` from Task 3 onward. ✓
4. **DarkTheme factory alignment gates:** Tasks 4, 5, 6 all have a sub-step (4.2, 5.2, 6.2) to cross-check factory method names against the actual `DarkTheme.cs` — acknowledges the risk that method names in the plan may not exactly match the codebase. ✓

---

**Plan complete.** Ready for execution.
