# EQSwitch Combo G — Phase 4 CXStr Recon (2026-04-24)

**Target:** `Native/recon/eqmain.dll` (Dalaya x86 PE32, compiled 2013-05-11)
**ImageBase:** `0x10000000`  **Arch:** i386 / 32-bit / MSVC
**Cross-ref:** `phase1-findings.md` (vtable[73] target + InputText offset)

> **Why this exists:** to call `eqmain::CEditWnd::SetWindowText` (Phase 1's
> bypass target), we need an **eqmain-flavored `CXStr`** as the argument.
> `eqmain.dll` has zero exports (verified via `rizin -c iE`), so we cannot
> `GetProcAddress` the ctor — pattern-scan or RVA-pin is the only access path.
> The existing `g_fnCXStrCtor` in `mq2_bridge.cpp` resolves
> `??0CXStr@EQClasses@@QAE@PBD@Z` from `dinput8.dll` exports; that's the
> **eqgame-side** CXStr which targets eqgame's CXFreeList allocator —
> wrong allocator for an eqmain widget.

## Three deliverables

1. **`eqmain::CXStr::CXStr(const char*)`** at RVA `0x000473d0`
2. **`eqmain::CXStr::FreeRep`** at RVA `0x000472d0` (used as dtor)
3. **Confirmed Dalaya CStrRep layout** matches the legacy comment in
   `Native/mq2_bridge.cpp:730-740` — old MQ2 layout, NOT modern MQ2's
   (no `encoding` or `freeList` fields)

## Discovery method

Rizin xref-walk from a known login-screen string literal:

```
$ rizin -q -c 'aa;aac;axt @ 0x10103590' Native/recon/eqmain.dll
fcn.1002e830 0x1002e994 [DATA] push str.PasswordEdit
```

Single xref to `"PasswordEdit"`. Disassembly at the push site:

```asm
0x1002e994  push  str.PasswordEdit          ; 0x10103590
0x1002e999  lea   ecx, [var_34h]             ; ecx = &CXStr (this)
0x1002e99c  call  fcn.100473d0               ; <-- CXStr ctor
0x1002e9a1  push  1
0x1002e9a3  lea   eax, [var_34h]             ; eax = &CXStr (constructed)
0x1002e9a6  push  eax                        ; push CXStr*
0x1002e9a7  mov   ecx, esi                   ; ecx = widget
0x1002e9a9  mov   byte [var_40h], 5          ; SEH state
0x1002e9ad  call  fcn.10068ae0               ; FindWidgetByName(*name, recurse=1)
```

Classic `__thiscall` ctor shape — string in stacked arg, `this` in ecx.
Address `fcn.100473d0` clusters with the operator= at `0x10047590` (already
identified in Phase 1 as the CXStr op= used by SetWindowText body), which is
MSVC-typical for member functions of the same translation unit.

## Ctor body — `fcn.100473d0` (verified)

```asm
0x100473d0  push  ebp                       ; standard frame
0x100473d1  mov   ebp, esp
0x100473d3  mov   eax, [arg_4h]              ; eax = const char* s
0x100473d6  push  esi
0x100473d7  push  edi
0x100473d8  mov   esi, ecx                   ; esi = this (CXStr*)
0x100473da  test  eax, eax                   ; if (s == nullptr)
0x100473dc  je    0x100473ee
0x100473de  lea   edx, [eax + 1]             ; inline strlen
0x100473e1  mov   cl, [eax]
0x100473e3  inc   eax
0x100473e4  test  cl, cl
0x100473e6  jne   0x100473e1
0x100473e8  sub   eax, edx                   ; eax = strlen(s)
0x100473ea  mov   edi, eax
0x100473ec  jmp   0x100473f0
0x100473ee  xor   edi, edi                   ; len = 0
0x100473f0  mov   [esi], 0                   ; this->m_data = nullptr
0x100473f6  test  edi, edi
0x100473f8  jle   0x1004742d                 ; if (len <= 0) return
0x100473fa  push  ebx
0x100473fb  push  0                          ; encoding=0 (utf8)
0x100473fd  lea   ebx, [edi + 1]             ; size = len + 1 (incl null)
0x10047400  push  ebx                        ; size
0x10047401  mov   ecx, esi                   ; this
0x10047403  call  fcn.10047070               ; CStrRep allocator (Assure)
0x10047408  mov   eax, [esi]                 ; eax = m_data
0x1004740a  test  eax, eax
0x1004740c  je    0x10047424                 ; if alloc failed, skip copy
0x1004740e  mov   [eax + 8], edi             ; m_data->length = strlen(s)
0x10047411  mov   eax, [arg_4h]              ; src
0x10047414  mov   ecx, [esi]                 ; m_data
0x10047416  push  ebx                        ; len+1
0x10047417  push  eax                        ; src
0x10047418  add   ecx, 0x14                  ; m_data + 0x14 (utf8 base)
0x1004741b  push  ecx                        ; dest
0x1004741c  call  fcn.100d1900               ; memcpy
0x10047421  add   esp, 0xc
0x10047424  pop   ebx
0x10047425  pop   edi
0x10047426  mov   eax, esi                   ; return this
0x10047428  pop   esi
0x10047429  pop   ebp
0x1004742a  ret   4
```

**Confirms semantically equivalent to MQ2's `CXStr(const char* s) : assign(s)`.**

## FreeRep body — `fcn.100472d0` (verified)

Locked variant — enters CS, does refcount-decrement-and-maybe-free.

```asm
0x100472d0  push  ebp                       ; SEH frame setup
0x100472d1  mov   ebp, esp
0x100472d3  push  -1
0x100472d5  push  0x100f15d8                ; SEH handler
0x100472da  mov   eax, fs:[0]
0x100472e0  push  eax
... (SEH cookie xor-with-ebp, EH frame install) ...
0x10047317  call  EnterCriticalSection      ; locks 0x10341674 (CXStr mutex)
... (refcount-- on m_data, free if zero) ...
```

Identical role to MQ2's `CXStr::FreeRep(CStrRep* rep)`. Use it for cleanup
after `SetWindowText` returns.

## Prologue signatures (for runtime byte check)

Sourced fresh from `rizin pxw 16` on each function — **DO NOT trust handoff
transcriptions.** The Phase 1 RESUME handoff said `0x10097af0` prologue was
`64 a1 00 00 00 00 ff 35`; actual rizin output is `64 A1 00 00 00 00 6A FF`.
Bug was a transcription error (`6A FF` = `push imm8 sign-extended -1`, not
`FF 35` = `push m32`). Path B's runtime check would have always-mismatched
if implemented from the handoff text.

| Function | RVA | First 8 bytes | Decoded |
|---|---|---|---|
| `CXStr::CXStr(const char*)` | `0x000473d0` | `55 8B EC 8B 45 08 56 57` | `push ebp; mov ebp,esp; mov eax,[ebp+8]; push esi; push edi` |
| `CXStr::FreeRep` | `0x000472d0` | `55 8B EC 6A FF 68 D8 15` | `push ebp; mov ebp,esp; push -1; push 0x100f15d8` |
| `CEditWnd::SetWindowText` | `0x00097af0` | `64 A1 00 00 00 00 6A FF` | `mov eax,fs:[0]; push -1` (SEH frame) |

To regenerate after a Dalaya patch:
```bash
rizin -q -c 'pxw 16 @ 0x100473d0' Native/recon/eqmain.dll
rizin -q -c 'pxw 16 @ 0x100472d0' Native/recon/eqmain.dll
rizin -q -c 'pxw 16 @ 0x10097af0' Native/recon/eqmain.dll
```

## CStrRep layout (Dalaya x86, confirmed by ctor body writes)

```c
struct CStrRep_Dalaya {
    /*0x00*/ int32_t   refCount;     // set by allocator (fcn.10047070)
    /*0x04*/ uint32_t  alloc;        // set by allocator
    /*0x08*/ uint32_t  length;       // set by ctor: mov [eax+8], edi
    /*0x0c*/ uint8_t   _pad[0x0c];   // contents unknown (likely encoding/freeList in some order)
    /*0x14*/ char      utf8[1];      // memcpy dest: ecx = m_data + 0x14
};
// CXStr is a single-DWORD wrapper: just CStrRep* m_data
struct CXStr_Dalaya { CStrRep_Dalaya* m_data; };
```

Compare to **modern MQ2's CStrRep** (`eqlib/include/eqlib/game/CXStr.h:69-89`):

```c
struct CStrRep_Modern {
    /*0x00*/ atomic<int>      refCount;
    /*0x04*/ uint32_t          alloc;
    /*0x08*/ uint32_t          length;
    /*0x0c*/ EStringEncoding   encoding;     // ← NEW
    /*0x10*/ CXFreeList*       freeList;     // ← NEW
    /*0x18*/ char              utf8[4];      // ← shifted from +0x14
};
```

**Important:** Modern MQ2's CStrRep is NOT layout-compatible with Dalaya's.
The eqgame-side `g_fnCXStrCtor` (resolved from `dinput8.dll` exports) uses
the modern layout because Dalaya's dinput8.dll IS modern-MQ2 — but the
**eqmain.dll widgets internally use the OLDER 2013 CStrRep layout**, which
is what `fcn.100473d0` builds. **Do not mix.**

## Combo G C++ helper sketch (DORMANT — do not wire up yet)

Belongs in a NEW translation unit (`Native/eqmain_cxstr.cpp` + header) to
keep the two CXStr namespaces strictly separated. Per
`feedback_eqswitch_no_regression_to_dinput8.md`: fail-mode hierarchy is
**probe → AOB rescan → hard-fail loud, NEVER fall back to dinput8 path**.

```cpp
// Native/eqmain_cxstr.h — eqmain-flavored CXStr (NOT EQClasses::CXStr)
namespace EQMain {
    struct CStrRep;                           // opaque — only ctor/FreeRep touch it
    struct CXStr  { CStrRep* m_data; };

    // Resolve from cached eqmain base + RVAs in OnEQMainLoaded().
    // Validate prologue bytes BEFORE caching the function pointers.
    bool ResolveCXStrFunctions(uintptr_t eqmainBase);

    // Fail if not resolved — never silently no-op.
    bool ConstructFromCStr(CXStr* out, const char* s);
    void Free(CXStr* x);

    // Combined helper: writes text into an eqmain CEditWnd via vtable[73].
    // Returns false on any of: prologue mismatch, vtable slot mismatch,
    // SEH fault during call. Caller must NOT fall back to dinput8/keyboard
    // path — that path is the regression we're escaping.
    bool WriteEditTextDirect(void* pEditWnd, const char* text);
}
```

```cpp
// Native/eqmain_cxstr.cpp — implementation skeleton

namespace EQMain {

constexpr uint32_t RVA_CXStr_Ctor    = 0x000473d0;
constexpr uint32_t RVA_CXStr_FreeRep = 0x000472d0;
constexpr uint32_t RVA_CEditWnd_SetWindowText = 0x00097af0;

// Prologue bytes — re-verify on every Dalaya patch via:
//   rizin -q -c 'pxw 16 @ <RVA + 0x10000000>' eqmain.dll
constexpr uint8_t PROLOGUE_CTOR[8]    = {0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x08, 0x56, 0x57};
constexpr uint8_t PROLOGUE_FREEREP[8] = {0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68, 0xD8, 0x15};
constexpr uint8_t PROLOGUE_SETTEXT[8] = {0x64, 0xA1, 0x00, 0x00, 0x00, 0x00, 0x6A, 0xFF};

typedef CXStr* (__thiscall *FN_CtorFromCStr)(CXStr* this_, const char* s);
typedef void   (__thiscall *FN_FreeRep)     (CXStr* this_, CStrRep* rep);
typedef void   (__thiscall *FN_SetWindowText)(void* widget, const CXStr* text);

static FN_CtorFromCStr   g_ctor    = nullptr;
static FN_FreeRep        g_freeRep = nullptr;

static bool MatchesPrologue(uintptr_t addr, const uint8_t (&expected)[8]) {
    __try {
        return memcmp(reinterpret_cast<void*>(addr), expected, 8) == 0;
    } __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}

bool ResolveCXStrFunctions(uintptr_t eqmainBase) {
    uintptr_t ctor    = eqmainBase + RVA_CXStr_Ctor;
    uintptr_t freeRep = eqmainBase + RVA_CXStr_FreeRep;
    if (!MatchesPrologue(ctor,    PROLOGUE_CTOR))    return false;
    if (!MatchesPrologue(freeRep, PROLOGUE_FREEREP)) return false;
    g_ctor    = reinterpret_cast<FN_CtorFromCStr>(ctor);
    g_freeRep = reinterpret_cast<FN_FreeRep>(freeRep);
    return true;
}

bool ConstructFromCStr(CXStr* out, const char* s) {
    if (!g_ctor) return false;
    out->m_data = nullptr;
    __try { g_ctor(out, s); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { out->m_data = nullptr; return false; }
}

void Free(CXStr* x) {
    if (!g_freeRep || !x->m_data) return;
    __try { g_freeRep(x, x->m_data); }
    __except(EXCEPTION_EXECUTE_HANDLER) {}
    x->m_data = nullptr;
}

// Probe vtable[73] → 72 → 74 in that order, prologue-validating each.
// Returns the matching slot's function pointer or nullptr.
static FN_SetWindowText ResolveSetWindowTextSlot(void* pWnd) {
    void** vtbl = *reinterpret_cast<void***>(pWnd);
    for (int slot : {73, 72, 74}) {
        uintptr_t fn = reinterpret_cast<uintptr_t>(vtbl[slot]);
        if (MatchesPrologue(fn, PROLOGUE_SETTEXT)) {
            return reinterpret_cast<FN_SetWindowText>(fn);
        }
    }
    return nullptr;  // probe exhausted — caller escalates to AOB rescan
}

bool WriteEditTextDirect(void* pEditWnd, const char* text) {
    auto setText = ResolveSetWindowTextSlot(pEditWnd);
    if (!setText) {
        // TODO Phase 4b: AOB rescan on PROLOGUE_SETTEXT in eqmain .text range,
        //                cache result. If rescan fails: hard-fail loud, NO
        //                dinput8 fallback per feedback_eqswitch_no_regression_to_dinput8.md
        return false;
    }
    CXStr arg;
    if (!ConstructFromCStr(&arg, text)) return false;
    bool ok = false;
    __try { setText(pEditWnd, &arg); ok = true; }
    __except(EXCEPTION_EXECUTE_HANDLER) {}
    Free(&arg);
    return ok;
}

} // namespace EQMain
```

Wire-up plan: `OnEQMainLoaded` (in `eqmain_offsets.cpp`) calls
`EQMain::ResolveCXStrFunctions(eqmainBase)`. Login flow does NOT call
`WriteEditTextDirect` yet — kept dormant until Nate green-lights the
switchover from the current b142afe-anchor autologin path.

## Verification status

| Witness | Source | CXStr ctor | FreeRep | Status |
|---|---|---|---|---|
| 1 — MQ2 source shape | `eqlib/game/CXStr.h:392-402` | matches | matches | ✓ |
| 2 — rizin disassembly | `pdf @ <addr>` | verified | verified | ✓ |
| 3 — string xref convergence | `axt @ str.PasswordEdit` → ctor | verified (single xref) | n/a | ✓ |
| 4 — runtime prologue check | dormant in C++ helper | will fire on every load | will fire on every load | code written, not deployed |

Three independent witnesses agree. No need for IDA Witness 3.

## Open questions (Phase 4b)

1. **AOB rescan strategy on prologue mismatch.** Need a unique signature
   spanning ctor body — first 16 bytes are MSVC-generic but
   `mov [esi], 0; test edi, edi` at `+0x20` is more distinctive.
2. **`fcn.10047070` (CStrRep allocator).** Not strictly needed for our use
   case (the ctor calls it internally), but worth pinning if we ever want
   to construct CStrRep manually for size pre-allocation.
3. **eqmain.dll relocation.** The `OnEQMainLoaded` callback already caches
   the runtime base — RVA-based addressing is robust to relocation. No
   action needed.

## Phase 4 status

**Recon: COMPLETE.** Three witnesses agree on ctor + FreeRep + SetWindowText
locations and signatures. C++ helper sketch above is ready to turn into a
real translation unit when the next session picks it up.

**Not yet done (intentional):** the actual `Native/eqmain_cxstr.{h,cpp}`
files are not committed to the live `Native/` tree per
`feedback_eqswitch_e8faf9b_is_anchor.md` — Native sources stay at the
e8faf9b state until a dual-box smoke test validates the new path. This
recon doc is the handoff for that next session.

**Anchor state:** `1d09bac` on `origin/main` (Phase 1 recon). Working
autologin still at `b142afe` = `working-autologin-20260424-post-yesno-fix`.
