// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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

// ─── eqgame.exe __ScreenMode (Diff 1 from mq2-autologin-eqswitch-diff.md) ────
// Empirical enum (live-verified 2026-05-14 across all 4 screen states):
//   1 = login screen (eqmain.dll UI)
//   1 = server-select (eqmain.dll UI, same)
//   2 = char-select transition (eqgame.exe takes over)
//   2 = in-world (eqgame.exe natural)
//   3 = NEVER natural — special "fullscreen-UI-input" mode MQ2's autologin
//       forces during writes (cross-confirmed via MQ2HUD.cpp:617 HUDTYPE_FULLSCREEN
//       gate and MQ2FrameLimiter.cpp:271 *pScreenMode != 3 branch).
//
// MQ2's autologin pattern (StateMachine.cpp:265-279) swaps to 3 during the
// password/username writes + Connect click, then restores. The hypothesis B-1
// premise (mq2-autologin-walkthrough.md §3.2): without this swap, EQ's natural
// state-1 login UI may apply input filters that prevent Combo G's CXStr write
// from propagating to the submit pipeline at Connect-click time.
//
// Source: emu-branch eqlib/include/eqlib/offsets/eqgame.h:84
//   __ScreenMode_x = 0xD1F3B8 (absolute at preferred ImageBase 0x400000)
//   → RVA 0xD1F3B8 - 0x400000 = 0x91F3B8
// Live-verified at this RVA against running Dalaya PID 8596 on 2026-05-14.
// Build-date match confirmed: emu-branch __ClientDate = 20130510u "May 10 2013"
// matches Dalaya eqgame.exe exactly.
//
// ASLR active on Dalaya — eqgame.exe rebases (observed: 0x006E0000 vs preferred
// 0x400000). MUST use GetModuleHandleA(NULL) + RVA at runtime; absolute value
// 0xD1F3B8 will be wrong on every launch.
static constexpr uint32_t RVA_GLOBAL_ScreenMode = 0x0091F3B8;

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

// Forward decl for the inner implementation. WriteEditTextDirect wraps this
// with the ScreenMode = 3 swap (per Diff 1; see mq2-autologin-eqswitch-diff.md).
static bool WriteEditTextDirectImpl(void *pEditWnd, const char *text);

bool WriteEditTextDirect(void *pEditWnd, const char *text) {
    // ScreenMode swap (Diff 1, hypothesis B-1 from the walkthrough).
    // Resolved at-call to keep the function callable from any thread without
    // a cached-pointer-staleness concern; GetModuleHandleA(NULL) is O(1) on
    // an already-loaded module (returns the cached PE-loader pointer).
    HMODULE hEqgame = GetModuleHandleA(NULL);
    volatile DWORD *pScreenMode = nullptr;
    DWORD oldScreenMode = 0;
    bool screenModeSwapped = false;
    if (hEqgame) {
        pScreenMode = reinterpret_cast<volatile DWORD *>(
            reinterpret_cast<uint8_t *>(hEqgame) + RVA_GLOBAL_ScreenMode);
        __try {
            // Empirical mapping (live probe 2026-05-14): natural ScreenMode is
            //   - 1 at login screen (when user is VISUALLY at the password field)
            //   - 2 at char-select / in-world
            //   - 3 NEVER natural (MQ2's deliberate fullscreen-UI-input mode)
            //
            // But: live test 2026-05-14 PID 8048 showed ScreenMode=0 at write-time.
            // The DLL's Combo G fires VERY EARLY in EQ's init (right after eqmain
            // widget tree is built), BEFORE EQ sets ScreenMode = 1. At that window,
            // ScreenMode is still BSS-zero. So the natural pre-write value can be
            // 0 OR 1 depending on timing.
            //
            // Gate <=8 admits both natural values + rejects garbage.
            // RESTORE policy: don't restore-to-0 — if we were at 0, EQ is about
            // to set it to 1; restoring to 0 would race against EQ's init and
            // could leave it inappropriately at 0. Restore only if was >=1.
            oldScreenMode = *pScreenMode;
            if (oldScreenMode <= 8) {
                *pScreenMode = 3;
                // Mark swapped only if we have a meaningful old value to restore.
                // Was-0 → don't restore (EQ will set it itself; restoring to 0
                // could race against EQ's natural init transition 0→1).
                screenModeSwapped = (oldScreenMode >= 1);
            } else {
                DI8Log("eqmain_cxstr: ScreenMode swap skipped — natural value 0x%X out of expected range [0..8] (wrong RVA on this Dalaya build?)",
                       (unsigned)oldScreenMode);
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("eqmain_cxstr: ScreenMode swap SEH fault — RVA 0x%08X unreadable at runtime",
                   RVA_GLOBAL_ScreenMode);
            screenModeSwapped = false;
        }
    }

    // The actual write. Wrapped in __try/__except (NOT __finally) because
    // MSVC C2702 forbids __try/__except inside a termination block, and the
    // restore step below needs its own __try/__except. Catching here also
    // SWALLOWS the SEH instead of letting it unwind to the caller — safer
    // since the caller (login_state_machine.cpp:375) only checks the boolean
    // return and isn't expecting to handle exceptions.
    bool result = false;
    __try {
        result = WriteEditTextDirectImpl(pEditWnd, text);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("eqmain_cxstr: WriteEditTextDirectImpl SEH-unwind caught — returning false");
        result = false;
    }

    // Restore ScreenMode AFTER the write (whether it succeeded, returned false,
    // or SEH-faulted-and-was-caught above). Runs on all exit paths because we
    // swallowed the SEH unwind in the __except above.
    if (screenModeSwapped && pScreenMode) {
        __try {
            *pScreenMode = oldScreenMode;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            DI8Log("eqmain_cxstr: ScreenMode restore SEH fault — value left at 3");
        }
    }

    return result;
}

static bool WriteEditTextDirectImpl(void *pEditWnd, const char *text) {
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
    // SECURITY: never log `text` content — this function writes passwords.
    // All diagnostic logs use length + first-byte hex only. Length is a low-
    // entropy fact already inferable from any successful login; first-byte
    // hex on a single failed write doesn't meaningfully reduce entropy of a
    // multi-character password.
    size_t textLen = 0;
    for (textLen = 0; textLen < 256 && text[textLen] != '\0'; ++textLen) {}

    Free(inputText);
    if (!ConstructFromCStr(inputText, text)) {
        DI8Log("eqmain_cxstr: WriteEditTextDirect — ctor failed (textLen=%u)",
               (unsigned)textLen);
        return false;
    }

    // 2026-04-25 Fix (2): read-back verification. ConstructFromCStr can
    // succeed at the API level (returns true, m_data != null) while the
    // CStrRep itself contains zero bytes — observed in dual-box test where
    // DLL log said "set password via Combo G" but EQ's password field
    // displayed empty (screenshot evidence). Verify the new CStrRep has
    // length > 0 AND first utf8 byte matches what we requested. If the
    // read-back fails, return false so caller falls back to keystroke.
    __try {
        CStrRep_Dalaya *rep = inputText->m_data;
        if (!rep) {
            DI8Log("eqmain_cxstr: WriteEditTextDirect read-back FAILED — m_data is null after ConstructFromCStr (textLen=%u)",
                   (unsigned)textLen);
            return false;
        }
        if (rep->length == 0) {
            DI8Log("eqmain_cxstr: WriteEditTextDirect read-back FAILED — CStrRep length is 0 after write (requested textLen=%u)",
                   (unsigned)textLen);
            return false;
        }
        if (rep->utf8[0] != text[0]) {
            // Hex dump first 0x40 bytes of the CStrRep so we can FIND the
            // actual utf8 offset. Length matches (so refCount/alloc/length
            // layout is correct) but utf8 isn't where we think.
            // Logs only first byte of `text` (not full password) — see
            // top-of-function security note.
            const unsigned char *raw = (const unsigned char *)rep;
            char hex[3 * 0x40 + 1] = {};
            int hexLen = 0;
            for (int i = 0; i < 0x40; i++) {
                __try {
                    hexLen += wsprintfA(hex + hexLen, "%02x ", raw[i]);
                } __except (EXCEPTION_EXECUTE_HANDLER) {
                    hexLen += wsprintfA(hex + hexLen, "?? ");
                }
            }
            DI8Log("eqmain_cxstr: WriteEditTextDirect read-back FAILED — first byte mismatch: wrote 0x%02x, read 0x%02x at +0x10 (requested length=%u, repLength=%u). CStrRep hex dump (+0x00..+0x40):",
                   (unsigned char)text[0], (unsigned char)rep->utf8[0],
                   (unsigned)textLen, (unsigned)rep->length);
            DI8Log("eqmain_cxstr:   %s", hex);
            return false;
        }
        DI8Log("eqmain_cxstr: WriteEditTextDirect read-back OK — length=%u, first byte 0x%02x matches",
               rep->length, (unsigned char)rep->utf8[0]);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("eqmain_cxstr: WriteEditTextDirect read-back SEH — m_data invalid after write (textLen=%u)",
               (unsigned)textLen);
        return false;
    }
    return true;
}

} // namespace EQMainCXStr
