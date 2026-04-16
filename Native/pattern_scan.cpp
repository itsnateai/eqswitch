// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// pattern_scan.cpp — Locates EQ's internal g_bActive flag via binary scanning.
//
// Strategy: EQ's WndProc handles WM_ACTIVATEAPP (0x1C) by storing wParam into
// a global DWORD (g_bActive). When this flag is 0, EQ's main loop skips
// GetDeviceData calls entirely, making background input impossible.
//
// We scan eqgame.exe's .text section for CMP instructions with 0x1C, then
// look nearby for MOV instructions that write to a global address. Candidates
// are validated (must be aligned, writable, value 0 or 1) and ranked by
// cross-reference count — the real g_bActive is referenced from many places.

#define _CRT_SECURE_NO_WARNINGS
#include "pattern_scan.h"
#include <windows.h>
#include <string.h>

void DI8Log(const char *fmt, ...);

namespace PatternScan {

// --- PE section parsing ---

struct Section {
    uint8_t *base;
    uint32_t size;
};

static bool GetSections(uint8_t *imageBase, Section *text, Section *data,
                        uint32_t *outImageSize)
{
    if (*(uint16_t *)imageBase != 0x5A4D) return false;
    int32_t eLfanew = *(int32_t *)(imageBase + 0x3C);
    if (eLfanew < 0x40 || eLfanew > 0x1000) return false;
    if (*(uint32_t *)(imageBase + eLfanew) != 0x00004550) return false;

    uint16_t numSections = *(uint16_t *)(imageBase + eLfanew + 6);
    uint16_t optSize = *(uint16_t *)(imageBase + eLfanew + 20);
    *outImageSize = *(uint32_t *)(imageBase + eLfanew + 24 + 56);

    uint8_t *sh = imageBase + eLfanew + 24 + optSize;
    text->base = nullptr; text->size = 0;
    data->base = nullptr; data->size = 0;

    for (int i = 0; i < numSections && i < 64; i++, sh += 40) {
        char name[9] = {0};
        memcpy(name, sh, 8);
        uint32_t vsize = *(uint32_t *)(sh + 8);
        uint32_t vaddr = *(uint32_t *)(sh + 12);

        if (strcmp(name, ".text") == 0) {
            text->base = imageBase + vaddr;
            text->size = vsize;
        } else if (strcmp(name, ".data") == 0) {
            data->base = imageBase + vaddr;
            data->size = vsize;
        }
    }
    return text->base && text->size;
}

// --- Address validation ---

static bool IsValidFlagAddr(uint32_t *addr, uint8_t *imageBase, uint32_t imageSize) {
    uintptr_t a = (uintptr_t)addr;
    uintptr_t b = (uintptr_t)imageBase;

    // Must be within eqgame.exe's mapped image
    if (a < b || a + 4 > b + imageSize) return false;

    // Must be DWORD-aligned
    if (a & 3) return false;

    // Must be in a writable page
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(addr, &mbi, sizeof(mbi))) return false;
    if (!(mbi.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE | PAGE_WRITECOPY)))
        return false;

    // Value must be 0 or 1 (boolean activation flag)
    uint32_t val = *(volatile uint32_t *)addr;
    return val <= 1;
}

// --- Instruction pattern matching ---

// Extract absolute address from a MOV-to-global instruction at `p`.
// Recognizes three x86 patterns:
//   A3 XX XX XX XX           — MOV [addr], EAX
//   89 [05,0D,15,1D,2D,35,3D] XX XX XX XX — MOV [addr], reg
//   C7 05 XX XX XX XX imm32  — MOV DWORD [addr], imm (imm must be 0 or 1)
static uint32_t *ExtractMovTarget(const uint8_t *p) {
    // MOV [addr], EAX (compact form)
    if (p[0] == 0xA3)
        return (uint32_t *)(uintptr_t)*(uint32_t *)(p + 1);

    // MOV [addr], reg — opcode 89, ModR/M mod=00 r/m=101 (disp32)
    if (p[0] == 0x89 && (p[1] & 0xC7) == 0x05)
        return (uint32_t *)(uintptr_t)*(uint32_t *)(p + 2);

    // MOV DWORD [addr], imm32 — only accept 0 or 1
    if (p[0] == 0xC7 && p[1] == 0x05) {
        uint32_t imm = *(uint32_t *)(p + 6);
        if (imm <= 1)
            return (uint32_t *)(uintptr_t)*(uint32_t *)(p + 2);
    }

    return nullptr;
}

// --- Main scanner ---

static uint32_t *g_cachedResult = nullptr;
static bool g_scanned = false;

uint32_t *FindActivationFlag() {
    if (g_scanned) return g_cachedResult;
    g_scanned = true;

    uint8_t *imageBase = (uint8_t *)GetModuleHandleW(nullptr);
    if (!imageBase) {
        DI8Log("pattern_scan: GetModuleHandle(nullptr) failed");
        return nullptr;
    }

    Section text, data;
    uint32_t imageSize = 0;
    if (!GetSections(imageBase, &text, &data, &imageSize)) {
        DI8Log("pattern_scan: PE section parsing failed");
        return nullptr;
    }

    DI8Log("pattern_scan: image=0x%08X (%u KB), .text=0x%08X+%u KB, .data=0x%08X+%u KB",
           (unsigned)(uintptr_t)imageBase, imageSize / 1024,
           (unsigned)(uintptr_t)text.base, text.size / 1024,
           data.base ? (unsigned)(uintptr_t)data.base : 0, data.size / 1024);

    // Scan .text for CMP *, 0x1C (WM_ACTIVATEAPP = 28 = 0x1C)
    struct Candidate {
        uint32_t *addr;
        uintptr_t cmpOfs;  // offset of CMP instruction from image base
        int movDist;        // distance from CMP to MOV
    };
    Candidate candidates[32];
    int numCandidates = 0;
    int cmpCount = 0;

    const int SEARCH_WINDOW = 96;
    uint8_t *scanEnd = text.base + text.size - SEARCH_WINDOW;

    for (uint8_t *p = text.base; p < scanEnd; p++) {
        bool isCmp = false;

        // 83 F8..FF 1C — CMP reg, imm8=0x1C
        if (p[0] == 0x83 && (p[1] & 0xF8) == 0xF8 && p[2] == 0x1C)
            isCmp = true;
        // 3D 1C000000 — CMP EAX, imm32=0x1C
        else if (p[0] == 0x3D && *(uint32_t *)(p + 1) == 0x1C)
            isCmp = true;
        // 81 F8..FF 1C000000 — CMP reg, imm32=0x1C
        else if (p[0] == 0x81 && (p[1] & 0xF8) == 0xF8 && *(uint32_t *)(p + 2) == 0x1C)
            isCmp = true;

        if (!isCmp) continue;
        cmpCount++;

        // Scan forward for MOV [global_addr], reg/imm
        for (uint8_t *q = p + 2; q < p + SEARCH_WINDOW && numCandidates < 32; q++) {
            uint32_t *target = ExtractMovTarget(q);
            if (!target) continue;
            if (!IsValidFlagAddr(target, imageBase, imageSize)) continue;

            // Deduplicate
            bool dup = false;
            for (int i = 0; i < numCandidates; i++) {
                if (candidates[i].addr == target) { dup = true; break; }
            }
            if (dup) continue;

            candidates[numCandidates].addr = target;
            candidates[numCandidates].cmpOfs = (uintptr_t)(p - imageBase);
            candidates[numCandidates].movDist = (int)(q - p);
            numCandidates++;

            DI8Log("pattern_scan: candidate #%d at 0x%08X (CMP at +0x%X, MOV +%d bytes, val=%u)",
                   numCandidates, (unsigned)(uintptr_t)target,
                   (unsigned)(p - imageBase), (int)(q - p),
                   *(volatile uint32_t *)target);
        }
    }

    DI8Log("pattern_scan: %d CMP-0x1C instructions, %d unique candidate(s)", cmpCount, numCandidates);

    if (numCandidates == 0) {
        DI8Log("pattern_scan: FAILED — no g_bActive candidates found");
        return nullptr;
    }

    if (numCandidates == 1) {
        g_cachedResult = candidates[0].addr;
        DI8Log("pattern_scan: single candidate — using 0x%08X",
               (unsigned)(uintptr_t)g_cachedResult);
        return g_cachedResult;
    }

    // Multiple candidates — rank by cross-reference count in .text.
    // The real g_bActive is read from many places (main loop, WndProc,
    // renderer, sound, etc.), so it will have the most xrefs.
    int bestIdx = 0;
    int bestXrefs = -1;

    for (int i = 0; i < numCandidates; i++) {
        uint32_t needle = (uint32_t)(uintptr_t)candidates[i].addr;
        int xrefs = 0;

        // Count 4-byte occurrences of the address in .text
        // (instruction operands referencing this global)
        for (uint8_t *p = text.base; p <= scanEnd; p++) {
            if (*(uint32_t *)p == needle) xrefs++;
        }

        DI8Log("pattern_scan: candidate #%d 0x%08X — %d xrefs, MOV dist=%d",
               i + 1, (unsigned)(uintptr_t)candidates[i].addr,
               xrefs, candidates[i].movDist);

        if (xrefs > bestXrefs || (xrefs == bestXrefs && candidates[i].movDist < candidates[bestIdx].movDist)) {
            bestXrefs = xrefs;
            bestIdx = i;
        }
    }

    g_cachedResult = candidates[bestIdx].addr;
    DI8Log("pattern_scan: selected 0x%08X (%d xrefs, MOV dist=%d)",
           (unsigned)(uintptr_t)g_cachedResult, bestXrefs, candidates[bestIdx].movDist);

    return g_cachedResult;
}

} // namespace PatternScan
