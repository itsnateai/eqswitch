// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

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

void DI8Log(const char *fmt, ...);

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
    DI8Log("login_sm: ERROR: %s", msg);
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
}

// ─── Widget discovery ──────────────────────────────────────────
// Uses MQ2Bridge's FindWindowByName to locate login screen widgets.
// Widget names from MQ2AutoLogin (ROF2 emu -- matches Dalaya).

static int g_discoverAttempts = 0;

static void DiscoverLoginWidgets() {
    if (g_widgetsCached) return;
    g_discoverAttempts++;

    // Iter 12.4 (2026-04-25): skipped heap-cross-ref scans for
    // LOGIN_UsernameEdit and LOGIN_PasswordEdit. Both returned CXMLDataPtr
    // wrappers (~3.8s combined per launch); WriteEditTextDirect now
    // rejects wrappers via vtable check and returns false. Username is
    // ini-prefilled per the autologin spec (no widget needed); password
    // is sourced from EQMainWidgets::FindLivePasswordCEditWnd at credential-
    // write time. Removing these eliminates the visible idle window where
    // the login screen sits up before the password fills.
    g_pUsernameEdit  = nullptr;
    g_pPasswordEdit  = nullptr;
    g_pConnectButton = MQ2Bridge::FindWindowByName("LOGIN_ConnectButton");

    if (g_pConnectButton) {
        g_widgetsCached = true;
        DI8Log("login_sm: connect button found (connect=%p) after %d attempts; "
               "user/pass widgets sourced structurally at write time",
               g_pConnectButton, g_discoverAttempts);
    } else if (g_discoverAttempts <= 5 || g_discoverAttempts % 20 == 0) {
        DI8Log("login_sm: connect button NOT found attempt %d", g_discoverAttempts);

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
            InvalidateWidgets();
            SetPhase(loginShm, PHASE_WAIT_LOGIN_SCREEN);
            DI8Log("login_sm: LOGIN command — user='%s' server='%s' char='%s'",
                   g_username, g_server, g_character);
        }
        else if (cmd == LOGIN_CMD_CANCEL) {
            memset(g_password, 0, sizeof(g_password));
            SetPhase(loginShm, PHASE_IDLE);
            DI8Log("login_sm: CANCEL command");
        }

        loginShm->command = LOGIN_CMD_NONE;
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
        if (SinceLastAction() < 500) break; // debounce

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

        // Iter 12.4: connect button is the only legacy widget pointer we
        // still need (for the post-credentials click). Username is ini-
        // prefilled per spec; password is sourced from the structural
        // lookup at write time. No need to validate username/password
        // widget pointers here — the writes themselves handle the unfound
        // case (username write becomes no-op via WriteEditTextDirect's
        // null-pEditWnd guard; password write fails through to keystroke
        // fallback). Connect button is required for PHASE_CLICKING_CONNECT.
        if (!g_pConnectButton) {
            SetError(loginShm, "LOGIN_ConnectButton not found before credentials could be set");
            memset(g_password, 0, sizeof(g_password));
            break;
        }

        // Username path: skipped entirely. The autologin spec
        // (CLAUDE.md AUTOLOGIN SPEC) says username is auto-populated from
        // eqlsPlayerData*.ini by LaunchManager before launch. We don't
        // need to type it. Removing the WriteEditTextDirect attempt here
        // saves a vtable-rejection round trip and the pre-existing widget
        // cache lookup that produced a wrapper pointer.

        if (g_password[0]) {
            // Structural lookup via EQMainWidgets — walks live CEditBaseWnd
            // instances on the heap and returns the one whose CXWnd::XMLIndex
            // (+0xD8) matches the cached LOGIN_PasswordEdit value (hardcoded
            // at iter 12.3 to (34<<16)|1 based on iter 12.2 diagnostic dump
            // showing username at idx=0 with .sidl-stable sequential layout).
            // Sidesteps the iter 1-9 dead-end where FindLiveCXWnd returned
            // CXMLDataPtr wrappers (vtable RVA 0x10A7D4) instead of real
            // live CEditBaseWnd (vtable RVA 0x10BCDC).
            void *pPasswordWidget = EQMainWidgets::FindLivePasswordCEditWnd();
            if (!pPasswordWidget) {
                DI8Log("login_sm: structural password lookup failed — Combo G "
                       "unavailable; deferring to C# keystroke fallback (DI8 SHM)");
                SetError(loginShm,
                         "Combo G structural password widget not found "
                         "(EQMainWidgets::FindLivePasswordCEditWnd)");
                memset(g_password, 0, sizeof(g_password));
                break;
            }
            DI8Log("login_sm: structural password widget @ %p (XMLIndex=0x%08X)",
                   pPasswordWidget, EQMainWidgets::GetCachedPasswordXMLIndex());

            // Password is critical — if Combo G fails, we MUST surface error
            // immediately rather than leaving C# to wait its 14s outer timeout
            // and silently fall back to keystroke injection. Per
            // memory/feedback_eqswitch_no_regression_to_dinput8.md, the
            // dinput8 keystroke path is the regression we're escaping; failing
            // loud here is the right behavior.
            if (EQMainCXStr::WriteEditTextDirect(pPasswordWidget, g_password)) {
                DI8Log("login_sm: set password via Combo G (direct field write @ +0x1A8)");
                // Password stays in g_password until PHASE_COMPLETE for retry
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

        if (g_pConnectButton) {
            MQ2Bridge::ClickButton(g_pConnectButton);
            DI8Log("login_sm: clicked LOGIN_ConnectButton");
        }
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

                // Fatal errors — stop login
                if (strstr(dialogText, "password were not valid") ||
                    strstr(dialogText, "Invalid Password") ||
                    strstr(dialogText, "enter a username and password")) {
                    SetError(loginShm, dialogText);
                    break;
                }

                // Success message — server is connecting
                if (strstr(dialogText, "Logging in to the server")) {
                    DI8Log("login_sm: server connecting...");
                    // Wait for game state to change
                    break;
                }

                // Recoverable error — click OK and retry
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
            }
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
