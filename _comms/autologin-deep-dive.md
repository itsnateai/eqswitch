# Autologin Deep-Dive — for Cloud Claude review

**Status:** review-only PR. Zero source changes. The diff is this document.
**Branch:** `review/autologin-deep-dive` off `main` (HEAD `0c72bbe`, v3.14.8 shipped 2026-05-01).
**Reviewer expectations:** read this end-to-end, then walk the file/line anchors. Branch tip is current `main` — every line ref resolves at HEAD.
**Edit log:** v2 (this revision) folds in pre-flight findings from 5 reviewer agents (3 Sonnet + 2 Opus). Items listed in `## 0. Pre-flight corrections` below.

---

## 0. Pre-flight corrections (v1 → v2)

Before we shipped this to cloud review, 5 pre-flight reviewer agents found errors in v1. The corrections are folded in below. We list them here transparently so cloud reviewers know where v1 was misleading and don't waste cycles re-deriving:

- **§2 typing-duration math was wrong.** v1 expected ≈115ms, actual coded floor is 40ms/char × 6 chars = **240ms** (`Core/AutoLoginManager.cs:1411,1424`). Observed 474ms ÷ 6 = 79ms/char ≈ 2× coded floor — normal PostMessage scheduler variance, **not** evidence of a stall. The "what's eating the rest?" rhetorical hook in v1 collapsed.
- **§2 modal-collision hypothesis was overconfident.** Zero log evidence of any modal in `eqswitch.log:3463–3578`. The retry succeeds with byte-identical credentials → any hypothesis requiring BURST 1's payload to be wrong must explain why retry's identical payload works. v2 ranks H1 (DI8 coop-level not yet BACKGROUND) and H3 (stale-session 30s release) **above** the modal theory because both are already documented as known failure modes in our own code comments.
- **§2 wrong line range.** Cited `login_state_machine.cpp:399-415` for `PHASE_WAIT_CONNECT_RESP`; actual handler is `:458-474`. Lines `:399-415` are the `PHASE_CLICKING_CONNECT` widget-discovery + vtable validation. The error originated in a stale comment in `Core/AutoLoginManager.cs` itself — fixed in v2 with a margin note.
- **§3 iteration count inflated by ~40%.** v1 said "55 named iterations" (and 56 was the actual git grep count). Of 103 commits on the four files, only **11** carry literal `iter N` / `combo-g iter` markers on credential entry; **22** are charselect / enter-world / MQ2-bridge work (different sub-problem). The arch-smoke argument still stands but the count needs honesty.
- **§4 PATH C "designed but never built" was factually wrong.** The detour transport SHIPPED in v3.12.0 — `Native/login_givetime_detour.cpp` (214 LOC) is live. MinHook on `LoginController::GiveTime` at RVA `0x128B0` with prologue gate at `:128-135`. What's missing is the **FSM strategy port** from MQ2's `StateMachine.cpp:111+` — the Wait/Connect/ServerSelect classes. Reframed.
- **§4 PATH C had no offset-fallback risk register.** `:29` hardcodes a single RVA, `:128-135` refuses install on first-byte mismatch (`g_installAttempted = true`, permanent). No AOB pattern-scan. Any Dalaya eqmain.dll patch silently disables autologin until rebuild. Added as a major risk in v2.
- **§4 "contradiction" between `:350` and `:553` wasn't one.** `:553` is stale commentary; `:606-609` is the current truth ("CXStr write at +0x1A8 doesn't reach EQ's render/submit buffer"). The Combo G write reaches *some* buffer (line 553's "works") but not the render/submit buffer (line 350/606's "silent-no-op"). v2 reframes Q-A2 around comment rot, not a logical contradiction.
- **§4.5 "Coupling map" missing.** v1 admitted only the wallclock side-effect coupling (PATH A's 45s → PATH B's DI8 settle). Two more couplings exist: SHM-activation triggers IAT hooks + DI8 coop-level switch (not just wallclock); `device_proxy.cpp` is shared infra for hotkey switching, NOT autologin-only. Deletion plan in Q4 was too clean. Added §4.5.
- **PATH D missing entirely.** v1 paraphrased the code comment that names options (a) and (b) and inherited its blind spot. Option (c) — explicit `Sleep(WarmupDwellMs)` after CANCEL, before Activate — decouples A from B at zero cost (~20 LOC C#, no Native rebuild). Added as PATH D.
- **§1 framing too absolute.** User's original "one path, no backup" stance is wrong for resilience reasons: PATH B's pure Win32 SendInput keystrokes are the only Dalaya-eqmain.dll-patch-resilient credential-entry path. PATH A and PATH C both depend on EQ's internal struct layouts. Reframed §1 to invite "one primary + one Win32-only floor" as a candidate answer.
- **Missing references in §5 reading guide.** `Native/login_givetime_detour.cpp`, `Native/mq2_bridge.cpp` (3637 LOC, owns MQ2BridgePollTick), `Native/iat_hook.cpp:171-189`, `Native/device_proxy.cpp:586-625`. SHM contract files (`login_shm.h` + `key_shm.h`). Test surface: `*Tests*.cs` (5 files exist; cited in §10).
- **Broken relative path** to memory file in v1 (`../../../.claude/...` resolves off-drive). Replaced with `(#)` placeholder like the other anchor refs.

---

## 1. The ask

We have **two parallel password-entry paths** in production today, both flawed, plus a **third architectural approach** (v7 GiveTime detour) whose **transport** shipped in v3.12.0 but whose **FSM strategy** has never been ported. The two live paths fight each other through three coupling points; we keep one alive only because the other's wasted-time-while-failing accidentally settles a DirectInput state machine. **11 named credential-entry iterations** plus 22 charselect-side fixes in 25 days of file churn. That's the smell of an architecture problem, not a sequence of bugs — but the *count* alone isn't the proof. The chesterton-fence index in §6 is.

**The decision we want from you:**

> Today autologin took 3:12 instead of the 5s spec, on the same retry path that has been the de-facto workhorse for 7 days. The credential-entry hot path now spans three implementations (in-process SHM widget write, DI8 keystroke burst, and a partially-built GiveTime detour), with a load-bearing comment at `Core/AutoLoginManager.cs:548-580` admitting the disabled PATH A is structurally coupled to PATH B's success via wallclock side effect.
>
> **Decision needed:** does the GiveTime detour (shell shipped, FSM port pending) plus a clean post-CANCEL `Sleep(WarmupDwellMs)` decouple A→B sufficiently to keep PATH B as a Dalaya-eqmain.dll-patch fallback, or is full PATH-C migration with PATH-B deletion correct? We are open to "keep one Win32-only fallback for resilience" if the audit supports it; we want to stop iterating on PATH A.

We want to stop iterating. Not necessarily delete every backup — the resilience case for keeping PATH B as a Win32-only floor is real and called out in §4.

---

## 2. Symptom — today's 3-minute team-login failure

**Build:** v3.14.8, deployed `C:\Users\nate\proggy\Everquest\EQSwitch\EQSwitch.exe` (md5 not captured, file mtime 2026-05-01 20:00).
**Native DLLs deployed:** `eqswitch-di8.dll` + `eqswitch-hook.dll`, mtime 2026-05-02 00:26 (rebuild from `Native/` source after v3.14.8 publish).
**User action:** `Ctrl+Alt+Shift+F9` (team1 hotkey) at 2026-05-02 07:44:03 local. Two clients, gotquiz + gotquiz1, both Dalaya.

Log path: `C:\Users\nate\proggy\Everquest\EQSwitch\eqswitch.log` lines 3463–3578.

```
07:44:03.481  FireTeam(1): Slot 1 'Toby' → Character 'Toby' → enter world
07:44:03.532  created suspended PID 23864 for gotquiz
07:44:04.686  FireTeam(1): Slot 2 'nate' → Account 'gotquiz1' → charselect
07:44:04.705  created suspended PID 21708 for gotquiz1
07:44:11.004  DLL gameState ready 2193ms (PID 21708)        ← warmup gate cleared
07:44:11.004  LoginShmWriter: LOGIN command sent (user='gotquiz1', server='Dalaya', char='', seq=1)
07:44:13.159  warmup phase advanced to ClickingConnect (PID 23864) ← DLL is now actively
07:44:13.159  Warmup done — settling…                                clicking the Connect btn
07:44:13.374  warmup phase advanced to ClickingConnect (PID 21708)
07:44:17.168  CANCEL command sent → BURST 1 activated → Submitting login → BURST 1 deactivated
              (same sequence ~200ms later for PID 21708)
07:45:53.073  ⚠ screen transition timeout 90000ms (both PIDs, 187ms apart)
07:45:53.073  attempting stale-session recovery (one-shot retry)
07:46:23.863  Retry: typing credentials… → submit → 60s wait → ✓ char select reached
07:46:55.719  charselect ready, hwnd=0x380ADE for PID 21708
07:46:55.970  charselect ready, hwnd=0x180D4E for PID 23864
```

**3 minutes 12 seconds from hotkey to char select.** Spec says (CLAUDE.md, "AUTOLOGIN SPEC"):

> "For each client, **within the first 5 seconds** after the login screen is ready: type password → Enter → Enter → char select."

### Failure mode — six candidate hypotheses, ranked

The retry path that succeeded shows *"EQ window hung (loading) … 4581ms"* then *"responsive after loading … 24963ms"* (eqswitch.log:3587-3590). That is a real 3D charselect scene loading — not a dialog dismissal. **Whatever changed between attempt 1 and attempt 2, it lets the byte-identical credential burst go through.** Any hypothesis that requires mutating BURST 1's payload (e.g., "modal eats characters") needs to explain why retry's identical burst, fired into a presumably identical UI, succeeds.

| # | Hypothesis | Evidence for | Evidence against | Decisive test |
|---|---|---|---|---|
| **H1** | DI8 cooperative level not yet `BACKGROUND` when BURST 1 fires; keystrokes typed into a foreground-only DI device → EQ's `GetDeviceState` returns zeros for password chars | BURST-1-truncation chesterton at `Core/AutoLoginManager.cs:566-569` proves this exact failure mode is known. The retry is fired at T+~140s — coop has long since switched, BURST drops zero chars. v3.5.0 release notes call out the pre-fix pattern. | Spec says retry uses same writer with same coop state. But coop state DOES drift across the 90s timeout — by retry it's settled. | Log `g_coopSwitched` and `g_originalCoopFlags` at every `KeyShm::IsActive()` edge in `Native/device_proxy.cpp:586-625`. Correlate with BURST 1 timestamps. |
| **H3** | Login server holds stale session for 30-45s; first attempt's submit reaches the server but server silently drops/rejects without UI feedback | The retry comment at `Core/AutoLoginManager.cs:660-668` *already documents this exact failure mode*. Retry waits 30s before re-firing — exactly enough for stale-session release. Retry succeeded after the 30s wait. | Comment claims a "Connection to the server could not be reached" dialog with focused OK button. Nothing in the log proves the dialog actually exists. Enter in the recovery path is benign on a live form too. | PrintWindow screenshot at T+90s on both clients before retry fires. If screen still shows login form (not modal), H3 is more likely than the modal theory. |
| **H4** | Login screen widget rendered but not yet input-routable — `CXWndManager::keyboardFocus` not on password field; keystrokes type into nowhere; Enter goes to wrong default button | doc's modal theory is a special case of "focus not on password field". `FindLivePasswordCEditWnd` returns valid pointer ~2.1s in (log:3535) but pointer existence ≠ keyboard-focus owner. | BURST 1 issues `Tab` first when `!UseLoginFlag`, but `account.UseLoginFlag` is `true` for gotquiz/gotquiz1 (verified, eqswitch-config.json), so no Tab fires. | Dump `CXWndManager` focused-widget pointer pre/post-BURST 1 via DLL log line. |
| **H2 (modal-collision)** | DLL ClickButton during `PHASE_CLICKING_CONNECT` submits empty form (Combo G silent-no-op on Dalaya), raises *"you need to enter a password"* modal, BURST 1 types into modal, Enter clicks OK | Code comment at `Core/AutoLoginManager.cs:470-474` confirms empty-Enter raises this modal on Dalaya patchme; DLL ClickButton has the same empty-form effect | **Zero log evidence of any modal** — no `OK_Display` line, no kick-session log, no `clicked OK on error dialog`. Pure inference. Also requires explaining why retry's identical payload works. | DLL log telemetry for `ClickButton` calls during `PHASE_CLICKING_CONNECT`; correlate with EQ's modal-spawn signal. |
| **H5** | `gameState`/title polling itself is broken on Dalaya per `memory/reference_eqswitch_dalaya_signals.md`; login *did* succeed first try, `WaitForScreenTransition` is the lying detector | Zero log evidence of a modal anywhere supports "everything is fine, our detector is wrong". | Retry shows a clear hung→responsive transition — first attempt presumably would have too if it had reached the same point. | `GetWindowRect` poll every 500ms during the 90s timeout. If rect is stable at charselect dimensions, H5 is in play. |
| **H6** | Race between `SendCancelCommand` and `Activate` — DLL's 500ms polling tick fires `MQ2Bridge::ClickButton` after BURST 1 starts typing | Cancel sent at T+17.168s, BURST 1 activated at T+17.670s — exactly one DLL tick. No margin. The `:566-569` chesterton comment worries about exactly this. | If `IsEQMainButtonWidget` gate at `:402-406` rejects the connect button as a CXMLDataPtr def, no real ClickButton ever fires (just retries). | DLL log ClickButton timestamps relative to BURST 1 entry/exit. |

This pattern of 90s timeout + retry-success is reproducible in `eqswitch.log.bak` (2026-04-25 17:07/17:09/17:41/17:42) and current log (2026-04-26 11:36/11:42). It is **not** a v3.14.8 regression — v3.14.8 only flipped 54 log strings in `AutoLoginManager.cs` (commit `0c72bbe`, verified, no behavioral change).

**Q-A1:** rank these six hypotheses with evidence from `Native/login_state_machine.cpp` `PHASE_CLICKING_CONNECT` and `PHASE_WAIT_CONNECT_RESP` handlers (line ranges in §6). H1 and H3 are pre-ranked above the modal theory because both are documented as known failure modes in our own comments — confirm or invert.

**Q-A1 typing-duration sub-question:** observed BURST 1 typing 474ms (PID 23864 17.670→18.144). For a 6-char password at 25+15=40ms/char coded cadence (`Core/AutoLoginManager.cs:1411,1424`), expected floor is 240ms. 474ms / 6 = 79ms/char ≈ 2× — within normal PostMessage scheduler variance. **This is not anomalous. No stall to explain.**

---

## 3. Iteration history (sharper)

```
v3.4.3  2026-04-08  Auto-login past character select               (3 commits)
v3.5.0  2026-04-09  Background input + 3-layer activation defense  (focus-faking)
v3.6.0  2026-04-10  C++ vtable validation, lazy MQ2 init           (Native firsts)
        2026-04-15  v7 GiveTime detour transport SHIPPED           (login_givetime_detour.cpp lives)
        2026-04-15  v6e WM_TIMER stabilization                      (anti-IsHungAppWindow)
        2026-04-23  pre-vkreturn-fallback-20260423 backup tag
        2026-04-24  working-autologin-20260424 + post-tune + post-yesno-fix tags
        2026-04-25  combo-g iter 11 → 15.2  (8 named iterations in one day)
v3.12.0 2026-04-25  ~13s faster dual-box, 3x faster typing, P0 sync-context fix
        2026-04-25  PATH A disabled "for agent investigation" — STILL DISABLED in HEAD
        2026-04-26  iter-12 dormant — burst-starvation memory file
        2026-04-30  v3.14.4 self-update fail-closed
v3.14.8 2026-05-01  Account.Notes/Name decouple (cosmetic for autologin)
        2026-05-02  TODAY — 3-minute team login on a 5-second spec
```

**Density (honest count):** 103 commits touching `Core/AutoLoginManager.cs` + `Native/login_state_machine.cpp` + `Native/eqmain_widgets.cpp` + `Native/eqswitch-di8.cpp`. 56 with `combo-g`, `iter`, `burst`, `warmup`, `givetime`, or `autologin` in the subject (was "55" in v1 — off-by-one). Of those, **only 11 are named iterations on credential entry** (`combo-g iter 11..15.2`, `iter-12`); **22 are charselect / enter-world / MQ2-bridge work** (different sub-problem); the remainder are touch-ups. Three named "working" tags in 2 days that didn't stay working.

The architectural ask — *"is the architecture wrong?"* — is the systematic-debugging skill's Phase 4 step 5: "If 3+ fixes failed: question the architecture." 11 named iterations on credential entry plus a load-bearing-side-effect comment that admits coupling: well past that gate.

---

## 4. The four paths

> v1 named three paths. v2 adds PATH D — the cheapest fix the v1 doc missed because it inherited a blind spot from a code comment that listed only options (a) and (b).

### PATH A — In-process login via SHM LOGIN command (DLL drives UI widgets)

**Files:**
- C# producer: [`Core/LoginShmWriter.cs`](#) (366 LOC)
- SHM contract: [`Native/login_shm.h`](#) (77 LOC) — `LoginCommand` + `LoginPhase` enums
- DLL state machine: [`Native/login_state_machine.cpp`](#) (671 LOC)
- Widget discovery: [`Native/eqmain_widgets.cpp`](#) (511 LOC) + [`Native/eqmain_widgets_mq2style.cpp`](#) (389 LOC, "iter 12")
- Combo G writer: [`Native/eqmain_cxstr.cpp`](#) (303 LOC) — CXStr ctor/FreeRep prologue-pinned
- Offsets: [`Native/eqmain_offsets.cpp`](#) (416 LOC)

**Intent:** zero keystrokes. DLL discovers `LOGIN_PasswordEdit` widget, calls `CXWnd::SetWindowText` (Combo G) to write password, then sends `XWM_LCLICK` to `LOGIN_ConnectButton`. Same approach as MacroQuest's autologin plugin (`MQ2LoginFrontend.cpp` + `StateMachine.cpp`).

**Status:** broken on Dalaya. Per the load-bearing comment at `Core/AutoLoginManager.cs:548-580`:

> "Combo G's password write works (verified in DLL log: 'set password via Combo G'), but the DLL's `PHASE_WAIT_CONNECT_RESP` detection in `login_state_machine.cpp:399-415` polls for a `gameState` change that never advances on Dalaya (gameState/title both lie — see memory `reference_eqswitch_dalaya_signals.md`). PATH A therefore always times out at 45s and falls through to PATH B."
>
> *» Note (v2 verifier finding):* the `:399-415` reference in this comment is itself stale — the actual `PHASE_WAIT_CONNECT_RESP` handler is at `Native/login_state_machine.cpp:458-474`. Lines `:399-415` are the `PHASE_CLICKING_CONNECT` widget-discovery + vtable validation. Comment rot in our own code; cited verbatim above for fidelity, fixed in §6.

**The "comment-rot" that v1 misread as a contradiction:**

| Line | Quote | Status |
|---|---|---|
| `:350` | *"On Dalaya the write silent-no-ops (wrong buffer — EQ renders/submits from a different field)"* | **Current truth** (also restated at `:606-609`) |
| `:553` | *"Combo G's password write works (verified in DLL log: 'set password via Combo G')"* | **Stale.** Resolved at `:606-609`: write reaches *some* buffer (read-back at +0x14 confirms we wrote), but EQ reads from a *different* field. So the read-back log "works" line is local to the write, not the submit-buffer. Should be deleted. |

**Q-A2 (v2):** confirm `:553` is stale and should be removed. The actual story: Combo G's CXStr write at +0x1A8 reaches a buffer EQ doesn't read from on Dalaya; EQ reads from a different field. Walk `Native/eqmain_cxstr.cpp` and the DLL log format to confirm.

**Disabled in HEAD:** PATH A's full call (`TryLoginViaShm`) is commented out at `Core/AutoLoginManager.cs:583-598`. The SHM warmup ritual is what's actually running today — see PATH B.

---

### PATH B — BURST 1 keystrokes via DI8 SHM (current default workhorse)

**Files:**
- C# orchestrator: [`Core/AutoLoginManager.cs:376-496`](#) — `RunCredentialEntry`
- DI8 SHM producer: [`Core/KeyInputWriter.cs`](#) (256 LOC)
- DI8 IAT hooks: [`Native/iat_hook.cpp`](#) (509 LOC) — focus-faking, BURST-window-only at `:171-189`
- DI8 device proxy: [`Native/device_proxy.cpp`](#) (760 LOC) + [`Native/di8_proxy.cpp`](#) (103 LOC) — coop-level switch at `:586-625`
- DI8 SHM consumer: [`Native/key_shm.cpp`](#) (161 LOC)
- Retry path: [`Core/AutoLoginManager.cs:660-731`](#) — only deterministic credential-entry sequence we have

**Intent:** type password into focused login field via DI8 keystroke injection. EQ's DI8 must be in `BACKGROUND|NONEXCLUSIVE` cooperative mode for this to work without focus-stealing. The IAT hooks at `:171-189` only spoof while SHM is active.

**Status:** the workhorse that gets users in-world today, but it is bolted on top of PATH A's wreckage:

```
RunCredentialEntry()                          [AutoLoginManager.cs:376]
├─ if (loginShm != null):
│     SendLoginCommand(...)                   ← starts PATH A's DLL FSM
│     wait for phase >= ClickingConnect       ← 2-5s on Dalaya
│     dwell WarmupDwellMs (default 4s)        ← DLL keeps clicking Connect, EQ may raise modal
│     SendCancelCommand                       ← stop DLL, ~500ms before BURST 1
├─ writer.Activate(pid, suppress=true)        ← sets DI8 SHM, IAT hooks active
├─ Thread.Sleep(500)                          ← "let DLL switch coop + blast activation"
├─ CombinedTypeString(password)               ← DI8 keystroke burst, 25+15=40ms/char floor
├─ Press Enter
└─ writer.Deactivate(pid)
```

The "warmup ritual" (line 380–462) is **the disabled-PATH-A's failed login attempt**, kept on life-support because its 4-second wallclock is the ONLY thing that gives DI8 cooperative-level negotiation enough time to settle into BACKGROUND mode before BURST 1 fires.

This is admitted explicitly at line 561-580:

> "⚠ LOAD-BEARING SIDE EFFECT — DO NOT NAIVELY DISABLE ⚠ ... PATH A's wasted 45s is incidentally giving EQ's DirectInput cooperative-level negotiation enough wall-clock to settle before BURST 1 fires. ... To skip PATH A safely you need EITHER (a) a real DLL post-connect detection signal so PATH A actually completes and reports success, OR (b) a non-time-based readiness gate before PATH B's BURST 1 (e.g. wait for password-field focus, first scene render, or DI8 cooperative level transition)."

**The comment names two ways out. v1 of this doc inherited that blind spot.** A third way is named in PATH D below — a clean post-CANCEL `Sleep(WarmupDwellMs)` decouples A from B at zero cost. The comment dismisses it as "trades the 45s for a different fixed wait" but that is an argument against bumping the *existing* knob, not against introducing a clean post-CANCEL sleep that lets us delete PATH A entirely.

**The retry path at `Core/AutoLoginManager.cs:660-731` is the only code that has ever worked deterministically:** Activate → press Enter (dismiss any modal, benign on live form) → 30s server-release wait → Activate → BURST 1 → Enter. **The retry IS the working algorithm — it just costs 30 extra seconds.** That is a defense-in-depth fact, not a code smell.

**Why PATH B is the resilience floor:** DI8 SendInput uses *only* Win32 (`WM_KEYDOWN` / `WM_CHAR` / `WM_KEYUP` to a window handle). No EQ struct dependencies. PATH A reads `pinstLoginController` + widget heap layout + CXStr ctor prologue. PATH C (next) reads `LoginController::GiveTime` RVA + 7+ instance pointers. **When (not if) Dalaya patches eqmain.dll, PATH B is the only thing that still types.** That's a real production concern — the user's "no backup" framing in v1's §1 was wrong about resilience.

**Q-B1:** is there a DI8 cooperative-level signal we can poll directly (instead of using PATH A's 45s timeout as a proxy)? `Native/device_proxy.cpp:586-625` shows the BACKGROUND coercion fires only when SHM is active. Can the DLL surface "BACKGROUND mode achieved" through SHM the way it surfaces `LoginPhase`?

**Q-B2:** the "Tested + reverted 2026-04-24" note at `Core/AutoLoginManager.cs:470-474` says pre-flight Enter raises a modal. The retry path uses pre-flight Enter and works. Why? (Hypothesis: by retry time the form is already in a stale-session-rejected state with a modal up, so Enter dismisses; on first attempt no modal is up, so Enter creates one. The fix is to check before pre-Entering — but we don't have a modal-detection primitive.)

---

### PATH C — MQ2 GiveTime detour (transport shipped, FSM port pending)

**Memory:** [`memory/project_eqswitch_v7_goal_mq2_givetime_detour.md`](#)
**Reference impl:** `_.src/_srcexamples/macroquest-rof2-emu/src/main/MQ2LoginFrontend.cpp:50-88` and `src/plugins/autologin/StateMachine.cpp:111-729`

**Files (already in HEAD):**
- DETOUR TRANSPORT (shipped v3.12.0): [`Native/login_givetime_detour.cpp`](#) (214 LOC)
  - MinHook on `LoginController::GiveTime` at RVA `0x128B0` (line `:29`)
  - Prologue gate `firstByte == 0x56` at `:128-135` — **refuses install on mismatch**
  - Detour body calls `MQ2BridgePollTick()` then trampoline (line `:73-91`)
- POLLING CADENCE: [`Native/mq2_bridge.cpp`](#) (3637 LOC) — owns the per-tick FSM target
- FSM CONSUMER (still PATH A's widget-driven SHM): [`Native/login_state_machine.cpp`](#) — what gets called from inside the detour

**What's missing:** the **FSM strategy port** from MQ2's `StateMachine.cpp:111-729` (the `Wait`/`Connect`/`ServerSelect`/`CharacterSelect` state classes). The current FSM is PATH A's widget-driven SHM consumer, not MQ2's gameState-aware FSM. Running PATH A's broken `gameState` polling at 60Hz instead of via SHM doesn't fix PATH A's bug; it just polls faster. **The transport is right; the strategy isn't.**

**Intent (when complete):** detour `LoginController::GiveTime`. Run autologin state machine **inside EQ's game loop**, not on the Windows message pump. Optional `ShowWindow(SW_HIDE)` on login/server/charselect HWNDs so the user never sees them — click hotkey, see only the in-world loading screen.

**Why this beats both A and B (when the FSM lands):**
- **Beats A:** detour runs every frame, has direct read access to EQ's render-side EditWnd buffers — no Combo G silent-no-op, no SHM polling latency.
- **Beats B:** no DI8 keystrokes needed at all. No DI8 cooperative-level dance, no focus-faking IAT hooks, no `BACKGROUND` mode requirement.
- **Beats both:** matches MacroQuest's battle-tested approach (years of production use). Source available.

### Risks the v1 doc missed

**R1 — Single-RVA hardcode + no AOB fallback.** `:29` hardcodes `GIVETIME_RVA = 0x128B0`; `:128-135` first-byte gate rejects on prologue change with `g_installAttempted = true` (permanent failure, no retry, no signature scan). **Any Dalaya eqmain.dll patch silently disables PATH C until we ship a new build.** The MQ2 reference has the same issue but their community ships RVA updates within hours. We don't have that pipeline. PATH B has zero exposure to this — its only dependencies are Win32 message-pump APIs that don't change.

**R2 — gameState ambiguity is a *strategy* problem, not a *transport* problem.** `Native/login_state_machine.cpp:31-38` admits "Dalaya ROF2 uses different gameState values from modern MQ2 (PRECHARSELECT==0 vs MQ2's -1). Strategy: don't gate on gameState for login screen — gate on widget presence". MQ2's FSM (`StateMachine.cpp:116`, `MQ2AutoLogin.cpp:996/1012/1030/...`) DOES gate on `GAMESTATE_PRECHARSELECT` in 10+ places. **A naive port will deadlock at the same place PATH A deadlocks today.** The FSM port has to translate every `gameState`-gated branch to Dalaya's "widget-presence-only" rule. This is engineering, not transcription.

**R3 — three more instance pointers needed.** Today's detour pins only `LoginController::GiveTime` (`0x128B0`) and `pLoginController` (`0x150174`). MQ2's FSM uses `pinstLoginServerAPI`, `pinstCharacterListWnd`, and `g_pLoginClient` — each a separate Dalaya x86 RVA we'd need to derive via Cheat Engine 7.5 / MiraDump. Memory file's "2-4 hour" estimate is optimistic by ~2x.

**R4 — multi-client correctness.** MQ2's plugin assumes one DLL per process with global `Login::m_record`. EQSwitch drives N processes from one C# host via per-PID SHM. PATH C needs per-client `ProfileRecord` injection — via per-PID SHM into the detour, or via `/login:<token>` cmdline (per `StateMachine.cpp:48-58`). Doc doesn't pick.

**R5 — `Account.Name` interaction.** v3.14.8's Notes/Name decouple is currently described as "cosmetic for autologin". MQ2's FSM uses `record->accountName` (`StateMachine.cpp:88-108`, `IsWrongAccount`) as the **identity key** for "wrong-server-quit-back-out" logic. If PATH C ports that, the FSM consumes `Account.Name` (post-decouple = real username), not `EffectiveLabel`. Wire wrong, the FSM thinks every login is a wrong-account quit-out.

**Q-C1 (v2):** is there any reason PATH C's *FSM port* (not just the transport) can't work on Dalaya? Specifically, can MQ2's `StateMachine.cpp:111-300` Wait/Connect classes have their `gameState`-gated branches mechanically translated to `widget-presence`-gated branches, or does that lose semantics?

**Q-C2 (v2 — narrower):** if we ship PATH C's FSM port, what do we delete? The v1 estimate "delete most of PATH B" was wrong because of the coupling map (§4.5) — `device_proxy.cpp` BACKGROUND coercion is also used for hotkey switching. Realistic deletion surface:
- All of PATH A: `LoginShmWriter` (366), `login_shm.h` (77), `login_state_machine.cpp` (671), `eqmain_widgets*.cpp` (900), `eqmain_cxstr.cpp` (303). ~2.3k LOC.
- BURST 1 path in `RunCredentialEntry` (~120 LOC C#) — IF PATH B is also being deleted; if kept as `--legacy-burst` floor, this stays.
- The retry path (~80 LOC C#) — same caveat.
- **Cannot delete:** `device_proxy.cpp` BACKGROUND coercion (shared with hotkey switching), `iat_hook.cpp` (still useful for parallel-client focus management).

---

### PATH D — Sleep(WarmupDwellMs) decoupling (cheapest fix; 20 LOC C#)

**Files (changes only):**
- [`Core/AutoLoginManager.cs:376-496`](#) — `RunCredentialEntry`. Replace SHM warmup ritual with explicit `Sleep(WarmupDwellMs)` after CANCEL, before Activate.

**Intent:** kill the load-bearing-side-effect coupling. Today, PATH A's failed-login wallclock IS the DI8 settle gate. PATH D makes the wallclock explicit:

```diff
  // Existing (PATH A's failed FSM as warmup ritual)
- if (loginShm != null) {
-     loginShm.SendLoginCommand(...);
-     // wait for phase >= ClickingConnect (2-5s)
-     // dwell WarmupDwellMs (4s) — DLL keeps clicking Connect, may raise modal
-     loginShm.SendCancelCommand(...);
- }
+ // Explicit DI8 settle window — no SHM, no DLL ClickButton, no modal risk
+ Thread.Sleep(WarmupDwellMs);  // tunable, default 4000ms
+
  writer.Activate(pid, suppress: true);
  Thread.Sleep(500);  // existing — let DLL switch coop + blast activation
  CombinedTypeString(password);
  ...
```

**Why this works (claim — needs Cloud Claude verification):** the only thing PATH A's warmup actually *does* on Dalaya is occupy 4s of wallclock for DI8 cooperative-level negotiation. It does NOT successfully type the password (silent-no-op, `:606-609`). It does NOT successfully advance gameState (`PHASE_WAIT_CONNECT_RESP` never fires on Dalaya). It DOES potentially raise a modal via empty-form ClickButton (H2 from §2). Replacing the ritual with `Sleep(WarmupDwellMs)` preserves the only useful side effect (wallclock) and removes the harmful side effect (potential modal).

**Why this doesn't fix the root cause:** if H1 is the real bug (BURST 1 typed before BACKGROUND coop), PATH D doesn't help — it just changes which 4s of wallclock we waste. The real fix is Q-B1's signal-based gate. PATH D is the **bridge** that lets us ship a 5-second autologin TODAY while we work on Q-B1 / PATH C properly.

**Risks:**
- D1 — if EQ's DI8 SCL call timing depends on SHM being active (not just on wallclock), a passive Sleep won't trigger it. Verify by reading `Native/iat_hook.cpp:171-189` and `Native/device_proxy.cpp:586-625`. If SHM activation is the trigger (not wallclock alone), PATH D doesn't decouple — we'd need to issue an SHM activation pulse without the LOGIN command.
- D2 — if H2 (modal-collision) is real and frequency >0%, removing the warmup eliminates the modal risk; if H1 is the real bug, PATH D is a no-op.

**Q-D1:** read `Native/iat_hook.cpp` and `Native/device_proxy.cpp:586-625`. Does DI8 cooperative-level switch trigger on (a) SHM-active edge, or (b) wallclock elapsed since IAT hook install, or (c) some other signal? If (a), PATH D needs a "no-op SHM pulse" variant; if (b), PATH D works as drafted.

**Q-D2:** if PATH D works, is it the right "ship tonight" path while PATH C's FSM port is engineered? Or is PATH D just another iteration on top and we should bite the bullet on PATH C?

---

## 4.5. Coupling map — three paths, three couplings

> v1 admitted only one coupling. v2 surfaces two more.

**Coupling C1 — Wallclock side effect (PATH A → PATH B).** Documented at `Core/AutoLoginManager.cs:548-580`. PATH A's 45s timeout is what gives DI8 BACKGROUND mode time to settle before BURST 1. Remove PATH A naively → BURST 1 truncates (verified 2026-04-25 dual-box: 4-of-6 chars on client 1, 0-of-6 on client 2). PATH D's Sleep replaces this.

**Coupling C2 — SHM-activation triggers IAT hooks AND coop-level switch (PATH A → PATH B activation).** `Native/iat_hook.cpp:171-189` — `GetForegroundWindow` / `GetFocus` / `GetActiveWindow` IAT hooks only spoof when SHM is active. SHM is activated by `LoginShmWriter` (PATH A's path) at `Core/AutoLoginManager.cs:393`. **Deleting PATH A naively kills the activation trigger for the IAT hooks AND the coop-level switch.** This is *not* the same as Coupling C1 — even with PATH D's wallclock, if no SHM goes active, no coop switch fires. PATH D needs to issue an SHM activation pulse (without the LOGIN command) OR PATH B's `writer.Activate` itself triggers SHM-active.

**Q-coupling-C2:** does `KeyInputWriter.Activate` independently set SHM-active enough to trigger the IAT hooks + coop switch, or does it rely on `LoginShmWriter` having already activated SHM upstream? Read `Native/key_shm.cpp` + `Core/KeyInputWriter.cs`.

**Coupling C3 — `device_proxy.cpp` is shared infra, not autologin-only.** `Native/device_proxy.cpp:5,258-271` documents the phantom-keys hotfix that **restores** coop level after autologin to avoid stuck-BACKGROUND mode breaking hotkey-driven focus switching. Deleting PATH B (Q-C2 estimate) would orphan this restore path and break hotkey switching unless explicitly accounted for. v1's deletion plan treated `device_proxy.cpp` as ~1k LOC of pure-autologin code; it's shared infrastructure.

**Coupling C4 — the retry path is defense-in-depth.** `Core/AutoLoginManager.cs:660-731` is the only deterministic credential-entry sequence we've shipped. v1 framed it as "the working algorithm — costs 30 extra seconds" (mildly negative). v2 reframes: it's a defense-in-depth fact. Deleting it before any new path has 30 days of green production is premature. Keep it as retry even after PATH C/D ship.

**Account.Name=="" data issue is genuinely orthogonal.** `Models/Account.cs:18,38` shows `EffectiveLabel` falls back across Name/Username; `Core/AutoLoginManager.cs` uses `account.Username` exclusively (lines 132, 190, 269, 393, 480, 638, 671, 691, 695, 713, 735, 752 — verified). The empty-Name issue does NOT interact with PATH A/B/C/D. State this explicitly so cloud reviewers don't go down that rabbit hole. (v3.14.8's `AppConfig.Validate` migration only fires when `Name` is non-empty AND diverges from `Username`; empty-Name from a v3.14.7 broken Note→Name save falls through. Separate 5-line patch in a follow-up PR.)

---

## 5. Reading guide — the order to read in

For Cloud Claude. Walk this top-down.

1. **The user's spec** — `CLAUDE.md` lines 60-78 ("AUTOLOGIN SPEC — THE ACTUAL REQUIREMENT"). 4 lines, 5-second budget, "zero manual clicks".
2. **The architectural admission** — `Core/AutoLoginManager.cs:548-616`. Read every line of the two adjacent comment blocks. They describe Coupling C1.
3. **PATH B today** — `Core/AutoLoginManager.cs:376-496` (`RunCredentialEntry`). The "warmup" branch is PATH A's failed FSM kept alive purely for its 4s wallclock side effect.
4. **The retry that actually works** — `Core/AutoLoginManager.cs:660-731`. Only deterministic credential-entry sequence. **Why isn't this the first attempt?**
5. **PATH A's DLL state machine** — `Native/login_state_machine.cpp` end-to-end (671 LOC). `PHASE_WAIT_CONNECT_RESP` is at `:458-474` (the AutoLoginManager comment cites stale `:399-415`). Why doesn't `gameState` advance on Dalaya?
6. **The widget write that doesn't reach EQ's submit buffer** — `Native/eqmain_cxstr.cpp` + `Native/eqmain_widgets.cpp`. Resolve: where does CXStr ctor at +0x1A8 actually write, and which buffer does EQ read from at submit? Confirm `:553` is stale comment-rot.
7. **PATH C transport already shipped** — `Native/login_givetime_detour.cpp` (214 LOC). MinHook install, prologue gate, trampoline publish. Read `:29` (RVA hardcode) and `:128-135` (first-byte gate, no AOB fallback).
8. **PATH C reference impl** — `_.src/_srcexamples/macroquest-rof2-emu/src/main/MQ2LoginFrontend.cpp:50-88` (detour install) + `src/plugins/autologin/StateMachine.cpp:111-729` (FSM strategy). Compare against our current `Native/login_state_machine.cpp` — what does MQ2's FSM do that ours doesn't? Specifically: how do MQ2's `gameState`-gated branches translate to Dalaya's "widget-presence-only" rule?
9. **DI8 coop-level question** (Q-B1, Q-D1) — `Native/iat_hook.cpp:171-189` (IAT hook spoof predicate) + `Native/device_proxy.cpp:586-625` (`SetCooperativeLevel` proxy). What signal triggers BACKGROUND coercion?
10. **The chesterton fence index** (next section) — every load-bearing comment in `Core/AutoLoginManager.cs` and what specific incident put it there.

---

## 6. Chesterton fence index — every load-bearing comment

(Format: **file:line** — *summary* — incident date — full quote.)

- **`AutoLoginManager.cs:344-345`** — *"load-bearing warmup contract"* — see `memory/feedback_chesterton_fence_load_bearing_bugs.md`
- **`AutoLoginManager.cs:350-355`** — *"On Dalaya the write silent-no-ops"* — describes Combo G failure mode. **Current truth.**
- **`AutoLoginManager.cs:363-367`** — *"SendCancelCommand fires BEFORE BURST 1 — AFTER caused 4-of-6 char truncation"* — verified 2026-04-25 dual-box
- **`AutoLoginManager.cs:447-462`** — *"DLL ClickButton retry loop contended with C# typing for EQ's message pump"* — same incident
- **`AutoLoginManager.cs:470-474`** — *"a pre-flight Enter is NOT idempotent on Dalaya patchme — empty-password Enter raises a modal"* — Tested + reverted 2026-04-24
- **`AutoLoginManager.cs:548-572`** — **THE BIG ONE (Coupling C1)** — *"⚠ LOAD-BEARING SIDE EFFECT — DO NOT NAIVELY DISABLE ⚠ PATH A's wasted 45s is incidentally giving EQ's DirectInput cooperative-level negotiation enough wall-clock to settle before BURST 1 fires"*
- **`AutoLoginManager.cs:553`** — *"Combo G's password write works (verified in DLL log)"* — **STALE.** Contradicted by `:606-609`. Should be deleted.
- **`AutoLoginManager.cs:573-580`** — *"To skip PATH A safely you need EITHER (a) a real DLL post-connect detection signal, OR (b) a non-time-based readiness gate"* — v1 inherited this blind spot. PATH D adds option (c).
- **`AutoLoginManager.cs:594-601`** — *"PATH A disabled for agent investigation 2026-04-25 — truncation symptom"* — disabled, never re-enabled, kludge has lived 7 days
- **`AutoLoginManager.cs:606-609`** — *"Combo G's CXStr write at +0x1A8 doesn't reach EQ's render/submit buffer (read-back at +0x14 confirms we wrote somewhere, but EQ reads from a different field)"* — **Current truth, supersedes `:553`.**
- **`AutoLoginManager.cs:659-668`** — *"Hotfix 2026-04-24: stale-session auto-recovery"* — the retry path, defense-in-depth fact (Coupling C4)
- **`AutoLoginManager.cs:870-875`** — *"Hotfix v4 (HIGH-A): MQ2 bridge never came up after 30s wait"* — char-select-side bug, tangentially related
- **`Native/login_state_machine.cpp:31-38`** — *"Dalaya ROF2 uses different gameState values from modern MQ2 (PRECHARSELECT 0 vs -1). Strategy: don't gate on gameState for login screen — gate on widget presence"* — strategy contradicted by `:458-474` which DOES gate on gameState. Unresolved internal ambiguity.
- **`Native/login_givetime_detour.cpp:29`** — *"GIVETIME_RVA = 0x128B0"* — single-RVA hardcode. PATH C R1.
- **`Native/login_givetime_detour.cpp:128-135`** — *"firstByte == 0x56 (push esi) sanity check; refuses install on mismatch"* — no AOB fallback. PATH C R1.
- **`Native/device_proxy.cpp:5,258-271`** — *"phantom-keys hotfix: restore coop level after autologin"* — Coupling C3.

**Pattern:** every comment is in the form *"if you do X you'll break Y, learned 2026-04-NN, don't"*. None is in the form *"this is correct because Z"*. We are deep in apology territory. The chesterton fences are themselves evidence the architecture is wrong.

---

## 7. The asks (TL;DR)

For Cloud Claude — answer these in order, with file:line evidence:

**Architecture choice:**
- **Q1:** Of PATH A / PATH B / PATH C / PATH D, which combination is the right ship sequence? Specifically: does PATH D unblock tonight while PATH C's FSM is engineered, with PATH B retained as the Win32-only resilience floor? Or is there a better play? Justify with code reading, not vibes.
- **Q2:** Reframed from v1 "pick one, delete two" — is "one primary + one Win32-only floor + delete middle" the correct shape, given Coupling C3 (`device_proxy.cpp` is shared infra) and the Dalaya-eqmain.dll-patch fragility of PATH A and PATH C?

**Today's flake (low priority — cause is academic if we ship a different path):**
- **Q3:** rank the six hypotheses in §2's table. H1 (DI8 coop not yet BACKGROUND) and H3 (stale-session) are pre-ranked above H2 (modal-collision); confirm or invert based on `Native/login_state_machine.cpp:458-474` + `Native/iat_hook.cpp:171-189`.

**PATH-specific:**
- **Q-A2:** confirm `:553` is stale comment-rot, and `:606-609` is current truth. Read `Native/eqmain_cxstr.cpp` + DLL log format.
- **Q-B1:** is there a DI8 cooperative-level signal we can poll directly, instead of using PATH A's wallclock as a proxy?
- **Q-C1:** can MQ2's `StateMachine.cpp:111-300` `gameState`-gated branches mechanically translate to `widget-presence`-gated branches on Dalaya, or does that lose semantics?
- **Q-D1:** does DI8 cooperative-level switch trigger on SHM-active edge, or wallclock alone? (Decides whether PATH D needs an SHM activation pulse.)

**Operational:**
- **Q5 (rollout):** What's the deploy gate to ship PATH C without breaking the working v3.14.8 retry path? Behind a `LoginStrategy` enum config flag with PATH B fallback for N sessions? What log signal flips users back to B automatically?
- **Q6 (instance pointers):** PATH C-FSM needs `pinstLoginServerAPI`, `pinstCharacterListWnd`, `g_pLoginClient` resolved on Dalaya x86. How many additional CE sessions, and is `MiraDump` set up to AOB-scan from MQ2's symbol names instead of hand-walking?
- **Q7 (multi-client):** how does PATH C's FSM address per-client `ProfileRecord` injection — per-PID SHM into the detour, or `/login:<token>` cmdline?
- **Q8 (Account.Name vs FSM):** confirm which field flows into `ProfileRecord.accountName` if PATH C ports MQ2's `IsWrongAccount` logic. Wire wrong, the FSM thinks every login is a wrong-account quit-out.
- **Q9 (headless windows scope):** memory file lists `ShowWindow(SW_HIDE)` on login/charselect HWNDs as part of v7's UX win. In scope for first PATH C ship, or deferred? Interacts with `UI/PipOverlay.cs` (DWM thumbnails) and `Core/WindowManager.cs` (slim-titlebar guard timer).

**Deletion plan:**
- **Q4 (revised):** for whichever combination you pick in Q1/Q2, list the files + line ranges to delete, with explicit acknowledgement of the 4 couplings in §4.5. What chesterton fences become safe to remove after PATH A is genuinely deleted (not just commented out)?

---

## 8. Artifacts

- **Today's full log:** `C:\Users\nate\proggy\Everquest\EQSwitch\eqswitch.log` lines 3463–3578 (90s timeout + retry succeeded). Earlier sections of same file have working dual-box logins on 4/26 — useful positive control.
- **Historical log:** `C:\Users\nate\proggy\Everquest\EQSwitch\eqswitch.log.bak` — 4 timeout incidents on 2026-04-25 (17:07/17:09/17:41/17:42).
- **Working anchor binary:** tag `working-autologin-20260424` (md5 `09e138ce56711ac66cb04bbae08c7725` per `memory/feedback_eqswitch_native_anchor.md`). Per memory, "ship-time verify-before-no-regression" — DO NOT rebuild Native/ from HEAD without dual-box end-to-end test.
- **MQ2 reference source:** `X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/`. Authoritative for PATH C class layouts + symbol names. Numeric VAs are x64 RoF2 — translate to x86 Dalaya at offset-verify step.
- **CE 7.5 (clean):** GitHub source build per `memory/project_cheatengine_76_malware_2026_04_11.md`. **Do not** download `cheatengine.org` 7.6 — confirmed malware (37/72 VirusTotal).
- **Build contract:** `Native/build.cmd` (MSVC) / `Native/build.sh` (MinGW). PATH C/D rebuild path passes through here.

---

## 9. What we're explicitly NOT asking

- *"Why doesn't BURST 1 type fast enough?"* — typing speed has been tuned 130ms→40ms→25ms in three iterations. Speed is not the issue. The §2 v1 "474ms vs 115ms expected" rhetorical hook was math error; observed is ~2× coded floor = normal variance.
- *"Why does WarmupDwellMs need to be 4s?"* — that knob is a band-aid for Coupling C1. Fixing the coupling (PATH D) makes the knob a clean, named DI8 settle window instead of a hidden side effect.
- *"Should we add another retry?"* — three retries already (BURST 1 → 30s server-release → retry BURST 1 → if char-select still missing, surface to user). Adding a fourth doesn't fix what's broken.
- *"Should we add YESNO dialog handling?"* — `memory/feedback_eqswitch_no_yesno_in_patchme.md` confirms patchme has no kick-session dialog. Don't go there.
- *"Is `Account.Name=='' ` part of this?"* — no. `Core/AutoLoginManager.cs` uses `account.Username` exclusively (12 verified call sites in §4.5). Separate 5-line follow-up patch.

---

## 10. Test + memory surface

**Test files (already in repo — listed for cloud reviewers; all live under `Core/`, not a separate `Tests/` directory):**
- `Core/AppConfigValidateTests.cs` — covers `AppConfig.Validate` migration; should be extended for empty-Name repair when that follow-up lands
- `Core/CharacterSelectorTests.cs` — char-select path
- `Core/KeyInputWriterTests.cs` — DI8 SHM producer
- `Core/ShmLayoutTests.cs` — **load-bearing**: only mechanical guard against the C# producer / C++ consumer struct-layout mismatch noted in CLAUDE.md "Memory-mapped file struct must match exactly"
- `Core/TestAutoLoginRunner.cs` — orchestration smoke

**Memory pointers cloud reviewers should read:**
- `memory/project_eqswitch_v7_goal_mq2_givetime_detour.md` — PATH C origin (16 days old; may be stale on offset count)
- `memory/feedback_eqswitch_native_anchor.md` — working-tag anchor
- `memory/reference_eqswitch_dalaya_signals.md` — gameState/title lying behavior
- `memory/feedback_eqswitch_no_yesno_in_patchme.md` — confirms no kick-session modal
- `memory/feedback_chesterton_fence_load_bearing_bugs.md` — Coupling C1 origin
- `memory/feedback_eqswitch_iter12_burst_starvation.md` — burst-truncation root cause analysis
- `memory/project_eqswitch_inprocess_login_debug.md` — PATH A debug history
- `memory/project_eqswitch_v7_phase4_csharp.md` — v7 phase 4 C# wiring
- `memory/project_eqswitch_chararray_intel.md` — char array intel for charselect (adjacent path)
- `memory/reference_sendinput_union_size_bug.md` — Win32 SendInput-specific gotcha relevant to PATH B

---

**Prepared 2026-05-02 from `main` HEAD `0c72bbe`. v2 (post-pre-flight). No source files modified in this PR. Reviewer reads at branch tip.**
