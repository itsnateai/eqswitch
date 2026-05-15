// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

// login_state_machine.cpp -- In-process login via MQ2 UI widget manipulation
//
// Drives EQ's login flow by calling CXWnd::SetWindowText on edit fields
// and SendWndNotification(XWM_LCLICK) on buttons. All in-process -- no
// focus faking, no PostMessage, no key injection needed.
//
// Reference: MQ2AutoLogin StateMachine.cpp (macroquest-rof2-emu)
// Widget names: LOGIN_UsernameEdit, LOGIN_PasswordEdit, LOGIN_ConnectButton,
//               OK_Display, OK_OKButton, YESNO_YesButton, Character_List

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <string.h>
#include "login_state_machine.h"
#include "eqmain_cxstr.h"
#include "eqmain_widgets.h"
#include "eqmain_widgets_mq2style.h"  // ITER 12: MQ2-style structural lookup
#include "eqmain_offsets.h"
#include "mq2_bridge.h"               // Diff 4 — MQ2Bridge::JoinServerDirect

void DI8Log(const char *fmt, ...);

// ─── Diff 4 (2026-05-15) — JoinServerDirect request tracking ─────────
// Per-DLL-load monotonic counter of the LAST joinServerReqSeq we processed.
// Reset to 0 by the LOGIN_CMD_LOGIN handler so a new login session starts
// with a clean slate (in case the prior session left a non-zero reqSeq in
// the SHM mapping that's still around if C# kept the mapping open).
static uint32_t g_lastJoinServerReqSeq = 0;

// ─── XWM constants (from MQ2 EQClasses.h) ─────────────────────
#define XWM_LCLICK          1
#define XWM_LMOUSEUP        2
#define XWM_RCLICK          3

// ─── Game state constants ──────────────────────────────────────
// Dalaya ROF2 uses different values from modern MQ2 (which changed
// PRECHARSELECT from 6 to -1). We discover the actual values at runtime.
// Known from DLL log: login screen = 0, charselect = ?, ingame = ?
// Strategy: don't gate on gameState for login screen — gate on widget presence.
#define GAMESTATE_CHARSELECT      1    // Verify via testing
#define GAMESTATE_INGAME          5    // Verify via testing

// ─── Internal state ────────────────────────────────────────────

static LoginPhase g_phase = PHASE_IDLE;
static uint32_t   g_lastCommandSeq = 0;
static uint32_t   g_retryCount = 0;
static DWORD      g_phaseEntryTick = 0;   // GetTickCount when phase was entered
static DWORD      g_lastActionTick = 0;   // Debounce for widget interactions
static int        g_lastGameState = -99;  // Track game state transitions
static bool       g_loginBtnClicked = false;  // v7 Phase 6: main menu LOGIN button
static int        g_loginBtnAttempts = 0;
static bool       g_widgetsCached = false;
static uint32_t   g_yesBtnAttempts = 0;   // kick-session dialog retry counter
static int        g_connectGameState = -99;  // Track gameState when connect was clicked
static int        g_charSelGameState = -99;  // Track gameState when charselect was entered
static int        g_connectButtonRetries = 0;  // Fix (1) 2026-04-25: gate ClickButton on real CButtonWnd vtable

// Cached widget pointers (invalidated on game state change)
static void *g_pUsernameEdit = nullptr;
static void *g_pPasswordEdit = nullptr;
static void *g_pConnectButton = nullptr;
static void *g_pOkDisplay = nullptr;
static void *g_pOkButton = nullptr;
static void *g_pYesButton = nullptr;
static void *g_pCharList = nullptr;
static void *g_pEnterWorldBtn = nullptr;

// Credentials (copied from SHM on command receipt, cleared after use)
static char g_username[LOGIN_NAME_LEN] = {};
static char g_password[LOGIN_PASS_LEN] = {};
static char g_server[LOGIN_SERVER_LEN] = {};
static char g_character[LOGIN_CHAR_LEN] = {};

// ─── Helpers ───────────────────────────────────────────────────

static void SetPhase(volatile LoginShm *shm, LoginPhase phase) {
    g_phase = phase;
    shm->phase = phase;
    g_phaseEntryTick = GetTickCount();
    g_lastActionTick = GetTickCount();
    DI8Log("login_sm: phase -> %d", (int)phase);
}

static void SetError(volatile LoginShm *shm, const char *msg) {
    g_phase = PHASE_ERROR;
    shm->phase = PHASE_ERROR;
    strncpy((char *)shm->errorMessage, msg, LOGIN_ERROR_LEN - 1);
    ((char *)shm->errorMessage)[LOGIN_ERROR_LEN - 1] = '\0';
    memset(g_password, 0, sizeof(g_password)); // Clear password on error
    // Fix 1 round-3 verifier-fix (T3-S MED, T3-O HIGH 2026-05-15): clear the
    // v5 comboGWriteOk flag on error too. SetError is the terminal failure
    // path — leaving the flag set could mislead a C# retry that reads it
    // before the next LOGIN_CMD_LOGIN re-clears it. Self-healing on the
    // next LOGIN regardless, but defensive consistency with the CANCEL clear.
    shm->comboGWriteOk = 0;
    DI8Log("login_sm: ERROR: %s", msg);
}

// v3 SHM (2026-05-15): publish the LIVE OK_Display dialog text + a pre-
// classified bucket so C#'s RunLoginSequence retry loop can tune behavior
// without re-implementing the strstr matching here. Distinct from
// SetError() — SetError is SET-ONCE on fatal classification, this is LIVE
// per-poll mirror that gets cleared when the dialog disappears.
//
// classBucket values:
//   0 = none       (no dialog up, OR g_pOkDisplay returned empty text)
//   1 = fatal      ("password were not valid", "Invalid Password",
//                   "enter a username and password")
//   2 = recoverable (any other dialog text — stale-session, server-busy,
//                   truncated-creds, etc.)
//   3 = success    ("Logging in to the server")
static void SetOkDisplay(volatile LoginShm *shm, const char *text, uint32_t classBucket) {
    if (text && text[0]) {
        strncpy((char *)shm->okDisplayText, text, LOGIN_ERROR_LEN - 1);
        ((char *)shm->okDisplayText)[LOGIN_ERROR_LEN - 1] = '\0';
    } else {
        ((char *)shm->okDisplayText)[0] = '\0';
    }
    shm->okDisplayClass = classBucket;
}

// Helper: clear the LIVE OK_Display fields. Called when we leave
// PHASE_WAIT_CONNECT_RESP (no dialog is being polled there anymore) or
// when g_pOkDisplay returns empty text on a tick where it was previously
// populated. Safe to call repeatedly — idempotent.
static void ClearOkDisplay(volatile LoginShm *shm) {
    ((char *)shm->okDisplayText)[0] = '\0';
    shm->okDisplayClass = 0;
}

// R2 (v3.18.0 verifier T2-O #4 + T3-O P1 #4 + T3-S LOW): single source of
// truth for OK_Display dialog-text classification. Was duplicated between
// PollOkDisplayToShm (always-on probe) and the in-phase
// PHASE_WAIT_CONNECT_RESP body — adding a new fatal pattern to one site
// without the other would cause silent classification drift. The duplication
// was acknowledged in code-comments as "the honest cost of having a phase-
// independent mirror" but the verifier sweep convergent-flagged it as a
// real maintenance hazard worth fixing.
//
// Returns the classBucket value used by SetOkDisplay:
//   1 = Fatal       (login server rejected — re-typing won't help)
//   2 = Recoverable (any other dialog text — server-busy, stale-session, etc.)
//   3 = Success     (server accepted, mid-handshake)
//
// Caller guarantees text is non-empty (this returns the wrong bucket for
// empty text — empty should ClearOkDisplay, not Classify).
static uint32_t ClassifyDialogText(const char *text) {
    if (strstr(text, "password were not valid") ||
        strstr(text, "Invalid Password") ||
        strstr(text, "enter a username and password")) {
        return 1; // Fatal
    }
    if (strstr(text, "Logging in to the server")) {
        return 3; // Success ("connecting...")
    }
    return 2; // Recoverable (stale-session, server-busy, truncated, etc.)
}

// Forward decl — defined ~line 193 below. Needed because the standalone
// always-on probe lives above the original PHASE_WAIT_CONNECT_RESP-only
// callers in the file.
static void DiscoverDialogWidgets();

// v3 SHM (2026-05-15): standalone always-on OK_Display probe.
//
// Called from Tick() on every poll regardless of state machine phase.
// The state machine sits at PHASE_IDLE for the entirety of today's PATH B
// (C# keystroke retry) flow — PATH A (TryLoginViaShm in AutoLoginManager.cs
// line ~795) is currently commented out, so the state machine never enters
// PHASE_WAIT_CONNECT_RESP and the in-phase SetOkDisplay calls don't fire.
//
// Without this standalone probe, the v3 LIVE OK_Display SHM mirror would
// be dormant in today's actual login flow (only useful when PATH A is
// reanimated). The probe's purpose: publish dialog state to SHM
// continuously so the C# v3.17.0 retry loop in AutoLoginManager.cs can
// distinguish stale-session from wrong-password from truncated-creds even
// while the DLL state machine is idle.
//
// Cost: two FindWindowByName heap-walks per 500ms tick. Negligible
// compared to the bridge's existing pre-login kPromptWindows[] walk and
// HeapScanForWidget calls. Re-uses g_pOkDisplay / g_pOkButton cache.
//
// Bypass policy: only fires when loginShm is mapped (i.e., AutoLoginManager
// has explicitly opened the LoginShm for this PID via LoginShmWriter.Open).
// Bare-launch processes (no LoginShm mapping) never reach this code path
// — Tick() returns immediately at the magic-mismatch check at line 239.
static void PollOkDisplayToShm(volatile LoginShm *shm) {
    DiscoverDialogWidgets();   // resolves g_pOkDisplay if a dialog is up

    if (!g_pOkDisplay) {
        ClearOkDisplay(shm);
        return;
    }

    char dialogText[512] = {};
    MQ2Bridge::ReadWindowText(g_pOkDisplay, dialogText, sizeof(dialogText));

    if (!dialogText[0]) {
        // Widget cached but text empty — dialog was just dismissed, OR
        // is mid-construction. Clear so C# doesn't see stale text from
        // a prior tick where the dialog WAS populated.
        ClearOkDisplay(shm);
        return;
    }

    // R2 (v3.18.0): single source of truth via ClassifyDialogText helper.
    // The in-phase PHASE_WAIT_CONNECT_RESP body uses the same helper, so
    // adding a new pattern only needs editing one site.
    SetOkDisplay(shm, dialogText, ClassifyDialogText(dialogText));
}

static DWORD PhaseAge() {
    return GetTickCount() - g_phaseEntryTick;
}

static DWORD SinceLastAction() {
    return GetTickCount() - g_lastActionTick;
}

// ─── Widget invalidation ───────────────────────────────────────

static void InvalidateWidgets() {
    g_pUsernameEdit = nullptr;
    g_pPasswordEdit = nullptr;
    g_pConnectButton = nullptr;
    g_pOkDisplay = nullptr;
    g_pOkButton = nullptr;
    g_pYesButton = nullptr;
    g_pCharList = nullptr;
    g_pEnterWorldBtn = nullptr;
    g_widgetsCached = false;
    g_connectButtonRetries = 0;  // Fix Native C1 (agent verify): reset in lockstep with widget-ptr nulls
    // ITER 12 v4 (2026-04-26): the v3 ResetPasswordCache() pair-call was REVERTED.
    // It looked right on paper (Agent #2 secondary-latent flag), but in practice
    // InvalidateWidgets fires on EVERY gameState transition (line 213), including
    // login→server-select→char-select. Nuking g_cachedXMLIndex mid-flow forced
    // every subsequent BURST keystroke through a slow ResolvePasswordXMLIndex
    // bootstrap. Revert to v2's behavior — the SM-layer cache (g_pPasswordEdit)
    // and native cache (g_cachedWidgetPtr) intentionally diverge; native re-validates
    // on every call so a stale ptr is benign.
}

// ─── Widget discovery ──────────────────────────────────────────
// Uses MQ2Bridge's FindWindowByName to locate login screen widgets.
// Widget names from MQ2AutoLogin (ROF2 emu -- matches Dalaya).

static int g_discoverAttempts = 0;

static void DiscoverLoginWidgets() {
    if (g_widgetsCached) return;
    g_discoverAttempts++;

    // Iter 13 (2026-04-25): use structural password widget lookup as the
    // login-screen-ready signal. Cheap (~0.5s heap scan via vtable match)
    // and tells us the login UI's CEditWnd allocations are complete.
    // Defer LOGIN_ConnectButton heap-cross-ref scan to PHASE_CLICKING_CONNECT
    // (~3.2s) — runs AFTER the password is typed, so the user-visible
    // experience is "screen up → password instantly fills → brief pause →
    // connect clicks" rather than "screen up → 5s idle → password → connect".
    g_pUsernameEdit  = nullptr;
    g_pConnectButton = nullptr;  // resolved in PHASE_CLICKING_CONNECT

    void *pPasswordWidget = EQMainWidgets::FindLivePasswordCEditWnd();
    if (pPasswordWidget) {
        // Iter 14.2: cache the widget pointer so PHASE_TYPING_CREDENTIALS
        // doesn't have to repeat the heap scan. Saves ~600ms on the
        // critical path. Invalidated by InvalidateWidgets() on retry.
        g_pPasswordEdit = pPasswordWidget;
        g_widgetsCached = true;
        DI8Log("login_sm: login screen ready — structural password widget @ %p (XMLIndex=0x%08X) "
               "after %d attempts; LOGIN_ConnectButton resolution deferred to click phase",
               pPasswordWidget, EQMainWidgets::GetCachedPasswordXMLIndex(), g_discoverAttempts);
    } else if (g_discoverAttempts <= 5 || g_discoverAttempts % 20 == 0) {
        DI8Log("login_sm: login screen not ready (password widget unfound) attempt %d",
               g_discoverAttempts);

        if (g_discoverAttempts == 3) {
            DI8Log("login_sm: running diagnostic enumeration...");
            MQ2Bridge::EnumerateAllWindows();
        }
    }
}

static void DiscoverDialogWidgets() {
    g_pOkDisplay = MQ2Bridge::FindWindowByName("OK_Display");
    g_pOkButton  = MQ2Bridge::FindWindowByName("OK_OKButton");
    // YESNO_YesButton is the "kick existing session" confirm button.
    // EQSwitch launches eqgame.exe with `patchme` (LaunchManager), which
    // bypasses the kick-session flow on Dalaya entirely — no YESNO dialog
    // is ever displayed. Resolving the widget name here anyway pulled a
    // stale CXMLDataPtr *definition* pointer (always present in eqmain's
    // memory) which caused PHASE_WAIT_CONNECT_RESP to loop-click a
    // phantom button for 20 attempts before SetError'ing out.
    // Confirmed live 2026-04-23 via Nate — no YES button on his patchme
    // login flow. Leaving the phase-4 `if (g_pYesButton)` check wired:
    // if a future non-patchme flow needs it, re-enable resolution here.
    g_pYesButton = nullptr;
}

static void DiscoverCharSelectWidgets() {
    g_pCharList      = MQ2Bridge::FindWindowByName("Character_List");
    g_pEnterWorldBtn = MQ2Bridge::FindWindowByName("CLW_EnterWorldButton");

    if (g_pCharList)
        DI8Log("login_sm: char select widgets found (list=%p enter=%p)",
               g_pCharList, g_pEnterWorldBtn);
}

// ─── Diagnostic: enumerate all windows ─────────────────────────

static bool g_diagnosticDone = false;

static void RunDiagnostic(int gameState) {
    // Only run once per game state change
    static int lastDiagState = -99;
    if (gameState == lastDiagState && g_diagnosticDone) return;
    lastDiagState = gameState;
    g_diagnosticDone = true;

    DI8Log("=== DIAGNOSTIC: Enumerating windows at gameState=%d ===", gameState);
    MQ2Bridge::EnumerateAllWindows();
    DI8Log("=== DIAGNOSTIC: Enumeration complete ===");
}

// ─── State machine tick ────────────────────────────────────────

namespace LoginStateMachine {

void Tick(volatile LoginShm *loginShm, volatile CharSelectShm *charSelShm) {
    if (!loginShm || loginShm->magic != LOGIN_SHM_MAGIC) return;

    // Read game state
    int gameState = MQ2Bridge::ReadGameState();
    loginShm->gameState = gameState;

    // Invalidate widget cache on game state transitions
    if (gameState != g_lastGameState) {
        DI8Log("login_sm: gameState %d -> %d", g_lastGameState, gameState);
        InvalidateWidgets();
        g_lastGameState = gameState;
    }

    // Diagnostic mode: enumerate widgets and return
    if (loginShm->diagnosticMode) {
        RunDiagnostic(gameState);
        return;
    }

    // Check for new command from C#
    uint32_t cmdSeq = loginShm->commandSeq;
    if (cmdSeq != g_lastCommandSeq) {
        g_lastCommandSeq = cmdSeq;
        loginShm->commandAck = cmdSeq;

        LoginCommand cmd = loginShm->command;
        DI8Log("login_sm: command=%d seq=%u", (int)cmd, cmdSeq);

        if (cmd == LOGIN_CMD_LOGIN) {
            // Copy credentials locally and zero SHM password immediately
            strncpy(g_username, (const char *)loginShm->username, LOGIN_NAME_LEN - 1);
            g_username[LOGIN_NAME_LEN - 1] = '\0';
            strncpy(g_password, (const char *)loginShm->password, LOGIN_PASS_LEN - 1);
            g_password[LOGIN_PASS_LEN - 1] = '\0';
            strncpy(g_server, (const char *)loginShm->server, LOGIN_SERVER_LEN - 1);
            g_server[LOGIN_SERVER_LEN - 1] = '\0';
            strncpy(g_character, (const char *)loginShm->character, LOGIN_CHAR_LEN - 1);
            g_character[LOGIN_CHAR_LEN - 1] = '\0';

            // Zero password in SHM after copying
            memset((void *)loginShm->password, 0, LOGIN_PASS_LEN);

            g_retryCount = 0;
            loginShm->retryCount = 0;
            g_loginBtnClicked = false;
            g_loginBtnAttempts = 0;
            g_yesBtnAttempts = 0;
            // Diff 4: reset JoinServer req-seq tracking so a stale C# reqSeq
            // from a prior session (in case the mapping outlived the prior
            // RunLoginSequence) doesn't get re-processed as if new. The
            // canonical reset is "C# starts each login session with reqSeq=0
            // for that PID" — but C# may keep the mapping open across
            // logins, in which case reqSeq is monotonic across sessions,
            // not per-session. Resetting g_last to current observed value
            // (not 0) prevents re-processing without forcing C# to track
            // per-session base seq.
            g_lastJoinServerReqSeq = loginShm->joinServerReqSeq;
            // Also clear stale outcome from any prior session so C# sees
            // pending=0 until the next dispatch completes.
            loginShm->joinServerOutcome = 0;
            loginShm->joinServerFnResult = 0;
            // Fix 1 (v5 SHM, 2026-05-15): clear comboGWriteOk so this session's
            // Combo G success is not confused with a prior session's. C# only
            // honors the gate when the flag is set DURING the current session.
            loginShm->comboGWriteOk = 0;
            InvalidateWidgets();
            SetPhase(loginShm, PHASE_WAIT_LOGIN_SCREEN);
            // Username is a credential half on Dalaya — redacted for parity with
            // the v3.15.5 LoginShmWriter.SendLoginCommand C# log change. Server
            // and character are non-secret. (v3.15.6 — closed asymmetric leak.)
            DI8Log("login_sm: LOGIN command — user=<redacted> server='%s' char='%s'",
                   g_server, g_character);
        }
        else if (cmd == LOGIN_CMD_CANCEL) {
            memset(g_password, 0, sizeof(g_password));
            // Fix 1 round-3 verifier-fix (T2-O C1, T3-S H1, T3-O H2 convergent
            // 2026-05-15): CANCEL must clear the v5 comboGWriteOk flag so that
            // a future re-LOGIN doesn't observe a stale "1" from this aborted
            // session before native PHASE_TYPING_CREDENTIALS re-runs and
            // re-publishes. The next LOGIN_CMD_LOGIN handler also clears it
            // (line ~395 above), but adding the clear here too closes the
            // narrow race where C# reads ReadComboGWriteOk between CANCEL
            // and the next LOGIN being processed.
            loginShm->comboGWriteOk = 0;
            SetPhase(loginShm, PHASE_IDLE);
            DI8Log("login_sm: CANCEL command");
        }

        loginShm->command = LOGIN_CMD_NONE;
    }

    // ─── Diff 4 (v4 SHM, 2026-05-15) — JoinServerDirect RPC ────────
    // Process JoinServer requests independently from the LOGIN command
    // channel. C# fires this in the post-BURST-1 settle window after the
    // login submit, when LoginServerAPI is expected to be populated (server
    // list returned from network).
    //
    // Gated on autoLoginActive — if 0, the request is REFUSED (outcome=3).
    // This defends against:
    //   (a) Stray SHM writes from a leaked mapping after RunLoginSequence's
    //       finally block clears autoLoginActive — keystroke retry path is
    //       owned by C# at that point; firing JoinServerDirect would race.
    //   (b) Bare launches that somehow get a non-zero reqSeq written —
    //       autoLoginActive is the singular "C# is actively driving this
    //       login" gate, mirroring the existing OK_Display polling gate.
    //
    // SEH-wrapped: MQ2Bridge::JoinServerDirect already wraps its internal
    // calls, but the read of joinServerReqSeq itself crosses the process
    // boundary; the volatile qualifier ensures the compiler doesn't cache
    // it across iterations, but doesn't ward off torn reads on
    // misconfigured mappings. The seq-comparison-then-process pattern is
    // single-write-single-read so atomicity isn't required.
    {
        uint32_t reqSeq = loginShm->joinServerReqSeq;
        if (reqSeq != g_lastJoinServerReqSeq) {
            g_lastJoinServerReqSeq = reqSeq;

            if (!loginShm->autoLoginActive) {
                // Refuse without dispatching — defense against stray writes
                // from leaked mappings on bare launches / post-cleanup state.
                loginShm->joinServerFnResult = 0;
                loginShm->joinServerOutcome  = 3;  // SHM_GATED
                MemoryBarrier();
                loginShm->joinServerAckSeq   = reqSeq;
                DI8Log("login_sm: JOIN_SERVER refused — autoLoginActive=0 (seq=%u)",
                       reqSeq);
            } else {
                int serverID = (int)loginShm->joinServerSerialId;
                DI8Log("login_sm: JOIN_SERVER dispatch — seq=%u serverID=%d",
                       reqSeq, serverID);

                unsigned int fnResult = 0;
                bool dispatched = MQ2Bridge::JoinServerDirect(serverID, &fnResult);

                // Write outcome BEFORE ackSeq — C# polls ackSeq and reads
                // outcome/fnResult once it observes ackSeq == reqSeq. The
                // MemoryBarrier ensures publication order: outcome+fnResult
                // visible before the ack flip.
                loginShm->joinServerFnResult = dispatched ? fnResult : 0;
                loginShm->joinServerOutcome  = dispatched ? 1u : 2u;
                MemoryBarrier();
                loginShm->joinServerAckSeq   = reqSeq;

                DI8Log("login_sm: JOIN_SERVER ack — seq=%u dispatched=%d "
                       "outcome=%u fnResult=0x%08X",
                       reqSeq, dispatched ? 1 : 0,
                       (unsigned)loginShm->joinServerOutcome, fnResult);
            }
        }
    }

    // v3 SHM (2026-05-15): OK_Display SHM mirror — gated on autoLoginActive
    // per in-flight smoke 2026-05-15 ~00:27 (3-sec system-wide UI lag +
    // Windows "please close this program" popup; root cause hypothesis:
    // FindWindowByName heap-walks every 500ms tick contend with EQ's
    // allocator during mid-init / connect-retry, stalling the game thread
    // → tray balloons queue on C# side → Windows flags UI as hung).
    //
    // Gate semantics: autoLoginActive==1 only during the
    // AutoLoginManager.RunLoginSequence active window (set after
    // LoginShmWriter.Open + SetAutoLoginActive, cleared in the finally
    // block). Bare launches and post-login idle stay at 0 → probe never
    // fires → zero overhead.
    //
    // Trade-off: the v3.18 OK_Display mirror is now scoped to active
    // autologin only. PATH B (C# keystroke retry) is exactly the use-case
    // that needs it today — autoLoginActive=1 during that whole window.
    // PATH A (currently disabled) would also have autoLoginActive=1
    // during its TryLoginViaShm run. The "fire even when no login is
    // in progress" semantic was overly broad and produced no benefit at
    // measurable cost.
    if (loginShm->autoLoginActive) {
        PollOkDisplayToShm(loginShm);
    }

    // Nothing to do if idle or terminal state
    if (g_phase == PHASE_IDLE || g_phase == PHASE_COMPLETE || g_phase == PHASE_ERROR)
        return;

    // Phase timeout: 120 seconds max per phase
    if (PhaseAge() > 120000) {
        SetError(loginShm, "Login phase timed out after 120 seconds");
        memset(g_password, 0, sizeof(g_password));
        return;
    }

    // ── Phase-specific logic ──────────────────────────────────

    switch (g_phase) {

    case PHASE_WAIT_LOGIN_SCREEN: {
        // Wait for login widgets to appear — don't gate on gameState value
        // (Dalaya ROF2 uses gameState=0 at login, not -1 like modern MQ2)
        //
        // Iter 14 (2026-04-25): debounce reduced 500ms→100ms. The previous
        // 500ms gap was a second source of visible idle on top of the SHM
        // open retry — once the SHM was open and the screen was up,
        // DiscoverLoginWidgets only ran once per 500ms. With structural
        // password lookup the heap scan is the throttle (~500ms intrinsic),
        // so 100ms debounce just lets us start the first scan promptly.
        if (SinceLastAction() < 100) break;

        DiscoverLoginWidgets();
        if (g_widgetsCached) {
            SetPhase(loginShm, PHASE_TYPING_CREDENTIALS);
            break;
        }

        // v7 Phase 6: login widgets not found — the login SUB-SCREEN may not
        // be open yet. eqmain starts on a main menu with buttons like "LOGIN",
        // "HELP", "EXIT". We need to click the "LOGIN" button to open the
        // username/password form. Try to find it by label text at CXWnd+0x1A8.
        if (!g_loginBtnClicked && g_loginBtnAttempts < 10) {
            g_loginBtnAttempts++;
            void *pLoginBtn = MQ2Bridge::FindWidgetByLabel("LOGIN");
            if (pLoginBtn) {
                MQ2Bridge::ClickButton(pLoginBtn);
                g_loginBtnClicked = true;
                DI8Log("login_sm: clicked 'LOGIN' main menu button at %p (attempt %d)",
                       pLoginBtn, g_loginBtnAttempts);
                // Reset widget cache so FindLiveCXWnd retries after sub-screen opens
                MQ2Bridge::ResetWidgetCache();
            } else if (g_loginBtnAttempts <= 3) {
                DI8Log("login_sm: 'LOGIN' main menu button not found (attempt %d)", g_loginBtnAttempts);
            }
        }

        g_lastActionTick = GetTickCount();
        break;
    }

    case PHASE_TYPING_CREDENTIALS:
        // Set username and password on edit fields, then click connect.
        //
        // No debounce — Combo G writes are direct memory writes via the
        // resolved eqmain CXStr ctor + vtable[73] SetWindowText. Each call
        // is a synchronous 4-CALL chain (ctor + setText + redraw1 + redraw2)
        // that completes in microseconds. The previous 200ms debounce
        // existed to space keystroke-injection events; with WriteEditTextDirect
        // it's pure latency that loses the race against C#'s 3s WaitLoginScreen
        // timeout (AutoLoginManager.cs:976).

        // Iter 13: no widget-pointer preconditions to validate here.
        // Username is ini-prefilled per spec; password is sourced
        // structurally at write time; connect button is resolved lazily
        // in PHASE_CLICKING_CONNECT.

        // Username path: skipped entirely. The autologin spec
        // (CLAUDE.md AUTOLOGIN SPEC) says username is auto-populated from
        // eqlsPlayerData*.ini by LaunchManager before launch. We don't
        // need to type it. Removing the WriteEditTextDirect attempt here
        // saves a vtable-rejection round trip and the pre-existing widget
        // cache lookup that produced a wrapper pointer.

        if (g_password[0]) {
            // Iter 14.2: prefer the cached widget pointer from DiscoverLoginWidgets
            // (saved when the login-screen-ready signal fired) — avoids a
            // redundant ~600ms heap scan. Falls back to a fresh structural
            // lookup if the cache is empty (e.g., race between phase transitions).
            void *pPasswordWidget = g_pPasswordEdit;
            if (!pPasswordWidget) {
                pPasswordWidget = EQMainWidgets::FindLivePasswordCEditWnd();
            }
            if (!pPasswordWidget) {
                DI8Log("login_sm: structural password lookup failed — Combo G "
                       "unavailable; deferring to C# keystroke fallback (DI8 SHM)");
                SetError(loginShm,
                         "Combo G structural password widget not found "
                         "(EQMainWidgets::FindLivePasswordCEditWnd)");
                memset(g_password, 0, sizeof(g_password));
                break;
            }

            // Password is critical — if Combo G fails, we MUST surface error
            // immediately rather than leaving C# to wait its 14s outer timeout
            // and silently fall back to keystroke injection. Per
            // memory/feedback_eqswitch_no_regression_to_dinput8.md, the
            // dinput8 keystroke path is the regression we're escaping; failing
            // loud here is the right behavior.
            if (EQMainCXStr::WriteEditTextDirect(pPasswordWidget, g_password)) {
                DI8Log("login_sm: set password via Combo G (direct field write @ +0x1A8)");
                // Password stays in g_password until PHASE_COMPLETE for retry
                // Fix 1 (v5 SHM, 2026-05-15): publish success signal so C#
                // skips BURST 1 primer + retype (avoids double-write that
                // corrupts the field to ~13 chars and gets login-rejected).
                // Set AFTER WriteEditTextDirect returns true — the function
                // already includes a read-back at +0x1A8 verifying the write
                // landed; if read-back failed, WriteEditTextDirect returns
                // false and the SetError branch below fires instead.
                loginShm->comboGWriteOk = 1;
            } else {
                SetError(loginShm,
                         "Combo G WriteEditTextDirect failed on password edit "
                         "(CXStr unresolved or +0x1A8 touch faulted on wrapper)");
                memset(g_password, 0, sizeof(g_password));
                break;
            }
        }

        SetPhase(loginShm, PHASE_CLICKING_CONNECT);
        break;

    case PHASE_CLICKING_CONNECT:
        // Small delay then click connect
        if (SinceLastAction() < 300) break;

        // Iter 13: lazy-resolve LOGIN_ConnectButton on first click attempt.
        // Deferred from DiscoverLoginWidgets so the heap-cross-ref scan
        // (~3.2s) doesn't block password entry. Password is already in the
        // widget at this point (typed in PHASE_TYPING_CREDENTIALS) so this
        // delay is hidden by the natural pause between typing and clicking.
        //
        // 2026-04-25 Fix (1): gate the lazy-resolve on IsEQMainButtonWidget.
        // FindWindowByName has a legacy fallback that returns CXMLDataPtr
        // definitions when no live widget is found — the def has a vtable
        // pointing to eqmain.dll's DOS header (literally eqmain+0x00000),
        // not a real CButtonWnd vtable. Pre-fix the state machine called
        // ClickButton on this def, ClickButton silently early-returned
        // ("not a known widget class"), and the phase advanced to
        // PHASE_WAIT_CONNECT_RESP regardless. C# trusted the lying
        // advancement and skipped its keystroke fallback. Verified via
        // eqswitch-dinput8-{pid}.log on 2026-04-25 dual-box test.
        if (!g_pConnectButton) {
            // ITER 12 v2 (toggle re-enabled 2026-05-15): try MQ2-style structural
            // traversal first (~few ms), fall back to legacy heap-cross-ref scan
            // (~3.2s) on miss. The outer `!g_pConnectButton` check is the
            // cache-first guard — MQ2-style only runs when cache cold. Toggle
            // was false 2026-04-26 → 2026-05-15 due to game-thread starvation
            // (see eqmain_widgets_mq2style.h:82-105 for re-enable rationale).
            // 2026-05-15 dual-box smoke regression: legacy heap-cross-ref
            // returned a CXMLDataPtr definition for LOGIN_ConnectButton (rejected
            // by IsEQMainButtonWidget), retried 3× then C# fell back to BURST 1
            // — re-enabling structural lookup pulls the LIVE widget instead.
            void *pCandidate = nullptr;
            if (EQMainWidgetsMQ2::kMQ2StyleWidgetLookup) {
                pCandidate = EQMainWidgetsMQ2::FindChildByName("connect", "LOGIN_ConnectButton");
                if (pCandidate && EQMainOffsets::IsEQMainButtonWidget(pCandidate)) {
                    DI8Log("login_sm: LOGIN_ConnectButton resolved via MQ2-style @ %p "
                           "(skipping legacy heap-cross-ref scan)", pCandidate);
                } else {
                    if (pCandidate) {
                        DI8Log("login_sm: MQ2-style returned %p but failed IsEQMainButtonWidget; "
                               "falling back to legacy FindWindowByName", pCandidate);
                    }
                    pCandidate = MQ2Bridge::FindWindowByName("LOGIN_ConnectButton");
                }
            } else {
                pCandidate = MQ2Bridge::FindWindowByName("LOGIN_ConnectButton");
            }
            if (pCandidate && EQMainOffsets::IsEQMainButtonWidget(pCandidate)) {
                g_pConnectButton = pCandidate;
                g_connectButtonRetries = 0;
                DI8Log("login_sm: LOGIN_ConnectButton resolved lazily @ %p (verified CButtonWnd vtable)", pCandidate);
            } else {
                g_connectButtonRetries++;
                if (pCandidate) {
                    DI8Log("login_sm: REJECTED LOGIN_ConnectButton candidate @ %p (retry %d) — not CButtonWnd vtable (likely CXMLDataPtr def)",
                           pCandidate, g_connectButtonRetries);
                } else {
                    DI8Log("login_sm: LOGIN_ConnectButton not found at click phase (retry %d)", g_connectButtonRetries);
                }
                if (g_connectButtonRetries > 50) {
                    // ~25s of 500ms-debounced retries — give up loud so C#
                    // can fall back to keystroke BURST 1 instead of waiting
                    // the full 90s WaitForScreenTransition timeout.
                    SetError(loginShm, "LOGIN_ConnectButton lookup never returned a live CButtonWnd vtable");
                    g_connectButtonRetries = 0;
                }
                break;  // retry next tick
            }
        }

        MQ2Bridge::ClickButton(g_pConnectButton);
        DI8Log("login_sm: clicked LOGIN_ConnectButton");
        SetPhase(loginShm, PHASE_WAIT_CONNECT_RESP);
        break;

    case PHASE_WAIT_CONNECT_RESP:
        // Poll for confirmation dialog or game state change
        if (SinceLastAction() < 500) break;
        g_lastActionTick = GetTickCount();

        // Track the gameState when we clicked connect
        {
            if (g_connectGameState == -99) g_connectGameState = gameState;

            // Success: game state changed from what it was when we clicked connect
            if (gameState != g_connectGameState && g_connectGameState != -99) {
                DI8Log("login_sm: connect response — gameState changed %d -> %d", g_connectGameState, gameState);
                g_connectGameState = -99; // reset for next login
                // v3 SHM: clear LIVE OK_Display fields on successful advance
                // so C# retry path doesn't see stale text from a prior tick.
                ClearOkDisplay(loginShm);
                SetPhase(loginShm, PHASE_SERVER_SELECT);
                break;
            }
        }

        // Check for error/confirmation dialogs
        DiscoverDialogWidgets();

        if (g_pOkDisplay) {
            // Read dialog text to determine success/failure
            char dialogText[512] = {};
            MQ2Bridge::ReadWindowText(g_pOkDisplay, dialogText, sizeof(dialogText));

            if (dialogText[0]) {
                DI8Log("login_sm: dialog text: '%s'", dialogText);

                // R2 (v3.18.0): single classification site via shared helper.
                // Publish to SHM BEFORE acting so C# retry path observes the
                // classification at any poll cadence (including the post-
                // SetError-triggered PHASE_ERROR transition for fatal, and
                // the post-OK-click dismiss for recoverable).
                uint32_t classBucket = ClassifyDialogText(dialogText);
                SetOkDisplay(loginShm, dialogText, classBucket);

                if (classBucket == 1) {
                    // Fatal — stop login
                    SetError(loginShm, dialogText);
                    break;
                }
                if (classBucket == 3) {
                    // Success — server is connecting; wait for gameState change
                    DI8Log("login_sm: server connecting...");
                    break;
                }

                // Recoverable (classBucket == 2) — click OK and retry
                if (g_pOkButton) {
                    g_retryCount++;
                    loginShm->retryCount = g_retryCount;
                    if (g_retryCount > 5) {
                        SetError(loginShm, "Login failed after 5 retries");
                        break;
                    }
                    MQ2Bridge::ClickButton(g_pOkButton);
                    DI8Log("login_sm: clicked OK on error dialog (retry %u)", g_retryCount);
                    // Go back to typing credentials
                    InvalidateWidgets();
                    SetPhase(loginShm, PHASE_WAIT_LOGIN_SCREEN);
                }
            } else {
                // g_pOkDisplay exists but ReadWindowText returned empty —
                // dialog widget is up but not yet populated, OR was just
                // dismissed. Clear LIVE fields so C# doesn't see stale text
                // from a prior tick.
                ClearOkDisplay(loginShm);
            }
        } else {
            // No OK_Display widget cached on this poll. Clear LIVE fields.
            // (Note: this fires every tick on the happy path where no error
            // dialog is up — that's fine, ClearOkDisplay is idempotent and
            // cheap.)
            ClearOkDisplay(loginShm);
        }

        // Check for "already logged in" yes/no dialog
        if (g_pYesButton) {
            // Auto-click "Yes" to kick existing session
            // (matches MQ2AutoLogin ServerSelectKick behavior).
            // Cap at 20 attempts (~10s at 500ms tick). Post-Step-2B the call
            // no longer SEHs — it gets class-rejected silently because the
            // YES button pointer from FindWindowByName is a CXMLDataPtr
            // definition, not a live CButtonWnd. Step 3 (live widget
            // discovery) will make these clicks land; until then we cap
            // retries and surface a clear error rather than spin forever.
            g_yesBtnAttempts++;
            if (g_yesBtnAttempts > 20) {
                SetError(loginShm,
                    "Kick-session dialog: click not reaching live button "
                    "(FindWindowByName returns CXMLDataPtr definition pointer, "
                    "not live CButtonWnd — pending Step 3 widget-list reinit). "
                    "Please click YES manually to continue, or close EQ and retry.");
                break;
            }
            MQ2Bridge::ClickButton(g_pYesButton);
            DI8Log("login_sm: clicked YESNO_YesButton (kick existing session, attempt %u/20)",
                   g_yesBtnAttempts);
        } else {
            // Dialog disappeared — reset counter so a future kick dialog
            // gets a fresh 20 attempts.
            g_yesBtnAttempts = 0;
        }
        break;

    case PHASE_SERVER_SELECT: {
        // Dalaya has one server — just wait for charselect transition.
        // Server is pre-selected via eqlsPlayerData*.ini (WriteServerToIni in C#).
        if (SinceLastAction() < 1000) break;
        g_lastActionTick = GetTickCount();

        // Check if Character_List widget appeared (= we're at char select)
        DiscoverCharSelectWidgets();
        if (g_pCharList) {
            DI8Log("login_sm: Character_List found — at char select (gameState=%d)", gameState);
            SetPhase(loginShm, PHASE_WAIT_SERVER_LOAD);
            break;
        }

        DI8Log("login_sm: waiting for server select transition (gameState=%d)", gameState);
        break;
    }

    case PHASE_WAIT_SERVER_LOAD:
        // Wait for charselect to fully load (3D scene, character list populated)
        if (SinceLastAction() < 1000) break;
        g_lastActionTick = GetTickCount();

        // Try to find character list widget
        DiscoverCharSelectWidgets();
        if (g_pCharList) {
            SetPhase(loginShm, PHASE_CHAR_SELECT);
        }
        break;

    case PHASE_CHAR_SELECT: {
        if (SinceLastAction() < 500) break;
        g_lastActionTick = GetTickCount();

        // Populate character data in LoginShm (same as CharSelectShm)
        MQ2Bridge::PopulateCharacterData(loginShm);

        // Select character by name if specified
        if (g_character[0] && g_pCharList) {
            int charCount = loginShm->charCount;
            int targetIdx = -1;

            for (int i = 0; i < charCount; i++) {
                if (_stricmp((const char *)loginShm->charNames[i], g_character) == 0) {
                    targetIdx = i;
                    break;
                }
            }

            if (targetIdx >= 0) {
                MQ2Bridge::SelectCharacter(g_pCharList, targetIdx);
                loginShm->selectedIndex = targetIdx;
                DI8Log("login_sm: selected character '%s' at index %d", g_character, targetIdx);
            } else {
                DI8Log("login_sm: character '%s' not found in %d chars, using default",
                       g_character, charCount);
            }
        }

        // Brief pause after selection, then enter world
        SetPhase(loginShm, PHASE_ENTERING_WORLD);
        break;
    }

    case PHASE_ENTERING_WORLD: {
        if (SinceLastAction() < 1000) break;
        g_lastActionTick = GetTickCount();

        // Detect in-game: Character_List widget disappears when we leave char select.
        // Also check if gameState changed from what it was at char select.
        if (g_charSelGameState == -99) g_charSelGameState = gameState;

        void *charListCheck = MQ2Bridge::FindWindowByName("Character_List");
        if (!charListCheck && gameState != g_charSelGameState) {
            DI8Log("login_sm: INGAME reached! (gameState=%d, no Character_List)", gameState);
            g_charSelGameState = -99;
            memset(g_password, 0, sizeof(g_password));
            SetPhase(loginShm, PHASE_COMPLETE);
            break;
        }

        // Still at char select — click Enter World button
        {
            if (!g_pEnterWorldBtn) {
                DiscoverCharSelectWidgets();
            }

            if (g_pEnterWorldBtn) {
                MQ2Bridge::ClickButton(g_pEnterWorldBtn);
                DI8Log("login_sm: clicked CLW_EnterWorldButton (attempt %u)", g_retryCount + 1);
                g_retryCount++;
                loginShm->retryCount = g_retryCount;

                if (g_retryCount > 10) {
                    SetError(loginShm, "Enter World failed after 10 attempts");
                }
            } else {
                DI8Log("login_sm: CLW_EnterWorldButton not found, retrying...");
            }
        }
        break;
    }

    default:
        break;
    }
}

void Shutdown() {
    g_phase = PHASE_IDLE;
    g_lastCommandSeq = 0;
    g_retryCount = 0;
    g_lastGameState = -99;
    g_connectGameState = -99;
    g_charSelGameState = -99;
    g_loginBtnClicked = false;
    g_loginBtnAttempts = 0;
    InvalidateWidgets();
    memset(g_password, 0, sizeof(g_password));
    memset(g_username, 0, sizeof(g_username));
    DI8Log("login_sm: shutdown");
}

} // namespace LoginStateMachine
