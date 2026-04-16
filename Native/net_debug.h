// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

#pragma once
// EQSwitch dinput8 proxy — Winsock network debug hooks
// Hooks send/recv/closesocket from WS2_32.dll to log network
// activity around disconnects. Diagnostic-only, no behavior changes.

namespace NetDebug {
    // Install Winsock IAT hooks on eqgame.exe.
    void Install();

    // Remove all Winsock hooks and flush the ring buffer summary.
    void Remove();
}
