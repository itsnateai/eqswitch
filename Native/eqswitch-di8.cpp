// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

// eqswitch-di8.dll — Injected into eqgame.exe via CreateRemoteThread
//
// Hooks DirectInput8Create via MinHook detour to wrap COM objects for
// background keyboard input. Injected into a CREATE_SUSPENDED process
// before the main thread starts, so the hook is in place before EQ
// calls DirectInput8Create.
//
// Load chain: eqgame.exe → Dalaya's dinput8.dll (untouched) → system32
//             ↑ our detour wraps the result in DI8Proxy/DeviceProxy
//
// Also installs:
//   - IAT hooks for GetAsyncKeyState/GetForegroundWindow etc. (iat_hook.cpp)
//   - Winsock hooks for disconnect diagnostics (net_debug.cpp)

#define _CRT_SECURE_NO_WARNINGS
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <dinput.h>
#include <stdio.h>
#include <stdarg.h>
#include "MinHook.h"
#include "di8_proxy.h"
#include "iat_hook.h"
#include "net_debug.h"
#include "mq2_bridge.h"
#include "login_givetime_detour.h"
#include "login_state_machine.h"
#include "eqmain_offsets.h"
#include "eqmain_widgets_mq2style.h"

extern "C" void DeviceProxy_Shutdown();

// ─── DLL Load Notification — MQ2-style eqmain detection ────────
// STEP 1 of the MQ2 port. Uses ntdll's LdrRegisterDllNotification to
// get a synchronous callback for every DLL load/unload in the process.
// Fires BEFORE the new DLL's DllMain runs, so anything we want to set up
// on eqmain load is guaranteed to happen before eqmain executes any code.
//
// This step is OBSERVATION ONLY — the callback logs the event and does
// nothing else. Step 2 will hang InitializeEQMainOffsets() off the load
// event. Step 3 will use the ShutdownEQMain-equivalent off the unload.
//
// Ported verbatim (with 32-bit-safe integer types) from MacroQuest's
// EQLibImpl.cpp:536-571, :693-738, :740-754 in the local rof2-emu fork.
// Loader-lock safety: callback runs inside NT loader lock. We do NOT call
// LoadLibrary/FreeLibrary or any locking functions from inside it — just
// DI8Log which is loader-lock-safe per MSVC CRT.

namespace {

struct UNICODE_STRING {
    uint16_t Length;
    uint16_t MaximumLength;
    wchar_t* Buffer;
};

struct LDR_DLL_NOTIFICATION_DATA {
    uint32_t        Flags;
    UNICODE_STRING* FullDllName;
    UNICODE_STRING* BaseDllName;
    uintptr_t       DllBase;
    uint32_t        SizeOfImage;
};

constexpr uint32_t LDR_DLL_NOTIFICATION_REASON_LOADED   = 1;
constexpr uint32_t LDR_DLL_NOTIFICATION_REASON_UNLOADED = 2;

using PLDR_DLL_NOTIFICATION_FUNCTION = void(__stdcall*)(
    uint32_t, const LDR_DLL_NOTIFICATION_DATA*, void*);
using PLDR_REGISTER_DLL_NOTIFICATION = uint32_t(__stdcall*)(
    uint32_t, PLDR_DLL_NOTIFICATION_FUNCTION, void*, void**);
using PLDR_UNREGISTER_DLL_NOTIFICATION = uint32_t(__stdcall*)(void*);

static void* g_loaderNotificationCookie = nullptr;

// Iter 15: when eqmain.dll loads, set this flag. The next MQ2BridgePollTick
// will see it and prioritize the SHM open + login state machine work over
// any other deferred init. Lets us collapse the ~500ms wait between eqmain
// LOAD and the next poll noticing.
static volatile LONG g_eqmainJustLoaded = 0;

// Case-insensitive wide-string equality. Windows can present module names
// with any case — MQ2 uses mq::ci_equals here for the same reason.
static bool CiEqualsW(const wchar_t* a, const wchar_t* b) {
    while (*a && *b) {
        wchar_t ca = (*a >= L'A' && *a <= L'Z') ? (wchar_t)(*a - L'A' + L'a') : *a;
        wchar_t cb = (*b >= L'A' && *b <= L'Z') ? (wchar_t)(*b - L'A' + L'a') : *b;
        if (ca != cb) return false;
        ++a; ++b;
    }
    return *a == *b;
}

static void __stdcall LdrDllNotificationCallback(
    uint32_t reason,
    const LDR_DLL_NOTIFICATION_DATA* data,
    void* /*context*/)
{
    if (!data || !data->BaseDllName || !data->BaseDllName->Buffer) return;

    if (!CiEqualsW(data->BaseDllName->Buffer, L"eqmain.dll")) return;

    if (reason == LDR_DLL_NOTIFICATION_REASON_LOADED) {
        DI8Log("dll_notify: eqmain.dll LOADED at 0x%08X (size 0x%X) — callback fired BEFORE eqmain DllMain",
               (unsigned)data->DllBase, data->SizeOfImage);
        // STEP 2A: publish range so mq2_bridge can identify eqmain-owned widgets.
        EQMainOffsets::OnEQMainLoaded(data->DllBase, data->SizeOfImage);

        // Iter 15: signal the poll loop that eqmain just loaded so it can
        // skip the throttle on the next tick and immediately try SHM open +
        // login state machine work. Avoids the loader-lock concern of
        // calling OpenFileMappingA directly from this callback.
        InterlockedExchange(&g_eqmainJustLoaded, 1);
    } else if (reason == LDR_DLL_NOTIFICATION_REASON_UNLOADED) {
        DI8Log("dll_notify: eqmain.dll UNLOADING — will tear down eqmain-side state");
        // STEP 2A: clear range so IsEQMainWidget returns false for dangling ptrs.
        EQMainOffsets::OnEQMainUnloaded();
    }
}

static void RegisterDllNotification() {
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) {
        DI8Log("dll_notify: ntdll.dll not resolvable — poll-only mode");
        return;
    }
    auto pReg = (PLDR_REGISTER_DLL_NOTIFICATION)GetProcAddress(
        ntdll, "LdrRegisterDllNotification");
    if (!pReg) {
        DI8Log("dll_notify: LdrRegisterDllNotification not exported — poll-only mode");
        return;
    }
    uint32_t status = pReg(0, &LdrDllNotificationCallback, nullptr,
                           &g_loaderNotificationCookie);
    if (status != 0) {
        DI8Log("dll_notify: LdrRegisterDllNotification NTSTATUS=0x%08X — poll-only mode",
               (unsigned)status);
        g_loaderNotificationCookie = nullptr;
        return;
    }
    DI8Log("dll_notify: registered (cookie=%p)", g_loaderNotificationCookie);

    // Mirror MQ2's fallback-then-register: if eqmain is ALREADY loaded by
    // the time we register, we missed its load notification. Fire the
    // handler manually with a synthesized data record so the load path
    // still runs.
    HMODULE existing = GetModuleHandleW(L"eqmain.dll");
    if (existing) {
        DI8Log("dll_notify: eqmain.dll ALREADY loaded at %p — firing synthetic LOAD event",
               existing);
        // Synthesize just enough of the notification data for the callback.
        // We don't have SizeOfImage from the real event; pull it from the PE.
        uint8_t* base = (uint8_t*)existing;
        uint32_t sizeOfImage = 0;
        if (*(uint16_t*)base == 0x5A4D) {
            int32_t elf = *(int32_t*)(base + 0x3C);
            if (elf > 0 && elf < 0x1000 && *(uint32_t*)(base + elf) == 0x00004550) {
                sizeOfImage = *(uint32_t*)(base + elf + 24 + 56);
            }
        }
        UNICODE_STRING baseName = { 22, 24, (wchar_t*)L"eqmain.dll" }; // 11 wchars * 2 bytes
        LDR_DLL_NOTIFICATION_DATA synth = { 0, nullptr, &baseName,
                                            (uintptr_t)existing, sizeOfImage };
        LdrDllNotificationCallback(LDR_DLL_NOTIFICATION_REASON_LOADED, &synth, nullptr);
    }
}

static void UnregisterDllNotification() {
    if (!g_loaderNotificationCookie) return;
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) return;
    auto pUnreg = (PLDR_UNREGISTER_DLL_NOTIFICATION)GetProcAddress(
        ntdll, "LdrUnregisterDllNotification");
    if (!pUnreg) return;
    pUnreg(g_loaderNotificationCookie);
    g_loaderNotificationCookie = nullptr;
    DI8Log("dll_notify: unregistered");
}

} // anonymous namespace

// ─── CharSelect Shared Memory ──────────────────────────────────
// Opened lazily — created by C# CharSelectReader, DLL reads/writes.

static HANDLE g_charSelMap = nullptr;
static volatile CharSelectShm* g_charSelShm = nullptr;
static uint32_t g_charSelRetry = 0;

// ─── Login Shared Memory ──────────────────────────────────────
// Opened lazily — created by C# AutoLoginManager, DLL reads/writes.
static HANDLE g_loginShmMap = nullptr;
// External-linkage so mq2_bridge.cpp can read g_loginShm->character for
// the single-char anchor-scan path (Track B v3 / 2026-05-05). Marked
// volatile because C# writes it from a different thread.
volatile LoginShm* g_loginShm = nullptr;
static uint32_t g_loginShmRetry = 0;

static bool TryOpenCharSelShm() {
    DWORD pid = GetCurrentProcessId();
    char name[64];
    snprintf(name, sizeof(name), "Local\\EQSwitchCharSel_%lu", pid);

    HANDLE h = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, name);
    if (!h) return false;

    void* view = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(CharSelectShm));
    if (!view) {
        CloseHandle(h);
        return false;
    }

    g_charSelMap = h;
    g_charSelShm = (volatile CharSelectShm*)view;
    DI8Log("mq2_bridge: opened CharSelect SHM (magic=0x%08X)", g_charSelShm->magic);
    return true;
}

static void CloseCharSelShm() {
    if (g_charSelShm) { UnmapViewOfFile((void*)g_charSelShm); g_charSelShm = nullptr; }
    if (g_charSelMap) { CloseHandle(g_charSelMap); g_charSelMap = nullptr; }
}

static bool TryOpenLoginShm() {
    DWORD pid = GetCurrentProcessId();
    char name[64];
    snprintf(name, sizeof(name), "Local\\EQSwitchLogin_%lu", pid);

    HANDLE h = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, name);
    if (!h) return false;

    void* view = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(LoginShm));
    if (!view) {
        CloseHandle(h);
        return false;
    }

    g_loginShmMap = h;
    g_loginShm = (volatile LoginShm*)view;
    DI8Log("login_shm: opened (magic=0x%08X, version=%u)", g_loginShm->magic, g_loginShm->version);
    return true;
}

static void CloseLoginShm() {
    if (g_loginShm) { UnmapViewOfFile((void*)g_loginShm); g_loginShm = nullptr; }
    if (g_loginShmMap) { CloseHandle(g_loginShmMap); g_loginShmMap = nullptr; }
}

// Called from device_proxy.cpp's ActivateThread (~60Hz, throttled to ~500ms internally)
static bool g_mq2Initialized = false;
static uint32_t g_mq2InitRetry = 0;

// Hotfix v4 (HIGH-D): RAII reentrancy guard for MQ2BridgePollTick. ActivateThread
// and TIMERPROC can race to satisfy the 500ms throttle simultaneously. Without this
// guard, BOTH threads could enter MQ2Bridge::Poll concurrently and fire XWM_LCLICK
// on CLW_EnterWorldButton twice — double enter-world. The existing "// double-fire
// is harmless" comment predates the SHM enter-world side-effect semantics.
struct PollReentryGuard {
    volatile LONG* flag;
    bool entered;
    PollReentryGuard(volatile LONG* f) : flag(f) {
        entered = (InterlockedCompareExchange(flag, 1, 0) == 0);
    }
    ~PollReentryGuard() { if (entered) InterlockedExchange(flag, 0); }
};

// MSVC C2712: __try/__except is not allowed in a function that has C++
// destructors on the stack (PollReentryGuard). Wrap the SHM seq read in
// this helper which has NO C++ unwinding so SEH can be used freely.
// Defensive: SHM mapping can be torn during process shutdown; observing
// that as "no pending" is the right default.
static bool ShmHasPendingRequest(volatile CharSelectShm *shm) {
    if (!shm) return false;
    bool pending = false;
    __try {
        pending = (shm->requestSeq != shm->ackSeq) ||
                  (shm->enterWorldReq != shm->enterWorldAck);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        pending = false;
    }
    return pending;
}

void MQ2BridgePollTick() {
    static volatile LONG s_pollInProgress = 0;
    PollReentryGuard guard(&s_pollInProgress);
    if (!guard.entered) return;  // another thread is already in Poll

    // v3.15.11 two-tier throttle. The pre-v3.15.11 gate was:
    //     if (!bypassThrottle && now - lastPoll < 500) return;
    //     lastPoll = now;
    // which made C# requests (selection ack, enter-world ack) wait avg 250ms
    // (worst-case 500ms) before the bridge looked at them. v3.15.10 log showed
    // slot-req → "Entering world..." span = 426ms — almost entirely throttle
    // wait. The new gate keeps the 500ms cadence for the EXPENSIVE work
    // (heap scans, latch counter, kPromptWindows walk, LoginStateMachine tick)
    // but lets pending C# requests run through MQ2Bridge::PollRequestsOnly at
    // the unthrottled ~16ms cadence of ActivateThread + TIMERPROC.
    //
    // CRITICAL INVARIANT: the fast path must NOT run any code that increments
    // 500ms-cadence counters. mq2_bridge.cpp::g_consecutiveNullPolls (latch
    // clear at 30 polls = 15s) + g_standaloneDelay (heap-scan gate at 20
    // polls = 10s) + the lazy-init g_mq2InitRetry (10 polls = 5s) all assume
    // exactly one tick per 500ms. PollRequestsOnly only runs the two cheap
    // handlers — no counter increments, no heap walks. Don't move SHM open,
    // MQ2 init guard, or kPromptWindows into the fast path.
    //
    // Iter 15 carry-over: bypassThrottle (eqmain.dll just loaded) still forces
    // a full poll on the next tick so SHM open + state machine start
    // immediately. The flag is consumed exactly once via InterlockedExchange.
    static volatile DWORD lastFullPoll = 0;  // accessed from ActivateThread + TIMERPROC
    DWORD now = GetTickCount();
    bool bypassThrottle = (InterlockedExchange(&g_eqmainJustLoaded, 0) != 0);

    // SHM seq probe via static helper — see ShmHasPendingRequest comment for
    // why this isn't inline (MSVC C2712: __try forbidden in functions with
    // C++ destructors, and PollReentryGuard above is one).
    bool hasPendingRequest = ShmHasPendingRequest(g_charSelShm);

    bool runFullPoll = bypassThrottle || (now - lastFullPoll >= 500);

    if (!runFullPoll) {
        if (!hasPendingRequest) return;
        // Fast path: handle the request via the cheap PollRequestsOnly entry
        // and return. lastFullPoll deliberately not updated — the next full
        // poll still fires on its 500ms cadence. C# request protocol
        // guarantees charSelectReady=1 + charCount were published by a prior
        // full poll (RequestSelectionBySlot only fires after observing
        // charSelectReady), so the handlers have what they need.
        if (g_mq2Initialized && g_charSelShm) {
            MQ2Bridge::PollRequestsOnly(g_charSelShm);
        }
        return;
    }

    lastFullPoll = now;

    // ── Iter 15 fix: SHM open + login state machine BEFORE MQ2 init guard ──
    // The prior order had MQ2Bridge::Init's failure-retry counter (set to 10 ticks
    // = 5 seconds) gating the entire poll body. SHM open and the login state
    // machine have NO dependency on MQ2 bridge — the bridge is needed only for
    // char-select widget enumeration (which fires from the CharSelShm path).
    // Putting them first means a failed MQ2 init no longer blocks the password
    // entry for up to 5 seconds. (User reported being able to manually type the
    // password and reach char-select before our DLL even attempted Combo G —
    // root cause was this guard, not eqgame.exe boot time.)
    if (!g_loginShm) {
        TryOpenLoginShm();
    }
    if (g_loginShm && g_loginShm->magic == LOGIN_SHM_MAGIC) {
        LoginStateMachine::Tick(g_loginShm, g_charSelShm);
    }

    if (!g_charSelShm) {
        TryOpenCharSelShm();
    }

    // Lazy MQ2 bridge init — retries every ~5 seconds until MQ2 globals are ready.
    // Now gates ONLY the CharSel poll path (which actually needs the bridge),
    // not the login state machine.
    if (!g_mq2Initialized) {
        if (g_mq2InitRetry == 0) {
            g_mq2Initialized = MQ2Bridge::Init();
            if (!g_mq2Initialized)
                g_mq2InitRetry = 10;  // Retry in ~5 seconds (10 × 500ms)
        } else {
            g_mq2InitRetry--;
        }
        if (!g_mq2Initialized) return;
    }

    // Auto-dismiss pre-login modals. SIDL widget names ported from MQ2:
    //   _.src/_srcexamples/macroquest-rof2-emu/src/plugins/autologin/MQ2AutoLogin.cpp:1199-1206
    //
    // Background — confirmed by Nate 2026-05-08: DECLINE is the default Enter
    // focus on Dalaya's EULA. The keystroke approach (sending VK_RETURN via
    // DI8 SHM) closed the game on every Launch Client. Direct widget-click
    // via WndNotification(XWM_LCLICK) targets the right button by SIDL name
    // regardless of focus.
    //
    // Session gate: only run when gameState != 5 (NOT in-game). After
    // charselect transitions to PRE_CHARSELECT (gameState == 0) the prompt
    // chain is gone, but the explicit `gameState == 5` skip prevents this
    // walk from racing autologin BURST 1's keystrokes during in-game state
    // (legacy iter-12 starvation rationale — heap walks on game thread can
    // stall the EQ input pump and drop background-client BURST keys; per the
    // 2026-05-15 toggle re-enable comment in eqmain_widgets_mq2style.h, this
    // gating is still load-bearing during in-game state where BURST is not
    // active). gameState reading
    // returns -99 on SEH; we still run in that case (fresh-launch
    // pre-init — EULA may be up before exports resolve).
    //
    // Lookup strategy per row:
    //   1. EQMainWidgetsMQ2::FindChildByName(window, button) — anchored
    //      hierarchical walk. Returns the LIVE CButtonWnd; ClickButton
    //      dispatches via vtable.
    //   2. Fallback: MQ2Bridge::FindWindowByName(button). Often returns a
    //      CXMLDataPtr definition; ClickButton will skip + log. Kept cheap.
    //
    // Loop continues across all rows per tick — `break` on first FOUND but
    // skipped-by-ClickButton would leave subsequent prompts un-tried. Each
    // ClickButton is idempotent (no-op if not a live button vtable).
    //
    // OrderWindow / OrderExpansionWindow intentionally OMITTED — auto-clicking
    // DECLINE on a future Dalaya-repurposed "OrderWindow" risks silently
    // declining server prompts the user wanted to see. Re-add only after
    // confirming the SIDL name is exclusive to dismissable retail prompts.
    //
    // 2026-05-08 update: the screen Nate observed between EULA and login was
    // "main" (Dalaya's post-EULA login-options menu) — handled by the second
    // pair of kPromptWindows entries below. Verified end-to-end via screenshot
    // capture during smoketest: bare Launch Client lands on the login screen.
    if (MQ2Bridge::ReadGameState() != 5) {
        // v3.15.7 (2026-05-09): suppress kPromptWindows dismiss machinery when
        // C# AutoLoginManager is driving the login flow for this PID. Without
        // this gate, the dismiss iterates EVERY native poll-tick from
        // gameState=0 (login screen) all the way to gameState=5 (in-game) —
        // including server-select transitions and the char-select load window.
        // The 2026-05-09 team1 regression: at server-select / charselect-load,
        // a transient kPromptWindows match (news / stale main / repurposed
        // widget slipping past IsCXWndVisible) fired WndNotification(XWM_LCLICK)
        // and the EQ process self-exited within ~7 seconds. 4-of-4 reproduction.
        //
        // Bare-launch path (no autologin) still hits the dismiss as designed —
        // autoLoginActive defaults to 0 in that case, so EULA + main-menu
        // auto-click continues to work for the v3.15.5 use case.
        bool suppressDismiss = (g_loginShm != nullptr && g_loginShm->autoLoginActive != 0);
        if (suppressDismiss) {
            // AutoLoginManager owns the keystroke flow for this PID. Skip the
            // entire prompt-dismiss block; the rest of the poll tick (gameState
            // updates, char-data publishing) still runs.
            goto SkipPromptDismiss;
        }
        struct PromptWindow { const char *windowName; const char *buttonName; };
        static const PromptWindow kPromptWindows[] = {
            // MQ2's SIDL widget name first; Dalaya visible-label fallback
            // second. Verified 2026-05-08 via DumpSubtreeNamesOnce + screen-
            // capture: Dalaya widgets store their visible button label as the
            // first CXStr field reachable by the heuristic scan. The SIDL
            // identifier exists but at a higher offset our scanner doesn't
            // reach — the visible-label fallback is the empirically working
            // path on Dalaya.
            //
            // EULA dismiss verified working 2026-05-08: ACCEPT match landed
            // the live CButtonWnd at vtRva 0x10B53C, ClickButton dispatched
            // WndNotification(XWM_LCLICK), modal closed, advanced to "main"
            // menu (Dalaya's post-EULA login-options screen).
            { "EulaWindow",           "EULA_AcceptButton"       },
            { "EulaWindow",           "ACCEPT"                  },
            // "main" = Dalaya's post-EULA login-options screen with buttons
            // LOGIN / ACCOUNT / EQ LIVE WEBPAGE / HELP / OPTIONS / EXIT. We
            // want LOGIN. MAIN_ConnectButton is MQ2's SIDL name (Dalaya may
            // or may not match); LOGIN is the visible-label fallback.
            { "main",                 "MAIN_ConnectButton"      },
            { "main",                 "LOGIN"                   },
            { "seizurewarning",       "HELP_OKButton"           },
            { "news",                 "NEWS_OKButton"           },
        };

        for (const auto &pw : kPromptWindows) {
            // First, find the live screen (top-level CXWnd named windowName).
            // CRITICAL: also gate on IsCXWndVisible — Dalaya keeps prompt
            // CXWnds in the live tree after they're dismissed (just with
            // dShow=0). Without this gate, the polling loop fires clicks
            // on stale-but-invisible widgets every 500ms forever, spamming
            // the log + risking unintended notification dispatch.
            void *pScreen = EQMainWidgetsMQ2::FindLiveScreenByName(pw.windowName);
            if (!pScreen) continue;
            if (!EQMainWidgetsMQ2::IsCXWndVisible(pScreen)) continue;

            void *pBtn = EQMainWidgetsMQ2::RecurseAndFindName(pScreen, pw.buttonName);
            const char *via = "live (anchored)";
            if (!pBtn) {
                // Live visible screen exists but recursive walk can't find the
                // button by name — fall back to top-level FindWindowByName
                // (often a CXMLDataPtr def; ClickButton skips + logs).
                // For diagnosing what IS in the subtree, re-attach the
                // DumpSubtreeNamesOnce diagnostic from
                // _.claude/_tools/eqswitch-debug/re-enable-debug.md.
                pBtn = MQ2Bridge::FindWindowByName(pw.buttonName);
                via = "def fallback";
            }
            if (pBtn) {
                MQ2Bridge::ClickButton(pBtn);
                DI8Log("eqswitch-di8: prompt dismiss attempted '%s'→'%s' via %s @ %p",
                       pw.windowName, pw.buttonName, via, pBtn);
                // No `break` — try every row each tick. ClickButton is
                // idempotent on def-pointer skips, and the visibility gate
                // above ensures we only click on actually-shown windows.
            }
        }
    SkipPromptDismiss:;  // jumped here from the autologin-active suppress path
    }

    if (g_charSelShm && g_charSelShm->magic == CHARSEL_SHM_MAGIC) {
        MQ2Bridge::Poll(g_charSelShm);
    }
}

// ─── Globals ────────────────────────────────────────────────────

typedef HRESULT(WINAPI *PFN_DirectInput8Create)(
    HINSTANCE, DWORD, REFIID, LPVOID *, LPUNKNOWN);

static PFN_DirectInput8Create g_trampolineCreate = nullptr;  // MinHook trampoline → Dalaya's original
static HMODULE g_hModule = nullptr;
static HANDLE g_initThread = nullptr;
static HANDLE g_stopEvent = nullptr;   // signaled on DLL_PROCESS_DETACH to stop init thread cooperatively
static volatile LONG g_initialized = 0;
static bool g_hookInstalled = false;

// v3.22.44 Gate #4 — cross-TU detour critical section. Detours in this file
// (HookedDirectInput8Create) and iat_hook.cpp (HookedGetAsyncKeyState +
// 8 sibling user32 hooks) Enter this at entry, Leave on exit. Cleanup()
// Enters it before the IAT-restore / MinHook-uninstall teardown so any
// EQ thread mid-detour drains before we yank the trampoline pages.
// Non-static — iat_hook.cpp references via `extern CRITICAL_SECTION
// g_di8DetourCs;`. Matches MacroQuest's gDetourCS discipline.
//
// v3.22.44 round-2 (T3-Opus HIGH #1): switched from SRWLOCK to
// CRITICAL_SECTION because of the IAT-redirect → inline-patch chain in
// iat_hook.cpp. HookedGetForegroundWindow (IAT-replaced) takes shared,
// then calls g_realGetForegroundWindow whose prologue is inline-patched
// to InlineHookedGetForegroundWindow which takes shared AGAIN on the same
// thread. SRWLOCK forbids recursive shared acquire — with an exclusive
// waiter pending (Cleanup running) the inner shared blocks → outer cannot
// release → permanent deadlock that hangs eqgame.exe. CRITICAL_SECTION is
// recursive by design. The deferred GiveTime_Detour wrapping concern from
// round 1 is partially resolved too: the recursive-acquire reason that
// blocked locking GiveTime no longer applies with CRITICAL_SECTION. (We
// still don't lock GiveTime in round 2 because the existing g_detachInProgress
// guard in login_givetime_detour.cpp covers the operational path; revisit
// in a follow-up to add full coverage.)
//
// Initialized in DllMain DLL_PROCESS_ATTACH (safe — no loader-lock
// interaction) before the init thread is spawned. NOT deleted in Cleanup —
// the DLL is unloading; the kernel object goes away with the .data section.
CRITICAL_SECTION g_di8DetourCs;
volatile LONG g_di8DetourCsInitialized = 0;  // set by DllMain ATTACH

// v3.22.44 round-2 (T3-Opus HIGH #2): RAII guard so HookedDirectInput8Create's
// `new DI8Proxy(*ppvOut)` throw path can't leak the critical section.
// Sibling pattern to iat_hook.cpp's DetourSharedLock, but Enters/Leaves a
// CRITICAL_SECTION instead of SRWLOCK shared.
namespace {
struct Di8DetourLock {
    Di8DetourLock() { EnterCriticalSection(&g_di8DetourCs); }
    ~Di8DetourLock() { LeaveCriticalSection(&g_di8DetourCs); }
    Di8DetourLock(const Di8DetourLock&) = delete;
    Di8DetourLock& operator=(const Di8DetourLock&) = delete;
};
} // namespace

// ─── Logging ────────────────────────────────────────────────────

static FILE *g_logFile = nullptr;
static volatile LONG g_logInitAttempted = 0;
static char g_logPath[MAX_PATH] = {};

static void EnsureLogOpen() {
    if (g_logFile) return;
    // Atomic CAS — only one thread opens the file
    if (InterlockedCompareExchange(&g_logInitAttempted, 1, 0) != 0) return;
    if (g_logPath[0])
        g_logFile = fopen(g_logPath, "w");
}

void DI8Log(const char *fmt, ...) {
    EnsureLogOpen();
    FILE *f = g_logFile;
    if (!f) return;
    fprintf(f, "[%lu] ", GetTickCount());
    va_list args;
    va_start(args, fmt);
    vfprintf(f, fmt, args);
    va_end(args);
    fprintf(f, "\n");
    fflush(f);
}

// ─── DirectInput8Create Detour ──────────────────────────────────

static HRESULT WINAPI HookedDirectInput8Create(
    HINSTANCE hinst, DWORD dwVersion, REFIID riidltf,
    LPVOID *ppvOut, LPUNKNOWN punkOuter)
{
    // v3.22.44 r2 (T3-Opus HIGH #2): RAII guard replaces manual Enter/Leave.
    // The `new DI8Proxy(*ppvOut)` line below can throw std::bad_alloc; without
    // RAII the throw skips the manual LeaveCriticalSection and permanently
    // leaks the CS, so every future Cleanup() / Detach-Hooks attempt
    // deadlocks. Di8DetourLock's dtor runs on any unwind path including C++
    // exceptions. Note we used SRWLock+manual in round 1; round 2 migrated
    // to CRITICAL_SECTION for recursive safety against the IAT→inline chain
    // in iat_hook.cpp (see g_di8DetourCs comment).
    Di8DetourLock _l;
    DI8Log("DirectInput8Create: version=0x%04X", dwVersion);

    if (!g_trampolineCreate) {
        return E_FAIL;
    }

    // Call Dalaya's original DirectInput8Create (which calls system32 internally)
    HRESULT hr = g_trampolineCreate(hinst, dwVersion, riidltf, ppvOut, punkOuter);
    if (FAILED(hr)) {
        DI8Log("DirectInput8Create: real call failed (0x%08X)", (unsigned)hr);
        return hr;
    }

    // Wrap the returned IDirectInput8 in our proxy.
    // DI8Proxy intercepts CreateDevice to wrap keyboards in DeviceProxy.
    *ppvOut = new DI8Proxy(*ppvOut);
    DI8Log("DirectInput8Create: wrapped in DI8Proxy");
    return hr;
}

// ─── Init Thread ────────────────────────────────────────────────
// Runs outside the loader lock (spawned from DllMain via CreateThread).
// Polls for dinput8.dll to appear, then hooks DirectInput8Create.

static DWORD WINAPI InitThread(LPVOID) {
    DI8Log("Init thread started — waiting for dinput8.dll");

    // Poll for Dalaya's dinput8.dll to be loaded by the Windows loader.
    // Our thread starts before ResumeThread (process is suspended).
    // GetModuleHandle returns NULL while dinput8.dll isn't loaded yet, so
    // we poll with a short sleep. Once the main thread is resumed and the
    // loader processes imports, GetModuleHandle will succeed and we can
    // hook DirectInput8Create before EQ's WinMain calls it.
    HMODULE hDinput8 = nullptr;
    const DWORD startTick = GetTickCount();
    const DWORD timeoutMs = 30000;

    while (!hDinput8) {
        hDinput8 = GetModuleHandleA("dinput8.dll");
        if (hDinput8) break;

        if (GetTickCount() - startTick > timeoutMs) {
            DI8Log("FATAL: dinput8.dll not loaded after %lu ms — aborting", timeoutMs);
            InterlockedExchange(&g_initialized, 1);
            return 1;
        }

        // Wait 10ms or until stop event is signaled (DLL_PROCESS_DETACH).
        // This replaces Sleep(10) so the thread exits cooperatively on unload
        // instead of continuing to run after the DLL is unmapped.
        if (WaitForSingleObject(g_stopEvent, 10) == WAIT_OBJECT_0) {
            DI8Log("Init thread: stop requested during poll, exiting cleanly");
            InterlockedExchange(&g_initialized, 1);
            return 1;
        }
    }

    DI8Log("GetModuleHandle succeeded: dinput8.dll at 0x%p", hDinput8);

    // Resolve DirectInput8Create from Dalaya's dinput8.dll
    auto realCreate = (PFN_DirectInput8Create)
        GetProcAddress(hDinput8, "DirectInput8Create");
    if (!realCreate) {
        DI8Log("FATAL: DirectInput8Create not found in dinput8.dll");
        InterlockedExchange(&g_initialized, 1);
        return 1;
    }
    DI8Log("Resolved DirectInput8Create at 0x%p", realCreate);

    // Install MinHook detour on DirectInput8Create
    MH_STATUS mh = MH_Initialize();
    if (mh != MH_OK) {
        DI8Log("FATAL: MH_Initialize failed: %d", mh);
        InterlockedExchange(&g_initialized, 1);
        return 1;
    }

    mh = MH_CreateHook((LPVOID)realCreate, (LPVOID)HookedDirectInput8Create,
                        (LPVOID *)&g_trampolineCreate);
    if (mh != MH_OK) {
        DI8Log("FATAL: MH_CreateHook failed: %d", mh);
        MH_Uninitialize();
        InterlockedExchange(&g_initialized, 1);
        return 1;
    }

    mh = MH_EnableHook((LPVOID)realCreate);
    if (mh != MH_OK) {
        DI8Log("FATAL: MH_EnableHook failed: %d", mh);
        MH_Uninitialize();
        InterlockedExchange(&g_initialized, 1);
        return 1;
    }

    g_hookInstalled = true;
    DI8Log("MinHook detour installed on DirectInput8Create");

    // Install IAT hooks (keyboard state + window focus spoofing)
    IatHook::InstallKeyboardHooks();
    DI8Log("IAT keyboard/focus hooks installed");

    // Install Winsock diagnostic hooks
    NetDebug::Install();
    DI8Log("Winsock diagnostic hooks installed");

    // MQ2 bridge init deferred to Poll() — MQ2 needs time to resolve its own
    // pointers after dinput8.dll loads, and a fixed Sleep() is unreliable.
    // MQ2BridgePollTick() will lazy-init on each poll cycle.
    DI8Log("MQ2 bridge init deferred to poll cycle (lazy retry)");

    // Step 1 of the MQ2 port: race-free eqmain detection via NT loader.
    // Registered HERE (after all eqgame-side hooks are live) so the first
    // eqmain load event — which may fire within milliseconds — lands on
    // a fully-initialized DLL state.
    RegisterDllNotification();

    InterlockedExchange(&g_initialized, 1);
    DI8Log("Init complete — all hooks active");
    return 0;
}

// ─── Cleanup ────────────────────────────────────────────────────

static void Cleanup() {
    DI8Log("Cleanup: removing hooks");

    // Unregister DLL notification FIRST so no late load/unload events
    // arrive during teardown of downstream state. This call doesn't depend
    // on the detour lock — Ldr notification callbacks don't go through any
    // of our hooked functions.
    UnregisterDllNotification();

    // v3.22.44 r2 (T3-Opus HIGH #1, #3): Enter the detour CRITICAL_SECTION
    // before flipping ANY hook off. EnterCriticalSection blocks until every
    // in-flight holder Leaves, which means every EQ thread currently inside
    // HookedGetAsyncKeyState / Hooked{Get,Inline}{Foreground,Focus,Active}Window
    // / HookedDirectInput8Create has returned. CRITICAL_SECTION supports
    // recursive same-thread Enter — required because iat_hook.cpp's
    // IAT-replaced GetForegroundWindow calls into user32 whose prologue is
    // inline-patched to InlineHookedGetForegroundWindow which Enters AGAIN
    // on the same thread; round-1's SRWLOCK shared would deadlock that chain.
    // Mirrors MacroQuest's CAutoLock(&gDetourCS) at MQ2DetourAPI.cpp:144-200.
    //
    // CS is NOT Left — once we're past the un-hook calls below no further
    // detour body entry is possible (their code pages are about to be
    // unmapped). The CRITICAL_SECTION struct goes away with the DLL.
    //
    // Documented limitation (round-2 deliberate, not blocking): GiveTime_Detour
    // in login_givetime_detour.cpp does NOT Enter g_di8DetourCs. Its own
    // g_detachInProgress flag covers the main shutdown path; the detour only
    // fires while eqmain.dll is loaded (login/charselect phases), and EnterCS
    // on a hot-path detour at 50–130 Hz is a measurable perf cost worth
    // measuring before adding. Follow-up planned.
    if (g_di8DetourCsInitialized) EnterCriticalSection(&g_di8DetourCs);

    NetDebug::Remove();
    IatHook::RemoveKeyboardHooks();
    DeviceProxy_Shutdown();

    // v7 Phase 2: remove GiveTime detour BEFORE MH_Uninitialize so MinHook's
    // global shutdown doesn't race with our hook's trampoline being called.
    GiveTimeDetour::Uninstall();

    if (g_hookInstalled) {
        MH_DisableHook(MH_ALL_HOOKS);
        MH_Uninitialize();
        g_hookInstalled = false;
    }

    MQ2Bridge::Shutdown();
    CloseCharSelShm();
    CloseLoginShm();

    DI8Log("Cleanup complete");
}

// ─── DLL Entry Point ────────────────────────────────────────────

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    switch (reason) {
    case DLL_PROCESS_ATTACH: {
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);

        // v3.22.44 r2 — initialize detour CRITICAL_SECTION before InitThread
        // spawns. Detours can only fire after MH_EnableHook completes inside
        // InitThread, so the CS is guaranteed initialized before any Enter.
        // Spin count 4000 matches MQ2's gDetourCS — favors short uncontended
        // acquires without crossing into the kernel.
        InitializeCriticalSectionAndSpinCount(&g_di8DetourCs, 4000);
        InterlockedExchange(&g_di8DetourCsInitialized, 1);

        // Build per-PID log path next to eqgame.exe (not next to our DLL —
        // we're alongside EQSwitch.exe, not in the game folder). Per-PID
        // naming is REQUIRED for multi-client (team) launches: the prior
        // shared `eqswitch-dinput8.log` was truncate-on-open, so two
        // concurrent eqgame.exe processes would clobber each other's logs
        // and lose any diagnostics from whichever client started later.
        // Each process now gets its own `eqswitch-dinput8-{PID}.log`,
        // letting 2-4 clients run fully independent with separate logs
        // we can grep without interleaving.
        DWORD pid = GetCurrentProcessId();
        if (GetModuleFileNameA(nullptr, g_logPath, MAX_PATH)) {
            char *lastSlash = strrchr(g_logPath, '\\');
            if (lastSlash) {
                size_t prefixLen = (size_t)(lastSlash + 1 - g_logPath);
                if (prefixLen + 32 < MAX_PATH) {
                    snprintf(lastSlash + 1, MAX_PATH - prefixLen,
                             "eqswitch-dinput8-%lu.log", pid);
                } else {
                    snprintf(g_logPath, MAX_PATH, "eqswitch-dinput8-%lu.log", pid);
                }
            } else {
                snprintf(g_logPath, MAX_PATH, "eqswitch-dinput8-%lu.log", pid);
            }
        } else {
            snprintf(g_logPath, MAX_PATH, "eqswitch-dinput8-%lu.log", pid);
        }

        // Create stop event BEFORE init thread — manual-reset, initially non-signaled.
        // The init thread checks this during its poll loop so it can exit cooperatively
        // on DLL_PROCESS_DETACH instead of continuing after the DLL is unmapped.
        g_stopEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);

        // Spawn init thread — defers all work outside the loader lock.
        // CreateThread in DLL_PROCESS_ATTACH is safe; the new thread blocks
        // on the loader lock until DllMain returns.
        g_initThread = CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr);
        break;
    }

    case DLL_PROCESS_DETACH:
        // reserved != NULL → process exiting: OS reclaims everything
        // reserved == NULL → FreeLibrary: wait for init, then clean up
        if (reserved == nullptr) {
            // Signal init thread to stop, then wait for it to exit
            if (g_stopEvent) SetEvent(g_stopEvent);
            if (g_initThread) {
                WaitForSingleObject(g_initThread, 3000);
                CloseHandle(g_initThread);
                g_initThread = nullptr;
            }
            if (g_stopEvent) { CloseHandle(g_stopEvent); g_stopEvent = nullptr; }
            if (g_initialized)
                Cleanup();
        }
        // Close log — null first so racing threads see nullptr
        FILE *lf = g_logFile;
        g_logFile = nullptr;
        if (lf) fclose(lf);
        break;
    }
    return TRUE;
}
