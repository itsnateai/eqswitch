// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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
// MIN_CHILDREN_SCREEN removed v3.22.24: each FindLiveScreenByName / ResolveCachedScreen
// caller supplies its own min-children gate via the `minChildren` parameter (header
// default 3 for screens; dialog callers pass 0). A file-scope constant here would
// be a drift trap — the authoritative default is in eqmain_widgets_mq2style.h.

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
    uint32_t    scanBytes;   // v3.22.24-fix3: per-call body-scan window
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
    if (CXWndHasXMLName(pWnd, fnc->targetName, fnc->scanBytes)) {
        fnc->result = pWnd;
        return true;  // halt
    }
    return false;
}

void *RecurseAndFindName(void *pWnd, const char *name, uint32_t scanBytes) {
    if (!pWnd || !name) return nullptr;
    FindNameCtx fnc{ name, scanBytes, nullptr };
    bool halted = false;
    WalkSubtreeImpl(pWnd, FindNameVisitCb, &fnc, 0, &halted);
    return fnc.result;
}

// ─── FindLiveScreenByName (iter-12 v2) ───────────────────────
// v1 over-filtered: `CSidlScreenWnd` vtable + `IsCXWndVisible` together
// rejected every screen (notVisible=0, noName=9, result=null). v2 uses
// the proven `find_parent_window.py:115` shape filter:
//   - widget vtable IS in eqmain range (loose — any eqmain class)
//   - widget has at least `minChildren` direct children (v3.22.24:
//     caller-supplied — default 3 rejects leaf widgets with tooltip
//     name false-positives; dialog callers pass 0 to admit 1-child modals)
//   - widget body contains a CXStr matching `name` within 0x400 bytes
struct FindScreenCtx {
    const char *targetName;
    void       *result;
    int         minChildren;          // v3.22.24: caller-supplied — 3 for screens, 0 for dialogs
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
    if (childCount < fsc->minChildren) {
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

void *FindLiveScreenByName(const char *name, int minChildren) {
    if (!name) return nullptr;
    // Default MIN_CHILDREN_SCREEN=3 from the header keeps screen-finding
    // semantics identical to v3.22.23. Dialog callers pass 0 — see comment
    // in header for the live-process probe that confirmed okdialog has
    // children=1 (filtered out under the legacy 3-child gate).
    FindScreenCtx fsc{ name, nullptr, minChildren, 0, 0, 0, 0 };
    bool ok = MQ2Bridge::IterateAllWindowsPublic(FindScreenIterCb, &fsc);
    DI8Log("eqmain_widgets_mq2style: FindLiveScreenByName('%s', minChildren=%d) — "
           "iterCompleted=%d scanned=%d notEqmain=%d tooFewChildren=%d noName=%d result=%p",
           name, minChildren, ok ? 1 : 0, fsc.scanned, fsc.skippedNotEqmain,
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
void *FindChildByName(const char *screenName, const char *childName,
                      uint32_t scanBytes) {
    if (!screenName || !childName) return nullptr;
    void *screen = FindLiveScreenByName(screenName);
    if (!screen) {
        DI8Log("eqmain_widgets_mq2style: FindChildByName('%s','%s') — "
               "screen not found",
               screenName, childName);
        return nullptr;
    }
    void *child = RecurseAndFindName(screen, childName, scanBytes);
    if (!child) {
        DI8Log("eqmain_widgets_mq2style: FindChildByName('%s','%s', scanBytes=0x%X) — "
               "screen %p found but child name not found in subtree",
               screenName, childName, scanBytes, screen);
    }
    return child;
}

// ─── FindEmptyEditInScreen (structural password lookup) ──────
//
// Walks the named screen's subtree, returns the first CEditWnd-shape widget
// (vt = CEditWnd or CEditBaseWnd) whose InputText CXStr at +0x1A8 is valid
// AND has length == 0.
//
// Architectural fix for the 2026-05-15 PM smoke bug: the hardcoded
// XMLIndex=0x00220001 fallback was returning a widget at 0x11504A08 that
// has the right vtable but DOESN'T have a valid CXStr at +0x1A8. Combo G's
// `WriteEditTextDirect` would happily write to that widget's +0x1A8 (the
// read-back succeeded because we read what we just wrote), but the
// rendered password field was unchanged — the bytes went to memory that
// isn't bound to the visible CEditWnd's render path.
//
// The +0x1A8 validity check filters out that false-positive cleanly.
// Among the remaining widgets, the password edit is the empty one
// (username is ini-prefilled by EQ before the login UI is shown).

constexpr uint32_t OFFSET_CEDITWND_INPUT_TEXT = 0x1A8;

struct FindEmptyEditCtx {
    uintptr_t vtCEditWnd;
    uintptr_t vtCEditBaseWnd;
    void *screenRoot;             // skip the root itself — defensive vs WalkSubtreeImpl
                                  // visiting root before descending into children (Opus
                                  // T2-C2 verifier finding 2026-05-15)
    void *result;
    int candidatesScanned;
    int candidatesEditShape;
    int candidatesValidCXStr;
    int candidatesEmpty;
};

static bool FindEmptyEditCallback(void *pWnd, void *ctx) {
    FindEmptyEditCtx *c = reinterpret_cast<FindEmptyEditCtx*>(ctx);
    if (!pWnd || c->result) return c->result != nullptr;

    // (0) Screen-root skip (Opus T2-C2 2026-05-15): WalkSubtreeImpl visits
    //     pWnd BEFORE descending into children. The screen container has
    //     a CSidlScreenWnd vtable in practice (rejected by the vt check
    //     below), but if a future Dalaya patch ever puts a CEditWnd-shaped
    //     screen root with empty +0x1A8 in the connect screen position,
    //     this guard prevents structural-empty from returning the wrong
    //     widget. Defensive depth-1 enforcement.
    if (pWnd == c->screenRoot) return false;

    c->candidatesScanned++;

    // (1) vtable check — must be CEditWnd or CEditBaseWnd
    uintptr_t vt = SafeRead4(pWnd, 0);
    if (vt != c->vtCEditWnd && vt != c->vtCEditBaseWnd) return false;
    c->candidatesEditShape++;

    // (2) CXStr-at-+0x1A8 validity check — the false-positive widget
    //     (vt=CEditWnd but no real InputText) fails this gate. Validates
    //     refCount + length + alloc sanity against CStrRep_Dalaya layout.
    uintptr_t pRep = SafeRead4(pWnd, OFFSET_CEDITWND_INPUT_TEXT);
    if (!pRep) return false;
    if (pRep < 0x00010000 || pRep >= 0x80000000) return false;
    if (pRep & 0x3) return false;  // 4-byte aligned

    uint32_t refCount  = static_cast<uint32_t>(SafeRead4(
        reinterpret_cast<const void*>(pRep), CSTRREP_REFCOUNT));
    if (refCount == 0 || refCount >= 0x10000) return false;

    uint32_t allocSize = static_cast<uint32_t>(SafeRead4(
        reinterpret_cast<const void*>(pRep), CSTRREP_ALLOC));
    uint32_t length    = static_cast<uint32_t>(SafeRead4(
        reinterpret_cast<const void*>(pRep), CSTRREP_LENGTH));
    if (length > allocSize) return false;       // capacity sanity
    if (length > 0x80) return false;            // password edits are short
    c->candidatesValidCXStr++;

    // (3) Empty check — password field starts empty; username is prefilled
    if (length == 0) {
        c->candidatesEmpty++;
        c->result = pWnd;
        return true;  // halt walk
    }
    return false;
}

void *FindEmptyEditInScreen(const char *screenName) {
    if (!screenName) return nullptr;

    void *screen = FindLiveScreenByName(screenName);
    if (!screen) {
        DI8Log("eqmain_widgets_mq2style: FindEmptyEditInScreen('%s') — "
               "screen not found", screenName);
        return nullptr;
    }

    uintptr_t eqmBase = 0;
    uint32_t  eqmSize = 0;
    EQMainOffsets::GetRange(&eqmBase, &eqmSize);
    if (!eqmBase) {
        DI8Log("eqmain_widgets_mq2style: FindEmptyEditInScreen('%s') — "
               "eqmain base unresolved", screenName);
        return nullptr;
    }

    FindEmptyEditCtx ctx{};
    ctx.vtCEditWnd     = eqmBase + EQMainOffsets::RVA_VTABLE_CEditWnd;
    ctx.vtCEditBaseWnd = eqmBase + EQMainOffsets::RVA_VTABLE_CEditBaseWnd;
    ctx.screenRoot     = screen;

    bool halted = false;
    WalkSubtreeImpl(screen, FindEmptyEditCallback, &ctx, 0, &halted);

    DI8Log("eqmain_widgets_mq2style: FindEmptyEditInScreen('%s') — "
           "screen=%p scanned=%d editShape=%d validCXStr=%d empty=%d result=%p",
           screenName, screen,
           ctx.candidatesScanned, ctx.candidatesEditShape,
           ctx.candidatesValidCXStr, ctx.candidatesEmpty, ctx.result);

    return ctx.result;
}

// ─── FindEmptyEditGlobal — global widget walk variant ───────
//
// Uses MQ2Bridge::IterateAllWindowsPublic to walk the entire pinstCXWndManager
// widget collection. Applies a two-pass algorithm:
//   Pass 1: COLLECT every CEditWnd-shape widget with valid +0x1A8 CXStr.
//           Record address, length, refCount. Cap at MAX_EDIT_CANDIDATES.
//   Pass 2: ANCHOR + PROXIMITY pick:
//           - Find the ANCHOR: a CEditWnd with non-empty CXStr (the
//             ini-prefilled username — always present during autologin).
//           - Among empty CEditWnds, return the one whose address is
//             CLOSEST to the anchor. EQ allocates SIDL-screen widgets
//             in a tight cluster — password edit is adjacent to username.
//           - Falls back to first empty if no anchor (e.g., username
//             isn't pre-populated yet, edge case).
//
// v3.20.3 (2026-05-15) rationale: the 17:20 smoke showed the prior
// visibility-filter approach picked the wrong widget (a visible+empty
// CEditWnd 0x37370 away from username — unrelated UI input). The password
// edit had visible=0 (dShow flag unset, likely because password fields use
// asterisk-masking rendering through a different path). Address-proximity
// to the non-empty username CEditWnd is a much stronger signal.

static constexpr int MAX_EDIT_CANDIDATES = 32;

struct EditCandidate {
    void *pWnd;
    uintptr_t vt;
    uintptr_t pRep;
    uint32_t refCount;
    uint32_t allocSize;
    uint32_t length;
    uint8_t  dShow;
    uint8_t  minimized;
};

struct FindEmptyEditGlobalCtx {
    uintptr_t vtCEditWnd;
    uintptr_t vtCEditBaseWnd;
    int candidatesScanned;
    int candidatesEditShape;
    int candidatesValidCXStr;
    int candidateCount;
    EditCandidate candidates[MAX_EDIT_CANDIDATES];
};

static bool CollectEditCandidatesCallback(void *pWnd, void *ctx) {
    FindEmptyEditGlobalCtx *c = reinterpret_cast<FindEmptyEditGlobalCtx*>(ctx);
    if (!pWnd) return false;
    if (c->candidateCount >= MAX_EDIT_CANDIDATES) return true;  // halt
    c->candidatesScanned++;

    // (1) vtable check
    uintptr_t vt = SafeRead4(pWnd, 0);
    if (vt != c->vtCEditWnd && vt != c->vtCEditBaseWnd) return false;
    c->candidatesEditShape++;

    // (2) Read CXStr at +0x1A8 + validate via CStrRep_Dalaya layout
    uintptr_t pRep = SafeRead4(pWnd, OFFSET_CEDITWND_INPUT_TEXT);
    if (!pRep || pRep < 0x00010000 || pRep >= 0x80000000 || (pRep & 0x3) != 0) return false;
    uint32_t refCount  = static_cast<uint32_t>(SafeRead4(reinterpret_cast<const void*>(pRep), CSTRREP_REFCOUNT));
    uint32_t allocSize = static_cast<uint32_t>(SafeRead4(reinterpret_cast<const void*>(pRep), CSTRREP_ALLOC));
    uint32_t length    = static_cast<uint32_t>(SafeRead4(reinterpret_cast<const void*>(pRep), CSTRREP_LENGTH));
    if (refCount == 0 || refCount >= 0x10000000) return false;
    if (length > allocSize || length > 0x80) return false;
    c->candidatesValidCXStr++;

    // Collect — defer the empty/anchor logic to the post-walk picker
    EditCandidate &cand = c->candidates[c->candidateCount++];
    cand.pWnd      = pWnd;
    cand.vt        = vt;
    cand.pRep      = pRep;
    cand.refCount  = refCount;
    cand.allocSize = allocSize;
    cand.length    = length;
    cand.dShow     = SafeRead1(pWnd, OFFSET_CXWND_DSHOW);
    cand.minimized = SafeRead1(pWnd, OFFSET_CXWND_MINIMIZED);
    return false;  // continue walking
}

void *FindEmptyEditGlobal() {
    uintptr_t eqmBase = 0;
    uint32_t  eqmSize = 0;
    EQMainOffsets::GetRange(&eqmBase, &eqmSize);
    if (!eqmBase) {
        DI8Log("eqmain_widgets_mq2style: FindEmptyEditGlobal — "
               "eqmain base unresolved");
        return nullptr;
    }

    FindEmptyEditGlobalCtx ctx{};
    ctx.vtCEditWnd     = eqmBase + EQMainOffsets::RVA_VTABLE_CEditWnd;
    ctx.vtCEditBaseWnd = eqmBase + EQMainOffsets::RVA_VTABLE_CEditBaseWnd;

    // Pass 1: collect every valid CEditWnd-shape widget with +0x1A8 CXStr
    bool iterated = MQ2Bridge::IterateAllWindowsPublic(
        reinterpret_cast<MQ2Bridge::PublicWndIterCallback>(CollectEditCandidatesCallback),
        &ctx);

    DI8Log("eqmain_widgets_mq2style: FindEmptyEditGlobal — pass-1 collect: "
           "iterated=%d scanned=%d editShape=%d validCXStr=%d candidates=%d",
           iterated ? 1 : 0,
           ctx.candidatesScanned, ctx.candidatesEditShape,
           ctx.candidatesValidCXStr, ctx.candidateCount);

    // Log every candidate (capped naturally by MAX_EDIT_CANDIDATES = 32)
    for (int i = 0; i < ctx.candidateCount; i++) {
        const EditCandidate &c = ctx.candidates[i];
        DI8Log("eqmain_widgets_mq2style:   cand[%d] pWnd=%p CXStr@%p "
               "refCount=%u alloc=%u length=%u dShow=%u minimized=%u",
               i, c.pWnd, (void*)c.pRep, c.refCount, c.allocSize, c.length,
               (unsigned)c.dShow, (unsigned)c.minimized);
    }

    // Pass 2: find anchor (non-empty CEditWnd) + pick empty closest to it
    void *anchor = nullptr;
    uintptr_t anchorAddr = 0;
    for (int i = 0; i < ctx.candidateCount; i++) {
        if (ctx.candidates[i].length > 0) {
            anchor = ctx.candidates[i].pWnd;
            anchorAddr = reinterpret_cast<uintptr_t>(anchor);
            break;
        }
    }

    if (!anchor) {
        // No anchor — return first empty as a fallback
        for (int i = 0; i < ctx.candidateCount; i++) {
            if (ctx.candidates[i].length == 0) {
                DI8Log("eqmain_widgets_mq2style: FindEmptyEditGlobal — NO ANCHOR "
                       "(no non-empty CEditWnd found); falling back to first empty @ %p",
                       ctx.candidates[i].pWnd);
                return ctx.candidates[i].pWnd;
            }
        }
        DI8Log("eqmain_widgets_mq2style: FindEmptyEditGlobal — no empty CEditWnd "
               "found in %d candidates", ctx.candidateCount);
        return nullptr;
    }

    // Look up anchor's length for log
    uint32_t anchorLength = 0;
    for (int i = 0; i < ctx.candidateCount; i++) {
        if (ctx.candidates[i].pWnd == anchor) {
            anchorLength = ctx.candidates[i].length;
            break;
        }
    }

    // Find empty CEditWnd with smallest |address - anchorAddr|
    void *bestPwd = nullptr;
    uintptr_t bestDist = UINTPTR_MAX;
    for (int i = 0; i < ctx.candidateCount; i++) {
        const EditCandidate &c = ctx.candidates[i];
        if (c.length != 0) continue;  // skip non-empty (including anchor itself)
        uintptr_t addr = reinterpret_cast<uintptr_t>(c.pWnd);
        uintptr_t dist = (addr > anchorAddr) ? (addr - anchorAddr) : (anchorAddr - addr);
        if (dist < bestDist) {
            bestDist = dist;
            bestPwd = c.pWnd;
        }
    }

    DI8Log("eqmain_widgets_mq2style: FindEmptyEditGlobal — anchor=%p "
           "(non-empty CEditWnd len=%u) → result=%p (closest empty, dist=0x%X)",
           anchor, anchorLength, bestPwd, (unsigned)bestDist);

    return bestPwd;
}

// ─── FindButtonNearWidget — proximity heuristic for CButtonWnd ──

struct FindButtonNearCtx {
    uintptr_t vtCButtonWnd;
    uintptr_t anchorAddr;
    void *result;
    uintptr_t bestDist;
    int candidatesScanned;
    int candidatesButtonShape;
    int loggedCount;
    static constexpr int MAX_LOG = 12;
};

static bool FindButtonNearCallback(void *pWnd, void *ctx) {
    FindButtonNearCtx *c = reinterpret_cast<FindButtonNearCtx*>(ctx);
    if (!pWnd) return false;
    c->candidatesScanned++;

    uintptr_t vt = SafeRead4(pWnd, 0);
    if (vt != c->vtCButtonWnd) return false;
    c->candidatesButtonShape++;

    uintptr_t addr = reinterpret_cast<uintptr_t>(pWnd);
    uintptr_t dist = (addr > c->anchorAddr) ? (addr - c->anchorAddr) : (c->anchorAddr - addr);

    // Diagnostic log first N button widgets
    if (c->loggedCount < FindButtonNearCtx::MAX_LOG) {
        c->loggedCount++;
        uint8_t dShow     = SafeRead1(pWnd, OFFSET_CXWND_DSHOW);
        uint8_t minimized = SafeRead1(pWnd, OFFSET_CXWND_MINIMIZED);
        DI8Log("eqmain_widgets_mq2style:   btn[%d] pWnd=%p vt=%p dist=0x%X dShow=%u minimized=%u",
               c->loggedCount, pWnd, (void*)vt, (unsigned)dist,
               (unsigned)dShow, (unsigned)minimized);
    }

    if (dist < c->bestDist) {
        c->bestDist = dist;
        c->result = pWnd;
    }
    return false;
}

void *FindButtonNearWidget(void *anchor) {
    if (!anchor) return nullptr;
    uintptr_t eqmBase = 0;
    uint32_t  eqmSize = 0;
    EQMainOffsets::GetRange(&eqmBase, &eqmSize);
    if (!eqmBase) return nullptr;

    FindButtonNearCtx ctx{};
    ctx.vtCButtonWnd = eqmBase + EQMainOffsets::RVA_VTABLE_CButtonWnd;
    ctx.anchorAddr   = reinterpret_cast<uintptr_t>(anchor);
    ctx.bestDist     = UINTPTR_MAX;

    bool iterated = MQ2Bridge::IterateAllWindowsPublic(
        reinterpret_cast<MQ2Bridge::PublicWndIterCallback>(FindButtonNearCallback),
        &ctx);

    DI8Log("eqmain_widgets_mq2style: FindButtonNearWidget(anchor=%p) — "
           "iterated=%d scanned=%d buttonShape=%d result=%p (dist=0x%X)",
           anchor, iterated ? 1 : 0,
           ctx.candidatesScanned, ctx.candidatesButtonShape,
           ctx.result, (unsigned)ctx.bestDist);

    return ctx.result;
}

// ─── FindConnectButtonStructural — Round 5 live-verified path ─
//
// MQ2's StateMachine.cpp:275 (RoF2 emu branch):
//     GetChildWindow<CButtonWnd>(m_currentWindow, "LOGIN_ConnectButton")
//
// MQ2 calls `m_currentWindow->GetChildItem(name)` which recurses children
// via CXMLDataManager. Two ports attempted that direct call (Round 7) and
// both SEH-faulted — calling convention / preconditions unresolved.
//
// Live verification (findings.md Round 5, eqgame PID 22892 2026-05-04):
// ConnectWnd's widget pointers live at fixed slots in the screen body —
// NOT in the CXWnd::pFirstChild list (that returns junk per
// probe_connectwnd_children.py). The 7 child widgets are at:
//
//     ConnectWnd+0x2C → CButtonWnd
//     ConnectWnd+0x30 → CButtonWnd
//     ConnectWnd+0x34 → CButtonWnd
//     ConnectWnd+0x38 → CButtonWnd    (4 buttons total)
//     ConnectWnd+0x3C → CEditWnd      (probably username)
//     ConnectWnd+0x40 → CEditWnd      (probably password)
//     ConnectWnd+0x48 → CLabelWnd     ("Dalaya" branding)
//
// This is the plug-and-play port: enumerate the 4 button slots, validate
// each pointer points to a CButtonWnd, then disambiguate among the 4 by
// scanning each button's body for a CStrRep buffer containing
// "ConnectButton" or "LOGIN_ConnectButton" (both names exist per Round 5
// line 60-65 — Dalaya's SIDL XML defines BOTH the prefixed Name and bare
// ScreenID, and the live widget body's CStrRep heuristic hits whichever
// the engine inlined into the widget's style/anim string table).
//
// Diagnostic log: for every slot we log address, vtable RVA, dShow, and
// the first 0x40 bytes of the body so smoke logs are sufficient to lock
// down which slot is LOGIN_ConnectButton on the next iteration.

// Slot range live-verified 2026-05-15 via probe_connectwnd_slots.py on
// PIDs 24856 + 37432. Note: Round 5's findings.md said +0x2C..+0x38 but
// live shows +0x2C is NULL on current Dalaya (UI may have shifted by one
// slot). The 4 buttons live at +0x30..+0x3C, with LOGIN at +0x30. We
// scan +0x2C..+0x40 to be defensive — +0x2C and +0x40 are filtered out
// by the CButtonWnd vtable check, so harmlessly skipped.
static constexpr uint32_t CONNECTWND_BUTTON_SLOT_MIN = 0x2C;
static constexpr uint32_t CONNECTWND_BUTTON_SLOT_MAX = 0x40;
static constexpr uint32_t CONNECTWND_SLOT_STRIDE     = 0x04;

// Live-verified default: LOGIN button is at ConnectWnd+0x30 on current
// Dalaya build. If the CStrRep label-match misses (locale change, label
// refresh, future UI patch), fall back to this slot.
static constexpr uint32_t CONNECTWND_DEFAULT_LOGIN_SLOT = 0x30;

// Live-verified 2026-05-15 (PIDs 24856 + 30496 + 34204 + 37432):
//   ConnectWnd+0x40 → username CEditWnd (ini-prefilled)
//   ConnectWnd+0x44 → password CEditWnd (empty pre-login)
// CRITICAL: these CEditWnds are NOT reachable via CXWnd's pFirstChild
// linked list — they're held only by the ConnectWnd screen body at fixed
// offsets. FindEmptyEditGlobal walks pinstCXWndManager which doesn't
// enumerate them, so its "closest-empty" proximity heuristic picks a
// different (wrong) widget. Combo G writes the password to the wrong
// CXStr; the LOGIN button reads the structural password (still empty)
// at click time → auth submission contains empty password → login fails.
static constexpr uint32_t CONNECTWND_USERNAME_SLOT = 0x40;
static constexpr uint32_t CONNECTWND_PASSWORD_SLOT = 0x44;

void *FindUsernameEditStructural() {
    uintptr_t eqmBase = 0;
    uint32_t  eqmSize = 0;
    EQMainOffsets::GetRange(&eqmBase, &eqmSize);
    if (!eqmBase) return nullptr;
    void *pConnect = ResolveConnectWnd();
    if (!pConnect) return nullptr;
    uintptr_t slot = SafeRead4(pConnect, CONNECTWND_USERNAME_SLOT);
    if (!slot || slot < 0x00010000 || slot >= 0xC0000000 || (slot & 0x3)) {
        DI8Log("eqmain_widgets_mq2style: FindUsernameEditStructural — "
               "slot @ ConnectWnd+0x%02X invalid (=%p)",
               CONNECTWND_USERNAME_SLOT, (void*)slot);
        return nullptr;
    }
    uintptr_t vt = SafeRead4(reinterpret_cast<const void*>(slot), 0);
    if (vt != eqmBase + EQMainOffsets::RVA_VTABLE_CEditWnd &&
        vt != eqmBase + EQMainOffsets::RVA_VTABLE_CEditBaseWnd) {
        DI8Log("eqmain_widgets_mq2style: FindUsernameEditStructural — "
               "slot=%p vt=%p mismatch (expected CEditWnd %p)",
               (void*)slot, (void*)vt,
               (void*)(eqmBase + EQMainOffsets::RVA_VTABLE_CEditWnd));
        return nullptr;
    }
    DI8Log("eqmain_widgets_mq2style: FindUsernameEditStructural — "
           "ConnectWnd+0x%02X → %p (CEditWnd)",
           CONNECTWND_USERNAME_SLOT, (void*)slot);
    return reinterpret_cast<void*>(slot);
}

void *FindPasswordEditStructural() {
    uintptr_t eqmBase = 0;
    uint32_t  eqmSize = 0;
    EQMainOffsets::GetRange(&eqmBase, &eqmSize);
    if (!eqmBase) return nullptr;
    void *pConnect = ResolveConnectWnd();
    if (!pConnect) return nullptr;
    uintptr_t slot = SafeRead4(pConnect, CONNECTWND_PASSWORD_SLOT);
    if (!slot || slot < 0x00010000 || slot >= 0xC0000000 || (slot & 0x3)) {
        DI8Log("eqmain_widgets_mq2style: FindPasswordEditStructural — "
               "slot @ ConnectWnd+0x%02X invalid (=%p)",
               CONNECTWND_PASSWORD_SLOT, (void*)slot);
        return nullptr;
    }
    uintptr_t vt = SafeRead4(reinterpret_cast<const void*>(slot), 0);
    if (vt != eqmBase + EQMainOffsets::RVA_VTABLE_CEditWnd &&
        vt != eqmBase + EQMainOffsets::RVA_VTABLE_CEditBaseWnd) {
        DI8Log("eqmain_widgets_mq2style: FindPasswordEditStructural — "
               "slot=%p vt=%p mismatch (expected CEditWnd %p)",
               (void*)slot, (void*)vt,
               (void*)(eqmBase + EQMainOffsets::RVA_VTABLE_CEditWnd));
        return nullptr;
    }
    DI8Log("eqmain_widgets_mq2style: FindPasswordEditStructural — "
           "ConnectWnd+0x%02X → %p (CEditWnd)",
           CONNECTWND_PASSWORD_SLOT, (void*)slot);
    return reinterpret_cast<void*>(slot);
}

// Resolve ConnectWnd via pinstLoginViewManager. Walks LVM+0..+0x200 in
// 4-byte steps, returning the first DWORD whose pointee has vtable
// matching RVA_VTABLE_ConnectWnd. Vtable match is the stable anchor;
// the slot offset has historically been LVM+0x14 but may drift across
// Dalaya patches (the probe-script approach over-fits).
void *ResolveConnectWnd() {
    uintptr_t eqmBase = 0;
    uint32_t  eqmSize = 0;
    EQMainOffsets::GetRange(&eqmBase, &eqmSize);
    if (!eqmBase) return nullptr;

    uintptr_t pinstLvm = eqmBase + EQMainOffsets::RVA_PINST_LoginViewManager;
    uintptr_t lvmPtr   = SafeRead4(reinterpret_cast<const void*>(pinstLvm), 0);
    if (!lvmPtr || lvmPtr < 0x00010000 || lvmPtr >= 0xC0000000) {
        DI8Log("eqmain_widgets_mq2style: ResolveConnectWnd — pinstLVM=%p invalid",
               (void*)lvmPtr);
        return nullptr;
    }

    uintptr_t targetVt = eqmBase + EQMainOffsets::RVA_VTABLE_ConnectWnd;

    for (uint32_t off = 0; off + 4 <= 0x200; off += 4) {
        uintptr_t cand = SafeRead4(reinterpret_cast<const void*>(lvmPtr), off);
        if (!cand || cand < 0x00010000 || cand >= 0xC0000000) continue;
        if (cand & 0x3) continue;
        uintptr_t vt = SafeRead4(reinterpret_cast<const void*>(cand), 0);
        if (vt == targetVt) {
            DI8Log("eqmain_widgets_mq2style: ResolveConnectWnd — found @ "
                   "LVM+0x%03X → %p (vt=%p)",
                   off, (void*)cand, (void*)vt);
            return reinterpret_cast<void*>(cand);
        }
    }
    DI8Log("eqmain_widgets_mq2style: ResolveConnectWnd — no ConnectWnd-vtable "
           "slot in LVM+0..+0x200 (lvm=%p)", (void*)lvmPtr);
    return nullptr;
}

// Scan widget body for a CStrRep DWORD whose UTF-8 buffer case-insensitive-
// matches `targetName`. Returns true on first hit. Reuses TryReadCStrRepName
// from earlier in this file.
static bool WidgetBodyContainsName(void *pWnd, const char *targetName) {
    if (!pWnd || !targetName) return false;
    char tmp[64];
    for (uint32_t off = 0; off + 4 <= SCAN_BYTES_WIDGET; off += 4) {
        uintptr_t cand = SafeRead4(pWnd, off);
        if (!TryReadCStrRepName(cand, tmp, sizeof(tmp))) continue;
        if (CIEquals(tmp, targetName)) return true;
    }
    return false;
}

void *FindConnectButtonStructural() {
    uintptr_t eqmBase = 0;
    uint32_t  eqmSize = 0;
    EQMainOffsets::GetRange(&eqmBase, &eqmSize);
    if (!eqmBase) {
        DI8Log("eqmain_widgets_mq2style: FindConnectButtonStructural — "
               "eqmain base unresolved");
        return nullptr;
    }

    void *pConnect = ResolveConnectWnd();
    if (!pConnect) return nullptr;

    uintptr_t vtCButton = eqmBase + EQMainOffsets::RVA_VTABLE_CButtonWnd;

    // Pick priorities (in order):
    //   1. ★ "QUICK CONNECT" — skips the server-select step entirely on
    //      Dalaya (tooltip: "Quick connect to last server"). Per Nate
    //      2026-05-15: this button submits auth AND server-join in one
    //      shot, bypassing the slow LoginServerAPI populate dance + the
    //      ServerSelectWnd Enter sequence. Live at ConnectWnd+0x34
    //      (label "QUICK CONNECT").
    //   2. "LOGIN" (button text) — regular path, lands at server-select.
    //      Live at ConnectWnd+0x30.
    //   3. Slot match at CONNECTWND_DEFAULT_LOGIN_SLOT (+0x30), live-verified.
    //   4. First valid CButtonWnd in the range (last-resort fallback).
    void *byQuickConnect  = nullptr;
    void *byLabelMatch    = nullptr;
    void *byDefaultSlot   = nullptr;
    void *firstValid      = nullptr;
    void *lastValid       = nullptr;
    int   validCount      = 0;
    int   slotIdxForLabel = -1;
    int   slotIdxFirst    = -1;
    int   slotIdxLast     = -1;

    for (uint32_t off = CONNECTWND_BUTTON_SLOT_MIN;
         off <= CONNECTWND_BUTTON_SLOT_MAX;
         off += CONNECTWND_SLOT_STRIDE) {
        uintptr_t slot = SafeRead4(pConnect, off);
        bool valid = (slot != 0 && slot >= 0x00010000 && slot < 0xC0000000 &&
                      (slot & 0x3) == 0);
        uintptr_t vt = valid ? SafeRead4(reinterpret_cast<const void*>(slot), 0) : 0;
        bool isButton = valid && (vt == vtCButton);
        uint8_t dShow     = isButton ? SafeRead1(reinterpret_cast<const void*>(slot),
                                                 OFFSET_CXWND_DSHOW) : 0;
        uint8_t minimized = isButton ? SafeRead1(reinterpret_cast<const void*>(slot),
                                                 OFFSET_CXWND_MINIMIZED) : 0;
        uint32_t xmlIdx   = isButton ? static_cast<uint32_t>(SafeRead4(
                              reinterpret_cast<const void*>(slot),
                              OFFSET_CXWND_XMLINDEX)) : 0;

        bool labelQuickConnect = false;
        bool labelLogin = false;
        bool labelLoginConnect = false;
        bool labelConnect = false;
        if (isButton) {
            // ★ Label "QUICK CONNECT" — Dalaya's shortcut that skips
            // server-select. Tooltip "Quick connect to last server". Live
            // at ConnectWnd+0x34. Per Nate's operator insight 2026-05-15:
            // submits auth + server-join atomically, bypassing the slow
            // LoginServerAPI populate window.
            labelQuickConnect = WidgetBodyContainsName(reinterpret_cast<void*>(slot),
                                                      "QUICK CONNECT");

            // Label "LOGIN" — regular path. Live-verified uniqueness on
            // Dalaya 2026-05-15 (other buttons are CANCEL / QUICK CONNECT /
            // CHAT — none have a CStrRep equal to "LOGIN").
            labelLogin = WidgetBodyContainsName(reinterpret_cast<void*>(slot), "LOGIN");

            // Secondary signals — SIDL names on RoF2-emu builds. Live Dalaya
            // doesn't store these in the widget body, but we check anyway in
            // case a future patch / fork re-embeds them.
            labelLoginConnect = WidgetBodyContainsName(reinterpret_cast<void*>(slot),
                                                      "LOGIN_ConnectButton");
            labelConnect      = WidgetBodyContainsName(reinterpret_cast<void*>(slot),
                                                      "ConnectButton");
        }

        DI8Log("eqmain_widgets_mq2style: ConnectWnd+0x%02X slot=%p vt=%p "
               "isButton=%d dShow=%u min=%u xmlIdx=0x%08X "
               "labelQC=%d labelLOGIN=%d labelLOGIN_Connect=%d labelConnect=%d",
               off, (void*)slot, (void*)vt, isButton ? 1 : 0,
               (unsigned)dShow, (unsigned)minimized, xmlIdx,
               labelQuickConnect ? 1 : 0,
               labelLogin ? 1 : 0, labelLoginConnect ? 1 : 0, labelConnect ? 1 : 0);

        if (!isButton) continue;
        ++validCount;
        if (!firstValid) { firstValid = reinterpret_cast<void*>(slot); slotIdxFirst = static_cast<int>(off); }
        lastValid    = reinterpret_cast<void*>(slot);
        slotIdxLast  = static_cast<int>(off);
        if (labelQuickConnect && !byQuickConnect) {
            byQuickConnect  = reinterpret_cast<void*>(slot);
            slotIdxForLabel = static_cast<int>(off);
        }
        // Note: labelLogin matches BOTH the "LOGIN" button (exact) AND the
        // "CHAT" button (whose body contains "Login to chat" tooltip).
        // But CIEquals checks equality not substring, so "Login to chat"
        // shouldn't match "LOGIN". Still — prefer LOGIN button explicitly
        // by checking it via slot order: first button with label match is
        // captured here only if QUICK CONNECT hasn't matched first.
        if ((labelLogin || labelLoginConnect || labelConnect) && !byLabelMatch) {
            byLabelMatch    = reinterpret_cast<void*>(slot);
            if (slotIdxForLabel < 0) slotIdxForLabel = static_cast<int>(off);
        }
        if (off == CONNECTWND_DEFAULT_LOGIN_SLOT && !byDefaultSlot) {
            byDefaultSlot = reinterpret_cast<void*>(slot);
        }
    }

    void *pick;
    int   pickSlot;
    const char *reason;
    if (byQuickConnect) {
        pick     = byQuickConnect;
        pickSlot = slotIdxForLabel;
        reason   = "QUICK-CONNECT-skips-server-select";
    } else if (byLabelMatch) {
        pick     = byLabelMatch;
        pickSlot = slotIdxForLabel;
        reason   = "label-match-LOGIN";
    } else if (byDefaultSlot) {
        pick     = byDefaultSlot;
        pickSlot = CONNECTWND_DEFAULT_LOGIN_SLOT;
        reason   = "default-slot-+0x30";
    } else if (firstValid) {
        pick     = firstValid;
        pickSlot = slotIdxFirst;
        reason   = "first-valid-button-fallback";
    } else {
        pick     = nullptr;
        pickSlot = -1;
        reason   = "no-valid-button";
    }

    DI8Log("eqmain_widgets_mq2style: FindConnectButtonStructural — "
           "ConnectWnd=%p validButtons=%d pick=%p (slot=+0x%02X reason=%s) "
           "firstValid=%p (slot=+0x%02X) lastValid=%p (slot=+0x%02X)",
           pConnect, validCount, pick, pickSlot, reason,
           firstValid, slotIdxFirst, lastValid, slotIdxLast);

    return pick;
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
