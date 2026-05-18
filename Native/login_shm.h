// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

// login_shm.h -- Shared memory for in-process login state machine
//
// C# creates "Local\EQSwitchLogin_{PID}", writes credentials + command.
// DLL reads credentials, drives EQ's UI widgets, writes state feedback.
// Replaces PostMessage/WM_CHAR + focus-faking login entirely.

#pragma once
#include <windows.h>
#include <stdint.h>

#define LOGIN_SHM_MAGIC   0x45534C53  // "ESLS"
// Version 2 (2026-05-09): added autoLoginActive field at end. Native readers
// at version 1 still see all prior fields correctly (backward-compatible
// append). C# version-2 writers send a 1344-byte mapping; v1 native readers
// see the first 1340 bytes and ignore the trailing field.
//
// Version 3 (2026-05-15): appended live OK_Display poll-tick fields —
// `okDisplayText[256]` and `okDisplayClass`. Distinct from the existing
// `errorMessage` field (which is set ONCE on PHASE_ERROR via SetError and
// stays until the next login). The v3 fields are LIVE: written on every
// poll where g_pOkDisplay returns text, cleared to empty/0 when the dialog
// is gone. Pre-classified by native (1=fatal/2=recoverable/3=success) so
// C# doesn't re-implement strstr matching. Backward-compatible append —
// v2 native readers see the first 1344 bytes unchanged. C# v3 writers
// allocate 1604 bytes; v2 native readers ignore the trailing 260 bytes.
//
// Consumer in C#: AutoLoginManager.RunLoginSequence retry loop reads these
// to distinguish "stale-session" (recoverable, needs ~30s wait) from
// "wrong password" (fatal, abort retry budget) from "truncated creds"
// (recoverable, ~1s wait suffices) — closes the gap explicitly flagged in
// v3.17.0 CHANGELOG ("ALWAYS use staleSessionWaitMs ... distinguishing
// requires the OK_Display error-text probe deferred to v3.18 + SHM v3 bump").
//
// Version 4 (2026-05-15): JoinServerDirect (Diff 4) RPC fields appended.
// C# writes joinServerSerialId + joinServerReqSeq. Native handler in
// login_state_machine.cpp Tick() observes seq increment, calls
// MQ2Bridge::JoinServerDirect(serverID, &fnResult), writes outcome +
// fnResult, then ackSeq = reqSeq. Replaces BURST 2 (server-select Enter
// PostMessage) when the in-process __thiscall succeeds. Backward-
// compatible append — v3 native readers see the first 1604 bytes
// unchanged; C# v4 writers allocate 1624 bytes (v3 native readers ignore
// the trailing 20 bytes — JoinServer never fires because reqSeq stays 0).
//
// Architectural note (per MQ2 RoF2-emu autologin walkthrough §5.1 +
// Diff 4 of `_.eqswitch-re/mq2-autologin-eqswitch-diff.md`): MQ2's
// StateMachine.cpp:773 calls g_pLoginServerAPI->JoinServer((int)server->ID)
// directly to advance from server-select to char-select, bypassing the
// UI server-row click chain entirely. EQSwitch's pre-Diff-4 path used
// VK_RETURN PostMessage (BURST 2) which assumed Dalaya was already
// highlighted. Diff 4's direct call is more deterministic AND removes
// the only BURST surviving the Diff 2/3 structural button click flip.
//
// Version 5 (2026-05-15): single-byte append `comboGWriteOk` at offset 1624
// (4 bytes for alignment). Native sets to 1 inside PHASE_TYPING_CREDENTIALS
// after WriteEditTextDirect read-back succeeds. Cleared to 0 on every new
// LOGIN_CMD_LOGIN. C# AutoLoginManager reads this post-warmup; when set,
// SKIPS BURST 1's primer (Backspace) and password retype to avoid the
// double-write bug — Combo G's structural CXStr write at +0x1A8 has
// already populated the field with the correct 7-char password, and
// keystroke retyping ON TOP would either prepend (cursor at 0) or append
// (cursor at end), corrupting the password to ~13 chars and causing EQ
// login server rejection. BURST 1's Activate + Enter (submit) is retained
// — Enter is what EQ interprets as "click Connect". Backward-compatible
// append: v4 native readers see the first 1624 bytes unchanged and never
// observe the field, so they continue typing keystrokes.
//
// Version 6 (2026-05-15): append `loginServerAPIReady` uint32 at offset
// 1628 (Fix 2 — auth-completion gate for JoinServerDirect dispatch).
// Native Tick() polls *(eqmain+0x150164) every tick:
//   - 0 = pinstLoginServerAPI is NULL OR its vtable doesn't match
//         eqmain+0x1002D0 (RVA_VTABLE_LoginServerAPI_Secondary)
//   - 1 = pinstLoginServerAPI populated AND vtable matches, for ≥3
//         CONSECUTIVE Ticks (stability counter — defends against the
//         transient pinstLoginServerAPI construction state observed
//         at very-early launch / EULA→login screen transitions per
//         2026-05-14 probe history)
// Reset to 0 on every LOGIN_CMD_LOGIN. C# AutoLoginManager polls this
// for up to 5000ms before dispatching JoinServerDirect — if still 0 at
// timeout, skips the dispatch entirely and falls through to BURST 2.
// The 2026-05-15 PM smoke showed JoinServerDirect firing on a fixed 3s
// post-BURST-1 timer returned fnResult=3 ("no auth session") because
// the auth handshake hadn't completed; the ready-gate replaces wall-clock
// timing with auth-state observation.
// Backward-compatible append: v5 native readers see the first 1628
// bytes unchanged and never publish the field, so C# v6 readers always
// read 0 (timeout → BURST 2 fallback — graceful degradation).
// Version 7 (2026-05-16): widget-presence probes appended for v3.21.0.
// Native PollWidgetVisibilityToShm in login_state_machine.cpp polls 5
// SIDL-screen widgets every Tick via EQMainWidgetsMQ2::FindLiveScreenByName
// + IsCXWndVisible. Mirrors MQ2's OnPulse named-screen inspection across
// the GAMESTATE_PRECHARSELECT block (MQ2AutoLogin.cpp:1195-1240, source
// for "connect"/"serverselect"/"okdialog"/"yesnodialog") AND the
// GAMESTATE_CHARSELECT block (MQ2AutoLogin.cpp:1156-1191, source for
// "ConfirmationDialogBox"). Plus ConfirmationDialogBox text mirror (parallel to
// v3's okDisplayText) so C# can detect the "Loading Characters" stuck-
// state and the EULA/orderwindow prompt-windows variant without needing
// a future SHM bump.
//
// Field group:
//   widgetConnectVisible        — connect screen (login UI)
//   widgetServerSelectVisible   — serverselect screen
//   widgetOkDialogVisible       — okdialog (parent of OK_Display)
//   widgetYesNoDialogVisible    — yesnodialog (kick-session prompt)
//   widgetConfirmDialogVisible  — ConfirmationDialogBox (Loading Characters etc.)
//   widgetConfirmDialogText     — CD_TextOutput STML when ConfirmDialog visible,
//                                 empty when not
//   widgetTickSeq               — increments each probe pass; C# uses to
//                                 detect probe staleness (no advance for N
//                                 polls → DLL crashed or unloaded)
//
// Consumer in C# (v3.21.0+): the WidgetState reader exposes these probes for
// logging, verification, and (since v3.22.0) state-machine dispatch in
// RunLoginStateMachine — a tick-loop observer that branches on the widget
// snapshot rather than wall-clock budgets. The legacy RunLoginSequence
// remains in the codebase as an escape hatch; it does not branch on these
// probes (preserved as the pre-state-machine baseline).
//
// Backward-compatible append: v6 native readers see the first 1632 bytes
// unchanged and never publish these fields. C# v7 readers always observe
// 0/empty (interpreted as "no probe data available" → fall through to
// existing rect-stability heuristic — graceful degradation).
// Version 8 (2026-05-16): charSelectAvailable bool appended for v3.22.0 Iter-2A.
// Path A smoke (2026-05-16) confirmed gGameState on Dalaya never advances past
// 0 even at char-select, breaking Native's PHASE_WAIT_CONNECT_RESP →
// PHASE_SERVER_SELECT gating. pinstCCharacterSelect DOES update on Dalaya
// (mq2_bridge tracks the transition for heap-scan cache invalidation), so v8
// exposes it as a separate SHM signal C# can drive transitions from.
// Backward-compatible append: v7 native readers stop reading at byte 1912 and
// never publish this field. C# v8 readers always observe 0 when talking to a
// v7 DLL — graceful degradation (state machine falls through to widget probes).
#define LOGIN_SHM_VERSION 8

// C# -> DLL: what to do
enum LoginCommand : uint32_t {
    LOGIN_CMD_NONE   = 0,
    LOGIN_CMD_LOGIN  = 1,   // Start full login sequence
    LOGIN_CMD_CANCEL = 2,   // Abort in-progress login
};

// DLL -> C#: current login phase
enum LoginPhase : uint32_t {
    PHASE_IDLE                = 0,
    PHASE_WAIT_LOGIN_SCREEN   = 1,
    PHASE_TYPING_CREDENTIALS  = 2,
    PHASE_CLICKING_CONNECT    = 3,
    PHASE_WAIT_CONNECT_RESP   = 4,
    PHASE_SERVER_SELECT       = 5,
    PHASE_WAIT_SERVER_LOAD    = 6,
    PHASE_CHAR_SELECT         = 7,
    PHASE_ENTERING_WORLD      = 8,
    PHASE_COMPLETE            = 10,
    PHASE_ERROR               = 99,
};

#define LOGIN_MAX_CHARS     10
#define LOGIN_NAME_LEN      64
#define LOGIN_PASS_LEN      128
#define LOGIN_SERVER_LEN    64
#define LOGIN_CHAR_LEN      64
#define LOGIN_ERROR_LEN     256

#pragma pack(push, 1)
struct LoginShm {
    // Header
    uint32_t     magic;                         // LOGIN_SHM_MAGIC
    uint32_t     version;                       // LOGIN_SHM_VERSION

    // C# -> DLL: login request
    LoginCommand command;                       // Set by C#, cleared by DLL
    uint32_t     commandSeq;                    // Incremented by C# per request
    uint32_t     commandAck;                    // Set by DLL when picked up
    char         username[LOGIN_NAME_LEN];      // Plaintext username
    char         password[LOGIN_PASS_LEN];      // Plaintext (decrypted by C#)
    char         server[LOGIN_SERVER_LEN];      // Target server name
    char         character[LOGIN_CHAR_LEN];     // Target character name

    // DLL -> C#: state feedback
    LoginPhase   phase;                         // Current login phase
    int32_t      gameState;                     // Raw EQ gGameState
    char         errorMessage[LOGIN_ERROR_LEN]; // Set on PHASE_ERROR
    uint32_t     retryCount;                    // Retries attempted

    // DLL -> C#: character data (populated at char select)
    int32_t      charCount;
    int32_t      selectedIndex;
    char         charNames[LOGIN_MAX_CHARS][LOGIN_NAME_LEN];
    int32_t      charLevels[LOGIN_MAX_CHARS];
    int32_t      charClasses[LOGIN_MAX_CHARS];

    // Diagnostic: widget enumeration mode
    uint32_t     diagnosticMode;                // 1 = enumerate all widgets to log

    // C# -> DLL: AutoLoginManager active flag (v2 / 2026-05-09).
    // Set to 1 by C# AutoLoginManager from BURST 1 setup through cleanup;
    // cleared on every exit path (success/failure/exception) in the finally
    // block. Used by eqswitch-di8.cpp's pre-login kPromptWindows[] dismiss
    // machinery (v3.15.5) to STAND DOWN during autologin — the C#-driven
    // BURST flow owns keystroke injection, and concurrent native widget-
    // clicks at server-select / charselect-load can close the EQ process
    // (root cause of the 2026-05-09 team1 regression).
    //
    // volatile because C# writes from a different process via the shared
    // mapping; volatile prevents the compiler from caching the read across
    // the per-tick poll loop.
    //
    // 0 = bare launch (kPromptWindows iteration runs as designed for EULA
    //     auto-dismiss). Default state — old configs / pre-autologin start
    //     /post-autologin cleanup all read 0 here.
    // 1 = autologin in progress (kPromptWindows iteration is suppressed for
    //     this PID). C# clears in the RunLoginSequence finally block.
    volatile uint32_t autoLoginActive;

    // ── v3 (2026-05-15) — Live OK_Display poll-tick mirror ─────────
    //
    // DLL -> C#: the LIVE text inside EQ's OK_Display widget on the most
    // recent poll, plus a pre-classification so C# doesn't have to re-
    // implement the strstr matching that login_state_machine.cpp already
    // does. Native classification lives in the `ClassifyDialogText` helper
    // (single source of truth as of R2 2026-05-15) — both the always-on
    // PollOkDisplayToShm probe and the in-phase PHASE_WAIT_CONNECT_RESP
    // body call it.
    //
    // Distinct from `errorMessage` above (which is SET ONCE by SetError on
    // PHASE_ERROR transition and stays until next login). These v3 fields
    // are LIVE: written on every PHASE_WAIT_CONNECT_RESP tick where
    // g_pOkDisplay has text, cleared to empty/None when no dialog is up.
    //
    // Consumer: AutoLoginManager.RunLoginSequence retry loop in C# uses
    // okDisplayClass to decide:
    //   - Class=Fatal (1) → break out of retry budget (no further retries
    //     can help; "Invalid Password" doesn't get fixed by re-typing).
    //   - Class=Recoverable (2) → tune staleSessionWaitMs from text:
    //     contains "stale" → 30s; contains "truncated" → 1s; else default.
    //   - Class=Success (3) → fall through to existing gameState gate (the
    //     "Logging in to the server" message means EQ is mid-handshake;
    //     don't dismiss with blind Enter).
    //   - Class=None (0) → no dialog; fall through to existing gateState gate.
    //
    // volatile because cross-process shared mapping. Backward-compatible
    // append: v2 native readers (no recompile of bridge code) see the
    // first 1344 bytes unchanged and ignore the trailing 260 bytes.
    char              okDisplayText[LOGIN_ERROR_LEN];
    volatile uint32_t okDisplayClass;   // 0=None, 1=Fatal, 2=Recoverable, 3=Success

    // ── v4 (2026-05-15) — Diff 4 JoinServerDirect RPC ─────────────
    //
    // C# → DLL: serverID + reqSeq increment requests one JoinServerDirect
    //   dispatch. Native writes outcome + fnResult then sets ackSeq=reqSeq.
    //
    // C# init-side preconditions (caller responsibility):
    //   - LoginShmWriter.Open succeeded (mapping is 1624 bytes, this version)
    //   - autoLoginActive must be 1 (defends against stray seq increments
    //     from leaked mappings on bare launches; native handler gates on it)
    //   - reqSeq starts at 0 and increments PER REQUEST (per-PID, owned by C#)
    //
    // Outcome semantics (joinServerOutcome):
    //   0 = pending (initial state, or DLL hasn't observed reqSeq yet)
    //   1 = SUCCESS — JoinServerDirect returned true; fnResult contains
    //       JoinServer's actual return code (0 = network dispatch OK,
    //       non-zero = EQ-side error code; caller interprets per game state)
    //   2 = JOINSERVER_FAILED — JoinServerDirect returned false (one of:
    //       eqmain not loaded, pinstLoginServerAPI null, vtable mismatch,
    //       prologue patch detected, SEH inside the call). fnResult is 0.
    //       This is the "fall back to BURST 2" trigger on the C# side.
    //   3 = SHM_GATED — autoLoginActive was 0 when reqSeq incremented.
    //       Native refuses to dispatch (defense against leaked mappings).
    //       fnResult is 0.
    //
    // Timing: native processes the request in Tick() (called from the
    // ActivateThread + GiveTime detour cadence, ~16ms typical). Total
    // round-trip from C# write to ackSeq observable is typically ~50ms.
    // C# should poll with bounded timeout (recommend 2000ms — generous,
    // covers any LoadLibraryA contention inside JoinServerDirect).
    //
    // volatile because cross-process shared mapping. Backward-compatible
    // append: v3 native readers (1604-byte struct in their #include) see
    // exactly the first 1604 bytes — they NEVER read these fields and
    // NEVER fire JoinServerDirect, regardless of what C# writes here.
    volatile uint32_t joinServerSerialId;   // C# in:  server ID for JoinServer call
    volatile uint32_t joinServerReqSeq;     // C# in:  increment per request
    volatile uint32_t joinServerAckSeq;     // DLL out: set to reqSeq when processed
    volatile uint32_t joinServerOutcome;    // DLL out: 0=pending, 1=success, 2=failed, 3=gated
    volatile uint32_t joinServerFnResult;   // DLL out: JoinServer return code (only valid if outcome==1)

    // ── v5 (2026-05-15) — Combo G success signal (Fix 1 for the double-write bug) ──
    //
    // DLL → C#: set to 1 when WriteEditTextDirect read-back confirmed the
    // password was written to InputText+0x1A8 in the current login session.
    // Cleared to 0 on every LOGIN_CMD_LOGIN (new session).
    //
    // C# AutoLoginManager.RunCredentialEntry reads this post-warmup. When
    // the warmup advanced AND comboGWriteOk == 1, BURST 1 SKIPS the primer
    // (Backspace) and password retype but STILL fires Enter (submit). This
    // eliminates the double-write — Combo G's CXStr write at +0x1A8 already
    // populated the field correctly; keystrokes typed on top would corrupt
    // the password to ~13 chars (cursor-at-0 prepend OR cursor-at-end
    // append depending on EQ's edit-mode state).
    //
    // 2026-05-15 smoke evidence: with both Combo G AND BURST 1 firing,
    // EQ login server rejected the corrupted password, no auth session
    // was created, and the subsequent JoinServerDirect dispatch returned
    // fnResult=3 ("no auth session") which EQ surfaces as "Quick Connect
    // to server Dalaya failed. Going to server select instead." The user's
    // observation: "quick connect would have been successful if we had
    // entered the letters for the password" — i.e., correct 7-char
    // password instead of the 13-char double-write garbage.
    //
    // volatile because cross-process shared mapping. Backward-compatible
    // append: v4 native readers see exactly the first 1624 bytes — they
    // never observe this field, so they always run BURST 1 (matches v4
    // behavior). Field is never read on the bare-launch / non-autologin
    // path either, so no overhead concern.
    volatile uint32_t comboGWriteOk;

    // ── v6 (2026-05-15) — LoginServerAPI-ready gate (Fix 2) ────────
    //
    // DLL → C#: 1 = `pinstLoginServerAPI` at eqmain+0x150164 is populated
    // AND its vtable[0] equals eqmain+0x1002D0 (the documented secondary
    // vtable) for ≥3 CONSECUTIVE Ticks. 0 = not yet ready or has gone bad.
    //
    // The stability counter (3 consecutive populated ticks) defends against
    // the transient construction state observed at very-early launch /
    // EULA→login screen transitions per 2026-05-14 RVA probe history. A
    // single-tick check would race the transition.
    //
    // C# AutoLoginManager.TryJoinServerDirectOrFallback polls this for up
    // to ~5000ms before sending the JoinServerDirect request. If still 0
    // at timeout, dispatch is SKIPPED entirely and the caller falls through
    // to BURST 2 (server-select Enter via PostMessage). This replaces the
    // pre-Fix-2 wall-clock-only model (PostBurst1WaitMs=3000ms) which
    // dispatched too early and returned fnResult=3 ("no auth session") on
    // the 2026-05-15 PM smoke.
    //
    // Reset to 0 on every LOGIN_CMD_LOGIN so a stale "1" from a prior
    // session can't trick C# into firing JoinServerDirect before the new
    // session's auth completes.
    //
    // volatile: cross-process shared mapping. Backward-compatible append:
    // v5 native readers see exactly the first 1628 bytes — they never
    // publish this field, so C# v6 readers always observe 0 → timeout →
    // BURST 2 fallback (graceful degradation matching pre-Fix-2 behavior).
    volatile uint32_t loginServerAPIReady;

    // ── v7 (2026-05-16) — Widget-presence probes ─────────────────
    //
    // DLL → C#: per-tick visibility snapshot for 5 SIDL-screen widgets.
    // Each field is 1 when EQMainWidgetsMQ2::FindLiveScreenByName resolved
    // a non-null widget AND IsCXWndVisible returned true on the most
    // recent tick. 0 otherwise (widget not found, not visible, or eqmain
    // not loaded). All cleared to 0 when loginShm magic-gated probe exits
    // early (e.g., bare-launch PIDs without an open mapping).
    //
    // widgetConfirmDialogText: when widgetConfirmDialogVisible == 1, the
    // 256-byte UTF-8 STML body of ConfirmationDialogBox.CD_TextOutput.
    // Empty when ConfirmDialog not visible. The field is exposed for native
    // diagnostic logging and C#-side observability; consumer-side matching
    // ("Loading Characters" / "Do you accept these rules?" / "characters
    // missing") is permanently deferred per the project-final-release
    // closeout — no current trigger justifies the action-routing surface.
    //
    // widgetTickSeq: monotonic increment per probe pass. C# can read this
    // twice ~100ms apart to verify the DLL is alive and probing. Wraps at
    // UINT32_MAX which gives ~32 years at 2Hz — non-issue.
    volatile uint32_t widgetConnectVisible;
    volatile uint32_t widgetServerSelectVisible;
    volatile uint32_t widgetOkDialogVisible;
    volatile uint32_t widgetYesNoDialogVisible;
    volatile uint32_t widgetConfirmDialogVisible;
    char              widgetConfirmDialogText[LOGIN_ERROR_LEN];
    volatile uint32_t widgetTickSeq;

    // ── v8 (2026-05-16) — pinstCCharacterSelect availability gate ───
    //
    // DLL → C#: 1 when MQ2's exported pinstCCharacterSelect points to a
    // valid CCharacterSelect window (Dalaya-reliable char-select signal),
    // 0 otherwise. Set per-Tick after the gameState read (Tick body in
    // login_state_machine.cpp), so the value reflects the same poll-tick
    // as gameState + widget probes — atomic from C#'s perspective at
    // typical C# tick interval (250ms).
    //
    // Why a separate field instead of routing through gameState: gGameState
    // on Dalaya stays at 0 throughout the entire login pipeline (Path A
    // smoke 2026-05-16 18:02 confirmed via per-tick observation logging;
    // DLL log shows zero `gameState X -> Y` transitions across 6+ min
    // post-cancel even with both clients sitting at char-select). Root
    // cause requires Ghidra/RE on Dalaya's patched dinput8.dll — either
    // EQ stops writing gGameState post-login, or Native reads a stale
    // offset. Out of scope for v3.22.0; we route around it.
    //
    // Consumer: AutoLoginManager.RunLoginStateMachine (v3.22.0 Iter-2B)
    // uses this as the primary trigger for PHASE_WAIT_CONNECT_RESP →
    // CharSelect transition. Widget probes (ConnectVisible / ServerSelect
    // / OkDialog / YesNoDialog / ConfirmDialog) cover the rest of the
    // pipeline; charSelectAvailable covers the gap where EQ has dismissed
    // the login screen but server-select probes never light up (Dalaya's
    // QUICK-CONNECT button slot=+0x34 skips server-select).
    //
    // volatile because cross-process shared mapping; volatile prevents the
    // compiler from caching the read across the per-Tick poll loop.
    volatile uint32_t charSelectAvailable;
};
#pragma pack(pop)
