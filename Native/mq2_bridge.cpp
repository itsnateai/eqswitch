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
static void         **g_ppWndMgr        = nullptr;
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
// Common ROF2 x86 offsets for ArrayClass<CXWnd*> inside CXWndManager

static const uint32_t g_wndMgrOffsets[] = { 0x58, 0x5C, 0x60, 0x54, 0x64, 0x50, 0x68 };
static const int g_numWndMgrOffsets = sizeof(g_wndMgrOffsets) / sizeof(g_wndMgrOffsets[0]);

// Cached working offset for WndMgr window array
static uint32_t g_wndMgrValidOffset = 0;
static bool g_wndMgrOffsetFound = false;

// Iterate all windows in WndMgr and call a callback.
// Returns true if iteration succeeded.
typedef bool (*WndIterCallback)(void *pWnd, void *context);

static bool IterateAllWindows(WndIterCallback callback, void *context) {
    if (!g_ppWndMgr) return false;

    void *pWndMgr = nullptr;
    __try { pWndMgr = *g_ppWndMgr; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    if (!pWndMgr) return false;

    const uint8_t *pMgr = (const uint8_t *)pWndMgr;

    // If we found a working offset before, try it first
    if (g_wndMgrOffsetFound) {
        __try {
            const ArrayClassHeader *arr = (const ArrayClassHeader *)(pMgr + g_wndMgrValidOffset);
            if (arr->Count >= 10 && arr->Count <= 500 && arr->Data) {
                void **wndArray = (void **)arr->Data;
                for (int i = 0; i < arr->Count; i++) {
                    if (!wndArray[i]) continue;
                    if (!IsReadablePtr(wndArray[i], sizeof(void *))) continue;
                    void *vtable = *(void **)wndArray[i];
                    if (!IsReadablePtr(vtable, sizeof(void *))) continue;
                    if (callback(wndArray[i], context)) return true;
                }
                return false; // iterated but callback didn't stop early
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
            if (arr->Count < 10 || arr->Count > 500) continue;
            if (!arr->Data) continue;

            void **wndArray = (void **)arr->Data;
            bool found = false;

            for (int i = 0; i < arr->Count; i++) {
                if (!wndArray[i]) continue;
                if (!IsReadablePtr(wndArray[i], sizeof(void *))) continue;
                void *vtable = *(void **)wndArray[i];
                if (!IsReadablePtr(vtable, sizeof(void *))) continue;
                if (callback(wndArray[i], context)) { found = true; break; }
            }

            // If we got through without crashing, cache this offset
            g_wndMgrValidOffset = off;
            g_wndMgrOffsetFound = true;

            if (found) return true;
            return false; // iterated successfully but didn't find target
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            // Bad offset, try next
        }
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

void *MQ2Bridge::FindWindowByName(const char *name) {
    if (!g_fnGetChildItem || !name) return nullptr;

    FindByNameCtx ctx = { name, nullptr };
    IterateAllWindows(FindByNameCallback, &ctx);
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
    if (!g_fnWndNotification || !pButton) return;

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
    if (!g_fnGetChildItem || !g_ppWndMgr) {
        DI8Log("mq2_bridge: EnumerateAllWindows -- GetChildItem or ppWndMgr missing");
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

    if (gameState != 1) {
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
