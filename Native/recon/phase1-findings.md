# EQSwitch Combo G — Phase 1 Findings (2026-04-24)

**Target:** `C:/Users/nate/proggy/Everquest/Eqfresh/eqmain.dll` (Dalaya x86 PE32, compiled 2013-05-11)
**ImageBase:** `0x10000000`  **Arch:** i386 / 32-bit / MSVC

## Summary

Three deliverables extracted from static analysis (rizin RTTI + disassembly), cross-referenced against MQ2 RoF2 canonical class layout:

1. **`CEditBaseWnd::InputText` byte offset:** `0x1ec`
2. **Redraw trigger:** two consecutive non-virtual calls post-write (`fcn.10096560` + `fcn.10096f70`), `this` in ECX
3. **Replicable bypass:** call `vtable[73]` (offset `0x124`) — NOT 74 (the 2026-04-16 off-by-one) — which points to `CEditWnd::SetWindowText` at `0x10097af0` in eqmain.dll, avoiding dinput8.dll's broken dispatch entirely

## Class RTTI addresses

| Class | Complete Object Locator | Vtable VA | Type Descriptor |
|---|---|---|---|
| `CEditBaseWnd` | `0x10115904` | **`0x1010bcdc`** | `0x101352d8` |
| `CEditWnd`     | `0x10115958` | **`0x1010be6c`** | `0x101352f4` |

(COLs located by searching `.rdata` for the 4-byte LE value; vtable = COL_ref_addr + 4 per MSVC ABI.)

## Vtable slot identification

Comparing CEditBaseWnd vs CEditWnd slot-by-slot, the latter overrides these slots relative to the base:

| Slot | CEBW (inherited/pure) | CEW (concrete override) | MQ2 canonical name |
|---|---|---|---|
| 1   | `0x10094ff0` | `0x10097c80` | `~CEditWnd` dtor |
| 3   | `0x100cf006` (pure) | `0x10095010` | `Draw` |
| 7   | `0x100cf006` (pure) | `0x10095020` | `DrawCaret` |
| 14  | `0x100cf006` (pure) | `0x10097970` | `HandleLButtonDown` |
| 15  | `0x1005fb30` | `0x10095130` | `HandleLButtonUp` |
| 24  | `0x1005fc50` | `0x10097a30` | `HandleMouseMove` |
| 26  | `0x100cf006` (pure) | `0x100972a0` | `HandleKeyboardMsg` |
| 39  | `0x10004430` | `0x10097d10` | `OnMove` |
| 40  | `0x10004440` | `0x10097c10` | `OnResize` |
| 47  | `0x10062b20` | `0x10095160` | `OnSetFocus` |
| 48  | `0x100ca070` | `0x100951a0` | `OnKillFocus` |
| **73** | `0x100044e0` | **`0x10097af0`** | **`SetWindowText`** ← THE TARGET |
| 87  | `0x10004520` | `0x10097c70` | `GetActiveEditWnd` |
| 89-93 | `0x100cf006` (all pure) | overridden | 5 `CEditBaseWnd` pure virtuals (`GetHorzOffset`, `GetDisplayString`, `GetCaretPt`, `PointFromPrintableChar`, `ResetWnd`) |

**Off-by-one root cause:** Dalaya's May 2013 x86 CXWnd has 89 vtable slots; MQ2's documented RoF2 x64 has 90. One virtual in CXWnd was added between Dalaya's build and MQ2's documented build, shifting every subsequent slot down by 1. This is why `SetWindowText` is at slot **73 (offset 0x124)** on Dalaya, not slot 74 (0x128) per MQ2.

## CEditBaseWnd field layout (Dalaya x86)

Reconstructed from writes/reads observed in `CEditWnd::SetWindowText` body:

| Offset | Field | Type | Evidence |
|---|---|---|---|
| 0x1d8 | eAlign | int | (inferred from layout — 12 bytes before 0x1e4) |
| `0x1dc` | StartPos | int | `mov [esi+0x1dc], eax` at 0x10097bd8 |
| `0x1e0` | EndPos | int | `mov [esi+0x1e0], eax` at 0x10097bd2 |
| `0x1e4` | MaxChars | int | `mov eax, [esi+0x1e4]` at 0x10097b50 (used as truncation limit) |
| 0x1e8 | MaxBytesUTF8 | int | (inferred — gap before InputText) |
| **`0x1ec`** | **InputText** | **CXStr (4-byte ptr to CXStrRep on x86)** | **`lea ecx, [esi+0x1ec]; call CXStr::op=` at 0x10097ba8** |

Corresponding MQ2 LoginFrontend.h x64 layout (0x260–0x278) shows the same structure — only the base offsets differ due to x86 vs x64 pointer sizes in CXWnd parent.

## CEditWnd::SetWindowText body (0x10097af0)

Sequence after CXStr ref-count handling + optional truncation:

```asm
; arg_4h = const CXStr& text (pointer to caller's CXStr)
0x10097ba3  lea   ecx, [arg_4h]
0x10097ba7  push  ecx
0x10097ba8  lea   ecx, [esi + 0x1ec]       ; &this->InputText
0x10097bae  call  fcn.10047590              ; CXStr::operator= — WRITE
0x10097bb3  mov   ecx, esi
0x10097bb5  call  fcn.10096560              ; REDRAW-CALL #1 (this) — likely UpdateDisplayString
0x10097bba  mov   ecx, esi
0x10097bbc  call  fcn.10096f70              ; REDRAW-CALL #2 (this) — likely InvalidateRect-equivalent
0x10097bc1  mov   eax, [esi + 0x1a8]        ; read WindowText CXStrRep*
0x10097bc7  test  eax, eax
0x10097bc9  je    0x10097bd0
0x10097bcb  mov   eax, [eax + 8]            ; eax = WindowText.length
0x10097bd2  mov   [esi + 0x1e0], eax         ; EndPos = length
0x10097bd8  mov   [esi + 0x1dc], eax         ; StartPos = length
```

## Recommended bypass

**Option A (preferred — vtable-dispatch at correct slot):**

```cpp
// Native/eqmain_dalaya_layouts.h
#define CEDITBASEWND_INPUTTEXT_OFFSET   0x1ec
#define CEDITBASEWND_STARTPOS_OFFSET    0x1dc
#define CEDITBASEWND_ENDPOS_OFFSET      0x1e0
#define CXWND_VTABLE_SETWINDOWTEXT_SLOT 73           // offset 0x124 on x86

// Native/mq2_bridge.cpp
void WriteEditTextDirect(void* pWnd, const CXStr& text) {
    void** vtbl = *reinterpret_cast<void***>(pWnd);
    using SetTextFn = void(__thiscall*)(void*, const CXStr&);
    auto fn = reinterpret_cast<SetTextFn>(vtbl[CXWND_VTABLE_SETWINDOWTEXT_SLOT]);
    fn(pWnd, text);
}
```

**Option B (fallback — direct function address, less robust):**

Call `0x10097af0` directly by address. Requires ASLR-aware resolve if eqmain.dll relocates, but Dalaya typically doesn't relocate.

**Option C (lowest-level — full replication):**

Replicate the body ourselves: write `[this+0x1ec]` via CXStr op=, call `0x10096560(this)`, call `0x10096f70(this)`, set `[this+0x1e0]` and `[this+0x1dc]` to length. NOT recommended — skips password-masking logic that lives in the two redraw helpers.

## Cross-validation status

- **Witness 1 — MQ2 headers:** Layout shape matches (pure-virtual count, override pattern, field ordering). ✓
- **Witness 2 — Rizin RTTI + disassembly:** Concrete addresses extracted. ✓
- **Witness 3 — IDA Free 9.3 Hex-Rays:** ⚠ BLOCKED on first-launch EULA (no `--accept-license` flag exists; IDA must be opened in GUI once to accept).
- **Witness 4 — CE/x32dbg runtime:** OPTIONAL (confirms offsets on live process)

Per handoff non-negotiables: three independent witnesses must agree before writing C++.

## Additional finding: eqmain.dll has ZERO exports

Confirmed via `rizin -c "iE"`: zero exported symbols. This means:

- Dalaya's `dinput8.dll` "exports" like `?SetWindowTextA@CXWnd@EQClasses@@QAEXAAVCXStr@2@@Z` are **thunked re-exports** — internally `dinput8.dll` pattern-scans eqmain at runtime and dispatches. **The off-by-one (slot 74 vs 73) lives inside dinput8.dll's pattern resolver, not in eqmain itself.**
- We CANNOT use `GetProcAddress(eqmain, ...)` for ANYTHING in eqmain. Pattern-scan or vtable-walk is the only access path.

## Open question for C++ implementation

`CEditWnd::SetWindowText` takes `const eqmain::CXStr&`, NOT `const EQClasses::CXStr&`. The two are layout-incompatible (per the comment block in `Native/eqmain_offsets.h`: "separate parallel class hierarchy with different member offsets"). The existing `g_fnCXStrCtor` in `mq2_bridge.cpp` constructs an `EQClasses::CXStr` from a const char* — wrong type for our call.

Three implementation strategies:

| Strategy | Difficulty | Risk |
|---|---|---|
| A — Pattern-scan `eqmain::CXStr::CXStr(const char*)` ctor, use it to build the arg | Medium (need a stable signature) | Low — uses real ctor, refcounting correct |
| B — Find a live empty `eqmain::CXStr` in the process (e.g. on the username field already on screen), copy-construct from it via `CXStr::operator=`, then call SetWindowText | Easy | Medium — depends on having a known live CXStr at lookup time |
| C — Construct CXStr/CXStrRep manually in C++ matching observed layout | Hard | High — refcount/alloc semantics easy to get subtly wrong, leaks possible |

Strategy A is cleanest. Discovering the ctor's address is one more rizin pass — search for short functions (~30-60 bytes) that take 2 args, allocate a CXStrRep with `new`, and copy the input string. There's likely a known MQ2 signature for it.

## Phase 1 status

**Static-only static analysis: COMPLETE for the three named deliverables. Ready to proceed once:**
1. IDA Witness #3 done (Path A — Nate accepts EULA, ~30 sec interaction), OR
2. User accepts 2-witness sufficiency (Path B — over-determined override pattern + binary disassembly is strong evidence; harden C++ with prologue-byte sanity check at runtime).

**Phase 4 has additional dependency:** locate `eqmain::CXStr::CXStr(const char*)` for proper arg construction (Strategy A above). This is straightforward but needs another rizin pass. Defer until paths above are decided.
