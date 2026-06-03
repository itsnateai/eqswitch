// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

// device_proxy.cpp — IDirectInputDevice8 COM proxy implementation
// Keyboard interception: SetCooperativeLevel (BACKGROUND|NONEXCLUSIVE),
// GetDeviceState (inject keys), GetDeviceData (synthetic events),
// SetEventNotification (signal on key changes), Acquire (fake success).
// WM_ACTIVATEAPP thread tricks EQ into processing input while backgrounded.

#define _CRT_SECURE_NO_WARNINGS
#include "device_proxy.h"
#include "key_shm.h"
#include "pattern_scan.h"
#include "login_givetime_detour.h"
#include <string.h>

void DI8Log(const char *fmt, ...);
extern void MQ2BridgePollTick();

// --- Global state (shared across all device instances) ---

static volatile HWND g_eqHwnd = nullptr;
static volatile HANDLE g_kbEventHandle = nullptr;
static bool g_activateThreadStarted = false;
static bool g_shmThreadStarted = false;
static DWORD g_originalCoopFlags = 0;
static bool g_coopSwitched = false;
static IDirectInputDevice8W *g_realKeyboardDevice = nullptr;
static volatile bool g_shutdown = false;
static HANDLE g_hActivateThread = nullptr;
static HANDLE g_hShmThread = nullptr;
// v3.24.4 — set true if the cooperative-level restore-to-FOREGROUND TOMBSTONES (gives up after
// the 60-attempt retry cap, leaving the device stuck in BACKGROUND with g_coopSwitched stuck
// true). In that failure state the OLD behavior was leaky-but-PLAYABLE (BACKGROUND reads the
// global keyboard, so the user's keys still reach EQ). Without this flag the anti-bleed
// suppression below would turn that into a DEAD keyboard in-world (every physical key dropped
// forever) — strictly worse. So once restore gives up we STOP suppressing: revert to the old
// leaky-but-playable state (recoverable by the next autologin or an EQSwitch restart, exactly as
// the existing tombstone contract promises). Cleared on the next login's rising edge.
static bool g_restoreGaveUp = false;

// v3.24.4 — physical-key suppression is the EQ-side half of a TWO-condition invariant:
// while the keyboard is in BACKGROUND mode it reads the GLOBAL keyboard regardless of focus,
// so a physical key pressed in ANY app would land in EQ — and with a team's clients all
// BACKGROUND during simultaneous autologin, in EVERY one of them at once (the "both clients
// typed the same key" bleed). The injection bursts already pass suppress=true, but the SHM
// suppress flag (active && suppress) and the actual BACKGROUND state (g_coopSwitched) DECOUPLE
// in the gaps: the ~16ms while `active` has gone 0 but the cooperative-level restore-to-
// FOREGROUND hasn't fired yet, and any active=0-but-still-BACKGROUND phase between login steps.
// In those windows ShouldSuppress() is false but the device is still BACKGROUND → physical keys
// leak. Key the suppression on the DANGER condition itself: suppress physical input whenever the
// keyboard is BACKGROUND (g_coopSwitched) OR the SHM says so. BACKGROUND exists ONLY for
// injection, which writes its keys AFTER the physical-zeroing — so this never drops an injected
// key; and normal play is FOREGROUND (g_coopSwitched=false) so physical passes untouched.
static bool SuppressPhysicalInput() {
    // !g_restoreGaveUp — once the FOREGROUND restore has tombstoned, stop suppressing so the
    // keyboard stays usable (leaky, the pre-v3.24.4 behavior) instead of going dead in-world.
    return !g_restoreGaveUp && (g_coopSwitched || KeyShm::ShouldSuppress());
}

HWND GetEqHwnd() { return g_eqHwnd; }

// ─── v3.24.7: game-thread marshaling for char-select UI calls ──────────────
// EQ's game (window-owning) thread id. Written by BOTH the LoginController::
// GiveTime detour (login phase) and ActivateWndProc (always the window thread),
// read by mq2_bridge.cpp to detect when a bridge poll is running on the
// ActivateThread fallback instead of the game thread.
//
// THE BUG THIS FIXES: at charselect, eqmain.dll unloads, so the GiveTime
// game-thread poll is gone and MQ2BridgePollTick() falls back to running on the
// ActivateThread (see the !GiveTimeDetour::IsInstalled() branch below). Calling
// CListWnd::SetCurSel / WndNotification from there is a CROSS-THREAD UI call —
// it SendMessage()s the owning (game) thread and BLOCKS/HANGS when that thread
// is busy and not pumping (background client mid-3D-scene-load). That is the
// intermittent, background-only char-select freeze. The fix: when a UI mutation
// is requested off the game thread, PostMessage a coalesced "run the poll" msg
// to the subclassed window so the poll re-runs ON the game thread, where the UI
// call is a safe direct call.
volatile DWORD g_gameThreadId = 0;
static UINT g_wmGameThreadPoll = 0;          // RegisterWindowMessage handle (0 until subclass install)
static volatile LONG g_pollPostPending = 0;  // coalesce: at most one queued poll msg at a time

// Called by the bridge (mq2_bridge.cpp) from the ActivateThread when it needs a
// UI mutation that must happen on the game thread. Async + coalesced: never
// blocks the caller, never floods the queue. If the game thread is wedged the
// msg simply waits (no hang); the bridge re-requests every poll until acked.
void PostGameThreadPoll() {
    // Lazy-register (idempotent; RegisterWindowMessage returns the same id for the
    // same string process-wide) so this never no-ops on wm=0 even if the subclass
    // installer hasn't run yet. The WndProc reads the same g_wmGameThreadPoll.
    if (!g_wmGameThreadPoll)
        g_wmGameThreadPoll = RegisterWindowMessageW(L"EQSwitchGameThreadPollV1");
    HWND hwnd = g_eqHwnd;
    if (!hwnd || !g_wmGameThreadPoll) {
        DI8Log("PostGameThreadPoll: NO-OP hwnd=%p wm=%u (not ready)", hwnd, g_wmGameThreadPoll);
        return;
    }
    if (InterlockedCompareExchange(&g_pollPostPending, 1, 0) != 0) return;  // already queued
    BOOL ok = PostMessageW(hwnd, g_wmGameThreadPoll, 0, 0);
    DI8Log("PostGameThreadPoll: posted=%d hwnd=%p wm=%u gameTid=%lu callerTid=%lu",
           (int)ok, hwnd, g_wmGameThreadPoll, g_gameThreadId, GetCurrentThreadId());
    if (!ok)
        InterlockedExchange(&g_pollPostPending, 0);  // post failed — allow retry next bridge tick
}

// --- Background activation (Phase 2c: multi-layer defense) ---
//
// EQ's main loop checks an internal activation flag. When the window loses
// focus, WndProc sets the flag to 0 and EQ stops calling GetDeviceData.
//
// Previous approaches that FAILED:
//   - PostMessage(WM_ACTIVATEAPP) alone: EQ resets the flag immediately
//   - WH_CALLWNDPROC/RET: can't modify sent messages
//   - DefWindowProcA IAT hook: only catches default path, not EQ's handler
//   - Single subclass attempt: EQ overwrites it during its own init
//   - Binary scan for global flag: flag is an object member, not a global
//
// Solution: THREE simultaneous layers:
// 1. WndProc subclass (persistent): intercepts WM_ACTIVATEAPP(FALSE),
//    WM_ACTIVATE(WA_INACTIVE), WM_KILLFOCUS — forces "active" state.
//    Re-installed every tick if EQ overwrites it.
// 2. Pattern scan: finds and patches the activation flag directly as backup.
// 3. PostMessage: belt-and-suspenders re-posting.

// --- Layer 1: WndProc subclass ---

static WNDPROC g_origWndProc = nullptr;
static bool g_subclassInstalled = false;
static HWND g_subclassHwnd = nullptr;     // window our subclass is currently installed on

// v3.24.11 — cooperative raw subclassing. The hook DLL (eqswitch-hook.dll) ALSO raw-subclasses
// this same EQ HWND (its GeoWndProc). Two raw subclassers that each re-grab on seeing an
// unrecognized proc on top formed a circular WndProc chain (di8.orig=Geo AND Geo.orig=di8) on
// the Fullscreen<->Windowed toggle → infinite recursion → stack overflow → USER32 0xc000041d.
// FIX without comctl32 (which hard-fails cross-thread — and the di8 MUST install from its
// background ActivateThread so the subclass is present even when the game thread is wedged
// mid-scene-load, the v3.24.8 char-select-marshaling contract): each DLL PUBLISHES its own
// subclass proc to a window property and, before re-grabbing, checks whether the OTHER DLL's
// published proc is the current top proc. If so — and we're already installed on this window —
// we are chained BELOW the friendly proc, so we do NOT re-grab. That breaks the mutual re-grab
// war (→ no cycle) while still re-grabbing when EQ genuinely overwrites the proc in place.
// Property names are a process-wide contract with eqswitch-hook.cpp — keep both in sync.
static const wchar_t* PROP_DI8_SUBCLASS = L"EQSwitchDi8SubclassProc";
static const wchar_t* PROP_HOOK_SUBCLASS = L"EQSwitchHookSubclassProc";

// v7 Phase 3: WM_TIMER-based MQ2 poll removed. MQ2BridgePollTick() now runs
// from the LoginController::GiveTime detour (see login_givetime_detour.cpp)
// during login/server-select/charselect, and from ActivateThread's background
// tick during the pre-eqmain-load window + any post-in-game phase. The detour
// runs on EQ's game thread inside EQ's own frame loop, so it cannot contribute
// to message-pump pressure the way v6e's SetTimer did.

static LRESULT CALLBACK ActivateWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    // v3.24.7 — a WndProc ALWAYS runs on the window's owning thread = EQ's game
    // thread. Record it so the bridge can tell a game-thread poll from the
    // ActivateThread fallback. Cheap; idempotent.
    g_gameThreadId = GetCurrentThreadId();

    // v3.24.7 — marshaled poll: the bridge posts this from the ActivateThread when
    // it needs a UI mutation (SetCurSel / enter-world click) that must run on the
    // game thread. Running MQ2BridgePollTick() HERE puts those calls on the owning
    // thread → no cross-thread SendMessage deadlock. Swallow (don't forward to EQ).
    if (g_wmGameThreadPoll && msg == g_wmGameThreadPoll) {
        InterlockedExchange(&g_pollPostPending, 0);  // allow the next post before we run
        DI8Log("ActivateWndProc: marshaled poll RECEIVED on tid=%lu (gameTid=%lu) — running MQ2BridgePollTick",
               GetCurrentThreadId(), g_gameThreadId);
        MQ2BridgePollTick();
        return 0;
    }

    if (KeyShm::IsActive()) {
        // Block all focus-loss messages — EQ must believe it's always foreground
        if (msg == WM_ACTIVATEAPP) {
            if (wParam == FALSE) {
                // Swallow the deactivation entirely — don't let EQ see it
                DI8Log("wndproc_hook: BLOCKED WM_ACTIVATEAPP(0)");
                return 0;
            }
        }
        // WM_ACTIVATE: low word 0 = WA_INACTIVE
        if (msg == WM_ACTIVATE && LOWORD(wParam) == 0) {
            DI8Log("wndproc_hook: BLOCKED WM_ACTIVATE(WA_INACTIVE)");
            return 0;
        }
        if (msg == WM_KILLFOCUS) {
            DI8Log("wndproc_hook: BLOCKED WM_KILLFOCUS");
            return 0;
        }
        // Keep title bar drawn as active (prevents visual deactivation cues
        // that some engines use to trigger internal state changes)
        if (msg == WM_NCACTIVATE) {
            return DefWindowProcW(hwnd, WM_NCACTIVATE, TRUE, lParam);
        }
    }
    return CallWindowProcW(g_origWndProc, hwnd, msg, wParam, lParam);
}

// Install or re-install the WndProc subclass. Safe to call repeatedly.
// Returns true if the subclass was freshly (re-)installed.
static bool EnsureSubclassInstalled(HWND hwnd) {
    // v3.24.7 — register the game-thread marshaling message once. RegisterWindowMessage
    // returns a process-unique id in the 0xC000-0xFFFF range, so it can never collide
    // with EQ's own WM_USER/WM_APP messages.
    if (!g_wmGameThreadPoll)
        g_wmGameThreadPoll = RegisterWindowMessageW(L"EQSwitchGameThreadPollV1");

    WNDPROC current = (WNDPROC)GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
    if (current == ActivateWndProc) return false; // already on top — nothing to do

    // v3.24.11 — cooperative check: if the hook's GeoWndProc is the current top proc AND OUR
    // proc is genuinely in THIS window's chain (our property is set on THIS hwnd), then the hook
    // merely chained ON TOP of us — we are still below it (Geo -> ActivateWndProc -> EQ).
    // Re-grabbing here is exactly what captured the hook's proc as our chain target and closed
    // the circular loop, so DON'T. We self-verify via OUR OWN property (GetPropW == our proc)
    // rather than the g_subclassInstalled/g_subclassHwnd flags — the property is per-window and
    // immune to HWND-value recycling (a fresh window with no di8 property → install, not decline).
    // (If current is neither ours nor the hook's, EQ overwrote the proc in place → fall through
    // and re-grab; the di8 marshaling subclass always recovers that way.)
    WNDPROC hookProc = (WNDPROC)GetPropW(hwnd, PROP_HOOK_SUBCLASS);
    WNDPROC myProc   = (WNDPROC)GetPropW(hwnd, PROP_DI8_SUBCLASS);
    if (hookProc && current == hookProc && myProc == ActivateWndProc)
        return false;  // friendly hook subclass on top; we are verifiably chained below it

    // (Re)grab. Capture whatever is currently on top as our chain target, then PUBLISH our proc
    // BEFORE installing it on top: a cross-thread peer (the hook's UI-thread EnsureGeoSubclass)
    // must never observe our proc as `current` without also seeing our property — otherwise its
    // cooperative check misses us and it re-grabs → the circular chain re-forms. SetProp-then-
    // SetWindowLongPtr closes that TOCTOU window (the property is live before the proc is on top).
    g_origWndProc = current;
    SetPropW(hwnd, PROP_DI8_SUBCLASS, (HANDLE)ActivateWndProc);
    SetWindowLongPtrW(hwnd, GWLP_WNDPROC, (LONG_PTR)ActivateWndProc);
    g_subclassHwnd = hwnd;
    if (!g_subclassInstalled) {
        DI8Log("wndproc_hook: installed subclass (orig=0x%08X hwnd=0x%X)",
               (unsigned)(uintptr_t)current, (unsigned)(uintptr_t)hwnd);
        g_subclassInstalled = true;
    } else {
        DI8Log("wndproc_hook: RE-installed subclass (EQ overwrote, new orig=0x%08X hwnd=0x%X)",
               (unsigned)(uintptr_t)current, (unsigned)(uintptr_t)hwnd);
    }
    return true; // freshly installed
}

// Remove the subclass when we no longer need it
static void RemoveSubclass(HWND hwnd) {
    if (!g_subclassInstalled || !g_origWndProc) return;
    // v7 Phase 3: GiveTime detour runs independently of this subclass — no
    // timer coupling to worry about here.
    WNDPROC current = (WNDPROC)GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
    if (current == ActivateWndProc) {
        // We're on top — safe to fully unwind: restore EQ's proc, stop advertising, clear state.
        SetWindowLongPtrW(hwnd, GWLP_WNDPROC, (LONG_PTR)g_origWndProc);
        RemovePropW(hwnd, PROP_DI8_SUBCLASS);
        if (hwnd == g_subclassHwnd) g_subclassHwnd = nullptr;
        g_subclassInstalled = false;
        DI8Log("wndproc_hook: removed subclass");
    }
    // else: we are chained BELOW a friendly proc (the hook's GeoWndProc) — we can't unwind
    // without corrupting the chain, and tearing down our property/flags here would defeat our
    // own cooperative decline next tick (g_subclassInstalled=false → we'd re-grab on top of the
    // hook → re-form the circular chain). So leave the subclass + property intact: it is always-on
    // by design and the hook's cooperative check keeps the chain linear. (Symmetric with the
    // hook's RemoveGeoSubclass, which also only unwinds when it is the current top proc.)
}

// --- Layer 2: Pattern scan for activation flag ---

static volatile uint32_t *g_pActiveFlag = nullptr;
static bool g_activeFlagScanned = false;

// --- ActivateThread: orchestrates all three layers ---

static DWORD WINAPI ActivateThread(LPVOID) {
    bool wasActive = false;
    int ticksSinceRepost = 0;
    int initDelay = 0; // delay subclass install for EQ to finish init

    // v3.22.77 phantom-key retry state. The falling-edge restore branch fires
    // ONCE per autologin cycle; if SetCoopLevel(FOREGROUND|EXCLUSIVE) fails
    // there, g_coopSwitched stays true and EQ keeps reading global keyboard
    // state via DI8 BACKGROUND|NONEXCLUSIVE mode forever — every key the user
    // presses in any foreground app leaks into EQ. This counter drives the
    // standalone retry block below at ~1Hz, capped at 60 attempts (~60s).
    int retryTickCounter = 0;
    int retryAttempts = 0;

    while (!g_shutdown) {
        Sleep(16); // ~60Hz

        // v7: try to install the LoginController::GiveTime detour. No-op
        // after first successful install; cheap boolean check on subsequent
        // iterations. Returns false while eqmain.dll isn't loaded — expected
        // until the client reaches login screen.
        GiveTimeDetour::PollAndInstall();

        // v7 Phase 4 FIX: detect eqmain.dll unload. eqmain handles login/server
        // screens ONLY — it UNLOADS when EQ transitions to the 3D charselect
        // scene (rendered by eqgame.exe, not eqmain). When this happens:
        //   - GiveTime detour is gone (code was in eqmain's .text)
        //   - g_installed falsely stays true
        //   - ActivateThread skips MQ2BridgePollTick (thinks detour is running)
        //   - Zero polling at charselect → charCount stays 0 → stall
        // Fix: check if eqmain vanished and reset detour state so the fallback
        // MQ2BridgePollTick resumes from this thread.
        if (GiveTimeDetour::IsInstalled() && !GetModuleHandleA("eqmain.dll")) {
            DI8Log("device_proxy: eqmain.dll UNLOADED — detour is gone, resuming background poll");
            GiveTimeDetour::OnEqmainUnloaded();
        }

        // MQ2 bridge: background poll while the GiveTime detour is NOT active.
        // During login (eqmain loaded), the detour fires at 50-130 Hz and this
        // is skipped. At charselect (eqmain unloaded), we resume polling here.
        if (!GiveTimeDetour::IsInstalled()) {
            MQ2BridgePollTick();
        }

        bool active = KeyShm::IsActive();
        HWND hwnd = g_eqHwnd;

        // Count ticks after HWND appears — delay subclass for EQ init
        if (hwnd && initDelay < 400) initDelay++; // ~6.4 seconds

        // v7 Phase 3: SetTimer-based MQ2 poll REMOVED. Was v6e's 1500ms
        // TIMERPROC; now replaced by the LoginController::GiveTime detour
        // installed lazily in login_givetime_detour.cpp. The detour runs on
        // EQ's game thread inside EQ's own frame loop, so we cannot contribute
        // to message-pump latency the way SetTimer-dispatched WM_TIMER did.
        // v6e's 1500ms interval + 300-tick initDelay were emergency band-aids
        // for the IsHungAppWindow / Event 1002 crashes and are obsolete now.

        // Layer 2: one-shot pattern scan after EQ's window is created
        if (!g_activeFlagScanned && hwnd && initDelay >= 100) {
            g_pActiveFlag = (volatile uint32_t *)PatternScan::FindActivationFlag();
            g_activeFlagScanned = true;
            if (g_pActiveFlag)
                DI8Log("wm_activate: pattern scan found flag at 0x%08X (value=%u)",
                       (unsigned)(uintptr_t)g_pActiveFlag, *g_pActiveFlag);
            else
                DI8Log("wm_activate: pattern scan found nothing — relying on WndProc subclass");
        }

        // v3.24.7 — install/verify the WndProc subclass whenever the window exists,
        // NOT only when KeyShm is active. The subclass is the di8's ONLY game-thread
        // execution point at charselect (eqmain unloaded → GiveTime detour gone), and
        // the StateMachine login path uses no DI8 key injection so `active` is often
        // false the whole time. The WndProc only blocks focus messages when
        // KeyShm::IsActive(), so installing it while inactive is harmless. This also
        // runs RegisterWindowMessage (inside EnsureSubclassInstalled), fixing the
        // PostGameThreadPoll wm=0 NO-OP that broke the game-thread marshal.
        bool freshInstall = false;
        if (hwnd && initDelay >= 100) {
            freshInstall = EnsureSubclassInstalled(hwnd);
        }

        if (active && hwnd) {
            if (!wasActive || freshInstall) {
                // Rising edge OR subclass was just (re-)installed after EQ overwrote it.
                // When EQ re-inits DirectInput (e.g. 3D char select), it replaces our
                // WndProc. During the gap, deactivation messages slip through and EQ's
                // internal activation flag goes to 0. We must blast ALL activation
                // messages to reset EQ's state.

                // v3.22.77 (R1 form, R2 reverted by R3-verifier post-mortem):
                // reset retry counters unconditionally on rising-edge / freshInstall,
                // before the !g_coopSwitched gate below. The earlier R1 placement
                // inside the SUCCEEDED(hr) branch had a counter-leak hole — a
                // post-tombstone autologin would skip the entire inner block
                // (g_coopSwitched=true blocks the gate) and never reset the
                // counter, so the next failed-restore would tombstone immediately
                // with zero retries. Unconditional reset means each autologin
                // cycle gets a fresh 60-attempt budget for the standalone retry
                // block on the falling edge.
                //
                // We do NOT force-clear g_coopSwitched here (R2 attempted this,
                // R3 verifier round flagged it as a regression). Per DI8 docs,
                // SetCooperativeLevel failure preserves the prior cooperative
                // level — so after a tombstone, the device is still in BACKGROUND
                // mode (the failed restore-to-FOREGROUND didn't take effect).
                // The inner `!g_coopSwitched` gate below correctly SKIPS the
                // re-switch (device is already in BACKGROUND), autologin keys
                // are delivered in BACKGROUND mode as expected, and the falling
                // edge re-tries restore with a fresh 60-attempt budget from this
                // counter reset. R2's force-clear created a device/flag desync
                // window and conflicted with the alternate g_coopSwitched
                // writer at the SetCooperativeLevel intercept (other thread).
                retryTickCounter = 0;
                retryAttempts = 0;
                // v3.24.4 — a new login cycle re-attempts the restore, so clear the give-up
                // sentinel and re-enable anti-bleed suppression for this login.
                g_restoreGaveUp = false;

                if (!wasActive && !g_coopSwitched && g_realKeyboardDevice) {
                    g_realKeyboardDevice->Unacquire();
                    DWORD bgFlags = (g_originalCoopFlags & ~(DISCL_EXCLUSIVE | DISCL_FOREGROUND))
                                  | DISCL_NONEXCLUSIVE | DISCL_BACKGROUND;
                    HRESULT hr = g_realKeyboardDevice->SetCooperativeLevel(hwnd, bgFlags);
                    HRESULT acqHr = g_realKeyboardDevice->Acquire();
                    // Hotfix v4: gate g_coopSwitched flip on SUCCEEDED(hr), matching the
                    // MED-5/MED-6 discipline in v3. Without this, a SetCoop failure on
                    // rising edge would set g_coopSwitched=true while the device is still
                    // in the original mode — the next restore cycle would then perform a
                    // no-op SetCoop(original), clear the flag, and silently mask the
                    // original failure. Phantom-keys risk if EQ was in BACKGROUND at
                    // restore time. Acquire failure is NOT treated as failure here — it
                    // typically returns E_ACCESSDENIED when EQ lacks focus, which is OK.
                    if (SUCCEEDED(hr)) {
                        g_coopSwitched = true;
                        DI8Log("wm_activate: Unacquire → SetCoopLevel(BACKGROUND|NONEXCLUSIVE)=0x%08X → Acquire=0x%08X",
                               (unsigned)hr, (unsigned)acqHr);
                    } else {
                        DI8Log("wm_activate: RISING-EDGE SetCoopLevel FAILED (hr=0x%08X, acqHr=0x%08X) — g_coopSwitched stays false, will retry next tick; phantom-keys risk until retry succeeds",
                               (unsigned)hr, (unsigned)acqHr);
                    }
                }

                // Blast all three activation messages to reset EQ's state
                PostMessageW(hwnd, WM_ACTIVATEAPP, TRUE, 0);
                PostMessageW(hwnd, WM_ACTIVATE, 1 /*WA_ACTIVE*/, 0);
                PostMessageW(hwnd, WM_SETFOCUS, 0, 0);
                DI8Log("wm_activate: %s — posted WM_ACTIVATEAPP(1)+WM_ACTIVATE(1)+WM_SETFOCUS hwnd=0x%X",
                       freshInstall ? "subclass RE-INSTALLED" : "rising edge",
                       (unsigned)(uintptr_t)hwnd);
                ticksSinceRepost = 0;
            }

            // Layer 2: force flag = 1 every tick if pattern scan found it
            if (g_pActiveFlag)
                *g_pActiveFlag = 1;

            // Layer 3: re-post activation every ~200ms as fallback
            // Only WM_ACTIVATEAPP here — spamming WM_ACTIVATE/WM_SETFOCUS
            // every 200ms disrupts EQ's normal input processing.
            ticksSinceRepost++;
            if (ticksSinceRepost >= 12) {
                ticksSinceRepost = 0;
                PostMessageW(hwnd, WM_ACTIVATEAPP, TRUE, 0);
            }
        } else if (!active && wasActive && hwnd) {
            // SHM deactivated — restore EQ's natural state
            RemoveSubclass(hwnd);

            // Phantom-keys hotfix: restore original DI8 cooperative level
            // (typically FOREGROUND|EXCLUSIVE). Without this, EQ's keyboard
            // stays in BACKGROUND|NONEXCLUSIVE indefinitely after first auto-
            // login, causing EQ to read global OS keyboard state regardless
            // of focus — so any key pressed anywhere lands in EQ.
            if (g_coopSwitched && g_realKeyboardDevice) {
                g_realKeyboardDevice->Unacquire();
                HRESULT hr = g_realKeyboardDevice->SetCooperativeLevel(hwnd, g_originalCoopFlags);
                HRESULT acqHr = g_realKeyboardDevice->Acquire();
                // Hotfix v3 (MED-6): only clear g_coopSwitched on SetCoop success.
                // Acquire failing with E_ACCESSDENIED is expected when EQ lacks
                // focus — the device is in the right MODE (FOREGROUND), EQ will
                // reacquire when it regains focus. SetCoop failing means the
                // device is stuck in BACKGROUND — leave the flag true so the
                // next cycle retries; log loudly because phantom-keys will fire.
                if (SUCCEEDED(hr)) {
                    g_coopSwitched = false;
                    // v3.22.77: clean restore — no retry needed
                    retryTickCounter = 0;
                    retryAttempts = 0;
                    DI8Log("wm_activate: restored coop level (orig=0x%X SetCoop=0x%08X Acquire=0x%08X)",
                           (unsigned)g_originalCoopFlags, (unsigned)hr, (unsigned)acqHr);
                } else {
                    // v3.22.77: the standalone retry block below picks up
                    // from here, retrying SetCoopLevel at ~1Hz until it
                    // succeeds or hits the 60-attempt cap.
                    DI8Log("wm_activate: RESTORE FAILED (orig=0x%X SetCoop=0x%08X Acquire=0x%08X) — standalone retry block will retry at ~1Hz; phantom-keys risk until retry succeeds",
                           (unsigned)g_originalCoopFlags, (unsigned)hr, (unsigned)acqHr);
                }
            }

            HWND fg = GetForegroundWindow();
            if (fg != hwnd) {
                if (g_pActiveFlag)
                    *g_pActiveFlag = 0;
                PostMessageW(hwnd, WM_ACTIVATEAPP, FALSE, 0);
                DI8Log("wm_activate: deactivated — restored natural state");
            }
            ticksSinceRepost = 0;
        }
        // v3.22.77: standalone retry block. The falling-edge branch above
        // (!active && wasActive) only fires ONCE per autologin cycle, so a
        // single SetCoopLevel(FOREGROUND|EXCLUSIVE) failure used to leave
        // EQ in BACKGROUND|NONEXCLUSIVE indefinitely — keys pressed in any
        // other foreground app would leak into EQ via DI8's global keyboard
        // polling pipeline (a different pipeline from the WM_KEYDOWN
        // window-message queue, so LL keyboard hooks can't see the leak).
        // This block retries SetCoopLevel at ~1Hz (60 ticks at 60Hz) and
        // caps at 60 attempts so a fundamentally broken state doesn't loop
        // forever burning CPU. The cap is a tombstone — we log loudly and
        // give up; user can recover by restarting EQSwitch.
        else if (!active && g_coopSwitched && g_realKeyboardDevice && hwnd) {
            retryTickCounter++;
            if (retryTickCounter >= 60 && retryAttempts < 60) {
                retryTickCounter = 0;
                retryAttempts++;
                g_realKeyboardDevice->Unacquire();
                HRESULT hr = g_realKeyboardDevice->SetCooperativeLevel(hwnd, g_originalCoopFlags);
                // Acquire failure here is OK (typically E_ACCESSDENIED when EQ
                // lacks focus); the device is in the right MODE, EQ will
                // reacquire on focus regain. Same rationale as the falling-
                // edge restore branch above. Only SetCoop's hr matters.
                HRESULT acqHr = g_realKeyboardDevice->Acquire();
                if (SUCCEEDED(hr)) {
                    g_coopSwitched = false;
                    DI8Log("wm_activate: RESTORE RETRY #%d succeeded after ~%ds — phantom-key window closed (SetCoop=0x%08X Acquire=0x%08X)",
                           retryAttempts, retryAttempts, (unsigned)hr, (unsigned)acqHr);
                    retryAttempts = 0;
                } else if (retryAttempts <= 5 || retryAttempts == 60) {
                    // Log first 5 attempts to confirm the loop is firing,
                    // then go quiet until the cap to avoid log spam over
                    // 60 seconds of repeated failure.
                    DI8Log("wm_activate: RESTORE RETRY #%d failed (SetCoop=0x%08X Acquire=0x%08X)%s",
                           retryAttempts, (unsigned)hr, (unsigned)acqHr,
                           retryAttempts == 60 ? " — GIVING UP; phantom keys persist until next autologin or EQSwitch restart" : "");
                    // v3.24.4 — give-up: the device is stuck BACKGROUND. Stop the anti-bleed
                    // suppression so the keyboard stays usable in-world (leaky, pre-v3.24.4) rather
                    // than going dead. Re-enabled on the next login's rising edge.
                    if (retryAttempts == 60)
                        g_restoreGaveUp = true;
                }
            }
        }
        wasActive = active;
    }
    return 0;
}

static void StartActivateThread() {
    if (g_activateThreadStarted) return;
    g_hActivateThread = CreateThread(nullptr, 0, ActivateThread, nullptr, 0, nullptr);
    if (g_hActivateThread)
        g_activateThreadStarted = true;
    else
        DI8Log("StartActivateThread: CreateThread failed (%lu)", GetLastError());
}

// Signal background threads to exit and wait for them before releasing resources.
// Safe to wait here because Cleanup() in eqswitch-di8.cpp calls this AFTER
// WaitForSingleObject on the init thread — the loader lock is NOT held.
// On process exit (DLL_PROCESS_DETACH with reserved != NULL), Cleanup is
// skipped entirely — the OS reclaims everything.
extern "C" void DeviceProxy_Shutdown() {
    g_shutdown = true;
    // v7 Phase 3: no WM_TIMER to kill — GiveTime detour is uninstalled
    // separately by GiveTimeDetour::Uninstall() called from eqswitch-di8.cpp
    // Cleanup() right before MH_Uninitialize.
    HWND hwnd = g_eqHwnd;
    if (hwnd) RemoveSubclass(hwnd);
    // Wait for threads to observe g_shutdown and exit before releasing SHM.
    // ActivateThread sleeps 16ms, ShmPollingThread sleeps 8ms — 100ms is plenty.
    if (g_hActivateThread) {
        WaitForSingleObject(g_hActivateThread, 100);
        CloseHandle(g_hActivateThread);
        g_hActivateThread = nullptr;
    }
    if (g_hShmThread) {
        WaitForSingleObject(g_hShmThread, 50);
        CloseHandle(g_hShmThread);
        g_hShmThread = nullptr;
    }
    KeyShm::Close(); // now safe — threads have stopped
}

// --- SHM polling thread (Phase 3) ---
// Signals the keyboard event handle when synthetic keys change,
// waking EQ's DirectInput polling loop.

static DWORD WINAPI ShmPollingThread(LPVOID) {
    bool prevAnyKeys = false;
    while (!g_shutdown) {
        Sleep(8); // ~120Hz
        uint8_t keys[256];
        bool active = KeyShm::ReadKeys(keys);
        bool anyKeys = false;
        if (active) {
            for (int i = 0; i < 256; i++) {
                if (keys[i]) { anyKeys = true; break; }
            }
        }
        // Signal on press or release transitions
        if (anyKeys || (prevAnyKeys && !anyKeys)) {
            HANDLE h = g_kbEventHandle;
            if (h) SetEvent(h);
        }
        prevAnyKeys = anyKeys;
    }
    return 0;
}

static void StartShmPollingThread() {
    if (g_shmThreadStarted) return;
    g_hShmThread = CreateThread(nullptr, 0, ShmPollingThread, nullptr, 0, nullptr);
    if (g_hShmThread)
        g_shmThreadStarted = true;
    else
        DI8Log("StartShmPollingThread: CreateThread failed (%lu)", GetLastError());
}

// --- Constructor ---

DeviceProxy::DeviceProxy(void *real, bool isKeyboard)
    : m_real(reinterpret_cast<IDirectInputDevice8W *>(real))
    , m_refCount(1)
    , m_isKeyboard(isKeyboard)
    , m_synthSequence(0x80000000)
{
    memset(m_prevShmKeys, 0, sizeof(m_prevShmKeys));
    m_real->AddRef();
    if (isKeyboard)
        g_realKeyboardDevice = m_real;
}

// --- IUnknown -----------------------------------------------------------

HRESULT STDMETHODCALLTYPE DeviceProxy::QueryInterface(REFIID riid, void **ppv) {
    HRESULT hr = m_real->QueryInterface(riid, ppv);
    if (SUCCEEDED(hr)) {
        reinterpret_cast<IUnknown *>(*ppv)->Release();
        InterlockedIncrement(&m_refCount);
        *ppv = this;
    }
    return hr;
}

ULONG STDMETHODCALLTYPE DeviceProxy::AddRef() {
    return InterlockedIncrement(&m_refCount);
}

ULONG STDMETHODCALLTYPE DeviceProxy::Release() {
    LONG count = InterlockedDecrement(&m_refCount);
    if (count == 0) {
        m_real->Release();
        delete this;
        return 0;
    }
    return count;
}

// --- Intercepted methods ------------------------------------------------

HRESULT STDMETHODCALLTYPE DeviceProxy::Acquire() {
    HRESULT hr = m_real->Acquire();
    // When shm is active, fake success even if real Acquire fails
    // (BACKGROUND mode may fail to acquire in some edge cases)
    if (FAILED(hr) && m_isKeyboard && KeyShm::IsActive())
        return DI_OK;
    return hr;
}

// Diagnostic: log injection activity during login
static volatile int g_gdsLogCount = 0;
static volatile bool g_gdsWasActive = false;
static volatile int g_gdsCallCount = 0; // total calls for detecting if method is used

HRESULT STDMETHODCALLTYPE DeviceProxy::GetDeviceState(DWORD cbData, LPVOID lpvData) {
    HRESULT hr = m_real->GetDeviceState(cbData, lpvData);

    if (m_isKeyboard) {
        bool active = KeyShm::IsActive();
        int calls = InterlockedIncrement((volatile LONG*)&g_gdsCallCount);
        if (active && !g_gdsWasActive) {
            g_gdsLogCount = 0;
            DI8Log("GetDeviceState: === SHM active, injection enabled (callCount=%d) ===", calls);
        }
        // Periodic call counter to detect if method is used at all
        if (active && (calls % 500 == 0))
            DI8Log("GetDeviceState: heartbeat callCount=%d hr=0x%08X", calls, (unsigned)hr);
        g_gdsWasActive = active;

        DWORD kbLen = (cbData > 256) ? 256 : cbData;
        if (SUCCEEDED(hr)) {
            if (SuppressPhysicalInput())  // v3.24.4 — BACKGROUND ⇒ drop physical (anti-bleed)
                memset(lpvData, 0, kbLen);
            bool injected = KeyShm::InjectKeys((uint8_t *)lpvData, kbLen);
            if (injected && g_gdsLogCount < 100) {
                g_gdsLogCount++;
                // Log which scan codes are being injected
                for (DWORD i = 0; i < kbLen; i++) {
                    if (((uint8_t *)lpvData)[i] & 0x80)
                        DI8Log("GetDeviceState: scan 0x%02X=0x80 (injected) hr=0x%08X", i, (unsigned)hr);
                }
            }
        } else {
            memset(lpvData, 0, kbLen);
            bool injected = KeyShm::InjectKeys((uint8_t *)lpvData, kbLen);
            if (injected) {
                if (g_gdsLogCount < 100) {
                    g_gdsLogCount++;
                    DI8Log("GetDeviceState: device FAILED (0x%08X) but injected synthetic keys", (unsigned)hr);
                }
                return DI_OK;
            }
        }
    }
    return hr;
}

// Diagnostic: log GetDeviceData injection activity during login
static volatile int g_gddLogCount = 0;
static volatile bool g_gddWasActive = false;
static volatile int g_gddCallCount = 0;

HRESULT STDMETHODCALLTYPE DeviceProxy::GetDeviceData(
    DWORD cbObjectData, LPDIDEVICEOBJECTDATA rgdod,
    LPDWORD pdwInOut, DWORD dwFlags)
{
    if (!m_isKeyboard)
        return m_real->GetDeviceData(cbObjectData, rgdod, pdwInOut, dwFlags);

    DWORD originalCapacity = pdwInOut ? *pdwInOut : 0;
    bool peek = (dwFlags & DIGDD_PEEK) != 0;

    HRESULT hr = m_real->GetDeviceData(cbObjectData, rgdod, pdwInOut, dwFlags);
    DWORD realCount = SUCCEEDED(hr) ? *pdwInOut : 0;

    bool shmIsActive = KeyShm::IsActive();
    int calls = InterlockedIncrement((volatile LONG*)&g_gddCallCount);
    if (shmIsActive && !g_gddWasActive) {
        g_gddLogCount = 0;
        DI8Log("GetDeviceData: === SHM active, injection enabled (callCount=%d, hr=0x%08X, cap=%lu) ===",
               calls, (unsigned)hr, originalCapacity);
    }
    // Periodic heartbeat to detect if method is used at all
    if (shmIsActive && (calls % 500 == 0))
        DI8Log("GetDeviceData: heartbeat callCount=%d hr=0x%08X realCount=%lu", calls, (unsigned)hr, realCount);
    g_gddWasActive = shmIsActive;

    if (SuppressPhysicalInput() && realCount > 0)  // v3.24.4 — BACKGROUND ⇒ drop physical (anti-bleed)
        realCount = 0;

    // Read current synthetic key state
    uint8_t curKeys[256];
    bool shmActive = KeyShm::ReadKeys(curKeys);

    if (!shmActive) {
        if (!peek) memset(m_prevShmKeys, 0, 256);
        *pdwInOut = realCount;
        return hr;
    }

    // Detect changes since last non-peek read
    struct Change { uint8_t scan; uint8_t value; };
    Change changes[256];
    int numChanges = 0;
    for (int i = 0; i < 256; i++) {
        if (m_prevShmKeys[i] != curKeys[i]) {
            changes[numChanges].scan = (uint8_t)i;
            changes[numChanges].value = curKeys[i];
            numChanges++;
        }
    }

    if (numChanges == 0) {
        *pdwInOut = realCount;
        return SUCCEEDED(hr) ? hr : DI_OK; // keep device "alive" for background windows
    }

    // NULL rgdod = just querying count
    if (!rgdod) {
        *pdwInOut = realCount + (DWORD)numChanges;
        if (!peek) memcpy(m_prevShmKeys, curKeys, 256);
        return DI_OK;
    }

    // Guard against undersized object data structs (e.g. legacy DX3 callers)
    if (cbObjectData < sizeof(DIDEVICEOBJECTDATA)) {
        *pdwInOut = realCount;
        if (!peek) memcpy(m_prevShmKeys, curKeys, 256);
        return hr;
    }

    // Inject synthetic events into the buffer
    DWORD available = (originalCapacity > realCount) ? originalCapacity - realCount : 0;
    DWORD toInject = ((DWORD)numChanges < available) ? (DWORD)numChanges : available;
    DWORD timestamp = GetTickCount();

    uint8_t *bufStart = (uint8_t *)rgdod;
    for (DWORD j = 0; j < toInject; j++) {
        DIDEVICEOBJECTDATA *entry = (DIDEVICEOBJECTDATA *)
            (bufStart + (realCount + j) * cbObjectData);
        entry->dwOfs = changes[j].scan;
        entry->dwData = changes[j].value ? 0x80 : 0x00;
        entry->dwTimeStamp = timestamp;
        entry->dwSequence = m_synthSequence++;
        entry->uAppData = 0;
    }

    *pdwInOut = realCount + toInject;
    if (!peek) memcpy(m_prevShmKeys, curKeys, 256);

    // Log injected events
    if (toInject > 0 && g_gddLogCount < 200) {
        g_gddLogCount++;
        for (DWORD j = 0; j < toInject; j++) {
            DI8Log("GetDeviceData: injected scan=0x%02X data=0x%02X (event %d/%d)",
                   changes[j].scan, changes[j].value ? 0x80 : 0x00, j + 1, toInject);
        }
    }

    // Return DI_OK when we injected synthetic events — even if the real device
    // failed (DIERR_NOTACQUIRED for background windows). Without this, EQ sees
    // the error code and discards all injected keystroke data.
    return (toInject > 0) ? DI_OK : hr;
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetEventNotification(HANDLE hEvent) {
    HRESULT hr = m_real->SetEventNotification(hEvent);
    if (m_isKeyboard && hEvent) {
        g_kbEventHandle = hEvent;
        DI8Log("SetEventNotification: keyboard event=0x%X",
               (unsigned)(uintptr_t)hEvent);
        StartShmPollingThread();
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetCooperativeLevel(HWND hwnd, DWORD dwFlags) {
    if (m_isKeyboard) {
        g_eqHwnd = hwnd;
        g_originalCoopFlags = dwFlags;

        if (KeyShm::IsActive()) {
            // Mid-login: EQ is re-setting coop level (e.g. server→charselect transition).
            // Apply BACKGROUND immediately — the ActivateThread won't catch this because
            // SHM was already active (no false→true transition to trigger it).
            DWORD bgFlags = (dwFlags & ~(DISCL_EXCLUSIVE | DISCL_FOREGROUND))
                          | DISCL_NONEXCLUSIVE | DISCL_BACKGROUND;
            StartActivateThread();
            HRESULT hr = m_real->SetCooperativeLevel(hwnd, bgFlags);
            if (SUCCEEDED(hr)) {
                g_coopSwitched = true;
                DI8Log("SetCooperativeLevel: keyboard hwnd=0x%X flags=0x%X → 0x%X (SHM active, forced BACKGROUND)",
                       (unsigned)(uintptr_t)hwnd, (unsigned)dwFlags, (unsigned)bgFlags);
            } else {
                // Hotfix v3 (MED-5): don't mark as switched if the actual call
                // failed. Leaves state coherent with the device for the next
                // restore attempt.
                DI8Log("SetCooperativeLevel: SHM-active forced BACKGROUND FAILED (hr=0x%08X) — g_coopSwitched left unchanged",
                       (unsigned)hr);
            }
            // Re-post activation — EQ may have deactivated during screen transition
            PostMessageW(hwnd, WM_ACTIVATEAPP, TRUE, 0);
            return hr;
        }

        g_coopSwitched = false;
        // Always strip EXCLUSIVE — EQ works fine with NONEXCLUSIVE, and EXCLUSIVE
        // blocks other EQ instances from switching to BACKGROUND cooperative level.
        // Keep FOREGROUND initially (switching to BACKGROUND at startup makes EQ minimize).
        DWORD safeFlags = (dwFlags & ~DISCL_EXCLUSIVE) | DISCL_NONEXCLUSIVE;
        DI8Log("SetCooperativeLevel: keyboard hwnd=0x%X flags=0x%X → 0x%X (stripped EXCLUSIVE)",
               (unsigned)(uintptr_t)hwnd, dwFlags, safeFlags);
        StartActivateThread();
        return m_real->SetCooperativeLevel(hwnd, safeFlags);
    }
    return m_real->SetCooperativeLevel(hwnd, dwFlags);
}

// --- Pure forwarding methods (unchanged) --------------------------------

HRESULT STDMETHODCALLTYPE DeviceProxy::GetCapabilities(LPDIDEVCAPS lpDIDevCaps) {
    return m_real->GetCapabilities(lpDIDevCaps);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::EnumObjects(
    LPDIENUMDEVICEOBJECTSCALLBACKW lpCallback, LPVOID pvRef, DWORD dwFlags)
{
    return m_real->EnumObjects(lpCallback, pvRef, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetProperty(
    REFGUID rguidProp, LPDIPROPHEADER pdiph)
{
    return m_real->GetProperty(rguidProp, pdiph);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetProperty(
    REFGUID rguidProp, LPCDIPROPHEADER pdiph)
{
    return m_real->SetProperty(rguidProp, pdiph);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::Unacquire() {
    return m_real->Unacquire();
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetDataFormat(LPCDIDATAFORMAT lpdf) {
    return m_real->SetDataFormat(lpdf);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetObjectInfo(
    LPDIDEVICEOBJECTINSTANCEW pdidoi, DWORD dwObj, DWORD dwHow)
{
    return m_real->GetObjectInfo(pdidoi, dwObj, dwHow);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetDeviceInfo(LPDIDEVICEINSTANCEW pdidi) {
    return m_real->GetDeviceInfo(pdidi);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::RunControlPanel(
    HWND hwndOwner, DWORD dwFlags)
{
    return m_real->RunControlPanel(hwndOwner, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::Initialize(
    HINSTANCE hinst, DWORD dwVersion, REFGUID rguid)
{
    return m_real->Initialize(hinst, dwVersion, rguid);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::CreateEffect(
    REFGUID rguid, LPCDIEFFECT lpeff,
    LPDIRECTINPUTEFFECT *ppdeff, LPUNKNOWN punkOuter)
{
    return m_real->CreateEffect(rguid, lpeff, ppdeff, punkOuter);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::EnumEffects(
    LPDIENUMEFFECTSCALLBACKW lpCallback, LPVOID pvRef, DWORD dwEffType)
{
    return m_real->EnumEffects(lpCallback, pvRef, dwEffType);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetEffectInfo(
    LPDIEFFECTINFOW pdei, REFGUID rguid)
{
    return m_real->GetEffectInfo(pdei, rguid);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetForceFeedbackState(LPDWORD pdwOut) {
    return m_real->GetForceFeedbackState(pdwOut);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SendForceFeedbackCommand(DWORD dwFlags) {
    return m_real->SendForceFeedbackCommand(dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::EnumCreatedEffectObjects(
    LPDIENUMCREATEDEFFECTOBJECTSCALLBACK lpCallback, LPVOID pvRef, DWORD fl)
{
    return m_real->EnumCreatedEffectObjects(lpCallback, pvRef, fl);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::Escape(LPDIEFFESCAPE pesc) {
    return m_real->Escape(pesc);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::Poll() {
    return m_real->Poll();
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SendDeviceData(
    DWORD cbObjectData, LPCDIDEVICEOBJECTDATA rgdod,
    LPDWORD pdwInOut, DWORD fl)
{
    return m_real->SendDeviceData(cbObjectData, rgdod, pdwInOut, fl);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::EnumEffectsInFile(
    LPCWSTR lpszFileName, LPDIENUMEFFECTSINFILECALLBACK pec,
    LPVOID pvRef, DWORD dwFlags)
{
    return m_real->EnumEffectsInFile(lpszFileName, pec, pvRef, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::WriteEffectToFile(
    LPCWSTR lpszFileName, DWORD dwEntries,
    LPDIFILEEFFECT rgDiFileEft, DWORD dwFlags)
{
    return m_real->WriteEffectToFile(lpszFileName, dwEntries, rgDiFileEft, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::BuildActionMap(
    LPDIACTIONFORMATW lpdiaf, LPCWSTR lpszUserName, DWORD dwFlags)
{
    return m_real->BuildActionMap(lpdiaf, lpszUserName, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetActionMap(
    LPDIACTIONFORMATW lpdiaf, LPCWSTR lpszUserName, DWORD dwFlags)
{
    return m_real->SetActionMap(lpdiaf, lpszUserName, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetImageInfo(
    LPDIDEVICEIMAGEINFOHEADERW lpdiDevImageInfoHeader)
{
    return m_real->GetImageInfo(lpdiDevImageInfoHeader);
}
