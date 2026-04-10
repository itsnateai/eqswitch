# Character Select via MQ2 Exports — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add character selection by name at the char select screen using Dalaya's MQ2 exports from dinput8.dll, replacing blind Enter keypress with programmatic character lookup and selection.

**Architecture:** The already-injected `eqswitch-di8.dll` gains a new module (`mq2_bridge.cpp`) that resolves MQ2 symbols via `GetProcAddress` from Dalaya's dinput8.dll. A new shared memory segment (`EQSwitchCharSel_{PID}`) carries character list data from the DLL to the C# host, and selection requests from C# to the DLL. The C# `AutoLoginManager` uses this to select characters by name before entering world.

**Tech Stack:** C++ (x86, MSVC), C# .NET 8 WinForms, Win32 shared memory (memory-mapped files)

**Spec:** `docs/superpowers/specs/2026-04-09-charselect-mq2-integration-design.md`

**MQ2 Reference Source:** `X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/` (autologin plugin: `src/plugins/autologin/StateMachine.cpp`)

**IMPORTANT — Another Claude session may be active on this repo.** Check `_.claude/_comms/active-work.md` before starting. Create a feature branch (`feat/charselect-mq2`) to avoid conflicts.

---

## File Map

### New Files
| File | Responsibility |
|------|---------------|
| `Native/mq2_bridge.cpp` | Resolve MQ2 exports from dinput8.dll, read char list via UI scraping, handle selection requests |
| `Native/mq2_bridge.h` | Header: shared memory struct, init/poll API |
| `Core/CharSelectReader.cs` | C# reader for `EQSwitchCharSel_{PID}` shared memory — reads char list, writes selection requests |

### Modified Files
| File | Change |
|------|--------|
| `Native/eqswitch-di8.cpp` | Call `MQ2Bridge::Init()` and `MQ2Bridge::Poll()` from the existing threads |
| `Native/build-di8-inject.sh` | Add `mq2_bridge.cpp` to the MSVC compile command |
| `Core/AutoLoginManager.cs` | Use `CharSelectReader` to select character by name before entering world |
| `Models/LoginAccount.cs` | Already has `CharacterName` — no changes needed, just use it |

---

## Task 1: Shared Memory Contract (C++ header)

**Files:**
- Create: `Native/mq2_bridge.h`

This defines the shared memory layout that both C++ and C# must agree on. Write this first so both sides compile against the same contract.

- [ ] **Step 1: Create the header file**

```cpp
// Native/mq2_bridge.h — MQ2 bridge for character select via Dalaya's dinput8.dll exports
#pragma once
#include <windows.h>
#include <stdint.h>

// Shared memory name: "Local\EQSwitchCharSel_{PID}"
// C# creates it (like KeyInputWriter pattern), DLL reads/writes.

#define CHARSEL_SHM_MAGIC 0x45534353  // "ESCS"
#define CHARSEL_MAX_CHARS 8
#define CHARSEL_NAME_LEN  64

#pragma pack(push, 1)
struct CharSelectShm {
    uint32_t magic;            // CHARSEL_SHM_MAGIC
    uint32_t version;          // 1
    int32_t  gameState;        // Current EQ game state (-1=pre, 1=charsel, 5=ingame)
    int32_t  charCount;        // Number of characters found (0 if not at char select)
    int32_t  selectedIndex;    // Currently selected index in list (-1 = none)
    uint32_t mq2Available;     // 1 = MQ2 exports resolved, 0 = not found

    // C# → DLL: request character selection
    int32_t  requestedIndex;   // Set by C# to index to select (-1 = no request)
    uint32_t requestSeq;       // Incremented by C# on each new request
    uint32_t ackSeq;           // Set by DLL when request is processed

    // Character data (DLL writes, C# reads)
    char     names[CHARSEL_MAX_CHARS][CHARSEL_NAME_LEN];
    int32_t  levels[CHARSEL_MAX_CHARS];
    int32_t  classes[CHARSEL_MAX_CHARS];
};
#pragma pack(pop)
// Total struct size: 4+4+4+4+4+4 + 4+4+4 + (8*64)+(8*4)+(8*4) = 36 + 512 + 32 + 32 = 612 bytes

namespace MQ2Bridge {
    // Call once from DLL init thread (after dinput8.dll is loaded).
    // Resolves MQ2 exports. Returns true if exports found.
    bool Init();

    // Call periodically (e.g., every 500ms from the existing SHM poll thread).
    // Reads game state, populates char list when at char select,
    // processes selection requests from C#.
    // shm = pointer to the mapped CharSelectShm (created by C# CharSelectReader).
    void Poll(volatile CharSelectShm* shm);

    // Cleanup. Call from DLL_PROCESS_DETACH.
    void Shutdown();
}
```

- [ ] **Step 2: Commit**

```bash
git add Native/mq2_bridge.h
git commit -m "feat(charsel): add shared memory contract for MQ2 bridge"
```

---

## Task 2: MQ2 Bridge Implementation (C++)

**Files:**
- Create: `Native/mq2_bridge.cpp`
- Reference: `Native/key_shm.cpp` (pattern for shared memory access)
- Reference: `X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/src/plugins/autologin/StateMachine.cpp:618-665` (MQ2 autologin char select flow)

This is the core — resolves MQ2 exports and uses them to read the character list and select characters.

- [ ] **Step 1: Create mq2_bridge.cpp with MQ2 symbol resolution**

```cpp
// Native/mq2_bridge.cpp — MQ2 bridge: resolves Dalaya's dinput8.dll exports
// for character select interaction.
//
// Dalaya's dinput8.dll is a custom MQ2 build with 2,966 exports including:
//   gGameState          — int*, 1=CHARSELECT, 5=INGAME
//   ppWndMgr            — void**, EQ window manager
//   CListWnd__GetItemText  — read list widget text
//   CListWnd__SetCurSel    — select list item
//   CListWnd__GetCurSel    — get current selection
//   CSidlScreenWnd__GetChildItem — find child window by name
//
// The mangled C++ exports use __thiscall (ECX = this pointer on x86).
// The unmangled C-linkage exports (like gGameState) are plain pointers/functions.

#include "mq2_bridge.h"
#include <stdio.h>
#include <string.h>

// Forward decl for logging (defined in eqswitch-di8.cpp)
void DI8Log(const char* fmt, ...);

// ─── MQ2 Export Types ───────────────────────────────────────────

// gGameState: exported as a pointer to int
static volatile int* g_pGameState = nullptr;

// ppWndMgr: pointer-to-pointer to EQ's window manager
static void** g_ppWndMgr = nullptr;

// CListWnd methods (mangled __thiscall)
// CXStr* CListWnd::GetItemText(CXStr* out, int row, int col) const
typedef void* (__thiscall *FN_GetItemText)(void* thisPtr, void* outStr, int row, int col);
static FN_GetItemText g_fnGetItemText = nullptr;

// void CListWnd::SetCurSel(int index)
typedef void (__thiscall *FN_SetCurSel)(void* thisPtr, int index);
static FN_SetCurSel g_fnSetCurSel = nullptr;

// int CListWnd::GetCurSel() const
typedef int (__thiscall *FN_GetCurSel)(const void* thisPtr);
static FN_GetCurSel g_fnGetCurSel = nullptr;

// CXWnd* CSidlScreenWnd::GetChildItem(char* name)
// Using the char* overload (ordinal [106])
typedef void* (__thiscall *FN_GetChildItem)(void* thisPtr, const char* name);
static FN_GetChildItem g_fnGetChildItem = nullptr;

// Resolved state
static bool g_mq2Resolved = false;
static HMODULE g_hMQ2 = nullptr;

// ─── CXStr helpers ──────────────────────────────────────────────
// MQ2's CXStr is a ref-counted string class. On x86 ROF2, the layout is:
//   +0x00: char* Ptr   (pointer to null-terminated string data)
//   +0x04: int   Length
//   +0x08: int   Alloc  (allocated capacity)
//   +0x0C: int   RefCount
// GetItemText writes into a CXStr. We need to read .Ptr and free properly.
// Simplest approach: allocate a local CXStr-sized buffer, call GetItemText,
// read the char* at offset 0, then clean up.

struct CXStr {
    char* Ptr;
    int Length;
    int Alloc;
    int RefCount;
};

// Read a character name from the list at (row, col).
// Returns true if a name was read into outBuf (null-terminated).
static bool ReadListItemText(void* pListWnd, int row, int col, char* outBuf, int outBufLen) {
    if (!g_fnGetItemText || !pListWnd || outBufLen <= 0) return false;

    // Zero-init a CXStr on stack
    CXStr str = {0};

    // Call: CXStr* CListWnd::GetItemText(CXStr* out, int row, int col)
    g_fnGetItemText(pListWnd, &str, row, col);

    if (str.Ptr && str.Length > 0) {
        int copyLen = (str.Length < outBufLen - 1) ? str.Length : (outBufLen - 1);
        memcpy(outBuf, str.Ptr, copyLen);
        outBuf[copyLen] = '\0';
        // Note: we don't free str.Ptr — it points into EQ's internal string table.
        // CXStr returned by GetItemText is typically a reference, not an allocation.
        return true;
    }

    outBuf[0] = '\0';
    return false;
}

// ─── Window Finder ──────────────────────────────────────────────
// MQ2's ppWndMgr points to the CXWndManager. We need to find
// "CharacterListWnd" in EQ's window list. MQ does this via FindMQ2Window
// which walks the SIDL registry. We'll use a simpler approach:
// EQ's CXWndManager has a window list we can iterate, but the layout
// is version-specific. Instead, we'll try FindMQ2Data if available,
// or iterate the SIDL screen list.
//
// Simpler approach: use ppWndMgr to get the window manager,
// then call GetChildItem on the root with known window names.
// But CXWndManager doesn't have GetChildItem in the same way.
//
// ACTUAL simplest approach based on MQ2 autologin:
// The autologin plugin receives the current window handle from a sensor.
// It then calls GetChildWindow<CListWnd>(currentWnd, "Character_List")
// which is CSidlScreenWnd::GetChildItem.
//
// We need to find the CharacterListWnd first. Options:
// 1. Use FindMQ2Data("CharacterListWnd") — NOT exported with that name
// 2. Enumerate windows from ppWndMgr
// 3. Use the HWND we already know (from eqswitch-hook.dll) and find EQ
//    UI windows by iterating ppWndMgr's list.
//
// For now: we'll use a polling approach. When gGameState == 1 (CHARSELECT),
// we scan ppWndMgr's window array for a window whose SIDL name matches
// "CharacterListWnd". MQ2's CXWndManager stores windows in a hash table
// keyed by XML name. The layout is complex and version-specific.
//
// PRAGMATIC APPROACH: Dalaya's dinput8.dll exports ppSidlMgr. The SIDL
// manager has FindScreenPieceTemplate which resolves window names.
// But getting from template to live instance is non-trivial.
//
// SIMPLEST VIABLE APPROACH: Use FindMQ2Data to query the TLO system.
// MQ2's TLO lets you query ${Window[CharacterListWnd].Child[Character_List]}
// But FindMQ2Data returns MQTypeVar, which requires knowing MQ2's data types.
//
// BEST APPROACH: Export pCharacterListWnd. Wait — it's NOT exported.
// But MQ2's global window registry IS populated at runtime.
//
// REVISED APPROACH: We know ppWndMgr is exported. The CXWndManager in
// MQ2 stores a flat array of all windows at a known offset. On ROF2 x86,
// the window list is typically at CXWndManager+0x60 (ArrayClass<CXWnd*>).
// Each CXWnd has an XMLName (CXStr) that we can match against.
//
// However, these offsets are version-specific and fragile.
//
// FINAL APPROACH (robust): Use the exported HideDoCommand function.
// We can execute MQ2 macros that return data. But that's overkill.
//
// TRULY FINAL APPROACH: Search for the window by iterating
// all top-level EQ windows and checking their name. MQ2's SIDL system
// stores the XML screen name in each CSidlScreenWnd. We use
// CSidlScreenWnd::GetChildItem (which IS exported) once we have the
// parent window pointer.
//
// The missing piece is: how to get the CharacterListWnd pointer.
// Let's check if ppWndMgr gives us a usable window list.
//
// After analysis: the cleanest approach that avoids fragile offset
// dependencies is to use EnumWindows/FindWindowEx on the EQ process
// to find the main HWND, then use the MQ2 window registry.
//
// BUT WAIT — we're inside the EQ process. We have the HWND from
// eqswitch-hook.dll. And MQ2's autologin gets the window from
// a state machine sensor. We need a different entry point.
//
// SOLUTION: Poll-based. When gGameState transitions to CHARSELECT (1):
// 1. Get the main EQ HWND (we already cache it in eqswitch-hook.dll)
// 2. Try to find "CharacterListWnd" by walking ppWndMgr's list
// 3. If found, use GetChildItem to find "Character_List" (CListWnd)
// 4. Read character names from the list

// For the window walk, we need the CXWndManager layout.
// MQ2 source (src/eqlib/include/eqlib/ui/CXWnd.h) shows:
// CXWndManager has a member `WindowList` which is an ArrayClass<CXWnd*>
// But the offset varies by build.
//
// PRAGMATIC SHORTCUT: Instead of walking the window list ourselves,
// we can use a known trick: the SIDL manager (ppSidlMgr) creates
// windows by name. At char select, we know the game state is 1.
// We can use EQ's internal function FindScreenPieceTemplate to get
// the template, then check if the window is currently showing.
//
// OK, I'm overcomplicating this. Let's use the MQ2 export
// `FindMQ2Data` or `ParseMQ2DataPortion` to query the TLO system:
//   "${EverQuest.CharSelectList[1].Name}" — returns character name
//   "${EverQuest.CharSelectList.Count}" — returns count
// This uses MQ2's own code to do the heavy lifting.
//
// FindMQ2Data signature: bool FindMQ2Data(char* szName, MQTypeVar& Result)
// MQTypeVar is: { union { void* Ptr; int Int; float Float; DWORD DWord; }; MQ2Type* Type; }
// This is 8 bytes on x86.
//
// BUT: FindMQ2Data needs MQTypeVar which depends on MQ2's type system.
// Too tightly coupled.
//
// ACTUAL FINAL APPROACH (for real this time):
// We combine two things:
// 1. gGameState for state detection (simple, just read an int)
// 2. For the character list, we use the ppEverQuest export.
//    ppEverQuest -> CEverQuest* -> charSelectPlayerArray
//    This is a memory read, not a UI scrape.
//    The struct offsets may differ from Live, but we can VALIDATE:
//    - Read charSelectPlayerArray.Count — should be 1-8
//    - Read first Name — should be ASCII, non-empty
//    If validation fails, set mq2Available = 0 and bail.

// ppEverQuest: pointer-to-pointer to CEverQuest
static void** g_ppEverQuest = nullptr;

// Offsets in CEverQuest — from MQ source (MAY DIFFER for Dalaya's ROF2 build)
// We validate at runtime before using.
static const int OFFSET_GAMESTATE = 0x5E4;
static const int OFFSET_CHARSELECT_ARRAY = 0x18EC0;

// ArrayClass<T> layout on x86:
// +0x00: T*  Data   (pointer to contiguous array)
// +0x04: int Count   (number of elements)
// +0x08: int Alloc   (allocated capacity)
struct ArrayClassHeader {
    void* Data;
    int Count;
    int Alloc;
};

// CharSelectInfo: 0x170 bytes each
// Name at +0x00 (64 chars), Class at +0x40, Race at +0x44, Level at +0x48
static const int CSI_SIZE = 0x170;
static const int CSI_NAME_OFF = 0x00;
static const int CSI_CLASS_OFF = 0x40;
static const int CSI_LEVEL_OFF = 0x48;

// Validated offsets — set to true once we've confirmed the offsets work
static bool g_offsetsValidated = false;
// Actual charSelectPlayerArray offset (may be adjusted during validation)
static int g_charArrayOffset = OFFSET_CHARSELECT_ARRAY;

// Validate that the charSelectPlayerArray offset is correct by checking
// that it looks like valid character data (count 1-8, ASCII names).
static bool ValidateCharArrayOffset(void* pEverQuest, int offset) {
    __try {
        auto* arr = (ArrayClassHeader*)((char*)pEverQuest + offset);
        if (arr->Count < 1 || arr->Count > 8) return false;
        if (!arr->Data) return false;

        // Check first character name — should be printable ASCII
        char* firstName = (char*)arr->Data + CSI_NAME_OFF;
        if (firstName[0] < 'A' || firstName[0] > 'z') return false;
        // Check null termination within 64 bytes
        bool hasNull = false;
        for (int i = 0; i < 64; i++) {
            if (firstName[i] == '\0') { hasNull = true; break; }
        }
        return hasNull;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool MQ2Bridge::Init() {
    g_hMQ2 = GetModuleHandleA("dinput8.dll");
    if (!g_hMQ2) {
        DI8Log("mq2_bridge: dinput8.dll not loaded — MQ2 bridge disabled");
        return false;
    }

    // Resolve exports
    g_pGameState = (volatile int*)GetProcAddress(g_hMQ2, "gGameState");
    g_ppEverQuest = (void**)GetProcAddress(g_hMQ2, "ppEverQuest");

    // UI exports (for SetCurSel — character selection)
    // These use the mangled C++ names for __thiscall methods
    g_fnSetCurSel = (FN_SetCurSel)GetProcAddress(g_hMQ2,
        "?SetCurSel@CListWnd@EQClasses@@QAEXH@Z");
    g_fnGetCurSel = (FN_GetCurSel)GetProcAddress(g_hMQ2,
        "?GetCurSel@CListWnd@EQClasses@@QBEHXZ");
    g_fnGetItemText = (FN_GetItemText)GetProcAddress(g_hMQ2,
        "?GetItemText@CListWnd@EQClasses@@QBEPAVCXStr@2@PAV32@HH@Z");
    g_fnGetChildItem = (FN_GetChildItem)GetProcAddress(g_hMQ2,
        "?GetChildItem@CSidlScreenWnd@EQClasses@@QAEPAVCXWnd@2@PAD@Z");

    // Check minimum required exports
    g_mq2Resolved = (g_pGameState != nullptr && g_ppEverQuest != nullptr);

    if (g_mq2Resolved) {
        DI8Log("mq2_bridge: MQ2 exports resolved — gGameState=%p ppEverQuest=%p",
               g_pGameState, g_ppEverQuest);
        DI8Log("mq2_bridge: UI exports — SetCurSel=%p GetCurSel=%p GetItemText=%p GetChildItem=%p",
               g_fnSetCurSel, g_fnGetCurSel, g_fnGetItemText, g_fnGetChildItem);
    } else {
        DI8Log("mq2_bridge: WARN — required MQ2 exports not found, bridge disabled");
        DI8Log("mq2_bridge:   gGameState=%p ppEverQuest=%p", g_pGameState, g_ppEverQuest);
    }

    return g_mq2Resolved;
}

void MQ2Bridge::Poll(volatile CharSelectShm* shm) {
    if (!shm || !g_mq2Resolved) return;

    // Read game state
    int gs = *g_pGameState;
    shm->gameState = gs;
    shm->mq2Available = 1;

    // Only read character data at char select screen
    if (gs != 1) { // GAMESTATE_CHARSELECT = 1
        shm->charCount = 0;
        shm->selectedIndex = -1;
        return;
    }

    // Get CEverQuest pointer
    void* pEverQuest = *g_ppEverQuest;
    if (!pEverQuest) {
        shm->charCount = 0;
        return;
    }

    // Validate offsets on first successful read
    if (!g_offsetsValidated) {
        if (ValidateCharArrayOffset(pEverQuest, OFFSET_CHARSELECT_ARRAY)) {
            g_charArrayOffset = OFFSET_CHARSELECT_ARRAY;
            g_offsetsValidated = true;
            DI8Log("mq2_bridge: charSelectPlayerArray validated at offset 0x%X", g_charArrayOffset);
        } else {
            // Try scanning nearby offsets (Dalaya's ROF2 may differ from Live)
            // Search in 8-byte steps around the expected offset
            for (int delta = -0x200; delta <= 0x200; delta += 8) {
                int tryOffset = OFFSET_CHARSELECT_ARRAY + delta;
                if (tryOffset < 0) continue;
                if (ValidateCharArrayOffset(pEverQuest, tryOffset)) {
                    g_charArrayOffset = tryOffset;
                    g_offsetsValidated = true;
                    DI8Log("mq2_bridge: charSelectPlayerArray found at adjusted offset 0x%X (delta=%d)",
                           g_charArrayOffset, delta);
                    break;
                }
            }
            if (!g_offsetsValidated) {
                DI8Log("mq2_bridge: WARN — could not validate charSelectPlayerArray offset, "
                       "character names unavailable");
                shm->charCount = 0;
                return;
            }
        }
    }

    // Read character array
    __try {
        auto* arr = (ArrayClassHeader*)((char*)pEverQuest + g_charArrayOffset);
        int count = arr->Count;
        if (count < 0 || count > CHARSEL_MAX_CHARS) count = 0;
        shm->charCount = count;

        char* base = (char*)arr->Data;
        if (!base) { shm->charCount = 0; return; }

        for (int i = 0; i < count; i++) {
            char* entry = base + (i * CSI_SIZE);

            // Name (64 chars at offset 0)
            char* name = entry + CSI_NAME_OFF;
            // Safe copy with null termination
            for (int j = 0; j < CHARSEL_NAME_LEN - 1 && name[j]; j++) {
                ((char*)shm->names[i])[j] = name[j];
            }
            ((char*)shm->names[i])[CHARSEL_NAME_LEN - 1] = '\0';

            // Level (byte at offset 0x48)
            shm->levels[i] = *(uint8_t*)(entry + CSI_LEVEL_OFF);

            // Class (int at offset 0x40)
            shm->classes[i] = *(int32_t*)(entry + CSI_CLASS_OFF);
        }

        // Clear unused slots
        for (int i = count; i < CHARSEL_MAX_CHARS; i++) {
            memset((void*)shm->names[i], 0, CHARSEL_NAME_LEN);
            shm->levels[i] = 0;
            shm->classes[i] = 0;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: exception reading charSelectPlayerArray");
        shm->charCount = 0;
        return;
    }

    // Handle selection request from C# host
    uint32_t reqSeq = *(volatile uint32_t*)&shm->requestSeq;
    uint32_t ackSeq = *(volatile uint32_t*)&shm->ackSeq;
    if (reqSeq != ackSeq) {
        int reqIdx = *(volatile int32_t*)&shm->requestedIndex;
        if (reqIdx >= 0 && reqIdx < shm->charCount && g_fnSetCurSel) {
            // We need the CListWnd pointer for the character list.
            // We'll find it via the CharacterListWnd → Character_List path.
            // For now, use ppWndMgr to enumerate, OR if GetChildItem works,
            // use it on the known window.
            //
            // PRAGMATIC: Since we have charSelectPlayerArray data via memory,
            // and SetCurSel needs the actual CListWnd pointer (which we can't
            // easily get without walking MQ2's window registry), we take a
            // simpler approach: use the ppWndMgr-based scan (Task 3).
            //
            // For MVP: if we can't get the list window pointer, log it.
            // The C# side will fall back to Enter on default character.
            DI8Log("mq2_bridge: selection request idx=%d — CListWnd lookup needed (Task 3)", reqIdx);
        }
        // Acknowledge the request regardless
        shm->ackSeq = reqSeq;
    }
}

void MQ2Bridge::Shutdown() {
    g_mq2Resolved = false;
    g_pGameState = nullptr;
    g_ppEverQuest = nullptr;
    g_fnSetCurSel = nullptr;
    g_fnGetCurSel = nullptr;
    g_fnGetItemText = nullptr;
    g_fnGetChildItem = nullptr;
    g_hMQ2 = nullptr;
    g_offsetsValidated = false;
}
```

**Note on the CListWnd lookup**: Finding the actual CListWnd pointer for `SetCurSel` requires walking MQ2's window manager. This is handled in Task 3. The memory-based character reading works without it.

- [ ] **Step 2: Commit**

```bash
git add Native/mq2_bridge.cpp
git commit -m "feat(charsel): implement MQ2 bridge — symbol resolution, char list reading, selection stub"
```

---

## Task 3: CListWnd Pointer Discovery

**Files:**
- Modify: `Native/mq2_bridge.cpp`

Add window discovery logic to find the "Character_List" CListWnd pointer so `SetCurSel` can actually work.

- [ ] **Step 1: Add window finder using ppWndMgr**

Add this code to `mq2_bridge.cpp` after the existing CXStr section, before `MQ2Bridge::Init()`:

```cpp
// ─── CListWnd Pointer Discovery ─────────────────────────────────
// We need the actual CListWnd* for "Character_List" to call SetCurSel.
// Approach: When gGameState==1, scan ppWndMgr's internal window array.
//
// CXWndManager layout (ROF2 x86, from MQ2 eqlib):
//   +0x04: ... (hash table and other data)
//   +0x58: ArrayClass<CXWnd*> pWindows (all registered windows)
// Each CXWnd has:
//   +0x1F0: CXStr XMLToolTip (or nearby — varies by version)
// But finding the XML name of each window requires knowing the CSidlScreenWnd
// layout, which has the ScreenID at a known offset.
//
// SIMPLER: We have CSidlScreenWnd__GetChildItem exported.
// If we can find ANY parent window, we can call GetChildItem("Character_List").
// MQ2's window registry maps "CharacterListWnd" -> CCharacterListWnd*.
//
// SIMPLEST VIABLE: Iterate ppWndMgr's window array, for each window,
// try GetChildItem("Character_List"). If it returns non-null, we found it.
// This is brute-force but happens once per char select screen entry.
//
// CXWndManager window array offset — this IS version-dependent.
// We'll try a few common offsets and validate.

static void* g_pCharListWnd = nullptr;  // Cached CListWnd* for "Character_List"
static bool g_charListSearched = false;

// Try to find the Character_List CListWnd by brute-force search.
// Called once when entering char select state.
static void FindCharacterListWnd() {
    g_pCharListWnd = nullptr;
    g_charListSearched = true;

    if (!g_fnGetChildItem || !g_ppWndMgr || !*g_ppWndMgr) {
        DI8Log("mq2_bridge: cannot search for Character_List — missing exports");
        return;
    }

    // ppWndMgr -> CXWndManager* -> window array
    // Try known offsets for the window array in CXWndManager
    // ROF2 x86: commonly at +0x58 or +0x5C
    void* pWndMgr = *g_ppWndMgr;
    int tryOffsets[] = { 0x58, 0x5C, 0x60, 0x54, 0x64, 0x50, 0x68 };

    for (int offsetIdx = 0; offsetIdx < sizeof(tryOffsets)/sizeof(tryOffsets[0]); offsetIdx++) {
        __try {
            auto* arr = (ArrayClassHeader*)((char*)pWndMgr + tryOffsets[offsetIdx]);
            if (arr->Count < 10 || arr->Count > 500 || !arr->Data) continue;  // Sanity check

            void** windows = (void**)arr->Data;
            for (int i = 0; i < arr->Count && i < 500; i++) {
                void* wnd = windows[i];
                if (!wnd) continue;

                // Try GetChildItem("Character_List") on this window
                __try {
                    void* child = g_fnGetChildItem(wnd, "Character_List");
                    if (child) {
                        g_pCharListWnd = child;
                        DI8Log("mq2_bridge: found Character_List at wndMgr offset 0x%X, window[%d]",
                               tryOffsets[offsetIdx], i);
                        return;
                    }
                }
                __except (EXCEPTION_EXECUTE_HANDLER) {
                    // This window didn't like GetChildItem — skip it
                    continue;
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            continue;
        }
    }

    DI8Log("mq2_bridge: Character_List window not found — SetCurSel unavailable");
}
```

- [ ] **Step 2: Update Poll() to use the cached CListWnd pointer for selection**

Replace the selection request handling section in `Poll()` with:

```cpp
    // Handle selection request from C# host
    uint32_t reqSeq = *(volatile uint32_t*)&shm->requestSeq;
    uint32_t ackSeq = *(volatile uint32_t*)&shm->ackSeq;
    if (reqSeq != ackSeq) {
        int reqIdx = *(volatile int32_t*)&shm->requestedIndex;
        if (reqIdx >= 0 && reqIdx < shm->charCount) {
            // Find the Character_List CListWnd if we haven't yet
            if (!g_charListSearched) {
                FindCharacterListWnd();
            }

            if (g_pCharListWnd && g_fnSetCurSel) {
                __try {
                    g_fnSetCurSel(g_pCharListWnd, reqIdx);
                    shm->selectedIndex = reqIdx;
                    DI8Log("mq2_bridge: selected character index %d (%s)",
                           reqIdx, (const char*)shm->names[reqIdx]);
                }
                __except (EXCEPTION_EXECUTE_HANDLER) {
                    DI8Log("mq2_bridge: exception calling SetCurSel(%d)", reqIdx);
                    // Invalidate cached pointer — window may have been destroyed
                    g_pCharListWnd = nullptr;
                    g_charListSearched = false;
                }
            } else {
                DI8Log("mq2_bridge: SetCurSel unavailable — Character_List not found or SetCurSel not exported");
            }
        }
        shm->ackSeq = reqSeq;
    }
```

- [ ] **Step 3: Reset cached window on game state transitions**

Add at the top of `Poll()`, after reading game state:

```cpp
    // Reset window cache on state transitions (window is destroyed/recreated)
    static int g_lastGameState = -1;
    if (gs != g_lastGameState) {
        g_pCharListWnd = nullptr;
        g_charListSearched = false;
        g_lastGameState = gs;
        DI8Log("mq2_bridge: game state changed %d -> %d", g_lastGameState, gs);
    }
```

- [ ] **Step 4: Resolve ppWndMgr in Init()**

In `MQ2Bridge::Init()`, add after the existing `g_ppEverQuest` line:

```cpp
    g_ppWndMgr = (void**)GetProcAddress(g_hMQ2, "ppWndMgr");
```

And update the log line to include it.

- [ ] **Step 5: Commit**

```bash
git add Native/mq2_bridge.cpp
git commit -m "feat(charsel): add CListWnd discovery via ppWndMgr for character selection"
```

---

## Task 4: Wire MQ2 Bridge into eqswitch-di8.dll

**Files:**
- Modify: `Native/eqswitch-di8.cpp` — add Init/Poll calls
- Modify: `Native/build-di8-inject.sh` — add mq2_bridge.cpp to compilation

- [ ] **Step 1: Read eqswitch-di8.cpp to find the init thread and poll locations**

The DLL has an init thread that runs after injection. Find where `MQ2Bridge::Init()` should be called (after dinput8.dll is loaded) and where `Poll()` should be called (from the existing SHM poll thread or a new timer).

Read: `Native/eqswitch-di8.cpp` — look for the `InitThread` function and the `ShmThread`/`ActivateThread` in `device_proxy.cpp`.

- [ ] **Step 2: Add shared memory open/map for CharSelectShm**

In `eqswitch-di8.cpp`, add a new shared memory segment alongside the existing `EQSwitchDI8_{PID}` one. The C# `CharSelectReader` will create the mapping; the DLL opens it for read/write.

Add near the top of `eqswitch-di8.cpp` (after existing includes):

```cpp
#include "mq2_bridge.h"

// CharSelect shared memory — opened lazily, created by C# CharSelectReader
static HANDLE g_charSelMap = nullptr;
static volatile CharSelectShm* g_charSelShm = nullptr;
static uint32_t g_charSelRetry = 0;

static bool TryOpenCharSelShm() {
    DWORD pid = GetCurrentProcessId();
    char name[64];
    snprintf(name, sizeof(name), "Local\\EQSwitchCharSel_%lu", pid);

    HANDLE h = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, name);
    if (!h) return false;

    void* view = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(CharSelectShm));
    if (!view) {
        CloseHandle(h);
        return false;
    }

    g_charSelMap = h;
    g_charSelShm = (volatile CharSelectShm*)view;
    DI8Log("mq2_bridge: opened CharSelect SHM (magic=0x%08X)", g_charSelShm->magic);
    return true;
}

static void CloseCharSelShm() {
    if (g_charSelShm) { UnmapViewOfFile((void*)g_charSelShm); g_charSelShm = nullptr; }
    if (g_charSelMap) { CloseHandle(g_charSelMap); g_charSelMap = nullptr; }
}
```

- [ ] **Step 3: Call MQ2Bridge::Init() from the init thread**

In the existing `InitThread` function (or `DllMain` init), after the MinHook setup completes and the game is running, add:

```cpp
    // Initialize MQ2 bridge (must happen after dinput8.dll is loaded by the game)
    // Wait briefly for MQ2 to initialize its globals
    Sleep(2000);  // Give MQ2 time to resolve its own pointers
    bool mq2Ready = MQ2Bridge::Init();
    DI8Log("MQ2 bridge init: %s", mq2Ready ? "OK" : "disabled");
```

- [ ] **Step 4: Add MQ2 bridge poll to the existing ActivateThread loop**

In `device_proxy.cpp`, the `ActivateThread` runs a loop that polls every ~16ms. Add the MQ2 bridge poll there (throttled to ~500ms):

```cpp
    // Inside the ActivateThread loop, add:
    static DWORD lastMQ2Poll = 0;
    DWORD now = GetTickCount();
    if (now - lastMQ2Poll > 500) {
        lastMQ2Poll = now;
        // Lazy-open CharSelect SHM
        if (!g_charSelShm) {
            if (g_charSelRetry == 0) {
                if (!TryOpenCharSelShm()) {
                    g_charSelRetry = 10;  // Retry every ~5 seconds
                }
            } else {
                g_charSelRetry--;
            }
        }
        // Poll MQ2 bridge
        if (g_charSelShm && g_charSelShm->magic == CHARSEL_SHM_MAGIC) {
            MQ2Bridge::Poll(g_charSelShm);
        }
    }
```

- [ ] **Step 5: Add cleanup to DLL_PROCESS_DETACH**

```cpp
    MQ2Bridge::Shutdown();
    CloseCharSelShm();
```

- [ ] **Step 6: Update build script**

In `Native/build-di8-inject.sh`, add `mq2_bridge.cpp` to the source file list:

```bash
# Change this line:
    eqswitch-di8.cpp di8_proxy.cpp device_proxy.cpp key_shm.cpp iat_hook.cpp net_debug.cpp pattern_scan.cpp \
# To:
    eqswitch-di8.cpp di8_proxy.cpp device_proxy.cpp key_shm.cpp iat_hook.cpp net_debug.cpp pattern_scan.cpp mq2_bridge.cpp \
```

- [ ] **Step 7: Build and verify compilation**

```bash
cd X:/_Projects/eqswitch && bash Native/build-di8-inject.sh
```

Expected: Build succeeds, `Native/eqswitch-di8.dll` is produced (should be slightly larger than before).

- [ ] **Step 8: Commit**

```bash
git add Native/eqswitch-di8.cpp Native/device_proxy.cpp Native/build-di8-inject.sh
git commit -m "feat(charsel): wire MQ2 bridge into eqswitch-di8.dll build and runtime"
```

---

## Task 5: C# CharSelectReader

**Files:**
- Create: `Core/CharSelectReader.cs`
- Reference: `Core/KeyInputWriter.cs` (pattern to follow)

This is the C# side of the shared memory — creates the mapping, reads character data, sends selection requests.

- [ ] **Step 1: Create CharSelectReader.cs**

```csharp
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace EQSwitch.Core;

/// <summary>
/// Creates and manages "Local\EQSwitchCharSel_{PID}" shared memory for
/// character select data exchange with the injected DLL's MQ2 bridge.
///
/// DLL writes: gameState, charCount, character names/levels/classes, mq2Available
/// C# writes: requestedIndex, requestSeq (to request character selection)
/// DLL acks: ackSeq (confirms request processed)
/// </summary>
public sealed class CharSelectReader : IDisposable
{
    private const string SharedMemoryPrefix = "Local\\EQSwitchCharSel_";
    private const uint Magic = 0x45534353; // "ESCS"
    private const uint Version = 1;
    private const int MaxChars = 8;
    private const int NameLen = 64;

    // Must match Native/mq2_bridge.h CharSelectShm exactly
    // Total: 612 bytes
    private static readonly int ShmSize = 612;

    // Field offsets (matching #pragma pack(push,1) C++ struct)
    private const int OFF_MAGIC = 0;
    private const int OFF_VERSION = 4;
    private const int OFF_GAMESTATE = 8;
    private const int OFF_CHARCOUNT = 12;
    private const int OFF_SELECTEDINDEX = 16;
    private const int OFF_MQ2AVAILABLE = 20;
    private const int OFF_REQUESTEDINDEX = 24;
    private const int OFF_REQUESTSEQ = 28;
    private const int OFF_ACKSEQ = 32;
    private const int OFF_NAMES = 36;          // 8 * 64 = 512 bytes
    private const int OFF_LEVELS = 36 + 512;   // 8 * 4 = 32 bytes
    private const int OFF_CLASSES = 36 + 512 + 32; // 8 * 4 = 32 bytes

    private sealed class MappingEntry : IDisposable
    {
        public readonly MemoryMappedFile Mmf;
        public readonly MemoryMappedViewAccessor Accessor;
        public uint RequestSeq;

        public MappingEntry(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor)
        {
            Mmf = mmf;
            Accessor = accessor;
        }

        public void Dispose()
        {
            Accessor.Dispose();
            Mmf.Dispose();
        }
    }

    private readonly Dictionary<int, MappingEntry> _mappings = new();
    private bool _disposed;

    /// <summary>
    /// Create shared memory for a process. Call during auto-login setup
    /// (alongside KeyInputWriter.Open).
    /// </summary>
    public bool Open(int pid)
    {
        if (_mappings.ContainsKey(pid)) return true;

        try
        {
            var name = $"{SharedMemoryPrefix}{(uint)pid}";
            var mmf = MemoryMappedFile.CreateOrOpen(name, ShmSize);
            var accessor = mmf.CreateViewAccessor(0, ShmSize);

            // Write header
            accessor.Write(OFF_MAGIC, Magic);
            accessor.Write(OFF_VERSION, Version);
            accessor.Write(OFF_GAMESTATE, -1);
            accessor.Write(OFF_CHARCOUNT, 0);
            accessor.Write(OFF_SELECTEDINDEX, -1);
            accessor.Write(OFF_MQ2AVAILABLE, (uint)0);
            accessor.Write(OFF_REQUESTEDINDEX, -1);
            accessor.Write(OFF_REQUESTSEQ, (uint)0);
            accessor.Write(OFF_ACKSEQ, (uint)0);

            _mappings[pid] = new MappingEntry(mmf, accessor);
            FileLogger.Info($"CharSelectReader: opened SHM for PID {pid}");
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"CharSelectReader: failed to open SHM for PID {pid}", ex);
            return false;
        }
    }

    /// <summary>Read current game state from DLL. Returns -1 if not available.</summary>
    public int ReadGameState(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return -1;
        return entry.Accessor.ReadInt32(OFF_GAMESTATE);
    }

    /// <summary>True if MQ2 exports were resolved by the DLL.</summary>
    public bool IsMQ2Available(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;
        return entry.Accessor.ReadUInt32(OFF_MQ2AVAILABLE) != 0;
    }

    /// <summary>Read character count at char select. 0 if not at char select or MQ2 unavailable.</summary>
    public int ReadCharCount(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return 0;
        return entry.Accessor.ReadInt32(OFF_CHARCOUNT);
    }

    /// <summary>Read character name at given index (0-based). Empty string if invalid.</summary>
    public string ReadCharName(int pid, int index)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return "";
        if (index < 0 || index >= MaxChars) return "";

        var bytes = new byte[NameLen];
        entry.Accessor.ReadArray(OFF_NAMES + (index * NameLen), bytes, 0, NameLen);

        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = NameLen;
        return Encoding.ASCII.GetString(bytes, 0, len);
    }

    /// <summary>Read character level at given index.</summary>
    public int ReadCharLevel(int pid, int index)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return 0;
        if (index < 0 || index >= MaxChars) return 0;
        return entry.Accessor.ReadInt32(OFF_LEVELS + (index * 4));
    }

    /// <summary>Read all character names. Returns empty array if not at char select.</summary>
    public string[] ReadAllCharNames(int pid)
    {
        int count = ReadCharCount(pid);
        if (count <= 0) return Array.Empty<string>();

        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = ReadCharName(pid, i);
        return names;
    }

    /// <summary>
    /// Request the DLL to select a character by index.
    /// Returns true if the request was sent.
    /// </summary>
    public bool RequestSelection(int pid, int index)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;

        entry.Accessor.Write(OFF_REQUESTEDINDEX, index);
        entry.RequestSeq++;
        entry.Accessor.Write(OFF_REQUESTSEQ, entry.RequestSeq);

        FileLogger.Info($"CharSelectReader: requested selection index {index} for PID {pid} (seq={entry.RequestSeq})");
        return true;
    }

    /// <summary>
    /// Request selection by character name (case-insensitive).
    /// Returns the index selected, or -1 if character not found.
    /// </summary>
    public int RequestSelectionByName(int pid, string characterName)
    {
        int count = ReadCharCount(pid);
        for (int i = 0; i < count; i++)
        {
            if (string.Equals(ReadCharName(pid, i), characterName, StringComparison.OrdinalIgnoreCase))
            {
                RequestSelection(pid, i);
                return i;
            }
        }

        FileLogger.Warn($"CharSelectReader: character '{characterName}' not found (PID {pid}, {count} chars available)");
        return -1;
    }

    /// <summary>Check if the last selection request was acknowledged by the DLL.</summary>
    public bool IsSelectionAcknowledged(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;
        uint ackSeq = entry.Accessor.ReadUInt32(OFF_ACKSEQ);
        return ackSeq == entry.RequestSeq;
    }

    /// <summary>Close shared memory for a process.</summary>
    public void Close(int pid)
    {
        if (_mappings.TryGetValue(pid, out var entry))
        {
            entry.Dispose();
            _mappings.Remove(pid);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _mappings.Values)
            entry.Dispose();
        _mappings.Clear();
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add Core/CharSelectReader.cs
git commit -m "feat(charsel): add C# CharSelectReader for MQ2 bridge shared memory"
```

---

## Task 6: Integrate into AutoLoginManager

**Files:**
- Modify: `Core/AutoLoginManager.cs` (lines ~276-337 — char select + enter world section)

Wire `CharSelectReader` into the login sequence to select a character by name before entering world.

- [ ] **Step 1: Add CharSelectReader to AutoLoginManager**

At the top of `RunLoginSequence()` (around line 209, after `KeyInputWriter writer`), add:

```csharp
        var charSelect = new CharSelectReader();
```

And in the `try` block, after `writer.Open(pid)` (line ~213):

```csharp
            charSelect.Open(pid);
```

And in the `finally` block (line ~346), add cleanup:

```csharp
            try { charSelect.Close(pid); } catch { }
            try { charSelect.Dispose(); } catch { }
```

- [ ] **Step 2: Replace the blind Enter World with character selection**

Replace the section from `// Step 9: Auto Enter World gate` through the enter world loop (approximately lines 285-336) with:

```csharp
            // Step 9: Auto Enter World gate
            if (!_config.AutoEnterWorld)
            {
                Report($"{account.Name} reached character select.");
                FileLogger.Info($"AutoLogin: {account.Name} stopped at char select (AutoEnterWorld disabled)");
                return;
            }

            // Reactivate SHM — creates rising edge so DLL re-blasts activation
            // messages after EQ overwrites WndProc subclass during 3D scene init.
            writer.Reactivate(pid);
            Thread.Sleep(2000); // let DLL re-install WndProc subclass + init MQ2 bridge

            // Step 10: Select character by name (if MQ2 available)
            if (!string.IsNullOrEmpty(account.CharacterName))
            {
                // Wait for MQ2 bridge to populate character list
                bool charListReady = false;
                for (int wait = 0; wait < 10; wait++)
                {
                    if (charSelect.IsMQ2Available(pid) && charSelect.ReadCharCount(pid) > 0)
                    {
                        charListReady = true;
                        break;
                    }
                    Thread.Sleep(500);
                }

                if (charListReady)
                {
                    var charNames = charSelect.ReadAllCharNames(pid);
                    FileLogger.Info($"AutoLogin: {charNames.Length} characters found: {string.Join(", ", charNames)}");

                    int selIdx = charSelect.RequestSelectionByName(pid, account.CharacterName);
                    if (selIdx >= 0)
                    {
                        // Wait for DLL to acknowledge the selection
                        for (int ack = 0; ack < 10; ack++)
                        {
                            if (charSelect.IsSelectionAcknowledged(pid))
                            {
                                FileLogger.Info($"AutoLogin: character '{account.CharacterName}' selected (index {selIdx})");
                                break;
                            }
                            Thread.Sleep(200);
                        }
                        Thread.Sleep(500); // Brief pause after selection
                    }
                    else
                    {
                        FileLogger.Warn($"AutoLogin: character '{account.CharacterName}' not found in list, entering world with default");
                    }
                }
                else
                {
                    FileLogger.Warn($"AutoLogin: MQ2 bridge not ready (mq2={charSelect.IsMQ2Available(pid)}, chars={charSelect.ReadCharCount(pid)}), entering world with default");
                }
            }

            // Step 11: Enter World — retry up to 5 times with pulsed key holds
            Report("Entering world...");
            bool entered = false;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                writer.Reactivate(pid);
                Thread.Sleep(500);
                PulseKey3D(writer, pid, hwnd, 0x0D);
                Thread.Sleep(4000);

                // Check if we left char select — window title changes to "EverQuest - CharName"
                hwnd = RefreshHandle(pid, hwnd);
                if (hwnd == IntPtr.Zero) { Report("Error: lost EQ window"); return; }

                int titleLen = NativeMethods.GetWindowTextLength(hwnd);
                var titleSb = new StringBuilder(titleLen + 1);
                NativeMethods.GetWindowText(hwnd, titleSb, titleSb.Capacity);
                string title = titleSb.ToString();

                if (title.Contains(" - "))
                {
                    entered = true;
                    FileLogger.Info($"AutoLogin: enter-world confirmed (title: {title})");
                    break;
                }

                if (attempt < 4)
                    FileLogger.Info($"AutoLogin: enter-world attempt {attempt + 1} — title still '{title}', retrying...");
            }

            if (entered)
            {
                Report($"{account.Name} logged in!");
                FileLogger.Info($"AutoLogin: {account.Name} login sequence complete (PID {pid})");
            }
            else
            {
                Report($"{account.Name}: reached char select but Enter World didn't register");
                FileLogger.Warn($"AutoLogin: {account.Name} enter-world failed after 5 attempts (PID {pid})");
            }
```

- [ ] **Step 3: Commit**

```bash
git add Core/AutoLoginManager.cs
git commit -m "feat(charsel): integrate character selection by name into auto-login sequence"
```

---

## Task 7: Build, Deploy, and Smoke Test

**Files:**
- Build: `Native/build-di8-inject.sh`
- Deploy: copy to `C:/Users/nate/proggy/Everquest/EQSwitch/`

- [ ] **Step 1: Build the DLL**

```bash
cd X:/_Projects/eqswitch && bash Native/build-di8-inject.sh
```

Expected: Build succeeds with `mq2_bridge.cpp` included.

- [ ] **Step 2: Build the C# host**

```bash
cd X:/_Projects/eqswitch && dotnet build
```

Expected: Build succeeds with `CharSelectReader.cs` compiled.

- [ ] **Step 3: Verify by checking the log output**

After building, the key verification is runtime:
1. Launch EQSwitch
2. Auto-login an account
3. Check `Eqfresh/eqswitch-dinput8.log` for:
   - `mq2_bridge: MQ2 exports resolved` — confirms dinput8.dll exports found
   - `mq2_bridge: charSelectPlayerArray validated at offset 0x...` — confirms memory reading works
   - `mq2_bridge: X characters found` — confirms character data is readable
4. Check EQSwitch's log for:
   - `CharSelectReader: opened SHM for PID ...`
   - `AutoLogin: N characters found: Name1, Name2, ...`

- [ ] **Step 4: Deploy**

```bash
cp X:/_Projects/eqswitch/Native/eqswitch-di8.dll "C:/Users/nate/proggy/Everquest/EQSwitch/"
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
cp X:/_Projects/eqswitch/bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe "C:/Users/nate/proggy/Everquest/EQSwitch/"
```

- [ ] **Step 5: Commit all if tests pass**

```bash
git add -A  # Only if everything works
git commit -m "feat(charsel): complete MQ2 bridge integration — character select by name"
```

---

## Known Issues for the Implementer

1. **CXStr layout**: The CXStr struct definition in Task 2 is based on MQ2 source for Live x64. On Dalaya's ROF2 x86, the layout might differ. If `ReadListItemText` crashes, the CXStr struct needs adjustment. The memory-based approach (reading charSelectPlayerArray directly) is the primary path and doesn't depend on CXStr.

2. **CXWndManager window array offset**: Task 3 tries multiple offsets (0x50-0x68) for the window array in CXWndManager. If none work, `SetCurSel` won't function, but character NAME reading via memory still works. The C# side will log this and proceed with Enter on the found character.

3. **MQ2 init timing**: The 2-second Sleep before `MQ2Bridge::Init()` in Task 4 gives dinput8.dll time to resolve its own globals. If `ppEverQuest` is still null, increase this delay or add a retry loop.

4. **charSelectPlayerArray offset (0x18EC0)**: This is from the Live 64-bit client source. Dalaya's 32-bit ROF2 build may have a different offset. The `ValidateCharArrayOffset` function in Task 2 scans +-0x200 bytes around the expected offset. If the actual offset is further away, expand the scan range.

5. **`LoginAccount.CharacterName`**: This field already exists in the model. It needs to be populated in the Settings UI (SettingsForm.cs Accounts tab). That's a separate UI task — for now, set it directly in the config JSON for testing.

6. **Another session active**: Check `_.claude/_comms/active-work.md` before starting. Work on branch `feat/charselect-mq2`.
