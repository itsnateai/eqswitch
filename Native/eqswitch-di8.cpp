// eqswitch-di8.dll -- Injected into eqgame.exe via CreateRemoteThread
//
// Hooks DirectInput8Create via MinHook detour to wrap COM objects for
// cooperative level enforcement. Also hosts the MQ2 bridge for in-process
// login (SetWindowText on edit fields, WndNotification on buttons).
//
// Load chain: eqgame.exe -> Dalaya's dinput8.dll (untouched) -> system32
//             ^ our detour wraps the result in DI8Proxy/DeviceProxy

#define _CRT_SECURE_NO_WARNINGS
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <dinput.h>
#include <stdio.h>
#include <stdarg.h>
#include "MinHook.h"
#include "di8_proxy.h"
#include "net_debug.h"
#include "mq2_bridge.h"
#include "login_shm.h"
#include "login_state_machine.h"

extern "C" void DeviceProxy_Shutdown();

// ─── CharSelect Shared Memory ──────────────────────────────────
// Opened lazily -- created by C# CharSelectReader, DLL reads/writes.

static HANDLE g_charSelMap = nullptr;
static volatile CharSelectShm* g_charSelShm = nullptr;
static uint32_t g_charSelRetry = 0;

static bool TryOpenCharSelShm() {
    DWORD pid = GetCurrentProcessId();
    char name[64];
    snprintf(name, sizeof(name), "Local\\EQSwitchCharSel_%lu", pid);

    HANDLE h = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, name);
    if (!h) return false;

    void* view = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(CharSelectShm));
    if (!view) {
        CloseHandle(h);
        return false;
    }

    g_charSelMap = h;
    g_charSelShm = (volatile CharSelectShm*)view;
    DI8Log("mq2_bridge: opened CharSelect SHM (magic=0x%08X)", g_charSelShm->magic);
    return true;
}

static void CloseCharSelShm() {
    if (g_charSelShm) { UnmapViewOfFile((void*)g_charSelShm); g_charSelShm = nullptr; }
    if (g_charSelMap) { CloseHandle(g_charSelMap); g_charSelMap = nullptr; }
}

// ─── Login Shared Memory ───────────────────────────────────────
// Opened lazily -- created by C# LoginShmWriter, DLL reads/writes.

static HANDLE g_loginShmMap = nullptr;
static volatile LoginShm* g_loginShm = nullptr;
static uint32_t g_loginShmRetry = 0;

static bool TryOpenLoginShm() {
    DWORD pid = GetCurrentProcessId();
    char name[64];
    snprintf(name, sizeof(name), "Local\\EQSwitchLogin_%lu", pid);

    HANDLE h = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, name);
    if (!h) return false;

    void* view = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(LoginShm));
    if (!view) {
        CloseHandle(h);
        return false;
    }

    g_loginShmMap = h;
    g_loginShm = (volatile LoginShm*)view;
    DI8Log("login: opened Login SHM (magic=0x%08X)", g_loginShm->magic);
    return true;
}

static void CloseLoginShm() {
    if (g_loginShm) { UnmapViewOfFile((void*)g_loginShm); g_loginShm = nullptr; }
    if (g_loginShmMap) { CloseHandle(g_loginShmMap); g_loginShmMap = nullptr; }
}

// ─── Bridge Polling Thread ─────────────────────────────────────
// Replaces ActivateThread's polling responsibilities.
// Runs MQ2 bridge init, CharSelect polling, and LoginStateMachine.
// No focus-faking -- all UI manipulation is in-process.

static bool g_mq2Initialized = false;
static uint32_t g_mq2InitRetry = 0;
static volatile bool g_bridgeShutdown = false;
static HANDLE g_hBridgeThread = nullptr;

static DWORD WINAPI BridgePollingThread(LPVOID) {
    DI8Log("BridgePollingThread: started");

    while (!g_bridgeShutdown) {
        Sleep(500); // ~2Hz -- sufficient for UI state machine

        // Lazy MQ2 bridge init
        if (!g_mq2Initialized) {
            if (g_mq2InitRetry == 0) {
                g_mq2Initialized = MQ2Bridge::Init();
                if (!g_mq2Initialized)
                    g_mq2InitRetry = 10; // Retry in ~5 seconds
            } else {
                g_mq2InitRetry--;
            }
            if (!g_mq2Initialized) continue;
        }

        // CharSelect SHM polling (existing feature)
        if (!g_charSelShm) {
            if (g_charSelRetry == 0) {
                if (!TryOpenCharSelShm())
                    g_charSelRetry = 10;
            } else {
                g_charSelRetry--;
            }
        }
        if (g_charSelShm && g_charSelShm->magic == CHARSEL_SHM_MAGIC) {
            MQ2Bridge::Poll(g_charSelShm);
        }

        // Login SHM polling (new in-process login)
        if (!g_loginShm) {
            if (g_loginShmRetry == 0) {
                if (!TryOpenLoginShm())
                    g_loginShmRetry = 10;
            } else {
                g_loginShmRetry--;
            }
        }
        if (g_loginShm && g_loginShm->magic == LOGIN_SHM_MAGIC) {
            LoginStateMachine::Tick(g_loginShm, g_charSelShm);
        }
    }

    DI8Log("BridgePollingThread: stopped");
    return 0;
}

// Called from DeviceProxy when it captures the EQ HWND
// (SetCooperativeLevel or SetEventNotification).
// Legacy: was used to start ActivateThread. Now starts bridge thread.
void MQ2BridgePollTick() {
    // No-op -- polling is now handled by BridgePollingThread
    // This function exists to avoid linker errors from device_proxy.cpp
}

// ─── Globals ────────────────────────────────────────────────────

typedef HRESULT(WINAPI *PFN_DirectInput8Create)(
    HINSTANCE, DWORD, REFIID, LPVOID *, LPUNKNOWN);

static PFN_DirectInput8Create g_trampolineCreate = nullptr;
static HMODULE g_hModule = nullptr;
static HANDLE g_initThread = nullptr;
static HANDLE g_stopEvent = nullptr;
static volatile LONG g_initialized = 0;
static bool g_hookInstalled = false;

// ─── Logging ────────────────────────────────────────────────────

static FILE *g_logFile = nullptr;
static volatile LONG g_logInitAttempted = 0;
static char g_logPath[MAX_PATH] = {};

static void EnsureLogOpen() {
    if (g_logFile) return;
    if (InterlockedCompareExchange(&g_logInitAttempted, 1, 0) != 0) return;
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

    HRESULT hr = g_trampolineCreate(hinst, dwVersion, riidltf, ppvOut, punkOuter);
    if (FAILED(hr)) {
        DI8Log("DirectInput8Create: real call failed (0x%08X)", (unsigned)hr);
        return hr;
    }

    *ppvOut = new DI8Proxy(*ppvOut);
    DI8Log("DirectInput8Create: wrapped in DI8Proxy");
    return hr;
}

// ─── Init Thread ────────────────────────────────────────────────

static DWORD WINAPI InitThread(LPVOID) {
    DI8Log("Init thread started -- waiting for dinput8.dll");

    HMODULE hDinput8 = nullptr;
    const DWORD startTick = GetTickCount();
    const DWORD timeoutMs = 30000;

    while (!hDinput8) {
        hDinput8 = GetModuleHandleA("dinput8.dll");
        if (hDinput8) break;

        if (GetTickCount() - startTick > timeoutMs) {
            DI8Log("FATAL: dinput8.dll not loaded after %lu ms -- aborting", timeoutMs);
            InterlockedExchange(&g_initialized, 1);
            return 1;
        }

        if (WaitForSingleObject(g_stopEvent, 10) == WAIT_OBJECT_0) {
            DI8Log("Init thread: stop requested during poll, exiting cleanly");
            InterlockedExchange(&g_initialized, 1);
            return 1;
        }
    }

    DI8Log("GetModuleHandle succeeded: dinput8.dll at 0x%p", hDinput8);

    auto realCreate = (PFN_DirectInput8Create)
        GetProcAddress(hDinput8, "DirectInput8Create");
    if (!realCreate) {
        DI8Log("FATAL: DirectInput8Create not found in dinput8.dll");
        InterlockedExchange(&g_initialized, 1);
        return 1;
    }
    DI8Log("Resolved DirectInput8Create at 0x%p", realCreate);

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

    // Install Winsock diagnostic hooks
    NetDebug::Install();
    DI8Log("Winsock diagnostic hooks installed");

    // Start bridge polling thread (MQ2 init + login state machine)
    g_hBridgeThread = CreateThread(nullptr, 0, BridgePollingThread, nullptr, 0, nullptr);
    if (g_hBridgeThread)
        DI8Log("Bridge polling thread started");
    else
        DI8Log("WARNING: Failed to start bridge polling thread (%lu)", GetLastError());

    InterlockedExchange(&g_initialized, 1);
    DI8Log("Init complete -- hooks active, bridge thread running");
    return 0;
}

// ─── Cleanup ────────────────────────────────────────────────────

static void Cleanup() {
    DI8Log("Cleanup: removing hooks");

    // Stop bridge thread
    g_bridgeShutdown = true;
    if (g_hBridgeThread) {
        WaitForSingleObject(g_hBridgeThread, 2000);
        CloseHandle(g_hBridgeThread);
        g_hBridgeThread = nullptr;
    }

    NetDebug::Remove();
    DeviceProxy_Shutdown();

    if (g_hookInstalled) {
        MH_DisableHook(MH_ALL_HOOKS);
        MH_Uninitialize();
        g_hookInstalled = false;
    }

    LoginStateMachine::Shutdown();
    MQ2Bridge::Shutdown();
    CloseCharSelShm();
    CloseLoginShm();

    DI8Log("Cleanup complete");
}

// ─── DLL Entry Point ────────────────────────────────────────────

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);

        if (GetModuleFileNameA(nullptr, g_logPath, MAX_PATH)) {
            char *lastSlash = strrchr(g_logPath, '\\');
            if (lastSlash && (size_t)(lastSlash + 1 - g_logPath) + 21 < MAX_PATH)
                memcpy(lastSlash + 1, "eqswitch-dinput8.log", 21);
            else
                snprintf(g_logPath, MAX_PATH, "eqswitch-dinput8.log");
        } else {
            snprintf(g_logPath, MAX_PATH, "eqswitch-dinput8.log");
        }

        g_stopEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);

        g_initThread = CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr);
        break;

    case DLL_PROCESS_DETACH:
        if (reserved == nullptr) {
            if (g_stopEvent) SetEvent(g_stopEvent);
            if (g_initThread) {
                WaitForSingleObject(g_initThread, 3000);
                CloseHandle(g_initThread);
                g_initThread = nullptr;
            }
            if (g_stopEvent) { CloseHandle(g_stopEvent); g_stopEvent = nullptr; }
            if (g_initialized)
                Cleanup();
        }
        FILE *lf = g_logFile;
        g_logFile = nullptr;
        if (lf) fclose(lf);
        break;
    }
    return TRUE;
}
