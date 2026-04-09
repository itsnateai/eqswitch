// device_proxy.cpp — IDirectInputDevice8 COM proxy implementation
// Keyboard interception: SetCooperativeLevel (BACKGROUND|NONEXCLUSIVE),
// GetDeviceState (inject keys), GetDeviceData (synthetic events),
// SetEventNotification (signal on key changes), Acquire (fake success).
// WM_ACTIVATEAPP thread tricks EQ into processing input while backgrounded.

#define _CRT_SECURE_NO_WARNINGS
#include "device_proxy.h"
#include "key_shm.h"
#include <string.h>

void DI8Log(const char *fmt, ...);

// --- Global state (shared across all device instances) ---

static volatile HWND g_eqHwnd = nullptr;
static volatile HANDLE g_kbEventHandle = nullptr;
static bool g_activateThreadStarted = false;
static bool g_shmThreadStarted = false;
static DWORD g_originalCoopFlags = 0;
static bool g_coopSwitched = false;
static IDirectInputDevice8W *g_realKeyboardDevice = nullptr;
static volatile bool g_shutdown = false;
static HANDLE g_hActivateThread = nullptr;
static HANDLE g_hShmThread = nullptr;

HWND GetEqHwnd() { return g_eqHwnd; }

// --- WM_ACTIVATEAPP defense (Phase 2a) ---
// Posts WM_ACTIVATEAPP(TRUE) when SHM goes active, tricking EQ's main loop
// into calling keyboard_process for background windows.
//
// NOTE: Message-level approaches (subclassing, WH_CALLWNDPROC/RET, DefWindowProcA
// IAT hook, SendMessage, SetActiveWindow/SetFocus) have all been tested and FAIL
// to make EQ process input when backgrounded. EQ's main loop checks an internal
// g_bActive flag and stops calling GetDeviceData entirely when inactive.
// The next approach is binary scanning to find and patch g_bActive directly.
// For now, this code keeps the simple PostMessage approach that works for
// foreground and doesn't interfere with normal operation.

static DWORD WINAPI ActivateThread(LPVOID) {
    bool wasActive = false;
    int ticksSinceRepost = 0;
    while (!g_shutdown) {
        Sleep(16); // ~60Hz
        bool active = KeyShm::IsActive();
        HWND hwnd = g_eqHwnd;

        if (active && hwnd) {
            if (!wasActive) {
                // Rising edge: deferred cooperative level switch to BACKGROUND
                if (!g_coopSwitched && g_realKeyboardDevice) {
                    g_realKeyboardDevice->Unacquire();
                    DWORD bgFlags = (g_originalCoopFlags & ~(DISCL_EXCLUSIVE | DISCL_FOREGROUND))
                                  | DISCL_NONEXCLUSIVE | DISCL_BACKGROUND;
                    HRESULT hr = g_realKeyboardDevice->SetCooperativeLevel(hwnd, bgFlags);
                    HRESULT acqHr = g_realKeyboardDevice->Acquire();
                    g_coopSwitched = true;
                    DI8Log("wm_activate: Unacquire → SetCoopLevel(BACKGROUND|NONEXCLUSIVE)=0x%08X → Acquire=0x%08X",
                           (unsigned)hr, (unsigned)acqHr);
                }
                PostMessageW(hwnd, WM_ACTIVATEAPP, TRUE, 0);
                DI8Log("wm_activate: posted WM_ACTIVATEAPP(1) hwnd=0x%X",
                       (unsigned)(uintptr_t)hwnd);
                ticksSinceRepost = 0;
            }

            // Re-post activation every ~200ms when EQ is not foreground
            ticksSinceRepost++;
            if (ticksSinceRepost >= 12) {
                ticksSinceRepost = 0;
                HWND fg = GetForegroundWindow();
                if (fg != hwnd) {
                    PostMessageW(hwnd, WM_ACTIVATEAPP, TRUE, 0);
                }
            }
        } else if (!active && wasActive && hwnd) {
            HWND fg = GetForegroundWindow();
            if (fg != hwnd) {
                PostMessageW(hwnd, WM_ACTIVATEAPP, FALSE, 0);
                DI8Log("wm_activate: posted WM_ACTIVATEAPP(0) hwnd=0x%X",
                       (unsigned)(uintptr_t)hwnd);
            }
            ticksSinceRepost = 0;
        }
        wasActive = active;
    }
    return 0;
}

static void StartActivateThread() {
    if (g_activateThreadStarted) return;
    g_hActivateThread = CreateThread(nullptr, 0, ActivateThread, nullptr, 0, nullptr);
    if (g_hActivateThread)
        g_activateThreadStarted = true;
    else
        DI8Log("StartActivateThread: CreateThread failed (%lu)", GetLastError());
}

// Signal background threads to exit and wait for them before releasing resources.
// Safe to wait here because Cleanup() in eqswitch-di8.cpp calls this AFTER
// WaitForSingleObject on the init thread — the loader lock is NOT held.
// On process exit (DLL_PROCESS_DETACH with reserved != NULL), Cleanup is
// skipped entirely — the OS reclaims everything.
extern "C" void DeviceProxy_Shutdown() {
    g_shutdown = true;
    // Wait for threads to observe g_shutdown and exit before releasing SHM.
    // ActivateThread sleeps 16ms, ShmPollingThread sleeps 8ms — 100ms is plenty.
    if (g_hActivateThread) {
        WaitForSingleObject(g_hActivateThread, 100);
        CloseHandle(g_hActivateThread);
        g_hActivateThread = nullptr;
    }
    if (g_hShmThread) {
        WaitForSingleObject(g_hShmThread, 50);
        CloseHandle(g_hShmThread);
        g_hShmThread = nullptr;
    }
    KeyShm::Close(); // now safe — threads have stopped
}

// --- SHM polling thread (Phase 3) ---
// Signals the keyboard event handle when synthetic keys change,
// waking EQ's DirectInput polling loop.

static DWORD WINAPI ShmPollingThread(LPVOID) {
    bool prevAnyKeys = false;
    while (!g_shutdown) {
        Sleep(8); // ~120Hz
        uint8_t keys[256];
        bool active = KeyShm::ReadKeys(keys);
        bool anyKeys = false;
        if (active) {
            for (int i = 0; i < 256; i++) {
                if (keys[i]) { anyKeys = true; break; }
            }
        }
        // Signal on press or release transitions
        if (anyKeys || (prevAnyKeys && !anyKeys)) {
            HANDLE h = g_kbEventHandle;
            if (h) SetEvent(h);
        }
        prevAnyKeys = anyKeys;
    }
    return 0;
}

static void StartShmPollingThread() {
    if (g_shmThreadStarted) return;
    g_hShmThread = CreateThread(nullptr, 0, ShmPollingThread, nullptr, 0, nullptr);
    if (g_hShmThread)
        g_shmThreadStarted = true;
    else
        DI8Log("StartShmPollingThread: CreateThread failed (%lu)", GetLastError());
}

// --- Constructor ---

DeviceProxy::DeviceProxy(void *real, bool isKeyboard)
    : m_real(reinterpret_cast<IDirectInputDevice8W *>(real))
    , m_refCount(1)
    , m_isKeyboard(isKeyboard)
    , m_synthSequence(0x80000000)
{
    memset(m_prevShmKeys, 0, sizeof(m_prevShmKeys));
    m_real->AddRef();
    if (isKeyboard)
        g_realKeyboardDevice = m_real;
}

// --- IUnknown -----------------------------------------------------------

HRESULT STDMETHODCALLTYPE DeviceProxy::QueryInterface(REFIID riid, void **ppv) {
    HRESULT hr = m_real->QueryInterface(riid, ppv);
    if (SUCCEEDED(hr)) {
        reinterpret_cast<IUnknown *>(*ppv)->Release();
        InterlockedIncrement(&m_refCount);
        *ppv = this;
    }
    return hr;
}

ULONG STDMETHODCALLTYPE DeviceProxy::AddRef() {
    return InterlockedIncrement(&m_refCount);
}

ULONG STDMETHODCALLTYPE DeviceProxy::Release() {
    LONG count = InterlockedDecrement(&m_refCount);
    if (count == 0) {
        m_real->Release();
        delete this;
        return 0;
    }
    return count;
}

// --- Intercepted methods ------------------------------------------------

HRESULT STDMETHODCALLTYPE DeviceProxy::Acquire() {
    HRESULT hr = m_real->Acquire();
    // When shm is active, fake success even if real Acquire fails
    // (BACKGROUND mode may fail to acquire in some edge cases)
    if (FAILED(hr) && m_isKeyboard && KeyShm::IsActive())
        return DI_OK;
    return hr;
}

// Diagnostic: log injection activity during login
static volatile int g_gdsLogCount = 0;
static volatile bool g_gdsWasActive = false;
static volatile int g_gdsCallCount = 0; // total calls for detecting if method is used

HRESULT STDMETHODCALLTYPE DeviceProxy::GetDeviceState(DWORD cbData, LPVOID lpvData) {
    HRESULT hr = m_real->GetDeviceState(cbData, lpvData);

    if (m_isKeyboard) {
        bool active = KeyShm::IsActive();
        int calls = InterlockedIncrement((volatile LONG*)&g_gdsCallCount);
        if (active && !g_gdsWasActive) {
            g_gdsLogCount = 0;
            DI8Log("GetDeviceState: === SHM active, injection enabled (callCount=%d) ===", calls);
        }
        // Periodic call counter to detect if method is used at all
        if (active && (calls % 500 == 0))
            DI8Log("GetDeviceState: heartbeat callCount=%d hr=0x%08X", calls, (unsigned)hr);
        g_gdsWasActive = active;

        DWORD kbLen = (cbData > 256) ? 256 : cbData;
        if (SUCCEEDED(hr)) {
            if (KeyShm::ShouldSuppress())
                memset(lpvData, 0, kbLen);
            bool injected = KeyShm::InjectKeys((uint8_t *)lpvData, kbLen);
            if (injected && g_gdsLogCount < 100) {
                g_gdsLogCount++;
                // Log which scan codes are being injected
                for (DWORD i = 0; i < kbLen; i++) {
                    if (((uint8_t *)lpvData)[i] & 0x80)
                        DI8Log("GetDeviceState: scan 0x%02X=0x80 (injected) hr=0x%08X", i, (unsigned)hr);
                }
            }
        } else {
            memset(lpvData, 0, kbLen);
            bool injected = KeyShm::InjectKeys((uint8_t *)lpvData, kbLen);
            if (injected) {
                if (g_gdsLogCount < 100) {
                    g_gdsLogCount++;
                    DI8Log("GetDeviceState: device FAILED (0x%08X) but injected synthetic keys", (unsigned)hr);
                }
                return DI_OK;
            }
        }
    }
    return hr;
}

// Diagnostic: log GetDeviceData injection activity during login
static volatile int g_gddLogCount = 0;
static volatile bool g_gddWasActive = false;
static volatile int g_gddCallCount = 0;

HRESULT STDMETHODCALLTYPE DeviceProxy::GetDeviceData(
    DWORD cbObjectData, LPDIDEVICEOBJECTDATA rgdod,
    LPDWORD pdwInOut, DWORD dwFlags)
{
    if (!m_isKeyboard)
        return m_real->GetDeviceData(cbObjectData, rgdod, pdwInOut, dwFlags);

    DWORD originalCapacity = pdwInOut ? *pdwInOut : 0;
    bool peek = (dwFlags & DIGDD_PEEK) != 0;

    HRESULT hr = m_real->GetDeviceData(cbObjectData, rgdod, pdwInOut, dwFlags);
    DWORD realCount = SUCCEEDED(hr) ? *pdwInOut : 0;

    bool shmIsActive = KeyShm::IsActive();
    int calls = InterlockedIncrement((volatile LONG*)&g_gddCallCount);
    if (shmIsActive && !g_gddWasActive) {
        g_gddLogCount = 0;
        DI8Log("GetDeviceData: === SHM active, injection enabled (callCount=%d, hr=0x%08X, cap=%lu) ===",
               calls, (unsigned)hr, originalCapacity);
    }
    // Periodic heartbeat to detect if method is used at all
    if (shmIsActive && (calls % 500 == 0))
        DI8Log("GetDeviceData: heartbeat callCount=%d hr=0x%08X realCount=%lu", calls, (unsigned)hr, realCount);
    g_gddWasActive = shmIsActive;

    if (KeyShm::ShouldSuppress() && realCount > 0)
        realCount = 0;

    // Read current synthetic key state
    uint8_t curKeys[256];
    bool shmActive = KeyShm::ReadKeys(curKeys);

    if (!shmActive) {
        if (!peek) memset(m_prevShmKeys, 0, 256);
        *pdwInOut = realCount;
        return hr;
    }

    // Detect changes since last non-peek read
    struct Change { uint8_t scan; uint8_t value; };
    Change changes[256];
    int numChanges = 0;
    for (int i = 0; i < 256; i++) {
        if (m_prevShmKeys[i] != curKeys[i]) {
            changes[numChanges].scan = (uint8_t)i;
            changes[numChanges].value = curKeys[i];
            numChanges++;
        }
    }

    if (numChanges == 0) {
        *pdwInOut = realCount;
        return SUCCEEDED(hr) ? hr : DI_OK; // keep device "alive" for background windows
    }

    // NULL rgdod = just querying count
    if (!rgdod) {
        *pdwInOut = realCount + (DWORD)numChanges;
        if (!peek) memcpy(m_prevShmKeys, curKeys, 256);
        return DI_OK;
    }

    // Guard against undersized object data structs (e.g. legacy DX3 callers)
    if (cbObjectData < sizeof(DIDEVICEOBJECTDATA)) {
        *pdwInOut = realCount;
        if (!peek) memcpy(m_prevShmKeys, curKeys, 256);
        return hr;
    }

    // Inject synthetic events into the buffer
    DWORD available = (originalCapacity > realCount) ? originalCapacity - realCount : 0;
    DWORD toInject = ((DWORD)numChanges < available) ? (DWORD)numChanges : available;
    DWORD timestamp = GetTickCount();

    uint8_t *bufStart = (uint8_t *)rgdod;
    for (DWORD j = 0; j < toInject; j++) {
        DIDEVICEOBJECTDATA *entry = (DIDEVICEOBJECTDATA *)
            (bufStart + (realCount + j) * cbObjectData);
        entry->dwOfs = changes[j].scan;
        entry->dwData = changes[j].value ? 0x80 : 0x00;
        entry->dwTimeStamp = timestamp;
        entry->dwSequence = m_synthSequence++;
        entry->uAppData = 0;
    }

    *pdwInOut = realCount + toInject;
    if (!peek) memcpy(m_prevShmKeys, curKeys, 256);

    // Log injected events
    if (toInject > 0 && g_gddLogCount < 200) {
        g_gddLogCount++;
        for (DWORD j = 0; j < toInject; j++) {
            DI8Log("GetDeviceData: injected scan=0x%02X data=0x%02X (event %d/%d)",
                   changes[j].scan, changes[j].value ? 0x80 : 0x00, j + 1, toInject);
        }
    }

    // Return DI_OK when we injected synthetic events — even if the real device
    // failed (DIERR_NOTACQUIRED for background windows). Without this, EQ sees
    // the error code and discards all injected keystroke data.
    return (toInject > 0) ? DI_OK : hr;
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetEventNotification(HANDLE hEvent) {
    HRESULT hr = m_real->SetEventNotification(hEvent);
    if (m_isKeyboard && hEvent) {
        g_kbEventHandle = hEvent;
        DI8Log("SetEventNotification: keyboard event=0x%X",
               (unsigned)(uintptr_t)hEvent);
        StartShmPollingThread();
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetCooperativeLevel(HWND hwnd, DWORD dwFlags) {
    if (m_isKeyboard) {
        g_eqHwnd = hwnd;
        g_originalCoopFlags = dwFlags;

        if (KeyShm::IsActive()) {
            // Mid-login: EQ is re-setting coop level (e.g. server→charselect transition).
            // Apply BACKGROUND immediately — the ActivateThread won't catch this because
            // SHM was already active (no false→true transition to trigger it).
            DWORD bgFlags = (dwFlags & ~(DISCL_EXCLUSIVE | DISCL_FOREGROUND))
                          | DISCL_NONEXCLUSIVE | DISCL_BACKGROUND;
            g_coopSwitched = true;
            DI8Log("SetCooperativeLevel: keyboard hwnd=0x%X flags=0x%X → 0x%X (SHM active, forced BACKGROUND)",
                   (unsigned)(uintptr_t)hwnd, dwFlags, bgFlags);
            // TODO: binary scan for g_bActive and patch it here
            StartActivateThread();
            HRESULT hr = m_real->SetCooperativeLevel(hwnd, bgFlags);
            // Re-post activation — EQ may have deactivated during screen transition
            PostMessageW(hwnd, WM_ACTIVATEAPP, TRUE, 0);
            return hr;
        }

        g_coopSwitched = false;
        // Always strip EXCLUSIVE — EQ works fine with NONEXCLUSIVE, and EXCLUSIVE
        // blocks other EQ instances from switching to BACKGROUND cooperative level.
        // Keep FOREGROUND initially (switching to BACKGROUND at startup makes EQ minimize).
        DWORD safeFlags = (dwFlags & ~DISCL_EXCLUSIVE) | DISCL_NONEXCLUSIVE;
        DI8Log("SetCooperativeLevel: keyboard hwnd=0x%X flags=0x%X → 0x%X (stripped EXCLUSIVE)",
               (unsigned)(uintptr_t)hwnd, dwFlags, safeFlags);
        // TODO: binary scan for g_bActive when background input is implemented
        StartActivateThread();
        return m_real->SetCooperativeLevel(hwnd, safeFlags);
    }
    return m_real->SetCooperativeLevel(hwnd, dwFlags);
}

// --- Pure forwarding methods (unchanged) --------------------------------

HRESULT STDMETHODCALLTYPE DeviceProxy::GetCapabilities(LPDIDEVCAPS lpDIDevCaps) {
    return m_real->GetCapabilities(lpDIDevCaps);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::EnumObjects(
    LPDIENUMDEVICEOBJECTSCALLBACKW lpCallback, LPVOID pvRef, DWORD dwFlags)
{
    return m_real->EnumObjects(lpCallback, pvRef, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetProperty(
    REFGUID rguidProp, LPDIPROPHEADER pdiph)
{
    return m_real->GetProperty(rguidProp, pdiph);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetProperty(
    REFGUID rguidProp, LPCDIPROPHEADER pdiph)
{
    return m_real->SetProperty(rguidProp, pdiph);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::Unacquire() {
    return m_real->Unacquire();
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetDataFormat(LPCDIDATAFORMAT lpdf) {
    return m_real->SetDataFormat(lpdf);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetObjectInfo(
    LPDIDEVICEOBJECTINSTANCEW pdidoi, DWORD dwObj, DWORD dwHow)
{
    return m_real->GetObjectInfo(pdidoi, dwObj, dwHow);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetDeviceInfo(LPDIDEVICEINSTANCEW pdidi) {
    return m_real->GetDeviceInfo(pdidi);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::RunControlPanel(
    HWND hwndOwner, DWORD dwFlags)
{
    return m_real->RunControlPanel(hwndOwner, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::Initialize(
    HINSTANCE hinst, DWORD dwVersion, REFGUID rguid)
{
    return m_real->Initialize(hinst, dwVersion, rguid);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::CreateEffect(
    REFGUID rguid, LPCDIEFFECT lpeff,
    LPDIRECTINPUTEFFECT *ppdeff, LPUNKNOWN punkOuter)
{
    return m_real->CreateEffect(rguid, lpeff, ppdeff, punkOuter);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::EnumEffects(
    LPDIENUMEFFECTSCALLBACKW lpCallback, LPVOID pvRef, DWORD dwEffType)
{
    return m_real->EnumEffects(lpCallback, pvRef, dwEffType);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetEffectInfo(
    LPDIEFFECTINFOW pdei, REFGUID rguid)
{
    return m_real->GetEffectInfo(pdei, rguid);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetForceFeedbackState(LPDWORD pdwOut) {
    return m_real->GetForceFeedbackState(pdwOut);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SendForceFeedbackCommand(DWORD dwFlags) {
    return m_real->SendForceFeedbackCommand(dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::EnumCreatedEffectObjects(
    LPDIENUMCREATEDEFFECTOBJECTSCALLBACK lpCallback, LPVOID pvRef, DWORD fl)
{
    return m_real->EnumCreatedEffectObjects(lpCallback, pvRef, fl);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::Escape(LPDIEFFESCAPE pesc) {
    return m_real->Escape(pesc);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::Poll() {
    return m_real->Poll();
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SendDeviceData(
    DWORD cbObjectData, LPCDIDEVICEOBJECTDATA rgdod,
    LPDWORD pdwInOut, DWORD fl)
{
    return m_real->SendDeviceData(cbObjectData, rgdod, pdwInOut, fl);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::EnumEffectsInFile(
    LPCWSTR lpszFileName, LPDIENUMEFFECTSINFILECALLBACK pec,
    LPVOID pvRef, DWORD dwFlags)
{
    return m_real->EnumEffectsInFile(lpszFileName, pec, pvRef, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::WriteEffectToFile(
    LPCWSTR lpszFileName, DWORD dwEntries,
    LPDIFILEEFFECT rgDiFileEft, DWORD dwFlags)
{
    return m_real->WriteEffectToFile(lpszFileName, dwEntries, rgDiFileEft, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::BuildActionMap(
    LPDIACTIONFORMATW lpdiaf, LPCWSTR lpszUserName, DWORD dwFlags)
{
    return m_real->BuildActionMap(lpdiaf, lpszUserName, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetActionMap(
    LPDIACTIONFORMATW lpdiaf, LPCWSTR lpszUserName, DWORD dwFlags)
{
    return m_real->SetActionMap(lpdiaf, lpszUserName, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetImageInfo(
    LPDIDEVICEIMAGEINFOHEADERW lpdiDevImageInfoHeader)
{
    return m_real->GetImageInfo(lpdiDevImageInfoHeader);
}
