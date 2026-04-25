// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// Native/eqmain_cxstr.cpp -- Combo G CXStr ctor/dtor + SetWindowText helper
//
// See eqmain_cxstr.h for the rationale, threat model, and dormant-code
// gate. Recon source: Native/recon/phase4-cxstr-recon.md.
//
// All RVAs and prologue bytes below are sourced from rizin static
// analysis of `C:/Users/nate/proggy/Everquest/Eqfresh/eqmain.dll`
// (Dalaya x86 PE32, compiled 2013-05-11). To regenerate after a
// Dalaya patch:
//   rizin -q -c 'pxw 16 @ 0x100473d0' Native/recon/eqmain.dll
//   rizin -q -c 'pxw 16 @ 0x100472d0' Native/recon/eqmain.dll
//   rizin -q -c 'pxw 16 @ 0x10097af0' Native/recon/eqmain.dll
// And update PROLOGUE_* arrays below.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include "eqmain_cxstr.h"

// DI8Log is defined in eqswitch-di8.cpp; matches the convention used by
// eqmain_offsets.cpp. Same translation unit network, same log sink.
void DI8Log(const char *fmt, ...);

namespace EQMainCXStr {

// ─── Recon constants ────────────────────────────────────────
// RVAs relative to eqmain.dll ImageBase (0x10000000 in the recon binary;
// runtime base is whatever the loader picks, retrieved from
// EQMainOffsets::GetRange()).
static constexpr uint32_t RVA_CXStr_Ctor              = 0x000473d0;
static constexpr uint32_t RVA_CXStr_FreeRep           = 0x000472d0;
static constexpr uint32_t RVA_CEditWnd_SetWindowText  = 0x00097af0;

// Prologue signatures (first 8 bytes). DO NOT trust transcriptions —
// these are sourced from `rizin pxw 16` directly. The 2026-04-24 handoff
// at handoff-eqswitch-combo-g-cebw-extraction-RESUME-20260424.md said the
// SetWindowText prologue ended in `ff 35`; actual bytes are `6A FF`
// (push imm8 sign-extended -1). The bug would have caused this runtime
// check to always-mismatch and silently kill Combo G.
static constexpr uint8_t PROLOGUE_CTOR[8]    = { 0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x08, 0x56, 0x57 };
static constexpr uint8_t PROLOGUE_FREEREP[8] = { 0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68, 0xD8, 0x15 };
static constexpr uint8_t PROLOGUE_SETTEXT[8] = { 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00, 0x6A, 0xFF };

// ─── Cached function pointers ───────────────────────────────
typedef CXStr_Dalaya *(__thiscall *FN_CtorFromCStr)(CXStr_Dalaya *self, const char *s);
typedef void          (__thiscall *FN_FreeRep)     (CXStr_Dalaya *self, CStrRep_Dalaya *rep);
typedef void          (__thiscall *FN_SetWindowText)(void *widget, const CXStr_Dalaya *text);

// Updates only happen under the same single-writer discipline as
// eqmain_offsets.cpp's range cache (loader-lock-held callback or
// serialized init thread). Readers see {0,0} or coherent {ctor,free}.
static FN_CtorFromCStr g_ctor       = nullptr;
static FN_FreeRep      g_freeRep    = nullptr;
static uintptr_t       g_ctorAddr   = 0;
static uintptr_t       g_freeAddr   = 0;

// ─── SEH-wrapped helpers ────────────────────────────────────
// Read first 8 bytes of `addr` and compare to expected signature.
// Returns false on fault. Used to validate function pointers before
// caching them — a faulting read here is a strong signal the address
// doesn't point to executable code.
static bool MatchesPrologue(uintptr_t addr, const uint8_t (&expected)[8]) {
    uint8_t actual[8];
    __try {
        memcpy(actual, reinterpret_cast<const void *>(addr), 8);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    return memcmp(actual, expected, 8) == 0;
}

// ─── Lifecycle ──────────────────────────────────────────────
bool ResolveCXStrFunctions(uintptr_t eqmainBase) {
    if (!eqmainBase) {
        DI8Log("eqmain_cxstr: ResolveCXStrFunctions skipped — base=0");
        return false;
    }

    uintptr_t ctorAddr = eqmainBase + RVA_CXStr_Ctor;
    uintptr_t freeAddr = eqmainBase + RVA_CXStr_FreeRep;

    if (!MatchesPrologue(ctorAddr, PROLOGUE_CTOR)) {
        DI8Log("eqmain_cxstr: ctor prologue MISMATCH at 0x%08X — Dalaya patch shifted "
               "CXStr::CXStr(const char*); Combo G unsafe, refusing to cache",
               (unsigned)ctorAddr);
        ClearResolvedFunctions();
        return false;
    }
    if (!MatchesPrologue(freeAddr, PROLOGUE_FREEREP)) {
        DI8Log("eqmain_cxstr: FreeRep prologue MISMATCH at 0x%08X — Dalaya patch shifted "
               "CXStr::FreeRep; Combo G unsafe, refusing to cache",
               (unsigned)freeAddr);
        ClearResolvedFunctions();
        return false;
    }

    g_ctorAddr = ctorAddr;
    g_freeAddr = freeAddr;
    g_ctor     = reinterpret_cast<FN_CtorFromCStr>(ctorAddr);
    g_freeRep  = reinterpret_cast<FN_FreeRep>(freeAddr);

    DI8Log("eqmain_cxstr: resolved — ctor=0x%08X (eqmain+0x%05X) freeRep=0x%08X (eqmain+0x%05X)",
           (unsigned)ctorAddr, RVA_CXStr_Ctor,
           (unsigned)freeAddr, RVA_CXStr_FreeRep);
    return true;
}

bool HasResolvedFunctions() {
    return g_ctor != nullptr && g_freeRep != nullptr;
}

void ClearResolvedFunctions() {
    g_ctor     = nullptr;
    g_freeRep  = nullptr;
    g_ctorAddr = 0;
    g_freeAddr = 0;
}

void GetResolvedAddresses(uintptr_t *outCtor, uintptr_t *outFreeRep) {
    if (outCtor)    *outCtor    = g_ctorAddr;
    if (outFreeRep) *outFreeRep = g_freeAddr;
}

// ─── CXStr operations ───────────────────────────────────────
bool ConstructFromCStr(CXStr_Dalaya *out, const char *s) {
    if (!out) return false;
    out->m_data = nullptr;
    if (!g_ctor) {
        // Functions not resolved — Combo G unavailable. Per fail-mode rule,
        // caller must not regress to dinput8; they should treat false as
        // hard-fail and abort the autologin attempt.
        return false;
    }
    __try {
        g_ctor(out, s);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // Ctor faulted mid-allocation. m_data may be in undefined state;
        // null it explicitly so Free() is a safe no-op for the caller.
        DI8Log("eqmain_cxstr: ConstructFromCStr SEH fault — ctor=0x%08X arg=\"%.32s\"",
               (unsigned)g_ctorAddr, s ? s : "(null)");
        out->m_data = nullptr;
        return false;
    }
}

void Free(CXStr_Dalaya *x) {
    if (!x || !x->m_data) return;
    if (!g_freeRep) {
        // Resolver was cleared between Construct and Free. The CStrRep
        // will leak (refcount never decremented) but that's preferable
        // to calling a null function pointer or a stale FreeRep address
        // from a prior eqmain load. Log so the leak is visible.
        DI8Log("eqmain_cxstr: Free LEAK — freeRep unresolved, CStrRep at 0x%08X "
               "will not be released", (unsigned)(uintptr_t)x->m_data);
        x->m_data = nullptr;
        return;
    }
    __try {
        g_freeRep(x, x->m_data);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("eqmain_cxstr: Free SEH fault — freeRep=0x%08X CStrRep=0x%08X",
               (unsigned)g_freeAddr, (unsigned)(uintptr_t)x->m_data);
    }
    x->m_data = nullptr;
}

// ─── SetWindowText slot resolution + invocation ─────────────
// Probe vtable[73] first (the recon-time slot), then [72] and [74] as
// adjacent fallbacks. Each candidate is prologue-validated before being
// returned. Returns nullptr if all three slots fail — caller's
// fail-mode escalation (AOB rescan TODO; hard-fail loud) takes over.
//
// Why probe adjacent slots: on 2026-04-16 we hit the exact off-by-one
// drift this is designed to recover from (Dalaya 89-slot CXWnd vs MQ2
// RoF2 90-slot, putting SetWindowText at slot 73 not 74). If a future
// Dalaya patch adds or removes a virtual, we want to auto-recover
// without a code change.
static FN_SetWindowText ResolveSetWindowTextSlot(void *pWnd) {
    if (!pWnd) return nullptr;

    void **vtbl = nullptr;
    __try {
        vtbl = *reinterpret_cast<void ***>(pWnd);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
    if (!vtbl) return nullptr;

    static constexpr int probe_order[] = { 73, 72, 74 };
    for (int slot : probe_order) {
        uintptr_t fnAddr = 0;
        __try {
            fnAddr = reinterpret_cast<uintptr_t>(vtbl[slot]);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            continue;
        }
        if (!fnAddr) continue;
        if (MatchesPrologue(fnAddr, PROLOGUE_SETTEXT)) {
            if (slot != 73) {
                DI8Log("eqmain_cxstr: SetWindowText found at slot %d (drifted from 73) "
                       "fn=0x%08X — Dalaya vtable layout has shifted",
                       slot, (unsigned)fnAddr);
            }
            return reinterpret_cast<FN_SetWindowText>(fnAddr);
        }
    }

    // All three probed slots failed prologue validation. Either the vtable
    // is not a CEditWnd, or the SetWindowText body relocated by more than
    // 1 slot. Phase 4b will add an AOB rescan over eqmain's .text range
    // here; for now we hard-fail the call.
    DI8Log("eqmain_cxstr: SetWindowText slot probe EXHAUSTED — slots 72/73/74 all "
           "failed prologue check on vtable=0x%08X. AOB rescan not yet implemented.",
           (unsigned)(uintptr_t)vtbl);
    return nullptr;
}

bool WriteEditTextDirect(void *pEditWnd, const char *text) {
    if (!pEditWnd || !text) return false;
    if (!HasResolvedFunctions()) {
        DI8Log("eqmain_cxstr: WriteEditTextDirect refused — CXStr functions unresolved");
        return false;
    }

    FN_SetWindowText setText = ResolveSetWindowTextSlot(pEditWnd);
    if (!setText) {
        // Already logged inside the resolver. Caller should treat this as
        // hard-fail per the fail-mode rule.
        return false;
    }

    CXStr_Dalaya arg;
    arg.m_data = nullptr;
    if (!ConstructFromCStr(&arg, text)) {
        DI8Log("eqmain_cxstr: WriteEditTextDirect — CXStr construction failed");
        return false;
    }

    bool ok = false;
    __try {
        setText(pEditWnd, &arg);
        ok = true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("eqmain_cxstr: WriteEditTextDirect SEH fault — setText=%p widget=%p",
               (void *)setText, pEditWnd);
    }

    Free(&arg);
    return ok;
}

} // namespace EQMainCXStr
