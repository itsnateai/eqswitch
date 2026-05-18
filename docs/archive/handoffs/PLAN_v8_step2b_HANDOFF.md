# New-Session Handoff — v8 Step 2B: eqmain-side function pointer resolution

**Paste this into a new session to continue.**

---

Continuing v8 EQSwitch port of MQ2's login architecture.

**Steps 1 + 2A SHIPPED** (commit `7528477` on `main`, pushed to GitHub, deployed as
`C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-di8.dll` at 195,584 bytes).

**Proven live across 2 test logins today (2026-04-16):**
- `LdrRegisterDllNotification` callback fires synchronously before eqmain's DllMain
- `EQMainOffsets::IsEQMainWidget(pWnd)` correctly identifies eqmain-owned widgets
  via vtable-in-module-range test
- Hypothesis confirmed: **64/64** SetEditText+ClickButton SEHs per login are
  `isEqMain=1` — classic class mismatch (our g_fnSetWindowText was resolved from
  dinput8.dll's `EQClasses::CXWnd::SetWindowTextA` export, i.e. eqgame-side; the
  widgets live in `eqlib::eqmain::CXWnd` with different member offsets)
- Latent Tier-0 scanner bug fixed (ReadWindowText SEHs: 26 → 0) by gating the
  diagnostic dump with `IsEQMainWidget`

**Pick up at Step 2B: resolve eqmain-side SetWindowTextA + WndNotification
function pointers and route `SetEditText` / `ClickButton` through them when
`IsEQMainWidget(pWnd)` is true. Goal: zero SEH in next live-login log.**

## Read these first (5 minutes)

1. `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v8_step2a_shipped.md` —
   full session summary with verified facts, runtime addresses, and Step 2B options.
2. `C:/Users/nate/.claude/projects/X---Projects/memory/reference_eqswitch_mq2_eqmain_detection.md` —
   MQ2 architecture research. Part 2 explains the class-mismatch root cause.
3. `C:/Users/nate/.claude/projects/X---Projects/memory/feedback_verify_claims_in_claudenotes.md` —
   READ THIS. Verify claims against primary sources before acting. Prior sessions
   cost 4 days on a wrong CXWnd-ctor-hook hypothesis.

## What's NOT yet done

- Step 2B: eqmain-side fn ptr resolution — **your task this session**
- Step 3: port MQ2's `ReinitializeWindowList` into GiveTime first-fire hook;
  replace Phase 6 heuristic. Deferred.
- Step 5: delete orphaned `Native/sidl_ctor_hook.{cpp,h}` (Phase 7 abandonment).
  Deferred.

## The actual problem

`mq2_bridge.cpp:SetEditText` and `:ClickButton` call:

```cpp
g_fnSetWindowText(pEditWnd, cxstrBuf);   // eqgame-side function body
g_fnWndNotification(pButton, pButton, 1, nullptr);
```

Both function pointers were resolved at DLL init time from dinput8.dll exports:

```cpp
?SetWindowTextA@CXWnd@EQClasses@@QAEXAAVCXStr@2@@Z
?WndNotification@CXWnd@EQClasses@@QAEHPAV12@IPAX@Z
```

Those are `EQClasses::CXWnd::...` — the eqgame-side class. When called on an
eqmain-owned widget (every login widget, confirmed by `isEqMain=1` on 64/64
SEH faults), they access member offsets that don't match eqmain's layout →
SEH fault (sometimes silently corrupts state before faulting).

## Fix: mirror MQ2's architecture

Resolve parallel eqmain-side function pointers at DLL-notification LOAD time,
then branch on IsEQMainWidget in the call sites. Exactly what MQ2's
`InitializeEQMainOffsets(BaseAddress)` does in
`X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/src/eqlib/src/EQLibImpl.cpp:888-913`.

## Three strategy options (pick and commit)

### 1. Pattern-scan eqmain.dll at runtime (MQ2 upstream approach)
- Use dinput8.dll's exported eqgame-side function body as a byte-template
- Scan eqmain.dll's .text for similar prologues
- **Risk**: signature fragility across builds. A wrong match replaces
  catchable SEH with a direct call to random bytes that corrupts state.
  Worse failure mode than current behavior.

### 2. Vtable-slot discovery
- Read an eqmain widget's vtable pointer (every widget has one)
- For each vtable slot (~180 in CXWnd), check if the slot function body
  matches SetWindowTextA signature
- **Risk**: still signature-dependent. Lower risk than option 1 because
  the vtable gives us a bounded search space.
- **Advantage**: bootstraps from any eqmain widget (we have plenty at login)

### 3. Hard-coded RVAs from one-shot IDA/Ghidra reverse pass
- Statically disassemble `C:/Users/nate/proggy/Everquest/Eqfresh/eqmain.dll`
  (or wherever Dalaya ships eqmain.dll)
- Find `SetWindowTextA`/`WndNotification` by signature and cross-reference
  from the exported eqgame-side function in dinput8.dll (same semantic,
  often similar structure)
- Extract RVAs. Add to `eqmain_offsets.cpp` as hard-coded constants:
  `fnSetWindowText = (FN_SetWindowText)(base + EQMAIN_RVA_SetWindowTextA);`
- **Risk**: Dalaya patches eqmain.dll → RVAs change. Mitigated by tagging
  each RVA with the eqmain.dll build hash we reversed against, and logging
  a warning if live eqmain hash differs.
- **Advantage**: zero runtime complexity, rock-solid when the build matches.

**Prior session's recommendation after Step 2A data**: **option 3.** We only
need 2 RVAs. Reverse once in ~30 min with IDA/Ghidra. Add a build-hash check
for self-detection of Dalaya patches. Cleanest.

Don't re-derive this — pick based on what tools you have ready. If IDA/Ghidra
is installed, option 3. If not, option 2.

## Concrete file plan (whichever strategy)

Extend `Native/eqmain_offsets.{h,cpp}`:

```cpp
namespace EQMainOffsets {
    // Already shipped in Step 2A:
    void OnEQMainLoaded(uintptr_t dllBase, uint32_t sizeOfImage);
    void OnEQMainUnloaded();
    bool IsEQMainWidget(const void *pWnd);
    void GetRange(uintptr_t *outBase, uint32_t *outSize);

    // NEW in Step 2B:
    typedef void (__thiscall *FN_SetWindowText)(void *thisPtr, void *pCXStr);
    typedef int  (__thiscall *FN_WndNotification)(void *thisPtr, void *sender, uint32_t msg, void *data);

    extern FN_SetWindowText   fnSetWindowText;    // nullptr until resolved
    extern FN_WndNotification fnWndNotification;  // nullptr until resolved
    // Resolution happens inside OnEQMainLoaded. Strategy-specific
    // helper function (PatternScan / VtableSlotScan / HardcodedRVA)
    // populates these. Log every resolution attempt clearly.
}
```

Then `Native/mq2_bridge.cpp` `SetEditText` and `ClickButton` become:

```cpp
void MQ2Bridge::SetEditText(void *pEditWnd, const char *text) {
    if (!g_fnCXStrCtor || !g_fnCXStrDtor || !pEditWnd || !text) return;

    auto fn = EQMainOffsets::IsEQMainWidget(pEditWnd) &&
              EQMainOffsets::fnSetWindowText
            ? EQMainOffsets::fnSetWindowText  // eqmain-side, correct layout
            : g_fnSetWindowText;              // eqgame-side fallback
    if (!fn) return;

    __try {
        uint8_t cxstrBuf[16] = {};
        g_fnCXStrCtor(cxstrBuf, text);
        fn(pEditWnd, cxstrBuf);
        g_fnCXStrDtor(cxstrBuf);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        DI8Log("mq2_bridge: SEH in SetEditText (pWnd=%p isEqMain=%d fn=%p)",
               pEditWnd, EQMainOffsets::IsEQMainWidget(pEditWnd) ? 1 : 0, fn);
    }
}
```

Same pattern for `ClickButton` with `fnWndNotification`.

## Build + deploy

```bash
cd X:/_Projects/eqswitch
bash Native/build-di8-inject.sh
# Backup current deploy before overwrite
cp C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-di8.dll \
   C:/Users/nate/proggy/Everquest/EQSwitch/eqswitch-di8.dll.prev
cp Native/eqswitch-di8.dll \
   C:/Users/nate/proggy/Everquest/EQSwitch/
# If EQ is running and file-lock blocks copy:
# mv the running DLL to .locked.<ts>, then cp fresh in.
```

## Verification after Step 2B deploy

Nate launches one test login. Check new log at
`C:/Users/nate/proggy/Everquest/Eqfresh/eqswitch-dinput8.log`:

```bash
grep -cE "SEH in (SetEditText|ClickButton|ReadWindowText)" $LOG
# TARGET: 0

grep "eqmain_offsets: resolved" $LOG
# Should show: resolved SetWindowTextA at 0x71E..., WndNotification at 0x71E...

grep "isEqMain=1" $LOG
# Should be empty (no SEHs means no isEqMain logging fires)
```

## Keys to hit back into

### Files you'll touch
- `Native/eqmain_offsets.h` — add fn ptr declarations
- `Native/eqmain_offsets.cpp` — add resolution logic (strategy-specific)
- `Native/mq2_bridge.cpp` — route SetEditText / ClickButton based on IsEQMainWidget
- No new files needed

### Live runtime addresses (verified 2026-04-16, 2 test logins)
- eqmain.dll base: `0x71E20000` (stable across sessions)
- eqmain.dll SizeOfImage: `0x359000`
- eqmain .text: base + `0x1000`, size `0xFDAE3`
- Known eqmain vtables (inside module range):
  - Editbox: `0x71F2D304`
  - Button: `0x71F2AA08`
  - SIDL other: `0x71F2D370`
- dinput8.dll (MQ2-patched by Dalaya): at `0x72EE0000` (was seen in prior sessions)

### Login widget addresses from latest log (for bootstrapping vtable-slot discovery)
- `LOGIN_UsernameEdit` at `0x10DBC610`
- `LOGIN_PasswordEdit` at `0x10EF97C4`
- `LOGIN_ConnectButton` at `0x1108D260` (from earlier log; may drift per session)
- `YESNO_YesButton` at `0x10EF9854` (the kick-session dialog — hit 62x by retry loop)

### MQ2 reference source (authoritative)
`X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/`
- `src/eqlib/include/eqlib/game/LoginFrontend.h:270-780` — eqmain::CXWnd class
  declaration (64-bit offsets; use as structural ref, halve for 32-bit)
- `src/eqlib/src/EQLibImpl.cpp:888-913` — InitializeEQMain pattern
- `src/eqlib/include/eqlib/offsets/eqmain.h` — public RVA template (LoginController
  et al; DOES NOT include SetWindowTextA/WndNotification — those live in the
  non-public `eqmain-private.h` private header). Don't waste time grepping public
  MQ2 for them; they're not there.

### What NOT to do

- **Don't re-do Step 2A.** The primitive works. Use `EQMainOffsets::IsEQMainWidget`
  as-is.
- **Don't pattern-scan without a fallback.** If the scan is ambiguous (multiple
  matches or zero matches), LOG IT and keep existing `g_fnSetWindowText` behavior.
  Worse to silently use a wrong function pointer than to leave current SEH noise.
- **Don't re-derive the class-mismatch claim.** It's verified in the source.
  Don't reopen the question.
- **Don't hunt for another MQ2 fork.** `_.src/_srcexamples/macroquest-rof2-emu`
  is the correct one. Verified.
- **Don't reintroduce Phase 7 ctor hooks** (`sidl_ctor_hook.*` on disk but not
  compiled). MQ2 doesn't use them; neither should we.

### One-sentence resume

"Read `project_eqswitch_v8_step2a_shipped.md`, pick one of three strategies
for resolving eqmain-side `SetWindowTextA` + `WndNotification` function pointers,
implement in `Native/eqmain_offsets.{h,cpp}` with graceful fallback when resolution
fails, route `SetEditText` / `ClickButton` through them when `IsEQMainWidget(pWnd)`
is true, rebuild and deploy, verify zero SEH in live-login log."
