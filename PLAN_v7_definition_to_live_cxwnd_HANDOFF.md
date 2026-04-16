# New-Session Handoff — v7 CXWnd Constructor Hook (Phase 7)

Read `CLAUDE.md` first. Repo: `X:\_Projects\EQSwitch\`.

**The standard:** *"Every feature you build, every bug you fix is a showcase of the absolute best work Claude Code can produce."*

## Current State (HEAD 5f3db77 on main)

PATH A (in-process login) is BLOCKED: `FindWindowByName` returns definition objects
(screen-piece blueprints), not live CXWnd widgets. All `SetEditText` and `ClickButton`
calls SEH-fault on definitions. PATH B (keyboard fallback) still works — login succeeds
via DirectInput injection when C# times out after ~5s.

## What's Done (don't redo)

### Phase 5 (HeapScanForWidget) — WORKS but returns definitions
- Scans entire heap for objects with eqmain vtable + CXStr name at +0x18
- Finds ALL 6 login widget definitions (UsernameEdit, PasswordEdit, ConnectButton, etc.)
- Definitions are blueprints, not live widgets — can't call SetEditText/ClickButton

### Phase 6 (FindLiveCXWnd) — EXHAUSTED, pivoting away
- CXWndManager tree walk: 523+ nodes, only main menu widgets (LOGIN/HELP/EXIT buttons)
- Login sub-screen CXWnds NOT in eqmain's CXWndManager at .data+0x214AF8
- FindWidgetByLabel("LOGIN"): WORKS — clicks main menu button, opens login sub-screen
- Heap cross-ref: scans for DWORDs matching definition addresses — FALSE POSITIVES
  - Run 1: 0x009FF4xx (stack vars, +0x18) — filtered by address threshold
  - Run 2: 0x0233F1xx (+0x28, child=0x72A1C870 in DLL code range) — filtered by MEM_IMAGE
- CXStr body scan 0x04-0x400: SIDL names NOT stored in CXWnd bodies at all
- Diagnostic dumps confirmed: only tooltip text (+0xE8), labels (+0x1A8), rare IDs (+0x1DC)

### What's been eliminated
- SIDL names not in CXWnd body at any offset
- m_pSidlPiece back-pointer not found (or gives false positives at low addresses)
- Login sub-screen not in any discovered CXWndManager
- GetChildItem thunks to eqgame code with NULL template table — fundamentally broken during login

## The Answer: CXWnd Constructor Hook

**How MQ2AutoLogin actually works:** MQ2 hooks the CXWnd constructor. Every widget creation
is intercepted, and the SIDL name + CXWnd pointer are stored in a hash map. MQ2's exports
(GetChildItem, etc.) are just game-code wrappers that don't work during login.

### Implementation Plan

1. **Find CSidlScreenWnd constructor in eqmain.dll**
   - Pattern scan for the constructor signature
   - Or: find it by tracing from known vtable entries (0x71F2D304, 0x71F2AA08)
   - Or: hook a known callsite (e.g., the XML screen-piece instantiation function)
   - eqmain base is 0x71E20000 (no ASLR) — addresses are stable across launches

2. **Hook with MinHook** (already in our build)
   - `MH_CreateHookEx(pOrigConstructor, &OurHook, &pTrampoline)`
   - The constructor receives the screen-piece definition as a parameter
   - Definition has SIDL name at +0x18 as CXStr

3. **In the hook:**
   ```cpp
   void __fastcall OurCSidlScreenWndCtor(void *thisPtr, void *edx, /* params */) {
       // Call original constructor first
       pTrampoline(thisPtr, edx, /* params */);
       // Now 'thisPtr' is the fully constructed CXWnd
       // Read the definition's SIDL name
       // Store in our map: name -> thisPtr
   }
   ```

4. **FindWindowByName reads from the map** — instant lookup, no scanning

5. **Clear map when eqmain unloads** (already detected by FindEQMainWndMgr)

### Alternative: Hook CXWnd::SetWindowName or similar
If the constructor is hard to find, look for any function that sets the SIDL name
on a CXWnd. This might be called during construction or initialization.

### Alternative: Hook from vtable dispatch
Every CXWnd vtable call goes through the vtable at +0x00. If we detour one of the
early vtable entries (like the destructor at vtable[0]), we can capture all CXWnds.
But this is noisier than hooking the constructor.

## Key Files
```
Native/mq2_bridge.cpp       — FindLiveCXWnd (Phase 6), HeapScanForWidget (Phase 5)
Native/mq2_bridge.h         — FindWidgetByLabel declaration
Native/login_state_machine.cpp — LOGIN button click in PHASE_WAIT_LOGIN_SCREEN
Native/eqswitch-hook.cpp    — MinHook usage reference (SetWindowPos/MoveWindow hooks)
Native/MinHook.h            — MinHook API
```

## Key Addresses (stable — no ASLR on eqmain)
- eqmain.dll base: 0x71E20000, size: 0x359000
- eqmain .data: RVA 0x12D000, size 0x21727C (WRITABLE)
- Definition vtables: 0x71F2D304 (Editbox), 0x71F2AA08 (Button), 0x71F2D370 (other)
- eqgame.exe: base 0x00E90000, GetChildItem core: 0x012F8330
- SIDL template table: [0x02063D08] — NULL during login
- GiveTime RVA in eqmain: 0x128B0

## Build & Deploy
```bash
cd X:/_Projects/eqswitch
bash Native/build-di8-inject.sh                    # Build DLL
cp Native/eqswitch-di8.dll "C:/Users/nate/proggy/Everquest/EQSwitch/"  # Deploy
# Launch from EQSwitch tray menu → right-click → account → login
# Log: C:/Users/nate/proggy/Everquest/Eqfresh/eqswitch-dinput8.log
```

## Memory References
- `project_eqswitch_v7_phase6_live_cxwnd.md` — full Phase 6 session notes
- `project_eqswitch_v7_widget_discovery_session.md` — Phase 5 session notes
- `reference_eqswitch_dalaya_rof2_offsets.md` — ALL discovered offsets, vtables, structs
- `project_eqswitch_v7_phase4_csharp.md` — C# wiring (PATH A/B)
- `project_eqswitch_v7_givetime_session.md` — native GiveTime detour
