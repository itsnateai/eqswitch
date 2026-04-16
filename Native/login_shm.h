// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// login_shm.h -- Shared memory for in-process login state machine
//
// C# creates "Local\EQSwitchLogin_{PID}", writes credentials + command.
// DLL reads credentials, drives EQ's UI widgets, writes state feedback.
// Replaces PostMessage/WM_CHAR + focus-faking login entirely.

#pragma once
#include <windows.h>
#include <stdint.h>

#define LOGIN_SHM_MAGIC   0x45534C53  // "ESLS"
#define LOGIN_SHM_VERSION 1

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
};
#pragma pack(pop)
