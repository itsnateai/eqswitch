# New-Session Handoff — EQSwitch Hotfix v5 (Suppress-Flag Fix) + Red-Team/White-Team Verification

Paste the prompt below into a fresh Claude Code session. Assumes nothing from prior sessions.

---

## Prompt

You are continuing work on **EQSwitch**, a C#/.NET 8 WinForms multiboxing tray app for EverQuest (Shards of Dalaya). Repo: `X:/_Projects/EQSwitch/`. Read `CLAUDE.md` first for architecture + conventions — it's authoritative.

**The standard (Nate's words, verbatim):** *"Every feature you build, every bug you fix, every audit you run is a showcase of the absolute best work Claude Code can produce. Every UI element, every interaction, every implementation detail, every verification should reflect top-tier craftsmanship. Don't stop at 'good enough.'"* The user's framing is **"click and never worry like WinEQ2."**

**Your mission: RED-TEAM GO, WHITE-TEAM GO.** Ship hotfix v5 to close the remaining ship-stopper, then dispatch a comprehensive multi-lens verification sweep to confirm the entire login pipeline (credentials → server select → charselect → enter world → in-game) is best-in-class. Fold every verified finding. This builds the solid foundation for Phase 6.

---

### Immediate ship-stopper: "Notepad bleed during BURST 1"

**User symptom:** Nate launched natedogg (triggered auto-login). During credential typing, he started typing in Notepad. The letters he typed in Notepad ALSO showed up in EQ's password field, corrupting it. EQ is stuck at the password screen. Nate confirmed: "I can ONLY type into the password from Notepad WHILE the script is typing in the password field."

**Root cause (verified by code reading):** The DirectInput 8 proxy in `Native/device_proxy.cpp` puts EQ's keyboard device into `BACKGROUND|NONEXCLUSIVE` mode during SHM activation so background injection works. In that mode, `GetDeviceState` reads the **global OS keyboard state** regardless of focus. The DLL's `GetDeviceState` call site at `Native/device_proxy.cpp:434` already has a defense — `if (KeyShm::ShouldSuppress()) memset(lpvData, 0, kbLen);` — which zeros the real keyboard state before ORing synthetic SHM keys on top. But:

- `KeyInputWriter.Activate(int pid, bool suppress = false)` in `Core/KeyInputWriter.cs:114` defaults `suppress` to **false**.
- All three call sites use the default: `Core/AutoLoginManager.cs:344`, `:378`, `:651`.
- Therefore `KeyShm::ShouldSuppress()` always returns false.
- Therefore the real keyboard state is never zeroed during BURST.
- Therefore Nate's Notepad keys OR into EQ's synthetic key stream during BURST 1 / BURST 2 / PulseKey3D.

The defense-in-depth mechanism was fully implemented years ago and simply never turned on. This is the final phantom-key bleed vector not closed by hotfixes v1-v4.

**Required fix (hotfix v5):**

Change the three `writer.Activate(pid)` call sites in `AutoLoginManager.cs` to `writer.Activate(pid, suppress: true)`. Alternative: change the default parameter value in `KeyInputWriter.cs` to `true`. Prefer the explicit-site approach — it signals intent at each auto-login burst without silently changing default semantics for any future caller.

**Expected outcome:** During BURST 1/2/PulseKey3D, physical keys pressed anywhere (Notepad, browser, File Explorer) are suppressed from reaching EQ's DirectInput state. Our synthetic keys reach EQ cleanly. User can type in Notepad during auto-login with zero bleed.

---

### Current state (committed to `main` locally, NOT pushed)

HEAD is `846bfed` (hotfix v4). **5 hotfix commits pending push** (bash tool permission layer is denying `git push` — user must push manually):

```
846bfed fix(login): fold final-sweep agent findings (hotfix v4)
e982ed5 fix(input): fold silent-failure review findings (hotfix v3)
b61c9e6 fix(input): gate IAT keyboard-state hooks on true foreground (v2)
f8550e7 build(native): rebuild eqswitch-di8.dll for 691c71e
691c71e fix(input): gate SHM key reads on active flag (phantom-keys hotfix)
```

**What each hotfix did:**
- **v1 (`691c71e`):** `Native/key_shm.cpp` `InjectKeys` + `ReadKeys` now gate on SHM `active` flag. `device_proxy.cpp` now restores DI8 cooperative level on active→inactive transition. `KeyInputWriter.Deactivate` zeros keys buffer. Closed: stale-byte injection during deactivation windows.
- **v2 (`b61c9e6`):** `Native/iat_hook.cpp` keyboard-state hooks (`GetAsyncKeyState` / `GetKeyState` / `GetKeyboardState`) now gate on true foreground via `g_ntGetForegroundWindow` (win32u.dll syscall wrapper bypassing our own GetForegroundWindow hook). `KeyInputWriter.Reactivate` zeros buffer. Closed: Win32 keyboard-state API bleed.
- **v3 (`e982ed5`):** Write-order flip in `KeyInputWriter` — zero buffer BEFORE clear active across Deactivate/Close/Reactivate/Dispose. `mq2_bridge.cpp:1103` now treats `gameState == -99` (SEH fallback) same as 5 for phantom-click guard. `device_proxy.cpp` `g_coopSwitched` flag flips gated on `SUCCEEDED(hr)`. `AutoLoginManager.cs:677-679` empty catches replaced with logged catches.
- **v4 (`846bfed`):** HIGH-A abort when MQ2 bridge doesn't initialize (was falling through to wrong-character enter). HIGH-D `MQ2BridgePollTick` reentrancy guard via `InterlockedCompareExchange` (prevents double enter-world click). HIGH `MQ2Bridge::Shutdown` now resets `g_cachedSlotCount` + `g_heapScanArrayBase`. ActivateThread rising-edge `SetCoop` now gates `g_coopSwitched=true` on `SUCCEEDED(hr)` (symmetric to v3). `WaitForScreenTransition` timeout now surfaces to user. AltGr-char skip now logs. "Lost EQ window" messages now include account name. 5 new `KeyInputWriterTests` + 7 new `ShmLayoutTests` lock in the hotfix contract. Manual smoke-test checklist at `_tests/smoke/manual-login.md`.

**Build:** 0 errors, 1 expected `[Obsolete]` warning at `UI/TrayManager.cs:~1726` (goes away in Phase 6).

**Test suite (34 tests):**
- `--test-character-selector` → 10 cases
- `--test-config-validate` → 3 cases
- `--test-key-input-writer` → 5 cases (guards hotfix v3 write-order contract)
- `--test-shm-layout` → 7 cases (guards C#/C++ SharedKeyState parity)
- `bash _tests/migration/run_fixtures.sh` → 9 fixtures

**Phantom-click defense gates (MUST stay at 2/1):**
- `grep -c "gameState == 5" Native/mq2_bridge.cpp` → 2
- `grep -c "result == -2" Core/AutoLoginManager.cs` → 1

**Deployed at `C:/Users/nate/proggy/Everquest/EQSwitch/`:**
- `EQSwitch.exe` ← must match `bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe`
- `eqswitch-di8.dll` ← must match `Native/eqswitch-di8.dll` (MD5 verifiable)
- `eqswitch-hook.dll` ← separate native hook, unchanged in hotfix chain

---

### Hotfix v5 scope (minimal — the immediate fix)

Fold in ONE commit:

1. **`Core/AutoLoginManager.cs:344`** — `writer.Activate(pid)` → `writer.Activate(pid, suppress: true)`
2. **`Core/AutoLoginManager.cs:378`** — same change
3. **`Core/AutoLoginManager.cs:651`** — same change (inside PulseKey3D outer loop)

Optionally update the XML-doc comment on `KeyInputWriter.Activate` (lines 109-114) to note that auto-login callers should always pass `suppress: true`.

**Commit message suggestion:**
```
fix(login): enable SHM Suppress flag during auto-login bursts (hotfix v5)

User reported that typing in Notepad DURING BURST 1's credential-typing
phase bled into EQ's password field, corrupting it. Root cause: the
DirectInput proxy puts EQ's keyboard into BACKGROUND|NONEXCLUSIVE mode
during SHM activation, so GetDeviceState reads global OS keyboard
state. The DLL has a Suppress flag defense — when set, GetDeviceState
zeros the real keyboard buffer before ORing synthetic keys — but
KeyInputWriter.Activate defaulted suppress=false and all three call
sites used the default. The defense was never turned on.

Fix: pass suppress:true explicitly at all three Activate call sites.
Prefers explicit-site over changing the default to keep intent visible
at each burst boundary.

Closes the final phantom-key bleed vector not addressed by hotfixes
v1-v4. Completes the hotfix chain.
```

**Gate after fix:**
- Build clean (0 errors, 1 expected `[Obsolete]`)
- All 34 automated tests pass
- Phantom-click gates unchanged
- Native DLL does NOT need rebuilding (this is pure C# change)
- Smoke test: type in Notepad during auto-login → verify zero bleed

---

### After hotfix v5 ships — RED-TEAM/WHITE-TEAM verification sweep

**User's mandate:** *"please send multiple different types of agents to verify our code and lets fix all the errors. is best in class ready to ship completed."*

Dispatch **five** parallel review agents, each with a distinct lens. After they return, **VERIFY EVERY SUGGESTION AGAINST ACTUAL CODE** before folding anything — the user explicitly wants suggestions verified, not blindly applied.

**Agent 1: `feature-dev:code-reviewer`** — cold-read bug/logic/security review of the full login pipeline (`Core/AutoLoginManager.cs`, `Core/KeyInputWriter.cs`, `Core/CharSelectReader.cs`, `Native/mq2_bridge.cpp`, `Native/device_proxy.cpp`, `Native/iat_hook.cpp`, `Native/key_shm.cpp`). Confidence ≥ 80% on HIGH/MEDIUM.

**Agent 2: `pr-review-toolkit:silent-failure-hunter`** — hunt silent failures in phase transitions (login → server → charselect → enter world → in-game) and cleanup paths (finally block, Shutdown, Dispose). Specifically look for post-hotfix-v5 remaining bleed vectors.

**Agent 3: `feature-dev:code-explorer`** — state-machine trace. Compare to WinEQ2 behavior. Map every loose-end past server-select specifically (the user's named concern). Flag BLOCKERS vs ROUGH EDGES vs POLISH.

**Agent 4: `pr-review-toolkit:pr-test-analyzer`** — coverage gap analysis against the v5 diff. Recommend regression tests for the Suppress-flag contract specifically.

**Agent 5: `superpowers:code-reviewer`** — quality review of hotfix v5 diff.

**Red-team angle:** explicitly ask agents to think adversarially. "What would break this?" "What happens on a slow machine?" "What happens with 4 parallel logins?" "What happens if EQ crashes at every phase boundary?" "What if the user's password contains unusual characters?"

**White-team angle:** explicitly ask agents to think constructively. "What's the path to WinEQ2 parity?" "What diagnostics would help users?" "What makes this a joy to use?"

---

### Verification discipline (MANDATORY)

Before folding any finding:

1. **Read the actual code.** Don't trust the agent's claim about line numbers or behavior.
2. **Run the code path in your head.** Trace from entry to exit. Verify the bug is real.
3. **Check for existing defenses.** Some findings turn out to be already-addressed by other code.
4. **Confirm fix direction.** Sometimes the agent proposes the wrong fix even when the bug is real.

Record dispositions in the commit message:
- **Folded** — bug verified, fix applied.
- **Dismissed with reason** — bug claim verified as wrong, why.
- **Deferred to Phase 6** — bug real but out of hotfix scope.

---

### Phase 6 backlog (EXPLICITLY DO NOT ADDRESS — defer with rationale)

These are tracked for the next phase and MUST NOT be folded into the hotfix chain:

1. **Wire `Native/login_state_machine.cpp`** — fully implemented but zero callers. Would give WinEQ2-parity bad-password detection (currently takes 2-3 minutes and reports misleading error), widget-based phase gates (replaces fixed-duration sleeps), and cleaner retry. This is the single biggest UX win available.
2. **`WriteServerToIni` parallel-launch race** — `File.ReadAllLines` + `File.WriteAllLines` on shared INI with no lock. Last-writer-wins on parallel team launches. Fix: per-process INI strategy OR file lock.
3. **`g_eqHwnd` lifecycle** — never cleared, can become a stale HWND after EQ window recreation. Coop-restore could act on destroyed/recycled window.
4. **`g_realKeyboardDevice` UAF** — never nulled when DeviceProxy is released. ActivateThread could Unacquire() on freed memory if EQ recreates its keyboard device mid-session.
5. **`g_origWndProc` dangling pointer** — subclass re-install race with EQ's WndProc overwrites could leave a dangling function pointer. Theoretical crash vector.
6. **Cancellation token** — no way to abort a running auto-login (can take 3+ minutes on failure paths).
7. **Progress indication** — silent 5-60s windows between phase transitions hurt user trust.
8. **Post-selection slot verification** — `RequestSelectionBySlot` acks the request but doesn't read back `GetCurSel` to verify. Wrong-character risk if MQ2's `SetCurSel` silently failed.
9. **Dead code cleanup:**
   - `AutoLoginManager.cs`: `SendKeyEvent`, `SendPressKey`, `SendTypeString`, `BringToForeground`, `RestoreForeground` — zero callers, stale class-level doc comment says "foreground flash + SendInput" which describes none of the live paths.
   - `Core/LoginShmWriter.cs` — fully implemented, zero callers (belongs to the dormant state-machine path).
   - `Native/login_state_machine.cpp` + `Native/login_shm.h` — compile into DLL but never invoked.
10. **Version bump + tag release** — `EQSwitch.csproj` should bump to `3.10.2` or similar to mark the hotfix-chain release.

---

### Hard rules (carry forward)

- **Stage specific files.** Never `git add -A`.
- **Conventional commits, titles under 72 chars.**
- **No emojis** in code, comments, commits (except existing tray-menu surrogate-pair escapes).
- **Phantom-click defense gates** must stay: `gameState == 5` ×2, `result == -2` ×1.
- **`StringComparison.Ordinal` for config-to-config name compares**, `OrdinalIgnoreCase` for heap-to-config compares (the CharacterSelector contract).
- **DI8 DLL tracked in repo** at `Native/eqswitch-di8.dll`. After any `Native/*.cpp` change, rebuild via `bash Native/build-di8-inject.sh` and commit the binary alongside. After any `Core/*.cs` change only, no DLL rebuild needed.
- **Deploy target:** `C:/Users/nate/proggy/Everquest/EQSwitch/`. Kill any running `EQSwitch.exe` before copying. MD5 must match between repo and deployed binary.
- **User push is blocked** — bash tool denies `git push`. User pushes manually. Focus on local commits; do not worry about the push.

---

### Execution checklist

1. Read `CLAUDE.md`.
2. Read this handoff.
3. Verify the current HEAD matches `846bfed` and gates are green:
   ```bash
   cd /x/_Projects/EQSwitch
   git log --oneline -6
   dotnet build --no-incremental 2>&1 | tail -3
   ./bin/Debug/net8.0-windows/win-x64/EQSwitch.exe --test-character-selector; echo "exit: $?"
   ./bin/Debug/net8.0-windows/win-x64/EQSwitch.exe --test-key-input-writer; echo "exit: $?"
   ./bin/Debug/net8.0-windows/win-x64/EQSwitch.exe --test-shm-layout; echo "exit: $?"
   bash _tests/migration/run_fixtures.sh 2>&1 | tail -3
   echo "gameState: $(grep -c 'gameState == 5' Native/mq2_bridge.cpp)"
   echo "result: $(grep -c 'result == -2' Core/AutoLoginManager.cs)"
   ```
4. **Ship hotfix v5** (the Suppress-flag fix). Rebuild C# only (no DLL change). Deploy to live install. Verify deployed exe matches publish output.
5. **Dispatch the 5-agent verification sweep** in parallel (background mode).
6. Wait for all 5 to complete. Consolidate findings.
7. **Verify each suggestion against actual code.** Dismiss bogus ones with written reason. Accept real ones.
8. **Fold verified findings** into hotfix v6 (or as many additional commits as are warranted — prefer smaller atomic commits if the findings cluster by concern).
9. **Re-run the full test suite + gates** after each fold.
10. **Smoke-test checklist:** `_tests/smoke/manual-login.md` — specifically the "type in Notepad during login, verify zero bleed" test and the bad-password test.
11. **Summary report** to the user: commits landed, findings folded/dismissed/deferred, remaining Phase 6 backlog, final gate status.

---

### Bar, one more time

*"Every feature you build, every bug you fix, every audit you run is a showcase of the absolute best work Claude Code can produce. Every UI element, every interaction, every implementation detail, every verification should reflect top-tier craftsmanship. Don't stop at 'good enough.'"*

The hotfix chain is 4 commits of iteratively tightening the phantom-key class of bug. v5 is the final close. The verification sweep is the QA pass that proves the login pipeline is ready for Phase 6 to build on.

**RED-TEAM GO. WHITE-TEAM GO. CLAUDE CODE MYTHOS GO.**

Phase 6 will build on this foundation. Make sure the foundation is rock.
