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

// ─── v6 SHM (2026-05-15) — LoginServerAPI-ready stability counter ────
// Counts CONSECUTIVE Ticks where pinstLoginServerAPI is populated AND its
// vtable matches eqmain+0x1002D0 (RVA_VTABLE_LoginServerAPI_Secondary).
// Published as `loginServerAPIReady` = 1 to SHM only when:
//   (a) counter >= LOGIN_SERVER_API_READY_STABILITY_THRESHOLD (3 ticks),
//   AND
//   (b) g_sawNonReadyAfterLogin == true (we've observed at least one
//       non-ready tick since the last LOGIN_CMD_LOGIN reset).
//
// Condition (b) is critical — without it, the verifier sweep flagged
// (Sonnet+Opus T2 convergent 2026-05-15) that the poll-block runs in
// the SAME Tick as LOGIN_CMD_LOGIN's reset. If pinstLoginServerAPI is
// still populated from a prior session (e.g., C# kept the SHM mapping
// open across logins, or the previous session ended at char-select
// where LoginServerAPI is still constructed), the counter climbs
// 0→1→2→3 in ~48ms and republishes ready=1 BEFORE BURST 1 has even
// typed credentials. C# `WaitForLoginServerAPIReady` returns true
// near-instantly → JoinServerDirect dispatches pre-auth → fnResult=3
// reproduces (the exact bug Fix 2 was supposed to close).
//
// The "saw non-ready" gate forces an actual transition through
// pinstLoginServerAPI=NULL (or vtable mismatch) before publishing — at
// EQ's login screen, pinstLoginServerAPI IS NULL per 2026-05-14 probe
// history ("vtable=eqmain+0x1002D0 at EULA, NULL at login screen,
// populates after Connect click"). So the gate naturally clears during
// the BURST 1 / Connect window and only republishes once the new
// auth handshake completes.
//
// Reset on every LOGIN_CMD_LOGIN so a stale stability signal from
// the prior session can't bleed into the new one.
static uint32_t g_loginServerAPIReadyTicks = 0;
static bool     g_sawNonReadyAfterLogin = false;
#define LOGIN_SERVER_API_READY_STABILITY_THRESHOLD 3

// Distinguishing-failure logging cadence: rate-limit log spam by only
// emitting the diagnostic when the failure mode changes or when the
// stability counter was previously stable. Avoids tick-rate spam.
static int      g_lastReadyFailureMode = 0;  // 0=none, 1=pAPI NULL, 2=vtable mismatch
static uintptr_t g_lastReadyVtableRva  = 0;

// PollLoginServerAPIReady — single-Tick probe of pinstLoginServerAPI.
// Returns 1 when ready (pAPI populated AND vtable matches). Returns
// 0 when not ready, with the FAILURE-MODE distinguished via out-param:
//   *outFailMode = 0 — eqmain not loaded OR SEH faulted (rare)
//   *outFailMode = 1 — pAPI was NULL (normal at login screen pre-Connect)
//   *outFailMode = 2 — pAPI populated but vtable mismatch (Dalaya patch
//                     shifted RVA, mid-construction, or unrelated global)
// *outVtableRva is populated only when failMode==2 (the observed vtable
// minus eqmainBase, useful for Dalaya-patch RVA hunting).
//
// SEH-wrapped for the cross-module reads (eqmain could theoretically
// unload, though we're inside its process so this is defense-in-depth).
//
// Uses GetModuleHandleA rather than LoadLibraryA because we're called
// every Tick (~16ms): the LoadLibraryA refcount-bump that JoinServerDirect
// does is appropriate for a single-shot call across module boundaries,
// but here we just need the current base address. eqmain is loaded by
// eqgame.exe's own statically-linked init; no risk of it not being present
// during the autologin window.
static bool PollLoginServerAPIReady(int *outFailMode, uintptr_t *outVtableRva) {
    *outFailMode = 0;
    *outVtableRva = 0;

    HMODULE hEqmain = GetModuleHandleA("eqmain.dll");
    if (!hEqmain) return false;

    uintptr_t eqmainBase = (uintptr_t)hEqmain;
    void *pAPI = nullptr;
    void *vtable = nullptr;

    __try {
        pAPI = *(void **)(eqmainBase + EQMainOffsets::RVA_PINST_LoginServerAPI);
        if (!pAPI) {
            *outFailMode = 1;  // pAPI is NULL — normal at login screen
            return false;
        }
        vtable = *(void **)pAPI;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }

    uintptr_t expectedVtable = eqmainBase + EQMainOffsets::RVA_VTABLE_LoginServerAPI_Secondary;
    if ((uintptr_t)vtable != expectedVtable) {
        *outFailMode = 2;  // vtable mismatch — Dalaya patch / mid-construction
        *outVtableRva = (uintptr_t)vtable - eqmainBase;
        return false;
    }
    return true;
}

// ─── XWM constants (from MQ2 EQClasses.h) ─────────────────────
#define XWM_LCLICK          1
#define XWM_LMOUSEUP        2
#define XWM_RCLICK          3

// ─── Game state constants ──────────────────────────────────────
// Dalaya ROF2 uses different values from modern MQ2 (which changed
// PRECHARSELECT from 6 to -1). We discover the actual values at runtime.
// Known from DLL log: login screen = 0, charselect = ?, ingame = ?
// Strategy: don't gate on gameState for login screen — gate on widget presence.
// Empirically verified by the 2026-05-16 12:51 smoke: both clients
// reached in-world with gameState transitioning 0 (login) → 1 (charselect)
// → 5 (in-world). Gate at GAMESTATE_CHARSELECT correctly suppresses the
// v7 widget probe at char-select+ where eqmain has unloaded and the 5
// SIDL login screens no longer exist.
#define GAMESTATE_CHARSELECT      1
#define GAMESTATE_INGAME          5

// ─── Internal state ────────────────────────────────────────────

static LoginPhase g_phase = PHASE_IDLE;
static uint32_t   g_lastCommandSeq = 0;
static uint32_t   g_retryCount = 0;
// v3.24.28: enter-world clicks use a SEPARATE counter from the connect-phase
// retry counter. Previously both shared g_retryCount, so connect-phase
// recoverable retries silently ate into the enter-world attempt budget
// (cap became 10 - prior_connect_retries instead of a clean 10). Reset
// alongside g_retryCount on LOGIN_CMD_LOGIN and Shutdown.
static uint32_t   g_enterWorldRetryCount = 0;
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
// v3.24.28: case-insensitive ASCII substring search. Dalaya's dialog-text
// casing isn't guaranteed stable across client builds, so a recased/reworded
// "Invalid Password" must still classify Fatal rather than silently falling
// through to Recoverable and retrying a doomed login. ASCII-only lowering —
// no locale / <ctype> dependency.
static bool ContainsNoCase(const char *hay, const char *needle) {
    if (!hay || !needle) return false;
    if (!needle[0]) return true;
    for (const char *p = hay; *p; ++p) {
        const char *h = p;
        const char *n = needle;
        while (*h && *n) {
            char hc = (*h >= 'A' && *h <= 'Z') ? (char)(*h + 32) : *h;
            char nc = (*n >= 'A' && *n <= 'Z') ? (char)(*n + 32) : *n;
            if (hc != nc) break;
            ++h; ++n;
        }
        if (!*n) return true;
    }
    return false;
}

static uint32_t ClassifyDialogText(const char *text) {
    if (ContainsNoCase(text, "password were not valid") ||
        ContainsNoCase(text, "invalid password") ||
        ContainsNoCase(text, "enter a username and password")) {
        return 1; // Fatal
    }
    if (ContainsNoCase(text, "logging in to the server")) {
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
// v3.22.24-fix4/5: Dalaya structural offsets for OK error dialog text storage.
// Pinned via live PID 33768 probe 2026-05-21. Extracted to constexpr to
// keep all the magic numbers in one place — same drift-trap concern that
// motivated removing MIN_CHILDREN_SCREEN in fix3.
static constexpr uint32_t DALAYA_MAIN_TEXT_WIDGET_SLOT_OFF  = 0x25C;
static constexpr uint32_t DALAYA_OKDISPLAY_TEXT_CXSTR_OFF   = 0x30C;
static constexpr uint32_t DALAYA_CXSTR_BUFFER_OFF           = 0x14;
static constexpr uint32_t DALAYA_CXSTR_LENGTH_OFF           = 0x08;
static constexpr uint32_t DALAYA_DIALOG_TEXT_MAX_LEN        = 4096;

// v3.22.24-fix4/5: Dalaya-specific dialog text reader.
//
// Reads the CXStr* at pTextWidget+DALAYA_OKDISPLAY_TEXT_CXSTR_OFF, extracts
// text from CStrRep+DALAYA_CXSTR_BUFFER_OFF (skipping any leading NUL bytes
// that Dalaya prepends), writes to outBuf. Returns true if non-empty text
// was extracted.
//
// Distinct from MQ2Bridge::ReadWindowText (which reads CXWnd::WindowText
// at the canonical +0x11C offset OR +0x1A8 for CEditWnd InputText) —
// neither applies here: pTextWidget is a Dalaya-repurposed CButtonWnd-class
// widget whose +0x30C is the actual dialog-text store. See
// DiscoverDialogWidgets fix4 comment for the live-probe derivation.
//
// CStrRep at +0x30C uses a non-standard refCount=0 (ownerless) layout:
//   +0x00 refCount   = 0   (transient / not refcounted on Dalaya)
//   +0x04 alloc      = buffer size (e.g. 0x400)
//   +0x08 length     = text length including any prefix bytes
//   +0x10 owner ptr  = eqmain code address (non-null but not a CXStr-owner)
//   +0x14 buffer starts here — first 4 bytes observed as NUL on PID 33768
//         (format is unverified; could be CSTML markup token, alignment
//         pad, or a length/type header. Empirically NUL-stripping works
//         for the observed bad-pass message; alternate prefix bytes would
//         leave the visible text intact at the first non-NUL position.)
//
// We read length bytes from +0x14, then skip leading NUL bytes — handles
// both standard CStrReps (no prefix) and Dalaya's prefixed format.
static bool ReadDalayaDialogText(void *pTextWidget, char *outBuf, size_t outBufLen) {
    if (!pTextWidget || !outBuf || outBufLen < 2) return false;
    outBuf[0] = '\0';

    void *pCXStr = nullptr;
    uint32_t length = 0;
    __try {
        pCXStr = *(void**)((uint8_t*)pTextWidget + DALAYA_OKDISPLAY_TEXT_CXSTR_OFF);
        if (!pCXStr || (uintptr_t)pCXStr < 0x10000 || (uintptr_t)pCXStr >= 0x80000000) return false;
        length = *(uint32_t*)((uint8_t*)pCXStr + DALAYA_CXSTR_LENGTH_OFF);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }

    if (length == 0 || length > DALAYA_DIALOG_TEXT_MAX_LEN) return false;

    size_t toRead = (length < outBufLen - 1) ? length : (outBufLen - 1);
    __try {
        memcpy(outBuf, (uint8_t*)pCXStr + DALAYA_CXSTR_BUFFER_OFF, toRead);
        outBuf[toRead] = '\0';
    } __except (EXCEPTION_EXECUTE_HANDLER) { outBuf[0] = '\0'; return false; }

    // Strip leading NUL bytes (Dalaya's observed 4-byte prefix). If the
    // format ever changes to non-NUL prefix bytes, the strip stops at the
    // first non-NUL char and the visible text remains intact.
    size_t leading = 0;
    while (leading < toRead && outBuf[leading] == '\0') ++leading;
    if (leading > 0 && leading < toRead) {
        memmove(outBuf, outBuf + leading, toRead - leading);
        outBuf[toRead - leading] = '\0';
    } else if (leading == toRead) {
        outBuf[0] = '\0';
    }

    return outBuf[0] != '\0';
}

static void PollOkDisplayToShm(volatile LoginShm *shm) {
    DiscoverDialogWidgets();   // resolves g_pOkDisplay if a dialog is up

    if (!g_pOkDisplay) {
        ClearOkDisplay(shm);
        return;
    }

    // v3.22.24-fix4: use Dalaya-structural reader instead of
    // MQ2Bridge::ReadWindowText. The legacy reader reads +0x11C (WindowText)
    // or +0x1A8 (InputText) — both are NULL on Dalaya's repurposed dialog
    // text widget. The text actually lives in a CXStr at +0x30C.
    char dialogText[512] = {};
    if (!ReadDalayaDialogText(g_pOkDisplay, dialogText, sizeof(dialogText))) {
        // No text available — dialog dismissed or mid-construction.
        ClearOkDisplay(shm);
        return;
    }

    // R2 (v3.18.0): single source of truth via ClassifyDialogText helper.
    // ClassifyDialogText is substring-based, so the Dalaya text fragment
    // ("r - The username and/or password were not valid...") still matches
    // the "password were not valid" Fatal pattern — the missing 'Erro' prefix
    // (consumed by the 4-byte CSTML markup token) doesn't affect classification.
    SetOkDisplay(shm, dialogText, ClassifyDialogText(dialogText));
}

// Widget-presence probe — fires every Tick alongside PollOkDisplayToShm.
// Mirrors MQ2 OnPulse named-screen inspection across MQ2AutoLogin.cpp's
// GAMESTATE_PRECHARSELECT block (1195-1240, for the 4 login screens) and
// GAMESTATE_CHARSELECT block (1156-1191, for ConfirmationDialogBox).
// v3.21.0 introduces this as pure observability for C#-side
// logging + verification; the linear RunLoginSequence does not branch on
// these bools yet. v3.22.0 will replace RunLoginSequence with a state
// machine that reads them.
//
// Cost: cache hit = 5× IsEQMainWidget vtable-range check (cheap byte
// reads). Cache miss = 5× FindLiveScreenByName top-level walk (~846 node
// scans). Misses are rare in steady state — only on first probe, after
// eqmain unload/reload, or when EQ heap-recycles a SIDL screen widget
// outside eqmain's range. v3.21.0-fix-1 (this iteration): the pre-cache
// version did 846 scans every Tick and starved EQ's game thread during
// auth — confirmed 2026-05-16 smoke regression vs v3.20.11 (iter-12
// dormancy comment in eqmain_widgets_mq2style.cpp:102-114 flagged the
// same failure mode). Caching restores per-Tick cost to ~5 byte reads.
//
// Stale-but-still-eqmain risk: if heap recycles a cached widget's
// address for a DIFFERENT eqmain widget, IsEQMainWidget still passes
// (it's an eqmain-class widget) but the visibility report is for the
// wrong screen. Benign in v3.21.0 (observability only). v3.22.0 should
// add per-call GetCXWndXMLName() match when visibility bools become
// decision inputs.
//
// Bypass: only called from Tick() body, inside the autoLoginActive gate
// (currently around line 773), after the magic check at Tick entry. Bare-
// launch PIDs without an open LoginShm mapping never reach this code path.
// As of v3.20.11, Tick gates on magic ONLY (no runtime version-mismatch
// rejection); autoLoginActive is the secondary filter that scopes probes
// to active C#-driven login sessions only.

// Cache slots for the 5 SIDL screens. Two defense layers:
//   1. Per-call IsEQMainWidget vtable-range validation in ResolveCachedScreen
//      (catches widget destroyed or memory recycled outside eqmain range)
//   2. Per-tick eqmainBase-snapshot check in PollWidgetVisibilityToShm
//      (catches eqmain unload+reload — if the DLL reloads at a different
//      ASLR address, layer 1's vtable check can false-positive when the
//      cached ptr coincidentally points to a valid eqmain widget at the
//      new base; layer 2 invalidates the cache when base changes)
// 4 verifiers convergent on the eqmain-reload gap post-v3.21.0-fix-2.
static void     *g_cachedConnect      = nullptr;
static void     *g_cachedServerSelect = nullptr;
static void     *g_cachedOkDialog     = nullptr;
static void     *g_cachedYesNoDialog  = nullptr;
static void     *g_cachedConfirmDlg   = nullptr;
// v3.22.24-fix5: g_cachedMain added to match the v3.21.0-fix-1 cache pattern.
// DiscoverDialogWidgets calls FindLiveScreenByName("main", 3) every Tick to
// resolve the structural Dalaya text widget at main+0x25C; without a cache
// slot here every Tick walks the ~205-widget pWindows array, which is the
// exact starvation pattern that v3.21.0-fix-1 was created to avoid (3 of 8
// verifier agents flagged this in the fix4 audit round).
static void     *g_cachedMain         = nullptr;
static uintptr_t g_lastEqmainBase     = 0;

static void InvalidateWidgetVisibilityCache() {
    g_cachedConnect      = nullptr;
    g_cachedServerSelect = nullptr;
    g_cachedOkDialog     = nullptr;
    g_cachedYesNoDialog  = nullptr;
    g_cachedConfirmDlg   = nullptr;
    g_cachedMain         = nullptr;
}

static void *ResolveCachedScreen(void **slot, const char *name, int minChildren = 3) {
    // Cache hit: cached ptr is non-null AND still passes the eqmain
    // vtable-range check. Returns immediately — no heap walk.
    if (*slot && EQMainOffsets::IsEQMainWidget(*slot)) {
        return *slot;
    }
    // Cache miss: re-resolve via the proven top-level walk. Result may
    // be nullptr (screen not in widget tree right now) — that's fine,
    // we'll retry on the next Tick.
    //
    // v3.22.24: `minChildren` defaults to 3 (preserves legacy screen-finder
    // semantics for connect/serverselect). The three modal-dialog lookups
    // below pass minChildren=0 — okdialog has children=1 (verified via
    // live-process probe of PID 21864, 2026-05-21) and was previously
    // filtered out under the 3-child gate, leaving widgetOkDialogVisible
    // stuck at 0 and forcing C# to wait the full 120s phase timeout
    // before seeing the bad-pass error.
    *slot = EQMainWidgetsMQ2::FindLiveScreenByName(name, minChildren);
    return *slot;
}

static void PollWidgetVisibilityToShm(volatile LoginShm *shm) {
    // gameState gate (v3.21.0-fix-2, v3.21.1 reorder): once gameState >=
    // CHARSELECT, eqmain has unloaded and the 5 SIDL login screens no
    // longer exist in the widget tree. Returning early here — BEFORE the
    // eqmain-reload check below — also skips the GetModuleHandleA syscall
    // during the char-select-and-beyond window. Correctness is preserved:
    // cache invalidation only matters when we're about to probe, and the
    // first tick we resume probing (gameState < CHARSELECT) re-runs the
    // base check and invalidates if eqmain reloaded at a different base.
    //
    // The 2026-05-16 12:35 smoke showed Backup (10-char account) crashing
    // during Enter World → Loading-Zone transition; the residual probe
    // cost during char-select was the most likely contributor (Natedogg,
    // 1-char account, survived the same transition). Char-select-and-
    // beyond state observability lives in CharSelectReader's separate
    // SHM mapping — not this probe.
    if (shm->gameState >= GAMESTATE_CHARSELECT) {
        // Clear visibility so C# observes "no login widgets" instead of
        // stale state from the prior tick. Bump seq so C# still knows the
        // probe path is alive (cache stays warm for the next login flow).
        shm->widgetConnectVisible       = 0;
        shm->widgetServerSelectVisible  = 0;
        shm->widgetOkDialogVisible      = 0;
        shm->widgetYesNoDialogVisible   = 0;
        shm->widgetConfirmDialogVisible = 0;
        shm->widgetConfirmDialogText[0] = 0;  // null-terminate; rest stays zero
        shm->widgetTickSeq = shm->widgetTickSeq + 1;
        return;
    }

    // eqmain-reload detection (v3.21.0-fix-3): if eqmain's base address
    // changed since the last probe, the cached widget ptrs reference a
    // prior eqmain instance — invalidate so the next ResolveCachedScreen
    // re-resolves cleanly via FindLiveScreenByName. On eqmain unload
    // (currentBase=0), the invalidation also clears cleanly so a future
    // reload starts with empty cache.
    HMODULE hEqmain = GetModuleHandleA("eqmain.dll");
    uintptr_t currentBase = (uintptr_t)hEqmain;
    if (currentBase != g_lastEqmainBase) {
        InvalidateWidgetVisibilityCache();
        g_lastEqmainBase = currentBase;
    }

    // Snapshot — read each named screen via the cache, gate on visibility.
    //
    // v3.22.24: dialog lookups pass minChildren=0 because modal dialogs in
    // eqmain have only 1-2 children (text label + OK button). Under the
    // legacy 3-child gate the okdialog widget (slot [176] in PID 21864's
    // CXWndManager.pWindows, children=1, name 'okdialog' at body +0x1DC)
    // was always filtered out — so widgetOkDialogVisible was stuck at 0
    // and the C# state machine couldn't short-circuit on Fatal okClass,
    // leaving bad-password fail detection blocked behind the 120s phase
    // timeout. See the local probe_okdialog_search.py
    // and okdialog-walk-PID21864.txt for the live-process probe data.
    void *pConnect      = ResolveCachedScreen(&g_cachedConnect,      "connect");
    void *pServerSel    = ResolveCachedScreen(&g_cachedServerSelect, "serverselect");
    void *pOkDialog     = ResolveCachedScreen(&g_cachedOkDialog,     "okdialog",              0);
    void *pYesNoDialog  = ResolveCachedScreen(&g_cachedYesNoDialog,  "yesnodialog",           0);
    void *pConfirmDlg   = ResolveCachedScreen(&g_cachedConfirmDlg,   "ConfirmationDialogBox", 0);

    shm->widgetConnectVisible       = (pConnect      && EQMainWidgetsMQ2::IsCXWndVisible(pConnect))      ? 1u : 0u;
    shm->widgetServerSelectVisible  = (pServerSel    && EQMainWidgetsMQ2::IsCXWndVisible(pServerSel))    ? 1u : 0u;
    shm->widgetOkDialogVisible      = (pOkDialog     && EQMainWidgetsMQ2::IsCXWndVisible(pOkDialog))     ? 1u : 0u;
    shm->widgetYesNoDialogVisible   = (pYesNoDialog  && EQMainWidgetsMQ2::IsCXWndVisible(pYesNoDialog))  ? 1u : 0u;
    shm->widgetConfirmDialogVisible = (pConfirmDlg   && EQMainWidgetsMQ2::IsCXWndVisible(pConfirmDlg))   ? 1u : 0u;

    // ConfirmationDialogBox text mirror — only populated when visible.
    if (shm->widgetConfirmDialogVisible) {
        void *pStml = EQMainWidgetsMQ2::FindChildByName("ConfirmationDialogBox", "CD_TextOutput");
        char text[LOGIN_ERROR_LEN] = {};
        if (pStml) {
            MQ2Bridge::ReadWindowText(pStml, text, sizeof(text));
        }
        // Write into volatile char array — manual copy (memcpy on volatile is UB).
        for (size_t i = 0; i < LOGIN_ERROR_LEN; ++i) {
            shm->widgetConfirmDialogText[i] = text[i];
            if (text[i] == 0) {
                // Zero-fill the rest so stale tail from prior tick doesn't leak.
                for (size_t j = i + 1; j < LOGIN_ERROR_LEN; ++j) {
                    shm->widgetConfirmDialogText[j] = 0;
                }
                break;
            }
        }
    } else {
        // Clear stale text on transition to not-visible.
        for (size_t i = 0; i < LOGIN_ERROR_LEN; ++i) {
            shm->widgetConfirmDialogText[i] = 0;
        }
    }

    // Tick-seq bump — last write so C# readers see consistent state when seq advances.
    shm->widgetTickSeq = shm->widgetTickSeq + 1;
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
    // v3.22.24-fix4 (2026-05-21, Dalaya-structural OK_Display anchor):
    //
    // MQ2's pattern `WindowMap["okdialog"]->_GetChildItem("OK_Display")` is
    // literally `RecurseAndFindName(this, "OK_Display")` per the open MQ2
    // sources at _.src/_srcexamples/mq2emu-rof2-x86/MQ2Main/EQClasses.cpp.
    // On vanilla EQ the OK_Display widget is a child of okdialog and the
    // recurse finds it. On Dalaya it does NOT — confirmed via live probe of
    // PID 33768 (2026-05-21 16:50 raistlin smoke):
    //   - okdialog widget @ 0x1176BB80 has only ONE child (OK_Box), whose
    //     children are an OK button + a non-text widget. The actual dialog
    //     message is NOT in this subtree.
    //   - main screen widget @ 0x116F17E0 (pWindows[12]) has TWO structural
    //     pointer slots — at +0x25C and +0x4E0 — both pointing to a child
    //     widget @ 0x116F1F20 (a CButtonWnd-class widget whose +0x1A8 label
    //     is "HELP" but whose +0x30C field is repurposed by Dalaya as the
    //     dialog text display CXStr).
    //   - That CXStr at widget+0x30C holds the 877-char "Error - The
    //     username and/or password were not valid... Please Note: ..." text
    //     visible to the user.
    //
    // So the Dalaya structural anchor (mirrors v3.20.5 ConnectButton pattern
    // and v3.22.x ConnectWnd password-edit at fixed slot +0x44):
    //   pMain = FindLiveScreenByName("main", 3)
    //   pTextWidget = *(void**)(pMain + 0x25C)   // validated via IsEQMainWidget
    //   pCXStr      = *(void**)(pTextWidget + 0x30C)
    //   text starts at pCXStr+0x18 (Dalaya prepends 4 null bytes; standard
    //   CStrRep utf8 buffer at +0x14, but the first 4 bytes are an opaque
    //   CSTML markup prefix that the renderer skips).
    //
    // OK button discovery preserved from fix3: still uses
    // RecurseAndFindName(okdialog, "OK_OKButton", 0x400) which DOES work
    // because the OK button IS a child of okdialog (the dialog frame owns
    // its OK click target, just not the message text widget).
    //
    // See the local {probe_okdialog_search.py,
    // probe_okbox_dump.py, okbox-dump-PID33768.txt} for the live probe data.
    g_pOkDisplay = nullptr;
    g_pOkButton  = nullptr;

    // v3.22.24-fix5 visibility gate (Gap-Sonnet + Gap-Opus convergent
    // finding): resolve g_pOkDisplay ONLY when an okdialog screen is
    // actually visible. The structural slot main+0x25C points at a
    // Dalaya-repurposed HELP button widget that ALSO exists during normal
    // login (no dialog active). Reading its +0x30C CXStr unconditionally
    // could classify residual HELP popup text as Fatal and abort a valid
    // in-progress login. The okdialog cache slot is shared with
    // PollWidgetVisibilityToShm so this is zero-cost on the common path.
    void *pOkDialogScreen = ResolveCachedScreen(&g_cachedOkDialog, "okdialog", 0);
    if (!pOkDialogScreen || !EQMainWidgetsMQ2::IsCXWndVisible(pOkDialogScreen)) {
        return;
    }

    // v3.22.24-fix5 main-screen cache (Code-Sonnet + Code-Opus + Gap-Opus
    // convergent finding): use ResolveCachedScreen instead of an uncached
    // FindLiveScreenByName walk each tick. The pre-fix5 code did the ~205-
    // node pWindows scan EVERY tick — the exact starvation pattern that
    // v3.21.0-fix-1 was created to fix for the other 5 cached screens.
    void *pMain = ResolveCachedScreen(&g_cachedMain, "main", 3);
    if (pMain) {
        void *pTextWidget = nullptr;
        __try {
            pTextWidget = *(void**)((uint8_t*)pMain + DALAYA_MAIN_TEXT_WIDGET_SLOT_OFF);
        } __except (EXCEPTION_EXECUTE_HANDLER) { pTextWidget = nullptr; }
        if (pTextWidget && EQMainOffsets::IsEQMainWidget(pTextWidget)) {
            g_pOkDisplay = pTextWidget;
        } else {
            // Log structural-slot miss so a future Dalaya layout shift is
            // diagnosable from logs alone (silent in fix4 — Code-Opus flag).
            static bool s_loggedTextWidgetMiss = false;
            if (!s_loggedTextWidgetMiss) {
                DI8Log("login_sm: DiscoverDialogWidgets — main+0x%X structural "
                       "slot miss (pTextWidget=%p, IsEQMainWidget=false) — "
                       "Dalaya layout may have shifted",
                       DALAYA_MAIN_TEXT_WIDGET_SLOT_OFF, pTextWidget);
                s_loggedTextWidgetMiss = true;
            }
        }
    }

    g_pOkButton = EQMainWidgetsMQ2::RecurseAndFindName(pOkDialogScreen, "OK_OKButton", 0x400);
    // YESNO_YesButton is the "kick existing session" confirm button.
    // No YESNO dialog ever fires in practice on Dalaya patchme flow
    // (confirmed live 2026-04-23 via Nate). The mechanism is NOT that
    // patchme bypasses kick-session — Dalaya's DINPUT8.dll patcher
    // ("Edge") actively disables the `patchme` arg shortly after process
    // start (see 2026-05-21 audit at
    // the local dalaya-dinput8-audit notes, string
    // "disabling patchme" at VA 0x100f7fc0). Kick-session simply doesn't
    // fire because Edge's MQ2-derived connection management is compatible
    // with concurrent multi-client launches when each client uses unique
    // credentials. Resolving the widget name here anyway pulled a stale
    // CXMLDataPtr *definition* pointer (always present in eqmain's
    // memory) which caused PHASE_WAIT_CONNECT_RESP to loop-click a
    // phantom button for 20 attempts before SetError'ing out. Leaving
    // the phase-4 `if (g_pYesButton)` check wired in case a future
    // non-Edge flow needs it.
    g_pYesButton = nullptr;
}

static void DiscoverCharSelectWidgets() {
    g_pCharList      = MQ2Bridge::FindWindowByName("Character_List");
    // RoF2/Dalaya enter-world button is ScreenID "Play_Button" (CLW_EnterWorldButton
    // is the pre-RoF fallback) — single source in MQ2Bridge::FindEnterWorldButton.
    g_pEnterWorldBtn = MQ2Bridge::FindEnterWorldButton();

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

// Forward declaration — TickImpl holds the ~800-line state-machine body
// (~41 `loginShm->` deref sites). v3.22.70 wraps the entire body in a
// single SEH frame at the public `Tick()` entry point below so any
// MMF-unmap-during-detach AV at any deref site is caught safely.
static void TickImpl(volatile LoginShm *loginShm, volatile CharSelectShm *charSelShm);

// v3.22.70 (deferred from v3.22.69 R3 — 4-verifier convergent T2/T3/T4):
// SEH-protected entry point. The 41 `loginShm->` deref sites inside
// TickImpl were unprotected pre-this-wrap; the same MMF-unmap-during-
// DLL-detach hazard the R3 helpers cover at the gate (eqswitch-di8.cpp
// `IsLoginShmReady` / `ReadAutoLoginActiveSafe`) was wide open inside
// the Tick body. Wrapping every deref individually would have meant
// ~41 separate __try frames; wrapping the body via a thin call-through
// wrapper costs one frame, catches every site, and keeps TickImpl free
// of SEH constraints (RAII / unwinding semantics OK in TickImpl since
// this outer wrapper has no destructor-bearing locals — C2712 not
// triggered). On exception, returns silently — next tick (~16ms) will
// retry; the DLL is presumably mid-detach anyway.
void Tick(volatile LoginShm *loginShm, volatile CharSelectShm *charSelShm) {
    if (!loginShm) return;
    __try {
        if (loginShm->magic != LOGIN_SHM_MAGIC) return;
        TickImpl(loginShm, charSelShm);
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        // MMF unmapped mid-tick. Silent recovery — the next poll will retry
        // via TryOpenLoginShm. Logging here is omitted because DI8Log itself
        // can dereference state that may also be in the unmap window.
    }
}

static void TickImpl(volatile LoginShm *loginShm, volatile CharSelectShm *charSelShm) {
    // Magic check moved to the SEH wrapper above; loginShm is non-null
    // and magic was validated by the caller before reaching this body.

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
            // v3.24.x one-shot connect recovery (C#-driven): if a stuck, unreadable
            // OK error dialog is blocking the login screen, dismiss it before re-
            // driving the sequence — otherwise the re-typed credentials land behind
            // the modal. INERT on a fresh login: DiscoverDialogWidgets only resolves
            // g_pOkButton when an "okdialog" screen is actually visible, which never
            // happens on a clean first login — so this fires ONLY on the recovery
            // re-LOGIN that StepWaitConnectResponse triggers after a stuck dialog.
            // Reuses the proven OK-button resolution + ClickButton (the same call the
            // dormant in-phase recoverable-retry path at PHASE_WAIT_CONNECT_RESP uses),
            // and runs on the game thread here (eqmain loaded at login, GiveTime detour
            // drives Tick) — not the char-select cross-thread hazard surface.
            DiscoverDialogWidgets();
            if (g_pOkButton) {
                MQ2Bridge::ClickButton(g_pOkButton);
                DI8Log("login_sm: LOGIN — dismissed blocking OK dialog before restart");
            }
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
            g_enterWorldRetryCount = 0;
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
            // Fix 2 (v6 SHM, 2026-05-15): reset the LoginServerAPI-ready
            // stability counter + SHM flag. pinstLoginServerAPI may already
            // be populated from a prior session's auth (especially if the
            // mapping was kept open across logins), but the 3-tick stability
            // count must be re-earned in the current session before C# is
            // allowed to fire JoinServerDirect on it.
            //
            // R2 (verifier-driven 2026-05-15, Sonnet+Opus T2 convergent):
            // ALSO reset g_sawNonReadyAfterLogin to false. This forces
            // the poll-block to observe at least one not-ready tick
            // BEFORE it's allowed to publish ready=1 — defeats the
            // "stale pAPI from prior session republishes in the same
            // Tick as the reset" race. At EQ's login screen,
            // pinstLoginServerAPI IS NULL per probe history, so the
            // gate naturally clears within ~1 tick on the happy path.
            g_loginServerAPIReadyTicks = 0;
            g_sawNonReadyAfterLogin = false;
            g_lastReadyFailureMode = 0;
            g_lastReadyVtableRva = 0;
            loginShm->loginServerAPIReady = 0;
            InvalidateWidgets();
            // v3.21.1 (verifier-flagged 2026-05-16, T2-S/T2-O/T3-O convergent):
            // also reset the widget-visibility cache + eqmainBase snapshot.
            // The base-snapshot path only catches eqmain reloads at a DIFFERENT
            // ASLR base; same-base reloads (common when a single eqmain.dll is
            // unloaded and re-loaded into the same preferred base in-process)
            // would slip past layer 2 and rely entirely on layer 1 (the
            // IsEQMainWidget vtable-range check), which is known-imperfect on
            // recycled addresses. Forcing the cache clean here + zeroing the
            // base snapshot is symmetric to the existing g_lastJoinServerReqSeq
            // reset just above.
            //
            // Edge case: if eqmain isn't loaded yet at LOGIN_CMD_LOGIN time
            // (early-init / pre-eqmain-DLL-load), the next PollWidgetVisibilityToShm
            // tick sees currentBase=0 == g_lastEqmainBase=0 and SKIPS the
            // base-change invalidation branch. That's safe because we already
            // cleared the cache one line up — the skip just avoids a redundant
            // re-clear. The first tick where eqmain IS loaded (currentBase != 0)
            // then triggers the branch normally.
            InvalidateWidgetVisibilityCache();
            g_lastEqmainBase = 0;
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

    // ─── Fix 2 (v6 SHM, 2026-05-15) — LoginServerAPI-ready poll ───
    //
    // Poll pinstLoginServerAPI + vtable every Tick. When populated AND
    // vtable matches for >= 3 CONSECUTIVE Ticks, publish ready=1 to SHM.
    // Any bad tick (NULL or vtable mismatch) resets the counter AND flag
    // immediately — defends against transient construction states.
    //
    // Cost: ~3 memory reads + SEH frame per Tick (Tick runs every ~16ms
    // on the EQ game thread cadence). Negligible compared to the existing
    // FindWindowByName heap walks already gated by autoLoginActive.
    //
    // The poll is unconditional (NOT gated on autoLoginActive) because:
    //   (a) GetModuleHandleA is free (no DLL search-order walk)
    //   (b) Cross-process SHM writes are publication-only; the C# side
    //       only reads the field during its active autologin window
    //   (c) Unconditional polling means transitions during the small
    //       window between SetAutoLoginActive(true) and the JoinServerDirect
    //       dispatch are captured without race
    //
    // The C# side (AutoLoginManager.TryJoinServerDirectOrFallback) polls
    // this field with a 5000ms timeout before firing JoinServerDirect. If
    // the flag never reaches 1, dispatch is skipped — caller falls through
    // to BURST 2 (server-select Enter PostMessage), preserving the
    // pre-Fix-2 behavior on the slow-auth path.
    {
        int failMode = 0;
        uintptr_t vtableRva = 0;
        bool readyNow = PollLoginServerAPIReady(&failMode, &vtableRva);

        if (readyNow) {
            if (g_loginServerAPIReadyTicks < UINT32_MAX) {
                g_loginServerAPIReadyTicks++;
            }
            // R2 verifier-fix (2026-05-15): the publish-gate now requires
            // BOTH the 3-tick stability AND g_sawNonReadyAfterLogin==true.
            // The second condition forces an actual transition through
            // pAPI=NULL (or vtable-mismatch) since the last LOGIN_CMD_LOGIN
            // before allowing republish — defeats the "stale pAPI republishes
            // in same Tick as reset" race that Sonnet+Opus T2 convergent
            // flagged. C# WaitForLoginServerAPIReady is read-side; native
            // is the publisher; the gate enforces session-fresh semantics.
            if (g_loginServerAPIReadyTicks >= LOGIN_SERVER_API_READY_STABILITY_THRESHOLD &&
                g_sawNonReadyAfterLogin) {
                // Edge-log the transition 0→1 so the DLL log makes the
                // auth-completion signal observable without spamming on
                // every subsequent tick.
                if (loginShm->loginServerAPIReady == 0) {
                    DI8Log("login_sm: LoginServerAPI ready — pinstLoginServerAPI "
                           "populated + vtable@eqmain+0x1002D0 stable for %u ticks "
                           "(transition through not-ready observed since LOGIN)",
                           (unsigned)g_loginServerAPIReadyTicks);
                }
                loginShm->loginServerAPIReady = 1;
                // Reset failure-mode tracker once successfully published.
                g_lastReadyFailureMode = 0;
                g_lastReadyVtableRva = 0;
            }
        } else {
            // R2 verifier-fix: mark that we've seen at least one
            // not-ready tick in this session — required gate for the
            // next ready=1 publish.
            g_sawNonReadyAfterLogin = true;

            // Edge-log the 1→0 transition with the SPECIFIC failure mode
            // so a Dalaya-patch RVA shift surfaces in the DLL log without
            // forcing a Ghidra dive. Rate-limited: only emit when the
            // failure mode CHANGES or when we were previously publishing
            // ready=1 (the "edge").
            bool wasPublishing = (loginShm->loginServerAPIReady != 0);
            bool modeChanged = (failMode != g_lastReadyFailureMode) ||
                               (failMode == 2 && vtableRva != g_lastReadyVtableRva);

            if (wasPublishing || modeChanged) {
                if (failMode == 1) {
                    DI8Log("login_sm: LoginServerAPI not-ready — pinstLoginServerAPI "
                           "is NULL (normal pre-Connect at login screen, or "
                           "post-error-disconnect)");
                } else if (failMode == 2) {
                    DI8Log("login_sm: LoginServerAPI not-ready — pAPI populated but "
                           "vtable mismatch: observed eqmain+0x%06X, expected "
                           "eqmain+0x%06X (Dalaya RVA shift? mid-construction? "
                           "unrelated global at +0x150164?)",
                           (unsigned)vtableRva,
                           (unsigned)EQMainOffsets::RVA_VTABLE_LoginServerAPI_Secondary);
                } else {
                    DI8Log("login_sm: LoginServerAPI not-ready — eqmain not "
                           "loaded or SEH faulted during pAPI/vtable read");
                }
                g_lastReadyFailureMode = failMode;
                g_lastReadyVtableRva = vtableRva;
            }

            g_loginServerAPIReadyTicks = 0;
            loginShm->loginServerAPIReady = 0;
        }
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
        PollWidgetVisibilityToShm(loginShm);   // v3.21.0

        // v8 (2026-05-16, v3.22.0 Iter-2A) — publish pinstCCharacterSelect availability
        // as a Dalaya-reliable alternative to gGameState. Path A smoke confirmed
        // gGameState never advances past 0 on Dalaya even at char-select; the
        // pinstCCharacterSelect transition is the reliable signal. C# state machine
        // (Iter-2B) consumes this for transitions from PHASE_WAIT_CONNECT_RESP onward.
        //
        // Gated on autoLoginActive (sibling pattern with PollWidget/OkDisplay) per
        // T3-Opus principle 2026-05-16: cross-process SHM writes outside the autologin
        // window were load-bearing for the 2026-05-15 Windows hung-app balloon root
        // cause. Bare launches keep charSelectAvailable=0 (Open()-time zero) until
        // C# sets autoLoginActive=1.
        //
        // Edge-detection log per T3-Sonnet/T3-Opus: production debug visibility on the
        // 0→1 flip (the load-bearing Iter-2B transition trigger).
        static uint32_t lastCsPublished = UINT32_MAX;  // sentinel: forces first-tick log
        uint32_t csNew = MQ2Bridge::IsCharSelectAvailable() ? 1u : 0u;
        if (csNew != lastCsPublished) {
            DI8Log("login_sm: charSelectAvailable %u -> %u (gameState=%d, phase=%u)",
                   lastCsPublished == UINT32_MAX ? 0u : lastCsPublished, csNew,
                   gameState, (uint32_t)g_phase);
            lastCsPublished = csNew;
        }
        loginShm->charSelectAvailable = csNew;
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
            // v3.20.6 (2026-05-15) — STRUCTURAL ConnectButton resolution.
            // Plug-and-play port of MQ2's StateMachine.cpp:275 backed by
            // findings.md Round 5 live verification (PID 22892 2026-05-04):
            // ConnectWnd's button widgets live at fixed slots +0x2C..+0x38
            // (NOT in the CXWnd pFirstChild list — that returns junk per
            // probe_connectwnd_children.py). We enumerate the 4 button
            // slots, pick the one whose body's CStrRep heuristic matches
            // "LOGIN_ConnectButton" or "ConnectButton" (both names live on
            // each widget per Round 5 line 60-65). If the name-match fails
            // we still get a valid CButtonWnd from the connect screen (vs
            // the prior proximity heuristic which routinely picked the
            // wrong button out of ~69 CButtonWnd-shape widgets globally).
            void *pCandidate = nullptr;
            DI8Log("login_sm: PHASE_CLICKING_CONNECT entry — g_pPasswordEdit=%p "
                   "(pre-resolve)", g_pPasswordEdit);

            // Primary: structural ConnectWnd-rooted enumeration
            pCandidate = EQMainWidgetsMQ2::FindConnectButtonStructural();
            if (pCandidate && EQMainOffsets::IsEQMainButtonWidget(pCandidate)) {
                DI8Log("login_sm: LOGIN_ConnectButton resolved via STRUCTURAL "
                       "ConnectWnd-rooted @ %p", pCandidate);
            } else {
                if (pCandidate) {
                    DI8Log("login_sm: STRUCTURAL returned %p but failed "
                           "IsEQMainButtonWidget; falling back to proximity",
                           pCandidate);
                }
                pCandidate = nullptr;
            }

            // Secondary: proximity-to-password (prior v3.20.5 behavior).
            // Re-resolve g_pPasswordEdit if InvalidateWidgets nulled it on a
            // gameState transition between PHASE_TYPING and here.
            if (!pCandidate) {
                if (!g_pPasswordEdit) {
                    g_pPasswordEdit = EQMainWidgets::FindLivePasswordCEditWnd();
                    DI8Log("login_sm: re-resolved password edit at click phase — "
                           "g_pPasswordEdit=%p", g_pPasswordEdit);
                }
                if (g_pPasswordEdit) {
                    pCandidate = EQMainWidgetsMQ2::FindButtonNearWidget(g_pPasswordEdit);
                    if (pCandidate && EQMainOffsets::IsEQMainButtonWidget(pCandidate)) {
                        DI8Log("login_sm: LOGIN_ConnectButton resolved via PROXIMITY-TO-PASSWORD "
                               "@ %p (anchor=%p)", pCandidate, g_pPasswordEdit);
                    } else {
                        if (pCandidate) {
                            DI8Log("login_sm: PROXIMITY-TO-PASSWORD returned %p but failed "
                                   "IsEQMainButtonWidget; falling back to MQ2-style + legacy",
                                   pCandidate);
                        }
                        pCandidate = nullptr;
                    }
                }
            }

            // Tertiary: legacy MQ2-style + FindWindowByName fallbacks
            if (!pCandidate && EQMainWidgetsMQ2::kMQ2StyleWidgetLookup) {
                pCandidate = EQMainWidgetsMQ2::FindChildByName("connect", "LOGIN_ConnectButton");
                if (pCandidate && EQMainOffsets::IsEQMainButtonWidget(pCandidate)) {
                    DI8Log("login_sm: LOGIN_ConnectButton resolved via MQ2-style @ %p", pCandidate);
                } else {
                    if (pCandidate) {
                        DI8Log("login_sm: MQ2-style returned %p but failed IsEQMainButtonWidget; "
                               "falling back to legacy FindWindowByName", pCandidate);
                    }
                    pCandidate = MQ2Bridge::FindWindowByName("LOGIN_ConnectButton");
                }
            } else if (!pCandidate) {
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
            // v3.22.24-fix5 (5 of 8 verifier agents convergent finding):
            // use ReadDalayaDialogText here too. Pre-fix5 this site called
            // MQ2Bridge::ReadWindowText (reads +0x11C / +0x1A8 — both NULL
            // on the Dalaya-repurposed text widget), which silently bailed
            // the SetError + recoverable-retry branches at lines 1335-1358
            // even when a dialog was up. Currently dormant (PATH B uses
            // PHASE_IDLE) but breaks immediately if PATH A is re-enabled.
            char dialogText[512] = {};
            ReadDalayaDialogText(g_pOkDisplay, dialogText, sizeof(dialogText));

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
                DI8Log("login_sm: clicked enter-world button (Play_Button/CLW fallback) (attempt %u)", g_enterWorldRetryCount + 1);
                g_enterWorldRetryCount++;
                loginShm->retryCount = g_enterWorldRetryCount;

                if (g_enterWorldRetryCount > 10) {
                    SetError(loginShm, "Enter World failed after 10 attempts");
                }
            } else {
                DI8Log("login_sm: enter-world button not found (Play_Button/CLW), retrying...");
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
    g_enterWorldRetryCount = 0;
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
