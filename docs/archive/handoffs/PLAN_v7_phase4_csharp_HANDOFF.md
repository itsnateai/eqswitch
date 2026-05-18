# New-Session Handoff — EQSwitch v7 Phase 4 C# Integration

Paste the prompt below into a fresh Claude Code session. Self-contained.

---

## Prompt

You are continuing work on **EQSwitch**, Nate's C#/.NET 8 WinForms multiboxing tray app for EverQuest (Shards of Dalaya emulator). Repo: `X:\_Projects\EQSwitch\`. Read `CLAUDE.md` first.

**The standard:** *"Every feature you build, every bug you fix is a showcase of the absolute best work Claude Code can produce."*

## What just shipped (v7 native side — merged to main)

The native DLL (`eqswitch-di8.dll`) now has a MinHook detour on `LoginController::GiveTime` in eqmain.dll. This replaces the old SetTimer/WM_TIMER approach and eliminates the IsHungAppWindow / Event 1002 crash class entirely. Key capabilities:

1. **GiveTime detour** fires at 50-130 Hz during login/server-select (eqmain phase)
2. **eqmain unload detection** — when EQ transitions to 3D charselect, eqmain.dll unloads. ActivateThread detects this and resumes background MQ2BridgePollTick
3. **LoginController* exposed** — `GiveTimeDetour::GetLoginController()` returns the top-level login window. `FindWindowByName` Tier-0 uses it to find `LOGIN_UsernameEdit`, `LOGIN_PasswordEdit`, `LOGIN_ConnectButton` instantly
4. **Standalone heap scan at charselect** — finds ALL character names (10/10 verified on acpots account: Acpots, Backup, Healpots, Jonopua, Nate, Potiongirl, Potionguy, Staxue, Thazguard, Zfree) without needing CXWndManager or MQ2 exports
5. **LoginStateMachine wired** — full credential→connect→server→charselect→enter-world pipeline in `login_state_machine.cpp`, driven by `LoginShm` (`Local\EQSwitchLogin_{PID}`)

## What you need to do (C# side)

### Task 1: Wire AutoLoginManager to create LoginShm

The DLL's `LoginStateMachine::Tick` already handles the entire login flow:
- Reads credentials from `LoginShm.username` / `LoginShm.password`
- Calls `CEditWnd::SetText` on `LOGIN_UsernameEdit` / `LOGIN_PasswordEdit` (instant, no keyboard injection)
- Clicks `LOGIN_ConnectButton` via `WndNotification(XWM_LCLICK)`
- Handles error dialogs (bad password, already logged in)
- Waits for server select transition
- At charselect: populates character data in LoginShm

**C# needs to:**
1. Create `LoginShm` SHM (`Local\EQSwitchLogin_{PID}`) with the struct from `Native/login_shm.h`
2. Write `LOGIN_CMD_LOGIN` command + credentials + server + character name
3. Monitor `LoginShm.phase` for progress and `LoginShm.errorMessage` for failures
4. When `phase == PHASE_CHAR_SELECT`, character data is in `LoginShm.charNames[0..9]`
5. The DLL handles character selection and enter-world if `LoginShm.character` was specified

**Key:** The old keyboard-injection path in `AutoLoginManager` (SendInput/DirectInput SHM key bursts) should remain as fallback, but LoginShm should be tried first.

### Task 2: Wire charselect Enter World via SHM

`CharSelectShm.charCount` now populates at charselect (was always 0 before v7 due to the eqmain-unload bug). The existing `enterWorldReq`/`enterWorldAck` protocol should work, but the DLL needs `FindWindowByName('CLW_EnterWorldButton')` to succeed.

**Known limitation:** `CLW_EnterWorldButton` is a child of CCharacterSelect, not LoginController. `pinstCCharacterSelect` is null on Dalaya. The fallback path is the existing `PulseKey3D` (keyboard Enter) which works.

**Nate's vision:** "passwd screen, server select, char select — 700ms, user doesn't see it until in-world." Phase 5 (SW_HIDE) enables this once the flow is automated.

### Task 3 (Optional): Phase 5 headless mode

Add `ShowWindow(SW_HIDE)` on eqgame's window during login/server/charselect. Restore visibility (`SW_SHOW`) when entering world or when `AutoEnterWorld=false` and charselect reached. Tray balloon notification with status.

## Read these files

1. `CLAUDE.md` — architecture
2. `Native/login_shm.h` — LoginShm struct definition
3. `Native/login_state_machine.cpp` — the DLL-side state machine (widget names, phase transitions, error handling)
4. `Core/AutoLoginManager.cs` — the C# side that needs to create LoginShm
5. `Native/mq2_bridge.h` — CharSelectShm struct
6. Memory: `project_eqswitch_v7_givetime_session.md` — full session notes

## Critical facts

- `pinstCCharacterSelect` = **permanently null** on Dalaya. Don't rely on it.
- `pinstCXWndManager` = **permanently null** on Dalaya. Don't rely on it.
- eqmain.dll **UNLOADS at charselect** (not at in-game). Login widgets die. Background poll resumes.
- Heap scan at stride 0x160 with strict title-case validation is the proven charselect data path.
- LoginStateMachine widget names: `LOGIN_UsernameEdit`, `LOGIN_PasswordEdit`, `LOGIN_ConnectButton`, `OK_Display`, `OK_OKButton`, `YESNO_YesButton`, `Character_List`, `CLW_EnterWorldButton`
- HEAD on main: merge commit `6219d50`, all pushed. DLL deployed at `C:\Users\nate\proggy\Everquest\EQSwitch\`.
- Worktree `.worktrees/v7-givetime-detour` may need manual `rm -rf` (was locked at session end).

## Definition of done

1. `AutoLoginManager` creates `LoginShm`, sends `LOGIN_CMD_LOGIN`, monitors phase transitions
2. Single auto-login reaches charselect with credentials set via `SetEditText` (zero keyboard injection)
3. Character selected by name from `LoginShm.charNames[]` + enter world completes
4. Parallel team launch (natedogg + acpots) both reach in-world without Event 1002
5. Old keyboard-injection path preserved as fallback
