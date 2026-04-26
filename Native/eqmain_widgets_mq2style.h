// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// Native/eqmain_widgets_mq2style.h
//
// Combo G iter 12 — MQ2-style structural recursion through CXWnd::pNext +
// CXWnd::pFirstChild fields, replacing the iter 11 heap-cross-ref XMLIndex
// match. Mirrors eqmain::CXWnd's TListNode-of-siblings + TList-of-children
// inheritance pattern from MQ2's
// _.src/_srcexamples/macroquest-rof2-emu/src/eqlib/include/eqlib/game/
// LoginFrontend.h:270-273:
//
//     class CXWnd
//         : public TListNode<CXWnd>   // node in list of siblings
//         , public TList<CXWnd>       // list of children
//
// MSVC multiple-inheritance layout (Dalaya x86, both bases have virtual
// dtors so each contributes a vptr):
//     +0x00  CXWnd vptr (most-derived)
//     +0x04  TListNode<CXWnd> vptr
//     +0x08  TListNode<CXWnd>::m_pNext      ← sibling
//     +0x0C  TList<CXWnd> vptr
//     +0x10  TList<CXWnd>::m_pFirstNode     ← first child
//
// pNext + pFirstChild offsets PINNED 2026-04-26:
//   - Static disasm: dumps/vtable_dump.txt slots 41/42/75/88 read [ecx+0x10]
//     for child-list iteration; layout matches MSVC multiple-inheritance.
//   - Runtime walk: dumps/find_parent_window.py:7 documents same offsets;
//     successfully traversed sibling chains to find LOGIN_* widgets in a
//     live Dalaya client session.
//
// dShow + CXMLDataManager-hash structure: pending Phase 2 sandbox + rz-ghidra
// pseudocode review per
// `_.claude/_comms/handoff-eqswitch-combo-g-pinning-EXECUTE-NEXT-SESSION.md`.
// Until pinned, IsCXWndVisible() returns true unconditionally and
// GetCXWndXMLName() returns nullptr — both produce a graceful "fall back to
// legacy" path in callers.
//
// ─── Fail-mode contract ────────────────────────────────────────
// All public functions return nullptr when:
//   - eqmain isn't loaded
//   - any field-read SEH-faults
//   - structural traversal exhausts without a match
//   - GetCXWndXMLName is unavailable (CXMLDataManager hash unpinned)
// Callers MUST treat nullptr as "MQ2-style unavailable, fall back to legacy
// XMLIndex match" per memory/feedback_eqswitch_native_anchor.md — never
// silently regress, always log + fall back, never crash.

#pragma once
#include <stdint.h>

namespace EQMainWidgetsMQ2 {

// ─── Pinned offsets ─────────────────────────────────────────
// CXWnd inherits TListNode<CXWnd> + TList<CXWnd> in MSVC multiple-inheritance
// layout. See file header for memory layout derivation.
constexpr uint32_t OFFSET_CXWND_NEXT_SIBLING = 0x08;
constexpr uint32_t OFFSET_CXWND_FIRST_CHILD  = 0x10;

// CXMLData::sItemName is a CXStr at +0x18; pinned by mq2_bridge.cpp:761
// (existing heap-cross-ref code uses this offset successfully).
constexpr uint32_t OFFSET_CXMLDATA_NAME = 0x18;

// CXWnd::XMLIndex is uint32_t at +0xD8 (encoded `(classIdx << 16) | itemIdx`);
// pinned in eqmain_widgets.h iter 10a.
constexpr uint32_t OFFSET_CXWND_XMLINDEX = 0xD8;

// dShow byte field offset PINNED 2026-04-26.
// Source: ShouldProcessChildrenFrames (CXWnd vtable slot 68/69, fn 0x10004670)
// has the literal body `cmp byte [ecx+0x196], 0 ; je SKIP ; cmp byte [ecx+0x1CE], 0 ; jne SKIP`,
// which is `IsVisible() && !IsMinimized()` per MQ2 LoginFrontend.h:351-352.
// Therefore dShow is at +0x196 and Minimized is at +0x1CE.
// (Slots 68 and 69 share a single fn pointer due to MSVC /OPT:ICF — both
// virtuals had identical bodies.)
constexpr uint32_t OFFSET_CXWND_DSHOW     = 0x196;
constexpr uint32_t OFFSET_CXWND_MINIMIZED = 0x1CE;  // free byproduct

// CXWnd window-message constant — XWM_LCLICK = 1 in MQ2 ROF2 source
// (engine-stable; confirmed via WndNotification call sites).
constexpr uint32_t XWM_LCLICK = 1;

// ─── ITER 12 v5 MASTER TOGGLE — DISABLED 2026-04-26 ────────
// Set false after 4 background-fail dual-box runs across v1-v4. Three
// review agents converged on the same root cause at 75% confidence:
// the MQ2-style structural walk (IterateAllWindowsPublic + RecurseAndFindName)
// runs from Tick() on EQ's game thread (via LoginController::GiveTime detour).
// That thread also services IDirectInputDevice8::GetDeviceState, which is
// how SHM-injected BURST keystrokes get observed by EQ's input loop.
// When the walk is in flight, GetDeviceState polling stalls -> background
// client's keystrokes drop. Foreground has a Win32 keyboard fallback path
// that doesn't depend on GetDeviceState, hence deterministic
// foreground-OK / background-fail across all 4 iter-12 builds.
//
// With toggle == false, both call sites (FindLivePasswordCEditWnd in
// eqmain_widgets.cpp and the ConnectButton lookup in login_state_machine.cpp)
// skip the MQ2-style attempt entirely and use the legacy heap-cross-ref +
// cache path that v3.12.0 / 17ce2ca shipped working.
//
// Iter-13/14 redesign brief (per Nate 2026-04-26):
//   1. Combo G primary, no keystrokes — write password directly into the
//      InputText CXStr at +0x1A8, skip BURST entirely.
//   2. Sidequest: trace how eqmain populates username from
//      eqlsPlayerData.ini and mirror that EXACT pattern for password —
//      EQ's own username-write path doesn't have foreground/background
//      asymmetry, so whatever it does works for both clients.
constexpr bool kMQ2StyleWidgetLookup = false;

// ─── Public API ─────────────────────────────────────────────
// FindLiveScreenByName — finds the first top-level widget whose body
// contains a CXStr field matching `name` (case-insensitive) AND has at
// least 3 child widgets. Uses the proven `find_parent_window.py` heuristic:
// rather than gate on CSidlScreenWnd vtable (which Dalaya may not use
// uniformly) or on dShow visibility (whose offset is 90% confident, not
// 100%), it filters by structural shape — top-level widget with eqmain
// vtable that has children that are themselves eqmain widgets.
// Walks MQ2Bridge::IterateAllWindowsPublic.
//
// Returns nullptr when no top-level widget matches.
void *FindLiveScreenByName(const char *name);

// One-shot per-eqmain-load diagnostic. Logs every top-level widget's
// vtable, child count, and any plausible CXStr names found in its first
// 0x400 bytes. Run once at first FindLivePasswordCEditWnd call. Helps
// debug heuristic misses by showing what's actually there.
void DumpTopLevelWidgetNamesOnce();

// RecurseAndFindName — depth-first recurse pWnd's child tree, comparing
// each visited node's name (via GetCXWndXMLName) to `name` (case-insensitive).
// Direct port of MQ2's
// _.src/_srcexamples/macroquest-rof2-emu/src/eqlib/src/game/CXWnd.cpp:115-149:
//
//     static CXWnd* RecurseAndFindName(CXMLDataManager*, CXWnd* pWnd, const CXStr& Name)
//     {
//         if (!pWnd) return nullptr;
//         if (CXMLData* pXMLData = pWnd->GetXMLData(dataMgr)) {
//             if (mq::ci_equals(pXMLData->Name, Name))     return pWnd;
//             if (mq::ci_equals(pXMLData->ScreenID, Name)) return pWnd;
//         }
//         if (CXWnd* pChildWnd = pWnd->GetFirstNode())
//             if (CXWnd* tmp = RecurseAndFindName(dataMgr, pChildWnd, Name))
//                 return tmp;
//         if (CXWnd* pSiblingWnd = pWnd->GetNext())
//             return RecurseAndFindName(dataMgr, pSiblingWnd, Name);
//         return nullptr;
//     }
//
// We compress GetXMLData/Name/ScreenID into a single GetCXWndXMLName call.
// The iter 12 port uses field reads at +0x08 / +0x10 directly instead of
// MQ2's compiler-bound member access.
//
// Recursion depth bounded at 32 (deeper than any observed Dalaya UI tree).
// Sibling iteration bounded at 1024 with cycle detection.
void *RecurseAndFindName(void *pWnd, const char *name);

// FindChildByName — convenience composition:
//     FindLiveScreenByName(screenName) → RecurseAndFindName(screen, childName)
// Returns nullptr on either step's failure.
void *FindChildByName(const char *screenName, const char *childName);

// ─── Lower-level helpers (testable, exposed for diagnostics) ──
typedef bool (*MQ2VisitCallback)(void *pWnd, void *ctx);

// Walk pWnd's CXWnd::pNext chain (sibling list). Visits pWnd then each
// next-sibling. Bounded at 1024 iters; stops on null OR cycle. SEH-wrapped.
// Callback returning true halts iteration.
void WalkSiblings(void *pWnd, MQ2VisitCallback cb, void *ctx);

// Returns the widget's XML name (CXMLData::sItemName UTF-8 buffer) or
// nullptr when:
//   - CXMLDataManager hash structure isn't pinned (current state)
//   - XMLIndex == 0 (widget has no SIDL backing — usually a runtime-built node)
//   - any deref faults
// The returned string lives in eqmain's read-only data; caller must NOT free
// it. Stable for the duration of the current eqmain load.
const char *GetCXWndXMLName(void *pWnd);

// Returns true if pWnd's dShow byte != 0. When OFFSET_CXWND_DSHOW is the
// unpinned sentinel, returns true unconditionally so callers don't gate on
// visibility prematurely.
bool IsCXWndVisible(const void *pWnd);

// One-shot diagnostic: log offsets in use, whether CXMLDataManager hash is
// pinned, and a count of top-level screens currently iterable. Called once
// per eqmain load from the di8 init path.
void LogStartupDiagnostics();

} // namespace EQMainWidgetsMQ2
