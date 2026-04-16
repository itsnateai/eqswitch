// login_givetime_detour.cpp — MinHook detour on LoginController::GiveTime.
//
// See header for rationale. Offsets resolved in Phase 1
// (_.diagnostics/dalaya_offsets.md):
//
//   Dalaya eqmain.dll RVAs:
//     LoginController::GiveTime      0x128B0
//     pLoginController (ptr-to-ptr)  0x150174  (for Phase 4 — not used here)
//
// GiveTime's calling convention is __thiscall: `this` arrives in ECX with no
// stack args. For a method with zero extra args, __fastcall(ecx, edx) is
// binary-compatible — ECX holds `this`, EDX is a bogus fill from the fastcall
// convention that __thiscall ignores. Using __fastcall lets us write this in
// plain C++ without naked assembly.

#include "login_givetime_detour.h"

#include <windows.h>
#include "MinHook.h"

void DI8Log(const char *fmt, ...);

// Defined in eqswitch-di8.cpp. Safe to call at EQ frame rate — internal
// 500ms throttle + PollReentryGuard keep MQ2 work to ~2/sec.
void MQ2BridgePollTick();

namespace {

constexpr uintptr_t GIVETIME_RVA = 0x128B0;

// Matches MinHook's contract for a trampoline pointer — receives the original
// bytes we replaced, plus a jmp back to the rest of GiveTime. We call through
// it to invoke the unhooked behavior after our pre-work.
using GiveTimeFn = void(__fastcall *)(void *thisPtr, void *edxBogus);

// `g_trampoline` is written once from ActivateThread after MH_CreateHook, read
// from EQ's game thread inside GiveTime_Detour. Volatile prevents compiler
// reordering around the MH_EnableHook sync point (x86 TSO handles the store
// ordering in hardware, but volatile makes the intent explicit for the null
// check in the detour body).
volatile GiveTimeFn g_trampoline = nullptr;
void *g_targetAddr = nullptr;     // resolved eqmain base + RVA
bool g_installed = false;
bool g_installAttempted = false;  // once true, we stop retrying (either success or permanent failure)
volatile unsigned g_tickCount = 0;

// v7 Phase 4: cache the last-seen LoginController* so FindWindowByName can
// use GetChildItem on it instead of the broken eqmain CXWndManager scan.
// Written by game thread (detour), read by ActivateThread (FindWindowByName).
// Volatile: x86 TSO guarantees the reader sees the latest store-buffered value;
// a stale null on the first few reads is harmless (FindWindowByName falls through).
volatile void *g_loginController = nullptr;

// Set TRUE from Cleanup() on DLL_PROCESS_DETACH BEFORE touching MinHook state.
// PollAndInstall() checks this at entry and bails immediately, closing the
// TOCTOU gap between ActivateThread's install path and MH_Uninitialize.
volatile bool g_detachInProgress = false;

// The detour itself. ECX holds `this` (LoginController*) on entry per __thiscall;
// because we declare __fastcall, the compiler reads ECX into thisPtr.
//
// Runs on EQ's game thread ~50-130 Hz (50 during idle login, spikes during
// bad-password dialog when 3D frame-limiting is off). This is the permanent
// replacement for v6's SetTimer(1500ms, MQ2TimerProc) — instead of piggybacking
// on the Windows message pump (which hit IsHungAppWindow on slow loads), we
// run inside EQ's own game loop, so by construction any latency we add is
// latency EQ was already going to incur on its next frame.
void __fastcall GiveTime_Detour(void *thisPtr, void *edxBogus) {
    // Increment tick counter for diagnostics (GetTickCount() accessor).
    // Not atomic — only EQ's game thread writes, readers tolerate stale values.
    ++g_tickCount;

    // v7 Phase 4: stash the LoginController* so FindWindowByName can use it.
    // thisPtr changes identity across eqmain reloads (new login session after
    // returning to character select from in-game), so we update every frame.
    g_loginController = thisPtr;

    // MQ2 bridge poll — the whole reason this detour exists. Internal 500ms
    // throttle means we do real work only 1-2× per second even though we're
    // called 50-130× per second. PollReentryGuard inside MQ2BridgePollTick
    // prevents concurrent entry from ActivateThread's background fallback.
    MQ2BridgePollTick();

    // Call the original GiveTime via the trampoline. MUST happen on every call —
    // skipping it means EQ never processes keyboard/mouse and the client looks
    // hung. `edxBogus` is the garbage EDX value EQ's caller happened to have;
    // passing it through preserves register state as if we weren't here.
    if (g_trampoline) {
        g_trampoline(thisPtr, edxBogus);
    }
}

} // namespace

namespace GiveTimeDetour {

bool PollAndInstall() {
    // Fence against DLL_PROCESS_DETACH: Cleanup() sets g_detachInProgress
    // BEFORE calling MH_Uninitialize. Bail immediately so we don't touch
    // MinHook while it's being torn down.
    if (g_detachInProgress) return false;
    if (g_installed) return true;
    if (g_installAttempted) return false;  // permanent failure — stop retrying

    HMODULE hEqmain = GetModuleHandleA("eqmain.dll");
    if (!hEqmain) {
        // eqmain.dll not loaded yet. This is expected early in the process
        // lifecycle — eqmain only loads when the client reaches login screen.
        // Caller should retry on the next tick.
        return false;
    }

    // Once we enter this block we're committed — either we successfully install
    // or we record a permanent failure (bad offset, MinHook misbehaving). No
    // point retrying on every tick in the failure case.
    g_installAttempted = true;

    g_targetAddr = reinterpret_cast<void *>(
        reinterpret_cast<uintptr_t>(hEqmain) + GIVETIME_RVA);

    DI8Log("givetime_detour: eqmain.dll at 0x%p, targeting GiveTime @ 0x%p "
           "(RVA 0x%X)",
           hEqmain, g_targetAddr, (unsigned)GIVETIME_RVA);

    // Sanity-check the first byte of the target — we expect 0x56 (push esi)
    // matching MQ2's documented 32-bit ROF2 prologue. If this is wrong, the
    // detour would corrupt eqmain.dll and crash the client immediately.
    const unsigned char firstByte = *static_cast<const unsigned char *>(g_targetAddr);
    if (firstByte != 0x56) {
        DI8Log("givetime_detour: REFUSING install — expected 0x56 (push esi) "
               "at RVA 0x%X, got 0x%02X. Offset is wrong or eqmain was patched.",
               (unsigned)GIVETIME_RVA, firstByte);
        g_targetAddr = nullptr;
        return false;
    }

    // MinHook is already initialized by eqswitch-di8.cpp's InitThread (for
    // DirectInput8Create). CreateHook / EnableHook are safe to call with it
    // already initialized. Use a non-volatile local for MH_CreateHook's
    // trampoline out-parameter — assigning the final value to the volatile
    // global after EnableHook succeeds acts as a publish fence so the game
    // thread never observes g_trampoline != nullptr with a half-installed hook.
    GiveTimeFn localTrampoline = nullptr;
    MH_STATUS status = MH_CreateHook(
        g_targetAddr,
        reinterpret_cast<LPVOID>(&GiveTime_Detour),
        reinterpret_cast<LPVOID *>(&localTrampoline));
    if (status != MH_OK) {
        DI8Log("givetime_detour: MH_CreateHook failed: %d", (int)status);
        g_targetAddr = nullptr;
        return false;
    }

    status = MH_EnableHook(g_targetAddr);
    if (status != MH_OK) {
        DI8Log("givetime_detour: MH_EnableHook failed: %d", (int)status);
        MH_RemoveHook(g_targetAddr);
        g_targetAddr = nullptr;
        return false;
    }

    // Publish: trampoline before installed flag, so any future reader that
    // checks g_installed first sees a non-null trampoline.
    g_trampoline = localTrampoline;
    g_installed = true;
    DI8Log("givetime_detour: INSTALLED — trampoline at 0x%p", (void *)localTrampoline);
    return true;
}

void Uninstall() {
    // Close the TOCTOU gap: publish detach-in-progress BEFORE MinHook teardown.
    // ActivateThread's PollAndInstall() checks this at entry and bails.
    g_detachInProgress = true;

    if (!g_installed || !g_targetAddr) return;

    MH_STATUS status = MH_DisableHook(g_targetAddr);
    if (status != MH_OK) {
        DI8Log("givetime_detour: MH_DisableHook failed: %d", (int)status);
    }
    status = MH_RemoveHook(g_targetAddr);
    if (status != MH_OK) {
        DI8Log("givetime_detour: MH_RemoveHook failed: %d", (int)status);
    }

    DI8Log("givetime_detour: uninstalled (final tick count: %u)", g_tickCount);
    g_installed = false;
    g_targetAddr = nullptr;
    g_trampoline = nullptr;
}

unsigned GetTickCount() { return g_tickCount; }

bool IsInstalled() { return g_installed; }

void *GetLoginController() { return (void *)g_loginController; }

void ClearLoginController() { g_loginController = nullptr; }

void OnEqmainUnloaded() {
    // eqmain.dll unloaded — the detour's code page is gone.
    // Don't call MH_DisableHook/MH_RemoveHook — the target address is unmapped.
    // Just reset state so ActivateThread resumes background polling and
    // PollAndInstall can re-hook if eqmain reloads (camp → charselect → login).
    g_installed = false;
    g_installAttempted = false;
    g_targetAddr = nullptr;
    g_trampoline = nullptr;
    g_loginController = nullptr;
    g_tickCount = 0;
    DI8Log("givetime_detour: reset after eqmain unload — background poll resumed");
}

} // namespace GiveTimeDetour
