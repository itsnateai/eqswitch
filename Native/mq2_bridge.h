// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

// Native/mq2_bridge.h -- MQ2 bridge for character select + in-process login
#pragma once
#include <windows.h>
#include <stdint.h>

// ─── CharSelect Shared Memory (existing) ──────────────────────
// Shared memory name: "Local\EQSwitchCharSel_{PID}"

#define CHARSEL_SHM_MAGIC 0x45534353  // "ESCS"
#define CHARSEL_MAX_CHARS 10
#define CHARSEL_NAME_LEN  64

#pragma pack(push, 1)
struct CharSelectShm {
    uint32_t magic;            // CHARSEL_SHM_MAGIC
    uint32_t version;          // 3 (v3 adds charSelectReady; v2 added Enter World; v1 baseline)
    int32_t  gameState;        // Current EQ game state (-1=pre, 1=charsel, 5=ingame)
    int32_t  charCount;        // Number of characters found (0 if not at char select)
    int32_t  selectedIndex;    // Currently selected index in list (-1 = none)
    uint32_t mq2Available;     // 1 = MQ2 exports resolved, 0 = not found

    // C# -> DLL: request character selection
    int32_t  requestedIndex;   // Set by C# to index to select (-1 = no request)
    uint32_t requestSeq;       // Incremented by C# on each new request
    uint32_t ackSeq;           // Set by DLL when request is processed

    // C# -> DLL: request Enter World (in-process CLW_EnterWorldButton click)
    uint32_t enterWorldReq;    // Incremented by C# to request Enter World
    uint32_t enterWorldAck;    // Set by DLL when processed
    int32_t  enterWorldResult; // 0=pending, 1=clicked, -1=button not found

    // Character data (DLL writes, C# reads)
    char     names[CHARSEL_MAX_CHARS][CHARSEL_NAME_LEN];  // 10 * 64 = 640
    int32_t  levels[CHARSEL_MAX_CHARS];                    // 10 * 4 = 40
    int32_t  classes[CHARSEL_MAX_CHARS];                   // 10 * 4 = 40

    // v3: monotonic ready latch. Set to 1 the first time the bridge populates
    // shm->names with at least one real (non-placeholder, non-empty) character
    // name in the current charselect cycle. Stays 1 across pinst flutter and
    // transient cache invalidations so C# polling can advance even when a
    // pinstCCharacterSelect non-null→null→non-null transition zeros charCount
    // for ~10s during the standalone-scan warm-up window. Cleared on
    // gameState==5 (in-game) and Shutdown(); deliberately NOT cleared on
    // pinst-transition cache resets (that is the bug we're fixing).
    uint32_t charSelectReady;  // 0 = never populated this cycle; 1 = populated
};
// Total: 24 + 12 + 12 + 640 + 40 + 40 + 4 = 772 bytes
#pragma pack(pop)

// Forward declare LoginShm (defined in login_shm.h)
struct LoginShm;

namespace MQ2Bridge {
    // ─── Lifecycle ─────────────────────────────────────────────
    // Call once from DLL init thread (after dinput8.dll is loaded).
    bool Init();
    void Poll(volatile CharSelectShm* shm);
    void Shutdown();

    // ─── Fast-path request handler (v3.15.11 two-tier throttle) ───────
    // Cheap subset of Poll: runs ONLY the Enter World + selection request
    // handlers; skips char-data reads, latch counter, gameState transition
    // logging, and all heap scans. Caller (MQ2BridgePollTick) invokes this
    // unthrottled (within ~16ms cadence of ActivateThread/TIMERPROC) when a
    // C# request is pending in SHM but the 500ms throttle for full Poll has
    // not yet expired. Pre-condition: shm->charCount must already be
    // published by a prior full Poll tick — guaranteed by C# AutoLoginManager
    // protocol (only fires RequestSelectionBySlot after observing
    // charSelectReady == 1).
    void PollRequestsOnly(volatile CharSelectShm* shm);

    // ─── Game state ────────────────────────────────────────────
    // Read gGameState safely (SEH-wrapped). Returns -99 if unavailable.
    int ReadGameState();

    // ─── Window finding ────────────────────────────────────────
    // Find a top-level EQ window or child widget by name.
    // Iterates ppWndMgr's window array, calls GetChildItem on each.
    // Returns the widget pointer, or nullptr if not found.
    void *FindWindowByName(const char *name);

    // ─── UI manipulation (in-process login) ────────────────────
    // Set text on a CEditWnd (username/password fields).
    // Calls CXWnd::SetWindowText internally.
    void SetEditText(void *pEditWnd, const char *text);

    // Click a button via SendWndNotification(XWM_LCLICK).
    void ClickButton(void *pButton);

    // Read window text (for dialog messages like "password were not valid").
    void ReadWindowText(void *pWnd, char *outBuf, int bufSize);

    // ─── Character select helpers ──────────────────────────────
    // Select a character by index in the Character_List CListWnd.
    void SelectCharacter(void *pCharList, int index);

    // Populate character data into LoginShm fields.
    void PopulateCharacterData(volatile LoginShm *shm);

    // ─── Widget cache ──────────────────────────────────────────
    // Reset cached widget pointers (call when eqmain.dll unloads
    // at charselect transition — widgets die with eqmain).
    void ResetWidgetCache();

    // ─── Label-based widget search ──────────────────────────────
    // Find a live CXWnd by its visible label text (at CXWnd+0x1A8).
    // Used to click the "LOGIN" main menu button to open the login
    // sub-screen before searching for username/password widgets.
    void *FindWidgetByLabel(const char *label);

    // ─── Diagnostics ───────────────────────────────────────────
    // Enumerate all CXWnd windows and log their names.
    // Used during Phase 0 to discover Dalaya's widget names.
    void EnumerateAllWindows();

    // ─── Public window iteration (iter 15) ─────────────────────
    // Exposes the internal IterateAllWindows so structural-lookup callers
    // (EQMainWidgets) can walk the CXWndManager child array without doing
    // their own ~700ms heap scan. Tries eqmain's CXWndManager first, then
    // pinstCXWndManager, then ppWndMgr — first non-null hit wins.
    //
    // Callback receives each top-level + descendant CXWnd. Return true to
    // stop iteration, false to continue.
    typedef bool (*PublicWndIterCallback)(void *pWnd, void *context);
    bool IterateAllWindowsPublic(PublicWndIterCallback callback, void *context);

    // ─── LoginServerAPI::JoinServer (Diff 4 of MQ2 walkthrough) ─────
    // In-process __thiscall to eqmain's LoginServerAPI::JoinServer at
    // RVA 0x13C30, on the LoginServerAPI instance at *(eqmain+0x150164).
    // Bypasses the UI server-select click loop entirely — when this lands,
    // BURST keystroke fallback for the server-select Enter becomes dead
    // weight (per emu-branch StateMachine.cpp:773 — MQ2 itself never
    // clicks the server row, only this API call).
    //
    // Pre-conditions (validated internally; failures return false). The
    // four checks form a layered defense — each catches a different threat
    // model; collectively they bracket the call against runtime corruption
    // and mid-session attack:
    //   - eqmain.dll is currently loaded; this fn pins it via LoadLibraryA
    //     refcount across its own lifetime (TOCTOU defense). Cross-checks
    //     GetModuleHandleA == LoadLibraryA HMODULE — catches MID-SESSION
    //     DLL drops via search-order hijack (planter sets up post-init).
    //     ⚠️ Does NOT catch pre-init substitution (planter already loaded
    //     when EQSwitch started); for that case the vtable + prologue
    //     checks below are the load-bearing defense.
    //   - *(eqmain+0x150164) is non-null (LoginServerAPI populated; this is
    //     true any time after eqmain's globals init, well before login UI
    //     becomes interactive — verified live 2026-05-15)
    //   - Pointee's vtable[0] matches eqmain+0x1002D0 (the documented
    //     LoginServerAPI secondary vtable; mismatch ⇒ refuse + log RVA).
    //     ALSO catches pre-init substitution where the planter's eqmain.dll
    //     has its own LoginServerAPI class with a different vtable layout.
    //   - Function pointer at eqmain+0x13C30 begins with a known x86
    //     prologue byte (0x55/53/56/57/83/8B/6A) — defends against
    //     Dalaya-patch RVA shift, anti-cheat hooks, AND a planter that
    //     somehow matched the vtable but stub'd the function entry.
    //
    // The call is __thiscall(this=pAPI, int serverID, void* userdata,
    // int timeoutSeconds). MQ2 always passes (id, nullptr, 30) at the
    // server-select moment per StateMachine.cpp:773 — we mirror that.
    //
    // Out-param contract (R3 — convergent T2-Opus + T2-Sonnet finding):
    //   - On `true` return: *outResult is written with JoinServer's actual
    //     return value (caller interprets EQ network-stack semantics —
    //     0 = success, non-zero = EQ-side error code).
    //   - On `false` return: *outResult is NEVER touched. Caller's pre-call
    //     value is preserved. THIS IS LOAD-BEARING — no sentinel value is
    //     written, because any sentinel collides with valid `unsigned int`
    //     interpretations of negative EQ result codes (0xFFFFFFFE = -2,
    //     0xFFFFFFFF = -1, etc.).
    //   - outResult MAY be nullptr if the caller doesn't care about the
    //     return code; bool dispatch-vs-failure is the primary signal.
    //   - If outResult is non-null, it MUST point to writable memory —
    //     no SEH wrap on the dereference (caller responsibility).
    //
    // ⚠️ C# wiring note for v3.19+ author (T2-Sonnet B):
    //   Roslyn's `out uint` definite-assignment rule REQUIRES the callee to
    //   write the parameter on every code path. Our "untouched on false"
    //   contract is C++ idiom but conflicts with C#'s `out`. The correct
    //   P/Invoke binding is `ref uint outResult`, NOT `out uint outResult`.
    //   The C# caller MUST initialize the local before the call:
    //     uint result = 0;  // pre-init required — C++ may not write
    //     if (NativeBridge.JoinServerDirect(serverId, ref result)) {
    //         // result holds JoinServer's actual return code
    //     } else {
    //         // result is whatever the C# caller pre-initialized it to
    //     }
    //   An `out uint` binding would compile but (a) silently zero the value
    //   on `false` (CLR initializes outs) AND (b) violate the contract by
    //   passing an uninitialized stack slot to native — the unguarded
    //   *outResult write inside the `if (dispatched)` block on the success
    //   path would still work, but the false-return path's "untouched"
    //   semantics get clobbered by the CLR's auto-zero of outs.
    //
    // Thread-safety (R3 — T2-Opus #2):
    //   - Caller MUST serialize concurrent invocations. EQ's LoginServerAPI
    //     owns network sockets and a state machine; concurrent JoinServer
    //     calls would corrupt that state in ways SEH cannot catch (logical
    //     corruption, not AVs). This fn does NOT acquire a mutex; the
    //     caller's calling protocol must guarantee serialization (e.g.,
    //     by calling only from the GiveTime detour body which is
    //     single-threaded on EQ's game thread).
    //
    // Caller should NOT assume true means "server-select advanced" — it
    // only means the API didn't crash. Verify by polling subsequent EQ
    // state (LVM transition to char-select, gameState change).
    bool JoinServerDirect(int serverID, unsigned int *outResult);
}
