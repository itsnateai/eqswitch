// eqswitch-hook.dll — Hooks SetWindowPos/MoveWindow inside eqgame.exe
// to enforce window positioning from EQSwitch via shared memory.
// Compiled as 32-bit DLL (eqgame.exe is x86).

#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include "MinHook.h"

// Shared memory struct — must match C# HookConfigWriter layout exactly
#pragma pack(push, 1)
struct HookConfig {
    int enabled;            // 1 = enforce positions, 0 = passthrough
    int targetX;
    int targetY;
    int targetW;            // 0 = don't override width
    int targetH;            // 0 = don't override height
    int stripThickFrame;    // 1 = remove WS_THICKFRAME on hooked calls
};
#pragma pack(pop)

static const char* SHARED_MEM_NAME = "EQSwitchHookCfg";
static const DWORD SHARED_MEM_SIZE = sizeof(HookConfig);

// Globals
static HANDLE g_hMapFile = NULL;
static volatile HookConfig* g_pConfig = NULL;
static HWND g_cachedEqHwnd = NULL;
static HMODULE g_hModule = NULL;

// Original function pointers (trampolines)
typedef BOOL(WINAPI* PFN_SetWindowPos)(HWND, HWND, int, int, int, int, UINT);
typedef BOOL(WINAPI* PFN_MoveWindow)(HWND, int, int, int, int, BOOL);

static PFN_SetWindowPos g_origSetWindowPos = NULL;
static PFN_MoveWindow g_origMoveWindow = NULL;

// Simple file logger for debugging injection issues
static void LogMessage(const char* fmt, ...) {
    // Log to a file next to eqgame.exe for debugging
    char path[MAX_PATH];
    GetModuleFileNameA(NULL, path, MAX_PATH);
    // Replace exe name with log name
    char* lastSlash = strrchr(path, '\\');
    if (lastSlash) {
        strcpy(lastSlash + 1, "eqswitch-hook.log");
    } else {
        strcpy(path, "eqswitch-hook.log");
    }

    FILE* f = fopen(path, "a");
    if (!f) return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    fprintf(f, "[%02d:%02d:%02d.%03d] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    va_list args;
    va_start(args, fmt);
    vfprintf(f, fmt, args);
    va_end(args);

    fprintf(f, "\n");
    fclose(f);
}

// Check if a window belongs to this EQ process
static BOOL IsEqWindow(HWND hWnd) {
    // Fast path: cached handle still valid
    if (g_cachedEqHwnd && g_cachedEqHwnd == hWnd && IsWindow(hWnd)) {
        return TRUE;
    }

    // Verify window belongs to our process
    DWORD pid = 0;
    GetWindowThreadProcessId(hWnd, &pid);
    if (pid != GetCurrentProcessId()) {
        return FALSE;
    }

    // Check if it's a top-level window with a title (EQ's main window)
    // Skip child windows and message-only windows
    if (GetParent(hWnd) != NULL) {
        return FALSE;
    }

    char title[256] = {0};
    GetWindowTextA(hWnd, title, sizeof(title));
    if (strlen(title) == 0) {
        return FALSE;
    }

    // EQ window titles start with "EverQuest"
    if (strncmp(title, "EverQuest", 9) == 0) {
        g_cachedEqHwnd = hWnd;
        return TRUE;
    }

    return FALSE;
}

// Read config from shared memory
static BOOL ReadConfig(HookConfig* out) {
    if (!g_pConfig) return FALSE;
    // Volatile read — C# side may update at any time
    *out = *(const HookConfig*)g_pConfig;
    return TRUE;
}

// Hooked SetWindowPos
static BOOL WINAPI HookedSetWindowPos(
    HWND hWnd, HWND hWndInsertAfter,
    int X, int Y, int cx, int cy, UINT uFlags)
{
    HookConfig cfg;
    if (IsEqWindow(hWnd) && ReadConfig(&cfg) && cfg.enabled) {
        // Override position
        X = cfg.targetX;
        Y = cfg.targetY;

        // Override size unless SWP_NOSIZE flag is set
        if (!(uFlags & SWP_NOSIZE)) {
            if (cfg.targetW > 0) cx = cfg.targetW;
            if (cfg.targetH > 0) cy = cfg.targetH;
        }

        // Never let EQ set SWP_NOMOVE — we always want our position
        uFlags &= ~SWP_NOMOVE;

        // Strip thick frame if requested
        if (cfg.stripThickFrame) {
            LONG_PTR style = GetWindowLongPtr(hWnd, GWL_STYLE);
            if (style & WS_THICKFRAME) {
                style &= ~WS_THICKFRAME;
                SetWindowLongPtr(hWnd, GWL_STYLE, style);
                uFlags |= SWP_FRAMECHANGED;
            }
        }
    }

    return g_origSetWindowPos(hWnd, hWndInsertAfter, X, Y, cx, cy, uFlags);
}

// Hooked MoveWindow
static BOOL WINAPI HookedMoveWindow(
    HWND hWnd, int X, int Y, int nWidth, int nHeight, BOOL bRepaint)
{
    HookConfig cfg;
    if (IsEqWindow(hWnd) && ReadConfig(&cfg) && cfg.enabled) {
        X = cfg.targetX;
        Y = cfg.targetY;
        if (cfg.targetW > 0) nWidth = cfg.targetW;
        if (cfg.targetH > 0) nHeight = cfg.targetH;

        if (cfg.stripThickFrame) {
            LONG_PTR style = GetWindowLongPtr(hWnd, GWL_STYLE);
            if (style & WS_THICKFRAME) {
                style &= ~WS_THICKFRAME;
                SetWindowLongPtr(hWnd, GWL_STYLE, style);
            }
        }
    }

    return g_origMoveWindow(hWnd, X, Y, nWidth, nHeight, bRepaint);
}

// Open shared memory region
static BOOL OpenSharedMemory() {
    g_hMapFile = OpenFileMappingA(FILE_MAP_READ, FALSE, SHARED_MEM_NAME);
    if (!g_hMapFile) {
        LogMessage("OpenFileMapping failed: %lu", GetLastError());
        return FALSE;
    }

    g_pConfig = (volatile HookConfig*)MapViewOfFile(
        g_hMapFile, FILE_MAP_READ, 0, 0, SHARED_MEM_SIZE);
    if (!g_pConfig) {
        LogMessage("MapViewOfFile failed: %lu", GetLastError());
        CloseHandle(g_hMapFile);
        g_hMapFile = NULL;
        return FALSE;
    }

    LogMessage("Shared memory opened: enabled=%d, x=%d, y=%d, w=%d, h=%d",
        g_pConfig->enabled, g_pConfig->targetX, g_pConfig->targetY,
        g_pConfig->targetW, g_pConfig->targetH);
    return TRUE;
}

// Install hooks
static BOOL InstallHooks() {
    MH_STATUS status = MH_Initialize();
    if (status != MH_OK) {
        LogMessage("MH_Initialize failed: %d", status);
        return FALSE;
    }

    // Hook SetWindowPos
    status = MH_CreateHook(
        (LPVOID)&SetWindowPos,
        (LPVOID)&HookedSetWindowPos,
        (LPVOID*)&g_origSetWindowPos);
    if (status != MH_OK) {
        LogMessage("MH_CreateHook(SetWindowPos) failed: %d", status);
        MH_Uninitialize();
        return FALSE;
    }

    // Hook MoveWindow
    status = MH_CreateHook(
        (LPVOID)&MoveWindow,
        (LPVOID)&HookedMoveWindow,
        (LPVOID*)&g_origMoveWindow);
    if (status != MH_OK) {
        LogMessage("MH_CreateHook(MoveWindow) failed: %d", status);
        MH_Uninitialize();
        return FALSE;
    }

    // Enable all hooks
    status = MH_EnableHook(MH_ALL_HOOKS);
    if (status != MH_OK) {
        LogMessage("MH_EnableHook failed: %d", status);
        MH_Uninitialize();
        return FALSE;
    }

    LogMessage("Hooks installed successfully");
    return TRUE;
}

// Cleanup
static void Cleanup() {
    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();

    if (g_pConfig) {
        UnmapViewOfFile((LPCVOID)g_pConfig);
        g_pConfig = NULL;
    }
    if (g_hMapFile) {
        CloseHandle(g_hMapFile);
        g_hMapFile = NULL;
    }

    LogMessage("Hooks removed, cleanup complete");
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
        LogMessage("=== eqswitch-hook.dll loaded into PID %lu ===", GetCurrentProcessId());

        if (!OpenSharedMemory()) {
            LogMessage("Shared memory not available — hooks NOT installed");
            return TRUE; // Still load, but do nothing
        }

        if (!InstallHooks()) {
            LogMessage("Hook installation failed");
            // Clean up shared memory
            if (g_pConfig) { UnmapViewOfFile((LPCVOID)g_pConfig); g_pConfig = NULL; }
            if (g_hMapFile) { CloseHandle(g_hMapFile); g_hMapFile = NULL; }
            return TRUE;
        }
        break;

    case DLL_PROCESS_DETACH:
        LogMessage("DLL_PROCESS_DETACH — cleaning up");
        Cleanup();
        break;
    }
    return TRUE;
}
