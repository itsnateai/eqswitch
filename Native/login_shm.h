// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

// login_shm.h -- Shared memory for in-process login state machine
//
// C# creates "Local\EQSwitchLogin_{PID}", writes credentials + command.
// DLL reads credentials, drives EQ's UI widgets, writes state feedback.
// Replaces PostMessage/WM_CHAR + focus-faking login entirely.

#pragma once
#include <windows.h>
#include <stdint.h>

#define LOGIN_SHM_MAGIC   0x45534C53  // "ESLS"
// Version 2 (2026-05-09): added autoLoginActive field at end. Native readers
// at version 1 still see all prior fields correctly (backward-compatible
// append). C# version-2 writers send a 1344-byte mapping; v1 native readers
// see the first 1340 bytes and ignore the trailing field.
#define LOGIN_SHM_VERSION 2

// C# -> DLL: what to do
enum LoginCommand : uint32_t {
    LOGIN_CMD_NONE   = 0,
    LOGIN_CMD_LOGIN  = 1,   // Start full login sequence
    LOGIN_CMD_CANCEL = 2,   // Abort in-progress login
};

// DLL -> C#: current login phase
enum LoginPhase : uint32_t {
    PHASE_IDLE                = 0,
    PHASE_WAIT_LOGIN_SCREEN   = 1,
    PHASE_TYPING_CREDENTIALS  = 2,
    PHASE_CLICKING_CONNECT    = 3,
    PHASE_WAIT_CONNECT_RESP   = 4,
    PHASE_SERVER_SELECT       = 5,
    PHASE_WAIT_SERVER_LOAD    = 6,
    PHASE_CHAR_SELECT         = 7,
    PHASE_ENTERING_WORLD      = 8,
    PHASE_COMPLETE            = 10,
    PHASE_ERROR               = 99,
};

#define LOGIN_MAX_CHARS     10
#define LOGIN_NAME_LEN      64
#define LOGIN_PASS_LEN      128
#define LOGIN_SERVER_LEN    64
#define LOGIN_CHAR_LEN      64
#define LOGIN_ERROR_LEN     256

#pragma pack(push, 1)
struct LoginShm {
    // Header
    uint32_t     magic;                         // LOGIN_SHM_MAGIC
    uint32_t     version;                       // LOGIN_SHM_VERSION

    // C# -> DLL: login request
    LoginCommand command;                       // Set by C#, cleared by DLL
    uint32_t     commandSeq;                    // Incremented by C# per request
    uint32_t     commandAck;                    // Set by DLL when picked up
    char         username[LOGIN_NAME_LEN];      // Plaintext username
    char         password[LOGIN_PASS_LEN];      // Plaintext (decrypted by C#)
    char         server[LOGIN_SERVER_LEN];      // Target server name
    char         character[LOGIN_CHAR_LEN];     // Target character name

    // DLL -> C#: state feedback
    LoginPhase   phase;                         // Current login phase
    int32_t      gameState;                     // Raw EQ gGameState
    char         errorMessage[LOGIN_ERROR_LEN]; // Set on PHASE_ERROR
    uint32_t     retryCount;                    // Retries attempted

    // DLL -> C#: character data (populated at char select)
    int32_t      charCount;
    int32_t      selectedIndex;
    char         charNames[LOGIN_MAX_CHARS][LOGIN_NAME_LEN];
    int32_t      charLevels[LOGIN_MAX_CHARS];
    int32_t      charClasses[LOGIN_MAX_CHARS];

    // Diagnostic: widget enumeration mode
    uint32_t     diagnosticMode;                // 1 = enumerate all widgets to log

    // C# -> DLL: AutoLoginManager active flag (v2 / 2026-05-09).
    // Set to 1 by C# AutoLoginManager from BURST 1 setup through cleanup;
    // cleared on every exit path (success/failure/exception) in the finally
    // block. Used by eqswitch-di8.cpp's pre-login kPromptWindows[] dismiss
    // machinery (v3.15.5) to STAND DOWN during autologin — the C#-driven
    // BURST flow owns keystroke injection, and concurrent native widget-
    // clicks at server-select / charselect-load can close the EQ process
    // (root cause of the 2026-05-09 team1 regression).
    //
    // volatile because C# writes from a different process via the shared
    // mapping; volatile prevents the compiler from caching the read across
    // the per-tick poll loop.
    //
    // 0 = bare launch (kPromptWindows iteration runs as designed for EULA
    //     auto-dismiss). Default state — old configs / pre-autologin start
    //     /post-autologin cleanup all read 0 here.
    // 1 = autologin in progress (kPromptWindows iteration is suppressed for
    //     this PID). C# clears in the RunLoginSequence finally block.
    volatile uint32_t autoLoginActive;
};
#pragma pack(pop)
