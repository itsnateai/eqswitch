// iat_hook.cpp — IAT patching for keyboard state and window focus queries
// Hooks GetAsyncKeyState, GetKeyState, GetKeyboardState (return synthetic state)
// and GetForegroundWindow, GetFocus, GetActiveWindow (return EQ HWND when active).
//
// Only patches eqgame.exe's IAT — our DLL's imports are unaffected,
// so our own calls to these functions still get the real results.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include "iat_hook.h"
#include "key_shm.h"

void DI8Log(const char *fmt, ...);

// Defined in device_proxy.cpp
HWND GetEqHwnd();

// --- Original function pointers ---

typedef SHORT(WINAPI *PFN_GetAsyncKeyState)(int vKey);
typedef SHORT(WINAPI *PFN_GetKeyState)(int nVirtKey);
typedef BOOL(WINAPI *PFN_GetKeyboardState)(PBYTE lpKeyState);
typedef HWND(WINAPI *PFN_GetForegroundWindow)();
typedef HWND(WINAPI *PFN_GetFocus)();
typedef HWND(WINAPI *PFN_GetActiveWindow)();

static PFN_GetAsyncKeyState   g_realGetAsyncKeyState   = nullptr;
static PFN_GetKeyState        g_realGetKeyState        = nullptr;
static PFN_GetKeyboardState   g_realGetKeyboardState   = nullptr;
static PFN_GetForegroundWindow g_realGetForegroundWindow = nullptr;
static PFN_GetFocus           g_realGetFocus           = nullptr;
static PFN_GetActiveWindow    g_realGetActiveWindow    = nullptr;

// --- Hook implementations ---

// Diagnostic: log first N calls per active session to identify what EQ queries during login
static int g_diagLogCount = 0;
static bool g_diagWasActive = false;

static SHORT WINAPI HookedGetAsyncKeyState(int vKey) {
    bool active = KeyShm::IsActive();
    // Reset counter on active edge (new login session)
    if (active && !g_diagWasActive) {
        g_diagLogCount = 0;
        DI8Log("iat_hook: === LOGIN SESSION START — GetAsyncKeyState logging enabled ===");
    }
    g_diagWasActive = active;

    if (vKey >= 0 && vKey <= 255) {
        UINT scan = MapVirtualKeyW(vKey, MAPVK_VK_TO_VSC);
        if (scan > 0 && scan < 256 && KeyShm::IsKeyPressed((uint8_t)scan)) {
            if (active && g_diagLogCount < 200) {
                g_diagLogCount++;
                DI8Log("iat_hook: GetAsyncKeyState HIT vk=0x%02X scan=0x%02X → 0x8001", vKey, scan);
            }
            return (SHORT)0x8001;
        }
        // Log misses too (first 50 per session) to see what EQ polls
        if (active && g_diagLogCount < 50) {
            g_diagLogCount++;
            DI8Log("iat_hook: GetAsyncKeyState MISS vk=0x%02X scan=0x%02X", vKey, scan);
        }
    }
    return g_realGetAsyncKeyState ? g_realGetAsyncKeyState(vKey) : 0;
}

static SHORT WINAPI HookedGetKeyState(int nVirtKey) {
    if (nVirtKey >= 0 && nVirtKey <= 255) {
        UINT scan = MapVirtualKeyW(nVirtKey, MAPVK_VK_TO_VSC);
        if (scan > 0 && scan < 256 && KeyShm::IsKeyPressed((uint8_t)scan))
            return (SHORT)0x8001;
    }
    return g_realGetKeyState ? g_realGetKeyState(nVirtKey) : 0;
}

static BOOL WINAPI HookedGetKeyboardState(PBYTE lpKeyState) {
    BOOL ok = g_realGetKeyboardState ? g_realGetKeyboardState(lpKeyState) : FALSE;
    if (lpKeyState) {
        for (int vk = 0; vk <= 255; vk++) {
            UINT scan = MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
            if (scan > 0 && scan < 256 && KeyShm::IsKeyPressed((uint8_t)scan))
                lpKeyState[vk] |= 0x80;
        }
    }
    return ok;
}

static HWND WINAPI HookedGetForegroundWindow() {
    HWND hwnd = GetEqHwnd();
    if (hwnd && KeyShm::IsActive())
        return hwnd;
    return g_realGetForegroundWindow ? g_realGetForegroundWindow() : nullptr;
}

static HWND WINAPI HookedGetFocus() {
    HWND hwnd = GetEqHwnd();
    if (hwnd && KeyShm::IsActive())
        return hwnd;
    return g_realGetFocus ? g_realGetFocus() : nullptr;
}

static HWND WINAPI HookedGetActiveWindow() {
    HWND hwnd = GetEqHwnd();
    if (hwnd && KeyShm::IsActive())
        return hwnd;
    return g_realGetActiveWindow ? g_realGetActiveWindow() : nullptr;
}

// --- IAT patch tracking (for removal on detach) ---

struct IatPatchEntry { uint32_t *slot; uint32_t original; };
static IatPatchEntry g_patches[8];
static int g_patchCount = 0;

// --- IAT patching engine (x86 PE32 only) ---

// Patch a single IAT entry. Returns original function pointer, or nullptr on failure.
static void *PatchIat(const uint8_t *base, const char *targetDll,
                      const char *targetFn, void *newFn)
{
    // PE header navigation
    int32_t eLfanew = *(int32_t *)(base + 0x3C);
    const uint8_t *ntHeaders = base + eLfanew;
    const uint8_t *optHeader = ntHeaders + 24;

    uint16_t magic = *(uint16_t *)optHeader;
    if (magic != 0x010B) return nullptr; // PE32 only (eqgame.exe is x86)

    // Import directory is data directory entry #1
    // PE32: data directories start at optHeader + 96, each entry is 8 bytes
    uint32_t importDirRva = *(uint32_t *)(optHeader + 104);
    if (importDirRva == 0) return nullptr;

    // Walk IMAGE_IMPORT_DESCRIPTOR array (each is 20 bytes)
    const uint8_t *desc = base + importDirRva;
    while (true) {
        uint32_t nameRva = *(uint32_t *)(desc + 12);
        if (nameRva == 0) break;

        const char *dllName = (const char *)(base + nameRva);
        if (_stricmp(dllName, targetDll) == 0) {
            uint32_t origFirstThunk = *(uint32_t *)desc;        // OriginalFirstThunk
            uint32_t firstThunkRva  = *(uint32_t *)(desc + 16); // FirstThunk (IAT)

            // Bound imports have OriginalFirstThunk == 0 — no hint/name table to walk
            if (origFirstThunk == 0) { desc += 20; continue; }

            const uint32_t *orig = (const uint32_t *)(base + origFirstThunk);
            uint32_t *thunk = (uint32_t *)(base + firstThunkRva);

            while (*orig) {
                // Skip ordinal imports (high bit set)
                if ((*orig & 0x80000000) == 0) {
                    // Hint/Name table entry: 2-byte hint + null-terminated name
                    const char *fnName = (const char *)(base + *orig + 2);
                    if (strcmp(fnName, targetFn) == 0) {
                        void *original = (void *)(uintptr_t)*thunk;
                        DWORD oldProtect, dummy;
                        VirtualProtect(thunk, 4, PAGE_READWRITE, &oldProtect);
                        *thunk = (uint32_t)(uintptr_t)newFn;
                        VirtualProtect(thunk, 4, oldProtect, &dummy);
                        // Track for removal on DLL detach
                        if (g_patchCount < 8) {
                            g_patches[g_patchCount].slot = thunk;
                            g_patches[g_patchCount].original = (uint32_t)(uintptr_t)original;
                            g_patchCount++;
                        }
                        return original;
                    }
                }
                orig++;
                thunk++;
            }
        }
        desc += 20;
    }
    return nullptr;
}

// --- Public API ---

void IatHook::InstallKeyboardHooks() {
    HMODULE base = GetModuleHandleW(nullptr); // eqgame.exe
    if (!base) {
        DI8Log("iat_hook: GetModuleHandleW failed");
        return;
    }
    const uint8_t *basePtr = (const uint8_t *)base;

    int hooked = 0;

    // Keyboard state hooks (user32.dll)
    void *p;

    p = PatchIat(basePtr, "user32.dll", "GetAsyncKeyState", (void *)HookedGetAsyncKeyState);
    if (p) { g_realGetAsyncKeyState = (PFN_GetAsyncKeyState)p; hooked++; DI8Log("iat_hook: hooked GetAsyncKeyState"); }

    p = PatchIat(basePtr, "user32.dll", "GetKeyState", (void *)HookedGetKeyState);
    if (p) { g_realGetKeyState = (PFN_GetKeyState)p; hooked++; DI8Log("iat_hook: hooked GetKeyState"); }

    p = PatchIat(basePtr, "user32.dll", "GetKeyboardState", (void *)HookedGetKeyboardState);
    if (p) { g_realGetKeyboardState = (PFN_GetKeyboardState)p; hooked++; DI8Log("iat_hook: hooked GetKeyboardState"); }

    // Window focus hooks — try user32.dll, then apiset redirect
    p = PatchIat(basePtr, "user32.dll", "GetForegroundWindow", (void *)HookedGetForegroundWindow);
    if (!p) p = PatchIat(basePtr, "api-ms-win-ntuser-ia-l1-1-0.dll", "GetForegroundWindow", (void *)HookedGetForegroundWindow);
    if (p) { g_realGetForegroundWindow = (PFN_GetForegroundWindow)p; hooked++; DI8Log("iat_hook: hooked GetForegroundWindow"); }
    else DI8Log("iat_hook: FAILED GetForegroundWindow — background input may not work");

    p = PatchIat(basePtr, "user32.dll", "GetFocus", (void *)HookedGetFocus);
    if (p) { g_realGetFocus = (PFN_GetFocus)p; hooked++; DI8Log("iat_hook: hooked GetFocus"); }

    p = PatchIat(basePtr, "user32.dll", "GetActiveWindow", (void *)HookedGetActiveWindow);
    if (p) { g_realGetActiveWindow = (PFN_GetActiveWindow)p; hooked++; DI8Log("iat_hook: hooked GetActiveWindow"); }

    DI8Log("iat_hook: %d function(s) hooked", hooked);
}

void IatHook::RemoveKeyboardHooks() {
    for (int i = 0; i < g_patchCount; i++) {
        DWORD oldProtect, dummy;
        VirtualProtect(g_patches[i].slot, 4, PAGE_READWRITE, &oldProtect);
        *g_patches[i].slot = g_patches[i].original;
        VirtualProtect(g_patches[i].slot, 4, oldProtect, &dummy);
    }
    DI8Log("iat_hook: restored %d IAT entries", g_patchCount);
    g_patchCount = 0;
}
