// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// Native/eqmain_widgets.cpp -- structural live-widget lookup for Combo G
//
// See eqmain_widgets.h for the algorithm overview, threat model, and
// dormant-code gate. This implementation files all heap scans through
// SEH-wrapped reads and bounds every loop with a page count cap.
//
// All RVAs and offsets sourced from iter 8/9/10 smokes — see
// memory/reference_eqswitch_dalaya_widget_def_link.md for the offset
// table and X:/_Projects/_.claude/_comms/handoff-eqswitch-combo-g-
// iterations1-9-20260424.md for the recon trail.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include "eqmain_widgets.h"
#include "eqmain_offsets.h"

void DI8Log(const char *fmt, ...);

namespace EQMainWidgets {

// Cached encoded XMLIndex value. 0 means unresolved. Single writer
// (ResolvePasswordXMLIndex) under the same de-facto serialization as the
// rest of the early-init path. Readers see 0 or a coherent value.
static volatile uint32_t g_cachedXMLIndex = 0;

// Iter 14.3: cached widget pointer. Validated on each FindLive call by
// re-reading the vtable + XMLIndex; if both still match, return cached
// without rescanning. Invalidated by ResetPasswordCache (eqmain unload).
static volatile uintptr_t g_cachedWidgetPtr = 0;

// ─── Memory-safety helpers ──────────────────────────────────
static bool SafeReadDword(uintptr_t addr, uint32_t *out) {
    __try {
        *out = *reinterpret_cast<const volatile uint32_t *>(addr);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool IsCommittedReadable(const MEMORY_BASIC_INFORMATION &mbi) {
    if (mbi.State != MEM_COMMIT) return false;
    if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;
    if (!(mbi.Protect & (PAGE_READONLY | PAGE_READWRITE |
                         PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE))) return false;
    if (mbi.Type != MEM_PRIVATE && mbi.Type != MEM_MAPPED) return false;
    return true;
}

// ─── Phase 1: find LOGIN_PasswordEdit CXStr buf ─────────────
// Walks committed user-space pages looking for the literal string
// "LOGIN_PasswordEdit" at a 4-byte aligned position whose -0x0C dword
// matches the string length (the CXStr length field at buf+0x08 sits at
// data-0x14+0x08 == data-0x0C). Returns the first STRICT match's buf
// address, 0 if none.
//
// Iter 10c proved this finds 1-2 STRICT instances per process load.
static uintptr_t FindCXStrBuf(const char *target, int targetLen) {
    MEMORY_BASIC_INFORMATION mbi;
    uintptr_t addr = 0x00010000;
    int pages = 0;
    while (addr < 0x7FFF0000 && pages < 5000) {
        if (VirtualQuery(reinterpret_cast<void *>(addr), &mbi, sizeof(mbi)) == 0) break;
        uintptr_t b = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        SIZE_T sz = mbi.RegionSize;
        if (IsCommittedReadable(mbi)) {
            pages++;
            __try {
                const uint8_t *p = reinterpret_cast<const uint8_t *>(b);
                for (uintptr_t off = 0x14; off + targetLen + 1 <= sz; off += 4) {
                    if (p[off] != target[0]) continue;
                    bool eq = true;
                    for (int k = 1; k < targetLen; k++) {
                        if (p[off + k] != target[k]) { eq = false; break; }
                    }
                    if (!eq || p[off + targetLen] != 0) continue;
                    int maybeLen = *reinterpret_cast<const int *>(p + off - 0x0C);
                    if (maybeLen != targetLen) continue;
                    uintptr_t bufAddr = reinterpret_cast<uintptr_t>(p + off - 0x14);
                    DI8Log("eqmain_widgets: FindCXStrBuf — STRICT match buf=0x%08X data=0x%08X len=%d (%d pages scanned)",
                           (unsigned)bufAddr, (unsigned)reinterpret_cast<uintptr_t>(p + off),
                           targetLen, pages);
                    return bufAddr;
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        }
        addr = b + sz;
        if (addr <= b) addr = b + 0x1000;
    }
    DI8Log("eqmain_widgets: FindCXStrBuf — NO STRICT match in %d pages", pages);
    return 0;
}

// ─── Phase 2: cross-ref scan + nItemIdx extraction ──────────
// Walks committed pages for any DWORD == targetBufAddr. Each hit is a
// pointer to the CXStr buf, located in some struct's field. Logs up to
// 12 hits with surrounding context. For each, scans surrounding ±0x20
// bytes for a small uint32 candidate nItemIdx (< 0x1000, non-zero), and
// also checks for a "vtable-like" DWORD (high address pointing into
// eqmain's range) which would suggest CXMLDataPtr wrapper rather than
// XMLData entry.
//
// Returns the nItemIdx of the first hit that:
//   - has a small int candidate within ±0x20
//   - does NOT have an eqmain-vtable DWORD at hit-0x10 or hit-0x14
//     (filters out CXMLDataPtr wrappers — they have vtable at struct base)
// Returns -1 if no qualifying hit.
static int FindCrossRefAndExtractItemIdx(uintptr_t targetBufAddr) {
    if (!targetBufAddr) return -1;

    uintptr_t eqmBase = 0;
    uint32_t  eqmSize = 0;
    EQMainOffsets::GetRange(&eqmBase, &eqmSize);
    uintptr_t eqmHi = eqmBase ? eqmBase + eqmSize : 0;

    int totalHits = 0;
    int qualifiedHits = 0;
    int firstQualifiedIdx = -1;
    uintptr_t firstQualifiedHit = 0;

    MEMORY_BASIC_INFORMATION mbi;
    uintptr_t addr = 0x00010000;
    int pages = 0;
    while (addr < 0x7FFF0000 && pages < 5000) {
        if (VirtualQuery(reinterpret_cast<void *>(addr), &mbi, sizeof(mbi)) == 0) break;
        uintptr_t b = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        SIZE_T sz = mbi.RegionSize;
        if (IsCommittedReadable(mbi)) {
            pages++;
            __try {
                const uint32_t *q = reinterpret_cast<const uint32_t *>(b);
                size_t qLen = sz / 4;
                for (size_t i = 0; i < qLen; i++) {
                    if (q[i] != targetBufAddr) continue;
                    uintptr_t hitAddr = reinterpret_cast<uintptr_t>(q + i);
                    totalHits++;

                    // Skip hits in module .data / .text / .rdata / stack
                    // ranges — only heap-allocated XMLData entries are
                    // valid candidates. Iter 12 first smoke proved a hit at
                    // 0x0042EAF0 (EQSwitch.exe .data) had a false-positive
                    // nItemIdxCand=35 from coincidentally-adjacent bytes.
                    // Heap on Win32 user-mode starts at ~0x10000000.
                    if (hitAddr < 0x10000000) {
                        DI8Log("eqmain_widgets: cross-ref hit @ 0x%08X SKIPPED (below heap range, %d total)",
                               (unsigned)hitAddr, totalHits);
                        continue;
                    }

                    // Wrapper detection: if hit-0x10 or hit-0x14 is an eqmain
                    // vtable (within eqmain code range), the containing struct
                    // is likely CXMLDataPtr (vtable+CXStr at +0x18).
                    bool wrapperLike = false;
                    if (eqmBase) {
                        static const int wrapperBackOffsets[] = { 0x10, 0x14, 0x18 };
                        for (int wbi = 0; wbi < 3; wbi++) {
                            uintptr_t baseCand = hitAddr - wrapperBackOffsets[wbi];
                            if (baseCand < b || baseCand + 4 > b + sz) continue;
                            uint32_t v;
                            if (!SafeReadDword(baseCand, &v)) continue;
                            if (v >= eqmBase && v < eqmHi) {
                                wrapperLike = true;
                                break;
                            }
                        }
                    }

                    // Scan ±0x20 for a small uint32 (potential nItemIdx).
                    int candidateIdx = -1;
                    for (int o = -0x20; o <= 0x20; o += 4) {
                        if (o == 0) continue;
                        uintptr_t scanAddr = hitAddr + o;
                        if (scanAddr < b || scanAddr + 4 > b + sz) continue;
                        uint32_t v;
                        if (!SafeReadDword(scanAddr, &v)) continue;
                        if (v < 0x1000 && v != 0 && v != (uint32_t)targetBufAddr) {
                            candidateIdx = static_cast<int>(v);
                            break;
                        }
                    }

                    DI8Log("eqmain_widgets: cross-ref hit @ 0x%08X (wrapperLike=%d nItemIdxCand=%d, %d total)",
                           (unsigned)hitAddr, wrapperLike ? 1 : 0, candidateIdx, totalHits);

                    if (!wrapperLike && candidateIdx >= 0) {
                        qualifiedHits++;
                        if (firstQualifiedIdx < 0) {
                            firstQualifiedIdx = candidateIdx;
                            firstQualifiedHit = hitAddr;
                        }
                    }

                    if (totalHits >= 12) break;
                }
                if (totalHits >= 12) break;
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        }
        addr = b + sz;
        if (addr <= b) addr = b + 0x1000;
    }

    DI8Log("eqmain_widgets: cross-ref scan — %d total hits, %d qualified (non-wrapper + nItemIdx), %d pages",
           totalHits, qualifiedHits, pages);
    if (firstQualifiedIdx >= 0) {
        DI8Log("eqmain_widgets: cross-ref scan — selected first qualified hit @ 0x%08X nItemIdx=%d",
               (unsigned)firstQualifiedHit, firstQualifiedIdx);
    }
    return firstQualifiedIdx;
}

// ─── Public API ─────────────────────────────────────────────
bool ResolvePasswordXMLIndex() {
    if (g_cachedXMLIndex != 0) return true;

    // Iter 12.2 diagnostic dump (smoke 2026-04-25 07:40) produced the
    // decisive empirical evidence: live CEditWnds on the connect screen
    // have classIdx=34 with sequential itemIdx values starting at 0 for
    // LOGIN_UsernameEdit (proven by reading 'gotquiz' at +0x1A8). The
    // .sidl/EQUI_LoginScreen.xml allocates widgets in declaration order:
    //
    //   idx 0 = LOGIN_UsernameEdit  (username, ini-prefilled with 'gotquiz')
    //   idx 1 = LOGIN_PasswordEdit  (target — the field we want)
    //   idx 2-6 = AccountKey + char-create-screen edits
    //
    // The previous cross-ref scan found 'idx=32' as a convergent value
    // among 4-5 CParamEditbox / CXMLDataPtr / lookup-table holders of
    // the LOGIN_PasswordEdit CXStr buf. That value is real but not
    // nItemIdx — it's a class-level constant (max-chars or similar)
    // that happens to be 32 across many EditBox defs. Live widgets at
    // XMLIndex=(34<<16)|32 don't exist; the cross-ref path was finding
    // metadata, not widget keys.
    //
    // Hardcoded to idx=1 for the .sidl-stable Dalaya UI (frozen 2013).
    // FindLivePasswordCEditWnd applies a defensive empty-check at write
    // time: if the resolved widget already has non-empty InputText, we
    // refuse to write (protects against .sidl reorder on hypothetical
    // future Dalaya patch — keystroke fallback takes over).
    static constexpr uint32_t PASSWORD_ITEM_IDX = 1;
    uint32_t encoded = (CLASSIDX_EDITBOX << 16) | PASSWORD_ITEM_IDX;
    InterlockedExchange(reinterpret_cast<volatile LONG *>(&g_cachedXMLIndex),
                        (LONG)encoded);
    DI8Log("eqmain_widgets: ResolvePasswordXMLIndex — RESOLVED XMLIndex=0x%08X "
           "(hardcoded — classIdx=%u itemIdx=%u, .sidl-stable for Dalaya 2013)",
           encoded, CLASSIDX_EDITBOX, PASSWORD_ITEM_IDX);
    return true;
}

// Legacy cross-ref-based resolver. Kept as silence-warned dead code in
// case a future Dalaya patch changes the .sidl ordering and we need to
// re-derive idx dynamically. Currently unused — ResolvePasswordXMLIndex
// uses the hardcoded path.
[[maybe_unused]] static bool ResolvePasswordXMLIndex_CrossRef() {
    static const char target[] = "LOGIN_PasswordEdit";
    static const int  targetLen = sizeof(target) - 1;
    DI8Log("eqmain_widgets: legacy cross-ref resolver — starting");
    uintptr_t cxstrBuf = FindCXStrBuf(target, targetLen);
    if (!cxstrBuf) return false;
    int itemIdx = FindCrossRefAndExtractItemIdx(cxstrBuf);
    if (itemIdx < 0) return false;
    uint32_t encoded = (CLASSIDX_EDITBOX << 16) | (uint32_t)itemIdx;
    InterlockedExchange(reinterpret_cast<volatile LONG *>(&g_cachedXMLIndex),
                        (LONG)encoded);
    return true;
}

uint32_t GetCachedPasswordXMLIndex() {
    return g_cachedXMLIndex;
}

void ResetPasswordCache() {
    InterlockedExchange(reinterpret_cast<volatile LONG *>(&g_cachedXMLIndex), 0);
    InterlockedExchangePointer((PVOID volatile *)&g_cachedWidgetPtr, nullptr);
}

void *FindLivePasswordCEditWnd() {
    // Lazy resolve on first call — at module-load time the .sidl-derived
    // CXStr buffers don't exist yet, so resolution must wait until the
    // login UI has been fully loaded. ResolvePasswordXMLIndex is idempotent
    // and returns true immediately if the XMLIndex is already cached.
    uint32_t target = g_cachedXMLIndex;
    if (!target) {
        if (!ResolvePasswordXMLIndex()) {
            // ResolvePasswordXMLIndex logged its own failure
            return nullptr;
        }
        target = g_cachedXMLIndex;
        if (!target) return nullptr;
    }

    uintptr_t eqmBase = 0;
    uint32_t  eqmSize = 0;
    EQMainOffsets::GetRange(&eqmBase, &eqmSize);
    if (!eqmBase || !eqmSize) {
        DI8Log("eqmain_widgets: FindLivePasswordCEditWnd — FAIL (eqmain not loaded)");
        return nullptr;
    }

    uintptr_t vtCEditWnd     = eqmBase + EQMainOffsets::RVA_VTABLE_CEditWnd;
    uintptr_t vtCEditBaseWnd = eqmBase + EQMainOffsets::RVA_VTABLE_CEditBaseWnd;

    // Iter 14.3: fast path — validate cached widget pointer. Re-reads vtable
    // and XMLIndex; if both still match, return immediately (~10us instead
    // of ~750ms heap scan). The widget can move/free across charselect-
    // enter-world transitions, so this re-validation is required on each
    // call. ResetPasswordCache (called from OnEQMainUnloaded) clears the
    // cache when the screen tears down.
    uintptr_t cached = g_cachedWidgetPtr;
    if (cached) {
        __try {
            uintptr_t vt = *(const uintptr_t *)cached;
            if (vt == vtCEditWnd || vt == vtCEditBaseWnd) {
                uint32_t xmlIdx = *(const uint32_t *)(cached + OFFSET_XMLINDEX);
                if (xmlIdx == target) {
                    return (void *)cached;
                }
            }
            // Cache stale — fall through to rescan
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            // Cached pointer faulted — widget freed. Fall through to rescan.
        }
        // Invalidate stale cache before rescan
        InterlockedExchangePointer((PVOID volatile *)&g_cachedWidgetPtr, nullptr);
    }

    int candidatesScanned = 0;
    void *result = nullptr;

    // Iter 12.2 diagnostic: log first ~12 live CEditWnd XMLIndex values
    // so we can see whether the cached target XMLIndex actually appears
    // on any live widget, and identify the right one if not.
    static bool s_dumpedThisLoad = false;
    bool dumpThisCall = !s_dumpedThisLoad;
    if (dumpThisCall) {
        s_dumpedThisLoad = true;
        DI8Log("eqmain_widgets: FindLive diagnostic — target=0x%08X; dumping first 12 candidates (vt, addr, XMLIndex, InputText):",
               target);
    }
    int dumped = 0;

    MEMORY_BASIC_INFORMATION mbi;
    uintptr_t addr = 0x00010000;
    int pages = 0;
    while (addr < 0x7FFF0000 && pages < 10000 && !result) {
        if (VirtualQuery(reinterpret_cast<void *>(addr), &mbi, sizeof(mbi)) == 0) break;
        uintptr_t b = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        SIZE_T sz = mbi.RegionSize;
        if (IsCommittedReadable(mbi)) {
            pages++;
            __try {
                const uint8_t *p = reinterpret_cast<const uint8_t *>(b);
                for (uintptr_t off = 0; off + (OFFSET_XMLINDEX + 4) <= sz; off += 4) {
                    uintptr_t vt = *reinterpret_cast<const uintptr_t *>(p + off);
                    if (vt != vtCEditWnd && vt != vtCEditBaseWnd) continue;
                    candidatesScanned++;
                    uint32_t xmlIdx = *reinterpret_cast<const uint32_t *>(p + off + OFFSET_XMLINDEX);

                    // Iter 12.2 diagnostic dump (one-shot per eqmain load)
                    if (dumpThisCall && dumped < 12) {
                        // Read InputText CXStr first 32 chars if present
                        char preview[40] = {};
                        __try {
                            uintptr_t cxstrBuf = *reinterpret_cast<const uintptr_t *>(p + off + 0x1A8);
                            if (cxstrBuf >= 0x10000 && cxstrBuf <= 0x7FFFFFFF) {
                                int len = *reinterpret_cast<const int *>(cxstrBuf + 8);
                                if (len > 0 && len < 33) {
                                    memcpy(preview, reinterpret_cast<const void *>(cxstrBuf + 0x14),
                                           (size_t)len);
                                    preview[len] = 0;
                                } else if (len == 0) {
                                    strcpy(preview, "(empty)");
                                }
                            }
                        } __except (EXCEPTION_EXECUTE_HANDLER) {
                            strcpy(preview, "(SEH)");
                        }
                        DI8Log("eqmain_widgets:   cand[%d] @ 0x%08X vt=0x%08X XMLIndex=0x%08X InputText='%s'",
                               dumped, (unsigned)(b + off), (unsigned)vt, xmlIdx, preview);
                        dumped++;
                    }

                    if (xmlIdx == target) {
                        result = reinterpret_cast<void *>(b + off);
                        // Cache for fast re-lookup on subsequent calls
                        InterlockedExchangePointer((PVOID volatile *)&g_cachedWidgetPtr, result);
                        DI8Log("eqmain_widgets: FindLivePasswordCEditWnd — MATCH @ %p vt=0x%08X XMLIndex=0x%08X",
                               result, (unsigned)vt, xmlIdx);
                        break;
                    }
                }
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        }
        addr = b + sz;
        if (addr <= b) addr = b + 0x1000;
    }

    if (!result) {
        DI8Log("eqmain_widgets: FindLivePasswordCEditWnd — NO MATCH (target XMLIndex=0x%08X, "
               "%d CEditWnd candidates scanned, %d pages)",
               target, candidatesScanned, pages);
    }
    return result;
}

} // namespace EQMainWidgets
