// login_givetime_detour.h — MinHook detour on Dalaya's LoginController::GiveTime
//
// v7 goal: run EQSwitch's autologin pulse inside EQ's game loop instead of on
// the Windows message pump (which v6's SetTimer/WM_TIMER approach uses). EQ
// calls LoginController::GiveTime ~60 times/sec during login/server-select/
// charselect. Detouring it gives us a pulse that's immune to message-pump
// pressure, eliminating the IsHungAppWindow / Event 1002 class of crashes.
//
// Resolution: Dalaya's eqmain.dll is ASLR-relocated. GiveTime's RVA (0x128B0)
// is stable across boots — multiply against the per-process eqmain base.
//
// Phase 2 (this commit): minimal detour that logs tick counts to verify the
// detour fires at EQ's frame rate. No behavior change yet.
// Phase 3: move MQ2BridgePollTick() onto the detour thread, remove SetTimer.

#pragma once

namespace GiveTimeDetour {

// Try to install the detour. Safe to call repeatedly — returns immediately if
// already installed. Returns false if eqmain.dll isn't loaded yet (caller
// should retry next tick).
bool PollAndInstall();

// Uninstall the detour. Called from eqswitch-di8.cpp's Cleanup() on
// DLL_PROCESS_DETACH. Safe to call if never installed.
void Uninstall();

// For diagnostics: returns the number of times the detour has fired.
unsigned GetTickCount();

// For diagnostics: returns true if MinHook reports the detour is installed.
bool IsInstalled();

} // namespace GiveTimeDetour
