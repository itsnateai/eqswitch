// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// Native/eqmain_offsets.h -- eqmain.dll-side function pointer resolution
//
// STEP 2A of the v8 MQ2 port. This header declares the range-tracking and
// ownership-testing primitives used to route widget operations to the
// correct DLL's function bodies.
//
// Background:
//   mq2_bridge's g_fnSetWindowText / g_fnWndNotification are resolved from
//   dinput8.dll exports — these are EQClasses::CXWnd:: functions (eqgame
//   side). When called on a widget that lives inside eqmain.dll (separate
//   parallel class hierarchy with different member offsets and vtable
//   layout), the functions read/write at the wrong offsets and SEH-fault.
//   Live 2026-04-16 log evidence: 22 SEH faults per login, all on widgets
//   with vtables inside eqmain.dll's load range (0x71E20000-0x72179000).
//
// MQ2 solves this by pattern-resolving eqmain-side function pointers at
// DLL-notification LOAD time and dispatching based on which DLL owns the
// widget (see LoginFrontend.h's separate eqmain::CXWnd class parallel to
// the global CXWnd). This is the mirror of that approach for EQSwitch.
//
// Scope of STEP 2A (this file now):
//   - Range tracking: cache eqmain's [base, base+SizeOfImage) on LOAD, clear
//     on UNLOAD.
//   - Ownership test: IsEQMainWidget() checks whether a CXWnd's vtable
//     pointer falls inside the cached range.
//   - No function resolution yet — callers use the result to LOG which
//     widgets are eqmain-side so STEP 2B has empirical data.
//
// Scope of STEP 2B (next session):
//   - Resolve eqmain-side function pointers for SetWindowTextA / WndNotification
//     / GetChildItem / GetXMLData (strategy TBD once Step 2A data is in).
//   - Route widget ops through them when IsEQMainWidget(pWnd) is true.
//
// Thread safety: all operations are simple pointer comparisons against
// snapshot-stable globals updated only from the loader-lock-held DLL
// notification callback. Readers see a consistent base/size pair because
// updates go through InterlockedExchangePointer / a single 64-bit write.
// The "is loaded" check is a single pointer read.

#pragma once
#include <stdint.h>

namespace EQMainOffsets {

// ─── Lifecycle ────────────────────────────────────────────────
// Called from LdrDllNotificationCallback in eqswitch-di8.cpp on eqmain
// LOAD. Caches base/size and logs the registration. Idempotent — repeat
// calls with the same base are no-ops; calls with a different base log
// a warning and overwrite (shouldn't happen for in-process eqmain).
void OnEQMainLoaded(uintptr_t dllBase, uint32_t sizeOfImage);

// Called on eqmain UNLOAD. Clears cached range. Any in-flight
// IsEQMainWidget() check after this point returns false.
void OnEQMainUnloaded();

// ─── Ownership test ───────────────────────────────────────────
// Returns true if pWnd is a plausible CXWnd whose vtable pointer lives
// inside the cached eqmain.dll range. Performs an SEH-wrapped deref of
// *(uintptr_t*)pWnd to read the vtable pointer; returns false on fault
// (treating unreadable memory as not-eqmain so callers fall back to the
// existing eqgame-side code path).
//
// Safety note: this is called on pointers sourced from heap-scan /
// cross-ref results which can be stale between charselect and eqmain
// unload. The SEH wrapper means IsEQMainWidget(stale_pointer) returns
// false instead of crashing.
bool IsEQMainWidget(const void *pWnd);

// ─── Diagnostics ──────────────────────────────────────────────
// Returns cached eqmain range. Base=0, size=0 when unloaded.
// Used by log statements so operators can correlate widget pointers to
// the active eqmain load without grep-walking earlier log lines.
void GetRange(uintptr_t *outBase, uint32_t *outSize);

} // namespace EQMainOffsets
