#pragma once
// EQSwitch dinput8 proxy -- IDirectInputDevice8 COM wrapper
// Strips EXCLUSIVE from SetCooperativeLevel for multi-instance keyboard access.

#ifndef DIRECTINPUT_VERSION
#define DIRECTINPUT_VERSION 0x0800
#endif
#include <windows.h>
#include <dinput.h>
#include <stdint.h>

// EQ HWND captured from SetCooperativeLevel
HWND GetEqHwnd();

class DeviceProxy : public IDirectInputDevice8W {
    IDirectInputDevice8W *m_real;
    volatile LONG m_refCount;
    bool m_isKeyboard;

    // Per-device state for GetDeviceData synthetic event generation
    uint8_t  m_prevShmKeys[256];
    uint32_t m_synthSequence;

public:
    DeviceProxy(void *real, bool isKeyboard);

    // IUnknown
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void **ppv) override;
    ULONG   STDMETHODCALLTYPE AddRef() override;
    ULONG   STDMETHODCALLTYPE Release() override;

    // IDirectInputDevice8 — 29 methods, vtable slots 3–31
    HRESULT STDMETHODCALLTYPE GetCapabilities(LPDIDEVCAPS) override;
    HRESULT STDMETHODCALLTYPE EnumObjects(LPDIENUMDEVICEOBJECTSCALLBACKW, LPVOID, DWORD) override;
    HRESULT STDMETHODCALLTYPE GetProperty(REFGUID, LPDIPROPHEADER) override;
    HRESULT STDMETHODCALLTYPE SetProperty(REFGUID, LPCDIPROPHEADER) override;
    HRESULT STDMETHODCALLTYPE Acquire() override;
    HRESULT STDMETHODCALLTYPE Unacquire() override;
    HRESULT STDMETHODCALLTYPE GetDeviceState(DWORD, LPVOID) override;
    HRESULT STDMETHODCALLTYPE GetDeviceData(DWORD, LPDIDEVICEOBJECTDATA, LPDWORD, DWORD) override;
    HRESULT STDMETHODCALLTYPE SetDataFormat(LPCDIDATAFORMAT) override;
    HRESULT STDMETHODCALLTYPE SetEventNotification(HANDLE) override;
    HRESULT STDMETHODCALLTYPE SetCooperativeLevel(HWND, DWORD) override;
    HRESULT STDMETHODCALLTYPE GetObjectInfo(LPDIDEVICEOBJECTINSTANCEW, DWORD, DWORD) override;
    HRESULT STDMETHODCALLTYPE GetDeviceInfo(LPDIDEVICEINSTANCEW) override;
    HRESULT STDMETHODCALLTYPE RunControlPanel(HWND, DWORD) override;
    HRESULT STDMETHODCALLTYPE Initialize(HINSTANCE, DWORD, REFGUID) override;
    HRESULT STDMETHODCALLTYPE CreateEffect(REFGUID, LPCDIEFFECT, LPDIRECTINPUTEFFECT *, LPUNKNOWN) override;
    HRESULT STDMETHODCALLTYPE EnumEffects(LPDIENUMEFFECTSCALLBACKW, LPVOID, DWORD) override;
    HRESULT STDMETHODCALLTYPE GetEffectInfo(LPDIEFFECTINFOW, REFGUID) override;
    HRESULT STDMETHODCALLTYPE GetForceFeedbackState(LPDWORD) override;
    HRESULT STDMETHODCALLTYPE SendForceFeedbackCommand(DWORD) override;
    HRESULT STDMETHODCALLTYPE EnumCreatedEffectObjects(LPDIENUMCREATEDEFFECTOBJECTSCALLBACK, LPVOID, DWORD) override;
    HRESULT STDMETHODCALLTYPE Escape(LPDIEFFESCAPE) override;
    HRESULT STDMETHODCALLTYPE Poll() override;
    HRESULT STDMETHODCALLTYPE SendDeviceData(DWORD, LPCDIDEVICEOBJECTDATA, LPDWORD, DWORD) override;
    HRESULT STDMETHODCALLTYPE EnumEffectsInFile(LPCWSTR, LPDIENUMEFFECTSINFILECALLBACK, LPVOID, DWORD) override;
    HRESULT STDMETHODCALLTYPE WriteEffectToFile(LPCWSTR, DWORD, LPDIFILEEFFECT, DWORD) override;
    HRESULT STDMETHODCALLTYPE BuildActionMap(LPDIACTIONFORMATW, LPCWSTR, DWORD) override;
    HRESULT STDMETHODCALLTYPE SetActionMap(LPDIACTIONFORMATW, LPCWSTR, DWORD) override;
    HRESULT STDMETHODCALLTYPE GetImageInfo(LPDIDEVICEIMAGEINFOHEADERW) override;
};
