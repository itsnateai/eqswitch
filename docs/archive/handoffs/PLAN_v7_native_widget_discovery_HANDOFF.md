# New-Session Handoff — EQSwitch v7 Native Widget Discovery

Read `CLAUDE.md` first. Repo: `X:\_Projects\EQSwitch\`.

**The standard:** *"Every feature you build, every bug you fix is a showcase of the absolute best work Claude Code can produce."*

## Current State

The keyboard fallback login path works (password typing, server select, char select, enter world via PulseKey3D). Background typing protection is functional (`writer.Activate(pid, suppress: true)` — user can type elsewhere while EQ gets credentials). **Do not break this.**

The v7 in-process login path (LoginShm) is DISABLED (`if (false && ...)` in `AutoLoginManager.cs:369`). All C# wiring is complete and tested. The DLL's LoginStateMachine is wired and functional. The ONLY blocker is native widget discovery — finding the CXWnd objects for `LOGIN_UsernameEdit`, `LOGIN_PasswordEdit`, `LOGIN_ConnectButton` to call `SetEditText`/`ClickButton` on them.

## What's Done (don't redo)

- `Core/LoginShmWriter.cs` — complete, 1340-byte SHM matching `login_shm.h`
- `Core/AutoLoginManager.cs` — `TryLoginViaShm` + `HandleCharSelectViaShm` wired, CharacterSelector.Decide safety gate preserved
- `Native/login_state_machine.cpp` — full credentials→connect→server→charselect→enter-world state machine
- `Native/login_givetime_detour.cpp` — MinHook detour on `LoginController::GiveTime` at RVA 0x128B0, fires 50-130Hz
- `Native/login_shm.h` — SHM struct, C#↔DLL protocol
- `Program.cs` — test classes wrapped in `#if DEBUG` for Release builds
- GiveTime detour verified working, LoginStateMachine command/ack verified working
- All unit tests pass (CharacterSelector 10/10, KeyInputWriter 6/6, ShmLayout all pass)

## The Blocker: FindWindowByName

`FindWindowByName` in `Native/mq2_bridge.cpp` can't locate login widgets. Every approach tried and failed:

| Approach | Result |
|----------|--------|
| GetChildItem on LoginController* | LoginController is NOT a CXWnd (first DWORD is heap ptr, not vtable) |
| GetChildItem on LoginController's 27 CXWnd-like fields | All return NULL |
| eqmain.dll .data scan for CXWndManager | Finds false positive (310 entries, only data-like objects, no real UI windows). Tried thresholds 3→15→50, best-of-all-candidates — only one candidate exists |
| pinstCXWndManager (MQ2 export) | NULL during login. Only populated at charselect |
| pinstCEQMainWnd (MQ2 export) | NULL during login |
| ppWndMgr (MQ2 export) | NULL during login |
| eqmain globals near pLoginController RVA 0x150174 | No CXWnd found in ±512 bytes |
| Heap string scan for "LOGIN_UsernameEdit" | **String found at ~0x019Fxxxx.** CXWnd candidate at ~0x019FF1xx with vt=0x72A1xxxx. But CXStr offset unreliable — reads different names on subsequent passes |
| MQ2AutoLogin source (macroquest/macroquest) | Uses LIVE client infrastructure (`LoginStateSensor`, `g_pLoginViewManager`) that doesn't exist in ROF2 emu |

**Critical mistake:** Used LIVE MQ2 source as reference. Dalaya runs ROF2 emulator. Need the ROF2-emu MQ2 fork (if it exists publicly) or reverse-engineer from Dalaya's own `dinput8.dll`.

## What the Next Session Must Do

**DO NOT iterate with incremental DLL deploys.** Do not ask Nate to launch EQ 15 times. The previous session wasted 4+ hours on that approach.

### Step 1: Memory Dump (ONE launch)

```bash
# Launch EQ via EQSwitch Accounts menu, wait for password screen to fully render (~8s)
MSYS_NO_PATHCONV=1 tasklist /FI "IMAGENAME eq eqgame.exe"
# Note the PID

# Full memory dump while at login screen
mkdir -p "X:/_Projects/eqswitch/dumps"
procdump -ma <PID> "X:/_Projects/eqswitch/dumps/eqgame_login.dmp"
# ~800MB-2GB file (32-bit process)

# Let EQ proceed to charselect, dump again
procdump -ma <PID> "X:/_Projects/eqswitch/dumps/eqgame_charsel.dmp"
```

### Step 2: Offline Analysis (Python, NO EQ running)

Write a Python script using `ctypes` + `ReadProcessMemory` (or parse the .dmp file with `minidump` library) to:

1. Find ALL instances of `"LOGIN_UsernameEdit\0"` in the dump
2. For each string address, scan the ENTIRE dump for DWORDs matching that address (= CXStr.Ptr references)
3. For each reference, walk backwards at 4-byte alignment checking for a valid vtable pointer (value in 0x70000000-0x73000000 range = loaded DLL code)
4. Record the CXWnd base address and the offset of the CXStr.Ptr from base
5. Repeat for `"LOGIN_PasswordEdit"`, `"LOGIN_ConnectButton"`, `"Character_List"`, `"CLW_EnterWorldButton"`
6. Compare all found CXWnds — they should share the same SIDL-name-offset within the struct

### Step 3: Map CXWnd Struct Layout

From the found CXWnds, document:
- Vtable offset (should be 0x0)
- SIDL name CXStr offset (the offset where CXStr.Ptr to the name string lives)
- Any other identifiable members (child list, parent pointer, sibling pointer, HWND, position rect)

### Step 4: Fix FindWindowByName

With the known SIDL name offset, rewrite FindWindowByName to:
1. Scan the heap region (~0x019xxxxx) for objects with vtable in the DLL range
2. At the known SIDL name offset, read the CXStr.Ptr
3. Compare the pointed-to string with the target name
4. Cache found widgets — they don't move during the login phase

### Step 5: Re-enable LoginShm

1. Remove `if (false && ...)` guard in `AutoLoginManager.cs:369`
2. Build + deploy
3. Test: launch ONE character, verify instant password push + enter world
4. Test: launch TWO characters simultaneously, verify both work in background

## Key Files

```
Native/mq2_bridge.cpp        — FindWindowByName (the broken part), MQ2 export resolution
Native/login_state_machine.cpp — uses FindWindowByName results for SetEditText/ClickButton
Native/login_givetime_detour.cpp — GiveTime detour, LoginController* capture
Native/login_shm.h            — LoginShm struct definition
Native/build-di8-inject.sh    — builds eqswitch-di8.dll (MSVC x86)
Core/AutoLoginManager.cs      — LoginShm path (disabled), keyboard fallback (working)
Core/LoginShmWriter.cs         — C# SHM wrapper (complete)
```

## Key Facts

- eqmain.dll: ASLR per launch. GiveTime RVA 0x128B0, pLoginController RVA 0x150174
- eqmain.dll **UNLOADS** at charselect. Login widgets die. GiveTime detour dies.
- Dalaya MQ2 dinput8.dll exports: GetChildItem, SetWindowText, GetWindowText, WndNotification, SetCurSel, GetCurSel, GetItemText, CXStr ctor/dtor — all resolved and functional
- CXWnd vtables for login widgets: ~0x72A1xxxx range (inside MQ2 dinput8.dll)
- pinstCXWndManager at charselect: CXWndManager* at heap, window array at offset **0x54** (31 windows, 3 valid)
- ALL MQ2 export pointers (pWndMgr, pinstCEQMainWnd, pinstCCharSelect) are **NULL during login**
- procdump is available via Sysinternals (already installed)
- Deploy path: `C:\Users\nate\proggy\Everquest\EQSwitch\`

## Memory References

- `project_eqswitch_v7_givetime_session.md` — native side complete
- `project_eqswitch_v7_phase4_csharp.md` — C# wiring complete
- `project_eqswitch_v7_goal_mq2_givetime_detour.md` — north star vision
