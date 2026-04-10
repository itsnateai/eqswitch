// Native/mq2_bridge.h — MQ2 bridge for character select via Dalaya's dinput8.dll exports
#pragma once
#include <windows.h>
#include <stdint.h>

// Shared memory name: "Local\EQSwitchCharSel_{PID}"
// C# creates it (like KeyInputWriter pattern), DLL reads/writes.

#define CHARSEL_SHM_MAGIC 0x45534353  // "ESCS"
#define CHARSEL_MAX_CHARS 8
#define CHARSEL_NAME_LEN  64

#pragma pack(push, 1)
struct CharSelectShm {
    uint32_t magic;            // CHARSEL_SHM_MAGIC
    uint32_t version;          // 1
    int32_t  gameState;        // Current EQ game state (-1=pre, 1=charsel, 5=ingame)
    int32_t  charCount;        // Number of characters found (0 if not at char select)
    int32_t  selectedIndex;    // Currently selected index in list (-1 = none)
    uint32_t mq2Available;     // 1 = MQ2 exports resolved, 0 = not found

    // C# → DLL: request character selection
    int32_t  requestedIndex;   // Set by C# to index to select (-1 = no request)
    uint32_t requestSeq;       // Incremented by C# on each new request
    uint32_t ackSeq;           // Set by DLL when request is processed

    // Character data (DLL writes, C# reads)
    char     names[CHARSEL_MAX_CHARS][CHARSEL_NAME_LEN];
    int32_t  levels[CHARSEL_MAX_CHARS];
    int32_t  classes[CHARSEL_MAX_CHARS];
};
#pragma pack(pop)
// Total struct size: 4+4+4+4+4+4 + 4+4+4 + (8*64)+(8*4)+(8*4) = 36 + 512 + 32 + 32 = 612 bytes

namespace MQ2Bridge {
    // Call once from DLL init thread (after dinput8.dll is loaded).
    // Resolves MQ2 exports. Returns true if exports found.
    bool Init();

    // Call periodically (e.g., every 500ms from the existing SHM poll thread).
    // Reads game state, populates char list when at char select,
    // processes selection requests from C#.
    // shm = pointer to the mapped CharSelectShm (created by C# CharSelectReader).
    void Poll(volatile CharSelectShm* shm);

    // Cleanup. Call from DLL_PROCESS_DETACH.
    void Shutdown();
}
