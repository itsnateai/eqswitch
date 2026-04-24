// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// Native/eqmain_offsets.h -- eqmain.dll-side function pointer resolution
//
// STEP 2A of the v8 MQ2 port. This header declares the range-tracking and
// ownership-testing primitives used to route widget operations to the
// correct DLL's function bodies.
//
// Background:
//   mq2_bridge's g_fnSetWindowText / g_fnWndNotification are resolved from
//   dinput8.dll exports — these are EQClasses::CXWnd:: functions (eqgame
//   side). When called on a widget that lives inside eqmain.dll (separate
//   parallel class hierarchy with different member offsets and vtable
//   layout), the functions read/write at the wrong offsets and SEH-fault.
//   Live 2026-04-16 log evidence: 22 SEH faults per login, all on widgets
//   with vtables inside eqmain.dll's load range (0x71E20000-0x72179000).
//
// MQ2 solves this by pattern-resolving eqmain-side function pointers at
// DLL-notification LOAD time and dispatching based on which DLL owns the
// widget (see LoginFrontend.h's separate eqmain::CXWnd class parallel to
// the global CXWnd). This is the mirror of that approach for EQSwitch.
//
// Scope of STEP 2A (this file now):
//   - Range tracking: cache eqmain's [base, base+SizeOfImage) on LOAD, clear
//     on UNLOAD.
//   - Ownership test: IsEQMainWidget() checks whether a CXWnd's vtable
//     pointer falls inside the cached range.
//   - No function resolution yet — callers use the result to LOG which
//     widgets are eqmain-side so STEP 2B has empirical data.
//
// Scope of STEP 2B (next session):
//   - Resolve eqmain-side function pointers for SetWindowTextA / WndNotification
//     / GetChildItem / GetXMLData (strategy TBD once Step 2A data is in).
//   - Route widget ops through them when IsEQMainWidget(pWnd) is true.
//
// Thread safety: all operations are simple pointer comparisons against
// snapshot-stable globals updated only from the loader-lock-held DLL
// notification callback. Readers see a consistent base/size pair because
// updates go through InterlockedExchangePointer / a single 64-bit write.
// The "is loaded" check is a single pointer read.

#pragma once
#include <stdint.h>

namespace EQMainOffsets {

// ─── Lifecycle ────────────────────────────────────────────────
// Called from LdrDllNotificationCallback in eqswitch-di8.cpp on eqmain
// LOAD. Caches base/size and logs the registration. Idempotent — repeat
// calls with the same base are no-ops; calls with a different base log
// a warning and overwrite (shouldn't happen for in-process eqmain).
void OnEQMainLoaded(uintptr_t dllBase, uint32_t sizeOfImage);

// Called on eqmain UNLOAD. Clears cached range. Any in-flight
// IsEQMainWidget() check after this point returns false.
void OnEQMainUnloaded();

// ─── Ownership test ───────────────────────────────────────────
// Returns true if pWnd is a plausible CXWnd whose vtable pointer lives
// inside the cached eqmain.dll range. Performs an SEH-wrapped deref of
// *(uintptr_t*)pWnd to read the vtable pointer; returns false on fault
// (treating unreadable memory as not-eqmain so callers fall back to the
// existing eqgame-side code path).
//
// Safety note: this is called on pointers sourced from heap-scan /
// cross-ref results which can be stale between charselect and eqmain
// unload. The SEH wrapper means IsEQMainWidget(stale_pointer) returns
// false instead of crashing.
bool IsEQMainWidget(const void *pWnd);

// ─── Diagnostics ──────────────────────────────────────────────
// Returns cached eqmain range. Base=0, size=0 when unloaded.
// Used by log statements so operators can correlate widget pointers to
// the active eqmain load without grep-walking earlier log lines.
void GetRange(uintptr_t *outBase, uint32_t *outSize);

// ─── Step 2B: vtable-dispatched eqmain method resolution ─────
//
// eqmain::CXWnd declares SetWindowText and WndNotification as virtual
// methods. MQ2's autologin plugin (MQ2AutoLogin.cpp:993-1002) dispatches
// them via `reinterpret_cast<eqmain::CXWnd*>(pWnd)->WndNotification(...)` —
// the compiler emits a normal virtual call through the widget's own vtable.
// We mirror that architecture here: given an eqmain widget, read its vtable
// pointer, index to the slot, return the slot function pointer.
//
// Authoritative source for vtable layout (this project, not MQ2 upstream):
//   Static RTTI walk of C:/Users/nate/proggy/Everquest/Eqfresh/eqmain.dll
//   (Dalaya ROF2 frozen 2013-05-11). Cross-verified against the eqgame-side
//   virtual-dispatch thunks in dinput8.dll (?SetWindowTextA@CXWnd, etc).
//   Reproduce with: `python X:/_Projects/eqswitch/dumps/find_slot_diffs.py`.
//
//   dinput8.dll ?SetWindowTextA    — thunk: `mov eax,[ecx]; lea eax,[eax+0x124]; mov eax,[eax]; jmp eax`
//   dinput8.dll ?WndNotification   — thunk: `mov eax,[ecx]; lea eax,[eax+0x88];  mov eax,[eax]; jmp eax`
//
//     method           | slot # | 32-bit offset | notes
//     WndNotification  |   34   |     0x88      | inherited in CButtonWnd; real body in CXWnd dispatches slot 35
//     SetWindowText    |   73   |     0x124     | thunk in CXWnd (add ecx,0x1A8; jmp CXStr::op=); override in CEditWnd
//
// HISTORY: commit 7528477 had these at 0x88 (correct) and 0x128 (WRONG — slot
// 74, not 73). Slot 74 in CXWnd is `push ebp; xor ebp,ebp; cmp [ecx+0x1CE],0; ...`
// — an unrelated flag-test method. Calling it as SetWindowText caused a
// 4-byte stack imbalance on the CXMLDataPtr vtable that crashed mid-password
// (see memory project_eqswitch_v8_step2b_vtable_probe.md). Fixed here to 0x124.
//
// Defense-in-depth (necessary because the Phase 5 heap-scan returns CXMLDataPtr
// definition pointers, NOT live CEditWnd/CButtonWnd widgets, and those
// pointers ALSO test positive under IsEQMainWidget's range check):
//   1. Exact-vtable class validation via IsEQMainEditWidget / IsEQMainButtonWidget
//      below — compares pWnd's vtable ptr against cached runtime RVAs of
//      CEditWnd / CButtonWnd / CSidlScreenWnd. Rejects CXMLDataPtr and all
//      other non-widget classes cleanly (no SEH).
//   2. Slot fn-ptr must land inside eqmain's .text range (see ResolveVtableSlot).
//   3. Optional: prologue-byte sanity check via ValidateSlotPrologue —
//      rejects slots whose first bytes don't match a known function prologue.
//   4. Call site still __try/__except so a bad resolution catches instead
//      of corrupting state before faulting.
constexpr uint32_t VTABLE_OFFSET_WndNotification = 0x88;   // slot 34 * 4
constexpr uint32_t VTABLE_OFFSET_SetWindowText   = 0x124;  // slot 73 * 4

// Static-verified vtable RVAs (ImageBase=0x10000000, runtime 0x71E20000).
// eqmain.dll has no ASLR per memory — these are stable across launches.
// Reproduce with `python X:/_Projects/eqswitch/dumps/find_vtables.py`.
constexpr uint32_t RVA_VTABLE_CXWnd          = 0x0010A084;
constexpr uint32_t RVA_VTABLE_CSidlScreenWnd = 0x0010A574;
constexpr uint32_t RVA_VTABLE_CButtonWnd     = 0x0010B53C;
constexpr uint32_t RVA_VTABLE_CEditBaseWnd   = 0x0010BCDC;
constexpr uint32_t RVA_VTABLE_CEditWnd       = 0x0010BE6C;
constexpr uint32_t RVA_VTABLE_CListWnd       = 0x0010AE94;
constexpr uint32_t RVA_VTABLE_CLabelWnd      = 0x0010AC2C;

// Signatures match the eqgame-side dinput8 exports so call sites can
// swap between them without adapter code:
//   FN_WndNotification: `int (CXWnd*, CXWnd* sender, uint32_t msg, void* data)`
//   FN_SetWindowText:   `void (CXWnd*, CXStr* text)`
typedef int  (__thiscall *FN_WndNotification)(void *thisPtr, void *sender, uint32_t msg, void *data);
typedef void (__thiscall *FN_SetWindowText)  (void *thisPtr, void *pCXStr);

// ─── MQ2-faithful widget resolution ──────────────────────────
// MQ2's autologin plugin resolves widgets by XML name via CXWnd::GetChildItem,
// which internally walks the widget's child tree comparing pXMLData->Name
// (and ScreenID) case-insensitively. Dalaya's MQ2 dinput8.dll exports the
// char*-overload of this function:
//   ?GetChildItem@CXWnd@EQClasses@@QAEPAV12@PAD@Z
// as a thunk into MQ2's own C++ implementation, which is byte-for-byte the
// MacroQuest eqlib source (CXWnd.cpp RecurseAndFindName + GetChildItem).
// Calling it on each top-level widget in eqmain's pWindows reproduces MQ2's
// autologin widget lookup verbatim — no pattern scan, no heuristic.
typedef void *(__thiscall *FN_CXWndGetChildItemChar)(void *thisPtr, const char *name);

// Returns the eqmain-side function pointer for this widget's
// WndNotification / SetWindowText vtable slot, or nullptr if:
//   - pWnd is not an eqmain widget (falls through to eqgame-side fn)
//   - eqmain range is unloaded
//   - vtable or slot read faults
//   - the resolved slot is outside eqmain's code range (defense-in-depth)
//   - the vtable is NOT one of the known CXWnd-family widget vtables
//     (defends against the Phase 5 heap-scan returning CXMLDataPtr
//      definitions which superficially pass the range check but SEH-fault
//      when called because their slot bodies have different signatures)
//
// Caller is expected to fall back to its existing eqgame-side function
// pointer when the return is nullptr. Never returns a pointer that
// isn't inside the currently-cached eqmain module range.
FN_WndNotification GetWndNotificationFor(const void *pWnd);
FN_SetWindowText   GetSetWindowTextFor  (const void *pWnd);

// ─── Widget class validation (exact vtable match) ────────────
// Checks whether pWnd's vtable pointer matches one of the known
// CXWnd-family vtables (CXWnd, CSidlScreenWnd, CEditBaseWnd, CEditWnd,
// CButtonWnd, CListWnd, CLabelWnd). Returns false for CXMLDataPtr and
// all other eqmain classes that have a CXStr at +0x18 but aren't widgets.
//
// Why: the Phase 5 heap-scan enumerates objects by "has eqmain vtable +
// CXStr at +0x18" which matches CXMLDataPtr definitions (screen-piece
// blueprints) just as well as live widgets. Calling SetWindowText or
// WndNotification on a definition corrupts state because the member
// offsets don't match. Step 3 (ReinitializeWindowList port, future) will
// get real widget pointers from the live CXWndManager tree; until then
// this gate prevents the native dispatch path from hitting bad objects
// without a SEH flood.
//
// IsEQMainWidgetClass = ANY of the known widget vtables.
// The specialized helpers target operations that need a narrower type.
bool IsEQMainWidgetClass(const void *pWnd);
bool IsEQMainEditWidget  (const void *pWnd);   // CEditWnd or CEditBaseWnd
bool IsEQMainButtonWidget(const void *pWnd);   // CButtonWnd

// Diagnostic — returns the short class name for pWnd's vtable, or nullptr
// if not a known widget class. Does not allocate; returns a string literal.
const char *GetEQMainWidgetClassName(const void *pWnd);

// ─── Step 3: live CXWndManager tree walk (MQ2 ReinitializeWindowList port) ──
//
// Replaces Phase-5 heap-scan false positives with authoritative enumeration
// from CXWndManager::pWindows. MQ2's ReinitializeWindowList iterates this
// array to map widget names to live pointers — we do the equivalent via
// tree walk so we get widgets (not screens) back without needing a
// pattern-scanned GetChildItem function pointer.
//
// Offsets confirmed by static disassembly of eqmain-dalaya.dll (see
// dumps/scout_step3_v2.py). CXWndManager ctor @ RVA 0x0007a460 zeroes
// pWindows{data, count, capacity} at [this+0x04..0x0c] — classic 32-bit
// ArrayClass<CXWnd*> layout, 12 bytes wide.
constexpr uint32_t RVA_VTABLE_CXWndManager        = 0x0010abe0;
// ArrayClass<CXWnd*> pWindows starts at CXWndManager+0x04. Layout inherits
// from CDynamicArrayBase (`m_length` first at +0x00), THEN ArrayClass adds
// `m_array` (data ptr) at +0x04, `m_alloc` (capacity) at +0x08. So within
// the outer CXWndManager: [+0x04]=count, [+0x08]=data, [+0x0c]=alloc.
//
// Earlier pass had Cnt/Data swapped which caused the scanner to pick a
// zombie object (anything with the CXWndManager vtable at +0 whose +4
// happened to look like a pointer and +8 like a small int). The SEH-
// wrapped reads were "succeeding" on garbage.
constexpr uint32_t OFF_CXWndManager_pWindows_Cnt  = 0x04;   // m_length
constexpr uint32_t OFF_CXWndManager_pWindows_Data = 0x08;   // m_array
constexpr uint32_t OFF_CXWndManager_pWindows_Alloc = 0x0c;  // m_alloc (capacity)

// CXWnd tree-link offsets. Values from reference_eqswitch_dalaya_rof2_offsets.md,
// empirically validated by v7 Phase 6 walk. If Step 3 telemetry shows no
// screens being found, these are the first thing to re-verify.
constexpr uint32_t OFF_CXWnd_NextSibling = 0x08;
constexpr uint32_t OFF_CXWnd_FirstChild  = 0x10;

// Scans eqmain's data range for the live CXWndManager instance (object whose
// vtable pointer equals base+RVA_VTABLE_CXWndManager). Result is cached per
// eqmain load — first call walks the range (~2-4MB), subsequent calls return
// the cached pointer. Returns nullptr if eqmain unloaded or no match.
//
// Thread safety: first-call scan holds a static-bool gate under a trivial
// one-shot pattern. Worst case under a race, two threads scan; they find
// the same address; last-writer-wins is fine. No lock because the scan
// runs on the game thread in practice (from GiveTime detour).
void *FindLiveCXWndManager();

// Copies up to maxCount top-level screen pointers from pWndMgr->pWindows
// into `out`. Returns the number actually copied. Each entry is validated
// via IsEQMainWidgetClass before being included — this rejects stale
// pointers the heap might have left in the array mid-teardown.
//
// Typical return: 3-7 during login (LoginBaseScreen, SplashScreen,
// YESNO_Dialog, OK_Dialog, etc.). Returns 0 if CXWndManager not found or
// pWindows.Count is obviously corrupt (> 256).
int EnumerateTopLevelScreens(void *out[], int maxCount);

// BFS tree-walk starting from every top-level screen. For each widget
// reached, if `classFilterRVA` matches the widget's vtable (or is 0 meaning
// "any widget class"), adds the widget to `out`. Returns the count copied.
//
// `maxCount` bounds the output array. Internal traversal queue is fixed
// at 512 entries — sufficient for login-screen trees which have ~20-40
// widgets total. Beyond that, additional widgets are silently skipped
// (diagnostic log notes the overflow).
//
// Every pointer deref is SEH-wrapped individually so a single bad link
// doesn't abort the whole walk. This matters because Step 2B already
// showed that some LoginController fields point at CXMLDataPtr/junk —
// our class filter rejects them, but the walk itself must survive.
int EnumerateWidgetsInTree(void *out[], int maxCount, uint32_t classFilterRVA);

// One-shot diagnostic that walks the tree and dumps:
//   - # of top-level screens
//   - per-screen: pointer, class name, child count
//   - flat list of CEditWnd pointers found (Username/Password candidates)
//   - flat list of CButtonWnd pointers found (Connect/Yes/OK candidates)
//   - flat list of CListWnd pointers found (CharacterList candidate)
//
// Called at most once per eqmain load — subsequent calls are no-ops. Log
// volume is ~50 lines per login, one-time. Goal: empirically verify the
// tree walk finds real widgets that the Phase-5 heap scan missed.
void DumpStep3TreeDiagnostic();

// Returns true for widget names that the Step 3 resolver understands,
// regardless of whether it currently has a live widget to return. Used
// by mq2_bridge::FindWindowByName to gate Phase-5 heap-scan fallbacks
// while eqmain is loaded — if Step 3 owns the name, the heap scan's
// CXMLDataPtr false positives (which cause SEH floods via ReadWindowText)
// never get a chance to pollute the result.
bool IsStep3KnownName(const char *name);

// MQ2-faithful widget-by-name resolver. Iterates pWindows and delegates
// each entry to Dalaya MQ2's exported CXWnd::GetChildItem(const char*),
// which recursively walks the widget subtree matching pXMLData->Name or
// pXMLData->ScreenID case-insensitively (see MQ2AutoLogin.h:60-66 +
// macroquest-rof2-emu/src/eqlib/src/game/CXWnd.cpp RecurseAndFindName).
//
// No heuristic. No text/ordinal matching. Returns the same widget MQ2's
// autologin plugin would locate via `GetChildWindow<T>(m_currentWindow,
// name)` — except we don't need MQ2's LoginStateSensor to know which
// top-level is active; we try every top-level until one returns a hit.
//
// Requires the MQ2 bridge function pointer to be resolved (happens in
// OnEQMainLoaded via ResolveMQ2FaithfulFunctions). Returns nullptr when
// resolution hasn't fired yet or no screen contains a child with this name.
void *FindWidgetByKnownName(const char *name);

// Resolves the CXWnd::GetChildItem(char*) export from Dalaya's dinput8.dll
// (Dalaya MQ2 — loaded by the Windows loader before eqgame's main thread
// runs, always available by the time eqswitch-di8 is injected). Called
// from OnEQMainLoaded; idempotent, safe to call repeatedly. Returns true
// once resolved — caches the function pointer in a static.
bool ResolveMQ2FaithfulFunctions();

// ─── The pSidlMgr swap — makes GetChildItem work on eqmain widgets ────
//
// dinput8's CXWnd::GetChildItem implementation reads a GLOBAL variable
// `pSidlMgr` (a ForeignPointer<CSidlManager>) whose address is exported as
// `ppSidlMgr` at dinput8 RVA 0x1353b8. During login, this pointer is null
// or stale — eqgame hasn't loaded its CSidlManager yet. GetChildItem then
// crashes inside pSidlMgr->GetParamManager() or silently returns null.
//
// MQ2's solution (Globals.cpp:1678): on eqmain LOAD, assign
// `pSidlMgr = EQMain__pinstCSidlManager` so that lookups route through
// eqmain's instance. We replicate this behavior without having MQ2's
// hook infra — scan eqmain's memory for the CSidlManager instance
// (vtable match against RVA_VTABLE_CSidlManagerBase), write that pointer
// into ppSidlMgr, then GetChildItem walks the correct tree.
//
// Paired with RestorePSidlMgrSwap() on eqmain UNLOAD to restore the
// prior value so eqgame's state isn't polluted.
bool InstallPSidlMgrSwap();
void RestorePSidlMgrSwap();

// Scans eqmain's committed memory regions for an object whose vtable
// pointer equals `base + RVA_VTABLE_CSidlManagerBase`. Returns nullptr if
// no live instance exists (eqmain not loaded, or instance not yet
// constructed). Cached; invalidated on eqmain UNLOAD.
void *FindLiveCSidlManagerBase();

// RTTI-verified vtable RVA for CSidlManagerBase in eqmain-dalaya.dll.
// Source: vtable_dump.txt line "CSidlManagerBase TD=0x101341e4 ...
// vtables=['0x1010aa40']".
constexpr uint32_t RVA_VTABLE_CSidlManagerBase = 0x0010AA40;

// ─── Step 2B-diag: one-shot vtable dump ──────────────────────
// Called ONCE per unique eqmain vtable (editbox vtable ≠ button vtable).
// Dumps all non-null slots 0..179 with:
//   - slot index + byte offset
//   - resolved fn ptr
//   - RVA from eqmain base (so operators can cross-reference with disassembly)
//   - whether fn ptr is inside eqmain code range
//   - first 12 bytes of function prologue (for signature matching)
//
// `referenceExport` is a known eqgame-side function body pointer (from
// dinput8.dll export resolution) we're trying to find the eqmain-side
// counterpart for. Its first 12 bytes are logged alongside so
// post-processing can eyeball-match. Pass 0 to skip that.
//
// Safe on pointers of unknown quality — every memory read is SEH-wrapped.
// Rate-limited by a tiny static dedup array (max 8 distinct vtables).
void DumpVtableForDiagnostics(const void *pWnd, const char *widgetKind,
                              uintptr_t referenceExport);

} // namespace EQMainOffsets
