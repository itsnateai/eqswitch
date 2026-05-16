// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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
// dShow PINNED 2026-04-26 (offset 0x196 from vtable slot disasm — see
// OFFSET_CXWND_DSHOW comment below). IsCXWndVisible() now reads the byte
// at +0x196 (no longer returns true unconditionally — header doc updated
// 2026-05-15 to match implementation).
//
// CXMLDataManager-hash structure: STILL pending. GetCXWndXMLName() does
// NOT return nullptr unconditionally — implementation falls back to a
// CStrRep heuristic scan of the first 0x200/0x400 bytes of the widget body
// (eqmain_widgets_mq2style.cpp:163-205). Returns the first plausible
// CStrRep hit's UTF-8 buffer copied into a thread-local-shared static
// (cpp:198 — caller MUST NOT cache the pointer across calls). Returns
// nullptr only when no CStrRep field is found OR when SafeRead faults.
// Has false-positive surface: tooltip / style / font-face strings can
// match if they appear before the SIDL name. Phase 2 CXMLDataManager pin
// would replace the heuristic with the canonical lookup.
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

// ─── ITER 12 v5 MASTER TOGGLE — RE-ENABLED 2026-05-15 ──────
// Re-enabled per the MQ2 RoF2-emu autologin diff (Diff 2/3): with the legacy
// heap-cross-ref path, LOGIN_ConnectButton lookup returns a CXMLDataPtr
// definition (not a live widget), gets rejected by IsEQMainButtonWidget,
// retries 3× then C# falls back to BURST 1 keystrokes. The structural path
// (FindLiveScreenByName via LVM+0x14 anchor → RecurseAndFindName) returns
// the LIVE widget — verified working for OK_Display polling in DLL log
// 2026-05-15 dual-box smoke (eqswitch-dinput8-13560.log: FindLiveScreenByName
// for 'EulaWindow', 'main', 'seizurewarning', 'news' all return non-null
// pointers with iterCompleted=1).
//
// Background-starvation note (the original 2026-04-26 dormancy reason):
//   - Iter-12 disabled this because game-thread structural walks starved
//     IDirectInputDevice8::GetDeviceState polling, dropping BURST keystrokes
//     on background clients.
//   - Path A from diff doc: with structural Combo G + ScreenMode swap
//     (v3.16.0 shipping) writing the password successfully, BURST keystrokes
//     become unnecessary; no GetDeviceState race; walk-induced starvation
//     is harmless. The 2026-05-15 DLL log confirms Combo G writes do reach
//     +0x1A8 ("WriteEditTextDirect read-back OK — length=7, first byte 0x45
//     matches"), so the prereq is met.
//   - ConnectButton structural lookup adds ONE walk per PHASE_CLICKING_CONNECT
//     tick — comparable cost to the OK_Display polling already firing every
//     ~200ms today without measurable impact.
constexpr bool kMQ2StyleWidgetLookup = true;

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

// FindEmptyEditInScreen — STRUCTURAL password lookup that bypasses the
// CXMLDataManager-name-resolution problem on Dalaya (FindChildByName for
// 'LOGIN_PasswordEdit' returns NULL because the SIDL name heuristic doesn't
// recover the right widget name).
//
// Walks the named screen's subtree and returns the FIRST widget that:
//   1. Has vtable matching CEditWnd or CEditBaseWnd (validated via
//      EQMainOffsets::IsEQMainEditWidget)
//   2. Has a valid CXStr_Dalaya at +0x1A8 (the InputText field — every real
//      CEditWnd has this; the false-positive vtable-but-no-InputText widget
//      that the hardcoded XMLIndex=0x00220001 fallback was returning fails
//      this gate)
//   3. The CXStr's length == 0 (the password field starts empty — the
//      username field is ini-prefilled by EQ before the login UI is shown)
//
// Returns nullptr if no qualifying widget found. On re-login when the
// password field has leftover asterisks (length > 0), this returns nullptr
// — callers fall through to the legacy XMLIndex path.
//
// 2026-05-15 ground-truth from PID 10012 smoke (eqswitch-dinput8-10012.log
// lines 145-184): the connect screen has 3 CEditWnd-shape widgets at
// 115040F0 (empty CXStr@+0x1A8, password edit), 115046C0 ('gotquiz' at
// +0x1A8, username), and 11504A08 (NO valid CXStr at +0x1A8 — the hardcoded
// XMLIndex=0x00220001 fallback picks this one, then Combo G writes the
// password to memory that isn't the visible InputText, leaving the
// rendered password field empty and EQ login server rejecting the
// 1-char fragment that BURST 1 keystrokes managed to land in the
// visible field).
void *FindEmptyEditInScreen(const char *screenName);

// FindEmptyEditGlobal — like FindEmptyEditInScreen but walks the ENTIRE
// pinstCXWndManager widget collection via MQ2Bridge::IterateAllWindowsPublic
// rather than just the named screen's subtree. Necessary because the
// password CEditWnd on Dalaya may not be a child of the "connect" screen
// widget — the 2026-05-15 17:05 smoke showed FindEmptyEditInScreen reached
// only 2 CEditWnd-shape widgets in the connect subtree (vs 4+ globally).
//
// Per-widget filter: vt = CEditWnd|CEditBaseWnd, valid CXStr at +0x1A8
// (refCount/length/alloc sanity per CStrRep_Dalaya layout).
//
// v3.20.3 (2026-05-15) — PROXIMITY heuristic replaces the broken visibility
// filter. Smoke at 17:20 (PID 16120) showed the visibility filter picked
// a widget (1153BA30, visible=1, empty) that turned out NOT to be the
// password edit — Combo G wrote to it and user reported empty password
// fields. The actual password edit (115040F0) had visible=0 because
// password fields use asterisk-masking rendering with the dShow flag
// unset. Address-distance to the username-bearing widget is a much
// stronger signal — EQ allocates SIDL-screen widgets in a tight cluster
// (the prior smoke had password 0x5D0 below username, while the decoy
// was 0x37370 away).
//
// Algorithm: walk globally → collect every CEditWnd-shape widget with
// valid CXStr at +0x1A8 → identify the anchor (CEditWnd with non-empty
// CXStr — the ini-prefilled username) → among empties, return the one
// whose address is closest to the anchor. Falls back to first empty
// found if no anchor (no widget with non-empty CXStr in the walk).
//
// DIAGNOSTIC LOGGING: logs every CEditWnd-shape widget visited with
// vtable, +0x1A8 CXStr status, distance-to-anchor when applicable.
void *FindEmptyEditGlobal();

// ─── FindConnectButtonStructural — Round 5 plug-and-play port ──
//
// Round 5 live verification (findings.md, PID 22892 2026-05-04) established
// that ConnectWnd's child widgets live at FIXED slots in the screen body
// (NOT via CXWnd's pFirstChild list — that returns junk per
// probe_connectwnd_children.py). Layout:
//
//     ConnectWnd+0x2C..+0x38 → 4× CButtonWnd  (one of which is LOGIN_ConnectButton)
//     ConnectWnd+0x3C..+0x40 → 2× CEditWnd    (username, password)
//     ConnectWnd+0x48        → 1× CLabelWnd   (Dalaya branding)
//
// FindConnectButtonStructural:
//   1. Resolves ConnectWnd by walking pinstLoginViewManager+0..+0x200
//      for the slot whose pointee has vtable matching RVA_VTABLE_ConnectWnd.
//   2. Enumerates the 4 button slots, validates CButtonWnd vtable for each,
//      and logs full diagnostics (address, dShow, XMLIndex, name-hits).
//   3. Picks the slot whose body contains a CStrRep matching
//      "LOGIN_ConnectButton" or "ConnectButton" via CIEquals — if both fail,
//      falls back to the first valid button with a loud log line so smoke
//      iterations can lock the slot.
//
// Why this beats FindButtonNearWidget (proximity):
//   - Two `ConnectButton`s exist in eqmain.dll (MainWnd's vs ConnectWnd's)
//     and proximity tie-breaks unreliably across the ~69 CButtonWnd-shape
//     widgets enumerated globally. Scoping by ConnectWnd narrows to 4
//     candidates; one of them IS the login submit button by SIDL design.
//
// Returns nullptr if ConnectWnd can't be resolved (LVM unloaded, slot drift)
// OR if none of the 4 slots holds a CButtonWnd vtable.
void *FindConnectButtonStructural();

// ResolveConnectWnd — exposed for callers that want to anchor a structural
// walk on ConnectWnd themselves. Walks pinstLoginViewManager+0..+0x200,
// returns the first slot whose pointee.vtable matches RVA_VTABLE_ConnectWnd.
// Returns nullptr if LVM is unloaded or no ConnectWnd-vtable slot exists.
void *ResolveConnectWnd();

// FindUsernameEditStructural / FindPasswordEditStructural — Round 5 fixed
// slots on ConnectWnd (+0x40 username, +0x44 password). Live-verified
// 2026-05-15 (PIDs 24856 + 30496 + 34204 + 37432). These CEditWnds are
// NOT reachable from the standard CXWnd pFirstChild linked list — the
// previous proximity-via-FindEmptyEditGlobal heuristic picked a different
// (wrong) widget because the global CXWndManager walk doesn't enumerate
// them. Combo G must write to the structural password edit, not the
// proximity one, for the LOGIN button click to read a populated CXStr.
//
// Each function: resolves ConnectWnd, reads the fixed slot, validates
// CEditWnd/CEditBaseWnd vtable, returns the pointer. Returns nullptr
// on LVM unloaded / slot invalid / vtable mismatch.
void *FindUsernameEditStructural();
void *FindPasswordEditStructural();

// FindButtonNearWidget — walk all CButtonWnd-shape widgets globally, pick the
// one whose address is closest to `anchor`. Used to find LOGIN_ConnectButton
// after FindEmptyEditGlobal identifies the password edit — the connect button
// is allocated in the same SIDL-screen cluster (close in heap address).
//
// v3.20.5 (2026-05-15) — MQ2's autologin calls `WndNotification(connectButton,
// XWM_LCLICK, 0)` to submit, NOT a VK_RETURN keystroke. The button's onClick
// reads the password's InputText and submits to the login server. Our prior
// VK_RETURN approach went through EQ's keyboard pump which doesn't fire the
// same submit path on the password edit, so auth never started despite the
// password being in BOTH +0x1A8 and +0x1EC CXStrs (v3.20.4 dual-write).
//
// The legacy ConnectButton finder via FindWindowByName returned CXMLDataPtr
// definitions (not live CButtonWnd instances) — IsEQMainButtonWidget rejected
// them. The proximity heuristic bypasses that by addressing live widgets
// directly from their heap allocation cluster.
//
// Returns nullptr if no CButtonWnd-shape widget found.
void *FindButtonNearWidget(void *anchor);

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
