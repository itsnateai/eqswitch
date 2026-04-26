// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// Native/eqmain_widgets_mq2style.cpp -- structural CXWnd traversal
//
// See eqmain_widgets_mq2style.h for the field-offset derivation + fail-mode
// contract. This translation unit is the implementation; everything that
// touches widget memory is SEH-wrapped to honor the never-crash rule.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>

#include "eqmain_widgets_mq2style.h"
#include "eqmain_offsets.h"
#include "mq2_bridge.h"

void DI8Log(const char *fmt, ...);

namespace EQMainWidgetsMQ2 {

// ─── Internal limits ─────────────────────────────────────────
static constexpr int      MAX_RECURSION_DEPTH = 32;    // Dalaya UI ~6 deep observed
static constexpr int      MAX_SIBLING_ITER    = 1024;  // cycle/runaway bound
static constexpr int      MAX_NAME_LEN        = 64;    // names like LOGIN_PasswordEdit fit
static constexpr int      MAX_SCREEN_SCAN     = 256;   // top-level screens is <30
static constexpr uint32_t SCAN_BYTES_WIDGET   = 0x200; // child widget body size for name lookup
static constexpr uint32_t SCAN_BYTES_SCREEN   = 0x400; // top-level screens have larger layout
static constexpr int      MIN_CHILDREN_SCREEN = 3;     // a screen with fewer than 3 children is unlikely "connect"

// ─── SEH-wrapped field reads ─────────────────────────────────
// Every field read goes through these so a stale pointer never crashes the
// host process — return value 0 / nullptr means either the field is null
// OR the read faulted; both treated as "absent" by callers.

static uintptr_t SafeRead4(const void *p, uint32_t off) {
    if (!p) return 0;
    __try {
        return *reinterpret_cast<const uintptr_t*>(
            reinterpret_cast<const uint8_t*>(p) + off);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

static uint8_t SafeRead1(const void *p, uint32_t off) {
    if (!p) return 0;
    __try {
        return *(reinterpret_cast<const uint8_t*>(p) + off);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

static void *NextSibling(const void *pWnd) {
    return reinterpret_cast<void*>(SafeRead4(pWnd, OFFSET_CXWND_NEXT_SIBLING));
}

static void *FirstChild(const void *pWnd) {
    return reinterpret_cast<void*>(SafeRead4(pWnd, OFFSET_CXWND_FIRST_CHILD));
}

// ─── Visibility (dShow) ──────────────────────────────────────
bool IsCXWndVisible(const void *pWnd) {
    if (!pWnd) return false;
    return SafeRead1(pWnd, OFFSET_CXWND_DSHOW) != 0;
}

// Helper: matches MQ2's `IsVisible() && !IsMinimized()` (used by
// ShouldProcessChildrenFrames). Useful for the FindLiveScreenByName filter.
static bool IsCXWndShownAndNotMinimized(const void *pWnd) {
    if (!pWnd) return false;
    return SafeRead1(pWnd, OFFSET_CXWND_DSHOW)     != 0
        && SafeRead1(pWnd, OFFSET_CXWND_MINIMIZED) == 0;
}

// ─── Sibling iteration ───────────────────────────────────────
void WalkSiblings(void *pWnd, MQ2VisitCallback cb, void *ctx) {
    if (!pWnd || !cb) return;
    void *visited[64];     // bounded cycle-detection set; 64 siblings max-real
    int   visitedCount = 0;
    void *cur = pWnd;
    for (int i = 0; i < MAX_SIBLING_ITER && cur; ++i) {
        // Cycle check (cheap — we only track first 64 to avoid alloc)
        for (int v = 0; v < visitedCount; ++v) {
            if (visited[v] == cur) {
                DI8Log("eqmain_widgets_mq2style: WalkSiblings cycle at 0x%p iter %d",
                       cur, i);
                return;
            }
        }
        if (visitedCount < 64) visited[visitedCount++] = cur;
        if (cb(cur, ctx)) return;  // callback halts
        cur = NextSibling(cur);
    }
}

// ─── Recursive subtree walk ──────────────────────────────────
static void WalkSubtreeImpl(void *pWnd, MQ2VisitCallback cb, void *ctx,
                            int depth, bool *halted) {
    if (!pWnd || depth >= MAX_RECURSION_DEPTH || *halted) return;
    if (cb(pWnd, ctx)) { *halted = true; return; }
    void *child = FirstChild(pWnd);
    int siblingIter = 0;
    while (child && siblingIter++ < MAX_SIBLING_ITER && !*halted) {
        WalkSubtreeImpl(child, cb, ctx, depth + 1, halted);
        if (*halted) return;
        child = NextSibling(child);
    }
}

// ─── Name resolution via heuristic per-widget CXStr scan ─────
// Each CXWnd has SOME CXStr field whose buffer holds the widget's SIDL name
// (LOGIN_PasswordEdit, connect, LOGIN_ConnectButton, etc.). Different widget
// classes may store it at different offsets — the field offset isn't stable.
// We use the proven trick from `dumps/find_parent_window.py:188`:
//
//   For each 4-byte-aligned DWORD in pWnd's first 0x200 (child) or 0x400
//   (top-level screen) bytes, treat it as a possible CStrRep_Dalaya base.
//   CStrRep layout (pinned in eqmain_cxstr.h:69-77):
//     +0x00 refCount  (sane: 1..0xFFFF)
//     +0x04 alloc     (capacity)
//     +0x08 length    (sane: 1..60 for SIDL names)
//     +0x10 ownerPtr
//     +0x14 utf8      (the actual string)
//   Validate ALL these constraints (not just length) before reading utf8 —
//   tighter validation kills false-positive name matches that the v1 had.
//
// Returns nullptr if no plausible name found.
//
// 2026-04-26 v2 changes per background-agent review:
//   - Removed thread_local s_nameBuf (replaced with stack/parameter buf)
//   - Tightened CStrRep validation (refCount range + 4-byte alignment)
//   - Caller controls scan window (0x200 for child widgets, 0x400 for screens)

static bool IsPlausibleWidgetName(const char *s, size_t maxLen) {
    if (!s) return false;
    size_t len = 0;
    while (len < maxLen) {
        unsigned char c = static_cast<unsigned char>(s[len]);
        if (c == 0) break;
        // Widget names are ASCII identifiers — letters, digits, _ . / -
        if (c >= 0x80) return false;
        if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
              (c >= '0' && c <= '9') || c == '_' || c == '.' ||
              c == '/' || c == '-' || c == ' ')) {
            return false;
        }
        ++len;
    }
    return len >= 2 && len < maxLen;
}

// CStrRep_Dalaya layout (per eqmain_cxstr.h:69-77):
//   refCount@0x00 / alloc@0x04 / length@0x08 / ownerPtr@0x10 / utf8@0x14
constexpr uint32_t CSTRREP_REFCOUNT = 0x00;
constexpr uint32_t CSTRREP_ALLOC    = 0x04;
constexpr uint32_t CSTRREP_LENGTH   = 0x08;
constexpr uint32_t CSTRREP_UTF8     = 0x14;

// Validate a candidate CStrRep pointer against the layout invariants. Tight
// validation here is the v2 fix for v1's false-positive name matches.
static bool TryReadCStrRepName(uintptr_t cand, char *outBuf, size_t outLen) {
    if (!outBuf || outLen < 4) return false;
    // Pointer-range + alignment. CStrRep is heap-allocated, 4-byte aligned.
    if (cand < 0x00010000 || cand >= 0x80000000) return false;
    if (cand & 0x3) return false;
    // refCount sane (heap objects rarely have refCount 0 or > a few thousand
    // for a SIDL-derived name; >= 1 and < 0x10000 is the v2 tightening).
    uint32_t rc = static_cast<uint32_t>(SafeRead4(
        reinterpret_cast<const void*>(cand), CSTRREP_REFCOUNT));
    if (rc == 0 || rc >= 0x10000) return false;
    // length plausible for a SIDL name (1..60).
    uint32_t lenField = static_cast<uint32_t>(SafeRead4(
        reinterpret_cast<const void*>(cand), CSTRREP_LENGTH));
    if (lenField == 0 || lenField > 60) return false;
    // alloc >= length (alloc is capacity)
    uint32_t allocField = static_cast<uint32_t>(SafeRead4(
        reinterpret_cast<const void*>(cand), CSTRREP_ALLOC));
    if (allocField < lenField) return false;
    // Read utf8 buffer
    const char *utf8 = reinterpret_cast<const char*>(cand + CSTRREP_UTF8);
    uint32_t copyLen = lenField < (outLen - 1) ? lenField : (outLen - 1);
    __try {
        for (uint32_t i = 0; i < copyLen; ++i) outBuf[i] = utf8[i];
        outBuf[copyLen] = 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    return IsPlausibleWidgetName(outBuf, outLen);
}

const char *GetCXWndXMLName(void *pWnd) {
    if (!pWnd) return nullptr;
    // First-plausible-name variant. Returned pointer is into a static
    // buffer; caller must copy if it needs longer lifetime than the next
    // GetCXWndXMLName call. (For CXWndHasXMLName we use a stack buffer.)
    static char s_nameBuf[64];
    for (uint32_t off = 0; off + 4 <= SCAN_BYTES_SCREEN; off += 4) {
        uintptr_t cand = SafeRead4(pWnd, off);
        if (TryReadCStrRepName(cand, s_nameBuf, sizeof(s_nameBuf))) {
            return s_nameBuf;
        }
    }
    return nullptr;
}

// Case-insensitive ASCII compare bounded at MAX_NAME_LEN. Names are SIDL
// identifiers (LOGIN_PasswordEdit, connect, etc.) — pure ASCII.
static bool CIEquals(const char *a, const char *b) {
    if (!a || !b) return false;
    for (int i = 0; i < MAX_NAME_LEN; ++i) {
        char ca = a[i], cb = b[i];
        if (ca >= 'A' && ca <= 'Z') ca = static_cast<char>(ca + 32);
        if (cb >= 'A' && cb <= 'Z') cb = static_cast<char>(cb + 32);
        if (ca != cb) return false;
        if (ca == 0)  return true;
    }
    return false;  // both >= MAX_NAME_LEN without null — refuse to match
}

// ─── RecurseAndFindName (the MQ2 port) ───────────────────────
struct FindNameCtx {
    const char *targetName;
    void       *result;
};

// Test whether ANY CXStr field within scanBytes of pWnd's body holds a
// string that case-insensitive-equals targetName. Iter-12 v2: tightened
// CStrRep validation via TryReadCStrRepName; configurable scan window so
// top-level screens (0x400) can have larger layouts than child widgets
// (0x200). All locals are stack-bound — no thread_local cross-call leakage.
static bool CXWndHasXMLName(void *pWnd, const char *targetName,
                            uint32_t scanBytes) {
    if (!pWnd || !targetName) return false;
    char tmpBuf[64];
    for (uint32_t off = 0; off + 4 <= scanBytes; off += 4) {
        uintptr_t cand = SafeRead4(pWnd, off);
        if (!TryReadCStrRepName(cand, tmpBuf, sizeof(tmpBuf))) continue;
        if (CIEquals(tmpBuf, targetName)) return true;
    }
    return false;
}

// Count direct children of pWnd (walk its child list via +0x10/+0x08).
// Bounded at MAX_SIBLING_ITER. Used by FindLiveScreenByName to filter
// leaf widgets out — the connect SCREEN has many children; a leaf button
// or label whose tooltip says "connect" has zero.
static int CountDirectChildren(void *pWnd) {
    if (!pWnd) return 0;
    void *child = FirstChild(pWnd);
    int count = 0;
    while (child && count < MAX_SIBLING_ITER) {
        ++count;
        child = NextSibling(child);
    }
    return count;
}

static bool FindNameVisitCb(void *pWnd, void *ctx) {
    FindNameCtx *fnc = static_cast<FindNameCtx*>(ctx);
    if (CXWndHasXMLName(pWnd, fnc->targetName, SCAN_BYTES_WIDGET)) {
        fnc->result = pWnd;
        return true;  // halt
    }
    return false;
}

void *RecurseAndFindName(void *pWnd, const char *name) {
    if (!pWnd || !name) return nullptr;
    FindNameCtx fnc{ name, nullptr };
    bool halted = false;
    WalkSubtreeImpl(pWnd, FindNameVisitCb, &fnc, 0, &halted);
    return fnc.result;
}

// ─── FindLiveScreenByName (iter-12 v2) ───────────────────────
// v1 over-filtered: `CSidlScreenWnd` vtable + `IsCXWndVisible` together
// rejected every screen (notVisible=0, noName=9, result=null). v2 uses
// the proven `find_parent_window.py:115` shape filter:
//   - widget vtable IS in eqmain range (loose — any eqmain class)
//   - widget has at least MIN_CHILDREN_SCREEN direct children (rejects
//     leaf widgets with tooltip="connect" false-positives)
//   - widget body contains a CXStr matching `name` within 0x400 bytes
struct FindScreenCtx {
    const char *targetName;
    void       *result;
    int         scanned;
    int         skippedNotEqmain;
    int         skippedTooFewChildren;
    int         skippedNoName;
};

static bool FindScreenIterCb(void *pWnd, void *ctx) {
    FindScreenCtx *fsc = static_cast<FindScreenCtx*>(ctx);
    if (fsc->scanned++ >= MAX_SCREEN_SCAN) return true;
    if (!EQMainOffsets::IsEQMainWidget(pWnd)) {
        fsc->skippedNotEqmain++;
        return false;
    }
    int childCount = CountDirectChildren(pWnd);
    if (childCount < MIN_CHILDREN_SCREEN) {
        fsc->skippedTooFewChildren++;
        return false;
    }
    if (CXWndHasXMLName(pWnd, fsc->targetName, SCAN_BYTES_SCREEN)) {
        fsc->result = pWnd;
        return true;  // halt
    }
    fsc->skippedNoName++;
    return false;
}

void *FindLiveScreenByName(const char *name) {
    if (!name) return nullptr;
    FindScreenCtx fsc{ name, nullptr, 0, 0, 0, 0 };
    bool ok = MQ2Bridge::IterateAllWindowsPublic(FindScreenIterCb, &fsc);
    DI8Log("eqmain_widgets_mq2style: FindLiveScreenByName('%s') — "
           "iterCompleted=%d scanned=%d notEqmain=%d tooFewChildren=%d noName=%d result=%p",
           name, ok ? 1 : 0, fsc.scanned, fsc.skippedNotEqmain,
           fsc.skippedTooFewChildren, fsc.skippedNoName, fsc.result);
    return fsc.result;
}

// ─── Diagnostic dump ─────────────────────────────────────────
// One-shot per eqmain load — emit every top-level eqmain widget's
// vtable RVA + child count + first plausible CXStr name found in its
// first 0x400 bytes. Used to debug heuristic misses by giving operators
// a complete picture of what's actually iterable at login phase.
struct DumpCtx { int dumped; };

static bool DumpVisitCb(void *pWnd, void *ctx) {
    DumpCtx *dc = static_cast<DumpCtx*>(ctx);
    if (dc->dumped++ >= MAX_SCREEN_SCAN) return true;
    if (!EQMainOffsets::IsEQMainWidget(pWnd)) return false;
    uintptr_t vt = SafeRead4(pWnd, 0);
    uintptr_t base = 0;
    uint32_t size = 0;
    EQMainOffsets::GetRange(&base, &size);
    uintptr_t vtRva = base ? (vt - base) : 0;
    int childCount = CountDirectChildren(pWnd);
    const char *name = GetCXWndXMLName(pWnd);
    DI8Log("eqmain_widgets_mq2style: dump top-level [%d] @ %p vtRva=0x%06X "
           "children=%d firstPlausibleName='%s'",
           dc->dumped - 1, pWnd, (unsigned)vtRva, childCount,
           name ? name : "<none>");
    return false;  // continue
}

void DumpTopLevelWidgetNamesOnce() {
    static volatile LONG s_dumped = 0;
    if (InterlockedCompareExchange(&s_dumped, 1, 0) != 0) return;  // already dumped
    DI8Log("eqmain_widgets_mq2style: ===== one-shot top-level widget dump (iter-12 v2 diagnostic) =====");
    DumpCtx dc{ 0 };
    MQ2Bridge::IterateAllWindowsPublic(DumpVisitCb, &dc);
    DI8Log("eqmain_widgets_mq2style: ===== dump complete (%d widgets) =====", dc.dumped);
}

// ─── FindChildByName (composition) ───────────────────────────
void *FindChildByName(const char *screenName, const char *childName) {
    if (!screenName || !childName) return nullptr;
    void *screen = FindLiveScreenByName(screenName);
    if (!screen) {
        DI8Log("eqmain_widgets_mq2style: FindChildByName('%s','%s') — "
               "screen not found",
               screenName, childName);
        return nullptr;
    }
    void *child = RecurseAndFindName(screen, childName);
    if (!child) {
        DI8Log("eqmain_widgets_mq2style: FindChildByName('%s','%s') — "
               "screen %p found but child name not found in subtree",
               screenName, childName, screen);
    }
    return child;
}

// ─── Diagnostics ─────────────────────────────────────────────
void LogStartupDiagnostics() {
    DI8Log("eqmain_widgets_mq2style: startup — pNext=+0x%X pFirstChild=+0x%X "
           "dShow=+0x%X minimized=+0x%X xmlIndex=+0x%X xmlDataName=+0x%X "
           "MQ2StyleEnabled=%d",
           OFFSET_CXWND_NEXT_SIBLING, OFFSET_CXWND_FIRST_CHILD,
           OFFSET_CXWND_DSHOW, OFFSET_CXWND_MINIMIZED,
           OFFSET_CXWND_XMLINDEX, OFFSET_CXMLDATA_NAME,
           kMQ2StyleWidgetLookup ? 1 : 0);
}

} // namespace EQMainWidgetsMQ2
