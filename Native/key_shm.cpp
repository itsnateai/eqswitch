// key_shm.cpp — Shared memory reader for key state injection
// Opens Local\EQSwitchDI8_{PID} (created by C# KeyInputWriter).
// All reads use volatile to see cross-process writes.

#include "key_shm.h"
#include <stdio.h>
#include <string.h>

void DI8Log(const char *fmt, ...);

static const SharedKeyState *g_shmPtr = nullptr;
static HANDLE g_shmHandle = nullptr;
// Countdown to avoid hammering OpenFileMapping at 60fps when shm doesn't exist yet
static uint32_t g_retryCountdown = 0;

static bool TryOpen() {
    DWORD pid = GetCurrentProcessId();
    char name[64];
    snprintf(name, sizeof(name), "Local\\EQSwitchDI8_%lu", pid);

    HANDLE h = OpenFileMappingA(FILE_MAP_READ, FALSE, name);
    if (!h) return false;

    void *view = MapViewOfFile(h, FILE_MAP_READ, 0, 0, sizeof(SharedKeyState));
    if (!view) {
        CloseHandle(h);
        return false;
    }

    g_shmHandle = h;
    g_shmPtr = (const SharedKeyState *)view;

    DI8Log("key_shm: opened %s magic=0x%08X",
           name, *(volatile uint32_t *)&g_shmPtr->magic);
    return true;
}

// Returns the shared state if open, magic valid, and active. Lazy-opens on first call.
static bool g_diagFirstActive = true;

static const SharedKeyState *GetState() {
    if (!g_shmPtr) {
        if (g_retryCountdown > 0) {
            g_retryCountdown--;
            return nullptr;
        }
        if (!TryOpen()) {
            g_retryCountdown = 4; // retry every ~4 calls
            return nullptr;
        }
    }

    uint32_t magic = *(volatile uint32_t *)&g_shmPtr->magic;
    uint32_t active = *(volatile uint32_t *)&g_shmPtr->active;
    if (magic != KEY_SHM_MAGIC || active == 0)
        return nullptr;

    // Log once when SHM first becomes active
    if (g_diagFirstActive) {
        g_diagFirstActive = false;
        uint32_t seq = *(volatile uint32_t *)&g_shmPtr->seq;
        uint32_t suppress = *(volatile uint32_t *)&g_shmPtr->suppress;
        int keyCount = 0;
        for (int i = 0; i < 256; i++)
            if (*(volatile uint8_t *)&g_shmPtr->keys[i]) keyCount++;
        DI8Log("key_shm: FIRST ACTIVE magic=0x%08X active=%u suppress=%u seq=%u keysDown=%d",
               magic, active, suppress, seq, keyCount);
    }

    return g_shmPtr;
}

bool KeyShm::IsActive() {
    if (!g_shmPtr) {
        GetState();
        if (!g_shmPtr) return false;
    }
    uint32_t magic = *(volatile uint32_t *)&g_shmPtr->magic;
    uint32_t active = *(volatile uint32_t *)&g_shmPtr->active;
    return magic == KEY_SHM_MAGIC && active != 0;
}

bool KeyShm::ShouldSuppress() {
    if (!g_shmPtr) return false;
    uint32_t magic = *(volatile uint32_t *)&g_shmPtr->magic;
    uint32_t active = *(volatile uint32_t *)&g_shmPtr->active;
    uint32_t suppress = *(volatile uint32_t *)&g_shmPtr->suppress;
    return magic == KEY_SHM_MAGIC && active != 0 && suppress != 0;
}

bool KeyShm::IsKeyPressed(uint8_t scanCode) {
    if (!g_shmPtr) {
        GetState();
        if (!g_shmPtr) return false;
    }
    uint32_t active = *(volatile uint32_t *)&g_shmPtr->active;
    if (active == 0) return false;
    return *(volatile uint8_t *)&g_shmPtr->keys[scanCode] != 0;
}

bool KeyShm::InjectKeys(uint8_t *buf, uint32_t bufLen) {
    const SharedKeyState *state = GetState();
    if (!state) return false;

    uint32_t len = bufLen < 256 ? bufLen : 256;
    bool injected = false;
    for (uint32_t i = 0; i < len; i++) {
        uint8_t k = *(volatile uint8_t *)&state->keys[i];
        if (k != 0) {
            buf[i] |= k;
            injected = true;
        }
    }
    return injected;
}

bool KeyShm::ReadKeys(uint8_t out[256]) {
    const SharedKeyState *state = GetState();
    if (!state) {
        memset(out, 0, 256);
        return false;
    }
    for (int i = 0; i < 256; i++)
        out[i] = *(volatile uint8_t *)&state->keys[i];
    return true;
}

void KeyShm::Close() {
    if (g_shmPtr) { UnmapViewOfFile((void *)g_shmPtr); g_shmPtr = nullptr; }
    if (g_shmHandle) { CloseHandle(g_shmHandle); g_shmHandle = nullptr; }
}
