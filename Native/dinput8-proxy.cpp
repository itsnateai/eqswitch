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

extern "C" void DeviceProxy_Shutdown();

typedef HRESULT(WINAPI *PFN_DirectInput8Create)(
    HINSTANCE, DWORD, REFIID, LPVOID *, LPUNKNOWN);

static PFN_DirectInput8Create g_realCreate = nullptr;
static HMODULE g_realDll = nullptr;
static FILE *g_logFile = nullptr;
static bool g_logInitAttempted = false;
// Log path built from hModule during DLL_PROCESS_ATTACH (no loader-lock concern)
static char g_logPath[MAX_PATH] = {};

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

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);

        // Build log path from hModule (safe in DllMain — no loader lock re-entry).
        // Actual fopen is deferred to first DI8Log call.
        if (GetModuleFileNameA(hModule, g_logPath, MAX_PATH)) {
            char *lastSlash = strrchr(g_logPath, '\\');
            if (lastSlash && (size_t)(lastSlash + 1 - g_logPath) + 21 < MAX_PATH)
                memcpy(lastSlash + 1, "eqswitch-dinput8.log", 21);
            else
                snprintf(g_logPath, MAX_PATH, "eqswitch-dinput8.log");
        } else {
            snprintf(g_logPath, MAX_PATH, "eqswitch-dinput8.log");
        }

        // Do NOT call DI8Log here — fopen inside DllMain risks loader lock deadlock.
        // First log entry fires naturally in DirectInput8Create (outside DllMain).

        // Build path to real dinput8.dll dynamically.
        // GetSystemDirectoryA returns System32; WoW64 redirects 32-bit
        // processes to SysWOW64 transparently.
        char sysDir[MAX_PATH];
        GetSystemDirectoryA(sysDir, MAX_PATH);
        char realPath[MAX_PATH];
        snprintf(realPath, MAX_PATH, "%s\\dinput8.dll", sysDir);

        g_realDll = LoadLibraryA(realPath);
        if (!g_realDll) {
            DI8Log("FATAL: failed to load real dinput8.dll from %s", realPath);
            return FALSE;
        }
        DI8Log("Loaded real dinput8.dll from %s", realPath);

        g_realCreate = (PFN_DirectInput8Create)
            GetProcAddress(g_realDll, "DirectInput8Create");
        if (!g_realCreate) {
            DI8Log("FATAL: failed to resolve DirectInput8Create");
            FreeLibrary(g_realDll);
            g_realDll = nullptr;
            return FALSE;
        }
        DI8Log("Resolved real DirectInput8Create");

        // Install IAT hooks on eqgame.exe's import table for keyboard
        // state and window focus queries (Phase 4).
        IatHook::InstallKeyboardHooks();
        DI8Log("DllMain: proxy ready");
    }
    else if (reason == DLL_PROCESS_DETACH) {
        DI8Log("DllMain: PROCESS_DETACH");
        // Restore IAT patches FIRST — before code pages are unmapped
        IatHook::RemoveKeyboardHooks();
        // Signal background threads to exit and release handles (no wait — loader lock held)
        DeviceProxy_Shutdown();
        // Release the real dinput8.dll reference
        if (g_realDll) { FreeLibrary(g_realDll); g_realDll = nullptr; }
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
