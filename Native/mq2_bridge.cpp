// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

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
// psapi.h not needed — we read SizeOfImage from PE header directly
#include <stdint.h>
#include <string.h>
#include "mq2_bridge.h"
#include "login_shm.h"
#include "login_givetime_detour.h"
#include "eqmain_offsets.h"

// ─── Forward declarations ──────────────────────────────────────

void DI8Log(const char *fmt, ...);

// CXMLDataPtr vtable RVA — Dalaya x86 eqmain. Used in cross-ref scans
// and walker indirect-backref check (live widget holds CXMLDataPtr* whose
// m_pXMLData == def). Declared at file scope so both FindLiveCXWnd's
// heap cross-ref (Iteration 5) and WalkForDefBackref's tree walk can use it.
static constexpr uint32_t RVA_VTABLE_CXMLDataPtr_Dalaya = 0x0010A7D4;

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
static void         **g_ppWndMgr        = nullptr;    // ppWndMgr: triple-ptr (&ForeignPointer.m_ptr)
static uintptr_t     *g_pinstWndMgr     = nullptr;    // pinstCXWndManager: *ptr IS CXWndManager*
static uintptr_t     *g_pinstEQMainWnd  = nullptr;    // pinstCEQMainWnd: eqmain's window
static uintptr_t     *g_pinstCharSelect = nullptr;    // pinstCCharacterSelect: direct char select wnd
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

// Verified CCharacterSelect vtable on Dalaya ROF2 (stable across sessions)
static const uintptr_t CHARSELECT_EXPECTED_VTABLE = 0x00B05410;

static const uint32_t OFFSET_CHARSELECT_ARRAY = 0x18EC0;
// Hotfix v6f: stride is 0x160, not 0x170 (per live RPM intel 2026-04-14 and the
// comment 10 lines below this at line 111 that says "0x160-byte structs" — the
// constant was wrong-by-one-nibble since the reader was written). The 0x10-byte
// miscount means every entry after entry[0] reads from shifted offsets; at best
// it produces garbage, at worst it reads an adjacent UI field-label string like
// "Height" for the name. Fixes the heap-scan path AND this primary Poll path to
// agree on HEAP_SCAN_STRIDE (0x160). Class/level fields inside this struct are
// UNRELIABLE per memory intel (class not in this array at all; 0x50 is a stale
// level that holds prior char's max level when a slot was recreated). Keep the
// reads but don't trust the values — a proper level+class sourcing is v7 work.
static const uint32_t CSI_SIZE       = 0x160;
static const uint32_t CSI_NAME_OFF   = 0x00;
static const uint32_t CSI_CLASS_OFF  = 0x40;    // UNRELIABLE — see note above
static const uint32_t CSI_LEVEL_OFF  = 0x48;    // UNRELIABLE — see note above

// ─── Offset validation state ───────────────────────────────────

// volatile: accessed from ActivateThread + TIMERPROC (game thread)
static volatile bool     g_offsetValidated   = false;
static volatile uint32_t g_validatedOffset   = 0;
static volatile bool     g_uiFallbackLogged  = false;
static volatile int      g_cachedNameCol     = -1;
static volatile bool     g_charArrayNotFoundLogged = false;
static volatile int      g_cachedSlotCount   = -1;  // slot probe result cache (-1 = not probed)
static volatile bool     g_verificationDone  = false;
static volatile uintptr_t g_heapScanArrayBase = 0;   // heap scan result (0 = not found/not scanned)
static volatile bool     g_heapScanDone      = false; // one-shot per charselect session
static int               g_standaloneDelay   = 0;    // delay standalone heap scan by N poll cycles

// ─── Heap scan for character name array ───────────────────────
// Dalaya ROF2 stores char names in a heap-allocated array of 0x160-byte structs.
// Standard MQ2 charSelectPlayerArray offset doesn't exist. We scan committed pages
// for the pattern: 10 consecutive entries at 0x160 stride, each starting with a
// printable ASCII name (uppercase first char, >= 3 chars, null-terminated within 64 bytes).
// Runs ONCE per charselect session (gated by g_heapScanDone).

static bool IsPlausibleName(const uint8_t *p) {
    // EQ character names: strict title case — uppercase first, ALL rest lowercase.
    // Length 4-15 chars. Rejects UI labels like "Height", "Heading" via blocklist.
    if (p[0] < 'A' || p[0] > 'Z') return false;
    int len = 0;
    for (int i = 1; i < 64; i++) {
        if (p[i] == '\0') { len = i; break; }
        // v7 Phase 4: strict lowercase after first char. Rejects "MinVSize",
        // "OneTextures", "DrawLinesFill" etc. that matched the old rule.
        if (p[i] < 'a' || p[i] > 'z') return false;
    }
    if (len < 4 || len > 15) return false;

    // Blocklist: common EQ/eqmain UI labels that pass strict title-case.
    // "Height" and "Heading" are the known false positives from eqmain's
    // character-info panel label block (see v6f hotfix notes).
    static const char *const kBadNames[] = {
        "Height", "Heading", "Class", "Level", "Name", "Race", "Deity",
        "Gender", "Strength", "Stamina", "Charisma", "Dexterity",
        "Agility", "Intelligence", "Wisdom", "Account", "Character",
        "Login", "Server", "World", "Select", "Options", "Default",
        nullptr
    };
    for (int k = 0; kBadNames[k]; k++) {
        const char *bad = kBadNames[k];
        int bi = 0;
        while (bad[bi] && (char)p[bi] == bad[bi]) bi++;
        if (!bad[bi] && p[bi] == '\0') return false;
    }
    return true;
}

static const uint32_t HEAP_SCAN_STRIDE = 0x160;

static uintptr_t HeapScanForCharArray() {
    MEMORY_BASIC_INFORMATION mbi;
    uintptr_t addr = 0x01000000; // skip low addresses
    int pagesScanned = 0;

    while (addr < 0x7FFF0000 && pagesScanned < 200000) {
        if (VirtualQuery((void *)addr, &mbi, sizeof(mbi)) == 0) break;

        uintptr_t base = (uintptr_t)mbi.BaseAddress;
        SIZE_T size = mbi.RegionSize;

        if (mbi.State == MEM_COMMIT &&
            !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) &&
            (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE))) {

            pagesScanned++;
            // Scan in 64KB chunks
            for (uintptr_t off = 0; off < size; off += 0x10000) {
                uintptr_t chunk = base + off;
                SIZE_T chunkSize = (size - off < 0x10000) ? (size - off) : 0x10000;

                // Need at least 10 * 0x160 = 0xDC0 bytes
                if (chunkSize < 10 * HEAP_SCAN_STRIDE) continue;

                __try {
                    const uint8_t *p = (const uint8_t *)chunk;
                    // Step through the chunk looking for name-like starts
                    for (uintptr_t i = 0; i + 10 * HEAP_SCAN_STRIDE <= chunkSize; i += 4) {
                        if (!IsPlausibleName(p + i)) continue;
                        // Check if entries at stride 0x160 also have plausible names
                        int validCount = 1;
                        for (int s = 1; s < 10; s++) {
                            if (IsPlausibleName(p + i + s * HEAP_SCAN_STRIDE))
                                validCount++;
                            else
                                break;
                        }
                        if (validCount >= 5) {
                            // Strong match — 5+ consecutive name-like entries at 0x160 stride
                            uintptr_t arrayAddr = chunk + i;
                            DI8Log("mq2_bridge: heap scan FOUND char array at 0x%08X (%d/%d names valid)",
                                   arrayAddr, validCount, 10);
                            return arrayAddr;
                        }
                    }
                } __except(EXCEPTION_EXECUTE_HANDLER) {
                    // Page became unreadable mid-scan, skip
                }
            }
        }

        addr = base + size;
        if (addr <= base) addr = base + 0x1000;
    }

    DI8Log("mq2_bridge: heap scan: no char array found (%d pages scanned)", pagesScanned);
    return 0;
}

// ─── ReadListItemText helper ───────────────────────────────────

static bool ReadListItemText(void *listWnd, int row, int col, char *outBuf, int bufSize) {
    if (!g_fnGetItemText || !listWnd || bufSize <= 0) return false;

    outBuf[0] = '\0';
    bool result = false;

    __try {
        CXStr str = {};
        g_fnGetItemText(listWnd, &str, row, col);

        if (str.Ptr && str.Length > 0) {
            int copyLen = (str.Length < bufSize - 1) ? str.Length : (bufSize - 1);
            memcpy(outBuf, str.Ptr, copyLen);
            outBuf[copyLen] = '\0';
            result = true;
        }

        // MUST destroy CXStr — GetItemText allocates from game's CRT heap
        if (g_fnCXStrDtor) g_fnCXStrDtor(&str);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH in ReadListItemText(row=%d, col=%d)", row, col);
    }

    return result;
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
        // Character name MUST start with uppercase A-Z per EQ naming rules.
        // Field-label strings like "Height" also start uppercase, so this alone
        // isn't enough — tighten further below with length + reject known-label list.
        if (name[0] < 'A' || name[0] > 'Z')
            return false;

        int len = 0;
        for (int i = 0; i < 64; i++) {
            if (name[i] == '\0') { len = i; break; }
            // Name chars are A-Z lowercase/uppercase only (no spaces, digits, punctuation).
            char c = name[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
                return false;
        }
        // EQ names: min 4 chars, max 15 chars. Reject anything outside.
        if (len < 4 || len > 15) return false;

        // Hotfix v6f: reject known eqmain UI field-label strings that passed the
        // old "printable ASCII" validator. "Height" was the actual symptom —
        // heap-scan hit returned eqmain's character-info panel label block
        // instead of the charselect array. If one of these strings appears as
        // "name", we're reading from the wrong base.
        static const char *const kBadNames[] = {
            "Height", "Class", "Level", "Name", "Race", "Deity", "Gender",
            "Strength", "Stamina", "Charisma", "Dexterity", "Agility",
            "Intelligence", "Wisdom", "Account", "Character", "Login",
            nullptr
        };
        for (int k = 0; kBadNames[k]; k++) {
            const char *bad = kBadNames[k];
            int bi = 0;
            while (bad[bi] && name[bi] == bad[bi]) bi++;
            if (!bad[bi] && name[bi] == '\0') {
                // Exact match to a UI label — not a character name.
                return false;
            }
        }

        // Additional sanity: entry[0].level field (even though unreliable as-displayed)
        // should be in a plausible character-level range 1..250 OR zero (stale slot).
        // Reject if it's obviously garbage (negative, > 10000).
        int32_t lvl = *(const int32_t *)(arr->Data + CSI_LEVEL_OFF);
        if (lvl < 0 || lvl > 10000) return false;

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
        g_charArrayNotFoundLogged = false;
        DI8Log("mq2_bridge: charSelectPlayerArray validated at expected offset 0x%X", g_validatedOffset);
        return true;
    }

    // Log scan attempt only once per session (resets on game state transition)
    if (!g_charArrayNotFoundLogged)
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
            g_charArrayNotFoundLogged = false;
            DI8Log("mq2_bridge: charSelectPlayerArray FOUND at scanned offset 0x%X (delta=%+d)",
                   off, (int)off - (int)baseOffset);
            return true;
        }
    }

    if (!g_charArrayNotFoundLogged) {
        DI8Log("mq2_bridge: charSelectPlayerArray NOT FOUND in scan range (suppressing future logs)");
        g_charArrayNotFoundLogged = true;
    }
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
    0x08, // Verified on Dalaya ROF2 (630 windows at charselect)
    0x04, 0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20,
    0x24, 0x28, 0x2C, 0x30, 0x34, 0x38, 0x3C, 0x40,
    0x50, 0x54, 0x58, 0x5C, 0x60, 0x64, 0x68
};
static const int g_numWndMgrOffsets = sizeof(g_wndMgrOffsets) / sizeof(g_wndMgrOffsets[0]);

// Cached working offset for WndMgr window array (volatile: dual-thread access)
static volatile uint32_t g_wndMgrValidOffset = 0;
static volatile bool g_wndMgrOffsetFound = false;

// Iterate all windows in WndMgr and call a callback.
// Returns true if iteration succeeded.
typedef bool (*WndIterCallback)(void *pWnd, void *context);


// Try a single WndMgr pointer with all offset candidates.
// Returns true if callback stopped early (found target).
static bool TryWndMgrPointer(void *pWndMgr, const char *label,
                             WndIterCallback callback, void *context) {
    if (!pWndMgr) return false;
    const uint8_t *pMgr = (const uint8_t *)pWndMgr;

    // Diagnostic dump removed (production) — verification report covers this

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

static volatile void *g_pEQMainWndMgr = nullptr;
static volatile bool g_eqmainScanned = false;
static volatile uint32_t g_eqmainWndMgrOffset = 0;  // dedicated offset for eqmain

static void *FindEQMainWndMgr() {
    // Re-check if eqmain.dll is still loaded — it unloads at charselect transition.
    // Stale cached pointer = use-after-free on freed module memory.
    HMODULE hEQMain = GetModuleHandleA("eqmain.dll");
    if (!hEQMain) {
        if (g_pEQMainWndMgr) {
            DI8Log("mq2_bridge: eqmain.dll unloaded — clearing cached WndMgr");
            g_pEQMainWndMgr = nullptr;
            g_eqmainScanned = false;
            g_eqmainWndMgrOffset = 0;
        }
        // v7 Phase 4: clear dangling LoginController* — its memory lived in
        // eqmain.dll's address space and is now unmapped.
        GiveTimeDetour::ClearLoginController();
        // v7 Phase 5: widget definition objects live on the heap managed by
        // eqmain.dll — they're freed when eqmain unloads.
        MQ2Bridge::ResetWidgetCache();
        return nullptr;
    }
    // v7 Phase 4b: validate cached result has enough valid windows before returning it.
    // The initial scan can run before eqmain creates login widgets, finding a false
    // positive (e.g. 103 entries but only 3 valid vtable pointers). Re-validate on
    // each call and clear the cache if the result looks wrong, triggering rescan.
    if (g_eqmainScanned && g_pEQMainWndMgr) {
        // Quick re-validate: count valid windows in the cached ArrayClass
        uint8_t *cached = (uint8_t *)g_pEQMainWndMgr;
        const ArrayClassHeader *arr = (const ArrayClassHeader *)(cached + g_eqmainWndMgrOffset);
        if (arr->Count >= 1 && arr->Count <= 500 && arr->Data &&
            IsReadablePtr(arr->Data, arr->Count * 4)) {
            void **wndArray = (void **)arr->Data;
            int validNow = 0;
            for (int k = 0; k < arr->Count && k < 50; k++) {
                if (!wndArray[k]) continue;
                if (!IsReadablePtr(wndArray[k], sizeof(void *))) continue;
                void *vt = *(void **)wndArray[k];
                if (vt && IsReadablePtr(vt, sizeof(void *))) validNow++;
            }
            if (validNow >= 15) {
                return (void *)g_pEQMainWndMgr; // Still good
            }
            // Cached result degraded or was a false positive — clear and rescan
            DI8Log("mq2_bridge: cached CXWndManager at %p has only %d valid windows — clearing for rescan",
                   g_pEQMainWndMgr, validNow);
            g_pEQMainWndMgr = nullptr;
        } else {
            g_pEQMainWndMgr = nullptr;
        }
    }
    if (g_eqmainScanned && !g_pEQMainWndMgr) {
        // Allow rescan — eqmain's windows may have been created since last attempt.
        // Throttle: only rescan every 5 calls (~2.5 seconds at 500ms poll rate).
        static int rescanCount = 0;
        if (++rescanCount % 5 != 0) return nullptr;
    }
    g_eqmainScanned = true;

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

    // Scan .data for ALL potential CXWndManager candidates, pick the BEST one
    // (most valid windows). Don't stop at first match — the first hit is often
    // a false positive (310 entries, 20 valid) while the real CXWndManager has
    // 100+ valid entries with actual login screen widgets.
    int scanCount = 0;
    uint8_t *bestCandidate = nullptr;
    uint32_t bestArrOff = 0;
    int bestValid = 0;
    int bestCount = 0;
    uint32_t bestDataOff = 0;

    for (uint32_t off = 0; off + 4 <= dataSize; off += 4) {
        __try {
            uintptr_t val = *(uintptr_t *)(dataBase + off);
            if (val < 0x10000 || val > 0x7FFFFFFF) continue;

            uint8_t *candidate = (uint8_t *)val;
            if (!IsReadablePtr(candidate, 0x70)) continue;

            // Check for ArrayClass at offsets 0x04-0x60.
            // Dalaya's CXWndManager has pWindows at 0x54 (confirmed at charselect).
            for (uint32_t arrOff = 0x04; arrOff <= 0x60; arrOff += 4) {
                const ArrayClassHeader *arr = (const ArrayClassHeader *)(candidate + arrOff);
                if (arr->Count < 1 || arr->Count > 500) continue;
                if (!arr->Data || !IsReadablePtr(arr->Data, arr->Count * 4)) continue;

                void **wndArray = (void **)arr->Data;
                if (!wndArray[0]) continue;
                if (!IsReadablePtr(wndArray[0], 4)) continue;
                void *vtable = *(void **)wndArray[0];
                if (!IsReadablePtr(vtable, 4)) continue;

                // Count valid vtable entries (check more — up to 50)
                int validEntries = 0;
                for (int k = 0; k < arr->Count && k < 50; k++) {
                    if (!wndArray[k]) continue;
                    if (!IsReadablePtr(wndArray[k], sizeof(void *))) continue;
                    void *vt = *(void **)wndArray[k];
                    if (vt && IsReadablePtr(vt, sizeof(void *))) validEntries++;
                }
                if (validEntries >= 15 && validEntries > bestValid) {
                    bestCandidate = candidate;
                    bestArrOff = arrOff;
                    bestValid = validEntries;
                    bestCount = arr->Count;
                    bestDataOff = off;
                    DI8Log("mq2_bridge: CXWndManager candidate at %p (data+0x%X), offset 0x%X (%d windows, %d valid) — new best",
                           candidate, off, arrOff, arr->Count, validEntries);
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            // Not a valid pointer, skip
        }
        scanCount++;
    }

    if (bestCandidate) {
        g_pEQMainWndMgr = bestCandidate;
        g_eqmainWndMgrOffset = bestArrOff;
        DI8Log("mq2_bridge: SELECTED eqmain CXWndManager at %p (data+0x%X), pWindows at offset 0x%X (%d windows, %d valid)",
               bestCandidate, bestDataOff, bestArrOff, bestCount, bestValid);
        return (void *)g_pEQMainWndMgr;
    }

    DI8Log("mq2_bridge: eqmain CXWndManager NOT FOUND (scanned %d entries)", scanCount);
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
    //    FindEQMainWndMgr caches the result and g_eqmainWndMgrOffset (separate from eqgame's)
    void *eqMainMgr = FindEQMainWndMgr();
    if (eqMainMgr && g_eqmainScanned && g_eqmainWndMgrOffset) {
        if (IterateWindowsDirect(eqMainMgr, g_eqmainWndMgrOffset, callback, context))
            return true;
    }

    // 2. Try pinstCXWndManager (DOUBLE deref → CXWndManager*)
    //    pinstCXWndManager is a uintptr_t whose value is the ADDRESS where CXWndManager* is stored.
    //    Deref 1: *g_pinstWndMgr = storage address. Deref 2: *storage = CXWndManager*.
    void *pWndMgrInst = nullptr;
    if (g_pinstWndMgr) {
        __try {
            uintptr_t storageAddr = *g_pinstWndMgr;  // deref 1: get storage address
            if (storageAddr && IsReadablePtr((void *)storageAddr, sizeof(void *))) {
                pWndMgrInst = *(void **)storageAddr;  // deref 2: get CXWndManager*
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { pWndMgrInst = nullptr; }
    }
    if (pWndMgrInst) {
        if (TryWndMgrPointer(pWndMgrInst, "pinstCXWndManager", callback, context))
            return true;
    }

    // 3. Try ppWndMgr (ForeignPointer — needs DOUBLE deref to reach CXWndManager*)
    //    ppWndMgr points to a ForeignPointer<CXWndManager> whose first field is CXWndManager** m_ptr.
    //    Deref 1: *ppWndMgr → m_ptr (CXWndManager**). Deref 2: *m_ptr → CXWndManager*.
    void *pWndMgr2 = nullptr;
    if (g_ppWndMgr) {
        __try {
            void **m_ptr = (void **)*g_ppWndMgr;  // first deref: get ForeignPointer.m_ptr
            if (m_ptr && IsReadablePtr(m_ptr, sizeof(void *))) {
                pWndMgr2 = *m_ptr;                 // second deref: get CXWndManager*
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { pWndMgr2 = nullptr; }
    }
    // Only try ppWndMgr if it gives a DIFFERENT pointer than pinstCXWndManager (avoid double scan)
    if (pWndMgr2 && pWndMgr2 != pWndMgrInst) {
        if (TryWndMgrPointer(pWndMgr2, "ppWndMgr(2x)", callback, context))
            return true;
    }

    return false;
}

// ─── Widget Heap Scan Cache ───────────────────────────────────
//
// v7 Phase 5: bypass GetChildItem entirely during login.
//
// Problem: MQ2's GetChildItem thunks to eqgame.exe code which reads a
// global template manager at a fixed address. During login, eqmain.dll
// manages its own UI system and that global is NULL → GetChildItem
// always fails with an SEH fault.
//
// Solution: scan the heap for "screen piece definition" objects that
// store the SIDL widget name as a CXStr member at offset +0x18.
//
// CXStr buffer layout (Dalaya ROF2):
//   [+0x00] int refcount
//   [+0x04] int alloc
//   [+0x08] int length
//   [+0x0C] int pad (0)
//   [+0x10] void* allocator_table_ptr (constant per session)
//   [+0x14] char data[]  (null-terminated string)
//
// So CXStr object = single DWORD pointing to buffer base.
// String content = *(CXStr) + 20.
//
// Widget definition object layout:
//   [+0x00] void* vtable  (in eqmain.dll range)
//   [+0x18] CXStr  name   (SIDL name like "LOGIN_UsernameEdit")

struct WidgetCacheEntry {
    const char *name;        // static string (caller's constant)
    void       *pWidget;     // cached result (definition object pointer)
    bool        searched;    // true = already scanned for this name
};

static const int WIDGET_CACHE_MAX = 16;
static WidgetCacheEntry g_widgetCache[WIDGET_CACHE_MAX] = {};
static int              g_widgetCacheCount = 0;
static volatile bool    g_widgetScanDone = false;  // true after first full scan

static void *HeapScanForWidget(const char *name) {
    static int scanCallCount = 0;
    if (scanCallCount < 5) {
        DI8Log("mq2_bridge: HeapScanForWidget('%s') — starting scan", name);
        scanCallCount++;
    }

    // Find eqmain.dll range (ASLR — resolves fresh each call)
    HMODULE hEqmain = GetModuleHandleA("eqmain.dll");
    if (!hEqmain) return nullptr;

    // Get eqmain size from PE header (avoids psapi.lib dependency)
    uintptr_t eqmLo = (uintptr_t)hEqmain;
    uintptr_t eqmHi = eqmLo;
    __try {
        const uint8_t *pe = (const uint8_t *)hEqmain;
        uint32_t e_lfanew = *(const uint32_t *)(pe + 0x3C);
        if (e_lfanew < 0x400) {
            uint32_t sizeOfImage = *(const uint32_t *)(pe + e_lfanew + 0x50);
            eqmHi = eqmLo + sizeOfImage;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: HeapScanForWidget — failed to read eqmain PE header");
        return nullptr;
    }
    if (eqmHi <= eqmLo) {
        DI8Log("mq2_bridge: HeapScanForWidget — bad eqmain range 0x%08X-0x%08X", eqmLo, eqmHi);
        return nullptr;
    }

    DI8Log("mq2_bridge: HeapScanForWidget('%s') — eqmain range 0x%08X-0x%08X", name, eqmLo, eqmHi);

    int nameLen = (int)strlen(name);
    MEMORY_BASIC_INFORMATION mbi;
    uintptr_t addr = 0x00010000; // start from very low
    int pagesScanned = 0;
    int regionsTotal = 0;
    uintptr_t lastAddr = 0;

    while (addr < 0x7FFF0000 && pagesScanned < 300000) {
        if (VirtualQuery((void *)addr, &mbi, sizeof(mbi)) == 0) {
            DI8Log("mq2_bridge: HeapScanForWidget — VirtualQuery failed at 0x%08X (err=%d), last=0x%08X",
                   addr, GetLastError(), lastAddr);
            break;
        }

        uintptr_t base = (uintptr_t)mbi.BaseAddress;
        SIZE_T size = mbi.RegionSize;
        regionsTotal++;
        lastAddr = base;

        // Only scan committed, readable, private/mapped (heap) pages
        if (mbi.State == MEM_COMMIT &&
            !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) &&
            (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE)) &&
            (mbi.Type == MEM_PRIVATE || mbi.Type == MEM_MAPPED)) {

            pagesScanned++;

            __try {
                const uint8_t *p = (const uint8_t *)base;

                for (uintptr_t off = 0; off + 0x20 <= size; off += 4) {
                    // Check if DWORD at this position is an eqmain vtable
                    uintptr_t vt = *(const uintptr_t *)(p + off);
                    if (vt < eqmLo || vt >= eqmHi) continue;

                    // Quick vtable validation: first entry should be in code range
                    uintptr_t vt0 = *(const uintptr_t *)vt;
                    if (vt0 < eqmLo || vt0 >= eqmHi) {
                        // Also accept eqgame.exe code range
                        if (vt0 < 0x00400000 || vt0 >= 0x02200000) continue;
                    }

                    // Read CXStr buf_base at +0x18 from this object
                    uintptr_t cxstrBufBase = *(const uintptr_t *)(p + off + 0x18);
                    if (cxstrBufBase < 0x10000 || cxstrBufBase > 0x7FFFFFFF) continue;

                    // Validate CXStr buffer header: length at buf_base+8 should match
                    __try {
                        int bufLen = *(const int *)(cxstrBufBase + 8);
                        if (bufLen != nameLen) continue;

                        // String data at buf_base + 20
                        const char *str = (const char *)(cxstrBufBase + 20);

                        // Fast first-char check
                        if (str[0] != name[0]) continue;

                        // Full string comparison (bounded by length)
                        bool match = true;
                        for (int i = 0; i < nameLen; i++) {
                            if (str[i] != name[i]) { match = false; break; }
                        }
                        if (!match || str[nameLen] != '\0') continue;

                        // FOUND! Return this object's address
                        void *result = (void *)(base + off);
                        DI8Log("mq2_bridge: HeapScanForWidget('%s') FOUND at %p (vt=%p, eqmain=0x%08X-0x%08X)",
                               name, result, (void *)vt, eqmLo, eqmHi);
                        return result;
                    }
                    __except (EXCEPTION_EXECUTE_HANDLER) {
                        // CXStr buf_base pointed to bad memory, skip
                    }
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                // Page became unreadable mid-scan
            }
        }

        addr = base + size;
        if (addr <= base) addr = base + 0x1000;
    }

    DI8Log("mq2_bridge: HeapScanForWidget('%s') — not found (%d heap pages, %d total regions, last=0x%08X)",
           name, pagesScanned, regionsTotal, lastAddr);
    return nullptr;
}

// Cached wrapper: scans once per widget name, caches result
static void *FindWidgetByHeapScan(const char *name) {
    // Check cache first
    for (int i = 0; i < g_widgetCacheCount; i++) {
        if (g_widgetCache[i].name == name || strcmp(g_widgetCache[i].name, name) == 0) {
            return g_widgetCache[i].pWidget;  // return cached (may be nullptr if not found)
        }
    }

    // Not in cache — do the scan
    void *result = HeapScanForWidget(name);

    // Cache the result (even if nullptr, to avoid re-scanning)
    if (g_widgetCacheCount < WIDGET_CACHE_MAX) {
        g_widgetCache[g_widgetCacheCount].name = name;
        g_widgetCache[g_widgetCacheCount].pWidget = result;
        g_widgetCache[g_widgetCacheCount].searched = true;
        g_widgetCacheCount++;
    }

    return result;
}

// Forward declarations for Phase 6 live CXWnd cache (defined below)
static void ResetLiveWidgetCache();

// Reset widget cache (call when eqmain.dll unloads at charselect transition)
void MQ2Bridge::ResetWidgetCache() {
    for (int i = 0; i < g_widgetCacheCount; i++) {
        g_widgetCache[i].pWidget = nullptr;
        g_widgetCache[i].searched = false;
    }
    g_widgetCacheCount = 0;
    g_widgetScanDone = false;

    // Also clear live CXWnd cache (Phase 6)
    ResetLiveWidgetCache();
    DI8Log("mq2_bridge: widget cache reset (definitions + live)");
}

// ─── Live CXWnd discovery via CXWndManager tree walk ──────────
//
// During login, MQ2's GetChildItem fails because eqgame.exe's SIDL
// template table ([0x02063D08]) is NULL. eqmain.dll has its own
// CXWndManager with the live login-screen widgets, but no exports
// for name-based lookup.
//
// Strategy: walk eqmain's CXWnd tree and for each node, check if
// any DWORD in its body matches:
//   A) The address of the widget's DEFINITION (screen-piece object
//      found by HeapScanForWidget). This exploits CSidlScreenWnd's
//      m_pSidlPiece member — a back-pointer to the definition.
//   B) A CXStr buf_base pointer whose string content matches the
//      target widget name. Catches cases where the name is stored
//      directly (not just via SIDL template ID).
//
// Once method A discovers the m_pSidlPiece offset from the first
// successful match, subsequent lookups only check that single offset
// (O(1) per node instead of scanning 128 DWORDs).

struct LiveCacheEntry {
    const char *name;
    void       *pLiveWnd;
    int         nameOffset;
};

static const int LIVE_CACHE_MAX = 16;
static LiveCacheEntry g_liveCache[LIVE_CACHE_MAX] = {};
static int g_liveCacheCount = 0;
static int g_pSidlPieceOffset = -1;       // discovered CXWnd offset for m_pSidlPiece

static bool g_liveDumpDone = false;   // diagnostic dump flag (reset on cache clear)
static int  g_liveNfLog = 0;         // not-found log counter
static bool g_liveVtEnumDone = false; // one-shot heap enum of live-widget vtables

static void ResetLiveWidgetCache() {
    g_liveCacheCount = 0;
    g_pSidlPieceOffset = -1;
    g_liveDumpDone = false;
    g_liveNfLog = 0;
    g_liveVtEnumDone = false;
}

// One-shot heap enumeration: scan ALL committed pages for objects whose
// first DWORD (vtable pointer) matches any of the known live-widget vtable
// RVAs in eqmain.dll {CEditWnd, CEditBaseWnd, CButtonWnd}. Logs total count
// + first-found address per class. NO def-backref filter — pure presence
// check, answers "do real live CEditBaseWnd instances exist on Dalaya?"
//
// Iteration 4 (2026-04-24) diagnostic. Background: iterations 1-3 found
// the def-backref always returning CXMLDataPtr-vtable matches (vt RVA
// 0x10A7D4) which our recon labels as "wrapper". The slot probe on that
// vtable always fails, blocking Combo G. Two competing hypotheses:
//   H1: The CXMLDataPtr-class wrapper IS the actual login widget on
//       Dalaya (RTTI label CXMLDataPtr but class repurposed). If so,
//       we need to discover the right SetWindowText slot or InputText
//       offset for that class — slot 73 is the wrong target.
//   H2: A separate live CEditBaseWnd exists on the heap but doesn't
//       backref the def at any offset our scan reaches.
// This enum disambiguates: count > 0 with live-widget vtables → H2 (find
// + use them); count == 0 → H1 confirmed (probe wrapper slots).
static void EnumerateLiveWidgetVtablesOnce() {
    if (g_liveVtEnumDone) return;
    g_liveVtEnumDone = true;

    HMODULE hEqmain = GetModuleHandleA("eqmain.dll");
    if (!hEqmain) return;
    uintptr_t eqmLo = (uintptr_t)hEqmain;
    uintptr_t eqmHi = eqmLo;
    __try {
        uint32_t e_lfanew = *(const uint32_t *)((const uint8_t *)hEqmain + 0x3C);
        if (e_lfanew < 0x400)
            eqmHi = eqmLo + *(const uint32_t *)((const uint8_t *)hEqmain + e_lfanew + 0x50);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (eqmHi <= eqmLo) return;

    uintptr_t vtCEditWnd     = eqmLo + EQMainOffsets::RVA_VTABLE_CEditWnd;
    uintptr_t vtCEditBaseWnd = eqmLo + EQMainOffsets::RVA_VTABLE_CEditBaseWnd;
    uintptr_t vtCButtonWnd   = eqmLo + EQMainOffsets::RVA_VTABLE_CButtonWnd;
    uintptr_t vtCSidlScreen  = eqmLo + EQMainOffsets::RVA_VTABLE_CSidlScreenWnd;
    uintptr_t vtCXWndBase    = eqmLo + EQMainOffsets::RVA_VTABLE_CXWnd;

    static const int SAMPLE_MAX = 10;
    int countEdit = 0, countEditBase = 0, countButton = 0;
    int countSidlScreen = 0, countXWndBase = 0;
    void *samplesEdit[SAMPLE_MAX]      = {};
    void *samplesEditBase[SAMPLE_MAX]  = {};
    void *samplesButton[SAMPLE_MAX]    = {};
    void *samplesSidlScreen[SAMPLE_MAX] = {};
    void *samplesXWndBase[SAMPLE_MAX]  = {};

    MEMORY_BASIC_INFORMATION mbi;
    uintptr_t addr = 0x00010000;
    int pages = 0;

    while (addr < 0x7FFF0000 && pages < 10000) {
        if (VirtualQuery((void *)addr, &mbi, sizeof(mbi)) == 0) break;
        uintptr_t base = (uintptr_t)mbi.BaseAddress;
        SIZE_T    size = mbi.RegionSize;

        if (mbi.State == MEM_COMMIT &&
            !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) &&
            (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE)) &&
            (mbi.Type == MEM_PRIVATE || mbi.Type == MEM_MAPPED)) {

            pages++;
            __try {
                const uint8_t *p = (const uint8_t *)base;
                for (uintptr_t off = 0; off + 4 <= size; off += 4) {
                    uintptr_t vt = *(const uintptr_t *)(p + off);
                    if (vt == vtCEditWnd) {
                        if (countEdit < SAMPLE_MAX) samplesEdit[countEdit] = (void *)(base + off);
                        countEdit++;
                    } else if (vt == vtCEditBaseWnd) {
                        if (countEditBase < SAMPLE_MAX) samplesEditBase[countEditBase] = (void *)(base + off);
                        countEditBase++;
                    } else if (vt == vtCButtonWnd) {
                        if (countButton < SAMPLE_MAX) samplesButton[countButton] = (void *)(base + off);
                        countButton++;
                    } else if (vt == vtCSidlScreen) {
                        if (countSidlScreen < SAMPLE_MAX) samplesSidlScreen[countSidlScreen] = (void *)(base + off);
                        countSidlScreen++;
                    } else if (vt == vtCXWndBase) {
                        if (countXWndBase < SAMPLE_MAX) samplesXWndBase[countXWndBase] = (void *)(base + off);
                        countXWndBase++;
                    }
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        }
        addr = base + size;
        if (addr <= base) addr = base + 0x1000;
    }

    DI8Log("mq2_bridge: LIVE-WIDGET HEAP ENUM — scanned %d pages", pages);

    auto LogSamples = [](const char *className, uintptr_t vt, uint32_t rva,
                         int count, void **samples, int sampleMax) {
        int n = count < sampleMax ? count : sampleMax;
        DI8Log("mq2_bridge:   %s vt=0x%08X (eqmain+0x%05X) count=%d (showing first %d)",
               className, (unsigned)vt, (unsigned)rva, count, n);
        for (int i = 0; i < n; i++) {
            const char *zone = ((uintptr_t)samples[i] < 0x02000000) ? "LOW (likely vftable-array false-pos)"
                             : ((uintptr_t)samples[i] >= 0x10000000) ? "HIGH (real heap candidate)"
                             : "MID";
            DI8Log("mq2_bridge:     [%d] %p — %s", i, samples[i], zone);
        }
    };
    LogSamples("CEditWnd      ", vtCEditWnd,     EQMainOffsets::RVA_VTABLE_CEditWnd,
               countEdit, samplesEdit, SAMPLE_MAX);
    LogSamples("CEditBaseWnd  ", vtCEditBaseWnd, EQMainOffsets::RVA_VTABLE_CEditBaseWnd,
               countEditBase, samplesEditBase, SAMPLE_MAX);
    LogSamples("CButtonWnd    ", vtCButtonWnd,   EQMainOffsets::RVA_VTABLE_CButtonWnd,
               countButton, samplesButton, SAMPLE_MAX);
    LogSamples("CSidlScreenWnd", vtCSidlScreen,  EQMainOffsets::RVA_VTABLE_CSidlScreenWnd,
               countSidlScreen, samplesSidlScreen, SAMPLE_MAX);
    LogSamples("CXWnd (base)  ", vtCXWndBase,    EQMainOffsets::RVA_VTABLE_CXWnd,
               countXWndBase, samplesXWndBase, SAMPLE_MAX);

    // ─── Iteration 7 (FORWARD scan) ─────────────────────────────
    // For each high-heap live CEditWnd / CButtonWnd, scan its body
    // 0x14..0x800 for DWORDs that point at any def-like vtable
    // (CParamEditbox / CParamButton / CXMLDataPtr). This finds the
    // link between live widget and its XML def, however deep it lives
    // in the live widget's structure. Iterations 1-6 all looked
    // backward (from def to holder); this looks forward (from live
    // widget to its def reference).
    auto ScanLiveBodyForDefRefs = [&](const char *cls, void **samples, int count) {
        for (int i = 0; i < SAMPLE_MAX && i < count; i++) {
            void *we = samples[i];
            if (!we || (uintptr_t)we < 0x02000000) continue; // skip false-pos
            int defLikeRefs = 0;
            DI8Log("mq2_bridge:   FORWARD scan %s[%d]@%p body 0x14..0x800:",
                   cls, i, we);
            __try {
                const uint8_t *eb = (const uint8_t *)we;
                for (int eo = 0x14; eo < 0x800; eo += 4) {
                    if (eo == 0x18) continue;
                    uintptr_t ed = *(const uintptr_t *)(eb + eo);
                    if (ed < 0x10000 || ed > 0x7FFFFFFF) continue;
                    if (!IsReadablePtr((void *)ed, 4)) continue;
                    __try {
                        uintptr_t edvt = *(const uintptr_t *)ed;
                        if (edvt < eqmLo || edvt >= eqmHi) continue;
                        uint32_t edvtRVA = (uint32_t)(edvt - eqmLo);
                        // Def-like vtables (param-class definitions + smart ptrs).
                        // Also report any other eqmain-vtable hit (could be a
                        // related XML data class we haven't catalogued).
                        bool isDefLike =
                            (edvtRVA == 0x10D304 ||  // CParamEditbox
                             edvtRVA == 0x10AA08 ||  // CParamButton
                             edvtRVA == 0x10A7D4);   // CXMLDataPtr
                        if (isDefLike) {
                            DI8Log("mq2_bridge:     +0x%03X = %p -> vt=eqmain+0x%05X DEF-LIKE",
                                   eo, (void *)ed, edvtRVA);
                            defLikeRefs++;
                            if (defLikeRefs >= 8) break;
                        }
                    } __except (EXCEPTION_EXECUTE_HANDLER) {}
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
            if (defLikeRefs == 0) {
                DI8Log("mq2_bridge:     (no def-like vtable refs in body)");
            }
        }
    };
    ScanLiveBodyForDefRefs("CEditWnd  ", samplesEdit,    countEdit);
    ScanLiveBodyForDefRefs("CButtonWnd", samplesButton,  countButton);

    // ─── Iteration 8a: pinstCSidlManager probe ────────────────────
    // CSidlManagerBase RTTI-walked vtable RVA on Dalaya eqmain (per
    // rizin probe 2026-04-24): COL at 0x10115608 in DLL imageBase view,
    // vtable at 0x1010aa40, so vtable RVA = 0x10aa40.
    // pSidlMgr is the singleton — scan eqmain's .data section for any
    // pointer whose deref has this vtable.
    constexpr uint32_t RVA_VTABLE_CSidlManagerBase = 0x10aa40;
    uintptr_t expectedCSidlVt = eqmLo + RVA_VTABLE_CSidlManagerBase;
    DI8Log("mq2_bridge: pinstCSidlManager probe — looking for ptr to obj with vt=0x%08X (eqmain+0x%05X)",
           (unsigned)expectedCSidlVt, RVA_VTABLE_CSidlManagerBase);

    // Locate eqmain's .data section
    uint8_t *eqMainBaseB = (uint8_t *)hEqmain;
    uint8_t *dataBase = nullptr;
    uint32_t dataSize = 0;
    __try {
        if (*(uint16_t *)eqMainBaseB == 0x5A4D) {
            int32_t eLfanew = *(int32_t *)(eqMainBaseB + 0x3C);
            if (eLfanew >= 0x40 && eLfanew <= 0x1000 &&
                *(uint32_t *)(eqMainBaseB + eLfanew) == 0x00004550) {
                uint16_t numSections = *(uint16_t *)(eqMainBaseB + eLfanew + 6);
                uint16_t optSize     = *(uint16_t *)(eqMainBaseB + eLfanew + 20);
                uint8_t *sh = eqMainBaseB + eLfanew + 24 + optSize;
                for (int i = 0; i < numSections && i < 64; i++, sh += 40) {
                    char nm[9] = {};
                    memcpy(nm, sh, 8);
                    if (strcmp(nm, ".data") == 0) {
                        dataBase = eqMainBaseB + *(uint32_t *)(sh + 12);
                        dataSize = *(uint32_t *)(sh + 8);
                        break;
                    }
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}

    int sidlMgrCount = 0;
    void *firstSidlMgr = nullptr;
    uint32_t firstSidlOff = 0;
    if (dataBase && dataSize > 16) {
        __try {
            for (uint32_t off = 0; off + 4 <= dataSize; off += 4) {
                uintptr_t p = *(uintptr_t *)(dataBase + off);
                if (p < 0x10000 || p > 0x7FFFFFFF) continue;
                if (!IsReadablePtr((void *)p, sizeof(void *))) continue;
                __try {
                    uintptr_t pvt = *(uintptr_t *)p;
                    if (pvt != expectedCSidlVt) continue;
                    sidlMgrCount++;
                    if (!firstSidlMgr) {
                        firstSidlMgr = (void *)p;
                        firstSidlOff = off;
                    }
                    if (sidlMgrCount <= 5) {
                        DI8Log("mq2_bridge:   pSidlMgr cand[%d] data+0x%X = %p (vt matches CSidlManagerBase)",
                               sidlMgrCount - 1, off, (void *)p);
                    }
                } __except (EXCEPTION_EXECUTE_HANDLER) {}
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    DI8Log("mq2_bridge: pinstCSidlManager probe — %d candidate(s) total, first at data+0x%X = %p",
           sidlMgrCount, firstSidlOff, firstSidlMgr);

    // ─── Iteration 8b: CXWnd structure probe (XMLIndex + InputText offsets) ───
    // Per agent's MQ2-source port spec:
    //   x64: XMLIndex at CXWnd+0x094 (uint32_t key into CXMLDataManager hash)
    //        InputText at CEditBaseWnd+0x278 (CXStr field on edit widget)
    //   x86: pointers/CXStr handles halve, so estimates:
    //        XMLIndex at ~CXWnd+0x4A..+0x60 (uint32, small integer)
    //        InputText at ~CEditBaseWnd+0x180..+0x1A0 (CXStr — length<200, ASCII data)
    // Probe each high-heap CEditWnd sample for these fields.
    auto ProbeXWndForKnownFields = [&](void *we, int idx) {
        if (!we || (uintptr_t)we < 0x02000000) return;
        DI8Log("mq2_bridge: CXWnd-field probe CEditWnd[%d]@%p:", idx, we);

        // XMLIndex candidates: uint32 small integer (1..9999) at offsets 0x40..0x100
        DI8Log("mq2_bridge:   XMLIndex candidates (uint32 in range 1..9999):");
        int xmlIdxHits = 0;
        __try {
            const uint8_t *eb = (const uint8_t *)we;
            for (int o = 0x40; o < 0x100; o += 4) {
                uint32_t v = *(const uint32_t *)(eb + o);
                if (v >= 1 && v <= 9999) {
                    DI8Log("mq2_bridge:     +0x%02X = %u", o, v);
                    if (++xmlIdxHits >= 6) break;
                }
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (!xmlIdxHits) DI8Log("mq2_bridge:     (no small-int candidates in 0x40..0x100)");

        // InputText CXStr candidates: pointer at offset 0x100..0x300 such that
        //   *(int*)(p+8) is a small length (1..200) AND *(char*)(p+0x14) is ASCII
        DI8Log("mq2_bridge:   CXStr candidates (likely InputText):");
        int cxstrHits = 0;
        __try {
            const uint8_t *eb = (const uint8_t *)we;
            for (int o = 0x100; o < 0x300; o += 4) {
                uintptr_t p = *(const uintptr_t *)(eb + o);
                if (p < 0x10000 || p > 0x7FFFFFFF) continue;
                if (!IsReadablePtr((void *)p, 0x18)) continue;
                __try {
                    int len = *(const int *)(p + 8);
                    if (len < 0 || len > 200) continue;
                    const char *s = (const char *)(p + 0x14);
                    bool ok = true;
                    int useLen = len > 0 ? len : 1;
                    for (int k = 0; k < useLen && k < 32; k++) {
                        char c = s[k];
                        if (c == 0) break;
                        if (c < 0x20 || c > 0x7E) { ok = false; break; }
                    }
                    if (!ok) continue;
                    char preview[40] = {};
                    int prevN = len < 32 ? len : 32;
                    if (prevN > 0) memcpy(preview, s, prevN);
                    DI8Log("mq2_bridge:     +0x%03X = CXStr@%p len=%d data='%s'",
                           o, (void *)p, len, preview);
                    if (++cxstrHits >= 6) break;
                } __except (EXCEPTION_EXECUTE_HANDLER) {}
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (!cxstrHits) DI8Log("mq2_bridge:     (no CXStr-shaped candidates in 0x100..0x300)");
    };

    // Probe first 3 high-heap CEditWnd samples
    int probed = 0;
    void *probedPtrs[3] = {};
    for (int i = 0; i < SAMPLE_MAX && i < countEdit && probed < 3; i++) {
        if (samplesEdit[i] && (uintptr_t)samplesEdit[i] >= 0x02000000) {
            probedPtrs[probed] = samplesEdit[i];
            ProbeXWndForKnownFields(samplesEdit[i], i);
            probed++;
        }
    }

    // ─── Iteration 9: cross-instance diff for XMLIndex discovery ───
    // XMLIndex must be UNIQUE per widget instance. Class-shared fields
    // (flags, defaults) appear at the same value across all instances of
    // the same class — those are NOT XMLIndex. Cross-compare the 3
    // probed CEditWnds; offsets where values differ AND values are small
    // ints (1..9999) are XMLIndex candidates.
    if (probed >= 2 && probedPtrs[0] && probedPtrs[1]) {
        DI8Log("mq2_bridge: cross-instance DIFF (CEditWnd[%p] vs [%p]%s) — looking for XMLIndex (unique per widget):",
               probedPtrs[0], probedPtrs[1], probedPtrs[2] ? " vs [3rd]" : "");
        int diffHits = 0;
        __try {
            const uint8_t *e1 = (const uint8_t *)probedPtrs[0];
            const uint8_t *e2 = (const uint8_t *)probedPtrs[1];
            const uint8_t *e3 = probedPtrs[2] ? (const uint8_t *)probedPtrs[2] : nullptr;
            for (int o = 0x60; o < 0x180; o += 4) {
                uint32_t v1 = *(const uint32_t *)(e1 + o);
                uint32_t v2 = *(const uint32_t *)(e2 + o);
                uint32_t v3 = e3 ? *(const uint32_t *)(e3 + o) : 0;
                // Skip if all identical (class-shared, not XMLIndex)
                if (v1 == v2 && (!e3 || v1 == v3)) continue;
                // Skip if any value looks like a pointer (XMLIndex is uint32, not ptr)
                bool anyPtrLike = (v1 >= 0x10000 && v1 <= 0x7FFFFFFF) ||
                                   (v2 >= 0x10000 && v2 <= 0x7FFFFFFF) ||
                                   (e3 && v3 >= 0x10000 && v3 <= 0x7FFFFFFF);
                if (anyPtrLike) continue;
                DI8Log("mq2_bridge:   +0x%02X DIFFERS: [1]=%u [2]=%u%s%s",
                       o, v1, v2,
                       e3 ? " [3]=" : "",
                       e3 ? (v3 < 100000 ? "small" : "large") : "");
                if (e3) {
                    DI8Log("mq2_bridge:                       (verbose) [1]=0x%X [2]=0x%X [3]=0x%X",
                           v1, v2, v3);
                }
                if (++diffHits >= 12) break;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (!diffHits) DI8Log("mq2_bridge:   (no varying small-int fields in 0x60..0x180)");
    }

    // ─── Iteration 9: pSidlMgr body dump for XMLDataMgr discovery ───
    // Look for the inline CXMLDataManager. Agent estimate: x86 ~+0xD8.
    // We don't know its vtable but we can spot it by structure: it likely
    // has an ArrayClass<CXMLData*> at some offset (count + data ptr).
    // First, find pSidlMgr — re-do the .data scan inline (we already did
    // this above into firstSidlMgr).
    if (firstSidlMgr) {
        DI8Log("mq2_bridge: pSidlMgr body dump @ %p, offsets 0x80..0x200 (looking for inline XMLDataMgr):",
               firstSidlMgr);
        int dumpHits = 0;
        __try {
            const uint8_t *sm = (const uint8_t *)firstSidlMgr;
            for (int o = 0x80; o < 0x200; o += 4) {
                uint32_t v = *(const uint32_t *)(sm + o);
                const char *kind = "scalar";
                bool kindIsScalar = true;
                if (v >= 0x10000 && v <= 0x7FFFFFFF) {
                    if (IsReadablePtr((void *)(uintptr_t)v, 4)) {
                        __try {
                            uintptr_t pvt = *(const uintptr_t *)(uintptr_t)v;
                            if (pvt >= eqmLo && pvt < eqmHi) {
                                uint32_t rva = (uint32_t)(pvt - eqmLo);
                                DI8Log("mq2_bridge:     pSidlMgr+0x%02X = 0x%08X -> ptr to obj with vt=eqmain+0x%05X",
                                       o, v, rva);
                                dumpHits++;
                                continue;
                            }
                        } __except (EXCEPTION_EXECUTE_HANDLER) {}
                        kind = "heap-ptr";
                        kindIsScalar = false;
                    } else {
                        kind = "ptr-unreadable";
                        kindIsScalar = false;
                    }
                }
                if (v != 0 && (v < 100000 || !kindIsScalar)) {
                    DI8Log("mq2_bridge:     pSidlMgr+0x%02X = 0x%08X (%s)", o, v, kind);
                    dumpHits++;
                }
                if (dumpHits >= 30) break;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
}

// Check if a DWORD looks like a CXStr buf_base pointing to target name.
// CXStr buffer layout: +0x08=length, +0x14=string data (buf_base+20).
static bool IsCXStrMatch(uintptr_t val, const char *name, int nameLen) {
    if (val < 0x10000 || val > 0x7FFFFFFF) return false;
    __try {
        int bufLen = *(const int *)(val + 8);
        if (bufLen != nameLen) return false;
        const char *str = (const char *)(val + 20);
        if (str[0] != name[0]) return false;
        for (int i = 0; i < nameLen; i++) {
            if (str[i] != name[i]) return false;
        }
        return str[nameLen] == '\0';
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

struct TreeSearchCtx {
    const char *name;
    int         nameLen;
    void       *defAddr;       // definition address from HeapScan (cross-ref target)
    void       *result;        // output: found live CXWnd
    int         foundOffset;   // output: offset where match was found
    int         nodesChecked;
};

// Walk a CXWnd tree recursively, checking each node for matches.
// Returns true on first match (stored in ctx->result).
static bool WalkTreeSearch(void *pWnd, TreeSearchCtx *ctx, int depth) {
    if (!pWnd || depth > 25 || ctx->nodesChecked > 5000) return false;
    if ((uintptr_t)pWnd < 0x10000 || (uintptr_t)pWnd > 0x7FFFFFFF) return false;

    __try {
        if (!IsReadablePtr(pWnd, 0x400)) return false;

        // Skip definition objects: definitions have 0xFFFFFFFF at +0x10
        uintptr_t atOx10 = *(uintptr_t *)((uintptr_t)pWnd + 0x10);
        if (atOx10 == 0xFFFFFFFF) return false;

        ctx->nodesChecked++;
        const uint8_t *body = (const uint8_t *)pWnd;

        // Fast path: if m_pSidlPiece offset is already known, just check that
        if (g_pSidlPieceOffset > 0 && ctx->defAddr) {
            uintptr_t val = *(const uintptr_t *)(body + g_pSidlPieceOffset);
            if ((void *)val == ctx->defAddr) {
                ctx->result = pWnd;
                ctx->foundOffset = g_pSidlPieceOffset;
                return true;
            }
            // Also try: the offset holds a DIFFERENT definition — read ITS name
            // to support multiple widget lookups with the same offset.
            if (val > 0x10000 && val < 0x7FFFFFFF) {
                __try {
                    uintptr_t defName = *(uintptr_t *)(val + 0x18);
                    if (IsCXStrMatch(defName, ctx->name, ctx->nameLen)) {
                        ctx->result = pWnd;
                        ctx->foundOffset = g_pSidlPieceOffset;
                        return true;
                    }
                } __except (EXCEPTION_EXECUTE_HANDLER) {}
            }
        }

        // Full scan: check every DWORD in first 0x400 bytes
        if (g_pSidlPieceOffset < 0) {
            for (int off = 0x04; off < 0x400; off += 4) {
                if (off == 0x08 || off == 0x10) continue; // skip sibling/child ptrs

                uintptr_t val = *(const uintptr_t *)(body + off);

                // Method A: cross-reference — DWORD == definition address
                if (ctx->defAddr && (void *)val == ctx->defAddr) {
                    ctx->result = pWnd;
                    ctx->foundOffset = off;
                    g_pSidlPieceOffset = off;
                    DI8Log("mq2_bridge: DISCOVERED m_pSidlPiece at CXWnd+0x%X via cross-ref", off);
                    return true;
                }

                // Method B: CXStr scan — DWORD is CXStr buf_base with target name
                if (IsCXStrMatch(val, ctx->name, ctx->nameLen)) {
                    ctx->result = pWnd;
                    ctx->foundOffset = off;
                    return true;
                }

                // Method A fallback: DWORD points to some object whose +0x18
                // is a CXStr with the target name (definition-like structure)
                if (val > 0x10000 && val < 0x7FFFFFFF && IsReadablePtr((void *)val, 0x20)) {
                    __try {
                        uintptr_t innerName = *(uintptr_t *)(val + 0x18);
                        if (IsCXStrMatch(innerName, ctx->name, ctx->nameLen)) {
                            ctx->result = pWnd;
                            ctx->foundOffset = off;
                            g_pSidlPieceOffset = off;
                            DI8Log("mq2_bridge: DISCOVERED m_pSidlPiece at CXWnd+0x%X via name dereference", off);
                            return true;
                        }
                    } __except (EXCEPTION_EXECUTE_HANDLER) {}
                }
            }
        }

        // Recurse into children: firstChild at +0x10
        void *child = (atOx10 > 0x10000 && atOx10 < 0x7FFFFFFF) ? (void *)atOx10 : nullptr;
        void *prev = nullptr;
        while (child) {
            if ((uintptr_t)child < 0x10000 || (uintptr_t)child > 0x7FFFFFFF) break;
            if (child == prev) break; // loop detection
            if (!IsReadablePtr(child, 0x20)) break;

            if (WalkTreeSearch(child, ctx, depth + 1)) return true;

            prev = child;
            __try {
                child = *(void **)((uintptr_t)child + 0x08); // nextSibling
            } __except (EXCEPTION_EXECUTE_HANDLER) { break; }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}

    return false;
}

// Find a live CXWnd by name using eqmain's CXWndManager tree.
// Returns nullptr if not found (does NOT cache negatives — allows retry).
static void *FindLiveCXWnd(const char *name) {
    // Check live cache first
    for (int i = 0; i < g_liveCacheCount; i++) {
        if (g_liveCache[i].name == name || strcmp(g_liveCache[i].name, name) == 0) {
            return g_liveCache[i].pLiveWnd;
        }
    }

    // Need eqmain's CXWndManager
    void *wndMgr = FindEQMainWndMgr();
    if (!wndMgr || !g_eqmainWndMgrOffset) return nullptr;

    // Get definition address for cross-reference (Method A)
    void *defAddr = FindWidgetByHeapScan(name);

    TreeSearchCtx ctx = {};
    ctx.name = name;
    ctx.nameLen = (int)strlen(name);
    ctx.defAddr = defAddr;
    ctx.nodesChecked = 0;

    const uint8_t *pMgr = (const uint8_t *)wndMgr;
    __try {
        const ArrayClassHeader *arr = (const ArrayClassHeader *)(pMgr + g_eqmainWndMgrOffset);
        if (arr->Count < 1 || arr->Count > 500 || !arr->Data) return nullptr;
        if (!IsReadablePtr(arr->Data, arr->Count * 4)) return nullptr;

        void **wndArray = (void **)arr->Data;
        for (int i = 0; i < arr->Count; i++) {
            void *pWnd = wndArray[i];
            if (!pWnd || !IsReadablePtr(pWnd, 0x20)) continue;

            if (WalkTreeSearch(pWnd, &ctx, 0)) {
                DI8Log("mq2_bridge: FindLiveCXWnd('%s') FOUND at %p (offset +0x%X, wnd[%d], %d nodes)",
                       name, ctx.result, ctx.foundOffset, i, ctx.nodesChecked);

                // Cache positive result
                if (g_liveCacheCount < LIVE_CACHE_MAX) {
                    g_liveCache[g_liveCacheCount].name = name;
                    g_liveCache[g_liveCacheCount].pLiveWnd = ctx.result;
                    g_liveCache[g_liveCacheCount].nameOffset = ctx.foundOffset;
                    g_liveCacheCount++;
                }
                return ctx.result;
            }
        }

        // CXWndManager tree walk failed — the login sub-screen CXWnds might
        // be in a different CXWndManager or not in any manager's array.
        // Fallback: full heap scan for ANY object with eqmain vtable that
        // contains the definition address as a DWORD (m_pSidlPiece cross-ref).
        if (defAddr) {
            HMODULE hEqmain = GetModuleHandleA("eqmain.dll");
            uintptr_t eqmLo = (uintptr_t)hEqmain;
            uintptr_t eqmHi = eqmLo;
            __try {
                uint32_t e_lfanew = *(const uint32_t *)((const uint8_t *)hEqmain + 0x3C);
                if (e_lfanew < 0x400)
                    eqmHi = eqmLo + *(const uint32_t *)((const uint8_t *)hEqmain + e_lfanew + 0x50);
            } __except (EXCEPTION_EXECUTE_HANDLER) {}

            if (eqmHi > eqmLo) {
                // Iteration 4 — first run a one-shot heap enumeration of
                // live-widget vtables ({CEditWnd, CEditBaseWnd, CButtonWnd,
                // CSidlScreenWnd, CXWnd}). If count == 0 for all, the
                // CXMLDataPtr-class wrapper (vt RVA 0x10A7D4) IS the actual
                // login widget on Dalaya and Combo G must probe its real
                // SetWindowText slot rather than assume slot 73.
                EnumerateLiveWidgetVtablesOnce();

                // Collect ALL heap-cross-ref matches (up to MAX_CANDS) instead of
                // returning the first one. Iteration 3 finding (2026-04-24): the
                // first match is consistently the CXMLDataPtr wrapper (vtable RVA
                // 0x10A7D4). Iteration 4 dedups by vtable — only the FIRST match
                // per unique vtable is kept, so the 16-slot cap captures up to
                // 16 distinct vtables instead of 16 wrapper-array slide-window
                // duplicates (which all read the same downstream def-pointer).
                //
                // Per MQ2 source (XMLData.h:603-687), MQ2's autologin writes
                // the InputText CXStr field directly on CEditBaseWnd
                // (MQ2AutoLogin.cpp:1039-1051), NOT via vtable SetWindowText.
                // We need the live CEditBaseWnd to do the same.
                //
                // Strategy: enumerate distinct-vtable candidates, log each with
                // vtable, then prefer one whose vtable is in the live-widget
                // set. Fallback to first match (= wrapper) for legacy behavior
                // if no live-vtable candidate (preserves keystroke-fallback
                // path: login_sm needs a non-null widget pointer to proceed).
                struct CrossRefCand {
                    void     *addr;
                    uintptr_t vt;
                    uint32_t  off;
                    void     *sib;
                    void     *child;
                };
                static const int MAX_CANDS = 16;
                CrossRefCand cands[MAX_CANDS] = {};
                int candCount = 0;

                MEMORY_BASIC_INFORMATION mbi;
                uintptr_t addr = 0x00010000;
                int heapPages = 0;
                uintptr_t defVal = (uintptr_t)defAddr;

                while (addr < 0x7FFF0000 && heapPages < 300000 && candCount < MAX_CANDS) {
                    if (VirtualQuery((void *)addr, &mbi, sizeof(mbi)) == 0) break;
                    uintptr_t base = (uintptr_t)mbi.BaseAddress;
                    SIZE_T size = mbi.RegionSize;

                    if (mbi.State == MEM_COMMIT &&
                        !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) &&
                        (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE)) &&
                        (mbi.Type == MEM_PRIVATE || mbi.Type == MEM_MAPPED)) {

                        heapPages++;
                        __try {
                            const uint8_t *p = (const uint8_t *)base;
                            for (uintptr_t off = 0; off + 0x400 <= size && candCount < MAX_CANDS; off += 4) {
                                // Check eqmain vtable at +0x00
                                uintptr_t vt = *(const uintptr_t *)(p + off);
                                if (vt < eqmLo || vt >= eqmHi) continue;
                                uintptr_t vt0 = *(const uintptr_t *)vt;
                                if (vt0 < eqmLo || vt0 >= eqmHi) {
                                    if (vt0 < 0x00400000 || vt0 >= 0x02200000) continue;
                                }

                                // Skip definitions (+0x10 == 0xFFFFFFFF)
                                uintptr_t at10 = *(const uintptr_t *)(p + off + 0x10);
                                if (at10 == 0xFFFFFFFF) continue;

                                // Skip low addresses (stack/TEB, not heap CXWnds)
                                uintptr_t objAddr = base + off;
                                if (objAddr < 0x02000000) continue;

                                // Validate CXWnd structure: +0x08 (sibling) and +0x10 (child)
                                // must be null or valid HEAP pointers to other CXWnds.
                                uintptr_t at08 = *(const uintptr_t *)(p + off + 0x08);
                                if (at08 != 0 && (at08 < 0x10000 || at08 > 0x7FFFFFFF)) continue;
                                if (at10 != 0 && (at10 < 0x10000 || at10 > 0x7FFFFFFF)) continue;

                                // Child at +0x10 must be in heap memory (MEM_PRIVATE),
                                // not in a loaded module (MEM_IMAGE). Catches false positives
                                // where "child" points into DLL code sections.
                                if (at10 != 0) {
                                    MEMORY_BASIC_INFORMATION childMbi;
                                    if (VirtualQuery((void *)at10, &childMbi, sizeof(childMbi)) &&
                                        childMbi.Type == MEM_IMAGE) continue;
                                }
                                // Sibling at +0x08 same check
                                if (at08 != 0) {
                                    MEMORY_BASIC_INFORMATION sibMbi;
                                    if (VirtualQuery((void *)at08, &sibMbi, sizeof(sibMbi)) &&
                                        sibMbi.Type == MEM_IMAGE) continue;
                                }

                                // Iteration 4 — dedup by vtable. Skip if we
                                // already have a candidate with this vtable;
                                // saves 16-slot cap for distinct vtables.
                                bool dupVtable = false;
                                for (int j = 0; j < candCount; j++) {
                                    if (cands[j].vt == vt) { dupVtable = true; break; }
                                }
                                if (dupVtable) continue;

                                // Scan body for backref to def. Two paths:
                                //   DIRECT:   body[off] == defAddr (m_pSidlPiece
                                //             stores CParamEditbox* directly).
                                //   INDIRECT: body[off] is a pointer to a
                                //             CXMLDataPtr instance whose +4
                                //             field == defAddr (m_pSidlPiece is
                                //             a smart-ptr wrapping CParamEditbox).
                                // Iteration 5: indirect path added because
                                // iteration 4 enum proved live CEditWnd/CButtonWnd
                                // exist on heap (8/70 instances) but cross-ref
                                // found 0 live-widget candidates via direct path
                                // alone. Live widgets must hold m_pSidlPiece as
                                // CXMLDataPtr* (matches MQ2 ROF2 source layout).
                                __try {
                                    for (int bo = 0x14; bo < 0x400; bo += 4) {
                                        // Skip +0x18 — same offset as definition's name CXStr
                                        // (high false positive rate from stack variables)
                                        if (bo == 0x18) continue;
                                        uintptr_t dw = *(const uintptr_t *)(p + off + bo);

                                        bool isDirect   = (dw == defVal);
                                        bool isIndirect = false;
                                        if (!isDirect
                                            && dw > 0x10000 && dw < 0x7FFFFFFF) {
                                            __try {
                                                uintptr_t innerVt = *(const uintptr_t *)dw;
                                                if (innerVt == eqmLo + RVA_VTABLE_CXMLDataPtr_Dalaya) {
                                                    uintptr_t inner = *(const uintptr_t *)(dw + 4);
                                                    if (inner == defVal) isIndirect = true;
                                                }
                                            } __except (EXCEPTION_EXECUTE_HANDLER) {}
                                        }

                                        if (!isDirect && !isIndirect) continue;

                                        cands[candCount].addr  = (void *)objAddr;
                                        cands[candCount].vt    = vt;
                                        cands[candCount].off   = (uint32_t)bo;
                                        cands[candCount].sib   = (void *)at08;
                                        cands[candCount].child = (void *)at10;
                                        candCount++;
                                        break; // first matching offset per object is enough
                                    }
                                } __except (EXCEPTION_EXECUTE_HANDLER) {}
                            }
                        } __except (EXCEPTION_EXECUTE_HANDLER) {}
                    }
                    addr = base + size;
                    if (addr <= base) addr = base + 0x1000;
                }

                // ─── Post-scan: log all candidates with vtable RVA + class ────
                DI8Log("mq2_bridge: HEAP CROSS-REF '%s' — %d candidate(s) collected in %d pages (def=%p)",
                       name, candCount, heapPages, defAddr);
                for (int i = 0; i < candCount; i++) {
                    const char *vtClass = EQMainOffsets::GetEQMainWidgetClassName(cands[i].addr);
                    DI8Log("mq2_bridge:   cand[%d] addr=%p vt=0x%08X (eqmain+0x%05X) off=+0x%X "
                           "sib=%p child=%p class=%s",
                           i, cands[i].addr, (unsigned)cands[i].vt,
                           (unsigned)(cands[i].vt - eqmLo), cands[i].off,
                           cands[i].sib, cands[i].child,
                           vtClass ? vtClass : "(unknown)");
                }

                // Prefer first candidate whose vtable is a known live widget class.
                // This is the actual Combo G fix — bypass the CXMLDataPtr wrapper.
                int chosen = -1;
                for (int i = 0; i < candCount; i++) {
                    uintptr_t v = cands[i].vt;
                    if (v == eqmLo + EQMainOffsets::RVA_VTABLE_CEditWnd ||
                        v == eqmLo + EQMainOffsets::RVA_VTABLE_CEditBaseWnd ||
                        v == eqmLo + EQMainOffsets::RVA_VTABLE_CButtonWnd) {
                        chosen = i;
                        DI8Log("mq2_bridge: HEAP CROSS-REF '%s' SELECTED cand[%d] (live-widget vtable)",
                               name, i);
                        break;
                    }
                }
                if (chosen < 0 && candCount > 0) {
                    chosen = 0;
                    DI8Log("mq2_bridge: HEAP CROSS-REF '%s' SELECTED cand[0] (fallback — no live-widget vtable found)",
                           name);
                }

                if (chosen >= 0) {
                    void *result = cands[chosen].addr;
                    int  bo     = (int)cands[chosen].off;
                    DI8Log("mq2_bridge: HEAP CROSS-REF '%s' FOUND at %p (def=%p at +0x%X, %d pages, sib=%p child=%p)",
                           name, result, defAddr, bo, heapPages,
                           cands[chosen].sib, cands[chosen].child);

                    // Iteration 6 diagnostic: scan SELECTED wrapper's body
                    // 0x00..0x100 for DWORDs that point at live-widget-vtable
                    // objects. If found, that's the actual live widget the
                    // wrapper belongs to (transitive: wrapper → live).
                    DI8Log("mq2_bridge:   wrapper-body trans-lookup (scanning %p body 0x00..0x100):", result);
                    int liveBackrefCount = 0;
                    __try {
                        const uint8_t *wb = (const uint8_t *)result;
                        for (int wo = 0x00; wo < 0x100; wo += 4) {
                            uintptr_t wd = *(const uintptr_t *)(wb + wo);
                            if (wd < 0x10000 || wd > 0x7FFFFFFF) continue;
                            if (!IsReadablePtr((void *)wd, 4)) continue;
                            __try {
                                uintptr_t wdvt = *(const uintptr_t *)wd;
                                if (wdvt == eqmLo + EQMainOffsets::RVA_VTABLE_CEditWnd ||
                                    wdvt == eqmLo + EQMainOffsets::RVA_VTABLE_CEditBaseWnd ||
                                    wdvt == eqmLo + EQMainOffsets::RVA_VTABLE_CButtonWnd ||
                                    wdvt == eqmLo + EQMainOffsets::RVA_VTABLE_CSidlScreenWnd ||
                                    wdvt == eqmLo + EQMainOffsets::RVA_VTABLE_CXWnd) {
                                    const char *cls = EQMainOffsets::GetEQMainWidgetClassName((void *)wd);
                                    DI8Log("mq2_bridge:     wrapper+0x%02X = %p -> vt=0x%08X class=%s LIVE-WIDGET BACKREF",
                                           wo, (void *)wd, (unsigned)wdvt, cls ? cls : "?");
                                    liveBackrefCount++;
                                }
                            } __except (EXCEPTION_EXECUTE_HANDLER) {}
                        }
                    } __except (EXCEPTION_EXECUTE_HANDLER) {}
                    DI8Log("mq2_bridge:   wrapper-body trans-lookup: %d live-widget backref(s) found", liveBackrefCount);

                    if (g_pSidlPieceOffset < 0) {
                        g_pSidlPieceOffset = bo;
                        DI8Log("mq2_bridge: DISCOVERED m_pSidlPiece at CXWnd+0x%X via heap cross-ref", bo);
                    }
                    if (g_liveCacheCount < LIVE_CACHE_MAX) {
                        g_liveCache[g_liveCacheCount].name = name;
                        g_liveCache[g_liveCacheCount].pLiveWnd = result;
                        g_liveCache[g_liveCacheCount].nameOffset = bo;
                        g_liveCacheCount++;
                    }
                    return result;
                }

                DI8Log("mq2_bridge: heap cross-ref for '%s' — not found (%d pages, def=%p)", name, heapPages, defAddr);
            }
        }

        // Diagnostic: dump CXStr values from first few CXWnds (resets on cache clear)
        if (!g_liveDumpDone && defAddr) {
            g_liveDumpDone = true;
            DI8Log("mq2_bridge: FindLiveCXWnd('%s') NOT FOUND — dumping CXStr data from tree (def=%p):",
                   name, defAddr);
            int dumped = 0;
            for (int i = 0; i < arr->Count && dumped < 5; i++) {
                void *pWnd = wndArray[i];
                if (!pWnd || !IsReadablePtr(pWnd, 0x200)) continue;

                // Only dump windows with children (likely container/screen)
                uintptr_t fc = *(uintptr_t *)((uintptr_t)pWnd + 0x10);
                if (fc < 0x10000 || fc == 0xFFFFFFFF) continue;

                dumped++;
                DI8Log("mq2_bridge:   === Wnd[%d] @ %p (vt=%p) ===",
                       i, pWnd, *(void **)pWnd);

                // Walk first few children
                void *child = (void *)fc;
                int ci = 0;
                while (child && ci < 8) {
                    if ((uintptr_t)child < 0x10000 || !IsReadablePtr(child, 0x200)) break;
                    const uint8_t *cb = (const uint8_t *)child;
                    DI8Log("mq2_bridge:     child[%d] @ %p (vt=%p):", ci, child, *(void **)child);

                    // Log interesting CXStr values
                    int found = 0;
                    for (int off = 0x04; off < 0x200 && found < 6; off += 4) {
                        if (off == 0x08 || off == 0x10) continue;
                        uintptr_t val = *(const uintptr_t *)(cb + off);
                        if (val < 0x10000 || val > 0x7FFFFFFF) continue;
                        __try {
                            int blen = *(const int *)(val + 8);
                            if (blen < 1 || blen > 200) continue;
                            const char *s = (const char *)(val + 20);
                            if (s[0] < 0x20 || s[0] > 0x7E) continue;
                            // Quick printability check
                            bool ok = true;
                            for (int k = 0; k < blen && k < 50; k++) {
                                if (s[k] == '\0') break;
                                if (s[k] < 0x20 || s[k] > 0x7E) { ok = false; break; }
                            }
                            if (!ok) continue;
                            char preview[52] = {};
                            strncpy(preview, s, 50);
                            DI8Log("mq2_bridge:       +0x%02X: CXStr '%s' (len=%d)", off, preview, blen);
                            found++;
                        } __except (EXCEPTION_EXECUTE_HANDLER) {}
                    }

                    ci++;
                    __try { child = *(void **)((uintptr_t)child + 0x08); }
                    __except (EXCEPTION_EXECUTE_HANDLER) { break; }
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: FindLiveCXWnd SEH");
    }

    if (g_liveNfLog++ < 10) {
        DI8Log("mq2_bridge: FindLiveCXWnd('%s') — not found (def=%p, %d nodes checked)",
               name, defAddr, ctx.nodesChecked);
    }
    return nullptr;
}

// ─── Combo G: definition → live widget translation ────────────
//
// FindLiveCXWnd's heap cross-reference (Method A in WalkTreeSearch) returns
// the FIRST object that contains the definition pointer in its body. On
// Dalaya 2013 eqmain, that "first object" is a `CXMLDataPtr` wrapper
// (vtable RVA 0x10A7D4) — a one-DWORD heap-allocated holder of a CParamXxx*.
// CXMLDataPtr is NOT a live CXWnd; calling SetWindowText / WndNotification
// on it crashes inside the vtable dispatch because the slot offsets have
// no relation to CEditWnd's body layout.
//
// The actual live widget (CEditWnd at vtable RVA 0x10BE6C, CButtonWnd at
// 0x10B53C, CEditBaseWnd at 0x10BCDC) does reference the def — but
// indirectly through a CXMLDataPtr member. So we walk the CXWnd tree
// looking for an object whose vtable matches a live-widget vtable AND
// whose body contains either (a) the def pointer directly, or (b) a
// CXMLDataPtr-wrapped pointer to the def.
//
// Verified 2026-04-24 via rizin RTTI walk on Native/recon/eqmain.dll:
//   COL at 0x10114F48 → TypeDescriptor at 0x10133F34 → name ".?AVCXMLDataPtr@@"
// Recon source: phase4-cxstr-recon.md (Combo G).

static bool IsLiveWidgetVtable(const void *pObj, uintptr_t eqmainBase) {
    if (!pObj || !eqmainBase) return false;
    __try {
        uintptr_t vt = *(const uintptr_t *)pObj;
        return vt == eqmainBase + EQMainOffsets::RVA_VTABLE_CEditWnd
            || vt == eqmainBase + EQMainOffsets::RVA_VTABLE_CEditBaseWnd
            || vt == eqmainBase + EQMainOffsets::RVA_VTABLE_CButtonWnd;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return false;
}

struct DefBackrefCtx {
    void       *defAddr;
    uintptr_t   eqmainBase;
    void       *result;
    int         foundOffset;
    int         nodesChecked;
    const char *name;        // widget name (e.g. "LOGIN_PasswordEdit") for diagnostic log
};

// CXMLDataPtr vtable RVA — declared at file scope (top) so both this
// walker and FindLiveCXWnd's cross-ref scan share the constant.

static bool WalkForDefBackref(void *pWnd, DefBackrefCtx *ctx, int depth) {
    if (!pWnd || depth > 25 || ctx->nodesChecked > 5000) return false;
    if ((uintptr_t)pWnd < 0x10000 || (uintptr_t)pWnd > 0x7FFFFFFF) return false;

    __try {
        // Lowered from 0x400 to 0x100 — leaf widgets (CEditWnd) can be
        // smaller than 0x400 bytes; the per-DWORD body scan below uses the
        // outer __try to catch any over-read into unmapped memory.
        if (!IsReadablePtr(pWnd, 0x100)) return false;
        ctx->nodesChecked++;

        // Read firstChild slot at +0x10. 0xFFFFFFFF here means "no first
        // child" (leaf widget) — DO NOT return false here: leaf widgets
        // are exactly what we're looking for (LOGIN_PasswordEdit etc.
        // have no children). The 0xFFFFFFFF was previously used as a
        // "skip definition objects" filter, but that filter also prunes
        // legitimate leaf live-widgets. Definitions are filtered correctly
        // by the IsLiveWidgetVtable predicate below — that's the right
        // gate. Caught 2026-04-24 smoke test: 523 nodes, 0 matches because
        // every login-screen leaf widget was pruned before predicate ran.
        uintptr_t atOx10 = *(uintptr_t *)((uintptr_t)pWnd + 0x10);

        // BODY SCAN — runs for EVERY node, not just live-widget ones.
        // The vtable check happens AFTER finding a backref, so unrecognized
        // live-widget vtables (e.g. Dalaya-specific subclasses) get logged
        // as REJECTED candidates instead of silently dropped. Previously
        // the vtable filter gated the entire body scan — when Dalaya's
        // live login widgets used a vtable not in {CEditWnd, CEditBaseWnd,
        // CButtonWnd}, the walker reported "523 nodes, 0 matches" with no
        // signal as to which class was actually holding the def.
        // Cost: ~256 reads per node, bounded by the 5000-node walker cap.
        // The body scan has its own __try so SEH from over-reading a
        // small leaf widget doesn't abort recursion into this node's
        // children (only matters for widgets with bodies < 0x400 bytes
        // that still have children — rare but possible).
        bool foundMatch = false;
        int  matchOff   = 0;
        __try {
            const uint8_t *body = (const uint8_t *)pWnd;
            for (int off = 0x04; off < 0x400; off += 4) {
                if (off == 0x08 || off == 0x10) continue; // skip sibling/child ptrs
                uintptr_t val = *(const uintptr_t *)(body + off);

                bool isDirect   = ((void *)val == ctx->defAddr);
                bool isIndirect = false;

                // Indirect backref: body[off] points at a CXMLDataPtr whose
                // m_pSidlPiece (the second DWORD) == def
                if (!isDirect
                    && val > 0x10000 && val < 0x7FFFFFFF
                    && IsReadablePtr((void *)val, 8)) {
                    __try {
                        uintptr_t innerVt = *(uintptr_t *)val;
                        if (innerVt == ctx->eqmainBase + RVA_VTABLE_CXMLDataPtr_Dalaya) {
                            uintptr_t inner = *(uintptr_t *)(val + 4);
                            if ((void *)inner == ctx->defAddr) isIndirect = true;
                        }
                    } __except (EXCEPTION_EXECUTE_HANDLER) {}
                }

                if (!isDirect && !isIndirect) continue;

                // Backref found. Is the holder a live widget we recognize?
                if (IsLiveWidgetVtable(pWnd, ctx->eqmainBase)) {
                    foundMatch = true;
                    matchOff   = off;
                    break;
                }

                // Holder vtable is NOT in the live-widget set. Log it so
                // we can decode the real Dalaya live-widget class via
                // RTTI/COL walk (see handoff for procedure). Rate-limited
                // to 30 entries — the CXMLDataPtr wrappers themselves
                // backref the def and are common, so unbounded would flood.
                static int g_unkVtLogCount = 0;
                if (g_unkVtLogCount < 30) {
                    uintptr_t nodeVt = 0;
                    __try { nodeVt = *(const uintptr_t *)pWnd; }
                    __except (EXCEPTION_EXECUTE_HANDLER) {}
                    DI8Log("mq2_bridge: WalkForDefBackref candidate REJECTED by vtable filter — "
                           "name='%s' node=%p vt=0x%08X (eqmain+0x%05X) off=+0x%X kind=%s def=%p",
                           ctx->name ? ctx->name : "?",
                           pWnd, (unsigned)nodeVt,
                           (unsigned)(nodeVt - ctx->eqmainBase),
                           off, isDirect ? "direct" : "indirect", ctx->defAddr);
                    g_unkVtLogCount++;
                }
                // Continue scanning — multiple backref offsets in one body
                // are possible (CXMLDataPtr def + member backref to itself).
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            // Body over-read SEH'd — fall through to children walk.
        }

        if (foundMatch) {
            ctx->result      = pWnd;
            ctx->foundOffset = matchOff;
            return true;
        }

        // Recurse into children: firstChild at +0x10. 0xFFFFFFFF means no
        // children — skip the child-loop without returning false (the
        // match predicate above has already run for this node).
        bool hasChildren = (atOx10 != 0xFFFFFFFF
                            && atOx10 > 0x10000
                            && atOx10 < 0x7FFFFFFF);
        void *child = hasChildren ? (void *)atOx10 : nullptr;
        void *prev = nullptr;
        while (child) {
            if ((uintptr_t)child < 0x10000 || (uintptr_t)child > 0x7FFFFFFF) break;
            if (child == prev) break; // loop detection
            if (!IsReadablePtr(child, 0x20)) break;

            if (WalkForDefBackref(child, ctx, depth + 1)) return true;

            prev = child;
            __try {
                child = *(void **)((uintptr_t)child + 0x08); // nextSibling
            } __except (EXCEPTION_EXECUTE_HANDLER) { break; }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}

    return false;
}

// Given a definition pointer (CParamEditbox, CParamButton, or any CXMLDataPtr-
// wrapped def), walk the CXWndManager tree and return the first live
// CEditWnd/CButtonWnd/CEditBaseWnd whose body backrefs that def.
//
// Returns nullptr if no live widget references the def — at which point
// caller should NOT use the def directly (it's not a live CXWnd) and
// should fail-loud per the no-regression-to-dinput8 rule.
static void *TranslateDefToLive(const char *name, void *defAddr) {
    if (!defAddr) return nullptr;

    HMODULE hEqmain = GetModuleHandleA("eqmain.dll");
    if (!hEqmain) return nullptr;

    void *wndMgr = FindEQMainWndMgr();
    if (!wndMgr || !g_eqmainWndMgrOffset) return nullptr;

    DefBackrefCtx ctx = {};
    ctx.defAddr     = defAddr;
    ctx.eqmainBase  = (uintptr_t)hEqmain;
    ctx.name        = name;  // for diagnostic log in WalkForDefBackref

    const uint8_t *pMgr = (const uint8_t *)wndMgr;
    __try {
        const ArrayClassHeader *arr = (const ArrayClassHeader *)(pMgr + g_eqmainWndMgrOffset);
        if (arr->Count < 1 || arr->Count > 500 || !arr->Data) return nullptr;
        if (!IsReadablePtr(arr->Data, arr->Count * 4)) return nullptr;

        void **wndArray = (void **)arr->Data;
        for (int i = 0; i < arr->Count; i++) {
            void *pTopWnd = wndArray[i];
            if (!pTopWnd || !IsReadablePtr(pTopWnd, 0x20)) continue;

            if (WalkForDefBackref(pTopWnd, &ctx, 0)) {
                uintptr_t resultVt = 0;
                __try { resultVt = *(uintptr_t *)ctx.result; }
                __except (EXCEPTION_EXECUTE_HANDLER) {}
                DI8Log("mq2_bridge: TranslateDefToLive('%s') def=%p -> live=%p "
                       "(vt=0x%08X, off=+0x%X, %d nodes, top[%d])",
                       name, defAddr, ctx.result,
                       (unsigned)resultVt, ctx.foundOffset, ctx.nodesChecked, i);
                return ctx.result;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}

    DI8Log("mq2_bridge: TranslateDefToLive('%s') — no live widget backrefs def=%p (%d nodes)",
           name, defAddr, ctx.nodesChecked);
    return nullptr;
}

// ─── FindWidgetByLabel ────────────────────────────────────────
// Find a live CXWnd by its VISIBLE LABEL TEXT at CXWnd+0x1A8.
// Used to click the "LOGIN" main menu button to open the login
// sub-screen (where username/password/connect widgets live).
// Only searches top-level children (one level deep) — main menu
// buttons are direct children of the screen CXWnd.

void *MQ2Bridge::FindWidgetByLabel(const char *label) {
    void *wndMgr = FindEQMainWndMgr();
    if (!wndMgr || !g_eqmainWndMgrOffset) return nullptr;

    int labelLen = (int)strlen(label);
    const uint8_t *pMgr = (const uint8_t *)wndMgr;

    __try {
        const ArrayClassHeader *arr = (const ArrayClassHeader *)(pMgr + g_eqmainWndMgrOffset);
        if (arr->Count < 1 || arr->Count > 500 || !arr->Data) return nullptr;
        if (!IsReadablePtr(arr->Data, arr->Count * 4)) return nullptr;

        void **wndArray = (void **)arr->Data;
        for (int i = 0; i < arr->Count; i++) {
            void *pWnd = wndArray[i];
            if (!pWnd || !IsReadablePtr(pWnd, 0x20)) continue;

            // Walk children of this top-level window
            uintptr_t fc;
            __try { fc = *(uintptr_t *)((uintptr_t)pWnd + 0x10); }
            __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
            if (fc < 0x10000 || fc == 0xFFFFFFFF) continue;

            void *child = (void *)fc;
            while (child) {
                if ((uintptr_t)child < 0x10000 || !IsReadablePtr(child, 0x1B0)) break;

                // Check +0x1A8 for label CXStr
                __try {
                    uintptr_t val = *(uintptr_t *)((uintptr_t)child + 0x1A8);
                    if (IsCXStrMatch(val, label, labelLen)) {
                        DI8Log("mq2_bridge: FindWidgetByLabel('%s') FOUND at %p (parent wnd[%d])",
                               label, child, i);
                        return child;
                    }
                } __except (EXCEPTION_EXECUTE_HANDLER) {}

                __try { child = *(void **)((uintptr_t)child + 0x08); }
                __except (EXCEPTION_EXECUTE_HANDLER) { break; }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}

    return nullptr;
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

// ─── Enumerate all windows (diagnostic — production: count only) ──

struct EnumCtx {
    int count;
    int logged;
};

static bool EnumCallback(void *pWnd, void *context) {
    EnumCtx *ctx = (EnumCtx *)context;
    ctx->count++;
    // Log first 15 windows with their text via MQ2's CXStr-based GetWindowText
    if (ctx->logged < 15 && g_fnGetWindowText && g_fnCXStrDtor) {
        char buf[128] = {};
        MQ2Bridge::ReadWindowText(pWnd, buf, sizeof(buf));
        if (buf[0]) {
            DI8Log("mq2_bridge:   wnd[%d] %p text='%s'", ctx->count - 1, pWnd, buf);
            ctx->logged++;
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
    // pinstCCharacterSelect: direct pointer to CCharacterSelect window (bypasses CXWndManager)
    g_pinstCharSelect = (uintptr_t *)GetProcAddress(g_hMQ2, "pinstCCharacterSelect");

    // eqmain.dll is the login screen module — has its own CXWndManager
    g_hEQMain = GetModuleHandleA("eqmain.dll");

    DI8Log("mq2_bridge: gGameState=%p  ppEverQuest=%p  ppWndMgr=%p",
           g_pGameState, g_ppEverQuest, g_ppWndMgr);
    DI8Log("mq2_bridge: pinstCXWndMgr=%p  pinstCEQMainWnd=%p  pinstCCharSelect=%p  eqmain.dll=%p",
           g_pinstWndMgr, g_pinstEQMainWnd, g_pinstCharSelect, g_hEQMain);

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

    // Diagnostic: log runtime values — ALL pinst* need DOUBLE deref
    // pinst = "pointer to instance" — *pinst = storage addr, **pinst = actual object
    if (g_pinstWndMgr) {
        __try {
            uintptr_t storageAddr = *g_pinstWndMgr;
            void *actual = nullptr;
            if (storageAddr && IsReadablePtr((void *)storageAddr, sizeof(void *)))
                actual = *(void **)storageAddr;
            DI8Log("mq2_bridge: pinstCXWndManager -> storage=0x%08X, CXWndManager*=%p",
                   storageAddr, actual);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("mq2_bridge: pinstCXWndManager -> SEH on deref");
        }
    }
    if (g_ppWndMgr) {
        __try {
            void *m_ptr = *g_ppWndMgr;
            void *actual = nullptr;
            if (m_ptr && IsReadablePtr(m_ptr, sizeof(void *)))
                actual = *(void **)m_ptr;
            DI8Log("mq2_bridge: ppWndMgr -> m_ptr=%p, CXWndManager*=%p", m_ptr, actual);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("mq2_bridge: ppWndMgr -> SEH on deref");
        }
    }
    if (g_pinstCharSelect) {
        __try {
            uintptr_t storageAddr = *g_pinstCharSelect;
            void *actual = nullptr;
            if (storageAddr && IsReadablePtr((void *)storageAddr, sizeof(void *)))
                actual = *(void **)storageAddr;
            DI8Log("mq2_bridge: pinstCCharacterSelect -> storage=0x%08X, CCharacterSelect*=%p",
                   storageAddr, actual);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("mq2_bridge: pinstCCharacterSelect -> SEH (expected at login)");
        }
    }

    // Core requirement: gGameState and ppEverQuest for char reading;
    // ppWndMgr + GetChildItem for login UI manipulation
    bool ok = (g_pGameState != nullptr && g_ppEverQuest != nullptr);
    bool loginReady = ((g_ppWndMgr != nullptr || g_pinstWndMgr != nullptr) &&
                       g_fnGetChildItem != nullptr &&
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
    if (!name) return nullptr;

    // v7 Phase 6 — Live CXWnd scan via CXWndManager tree walk.
    // Finds ACTUAL live widgets by walking eqmain's CXWnd tree and
    // matching via definition cross-reference (m_pSidlPiece) or
    // direct CXStr name match. Returns widgets that work with
    // SetEditText/ClickButton (unlike Phase 5 definitions).
    //
    // Combo G fix (2026-04-24): TRY to translate a CXMLDataPtr def to a
    // live CEditWnd via TranslateDefToLive (the new walker). If translation
    // succeeds, return the live widget — Combo G WriteEditTextDirect will
    // work. If translation FAILS, return the def pointer anyway (legacy
    // behavior preserved). WriteEditTextDirect will reject the def via
    // prologue check, login_sm will SetError, and C# falls back to its
    // keystroke path — same as the 3f40e1e baseline (36s end-to-end).
    // We MUST NOT return nullptr here because login_sm treats nullptr as
    // "widget not found" and stays in WaitLoginScreen forever, blocking
    // C# entirely (the regression seen at 19:53 smoke).
    {
        HMODULE hEqmain = GetModuleHandleA("eqmain.dll");
        if (hEqmain) {
            uintptr_t eqmainBase = (uintptr_t)hEqmain;
            void *live = FindLiveCXWnd(name);
            if (live) {
                if (IsLiveWidgetVtable(live, eqmainBase)) {
                    return live;  // already a live CEditWnd/CButtonWnd/CEditBaseWnd — fast path
                }
                // FindLiveCXWnd returned a CXMLDataPtr wrapper (heap-cross-ref
                // path) or a CParamXxx def — NOT the live CEditWnd. The actual
                // live widget's m_pSidlPiece points at the INNER CParamXxx def,
                // not at the wrapper that contains it. So the walker needs the
                // inner def as its needle, not the wrapper. Re-resolve via
                // FindWidgetByHeapScan (cached — essentially free) and try
                // matching against the inner def first; fall back to the
                // wrapper if that fails (covers the case where the live
                // widget happens to hold the wrapper pointer instead).
                //
                // Caught 2026-04-24 smoke #2: walker reported 0 nodes, 0
                // REJECTED candidates because it was hunting the WRAPPER
                // address (10EFD928) — only the wrapper itself contains it
                // in the body, and the wrapper isn't in the walked tree.
                void *innerDef = FindWidgetByHeapScan(name);
                void *real = innerDef ? TranslateDefToLive(name, innerDef) : nullptr;
                if (!real && innerDef != live) {
                    real = TranslateDefToLive(name, live);
                }
                if (real) return real;
                // Translation failed — return the def anyway (legacy behavior).
                // The downstream WriteEditTextDirect / SetEditText path will
                // detect the wrong vtable and SetError, triggering C# fallback.
                return live;
            }
        }
    }

    // v7 Phase 5 — Tier -1: heap scan for widget DEFINITIONS.
    // DISABLED as a return path — definitions cause SEH in SetEditText
    // and ClickButton. FindLiveCXWnd uses HeapScan internally for
    // cross-referencing (Method A), but we must NOT return definitions
    // to callers who will try to operate on them.
    // When eqmain is loaded, FindLiveCXWnd is the ONLY login-phase path.
    // If it returns nullptr, so does FindWindowByName — this lets the
    // LoginStateMachine fall through to the LOGIN button click logic.

    if (!g_fnGetChildItem) {
        if (g_findLogCount < 3) {
            DI8Log("mq2_bridge: FindWindowByName('%s') — GetChildItem=%p, heap scan also failed", name, g_fnGetChildItem);
            g_findLogCount++;
        }
        return nullptr;
    }

    // v7 Phase 4 — Tier 0: use LoginController* from the GiveTime detour.
    // LoginController is NOT a CXWnd — it's a game logic controller. GetChildItem
    // only works on CXWnd subclasses. Instead, scan LoginController's member fields
    // for pointers to CXWnd objects (which WOULD have GetChildItem), and try each.
    // Typical layout: LoginController has m_pLoginScreenWnd, m_pServerSelectWnd etc.
    // at offsets in the first ~0x200 bytes.
    {
        void *loginCtrl = GiveTimeDetour::GetLoginController();
        static int tier0LogCount = 0;
        if (loginCtrl && g_fnGetChildItem) {
            // Scan LoginController fields for CXWnd* pointers, then GetChildItem on each.
            // LoginController is NOT a CXWnd — it's a game logic object. But it has member
            // pointers to CXWnd screen objects (login screen, EULA, server select, etc.).
            // Scan 500 DWORDs (2000 bytes) to cover large objects.
            int cxwndCandidates = 0;
            __try {
                uintptr_t *fields = (uintptr_t *)loginCtrl;
                for (int fi = 0; fi < 500; fi++) { // scan first ~2000 bytes
                    uintptr_t fieldVal = fields[fi];
                    if (fieldVal < 0x10000 || fieldVal > 0x7FFFFFFF) continue;
                    if (!IsReadablePtr((void *)fieldVal, sizeof(void *))) continue;
                    void *vtable = *(void **)fieldVal;
                    if (!vtable || !IsReadablePtr(vtable, sizeof(void *))) continue;
                    cxwndCandidates++;

                    void *child = nullptr;
                    __try {
                        child = g_fnGetChildItem((void *)fieldVal, name);
                    } __except(EXCEPTION_EXECUTE_HANDLER) {
                        continue;
                    }
                    if (child) {
                        if (tier0LogCount < 20) {
                            DI8Log("mq2_bridge: FindWindowByName('%s') — found via LoginController+0x%X -> CXWnd@%p, child@%p",
                                   name, fi * 4, (void *)fieldVal, child);
                            tier0LogCount++;
                        }
                        return child;
                    }
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                if (tier0LogCount < 5) {
                    DI8Log("mq2_bridge: FindWindowByName('%s') — Tier-0 faulted (ctrl=%p, candidates=%d)",
                           name, loginCtrl, cxwndCandidates);
                    tier0LogCount++;
                }
            }
            // Log CXWnd-like fields with their text content (diagnostic — runs once).
            // STEP 2A fix (2026-04-16): tightened CXWnd detection from
            // "fv-readable && *fv-readable" to IsEQMainWidget(fv). The old
            // filter let through any memory whose first 4 bytes formed a
            // readable address — including string buffers and eqmain globals
            // — and ReadWindowText SEH-faulted on them 26x per login. The new
            // filter requires the vtable pointer to live inside eqmain.dll's
            // load range, which is the precise definition of "CXWnd-like".
            static bool tier0Dumped = false;
            if (!tier0Dumped && cxwndCandidates > 0 && g_fnGetWindowText && g_fnCXStrDtor) {
                tier0Dumped = true;
                DI8Log("mq2_bridge: Tier-0 LoginController@%p — dumping %d CXWnd-like fields:",
                       loginCtrl, cxwndCandidates);
                uintptr_t *dumpFields = (uintptr_t *)loginCtrl;
                int dumpIdx = 0;
                int skippedNonEqMain = 0;
                for (int di = 0; di < 500 && dumpIdx < 30; di++) {
                    uintptr_t fv = dumpFields[di];
                    if (fv < 0x10000 || fv > 0x7FFFFFFF) continue;
                    if (!IsReadablePtr((void *)fv, sizeof(void *))) continue;
                    void *vt = *(void **)fv;
                    if (!vt || !IsReadablePtr(vt, sizeof(void *))) continue;
                    // Only dump widgets whose vtable lives inside eqmain.dll.
                    // Filters out string buffers, module bases, and non-CXWnd
                    // structs that happened to pass the readability check.
                    if (!EQMainOffsets::IsEQMainWidget((void *)fv)) {
                        skippedNonEqMain++;
                        continue;
                    }
                    char textBuf[128] = {};
                    __try { MQ2Bridge::ReadWindowText((void *)fv, textBuf, sizeof(textBuf)); }
                    __except(EXCEPTION_EXECUTE_HANDLER) { textBuf[0] = '\0'; }
                    DI8Log("mq2_bridge:   field[%d] +0x%X = %p (vt=%p) text='%s'",
                           dumpIdx, di * 4, (void *)fv, vt, textBuf);
                    dumpIdx++;
                }
                if (skippedNonEqMain > 0) {
                    DI8Log("mq2_bridge: Tier-0 dump skipped %d non-eqmain fields (string buffers/globals)",
                           skippedNonEqMain);
                }
            }
            if (tier0LogCount < 5) {
                DI8Log("mq2_bridge: FindWindowByName('%s') — Tier-0 scanned LoginController@%p, %d CXWnd-like fields, none had target",
                       name, loginCtrl, cxwndCandidates);
                tier0LogCount++;
            }
        } else if (tier0LogCount < 3) {
            DI8Log("mq2_bridge: FindWindowByName('%s') — Tier-0 skipped (loginCtrl=%p, GetChildItem=%p)",
                   name, loginCtrl, g_fnGetChildItem);
            tier0LogCount++;
        }
    }

    // Tier 0b (DISABLED): heap widget scan — found CXWnd candidates but offset
    // identification is unreliable (+0x100 is not SIDL name). Needs dedicated
    // CE session to map CXWnd struct layout for Dalaya ROF2.
    // Research: string "LOGIN_UsernameEdit" exists on heap during login.
    // CXWnd candidates found near 0x019Fxxxx with vtable ~0x72A1xxxx.

    // Tier 0c: scan eqmain.dll globals near pLoginController (RVA 0x150174)
    // for CXWnd* pointers. The login screen CXWnd is likely stored as a
    // global variable adjacent to pLoginController in eqmain's .data section.
    {
        HMODULE hEqmain = GetModuleHandleA("eqmain.dll");
        if (hEqmain) {
            static int eqmainScanLogCount = 0;
            uintptr_t base = (uintptr_t)hEqmain;
            // Scan 256 DWORDs (1024 bytes) centered on pLoginController RVA
            uintptr_t scanStart = base + 0x150174 - 512;
            uintptr_t scanEnd = base + 0x150174 + 512;
            for (uintptr_t addr = scanStart; addr < scanEnd; addr += 4) {
                __try {
                    if (!IsReadablePtr((void *)addr, 4)) continue;
                    uintptr_t val = *(uintptr_t *)addr;
                    if (val < 0x10000 || val > 0x7FFFFFFF) continue;
                    if (!IsReadablePtr((void *)val, sizeof(void *))) continue;
                    // Check if val looks like a CXWnd (valid vtable in code range)
                    void *vt = *(void **)val;
                    if (!vt || !IsReadablePtr(vt, sizeof(void *))) continue;
                    // Must be in a code section (not heap) to be a real vtable
                    if ((uintptr_t)vt < 0x60000000 || (uintptr_t)vt > 0x80000000) continue;
                    // Try GetChildItem
                    void *child = nullptr;
                    __try {
                        child = g_fnGetChildItem((void *)val, name);
                    } __except(EXCEPTION_EXECUTE_HANDLER) { continue; }
                    if (child) {
                        if (eqmainScanLogCount < 20) {
                            DI8Log("mq2_bridge: FindWindowByName('%s') — found via eqmain global at RVA 0x%X (CXWnd@%p, vt=%p), child@%p",
                                   name, (unsigned)(addr - base), (void *)val, vt, child);
                            eqmainScanLogCount++;
                        }
                        return child;
                    }
                } __except(EXCEPTION_EXECUTE_HANDLER) { continue; }
            }
        }
    }

    // Tier 0c: try pinstCEQMainWnd — the "main" EQ window during login.
    // MQ2AutoLogin's state machine gets its m_currentWindow from MQ2's login
    // state sensor, which resolves to this window during the login phase.
    // Double-deref: *pinst = storage addr, *storage = CEQMainWnd*.
    if (g_pinstEQMainWnd) {
        __try {
            uintptr_t storageAddr = *g_pinstEQMainWnd;
            if (storageAddr && IsReadablePtr((void *)storageAddr, sizeof(void *))) {
                void *pMainWnd = *(void **)storageAddr;
                if (pMainWnd && IsReadablePtr(pMainWnd, sizeof(void *))) {
                    void *child = g_fnGetChildItem(pMainWnd, name);
                    if (child) {
                        static int mainWndLogCount = 0;
                        if (mainWndLogCount < 20) {
                            DI8Log("mq2_bridge: FindWindowByName('%s') — found via pinstCEQMainWnd@%p, child@%p",
                                   name, pMainWnd, child);
                            mainWndLogCount++;
                        }
                        return child;
                    }
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            // CEQMainWnd not valid at this game state
        }
    }

    // Tier 1: try pinstCCharacterSelect directly for charselect widgets.
    // This bypasses CXWndManager iteration entirely — most reliable path.
    // pinstCCharacterSelect is a double-deref: *pinst = storage addr, *storage = CCharacterSelect*.
    if (g_pinstCharSelect) {
        __try {
            uintptr_t storageAddr = *g_pinstCharSelect;  // deref 1: storage address
            if (storageAddr && IsReadablePtr((void *)storageAddr, sizeof(void *))) {
                void *pCharSelWnd = *(void **)storageAddr;  // deref 2: actual window

                // v7 Phase 4: log null→non-null transition so we can tell if
                // pinstCCharacterSelect populates at charselect time on Dalaya.
                static volatile void *lastObserved = nullptr;
                if (pCharSelWnd != lastObserved) {
                    DI8Log("mq2_bridge: pinstCCharacterSelect transition: %p -> %p",
                           (void *)lastObserved, pCharSelWnd);
                    lastObserved = pCharSelWnd;
                }

                if (pCharSelWnd && IsReadablePtr(pCharSelWnd, sizeof(void *))) {
                    void *vtable = *(void **)pCharSelWnd;
                    if (vtable && IsReadablePtr(vtable, sizeof(void *))) {
                        // Log vtable change (informational — SEH protects against crashes)
                        static volatile bool vtableWarned = false;
                        if ((uintptr_t)vtable != CHARSELECT_EXPECTED_VTABLE && !vtableWarned) {
                            DI8Log("mq2_bridge: NOTE — CCharacterSelect vtable 0x%08X (expected 0x%08X, delta=%+d)",
                                   (uintptr_t)vtable, CHARSELECT_EXPECTED_VTABLE,
                                   (int)((uintptr_t)vtable - CHARSELECT_EXPECTED_VTABLE));
                            vtableWarned = true;
                        }
                        // Try GetChildItem regardless — SEH handles wrong object type
                        void *child = g_fnGetChildItem(pCharSelWnd, name);
                        if (child) {
                            DI8Log("mq2_bridge: FindWindowByName('%s') — found via pinstCCharacterSelect at %p",
                                   name, child);
                            return child;
                        }
                    }
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            // pinstCCharacterSelect not valid at this game state, fall through
        }
    }

    // Standard path: iterate all CXWndManager windows
    FindByNameCtx ctx = { name, nullptr };
    bool iterated = IterateAllWindows(FindByNameCallback, &ctx);

    if (g_findLogCount < 3 && !ctx.result) {
        DI8Log("mq2_bridge: FindWindowByName('%s') — iterated=%d, result=%p",
               name, iterated, ctx.result);
        g_findLogCount++;
    }

    return ctx.result;
}

// ─── MQ2Bridge::SetEditText ────────────────────────────────────

void MQ2Bridge::SetEditText(void *pEditWnd, const char *text) {
    if (!g_fnCXStrCtor || !g_fnCXStrDtor || !pEditWnd || !text) return;

    // Step 2B: route through eqmain-side slot 73 (vtable+0x124) when pWnd is
    // a real CEditWnd/CEditBaseWnd. Exact-vtable gate because Phase 5 heap-
    // scan returns CXMLDataPtr definition pointers that live inside eqmain's
    // range but have wrong slot layout — slot 73 in CXMLDataPtr's vtable is
    // an unrelated method and corrupts state when called with SetWindowText's
    // thiscall signature (stack imbalance crash in the earlier 0x128 attempt).
    //
    // Dispatch table (SetEditText):
    //   A. pWnd is a real CEditWnd  → eqmain-side slot 73 (correct layout)
    //   B. pWnd is CXMLDataPtr/def  → log-once + no-op; keyboard injection
    //                                  fallback drives input
    //   C. pWnd is a known non-edit widget class but eqmain fn unavailable
    //       → SEH-wrapped eqgame call as Step 2A fallback
    const char *widgetClass = EQMainOffsets::GetEQMainWidgetClassName(pEditWnd);
    if (!widgetClass) {
        // Not a known widget class — almost certainly a Phase 5 definition
        // pointer. Log at low volume (once per unique pWnd we see) so the
        // upstream widget-enumeration bug stays visible without flooding.
        static void *s_loggedDefs[16] = {};
        static int s_loggedCount = 0;
        bool seen = false;
        for (int i = 0; i < s_loggedCount; i++) {
            if (s_loggedDefs[i] == pEditWnd) { seen = true; break; }
        }
        if (!seen && s_loggedCount < 16) {
            s_loggedDefs[s_loggedCount++] = pEditWnd;
            DI8Log("mq2_bridge: SetEditText skipped — pWnd=%p not a known widget class "
                   "(likely CXMLDataPtr definition from heap-scan; keyboard injection "
                   "will drive input instead)", pEditWnd);
        }
        return;
    }

    EQMainOffsets::FN_SetWindowText fnEqmain = EQMainOffsets::GetSetWindowTextFor(pEditWnd);
    if (fnEqmain) {
        __try {
            uint8_t cxstrBuf[16] = {}; // CXStr is 16 bytes inline
            g_fnCXStrCtor(cxstrBuf, text);
            fnEqmain(pEditWnd, cxstrBuf);
            g_fnCXStrDtor(cxstrBuf);
            return;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("mq2_bridge: SEH in SetEditText native path (pWnd=%p class=%s fn=%p)",
                   pEditWnd, widgetClass, fnEqmain);
            // Fall through to eqgame-side as last resort.
        }
    }

    // Fallback to eqgame-side for classes outside GetSetWindowTextFor's
    // narrow allow-list, or if the native path SEH'd.
    if (!g_fnSetWindowText) return;
    __try {
        uint8_t cxstrBuf[16] = {};
        g_fnCXStrCtor(cxstrBuf, text);
        g_fnSetWindowText(pEditWnd, cxstrBuf);
        g_fnCXStrDtor(cxstrBuf);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH in SetEditText fallback (pWnd=%p class=%s)",
               pEditWnd, widgetClass);
    }
}

// ─── MQ2Bridge::ClickButton ───────────────────────────────────

void MQ2Bridge::ClickButton(void *pButton) {
    if (!pButton) return;

    // Step 2B: route through eqmain-side slot 34 (vtable+0x88, WndNotification)
    // with exact-vtable class gate. XWM_LCLICK=1 mirrors MQ2AutoLogin's click
    // delivery pattern. CButtonWnd inherits WndNotification from CXWnd's
    // real-body implementation; the dispatcher handles msg routing internally.
    const char *widgetClass = EQMainOffsets::GetEQMainWidgetClassName(pButton);
    if (!widgetClass) {
        static void *s_loggedDefs[16] = {};
        static int s_loggedCount = 0;
        bool seen = false;
        for (int i = 0; i < s_loggedCount; i++) {
            if (s_loggedDefs[i] == pButton) { seen = true; break; }
        }
        if (!seen && s_loggedCount < 16) {
            s_loggedDefs[s_loggedCount++] = pButton;
            DI8Log("mq2_bridge: ClickButton skipped — pButton=%p not a known widget class "
                   "(likely CXMLDataPtr definition; keyboard injection fallback will drive click)",
                   pButton);
        }
        return;
    }

    EQMainOffsets::FN_WndNotification fnEqmain = EQMainOffsets::GetWndNotificationFor(pButton);
    if (fnEqmain) {
        __try {
            fnEqmain(pButton, pButton, 1, nullptr);
            return;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("mq2_bridge: SEH in ClickButton native path (pWnd=%p class=%s fn=%p)",
                   pButton, widgetClass, fnEqmain);
        }
    }

    if (!g_fnWndNotification) return;
    __try {
        g_fnWndNotification(pButton, pButton, 1, nullptr);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH in ClickButton fallback (pWnd=%p class=%s)",
               pButton, widgetClass);
    }
}

// ─── MQ2Bridge::ReadWindowText ─────────────────────────────────

void MQ2Bridge::ReadWindowText(void *pWnd, char *outBuf, int bufSize) {
    if (!outBuf || bufSize <= 0) return;
    outBuf[0] = '\0';

    if (!g_fnGetWindowText || !g_fnCXStrDtor || !pWnd) return;

    // STEP 2A diagnostic: GetWindowTextA is another eqgame-side function
    // that SEHs on eqmain-owned widgets. Expected 27 SEHs/login per log.
    bool isEqMain = EQMainOffsets::IsEQMainWidget(pWnd);

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
        DI8Log("mq2_bridge: SEH in ReadWindowText (pWnd=%p isEqMain=%d)",
               pWnd, isEqMain ? 1 : 0);
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

            // Hotfix v6f: tighten name charset to letters only (EQ naming rule) so
            // a field-label string or garbage bytes from a mis-aligned entry can't
            // be emitted as "name" to the user. Also enforces min length 1; empty
            // names are written as empty strings (consumer handles the skip).
            int nameLen = 0;
            while (nameLen < LOGIN_NAME_LEN - 1 && name[nameLen] != '\0') {
                char c = name[nameLen];
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) break;
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

// ─── Verification Report ──────────────────────────────────────
// One-shot comprehensive dump of all pointer chains when charselect
// first succeeds. Replaces scattered diagnostic logging.

static void EmitVerificationReport(volatile CharSelectShm *shm) {
    if (g_verificationDone) return;
    g_verificationDone = true;

    DI8Log("=== VERIFICATION REPORT (charselect) ===");

    // 1. pinstCCharacterSelect chain
    if (g_pinstCharSelect) {
        __try {
            uintptr_t storage = *g_pinstCharSelect;
            void *actual = nullptr;
            if (storage && IsReadablePtr((void *)storage, sizeof(void *)))
                actual = *(void **)storage;
            DI8Log("  pinstCCharacterSelect: export=%p -> storage=0x%08X -> CCharacterSelect*=%p",
                   g_pinstCharSelect, storage, actual);
            if (actual) {
                void *vtable = *(void **)actual;
                DI8Log("    vtable=%p", vtable);
                // Try GetChildItem("Character_List") on it
                if (g_fnGetChildItem) {
                    void *charList = g_fnGetChildItem(actual, "Character_List");
                    DI8Log("    GetChildItem('Character_List')=%p", charList);
                    if (charList && g_fnGetCurSel) {
                        int curSel = g_fnGetCurSel(charList);
                        DI8Log("    Character_List.GetCurSel()=%d", curSel);
                    }
                }
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("  pinstCCharacterSelect: SEH on deref chain");
        }
    } else {
        DI8Log("  pinstCCharacterSelect: NOT RESOLVED");
    }

    // 2. pinstCXWndManager chain
    if (g_pinstWndMgr) {
        __try {
            uintptr_t storage = *g_pinstWndMgr;
            void *actual = nullptr;
            if (storage && IsReadablePtr((void *)storage, sizeof(void *)))
                actual = *(void **)storage;
            DI8Log("  pinstCXWndManager: export=%p -> storage=0x%08X -> CXWndManager*=%p",
                   g_pinstWndMgr, storage, actual);
            if (actual && g_wndMgrOffsetFound) {
                const ArrayClassHeader *arr = (const ArrayClassHeader *)((uint8_t *)actual + g_wndMgrValidOffset);
                DI8Log("    pWindows at offset 0x%X: Count=%d Alloc=%d", g_wndMgrValidOffset, arr->Count, arr->Alloc);
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("  pinstCXWndManager: SEH on deref chain");
        }
    } else {
        DI8Log("  pinstCXWndManager: NOT RESOLVED");
    }

    // 3. ppEverQuest + charSelectPlayerArray
    if (g_ppEverQuest) {
        __try {
            void *pEQ = *g_ppEverQuest;
            DI8Log("  ppEverQuest: export=%p -> CEverQuest*=%p", g_ppEverQuest, pEQ);
            if (pEQ && g_offsetValidated) {
                const ArrayClassHeader *arr = (const ArrayClassHeader *)((uint8_t *)pEQ + g_validatedOffset);
                DI8Log("    charSelectPlayerArray at offset 0x%X: Count=%d", g_validatedOffset, arr->Count);
                if (arr->Count > 0 && arr->Data) {
                    const char *firstName = (const char *)(arr->Data + CSI_NAME_OFF);
                    char nameBuf[64] = {};
                    int len = 0;
                    while (len < 63 && firstName[len] >= 0x20 && firstName[len] <= 0x7E) len++;
                    memcpy(nameBuf, firstName, len);
                    DI8Log("    first char: '%s'", nameBuf);
                }
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("  ppEverQuest: SEH");
        }
    }

    // 4. SHM state
    DI8Log("  SHM: charCount=%d selectedIndex=%d mq2Available=%d gameState=%d",
           shm->charCount, shm->selectedIndex, shm->mq2Available, shm->gameState);

    DI8Log("=== END VERIFICATION REPORT ===");
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

    // Handle Enter World request — gated against gameState=5 (in-game).
    // Dalaya ROF2 uses gameState=0 at BOTH login and charselect, so we can't
    // gate on charselect via gameState. But gameState=5 reliably means in-game,
    // and CXWndManager keeps CLW_EnterWorldButton alive even after charselect
    // closes — so without this gate, a request that arrives just after the user
    // manually pressed Enter would phantom-click in-game.
    //
    // Result codes (read by C# AutoLoginManager — must keep in sync):
    //   1  = clicked successfully
    //  -1  = button not found
    //  -2  = dropped (in-game when request arrived; success-equivalent)
    //  -3  = bridge unavailable (g_fnWndNotification null)
    {
        uint32_t ewReq = shm->enterWorldReq;
        uint32_t ewAck = shm->enterWorldAck;

        if (ewReq != ewAck) {
            DI8Log("mq2_bridge: Enter World request (seq %u->%u, gameState=%d)", ewAck, ewReq, gameState);
            if (gameState == 5 || gameState == -99) {
                // Already in-game OR could not read game state (SEH fallback).
                // Either way, default-safe: drop the request to avoid phantom-
                // clicking Enter World while the player is actually in-game
                // (hotfix v3 HIGH-4).
                shm->enterWorldResult = -2;
                MemoryBarrier();
                shm->enterWorldAck = ewReq;
                DI8Log("mq2_bridge: dropped Enter World request (gameState=%d — in-game or unreadable)", gameState);
            } else {
                void *pEnterBtn = FindWindowByName("CLW_EnterWorldButton");
                if (!pEnterBtn) {
                    shm->enterWorldResult = -1;
                    DI8Log("mq2_bridge: CLW_EnterWorldButton not found (gameState=%d)", gameState);
                } else if (!g_fnWndNotification) {
                    shm->enterWorldResult = -3;
                    DI8Log("mq2_bridge: WndNotification fn unresolved -- cannot click");
                } else {
                    __try {
                        g_fnWndNotification(pEnterBtn, pEnterBtn, 1 /*XWM_LCLICK*/, nullptr);
                        shm->enterWorldResult = 1;
                        DI8Log("mq2_bridge: clicked CLW_EnterWorldButton");
                    }
                    __except (EXCEPTION_EXECUTE_HANDLER) {
                        // Hotfix v6c (Agent 2 F2.5, Agent 3 F3.4): disambiguate SEH
                        // from "button not found" (-1). Pre-v6c both cases wrote -1,
                        // so the C# caller retried and then fell back to PulseKey3D
                        // on what may be a faulted UI stack. A distinct -4 lets C#
                        // abort the login cleanly with a user-visible "client faulted"
                        // message instead of spamming Enter into a broken client.
                        shm->enterWorldResult = -4;
                        DI8Log("mq2_bridge: SEH clicking CLW_EnterWorldButton");
                    }
                }
                MemoryBarrier();
                shm->enterWorldAck = ewReq;
            }
        }
    }

    // Handle selection request — also no gameState gate for Dalaya compatibility.
    // The selection handler below (after char data read) checks charCount > 0.

    // Dalaya ROF2: gameState=0 at login AND charselect. We can't gate on
    // gameState==1. Instead, attempt to read character data always — the
    // SEH guards and validation (IsReadablePtr, name checks) handle invalid states.
    // If we're not at charselect, charCount will be 0 and nothing happens.
    if (gameState == 5) {
        // gameState 5 = in-game on Dalaya. Clear char data + reset all charselect caches.
        shm->charCount = 0;
        shm->selectedIndex = -1;
        // Drain any in-flight Enter World request so a later session in the same
        // process can't observe a stale ack/result from this charselect cycle.
        shm->enterWorldReq = 0;
        shm->enterWorldAck = 0;
        shm->enterWorldResult = 0;
        g_offsetValidated = false;
        g_uiFallbackLogged = false;
        g_cachedNameCol = -1;
        g_cachedSlotCount = -1;
        g_heapScanDone = false;
        g_heapScanArrayBase = 0;
        g_standaloneDelay = 0;  // reset for next charselect cycle
        g_verificationDone = false;
        g_charArrayNotFoundLogged = false;
        return;
    }

    // At character select -- read character data
    // Strategy: try charSelectPlayerArray first (struct offsets), then fall back to
    // UI-based reading via Character_List CListWnd (GetItemText).
    bool charDataRead = false;

    // Path A: CEverQuest::charSelectPlayerArray (struct-based, gives level+class)
    void *pEverQuest = nullptr;
    __try { pEverQuest = *g_ppEverQuest; }
    __except (EXCEPTION_EXECUTE_HANDLER) { pEverQuest = nullptr; }

    if (pEverQuest) {
        const uint8_t *pEQ = (const uint8_t *)pEverQuest;
        if (ValidateCharArrayOffset(pEQ)) {
            __try {
                const ArrayClassHeader *arr = (const ArrayClassHeader *)(pEQ + g_validatedOffset);
                int count = arr->Count;
                const uint8_t *data = arr->Data;

                if (count >= 1 && count <= CHARSEL_MAX_CHARS && data) {
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
                    charDataRead = true;
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                DI8Log("mq2_bridge: SEH reading charSelectPlayerArray");
            }
        }
    }

    // Path B: UI-based fallback — read from Character_List CListWnd via GetItemText.
    // MQ2AutoLogin reads column 2 for character name. This works even when
    // charSelectPlayerArray offset is wrong.
    if (!charDataRead && g_fnGetItemText) {
        void *pCharList = FindWindowByName("Character_List");
        if (pCharList) {
            if (!g_uiFallbackLogged) {
                DI8Log("mq2_bridge: charSelectPlayerArray unavailable — using UI fallback (CListWnd at %p)", pCharList);
                g_uiFallbackLogged = true;

                // Check if list has a current selection (proves list has items)
                if (g_fnGetCurSel) {
                    __try {
                        int curSel = g_fnGetCurSel(pCharList);
                        DI8Log("mq2_bridge: Character_List GetCurSel = %d", curSel);
                    } __except(EXCEPTION_EXECUTE_HANDLER) {
                        DI8Log("mq2_bridge: Character_List GetCurSel SEH");
                    }
                }
            }

            // Discover name column — retry every poll until found (don't cache failure)
            // Dalaya ROF2 may use non-standard columns, scan wider range (0-9)
            int nameCol = g_cachedNameCol;
            if (nameCol < 0) {
                for (int tryCol = 0; tryCol <= 9 && nameCol < 0; tryCol++) {
                    char test[CHARSEL_NAME_LEN] = {};
                    if (ReadListItemText(pCharList, 0, tryCol, test, CHARSEL_NAME_LEN) && test[0]) {
                        // EQ names: uppercase start, >= 4 chars, all alpha
                        bool looksLikeName = (test[0] >= 'A' && test[0] <= 'Z') && strlen(test) >= 4;
                        if (looksLikeName) {
                            bool allAlpha = true;
                            for (int k = 0; test[k]; k++) {
                                if (!((test[k] >= 'A' && test[k] <= 'Z') || (test[k] >= 'a' && test[k] <= 'z'))) {
                                    allAlpha = false; break;
                                }
                            }
                            if (allAlpha) {
                                nameCol = tryCol;
                                g_cachedNameCol = nameCol;
                                DI8Log("mq2_bridge: UI fallback: name column = %d (first name: '%s')", tryCol, test);
                            }
                        }
                    }
                }
            }

            int count = 0;
            if (nameCol >= 0) {
                for (int i = 0; i < CHARSEL_MAX_CHARS; i++) {
                    char nameBuf[CHARSEL_NAME_LEN] = {};
                    if (ReadListItemText(pCharList, i, nameCol, nameBuf, CHARSEL_NAME_LEN) && nameBuf[0]) {
                        memcpy((void *)shm->names[i], nameBuf, CHARSEL_NAME_LEN);
                        // Try adjacent columns for level
                        char lvlBuf[16] = {};
                        for (int lc = 0; lc < 6; lc++) {
                            if (lc == nameCol) continue;
                            if (ReadListItemText(pCharList, i, lc, lvlBuf, 16) && lvlBuf[0] >= '0' && lvlBuf[0] <= '9') {
                                shm->levels[i] = atoi(lvlBuf);
                                break;
                            }
                        }
                        shm->classes[i] = 0;
                        count++;
                    } else {
                        break;
                    }
                }
            }

            // Path B2: if GetItemText failed (empty columns) but GetCurSel works,
            // the list HAS items — populate charCount for slot-based selection.
            // Cache result to avoid re-probing every 500ms poll cycle.
            if (count == 0 && nameCol < 0 && g_fnGetCurSel) {
                if (g_cachedSlotCount > 0) {
                    // Use cached probe result — just update selectedIndex
                    count = g_cachedSlotCount;
                    __try {
                        int curSel = g_fnGetCurSel(pCharList);
                        shm->selectedIndex = curSel;
                    } __except(EXCEPTION_EXECUTE_HANDLER) {}
                    // Repopulate slot names (SHM may have been reset)
                    for (int i = 0; i < count; i++) {
                        char slotName[CHARSEL_NAME_LEN];
                        wsprintfA(slotName, "Slot %d", i + 1);
                        memcpy((void *)shm->names[i], slotName, CHARSEL_NAME_LEN);
                        shm->levels[i] = 0;
                        shm->classes[i] = 0;
                    }
                } else if (g_fnSetCurSel) {
                    // First probe — SetCurSel/GetCurSel on each slot to find actual count
                    __try {
                        int curSel = g_fnGetCurSel(pCharList);
                        if (curSel >= 0) {
                            int probeCount = 0;
                            int origSel = curSel;
                            for (int i = 0; i < CHARSEL_MAX_CHARS; i++) {
                                g_fnSetCurSel(pCharList, i);
                                int readBack = g_fnGetCurSel(pCharList);
                                if (readBack == i) {
                                    probeCount = i + 1;
                                } else {
                                    break;
                                }
                            }
                            g_fnSetCurSel(pCharList, origSel);

                            if (probeCount == 0) {
                                DI8Log("mq2_bridge: UI fallback: slot probe inconclusive (curSel=%d), skipping", origSel);
                            } else {
                                count = probeCount;
                                g_cachedSlotCount = probeCount;
                                for (int i = 0; i < count; i++) {
                                    char slotName[CHARSEL_NAME_LEN];
                                    wsprintfA(slotName, "Slot %d", i + 1);
                                    memcpy((void *)shm->names[i], slotName, CHARSEL_NAME_LEN);
                                    shm->levels[i] = 0;
                                    shm->classes[i] = 0;
                                }
                                shm->selectedIndex = origSel;
                                DI8Log("mq2_bridge: UI fallback: slot-based mode — probed %d slots (curSel=%d)",
                                       count, origSel);
                            }
                        }
                    } __except(EXCEPTION_EXECUTE_HANDLER) {
                        DI8Log("mq2_bridge: SEH in GetCurSel fallback");
                    }
                }
            }

            // Path C: heap scan for real character names when slot-based mode is active.
            // Dalaya stores names in a heap array at stride 0x160. One-shot scan per session.
            if (count > 0 && !g_heapScanDone) {
                g_heapScanDone = true;
                uintptr_t arrayBase = HeapScanForCharArray();
                if (arrayBase) {
                    g_heapScanArrayBase = arrayBase;
                    __try {
                        for (int i = 0; i < count && i < CHARSEL_MAX_CHARS; i++) {
                            const uint8_t *entry = (const uint8_t *)(arrayBase + i * HEAP_SCAN_STRIDE);
                            if (!IsPlausibleName(entry)) {
                                // Zero stale data from Path A/B so C# doesn't read mismatched names
                                ((char *)shm->names[i])[0] = '\0';
                                continue;
                            }
                            int nameLen = 0;
                            while (nameLen < CHARSEL_NAME_LEN - 1 && entry[nameLen] != '\0')
                                nameLen++;
                            memcpy((void *)shm->names[i], entry, nameLen);
                            ((char *)shm->names[i])[nameLen] = '\0';
                            // +0x44 confirmed to be RACE (1=Hum, 11=Halfling, etc), NOT class.
                            // Class and level offsets unknown — leave shm fields untouched.
                            int32_t race = *(const int32_t *)(entry + 0x44);
                            DI8Log("mq2_bridge: heap scan: slot %d = \"%s\" race=%d (cls/lvl unknown)",
                                   i, (const char *)shm->names[i], race);
                        }
                    } __except(EXCEPTION_EXECUTE_HANDLER) {
                        DI8Log("mq2_bridge: SEH reading heap-scanned char array");
                        g_heapScanArrayBase = 0;
                    }
                }
            }
            // On subsequent polls, re-read names from cached heap array (names may update).
            // Re-validate each entry so a stale cache (heap reuse) doesn't silently feed garbage.
            else if (count > 0 && g_heapScanArrayBase) {
                __try {
                    int validated = 0;
                    for (int i = 0; i < count && i < CHARSEL_MAX_CHARS; i++) {
                        const uint8_t *entry = (const uint8_t *)(g_heapScanArrayBase + i * HEAP_SCAN_STRIDE);
                        if (!IsPlausibleName(entry)) {
                            ((char *)shm->names[i])[0] = '\0';
                            continue;
                        }
                        validated++;
                        int nameLen = 0;
                        while (nameLen < CHARSEL_NAME_LEN - 1 && entry[nameLen] != '\0')
                            nameLen++;
                        memcpy((void *)shm->names[i], entry, nameLen);
                        ((char *)shm->names[i])[nameLen] = '\0';
                        // class/level offsets unknown — don't touch shm fields
                    }
                    // Invalidate aggressively: any failure (not just all-zero) suggests heap reuse.
                    // Reset g_heapScanDone so the next poll triggers a fresh full scan, not just
                    // a cached re-read against a (now-zero) base address.
                    if (validated < count) {
                        DI8Log("mq2_bridge: heap cache stale (%d/%d names valid) -- rescanning next poll",
                               validated, count);
                        g_heapScanArrayBase = 0;
                        g_heapScanDone = false;
                    }
                } __except(EXCEPTION_EXECUTE_HANDLER) {
                    DI8Log("mq2_bridge: SEH re-reading heap array -- rescanning next poll");
                    g_heapScanArrayBase = 0;
                    g_heapScanDone = false;
                }
            }

            if (count > 0) {
                MemoryBarrier();
                shm->charCount = count;
                for (int i = count; i < CHARSEL_MAX_CHARS; i++) {
                    ((char *)shm->names[i])[0] = '\0';
                    shm->levels[i] = 0;
                    shm->classes[i] = 0;
                }
                charDataRead = true;
            }
        }
    }

    // v7 Phase 4: if Path A (charSelectPlayerArray) and Path B (Character_List)
    // both failed, run the heap scan directly. The heap scan finds character names
    // by pattern-matching in committed pages — works even when MQ2 exports and
    // CXWndManager are both broken on Dalaya.
    // Delay: wait 20 poll cycles (~10 seconds) before scanning. Early scans hit
    // eqmain UI labels ("Height", "MinVSize") instead of character names because
    // charselect hasn't loaded its data yet.
    if (!charDataRead && !g_heapScanDone) {
        if (g_standaloneDelay < 20) {
            if (g_standaloneDelay == 0 || g_standaloneDelay == 10 || g_standaloneDelay == 19)
                DI8Log("mq2_bridge: standalone delay %d/20 (heapScanDone=%d)", g_standaloneDelay, (int)g_heapScanDone);
            g_standaloneDelay++;
            // fall through to charDataRead=false → charCount=0
        } else {
            g_heapScanDone = true;
            uintptr_t arrayBase = HeapScanForCharArray();
            if (arrayBase) {
                g_heapScanArrayBase = arrayBase;
                int count = 0;
                __try {
                    for (int i = 0; i < CHARSEL_MAX_CHARS; i++) {
                        const uint8_t *entry = (const uint8_t *)(arrayBase + i * HEAP_SCAN_STRIDE);
                        if (!IsPlausibleName(entry)) break;
                        int nameLen = 0;
                        while (nameLen < CHARSEL_NAME_LEN - 1 && entry[nameLen] != '\0')
                            nameLen++;
                        memcpy((void *)shm->names[i], entry, nameLen);
                        ((char *)shm->names[i])[nameLen] = '\0';
                        shm->levels[i] = 0;
                        shm->classes[i] = 0;
                        count++;
                        DI8Log("mq2_bridge: heap scan (standalone): slot %d = \"%s\"",
                               i, (const char *)shm->names[i]);
                    }
                } __except(EXCEPTION_EXECUTE_HANDLER) {
                    DI8Log("mq2_bridge: SEH in standalone heap scan");
                    g_heapScanArrayBase = 0;
                }
                if (count > 0) {
                    MemoryBarrier();
                    shm->charCount = count;
                    for (int i = count; i < CHARSEL_MAX_CHARS; i++) {
                        ((char *)shm->names[i])[0] = '\0';
                        shm->levels[i] = 0;
                        shm->classes[i] = 0;
                    }
                    charDataRead = true;
                    DI8Log("mq2_bridge: heap scan populated %d characters (Path A+B both failed)", count);
                }
            }
        }
    }
    // On subsequent polls, re-read names from heap cache (same as existing Path C logic)
    else if (!charDataRead && g_heapScanArrayBase) {
        int count = 0;
        __try {
            for (int i = 0; i < CHARSEL_MAX_CHARS; i++) {
                const uint8_t *entry = (const uint8_t *)(g_heapScanArrayBase + i * HEAP_SCAN_STRIDE);
                if (!IsPlausibleName(entry)) break;
                int nameLen = 0;
                while (nameLen < CHARSEL_NAME_LEN - 1 && entry[nameLen] != '\0')
                    nameLen++;
                memcpy((void *)shm->names[i], entry, nameLen);
                ((char *)shm->names[i])[nameLen] = '\0';
                shm->levels[i] = 0;
                shm->classes[i] = 0;
                count++;
            }
        } __except(EXCEPTION_EXECUTE_HANDLER) {
            g_heapScanArrayBase = 0;
            g_heapScanDone = false;
        }
        if (count > 0) {
            MemoryBarrier();
            shm->charCount = count;
            charDataRead = true;
        } else {
            // Cache stale, rescan next poll
            g_heapScanArrayBase = 0;
            g_heapScanDone = false;
        }
    }

    if (!charDataRead) {
        shm->charCount = 0;
        shm->selectedIndex = -1;
    }

    // One-shot verification report on first successful charselect load
    if (charDataRead && !g_verificationDone)
        EmitVerificationReport(shm);

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
                    shm->ackSeq = reqSeq;  // ack ONLY on successful SetCurSel
                    DI8Log("mq2_bridge: selected character index %d (\"%s\")",
                           requestedIdx, (const char *)shm->names[requestedIdx]);
                }
                __except (EXCEPTION_EXECUTE_HANDLER) {
                    DI8Log("mq2_bridge: SEH in SetCurSel(%d)", requestedIdx);
                }
            } else {
                DI8Log("mq2_bridge: selection DEFERRED — Character_List=%p SetCurSel=%p",
                       pCharListWnd, g_fnSetCurSel);
                // Don't ack — C# will retry on next poll
            }
        } else {
            // Invalid index — ack to prevent infinite retry
            DI8Log("mq2_bridge: selection SKIPPED — index=%d charCount=%d",
                   requestedIdx, shm->charCount);
            shm->ackSeq = reqSeq;
        }
    }

}

// ─── MQ2Bridge::Shutdown ───────────────────────────────────────

void MQ2Bridge::Shutdown() {
    DI8Log("mq2_bridge: Shutdown -- nullifying pointers");

    g_pGameState       = nullptr;
    g_ppEverQuest      = nullptr;
    g_ppWndMgr         = nullptr;
    g_pinstWndMgr      = nullptr;
    g_pinstCharSelect  = nullptr;
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
    g_wndMgrValidOffset = 0;
    g_eqmainWndMgrOffset = 0;
    g_uiFallbackLogged = false;
    g_cachedNameCol    = -1;
    g_verificationDone = false;
    g_findLogCount     = 0;

    // Hotfix v4: reset slot cache + heap-scan base so a mid-process MQ2 re-init
    // doesn't serve a stale count from the previous session's charselect.
    g_cachedSlotCount = -1;
    g_heapScanArrayBase = 0;
}
