# New-Session Handoff — EQSwitch v3.10.0 Phase 3 + Deferred Items

Paste the prompt below into a fresh session. It assumes nothing from the conversation that produced it.

---

## Prompt

You are continuing work on EQSwitch, a C#/.NET 8 WinForms multiboxing tray app for EverQuest (Shards of Dalaya). Repo: `X:/_Projects/EQSwitch/`. Read `CLAUDE.md` first for architecture and conventions — it is authoritative.

**The standard:** Nate's words, verbatim: *"Every feature you build, every bug you fix, every audit you run is a showcase of the absolute best work we can produce. Every UI element, every interaction, every implementation detail, every verification should reflect top-tier craftsmanship. Don't stop at 'good enough.'"* That is the bar. Hit it.

**Your task:** implement Phase 3 of `PLAN_account_character_split.md`, then address the deferred items accumulated during Phases 1-2 before starting Phase 4. Full plan (authoritative): `X:/_Projects/EQSwitch/PLAN_account_character_split.md`. This handoff supplements the plan; read both.

---

### Current state (committed + pushed to `itsnateai/eqswitch:main`)

| Commit | Role | Status |
|---|---|---|
| `87dc280` | Plan doc with add-then-rename transition pattern | shipped |
| `65b8b34` | **Phase 1** — models, MigrateV3ToV4, mechanical consumer rename | shipped + smoke-tested |
| `94fac40` | **Phase 2** — AutoLoginManager API split (LoginToCharselect, LoginAndEnterWorld, [Obsolete] LoginAccount wrapper) | shipped + smoke-tested |
| `983e87b` | **Phase 2.5** — AccountKey value type, V3Row record, abort-on-character-miss | agent-reviewed |
| `a2e5f65` | **Phase 2.6** — LoginAccountSplitter helper, v4 sync on Settings save, Validate() defense-in-depth | agent-reviewed |
| `beb2862` | **Phase 2.7** — H1 hotkey routing fix, M1 case-drift fix, splitter-mirror invariant, 3 new fixtures | agent-reviewed |

**Foundation state:**
- Build: 0 errors, 2 expected `[Obsolete]` warnings at `UI/TrayManager.cs:817` and `:1330`.
- 8 migration fixtures pass (5 original + `fixture_f` unresolved hotkey + `fixture_g` H1 regression + `fixture_h` M1 regression) with splitter-mirror invariant on every fixture.
- Live deployment at `C:/Users/nate/proggy/Everquest/EQSwitch/` is byte-identical to `bin/Release/net8.0-windows/win-x64/publish/`.
- Nate's live config is migrated to v4 (configVersion=4, 3 accountsV4, 4 charactersV4, 0 characterAliases, 4 LegacyAccounts preserved for downgrade). All `autoEnterWorld` flags set to `false` (clean baseline for Phase 3 testing). Backup at `eqswitch-config.json.v3-backup`.

**Tray flow smoke-tested:** Clicking `backup` in the legacy Accounts submenu stops at charselect correctly. Phase 3 must preserve this behavior for identical configs while rebuilding the menu structure.

---

### Phase 3 scope (per plan lines ~307-320, 376-382)

Rewrite `UI/TrayManager.cs` into the three-submenu intent-explicit structure. The plan's mockup is:

```
⚔  EQ Switch v3.10.0
─────────────────────────
⚔  Launch Client                        (bare eqgame.exe patchme — kept)
🎮  Launch Team                Ctrl+Alt+1  (Team 1 one-click default — kept)
─────────────────────────
🔑  Accounts ▸                           (login + stop at charselect)
    👤  Main Account          Ctrl+1
    👤  Alt Account           Ctrl+2
    ─────────────
    ⚙  Manage Accounts...
🧙  Characters ▸                         (login + enter world)
    🧙  Backup    (Cleric)     Alt+1
    🧙  Healpots  (Cleric)     Alt+2
    🧙  Acpots    (Rogue)      Alt+3
    🧙  Staxue    (Ranger)
    ─────────────
    ⚙  Manage Characters...
👥  Teams ▸                              (multi-client, parallel launch)
    🚀  Auto-Login Team 1     Ctrl+Alt+1  (also root button)
    🚀  Auto-Login Team 2     Ctrl+Alt+2
    🚀  Auto-Login Team 3     Ctrl+Alt+3
    🚀  Auto-Login Team 4     Ctrl+Alt+4
    ─────────────
    ⚙  Manage Teams...
─────────────────────────
[existing Clients, Process Manager, Video, Settings, Help, Exit]
```

**Concrete Phase 3 work:**

1. **`BuildContextMenu()`** (starts at `TrayManager.cs:~775`):
   - Keep root `Launch Client` and `Launch Team` buttons.
   - REPLACE the current single "Login" submenu at line 807 (which mixes accounts and teams) with three distinct submenus: `🔑 Accounts`, `🧙 Characters`, `👥 Teams`.
   - Extract helpers: `BuildAccountsSubmenu()`, `BuildCharactersSubmenu()`, `BuildTeamsSubmenu()`. Per the plan (line 309) and code-explorer agent's recommendation, take the list as an argument (`BuildAccountsSubmenu(List<Account>)`) so rendering can be unit-smoked with synthetic lists in the future.

2. **Accounts submenu** reads `_config.Accounts` (NEW `List<Account>` type, already populated by migration). Each item fires `_autoLoginManager.LoginToCharselect(account)`. Label fallback: `account.Name ?? account.Username`. Tooltip: `username@server`. Empty state: `"No accounts yet — Manage Accounts..."`.

3. **Characters submenu** reads `_config.Characters` (NEW `List<Character>`). Each item fires `_autoLoginManager.LoginAndEnterWorld(character)`. Label: `character.DisplayLabel ?? character.Name`, optionally with class decoration `"Name (Class)"` if `ClassHint` is set. Tooltip: `→ Account 'AccountName' · slot auto/N`. Empty state: `"No characters yet — characters added here will auto-enter-world"`. Plan (line 293) recommends grouping by Account when count > 10 — defer the grouping decision until you see the real list length in Nate's data (4 chars; grouping not needed yet).

4. **Teams submenu** shows all 4 teams (when populated). Team 1 stays accessible from both root "Launch Team" button AND this submenu. Each team item fires `ExecuteTrayAction("LoginAll" | "LoginAll2" | ...)` — teams still route through the `[Obsolete]` wrapper via `ExecuteQuickLogin` until Phase 5. That's intentional.

5. **Rename `FireTeamLogin` → `FireTeam`** at line 1339 — preserve the **parallel fire-and-forget** semantics (`_ = ExecuteQuickLogin(...)` inside the foreach; DO NOT add `await`). Plan lines 291, 484 are emphatic: sequential awaits would re-expose race conditions.

6. **"Manage Accounts..." and "Manage Characters..."** entries both call `ShowSettings(2)` (the Accounts tab index at line 841). Phase 4 will add an inner-section focus parameter — for Phase 3 just open the tab.

7. **Tray label + tooltip helpers** — agent recommendation (code-explorer, feature-dev): codify the fallback chains as instance/extension methods on `Account` and `Character` so tray, settings, and AutoLoginTeamsDialog share one source. Cheap now, expensive to retrofit in Phase 4:
   ```csharp
   // Models/Account.cs (add property or extension method)
   public string EffectiveLabel => string.IsNullOrEmpty(Name) ? Username : Name;
   public string Tooltip => $"{Username}@{Server}";

   // Models/Character.cs
   public string EffectiveLabel => string.IsNullOrEmpty(DisplayLabel) ? Name : DisplayLabel;
   public string LabelWithClass => string.IsNullOrEmpty(ClassHint) ? EffectiveLabel : $"{EffectiveLabel} ({ClassHint})";
   ```

8. **Lookup helpers** — agent recommendation: add `FindAccountByName(string)` and `FindCharacterByName(string)` to `AppConfig` or a static helper. You'll need them for hotkey dispatch in Phase 5 and for tray-click action strings (`"AutoLogin1".."AutoLogin4"` in `ExecuteTrayAction`). Write them now so they're in one place.

9. **Expected outcome:** After Phase 3, the warning at `TrayManager.cs:817` should DISAPPEAR (that call site migrates to `LoginToCharselect` / `LoginAndEnterWorld`). The warning at `:1330` (`ExecuteQuickLogin` for team launches) stays until Phase 5.

10. **Verification gate per plan line 405:**
    - Each of the three launch modes fires correctly from tray: bare → no login, Account → stops at charselect, Character → enters world.
    - Root "Launch Team" button still fires Team 1 as before.
    - New Teams submenu launches Teams 1-4 correctly.
    - Phantom-click gate: `grep XWM_LCLICK C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-dinput8.log | grep -A2 "gameState -> 5"` is empty after all menu-driven launches.

---

### Deferred items from agent reviews — address BEFORE/DURING the phase the agent targeted

These are items that were found during Phase 1-2.7 reviews and explicitly deferred. Don't rediscover them; fold them in at the right phase.

**During Phase 3 itself:**
- Pull out `BuildAccountsSubmenu` / `BuildCharactersSubmenu` / `BuildTeamsSubmenu` as list-arg helpers (not `_config`-reaching). Feature-dev code-explorer agent's recommendation.
- Add `Account.EffectiveLabel`, `Account.Tooltip`, `Character.EffectiveLabel`, `Character.LabelWithClass` — see #7 above.
- Add `AppConfig.FindAccountByName(string)` / `FindCharacterByName(string)` — see #8 above.
- Guard `_config.Characters.Count == 0` gracefully in `BuildCharactersSubmenu`. Don't render an empty submenu without a hint.

**During Phase 4 (SettingsForm dual-section UI):**
- **DisplayLabel preservation** (feature-dev review L1): `LoginAccountSplitter.Split` re-derives Characters from scratch each save, dropping any `DisplayLabel` the user set. Currently not triggerable (LoginAccount has no DisplayLabel field). Becomes real the moment Phase 4 wires Character editing. Fix: in `SettingsForm.ApplySettings`, merge splitter output with existing `_config.Characters` by `(Name, AccountUsername, AccountServer)` key to preserve user-edited fields before writing.
- **SettingsForm Character independence invariant** (code-reviewer): every `_config.Characters` entry currently derives from a `_pendingAccounts` row. The moment Phase 4 adds direct Character editing, this breaks — orphan Characters (ones without a backing LoginAccount) would be silently dropped on save. Document the invariant AND update `ApplySettings` to preserve manually-added Characters.
- **Case-sensitivity in `AccountKey.Matches`** (code-reviewer L2): currently `StringComparison.Ordinal`. Phase 4's Account picker combo must bind to `_config.Accounts`, not free-text, so user-entered Username/Server never drifts in case from the backing Account. Add a SettingsForm validator that warns on detected case mismatch if a user imports/pastes config.
- **`LoginAccountSplitter.Split` empty-Server defense** (code-reviewer): if a programmatic `new LoginAccount { Server = "" }` reaches the splitter, it produces an Account with empty Server. One-line `Server = string.IsNullOrEmpty(la.Server) ? "Dalaya" : la.Server` hardens it. Theoretical but cheap.

**During Phase 5 (hotkey families):**
- `WindowManager.cs:437-439` still reads `_config.LegacyAccounts[slotIndex].CharacterName` for window title templating. Migrate to the v4 lists.
- `AutoLoginTeamsDialog.cs:30-33` still takes `List<LoginAccount>` via constructor. Update signature to take `List<Account> + List<Character>` and display both. Slot picker should show both Accounts and Characters with distinct prefixes.
- `AffinityManager.cs:134` still reads `_config.LegacyCharacterProfiles`. Swap to `_config.CharacterAliases`.
- Extract the character-selection block from `RunLoginSequence` into a `Core/CharacterSelector.cs` pure function (test-analyzer agent's recommendation). This is the block around `AutoLoginManager.cs:~430-475`. Surface: `Decide(int requestedSlot, string requestedName, string[] charNamesInHeap) → (int selectedSlot, bool abort, string reason)`. Cover 4 cases with a scripted test: slot-mode-with-named-miss (no abort), named-mode-with-character-present, named-mode-with-character-missing (abort), slot-exceeds-count (abort). Gives regression coverage for the abort-on-miss fix that currently exists only in runtime code.

**During Phase 6 (cleanup):**
- Remove `AppConfig.Validate()` defense-in-depth v4-resync block. Once `LegacyAccounts` is deleted, the trigger condition can never fire. Delete it with a comment noting when/why it existed.
- Implement `MigrateV4ToV5` that moves `accountsV4` → `accounts` + `charactersV4` → `characters` in JSON, drops the orphaned `accounts` (legacy) and `characters` (legacy CharacterProfile) keys. Per plan line 383 and beyond.
- Delete `Config/LoginAccountSplitter.cs` (no more LoginAccount source type to split from).
- Delete `Models/LoginAccount.cs`, `Config/CharacterProfile` class, `LegacyAccounts`/`LegacyCharacterProfiles` fields on AppConfig.
- Remove `[Obsolete] LoginAccount(LoginAccount, bool?)` wrapper from `AutoLoginManager`.
- Remove `QuickLogin1-4` + `HotkeyConfig.AutoLogin1-4` (deprecated fields).
- Bump version in `EQSwitch.csproj` to `3.10.0`.
- Document the add-then-rename pattern in `_templates/references/migration-framework.md` for reuse on future schema migrations (per plan line ~138).

---

### Hard rules (carry forward from Phase 1 handoff)

- **Never break the v3 config of an existing user.** Always back up before migration. Maintain a v3-restore path until v3.11.0 ships.
- **Do not regress the phantom-click defenses.** `gameState==5` at `Native/mq2_bridge.cpp:1103/1141`. `result==-2` handler at `Core/AutoLoginManager.cs:~519`. After any change, re-grep for both and confirm bytes are identical.
- **Stage specific files, never `git add -A`.** Conventional commits. Under 72-char title.
- **Each phase = one or more atomic commits.** Never bundle migration code with UI rewrites.
- **No emojis in code, comments, or commits.** Tray-menu emoji (🔑 🧙 👥 ⚔ 🎮 🚀 👤 ⚙) are user-facing UI strings per plan line 211 — those are fine.
- **Native side: assume unchanged.** If you find yourself in `mq2_bridge.cpp` or `eqswitch-di8.cpp`, STOP and re-read the plan — Phase 3 is a UI/data concern only.
- **Memory file:** append a Phase 3 status line to `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v3_10_0_account_split.md` when the phase closes.

---

### Required workflow

1. **Read the plan first** — `X:/_Projects/EQSwitch/PLAN_account_character_split.md`. Pay attention to the "Tray menu — new structure" section (line ~179), "File-by-file change list" (line ~219), "Nuances to watch" (line ~288), and the Phase 3 verification gate (line ~332).
2. **Invoke `superpowers:brainstorming`** before coding. Even though the plan is exhaustive, brainstorming surfaces intent gaps and UI/UX decisions specific to how Nate uses the tray. Ask about: grouping strategy (flat vs by-Account when char count > 10), hotkey suffix rendering (`HkSuffix` helper at existing code — extend to new families), empty-state copy.
3. **Invoke `superpowers:writing-plans`** to produce a granular Phase 3 implementation plan if you need more than the plan's current specificity — the build sequence in the main plan is prose-level; a task-level breakdown may help if you find yourself thrashing.
4. **Implement.** Atomic commits. Each gate passed before the next starts.
5. **Dispatch parallel review agents** after implementation lands but before publishing:
   - `pr-review-toolkit:code-reviewer` — general correctness + CLAUDE.md conventions.
   - `pr-review-toolkit:silent-failure-hunter` — tray click handlers, empty-state edges, error balloon UX.
   - `feature-dev:code-reviewer` — independent second opinion, focused on the tray menu rebuild surface.
   Run them in parallel with self-contained prompts. Fold findings back into the implementation. Phase 3 is UI-facing — reviewers will catch UX smells you'll miss.
6. **Build + publish + deploy to `C:/Users/nate/proggy/Everquest/EQSwitch/`**. Use the CLAUDE.md publish command: `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`. Always `--self-contained` (the memory pin on deploy workflow is explicit).
7. **Smoke test with Nate driving.** Tell him exactly what to click and what to expect. Phase 3 has THREE launch paths (bare, charselect, enter-world) and TWO menu paths per target (Accounts/Characters/Teams submenu). Each needs at least one observation from Nate before sign-off.
8. **STOP after Phase 3 verification gate passes.** Don't start Phase 4 without explicit sign-off.

---

### Current environment state (do not re-derive)

- **EQSwitch.exe** at `C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe` is byte-identical to `bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe` from commit `beb2862`. Native DLLs (`eqswitch-di8.dll`, `eqswitch-hook.dll`) match too.
- **Live config** at `C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-config.json` is v4, clean baseline:
  - `configVersion: 4`
  - Global `autoEnterWorld: false`
  - 4 `LegacyAccounts` (natedogg, flotte, acpots, backup — all `autoEnterWorld: false`)
  - 3 `accountsV4` (natedogg, flotte, acpots — gotquiz deduped)
  - 4 `charactersV4` (natedogg, flotte, acpots, backup — backup's FK points at acpots's canonical Account)
  - 0 `characterAliases`
- **Backups preserved:**
  - `eqswitch-config.json.v3-backup` — pre-migration v3 snapshot
  - `eqswitch-config.json.before-flag-reset` — post-migration, pre-flag-zeroing snapshot
  - `eqswitch-config.json.pre-validate-test` — pre-Validate()-defense test snapshot

---

### Agents that have reviewed the foundation (consensus: GO for Phase 3)

- Phase 1+2 first review: 3 agents (code-reviewer, silent-failure-hunter, type-design-analyzer) — found 4 HIGH issues, all fixed in commit `983e87b`.
- Phase 1+2+2.5 foundation audit: 3 agents (code-explorer, code-reviewer, superpowers:code-reviewer) — found SettingsForm v4 desync timebomb + case-drift + weak FK, all fixed in commits `a2e5f65` + `beb2862`.
- Phase 2.5+2.6 verification: 3 agents (code-reviewer, feature-dev:code-reviewer, pr-test-analyzer) — found H1 hotkey routing + M1 case-drift, both fixed in commit `beb2862`. Also recommended splitter-mirror invariant test (landed in `beb2862`).

The foundation has been reviewed by 9 agents across 3 rounds. All issues they found are either fixed or explicitly deferred to the right future phase. Phase 3 stands on solid ground.

---

### Suggested first message back to Nate

After reading the plan + this handoff + running brainstorming: a short summary of:
1. What Phase 3 will touch (file list + line deltas expected).
2. Any UI/UX decisions you need from Nate (grouping threshold, emoji choices if you deviate from the plan's mockup, empty-state copy, etc.).
3. The agent fanout you plan to use after implementation.

Do not ask for sign-off on "the plan" — the plan is already signed off. Ask for sign-off on specific new decisions.

---

**Bar one more time, since it matters:** *Every UI element, every interaction, every implementation detail, every verification should reflect top-tier craftsmanship. Don't stop at "good enough."*

The tray menu is the face of EQSwitch. It's the most-interacted-with surface in the entire app. Phase 3 is where v3.10.0 becomes visible to the user. Make it the best tray menu we have ever produced.
