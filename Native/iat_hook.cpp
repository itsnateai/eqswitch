// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

// iat_hook.cpp — IAT patching for keyboard state and window focus queries
// Hooks GetAsyncKeyState, GetKeyState, GetKeyboardState (return synthetic state)
// and GetForegroundWindow, GetFocus, GetActiveWindow (return EQ HWND when active).
//
// Only patches eqgame.exe's IAT — our DLL's imports are unaffected,
// so our own calls to these functions still get the real results.

#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <string.h>
// _ReturnAddress() — MSVC intrinsic; GCC/MinGW uses __builtin_return_address(0)
#ifdef _MSC_VER
#include <intrin.h>
#define GET_RETURN_ADDRESS() _ReturnAddress()
#else
#define GET_RETURN_ADDRESS() __builtin_return_address(0)
#endif
#include "iat_hook.h"
#include "key_shm.h"

void DI8Log(const char *fmt, ...);

// Defined in device_proxy.cpp
HWND GetEqHwnd();

// --- eqgame.exe address range for caller checks ---
// Inline hooks patch user32.dll globally (process-wide). Without a caller
// check, Discord overlay, Steam, audio drivers, etc. all get fake results.
// We only want to fake focus for calls originating from eqgame.exe code.
static uintptr_t g_eqBase = 0;
static uintptr_t g_eqEnd = 0;

static void ComputeEqImageRange() {
    HMODULE base = GetModuleHandleW(nullptr); // eqgame.exe
    if (!base) return;
    const uint8_t *p = (const uint8_t *)base;

    // Validate DOS header magic
    if (*(uint16_t *)p != 0x5A4D) {  // "MZ"
        DI8Log("iat_hook: invalid DOS header — ComputeEqImageRange aborted");
        return;
    }

    int32_t eLfanew = *(int32_t *)(p + 0x3C);
    // Sanity: e_lfanew should point into the first 4KB (typical range 0x80-0x200)
    if (eLfanew < 0x40 || eLfanew > 0x1000) {
        DI8Log("iat_hook: e_lfanew=0x%X out of sane range — aborted", eLfanew);
        return;
    }

    // Validate PE signature ("PE\0\0")
    if (*(uint32_t *)(p + eLfanew) != 0x00004550) {
        DI8Log("iat_hook: invalid PE signature at e_lfanew=0x%X — aborted", eLfanew);
        return;
    }

    // PE32 optional header starts at eLfanew + 24; SizeOfImage is at offset 56
    uint32_t sizeOfImage = *(uint32_t *)(p + eLfanew + 24 + 56);
    // Sanity: eqgame.exe is typically 4-64 MB; reject >256 MB to prevent wrap-around
    if (sizeOfImage == 0 || sizeOfImage > 0x10000000) {
        DI8Log("iat_hook: SizeOfImage=0x%X looks wrong — aborted", sizeOfImage);
        return;
    }

    g_eqBase = (uintptr_t)base;
    g_eqEnd = g_eqBase + sizeOfImage;
    DI8Log("iat_hook: eqgame.exe range 0x%08X-0x%08X (%u KB)",
           (unsigned)g_eqBase, (unsigned)g_eqEnd, sizeOfImage / 1024);
}

static __forceinline bool IsCallerInEq(void *retAddr) {
    uintptr_t a = (uintptr_t)retAddr;
    return a >= g_eqBase && a < g_eqEnd;
}

// --- Original function pointers ---

typedef SHORT(WINAPI *PFN_GetAsyncKeyState)(int vKey);
typedef SHORT(WINAPI *PFN_GetKeyState)(int nVirtKey);
typedef BOOL(WINAPI *PFN_GetKeyboardState)(PBYTE lpKeyState);
typedef HWND(WINAPI *PFN_GetForegroundWindow)();
typedef HWND(WINAPI *PFN_GetFocus)();
typedef HWND(WINAPI *PFN_GetActiveWindow)();
typedef LRESULT(WINAPI *PFN_DefWindowProcA)(HWND, UINT, WPARAM, LPARAM);

static PFN_GetAsyncKeyState   g_realGetAsyncKeyState   = nullptr;
static PFN_GetKeyState        g_realGetKeyState        = nullptr;
static PFN_GetKeyboardState   g_realGetKeyboardState   = nullptr;
static PFN_GetForegroundWindow g_realGetForegroundWindow = nullptr;
static PFN_GetFocus           g_realGetFocus           = nullptr;
static PFN_GetActiveWindow    g_realGetActiveWindow    = nullptr;
static PFN_DefWindowProcA     g_realDefWindowProcA     = nullptr;

// --- Hook implementations ---

// Diagnostic: log first N calls per active session to identify what EQ queries during login
static volatile int g_diagHitCount = 0;
static volatile int g_diagMissCount = 0;
static volatile bool g_diagWasActive = false;

// Phantom-keys hotfix v2: forward-decl. Body lives below after
// g_ntGetForegroundWindow is declared. Returns true iff EQ's HWND is the
// true OS foreground (using the win32u.dll syscall wrapper, which bypasses
// our own GetForegroundWindow hook that spoofs EQ when SHM is active).
static bool EqIsTrueForeground();

static SHORT WINAPI HookedGetAsyncKeyState(int vKey) {
    bool active = KeyShm::IsActive();
    // Reset counters on active edge (new login session)
    if (active && !g_diagWasActive) {
        g_diagHitCount = 0;
        g_diagMissCount = 0;
        DI8Log("iat_hook: === LOGIN SESSION START — GetAsyncKeyState logging enabled ===");
    }
    g_diagWasActive = active;

    if (vKey >= 0 && vKey <= 255) {
        UINT scan = MapVirtualKeyW(vKey, MAPVK_VK_TO_VSC);
        if (scan > 0 && scan < 256 && KeyShm::IsKeyPressed((uint8_t)scan)) {
            if (active && g_diagHitCount < 200) {
                g_diagHitCount++;
                DI8Log("iat_hook: GetAsyncKeyState HIT vk=0x%02X scan=0x%02X → 0x8001", vKey, scan);
            }
            return (SHORT)0x8001;
        }
        // Log misses too (first 50 per session) to see what EQ polls
        if (active && g_diagMissCount < 50) {
            g_diagMissCount++;
            DI8Log("iat_hook: GetAsyncKeyState MISS vk=0x%02X scan=0x%02X", vKey, scan);
        }
    }
    // Phantom-keys hotfix v2: physical-key pass-through requires EQ to be the
    // TRUE foreground window. Otherwise the real GetAsyncKeyState reads global
    // OS keyboard state and Nate's typing in other windows bleeds into EQ.
    if (!EqIsTrueForeground()) return 0;
    return g_realGetAsyncKeyState ? g_realGetAsyncKeyState(vKey) : 0;
}

static SHORT WINAPI HookedGetKeyState(int nVirtKey) {
    if (nVirtKey >= 0 && nVirtKey <= 255) {
        UINT scan = MapVirtualKeyW(nVirtKey, MAPVK_VK_TO_VSC);
        if (scan > 0 && scan < 256 && KeyShm::IsKeyPressed((uint8_t)scan))
            return (SHORT)0x8001;
    }
    // Phantom-keys hotfix v2: see HookedGetAsyncKeyState.
    if (!EqIsTrueForeground()) return 0;
    return g_realGetKeyState ? g_realGetKeyState(nVirtKey) : 0;
}

static BOOL WINAPI HookedGetKeyboardState(PBYTE lpKeyState) {
    BOOL ok = g_realGetKeyboardState ? g_realGetKeyboardState(lpKeyState) : FALSE;
    if (lpKeyState) {
        // Phantom-keys hotfix v2: if EQ isn't truly focused, zero the physical
        // key buffer BEFORE injecting SHM state. Otherwise Nate's typing in
        // other windows bleeds through this API into EQ's game logic.
        if (!EqIsTrueForeground())
            memset(lpKeyState, 0, 256);
        for (int vk = 0; vk <= 255; vk++) {
            UINT scan = MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
            if (scan > 0 && scan < 256 && KeyShm::IsKeyPressed((uint8_t)scan))
                lpKeyState[vk] |= 0x80;
        }
    }
    return ok;
}

static HWND WINAPI HookedGetForegroundWindow() {
    HWND hwnd = GetEqHwnd();
    if (hwnd && KeyShm::IsActive())
        return hwnd;
    return g_realGetForegroundWindow ? g_realGetForegroundWindow() : nullptr;
}

static HWND WINAPI HookedGetFocus() {
    HWND hwnd = GetEqHwnd();
    if (hwnd && KeyShm::IsActive())
        return hwnd;
    return g_realGetFocus ? g_realGetFocus() : nullptr;
}

static HWND WINAPI HookedGetActiveWindow() {
    HWND hwnd = GetEqHwnd();
    if (hwnd && KeyShm::IsActive())
        return hwnd;
    return g_realGetActiveWindow ? g_realGetActiveWindow() : nullptr;
}

// --- IAT patch tracking (for removal on detach) ---

struct IatPatchEntry { uint32_t *slot; uint32_t original; };
static IatPatchEntry g_patches[12];
static int g_patchCount = 0;

// --- IAT patching engine (x86 PE32 only) ---

// Patch a single IAT entry. Returns original function pointer, or nullptr on failure.
static void *PatchIat(const uint8_t *base, const char *targetDll,
                      const char *targetFn, void *newFn)
{
    // PE header navigation
    int32_t eLfanew = *(int32_t *)(base + 0x3C);
    const uint8_t *ntHeaders = base + eLfanew;
    const uint8_t *optHeader = ntHeaders + 24;

    uint16_t magic = *(uint16_t *)optHeader;
    if (magic != 0x010B) return nullptr; // PE32 only (eqgame.exe is x86)

    // Import directory is data directory entry #1
    // PE32: data directories start at optHeader + 96, each entry is 8 bytes
    uint32_t importDirRva = *(uint32_t *)(optHeader + 104);
    if (importDirRva == 0) return nullptr;

    // Walk IMAGE_IMPORT_DESCRIPTOR array (each is 20 bytes)
    const uint8_t *desc = base + importDirRva;
    while (true) {
        uint32_t nameRva = *(uint32_t *)(desc + 12);
        if (nameRva == 0) break;

        const char *dllName = (const char *)(base + nameRva);
        if (_stricmp(dllName, targetDll) == 0) {
            uint32_t origFirstThunk = *(uint32_t *)desc;        // OriginalFirstThunk
            uint32_t firstThunkRva  = *(uint32_t *)(desc + 16); // FirstThunk (IAT)

            // Bound imports have OriginalFirstThunk == 0 — no hint/name table to walk
            if (origFirstThunk == 0) { desc += 20; continue; }

            const uint32_t *orig = (const uint32_t *)(base + origFirstThunk);
            uint32_t *thunk = (uint32_t *)(base + firstThunkRva);

            while (*orig) {
                // Skip ordinal imports (high bit set)
                if ((*orig & 0x80000000) == 0) {
                    // Hint/Name table entry: 2-byte hint + null-terminated name
                    const char *fnName = (const char *)(base + *orig + 2);
                    if (strcmp(fnName, targetFn) == 0) {
                        // Check table space BEFORE patching — unrecorded patches can't be restored
                        if (g_patchCount >= 12) {
                            DI8Log("iat_hook: patch table full, skipping %s (would be unrestorable)", targetFn);
                            return nullptr;
                        }
                        void *original = (void *)(uintptr_t)*thunk;
                        DWORD oldProtect, dummy;
                        VirtualProtect(thunk, 4, PAGE_READWRITE, &oldProtect);
                        *thunk = (uint32_t)(uintptr_t)newFn;
                        VirtualProtect(thunk, 4, oldProtect, &dummy);
                        g_patches[g_patchCount].slot = thunk;
                        g_patches[g_patchCount].original = (uint32_t)(uintptr_t)original;
                        g_patchCount++;
                        return original;
                    }
                }
                orig++;
                thunk++;
            }
        }
        desc += 20;
    }
    return nullptr;
}

// --- Inline hook infrastructure (patches function body, not just IAT) ---
// Catches calls via GetProcAddress, delay-loaded imports, and any other resolution path.
// Uses NtUser* from win32u.dll as the "real" implementation to avoid trampolines.

// Track inline patches for cleanup
struct InlinePatchEntry {
    uint8_t *addr;      // patched function address
    uint8_t  original[8]; // saved original bytes
    int      patchLen;  // number of bytes patched (7 for x86 MOV+JMP)
};
static InlinePatchEntry g_inlinePatches[4];
static int g_inlinePatchCount = 0;

// Real NtUser functions (from win32u.dll) — bypass our inline patches
typedef HWND (WINAPI *PFN_NtUserGetForegroundWindow)();
typedef HWND (WINAPI *PFN_NtUserGetFocus)();
typedef HWND (WINAPI *PFN_NtUserGetActiveWindow)(); // actually GetThreadState-based

static PFN_NtUserGetForegroundWindow g_ntGetForegroundWindow = nullptr;
static PFN_NtUserGetFocus g_ntGetFocus = nullptr;

// Phantom-keys hotfix v2: true-foreground check that bypasses our own
// GetForegroundWindow hook (which spoofs EQ when SHM active). Uses the
// win32u.dll syscall wrapper captured at InstallInlineHooks time. Permissive
// if unknown (early DLL init before ptr/HWND set) to keep the happy path alive.
static bool EqIsTrueForeground() {
    HWND eqHwnd = GetEqHwnd();
    if (!eqHwnd || !g_ntGetForegroundWindow) return true;
    HWND trueFg = g_ntGetForegroundWindow();
    return trueFg == eqHwnd;
}

// Inline-hooked replacements: these get called regardless of how EQ resolved the function.
// They use the same logic as the IAT hooks but call NtUser* as fallback.

static volatile int g_inlineGfwLogCount = 0;

static HWND WINAPI InlineHookedGetForegroundWindow() {
    HWND hwnd = GetEqHwnd();
    bool active = KeyShm::IsActive();

    // When SHM is active (auto-login in progress), return EQ's HWND for ALL
    // callers — not just eqgame.exe. EQ's game loop may call GetForegroundWindow
    // from loaded DLLs (not just the main exe), and the old IsCallerInEq check
    // rejected those calls, causing EQ to see itself as backgrounded.
    if (hwnd && active) {
        int count = InterlockedIncrement((volatile LONG*)&g_inlineGfwLogCount);
        if (count <= 20)
            DI8Log("inline_gfw: returning hwnd=0x%X (active, caller=0x%X) #%d",
                   (unsigned)(uintptr_t)hwnd,
                   (unsigned)(uintptr_t)GET_RETURN_ADDRESS(), count);
        return hwnd;
    }

    // SHM inactive — only fake for eqgame.exe callers (protect Discord etc.)
    if (!IsCallerInEq(GET_RETURN_ADDRESS()))
        return g_ntGetForegroundWindow ? g_ntGetForegroundWindow() : nullptr;

    return g_ntGetForegroundWindow ? g_ntGetForegroundWindow() : nullptr;
}

static HWND WINAPI InlineHookedGetFocus() {
    HWND hwnd = GetEqHwnd();
    if (hwnd && KeyShm::IsActive())
        return hwnd;

    if (!IsCallerInEq(GET_RETURN_ADDRESS()))
        return g_ntGetFocus ? g_ntGetFocus() : nullptr;

    return g_ntGetFocus ? g_ntGetFocus() : nullptr;
}

static HWND WINAPI InlineHookedGetActiveWindow() {
    HWND hwnd = GetEqHwnd();
    if (hwnd && KeyShm::IsActive())
        return hwnd;

    if (!IsCallerInEq(GET_RETURN_ADDRESS()))
        return g_ntGetForegroundWindow ? g_ntGetForegroundWindow() : nullptr;

    return g_ntGetForegroundWindow ? g_ntGetForegroundWindow() : nullptr;
}

// Patch a function's first bytes with MOV EAX, <hook>; JMP EAX (7 bytes, x86)
static bool InstallInlineHook(const char *fnName, uint8_t *target, void *hook) {
    if (!target || !hook) return false;

    // Save original bytes
    if (g_inlinePatchCount >= 4) {
        DI8Log("inline: patch table full, skipping %s", fnName);
        return false;
    }

    InlinePatchEntry &entry = g_inlinePatches[g_inlinePatchCount];
    entry.addr = target;
    entry.patchLen = 7;
    memcpy(entry.original, target, 7);

    // Make the page writable
    DWORD oldProtect, dummy;
    if (!VirtualProtect(target, 7, PAGE_EXECUTE_READWRITE, &oldProtect)) {
        DI8Log("inline: VirtualProtect failed for %s (err=%lu)", fnName, GetLastError());
        return false;
    }

    // Write: MOV EAX, <hook_addr32>; JMP EAX
    target[0] = 0xB8; // MOV EAX, imm32
    *(uint32_t *)(target + 1) = (uint32_t)(uintptr_t)hook;
    target[5] = 0xFF; // JMP EAX
    target[6] = 0xE0;

    VirtualProtect(target, 7, oldProtect, &dummy);

    // Flush instruction cache so CPU sees new code
    FlushInstructionCache(GetCurrentProcess(), target, 7);

    g_inlinePatchCount++;
    DI8Log("inline: patched %s at %p", fnName, target);
    return true;
}

static void InstallInlineHooks() {
    // win32u.dll is always loaded in any modern Win32 process (it contains syscall stubs).
    // Use GetModuleHandle to avoid bumping the refcount — LoadLibraryA would leak a ref.
    HMODULE win32u = GetModuleHandleA("win32u.dll");
    if (!win32u) {
        DI8Log("inline: win32u.dll not found — inline hooks unavailable");
        return;
    }

    // Resolve NtUser real implementations
    g_ntGetForegroundWindow = (PFN_NtUserGetForegroundWindow)
        GetProcAddress(win32u, "NtUserGetForegroundWindow");
    if (!g_ntGetForegroundWindow)
        DI8Log("inline: NtUserGetForegroundWindow not found in win32u.dll");

    g_ntGetFocus = (PFN_NtUserGetFocus)
        GetProcAddress(win32u, "NtUserGetFocus");

    // Resolve user32.dll functions to patch
    HMODULE user32 = GetModuleHandleA("user32.dll");
    if (!user32) {
        DI8Log("inline: user32.dll not loaded?!");
        return;
    }

    // Inline hook GetForegroundWindow — THE critical hook
    uint8_t *gfwAddr = (uint8_t *)GetProcAddress(user32, "GetForegroundWindow");
    if (gfwAddr && g_ntGetForegroundWindow) {
        InstallInlineHook("GetForegroundWindow", gfwAddr, (void *)InlineHookedGetForegroundWindow);
    } else {
        DI8Log("inline: cannot hook GetForegroundWindow (addr=%p ntReal=%p)", gfwAddr, g_ntGetForegroundWindow);
    }

    // Inline hook GetFocus — IAT hook failed (not in EQ's import table),
    // but EQ may call it via GetProcAddress
    uint8_t *focusAddr = (uint8_t *)GetProcAddress(user32, "GetFocus");
    if (focusAddr && g_ntGetFocus) {
        InstallInlineHook("GetFocus", focusAddr, (void *)InlineHookedGetFocus);
    }

    // Inline hook GetActiveWindow — also missing from IAT
    uint8_t *activeAddr = (uint8_t *)GetProcAddress(user32, "GetActiveWindow");
    if (activeAddr) {
        InstallInlineHook("GetActiveWindow", activeAddr, (void *)InlineHookedGetActiveWindow);
    }

    DI8Log("inline: %d function(s) patched", g_inlinePatchCount);
}

static void RemoveInlineHooks() {
    for (int i = 0; i < g_inlinePatchCount; i++) {
        InlinePatchEntry &entry = g_inlinePatches[i];
        DWORD oldProtect, dummy;
        if (VirtualProtect(entry.addr, entry.patchLen, PAGE_EXECUTE_READWRITE, &oldProtect)) {
            memcpy(entry.addr, entry.original, entry.patchLen);
            VirtualProtect(entry.addr, entry.patchLen, oldProtect, &dummy);
            FlushInstructionCache(GetCurrentProcess(), entry.addr, entry.patchLen);
        }
    }
    DI8Log("inline: restored %d function(s)", g_inlinePatchCount);
    g_inlinePatchCount = 0;
}

// --- Public API ---

void IatHook::InstallKeyboardHooks() {
    HMODULE base = GetModuleHandleW(nullptr); // eqgame.exe
    if (!base) {
        DI8Log("iat_hook: GetModuleHandleW failed");
        return;
    }
    const uint8_t *basePtr = (const uint8_t *)base;

    // Compute eqgame.exe's address range for inline hook caller checks (SF12).
    // If this fails, inline hooks install but silently pass through all calls.
    ComputeEqImageRange();
    if (g_eqBase == 0 || g_eqEnd == 0)
        DI8Log("iat_hook: WARNING — eqgame.exe range unknown, inline hooks will pass through all callers");

    int hooked = 0;

    // Keyboard state hooks (user32.dll)
    void *p;

    p = PatchIat(basePtr, "user32.dll", "GetAsyncKeyState", (void *)HookedGetAsyncKeyState);
    if (p) { g_realGetAsyncKeyState = (PFN_GetAsyncKeyState)p; hooked++; DI8Log("iat_hook: hooked GetAsyncKeyState"); }

    p = PatchIat(basePtr, "user32.dll", "GetKeyState", (void *)HookedGetKeyState);
    if (p) { g_realGetKeyState = (PFN_GetKeyState)p; hooked++; DI8Log("iat_hook: hooked GetKeyState"); }

    p = PatchIat(basePtr, "user32.dll", "GetKeyboardState", (void *)HookedGetKeyboardState);
    if (p) { g_realGetKeyboardState = (PFN_GetKeyboardState)p; hooked++; DI8Log("iat_hook: hooked GetKeyboardState"); }

    // Window focus hooks — try user32.dll, then apiset redirect
    p = PatchIat(basePtr, "user32.dll", "GetForegroundWindow", (void *)HookedGetForegroundWindow);
    if (!p) p = PatchIat(basePtr, "api-ms-win-ntuser-ia-l1-1-0.dll", "GetForegroundWindow", (void *)HookedGetForegroundWindow);
    if (p) { g_realGetForegroundWindow = (PFN_GetForegroundWindow)p; hooked++; DI8Log("iat_hook: hooked GetForegroundWindow"); }
    else DI8Log("iat_hook: FAILED GetForegroundWindow — background input may not work");

    p = PatchIat(basePtr, "user32.dll", "GetFocus", (void *)HookedGetFocus);
    if (p) { g_realGetFocus = (PFN_GetFocus)p; hooked++; DI8Log("iat_hook: hooked GetFocus"); }

    p = PatchIat(basePtr, "user32.dll", "GetActiveWindow", (void *)HookedGetActiveWindow);
    if (p) { g_realGetActiveWindow = (PFN_GetActiveWindow)p; hooked++; DI8Log("iat_hook: hooked GetActiveWindow"); }

    DI8Log("iat_hook: %d IAT function(s) hooked", hooked);

    // Install inline hooks on function bodies — catches GetProcAddress calls
    // that bypass the IAT. This is THE critical fix for background input.
    InstallInlineHooks();
}

void IatHook::RemoveKeyboardHooks() {
    // Remove inline hooks FIRST (patches function bodies in user32.dll)
    RemoveInlineHooks();
    for (int i = 0; i < g_patchCount; i++) {
        DWORD oldProtect, dummy;
        VirtualProtect(g_patches[i].slot, 4, PAGE_READWRITE, &oldProtect);
        *g_patches[i].slot = g_patches[i].original;
        VirtualProtect(g_patches[i].slot, 4, oldProtect, &dummy);
    }
    DI8Log("iat_hook: restored %d IAT entries", g_patchCount);
    g_patchCount = 0;
}
