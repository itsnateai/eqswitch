# Phase 5 Recon — Byte-Pattern Scanning Dead-End on Dalaya eqmain

**Date:** 2026-04-24 (late session, Combo G iterations 1-7)
**Status:** **FALSIFICATION** of byte-pattern-scanning approach for live-widget discovery

## TL;DR

On Dalaya's `eqmain.dll` build, **live login widgets (CEditWnd, CButtonWnd) do
NOT reference their XML defs (CParamEditbox, CParamButton) via any member field
in the live widget body**, scanning 0x14..0x800. This contradicts the MQ2 ROF2
upstream layout where `m_pSidlPiece` is a CXWnd member.

The connection between Dalaya's live widgets and their XML defs MUST live in
a separate manager structure (`CSidlManager` / `CXMLDataManager` / similar).
Combo G cannot work via heap pattern-scanning alone — it needs manager-side
lookup or a Cutter/IDA RE pass to map the actual link.

## What we proved (with code instrumentation in `mq2_bridge.cpp`)

### Walker (CXWndManager screen-list tree)
- `WalkForDefBackref` walks 523 nodes from `wndArray[i]` (top-level screens
  via `g_eqmainWndMgrOffset`). Body-scan ungated from vtable filter.
- Tested with both inner def AND wrapper as `defAddr`: **0 matches, 0 REJECTED**
  for all login widgets across multiple smokes.
- **Conclusion:** Live login widgets are not reachable via the screen-list
  tree walk. Probably owned by `CLoginViewManager` (per project CLAUDE.md)
  rather than the global `CXWndManager`.

### Heap cross-ref (full heap scan)
- `FindLiveCXWnd`'s heap-scan finds objects with eqmain-range vtables whose
  body contains the def-pointer at any offset 0x14..0x400.
- Iteration 4 added per-vtable dedup (16-slot cap holds 16 distinct vtables).
- Iteration 5 added the indirect-backref check (`body[off] → CXMLDataPtr → +4 == def`).
- Result: **only `CXMLDataPtr` (vt RVA 0x10A7D4) candidates ever found.**
  The wrapper class. Never any candidate with vtable `CEditWnd` (0x10BE6C),
  `CEditBaseWnd` (0x10BCDC), or `CButtonWnd` (0x10B53C).

### Live-widget heap enum (one-shot, vtable-presence only)
- Iteration 4 added a separate scan that ignores def-backref filter and just
  counts heap objects with each known live-widget vtable.
- **Result (smoke 7): 8 CEditWnd, 1 CEditBaseWnd, 70 CButtonWnd, 10 CSidlScreenWnd, 1 CXWnd-base.**
- Iteration 6 logged 10 sample addresses per vtable with HIGH/LOW classification.
- 7/8 CEditWnd and 9/10 CButtonWnd at HIGH heap addresses — **real instances exist.**

### Wrapper trans-lookup (wrapper → live widget)
- Iteration 6 scanned each cross-ref-found wrapper's body 0x00..0x100 for
  DWORDs pointing at live-widget-vtable objects.
- **Result: 0 live-widget backrefs in any wrapper body.** Wrapper does not
  contain a parent/owner pointer to its live widget in its first 0x100 bytes.

### FORWARD scan (live widget → def)
- Iteration 7 scanned the body of each high-heap live CEditWnd/CButtonWnd
  0x14..0x800 for any DWORD pointing at an object with a def-like vtable
  (CParamEditbox 0x10D304, CParamButton 0x10AA08, CXMLDataPtr 0x10A7D4).
- **Result: 0 def-like vtable refs in 7 CEditWnd bodies + 9 CButtonWnd bodies.**

## What this means for Combo G

The current `WriteEditTextDirect` (`Native/eqmain_cxstr.cpp:223`) is given
the wrong widget pointer. It receives the CXMLDataPtr-vtable wrapper because
that's what `FindLiveCXWnd`'s heap-cross-ref returns. Slot probe on slots
72/73/74 of the wrapper's vtable always fails (wrapper class has different
slot layout than CEditBaseWnd).

To make Combo G fire we need:
1. Find the LIVE CEditBaseWnd address (not the wrapper) — requires manager-
   based lookup; byte-scanning is exhausted.
2. Either call SetWindowText at the right slot for the **actual** live class
   (probably 73 IS correct on a real CEditBaseWnd, but we never reach one),
   OR follow MQ2 ROF2's autologin pattern and write `InputText` CXStr field
   directly (`MQ2AutoLogin.cpp:1039-1051` is the model).

## Next-session direction

Per `_.claude/_comms/handoff-eqswitch-combo-g-iterations1-7-20260424.md`:

1. Pin `pinstCSidlMgr` from MQ2 source / RE pass on Dalaya eqmain.
2. Cutter/IDA Free RE pass on eqmain.dll to pin `CEditBaseWnd::InputText`
   instance offset.
3. Replace heap-cross-ref path with manager lookup; replace SetWindowText
   vtable call with InputText direct write.

## Diagnostic infrastructure (now permanent in code)

Every smoke now writes:
- `LIVE-WIDGET HEAP ENUM` block with per-vtable counts + 10 sample addresses
- `HEAP CROSS-REF` block with up to 16 deduped candidates per widget + vtable info
- `wrapper-body trans-lookup` block per selected candidate
- `FORWARD scan` block per high-heap live widget sample

That data is now reproducible across DLL builds — future RE work can
correlate against fresh logs without re-instrumenting.
