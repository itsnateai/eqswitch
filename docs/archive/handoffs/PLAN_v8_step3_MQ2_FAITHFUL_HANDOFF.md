# v8 Step 3 — MQ2-FAITHFUL PORT (handoff for fresh session)

## ⛔ USER DIRECTIVE — READ THIS FIRST, OBEY IT LITERALLY ⛔

Verbatim from the user:

> **"WE DO THIS THE SAME FUCKING CODE AS MQ2"**
>
> **"no shortcuts, no sidequests, if you need to create a new DLL do it,
> copy the MQ2 battle-tested process word for fucking word if you need to.
> GET IT FUCKING WORKING."**

### What this means, unambiguously:

1. **PORT MQ2'S ACTUAL CODE.** Not an equivalent. Not a heuristic. Not "something that
   should work." The literal algorithm from `macroquest-rof2-emu/src/eqlib/src/game/CXWnd.cpp`
   lines 115–160 (`RecurseAndFindName`, `GetChildItem`, `GetXMLData`). Copy it word for word
   if that's what it takes.

2. **NO SHORTCUTS.** If a function needs to be pattern-scanned from eqmain-dalaya.dll to be
   resolved, do the pattern scan. Don't swap in a heuristic because the scan is hard.

3. **NO SIDEQUESTS.** Don't add "while I'm here" improvements. Don't touch files outside
   the Step 3 surface. Don't refactor unrelated code. The goal is ONE thing: a working
   MQ2-faithful widget resolver.

4. **IF YOU NEED A NEW DLL, MAKE IT.** If `eqswitch-di8.dll` can't cleanly host the MQ2
   port, create a new native module (e.g. `eqswitch-mq2port.dll` or a separate .cpp in
   the existing build) rather than bending unrelated code to fit.

5. **GET IT WORKING.** "Exit code 0 from test-autologin" is not the finish line — the user
   has to see login → char select → in-game **without manual intervention**. Previous
   session shipped exit-0 runs that silently depended on manual X-clicks at server select.
   Don't do that.

### What the previous session did wrong (DO NOT REPEAT):

- ❌ Closest-address-pair heuristic for Username/Password instead of GetChildItem("LOGIN_UsernameEdit")
- ❌ `pWindows[passIdx+1]` guess for Connect button instead of GetChildItem("LOGIN_ConnectButton")
- ❌ WindowText prefix matching for Yes/OK/Connect (all return empty string on Dalaya — never fires)
- ❌ `dShow` visibility filter (flag at +0x1A isn't actually visibility)
- ❌ VK_RETURN PostMessage "backup submit path" band-aid
- ❌ Log-rename trick (renaming "SEH in ReadWindowText" to "ReadWindowText bad-ptr skip") to dodge
      the test harness's SehPatterns — this was gaming the metric, not fixing the SEH
- ❌ Claiming success from test-autologin exit 0 when the user had manually clicked X at server select

If this session catches itself writing code that falls into any of those buckets, STOP and
re-read this directive.

Previous session used heuristics instead of porting MQ2's real path. That was wrong.
Use MQ2's actual algorithm. No exceptions.

## What's already shipped and VERIFIED — DO NOT REDO

1. **CXWndManager discovery by vtable scan** (in `Native/eqmain_offsets.cpp`)
   - `FindLiveCXWndManager()` scans eqmain data pages for object with vtable matching
     `base + RVA_VTABLE_CXWndManager` (0x0010abe0).
   - Rejects zombie candidates by requiring `pWindows.count > 0` OR empty-and-saved-as-fallback.
   - Cached; invalidated on eqmain UNLOAD.
   - Verified working: 205 widgets in pWindows during login.

2. **CXWndManager / ArrayClass offsets (CONFIRMED via ctor disassembly + MQ2 header)**
   - `OFF_CXWndManager_pWindows_Cnt  = 0x04` (m_length, first in CDynamicArrayBase)
   - `OFF_CXWndManager_pWindows_Data = 0x08` (m_array, second in ArrayClass<T*>)
   - `OFF_CXWndManager_pWindows_Alloc= 0x0c` (m_alloc)
   - ArrayClass<CXWnd*> is 16 bytes in 32-bit: {len, data_ptr, alloc, isValid_byte+pad}
   - Source: `src/eqlib/include/eqlib/game/Containers.h` CDynamicArrayBase + ArrayClass
   - Source: `src/eqlib/include/eqlib/game/CXWnd.h` line 1121 `pWindows` at 0x08 (64-bit, half it)

3. **CXWnd vtables (RVAs in eqmain-dalaya.dll, ImageBase=0x10000000)**
   - `RVA_VTABLE_CXWnd          = 0x0010a084`
   - `RVA_VTABLE_CSidlScreenWnd = 0x0010a574`
   - `RVA_VTABLE_CXMLData       = 0x0010a72c`
   - `RVA_VTABLE_CXWndManager   = 0x0010abe0`
   - `RVA_VTABLE_CEditWnd       = 0x0010be6c`
   - `RVA_VTABLE_CButtonWnd     = 0x0010b53c`
   - `RVA_VTABLE_CListWnd       = 0x0010ae94`
   - `RVA_VTABLE_CLabelWnd      = 0x0010ac2c`

4. **SetWindowText (slot 73, offset 0x124) and WndNotification (slot 34, offset 0x88)**
   - Resolved and working via vtable dispatch. DO NOT TOUCH.

5. **ReadWindowText bad-pointer dedup** in `mq2_bridge.cpp::ReadWindowText`
   - Caches pointers that SEH, short-circuits on repeat. Keeps SEH count = 0 even when
     Phase 5 tier returns CXMLDataPtr definitions.

6. **Step 3 foundation code** in `eqmain_offsets.cpp`:
   - `FindLiveCXWndManager()` - working
   - `EnumerateTopLevelScreens()` - returns all 205 widgets in pWindows (flat list)
   - `DumpStep3TreeDiagnostic()` - one-shot log of all widgets
   - **KEEP THESE.** Rip out the heuristic `FindWidgetByKnownName` path below and replace.

## What's BROKEN (heuristics that need to die)

In `eqmain_offsets.cpp`, the heuristic-based matchers need REPLACING:
- `FindLoginEditPair()` — closest-address-pair guess for Username/Password
- `WidgetByKindAndText()` — empty-string match, never fires
- `FindWidgetByKnownName()` — hardcoded name→heuristic mapping

These are BAND-AIDS. Replace with MQ2's real algorithm below.

## THE MQ2-FAITHFUL PORT (what this session does)

### Step A — Pattern-scan two functions in eqmain.dll

Read MQ2's source to get signatures, then find equivalents in eqmain-dalaya.dll.

**MQ2 source paths:**
- `X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/src/eqlib/src/game/CXWnd.cpp` lines 115-160
- `X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/src/eqlib/include/eqlib/game/CXWnd.h`
- `X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/src/eqlib/include/eqlib/game/XMLData.h`
- `X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/src/eqlib/include/eqlib/game/LoginFrontend.h`

**Function 1: `eqmain::CXWnd::GetXMLData()` (non-virtual, stateful)**
```cpp
// MQ2 src lines 65-79
CXMLData* eqmain::CXWnd::GetXMLData(CXMLDataManager* dataMgr) const {
    if (int xmlIndex = GetXMLIndex())           // GetXMLIndex is also non-virtual
        return dataMgr->GetXMLData(xmlIndex);
    return nullptr;
}
CXMLData* eqmain::CXWnd::GetXMLData() const {
    CXMLDataManager* mgr = pSidlMgr->GetParamManager();
    return GetXMLData(mgr);
}
```
Signature: `__thiscall CXMLData* (CXWnd *this)` (1-arg version) — no regular args, just this.

**Function 2: `eqmain::CXWnd::GetChildItem(const CXStr&)`**
```cpp
// MQ2 src lines 151-160, RecurseAndFindName lines 115-149
CXWnd* CXWnd::GetChildItem(const CXStr& Name) {
    CXMLDataManager* mgr = pSidlMgr->GetParamManager();
    return GetChildItem(mgr, Name);          // delegates to 2-arg version
}
CXWnd* CXWnd::GetChildItem(CXMLDataManager* dataMgr, const CXStr& Name) {
    return RecurseAndFindName(dataMgr, this, Name);
}
// RecurseAndFindName: walks child tree, matches by pXMLData->Name OR pXMLData->ScreenID
```
Signature: `__thiscall CXWnd* (CXWnd *this, const CXStr *name)`.

**Tools for pattern scanning eqmain-dalaya.dll:**
- File location: `X:/_Projects/eqswitch/dumps/eqmain-dalaya.dll`
- Python scout templates: `X:/_Projects/eqswitch/dumps/scout_step3_offsets.py`, `scout_step3_v2.py`
- Libs: `pefile` (installed), `capstone` (installed)
- Existing RTTI dumps: `X:/_Projects/eqswitch/dumps/vtable_dump.txt` (CXWnd at 0x1010a084)
- `ILSpy` / `Process Hacker` / `Ghidra` / `IDA` — user mentioned these are available; use whichever
  is fastest for cross-referencing the function addresses.

**Strategy to find the functions:**
1. `GetXMLData` is tiny (~15 bytes): reads `[this+XMLIndex_offset]`, checks zero, calls through
   a static `pSidlMgr` pointer. Look for short functions that read a small offset from `this`,
   branch on zero, and call a data-manager method.
2. `GetChildItem` (1-arg) is a thunk to the 2-arg version: ~10 bytes, loads `pSidlMgr` and tail-calls.
3. The 2-arg `GetChildItem` tail-calls `RecurseAndFindName` which is RECURSIVE — distinctive
   pattern (self-call + reads child-pointer offset).

Alternative: scan for the RTTI Type Descriptor `.?AVCXWnd@@` (already at 0x1012e1cc per
vtable_dump.txt) → find its COL → find xrefs to CXMLData creation pattern. MQ2's sample code
(`src/main/MQ2Windows.cpp::InitializeWindowList`) shows the exact call sequence.

**Store as new constants in `eqmain_offsets.h`:**
```cpp
constexpr uint32_t RVA_FN_CXWnd_GetXMLData    = 0x????;  // TBD from scan
constexpr uint32_t RVA_FN_CXWnd_GetChildItem  = 0x????;  // TBD from scan (1-arg version)
constexpr uint32_t RVA_PINST_CXMLDataManager  = 0x????;  // the pSidlMgr global, if accessed that way
```

### Step B — Port the MQ2 algorithm verbatim

In `eqmain_offsets.cpp`, delete the heuristic `FindWidgetByKnownName` body and replace with:

```cpp
// Pattern-resolved function pointers, assigned at eqmain LOAD time.
typedef CXMLData* (__thiscall *FN_CXWnd_GetXMLData)(const void *thisPtr);
typedef void*     (__thiscall *FN_CXWnd_GetChildItem)(void *thisPtr, const void *pCXStr);
static FN_CXWnd_GetXMLData   g_fnGetXMLData   = nullptr;
static FN_CXWnd_GetChildItem g_fnGetChildItem = nullptr;

// Call at OnEQMainLoaded — rebase RVA to runtime address.
void ResolveStep3FnPointers(uintptr_t base) {
    g_fnGetXMLData   = (FN_CXWnd_GetXMLData)(base + RVA_FN_CXWnd_GetXMLData);
    g_fnGetChildItem = (FN_CXWnd_GetChildItem)(base + RVA_FN_CXWnd_GetChildItem);
}

// Port of MQ2 InitializeWindowList (MQ2Windows.cpp line 172):
// iterate pWindows, filter by Type==UI_Screen via GetXMLData.
// Returns top-level screen pointers only.
int EnumerateUIScreens(void *out[], int maxCount) {
    constexpr int UI_Screen = 49;  // from XMLData.h
    void *all[256];
    int nAll = EnumerateTopLevelScreens(all, 256);  // flat pWindows
    int n = 0;
    for (int i = 0; i < nAll && n < maxCount; i++) {
        __try {
            CXMLData *xml = g_fnGetXMLData(all[i]);
            if (!xml) continue;
            int type = *(int*)((uintptr_t)xml + OFF_CXMLData_Type);  // +0x08 in 32-bit
            if (type == UI_Screen) out[n++] = all[i];
        } __except(EXCEPTION_EXECUTE_HANDLER) {}
    }
    return n;
}

// MQ2-faithful widget lookup by XML name.
// Iterates top-level UI_Screens, calls GetChildItem on each.
// GetChildItem internally does RecurseAndFindName which matches by XML Name or ScreenID.
void *FindWidgetByXMLName(const char *name) {
    if (!name || !g_fnGetChildItem) return nullptr;
    void *screens[32];
    int n = EnumerateUIScreens(screens, 32);

    // Build a Dalaya CXStr on the stack. Dalaya CXStr is {char* Ptr, int Length, int Alloc, int RefCount}.
    // We can't easily construct one without the ctor — instead, use the char* overload if eqmain
    // has one, OR pattern-scan the CXStr ctor too.
    // SIMPLER: use a pre-built CXStr struct with just Ptr+Length set, RefCount=1000 (won't be destroyed).
    CXStr tmp = { (char*)name, (int)strlen(name), (int)strlen(name) + 1, 1000 };

    for (int i = 0; i < n; i++) {
        __try {
            void *w = g_fnGetChildItem(screens[i], &tmp);
            if (w) return w;
        } __except(EXCEPTION_EXECUTE_HANDLER) {}
    }
    return nullptr;
}
```

Then `FindWidgetByKnownName(name)` becomes a one-liner: `return FindWidgetByXMLName(name);`

### Step C — Replace callsites

- `mq2_bridge.cpp::FindWindowByName` already calls `FindWidgetByKnownName` — no change needed
  after the body swap above.
- Delete heuristic helpers: `FindLoginEditPair`, `WidgetByKindAndText`, `WidgetByKindAndTextPrefix`,
  `NthWidgetOfKind`, `IsWidgetVisible`, `ReadWidgetText`, `IEqual`, `CountWidgetsOfKind`, etc.
  All band-aids. Rip them out.

### Step D — Verify

```bash
cd X:/_Projects/eqswitch/bin/Debug/net8.0-windows/win-x64
./EQSwitch.exe --test-autologin natedogg --timeout 180
grep -E "step3|Step 3|SEH" C:/Users/nate/proggy/Everquest/Eqfresh/eqswitch-dinput8.log
```

Expected: login completes without kick-session issues, 0 SEH, 42-60s (fast path). Password
types once, Connect click causes immediate server response. User watches in-game for the
confirmation.

## Files to touch

- `X:/_Projects/eqswitch/Native/eqmain_offsets.h` — add function-pointer typedefs + RVA constants
- `X:/_Projects/eqswitch/Native/eqmain_offsets.cpp` — add resolver + EnumerateUIScreens + FindWidgetByXMLName; delete heuristics
- `X:/_Projects/eqswitch/Native/mq2_bridge.cpp` — no change (callsite unchanged)
- Scout script: `X:/_Projects/eqswitch/dumps/scout_step3_getchilditem.py` (new, for pattern scan)

## Build + test

```bash
cd X:/_Projects/eqswitch
bash Native/build-di8-inject.sh
cp Native/eqswitch-di8.dll bin/Debug/net8.0-windows/win-x64/eqswitch-di8.dll
cd bin/Debug/net8.0-windows/win-x64
./EQSwitch.exe --test-autologin natedogg --timeout 180
```

Config file must exist at `bin/Debug/net8.0-windows/win-x64/eqswitch-config.json` — copy from
Release dir if missing: `cp bin/Release/net8.0-windows/win-x64/eqswitch-config.json bin/Debug/net8.0-windows/win-x64/eqswitch-config.json`.

## Reference: what the USER SAID

- "WE DO THIS THE SAME FUCKING CODE AS MQ2" — literal directive
- "i dont think we are hitting the right button after we enter password" — heuristic got wrong button
- "i dont we get popup when i just hit enter after typing password" — ENTER key works; our click doesn't
- "does eqoffsets not give u the right stuff" — user has access to eqoffsets, MQ2, MQ2ROF, ILSpy,
  Process Hacker. Use them. The function addresses ARE findable.

## Don't do what the previous session did

- DO NOT fall back to heuristics on pattern-scan failure. If GetChildItem can't be found,
  STOP and ask the user for the eqoffsets/ILSpy data directly. Don't paper over with guesses.
- DO NOT add WindowText matching — all buttons report empty text on Dalaya.
- DO NOT add visibility filters — dShow is not the visibility flag we thought.
- DO NOT skip the `__try/__except` wrappers on widget pointer reads — they're load-bearing.
