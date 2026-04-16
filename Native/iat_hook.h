// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

#pragma once
// EQSwitch dinput8 proxy — IAT (Import Address Table) hooks
// Patches eqgame.exe's import table to intercept keyboard state
// and window focus queries, returning synthetic state from shared memory.

namespace IatHook {
    // Patch eqgame.exe's IAT for keyboard + window focus functions.
    // Call once from DllMain after shared memory is ready.
    void InstallKeyboardHooks();

    // Restore all patched IAT entries to their original function pointers.
    // MUST be called from DLL_PROCESS_DETACH BEFORE the DLL is unmapped,
    // otherwise the IAT entries point into unmapped code and crash.
    void RemoveKeyboardHooks();
}
