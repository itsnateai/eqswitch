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
// And update PROLOGUE_* arrays below.
//
// Iter 11 (2026-04-25): replaced vtable-slot SetWindowText path with
// direct CXStr-field assignment at +0x1A8, matching MQ2's reference impl
// at MQ2AutoLogin.cpp:1049 (`pWnd->InputText = text`). Slot-73 probe never
// fired successfully across 9 iters — FindLiveCXWnd was returning
// CXMLDataPtr wrappers, not live CEditBaseWnd. Iter 12 will wire
// EQMainWidgets::FindLivePasswordCEditWnd as the widget source.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include "eqmain_cxstr.h"
#include "eqmain_offsets.h"

// DI8Log is defined in eqswitch-di8.cpp; matches the convention used by
// eqmain_offsets.cpp. Same translation unit network, same log sink.
void DI8Log(const char *fmt, ...);

namespace EQMainCXStr {

// ─── Recon constants ────────────────────────────────────────
// RVAs relative to eqmain.dll ImageBase (0x10000000 in the recon binary;
// runtime base is whatever the loader picks, retrieved from
// EQMainOffsets::GetRange()).
static constexpr uint32_t RVA_CXStr_Ctor    = 0x000473d0;
static constexpr uint32_t RVA_CXStr_FreeRep = 0x000472d0;

// Prologue signatures (first 8 bytes). DO NOT trust transcriptions —
// these are sourced from `rizin pxw 16` directly.
static constexpr uint8_t PROLOGUE_CTOR[8]    = { 0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x08, 0x56, 0x57 };
static constexpr uint8_t PROLOGUE_FREEREP[8] = { 0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68, 0xD8, 0x15 };

// CEditBaseWnd::InputText offset on Dalaya x86 — pinned iter 8 by reading
// 'gotquiz1' from this offset on a known live CEditWnd whose autologin
// keystroke fallback had filled the username. Mirrors MQ2 source's
// LoginFrontend.h:799 (`/*0x278*/ CXStr InputText`) compressed for x86.
static constexpr uint32_t OFFSET_INPUT_TEXT = 0x1A8;

// ─── Cached function pointers ───────────────────────────────
typedef CXStr_Dalaya *(__thiscall *FN_CtorFromCStr)(CXStr_Dalaya *self, const char *s);
typedef void          (__thiscall *FN_FreeRep)     (CXStr_Dalaya *self, CStrRep_Dalaya *rep);

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

// ─── Direct InputText field assignment (iter 11) ─────────────
// Writes `text` into the widget's InputText CXStr field at +0x1A8 by
// composing the existing CXStr ctor + FreeRep primitives. Replaces the
// prior vtable[73] SetWindowText path which never fired successfully on a
// real widget across iters 1-9 (FindLiveCXWnd was returning CXMLDataPtr
// wrappers, not live CEditBaseWnd).
//
// Mirrors MQ2's reference impl at MQ2AutoLogin.cpp:1049:
//   `pWnd->InputText = text;`
// CXStr's operator= internally Frees old CStrRep then Constructs new one
// with new text — which is exactly what we compose here.
//
// Caller MUST pass a real live CEditBaseWnd / CEditWnd. Iter 12's wiring
// will source these from EQMainWidgets::FindLivePasswordCEditWnd, which
// filters by exact vtable match. If pEditWnd is a wrapper or stale, the
// SEH-wrapped touch-test on +0x1A8 catches and we return false cleanly so
// the caller falls back to keystroke (b142afe path).
bool WriteEditTextDirect(void *pEditWnd, const char *text) {
    if (!pEditWnd || !text) return false;
    if (!HasResolvedFunctions()) {
        DI8Log("eqmain_cxstr: WriteEditTextDirect refused — CXStr functions unresolved");
        return false;
    }

    // Vtable gate (added 2026-04-25 after iter 12 smoke showed false-success
    // on CXMLDataPtr wrappers). The wrapper has readable memory at +0x1A8
    // (just unrelated bytes), so the SEH touch-test below DOESN'T fault.
    // Result: WriteEditTextDirect reported success while writing into the
    // wrong widget's memory; login_sm then clicked LOGIN_ConnectButton
    // without the password being in the actual field, and C# fell back to
    // a delayed keystroke retry. Reject any non-edit-widget vtable here so
    // the caller's false branch fires and the b142afe DI8 SHM path takes
    // over instantly instead of after a doomed connect-click cycle.
    if (!EQMainOffsets::IsEQMainEditWidget(pEditWnd)) {
        // IsEQMainEditWidget already logs a rate-limited rejection line
        // identifying the wrong vtable, so we don't re-log here.
        return false;
    }

    CXStr_Dalaya *inputText = nullptr;
    __try {
        inputText = reinterpret_cast<CXStr_Dalaya *>(
            reinterpret_cast<uint8_t *>(pEditWnd) + OFFSET_INPUT_TEXT);
        // Touch-test: read m_data without committing to a write yet. If
        // pEditWnd isn't a real CEditBaseWnd, +0x1A8 lives outside the
        // object's allocation and this faults.
        (void)inputText->m_data;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("eqmain_cxstr: WriteEditTextDirect SEH on field read — pEditWnd=%p "
               "+0x%X unreadable; widget likely not a real CEditBaseWnd",
               pEditWnd, OFFSET_INPUT_TEXT);
        return false;
    }

    // CXStr operator= semantics: Free releases the old CStrRep (refcount-
    // safe — other holders keep their reference) and nulls m_data, then
    // ConstructFromCStr allocates a new CStrRep from eqmain's CXFreeList
    // and writes its pointer back to m_data. The widget's InputText slot
    // is updated in place.
    Free(inputText);
    if (!ConstructFromCStr(inputText, text)) {
        DI8Log("eqmain_cxstr: WriteEditTextDirect — ctor failed for text=\"%.32s\"",
               text);
        return false;
    }
    return true;
}

} // namespace EQMainCXStr
