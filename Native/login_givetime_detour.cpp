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
// check at line 57 of the detour body).
volatile GiveTimeFn g_trampoline = nullptr;
void *g_targetAddr = nullptr;     // resolved eqmain base + RVA
bool g_installed = false;
bool g_installAttempted = false;  // once true, we stop retrying (either success or permanent failure)
volatile unsigned g_tickCount = 0;
volatile unsigned g_lastLoggedTick = 0;

// Set TRUE from Cleanup() on DLL_PROCESS_DETACH BEFORE touching MinHook state.
// PollAndInstall() checks this at entry and bails immediately, closing the
// TOCTOU gap between ActivateThread's install path and MH_Uninitialize.
volatile bool g_detachInProgress = false;

// The detour itself. ECX holds `this` (LoginController*) on entry per __thiscall;
// because we declare __fastcall, the compiler reads ECX into thisPtr.
void __fastcall GiveTime_Detour(void *thisPtr, void *edxBogus) {
    // Increment tick counter (not atomic — single game thread calls this)
    const unsigned tick = ++g_tickCount;

    // Log every 60 ticks (~once per second at 60 Hz) to prove the detour fires
    // without spamming the log at frame rate.
    if (tick - g_lastLoggedTick >= 60) {
        DI8Log("givetime_detour: tick %u, this=0x%p", tick, thisPtr);
        g_lastLoggedTick = tick;
    }

    // Call the original GiveTime via the trampoline. MUST happen on every call —
    // skipping it means EQ never processes keyboard/mouse and the client looks
    // hung.
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

} // namespace GiveTimeDetour
