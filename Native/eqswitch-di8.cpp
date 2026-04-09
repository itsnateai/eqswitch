// eqswitch-di8.dll — Injected into eqgame.exe via CreateRemoteThread
//
// Hooks DirectInput8Create via MinHook detour to wrap COM objects for
// background keyboard input. Injected into a CREATE_SUSPENDED process
// before the main thread starts, so the hook is in place before EQ
// calls DirectInput8Create.
//
// Load chain: eqgame.exe → Dalaya's dinput8.dll (untouched) → system32
//             ↑ our detour wraps the result in DI8Proxy/DeviceProxy
//
// Also installs:
//   - IAT hooks for GetAsyncKeyState/GetForegroundWindow etc. (iat_hook.cpp)
//   - Winsock hooks for disconnect diagnostics (net_debug.cpp)

#define _CRT_SECURE_NO_WARNINGS
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <dinput.h>
#include <stdio.h>
#include <stdarg.h>
#include "MinHook.h"
#include "di8_proxy.h"
#include "iat_hook.h"
#include "net_debug.h"

extern "C" void DeviceProxy_Shutdown();

// ─── Globals ────────────────────────────────────────────────────

typedef HRESULT(WINAPI *PFN_DirectInput8Create)(
    HINSTANCE, DWORD, REFIID, LPVOID *, LPUNKNOWN);

static PFN_DirectInput8Create g_trampolineCreate = nullptr;  // MinHook trampoline → Dalaya's original
static HMODULE g_hModule = nullptr;
static HANDLE g_initThread = nullptr;
static volatile LONG g_initialized = 0;
static bool g_hookInstalled = false;

// ─── Logging ────────────────────────────────────────────────────

static FILE *g_logFile = nullptr;
static bool g_logInitAttempted = false;
static char g_logPath[MAX_PATH] = {};

static void EnsureLogOpen() {
    if (g_logFile || g_logInitAttempted) return;
    g_logInitAttempted = true;
    if (g_logPath[0])
        g_logFile = fopen(g_logPath, "w");
}

void DI8Log(const char *fmt, ...) {
    EnsureLogOpen();
    FILE *f = g_logFile;
    if (!f) return;
    fprintf(f, "[%lu] ", GetTickCount());
    va_list args;
    va_start(args, fmt);
    vfprintf(f, fmt, args);
    va_end(args);
    fprintf(f, "\n");
    fflush(f);
}

// ─── DirectInput8Create Detour ──────────────────────────────────

static HRESULT WINAPI HookedDirectInput8Create(
    HINSTANCE hinst, DWORD dwVersion, REFIID riidltf,
    LPVOID *ppvOut, LPUNKNOWN punkOuter)
{
    DI8Log("DirectInput8Create: version=0x%04X", dwVersion);

    if (!g_trampolineCreate) return E_FAIL;

    // Call Dalaya's original DirectInput8Create (which calls system32 internally)
    HRESULT hr = g_trampolineCreate(hinst, dwVersion, riidltf, ppvOut, punkOuter);
    if (FAILED(hr)) {
        DI8Log("DirectInput8Create: real call failed (0x%08X)", (unsigned)hr);
        return hr;
    }

    // Wrap the returned IDirectInput8 in our proxy.
    // DI8Proxy intercepts CreateDevice to wrap keyboards in DeviceProxy.
    *ppvOut = new DI8Proxy(*ppvOut);
    DI8Log("DirectInput8Create: wrapped in DI8Proxy");
    return hr;
}

// ─── Init Thread ────────────────────────────────────────────────
// Runs outside the loader lock (spawned from DllMain via CreateThread).
// Polls for dinput8.dll to appear, then hooks DirectInput8Create.

static DWORD WINAPI InitThread(LPVOID) {
    DI8Log("Init thread started — waiting for dinput8.dll");

    // Poll for Dalaya's dinput8.dll to be loaded by the Windows loader.
    // Our thread starts before ResumeThread (process is suspended), so
    // GetModuleHandle will block on the loader lock until all imports are
    // processed. Once it returns non-NULL, dinput8.dll is loaded and we
    // can hook DirectInput8Create before EQ's WinMain calls it.
    HMODULE hDinput8 = nullptr;
    const DWORD startTick = GetTickCount();
    const DWORD timeoutMs = 30000;

    while (!hDinput8) {
        hDinput8 = GetModuleHandleA("dinput8.dll");
        if (hDinput8) break;

        if (GetTickCount() - startTick > timeoutMs) {
            DI8Log("FATAL: dinput8.dll not loaded after %lu ms — aborting", timeoutMs);
            InterlockedExchange(&g_initialized, 1);
            return 1;
        }
        Sleep(10);
    }

    DI8Log("GetModuleHandle succeeded: dinput8.dll at 0x%p", hDinput8);

    // Resolve DirectInput8Create from Dalaya's dinput8.dll
    auto realCreate = (PFN_DirectInput8Create)
        GetProcAddress(hDinput8, "DirectInput8Create");
    if (!realCreate) {
        DI8Log("FATAL: DirectInput8Create not found in dinput8.dll");
        InterlockedExchange(&g_initialized, 1);
        return 1;
    }
    DI8Log("Resolved DirectInput8Create at 0x%p", realCreate);

    // Install MinHook detour on DirectInput8Create
    MH_STATUS mh = MH_Initialize();
    if (mh != MH_OK) {
        DI8Log("FATAL: MH_Initialize failed: %d", mh);
        InterlockedExchange(&g_initialized, 1);
        return 1;
    }

    mh = MH_CreateHook((LPVOID)realCreate, (LPVOID)HookedDirectInput8Create,
                        (LPVOID *)&g_trampolineCreate);
    if (mh != MH_OK) {
        DI8Log("FATAL: MH_CreateHook failed: %d", mh);
        MH_Uninitialize();
        InterlockedExchange(&g_initialized, 1);
        return 1;
    }

    mh = MH_EnableHook((LPVOID)realCreate);
    if (mh != MH_OK) {
        DI8Log("FATAL: MH_EnableHook failed: %d", mh);
        MH_Uninitialize();
        InterlockedExchange(&g_initialized, 1);
        return 1;
    }

    g_hookInstalled = true;
    DI8Log("MinHook detour installed on DirectInput8Create");

    // Install IAT hooks (keyboard state + window focus spoofing)
    IatHook::InstallKeyboardHooks();
    DI8Log("IAT keyboard/focus hooks installed");

    // Install Winsock diagnostic hooks
    NetDebug::Install();
    DI8Log("Winsock diagnostic hooks installed");

    InterlockedExchange(&g_initialized, 1);
    DI8Log("Init complete — all hooks active");
    return 0;
}

// ─── Cleanup ────────────────────────────────────────────────────

static void Cleanup() {
    DI8Log("Cleanup: removing hooks");

    NetDebug::Remove();
    IatHook::RemoveKeyboardHooks();
    DeviceProxy_Shutdown();

    if (g_hookInstalled) {
        MH_DisableHook(MH_ALL_HOOKS);
        MH_Uninitialize();
        g_hookInstalled = false;
    }

    DI8Log("Cleanup complete");
}

// ─── DLL Entry Point ────────────────────────────────────────────

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);

        // Build log path next to eqgame.exe (not next to our DLL — we're
        // alongside EQSwitch.exe, not in the game folder).
        if (GetModuleFileNameA(nullptr, g_logPath, MAX_PATH)) {
            char *lastSlash = strrchr(g_logPath, '\\');
            if (lastSlash && (size_t)(lastSlash + 1 - g_logPath) + 21 < MAX_PATH)
                memcpy(lastSlash + 1, "eqswitch-dinput8.log", 21);
            else
                snprintf(g_logPath, MAX_PATH, "eqswitch-dinput8.log");
        } else {
            snprintf(g_logPath, MAX_PATH, "eqswitch-dinput8.log");
        }

        // Spawn init thread — defers all work outside the loader lock.
        // CreateThread in DLL_PROCESS_ATTACH is safe; the new thread blocks
        // on the loader lock until DllMain returns.
        g_initThread = CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr);
        break;

    case DLL_PROCESS_DETACH:
        // reserved != NULL → process exiting: OS reclaims everything
        // reserved == NULL → FreeLibrary: wait for init, then clean up
        if (reserved == nullptr) {
            if (g_initThread) {
                WaitForSingleObject(g_initThread, 3000);
                CloseHandle(g_initThread);
                g_initThread = nullptr;
            }
            if (g_initialized)
                Cleanup();
        }
        // Close log — null first so racing threads see nullptr
        FILE *lf = g_logFile;
        g_logFile = nullptr;
        if (lf) fclose(lf);
        break;
    }
    return TRUE;
}
