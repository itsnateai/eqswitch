// dinput8-proxy.cpp — Proxy DLL entry point and DirectInput8Create wrapper
// Loads the real dinput8.dll from System32, wraps the returned interface.
// Phases 1-4: COM wrapping, background input, key injection, IAT hooks.

#define _CRT_SECURE_NO_WARNINGS
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <dinput.h>
#include <stdio.h>
#include <stdarg.h>
#include "di8_proxy.h"
#include "iat_hook.h"
#include "net_debug.h"

extern "C" void DeviceProxy_Shutdown();

typedef HRESULT(WINAPI *PFN_DirectInput8Create)(
    HINSTANCE, DWORD, REFIID, LPVOID *, LPUNKNOWN);

// Function pointer types for forwarded COM exports from Dalaya's DLL
typedef HRESULT(WINAPI *PFN_DllCanUnloadNow)();
typedef HRESULT(WINAPI *PFN_DllGetClassObject)(REFCLSID, REFIID, LPVOID *);
typedef HRESULT(WINAPI *PFN_DllRegisterServer)();
typedef HRESULT(WINAPI *PFN_DllUnregisterServer)();
typedef LPCDIDATAFORMAT(WINAPI *PFN_GetdfDIJoystick)();

static PFN_DirectInput8Create g_realCreate = nullptr;
static HMODULE g_realDll = nullptr;  // Dalaya's dinput8_dalaya.dll (or system32 fallback)
// Forwarded COM exports — resolved lazily from Dalaya's DLL
static PFN_DllCanUnloadNow g_pfnDllCanUnloadNow = nullptr;
static PFN_DllGetClassObject g_pfnDllGetClassObject = nullptr;
static PFN_DllRegisterServer g_pfnDllRegisterServer = nullptr;
static PFN_DllUnregisterServer g_pfnDllUnregisterServer = nullptr;
static PFN_GetdfDIJoystick g_pfnGetdfDIJoystick = nullptr;
static FILE *g_logFile = nullptr;
static bool g_logInitAttempted = false;
static INIT_ONCE g_initOnce = INIT_ONCE_STATIC_INIT;
static bool g_proxyInitDone = false;  // set after InitOnceExecuteOnce returns
// Log path built from hModule during DLL_PROCESS_ATTACH (no loader-lock concern)
static char g_logPath[MAX_PATH] = {};

// Message queue for DllMain — fopen is unsafe under loader lock,
// so we buffer messages and flush on first DirectInput8Create call.
static char g_earlyMessages[4][256];
static int g_earlyMsgCount = 0;

static void QueueEarlyMsg(const char *msg) {
    if (g_earlyMsgCount < 4) {
        strncpy(g_earlyMessages[g_earlyMsgCount], msg, 255);
        g_earlyMessages[g_earlyMsgCount][255] = '\0';
        g_earlyMsgCount++;
    }
}

static void FlushEarlyMessages() {
    for (int i = 0; i < g_earlyMsgCount; i++)
        DI8Log("%s", g_earlyMessages[i]);
    g_earlyMsgCount = 0;
}

// Open log file using the path prepared during DLL_PROCESS_ATTACH.
// Deferred from DllMain because fopen calls CreateFileA internally.
static void EnsureLogOpen() {
    if (g_logFile || g_logInitAttempted) return;
    g_logInitAttempted = true;
    if (g_logPath[0])
        g_logFile = fopen(g_logPath, "w");
}

void DI8Log(const char *fmt, ...) {
    EnsureLogOpen();
    FILE *f = g_logFile; // snapshot — detach may null g_logFile from another thread
    if (!f) return;
    fprintf(f, "[%lu] ", GetTickCount());
    va_list args;
    va_start(args, fmt);
    vfprintf(f, fmt, args);
    va_end(args);
    fprintf(f, "\n");
    fflush(f);
}

// ─── Lazy Init ──────────────────────────────────────────────────
// LoadLibraryA and fopen are unsafe under the loader lock (DllMain).
// Defer real DLL loading, IAT hooks, and logging to first DirectInput8Create call.
// Resolve the 5 COM exports from the loaded DLL (Dalaya's or system32 fallback).
// Non-fatal — these are only needed if something queries the COM registration.
static void ResolveForwardedExports() {
    if (!g_realDll) return;
    g_pfnDllCanUnloadNow = (PFN_DllCanUnloadNow)GetProcAddress(g_realDll, "DllCanUnloadNow");
    g_pfnDllGetClassObject = (PFN_DllGetClassObject)GetProcAddress(g_realDll, "DllGetClassObject");
    g_pfnDllRegisterServer = (PFN_DllRegisterServer)GetProcAddress(g_realDll, "DllRegisterServer");
    g_pfnDllUnregisterServer = (PFN_DllUnregisterServer)GetProcAddress(g_realDll, "DllUnregisterServer");
    g_pfnGetdfDIJoystick = (PFN_GetdfDIJoystick)GetProcAddress(g_realDll, "GetdfDIJoystick");
}

static bool InitProxy() {
    FlushEarlyMessages();

    // Build path to dinput8_dalaya.dll in the same directory as us (the game dir).
    // This is Dalaya's MQ2 core — it chain-loads system32\dinput8.dll internally.
    char ourDir[MAX_PATH];
    if (GetModuleFileNameA(nullptr, ourDir, MAX_PATH)) {
        char *lastSlash = strrchr(ourDir, '\\');
        if (lastSlash) *(lastSlash + 1) = '\0';
    } else {
        ourDir[0] = '\0';
    }

    char dalayaPath[MAX_PATH];
    snprintf(dalayaPath, MAX_PATH, "%sdinput8_dalaya.dll", ourDir);

    g_realDll = LoadLibraryA(dalayaPath);
    if (g_realDll) {
        DI8Log("Chain-load: loaded Dalaya MQ2 core from %s", dalayaPath);
    } else {
        // Fallback: load system32 dinput8.dll directly (no MQ2, but game may work)
        DI8Log("Chain-load: dinput8_dalaya.dll not found, falling back to system32");
        char sysDir[MAX_PATH];
        GetSystemDirectoryA(sysDir, MAX_PATH);
        char sysPath[MAX_PATH];
        snprintf(sysPath, MAX_PATH, "%s\\dinput8.dll", sysDir);
        g_realDll = LoadLibraryA(sysPath);
        if (!g_realDll) {
            DI8Log("FATAL: failed to load dinput8.dll from system32 either");
            return false;
        }
        DI8Log("Loaded system32 dinput8.dll from %s (no MQ2)", sysPath);
    }

    g_realCreate = (PFN_DirectInput8Create)
        GetProcAddress(g_realDll, "DirectInput8Create");
    if (!g_realCreate) {
        DI8Log("FATAL: failed to resolve DirectInput8Create");
        FreeLibrary(g_realDll);
        g_realDll = nullptr;
        return false;
    }
    DI8Log("Resolved DirectInput8Create from chain-loaded DLL");

    ResolveForwardedExports();

    IatHook::InstallKeyboardHooks();
    NetDebug::Install();
    DI8Log("Proxy initialized — chain-load complete");
    return true;
}

// INIT_ONCE callback — thread-safe even if DirectInput8Create is called concurrently.
// If InitProxy fails, INIT_ONCE will retry on the next call (returns FALSE).
// g_proxyInitDone is only set on success so DLL_PROCESS_DETACH doesn't clean up
// resources that were never created.
static BOOL CALLBACK InitProxyOnce(INIT_ONCE *, PVOID, PVOID *) {
    if (!InitProxy())
        return FALSE;
    g_proxyInitDone = true;
    return TRUE;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);

        // Build log path from hModule (safe — no loader lock re-entry).
        // Everything else deferred to InitProxy() on first DirectInput8Create.
        if (GetModuleFileNameA(hModule, g_logPath, MAX_PATH)) {
            char *lastSlash = strrchr(g_logPath, '\\');
            if (lastSlash && (size_t)(lastSlash + 1 - g_logPath) + 21 < MAX_PATH)
                memcpy(lastSlash + 1, "eqswitch-dinput8.log", 21);
            else
                snprintf(g_logPath, MAX_PATH, "eqswitch-dinput8.log");
        } else {
            snprintf(g_logPath, MAX_PATH, "eqswitch-dinput8.log");
        }

        QueueEarlyMsg("DllMain: DLL_PROCESS_ATTACH (init deferred)");
    }
    else if (reason == DLL_PROCESS_DETACH) {
        // Only clean up if init completed (reserved==NULL means FreeLibrary,
        // reserved!=NULL means process exit — OS handles cleanup).
        if (g_proxyInitDone && reserved == NULL) {
            DI8Log("PROCESS_DETACH: cleaning up");
            NetDebug::Remove();
            IatHook::RemoveKeyboardHooks();
            DeviceProxy_Shutdown();
            if (g_realDll) { FreeLibrary(g_realDll); g_realDll = nullptr; }
        }
        // Close log file — null before fclose so racing threads see nullptr
        FILE *lf = g_logFile;
        g_logFile = nullptr;
        if (lf) fclose(lf);
    }
    return TRUE;
}

// Exported function — replaces the real DirectInput8Create.
// EQ calls this via its import table; we forward to the real DLL
// and wrap the returned IDirectInput8 in our proxy.
// No __declspec(dllexport) needed — the .def file controls the export.
// dinput.h already declares this as an import; re-declaring with dllexport
// causes C2375. The .def file overrides linkage at link time.
extern "C" HRESULT WINAPI DirectInput8Create(
    HINSTANCE hinst, DWORD dwVersion, REFIID riidltf,
    LPVOID *ppvOut, LPUNKNOWN punkOuter)
{
    // Lazy init: load real DLL + install hooks on first call (outside loader lock).
    // INIT_ONCE guarantees thread-safe one-shot execution. If InitProxy fails,
    // INIT_ONCE will retry on next call (callback returned FALSE).
    InitOnceExecuteOnce(&g_initOnce, InitProxyOnce, nullptr, nullptr);

    DI8Log("DirectInput8Create: version=0x%04X", dwVersion);

    if (!g_realCreate) return E_FAIL;

    HRESULT hr = g_realCreate(hinst, dwVersion, riidltf, ppvOut, punkOuter);
    if (FAILED(hr)) {
        DI8Log("DirectInput8Create: real call failed (0x%08X)", (unsigned)hr);
        return hr;
    }

    // Wrap the real IDirectInput8 in our proxy.
    // The proxy forwards all calls; CreateDevice wraps devices in DeviceProxy.
    *ppvOut = new DI8Proxy(*ppvOut);
    DI8Log("DirectInput8Create: wrapped in DI8Proxy");
    return hr;
}

// ─── Forwarded COM exports ──────────────────────────────────────
// These 5 exports exist in Dalaya's dinput8.dll but were missing from
// our proxy. We forward them at runtime to the chain-loaded DLL.
// The .def file exposes them; they resolve here via GetProcAddress.

extern "C" HRESULT WINAPI ProxyDllCanUnloadNow() {
    InitOnceExecuteOnce(&g_initOnce, InitProxyOnce, nullptr, nullptr);
    return g_pfnDllCanUnloadNow ? g_pfnDllCanUnloadNow() : S_FALSE;
}

extern "C" HRESULT WINAPI ProxyDllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID *ppv) {
    InitOnceExecuteOnce(&g_initOnce, InitProxyOnce, nullptr, nullptr);
    return g_pfnDllGetClassObject ? g_pfnDllGetClassObject(rclsid, riid, ppv) : CLASS_E_CLASSNOTAVAILABLE;
}

extern "C" HRESULT WINAPI ProxyDllRegisterServer() {
    InitOnceExecuteOnce(&g_initOnce, InitProxyOnce, nullptr, nullptr);
    return g_pfnDllRegisterServer ? g_pfnDllRegisterServer() : E_FAIL;
}

extern "C" HRESULT WINAPI ProxyDllUnregisterServer() {
    InitOnceExecuteOnce(&g_initOnce, InitProxyOnce, nullptr, nullptr);
    return g_pfnDllUnregisterServer ? g_pfnDllUnregisterServer() : E_FAIL;
}

extern "C" LPCDIDATAFORMAT WINAPI ProxyGetdfDIJoystick() {
    InitOnceExecuteOnce(&g_initOnce, InitProxyOnce, nullptr, nullptr);
    return g_pfnGetdfDIJoystick ? g_pfnGetdfDIJoystick() : nullptr;
}
