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
