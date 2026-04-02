// di8_proxy.cpp — IDirectInput8 COM proxy implementation
// Forwards all methods to the real interface.
// CreateDevice intercepts keyboard creation to wrap in DeviceProxy.

#include "di8_proxy.h"
#include "device_proxy.h"

// GUID_SysKeyboard {6F1D2B61-D5A0-11CF-BFC7-444553540000}
static const GUID kGuidSysKeyboard =
    {0x6F1D2B61, 0xD5A0, 0x11CF, {0xBF, 0xC7, 0x44, 0x45, 0x53, 0x54, 0x00, 0x00}};

DI8Proxy::DI8Proxy(void *real)
    : m_real(reinterpret_cast<IDirectInput8W *>(real))
    , m_refCount(1)
{
    // AddRef the real interface — we hold a reference for the proxy's lifetime.
    m_real->AddRef();
}

// --- IUnknown -----------------------------------------------------------

HRESULT STDMETHODCALLTYPE DI8Proxy::QueryInterface(REFIID riid, void **ppv) {
    // Forward QI to real, but return our proxy to keep the wrapper chain intact.
    HRESULT hr = m_real->QueryInterface(riid, ppv);
    if (SUCCEEDED(hr)) {
        reinterpret_cast<IUnknown *>(*ppv)->Release();
        InterlockedIncrement(&m_refCount);
        *ppv = this;
    }
    return hr;
}

ULONG STDMETHODCALLTYPE DI8Proxy::AddRef() {
    return InterlockedIncrement(&m_refCount);
}

ULONG STDMETHODCALLTYPE DI8Proxy::Release() {
    LONG count = InterlockedDecrement(&m_refCount);
    if (count == 0) {
        m_real->Release();
        delete this;
        return 0;
    }
    return count;
}

// --- IDirectInput8 ------------------------------------------------------

HRESULT STDMETHODCALLTYPE DI8Proxy::CreateDevice(
    REFGUID rguid, LPDIRECTINPUTDEVICE8W *lplpDevice, LPUNKNOWN pUnkOuter)
{
    HRESULT hr = m_real->CreateDevice(rguid, lplpDevice, pUnkOuter);
    if (SUCCEEDED(hr) && lplpDevice && *lplpDevice) {
        bool isKeyboard = IsEqualGUID(rguid, kGuidSysKeyboard);
        *lplpDevice = reinterpret_cast<LPDIRECTINPUTDEVICE8W>(
            new DeviceProxy(*lplpDevice, isKeyboard));
        DI8Log("CreateDevice: wrapped %s device", isKeyboard ? "KEYBOARD" : "other");
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE DI8Proxy::EnumDevices(
    DWORD dwDevType, LPDIENUMDEVICESCALLBACKW lpCallback,
    LPVOID pvRef, DWORD dwFlags)
{
    return m_real->EnumDevices(dwDevType, lpCallback, pvRef, dwFlags);
}

HRESULT STDMETHODCALLTYPE DI8Proxy::GetDeviceStatus(REFGUID rguidInstance) {
    return m_real->GetDeviceStatus(rguidInstance);
}

HRESULT STDMETHODCALLTYPE DI8Proxy::RunControlPanel(HWND hwndOwner, DWORD dwFlags) {
    return m_real->RunControlPanel(hwndOwner, dwFlags);
}

HRESULT STDMETHODCALLTYPE DI8Proxy::Initialize(HINSTANCE hinst, DWORD dwVersion) {
    return m_real->Initialize(hinst, dwVersion);
}

HRESULT STDMETHODCALLTYPE DI8Proxy::FindDevice(
    REFGUID rguidClass, LPCWSTR ptszName, LPGUID pguidInstance)
{
    return m_real->FindDevice(rguidClass, ptszName, pguidInstance);
}

HRESULT STDMETHODCALLTYPE DI8Proxy::EnumDevicesBySemantics(
    LPCWSTR ptszUserName, LPDIACTIONFORMATW lpdiaf,
    LPDIENUMDEVICESBYSEMANTICSCBW lpCallback, LPVOID pvRef, DWORD dwFlags)
{
    return m_real->EnumDevicesBySemantics(
        ptszUserName, lpdiaf, lpCallback, pvRef, dwFlags);
}

HRESULT STDMETHODCALLTYPE DI8Proxy::ConfigureDevices(
    LPDICONFIGUREDEVICESCALLBACK lpdiCallback,
    LPDICONFIGUREDEVICESPARAMSW lpdiCDParams, DWORD dwFlags, LPVOID pvRefData)
{
    return m_real->ConfigureDevices(lpdiCallback, lpdiCDParams, dwFlags, pvRefData);
}
