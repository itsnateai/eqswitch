# New-Session Handoff — EQSwitch Account/Character Split

Paste the prompt below into a fresh Claude Code session. It assumes nothing from this conversation.

---

## Prompt

You are continuing work on EQSwitch, a C#/.NET 8 WinForms multiboxing tray app for EverQuest (Shards of Dalaya). Repo: `X:/_Projects/EQSwitch/`. Read `CLAUDE.md` first for architecture and conventions — it's authoritative.

**Your task:** verify, refine, and implement the plan in `PLAN_account_character_split.md` (same directory). The plan splits the current `LoginAccount` model into two first-class entities (`Account` for credentials, `Character` for play targets) and restructures the tray menu into three intentional launch modes (bare client, login-and-stop-at-charselect, login-and-enter-world).

### Phase A — Verify the plan against current code (READ-ONLY first, ~30 min)

Before writing any code, prove the plan is grounded. Two specific verifications the user explicitly called out:

- **GUI table restructure** — the plan's "SettingsForm Accounts tab — table restructure (detailed)" section describes a dual-section layout (Accounts grid above, Characters grid below). Read the current `UI/SettingsForm.cs` Accounts tab carefully — note every field, every event handler, every staging variable. Confirm the staging pattern (`ApplySettings()` builds new config from controls; never mutates `_config` directly) matches the plan's description and that `_stagedAccounts`/`_stagedCharacters` is the right way to extend it.
- **Teams** — the plan's "Teams — verification + integration" section asks you to verify three things in code BEFORE writing migration code: (1) does today's team launch actually call the same `LoginAccount(LoginAccount)` entry point so the phantom-click defenses apply? (2) does each member's `AutoEnterWorld` flag get respected, or is there a team-level override? (3) parallel vs serial launches and shared state. Trace the call graph from the team-launch hotkey through to `AutoLoginManager`. If teams currently DON'T always enter world, the plan's "teams always enter world" claim is a behavior change, not just a model rename — flag this.
- **Per-account quick-launch hotkeys** — the plan's "Per-account quick-launch hotkeys — explicit handling" section adds an `AutoLoginTargets` array to `HotkeyConfig` so each AutoLoginN slot binds to either an Account or a Character (with a `Kind` discriminator). Verify the existing `HotkeyConfig` shape, the existing `AutoLogin1-4` semantics, and how the hotkey handler dispatches today. Confirm migration logic preserves user intent (Character-bound = enter world, Account-bound = stop at charselect).

Then open these files and confirm the rest of the plan matches reality:

- `Models/LoginAccount.cs` — current schema.
- `Config/AppConfig.cs` — `Accounts`, `Characters` (existing `CharacterProfile`), `ConfigVersion`, `HotkeyConfig`, `Team{N}Account{M}` fields.
- `Config/ConfigMigration.cs` and `ConfigVersionMigrator` — where v3→v4 migration must hook in.
- `Core/AutoLoginManager.cs` — confirm `LoginAccount(LoginAccount)` is the single autologin entry point. Trace all call sites. Especially read lines around 200-540 (the main login flow), 685-755 (`WaitForScreenTransition`), and the `finally` cleanup.
- `Core/CharSelectReader.cs` — SHM contract. The plan claims native is unchanged; verify by checking that selection-by-name + Enter World only need a character name and a PID.
- `UI/TrayManager.cs` — current `BuildContextMenu()` (~line 775), Accounts submenu (~line 807). Note all the helper methods that read `_config.Accounts`.
- `UI/SettingsForm.cs` — current Accounts tab (search for `_chkAutoEnterWorld`, the DataGridView for accounts, the AccountEditDialog if any).
- Recent commits `ca66aee`, `4bc13c9` — phantom-click defenses. The plan must not regress these.

If the plan and the code disagree on any structural claim, flag it before proceeding. The plan was written from a brief skim and may have missed nuance.

### Phase B — Sharpen the plan (~15 min)

Update `PLAN_account_character_split.md` in place if your verification turns up anything material. Specifically resolve:

- The **naming-collision** decision (Option A vs B in the plan) — pick one and commit it in writing.
- The four **open questions** at the bottom of the plan — propose answers based on what you've now read in the code; mark each as "proposed" so the user can override.
- Any nuance from "Nuances to watch" that turns out to be wrong or already handled.

If the plan still looks right, leave it as-is and add a one-line "Verified against codebase 2026-MM-DD" at the top.

### Phase C — Use the brainstorming + writing-plans skills before coding

This is a multi-phase, multi-file change touching schema migration, native SHM (verified-unchanged but re-check), C# core flow, two pieces of UI, and configuration round-tripping. It absolutely qualifies for the `superpowers:brainstorming` skill before implementation. Then use `superpowers:writing-plans` to produce the per-phase implementation plan if any phase needs more granular steps than the existing plan provides.

Do NOT skip these. The phantom-click bugs we just spent a session chasing came from inadequate upfront design — three reviewers had to find the bugs after the fact. This change is bigger than that one.

### Phase D — Implement Phase 1 (Models + migration), STOP, get sign-off

Per the plan's "Build sequence":

1. Create `Models/Account.cs` and `Models/Character.cs` per the schema in the plan.
2. Update `Config/AppConfig.cs` to use the new types. Bump `ConfigVersion` to 4.
3. Implement `MigrateV3ToV4` in `ConfigVersionMigrator`.
4. Add migration unit tests (or scripted fixtures if the project has no test project) covering: (a) one account / one character; (b) one account / three characters; (c) account with no character; (d) DPAPI password round-trip after migration.
5. Keep existing `LoginAccount.cs` as a thin shim or `[Obsolete]` so the rest of the codebase still compiles. **Do not touch `TrayManager.cs`, `SettingsForm.cs`, or `AutoLoginManager.cs` yet.**
6. Build (`dotnet build`), publish (`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`).
7. Manually load the existing `eqswitch-config.json` from `C:/Users/nate/proggy/Everquest/EQSwitch/` (back it up first to `eqswitch-config.json.v3-backup`), run the new build once with the user watching, verify migration logs are clean, EXIT THE APP, then `cat eqswitch-config.json` and confirm Accounts and Characters look right.
8. **STOP. Report back. Get user sign-off before Phase 2.**

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
