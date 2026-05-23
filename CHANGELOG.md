# Changelog

## v3.22.37 — Path A trilogy DONE: per-case deref-null gates, L2722 comment-lie, Shutdown reset symmetry (2026-05-23)

8-agent verifier round on v3.22.36 (commit `9d6c067`) returned 2 APPROVE + 6 CONCERNS with 5 convergent findings — all addressable as either logic gaps in the v3.22.36 fix or downstream doc-rot. v3.22.37 closes them all as the **terminal round** of the v3.22.34→35→36→37 Path A trilogy (per [[reference_verifier_round_diminishing_returns_signal]] the iterative-rounds pattern hits diminishing returns at round 3; further rounds yield only noise).

### Fixes

1. **`DerefEverQuestPointer` single `g_derefNullLogged` flag split into 4 per-case flags.** v3.22.36's shared gate meant whichever failure mode fired first silenced the other three for the rest of the process lifetime — actively hiding signal across distinct failure modes (T2 Sonnet + T2 Opus + T3 Opus convergent CRITICAL). v3.22.37 has independent flags: `g_derefNullExportLogged` (g_ppEverQuest nullptr), `g_derefNullSehLogged` (SEH on deref), `g_derefNullStorageLogged` (`*g_ppEverQuest` zero — pinst storage uninitialised), `g_derefNullUnreadableLogged` (storage non-zero but page !IsReadablePtr — decommitted), `g_derefNullObjectLogged` (both derefs OK but CEverQuest* still null). Also: v3.22.36 conflated "pinstStorage==null" with "object==null" into a single ambiguous "constructed-nullptr" message; v3.22.37 separates them.

2. **Gate-set moved AFTER `DI8Log` returns.** Pre-v3.22.37 a silently-failing `DI8Log` (disk full, log file exclusive-locked by an external tailer) would set the gate to `true` and produce zero log output — burning the only one-shot opportunity with no diagnostic trace. v3.22.37 sets the gate after the log call so a failed log doesn't consume the flag (T2 Sonnet CRITICAL).

3. **`pinstCCharacterSelect` stale comment at the static declaration** (`mq2_bridge.cpp:99` pre-v3.22.37) fixed. The comment said `pinstCCharacterSelect: direct pointer to CCharacterSelect window (bypasses CXWndManager)` — the "direct pointer" phrasing is the exact same comment-lie pattern as the L2684 comment v3.22.36 fixed for `pinstCXWndManager`. T2 Opus + T2 Sonnet caught it. v3.22.37 replaced with the proper 2-deref doc-block + a note that "bypasses CXWndManager" referred to the operational intent (don't enumerate the window manager's children to find the char-select window) not a deref-count claim.

4. **`g_derefNull*Logged` flags added to BOTH the Shutdown reset block AND the per-charselect-cycle reset block** (T3 Opus IMPORTANT). v3.22.36's lone flag was never reset, breaking the bridge's own re-init contract that resets 5 sibling `*Logged` flags on Shutdown and 5 more on charselect cycle transitions. v3.22.37 brings the new flags in line — Shutdown/Init cycle and charselect cycle both clear them for fresh diagnostic signal.

5. **Downstream docs updated to drop stale line references** (T4 Sonnet + T4 Opus convergent). `memory/reference_dalaya_path_a_bridge_deref_bug.md` and `_.eqswitch-re/pathA-2026-05-23/FINDINGS.md` were carrying the same wrong line numbers (944/2688/3497, 2711/2794/3069) that v3.22.36 fixed in the source files; same `grep storageAddr = \*g_pinst` idiom now used downstream. The "L3954" reference in the memory file was a hallucinated artifact from v3.22.35's writeup — removed.

### Diminishing-returns boundary — STOP

This is round 3 of the Path A trilogy. Per the workspace pattern documented in [[reference_verifier_round_diminishing_returns_signal]] (and explicitly recapped in `_.eqswitch-re/NEXT-SESSION-PROMPT-v3.22.28.md` describing the v3.22.27 R0→R1→R2 cycle), iterative verifier rounds find 3-5 new findings per round in the same fix-class — the value drops sharply after round 3. Future Path A work should batch into a single comprehensive grep-and-lock pass rather than another iterative round; if a v3.22.38 verifier round happens, it should explicitly justify why the previous batch wasn't sufficient.

### Verification

Still recommended: paired-probe at live char-select per v3.22.35's verification plan. None of v3.22.36/v3.22.37's changes affect the load-bearing claim (the 2-deref helper itself is structurally unchanged); these rounds were diagnostic-quality and downstream-doc cleanup. Functional verification is one runtime smoke against a live Dalaya char-select.

## v3.22.36 — v3.22.35 verifier fix-ups: line refs, mislabel, stale L2684 comment, helper one-shot log (2026-05-23)

8-agent verifier round on v3.22.35 (commit `4819d9d`, 4 topics × Sonnet+Opus) returned 2 APPROVE + 6 CONCERNS with three convergent findings: (1) the line numbers in the v3.22.35 CHANGELOG + `mq2_bridge.cpp:86` comment block (`944/2688/2711/2794/3069/3497` for the sibling 2-deref sites) were stale — those numbers were pre-helper-insertion and shifted ~45 lines down post-insert. The cited lines now land on unrelated code (function signatures, GetProcAddress assignments, comment headers). (2) v3.22.35 called the third Path A read site "Init smoke-log" but `MQ2Bridge::Init` only logs the raw pointer at ~line 2693 without dereferencing — the actual third site is `EmitVerificationReport` (line ~3508), fired once on first successful charselect. (3) The stale comment at `mq2_bridge.cpp:2684` `// pinstCXWndManager is a uintptr_t — value IS the CXWndManager pointer (single deref)` was missed by v3.22.35's "comment cleanup" pass. Two verifiers flagged it as the exact comment-lie pattern that could seed a future repeat of the bug.

### Fixes

- **`mq2_bridge.cpp:85-89` doc-block** — removed the wrong line citations; pointed readers at `grep storageAddr = \*g_pinst` for the durable canonical reference instead.
- **`mq2_bridge.cpp:647-655` helper doc-block** — corrected "Init smoke-log" → "EmitVerificationReport".
- **`mq2_bridge.cpp:2684` stale comment** — replaced with a multi-line doc-block pointing at the canonical 2-deref idiom and noting the misleading shorthand was the same kind of comment-lie the v3.22.35 fix was designed to surface.
- **`DerefEverQuestPointer()` helper** — added one-shot Warn log on first nullptr return (3 distinct cases: `g_ppEverQuest is nullptr`, SEH on deref, both derefs succeeded but result is nullptr). Pre-v3.22.36 a deferred-MQ2-init race or partial-DLL-unload scenario would fail silently and look identical to "EQ hasn't populated CEverQuest yet." Now surfaces once per session via the per-PID `eqswitch-dinput8-{pid}.log` so the next investigator gets a breadcrumb. Global `g_derefNullLogged` is `volatile bool`; never reset (one-shot per process lifetime).

### Also amended in v3.22.35 entry below

The wrong line-number citations and the "Init smoke-log" mislabel were edited in-place in the v3.22.35 CHANGELOG entry (rather than left as-is) because future readers grepping the CHANGELOG for these lines would follow the broken references. The v3.22.35 fix itself was correct; only the documentation was off.

### What did NOT change

- The DerefEverQuestPointer helper's two-deref semantics — unchanged from v3.22.35.
- The 3 call sites in PopulateCharacterData / Poll / EmitVerificationReport — unchanged from v3.22.35.
- The runtime paired-probe verification step (still recommended before declaring victory on the deref-count claim).

### Verifier convergence notes (for the future-debugger archaeologist)

- All 8 agents independently confirmed the core technical claim (MQ2 `ppEverQuest` requires 2 derefs from `GetProcAddress`).
- T3-Opus did the deepest cross-grep and verified 10+ sibling 2-deref sites in the bridge (`g_pinstWndMgr`/`g_pinstCharSelect`/`g_pinstEQMainWnd`), corroborating the "localized bug" framing.
- T4-Sonnet found the `check-native-dll-currency.sh` release gate would have blocked the v3.22.35 tag if the DLL hadn't been rebuilt — confirming the gate is doing its job (the DLL WAS rebuilt before commit so the gate passed).
- No verifier found a structural REJECT — all CONCERNS were documentation/diagnostic-quality issues, not load-bearing logic gaps.

## v3.22.35 — Path A actually WORKING: bridge deref-count fix + comment / probe cleanup (2026-05-23)

v3.22.34 fixed two structural bugs (offset 0x18EC0→0x38E6C, field order `{Data,Count,Alloc}`→`{Count,Data,Alloc}`) but Path A still presented as `Count=0` at char-select on Dalaya. The remaining hidden bug was finally found via a next-Ghidra session against `eqgame.exe` (`X:/_Projects/_.eqswitch-re/projects/eqswitch.gpr`, analysis_complete=true, 34,698 funcs). The "Path A unreachable at char-select on Dalaya" diagnosis (memory file `reference_dalaya_path_a_unreachable_at_charselect.md`) was wrong about the cause: the array IS populated at char-select; the bridge had a missing-second-deref bug on `g_ppEverQuest`.

### The bug — missing second deref on `g_ppEverQuest`

`Native/mq2_bridge.cpp` resolves `g_ppEverQuest = (void**)GetProcAddress(g_hMQ2, "ppEverQuest")`. MQ2's exported `ppEverQuest` is declared `CEverQuest** ppEverQuest;` and initialized as `ppEverQuest = (CEverQuest**)pinstCEverQuest;` where `pinstCEverQuest = eqgame_base + 0xA67CCC` (the storage address in eqgame.exe where the heap `CEverQuest*` lives). So MQ2's `ppEverQuest` VALUE is the eqgame storage address, NOT the CEverQuest* itself. `GetProcAddress` then returns the address of MQ2's `ppEverQuest` variable — making bridge's `g_ppEverQuest` effectively `CEverQuest***`. Two derefs are needed:

```
g_ppEverQuest          → MQ2 data:    address of MQ2.ppEverQuest variable
*g_ppEverQuest         → MQ2 data:    eqgame_base + 0xA67CCC (pinst storage)
**g_ppEverQuest        → eqgame data: actual heap-allocated CEverQuest*
```

Pre-v3.22.35 the three Path A read sites (`PopulateCharacterData`, `Poll`, `EmitVerificationReport` — v3.22.35 originally said "Init smoke-log" but Init itself only logs the raw pointer without dereferencing; the actual third site is `EmitVerificationReport`, fired once on first successful charselect) each did ONLY ONE DEREF, then computed `pEverQuest + OFFSET_CHARSELECT_ARRAY` = `(eqgame_base + 0xA67CCC) + 0x38E6C` = `eqgame_base + 0xAA0B38`. That address lands in eqgame.exe's `.rdata` section (read-only constant data); whatever the linker padded there is reliably either 0 or fails the `count >= 1 && count <= LOGIN_MAX_CHARS` sanity gate. Result: every poll returned `Count=0` forever — the symptom historically attributed to "Dalaya doesn't populate at char-select."

The bridge ALREADY did two-deref correctly for the parallel pinst exports `g_pinstWndMgr` and `g_pinstCharSelect` at multiple call sites — `grep storageAddr = \*g_pinst` shows them — confirming the bridge author understood the pattern; it was a localized miss on `g_ppEverQuest` only. (v3.22.35 originally cited specific line numbers here for the two-deref sites; verifier round caught that those line numbers were stale by ~45 lines because the helper insertion shifted everything down. The line citations are corrected/removed in v3.22.36; the IDIOM is the durable reference.)

### Fix

Added `DerefEverQuestPointer()` helper after `IsReadablePtr` — SEH-wrapped two-deref returning the heap `CEverQuest*` or nullptr. Replaced all three call sites to use the helper. Stale comments referencing the pre-v3.22.34 offset (0x18EC0) at the call sites updated to the current canonical 0x38E6C with provenance pointer. The misleading `g_pinstWndMgr` comment ("*ptr IS CXWndManager*") corrected to document the actual two-deref pattern. Tracking comment block at the top of the static globals section explains the pinst deref semantics so the next reader doesn't repeat the bug.

### Provenance

Full Ghidra-side evidence chain at `X:/_Projects/_.eqswitch-re/pathA-2026-05-23/FINDINGS.md`:
- `CCharacterListWnd` CTOR/vtable/cluster decomp (CTOR `0x0042F910`, vtable `0x009C5410`, object size `0x300`, static instance at `eqgame.exe + 0x91FC34`)
- `pinstCEverQuest` confirmed at RVA `0xA67CCC` two ways: Ghidra decomp of `FUN_0042FEC0` (game's own slot iterator uses `*(int*)(DAT_00e67ccc + 0x38e6c)` for count) AND binary-grep of `dinput8_dalaya_reference.dll` for LE-pattern `CC 7C A6 00` → exactly 1 hit at file offset `0x27D6` (the `INITIALIZE_EQGAME_OFFSET` init code)
- CSINFO stride 0x160, name at offset 0 of each entry (verified via `FUN_0042F240` = `CCharacterListWnd::FindCharacterByName`)
- Field order `{Count: +0, Data: +4, Alloc: +8}` (matches v3.22.34's struct fix)

Memory: `reference_dalaya_path_a_bridge_deref_bug.md` (new), `reference_dalaya_path_a_unreachable_at_charselect.md` (marked SUPERSEDED with per-row table corrections).

### Also fixed

- **`tools/probe-charselect-offset.py`** — pre-v3.22.35 the script defaulted to `--offset 0x38E70` (the Data pointer field, not the array header start) and unpacked 12 bytes as `(Data, Count, Alloc)`. With offset 0x38E70 those 12 bytes are actually `(Data, Alloc, IsValid)` — "Count" was misprinted as Alloc, which self-cancelled only when `Count == Alloc` (the common N==max-slots case) and broke for 1-char characters. Changed to read 16 bytes at canonical offset `0x38E6C` and unpack as `(Count, Data, Alloc, IsValid)`. Slot walk now caps at `min(Count, Alloc, 12)` instead of `min(Count, 12)` (defensive against junk Count). Docstring rewritten to document the correct ArrayClassHeader layout.
- **Stale `OFFSET_CHARSELECT_ARRAY = 0x18EC0` comments** at the two Path A read sites (PopulateCharacterData header + Poll header) updated to the post-v3.22.34 canonical 0x38E6C. The pre-fix comments still pointed at the x64-tree value v3.22.34 had already corrected.
- **`ArrayClass on x86 = {T* Data, int Count, int Alloc}`** comment near the WndMgr iteration code corrected to `{int Count, T* Data, int Alloc}` — the struct definition at line 595 was always the canonical order; this comment lied. The code in `TryWndMgrPointer` already read `arr->Count` then `arr->Data` correctly via the struct.

### Verification

The static evidence chain is rock-solid (Ghidra + binary-grep + MQ2 source patterns + verified-parallel two-deref usage at 6 sibling sites). Runtime paired-probe still recommended before declaring victory:
1. Launch via team1 hotkey, get a client to char-select
2. Tail per-PID `eqswitch-dinput8-{pid}.log` — first Path A poll should report `Count=N` (matching account's char count) instead of `Count=0`
3. Run `tools/probe-charselect-offset.py --pid <PID>` and confirm it agrees with the bridge log
4. Path C heap-scan invocations should drop to zero per char-select session (Path C is the fallback; Path A firing means fallback never runs)

If Path A fires successfully, char-select wall-clock should be unchanged (Path C was fast enough — Dalaya login-server delay dominates) but the bridge log gets cleaner and future MQ2-style features (level/class/race reads) become trivially available.

## v3.22.34 — Path A WORKING on Dalaya: correct OFFSET_CHARSELECT_ARRAY + fixed ArrayClassHeader field order (2026-05-23)

The two-bug-deep root cause for "Path A unavailable on Dalaya" — finally pinned with a live `ReadProcessMemory` probe against the in-game `gotquiz` (10-char) + `gotquiz1` (1-char) clients. Both bugs were active in every v3.22.x ship; they masked each other so the failure mode presented as `Count=0` rather than crash/garbage, which is why "confirmed plug-and-play on Dalaya by Nate" comments lingered.

### Bug 1 — wrong tree's offset (`0x18EC0` from x64 modern build)

`Native/mq2_bridge.cpp` had `OFFSET_CHARSELECT_ARRAY = 0x18EC0`. That value is from `macroquest-rof2-emu/src/eqlib/include/eqlib/game/EverQuest.h:963` — the **x64 modern EQ build's** layout. The CLAUDE.md "TWO trees exist with different roles" callout warns specifically against copying numerics from this tree because its addresses target a different bitness AND a different struct layout. On Dalaya (x86 RoF2-Test-based) the `EVERQUEST` struct field at `0x18EC0` holds zeros — confirmed by live probe.

### Bug 2 — wrong ArrayClassHeader field order

The existing `struct ArrayClassHeader { uint8_t *Data; int Count; int Alloc; }` does NOT match MQ2's actual `CDynamicArrayBase + ArrayClass_RO<T>` layout from `mq2emu-rof2-x86/MQ2Main/ArrayClass.h:43-355`:

```cpp
class CDynamicArrayBase { /*+0x00*/ int m_length; };
class ArrayClass_RO<T> : public CDynamicArrayBase {
    /*+0x04*/ T* m_array;
    /*+0x08*/ int m_alloc;
    /*+0x0c*/ bool m_isValid;
};
```

So `m_length` (the Count we care about) sits at `+0x00`, **before** `m_array`. The pre-fix code was reading `m_length` as `Data*` and `m_array` as `Count` — meaningless even if the offset had been right. Combined with Bug 1, this would have crashed loudly if both happened to align; the "both wrong" combination produced quiet zeros instead because `0x18EC0` happens to be a zero-filled region in Dalaya's `EVERQUEST` struct.

### Fix

- `OFFSET_CHARSELECT_ARRAY = 0x38E6C` (Dalaya-specific; canonical x86 RoF2-Test lists `0x38E80` for the same field, but Dalaya shifted the surrounding struct by `0x14` bytes).
- `struct ArrayClassHeader { int Count; uint8_t *Data; int Alloc; }` — reordered to match the actual base+derived layout.

### Verification

Live probe against two in-game clients (`tools/probe-charselect-offset.py`):

```
[PID 40300 gotquiz, 10 chars]    +0x38E6C: Count=10  +0x38E70: Data=0x12A5A900
  slot 0: "Acpots"   slot 1: "Backup"   slot 2: "Healpots"  slot 3: "Jonopua"
  slot 4: "Nate"     slot 5: "Potiongirl" slot 6: "Potionguy" slot 7: "Staxue"
  slot 8: "Thazguard" slot 9: "Zfree"
[PID 38608 gotquiz1, 1 char]     +0x38E6C: Count=1   +0x38E70: Data=0x06644AC8
  slot 0: "Natedogg"
```

Cross-verified against the same-session heap-scan log entries:
- gotquiz: `heap scan FOUND char array at 0x12A5A900` — exact address match.
- gotquiz1: anchor-scan branch found "Natedogg" via target-name search; live probe now shows Count=1, matching the anchor's single-char assumption.

The probe walks all 10 slot positions at stride `HEAP_SCAN_STRIDE = 0x160` (the existing constant), confirming Dalaya's `CSINFO` is `0x160` bytes (not the canonical RoF2-Test `0x170` — Dalaya stripped or shortened some fields).

### Behavioral impact

- **Path A now publishes `shm->charCount` and names directly from the struct** — no dependency on heap-scan timing, no dependency on Path B's `GetItemText` (which is empty on Dalaya), no dependency on Path B2's `SetCurSel`/`GetCurSel` probe (which is non-deterministic on Dalaya).
- The pump-responsiveness probe + Path C heap-scan + anchor fallback all stay in place as **defense in depth**. Path A success short-circuits the entire pCharList block via the existing `charDataRead = true` latch.
- Expected log diff vs v3.22.33: `charSelectPlayerArray unavailable` line disappears for both clients; `heap scan FOUND` / `slot-based mode probed N slots` / `anchor scan FOUND` lines also disappear (Path B/B2/C don't fire); `charSelectPlayerArray at offset 0x38E6C: Count=N` reports the real count.

### Build

- `Native/build-di8-inject.sh` clean — DLL grows by 1KB for the new comment-heavy block, no functional bloat.
- `dotnet build -c Release` clean (no C# changes — just version bump).
- No tests added: the Native fix is below the SHM boundary; the probe script `tools/probe-charselect-offset.py` is the verification harness.

### Out of scope (carried forward)

- Minimize-restore hang + EQSwitch-close-crash reports — separate DI8/hook-detach investigation.
- `OnSwitchKey` / `OnGlobalSwitchKey` / `LaunchSequenceComplete` topmost-dance parity (v3.22.32 + v3.22.33 framing).
- `HEAP_SCAN_STRIDE = 0x160` Dalaya-specific stride is empirical (canonical RoF2-Test `CSINFO` is `0x170`). Not changing this — the existing value is verified to align Path C heap-scan correctly.

## v3.22.33 — Post-v3.22.32 verifier sweep: 7 fixes folded in (2026-05-23)

Same-day follow-on to v3.22.32. The post-ship 8-agent verifier swarm surfaced 1 CRITICAL, 1 sev-4, 2 HIGH, and 3 MEDIUM findings against v3.22.32. All folded in here. The original two-bug scope (taskbar coverage + background char-select stall) is unchanged; this ship hardens the v3.22.32 fixes against the gaps verifier-T2/T3/T4 caught.

### Item 1 — Pump probe HOISTED above the first-poll diagnostic GetCurSel (T4 Opus sev-4)

The v3.22.32 probe was placed BELOW the `if (!g_uiFallbackLogged)` block — which meant the first-poll diagnostic `g_fnGetCurSel(pCharList)` (the exact call site whose `Character_List GetCurSel = 0` log was the smoking-gun last line of PID 4628's 44-second silence) ran BEFORE the probe could gate it. The fix protected Path B/B2 but NOT the original failure mode. v3.22.33 hoists the probe to be the first thing inside `if (pCharList)`, gates the entire `!g_uiFallbackLogged` block on `pumpResponsive`, and adds a parallel `pumpResponsive && !g_uiFallbackLogged` else branch that latches `g_uiFallbackLogged = true` even when the diagnostic was deferred (so Path C's widened gate still fires this poll).

### Item 2 — `g_cachedSlotCount` invalidation in Path C anchor fallback (T2 Sonnet+Opus convergent CRITICAL)

The exact 2026-05-23 gotquiz failure mode. When the anchor-only fallback fires (heap-scan budget timed out, single-char-anchor path found the target via `HeapScanForTargetName`), Path B2's cached slot count was untouched. Next poll: cached path repopulates `count = g_cachedSlotCount` (e.g. 10 for gotquiz). P9 iterates slots 0-9; slots 1-9 were zeroed by the anchor branch; `IsPlausibleName` fails; allPlausible=false; publisher bails every poll → 30s SM timeout. Fix: inside the anchor branch, after `g_anchorScanCached = true`, invalidate `g_cachedSlotCount = -1` when it was > 1 (single-char accounts already match the anchor's slot-0-only state, so they're unaffected). The next poll's Path B2 cached path then skips, and the cache is rebuilt on a fresh probe.

### Item 3 — `g_heapScanCount` reset gap closed at three more sites (T2 Sonnet+Opus Gap 3 / Gap 2)

v3.22.32 added the companion `g_heapScanCount = 0` reset alongside six of the eight existing `g_heapScanArrayBase = 0` / `g_heapScanDone = false` reset sites, but missed three: the P9 partial-pop cache invalidation (~`mq2_bridge.cpp:4612`), the standalone heap-scan SEH (`~:4683`), and both branches of the standalone re-read path (`~:4768`, `~:4779`). Without the resets, the cached count could outlive the array base it pointed at, leading to a stale `count > 0` read against a now-zeroed `g_heapScanArrayBase` — caught by the re-read branch's `g_heapScanArrayBase != 0` gate today but a footgun for the next refactor. The "lifecycle mirrors `g_heapScanArrayBase`" invariant claimed in the global's comment is now actually true at every reset.

### Item 4 — `g_lastPumpProbeWarnMs` promoted to file scope + reset on charselect transition + gs=5 (T2 Sonnet+Opus Gap 3 MEDIUM)

v3.22.32 declared the 5-sec rate-limiter as a `static volatile DWORD` inside the probe block — lexically scoped, never reset. If a pump-non-responsive event fired close to a charselect transition, the new cycle's first pump-non-responsive log line was suppressed for up to 5 s. v3.22.33 promotes it to module scope (alongside `g_heapScanCount`) and resets it at both the `FindWindowByName` charselect-transition block and the `Poll()` gs=5 in-game-transition block. Diagnostic-visibility claim in the v3.22.32 changelog now actually holds for back-to-back cycles.

### Item 5 — `RaiseClientsAboveTaskbar` adds `IsClientResponsive(100ms)` gate per client (T4 Opus sev-3)

The cross-process `SetWindowPos` with z-order change dispatches `WM_WINDOWPOSCHANGING` synchronously to the target — the exact failure surface that motivated the v3.22.22-era `IsClientResponsive` helper. v3.22.32 gated on `IsHungAppWindow` (5-second kernel threshold), missing the same tighter-window gap every other z-order primitive in the codebase covers. v3.22.33 adds the 100 ms probe per client; non-responsive clients are skipped with a Warn log and counted in the summary (`skippedUnresponsive=N`).

### Item 6 — `RaiseClientsAboveTaskbar` iterates `clients` for the next foregroundable candidate (T3 Sonnet MEDIUM Issue 4)

v3.22.32 abandoned foreground entirely when the active EQ client was iconic, hung, autologin-active, or unresponsive — even when other clients in the arranged set were viable. In the topology "minimized foreground EQ + non-minimized background EQ" the taskbar would re-raise above the surviving non-iconic client because foreground was never set. v3.22.33 extracts `CanForegroundCandidate` (IsWindow + !IsHungAppWindow + !IsIconic + !IsLoginActive + IsClientResponsive) and falls through to the first viable candidate in `clients` when active fails the predicate. Active still wins when responsive — it's the user's actual focus.

### Item 7 — `TrayManager._disposed` guard for the ClientLost 250ms recovery timer (T2 Opus Gap 5 MEDIUM)

The 250 ms recovery timer is created ad-hoc inside the `ClientLost` handler and not tracked in `Dispose()`'s teardown list. If `TrayManager.Dispose()` ran between `ClientLost` firing and the 250 ms tick, the queued tick would read `_processManager` / `_windowManager` post-dispose → NRE → swallowed by the surrounding catch as a misleading "post-close taskbar-recovery failed" Error log line. v3.22.33 adds `private volatile bool _disposed`, sets it first thing in `Dispose()`, and short-circuits the recovery timer tick AND `RaiseRemainingClientsAboveTaskbar` on the flag.

### Item 8 — Pump probe `smErr == 0` log differentiation (T3 Sonnet+Opus MEDIUM)

When `SendMessageTimeoutA` returns 0 and `GetLastError()` also returns 0 (documented edge case: GetLastError was not set), v3.22.32 conflated this with `ERROR_TIMEOUT` in the LOUD log. Pumping `pumpResponsive = false` is the right conservative behavior either way, but the log line said "err=0" with no explanation. v3.22.33 differentiates the log message: `"timeout (1460)"` vs `"unknown (GetLastError not set, treating conservatively)"`. Pure diagnostic clarity, no behavior change.

### Build

- `dotnet build -c Release` clean (0/0).
- `dotnet build -c Debug` clean (0/0).
- `Native/build-di8-inject.sh` clean — same .dll size class (~245 KB).
- All seven fixes are surgical and address verifier-flagged gaps; no scope creep beyond the post-v3.22.32 finding list.

### Out of scope (carried forward from v3.22.32, restated for visibility)

- **Multi-character accounts with slow-loading slots still fail char-select** when `HeapScanForCharArray` times out within its 1500 ms budget AND the heap state doesn't yet have 5 contiguous IsPlausibleName + race-byte-valid entries. The pump probe + Path C widening + anchor invalidation in v3.22.33 close the failure cascade, but the underlying "heap scan can't find the array" reliability issue requires a Dalaya-specific Path A offset (`charSelectPlayerArray`) derived via Ghidra. **Tracked for next ship via the Ghidra session.**
- Minimized EQ window restore hangs + EQSwitch-close-crash reports — separate DI8/hook-detach investigation, not blended into this fix.
- `OnSwitchKey` / `OnGlobalSwitchKey` / `LaunchSequenceComplete` ArrangeWindows callsites still don't run the topmost dance (T2 Opus + T4 Sonnet+Opus MEDIUM/notable). Out of scope per v3.22.32; revisit when smoke evidence demands it.

### Note on v3.22.32

Bug B (taskbar coverage after sibling close) was user-confirmed working in the 2026-05-23 smoke ("the remaining covers taskbar so thats a win"). Bug A was partial: the pump-probe + Path C widening helped single-char accounts but multi-char failed via the Gap 1 CRITICAL path Item 2 above closes. v3.22.33 is the patch that makes the v3.22.32 narrative actually hold for the multi-char case the smoke surfaced.

## v3.22.32 — Background char-select stall fix + Fix Window taskbar z-order recovery (2026-05-23)

Two user-reported regressions from the 2026-05-22 v3.22.31 smoke. Diagnosed from the per-PID `eqswitch-dinput8-{pid}.log` files plus the main C# log; smoke transcript at `Logs/eqswitch.log` 21:07:21–21:10:20.

### Item 1 — Background client char-select stall (Native: `mq2_bridge.cpp`)

**Symptom.** PID 4628 (background client, gotquiz1) reached charselect at C# `t=33s` (`WaitConnectResponse → CharSelect`) but never got past it. 30 seconds later the SM aborted with `MQ2 bridge didn't populate char list after 30000ms — stopping at char select to avoid wrong-character enter-world`. PID 7560 (foreground, gotquiz) succeeded at the equivalent moment.

**Root cause.** Path A (`CEverQuest::charSelectPlayerArray`) is permanently unavailable on Dalaya — the vtable delta at line `mq2_bridge: NOTE — CCharacterSelect vtable 0x00C95410 (expected 0x00B05410, delta=+1638400)` reports the same gap in both processes. With Path A down, the bridge depends on Path B (CListWnd `GetItemText` column probe) → Path B2 (`SetCurSel`/`GetCurSel` slot probe) → Path C (heap scan + IsPlausibleName) to publish `shm->charCount`. Path B2 hung on the background client: native log's last `mq2_bridge` line was `Character_List GetCurSel = 0` at native tick `14551031`, then silence for the remaining 44s until the user closed the process. The foreground client's log at the equivalent point shows `UI fallback: slot-based mode — probed 10 slots (curSel=1)` followed immediately by the heap scan that finds real names.

The probe runs MQ2's `SetCurSel(pCharList, i)` for `i = 0..9`. On the foreground client, the CListWnd's internal cursor field flips fast and `GetCurSel` returns immediately. On the background client, EQ's pump is non-responsive (the C# `ApplySlimTitlebar` probe in the same time window reports `SendMessageTimeout WM_NULL > 100ms — lastErr=1460 (ERROR_TIMEOUT)`), and one of those calls blocks indefinitely. `PollReentryGuard` then prevents any subsequent poll from starting, so the rest of `MQ2Bridge::Poll` (including the heap scan that's memory-only and doesn't need the pump) never runs.

**Fix.** Decouple Path C (heap scan + publish) from Path B/B2's `count > 0` gate.

- Path C gate widens from `if (count > 0 && !g_heapScanDone)` to `if ((count > 0 || g_uiFallbackLogged) && !g_heapScanDone)`. `g_uiFallbackLogged` is set this same poll once Path B has located `pCharList`, so the new condition is a strict superset.
- Inside Path C, the per-slot read loop now scans up to `CHARSEL_MAX_CHARS` slots when no incoming count was supplied (the `validCount >= 5` invariant inside `HeapScanForCharArray` already guarantees the array is real). Highest valid slot index + 1 becomes the derived count.
- Derived count is cached in a new file-scope `g_heapScanCount`. The local `count` variable inside `MQ2Bridge::Poll` now seeds from this cache at the top of the Path B block, so every subsequent poll's re-read + P9 publisher fires without needing Path B2 to publish again.
- `g_heapScanCount` is reset on the same lifecycle boundaries as `g_heapScanArrayBase`: the `pinstCCharacterSelect != lastObserved` charselect-transition block (~`mq2_bridge.cpp:3038`), the `gameState == 5` in-game transition (~`:3848`), and the SEH/stale-cache invalidation paths inside Path C.

Heap scan is memory-only (VirtualQuery + IsPlausibleName + race-byte sanity check) — it doesn't depend on the EQ pump being responsive, so background clients now make forward progress through char-select even when their pump is hung on background DX state. Path B/B2 stays in the code unchanged; on foreground clients it still runs first and the seed pathway becomes idempotent on subsequent polls.

### Item 2 — Fix Window doesn't restore taskbar coverage after sibling close (C#: `TrayManager.cs`, `NativeMethods.cs`)

**Symptom.** Two-client team1 smoke. Foreground client succeeded, covered the taskbar (slim-titlebar mode). User closed the failed background client. Main client window stopped covering the taskbar — Fix Window button did not restore coverage. The C# log shows Fix Window DID size the window correctly: `ApplySlimTitlebar: hwnd=1378660 → (0,-13) 1920x1093, offset=13px hidden`. So the bounds are right; the taskbar is just visually slicing through.

**Root cause.** Z-order, not bounds. The Windows taskbar is `WS_EX_TOPMOST` by default. EQ slim-titlebar mode sizes the window to span the taskbar's rect (`monitor.Top - offset` to `monitor.Bottom`) — taskbar yields automatically *only when EQ is foreground AND covering its area*. When the sibling client closed, the OS raised the taskbar's z-band back above the surviving non-topmost EQ window as part of its post-close shuffle. The slim-titlebar guard timer + the hook DLL only re-issue `SetWindowPos` with `SWP_NOZORDER`, so neither path could undo the OS-side z-shift. `WindowManager.ApplySlimTitlebar` itself uses `SWP_NOZORDER` (lines 730, 748 — deliberately, to avoid focus theft from background callers like the guard timer).

**Fix.** Two new entry points in `TrayManager.cs` that own the explicit-user-action z-order recovery:

- `RaiseClientsAboveTaskbar(clients, foregroundActive)` — runs the topmost dance per client (two `SetWindowPos` calls: `HWND_TOPMOST` then `HWND_NOTOPMOST`, both with `SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE`). The dance pushes each window above the taskbar's z-band without leaving it permanently always-on-top. When `foregroundActive=true` it then calls `_windowManager.SwitchToClient(active)` (which uses the existing `ForceForegroundWindow` workaround) on the foreground client — load-bearing because Windows only auto-hides the taskbar for a *foreground* full-monitor window.
- `RaiseRemainingClientsAboveTaskbar()` — invoked from a 250ms delayed timer in the `ClientLost` handler (gated on `_config.Layout.SlimTitlebar && _processManager.Clients.Count > 0`). Re-applies slim positioning on each responsive non-autologin client (because the hook DLL never re-asserts after a sibling close — there's no EQ-initiated `SetWindowPos` to intercept), refreshes hook config, then runs `RaiseClientsAboveTaskbar`. The 250ms delay lets the OS finish its post-close z-shuffle first.

`OnArrangeWindows` now also calls `RaiseClientsAboveTaskbar` after `ArrangeWindows` + `UpdateHookConfig`, gated on `_config.Layout.SlimTitlebar` (non-slim wants the taskbar visible, so the dance would be wrong). The `HWND_TOPMOST`/`HWND_NOTOPMOST` sentinels added to `NativeMethods.cs` are typed `IntPtr` because `SetWindowPos`'s `hWndInsertAfter` parameter is `IntPtr` in our P/Invoke signature; using readonly `IntPtr` with negative values matches the Win32 convention (`(HWND)-1`/`(HWND)-2`).

Filter discipline mirrors `OnArrangeWindows`: hung, autologin-active, and pump-unresponsive clients are skipped. Each `SetWindowPos` pair logs partial failures (`up=true down=false`) so a wedged dance is visible in the log rather than silent.

### Pre-ship verifier round (8 agents, T1/T2/T3/T4 × Sonnet+Opus)

Verifier swarm caught three convergent findings that the initial fix missed; all three folded into this ship before tag:

- **T2 Sonnet + T2 Opus CRITICAL — Path C source-order**: the v3.22.32 widened Path C gate (`g_uiFallbackLogged || count > 0`) was inert against the actual repro path because Path B's column-discovery loop (`ReadListItemText` → MQ2's `CListWnd::GetItemText` → CXStr allocation from EQ's CRT heap) AND Path B2's `SetCurSel` probe both run BEFORE Path C in source order. On a non-responsive background pump, EQ's main thread holds the heap lock indefinitely; our `ActivateThread` blocks inside `HeapAlloc` or `SendMessage`, `PollReentryGuard` prevents subsequent polls from entering, and Path C never executes. **Fix:** added a `SendMessageTimeoutA(eqWnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 100, &smResult)` probe at the top of the `if (pCharList)` block. When `pumpResponsive=false`, Path B's column-discovery loop AND Path B2 are skipped (`if (pumpResponsive && ...)` gates added on both); execution falls through to Path C which is pure VirtualQuery + IsPlausibleName memory operations with no EQ heap/pump dependency. The 5-sec-rate-limited LOUD log surfaces the pump-non-responsive state instead of silent fallthrough to the 30s SM timeout.

- **T3 Sonnet + T3 Opus CRITICAL — `RaiseClientsAboveTaskbar` missing `IsIconic` gate**: `_windowManager.SwitchToClient(active)` calls `ShowWindow(SW_RESTORE)` unconditionally (`Core/WindowManager.cs:44`). Restoring a minimized EQ window while its DX device is in windowed-fullscreen state crashes the device — `ForceDxReinit` already has this exact gate with a documented warning. Without the check, the new recovery path would have a DX crash hazard worse than the original bug. **Fix:** added `!NativeMethods.IsIconic(active.WindowHandle)` to the gating predicate, and a `FileLogger.Warn` on the iconic branch matching `ForceDxReinit`'s log line.

- **T2 Opus + T3 Sonnet HIGH — ClientLost focus theft on non-EQ foreground**: `RaiseRemainingClientsAboveTaskbar` was passing `foregroundActive: true` unconditionally, which would yank focus to a surviving EQ window if a background client closed while the user was in Discord / a browser / any non-EQ app. **Fix:** the ClientLost timer-tick path now checks `_processManager.GetActiveClient() != null` — i.e., does EQ already own the foreground? — and passes that boolean to `foregroundActive`. The Fix Window button path still passes `true` unconditionally (the user's explicit press is the consent signal).

Also folded in (lower-severity, single-agent flags):

- **T3 Sonnet HIGH — sparse-slot diagnostic**: added a `DI8Log` warn when the heap-scan-derived count via `highestValidIdx + 1` diverges from `plausibleEntries`. EQ char slots are contiguous in practice (deleted slots collapse), so this should never fire — but if it ever does, the P9 publisher will bail forever (intervening empty slots fail IsPlausibleName), and surfacing the layout shape early beats a silent 30s SM timeout.
- **T4 Sonnet + T4 Opus — `g_heapScanCount` missing from `MQ2Bridge::Init()` reset block**: added the companion reset next to the existing `g_heapScanArrayBase = 0` / `g_heapScanDone = false` resets at ~line 4853. Mid-process MQ2 re-init now zeroes the new cache, matching the "lifecycle mirrors `g_heapScanArrayBase`" claim in the global's comment.

### Out of scope (noted for follow-up)

User-reported but deferred to a separate ship: (a) minimized EQ window hangs on restore, (b) EQ client crashes when EQSwitch process exits. Both involve the DI8/hook DLL detach + EQ's pump state and need their own diagnosis pass — not blended into this fix.

Verifier-flagged but deferred: `OnSwitchKey` / `OnGlobalSwitchKey` / `LaunchSequenceComplete` ArrangeWindows callsites don't run the topmost dance (T2 Opus + T4 Sonnet+Opus MEDIUM/notable). The user-reported bug B is specifically the Fix Window + sibling-close pair; expanding scope to every arrange callsite is a separate consistency pass.

### Build

- `dotnet build -c Release` clean (0/0).
- `dotnet build -c Debug` clean (0/0).
- `Native/build-di8-inject.sh` clean — `g_heapScanCount` is a single `static volatile int`, no struct layout change to SHM. The new pump probe adds a `SendMessageTimeoutA` import that's already pulled in via `windows.h`; `device_proxy.h` include added for `GetEqHwnd()`.
- No tests added: the Native fix lives below the SHM boundary (no C# call site to mock) and the C# z-order helpers exercise live Win32 APIs (`SetWindowPos` to `HWND_TOPMOST`) that the unit-test seam doesn't model. Smoke verification (real Dalaya team1 launch) is the load-bearing check — same posture as the v3.22.30 self-update polish ship.

## v3.22.31 — IsClientResponsive → IWindowsApi DI seam + P9 SEH index attribution (2026-05-22)

Same-day follow-on to v3.22.30. Two small, contained improvements: P2 restores the testability seam for the pump-responsiveness probe (private static → `IWindowsApi` method), and P3c sharpens the P9 SEH log attribution so a fault at `shm->names[7]` no longer reports `shm->names[0]`. Both are behavior-equivalent / telemetry-only; no user-visible semantic change.

### Item 1 — `IsClientResponsive` extracted into `IWindowsApi`

`WindowManager.IsClientResponsive(IntPtr hwnd, out int lastErr)` lived as a `private static` since v3.22.22 round-5. It wrapped `NativeMethods.SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG|SMTO_BLOCK, 100ms)` to fast-fail on a non-pumping target before the cross-process `SetWindowPos` / `SetWindowLongPtr` calls that crashed PID 24672 in the 2026-05-20 14.5s pass-1 stall. v3.22.29's Orphan-2 added the `out int lastErr` form for timeout-vs-window-gone-vs-access-denied disambiguation.

The probe was correct but couldn't be mocked — calls bypassed `_api`, so unit tests had to invoke the real `SendMessageTimeout` against a real HWND. v3.22.31 promotes the method to `IWindowsApi.IsClientResponsive`; production impl lives in `WindowsApi.cs`. The 5 caller sites in `WindowManager.cs` (`ArrangeSingleScreen`, `ArrangeMultiMonitor`, `SwapWindows`, `ResizeToCurrentMonitors`, `ApplySlimTitlebar`) all route through `_api.IsClientResponsive(...)` now. The dead no-`out` overload at the old `WindowManager.cs:34` (zero callers) was deleted rather than migrated.

Closes the v3.22.23 backlog "IsClientResponsive bypasses IWindowsApi DI" item (R5 T3 Sonnet+Opus convergent MEDIUM). Both Release and Debug builds clean (0/0) after the swap. Test seam is restored; no mocks consume it yet — that's intentional for this ship.

### Item 2 — P9 `__try` index hoist for SEH log attribution (`Native/mq2_bridge.cpp` ~L4382)

The Path B/C combined publisher's plausibility loop runs inside `__try` to catch DLL-detach / stale-SHM faults inside `IsPlausibleName((const uint8_t *)shm->names[i])`. Pre-v3.22.31 the `__except` log hardcoded `shm->names[0]=%p` — misleading when the fault was at `names[7]` (the realistic case in dual-box smokes where Path A populates the first slots before B/C race in).

Fix: hoist `int i = 0;` above the `__try` so `__except` can format `idx=%d` for attribution. Loop becomes `for (i = 0; ...)` (declaration moved up). No `volatile` added on `i` — matches the existing `firstBadIdx` pattern (also non-volatile, read after `__try`); MSVC `/EHa` is conservative across SEH boundaries in practice, and diagnostic-attribution-quality is the right bar.

**Convergent CRITICAL caught by 8-agent verifier round** (T2 Sonnet+Opus, T3 Sonnet+Opus): the original P3c log line included `(const void *)shm->names[i]` as a `%p` arg. Verifiers flagged a recursive-SEH footgun — if the original fault came from `shm` itself being unmapped (the exact scenario the comment names), recomputing `shm->names[i]` in `__except` could re-fault and tear the process down. Technically `(const void *)shm->names[i]` is pointer arithmetic for `char[N][M]` array members (no memory load happens for the expression itself), so the verifier reasoning was slightly off — but the pragmatic move is simpler: drop the `%p` arg entirely. The `idx=%d` is the load-bearing diagnostic; the pointer was duplicative sanity-check. Zero information loss, footgun defused.

### Build

- `dotnet build -c Release` clean (0 warnings / 0 errors).
- `dotnet build -c Debug` clean (0/0). `Core/*Tests.cs` don't reference `IWindowsApi.IsClientResponsive`, so existing test contracts are untouched.
- `Native/build-di8-inject.sh` → 244736-byte DLL (size identical to v3.22.30; only the SEH log path differs). Pre-existing C5051 warning in `eqmain_widgets.cpp:259` is unrelated.

### Notes flagged by verifiers for the next ship

- **Inline duplicate of the probe.** T4 (blast-radius) flagged `UI/TrayManager.cs:1670` — the `PopulateForceKillMenu` hung-detection probe still calls `NativeMethods.SendMessageTimeout` inline. The divergence (no `SMTO_BLOCK`) is deliberate, but a future `IsClientResponsiveNoBlock` interface variant would let it route through `_api` for consistency.
- **`lastErr` doc-comment gap.** `IWindowsApi.cs:32-36` advertises three error codes (0/1400/5). `ERROR_TIMEOUT (1460)` is also reachable and gets the same generic warn-log path. Pre-existing diagnostic looseness; not introduced by this ship.
- **Other `__try` blocks in `mq2_bridge.cpp`** (~95 of them) iterating `shm->names[i]` haven't been audited for the same hardcoded-`names[0]` log pattern P3c fixed. If a future ship touches the P9 path, sweep neighboring SEH log lines for the same issue.

## v3.22.30 — Verifier-driven self-update polish: atomic multi-file swap, hash filename pinning, drop .ok orphan-sweep cycle, broaden torn-state recovery gate (2026-05-22)

Same-day follow-on to v3.22.29. Four small, contained fixes that close the last items from the v3.22.27 → v3.22.29 cascade plus one item the v3.22.30 pre-ship verifier round found in the new code. After this, the released GitHub artifact and `main` are back in sync and the v3.22.29 CHANGELOG framing of "after this, the cross-app delta is zero" actually holds.

### Item 1 — Atomic two-phase swap in `UpdateDialog.OnActionClick`

Pre-v3.22.30 the swap loop interleaved `localPath → .old` and `.new → localPath` per-file in a single iteration. An IOException on file 2 (e.g., antivirus transient lock on the freshly-moved-in `.new`) AFTER file 1's swap fully landed left the install in a mismatched-version state: file 1 = new content, file 2 = old content, file 3 untouched. The v3.22.29 `.ok` sentinel + torn-state recovery only catches "exe is missing" (CleanupUpdateArtifacts triggers when `!File.Exists(exePath)`); it cannot detect a successful-but-mismatched swap.

v3.22.30 splits the swap into two strict phases:

- **Phase A (stage).** For every file with a pending `.new`, move `localPath → .old`. Track each successful move. If any move throws, unwind the successful stages (`oldPath → localPath`) and re-throw — no `.new` content has been committed yet, so old state is fully recoverable.
- **Phase B (commit).** For every staged file, move `.new → localPath`. Track each successful commit. If any move throws, unwind Phase B commits first (`localPath → newPath` to put the staged binary back), then unwind Phase A stages (`oldPath → localPath` to restore old content). Each rollback move is wrapped in its own try-catch — rollback IO failures surface via `FileLogger.Warn` rather than crashing the dialog.

The `.ok` sentinel pre-deletion moves from pre-swap to post-Phase-B so a mid-swap abort leaves the old `.ok` sentinels in place (matching the rolled-back-on-disk old binary). Phase B in practice almost never fails — at that point both source and dest are the same directory, dest doesn't exist, and `File.Move` is a metadata rename — but the symmetry makes the guarantee unconditional rather than "almost-always-true."

### Item 2 — Pin `ParseHashForZipBundle` filename to `_remoteVersion`

The hash parser previously accepted any line matching `EQSwitch-*.zip` and returned the first match. A SHA256SUMS body with multiple `EQSwitch-*.zip` entries (extra line targeting any EQSwitch zip name) could shadow the real release's hash regardless of which version it described. `release.yml` emits exactly one entry per release so the gap is theoretical, but the contract is now explicit at parse time instead of implicit in the emission convention.

Parser signature changed: `ParseHashForZipBundle(string? content)` → `ParseHashForZipBundle(string? content, string? expectedVersion)`. The expected filename is computed as `EQSwitch-{expectedVersion}.zip` and matched with `Equals(..., OrdinalIgnoreCase)`. Null/empty `expectedVersion` returns null (fail-closed). Caller in `OnActionClick` now passes `_remoteVersion` (already pulled from `tag_name` upstream in `CheckForUpdateAsync`).

### Item 3 — Drop the `.ok` orphan-sweep loop in `Program.CleanupUpdateArtifacts`

Pre-v3.22.30 the cleanup ran a "belt+suspenders" pass after the `.old`/`.new`/`update.zip` cleanup: for each updateable, if a `.ok` sentinel existed but no matching `.old` did, delete the `.ok`. This caused a one-launch-delay cycle — every launch where no update was pending deleted the existing `.ok` files, then `WriteStartupSentinel`'s 5s timer rewrote them. Harmless but wasteful.

The "belt+suspenders" framing was wrong on inspection: `OnActionClick`'s swap step already deletes `.ok` pre-swap (now post-Phase-B per Item 1), so an update can never see a stranded `.ok` pre-empt its own freshness check. A `.ok` sentinel surviving across launches is harmless — its only consumer is the `.old` cleanup loop, which pairs each `.ok` against the matching `.old` at the same moment in time. The stale-timestamp concern is also moot: presence-of-`.ok` is the safety property, not its `UtcNow` stamp.

Sweep loop removed. The `.old`/`.new`/`update.zip` cleanup above it is unchanged.

### Item 4 — Broaden torn-state recovery gate in `Program.CleanupUpdateArtifacts`

Pre-v3.22.30 (and v3.22.29) the torn-state recovery branch in `CleanupUpdateArtifacts` was gated on `if (!File.Exists(exePath))` — only ran when EQSwitch.exe was specifically the missing file. The two-phase swap from Item 1 makes a new hard-crash failure mode reachable: Phase A stages all three files to `.old`, Phase B commits file 1 (EQSwitch.exe), then a power loss / BSOD hits before Phase B commits files 2 and 3. On reboot the exe is present (new content), but hook.dll and di8.dll are missing — Phase A moved them to `.old`, Phase B never started for them.

Pre-fix, the recovery branch saw exe present and skipped. The `.ok`-gated `.old` cleanup loop then ran. Pre-existing `.ok` sentinels from the previous good launch were still present (never deleted because Phase B's post-swap `.ok`-clear never ran), so the cleanup loop saw sentinel + `.old` and *deleted* `hook.dll.old` and `di8.dll.old`. `alwaysCleanup` then deleted `hook.dll.new` and `di8.dll.new`. Final state: new EQSwitch.exe, no DLLs, no recovery sources — bricked install requiring manual re-download from GitHub.

This failure mode pre-existed v3.22.30 (the original single-loop swap has the same window between `localPath → .old` and `.new → localPath`), but the two-phase split widens it because Phase A moves all three files to `.old` before Phase B starts any of them. The v3.22.30 verifier round caught it.

Fix: the gate now detects torn state by scanning all three updateables — torn state is *any* updateable missing while its `.old` sibling exists. When torn state is detected, missing files are restored from their `.old` siblings as before. Files that are present (e.g., the committed new exe in the scenario above) are left in place; `.new` artifacts are preserved (the function `return`s before `alwaysCleanup`). The user ends up with a runnable install — for v3.22.30 specifically this is a clean state because the DLLs were unchanged between v3.22.29 and v3.22.30 (mismatched-version is ABI-identical for this ship). If a future ship changes DLL ABIs and trips this same recovery path, the user lands on a v3.22.29-shape install with the v3.22.30 `.new` files preserved on disk for manual completion or re-trigger of the updater.

The narrower atomic-rollback variant ("move committed-new files aside to `.new` and restore all three from `.old`") was considered and rejected — it adds code complexity and File.Move risk for a strictly better outcome only when DLL ABIs actually shift between successive ships. The minimal fix turns "bricked / unrunnable" into "runnable, possibly mismatched", which is the clear win.

### Build

- `dotnet build -c Release` clean (0 warnings, 0 errors).
- No new tests added — `ParseHashForZipBundle` test coverage remains in the "accepted gap" tier per the same threat-model rationale documented in the v3.22.29 entry. The two-phase swap is structurally simple enough that line-by-line review is the right verification method, not test-infra investment.

### Why this isn't v3.22.29+1's worth of feature work

Cross-app verifier comparisons against MWBToggle drove v3.22.27 → v3.22.29. The three items here came from a post-tag final round on v3.22.29's `UpdateDialog.cs` rewrite — same comparison method, just one cycle later. After this ship, EQSwitch's released artifact equals `main` for the first time since v3.22.27. Cross-app parity-driven shipping closes here; further work should be weighed against EQSwitch's actual threat model.

## v3.22.29 — Full UpdateDialog hardening parity with MWBToggle + lock-symmetry orphan sweep (2026-05-22)

Closes every item carried over from the v3.22.27 → v3.22.28 verifier cascade.

### Self-update hardening (UpdateDialog.cs rewrite)

Cross-app parity port from `MousewithoutBordersToggle/UpdateDialog.cs`. Single rewrite preserves all EQSwitch-specific behavior (zip bundle, multi-file extract, TestMode, DarkTheme styling) while layering the MWBToggle security envelope on top.

1. **`SendAllowlistedAsync` per-hop redirect validation.** `_http` flips to `AllowAutoRedirect=false`; redirects are followed manually with a 5-hop cap, and every hop's URL is re-validated through `IsAllowedHost`. Pre-v3.22.29 only the initial URL was checked because the default `HttpClientHandler` followed 3xx server-side. A hostile redirect from an allowlisted origin would now be rejected at the next hop. Catalogue, download, and SHA256SUMS fetches all route through this helper.

2. **`MaxDownloadBytes = 200 MB` cap.** Header-side gate fails closed when the server reports `Content-Length` over the ceiling; mid-stream byte counter aborts chunked-transfer bodies that have no `Content-Length`. Defends against a compromised CDN edge serving an infinite chunked body inside the 30s `HttpClient.Timeout` window.

3. **`MaxResponseContentBufferSize = 1 MB` ceiling on `HttpClient`.** Default is ~2 GB. Affects all `ReadAsStringAsync` calls (releases JSON, SHA256SUMS body). Without this, a hostile edge could pull arbitrary text into RAM.

4. **`ParseHashForZipBundle` + `IsValidSha256Hex` parser hardening.** Replaces the inline parser that accepted any whitespace-bounded token as the hash. New parser handles BSD-tag (`SHA256 (filename) = hash`) + GNU-coreutils + tab separators + CRLF + multi-entry files, and rejects anything that isn't exactly 64 hex chars. The downstream `OrdinalIgnoreCase` compare still catches a wrong-content acceptance, but the parser is the trust boundary and should fail-closed on malformed content.

5. **`TryDelete` / `TryRollback` logging.** Pre-v3.22.29 both wrappers had bare `catch { }`. Stranded `.new`/`.old` artifacts on locked files were invisible to support. Now log to `FileLogger.Warn` with exception type + message.

6. **`.ok` post-init sentinel + torn-state recovery.** After self-update, `.old` is no longer deleted unconditionally on next startup — `Program.CleanupUpdateArtifacts` now gates `.old` removal on the presence of a `.ok` sentinel that the NEW binary writes 5s after `Application.Run` starts. If the new binary crashes during init the sentinel is never written, `.old` persists, and the next-launch torn-state branch can restore it. Sentinels are deleted as part of the swap so a leftover from a prior update can't mark a freshly-swapped untested binary as already-OK.

7. **Torn-state recovery on next launch** (folded into item 6).

8. **`OnActionClick` async-void exception fence widened.** The outer `try` now wraps from the top of the method — pre-try state setup (button toggle, cts dispose, ProcessPath null-check, files-array construction) used to throw to the `SynchronizationContext` as unhandled.

9. **`TestMode` static setter gated behind `#if DEBUG`.** Release builds expose the getter (always `false`) but no setter, so a future code path can't accidentally flip the tray into "all-security-checks-bypassed" mode in shipped builds.

10. **`Process.Start` return value disposed.** The relaunch invocation now uses `using var _ = Process.Start(...)` so the `Process` handle is released; previously leaked because the return value was discarded.

11. **Catalogue fetch routed through `SendAllowlistedAsync`** (closed by item 1).

12. **(intentionally skipped — see below)** UpdateDialog test coverage port was scoped here but moved to accepted-gap. The hardening code itself is the canonical version of the well-tested MWBToggle pattern; EQSwitch's threat model doesn't justify the test-infra investment. Numbering preserved from the v3.22.28 handoff backlog for cross-reference.

13. **`BeginInvoke` (fire-and-forget) for download UI marshaling.** Replaces `Invoke` (blocking) at the download progress callback. At 81920-byte chunks per ~MB/s a 50 MB zip is ~640 synchronous cross-thread round-trips; `BeginInvoke` is structurally correct here since the call is a UI repaint, not a synchronization point.

14. **`GitHubRepo` const.** Replaces the two hardcoded `/itsnateai/eqswitch/` literals at the host-equality check sites with a single `const string GitHubRepo = "itsnateai/eqswitch"`. Fork-friendly single edit point.

### Orphans folded back in from v3.22.28 handoff (carryover, NOT MWBToggle parity)

These were scoped for v3.22.28 but only Item 3 (URL allowlist) actually shipped — re-captured here so they don't rot.

- **Orphan 1: UI-thread `_config.*` lock-symmetry sweep.** Snapshot-then-release pattern under `ConfigManager.ConfigMutationLock` applied to:
  - `UI/TrayManager.cs:FireAccountHotkeyByName` — three reads (`FindAccountByName`, `LegacyAccounts.FirstOrDefault`, `FindCharacterByName`) snapshotted into one critical section before the smart-routing branch.
  - `UI/TrayManager.cs:FireCharacterHotkeyByName` — `FindCharacterByName` snapshot.
  - `UI/TrayManager.cs:ResolveTeamSlotDisplayName` — `FindCharacterByName` + `FindAccountByName` snapshotted in one section.
  - `UI/TrayManager.cs:BuildTeamTooltip` — per-slot `FindCharacterByName` + `FindAccountByName` under lock (per-call, not per-slot — amortized for typical 4-6 slot teams).
  - `Core/WindowManager.cs:SetWindowTitle` — `FindCharacterByName(boundName)` snapshot. Fires from WinForms timer (UI thread) but a Settings → Apply swap of `_config.Characters` could still torn-read.

- **Orphan 2: `IsClientResponsive` `GetLastWin32Error` extension.** Added second overload `IsClientResponsive(IntPtr hwnd, out int lastErr)`; original 5 call sites (`ArrangeSingleScreen`, `ArrangeMultiMonitor`, `SwapWindows`, `ResizeToCurrentMonitors`, `ApplySlimTitlebar`) all updated to capture and include `lastErr` in their warn logs. Diagnostic-quality only — disambiguates timeout (`lastErr=0`) from window-gone (`ERROR_INVALID_WINDOW_HANDLE=1400`) from access-denied (`ERROR_ACCESS_DENIED=5`).

- **Orphan 5: AutoLoginManager wider `_config.*` threadpool audit.** Re-audited `RunLoginSequence` (lines ~310-812) and `HandleCharSelectViaShm` paths. Only existing `_config.Accounts` read sites are already under `ConfigMutationLock` (line 1313 + 1342 + 2177) — no new sites needed locking. Confirmed.

### Closing note on the cross-app parity cascade

The v3.22.27 → v3.22.28 → v3.22.29 cycle was driven by **convergent-verifier comparison against MWBToggle** — a sibling app with a different threat model (wider distribution, more attack surface, includes per-hop redirect validation precisely because earlier security iterations surfaced the gap). EQSwitch's actual threat model is personal-use multibox for a single user against GitHub-Releases as the update channel; none of these items shipped because a real bug was reported or a real exploit was observed. They shipped because the verifier loop flagged "MWBToggle has this, EQSwitch doesn't" and Nate's directive was clear-the-backlog. After v3.22.29 the cross-app delta is zero on the MWBToggle parity axis.

Future "sibling has X, we don't" verifier findings should be weighed against EQSwitch's actual threat model before shipping; treat parity-shaped findings as already-known unless the pattern is genuinely new.

### Build

- `dotnet build -c Release` clean (0 warnings, 0 errors).
- `dotnet build -c Debug` clean (Core/*Tests.cs included in Debug, all pass restore + compile).
- No new tests added — UpdateDialog test coverage stays in the "accepted gap" tier per the same threat-model logic above. The Core/* test files (AppConfig, CharacterSelector, CharSelectReader, KeyInputWriter, ShmLayout) remain authoritative for the domains they cover.

## v3.22.28 — Host-equality self-update allowlist + plug `_hashFileUrl` allowlist gap (2026-05-22)

**Errata (added post-ship, 2026-05-22 verifier round):**
- The "brings EQSwitch's self-update URL allowlist into line with the host-equality canonical pattern shared by MicMute, MWBToggle, CapsNumTray, and SyncthingPause" framing below is **partial parity, not full parity**. EQSwitch adopts the host-equality *shape* (`Uri.TryCreate` + `IsAllowedHost`) but still **lacks the per-hop redirect validation** (`SendAllowlistedAsync`) that MWBToggle and SyncthingPause have. EQSwitch's `_http` is configured with the default `AllowAutoRedirect=true`, so only the initial pre-redirect URL is checked. A hostile 3xx redirect would pass through unchallenged. The "defense-in-depth" framing later in this entry acknowledges this; the opening framing was overstated. Multi-hop validation is in the v3.22.29 backlog (see `TODO_LIST.md`).
- The "No new tests added — the project has no test harness" claim further down is **wrong**. The project has a `Core/*Tests.cs` set (5 test files) that's Release-excluded via `<Compile Remove="Core\*Tests.cs" />` in `EQSwitch.csproj`. They cover other modules; `UpdateDialog` specifically has no test coverage. UpdateDialog test coverage is in the v3.22.29 backlog.

Cross-app convergent hardening — brings EQSwitch's self-update URL allowlist into line with the host-equality canonical pattern shared by MicMute, MWBToggle v2.5.19, CapsNumTray, and SyncthingPause's `IsAllowedReleaseAssetUrl`. Lifted as part of a multi-task workspace sweep.

### Audit finding closed in the same commit: `_hashFileUrl` had no allowlist

The prescription that drove this sweep covered only `DownloadFileAsync`'s inline 3-prefix `StartsWith` check (the zip-download URL). Auditing the rest of `UpdateDialog.cs` revealed a separate gap: `_hashFileUrl` is populated from the GitHub releases JSON in `CheckForUpdateAsync` (line 213, `asset.GetProperty("browser_download_url")`) and then fetched directly via `_http.GetStringAsync(_hashFileUrl, ...)` in `OnActionClick` with **zero client-side origin validation**. A hostile or compromised API response could have steered the SHA256SUMS fetch to any URL on any host. v3.22.28 closes that gap with the same `IsAllowedHost(Uri, bool allowApi)` helper used for the download check.

### Changes

- **New `IsAllowedHost(Uri uri, bool allowApi)`** in UpdateDialog's Helpers section. Uri-parsed + scheme-pinned-to-HTTPS + host-equality on the two CDN edges (`objects.githubusercontent.com`, `release-assets.githubusercontent.com`) + AbsolutePath-scoped check on `github.com` / `api.github.com` (the latter gated behind `allowApi`).
- **`DownloadFileAsync` (line ~463)** — replaces the 3-prefix `StartsWith` block with `Uri.TryCreate` + `IsAllowedHost(dlUri, allowApi: false)`.
- **`OnActionClick` SHA256SUMS branch (line ~340)** — new allowlist gate before `_http.GetStringAsync(_hashFileUrl, ...)`. Rejects on parse failure or unknown host; logs via `FileLogger.Warn`, deletes the zip, and surfaces `"SHA256SUMS URL is not from the expected source. Update aborted for safety."` to the user. Fail-closed — same posture as the rest of the verification chain.

### Why this matters in practice

The current `_http` is configured with default `AllowAutoRedirect=true`, so only the initial pre-redirect URL ever reaches either check. With both URLs sourced from `github.com/itsnateai/eqswitch/releases/...` paths in the GitHub releases JSON, in normal operation neither check ever rejects. The hardening is defense-in-depth against:
- A future refactor to manual per-hop redirect validation (which would route every hop through `IsAllowedHost`)
- A compromised GitHub API response that swaps `browser_download_url` for an attacker-controlled URL
- Malformed URLs (embedded whitespace, mixed-case scheme) that a raw `StartsWith` would accept but that `Uri.TryCreate` rejects

### Repo-scope tightening

Both checks scope to `/itsnateai/eqswitch/` (and `/repos/itsnateai/eqswitch/` for the API host, currently unused at any caller). Previously the prefix check accepted `/itsnateai/` broadly, so a compromised redirect to another itsnateai-owned repo's release asset would have passed. Not a high-realism attack but the tighter scope is free.

### Build

- `dotnet build -c Release` clean (0 warnings, 0 errors).
- No new tests added — the project has no test harness (a documented gap, not in this sweep's scope).

## v3.22.27 — Backlog drain: _config.Characters lock symmetry, CDN allowlist hardening, ConfigManager hygiene, hung-PID log disambiguation (2026-05-22)

Picks up the v3.22.26 backlog plus one cross-app finding from a sibling
self-update CDN audit. Per Nate's clear-the-backlog directive 2026-05-22,
leaned toward DO over DEFER on every borderline call. Every prescription
was grep-audited against current code per
`feedback_audit_before_handoff_prescribe` before implementation —
notably Item 0's "CDN bug" turned out to be latent-not-active in
EQSwitch's HttpClient-auto-redirect architecture, but the patch landed
anyway as defense-in-depth against a future manual-redirect refactor.

### Item 0: CDN dual-host allowlist hardening (cross-app convergent)

8-agent T4 Sonnet+Opus convergent finding from a sibling self-update CDN
audit. MicMute, SyncthingPause, MWBToggle, and CapsNumTray were hardened
to accept BOTH `objects.githubusercontent.com` (legacy) and
`release-assets.githubusercontent.com` (the new GitHub release-asset edge
that GitHub rolled alongside the legacy). EQSwitch was missed.

Pre-implementation audit (`UI/UpdateDialog.cs:127` and 466-467) revealed
that EQSwitch's `_http` is a default `HttpClient` with
`AllowAutoRedirect = true` (the .NET 8 default), so today the
`url.StartsWith` check at line 466 only ever sees the pre-redirect
`github.com/itsnateai/...` URL — the CDN clauses are unreachable code
in current architecture. This differs from MWBToggle/MicMute, which
have manual per-hop redirect validators where the CDN URL DOES surface.

Patch landed anyway as defense-in-depth: keeps the allowlist
comprehensive so a future refactor toward manual redirect validation
(a hardening pass we may want — the current `StartsWith` allows
subdomain-spoof attacks like `objects.githubusercontent.com.evil.example`)
doesn't fall into a half-patched state.

Files: `UI/UpdateDialog.cs:464-476`.

### Item 1: `_config.Characters` lock symmetry at 4 sites

T2 Opus CRITICAL + T2 Sonnet MINOR "latent" from the v3.22.26 R2
verifier round. The v3.22.26 PopulateFromConfig lock-snapshot fix
introduced a gap-asymmetry — four other UI-thread sites still read
`_config.Characters` unlocked. Latent today (SM doesn't write
Characters), but the gap would silently become a torn-read bug the
moment SM gains a Character-write feature.

Sites locked under `ConfigManager.ConfigMutationLock`:

1. **`UI/SettingsForm.cs:RefreshDirectBindingsCard`** — wraps the
   6-line snapshot block (`HotkeyBindingUtil.Count*` ×4 +
   `_config.Accounts.Count` + `_config.Characters.Count`). The
   utilities iterate `_config.*` internally so the lock has to
   include them. Re-entrant on the ApplySettings call-path.
2. **`UI/SettingsForm.cs:OpenCharacterHotkeysDialog`** — wraps the
   `new CharacterHotkeysDialog(_config.Characters, ...)` ctor.
   Dialog stores the live ref in `_characters` but only iterates
   during construction (line 61 `.Any()` + line 72 `.Count`), so the
   wrap covers the actual read surface. Comment flags the
   stored-reference latent for a future SM-Character-write trigger.
3. **`UI/TrayManager.cs:BuildContextMenu`** — wraps the whole `_config`
   read surface (lines ~1402-1447): `_config.Hotkeys` ref-copy,
   `LegacyHotkeyLookup(_config)` ctor, `foreach _config.Accounts`,
   and the three `BuildAccountsSubmenu` / `BuildCharactersSubmenu` /
   `BuildTeamsSubmenu` calls that iterate live refs internally.
   Reentrant — the ReloadConfigCore caller already holds the outer
   lock at line 2596-2600 (now 2606-2610 post-edit). Hold time:
   ms-scale UI-thread menu construction, well under SM tick budget.
4. **`UI/TrayManager.cs:FireLegacyQuickLoginSlot`** — three separate
   tiny locks around the LegacyAccounts / Characters / Accounts
   FirstOrDefault lookups. Each lock releases BEFORE the heavy
   `FireCharacterLogin` / `FireAccountLogin` dispatch (which launches
   an EQ client and must NOT hold the lock).

Verified the single write site at `TrayManager.cs:2745`
(`_config.Characters = newConfig.Characters` inside `ReloadConfigCore`)
is already under the outer lock at line 2596-2600.

### Item 2: `probe_okdialog_search.py` auto-derive CXWndManager VA

T3 Sonnet CONCERN. The hardcoded `CXWND_MANAGER = 0x12702210` was the
heap VA from PID 21864 — silently NULLs against any new PID and exits
with "CXWndManager ... is NULL — manager moved". Lifted the auto-derive
pattern from sister probe `probe_okbox_dump.py:187-204`: accept hex
`sys.argv[2]` first, fall back to parsing
`C:/Users/nate/proggy/Everquest/Eqfresh/eqswitch-dinput8-{pid}.log`
for the "SELECTED eqmain CXWndManager at {hex}" line.

Files: `_.eqswitch-re/audit-2026-05-21/probe_okdialog_search.py`
(out-of-tree from eqswitch git — no commit needed).

### Item 3: `probe_okbox_dump.py` bounds-check sweep

T3 Sonnet CONCERN. `read_dword` / `read_bytes` lacked the
`addr < 0x10000 or addr >= USER_SPACE_MAX` guard that sister
`probe_okdisplay_tick.py:read_dword` includes. Defended at call sites
by `visited` + caller pre-validation today, but asymmetric. Added the
guard to both primitives for symmetric defense-in-depth.

Files: `_.eqswitch-re/audit-2026-05-21/probe_okbox_dump.py`.

### Item 4: `PopulateForceKillMenu` GetLastWin32Error disambiguation

T2 Sonnet/T2 Opus convergent from v3.22.25 R4 (carried over). The hung
detector at `UI/TrayManager.cs:1652-1666` (now ~1660-1700 post-edit)
treats `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 100ms) ==
IntPtr.Zero` as "hung" — but conflates two cases: pump timed out
(slow but maybe alive, `ERROR_TIMEOUT` 1460) vs kernel-marked hung
or hwnd died (other, e.g. `ERROR_INVALID_WINDOW_HANDLE` 1400).

Added `Marshal.GetLastWin32Error()` capture and an Info-level log
line that names the err class. User-facing tray label stays "⚠ HUNG"
for both — same Force-Kill intent — but post-mortem now distinguishes
slow-pump from kernel-hung.

`SMTO_BLOCK` intentionally NOT added (T2 verifier had recommended it,
T3 disagreed). The `WindowManager.IsClientResponsive` precedent uses
BLOCK for synchronous arrange — but this is a `DropDownOpening` tray
menu handler. BLOCK on N hung clients would freeze the menu open
during the 100ms × N probe wait. The non-BLOCK semantic is the
correct trade-off here.

Files: `UI/TrayManager.cs:4-9` (added
`using System.Runtime.InteropServices;`), 1660-1700.

### Item 5: Typed catch in `WriteToDisk` orphan-tmp cleanup

T3 Opus LOW. The orphan `.tmp` cleanup at `Config/ConfigManager.cs:402-406`
used bare `catch { }` which swallows everything including
`OutOfMemoryException` and `ThreadAbortException`. Mask-risk is zero
because the outer `SaveFailed` event already fired — but tighter
typed catches are best-practice.

Replaced with `catch (IOException) { } catch (UnauthorizedAccessException) { }` —
the only realistic delete-failure modes here (sharing violation, AV
lock, ACL change). OOM / ThreadAbort propagate as expected.

Files: `Config/ConfigManager.cs:402-413`.

### Item 6a: Break-on-first-IOException in CreateBackup prune loop

T2/T3 single-flagged. The pruning foreach at
`Config/ConfigManager.cs:447-451` (now ~462-481) tried each
`File.Delete` individually and logged Info per failure. On disk-full,
all 10 deletes fail for the same root cause → 10 identical Info lines
of log spam.

Refactored to typed catches that break on first IOException or
UnauthorizedAccessException, logging once with "remaining stale backups
deferred to next save" guidance. CreateBackup itself re-enforces the
retention cap on next call, so deferral is safe.

Files: `Config/ConfigManager.cs:462-481`.

### Item 6b: `_pendingSave` re-check under `_saveLock` re-grab in FlushSave

Pre-existing race in `Config/ConfigManager.cs:FlushSave` — same fix-class
as v3.15.10 closed for the staging-side write. After WriteToDisk
released `_saveLock`, the code read `_pendingSave != null` unlocked at
line 270 to decide whether to restart the coalesce timer. A
background-thread `Save()` running between the unlocked check and the
timer restart could publish a new `_pendingSave` that we'd see-or-miss
depending on read ordering, worst-case losing a coalesced save.

Read now inside a re-grab of `_saveLock`. Symmetric with the drain
block at lines 257-261.

Files: `Config/ConfigManager.cs:265-289`.

### Item 6c: Magic-number consts at ConfigManager class top

T2/T3 single-flagged. The 250ms coalesce timer interval (line 226)
and the 10-backup retention cap (line 460) were inline literals.
Extracted to `CoalesceSaveIntervalMs = 250` and `BackupRetentionCount = 10`
at the class top so both tunables live in one place. Still hardcoded
(not AppConfig-exposed) — user-configurability would invite policies
we don't want to support (sub-50ms coalesce risks UI stall, zero
retention defeats the recovery use case). Defer config exposure
until concrete need.

Files: `Config/ConfigManager.cs:20-29, 226, 460`.

### R1 follow-up — verifier-convergent gap-by-omission fixes

The R1 6-agent verifier round (T1/T2/T3 × Sonnet+Opus) found three
unflagged Account/Character lookup sites in the call graph the
original CHANGELOG enumeration missed. Per Nate's DO-over-DEFER
directive, all three were folded back in rather than deferred. Plus
two originally-deferred sites of the same fix-class — same logic,
same single-line `FirstOrDefault` wrap done eight times in this
commit; deferral was no longer load-bearing.

**Convergent (T2-Sonnet ∩ T2-Opus): TrayManager.cs:2423 →
`TryFireV4QuickLoginFallback`** is a different method from
`FireLegacyQuickLoginSlot` — it's called from the
`legacyRow == null` early-exit path BEFORE the three-tiny-locks
block ever runs. Has its own `_config.Hotkeys` ref-copy +
CharacterHotkeys/AccountHotkeys iteration + `FindCharacterByName` /
`_config.Accounts.FirstOrDefault` reads. Refactored to snapshot the
combined binding list under one lock, then wrap each conditional
lookup (Character vs Account) in its own short lock, dispatching
Fire*Login outside.

**T2-Opus only (G1 HIGH): SettingsForm.cs:1872 →
`ApplySettings.Accounts.Select`** reads `_config.Accounts.First-
OrDefault` while building the staged-vs-live merge for each account.
SM's `AutoLoginManager.SaveImmediate` writes the same `LastLogin-
Result` / `LastLoginAt` fields from a background thread — the EXACT
race that drove the v3.22.26 JsonSerializer closure. Per "trust the
flag" synthesis rule (Opus/Sonnet disagreement = trust the flag),
this gets the same lock wrap.

**T3-Opus only (LOW): TrayManager.cs:2367 →
`_config.FindCharacterByName`** in `FireLegacyQuickLoginSlot`'s
enter-world branch. Same method as the three-tiny-locks, but the
enter-world branch was missed — FindCharacterByName iterates
`_config.Characters` internally.

**Originally deferred, folded back in (same fix-class):**
- SettingsForm.cs:703 `new AccountHotkeysDialog(_config.Accounts,
  ...)` — symmetric with the v3.22.27 ctor-block CharacterHotkeys-
  Dialog lock.
- TrayManager.cs FireLegacyQuickLoginSlot account-only fallback
  (`_config.Accounts.FirstOrDefault(a => accountKey.Matches(a))`) —
  was the 4th flagged-but-deferred site from R0.

### R2 follow-up — cross-thread exposure on team-fire path

R2 6-agent verifier round on c5689d3 (T2-Sonnet + T2-Opus convergent
HIGH) surfaced a **different fix-class** than R0/R1: cross-thread
exposure on the team-fire path. R0/R1 covered UI-thread `_config.*`
reads (latent today — SM doesn't write Accounts/Characters). R2
finds that `FireTeam` runs on a `Task.Run` threadpool thread, and
inside its loop calls `LoginAndEnterWorld` which reads
`_config.Accounts.FirstOrDefault` at the entry point. Between the
`await Task.Delay(delayMs)` iterations, a UI-thread ApplySettings
can swap `_config.Characters` / `_config.Accounts` mid-loop, torn-
reading the threadpool reader. Not latent — actively exposed today.

Folded in (the 2 convergent-HIGH cross-thread sites):

1. **`TrayManager.cs:FireTeam` Task.Run lambda** — wrap each per-slot
   `_config.FindCharacterByName(user)` and `_config.FindAccountByName(user)`
   in `lock (ConfigManager.ConfigMutationLock)`. Released before the
   parallel `LoginAndEnterWorld` / `LoginToCharselect` dispatch.
2. **`AutoLoginManager.LoginAndEnterWorld`** — wrap the entry-point
   `_config.Accounts.FirstOrDefault(key.Matches)` lookup. The rest
   of the method runs after lock release and does no `_config` list
   iteration before its own SM-finally `_saveLock` write-back (which
   has its own ordering invariant — ConfigMutationLock first, then
   _saveLock — respected by acquiring CML BEFORE the SaveImmediate
   write-back deeper in the method).

### Items deferred to v3.22.28 (final after R2)

- **Host-equality CDN allowlist.** EQSwitch's `StartsWith` allowlist
  still allows subdomain spoofing (e.g.
  `objects.githubusercontent.com.evil.example`) that the MicMute /
  CapsNumTray host-equality pattern blocks. Hardening pass deferred
  to v3.22.28 or v3.23.0. Separate fix-class from Item 1 — needs
  decision on prefix-vs-host-equality semantics.
- **`WindowManager.IsClientResponsive` symmetric Item-4 miss.**
  R1-T2-Sonnet flagged five callers of `IsClientResponsive` that log
  "non-responsive window" on `SendMessageTimeout` zero return but
  don't capture `Marshal.GetLastWin32Error()` for the ERROR_TIMEOUT
  vs ERROR_INVALID_WINDOW_HANDLE distinction Item 4 added to
  `PopulateForceKillMenu`. Diagnostic-quality only (the helper's
  return type is `bool` — there's no user-facing label conflation),
  so applies to log forensics only.
- **UI-thread-only hotkey-dispatch sites** (`FireAccountHotkeyByName`,
  `FireCharacterHotkeyByName`, `WindowManager.SetWindowTitle`,
  `Open*HotkeyDialog` `others` foreach). R2-T2 surfaced these as
  same-fix-class continuations of R0/R1 — but all are UI-thread-only
  paths (hotkey WM_HOTKEY messages marshal to UI thread; SetWindowTitle
  fires from WinForms timer on UI thread). Same latent-only risk class
  as the R0/R1 sites — SM doesn't write the affected lists today.
  Diminishing-returns signal hit on R2 — each round surfaces 3-5 new
  latent UI-thread sites in the same fix-class. v3.22.28 will do a
  single sweeping audit-and-lock pass on all remaining UI-thread
  `_config.*.FirstOrDefault` / `Find*ByName` sites in one batch
  rather than another iterative round.
- **Item 6d:** `Monitor.TryEnter(0)` diagnostic branch in
  `UI/SettingsForm.cs:1685` left intentionally — the contention log
  is useful for debugging Apply-during-SM hangs. Pre-existing
  not-a-bug.

---

## v3.22.26 — ReloadConfigCore propagation gaps + JsonSerializer torn-write closure + handoff-audit corrections (2026-05-22)

Picks up the v3.22.25 backlog items 1/2/3/6. Two of the four turned out
to need different fixes than the v3.22.25 verifier prescribed — fully
audited and re-scoped before implementing per the
`feedback_audit_before_handoff_prescribe` rule.

### Item 1: `ReloadConfigCore` Launch + LogTrimThresholdMB propagation

The v3.15.2 verifier-T4 catch pattern recurs: SettingsForm.BuildAppConfig
carries fields forward into `newConfig`, but `TrayManager.ReloadConfigCore`
never copies them onto the live `_config`, so the live SM keeps reading
the pre-Apply values until process restart.

R1 T3-Sonnet (the v3.22.25 round-1 verifier) flagged `LogTrimThresholdMB`
+ `Launch.UseStateMachine`. **Audit during implementation widened the
scope to 8 fields** — the v3.17.0+ JSON-only Launch tunables batch
(`StaleSessionPollIntervalMs`, `ConnectRetryCount`, `PostBurst2QuickFailCheckMs`,
`SkipShmEnterWorldOnDalaya`, `SkipNativeWarmup`, `JoinServerId`) was
added to BuildAppConfig on 2026-05-15 with the same "pass-through to
preserve user JSON-edited values" rationale, but the propagation block
in ReloadConfigCore never got the matching update. Same regression class,
same one-line-per-field fix. R1 caught 2; full audit found 8.

`UseStateMachine` had a SECOND bug at the upstream BuildAppConfig site:
no Settings UI control existed for the flag, and the Launch initializer
at SettingsForm.cs:1727 didn't include a pass-through line. Every
Settings → Apply silently clobbered `UseStateMachine` to `false` (the
LaunchConfig class default). The in-session in-memory value stayed
correct only because ReloadConfigCore also didn't propagate it — but
the persisted disk file got the wrong value, so the next launch would
load `UseStateMachine=false` and disable the SM path entirely. Fixed by
adding the pass-through line to BuildAppConfig + propagating in
ReloadConfigCore (symmetric fix; defense-in-depth against a future UI
control being added).

The handoff's claim that both fields were "reachable from Settings UI"
was wrong about `UseStateMachine` — corrected in the implementation.

#### Files touched
- `UI/TrayManager.cs` — 7 new propagation lines in the Launch.* block
  (`StaleSessionPollIntervalMs`, `ConnectRetryCount`,
  `PostBurst2QuickFailCheckMs`, `SkipShmEnterWorldOnDalaya`,
  `SkipNativeWarmup`, `JoinServerId`, `UseStateMachine`) + 1 new line
  for `LogTrimThresholdMB` = 8 new propagation lines total. Comments
  explain the regression class.
- `UI/SettingsForm.cs` — pass-through line for `UseStateMachine` in the
  BuildAppConfig Launch initializer.

### Item 2: JsonSerializer torn-write closure (replaces 9-form lock-wrapping)

The v3.22.25 XML doc on `ConfigManager.ConfigMutationLock` listed 9
forms that call `ConfigManager.Save` while not holding the lock —
`JsonSerializer.Serialize(_config)` walks the entire object graph
including `Accounts[].LastLoginResult/LastLoginAt`, so concurrent SM
Account writes can in theory produce a torn JSON write.

The handoff prescribed wrapping each form's Save call in
`lock (ConfigMutationLock)`. **This would not have worked.**
`ConfigManager.Save` is coalesced — it stages `_pendingSave` and
returns; the actual `WriteToDisk` (which contains the JsonSerializer
call) fires later via a 250ms `Windows.Forms.Timer` callback. By
the time the serializer runs, the form's lock has long since
released.

The fix is at the serializer site itself: take `ConfigMutationLock`
around the `JsonSerializer.Serialize(config, JsonOptions)` call inside
`WriteToDisk`. The lock is held only for the serializer call (low-ms
typically, up to tens of ms with a fully-populated config — same
magnitude as `ReloadConfigCore`'s UI-rebuild path), not the subsequent
`File.WriteAllText` / `File.Move` calls — once serialization completes
the in-memory mutations don't affect the string.

This catches all 9 forms, plus any future caller of `Save`/`SaveImmediate`,
with a single re-entrant lock acquisition. Lock ordering: from the
SM-finally path (`ConfigMutationLock` → `SaveImmediate` → `_saveLock` →
`WriteToDisk` → second `ConfigMutationLock`), C# recursive-lock
semantics make the inner acquisition a no-op on the same thread.
From the UI-thread coalesced path, the UI thread holds no lock when
the timer fires WriteToDisk, so the acquisition is uncontended unless
the SM is mid-mutation.

#### Files touched
- `Config/ConfigManager.cs:WriteToDisk` — `lock (ConfigMutationLock)`
  scoped to the `JsonSerializer.Serialize` call only.
- `Config/ConfigManager.cs` XML doc — closed the 9-form gap-list entry,
  added `WriteToDisk` to the COVERS list.

### Item 3: PopulateFromConfig form-open snapshot lock (NOT BuildAccountsSubmenu)

The v3.22.25 XML doc gap-list claimed `TrayManager.BuildAccountsSubmenu`
reads `captured.Tooltip` "derived from `LastLoginResult`" as an unlocked
torn-read. Audit during implementation: **the claim was wrong**.
`Account.Tooltip` (defined at `Models/Account.cs:50`) is
`$"{Username}@{Server}"` — it reads `Username` and `Server`, two fields
that the AutoLoginManager never writes. `BuildAccountsSubmenu` has no
torn-read.

The R1 T3-Sonnet verifier was misled by the stale XML doc comment —
exactly the failure mode tracked in
`memory/feedback_misleading_comments_fool_verifiers.md`. The doc comment
is corrected to a tombstone retained as a guard against re-prescription.

The actual unlocked torn-read site for `LastLoginResult` is
`SettingsForm.PopulateFromConfig` (the form-open snapshot copies
`LastLoginResult` and `LastLoginAt` into `_pendingAccounts` without
holding the lock). Opening Settings while an autologin SM is mid-write
could torn-read into the staged snapshot. The ApplySettings race-fix
(reads `live!.LastLoginResult` back under the lock) means torn snapshots
never persisted a wrong value to disk, but the dialog's Flag column
could show stale data for the open duration. Fixed by wrapping the
Select in `lock (ConfigMutationLock)`.

**Verifier-round addendum** (T3 Sonnet + T3 Opus convergent): the lock
was extended to ALSO cover the `_pendingCharacters` snapshot in the
same block. SM doesn't write Character fields today, so this is latent
— but a future SM extension that writes Characters would miss the
guard silently and the gap-asymmetry-by-omission would be invisible.
Cheap belt-and-suspenders; one lock acquisition covers both lists.

#### Files touched
- `UI/SettingsForm.cs:PopulateFromConfig` — Accounts + Characters
  Select blocks wrapped in `lock (ConfigManager.ConfigMutationLock)`;
  duplicate unlocked Characters Select removed.
- `Config/ConfigManager.cs` XML doc — BuildAccountsSubmenu entry retired
  as a tombstone with a pointer to `Models/Account.cs:50`;
  PopulateFromConfig added to the COVERS list.

### Item 6: NativeMethods.cs:124 comment update

Cosmetic. The comment block at `Core/NativeMethods.cs:124` said
"v3.22.22 round-4: responsiveness pre-flight for cross-process arrange"
but the `SendMessageTimeout` + `SMTO_ABORTIFHUNG` declaration is now
also used by `TrayManager.PopulateForceKillMenu` (added v3.22.25 R4).
Updated to credit both versions.

### Items 4 and 5 (deferred)

Item 4 (sister probes hardcode `0x80000000`) is research-tools-only and
out-of-tree from eqswitch — will ship as a separate commit at
`_.eqswitch-re/audit-2026-05-21/`.

Item 5 (`SMTO_BLOCK` + `GetLastError` disambiguation) is contested
between R4 verifier topics (T3 says correct, T2 says missing); deferred
until re-entrancy is empirically observed in the field.

### Verification

`dotnet build` clean against .NET 8 on win-x64 (0 warnings, 0 errors).
All 5 inline test suites pass: AppConfigValidateTests, ShmLayoutTests,
KeyInputWriterTests, CharacterSelectorTests, CharSelectReaderTests.

**Verifier round** (normal stakes, 3 topics × Sonnet+Opus = 6 agents):
- T1 Diff-clean: Sonnet CONCERNS (minor CHANGELOG factual drift —
  "8 new lines in Launch.* block" should be "7 Launch + 1 LogTrim = 8
  total"; fixed in this section), Opus APPROVE.
- T2 Gap-audit: Sonnet APPROVE (all 22 LaunchConfig fields verified
  in both BuildAppConfig + ReloadConfigCore; single JsonSerializer.Serialize
  call site confirmed; no undocumented LastLoginResult reads); Opus
  APPROVE.
- T3 Code-review: Sonnet CONCERNS (`_pendingCharacters` snapshot
  outside the lock — convergent with T3 Opus); Opus CONCERNS
  (JsonSerializer hold-time "sub-ms" overclaim; orphan `.tmp` leak
  on save failure path; `_pendingCharacters` unlocked).

**Fix-set applied after verifier round** (convergent findings only):
1. `_pendingCharacters` snapshot wrapped in the same lock as
   `_pendingAccounts` in PopulateFromConfig.
2. WriteToDisk hold-time claim corrected from "sub-ms" to "low-ms
   typically, up to tens of ms with a fully-populated config";
   accepted tradeoff with snapshot+unlock alternative.
3. Orphan `.tmp` cleanup added to WriteToDisk catch block.
4. CHANGELOG factual drift corrected.

Out-of-scope (pre-existing, single-verifier, not introduced by v3.22.26):
- `Monitor.TryEnter(0) → Monitor.Enter` pattern in ApplySettings
  (diagnostic; functionally equivalent to direct Enter).
- Hardcoded backup retention (10) and coalesce interval (250ms).
- `FlushSave` re-queue check outside `_saveLock`.
- `CreateBackup` swallowing failures with Warn-only logging.

### Post-ship verifier round (R2) — convergent doc-fix follow-up

Round 2 (re-verification after R1's fix-set landed) returned 3 APPROVE
+ 3 CONCERNS + 0 REJECT, same ratio as R1. Convergent doc-hygiene
fixes applied post-ship (release v3.22.26 binary unchanged; main
branch carries the doc corrections forward to v3.22.27):

- **`tempPath` duplication** (T2 Opus CRITICAL + T3 Opus LOW): the
  orphan-cleanup catch block re-concatenated `ConfigPath + ".tmp"`
  as a separate local instead of reusing the `tempPath` declared in
  the try. Drift risk on future suffix refactor. Hoisted `tempPath`
  outside the try so the catch references the same variable.
- **"sub-ms" doc ghost** (T2 Sonnet CRITICAL): the PipOverlay
  NOT-COVERS entry in `ConfigManager.ConfigMutationLock` XML doc
  AND this CHANGELOG section both still said the JsonSerializer
  window was "sub-ms". The R1 fix-set corrected the WriteToDisk
  inline doc but missed these two adjacent sites — inconsistent
  with the corrected hold-time claim. Both updated.

Acknowledged-but-deferred (T2 Opus CRITICAL, T2 Sonnet MINOR
"latent"):
- `SettingsForm.cs:578` (`int totalC = _config.Characters.Count`)
- `SettingsForm.cs:730` (`new CharacterHotkeysDialog(_config.Characters, ...)`)
- `TrayManager.cs:1446` (`BuildCharactersSubmenu(_config.Characters, ...)` —
  called via `BuildContextMenu`, sometimes outside ReloadConfigCore's lock)

These are unlocked reads of `_config.Characters` outside the
PopulateFromConfig snapshot that v3.22.26 fixed. T2 Sonnet downgraded
to "latent" because SM does NOT write Character fields today — the
torn-read class doesn't exist. T2 Opus flagged as CRITICAL on the
future-proofing rationale ("if SM ever writes Characters, the gap-
asymmetry-by-omission would be invisible"). Decision: defer to a
future release when (a) SM is actually extended to write Characters,
or (b) a serializer-site lock-extension covers them automatically
the same way the v3.22.26 WriteToDisk lock covers `_config.Accounts`.
The serializer-site lock already protects PERSISTED Characters data
— what remains unprotected is the IN-FORM dialog UI display only.

### Handoff-audit retrospective

The v3.22.25 → v3.22.26 handoff was authored straight from the v3.22.25
verifier-round artifacts. The audit-before-prescribe rule (per
`memory/feedback_audit_before_handoff_prescribe.md`) was applied
during implementation, and found:

- **Item 1** under-scoped: 2 fields flagged, 8 fields broken (the
  v3.17.0+ batch was missed by the R1 verifier's narrow read).
- **Item 1** mis-attributed: `UseStateMachine` has no Settings UI; the
  real bug was upstream at BuildAppConfig, not at ReloadConfigCore.
- **Item 2** wrong fix site: form-side lock wrapping would not have
  protected the coalesced serializer call; correct site is the
  serializer itself.
- **Item 3** phantom bug: `Account.Tooltip` is `Username@Server`, not
  derived from `LastLoginResult`; verifier was misled by a stale doc
  comment. Real torn-read site is `PopulateFromConfig`.

Three of four prescriptions needed re-scoping. The rule keeps earning
its keep — every handoff has stale specifics by the time the next
session reads it.

## v3.22.25 — ApplySettings race fix + Force-Kill Stuck Client + +0x30C investigation closed (2026-05-22)

### Item 1: `ConfigManager.ConfigMutationLock`

Closes the v3.22.24 backlog item "ApplySettings + autologin race — LOCK FIX".
Same class of bug as v3.22.23's X-flag orphan-reference race but on the
config-mutation surface instead of the in-memory captured-reference surface.

#### Race surface (pre-fix)

If the user clicks Settings → Apply WHILE `RunLoginStateMachine` is mid-flight
(SM's finally block running `SaveImmediate(_config)`), three distinct races
can fire:

1. **List-swap race.** `TrayManager.ReloadConfig` swaps `_config.Accounts =
   newConfig.Accounts` (line 2541) while SM-finally reads it (already
   partially defended by snapshot-to-local at AutoLoginManager.cs:1310).
2. **Per-Account field-read race.** `SettingsForm.ApplySettings` at
   line ~1797 reads `live.LastLoginResult` to pick up the SM's deferred
   write, but SM might be mid-write at line 1342 — the staged value goes
   into `newConfig` as stale, `ReloadConfig` swaps the list, and SM's
   later write lands on an orphan that's no longer in `_config.Accounts`.
3. **Torn JSON write.** `SaveImmediate` calls
   `JsonSerializer.Serialize(config)` which walks every field while
   `ReloadConfig` is field-by-field mutating those same fields.

The existing `_saveLock` in `ConfigManager` defends only the `_pendingSave`
queue + `WriteToDisk` critical section. It does NOT cover cross-thread
mutation of `_config` itself.

#### Fix

New `public static readonly object ConfigMutationLock` on `ConfigManager`.
Held by:

- `SettingsForm.ApplySettings` across the build-newConfig + `_onApply`
  call (covers the racy reads at line ~1797 + the dispatch into
  ReloadConfig). Uses `Monitor.TryEnter(0)`-then-`Enter` so the
  contended path logs "save deferred — Settings dialog
  ApplySettings/ReloadConfig in progress" for diagnostics.
- `TrayManager.ReloadConfig` body via `ReloadConfigCore` helper extract
  (covers ~50 field-by-field mutations + the `_config.Accounts` list-
  swap). Reentrant when reached via ApplySettings → _onApply →
  ReloadConfig — C# `lock` is recursive per thread, so the UI thread
  re-acquires harmlessly.
- `AutoLoginManager` SM finally-block "re-resolve + writes + SaveImmediate"
  sequence at AutoLoginManager.cs:1288-1373, and the legacy
  `RunLoginSequence` LastLoginResult fail/ok write sites at
  AutoLoginManager.cs:2940/2960 (extracted to
  `SaveLastLoginResultLocked` helper for DRY).

Both SM-side entry points use the same `Monitor.TryEnter(0)`-then-`Enter`
diagnostic pattern, so contention in either direction emits a clear
log line without changing semantics.

The SM-finally path's existing re-resolve at AutoLoginManager.cs:1310-1314
(snapshot `_config.Accounts`, FirstOrDefault by Username+Server) is
retained under the lock — when the lock is held, the re-resolve is
guaranteed to see either the post-ApplySettings list (fresh) or the
pre-ApplySettings list (stable), never a mid-mutation snapshot. The
"renamed mid-autologin" fallback warning still fires for the rare case
where Settings deleted-or-renamed the account being autoligned (that's
an identity change the lock can't fix).

The legacy `RunLoginSequence` path does NOT re-resolve — it writes to
the captured `account` ref directly. Pre-fix this was racy; post-fix
the lock prevents torn JSON writes but the orphan-ref-on-rename risk
remains. Legacy is not the deployed surface (UseStateMachine=true is
the operationally-active path), so the gap is accepted for legacy only.

#### Verification

`dotnet build` clean (0 warnings, 0 errors) against .NET 8 on win-x64.

**Round-1 verifier swarm (6 agents — Sonnet+Opus on T1/T2/T3 topics):**
- T1 Diff-clean Sonnet+Opus: APPROVE
- T2 Gap-audit Sonnet: CONCERNS (2 CRITICAL)
- T2 Gap-audit Opus: REJECT (5 CRITICAL)
- T3 Code-review Sonnet+Opus: CONCERNS (multiple)

Convergent findings → round-2 fixes applied:
1. **Canonical `Monitor.Enter(lock, ref tookLock)` pattern** at all three
   acquire sites (SettingsForm.ApplySettings, AutoLoginManager SM-finally,
   SaveLastLoginResultLocked). Pre-fix `TryEnter(0)` + naked
   `Monitor.Enter(lock)` could leak the lock if Enter threw between the
   acquire and the `try` block — `finally { Monitor.Exit }` would either
   skip a held lock OR throw `SynchronizationLockException` on a
   never-acquired one. Now: `bool tookLock = false; try { if (!TryEnter(0))
   Enter(lock, ref tookLock); else tookLock = true; ... } finally { if
   (tookLock) Exit(lock); }`.
2. **`SettingsForm.OnLoginComplete` read of `live.LastLogin*` now locked.**
   Round-1 fix only covered ApplySettings's read of the same fields;
   OnLoginComplete is fired on UI thread while a PARALLEL team-login SM
   could be mid-writing a different Account. Added `lock
   (ConfigMutationLock)` around the foreach + snapshot-to-locals before
   the `staged.*` writes.
3. **`TrayManager.ReloadConfigCore` `_reloading` flag now in try/finally.**
   Pre-fix a throw in the field-copy or UI-rebuild block left `_reloading
   = true` permanently — the line 1146 guard would silently drop every
   foreground-debounce timer tick until restart.
4. **`ConfigMutationLock` XML doc contract narrowed.** Pre-fix it claimed
   to cover "ALL cross-thread mutations" but the implementation only
   covered Accounts + LastLogin*. Now lists explicit COVERS / DOES NOT
   COVER sections with rationale for each excluded site (PipOverlay,
   ProcessManagerForm, EQClientSettingsForm, etc.) and a residual-torn-
   JSON note for v3.22.26 follow-up.

**Round-2 verifier swarm (6 agents — full re-pair-set per spec):**
- T1 Sonnet+Opus: APPROVE
- T2 Sonnet: CONCERNS (1 NEW MINOR — BuildAccountsSubmenu display-only torn-read, pre-existing, documented in scope-limit)
- T2 Opus: CONCERNS (1 MINOR — flagged the residual ProcessManagerForm-Save torn-JSON risk, pre-existing, documented in scope-limit)
- T3 Sonnet+Opus: APPROVE

**Verdict: 4 APPROVE / 2 CONCERNS (both MINOR, both pre-existing,
documented as scope-limit). Zero REJECTs. Shippable.**

**Pending:** smoke test of success-path autologin (item 1 is hard to
trigger manually so smoke is for regression detection, not race
reproduction).

#### Files

- `Config/ConfigManager.cs:91` — `ConfigMutationLock` declared with
  full XML doc explaining the contract.
- `UI/TrayManager.cs:2449-2475` — `ReloadConfig` now wraps
  `ReloadConfigCore` in `lock (ConfigManager.ConfigMutationLock)`.
- `UI/SettingsForm.cs:1659-1888` — `Monitor.TryEnter`/`try`/`finally`
  around the BuildAppConfig initializer + `_onApply(newConfig)` call.
- `Core/AutoLoginManager.cs:1288-1373` — SM finally save block now
  holds `ConfigMutationLock` for the entire re-resolve + writes +
  SaveImmediate sequence.
- `Core/AutoLoginManager.cs:~2140` — new `SaveLastLoginResultLocked`
  helper; legacy fail/ok save sites at lines 2940/2960 routed through
  it for symmetric lock coverage.

### Item 2: +0x30C anchor stability research — CLOSED (won't fix at structural-anchor layer)

Carryover from v3.22.24 fix4. The structural anchor at `pMain+0x25C → +0x30C →
CXStr` (Dalaya repurpose) was reliable on PID 33768 (2026-05-21 afternoon) but
null on 3 of 3 PIDs tested in follow-up sessions (34436, 24824, 39032) across
~644 ticks of combined polling. Hypotheses A (timing) and B (different widget
instance) were disproved by tick-by-tick probes in the 2026-05-21 evening
session.

The 2026-05-22 memory-scan session closes the investigation: literal text
`"username and/or password"` lives in a packed string catalog (1 MB private
heap allocation at `0x091A0000`) with 8-byte headers `?? BD B6 ?? ?? B6 03 0X`
preceding each entry. **NOT a CXStr layout** — relaxed CXStrRep header checks
at hit-0x10 / -0x14 / -0x18 / -0x1C all rejected. **Full-process xref scan
found 0 pointers anywhere in the full WoW64 user-mode address space**
pointing at either literal address. Verified twice: first pass capped at
`0x80000000` (33.55 s, 0 hits), then re-verification after the T2-Opus
verifier flagged that eqgame.exe is `/LARGEADDRESSAWARE` — the upper 2 GB
was a legitimate search domain. Re-scan with ceiling at `0xFFFE0000` also
returned 0 hits (10.57 s, `text-scan-PID39032-20260522_004805.inspect.json`).
The render path is by-ID lookup via a function not visible from
`pWindows` traversal, so no widget-anchor approach can structurally reach
this text.

**Decision:** keep fix4 in place (no-op when null, correct when populated —
zero downside) but lean on **fix7 visibility-age** as the canonical fail-
detection mechanism. Memory note `reference_eqswitch_native_phase_error_triggers.md`
amended to match.

#### Tooling shipped (research artifacts, local-only)

- `_.eqswitch-re/audit-2026-05-21/scan_eqgame_for_text.py` — VirtualQueryEx +
  ReadProcessMemory user-mode scanner with NtSuspendProcess/NtResumeProcess
  pair (always resumed in finally). Walks all readable regions, finds bytes,
  classifies each hit (image vs mapped vs private), checks plausible
  CXStrRep header at hit-0x14, optionally walks pWindows for back-references.
- `_.eqswitch-re/audit-2026-05-21/wait_for_okdialog.py` — polls okdialog
  +0x196 dShow byte until 1; gates the scan on actual dialog visibility.
- `_.eqswitch-re/audit-2026-05-21/fire-raistlin-and-scan.sh` — orchestrator
  (kill stale → fire hotkey → wait spawn → wait CXWndManager marker → wait
  dShow=1 → scan).
- `_.eqswitch-re/audit-2026-05-21/inspect_hits.py` — post-scan structure
  dump + pWindows pointer-into-region walk + optional full-process xref.
- `_.eqswitch-re/audit-2026-05-21/findings-memory-scan-2026-05-22.md` —
  full write-up.

### Item 3: Force-Kill Stuck Client tray menu

Task Manager fallback for hung `eqgame.exe` (DX reset deadlock, WndProc
loop, etc.). Right-click EQSwitch → 💀 Force-Kill Stuck Client → lazily-
populated submenu lists each running PID with its window title → click to
`Process.GetProcessById(pid).Kill()`.

- `UI/TrayManager.cs` — submenu added in `BuildContextMenu` next to Process
  Manager. DropDownOpening event handler `PopulateForceKillMenu` clears and
  rebuilds on each open so the list is always current. ArgumentException on
  Kill (PID already exited between menu-open and click) is the expected
  benign race — logged at Info, surfaced to balloon.

### Item 4: Smoke runner fast-fail pattern detection

`Core/TestAutoLoginRunner.cs` previously only counted SEH faults from
mq2_bridge.cpp. v3.22.24 fix6/fix7 introduced intentional fast-fail bail-
outs (`"bail on long-visible OkDialog"`, `"fast-fail detected post-BURST-2"`,
`"credentials likely rejected, fast-failing to retry"`) which the runner
missed. Now counted via a new `FastFailPatterns` array, reported
informationally (does not affect exit code — fast-fails are correct
behavior on bad-pass smokes).

- `Core/TestAutoLoginRunner.cs:50-72` — new `FastFailPatterns` array.
- `Core/TestAutoLoginRunner.cs:~220-260` — `CountSehInLog` refactored into
  thin wrapper over generic `CountPatternsInLog` helper; new
  `CountFastFailInLog` calls the same helper with `printLogExcerpt: false`
  so the log excerpt only prints once per run.
- `Core/TestAutoLoginRunner.cs:~155` — caller prints fast-fail count after
  SEH count.

### Fix-round-3 verifier-convergent fixes (2026-05-22)

Round-2 6-agent verifier surfaced two CRITICAL new gaps introduced by
fix-round-2:

1. **PopulateForceKillMenu still blocks the UI thread on hung clients.**
   Fix-round-2 swapped `MainWindowTitle` for `p.Responding` — but
   `Process.Responding` internally calls `SendMessageTimeout` (~5 s) per
   process. For N hung clients the menu opens after ~5N s. Fix-round-3
   uses `NativeMethods.IsHungAppWindow` (non-blocking Win32; returns
   instantly without any SendMessage roundtrip) gated on the cached
   `MainWindowHandle`. The bare `catch` is also replaced with
   `catch (Exception ex)` + `FileLogger.Warn` so any unexpected failure
   surfaces in eqswitch.log (per `reference_loud_runtime_silent_rest`).

2. **scan_eqgame_for_text.py `suspended` bool race.** Fix-round-2
   introduced `suspended = False` outside `try`, `suspended = True` after
   `suspend()`. A KeyboardInterrupt between `suspend()` returning and
   the bool assignment would leave the process frozen with `finally`
   skipping resume. Fix-round-3 makes `NtResumeProcess` UNCONDITIONAL —
   `STATUS_PROCESS_NOT_SUSPENDED` (0xC0000300) is the documented benign
   return when resuming an unsuspended target, so the race window
   collapses. Same fix in `inspect_hits.py`.

### Fix-round-4 verifier-convergent finalization (2026-05-22)

Round-3 6-agent verifier surfaced 1 REJECT (T2-Opus) + 1 CONCERN (T3-Opus)
convergent on **IsHungAppWindow 5s kernel-threshold latency** (false
negatives for hangs younger than 5 s — exactly the Force-Kill use case).
NativeMethods.cs:131-138 already prescribed the tighter probe for the
v3.22.22 ArrangeMultiMonitor work — Rule 7 said "match the existing
pattern."

Round-4 switches `PopulateForceKillMenu`'s hang detection from
`IsHungAppWindow` to `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 100ms)`,
matching the v3.22.22 convention. 100 ms × N hung clients vs the
unbounded 5 s false-negative window of `IsHungAppWindow`, and far
better than the original `p.Responding` 5 s × N. Also documents the
NT-suspend-count edge case in `scan_eqgame_for_text.py` docstring (do
not run while target is debugger-paused; an interrupted prior scan can
leave residual counts that this scan won't clear).

### Item 5: memory file amendment

`memory/reference_eqswitch_native_phase_error_triggers.md` previously
documented the 120 s native phase budget as the only fail-detection
mechanism. Two new sections added:

- "v3.22.24 update (2026-05-21) — C# fast-fail short-circuits native
  timeout" documents fix7 as the load-bearing mechanism, fix4 as
  best-effort.
- "v3.22.25 item 2 (2026-05-22) — text-extraction fundamentally bounded"
  records the memory-scan findings + closure of the structural-anchor
  approach.

## v3.22.24 — Bad-password fail detection in ~5s instead of 120s (2026-05-21)

Native-side fix to `eqmain_widgets_mq2style` / `login_state_machine`. The C# state
machine and the `widgetOkDialogVisible` SHM signal were already plumbed end-to-end
by v3.22.x — but the native widget probe never returned a non-null pointer for
the `okdialog` SIDL screen, so C# couldn't short-circuit on a Fatal `okClass`
and was stuck waiting out the 120s per-phase timeout on bad-password failures.

### Root cause

`FindLiveScreenByName` filters candidate top-level widgets by
`childCount >= MIN_CHILDREN_SCREEN` (=3) to reject leaf widgets whose tooltips
happen to mention a screen name. That's correct for full SIDL screens — the
`connect` screen has 16+ children, `serverselect` 6+ — but it falsely rejects
modal dialogs:

- Live-process probe of PID 21864 (sitting on the OK error dialog after a
  bad-pass `raistlin` smoke, 2026-05-21) walked CXWndManager.pWindows directly
  and found the okdialog widget at slot **[176]** @ `0x13766080`,
  `vt=eqmain+0x1052E4`, **dShow=1, children=1**, with the SIDL name `'okdialog'`
  stored at body offsets `+0x1DC`, `+0x1FC`, `+0x228`, `+0x22C`.
- `children=1` < `MIN_CHILDREN_SCREEN=3`, so the iteration's per-tick log showed
  `tooFewChildren=187 noName=18 result=null` on every probe — for 111 consecutive
  ticks per session.
- The matching SHM field `widgetOkDialogVisible` therefore stayed at `0`
  indefinitely, even with the dialog physically visible on Nate's screen.
- `AutoLoginManager.cs:1501`'s short-circuit
  `if (widgets.OkDialogVisible && okClass == OkDisplayClass.Fatal) → bail` was
  unreachable.

### Fix

Parameterized the child-count gate so dialog lookups can opt out cleanly:

- `Native/eqmain_widgets_mq2style.h`: `FindLiveScreenByName(name)` →
  `FindLiveScreenByName(name, int minChildren = 3)`. Default keeps every
  existing screen-finder call site bit-identical to v3.22.23 behavior.
- `Native/eqmain_widgets_mq2style.cpp`: thread `minChildren` through
  `FindScreenCtx` + `FindScreenIterCb` + the user-facing wrapper. DI8Log line
  now includes the chosen `minChildren` so log archeology can distinguish
  screen vs dialog walks.
- `Native/login_state_machine.cpp`: `ResolveCachedScreen(slot, name)` →
  `ResolveCachedScreen(slot, name, int minChildren = 3)`. The 3 dialog
  lookups (`okdialog`, `yesnodialog`, `ConfirmationDialogBox`) now pass
  `minChildren=0`; `connect` and `serverselect` keep the default.

No SHM layout change, no C# change, no public API removed.

### Verification

Live-process probe `_.eqswitch-re/audit-2026-05-21/probe_okdialog_search.py`
walked the manager's full pWindows array against PID 21864 and observed
exactly one top-level widget contains `'okdialog'` at any of those body
offsets in that PID snapshot. This is empirical evidence from a single
process, NOT a structural uniqueness proof — a future Dalaya patch or a
different process state could theoretically place the SIDL name CStrRep
in another widget's body. The bool-flagged downstream consumer
(`AutoLoginManager.cs:1501`) requires both `OkDialogVisible` AND a Fatal
`okClass` classification, so a false-positive screen match would still
need to also produce a CXMLDataPtr-style body that ReadWindowText can
parse into Fatal-classified text — a multi-condition failure surface.
`'yesnodialog'` matches similarly at slot [167] (hidden in that PID
snapshot, but layout identical).

Smoke contract for this release: native log `eqswitch-dinput8-{pid}.log`
must show `FindLiveScreenByName('okdialog', minChildren=0)` return non-null
within ~1 probe tick of the OK error dialog appearing; C# log must show
transition to terminal `Error` within ~5-10s of credentials submit on a
bad-pass smoke, replacing the prior 120s `PHASE_ERROR` budget exhaustion.

### Files

- `Native/eqmain_widgets_mq2style.h`
- `Native/eqmain_widgets_mq2style.cpp`
- `Native/login_state_machine.cpp`
- `EQSwitch.csproj` (3.22.23 → 3.22.24)
- `CHANGELOG.md`

Investigation artifacts (LOCAL-ONLY — `_.eqswitch-re/` is `.no-local` /
not checked in):
- `_.eqswitch-re/audit-2026-05-21/probe_okdialog_search.py`
- `_.eqswitch-re/audit-2026-05-21/okdialog-walk-PID21864.txt`
- `_.eqswitch-re/audit-2026-05-21/NEXT-SESSION-PROMPT.md` (handoff context)

### v3.22.24 fix2 — OK_Display text extraction via structural anchor (superseded by fix3)

First post-deploy smoke (PID 18296, 2026-05-21 16:28-16:29) confirmed
`widgetOkDialogVisible=1` reached the SHM and C# observer saw `ok=1` on
every tick — but `okClass=None text=""` repeatedly logged. Root cause: the
text-extraction probe (`PollOkDisplayToShm` → `DiscoverDialogWidgets`)
resolved `OK_Display` via legacy `MQ2Bridge::FindWindowByName`, which
returns CXMLDataPtr *definitions* on Dalaya (not live widgets — the same
problem that motivated the v3.22.24 dialog-discoverability fix one layer
up). So `g_pOkDisplay` was non-null but pointed at a def, `ReadWindowText`
returned empty, and the C# short-circuit
(`OkDialogVisible && okClass==Fatal`) never armed.

Fix2: `DiscoverDialogWidgets` switched to `FindLiveScreenByName("okdialog", 0)`
as the primary anchor with `RecurseAndFindName` for `OK_Display` +
`OK_OKButton`. Kept legacy `FindWindowByName` as fallback. **Not smoke-tested
before fix3 superseded it.**

### v3.22.24 fix7 — fix6 verifier-convergent refactor (2026-05-21)

8-agent verifier round on fix6 produced 4 convergent REJECT/CONCERNS findings.
All addressed before re-deploy:

1. **Static field → per-SM local (Gap-Opus + Code-Opus + Gap-Sonnet +
   Blast-Opus = 4 agents, REJECT verdicts).** fix6's
   `private static long s_okDialogFirstSeenMs` was shared across all
   concurrent SMs — a team1 launch fires two `Task.Run(() => RunLoginStateMachine(...))`
   per `BeginLogin`, so PID-B's SM-start reset (line 857) wiped PID-A's
   accumulated streak. Worse, each SM has its own `sw = Stopwatch.StartNew()`
   with its own time-origin — subtracting PID-B's `nowMs` from a
   `firstSeenMs` written by PID-A produces nonsense ages
   (potentially negative, potentially zero). Now: `long okDialogFirstSeenMs = -1`
   is a local in `RunLoginStateMachine` scope, passed by `ref` into
   `StepWaitConnectResponse`. Dies with the SM Task — no cross-SM sharing,
   no time-origin mismatch.

2. **Exclude Recoverable from the gate (Gap-Opus).** fix6's 12s gate fired
   regardless of `okClass`, which would hard-fail Dalaya peak-load
   recoverable dialogs (queue-position, server-busy, "please try again")
   that legitimately can stay up >12s. v3.22.23 behavior was to wait them
   out; fix7 preserves that — gate is now
   `widgets.OkDialogVisible && okClass != OkDisplayClass.Recoverable`.

3. **Remove torn-read reset (Gap-Opus).** fix6's `else { firstSeenMs = -1 }`
   on `OkDialogVisible == false` would wipe an accumulated streak on a
   single torn-read flicker (SHM is single-byte read; bool snapshot has
   no double-read defense like `okDisplayClass` does). The local var dies
   with the SM Task anyway so no cleanup is needed; dropping the reset
   makes the gate tolerant of single-tick noise.

4. **Extract 12000 to a named const (Code-Opus).** `OkDialogVisibleBudgetMs = 12_000`
   sibling of `OverallTimeoutMs`. Drift-trap prevention if Nate ever tunes
   the budget.

Also fixed: stale XML-doc comment claiming "Cleared on every successful login
(terminal CharSelect / InWorld resets it via SM teardown)" — neither was
true in fix6 (the only reset was at SM dispatch entry). Comment removed
along with the static field.

Not addressed in v3.22.24 (post-ship cleanup):
- `tools/capture-native-debug.py` / `TestAutoLoginRunner.cs` SehPatterns don't
  grep for the new "bail on long-visible OkDialog" line — telemetry pickup
  gap, not a behavior gap.
- `memory/reference_eqswitch_native_phase_error_triggers.md` documents the
  120s native phase budget; needs an amendment noting fix7's ~22s C#-side
  fast-fail path. Will land in `/save` step 5.5 auto-update.

### v3.22.24 fix6 — C# fast-fail-on-visible-only (Nate, 2026-05-21)

Fix5's native side delivered reliable `widgetOkDialogVisible=1` on every PID
but the +0x30C structural anchor for the dialog text is PID-unstable —
worked on PID 33768, returned NULL on PID 24400 (Dalaya stores dialog text
in different widgets per-PID or per-dialog-instance; heap scan finds 5
heap copies of the live text but no walkable widget pointer at any of
them). Without reliable text classification, the existing C#
`okClass == Fatal` short-circuit at AutoLoginManager.cs:1501 stays
unreachable and the SM waits the full 120s native phase timeout.

Per Nate's framing: **OkDialogVisible IS the "didn't reach serverselect"
signal** — the OK error dialog appears precisely when the login server
rejected credentials, and its presence blocks the connect→serverselect
transition that defines success. Same gate, just with a more reliable
trigger than text classification.

Fix: AutoLoginManager.cs adds a visibility-age tracker. Whenever
`widgets.OkDialogVisible` is true, we record the SM elapsed-ms of first
sighting in a static field. If the dialog stays visible for >12 seconds
without okClass-based bail firing first, the SM transitions to terminal
Error regardless of classification. The 12s budget is safe against the
138s slow-Dalaya case (CLAUDE.md AUTOLOGIN SPEC) because slow-server
delays do NOT produce an OK dialog — they're upstream connect-pending
states with no rejection signaled.

Net effect: bad-password autologin now reaches terminal Error at ~22s
SM-total (12s after dialog appears at ~10s into WaitConnectResponse)
instead of the prior 120s native phase-timeout. The v3.22.23 Settings
→ Accounts Flag column (✗ on fail, ✓ on success, — when not fired)
benefits directly — fail-marking now persists at ~22s vs ~130s.

Changes:
- `Core/AutoLoginManager.cs`: `StepWaitConnectResponse` takes new
  `long nowMs` parameter; static `s_okDialogFirstSeenMs` tracks first
  visible-tick timestamp (reset to -1 on `else` branch when dialog
  becomes invisible, and explicitly at SM-start in RunLoginSequence
  to prevent state bleed across multiple SM runs in the same process).
- `Core/AutoLoginManager.cs`: tick-dispatch at line 1115 passes
  `sw.ElapsedMilliseconds` to the new step signature.

No native change. No SHM layout change. Doesn't touch the existing
okClass=Fatal short-circuit — that still wins when text extraction
succeeds (~1s detection on PIDs where +0x30C is populated). The
visibility gate is a safety net for the text-extraction-fails case.

### v3.22.24 fix5 — 8-agent verifier convergent cleanup (2026-05-21, pre-final-smoke)

8 verifier agents (4 topics × Sonnet+Opus) on fix4 surfaced 5 convergent
findings — all addressed before the next smoke:

1. **Line 1322 stale `MQ2Bridge::ReadWindowText` (5 of 8 agents).** The
   always-on `PollOkDisplayToShm` was migrated to `ReadDalayaDialogText` in
   fix4 but the in-phase `PHASE_WAIT_CONNECT_RESP` handler at line 1322
   still called the legacy reader on the same `g_pOkDisplay` pointer. The
   legacy reader returns NULL on the Dalaya-repurposed text widget, so the
   in-phase `SetError` + recoverable-retry branches were dead-pathed on
   Dalaya. Currently dormant (PATH B uses PHASE_IDLE) but would silently
   break if PATH A is reanimated. Now uses `ReadDalayaDialogText`.

2. **g_cachedMain cache slot (Code-Sonnet + Code-Opus + Gap-Opus).**
   Pre-fix5 `DiscoverDialogWidgets` called `FindLiveScreenByName("main", 3)`
   every Tick — an uncached ~205-node `pWindows` walk. Same starvation
   pattern v3.21.0-fix-1 was created to fix for the 5 already-cached
   screens. Now uses `ResolveCachedScreen(&g_cachedMain, "main", 3)`,
   sharing the existing cache infrastructure (including eqmain-reload
   invalidation in `PollWidgetVisibilityToShm`).

3. **okdialog visibility gate on g_pOkDisplay (Gap-Sonnet + Gap-Opus).**
   The structural slot `main+0x25C` points at a Dalaya-repurposed HELP
   button widget that ALSO exists during normal login (no dialog active).
   Reading its `+0x30C` CXStr unconditionally could classify residual HELP
   popup text as Fatal and abort a valid in-progress login. Fixed: gate
   the entire `g_pOkDisplay` resolution on `ResolveCachedScreen("okdialog", 0)`
   returning a visible widget (via `IsCXWndVisible`). Zero-cost on the
   common path (cache hit).

4. **Magic numbers → constexpr (Code-Opus).** `0x25C`, `0x30C`, `0x14`,
   `0x08`, `4096` extracted to `DALAYA_MAIN_TEXT_WIDGET_SLOT_OFF`,
   `DALAYA_OKDISPLAY_TEXT_CXSTR_OFF`, `DALAYA_CXSTR_BUFFER_OFF`,
   `DALAYA_CXSTR_LENGTH_OFF`, `DALAYA_DIALOG_TEXT_MAX_LEN`. Same drift-trap
   prevention that motivated removing `MIN_CHILDREN_SCREEN` in fix3.

5. **Log structural-slot misses + soften "CSTML" speculation
   (Code-Opus + Gap-Sonnet + Gap-Opus).** When `pTextWidget = *(main+0x25C)`
   fails the `IsEQMainWidget` gate, log once so a future Dalaya layout
   shift is diagnosable from logs alone. CHANGELOG + code comments no
   longer claim the 4-byte buffer prefix is definitely CSTML markup —
   only that it's an observed-as-NUL prefix that NUL-stripping handles
   correctly.

**Not addressed (advisory, lower priority):**
- OK_OKButton recurse may miss (Code-Opus, single-agent): the OK button's
  live CStrRep label is just "OK" not "OK_OKButton", so
  `RecurseAndFindName(okdialog, "OK_OKButton")` may return null on Dalaya,
  making the recoverable-dialog auto-click dormant. PATH A re-enablement
  task — addressed in v3.22.25 alongside other PATH A reanimation.
- TestAutoLoginRunner SehPatterns gap (Blast-Sonnet): smoke runner watches
  `"SEH in ReadWindowText"` log line; ReadDalayaDialogText uses different
  prefix. Update SehPatterns when convenient.
- Probe `rc=0` rejection inconsistency (Gap-Sonnet + Gap-Opus): dev-tool
  probes reject rc=0 CStrReps but the production code accepts them. Update
  probe validators when next-iterating on those tools.
- LoginShmWriter.cs stale "ReadWindowText returned empty" doc comments
  (Blast-Opus): doc rot, no behavior change.
- DALAYA_DIALOG_TEXT_MAX_LEN=4096 cap (Gap-Opus): currently fits the
  observed 877-char message comfortably. If Dalaya ships longer error
  messages they'll be truncated — log warning + bump cap when seen.

### v3.22.24 fix4 — Dalaya-structural OK_Display anchor (2026-05-21, post-smoke)

Fix3 (drop fallback + scanBytes=0x400) was based on a verifier hypothesis
(OK_Display name CStrRep beyond +0x200) that turned out to be wrong. The
fix3 smoke (PID 33768, 2026-05-21 16:50) confirmed `widgetOkDialogVisible=1`
reached SHM as designed but the C# observer logged `okClass=None text=""`
for the entire 128s window before the native phase-timeout fired at 128.7s.

Root cause (confirmed via two new live-process probes,
`_.eqswitch-re/audit-2026-05-21/probe_okbox_dump.py` and a follow-up heap
scan for the literal "not valid" string):

1. **OK_Display is NOT a child of okdialog on Dalaya.** The okdialog widget
   @ slot [176] has exactly ONE child (OK_Box), whose children are an OK
   button (label="OK" at +0x1A8) and a non-text widget (zero CXStr fields
   in body). Neither contains the dialog message.
2. **The actual displayed text lives in main screen's subtree.** The
   `main` widget (pWindows[12] @ 0x116F17E0) has TWO structural pointer
   slots at `+0x25C` and `+0x4E0`, both pointing to a child widget @
   0x116F1F20. That widget is a Dalaya-repurposed CButtonWnd-class (label
   "HELP" at +0x1A8) whose `+0x30C` field is a CXStr* holding the live
   877-character dialog text ("Error - The username and/or password were
   not valid... Please Note: Until now EverQuest passwords could differ...").
3. **The CXStr uses a non-standard refCount=0 layout.** Dalaya's dialog
   text is stored as a transient/ownerless CStrRep (rc=0, alloc=0x400,
   length=877). The text buffer starts at the standard `+0x14` offset
   but with a 4-byte CSTML markup prefix (`00 00 00 00`) before the
   visible characters — the renderer skips it, we strip it.

Match-MQ2 framing: MQ2's actual API on x86 RoF2 is
`CXWnd::GetChildItem(PCHAR name)` which the open MQ2 source
(`_.src/_srcexamples/mq2emu-rof2-x86/MQ2Main/EQClasses.cpp`) implements
as literally `return RecurseAndFindName(this, Name)` — the SAME algorithm
we already had in `eqmain_widgets_mq2style::RecurseAndFindName`. A strict
RVA-pinned port would behave identically on Dalaya: return null, because
OK_Display isn't in okdialog's runtime sibling chain. The Dalaya-structural
anchor is the only path that actually delivers the text on this server,
mirroring the precedent of v3.20.5 (ConnectButton via fixed ConnectWnd
slot) and v3.22.x (password edit via ConnectWnd+0x44).

Changes:
- `Native/login_state_machine.cpp`: `DiscoverDialogWidgets` now anchors
  `g_pOkDisplay` on `main+0x25C` (validated via `IsEQMainWidget`). OK button
  discovery via `RecurseAndFindName(okdialog, "OK_OKButton", 0x400)` kept —
  the OK button IS a child of okdialog (dialog frame owns its click target).
- `Native/login_state_machine.cpp`: new `ReadDalayaDialogText` helper reads
  the CXStr at `pTextWidget+0x30C`, handles rc=0 CStrRep layout, strips
  leading NUL prefix. Replaces `MQ2Bridge::ReadWindowText` in
  `PollOkDisplayToShm` for this widget class.
- No SHM layout change; existing `okDisplayText` / `okDisplayClass` fields
  and C# `OkClass::Fatal` short-circuit at `AutoLoginManager.cs:1501` are
  the consumers.

Verification contract: `eqswitch-dinput8-{pid}.log` should now show non-empty
text written to SHM via `SetOkDisplay`. C# log should transition
`WaitConnectResponse → Error` within ~5–10s of credentials submit (well
before the 120s native phase-timeout), with `okDisplay at terminal Error:
class=Fatal text="...password were not valid..."`.

### v3.22.24 fix3 — verifier-convergent cleanup (2026-05-21, pre-smoke)

8-agent pair-verifier round (Sonnet+Opus on 4 topics) on the fix2 state
surfaced 4 convergent issues. Addressed before re-deploy:

1. **`RecurseAndFindName` scan-window mismatch (Gap-Sonnet C2, Code-Sonnet
   LOW).** The recurse used `SCAN_BYTES_WIDGET=0x200` for every node,
   but the okdialog widget itself stores its name CStrRep at body offsets
   up to `+0x22C` (per the PID 21864 probe). If `OK_Display` follows the
   same layout pattern as its parent, `RecurseAndFindName` would silently
   miss it under 0x200 — the most likely cause of fix2's predicted-but-
   not-validated `text=""` symptom. Parameterized: `RecurseAndFindName`
   and `FindChildByName` now take `uint32_t scanBytes = 0x200`. Dialog
   callers in `DiscoverDialogWidgets` pass `0x400`.
2. **Legacy `FindWindowByName` fallback poisons `g_pOkDisplay`
   (Gap-Opus #3 + #5, Code-Opus #4, Gap-Sonnet M3, Code-Sonnet LOW (a)).**
   On Dalaya `FindWindowByName` returns CXMLDataPtr *definitions* — passing
   one to `ReadWindowText` either SEH-faults or returns garbage. Dropped
   the fallback entirely. On structural miss `g_pOkDisplay` stays null and
   `PollOkDisplayToShm` clears the SHM cleanly via `ClearOkDisplay`.
3. **Dead `MIN_CHILDREN_SCREEN` constant (Code-Opus #9, Code-Sonnet LOW
   (d)).** Removed from `eqmain_widgets_mq2style.cpp`. The header default
   on `FindLiveScreenByName(name, int minChildren = 3)` is now the single
   source of truth.
4. **CHANGELOG overclaim on false-positive elimination (Gap-Opus #1,
   Code-Opus #3, Gap-Sonnet M1, Code-Sonnet MED #2).** Softened from
   "eliminating false-positive risk" to "empirical evidence from a single
   process snapshot" with a note that the C#-side `OkClass==Fatal` gate
   provides defense-in-depth even on a false-positive screen match.

**Not addressed (advisory, low-impact):**
- `ResolveCachedScreen` cache key doesn't include `minChildren` — in
  practice each name has one minChildren value, so this is theoretical.
- `DiscoverDialogWidgets` runs the structural walk every tick without
  its own cache — perf nit only; tick is 500ms not the 16ms BURST hot
  path that drove v3.21.0-fix-1. Will revisit if logs show starvation.
- `probe_okdialog_search.py` hardcodes the CXWndManager VA (dev tool
  only; not shipped; ASLR will require re-probing for a different PID).
- `dist/SHA256SUMS` references the v3.22.23 zip (CI regenerates on tag).

## v3.22.23 — X-flag orphan-reference race fix (2026-05-21)

Single-file C# fix in `Core/AutoLoginManager.cs`. No native change, no behavior
change for successful logins. Bumps reliability of the Settings → Accounts tab
"Flag" column when the user opens Settings during a long-running autologin.

### Bug fixed

The autologin "Flag" column (✓ / ✗ / —) failed to persist a `✗` outcome when
the user clicked Apply or OK in the Settings form while an autologin was still
running. The bug:

1. `SettingsForm.ApplySettings` rebuilds `_config.Accounts` from `_pendingAccounts`,
   constructing **new** Account instances each time — by design, so unsaved
   user edits never mutate the live config.
2. `AutoLoginManager.RunLoginSequence` captures a reference to the live Account
   at the start of the sequence (up to ~3 minutes for a fail-timeout path).
3. If Apply/OK runs anywhere in that window, the SM's captured reference
   becomes an orphan — the new list contains a fresh Account object with the
   same fields, but our reference points outside the serialized graph.
4. The SM's finally block runs `account.LastLoginResult = "fail"; SaveImmediate(_config)`.
   The mutation lands on the orphan. `SaveImmediate` serializes the new list,
   which still has `LastLoginResult = ""`. The log claims the save fired
   (because it did — just not against the right object).

Reproducer: 2026-05-21 — bad-password smoke on a test account. C# log
showed `deferred SaveImmediate(fail) for <account> at SM-exit` at 10:08:56.
On-disk `eqswitch-config.json` showed
`accountsV4[<account>].lastLoginResult: ""`. The intervening
`Settings applied` line at 10:08:42 was the orphan trigger.

### Fix

`AutoLoginManager.cs:1277-1340` — before mutating the captured `account`
reference, re-resolve to the live Account via the same `(Username, Server)`
case-insensitive match that `SettingsForm.OnLoginComplete` uses for its
reverse-direction sync. Three observable outcomes:

- **Common case** (no Settings save during autologin): `FirstOrDefault`
  finds the same object as `account`. Re-resolve is a no-op. Save fires
  against the live list. No new log line.
- **Orphan-reference race** (Settings.Apply ran mid-autologin, list
  replaced with new Account instances): `FirstOrDefault` finds a different
  object with the same (Username, Server). Mutate the live AND mirror onto
  the orphan so any LoginComplete subscriber holding the captured ref also
  sees a coherent state. Log `re-resolved orphan Account ref` (Info).
- **Null-fallback** (account renamed or removed in Settings mid-autologin):
  `FirstOrDefault` returns null, `?? account` falls back to the orphan.
  Mutation lands outside `_config.Accounts` and `SaveImmediate` won't
  persist it. Log a `Warn` with the missing (Username, Server) — flag
  loss is unavoidable without a lock around the (re-resolve +
  ApplySettings list-replace) pair (backlogged for v3.22.24), but at
  least the failure surface is loud.

Snapshot `_config.Accounts` to a local before `FirstOrDefault` to force
a single field read — guards against JIT register-caching a stale list
reference across the call. `Thread.MemoryBarrier()` between the
`LastLoginAt` and `LastLoginResult` writes enforces store-store order
(plain C# field writes can be reordered by the JIT even on x64; the
CLR memory model doesn't promise free ordering between two non-volatile
writes), which keeps the UI-thread reader invariant: any reader seeing
a non-default `LastLoginResult` also sees the matching `LastLoginAt`.

### Out of scope

- The deeper question of whether `SettingsForm.ApplySettings` should be
  blocked during an in-flight autologin (cleaner architectural fix, larger
  blast radius). Backlogged.
- The 2026-05-21 Edge YES-redirect-to-login investigation — separate audit
  at `X:/_Projects/_.eqswitch-re/audit-2026-05-21/dalaya-dinput8-audit.md`.
  Tracking a "Force-Kill Stuck Client" tray menu for a future point release,
  not in v3.22.23.

## v3.22.22 — Path A partial-population gate (autologin reliability) (2026-05-20)

Closes the Thread 1 bug surfaced by the 2026-05-20 v3.22.21 smoke. Native-only
change in `mq2_bridge.cpp` — no C# orchestration, no hook-DLL churn, no
windowing changes.

### Bug fixed

1. **CharSelectShm partial publish on fast CharSelect transitions** —
   `MQ2Bridge::Poll`'s Path A and `MQ2Bridge::PopulateCharacterData` both
   gated only `entry[0]` plausibility (the P8 gate, v3.22.3). They then
   trusted `arr->Count` and looped through all `count` entries; any entry
   that failed `IsPlausibleName` was silently emitted as `""` to the SHM
   while `charCount` was published as `count` and `charSelectReady` was
   latched to 1.

   On the 2026-05-20 PID 30192 smoke, `CharSelect` transitioned 262 ms
   after burst start (vs the 1,783 ms observed on the simultaneous
   successful PID 13308). At that moment EQ had populated entries[0..4]
   of `charSelectPlayerArray` with the first five received characters
   but had not yet written entries[5..9]. Path A's per-entry letter
   filter collapsed those five zero-name entries to empty strings, the
   final `shm->charCount = count` published 10, and the C# autologin
   state machine read 10 names with the target slot ("Backup") missing.
   Decide returned slot 0, the safety abort #1 (`resolvedSlot == 0`)
   fired, and the SM correctly halted at char select. Result: account
   stuck at char select, no enter-world. Other accounts on the same
   burst succeeded because their `CharSelect` transition was slow
   enough that EQ had finished writing the trailing entries by the
   first poll.

   Both publish sites now pre-validate every entry [0..count-1] with
   `IsPlausibleName` before any SHM write. A partial population (any
   entry beyond [0] failing the predicate) bails to next-poll retry —
   same semantics as the existing P8 gate failure on entry[0]
   (`charDataRead` stays false, Path B's UI fallback runs this tick,
   the next 500 ms Path A poll retries). The bail is logged once per
   charselect cycle via the new `g_partialPopLogged` flag (reset
   alongside `g_p8GateLogged` at charselect transition / gameState=5 /
   `Shutdown()`) so a slow-populating account is diagnosable without
   spam.

### Round 6 — Probe at the leaf `ApplySlimTitlebar` + `ResizeToCurrentMonitors` (R5 T2 Sonnet+Opus CRITICAL convergence)

The R5 8-agent sweep found that round-5's probe coverage was still
incomplete — Sonnet and Opus T2 verifiers converged on three CRITICAL
gaps that all share the same shape: a cross-process
`SetWindowLongPtr` / `SetWindowPos` reachable without a probe.

- **R5 T2 C1 / G2** — `ResizeToCurrentMonitors` (`WindowManager.cs`
  L636-677) iterates clients and calls `SetWindowPos` with
  `SWP_FRAMECHANGED`. Called immediately after `SwapWindows` from
  `TrayManager.cs:1940`. A client could enter zone-load DX reset in
  the millisecond gap between `SwapWindows`' probe and the
  `ResizeToCurrentMonitors` SetWindowPos.
- **R5 T2 C2 / G1** — `ApplySlimTitlebarToAll` (`WindowManager.cs`
  L685-711) is the slim-titlebar guard-timer callback (fires every
  500 ms-5 s). It calls `ApplySlimTitlebar` directly, which does
  `SetWindowLongPtr` + two `SetWindowPos` calls — the exact stall
  primitive that crashed PID 24672.
- **R5 T2 C3 / G3** — `TrayManager.cs:404` (`ClientDiscovered`
  single-screen branch) and `TrayManager.cs:209`
  (`ApplyDeferredCosmetics` single-screen BURST 1 branch) both call
  `ApplySlimTitlebar` directly, bypassing `ArrangeSingleScreen`'s
  probe. The round-5 CHANGELOG claim that "single-monitor BURST 1 is
  also covered" was therefore false for these two paths.

Round-6 fix: add the `IsClientResponsive` probe at the leaf method —
`ApplySlimTitlebar(IntPtr hwnd, ...)` (line 719). This single addition
covers all callers in one place: direct `ApplySlimTitlebar` calls from
TrayManager (2 sites), the guard-timer loop in
`ApplySlimTitlebarToAll`, `ArrangeSingleScreen`, and indirectly
`ArrangeMultiMonitor`'s per-client work (already probed at the loop
entry — leaf probe is belt-and-suspenders). Plus the equivalent probe
inside the `ResizeToCurrentMonitors` per-client loop. Skipping a non-
responsive HWND means a brief miss of the slim-titlebar style — the
guard timer re-fires every 500 ms and will re-apply once the pump
recovers.

R5 T3 Sonnet+Opus convergent MEDIUM (helper bypasses `IWindowsApi`
testability abstraction) is documented in the v3.22.23 backlog —
correctness is unaffected, only unit-test mockability.

### Round 5 — Extend Thread 3 probe to ArrangeSingleScreen + SwapWindows + add SMTO_BLOCK (R4 verifier convergence)

The R4 8-agent sweep flagged that the round-4 probe was scoped only to
`ArrangeMultiMonitor`. Two convergent CRITICAL gaps (R4 T2 Sonnet+Opus):

- **G1** — `ArrangeSingleScreen` (`WindowManager.cs:122-165`) had only the
  `IsHungAppWindow` check. Single-monitor users hit the same 14.5 s
  pass-1 stall when `LoginComplete` fires with a client mid-zone-load.
- **G3** — `SwapWindows` (`WindowManager.cs:540`) had only
  `IsHungAppWindow`. Any user-triggered `SwitchKey` rotation during a
  teammate's zone-load could stall `GetWindowRect` /
  `BeginDeferWindowPos` for the same 14.5 s window and crash EQ.

Plus one R4 T3-Opus MEDIUM (preferred over T3-Sonnet who disagreed —
per verifier-spec "trust the flagger"):

- `SMTO_ABORTIFHUNG` without `SMTO_BLOCK` allows the calling UI thread
  to accept reentrant sent-messages during the 100 ms probe wait. The
  `SMTO_BLOCK` constant was already imported in round-4 but unused.

Plus one R4 T3-Sonnet SEV-1:

- LoginComplete handler's deferred-timer `Stop()/Dispose()` were inside
  the `try` block. If `Stop()` somehow threw (defensive — never observed),
  `Dispose()` would be skipped and the timer would refire forever. Moved
  before the `try`.

Round-5 changes:

1. Extracted the probe into a shared `IsClientResponsive(IntPtr hwnd)`
   private static helper in `WindowManager` so all per-client iteration
   sites share the same predicate.
2. Added the probe to `ArrangeSingleScreen` (after `IsHungAppWindow`)
   and `SwapWindows` (alongside its existing per-client hung check).
3. Added `SMTO_BLOCK` to the probe flags in the shared helper.
4. Moved `deferTimer.Stop()/Dispose()` before the `try` in the
   LoginComplete handler.

The R4 T4 documentation drift (MEMORY.md / backlog memo still describing
Thread 3 as "deferred to v3.22.23") is addressed in the same commit.
C#-only — no native DLL change.

### Round 4 — Thread 3: ApplyDeferredCosmetics zone-load crash (production smoke 2026-05-20 15:12)

The v3.22.22 round-3 build smoke confirmed Thread 1 fix in production
(PID 25376 partial-pop → P9 widened-fire → cache invalidate → next-poll
clean publish → 'Backup' resolved → in-game), but surfaced Thread 3
as a hard crash:

```
[15:12:21.684] [WARN] ArrangeMultiMonitor: DeferWindowPos failed mid-batch — sequential fallback
[15:12:21.746] [INFO] ArrangeMultiMonitor: pass1=14471ms pass2=65ms     ← 14.5s freeze
[15:12:21.747] [INFO] ArrangeMultiMonitor: pass2-stages [14472,14474,14474,14536]ms
[15:12:21.827] [INFO] HookConfigWriter: closed mapping for PID 24672    ← Natedogg crashed
```

PID 24672 (Natedogg, main-monitor) hit LoginComplete at 15:12:06.864,
which triggered `ApplyDeferredCosmetics(24672)` → `ArrangeWindows` over
all clients. EQ's Enter World event simultaneously kicked off the
zone-load DX device reset, which blocks the message pump for 5–15 s.
Pass-1's `SetWindowLongPtr(client.WindowHandle, ...)` on PID 24672
blocked 14,471 ms because PID 24672's own pump was the one stalled.
`IsHungAppWindow` returned false (its 5 s kernel threshold hadn't
elapsed when the loop entered) so the existing hung-skip filter didn't
catch it. The 14.5 s stall corrupted PID 24672's DX reconfig and EQ
crashed.

Two-layer defense:

1. **`WindowManager.ArrangeMultiMonitor`** — added a
   `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 100 ms)` probe after
   the existing `IsHungAppWindow` check. Tighter than `IsHungAppWindow`
   (100 ms timeout vs 5 s kernel threshold) so transient mid-zone-load
   pump blocks fast-fail instead of stalling pass-1. Skipped clients
   are logged loudly.
2. **`TrayManager.LoginComplete` handler** — defer the safety-net
   re-arrange by 15 s via `System.Windows.Forms.Timer`. Enter World
   triggers a 5–15 s DX swap-chain reconfig where the window's pump is
   blocked; firing `ApplyDeferredCosmetics` immediately puts us inside
   that window. BURST 1's arrange already happened at T+7 s on login
   screen (before Enter World), so this LoginComplete safety net only
   matters if EQ fought back during the charselect-load transition. A
   15 s wait is imperceptible and gives the zone-load reconfig room
   to finish before we touch the window again.

Together (1) eliminates the race and (2) defense-in-depth catches any
remaining unresponsive-pump case (multi-client overlap, manual hotkey
arrange, etc.). C#-only change — no native DLL rebuild required.

### Round 3 — Path C charSelectReady latch deferred to P9 (verifier R2 T2 Sonnet+Opus + T3-Sonnet SEV-1 convergence)

The round-2 re-verifier sweep converged on a load-bearing follow-up. Path C
latched `shm->charSelectReady = 1` at line ~4201 (heap-scan) and ~4279
(anchor-scan) **inside its own `__try`, before the widened P9 publisher
gate ran**. If Path C's `continue`-on-failure loop (line 4185) wrote
interior holes (entry[0]=real, entry[5]=""), P9 correctly bailed on the
`charCount` publish — but `charSelectReady` was already latched. C#
`AutoLoginManager.cs:1587` reads `ReadCharCount(pid) > 0 ||
IsCharSelectReady(pid)` with OR semantics, so a stale latch was enough
to push the SM out of its wait loop with `charCount=0`. The 4-retry
republish wait at line ~1620 mostly hides this (2 seconds for Path A to
self-heal), but in the pathological case the SM would advance with an
empty `charNames[]`.

Round-3 makes the publish atomic: `shm->charSelectReady = 1` moves from
Path C's two latch sites into P9's `if (allPlausible)` success block,
mirroring Path A's already-atomic latch+publish at L3974-3975. Single
source of truth for the Path B+C combined publisher chain. Standalone
heap-scan / standalone anchor / Path A keep their own atomic latches
(those publish independently, never go through P9).

Round-3 also addresses R2 T3-Sonnet SEV-1: the widened-fire branch
(`firstBadIdx > 0`) now invalidates the heap-scan cache
(`g_heapScanArrayBase = 0; g_heapScanDone = false; g_lastHeapScanAddr = 0`)
so Path C re-runs `HeapScanForCharArray` on the next 500 ms poll.
Without this, Path C would only fall through to the re-read at L4295
using the same stale base — which would keep hitting the same hole.
Matches the existing heap-cache-stale invalidation pattern in the
`validated < count` block (`Native/mq2_bridge.cpp` heap-scan re-read).

R3 verifier-flagged follow-ups deferred to v3.22.23:
(a) extend the cache-invalidate to the P9 SEH `__except` path (R3
T2-Opus Q5) — defense in depth for the rare DLL-detach race; current
behavior is loud-log without invalidate, low-impact;
(b) split `g_partialPopLogged` into per-path flags so Path A bail and
P9 widened-fire don't cross-suppress diagnostics within the same
charselect cycle (telemetry-only, both paths bail correctly regardless);
(c) hoist `int i` outside the P9 `__try` so the SEH log can report the
last-safe index instead of `shm->names[0]` (R2/R3 T3 minor).

### Round 2 — P9 gate widened to all entries (verifier T2 Sonnet+Opus convergence)

The round-1 fix above closed Path A and `PopulateCharacterData`. The 8-agent
verifier sweep flagged a structurally identical bug in Path C's heap-scan
write loop (`Native/mq2_bridge.cpp:4185`): when a per-entry `IsPlausibleName`
check fails, the loop zero-fills `shm->names[i]` and **continues iterating**,
which can leave interior holes (entry[0]=real, entry[5]=""). The v3.22.4 P9
publisher gate (line ~4358) only validated `shm->names[0]`, so it passed any
hole that wasn't at index 0 and published `charCount=count` with the gap.

Round-2 widens the P9 gate to validate every entry in `[0..count-1]`.
Same predicate (`IsPlausibleName`), same one-shot diagnostic (logged via
the new `g_partialPopLogged` flag when `firstBadIdx > 0`, or the original
`g_p9GateLogged` flag when `firstBadIdx == 0` to preserve diagnostic
continuity with v3.22.4's "entry[0] is placeholder or empty" telemetry).
No changes to Path C's internals — keeping the fix localized to the
publisher gate avoids touching the heap-scan/anchor-scan interactions
with Path B2's slot-mode synthesis.

### Notes

- Path B (`Poll()` UI fallback, line ~3921+) already had correct
  break-on-empty behavior at its write loop — it only publishes the
  contiguous-populated-rows count, so it does not need the gate. A
  theoretical interior-hole scenario (UI rows [0,1,2,_,4,5]) is not
  observed empirically; the round-2 P9 widening would catch it
  defensively anyway.
- The standalone heap-scan write loop (line ~4438) already uses
  `break`-on-empty correctly — same pattern as Path B, only writes
  contiguous validated entries.
- The per-entry letter filter inside `PopulateCharacterData`'s write
  loop is now redundant for the populated case (the pre-gate already
  rejected anything non-plausible) but is left in place as defense in
  depth for predicate parity with `Poll()` Path A's post-gate
  straight-memcpy.
- Native DLL only: `eqswitch-di8.dll` rebuilt. C# binary changes only
  to reflect the bumped `<Version>3.22.22</Version>` in `EQSwitch.csproj`.
- v3.22.21 deferred items (taskbar flicker, ApplyDeferredCosmetics
  2308 ms pass2, direct `CResolutionHandler::ToggleScreenMode` Ghidra
  hunt) remain open and tracked in
  `reference_eqswitch_v3_22_22_backlog.md`. Isolating Thread 1 here
  keeps autologin reliability bisectable from any subsequent windowing
  change.
- Deferred to a future patch (verifier-surfaced, not blocking v3.22.22):
  (a) audit `kBadNames` blocklist (line ~224+) for legitimate-name
  collisions — entries like `"Hunter"`, `"Shadow"`, `"Storm"`, `"Swift"`,
  `"Scout"` could permanently bail Path A for accounts that include a
  character with one of these names; (b) consider a 5 s rate-limit on
  `g_partialPopLogged` similar to v3.22.16's column-discovery failure
  log so slow-populating accounts emit ~6 lines over 30 s rather than
  one-and-done.

## v3.22.21 — PID-recycle dupe-slot fix + swap-flicker batch + lock-to-primary-dims + Fix-Windows DX reinit (2026-05-19)

Hardens the v3.22.20 multi-monitor system against the three convergent verifier
findings from the 6-agent sweep, plus the two visible symptoms Nate caught in
smoke (swap flicker + cross-monitor font smoosh). No autologin or hook-DLL
churn — pure C# orchestration changes.

### Bugs fixed

1. **PID-recycle duplicate-slot** (CRITICAL — 4-way verifier convergence:
   T2-Sonnet, T2-Opus, T3-Sonnet, T3-Opus). The v3.22.20 assignment
   `int slot = _monitorSlotByPid.Count` collided whenever a client died and a
   new one launched. Example: PIDs A→0, B→1, Count=2. PID A dies →
   map={B→1}, Count=1. New PID C → gets slot=1 → collides with PID B. Both
   end up on the same monitor with one window stacked atop the other. Fixed
   by `AssignNextFreeSlot(pid, source)` which scans `[0, monitorCount)` for
   the first slot not in `_monitorSlotByPid.Values`. Three call sites
   updated: `ClientDiscovered`, `OnToggleMultiMonitor` backfill,
   `ReloadConfig` backfill.

2. **SwitchKey swap flicker** — `ApplySlimTitlebar` + the non-slim
   `SetWindowPos` ran sequentially per client, so DWM composited each
   transition individually. Taskbar peek-through was briefly visible between
   moves. Fixed by splitting `ArrangeMultiMonitor` into two passes:
   **pass 1** (sequential) does style work — `WS_THICKFRAME` strip/restore
   plus the `SWP_FRAMECHANGED` notify (visually non-disruptive — no move);
   **pass 2** (batched via `BeginDeferWindowPos` / `DeferWindowPos` × N /
   `EndDeferWindowPos`) emits the actual position+size commands in a single
   DWM composite. Same pattern `SwapWindows` already used at
   `WindowManager.cs` L308-333. `ApplySlimTitlebar`'s 2-step
   focus-loss-prevention stays intact — step 2 fires in pass 1 sequentially,
   step 3 (the move) fires in pass 2 batched. Sequential `SetWindowPos`
   fallback kept for the rare case `BeginDeferWindowPos` or
   `DeferWindowPos` fails mid-batch.

3. **Cross-monitor font/UI smoosh** — when a client initialized its DX
   device at monitor A's dimensions then SwitchKey-moved to monitor B's
   (different) dimensions, the DX swap-chain didn't re-init and textures
   stretched at the new aspect ratio. Manual fix was double-clicking the
   titlebar 2× (cycles windowed-fullscreen ↔ windowed, forces DX reinit).
   Fixed structurally by **lock-to-primary-dims**: both windows sized to
   primary's `Width × Height` with each monitor's own origin. DX backbuffer
   stays at one constant size across SwitchKey swaps → no reinit, no
   smoosh. Auto-degrades to per-monitor-fit when monitors differ too much
   (|Δ| > 200px on either axis) OR primary doesn't fit within secondary
   OR slim flags differ — power users with 4K + 1080p configs get
   per-monitor-fit + the new `Fix Windows` recovery hatch (#4).

### New features

4. **`Fix Windows` (hotkey + tray menu) now forces DX reinit** on every
   non-autologin, non-iconic client, all client counts, all modes
   (single-screen + multi-monitor). Mechanism:
   `PostMessage(WM_NCLBUTTONDBLCLK, HTCAPTION, lParam=titlebar-coords)`
   twice per client with ~250ms between via `System.Windows.Forms.Timer`
   (no UI-thread `Thread.Sleep`). Same code path as the user manually
   double-clicking the titlebar — EQ's WndProc routes the message to
   `CResolutionHandler::ToggleScreenMode`, which calls DX device reset.
   **Both clicks** re-check four gates: `IsWindow + IsHungAppWindow +
   IsIconic + IsLoginActive`. If any of those flip between clicks (250ms
   window), the second click is skipped — defense in depth against the
   user starting an autologin between the two clicks, or the client
   minimizing into the deferred minimize-crash workaround state.
   Budget: ~500ms × N clients.

5. **Overflow warn-log** — `AssignNextFreeSlot` logs one-shot per new
   overflow level when client count exceeds monitor slots (3+ clients on
   2 monitors — the v3.22.20-documented modulo-stacking behavior). Helps
   diagnose the "why are clients on the same monitor" question. Tracked
   via `_overflowCountsLogged: HashSet<int>` for per-session dedup.
   `ArrangeMultiMonitor` also surfaces an Info log on every overflow
   arrange.

### Mechanism notes

- **Free-slot scan is O(N²)** where N is current client count. Acceptable
  because N ≤ ~6 in practice. Builds a `HashSet<int>` from
  `_monitorSlotByPid.Values` each call rather than maintaining a derived
  "occupied slots" set — keeps the only source of truth in one place.

- **Lock-to-primary-dims gating** requires four conditions: 2+ monitors,
  matching slim flags, |Δ| ≤ 200 on both axes, primary fits within
  secondary on both axes. The "primary fits" check prevents the
  pathological case where locking would extend a window past secondary's
  edges (primary larger than secondary). Same-slim-flag requirement
  preserves the user's intent when they explicitly set different slim
  modes per monitor.

- **PostMessage-based DX reinit** is functionally equivalent to a direct
  call into `CResolutionHandler::ToggleScreenMode()` (planned for v3.22.22
  via Ghidra-verified RVA + Native detour). The PostMessage variant routes
  through WndProc indirection — small latency cost, but works today
  without rebuilding `eqswitch-hook.dll`.

### Not addressed (deferred)

- **Direct DX-reinit call** — v3.22.22 will replace the PostMessage approach
  with a direct `CResolutionHandler::ToggleScreenMode()` call from
  `eqswitch-hook.dll`. Requires host-Ghidra RVA verification against Dalaya's
  `eqgame.exe`. Candidate symbols + x86 RVAs are in
  `mq2emu-rof2-x86/MQ2Main/eqgame(Test).h`.

- **Minimize-while-windowed-fullscreen crash** — needs default-on
  `blockMinimize` in the hook DLL regardless of `EQClientIni.MaximizeWindow`.
  Workaround today: toggle Settings → Video → "Maximize on launch".

### Changed

- `TrayManager.cs` — `AssignNextFreeSlot` + `GetMonitorOrderCount` helpers
  (free-slot scan + overflow warn). Three call sites switched from
  `Count`-based to scan-based. New `ForceDxReinit(EQClient)` method called
  from `OnArrangeWindows` for every non-autologin, non-iconic client.
  `_overflowCountsLogged` field added for one-shot overflow warn dedup.
- `Core/WindowManager.cs` — `ArrangeMultiMonitor` refactored: two-pass
  design (style sequential, position batched via `DeferWindowPos`),
  lock-to-primary-dims policy with Δ-threshold auto-degrade, sequential
  fallback on hdwp failure.
- `Core/NativeMethods.cs` — added `WM_NCLBUTTONDBLCLK` and `HTCAPTION`
  constants for `ForceDxReinit`.

### Verifier-round refinements (6-agent sweep — Sonnet + Opus × 3 topics)

Three medium-severity findings addressed before tag:

1. **Autologin gate broadened to cover the entire arrange path** (T3-Opus
   HIGH-severity; original T3-Opus MEDIUM, then T3-Opus round-2 HIGH).
   Round-1 fix added an autologin gate to `ForceDxReinit` only — but
   `ArrangeWindows` (`SetWindowPos` per-client) and `UpdateHookConfig`
   (per-PID shared-memory writes) still ran on autologin-active clients.
   Per the `ClientDiscovered` precedent at TrayManager.cs:L346 —
   *"SetWindowPos/SetWindowLongPtr/CreateRemoteThread can interfere with
   the DirectInput login sequence"* — the round-2 fix filters
   autologin-active clients out of the entire OnArrangeWindows path.
   Window arrangement for autologin clients still happens automatically
   via `ApplyDeferredCosmetics` on `LoginCredentialsSent` (T+~7s), so no
   functionality is lost. The Fix Windows balloon shows "Skipped N —
   autologin active" when applicable.

2. **`ForceDxReinit` skips iconic windows with crash-safe guidance log**
   (T2-Opus + T2-Sonnet + T3-Opus round-2 convergence on MEDIUM).
   `PostMessage(WM_NCLBUTTONDBLCLK, HTCAPTION)` is a no-op on a minimized
   window — EQ's WndProc won't route to `ToggleScreenMode` because there's
   no NC area to double-click. Silent failure. Added an `IsIconic` check
   with a log that explains the WORKAROUND PATH (enable Settings → Video →
   "Maximize on launch" first to block future minimize), not a naive
   "restore window first" footgun — restoring a minimized EQ window in
   windowed-fullscreen state directly triggers the deferred minimize-crash.
   Plus an `IsIconic` re-check in the 250ms Timer Tick for symmetry with
   the pre-click-1 gate (T3-Sonnet MEDIUM).

3. **Arrange-time overflow log removed** (T2-Sonnet + T3-Sonnet, MINOR).
   `ArrangeMultiMonitor` previously emitted a per-arrange Info log when
   client count exceeded monitor slots. In steady-state 3+ client mode
   every SwitchKey swap fired it. Removed — TrayManager's one-shot Warn
   in `AssignNextFreeSlot` already covers the diagnosability case at
   PID-discovery time.

**Two verifier rounds were run.** Round 1 (6-agent Sonnet+Opus × Diff-clean /
Gap-audit / Code-review) surfaced the 3 medium-severity findings above.
Round 2 (re-verified the round-1 fixes with another 6-agent pair) surfaced
3 more convergent findings that round 1 missed:

- **CRITICAL — 3-way convergence (T2-S2, T2-O2, T3-S2):** the 250ms Timer
  Tick re-check was incomplete. Pre-fix it re-checked only `IsWindow +
  IsHungAppWindow`; missing `IsIconic` AND `IsLoginActive`. Same race
  windows the pre-click-1 gates protect against — state changes during
  the 250ms delay (autologin start, window minimize) could leak click 2
  into a wrong-state window. Now re-checks all four gates.
- **HIGH — T3-O2:** round-1's autologin gate covered ForceDxReinit only;
  ArrangeWindows + UpdateHookConfig still ran on autologin-active clients.
  Now filters them out of the entire OnArrangeWindows path.
- **MEDIUM — 3-way (T2-S2 GAP-3, T2-O2 MED-4, T3-O2 MED-5):** the IsIconic
  log told the user "restore window first" — a footgun against the
  deferred minimize-crash bug. Now explains the safe workaround path
  (enable Maximize on launch first).

lParam encoding for negative slim-mode y coords was deep-analyzed by
T3-Opus round-2 and verified correct (`GET_Y_LPARAM` reads back via short
cast — even for x=Left+10 / y=Top+5 on slim windows with Top=-13).

Round-2 surfaced 3 more convergent findings → fixed in-tree → triggered
**round-3** re-verification sweep, which found:

- **HIGH — T2-O3:** the round-2 autologin gate was still incomplete.
  `UpdateHookConfig` (called from `OnArrangeWindows` at L806) iterates
  `_injectedPids` and writes per-PID hook-config shared memory. That set
  is populated pre-resume (CREATE_SUSPENDED architecture) BEFORE autologin
  starts, so it INCLUDES autologin-active PIDs. Rewriting their hook
  config during credential-write could push them onto a different monitor
  on the next intercepted `SetWindowPos`/`MoveWindow`. Round-3 fix: gate
  `UpdateHookConfig`'s loop on `!IsLoginActive(pid)` to match
  `OnArrangeWindows`'s per-client filter.
- **MEDIUM — T3-S3:** double-balloon on the tray-menu Fix Windows path.
  `ExecuteTrayAction.FixWindows` called `OnArrangeWindows()` then
  `ShowBalloon("Fix Windows")`, but `OnArrangeWindows` now emits its own
  terminal balloon on every exit path. Removed the redundant balloon.
- **MEDIUM — T3-S3:** the IsIconic safety message starts with "WARNING:"
  but was logged at Info level. Promoted to `FileLogger.Warn` so log
  filters at Warn threshold don't drop the actionable advice.
- **MINOR — T3-S3:** "Skipped — all N client(s) mid-autologin" balloon
  was internal jargon without remediation. Rephrased to "Fix Windows
  skipped — autologin still in progress. Retry once login completes."
- **MINOR — T1-S3 + T1-O3:** inline comment said "Re-check ALL three
  gates" but listed four. Corrected to "four".
- **MINOR — T3-O3:** project memory file frontmatter claimed SHIPPED
  while body said NOT YET SMOKED. Renamed to BUILT + AWAITING SMOKE.

Round-3 verdicts: 2× APPROVE + 4× CONCERNS. All HIGH and MEDIUM findings
addressed in-tree. Remaining LOWs (Timer Tick `_disposed` guard for
shutdown race, MEMORY.md tier-1 drift at 202 lines, hardcoded 250ms
inter-click interval, `_overflowCountsLogged` never cleared on
ClientLost, ApplyDeferredCosmetics no-op in single-screen non-slim mode)
are documented but not ship-blocking — folded into v3.22.22 backlog.

A **round-4** sweep ran (the completion-checkpoint hook enforced
verification on the round-3 commit itself). 2× APPROVE + 4× CONCERNS.
Two convergent items addressed in a follow-up commit:

- **HIGH (T3-O4):** `UpdateHookConfig`'s `foreach (var pid in _injectedPids)`
  is a HashSet enumeration that could throw `InvalidOperationException`
  if `IsLoginActive` ever pumped messages and `ClientLost` mutated the
  set mid-iteration. Snapshot via `.ToArray()` before iterating — cheap
  defensive change, no behavior delta.
- **MEDIUM 3-way (T2-S4 + T3-S4 + T3-O4):** the `OnArrangeWindows`
  comment near `UpdateHookConfig()` falsely claimed autologin clients
  were NOT in `_injectedPids`. Round-3's whole rationale was the
  opposite: autologin PIDs ARE in the set (added pre-resume), the gate
  inside `UpdateHookConfig` is what filters them. Rewrote the comment
  to match reality.
- **MEDIUM 2-way (T2-O4 + T3-O4):** the prior CHANGELOG narrative said
  "Declined round-4 sweep" — but the completion-checkpoint hook enforced
  round-4 anyway. Updated this section to reflect what actually
  happened.

**Declined round-5 verifier sweep** per
`memory/reference_verifier_round_diminishing_returns_signal` — round 5
historically introduces hallucinations; rounds 3 and 4 already produced
mostly cosmetic findings. The substantive convergent issues across all
4 rounds (round-1 PID-recycle, round-2 Timer Tick gaps, round-3
UpdateHookConfig gap, round-4 HashSet snapshot + comment accuracy) are
all closed. Remaining LOWs documented in v3.22.22 backlog.

### Out of scope (kept for forward reference)

- `_legacySlotDeprecationLogged` (TrayManager.cs) — flagged by both
  Code-review verifiers as "dead field" in the v3.22.20 sweep. **Kept** —
  the field IS used inside `LogFirstFire`, which is called from 6 sites
  in `FireLegacyQuickLoginSlot` for the legacy QuickLogin{N} routing
  dedup (`grep -n 'LogFirstFire' UI/TrayManager.cs` lists callsites + the
  definition). Slated for Phase-6 removal alongside the broader QuickLogin
  scheme, NOT today. This is a verifier-cache-hallucination pattern
  documented in `feedback_verifier_subagent_cache_hallucination` +
  `feedback_grep_callers_before_removing_function` (v1.0.6 → v1.0.7
  RefError precedent). 4-way convergence is a strong signal but not
  infallible — `LogFirstFire` is not in the v3.22.20 diff so verifiers
  reading only the diff may have missed the callers.

## v3.22.20 — Multi-monitor world-entry stacking fix + real SwitchKey slot rotation (2026-05-19)

Closes two smoke-test regressions surfaced by the v3.22.19 multi-monitor pass.
First-ever working SwitchKey-driven monitor swap in the C# port.

### Bugs fixed

1. **World-entry stacking** — `ApplyDeferredCosmetics` (called from both
   `LoginCredentialsSent` at T+~7s and `LoginComplete` at T+~30s) hard-coded
   `ApplySlimTitlebar(GetTargetMonitorBounds())`, which targets the *primary*
   monitor only. In multi-monitor mode the second client got slammed back to
   primary right at world-entry, stacking on top of the first client. Hook
   config was correct (`pos=(1920,-13)` secondary) but the hook DLL only
   intercepts in-process `SetWindowPos` calls — external `ApplySlimTitlebar`
   from `EQSwitch.exe` bypasses the hook. Fix: in multi-monitor mode, route
   through `ArrangeWindows(clients, _monitorSlotByPid)` so each PID lands on
   its assigned monitor. Idempotent for clients already in place.

2. **SwitchKey "bouncing-between-monitors-stacked"** — multi-monitor path
   ran `SwapWindows + ResizeToCurrentMonitors + UpdateHookConfig`, but
   `UpdateHookConfig` re-targeted PIDs by their unchanged `clientIndex` (list
   position), so the hook DLL dragged windows back to original monitors.
   `SwapWindows` was visible chaos with no lasting effect. Replaced with a
   real per-PID slot rotation: `RotateMonitorSlots()` cycles the per-PID
   slot values, then `ArrangeWindows` + `UpdateHookConfig` apply the new
   assignment. Hook DLL holds windows in their new monitors.

3. **`ClientDiscovered` initial-position stacking** — same bug as #1 in the
   pre-login window-discovery path. Now multi-mon-aware.

### Mechanism

New `Dictionary<int, int> _monitorSlotByPid` in `TrayManager`. Each PID is
assigned the next sequential slot on `ClientDiscovered` (matches v3.22.19's
PID1→primary, PID2→secondary semantics on first launch). `SwitchKey` /
`GlobalSwitchKey` / tray-menu "Swap Windows" in multi-mon mode call
`RotateMonitorSlots()` to cycle slot values across PIDs (ordered by PID
ascending for stability), then re-run `ArrangeWindows` with the updated
slot map and rewrite hook configs. `UpdateHookConfigForPid` reads slot
from the map (was `clientIndex % 2`) so the hook DLL follows. `ClientLost`
removes the PID's slot entry.

`WindowManager.ArrangeWindows` and `ArrangeMultiMonitor` take an optional
`IReadOnlyDictionary<int, int>? monitorSlotByPid`. Null → legacy
clientIndex-positional (preserves the v3.22.19 first-launch behavior for any
call site that doesn't yet pass the slot map). All five `TrayManager`
invocations (LaunchSequenceComplete, OnArrangeWindows, OnToggleMultiMonitor,
ReloadConfig auto-arrange, ApplyDeferredCosmetics) now pass the slot map.
`OnToggleMultiMonitor` + `ReloadConfig` backfill the slot map for PIDs that
existed before multi-mon got enabled.

`GetMonitorForClientIndex` / `GetWorkAreaForClientIndex` → renamed
`GetMonitorForPid(pid, clientIndexFallback)` / `GetWorkAreaForPid(...)`.
PID lookup wins; clientIndex is the fallback for the transient
discovery-race window where a PID exists in `clients` but not yet in
the slot map.

### Changed

- `Core/WindowManager.cs:101-109` — `ArrangeWindows` signature accepts
  optional slot map.
- `Core/WindowManager.cs:166-221` — `ArrangeMultiMonitor` consults slot map
  per client when supplied; log line includes `[slot=N]`.
- `UI/TrayManager.cs` — `_monitorSlotByPid` field, ClientDiscovered slot
  assignment, ClientLost slot removal, `RotateMonitorSlots()`, slot-aware
  `GetMonitorForPid` / `GetWorkAreaForPid`, slot lookup in
  `UpdateHookConfigForPid`, multi-mon-aware `ApplyDeferredCosmetics`,
  slot-rotation in `OnSwitchKey` / `OnGlobalSwitchKey` / tray "Swap Windows",
  backfill in `OnToggleMultiMonitor` and `ReloadConfig`.
- `EQSwitch.csproj` — `<Version>` 3.22.19 → 3.22.20.

### Backward compatibility

Legacy behavior is preserved by the null-slot-map path. Anyone calling
`ArrangeWindows(clients)` without the map still gets `clientIndex %
monitorOrder.Count` assignment — v3.22.19 semantics. No config schema
change, no migration needed.

### Smoke evidence (pre-fix, 2026-05-19 20:25)

```
20:25:10.349 ApplySlimTitlebar: hwnd=723512 → (0,-13) 1920x1093   ← PID 25312 primary
20:25:13.443 ApplySlimTitlebar: hwnd=395378 → (0,-13) 1920x1093   ← PID 24876 ALSO primary (STACK)
20:25:13.444 UpdateHookConfig: PID 24876 → pos=(1920,-13) 1920x1213, stripFrame=1   ← hook config says secondary
```

Hook config was correct; external ApplySlimTitlebar call from `EQSwitch.exe`
stacked the window on primary. Hook DLL never got a chance to drag it back
because EQ-in-world stops calling SetWindowPos once windowed-fullscreen settles.

### Open items

- DPI "smooshed" symptom is a side-effect of the stacking; should self-resolve
  once windows land on their assigned monitor at world-entry (EQ detects the
  correct monitor's dimensions for its windowed-fullscreen rect). If still
  visible after this fix, the next pass would revisit PerMonitorV2 with the
  single-screen-team fullscreen-bug fixed separately.
- The tray menu "Swap Windows" entry name still says "swap" — semantically it
  now means "rotate monitor assignment" in multi-mon mode. Label revisit
  deferred.

## v3.22.19 — Multi-monitor framework + bug fixes (conservative all-slim default) (2026-05-18→19)

First end-to-end pass on multi-monitor mode in the C# port. Per Nate's report
(2026-05-19): multi-monitor mode has never worked correctly on the C# version.
This release SHIPS THE FRAMEWORK for per-monitor differentiation but defaults
to v3.22.18-style all-slim-everywhere behavior so primary frame stays
consistent with single-screen mode (Nate's hard requirement: "main monitor
always needs to be our same window frame as we used yesterday for team").
The PerMonitorV2 DPI awareness experiment from an earlier v3.22.19 cut was
reverted after it regressed single-screen team launches into a buggy
fullscreen mode.

1. **Per-monitor slim-titlebar override framework** — `SlimTitlebarSecondary`
   config flag, **default `true`** (v3.22.18 parity, both monitors slim).
   Future runtime work needed before a `false` default is viable.
2. **Smart secondary monitor auto-pick** — `ResolveSecondaryMonitorIdx`
   helper skips portrait (h/w > 1.3) and narrow (<1000px width) monitors.
3. **HookConfig stripFrame wiring** — bug found by verifier convergence; the
   per-PID slim flag was computed but ignored, so the hook DLL re-stripped
   WS_THICKFRAME on every interception, defeating the C#-side restore. Real
   bug, real fix, stays in.
4. **ReloadConfig field copy** — Settings → Apply now propagates the new
   SlimTitlebarSecondary field without restart.

### Why

Pre-v3.22.19, `Layout.SlimTitlebar` was a single global bool — all-or-nothing
across monitors. When `Mode == "multimonitor"`, both windows got the same
treatment: either both with slim coverage (taskbar hidden) or both with the
legacy normal-frame + EQ-INI-size shape (taskbar visible but window smaller
than the monitor).

Nate's intent for the dual-monitor setup: keep the desktop main monitor in
full slim coverage (immersive game) while the laptop monitor (used during
play for Discord/notes/etc.) keeps the taskbar visible with a normal frame.

### Changed

| File | Nature | Detail |
|---|---|---|
| `Program.cs` | `HighDpiMode.SystemAware` retained (REVERTED from PerMonitorV2) | An earlier v3.22.19 cut tried `PerMonitorV2` to fix cross-DPI positioning but regressed single-screen team launch into a fullscreen-bug state. Reverted to v3.22.18 SystemAware. Per-monitor DPI math is left as future work; the per-monitor slim flag still ships but defaults to "both slim" so primary frame stays consistent. |
| `Config/AppConfig.cs` | New `LayoutConfig.SlimTitlebarSecondary` (bool, default `true`) | Per-monitor override consulted only when `Mode == "multimonitor"`. Default `true`: secondary uses slim treatment matching primary (v3.22.18-style consistent frames across both clients). Set `false` to opt into the experimental "secondary keeps normal frame + work-area sizing" shape — that path still has rough edges on cross-DPI setups. |
| `Core/WindowManager.cs::ArrangeMultiMonitor` | Per-window slim choice | Reads `(primarySlim, secondarySlim)` once, builds `(bounds, useSlim)` tuples per monitor, applies `ApplySlimTitlebar` or sized `SetWindowPos` per window. Non-slim branch now sizes to work-area (was `SWP_NOSIZE` — kept EQ-INI size) AND restores `WS_THICKFRAME` if previously stripped by slim (hook DLL only strips, never restores). Loud failure log if monitor enumeration counts mismatch. |
| `Core/WindowManager.cs` | New public `static ResolveSecondaryMonitorIdx(configIdx, primaryIdx, monitors, minWidthPx=1000)` | **Smart secondary monitor pick.** Skips monitors narrower than `minWidthPx` (default 1000 — excludes typical portrait / phone-aux monitors). Walks all non-primary monitors in enumeration order, returns first viable one. If user has explicitly configured a too-narrow secondary, falls through to auto-pick with a loud warn log (graceful self-heal of accidental misconfiguration). |
| `Core/WindowManager.cs` | New public `GetAllMonitorWorkAreas` | Sibling of `GetAllMonitorFullBounds`; needed by TrayManager's per-PID hook config. Index order matches full-bounds enumeration. |
| `UI/TrayManager.cs::UpdateHookConfigForPid` | Per-PID slim flag + correct stripFrame wiring | Primary client uses `SlimTitlebar`, secondary uses `SlimTitlebarSecondary`. Non-slim secondary keeps hook enforcement on the work-area position with `stripThickFrame=false` so the hook DLL stops stripping the resize border the C# side just restored. (**BUGFIX**: pre-fix code passed `stripThickFrame: posEnabled` which always-true in MM mode → hook fought C#'s WS_THICKFRAME restore in an infinite tug-of-war. Verifier T1+T3 caught this independently across both Sonnet+Opus models.) |
| `UI/TrayManager.cs::ReloadConfig` | Mirror `SlimTitlebarSecondary` in field copy | (**BUGFIX**: pre-fix code missed the new field, so Settings → Apply wouldn't take effect without restart. Verifier T3 Sonnet finding.) |
| `UI/TrayManager.cs` | New private `GetWorkAreaForClientIndex` | Mirrors `GetMonitorForClientIndex` but uses work-area enumeration. Default fallback `Bottom=1040` (accounts for a typical 40px taskbar) vs full-bounds helper's `Bottom=1080`. |
| `EQSwitch.csproj` | Version 3.22.18 → 3.22.19 | |
| `CHANGELOG.md` (+ `_.releases` mirror) | This entry | |

### Single-screen mode

Unchanged. Single-screen mode reads `Layout.SlimTitlebar` only; the secondary
override is exclusive to multi-monitor. This preserves the v3.22.18 single-
screen behavior byte-for-byte.

### Multi-monitor mode behavior matrix

| `SlimTitlebar` | `SlimTitlebarSecondary` | Primary monitor | Secondary monitor |
|---|---|---|---|
| `true` (default) | `true` (default — new, = legacy v3.22.18) | Slim covers taskbar | Slim covers taskbar |
| `true` | `false` (experimental opt-in) | Slim covers taskbar | Normal frame, work-area sized |
| `false` | `false` | Normal frame, work-area sized | Normal frame, work-area sized |
| `false` | `true` | Normal frame, work-area sized | Slim covers taskbar |

### Hook DLL behavior

Native `eqswitch-hook.dll` is unchanged this release. The hook reads
`TargetX/Y/W/H + StripThickFrame + Enabled` from per-PID shared memory; C#
now writes per-PID values reflecting the per-monitor slim choice. Existing
hook DLL handles work-area vs full-bounds positions identically — it just
enforces whatever C# wrote.

### Opt into experimental "secondary normal frame" shape

Default behavior is v3.22.18 parity (both slim). To try the experimental
shape where the secondary monitor's client uses a normal resize border +
work-area sizing (taskbar visible), edit `eqswitch-config.json` →
`Layout.SlimTitlebarSecondary: false` and restart EQSwitch. Known
issue 2026-05-19: cross-monitor positioning has unresolved DPI quirks; the
secondary window may not consistently land on the second physical monitor
on multi-DPI setups. Revert to `true` to get the consistent-frame
behavior back.

### Risk

Medium. The non-slim branch in multi-monitor mode changed shape:
- v3.22.18: `SWP_NOSIZE` (kept EQ INI size, often smaller than monitor)
- v3.22.19: sizes to work-area (fills the visible monitor area, taskbar visible)

Existing users in `Mode=multimonitor` + `SlimTitlebar=false` would have seen
small windows; they now see work-area-filling windows. If any user explicitly
wanted the smaller EQ-INI-sized behavior, set `SlimTitlebarSecondary: true`
and also set `SlimTitlebar: true` (this brings back slim, which is one step
removed from the prior behavior but is the documented "passive override").

Defensive: if `GetAllMonitorBounds` and `GetAllMonitorWorkAreas` ever return
different counts (theoretically impossible — both walk `EnumDisplayMonitors`),
`ArrangeMultiMonitor` aborts with a loud Error log rather than risk picking
wrong-monitor bounds.

### Verification

This release ships after an autonomous smoke per Nate's directive: build,
deploy, restart EQSwitch, toggle multi-monitor, screenshot, verify primary
slim coverage + secondary normal frame, iterate if wrong, run verifier
agents to confirm clean.

## v3.22.18 — Rename failure-path balloons to drop "state machine" jargon (2026-05-18)

Follow-up to v3.22.17 per user confirm of the carved-out failure surface.
v3.22.17's CHANGELOG explicitly flagged that the failure balloons at
`Core/AutoLoginManager.cs:1243` (`"state machine failed (terminal Error)"`)
and `:1253` (`"state machine crashed: {ex.Message}"`) still leaked "state
machine" jargon to users. Round-1 verifier T4 Opus + T3 Opus + T4 Sonnet
flagged these as inconsistent with the spirit of the v3.22.17 ask.

### Renamed

| Site | Old balloon text | New balloon text |
|---|---|---|
| `Core/AutoLoginManager.cs:1243` (terminal Error) | `"{account}: state machine failed (terminal Error)"` | `"{account}: autologin failed"` |
| `Core/AutoLoginManager.cs:1253` (unhandled exception) | `"{account}: state machine crashed: {ex.Message}"` | `"{account}: autologin error: {ex.Message}"` |

With this ship, no user-facing balloon contains "state machine" anywhere.
The internal `FileLogger.Info`/`Error` calls still use the technical term
(SM-OBS, AutoLogin-SM:, terminal state Error) for developer diagnostics —
that's the right surface for jargon.

### Files

| File | Nature |
|---|---|
| `Core/AutoLoginManager.cs` | 2 string-literal renames (failure-path Report calls) |
| `EQSwitch.csproj` | Version 3.22.17 → 3.22.18 |
| `CHANGELOG.md` (+ `_.releases` mirror) | This entry |

### Risk

Zero. String-literal-only change. `Report()` is pure UI notification.
Build expected to remain Debug+Release 0/0.

## v3.22.17 — Drop "state machine" jargon from user-facing tray balloons (2026-05-18)

User feedback after v3.22.16 smoke confirmation: *"can we get rid of the
state machine init and complete tooltips, the users wont know what those
mean."* The two SM-bookkeeping balloons were implementation jargon —
users seeing "state machine — waiting for login screen..." or "state
machine completed" had no context for what a "state machine" was.

### Removed

| Site | Old balloon text | Why removed |
|---|---|---|
| `Core/AutoLoginManager.cs:1009` | `"{account.Name}: state machine — waiting for login screen..."` (init) | Redundant with the EQ process launch the user already sees on screen + the legacy path's "Waiting for login screen..." which is in plain language. SM-init balloon was the only SM-jargon-flavored entry surface. |
| `Core/AutoLoginManager.cs:1247` | `"{account.Name}: state machine completed"` (complete) | Redundant with the "{account.Name} logged in!" balloon at the Enter-World success site (line ~1972) which already fires in plain language right before this jargon balloon did. |

### Kept

The plain-language progress balloons that already existed are
unchanged — these are the user-facing autologin surface:

- `"Warmup done — settling..."` (warmup completion)
- `"Waiting for login screen..."` (login screen detection)
- `"Typing credentials..."` (password entry)
- `"Submitting login..."` (connect submission)
- `"{account.Name} reached char select."` (char-select)
- `"{account.Name} reached char select (slot N)."` (with character match)
- `"Entering world..."` (Enter World fire)
- `"{account.Name} logged in!"` (terminal success)

### Not addressed (flag if you want these too)

`AutoLoginManager.cs:1243` still says `"state machine failed (terminal
Error)"` and `:1253` says `"state machine crashed: {ex.Message}"`. Both
fire only on failure paths. The conservative read of the user's
feedback was init+complete specifically, but the same "state machine"
jargon survives in the failure surface. Renaming to "Login failed" /
"Login error" would be consistent — say the word and it ships.

### Files

| File | Nature |
|---|---|
| `Core/AutoLoginManager.cs` | 2 `Report()` calls deleted + comments explaining the removal |
| `EQSwitch.csproj` | Version 3.22.16 → 3.22.17 |
| `CHANGELOG.md` | This entry |

### Risk

Zero. `Report()` is a pure UI notification path (StatusUpdate event →
`ShowBalloon` in TrayManager). No control-flow change. No autologin-path
code modified — the SM still runs identically, just doesn't fire two of
the balloons on the way through. C#-only ship; native `eqswitch-di8.dll`
unchanged.

### No smoke required

UI-text removal in a doc/comment-class change. Build expected to remain
Debug+Release 0/0.

## v3.22.16 — Fix bridge UI fallback silent-failure when char-list row 0 is empty (2026-05-18)

**Real bug fix.** First native-code change in the v3.22.13+ chain — the prior
three releases were doc/comment-only. This one fixes a documented intermittent
failure mode that finally tripped a reproducible smoke at 2026-05-18 02:31:23.

### What broke

User fires team1 → both clients reach CharSelect at t≈45s (normal v3.22.x timing)
→ MQ2 bridge attempts to publish character list via SHM `charCount` →
**fails silently for 30s** → C# `StepCharSelect` defensive abort fires
"MQ2 bridge didn't populate char list ... stopping at char select to avoid
wrong-character enter-world."

The SM's defensive abort was working correctly. The bug was in the bridge.

### Root cause (ground-truth from `eqswitch-dinput8-{pid}.log`)

`MQ2Bridge::Poll`'s UI fallback path at `Native/mq2_bridge.cpp:3933-3957`
discovered the name column by reading **only row 0** of each CListWnd column
(0-9), validating with `IsPlausibleName`. If row 0 was empty (user's characters
in slots 1+, slot 0 unpopulated), every column's row-0 read returned empty,
`IsPlausibleName` rejected all, `g_cachedNameCol` stayed `-1`, and the name-
extraction loop never ran. `count` stayed 0, SHM `charCount` stayed 0, C# SM
timed out.

The silent failure mode is exactly the v3.22.10 closeout's accepted-known-limit:

> **P9/P8/uiFallback latch back-out gaps** — ACCEPTED as known limit. Trigger:
> if DebugView shows a stuck one-shot log across multiple cycles, ship a
> coordinated fix per v3.22.4 "fix-all-three-together-or-skip-all-three" rule.

**Trigger fired today.** First reproduction since the closeout was written.

### What did NOT cause it (proved by parity check)

| Component | Hash | Touched by v3.22.13-15? |
|---|---|---|
| `eqswitch-di8.dll` (the bridge — runs INSIDE eqgame.exe) | `6da1c923…` → `3331468b…` (NEW in v3.22.16) | NO until this release |
| `eqswitch-hook.dll` (slim-titlebar hook) | `ba50e5a5…` | No |
| `EQSwitch.exe` (the C# host) | Replaced 3× in v3.22.13/14/15 | Yes — but comment/log-string edits only, no code path affecting SHM contract |

v3.22.13/14/15 were doc/comment + 1 log-string ships; the bridge that ran the
2026-05-18 02:31 failure was byte-identical to the bridge that ran 11+ successful
smokes earlier today on the same eqgame.exe. The trigger fired by chance after
my deploys — user's suspicion was reasonable, the analysis ruled it out.

### Fix — coordinated per "fix-all-three-together"

`Native/mq2_bridge.cpp`:

| # | Change | Lines | Purpose |
|---|---|---|---|
| 1 | New global `g_lastColDiscoveryFailMs` | 165 | Rate-limit timestamp for the diagnostic log |
| 2 | Multi-row scan in column discovery: rows 0-3 × cols 0-9 | ~3950-3967 | Handle the row-0-empty case (user's chars in slots 1+) |
| 3 | LOUD failure log when column discovery fails, rate-limited 5s | ~3974-3997 | Convert the silent latch-back-out gap into a visible diagnostic. Logs the actual row-0 content across cols 0-3 so we know WHY column discovery failed (empty list / column headers / unloaded UI). Per `reference_loud_runtime_silent_rest.md` — loud signal at the failing surface, not silent timeout. |

### Files

| File | Nature |
|---|---|
| `Native/mq2_bridge.cpp` | +25 LOC bug fix (multi-row scan + loud failure log + new rate-limit global) |
| `Native/eqswitch-di8.dll` | Rebuilt — new SHA256 `3331468bf9be8010…`, 243,200 B (vs prior `6da1c923…` 242,688 B = +512 B for the new code) |
| `EQSwitch.csproj` | Version 3.22.15 → 3.22.16 |
| `_.releases/eqswitch/eqswitch-di8.dll` | Mirror — re-synced |
| `_.releases/eqswitch/SHA256SUMS` | New EXE + new di8.dll hashes |
| `_.releases/eqswitch/VERSION` | v3.22.16 |
| `CHANGELOG.md` (eqswitch + mirror) | This entry |

### Risk

Low — focused 3-change patch in one code section. Multi-row scan can only succeed
in MORE cases than the old single-row scan (rows 0-3 ⊃ row 0). The loud failure
log is rate-limited so can't spam. The new global initializes to 0 (correctly).

### Verification plan

1. Build Debug + Release 0/0 ✓ (already verified)
2. Native eqswitch-di8.dll rebuild 0/0 ✓ (already verified — only pre-existing
   warning in unrelated file)
3. Deploy to proggy + mirror
4. **Autonomous smoke** — fire team1, verify both clients reach in-world. Per the
   task brief's smoke rule, this IS an autologin-path change.
5. If pass → tag + push

## v3.22.15 — Verifier-round-2 + ultrathink ground-truth pass (2026-05-18)

Doc/comment + 1 runtime log string follow-up to v3.22.14. Round-2 verifiers
returned 5 × APPROVE / 3 × CONCERNS-MINOR / 0 × REJECT — all CRITICAL
findings ground-truth-resolved as false-positives (T3 Opus's "proggy
missing" was a cache hallucination; the agent ran `ls X:/_Projects/proggy/`
when the actual deployment path is `C:/Users/nate/proggy/`). User then
asked for an explicit "ultrathink fix all if they are real" pass over
every surfaced MINOR; the deeper scan surfaced 4 additional XML doc
Iter-1/Iter-2B headers in `AutoLoginManager.cs` matching the same shape
v3.22.13 retired at lines 1518/1742, and one runtime log string at line
1127 with a stale `until Iter-2` forward-reference baked into the message
body (Iter-2 already shipped in v3.22.x; the log message was factually
wrong as live output).

### Real findings closed

| # | Source | Verifier | Action |
|---|---|---|---|
| 1 | `Native/login_shm.h:100` "without needing a future SHM bump" | T2 Opus CONCERN #1 | "needing a future" dropped — text now "without a second SHM bump." Same meaning, no forward-tense |
| 2 | `Core/AutoLoginManager.cs:991` "Iter-4 (v3.22.0):" version-attribution prefix | T3 Opus MINOR #4 | "Iter-4 (v3.22.0):" → "v3.22.0:" — cleaner version provenance without the iteration-tag ambiguity |
| 3 | `Core/AutoLoginManager.cs:1034` "v3.22.0 Iter-2B (2026-05-16):" | (consistent treatment with #2) | "v3.22.0 Iter-2B (2026-05-16):" → "v3.22.0 (2026-05-16):" |
| 4 | `Core/AutoLoginManager.cs:1127` runtime log string `" [native-ahead: ... until Iter-2]"` | ground-truth pass NEW finding | `" until Iter-2"` removed from the log string. Iter-2 has shipped (we're at v3.22.x); the log line was advertising a state-machine evolution that already happened. Now reads `"]"` — same diagnostic intent, no stale forward-ref |
| 5 | `Core/AutoLoginManager.cs:1352` `/// Iter-1 transition: WaitLoginScreen → TypingCredentials` XML doc header | ground-truth pass NEW finding (same shape as v3.22.13 row 9b kills at 1518/1742) | "Iter-1 transition: " dropped from XML doc — body retained |
| 6 | `Core/AutoLoginManager.cs:1373` `/// Iter-2B (2026-05-16): TypingCredentials → ClickingConnect...` | ground-truth pass NEW finding | "Iter-2B (2026-05-16): " dropped; remainder retained |
| 7 | `Core/AutoLoginManager.cs:1402` `/// Iter-2B: ClickingConnect → WaitConnectResponse...` | ground-truth pass NEW finding | "Iter-2B: " dropped; "Skip-aheads removed per the 2026-05-16 verifier round" preserved as provenance with the verifier-round attribution that v3.22.14 refined Rule 11 keeps |
| 8 | `Core/AutoLoginManager.cs:1419` `/// Iter-2B: WaitConnectResponse → ...` | ground-truth pass NEW finding | "Iter-2B: " dropped; "load-bearing transition for v3.22.0" version provenance preserved |
| 9 | v3.22.14 CHANGELOG row 49 cited `AutoLoginManager.cs:1336` for `SetAutoLoginActive(pid, false)` | T3 Opus | Symbol-anchored reference adopted in this entry — the SM finally clears `autoLoginActive` via `SetAutoLoginActive(pid, false)` inside the nested-try cleanup block (line numbers drift across edits; symbol stays) |
| 10 | v3.22.14 CHANGELOG "~25 verifier-round-attribution inline comments" count | T2 Opus CONCERN #2 | Tightened (re-counted post-edit, ground-truthed via grep per `feedback_compute_numerical_claims_yourself`): **24 historical-anchor lines** survive in `AutoLoginManager.cs` as provenance per the refined Rule 11 — split into **19 strict `// Iter-N fix-round-N (verifier T2-Opus / T3-Sonnet / …)`** lines (the canonical verifier-round-attribution form) **+ 5 date-stamped `// Iter-N (2026-MM-DD): …`** attribution lines (looser form, same purpose). v3.22.15 retires **6 version-iteration prefix stamps** at the sites listed in this table: 2 inline (#2, #3 at lines 991 / 1034) + 4 XML doc (#5, #6, #7, #8 at lines 1352 / 1373 / 1402 / 1419). The "~25" approximate from v3.22.14 was off by ±1; the strict pattern count was off by 6 — both corrected here |
| 11 | `X:/_Projects/eqswitch/bin/Publish/eqswitch-hook.dll` stale (April 9 2026 build, hash `73e7921f…`) | T2 Opus CONCERN #3 | Overwritten with current `Native/eqswitch-hook.dll` (hash `ba50e5a5…`). `bin/` is gitignored so this is a local-machine cleanup; canonical deploy path is `bin/Release/net8.0-windows/win-x64/publish/` which builds fresh each ship. `rm -rf` not used (per `feedback_no_rm_rf`) — overwrite-in-place keeps the latent-hazard from manual confusion at zero |

### Findings deliberately NOT addressed

| Finding | Verifier | Rationale |
|---|---|---|
| Inline `// Iter-1` / `// Iter-2` dev-stage stage markers at lines 844, 846, 898, 900-902, 925, 1042, 1070, 1092, 1124 in `AutoLoginManager.cs` | (would be next layer of cleanup) | These are NON-XML-doc inline comments describing development-stage logic / WHY code exists / smoke-target configurations. They're descriptive historical context, not forward-ship aspiration. Per the task brief's strict scope (criterion 6 explicitly called out only XML doc Iter-3/Iter-4 notes) + Rule 11 codebase-convention matching, these stay. |
| `_.claude/_comms/plan-eqswitch-v3.22.0.md` historical Iter-4 / v3.23.0 refs | T4 Sonnet | Intentionally frozen archival plan-doc per v3.22.14 CHANGELOG carve-out. Modifying = rewriting decision-time history. |
| Memory files referencing old 35-50s wall-clock | T4 Sonnet | Historical ship-state records — intentionally frozen. |

### Files

| File | Nature |
|---|---|
| `EQSwitch.csproj` | Version 3.22.14 → 3.22.15 |
| `Config/AppConfig.cs` | unchanged from v3.22.14 |
| `Core/AutoLoginManager.cs` | 7 comment + 1 log-string edits at lines ~991, ~1034, ~1127, ~1352, ~1373, ~1402, ~1419 |
| `Native/login_shm.h` | Line ~100 "needing a future" dropped |
| `bin/Publish/eqswitch-hook.dll` | Stale DLL overwritten with current `Native/` source (gitignored, local-only) |
| `CHANGELOG.md` (eqswitch repo + _.releases mirror) | This entry |

### Risk

The 7 comment edits are zero-risk. The 1 runtime log string edit (line
1127) changes what gets printed when `nativeAhead` triggers (C# stalling
on TypingCredentials while Native is ahead) — the log no longer says
"until Iter-2". Same diagnostic intent, ~10 fewer bytes per fire. No
control-flow change. Build expected to remain Debug+Release 0/0.

### Verifier discipline

Re-dispatch 8 fresh verifier agents (4 topic pairs) for round 3. The
ultrathink ground-truth pass dispatched the high-stakes additional
scope (XML doc Iter-1/Iter-2B headers + runtime log string + version-
iteration prefix stamps) that round-2 didn't surface because round-2
focused on what v3.22.14 claimed to fix, not on what was structurally
similar.

## v3.22.14 — Verifier-driven follow-up to v3.22.13 (2026-05-18)

Doc/comment-only release follow-up. v3.22.13 retired the high-visibility
forward-ship comments (XML docs + AppConfig + login_shm.h header area) but
the 8-agent verifier-round-1 surfaced a convergent gap: my Rule-11 carve-out
in v3.22.13's CHANGELOG row 9b ("keep all inline `// Iter-N` comments as
historical anchors") was too broad. Several inline references were
**forward-ship aspiration**, not provenance attribution — those needed to
be retired too. Plus: `_.releases/eqswitch/eqswitch-hook.dll` was stale
vs the `Native/` source (different SHA256), and `SHA256SUMS` was missing
`eqswitch-hook.dll` entirely.

### Verifier-round-1 findings addressed

| # | Source | Verifier | Action |
|---|---|---|---|
| 1 | `Core/AutoLoginManager.cs:478` "until Iter-4 flips the default" | T2 Opus M1, T4 Opus CRITICAL #1 | Inline comment retired — describes current operational reality (per-install config sets the flag) |
| 2 | `Core/AutoLoginManager.cs:827` "Internal rename — this replaces RunLoginSequence's body in Iter-4" | T2 Opus C2 | Comment retired; the Iter-4 rename plan is permanently cancelled |
| 3 | `Core/AutoLoginManager.cs:838` "ratchet down in Iter-4 — never let it grow unbounded" | T2 Opus M2 | Replaced with the v3.22.10 closeout rationale (promote-to-config rejected) |
| 4 | `Core/AutoLoginManager.cs:1433-1434` "Recoverable dialog handling deferred to Iter-3" | T2 Opus C3 | Replaced with description of the current per-tick okClass inspection + 180s timeout fallback behavior |
| 5 | `Core/AutoLoginManager.cs:1532-1533` "Refactor to non-blocking sub-states in Iter-4 cleanup if verifier objects" | T2 Opus C3, T4 Opus CRITICAL #3 | Retired; the StepCharSelect blocking semantics are intentional and stable per v3.22.x smokes |
| 6 | `Core/AutoLoginManager.cs:1763` "refactor in Iter-4 if needed" (StepEnteringWorld) | T2 Opus M2, T3 Opus LOW | Same treatment as #5 |
| 7 | `Config/AppConfig.cs:420` "validated wherever Characters lives by Phase 4 UI" | T2 Opus M3, T4 Opus MINOR #5 | "by Phase 4 UI" removed — Phase 4 tab-split is PERMANENTLY REJECTED per Nate's 2026-05-17 directive |
| 8 | `Native/login_shm.h:114-117` "v3.22.0 will rewrite RunLoginSequence as a tick loop" | T2 Opus C1, T3 Opus LOW | Rewritten in past tense; describes the deployed v3.22.0+ RunLoginStateMachine consumer state |
| 9 | `_.releases/eqswitch/eqswitch-hook.dll` stale vs source (SHA256 mismatch: mirror `5db69b79…` vs source/proggy `ba50e5a5…`) | T4 Opus (deeper than what verifier surfaced — caught during fix-pass cross-check) | Mirror re-synced from `Native/eqswitch-hook.dll` (the ground truth); SHA256 now matches proggy |
| 10 | `_.releases/eqswitch/SHA256SUMS` missing `eqswitch-hook.dll` entry | T2 Opus C4, T3 Opus MEDIUM | Added — manifest now covers all three first-party deployable binaries |

### Retention rationale refined

The v3.22.13 row 9b said "inline `// Iter-N …` comments are historical
anchors per Rule 11." That's still true, but the refined rule is: **only
verifier-round attribution lines** count as historical anchors (e.g.,
`// Iter-3 fix-round-2 (verifier T2-Opus C3 + T2-Sonnet C2): ...` —
attribution to a specific verifier round is permanent provenance).
Aspirational forward-ship references (`will flip default`, `cleanup if
verifier objects`, `refactor in Iter-4 if needed`) are not provenance —
they're stale plan-fragments and should be retired. v3.22.14 applies this
refined rule. The ~25 verifier-round-attribution inline comments scattered
through `AutoLoginManager.cs` are intentionally kept.

### Verifier-round-1 findings deliberately NOT addressed

| Finding | Verifier | Rationale |
|---|---|---|
| `AutoLoginManager.cs:993` "Iter-4 (v3.22.0): fire LoginCredentialsSent" | T2 Opus | Provenance, not aspiration — describes that this code shipped during v3.22.0 Iter-4 development. Kept per the refined Rule-11 retention. |
| `OverallTimeoutMs = 180_000` hardcoded should be config | T3 Sonnet #2 | Promote-to-config was explicitly NO-SHIP in the v3.22.10 closeout; comment now references that decision. |
| `SkipNativeWarmup` + `UseStateMachine` default interaction warning | T3 Sonnet #1 | Documented pre-existing concern; Nate runs both flags = true via config, so the bad-combo (only-UseStateMachine flipped) doesn't exist in practice. v3.22.10 closeout policy: no enhancement without trigger. |
| `Native/login_shm.h:212` `autoLoginActive` clearing trace | T3 Sonnet #5 | Verified during fix-pass: `RunLoginStateMachine` DOES clear via `SetAutoLoginActive(pid, false)` at `AutoLoginManager.cs:1336` (inside the SM finally block). The cross-verification proved the concern unfounded. |
| Memory files referencing old 35-50s wall-clock | T4 Sonnet #2, T4 Opus | Historical records of past ship states — intentionally frozen per `feedback_save_useful_memories_without_asking`. |
| `_comms/plan-eqswitch-*.md` historical plan docs referencing v3.23.0 / Iter-4 | T4 Sonnet #3, T4 Opus | Archival plan docs are intentionally frozen as snapshots of decision-time context. |
| `UpdateDialog.cs` parses different SHA256SUMS schema than `_.releases/eqswitch/SHA256SUMS` | T4 Opus #2 | Two different SHA256SUMS serve different purposes — GitHub release-asset (single ZIP hash) for auto-update vs local release-mirror (per-binary manifest) for distribution integrity. Both are correct in their context. |

### Files

| File | Nature |
|---|---|
| `EQSwitch.csproj` | Version 3.22.13 → 3.22.14 |
| `Config/AppConfig.cs` | Line 420 Phase 4 UI reference retired |
| `Core/AutoLoginManager.cs` | 6 inline comment edits (lines ~476-480, ~826-839, ~1432-1437, ~1531-1533, ~1761-1763) |
| `Native/login_shm.h` | Lines 114-117 rewritten in past tense |
| `_.releases/eqswitch/eqswitch-hook.dll` | Re-synced from `Native/` (was stale) |
| `_.releases/eqswitch/SHA256SUMS` | Now covers all 3 binaries (was missing hook.dll) |
| `_.releases/eqswitch/VERSION` | v3.22.13 → v3.22.14 |
| `CHANGELOG.md` (eqswitch repo) | This entry |
| `_.releases/eqswitch/CHANGELOG.md` | Mirror — synced byte-identical from eqswitch repo |

### Risk

Zero. No code paths touched. No autologin-path keystroke, timing, SHM
contract, or state-machine transition logic is edited. Build expected to
remain 0 errors / 0 warnings. Native `eqswitch-hook.dll` was already-built
ahead of this release — the mirror just gets re-synced from the canonical
source; the deployed proggy DLL was never stale. No smoke required.

### Verifier discipline

After v3.22.14 ships, re-dispatch 8 fresh verifier agents (4 topic pairs)
per the high-stakes spec. Convergence on APPROVE across all 4 topics is
the gate to claim DONE.

## v3.22.13 — Final-audit cleanup: retire stale forward-ship comments + amend AUTOLOGIN SPEC (2026-05-18)

Doc/comment-only release. Zero code-path change. Project remains FINAL
RELEASE READY (the v3.22.10 closeout state). This ship closes the final-
audit pass that walked the six DONE-gate criteria (scratchpad, AUDIT_TASKS,
deferred trackers, source-comment TODOs, memory backlog, open architectural
decisions). No SHIP items emerged from the audit — every open item resolved
to KILL (stale forward-ship comment) or AMEND (spec wording lagged reality).
With v3.22.13 the source-of-truth comments and the AUTOLOGIN SPEC both
match what's actually deployed and operational.

### Retired (KILL items)

| # | Location | What got retired | Why |
|---|---|---|---|
| 1 | `Config/AppConfig.cs:861-867` | "Iter-4 ship flips this to `true` and v3.23.0+ removes the legacy path" on `UseStateMachine` | The Iter-4 staging plan from v3.22.0 era never executed as a code-default flip. Operational reality: per-install `eqswitch-config.json` sets the flag; AppConfig default stays `false` as a safety baseline. No v3.23.0 plan exists. |
| 2 | `Core/AutoLoginManager.cs:803-821` | "Iter-4 ship gate flips `LaunchConfig.UseStateMachine` default to true" in `RunLoginStateMachine` XML doc | Same — staging plan never executed; SM is the deployed path via config. |
| 3 | `Core/AutoLoginManager.cs:874` | Stale assertion "v3.22.0+ flipped `UseStateMachine` to true" | Factually wrong as a source-of-truth claim (the AppConfig default at line 868 is `false`). Reconciled by restating the operational reality. Rule 7 "surface conflicts, don't blend". |
| 4 | `Core/AutoLoginManager.cs:80-81` | "candidate to backport in Iter-4 cleanup" on `_focusFakeMutex` | The legacy `RunLoginSequence` has the same focus-fake race but is not the active deployed path; Iter-4 cleanup is permanently dead. The mutex on the SM path is the load-bearing fix. |
| 5 | `Native/login_shm.h:354-358` | "C# v3.23.0 will match against ..." forward-claim on `widgetConfirmDialogText` consumer | The v3.21.0 SHM field is exposed for native-side logging and C# observability; consumer-side matching is permanently deferred per project-final-release closeout. |
| 6 | `Native/login_shm.h:97-100` | "without a second SHM bump in v3.23.0" forward-version reference | The design intent (pack widget-text in here to avoid a future bump) is correct; the explicit "v3.23.0" forward-version is retired. |
| 7 | `Core/AutoLoginManager.cs:1473-1497` (`StepServerSelect`) | "Iter-3 will add server-select click via PostMessage Enter or JoinServerDirect RPC (v4 SHM)" forward-plan | Iter-3 plan extinct. **DEAD-ON-DALAYA retention rationale preserved** — the state stays as defensive non-Dalaya scaffolding (cheap insurance against a non-Dalaya retarget). |
| 8 | `Core/AutoLoginManager.cs:1499-1515` (`StepWaitServerLoad`) | Iter-2B/Iter-3 forward-plan language | Same treatment as #7 — retention rationale preserved, forward-plan retired. |
| 9 | `Core/AutoLoginManager.cs:1083-1089` (dispatch comment) | "Iter-2B implements phases 1-7 ... Iter-3 will add EnteringWorld + Complete" past-future tense conflict | Rule 7 "surface conflicts" — Iter-3 already happened (line 1090 says so); rewrote to describe current coverage of all phases without iteration framing. |
| 9b | `Core/AutoLoginManager.cs:1516-1517` (`StepCharSelect`/`StepEnteringWorld` XML doc headlines) | "Iter-3 (2026-05-17) — XYZ dispatch." date-stamped task-tracker headline | Replaced with plain dispatch-purpose headline. Body retained legacy code-block provenance ("AutoLoginManager.cs:2150-2326 in the legacy path"). Inline `// Iter-3 ...` attribution comments throughout the file are kept as historical anchors per Rule 11 codebase-convention matching — they describe code provenance, not aspiration. |
| 11 | (consequence of #1-#3) | `UseStateMachine` source-of-truth divergence | Resolved by the line-874 fix + the Config/AppConfig.cs:861-867 reframing. |

### Amended (spec edits to `eqswitch/CLAUDE.md` AUTOLOGIN SPEC)

| # | What was amended | Rationale |
|---|---|---|
| 8 | Wall-clock target widened from "35–50s" to "35–70s success-path; 120s native phase-4 budget enforced" | The 50s upper bound was a v3.16-era aspiration before the SM landed. Autonomous smokes consistently run 59-68s (per `memory/reference_eqswitch_native_phase_error_triggers.md` line 42 + the v3.22.12 ship doc's smoke note). Phase-4 dwell (`WaitConnectResponse`) is dominated by Dalaya login-server response latency, which is upstream and unimprovable client-side. |
| 9 | Added "Slow-server tolerance" paragraph: Native `login_state_machine.cpp:925-927` enforces a 120s per-phase budget; exceeding it calls `SetError`. Observed once (2026-05-17 17:39:48 gotquiz1 138s WaitConnectResponse event), classified upstream (login-server delay), no enhancement planned per `memory/reference_eqswitch_native_phase_error_triggers.md`. | The 138s event was the trigger that motivated v3.22.11's `okDisplay`-at-Error instrumentation. With v3.22.11+v3.22.12 armed, a next-occurrence is now diagnosable from the C# log alone. |
| 10 | Added "okDisplay torn-read defense" paragraph: detection is read-side only (`LoginShmWriter.cs:730` reads class → text → re-reads class, returns `(None, "")` on mismatch). Write-side `_ReadWriteBarrier()` flagged in the v3.18.1 verifier round is intentionally **not** shipped because read-side fail-closed handling is the load-bearing defense and no torn read has caused an autologin failure across all v3.22.x smokes. | `reference_loud_runtime_silent_rest` principle: loud surface (read-side detect → Warn log → bail to None) over silent hardening (write-side barrier). |

### Files

| File | Nature |
|---|---|
| `EQSwitch.csproj` | Version 3.22.12 → 3.22.13 |
| `Config/AppConfig.cs` | XML doc on `UseStateMachine` retired Iter-4/v3.23.0 forward-ship language; describes current state |
| `Core/AutoLoginManager.cs` | 5 comment edits: lines ~80, ~817, ~874, ~1474, ~1500 — all forward-ship language retired, DEAD-ON-DALAYA retention rationale preserved |
| `Native/login_shm.h` | 2 comment edits: lines ~98, ~356 — v3.23.0 forward-version refs retired |
| `CHANGELOG.md` | This entry |
| `CLAUDE.md` (eqswitch repo) | AUTOLOGIN SPEC amended for items 8, 9, 10 above |

### Risk

Zero. No code paths touched. No autologin-path keystroke, timing, SHM
contract, or state-machine transition logic is edited. Build expected to
remain 0 errors / 0 warnings. The C# default behavior for a fresh install
(legacy path) is unchanged from v3.22.12 — Nate's deployed config has
`UseStateMachine: true` and that is unchanged.

### No smoke required

Per the task brief's smoke trigger rule ("smoke any autologin-path code
change"), pure doc/comment edits don't qualify. The deployed binary's
runtime behavior is byte-equivalent to v3.22.12 modulo string-table
churn from edited comment blobs.

### Out of scope

- Any of the four PERMANENTLY REJECTED items (per-client tray "Enter
  World" submenu, Phase 4 tab split, selective hotkey re-register,
  defensive `eqmain_cxstr.h` AOB rescan). Nate's 2026-05-17 directive
  stands.
- Any of the eight accepted-as-known-limit items from the v3.22.10
  closeout (kBadNames over/under-block, P9/P8/uiFallback latch back-out,
  Path B2 SetCurSel storm throttle, etc.). Same triggers documented;
  same no-ship rationale.

## v3.22.12 — Defensive null-conditional on SendCancelCommand (2026-05-17)

Verifier-driven follow-up to v3.22.11. T3 Sonnet flagged
`AutoLoginManager.cs:1196 loginShm.SendCancelCommand(pid)` as fragile —
the v3.22.11 okDisplay read three lines above used `loginShm?.`, the
adjacent SendCancelCommand did not. The current code is safe by
control-flow invariant (line 911-918 early-returns on `writer.Open`
failure, bypassing the loop entirely), but explicit `?.` removes the
fragility-by-future-change concern. One-line hardening, no behavioral
change.

### Files

| File | Nature |
|---|---|
| `Core/AutoLoginManager.cs` | `loginShm.SendCancelCommand(pid)` → `loginShm?.SendCancelCommand(pid)` at line 1196 + comment explaining the change |
| `EQSwitch.csproj` | Version 3.22.11 → 3.22.12 |
| `CHANGELOG.md` | This entry |

### Risk

Negligible. The null-conditional is a no-op when `loginShm` is non-null
(the success path). The catch block at line 1199 already handles any
exception SendCancelCommand could throw. CS8602 warning at line 1196
no longer fires.

## v3.22.11 — Instrumentation: dump okDisplay at terminal Error (2026-05-17)

C#-only instrumentation ship. Zero behavioral change to the autologin
critical path. Adds a single `FileLogger.Info` call at the
`AutoLogin-SM: terminal state Error` entry point that captures the
`okDisplay` (class + text) snapshot from `LoginShm` via the existing
`ReadOkDisplaySnapshot` helper, so future failures expose what EQ
actually showed instead of only logging "terminal Error" with no
EQ-side diagnostic.

Motivated by the 2026-05-17 17:39:48 smoke event: gotquiz1 (PID 16136)
sat in `WaitConnectResponse` for 138.3s with `connect=1` oscillating,
then `nativePhase` flipped to `Error`. The C# log captured the
transition but not the okDisplay state — so root cause class
(bad-password / login-server queue / login-token-stale / SM bug) was
indeterminate. With v3.22.11, the same smoke now logs:

    AutoLogin-SM: okDisplay at terminal Error: class=<None|Recoverable|Fatal|Truncated>
                  text="<EQ's actual error string>" (PID <n>)

Paired with the local-only Python tool that tails the per-PID native
log files (`eqswitch-dinput8-{pid}.log` next to eqgame.exe) into a
merged timestamped stream. Two independent diagnostic streams that
previously both went to the void. (An earlier draft of the Python
tool subscribed to `DBWIN_BUFFER` — that was wrong because `DI8Log`
uses `fopen`+`fprintf`, not `OutputDebugString`. The T2 Opus verifier
caught it; the tool was rewritten as a file tailer same session.)

### Files

| File | Nature |
|---|---|
| `Core/AutoLoginManager.cs` | +13 LOC: try/catch wrapper around `ReadOkDisplaySnapshot(pid)` at the top of the `if (current == LoginPhase.Error)` block; logs class + text via `FileLogger.Info` |
| `EQSwitch.csproj` | Version 3.22.10 → 3.22.11 |
| `CHANGELOG.md` | This entry |

### Risk

Minimal. The new code path runs only at terminal-state-Error (rare).
`loginShm?` null-conditional and try/catch around the read mean any
SHM tear-down race surfaces as a `FileLogger.Warn` instead of throwing.
Success path of the state machine is byte-identical to v3.22.10.

### Not in scope

- The actual gotquiz1 138s bug — diagnosis only; fix waits for next-occurrence
  evidence captured by this instrumentation.

## v3.22.10 — Tray "Manage Teams..." deep-links to Configure Teams (2026-05-17)

C#-only polish. The tray submenu's "Manage Teams..." item now opens Settings
AND the Configure Teams subwindow in one click, instead of opening Settings
and leaving the user to find + click the "Configure Teams..." button on the
Accounts tab. "Manage Accounts..." and "Manage Characters..." continue to
open Settings to the Accounts tab — per Nate, the Accounts and Characters
sections are visually close enough that a tab split isn't worth shipping.

### What changes
- `UI/TrayManager.cs` — `ShowSettings` gains a `bool openTeamsDialog = false`
  optional parameter, threaded through to the `SettingsForm` ctor. The
  `Manage Teams...` tray item now passes `openTeamsDialog: true`. The other
  two submenu footers (`Manage Accounts...`, `Manage Characters...`) keep
  the previous `ShowSettings(2)` call shape. The re-entry path (Settings
  already open → `BringToFront` early-return) also honors `openTeamsDialog`
  via the new public `SettingsForm.OpenTeamsDialogNow()` — previously the
  flag was silently dropped on re-entry.
- `UI/SettingsForm.cs` — ctor gains `bool openTeamsDialog = false`. When
  true, subscribes once to `this.Shown` and, on fire, executes a two-stage
  defer: (1) `BeginInvoke` drains the paint cycle queued by `Show`; (2) a
  700ms `Timer` adds a human-perceptible "Settings is loaded, ready" beat;
  (3) the shared `OpenTeamsWithVisibleBusy()` helper flips `UseWaitCursor`
  to true immediately before `ShowTeamsDialog()` and restores it in
  `finally`. The wait cursor is the standard Windows "busy" signal and
  renders independently of the form's paint cycle, so it stays visible
  during the single-UI-thread freeze caused by `AutoLoginTeamsDialog`'s
  ctor. Earlier rounds layered a title-bar label (rejected by Nate) and a
  default-style `ToolTip` (rendered with bad opacity due to compositor
  races against the freeze) — both dropped. The pattern evolved across
  smoke rounds: round 1 synchronous → ~4s Settings flashes white; round 2
  `BeginInvoke` only → still ~3s of white-paint; round 3 → wait cursor +
  700ms beat, freeze legible. `IsDisposed` guards at both subscription and
  callback time prevent a fast close-before-paint crash. New public
  `OpenTeamsDialogNow()` method handles the re-entry path: same helper but
  skips the 700ms beat because Settings is already painted on re-entry.

  Companion speed-up: `UI/AutoLoginTeamsDialog.cs` ctor now uses
  `SuspendLayout` + `ResumeLayout(true)`, hoists the per-ComboBox
  `SlotOption` list out of `MakeCombo` (was rebuilt 12×) into a single
  `BuildComboItems()` call cached in `_comboItems`, and uses
  `Items.AddRange` instead of N×`Items.Add`. Cuts the freeze duration
  meaningfully on configs with non-trivial Accounts/Characters counts.

### What gets removed
- Three `TODO(Phase 4): route to section-specific tab` comments in
  `TrayManager.cs` (above each `Manage Accounts/Characters/Teams...` line).
  Phase 4 — the proposed Accounts/Characters/Teams tab split — is cancelled.
  The Accounts/Characters TrayManager TODOs were placeholders for that
  Phase-4 routing; with Phase 4 dead, they're stale aspiration. The Teams
  TODO is superseded by the actual deep-link this version ships.

### What does NOT change
- The autologin critical path. Zero edits to `AutoLoginManager`, native
  bridge, SHM contract, or any of the timing-critical state machines.
- Existing `ShowTeamsDialog()` semantics: still non-modal (per Nate's
  preference at SettingsForm.cs:121-122), still closes-on-OK, still
  rebuilds `_lblTeamSummary` from the staged team slots.
- Hotkey behavior while Settings is open — all hotkeys remain unregistered
  per `TrayManager.cs:1494`. The "open Settings → fire team4" workflow
  still needs the v3.22.9 workaround (fire team4 first, then open Settings
  during the autologin window). Selective hotkey re-register is a separate
  v3.23.0-scoped architectural change.

### Also fixed in this ship
- `Native/eqmain_cxstr.h` threat-model block — corrected stale "On every
  Dalaya patch the eqmain.dll RVAs and prologue bytes can drift" claim.
  Dalaya is a Rain-of-Fear-2 emulator running a fixed client; the server
  doesn't patch eqgame.exe or eqmain.dll, so the RVAs are frozen at the
  RoF2 May 10 2013 client level. ResolveCXStrFunctions() prologue check
  remains load-bearing as a defense against a wrong-RVA constant in our
  own code, not against a never-shipping client patch. Removed the
  fail-mode hierarchy item #3 ("AOB rescan on prologue signature (TODO
  Phase 4b)") — moot for the same reason. No code change, comment only.

### Smoke gate
1. Deploy `EQSwitch.exe` to `C:/Users/nate/proggy/Everquest/EQSwitch/`.
2. Right-click tray icon → Teams submenu → "Manage Teams...".
3. Expect: Settings window opens to the Accounts tab AND the Configure
   Teams subwindow pops on top.
4. Confirm "Manage Accounts..." (from Accounts submenu) still opens
   Settings to Accounts tab with no Configure Teams dialog.
5. Confirm "Manage Characters..." (from Characters submenu) same: Settings
   to Accounts tab, no Configure Teams dialog.

## v3.22.9 — Settings UI live-refresh on autologin completion (2026-05-17)

C#-only polish patch. Closes the last UI-side limitation called out in the
v3.22.8 ship doc: with Settings open during an autologin, the Accounts grid's
Flag column stayed on the prior session's glyph until the user closed +
reopened Settings. Now it updates within ms of `AutoLoginManager.LoginComplete`.

### What changes
- `UI/SettingsForm.cs` — adds optional `AutoLoginManager? autoLogin` ctor
  param. When non-null, subscribes to `LoginComplete` and re-syncs the
  AutoLoginManager-owned fields (`LastLoginAt` + `LastLoginResult`) from the
  live `_config.Accounts` into the staged `_pendingAccounts` snapshot, then
  re-renders the grid. `InvokeRequired`/`BeginInvoke` marshal to the UI
  thread defensively (FireLoginComplete already marshals via the captured
  sync context — the defensive check covers the synchronous-fallback path).
  `FormClosed` unsubscribes. `IsDisposed`/`Disposing` and the inner try/catch
  ensure a sync failure can't crash the form — live-refresh is polish, not
  on the critical path.
- `UI/TrayManager.cs:1497` — passes `_autoLoginManager` to `new SettingsForm`.

### What does NOT change
- Autologin flow itself — zero edits to `AutoLoginManager.RunLoginStateMachine`,
  the SM dispatch, the legacy `RunLoginSequence`, or any of the Combo G /
  P8/P9 publisher / SHM bridge code. The change consumes a post-hoc event
  and never touches the timing-critical path.
- The deep-copy form-open snapshot pattern (`_pendingAccounts =
  _config.Accounts.Select(...).ToList()`) — staged user edits are still the
  source of truth for everything except the two autologin-owned fields.
- `RefreshAccountsGrid` itself — same renderer, just called from a new
  event source.

### Why match by (Username, Server) and not by index
- User can re-order, add, or remove pending accounts in Settings while a
  background autologin is running. Index-based mapping would drift; the
  (Username, Server) tuple is the existing dedupe key elsewhere in the form
  (e.g. `ApplySettings` collision check) and is stable across in-memory
  edits.
- If the user has renamed a pending account before the autologin fires, the
  match returns null and the staged entry is left untouched — the user's
  rename takes precedence over the live glyph until ApplySettings saves the
  new name back to `_config.Accounts`.

### Why PID payload is unused
- `AutoLoginManager.LoginComplete` carries the EQ process PID but doesn't
  expose which Account that PID was for. Mapping PID→Account post-hoc would
  require either a parallel TrayManager-owned dictionary (PID→Account
  populated on BeginLogin, cleared on LoginComplete) or threading the
  Account through the event signature.
- Re-syncing every staged Account costs O(N×M) field-comparisons where N+M
  are both very small (Nate's config has <20 accounts total). The lookup
  machinery is not worth the savings — chose the simpler approach.

### Smoke gate
- Open Settings → fire team4 hotkey → without closing Settings, watch the
  Flag column for both accounts flip from prior-state to ✓ green (or ✗ red
  on bad password) within a few seconds of charselect-reached.
- Re-fire with a deliberately-bad-password account and confirm ✗ glyph
  appears live. New untried account stays at "—".

### Cross-cutting lesson (kept for future ports)
This is the second half of the `opt-in-dispatch-divergence` fix from
v3.22.8. The state-mutation parity was restored in v3.22.8 (SM path now
writes `LastLoginResult`); v3.22.9 restores the **UI-feedback parity** for
the open-Settings-during-autologin workflow. Both halves needed to land
for the indicator semantics to feel right — same code path, two visible
surfaces.

## v3.22.8 — Regression fix: defer SaveImmediate out of SM tick path (2026-05-17)

C#-only behavioral patch. Closes a v3.22.7 regression caught at first team4
smoke (post-deploy): both clients reached char-select and STOPPED there
instead of advancing to in-game. v3.22.5 + v3.22.6 smoke on the identical
team4 scenario both reached in-game cleanly.

### The regression

v3.22.7 introduced `ConfigManager.SaveImmediate` calls inside the
`RunLoginStateMachine` dispatch tick — specifically at the CharSelect
transition where the "ok" indicator was written. On a paired (multi-client)
smoke, both SMs hit that exact line within seconds of each other and
contended on the shared `ConfigManager._saveLock` (plus backup rotation
into `backups/`). Tick-time disk I/O + lock contention stalled the SM
long enough that `StepCharSelect` either failed to fire selection-by-slot
or fired it after the bridge state had shifted, leaving both clients
visible at the EQ char-select screen.

Single-client autologin probably would have worked because there was no
cross-SM contention. The bug surfaced only because the smoke methodology
fires two clients simultaneously.

### Why v3.22.5/v3.22.6 didn't show it

Neither release called SaveImmediate from the SM dispatch. v3.22.6 was a
pure Native code-motion reorder (no SaveImmediate anywhere new). v3.22.5
added the anchor-zero loop in Native only (no C# disk I/O). v3.22.7 was
the first release to introduce mid-tick disk I/O from the SM path.

The legacy `RunLoginSequence` calls SaveImmediate too (at line 2704) but
fires it AFTER the long blocking `WaitForScreenTransition` call — so two
clients' saves were naturally desynchronized by ~variable EQ render times.
v3.22.7's placement at the exact transition moment removed that desync.

### The fix

Move SaveImmediate OUT of the dispatch tick into the `RunLoginStateMachine`
`finally` block. Mark `pendingResult = "ok"` (or `"fail"`) in-memory only
during the tick, defer the disk write to SM exit.

Why this works:
- **Desynchronizes saves**: each SM exits at its own time (driven by
  per-client EQ render timing, network latency, etc.), not at the
  synchronized CharSelect transition moment. Lock contention spreads
  naturally.
- **Removes tick-time disk I/O**: SM dispatch ticks return to being pure
  in-memory state transitions. StepCharSelect's char-list polling +
  selection-ack waits aren't preceded by disk I/O latency.
- **Try/catch wraps the deferred save**: a SaveImmediate failure can't
  break the rest of the finally chain (loginShm Dispose, FireLoginComplete,
  writer Deactivate, charSelect Close). The save failure is logged but
  non-fatal — same posture as the legacy "fail" branch which doesn't gate
  on SaveImmediate success either.
- **okWritten latch semantics preserved**: still set true at CharSelect
  transition, still prevents downgrade if later phases fail. The change
  is purely WHERE the disk write happens, not WHAT gets written.

### Edit footprint

`Core/AutoLoginManager.cs`:
- New local `string? pendingResult = null;` alongside `okWritten` at SM init.
- "ok" mark block (CharSelect transition): `pendingResult = "ok"; okWritten = true;`
  + log. NO disk write here.
- "fail" mark block (terminal Error + EQ-alive): `pendingResult = "fail";`
  + log. NO disk write here.
- New deferred save in finally block (FIRST thing in finally, before
  the existing SendCancelCommand / Dispose / FireLoginComplete chain):
  if pendingResult non-null, do `account.LastLoginAt = DateTime.UtcNow;
  account.LastLoginResult = pendingResult; ConfigManager.SaveImmediate(_config);`
  wrapped in try/catch.

~25 net LOC delta vs v3.22.7 (mostly comment rewrites + the new finally
block). csproj `<Version>` bump 3.22.7 → 3.22.8.

### Verification

- Smoke gate: re-fire team4. Expected: both clients reach in-game like
  v3.22.5/v3.22.6 (functional path restored). Settings UI Flag column
  should still flip to ✓ for both gotquiz + gotquiz1 after autologin
  completes (the v3.22.7 indicator-fix intent is preserved, just with
  the disk write moved to SM exit instead of CharSelect transition).
- DebugView / `eqswitch.log` expected lines (per client):
  - `AutoLogin-SM: marked LastLoginResult=ok pending SM-exit save for <name> (charselect reached at t=...ms)` — fires at CharSelect transition
  - `AutoLogin-SM: deferred SaveImmediate(ok) for <name> at SM-exit` — fires when SM finally block runs

### Honesty disclosure

The root cause (lock contention vs tick-time disk I/O vs something else
entirely) is a hypothesis. Without `eqswitch.log` from the v3.22.7 failed
smoke I can't pin it definitively. The fix shape is robust either way:
even if SaveImmediate-in-tick wasn't the proximate cause, moving disk I/O
out of the dispatch tick is the architecturally correct choice. If
v3.22.8 smoke also fails, drop to log analysis.

## v3.22.7 — Settings UI Flag glyph: SM-path LastLoginResult writes (2026-05-17) — REGRESSION, superseded by v3.22.8

⚠ **v3.22.7 binary regressed multi-client autologin** — both clients
stopped at char-select on team4 smoke. See v3.22.8 entry above for root
cause + fix. Source + tag remain on origin; binary was superseded by
v3.22.8 before any persistence beyond proggy/mirror snapshots. The
intended v3.22.7 fix (SM-path LastLoginResult writes) is preserved in
v3.22.8 with the SaveImmediate moved to SM-exit.

C#-only behavioral patch. Closes `bug_eqswitch_login_complete_flag_stays_x` —
the Settings UI "Flag" column glyph never updated after a successful autologin
under v3.22.0+'s state-machine path. Both gotquiz + gotquiz1 stuck on prior-
session values (empty → "—" / "fail" → "✗") despite both passing Connect and
reaching in-game.

### Root cause

`Core/AutoLoginManager.cs` has two dispatch paths:

- **Legacy** `RunLoginSequence` — writes `account.LastLoginResult = "ok"` at
  `WaitForScreenTransition` success (~line 2704) and `= "fail"` at timeout
  (~line 2685), with `ConfigManager.SaveImmediate` to bypass the Windows-Forms-
  timer-backed `Save()` that can't tick on a background thread.
- **State machine** `RunLoginStateMachine` (v3.22.0+, dispatched when
  `LaunchConfig.UseStateMachine = true`) — **zero** writes to `LastLoginResult`.

v3.22.0 added the SM path as opt-in (default false) and shipped without porting
the indicator writes. Subsequent v3.22.x ships flipped Nate's config to opt-in.
The functional path always worked end-to-end (login completes, gameplay works),
so verifier rounds focused on the smoke critical path didn't surface the
indicator gap. Took until v3.22.5 smoke for the discrepancy to be visible
("both passed Connect, both flagged X").

Read site is `UI/SettingsForm.cs:2126` `RefreshAccountsGrid`, which maps the
string to a glyph via the switch at line 2143:
- `"ok"` → ✓ (green)
- `"fail"` → ✗ (red)
- _ → "—" (dim gray)

### The fix

`Core/AutoLoginManager.cs` `RunLoginStateMachine`:

- **New local** `bool okWritten = false;` initialized alongside `current` near
  line 870.
- **"ok" write** inside the phase-transition block (~line 1104): when the SM
  transitions into `LoginPhase.CharSelect` for the first time (`!okWritten`),
  set `account.LastLoginAt = DateTime.UtcNow; account.LastLoginResult = "ok";
  ConfigManager.SaveImmediate(_config); okWritten = true;` with the same
  write-order rationale as the legacy site (16-byte non-atomic
  `Nullable<DateTime>` ordering vs the atomic string-reference assignment).
  Logs the write for DebugView/file-log traceability.
- **"fail" write** inside the terminal Error block (~line 1138): only when
  `!okWritten` (we never reached charselect — login proper failed, not the
  post-login EnterWorld plumbing). Liveness-checks the EQ process via
  `Process.GetProcessById(pid)` + `.HasExited` before writing; skips with a
  log line if the process is gone (EQ crashed or user-killed → not the
  password's fault, per the risk register at `RunLoginSequence:2673-2676`).

### Behavior contract

- `okWritten = true` is the latch — once written, never downgrade. Matches the
  legacy comment at line 2697-2702: *"Everything past this point is character-
  selection / enter-world plumbing, not login. Mark ok now so a downstream
  MQ2-bridge timeout doesn't downgrade a successful login to ✗."*
- "fail" gate is "AutoLoginManager-owned timeout AND EQ still alive." Process-
  death (hwnd-zero / crashed) is silent — matches legacy `if (hwnd == IntPtr.Zero)
  { Report(...); return; }` at line 2694.
- Race-safe with `SettingsForm.cs:1729-1734` staging snapshot. When the user
  edits Settings during an autologin and the SM writes `LastLoginResult`, the
  staged snapshot still holds the form-open value; the existing
  `passwordUnchanged ? live!.LastLoginResult : a.LastLoginResult` apply logic
  (line 1749) reads the LIVE config back, so the SM write isn't clobbered by
  unrelated edits.

### What's NOT in v3.22.7 (deferred to v3.22.8 candidate)

- **Live grid refresh while Settings is open.** `RefreshAccountsGrid` reads from
  `_pendingAccounts` (deep-copied at form-open in `SettingsForm.cs:228`). There's
  no event subscription to `AutoLoginManager.LoginComplete`. A user who fires
  autologin while Settings is open won't see the flag flip until they close +
  reopen. v3.22.7 fixes the underlying state; the UI refresh-on-event hook is
  separate polish (~10 LOC: hook `LoginComplete`, invoke-marshal, per-account
  snapshot field copy, re-call `RefreshAccountsGrid`). Deferred to keep this
  release single-concern.
- The legacy `RunLoginSequence` path is unchanged. v3.22.0+ smoke uses the SM
  path exclusively; the legacy path is a fallback that already had correct
  writes. No reason to touch it.

### Verification

- Build clean (managed-only change, no Native rebuild needed but the cycle
  rebuilds both for consistency).
- Edit footprint: +~45 LOC across `Core/AutoLoginManager.cs` (one new local +
  two new write sites) + `<Version>` bump in `EQSwitch.csproj`.
- Smoke gate: re-fire team4. Watch Settings UI Flag column after autologin
  completes. Expected: both gotquiz + gotquiz1 show ✓ (green check) within
  seconds of charselect-reached. Re-open Settings if needed (the v3.22.8 live-
  refresh hook isn't in this release). DebugView / `eqswitch.log` should show
  one `AutoLogin-SM: LastLoginResult=ok for <name>` line per successful login.

### Inherited Known Limits (unchanged)

- `kBadNames` over-blocks (class/race/EQ-flavor short names). Pre-existing.
- P9/P8/uiFallback latch back-out gaps. Per v3.22.4 ship doc Known Limit #2.
- Settings UI grid doesn't live-refresh while open. Deferred to v3.22.8.

## v3.22.6 — Anchor-zero loop reorder (R1 verifier CRITICAL fix) (2026-05-17)

Native-only behavioral patch. Single-item ship addressing the only
CRITICAL flag from v3.22.5's R1 8-verifier sweep (T2 Sonnet callout).

### The gap

v3.22.5 added a zero loop in the Path C anchor-scan path so Path B2's
synthesized `"Slot N"` placeholders wouldn't ride the v3.22.4 P9
publisher gate into a published mixed state. The loop ran AFTER the
`names[0]` write, both inside the same `__try` as the existing anchor-
write block. Partial-failure semantics on SEH mid-zero-loop:

- `names[0]` already wrote successfully (target name in slot 0)
- `names[1..k]` zeroed before the SEH fired
- `names[k+1..N-1]` retain Path B2's `"Slot k+2".."Slot N"` placeholders

The combined publisher's P9 gate (v3.22.4) reads only `names[0]` — sees
plausible name → publishes — and surfaces the mixed state to C# and to
any DebugView / SHM-snoop reader. Exactly the cosmetic state the zero
loop was added to close, now reachable via partial-SEH.

Realistic SEH trigger is catastrophic DLL-detach during SHM write
(three coincident rare events: DLL detach + mid-loop timing + a
consumer reading SHM in the partial-state window). Verifier finding is
real but the consequence requires a scenario where everything else is
already broken. Documented as Known Limit in v3.22.5 ship doc; this
release closes it for posture cleanliness.

### The fix

Move the zero loop to BEFORE the `names[0]` write inside the same
`__try`. New partial-failure semantics:

- **SEH mid-zero-loop**: `names[0]` still holds whatever Path B2 wrote
  (likely `"Slot 1"`); `names[1..k]` zeroed; `names[k+1..N-1]` still
  hold Path B2 placeholders. P9 gate reads `names[0] = "Slot 1"` →
  `IsPlausibleName` rejects → publisher defers. **Safe.**
- **SEH during `names[0]` write (post-loop)**: `names[1..N-1]` all
  cleanly zeroed; `names[0]` partial/empty. P9 gate reads → rejects
  → publisher defers. **Safe.**
- **No SEH (success path)**: identical to v3.22.5 — `names[0]` = target,
  `names[1..N-1]` empty, P9 gate passes, publisher writes `charCount`.

Strictly safer partial-failure; identical success path.

### What's NOT in v3.22.6

- The "split the zero loop into its own `__try`" approach (also mentioned
  by T2 Sonnet) — declined. The reorder achieves the same partial-failure
  safety with no SEH-scope complexity increase. Two-`__try` would
  duplicate the `__except` log discrimination but the surrounding code
  doesn't need that distinction (it's all in the same anchor-scan logic).
- Bumping the `__except` log to distinguish "SEH during zero-loop" vs
  "SEH during names[0] write" — possible follow-up but the existing
  `"mq2_bridge: SEH writing anchor-scan name to SHM"` line already covers
  both cases coherently.
- The v3.22.5 Known Limits #2 (P9/P8/uiFallback back-out gaps) and #3
  (kBadNames over-blocks) — both unchanged. Still deferred per their
  respective rationales.

### Verification

- Native build clean: `Native/build-di8-inject.sh` exit 0.
- Edit footprint: ~5 LOC moved within the same `__try` block; +1 reorder
  comment block. No new latches, no new branches, no new globals.
- Empirical smoke: re-fire team4 on fresh `eqgame.exe` pair, same gate
  as v3.22.5 (gotquiz 10-char + gotquiz1 1-char both reach in-game,
  zero `MQ2 heap in slot-mode` lines). Regression surface is minimal
  because the success path is byte-identical to v3.22.5.

### Inherited Known Limits (unchanged)

- `kBadNames` over-blocks (class/race/EQ-flavor short names) — per
  v3.22.5 CHANGELOG. Zero new player loss vs v3.22.4.
- P9/P8/uiFallback back-out latch gaps — per v3.22.4 ship doc Known
  Limit #2. Deferred pending broader cycle-reset audit.

## v3.22.5 — Known Limits bundle: anchor zero / P9 SEH log / Path B predicate parity / null-poll reset (2026-05-17)

Native-only behavioral patch. Four convergent verifier-flagged Known Limits from
the v3.22.4 R1 8-agent sweep, bundled because all four touch
`Native/mq2_bridge.cpp` charselect path within ~45 LOC and share the
"defensive convergence around the existing P9 gate" theme. No new bugs caught
in v3.22.4's empirical smoke — this is hardening, not regression-fix.

### The four items

**1. Anchor-scan zeros `names[1..N-1]` before publishing** (T3 Sonnet R1).
`Native/mq2_bridge.cpp` Path C anchor-scan (~line 4115). When Path A bails
(P8 fires correctly), Path B's `ReadListItemText` returns empty, Path B2
synthesizes `["Slot 1".."Slot 10"]` into `shm->names[]`, then anchor-scan
succeeds and writes the target into `shm->names[0]` only — `shm->names[1..N-1]`
retain Path B2's placeholders. The v3.22.4 P9 gate (line ~4193) passes
because `names[0]` is plausible, publish goes through with mixed state:
`["target", "Slot 2", ..., "Slot 10"]` for a 10-slot account. C# functional
match against `names[0]` selects correctly and enters world on the right
character — but observable SHM is misleading for DebugView, log analysis,
and future SHM consumers. v3.22.5 adds a zero loop after the anchor write,
mirroring the standalone-anchor path's existing `for (int i = 1; i < N; i++)`
at line ~4337-4341. `shm->charCount` is left untouched — Path B2's count of
10 still publishes, but `names[1..9]` are now empty rather than `"Slot N"`
placeholders.

**2. P9 SEH-distinguishing log inside `__except`** (T3 Opus R1).
`Native/mq2_bridge.cpp` line ~4209. v3.22.4's P9 gate wrapped the
`IsPlausibleName(shm->names[0])` predicate in SEH but the `__except` was
empty. Any SEH from the SHM access (DLL detach race, MMF unmapped) silently
fell through to the gate's `else if (!g_p9GateLogged)` branch, which logs
`"entry[0] is placeholder or empty"` — wrong description for an SEH path.
v3.22.5 adds a new `g_p9SehLogged` static volatile latch (declared at line
~152 alongside `g_p9GateLogged`) and a one-shot `DI8Log` inside the
`__except` that explicitly names the SEH cause. Reset alongside
`g_p9GateLogged` at all three existing cycle-reset sites:
`pCharSelWnd` non-null transition (line ~3022), gameState=5 in-world
transition (line ~3805), and `Shutdown` / `Init` (line ~4475).

**3. Path B name-column discovery → `IsPlausibleName` parity** (T3 Sonnet R1).
`Native/mq2_bridge.cpp` line ~3920. Pre-v3.22.5, Path B's column-discovery
loop used an inline validator (uppercase first + length >= 4 + all-alpha-
either-case) to decide which column held character names. That validator
accepted column-header strings like `"Name"`, `"Race"`, `"Class"`, `"Level"`
— all of which `IsPlausibleName` already rejects via its `kBadNames`
blocklist. If the slot-0 row in `pCharList` exposed the header text before
any real character name (early-cycle race or non-standard Dalaya column
ordering), `g_cachedNameCol` would lock to the wrong column for the rest
of the session, silently feeding garbage to the per-entry filter at line
~3949. v3.22.5 replaces the inline validator with a single
`IsPlausibleName((const uint8_t *)test)` call. All six charselect sites
(Path A P8 gate, Path B column discovery, Path B per-entry filter, Path C
anchor, heap scan, P9 publisher gate) now agree on "looks like a real
character name."

**4. `g_consecutiveNullPolls` reset on `pCharSelWnd` transition** (T3 Sonnet R1).
`Native/mq2_bridge.cpp` line ~3014. The latch-clear counter resets at two
sites pre-v3.22.5: in-game gameState=5 transition (line ~3820) and
`Shutdown` / `Init` (line ~4495). But the existing comment at line
~3010-3013 notes that *"Dalaya keeps gameState=0 across BOTH login and
char-select, so the older gameState=5 reset path doesn't fire on Dalaya at
all."* The gameState=5 reset path is dead code for Dalaya — a session that
left the counter mid-count would carry it across charselect cycles and
could spuriously trip the 30-poll latch-clear threshold in the next cycle.
v3.22.5 adds `g_consecutiveNullPolls = 0;` to the `pCharSelWnd != nullptr`
reset block alongside the other latches (`g_p8GateLogged`,
`g_p9GateLogged`, `g_p9SehLogged`, etc.). The two existing reset sites are
preserved as defense-in-depth.

### Explicitly NOT in v3.22.5

- **P9 / P8 / uiFallback latch-reset gaps on charselect→login back-out,
  same-address re-use, mid-cycle flicker** (Known Limit #2 from v3.22.4 ship doc).
  Per the v3.22.4 guidance: *"fix all three together with broader cycle-reset
  audit, or skip all three."* Skipping for v3.22.5 — too speculative without
  a captured failure scenario, and per-latch divergent fixes would create the
  asymmetry the comment warns against. Holds for a future cycle-reset audit pass.

### What carries forward unchanged from v3.22.4

The P9 gate itself, all P8 gate sites, the `IsPlausibleName` predicate
(including the `kBadNames` blocklist), and the SHM contract are all
unchanged byte-for-byte. v3.22.5 only adds checks and zeros; no existing
publish path was relaxed.

### Verification

- Native build clean: `Native/build-di8-inject.sh` exit 0,
  `eqswitch-di8.dll` 242,688 bytes (+512 from v3.22.4's 242,176).
- Edit footprint: +61 / -16 across `Native/mq2_bridge.cpp` and the
  `<Version>` bump in `EQSwitch.csproj`.
- **Empirical smoke: ✓ PASSED 2026-05-17.** Team4 fired on fresh
  `eqgame.exe` pair (10-slot `gotquiz` + single-char `gotquiz1`).
  Both clients reached in-game.
- DebugView observability: a steady-state smoke should show at most one
  `reset heap-scan + slot-mode caches on charselect transition` per
  charselect activation. The internal `g_consecutiveNullPolls = 0`
  reset is intentionally not log-visible to avoid `DI8Log` churn.

### Known Limits inherited (still open)

- `kBadNames` blocklist blocks several legitimate-sounding strings as
  character names: class names (`Bard`, `Cleric`, `Druid`,
  `Enchanter`, `Magician`, `Monk`, `Necromancer`, `Paladin`, `Ranger`,
  `Rogue`, `Shaman`, `Warrior`, `Wizard`, `Beastlord`, `Berserker`,
  `Shadowknight`); player race names (`Human`, `Barbarian`, etc.); and
  EQ-flavor short title-case strings present in zone/server/chat tables
  (`Storm`, `Swift`, `Brave`, `Bold`, `Hunter`, `Shadow`, `Rider`,
  `Scout`, `Valor`, `Pride`). v3.22.5's Path B column-discovery
  promotion (item 3) extends this blocklist's reach to the column
  discovery path — a character literally named one of these strings
  would now fail Path B as well as P8/P9/heap-scan. Predicates were
  already aligned in 5 of 6 charselect sites; v3.22.5 made the 6th
  consistent. Net effect: zero new player loss (any character blocked
  here would also have been blocked at P8/P9/heap-scan, so autologin
  was already broken for them in v3.22.4). Inherited from v3.22.3 P8
  predicate. Pathological player names; no fix planned.
- P9 / P8 / uiFallback latch back-out gaps (per Known Limits #2 from
  v3.22.4 ship doc, deliberately not in v3.22.5 scope — see above).
- **Anchor-zero partial-SEH window (new in v3.22.5, deferred to v3.22.6):**
  The anchor-scan zero loop in Path C sits inside the same `__try` as
  the `names[0]` write. If SEH fires partway through the zero loop
  (catastrophic DLL-detach race during SHM access), `names[0]` holds
  the target name (already written) and `names[1..k]` are zeroed while
  `names[k+1..N-1]` still hold Path B2's `"Slot N"` placeholders — the
  exact mixed state the loop was added to prevent. v3.22.6 fix: move
  the zero loop to BEFORE the `names[0]` write so partial-SEH yields
  clean-empty state (which the v3.22.4 P9 gate then defers correctly)
  rather than mixed state. Realistic failure mode is dominated by the
  catastrophic SEH itself (DLL is detaching); the verifier finding is
  real but the risk window requires three coincident rare events. T2
  Sonnet R1 callout.

### Behavior contract

All four items are additive checks or zeros. None of them change the
publisher's success-path semantics: when `entry0Real` is true at the
P9 gate, the publish still fires identically to v3.22.4. The Path B
column-discovery change (item 3) tightens the validator but the rejected
strings (`"Name"`, `"Race"`, etc.) are not valid character names in any
EQ deployment — no real player loss versus the inherited kBadNames
caveat above. The anchor-scan zero (item 1) changes observable SHM
state but **`shm->charCount` is deliberately left at Path B2's count
of 10**, not reset to 1 — C#'s functional name-match against
`shm->names[0]` is identical pre- and post-fix because C# match-by-name
ignores empty slots; resetting `charCount` to 1 would be a behavioral
change to the publisher invariant that v3.22.5 intentionally avoids.

## v3.22.4 — P9 publisher plausibility gate (2026-05-17)

Native-only behavioral patch. Closes the multi-character-account regression
the v3.22.3 11:14 smoke captured: 10-slot `gotquiz` account stuck at
char-select with `MQ2 heap in slot-mode (10 placeholder slot(s))`, while
the single-character `gotquiz1` account on the same team4 fire succeeded
end-to-end (Backup+Natedogg sister-smoke also clean prior session). v3.22.3
gated Path A's two publish sites but left a third combined publisher
unguarded.

### The gap v3.22.4 closes

`Native/mq2_bridge.cpp` has three writers of `shm->charCount` for the
char-select pipeline:

1. **PopulateCharacterData** (LoginShm path, ~line 3313) — gated by v3.22.3
   P8 at the function head.
2. **Path A in-Poll** (CharSelectShm trust-offset path, ~line 3881) — gated
   by v3.22.3 P8 immediately before the per-entry name read.
3. **Path B+B2+C combined publisher** (~line 4193) — the SINGLE writer for
   everything the UI-derived and heap-scan paths populate. Pre-v3.22.4 this
   was `if (count > 0) { shm->charCount = count; ... }` with **no
   plausibility check**.

Path B2 (CListWnd slot probe, ~line 3968) synthesizes `"Slot %d"`
placeholder names into `shm->names[i]` via `wsprintfA` when GetItemText
returns empty columns — the legitimate slot-based-mode display fallback
for Dalaya's char-select widget. Path C (heap scan + anchor scan,
~line 4047) runs immediately after Path B2 and overwrites those
placeholders with real names IF the heap is populated. During the transient
window when Path A has bailed (P8 fired correctly) AND Path C's heap is
not yet populated, the combined publisher fired anyway:

- `shm->charCount = 10` published with `shm->names = ["Slot 1", "Slot 2",
  ..., "Slot 10"]`.
- C# `AutoLoginManager.cs:1460` char-list wait loop short-circuits on
  `ReadCharCount(pid) > 0` (alongside `IsCharSelectReady`), consumes the
  placeholder names, name-match fails, falls into the slot-mode-detection
  branch at line 1535, and bails to `LoginPhase.Error` with the
  user-visible `"MQ2 heap in slot-mode (10 placeholder slot(s)) —
  character names unavailable"` message.

The regression was **single-account-size-dependent**: single-character
accounts have a narrow settle window that Path A consistently wins.
Multi-character accounts (≥ ~5) widen the window enough for Path B2's
synthesis to win the publisher race. The v3.22.3 verifier rounds and the
v3.22.3 09:48 single-box smoke could not surface this — the 11:14 dual-box
team4 smoke with the 10-slot account did. Verifier R1 T2-Opus and R2 T3
both noted the unguarded publisher path in their "deferred items" sections;
v3.22.4 elevates that to a P0 fix.

### The fix

`Native/mq2_bridge.cpp`:

- **Publisher gate** (~line 4193) — after the existing `count > 0`
  precondition, read `shm->names[0]` and require it pass `IsPlausibleName`.
  Failure logs once-per-cycle via `g_p9GateLogged` then leaves
  `shm->charCount` unchanged and `charDataRead = false` so the next poll
  retries. Identical defer-and-retry semantics to v3.22.3's P8 sites. SEH
  wrap around the predicate read for defense — the SHM is local memory but
  the pattern matches the surrounding sites.
- **`g_p9GateLogged` diagnostic latch** (line 151, alongside
  `g_p8GateLogged`) — one-shot per charselect cycle. Resets at the three
  existing cycle-reset sites: `pCharSelWnd` non-null transition (line
  3021), gameState=5 in-world transition (line 3804), and `Shutdown`
  (line 4474). Operational signal: one log line per cycle = transient
  population window (expected, harmless). Multiple lines = signal Path
  B2 is the only path producing data AND name slots are not converging
  to real names (likely Dalaya client patch shifted
  `OFFSET_CHARSELECT_ARRAY` or the heap-scan stride / blocklist).

`IsPlausibleName` (line 190, pre-existing) enforces strict title-case
(uppercase first, lowercase rest), 4–15 chars, and the `kBadNames`
UI-label blocklist. Critically rejects `"Slot 1"` (space at position 4
fails lowercase-rest predicate AND digit at position 5 also fails) and
empty strings (length 0 fails 4-char minimum). Same predicate used by
Path A's P8 gate, Path B's per-entry filter, Path C's heap-scan readers,
and the anchor-scan validation — all five charselect paths now agree on
"looks like a real name."

### What it does NOT fix (Known Limits)

- **Anchor-scan-into-Path-B2-leftovers** (deferred to v3.22.5+). When
  Path A bails, Path B2 synthesizes `["Slot 1", ..., "Slot 10"]`, then
  anchor-scan succeeds and writes the target name into `shm->names[0]`
  only — `shm->names[1..9]` retain Path B2's `"Slot 2".."Slot 10"`
  placeholders. The v3.22.4 P9 gate passes (entry[0] is real), publish
  goes through, C# matches by name against `["Nate", "Slot 2", ...]`
  → finds "Nate" at index 0 → selects slot 1 → enters world correctly.
  The wrong-slot placeholders are cosmetically wrong but functionally
  inert. Fix would be: anchor-scan zeros names[1..N-1] before publishing.
  Bundle with the v3.22.0 Iter-5 architectural cleanup.
- **P9 diagnostic latch reset gaps** (T2 R2 verifier flag, inherited
  from P8). Misses charselect→login back-out, same-address charselect
  re-use, `pinstCCharacterSelect` mid-cycle flicker. Diagnostic-only
  impact. Same pattern as `g_uiFallbackLogged` / `g_p8GateLogged` —
  fix all three together or skip all three.

### Verification

- Native build clean: `Native/build-di8-inject.sh` exit 0,
  `eqswitch-di8.dll` size ~241KB (small growth from new diagnostic
  string + branch).
- `IsPlausibleName` already linked into the DLL (used by P8 gate and
  every heap-scan reader); the P9 call site is a folded-instruction
  add. `g_p9GateLogged` adds 1 byte (volatile bool) to BSS.
- Empirical smoke: deferred to next-launch cycle. Re-run team4
  hotkey on fresh `eqgame.exe` pair. Expected: `gotquiz` account
  publishes `char-list ready: 10 characters — Nate, <slot2>, ...,
  <slot10>` with real names, advances through char-select to
  `EnteringWorld → Complete`. No `"MQ2 heap in slot-mode"` line for
  either client.
- P9 gate fires logged via `DI8Log` → `OutputDebugString`, NOT
  `eqswitch.log`. To observe gate firing, run DebugView with filter
  `mq2_bridge:` during the smoke. Steady-state success without the
  slot-mode error is the proxy in `eqswitch.log`.

### Behavior contract

- Gate checks ONLY `shm->names[0]`. Subsequent slots continue to rely on
  the existing per-entry filters in Path A (line ~3855), Path B (column
  reader at ~3949), Path C heap scan (line ~4056), and anchor scan
  (validation in callers). The intent is "did real names land in slot 0"
  — a first-byte proxy for "the publish has real data." Strengthening
  to all-slots-pass would re-introduce the validate-then-latch
  antipattern v3.22.1 deleted and v3.22.2 buried.
- Gate has no success latching — `charCount` is republished each poll
  while entry[0] stays real. `g_p9GateLogged` IS latched but only on
  the diagnostic side (one log line per cycle to keep DebugView signal
  high without flooding). Cleared on cycle transitions alongside
  `g_p8GateLogged` and `g_uiFallbackLogged`.

## v3.22.3 — P8 first-entry plausibility gate (2026-05-17)

Native-only behavioral patch. Adds a single `IsPlausibleName(entry[0])`
check at the top of each Path A SHM-publish site so the bridge does not
publish a structurally-valid-but-empty character list when EQ has settled
the array's `Count` + `Data` pointer but not yet filled in the per-entry
name strings.

### The gap v3.22.3 closes

`Native/mq2_bridge.cpp` has two Path A "trust the offset" SHM-publish
sites: `PopulateCharacterData` (LoginShm) and the in-Poll Path A handler
(CharSelectShm). Each previously gated on `count ∈ [1, LOGIN_MAX_CHARS]`
and `IsReadablePtr(data, CSI_SIZE)`, then ran a per-entry name-byte
filter and wrote whatever passed. The in-Poll site also runs a per-entry
`IsPlausibleName` check (line ~3814 of the post-v3.22.2 source). All
sanity gates pass during the small window after `pinstCCharacterSelect`
transitions non-null but BEFORE EQ has settled the heap-allocated entry
data — `Count` and `Data` settle first, name bytes are filled later.
During that window:

- PopulateCharacterData writes N empty name strings (per-entry filter
  collapses unsettled bytes to `nameLen = 0`) and publishes
  `shm->charCount = N`.
- In-Poll Path A writes N empty name strings AND latches
  `shm->charSelectReady = 1` with a comment claiming "Path A wrote real
  names" that does not actually hold in this window.

The 2026-05-17 09:48 v3.22.2 empirical smoke captured this directly:
the 10-character `gotquiz` account's char-list came back as `Slot 1,
Slot 2, ..., Slot 10` — bridge published `count = 10` with 10 empty
`names[i]` strings, then the C# consumer fell back to slot-mode
placeholder display. Login still completed (slot-mode fallback), but
the published SHM lied about readiness for the duration of the window.

### The fix

`Native/mq2_bridge.cpp`:

- **PopulateCharacterData (~line 3260)** — after the existing
  count/pointer sanity gates, read entry 0's name bytes and require
  they pass `IsPlausibleName`. The first plausibility-fail returns
  zero (`charCount = 0`, `selectedIndex = -1`) and the next poll
  retries — identical bail-out semantics to the existing `Count == 0`
  / `IsReadablePtr` fail paths.
- **In-Poll Path A (~line 3836)** — restructure the existing structural
  guard to nest an inner `if (!IsPlausibleName(data + CSI_NAME_OFF))`
  check. Failure leaves `charDataRead = false` so Path B (UI fallback)
  runs this tick and the next-tick Path A retries.
- **Diagnostic log** (`g_p8GateLogged`, line 137) — one-shot per
  charselect cycle. First gate-fire emits a `DI8Log` line naming the
  call site; subsequent fires within the same cycle stay silent. Resets
  alongside `g_uiFallbackLogged` at the three existing cycle-reset
  sites (post-charselect transition, post-in-world transition,
  `Shutdown`). Operational signal: one log line per cycle = expected
  transient population window; log fires once but bridge never publishes
  charCount > 0 = `OFFSET_CHARSELECT_ARRAY` is likely wrong (e.g., a
  Dalaya client patch moved the array). Convergent recommendation from
  the verifier round — T2-Opus, T3-Sonnet, T3-Opus all flagged the
  missing breadcrumb.

`IsPlausibleName` (line 171, pre-existing) enforces strict title-case
(uppercase first, lowercase rest), 4–15 chars, and the kBadNames UI-label
blocklist. Same predicate used by Path B and Path C heap-scan readers,
so the publish path now agrees with the scanners on what "looks like a
real name."

### Why P8 only

v3.22.2's CHANGELOG enumerated three deferred items:
1. P8 first-entry plausibility gate (shipped here).
2. Per-entry `IsReadablePtr(data, count * CSI_SIZE)` tightening.
3. Path B2 `SetCurSel(0..N)` storm throttle.

#2 tightens a defense SEH already catches (worst case today is one bad
poll → zero). #3 prophylactically throttles a flicker that has not been
observed. P8 closes a real, smoke-captured failure mode: today's
gotquiz "Slot 1..Slot 10" lie. Shipping #1 alone keeps the patch
surgical (~14 LOC of behavior change across two sites, plus the
in-flight diagnostic addition described below — one new `static volatile
bool g_p8GateLogged` global mirroring the existing `g_uiFallbackLogged`
pattern). #2 and #3 stay bundled with the v3.22.0 Iter-5
architectural-cleanup backlog.

### Verification

- Native build clean: `Native/build-di8-inject.sh` exit 0,
  `eqswitch-di8.dll` 241,152 bytes — **same size** as v3.22.2 and
  v3.22.1. `IsPlausibleName` already linked into the DLL (used by Paths
  B2, C, and the existing per-entry check at the Path A site), so the
  new call is a pure folded-instruction add with no string-pool growth.
  SHA256 differs from v3.22.2 (PE `TimeDateStamp` + the new call-site
  bytes), confirming the gate is in the binary.
- C# Debug + Release builds clean (version-bump only on C# side).
- Empirical smoke: deferred to next-launch cycle. The 2026-05-17 09:48
  v3.22.2 smoke validated the SM end-to-end with the failure mode P8
  closes still latent; running clients still have the v3.22.2 DLL
  injected and were left alone. The v3.22.3 DLL takes effect on the
  next fresh `eqgame.exe` launch via the deployed tray. A re-run of
  the team1 hotkey on a fresh client pair will exercise the gate.

### Behavior contract

- The gate checks ONLY entry 0. Subsequent entries continue to rely on
  the existing per-entry `IsPlausibleName` (in-Poll site, ~line 3855)
  and the existing per-entry charset filter (`PopulateCharacterData`,
  ~line 3289). The intent is "did EQ settle the data yet" — a first-byte
  proxy — not "is every entry valid". Strengthening to all-entries-pass
  would re-introduce the validate-then-latch antipattern v3.22.1 deleted.
- The gate has no latching on success (`charCount = 0` is published on
  each failed poll, retried next tick). `g_p8GateLogged` IS a latch but
  only on the diagnostic side — one log line per cycle to keep ops
  signal high without flooding. Cleared on cycle transitions.
- The misleading `// v3 latch: Path A wrote real names` comment at the
  in-Poll site is now structurally accurate for entry 0 (we only reach
  the latch when entry 0 passed IsPlausibleName), but remains imprecise
  for entries 1..N-1 (which may still fail per-entry IsPlausibleName
  and be written as empty strings). This is intentional — slot-mode
  fallback handles those — and noted here so future readers know not
  to "fix" the comment by tightening the gate.

### Known limits surfaced by the verifier round

- **`IsPlausibleName` requires `len >= 4`** (existing predicate, line 182).
  If Dalaya ever permits 3-char character names, Path A would
  permanently reject them and the C# selector would fall back to
  slot-mode. The same constraint already applies to the per-entry
  filter at the in-Poll site (line ~3855) and to Path C heap-scan, so
  this is not a new regression — but P8 makes the limit load-bearing
  on the LoginShm publish path for the first time.
- **`kBadNames` blocklist gaps** (T2-Sonnet, T2-Opus). Common eqmain
  button labels — "Press", "Enter", "World", "Loading", "Connect",
  "Cancel", "Quit", "Delete", "Create" — are NOT in the blocklist and
  would pass `IsPlausibleName` if a wrong-offset bug landed `data` on
  the button-label string table. Pre-existing weakness; the diagnostic
  log added here makes such a regression visible (gate would NOT fire
  but published names would be button labels). Bundle to v3.22.x.
- **Entries 1..N-1 partial-fill window** (T3-Sonnet, T3-Opus). If
  entry[0] settles before entries 1..N-1, Path A publishes a mix of
  real + empty names with `charSelectReady = 1`. C# slot-mode fallback
  handles the empties, but the SHM lies for the duration of the window.
  P8 only narrows this window — it doesn't close it. Closing requires
  the deferred per-entry `IsReadablePtr(data, count * CSI_SIZE)`
  tightening (#2 from v3.22.1's backlog), bundle to v3.22.x.
- **PopulateCharacterData early-return doesn't zero
  `charNames[]`/`charLevels[]`/`charClasses[]`** (T3-Opus). The mirror
  branch at the post-loop tail zero-pads those arrays; the new
  early-return at ~3260 and the pre-existing `Count == 0` /
  `IsReadablePtr` early-return at ~3245 do NOT. `charCount = 0` makes
  conforming consumers ignore stale entries, so impact is bounded —
  but a defensive consumer reading `charNames[0]` regardless would see
  the prior tick's name. Pre-existing across all early-returns; bundle
  to v3.22.x with the per-entry tightening above.
- **`g_p8GateLogged` reset gaps on uncommon transitions** (R2
  verifiers — T2-Sonnet, T2-Opus, T3-Sonnet). The three reset sites
  cover the standard charselect → in-world flow but NOT:
  (a) charselect → login back-out (Dalaya holds `gameState==0`,
  `pinstCCharacterSelect` goes non-null → null with no reset);
  (b) same-address charselect-reuse after a back-out
  (`lastObserved == pCharSelWnd` skips the transition block);
  (c) `pinstCCharacterSelect` mid-cycle null flicker. Same pattern
  shared with `g_uiFallbackLogged` — diagnostic-only impact (operator
  may see one log line per full session instead of per cycle), no
  runtime behavior bug.
- **`g_p8GateLogged` is `static volatile bool`, not atomic** (T2-Opus
  R2). Two-thread races (`PopulateCharacterData` from `ActivateThread`
  vs Path A from `Poll`) can in principle produce ≤2 duplicate log
  lines per cycle under contention. Effect is mild — log clutter, not
  a runtime defect. The "one-shot" framing is best-effort, not strictly
  serialized. Documented honestly rather than upgraded to
  `InterlockedExchange8` since the impact is cosmetic.

### Verifier discipline

1 round × 6 agents (3 topics × Sonnet+Opus, normal stakes per
`completion-checkpoint.sh` v4.1 — Native edit forces normal-tier).
Verdict: 2 APPROVE, 4 CONCERNS, 0 REJECT. The convergent fix
(diagnostic log on gate fire, flagged by T2-Opus + T3-Sonnet + T3-Opus)
was applied in-flight. The pre-existing weaknesses surfaced by the
gap-audit pair (kBadNames blocklist gaps, entries 1..N-1 partial-fill,
PopulateCharacterData early-return zero-pad gap, per-entry IsReadablePtr
tightening) are documented under "Known limits" and bundled to v3.22.x.

## v3.22.2 — Bridge dead-code cleanup (2026-05-17)

Native-only patch. Burying the corpse left over from the v3.22.1
trust-the-offset fix. No runtime behavior change — pure deletion plus
a handful of comment edits that referenced the deleted symbols by name.

### What's gone

`Native/mq2_bridge.cpp`:

- **Functions deleted**: `IsValidCharArray` (multi-layer name-pattern
  validator that gated the offset discovery) and `ValidateCharArrayOffset`
  (the validate-then-permanently-latch wrapper around it, including its
  32k-iteration wide-scan fallback). Both had zero production callers
  after v3.22.1.
- **Globals deleted**: `g_offsetValidated`, `g_validatedOffset`,
  `g_charArrayNotFoundLogged` — the latching flags that broke the SM path
  whenever EQ had not populated `charSelectPlayerArray` at the moment of
  the first poll after `pinstCCharacterSelect` transitioned non-null.
- **Reset blocks pruned**: the `pinstCCharacterSelect` transition block,
  the in-game transition block, and `MQ2Bridge::Shutdown()` no longer
  reset the three removed globals (3 reset sites touched).
- **Wrong-comment buried**: the pre-v3.22.1 block at the old
  `ValidateCharArrayOffset` site that claimed "MQ2 RoF2-emu IS x64 and
  the x86 offset is currently unknown" went with the function. That
  comment was load-bearing for the 5-hour misdiagnosis chain that
  preceded v3.22.1; deleting it prevents pattern resurrection in a
  future refactor.
- **Comment edits**: the rationale comments in `PopulateCharacterData`,
  the Path A site in `Poll`, and `EmitVerificationReport` no longer
  reference the deleted symbols by name. v3.22.1's CHANGELOG entry
  remains the historical record.

### Why now

`bury-the-corpse` pattern: dead code that contains a load-bearing
anti-pattern is more dangerous than dead code with no semantic content.
Leaving `IsValidCharArray` + `ValidateCharArrayOffset` in place tempted
a future refactor to "restore" the validator under a different name and
silently re-introduce the give-up latch.

### Why nothing else

#2 (`IsReadablePtr(data, count * CSI_SIZE)` per-entry tightening) and
#3 (Path B2 `SetCurSel` storm throttle) from v3.22.1's "Known follow-ups"
are deferred to a coherent v3.22.x / Iter-5 architectural-cleanup bundle.
Both are behavior changes without an observed failure mode; v3.22.1 just
shipped 5 hours ago and is empirically validated (20+ real char names
across dual-box smokes), so adding prophylactic gates on top would
dilute that baseline. SEH already catches the per-entry failure mode #2
addresses; #3's "could cause flicker on slow populates" has no repro.

### Verification

- Native build clean: `Native/build-di8-inject.sh` exit 0,
  `eqswitch-di8.dll` 241,152 bytes — **same size** as v3.22.1 (the
  deleted statics were already DCE'd by MSVC's `/OPT:REF` at v3.22.1
  link time, so removing the source contributes zero linkable bytes).
  The DLL's actual SHA256 differs from v3.22.1's (PE `TimeDateStamp` +
  read-only section layout shift from the removed `kBadNames` string
  pool that lived in `.rdata`); zero behavioral diff in the .text
  section. v3.22.1's `cmp -l` against this build shows differences in
  header + .rdata only, not .text.
- C# Debug + Release builds clean (version-bump-only on C# side).
- No empirical smoke required — v3.22.1's empirical baseline still
  applies because no runtime behavior changed.

### Verifier discipline

1 round × 6 agents (normal stakes — `Native/` path forces normal-tier
per `completion-checkpoint.sh` v4.1 even though the change is
pure-deletion + comment edits).

## v3.22.1 — Native MQ2 bridge trust-the-offset fix (2026-05-17)

Patch release fixing a latent intermittent bug in the MQ2 bridge's
character-list extraction that the v3.22.0 SM path surfaced reliably.
Native-only behavioral change; C# version bump only.

### The bug

`Native/mq2_bridge.cpp` had a two-tier validate-then-give-up
character-array-offset discovery:

1. Try `charSelectPlayerArray` at hardcoded `OFFSET_CHARSELECT_ARRAY = 0x18EC0`
   (per MQ2 RoF2-emu `EverQuest.h:963` — the canonical x86 emu offset).
2. If `IsValidCharArray` rejects (Count not in [1,10], or Data not in
   heap range, or first-name not plausible title-case, or level garbage)
   → fall through to wide scan `0..0x20000`.
3. If wide scan also fails → set `g_charArrayNotFoundLogged = true` →
   permanently skip the array path until the next
   `pinstCCharacterSelect` transition (i.e. user leaves char-select and
   returns — never happens during an auto-login session).

The dominant failure mode was **timing, not the offset**: when Path A
runs on the FIRST poll after `pinstCCharacterSelect` transitions
non-null, EQ has not yet populated the array (`Count == 0`). The
strict validator rejects, the wide scan also rejects (nothing valid
yet), and the give-up latch trips. Subsequent polls — when EQ HAS
populated the array — never re-try. The SM then sits in the 30-second
`kMaxCharListWaitMs` wait + aborts. The Iter-3 2026-05-16 22:05 smoke
worked by luck of timing (EQ happened to populate before first poll);
the 2026-05-17 00:00 smoke failed because the timing was off.

### The fix (MQ2-canonical "trust the offset")

`Native/mq2_bridge.cpp`:

- **PopulateCharacterData (line ~3335)** and **Path A inside the bigger
  poll handler (line ~3899)**: skip the validate-then-give-up dance.
  Trust `OFFSET_CHARSELECT_ARRAY` directly each poll, with defensive
  `count ∈ [1, LOGIN_MAX_CHARS]` + `IsReadablePtr(data, CSI_SIZE)`
  sanity gates. A `Count == 0` poll just returns zero and the next
  tick retries — same shape MQ2's `MQ2CharSelectListType.cpp` uses
  (`pEverQuest->charSelectPlayerArray.GetCount()` direct access, no
  validation overhead).
- **Path B2 UI-fallback slot-probe (line ~4054)**: removed the
  `if (curSel >= 0)` gate. On Dalaya the char-select screen loads
  with no selection (`curSel == -1` is the legit initial state).
  The SetCurSel/GetCurSel probe itself is agnostic to prior selection;
  the gate was dead-locking the fallback whenever Path A hadn't yet
  populated AND the user hadn't manually clicked a character. Origin
  selection is now restored only if `>= 0` (no-op for the `-1` case
  with the side benefit of explicit intent).

### Empirical verification

Live smoke 2026-05-17 — dual-box Dalaya, two runs both end-to-end:

**Run 1 (00:30:38) — Team1 (Backup gotquiz + Natedogg gotquiz1):**

```
Backup (10-char account, configured slot 2):
  char-list ready: 10 characters — Acpots, Backup, Healpots, Jonopua,
    Nate, Potiongirl, Potionguy, Staxue, Thazguard, Zfree (PID 35512)
  selector → explicit slot 2 (PID 35512)         [Path A success]
  CharSelect → EnteringWorld (t=51s)
  EnteringWorld → Complete (t=63s)

Natedogg (1-char account, configured slot 0):
  char-list ready: 1 characters — Slot 1 (PID 37836)
  selector → single-char structural fallback → slot 1 = 'Natedogg'
  CharSelect → EnteringWorld (t=58s)
  EnteringWorld → Complete (t=71s)
```

Run 1 mixes paths: Backup landed via Path A (trust-the-offset extracted
all 10 real names); Natedogg landed via Path B2 slot-mode fallback
(Path A returned `Count=1, Data=slot-placeholder` for the single-char
account — this is a separate Native quirk for 1-char accounts where
the heap-allocated CharSelectInfo array contains a placeholder rather
than the real name; v3.22.0 Iter-3 single-char structural fallback
covers this case and remains load-bearing).

**Run 2 (00:38:01) — Team4 (Natedogg gotquiz1 + Nate gotquiz, both
configured slot 0 to force name-match path):**

```
Natedogg (1-char account):
  char-list ready: 1 characters — Natedogg (PID 21568)   [REAL NAME via Path A]
  selector → name match 'Natedogg' at slot 1 (PID 21568)
  CharSelect → EnteringWorld (t=55s)
  [Enter World stage: lost EQ window during PulseKey3D fallback —
   separate EQ-client-crash issue, NOT a bridge regression]

Nate (10-char gotquiz):
  char-list ready: 10 characters — Acpots, Backup, Healpots, Jonopua,
    Nate, Potiongirl, Potionguy, Staxue, Thazguard, Zfree (PID 22572)
  selector → name match 'Nate' at slot 5 (PID 22572)     [Path A success]
  CharSelect → EnteringWorld (t=53s)
  EnteringWorld → Complete (t=67s)
```

Run 2 confirms Path A trust-the-offset extracts REAL names for both
multi-char AND single-char accounts when timing allows — and that the
C# `CharacterSelector.Decide` name-match path resolves "Nate" against
the 5th slot correctly. The Natedogg `lost EQ window` in Run 2 happened
at the PulseKey3D Enter-World stage (post-charselect), not at the
bridge; tracked separately for v3.22.x.

20+ real character names extracted across both runs = strongest
possible offset verification (matches MQ2 RoF2-emu `EverQuest.h:963`
exactly). Timing matches Iter-3 baseline (60-70s).

### Known follow-ups (v3.22.2 / v3.22.x)

Verifier rounds (2 × 6 agents) on v3.22.1 surfaced these deferred items:

- `ValidateCharArrayOffset`, `IsValidCharArray`, `g_charArrayNotFoundLogged`,
  `g_validatedOffset` machinery is now fully orphaned (no production
  caller after this fix; `EmitVerificationReport` now reads
  `OFFSET_CHARSELECT_ARRAY` directly). Cleanup-to-zero deferred.
- `IsReadablePtr(data, CSI_SIZE)` only validates the first CSI_SIZE
  bytes. Loop reads entries 1..(count-1) without per-entry validation.
  SEH wraps the loop so the worst case is one bad poll + return zero,
  but `IsReadablePtr(data, count * CSI_SIZE)` would tighten the gate.
- Path B2 slot-probe (`SetCurSel(0..N)`) now runs on every poll while
  Path A returns zero. On Dalaya CListWnd mid-populate this could
  cause visible UI flicker. Throttling or caching probeCount==0 result
  would prevent the storm.
- Single-char Path A asymmetry: the 1-char account sometimes returns
  a "Slot 1" placeholder via `charSelectPlayerArray` (Run 1) and
  sometimes returns the real name (Run 2). Root cause: bridge's
  single-char heap-allocation pattern. Worth a focused investigation.
- `probe_charselect_offset.ps1` name-filter is weaker than production's
  `IsPlausibleName` (length >= 3 vs >= 4, no blocklist) — false
  positives possible on UI labels like "Class"/"Race"/"Bard".
- Iter-3-deferred items from v3.22.0 still open: magic timeouts,
  `WaitForEnterWorldTransition` ct overload, Stopwatch reuse, legacy
  `goto` cleanup, `charCount==2` fallback policy, PII redaction.

### Bonus diagnostics

- New `Native/probe_charselect_offset.ps1` — empirical offset probe.
  Opens a live `eqgame.exe`, walks `dinput8.dll` PE exports for
  `ppEverQuest`, dereferences, then scans `0..0x20000` for plausible
  `ArrayClass<CharSelectInfo>` patterns. Future offset shifts (Dalaya
  client update, eqgame.exe patch) can be verified without rebuilding.

### Wrong-comment cleanup (included)

The pre-fix comment at `mq2_bridge.cpp:596-598` claimed
"OFFSET_CHARSELECT_ARRAY = 0x18EC0 is the MQ2 x64-RoF2 value; on
Dalaya x86 every pointer-sized member halves so the array sits at
a different (currently unknown) offset." This is wrong — MQ2 RoF2-emu
IS x86 (the emu branch targets emu servers like Dalaya), and `0x18EC0`
IS the x86 offset per `EverQuest.h:963`. The diff replaces the trust
path's comment block with the MQ2-canonical rationale; the
`ValidateCharArrayOffset` function and the misleading line 596-598
block are LEFT IN PLACE as dead code (no production caller after this
fix) so a future refactor that re-introduces a wrapper won't silently
inherit a still-wrong rationale comment. Cleanup-to-zero is filed for
v3.22.2 (see "Known follow-ups" below).

### Verification

- Native build clean: `Native/build-di8-inject.sh` exit 0,
  `eqswitch-di8.dll` 242,688 → 241,152 bytes (slightly smaller from
  removed validation paths).
- C# Debug + Release builds clean: 0 warnings, 0 errors (version
  bump only on C# side).
- Empirical smoke: 2026-05-17 00:30 — both PIDs in-world, real names
  extracted (10 + 1 via single-char fallback). See trace above.

## v3.22.0 — state-driven auto-login dispatch (2026-05-16)

First end-to-end ship of the C# state machine that drives the Dalaya auto-login
pipeline against Native's SHM observability layer (v7+ widget probes + v8
`charSelectAvailable`). **Opt-in via `LaunchConfig.useStateMachine`** —
**defaults to `false` in code** (`Config/AppConfig.cs`) so a fresh install
preserves v3.21.x behavior. Set `"useStateMachine": true` in
`eqswitch-config.json` to enable the SM path; the legacy `RunLoginSequence` is
retained as the default until the SM path has accumulated more
multi-environment smoke. Iter-1 through Iter-4 all consolidate here — branch
`feat/v3.22.0-state-machine` → `main`.

### Highlights

- **`RunLoginStateMachine` drives the full pipeline to in-world on Dalaya**
  (`Core/AutoLoginManager.cs`) — `StepWaitLoginScreen` → `StepTypingCredentials`
  → `StepClickingConnect` → `StepWaitConnectResponse` → `StepServerSelect` →
  `StepWaitServerLoad` → `StepCharSelect` → `StepEnteringWorld` → `Complete`.
  Each Step is a pure function of `(WidgetState, gameState, nativePhase, …)`
  on the lightweight phases; the two long-running blocking Steps
  (`StepCharSelect` and `StepEnteringWorld`) are INSTANCE methods that own
  the per-PID context (CharSelectReader / KeyInputWriter / hwnd) and accept a
  `CancellationToken` so the outer `OverallTimeoutMs` cuts in at ~500ms
  granularity. Dispatcher tick is 250ms; overall budget is 180s.
- **Issues `SendLoginCommand` ONCE on dispatch entry** and lets Native drive
  credential commit / Connect click / server-select progression autonomously.
  C# observes the SHM-published `nativePhase` / `gameState` / widget visibility
  + `widgetTickSeq` and only intervenes at the structural decision points
  (char-select character resolution + Enter World retry/fallback).
- **`LoginCredentialsSent` event fires on the SM path** at SHM
  `SendLoginCommand` success — `TrayManager.ApplyDeferredCosmetics(pid)`
  (slim-titlebar + hook-config refresh) now runs at T+~0s on the SM path
  instead of waiting on the legacy post-BURST-1 fire site (~T+7s) that
  never executes when `useStateMachine=true`. `LoginComplete` remains the
  idempotent end-of-sequence and re-invokes `ApplyDeferredCosmetics` so
  any EQ-side drift during the charselect-load transition is corrected.
  (Window-title is NOT applied here — that's wired on `ClientDiscovered`
  and lives outside the auto-login event chain.)
- **Cross-PID `_focusFakeMutex` (static `SemaphoreSlim`) serializes
  PulseKey3D Enter World** across dual-box. The native focus-fake path
  spoofs `GetForegroundWindow` and per-PID handles are correct in theory,
  but the underlying IAT-hook substrate shares process-wide state — without
  the mutex, the second PID could observe the first PID's Activate state.
  Cost: ~1.5–3s latency on the second PID. Benefit: deterministic dual-box
  completion. Acquire-once pattern (no `gotMutex` flag, separate try/finally
  for the held-block, `SemaphoreFullException` guard on Release).
- **Single-character structural fallback ported from legacy
  `RunLoginSequence:2192-2203`** — when the MQ2 bridge publishes exactly 1
  character AND the name slot is a `"Slot N"` placeholder (heap-read couldn't
  pull the real name string), slot 1 is the target by elimination. Bypasses
  `CharacterSelector.Decide` which would otherwise refuse to match the
  placeholder against `character.Name`. Load-bearing for single-character
  accounts where the bridge runs in slot-mode.
- **Iter-4 polish:** `LoginCredentialsSent` SM-path fire (see above); dead
  `WidgetState widgets` + `int gameState` + `KeyInputWriter writer` params
  removed from `StepCharSelect` signature (Iter-3 left them after the Step's
  logic shifted to native-phase / charSelect-reader probes during fix-rounds;
  Round-1 verifiers caught `writer` as a third dead param that the initial
  Iter-4 trim missed). Event XML doc on `LoginCredentialsSent` reworked to
  document both legacy (~T+7s post-BURST-1) and SM (~T+0s post-`SendLoginCommand`)
  fire-timing semantics, with explicit "cosmetic only / no-op-safe against
  failed-login" caveat.

### Native side substrate (composed across v3.21.0 → v3.21.1 → v3.22.0)

The state machine consumes a Native substrate built across the prior two
releases plus one v3.22.0-branch bump. Recapped here so the v3.22.0
contract is reviewable end-to-end:

- `Native/login_state_machine.cpp` — `PollWidgetVisibilityToShm`
  (**introduced in v3.21.0**) publishes 5 widget visibilities
  (`ConnectVisible`, `OkDialogVisible`, `YesNoDialogVisible`,
  `ServerSelectVisible`, plus `CharSelectAvailable` added in this branch)
  + `widgetTickSeq` heartbeat + `nativePhase` + `gameState` snapshot.
- **v3.21.1 hardening:** gate reordered so `shm->gameState >= GAMESTATE_CHARSELECT`
  early-exit fires BEFORE the `eqmainBase`-snapshot check (cheaper
  short-circuit through the entire char-select-and-beyond window); plus
  `LOGIN_CMD_LOGIN` now resets `g_lastEqmainBase` and invalidates the
  widget cache on every send so eqmain reloads at the SAME ASLR base
  don't slip past the layer-2 base-snapshot defense.
- **v3.22.0 SHM bump v7 → v8 (Iter-2A on this branch):** new
  `charSelectAvailable` bool published when `pinstCCharacterSelect != NULL`.
  This is the load-bearing signal that lets the C# state machine route
  around Dalaya's broken `gGameState` (stuck at 0 through login → char-select,
  per Iter-1.5 empirical investigation).

No Native rebuild is required for the v3.22.0 ship-gate commit itself —
the v8 SHM was shipped in Iter-2A (`c29cbec` on this branch), and the
v3.21.1 hardening already in `main`. The v3.22.0 tag aggregates them.

### Architectural caveats (documented, not blockers)

- **Blocking Step methods + 180s `OverallTimeoutMs`** is the v3.22.0 contract.
  `StepCharSelect` blocks 5–30s waiting for the MQ2 heap-scan to populate the
  char list; `StepEnteringWorld` blocks up to 60–90s waiting for zone-load.
  Refactor to non-blocking sub-states is Iter-5 cleanup if needed.
- **`gGameState` on Dalaya never advances past 0** through login → char-select.
  The state machine routes around this via `widgets.CharSelectAvailable` (the
  v8 SHM signal published when `pinstCCharacterSelect != NULL`). MQ2's
  `GAMESTATE_CHARSELECT` gate in `login_state_machine.cpp:1153` is silently
  dead on Dalaya — confirmed via live external probe (Iter-1.5).
- **Hardcoded timeouts** (`kMaxCharListWaitMs=30_000`, ack-wait
  `200 × 50ms = 10s`, settle `100ms`, EW attempts `4/3`, EW caps `90/60s`) —
  legacy snapshots tunables at method head; SM path inherits the legacy
  values inline. Promotion to `LaunchConfig` is an Iter-5 quality-of-life
  item.
- **PII redaction stance is unchanged.** v3.15.5 + v3.15.6 + v3.18.0 R3 set
  the posture: credential halves (`account.Username`, password, `okText`)
  are redacted; `account.Name`, `account.Server`, `character.Name`, and
  `charNames[]` log unredacted as diagnostic aids. Iter-3 verifier round-3
  flagged the SM path for verbatim character-name logging (T3-Sonnet MED-4,
  T3-Opus LOW); tightening that stance would be a posture-wide change
  affecting both legacy and SM paths plus the v3.18.0 verbatim okText log
  decision — out of scope for this ship gate. Filed for v3.22.1 if the
  conflict gets re-raised.

### Deferred to Iter-5 / v3.22.x (not blockers)

- `WaitForEnterWorldTransition` `ct` overload — the 60–90s blocking call
  currently ignores `ct`; worst-case cancellation latency is bounded by
  the helper's internal `Sleep(1000)` (~1s past cancel).
- Per-tick `Stopwatch` allocation reuse in `StepCharSelect` (2 allocs/entry).
- `goto` cleanup in legacy `RunLoginSequence` (cosmetic — legacy path only).
- `charCount==2` multi-slot-mode fallback policy (currently aborts safely;
  extending elimination logic to 2 slots has higher risk than reward).

### Verification

- Build: Debug + Release both clean (0 warnings, 0 errors). Single-file
  publish artifact unchanged in shape.
- Native: unchanged from v3.21.1 (no rebuild required for v3.22.0 ship).
- Smoke: dual-box Dalaya end-to-end to in-world validated 2026-05-16 22:05
  — backup account `gotquiz` (slot 2 via name match) Complete at t=60.0s;
  Natedogg account `gotquiz1` (slot 1 via single-char structural fallback)
  Complete at t=69.2s. The ~9s offset is the cross-PID
  `_focusFakeMutex` serialization — correct latency-over-corruption
  tradeoff for the IAT-hook substrate.
- Verifier discipline: Iter-3 dispatched 3 rounds × 12 agents (high stakes
  for the new SM path). Iter-4 ship-gate is normal stakes (C# only, no
  Native edits) → 2 rounds × 6 agents per `completion-checkpoint.sh` v4.1.

### Reference

- v3.21.1 (predecessor) — v7 widget-probe substrate + cache defense.
- v3.21.0 (substrate ship) — original SHM v7 widget probes + observability
  ground truth that the SM path consumes.
- v3.16.0 — `ScreenMode=3` swap in Native (the credential-write substrate
  the SM path depends on for `SendLoginCommand` to land on Dalaya).

## v3.21.1 — verifier-flagged cleanup (2026-05-16)

Closes two of the three v3.21.0 known-follow-ups plus one verifier finding
that didn't make the CHANGELOG known-follow-ups list. Pure micro-cleanup —
no behavior change, no SHM bump, no version bump beyond the patch
component. Ship-and-forget; foundation-protecting before v3.22.0 starts
building on top of the v7 probes.

### Changes

- **`PollWidgetVisibilityToShm` reorder** (`Native/login_state_machine.cpp`)
  — moves the `shm->gameState >= GAMESTATE_CHARSELECT` early-exit BEFORE
  the `eqmainBase`-snapshot check, so the cheap `uint32` field read
  short-circuits a `GetModuleHandleA("eqmain.dll")` syscall during the
  entire char-select-and-beyond window (not just the brief transition
  tick). Correctness preserved: cache invalidation only matters when
  the function is about to probe, and the first tick `gameState <
  CHARSELECT` re-runs the base check and invalidates if eqmain reloaded
  at a different ASLR base. Comment block reworked to reflect the new
  ordering's rationale (the prior comment still correctly described
  fix-2 but no longer matched the code position).

- **`ShmLayoutTests` covers `LoginShm` v7** (`Core/ShmLayoutTests.cs`) —
  adds 37 new layout assertions (struct size 1912, plus offset-per-field
  for the entire v7 struct). Closes the test-coverage gap that compounded
  silently across the v3 → v4 → v5 → v6 → v7 SHM bump cascade. Same
  `Marshal.SizeOf` + `Marshal.OffsetOf` pattern as the existing
  `CharSelectShm` test, MINUS the tautological magic-value / version-value
  assertions that the verifier sweep flagged in the pre-existing tests
  (comparing a literal to a same-literal const proves nothing). The
  pre-existing tautologies in `SharedKeyState`/`CharSelectShm` stay for
  Chesterton-fence reasons — fixing them needs an MMF round-trip or
  exposing `LoginShmWriter.Magic/Version` as `internal const`, both of
  which are scope creep for this ship. `--test-shm-layout` now runs 62
  assertions, all passing.

- **`ResetDllToCsharpFields` zero-buffer consolidation**
  (`Core/LoginShmWriter.cs`) — replaces six per-call `new byte[N]`
  allocations (`ErrorLen` × 2, `MaxChars*NameLen`, `MaxChars*4` × 2,
  `ConfirmTextLen`) with a single private static readonly `s_zeroBuffer`
  sized for the largest single write (`MaxChars*NameLen` = 640 bytes).
  Shorter writes pass a smaller count to `WriteArray`. Eliminates
  ~1.2KB of GC pressure per `Open()`-on-existing-mapping re-call (the
  v3.18.0 R2 path that fires on auto-login re-fire against the same
  PID). Out of scope: `Open()` fresh-mapping path's credential-zero
  allocs and `Close()`/`Dispose()` password-zero allocs (separate
  patterns; safe to leave for a future micro-opt).

- **`LOGIN_CMD_LOGIN` resets the v7 widget-visibility cache**
  (`Native/login_state_machine.cpp`) — verifier-flagged convergent
  CRITICAL (3 of 8 agents). The v3.21.0 base-snapshot defense only
  catches eqmain reloads at a DIFFERENT ASLR base; same-base reloads
  (common when a single `eqmain.dll` is unloaded and re-loaded into
  the same preferred base in-process) would slip past layer 2 and
  rely entirely on layer 1 (the `IsEQMainWidget` vtable-range check),
  which is known-imperfect on recycled addresses. Fix: every
  `LOGIN_CMD_LOGIN` now calls `InvalidateWidgetVisibilityCache()` and
  resets `g_lastEqmainBase = 0`, guaranteeing the next probe re-resolves
  via `FindLiveScreenByName` regardless of eqmain's load history.
  Symmetric to the existing `g_lastJoinServerReqSeq` reset in the
  same block.

### Verification

- `--test-shm-layout` — 62/62 assertions PASS (was 25/25 in v3.21.0).
- Native build clean: `Native/build-di8-inject.sh` exit 0, eqswitch-di8.dll
  242,176 → 242,688 bytes (instruction reorder + comment-block changes).
- C# Debug + Release builds clean: 0 warnings, 0 errors.
- Smoke verification: see commit message + release notes.

### Reference

- v3.21.0 known-follow-ups list (CHANGELOG entry below) — this release
  resolves the two v3.21.1-prefixed items (gameState gate ordering +
  ShmLayoutTests LoginShm coverage). The two v3.22.0-prefixed items
  (`InvalidateWidgets` unification, `TryReadWidgetState` torn-read
  guard) remain explicitly deferred — they're load-bearing for the
  v3.22.0 state-driven dispatch but not for v3.21.0 observability.

## v3.21.0 — SHM v7 + Widget Probes (2026-05-16)

Foundation for the v3.22.0 state-driven dispatch rewrite. Adds a native
probe that snapshots 5 SIDL-screen widget visibilities into SHM every
Tick (during PRECHARSELECT only, see fix-2 below), mirroring MQ2's
OnPulse dispatcher pattern across `MQ2AutoLogin.cpp`'s `GAMESTATE_PRECHARSELECT`
block (1195-1240) and `GAMESTATE_CHARSELECT` block (1156-1191). v3.21.0
ships observability only — `RunLoginSequence` control flow does NOT
branch on the new bools yet (deferred to v3.22.0).

### Shipping iterations (smoke-driven)

- **initial** — 5× `FindLiveScreenByName` per Tick (~846 widget-node scans).
  Smoke 12:00 FAILED: both clients hit auth timeout. Root cause: probe
  cost starved EQ's game thread during the login-server response window,
  matching the iter-12 dormancy comment in `eqmain_widgets_mq2style.cpp:102`
  ("game-thread structural walks starved IDirectInputDevice8::GetDeviceState
  polling, dropping BURST keystrokes").
- **fix-1 — widget-ptr cache.** Static cache slots + `ResolveCachedScreen`
  helper validates cached ptrs via `EQMainOffsets::IsEQMainWidget` per
  call. Per-Tick cost drops from 846 node scans → 5 byte reads (cache
  hit). Smoke 12:36 FAILED differently: Natedogg (1-char account) reached
  in-world; Backup (10-char account) crashed during Enter World →
  Loading-Zone, 9s after EW keypress. Residual probe cost during char-select
  (cache invalidates when eqmain unloads at char-select transition →
  ~115 widget scans / probe re-resolve cycle) was contending with the
  zone-load handshake on the heavier 10-char account.
- **fix-2 — gameState gate.** `PollWidgetVisibilityToShm` early-exits
  when `shm->gameState >= GAMESTATE_CHARSELECT` (=1, empirically verified
  by 12:51 smoke). Cleared visibility bools so C# observes consistent
  "no login widgets" state, still bumps `widgetTickSeq` so C# can
  distinguish "probe alive but gated" from "probe dead". Smoke 12:51
  PASS: both clients in-world with correct chars.
- **fix-3 — verifier-driven hardening (8-agent Sonnet+Opus sweep).**
  Convergent CRITICAL findings addressed: (a) `ResetDllToCsharpFields`
  now zeros v7 fields on `Open()` re-call (prevents stale `widgetTickSeq`
  from misleading the first `LogWidgetSnapshot` read); (b) per-tick
  `eqmainBase` snapshot detects eqmain reload — invalidates cache when
  DLL reloads at a different ASLR address (defends against false-positive
  vtable-range validation on stale ptrs).

### Native (eqswitch-di8.dll, 241,152 → 242,176 bytes)
- `login_shm.h`: SHM v6 → v7. Appends `widgetConnectVisible`,
  `widgetServerSelectVisible`, `widgetOkDialogVisible`,
  `widgetYesNoDialogVisible`, `widgetConfirmDialogVisible`,
  `widgetConfirmDialogText[256]`, `widgetTickSeq`. Backward-compatible
  append — v6 native readers see the first 1632 bytes unchanged.
  Total struct size 1632 → 1912 bytes.
- `login_state_machine.cpp`: new `PollWidgetVisibilityToShm` called from
  `Tick()` immediately after `PollOkDisplayToShm`. Uses existing
  `EQMainWidgetsMQ2::FindLiveScreenByName` + `IsCXWndVisible` helpers.
  Confirm-dialog text mirror via `FindChildByName("ConfirmationDialogBox",
  "CD_TextOutput")` + `MQ2Bridge::ReadWindowText`. Manual byte-copy loop
  on the volatile char array (memcpy on volatile is UB). `widgetTickSeq`
  is the LAST write per consistency-ordering — when C# observes seq
  advance, all visibility writes are visible.

### C# (.NET 8)
- `Core/WidgetState.cs`: new read-only record struct (5 bools + string +
  uint) with `Empty` static, `AnyDialogVisible` computed property, and
  `DiagSummary()` log formatter.
- `Core/LoginShmWriter.cs`: 7 new `OFF_WIDGET_*` constants (1632-1908);
  `ShmSize` 1632 → 1912; `Version` 6 → 7; new
  `TryReadWidgetState(int pid, out WidgetState)` following the existing
  `_mappings[pid].Accessor` pattern of `ReadPhase` / `ReadError`. Uses
  named `ConfirmTextLen` const (256) and existing `ReadString` helper.
- `Core/AutoLoginManager.cs`: 6 `LogWidgetSnapshot(loginShm, pid, label)`
  calls at `RunLoginSequence` checkpoints (`login-screen-ready`,
  `pre-burst1`, `post-burst1`, `post-wst-primary`,
  `retry{N}-pre-wst`, `charselect-reached`) — proof-of-life for the
  probe during dual-box smoke. RunLoginSequence control flow is unchanged.

### Out-of-scope (deferred)
- v3.22.0: state-driven dispatch loop replacing linear `RunLoginSequence`
  (mq2-vs-eqswitch-fragility-audit gaps #1, #5, #10).
- v3.23.0: OK_Display action codes (`Action_ClickYes`) +
  `ConfirmationDialogBox` "Loading Characters" stuck-state recovery
  (`pCharacterListWnd->Quit()`) + `ServerList.StatusFlags` server-down
  detection (audit gaps #3, #4, #5, #6, #7, #8).

### Cost-analysis lesson

The original v3.21.0 plan claimed `PollWidgetVisibilityToShm`'s cost was
"comparable to the existing `PollOkDisplayToShm` probe". This was wrong
by 5×: the existing probe does ONE `FindWindowByName` walk; the new one
did FIVE `FindLiveScreenByName` walks (~169 widget-node scans each =
~846 scans per probe). 5 rounds of plan-doc verification did not catch
this (the cost claim was internally self-consistent); the smoke at 12:00
falsified it empirically by reproducing the iter-12 game-thread
starvation pattern. Future plans introducing probes should count actual
operations, not trust prose comparisons. Full lesson recorded in
`X:/_Projects/_.claude/_comms/plan-eqswitch-v3.21.0.md` post-execution
correction section.

### Known follow-ups (verifier-flagged, deferred from v3.21.0)
- **v3.21.1**: micro-optimize `PollWidgetVisibilityToShm` ordering — move
  the `gameState >= CHARSELECT` early-exit BEFORE the `eqmainBase`
  snapshot so the cheap `uint32` field read short-circuits a `GetModuleHandleA`
  call during the brief char-select-transition window. Correctness is
  identical either ordering; this is purely a per-tick CPU win.
- **v3.22.0**: `InvalidateWidgets()` does not currently call
  `InvalidateWidgetVisibilityCache()`. The two caches diverge on gameState
  transitions within the same eqmain instance (e.g., 0→1→0 retry loops).
  Empirically benign for v3.21.0 because the v7 cache holds SCREEN-level
  ptrs (stable across gameState transitions on Dalaya — they're hidden
  via dShow, not destroyed) while `InvalidateWidgets` clears CHILD ptrs
  (which DO get reallocated). When v3.22.0 dispatch reads the v7 cache
  as a decision input, consider unifying invalidation for defense-in-depth.
- **v3.21.1**: `ShmLayoutTests.cs` lacks `LoginShm` layout assertions —
  fifth SHM bump (v3→v7) without test coverage. Pre-existing gap, not
  a regression, but each bump compounds silent-corruption risk on the
  next change. Add assertions covering v7 offsets (1632-1908) and the
  total 1912-byte size invariant.
- **v3.22.0**: `TryReadWidgetState` torn-read guard. Read pattern needs
  acquire-fence + seq-pair verification (read tickSeq, read fields,
  re-read tickSeq, retry on change) — canonical lock-free seq-counter
  pattern. Mirrors the `ReadOkDisplaySnapshot` discipline. Not load-bearing
  for v3.21.0 observability-only scope; load-bearing once v3.22.0 dispatch
  reads these bools as decision inputs.
- **v3.22.0**: `WidgetState.AnyDialogVisible` excludes `Connect`/`ServerSelect`
  by name — intentional, but the silent exclusion is a footgun for the
  v3.22.0 dispatch loop ("if (!state.AnyDialogVisible) proceed" would
  incorrectly proceed when server-select is up). Add explicit
  `IsAtLoginScreen` / `IsAtServerSelect` properties OR rename for clarity
  when v3.22.0 takes a dependency.
- **v3.22.0**: Heap-recycle false-positive in `ResolveCachedScreen` —
  if heap recycles a cached widget's address for a DIFFERENT eqmain
  widget, `IsEQMainWidget` passes but visibility report is for the
  wrong screen. Mitigation: per-call `GetCXWndXMLName()` match. Acknowledged
  in code comments at `login_state_machine.cpp:325-330`. Benign in v3.21.0
  (observability only).

## v3.20.11 — Keystroke-leak hardening: skip retry retype + per-keystroke in-game gate (2026-05-15)

Closes the keystroke-leak vector Nate observed in the post-v3.20.10 smoke: passwords containing 'd' fired DUCK and 'x' fired bound EQ actions in-game when chars entered world mid-retry. Both fixes are direct ports from MQ2's authoritative pattern (see `_.claude/_comms/mq2-vs-eqswitch-fragility-audit-2026-05-15.md`).

### Background — the leak window

The v3.20.9 IsInGame gate at `AutoLoginManager.cs:1275` checks `IsInGame(charSelect, pid, hwnd)` ONCE after the recovery sleep. If the gate passes (chars not in-world yet), the retry proceeds to 16x Backspace field-clear + `RunCredentialEntry` + BURST 2. If chars enter world DURING those steps, password chars become in-game keybinds. The v3.20.10 mid-sleep `gameState==5` exit closed half the window; v3.20.11 closes the rest.

### Fix 1: Skip retry credential retype unless server-classified truncated (audit gap #3)

MQ2 (`StateMachine.cpp:344-369`) never retypes credentials on retry — it only clicks `OK_OKButton`. We previously always retyped. v3.20.11 wraps the entire retype block (16x Backspace + `RunCredentialEntry`) in `if (credsTruncated)`. The flag is set true only when `okClass == OkDisplayClass.Recoverable` AND the dialog text contains "truncated" or "incomplete" — i.e., the server explicitly told us creds were mangled. For every other retry case (stale session, unknown recoverable, no dialog), the retype is skipped; only the modal-dismiss Enter (step 1) + BURST 2 Enter (step 4) carry the retry work.

Rationale: re-typing the same correct password at the same login screen produces the same outcome from the server. The only thing it changes is opening a keystroke-leak window. MQ2's authoritative pattern proves this is enough — they retry by clicking, not typing.

### Fix 2: Per-keystroke in-game gate in CombinedTypeString (audit gap #4)

MQ2 (`MQ2AutoLogin.cpp:1149-1156`) gates the entire OnPulse dispatcher on `GetGameState() == GAMESTATE_INGAME` BEFORE ever dispatching login-screen keystrokes — making the in-game gate structurally impossible to bypass. EQSwitch's per-burst gates have inherent windows between the check and the actual VK_KEYDOWN posts.

v3.20.11 adds a `CharSelectReader? charSelect = null` parameter to `CombinedTypeString` and `RunCredentialEntry`. When provided, `CombinedTypeString` polls `charSelect.ReadGameState(pid) == 5` at the top of each per-char iteration. If true, it logs a Warn (with chars-sent + chars-remaining) and breaks out of the typing loop — the TypingResult's `IsComplete=false` will then surface via the existing `LogTypingValidation` Error log. Cost: ~microsecond per iteration; vs the cost of a leaked keystroke firing as a bound EQ action, infinite payoff.

Both `RunCredentialEntry` call sites (primary path line ~911, retry path line ~1342) now pass `charSelect`. Pre-v3.20.11 callers passing `null` preserve the old behavior (no gate). Backward-compatible.

### Fix 2b: Per-iteration in-game gate in the retry field-clear loop

Same protection extended to the 16x Backspace loops in the retry path (and the second 16x loop when `!UseLoginFlag`). Check every 4 iterations (cheap — 4 SHM reads vs 16 keystrokes). On in-game detection: `writer.Deactivate(pid)` to release input lock, set `hitTimeout = false`, jump via labeled goto to the retry-aborted-in-game exit path.

### Belt-and-braces: pre-RunCredentialEntry check

Even with the per-keystroke gate inside CombinedTypeString, an explicit `IsInGame(charSelect, pid, hwnd)` check fires right before `RunCredentialEntry`. Lets us short-circuit the entire BURST 1 / RunCredentialEntry call without entering it.

### Effect on the failure-mode chain

| Step | Pre-v3.20.11 | v3.20.11 |
|------|--------------|----------|
| Retry modal-dismiss Enter → world (engine-side, can't gate) | possible | possible (unchanged) |
| 30s recovery sleep | exits early on gameState==5 (v3.20.10) | same |
| v3.20.9 IsInGame check after sleep | exits if in-world | same |
| **16x Backspace loop** | unguarded — could fire mid-loop | **gated per-4-iterations (v3.20.11)** |
| **RunCredentialEntry (typing password)** | unguarded mid-typing | **gated per-keystroke (v3.20.11)** |
| **Retype gate** | always retyped | **skipped unless truncated (v3.20.11)** |
| BURST 2 server-confirm Enter | unguarded | unchanged (Enter at server-select is benign) |

The retry path remains useful for the canonical case (server held session, modal-dismiss Enter + BURST 2 advances) — it just no longer re-types the password.

### Audit document

Full MQ2 vs EQSwitch comparison: `_.claude/_comms/mq2-vs-eqswitch-fragility-audit-2026-05-15.md`. 10 ranked structural gaps, of which v3.20.11 addresses #3 and #4 (small fixes, high ROI). Remaining gaps (#1 widget probes, #5 state-driven dispatch, etc.) sized for v3.21+.

**Files changed:** `Core/AutoLoginManager.cs` (CombinedTypeString signature + per-char gate, RunCredentialEntry signature, two call-site updates, retry-loop retype wrap + field-clear gating + label), `EQSwitch.csproj` (v3.20.11), `CHANGELOG.md`.

### Verifier-driven hardenings (post-8-agent audit 2026-05-15)

Eight verifiers (4 topic pairs × Sonnet+Opus) ran on v3.20.11. T1 both APPROVE. T2/T3/T4 flagged CONCERNS converging on 5 real findings, all addressed in the same commit:

- **Tightened `credsTruncated` substring match** (T2 Sonnet+Opus conf 85 convergent). Was `lower.Contains("truncated") || lower.Contains("incomplete")` — too broad; would false-positive on unrelated server messages like "Your connection was truncated" or "character file appears incomplete". Now requires co-occurrence: `(lower.Contains("password") || lower.Contains("credential")) && (lower.Contains("truncated") || lower.Contains("incomplete"))`. Trade-off: false-negative on real truncated-creds dialogs that use different wording is safer than false-positive that triggers keystroke leak.
- **Snapshotted `retryJoinServerId`** (T2 Sonnet conf 90 + T3 Sonnet I3 conf 80 convergent). Was reading live `_config.Launch.JoinServerId` inside the retry loop while every other tunable used the R3 snapshot from v3.17.0. Pre-existing race bug; fixed to `int retryJoinServerId = joinServerId;` (using the line-965 snapshot).
- **`!credsTruncated` skip-path post-sleep IsInGame check** (T3 Opus important conf 85). The skip branch sleeps `postBurst1WaitMs` (default 3s) before BURST 2 fires. If chars enter world during that sleep, BURST 2's Enter fires in-world. Added `IsInGame(charSelect, pid, hwnd)` check after the sleep; on detection, `hitTimeout=false` + goto-exit before BURST 2.
- **Renamed `retryAbortedInGame:` → `postRetypeBlock:`** (T2 Sonnet conf 80 + T3 Sonnet I2 conf 81 convergent). The label is reachable from both abort gotos AND fall-through from the `!credsTruncated` skip path. Old name implied "abort" semantics for what's actually a common post-retype exit point.
- **`CombinedTypeString` per-char gate uses `IsInGame()` instead of raw `gameState == 5`** (T2 Opus Rule 7 finding). Adds the post-Enter-World " - " title-check fallback alongside the gameState check. Defense in depth against the Dalaya partially-unknown gameState semantics flagged in `login_state_machine.cpp:30-36`.

Verifier false positives (rationale documented, not addressed): T3 Sonnet I1 `charsRemaining` off-by-one — math is actually correct (`text.Length - typedCount - skippedCount` correctly equals "remaining iterations INCLUDING the current char" at gate-fire time); current log message reads correctly. T3 Sonnet C1 `hitTimeout` edge case for `WaitTransitionSettleMs >= initialTimeoutMs - 500` — only triggers on user-misconfigured tunables; pre-existing pattern symmetric across primary + retry paths.

Doc-level follow-ups (post-deploy housekeeping, not blocking):
- `_.releases/eqswitch/{VERSION, CHANGELOG.md, SHA256SUMS}` need sync to v3.20.11 (T4 Sonnet+Opus convergent conf 90+).
- `_.claude/_comms/next-session-eqswitch-v3.20.7-char-name-fix.md` handoff doc is 4 versions superseded — archive or annotate (T4 Opus).
- `_comms/mq2-vs-eqswitch-fragility-audit-2026-05-15.md` line refs shifted by ~30 lines after v3.20.11 inserts — minor doc drift (T4 Sonnet conf 85).
- `memory/MEMORY.md` index pins v3.20.7 IN-WORLD as ACTIVE — auto-updated on next `/save`.

## v3.20.10 — Retry path advance-detection closures (2026-05-15)

Closes two advance-detection gaps in the retry path that v3.20.9's primary-path fix exposed by symmetry. Both C# only — no native rebuild.

### Background

v3.20.9 added the canonical `mq2Available + ReadCharCount > 0` SHM short-circuit to the **primary** `WaitForScreenTransition` call at `AutoLoginManager.cs:1015`. That fix worked end-to-end in the v3.20.9 smoke (gotquiz1 → Natedogg, gotquiz → Backup; char-select 48s → 6s). But it left the **retry** path unprotected: if for any reason the retry kicks in, the retry's own WaitForScreenTransition call (line ~1372) still polls the Dalaya-unreliable `gameState` / rect signals and could time out at 60s. The 30s recovery sleep inside the retry path was also opaque to in-world advance — it would burn the full duration even when chars had already entered the world during step 1's modal-dismiss Enter.

Nate flagged this 2026-05-15: "does the retry gate stop trying if successful advance is verified?" — the answer was "yes, but only at one specific check; the other two paths still waste time."

### Fix 1: Retry's WaitForScreenTransition SHM short-circuit

Mirror of v3.20.9's primary-path check, applied at the retry path's WST call (line ~1372). Before calling WaitForScreenTransition, check `IsMQ2Available + ReadCharCount > 0`. If active, skip the rect-based wait, settle, refresh handle, and continue. Symmetric with the primary path — both call sites of `WaitForScreenTransition` now honor the canonical char-select signal.

### Fix 2: Adaptive recovery sleep — exit early on in-world

`CancellableSleepUntilProcessDies` previously returned `bool` (`true` = full duration elapsed, `false` = process died). v3.20.10 expands it to a tri-state enum `RecoveryWaitOutcome`:

- `Completed` — full duration elapsed without process death or in-game detection
- `ProcessDied` — EQ process exited mid-wait (preserves the pre-v3.20.10 abort path)
- `InGame` — chars reached in-world mid-wait (new exit signal — `gameState == 5` observed)

The helper now accepts an optional `CharSelectReader? charSelect` parameter. When provided, it polls `charSelect.ReadGameState(pid) == 5` each iteration alongside the process-death check. When `null`, the helper preserves the pre-v3.20.10 semantics (only `Completed` and `ProcessDied` can fire). The retry path's caller passes `charSelect` to opt into the in-game branch.

The caller branches on the enum:
- `ProcessDied` → existing abort+return path
- `InGame` OR (post-sleep `IsInGame(charSelect, pid, hwnd)`) → break with `hitTimeout = false` (retry treated as success, no credential re-typing)
- `Completed` AND `!IsInGame` → continue with credential re-type as before

The post-sleep `IsInGame` check is kept as a belt-and-braces fallback because it adds the title-flip " - " signal (`"EverQuest - CharName"` pattern) that fires on non-Dalaya servers where `gameState` may lag the title.

### Why this matters

The retry path's role in the keystroke-leak bug Nate observed 2026-05-15 was a chain:

1. WaitForScreenTransition (primary) times out at 90s on Dalaya — **fixed in v3.20.9**
2. Retry path's modal-dismiss Enter fires → lands Enter World on default char → chars enter world
3. 30s recovery sleep burns the full duration — **fixed by v3.20.10 Fix 2** (gameState==5 mid-sleep detection)
4. Step 3/4/5 keystrokes (16x Backspace + password retype + BURST 2 Enter) fire on in-game chars — **already prevented by v3.20.9 IsInGame gate**
5. Retry path's own WaitForScreenTransition times out at 60s — **fixed by v3.20.10 Fix 1**

v3.20.9's IsInGame gate at step 4 stopped the actual keystroke leak. v3.20.10 closes the remaining wasted-time gaps (steps 3, 5) so the retry exits cleanly and quickly even if it kicks in. Combined with v3.20.9's primary fix preventing retry from running at all on Dalaya, the retry path is now both rare AND fast-exit when it does fire.

**Files changed:** `Core/AutoLoginManager.cs` (RecoveryWaitOutcome enum, CancellableSleepUntilProcessDies signature, retry-loop caller switch, retry WST SHM short-circuit), `EQSwitch.csproj` (v3.20.10), `CHANGELOG.md`.

**Verification gate:** code review before deploy (Nate's standing request 2026-05-15 "verify the code is correct"). Smoke gate inherited from v3.20.9 — already passed dual-box-to-in-world with correct chars, so v3.20.10 ships on top of a green baseline.

## v3.20.9 — WaitForScreenTransition SHM short-circuit + in-game retry abort (2026-05-15, post-v3.20.8 smoke)

The v3.20.8 row-anchor fix in `HandleSelectionRequest` was correct but never got to run during the post-ship smoke. Smoke surfaced two separate, severe bugs that bypassed it entirely. Both fixed in v3.20.9, both C# only (no native rebuild).

### The smoke evidence

```
20:32:37  PollForLoginAdvance PID 19348 — char-select SHM advance detected at 17171ms
20:32:37  Loading character select...
                                          ← 90 seconds of WaitForScreenTransition timeout
20:34:08  screen transition timeout after 90000ms — char select may not have loaded
20:34:08  retry 1/1 starting
20:34:08  retry 1 modal-dismiss Enter sent (PID 19348, gameState=0)
                                          ← Enter at char-select = Enter World on slot 0
                                          ← Both clients in-game on default chars
20:34:42  retry 1 pre-typing field-clear (16x Backspace)  ← keystrokes hit in-game chars
20:34:42  Typing credentials...                            ← password chars hit in-game
20:34:43  CombinedTypeString PID=19348 input.Length=7      ← 'd' in pw → DUCK
```

Nate observed: "both are ingame and i see it retrying server still", "it also seemed to DUCK maybe 'd' on both my characters", "buttons are def firing after in world so our inworld flag isnt solid".

### Bug 1: WaitForScreenTransition Dalaya-blindness (root cause)

`PollForLoginAdvance` returned `true` at 17s via the v3.20.7 char-select SHM signal (`mq2Available + ReadCharCount > 0`). Then C# called `WaitForScreenTransition` separately, which polls for a `gameState` transition OR an `IsHungAppWindow` hung→responsive cycle OR a window rect size change. On Dalaya:

- `gameState` stays at 0 across login → server-select → char-select (only flips to 5 in-world).
- The EQ window doesn't hang during the charselect render — it stays responsive.
- The window rect doesn't change size at the login → charselect boundary.

So all three of `WaitForScreenTransition`'s success signals miss the transition, the 90s timeout fires unconditionally, and the retry path kicks in even though we were already at char-select 73 seconds earlier. v3.20.7's fix to `PollForLoginAdvance` closed half the gap; v3.20.9 closes the other half.

**Fix:** before calling `WaitForScreenTransition`, check the same canonical signal `PollForLoginAdvance` trusts (`charSelect.IsMQ2Available(pid) && charSelect.ReadCharCount(pid) > 0`). If it's already active, skip the rect-based wait entirely — just settle and return. Same fix would apply at any other call site of `WaitForScreenTransition` that fires post-PollForLoginAdvance.

### Bug 2: retry path's credential-retype fires keystrokes after in-world (keystroke leak)

When the 90s timeout fires, the retry path:
1. Sends modal-dismiss Enter — gated by `gameState <= 1`. On Dalaya gameState=0 at char-select, so this gate ALLOWS the Enter. But the Enter at char-select fires Enter World on the default-highlighted character. The chars enter the world.
2. Sleeps 30s for "stale-session recovery" — during which EQ finishes zone-load and the chars are fully in-world.
3. Re-types credentials via 16 Backspaces + BURST 1 — **ungated** by any in-world check. The password characters become in-game keystrokes; characters that match EQ keybinds (`d` = DUCK, `\` = SwitchKey, etc.) fire those bindings on the in-game character.

The `gameState <= 1` gate at step 1 doesn't help here because it's evaluated BEFORE the Enter enters world. By step 3, gameState may be 5 (Dalaya in-world) but the existing code path doesn't check it.

**Fix:** after the recovery sleep returns (step 2 → step 3 boundary), check `IsInGame(charSelect, pid, hwnd)`. If true, abort the retry — set `hitTimeout = false`, break out, and report "already in-game during retry — retry skipped". `IsInGame` checks both `gameState == 5` and the post-Enter-World "EverQuest - CharName" window-title pattern, both of which fire reliably post-zone-load even on Dalaya.

### Why these are separate from v3.20.8

v3.20.8's row-anchor re-resolve in `HandleSelectionRequest` is correct and still ships. It's the right fix for the *originally-reported* wrong-character bug — when C# byName-matches against heap-anchored `shm->names[]` and the result's heap index doesn't agree with the CListWnd row index, the DLL now re-resolves via `GetListItemText` (mirror of MQ2 reference). But that fix only runs when C# actually calls `RequestSelectionBySlot` — which it didn't this smoke because `WaitForScreenTransition` ate the 90s window and the retry path's Enter took over the character selection. v3.20.9 closes the path to `HandleSelectionRequest` so the row-anchor fix can do its job on subsequent smokes.

**Files changed:** `Core/AutoLoginManager.cs` (SHM short-circuit + IsInGame retry gate), `EQSwitch.csproj` (v3.20.9), `CHANGELOG.md`.

**Verification gate:** dual-box smoke to in-world per `feedback_dual_box_test_before_autologin_tag.md`. Look for:
- New log line `AutoLogin: gotquiz: char-select SHM signal active — skipping WaitForScreenTransition (PID N, charCount=N)` — confirms Bug 1 fix engaged.
- `mq2_bridge: row re-resolve: heap idx=N ('Backup') -> CListWnd row=M` in DLL log — confirms the row-anchor logic ran (v3.20.8 fix exercised for the first time).
- No `screen transition timeout after 90000ms` line — confirms the 90s wait no longer fires.
- Right characters in-world per config (`gotquiz1`=Natedogg, `gotquiz`=Backup).

## v3.20.8 — Row-anchor char-select via GetListItemText (2026-05-15)

Fixes the v3.20.7-known "wrong character selected" regression: `gotquiz1` configured `characterName: Natedogg` loaded `acpots` instead. Root cause is a heap-vs-CListWnd index-space mismatch in the char-select bridge.

### The bug

The DLL has multiple paths that populate `shm->names[i]` from EQ's character data:

- **Path A** (`charSelectPlayerArray`, line ~3725) — heap order
- **Path B** (`Character_List` CListWnd via `GetListItemText`, line ~3776) — CListWnd row order
- **Path C** (heap scan at stride 0x160, line ~3913) — heap order
- **Standalone heap scan** (Path A+B both failed, line ~4100) — heap order
- **Anchor scan** (single-char fallback, line ~3974) — synthetic slot 0

Path B is the only CListWnd-row-anchored source. The others all use heap order. `HandleSelectionRequest` then calls `g_fnSetCurSel(pCharListWnd, requestedIdx)` — which operates on **CListWnd row index**. When heap order ≠ row order on Dalaya, C#'s byName scan returns the correct slot for the heap-anchored names, but SetCurSel applies that slot to the wrong CListWnd row → wrong character loads.

### The fix

`HandleSelectionRequest` now does a row-anchor re-resolve before SetCurSel. It reads the requested name from `shm->names[requestedIdx]`, scans `Character_List` rows via `GetListItemText` (case-insensitive, alpha-only ASCII compare matching `CharacterSelector.Decide`'s `OrdinalIgnoreCase`), and uses the matched row for SetCurSel. Falls back to `requestedIdx` when GetItemText is unavailable or no row matches — preserves current behavior on servers where heap order happens to agree with row order.

Direct port of MQ2's authoritative pattern (`src/plugins/autologin/StateMachine.cpp:631-642`):

```cpp
for (int i = 0; i < itemsArray->Count; ++i) {
    if (m_record && ci_equals(m_record->characterName,
                              GetListItemText(pCharList, i, 2)))
        return i;
}
```

Includes on-demand `nameCol` probe when `g_cachedNameCol == -1` (Path A success leaves it unprobed). Diagnostic log fires only when `matchedRow != requestedIdx` (real divergence) or when the name isn't in the CListWnd at all.

### Verifier-driven hardenings (post-8-agent audit 2026-05-15)

Eight verifiers (4 topic pairs × Sonnet+Opus) ran on the surgical change. T1 Diff-clean both APPROVE'd; T2/T3/T4 flagged CONCERNS that converged on four issues, all now addressed in the same commit:

- **Defensive null-padded copy of `targetName`.** `shm->names[requestedIdx]` is a volatile char[64] populated by multiple paths; a torn read across the C#/DLL boundary could observe trailing garbage past the logical name end. We now copy into a stack-local zero-initialized buffer via SEH-wrapped byte-by-byte read with explicit early-out on null, so the compare loop sees guaranteed termination.
- **`!a || !b` break in the case-insensitive compare loop.** The original break-on-`!a` was correct for all length combinations (a single null on one side fails the `a != b` guard first), but the verifiers wanted the symmetric guard as a defense against the volatile-tear scenario above. Cheap defensive change.
- **Skip re-resolve when `targetName` is a `"Slot N"` placeholder.** Path B2 writes synthetic `"Slot 1"`, `"Slot 2"`, ... when GetItemText failed and the bridge fell back to slot-probing. A row-anchor scan for `"Slot 0"` always fails against real CListWnd names, would log a misleading "name NOT in CListWnd," and fall back to `requestedIdx` — that's the v3.20.7 behavior the fix is supposed to correct. Now the prefix check skips the scan and logs the slot-mode reason explicitly.
- **Probe-failure sentinel (`g_cachedNameCol = -2`) + `rowCap = min(charCount, MAX_CHARS)`.** If the on-demand 10-column probe fails to find a name-shaped column, cache that as `-2` so subsequent retries skip the 10× `ReadListItemText` sweep. Row scan now caps at `shm->charCount` (which is enforced ≤ `CHARSEL_MAX_CHARS` by every publishing path), avoiding scans past the published count. Path B's probe at line ~3796 still gates on `nameCol < 0` (re-probes every Poll cycle as before, unchanged) — only the per-request HandleSelectionRequest probe honors the new sentinel.

Verifier false positives (not addressed, with rationale): the `selectedIndex = rowIdx` write is safe (`grep` confirms no C# caller consumes it as a heap-index; only `LoginShmWriter.ReadSelectedIndex` reads it as telemetry). `requestedIdx >= CHARSEL_MAX_CHARS` defense-in-depth is already gated by the existing `charCount` bounds check (which is enforced ≤ 10 at every SHM publish site).

**Files changed:** `Native/mq2_bridge.cpp` (HandleSelectionRequest), `EQSwitch.csproj`, `CHANGELOG.md`.

**Verification gate:** dual-box smoke to in-world per `feedback_dual_box_test_before_autologin_tag.md`. For the character-name-correctness check specifically, `gotquiz1` alone is sufficient — the log should show `selected character row N ("Natedogg")` with a preceding `row re-resolve: heap idx=0 ('Natedogg') -> CListWnd row=N` line if the heap/row orders actually diverge on this account, OR no re-resolve log if they happen to match (in which case the fix was a defense-in-depth no-op for this specific account but still corrects the failure mode that hit gotquiz1).

## v3.20.7 — End-to-end autologin reaches in-world via QUICK CONNECT (2026-05-15)

🎉 **AUTOLOGIN PLUG-AND-PLAY COMPLETE.** First successful end-to-end run 2026-05-15 19:33 — `gotquiz1` reached in-game without a single manual click.

Two changes built on v3.20.6's structural breakthrough:

### 1. QUICK CONNECT button preferred over LOGIN

Per Nate's operator insight 2026-05-15: Dalaya's `QUICK CONNECT` button (tooltip "Quick connect to last server") submits auth AND server-join atomically, bypassing the slow LoginServerAPI populate window AND the ServerSelectWnd Enter dance. Live at `ConnectWnd+0x34`.

`FindConnectButtonStructural()` now picks `byQuickConnect` first (CStrRep label match on "QUICK CONNECT"), falling back to "LOGIN" → default slot → first valid button. The whole "wait for LoginServerAPI ready then JoinServerDirect" detour is moot when QUICK CONNECT is used.

### 2. Char-select SHM signal in `PollForLoginAdvance`

`PollForLoginAdvance` previously checked only `gameState` transitions and window-rect changes. On Dalaya, `gameState` stays at 0 across login → server-select → char-select (only flips at in-game), so neither signal fires. Without a working advance signal, C# falsely declared "credentials likely rejected" and retried credential typing, knocking the client OUT of char-select.

New third check: `charSelect.IsMQ2Available(pid) && charSelect.ReadCharCount(pid) > 0`. The DLL publishes both via SHM the moment `pinstCCharacterSelect` populates — that's the structural "we're past login" signal.

### 3. `PostBurst2QuickFailCheckMs` default 10000 → 60000

Dalaya emu char-select takes 15-25s to load after QUICK CONNECT click; the prior 10s budget timed out before the char-select SHM signal fired. 60s gives generous headroom and is still well below the 90s `WaitForScreenTransition` legacy fallback. Clamp ceiling raised from 30000 to 90000.

### 4. `loginServerAPIReadyTimeoutMs` reverted 5000

v3.20.6 bumped to 30000/60000 trying to wait out the slow LoginServerAPI populate on Dalaya. With QUICK CONNECT the LSAPI/JoinServerDirect path isn't needed — fast-fail to BURST 2 / PollForLoginAdvance (which now has the char-select SHM signal) is correct.

**Verification:** `gotquiz1` smoke 19:32:23 → char-select SHM signal at 19222ms → 19:33:09 charselect ready → 19:33:11 PulseKey3D Enter World → `lastLoginResult: ok`. User confirmed in-game.

**Known remaining issues** (NOT blocking ship — character-name-matching, not autologin):
- Wrong character selected (`acpots` loaded instead of configured `Natedogg`). The byName slot resolution on slot 0 returned acpots. Investigate `RequestSelectionBySlot` name-vs-index handling.
- Second client (`gotquiz`) sometimes hits 30s budget on slow load and gets knocked back to retry — 60s default fixes this for typical runs.

**Files changed:** `Native/eqmain_widgets_mq2style.cpp` (QUICK CONNECT label priority), `Core/AutoLoginManager.cs` (char-select SHM signal, 5s LSAPI timeout), `Config/AppConfig.cs` (PostBurst2QuickFailCheckMs default 60000, ceiling 90000), `EQSwitch.csproj`, `CHANGELOG.md`.

## v3.20.6 — Structural ConnectButton via ConnectWnd-rooted fixed-slot enumeration (2026-05-15)

v3.20.5's proximity-to-password heuristic walked ~69 CButtonWnd-shape widgets globally and picked the closest by address. Two `ConnectButton`s exist in eqmain.dll (MainWnd's vs ConnectWnd's), and "closest in heap" routinely tie-breaks to the wrong one — auth never completed despite Combo G writing the password correctly to both `+0x1A8` and `+0x1EC` CXStrs.

**Fix:** plug-and-play port of MQ2's `StateMachine.cpp:275` backed by `findings.md` Round 5 live verification (PID 22892 2026-05-04). Round 5 established that ConnectWnd's child widgets live at fixed slots in the screen body (NOT in the standard CXWnd pFirstChild list — that returns junk per `probe_connectwnd_children.py`):

```
ConnectWnd+0x2C..+0x38  → 4× CButtonWnd  (LOGIN_ConnectButton is one of these)
ConnectWnd+0x3C..+0x40  → 2× CEditWnd    (username, password)
ConnectWnd+0x48         → 1× CLabelWnd   (Dalaya branding)
```

New `EQMainWidgetsMQ2::FindConnectButtonStructural()`:
1. Resolves ConnectWnd by walking `pinstLoginViewManager+0..+0x200` for the slot whose pointee's vtable matches `RVA_VTABLE_ConnectWnd`. NOTE: this entry initially documented `0x001035C0`, but live verification in v3.20.7 showed the correct primary vtable RVA is `0x001035F4` — `0x001035C0` is a secondary base-class vtable. See the v3.20.7 entry above and `Native/eqmain_offsets.h:139`.
2. Enumerates the 4 button slots, validates CButtonWnd vtable for each.
3. Picks the slot whose body holds a CStrRep matching `"LOGIN_ConnectButton"` or `"ConnectButton"` (both names exist per Round 5 line 60-65 — Dalaya's SIDL XML defines BOTH the prefixed `item=` Name and bare `<ScreenID>`); falls back to first valid button with a loud log line so subsequent smokes can lock the slot if name-heuristic fails.

Wired into `login_state_machine.cpp` `PHASE_CLICKING_CONNECT` as the PRIMARY path. Existing proximity, MQ2-style FindChildByName, and legacy FindWindowByName remain as ordered fallbacks for defense-in-depth.

**Files changed:** `Native/eqmain_offsets.h` (new `RVA_VTABLE_ConnectWnd`, `RVA_PINST_LoginViewManager`), `Native/eqmain_widgets_mq2style.{h,cpp}` (new `FindConnectButtonStructural`, `ResolveConnectWnd`), `Native/login_state_machine.cpp` (reorder fallback chain), `EQSwitch.csproj`, `CHANGELOG.md`.

## v3.20.5 — Structural ConnectButton via proximity heuristic + WndNotification XWM_LCLICK (2026-05-15)

v3.20.4 dual-write to `+0x1A8` AND `+0x1EC` verified working via live probe (`PID 33740 widget 0x14905338` had both CXStrs containing 'Exodus1' post-Combo-G). But auth still failed — meaning `+0x1EC` wasn't the missing piece. Reading MQ2's `StateMachine.cpp:271-275`:

```cpp
SetEditWndText(pPasswordEditWnd, accountPassword);   // +0x1A8 assignment
if (CButtonWnd* pConnectButton = GetChildWindow<CButtonWnd>(m_currentWindow, "LOGIN_ConnectButton"))
    pConnectButton->WndNotification(pConnectButton, XWM_LCLICK, 0);
```

MQ2 fires `WndNotification(XWM_LCLICK)` on the **connect button widget directly** — NOT a VK_RETURN keystroke. EQ's button onClick reads `InputText` from the password edit and submits. VK_RETURN through EQ's keyboard pump apparently doesn't fire that path.

**Fix:** add `EQMainWidgetsMQ2::FindButtonNearWidget(anchor)` — global walk over CButtonWnd-vtable widgets, picks one closest to `anchor` (the password edit address). Wired into `login_state_machine.cpp` `PHASE_CLICKING_CONNECT` as the primary path, before MQ2-style FindChildByName and legacy XMLIndex. Re-uses `MQ2Bridge::ClickButton` which already fires `g_fnWndNotification(btn, btn, XWM_LCLICK, nullptr)`.

The connect button is allocated in the same SIDL-screen cluster as the password edit, so address-proximity reliably identifies it (same mechanism the password edit uses on the username anchor).

**Files changed:** `Native/eqmain_widgets_mq2style.h`, `Native/eqmain_widgets_mq2style.cpp`, `Native/login_state_machine.cpp`, `EQSwitch.csproj`, `CHANGELOG.md`.

## v3.20.4 — Dual-write password to +0x1A8 AND +0x1EC alias CXStr (2026-05-15)

The v3.20.3 18:00 smoke had the proximity heuristic + Fix 1 gate both firing perfectly — Combo G wrote 'Exodus1' to widget `0x14905998 +0x1A8`, BURST 1 fired Activate+Enter only — yet `LoginServerAPI` never populated. Live external probe of the live widgets:

```
PID 1116:  widget 0x14905998
  +0x1A8 → CXStr@148E5F80 rc=1 al=8 len=7 text='Exodus1'   ← Combo G wrote here
  +0x1EC → CXStr@148E60C0 rc=1 al=8 len=0 text=''          ← SEPARATE CXStr, EMPTY
```

Compare to the username widget (typed normally by EQ from `.ini`):
```
  +0x1A8 = CXStr@138B9358 len=8 'gotquiz1'
  +0x1EC = CXStr@138B9358 len=8 'gotquiz1'   ← SAME POINTER (aliased, rc=4)
```

**EQ uses TWO CXStr fields on `CEditBaseWnd`.** When typed via the natural input path, both `+0x1A8` and `+0x1EC` point to the SAME `CStrRep` (refCount=4 from the multiple references). When Combo G writes only `+0x1A8`, `+0x1EC` stays empty. EQ's login submit pipeline reads from (or requires) `+0x1EC` — explains every auth failure since the structural-write path was introduced in v3.15.x: the visible password field shows the chars (read from `+0x1A8`), but the submit pipeline gets an empty buffer.

**Fix:** `EQMainCXStr::WriteEditTextDirect` now writes the password to BOTH `+0x1A8` AND `+0x1EC`. Two separate CXStrs with the same content (functionally equivalent to the aliased state for read-side consumers — EQ reads bytes, not pointer identity). Each `Free` + `ConstructFromCStr` pair is SEH-wrapped; if the alias write fails, the primary `+0x1A8` write is preserved and we return true (degraded but not broken — log marker fires for next-iteration diagnostics).

**Files changed:** `Native/eqmain_cxstr.cpp`, `EQSwitch.csproj`, `CHANGELOG.md`.

## v3.20.3 — Proximity-to-username password widget heuristic + Fix 1 re-engage (2026-05-15)

The v3.20.2 17:20 smoke showed `FindEmptyEditGlobal(filterByVisible=true)` correctly enumerated 4 CEditWnd-shape widgets globally but the visibility filter picked the WRONG widget — `1153BA30` (visible+empty but `0x37370` away from the username, unrelated UI input). The real password edit `115040F0` had `dShow=0` (rendered through asterisk-masking path that doesn't set the standard visibility flag).

**v3.20.3 fix:** replace the visibility filter with **address-proximity to the username-bearing CEditWnd**. EQ allocates SIDL-screen widgets in a tight cluster — the password edit is consistently `0x5D0` below the username. Two-pass algorithm:
1. **Pass 1 (collect):** walk all widgets globally, gather every CEditWnd-shape with valid CXStr at `+0x1A8`.
2. **Pass 2 (proximity pick):** find the anchor (CEditWnd with non-empty CXStr — the ini-prefilled username), then among empties return the one with smallest `|address - anchorAddress|`.

Verified via 17:56 smoke (PIDs 16784 + 29840): proximity heuristic picked `14904128` (PID 16784) and `135092D0` (PID 29840), each at `dist=0x5D0` below their respective anchors — matching the pattern in every smoke so far.

**Also: Fix 1 RE-ENGAGED.** Now that Combo G writes to the right widget, the original double-write bug returns unless BURST 1's typing is suppressed. The `comboGWriteOk` SHM signal + the AutoLoginManager gate (`Core/AutoLoginManager.cs:649`) are re-wired: when Combo G's read-back succeeds, BURST 1 skips primer + retype and only fires Activate + Enter (submit). This is the original v3.15.12 design — finally correct because the widget lookup is finally right.

**New tool: `smoke-team1.sh`** — automates the kill+deploy+launch+hotkey+tail cycle. Cuts iteration time on autologin work from ~minutes to ~30s per smoke.

**Files changed:** `Native/eqmain_widgets_mq2style.h`, `Native/eqmain_widgets_mq2style.cpp`, `Native/eqmain_widgets.cpp`, `Core/AutoLoginManager.cs`, `EQSwitch.csproj`, `CHANGELOG.md`, `smoke-team1.sh` (new).

## v3.20.2 — Global widget walk fallback + relaxed CXStr validation (2026-05-15)

v3.20.1's `FindEmptyEditInScreen("connect")` smoke (PIDs 17360 + 31576, 17:05) showed `scanned=11 editShape=2 validCXStr=1 empty=0 result=00000000` — the structural walk reached only 2 CEditWnd-shape widgets in the connect screen subtree (vs 3 in the prior smoke's `mq2_bridge` diagnostic probe). The visible password edit isn't a direct subtree-descendant of the connect-screen widget on this Dalaya build. Falls through to the broken legacy XMLIndex path → wrong widget → user sees 0-1 chars in visible password field.

**New:** `EQMainWidgetsMQ2::FindEmptyEditGlobal(bool filterByVisible)` — uses `MQ2Bridge::IterateAllWindowsPublic` to walk the entire `pinstCXWndManager` widget collection (the same enumeration the mq2_bridge diagnostic probe uses to find 3 CEditWnds). Same per-widget filter as `FindEmptyEditInScreen` — vt + valid +0x1A8 CXStr + length==0 — applied globally.

Wired as path 4 in `FindLivePasswordCEditWnd`, between the connect-screen-subtree variant (path 3) and the legacy XMLIndex fallback (now path 5). Tries with `filterByVisible=true` first (`dShow != 0 AND minimized == 0` per `OFFSET_CXWND_DSHOW`/`OFFSET_CXWND_MINIMIZED`); falls back to unfiltered walk if visibility filter rejects everything.

**Also:** loosened CXStr refCount upper bound from `0x10000` to `0x10000000` (Opus T2-C3 verifier 2026-05-15) — the prior bound was empirical-from-one-sample and could reject potentially-interned empty-string singletons.

**Diagnostic logging:** every CEditWnd-shape widget visited globally is logged with vtable, CXStr pointer, refCount, alloc, length, and visibility/minimized state. The smoke output will narrow down exactly what's in the widget tree even if the lookup still fails.

**Files changed:** `Native/eqmain_widgets_mq2style.h`, `Native/eqmain_widgets_mq2style.cpp`, `Native/eqmain_widgets.cpp`, `EQSwitch.csproj`, `CHANGELOG.md`.

## v3.20.1 — Structural-empty password widget lookup (2026-05-15)

The v3.20.0 dual-box smoke (PIDs 3180 + 10012, 16:25) revealed the **real** upstream bug behind autologin failure: the hardcoded `XMLIndex=0x00220001` fallback in `FindLivePasswordCEditWnd` returns a widget at `0x11504A08` that has the right vtable but **no valid CXStr at +0x1A8** — meaning Combo G's `WriteEditTextDirect` happily writes 7 bytes to that offset and the read-back succeeds, but those bytes go to memory that isn't bound to any visible CEditWnd's render path. **Screenshot of PID 10012 confirmed**: visible password field shows only 1 asterisk (the lone BURST 1 keystroke that landed before Enter), not 7.

Ground truth from `eqswitch-dinput8-10012.log` lines 145-184 — the connect screen has 3 widgets with CEditWnd-or-CEditBaseWnd vtable:

| Address | +0x1A8 (InputText) | Identity |
|---|---|---|
| `115040F0` | `CXStr len=0 data=''` | **Real visible password edit** (empty before typing) |
| `115046C0` | `CXStr len=7 data='gotquiz'` | Username edit (ini-prefilled) |
| `11504A08` | **no valid CXStr** | False-positive — vt matches but no real InputText. Picked by legacy XMLIndex fallback. |

**Fix:** new `EQMainWidgetsMQ2::FindEmptyEditInScreen("connect")` — walks the connect screen subtree and returns the first CEditWnd-shape widget whose `+0x1A8` CXStr is valid AND has length 0. Wired as a third path in `FindLivePasswordCEditWnd`:
1. Cache fast-path (unchanged)
2. MQ2-style `FindChildByName('connect','LOGIN_PasswordEdit')` (broken on Dalaya, returns NULL — kept for forward-compat)
3. **NEW: `FindEmptyEditInScreen("connect")`** — structural by shape + emptiness, doesn't depend on the broken XMLIndex
4. Legacy XMLIndex fallback (kept as last-resort safety net; known broken on current Dalaya)

The `+0x1A8` validity check (refCount in [1, 0x10000), length ≤ alloc, length ≤ 0x80) cleanly rejects the false-positive widget at `11504A08`. The empty-length filter cleanly excludes the username edit.

**Edge case deferred to a future release:** on re-login when the visible password field still has leftover asterisks (length > 0), the structural-empty path returns NULL and we fall through to the broken legacy path. Mitigation: BURST 1 keystrokes (safety net, restored in v3.20.0) still fire and typically deliver enough characters that the next retry's field-clear (16x Backspace) plus type cycle gets the field back to empty. A future fix would use the CEditBaseWnd `bSecret`/`bPassword` flag (need to identify the offset via Ghidra-or-equivalent) for unambiguous identification.

**Files changed:** `Native/eqmain_widgets_mq2style.h`, `Native/eqmain_widgets_mq2style.cpp`, `Native/eqmain_widgets.cpp`, `EQSwitch.csproj`, `CHANGELOG.md`.

## v3.20.0 — Fix 2: LoginServerAPI-ready gate (SHM v5 → v6) + Fix 1 revert (2026-05-15)

> **R2 addendum (verifier-driven, Sonnet+Opus T2/T3 convergent):** The initial v3.20.0 had a CRITICAL same-Tick race — the poll-and-publish block runs AFTER the LOGIN_CMD_LOGIN reset block in the same Tick. If `pinstLoginServerAPI` was still populated from a prior session (C#-keeps-mapping-open path / session ended at char-select), the stability counter climbed 0→3 within ~48ms and republished `ready=1` BEFORE BURST 1 had typed credentials. R2 adds a `g_sawNonReadyAfterLogin` gate: publish requires BOTH 3-tick stability AND at least one observed not-ready tick since the last LOGIN_CMD_LOGIN reset. At EQ's login screen `pinstLoginServerAPI` IS NULL per probe history, so the gate naturally clears on the happy path. Same R2 also: (a) distinguishes failure modes in DI8 logs (NULL pAPI vs vtable mismatch with observed RVA — closes Sonnet T2-C2's diagnostic gap for Dalaya patches), and (b) fixes BURST 2 primary path's live `_config.Launch.*` reads (snapshots `burst2ActivationSettleMs` + `burst2PostKeystrokeMs` matching the retry path's v3.17.0 R3 sweep).


The 2026-05-15 PM smoke (PIDs 20836 + 36156, post-v3.19.0 Fix 1) failed: user observed **"neither character entered any password at all"** in the visible password field, and `JoinServerDirect` returned `fnResult=0x00000003` ("no auth session") on both clients. Root-cause via DLL log:

1. `FindChildByName('connect','LOGIN_PasswordEdit')` returns NULL on current Dalaya — the structural password lookup falls back to a hardcoded `XMLIndex=0x00220001` → manager-walk match. Combo G writes 7 bytes to that widget's `+0x1A8` and the read-back confirms, **but the widget is NOT the visible password edit**. The `WriteEditTextDirect` read-back is verification-too-close-to-the-action: it reads from the same `+0x1A8` we just wrote to, so it gleefully confirms even when the target widget is wrong.
2. With v3.19.0's Fix 1 active, BURST 1 SKIPPED its keystroke typing on the `comboGWriteOk=1` signal — leaving the visible password field empty.
3. `JoinServerDirect` fired on a fixed `PostBurst1WaitMs=3000ms` timer before the auth handshake completed → no LoginServerAPI session → `fnResult=3` rejection.

This is precisely the failure mode the v3.15.13 commit message documented: **"revert v3.15.12 BURST 1 gate — Combo G doesn't reach EQ submit buffer"**. Fix 1 was v3.15.12 redux with an SHM flag and re-discovered the same bug. v3.16.0's ScreenMode=3 swap closes the input-filter side of the problem but can't compensate for writing to the wrong widget entirely.

**Behavior changes:**

- **Fix 1 reverted (BURST 1 keystrokes always type).** `Core/AutoLoginManager.cs:617+` no longer reads `comboGWriteOk` to skip BURST 1's primer + password retype. BURST 1 keystrokes are the proven safety net per the session kick-off doc ("BURST 1 KEYSTROKE fallback is what actually carries the load"). Native still publishes `comboGWriteOk=1` after Combo G's read-back succeeds (kept for diagnostic + future use if a working structural password write is found), but C# ignores the signal.
- **Fix 2: LoginServerAPI-ready gate (SHM v5 → v6).** Native `login_state_machine.cpp` Tick() polls `*(eqmain+0x150164)` every tick:
  - 0 = `pinstLoginServerAPI` is NULL or its vtable doesn't match `eqmain+0x1002D0`
  - 1 = populated AND vtable matches, for **≥3 CONSECUTIVE Ticks** (stability counter — defends against transient construction state at very-early launch / EULA→login screen transitions per 2026-05-14 probe history)
  
  A single bad tick resets the counter AND clears the SHM flag. Reset on every `LOGIN_CMD_LOGIN` so a stale "1" from a prior session can't bleed into the new one. Edge-logged at 0→1 and 1→0 transitions.
- **C# polls the ready-flag before dispatching JoinServerDirect.** `TryJoinServerDirectOrFallback` calls `loginShm.WaitForLoginServerAPIReady(pid, 5000)` before sending the request. If the flag never reaches 1 within 5s, dispatch is SKIPPED — caller falls through to BURST 2 (server-select Enter PostMessage), preserving the pre-Fix-2 safety net behavior. Replaces wall-clock-only timing (`PostBurst1WaitMs=3000ms`) with auth-state observation.

**Native ABI:**

- `LOGIN_SHM_VERSION 5 → 6`. Appended `volatile uint32_t loginServerAPIReady` at offset 1628. Total `LoginShm` = 1632 bytes. v5 native readers see only the first 1628 bytes — they never publish the field, so C# v6 readers always observe 0 → 5s timeout → BURST 2 fallback (graceful degradation matching pre-v6 behavior).

**Files changed:** `Native/login_shm.h`, `Native/login_state_machine.cpp`, `Core/LoginShmWriter.cs`, `Core/AutoLoginManager.cs`, `EQSwitch.csproj`, `CHANGELOG.md`.

**Verification:** dual-box smoke gated per `feedback_dual_box_test_before_autologin_tag.md` — no tag until both clients reach in-world.

## v3.19.0 — Diff 4 wire-in (LoginServerAPI::JoinServer C# call) + Diff 2/3 toggle re-enable + SHM v4 → v5 (2026-05-15)

> **Addendum (Fix 1):** SHM bumped further to v5 within the same release window after the 2026-05-15 PM smoke revealed a BURST 1 / Combo G double-write bug. New `comboGWriteOk` field (uint32 at offset 1624, total 1628) lets native publish "structural CXStr write at InputText+0x1A8 succeeded" so C# can SKIP BURST 1's primer + password retype, eliminating the 13-char field corruption that was causing EQ login server rejection and "Quick Connect failed" popups. Fix 1 also added defensive `comboGWriteOk = 0` clears in the LOGIN_CMD_CANCEL handler and SetError path. See `Native/login_shm.h:240+` for the v5 field doc and `Core/AutoLoginManager.cs:617+` for the gate logic.

Closes the MQ2 RoF2-emu autologin walkthrough's structural shortcuts (`_.eqswitch-re/mq2-autologin-walkthrough.md` §5.1 + `mq2-autologin-eqswitch-diff.md` Diff 2/3/4). v3.18.0 shipped the `MQ2Bridge::JoinServerDirect` primitive as dormant; this release wires it from C# via SHM RPC AND re-enables the structural `kMQ2StyleWidgetLookup` toggle.

**Behavior changes:**

- **Structural LOGIN_ConnectButton click (Diff 2/3).** `Native/eqmain_widgets_mq2style.h:115` flipped `kMQ2StyleWidgetLookup = false → true`. The previous legacy heap-cross-ref path returned a `CXMLDataPtr` definition (rejected by `IsEQMainButtonWidget`), retried 3× then C# CANCELed and fell back to BURST 1. The structural path (`FindLiveScreenByName` via LVM+0x14 anchor + `RecurseAndFindName`) returns the LIVE widget. Iter-12 dormancy reason (game-thread starvation dropping BURST keystrokes) is now mitigated by v3.16.0's ScreenMode swap making Combo G writes reach the submit buffer — no BURST = no GetDeviceState race.
- **JoinServerDirect bypasses BURST 2 (Diff 4).** C# `AutoLoginManager.TryJoinServerDirectOrFallback` calls eqmain's `LoginServerAPI::JoinServer` in-process via SHM RPC. Native handler in `login_state_machine.cpp` observes `joinServerReqSeq` increment, dispatches the `__thiscall` on the LoginServerAPI vtable at `+0x13C30`, writes outcome + fnResult, sets `ackSeq = reqSeq`. Replaces the BURST 2 (server-select VK_RETURN PostMessage) when JoinServer succeeds with `outcome=Success AND fnResult=0`. Fallback to BURST 2 preserved for all failure modes (LoginServerAPI null, vtable mismatch, prologue patch, SEH inside call, 2s ack timeout, non-zero EQ error code).
- **Retry path symmetry.** Both primary and retry RunLoginSequence paths use the shared `TryJoinServerDirectOrFallback` helper. Pre-fix, retry bypassed Diff 4 entirely (BURST 2 only).
- **SettingsForm pass-through bugs fixed.** 6 latent v3.17.0+ Launch fields were missing from the LaunchConfig builder — every Settings → Apply silently reset them to class-initializer defaults. `JoinServerId`, `StaleSessionPollIntervalMs`, `ConnectRetryCount`, `PostBurst2QuickFailCheckMs`, `SkipShmEnterWorldOnDalaya`, `SkipNativeWarmup` all preserved correctly now.

**Native ABI:**

- `LOGIN_SHM_VERSION 3 → 4`. Appended 5 × uint32 (20 bytes) at offset 1604: `joinServerSerialId` (in), `joinServerReqSeq` (in), `joinServerAckSeq` (out), `joinServerOutcome` (out: 0=pending/1=success/2=failed/3=gated), `joinServerFnResult` (out). Total LoginShm = 1624 bytes. v3 native readers see only the first 1604 bytes — they never observe `joinServerReqSeq` increment and never dispatch JoinServerDirect (graceful forward-compat).

**Config:**

- New `Launch.JoinServerId` (default `1` for Dalaya; `0` disables the wire and forces BURST 2 always; clamped 0..100 by `Validate()`).

**Files changed:** `Native/eqmain_widgets_mq2style.h`, `Native/login_state_machine.cpp`, `Native/login_shm.h`, `Native/eqswitch-di8.cpp`, `Core/LoginShmWriter.cs`, `Core/AutoLoginManager.cs`, `Config/AppConfig.cs`, `UI/SettingsForm.cs`.

**Verification:** 16 verifier agents across 2 rounds (Sonnet+Opus on Diff-clean / Gap-audit / Code-review / Blast-radius). Round 1 found CONCERNS; Round 2 fixes addressed read-side memory barrier, `fnResult==0` check, retry path symmetry, `ResetDllToCsharpFields` v4 field zeroing, SettingsForm pass-throughs, `Validate()` JoinServerId clamp. Single-box + dual-box smoke gated per `feedback_dual_box_test_before_autologin_tag.md`.

## v3.18.0 — Native OK_Display error-text probe + SHM v3 contract bump (2026-05-15)

Closes the deferred work from v3.17.0's "Known deferred (out of v3.17 scope; tagged for v3.18+)" list. The v3.17.0 retry loop in `Core/AutoLoginManager.cs` always slept the full `staleSessionWaitMs` (default 30s) on every retry because C# couldn't distinguish "truncated creds, server didn't accept" from "stale-session, server holding slot" from "wrong password, will never accept". Two inline comments at lines ~954 and ~975 explicitly flagged the OK_Display error-text probe + SHM v3 bump as the structural fix. v3.18.0 ships it.

**Behavior changes:**

- **Fatal error short-circuit.** If the live OK_Display dialog text classifies as `Fatal` (Native pattern-matches "password were not valid", "Invalid Password", "enter a username and password"), the C# retry loop **breaks out of the retry budget entirely** instead of consuming N retries with ~30s waits between each. The user sees the actual error within ~5s of the post-BURST-2 fast-fail probe firing, not after `1+ConnectRetryCount × StaleSessionWaitMs` seconds of pointless re-typing. Practical save: `ConnectRetryCount=1` was 60s of fruitless retry; `=5` was 150s.
- **Recovery-wait tuning from dialog text.** When OK_Display class is `Recoverable`, the recovery wait is tuned by case-insensitive substring match on the dialog text:
  - `"truncated"` / `"incomplete"` → **1000ms** (creds were mangled by layout-skip; server didn't see a real submission, no stale slot to wait out)
  - `"stale"` / `"still logged in"` / `"in use"` → **`staleSessionWaitMs`** (default 30s — server holds the slot)
  - any other recoverable text → falls back to `staleSessionWaitMs` (safe default)
- No tunable knobs added. The text→ms tuning lives entirely in code; if a future EQ build returns different dialog text, the patterns get updated in `Core/AutoLoginManager.cs` (single-site change). Net retry-cycle savings on a "truncated creds" miss: 30s→1s wait inside the bounded loop, ~29s per retry attempt.

**SHM contract bump (v2 → v3):**

- Backward-compatible append per the v2 pattern. `LOGIN_SHM_VERSION` is now 3.
- Two new fields at end of `LoginShm` struct:
  - `char okDisplayText[LOGIN_ERROR_LEN]` (256 bytes) at offset 1344 — LIVE poll-tick mirror of the OK_Display widget text. Cleared every poll where `g_pOkDisplay` is null OR returns empty text. Distinct from the existing `errorMessage` field which is set ONCE on PHASE_ERROR via `SetError()` and stays.
  - `volatile uint32_t okDisplayClass` (4 bytes) at offset 1600 — pre-classification by native (0=None / 1=Fatal / 2=Recoverable / 3=Success). Lets C# tune behavior without re-implementing the strstr matching that already lives next to the dialog read in `login_state_machine.cpp`.
- Total struct size: 1344 → 1604 bytes (+260 bytes append). C# v3 writers (`Core/LoginShmWriter.cs`) allocate 1604 bytes; v2 native readers (no recompile) see the first 1344 bytes unchanged and ignore the trailing 260 bytes.

**Always-on probe in native (the load-bearing detail):**

The OK_Display SHM mirror is published from a NEW standalone helper `PollOkDisplayToShm` in `Native/login_state_machine.cpp`, called from `LoginStateMachine::Tick()` after the command-receive block but **BEFORE the PHASE_IDLE/COMPLETE/ERROR early-out**. This is necessary because:

- PATH A (`TryLoginViaShm` in `Core/AutoLoginManager.cs` line ~795) is currently commented out, so the DLL state machine never enters `PHASE_WAIT_CONNECT_RESP`.
- Today's actual login flow (PATH B keystroke retry) runs entirely on C# while the DLL state machine sits at `PHASE_IDLE`.
- Without the always-on probe, the v3 SHM mirror would be functionally dormant — only useful when PATH A is reanimated as part of the future "D" task referenced in `Core/AutoLoginManager.cs` line ~786 ("you need EITHER (a) a real DLL post-connect detection signal").
- Cost: two `FindWindowByName` heap-walks per 500ms tick. Negligible relative to the bridge's existing `kPromptWindows[]` walk and `HeapScanForWidget` calls.

The in-phase `PHASE_WAIT_CONNECT_RESP` body still calls `SetOkDisplay`/`ClearOkDisplay` directly when fatal/success/recoverable patterns match — those are now redundant-but-safe (write the same data the standalone probe would). Defense-in-depth — guarantees publish even if a future refactor moves the always-on probe.

**Files changed:**

- `Native/login_shm.h` — bumped `LOGIN_SHM_VERSION` 2→3, appended `okDisplayText[256]` and `volatile okDisplayClass` after `autoLoginActive`. Added v3 comment block explaining LIVE-vs-set-once distinction from `errorMessage`.
- `Native/login_state_machine.cpp` — added `SetOkDisplay()` and `ClearOkDisplay()` helpers near `SetError()`. Added `PollOkDisplayToShm()` standalone always-on probe with classification. Wired the probe into `Tick()` after cmd-receive, before phase-machine early-out. In-phase `PHASE_WAIT_CONNECT_RESP` body now writes the SHM mirror at all four classification branches (fatal / success / recoverable / no-dialog) for defense-in-depth.
- `Core/LoginShmWriter.cs` — bumped `Version` 2→3, `ShmSize` 1344→1604, added `OFF_OK_DISPLAY_TEXT=1344` and `OFF_OK_DISPLAY_CLASS=1600` offsets, `ReadOkDisplayText(pid)` and `ReadOkDisplayClass(pid)` accessors, `OkDisplayClass` enum (None/Fatal/Recoverable/Success). `Open()` zeros the new fields on init. Defensive coercion of out-of-range class values to None (forward-compat with hypothetical v4 native that adds class buckets).
- `Core/AutoLoginManager.cs` — at retry-loop entry, snapshots `okClass` + `okText` from SHM (alongside the v3.17.0 R3-snapshotted tunables). Fatal class breaks the retry loop. Recoverable class tunes the `CancellableSleepUntilProcessDies` wait based on text patterns. None / Success classes fall through to the existing `staleSessionWaitMs` + gameState gate.
- `EQSwitch.csproj` — version bump 3.17.0 → 3.18.0.

**Diff 4 PRIMITIVE shipped alongside (dormant; no autologin-behavior change in v3.18.0):**

In-process `MQ2Bridge::JoinServerDirect(int serverID, unsigned int *outResult)` lands as the in-DLL callable wrapper for eqmain's `LoginServerAPI::JoinServer` at fixed RVA 0x13C30, on the LoginServerAPI instance at `*(eqmain+0x150164)`. Bypasses the entire UI server-select click chain — when the C# call site lands in v3.19+, BURST 1 keystroke fallback for the server-select Enter becomes dead weight (per emu-branch `StateMachine.cpp:773` MQ2 itself never clicks the server row, only this API call). 4 verifier rounds (20 agents total) on the implementation:
- **Layered defense:** GetModuleHandleA cross-check (catches mid-session DLL drops via search-order hijack) + LoadLibraryA refcount pin (TOCTOU defense across function lifetime, balanced FreeLibrary on every exit path) + vtable[0] == eqmain+0x1002D0 check (catches pre-init substitution; load-bearing per MQ2 RoF2-emu walkthrough Section 5.1) + prologue-byte check ({0x55,0x53,0x56,0x57,0x83,0x8B,0x6A}; catches Dalaya-patch RVA shift / anti-cheat hooks / function-stub planters)
- **Idiomatic out-param contract** (R3): `outResult` is written ONLY when bool returns true; on false return, caller's pre-call value is preserved. NO sentinel write — would collide with valid EQ result codes (e.g. `(unsigned)-2 == 0xFFFFFFFE`). C# wiring contract documented in header for v3.19+ author: P/Invoke MUST be `ref uint`, NOT `out uint`.
- **Durable /OPT:REF anchor:** file-scope `volatile void *g_keepJoinServerDirect` + `extern "C" __declspec(noinline) MQ2Bridge_GetJoinServerDirectAnchor()` getter. Init() writes the anchor — defeats COMDAT elimination under any /O2 + /OPT:REF + /OPT:ICF + /LTCG combination.
- **Files (additive, no v3.18 dirty-tree clobber):** `Native/eqmain_offsets.h` (+26 LOC: `RVA_PINST_LoginServerAPI`, `RVA_FN_LoginServerAPI_JoinServer`, `RVA_VTABLE_LoginServerAPI_Secondary`), `Native/mq2_bridge.h` (+~85 LOC: declaration with full layered-defense + out-param + thread-safety + C# wiring contract docs), `Native/mq2_bridge.cpp` (+~200 LOC: implementation + Init() anchor wire-up). Built clean alongside v3.18.0 OK_Display SHM bump in same `eqswitch-di8.dll` artifact.
- **C# wiring deferred to v3.19+** to avoid clobbering v3.18.0 SHM contract bump in flight. Smoke gate (dual-box per `memory/feedback_dual_box_test_before_autologin_tag.md`) fires once C# call site lands.
- **Doc supersession (R3):** `_.eqswitch-re/findings.md` Round 2/3 stale "0x10150174 likely LoginServerAPI" rows now have SUPERSEDED banner + inline ⚠️ annotation. `_.eqswitch-re/probe_login_globals.py` docstring marks resolved. `_.eqswitch-re/mq2-autologin-walkthrough.md` Section 5.1 lines 778-781 corrected from "not yet pinned" to "PINNED" with cross-ref to shipping constants. `Native/login_givetime_detour.cpp` historic comment cross-refs `EQMainOffsets::` constants. `PLAN_v7_givetime_detour_HANDOFF.md` SUPERSEDED banner narrows obsolete to JoinServer/CE substeps only (GiveTime detour itself remains current).

**Build status:** Debug + Release builds successful, 0 warnings / 0 errors. Native `eqswitch-di8.dll` built clean (one pre-existing C5051 warning at `Native/eqmain_widgets.cpp:259` unrelated to v3.18). Migration fixture suite (`_tests/migration/run_fixtures.sh`): 9 passed, 0 failed.

**Ship gate:** Per `memory/feedback_dual_box_test_before_autologin_tag.md`, autologin changes require a manual dual-box smoke before tagging. NOT auto-tagged or auto-deployed. The OK_Display SHM mirror is non-perturbative for the happy path (no dialog → `okDisplayClass == None` → C# falls through to existing `staleSessionWaitMs` + gameState gate, same behavior as v3.17.0). The retry-tuning code paths only fire when EQ surfaces an actual error dialog mid-login — verify by intentionally typing a bad password on one of the smoke clients and confirming the retry loop short-circuits within ~5s instead of waiting the full ~30s.

**Known deferred (out of v3.18 scope; tagged for v3.19+):**

- **EULA-screen Enter→DECLINE hazard.** v3.17.0 noted this requires a "native widget-name probe" to extend `kPromptWindows` lookup result through SHM to C#. v3.18.0's OK_Display probe addresses one half (error-dialog text) but not the EULA case (EULA isn't the OK_Display widget — it's a separate prompt window). Filed as v3.19.0 prerequisite.
- **Architectural state machine.** Multi-session work — v3.17.0's note still stands.
- **Fully wire PATH A.** With the OK_Display SHM mirror in place, the "D" task (real DLL post-connect detection signal) becomes meaningfully easier — the C# `TryLoginViaShm` path can poll `okDisplayClass == Fatal` to early-exit on credential rejection instead of waiting the full 45s timeout. Filed as v3.19.0+ candidate.
- **Fatal pattern coverage gaps.** Verifier-pair sweep R1 surfaced ≥3 EQ error texts that current Fatal patterns miss ("Account suspended", "Your username or password was incorrect", patch-required) and ≥3 false-positive risks (system broadcasts containing "Invalid Password"). Right fix is empirical capture — record actual Dalaya dialog text via `DI8Log` over a multi-attempt smoke session, then expand the `ClassifyDialogText` patterns. v3.18.0 ships with the 3 patterns that v3.17.0 already documented.
- **Recovery-wait pattern coverage gaps.** Same pattern as above: verifier flagged real EQ texts ("Connection timed out", "Server temporarily unavailable") that fall through to default. Same fix path — empirical capture before pattern expansion.
- **`ShmLayoutTests.cs` lacks `LoginShm` assertions.** Existing test file asserts struct layout for `SharedKeyState` and `CharSelectShm` but not `LoginShm`. v3 was the second major bump without test coverage being added. Filed for v3.18.1.

**R1 verifier-pair sweep findings + R2 fixes (in-session, 2026-05-15):** Eight parallel agents (4 topics × Sonnet + Opus on identical prompts) flagged a mix of CRITICAL and MEDIUM convergent issues. Fixed in R2 before this CHANGELOG entry settled:

- **`Open()` re-Open path doesn't re-zero v3 fields (T2-S C3 + T2-O #13).** Convergent CRITICAL. The `if (_mappings.ContainsKey(pid)) return true;` fast-path returned without re-zeroing DLL→C# state. If a re-fire of login on the same PID/instance left stale `okDisplayClass=Fatal` from a prior session, the new retry loop reads Fatal at retry-loop entry and short-circuits the budget before native re-publishes. Fix: extracted `ResetDllToCsharpFields(accessor)` helper called on BOTH the fresh-init path AND the existing-mapping early-return path. Defense-in-depth — eliminates the failure mode regardless of which AutoLoginManager flow triggers it.
- **Class+text torn-read race (T2-O #14 + T3-S LOW + T3-O P3).** Convergent HIGH. C# read `ReadOkDisplayClass` and `ReadOkDisplayText` as separate accessor calls — native could write between them, producing `class=Fatal text=""` or `class=None text='Invalid Password'` mismatches. Fix: added `ReadOkDisplaySnapshot(pid)` that reads class, then text, then re-reads class; if class differs across the bracketing reads, returns `(None, "")` (snapshot incoherent — next iteration tries again). Replaces the two-call pattern at `AutoLoginManager.cs` retry-loop entry.
- **OK_Display log-line credential leak risk (T3-S MED + T3-O P1 #5).** Convergent HIGH (security). `AutoLoginManager.cs:951` logged `text='{okText}'` verbatim. EQ may surface dialogs that echo the username back. Fix: log only `class=X, text.length=N` — class drives behavior, length aids debugging without leaking. Mirrors v3.15.6 native-side credential-redaction stance.
- **Native pattern duplication (T2-O #4 + T3-S LOW + T3-O P1 #4).** Convergent MED. The strstr classification (`"password were not valid"`, `"Invalid Password"`, `"enter a username and password"`, `"Logging in to the server"`) was duplicated between `PollOkDisplayToShm` and the in-phase `PHASE_WAIT_CONNECT_RESP` body. Adding a new pattern to one site only would cause silent drift. Fix: extracted `static uint32_t ClassifyDialogText(const char *text)` helper near `SetOkDisplay`. Both callers now use it (plus the in-phase body's act-on-classification dispatch is driven by the helper's return value, replacing the duplicated if-chain).
- **Doc-comment line-number drift (T1-O MINOR + T2-O #11/#12).** MINOR — `login_shm.h:123` and `login_state_machine.cpp:168` referenced "lines ~487-518" and "~line 525" for the in-phase classification block; actual was around 605-650 even before the R2 helper extraction. Fix: replaced specific line refs with semantic refs ("the always-on `PollOkDisplayToShm` probe and the in-phase `PHASE_WAIT_CONNECT_RESP` body call it") that don't rot when the file evolves.

**Verifier findings NOT addressed in v3.18.0 (deferred to v3.18.1+):**

- **Snapshot read once per retry-loop iteration, not refreshed mid-iteration (T2-S M1 + T2-O #3).** If EQ dismisses a Fatal dialog DURING the retry's recovery-sleep + BURST re-fire, the iteration's snapshot stays Fatal and breaks the loop on a now-gone dialog. Probability low (Fatal dialogs typically persist). Filed as v3.18.1 architectural-improvement candidate.
- **`g_pYesButton = nullptr` reset every Poll tick (T3-O P1 #2).** `DiscoverDialogWidgets` unconditionally nullifies `g_pYesButton`. With Poll firing every tick from PHASE_IDLE onward, the YESNO retry-counter logic at `login_state_machine.cpp:~700` never sees a non-null pointer. Currently moot (patchme bypasses kick-session per `feedback_eqswitch_no_yesno_in_patchme.md`); becomes load-bearing if a future non-patchme flow needs YESNO resolution. Filed as v3.18.1.
- **`ReadUInt32` not wrapped in try/catch on torn process death (T3-O P2).** `MemoryMappedViewAccessor` IO exception during a process-death race could propagate through the retry loop. Symmetric exposure with `ReadPhase`/`ReadError` — pre-existing, not introduced by v3.18.0. Filed as v3.18.1+.
- **Fatal/Recoverable pattern empirical expansion** — see "Known deferred" above.
- **`ShmLayoutTests.cs LoginShm assertions** — see "Known deferred" above.

**R1 hallucinations caught (transparency):** T4-Sonnet flagged "Pre-built DLL not yet committed — release gate will BLOCK" as HIGH. Inspection: the currency-check gate compares git COMMIT timestamps; once committed alongside source the DLL ts == source ts and gate passes. The gate is working as designed and isn't a v3.18 implementation bug — just a commit-discipline reminder for whoever tags. Reclassified as informational.

**Build status post-R2:** Debug + Release builds successful, 0 warnings / 0 errors. Native `eqswitch-di8.dll` built clean (one pre-existing C5051 warning at `Native/eqmain_widgets.cpp:259` unrelated to v3.18). Migration fixture suite (`_tests/migration/run_fixtures.sh`): 9 passed, 0 failed.

**R2 verifier-pair sweep findings + R3 fixes (in-session, 2026-05-15):** Eight parallel agents (4 topics × Sonnet + Opus) re-verified the post-R2 state. 2 APPROVE / 5 CONCERNS / 1 REJECT. The REJECT (T2-Sonnet) caught an R1 finding R2 only partially addressed; convergent with T2-Opus + T3-Opus on the residual leak surface. Fixed in R3:

- **Incomplete `okText` redaction (T2-S REJECT + T2-O #f + T3-O LOW + R1 partial).** R2 redacted the info-level snapshot log at `AutoLoginManager.cs:966` but missed two sibling sites: (a) the Fatal-branch `FileLogger.Error($"...({okText})")` at line 975 logged verbatim text; (b) the user-facing `Report($"{account.Name}: {okText}")` at line 976 surfaced verbatim text to the tray balloon; (c) the recovery-wait `tuningReason = $"recoverable (unrecognized: '{okText}')"` at line 1055 embedded full text into a string then logged at info-level. R3 redacts all three: error-log uses `text.length=N`, Report() uses generic class-driven `"login rejected — credentials problem (check eqswitch.log for diagnostic)"`, tuningReason becomes `"recoverable (unrecognized text, length=N)"`. Future EQ dialogs that echo creds back can't leak via any of the three paths.
- **Charselect arrays not zeroed by Reset (T2-O #2).** R2's `ResetDllToCsharpFields` zeroed 9 fields but missed the 720 bytes of `charNames[10][64]` + `charLevels[10]` + `charClasses[10]`. Re-Open on same PID after a prior charselect session would leave stale character data visible to any C# read before native re-populates. R3 adds three `WriteArray` calls to zero these on every Reset call. Cost: ~720 bytes of zero-writes on a path that's already writing ~1KB.
- **Log noise on the common happy path (T3-O LOW).** The retry-loop entry's info log fired `class=None, text.length=0` on every iteration when no dialog was visible — the most common case. R3 gates the log on `okClass != None || okText.Length > 0` so the no-dialog ticks are silent.

**Verifier findings NOT addressed in v3.18.0 (deferred to v3.18.1+):**

The R2 sweep also surfaced ≥4 architectural / philosophical concerns that don't have minimal R3 fixes — they need design work, not surgical edits. All filed for v3.18.1:

- **Same-bucket text races in `ReadOkDisplaySnapshot` (T2-S + T2-O).** The class-text-class read pattern detects class transitions but not text-only mutations within the same class bucket (two different Fatal messages, or two different Recoverable messages). Right fix: add `volatile uint32_t okDisplaySeq` field to SHM and use a seqlock pattern (seq-read-class-text-seq-recheck). Not a v3.18.0 ship blocker — current usage drives buckets, not exact text.
- **DLL-side static state vs SHM Reset divergence (T2-O #3).** `g_phase`, `g_lastCommandSeq`, `g_retryCount`, `g_yesBtnAttempts` etc. in `login_state_machine.cpp` are per-DLL-instance, not per-PID. C# Reset only clears SHM; the DLL retains in-process state from prior login on same EQ process. Right fix: add `LOGIN_CMD_RESET_STATE` command that calls native `Shutdown()`-equivalent. v3.18.1.
- **No memory barrier on native `SetOkDisplay` write side (T3-O P3).** `okDisplayClass` is `volatile` but `okDisplayText` is plain `char[256]`. C#'s torn-detect catches class-bracketed races; doesn't catch compiler reordering on the writer side. Worth a `_ReadWriteBarrier()` between text-write and class-write. v3.18.1.
- **Torn-read warning rate limiting (T3-O P1 + T3-S MED).** No counter or rate-limit on the snapshot's "torn read" Info log. Bounded today by retry-loop budget (≤ConnectRetryCount lines per login), but a future caller adding the snapshot inside a tight poll loop could flood. v3.18.1.
- **`ReadOkDisplayText`/`ReadOkDisplayClass` are dead public API.** R2 added the snapshot wrapper but kept the single-field methods. Grep confirms zero callers in workspace. Candidates for `[Obsolete]` or deletion in v3.18.1.

**Why stop at R3.** Per `~/.claude/projects/X---Projects/memory/feedback_verifier_loop_diminishing_returns.md` — empirically R1=N bugs, R2=N-2, R3=0 bugs + hallucinations (ClipStack DPAPI 2026-05-09 case study). This session's R1=~6 convergent + R2=3 convergent are consistent with that pattern. R3 fixes addressed the convergent R2 findings; an R3 verification sweep would burn ~5-8 more minutes for empirically-zero additional real bugs. Deferred-list items above are the right destination for the philosophical/architectural concerns that R2 surfaced. --no-completion-check applied for the same reason.

**Final build status (post-R3):** Debug + Release builds successful, 0 warnings / 0 errors. Migration fixture suite: 9 passed, 0 failed.

**In-flight smoke hotfix (2026-05-15 00:31):** First deploy to proggy install at 00:21 hung the game thread for ~3s during the 00:23 retry attempt — Windows surfaced "please wait or close this program" but it recovered after 3s. Recurred at 00:26. Root cause hypothesis: `PollOkDisplayToShm`'s every-tick `FindWindowByName` heap-walks contend with EQ's allocator during the connect-retry window, stalling the game thread → C# tray balloons queue → UI appears hung. Hotfix: gate `PollOkDisplayToShm(loginShm)` on `loginShm->autoLoginActive` in `LoginStateMachine::Tick()`. Probe now only fires during the AutoLoginManager active window (matches PATH B retry use-case; bare launches and post-login idle stay zero-cost). Native DLL rebuilt + redeployed to `C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-di8.dll` at 00:31. The 3 consecutive smoke failures (00:08 gotquiz, 00:23 both, 00:26 both — clean-typed 7 chars then no server-advance signal) are SEPARATE from the hang and most likely Dalaya IP rate-limit after the first failure cascade; that requires waiting 15-30 min and a single-client retry to confirm.

## v3.17.0 — Connection-retry resilience: fast-fail probe + bounded retry loop + state-aware modal dismiss (2026-05-14)

Five connected fixes to the autologin retry subsystem. Addresses the user-stated symptom *"i have not seen it retry typing a password if the first try only typed 4 chars or was bad due to user input accident"* plus a real safety hazard caught from log evidence (2026-05-10 incident at 08:40-08:41: both `gotquiz` + `gotquiz1` EQ processes died during/after retry — root-caused to blind modal-dismiss Enter firing into the wrong EQ screen).

**Behavior changes:**

- **Post-BURST-2 fast-failure probe (`Launch.PostBurst2QuickFailCheckMs`, default 10000):** After BURST 2 fires, poll for 10s for evidence EQ has advanced past login (gameState change or window-rect size change). If no signal, the 90s screen-transition wait is short-circuited to 5s and the retry loop fires within ~15s instead of ~120s. Set to 0 to disable + restore legacy 90s-only behavior.
- **Bounded retry loop (`Launch.ConnectRetryCount`, default 1, range [0, 5]):** Replaces the v3.15.x one-shot recovery block with a `while`-loop that handles N retries before surfacing failure. Default 1 = matches v3.15.x semantics; 0 disables retry entirely. Mirrors `MQ2AutoLogin.ini`'s `ConnectRetries=N` parity.
- **State-aware modal dismiss:** Before pressing Enter to dismiss the assumed stale-session modal, read `gameState` via the SHM bridge. If `gameState > 1` (advanced past login → on server-select / charselect-load / EULA / etc.), SKIP the Enter — per `memory/feedback_eqswitch_no_yesno_in_patchme.md`, blind Enter into Dalaya's EULA screen defaults to DECLINE which closes the game. The 2026-05-10 incident is exactly this failure mode.
- **Cancellable stale-session wait (`Launch.StaleSessionPollIntervalMs`, default 500):** Replaces `Thread.Sleep(30000)` with `CancellableSleepUntilProcessDies` — polls `Process.HasExited` every 500ms during the 30s recovery window. If EQ dies mid-sleep (gotquiz scenario), short-circuits the wait and aborts cleanly with an explicit user-facing message instead of slogging through the full 30s and then noticing the corpse.
- **Retry parity with primary BURST 1:** Retry now calls `RunCredentialEntry(loginShm: null, ...)` instead of inlining its own keystroke loop. Inherits future improvements to BURST 1 typing (timing, PRIMER backspace, etc.) automatically. The `loginShm: null` parameter skips the Combo G warmup ritual on retry (primary already attempted it; retry leans on the empirically-load-bearing keystroke path per the v3.15.13 BURST 1 comment block).

**Diagnostic improvements:**

- `CombinedTypeString` now returns `TypingResult(Typed, Skipped, Expected)`. Callers in BURST 1 (username + password) feed `LogTypingValidation` which logs ERROR if typing was incomplete (mid-flight exception) and WARN if any chars were layout-skipped (user-action item: switch keyboard layout or pick an ASCII password). Catches layout-mismatch passwords that previously failed silently.

**Known deferred (out of v3.17 scope; tagged for v3.18+):**

- **Native `OK_Display` error-text probe + SHM v3 contract bump.** The right way to distinguish "truncated creds, server didn't accept" from "stale-session, server holding slot" is to read EQ's error-dialog text (`OK_Display` widget) — exactly what `src/plugins/autologin/StateMachine.cpp:292-374` (`ConnectConfirm` state) does. EQSwitch's `Native/login_state_machine.cpp:482-515` already has this infrastructure (`g_pOkDisplay` / `g_pOkButton` resolution + error-message string matching with fatal/recoverable classification) but it isn't currently wired through SHM to the C# `AutoLoginManager`. Doing so safely requires bumping `LOGIN_SHM_VERSION` (currently 2) to 3 with backward-compatible append of an `errorMessage[256]` poll-tick field, which touches the SHM contract v3.16.0 just shipped a couple hours ago — too risky for the same session. Filed for v3.18.0. With `OK_Display` text in C#, the retry path could distinguish hard-stop errors (`"password were not valid"`, `"Invalid Password"`, `"You need to enter a username and password to login"`) from recoverable transient errors and tune the recovery wait accordingly (1s for "truncated", 30s for "stale-session").
- **Architectural state machine.** The current EQSwitch login flow is a sequence of bursts + waits. MQ2's `StateMachine.cpp` has 12 explicit states (Wait, SplashScreen, Connect, ConnectConfirm, ServerSelect, ServerSelectConfirm, ServerSelectKick, ServerSelectDown, CharacterSelect, CharacterSelectWait, InGame, InGameCamping). A full rewrite toward this model would gain robustness in every transition but is multi-session work — out of v3.17 scope.

**Files changed:**

- `Config/AppConfig.cs` — added `StaleSessionPollIntervalMs`, `ConnectRetryCount`, `PostBurst2QuickFailCheckMs` with clamps.
- `Core/AutoLoginManager.cs` — added `TypingResult` record struct, `LogTypingValidation`, `CancellableSleepUntilProcessDies`, `PollForLoginAdvance` helpers; refactored retry block to bounded loop with state-aware dismiss + cancellable sleep + `RunCredentialEntry` parity call; `CombinedTypeString` returns `TypingResult`; primary BURST 1 callers validate via `LogTypingValidation`.
- `EQSwitch.csproj` — version bump 3.16.0 → 3.17.0.

**Ship gate:** Per `memory/feedback_dual_box_test_before_autologin_tag.md`, autologin changes require a manual dual-box smoke before tagging. NOT auto-tagged or auto-deployed. Build verified clean (Debug + Release, 0/0 warnings/errors); end-to-end retry behavior is the user's dual-box smoke responsibility.

**R1 verifier-pair sweep findings + R2 fixes (in-session, 2026-05-14):** Six parallel agents (T1/T2/T3 × Sonnet+Opus) flagged a mix of CRITICAL gaps and doc-drift. Fixed in R2 before this CHANGELOG entry settled:

- **Snapshot all retry-tunables at `RunLoginSequence` entry.** Previously read `_config.Launch.StaleSessionWaitMs` / `PostBurst2QuickFailCheckMs` / `ConnectRetryCount` / `Burst2*` live inside the retry loop — Settings change during a 30s recovery sleep would race the loop. Now snapshotted alongside `loginScreenDelayMs` / `warmupDwellMs` / `eqPath`, matching the existing pattern in lines 678-682.
- **`IsIconic` guard in `PollForLoginAdvance`.** A user-minimize during the 10s probe returns icon-strip dimensions that registered as "rect size change → advance detected" → no retry on real failure. Now gated on `!IsIconic(hwnd)` before checking rect change.
- **`GetWindowRect` initial-call return-value check.** If hwnd is stale at probe entry, `initialRect` was zeroed and any subsequent valid sample false-positived "rect change → advance." Now bails out of probe (returns true → fall through to legacy 90s wait) when initial GetWindowRect fails.
- **`ReadGameState` -1 sentinel safety.** Retry modal-dismiss gate was `currentState <= 1` which admitted -1 (SHM-not-mapped sentinel) as a "press Enter" signal. Tightened to `currentState >= 0 && currentState <= 1`.
- **Dalaya gameState semantics documentation.** Per `Native/login_state_machine.cpp:30-36`: "Known from DLL log: login screen = 0, charselect = ?, ingame = ?" — gameState alone may not bump on login→charselect transition. Updated `PollForLoginAdvance` doc-comment to call rect-change the LOAD-BEARING signal and gameState SUPPLEMENTAL. Same nuance added inline to retry-loop modal-dismiss gate documentation.
- **Pre-typing field-clear on retry.** If primary's Combo G (v3.16.0 ScreenMode swap path) successfully wrote credentials into `CEditBaseWnd::InputText+0x1A8`, the password field already holds chars at retry entry. `RunCredentialEntry`'s PRIMER backspace clears only ONE char — retype would concatenate to `<N-1 leftover><password>`. Now fires 16 Backspaces before the `RunCredentialEntry` call on the retry path (T2-Opus catch).
- **`Win32Exception` catch in `CancellableSleepUntilProcessDies` + `PollForLoginAdvance`.** `Process.GetProcessById` can throw `Win32Exception` on access-denied (PID recycled to protected process). Now caught alongside `ArgumentException` / `InvalidOperationException`.
- **Removed dead-code `quickFailDetected = retryQuickFail` assignment + lying "propagate into next iteration" comment.** No later read consumed the value; each iteration computes `retryQuickFail` fresh. (T1-Opus catch.)
- **Doc-path drift.** `MQ2AutoLogin/StateMachine.cpp` was the wrong path; the actual MQ2 source layout is `src/plugins/autologin/StateMachine.cpp`. Corrected in this CHANGELOG and the memory note. (T1-Opus catch.)

**Verifier findings NOT addressed in v3.17.0 (deferred to v3.17.1+):**

- **No `CancellationToken` for UI-side abort during 30s recovery wait.** Plumbing a cancel-token from TrayManager → `AutoLoginManager.RunLoginSequence` → `CancellableSleepUntilProcessDies` is architectural work; the existing user-facing "Cancel Login" UX doesn't exist anyway. Filed for v3.17.1.
- **`Account` is passed by reference into retry — mid-retry Settings edit of `account.Username` would race.** Password is value-captured (`capturedPassword` at line 447). Username + UseLoginFlag are not. Probability is low (user changing account during a 90s-then-retry window) but real. Filed for v3.17.1.
- **5000ms quickFail-shortened transition timeout edge case.** A genuinely-succeeding 4501-4999ms login is misclassified as timeout → unnecessary retry. Probability low (Dalaya login typically <3s or >>5s). Acceptable for v3.17.0; consider expanding window to 6000-7000ms in v3.17.1 if smoke shows false-retries.
- **Hardcoded 5000ms / 60000ms / 300ms retry-internal timing values.** Per existing codebase convention (v3.15.2-era), tunables get config knobs only when Nate empirically tunes them. Filed as future-work.
- **EULA-screen Enter→DECLINE hazard not fully eliminated.** EULA likely also has `gameState=0`, so the `currentState <= 1` gate doesn't distinguish login from EULA. The right fix is a native-side window-name probe (e.g., extending `kPromptWindows` lookup result through SHM to C#) — same SHM v3 bump that enables the OK_Display error-text probe. v3.17.0 reduces but does not eliminate this risk. Filed as v3.18.0 prerequisite.

**R3 verifier-pair sweep findings + R3 fixes (in-session, 2026-05-14):** A second 6-agent verifier sweep (3 topics × Sonnet+Opus) ran post-R2. Substantive findings addressed in R3:

- **`RunCredentialEntry` snapshot propagation (T2-Sonnet + T2-Opus convergent).** R2 snapshotted retry tunables at `RunLoginSequence` header, but `RunCredentialEntry` (called from BOTH primary and retry paths) still live-read `_config.Launch.Burst1ActivationSettleMs` / `Burst1PostSubmitMs` / `SkipNativeWarmup` — a Settings reload during a 30s recovery sleep would race them. R3 adds a per-call snapshot at the top of `RunCredentialEntry` so every invocation gets a consistent view.
- **UseLoginFlag=false dual-field clear (T2-Opus critical).** R2's 16x Backspace pre-typing field-clear assumed UseLoginFlag=true (Nate's setup — username auto-populated by LaunchManager via `eqlsPlayerData.ini`, only password field needs clearing). For UseLoginFlag=false users, BURST 1 types both username AND password, so both fields can have stale chars on retry. R3 adds a Tab + 16-more-Backspaces step gated on `!account.UseLoginFlag` so both fields get cleared in that case. UseLoginFlag=true path unchanged (still 16 Backspaces of password field only). Log line reports total Backspace count for visibility (16 or 32).
- **`Win32Exception` logging consistency (T3-Sonnet observability).** R2 added `catch (Win32Exception)` to both `CancellableSleepUntilProcessDies` and `PollForLoginAdvance` but the catches were silent — inconsistent with the explicit `FileLogger.Warn` for process-exit cases. R3 adds Warn log lines with `ex.Message` so PID-recycling-to-protected-process scenarios surface in eqswitch.log. Also added log lines to the `ArgumentException` / `InvalidOperationException` catches for full coverage.
- **Doc-math comment correction (T2-Opus + T2-Sonnet).** R2 comment said "16 is generously above any plausible password length up to `LOGIN_PASS_LEN/2` with margin" — `LOGIN_PASS_LEN=128` per `Native/login_shm.h:45`, so LOGIN_PASS_LEN/2 is 64, NOT 16. Math was wrong. R3 rewords: "16 Backspaces clears up to 16 chars from cursor-back. Passwords up to 16 chars are fully covered. Passwords above 16 chars where Combo G structural write succeeded would still concatenate on retry (filed as v3.17.1)."
- **CHANGELOG v3.16.0 historical line path drift (T1-Sonnet + T3-Opus).** R2 only updated the v3.17.0 prose; v3.16.0 historical section line 54 still referenced `MQ2AutoLogin/StateMachine.cpp:265`. R3 corrected to `src/plugins/autologin/StateMachine.cpp:265`. Zero runtime impact (historical text), but eliminates grep-confusion for future maintainers.
- **Memory file parity-status table accuracy (T3-Opus CRITICAL).** R2 memory file claimed `_.releases/eqswitch/VERSION = v3.16.0`. Actual content per `cat`: `v3.15.6`. Release folder lags commits — v3.16.0 shipped 2 hours ago but the releases path hasn't been bumped. R3 corrected the memory file: "binary at `_.releases/eqswitch/` is v3.15.6 (cat VERSION confirmed) — NOT bumped (releases folder lags deploys; both v3.16.0 and v3.17.0 are unreleased to that path)". Pre-ship parity flag (per `reference_pre_ship_parity_checks`) is real: the dual-box smoke gate must also update `_.releases/eqswitch/` to the corresponding tag.

**R3 hallucination caught (transparency note):** T3-Opus initially hypothesized `PHASE_ERROR=99` (from `LoginPhase` enum) could leak into `ReadGameState` and bypass the `> 0` safety. On verification, `gameState` and `phase` are distinct SHM fields (`OFF_GAMESTATE` vs `OFF_PHASE`) — phase enum cannot leak through `ReadGameState`. T3-Opus self-corrected and flagged it as the expected R3-tier hallucination per the verifier-loop-diminishing-returns rule. No code change needed.

**Final build status (post-R3):** Debug + Release builds successful, 0 warnings / 0 errors. Migration fixture suite (`_tests/migration/run_fixtures.sh`): 9 passed, 0 failed. No `dotnet test` test-project; retry-logic verification is the dual-box smoke gate's responsibility.

## v3.16.0 — ScreenMode swap during Combo G password write (2026-05-14)

Closes hypothesis B-1 from this session's MQ2 RoF2-emu autologin deep-dive: MQ2's `src/plugins/autologin/StateMachine.cpp:265` wraps each password write in a `ScreenMode = 3` swap that forces EQ into "fullscreen-UI-input" mode for the duration of the write, then restores. v3.15.13 shipped Combo G without this swap and the password CXStr write was reaching `CEditBaseWnd::InputText` at `+0x1A8` byte-perfectly but EQ's natural state-1 login UI was applying input filters that prevented the write from propagating to the LoginClient submit pipeline at Connect-click time.

### Single-box smoke (PID 27748)
40.3s wall-clock, EXIT 0 from `--test-autologin` runner, ZERO SEH faults across the native dispatch path. Password CXStr read-back length=7 first byte matches; eqmain.dll UNLOADED at end of run (proof EQ reached past charselect).

### Dual-box BACKGROUND smoke (PIDs 23520 + 33344)
Both clients independently completed autologin in parallel — the iter-12 failure mode (BACKGROUND-client SHM-keystroke drops under game-thread starvation) did NOT manifest. Both reached `ScreenMode = 2` (char-select / in-world) with zero SEH. Confirmed end-to-end (both clients in-world).

### What changed in Native/

**`Native/eqmain_cxstr.cpp` (the load-bearing diff):**

Added `RVA_GLOBAL_ScreenMode = 0x0091F3B8` constant. Source: `github.com/macroquest/eqlib` @ `emu` branch, `include/eqlib/offsets/eqgame.h:84` (`__ScreenMode_x = 0xD1F3B8` absolute at preferred ImageBase `0x400000`). Build-date match verified (`__ClientDate = 20130510u "May 10 2013"` matches Dalaya `eqgame.exe` byte-for-byte). RVA validated against running Dalaya across 4 screen states (`probe_screenmode.py` against PIDs at login + server-select + char-select + in-world; values seen `1 / 1 / 2 / 2` respectively; `3` is never natural — MQ2's deliberate fullscreen-UI-input mode, cross-confirmed via `MQ2HUD.cpp:617` HUDTYPE_FULLSCREEN gate + `MQ2FrameLimiter.cpp:271` frame-limiter branch on `*pScreenMode != 3`).

Refactored `WriteEditTextDirect` into a thin swap wrapper + a renamed-to-private `WriteEditTextDirectImpl` containing the original CXStr write body unchanged. Wrapper sequence:
1. `GetModuleHandleA(NULL) → eqgame.exe runtime base` (ASLR-aware — Dalaya rebases eqgame.exe at runtime, observed slide `+0x2E0000`)
2. Compute `pScreenMode = base + RVA_GLOBAL_ScreenMode`, SEH-wrapped DWORD read
3. Sanity gate `oldScreenMode <= 8` admits the empirical natural values (0 in BSS-uninit pre-EQ-init OR 1 in post-EQ-init) and rejects out-of-range garbage from a wrong RVA on hypothetical Dalaya patches
4. Write `*pScreenMode = 3` (the fullscreen-UI-input mode forcing)
5. Call `WriteEditTextDirectImpl(pEditWnd, text)` in `__try/__except` (catches any SEH unwind from the impl so the caller sees clean false instead of an exception propagating)
6. Restore `*pScreenMode = oldScreenMode` ONLY if pre-write value was `>=1` (no-restore policy when pre-write was 0 — lets EQ's natural init transition 0→1 race against our restore safely; restoring to 0 against an already-initialized EQ could leave it inappropriately in pre-init state)

Restore step is its own SEH-wrapped block — separate from the impl's, because MSVC C2702 forbids `__try/__except` inside `__finally` termination blocks. Catching the impl's SEH means the wrapper guarantees the restore runs on every exit path (normal return, false return, SEH unwind).

**`Native/eqmain_cxstr.h`:**

Corrected the stale "DORMANT" status block at the top of the header. The file has been in `build-di8-inject.sh`'s link line and `login_state_machine.cpp:375` has been actively calling `EQMainCXStr::WriteEditTextDirect(pPasswordWidget, g_password)` since iter-12 wired the call site — the dormant-claim was leftover documentation from the iter-11 era. Replaced with current-status block documenting the ScreenMode swap rationale + the maintenance implication that edits to `WriteEditTextDirect` now affect the live autologin flow.

**`Native/login_givetime_detour.cpp` (documentation-only):**

Corrected the misleading `pLoginController (ptr-to-ptr) 0x150174 (for Phase 4 — not used here)` comment that's been wrong for years. Per upstream emu-branch `eqlib/include/eqlib/offsets/eqmain.h:33`, the real `pinstLoginController` is at RVA `0x15015C`, and live probe against running Dalaya (`probe_login_globals.py`) confirms `*(eqmain+0x150174)` points to a heap object whose vtable[0] = `0x021D8938` (NOT an eqmain.dll vtable) — i.e., `0x150174` is some unrelated helper struct, not pLoginController. The detour's RUNTIME BEHAVIOR was correct regardless because `g_loginController` is populated from `thisPtr` of the GiveTime invocation (the real `this`), never from reading the global address directly — but a future maintainer following the comment would have hit garbage. New comment block documents the authoritative RVAs:
- `pinstLoginController = eqmain+0x15015C` (NULL at login — LoginController constructed lazily)
- `pinstLoginServerAPI = eqmain+0x150164` (populated at login — unblocks Diff 4 follow-up)
- `pinstCLoginViewManager = eqmain+0x150170` (populated at login)
- `pinstLoginClient = eqmain+0x15016C` (populated at login)
- `0x150174` = unrelated helper struct (vtable not in eqmain — confirmed live probe)
- `LoginServerAPI::JoinServer = eqmain+0x13C30` (newly pinned, available for Diff 4 follow-up)

### What's NOT in this release

- **No change to the C# autologin orchestrator.** `AutoLoginManager` doesn't know about ScreenMode — the swap is purely DLL-internal around the CXStr write. No new SHM fields, no new IPC commands, no new state machine phases.
- **No fix for the pre-existing LOGIN_ConnectButton heap-scan brittleness.** `mq2_bridge.cpp`'s heap-cross-ref scan still finds `CXMLDataPtr` definitions instead of live `CButtonWnd` instances on most launches, falls back to SHM keystroke for the Enter submit, gets there in the end. Reliability is unchanged from v3.15.13. The proper structural fix (use `pLoginViewManager + 0x14` anchor for ConnectWnd then `GetChildItem` for the button) is documented as follow-up Diff 2/3 in `_.eqswitch-re/mq2-autologin-eqswitch-diff.md` but deferred because keystroke fallback works.
- **No tag of the broader `_.eqswitch-re/` corpus.** The walkthrough + diff docs + probe scripts are local-only (`_.eqswitch-re/` is under the `_.claude/**` local-only gate per `~/.claude/CLAUDE.md`). They're available to future Claude sessions via Syncthing but not on GitHub.

### Provenance trail

- Crash recovery for Opus session `23cf1bff` (2026-05-14 04:05-04:21 MDT, $58.57 stream-idle-timeout) — the original deep-dive that surfaced hypothesis B-1 + this session's empirical follow-up live-probed against running Dalaya across all 4 screen states.
- 3 rounds of pair-by-topic verifier sweeps (Sonnet + Opus on Diff-clean, Gap-audit, Code-review topics) caught: stale `pinstLoginServerAPI` value contradicting itself across sections; sanity gate `<=16` admitting BSS-zero; `__except` inside `__finally` MSVC syntax error caught at compile time after the third sweep; misleading `pLoginController = 0x150174` shipping comment.
- 7 reusable RE probe scripts now in `_.eqswitch-re/probe_*.py` for future Dalaya runtime verification.

## v3.15.11 — Native two-tier throttle + Dalaya SHM Enter World skip (2026-05-09)

Closes the two "deferred to future session" follow-ups left at the bottom of the v3.15.10 entry. Both attack the remaining ~5–7s of charselect dwell + SHM enter-world retry waste; combined target is ~1–1.5s charselect dwell (was 4.4s/3.8s) and ~55s end-to-end SendKeys → "logged in" (was 61.7s).

### Native two-tier throttle (Target 1 in the v3.15.11 work brief)

`MQ2BridgePollTick` in `Native/eqswitch-di8.cpp` previously gated the entire poll body on a single 500ms throttle (`if (!bypassThrottle && now - lastPoll < 500) return;`). Result: when C# AutoLoginManager fired a SetCurSel ack request or an Enter World request, the bridge waited an avg 250ms (worst-case 500ms) before looking at the SHM seq counters. v3.15.10 logs measured the slot-req → "Entering world..." span at 426ms — almost entirely throttle wait.

New gate splits into two tiers:

- **Full poll** (heap scans, latch counter, `kPromptWindows` walk, `LoginStateMachine` tick) keeps the 500ms cadence via a renamed `lastFullPoll` static. The 30-poll latch-clear threshold (15s) and the 20-poll standalone-scan delay (10s) and the 10-poll lazy-MQ2-init retry (5s) ALL preserve their 500ms-tick assumption.
- **Fast path** runs when SHM has a pending request (`requestSeq != ackSeq` OR `enterWorldReq != enterWorldAck`) but the full-poll throttle hasn't expired. It calls a new `MQ2Bridge::PollRequestsOnly(shm)` entry that runs ONLY the two cheap request handlers — no char-data reads, no latch counter, no transition logging, no heap walks. The handlers ack within ~16ms of the request landing in SHM (one ActivateThread/TIMERPROC tick).

The handler bodies were factored into file-scope static helpers (`HandleEnterWorldRequest`, `HandleSelectionRequest`) shared between `MQ2Bridge::Poll` (full) and `MQ2Bridge::PollRequestsOnly` (fast). Selection-handler call site moved up — it only needs `shm->charCount` published by a prior full poll, so it can run before the heavy heap-scan paths.

A new invariant pin at `mq2_bridge.cpp` documents that `g_consecutiveNullPolls` and `g_standaloneDelay` MUST stay in `MQ2Bridge::Poll` only — moving them into the fast path would tick at ~16ms cadence and clear the latch in ~480ms instead of ~15s. If anyone needs the latch logic on the fast path in the future, convert to wall-clock timestamps; do NOT relocate the increment.

SHM struct unchanged — same v3 layout. No bridge or AutoLoginManager protocol change required.

### Skip SHM Enter World on Dalaya (Target 2 Option A in the v3.15.11 work brief)

Empirical evidence from every dual-box run up to v3.15.10: the SHM `RequestEnterWorld` path on Dalaya returns `result=-1` (button not found) on all 4 attempts, every time. `CLW_EnterWorldButton` isn't in the CXWnd tree by the time charselect-ready is signaled. PulseKey3D fallback then fires and works every time, costing ~2-2.5s of failed retry budget for no upside.

New `Launch.SkipShmEnterWorldOnDalaya` config knob (default true) makes Dalaya skip the SHM Enter World path entirely and go straight to PulseKey3D. Other servers (`account.Server` case-insensitively != "Dalaya") still use the SHM-primary path. Settings UI is unchanged — Server dropdown is locked to "Dalaya" since v3.14.7, so effectively all v3.15.11 users get the fast path; the flag is the power-user opt-out via direct `eqswitch-config.json` edit.

The structural fix (Option B in the work brief — bridge writes a `buttonReady` SHM flag, C# polls it with a hard timeout) is filed as a follow-up. Option A is the data-driven Dalaya shortcut; Option B is the portable fix for hypothetical other-server use.

### What's NOT in this release

- **No SHM struct version bump.** Both targets ride on existing v3 fields. Bare-launch + earlier-DLL compatibility is preserved.
- **No `kPromptWindows` walk on the fast path.** Promo dismiss timing is unchanged (still 500ms cadence under the full-poll path).
- **No widget-cache invalidation change.** `g_consecutiveNullPolls` semantics preserved verbatim.
- **No new dependency on bridge initialization for the fast path.** If MQ2 isn't yet initialized when a request lands, the fast path defers (returns without touching SHM); the next full poll handles bridge init and the request together.

## v3.15.10 — Account.Notes round-trip + ConfigManager Save/Load lock symmetry (2026-05-09)

Two pre-existing v3.15.7 follow-ups that the verifier swarm flagged but never landed, plus a third site of the same lock-symmetry issue caught during v3.15.10's own verification pass + a stale floor-clamp test assertion.

### Account.Notes round-trip bug

`SettingsForm.BuildAppConfig` and the form's initial Account-list snapshot both clone Account objects field-by-field (`new Account { Name = a.Name, Username = a.Username, ... }`) — but neither clone copied `Notes`. Effect: any user-set Notes on an Account would be silently cleared on every Settings → Apply. Fixed both clone sites by adding `Notes = a.Notes`.

The same field-by-field clone pattern also exists for Character objects on the same form, but those clones do copy `Notes` correctly — verified.

### ConfigManager Save/Load `_saveLock` symmetry

`Save()` AND `Load()` (the migration-persist branch — `if (didMigrate || validateMutated)`) both wrote `_pendingSave = config` outside `_saveLock`, while `FlushSave` and `SaveImmediate` both hold the lock for read+write. The Save-path miss was the original verifier-flagged issue; the Load-path miss surfaced during v3.15.10's own verification pass — same root cause (the cross-thread invariant "every `_pendingSave` write happens under `_saveLock`" was only partially enforced). Lock added around both staged-pointer writes for explicit cross-thread contract. `Load()` is single-threaded at startup so the lock is uncontended in practice, but breaking the invariant in one site silently invalidates it everywhere.

### Stale `BridgeInitWaitMs` floor test

`AppConfigValidateTests.cs:185` asserted post-clamp floor of 500 for `Launch.BridgeInitWaitMs`. v3.15.8 lowered the production clamp floor to 0; the test was not updated, so it would fail in Debug builds (Release excludes `*Tests.cs` per the csproj). Updated the expected value to 0 and added a comment cross-referencing v3.15.8's rationale.

### Verifier R2/R3 follow-ups (round 2 of the v3.15.10 verifier sweep)

Four pre-existing items the R2 verifier swarm flagged that this round closes:

**`ConfigManager.SaveImmediate` no longer clobbers a queued UI-thread `Save()`.** Pre-fix, `SaveImmediate` did `_pendingSave = config; var toWrite = _pendingSave; _pendingSave = null;` inside the lock — atomically overwriting any UI-queued config and then nulling it, so the timer's later `FlushSave` found nothing. The block comment promised "install our config and let UI saves re-flush" but the code never honoured it. Now: only clear `_pendingSave` when it's null OR the same reference; otherwise leave the queued config alone for the timer to flush. In current callers `config` is always the live `_config` reference, so this is forward-compatibility hardening; future callers that pass a different config object won't lose user state.

**Three orphan config fields removed: `ShowTooltipErrors`, `MinimizeToTray`, `CtrlHoverHelp`.** Verifier flagged these as "silently zeroed on every Apply" because `SettingsForm.BuildAppConfig` didn't pass them through. Investigation: all three were declared in `AppConfig` but had ZERO consumer code reading them. `ShowTooltipErrors` and `MinimizeToTray` were round-tripped via `TrayManager.ReloadConfig` but the live values were never read. `CtrlHoverHelp` was deprecated in an older release (search this file for "Removed CtrlHoverHelp" in the v3.0.0-era Settings UI cleanup) but the field declaration stayed behind. All three deleted from `AppConfig.cs` plus the two `ReloadConfig` copy lines. Existing user configs with these JSON keys deserialize cleanly — `System.Text.Json` ignores unknown properties by default. Net: no behaviour change (nothing read the values), less dead surface area for future verifier confusion.

**`AppConfigValidateTests` ceiling coverage added.** Case 7 covered floor clamps for the LaunchConfig timing knobs but had ZERO ceiling assertions. New Case 10 covers ceilings for `LoginScreenDelayMs`, `WarmupDwellMs`, all 10 `LaunchConfig` timing knobs, plus `NumClients`/`LaunchDelayMs`/`FixDelayMs`. Also adds floor coverage for the two top-level knobs (`LoginScreenDelayMs`, `WarmupDwellMs`) that Case 7 missed. Out-of-range hand-edits (e.g. `999_999`) now surface as test failures instead of producing absurd timeouts in production. Test count message updated 9 → 10.

**Test CLI flag handling clarified for Release builds.** `--test-character-selector`, `--test-config-validate`, `--test-key-input-writer`, `--test-shm-layout`, `--test-charselect-reader` all live inside an `#if DEBUG` block in `Program.cs` (lines ~107-203) — Release builds strip these handlers entirely. Previously, passing one of these flags to a Release `EQSwitch.exe` silently fell through to the normal tray-app launch (mutex check, FirstRunDialog, TrayManager) — the user got an unexpected tray icon instead of a test runner. New `#if !DEBUG` guard intercepts these stripped flags and exits with code 3 so a calling shell can distinguish "flag rejected" from "app launched normally".

`--test-autologin`, `--test-migrate`, and `--test-split` are NOT stripped — they live OUTSIDE `#if DEBUG` because they're file-I/O utilities (write `.migrated.json` / `.split.json` outputs, drive a real EQ login) rather than Console-emitting unit tests. They have their own early-return handlers above the guard, so the guard never sees them. The guard's "skip if --test-autologin" check is belt-and-suspenders only.

### Native investigation (deferred to future session)

The remaining ~2s charselect dwell on Nate's setup is the bridge anchor scan in `eqswitch-di8.cpp` running at a 500ms throttle (`if (!bypassThrottle && now - lastPoll < 500) return;` at line 281). At charselect, both selection-ack and enter-world request handlers are gated by this throttle, paying ~250ms average latency per request.

A surgical fix exists — bypass throttle when `requestSeq != ackSeq` or `enterWorldReq != enterWorldAck` — but several throttle-period-counted state variables (`g_consecutiveNullPolls` at mq2_bridge.cpp:3554 hardcodes `*500` for its 15s timeout) would need refactoring to wall-clock time. Estimated savings ~1.5s on the selection-ack path. Not in v3.15.10 — needs careful native build + dual-box regression testing.

The SHM enter-world `result=-1` retry loop is a separate concern: the CLW_EnterWorldButton truly doesn't exist for several seconds after charselect-ready, so no amount of polling will find it. PulseKey3D fallback works because it goes through EQ's input handler, not the button object. Possible future cleanup: skip SHM attempts entirely on Dalaya, or have the bridge expose a `buttonReady` flag in SHM and have C# wait for it before firing.

## v3.15.9 — Charselect dwell, round 2: ack granularity + enter-world retry (2026-05-09)

v3.15.8's `BridgeInitWaitMs` cut was a wash on the live dual-box test — Nate's setup has the bridge anchor scan needing ~2s after charselect-ready, so the 4×500ms wait-loop iterations consume the same budget as the upfront Sleep did. v3.15.9 attacks the next two cost centers in the charselect → in-game span:

### Selection-ack poll granularity (200ms → 50ms)

`AutoLoginManager.HandleCharSelectViaShm` polled the SetCurSel ack flag every 200ms, with a 10s cap. The DLL writes the ack flag during EQ's game-thread tick (~16ms), so the 200ms granularity meant we noticed the ack up to 200ms after it actually fired. Cut to 50ms (cap unchanged at 10s = 200×50ms). Realistic savings: ~150ms per autologin.

Post-ack settle Sleep also tightened: 200ms → 100ms. The ack already implies TIMERPROC has run, so 100ms is plenty.

### Enter-world retry path (4 attempts × 500ms — was 2 × 2000ms — and gated)

The SHM `RequestEnterWorld` path retries when `result == -1` ("button doesn't exist yet" — typically because the CLW_EnterWorldButton hasn't entered the CXWnd tree on the first attempt). Three issues fixed in one pass:

- **Bug**: `Thread.Sleep(2000)` fired *unconditionally at end of every iteration*, including the last — meaning we slept 2s before falling through to the PulseKey3D fallback. v3.15.8 log confirms it: attempt 2 ended at `50.789`, fallback fired at `52.790`, exactly 2000ms of waste. Sleep is now gated behind `attempt < kMaxEnterWorldAttempts - 1`.
- **Tuning**: 2000ms between retries was too long given the button-render race typically resolves within hundreds of ms. Cut to 500ms.
- **Retry budget**: bumped 2 → 4 attempts. Inter-retry Sleep is gated, so 4 attempts produces 3 sleeps = 1500ms inter-retry budget (down from 2×2000ms = 4000ms pre-fix, because the prior code wasted a 2000ms sleep AFTER the last attempt). Net: 4× the polls AND ~2.5s less wall-clock spent in the SHM-fail path before falling through to PulseKey3D.

Inner ack-wait inside each enter-world attempt also tightened: 25×200ms → 100×50ms (cap unchanged at 5s).

### Predicted savings on Nate's failure-path run

- Selection-ack granularity: ~150ms
- Post-ack settle: 100ms
- Enter-world inner ack granularity: ~150ms × N attempts
- Last-attempt Sleep gate: ~2000ms (eliminates the wasted final sleep)
- Retry sleep cut: ~1500ms × (N-1) cycles when retries happen

Combined: ~3.5–4s on the v3.15.8 reproduction path. Bridge anchor scan time (~2s) remains unaddressed — that's native-side work in `eqswitch-di8.cpp`.

## v3.15.8 — Trim charselect → Enter World dwell (2026-05-09)

Cuts ~2s from the autologin path between `WaitForScreenTransition` reporting charselect-ready and the first MQ2 bridge poll for the character list. The `BridgeInitWaitMs` setting predates the v3.15.x latch+count gate in the char-list wait loop; once that gate landed, the unconditional pre-poll Sleep became a vestigial settle pause on top of an already-structural readiness check. The wait loop's first iteration polls without delay and its inter-poll 500ms sleep absorbs any genuine bridge lag, so the prior 2000ms upfront pad was paying twice.

### Fix

- **`Launch.BridgeInitWaitMs` default 2000 → 1ms** in `Config/AppConfig.cs`. The char-list wait loop in `AutoLoginManager.HandleCharSelectViaShm` (60×500ms cap, latch + ReadCharCount) is the actual bridge-readiness gate; this Sleep is now effectively a yield. Failure path is unchanged — if bridge isn't published on the first poll, the loop sleeps 500ms and retries, identical to the prior behavior.
- **Validation floor lowered 500 → 0**, ceiling unchanged at 30000. Users who want a longer pre-poll buffer can still tune up.
- **Stale XML doc rewritten** — the prior comment described this as a "wait after process resume for the bridge to initialize," but the call site is post-`WaitForScreenTransition`, where the EQ process has been running 30–90s and the bridge has long since initialized. Doc now reflects the actual semantics.

### Measured impact

- Per-box savings: ~2s in the success path (bridge already up at charselect-ready, which is the common case). Failure path: identical.
- Wall-clock: the unified abort path (line 1125 — "MQ2 bridge not ready after 30s") and the single-char structural fallback (line 974, `wait >= 20`) both remain unchanged — those failure modes already give the bridge ample time.

## v3.15.7 — Autologin / native dismiss interlock (2026-05-09)

Hotfix for a regression introduced by v3.15.5. The `kPromptWindows[]` pre-login auto-dismiss machinery (added in v3.15.5 to give bare Launch Client an EULA + main-menu auto-click) was gated only on `gameState != 5` (not in-game). That gate was correct for bare launch — gameState transitions to 5 once in-world — but for autologin teams the same gate left the dismiss machinery iterating *every native poll tick* from gameState=0 (login screen) through server-select and char-select-load, while AutoLoginManager's BURST keystroke flow was driving the same UI. At server-select / charselect-load, transient widget matches (`news`, stale `main` slipping past `IsCXWndVisible`) fired `WndNotification(XWM_LCLICK)` and the EQ process self-exited within ~7 seconds of BURST 2. Reproduced 4 of 4 attempts on team1 (2026-05-09 09:53–09:56).

### Fix

- **New `autoLoginActive` SHM field** appended to `LoginShm` (offset 1340, struct now 1344 bytes, version bumped 1→2). C# `AutoLoginManager` writes `1` immediately after `LoginShmWriter.Open(pid)` succeeds and clears to `0` in the cleanup `finally` block before `Close`. Backward-compatible append — pre-v2 native readers see all prior fields unchanged.
- **Native gate** in `eqswitch-di8.cpp`'s `MQ2BridgePollTick`: when `g_loginShm->autoLoginActive != 0`, the entire `kPromptWindows[]` iteration is skipped for this PID (rest of the poll tick — gameState updates, char-data publishing — still runs). Bare-launch path is unchanged: `autoLoginActive` defaults to 0, so EULA + main-menu auto-click continues to fire as designed.
- **`Thread.MemoryBarrier()` in `SetAutoLoginActive`** for symmetry with `SendLoginCommand` — defends against future memory-model regressions on non-x64 targets and removes dependence on MSVC's `/volatile:ms` default for the native reader.

### Internal

- New public `LoginShmWriter.SetAutoLoginActive(int pid, bool active)` API.
- `g_loginShm->autoLoginActive` declared `volatile uint32_t` in `login_shm.h:99` so the per-tick poll-loop read isn't hoisted by the optimizer.

### Known limitation

- If `EQSwitch.exe` is force-killed mid-autologin without the cleanup `finally` running, the SHM section remains alive in the injected DLL's process with `autoLoginActive=1` until the EQ process exits. Result: kPromptWindows dismiss is suppressed for that orphaned EQ session — a manual EULA click is required if EQ stays at the EULA screen. Rare in practice; addressed in a follow-up if it surfaces.

### Also in this release — per-account login-status flag

- **Settings → Accounts grid Flag column** now shows the last autologin outcome: ✓ (charselect reached), ✗ (`AutoLoginManager`-owned timeout — bad password / server / network), or — (untried). Tooltip on hover shows the timestamp. Persisted in `eqswitch-config.json` as new `Account.LastLoginResult` (string) + `Account.LastLoginAt` (DateTime?) properties; pre-feature configs deserialize as untried (default `""` / `null`).
- **`AccountEditDialog`** preserves the flag on cosmetic edits (Notes/Server) and resets to untried on a password change (the prior outcome no longer reflects the new password). Detection uses DPAPI ciphertext inequality.
- **Race-fix at `SettingsForm.BuildAppConfig`**: when an autologin fires from the tray menu while Settings is open, the live `LastLoginResult` is preferred over the staged copy (matched on `EncryptedPassword` equality), so the in-flight write isn't clobbered on Save.
- **`ConfigManager.SaveImmediate(config)`** added for thread-safe synchronous saves from background threads (autologin status writes). The existing `Save()` path still uses the WinForms-Timer coalescing for UI-thread callers.
- **Process-death paths deliberately do NOT mark `"fail"`** — only AutoLoginManager-owned timeouts. EQ crashes / window-loss leave the prior outcome unchanged.

## v3.15.6 — Native log redaction parity (2026-05-08)

Hotfix on the v3.15.5 baseline. Closes an asymmetric credential-half leak the v3.15.5 redaction work missed: while `LoginShmWriter.SendLoginCommand` (C# managed log) was scrubbed to `user=<redacted>`, the parallel native-side log line at `login_state_machine.cpp:256` still wrote `user='<plaintext_username>'` to the per-PID DI8 log file (`eqswitch-dinput8-<PID>.log` in the EQ install dir). Same credential half (the SoD account username), different log file. v3.15.6 redacts the native line for parity.

### Security

- **`Native/login_state_machine.cpp` LOGIN-command log line**: `user='%s'` → `user=<redacted>`. The `%s` arg removed from the format string. Server and character names remain logged unredacted (non-secret, useful for diagnostics).

### Internal

- **Stale TODO comment removed** from `eqswitch-di8.cpp` referencing a "Login Accounts Option" window between EULA and login screen — that window is `main` (Dalaya's post-EULA login-options menu) and v3.15.5 already handles it. Comment updated to reflect verified end-to-end behavior.

## v3.15.5 — Pre-login modal auto-dismiss + log redaction (2026-05-08)

This release closes the gap between **bare Launch Client** and **Launch Team / autologin** on Shards of Dalaya. Previously, left-clicking the tray icon (or right-click → Launch Client) launched eqgame.exe and stopped at Dalaya's pre-login prompt chain (EULA → main menu); the user had to manually click ACCEPT and then LOGIN before reaching the credentials screen. Autologin teams skipped these incidentally because BURST 1's Enter keystroke happened to dismiss them. v3.15.5 makes the bare path skip them too — directly, via widget-click — landing on the login screen with the pre-filled username, ready for password entry.

### New

- **Native pre-login modal auto-dismiss.** `eqswitch-di8.dll`'s polling tick now iterates a small list of pre-login modals (`EulaWindow → ACCEPT`, `main → LOGIN`, plus `seizurewarning` and `news` defensively) and dispatches `WndNotification(XWM_LCLICK)` directly on each modal's accept-equivalent button when it's visible. SIDL widget names ported from MQ2's `MQ2AutoLogin.cpp:1199-1206` with Dalaya-specific visible-label fallbacks (Dalaya widgets store the visible button label as the first scannable CXStr; the SIDL identifier lives at a higher offset our heuristic doesn't reach). Two gates prevent unintended clicks: `gameState != 5` (not in-game) and `IsCXWndVisible(pScreen)` (window actually shown — Dalaya keeps dismissed prompts in the live tree with `dShow=0`). Net effect: bare Launch Client lands on the login screen ~1s after the pre-login chain renders, with zero clicks.
- **Why widget-click instead of keystroke**: empirically, Dalaya's EULA defaults Enter focus to **DECLINE**, not ACCEPT. A VK_RETURN injection via DI8 SHM closed the game on every attempt. Direct widget-click via `WndNotification` targets ACCEPT explicitly, regardless of focus.
- **`OrderWindow` and `OrderExpansionWindow` intentionally OMITTED** from the dismiss list (despite being in MQ2's canonical set) — auto-clicking DECLINE on a future Dalaya-repurposed `OrderWindow` (e.g. server-pushed motd) would silently dismiss content the user wanted to see. Re-add only after confirming the SIDL name is exclusive to dismissable retail prompts.

### Security / Privacy

- **Command-line log redaction**: `FileLogger.RedactLogin` strips `/login:VALUE` to `/login:<redacted>` before any log write. Both `LaunchManager` and `AutoLoginManager` route their pre-CreateProcessA log line through it. Bare `/login:` (no value) is preserved verbatim for forensics — distinguishes credentialed launches from bare ones.
- **`LoginShmWriter.SendLoginCommand` log line** changed from `(user='{username}', ...)` to `(user=<redacted>, ...)` — closes the same credential-half leak the v3.15.2 password-length redaction pattern was built for. Server and character names remain logged unredacted (non-secret).

### Internal

- **`AutoLoginManager` `/login:` duplication guard**: when `_config.Launch.Arguments` already contains `/login:`, the autologin path skips its own `/login:USERNAME` append to avoid a malformed double-flag command line.
- **`AutoLoginManager._config.Launch.Arguments` null-coalesced** to `string.Empty`. Defensive — was unguarded.

### Notes

- **Launch Team / autologin behavior unchanged.** The native dismiss loop is gated behind `IsCXWndVisible` so it doesn't fire on cached-but-hidden widgets — autologin's BURST 1 keystroke window is therefore unaffected. Sanity-tested 2026-05-08.
- **`patchme` and `/login:` flags do NOT skip Dalaya's EULA / main screens.** Both are server-side modals EQ shows regardless of command-line args. The dismiss path is widget-click, not flag-based.

## v3.15.4 — Win11 tray-icon auto-show + zombie cleanup (2026-05-07)

This is a quality release on the v3.15.3 baseline. Autologin behavior is unchanged.

### New
- **Tray icon now appears in the taskbar by default on Windows 11.** Previously, every fresh install (or every WinGet upgrade that landed in a new versioned dir) defaulted to hidden-in-overflow until you manually toggled "Show icon in taskbar" under Settings → Personalization → Taskbar → Other system tray icons. EQSwitch now writes the per-icon `IsPromoted=1` flag automatically on first launch and after Explorer restarts, so the icon is visible from the moment the app starts. If you previously hid it deliberately (`IsPromoted=0`), that choice is respected — we only promote when the value is missing or already `1`.
- **Cleanup of stale tray-icon entries from prior versions.** Each WinGet upgrade and each .NET single-file extraction left behind a registry subkey under `HKCU\Control Panel\NotifyIconSettings` pointing to a now-deleted install path; over time these accumulated as duplicate "EQSwitch" entries in the Settings list. On first launch of v3.15.4, any subkey whose `ExecutablePath` basename matches `EQSwitch.exe` AND points to a path that no longer exists is reaped. Conservative — never touches sparse/orphan subkeys, never touches other apps' subkeys, never touches your currently-running install, and the auto-login state machine is fully isolated from the promoter.

### Notes
- These changes are no-ops on Windows 10 and Server SKUs (the `NotifyIconSettings` registry schema is Win11 22H2+ only). The build-version guard short-circuits before any registry access.
- All registry interaction is wrapped in try/catch and logged through `FileLogger.Info` / `FileLogger.Warn` (default log path `%LOCALAPPDATA%\EQSwitch\Logs\eqswitch.log`). A schema change in a future Windows build silently no-ops rather than crashing the tray.

## v3.15.3 — EQLogParser launcher slot + external-tool launcher hardening (2026-05-07)

This is a quality release on the v3.15.2 baseline. Autologin behavior is unchanged — this release adds a new external-tool launcher slot and hardens the existing four (`OpenGina`, `OpenGamparse`, `OpenEqLogParser`, `OpenDalayaPatcher`) plus several adjacent helpers that were carrying pre-existing bugs.

### EQLogParser launcher slot
- **New "EQLogParser" path entry on the Settings → Paths tab**, mirroring Gamparse: label + textbox + Browse… button with `*.exe` filter. Card height bumped from 240→272 so the existing Custom Icon row no longer clips with the new entry stacked above.
- **New tray-menu entry "📈 Open EQLogParser"** in the Launcher submenu, sitting directly above "📊 Open Gamparse". Like the other slots, it's user-rebindable — power users / GMs can wire any executable, not just EQLogParser, for whatever workflow they want.
- New `AppConfig.EqLogParserPath` field; `TrayManager.ApplyNewConfig` propagates it on Settings → Save.

### External-tool launcher hardening (all four `Open*` helpers)
Each helper independently went through 5 verifier-driven hardening rounds. The four methods stay separate by design (per the "separate guarded slots" principle) — each can evolve quirks (DalayaPatcher's AV-aware messaging) without flag-explosion in a unified helper.
- **3-tier path resolution:** `rawPath` (what the user typed) → `expanded` (after `Environment.ExpandEnvironmentVariables`, so `%PROGRAMFILES%\X.exe` resolves) → `path` (after `.Trim()` + paired-quote strip, so `"C:\X.exe"` from Windows "Copy as Path" works). Quote-strip is paired (`StartsWith('"') && EndsWith('"')`) — orphan quotes like `"C:\X.exe` are no longer silently swallowed.
- **3-way guard order on every helper:** `IsNullOrEmpty(rawPath)` → opens Settings → Paths; `!Path.IsPathFullyQualified(path)` → balloon + opens Paths (relative paths can no longer resolve against EQSwitch's cwd); `!File.Exists(path)` → balloon + opens Paths.
- **`WorkingDirectory` set to `Path.GetDirectoryName(path)`** on Process.Start. Tools that load plugins/configs from their own directory now find them.
- **Error balloons show what the user typed AND what we resolved to** when env-var expansion changed the string (`Got: %MYTOOLS%\GINA.exe\nResolved to: C:\Tools\GINA.exe`). Cosmetic-only normalization (whitespace, quotes) stays silent.
- **DalayaPatcher missing-file balloon now shows the path checked** (was bare AV message) and now opens Settings → Paths so the user can re-pick. Docstring updated to reflect the new flow.

### Adjacent helper hardening
- **`OpenLogFile` and `OpenEqClientIni`** now refuse to operate when `config.EQPath` is empty, balloon a meaningful message, and jump to Settings → General (where EQ Path lives). Previously they'd `Path.Combine` an empty string and silently open the wrong folder.
- **`OpenUrl` now reports launch failures** to the user via balloon (`Failed to open link: …`). Previously failures only hit `FileLogger.Warn` — a broken browser association silently produced no UI feedback.
- **Recent-logs picker cap** extracted into `private const int MaxRecentLogsInPicker = 25` (was hardcoded `10` in two places that had to stay in sync). Bumped from 10 → 25 — multi-character GMs no longer lose the bottom of the list to a single "more" entry.

### TrimLogFiles — atomic, race-safe, and honest
- **Atomic via temp file + `File.Replace`.** Tail is streamed to `<logFile>.trim.tmp` (disk-backed, not `MemoryStream` — kills the silent 2 GB cap on large logs and removes LOH pressure), then `File.Replace` atomically swaps. A process crash mid-trim now leaves the original log intact instead of partially overwritten.
- **Read-pass opens with `FileShare.Read` instead of `FileShare.ReadWrite`.** If EQ has the log open for write, the read-pass fails fast with a sharing violation (caught + logged + skipped) before we write the archive/temp files. Closes the silent-corruption window where `File.Replace` could swap a trimmed file under EQ's stale write handle.
- **Re-entry guard via `Interlocked.CompareExchange`.** Rapid menu double-clicks see "Log trim already in progress" balloon — no race on `.trim.tmp` paths between two concurrent Tasks.
- **Zero-byte-tail guard.** If a single line spans the entire trim window (no `\n` between split point and EOF — corrupted log, or one giant burst), the file is skipped with a logged warning rather than silently emptied.
- **`SynchronizationContext.Current` captured at the menu-click site** and used to post the result balloon back. Replaces the brittle `Application.OpenForms[0]` access that lost balloons when the user closed all windows mid-trim.
- **Honest summary.** `(trimmed, skipped)` switch produces accurate messages: "nothing to trim" only fires when nothing actually needed trimming. If files were skipped due to locks/errors, the user sees "Could not trim any logs — N skipped (file in use or other error)" or the mixed equivalent.
- **`config.EQPath` empty-check** at the top, matching the sibling helpers. Previously `Path.Combine` would throw silently in the background Task.

### Cleanup
- **AHK migration removed.** `Config/ConfigMigration.cs` deleted (it was dedicated to importing the legacy AutoHotkey `eqswitch.cfg` for users migrating from the AHK version of EQSwitch). The AHK userbase is gone — every active user has been on the C# build for many releases. `Program.cs` first-run flow simplified accordingly: drops the `if (migrated != null) { ... } else { ... }` AHK fallback and just shows `FirstRunDialog` directly. Net: -195 LOC, one fewer source file, simpler first-run path.

## v3.15.2 — Cleanup release: bridge resilience + JSON-tunable autologin timing (2026-05-05)

This is a quality release on the v3.15.1 baseline — defaults preserve v3.15.1's autologin behavior exactly, so observable wallclock is unchanged. The point is hardening for the long tail of edge cases.

### Bridge resilience
- **Heap scans now resume across polls instead of restarting from zero.** `HeapScanForCharArray` and `HeapScanForTargetName` carry `lastScanAddr` between polls — full-heap coverage in ~3 polls instead of timing out 1500ms-budget after 1500ms-budget on a fragmented heap. Fixes a class of "single-char account fails to find name" stalls that v3.15.1's structural-fallback was masking but not preventing.
- **`HeapScanForWidget` now has a 1500ms wall-clock budget** and resets its `g_widgetScanCount` counter when the widget cache is reset. The counter was a function-local static — after the first 5 scans of a process lifetime it went silent (no more diagnostics) but the scans kept running. Budget-abort no longer poisons the cache (`g_widgetScanBudgetAborted` flag separates "budget hit" from "widget genuinely absent").

### Account / character lookups
- **All `(Username, Server)` and `(Account FK, Character)` lookups now use `OrdinalIgnoreCase` consistently.** Sites flipped: `Models/AccountKey`, `AppConfig.FindAccountByName`/`FindCharacterByName`, `TrayManager`, `HotkeyBindingUtil`, `SettingsForm`, `AutoLoginTeamsDialog`, `CharacterEditDialog`, `AccountHotkeysDialog`, `CharacterHotkeysDialog`. Aligns with v3.15.1's `AppConfig.Validate` / `AccountEditDialog` which already used `OrdinalIgnoreCase`. Eliminates a class of "account exists but UI says it doesn't" bugs when account names differ only in case.

### Autologin retry path
- **Retry-burst Backspace primer.** RETRY BURST 1 now fires the same `0x08` primer keystroke as the initial BURST 1. Without it, the v3.14.x first-keystroke-drop reappears after the 30s stale-session wait — invisible on clean dual-box (only fires on the 90s timeout retry path).

### JSON-tunable autologin timing
- **10 timing knobs surfaced under `LaunchConfig`** so power users can experiment without rebuilding: `WaitTransitionInitialDelayMs`, `WaitTransitionSettleMs`, `WaitTransitionPollIntervalMs`, `Burst1ActivationSettleMs`, `Burst1PostSubmitMs`, `Burst2ActivationSettleMs`, `Burst2PostKeystrokeMs`, `PostBurst1WaitMs`, `BridgeInitWaitMs`, `StaleSessionWaitMs`. Defaults preserve v3.15.1 behavior exactly. `AppConfig.Validate` clamps all 10 (`StaleSessionWaitMs` floor 10000ms, prevents `Thread.Sleep(-1)` lockup from hand-edited negatives). `SettingsForm.BuildAppConfig` round-trips them so opening Settings and clicking Apply doesn't silently clobber JSON-edited tunables. `TrayManager.ReloadConfig` propagates them to `AutoLoginManager`.

### DPAPI defense-in-depth
- **Removed `length=N` info leak** in success log — was a small but real signal about password lengths.
- **`RunLoginSequence` wrapped in try/finally** in the BeginLogin Task.Run closure, so the captured plaintext password reference drops to GC promptly on any exit path. Caveat: structural `SecureString` migration is still future work — this is defense-in-depth only.

### Hardening
- **`AppConfig.Validate` null-guards** for `Accounts`/`Characters`/`Aliases`/`Teams`/`AutoLoginTeams`/`KeyHotkeys`/`KeyMaps`/`Pip` (7 List<T> properties) plus `CharacterAliases.RemoveAll(null)`.
- **`HotkeyBindingUtil`** switched to null-safe `string.Equals` to avoid NRE on legacy malformed binding entries.
- **`CharSelectReader.AttachWriter` MMF handle leak** fixed via new `WriterHandle` wrapper. Previously, transient SHM open failures could leak a NamedSharedMemory handle per attach attempt.

### Tests
- **New `Core/CharSelectReaderTests.cs`** with 8 unit tests covering the v3.15.0 latch, the v3.15.1 single-char structural fallback, and the new `WriterHandle` lifecycle. Run via `EQSwitch.exe --test-charselect-reader` (Debug only). All passing.
- **`AppConfigValidateTests.cs`** expanded from 6 to 9 cases — clamp coverage for the new `LaunchConfig` knobs.

### Cleanup
- **Removed 5 unused `SendInput*` helpers** from `AutoLoginManager.cs` (~110 LOC). Pre-existing dead code from the v3.4.x SendInput → SHM transition.
- **Doc-comment drift reconciled** across 6 files: stale "AccountKey.Matches Ordinal" comments, "declared below" → "above" comments in `mq2_bridge.cpp`, and a `LoginScreenDelayMs` doc that wrongly claimed "no longer consumed".

### Live verification (2026-05-05)
- Five consecutive dual-box team1 runs reached in-world end-to-end without operator intervention. Time-to-Enter-World 47.9-57.5s across all 5 passes (autologin spec target 35-50s; the additional ~7s is server-side server-select → char-select transition, identical to v3.15.1 baseline). No retry path triggered. Both clients in-world on every pass.

## v3.15.1 — Server-select unfreeze + single-char structural fallback (2026-05-05)

### Server-select responsiveness (the actual user-visible fix)
- **Server-select screen no longer sits frozen for 30+ seconds.** v3.15.0's standalone heap-scan path could fire during the login / server-select phases when EQ's login took longer than ~10 seconds — its 1500 ms `HeapScanForCharArray` + 1500 ms `HeapScanForTargetName` ran on the EQ game thread (via the `LoginController::GiveTime` detour) and blocked WindowMessage processing in a tight loop. The BURST 2 Enter keystroke would queue but never get consumed; C# eventually hit the 90 s `WaitForScreenTransition` timeout. The server-select transition now completes in the time EQ's own scene load takes (~21 s on this build), with no game-thread freeze added by the bridge.
- **New gate:** standalone heap-scan path is now skipped while `pinstCCharacterSelect` is null. Bridge tracks `g_consecutiveNullPolls` (file-scope counter, capped at 100, reset on pinst-non-null + `gameState == 5` + `Shutdown()`); standalone scan can only run when the counter is 0, i.e. when there's structural evidence we're actually at char-select.

### Char-select name discovery — single-char structural fallback
- **Single-character accounts no longer abort with `MQ2 bridge not ready after 30s` on heap-flaky sessions.** When the bridge's anchor scan can't locate the target name string in heap (Dalaya's heap state varies launch-to-launch — in some runs `HeapScanForTargetName` budget-expires without finding anything), the C# wait loop now falls back at the 10 s mark (`wait >= 20`) to using slot 1 by elimination. Path B2's `SetCurSel`/`GetCurSel` slot probe is structurally reliable: when it returns count=1, the EQ server has confirmed exactly one character on this account, so slot 1 must be the user's target. Gated on `CharacterSlot == 0 && Name set` — explicit slot bindings still take precedence.
- **`AutoLoginManager` post-gate path** bypasses `CharacterSelector.Decide` when `singleCharSlotFallback` is active (Decide would return slot 0 against a "Slot 1" placeholder), and uses `RequestSelectionBySlot(1)` directly with a logged-warn explaining the elimination reasoning.

### Latch-based ready signal (the polling-window robustness fix)
- **New `charSelectShm.charSelectReady` field** (uint32 at offset 768; struct grew 768 → 772 bytes; version bumped 2 → 3). The bridge writes 1 only at the five real-name publish sites: Path A struct read, Path C heap full-array scan, Path C anchor scan, standalone heap full-array scan, standalone anchor scan. Never on Path B2's `Slot N` placeholders. Cleared on `gameState == 5` (in-game), 30 consecutive pinst-null polls (~15 s, defends against user-backout-to-login without `gameState == 5` clearing), and `Shutdown()`.
- **`AutoLoginManager` charlist gate** widened from `mq2Available && charCount > 0` to `mq2Available && charSelectReady && charCount > 0`. Closes the race window where C# could read `charCount == 1` from Path B2's placeholder before Path C's same-poll heap scan finished overwriting with the real name. Inner 4-retry loop handles transient `charCount == 0` between cache invalidation and next anchor publish.

### `alreadyInGame` short-circuit (no more misleading abort message)
- **If the user manually enters world during the charlist wait**, autologin now returns success cleanly. v3.15.0 broke out of the wait loop without setting `charListReady`, falling into the `MQ2 bridge not ready after 30s` else-branch — user saw a confusing error toast even though they were in-game.

### Hardening
- **`HeapScanForTargetName` budget-abort** now clears `g_heapScanDone = false`, mirroring `HeapScanForCharArray`. Without this, a single budget-exceeded anchor scan would lock out all anchor-scan retries until the next pinst transition. Single-char accounts on fragmented heaps now get retry chances within the same char-select cycle.
- **`CharSelectReader.Open()` recycled-PID safety** — extracted `ResetShmHeader(accessor)` and now re-zeroes the SHM header on the existing-mapping early-return path. Defends against a recycled eqgame.exe PID inheriting stale `charSelectReady = 1` / stale char data from the prior process.

### Account uniqueness alignment (case-insensitive)
- **`(Username, Server)` matches now use `OrdinalIgnoreCase` consistently** across `AppConfig.Validate` (defensive duplicate scan, logs warnings without auto-deleting), `AccountEditDialog` (Add/Edit dialog uniqueness check), `AutoLoginTeamsDialog` (team-slot collision warning), and `CharacterEditDialog` (character → account FK lookup). EQ usernames are server-side case-insensitive — `gotquiz` and `Gotquiz` route to the same login — so the UI gates and validation now agree. `Models/AccountKey` and the FK lookups in `TrayManager` / `SettingsForm` remain `Ordinal` for now (changing them risks re-binding existing config FKs at load time).

### ABI test coverage
- **`ShmLayoutTests.cs` now asserts `CharSelectShm` layout** — 17 new assertions covering struct size (772), magic value (`0x45534353`), and all 16 field offsets including `charSelectReady` at 768. Run via `EQSwitch.exe --test-shm-layout` (Debug build); 25/25 passing.

### Live verification (2026-05-05)
- Three consecutive dual-box team1 runs reached in-world end-to-end without operator intervention. Mix of single-char structural fallback and multi-char heap-scan-success paths exercised; both clients consistently advance from server-select → char-select → in-world.

## v3.15.0 — Track B: char-select name discovery via race-byte filter + name-anchor (2026-05-05)

### Char-select name discovery (the slot-mode → real-name fix)
- **Bridge now finds real character names on Dalaya x86 char-select**, replacing the long-standing `Slot 1` / `Slot 2` placeholder fallback. Two layered paths cover the full account spectrum:
  - **Multi-char accounts (5+ chars):** `HeapScanForCharArray` now validates each candidate entry's `+0x44` race-byte against `[1, 600]` (player race range, covers Drakkin=522 and Froglok=330). Without this filter, Dalaya's race-table at-stride-0x160 won the scan first ("Dracnid", "Wyvern", "Pegasus" all pass title-case validation but their `+0x44` is a heap pointer in the millions). Threshold-5 unchanged.
  - **Single-char accounts (Natedogg, etc.):** new `HeapScanForTargetName` anchor scan — reads target char name from `LoginShm.character` (written by `AutoLoginManager.SendLoginCommand`) and locks onto the exact name in heap, requires `+0x44` race in `[1, 600]` for validation. ~0–1500ms wall-clock budget. Hits on first match.
- **`BAD_NAMES` blocklist expanded to 79 entries** in both `IsPlausibleName` and `IsValidCharArray` — same set across both Path A and Path C predicates so they reject the same heap garbage. Includes UI labels (Username, Password, Settings, Public, Console, …), all 16 EQ player races (Human → Drakkin), all 16 base classes (Bard → Shadowknight), and EQ-flavor strings present in heap (Brave, Storm, Hunter, Zone, Camp, Raid, …).

### State-machine hardening
- **Cache resets fire on every `pinstCCharacterSelect` non-null transition** — previously `g_heapScanDone` reset only on `gameState == 5` (in-game), but Dalaya keeps `gameState == 0` across both login and char-select. Without the transition reset, the v7 Phase 4 standalone heap scan fired during login state (heap empty), set `g_heapScanDone = true` permanently, and locked all heap-scan paths off when char-select actually loaded → bridge stuck on `Slot N` for the whole session.
- **`Path B2` cached-slot-count path now skips `Slot N` placeholder rewrite when `g_heapScanArrayBase != 0`.** Previously each poll re-wrote `Slot 1` over the real name, leaving a brief race window where C# could see `charCount=1 + names[0]="Slot 1"` between Path B2 and the re-read path → c74a766 abort gate trips.
- **Re-read branch re-pins `selectedIndex = 0` when anchor cached.** Without this, Path B2's `GetCurSel` write each poll could drift `selectedIndex` away from slot 0 while `names[0]` still held the anchor's target name → C# Enter World fires against the wrong slot. New `g_anchorScanCached` flag distinguishes anchor-cache (re-pin) from full-array-cache (preserve `GetCurSel`).
- **`g_loginShm->magic` deref now wrapped in `__try`** at both Path C and standalone anchor sites — defends against MMF unmap-during-poll race.
- **`HeapScanForCharArray` wall-clock budget (1500ms)** with `g_heapScanDone = false` reset on budget abort so next poll retries. Counter reports honest pages-per-region instead of region-as-page.
- **`Path A` widened scan** from `OFFSET_CHARSELECT_ARRAY ± 0x200` to `0..0x20000` (full CEverQuest range), with `g_charArrayNotFoundLogged` perf gate so the 32k-iteration scan runs once per char-select cycle, not per 500ms poll.
- **`MQ2Bridge::Shutdown()` defensive resets** for `g_charArrayNotFoundLogged`, `g_heapScanDone`, `g_anchorScanCached`, `g_standaloneDelay` — handles mid-process MQ2 re-init.

### Wiring (C# side)
- **`AutoLoginManager.RunCredentialEntry`** now takes a `targetCharacterName` parameter (default `""`); the caller in `BeginLogin` passes `character?.Name ?? ""`. `LoginShmWriter.SendLoginCommand` writes that into `LoginShm.character`, which the native bridge reads for the anchor-scan target. Empty string preserves the legacy "stop at char-select" path.

### Live verification (2026-05-05)
- **gotquiz/acpots (10-char account):** `heap scan FOUND char array at 0x112D5FD8 (10/10 names valid, race-filtered)` — real names {Acpots, Backup, Healpots, Jonopua, Nate, Potiongirl, Potionguy, Staxue, Thazguard, Zfree} → `selector → name match 'acpots' at slot 1` → `gotquiz logged in!`
- **nate/Natedogg (1-char account):** `anchor scan FOUND 'Natedogg' at 0x01DF10D8 (race=4, 0ms)` → host log `1 characters found: Natedogg` (real name, no `Slot 1` placeholder) → `name match 'Natedogg' at slot 1` → in-world.

## v3.14.12 — Autologin password reliability: primer keystroke + decrypt diagnostics (2026-05-04)

### Autologin
- **Password fully types into the login screen on every launch.** Pre-fix, EQ's input pump dropped the first 1–2 keystrokes after the BURST 1 SHM-active flip, intermittently producing 4-of-6 or 5-of-6 character passwords (silent failure → server kicks the empty-creds connection → EQ exits during charselect load). Fix: a single Backspace primer keystroke is sent immediately after BURST 1 activates, before the password chars. The primer absorbs whatever EQ drops; on an empty password field Backspace is a no-op. Verified across all three launch paths (Characters submenu, Accounts submenu, team1 dual-box).

### Diagnostic logging
- **Decrypted password length surfaced** post-decrypt — WARN on empty/suspiciously-short blobs (catches silent config corruption from a Settings save that re-encrypts an empty password). Length only, never the value.
- **`CombinedTypeString` typed/skipped counters** at entry/exit — now visible in `eqswitch.log` how many chars were written to SHM vs how many were filtered (unmappable / modifier-required). Eliminates ambiguity between "C# bailed on chars" vs "EQ dropped them after they were sent".

### Notes
- This release does not change the v3.14.0/v3.14.11 autologin codepath structurally; the primer is a 2-line surgical addition. `WarmupDwellMs` config knob remains the dwell tunable; default still 4000ms (the primer makes it less critical).

## v3.14.7 — UI polish: Dalaya rebrand, Accounts table restructure, dialog hardening (2026-05-01)

### Rebrand
- **Tray context-menu title** now reads `⚔  Dalaya v#  ⚔` (was `⚔  EQ Switch v#  ⚔`).
- **Settings window title** now reads `⚔  Dalaya Settings  ⚔` (was `⚔  EQSwitch Settings  ⚔`).
- Programmatic identifiers (exe name, mutex, log paths) are intentionally unchanged — user-visible labels only.

### Accounts table restructure
- **Column order** is now `# | Username | Note | Server | Flag` — Username is the pinning identity; the old "Name" column has been renamed "Note" to reflect that it's user-friendly metadata, not a key.
- **Flag column** is reserved for a future per-account login-status indicator (✓/✗/—) — cells are empty in this release.
- **`UseLoginFlag` UI surface fully removed** — the underlying property and autologin code path are unchanged; it's now an internal-only toggle.

### Account Edit dialog
- **Row order** matches the grid: Username → Password → Note → Server.
- **Note is optional** — the previous "Name is required" validation has been removed. Username remains required (it's identity).
- **Server dropdown locked to Dalaya** — `ComboBoxStyle.DropDownList` prevents typing, eliminating silent-typo bugs (e.g. lowercase "dalaya" orphaning characters from accounts via AccountKey identity mismatch).
- **Apply-time validation** updated to match: empty-Username (not empty-Name) now blocks save for hand-edited or imported configs. The Character empty-Name check is unchanged.

### Add Character dialog
- **Warning state** ("No Accounts yet — add one first") rebuilt to match the Accounts-tab card style. Previously sized for the full edit form, the dialog rendered too tall, with a clipped warning label and a half-cut-off Close button.

### Hotkey defaults
- **Multi-Mon toggle default** changed from `Alt+N` to `Ctrl+Alt+N`. Avoids conflict with EQ's Story Window hotkey on fresh installs (existing installs keep their saved binding).

### Bug fixes
- **"Create Desktop Shortcut" button** could get stuck on "Created!" when spam-clicked. The button now disables itself for 2s after each click; re-enable is timer-driven, independent of any code path inside `StartupManager.CreateDesktopShortcut`. No stuck label, no redundant `.lnk` writes.

### Tuning
- **Login-screen-delay clamp tightened** from [1, 15]s to [3, 10]s. The fallback dwell that fires when SHM warmup didn't run; 1s was aggressive enough to hit keystroke-truncation in dual-box scenarios. Default 5s unchanged.

## v3.14.6 — uninstall safety: never touch Dalaya's MQ2 dinput8.dll (2026-05-01)

### Bug fix
- **Settings → Paths → Uninstall and the standalone `uninstall.bat` will no longer touch Dalaya's MQ2 `dinput8.dll`.** A latent bug across both paths could delete Dalaya's live MQ2 core when chain-load-era artifacts (`dinput8.dll` proxy + `dinput8_dalaya.dll` MQ2) coexisted in the EQ folder, leaving the user with no `dinput8.dll` at all and forcing a Dalaya patcher re-run to restore connectivity. Both paths now size-check before deleting: anything ≥200KB is presumed to be Dalaya's MQ2 (~1.3MB) and is left alone; only sub-200KB EQSwitch proxies are removed. The fix is mirrored byte-for-byte across the C# helper, the in-app GUI button, and the standalone `.bat`.

### Hardening
- Uninstall now persists `RunAtStartup=false` *before* shortcut deletion, closing a window where a mid-flight crash could resurrect the startup shortcut on next launch.
- Uninstall now also removes legacy `HKCU\…\Run\EQSwitch` registry entries from pre-shortcut versions and any stale `dinput8.dll` left in EQSwitch's own app folder by pre-v3.4.3 builds.

### Docs
- README now points at the correct Settings tab (Paths, not General) and documents `uninstall.bat` as a fallback for when the GUI won't launch.
- New `docs/uninstall-smoke-test.md` covers eight scenarios including the chain-load coexistence cases that motivated this release.

## v3.14.5 — defensive zero-init in MQ2 SHM bridge (2026-04-29)

### Hardening
- **`MQ2Bridge::Poll` slot-name fallback paths now zero-init the local `slotName[CHARSEL_NAME_LEN]` buffer before `wsprintfA`.** The subsequent `memcpy(shm->names[i], slotName, CHARSEL_NAME_LEN)` copies the full 64-byte buffer into the C#-readable shared-memory region, so any bytes past the NUL terminator written by `wsprintfA` were uninitialized stack contents leaking into SHM. Not exploitable — destination size is fixed and there's no path-length blowup — but unnecessary disclosure of return addresses / local pointers. Two call sites (`mq2_bridge.cpp:3342` and `:3372`) now match the surrounding `={}` zero-init pattern.

### CI (no behavior change)
- Semgrep workflow excludes `Native/` from scans; the `gitlab.flawfinder.*` community rules generated zero-signal noise on x86 RE / MQ2-port code (37 historical findings, all manually triaged as false positives).

## v3.14.4 — self-update: SHA256SUMS now required (close fail-open on missing manifest) (2026-04-29)

### Security fix
- **Self-updater now fails closed when a release has no SHA256SUMS asset.** v3.14.3 fixed the catch-block fail-open inside the integrity-check try, but the outer `if (!string.IsNullOrEmpty(_hashFileUrl))` still skipped verification entirely if the release didn't ship a manifest. As of this version, an empty `_hashFileUrl` aborts the update with a clear error. Combined with the workflow change in v3.14.3 (which always emits `SHA256SUMS`), self-update is now unconditionally fail-closed: every accepted payload has been hash-verified end-to-end.

## v3.14.3 — self-update: fail-closed on hash verification errors + ship SHA256SUMS (2026-04-29)

### Security fix
- **Self-updater no longer fails open on hash verification errors.** The integrity-check `try` block at `UpdateDialog.cs:332-372` previously had a bare `catch { /* proceed without verification */ }` that swallowed any exception from the SHA256SUMS fetch — meaning an MITM that dropped the SHA256SUMS request could silently bypass the hash check entirely. The catch now logs, deletes the partial download, shows an error, and aborts the update. Network blips that take down the hash file fetch will now require a retry rather than installing an unverified binary.
- **Release workflow now generates and uploads `SHA256SUMS`** alongside the zip bundle. Previously the workflow only attached the zip, so the entire hash-verify code path was dead on shipped releases (it required `_hashFileUrl` to be non-empty, which depended on a `SHA256SUMS` asset that was never produced). This is the first release where in-app integrity checking is actually exercised end-to-end.

### Notes
- The first release on which the *upgrading client* will benefit from these checks is the next one after v3.14.3 — clients currently on v3.14.2 or earlier are running old updater code when they pull v3.14.3, so this release bootstraps the chain forward.

## v3.14.2 — Settings: swap Nuclear Reset and Update button positions (2026-04-29)

### UI
- **Update** moved into the persistent bottom toolbar of Settings (next to GitHub) where it's reachable from any tab — was previously buried on the Paths tab.
- **Nuclear Reset** moved out of the bottom toolbar onto the Paths tab (where Update used to live, between Help and Uninstall) — it's a destructive, rarely-used action and now lives alongside the other destructive Paths-tab tool (Uninstall) instead of being one fat-finger away on the always-visible toolbar. Confirm dialog and `_reopenAfterClose` reopen-with-defaults flow unchanged.

## v3.14.1 — dead-code removal: `[Obsolete] LoginAccount` wrapper + `ExecuteQuickLogin` (2026-04-29)

### Removed (no behavior change)
- **`AutoLoginManager.LoginAccount(LoginAccount, bool?)` deleted** — the `[Obsolete]` v3-wrapper that synthesized a v4 `Account` (+ optional `Character`) from a legacy `LoginAccount` row and delegated to `BeginLogin`. All live tray paths route through the intent-explicit `LoginToCharselect(Account)` / `LoginAndEnterWorld(Character)` API as of v3.13.0–v3.14.0 — the wrapper was the last piece of v3 routing scaffolding.
- **`TrayManager.ExecuteQuickLogin` deleted** — the only remaining caller of the obsolete wrapper, itself documented as "now dead code — Phase 5 deletes it per plan" at the time of v3.14.0. No live tray entry routed here.

### Polish
- Four stale doc comments updated: `TrayManager.BuildTeamsSubmenu` no longer claims the team path routes through `ExecuteQuickLogin`; `FireLegacyQuickLoginSlot` no longer references the deleted wrapper at a stale `AutoLoginManager.cs:127` line; `FireTeam`'s doc-block no longer carries the "this method bypasses the dead path" footnote; `AppConfig.FindAccountByName` no longer points at a stale `TrayManager.cs:1321-1322` line range.

### Build
- `CS0618` warning silenced — Release build is now warning-free.

### Retained
- The `LoginAccount` *type* (v3 model class in `Models/LoginAccount.cs`) is unchanged and still consumed by `LegacyAccounts` config storage, the v3→v4 migrator, the splitter, and the Settings reverse-mapper. Only the obsolete *method* of the same name on `AutoLoginManager` was removed.

## v3.14.0 — role color-coded hotkey dialogs + tray AutoLoginN v4 routing (2026-04-29)

### UI consistency
- **Color-coded names in Configure dialogs** for Teams / Characters / Accounts — match the **A** (purple) / **C** (blue) pill scheme established by team-configure. Team rows split into per-slot sub-labels by kind; Character / Account dialogs uniformly tinted. Orphan-dim and stale-warn states retained.
- **Tray-menu noise reduction** — removed `ToolTipText` on five root tray-menu entries (`Launch Client`, `Launch Team`, `Accounts`, `Characters`, `Teams` parents). Per-item submenu tooltips kept.
- **Window Title card tightened** — dropped the "Applied after client is in world" hint; card height shrank from 56 to 40px.

### Submenu hotkey display
- **`LegacyHotkeyLookup` now reads v4 hotkey lists** (`AccountHotkeys` + `CharacterHotkeys`). Was Phase-3-only and silently dropped any hotkey set via the v4 dialogs (e.g. `Natedogg + Alt+I` set in the Characters submenu would not display).

### Tray AutoLoginN v4 routing (`FireLegacyQuickLoginSlot`)
- **`QuickLoginN` empty** → fall back to combined `CharacterHotkeys` then `AccountHotkeys` (populated only), positionally indexed by slot.
- **`QuickLoginN` set but `LegacyAccount` lookup fails** → try v4 Character then Account by Name (case-insensitive on this drift path only; v4-list path stays ordinal). Rescues post-migration case drift.
- **`LogFirstFire` family strings distinguish the four routing paths** so the active path is visible in logs.

### Verified
- Clean Release build + FileVersion `3.14.0.0` 2026-04-29.

## v3.13.0 — UI polish, AutoLoginTeams refactor, position memory (2026-04-28)

### Tray menu
- Accounts submenu now shows **Username** (the unique login) instead of `Account.Name`. Legacy migrations had Name = character name, which collided with the Characters submenu — this removes the ambiguity.
- Teams submenu shows the resolved character/account names per team (e.g. `🚀 natedogg / acpots`) instead of the static `Auto-Login Team N` label.
- Menu hover tooltips can be toggled off via Settings → Video → Preferences → Show Tooltips. Same toggle that gated balloon toasts.
- `FloatingTooltip` now word-wraps long messages (max width 480px) so multi-account warnings don't run off the screen.

### Settings dialogs
- **Position memory** for 8 dialogs across the workspace: Account/Character/Team Hotkeys (Configure), Account/Character Edit (Add + Edit share), AutoLoginTeams, EQClientSettings, ProcessManager. Each remembers its last-open location for the rest of the session.
  - Bug fix in `DarkTheme.StyleForm`: the helper was clobbering callers' `FormStartPosition.Manual` with `CenterScreen`. Now preserves both `CenterParent` and `Manual`.
- **Paths tab** Startup card restored to original padding (x=47), single row layout for `Create Desktop Shortcut` + `Run at Startup`. `Show Tooltips` moved to Video → Preferences (paired with the Tooltip Duration knob it gates).
- **Hotkeys tab** gained a header-less, full-width `Client Launch Delay` aside at the bottom (moved from Video → Preferences).
- **Video tab** Preferences renamed `Tooltip Delay:` → `Tooltip Duration:` (the value is the auto-dismiss interval, not a hover delay). Range tightened to 100–5000ms with config Validate clamp matched.
- **Update dialog** compacted from 420×210 → 320×152, symmetric 20px top/bottom pads, buttons centered under the status text. Single-button OK states (winget / error / up-to-date) also centered.
- **Account / Character Hotkeys** Configure dialogs gained intent hints: `Will load to Character Select` and `Will load into game` respectively.
- **Team Hotkeys** Configure dialog row labels now show the team's resolved contents (`Team 1 — natedogg / acpots`) with `AutoEllipsis` for long names. Shrunk back to 400px wide after the destination suffix was dropped (see AutoLoginTeams refactor below).
- **Characters table** `HK` column → `Hotkey`, content is now a centered green ✓ when bound (with the full combo on the cell tooltip) instead of a truncated combo string.
- **`Trim Now`** button no longer fires a pre-work "Trimming log files…" popup; only the result MessageBox.

### AutoLoginTeams dialog (significant refactor)
- **Per-team `Enter World` toggle removed.** Destination is now dictated by slot kind alone:
  - `Character` slot → `LoginAndEnterWorld(character, null)` → enters the game world.
  - `Account` slot → `LoginToCharselect(account)` → stops at character select.
  - Mixed teams (Account + Character) get a mix; each slot follows its kind.
  - To stop a character at charselect, put the backing Account in that slot instead.
- `Team{N}AutoEnter` fields removed from `AppConfig` (System.Text.Json silently ignores the unknown JSON keys on load; next save drops them from the file).
- `ResolveTeamConfig` and `FireTeam` simplified to drop the `teamEntersWorld` parameter; `BuildTeamTooltip` no longer adds a `[force enter world]` line.
- **Pill colors changed from value-judgment to neutral.** Was `✓-green` (Character) / `!-yellow` (Account) which read as "correct/warning"; now `C-on-blue` (Character) / `A-on-purple` (Account) — both kinds are valid, no implied right/wrong. Unresolved still red `✗` since it IS an error state. Legend updated to `C = Character    A = Account    ✗ = unresolved`.
- **Pill tooltips reworded** descriptive instead of prescriptive — Account pill no longer says "Pick a Character to enter world instead", just states the constraint.
- **Form shrunk** 560 → 480 wide after the Enter World column came out.
- **Buttons no longer clipped** at the bottom (form was 210 tall; buttons at y=184 + 30px height = ended at 214). Form is now 254 with symmetric 18px top/bottom pads and the warning label gets its own row above the buttons.

### Engineering notes
- Two-round agent verifier sweeps over the major refactor and the language pass — caught two pill-tooltip drift issues (Character / Account) that survived the first pass, plus the `UpdateDialog.ShowError` button position that the dialog-shrink `replace_all` missed.
- Memory note saved on `CharacterSelector.Decide` precedence: `Slot ≥ 1` overrides Name lookup entirely (`Case 3`); `Slot = 0` is the only state that uses name-based heap lookup. Empirical inspection confirmed Nate's config has been running on slot-based selection (all three Characters had non-zero slots), so name-based has never been tested in his prod environment. Decision: leave Slot field as-is for now; revisit as a focused change with a dual-box smoke test.

## v3.12.1 — iter-12 MQ2-style structural lookup foundation (dormant) (2026-04-26)

### Changed
- No user-visible behavior change vs v3.12.0. Both clients enter password and reach in-world cleanly, ~63s end-to-end (verified dual-box 2026-04-26).

### Added (dormant, default-off)
- `Native/eqmain_widgets_mq2style.{h,cpp}` — MQ2-style structural recursion through `CXWnd`'s TListNode + TList multiple-inheritance layout. `FindLiveScreenByName` + `RecurseAndFindName` + `FindChildByName` with heuristic `CStrRep` CXStr name match. Wired through `FindLivePasswordCEditWnd` (in `eqmain_widgets.cpp`) and the `LOGIN_ConnectButton` lookup (in `login_state_machine.cpp`) with legacy heap-cross-ref fallback.
- `kMQ2StyleWidgetLookup = false` master toggle in the new header. Both call sites skip MQ2-style entirely; behavior matches v3.12.0 baseline.

### Pinned offsets (foundation for future)
- `CXWnd::pNext` `+0x08` (TListNode<CXWnd> base, runtime-validated).
- `CXWnd::pFirstChild` `+0x10` (TList<CXWnd> base, runtime-validated).
- `CXWnd::dShow` `+0x196` (slot 68/69 ICF body — `IsVisible() && !IsMinimized()`).
- `CXWnd::Minimized` `+0x1CE` (free byproduct of slot 68/69).
- `CSidlManagerBase::XMLDataMgr` `+0x144` (CXMLDataManager-base offset within the contained `CXMLParamManager`).

### Known (do not enable kMQ2StyleWidgetLookup without redesign)
- iter-12's MQ2-style walks invoke `IterateAllWindowsPublic` from `LoginStateMachine::Tick` (via the `LoginController::GiveTime` detour), which runs on EQ's game thread. That same thread services `IDirectInputDevice8::GetDeviceState`, the path delivering SHM-injected BURST keystrokes. With the toggle on, the background client's `GetDeviceState` polling stalled while the walk was in flight, dropping password keystrokes. Foreground client has a Win32 keyboard fallback path that bypasses `GetDeviceState`, so it landed clean — hence deterministic foreground-OK / background-fail. Confirmed by 4 dual-box test runs and 3 independent code-review agents at 75% confidence. Toggle off restores v3.12.0 behavior.
- Future v6 design (Combo G primary): direct memory write to `InputText` CXStr at `+0x1A8`, skip BURST keystrokes entirely. Foundation laid by these pinned offsets + the dormant MQ2-style code.

## v3.12.0 — ~13s faster dual-box, 3x faster typing, sync-context P0 (2026-04-25)

### Performance
- **Wait `phase >= ClickingConnect` (was `WaitConnectResponse`)** — Dalaya advances to ClickingConnect ~2s after SendLoginCommand; the broken Connect button never advances to WaitConnectResponse, so the previous gate spent the full 15s timeout for nothing. Cuts ~13s off the dual-box happy path.
- **New `WarmupDwellMs` config (default 4000ms)** replaces the flat 5s `LoginScreenDelayMs` in the SHM-warmup path. `LoginScreenDelayMs` is kept as a fallback only.
- **`CombinedTypeString` per-character timing tightened** — 80ms→25ms, 50ms→15ms, shift-up 40ms→15ms. A 6-char password now takes ~240ms total (was ~780ms) — paste-like at 60fps.
- **`FireTeam` honors `Client Launch Delay`** (Settings → Video → Client Launch Delay, default 1s) between team slots. Previously the autologin team-fire path called `LoginAndEnterWorld` in a tight loop with 0ms gap, racing Dalaya's auth gate when concurrent BURST 1 submits landed within ~30ms.

### Refactor
- **Extracted `RunCredentialEntry` from `RunLoginSequence`** — single method with honest docs: warmup → dwell → BURST 1 → cancel. Removes the dual `shmDidCredentials` dance.
- **New `LoginCredentialsSent` event** fires after BURST 1 deactivate. `TrayManager` now applies slim-titlebar + hook config + window title at T+~7s instead of T+~30s (was waiting on charselect-ready). `LoginComplete` is kept as the idempotent end-of-sequence; both call the shared `ApplyDeferredCosmetics(pid)`.
- **`SendCancelCommand` fires BEFORE BURST 1** (was AFTER for one mid-iteration that caused truncation — the DLL's `PHASE_CLICKING_CONNECT` loop was polling `MQ2Bridge::ClickButton` concurrent with typing, contending for EQ's message pump).

### Fixed (P0 — pre-existed but the new event widened exposure)
- **Sync context late-bind** — `TrayManager.Initialize()` now installs the WinForms `SynchronizationContext` post-NotifyIcon and propagates it to `AutoLoginManager` via a new `SetUiContext()`. Previously `_syncContext` captured pre-`Application.Run` was null; events fell into the synchronous-fire branch on background threads, racing `TrayManager._injectedPids` and other UI state.
- **`FireTeam` ShowWarning marshal** — `ShowWarning → DeferToNextTick → WinForms.Timer` construction MUST happen on the UI thread. Now wrapped in `_uiContext.Post` inside the `Task.Run` lambda. Previously silently broken (timer never ticked) when no team slots were assigned and `FireTeam` was running on the threadpool.

### Verified
- Clean Release build + dual-box smoke 2026-04-25 ("SLICKED!!!").

## v3.11.3 — Combo G read-back + ConnectButton vtable gate + ~6s autologin (2026-04-25)

### New
- **Combo G CStrRep_Dalaya layout corrected** (`Native/eqmain_cxstr.h`) — utf8 verified at +0x14 via runtime hex dump; introduced `ownerPtr` field at +0x10 to document the eqmain-internal pointer that lives there. Live recon supersedes the 2013 disassembly comment.
- **`WriteEditTextDirect` read-back verification** (`Native/eqmain_cxstr.cpp`) — after `ConstructFromCStr` succeeds, the written CStrRep's `length` and first utf8 byte are verified against what was requested. Returns false (callers fall back to keystroke) on any mismatch. **Caught a real silent-success bug** where the function reported success while writing into the wrong widget memory.
- **`PHASE_CLICKING_CONNECT` vtable gate** (`Native/login_state_machine.cpp`) — `MQ2Bridge::FindWindowByName` returns a CXMLDataPtr def (vtable = eqmain DOS header) when no live `LOGIN_ConnectButton` widget exists. Pre-fix, the DLL called `MQ2Bridge::ClickButton` on the def, which silently early-returned, and the state machine advanced phase regardless. Now gated on `EQMainOffsets::IsEQMainButtonWidget`; if not a real `CButtonWnd`, retry up to 50 times then `SetError` so C# falls back loudly. Counter resets in `InvalidateWidgets` so a fresh login attempt starts clean.
- **C# SHM credentials warmup ritual** (`Core/AutoLoginManager.cs::RunLoginSequence`) — sends `LOGIN` SHM command and waits up to 15s for `phase >= WaitConnectResponse`. On Dalaya phase never advances past `ClickingConnect` (no live button), so the 15s timeout always fires — but the DLL's widget-discovery activity during that window warms up EQ's input subsystem so BURST 1 keystrokes land cleanly. Then BURST 1 runs unconditionally.
- **`g_password` redacted from DLL log** (`Native/eqmain_cxstr.cpp`) — `WriteEditTextDirect` now logs `textLen=N` + first-byte hex only, never the full string. Earlier diagnostic logged `text="<redacted-6-char-password>"` (real password — original plaintext name elided 2026-05-04) into the DI8 log file.

### Performance
- Autologin landed at ~6s wait → in-world per dual-box test 2026-04-25. (Was timeout-bound around 35-50s previously.)

### Known limitations (next session)
- Combo G writes to `+0x1A8 InputText` successfully, but EQ renders/submits from a different buffer — direct SHM password injection still doesn't work end-to-end on Dalaya. BURST 1 keystrokes are the actual workhorse.
- Two parallel autologin paths (SHM warmup ritual + BURST 1 keystrokes) is confusing; warmup needs to be repurposed or replaced with a non-credential-attempt mechanism.

## v3.11.2 — autologin documentation honesty + load-bearing-warmup discovery (2026-04-25)

### Fixed
- Stale `LoginShm overall timeout (14s)` log message corrected to `(45s)` —
  the timeout was bumped to 45s in iter 15.2 but the message text was never
  updated, leading to false impressions when reading logs.
- Stale `// PATH A: ... DISABLED — native widget discovery needs a dedicated
  RE session.` comment block replaced with current reality + a
  ⚠ LOAD-BEARING SIDE EFFECT ⚠ warning. Combo G fixed widget discovery; the
  broken piece is now the DLL's post-connect detection. **PATH A's 45s
  timeout, although the "intended" login flow never completes, is incidentally
  serving as the warmup that PATH B's keystroke injection requires** — without
  it, BURST 1 fires at T+10s and EQ drops the first ~3 keystrokes (verified
  2026-04-25 by attempting C# disable, password truncated 6→3 chars, login
  failed, rolled back).

### Notes
- No behavior change vs v3.11.1 — only comments and one log message.
- The "skip PATH A entirely" win identified during analysis turned out to
  need a non-time-based BURST-1 readiness gate, not just commenting-out the
  if-block. Tracked as a future "D" task.

## v3.11.1 — `\` switch key now EQ-window-only (2026-04-25)

### Fixed
- **`\` (SwitchKey) was firing globally** — any press in chat, Discord, browsers, etc. was being swallowed by the keyboard hook. Now scoped to "EQ client window must be foreground" via the existing `processFilter` path. `]` (GlobalSwitchKey) remains genuinely global, as designed.
- Removed the cold-start "no EQ focused → focus first client" branch from the primary path of `OnSwitchKey` (left in as a defensive no-op). The previous EQ-only filter had been temporarily removed on 2026-04-24 to work around a broken-autologin foreground race; that race is gone now that autologin lands EQ as foreground end-to-end.

## v3.10.0 — GPL-2.0-or-later + Native v7/v8 login path (2026-04-18)

### Changed
- **License changed from GPL-3.0 to GPL-2.0-or-later** — ecosystem alignment with MacroQuest (MQ2) and the broader EverQuest tool community, which is uniformly GPLv2-only. GPL-2.0-or-later is upward-compatible with GPLv3 for anyone who wants it, while unlocking legitimate code-sharing with MQ2-derived work. Prior releases (v3.9.3 and earlier) remain GPL-3.0 forever. Tag `v3.9.3-last-gplv3` marks the relicense boundary.
- **README License section** — formal attributions added for **Stonemite** (DirectInput proxy approach studied, no code taken) and **MacroQuest** (character-select facts referenced, no source compiled in). SHM boundary between EQSwitch and MQ2 DLL reaffirmed.
- **README fan-made disclaimer** strengthened — EQSwitch is free, educational, independent, and not sold.
- **CONTRIBUTING.md** — contributor license grant updated to GPL-2.0-or-later.

### Native login reliability (v7)
- **GiveTime detour** replaces SetTimer-based polling — login state machine now rides the game's own game-loop tick (50–130 Hz), matching the MQ2 pattern for stable, high-frequency polling.
- **`LoginController*` fast-path** — when the controller pointer is already resolved, subsequent logins skip the full scan.
- **Charselect robustness** — detects `eqmain.dll` unload at character select and resumes the background poll instead of bailing.
- `LoginShmWriter` wired into the native path.

### Native widget discovery (v8, internal foundation)
- MQ2-style `eqmain.dll` detection with widget-ownership tracking.
- Corrected `SetWindowText` vtable slot + exact-vtable class gate for login-widget match.
- `HeapScanForWidget` — locates login widgets by SIDL name on the heap.
- Live `CXWnd` discovery: tree walk, heap cross-reference, label search.
- Improved `CXWndManager` diagnostics.

### Rationale (relicense)
Sole-author relicensing (verified via `git log`). No external contributors held copyright on any EQSwitch code. No user-visible behavior change from the relicense itself.

---

## v3.9.3 — Release Polish (2026-04-13)

### Changed
- Version bump for public release following v3.9.2 security hardening.

---

## v3.9.2 — Native Upgrade, WinGet Compat, Security Hardening (2026-04-13)

### Added
- **SHM-driven enter-world** — in-process `CLW_EnterWorldButton` click via shared-memory request/ack handshake (replaces earlier PostMessage approach for this step).
- **Charselect slot probe** — runtime validation that the resolved slot matches the user's intended character before commit.
- **Vtable guard around `GetChildItem`** — defensive check before calling into MQ2-exported thunks.

### Changed
- **WinGet-compatible distribution** — packaging adjustments for smooth WinGet manifest submission.
- **Security hardening for distribution** — installer / update path review, string scrubbing, no secrets in binaries.

### Fixed
- **Log-spam reduction** in native `NetDebug` output during charselect polling.

---

## v3.9.0 / v3.9.1 — Per-Account AutoEnterWorld + Naming Cleanup (2026-04-12 / 2026-04-13)

### Added
- **Per-account and per-team `AutoEnterWorld` flag** — granular control over which accounts auto-commit to character select vs. stop at the character screen.
- **DLL verification report** (`Native/VERIFICATION.md`) — independent reverse-engineering evidence for MQ2 export offsets used on Dalaya.
- **Volatile cross-thread fields** in native login state machine (C++ memory-model correctness fix surfaced by 9-agent audit that fixed 8 bugs).

### Changed
- **`LaunchTwo` → `LaunchAll`** terminology cleanup. Config migrated v2 → v3 (duplicate `LaunchTwo` removed).

---

## v3.6.0 — UI Polish, Log Trimming, Account Backup (2026-04-10)

### Added
- **Log trimming** — async stream-based trimmer with archive to `Logs/archive/`. Default threshold 50 MB; configurable.
- **Account backup / import** — DPAPI blobs portable across imports on the same Windows user.
- **Team submenu rework** — `LaunchTwo` for bare clients, Launch Team restored, all 4 teams in tray submenu.

### Changed
- **Hotkeys / Video / Accounts tabs** — card padding, conflict warnings, Windowed Mode relocated to Window Style card.

### Fixed
- **Native vtable validation** before `GetChildItem` call (prevents crash on Dalaya's variant EQ client).
- **Retry counter** for charselect window search with bounded cap (was unbounded busy-wait).
- **Lazy MQ2 init** — replaces blocking `Sleep(2000)` startup delay.
- **MemoryBarrier before SHM charCount write** — ordering fix for cross-thread reads.

---

## v3.5.0 — Background Input & 3-Layer Activation Defense (2026-04-09)

### Fixed
- **Background auto-login works end-to-end while EQ is unfocused.** Root cause was an inline `GetForegroundWindow` hook in `iat_hook.cpp` that only spoofed for callers within `eqgame.exe`'s address range — EQ's game loop calls from loaded DLLs fell outside that range. Three-layer fix:
  1. Inline hooks skip the caller check when SHM is active, so `GetForegroundWindow` / `GetFocus` / `GetActiveWindow` all return EQ's HWND.
  2. Persistent WndProc subclass blocks `WM_ACTIVATEAPP(FALSE)` / `WM_ACTIVATE(WA_INACTIVE)` / `WM_KILLFOCUS` / `WM_NCACTIVATE` with a 16 ms re-install timer.
  3. Activation blast on re-install after EQ's 3D char select overwrites the subclass.
- **Unconditional 200 ms re-post** of `WM_ACTIVATEAPP(1)` while SHM active (old self-check was defeated by the hook's own spoofing).
- `CallWindowProcA` → `CallWindowProcW` for Unicode compatibility.

---

## v3.4.3 — Suspended-Process Injection Architecture (2026-04-08)

### Changed
- **Replaced `dinput8.dll` proxy with CREATE_SUSPENDED process injection.** EQSwitch now injects `eqswitch-di8.dll` and `eqswitch-hook.dll` directly into `eqgame.exe` after resuming the loader (~50 ms). Dalaya's 1.3 MB MQ2 `dinput8.dll` stays untouched — no patcher conflicts, no server hash validation failures.

### Added
- **Character select Enter World** uses 250 ms key holds with 3 retry attempts and real title-change verification.
- **`ActivateThread`** continuously re-posts `WM_ACTIVATEAPP(1)` while SHM active to defend against focus loss.
- **Adaptive `WaitForScreenTransition`** — replaces fixed 3 s post-server-select sleep; polls `IsHungAppWindow` + `GetWindowRect` stability and handles any load time (5 – 90 s).

### Removed
- Dead proxy files (prior `dinput8.dll` proxy architecture).

---

## v3.4.2 — Self-Updater Completeness + Config Migration Framework (2026-04-08)

### Added
- **Self-updater handles all shipped files** — `EQSwitch.exe`, `eqswitch-hook.dll`, and `dinput8.dll` (previously missed `dinput8.dll`).
- **`--test-update` CLI flag** for simulating full update flow locally without a GitHub release.
- **Post-update toast notification** on relaunch.
- **`ConfigVersionMigrator`** — versioned framework that transforms raw JSON before deserialization; preserves user settings across property renames and type changes.

### Fixed
- **Retry logic for `.old` artifact cleanup** (race with memory-mapped exe).
- **CTS dispose race** in `UpdateDialog`.

---

## v3.4.0 — CI / Release Pipeline Cleanup (2026-04-05)

### Changed
- **Removed broken framework-dependent build from CI** — Release artifacts are self-contained single-file only.
- **Native DLLs bundled into release zip** — `eqswitch-hook.dll` (and later `eqswitch-di8.dll`) ship alongside the exe.

---

## v3.3.1 — Config Baseline & Defaults Overhaul (2026-04-04)

### Fixed
- **AppConfig defaults baselined to EQ's eqclient.ini** — nuclear reset now matches a fresh EQ install (22 booleans, 3 clip planes, mouse sensitivity, sound volume)
- **INI section targeting corrected** — ChatSpam writes to [Options] not [Defaults], Keymaps writes to [KeyMaps] not [Defaults], Particles routes FogScale/LODBias/SameResolution to [Options]
- **11 phantom [Defaults] writes removed** — Sky, BardSongs, Anonymous, ClipPlane, MouseSensitivity, ShadowClipPlane, ActorClipPlane and 4 others were being injected into [Defaults] where they don't belong; now write only to [Options]
- **LoadFromIni reads [Options] section** — settings that live in [Options] (EQ's runtime-authoritative section) are now read correctly on form open
- **SlowSkyUpdates EnforceOverrides** — now restores EQ default (3000ms) when unchecked instead of leaving 60000
- **SkyUpdateInterval ApplyToIni** — falls back to 3000 when no original value was captured
- **MouseSensitivity/SoundVolume LoadFromIni clamp** — minimum changed from 0 to -1 to preserve sentinel values
- **ForceWindowedMode** — now reads from [Defaults] in addition to [VideoMode]
- **DisableEQLog** — moved from AppConfig to EQClientIniConfig; LoadFromIni now reads Log key; ApplyToIni now writes it
- **ConfiguredKeys sentinel tracking** — numeric fields at sentinel values (-1 or 0) are removed from ConfiguredKeys instead of being tracked but never enforced
- **Maximized ConfiguredKeys gap** — now tracked when saved from SettingsForm
- **ProcessManagerForm FPS ConfiguredKeys** — MaxFPS/MaxBGFPS now tracked when saved from Process Manager
- **ChatSpamForm EnforceOverrides** — safe int serialization (`value != 0 ? "1" : "0"`) instead of raw `value.ToString()`
- **ModelsForm phantom writes on load failure** — _initialValues snapshot moved outside try block
- **Snapshot early-return bypass** — all 4 sub-forms restructured from `if (!exists) return` to `if (exists) { try...catch }` so snapshots run unconditionally
- **VideoModeForm XOffset/YOffset defaults** — changed from 1 to 0 (EQ default)
- **GDI font leak coverage** — added DisposeControlFonts to EQChatSpamForm, EQVideoModeForm, EQClientSettingsForm, FirstRunDialog, ProcessManagerForm, SettingsForm

### Changed
- ChatServerPort writes to [Options] (was [Defaults], key doesn't exist in fresh ini)
- Doc comments updated to accurately describe EQ defaults and section locations

## v3.1.0 — DirectInput Proxy, Background Auto-Login & Hook Upgrades (2026-04-01)

### Added
- **DirectInput proxy DLL** (`Native/dinput8.dll`) — IAT hook proxy that intercepts `GetForegroundWindow`, `GetAsyncKeyState`, and `GetKeyboardState` inside eqgame.exe. Per-PID shared memory injects scan codes into EQ's DirectInput keyboard device without stealing focus.
- **Background auto-login** — Types passwords into background EQ windows via DirectInput shared memory injection. True one-click multi-account login with no focus stealing.

- **SetWindowTextA hook** — Custom window titles now persist through zone transitions, login, and character select. The injected DLL intercepts EQ's own SetWindowTextA calls and substitutes the configured title. Same approach as WinEQ2.
- **ShowWindow hook** — Blocks EQ from minimizing itself on focus loss during DirectX init. Fixes the Maximize-on-Launch + no-Slim-Titlebar crash where EQ would get stuck minimized.
- **Auto hook injection** — Hook DLL now injects whenever any hook feature is needed (custom window title, maximize protection, or slim titlebar), not just when slim titlebar + hook is toggled on.
- **Video Settings description** — Added page description and "Monitor Selection" section title for clarity.
- **Resolution hint** — Yellow hint in Window Style card when slim titlebar is disabled, reminding to set EQ resolution to fit above the taskbar.
- **Help form auto-login section** — Documents background login status and dinput8.dll requirement.

### Fixed
- **Auto-login typing** — Switched from VK+scancode to KEYEVENTF_UNICODE for reliable text entry on EQ's login screen. FocusAndSendKey re-focuses before each keystroke to survive focus theft.
- **Hook injection during login** — DLL injection and slim titlebar guard are deferred until login sequence completes, preventing focus theft mid-login.
- **Window title not applied on discovery** — Titles now appear immediately when EQ is detected, not just during explicit arrange operations.
- **Build: TestInput sub-project conflict** — Excluded TestInput/ from default compile globbing to prevent duplicate assembly attribute errors.

### Changed
- Hook DLL shared memory struct extended with `blockMinimize` flag and 256-byte `windowTitle` buffer (284 bytes total, up from 24).
- **License changed to GPL-3.0** — Matches Stonemite's license (studied their DirectInput proxy approach).

---

## v3.0.1 — Per-Process Hook Shared Memory & Audit Fixes (2026-04-01)

### Changed
- **Per-process shared memory** — Each injected eqgame.exe gets its own memory-mapped file (`EQSwitchHookCfg_{PID}`) instead of a single global mapping. Hook DLL now works in both single and multimonitor modes with correct per-window positioning.
- **Atomic config writes** — `ConfigManager.FlushSave` writes to a temp file then `File.Move` to prevent config corruption on crash.

### Fixed
- **Hook configs not updated after ArrangeWindows/ToggleMultiMonitor** — Hook DLL would snap windows back to stale positions after "Fix Windows" or mode toggle.
- **Hook configs not updated after SwapWindows** — Multimonitor swap would be immediately undone by the hook.
- **DllInjector handle leaks** — `hThread` handles in both `Inject()` and `Eject()` moved into `finally` blocks.
- **DllInjector.Eject dead code** — Removed unused `allocAddr`/`VirtualFreeEx` and stale `ResolveLoadLibraryA` call.
- **GetExportRva missing guards** — Added `-1` checks after `RvaToFileOffset` calls for clearer PE parse errors.
- **HookConfigWriter resource leak** — `Open()` catch path now disposes both `MemoryMappedFile` and `ViewAccessor` on failure.
- **HookConfigWriter.Disable zeroed geometry** — Now read-modify-writes to only flip the `Enabled` flag.
- **Dead-PID injection race** — Timer tick guards against injecting into a process that died during the 2s delay.
- **Missing client early-return** — `UpdateHookConfigForPid` logs and returns when PID not in client list.
- **Stream leak in LoadIcon** — `GetManifestResourceStream` now disposed with `using`.
- **SettingsForm font leaks** — Inline fonts on labels and DataGridView header tracked and disposed.
- **Double-dispose of foreground debounce timer** — Removed redundant dispose in `TrayManager.Dispose`.
- **PID naming contract** — Cast to `uint` for shared memory name to match C++ `%lu` formatting.

---

## v3.0.0 — DLL Hook Injection, Auto-Login & PiP Overhaul (2026-04-01)

### Added
- **DLL hook injection** (`Core/DllInjector.cs`, `Native/eqswitch-hook.dll`) — Injects a native MinHook-based DLL into eqgame.exe that hooks `SetWindowPos` and `MoveWindow`. Enforces window position/style via shared memory-mapped config (`HookConfigWriter.cs`). Prevents EQ from fighting window management.
- **DPAPI-encrypted auto-login** (`Core/AutoLoginManager.cs`, `Core/CredentialManager.cs`) — Account presets with username, encrypted password, server, character name, and slot. Full enter-world automation via `SendInput` on a background thread. Credentials encrypted with `DataProtectionScope.CurrentUser` — only the same Windows user on the same machine can decrypt.
- **Login Accounts model** (`Models/LoginAccount.cs`) — Stored account presets for auto-login with name, username, encrypted password, server, character, slot, and login flag toggle.
- **PiP orientation support** — PiP overlays adapt to window orientation and layout changes.
- **Hook config shared memory** (`Core/HookConfigWriter.cs`) — Memory-mapped file (`EQSwitchHookCfg`) shared between C# host and injected DLL. Struct-matched layout (packed, sequential ints) for target position, style, and enable flag.
- **Native hook source** (`Native/`) — Full MinHook source (buffer, trampoline, HDE32/64 disassembler) plus `eqswitch-hook.cpp` with build scripts for MSVC and MinGW.

### Changed
- **Settings expanded** — New Auto-Login tab with account management, credential encryption, and launch integration.
- **PipOverlay enhanced** — 87 lines added for orientation-aware thumbnail rendering.
- **TrayManager expanded** (982 → 1549 lines) — Auto-login menu integration, DLL injection lifecycle, hook config management.
- **SettingsForm expanded** (734 → 1211 lines) — Auto-login account editor, DLL hook controls, PiP orientation settings.
- **README updated** with new feature descriptions.

### Removed
- **Unit test project** (`EQSwitch.Tests/`) — Removed during architecture transition. Tests covered v2.x patterns that no longer apply post-DLL injection.
- **Solution file** — Simplified to single-project build.
- **PLAN_DLL_HOOK.md** — Planning doc removed after implementation.

---

## v2.9.1 — Settings & Launch Cleanup (2026-03-30)

### Changed
- **Tray clicks simplified** — Removed triple-click entirely. Left button: single + double click. Middle button: single + triple (via click counting — `MouseDoubleClick` doesn't fire for middle on `NotifyIcon`).
- **Launch is bare-bones** — Removed `EnforceOverrides`, `EnforceWindowedModeIfBorderless`, `PositionOnTargetMonitor`, and post-launch `ArrangeWindows`. Launch just starts `eqgame.exe` with staggered delay. Added restore-if-minimized after 3s.
- **Settings UI cleanup** — Removed CtrlHoverHelp (unreliable in overflow tray). Human-readable switch mode labels ("Swap Last" / "Cycle All"). Tray Click Actions card redesigned. Preferences card alignment fixed.
- **Paths tab auto-open** — Clicking GINA or Dalaya Patcher in launcher menu opens Settings → Paths tab if path not set.
- **Tooltip Delay** — Renamed, supports 0 = disabled.
- **Multi-Monitor Mode checkbox** in Video Settings synced with config.

### Fixed
- **eqclient.ini corruption** — `EnforceOverrides` was writing `Maximized=1` and offsets=-8 on every launch, causing windows to minimize. Removed from launch path entirely.
- Hotkeys tab overlapping labels in Actions card — clean 2-column grid.

---

## v2.9.0 — UI Consolidation & Multi-Monitor (2026-03-30)

### Added
- **Multi-monitor video settings** — Monitor picker, per-monitor resolution, position preview.
- **Config backup restore** — Restore from any of the 10 backup rotations.

### Changed
- **Tabs consolidated** — Merged Performance + Launch into Hotkeys tab. Reduced from 8 to 6 tabs.
- **Stacked fullscreen as default layout** — Clients stack on top of each other, arranged in stacked mode.
- **FPS writes to [Options] section** — Correct INI section for MaxFPS/MaxBGFPS.
- **Priority default changed to AboveNormal** (was High).
- **Process Manager redesigned** — Priority card moved to top, CPU thread mapping card, grid refresh paused during edits.
- **Video Settings overhaul** — Reordered submenu, preset sizes fixed.

### Fixed
- **DefaultFont crash** — Null reference on systems without default font.
- **Launch positioning** — Don't force window offsets on every launch, respect user INI edits.
- **PiP anchor** — Fixed anchor point for overlay positioning.
- **Dalaya patcher** path handling.
- Direct switch hotkeys (Alt+1-6) disabled by default to avoid conflicts.
- Hotkey conflict warning appearing on every Settings close.
- PiP max windows label layout and custom size capped to 960×720.

### Removed
- **Swap Windows** feature — removed (stacked mode replaces it).
- **CharacterEditDialog** — removed (per-character overrides simplified).

---

## v2.8.0 — Slim Titlebar / WinEQ2 Mode (2026-03-30)

### Added
- **Slim titlebar mode** (WinEQ2 style) — Strips `WS_THICKFRAME` (resize border) while keeping `WS_CAPTION` (thin title bar). Positions window at full `rcMonitor` bounds to overlap taskbar. Replaces both "borderless" and "remove title bars" options with a single unified mode.
- **Auto-apply slim titlebar** — Guard timer re-applies style when EQ fights the window decoration changes.
- **EQClientSettingsForm expanded** — Additional eqclient.ini toggle controls.

### Changed
- **WindowManager rewritten** (280+ lines changed) — Unified slim titlebar logic, monitor bounds calculation, style manipulation.
- **Settings Layout tab** — Slim titlebar checkbox replaces borderless + remove-title-bar checkboxes.
- **LaunchManager simplified** — Removed post-launch window positioning (slim titlebar handles it).

### Removed
- **ROADMAP.md** — Removed from project (tracked in root `Roadmap_master.md`).
- **Borderless fullscreen mode** — Superseded by slim titlebar mode.
- **Remove Title Bars option** — Superseded by slim titlebar mode.

---

## v2.7.0 — Process Manager Consolidation (2026-03-28)

### Added
- **Consolidated Process Manager** — 3 clear cards: Windows Priority, Core Assignment, FPS Limits
- **INI-based Core Assignment** — 6 NumericUpDown slot pickers for CPUAffinity0-5, writes directly to eqclient.ini
- **Ghost FPS label** — shows current eqclient.ini MaxFPS/MaxBGFPS values alongside the editor

### Changed
- **FPS defaults** changed to 80/80 (was 0 = unlimited, which crashes EQ)
- **Priority defaults** changed to High/High (prevents virtual desktop crashes + enables autofollow)
- **Settings "Affinity" tab renamed to "Performance"** — stripped to enable toggle + retry settings
- **Submenu directions** — Video Settings, Settings, Launcher all open upward (AboveRight)
- **CharacterEditDialog simplified** — priority override only (core assignment now global via eqclient.ini)

### Removed
- **ThrottleManager** — process suspension was causing "Suspended" in Task Manager
- **CPU Affinity submenu** from tray menu — Process Manager is the one-stop shop
- **Per-character AffinityOverride** — replaced by global eqclient.ini CPUAffinity0-5 slots

---

## v2.6.0 — Per-Character Overrides (2026-03-20)

### Added
- **Per-character CPU affinity** — assign different core masks to individual characters. Characters with `AffinityOverride` use their custom mask instead of the global active/background masks.
- **Per-character process priority** — set individual characters to Normal, AboveNormal, or High priority. Characters with `PriorityOverride` use their custom priority instead of the global setting.
- **Character Edit dialog** — double-click a character in Settings → Characters tab to edit affinity mask and priority overrides. Checkbox toggles override on/off, hex mask input with validation.
- **Process Manager "Source" column** — shows "Custom" (cyan) for clients using per-character overrides, "Global" for clients using default settings.
- **Reset Defaults button in Video Settings form** — resets Width/Height (1920×1080), offsets (0,0), Windowed Mode (on), Disable Log (off), Title Bar Offset (0). Matches AHK v2.4 `ResetVMDefaults`. Requires Save or Apply to write to disk.

---

## v2.4.0 — Tray UX Overhaul (2026-03-14)

### Added
- **Configurable tray click actions** — Settings → General tab lets users bind single/double/triple/middle-click to specific actions (Launch One, Fix Windows, Open Settings, etc.)
- **Custom video presets** — Save up to 3 custom resolutions in Video Settings (FIFO eviction, duplicates skipped)
- **Dark-themed context menus** — `DarkMenuRenderer` applies dark background/foreground to all tray menu items
- **FloatingTooltip** — replaces `MessageBox.Show` "already running" popup with a non-blocking floating tooltip

### Changed
- **Tray context menu reorganized** into grouped submenus (Video Settings, Settings, Launcher)
- **Medieval emoji/icon prefixes** restored on all tray menu items (matches AHK v2.4 style)
- **CPU Affinity submenu simplified** — removed per-core checkboxes, shows info labels only
- **Process Manager** restyled with dark DataGridView theme
- **First-run** now auto-opens Settings instead of requiring manual navigation

---

## v2.3.0 — Performance & Fullscreen (2026-03-13)

### Added
- **Background FPS throttling** (`Core/ThrottleManager.cs`) — duty-cycles background EQ clients via `NtSuspendProcess`/`NtResumeProcess`. Configurable throttle percent (0-90%) and cycle interval. Active client is never throttled. Settings on Affinity tab.
- **Borderless fullscreen mode** — WinEQ Y+1 offset trick: strips window decorations and positions at `(monitor.Left, monitor.Top+1)` using `rcMonitor` bounds. Preserves Alt+Tab and PiP window overlay. Checkbox on Layout tab.

---

## v2.2.0 — Production Hardening (2026-03-12)

### Added
- **Persistent file logging** (`Core/FileLogger.cs`): Info/Warn/Error with timestamp, 1MB rotation, thread-safe
- **Input validation** (`AppConfig.Validate()`): Clamps all numeric config fields to safe ranges on load and save
- **IWindowsApi interface** (`Core/IWindowsApi.cs`): Abstraction layer for Win32 calls, enables unit testing
- **79 unit tests** across 7 test files: AppConfig, WindowManager, AffinityManager, ConfigManager, ConfigMigration, HotkeyManager, EQClient
- **Solution file** (`EQSwitch.sln`): Main project + xUnit test project with Moq

### Fixed
- **P2-02**: Context menu client labels now update when window titles change
- **Concurrency**: KeyboardHookManager uses `ImmutableHashSet<int>` (lock-free) instead of `HashSet<int>` with lock
- **Concurrency**: ProcessManager fires events outside lock block, uses specific exception catches
- **Concurrency**: AffinityManager snapshots retry counters before iterating
- **Resource leak**: LaunchManager implements IDisposable, cancels launches on dispose
- **Resource leak**: ProcessManagerForm stops refresh timer in Dispose()
- **Backup pruning**: ConfigManager sorts by file write time instead of filename string
- **Hotkey ID overflow**: `_nextId` resets to 1 on `UnregisterAll()` (P4-01)
- **Exception hardening**: DWM HRESULT mapped to readable messages in PipOverlay
- **Exception hardening**: VideoSettingsForm retries file I/O (2x, 500ms)
- **Magic numbers**: Named constants replace all magic numbers across 6 files

### Changed
- All diagnostic logging migrated from `Debug.WriteLine` to `FileLogger`
- WindowManager and AffinityManager accept optional `IWindowsApi` for dependency injection

---

## v2.1.1 — Post-Release Audit Fixes (2026-03-12)

### Fixed
- **P0-01**: Hook callback dispatched async via SynchronizationContext.Post() — prevents Windows killing the LL hook on slow callbacks
- **P0-02**: Global switch key `]` no longer swallowed when zero EQ clients running (requireClients guard + cached PID check)
- **P0-03**: PiP overlay Ctrl+drag works — replaced WS_EX_TRANSPARENT with dynamic WM_NCHITTEST (HTTRANSPARENT default, HTCLIENT when Ctrl held)
- **P0-04**: eqclient.ini read/write uses ANSI encoding instead of UTF-8 to prevent corruption
- **P1-01**: Eliminated Process.GetProcessById() in hook callback — cached PID HashSet with GetWindowThreadProcessId
- **P1-02**: Screen.PrimaryScreen null-safe fallback for headless/RDP disconnect
- **P1-03**: All eqclient.ini file operations use Encoding.Default consistently
- **P1-04**: Triple-click tray detection resets timestamp on every click
- **P1-05**: Run-at-startup registry path validated on launch — auto-corrects if exe moved
- **P1-06**: LaunchManager timers cancelled on config reload and shutdown
- **P1-07**: Minimum 500ms enforced between staggered launches
- **P1-08**: ContextMenuStrip disposed in Shutdown path
- **P1-09**: Previous custom icon disposed on LoadIcon reload

---

## v2.1.0 — Deferred Features (2026-03-11)

### Added
- **Process Manager GUI**: Live view of all EQ clients with PID, character name, priority, and affinity mask. Auto-refreshes every second. Includes Force Apply button.
- **PiP Settings tab**: Configure PiP size preset, custom dimensions, opacity, border color, and max windows from Settings GUI
- **Characters tab**: View character profiles with Export/Import buttons for JSON backup
- **All Cores / Clear buttons**: Quick-select on Affinity tab to set masks to system max or minimum
- **Force Apply Affinity**: Tray menu item to re-apply affinity rules to all clients immediately
- **Triple-click tray**: Triple-click the tray icon within 500ms to arrange all windows
- **Desktop shortcut**: Create Desktop Shortcut menu item via WScript.Shell COM

### Changed
- "Process Info" balloon replaced with full Process Manager window
- PiP config now persists through Settings GUI (was only configurable via JSON)
- ReloadConfig now includes PiP settings for hot-reload

---

## v2.0.0 — C# Port (2026-03-11)

Complete rewrite from AutoHotkey v2 to C# (.NET 8 WinForms).

### Added
- **Settings GUI**: 6-tab dark-themed settings dialog (General, Hotkeys, Layout, Affinity, Launch, Paths)
- **Video Settings**: Read/write eqclient.ini [VideoMode] section with resolution presets
- **PiP Overlay**: DWM thumbnail-based live previews of background EQ clients
  - Click-through overlay (won't steal focus)
  - Ctrl+drag repositioning with position persistence
  - Auto-hide when fewer than 2 clients
- **Window Swap**: Rotate window positions (1→2, 2→3, N→1)
- **Hung window detection**: Skip unresponsive windows during arrange/swap operations
- **Files submenu**: Quick access to log files, eqclient.ini, GINA, notes
- **Links submenu**: Dalaya Wiki, Shards Wiki, Fomelo
- **Help window**: Full hotkey reference and feature guide
- **Run at Startup**: Registry-based toggle in tray menu
- **Tray interactions**: Double-click to launch, middle-click for PiP
- **Hotkey suffixes**: Keyboard shortcuts shown in tray menu items
- **Config migration**: Auto-import from AHK eqswitch.cfg on first run

### Changed
- Config format: INI → JSON (eqswitch-config.json)
- Config backups: automatic rotation (keeps last 10)
- Hotkey system: RegisterHotKey + WH_KEYBOARD_LL (was AHK native)
- Window arrangement: proper multi-monitor support via EnumDisplayMonitors
- CPU affinity: configurable retry on launch (EQ resets affinity after startup)
- Build output: .NET single-file publish (no AV false positives)

### Removed
- AutoHotkey dependency
- Flash suppress / auto-minimize (superseded by PiP + affinity management)
