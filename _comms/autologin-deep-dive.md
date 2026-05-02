# Autologin Deep-Dive — for Cloud Claude review

**Status:** review-only PR. Zero source changes. The diff is this document.
**Branch:** `review/autologin-deep-dive` off `main` (HEAD `0c72bbe`, v3.14.8 shipped 2026-05-01).
**Reviewer expectations:** read this end-to-end, then walk the file/line anchors. Branch tip is current `main` — every line ref resolves at HEAD.

---

## 1. The ask

We have **two parallel password-entry paths** in production today, both flawed, plus a **third architectural approach** (v7 GiveTime detour) designed 16 days ago that we never shipped. The two live paths fight each other; we keep one alive only because the other's wasted time accidentally settles a DirectInput state machine. We've shipped **55 named iterations** on this hot path in 25 days (`combo-g iter 11..15.2`, `iter-12`, `v7 Phase 4..6`, `v8 Step 1..3`, `Hotfix v3..v6e`, `BURST 1`, etc.). That's the smell of an architecture problem, not a sequence of bugs.

**The decision we want from you:**

> **Pick the one path that can be made 100% reliable on Shards-of-Dalaya patchme. Tell us how to delete the other two.**
>
> If none of the three can be 100%, tell us what fourth approach we're missing.

We do not want another iteration on top. We want one path. No backup.

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

**Failure mode (working hypothesis):** during `ClickingConnect` phase the DLL's `ClickButton` retry loop fires `XWM_LCLICK` on `LOGIN_ConnectButton` every game-tick for `WarmupDwellMs=4000ms`. On Dalaya, Combo G's password write at `+0x1A8` silent-no-ops (per code comment, [Core/AutoLoginManager.cs:553-558](#)), so each click submits an empty form, raising Dalaya's *"you need to enter a username and password"* modal. That modal steals focus from the password field. BURST 1's typing lands on the modal (or is dropped); BURST 1's `Enter` clicks the modal's OK button instead of submitting credentials. → no transition → 90s timeout. The retry path at [`Core/AutoLoginManager.cs:660-708`](#) succeeds because it explicitly `Press(Enter)` to dismiss the modal first, sleeps 30s for server-release, then re-types into the now-focused field.

This pattern is reproducible in `eqswitch.log.bak` (4/25 17:07/17:09/17:41/17:42) and `eqswitch.log` (4/26 11:36/11:42). It is not a v3.14.8 regression — v3.14.8 only flipped 54 log strings in `AutoLoginManager.cs` (commit `0c72bbe` — verified, no behavioral change).

**Question for you (Q-A1):** is the modal-collision hypothesis correct, or is BURST 1 dropped for a different reason (DI8 cooperative-level not yet `BACKGROUND`, focus-faking IAT hooks not active, DLL still holding the message pump)? Evidence to look at: BURST 1 typing duration is 474ms (PID 23864 17.670→18.144); for a 6-char password at the documented 25/15/15ms inter-key cadence, expected ≈115ms. So the typing window is wider than typing alone — what's eating the rest?

---

## 3. The 55-iteration history

```
v3.4.3  2026-04-08  Auto-login past character select               (3 commits)
v3.5.0  2026-04-09  Background input + 3-layer activation defense  (focus-faking)
v3.6.0  2026-04-10  C++ vtable validation, lazy MQ2 init           (Native firsts)
        2026-04-15  v7 GiveTime detour DESIGNED — never shipped
        2026-04-15  v6e WM_TIMER stabilization (anti-IsHungAppWindow)
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

**Density:** 103 commits touching `Core/AutoLoginManager.cs` + `Native/login_state_machine.cpp` + `Native/eqmain_widgets.cpp` + `Native/eqswitch-di8.cpp`. 55 with `combo-g`, `iter`, `burst`, `warmup`, `givetime`, or `autologin` in the subject. **Three** named "working" tags in 2 days that didn't stay working.

The architectural ask — *"is the architecture wrong?"* — is the systematic-debugging skill's Phase 4 step 5: "If 3+ fixes failed: question the architecture." We're at 55+. We are decisively past that gate. We have not asked it yet.

---

## 4. The three paths

### PATH A — In-process login via SHM LOGIN command (DLL drives UI widgets)

**Files:**
- C# producer: [`Core/LoginShmWriter.cs`](Core/LoginShmWriter.cs) (366 LOC)
- SHM contract: [`Native/login_shm.h`](Native/login_shm.h) (77 LOC) — `LoginCommand` + `LoginPhase` enums
- DLL state machine: [`Native/login_state_machine.cpp`](Native/login_state_machine.cpp) (671 LOC)
- Widget discovery: [`Native/eqmain_widgets.cpp`](Native/eqmain_widgets.cpp) (511 LOC) + [`Native/eqmain_widgets_mq2style.cpp`](Native/eqmain_widgets_mq2style.cpp) (389 LOC, "iter 12")
- Combo G writer: [`Native/eqmain_cxstr.cpp`](Native/eqmain_cxstr.cpp) (303 LOC) — CXStr ctor/FreeRep prologue-pinned
- Offsets: [`Native/eqmain_offsets.cpp`](Native/eqmain_offsets.cpp) (416 LOC)

**Intent:** zero keystrokes. DLL discovers `LOGIN_PasswordEdit` widget, calls `CXWnd::SetWindowText` (Combo G) to write password, then sends `XWM_LCLICK` to `LOGIN_ConnectButton`. Same approach as MacroQuest's autologin plugin (`MQ2LoginFrontend.cpp` + `StateMachine.cpp`).

**Status:** broken on Dalaya. Per the load-bearing comment at [`Core/AutoLoginManager.cs:553-580`](Core/AutoLoginManager.cs):

> "Combo G's password write works (verified in DLL log: 'set password via Combo G'), but the DLL's PHASE_WAIT_CONNECT_RESP detection in `login_state_machine.cpp:399-415` polls for a `gameState` change that never advances on Dalaya (gameState/title both lie — see memory `reference_eqswitch_dalaya_signals.md`). PATH A therefore always times out at 45s and falls through to PATH B."

There is **also** a contradiction in our own comments. Line 350:

> "On Dalaya the write silent-no-ops (wrong buffer — EQ renders/submits from a different field)."

vs line 553:

> "Combo G's password write works (verified in DLL log: 'set password via Combo G')."

**Question for you (Q-A2):** which is true — does Combo G's password write reach the right buffer or not? If it writes to the wrong buffer the click submits empty (and our modal hypothesis follows). If it writes to the right buffer the click should succeed but `gameState` doesn't advance — that's a different bug. **The two comments cannot both be right.** Reading `login_state_machine.cpp` end-to-end + the DLL log telemetry should be decisive. We have been arguing past each other in code comments for 9 days.

**Disabled in HEAD:** PATH A's full call (`TryLoginViaShm`) is commented out at [`Core/AutoLoginManager.cs:583-598`](Core/AutoLoginManager.cs). The SHM warmup ritual is what's actually running today — see PATH B.

---

### PATH B — BURST 1 keystrokes via DI8 SHM (current default)

**Files:**
- C# orchestrator: [`Core/AutoLoginManager.cs:376-496`](Core/AutoLoginManager.cs) — `RunCredentialEntry`
- DI8 SHM producer: [`Core/KeyInputWriter.cs`](Core/KeyInputWriter.cs) (256 LOC)
- DI8 IAT hooks: [`Native/iat_hook.cpp`](Native/iat_hook.cpp) (509 LOC) — focus-faking
- DI8 device proxy: [`Native/device_proxy.cpp`](Native/device_proxy.cpp) (760 LOC) + [`Native/di8_proxy.cpp`](Native/di8_proxy.cpp) (103 LOC)
- DI8 SHM consumer: [`Native/key_shm.cpp`](Native/key_shm.cpp) (161 LOC)

**Intent:** type password into focused login field via DI8 keystroke injection. EQ's DI8 must be in `BACKGROUND|NONEXCLUSIVE` cooperative mode for this to work without focus-stealing.

**Status:** the workhorse that gets users in-world today, but it is bolted on top of PATH A's wreckage:

```
RunCredentialEntry()                          [AutoLoginManager.cs:376]
├─ if (loginShm != null):
│     SendLoginCommand(...)                   ← starts PATH A's DLL FSM
│     wait for phase >= ClickingConnect       ← 2-5s on Dalaya
│     dwell WarmupDwellMs (default 4s)        ← DLL keeps clicking Connect
│     SendCancelCommand                       ← stop DLL, ~500ms before BURST 1
├─ writer.Activate(pid, suppress=true)        ← sets DI8 SHM, IAT hooks active
├─ Thread.Sleep(500)                          ← "let DLL switch coop + blast activation"
├─ CombinedTypeString(password)               ← DI8 keystroke burst, ~25/15/15ms inter-key
├─ Press Enter
└─ writer.Deactivate(pid)
```

The "warmup ritual" (line 380–462) is **the disabled-PATH-A's failed login attempt**, kept on life-support because its 4-second wallclock is the ONLY thing that gives DI8 cooperative-level negotiation enough time to settle into BACKGROUND mode before BURST 1 fires.

This is admitted explicitly at line 561-580:

> "⚠ LOAD-BEARING SIDE EFFECT — DO NOT NAIVELY DISABLE ⚠ ... PATH A's wasted 45s is incidentally giving EQ's DirectInput cooperative-level negotiation enough wall-clock to settle before BURST 1 fires. ... To skip PATH A safely you need EITHER (a) a real DLL post-connect detection signal so PATH A actually completes and reports success, OR (b) a non-time-based readiness gate before PATH B's BURST 1 (e.g. wait for password-field focus, first scene render, or DI8 cooperative level transition)."

**This is the architectural issue.** We have a path we cannot use (A) feeding wallclock to a path we cannot replace (B). The two paths are coupled by an accidental side effect.

The retry block at [`Core/AutoLoginManager.cs:660-731`](Core/AutoLoginManager.cs) is the only code that has ever worked deterministically: dismiss-modal-with-Enter → 30s server release → BURST 1 → Enter. **The retry IS the working algorithm — it just costs 30 extra seconds.**

**Question for you (Q-B1):** is there a DI8 cooperative-level signal we can poll directly (instead of using PATH A's 45s timeout as a proxy)? We have access to the DI8 proxy state in [`Native/device_proxy.cpp`](Native/device_proxy.cpp); can the DLL surface "BACKGROUND mode achieved" through SHM the way it surfaces `LoginPhase`?

**Question for you (Q-B2):** the "Tested + reverted 2026-04-24" note at [`Core/AutoLoginManager.cs:470-474`](Core/AutoLoginManager.cs):

> "a pre-flight Enter before typing is NOT idempotent on Dalaya patchme — empty-password Enter raises a 'you need to enter a username and password' modal that steals focus from the password field"

If the SHM warmup is *also* raising that modal (via the DLL's empty-form ClickButton), then we live in the modal-up state every login. The retry path's pre-Enter dismisses it. Why don't we always pre-dismiss?

---

### PATH C — MQ2 GiveTime detour (designed 2026-04-15, never built)

**Memory:** [`memory/project_eqswitch_v7_goal_mq2_givetime_detour.md`](../../../.claude/projects/X---Projects/memory/project_eqswitch_v7_goal_mq2_givetime_detour.md)
**Reference impl:** `_.src/_srcexamples/macroquest-rof2-emu/src/main/MQ2LoginFrontend.cpp:50-88` and `src/plugins/autologin/StateMachine.cpp:589-729`

**Intent:** detour `EQMain__LoginController__GiveTime` (eqmain.dll function called every frame from EQ's login game loop). Run autologin state machine **inside EQ's game loop**, not on the Windows message pump. Optional `ShowWindow(SW_HIDE)` on login/server/charselect HWNDs so the user never sees them — click hotkey, see only the in-world loading screen.

**Why this beats both A and B:**
- **Beats A:** detour runs every frame, has direct read access to EQ's render-side EditWnd buffers — no Combo G silent-no-op, no SHM polling latency.
- **Beats B:** no DI8 keystrokes needed at all. No DI8 cooperative-level dance, no focus-faking IAT hooks, no `BACKGROUND` mode requirement.
- **Beats both:** matches MacroQuest's battle-tested approach (years of production use). Source available.

**Why it didn't ship:** memory file lists "~2-4 hours of careful work" and a CE-verified eqmain.dll offset prerequisite. Listed as the right answer, then we did v3.12.0 instead.

**Question for you (Q-C1):** is there any reason PATH C *can't* work on Dalaya that we're missing? The eqmain.dll is the same engine MQ2 detours on RoF2 — we have authoritative offsets in [`_.src/_srcexamples/macroquest-rof2-emu/src/eqlib/include/eqlib/offsets/eqmain.h`](_.src/_srcexamples/macroquest-rof2-emu/src/eqlib/include/eqlib/offsets/eqmain.h) (`pinstLoginController`, `LoginController::GiveTime`). Dalaya is x86, RoF2 reference is x64 — symbol names + class relationships are authoritative, numeric VAs differ.

**Question for you (Q-C2):** if we ship PATH C, what do we delete from the codebase? Estimate of dead-code surface:
- All of PATH A: `LoginShmWriter`, `login_shm.h`, `login_state_machine.cpp`, `eqmain_widgets*.cpp`, `eqmain_cxstr.cpp` (1.5k+ LOC C++)
- Most of PATH B: `KeyInputWriter`, `key_shm.cpp`, `iat_hook.cpp` (focus-faking), `device_proxy.cpp` BACKGROUND-mode handling (~1k+ LOC)
- All BURST 1 code in `RunCredentialEntry` (~120 LOC C#)
- The retry path (~80 LOC C#)
- All "warmup ritual" comments admitting the architecture is wrong

That's ~2.5–3k LOC of deletion. **The code we keep around to apologize for is bigger than the code we'd write fresh.**

---

## 5. Reading guide — the order to read in

For Cloud Claude. Walk this top-down.

1. **The user's spec** — `CLAUDE.md` lines 60-78 ("AUTOLOGIN SPEC — THE ACTUAL REQUIREMENT"). 4 lines, 5-second budget, "zero manual clicks".
2. **The architectural admission** — `Core/AutoLoginManager.cs:548-616`. Read every line of the two adjacent comment blocks. They describe the load-bearing-side-effect coupling between A and B.
3. **PATH B today** — `Core/AutoLoginManager.cs:376-496` (`RunCredentialEntry`). Note that the "warmup" branch is PATH A's failed FSM kept alive purely for its 4-second wallclock side effect.
4. **The retry that actually works** — `Core/AutoLoginManager.cs:660-731`. Note this is the only deterministic credential-entry sequence in the codebase. **Why isn't this the first attempt?**
5. **PATH A's DLL state machine** — `Native/login_state_machine.cpp` end-to-end (671 LOC). Look for `PHASE_WAIT_CONNECT_RESP` (line ~399-415 per the AutoLoginManager comment). Why doesn't it advance on Dalaya?
6. **The widget write that maybe-works-maybe-doesn't** — `Native/eqmain_cxstr.cpp` + `Native/eqmain_widgets.cpp`. The "silent-no-op" vs "verified working" comment contradiction. Read until you can call it.
7. **PATH C reference** — `_.src/_srcexamples/macroquest-rof2-emu/src/main/MQ2LoginFrontend.cpp` (the detour install) + `src/plugins/autologin/StateMachine.cpp` (the in-detour FSM). Compare against our `Native/login_state_machine.cpp` — what does MQ2 do that we don't?
8. **The chesterton fence index** (next section) — every load-bearing comment in `Core/AutoLoginManager.cs` and what specific incident put it there.

---

## 6. Chesterton fence index — every load-bearing comment

(Format: **file:line** — *summary* — incident date — full quote.)

- **`AutoLoginManager.cs:344-345`** — *"load-bearing warmup contract"* — see `memory/feedback_chesterton_fence_load_bearing_bugs.md`
- **`AutoLoginManager.cs:350-355`** — *"On Dalaya the write silent-no-ops"* — describes Combo G failure mode
- **`AutoLoginManager.cs:363-367`** — *"SendCancelCommand fires BEFORE BURST 1 — AFTER caused 4-of-6 char truncation"* — verified 2026-04-25 dual-box
- **`AutoLoginManager.cs:447-462`** — *"DLL ClickButton retry loop contended with C# typing for EQ's message pump"* — same incident as above
- **`AutoLoginManager.cs:470-474`** — *"a pre-flight Enter is NOT idempotent on Dalaya patchme — empty-password Enter raises a modal"* — Tested + reverted 2026-04-24
- **`AutoLoginManager.cs:548-572`** — **THE BIG ONE** — *"⚠ LOAD-BEARING SIDE EFFECT — DO NOT NAIVELY DISABLE ⚠ PATH A's wasted 45s is incidentally giving EQ's DirectInput cooperative-level negotiation enough wall-clock to settle before BURST 1 fires"*
- **`AutoLoginManager.cs:573-580`** — *"To skip PATH A safely you need EITHER (a) a real DLL post-connect detection signal, OR (b) a non-time-based readiness gate"* — the two ways out, neither built
- **`AutoLoginManager.cs:594-601`** — *"PATH A disabled for agent investigation 2026-04-25 — truncation symptom"* — disabled, never re-enabled, kludge has lived 7 days
- **`AutoLoginManager.cs:659-668`** — *"Hotfix 2026-04-24: stale-session auto-recovery"* — the retry path that actually works
- **`AutoLoginManager.cs:870-875`** — *"Hotfix v4 (HIGH-A): MQ2 bridge never came up after 30s wait"* — char-select-side bug, tangentially related
- **`Native/login_state_machine.cpp:31-38`** — *"Dalaya ROF2 uses different gameState values from modern MQ2 (which changed PRECHARSELECT from 6 to -1). Strategy: don't gate on gameState for login screen — gate on widget presence"* — this strategy is contradicted by line 399-415 which DOES gate on gameState

**Pattern:** every comment is in the form *"if you do X you'll break Y, learned 2026-04-NN, don't"*. None of them is in the form *"this is correct because Z"*. We are deep in apology territory.

---

## 7. The four asks (TL;DR)

For Cloud Claude — answer these in order, with file:line evidence:

- **Q1 (architecture):** Of PATH A / PATH B / PATH C, which has the highest probability of being made 100% reliable on Dalaya? Justify with code reading, not vibes. If your answer is C, address Q-C1 first (any reason it can't work that we're missing?).
- **Q2 (today's flake):** Confirm or refute the modal-collision hypothesis from §2. The decisive evidence is in `Native/login_state_machine.cpp` `PHASE_CLICKING_CONNECT` handler — does it call `ClickButton` while `g_password` is empty/uncommitted on Dalaya? Quote the exact lines.
- **Q3 (the contradiction):** does Combo G's password write reach EQ's render/submit buffer on Dalaya, or not? `AutoLoginManager.cs:350` and `AutoLoginManager.cs:553` disagree. Resolve with evidence from `eqmain_cxstr.cpp` + the DLL log format.
- **Q4 (deletion plan):** for whichever path you pick in Q1, list the files + line ranges to delete. Estimate LOC. Identify any chesterton fences that would become safe to remove.

If the answer to Q1 is **PATH C (v7 GiveTime detour)**, also produce:

- A risk register for the offset-verification step (memory file lists Cheat Engine 7.5 from GitHub source — clean — at user's install path).
- A 1-paragraph migration plan: ship PATH C behind a config flag first, default off, A/B against PATH B for one session, then flip default and delete A/B.

---

## 8. Artifacts

- **Today's full log:** `C:\Users\nate\proggy\Everquest\EQSwitch\eqswitch.log` lines 3463–3578 (90s timeout + retry succeeded). Earlier sections of same file have working dual-box logins on 4/26 — useful positive control.
- **Historical log:** `C:\Users\nate\proggy\Everquest\EQSwitch\eqswitch.log.bak` — 4 timeout incidents on 2026-04-25 (17:07/17:09/17:41/17:42).
- **Working anchor binary:** tag `working-autologin-20260424` (md5 `09e138ce56711ac66cb04bbae08c7725` per `memory/feedback_eqswitch_native_anchor.md`). Per memory, "ship-time verify-before-no-regression" — DO NOT rebuild Native/ from HEAD without dual-box end-to-end test.
- **MQ2 reference source:** `X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/`. Authoritative for Path C class layouts + symbol names. Numeric VAs are x64 RoF2 — translate to x86 Dalaya at offset-verify step.
- **CE 7.5 (clean):** GitHub source build per `memory/project_cheatengine_76_malware_2026_04_11.md`. **Do not** download `cheatengine.org` 7.6 — confirmed malware (37/72 VirusTotal).

---

## 9. What we're explicitly NOT asking

- *"Why doesn't BURST 1 type fast enough?"* — typing speed has been tuned 130ms→40ms→25ms in three iterations. Speed is not the issue.
- *"Why does WarmupDwellMs need to be 4s?"* — that knob is a band-aid for the load-bearing-side-effect coupling. Fixing the coupling makes the knob irrelevant.
- *"Should we add another retry?"* — three retries already (BURST 1 → 30s server-release → retry BURST 1 → if char-select still missing, surface to user). Adding a fourth doesn't fix what's broken.
- *"Should we add YESNO dialog handling?"* — `memory/feedback_eqswitch_no_yesno_in_patchme.md` confirms patchme has no kick-session dialog. Don't go there.

---

**Prepared 2026-05-02 from `main` HEAD `0c72bbe`. No source files modified in this PR. Reviewer reads at branch tip.**
