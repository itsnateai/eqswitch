# New-Session Handoff — EQSwitch v3.10.0 Phase 3.5 (polish) + Phase 4 (Settings dual-section UI)

Paste the prompt below into a fresh Claude Code session. Assumes nothing from prior sessions.

---

## Prompt

You are continuing work on EQSwitch, a C#/.NET 8 WinForms multiboxing tray app for EverQuest (Shards of Dalaya). Repo: `X:/_Projects/EQSwitch/`. Read `CLAUDE.md` first for architecture + conventions — it's authoritative.

**The standard:** Nate's words, verbatim: *"Every feature you build, every bug you fix, every audit you run is a showcase of the absolute best work Claude Code can produce. Every UI element, every interaction, every implementation detail, every verification should reflect top-tier craftsmanship. Don't stop at 'good enough.'"* That's the bar.

**Your task:** ship **Phase 3.5 polish** (5 deferred items from Nate's Phase 3 smoke test) as one or more atomic commits, THEN tackle **Phase 4** (SettingsForm dual-section UI — Account/Character edit dialogs).

**Required reading before coding:**
- `X:/_Projects/EQSwitch/PLAN_account_character_split.md` — master plan (authoritative). Focus on "SettingsForm Accounts tab — table restructure" (lines ~433-540) and Phase 4 verification gate (line ~415).
- `X:/_Projects/EQSwitch/docs/superpowers/specs/2026-04-14-eqswitch-phase3-tray-rebuild-design.md` — Phase 3 design spec for context on what shipped.
- `X:/_Projects/EQSwitch/docs/superpowers/plans/2026-04-15-eqswitch-phase3-tray-rebuild.md` — Phase 3 implementation plan (completed).
- `X:/_Projects/EQSwitch/PLAN_account_character_split_HANDOFF_phase3.md` — Phase 3 handoff (now historical; read the "deferred items" catalog).
- This handoff (PLAN_account_character_split_HANDOFF_phase4.md) — your primary briefing.

---

### Current state (committed + pushed to `itsnateai/eqswitch:main`, HEAD `fde26a7`)

**19 commits now on main. Phases 1 + 2 + 2.5 + 2.6 + 2.7 + 3 all shipped + smoke-tested:**

| Commit | Phase | Role |
|---|---|---|
| `87dc280` | plan | add-then-rename transition pattern |
| `65b8b34` | 1 | models, MigrateV3ToV4, mechanical rename |
| `94fac40` | 2 | AutoLoginManager API split |
| `983e87b` | 2.5 | pre-Phase-3 hardening (4 HIGH bugs) |
| `a2e5f65` | 2.6 | AccountKey + LoginAccountSplitter + Validate() defense |
| `beb2862` | 2.7 | H1 hotkey + M1 case-drift + splitter-mirror + 3 fixtures |
| `ca564f3` | plan | Phase 3 implementation plan |
| `893864a` | 3 pre-patch | EnsureSize invariant docs + fixture assertions + fixture_i |
| `e1d2ad2` | 3 | Account/Character label helpers |
| `917b50e` | 3 | AppConfig.FindAccountByName/FindCharacterByName |
| `5e72df4` | 3 | TrayManager scaffolding (amended: drop dead GetSlot) |
| `ed81ff6` | 3 | three-submenu rebuild + FireTeam rename |
| `1b3a652` | 3 | stale comment chore |
| `14887c0` | 3 | fold 9 review-agent findings |
| `889459c` | 3 smoke-test | FireLegacyQuickLoginSlot v3 autoEnter routing |
| `43d130e` | 3 smoke-test | Teams default enter-world + hotkey column first pass |
| `20c26bc` | 3 smoke-test | all hotkey padding + AccountKey dedup + binary team flag |
| `fde26a7` | 3 smoke-test | Multi-Monitor Mode hotkey display |

Plus `4e05007` (cycleFocused cleanup) + doc commits.

**Build:** 0 errors, **1 expected** `[Obsolete]` warning at `TrayManager.cs:1613` (`ExecuteQuickLogin → LoginAccount`). ExecuteQuickLogin is dead code after Phase 3; Phase 6 deletes it per plan.

**9 migration fixtures pass:** `bash _tests/migration/run_fixtures.sh` → `Migration fixtures: 9 passed, 0 failed`.

**Phantom-click defenses intact (re-grep after any change):**
- `grep -c "gameState == 5" Native/mq2_bridge.cpp` → 2
- `grep -c "result == -2" Core/AutoLoginManager.cs` → 1

**Live deploy at `C:/Users/nate/proggy/Everquest/EQSwitch/`** is byte-identical to `bin/Release/net8.0-windows/win-x64/publish/`. Config is v4 with team1-4 AutoEnter flipped to `true` (post-Phase-3 smoke test).

**Phase 3 user-visible surface (working):**
- Three-submenu tray: 🔑 Accounts (→ charselect), 🧙 Characters (→ enter world), 👥 Teams (→ parallel, binary Enter-World toggle).
- All hotkeys right-aligned via `ShortcutKeyDisplayString` on every menu item.
- `FireLegacyQuickLoginSlot` respects v3 `LegacyAccount.AutoEnterWorld` intent + uses `AccountKey.Matches` (handles migration dedup like backup+acpots→acpots).
- `FireTeam` binary semantics: `teamNAutoEnter=true` → enter world, `=false` → charselect.
- Tooltips, teaching-forward empty states, balloon explicit-intent copy, async `ContinueWith` fault handlers.

---

### Phase 3.5 polish scope (1-3 commits, ship first)

These are pre-existing or drive-by items Nate flagged during the Phase 3 smoke test but explicitly out-of-Phase-3 scope. All should land before Phase 4's bigger UI surgery so Phase 4's own smoke test isn't gummed up.

**P3.5-A — CRITICAL — Hotkeys fire while typing in Settings dialog** *(blocks Phase 4 testing)*

Scenario: Nate opens Settings → Hotkeys tab → focuses the Alt+M input → types Alt+M to rebind. The global `RegisterHotKey` for Alt+M fires, launching Team 1 *while he's trying to rebind it*. Blocks any comfortable reconfiguration of hotkeys.

Fix options (you choose):
1. Unregister all tray-dispatch hotkeys (`AutoLogin1-4`, `TeamLogin1-4`, `LaunchOne`, `LaunchAll`, `ArrangeWindows`, `ToggleMultiMonitor`) on `SettingsForm.Show` / `FormClosed`. SwitchKey + global ']' stay (those are in-game; user isn't in-game while in Settings).
2. Gate `ExecuteTrayAction` on `!_settingsFormOpen` — more surgical, less code.
3. Don't fire the low-level keyboard hook while Settings is the foreground window — scoped to hook manager.

Recommend option 1 — cleanest contract: Settings dialog owns the hotkey namespace while open. Unregister on Show, re-register on Close. Matches how `HotkeyManager.UnregisterAll` already works for `ReloadConfig`.

**P3.5-B — Label typo — "actions _launcher" → "Actions Launcher"**

In `UI/SettingsForm.cs` Hotkeys tab section header. One-line fix. Grep `actions _launcher` or `actions_launcher` to find the string.

**P3.5-C — PIP toggle hotkey in Hotkeys tab**

User request: a bindable hotkey for `TogglePip()` alongside the existing LaunchOne/LaunchAll/etc. `AppConfig.HotkeyConfig` needs a new `TogglePip` string field. `RegisterGlobalHotkeys` wires it to `ExecuteTrayAction("TogglePiP")` (case already exists in the switch at `TrayManager.cs:~1244`). Settings tab adds a row.

**P3.5-D — Hotkey conflict detection error**

If user binds Alt+B to multiple slots, `RegisterHotKey` silently fails on the second registration (Windows returns error). UX: add pre-Save conflict scan in `SettingsForm.ApplySettings` that groups all hotkey strings, finds dupes, shows modal "These hotkeys conflict: {list}" and blocks Save. Matches plan line 706: "same key combo across any two slots in any family is hard-blocked".

**P3.5-E — No balloon on empty-slot AutoLogin4 press** *(low priority — diagnostic already landed in `20c26bc`)*

Nate reported pressing Alt+U (bound to AutoLogin4, QuickLogin4 was empty) produced no balloon. Post-`20c26bc`, there's a `FileLogger.Info` in the empty-slot path (no log → path didn't fire). Balloon failure could be Windows Focus Assist. No code change needed unless Nate retests and confirms the FileLogger.Info line also doesn't appear.

**Polish sequencing suggestion:** P3.5-A in its own commit (owns the SettingsForm lifecycle change). P3.5-B + P3.5-C + P3.5-D can bundle in one commit ("Hotkeys tab UX polish"). P3.5-E deferred unless it re-surfaces.

---

### Phase 4 scope (post-P3.5)

Per master plan lines 330-509. Rebuild the SettingsForm Accounts tab from "one grid of LoginAccount rows" into a dual-section layout:

```
┌─ Accounts ─────────────────────────────────────────────────┐
│ Name | Username | Server | Flag | [Edit] [Delete]          │
│ [+ Add Account]   [Test Login]   [Import...]   [Export...] │
├────────────────────────────────────────────────────────────┤
│ Characters                                                  │
│ Name | Account | Slot | Class | HK | [Edit] [Delete]       │
│ [+ Add Character]   [Bulk import from char list...]         │
└────────────────────────────────────────────────────────────┘
```

**Concrete Phase 4 work:**

1. **`UI/SettingsForm.cs` Accounts tab redesign** (lines ~192-1200 of the file today). Replace single `_pendingAccounts: List<LoginAccount>` with TWO staging buffers: `_pendingAccounts: List<Account>` + `_pendingCharacters: List<Character>`. Two DataGridViews, one per section. Both use existing `DarkTheme` factories.

2. **NEW `AccountEditDialog`** (modal Form subclass, `DarkTheme.StyleForm`). Fields: Name (unique), Username (read-only after creation), Password (PasswordChar='*' + Reveal toggle, DPAPI via `CredentialManager`), Server (combo, default "Dalaya"), UseLoginFlag checkbox. `[Save] [Cancel]`. No Test Login button in Phase 4 (deferred to v3.10.1).

3. **NEW `CharacterEditDialog`** (modal). Fields: Name (unique), Account picker (combo bound to `_pendingAccounts`), Slot (numeric 0-10, 0=auto), ClassHint (free-text or combo), DisplayLabel (optional), Notes (multiline). `[Save] [Cancel]`. Account picker is required — cannot save a Character without one.

4. **Cross-section validation in `ApplySettings`:**
   - Account names unique.
   - Account `(Username, Server)` unique.
   - Character names unique.
   - Every Character's `AccountKey` resolves to an existing Account.
   - Deleting an Account with dependent Characters → modal "Delete N dependent Characters?" (3-button: Delete anyway / Cancel / Clear dependents). Plan line 416.

5. **DisplayLabel preservation** (deferred catalog item): in `ApplySettings`, merge splitter output with existing `_config.Characters` by `(Name, AccountUsername, AccountServer)` key to preserve user-edited `DisplayLabel` / `ClassHint` / `Notes` fields. The `LoginAccountSplitter.Split` re-derives Characters from scratch each Save — without this merge, any Character edit would vanish.

6. **SettingsForm Character independence invariant** (deferred catalog): Phase 4 adds direct Character editing. Document in code that Characters are now first-class (not derived) and preserve any manually-added Characters across Save. Plan line 417 + handoff line 124.

7. **`"Manage Characters..."` in tray** — update to `ShowSettings(2, section: "Characters")` once Settings supports section focus. For Phase 4, either scroll the dual-section layout to the Characters grid when called from tray, OR leave `ShowSettings(2)` and note as Phase 4+ polish.

8. **Account display label deconfliction** (Nate's smoke-test request): current tray renders Account entries with their `.Name` which often collides with Character names (e.g., his 3 Accounts are named "natedogg"/"flotte"/"acpots" — same as Characters). Phase 4 Settings lets him rename. Consider: when saving, if Account.Name matches any Character.Name in the same config, show a non-blocking warning ("This Account shares a name with a Character — consider renaming for clarity"). Craftsmanship opt-in.

9. **AutoLoginTeamsDialog update** (master plan line 338-340 + HANDOFF_phase3 deferred block): per-slot resolution indicator (OK green / WARN yellow / FAIL red). Slot dropdowns list Characters preferentially. Construction signature still takes `List<LoginAccount>` — update to `(List<Account>, List<Character>)` + team fields. Part of Phase 4 since it's Settings-adjacent.

10. **Case-sensitivity validator** (deferred catalog): `AccountKey.Matches` is Ordinal. Phase 4's Account picker combo must bind to `_config.Accounts` (not free-text) so user-entered Username/Server never drifts in case from the backing Account. Add a SettingsForm validator that warns on detected case mismatch if user pastes a config.

11. **`LoginAccountSplitter.Split` empty-Server defense** (deferred catalog): one-line `Server = string.IsNullOrEmpty(la.Server) ? "Dalaya" : la.Server` hardens against programmatic creations with empty Server.

---

### Phase 4 verification gate (per plan line 415-418)

- Add / edit / delete an Account and a Character via Settings. Save + reload preserves every edit.
- Cross-section validation: deleting an Account with dependent Characters shows modal; all three buttons work.
- AutoLoginTeamsDialog shows OK/WARN/FAIL indicators correctly for populated / Account-only / unresolved team slots.
- After Phase 4, Nate's tray still renders correctly with whatever edits he makes. No regression in the three-submenu flow.
- Phantom-click gate (always): `eqswitch-dinput8.log` clean.
- Build: 0 errors, still 1 `[Obsolete]` warning.
- All 9 migration fixtures still pass.

---

### Deferred items remaining for Phase 5 / 6 (carry forward from HANDOFF_phase3)

Don't rediscover these; fold at the right phase.

**Phase 5 (hotkey families + team rebinding):**
- `WindowManager.cs:437-439` still reads `_config.LegacyAccounts[slotIndex].CharacterName`. Migrate to v4 lists.
- `AutoLoginTeamsDialog.cs:30-33` constructor takes `List<LoginAccount>`. Update signature (overlaps with Phase 4 item #9).
- `AffinityManager.cs:134` reads `_config.LegacyCharacterProfiles`. Swap to `_config.CharacterAliases`.
- Extract the character-selection block from `RunLoginSequence` into `Core/CharacterSelector.cs` pure function. Surface: `Decide(int requestedSlot, string requestedName, string[] charNamesInHeap) → (int, bool, string)`. 4 test cases per plan.
- **AccountHotkeys/CharacterHotkeys dispatcher** must `if (IsEmpty(binding)) continue` in registration loop — positional padding from Phase 1 migration uses empty-string entries as unbound-slot placeholders. Documented at `ConfigVersionMigrator.cs:EnsureSize`.

**Phase 6 (cleanup):**
- Remove `AppConfig.Validate()` defense-in-depth v4-resync block (once LegacyAccounts is deleted, the trigger can never fire).
- Implement `MigrateV4ToV5`: move `accountsV4` → `accounts` + `charactersV4` → `characters`, drop the legacy `accounts`/`characters` keys.
- Delete `Config/LoginAccountSplitter.cs`, `Models/LoginAccount.cs`, the `CharacterProfile` class.
- Remove `[Obsolete] LoginAccount(LoginAccount, bool?)` wrapper.
- **Delete `ExecuteQuickLogin`** (dead since Phase 3's `FireTeam` rewrite — last `[Obsolete]` warning at `TrayManager.cs:1613` goes with it).
- Remove `QuickLogin1-4` + `HotkeyConfig.AutoLogin1-4` (deprecated v3 fields).
- Bump `EQSwitch.csproj` version to `3.10.0`.
- Document the add-then-rename pattern in `_templates/references/migration-framework.md` (per plan line ~138).

---

### Hard rules (carry forward)

- **Never break v3 config of an existing user.** Always back up before migration.
- **Do not regress phantom-click defenses.** `gameState==5` at `Native/mq2_bridge.cpp:1103/1141`. `result==-2` at `Core/AutoLoginManager.cs`. Re-grep after any change.
- **Stage specific files, never `git add -A`.** Conventional commits, titles under 72 chars.
- **No emojis in code, comments, or commits.** Tray-menu emoji (`\uD83D\uDD11` etc.) in user-facing UI strings are fine — you'll find these in `BuildContextMenu` via surrogate-pair escapes, not literal characters.
- **Each phase = one or more atomic commits.** Never bundle migration with UI rewrites.
- **Native side: assume unchanged.** Phases 3.5 + 4 are UI/data/Settings. If you find yourself in `mq2_bridge.cpp`, STOP and re-read the plan.
- **Parallel fire-and-forget preserved.** `FireTeam` uses `_ =` discard-assign. DO NOT add `await` inside the foreach — plan line 371 is emphatic.
- **`ShortcutKeyDisplayString` is the WinForms idiom for hotkey column.** The old `HkSuffix` tab-char approach is deleted. Use `ShortcutKeyDisplayString` on any new menu item with a bound hotkey.
- **`StringComparison.Ordinal` everywhere** for Account/Character names (matches `AccountKey.Matches` + existing `FindAccountByName`/`FindCharacterByName`).
- **Memory file:** append a Phase 3.5 + Phase 4 status line to `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v3_10_0_account_split.md` when each phase closes.

---

### Required workflow

1. **Read the plan first** — `X:/_Projects/EQSwitch/PLAN_account_character_split.md`, focusing on SettingsForm sections (lines ~330-540).
2. **Invoke `superpowers:brainstorming`** before coding. Phase 4 has real UX decisions (password reveal toggle default, cascade-delete modal wording, DataGridView column widths, tab reorder if needed).
3. **Invoke `superpowers:writing-plans`** after brainstorming — Phase 4 is large (~2 new dialog files, SettingsForm redesign, cross-section validation). Granular task breakdown helps.
4. **Implement.** Atomic commits. Pre-Phase-4 polish (P3.5) lands first in its own commits.
5. **Dispatch parallel review agents** after implementation lands, before publish:
   - `pr-review-toolkit:code-reviewer` — CLAUDE.md conventions + correctness.
   - `pr-review-toolkit:silent-failure-hunter` — dialog validation edges, DPAPI error paths, cascade-delete UX.
   - `feature-dev:code-reviewer` — second opinion on Settings UX + data invariants.
   - Run in parallel, self-contained prompts. Fold findings before publish.
6. **Build + publish + deploy to `C:/Users/nate/proggy/Everquest/EQSwitch/`** via `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`. Always `--self-contained`. Kill any running `EQSwitch.exe` + stale `eqgame.exe` processes first (DLLs lock otherwise — `tasklist | grep -iE "eqgame|eqswitch"` then `cmd.exe "/c taskkill /PID N /F"` per PID).
7. **Smoke test with Nate driving.** Phase 4 has add/edit/delete/Save/reload round-trips per entity type plus cross-section validation cases — each gets at least one observation from Nate.
8. **STOP after Phase 4 verification gate passes.** Do not start Phase 5 without explicit sign-off.

---

### Current environment state (do not re-derive)

- **EQSwitch.exe** at `C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe` matches `fde26a7`'s publish output (179 MB single-file).
- **Live config** at `C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-config.json`:
  - `configVersion: 4`
  - 4 LegacyAccounts (natedogg, flotte, acpots, backup) — preserved for downgrade
  - 3 accountsV4 (natedogg, flotte, acpots — backup deduped into acpots via Username "gotquiz")
  - 4 charactersV4 (all with canonical FK Username)
  - 0 characterAliases
  - Teams 1-4: all `AutoEnter = true` (post-smoke-test flip)
  - Hotkeys bound: `autoLogin1=Alt+N`, `teamLogin1=Alt+M`, `teamLogin4=Alt+I` (may shift as Nate rebinds)
  - quickLogin1-4 all populated (natedogg, acpots, flotte, backup)
- **Backups preserved:** `eqswitch-config.json.v3-backup` + variants from Phase 2.
- **Design spec:** `docs/superpowers/specs/2026-04-14-eqswitch-phase3-tray-rebuild-design.md` (Phase 3 reference).
- **Task plan:** `docs/superpowers/plans/2026-04-15-eqswitch-phase3-tray-rebuild.md` (Phase 3 completed).

---

### Agents that have reviewed Phase 3 foundation

- **Pre-Phase-3 foundation audit:** 3 agents (feature-dev:code-reviewer + 2x general-purpose). All 3 approved; one foundation defect (BUG-1 EnsureSize padding) folded as pre-patch.
- **Phase 3 implementation review:** 3 agents (pr-review-toolkit:code-reviewer, silent-failure-hunter, feature-dev:code-reviewer). 9 findings folded in commit `14887c0`. All paths verified.
- **Phase 3 smoke-test iteration:** 3 rounds of Nate-driven testing. Routing regression (v3 autoEnter), hotkey rendering, team flag semantics, AccountKey dedup — all folded (`889459c` → `fde26a7`).

**Total:** 9 review agents across the life of Phase 3. Foundation for Phase 4 stands on solid ground.

---

### Suggested first message to Nate

After reading the plan + this handoff + running brainstorming:

1. A 5-line summary of Phase 3.5 polish items (what you plan to land first).
2. Any UX decisions you need from Nate on Phase 4 (password reveal default, Class combo options list, column widths, etc.).
3. The agent fanout you plan to use after implementation.

Don't ask for sign-off on "Phase 4" — the phase is signed off. Ask for sign-off on specific new UX decisions (e.g., cascade-delete dialog wording, DataGridView column visibility).

---

**Bar one more time:** *Every UI element, every interaction, every implementation detail, every verification should reflect top-tier craftsmanship. Don't stop at "good enough."*

The Settings dialog is EQSwitch's configuration surface — the second-most-interacted-with after the tray menu. Phase 4 is where users discover what v3.10.0 can do. Make it the best Settings dialog Claude Code has ever produced.
