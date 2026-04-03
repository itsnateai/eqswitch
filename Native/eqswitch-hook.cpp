// eqswitch-hook.dll — Injected into eqgame.exe to enforce window behavior
// via shared memory config from EQSwitch.
//
// Hooks:
//   SetWindowPos / MoveWindow    — enforce window position/size (slim titlebar)
//   SetWindowTextA               — override window title (like WinEQ2)
//   ShowWindow                   — block unwanted minimize during DirectX init
//
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
    int blockMinimize;      // 1 = prevent EQ from minimizing itself
    char windowTitle[256];  // custom title (empty = don't override)
};
#pragma pack(pop)

static const char* SHARED_MEM_PREFIX = "EQSwitchHookCfg_";
static const DWORD SHARED_MEM_SIZE = sizeof(HookConfig);

// Globals
static HANDLE g_hMapFile = NULL;
static volatile HookConfig* g_pConfig = NULL;
static HWND g_cachedEqHwnd = NULL;
static HMODULE g_hModule = NULL;

// Original function pointers (trampolines)
typedef BOOL(WINAPI* PFN_SetWindowPos)(HWND, HWND, int, int, int, int, UINT);
typedef BOOL(WINAPI* PFN_MoveWindow)(HWND, int, int, int, int, BOOL);
typedef BOOL(WINAPI* PFN_SetWindowTextA)(HWND, LPCSTR);
typedef BOOL(WINAPI* PFN_ShowWindow)(HWND, int);

static PFN_SetWindowPos g_origSetWindowPos = NULL;
static PFN_MoveWindow g_origMoveWindow = NULL;
static PFN_SetWindowTextA g_origSetWindowTextA = NULL;
static PFN_ShowWindow g_origShowWindow = NULL;

// Log path built from host exe path during DLL_PROCESS_ATTACH (safe under loader lock).
// Actual fopen is deferred to first LogMessage call outside DllMain.
static char g_hookLogPath[MAX_PATH] = {};
static bool g_hookLogPathReady = false;

static void BuildLogPath() {
    // Use NULL = process exe path (we're injected into eqgame.exe, log goes next to it)
    if (GetModuleFileNameA(NULL, g_hookLogPath, MAX_PATH)) {
        char* lastSlash = strrchr(g_hookLogPath, '\\');
        if (lastSlash && (size_t)(lastSlash + 1 - g_hookLogPath) + 18 < MAX_PATH)
            memcpy(lastSlash + 1, "eqswitch-hook.log", 18);
        else
            snprintf(g_hookLogPath, MAX_PATH, "eqswitch-hook.log");
    } else {
        snprintf(g_hookLogPath, MAX_PATH, "eqswitch-hook.log");
    }
    g_hookLogPathReady = true;
}

static void LogMessage(const char* fmt, ...) {
    if (!g_hookLogPathReady) return;

    FILE* f = fopen(g_hookLogPath, "a");
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

// Check if a window belongs to this EQ process.
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

    // Skip child windows and message-only windows
    if (GetParent(hWnd) != NULL) {
        return FALSE;
    }

    // Must be a visible top-level window (skip hidden helper windows)
    if (!IsWindowVisible(hWnd)) {
        return FALSE;
    }

    g_cachedEqHwnd = hWnd;
    return TRUE;
}

// Read config from shared memory with torn-read detection.
// The C# side is single-threaded and writes are fast, so torn reads
// are extremely unlikely but theoretically possible for the 284-byte struct.
// A simple retry on mismatch is sufficient — no lock needed.
static BOOL ReadConfig(HookConfig* out) {
    if (!g_pConfig) return FALSE;
    // Double-read to detect torn writes: read twice and compare enabled field.
    // If the C# side was mid-write, the two reads will likely differ.
    *out = *(const HookConfig*)g_pConfig;
    HookConfig verify;
    verify.enabled = ((const volatile HookConfig*)g_pConfig)->enabled;
    if (verify.enabled != out->enabled) {
        // Retry once — C# write should be complete by now
        *out = *(const HookConfig*)g_pConfig;
    }
    return TRUE;
}

// ─── Hooked SetWindowPos ─────────────────────────────────────────
static BOOL WINAPI HookedSetWindowPos(
    HWND hWnd, HWND hWndInsertAfter,
    int X, int Y, int cx, int cy, UINT uFlags)
{
    HookConfig cfg;
    if (IsEqWindow(hWnd) && ReadConfig(&cfg) && cfg.enabled) {
        X = cfg.targetX;
        Y = cfg.targetY;

        if (!(uFlags & SWP_NOSIZE)) {
            if (cfg.targetW > 0) cx = cfg.targetW;
            if (cfg.targetH > 0) cy = cfg.targetH;
        }

        uFlags &= ~SWP_NOMOVE;

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

// ─── Hooked MoveWindow ───────────────────────────────────────────
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

// ─── Hooked SetWindowTextA ───────────────────────────────────────
// EQ calls SetWindowTextA to set its own title ("EverQuest", "EverQuest - CharName").
// When we have a custom title configured, override any title EQ tries to set.
// This is how WinEQ2 makes titles stick — hook from inside the process.
static BOOL WINAPI HookedSetWindowTextA(HWND hWnd, LPCSTR lpString)
{
    HookConfig cfg;
    if (IsEqWindow(hWnd) && ReadConfig(&cfg) && cfg.windowTitle[0] != '\0') {
        // Use our title instead of whatever EQ wants to set
        return g_origSetWindowTextA(hWnd, cfg.windowTitle);
    }

    return g_origSetWindowTextA(hWnd, lpString);
}

// ─── Hooked ShowWindow ───────────────────────────────────────────
// EQ minimizes itself when it loses focus during DirectX init.
// Block SW_MINIMIZE/SW_SHOWMINIMIZED/SW_SHOWMINNOACTIVE when configured.
static BOOL WINAPI HookedShowWindow(HWND hWnd, int nCmdShow)
{
    HookConfig cfg;
    if (IsEqWindow(hWnd) && ReadConfig(&cfg) && cfg.blockMinimize) {
        if (nCmdShow == SW_MINIMIZE ||        // 6
            nCmdShow == SW_SHOWMINIMIZED ||   // 2
            nCmdShow == SW_SHOWMINNOACTIVE || // 7
            nCmdShow == SW_FORCEMINIMIZE)     // 11
        {
            LogMessage("Blocked minimize attempt (nCmdShow=%d)", nCmdShow);
            return TRUE; // Pretend we did it
        }
    }

    return g_origShowWindow(hWnd, nCmdShow);
}

// ─── Shared Memory ──────────────────────────────────────────────
static BOOL OpenSharedMemory() {
    char shmName[64];
    _snprintf(shmName, sizeof(shmName), "%s%lu", SHARED_MEM_PREFIX, GetCurrentProcessId());
    shmName[sizeof(shmName) - 1] = '\0';

    LogMessage("Opening shared memory: %s (size=%u)", shmName, SHARED_MEM_SIZE);
    g_hMapFile = OpenFileMappingA(FILE_MAP_READ, FALSE, shmName);
    if (!g_hMapFile) {
        LogMessage("OpenFileMapping(%s) failed: %lu", shmName, GetLastError());
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

    LogMessage("Shared memory opened: enabled=%d, pos=(%d,%d) size=%dx%d, blockMin=%d, title=\"%.64s\"",
        g_pConfig->enabled, g_pConfig->targetX, g_pConfig->targetY,
        g_pConfig->targetW, g_pConfig->targetH,
        g_pConfig->blockMinimize, (const char*)g_pConfig->windowTitle);
    return TRUE;
}

// ─── Hook Installation ──────────────────────────────────────────
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

    // Hook SetWindowTextA — EQ uses the ANSI version
    status = MH_CreateHook(
        (LPVOID)&SetWindowTextA,
        (LPVOID)&HookedSetWindowTextA,
        (LPVOID*)&g_origSetWindowTextA);
    if (status != MH_OK) {
        LogMessage("MH_CreateHook(SetWindowTextA) failed: %d", status);
        MH_Uninitialize();
        return FALSE;
    }

    // Hook ShowWindow — block EQ's self-minimize on focus loss
    status = MH_CreateHook(
        (LPVOID)&ShowWindow,
        (LPVOID)&HookedShowWindow,
        (LPVOID*)&g_origShowWindow);
    if (status != MH_OK) {
        LogMessage("MH_CreateHook(ShowWindow) failed: %d", status);
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

    LogMessage("All hooks installed (SetWindowPos, MoveWindow, SetWindowTextA, ShowWindow)");
    return TRUE;
}

// ─── Cleanup ─────────────────────────────────────────────────────
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
        // Build log path now (safe — GetModuleFileNameA(NULL,...) doesn't re-acquire loader lock).
        // Actual fopen is deferred to first LogMessage call outside DllMain.
        BuildLogPath();
        LogMessage("=== eqswitch-hook.dll loaded into PID %lu ===", GetCurrentProcessId());

        if (!OpenSharedMemory()) {
            LogMessage("Shared memory not available — hooks NOT installed");
            return TRUE;
        }

        if (!InstallHooks()) {
            LogMessage("Hook installation failed");
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
