# New-Session Handoff — v8 eqmain-side Function Pointer Resolution

Read `CLAUDE.md` first. Repo: `X:\_Projects\EQSwitch\`.

**The standard:** *"Every feature you build, every bug you fix is a showcase of the absolute best work Claude Code can produce."*

## One-line status

Step 1 of the MQ2 port SHIPPED and PROVEN. Login works end-to-end via Phase 6 (noisily with 72 SEH faults, but natedogg made it to charselect AFK). Step 2 is the clean-up fix.

## Read these first (5 minutes)

1. **`C:/Users/nate/.claude/projects/X---Projects/memory/reference_eqswitch_mq2_eqmain_detection.md`** — the full MQ2 architecture research. **Part 2** of that doc is the most important — it explains the class-mismatch root cause and the exact fix path.
2. **This file** (below) — step 2 concrete plan.
3. `project_eqswitch_v7_phase6_live_cxwnd.md` — why Phase 6 is unreliable.

## What's currently in the compiled DLL

- MinHook detour on `DirectInput8Create` (DI8 proxy, wraps keyboard for background input)
- IAT hooks on `GetForegroundWindow`/`GetAsyncKeyState`/etc. (focus spoofing during auto-login)
- `LdrRegisterDllNotification` callback ← **step 1, shipped this session, PROVEN** (see log line `dll_notify: eqmain.dll LOADED at 0x71E20000 — callback fired BEFORE eqmain DllMain`)
- `LoginController::GiveTime` trampoline detour (Phase 2, from earlier work)
- Phase 5 heap scan for widget definitions
- Phase 6 live-CXWnd cross-ref (the heuristic that returns usable-but-SEH-prone pointers)

## What's NOT in the compiled DLL

- `sidl_ctor_hook.cpp` / `sidl_ctor_hook.h` — files exist on disk but aren't in `build-di8-inject.sh`'s compile list. This was my Phase 7 attempt (MinHook constructor detours on the three known vtables). Pattern scan missed all three ctors (`0/3 hooks active` in every log). Deprecated — step 5 will delete these files. Ignore them.

## Step 2 — the actual task

**Problem (proven):** `mq2_bridge.cpp` calls `SetWindowTextA` / `WndNotification` / `ClickButton` through function pointers resolved from `dinput8.dll` exports (MQ2's eqgame-side functions in namespace `EQClasses`). When we call them on widgets that live inside `eqmain.dll`, they SEH-fault because eqmain has its own parallel class hierarchy with its own function addresses. The methods happen to succeed *sometimes* (why login works at all) but are unreliable.

**Fix:** Mirror MQ2's architecture by adding a second set of function pointers resolved from inside `eqmain.dll` at its load base, then routing widget operations through the right set based on which DLL owns the widget.

### Concrete file plan

Create `Native/eqmain_offsets.h` and `Native/eqmain_offsets.cpp`:

```cpp
// eqmain_offsets.h
namespace EQMainOffsets {
    // Called from LdrDllNotificationCallback in eqswitch-di8.cpp on eqmain LOAD.
    // Pattern-scans eqmain.dll starting at dllBase for known functions. Logs
    // each resolution attempt. Returns true if at least the critical ones
    // (SetWindowTextA, WndNotification) resolved.
    bool InitializeEQMainOffsets(uintptr_t dllBase);

    // Called on eqmain UNLOAD. Nulls all function pointers.
    void ShutdownEQMainOffsets();

    // The eqmain-side function pointers. All null if eqmain not loaded
    // or resolution failed.
    typedef void    (__thiscall *FN_SetWindowTextA)(void *pWnd, void *pCXStr);
    typedef int     (__thiscall *FN_WndNotification)(void *pWnd, void *sender, uint32_t msg, void *data);
    typedef void*   (__thiscall *FN_GetChildItem)(void *pWnd, const char *name);
    typedef void*   (__thiscall *FN_GetXMLData)(void *pWnd);

    extern FN_SetWindowTextA   fnSetWindowTextA;
    extern FN_WndNotification  fnWndNotification;
    extern FN_GetChildItem     fnGetChildItem;
    extern FN_GetXMLData       fnGetXMLData;

    // Diagnostic: true if a given CXWnd pointer lies inside eqmain.dll's
    // address range (so caller knows which fn set to use).
    bool IsEQMainWidget(const void *pWnd);
}
```

### How to find the function addresses

**Primary approach: pattern scan inside eqmain.dll.** Signatures for MSVC x86 thiscall methods are stable across builds. Suggested signatures to try (adapt from disassembling the equivalent eqgame-side functions which we have export symbols for):

1. `SetWindowTextA(CXStr&)`: typical prologue sequence — `8B FF 55 8B EC 56 8B F1 ...`, body stores the CXStr pointer into a member then calls invalidation. Find it by scanning for short `mov member, arg; ret` patterns in eqmain's .text section where the function takes `[this, &cxstr]`.

2. **Better approach — use dinput8.dll as a template**: get the bytes of `?SetWindowTextA@CXWnd@EQClasses@@QAEXAAVCXStr@2@@Z` from dinput8, grab the first 16 bytes of its body, then search for similar prologues in eqmain.dll. Pattern matching with wildcards for RVA-relative immediates.

3. **Last resort — IDA hcli** (`github.com/HexRaysSA/ida-hcli`): static disassemble eqmain.dll, find functions by signature match, extract RVAs. Only if 1&2 miss.

### Wiring into existing code

`eqswitch-di8.cpp` `LdrDllNotificationCallback`:
```cpp
if (reason == LDR_DLL_NOTIFICATION_REASON_LOADED) {
    DI8Log("dll_notify: eqmain.dll LOADED at 0x%08X ...", data->DllBase, ...);
    EQMainOffsets::InitializeEQMainOffsets(data->DllBase);  // ← ADD THIS
} else if (reason == LDR_DLL_NOTIFICATION_REASON_UNLOADED) {
    DI8Log("dll_notify: eqmain.dll UNLOADING — ...");
    EQMainOffsets::ShutdownEQMainOffsets();  // ← ADD THIS
}
```

`mq2_bridge.cpp` `SetEditText` and `ClickButton`:
```cpp
void SetEditText(void *pWnd, const char *text) {
    // If widget lives in eqmain's range and eqmain-side fn resolved, use it.
    auto fn = EQMainOffsets::IsEQMainWidget(pWnd)
            ? EQMainOffsets::fnSetWindowTextA
            : g_fnSetWindowText;  // eqgame-side fallback
    // ... build CXStr, call fn(pWnd, &cxstr), destroy CXStr
}
```

Same pattern for `ClickButton` → use `EQMainOffsets::fnWndNotification` when the widget is eqmain-side.

### Build + deploy
```bash
cd X:/_Projects/eqswitch
bash Native/build-di8-inject.sh  # add eqmain_offsets.cpp to the cpp list
cp Native/eqswitch-di8.dll "C:/Users/nate/proggy/Everquest/EQSwitch/"
```

### Verification path

After step 2 is deployed and user launches:

1. Log should show `eqmain_offsets: SetWindowTextA resolved to 0x71E...` for each target.
2. Login attempt should show ZERO `SEH in SetEditText` or `SEH in ClickButton`.
3. State machine should complete phases 1-4 cleanly and transition to charselect without the 116-second stall.

## Keys to hit back into

### Key files you'll touch
- `Native/eqmain_offsets.h` — NEW
- `Native/eqmain_offsets.cpp` — NEW
- `Native/eqswitch-di8.cpp` — 2 lines added in the notification callback (one `Initialize`, one `Shutdown`)
- `Native/mq2_bridge.cpp` — `SetEditText` and `ClickButton` bodies updated to branch on IsEQMainWidget
- `Native/build-di8-inject.sh` — add `eqmain_offsets.cpp` to compile list

### Key addresses (stable — verified in most recent log)
- eqmain.dll base: **0x71E20000**, size: **0x359000** (confirmed by step 1 notification callback data)
- eqmain .text: 0x71E21000, size 0xFDAE3
- dinput8.dll (MQ2 patched): at 0x72EE0000
- Editbox definition vtable: **0x71F2D304** (Phase 5 intel)
- Button definition vtable: **0x71F2AA08**
- Other SIDL vtable: **0x71F2D370**
- eqmain GiveTime RVA: 0x128B0

### MQ2 reference source
Local, up-to-date, RoF2-era fork (matches Dalaya client gen):
**`X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/`**

The exact files to consult:
- `src/eqlib/src/EQLibImpl.cpp:548-571` — DLL notification callback (source for our step 1)
- `src/eqlib/src/EQLibImpl.cpp:693-738` — Register/fallback pattern (source for our step 1)
- `src/eqlib/include/eqlib/game/LoginFrontend.h:437-440` — the separate eqmain-side CXWnd class declaration (proves class mismatch)
- `src/eqlib/include/eqlib/offsets/eqmain.h` — PUBLIC RVA template (live-era 64-bit values, don't use directly, but see the function list you need to resolve)
- `src/main/MQ2LoginFrontend.cpp:51-88` — GiveTime_Detour (reference for step 3)
- `src/main/MQ2Windows.cpp:172-200` — ReinitializeWindowList (reference for step 3)

### Current build state
- `Native/eqswitch-di8.dll` was deployed as `C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-di8.dll` (194,560 bytes, step 1)
- `.prev` backup in same folder is the 198KB pre-step-1 build (includes abandoned Phase 7 ctor hook experiments — don't revert to it unless step 2 deploy breaks things)

### State machine and login shm
- C# `AutoLoginManager` writes to `Local\\EQSwitchLogin_{PID}` shared memory
- `LoginStateMachine` (native) reads from it and drives widget operations via `mq2_bridge::SetEditText`/`ClickButton`
- Phases: 1=find widgets, 2=set creds, 3=click connect, 4=wait-for-server, 5=wait-for-charselect, etc.
- See `Native/login_state_machine.cpp` — shouldn't need to modify for step 2, just make the widget ops it calls work properly

## Remaining steps after step 2

- **Step 3**: Port `ReinitializeWindowList` into GiveTime first-fire hook. Replace Phase 6 heuristic with MQ2's direct iterate-pWndMgr approach. This removes the cross-ref false-positive risk.
- **Step 4**: Prove no-SEH login in live test with reproducibility (2-3 runs).
- **Step 5**: Delete Phase 5/6 fallbacks and orphaned `sidl_ctor_hook.*` files. Target: ~-600 lines.

## What NOT to do

- **Don't reintroduce Phase 7 ctor hooks.** MQ2 doesn't use them; their approach (GiveTime + pWndMgr enumeration) is simpler and battle-tested. The reverted code is on disk at `sidl_ctor_hook.*` — leave it there for now, delete in step 5.
- **Don't hunt for another MQ2 fork.** The local `_.src/_srcexamples/macroquest-rof2-emu` is the correct version. Private RVAs for Dalaya aren't in any public fork — we discover them by pattern-scanning eqmain.dll ourselves.
- **Don't trust the "SEH in ClickButton" being fatal.** Live evidence (2026-04-16) is that login succeeds AFK despite 72 SEH faults in one run. The SEH wrapper may be catching post-op faults while the underlying op succeeded. Step 2 makes this cleaner but isn't rescuing a broken feature — it's hardening a working one.
- **Don't trust any prior-session note in `X:\CLAUDENOTES\` as verified fact.** The old `EQSWITCH.TXT` (being deleted by Nate) contained a specific wrong claim ("MQ2 hooks the CXWnd constructor") that was reasoning-framed-as-fact and propagated into multiple handoff docs and 4 days of wrong-direction Phase 7 work. Anything in those scratch dirs is a hypothesis. Verify against primary sources (MQ2 source in `_.src/_srcexamples/`, the actual binary, official docs) before building on it. Memory file `feedback_verify_claims_in_claudenotes.md` documents this incident as a first-class lesson.

## One-sentence resume

"Read `reference_eqswitch_mq2_eqmain_detection.md` Part 2, then create `Native/eqmain_offsets.{h,cpp}` to pattern-scan eqmain.dll for `SetWindowTextA`/`WndNotification`/`GetChildItem`/`GetXMLData` and wire them into `mq2_bridge::SetEditText`/`ClickButton` branching on `IsEQMainWidget(pWnd)`."
