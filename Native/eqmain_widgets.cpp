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

    static const char target[] = "LOGIN_PasswordEdit";
    static const int  targetLen = sizeof(target) - 1;

    DI8Log("eqmain_widgets: ResolvePasswordXMLIndex — starting (target='%s')", target);

    uintptr_t cxstrBuf = FindCXStrBuf(target, targetLen);
    if (!cxstrBuf) {
        DI8Log("eqmain_widgets: ResolvePasswordXMLIndex — FAIL (CXStr buf not found in heap)");
        return false;
    }

    int itemIdx = FindCrossRefAndExtractItemIdx(cxstrBuf);
    if (itemIdx < 0) {
        DI8Log("eqmain_widgets: ResolvePasswordXMLIndex — FAIL (no qualified cross-ref hit had nItemIdx; "
               "buf=0x%08X)", (unsigned)cxstrBuf);
        return false;
    }

    uint32_t encoded = (CLASSIDX_EDITBOX << 16) | (uint32_t)itemIdx;
    InterlockedExchange(reinterpret_cast<volatile LONG *>(&g_cachedXMLIndex),
                        (LONG)encoded);
    DI8Log("eqmain_widgets: ResolvePasswordXMLIndex — RESOLVED XMLIndex=0x%08X (classIdx=%u itemIdx=%d)",
           encoded, CLASSIDX_EDITBOX, itemIdx);
    return true;
}

uint32_t GetCachedPasswordXMLIndex() {
    return g_cachedXMLIndex;
}

void ResetPasswordCache() {
    InterlockedExchange(reinterpret_cast<volatile LONG *>(&g_cachedXMLIndex), 0);
}

void *FindLivePasswordCEditWnd() {
    uint32_t target = g_cachedXMLIndex;
    if (!target) {
        DI8Log("eqmain_widgets: FindLivePasswordCEditWnd — FAIL (XMLIndex unresolved; "
               "call ResolvePasswordXMLIndex first)");
        return nullptr;
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

    int candidatesScanned = 0;
    void *result = nullptr;

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
                    if (xmlIdx == target) {
                        result = reinterpret_cast<void *>(b + off);
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
