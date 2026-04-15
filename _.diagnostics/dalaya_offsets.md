# Dalaya eqmain.dll Offsets — v7 GiveTime Detour

**Status:** Phase 1 complete 2026-04-15. Resolved via static PE byte-signature scan
cross-validated against CE live probe at runtime VA `0x71E30000`.

All offsets below are **RVAs** (stable across boots — ASLR shifts the base but not
the relative offsets within the DLL). Runtime address = `GetModuleHandleA("eqmain.dll") + RVA`.

## Resolved symbols

| Symbol | RVA | Default VA | Purpose |
|---|---|---|---|
| `LoginController::GiveTime` | `0x128B0` | `0x100128B0` | Target of MinHook detour. Called from the login main-loop once per frame; gives us a pulse inside EQ's game loop. |
| `LoginController::ProcessKeyboardEvents` | `0x125E0` | `0x100125E0` | Called from GiveTime's body. Informational — we don't hook it. |
| `LoginController::ProcessMouseEvents` | `0x11FD0` | `0x10011FD0` | Tail-called from GiveTime. Informational — we don't hook it. |
| `pLoginController` (`LoginController**`) | `0x150174` | `0x10150174` | Static pointer in `.data` holding the current `LoginController*`. Dereference once per tick inside the detour to get `this`. |

## Evidence

### Static signature match
File: `_.diagnostics/givetime_signature_matches.txt`

MQ2's documented 32-bit ROF2 `GiveTime` prologue (from
`macroquest-rof2-emu/src/eqlib/src/game/Globals.cpp:1657-1665`):

```
56              push esi
8B F1           mov esi, ecx           ; save this
E8 ?? ?? ?? ??  call ProcessKeyboardEvents
8B CE           mov ecx, esi           ; restore this
5E              pop esi
E9 ?? ?? ?? ??  jmp ProcessMouseEvents ; tail call
```

Signature: 16 bytes, 8 fixed (`56 8B F1 E8 ?? ?? ?? ?? 8B CE 5E E9 ?? ?? ?? ??`).
Exactly ONE match in Dalaya's 1,015 KB `.text`, at RVA `0x128B0`. Raw bytes:

```
56 8B F1 E8 28 FD FF FF 8B CE 5E E9 10 F7 FF FF
```

Both rel32 operands resolve to valid nearby `.text` functions (ProcessKeyboardEvents
at RVA `0x125E0`, ProcessMouseEvents at RVA `0x11FD0`).

### Live disassembly match
File: `_.diagnostics/eqmain_live_probe.txt` lines 82-89 (this session, PID 29072,
eqmain base `0x71E30000`):

```
71E428B0 - 56              - push esi
71E428B1 - 8B F1           - mov esi, ecx
71E428B3 - E8 28FDFFFF     - call 71E425E0   ; ProcessKeyboardEvents (RVA 0x125E0)
71E428B8 - 8B CE           - mov ecx, esi
71E428BA - 5E              - pop esi
71E428BB - E9 10F7FFFF     - jmp 71E41FD0    ; ProcessMouseEvents (RVA 0x11FD0)
71E428C0 - C7 01 A402F371  - mov [ecx], 71F302A4  ; next function: ctor (sets vtable)
71E428C6 - C3              - ret
```

Verification agent independently confirmed all five sanity checks.

### pLoginController call-site
File: `_.diagnostics/pLoginController.txt`

Scan found exactly ONE direct CALL site to GiveTime, at RVA `0x10C8D`. The
instruction sequence immediately preceding the call:

```
... 8B 0D 74 01 15 10  E8 <rel32>
    mov ecx, [0x10150174]    ; pLoginController — load this
    call GiveTime
```

`0x10150174` is at default ImageBase 0x10000000, so the pointer itself lives at
RVA `0x150174` in `.data`. At current runtime base `0x71E30000`, that's VA
`0x71F80174`. Dereferencing gives the current `LoginController*`.

## PE section map (eqmain.dll)

| Section | RVA | VSize | Raw | RSize |
|---|---|---|---|---|
| .text  | 0x00001000 | 0xFDAE3 | 0x000400 | 0xFDC00 |
| .rdata | 0x000FF000 | 0x2D1ED | 0x0FE000 | 0x2D200 |
| .data  | 0x0012D000 | 0x21727C | 0x12B200 | 0xAC00  |
| .rsrc  | 0x00345000 | 0x001B4 | 0x135E00 | 0x00200 |
| .reloc | 0x00346000 | 0x12B8C | 0x136000 | 0x12C00 |

## What's still open (not blocking Phase 2)

- **`CEditWnd::SetText`** — needed for Phase 4 (instant credential entry). Located
  in eqmain.dll, likely a virtual method on `CEditWnd`. Not needed to verify the
  detour in Phases 2-3. Static scan found `CEditWnd` class-name string at RVA
  `0x135300` (.data section, MSVC RTTI TypeDescriptor); walk vtable from there
  when Phase 4 rolls around.
- **XML widget names `LOGIN_USERNAME` / `LOGIN_PASSWORD`** — not present as
  literal strings in eqmain.dll (verified via broader string scan in
  `_.diagnostics/eqmain_exports_strings.txt`). Either Dalaya uses different
  XML names or the names live in external .sidl files under
  `C:\Users\nate\proggy\Everquest\Eqfresh\uifiles\default\`. Phase 4 concern.

## Code shape for Phase 2

```cpp
// Inside Native/login_givetime_detour.cpp
uintptr_t eqmainBase = (uintptr_t)GetModuleHandleA("eqmain.dll");
if (!eqmainBase) { /* eqmain not loaded yet — poll from ActivateThread */ return; }

constexpr uintptr_t GIVETIME_RVA          = 0x128B0;
constexpr uintptr_t PLOGINCONTROLLER_RVA  = 0x150174;

void* pGiveTime                  = (void*)(eqmainBase + GIVETIME_RVA);
LoginController** pLoginCtrlPtr  = (LoginController**)(eqmainBase + PLOGINCONTROLLER_RVA);

// MH_CreateHook(pGiveTime, &GiveTime_Detour, (LPVOID*)&g_GiveTime_Trampoline);
// MH_EnableHook(pGiveTime);
```

The detour is `__thiscall`; in MinHook we write it as a free function taking
no args and read `this` out of ECX on entry (or via `__fastcall` convention
with dummy edx). Simpler: don't touch `this` in the detour body — just call
`MQ2BridgePollTick()` and then the trampoline.
