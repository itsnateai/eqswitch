// mq2_bridge.cpp — MQ2 bridge for character select via Dalaya's dinput8.dll exports
//
// Resolves MQ2 symbols exported by Dalaya's custom dinput8.dll (2,966 exports),
// reads the character list from CEverQuest::charSelectPlayerArray, and handles
// selection requests from C# via the CharSelectShm shared memory.
//
// All memory access is wrapped in SEH (__try/__except) because the struct offsets
// are from MQ2's Live 64-bit build and may differ on Dalaya's ROF2 x86.
// ValidateCharArrayOffset scans +-0x200 bytes if the expected offset fails.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include "mq2_bridge.h"

// ─── Forward declarations ──────────────────────────────────────

void DI8Log(const char *fmt, ...);

// ─── MQ2 Export Types ──────────────────────────────────────────

// __thiscall on x86: 'this' in ECX, args on stack.
// MSVC uses __thiscall natively; we declare as __thiscall member-call pointers.

// void* CListWnd::GetItemText(CXStr* out, int row, int col)
typedef void *(__thiscall *FN_GetItemText)(void *thisPtr, void *outCXStr, int row, int col);

// void CListWnd::SetCurSel(int index)
typedef void (__thiscall *FN_SetCurSel)(void *thisPtr, int index);

// int CListWnd::GetCurSel()
typedef int (__thiscall *FN_GetCurSel)(void *thisPtr);

// CXWnd* CSidlScreenWnd::GetChildItem(char* name)
typedef void *(__thiscall *FN_GetChildItem)(void *thisPtr, const char *name);

// ─── Static globals ────────────────────────────────────────────

static HMODULE        g_hMQ2            = nullptr;  // Dalaya's dinput8.dll handle
static volatile int  *g_pGameState      = nullptr;   // gGameState export
static void         **g_ppEverQuest     = nullptr;   // ppEverQuest export
static void         **g_ppWndMgr        = nullptr;   // ppWndMgr export
static FN_GetItemText g_fnGetItemText   = nullptr;
static FN_SetCurSel   g_fnSetCurSel     = nullptr;
static FN_GetCurSel   g_fnGetCurSel     = nullptr;
static FN_GetChildItem g_fnGetChildItem = nullptr;

// ─── CXStr struct ──────────────────────────────────────────────
// MQ2's CXStr is a ref-counted string. GetItemText returns a pointer to one.
// We only need to read the Ptr field, then copy the C-string out.

struct CXStr {
    char    *Ptr;
    int      Length;
    int      Alloc;
    int      RefCount;
};

// ─── CEverQuest offset constants ───────────────────────────────
// From MQ2 source (Live 64-bit). May differ on Dalaya's ROF2 x86 build.
// ValidateCharArrayOffset will scan +-0x200 if these are wrong.

static const uint32_t OFFSET_CHARSELECT_ARRAY = 0x18EC0;  // charSelectPlayerArray in CEverQuest
static const uint32_t CSI_SIZE       = 0x170;  // CharSelectInfo struct size
static const uint32_t CSI_NAME_OFF   = 0x00;   // char Name[64] at start
static const uint32_t CSI_CLASS_OFF  = 0x40;   // int Class
static const uint32_t CSI_LEVEL_OFF  = 0x48;   // byte/int Level

// ─── Offset validation state ───────────────────────────────────

static bool     g_offsetValidated   = false;
static uint32_t g_validatedOffset   = 0;  // Actual working offset (may differ from constant)

// ─── ReadListItemText helper ───────────────────────────────────
// Calls g_fnGetItemText with a stack-allocated CXStr, copies result to buffer.
// Returns true if text was read successfully.

static bool ReadListItemText(void *listWnd, int row, int col, char *outBuf, int bufSize) {
    if (!g_fnGetItemText || !listWnd || bufSize <= 0) return false;

    outBuf[0] = '\0';

    __try {
        CXStr str = {};
        g_fnGetItemText(listWnd, &str, row, col);

        if (str.Ptr && str.Length > 0) {
            int copyLen = (str.Length < bufSize - 1) ? str.Length : (bufSize - 1);
            memcpy(outBuf, str.Ptr, copyLen);
            outBuf[copyLen] = '\0';
            return true;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH in ReadListItemText(row=%d, col=%d)", row, col);
    }

    return false;
}

// ─── ArrayClass header ────────────────────────────────────────
// MQ2's ArrayClass<T> has: T* Data; int Count; int Alloc;
// We only need Data and Count.

struct ArrayClassHeader {
    uint8_t *Data;    // Pointer to array of CharSelectInfo structs
    int      Count;   // Number of entries
    int      Alloc;   // Allocated capacity (unused by us)
};

// ─── ValidateCharArrayOffset ───────────────────────────────────
// SEH-wrapped validation: checks that the offset points to a plausible
// charSelectPlayerArray. Criteria:
//   - Count is 1-8 (EQ supports max 8 chars per server)
//   - Data pointer is non-null and readable
//   - First character name is printable ASCII with null termination
//
// If the expected offset fails, scans +-0x200 bytes in 4-byte steps.

static bool IsValidCharArray(const uint8_t *pEverQuest, uint32_t offset) {
    __try {
        const ArrayClassHeader *arr = (const ArrayClassHeader *)(pEverQuest + offset);

        // Count must be 1-8
        if (arr->Count < 1 || arr->Count > CHARSEL_MAX_CHARS)
            return false;

        // Data must be non-null
        if (!arr->Data)
            return false;

        // First name must be printable ASCII with null termination within 64 bytes
        const char *name = (const char *)(arr->Data + CSI_NAME_OFF);

        // Check first char is printable letter (names start with A-Z)
        if (name[0] < 0x20 || name[0] > 0x7E)
            return false;

        // Find null terminator within 64 bytes
        bool foundNull = false;
        for (int i = 0; i < 64; i++) {
            if (name[i] == '\0') { foundNull = true; break; }
            // All chars must be printable ASCII
            if (name[i] < 0x20 || name[i] > 0x7E)
                return false;
        }
        if (!foundNull) return false;

        // Name must be at least 2 chars (EQ minimum)
        if (name[1] == '\0') return false;

        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool ValidateCharArrayOffset(const uint8_t *pEverQuest) {
    if (g_offsetValidated) return true;

    // Try the expected offset first
    if (IsValidCharArray(pEverQuest, OFFSET_CHARSELECT_ARRAY)) {
        g_validatedOffset = OFFSET_CHARSELECT_ARRAY;
        g_offsetValidated = true;
        DI8Log("mq2_bridge: charSelectPlayerArray validated at expected offset 0x%X", g_validatedOffset);
        return true;
    }

    DI8Log("mq2_bridge: expected offset 0x%X failed — scanning +-0x200", OFFSET_CHARSELECT_ARRAY);

    // Scan +-0x200 bytes in 4-byte aligned steps
    const uint32_t scanRange = 0x200;
    uint32_t baseOffset = OFFSET_CHARSELECT_ARRAY;

    // Avoid underflow
    uint32_t startOffset = (baseOffset > scanRange) ? (baseOffset - scanRange) : 0;
    uint32_t endOffset = baseOffset + scanRange;

    for (uint32_t off = startOffset; off <= endOffset; off += 4) {
        if (off == baseOffset) continue;  // Already tried

        if (IsValidCharArray(pEverQuest, off)) {
            g_validatedOffset = off;
            g_offsetValidated = true;
            DI8Log("mq2_bridge: charSelectPlayerArray FOUND at scanned offset 0x%X (delta=%+d)",
                   off, (int)off - (int)baseOffset);
            return true;
        }
    }

    DI8Log("mq2_bridge: charSelectPlayerArray NOT FOUND in scan range — offsets may be wrong for this build");
    return false;
}

// ─── CListWnd cache ───────────────────────────────────────────
// Cached pointer to the "Character_List" CListWnd in the char select screen.
// Discovered once per char-select entry via FindCharacterListWnd().

static void* g_pCharListWnd = nullptr;  // Cached CListWnd* for "Character_List"
static bool g_charListSearched = false;

// ─── FindCharacterListWnd ─────────────────────────────────────
// Brute-force search through CXWndManager's window array to find the
// "Character_List" CListWnd. The window array offset in CXWndManager is
// version-dependent, so we try several common ROF2 x86 candidates.

static void FindCharacterListWnd() {
    g_charListSearched = true;
    g_pCharListWnd = nullptr;

    if (!g_fnGetChildItem || !g_ppWndMgr) {
        DI8Log("mq2_bridge: FindCharacterListWnd — missing GetChildItem or ppWndMgr");
        return;
    }

    void *pWndMgr = nullptr;
    __try {
        pWndMgr = *g_ppWndMgr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH reading ppWndMgr");
        return;
    }

    if (!pWndMgr) {
        DI8Log("mq2_bridge: FindCharacterListWnd — CXWndManager is null");
        return;
    }

    // Common ROF2 x86 offsets for the ArrayClass<CXWnd*> inside CXWndManager
    static const uint32_t candidateOffsets[] = { 0x58, 0x5C, 0x60, 0x54, 0x64, 0x50, 0x68 };
    const int numCandidates = sizeof(candidateOffsets) / sizeof(candidateOffsets[0]);

    const uint8_t *pMgr = (const uint8_t *)pWndMgr;

    for (int c = 0; c < numCandidates; c++) {
        uint32_t off = candidateOffsets[c];

        __try {
            // Read ArrayClass header at this offset
            const ArrayClassHeader *arr = (const ArrayClassHeader *)(pMgr + off);

            // Sanity check: total EQ windows should be 10-500
            if (arr->Count < 10 || arr->Count > 500)
                continue;
            if (!arr->Data)
                continue;

            // Iterate all windows in this array
            void **wndArray = (void **)arr->Data;

            for (int i = 0; i < arr->Count; i++) {
                void *wnd = wndArray[i];
                if (!wnd) continue;

                __try {
                    void *child = g_fnGetChildItem(wnd, "Character_List");
                    if (child) {
                        g_pCharListWnd = child;
                        DI8Log("mq2_bridge: FindCharacterListWnd — FOUND at offset 0x%X, window index %d",
                               off, i);
                        return;
                    }
                }
                __except (EXCEPTION_EXECUTE_HANDLER) {
                    // Bad window pointer — skip silently
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            // Bad offset — try next candidate
        }
    }

    DI8Log("mq2_bridge: FindCharacterListWnd — Character_List NOT FOUND (tried %d offsets)",
           numCandidates);
}

// ─── MQ2Bridge::Init ───────────────────────────────────────────

bool MQ2Bridge::Init() {
    DI8Log("mq2_bridge: Init — resolving MQ2 exports from dinput8.dll");

    g_hMQ2 = GetModuleHandleA("dinput8.dll");
    if (!g_hMQ2) {
        DI8Log("mq2_bridge: dinput8.dll not loaded — MQ2 bridge unavailable");
        return false;
    }
    DI8Log("mq2_bridge: dinput8.dll at 0x%p", g_hMQ2);

    // Resolve simple data exports
    g_pGameState = (volatile int *)GetProcAddress(g_hMQ2, "gGameState");
    g_ppEverQuest = (void **)GetProcAddress(g_hMQ2, "ppEverQuest");
    g_ppWndMgr = (void **)GetProcAddress(g_hMQ2, "ppWndMgr");

    DI8Log("mq2_bridge: gGameState=%p  ppEverQuest=%p  ppWndMgr=%p",
           g_pGameState, g_ppEverQuest, g_ppWndMgr);

    // Resolve mangled C++ exports (__thiscall methods)
    g_fnSetCurSel = (FN_SetCurSel)GetProcAddress(g_hMQ2,
        "?SetCurSel@CListWnd@EQClasses@@QAEXH@Z");
    g_fnGetCurSel = (FN_GetCurSel)GetProcAddress(g_hMQ2,
        "?GetCurSel@CListWnd@EQClasses@@QBEHXZ");
    g_fnGetItemText = (FN_GetItemText)GetProcAddress(g_hMQ2,
        "?GetItemText@CListWnd@EQClasses@@QBEPAVCXStr@2@PAV32@HH@Z");
    g_fnGetChildItem = (FN_GetChildItem)GetProcAddress(g_hMQ2,
        "?GetChildItem@CSidlScreenWnd@EQClasses@@QAEPAVCXWnd@2@PAD@Z");

    DI8Log("mq2_bridge: SetCurSel=%p  GetCurSel=%p  GetItemText=%p  GetChildItem=%p",
           g_fnSetCurSel, g_fnGetCurSel, g_fnGetItemText, g_fnGetChildItem);

    // Core requirement: we need at least gGameState and ppEverQuest to read chars
    bool ok = (g_pGameState != nullptr && g_ppEverQuest != nullptr);
    if (ok) {
        DI8Log("mq2_bridge: Init SUCCESS — core exports resolved");
    } else {
        DI8Log("mq2_bridge: Init PARTIAL — missing core exports, char list reading unavailable");
    }

    return ok;
}

// ─── MQ2Bridge::Poll ───────────────────────────────────────────

void MQ2Bridge::Poll(volatile CharSelectShm *shm) {
    if (!shm) return;

    // No MQ2 exports? Nothing to do.
    if (!g_pGameState || !g_ppEverQuest) {
        shm->mq2Available = 0;
        return;
    }

    shm->mq2Available = 1;

    // Read game state
    int gameState = -1;
    __try {
        gameState = *g_pGameState;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH reading gGameState");
        shm->gameState = -1;
        shm->charCount = 0;
        shm->selectedIndex = -1;
        return;
    }

    shm->gameState = gameState;

    // Reset cached window pointers on game state transitions
    static int lastGameState = -1;
    if (gameState != lastGameState) {
        if (lastGameState != -1) {
            DI8Log("mq2_bridge: game state %d → %d — clearing window cache", lastGameState, gameState);
        }
        g_pCharListWnd = nullptr;
        g_charListSearched = false;
        lastGameState = gameState;
    }

    // Not at character select? Clear char data and return.
    if (gameState != 1) {
        shm->charCount = 0;
        shm->selectedIndex = -1;
        // Reset validation so we re-validate on next char select entry
        g_offsetValidated = false;
        return;
    }

    // ── At character select (gameState == 1) ──

    // Get CEverQuest pointer
    void *pEverQuest = nullptr;
    __try {
        pEverQuest = *g_ppEverQuest;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH reading ppEverQuest");
        shm->charCount = 0;
        shm->selectedIndex = -1;
        return;
    }

    if (!pEverQuest) {
        shm->charCount = 0;
        shm->selectedIndex = -1;
        return;
    }

    const uint8_t *pEQ = (const uint8_t *)pEverQuest;

    // Validate charSelectPlayerArray offset (first time at char select only)
    if (!ValidateCharArrayOffset(pEQ)) {
        shm->charCount = 0;
        shm->selectedIndex = -1;
        return;
    }

    // Read ArrayClass header
    __try {
        const ArrayClassHeader *arr = (const ArrayClassHeader *)(pEQ + g_validatedOffset);
        int count = arr->Count;
        const uint8_t *data = arr->Data;

        if (count < 0 || count > CHARSEL_MAX_CHARS || !data) {
            shm->charCount = 0;
            shm->selectedIndex = -1;
            return;
        }

        // Read each CharSelectInfo entry
        for (int i = 0; i < count; i++) {
            const uint8_t *entry = data + (i * CSI_SIZE);

            // Name: copy with safe null-termination
            const char *name = (const char *)(entry + CSI_NAME_OFF);
            int nameLen = 0;
            while (nameLen < CHARSEL_NAME_LEN - 1 && name[nameLen] != '\0' &&
                   name[nameLen] >= 0x20 && name[nameLen] <= 0x7E) {
                nameLen++;
            }
            memcpy((void *)shm->names[i], name, nameLen);
            ((char *)shm->names[i])[nameLen] = '\0';

            // Level: byte at CSI_LEVEL_OFF (MQ2 uses int but value fits in byte)
            shm->levels[i] = *(const int32_t *)(entry + CSI_LEVEL_OFF);

            // Class: int at CSI_CLASS_OFF
            shm->classes[i] = *(const int32_t *)(entry + CSI_CLASS_OFF);
        }

        shm->charCount = count;

        // Clear unused slots
        for (int i = count; i < CHARSEL_MAX_CHARS; i++) {
            ((char *)shm->names[i])[0] = '\0';
            shm->levels[i] = 0;
            shm->classes[i] = 0;
        }

    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH reading charSelectPlayerArray");
        shm->charCount = 0;
        shm->selectedIndex = -1;
        return;
    }

    // ── Handle selection request ──
    // C# sets requestedIndex and increments requestSeq.
    // We process when requestSeq != ackSeq.

    uint32_t reqSeq = shm->requestSeq;
    uint32_t ackSeq = shm->ackSeq;

    if (reqSeq != ackSeq) {
        int requestedIdx = shm->requestedIndex;
        DI8Log("mq2_bridge: selection request — index=%d (seq %u→%u)",
               requestedIdx, ackSeq, reqSeq);

        if (requestedIdx >= 0 && requestedIdx < shm->charCount) {
            // Find the Character_List CListWnd if we haven't yet
            if (!g_charListSearched) {
                FindCharacterListWnd();
            }

            if (g_pCharListWnd && g_fnSetCurSel) {
                __try {
                    g_fnSetCurSel(g_pCharListWnd, requestedIdx);
                    shm->selectedIndex = requestedIdx;
                    DI8Log("mq2_bridge: selected character index %d (\"%s\")",
                           requestedIdx, (const char *)shm->names[requestedIdx]);
                }
                __except (EXCEPTION_EXECUTE_HANDLER) {
                    DI8Log("mq2_bridge: SEH in SetCurSel(%d) — invalidating cached pointer", requestedIdx);
                    g_pCharListWnd = nullptr;
                    g_charListSearched = false;
                }
            } else {
                DI8Log("mq2_bridge: SetCurSel unavailable — Character_List not found or SetCurSel not exported");
            }
        } else {
            DI8Log("mq2_bridge: selection request index %d out of range (charCount=%d)",
                   requestedIdx, (int)shm->charCount);
        }

        // Acknowledge the request (even if out of range — prevents re-processing)
        shm->ackSeq = reqSeq;
    }
}

// ─── MQ2Bridge::Shutdown ───────────────────────────────────────

void MQ2Bridge::Shutdown() {
    DI8Log("mq2_bridge: Shutdown — nullifying pointers");

    g_pGameState     = nullptr;
    g_ppEverQuest    = nullptr;
    g_ppWndMgr       = nullptr;
    g_fnGetItemText  = nullptr;
    g_fnSetCurSel    = nullptr;
    g_fnGetCurSel    = nullptr;
    g_fnGetChildItem = nullptr;
    g_hMQ2           = nullptr;

    g_offsetValidated = false;
    g_validatedOffset = 0;

    g_pCharListWnd = nullptr;
    g_charListSearched = false;
}
