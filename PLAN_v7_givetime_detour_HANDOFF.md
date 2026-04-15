# New-Session Handoff — EQSwitch v7 (MQ2 GiveTime Detour + Headless Login)

Paste the prompt below into a fresh Claude Code session. Self-contained — assumes nothing from prior sessions.

---

## Prompt

You are continuing work on **EQSwitch**, Nate's C#/.NET 8 WinForms multiboxing tray app for EverQuest (Shards of Dalaya emulator). Repo: `X:\_Projects\EQSwitch\`. HEAD: `960dfd6` (hotfix v6f). Read `CLAUDE.md` first — it's authoritative for architecture + conventions.

**The standard (Nate's words, verbatim):** *"Every feature you build, every bug you fix, every audit you run is a showcase of the absolute best work Claude Code can produce. Every UI element, every interaction, every implementation detail, every verification should reflect top-tier craftsmanship. Don't stop at 'good enough.'"*

**The v7 mission, Nate's words:** *"We need to copy MQ2 — they are proven to work and open source — battle tested code. Ideally the password and server-select and char-select screens all are just 'fake invis' windows that EQSwitch/MQ2 is able to push values to and the user is locked out of it until it gets into the world (unless autoenterworld is off)."*

## The engineering goal

Replace EQSwitch's current pump-pressure-prone `SetTimer(hwnd, 500ms, MQ2TimerProc)` architecture with MQ2's battle-tested approach: a MinHook detour on `LoginController::GiveTime` in eqmain.dll. Our autologin state machine runs inside EQ's game loop (not on the message pump) so nothing we do can trigger `IsHungAppWindow=true` and cause Windows to kill eqgame.exe. Then wire **instant password entry** by calling `CEditWnd::SetText` on the username/password controls — no more synthetic keystrokes burst-typing credentials with a 3-second settle.

Optional final polish: `ShowWindow(SW_HIDE)` on login/server-select/charselect windows so the user experiences "click → in world" with no visible login UI (respecting `AutoEnterWorld=false` to still show charselect for manual selection).

## Read these in order before starting

1. `CLAUDE.md` — architecture + conventions (note: "prefer editing existing files", never `git add -A`, conventional commits under 72 chars, explicit `--self-contained` on publish, DI8 DLL lives at `Native/eqswitch-di8.dll` and must be rebuilt + committed after `Native/*.cpp` changes)
2. `X:\_Projects\EQSwitch\_.diagnostics\v7_research_ANALYSIS.md` — all research gathered so far (Dalaya is 32-bit; MQ2's 64-bit stock offsets don't apply; static scan disproves GiveTime at 0x14B00; 41 candidate tail-call functions narrowed from .text)
3. `X:\_Projects\EQSwitch\_.diagnostics\eqmain_scan.txt` — static PE analysis output
4. `X:\_Projects\EQSwitch\PLAN_phantom_keys_hotfix_v5_HANDOFF.md` — the hotfix chain context (v1 through v6f) that preceded v7
5. Source reference: `X:\_Projects\_.src\_srcexamples\macroquest-rof2-emu\src\main\MQ2LoginFrontend.cpp:50-88` (the canonical GiveTime detour) and `src\plugins\autologin\StateMachine.cpp:589-729` (full autologin state machine)

## Current state you're inheriting

- **HEAD `960dfd6`** on `main`, pushed to origin.
- **v6e / v6f stabilization complete.** Team1 launch (natedogg + acpots) goes to charselect reliably in parallel. Settings form works. Alt+I smart-routes to LoginAndEnterWorld.
- **Deployed files at** `C:\Users\nate\proggy\Everquest\EQSwitch\`:
  - `EQSwitch.exe` MD5 `623f48764d7b4f42732d7acc14868d16` (v6d, commit 6593b1d)
  - `eqswitch-di8.dll` MD5 `65e95354c739c6dd9db1bc1246e88a61` (v6f, commit 960dfd6)
- **42+ automated tests passing.** Phantom-click gates at 2/1 unchanged.

## The "don't break this" list

- v6e's 1500ms TIMERPROC interval + 4.8s initDelay are the stabilization reason Nate's logins stopped getting killed by Windows `IsHungAppWindow`. Don't rip these out until the detour is fully replacing the WM_TIMER.
- v6f's `CSI_SIZE=0x160` stride + name validator are load-bearing for char-list display. Don't change them.
- Don't commit to main until the detour is verified on at least 2 concurrent test logins AND a wrong-password test AND a close-client-mid-login test.

## v7 execution plan

### Phase 1: Verify live Dalaya offsets (NO CODE CHANGES)

1. Confirm Defender exclusion for CE: `sudo powershell -Command "(Get-MpPreference).ExclusionPath" | Select-String cheatengine`. If missing, re-add: `sudo powershell -Command "Add-MpPreference -ExclusionPath 'X:\_Projects\_.claude\_tools\cheatengine\'"`.
2. Ask Nate to launch a fresh EQ client and leave it at the login screen (username/password entry screen).
3. Launch CE: `powershell.exe -Command "Start-Process 'X:\_Projects\_.claude\_tools\cheatengine\src\Cheat Engine\bin\cheatengine-x86_64.exe'"`. CE's autorun script `dump_eqmain_givetime.lua` fires automatically (it lives in CE's `autorun/` folder).
4. Read the output at `X:\_Projects\EQSwitch\_.diagnostics\eqmain_live_probe.txt`. It will have eqmain.dll base, string XREFs for `JoinServer` / `LoginController` / `CEditWnd`, and bytes at MQ2's stock-ROF2 offset (which we already know won't match).
5. **Manual narrowing step (Nate may need to help with CE GUI):** in CE, use "Find what accesses this address" on the `JoinServer` string's address. The function that reads that string is `LoginServerAPI::JoinServer`. Scroll up in the function — it's often called from `LoginController::DoServerSelect` or similar, which is called from `LoginController::GiveTime`. Walk the call chain up to the GiveTime function and note its VA.
6. From GiveTime's VA, subtract eqmain.dll's base (0x10000000 typically) to get the RVA. THAT's Dalaya's LoginController::GiveTime RVA. Write it down in `_.diagnostics/dalaya_offsets.md`.
7. Same process for `pLoginController` (find a static pointer written once at startup that holds a `LoginController *`), `CEditWnd::SetText` (vtable slot or exported symbol), and the XML widget names `LOGIN_USERNAME` / `LOGIN_PASSWORD`.

### Phase 2: Prove the detour works in isolation (NEW BRANCH)

1. `git checkout -b feat/v7-givetime-detour main`
2. Create `Native/login_givetime_detour.cpp` + `.h`. Start minimal:
   - On DLL init, resolve eqmain.dll base (use `GetModuleHandleA("eqmain.dll")`; handle null — eqmain may not be loaded yet on DLL entry).
   - `MH_CreateHook((LPVOID)(eqmainBase + DALAYA_LOGIN_GIVETIME_RVA), &GiveTime_Detour, (LPVOID*)&g_GiveTime_Trampoline)`.
   - In the detour: log a single line ("giveTime: tick %d", counter++) then call trampoline.
3. Build, deploy, run one EQ client, let it sit at login. Verify the log shows GiveTime ticks firing at EQ's frame rate (~30-60 Hz). If it crashes or doesn't fire, the offset is wrong — go back to Phase 1.
4. Only after ticks are confirmed, proceed to Phase 3.

### Phase 3: Move MQ2BridgePollTick onto the detour thread

1. Inside the detour, call `MQ2BridgePollTick()` before the trampoline. Existing 500ms throttle inside that function handles rate limiting.
2. Build + deploy. Run a full auto-login. Verify it still reaches charselect cleanly.
3. Rip out `SetTimer` / `MQ2TimerProc` / `KillTimer` / `g_mq2TimerInstalled` from `Native/device_proxy.cpp`. ActivateThread's MQ2BridgePollTick fallback (line 147) stays as a safety net for early-init phases before eqmain is loaded.
4. Revert v6e's TIMERPROC knobs (500ms interval, 100-tick initDelay) — they're obsolete now.
5. Smoke test: 3 parallel logins + a wrong-password test + a close-during-login test.

### Phase 4: Instant credential entry

1. Add `SetLoginCredentials(pid, username, password)` to MQ2Bridge. On call, sets a flag + stashes the strings in SHM.
2. Inside the detour, if the flag is set and `gameState` is at login screen:
   - Resolve pLoginController (static pointer in eqmain's .data).
   - Walk the controller's widget tree via CXWnd::FindChild by name ("LOGIN_USERNAME", "LOGIN_PASSWORD", or Dalaya's actual XML names — verify with Phase 1 data).
   - Call `CEditWnd::SetText` on each edit control.
   - Click the Connect button via `CXWnd::WndNotification` (we already do this for CLW_EnterWorldButton at charselect — same mechanism, different control).
   - Clear the flag.
3. From C# `AutoLoginManager`, call `SetLoginCredentials` instead of the 3-burst typing sequence. Keep the burst path as fallback if the new path fails.

### Phase 5: Optional headless UI

1. After Phase 4 works: add `ShowLoginWindow(bool visible)` to MQ2Bridge. Finds eqmain's main window, calls `ShowWindow(SW_HIDE)` or `SW_SHOW`.
2. From C# AutoLoginManager, hide on login start, show when gameState==5 (in-game) OR when AutoEnterWorld=false and charselect reached (for manual selection).
3. Handle the "user alt-tabs to check progress" case — balloon a tray notification with status ("logging in natedogg... credentials sent"), so the invisible window doesn't feel dead.

## Critical safety rules for v7 (carry forward from v1-v6 chain)

- **No DLL commits without a matching `Native/*.cpp` change.** The DLL is binary-deterministic from source; any DLL commit without corresponding source is a red flag.
- **`--self-contained true` on every publish.** Non-self-contained misses `System.Security.Cryptography.ProtectedData` and DPAPI decrypt silently crashes.
- **Deploy is `C:\Users\nate\proggy\Everquest\EQSwitch\`.** Kill running `EQSwitch.exe` before copying. MD5-verify exe AND dll match publish output.
- **Git push permission is granted** (user confirmed "you should be able to push again" for prior work). Still show what's being pushed.
- **Never rip out a working safety net while its replacement is unverified.** v6e's TIMERPROC stays until Phase 3 smoke test confirms the detour fires.

## If you hit a blocker

- **Offset wrong / detour crashes:** revert the deployed DLL to v6f MD5 `65e95354c739c6dd9db1bc1246e88a61`, go back to Phase 1 with more CE probing.
- **eqmain.dll not found at init:** eqmain only loads when the client reaches login screen, not at process start. Install the detour lazily — watch for `LoadLibraryA("eqmain.dll")` via a ModuleLoaded hook or poll `GetModuleHandleA` from ActivateThread.
- **`LoginController *` changes across sessions (ASLR or runtime allocation):** don't detour against a resolved instance. Detour the function directly (offset is stable in the DLL), and read `this` out of ECX on entry (`__thiscall` calling convention).
- **CEditWnd::SetText unresolvable:** fall back to our existing SendInput burst approach for credentials. Instant entry is Phase 4 polish, not a Phase 1-3 blocker.

## Definition of done

1. `git show feat/v7-givetime-detour --stat` shows the new `Native/login_givetime_detour.cpp/.h` + removals in `Native/device_proxy.cpp`.
2. Running the deployed build produces zero Windows `Application Hang` Event 1002 entries across 10+ logins.
3. A typical login goes credentials→charselect in <5 seconds (instant password entry makes this dramatic).
4. Parallel team launches (natedogg + acpots) complete without any SetTimer pump pressure in the device_proxy code path.
5. Optional: `ShowWindow(SW_HIDE)` headless mode can be toggled via a Settings checkbox.

## One last word

v6e/v6f were the emergency-stabilization band-aids. v7 is the permanent fix that matches MQ2's proven architecture. Every line of MinHook glue, every offset resolved, every state-machine tick handler — make it a showcase of Claude Code's best work. The foundation is rock, built carefully across 6 hotfixes. v7 stands on it.

**RED-TEAM GO. WHITE-TEAM GO. CLAUDE CODE MYTHOS GO.**
