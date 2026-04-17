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
}

void OnEQMainUnloaded() {
    uintptr_t priorBase = g_base;
    uint32_t priorSize = g_size;

    // Clear base first so concurrent readers see {0, old_size} — which still
    // yields IsEQMainWidget=false because the range test uses base as the gate.
    InterlockedExchangePointer((PVOID volatile *)&g_base, nullptr);
    InterlockedExchange((volatile LONG *)&g_size, 0);

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

} // namespace EQMainOffsets
