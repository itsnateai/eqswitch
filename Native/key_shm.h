// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

#pragma once
// EQSwitch dinput8 proxy — Shared memory reader for key state
// Reads EQSwitchDI8_{PID} written by C# KeyInputWriter.

#include <windows.h>
#include <stdint.h>

// Must match C# KeyInputWriter layout exactly
#pragma pack(push, 1)
struct SharedKeyState {
    uint32_t magic;     // 0x45534B53 ("ESKS" — EQSwitch Key State)
    uint32_t version;   // 1
    uint32_t active;    // 1 = inject keys, 0 = passthrough
    uint32_t suppress;  // 1 = zero physical keys before injecting
    uint32_t seq;       // Sequence counter for change detection
    uint8_t  keys[256]; // Scan code -> press state (0x00=up, 0x80=down)
};
#pragma pack(pop)
// Total: 276 bytes

#define KEY_SHM_MAGIC 0x45534B53

namespace KeyShm {
    // True if shared memory mapping exists (magic valid), regardless of active flag.
    // Use for early defense: suppress deactivation from the moment EQSwitch connects.
    bool IsOpen();
    // True if shared memory is open and active flag is set
    bool IsActive();
    // True if active + suppress flag is set (zero physical keys)
    bool ShouldSuppress();
    // True if the given scan code is pressed in shared memory
    bool IsKeyPressed(uint8_t scanCode);
    // OR synthetic key bits into a DirectInput state buffer. Returns true if any injected.
    bool InjectKeys(uint8_t *buf, uint32_t bufLen);
    // Copy full 256-byte key array. Returns true if active.
    bool ReadKeys(uint8_t out[256]);
    // Close shared memory handles. Call from DLL_PROCESS_DETACH cleanup.
    void Close();
}
