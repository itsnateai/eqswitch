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

// --- WM_ACTIVATEAPP thread (Phase 2) ---
// Posts WM_ACTIVATEAPP(TRUE) when shm goes active, tricking EQ's main loop
// into calling keyboard_process for background windows.

static DWORD WINAPI ActivateThread(LPVOID) {
    bool wasActive = false;
    while (!g_shutdown) {
        Sleep(16); // ~60Hz
        bool active = KeyShm::IsActive();
        HWND hwnd = g_eqHwnd;

        if (active && !wasActive && hwnd) {
            // Deferred cooperative level switch: only change to BACKGROUND
            // when shared memory first activates (background login starting).
            // Doing this at startup causes EQ to minimize itself.
            if (!g_coopSwitched && g_realKeyboardDevice) {
                DWORD bgFlags = (g_originalCoopFlags & ~(DISCL_EXCLUSIVE | DISCL_FOREGROUND))
                              | DISCL_NONEXCLUSIVE | DISCL_BACKGROUND;
                HRESULT hr = g_realKeyboardDevice->SetCooperativeLevel(hwnd, bgFlags);
                g_coopSwitched = true;
                DI8Log("wm_activate: deferred switch to BACKGROUND|NONEXCLUSIVE (hr=0x%08X)",
                       (unsigned)hr);
            }

            PostMessageW(hwnd, WM_ACTIVATEAPP, TRUE, 0);
            DI8Log("wm_activate: posted WM_ACTIVATEAPP(1) hwnd=0x%X",
                   (unsigned)(uintptr_t)hwnd);
        } else if (!active && wasActive && hwnd) {
            // Only reset if EQ isn't actually foreground
            HWND fg = GetForegroundWindow();
            if (fg != hwnd) {
                PostMessageW(hwnd, WM_ACTIVATEAPP, FALSE, 0);
                DI8Log("wm_activate: posted WM_ACTIVATEAPP(0) hwnd=0x%X",
                       (unsigned)(uintptr_t)hwnd);
            }
        }
        wasActive = active;
    }
    return 0;
}

static void StartActivateThread() {
    if (g_activateThreadStarted) return;
    g_activateThreadStarted = true;
    g_hActivateThread = CreateThread(nullptr, 0, ActivateThread, nullptr, 0, nullptr);
}

// Called from dinput8-proxy DLL_PROCESS_DETACH to signal threads to exit.
// MUST NOT call WaitForSingleObject here — DllMain holds the loader lock,
// and the threads may call DI8Log→EnsureLogOpen which touches the loader.
// The OS terminates threads on process exit; on FreeLibrary, the threads
// will see g_shutdown and exit within one sleep cycle (~16ms).
extern "C" void DeviceProxy_Shutdown() {
    g_shutdown = true;
    if (g_hActivateThread) { CloseHandle(g_hActivateThread); g_hActivateThread = nullptr; }
    if (g_hShmThread) { CloseHandle(g_hShmThread); g_hShmThread = nullptr; }
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
    g_shmThreadStarted = true;
    g_hShmThread = CreateThread(nullptr, 0, ShmPollingThread, nullptr, 0, nullptr);
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

HRESULT STDMETHODCALLTYPE DeviceProxy::GetDeviceState(DWORD cbData, LPVOID lpvData) {
    HRESULT hr = m_real->GetDeviceState(cbData, lpvData);

    if (m_isKeyboard) {
        if (SUCCEEDED(hr)) {
            if (KeyShm::ShouldSuppress())
                memset(lpvData, 0, cbData);
            KeyShm::InjectKeys((uint8_t *)lpvData, cbData);
        } else {
            // Device lost or not acquired — provide synthetic state anyway
            memset(lpvData, 0, cbData);
            if (KeyShm::InjectKeys((uint8_t *)lpvData, cbData))
                return DI_OK;
        }
    }
    return hr;
}

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
        return hr;
    }

    // NULL rgdod = just querying count
    if (!rgdod) {
        *pdwInOut = realCount + (DWORD)numChanges;
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
    return hr;
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
        // Store original flags — we'll switch to BACKGROUND later when SHM activates.
        // Changing to BACKGROUND at startup causes EQ to minimize itself.
        g_originalCoopFlags = dwFlags;
        g_coopSwitched = false;
        DI8Log("SetCooperativeLevel: keyboard hwnd=0x%X flags=0x%X (keeping FOREGROUND)",
               (unsigned)(uintptr_t)hwnd, dwFlags);
        StartActivateThread();
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
