# v7 Research — Dalaya eqmain.dll Reverse Engineering

**Status as of 2026-04-15, post-hotfix v6f (HEAD 960dfd6)**

Goal: replicate MQ2's battle-tested autologin-plugin architecture on Dalaya's custom 32-bit build of EQ. The core technique is a MinHook detour on `LoginController::GiveTime` so our autologin state machine runs inside EQ's game loop (not on the message pump — that's what v5/v6 had to band-aid around).

End-user vision (verbatim from Nate): *"ideally the password and server select and char select screens all are just 'fake invis' windows that eqswitch/mq2 is able to push values to and the user is locked out of it until it gets into the world (unless autoenterworld is off)"*

## What we know

### Dalaya eqmain.dll basics
- **Path:** `C:\Users\nate\proggy\Everquest\Eqfresh\eqmain.dll`
- **Size:** 1,346,560 bytes
- **Bitness:** PE32 / i386 (**32-bit**) — confirmed via `file` and PE header scan
- **Image base:** 0x10000000
- **Sections:** `.text` (VA 0x1000, size 0xFDAE3), `.rdata`, `.data`, `.rsrc`, `.reloc`
- **Lifecycle:** eqmain.dll is loaded ONLY during login → server-select → charselect phases. Once in-world, it's unloaded. Any live probing must happen while the client is at login/server/charselect.

### MQ2 stock ROF2 offsets — **DO NOT APPLY**
MQ2's published `EQMain__LoginController__GiveTime_x = 0x180016640` is a **64-bit** offset, targeting current TLP/Live EQ. Dalaya is 32-bit.

The 32-bit ROF2 reference MQ2 documents in comments (`macroquest-rof2-emu/src/eqlib/src/game/Globals.cpp:1660-1665`) puts the function at `.text:10014B00` with signature:
```
10014B00: ?? ?? ??              ; 3-byte preamble
10014B03: E8 ?? ?? ?? ??        ; CALL LoginController::ProcessKeyboardEvents
10014B08: ?? ?? ??              ; 3 bytes between
10014B0B: E9 ?? ?? ?? ??        ; JMP LoginController::ProcessMouseEvents (tail call)
```

**Dalaya static scan disproves this offset.** At `eqmain.dll + 0x14B00` Dalaya has:
```
0C 50 E8 C9 28 03 00 8B 4E 18 8B 87 EC 01 00 00
```
That's a `MOV ECX, [ESI+0x18]` / `MOV EAX, [EDI+0x1EC]` pattern — not the CALL+JMP tail-call. **Dalaya's eqmain.dll diverges from stock ROF2. MQ2's hardcoded offsets cannot be used directly.**

See: `X:\_Projects\EQSwitch\_.diagnostics\eqmain_scan.py` + `eqmain_scan.txt` for full static analysis output.

### Strings present in Dalaya's eqmain.dll
The symbols DO exist — just at different addresses. String scan found (file offsets):
- `LoginServerAPI` @ 0x0012C6C0
- `JoinServer` @ 0x0012D88A
- `CEditWnd` @ 0x00133500
- `CXWndManager` @ 0x00133428

Next step for v7 is to trace code references to these strings (cross-reference / XREF), which gives addresses of the corresponding functions.

### 41 candidate tail-call-pattern functions found in .text
Static scan for `[3 bytes preamble] + [E8 rel32 CALL] + [3 bytes] + [E9 rel32 JMP]` returned 41 hits across `eqmain.dll .text` — one of them is likely the real `LoginController::GiveTime`. See full list in `eqmain_scan.txt`. Top 5 candidates for closer inspection:
| VA | RVA | CALL target | JMP target |
|---|---|---|---|
| 0x1000751D | 0x0751D | 0x100071F0 | 0x1000776F |
| 0x100128B0 | 0x128B0 | 0x100125E0 | 0x10011FD0 |
| 0x10025E4E | 0x25E4E | 0x100472D0 | 0x10025C55 |
| 0x10094579 | 0x94579 | 0x10094300 | 0x10060250 |
| 0x100788FF | 0x788FF | 0x10004730 | 0x10078825 |

To narrow: identify which of these has XREFs near the `JoinServer` / `LoginServerAPI` strings, or uses the `pLoginController` pointer. Either CE live-session analysis or proper static disasm (IDA/Ghidra) is needed.

### Constraints
- MQ2's eqmain detour approach requires `pLoginController` as the `this` pointer passed into `LoginController::GiveTime`. Finding the instance pointer means finding the global that holds it — the MQ2-equivalent of `EQMain__pinstLoginController_x` (64-bit address `0x18017F4F0` won't apply; need Dalaya's 32-bit equivalent).
- `CEditWnd::SetText` signature (for pushing username/password instantly) needs to be located in eqmain.dll, NOT in eqgame.exe where our existing MQ2Bridge::SetEditText already tries to resolve (and fails for login widgets because of the separate CXWndManager in eqmain — documented in `project_eqswitch_hybrid_login.md` memory note).

## Tooling

### Static scanner
`X:\_Projects\EQSwitch\_.diagnostics\eqmain_scan.py` — Python PE parser + pattern scan. Run anytime, no disruption. Output to `eqmain_scan.txt`.

### Live probe (CE autorun Lua)
`X:\_Projects\_.claude\_tools\cheatengine\src\Cheat Engine\bin\autorun\dump_eqmain_givetime.lua` — fires on CE startup, attaches to first running eqgame.exe, dumps module bases + bytes at candidate offsets + string XREFs + disassembly. Output to `X:\_Projects\EQSwitch\_.diagnostics\eqmain_live_probe.txt`.

**Usage:** Launch an EQ client, leave it at login/server-select/charselect. Start CE (via `powershell.exe -Command "Start-Process X:\_Projects\_.claude\_tools\cheatengine\src\Cheat Engine\bin\cheatengine-x86_64.exe"` — bash `ce.sh` wrapper hits a Windows exec quirk; PowerShell's Start-Process works). The Lua autorun fires, dumps results, shows a message dialog when complete.

### Defender exclusion
`X:\_Projects\_.claude\_tools\cheatengine\` is in `Add-MpPreference -ExclusionPath` (re-added 2026-04-15 after apparent revocation). If CE starts refusing to launch again, `sudo powershell -Command "Add-MpPreference -ExclusionPath 'X:\_Projects\_.claude\_tools\cheatengine\'"`.

## v7 execution plan (next session)

1. **Gather live probe data.** User launches EQ to login screen → CE autorun fires → dumps live eqmain offsets + XREFs to `eqmain_live_probe.txt`.
2. **Identify LoginController::GiveTime in Dalaya.** Cross-reference string XREFs for `JoinServer` with the 41 tail-call candidates. Narrow to 1-2. Verify by checking the CALL target points to something that reads keyboard state (ProcessKeyboardEvents behavior).
3. **Find pLoginController global.** Static: scan `.data` for a pointer that's read by the GiveTime function's `this` register (typically ECX on `__thiscall`). Live: check what value is in ECX on entry to the candidate function.
4. **Find CEditWnd::SetText in eqmain.** Search for `CEditWnd` string XREFs, inspect vtables, or cross-reference against the dinput8 MQ2 exports (if `CEditWnd__SetText_x` is exported).
5. **Write the detour.** Add to `Native/eqswitch-di8.cpp` or a new `Native/login_givetime_detour.cpp`. Use MinHook (already linked). Inside the detour, call `MQ2BridgePollTick()` (or a new `LoginPulseTick()`) then call the trampoline.
6. **Rip out WM_TIMER machinery.** Once the detour reliably pulses on game thread, `SetTimer` / `MQ2TimerProc` / `KillTimer` in `Native/device_proxy.cpp` can be removed.
7. **Wire instant password entry.** In `LoginPulseTick()`, call `CEditWnd::SetText` on the username + password edit controls. Find the controls via `LoginController`'s vtable or a CXWnd::FindChild XML-name lookup ("LOGIN_USERNAME" / "LOGIN_PASSWORD" appear in the string scan list — confirm XML names).
8. **Optional: `ShowWindow(SW_HIDE)` the login/server/charselect windows** for full "fake invis" UX. Restore visibility when `gameState == 5` (in-game).

## Risk / safety discipline for v7

- **Offset mismatch = deterministic crash.** Every login. Verify offsets against Dalaya's live binary (not stock ROF2) before enabling the detour.
- **Fresh branch required.** `feat/v7-givetime-detour` off main. Don't commit v7 work to main until detour is verified on 2+ concurrent test logins.
- **Keep v6e + v6f DLL as fallback.** If v7 breaks a login, revert to `Native/eqswitch-di8.dll` at commit `960dfd6` (v6f). That DLL is deployed at `C:\Users\nate\proggy\Everquest\EQSwitch\eqswitch-di8.dll` with MD5 `65e95354c739c6dd9db1bc1246e88a61`.

## Current deployed state (don't break this)

- `EQSwitch.exe` @ `C:\Users\nate\proggy\Everquest\EQSwitch\` — v6d (commit 6593b1d, MD5 `623f4876...`)
- `eqswitch-di8.dll` @ same — v6f (commit 960dfd6, MD5 `65e95354...`)
- v6e raised WM_TIMER from 500ms→1500ms + initDelay 100→300 ticks (message-pump-pressure fix)
- v6f fixed `CSI_SIZE 0x170→0x160` + name validator rejects UI labels like "Height"
- Team1 launch (natedogg + acpots) stable, both reach charselect, parallel launches clean

## Files / references
- Source: `X:\_Projects\_.src\_srcexamples\macroquest-rof2-emu\src\main\MQ2LoginFrontend.cpp:50-88` (the canonical GiveTime detour)
- Source: `X:\_Projects\_.src\_srcexamples\macroquest-rof2-emu\src\plugins\autologin\StateMachine.cpp:589-729` (full autologin state machine)
- Source: `X:\_Projects\_.src\_srcexamples\macroquest-rof2-emu\src\eqlib\include\eqlib\offsets\eqmain.h` (MQ2's 64-bit offsets — reference only)
- Memory: `project_eqswitch_v7_goal_mq2_givetime_detour.md` (the north-star goal note)
- Memory: `project_eqswitch_hybrid_login.md` (v3.7.0 in-process login lessons — eqmain's separate CXWndManager constraint)
- Memory: `project_eqswitch_chararray_intel.md` (0x160 stride + other live-RPM intel)
- Memory: `project_cheatengine_76_malware_2026_04_11.md` (CE 7.5 build recipe + exclusion setup)
