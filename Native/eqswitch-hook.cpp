// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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

// Shared memory struct — must match C# HookConfigWriter layout exactly.
//
// v3.22.81 — added `pinGeometry` (8th int). The C# side hard-asserts the total
// size == 288 (8 ints * 4 + 256 title) at static-init and THROWS on mismatch,
// so any field add/remove here MUST update HookConfigWriter.cs in the same
// commit or the app refuses to start (a loud tripwire, not silent corruption).
#pragma pack(push, 1)
struct HookConfig {
    int enabled;            // 1 = enforce positions, 0 = passthrough
    int targetX;
    int targetY;
    int targetW;            // 0 = don't override width
    int targetH;            // 0 = don't override height
    int stripThickFrame;    // 1 = remove WS_THICKFRAME on hooked calls
    int blockMinimize;      // 1 = prevent EQ from minimizing itself
    int pinGeometry;        // v3.22.81: 1 = Windowed mode — install the GeoWndProc
                            // subclass and pin geometry synchronously per WM_
                            // message (kills the multi-monitor growth + sliver
                            // the external C# guard-timer race produced). 0 =
                            // Fullscreen — legacy SetWindowPos-hook + C# guard.
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
static HANDLE g_initThread = NULL;
static volatile LONG g_initialized = 0;
static volatile LONG g_hooksInstalled = 0;  // set only if MH_EnableHook succeeded

// v3.22.44 Gate #4 — detour-body critical section. Each of the four hook
// functions below (HookedSetWindowPos / HookedMoveWindow / HookedSetWindowTextA
// / HookedShowWindow) Enters this at entry and Leaves on exit. The Cleanup()
// teardown path Enters it before MH_DisableHook / MH_Uninitialize — which
// means an in-flight EQ thread inside a detour keeps the unhook path waiting
// until the detour returns. Without this, FreeLibrary-driven cleanup
// (CreateRemoteThread eject from C# side) races with EQ's rendering thread
// mid-detour: the trampoline pages are freed under it and the return path
// lands in unmapped memory → eqgame.exe AV. Mirrors MacroQuest's
// CAutoLock(&gDetourCS) discipline in MQ2DetourAPI.cpp.
//
// v3.22.44 round-2 (T3-Opus HIGH #1): switched from SRWLOCK to CRITICAL_SECTION
// to make recursive same-thread re-entry safe. The eqswitch-di8 sibling lock
// had a fundamental flaw: HookedGetForegroundWindow (IAT-replaced) takes
// shared, then calls g_realGetForegroundWindow whose prologue is inline-
// patched to InlineHookedGetForegroundWindow which takes shared AGAIN on the
// same thread. Windows SRWLOCK explicitly forbids recursive shared acquire,
// and with an exclusive waiter pending (Cleanup running) the inner shared
// blocks → outer cannot release → exclusive cannot acquire → permanent
// deadlock that hangs eqgame.exe. CRITICAL_SECTION is recursive by design
// (same-thread Enter increments a count; Leave decrements). Both DLLs use the
// same primitive now for consistency, even though eqswitch-hook.cpp's four
// hooks don't recurse (they're symmetrical for code-review clarity).
// MQ2 picked CRITICAL_SECTION over SRWLOCK for gDetourCS for the same reason.
// Initialized in DllMain DLL_PROCESS_ATTACH (safe — no loader-lock interaction)
// before the init thread is spawned. NOT deleted in Cleanup — the DLL is
// unloading; the kernel object goes away with the .data section.
static CRITICAL_SECTION g_detourCs;
static volatile LONG g_detourCsInitialized = 0;  // set by DllMain ATTACH

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
//
// v3.22.44 Gate #5: tightened cache validation. EQ recreates its top-level
// window across major state transitions — most importantly char-select →
// in-world, where the CSidlScreenWnd-backed char-select window is destroyed
// and the in-game DAG3D window is constructed. Pre-Gate-#5 this function
// cached the FIRST hwnd that satisfied the four checks (in-process, no
// parent, visible) and held onto it via the fast-path comparison
// `g_cachedEqHwnd == hWnd`. When EQ destroyed the cached HWND, the next
// hooked SetWindowPos for the NEW HWND fell through to the slow path (good)
// and re-cached (good), but if a hooked operation arrived for the OLD HWND
// (or an arbitrary new window EQ created later that didn't match the cache),
// the fast path returned TRUE for the cached value when it should have
// invalidated. The fix: invalidate the cache eagerly whenever it fails any
// of its invariants, AND don't cache a window unless it stably satisfies
// the "main eqgame window" predicate (parent==NULL + visible + matches our
// PID). The char-select→in-world close (Scenario C in Nate's 2026-05-23
// bug report) is the symptom this is meant to address.
static BOOL IsEqWindow(HWND hWnd) {
    // Fast path: cached handle is the one we're querying AND it still exists.
    // Cheap to re-validate IsWindow on every call (kernel walks the desktop
    // table; this is sub-microsecond on Win10/11).
    if (g_cachedEqHwnd != NULL && g_cachedEqHwnd == hWnd) {
        if (IsWindow(hWnd)) {
            return TRUE;
        }
        // Cached HWND was destroyed. Invalidate so subsequent calls don't
        // try to reuse it. The slow-path below will re-cache the next
        // qualifying window.
        g_cachedEqHwnd = NULL;
    }

    // Slow path: validate the requested hWnd against the full predicate set.

    // Filter by process FIRST — cheapest reject for non-our-process windows
    // (Discord overlay, Steam, audio drivers, etc.).
    DWORD pid = 0;
    GetWindowThreadProcessId(hWnd, &pid);
    if (pid != GetCurrentProcessId()) {
        return FALSE;
    }

    // Skip child windows and message-only windows. EQ's main eqgame window
    // is a top-level visible window; child windows belong to its UI elements
    // and should not get the slim-titlebar treatment.
    if (GetParent(hWnd) != NULL) {
        return FALSE;
    }

    // Must be visible. Excludes hidden helper windows EQ may create during
    // char-select → in-world transitions. The OLD char-select window
    // becomes IsWindowVisible=FALSE during teardown — the slow-path here
    // rejects it correctly, but if we'd cached it earlier the fast-path
    // would have already returned TRUE incorrectly. Gate #5's IsWindow
    // invalidation above closes that gap.
    if (!IsWindowVisible(hWnd)) {
        // Also: if this is the currently-cached window and it just became
        // hidden, invalidate the cache so we re-detect the new visible
        // top-level window on the next call (typically EQ's new state
        // window). Prevents the hook from operating on a hidden window
        // during a state-transition tear-down.
        if (g_cachedEqHwnd == hWnd) {
            g_cachedEqHwnd = NULL;
        }
        return FALSE;
    }

    // All predicates passed — this is a valid eqgame top-level window. Cache
    // it for the fast path on subsequent calls. (Re-)caching is idempotent.
    g_cachedEqHwnd = hWnd;
    return TRUE;
}

// Read config from shared memory with torn-read detection.
// The C# side is single-threaded and writes are fast, so torn reads
// are extremely unlikely but theoretically possible for the 288-byte struct.
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

// ─── v3.22.81 Windowed-mode geometry subclass (GeoWndProc) ──────────
//
// Ported from the WinEQ2 RE (memory/reference_wineq2_window_pin_technique.md):
// WinEQ2 pins its slim-titlebar window by SUBCLASSING eqgame's WndProc and
// forcing geometry synchronously inside WM_WINDOWPOSCHANGING — it does NOT use
// an external guard timer. EQSwitch's old Windowed enforcement was a ~500ms C#
// guard (WindowManager.ApplySlimTitlebarToAll) that read the already-DWM-bled
// rect and re-applied a bigger one → read-modify-write race vs DWM → runaway
// growth on a 2nd-monitor boundary + a right-edge sliver. Moving the per-message
// pin into this in-process subclass owns the rect BEFORE anyone (DWM, EQ, a
// drag) sees it, so the size can never accumulate.
//
// SCOPE: Windowed mode only (cfg.pinGeometry == 1). Fullscreen (WS_POPUP) keeps
// the legacy SetWindowPos-hook + C# guard path 100% unchanged — the subclass is
// never even installed unless a detour sees pinGeometry==1 on the EQ window.
//
// COEXISTENCE with eqswitch-di8.dll's activation subclass (device_proxy.cpp):
// that subclass is lifecycle-scoped to autologin (installed only while
// KeyShm::IsActive(), removed on the SHM falling edge). During normal Windowed
// gameplay — when geometry growth happens — it is already gone, so there is no
// steady-state chaining war. During the brief login overlap both chain via
// CallWindowProc and the di8 DLL stays mapped until process death, so chaining
// to whatever proc we captured is safe. We capture-and-chain exactly like
// device_proxy.cpp does.
//
// LIFETIME: installed lazily (first SetWindowPos/MoveWindow detour that sees
// pinGeometry==1 on the EQ window), behavior gated on the LIVE SHM flag every
// message (so a live Fullscreen<->Windowed switch flips pinning on/off WITHOUT
// install/remove churn), and removed only in Cleanup() on FreeLibrary eject.
// Minimizing removal events to the single moment the process is already tearing
// down matches device_proxy.cpp's discipline and sidesteps the classic
// subclass-removal-ordering trap.
static WNDPROC g_origGeoWndProc = NULL;       // proc present when we subclassed (chain target)
static HWND    g_geoSubclassHwnd = NULL;      // the window we subclassed (EQ's single main window)
static volatile LONG g_geoSubclassInstalled = 0;

static LRESULT CALLBACK GeoWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    // Defensive: GeoWndProc can only be the window proc if EnsureGeoSubclass set
    // g_origGeoWndProc first, so this is never NULL in practice — but never
    // CallWindowProc(NULL).
    if (!g_origGeoWndProc)
        return DefWindowProcW(hWnd, msg, wParam, lParam);

    // Read only the live fields we need, directly from the volatile mapping.
    // Individual int reads are atomic on x86; a torn rect (C# mid-write) is a
    // one-frame cosmetic blip that self-corrects next message, and C# rewrites
    // the rect only on a config/monitor change (rare). Null-guard against the
    // Cleanup() unmap window.
    const volatile HookConfig* cfg = g_pConfig;
    if (cfg) {
        int enabled = cfg->enabled;
        int pin     = cfg->pinGeometry;
        int tx = cfg->targetX, ty = cfg->targetY;
        int tw = cfg->targetW, th = cfg->targetH;

        if (enabled && pin && tw > 0 && th > 0) {
            switch (msg) {
            case WM_WINDOWPOSCHANGING: {
                // THE growth-killer. Force the authoritative outer rect (computed
                // once by C# ComputeSlimTitlebarOuterRect, written to SHM) and
                // return WITHOUT chaining — the system then applies the modified
                // WINDOWPOS, so EQ's / DWM's own re-expansion never runs.
                WINDOWPOS* wp = (WINDOWPOS*)lParam;
                // Don't fight legitimate minimize/maximize/show-state changes:
                // forcing the restored rect there would cancel a minimize. Pin
                // only steady-state moves/sizes of a normal (restored) window.
                if (!IsIconic(hWnd) && !IsZoomed(hWnd) &&
                    !(wp->flags & (SWP_HIDEWINDOW | SWP_SHOWWINDOW))) {
                    wp->x  = tx;  wp->y  = ty;
                    wp->cx = tw;  wp->cy = th;
                    wp->flags &= ~(SWP_NOSIZE | SWP_NOMOVE);
                    return 0;
                }
                break;
            }
            case WM_GETMINMAXINFO: {
                // Belt-and-suspenders fixed-size lock: clamp track size to the
                // pinned WxH so a stray resize-drag or maximize can't exceed it.
                // Chain first for sane defaults, then override.
                LRESULT r = CallWindowProcW(g_origGeoWndProc, hWnd, msg, wParam, lParam);
                MINMAXINFO* mmi = (MINMAXINFO*)lParam;
                mmi->ptMinTrackSize.x = mmi->ptMaxTrackSize.x = tw;
                mmi->ptMinTrackSize.y = mmi->ptMaxTrackSize.y = th;
                return r;
            }
            case WM_MOVING: {
                // Pin position — overwrite the proposed drag rect with the
                // authoritative outer rect so the window can't be dragged off
                // ("we don't need draggable" — Nate).
                RECT* r = (RECT*)lParam;
                r->left  = tx;       r->top    = ty;
                r->right = tx + tw;  r->bottom = ty + th;
                return TRUE;
            }
            case WM_SYSCOMMAND: {
                // Swallow maximize (double-click caption / Win+Up) — this is a
                // fixed-size pinned window. Low nibble of wParam is reserved.
                if ((wParam & 0xFFF0) == SC_MAXIMIZE)
                    return 0;
                break;
            }
            default: break;
            }
        }
    }
    // Pass-through (Fullscreen, disabled, or unhandled message).
    return CallWindowProcW(g_origGeoWndProc, hWnd, msg, wParam, lParam);
}

// Install (or re-install after EQ recreated its window / overwrote its proc).
// Called from the position detours on the EQ UI thread, under g_detourCs.
static void EnsureGeoSubclass(HWND hWnd) {
    WNDPROC current = (WNDPROC)GetWindowLongPtrW(hWnd, GWLP_WNDPROC);
    if (current == GeoWndProc) return;  // already ours on this window
    // EQ recreates its top-level window at char-select → in-world and may
    // re-set its WndProc; capture whatever is current as the chain target.
    g_origGeoWndProc = current;
    SetWindowLongPtrW(hWnd, GWLP_WNDPROC, (LONG_PTR)GeoWndProc);
    g_geoSubclassHwnd = hWnd;
    if (!InterlockedExchange(&g_geoSubclassInstalled, 1))
        LogMessage("GeoWndProc: installed Windowed geometry subclass (hwnd=0x%p orig=0x%p)", (void*)hWnd, (void*)current);
    else
        LogMessage("GeoWndProc: RE-installed subclass (EQ recreated window / overwrote proc; hwnd=0x%p orig=0x%p)", (void*)hWnd, (void*)current);
}

// Restore EQ's proc before the DLL unmaps. Only restore if we're STILL the
// current proc (if something subclassed on top we can't safely unwind — leave
// it; the residual chain to GeoWndProc is the process-is-dying case). Mirrors
// device_proxy.cpp::RemoveSubclass. NO SendMessage flush — Cleanup runs under
// the loader lock (DllMain) where SendMessage can deadlock; the residual
// EQ-thread-mid-GeoWndProc-during-unmap race is identical to the di8 subclass
// that has shipped for many versions and only fires on FreeLibrary eject
// (which coincides with client/process teardown).
static void RemoveGeoSubclass() {
    if (!InterlockedExchange(&g_geoSubclassInstalled, 0)) return;
    HWND hWnd = g_geoSubclassHwnd;
    if (hWnd && IsWindow(hWnd) && g_origGeoWndProc) {
        WNDPROC current = (WNDPROC)GetWindowLongPtrW(hWnd, GWLP_WNDPROC);
        if (current == GeoWndProc) {
            SetWindowLongPtrW(hWnd, GWLP_WNDPROC, (LONG_PTR)g_origGeoWndProc);
            LogMessage("GeoWndProc: removed geometry subclass");
        }
    }
    g_geoSubclassHwnd = NULL;
}

// ─── Hooked SetWindowPos ─────────────────────────────────────────
// v3.22.44 Gate #4 (round 2 — CRITICAL_SECTION): Enter/Leave the detour CS
// around the entire detour body. Cleanup enters the CS before MH_DisableHook
// and never Leaves, so any in-flight detour bodies must Leave before the
// unhook proceeds. CRITICAL_SECTION serializes detour bodies (each Enter is
// exclusive per-owner with recursive same-thread re-entry); SRWLOCK shared
// was used in round-1 but failed via recursive shared deadlock against the
// IAT→inline call chain in iat_hook.cpp. See g_detourCs declaration for
// full rationale.
static BOOL WINAPI HookedSetWindowPos(
    HWND hWnd, HWND hWndInsertAfter,
    int X, int Y, int cx, int cy, UINT uFlags)
{
    EnterCriticalSection(&g_detourCs);  // v3.22.44 r2
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

        // v3.22.81 — Windowed mode: lazily install the per-message geometry
        // subclass (kills the multi-monitor growth + sliver the C# guard timer
        // raced into). Re-installs transparently after EQ recreates its window.
        if (cfg.pinGeometry)
            EnsureGeoSubclass(hWnd);
    }

    BOOL r = g_origSetWindowPos(hWnd, hWndInsertAfter, X, Y, cx, cy, uFlags);
    LeaveCriticalSection(&g_detourCs);  // v3.22.44 r2
    return r;
}

// ─── Hooked MoveWindow ───────────────────────────────────────────
static BOOL WINAPI HookedMoveWindow(
    HWND hWnd, int X, int Y, int nWidth, int nHeight, BOOL bRepaint)
{
    EnterCriticalSection(&g_detourCs);  // v3.22.44 r2
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

        // v3.22.81 — Windowed mode: lazily install the per-message geometry
        // subclass (see HookedSetWindowPos for the full rationale).
        if (cfg.pinGeometry)
            EnsureGeoSubclass(hWnd);
    }

    BOOL r = g_origMoveWindow(hWnd, X, Y, nWidth, nHeight, bRepaint);
    LeaveCriticalSection(&g_detourCs);  // v3.22.44 r2
    return r;
}

// ─── Hooked SetWindowTextA ───────────────────────────────────────
// EQ calls SetWindowTextA to set its own title ("EverQuest", "EverQuest - CharName").
// When we have a custom title configured, override any title EQ tries to set.
// This is how WinEQ2 makes titles stick — hook from inside the process.
static BOOL WINAPI HookedSetWindowTextA(HWND hWnd, LPCSTR lpString)
{
    EnterCriticalSection(&g_detourCs);  // v3.22.44 r2
    HookConfig cfg;
    BOOL r;
    if (IsEqWindow(hWnd) && ReadConfig(&cfg) && cfg.windowTitle[0] != '\0') {
        // Use our title instead of whatever EQ wants to set
        r = g_origSetWindowTextA(hWnd, cfg.windowTitle);
    } else {
        r = g_origSetWindowTextA(hWnd, lpString);
    }
    LeaveCriticalSection(&g_detourCs);  // v3.22.44 r2
    return r;
}

// ─── Hooked ShowWindow ───────────────────────────────────────────
// EQ minimizes itself when it loses focus during DirectX init.
// Block SW_MINIMIZE/SW_SHOWMINIMIZED/SW_SHOWMINNOACTIVE when configured.
static BOOL WINAPI HookedShowWindow(HWND hWnd, int nCmdShow)
{
    EnterCriticalSection(&g_detourCs);  // v3.22.44 r2
    HookConfig cfg;
    if (IsEqWindow(hWnd) && ReadConfig(&cfg) && cfg.blockMinimize) {
        if (nCmdShow == SW_MINIMIZE ||        // 6
            nCmdShow == SW_SHOWMINIMIZED ||   // 2
            nCmdShow == SW_SHOWMINNOACTIVE || // 7
            nCmdShow == SW_FORCEMINIMIZE)     // 11
        {
            LogMessage("Blocked minimize attempt (nCmdShow=%d)", nCmdShow);
            LeaveCriticalSection(&g_detourCs);  // v3.22.44 r2
            return TRUE; // Pretend we did it
        }
    }

    BOOL r = g_origShowWindow(hWnd, nCmdShow);
    LeaveCriticalSection(&g_detourCs);  // v3.22.44 r2
    return r;
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

    InterlockedExchange(&g_hooksInstalled, 1);
    LogMessage("All hooks installed (SetWindowPos, MoveWindow, SetWindowTextA, ShowWindow)");
    return TRUE;
}

// ─── Cleanup ─────────────────────────────────────────────────────
static void Cleanup() {
    // v3.22.81 — restore EQ's WndProc FIRST, before MinHook teardown / SHM
    // unmap. After this, GeoWndProc is no longer the window proc, so EQ won't
    // dispatch into our (about-to-be-unmapped) code or read the (about-to-be-
    // unmapped) g_pConfig. Only runs on FreeLibrary eject (DllMain reserved==NULL
    // path); process-exit detach skips Cleanup entirely (OS reclaims everything).
    RemoveGeoSubclass();

    // Only tear down MinHook if it was successfully initialized and hooks enabled.
    // If OpenSharedMemory failed, MH_Initialize was never called — calling
    // MH_Uninitialize on uninitialized state is undefined behavior.
    //
    // v3.22.44 Gate #4 (round 2): Enter the detour CRITICAL_SECTION before
    // flipping hooks back. EnterCriticalSection blocks until every in-flight
    // holder (EQ threads currently inside HookedSetWindowPos /
    // HookedMoveWindow / HookedSetWindowTextA / HookedShowWindow) Leaves.
    // MH_DisableHook then atomically restores the trampoline prologues while
    // no detour body is on a stack frame whose return address points into
    // our about-to-be-unmapped code. CRITICAL_SECTION is recursive (same
    // thread can re-enter) which matches MQ2's gDetourCS discipline.
    //
    // The CS is NOT Left by Cleanup itself — once we're past MH_Uninitialize,
    // no further detour body will ever run (the trampolines are gone), so no
    // new entry is possible. The CRITICAL_SECTION struct goes away with the
    // DLL section.
    if (g_hooksInstalled) {
        // v3.22.44 r2: EnterCriticalSection serializes against any in-flight
        // detour body (recursive same-thread re-enter still works because CS
        // is recursive). Blocks until all current holders Leave. NOT followed
        // by LeaveCriticalSection — we deliberately keep the CS held for the
        // remainder of cleanup so no new detour entry is possible before
        // MH_Uninitialize frees the trampoline pages. The CRITICAL_SECTION
        // memory itself goes away with the DLL section.
        if (g_detourCsInitialized) EnterCriticalSection(&g_detourCs);
        MH_DisableHook(MH_ALL_HOOKS);
        MH_Uninitialize();
    }

    if (g_pConfig) {
        UnmapViewOfFile((LPCVOID)g_pConfig);
        g_pConfig = NULL;
    }
    if (g_hMapFile) {
        CloseHandle(g_hMapFile);
        g_hMapFile = NULL;
    }

    LogMessage("Cleanup complete (hooks=%s)", g_hooksInstalled ? "removed" : "never installed");
}

// ─── Deferred Init Thread ───────────────────────────────────────
// DllMain holds the loader lock. OpenSharedMemory calls LogMessage (fopen)
// and InstallHooks calls MH_Initialize (VirtualAlloc) — both can deadlock
// under the loader lock. Defer to a thread that runs after DllMain returns.
static DWORD WINAPI InitThread(LPVOID param) {
    (void)param;

    LogMessage("Init thread started (outside loader lock)");

    if (!OpenSharedMemory()) {
        LogMessage("Shared memory not available — hooks not installed");
        InterlockedExchange(&g_initialized, 1);
        return 0;
    }

    if (!InstallHooks()) {
        LogMessage("Hook installation failed");
        if (g_pConfig) { UnmapViewOfFile((LPCVOID)g_pConfig); g_pConfig = NULL; }
        if (g_hMapFile) { CloseHandle(g_hMapFile); g_hMapFile = NULL; }
    }

    InterlockedExchange(&g_initialized, 1);
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
        // v3.22.44 r2: initialize the detour critical section BEFORE the init
        // thread spawns. Detours can only fire after InitThread calls
        // MH_EnableHook (inside Cleanup of OpenSharedMemory + InstallHooks),
        // so the CS is guaranteed live before any acquire. Spin count 4000
        // matches MQ2's gDetourCS — favors short uncontended acquires without
        // crossing into the kernel.
        InitializeCriticalSectionAndSpinCount(&g_detourCs, 4000);
        InterlockedExchange(&g_detourCsInitialized, 1);
        // Build log path (safe — GetModuleFileNameA doesn't re-acquire loader lock).
        BuildLogPath();
        // Spawn init thread — runs after DllMain releases the loader lock.
        // CreateThread in DLL_PROCESS_ATTACH is safe; the new thread simply
        // blocks on the loader lock until DllMain returns.
        g_initThread = CreateThread(NULL, 0, InitThread, NULL, 0, NULL);
        break;

    case DLL_PROCESS_DETACH:
        // reserved != NULL → process exiting: OS reclaims everything, skip cleanup
        //   (can't safely wait on threads — ExitProcess in progress).
        // reserved == NULL → FreeLibrary: wait for init thread, then clean up.
        if (reserved == NULL) {
            if (g_initThread) {
                // Wait for init thread to finish before cleanup — prevents race
                // where C# host ejects DLL before hooks are fully installed.
                // Safe: loader lock is NOT re-acquired for FreeLibrary detach on Win8+.
                WaitForSingleObject(g_initThread, 3000);
                CloseHandle(g_initThread);
                g_initThread = NULL;
            }
            if (g_initialized) {
                Cleanup();
            }
        }
        break;
    }
    return TRUE;
}
