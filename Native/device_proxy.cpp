// device_proxy.cpp -- IDirectInputDevice8 COM proxy implementation
//
// Wraps EQ's DirectInput keyboard device. Responsibilities:
// - SetCooperativeLevel: strips EXCLUSIVE so multiple EQ instances coexist
// - SetEventNotification: captures the keyboard event handle
// - All other methods: pure forwarding to the real device
//
// NOTE (v3.7.0): Focus-faking (ActivateThread, WndProc subclass, pattern scan,
// KeyShm injection) has been removed. Login is now handled in-process by the
// MQ2 bridge state machine (SetWindowText on edit fields, WndNotification on
// buttons). The DeviceProxy stays because we still need NONEXCLUSIVE cooperative
// level for multi-instance keyboard access.

#define _CRT_SECURE_NO_WARNINGS
#include "device_proxy.h"
#include <string.h>

void DI8Log(const char *fmt, ...);

// --- Global state ---

static volatile HWND g_eqHwnd = nullptr;
static volatile HANDLE g_kbEventHandle = nullptr;
static DWORD g_originalCoopFlags = 0;
static IDirectInputDevice8W *g_realKeyboardDevice = nullptr;
static volatile bool g_shutdown = false;

HWND GetEqHwnd() { return g_eqHwnd; }

// Signal shutdown and clean up.
extern "C" void DeviceProxy_Shutdown() {
    g_shutdown = true;
    DI8Log("DeviceProxy_Shutdown: complete");
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
    return m_real->Acquire();
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetDeviceState(DWORD cbData, LPVOID lpvData) {
    return m_real->GetDeviceState(cbData, lpvData);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::GetDeviceData(
    DWORD cbObjectData, LPDIDEVICEOBJECTDATA rgdod,
    LPDWORD pdwInOut, DWORD dwFlags)
{
    return m_real->GetDeviceData(cbObjectData, rgdod, pdwInOut, dwFlags);
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetEventNotification(HANDLE hEvent) {
    HRESULT hr = m_real->SetEventNotification(hEvent);
    if (m_isKeyboard && hEvent) {
        g_kbEventHandle = hEvent;
        g_eqHwnd = FindWindowA(nullptr, "EverQuest");
        DI8Log("SetEventNotification: keyboard event=0x%X, hwnd=0x%X",
               (unsigned)(uintptr_t)hEvent, (unsigned)(uintptr_t)(HWND)g_eqHwnd);
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE DeviceProxy::SetCooperativeLevel(HWND hwnd, DWORD dwFlags) {
    if (m_isKeyboard) {
        g_eqHwnd = hwnd;
        g_originalCoopFlags = dwFlags;

        // Strip EXCLUSIVE -- EQ works fine with NONEXCLUSIVE, and EXCLUSIVE
        // blocks other EQ instances from acquiring the keyboard device.
        DWORD safeFlags = (dwFlags & ~DISCL_EXCLUSIVE) | DISCL_NONEXCLUSIVE;
        DI8Log("SetCooperativeLevel: keyboard hwnd=0x%X flags=0x%X -> 0x%X (stripped EXCLUSIVE)",
               (unsigned)(uintptr_t)hwnd, dwFlags, safeFlags);
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
