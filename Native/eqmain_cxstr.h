// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// Native/eqmain_cxstr.h -- eqmain-flavored CXStr ctor/dtor + Combo G text-write
//
// PHASE 4 of the v8 MQ2 port (Combo G). Sibling to eqmain_offsets.{h,cpp};
// keeps the eqmain-side CXStr completely separate from the eqgame-side
// EQClasses::CXStr in mq2_bridge.cpp, since the two have:
//   * different allocators (eqmain has its own CXFreeList instance)
//   * different CStrRep layouts (Dalaya 2013 has utf8 at +0x14 with no
//     encoding/freeList fields; modern MQ2 has utf8 at +0x18)
//   * different runtime origins (eqmain.dll has ZERO exports, so its
//     CXStr ctor is unreachable via GetProcAddress and must be RVA-pinned)
//
// Recon source: Native/recon/phase4-cxstr-recon.md (committed 2026-04-24)
//
// ─── DORMANT ────────────────────────────────────────────────
// This translation unit ships as DORMANT CODE. It is NOT yet:
//   * compiled into eqswitch-di8.dll (build.sh / build-di8-inject.sh do
//     not reference these files yet)
//   * called from any live code path in mq2_bridge.cpp,
//     login_state_machine.cpp, or eqswitch-di8.cpp's DllMain
//
// The Native/ binary tree stays at e8faf9b state per
// memory/feedback_eqswitch_e8faf9b_is_anchor.md — wire-up requires a
// dual-box smoke test on Dalaya before flipping any call site to
// WriteEditTextDirect(). Do not enable the build inclusion or invoke
// these functions without that gate.
//
// ─── Threat model ───────────────────────────────────────────
// On every Dalaya patch the eqmain.dll RVAs and prologue bytes can drift.
// The runtime prologue check in ResolveCXStrFunctions() catches this on
// startup; a mismatch means the Combo G path is unsafe and must NOT be
// used for the active autologin attempt. Per
// memory/feedback_eqswitch_no_regression_to_dinput8.md, fail-mode
// hierarchy is:
//   1. Prologue check on ctor / FreeRep at resolve time — false from
//      ResolveCXStrFunctions if either prologue mismatched
//   2. SEH on the field touch at +0x1A8 inside WriteEditTextDirect —
//      false if pEditWnd isn't a real live CEditBaseWnd / CEditWnd
//   3. AOB rescan on prologue signature (TODO Phase 4b)
//   4. Hard-fail loud — log + abort the autologin attempt
// **Never silently regress to dinput8 or keyboard injection.**
//
// Iter 11 (2026-04-25): replaced vtable-slot SetWindowText path with
// direct CXStr-field assignment at +0x1A8. The slot-73 probe never fired
// successfully on a real widget across 9 iters — FindLiveCXWnd was always
// returning CXMLDataPtr wrappers. Direct field write matches MQ2's own
// reference impl at MQ2AutoLogin.cpp:1049 (`pWnd->InputText = text`).

#pragma once
#include <stdint.h>

namespace EQMainCXStr {

// ─── Layout types ───────────────────────────────────────────
// Dalaya 2013 CStrRep — verified by reading fcn.100473d0 body (the ctor
// writes m_data->length at +0x08 and memcpys data at +0x14). NOT compatible
// with modern MQ2's CStrRep which has encoding/freeList fields and shifts
// utf8 to +0x18.
struct CStrRep_Dalaya {
    /*0x00*/ int32_t   refCount;
    /*0x04*/ uint32_t  alloc;
    /*0x08*/ uint32_t  length;
    /*0x0c*/ uint8_t   _pad[0x0c];   // contents unknown; ctor doesn't touch
    /*0x14*/ char      utf8[1];      // flexible array; allocator sizes for length+1
};
static_assert(sizeof(CStrRep_Dalaya) >= 0x15, "CStrRep_Dalaya layout regression");

// CXStr value-type wrapper — single CStrRep* pointer, just like the
// eqgame-side wrapper but pointing at eqmain's older CStrRep layout.
struct CXStr_Dalaya {
    CStrRep_Dalaya *m_data;
};

// ─── Lifecycle ──────────────────────────────────────────────
// Resolve eqmain::CXStr::CXStr(const char*) and eqmain::CXStr::FreeRep
// from the cached eqmain.dll base. Validates each candidate function's
// first 8 prologue bytes against the recon-time signatures BEFORE caching
// the function pointer, so a Dalaya patch that moves these functions
// fails-fast instead of crashing on first call.
//
// Returns:
//   true  — both functions matched their prologue signatures and are
//           cached for use; HasResolvedFunctions() will return true
//   false — one or both prologues did not match; functions are NOT
//           cached; ConstructFromCStr/Free will return false; caller
//           must skip the Combo G path entirely (do NOT regress to
//           dinput8 — abort the autologin attempt instead)
//
// Idempotent: safe to call multiple times. Subsequent calls re-validate
// and overwrite the cached pointers if the base or layout has changed.
//
// Thread-safety: caller must serialize relative to other callers and to
// any concurrent ConstructFromCStr/Free invocations. Intended call site
// is OnEQMainLoaded (loader-lock-held DLL notification callback) or
// during early init thread of eqswitch-di8.dll.
bool ResolveCXStrFunctions(uintptr_t eqmainBase);

// True if ResolveCXStrFunctions completed successfully and pointers are
// cached. Used by callers to gate Combo G code paths.
bool HasResolvedFunctions();

// Clear cached function pointers. Call from OnEQMainUnloaded so
// stale pointers from a prior load don't survive into a new load.
void ClearResolvedFunctions();

// ─── CXStr operations ───────────────────────────────────────
// Construct an eqmain CXStr from a null-terminated C string. The CXStr
// owns a refcounted CStrRep allocated by eqmain's own CXFreeList. Caller
// must call Free(out) before the CXStr goes out of scope or the CStrRep
// leaks (refcount never decremented).
//
// out must be a writeable CXStr_Dalaya. On entry its m_data is overwritten
// — pre-existing m_data will leak if not Free'd first.
//
// Returns false if functions not resolved or if the ctor faulted via SEH.
// On false, out->m_data is left as nullptr.
bool ConstructFromCStr(CXStr_Dalaya *out, const char *s);

// Release the CStrRep held by x. Safe to call on a default-constructed
// (m_data=nullptr) CXStr — does nothing in that case. Sets m_data to
// nullptr after release so re-Free is a safe no-op.
void Free(CXStr_Dalaya *x);

// ─── Combo G high-level helper ──────────────────────────────
// Write `text` into an eqmain CEditWnd via direct CXStr-field assignment
// at the InputText offset (+0x1A8 on Dalaya x86, pinned iter 8). Mirrors
// MQ2's reference impl at MQ2AutoLogin.cpp:1049 (`pWnd->InputText = text`).
// Composes existing CXStr ctor (RVA 0x473d0) + FreeRep (RVA 0x472d0) for
// the in-place Free→Construct sequence that operator= performs internally.
//
// pEditWnd must be an eqmain CEditWnd or CEditBaseWnd. Iter 12's wiring
// will source these from EQMainWidgets::FindLivePasswordCEditWnd which
// filters by exact vtable match. If pEditWnd is a wrapper (CXMLDataPtr)
// or stale, the SEH-wrapped touch-test on +0x1A8 catches it cleanly and
// returns false without faulting.
//
// Returns:
//   true  — InputText field was atomically replaced; the password edit
//           widget should now contain `text`
//   false — CXStr functions unresolved, the +0x1A8 field touch faulted
//           (wrong widget type), or ctor allocation failed. Caller MUST
//           NOT silently fall back to the dinput8 path; per the fail-mode
//           rule, log loudly and abort the autologin attempt.
bool WriteEditTextDirect(void *pEditWnd, const char *text);

// ─── Diagnostic ─────────────────────────────────────────────
// Returns the cached function pointer addresses, or 0 if unresolved.
// Used by log statements to confirm which RVA was pinned at the active
// eqmain load — useful when comparing across Dalaya patches.
void GetResolvedAddresses(uintptr_t *outCtor, uintptr_t *outFreeRep);

} // namespace EQMainCXStr
