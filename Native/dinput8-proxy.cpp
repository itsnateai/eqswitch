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

typedef HRESULT(WINAPI *PFN_DirectInput8Create)(
    HINSTANCE, DWORD, REFIID, LPVOID *, LPUNKNOWN);

static PFN_DirectInput8Create g_realCreate = nullptr;
static FILE *g_logFile = nullptr;

void DI8Log(const char *fmt, ...) {
    if (!g_logFile) return;
    fprintf(g_logFile, "[%lu] ", GetTickCount());
    va_list args;
    va_start(args, fmt);
    vfprintf(g_logFile, fmt, args);
    va_end(args);
    fprintf(g_logFile, "\n");
    fflush(g_logFile);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);

        g_logFile = fopen("eqswitch-dinput8.log", "w");
        DI8Log("DllMain: PROCESS_ATTACH (EQSwitch dinput8 proxy v1)");

        // Build path to real dinput8.dll dynamically.
        // GetSystemDirectoryA returns System32; WoW64 redirects 32-bit
        // processes to SysWOW64 transparently.
        char sysDir[MAX_PATH];
        GetSystemDirectoryA(sysDir, MAX_PATH);
        char realPath[MAX_PATH];
        snprintf(realPath, MAX_PATH, "%s\\dinput8.dll", sysDir);

        HMODULE realDll = LoadLibraryA(realPath);
        if (!realDll) {
            DI8Log("FATAL: failed to load real dinput8.dll from %s", realPath);
            return FALSE;
        }
        DI8Log("Loaded real dinput8.dll from %s", realPath);

        g_realCreate = (PFN_DirectInput8Create)
            GetProcAddress(realDll, "DirectInput8Create");
        if (!g_realCreate) {
            DI8Log("FATAL: failed to resolve DirectInput8Create");
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
        if (g_logFile) { fclose(g_logFile); g_logFile = nullptr; }
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
