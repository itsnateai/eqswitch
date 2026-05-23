// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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
#include "device_proxy.h"  // v3.22.32: GetEqHwnd() for pump-responsiveness probe

// ─── Forward declarations ──────────────────────────────────────

void DI8Log(const char *fmt, ...);

// Track B v3 (2026-05-05): pull target char name from LoginShm for anchor-scan
// fallback (single-char accounts where threshold-5 heap scan can't satisfy).
// `g_loginShm` lives in eqswitch-di8.cpp; declared `volatile` because the C#
// host writes it from a different thread. Read-only access from this TU.
extern volatile LoginShm* g_loginShm;

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

// MQ2 pinst deref semantics (post-v3.22.35 audit, see CHANGELOG):
// GetProcAddress returns the ADDRESS of MQ2's exported variable in MQ2's data
// section. Standard MQ2 publishes pinst exports as `T** pinstX = (T**)(eqgame_base + RVA)`,
// so two derefs are needed to reach the heap T*:
//   *g_pinst  -> storage address in eqgame.exe (eqgame_base + RVA)
//   **g_pinst -> the actual T* heap object  (== *(T**)(*g_pinst))
// The g_pinstWndMgr/g_pinstCharSelect call sites already do this correctly via
// the `storageAddr = *g_pinst; actual = *(void**)storageAddr` idiom — see lines
// 944/2688/2711/2794/3069/3497. The g_ppEverQuest Path A sites historically
// did only one deref (the "Path A unreachable on Dalaya" bug); fixed in
// v3.22.35 via the DerefEverQuestPointer() helper defined after IsReadablePtr.
static HMODULE        g_hMQ2            = nullptr;
static volatile int  *g_pGameState      = nullptr;
static void         **g_ppEverQuest     = nullptr;    // CEverQuest** — 2 derefs to reach CEverQuest* (use DerefEverQuestPointer())
static void         **g_ppWndMgr        = nullptr;    // ppWndMgr: triple-ptr (&ForeignPointer.m_ptr)
static uintptr_t     *g_pinstWndMgr     = nullptr;    // pinstCXWndManager — 2 derefs to reach CXWndManager*
static uintptr_t     *g_pinstEQMainWnd  = nullptr;    // pinstCEQMainWnd — 2 derefs to reach CEQMainWnd*
static uintptr_t     *g_pinstCharSelect = nullptr;    // pinstCCharacterSelect — 2 derefs to reach CCharacterSelect*
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

// v3.22.34 — DALAYA-SPECIFIC offset, confirmed via live ReadProcessMemory
// probe against in-game gotquiz (10 chars) + gotquiz1 (1 char) clients
// 2026-05-23. The prior value 0x18EC0 came from `macroquest-rof2-emu`
// (x64 modern build — wrong tree per CLAUDE.md "do NOT mix the trees"
// rule). Canonical x86 RoF2-Test (mq2emu-rof2-x86/MQ2Main/EQData(Test).h:
// 4839) listed 0x38E80, but Dalaya's EVERQUEST struct is shifted 0x14
// bytes earlier — the actual `pCharSelectPlayerArray` field (an
// `ArrayClass_RO<CSINFO>` header) begins at 0x38E6C on Dalaya. Verified
// by:
//   * gotquiz (PID 40300): +0x38E6C reads 10 (= chars), +0x38E70 reads
//     0x12A5A900 (matches heap-scan-found array address exactly)
//   * gotquiz1 (PID 38608): +0x38E6C reads 1 (= 1 char "Natedogg"),
//     +0x38E70 reads 0x06644AC8 (first slot's CSINFO begins with
//     null-terminated "Natedogg")
// Probe script: tools/probe-charselect-offset.py
static const uint32_t OFFSET_CHARSELECT_ARRAY = 0x38E6C;
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
// v3.22.2: g_offsetValidated / g_validatedOffset / g_charArrayNotFoundLogged
// removed along with the orphaned ValidateCharArrayOffset / IsValidCharArray
// machinery — production paths trust OFFSET_CHARSELECT_ARRAY directly per
// MQ2 RoF2-emu (x86) EverQuest.h:963.
static volatile bool     g_uiFallbackLogged  = false;
// v3.22.3: P8 first-entry plausibility gate diagnostic. One-shot per charselect
// cycle (reset alongside g_uiFallbackLogged) so logs show "gate fired during
// the population window" once instead of every poll. Fires-forever in logs
// after the one-shot = OFFSET_CHARSELECT_ARRAY is wrong (e.g., EQ patch moved
// the array) and Path A is permanently bailing out.
static volatile bool     g_p8GateLogged      = false;
// v3.22.4: P9 publisher plausibility gate diagnostic. Mirror of P8 for the
// Path B+B2+C combined publisher (single `if (count > 0) { shm->charCount =
// count; ... }` at end of Poll's charselect block — the only writer of
// shm->charCount for the UI-derived paths). Catches the case where Path A
// bailed (P8 fired), Path B's GetItemText returned empty, Path B2 synthesized
// "Slot %d" placeholder names into shm->names[], and Path C/anchor scans
// haven't populated real names yet. Pre-v3.22.4 the publisher fired anyway,
// surfacing placeholders to C# (charCount > 0 short-circuits the char-list
// wait loop in AutoLoginManager.cs:1460), which then bailed to Error with
// "MQ2 heap in slot-mode". Closes the 2026-05-17 multi-char-account regression
// (10-slot gotquiz account stuck at char-select while single-char gotquiz1
// succeeded — wider settle window let Path B2 win the publisher race).
static volatile bool     g_p9GateLogged      = false;
// v3.22.5: distinguishes "SEH inside P9 IsPlausibleName predicate" from the
// non-SEH "placeholder or empty" path. Without this latch the empty __except
// fell through to the else-if log below describing the wrong condition, which
// misled DebugView readers any time the SHM access itself faulted (DLL detach
// race, MMF unmapped). One-shot per cycle, mirrors g_p9GateLogged lifecycle.
static volatile bool     g_p9SehLogged       = false;
// v3.22.22: partial-population gate diagnostic. The P8 gate validates only
// entry[0]; observed 2026-05-20 PID 30192 smoke that EQ can set
// charSelectPlayerArray.Count to its final value (10) BEFORE writing all
// per-entry name bytes — at 262ms post-CharSelect entries[0..4] held real
// names, entries[5..9] were zero. Pre-v3.22.22 the loop wrote 5 real names +
// 5 empty strings and still published charCount=10, leaving C# autologin to
// fail "character not found" on a missing slot. The gate now checks every
// entry before publishing; partial pops bail to next-poll retry. Logged
// once per charselect cycle so a slow-populating account is diagnosable
// without log spam. Mirrors g_p8GateLogged lifecycle (reset at charselect
// transition + gameState=5 + Shutdown).
static volatile bool     g_partialPopLogged  = false;
static volatile int      g_cachedNameCol     = -1;
static volatile int      g_cachedSlotCount   = -1;  // slot probe result cache (-1 = not probed)
// v3.22.16: rate-limit timestamp for column-discovery failure logging.
// The v3.22.10 closeout's "P9/P8/uiFallback latch back-out gap" was exactly the
// silent retry-every-poll-no-log pattern. Now logs once per 5s per charselect
// cycle when column discovery fails so we get visibility instead of silence
// until the 30s SM abort fires. Reset at charselect transitions.
static volatile DWORD    g_lastColDiscoveryFailMs = 0;
static volatile bool     g_verificationDone  = false;
static volatile uintptr_t g_heapScanArrayBase = 0;   // heap scan result (0 = not found/not scanned)
static volatile bool     g_heapScanDone      = false; // one-shot per charselect session
// v3.22.32: heap-scan-derived char count. Separate from Path B/B2's `count`
// local (which dies between polls). Cached here so subsequent polls can seed
// their local count from the prior poll's heap scan result, enabling the
// re-read + P9 publish paths to fire every poll without needing Path B2's
// SetCurSel probe to succeed. Closes the 2026-05-22 PID 4628 stall: probe
// hung on a background-client EQ pump (DX idle, no foreground render cycle),
// count stayed 0 forever, Path C never ran, 30s SM timeout aborted autologin.
// Reset lifecycle mirrors g_heapScanArrayBase (charselect transition + gs=5).
static volatile int      g_heapScanCount     = 0;
// v3.22.33 Gap 4 (T2 Sonnet+Opus MEDIUM): promoted from function-local-static
// inside the pump probe block to file scope. The 5-sec rate-limiter on the
// LOUD "EQ pump non-responsive" log was lexically scoped, so the limiter
// could span charselect-transition boundaries and suppress the first
// pump-non-responsive log line of a fresh charselect cycle. Promoting here
// lets the transition + gs=5 reset blocks clear it, restoring the changelog's
// "LOUD log surfaces non-responsive state instead of silent fallthrough"
// diagnostic-visibility claim for back-to-back cycles.
static volatile DWORD    g_lastPumpProbeWarnMs = 0;
// v3.15.2 (2026-05-05) chunked-resume scan position. When budget fires before the array
// is found, save the next region base and resume there next poll instead of restarting at
// 0x01000000. After 2-3 polls the full address space (~0x7E000000) is covered exactly
// once, vs. the prior behavior of pounding the same low region forever on fragmented
// heaps. Reset to 0 on: full-walk-no-find, found, transition, Init(), and cache-stale.
// Anchor scan tracks its own resume cursor + the name it was scanning for; a name change
// restarts at 0x01000000 (different target = different match locations).
static volatile uintptr_t g_lastHeapScanAddr   = 0;
static volatile uintptr_t g_lastAnchorScanAddr = 0;
static char               g_lastAnchorScanName[16] = {0};
// Track B v3 (2026-05-05, T2 dual red-team v6 callout): distinguishes anchor-scan
// cache (single entry, slot 0) from full-array cache (real array, real curSel).
// In the re-read branch, anchor mode requires selectedIndex=0 each poll because
// Path B2's GetCurSel write would otherwise drift selectedIndex away from 0.
static volatile bool     g_anchorScanCached  = false;
static int               g_standaloneDelay   = 0;    // delay standalone heap scan by N poll cycles

// v3 latch defense-in-depth (2026-05-05, R2 verifier callout): consecutive
// pinstCCharacterSelect-null poll counter. Promoted to TU scope (was block
// static) so the gameState==5 reset path can clear it across charselect
// cycles — otherwise a session that left it mid-count would carry the count
// into the next cycle and could spuriously trip the latch-clear threshold.
// Capped at 100 to avoid wraparound + log spam. Threshold check is exact-30
// (one-shot log per session), idempotent re-clears below the cap.
static volatile uint32_t g_consecutiveNullPolls = 0;

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
    // Track B v2/v3 (2026-05-05): UI-label additions per gap-audit verifiers,
    // plus EQ class names + player race names which appear as plausible-name
    // strings in eqgame's class/race description tables (live strings, not
    // the field labels). Without these blocked, the heap scan picks the
    // class-list (Cleric, Druid, ...) or race-list (Human, Halfling, ...)
    // arrays as false positives. T2 Opus v3 verifier callout.
    static const char *const kBadNames[] = {
        // UI field labels
        "Height", "Heading", "Class", "Level", "Name", "Race", "Deity",
        "Gender", "Strength", "Stamina", "Charisma", "Dexterity",
        "Agility", "Intelligence", "Wisdom", "Account", "Character",
        "Login", "Server", "World", "Select", "Options", "Default",
        "Username", "Password", "Settings", "Network", "Inventory",
        "Public", "Console", "Argument", "Target", "Inspect", "Trading",
        // EQ class names (Dalaya/RoF2 set)
        "Bard", "Cleric", "Druid", "Enchanter", "Magician", "Monk",
        "Necromancer", "Paladin", "Ranger", "Rogue", "Shaman", "Warrior",
        "Wizard", "Beastlord", "Berserker", "Shadowknight",
        // EQ player race names
        "Human", "Barbarian", "Erudite", "Woodelf", "Highelf", "Darkelf",
        "Halfelf", "Dwarf", "Troll", "Ogre", "Halfling", "Gnome",
        "Iksar", "Vahshir", "Froglok", "Drakkin",
        // EQ-flavor short title-case strings present in zone/server/chat
        // string tables (T2 Sonnet v3 callout).
        "Zone", "Camp", "Raid", "Brave", "Bold", "Storm", "Swift",
        "Hunter", "Shadow", "Rider", "Scout", "Valor", "Pride",
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
    // v3.15.2 (2026-05-05): chunked-resume. Pick up where the prior budget-aborted
    // scan stopped. After 2-3 polls the full 0x01000000..0x7FFF0000 range is covered
    // exactly once. On fragmented heaps where any single 1500ms slice can't reach
    // the array, the prior behavior was to restart at 0x01000000 every poll and
    // never make forward progress. Cleared on found / full-walk-no-find / transition.
    const uintptr_t startAddr = g_lastHeapScanAddr ? g_lastHeapScanAddr : 0x01000000;
    uintptr_t addr = startAddr;
    int regionsScanned = 0;
    int pagesScanned = 0;
    // Track B v2 (2026-05-05): wall-clock budget. Without this, a fragmented heap
    // (many small MEM_FREE regions) could keep the scan walking VirtualQuery for
    // multiple seconds, blocking the bridge poll thread and starving Tick().
    // T2 Sonnet/Opus verifier callout — `regionsScanned < 200000` cap was misleading
    // because each iteration was a region (could be 2GB), not a 4KB page.
    const DWORD scanStartMs = GetTickCount();
    const DWORD scanBudgetMs = 1500;  // hard ceiling; observed normal scans 200-400ms

    if (startAddr != 0x01000000)
        DI8Log("mq2_bridge: heap scan: resuming from 0x%08X (chunked-resume)", startAddr);

    while (addr < 0x7FFF0000 && regionsScanned < 200000) {
        // Wall-clock abort takes precedence over region cap
        if (GetTickCount() - scanStartMs > scanBudgetMs) {
            DI8Log("mq2_bridge: heap scan: time budget exceeded (%ums, %d regions, %d pages, stopped at 0x%08X) — chunked-resume next poll",
                   scanBudgetMs, regionsScanned, pagesScanned, addr);
            // Track B v3 (2026-05-05, T3 Opus v3 HIGH callout): callers (Path C
            // line ~3496, standalone line ~3587) set g_heapScanDone=true BEFORE
            // calling us. On budget-abort, that gate would permanently lock all
            // future scans in this charselect cycle (only cleared on transition).
            // Clear it ourselves so next poll retries — at worst this means
            // repeated 1.5s scans on a pathologically fragmented heap, but at
            // best it gives the array a chance once swap pressure drops.
            g_heapScanDone = false;
            // v3.15.2: persist resume cursor so next poll picks up from here
            // instead of re-scanning addresses we already cleared.
            g_lastHeapScanAddr = addr;
            return 0;
        }
        if (VirtualQuery((void *)addr, &mbi, sizeof(mbi)) == 0) break;

        uintptr_t base = (uintptr_t)mbi.BaseAddress;
        SIZE_T size = mbi.RegionSize;

        regionsScanned++;
        if (mbi.State == MEM_COMMIT &&
            !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) &&
            (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE))) {

            // Track actual page count (4KB) for honest accounting in the
            // not-found log line. Track B v2 (2026-05-05).
            pagesScanned += (int)(size / 0x1000);
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
                        // Track B fix (2026-05-05): also require entry[0].race ∈ [1, 600].
                        // Without this, Dalaya's race-table at-stride-0x160 wins the scan
                        // (its names pass IsPlausibleName: "Dracnid","Wyvern","Pegasus"...
                        // and race-table entries' +0x44 holds a heap pointer, value > 1e8).
                        // Player CharSelectInfo has +0x44 as race int. Canonical RoF2 enum:
                        // 1..16 (base races), 128 Iksar, 130 VahShir, 330 Froglok, 522 Drakkin.
                        // Bound 600 covers all known player races while still rejecting heap
                        // pointers (millions+) seen in race-table entries.
                        int32_t entryRace = *(const int32_t *)(p + i + 0x44);
                        if (entryRace < 1 || entryRace > 600) continue;
                        // Check if entries at stride 0x160 also have plausible names AND
                        // valid race values. Race-table also has +0x44 huge values, so this
                        // strengthens validation beyond just name-pattern.
                        int validCount = 1;
                        for (int s = 1; s < 10; s++) {
                            if (!IsPlausibleName(p + i + s * HEAP_SCAN_STRIDE)) break;
                            int32_t nextRace = *(const int32_t *)(p + i + s * HEAP_SCAN_STRIDE + 0x44);
                            if (nextRace < 1 || nextRace > 600) break;
                            validCount++;
                        }
                        if (validCount >= 5) {
                            // Track B fix: kept threshold at 5 (race-filter alone isn't enough
                            // to disambiguate single-entry false positives — login-screen
                            // strings like "Public"/"Console" pass name+race when the +0x44
                            // field happens to fall in [1,200]). Single-char accounts
                            // (gotquiz1: Natedogg) won't be discovered by heap scan; they
                            // need a separate path (CListWnd row-array or anchor-by-known-name).
                            // The race-byte filter now blocks the multi-entry race-table
                            // false positive (Dalaya's race table happens to have 7 consecutive
                            // name-pattern entries at stride 0x160 with +0x44 = heap pointer).
                            uintptr_t arrayAddr = chunk + i;
                            DI8Log("mq2_bridge: heap scan FOUND char array at 0x%08X (%d/%d names valid, race-filtered)",
                                   arrayAddr, validCount, 10);
                            // v3.15.2: reset resume cursor on success — next time we scan,
                            // it'll be a new charselect cycle that needs a fresh full search.
                            g_lastHeapScanAddr = 0;
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

    DI8Log("mq2_bridge: heap scan: no char array found (%d regions, %d pages, %ums, full-walk from 0x%08X)",
           regionsScanned, pagesScanned, GetTickCount() - scanStartMs, startAddr);
    // v3.15.2: full address space walked (within budget) without finding the array.
    // Reset the resume cursor so any future cycle starts cleanly. g_heapScanDone is
    // left as caller set it (true) — caller's "don't retry until cycle reset" semantic
    // remains intact.
    g_lastHeapScanAddr = 0;
    return 0;
}

// ─── Anchor-scan for single-char accounts ─────────────────────
// Track B v3 (2026-05-05): when slot-probe = 1 and the threshold-5 heap scan
// fails (Natedogg / single-char accounts), do a name-anchored scan.
// We KNOW the target name (from LoginShm.character — what AutoLoginManager
// is autologin-ing as), so search for that exact null-terminated byte
// sequence in committed pages. For each match, verify the surrounding bytes
// look like a CharSelectInfo entry (race byte at +0x44 in [1,600]). This
// yields a genuine entry without needing 5+ consecutive name patterns.
//
// Safety: the target name must come from LoginShm (trusted source written
// by C# autologin path with config-listed value). We never use an arbitrary
// name from heap. False-positive risk: only if the same name string exists
// elsewhere in heap with a coincidental [1,600] DWORD at +0x44 — vanishingly
// rare for a fresh char-select state.
static uintptr_t HeapScanForTargetName(const char *targetName) {
    if (!targetName || !targetName[0]) return 0;

    // Snapshot length once; targetName is from LoginShm and could in principle
    // be raced by the C# writer, but for our purposes the snapshot is fine.
    int targetLen = 0;
    while (targetLen < 32 && targetName[targetLen] != '\0') targetLen++;
    if (targetLen < 4 || targetLen > 15) return 0;  // EQ name length bounds

    // v3.15.2 (2026-05-05): chunked-resume. Same pattern as HeapScanForCharArray.
    // Additional wrinkle: if the target name changed since the last attempt
    // (different account / different highlighted slot), restart at 0x01000000 —
    // the prior cursor is stale because we're hunting different bytes now.
    bool nameMatchesPrior = (strncmp(g_lastAnchorScanName, targetName, sizeof(g_lastAnchorScanName) - 1) == 0);
    const uintptr_t startAddr = (nameMatchesPrior && g_lastAnchorScanAddr) ? g_lastAnchorScanAddr : 0x01000000;
    if (!nameMatchesPrior) {
        // Save the new target so subsequent budget-abort resumes share a cursor.
        size_t nlen = (size_t)targetLen < sizeof(g_lastAnchorScanName) - 1
                          ? (size_t)targetLen
                          : sizeof(g_lastAnchorScanName) - 1;
        memcpy(g_lastAnchorScanName, targetName, nlen);
        g_lastAnchorScanName[nlen] = '\0';
    }
    MEMORY_BASIC_INFORMATION mbi;
    uintptr_t addr = startAddr;
    int regionsScanned = 0;
    const DWORD scanStartMs = GetTickCount();
    const DWORD scanBudgetMs = 1500;

    if (startAddr != 0x01000000)
        DI8Log("mq2_bridge: anchor scan: resuming from 0x%08X for '%s' (chunked-resume)", startAddr, targetName);

    while (addr < 0x7FFF0000 && regionsScanned < 200000) {
        if (GetTickCount() - scanStartMs > scanBudgetMs) {
            DI8Log("mq2_bridge: anchor scan: budget exceeded for '%s' (%ums, stopped at 0x%08X) — chunked-resume next poll",
                   targetName, scanBudgetMs, addr);
            // R-final verifier callout (T2-S #4): mirror HeapScanForCharArray's
            // budget-abort behavior. Callers (Path C line ~3680, standalone
            // line ~3870) set g_heapScanDone=true BEFORE calling us. On
            // budget-abort without this clear, the gate locks all future
            // anchor scans in this charselect cycle (only cleared on pinst
            // transition). For single-char accounts on a fragmented heap that
            // means we miss every chance the heap state opens up after the
            // first attempt. Clear it ourselves so next poll retries.
            g_heapScanDone = false;
            // v3.15.2: persist resume cursor so next poll picks up here.
            g_lastAnchorScanAddr = addr;
            return 0;
        }
        if (VirtualQuery((void *)addr, &mbi, sizeof(mbi)) == 0) break;
        uintptr_t base = (uintptr_t)mbi.BaseAddress;
        SIZE_T size = mbi.RegionSize;
        regionsScanned++;

        if (mbi.State == MEM_COMMIT &&
            !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) &&
            (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE))) {

            // Scan in 64KB chunks. Need at least targetLen+1 (null) + 0x48
            // bytes to validate +0x44 race byte after the name.
            for (uintptr_t off = 0; off < size; off += 0x10000) {
                uintptr_t chunk = base + off;
                SIZE_T chunkSize = (size - off < 0x10000) ? (size - off) : 0x10000;
                const SIZE_T MIN_TAIL = 0x48;
                if (chunkSize < (SIZE_T)(targetLen + 1) + MIN_TAIL) continue;

                __try {
                    const uint8_t *p = (const uint8_t *)chunk;
                    SIZE_T limit = chunkSize - (SIZE_T)(targetLen + 1) - MIN_TAIL;
                    // 4-byte aligned scan — CharSelectInfo entries start aligned
                    for (SIZE_T i = 0; i <= limit; i += 4) {
                        if (p[i] != (uint8_t)targetName[0]) continue;
                        if (memcmp(p + i, targetName, (size_t)targetLen) != 0) continue;
                        if (p[i + targetLen] != 0) continue;  // require null-term
                        // Validate +0x44 race byte
                        int32_t race = *(const int32_t *)(p + i + 0x44);
                        if (race < 1 || race > 600) continue;
                        // Strong match: name + null-term + valid race byte
                        uintptr_t entryAddr = chunk + i;
                        DI8Log("mq2_bridge: anchor scan FOUND '%s' at 0x%08X (race=%d, %ums)",
                               targetName, entryAddr, race, GetTickCount() - scanStartMs);
                        // v3.15.2: reset resume cursor on success.
                        g_lastAnchorScanAddr = 0;
                        return entryAddr;
                    }
                } __except(EXCEPTION_EXECUTE_HANDLER) {
                    // Page became unreadable mid-scan, skip
                }
            }
        }

        addr = base + size;
        if (addr <= base) addr = base + 0x1000;
    }

    DI8Log("mq2_bridge: anchor scan: '%s' not found (%d regions, %ums, full-walk from 0x%08X)",
           targetName, regionsScanned, GetTickCount() - scanStartMs, startAddr);
    // v3.15.2: full address space walked without finding. Reset resume cursor.
    g_lastAnchorScanAddr = 0;
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
// v3.22.34 — corrected field order to match MQ2 `CDynamicArrayBase` +
// `ArrayClass_RO` actual layout per `mq2emu-rof2-x86/MQ2Main/ArrayClass.h`:
//   class CDynamicArrayBase { /*+0x00*/ int m_length; };
//   class ArrayClass_RO<T> : public CDynamicArrayBase {
//       /*+0x04*/ T* m_array;
//       /*+0x08*/ int m_alloc;
//       /*+0x0c*/ bool m_isValid;
//   };
//
// Prior order `{Data, Count, Alloc}` was wrong — it would have read
// m_length as Data (massive int interpreted as ptr) and m_array as
// Count (heap address interpreted as count). The reason Path A's bug
// presented as "Count=0" rather than crashing/garbage was that
// OFFSET_CHARSELECT_ARRAY was ALSO wrong (0x18EC0 came from the x64
// tree; that address holds zeros on Dalaya x86) — so both bugs masked
// each other. Live ReadProcessMemory probe via tools/probe-charselect-
// offset.py 2026-05-23 confirmed:
//   * +0x00 (m_length): 10 for gotquiz, 1 for gotquiz1 ← real count
//   * +0x04 (m_array):  heap ptr to CSINFO[Count] @ stride 0x160
//   * +0x08 (m_alloc):  10 (slots allocated, EQ default)
//   * +0x0c (m_isValid): 1
struct ArrayClassHeader {
    int      Count;     // m_length
    uint8_t *Data;       // m_array
    int      Alloc;      // m_alloc
    // m_isValid (bool, +0x0c) intentionally omitted — code reads Data/Count/Alloc only
};

// ─── (removed v3.22.2) ─────────────────────────────────────────
// IsValidCharArray + ValidateCharArrayOffset deleted. The functions
// implemented a validate-then-permanently-latch pattern that broke the
// SM path's char-list extraction when EQ had not yet populated
// charSelectPlayerArray at the moment of the first poll after
// pinstCCharacterSelect transitioned non-null. Production paths
// (PopulateCharacterData + Path A in Poll) now trust
// OFFSET_CHARSELECT_ARRAY directly per MQ2 RoF2-emu (x86)
// EverQuest.h:963 — the same shape MQ2's MQ2CharSelectListType.cpp
// uses (pEverQuest->charSelectPlayerArray.GetCount() direct access).
// See CHANGELOG.md v3.22.1 + v3.22.2 entries for the full history.

// ─── Pointer validation ───────────────────────────────────────

static bool IsReadablePtr(const void *ptr, size_t size) {
    if (!ptr) return false;
    MEMORY_BASIC_INFORMATION mbi;
    if (VirtualQuery(ptr, &mbi, sizeof(mbi)) == 0) return false;
    if (mbi.State != MEM_COMMIT) return false;
    if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;
    return true;
}

// ─── MQ2 pinst deref helper (v3.22.35 Path A fix) ─────────────
//
// MQ2's `ppEverQuest` export is `CEverQuest** ppEverQuest;` initialized as
// `ppEverQuest = (CEverQuest**)pinstCEverQuest;` where `pinstCEverQuest` is
// the runtime absolute address `eqgame_base + RVA` (0xA67CCC on Dalaya).
// So MQ2's ppEverQuest VALUE = eqgame storage address (not the CEverQuest*).
// GetProcAddress("ppEverQuest") returns the address of that MQ2 variable,
// making `g_ppEverQuest` effectively `CEverQuest***`. Two derefs needed:
//   deref 1: *g_ppEverQuest    -> eqgame_base + 0xA67CCC (storage in eqgame)
//   deref 2: *(void**)<deref1> -> the heap-allocated CEverQuest*
//
// Pre-v3.22.35 the Path A read sites in PopulateCharacterData / Poll /
// the Init smoke-log did only ONE deref, reading at `(eqgame_base+0xA67CCC)
// + 0x38E6C` = `eqgame_base + 0xAA0B38` (eqgame.exe .rdata). That memory
// is linker-padding or unrelated constants — reliably read as Count=0 or
// out-of-range garbage that fails the sanity gate. Symptom: "Path A
// unreachable on Dalaya at char-select" reported in
// reference_dalaya_path_a_unreachable_at_charselect.md — actually a bridge
// bug, not a Dalaya structural quirk. See _.eqswitch-re/pathA-2026-05-23/
// FINDINGS.md for the full Ghidra-evidenced writeup.
//
// This helper SEH-wraps the two derefs and returns nullptr on any failure
// (matches the prior 1-deref site's error semantics).
static void *DerefEverQuestPointer() {
    if (!g_ppEverQuest) return nullptr;
    void *pEverQuest = nullptr;
    __try {
        void *pinstStorage = *g_ppEverQuest;   // deref 1: eqgame pinst storage addr
        if (pinstStorage && IsReadablePtr(pinstStorage, sizeof(void *))) {
            pEverQuest = *(void **)pinstStorage;   // deref 2: actual CEverQuest*
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    return pEverQuest;
}

// ─── WndMgr window iteration ─────────────────────────────────
// CXWndManager layout (from MQ2 CXWnd.h):
//   64-bit: pWindows ArrayClass<CXWnd*> at offset 0x008
//   32-bit (x86): vtable(4) then pWindows ArrayClass at offset 0x004
// ArrayClass on x86 = {int Count, T* Data, int Alloc} = 12 bytes — matches
// the corrected ArrayClassHeader struct above (Count at +0, Data at +4, Alloc
// at +8 — v3.22.34 fix). The prior `{T* Data, int Count, int Alloc}` comment
// here was wrong; the code in TryWndMgrPointer already used arr->Count and
// arr->Data correctly via the struct definition. Comment corrected v3.22.35.
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

static bool IterateAllWindows(WndIterCallback callback, void *context);

// Public wrapper — exposes the internal iterator to other translation units
// (specifically eqmain_widgets.cpp's structural lookup) without exporting
// internal types or the FindEQMainWndMgr / g_pinstWndMgr / g_ppWndMgr globals.
bool MQ2Bridge::IterateAllWindowsPublic(PublicWndIterCallback callback, void *context) {
    // Cast is safe: PublicWndIterCallback and WndIterCallback have identical
    // signatures — they're declared separately only because the public type
    // lives in the namespace and the internal one is file-static.
    return IterateAllWindows(reinterpret_cast<WndIterCallback>(callback), context);
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
// v3.15.2: file-scope scan-call counter so ResetWidgetCache can clear it on
// charselect transitions. Was a function-local static — meant the "first 5
// scans get a log line" diagnostic went silent after the first 5 calls of the
// process lifetime and never recovered, masking failures across cycles.
static int              g_widgetScanCount = 0;
// v3.15.2: signals to FindWidgetByHeapScan that the last scan budget-aborted
// (don't cache the resulting nullptr — that would lock out retries until
// ResetWidgetCache fires). Reset to false at every HeapScanForWidget entry.
static volatile bool    g_widgetScanBudgetAborted = false;

static void *HeapScanForWidget(const char *name) {
    g_widgetScanBudgetAborted = false;
    if (g_widgetScanCount < 5) {
        DI8Log("mq2_bridge: HeapScanForWidget('%s') — starting scan", name);
        g_widgetScanCount++;
    }
    // v3.15.2 (T3 Sonnet HIGH callout): wall-clock budget mirrors HeapScanFor-
    // CharArray. Without it, a fragmented heap could freeze the EQ game thread
    // for several seconds inside this scan (it runs synchronously from the
    // bridge poll). Observed normal scans complete in 50-300ms; 1500ms is a
    // generous ceiling that still bounds worst-case hitch to ~90 frames @60fps.
    const DWORD scanStartMs = GetTickCount();
    const DWORD scanBudgetMs = 1500;

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
        if (GetTickCount() - scanStartMs > scanBudgetMs) {
            DI8Log("mq2_bridge: HeapScanForWidget('%s') — time budget exceeded (%ums, %d pages, %d regions, last=0x%08X)",
                   name, scanBudgetMs, pagesScanned, regionsTotal, lastAddr);
            g_widgetScanBudgetAborted = true;
            return nullptr;
        }
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

    // Cache the result (even if nullptr, to avoid re-scanning) UNLESS the scan
    // budget-aborted. v3.15.2: caching a budget-abort nullptr would lock out
    // retries until ResetWidgetCache, defeating the budget's purpose (retry on
    // a less fragmented heap). The HeapScanForCharArray pattern uses the same
    // "clear g_heapScanDone on budget-abort" idea.
    if (!g_widgetScanBudgetAborted && g_widgetCacheCount < WIDGET_CACHE_MAX) {
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
    // v3.15.2: re-arm the "first 5 scan-start" diagnostic so the next charselect
    // cycle's first widget scans get logged again. Without this the counter
    // keeps incrementing across cycles and goes silent for the rest of the
    // process lifetime, hiding regressions.
    g_widgetScanCount = 0;
    g_widgetScanBudgetAborted = false;

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

    // ───────────────────────────────────────────────────────────
    // Iteration 10 — combined offset probe for MQ2 port:
    //   10a: XMLIndex shape filter (positive (type<<16)|idx match)
    //   10b: pSidlMgr body className hunt (locates dataArray)
    //   10c: heap scan for "LOGIN_PasswordEdit" CXStr (anchor)
    //
    // Goal: pin the offsets iter 11's compile-time-bound MQ2 port needs.
    // See internal handoff `handoff-eqswitch-combo-g-iterations1-9-20260424.md`.
    // ───────────────────────────────────────────────────────────

    // ── 10a: XMLIndex shape filter (smarter than iter 9's reject-all-ptrs) ──
    // XMLIndex on x86 is uint32 = (uiType<<16) | idx where uiType < 0x100
    // and idx < 0x1000 (per MQ2 eqlib.natvis:351-352). Iter 9 rejected
    // values >= 0x10000 as "pointer-like" — exactly the XMLIndex range.
    // Re-run with a positive shape match.
    if (probed >= 2 && probedPtrs[0] && probedPtrs[1]) {
        auto LooksLikeXMLIdx = [](uint32_t v) -> bool {
            uint32_t hi = v >> 16;
            uint32_t lo = v & 0xFFFF;
            return v != 0 && hi > 0 && hi < 0x100 && lo < 0x1000;
        };
        DI8Log("mq2_bridge: iter 10a — XMLIndex shape probe ((type<<16)|idx, type<0x100, idx<0x1000):");
        int xiHits = 0;
        __try {
            const uint8_t *e1 = (const uint8_t *)probedPtrs[0];
            const uint8_t *e2 = (const uint8_t *)probedPtrs[1];
            const uint8_t *e3 = probedPtrs[2] ? (const uint8_t *)probedPtrs[2] : nullptr;
            for (int o = 0x40; o < 0x200; o += 4) {
                uint32_t v1 = *(const uint32_t *)(e1 + o);
                uint32_t v2 = *(const uint32_t *)(e2 + o);
                uint32_t v3 = e3 ? *(const uint32_t *)(e3 + o) : 0;
                if (!LooksLikeXMLIdx(v1) || !LooksLikeXMLIdx(v2)) continue;
                if (e3 && !LooksLikeXMLIdx(v3)) continue;
                // XMLIndex is unique per widget — values must differ
                if (v1 == v2 && (!e3 || v1 == v3)) continue;
                DI8Log("mq2_bridge:   CAND +0x%02X: [1]=0x%08X (type=%u idx=%u) [2]=0x%08X (type=%u idx=%u)",
                       o, v1, v1>>16, v1&0xFFFF, v2, v2>>16, v2&0xFFFF);
                if (e3) {
                    DI8Log("mq2_bridge:                  [3]=0x%08X (type=%u idx=%u)",
                           v3, v3>>16, v3&0xFFFF);
                }
                if (++xiHits >= 8) break;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (!xiHits) DI8Log("mq2_bridge:   (no XMLIndex-shaped fields in 0x40..0x200)");
    }

    // ── 10b: pSidlMgr body className hunt (locates dataArray) ───────────
    // CSidlManager has inline CXMLDataManager with dataArray of XMLClassData,
    // each holding a className CXStr ("Screen", "EditBox", "Button", ...).
    // Scan pSidlMgr's body for ptr→CXStr buf with a known UI class name.
    // The offsets where these hit reveal dataArray's location and stride.
    if (firstSidlMgr) {
        static const char *const knownClassNames[] = {
            "Screen", "EditBox", "Button", "Slider", "STMLBox",
            "Listbox", "Page", "Label", "Gauge", "TabBox",
            "Spellgem", "Combobox", "TextEntry"
        };
        DI8Log("mq2_bridge: iter 10b — pSidlMgr body className hunt (looking for UI class names):");
        int hits = 0;
        __try {
            const uint8_t *sm = (const uint8_t *)firstSidlMgr;
            for (int o = 0x00; o < 0x800; o += 4) {
                uintptr_t v = *(const uintptr_t *)(sm + o);
                if (v < 0x10000 || v > 0x7FFFFFFF) continue;
                if (!IsReadablePtr((void *)v, 0x18)) continue;
                __try {
                    int len = *(const int *)(v + 8);
                    if (len < 1 || len > 32) continue;
                    const char *s = (const char *)(v + 0x14);
                    const char *match = nullptr;
                    for (const char *kn : knownClassNames) {
                        int klen = 0;
                        while (kn[klen]) klen++;
                        if (len != klen) continue;
                        bool eq = true;
                        for (int k = 0; k < klen; k++) {
                            if (s[k] != kn[k]) { eq = false; break; }
                        }
                        if (eq) { match = kn; break; }
                    }
                    if (match) {
                        DI8Log("mq2_bridge:   HIT pSidlMgr+0x%03X = CXStr@0x%08X len=%d '%s'",
                               o, (unsigned)v, len, match);
                        if (++hits >= 16) break;
                    }
                } __except (EXCEPTION_EXECUTE_HANDLER) {}
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (!hits) DI8Log("mq2_bridge:   (no known UI class names referenced in pSidlMgr+0x00..0x800)");
    }

    // ── 10c: heap-scan for "LOGIN_PasswordEdit" CXStr buffer ────────────
    // Find the literal "LOGIN_PasswordEdit" string anywhere in process.
    // CXStr layout: buf_base+0x14 = C string data, buf_base+0x08 = length.
    // For each hit, classify: STRICT (4-byte aligned + len@-0xC matches) =
    // a real CXStr buf reachable from XMLData::Name. LOOSE (anything else,
    // probably .rdata) = string literal, not useful for backtrace.
    DI8Log("mq2_bridge: iter 10c — heap scan for 'LOGIN_PasswordEdit' CXStr:");
    {
        static const char target[] = "LOGIN_PasswordEdit";
        static const int  targetLen = sizeof(target) - 1;
        int strict = 0, loose = 0;
        MEMORY_BASIC_INFORMATION mbi2;
        uintptr_t addr2 = 0x00010000;
        int pages2 = 0;
        while (addr2 < 0x7FFF0000 && pages2 < 5000 && strict < 4) {
            if (VirtualQuery((void *)addr2, &mbi2, sizeof(mbi2)) == 0) break;
            uintptr_t b = (uintptr_t)mbi2.BaseAddress;
            SIZE_T sz = mbi2.RegionSize;
            if (mbi2.State == MEM_COMMIT &&
                !(mbi2.Protect & (PAGE_NOACCESS | PAGE_GUARD)) &&
                (mbi2.Protect & (PAGE_READONLY | PAGE_READWRITE)) &&
                (mbi2.Type == MEM_PRIVATE || mbi2.Type == MEM_MAPPED)) {
                pages2++;
                __try {
                    const uint8_t *p = (const uint8_t *)b;
                    for (uintptr_t off = 0; off + targetLen + 1 <= sz; off++) {
                        if (p[off] != 'L') continue;  // fast reject
                        bool eq = true;
                        for (int k = 1; k < targetLen; k++) {
                            if (p[off+k] != target[k]) { eq = false; break; }
                        }
                        if (!eq || p[off+targetLen] != 0) continue;
                        bool aligned = (off >= 0x14 && (off & 3) == 0);
                        bool cxstrShaped = false;
                        if (aligned) {
                            int maybeLen = *(const int *)(p + off - 0x0C);
                            cxstrShaped = (maybeLen == targetLen);
                        }
                        if (cxstrShaped) {
                            DI8Log("mq2_bridge:   STRICT buf=0x%08X data=0x%08X len=%d (CXStr — XMLData backtrace target)",
                                   (unsigned)(uintptr_t)(p + off - 0x14), (unsigned)(uintptr_t)(p + off), targetLen);
                            if (++strict >= 4) break;
                        } else if (loose < 2) {
                            DI8Log("mq2_bridge:   LOOSE  data=0x%08X (raw string — likely .rdata, not CXStr)",
                                   (unsigned)(uintptr_t)(p + off));
                            loose++;
                        }
                    }
                } __except (EXCEPTION_EXECUTE_HANDLER) {}
            }
            addr2 = b + sz;
            if (addr2 <= b) addr2 = b + 0x1000;
        }
        DI8Log("mq2_bridge: iter 10c — strict=%d loose=%d hits in %d pages",
               strict, loose, pages2);
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

// ─── Diff 4 /OPT:REF anchor (file-scope volatile) ──────────────
// Stores &MQ2Bridge::JoinServerDirect to defeat linker COMDAT elimination
// when no in-DLL callers exist yet (C# wiring deferred to v3.19+). Volatile
// + file-scope makes the assignment a non-elidable side-effect under any
// /O2 + /OPT:REF + /OPT:ICF + /LTCG combination — the optimizer cannot
// observe the write as dead. The cross-TU-callable getter wrapper below
// provides a second escape route ICF would have to fold simultaneously.
namespace { volatile void *g_keepJoinServerDirect = nullptr; }

// Externally visible — gives the symbol a real cross-TU consumer that ICF
// cannot fold without folding the getter too. Declared C linkage so any
// future GetProcAddress-style debug probe can read the anchor.
extern "C" __declspec(noinline) volatile void *
MQ2Bridge_GetJoinServerDirectAnchor() {
    return g_keepJoinServerDirect;
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

    // ─── Diff 4 primitive availability anchor (R2) ─────────
    // Store the address of JoinServerDirect into a file-scope volatile so
    // the linker cannot /OPT:REF-strip the COMDAT (no in-DLL callers yet —
    // C# wiring deferred to v3.19+ to avoid clobbering v3.18.0 SHM-bump).
    //
    // R1 used a local-variable address-take followed by DI8Log. Verifier
    // pair sweep flagged this as fragile: under /LTCG + /OPT:REF + /OPT:ICF
    // the optimizer could observe the local never escapes and fold the
    // anchor away. Volatile file-scope assignment is the durable fix —
    // volatile writes are observable side-effects the optimizer must preserve
    // per ISO C++. The function-wrapper getter (MQ2Bridge_GetJoinServerDirectAnchor)
    // adds a second escape route: ICF would have to fold both the volatile
    // store AND the cross-TU-callable getter, which it cannot.
    g_keepJoinServerDirect = (volatile void *)(&MQ2Bridge::JoinServerDirect);
    DI8Log("mq2_bridge: Diff 4 primitive available -- JoinServerDirect at %p "
           "(call from native to bypass UI server-select chain; C# wiring "
           "deferred to v3.19+)", (void *)g_keepJoinServerDirect);

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

// ─── MQ2Bridge::IsCharSelectAvailable ──────────────────────────
//
// v3.22.0 Iter-2A (2026-05-16) — Dalaya-reliable char-select detection.
//
// gGameState on Dalaya never advances past 0 even when EQ has created
// the CCharacterSelect window (Path A smoke confirmed). pinstCCharacterSelect
// DOES update reliably — the existing transition-tracking block in
// EQMainWidgetsMQ2::FindLiveScreenByName already double-derefs it at
// mq2_bridge.cpp:3047-3051 for heap-scan cache invalidation. This function
// exposes the same safe double-deref as a boolean for the v8 SHM publish.
//
// Returns true iff:
//   - g_pinstCharSelect symbol was resolved at DLL load (non-null)
//   - *g_pinstCharSelect (the storage address) is non-null AND readable
//   - *(void**)storage (the actual CCharacterSelect window pointer) is non-null
//
// Returns false on any SEH fault at either deref level. The IsReadablePtr
// guard prevents AVs on the storage→*storage step during teardown windows
// (storage address briefly points at unmapped memory during MQ2 Shutdown()
// re-init cycles).
bool MQ2Bridge::IsCharSelectAvailable() {
    if (!g_pinstCharSelect) return false;
    __try {
        uintptr_t storageAddr = *g_pinstCharSelect;
        if (!storageAddr) return false;
        if (!IsReadablePtr((void *)storageAddr, sizeof(void *))) return false;
        void *pCharSelWnd = *(void **)storageAddr;
        if (!pCharSelWnd) return false;
        // Defensive: confirm the inner pointer is mapped before C# acts on it.
        // Match the IsReadablePtr guard the transition-tracker uses (mq2_bridge.cpp ~3086).
        // T2-Opus + T3-Opus + T3-Sonnet convergent finding 2026-05-16: without this,
        // a stale dangling pCharSelWnd from MQ2-Shutdown-in-progress would return true
        // even though the underlying CCharacterSelect object has been freed.
        if (!IsReadablePtr(pCharSelWnd, sizeof(void *))) return false;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
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
                    // Track B fix (2026-05-05): reset heap-scan one-shot gates whenever
                    // pCharSelWnd is a *new non-null* pointer — covers null→non-null
                    // (initial activation) AND non-null→different-non-null (heap reuse,
                    // mid-process MQ2 re-init, fresh char-select instance after Shutdown
                    // re-resolves at a different heap address). Does NOT reset on non-
                    // null→null (zone-out / world-load) — preserves perf gate so we
                    // don't thrash 32k-offset Path A re-scans on every world transition.
                    //
                    // Previously this was gated on (lastObserved == nullptr && pCharSelWnd
                    // != nullptr), which missed the heap-reuse + cross-Shutdown cases —
                    // T2 Sonnet/Opus verifier callout (2026-05-05). Dalaya keeps
                    // gameState=0 across BOTH login and char-select, so the older
                    // gameState=5 reset path doesn't fire on Dalaya at all. This block
                    // is the primary gate-clearing path; Shutdown() is the secondary.
                    if (pCharSelWnd != nullptr) {
                        g_heapScanDone = false;
                        g_heapScanArrayBase = 0;
                        g_heapScanCount = 0;  // v3.22.32: companion reset
                        g_anchorScanCached = false;
                        g_standaloneDelay = 0;
                        g_uiFallbackLogged = false;
                        g_p8GateLogged = false;
                        g_p9GateLogged = false;
                        g_p9SehLogged = false;
                        g_partialPopLogged = false;
                        g_cachedSlotCount = -1;
                        g_cachedNameCol = -1;
                        // v3.15.2: clear chunked-resume cursors so a fresh charselect
                        // cycle starts at 0x01000000 instead of mid-walk from prior cycle.
                        g_lastHeapScanAddr = 0;
                        g_lastAnchorScanAddr = 0;
                        g_lastAnchorScanName[0] = '\0';
                        // v3.22.5: clear consecutive-null-poll counter on charselect
                        // transition. Dalaya holds gameState=0 across login + charselect,
                        // so the gameState=5 reset path at line ~3820 never fires here —
                        // a session that left this counter mid-count was carrying it
                        // across charselect cycles and could spuriously trip the
                        // 30-poll latch-clear threshold in the next cycle.
                        g_consecutiveNullPolls = 0;
                        DI8Log("mq2_bridge: reset heap-scan + slot-mode caches on charselect transition");
                    }
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

    void *pEverQuest = DerefEverQuestPointer();   // v3.22.35: 2-deref via helper (was 1-deref bug)
    if (!pEverQuest) return;

    const uint8_t *pEQ = (const uint8_t *)pEverQuest;

    // MQ2-canonical "trust the offset" — read pEverQuest->charSelectPlayerArray
    // directly each poll. Dalaya x86 has the array at OFFSET_CHARSELECT_ARRAY
    // = 0x38E6C (mq2emu-rof2-x86 build, shifted -0x14 from canonical
    // RoF2-Test 0x38E80; the prior 0x18EC0 value was wrongly imported from
    // the x64 macroquest-rof2-emu tree and was corrected in v3.22.34).
    // Count==0 means EQ has not populated yet (dominant first-poll state
    // after pinstCCharacterSelect transitions); we return zero and the next
    // tick retries — same shape as MQ2's MQ2CharSelectListType.cpp. See
    // CHANGELOG.md v3.22.1 / v3.22.34 / v3.22.35 entries for the history.
    __try {
        const ArrayClassHeader *arr = (const ArrayClassHeader *)(pEQ + OFFSET_CHARSELECT_ARRAY);
        int count = arr->Count;
        const uint8_t *data = arr->Data;

        // Sanity gates: Count in [1, LOGIN_MAX_CHARS] AND Data is heap-readable.
        // Count==0 is "EQ hasn't populated yet" — return zero and retry next poll.
        if (count < 1 || count > LOGIN_MAX_CHARS || !data || !IsReadablePtr(data, CSI_SIZE)) {
            shm->charCount = 0;
            shm->selectedIndex = -1;
            return;
        }

        // P8 first-entry plausibility gate (v3.22.3): the structural sanity
        // gates above pass once `pinstCCharacterSelect` transitions non-null
        // but BEFORE EQ has populated the name strings in the array entries —
        // the array's Count and Data pointer settle first, then names are
        // filled byte-by-byte. Publishing during that window writes N empty
        // names to SHM (the per-entry charset filter below collapses garbage
        // bytes to nameLen=0). The C# consumer then displays slot-mode
        // placeholders instead of real names. Reading entry[0]'s name and
        // requiring it pass IsPlausibleName (strict title-case, 4-15 chars,
        // UI-label blocklist) catches the not-yet-populated window WITHOUT
        // reintroducing a latch — Count==0 is published, next tick retries.
        if (!IsPlausibleName(data + CSI_NAME_OFF)) {
            if (!g_p8GateLogged) {
                g_p8GateLogged = true;
                DI8Log("mq2_bridge: P8 gate fired (PopulateCharacterData) — entry[0] not yet plausible; deferring to next poll. Repeated polls without subsequent success = OFFSET_CHARSELECT_ARRAY likely wrong.");
            }
            shm->charCount = 0;
            shm->selectedIndex = -1;
            return;
        }

        // v3.22.22: validate ALL entries plausible before publishing. The P8
        // gate above only checks entry[0]; observed 2026-05-20 PID 30192 smoke
        // that EQ can set arr->Count = N before all per-entry name bytes are
        // written. Pre-v3.22.22 the per-entry letter filter below would emit
        // an empty string for any not-yet-written slot AND publish charCount=N
        // anyway. Now bail to next poll if any entry [0..count-1] fails
        // IsPlausibleName — same retry semantics as the P8 entry[0] gate.
        // Mirrors the equivalent gate in Poll() Path A.
        for (int i = 0; i < count; i++) {
            const uint8_t *entry = data + (i * CSI_SIZE);
            const uint8_t *name = (const uint8_t *)(entry + CSI_NAME_OFF);
            if (!IsPlausibleName(name)) {
                if (!g_partialPopLogged) {
                    g_partialPopLogged = true;
                    DI8Log("mq2_bridge: PopulateCharacterData partial population — arr->Count=%d but entry[%d] not yet plausible (entry[0] gate passed); deferring to next poll",
                           count, i);
                }
                shm->charCount = 0;
                shm->selectedIndex = -1;
                return;
            }
        }

        for (int i = 0; i < count; i++) {
            const uint8_t *entry = data + (i * CSI_SIZE);
            const char *name = (const char *)(entry + CSI_NAME_OFF);

            // Hotfix v6f: tighten name charset to letters only (EQ naming rule) so
            // a field-label string or garbage bytes from a mis-aligned entry can't
            // be emitted as "name" to the user. v3.22.22 partial-pop gate above
            // already rejected any non-plausible entry, so this letter filter is
            // now redundant for the populated case but kept for defense in depth
            // (predicate parity with Poll() Path A's straight-memcpy after gate).
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
    // Read OFFSET_CHARSELECT_ARRAY directly — same source of truth as
    // Path A + PopulateCharacterData. (Pre-v3.22.1 this was gated on a
    // g_offsetValidated flag; that flag and its validator were removed
    // in v3.22.1 / v3.22.2 — see CHANGELOG.)
    if (g_ppEverQuest) {
        __try {
            // v3.22.35: use 2-deref helper (was 1-deref bug — reading at storage addr + offset)
            void *pEQ = DerefEverQuestPointer();
            DI8Log("  ppEverQuest: export=%p -> CEverQuest*=%p", g_ppEverQuest, pEQ);
            if (pEQ) {
                const ArrayClassHeader *arr = (const ArrayClassHeader *)((uint8_t *)pEQ + OFFSET_CHARSELECT_ARRAY);
                DI8Log("    charSelectPlayerArray at offset 0x%X: Count=%d", OFFSET_CHARSELECT_ARRAY, arr->Count);
                if (arr->Count > 0 && arr->Data) {
                    // Track B v2 (2026-05-05, T3 Sonnet callout): use IsPlausibleName
                    // for the verification log too, so the diagnostic matches the
                    // production predicate. Previously this used 0x20..0x7E (any
                    // printable) and could log garbage like "Heading\0" while the
                    // SHM path correctly rejected the same bytes — actively
                    // misleading during a debug session.
                    const uint8_t *firstName = (const uint8_t *)(arr->Data + CSI_NAME_OFF);
                    char nameBuf[64] = {};
                    if (IsPlausibleName(firstName)) {
                        int len = 0;
                        while (len < 63 && firstName[len] != 0) { nameBuf[len] = firstName[len]; len++; }
                        DI8Log("    first char: '%s' (plausible)", nameBuf);
                    } else {
                        DI8Log("    first char: <fails IsPlausibleName predicate>");
                    }
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

// ─── Request handlers (extracted v3.15.11 for fast-path reuse) ────────────────
// Both helpers are file-scope statics so they don't widen mq2_bridge.h. They
// live above MQ2Bridge::Poll to satisfy the call site below.
//
// Invariants assumed by callers (Poll AND PollRequestsOnly):
//  - shm != nullptr (validated by caller)
//  - g_pGameState + g_ppEverQuest both non-null (validated by caller)
//  - All SEH-prone EQ deref chains are guarded INSIDE the helpers
//
// HandleEnterWorldRequest is gated against gameState == 5 (in-game) so it is
// safe to call even while the player has already entered the world.
//
// HandleSelectionRequest reads shm->charCount AS PUBLISHED BY A PRIOR FULL
// POLL. The fast-path (PollRequestsOnly) does NOT republish char data on the
// same tick — that is the point. C# only fires RequestSelectionBySlot after
// observing charSelectReady == 1, so a valid charCount is guaranteed by the
// time a selection request lands in SHM.

static void HandleEnterWorldRequest(volatile CharSelectShm *shm, int gameState) {
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
    //  -4  = SEH during click (UI stack faulted — abort, do not retry)
    uint32_t ewReq = shm->enterWorldReq;
    uint32_t ewAck = shm->enterWorldAck;

    if (ewReq == ewAck) return;

    DI8Log("mq2_bridge: Enter World request (seq %u->%u, gameState=%d)", ewAck, ewReq, gameState);
    if (gameState == 5 || gameState == -99) {
        // Already in-game OR could not read game state (SEH fallback).
        // Either way, default-safe: drop the request to avoid phantom-
        // clicking Enter World while the player is actually in-game
        // (hotfix v3 HIGH-4).
        shm->enterWorldResult = -2;
        MemoryBarrier();
        shm->enterWorldAck = ewReq;
        DI8Log("mq2_bridge: dropped Enter World request (gameState=%d -- in-game or unreadable)", gameState);
        return;
    }

    void *pEnterBtn = MQ2Bridge::FindWindowByName("CLW_EnterWorldButton");
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

static void HandleSelectionRequest(volatile CharSelectShm *shm) {
    uint32_t reqSeq = shm->requestSeq;
    uint32_t ackSeq = shm->ackSeq;

    if (reqSeq == ackSeq) return;

    int requestedIdx = shm->requestedIndex;
    DI8Log("mq2_bridge: selection request -- index=%d (seq %u->%u)",
           requestedIdx, ackSeq, reqSeq);

    if (requestedIdx < 0 || requestedIdx >= shm->charCount) {
        // Invalid index — ack to prevent infinite retry. (Either C# raced
        // ahead of charCount publish — the next full poll will republish —
        // or the slot really is out of range; C# guards against the latter
        // before fire so this is mostly the race.)
        DI8Log("mq2_bridge: selection SKIPPED -- index=%d charCount=%d",
               requestedIdx, shm->charCount);
        shm->ackSeq = reqSeq;
        return;
    }

    void *pCharListWnd = MQ2Bridge::FindWindowByName("Character_List");
    if (!pCharListWnd || !g_fnSetCurSel) {
        DI8Log("mq2_bridge: selection DEFERRED -- Character_List=%p SetCurSel=%p",
               pCharListWnd, g_fnSetCurSel);
        // Don't ack — C# will retry on next poll
        return;
    }

    // v3.20.8: row-anchor re-resolve. shm->names[] is heap-anchored when
    // populated by Path A / Path C / standalone heap-scan / anchor-scan; only
    // Path B's GetItemText reader is CListWnd-row-anchored. SetCurSel operates
    // on CListWnd row index. When heap order ≠ CListWnd row order (observed on
    // Dalaya 2026-05-15 — gotquiz1 configured "Natedogg" loaded "acpots"),
    // C#'s byName scan returns the right slot for the heap-anchored names but
    // SetCurSel applies that slot to the wrong CListWnd row.
    //
    // Mirror MQ2's authoritative path (src/plugins/autologin/StateMachine.cpp:
    // 631-642): scan CListWnd rows via GetListItemText, find the row whose
    // name equals the targetName C# matched against, and SetCurSel that row.
    // Falls back to requestedIdx when GetItemText is unavailable / no match —
    // preserves current behavior on servers where heap order happens to agree
    // with row order.
    int rowIdx = requestedIdx;
    // Defensive copy of the requested name out of SHM into a null-padded local
    // buffer. shm->names[] is volatile and written by Path A/B/B2/C/anchor —
    // most paths explicitly null-terminate within CHARSEL_NAME_LEN, but a
    // torn read across the C#/DLL boundary could observe garbage past the
    // logical end. Copying with a forced trailing null makes the compare loop
    // below safe against any volatile-tear scenario the verifiers flagged.
    char targetName[CHARSEL_NAME_LEN] = {};
    __try {
        for (int k = 0; k < CHARSEL_NAME_LEN - 1; k++) {
            char c = ((const char *)shm->names[requestedIdx])[k];
            if (!c) break;
            targetName[k] = c;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH reading targetName from shm->names[%d]", requestedIdx);
        targetName[0] = '\0';
    }
    // Skip the row re-resolve when targetName is a Path-B2 slot-mode
    // placeholder ("Slot 1", "Slot 2", ...). These synthetic names are
    // written when GetItemText returned empty columns and the bridge fell
    // back to SetCurSel/GetCurSel slot-probing — they will NEVER match a
    // real CListWnd row name (which is a character name like "Natedogg"),
    // and entering the scan would log a misleading "name NOT in CListWnd"
    // and fall back to requestedIdx (= the v3.20.7 behavior). Detect them
    // by the literal "Slot " prefix the wsprintfA path emits.
    bool isSlotPlaceholder = (targetName[0] == 'S' && targetName[1] == 'l' &&
                              targetName[2] == 'o' && targetName[3] == 't' &&
                              targetName[4] == ' ');
    if (g_fnGetItemText && targetName[0] && !isSlotPlaceholder) {
        // Use cached nameCol from Path B's earlier probe, or probe on-demand
        // here. g_cachedNameCol stays -1 when Path A succeeded first (Path B's
        // probe code is gated by !charDataRead and never ran). Sentinel -2
        // means "probed this cycle, no name column found" — skip the 10-column
        // sweep so C# retries don't pay the probe cost repeatedly. Cleared
        // alongside the other charselect-cycle caches on gameState==5.
        int nameCol = g_cachedNameCol;
        if (nameCol == -1) {
            for (int tryCol = 0; tryCol <= 9 && nameCol < 0; tryCol++) {
                char test[CHARSEL_NAME_LEN] = {};
                if (ReadListItemText(pCharListWnd, 0, tryCol, test, CHARSEL_NAME_LEN) && test[0]) {
                    bool looksLikeName = (test[0] >= 'A' && test[0] <= 'Z') && strlen(test) >= 4;
                    if (looksLikeName) {
                        bool allAlpha = true;
                        for (int k = 0; test[k]; k++) {
                            if (!((test[k] >= 'A' && test[k] <= 'Z') ||
                                  (test[k] >= 'a' && test[k] <= 'z'))) {
                                allAlpha = false; break;
                            }
                        }
                        if (allAlpha) {
                            nameCol = tryCol;
                            g_cachedNameCol = nameCol;
                            DI8Log("mq2_bridge: row re-resolve: probed nameCol=%d on-demand "
                                   "(first row: '%s')", tryCol, test);
                        }
                    }
                }
            }
            if (nameCol < 0) {
                // Full 10-column sweep produced no name candidate. Cache the
                // failure as -2 so we don't re-probe on every retry. Path B's
                // own probe (line ~3796) checks against -1 explicitly, so it
                // can still re-probe later in the cycle when more columns may
                // be populated; it just won't double-pay this call's sweep.
                g_cachedNameCol = -2;
            }
        }
        if (nameCol >= 0) {
            // Cap the row scan at the SHM-published charCount (which is itself
            // capped at CHARSEL_MAX_CHARS=10 by the publishing paths). Avoids
            // scanning empty rows past the actual char-count and reduces the
            // false-positive log when the name isn't found.
            int rowCap = shm->charCount;
            if (rowCap > CHARSEL_MAX_CHARS) rowCap = CHARSEL_MAX_CHARS;
            if (rowCap < 1) rowCap = CHARSEL_MAX_CHARS;  // defensive: charCount race
            int matchedRow = -1;
            for (int i = 0; i < rowCap; i++) {
                char rowName[CHARSEL_NAME_LEN] = {};
                if (!ReadListItemText(pCharListWnd, i, nameCol, rowName, CHARSEL_NAME_LEN)
                    || !rowName[0]) {
                    continue;
                }
                // Case-insensitive compare. EQ names are letters-only (server
                // naming rule), so a simple ASCII case-fold is sufficient and
                // matches the CharacterSelector.Decide OrdinalIgnoreCase rule
                // the C# side uses to derive requestedIdx in the first place.
                // Loop terminates when EITHER string ends (`!a || !b`) — guards
                // against the verifier-flagged false-positive prefix match if
                // one buffer lacks a null terminator within 64 bytes.
                bool match = true;
                for (int k = 0; k < CHARSEL_NAME_LEN; k++) {
                    char a = rowName[k];
                    char b = targetName[k];
                    if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                    if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
                    if (a != b) { match = false; break; }
                    if (!a || !b) break;
                }
                if (match) { matchedRow = i; break; }
            }
            if (matchedRow >= 0) {
                if (matchedRow != requestedIdx) {
                    DI8Log("mq2_bridge: row re-resolve: heap idx=%d ('%s') -> "
                           "CListWnd row=%d (UI-anchored, MQ2-canonical)",
                           requestedIdx, targetName, matchedRow);
                }
                rowIdx = matchedRow;
            } else {
                // Name not present in CListWnd — could be slot-mode placeholder
                // ("Slot N"), CListWnd still loading, or genuinely-missing char.
                // Fall back to requestedIdx and let C#'s pre-Enter abort gates
                // catch a wrong-character landing if they would.
                DI8Log("mq2_bridge: row re-resolve: name '%s' NOT in CListWnd "
                       "(nameCol=%d, rowCap=%d) -- falling back to heap idx=%d",
                       targetName, nameCol, rowCap, requestedIdx);
            }
        }
    } else if (isSlotPlaceholder) {
        DI8Log("mq2_bridge: row re-resolve: skipped (targetName='%s' is a slot "
               "placeholder; falling back to heap idx=%d)",
               targetName, requestedIdx);
    }

    __try {
        g_fnSetCurSel(pCharListWnd, rowIdx);
        shm->selectedIndex = rowIdx;
        shm->ackSeq = reqSeq;  // ack ONLY on successful SetCurSel
        DI8Log("mq2_bridge: selected character row %d (\"%s\") -- requested heap idx=%d",
               rowIdx, (const char *)shm->names[requestedIdx], requestedIdx);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH in SetCurSel(%d)", rowIdx);
    }
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

    // v3.15.11: handlers extracted to file-scope helpers. Call sites moved
    // up so the selection handler runs BEFORE the heavy heap-scan paths
    // below — a selection request only needs the charCount that was
    // published by a prior full Poll tick, so it can run early. The
    // PollRequestsOnly fast path also calls these two helpers (and only
    // these two) on the unthrottled fast-path tick when a request is
    // pending — see eqswitch-di8.cpp::MQ2BridgePollTick two-tier throttle.
    HandleEnterWorldRequest(shm, gameState);
    HandleSelectionRequest(shm);

    // Dalaya ROF2: gameState=0 at login AND charselect. We can't gate on
    // gameState==1. Instead, attempt to read character data always — the
    // SEH guards and validation (IsReadablePtr, name checks) handle invalid states.
    // If we're not at charselect, charCount will be 0 and nothing happens.
    // v3 latch defense-in-depth: clear charSelectReady after sustained
    // pinstCCharacterSelect-null period. gameState=5 (below) is the primary
    // clear path — fires when the user enters world. But Dalaya keeps
    // gameState=0 across BOTH login and charselect, so a user who backs out of
    // charselect to the login screen never trips the gameState=5 path. Without
    // this defense, the latch stays stale across the back-out and a re-fired
    // autologin in the same eqgame.exe PID would observe charSelectReady=1
    // immediately + charCount=0 + empty names → 2s retry → "character not
    // found" abort (misleading diagnostic, safe stop). Threshold: 30 polls
    // (~15s) — comfortably above the worst observed pinst-flutter null period
    // (~12s in the v3.15.0 bug logs) so brief flutter inside an active
    // charselect cycle doesn't trip it.
    //
    // Counter is at TU scope (declared above as g_consecutiveNullPolls — was
    // promoted from block-static to TU scope in v3.15.1 so the gameState==5
    // reset block can clear it across charselect cycles).
    //
    // v3.15.11 INVARIANT (do not violate): g_consecutiveNullPolls + the
    // log-line `g_consecutiveNullPolls * 500` arithmetic + the 30-poll
    // threshold ALL assume one increment per 500ms — i.e. that this code
    // path runs only inside MQ2Bridge::Poll under the 500ms throttle in
    // eqswitch-di8.cpp::MQ2BridgePollTick. The v3.15.11 fast path
    // (MQ2Bridge::PollRequestsOnly, called when a C# request is pending
    // and the throttle hasn't expired) deliberately does NOT execute this
    // block — moving the increment into PollRequestsOnly would tick the
    // counter at ~16ms cadence and the latch would clear in ~480ms instead
    // of ~15s. Same caveat applies to g_standaloneDelay (20-poll threshold
    // = 10s) below at line ~3990. If you ever need the latch logic to run
    // on the fast path, convert the counter to a wall-clock timestamp
    // first; do NOT just relocate the increment.
    {
        bool pinstNull = true;
        if (g_pinstCharSelect) {
            __try {
                uintptr_t storage = *g_pinstCharSelect;
                if (storage && IsReadablePtr((void *)storage, sizeof(void *))) {
                    void *pCharSelWnd = *(void **)storage;
                    if (pCharSelWnd) pinstNull = false;
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) { /* treat SEH as null */ }
        }
        if (pinstNull) {
            if (g_consecutiveNullPolls < 100) g_consecutiveNullPolls++;  // cap
            if (g_consecutiveNullPolls == 30 && shm->charSelectReady) {
                shm->charSelectReady = 0;
                DI8Log("mq2_bridge: charSelectReady latch cleared (pinst null %u polls ~ %ums)",
                       g_consecutiveNullPolls, g_consecutiveNullPolls * 500);
            }
        } else {
            g_consecutiveNullPolls = 0;
        }
    }

    if (gameState == 5) {
        // gameState 5 = in-game on Dalaya. Clear char data + reset all charselect caches.
        shm->charCount = 0;
        shm->selectedIndex = -1;
        // v3 latch: in-game means the prior charselect cycle is over. Clear so
        // a future charselect (camp + return) starts with a fresh latch.
        shm->charSelectReady = 0;
        // Drain any in-flight Enter World request so a later session in the same
        // process can't observe a stale ack/result from this charselect cycle.
        shm->enterWorldReq = 0;
        shm->enterWorldAck = 0;
        shm->enterWorldResult = 0;
        g_uiFallbackLogged = false;
        g_p8GateLogged = false;
        g_p9GateLogged = false;
        g_p9SehLogged = false;
        g_partialPopLogged = false;
        g_cachedNameCol = -1;
        g_cachedSlotCount = -1;
        g_heapScanDone = false;
        g_heapScanArrayBase = 0;
        g_heapScanCount = 0;  // v3.22.32: companion reset
        g_anchorScanCached = false;
        g_standaloneDelay = 0;  // reset for next charselect cycle
        g_verificationDone = false;
        // v3.15.2: clear chunked-resume cursors on in-game transition.
        g_lastHeapScanAddr = 0;
        g_lastAnchorScanAddr = 0;
        g_lastAnchorScanName[0] = '\0';
        // R2 verifier callout: latch-clear counter (file-scope above) must
        // also reset on in-game transition — otherwise a session that left
        // it mid-count would carry that count into the NEXT charselect cycle
        // and could spuriously trip the 30-poll threshold there.
        g_consecutiveNullPolls = 0;
        return;
    }

    // At character select -- read character data
    // Strategy: try charSelectPlayerArray first (struct offsets), then fall back to
    // UI-based reading via Character_List CListWnd (GetItemText).
    bool charDataRead = false;

    // Path A: CEverQuest::charSelectPlayerArray (struct-based, gives level+class)
    void *pEverQuest = DerefEverQuestPointer();   // v3.22.35: 2-deref via helper (was 1-deref bug)

    if (pEverQuest) {
        const uint8_t *pEQ = (const uint8_t *)pEverQuest;
        // MQ2-canonical "trust the offset" — same rationale as
        // PopulateCharacterData above. Trust OFFSET_CHARSELECT_ARRAY = 0x38E6C
        // (mq2emu-rof2-x86 build, Dalaya-shifted from canonical 0x38E80; v3.22.34
        // corrected the prior 0x18EC0 imported from the x64 tree) and let
        // Count==0 polls retry next tick.
        __try {
            const ArrayClassHeader *arr = (const ArrayClassHeader *)(pEQ + OFFSET_CHARSELECT_ARRAY);
            int count = arr->Count;
            const uint8_t *data = arr->Data;

            // P8 first-entry plausibility gate (v3.22.3): same rationale as
            // PopulateCharacterData above — the structural gates pass before EQ
            // has populated entry name bytes, so without this Path A would
            // write empty names AND latch `charSelectReady = 1`, telling C# the
            // array is ready when it isn't. Failure leaves `charDataRead = false`,
            // letting Path B's UI fallback run this tick + next-tick Path A retry.
            if (count >= 1 && count <= CHARSEL_MAX_CHARS && data && IsReadablePtr(data, CSI_SIZE)) {
                if (!IsPlausibleName(data + CSI_NAME_OFF)) {
                    if (!g_p8GateLogged) {
                        g_p8GateLogged = true;
                        DI8Log("mq2_bridge: P8 gate fired (Poll Path A) — entry[0] not yet plausible; deferring to next poll. Repeated polls without subsequent success = OFFSET_CHARSELECT_ARRAY likely wrong.");
                    }
                } else {
                    // v3.22.22: validate ALL entries plausible before publishing.
                    // The P8 gate above only checks entry[0]; observed 2026-05-20
                    // PID 30192 smoke that EQ can set arr->Count to its final
                    // value (10) BEFORE all per-entry name bytes are written —
                    // at 262ms post-CharSelect, entries[0..4] held real names,
                    // entries[5..9] were zero. Pre-v3.22.22 the write loop below
                    // emitted 5 real names + 5 empty strings into shm->names[]
                    // and still published charCount=10 + latched charSelectReady=1.
                    // C# autologin then read 10 names with empty slots between
                    // populated ones, couldn't find the target character, and
                    // aborted. The all-entries pre-gate bails to next-poll retry
                    // on partial pop — same semantics as the P8 gate failure
                    // path (charDataRead stays false, Path B's UI fallback runs
                    // this tick, next 500ms poll retries Path A). See
                    // reference_eqswitch_v3_22_22_backlog.md.
                    bool allPlausible = true;
                    int firstBadIdx = -1;
                    for (int i = 0; i < count; i++) {
                        const uint8_t *entry = data + (i * CSI_SIZE);
                        const uint8_t *name = (const uint8_t *)(entry + CSI_NAME_OFF);
                        if (!IsPlausibleName(name)) {
                            allPlausible = false;
                            firstBadIdx = i;
                            break;
                        }
                    }
                    if (!allPlausible) {
                        if (!g_partialPopLogged) {
                            g_partialPopLogged = true;
                            DI8Log("mq2_bridge: Path A partial population — arr->Count=%d but entry[%d] not yet plausible (entry[0] gate passed); deferring to next poll",
                                   count, firstBadIdx);
                        }
                        // Leave charDataRead=false; Path B and the next 500ms Path A
                        // poll will retry. Do NOT publish charCount or latch
                        // charSelectReady — that's what caused the 2026-05-20 bug.
                    } else {
                        for (int i = 0; i < count; i++) {
                            const uint8_t *entry = data + (i * CSI_SIZE);
                            const char *name = (const char *)(entry + CSI_NAME_OFF);
                            // All entries pre-validated as plausible by the gate
                            // above (Track B v3 / 2026-05-05 / T3 Opus v3 callout:
                            // IsPlausibleName parity with Path C heap-scan readers,
                            // strict title-case uppercase-first then lowercase-only).
                            // Loop is now a straight name-copy without per-entry
                            // predicate — the gate moved the predicate up so a
                            // partial pop fails before any SHM write.
                            int nameLen = 0;
                            while (nameLen < CHARSEL_NAME_LEN - 1 && name[nameLen] != '\0') {
                                nameLen++;
                            }
                            memcpy((void *)shm->names[i], name, nameLen);
                            ((char *)shm->names[i])[nameLen] = '\0';
                            shm->levels[i] = *(const int32_t *)(entry + CSI_LEVEL_OFF);
                            shm->classes[i] = *(const int32_t *)(entry + CSI_CLASS_OFF);
                        }
                        MemoryBarrier();
                        shm->charCount = count;
                        shm->charSelectReady = 1;  // v3 latch: Path A wrote real names
                        for (int i = count; i < CHARSEL_MAX_CHARS; i++) {
                            ((char *)shm->names[i])[0] = '\0';
                            shm->levels[i] = 0;
                            shm->classes[i] = 0;
                        }
                        charDataRead = true;
                    }
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("mq2_bridge: SEH reading charSelectPlayerArray (Path A trust-offset path)");
        }
    }

    // Path B: UI-based fallback — read from Character_List CListWnd via GetItemText.
    // MQ2AutoLogin reads column 2 for character name. This works even when
    // charSelectPlayerArray offset is wrong.
    if (!charDataRead && g_fnGetItemText) {
        void *pCharList = FindWindowByName("Character_List");
        if (pCharList) {
            // v3.22.33 — pump-responsiveness probe HOISTED above first-poll
            // diagnostic GetCurSel.
            //
            // v3.22.32 placed this probe BELOW the `if (!g_uiFallbackLogged)`
            // block — which meant the first-poll diagnostic GetCurSel call at
            // ~line 4025 (inside that block) ran BEFORE the probe could gate
            // it. T4 Opus verifier-CRITICAL (sev-4) 2026-05-23: the smoke's
            // smoking-gun was "Character_List GetCurSel = 0" as the last
            // mq2_bridge line for 44 s of silence — that line is emitted from
            // the first-poll block, NOT from Path B/B2. The hang point was
            // GetCurSel itself (an MQ2 export that, on Dalaya, sometimes
            // touches the CListWnd's internal state in a way that blocks on
            // EQ's main thread). Skipping it on a non-responsive pump is the
            // load-bearing protection for the original failure mode.
            //
            // Hoisting also lets us short-circuit ALL three downstream
            // heap-allocating surfaces (first-poll diagnostic GetCurSel, Path
            // B column discovery via GetItemText, Path B2 SetCurSel/GetCurSel
            // probe) on a single probe result rather than maintaining three
            // independent guards.
            //
            // The probe — SendMessageTimeout WM_NULL with 100 ms budget +
            // SMTO_ABORTIFHUNG | SMTO_BLOCK — short-circuits at this surface.
            // When pumpResponsive=false, the first-poll diagnostic, column-
            // discovery loop, AND Path B2 are SKIPPED. Execution falls
            // through to Path C (heap scan + P9 publisher), which is pure
            // memory operations (VirtualQuery + IsPlausibleName) with no EQ
            // heap/pump dependency. This is the load-bearing change.
            //
            // SMTO_BLOCK prevents reentrant dispatch into our hooks while
            // the probe waits, mirroring the v3.22.22 round-5 ApplySlimTitlebar
            // probe pattern. The 100 ms budget matches IWindowsApi.IsClientResponsive
            // on the C# side.
            bool pumpResponsive = true;
            HWND eqWnd = GetEqHwnd();
            if (eqWnd) {
                DWORD_PTR smResult = 0;
                LRESULT smRet = SendMessageTimeoutA(eqWnd, WM_NULL, 0, 0,
                                                   SMTO_ABORTIFHUNG | SMTO_BLOCK,
                                                   100, &smResult);
                if (smRet == 0) {
                    DWORD smErr = GetLastError();
                    if (smErr == ERROR_TIMEOUT || smErr == 0) {
                        pumpResponsive = false;
                        // v3.22.33 Gap 4 (T2): rate-limiter now file-scope so
                        // it's cleared on charselect transition + gs=5.
                        DWORD now = GetTickCount();
                        if (now - g_lastPumpProbeWarnMs > 5000) {
                            g_lastPumpProbeWarnMs = now;
                            // v3.22.33 (T3 Sonnet+Opus MEDIUM): differentiate
                            // ERROR_TIMEOUT from smErr==0. Conservative skip
                            // either way (safe-default), but log distinctly
                            // so the next debugger sees the difference.
                            const char *cause = (smErr == ERROR_TIMEOUT)
                                ? "timeout (1460)"
                                : "unknown (GetLastError not set, treating conservatively)";
                            DI8Log("mq2_bridge: EQ pump non-responsive (SendMessageTimeoutA WM_NULL > 100ms, err=%u %s) — SKIPPING first-poll diagnostic + Path B column discovery + Path B2 probe (all heap-allocating); Path C heap scan still runs",
                                   smErr, cause);
                        }
                    }
                }
            }

            if (pumpResponsive && !g_uiFallbackLogged) {
                DI8Log("mq2_bridge: charSelectPlayerArray unavailable — using UI fallback (CListWnd at %p)", pCharList);
                g_uiFallbackLogged = true;

                // Check if list has a current selection (proves list has items).
                // v3.22.33 (T4 Opus sev-4): gated by pumpResponsive — this is
                // the exact call site that hung in the 2026-05-23 gotquiz smoke.
                if (g_fnGetCurSel) {
                    __try {
                        int curSel = g_fnGetCurSel(pCharList);
                        DI8Log("mq2_bridge: Character_List GetCurSel = %d", curSel);
                    } __except(EXCEPTION_EXECUTE_HANDLER) {
                        DI8Log("mq2_bridge: Character_List GetCurSel SEH");
                    }
                }
            }
            else if (!g_uiFallbackLogged) {
                // Pump non-responsive on the FIRST entry to char-select.
                // g_uiFallbackLogged stays false so the next responsive poll
                // can run the diagnostic. Path C below STILL fires this poll
                // via the gate-widening (`g_uiFallbackLogged` OR Path B2
                // count) so heap-scan publishes regardless.
                //
                // Cosmetic: log the UI-fallback observation directly so the
                // log trail isn't missing "we reached charselect" entirely.
                DI8Log("mq2_bridge: charSelectPlayerArray unavailable — UI fallback queued (CListWnd at %p), pump non-responsive this poll — first-poll diagnostic deferred",
                       pCharList);
                // Set the latch so Path C below can fire. Subsequent polls'
                // first-poll diagnostic block is naturally idempotent on
                // !g_uiFallbackLogged, so re-running it once the pump
                // recovers is a no-op (the latch stays true).
                g_uiFallbackLogged = true;
            }

            // (v3.22.33: pump probe + first-poll diagnostic gating moved above
            // this point. The original v3.22.32 probe block lived here.)

            // Discover name column — retry every poll until found (don't cache failure)
            // Dalaya ROF2 may use non-standard columns, scan wider range (0-9).
            // v3.22.16: scan rows 0-3 instead of just row 0. The single-row scan
            // silently failed every poll on the 2026-05-18 02:31 smoke when the
            // CListWnd had row 0 unpopulated (user's characters in slots 1+).
            // Closes the v3.22.10 closeout's "P9/P8/uiFallback latch back-out"
            // gap for the row-0-empty case. Fix-all-three together per v3.22.4
            // rule: row-0-empty handling (this), failure-log re-arm (below),
            // and Path B2 fallback timing (unchanged — still fires when
            // count==0 && nameCol<0 after the loop, which is now correct).
            // v3.22.32: gated on pumpResponsive (above) — heap-allocating paths.
            int nameCol = g_cachedNameCol;
            if (pumpResponsive && nameCol < 0) {
                for (int tryCol = 0; tryCol <= 9 && nameCol < 0; tryCol++) {
                    for (int tryRow = 0; tryRow < 4 && nameCol < 0; tryRow++) {
                        char test[CHARSEL_NAME_LEN] = {};
                        if (ReadListItemText(pCharList, tryRow, tryCol, test, CHARSEL_NAME_LEN) && test[0]) {
                            // v3.22.5: promote to IsPlausibleName for parity with Path A
                            // P8 gate, Path C anchor, heap scans, and the P9 publisher
                            // gate. The prior inline validator (uppercase first + >=4
                            // chars + all-alpha-either-case) accepted column headers
                            // like "Name"/"Race"/"Class"/"Level" and would permanently
                            // lock g_cachedNameCol to the wrong column for the rest of
                            // the session if the slot-0 row's header text appeared
                            // before any real name. kBadNames inside IsPlausibleName
                            // already blocks those headers; the predicate now agrees
                            // across all six charselect sites.
                            if (IsPlausibleName((const uint8_t *)test)) {
                                nameCol = tryCol;
                                g_cachedNameCol = nameCol;
                                DI8Log("mq2_bridge: UI fallback: name column = %d (first plausible name at row %d: '%s')", tryCol, tryRow, test);
                            }
                        }
                    }
                }
                // v3.22.16: Make column-discovery failure LOUD instead of silent.
                // The v3.22.10 closeout's "P9/P8/uiFallback latch back-out" gap was
                // this exact silent-every-poll pattern. Rate-limit to once per 5s
                // per charselect cycle so we get diagnostic visibility without spam.
                // reference_loud_runtime_silent_rest.md: loud signal at the failing
                // surface, not silent fallthrough to a 30s SM timeout.
                if (nameCol < 0) {
                    DWORD now = GetTickCount();
                    if (now - g_lastColDiscoveryFailMs > 5000) {
                        g_lastColDiscoveryFailMs = now;
                        // Sample row 0 across cols 0-3 to diagnose what's actually there
                        char probes[4][CHARSEL_NAME_LEN] = {};
                        bool gotAny[4] = {};
                        for (int c = 0; c < 4; c++) {
                            gotAny[c] = ReadListItemText(pCharList, 0, c, probes[c], CHARSEL_NAME_LEN) && probes[c][0];
                        }
                        DI8Log("mq2_bridge: UI fallback: column discovery FAILED (rows 0-3 x cols 0-9) — row 0 cols [0]='%s' [1]='%s' [2]='%s' [3]='%s' (all fail IsPlausibleName)",
                               gotAny[0] ? probes[0] : "(empty)",
                               gotAny[1] ? probes[1] : "(empty)",
                               gotAny[2] ? probes[2] : "(empty)",
                               gotAny[3] ? probes[3] : "(empty)");
                    }
                }
            }

            // v3.22.32: seed local count from a prior poll's heap-scan result.
            // Without this seed each poll started at count=0 and Path B2's
            // SetCurSel/GetCurSel probe became the only producer — but the
            // probe stalls on background-client EQ pumps (PID 4628 smoke
            // 2026-05-22 hung indefinitely after `Character_List GetCurSel = 0`).
            // With the seed, once Path C below has discovered the heap array
            // ONCE, every subsequent poll's re-read + P9 publish runs without
            // needing the probe to return again. First poll after charselect
            // is the only one that still depends on Path B/B2 OR the new
            // Path C "fire on g_uiFallbackLogged" gate (below).
            int count = (g_heapScanDone && g_heapScanCount > 0) ? g_heapScanCount : 0;
            // v3.22.32: Path B (per-row GetItemText) gated on pumpResponsive.
            if (pumpResponsive && nameCol >= 0) {
                count = 0;  // Path B owns count when it has a name column
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
            // v3.22.32: gated on pumpResponsive — SetCurSel triggers EQ WndProc
            // dispatch + character-preview re-render, both of which block on a
            // hung pump and deadlock the bridge poll thread (PID 4628 2026-05-22).
            if (pumpResponsive && count == 0 && nameCol < 0 && g_fnGetCurSel) {
                if (g_cachedSlotCount > 0) {
                    // Use cached probe result — just update selectedIndex
                    count = g_cachedSlotCount;
                    __try {
                        int curSel = g_fnGetCurSel(pCharList);
                        shm->selectedIndex = curSel;
                    } __except(EXCEPTION_EXECUTE_HANDLER) {}
                    // Track B v3 fix (2026-05-05, T2 Sonnet+Opus v5 callout): skip
                    // the "Slot N" placeholder rewrite when Path C/anchor already
                    // populated real names this charselect cycle. Without this gate,
                    // each poll's cached path overwrites real names with "Slot 1"
                    // BEFORE the re-read path restores them — a brief window where
                    // C# can see charCount=1 + names[0]="Slot 1" + stale slot-mode
                    // abort gate trips. g_heapScanArrayBase is set whenever Path C
                    // (line ~3680) or anchor scan (line ~3680) found a live entry.
                    if (!g_heapScanArrayBase) {
                        // Repopulate slot names (SHM may have been reset)
                        for (int i = 0; i < count; i++) {
                            char slotName[CHARSEL_NAME_LEN] = {};
                            wsprintfA(slotName, "Slot %d", i + 1);
                            memcpy((void *)shm->names[i], slotName, CHARSEL_NAME_LEN);
                            shm->levels[i] = 0;
                            shm->classes[i] = 0;
                        }
                    }
                } else if (g_fnSetCurSel) {
                    // First probe — SetCurSel/GetCurSel on each slot to find actual count.
                    // 2026-05-17: removed `if (curSel >= 0)` gate. On Dalaya the char-select
                    // screen loads with NO selection (curSel == -1 is the legit "no selection
                    // yet" state, not an error). The probe itself works regardless: SetCurSel(i)
                    // + GetCurSel() returns i iff the slot exists, agnostic to prior selection.
                    // The pre-fix gate dead-locked Path B2 every time Path A hadn't yet
                    // populated AND the user hadn't manually clicked a char.
                    __try {
                        int curSel = g_fnGetCurSel(pCharList);
                        int probeCount = 0;
                        int origSel = curSel;  // -1 is fine; we'll skip restore if so
                        for (int i = 0; i < CHARSEL_MAX_CHARS; i++) {
                            g_fnSetCurSel(pCharList, i);
                            int readBack = g_fnGetCurSel(pCharList);
                            if (readBack == i) {
                                probeCount = i + 1;
                            } else {
                                break;
                            }
                        }
                        // Restore only if origSel was a legit selection (>=0). If it was -1
                        // (no selection at probe entry), restoring would be a no-op anyway,
                        // but skipping makes the intent obvious + avoids any side effect of
                        // SetCurSel(-1) in EQ's CListWnd implementation.
                        if (origSel >= 0) {
                            g_fnSetCurSel(pCharList, origSel);
                        }

                        if (probeCount == 0) {
                            DI8Log("mq2_bridge: UI fallback: slot probe inconclusive (curSel=%d), skipping", origSel);
                        } else {
                            count = probeCount;
                            g_cachedSlotCount = probeCount;
                            for (int i = 0; i < count; i++) {
                                char slotName[CHARSEL_NAME_LEN] = {};
                                wsprintfA(slotName, "Slot %d", i + 1);
                                memcpy((void *)shm->names[i], slotName, CHARSEL_NAME_LEN);
                                shm->levels[i] = 0;
                                shm->classes[i] = 0;
                            }
                            shm->selectedIndex = (origSel >= 0) ? origSel : 0;
                            DI8Log("mq2_bridge: UI fallback: slot-based mode — probed %d slots (curSel=%d)",
                                   count, origSel);
                        }
                    } __except(EXCEPTION_EXECUTE_HANDLER) {
                        DI8Log("mq2_bridge: SEH in GetCurSel fallback");
                    }
                }
            }

            // Path C: heap scan for real character names when slot-based mode is active.
            // Dalaya stores names in a heap array at stride 0x160. One-shot scan per session.
            //
            // v3.22.32: gate widened from `count > 0` to `(count > 0 || g_uiFallbackLogged)`.
            // The original gate required Path B/B2 to publish a count first — but Path B2's
            // SetCurSel probe stalls on background clients (DX device idle, EQ pump non-
            // responsive at char-select; observed 2026-05-22 PID 4628). g_uiFallbackLogged
            // is set this same poll once we've located pCharList, so it's a strict superset
            // of `count > 0` — heap-scan still gated on actually being at char-select, just
            // no longer gated on Path B2 returning. Heap scan is memory-only (VirtualQuery
            // + IsPlausibleName + race-byte sanity check) — no EQ pump dependency — so it
            // runs to completion regardless of background/foreground state. On success it
            // populates g_heapScanCount which the count-seed at the top of this block
            // propagates into subsequent polls' re-read + P9 publish paths.
            if ((count > 0 || g_uiFallbackLogged) && !g_heapScanDone) {
                g_heapScanDone = true;
                uintptr_t arrayBase = HeapScanForCharArray();
                if (arrayBase) {
                    g_heapScanArrayBase = arrayBase;
                    g_anchorScanCached = false;  // full-array path: real list, real curSel
                    // v3.22.32: scan all 10 slots regardless of incoming count.
                    // The HeapScanForCharArray validator required `validCount >= 5`
                    // adjacent plausible entries to anchor the array, so the array
                    // itself is known to hold at least 5 valid characters. We scan
                    // up to CHARSEL_MAX_CHARS and track the highest valid index for
                    // contiguous account layouts (the standard EQ pattern), with a
                    // fallback to count-of-plausible for sparse heap states.
                    int scanLimit = CHARSEL_MAX_CHARS;
                    if (count > 0 && count < scanLimit) {
                        // Path B/B2 already published a count; trust it as the cap.
                        scanLimit = count;
                    }
                    int highestValidIdx = -1;
                    int plausibleEntries = 0;
                    __try {
                        for (int i = 0; i < scanLimit; i++) {
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
                            highestValidIdx = i;
                            plausibleEntries++;
                            // +0x44 confirmed to be RACE (1=Hum, 11=Halfling, etc), NOT class.
                            // Class and level offsets unknown — leave shm fields untouched.
                            int32_t race = *(const int32_t *)(entry + 0x44);
                            DI8Log("mq2_bridge: heap scan: slot %d = \"%s\" race=%d (cls/lvl unknown)",
                                   i, (const char *)shm->names[i], race);
                        }
                        // v3.22.32: derive count from heap scan when Path B/B2 hasn't.
                        // Use highestValidIdx+1 for contiguous layouts (standard EQ
                        // convention — characters fill slots 0..N-1). Cache in
                        // g_heapScanCount so subsequent polls' count-seed at top of
                        // this block picks it up without re-running this path.
                        if (count == 0 && highestValidIdx >= 0) {
                            count = highestValidIdx + 1;
                            g_heapScanCount = count;
                            DI8Log("mq2_bridge: heap scan: derived count=%d from scan (Path B/B2 didn't publish; %d plausible slots, highest=%d) — closes background-client char-select stall",
                                   count, plausibleEntries, highestValidIdx);
                            // v3.22.32 defensive: EQ char slots are always contiguous
                            // in practice (deleted slots collapse), so highestValidIdx+1
                            // should equal plausibleEntries. Sparse layout would cause
                            // the P9 publisher to bail every poll (intervening empty
                            // slots fail IsPlausibleName). Log loud so the next smoke
                            // surfaces this if it ever happens — better than silent
                            // 30s SM timeout on a never-publishing sparse layout.
                            if (highestValidIdx + 1 != plausibleEntries) {
                                DI8Log("mq2_bridge: heap scan: SPARSE LAYOUT WARN — highestValidIdx+1=%d but plausibleEntries=%d (P9 will bail). Investigate: heap-array stride wrong, or characters at non-contiguous slots?",
                                       highestValidIdx + 1, plausibleEntries);
                            }
                        } else if (count > 0) {
                            // Path B/B2 published count; persist for re-read across polls.
                            g_heapScanCount = count;
                        }
                        // v3.22.22 round-3: charSelectReady=1 latch deferred to
                        // P9's allPlausible block at ~line 4392. Path C's
                        // `continue`-on-failure pattern above (line 4185) can
                        // write `""` to shm->names[i] when heap-scan finds a
                        // partially-populated array; latching here before P9
                        // validates would leave C# seeing charSelectReady=1
                        // paired with stale charCount=0 (R2 T2-Sonnet/T3-Sonnet
                        // CRITICAL convergence). Latch now fires only inside the
                        // P9 success path — atomic with the charCount publish,
                        // mirroring Path A's own atomic publish at L3974-3975.
                    } __except(EXCEPTION_EXECUTE_HANDLER) {
                        DI8Log("mq2_bridge: SEH reading heap-scanned char array");
                        g_heapScanArrayBase = 0;
                        g_heapScanCount = 0;  // v3.22.32: companion reset on SEH
                    }
                } else {
                    // Track B v3 (2026-05-05): full-array scan failed (single-char
                    // accounts can't satisfy threshold-5). Anchor-scan for the
                    // specific char we're trying to log into — read target from
                    // LoginShm.character. C# AutoLoginManager writes that field
                    // before issuing autologin.
                    char targetName[CHARSEL_NAME_LEN] = {};
                    // Track B v3 fix (2026-05-05, T3 Opus v5 callout): wrap the
                    // g_loginShm->magic dereference INSIDE __try too. The pointer
                    // could be non-null with the underlying MMF unmapped (DLL detach
                    // race), in which case the magic-read AVs before the inner __try
                    // covered it. SEH covers the whole chain now.
                    if (g_loginShm) {
                        __try {
                            if (g_loginShm->magic == LOGIN_SHM_MAGIC) {
                                // Volatile snapshot — copy to local before scanning
                                for (int k = 0; k < CHARSEL_NAME_LEN - 1 && k < LOGIN_CHAR_LEN; k++) {
                                    char c = g_loginShm->character[k];
                                    if (!c) break;
                                    targetName[k] = c;
                                }
                            }
                        } __except(EXCEPTION_EXECUTE_HANDLER) {
                            targetName[0] = '\0';
                        }
                    }
                    if (targetName[0]) {
                        uintptr_t entry = HeapScanForTargetName(targetName);
                        if (entry) {
                            // Found the target name's CharSelectInfo entry.
                            // We can't trust slot index from heap (which slot in
                            // the visual list it occupies is unknown without the
                            // array base) — but C# only needs name-match against
                            // targetName to satisfy the c74a766 abort gate. Write
                            // targetName to slot 0 (the highlighted slot per
                            // GetCurSel) so name-match succeeds.
                            __try {
                                // v3.22.6 (T2 Sonnet R1 verifier callout): zero loop
                                // FIRST, then write names[0]. v3.22.5 placed the zero
                                // loop AFTER the names[0] write, which meant a SEH
                                // mid-zero-loop left mixed state: names[0] = target
                                // (already written), names[1..k] zeroed, names[k+1..N-1]
                                // still holding Path B2's "Slot N" synthesis. The
                                // combined publisher's P9 gate then passes (names[0]
                                // is plausible) and publishes the mixed state — exactly
                                // the cosmetic surface area the zero loop was added to
                                // close. Reordering yields strictly safer partial-
                                // failure semantics: if SEH fires mid-zero-loop,
                                // names[0] retains Path B2's "Slot 1" (or whatever was
                                // there), P9 gate rejects it, publisher defers. If SEH
                                // fires after the zero loop completes but during the
                                // names[0] write, names[0] is partial/empty and P9
                                // still rejects. Success path is identical to v3.22.5.
                                for (int i = 1; i < CHARSEL_MAX_CHARS; i++) {
                                    ((char *)shm->names[i])[0] = '\0';
                                    shm->levels[i] = 0;
                                    shm->classes[i] = 0;
                                }
                                int nameLen = 0;
                                while (nameLen < CHARSEL_NAME_LEN - 1 && targetName[nameLen] != '\0')
                                    nameLen++;
                                memcpy((void *)shm->names[0], targetName, nameLen);
                                ((char *)shm->names[0])[nameLen] = '\0';
                                shm->levels[0] = 0;
                                shm->classes[0] = 0;
                                // Track B v3 fix (2026-05-05, red-team Sonnet+Opus
                                // dual callout): force selectedIndex=0 so C# Enter
                                // World fires against slot 0 (where we just wrote
                                // the target name), not against whatever GetCurSel
                                // happened to return from Path B2 (which can be
                                // non-zero on EQ pre-cursor or stale state).
                                // Mirrors the standalone path's explicit assignment.
                                shm->selectedIndex = 0;
                                // v3.22.22 round-3: charSelectReady=1 latch
                                // deferred to P9 (same rationale as the
                                // heap-scan site above — single atomic
                                // publisher for the Path B+C combined chain).
                                int32_t race = *(const int32_t *)(entry + 0x44);
                                DI8Log("mq2_bridge: anchor populated slot 0 = \"%s\" race=%d (single-char fallback)",
                                       targetName, race);
                                // Cache the anchor address for re-read path below
                                g_heapScanArrayBase = (uintptr_t)entry;
                                g_anchorScanCached = true;  // re-read branch will re-pin selectedIndex=0
                                // NOTE 2026-05-23: an earlier v3.22.32 patch forced
                                // count=1 here. REVERTED — user clarified gotquiz
                                // actually has 10 characters (Backup is the target,
                                // sits at slot 2). Forcing count=1 would cause EQ to
                                // log into slot 1 (the wrong character). The anchor
                                // branch is the SINGLE-CHAR fallback; multi-char
                                // accounts need the full-array heap scan to succeed.
                                // The real fix is heap-scan reliability, not muting
                                // the publisher with a fake count.
                                //
                                // v3.22.33 — Gap 1 CRITICAL (T2 Sonnet+Opus
                                // convergent): invalidate Path B2's cached slot
                                // count. The anchor branch zeroed slots 1..N-1 of
                                // shm->names above. If a prior poll's Path B2
                                // probe had populated `g_cachedSlotCount` to N>1
                                // (Dalaya's CListWnd accepts SetCurSel beyond
                                // actual row count, exactly the gotquiz failure
                                // mode 2026-05-23), the very next poll's cached
                                // Path B2 path republishes count=N. P9 then
                                // iterates slots 0..N-1; slot 1 is empty (we
                                // zeroed it); IsPlausibleName fails; allPlausible
                                // false; publisher bails every poll → 30s SM
                                // timeout. Resetting -1 forces Path B2 to either
                                // re-probe (fresh value) or skip entirely (gate
                                // requires g_cachedSlotCount > 0 in cached path).
                                // Single-char accounts (g_cachedSlotCount == 1)
                                // are unaffected — they'd still trigger anchor
                                // legitimately and re-publish count=1 next poll
                                // via the probe.
                                if (g_cachedSlotCount > 1) {
                                    int oldSlotCount = g_cachedSlotCount;
                                    g_cachedSlotCount = -1;
                                    DI8Log("mq2_bridge: anchor-fallback fired with cached_slot_count=%d>1 — invalidating Path B2 cache (anchor-only state cannot satisfy P9 for multi-char; T2 verifier-convergent CRITICAL fix)",
                                           oldSlotCount);
                                }
                            } __except(EXCEPTION_EXECUTE_HANDLER) {
                                DI8Log("mq2_bridge: SEH writing anchor-scan name to SHM");
                            }
                        }
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
                    // Track B v3 fix (2026-05-05, T2 dual red-team v6 callout): when
                    // the cached base came from anchor-scan (single entry, slot 0),
                    // re-pin shm->selectedIndex=0 each poll. Path B2 above unconditionally
                    // sets shm->selectedIndex=GetCurSel earlier in this same poll; without
                    // this re-pin the anchor-scan target's slot would drift to whatever
                    // GetCurSel returns (non-zero on EQ pre-cursor or stale state). For
                    // full-array cached state (g_anchorScanCached=false), curSel from
                    // Path B2 is the user's actual selection — leave it alone.
                    if (g_anchorScanCached) {
                        shm->selectedIndex = 0;
                    }
                    // Invalidate aggressively: any failure (not just all-zero) suggests heap reuse.
                    // Reset g_heapScanDone so the next poll triggers a fresh full scan, not just
                    // a cached re-read against a (now-zero) base address.
                    if (validated < count) {
                        DI8Log("mq2_bridge: heap cache stale (%d/%d names valid) -- rescanning next poll",
                               validated, count);
                        g_heapScanArrayBase = 0;
                        g_heapScanDone = false;
                        g_heapScanCount = 0;  // v3.22.32: companion reset on stale cache
                        // v3.15.2: cache turned stale → next scan should be a clean
                        // full search, not a chunked resume from where we last stopped.
                        g_lastHeapScanAddr = 0;
                    }
                } __except(EXCEPTION_EXECUTE_HANDLER) {
                    DI8Log("mq2_bridge: SEH re-reading heap array -- rescanning next poll");
                    g_heapScanArrayBase = 0;
                    g_heapScanDone = false;
                    g_heapScanCount = 0;  // v3.22.32: companion reset on SEH
                    g_lastHeapScanAddr = 0;
                }
            }

            if (count > 0) {
                // v3.22.4 P9 first-entry plausibility gate: do not publish
                // placeholder names. Path B2's slot-mode synthesis (~lines
                // 3988, 4030) writes "Slot %d" into shm->names[i] when
                // GetItemText returns empty AND no heap-scan path has
                // populated real names yet. Without this gate the publish
                // below surfaces those placeholders to C# (charCount > 0
                // short-circuits AutoLoginManager.cs:1460's char-list wait),
                // and C# bails to Error with "MQ2 heap in slot-mode (N
                // placeholder slot(s))". Mirror of v3.22.3's Path A P8 gate:
                // same IsPlausibleName predicate, same defer-and-retry
                // semantics, same one-shot DI8Log latch reset at the three
                // cycle-reset sites. Multi-character accounts hit this case
                // more often because their settle window is wider than
                // single-char accounts (10-slot gotquiz 2026-05-17 smoke).
                //
                // v3.22.22 widening (T2 Sonnet+Opus gap-audit convergence):
                // also validate entries [1..count-1]. The original entry[0]
                // check caught Path B2's "Slot %d" placeholder case (every
                // slot fails so entry[0] fails) but missed Path C heap-scan's
                // per-entry `continue` pattern at line 4185 (zeros the slot
                // and keeps iterating). Path C can publish entry[0]=real +
                // entry[5]="" when heap-scan finds a partially-populated
                // array — same observed failure shape as Path A's
                // partial-pop bug. Full-entries validation closes that gap.
                bool allPlausible = true;
                int firstBadIdx = -1;
                // v3.22.31 P3c: hoist `i` out of __try so the __except handler
                // can attribute which index faulted. Pre-fix the SEH log
                // hardcoded `shm->names[0]` — misleading when names[7] is the
                // faulting entry (DLL detach race or partial unmap). MSVC SEH
                // spec leaves register-cached locals "undefined" in __except,
                // but practice across /EHa matches the existing pattern used
                // by `firstBadIdx` (also read after __try without volatile).
                int i = 0;
                __try {
                    for (i = 0; i < count && i < CHARSEL_MAX_CHARS; i++) {
                        if (!IsPlausibleName((const uint8_t *)shm->names[i])) {
                            allPlausible = false;
                            firstBadIdx = i;
                            break;
                        }
                    }
                } __except(EXCEPTION_EXECUTE_HANDLER) {
                    // v3.22.5: distinguish SEH from the non-SEH "placeholder or
                    // empty" path. Pre-v3.22.5 the empty __except fell through to
                    // the else-if below which logs "entry[0] is placeholder or
                    // empty" — wrong description if shm->names[i] itself faulted
                    // (DLL detach race, MMF unmapped). One-shot per cycle alongside
                    // g_p9GateLogged so DebugView signal stays high without flooding.
                    // v3.22.31 P3c: log the hoisted `i` for index attribution.
                    // Do NOT re-read shm->names[i] in the __except — even though
                    // the expression is pointer arithmetic (no memory load for
                    // `char names[N][M]` array members), the convergent verifier
                    // finding (4-of-8 agents, 2026-05-22) flagged it as a
                    // recursive-SEH footgun. The index `i` alone is the
                    // load-bearing attribution; pointer value was duplicative.
                    if (!g_p9SehLogged) {
                        g_p9SehLogged = true;
                        DI8Log("mq2_bridge: P9 gate SEH in IsPlausibleName predicate (idx=%d) — treating as not plausible, deferring publish; check for DLL detach race or stale SHM mapping",
                               i);
                    }
                    allPlausible = false;
                }
                if (allPlausible) {
                    MemoryBarrier();
                    shm->charCount = count;
                    // v3.22.22 round-3: own the charSelectReady latch for the
                    // Path B+C combined publisher. Previously Path C latched
                    // unconditionally at L4201/L4279 before this gate ran —
                    // R2 T2-Sonnet CRITICAL: a Path C `continue`-hole would
                    // set charSelectReady=1 paired with this gate's bail
                    // (no charCount publish). Latching here makes the publish
                    // atomic: ready⇔count, mirroring Path A at L3974-3975.
                    shm->charSelectReady = 1;
                    for (int i = count; i < CHARSEL_MAX_CHARS; i++) {
                        ((char *)shm->names[i])[0] = '\0';
                        shm->levels[i] = 0;
                        shm->classes[i] = 0;
                    }
                    charDataRead = true;
                } else if (firstBadIdx == 0 && !g_p9GateLogged) {
                    // Classic v3.22.4 P9 case: entry[0] itself is bad
                    // (slot-mode placeholder or empty). Use the original
                    // log message for diagnostic continuity.
                    g_p9GateLogged = true;
                    DI8Log("mq2_bridge: P9 gate fired (Poll publisher) — entry[0] is placeholder or empty (\"%.10s\"); deferring publish until Path A/C/anchor populates real names. Repeated polls without recovery = both heap scans + Path A all stuck.",
                           (const char *)shm->names[0]);
                } else if (firstBadIdx > 0) {
                    // v3.22.22 new case: entry[0] real but a later entry
                    // failed — Path C heap-scan partial-pop hole, or Path
                    // B row-loop missed a row. Reuse the partial-pop one-shot
                    // flag (same lifecycle as the Path A pre-gate).
                    if (!g_partialPopLogged) {
                        g_partialPopLogged = true;
                        DI8Log("mq2_bridge: P9 gate widened-fire (Poll publisher) — entry[0] real (\"%.10s\") but entry[%d] not plausible (\"%.10s\"); deferring publish + invalidating heap-scan cache so next poll's Path C re-scans from scratch.",
                               (const char *)shm->names[0], firstBadIdx, (const char *)shm->names[firstBadIdx]);
                    }
                    // v3.22.22 round-3 (R2 T3-Sonnet SEV-1): invalidate the
                    // heap-scan cache so Path C re-runs HeapScanForCharArray
                    // on the next 500ms poll. Without this, g_heapScanDone
                    // stays true and Path C only ever falls through to the
                    // re-read path at L4295 — which uses the same array
                    // base and would keep hitting the same hole. Resetting
                    // matches the heap-cache-stale path at L4329-4330 which
                    // already does this for the validated<count case.
                    g_heapScanArrayBase = 0;
                    g_heapScanDone = false;
                    g_heapScanCount = 0;  // v3.22.33 Gap 3 (T2): companion reset (was missing here)
                    g_lastHeapScanAddr = 0;
                }
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
    //
    // 2026-05-05 server-select-timeout fix: gate standalone path on pinstC-
    // CharacterSelect actually being non-null. The 1500ms HeapScanForCharArray
    // + 1500ms HeapScanForTargetName run on the EQ game thread (via the
    // GiveTime detour) and block WindowMessages from being processed. If a
    // user's login phase exceeds 10s (slow disk/network), the 20-poll delay
    // expires while we're still at server-select; standalone scan fires,
    // game thread freezes ~3s per cycle in a tight loop, EQ misses the
    // BURST 2 Enter keystroke, server-select never advances → 90s C# screen-
    // transition timeout. g_consecutiveNullPolls (incremented at top of Poll
    // when pinst is null, reset to 0 when pinst is non-null) is the cleanest
    // gate: skip the scan entirely until we have visual evidence of being at
    // charselect. Path B's pCharList scan is the fast path once pinst lights
    // up; standalone is the slow fallback that should only fire when pCharList
    // is null DESPITE pinst being non-null.
    // v3.15.11 INVARIANT (mirror of g_consecutiveNullPolls pin above): the
    // 20-poll g_standaloneDelay threshold ALSO assumes one increment per
    // 500ms cadence (= 10s wall-clock). Same caveat — must stay inside
    // MQ2Bridge::Poll, NEVER moved into PollRequestsOnly. Convert to
    // wall-clock timestamp first if you ever want this gate to run on
    // the fast path.
    if (!charDataRead && !g_heapScanDone) {
        if (g_consecutiveNullPolls > 0) {
            // pinst null this poll — login or server-select phase. Skip the
            // 1.5s scan that would freeze the game thread. Resume when pinst
            // populates (transition reset will also zero g_standaloneDelay).
        } else if (g_standaloneDelay < 20) {
            if (g_standaloneDelay == 0 || g_standaloneDelay == 10 || g_standaloneDelay == 19)
                DI8Log("mq2_bridge: standalone delay %d/20 (heapScanDone=%d)", g_standaloneDelay, (int)g_heapScanDone);
            g_standaloneDelay++;
            // fall through to charDataRead=false → charCount=0
        } else {
            g_heapScanDone = true;
            uintptr_t arrayBase = HeapScanForCharArray();
            if (arrayBase) {
                g_heapScanArrayBase = arrayBase;
                g_anchorScanCached = false;  // full-array path: real list, real curSel
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
                    g_heapScanCount = 0;  // v3.22.33 Gap 3 (T2): companion reset (was missing here)
                }
                if (count > 0) {
                    MemoryBarrier();
                    shm->charCount = count;
                    shm->charSelectReady = 1;  // v3 latch: standalone heap scan wrote real names
                    for (int i = count; i < CHARSEL_MAX_CHARS; i++) {
                        ((char *)shm->names[i])[0] = '\0';
                        shm->levels[i] = 0;
                        shm->classes[i] = 0;
                    }
                    charDataRead = true;
                    DI8Log("mq2_bridge: heap scan populated %d characters (Path A+B both failed)", count);
                }
            } else {
                // Track B v3 (2026-05-05): standalone full-array scan failed.
                // Anchor-scan for the target char from LoginShm.character.
                // Same fallback as Path C — handles single-char accounts.
                char targetName[CHARSEL_NAME_LEN] = {};
                // Track B v3 fix (2026-05-05, T3 Opus v5 callout): wrap magic check
                // inside __try — see Path C site for rationale.
                if (g_loginShm) {
                    __try {
                        if (g_loginShm->magic == LOGIN_SHM_MAGIC) {
                            for (int k = 0; k < CHARSEL_NAME_LEN - 1 && k < LOGIN_CHAR_LEN; k++) {
                                char c = g_loginShm->character[k];
                                if (!c) break;
                                targetName[k] = c;
                            }
                        }
                    } __except(EXCEPTION_EXECUTE_HANDLER) {
                        targetName[0] = '\0';
                    }
                }
                if (targetName[0]) {
                    uintptr_t entry = HeapScanForTargetName(targetName);
                    if (entry) {
                        __try {
                            int nameLen = 0;
                            while (nameLen < CHARSEL_NAME_LEN - 1 && targetName[nameLen] != '\0')
                                nameLen++;
                            memcpy((void *)shm->names[0], targetName, nameLen);
                            ((char *)shm->names[0])[nameLen] = '\0';
                            shm->levels[0] = 0;
                            shm->classes[0] = 0;
                            for (int i = 1; i < CHARSEL_MAX_CHARS; i++) {
                                ((char *)shm->names[i])[0] = '\0';
                                shm->levels[i] = 0;
                                shm->classes[i] = 0;
                            }
                            MemoryBarrier();
                            shm->charCount = 1;
                            shm->charSelectReady = 1;  // v3 latch: standalone anchor wrote real name
                            shm->selectedIndex = 0;
                            charDataRead = true;
                            int32_t race = *(const int32_t *)(entry + 0x44);
                            DI8Log("mq2_bridge: anchor scan populated slot 0 = \"%s\" race=%d (standalone, single-char)",
                                   targetName, race);
                            g_heapScanArrayBase = (uintptr_t)entry;
                            g_anchorScanCached = true;  // re-read branch will re-pin selectedIndex=0
                        } __except(EXCEPTION_EXECUTE_HANDLER) {
                            DI8Log("mq2_bridge: SEH writing standalone anchor-scan to SHM");
                        }
                    }
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
            g_heapScanCount = 0;  // v3.22.33 Gap 3 (T2): companion reset (was missing here)
            g_lastHeapScanAddr = 0;
        }
        if (count > 0) {
            MemoryBarrier();
            shm->charCount = count;
            charDataRead = true;
        } else {
            // Cache stale, rescan next poll
            g_heapScanArrayBase = 0;
            g_heapScanDone = false;
            g_heapScanCount = 0;  // v3.22.33 Gap 3 (T2): companion reset (was missing here)
            g_lastHeapScanAddr = 0;
        }
    }

    if (!charDataRead) {
        shm->charCount = 0;
        shm->selectedIndex = -1;
    }

    // One-shot verification report on first successful charselect load
    if (charDataRead && !g_verificationDone)
        EmitVerificationReport(shm);

    // v3.15.11: selection request handler moved up — see HandleSelectionRequest
    // call site near top of Poll. Running it before the heap-scan paths means a
    // mid-cycle SetCurSel doesn't have to wait for the slow scans to re-run.
}

// ─── MQ2Bridge::PollRequestsOnly (v3.15.11 fast-path) ──────────
//
// Two-tier throttle hook: invoked from MQ2BridgePollTick on unthrottled ticks
// (~16ms cadence via ActivateThread + TIMERPROC) when a pending C# request is
// detected in SHM but the 500ms full-poll throttle has not yet expired.
//
// What this DOES:
//   - HandleEnterWorldRequest (CLW_EnterWorldButton click via WndNotification)
//   - HandleSelectionRequest (Character_List SetCurSel)
//
// What this DOES NOT do (those stay in MQ2Bridge::Poll on the 500ms cadence):
//   - shm->mq2Available / shm->gameState publish (full Poll already wrote them)
//   - gameState transition logging (full Poll has lastGameState static)
//   - g_consecutiveNullPolls increment + charSelectReady latch clear
//     (counter assumes 500ms cadence — see invariant pin in Poll body)
//   - g_standaloneDelay tick (also assumes 500ms cadence)
//   - gameState==5 reset block (only reachable on full poll; if we reach
//     in-game during a fast-path tick, the EW handler drops the request
//     with -2 and the next full poll handles the cleanup)
//   - Char data reads (Path A/B/B2/C/standalone) and verification report
//
// Pre-conditions (validated by caller MQ2BridgePollTick):
//   - shm != nullptr
//   - g_pGameState + g_ppEverQuest non-null (MQ2Bridge::Init succeeded)
//
// Race + reentry: PollReentryGuard at MQ2BridgePollTick top serializes calls.
// Within a single eqgame.exe process this function never overlaps with Poll
// or with itself.
void MQ2Bridge::PollRequestsOnly(volatile CharSelectShm *shm) {
    if (!shm) return;
    if (!g_pGameState || !g_ppEverQuest) return;

    int gameState = ReadGameState();
    HandleEnterWorldRequest(shm, gameState);
    HandleSelectionRequest(shm);
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

    g_wndMgrOffsetFound = false;
    g_wndMgrValidOffset = 0;
    g_eqmainWndMgrOffset = 0;
    g_uiFallbackLogged = false;
    g_p8GateLogged     = false;
    g_p9GateLogged     = false;
    g_p9SehLogged      = false;
    g_partialPopLogged = false;
    g_cachedNameCol    = -1;
    g_verificationDone = false;
    g_findLogCount     = 0;

    // Hotfix v4: reset slot cache + heap-scan base so a mid-process MQ2 re-init
    // doesn't serve a stale count from the previous session's charselect.
    g_cachedSlotCount = -1;
    g_heapScanArrayBase = 0;
    g_heapScanCount = 0;  // v3.22.32: companion reset to keep the seed expression honest

    // Track B fix (2026-05-05, T2 Opus verifier callout): reset the heap-scan-done
    // flag so a mid-process MQ2 re-init doesn't land in an Init() with
    // g_heapScanDone=true from the previous cycle, which would permanently
    // lock off Path C's heap scan until the next pinstCCharacterSelect
    // transition — which may not fire promptly if the new char-select
    // instance is resolved at the same heap address as the previous one
    // (heap reuse). v3.22.2: the sibling g_charArrayNotFoundLogged reset
    // was removed with the dead validator machinery.
    g_heapScanDone = false;
    g_anchorScanCached = false;
    g_standaloneDelay = 0;
    g_consecutiveNullPolls = 0;
    // v3.15.2: clear chunked-resume cursors on Init() (mid-process MQ2 re-init).
    g_lastHeapScanAddr = 0;
    g_lastAnchorScanAddr = 0;
    g_lastAnchorScanName[0] = '\0';
}

// ─── LoginServerAPI::JoinServer (Diff 4) ─────────────────────
// In-process __thiscall to eqmain's LoginServerAPI::JoinServer at fixed
// RVA 0x13C30, on the LoginServerAPI instance at *(eqmain+0x150164).
//
// Why a primitive (not yet wired into autologin):
//   - C# AutoLoginManager.cs / login_state_machine.cpp are dirty in the
//     v3.18.0 working tree (OK_Display SHM mirror in flight). Wiring this
//     into the FSM would tangle with that work. We ship the primitive;
//     the FSM call site lands in v3.19+.
//   - Provides a clean callable surface that other native code (e.g., a
//     future GiveTime-detour-driven server-select replacement) can use
//     immediately.
//
// 2026-05-15 R1 verifier-pair sweep findings addressed in this revision:
//   1. Vanishing-failure pattern (Rule 12): outResult* surfaces the actual
//      JoinServer return code; bool return now means "dispatched cleanly,
//      result is valid", not "this called something somewhere".
//   2. /OPT:REF anchor durability: the address-take in Init() now stores
//      to a `volatile` file-static — guaranteed not optimizable away under
//      any /O2 + /OPT:REF + /OPT:ICF + /LTCG combination.
//   3. Prologue-byte sanity: validates the JoinServer fn at +0x13C30
//      starts with a known x86 thiscall prologue byte (0x55 push ebp /
//      0x53 push ebx / 0x56 push esi / 0x57 push edi / 0x83 sub esp,N).
//      Refuses + logs the actual byte if patched.
//   4. eqmain unload TOCTOU: LoadLibraryA pin bumps refcount across the
//      function's lifetime; FreeLibrary releases on every exit path.
//      Closes the (small but real) window where eqmain could unload
//      between GetModuleHandleA and the actual call.
typedef unsigned int (__thiscall *FN_JoinServer)(void *thisPtr, int serverID,
                                                 void *userdata, int timeoutSeconds);

bool MQ2Bridge::JoinServerDirect(int serverID, unsigned int *outResult) {
    // R3 fix (T2-Opus #1, T2-Sonnet C1 convergent): NO sentinel write here.
    // Per idiomatic out-param contract: outResult is written ONLY when this
    // function returns true. On false return, outResult is untouched —
    // caller's pre-call value is preserved. This eliminates:
    //   (a) Unguarded write to potentially invalid caller-supplied pointer
    //   (b) Sentinel-collision risk with valid EQ codes (R2 used 0xFFFFFFFE
    //       which equals (unsigned)-2 — a plausible JoinServer error code).
    // Caller MUST init their own outResult variable before calling, AND
    // check the bool return BEFORE reading outResult. Header documents this.

    // R3 fix (T2-Sonnet C2): cross-check via GetModuleHandleA BEFORE
    // LoadLibraryA. GetModuleHandleA returns the existing module handle
    // without bumping refcount or triggering DLL search-order (no planted
    // eqmain.dll on PATH/cwd will be loaded). If it returns null, eqmain
    // genuinely isn't in the process — we refuse without LoadLibraryA at
    // all. If non-null, we then LoadLibraryA for the refcount bump and
    // verify it returned the SAME HMODULE — a different handle would
    // indicate either DLL search-order hijack or a multi-load race.
    HMODULE hEqmainCheck = GetModuleHandleA("eqmain.dll");
    if (!hEqmainCheck) {
        DI8Log("mq2_bridge: JoinServerDirect — eqmain.dll not in process "
               "(GetModuleHandleA returned null)");
        return false;
    }

    // R2 fix: pin eqmain.dll across the function lifetime via LoadLibraryA
    // refcount bump. If eqmain was unloaded between callers' invocations,
    // LoadLibraryA returns null (file not in process); if loaded, increments
    // the loader-managed refcount so a competing thread's FreeLibrary doesn't
    // drop it during our call. FreeLibrary balances on every exit path.
    HMODULE hEqmain = LoadLibraryA("eqmain.dll");
    if (!hEqmain) {
        DI8Log("mq2_bridge: JoinServerDirect — LoadLibraryA(eqmain.dll) "
               "failed despite GetModuleHandleA succeeding (race with unload?)");
        return false;
    }

    // R3 fix (T2-Sonnet C2): handle mismatch ⇒ DLL planting / search-order
    // hijack — refuse and FreeLibrary the unexpected module.
    if (hEqmain != hEqmainCheck) {
        DI8Log("mq2_bridge: JoinServerDirect — HMODULE mismatch: "
               "GetModuleHandleA=%p LoadLibraryA=%p — possible DLL planting; "
               "refusing call",
               hEqmainCheck, hEqmain);
        FreeLibrary(hEqmain);
        return false;
    }
    uintptr_t eqmainBase = (uintptr_t)hEqmain;

    // Read LoginServerAPI* from pinstLoginServerAPI (eqmain+0x150164).
    void *pAPI = nullptr;
    __try {
        pAPI = *(void **)(eqmainBase + EQMainOffsets::RVA_PINST_LoginServerAPI);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: JoinServerDirect — SEH reading pinstLoginServerAPI");
        FreeLibrary(hEqmain);
        return false;
    }
    if (!pAPI) {
        DI8Log("mq2_bridge: JoinServerDirect — pinstLoginServerAPI is NULL "
               "(eqmain init incomplete?)");
        FreeLibrary(hEqmain);
        return false;
    }

    // Sanity gate: pointee's vtable[0] must match the documented
    // LoginServerAPI secondary vtable at eqmain+0x1002D0. Mismatch ⇒
    // the global at +0x150164 is something other than LoginServerAPI
    // (Dalaya patch shifted layout? heap-garbage from a wrong RVA?) and
    // calling JoinServer on it would crash.
    void *vtable = nullptr;
    __try {
        vtable = *(void **)pAPI;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: JoinServerDirect — SEH reading vtable of pAPI=%p",
               pAPI);
        FreeLibrary(hEqmain);
        return false;
    }
    uintptr_t expectedVtable = eqmainBase + EQMainOffsets::RVA_VTABLE_LoginServerAPI_Secondary;
    if ((uintptr_t)vtable != expectedVtable) {
        // R1 fix: log the actual vtable RVA so a post-Dalaya-patch debug run
        // can identify the new value without a disassembler.
        uintptr_t actualRva = (uintptr_t)vtable - eqmainBase;
        DI8Log("mq2_bridge: JoinServerDirect — vtable mismatch: pAPI=%p "
               "vtable=%p (eqmain+0x%06X) expected=%p (eqmain+0x%06X) — refusing call",
               pAPI, vtable, (unsigned)actualRva,
               (void *)expectedVtable,
               (unsigned)EQMainOffsets::RVA_VTABLE_LoginServerAPI_Secondary);
        FreeLibrary(hEqmain);
        return false;
    }

    // Resolve the function pointer.
    FN_JoinServer pJoinServer = (FN_JoinServer)(eqmainBase +
        EQMainOffsets::RVA_FN_LoginServerAPI_JoinServer);

    // R1 fix: prologue-byte sanity check. The vtable gate proves pAPI is a
    // LoginServerAPI instance; this gate proves the bytes at +0x13C30 still
    // look like an x86 function prologue. Defends against future Dalaya
    // patches that shift the function address or anti-cheat hooks that
    // INT3-trap or RET-stub the entry. Common x86 thiscall prologues:
    //   0x55       push ebp           (most common; emu-branch JoinServer uses this)
    //   0x53       push ebx           (occasional)
    //   0x56       push esi           (occasional)
    //   0x57       push edi           (occasional)
    //   0x83       sub esp, N         (no-frame-pointer optimized prologue)
    //   0x8B       mov reg, X         (frame-omitted; rare for non-trivial fns)
    //   0x6A       push imm8          (immediate args before frame setup)
    unsigned char firstByte = 0;
    __try {
        firstByte = *(const unsigned char *)pJoinServer;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: JoinServerDirect — SEH reading prologue at fn=%p "
               "(eqmain+0x%X)", pJoinServer,
               (unsigned)EQMainOffsets::RVA_FN_LoginServerAPI_JoinServer);
        FreeLibrary(hEqmain);
        return false;
    }
    if (firstByte != 0x55 && firstByte != 0x53 && firstByte != 0x56 &&
        firstByte != 0x57 && firstByte != 0x83 && firstByte != 0x8B &&
        firstByte != 0x6A) {
        DI8Log("mq2_bridge: JoinServerDirect — prologue mismatch at fn=%p "
               "(eqmain+0x%X): first byte 0x%02X is not a known x86 prologue "
               "(expected one of 0x55/53/56/57/83/8B/6A) — refusing call",
               pJoinServer,
               (unsigned)EQMainOffsets::RVA_FN_LoginServerAPI_JoinServer,
               firstByte);
        FreeLibrary(hEqmain);
        return false;
    }

    // Per emu-branch StateMachine.cpp:773, MQ2 always calls (serverID, nullptr, 30).
    DI8Log("mq2_bridge: JoinServerDirect — dispatching pAPI=%p fn=%p "
           "serverID=%d userdata=NULL timeout=30 prologue=0x%02X",
           pAPI, pJoinServer, serverID, firstByte);

    unsigned int result = 0;
    bool dispatched = false;
    __try {
        result = pJoinServer(pAPI, serverID, nullptr, 30);
        dispatched = true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: JoinServerDirect — SEH inside JoinServer call "
               "(serverID=%d, code=0x%08X)",
               serverID, GetExceptionCode());
        // dispatched stays false; outResult is UNTOUCHED per R3 idiomatic
        // out-param contract (caller's pre-call value preserved on false
        // return — no sentinel write that could collide with valid EQ
        // result codes like (unsigned)-2 == 0xFFFFFFFE)
    }

    if (dispatched) {
        if (outResult) *outResult = result;
        DI8Log("mq2_bridge: JoinServerDirect — call returned 0x%08X (no SEH)", result);
    }

    FreeLibrary(hEqmain);
    return dispatched;
}
