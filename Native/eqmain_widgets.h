// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// Native/eqmain_widgets.h -- structural live-widget lookup for Combo G
//
// ITER 11 of the Combo G port (2026-04-25). Replaces the heap-cross-ref
// path in mq2_bridge.cpp's FindLiveCXWnd (which returned CXMLDataPtr
// wrappers, not live CEditBaseWnd) with a structural lookup grounded in
// MQ2's compile-time-bound mechanism: walk live CEditWnd instances on
// the heap and identify the password field by its CXWnd::XMLIndex value.
//
// Pinned x86 offsets (from iter 8/9/10 smokes):
//   - CXWnd::XMLIndex          at +0xD8     (iter 10a)
//   - CEditBaseWnd::InputText  at +0x1A8    (iter 8)
//   - pinstCSidlManager        at .data+0x214B00  (iter 8a)
//   - CEditWnd vtable          at eqmain+0x10BE6C (already in eqmain_offsets.h)
//
// Encoding (per MQ2 eqlib.natvis:351-353):
//   XMLIndex = (classIdx << 16) | itemIdx
//   getXmlData = paramManager->dataArray[classIdx].items[itemIdx].m_xmlData
//
// Iter 10a observed all 3 probed CEditWnds had classIdx=34 — the EditBox
// class index in CXMLDataManager's dataArray. To find LOGIN_PasswordEdit's
// itemIdx without walking dataArray (whose layout iter 10b couldn't pin),
// we use a HEAP-CROSS-REF SHORTCUT:
//   1. Heap-scan for "LOGIN_PasswordEdit" CXStr buf (STRICT — 4-byte
//      aligned, length@-0xC matches). Iter 10c proved this works.
//   2. Heap-scan for any DWORD == that buf address. Each hit is a
//      struct field holding the CXStr; candidates include XMLData::Name
//      and CXMLDataPtr::name.
//   3. For each hit, scan surrounding ±0x20 bytes for a small uint32
//      candidate nItemIdx (< 0x1000). XMLData entries pack nItemIdx
//      adjacent to the Name CXStr; CXMLDataPtr wrappers have a vtable
//      pointer instead.
//   4. The first hit with a clean nItemIdx candidate IS the XMLData
//      entry; cache passwordXMLIndex = (34 << 16) | nItemIdx.
//
// At login time, walk live CEditWnd instances on heap (vt match against
// EQMainOffsets::RVA_VTABLE_CEditWnd / CEditBaseWnd), return the one whose
// uint32 at +0xD8 matches the cached passwordXMLIndex.
//
// ─── DORMANT ────────────────────────────────────────────────
// Like eqmain_cxstr.cpp, this module ships DORMANT in iter 11. No call
// site invokes ResolvePasswordXMLIndex / FindLivePasswordCEditWnd until
// iter 12 wires login_state_machine.cpp's Combo G call. b142afe anchor
// preserved (autologin keystroke fallback unchanged).
//
// ─── Threat model ───────────────────────────────────────────
// Per memory/feedback_eqswitch_no_regression_to_dinput8.md, fail-mode
// hierarchy when ResolvePasswordXMLIndex / FindLivePasswordCEditWnd
// returns nothing:
//   1. Log loudly with diagnostic context
//   2. Caller treats it as Combo G unavailable, returns false
//   3. login_state_machine falls back to the b142afe keystroke path
// **Never silently regress to dinput8.**

#pragma once
#include <stdint.h>

namespace EQMainWidgets {

// CXMLDataManager classIdx for the EditBox class. Observed iter 10a:
// all three probed live CEditWnds reported `XMLIndex >> 16 == 34` (0x22).
// This is the index into dataArray, not the UI type tag.
static constexpr uint32_t CLASSIDX_EDITBOX = 34;

// Body offset of CXWnd::XMLIndex on Dalaya x86. Pinned iter 10a as the
// only field in 0x40..0x200 range matching the (type<<16)|idx shape with
// distinct values across instances. Mirrors MQ2's CXWnd.h:751
// (`/*0x094*/ uint32_t XMLIndex` on x64), compressed for x86.
static constexpr uint32_t OFFSET_XMLINDEX = 0xD8;

// ─── Lifecycle ──────────────────────────────────────────────
// Resolves the encoded XMLIndex for the LOGIN_PasswordEdit widget by
// running the heap-cross-ref algorithm described in the file header.
// Logs every step (CXStr buf address, cross-ref hit count, nItemIdx
// candidates) for forensic analysis if the resolution fails.
//
// Idempotent: subsequent calls return the cached result without rescanning.
// Call from anywhere; no thread-safety guarantees beyond not-tearing the
// cached uint32 (it's volatile-stored).
//
// Returns:
//   true  — XMLIndex resolved and cached. GetCachedPasswordXMLIndex()
//           and FindLivePasswordCEditWnd() will work.
//   false — could not find the CXStr buf, or no cross-ref hit had a
//           plausible nItemIdx candidate. Caller treats Combo G as
//           unavailable for this autologin attempt.
bool ResolvePasswordXMLIndex();

// Returns the cached encoded XMLIndex value, or 0 if not yet resolved.
uint32_t GetCachedPasswordXMLIndex();

// Reset the cache. Called from EQMainOffsets::OnEQMainUnloaded so a
// subsequent eqmain reload triggers fresh resolution.
void ResetPasswordCache();

// ─── Live-widget lookup ─────────────────────────────────────
// Walks the heap for live CEditBaseWnd / CEditWnd instances (vtable match
// against eqmain's RVA_VTABLE_CEditWnd / RVA_VTABLE_CEditBaseWnd) and
// returns the first one whose uint32 at +0xD8 matches the cached password
// XMLIndex.
//
// Returns nullptr if:
//   - ResolvePasswordXMLIndex hasn't been called or returned false
//   - eqmain isn't loaded
//   - no live CEditWnd has the matching XMLIndex (heap scan exhausted)
//
// Safe to call repeatedly; each call rescans the heap (live widgets
// appear and disappear as screens transition). Bounded — caps page
// iteration to keep the call under a few hundred milliseconds even on
// fragmented address spaces.
//
// ITER 12: this wrapper tries the MQ2-style structural traversal first
// (`EQMainWidgetsMQ2::FindChildByName("connect", "LOGIN_PasswordEdit")` +
// vtable validation). On null/invalid, falls back to FindLivePasswordCEditWnd_Legacy.
// Per memory/feedback_eqswitch_native_anchor.md the legacy XMLIndex path
// stays in place for at least 2 weeks of production validation before
// being a candidate for removal.
void *FindLivePasswordCEditWnd();

// Original ITER 11 implementation — XMLIndex-keyed heap scan with cache.
// Kept as the fallback for the ITER 12 MQ2-style wrapper.
void *FindLivePasswordCEditWnd_Legacy();

} // namespace EQMainWidgets
