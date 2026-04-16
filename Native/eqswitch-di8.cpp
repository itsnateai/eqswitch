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
#include "mq2_bridge.h"
#include "login_givetime_detour.h"
#include "login_state_machine.h"

extern "C" void DeviceProxy_Shutdown();

// ─── CharSelect Shared Memory ──────────────────────────────────
// Opened lazily — created by C# CharSelectReader, DLL reads/writes.

static HANDLE g_charSelMap = nullptr;
static volatile CharSelectShm* g_charSelShm = nullptr;
static uint32_t g_charSelRetry = 0;

// ─── Login Shared Memory ──────────────────────────────────────
// Opened lazily — created by C# AutoLoginManager, DLL reads/writes.
static HANDLE g_loginShmMap = nullptr;
static volatile LoginShm* g_loginShm = nullptr;
static uint32_t g_loginShmRetry = 0;

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
    DI8Log("login_shm: opened (magic=0x%08X, version=%u)", g_loginShm->magic, g_loginShm->version);
    return true;
}

static void CloseLoginShm() {
    if (g_loginShm) { UnmapViewOfFile((void*)g_loginShm); g_loginShm = nullptr; }
    if (g_loginShmMap) { CloseHandle(g_loginShmMap); g_loginShmMap = nullptr; }
}

// Called from device_proxy.cpp's ActivateThread (~60Hz, throttled to ~500ms internally)
static bool g_mq2Initialized = false;
static uint32_t g_mq2InitRetry = 0;

// Hotfix v4 (HIGH-D): RAII reentrancy guard for MQ2BridgePollTick. ActivateThread
// and TIMERPROC can race to satisfy the 500ms throttle simultaneously. Without this
// guard, BOTH threads could enter MQ2Bridge::Poll concurrently and fire XWM_LCLICK
// on CLW_EnterWorldButton twice — double enter-world. The existing "// double-fire
// is harmless" comment predates the SHM enter-world side-effect semantics.
struct PollReentryGuard {
    volatile LONG* flag;
    bool entered;
    PollReentryGuard(volatile LONG* f) : flag(f) {
        entered = (InterlockedCompareExchange(flag, 1, 0) == 0);
    }
    ~PollReentryGuard() { if (entered) InterlockedExchange(flag, 0); }
};

void MQ2BridgePollTick() {
    static volatile LONG s_pollInProgress = 0;
    PollReentryGuard guard(&s_pollInProgress);
    if (!guard.entered) return;  // another thread is already in Poll

    static volatile DWORD lastPoll = 0;  // accessed from ActivateThread + TIMERPROC
    DWORD now = GetTickCount();
    if (now - lastPoll < 500) return;
    lastPoll = now;

    // Lazy MQ2 bridge init — retries every ~5 seconds until MQ2 globals are ready.
    // Replaces the old Sleep(2000) one-shot that failed if MQ2 needed more time.
    if (!g_mq2Initialized) {
        if (g_mq2InitRetry == 0) {
            g_mq2Initialized = MQ2Bridge::Init();
            if (!g_mq2Initialized)
                g_mq2InitRetry = 10;  // Retry in ~5 seconds (10 × 500ms)
        } else {
            g_mq2InitRetry--;
        }
        if (!g_mq2Initialized) return;
    }

    if (!g_charSelShm) {
        if (g_charSelRetry == 0) {
            if (!TryOpenCharSelShm())
                g_charSelRetry = 10;  // Retry every ~5 seconds
        } else {
            g_charSelRetry--;
        }
    }
    if (g_charSelShm && g_charSelShm->magic == CHARSEL_SHM_MAGIC) {
        MQ2Bridge::Poll(g_charSelShm);
    }

    // v7 Phase 4: open LoginShm lazily and tick the login state machine.
    // LoginStateMachine drives the entire login flow (credentials → connect →
    // server select → charselect → enter world) via in-process MQ2 widget calls.
    if (!g_loginShm) {
        if (g_loginShmRetry == 0) {
            if (!TryOpenLoginShm())
                g_loginShmRetry = 10;  // Retry every ~5 seconds
        } else {
            g_loginShmRetry--;
        }
    }
    if (g_loginShm && g_loginShm->magic == LOGIN_SHM_MAGIC) {
        LoginStateMachine::Tick(g_loginShm, g_charSelShm);
    }
}

// ─── Globals ────────────────────────────────────────────────────

typedef HRESULT(WINAPI *PFN_DirectInput8Create)(
    HINSTANCE, DWORD, REFIID, LPVOID *, LPUNKNOWN);

static PFN_DirectInput8Create g_trampolineCreate = nullptr;  // MinHook trampoline → Dalaya's original
static HMODULE g_hModule = nullptr;
static HANDLE g_initThread = nullptr;
static HANDLE g_stopEvent = nullptr;   // signaled on DLL_PROCESS_DETACH to stop init thread cooperatively
static volatile LONG g_initialized = 0;
static bool g_hookInstalled = false;

// ─── Logging ────────────────────────────────────────────────────

static FILE *g_logFile = nullptr;
static volatile LONG g_logInitAttempted = 0;
static char g_logPath[MAX_PATH] = {};

static void EnsureLogOpen() {
    if (g_logFile) return;
    // Atomic CAS — only one thread opens the file
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
    // Our thread starts before ResumeThread (process is suspended).
    // GetModuleHandle returns NULL while dinput8.dll isn't loaded yet, so
    // we poll with a short sleep. Once the main thread is resumed and the
    // loader processes imports, GetModuleHandle will succeed and we can
    // hook DirectInput8Create before EQ's WinMain calls it.
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

        // Wait 10ms or until stop event is signaled (DLL_PROCESS_DETACH).
        // This replaces Sleep(10) so the thread exits cooperatively on unload
        // instead of continuing to run after the DLL is unmapped.
        if (WaitForSingleObject(g_stopEvent, 10) == WAIT_OBJECT_0) {
            DI8Log("Init thread: stop requested during poll, exiting cleanly");
            InterlockedExchange(&g_initialized, 1);
            return 1;
        }
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

    // MQ2 bridge init deferred to Poll() — MQ2 needs time to resolve its own
    // pointers after dinput8.dll loads, and a fixed Sleep() is unreliable.
    // MQ2BridgePollTick() will lazy-init on each poll cycle.
    DI8Log("MQ2 bridge init deferred to poll cycle (lazy retry)");

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

    // v7 Phase 2: remove GiveTime detour BEFORE MH_Uninitialize so MinHook's
    // global shutdown doesn't race with our hook's trampoline being called.
    GiveTimeDetour::Uninstall();

    if (g_hookInstalled) {
        MH_DisableHook(MH_ALL_HOOKS);
        MH_Uninitialize();
        g_hookInstalled = false;
    }

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

        // Create stop event BEFORE init thread — manual-reset, initially non-signaled.
        // The init thread checks this during its poll loop so it can exit cooperatively
        // on DLL_PROCESS_DETACH instead of continuing after the DLL is unmapped.
        g_stopEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);

        // Spawn init thread — defers all work outside the loader lock.
        // CreateThread in DLL_PROCESS_ATTACH is safe; the new thread blocks
        // on the loader lock until DllMain returns.
        g_initThread = CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr);
        break;

    case DLL_PROCESS_DETACH:
        // reserved != NULL → process exiting: OS reclaims everything
        // reserved == NULL → FreeLibrary: wait for init, then clean up
        if (reserved == nullptr) {
            // Signal init thread to stop, then wait for it to exit
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
        // Close log — null first so racing threads see nullptr
        FILE *lf = g_logFile;
        g_logFile = nullptr;
        if (lf) fclose(lf);
        break;
    }
    return TRUE;
}
