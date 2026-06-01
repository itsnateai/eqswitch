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
    // Use NULL = process exe path (we're injected into eqgame.exe, log goes next to it).
    // PER-PID filename: every injected eqgame.exe shares the same exe dir, so a single
    // fixed name interleaves/corrupts under multibox (N clients append to one file with
    // no cross-process locking). eqswitch-hook-{pid}.log keeps each client's log clean —
    // matches the C# side's per-PID eqswitch-dinput8-{pid}.log convention.
    char dir[MAX_PATH] = {};
    if (GetModuleFileNameA(NULL, dir, MAX_PATH)) {
        char* lastSlash = strrchr(dir, '\\');
        if (lastSlash) *(lastSlash + 1) = '\0';   // keep the directory + trailing backslash
        else dir[0] = '\0';
    }
    _snprintf(g_hookLogPath, MAX_PATH, "%seqswitch-hook-%lu.log", dir, (unsigned long)GetCurrentProcessId());
    g_hookLogPath[MAX_PATH - 1] = '\0';
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

// ─── v3.24.0 Native Window-Mode backbuffer resize ────────────────────────────
// Instant-crisp DX backbuffer rebuild on a Window-Mode toggle — BOTH directions,
// no ScreenMode flip, no window restyle (the v3.23.7 interim's ForceDxReinit had
// both; its windowed-end-state fought our WS_POPUP Fullscreen → titlebar/glitch/
// growth, so it was gated Windowed-only, leaving a transient stretch the other
// way). Ported from EQ's OWN windowed-resize path (eqgame.exe FUN_008d9fd0,
// RE'd 2026-06-01 — see X:/_Projects/_.eqswitch-re/decompile_spike/FINDINGS.md).
//
// eqgame.exe loads a graphics DLL (CreateGraphicsEngine) and stores the render-
// engine interface pointer at a global (Ghidra DAT_015d46a4). That pointer is a
// COM-like object whose vtable lives in the gfx DLL. EQ rebuilds its backbuffer
// by calling this interface's SetResolution + ResetDevice; we call the SAME
// interface, the SAME way, on the SAME thread (EQ's main/UI thread — where
// GeoWndProc runs, between frames). Both EQSwitch slim modes are D3D-*windowed*
// (WS_POPUP vs WS_CAPTION; neither is exclusive fullscreen), so present mode
// never changes — only the backbuffer size (monH vs monH-caption). A pure
// SetResolution(w,h,32,0)+ResetDevice(0) is exactly that.
//
// RVA is relative to eqgame.exe's preferred ImageBase (0x400000); ASLR-rebased
// at runtime → resolve as GetModuleHandleA(NULL) + RVA.
static const DWORD RVA_GFX_RENDER_PTR = 0x11D46A4;  // -> g_pRender (gfx interface)
// gfx-interface vtable byte-offsets, verified against EQ's own call sites
// (FUN_008d9fd0 / FUN_008d9c50): +0x18/+0x1c read the current backbuffer dims,
// +0x6c sets the desired resolution, +0x64 resets the device (its false-return
// path is the one that logs "ResetDevice() failed!" inside EQ).
static const DWORD VT_GetBBWidth    = 0x18;
static const DWORD VT_GetBBHeight   = 0x1c;
static const DWORD VT_ResetDevice   = 0x64;
static const DWORD VT_SetResolution = 0x6c;

typedef int  (__thiscall *PFN_GfxGetDim)(void* self);
typedef void (__thiscall *PFN_GfxSetRes)(void* self, int w, int h, int bpp, int refreshHz);
typedef char (__thiscall *PFN_GfxReset) (void* self, int unused);

// Registered once via RegisterWindowMessage — the SAME string resolves to the
// SAME system-wide id in this hook AND the C# host, so PostMessage(eqHwnd,
// g_backbufferResizeMsg) from C# lands in GeoWndProc here. 0 until registered.
static UINT g_backbufferResizeMsg = 0;
static const wchar_t* BACKBUFFER_RESIZE_MSG_NAME = L"EQSwitch_BackbufferResize_v1";

// Best-effort gate: is [p, p+n) committed + readable (and executable if asked),
// without straddling out of its region? The SEH __try below is the real safety
// net; this just rejects obviously-bad pointers (null/uninit/stale offset) early
// and loudly instead of faulting.
static bool MemIsReadable(const void* p, SIZE_T n, bool needExec) {
    if (!p) return false;
    MEMORY_BASIC_INFORMATION mbi;
    if (VirtualQuery(p, &mbi, sizeof(mbi)) != sizeof(mbi)) return false;
    if (mbi.State != MEM_COMMIT) return false;
    DWORD prot = mbi.Protect & 0xFF;  // strip PAGE_GUARD/NOCACHE/WRITECOMBINE bits
    bool readable = (prot & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY |
                             PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
    if (!readable) return false;
    if (needExec) {
        bool exec = (prot & (PAGE_EXECUTE | PAGE_EXECUTE_READ |
                             PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
        if (!exec) return false;
    }
    UINT_PTR endOfRegion = (UINT_PTR)mbi.BaseAddress + mbi.RegionSize;
    if ((UINT_PTR)p + n > endOfRegion) return false;
    return true;
}

// Rebuild EQ's windowed D3D backbuffer to (clientW x clientH) via EQ's own gfx
// interface. MUST run on EQ's render/main thread (GeoWndProc guarantees this).
// Fully SEH-guarded + pointer-validated: on ANY fault or bad state it logs and
// returns false rather than crash the host. NEVER calls _exit — EQ does that on
// ResetDevice failure, but we must not take a live client down. Idempotent: a
// no-op (returns true) when the backbuffer is already at the target size, which
// is also the natural guard against a redundant reset flash.
static bool ApplyBackbufferResize(int clientW, int clientH) {
    if (clientW <= 0 || clientH <= 0 || clientW > 16384 || clientH > 16384) {
        LogMessage("BackbufferResize: refused — degenerate target %dx%d", clientW, clientH);
        return false;
    }
    HMODULE base = GetModuleHandleA(NULL);
    if (!base) return false;

    bool result = false;
    __try {
        void** ppRender = (void**)((BYTE*)base + RVA_GFX_RENDER_PTR);
        if (!MemIsReadable(ppRender, sizeof(void*), false)) {
            LogMessage("BackbufferResize: gfx ptr slot unreadable (RVA 0x%X) — skipping", RVA_GFX_RENDER_PTR);
            return false;
        }
        void* gfx = *ppRender;
        if (!gfx || !MemIsReadable(gfx, sizeof(void*), false)) {
            LogMessage("BackbufferResize: gfx interface null/not ready (no device yet?) — skipping");
            return false;
        }
        void** vtbl = *(void***)gfx;
        if (!MemIsReadable(vtbl, (VT_SetResolution / 4 + 1) * sizeof(void*), false)) {
            LogMessage("BackbufferResize: gfx vtable unreadable — skipping");
            return false;
        }
        void* pGetW  = vtbl[VT_GetBBWidth / 4];
        void* pGetH  = vtbl[VT_GetBBHeight / 4];
        void* pSet   = vtbl[VT_SetResolution / 4];
        void* pReset = vtbl[VT_ResetDevice / 4];
        if (!MemIsReadable(pGetW, 1, true) || !MemIsReadable(pGetH, 1, true) ||
            !MemIsReadable(pSet, 1, true)  || !MemIsReadable(pReset, 1, true)) {
            LogMessage("BackbufferResize: gfx vtable slots not executable — skipping (offsets stale on this build?)");
            return false;
        }

        int curW = ((PFN_GfxGetDim)pGetW)(gfx);
        int curH = ((PFN_GfxGetDim)pGetH)(gfx);
        if (curW == clientW && curH == clientH) {
            LogMessage("BackbufferResize: already %dx%d — no reset needed", curW, curH);
            return true;
        }

        LogMessage("BackbufferResize: %dx%d -> %dx%d (gfx SetResolution + ResetDevice)", curW, curH, clientW, clientH);
        ((PFN_GfxSetRes)pSet)(gfx, clientW, clientH, 32, 0);  // bpp=32, refresh=0 (windowed)
        char ok = ((PFN_GfxReset)pReset)(gfx, 0);
        if (ok == 0) {
            LogMessage("BackbufferResize: gfx ResetDevice returned FALSE — left to EQ's own recovery (NOT exiting)");
            result = false;
        } else {
            int newW = ((PFN_GfxGetDim)pGetW)(gfx);
            int newH = ((PFN_GfxGetDim)pGetH)(gfx);
            LogMessage("BackbufferResize: OK — backbuffer now %dx%d", newW, newH);
            result = (newW == clientW && newH == clientH);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LogMessage("BackbufferResize: SEH fault during gfx reset — host protected, no reset applied");
        result = false;
    }
    return result;
}

static LRESULT CALLBACK GeoWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    // Defensive: GeoWndProc can only be the window proc if EnsureGeoSubclass set
    // g_origGeoWndProc first, so this is never NULL in practice — but never
    // CallWindowProc(NULL).
    if (!g_origGeoWndProc)
        return DefWindowProcW(hWnd, msg, wParam, lParam);

    // v3.24.0 — Window-Mode toggle backbuffer resize. Lazily register the private
    // message id (idempotent; same string → same id as the C# host). We run on
    // EQ's main/UI thread between frames, so user32 + a synchronous gfx ResetDevice
    // are both safe here. The C# host PostMessages this AFTER the slim restyle has
    // settled, so GetClientRect now reports the NEW mode's client size; we rebuild
    // the DX backbuffer to match. Handled regardless of pin/enabled so the
    // Fullscreen (WS_POPUP) target gets it too, and we return WITHOUT chaining (EQ
    // has no handler for this registered id). Skip while minimized (0x0 client).
    if (g_backbufferResizeMsg == 0)
        g_backbufferResizeMsg = RegisterWindowMessageW(BACKBUFFER_RESIZE_MSG_NAME);
    if (g_backbufferResizeMsg != 0 && msg == g_backbufferResizeMsg) {
        if (IsIconic(hWnd)) {
            LogMessage("BackbufferResize: window minimized — skipping reset");
        } else {
            RECT rc;
            if (GetClientRect(hWnd, &rc))
                ApplyBackbufferResize(rc.right - rc.left, rc.bottom - rc.top);
        }
        return 0;
    }

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

// Install (or re-install after EQ recreated its window / overwrote its proc),
// then apply the slim transform in-process so the window is slim immediately.
// Called from the position detours AND HookedShowWindow on the EQ UI thread,
// under the recursive g_detourCs.
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

    // v3.22.82 — INSTANT in-process slim transform. Runs on every actual
    // (re)install (we only reach here past the `current == GeoWndProc` early
    // return) — most importantly the charselect→in-world recreation, where EQ's
    // new top-level window arrives NORMAL (WS_THICKFRAME, work-area sized,
    // taskbar visible). Applying the slim style + SHM target rect HERE — driven
    // earliest from HookedShowWindow — makes the window slim before its first
    // paint, instead of waiting up to a full C# guard tick (the "dance once
    // ingame" Nate flagged). GeoWndProc (installed just above) then pins the
    // rect per-message; the C# guard (ApplySlimTitlebarToAll) stays as pure
    // belt-and-suspenders recovery.
    //
    // Reads the LIVE SHM rect (monitor-relative geometry — already current for
    // this PID's monitor + mode; C# rewrites it only on a config/monitor
    // change). Uses the trampoline g_origSetWindowPos to bypass our own
    // SetWindowPos hook (no recursive detour body); the subclass still pins
    // synchronously via the WM_WINDOWPOSCHANGING this reposition dispatches to
    // GeoWndProc. The enclosing detour holds the recursive g_detourCs and
    // GeoWndProc itself takes no lock, so there is no reentrancy hazard.
    const volatile HookConfig* cfg = g_pConfig;
    if (!cfg) return;
    int enabled = cfg->enabled;
    int pin     = cfg->pinGeometry;
    int strip   = cfg->stripThickFrame;
    int tx = cfg->targetX, ty = cfg->targetY;
    int tw = cfg->targetW, th = cfg->targetH;
    if (!(enabled && pin && tw > 0 && th > 0)) return;

    if (strip) {
        LONG_PTR style = GetWindowLongPtr(hWnd, GWL_STYLE);
        if (style & WS_THICKFRAME) {
            style &= ~WS_THICKFRAME;
            SetWindowLongPtr(hWnd, GWL_STYLE, style);
        }
    }
    // SWP_FRAMECHANGED so the WS_THICKFRAME strip recalculates the non-client
    // area; SWP_NOZORDER | SWP_NOACTIVATE so a background re-slim never steals
    // focus / z-order from the user's active client. g_origSetWindowPos is
    // always non-NULL when reached from a detour (hooks installed first); the
    // guard degrades to "subclass installed, C# guard recovers position" only
    // in the impossible pre-install case.
    if (g_origSetWindowPos) {
        g_origSetWindowPos(hWnd, NULL, tx, ty, tw, th,
                           SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        LogMessage("GeoWndProc: applied instant slim transform (pos=(%d,%d) %dx%d strip=%d)", tx, ty, tw, th, strip);
    }
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

        // v3.22.81 — install the per-message geometry subclass (kills the
        // multi-monitor growth + sliver the C# guard timer raced into). Re-installs
        // transparently after EQ recreates its window. v3.24.0: install in BOTH
        // modes (was `if (cfg.pinGeometry)` = Windowed only) so the GeoWndProc
        // backbuffer-resize message handler is present for the Fullscreen target
        // too — a client launched in Fullscreen and never toggled to Windowed would
        // otherwise silently drop the resize PostMessage (no subclass = EQ's own
        // WndProc gets it and ignores the registered id). Geometry-pinning AND the
        // slim transform stay Windowed-gated inside GeoWndProc / EnsureGeoSubclass
        // (the latter returns right after install when !pin), so Fullscreen behavior
        // is unchanged except that the resize message is now handled.
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

        // Install the per-message geometry subclass in BOTH modes (v3.24.0; was
        // pinGeometry-gated). See HookedSetWindowPos for the full rationale — the
        // backbuffer-resize handler lives in GeoWndProc and must be present for the
        // Fullscreen target too; pinning/slim stay Windowed-gated.
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

    // v3.22.82 — INSTANT in-world re-slim. EQ reliably calls ShowWindow on the
    // top-level window it recreates at charselect→in-world (this hook already
    // exists to block EQ's self-minimize during that window's DX init). After
    // the real ShowWindow the new window is now visible, so IsEqWindow qualifies
    // it; EnsureGeoSubclass installs the GeoWndProc subclass AND applies the
    // slim style+rect in-process (see EnsureGeoSubclass). This is the earliest
    // reliable trigger — earlier than the SetWindowPos/MoveWindow detours and
    // far earlier than the C# guard tick — so the recreated window is slim
    // before its first paint (no guard-tick flash, no "dance once ingame").
    // No message pump runs between g_origShowWindow and here (same detour frame
    // on the EQ UI thread), so the NORMAL window never composites. v3.24.0:
    // install in BOTH modes (was `&& cfg.pinGeometry`) so GeoWndProc — and its
    // backbuffer-resize message handler — is present for Fullscreen-launched
    // clients too. The slim transform inside EnsureGeoSubclass stays pin-gated, so
    // a Fullscreen client gets the subclass installed but no Windowed restyle.
    if (IsEqWindow(hWnd) && ReadConfig(&cfg) && cfg.enabled) {
        EnsureGeoSubclass(hWnd);
    }

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
