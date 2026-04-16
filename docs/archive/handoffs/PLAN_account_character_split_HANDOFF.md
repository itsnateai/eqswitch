# New-Session Handoff — EQSwitch Account/Character Split

Paste the prompt below into a fresh Claude Code session. It assumes nothing from this conversation.

---

## Prompt

You are continuing work on EQSwitch, a C#/.NET 8 WinForms multiboxing tray app for EverQuest (Shards of Dalaya). Repo: `X:/_Projects/EQSwitch/`. Read `CLAUDE.md` first for architecture and conventions — it's authoritative.

**Your task:** verify, refine, and implement the plan in `PLAN_account_character_split.md` (same directory). The plan splits the current `LoginAccount` model into two first-class entities (`Account` for credentials, `Character` for play targets) and restructures the tray menu into three intentional launch modes (bare client, login-and-stop-at-charselect, login-and-enter-world).

### Phase A — Re-verify the patched plan against current code (READ-ONLY, ~20 min)

The plan was audited on 2026-04-14 and 16 gaps were patched. The header has `Verified against codebase: 2026-04-14`. Your job is to re-verify the patched claims (not re-find the original gaps — those are already fixed). Spot-check:

- **`ConfigVersionMigrator.cs` is the right home** for `MigrateV3ToV4` — not `ConfigMigration.cs` (which is AHK-only). Confirm pattern matches `MigrateV2ToV3`.
- **`AutoLoginManager.LoginAccount(account, teamAutoEnter)`** at line 63 — two-parameter signature. The plan's new methods must preserve the `teamAutoEnter` override semantic.
- **`Team{N}AutoEnter` fields** in `AppConfig.cs` lines 86-89 — four bools, preserved in v4 migration unchanged.
- **Hotkey two-step lookup** — `HotkeyConfig.AutoLoginN` (key combo) + `AppConfig.QuickLoginN` (username/charname) — migration rule in "Step 2" is the right shape.
- **`CharacterProfile.PriorityOverride`** at line 485 — used by `AffinityManager`, not tooltip-only. Rename preserves it.
- **`ExecuteQuickLogin` at line 1320-1322** — matches by CharacterName first, Username second. Migration follows same order.
- **Root "Launch Team" button** at line 802-804 — stays. Team 1 accessible from both root and Teams submenu.
- **Parallel fire-and-forget** in `FireTeamLogin` at line 1339 — new `FireTeam` must preserve parallelism.
- **Phantom-click defenses** — `gameState==5` at `mq2_bridge.cpp` lines 1103 and 1141; `result==-2` at `AutoLoginManager.cs:417`.

If any claim in the patched plan disagrees with the code, flag it BEFORE writing Phase 1. The patches were done carefully but six-eyes is cheap insurance.

### Phase B — Review the resolved decisions (~10 min)

The plan's "Open questions — resolved decisions" section has `(proposed)` and `(resolved)` markers. Read them once and flag anything Nate should override:

- `(proposed)` items — Nate's preferences are unknown; current recommendations are defensible defaults based on the audit.
- `(resolved)` items — these are derived from code reading or audit findings and should not change without a specific reason.

If you disagree with a recommendation, make your counter-argument in writing before Phase 1 kicks off. Don't silently implement a different choice.

### Phase C — Use the brainstorming + writing-plans skills before coding

This is a multi-phase, multi-file change touching schema migration, native SHM (verified-unchanged but re-check), C# core flow, two pieces of UI, and configuration round-tripping. It absolutely qualifies for the `superpowers:brainstorming` skill before implementation. Then use `superpowers:writing-plans` to produce the per-phase implementation plan if any phase needs more granular steps than the existing plan provides.

Do NOT skip these. The phantom-click bugs we just spent a session chasing came from inadequate upfront design — three reviewers had to find the bugs after the fact. This change is bigger than that one.

### Phase D — Implement Phase 1 (Models + migration), STOP, get sign-off

Per the plan's "Build sequence" — it was updated with the patched steps on 2026-04-14:

1. Create `Models/Account.cs`, `Models/Character.cs`, `Models/HotkeyBinding.cs`, `Models/CharacterAlias.cs` (rename of `CharacterProfile`) per the schema in the plan.
2. Update `Config/AppConfig.cs`: swap `List<LoginAccount> Accounts` → `List<Account> Accounts`; swap `List<CharacterProfile> Characters` → `List<CharacterAlias> CharacterAliases` (new JSON key `"characterAliases"`); add `List<Character> Characters` (reclaims the `"characters"` JSON key). Bump `CurrentConfigVersion` to 4.
3. Implement `MigrateV3ToV4(JsonObject root)` in `Config/ConfigVersionMigrator.cs` — **NOT `ConfigMigration.cs`**. Follow the existing `MigrateV2ToV3` pattern. Covers all 5 steps (account split, hotkey two-step lookup, team field rebinding, Team{N}AutoEnter preservation, TrayClickConfig noop).
4. Add migration scripted fixtures (no test project exists) covering the 5 patterns (a-e) from the plan's "Migration test fixtures" section. Script them under `_tests/migration/` or similar.
5. Keep existing `LoginAccount.cs` as `[Obsolete("v3 compat only; removed in v3.11.0")]` so the rest of the codebase still compiles. **Do not touch `TrayManager.cs`, `SettingsForm.cs`, or `AutoLoginManager.cs` yet.**
6. Verify `AffinityManager` still works with the `CharacterProfile` → `CharacterAlias` rename. Update any direct type references.
7. Build (`dotnet build`), publish (`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`).
8. Manually load the existing `eqswitch-config.json` from `C:/Users/nate/proggy/Everquest/EQSwitch/` (back it up first to `eqswitch-config.json.v3-backup`), run the new build once with the user watching, verify migration logs are clean, EXIT THE APP, then `cat eqswitch-config.json` and confirm:
   - `configVersion: 4`
   - `accounts` array has deduped Account entries (no duplicate username+server pairs).
   - `characters` array has Character entries for every v3 LoginAccount with CharacterName set.
   - `characterAliases` array holds the migrated v3 `characters` list (the old CharacterProfile data).
   - `quickLogin1-4` and `hotkeys.autoLogin1-4` retained for downgrade safety.
   - `team{N}Account{M}` fields rebound to Character names where possible, Account names otherwise.
   - `team{N}AutoEnter` unchanged.
9. **STOP. Report back. Get user sign-off before Phase 2.**

### Phase E — Subsequent phases

After sign-off on Phase 1, proceed through Phases 2-6 from the plan. Each phase has its own verification gate listed in the plan. Hit each gate before moving on. Use `pr-review-toolkit:code-reviewer` and `pr-review-toolkit:silent-failure-hunter` agents in parallel after Phase 3 (tray menu) and Phase 4 (SettingsForm) — these are the highest-risk surfaces.

### Hard rules

- **Never break the v3 config of an existing user.** Always back up before migration. Always provide a v3-restore path until v3.11.0.
- **Do not regress the phantom-click defenses.** The `gameState=5` gate (`Native/mq2_bridge.cpp` ~line 1085) and the C# `result==-2` handling (`Core/AutoLoginManager.cs` ~line 416) must survive the AutoLoginManager API split. After your changes, re-grep for `gameState == 5` and `result == -2` and confirm the logic is identical.
- **Stage specific files, never `git add -A`.** Conventional commits, under 72-char title.
- **Each phase = one or more atomic commits.** Never bundle migration code with UI rewrites in a single commit.
- **No emojis in code, comments, or commits.** Tray menu icons (the existing emoji icons in TrayManager.cs) are fine — those are user-facing UI strings, not code.
- **Native side: assume unchanged but re-verify.** If you find yourself touching `Native/mq2_bridge.cpp` or `Native/eqswitch-di8.cpp`, STOP and re-read the plan — the split is a UI/data-model concern only.
- **Memory file:** at the end of each phase, append a one-line status to `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v392_native_upgrade.md` (or create a new `project_eqswitch_v3_10_0_account_split.md` if that file is closed) so future sessions have continuity.

### Standard of work

This change touches schema migration (one-shot, hard to reverse if wrong), the user-facing tray menu (the most-used surface of the entire app), and the autologin flow (which already has subtle race conditions we just fixed). It is exactly the kind of change where "good enough" creates technical debt that bites for months.

Take the time. Read the code before writing the code. Run agents to verify before claiming done. If you discover the plan is wrong, FIX THE PLAN and re-confirm before continuing — don't paper over it in the implementation.

If at any point you find yourself thinking "this is taking too long, I'll just ship Phase 1," that's the signal to slow down, not speed up. Phase 1 done correctly takes longer than Phase 1 done sloppily, but Phase 1 done sloppily costs Phase 2-6 in cascading rework.

The user (Nate) has explicitly said: *"Every feature you build, every bug you fix, every audit you run is a showcase of the absolute best work Claude Code can produce. Every UI element, every interaction, every implementation detail, every verification should reflect top-tier craftsmanship. Don't stop at 'good enough.'"* That's the bar. Hit it.

---

## What the original session accomplished

(Context for the new session — this is what was already done before the handoff.)

- Heap scan for character names on Dalaya ROF2: deployed and verified live (10/10 names + race confirmed).
- Bust of prior memory-file claims: `+0x44 IS race, NOT class`. `+0x50` is stale level data, not current. Class+level offsets remain unverified in heap struct (a separate stride-0x20 array holds level — see `project_eqswitch_chararray_intel.md`).
- Phantom-click hardening (commits `4bc13c9`, `ca66aee`): native `gameState==5` gate on Enter World, C# `result==-2` handling, drag-aware rect tracking, heap-cache aggressive invalidation, `g_fnWndNotification`-null safety, gameState=5 SHM drain.
- README screenshots wired (`docs/img/`).
- `TODO_LIST.md` carries the now-superseded "tray Enter World" item — that work is folded into the `Characters ▸` submenu in this plan.
- Three reviewers (code-reviewer, silent-failure-hunter, feature-dev) audited the phantom-click work; their real findings were applied, false positives skipped with reasoning preserved in commit `ca66aee`.

Current build at `C:/Users/nate/proggy/Everquest/EQSwitch/` is v3.9.3 + the phantom-click fixes. Last commit on `main`: `ca66aee`.

---

## Suggested first message back to Nate

After Phase A: a 5-bullet list of "plan claims I verified" + "anything I had to correct" + "any open question I need answered before proceeding to Phase B/C." Don't ask for sign-off on the *plan* — ask for sign-off on the *resolutions* to specific open questions.
