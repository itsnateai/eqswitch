// mq2_bridge.cpp -- MQ2 bridge for character select + in-process login
//
// Resolves MQ2 symbols exported by Dalaya's dinput8.dll (2,966 exports),
// reads the character list, handles character selection, and provides
// in-process UI manipulation for login (SetWindowText, WndNotification).
//
// Two-tier resolution: Dalaya exports first, pattern scan fallback.
// All memory access is wrapped in SEH (__try/__except).

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include "mq2_bridge.h"
#include "login_shm.h"

// ─── Forward declarations ──────────────────────────────────────

void DI8Log(const char *fmt, ...);

// ─── MQ2 Export Types ──────────────────────────────────────────

// __thiscall on x86: 'this' in ECX, args on stack.

// void* CListWnd::GetItemText(CXStr* out, int row, int col)
typedef void *(__thiscall *FN_GetItemText)(void *thisPtr, void *outCXStr, int row, int col);

// void CListWnd::SetCurSel(int index)
typedef void (__thiscall *FN_SetCurSel)(void *thisPtr, int index);

// int CListWnd::GetCurSel()
typedef int (__thiscall *FN_GetCurSel)(void *thisPtr);

// CXWnd* CSidlScreenWnd::GetChildItem(char* name)
typedef void *(__thiscall *FN_GetChildItem)(void *thisPtr, const char *name);

// void CXWnd::SetWindowTextA(CXStr& text)
// Dalaya export: ?SetWindowTextA@CXWnd@EQClasses@@QAEXAAVCXStr@2@@Z
typedef void (__thiscall *FN_SetWindowText)(void *thisPtr, void *pCXStr);

// CXStr CXWnd::GetWindowTextA() — returns CXStr by value (hidden sret pointer)
// Dalaya export: ?GetWindowTextA@CXWnd@EQClasses@@QBE?AVCXStr@2@XZ
typedef void *(__thiscall *FN_GetWindowText)(void *thisPtr, void *outCXStr);

// int CXWnd::WndNotification(CXWnd* sender, uint32_t msg, void* data)
// Dalaya export: ?WndNotification@CXWnd@EQClasses@@QAEHPAV12@IPAX@Z
typedef int (__thiscall *FN_WndNotification)(void *thisPtr, void *sender, uint32_t msg, void *data);

// CXStr::CXStr(const char*) — constructor
// Dalaya export: ??0CXStr@EQClasses@@QAE@PBD@Z
typedef void (__thiscall *FN_CXStrCtor)(void *thisPtr, const char *text);

// CXStr::~CXStr() — destructor
// Dalaya export: ??1CXStr@EQClasses@@QAE@XZ
typedef void (__thiscall *FN_CXStrDtor)(void *thisPtr);

// ─── Static globals ────────────────────────────────────────────

static HMODULE        g_hMQ2            = nullptr;
static volatile int  *g_pGameState      = nullptr;
static void         **g_ppEverQuest     = nullptr;
static void         **g_ppWndMgr        = nullptr;    // ppWndMgr: double-ptr (deref once)
static uintptr_t     *g_pinstWndMgr     = nullptr;    // pinstCXWndManager: uintptr_t (value IS the ptr)
static uintptr_t     *g_pinstEQMainWnd  = nullptr;    // pinstCEQMainWnd: eqmain's window
static HMODULE        g_hEQMain         = nullptr;    // eqmain.dll handle (login screen module)
static FN_GetItemText   g_fnGetItemText   = nullptr;
static FN_SetCurSel     g_fnSetCurSel     = nullptr;
static FN_GetCurSel     g_fnGetCurSel     = nullptr;
static FN_GetChildItem  g_fnGetChildItem  = nullptr;
static FN_SetWindowText g_fnSetWindowText = nullptr;
static FN_GetWindowText g_fnGetWindowText = nullptr;
static FN_WndNotification g_fnWndNotification = nullptr;
static FN_CXStrCtor   g_fnCXStrCtor     = nullptr;
static FN_CXStrDtor   g_fnCXStrDtor     = nullptr;

// ─── CXStr struct ──────────────────────────────────────────────

struct CXStr {
    char    *Ptr;
    int      Length;
    int      Alloc;
    int      RefCount;
};

// ─── CEverQuest offset constants ───────────────────────────────

static const uint32_t OFFSET_CHARSELECT_ARRAY = 0x18EC0;
static const uint32_t CSI_SIZE       = 0x170;
static const uint32_t CSI_NAME_OFF   = 0x00;
static const uint32_t CSI_CLASS_OFF  = 0x40;
static const uint32_t CSI_LEVEL_OFF  = 0x48;

// ─── Offset validation state ───────────────────────────────────

static bool     g_offsetValidated   = false;
static uint32_t g_validatedOffset   = 0;

// ─── ReadListItemText helper ───────────────────────────────────

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

struct ArrayClassHeader {
    uint8_t *Data;
    int      Count;
    int      Alloc;
};

// ─── ValidateCharArrayOffset ───────────────────────────────────

static bool IsValidCharArray(const uint8_t *pEverQuest, uint32_t offset) {
    __try {
        const ArrayClassHeader *arr = (const ArrayClassHeader *)(pEverQuest + offset);

        if (arr->Count < 1 || arr->Count > CHARSEL_MAX_CHARS)
            return false;
        if (!arr->Data)
            return false;

        const char *name = (const char *)(arr->Data + CSI_NAME_OFF);
        if (name[0] < 0x20 || name[0] > 0x7E)
            return false;

        bool foundNull = false;
        for (int i = 0; i < 64; i++) {
            if (name[i] == '\0') { foundNull = true; break; }
            if (name[i] < 0x20 || name[i] > 0x7E)
                return false;
        }
        if (!foundNull) return false;
        if (name[1] == '\0') return false;

        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool ValidateCharArrayOffset(const uint8_t *pEverQuest) {
    if (g_offsetValidated) return true;

    if (IsValidCharArray(pEverQuest, OFFSET_CHARSELECT_ARRAY)) {
        g_validatedOffset = OFFSET_CHARSELECT_ARRAY;
        g_offsetValidated = true;
        DI8Log("mq2_bridge: charSelectPlayerArray validated at expected offset 0x%X", g_validatedOffset);
        return true;
    }

    DI8Log("mq2_bridge: expected offset 0x%X failed -- scanning +-0x200", OFFSET_CHARSELECT_ARRAY);

    const uint32_t scanRange = 0x200;
    uint32_t baseOffset = OFFSET_CHARSELECT_ARRAY;
    uint32_t startOffset = (baseOffset > scanRange) ? (baseOffset - scanRange) : 0;
    uint32_t endOffset = baseOffset + scanRange;

    for (uint32_t off = startOffset; off <= endOffset; off += 4) {
        if (off == baseOffset) continue;
        if (IsValidCharArray(pEverQuest, off)) {
            g_validatedOffset = off;
            g_offsetValidated = true;
            DI8Log("mq2_bridge: charSelectPlayerArray FOUND at scanned offset 0x%X (delta=%+d)",
                   off, (int)off - (int)baseOffset);
            return true;
        }
    }

    DI8Log("mq2_bridge: charSelectPlayerArray NOT FOUND in scan range");
    return false;
}

// ─── Pointer validation ───────────────────────────────────────

static bool IsReadablePtr(const void *ptr, size_t size) {
    if (!ptr) return false;
    MEMORY_BASIC_INFORMATION mbi;
    if (VirtualQuery(ptr, &mbi, sizeof(mbi)) == 0) return false;
    if (mbi.State != MEM_COMMIT) return false;
    if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;
    return true;
}

// ─── WndMgr window iteration ─────────────────────────────────
// CXWndManager layout (from MQ2 CXWnd.h):
//   64-bit: pWindows ArrayClass<CXWnd*> at offset 0x008
//   32-bit (x86): vtable(4) then pWindows ArrayClass at offset 0x004
// ArrayClass on x86 = {T* Data, int Count, int Alloc} = 12 bytes
// We scan a range of offsets starting from the top of the struct.

static const uint32_t g_wndMgrOffsets[] = {
    0x04, 0x08, 0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20,
    0x24, 0x28, 0x2C, 0x30, 0x34, 0x38, 0x3C, 0x40,
    // Also try the old offsets in case of a different build
    0x50, 0x54, 0x58, 0x5C, 0x60, 0x64, 0x68
};
static const int g_numWndMgrOffsets = sizeof(g_wndMgrOffsets) / sizeof(g_wndMgrOffsets[0]);

// Cached working offset for WndMgr window array
static uint32_t g_wndMgrValidOffset = 0;
static bool g_wndMgrOffsetFound = false;

// Iterate all windows in WndMgr and call a callback.
// Returns true if iteration succeeded.
typedef bool (*WndIterCallback)(void *pWnd, void *context);

static int g_iterLogCount = 0;
static bool g_dumpedOnce = false;

// Try a single WndMgr pointer with all offset candidates.
// Returns true if callback stopped early (found target).
static bool TryWndMgrPointer(void *pWndMgr, const char *label,
                             WndIterCallback callback, void *context) {
    if (!pWndMgr) return false;
    const uint8_t *pMgr = (const uint8_t *)pWndMgr;

    // One-time diagnostic dump: log raw memory at every candidate offset
    if (!g_dumpedOnce) {
        g_dumpedOnce = true;
        DI8Log("mq2_bridge: === DIAGNOSTIC DUMP %s at %p ===", label, pWndMgr);
        for (int c = 0; c < g_numWndMgrOffsets; c++) {
            uint32_t off = g_wndMgrOffsets[c];
            __try {
                const ArrayClassHeader *arr = (const ArrayClassHeader *)(pMgr + off);
                DI8Log("  offset 0x%02X: Data=%p Count=%d Alloc=%d",
                       off, arr->Data, arr->Count, arr->Alloc);
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                DI8Log("  offset 0x%02X: SEH EXCEPTION", off);
            }
        }
        DI8Log("mq2_bridge: === END DUMP ===");
    }

    // If we found a working offset before, try it first
    if (g_wndMgrOffsetFound) {
        __try {
            const ArrayClassHeader *arr = (const ArrayClassHeader *)(pMgr + g_wndMgrValidOffset);
            // Login screen may have very few windows (< 10), so accept >= 1
            if (arr->Count >= 1 && arr->Count <= 500 && arr->Data) {
                void **wndArray = (void **)arr->Data;
                for (int i = 0; i < arr->Count; i++) {
                    if (!wndArray[i]) continue;
                    if (!IsReadablePtr(wndArray[i], sizeof(void *))) continue;
                    void *vtable = *(void **)wndArray[i];
                    if (!IsReadablePtr(vtable, sizeof(void *))) continue;
                    if (callback(wndArray[i], context)) return true;
                }
                return false;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            g_wndMgrOffsetFound = false;
        }
    }

    // Scan all candidate offsets
    for (int c = 0; c < g_numWndMgrOffsets; c++) {
        uint32_t off = g_wndMgrOffsets[c];
        __try {
            const ArrayClassHeader *arr = (const ArrayClassHeader *)(pMgr + off);
            // Accept >= 1 window (login screen may have very few)
            if (arr->Count < 1 || arr->Count > 500) continue;
            if (!arr->Data) continue;
            if (!IsReadablePtr(arr->Data, sizeof(void *))) continue;

            void **wndArray = (void **)arr->Data;
            bool found = false;
            int validCount = 0;

            for (int i = 0; i < arr->Count; i++) {
                if (!wndArray[i]) continue;
                if (!IsReadablePtr(wndArray[i], sizeof(void *))) continue;
                void *vtable = *(void **)wndArray[i];
                if (!IsReadablePtr(vtable, sizeof(void *))) continue;
                validCount++;
                if (callback(wndArray[i], context)) { found = true; break; }
            }

            // Only cache if we found some valid windows
            if (validCount > 0) {
                if (!g_wndMgrOffsetFound) {
                    DI8Log("mq2_bridge: WndMgr window array found via %s at offset 0x%X (%d total, %d valid)",
                           label, off, arr->Count, validCount);
                }
                g_wndMgrValidOffset = off;
                g_wndMgrOffsetFound = true;
                if (found) return true;
                return false;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            // Bad offset, try next
        }
    }

    return false;
}

// ─── eqmain.dll CXWndManager discovery ────────────────────────
// At login, windows are in eqmain.dll's CXWndManager, not eqgame.exe's.
// Dalaya doesn't export EQMain__pinstCXWndManager, so we scan eqmain.dll's
// PE sections for a pointer that looks like a valid CXWndManager.

static void *g_pEQMainWndMgr = nullptr;
static bool g_eqmainScanned = false;

static void *FindEQMainWndMgr() {
    if (g_eqmainScanned) return g_pEQMainWndMgr;
    g_eqmainScanned = true;

    HMODULE hEQMain = GetModuleHandleA("eqmain.dll");
    if (!hEQMain) {
        DI8Log("mq2_bridge: eqmain.dll not loaded");
        return nullptr;
    }

    // Scan eqmain.dll's .data section for pointers that look like CXWndManager instances.
    // A valid CXWndManager has pWindows (ArrayClass<CXWnd*>) near the start with Count > 0.
    uint8_t *base = (uint8_t *)hEQMain;
    if (*(uint16_t *)base != 0x5A4D) return nullptr;
    int32_t eLfanew = *(int32_t *)(base + 0x3C);
    if (eLfanew < 0x40 || eLfanew > 0x1000) return nullptr;
    if (*(uint32_t *)(base + eLfanew) != 0x00004550) return nullptr;

    uint16_t numSections = *(uint16_t *)(base + eLfanew + 6);
    uint16_t optSize = *(uint16_t *)(base + eLfanew + 20);
    uint8_t *sh = base + eLfanew + 24 + optSize;

    // Find .data section
    uint8_t *dataBase = nullptr;
    uint32_t dataSize = 0;
    for (int i = 0; i < numSections && i < 64; i++, sh += 40) {
        char name[9] = {};
        memcpy(name, sh, 8);
        if (strcmp(name, ".data") == 0) {
            dataBase = base + *(uint32_t *)(sh + 12);
            dataSize = *(uint32_t *)(sh + 8);
            break;
        }
    }

    if (!dataBase || dataSize < 16) {
        DI8Log("mq2_bridge: eqmain.dll .data section not found");
        return nullptr;
    }

    DI8Log("mq2_bridge: scanning eqmain.dll .data at %p (%u bytes) for CXWndManager",
           dataBase, dataSize);

    // Scan .data for pointers to potential CXWndManager objects.
    // Each DWORD in .data could be a pointer to a CXWndManager.
    // We look for: a pointer to an object that has a valid ArrayClass
    // at various offsets with Count 1-500 and valid CXWnd* entries.
    int candidates = 0;
    for (uint32_t off = 0; off + 4 <= dataSize; off += 4) {
        __try {
            uintptr_t val = *(uintptr_t *)(dataBase + off);
            if (val < 0x10000 || val > 0x7FFFFFFF) continue; // not a valid x86 pointer

            uint8_t *candidate = (uint8_t *)val;
            if (!IsReadablePtr(candidate, 0x80)) continue;

            // Check for ArrayClass at small offsets (0x04-0x20)
            for (uint32_t arrOff = 0x04; arrOff <= 0x20; arrOff += 4) {
                const ArrayClassHeader *arr = (const ArrayClassHeader *)(candidate + arrOff);
                if (arr->Count < 1 || arr->Count > 500) continue;
                if (!arr->Data || !IsReadablePtr(arr->Data, arr->Count * 4)) continue;

                // Validate first entry looks like a CXWnd* (has readable vtable)
                void **wndArray = (void **)arr->Data;
                if (!wndArray[0]) continue;
                if (!IsReadablePtr(wndArray[0], 4)) continue;
                void *vtable = *(void **)wndArray[0];
                if (!IsReadablePtr(vtable, 4)) continue;

                // This looks like a valid CXWndManager!
                g_pEQMainWndMgr = candidate;
                g_wndMgrValidOffset = arrOff;
                g_wndMgrOffsetFound = true;
                DI8Log("mq2_bridge: FOUND eqmain CXWndManager at %p (data+0x%X), pWindows at offset 0x%X (%d windows)",
                       candidate, off, arrOff, arr->Count);
                return g_pEQMainWndMgr;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            // Not a valid pointer, skip
        }
        candidates++;
    }

    DI8Log("mq2_bridge: eqmain CXWndManager NOT FOUND (scanned %d candidates)", candidates);
    return nullptr;
}

// Direct iteration: given a CXWndManager pointer and the known pWindows offset,
// iterate all windows and call callback. No shared globals — simple and correct.
static bool IterateWindowsDirect(void *pWndMgr, uint32_t arrOffset,
                                 WndIterCallback callback, void *context) {
    if (!pWndMgr) return false;
    const uint8_t *pMgr = (const uint8_t *)pWndMgr;

    __try {
        const ArrayClassHeader *arr = (const ArrayClassHeader *)(pMgr + arrOffset);
        if (arr->Count < 1 || arr->Count > 500 || !arr->Data) return false;
        if (!IsReadablePtr(arr->Data, arr->Count * 4)) return false;

        void **wndArray = (void **)arr->Data;
        for (int i = 0; i < arr->Count; i++) {
            if (!wndArray[i]) continue;
            if (!IsReadablePtr(wndArray[i], sizeof(void *))) continue;
            void *vtable = *(void **)wndArray[i];
            if (!IsReadablePtr(vtable, sizeof(void *))) continue;
            if (callback(wndArray[i], context)) return true;
        }
        return false; // iterated all, callback didn't stop
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool IterateAllWindows(WndIterCallback callback, void *context) {
    // 1. Try eqmain.dll's CXWndManager (login screen)
    //    FindEQMainWndMgr caches the result and sets g_wndMgrValidOffset
    void *eqMainMgr = FindEQMainWndMgr();
    if (eqMainMgr && g_eqmainScanned) {
        // Use the offset discovered during scanning (stored in g_wndMgrValidOffset by FindEQMainWndMgr)
        if (IterateWindowsDirect(eqMainMgr, g_wndMgrValidOffset, callback, context))
            return true;
    }

    // 2. Try eqgame's CXWndManager (charselect and beyond)
    void *pWndMgr1 = nullptr;
    if (g_ppWndMgr) {
        __try { pWndMgr1 = *g_ppWndMgr; }
        __except (EXCEPTION_EXECUTE_HANDLER) { pWndMgr1 = nullptr; }
    }
    if (pWndMgr1) {
        // Scan all offsets for eqgame's WndMgr
        if (TryWndMgrPointer(pWndMgr1, "ppWndMgr", callback, context))
            return true;
    }

    return false;
}

// ─── FindWindowByName implementation ──────────────────────────

struct FindByNameCtx {
    const char *targetName;
    void *result;
};

static bool FindByNameCallback(void *pWnd, void *context) {
    FindByNameCtx *ctx = (FindByNameCtx *)context;
    if (!g_fnGetChildItem) return false;

    __try {
        void *child = g_fnGetChildItem(pWnd, ctx->targetName);
        if (child) {
            ctx->result = child;
            return true; // stop iteration
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        // Bad window, skip
    }
    return false;
}

// ─── Enumerate all windows (diagnostic) ───────────────────────
// Tries to read the "name" field from each CXWnd. On ROF2 x86,
// the window name (XMLIndex/ScreenID) is typically at offset 0xA8
// or nearby as a CXStr. We also try GetChildItem with known names.

struct EnumCtx {
    int count;
};

static bool EnumCallback(void *pWnd, void *context) {
    EnumCtx *ctx = (EnumCtx *)context;
    ctx->count++;

    // Log the window address and try to read its text
    if (g_fnGetWindowText && g_fnCXStrDtor) {
        __try {
            uint8_t cxstrBuf[16] = {};
            g_fnGetWindowText(pWnd, cxstrBuf);
            CXStr *str = (CXStr *)cxstrBuf;
            if (str->Ptr && str->Length > 0 && str->Length < 256) {
                char buf[256] = {};
                memcpy(buf, str->Ptr, str->Length < 255 ? str->Length : 255);
                DI8Log("mq2_bridge: ENUM wnd[%d] at %p text='%s'", ctx->count - 1, pWnd, buf);
            } else {
                DI8Log("mq2_bridge: ENUM wnd[%d] at %p (no text)", ctx->count - 1, pWnd);
            }
            g_fnCXStrDtor(cxstrBuf);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("mq2_bridge: ENUM wnd[%d] at %p (SEH reading text)", ctx->count - 1, pWnd);
        }
    } else {
        DI8Log("mq2_bridge: ENUM wnd[%d] at %p", ctx->count - 1, pWnd);
    }

    // Try known widget names against this window
    static const char *knownNames[] = {
        "LOGIN_UsernameEdit", "LOGIN_PasswordEdit", "LOGIN_ConnectButton",
        "OK_Display", "OK_OKButton",
        "YESNO_Display", "YESNO_YesButton", "YESNO_NoButton",
        "Character_List", "CLW_EnterWorldButton", "CLW_CharactersScreen",
        "CONNECT_ConnectButton", "CONNECT_UsernameEdit", "CONNECT_PasswordEdit",
        "serverselect", "connect", "CLW_CharactersScreen",
        "MAIN_ConnectButton",
        nullptr
    };

    if (g_fnGetChildItem) {
        for (int i = 0; knownNames[i]; i++) {
            __try {
                void *child = g_fnGetChildItem(pWnd, knownNames[i]);
                if (child) {
                    DI8Log("mq2_bridge: ENUM wnd[%d] has child '%s' at %p",
                           ctx->count - 1, knownNames[i], child);
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                // Skip
            }
        }
    }

    return false; // continue iterating
}

// ─── MQ2Bridge::Init ───────────────────────────────────────────

bool MQ2Bridge::Init() {
    DI8Log("mq2_bridge: Init -- resolving MQ2 exports from dinput8.dll");

    g_hMQ2 = GetModuleHandleA("dinput8.dll");
    if (!g_hMQ2) {
        DI8Log("mq2_bridge: dinput8.dll not loaded -- MQ2 bridge unavailable");
        return false;
    }
    DI8Log("mq2_bridge: dinput8.dll at 0x%p", g_hMQ2);

    // Resolve data exports
    g_pGameState = (volatile int *)GetProcAddress(g_hMQ2, "gGameState");
    g_ppEverQuest = (void **)GetProcAddress(g_hMQ2, "ppEverQuest");
    g_ppWndMgr = (void **)GetProcAddress(g_hMQ2, "ppWndMgr");
    // pinstCXWndManager is a uintptr_t — value IS the CXWndManager pointer (single deref)
    g_pinstWndMgr = (uintptr_t *)GetProcAddress(g_hMQ2, "pinstCXWndManager");
    g_pinstEQMainWnd = (uintptr_t *)GetProcAddress(g_hMQ2, "pinstCEQMainWnd");

    // eqmain.dll is the login screen module — has its own CXWndManager
    g_hEQMain = GetModuleHandleA("eqmain.dll");

    DI8Log("mq2_bridge: gGameState=%p  ppEverQuest=%p  ppWndMgr=%p",
           g_pGameState, g_ppEverQuest, g_ppWndMgr);
    DI8Log("mq2_bridge: pinstCXWndMgr=%p  pinstCEQMainWnd=%p  eqmain.dll=%p",
           g_pinstWndMgr, g_pinstEQMainWnd, g_hEQMain);

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

    // ── Resolve login-related exports (exact Dalaya mangled names) ──

    // CXWnd::SetWindowTextA(CXStr&)
    g_fnSetWindowText = (FN_SetWindowText)GetProcAddress(g_hMQ2,
        "?SetWindowTextA@CXWnd@EQClasses@@QAEXAAVCXStr@2@@Z");

    // CXWnd::GetWindowTextA() -> CXStr
    g_fnGetWindowText = (FN_GetWindowText)GetProcAddress(g_hMQ2,
        "?GetWindowTextA@CXWnd@EQClasses@@QBE?AVCXStr@2@XZ");

    // CXWnd::WndNotification(CXWnd*, uint, void*) -> int
    g_fnWndNotification = (FN_WndNotification)GetProcAddress(g_hMQ2,
        "?WndNotification@CXWnd@EQClasses@@QAEHPAV12@IPAX@Z");

    // CXStr constructor and destructor (needed for SetWindowTextA parameter)
    g_fnCXStrCtor = (FN_CXStrCtor)GetProcAddress(g_hMQ2,
        "??0CXStr@EQClasses@@QAE@PBD@Z");
    g_fnCXStrDtor = (FN_CXStrDtor)GetProcAddress(g_hMQ2,
        "??1CXStr@EQClasses@@QAE@XZ");

    DI8Log("mq2_bridge: SetWindowTextA=%p  GetWindowTextA=%p  WndNotification=%p",
           g_fnSetWindowText, g_fnGetWindowText, g_fnWndNotification);
    DI8Log("mq2_bridge: CXStr ctor=%p  dtor=%p", g_fnCXStrCtor, g_fnCXStrDtor);

    // Core requirement: gGameState and ppEverQuest for char reading;
    // ppWndMgr + GetChildItem for login UI manipulation
    bool ok = (g_pGameState != nullptr && g_ppEverQuest != nullptr);
    bool loginReady = (g_ppWndMgr != nullptr && g_fnGetChildItem != nullptr &&
                       g_fnSetWindowText != nullptr && g_fnWndNotification != nullptr &&
                       g_fnCXStrCtor != nullptr && g_fnCXStrDtor != nullptr);

    if (ok && loginReady)
        DI8Log("mq2_bridge: Init SUCCESS -- all exports resolved (char select + login)");
    else if (ok)
        DI8Log("mq2_bridge: Init PARTIAL -- char select OK, login exports missing (SetWindowText=%p WndNotification=%p)",
               g_fnSetWindowText, g_fnWndNotification);
    else
        DI8Log("mq2_bridge: Init PARTIAL -- missing core exports");

    return ok;
}

// ─── MQ2Bridge::ReadGameState ──────────────────────────────────

int MQ2Bridge::ReadGameState() {
    if (!g_pGameState) return -99;
    __try {
        return *g_pGameState;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return -99;
    }
}

// ─── MQ2Bridge::FindWindowByName ───────────────────────────────

static int g_findLogCount = 0;

void *MQ2Bridge::FindWindowByName(const char *name) {
    if (!g_fnGetChildItem || !name) {
        if (g_findLogCount < 3) {
            DI8Log("mq2_bridge: FindWindowByName('%s') — GetChildItem=%p", name ? name : "null", g_fnGetChildItem);
            g_findLogCount++;
        }
        return nullptr;
    }

    FindByNameCtx ctx = { name, nullptr };
    bool iterated = IterateAllWindows(FindByNameCallback, &ctx);

    if (g_findLogCount < 10 && !ctx.result) {
        DI8Log("mq2_bridge: FindWindowByName('%s') — iterated=%d, result=%p, wndMgrFound=%d",
               name, iterated, ctx.result, g_wndMgrOffsetFound);
        g_findLogCount++;
    }

    return ctx.result;
}

// ─── MQ2Bridge::SetEditText ────────────────────────────────────

void MQ2Bridge::SetEditText(void *pEditWnd, const char *text) {
    if (!g_fnSetWindowText || !g_fnCXStrCtor || !g_fnCXStrDtor || !pEditWnd || !text) return;

    __try {
        // Construct a CXStr from const char*, pass it to SetWindowTextA, then destroy
        uint8_t cxstrBuf[16] = {}; // CXStr is 16 bytes (Ptr, Length, Alloc, RefCount)
        g_fnCXStrCtor(cxstrBuf, text);
        g_fnSetWindowText(pEditWnd, cxstrBuf);
        g_fnCXStrDtor(cxstrBuf);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH in SetEditText");
    }
}

// ─── MQ2Bridge::ClickButton ───────────────────────────────────

void MQ2Bridge::ClickButton(void *pButton) {
    if (!pButton) return;
    if (!g_fnWndNotification) {
        DI8Log("mq2_bridge: ClickButton SKIPPED — WndNotification export not resolved");
        return;
    }

    __try {
        // XWM_LCLICK = 1, matching MQ2AutoLogin's SendWndNotification pattern
        g_fnWndNotification(pButton, pButton, 1, nullptr);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH in ClickButton");
    }
}

// ─── MQ2Bridge::ReadWindowText ─────────────────────────────────

void MQ2Bridge::ReadWindowText(void *pWnd, char *outBuf, int bufSize) {
    if (!outBuf || bufSize <= 0) return;
    outBuf[0] = '\0';

    if (!g_fnGetWindowText || !g_fnCXStrDtor || !pWnd) return;

    __try {
        // GetWindowTextA returns CXStr by value via hidden sret pointer
        uint8_t cxstrBuf[16] = {};
        g_fnGetWindowText(pWnd, cxstrBuf);

        CXStr *str = (CXStr *)cxstrBuf;
        if (str->Ptr && str->Length > 0) {
            int copyLen = (str->Length < bufSize - 1) ? str->Length : (bufSize - 1);
            memcpy(outBuf, str->Ptr, copyLen);
            outBuf[copyLen] = '\0';
        }

        // Destroy the returned CXStr to prevent memory leak
        g_fnCXStrDtor(cxstrBuf);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH in ReadWindowText");
    }
}

// ─── MQ2Bridge::SelectCharacter ────────────────────────────────

void MQ2Bridge::SelectCharacter(void *pCharList, int index) {
    if (!g_fnSetCurSel || !pCharList || index < 0) return;

    __try {
        g_fnSetCurSel(pCharList, index);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH in SelectCharacter(%d)", index);
    }
}

// ─── MQ2Bridge::PopulateCharacterData ──────────────────────────

void MQ2Bridge::PopulateCharacterData(volatile LoginShm *shm) {
    if (!shm || !g_ppEverQuest) {
        if (shm) { shm->charCount = 0; shm->selectedIndex = -1; }
        return;
    }

    void *pEverQuest = nullptr;
    __try { pEverQuest = *g_ppEverQuest; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    if (!pEverQuest) return;

    const uint8_t *pEQ = (const uint8_t *)pEverQuest;

    if (!ValidateCharArrayOffset(pEQ)) {
        shm->charCount = 0;
        shm->selectedIndex = -1;
        return;
    }

    __try {
        const ArrayClassHeader *arr = (const ArrayClassHeader *)(pEQ + g_validatedOffset);
        int count = arr->Count;
        const uint8_t *data = arr->Data;

        if (count < 0 || count > LOGIN_MAX_CHARS || !data) {
            shm->charCount = 0;
            shm->selectedIndex = -1;
            return;
        }

        for (int i = 0; i < count; i++) {
            const uint8_t *entry = data + (i * CSI_SIZE);
            const char *name = (const char *)(entry + CSI_NAME_OFF);

            int nameLen = 0;
            while (nameLen < LOGIN_NAME_LEN - 1 && name[nameLen] != '\0' &&
                   name[nameLen] >= 0x20 && name[nameLen] <= 0x7E) {
                nameLen++;
            }
            memcpy((void *)shm->charNames[i], name, nameLen);
            ((char *)shm->charNames[i])[nameLen] = '\0';

            shm->charLevels[i] = *(const int32_t *)(entry + CSI_LEVEL_OFF);
            shm->charClasses[i] = *(const int32_t *)(entry + CSI_CLASS_OFF);
        }

        MemoryBarrier();
        shm->charCount = count;

        for (int i = count; i < LOGIN_MAX_CHARS; i++) {
            ((char *)shm->charNames[i])[0] = '\0';
            shm->charLevels[i] = 0;
            shm->charClasses[i] = 0;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH reading charSelectPlayerArray");
        shm->charCount = 0;
        shm->selectedIndex = -1;
    }
}

// ─── MQ2Bridge::EnumerateAllWindows ────────────────────────────

void MQ2Bridge::EnumerateAllWindows() {
    if (!g_fnGetChildItem) {
        DI8Log("mq2_bridge: EnumerateAllWindows -- GetChildItem missing");
        return;
    }

    EnumCtx ctx = { 0 };
    IterateAllWindows(EnumCallback, &ctx);
    DI8Log("mq2_bridge: EnumerateAllWindows -- iterated %d windows", ctx.count);
}

// ─── MQ2Bridge::Poll (existing -- CharSelectShm) ───────────────

void MQ2Bridge::Poll(volatile CharSelectShm *shm) {
    if (!shm) return;

    if (!g_pGameState || !g_ppEverQuest) {
        shm->mq2Available = 0;
        return;
    }

    shm->mq2Available = 1;

    int gameState = ReadGameState();
    shm->gameState = gameState;

    // Reset cached state on game state transitions
    static int lastGameState = -1;
    if (gameState != lastGameState) {
        if (lastGameState != -1) {
            DI8Log("mq2_bridge: game state %d -> %d", lastGameState, gameState);
        }
        lastGameState = gameState;
    }

    // Handle Enter World request — no gameState gate.
    // Dalaya ROF2 uses gameState=0 at both login AND charselect.
    // We try the click regardless; FindWindowByName returns nullptr if
    // the button doesn't exist (not at charselect), which is handled.
    {
        uint32_t ewReq = shm->enterWorldReq;
        uint32_t ewAck = shm->enterWorldAck;

        if (ewReq != ewAck) {
            DI8Log("mq2_bridge: Enter World request (seq %u->%u, gameState=%d)", ewAck, ewReq, gameState);
            void *pEnterBtn = FindWindowByName("CLW_EnterWorldButton");
            if (pEnterBtn) {
                __try {
                    if (g_fnWndNotification)
                        g_fnWndNotification(pEnterBtn, pEnterBtn, 1 /*XWM_LCLICK*/, nullptr);
                    shm->enterWorldResult = 1;  // clicked
                    DI8Log("mq2_bridge: clicked CLW_EnterWorldButton");
                }
                __except (EXCEPTION_EXECUTE_HANDLER) {
                    shm->enterWorldResult = -1;
                    DI8Log("mq2_bridge: SEH clicking CLW_EnterWorldButton");
                }
            } else {
                shm->enterWorldResult = -1;
                DI8Log("mq2_bridge: CLW_EnterWorldButton not found (gameState=%d)", gameState);
            }
            MemoryBarrier();
            shm->enterWorldAck = ewReq;
        }
    }

    // Handle selection request — also no gameState gate for Dalaya compatibility.
    // The selection handler below (after char data read) checks charCount > 0.

    // Dalaya ROF2: gameState=0 at login AND charselect. We can't gate on
    // gameState==1. Instead, attempt to read character data always — the
    // SEH guards and validation (IsReadablePtr, name checks) handle invalid states.
    // If we're not at charselect, charCount will be 0 and nothing happens.
    if (gameState == 5) {
        // gameState 5 = in-game on Dalaya. Clear char data + skip polling.
        shm->charCount = 0;
        shm->selectedIndex = -1;
        g_offsetValidated = false;
        return;
    }

    // At character select -- read character data
    void *pEverQuest = nullptr;
    __try { pEverQuest = *g_ppEverQuest; }
    __except (EXCEPTION_EXECUTE_HANDLER) {
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

    if (!ValidateCharArrayOffset(pEQ)) {
        shm->charCount = 0;
        shm->selectedIndex = -1;
        return;
    }

    __try {
        const ArrayClassHeader *arr = (const ArrayClassHeader *)(pEQ + g_validatedOffset);
        int count = arr->Count;
        const uint8_t *data = arr->Data;

        if (count < 0 || count > CHARSEL_MAX_CHARS || !data) {
            shm->charCount = 0;
            shm->selectedIndex = -1;
            return;
        }

        for (int i = 0; i < count; i++) {
            const uint8_t *entry = data + (i * CSI_SIZE);
            const char *name = (const char *)(entry + CSI_NAME_OFF);
            int nameLen = 0;
            while (nameLen < CHARSEL_NAME_LEN - 1 && name[nameLen] != '\0' &&
                   name[nameLen] >= 0x20 && name[nameLen] <= 0x7E) {
                nameLen++;
            }
            memcpy((void *)shm->names[i], name, nameLen);
            ((char *)shm->names[i])[nameLen] = '\0';
            shm->levels[i] = *(const int32_t *)(entry + CSI_LEVEL_OFF);
            shm->classes[i] = *(const int32_t *)(entry + CSI_CLASS_OFF);
        }

        MemoryBarrier();
        shm->charCount = count;

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

    // Handle selection request from C#
    uint32_t reqSeq = shm->requestSeq;
    uint32_t ackSeq = shm->ackSeq;

    if (reqSeq != ackSeq) {
        int requestedIdx = shm->requestedIndex;
        DI8Log("mq2_bridge: selection request -- index=%d (seq %u->%u)",
               requestedIdx, ackSeq, reqSeq);

        if (requestedIdx >= 0 && requestedIdx < shm->charCount) {
            void *pCharListWnd = FindWindowByName("Character_List");
            if (pCharListWnd && g_fnSetCurSel) {
                __try {
                    g_fnSetCurSel(pCharListWnd, requestedIdx);
                    shm->selectedIndex = requestedIdx;
                    DI8Log("mq2_bridge: selected character index %d (\"%s\")",
                           requestedIdx, (const char *)shm->names[requestedIdx]);
                }
                __except (EXCEPTION_EXECUTE_HANDLER) {
                    DI8Log("mq2_bridge: SEH in SetCurSel(%d)", requestedIdx);
                }
            }
        }
        shm->ackSeq = reqSeq;
    }

}

// ─── MQ2Bridge::Shutdown ───────────────────────────────────────

void MQ2Bridge::Shutdown() {
    DI8Log("mq2_bridge: Shutdown -- nullifying pointers");

    g_pGameState       = nullptr;
    g_ppEverQuest      = nullptr;
    g_ppWndMgr         = nullptr;
    g_pinstWndMgr      = nullptr;
    g_pinstEQMainWnd   = nullptr;
    g_hEQMain          = nullptr;
    g_pEQMainWndMgr    = nullptr;
    g_eqmainScanned    = false;
    g_fnGetItemText    = nullptr;
    g_fnSetCurSel      = nullptr;
    g_fnGetCurSel      = nullptr;
    g_fnGetChildItem   = nullptr;
    g_fnSetWindowText  = nullptr;
    g_fnGetWindowText  = nullptr;
    g_fnWndNotification = nullptr;
    g_fnCXStrCtor      = nullptr;
    g_fnCXStrDtor      = nullptr;
    g_hMQ2             = nullptr;

    g_offsetValidated  = false;
    g_validatedOffset  = 0;
    g_wndMgrOffsetFound = false;
}
