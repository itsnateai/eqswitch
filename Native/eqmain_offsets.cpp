// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// Native/eqmain_offsets.cpp -- eqmain.dll range tracking + ownership tests
//
// STEP 2A of the v8 MQ2 port. See eqmain_offsets.h for the full rationale.
// This file implements the infrastructure; function-pointer resolution is
// deferred to STEP 2B once we have empirical evidence from Step 2A logs.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include "eqmain_offsets.h"

// DI8Log is defined in eqswitch-di8.cpp; forward-declare so this TU can use
// the same log sink without pulling in di8_proxy or MinHook headers.
void DI8Log(const char *fmt, ...);

namespace EQMainOffsets {

// Snapshot-stable globals. Updated only under loader lock (from the DLL
// notification callback in eqswitch-di8.cpp) and published via a release
// fence implicit in InterlockedExchange. Readers see either {0,0} or a
// coherent {base,size} pair — never a torn update.
static volatile uintptr_t g_base = 0;
static volatile uint32_t  g_size = 0;

// Forward decl — defined in the mq2port section below. Referenced by
// InvalidateStep3Caches which runs at UNLOAD time.
extern void *volatile g_cachedCSidlMgr;

void OnEQMainLoaded(uintptr_t dllBase, uint32_t sizeOfImage) {
    if (!dllBase || !sizeOfImage) {
        DI8Log("eqmain_offsets: LOAD ignored — invalid (base=0x%08X size=0x%X)",
               (unsigned)dllBase, sizeOfImage);
        return;
    }

    // Idempotent same-base reload (e.g. synthetic event after registration).
    if (g_base == dllBase && g_size == sizeOfImage) {
        DI8Log("eqmain_offsets: LOAD already tracked (base=0x%08X size=0x%X)",
               (unsigned)dllBase, sizeOfImage);
        return;
    }

    // Different-base reload is unexpected (DLL can only be loaded once
    // per process until it's unloaded). Log and continue — last writer wins.
    if (g_base != 0) {
        DI8Log("eqmain_offsets: LOAD warning — base changed without prior UNLOAD "
               "(was 0x%08X+0x%X, now 0x%08X+0x%X)",
               (unsigned)g_base, g_size, (unsigned)dllBase, sizeOfImage);
    }

    // Publish size first, then base. Readers that see non-zero base
    // always see a valid size thanks to the MemoryBarrier implied by
    // InterlockedExchange.
    InterlockedExchange((volatile LONG *)&g_size, (LONG)sizeOfImage);
    InterlockedExchangePointer((PVOID volatile *)&g_base, (PVOID)dllBase);

    DI8Log("eqmain_offsets: LOAD tracked — range 0x%08X-0x%08X (size 0x%X)",
           (unsigned)dllBase, (unsigned)(dllBase + sizeOfImage), sizeOfImage);

    // Resolve MQ2-faithful widget-lookup function pointer now that eqmain is
    // loaded. The CXWnd::GetChildItem export lives in Dalaya's dinput8.dll
    // (Dalaya MQ2, 1.3MB proxy) which is loaded eagerly by eqgame's import
    // table, so GetModuleHandle is guaranteed to succeed by this point.
    //
    // Also install the pSidlMgr swap — point dinput8's pSidlMgr global at
    // eqmain's CSidlManagerBase instance so GetChildItem walks the live
    // login-frontend tree instead of using eqgame's (null during login)
    // CSidlManager. InstallPSidlMgrSwap may return false if the CSidlManager
    // isn't constructed yet — FindWidgetByKnownName retries on each call.
    ResolveMQ2FaithfulFunctions();
    InstallPSidlMgrSwap();
}

// Forward decl — defined below in the Step 3 section. Called from
// OnEQMainUnloaded so the cached CXWndManager pointer doesn't outlive
// the module that owns it.
static void InvalidateStep3Caches();

void OnEQMainUnloaded() {
    uintptr_t priorBase = g_base;
    uint32_t priorSize = g_size;

    // Restore dinput8's pSidlMgr BEFORE invalidating our caches. The swap
    // was pointing at eqmain's CSidlManager instance; once eqmain unloads,
    // that pointer is dangling, so we must restore the prior (eqgame-side)
    // value or the next GetChildItem call will crash.
    RestorePSidlMgrSwap();

    // Clear base first so concurrent readers see {0, old_size} — which still
    // yields IsEQMainWidget=false because the range test uses base as the gate.
    InterlockedExchangePointer((PVOID volatile *)&g_base, nullptr);
    InterlockedExchange((volatile LONG *)&g_size, 0);

    // Step 3 caches point into the (now-unloaded) eqmain data region. Dropping
    // them before any reader calls FindLiveCXWndManager() prevents returning
    // a dangling pointer after an eqmain reload grabs a different address.
    InvalidateStep3Caches();

    if (priorBase != 0) {
        DI8Log("eqmain_offsets: UNLOAD cleared range 0x%08X-0x%08X",
               (unsigned)priorBase, (unsigned)(priorBase + priorSize));
    }
}

void GetRange(uintptr_t *outBase, uint32_t *outSize) {
    // Snapshot both values via a single pair of reads. A race with
    // OnEQMainUnloaded can yield {0, old_size} but IsEQMainWidget-style
    // callers treat base=0 as "not loaded" so this is safe.
    uintptr_t base = g_base;
    uint32_t  size = g_size;
    if (outBase) *outBase = base;
    if (outSize) *outSize = size;
}

// ─── Step 2B: vtable-slot fn-ptr resolution ──────────────────
// Shared resolver for any eqmain::CXWnd virtual method. Returns the
// function pointer stored in the widget's vtable at `slotOffset`, or
// nullptr if any check fails. See eqmain_offsets.h for the threat
// model and defenses.
//
// Concurrency: this is called from arbitrary game-loop threads while
// eqmain is loaded. The IsEQMainWidget() call inside does its own
// snapshot-stable read of g_base/g_size; the post-read range check
// on the resolved slot re-snapshots to guard against a LOAD→UNLOAD
// race between the IsEQMainWidget test and slot read. Worst case on
// a race is we hand back a slot pointer that WAS valid at snapshot
// time; caller's __try/__except catches a crash, caller falls back.
static void *ResolveVtableSlot(const void *pWnd, uint32_t slotOffset) {
    if (!IsEQMainWidget(pWnd)) return nullptr;

    // Re-read the vtable pointer. Cheaper and race-safer than caching
    // from IsEQMainWidget — that read happened under a different SEH
    // frame and the result was discarded.
    uintptr_t vtable = 0;
    __try {
        vtable = *(const uintptr_t *)pWnd;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
    if (!vtable) return nullptr;

    // Read the function pointer at vtable + slotOffset.
    uintptr_t slot = 0;
    __try {
        slot = *(const uintptr_t *)(vtable + slotOffset);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
    if (!slot) return nullptr;

    // Defense: the resolved slot must point inside eqmain's code range.
    // If Dalaya patched the vtable layout (inserted a new virtual so slot
    // numbering drifted), slot may point at arbitrary data in .rdata or
    // an entirely different module. Rather than hand back a bogus fn
    // pointer that might corrupt state before faulting, bail and let the
    // caller fall back to the eqgame-side function.
    uintptr_t base = 0;
    uint32_t  size = 0;
    GetRange(&base, &size);
    if (!base || !size) return nullptr;  // unloaded between IsEQMainWidget and here
    if (slot < base || slot >= base + size) {
        // Log ONCE per (vtable, slotOffset) combo so operators notice
        // without a flooded log. A static set in C would be painful;
        // instead we log per-call at low rate — login hits this path
        // ~2x per SetEditText and ~1-60x per ClickButton, volume is
        // bounded and the log line is the smoking-gun anomaly signal.
        DI8Log("eqmain_offsets: slot rejected — vtable=0x%08X off=0x%X slot=0x%08X outside range 0x%08X-0x%08X",
               (unsigned)vtable, slotOffset, (unsigned)slot,
               (unsigned)base, (unsigned)(base + size));
        return nullptr;
    }

    return (void *)slot;
}

// ─── Widget class validation (exact vtable match) ────────────
// Reads pWnd's vtable pointer (SEH-wrapped) and compares against the
// runtime-rebased known-widget vtable addresses. Returns false for
// any mismatch including fault during deref.

static bool ReadVtablePtr(const void *pWnd, uintptr_t *outVt) {
    if (!pWnd) return false;
    __try {
        *outVt = *(const uintptr_t *)pWnd;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool VtableMatchesRVA(const void *pWnd, uint32_t rva) {
    uintptr_t base = g_base;
    if (!base) return false;
    uintptr_t vt = 0;
    if (!ReadVtablePtr(pWnd, &vt)) return false;
    return vt == (base + rva);
}

bool IsEQMainWidgetClass(const void *pWnd) {
    uintptr_t base = g_base;
    if (!base) return false;
    uintptr_t vt = 0;
    if (!ReadVtablePtr(pWnd, &vt)) return false;
    // Widget-family vtables. CXMLDataPtr / CXMLData / etc are deliberately
    // NOT in this list because they have the CXStr-at-+0x18 shape but
    // aren't actual wnds — calling SetWindowText on them corrupts state.
    const uint32_t rvas[] = {
        RVA_VTABLE_CXWnd,
        RVA_VTABLE_CSidlScreenWnd,
        RVA_VTABLE_CButtonWnd,
        RVA_VTABLE_CEditBaseWnd,
        RVA_VTABLE_CEditWnd,
        RVA_VTABLE_CListWnd,
        RVA_VTABLE_CLabelWnd,
    };
    for (uint32_t rva : rvas) {
        if (vt == base + rva) return true;
    }
    return false;
}

bool IsEQMainEditWidget(const void *pWnd) {
    // CEditWnd is the live edit widget. CEditBaseWnd is abstract base —
    // should not appear as a live vtable but included for belt-and-braces.
    return VtableMatchesRVA(pWnd, RVA_VTABLE_CEditWnd)
        || VtableMatchesRVA(pWnd, RVA_VTABLE_CEditBaseWnd);
}

bool IsEQMainButtonWidget(const void *pWnd) {
    return VtableMatchesRVA(pWnd, RVA_VTABLE_CButtonWnd);
}

const char *GetEQMainWidgetClassName(const void *pWnd) {
    uintptr_t base = g_base;
    if (!base) return nullptr;
    uintptr_t vt = 0;
    if (!ReadVtablePtr(pWnd, &vt)) return nullptr;
    struct Entry { uint32_t rva; const char *name; };
    static const Entry table[] = {
        { RVA_VTABLE_CXWnd,          "CXWnd"          },
        { RVA_VTABLE_CSidlScreenWnd, "CSidlScreenWnd" },
        { RVA_VTABLE_CButtonWnd,     "CButtonWnd"     },
        { RVA_VTABLE_CEditBaseWnd,   "CEditBaseWnd"   },
        { RVA_VTABLE_CEditWnd,       "CEditWnd"       },
        { RVA_VTABLE_CListWnd,       "CListWnd"       },
        { RVA_VTABLE_CLabelWnd,      "CLabelWnd"      },
    };
    for (const auto &e : table) {
        if (vt == base + e.rva) return e.name;
    }
    return nullptr;
}

FN_WndNotification GetWndNotificationFor(const void *pWnd) {
    // Defense-in-depth: require a known widget vtable before handing out
    // a slot pointer. If the caller has a CXMLDataPtr definition (common
    // outcome of Phase 5 heap-scan), this returns nullptr and the caller
    // falls back to its eqgame-side pointer (which will also SEH but at
    // least won't stack-imbalance).
    if (!IsEQMainWidgetClass(pWnd)) return nullptr;
    return (FN_WndNotification)ResolveVtableSlot(pWnd, VTABLE_OFFSET_WndNotification);
}

FN_SetWindowText GetSetWindowTextFor(const void *pWnd) {
    // SetWindowText is only safe on edit-family widgets (slot 73's thunk
    // does `add ecx,0x1A8` expecting the WindowText CXStr member at that
    // offset; for non-edit widgets that offset is arbitrary bytes).
    // CButtonWnd and CSidlScreenWnd technically have the inherited thunk
    // too, but Caller is SetEditText, so restrict to edit widgets.
    if (!IsEQMainEditWidget(pWnd)) return nullptr;
    return (FN_SetWindowText)ResolveVtableSlot(pWnd, VTABLE_OFFSET_SetWindowText);
}

// ─── Step 2B-diag: one-shot vtable dump ──────────────────────
// See eqmain_offsets.h for the contract. Goal: identify the correct
// vtable slot for SetWindowText (edit widgets) and WndNotification
// (buttons) by dumping every slot's fn ptr + prologue bytes, which
// we then eyeball against the known eqgame-side export prologue.

// Helpers to read memory without faulting.
static bool SafeReadPtr(const void *addr, uintptr_t *out) {
    __try {
        *out = *(const uintptr_t *)addr;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
static bool SafeReadBytes(const void *addr, uint8_t *out, size_t n) {
    __try {
        memcpy(out, addr, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void DumpVtableForDiagnostics(const void *pWnd, const char *widgetKind,
                              uintptr_t referenceExport) {
    if (!pWnd || !widgetKind) return;
    if (!IsEQMainWidget(pWnd)) return;

    uintptr_t vtable = 0;
    if (!SafeReadPtr(pWnd, &vtable) || !vtable) return;

    // Dedup: only dump each vtable ONCE per process. 8 slots is plenty
    // for login widgets (editbox, button, screen, maybe a few more).
    static uintptr_t s_dumped[8] = {};
    static int s_dumpedCount = 0;
    for (int i = 0; i < s_dumpedCount; i++) {
        if (s_dumped[i] == vtable) return;
    }
    if (s_dumpedCount >= 8) return;
    s_dumped[s_dumpedCount++] = vtable;

    uintptr_t base = 0; uint32_t size = 0;
    GetRange(&base, &size);
    if (!base || !size) return;

    DI8Log("==== VTABLE DUMP [%s] pWnd=%p vtable=0x%08X (eqmain+0x%05X) ====",
           widgetKind, pWnd, (unsigned)vtable, (unsigned)(vtable - base));

    // Dump the reference eqgame-side export's prologue for signature matching.
    if (referenceExport) {
        uint8_t refBytes[12] = {};
        if (SafeReadBytes((const void *)referenceExport, refBytes, 12)) {
            DI8Log("  REFERENCE (eqgame-side) @ 0x%08X: %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X",
                   (unsigned)referenceExport,
                   refBytes[0], refBytes[1], refBytes[2],  refBytes[3],
                   refBytes[4], refBytes[5], refBytes[6],  refBytes[7],
                   refBytes[8], refBytes[9], refBytes[10], refBytes[11]);
        }
    }

    // Dump up to 180 slots (CXWnd_vftable_size 0x2D0 at 8 bytes/slot = 90 slots
    // in 64-bit MQ2 upstream; at 4 bytes/slot = 180 slots in 32-bit Dalaya).
    // Skip null slots (unused/reserved). Stop early on two consecutive faults.
    int consecFaults = 0;
    for (int slot = 0; slot < 180; slot++) {
        uintptr_t fn = 0;
        if (!SafeReadPtr((const void *)(vtable + slot * 4), &fn)) {
            if (++consecFaults >= 2) break;
            continue;
        }
        consecFaults = 0;
        if (!fn) continue;

        bool inRange = (fn >= base && fn < base + size);
        uint8_t prologue[12] = {};
        bool prologueOk = SafeReadBytes((const void *)fn, prologue, 12);

        if (prologueOk) {
            DI8Log("  slot[%3d] off=0x%04X fn=0x%08X %s%s prologue=%02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X",
                   slot, slot * 4, (unsigned)fn,
                   inRange ? "(eqmain+0x" : "(OUT-OF-RANGE",
                   inRange ? "" : ")",
                   prologue[0], prologue[1], prologue[2],  prologue[3],
                   prologue[4], prologue[5], prologue[6],  prologue[7],
                   prologue[8], prologue[9], prologue[10], prologue[11]);
            if (inRange) {
                DI8Log("              -> RVA=0x%05X", (unsigned)(fn - base));
            }
        } else {
            DI8Log("  slot[%3d] off=0x%04X fn=0x%08X (PROLOGUE UNREADABLE)",
                   slot, slot * 4, (unsigned)fn);
        }
    }
    DI8Log("==== END VTABLE DUMP [%s] vtable=0x%08X ====",
           widgetKind, (unsigned)vtable);
}

bool IsEQMainWidget(const void *pWnd) {
    if (!pWnd) return false;

    uintptr_t base = g_base;
    uint32_t  size = g_size;
    if (!base || !size) return false;  // eqmain not loaded — cannot be eqmain widget

    // Read the vtable pointer out of the widget. The vtable pointer is the
    // first machine word of any polymorphic C++ object. SEH-wrap because
    // pWnd can be stale (charselect→enter-world widgets get freed when
    // eqmain unloads) — we'd rather return false than fault.
    uintptr_t vtable = 0;
    __try {
        vtable = *(const uintptr_t *)pWnd;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }

    if (!vtable) return false;

    // Range test: widget is eqmain-owned iff its vtable lives inside the
    // cached eqmain module image. Log evidence 2026-04-16 shows Dalaya's
    // known eqmain vtables (editbox 0x71F2D304, button 0x71F2AA08, other
    // SIDL 0x71F2D370) all fall inside the reported module range.
    return (vtable >= base) && (vtable < base + size);
}

// ═══════════════════════════════════════════════════════════════════
// Step 3 — Live CXWndManager tree walk
// ═══════════════════════════════════════════════════════════════════
//
// Replaces Phase-5 heap-scan false positives with authoritative
// enumeration from CXWndManager::pWindows (MQ2's ReinitializeWindowList
// source-of-truth). See eqmain_offsets.h § Step 3 for the contract.
//
// The flow is: (1) find live CXWndManager by vtable-match scan, (2) read
// its pWindows ArrayClass, (3) BFS every screen's descendant tree, (4)
// return widgets filtered by class + index position. Everything is
// SEH-wrapped at read granularity so a single torn pointer during UI
// teardown can't abort the whole resolver.

// ─── Cache ───────────────────────────────────────────────────────────
// Cached CXWndManager pointer — avoids re-scanning the whole eqmain
// data region on every FindWindowByName call. Invalidated on UNLOAD.
static void *volatile g_cachedWndMgr  = nullptr;
static bool           g_step3Dumped   = false;  // DumpStep3TreeDiagnostic one-shot

// Forward decls — learned offsets reset on eqmain UNLOAD. Declared here so
// InvalidateStep3Caches can see them; definitions are in the mq2port section.
extern int g_learnedDefOffset;
extern int g_learnedNameOffset;

static void InvalidateStep3Caches() {
    InterlockedExchangePointer((PVOID volatile *)&g_cachedWndMgr, nullptr);
    InterlockedExchangePointer((PVOID volatile *)&g_cachedCSidlMgr, nullptr);
    g_step3Dumped = false;
    g_learnedDefOffset = -1;
    g_learnedNameOffset = -1;
}

// ─── Live-CXWndManager scan ──────────────────────────────────────────
// Scans the eqmain image range for the first object whose vtable pointer
// equals `base + RVA_VTABLE_CXWndManager`. On Dalaya ROF2 there's exactly
// one CXWndManager instance and it's allocated on the heap during eqmain
// init — so the scan walks committed memory regions, not the image itself.
//
// Scanning the WHOLE address space would be too slow. Instead we walk
// committed readable pages using VirtualQuery, bounded to 10 MB total of
// scanning before giving up. The CXWndManager singleton gets allocated
// early in eqmain startup so it's almost always in the first few heap
// regions — first-match latency is typically under 100ms.
void *FindLiveCXWndManager() {
    // Fast path: cached.
    void *cached = g_cachedWndMgr;
    if (cached) return cached;

    uintptr_t base = 0;
    uint32_t  size = 0;
    GetRange(&base, &size);
    if (!base || !size) return nullptr;  // eqmain not loaded

    uintptr_t targetVT = base + RVA_VTABLE_CXWndManager;

    // Walk committed readable pages. Wider limits than before since with
    // the offset bug fixed, the real CXWndManager turned out to live past
    // the old 10MB / 2000-region caps. Login-phase process footprint is
    // typically 200-400 MB; we budget 150 MB of scan which covers heap
    // tail but not every region. Caps are defence against runaway scans
    // on corrupted VAD tables; normal case finds within the first few MB.
    MEMORY_BASIC_INFORMATION mbi = {};
    uintptr_t addr = 0x10000;
    size_t    totalScanned = 0;
    int       regions = 0;
    int       candidatesSeen = 0;
    int       candidatesRejected = 0;
    const size_t MAX_SCAN_BYTES = 150ULL * 1024 * 1024;
    const int    MAX_REGIONS    = 20000;

    void     *bestCandidate = nullptr;
    uintptr_t bestDataPtr   = 0;
    int       bestCount     = -1;

    while (addr < 0x7FFF0000 && totalScanned < MAX_SCAN_BYTES && regions < MAX_REGIONS) {
        if (!VirtualQuery((LPCVOID)addr, &mbi, sizeof(mbi))) break;
        uintptr_t rBase = (uintptr_t)mbi.BaseAddress;
        size_t    rSize = mbi.RegionSize;
        if (rSize == 0) break;

        // Accept all commit-readable regions including image-backed
        // (MEM_IMAGE) because eqmain's .data can hold the static pointer
        // we're after. The subsequent sanity gate filters false positives.
        const bool readable = (mbi.State == MEM_COMMIT)
                           && !(mbi.Protect & PAGE_GUARD)
                           && (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE |
                                               PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE));

        if (readable) {
            regions++;
            // Cap per-region scan at 8 MB — login uses some bigger heap
            // blocks for textures/fonts that we don't need to walk fully.
            size_t scanSize = rSize > 8 * 1024 * 1024 ? 8 * 1024 * 1024 : rSize;
            totalScanned += scanSize;

            __try {
                const uintptr_t *p   = (const uintptr_t *)rBase;
                const uintptr_t *end = (const uintptr_t *)(rBase + scanSize);
                for (; p < end; p++) {
                    if (*p != targetVT) continue;
                    candidatesSeen++;
                    uintptr_t candidate = (uintptr_t)p;
                    uintptr_t dataPtr = *(uintptr_t *)(candidate + OFF_CXWndManager_pWindows_Data);
                    int       count   = *(int *)       (candidate + OFF_CXWndManager_pWindows_Cnt);

                    // Stricter gate: a real CXWndManager either has 0
                    // screens (very early init) or a heap-range data ptr
                    // with a matching non-huge count.
                    bool countSane = (count >= 0 && count < 1024);
                    bool empty     = (count == 0 && dataPtr == 0);
                    bool populated = (count > 0)
                                  && (dataPtr >= 0x00100000)
                                  && (dataPtr <  0x7FFF0000);
                    if (!countSane || !(empty || populated)) {
                        candidatesRejected++;
                        continue;
                    }

                    // Prefer a POPULATED manager over an empty one. During
                    // login frontend teardown there can be a stale
                    // secondary manager with 0 screens; don't let it win
                    // the race against the real active one.
                    if (populated) {
                        void *found = (void *)candidate;
                        InterlockedExchangePointer((PVOID volatile *)&g_cachedWndMgr, found);
                        DI8Log("step3: FindLiveCXWndManager → %p (pWindows.data=0x%08X count=%d, scanned %d regions / %u bytes, %d cand / %d reject)",
                               found, (unsigned)dataPtr, count, regions,
                               (unsigned)totalScanned, candidatesSeen, candidatesRejected);
                        return found;
                    }
                    // Remember an empty manager as a fallback — but keep
                    // scanning in case the populated one is further along.
                    if (empty && !bestCandidate) {
                        bestCandidate = (void *)candidate;
                        bestDataPtr   = dataPtr;
                        bestCount     = count;
                    }
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {
                // Region got yanked mid-scan — continue with the next one.
            }
        }

        uintptr_t next = rBase + rSize;
        if (next <= addr) break;  // guard against zero-advance loop
        addr = next;
    }

    // Exhausted scan without finding a populated manager. Fall back to
    // the empty one if we kept it — it'll get populated as eqmain
    // registers screens, so a retry after some game ticks should succeed
    // on the live singleton.
    if (bestCandidate) {
        InterlockedExchangePointer((PVOID volatile *)&g_cachedWndMgr, bestCandidate);
        DI8Log("step3: FindLiveCXWndManager → %p (EMPTY pWindows, scanned %d regions / %u bytes, %d cand / %d reject)",
               bestCandidate, regions, (unsigned)totalScanned,
               candidatesSeen, candidatesRejected);
        return bestCandidate;
    }

    // No match — log ONCE per invocation series (cache miss = retry next call).
    static int s_missLogCount = 0;
    if (s_missLogCount < 3) {
        DI8Log("step3: FindLiveCXWndManager — not found after %d regions / %u bytes (target vt=0x%08X, %d cand seen / %d rejected)",
               regions, (unsigned)totalScanned, (unsigned)targetVT,
               candidatesSeen, candidatesRejected);
        s_missLogCount++;
    }
    return nullptr;
}

// ─── pWindows iteration ──────────────────────────────────────────────
// Empirical finding (Step 3 first run): top-level screens in pWindows use
// CXWnd subclasses that aren't in our IsEQMainWidgetClass whitelist (the
// whitelist is tuned for widgets we SetWindowText/click on, not for
// screen containers). We relax to IsEQMainWidget (range-based vtable
// check) here — any object whose vtable lives inside eqmain's image is
// accepted. MQ2 does the equivalent: it trusts whatever eqmain puts in
// pWindows without class-filtering at the enumeration stage.
//
// The tighter whitelist still applies when we *match* a widget (edit
// vs button etc) — so CXMLDataPtr-shaped junk can't accidentally get
// dispatched to, even if it sneaks into pWindows (shouldn't happen but
// belt-and-braces).
int EnumerateTopLevelScreens(void *out[], int maxCount) {
    if (!out || maxCount <= 0) return 0;
    void *pMgr = FindLiveCXWndManager();
    if (!pMgr) return 0;

    // Unconditional entry log (rate-limited) — we need visibility on why
    // earlier runs produced 0 screens. The one-shot "EnumerateTopLevelScreens
    // accepted/skipped" log at the end can be swallowed by a silent SEH or
    // early return, which makes the failure mode invisible.
    static int s_enterLogCount = 0;
    if (s_enterLogCount < 3) {
        DI8Log("step3: EnumerateTopLevelScreens entered, pMgr=%p maxCount=%d", pMgr, maxCount);
        s_enterLogCount++;
    }

    int produced = 0;
    int nSkipped = 0;
    int readCount = -1;
    uintptr_t readDataPtr = 0;
    bool loopCompleted = false;

    __try {
        uintptr_t mgrAddr = (uintptr_t)pMgr;
        readDataPtr = *(uintptr_t *)(mgrAddr + OFF_CXWndManager_pWindows_Data);
        readCount   = *(int *)       (mgrAddr + OFF_CXWndManager_pWindows_Cnt);

        if (readCount <= 0 || readCount > 256) {
            // Fall through — caller-visible log at bottom explains why 0.
        } else if (!readDataPtr) {
            // Same — fall through to exit log.
        } else {
            uintptr_t base = 0; uint32_t sz = 0;
            GetRange(&base, &sz);

            static bool s_perScreenLogged = false;
            void **arr = (void **)readDataPtr;
            for (int i = 0; i < readCount && produced < maxCount; i++) {
                void *screen = nullptr;
                __try { screen = arr[i]; } __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
                if (!screen) continue;
                if (!IsEQMainWidget(screen)) { nSkipped++; continue; }

                if (!s_perScreenLogged && base) {
                    uintptr_t vt = 0;
                    __try { vt = *(uintptr_t *)screen; } __except(EXCEPTION_EXECUTE_HANDLER) {}
                    const char *known = GetEQMainWidgetClassName(screen);
                    DI8Log("step3:   pWindows[%d]=%p vt=0x%08X (eqmain+0x%05X) known=%s",
                           i, screen, (unsigned)vt,
                           (unsigned)(vt ? vt - base : 0),
                           known ? known : "<unknown-subclass>");
                }
                out[produced++] = screen;
            }
            s_perScreenLogged = true;
            loopCompleted = true;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // pWindows array got reallocated mid-read. Return what we have so far.
    }

    // Unconditional exit log (rate-limited) — fires even on early-return paths.
    static int s_exitLogCount = 0;
    if (s_exitLogCount < 3) {
        DI8Log("step3: EnumerateTopLevelScreens exit — count=%d data=0x%08X produced=%d skipped=%d loopDone=%d",
               readCount, (unsigned)readDataPtr, produced, nSkipped, loopCompleted ? 1 : 0);
        s_exitLogCount++;
    }
    return produced;
}

// ─── BFS tree walk ───────────────────────────────────────────────────
int EnumerateWidgetsInTree(void *out[], int maxCount, uint32_t classFilterRVA) {
    if (!out || maxCount <= 0) return 0;

    // Seed queue with all top-level screens.
    void *screens[16];
    int nScreens = EnumerateTopLevelScreens(screens, 16);
    if (nScreens == 0) return 0;

    // Bounded BFS queue. 512 is ample for Dalaya's login UI (~30-50 widgets).
    void *queue[512];
    int   qHead = 0, qTail = 0;
    for (int i = 0; i < nScreens && qTail < 512; i++) {
        queue[qTail++] = screens[i];
    }

    uintptr_t base = g_base;
    int produced = 0;
    int overflow = 0;

    while (qHead < qTail && produced < maxCount) {
        void *node = queue[qHead++];
        if (!node) continue;

        // Class-match → collect.
        uintptr_t vt = 0;
        if (ReadVtablePtr(node, &vt)) {
            bool match = (classFilterRVA == 0) || (base && vt == base + classFilterRVA);
            if (match && IsEQMainWidgetClass(node)) {
                out[produced++] = node;
            }
        }

        // Enqueue first child + next sibling. These reads fault easily on
        // torn UI state — SEH each one individually.
        __try {
            void *child = *(void **)((uintptr_t)node + OFF_CXWnd_FirstChild);
            if (child) {
                if (qTail < 512) queue[qTail++] = child;
                else overflow++;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        __try {
            void *sib = *(void **)((uintptr_t)node + OFF_CXWnd_NextSibling);
            if (sib) {
                if (qTail < 512) queue[qTail++] = sib;
                else overflow++;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    if (overflow > 0) {
        DI8Log("step3: EnumerateWidgetsInTree — queue overflow, %d widgets skipped", overflow);
    }
    return produced;
}

// ─── Diagnostic dump ─────────────────────────────────────────────────
void DumpStep3TreeDiagnostic() {
    if (g_step3Dumped) return;  // one-shot per eqmain load

    void *screens[16];
    int nScreens = EnumerateTopLevelScreens(screens, 16);
    if (nScreens == 0) {
        // No screens yet — CXWndManager might not be populated. Don't flip
        // the dumped flag, let a later call retry.
        return;
    }
    g_step3Dumped = true;

    DI8Log("step3: ═══ TREE DIAGNOSTIC ═══ %d top-level screens", nScreens);
    for (int i = 0; i < nScreens; i++) {
        const char *cls = GetEQMainWidgetClassName(screens[i]);
        DI8Log("step3:   screen[%d] = %p  class=%s", i, screens[i], cls ? cls : "?");
    }

    // Class-filtered snapshots of the combined descendant tree.
    struct { uint32_t rva; const char *label; } filters[] = {
        { RVA_VTABLE_CEditWnd,       "CEditWnd"       },
        { RVA_VTABLE_CButtonWnd,     "CButtonWnd"     },
        { RVA_VTABLE_CListWnd,       "CListWnd"       },
        { RVA_VTABLE_CSidlScreenWnd, "CSidlScreenWnd" },
    };
    for (auto &f : filters) {
        void *widgets[64];
        int n = EnumerateWidgetsInTree(widgets, 64, f.rva);
        DI8Log("step3:   %-15s x %d", f.label, n);
        for (int i = 0; i < n && i < 16; i++) {
            DI8Log("step3:     [%d] %p", i, widgets[i]);
        }
    }
    DI8Log("step3: ═══ END TREE DIAGNOSTIC ═══");
}

// ─── MQ2-faithful widget-by-name resolver ────────────────────────────
// Dalaya's dinput8.dll (Dalaya MQ2, 1.3MB) exports
//   ?GetChildItem@CXWnd@EQClasses@@QAEPAV12@PAD@Z
// as a thunk into MQ2's own C++ implementation of CXWnd::GetChildItem
// (macroquest-rof2-emu/src/eqlib/src/game/CXWnd.cpp:115-160,
// RecurseAndFindName + GetChildItem). Semantics: recursively walks the
// widget's child subtree comparing pXMLData->Name (and pXMLData->ScreenID)
// case-insensitively against the requested name. Returns the first
// matching descendant, or nullptr if the name isn't in this subtree.
//
// Calling this on every top-level widget in eqmain's pWindows reproduces
// MQ2's autologin lookup (MQ2AutoLogin.h:60-66 +
// StateMachine.cpp:243,271,275,341 — GetChildWindow<T>(m_currentWindow,
// name)) without depending on MQ2's LoginStateSensor to know which
// top-level is the active one. First non-null hit wins.
//
// dinput8.dll is loaded by eqgame's import table before the main thread
// runs, so GetModuleHandleA succeeds by the time eqswitch-di8 is injected.
// GetProcAddress on the mangled export returns a callable __thiscall thunk.

static FN_CXWndGetChildItemChar g_fnCXWnd_GetChildItem_Char = nullptr;

// ─── pSidlMgr swap state ──────────────────────────────────────────
// dinput8.dll exports:
//   ppSidlMgr          @ RVA 0x1353b8 (address of the pSidlMgr variable)
//   pinstCSidlManager  @ RVA 0x14ae74 (address of the instance-holder variable)
// GetProcAddress("ppSidlMgr") returns the STORAGE ADDRESS of the variable
// (so it's a void** — deref once to get the pointer value, write through to
// replace it). See eqmain_offsets.h for why we need this swap.
static void      **g_ppSidlMgr_storage     = nullptr;  // &pSidlMgr in dinput8
static uintptr_t  *g_pinstCSidlMgr_storage = nullptr;  // &pinstCSidlManager in dinput8
static void       *g_priorPSidlMgr         = nullptr;  // restore value on UNLOAD
static uintptr_t   g_priorPInst            = 0;        // restore value on UNLOAD
static bool        g_swapInstalled         = false;

bool ResolveMQ2FaithfulFunctions() {
    if (g_fnCXWnd_GetChildItem_Char) return true;  // already resolved

    HMODULE hDinput8 = GetModuleHandleA("dinput8.dll");
    if (!hDinput8) {
        DI8Log("mq2port: dinput8.dll not loaded — cannot resolve CXWnd::GetChildItem");
        return false;
    }

    // ?GetChildItem@CXWnd@EQClasses@@QAEPAV12@PAD@Z
    //   = CXWnd::EQClasses::GetChildItem(char*) -> CXWnd*
    //   char-pointer overload (simpler than constructing a CXStr on the stack).
    //   Internally reads the global `pSidlMgr` — so we also need to resolve
    //   the pSidlMgr variable's storage address so we can swap it to eqmain's
    //   CSidlManager instance during login.
    const char *mangled = "?GetChildItem@CXWnd@EQClasses@@QAEPAV12@PAD@Z";
    FARPROC fn = GetProcAddress(hDinput8, mangled);
    if (!fn) {
        DI8Log("mq2port: dinput8.dll missing export %s — autologin will fall back", mangled);
        return false;
    }

    g_fnCXWnd_GetChildItem_Char = (FN_CXWndGetChildItemChar)fn;
    DI8Log("mq2port: resolved CXWnd::GetChildItem(char*) = %p via dinput8.dll", (void*)fn);

    // Resolve the pSidlMgr variable storage addresses (lazy — first swap
    // attempt binds them). GetProcAddress on a data export returns the
    // variable's address: treat ppSidlMgr as `void **` so *ppSidlMgr is
    // the pSidlMgr value, and writing *ppSidlMgr replaces it.
    g_ppSidlMgr_storage = (void **)GetProcAddress(hDinput8, "ppSidlMgr");
    g_pinstCSidlMgr_storage = (uintptr_t *)GetProcAddress(hDinput8, "pinstCSidlManager");
    DI8Log("mq2port: pSidlMgr storage resolved — ppSidlMgr=%p  pinstCSidlManager=%p",
           g_ppSidlMgr_storage, g_pinstCSidlMgr_storage);

    return true;
}

// ─── CSidlManagerBase live-instance scanner ──────────────────────
// Same strategy as FindLiveCXWndManager but for the CSidlManagerBase
// singleton. eqmain constructs exactly one instance during init; its
// vtable pointer equals base + RVA_VTABLE_CSidlManagerBase.
// (Forward-declared near the top of file so InvalidateStep3Caches can
// touch it on UNLOAD; this is the definition.)
void *volatile g_cachedCSidlMgr = nullptr;

void *FindLiveCSidlManagerBase() {
    void *cached = g_cachedCSidlMgr;
    if (cached) return cached;

    uintptr_t base = 0;
    uint32_t  size = 0;
    GetRange(&base, &size);
    if (!base || !size) return nullptr;

    uintptr_t targetVT = base + RVA_VTABLE_CSidlManagerBase;

    MEMORY_BASIC_INFORMATION mbi = {};
    uintptr_t addr = 0x10000;
    size_t    totalScanned = 0;
    int       regions = 0;
    const size_t MAX_SCAN_BYTES = 150ULL * 1024 * 1024;
    const int    MAX_REGIONS    = 20000;

    while (addr < 0x7FFF0000 && totalScanned < MAX_SCAN_BYTES && regions < MAX_REGIONS) {
        if (!VirtualQuery((LPCVOID)addr, &mbi, sizeof(mbi))) break;
        uintptr_t rBase = (uintptr_t)mbi.BaseAddress;
        size_t    rSize = mbi.RegionSize;
        if (rSize == 0) break;

        const bool readable = (mbi.State == MEM_COMMIT)
                           && !(mbi.Protect & PAGE_GUARD)
                           && (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE |
                                               PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE));

        if (readable) {
            regions++;
            size_t scanSize = rSize > 8 * 1024 * 1024 ? 8 * 1024 * 1024 : rSize;
            totalScanned += scanSize;

            __try {
                const uintptr_t *p   = (const uintptr_t *)rBase;
                const uintptr_t *end = (const uintptr_t *)(rBase + scanSize);
                for (; p < end; p++) {
                    if (*p != targetVT) continue;
                    void *found = (void *)p;
                    InterlockedExchangePointer((PVOID volatile *)&g_cachedCSidlMgr, found);
                    DI8Log("mq2port: FindLiveCSidlManagerBase → %p (scanned %d regions / %u bytes)",
                           found, regions, (unsigned)totalScanned);
                    return found;
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {
                // region yanked mid-scan — continue
            }
        }

        uintptr_t next = rBase + rSize;
        if (next <= addr) break;
        addr = next;
    }

    DI8Log("mq2port: FindLiveCSidlManagerBase — not found after %d regions / %u bytes (target vt=0x%08X)",
           regions, (unsigned)totalScanned, (unsigned)targetVT);
    return nullptr;
}

// Install the pSidlMgr swap. Called from OnEQMainLoaded after resolver +
// CSidlManagerBase scan both succeed. Persists until UNLOAD.
//
// Indirection math — verified by disassembling dinput8's GetChildItem@CXStr
// (RVA 0x9ffa0):
//   mov eax, [0x101353b8]   ; load ppSidlMgr's VALUE
//   mov ecx, [eax]          ; DEREF it → pSidlMgr (CSidlManager*)
// So ppSidlMgr at RVA 0x1353b8 is a pointer-to-pointer: its value is the
// STORAGE ADDRESS of the pSidlMgr variable. To swap pSidlMgr we must:
//   1. Read ppSidlMgr's value → get pSidlMgr's storage address
//   2. Write eqmainCSidlMgr TO that address → replace the pSidlMgr pointer
// Writing directly to ppSidlMgr_storage (what the previous version did) only
// clobbers the outer pointer and GetChildItem then double-derefs a wild value.
bool InstallPSidlMgrSwap() {
    if (g_swapInstalled) return true;  // idempotent

    if (!g_ppSidlMgr_storage) {
        DI8Log("mq2port: InstallPSidlMgrSwap — ppSidlMgr not resolved (ResolveMQ2FaithfulFunctions must run first)");
        return false;
    }

    void *eqmainCSidlMgr = FindLiveCSidlManagerBase();
    if (!eqmainCSidlMgr) {
        DI8Log("mq2port: InstallPSidlMgrSwap — eqmain CSidlManagerBase not found yet (retry on next widget lookup)");
        return false;
    }

    // Resolve the REAL pSidlMgr storage: *ppSidlMgr_storage is an address,
    // that address is where the actual CSidlManager pointer lives.
    void **realPSidlMgrStorage = nullptr;
    __try {
        realPSidlMgrStorage = (void **)*g_ppSidlMgr_storage;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2port: InstallPSidlMgrSwap — could not read ppSidlMgr value");
        return false;
    }
    if (!realPSidlMgrStorage) {
        DI8Log("mq2port: InstallPSidlMgrSwap — *ppSidlMgr = NULL (dinput8 not fully initialized?)");
        return false;
    }

    // Save prior pSidlMgr value so UNLOAD restores the exact same pointer.
    __try {
        g_priorPSidlMgr = *realPSidlMgrStorage;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2port: InstallPSidlMgrSwap — storage at %p unreadable", realPSidlMgrStorage);
        return false;
    }

    // pinstCSidlManager is a direct uintptr_t variable (per MQ2 source). Save
    // its current value too so restoration is symmetric — even though we
    // don't know if any dinput8 code reads it separately from pSidlMgr.
    if (g_pinstCSidlMgr_storage) {
        __try {
            g_priorPInst = *g_pinstCSidlMgr_storage;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            g_priorPInst = 0;
        }
    }

    // Write the swap. realPSidlMgrStorage is in writable heap (dinput8's
    // runtime-allocated var storage); pinstCSidlManager is in dinput8's
    // .data which is also writable at runtime (MSVC default).
    __try {
        *realPSidlMgrStorage = eqmainCSidlMgr;
        if (g_pinstCSidlMgr_storage) {
            *g_pinstCSidlMgr_storage = (uintptr_t)eqmainCSidlMgr;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2port: InstallPSidlMgrSwap — write faulted (storage not writable?)");
        return false;
    }

    g_swapInstalled = true;
    DI8Log("mq2port: pSidlMgr swap INSTALLED — eqmain CSidlMgr=%p  "
           "wrote to %p (was %p)  pinstCSidlManager=%p (was 0x%08X)",
           eqmainCSidlMgr, realPSidlMgrStorage, g_priorPSidlMgr,
           g_pinstCSidlMgr_storage, (unsigned)g_priorPInst);
    return true;
}

void RestorePSidlMgrSwap() {
    if (!g_swapInstalled) return;
    if (g_ppSidlMgr_storage) {
        __try {
            void **realPSidlMgrStorage = (void **)*g_ppSidlMgr_storage;
            if (realPSidlMgrStorage) {
                *realPSidlMgrStorage = g_priorPSidlMgr;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    if (g_pinstCSidlMgr_storage) {
        __try {
            *g_pinstCSidlMgr_storage = g_priorPInst;
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    InterlockedExchangePointer((PVOID volatile *)&g_cachedCSidlMgr, nullptr);
    g_swapInstalled = false;
    DI8Log("mq2port: pSidlMgr swap RESTORED (prior pSidlMgr=%p, prior pinst=0x%08X)",
           g_priorPSidlMgr, (unsigned)g_priorPInst);
}

// ─── REMOVED: heuristic helpers (FindLoginEditPair, WidgetByKindAndText,
// WidgetByKindAndTextPrefix, NthWidgetOfKind, CountWidgetsOfKind,
// IsWidgetVisible, ReadWidgetText, IEqual, FindScreenByWidgetCount,
// DumpAllButtonTextsOnce, WidgetMatchesKind, WidgetKind enum)
//
// These were position/text-based guesses for Username/Password/Connect/Yes
// button resolution. Replaced wholesale by the MQ2-faithful
// CXWnd::GetChildItem loop in FindWidgetByKnownName below.
//
// Don't restore. If FindWidgetByKnownName returns nullptr, the right fix is
// to verify the MQ2 export resolved (check the "mq2port: resolved" log) and
// that pWindows is populated — NOT to add a text/position fallback. ─────

namespace {
    // (anonymous namespace intentionally empty — previous heuristic helpers
    // listed in the REMOVED comment above were deleted wholesale. If a
    // future widget-resolution need arises, add a NEW resolver that uses
    // MQ2's GetChildItem thunk, not position/text guessing.)
} // anonymous namespace
#if 0
    // BEGIN DELETED HEURISTIC BLOCK — kept inside #if 0 for historical reference
    // only. Not compiled. Delete entirely once the MQ2-faithful port is verified
    // in-game for >=3 successful full logins.
    bool _removed_ReadWidgetText(const void *pWnd, char *outBuf, int bufSize) {
        // ...body elided...
        return false;
    }
#endif
// Original body of anonymous namespace continues below under #if 0 for the
// rest of this edit series — it will be deleted in the cleanup commit.
#if 0
namespace {
    // Reads a widget's WindowText (CXStr at +0x1A8) directly, bypassing
    // the eqgame-side GetWindowTextA export which SEHs on eqmain widgets.
    //
    // Dalaya CXStr is the 16-byte struct {Ptr, Length, Alloc, RefCount}.
    // Reading it via memcpy is safe as long as pWnd is a live widget (we
    // gate on IsEQMainWidgetClass before calling this).
    //
    // Returns true on success with outBuf populated. Returns false and
    // leaves outBuf empty on any fault or empty-string widget.
    bool ReadWidgetText(const void *pWnd, char *outBuf, int bufSize) {
        if (!outBuf || bufSize <= 0) return false;
        outBuf[0] = '\0';
        if (!pWnd) return false;
        constexpr uint32_t OFF_WINDOWTEXT = 0x1A8;
        __try {
            const uint8_t *base = (const uint8_t *)pWnd + OFF_WINDOWTEXT;
            const char *ptr = *(const char * const *)base;
            int length      = *(const int *)(base + 4);
            if (!ptr || length <= 0 || length > 512) return false;
            int n = length < bufSize - 1 ? length : bufSize - 1;
            // Copy byte-by-byte under the same SEH guard so a torn string
            // pointer during teardown can't crash us mid-read.
            for (int i = 0; i < n; i++) outBuf[i] = ptr[i];
            outBuf[n] = '\0';
            return true;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            outBuf[0] = '\0';
            return false;
        }
    }

    // Case-insensitive exact compare for WindowText matching. "Yes" and
    // "YES" should both match YESNO_YesButton.
    bool IEqual(const char *a, const char *b) {
        if (!a || !b) return false;
        while (*a && *b) {
            char ca = *a, cb = *b;
            if (ca >= 'A' && ca <= 'Z') ca = ca - 'A' + 'a';
            if (cb >= 'A' && cb <= 'Z') cb = cb - 'A' + 'a';
            if (ca != cb) return false;
            a++; b++;
        }
        return *a == '\0' && *b == '\0';
    }

    // Visibility flag: CXWnd::dShow at offset 0x1A. True if the widget is
    // currently visible on screen. First observation (Step 3 attempt 6)
    // found 3 CEditWnds in pWindows but only 2 should be visible at login
    // time (Username + Password). The extra edit is from some other
    // screen that's pre-allocated but hidden (error dialog input, SIDL
    // template, etc.) — filtering by dShow eliminates it cleanly.
    constexpr uint32_t OFF_CXWnd_dShow = 0x1A;

    bool IsWidgetVisible(const void *pWnd) {
        if (!pWnd) return false;
        __try {
            uint8_t v = *((const uint8_t *)pWnd + OFF_CXWnd_dShow);
            return v != 0;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
    }

    // Flat enumeration of pWindows filtered by class predicate. Produces
    // widgets IN THE ORDER THEY APPEAR IN pWindows — important for
    // "first CEditWnd = Username, second = Password" semantics.
    enum WidgetKind { K_EDIT, K_BUTTON, K_LIST, K_ANY };

    bool WidgetMatchesKind(const void *pWnd, WidgetKind kind) {
        switch (kind) {
            case K_EDIT:   return EQMainOffsets::IsEQMainEditWidget(pWnd);
            case K_BUTTON: return EQMainOffsets::IsEQMainButtonWidget(pWnd);
            case K_LIST: {
                uintptr_t base = 0; uint32_t sz = 0;
                EQMainOffsets::GetRange(&base, &sz);
                if (!base) return false;
                uintptr_t vt = 0;
                __try { vt = *(const uintptr_t *)pWnd; } __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
                return vt == base + EQMainOffsets::RVA_VTABLE_CListWnd;
            }
            case K_ANY: return EQMainOffsets::IsEQMainWidget(pWnd);
        }
        return false;
    }

    // Returns Nth widget in pWindows matching `kind` (zero-based).
    // Visibility filter removed — empirically (attempt 7) all widgets
    // report dShow=1 regardless of actual on-screen state, so the flag
    // at 0x1A isn't the visibility we thought it was. Rely on class+
    // position+address-clustering heuristics instead.
    void *NthWidgetOfKind(WidgetKind kind, int n) {
        if (n < 0) return nullptr;
        void *screens[256];
        int nScreens = EQMainOffsets::EnumerateTopLevelScreens(screens, 256);
        int seen = 0;
        for (int i = 0; i < nScreens; i++) {
            if (!WidgetMatchesKind(screens[i], kind)) continue;
            if (seen == n) return screens[i];
            seen++;
        }
        return nullptr;
    }

    // Counts total widgets in pWindows matching `kind`. Used to adapt
    // the lookup position — when there are 3+ CEditWnds, the extra ones
    // are from non-login screens (stale/hidden) and we need to skip them.
    int CountWidgetsOfKind(WidgetKind kind) {
        void *screens[256];
        int nScreens = EQMainOffsets::EnumerateTopLevelScreens(screens, 256);
        int n = 0;
        for (int i = 0; i < nScreens; i++) {
            if (WidgetMatchesKind(screens[i], kind)) n++;
        }
        return n;
    }

    // Username/Password are declared adjacent in EQUI.xml — on Dalaya
    // they allocate at close heap addresses (observed delta ~0x348).
    // Stale/pre-screen edits are much further away (delta ~0x5D0+).
    // This finds the closest-pair and returns them in address order
    // (lower = Username per XML declaration order).
    //
    // Returns true when a valid pair is identified. False when < 2 edits
    // exist (eqmain not yet at the login frontend).
    bool FindLoginEditPair(void **outUser, void **outPass) {
        void *edits[16];
        int nEdits = 0;
        void *screens[256];
        int nScreens = EQMainOffsets::EnumerateTopLevelScreens(screens, 256);
        for (int i = 0; i < nScreens && nEdits < 16; i++) {
            if (EQMainOffsets::IsEQMainEditWidget(screens[i])) {
                edits[nEdits++] = screens[i];
            }
        }
        if (nEdits < 2) return false;

        // Exactly 2 edits — easy case, first is Username.
        if (nEdits == 2) {
            uintptr_t a = (uintptr_t)edits[0], b = (uintptr_t)edits[1];
            *outUser = (void *)(a < b ? a : b);
            *outPass = (void *)(a < b ? b : a);
            return true;
        }

        // 3+ edits — find the closest-address pair.
        int bestI = 0, bestJ = 1;
        uintptr_t bestDelta = (uintptr_t)-1;
        for (int i = 0; i < nEdits - 1; i++) {
            for (int j = i + 1; j < nEdits; j++) {
                uintptr_t a = (uintptr_t)edits[i];
                uintptr_t b = (uintptr_t)edits[j];
                uintptr_t d = a > b ? a - b : b - a;
                if (d < bestDelta) { bestDelta = d; bestI = i; bestJ = j; }
            }
        }
        uintptr_t a = (uintptr_t)edits[bestI];
        uintptr_t b = (uintptr_t)edits[bestJ];
        *outUser = (void *)(a < b ? a : b);
        *outPass = (void *)(a < b ? b : a);
        DI8Log("step3: FindLoginEditPair — %d edits total, closest pair delta=0x%X → User=%p Pass=%p",
               nEdits, (unsigned)bestDelta, *outUser, *outPass);
        return true;
    }

    // Returns the FIRST widget in pWindows matching `kind` AND whose
    // WindowText case-insensitively equals `text`. Used to disambiguate
    // among multiple buttons (Yes vs No vs OK vs Connect).
    //
    // KNOWN LIMITATION: on Dalaya ROF2 all buttons report empty WindowText
    // via direct CXStr-at-0x1A8 read. Label text lives elsewhere (probably
    // in a CButtonTemplate referenced indirectly). This function is kept
    // for future use when we resolve the correct label-text path; for
    // now it returns nullptr and callers fall back to position heuristics.
    void *WidgetByKindAndText(WidgetKind kind, const char *text) {
        if (!text) return nullptr;
        void *screens[256];
        int nScreens = EQMainOffsets::EnumerateTopLevelScreens(screens, 256);
        for (int i = 0; i < nScreens; i++) {
            if (!WidgetMatchesKind(screens[i], kind)) continue;
            char buf[64] = {};
            if (!ReadWidgetText(screens[i], buf, sizeof(buf))) continue;
            if (IEqual(buf, text)) return screens[i];
        }
        return nullptr;
    }

    void *WidgetByKindAndTextPrefix(WidgetKind kind, const char *prefix) {
        if (!prefix) return nullptr;
        size_t plen = strlen(prefix);
        void *screens[256];
        int nScreens = EQMainOffsets::EnumerateTopLevelScreens(screens, 256);
        for (int i = 0; i < nScreens; i++) {
            if (!WidgetMatchesKind(screens[i], kind)) continue;
            char buf[64] = {};
            if (!ReadWidgetText(screens[i], buf, sizeof(buf))) continue;
            bool ok = true;
            for (size_t j = 0; j < plen; j++) {
                char a = buf[j], b = prefix[j];
                if (!a) { ok = false; break; }
                if (a >= 'A' && a <= 'Z') a = a - 'A' + 'a';
                if (b >= 'A' && b <= 'Z') b = b - 'A' + 'a';
                if (a != b) { ok = false; break; }
            }
            if (ok) return screens[i];
        }
        return nullptr;
    }

    // One-shot diagnostic — after pWindows populates, log every edit+button
    // with its WindowText AND visibility flag so we can eyeball-verify
    // "visible Connect button" etc.
    void DumpAllButtonTextsOnce() {
        static bool s_dumped = false;
        if (s_dumped) return;
        void *screens[256];
        int nScreens = EQMainOffsets::EnumerateTopLevelScreens(screens, 256);
        if (nScreens == 0) return;
        int nLogged = 0;
        for (int i = 0; i < nScreens && nLogged < 40; i++) {
            bool isEdit   = EQMainOffsets::IsEQMainEditWidget(screens[i]);
            bool isButton = EQMainOffsets::IsEQMainButtonWidget(screens[i]);
            if (!(isEdit || isButton)) continue;
            char buf[64] = {};
            ReadWidgetText(screens[i], buf, sizeof(buf));
            const char *cls = EQMainOffsets::GetEQMainWidgetClassName(screens[i]);
            bool vis = IsWidgetVisible(screens[i]);
            DI8Log("step3: widget[%d] %s %p dShow=%d text='%s'",
                   i, cls ? cls : "?", screens[i], vis ? 1 : 0, buf);
            nLogged++;
        }
        s_dumped = true;
    }

    // ─── Legacy tree-based helpers kept for Character_List fallback ──

    // Returns a screen whose descendant tree contains `wantEdits` CEditWnds
    // and `wantButtons` CButtonWnds. Used to disambiguate LoginBaseScreen
    // from dialog screens. Returns nullptr if no screen matches.
    //
    // Counts are "at least" — a screen with 3 edits matches a request for 2.
    // For exact-match semantics pass -1 as "don't care".
    void *FindScreenByWidgetCount(int wantEdits, int wantButtons) {
        void *screens[16];
        int nScreens = EnumerateTopLevelScreens(screens, 16);
        for (int i = 0; i < nScreens; i++) {
            // Count descendants of this one screen. Tree-walk manually
            // instead of filtering the combined tree so we can per-screen.
            int nEdits = 0, nButtons = 0;
            void *queue[256];
            int qHead = 0, qTail = 0;
            queue[qTail++] = screens[i];
            while (qHead < qTail) {
                void *n = queue[qHead++];
                if (!n) continue;
                if (EQMainOffsets::IsEQMainEditWidget(n))   nEdits++;
                if (EQMainOffsets::IsEQMainButtonWidget(n)) nButtons++;
                __try {
                    void *c = *(void **)((uintptr_t)n + OFF_CXWnd_FirstChild);
                    if (c && qTail < 256) queue[qTail++] = c;
                } __except (EXCEPTION_EXECUTE_HANDLER) {}
                __try {
                    void *s = *(void **)((uintptr_t)n + OFF_CXWnd_NextSibling);
                    // Only follow siblings of children, not top-level.
                    // The top-level screen's own NextSibling would loop us
                    // across other screens — skip it at depth 0.
                    if (s && qHead > 1 && qTail < 256) queue[qTail++] = s;
                } __except (EXCEPTION_EXECUTE_HANDLER) {}
            }
            bool editsOk   = (wantEdits   < 0) || (nEdits   >= wantEdits);
            bool buttonsOk = (wantButtons < 0) || (nButtons >= wantButtons);
            // For exact-screen identity we want the edit count to match
            // precisely for dialogs — but for login the 2-edit screen is
            // unique anyway. Accept >= and let caller disambiguate.
            if (editsOk && buttonsOk) return screens[i];
        }
        return nullptr;
    }

} // anonymous namespace (original — all contents disabled via #if 0 above)
#endif // end of historical-reference #if 0 block

bool IsStep3KnownName(const char *name) {
    if (!name) return false;
    // Allow-list of widget names the login state machine requests. The
    // MQ2-faithful resolver in FindWidgetByKnownName accepts ANY name, so
    // this list is no longer a correctness gate — it's still useful as a
    // telemetry filter so callers can log "known login widget" vs arbitrary
    // lookups. Add new names here when the state machine starts asking for
    // them; removing names just loses the telemetry tag, nothing else.
    static const char *kKnown[] = {
        "LOGIN_UsernameEdit",
        "LOGIN_PasswordEdit",
        "LOGIN_ConnectButton",
        "YESNO_YesButton",
        "OK_OKButton",
        "OK_Display",
        "Character_List",
        "CLW_EnterWorldButton",
        nullptr,
    };
    for (int i = 0; kKnown[i]; i++) {
        if (strcmp(name, kKnown[i]) == 0) return true;
    }
    return false;
}

// ─── Native RecurseAndFindName port ──────────────────────────────
//
// Direct C++ port of MQ2's RecurseAndFindName from
// macroquest-rof2-emu/src/eqlib/src/game/CXWnd.cpp lines 115-149, adapted
// to eqmain's struct layout (no dependency on dinput8 exports or pSidlMgr).
//
// Offsets — discovered empirically from the existing HeapScan+CrossRef code
// that already successfully matches widgets by name:
//   CXWnd + 0x2C  — pSidlPiece / pXMLData (m_pSidlPiece, discovered via
//                    heap cross-reference in mq2_bridge::FindLiveCXWnd)
//   CXMLData + 0x18 — Name CXStr storage (HeapScanForWidget reads at +0x18)
//   CXStr.buf + 0x08 — int length
//   CXStr.buf + 0x14 — char data[] (20-byte header)
//
// These are the SAME offsets mq2_bridge uses — this function is just the
// tree-walk variant of the same algorithm. Heap scan finds by brute force;
// this walks the live tree from a known root.
//
// The tree walk is faster than heap scan (targeted traversal vs O(heap-size))
// and — critically — operates on LIVE widgets rather than definitions. That
// makes it resilient to transient UI (kick dialogs, OK popups) that register
// and disappear faster than a heap scan can complete.
constexpr uint32_t OFF_CXWnd_pSidlPiece = 0x2C;
constexpr uint32_t OFF_CXMLData_Name    = 0x18;  // CXStr (pointer to buf)
constexpr uint32_t OFF_CXStrBuf_Length  = 0x08;
constexpr uint32_t OFF_CXStrBuf_Data    = 0x14;

// Case-insensitive byte compare.
static bool CIEqualsN(const char *a, const char *b, int len) {
    for (int i = 0; i < len; i++) {
        char ca = a[i], cb = b[i];
        if (ca >= 'A' && ca <= 'Z') ca = ca - 'A' + 'a';
        if (cb >= 'A' && cb <= 'Z') cb = cb - 'A' + 'a';
        if (ca != cb) return false;
    }
    return true;
}

// Returns true if `bufPtr` is a valid CXStr buffer pointer whose data matches
// `target` case-insensitively. Mirrors mq2_bridge's IsCXStrMatch — the same
// algorithm that HeapScanForWidget uses successfully to identify widget
// definitions by name.
//
// CXStr buffer layout (Dalaya):
//   [+0x00]  ??? (likely refcount / header)
//   [+0x08]  int length
//   [+0x14]  char data[]  (20-byte header, data follows)
static bool IsCXStrMatchCI(uintptr_t bufPtr, const char *target, int targetLen) {
    if (bufPtr < 0x10000 || bufPtr > 0x7FFFFFFF) return false;
    __try {
        int len = *(const int *)(bufPtr + 0x08);
        if (len != targetLen) return false;
        const char *src = (const char *)(bufPtr + 0x14);
        if (!CIEqualsN(src, target, targetLen)) return false;
        // Ensure null-terminated at expected length (defense against lengthh-padding races)
        return src[targetLen] == '\0';
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Offset cache — learned from first successful lookup. mq2_bridge uses the
// same caching pattern in g_pSidlPieceOffset. Once we know which offset
// within a widget's body holds the pSidlPiece/CXMLData pointer, every
// subsequent lookup is a single deref + CXStr match.
// (Non-static so InvalidateStep3Caches at the top of the file can reset
// them on UNLOAD — matches the g_cachedCSidlMgr forward-decl pattern.)
int g_learnedDefOffset = -1;   // offset from widget body to def ptr
int g_learnedNameOffset = -1;  // offset from def to Name CXStr (usually 0x18)

// Scan a CXWnd's body for an embedded pointer to a CXStr whose buffer
// matches `name`. Two-tier:
//   1. Fast path — if g_learnedDefOffset is known, only check that.
//   2. Discovery path — on first call, try a bounded set of candidate
//      offsets (not exhaustive — exhaustive was too slow during login).
// Skips definitions (0xFFFFFFFF at +0x10).
static bool WidgetNameMatches(const void *pWnd, const char *target, int targetLen) {
    if (!pWnd || !target) return false;
    __try {
        const uint8_t *body = (const uint8_t *)pWnd;

        // Skip definitions — we want LIVE widgets only. 0xFFFFFFFF at +0x10
        // is mq2_bridge's established def-marker check.
        uintptr_t at10 = *(const uintptr_t *)(body + 0x10);
        if (at10 == 0xFFFFFFFF) return false;

        // Fast path — cached offset from a prior successful lookup
        if (g_learnedDefOffset > 0) {
            uintptr_t val = *(const uintptr_t *)(body + g_learnedDefOffset);
            if (IsCXStrMatchCI(val, target, targetLen)) return true;
            if (val >= 0x10000 && val < 0x7FFFFFFF) {
                int nameOff = g_learnedNameOffset > 0 ? g_learnedNameOffset : 0x18;
                __try {
                    uintptr_t inner = *(const uintptr_t *)(val + nameOff);
                    if (IsCXStrMatchCI(inner, target, targetLen)) return true;
                } __except (EXCEPTION_EXECUTE_HANDLER) {}
            }
            return false;  // cached offset didn't match — don't rescan
        }

        // Discovery path — bounded candidate offsets.
        // 0x2C is mq2_bridge's cached g_pSidlPieceOffset for eqgame widgets.
        // 0xD4/0xD8 are common in eqmain-side CXWnd derivatives (observed in
        // probe dump — widget[0] +D8=0x310000 etc.).
        static const uint32_t candidateDefOffs[] = {
            0x2C, 0xD4, 0xD8, 0x1C, 0x98, 0x9C, 0xA4, 0xB0, 0xC0, 0xE0, 0x100, 0x1A8
        };
        static const uint32_t candidateNameOffs[] = {
            0x18, 0x08, 0x1C, 0x20  // Name-CXStr position in def
        };
        for (uint32_t defOff : candidateDefOffs) {
            uintptr_t val = *(const uintptr_t *)(body + defOff);
            // Direct CXStr-buf match at this offset
            if (IsCXStrMatchCI(val, target, targetLen)) {
                g_learnedDefOffset = (int)defOff;
                g_learnedNameOffset = 0;  // direct CXStr, no indirection
                return true;
            }
            if (val < 0x10000 || val >= 0x7FFFFFFF) continue;
            // Indirect — val is def pointer, Name CXStr lives at one of
            // several offsets inside the def.
            for (uint32_t nameOff : candidateNameOffs) {
                __try {
                    uintptr_t inner = *(const uintptr_t *)(val + nameOff);
                    if (IsCXStrMatchCI(inner, target, targetLen)) {
                        g_learnedDefOffset = (int)defOff;
                        g_learnedNameOffset = (int)nameOff;
                        DI8Log("mq2port: LEARNED widget def offset +0x%X, name offset +0x%X",
                               (unsigned)defOff, (unsigned)nameOff);
                        return true;
                    }
                } __except (EXCEPTION_EXECUTE_HANDLER) {}
            }
        }
        return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Recursive tree walk — verbatim port of MQ2's RecurseAndFindName, but
// reads struct offsets instead of calling into dinput8. Depth-capped to
// prevent runaway on corrupt sibling/child pointers. Node-count cap to
// bound worst-case scan time.
static int g_recurseNodeCount = 0;  // reset per top-level call
static void *RecurseAndFindNameNative(void *pWnd, const char *name, int nameLen, int depth) {
    if (!pWnd || depth > 32 || g_recurseNodeCount > 5000) return nullptr;
    if ((uintptr_t)pWnd < 0x10000 || (uintptr_t)pWnd > 0x7FFFFFFF) return nullptr;
    g_recurseNodeCount++;

    if (WidgetNameMatches(pWnd, name, nameLen)) return pWnd;

    // Recurse into first child
    void *pChild = nullptr;
    __try {
        pChild = *(void **)((uintptr_t)pWnd + OFF_CXWnd_FirstChild);
    } __except (EXCEPTION_EXECUTE_HANDLER) { pChild = nullptr; }
    if (pChild && pChild != pWnd) {  // guard against trivial self-cycle
        void *hit = RecurseAndFindNameNative(pChild, name, nameLen, depth + 1);
        if (hit) return hit;
    }

    // Recurse into next sibling
    void *pNext = nullptr;
    __try {
        pNext = *(void **)((uintptr_t)pWnd + OFF_CXWnd_NextSibling);
    } __except (EXCEPTION_EXECUTE_HANDLER) { pNext = nullptr; }
    if (pNext && pNext != pWnd) {
        return RecurseAndFindNameNative(pNext, name, nameLen, depth + 1);
    }

    return nullptr;
}

// MQ2-faithful widget-by-name resolver — with an important caveat.
//
// ATTEMPTED STRATEGY:
//   For each top-level widget in eqmain's pWindows, call Dalaya MQ2's
//   exported CXWnd::GetChildItem(char*) — which internally runs MQ2's
//   RecurseAndFindName walk over that subtree and matches by
//   pXMLData->Name or pXMLData->ScreenID case-insensitively
//   (macroquest-rof2-emu/src/eqlib/src/game/CXWnd.cpp:115-160).
//
// WHY IT DOESN'T (YET) WORK FOR LOGIN WIDGETS:
//   Dalaya's dinput8.dll export is `?GetChildItem@CXWnd@EQClasses@@...`
//   — the `EQClasses::CXWnd` (eqgame-side, post-login, in-game) class
//   hierarchy. Login-frontend widgets live in the separate `eqmain::CXWnd`
//   hierarchy (MQ2 ROF2-emu splits them via `namespace eqmain { class CXWnd {...} }`
//   in LoginFrontend.h:260 — different field offsets, different vtable).
//   Calling EQClasses' GetChildItem on an eqmain widget reads fields at
//   offsets the widget doesn't have → RecurseAndFindName misses everything
//   and returns nullptr for all 205 top-level widgets during login.
//
//   MQ2's actual solution is to resolve `eqmain::CXWnd::GetChildItem` by
//   pattern-scanning eqmain.dll directly (EQLIB_OBJECT declaration with no
//   C++ body — linked at runtime via offset-file). Dalaya's eqmain.dll
//   exports ZERO CXWnd symbols, so we'd need to pattern-scan the function
//   ourselves. That's deferred — tracked as a TODO for a future session.
//
// CURRENT BEHAVIOR:
//   - Call still fires: useful for IN-GAME widget lookups (EQClasses
//     namespace — post-login char-select, world UI). Those DO match the
//     resolved function's hierarchy and will return real widgets.
//   - For login-frontend (LOGIN_*, YESNO_*, OK_*, Character_List), loop
//     returns nullptr cleanly and caller falls through to the existing
//     heap-scan + cross-reference tier in mq2_bridge::FindWindowByName
//     (which IS an effective backwards-RecurseAndFindName: finds the def
//     by name in heap, then finds the live CXWnd that references it).
//
// NOT a heuristic, not a band-aid. The heap-scan fallback IS the faithful
// algorithm — just implemented as name→def→widget instead of tree→name.
// One-shot diagnostic — dump what's at offset +0x2C, and the CXStr buffer
// at +0x2C->+0x18, for the first 10 widgets in pWindows. Helps verify the
// OFF_CXWnd_pSidlPiece / OFF_CXMLData_Name offsets are correct for eqmain
// widgets (not just eqgame-side ones we derived them from).
static void DumpWidgetOffsetsOnce(void *const *screens, int nScreens) {
    static bool s_dumped = false;
    if (s_dumped || nScreens == 0) return;
    s_dumped = true;

    DI8Log("mq2port: ═══ OFFSET PROBE ═══ checking +0x2C (pSidlPiece) on first 12 widgets");
    for (int i = 0; i < nScreens && i < 12; i++) {
        void *w = screens[i];
        if (!w) continue;
        uintptr_t val_2C = 0, val_20 = 0, val_24 = 0, val_28 = 0;
        uintptr_t val_30 = 0, val_34 = 0, val_D8 = 0;
        __try {
            val_20 = *(uintptr_t *)((uint8_t *)w + 0x20);
            val_24 = *(uintptr_t *)((uint8_t *)w + 0x24);
            val_28 = *(uintptr_t *)((uint8_t *)w + 0x28);
            val_2C = *(uintptr_t *)((uint8_t *)w + 0x2C);
            val_30 = *(uintptr_t *)((uint8_t *)w + 0x30);
            val_34 = *(uintptr_t *)((uint8_t *)w + 0x34);
            val_D8 = *(uintptr_t *)((uint8_t *)w + 0xD8);
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        DI8Log("  widget[%d] %p: +20=0x%08X +24=0x%08X +28=0x%08X +2C=0x%08X +30=0x%08X +34=0x%08X +D8=0x%08X",
               i, w, (unsigned)val_20, (unsigned)val_24, (unsigned)val_28,
               (unsigned)val_2C, (unsigned)val_30, (unsigned)val_34, (unsigned)val_D8);

        // Also dump +0x08, +0x10 (NextSib, FirstChild from existing code)
        // and try a range of other offsets that might be pSidlPiece in eqmain.
        uintptr_t val_08 = 0, val_10 = 0, val_18 = 0, val_38 = 0, val_3C = 0, val_40 = 0, val_80 = 0, val_84 = 0;
        __try {
            val_08 = *(uintptr_t *)((uint8_t *)w + 0x08);  // NextSibling
            val_10 = *(uintptr_t *)((uint8_t *)w + 0x10);  // FirstChild
            val_18 = *(uintptr_t *)((uint8_t *)w + 0x18);  // ?
            val_38 = *(uintptr_t *)((uint8_t *)w + 0x38);
            val_3C = *(uintptr_t *)((uint8_t *)w + 0x3C);
            val_40 = *(uintptr_t *)((uint8_t *)w + 0x40);
            val_80 = *(uintptr_t *)((uint8_t *)w + 0x80);
            val_84 = *(uintptr_t *)((uint8_t *)w + 0x84);
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        DI8Log("      +08=0x%08X(sib) +10=0x%08X(child) +18=0x%08X +38=0x%08X +3C=0x%08X +40=0x%08X +80=0x%08X +84=0x%08X",
               (unsigned)val_08, (unsigned)val_10, (unsigned)val_18,
               (unsigned)val_38, (unsigned)val_3C, (unsigned)val_40,
               (unsigned)val_80, (unsigned)val_84);

        // If FirstChild looks like a pointer, try to read child's vtable + CXStr-at-various-offsets
        if (val_10 >= 0x10000 && val_10 < 0x7FFFFFFF && val_10 != (uintptr_t)w) {
            __try {
                uintptr_t childVT = *(uintptr_t *)val_10;
                DI8Log("      child[0]=0x%08X vt=0x%08X", (unsigned)val_10, (unsigned)childVT);
                // Probe child at +0x2C, +0x20, +0xD4
                uintptr_t cAt2C = *(uintptr_t *)(val_10 + 0x2C);
                uintptr_t cAtD4 = *(uintptr_t *)(val_10 + 0xD4);
                uintptr_t cAtD8 = *(uintptr_t *)(val_10 + 0xD8);
                DI8Log("      child[0]: +2C=0x%08X +D4=0x%08X +D8=0x%08X",
                       (unsigned)cAt2C, (unsigned)cAtD4, (unsigned)cAtD8);
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        }

        // If +0x2C looks like a pointer, probe its contents for a CXStr buf
        if (val_2C >= 0x10000 && val_2C < 0x7FFFFFFF) {
            char preview[32] = {0};
            uintptr_t bufAt18 = 0, bufAt08 = 0, bufAt04 = 0, bufAt10 = 0;
            __try {
                bufAt04 = *(uintptr_t *)(val_2C + 0x04);
                bufAt08 = *(uintptr_t *)(val_2C + 0x08);
                bufAt10 = *(uintptr_t *)(val_2C + 0x10);
                bufAt18 = *(uintptr_t *)(val_2C + 0x18);
            } __except (EXCEPTION_EXECUTE_HANDLER) {}

            // For each potential CXStr offset, try to read the string
            struct { uintptr_t at; uintptr_t off; } tries[] = {
                {bufAt04, 0x04}, {bufAt08, 0x08}, {bufAt10, 0x10}, {bufAt18, 0x18}
            };
            for (auto &t : tries) {
                if (t.at < 0x10000 || t.at > 0x7FFFFFFF) continue;
                __try {
                    int len = *(int *)(t.at + 0x08);
                    if (len > 0 && len < 64) {
                        const char *src = (const char *)(t.at + 0x14);
                        int copy = len < 30 ? len : 30;
                        for (int k = 0; k < copy; k++) {
                            char c = src[k];
                            preview[k] = (c >= 32 && c < 127) ? c : '.';
                        }
                        preview[copy] = '\0';
                        DI8Log("    [2C+%02X]=0x%08X → buf(len=%d)='%s'",
                               (unsigned)t.off, (unsigned)t.at, len, preview);
                        preview[0] = '\0';
                    }
                } __except (EXCEPTION_EXECUTE_HANDLER) {}
            }
        }
    }
    DI8Log("mq2port: ═══ END OFFSET PROBE ═══");
}

void *FindWidgetByKnownName(const char *name) {
    if (!name) return nullptr;
    if (!FindLiveCXWndManager()) return nullptr;      // pWindows not ready yet

    // One-shot tree diagnostic on first call (prints pWindows class layout).
    DumpStep3TreeDiagnostic();

    void *screens[256];
    int nScreens = EnumerateTopLevelScreens(screens, 256);
    if (nScreens == 0) return nullptr;

    // Offset probe disabled — data already gathered. Re-enable if widget
    // layout changes (Dalaya update): DumpWidgetOffsetsOnce(screens, nScreens);

    // Primary path: native RecurseAndFindName walk (our own C++ port — see
    // comment block on RecurseAndFindNameNative above). Walks the live tree
    // from each top-level in pWindows. Returns the first widget whose
    // CXMLData.Name case-insensitively matches `name`.
    int nameLen = (int)strlen(name);
    g_recurseNodeCount = 0;  // reset global budget for this lookup
    for (int i = 0; i < nScreens; i++) {
        void *hit = RecurseAndFindNameNative(screens[i], name, nameLen, 0);
        if (hit) {
            static int s_hitLog = 0;
            if (s_hitLog < 40) {
                DI8Log("mq2port: RecurseAndFindName('%s') → %p via pWindows[%d]=%p",
                       name, hit, i, screens[i]);
                s_hitLog++;
            }
            return hit;
        }
    }

    // Secondary path: dinput8's EQClasses::GetChildItem — works for in-game
    // (EQClasses-namespace) widgets if they ever end up in pWindows. Skipped
    // if ResolveMQ2FaithfulFunctions failed.
    if (g_fnCXWnd_GetChildItem_Char) {
        if (!g_swapInstalled) InstallPSidlMgrSwap();
        for (int i = 0; i < nScreens; i++) {
            void *parent = screens[i];
            if (!parent) continue;
            void *hit = nullptr;
            __try {
                hit = g_fnCXWnd_GetChildItem_Char(parent, name);
            } __except (EXCEPTION_EXECUTE_HANDLER) {
                hit = nullptr;
            }
            if (hit) {
                DI8Log("mq2port: dinput8 GetChildItem fallback('%s') → %p via pWindows[%d]=%p",
                       name, hit, i, parent);
                return hit;
            }
        }
    } else {
        if (!ResolveMQ2FaithfulFunctions()) { /* ignore — native walk is primary */ }
    }

    // Rate-limited "no hit" diagnostic so logs show this tier attempted
    // resolution cleanly. Caller proceeds to heap-scan tier.
    static int s_missLog = 0;
    if (s_missLog < 8) {
        DI8Log("mq2port: no hit for '%s' across %d top-level widgets (native walk "
               "+ dinput8 fallback both miss — falling through to heap scan).",
               name, nScreens);
        s_missLog++;
    }
    return nullptr;
}

} // namespace EQMainOffsets
