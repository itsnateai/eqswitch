#pragma once
// EQSwitch dinput8 proxy — IAT (Import Address Table) hooks
// Patches eqgame.exe's import table to intercept keyboard state
// and window focus queries, returning synthetic state from shared memory.

namespace IatHook {
    // Patch eqgame.exe's IAT for keyboard + window focus functions.
    // Call once from DllMain after shared memory is ready.
    void InstallKeyboardHooks();
}
