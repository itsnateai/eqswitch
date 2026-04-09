#pragma once
// pattern_scan.h — Runtime binary scanner for EQ's internal activation flag.
// Scans eqgame.exe's .text section for the WM_ACTIVATEAPP handler to locate
// the g_bActive global that controls whether EQ processes input.

#include <stdint.h>

namespace PatternScan {
    // Scan eqgame.exe for the internal activation flag (g_bActive).
    // Returns pointer to the DWORD flag, or nullptr if not found.
    // Thread-safe: caches result after first successful scan.
    uint32_t *FindActivationFlag();
}
