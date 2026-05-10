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
}
