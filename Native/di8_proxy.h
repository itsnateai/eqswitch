// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

#pragma once
// EQSwitch dinput8 proxy — IDirectInput8 COM wrapper
// Wraps CreateDevice to intercept keyboard device creation.

#ifndef DIRECTINPUT_VERSION
#define DIRECTINPUT_VERSION 0x0800
#endif
#include <windows.h>
#include <dinput.h>

// Log to eqswitch-dinput8.log (defined in the DLL entry point — eqswitch-di8.cpp)
void DI8Log(const char *fmt, ...);

class DI8Proxy : public IDirectInput8W {
    IDirectInput8W *m_real;
    volatile LONG m_refCount;

public:
    explicit DI8Proxy(void *real);

    // IUnknown
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void **ppv) override;
    ULONG   STDMETHODCALLTYPE AddRef() override;
    ULONG   STDMETHODCALLTYPE Release() override;

    // IDirectInput8 (8 methods)
    HRESULT STDMETHODCALLTYPE CreateDevice(REFGUID, LPDIRECTINPUTDEVICE8W *, LPUNKNOWN) override;
    HRESULT STDMETHODCALLTYPE EnumDevices(DWORD, LPDIENUMDEVICESCALLBACKW, LPVOID, DWORD) override;
    HRESULT STDMETHODCALLTYPE GetDeviceStatus(REFGUID) override;
    HRESULT STDMETHODCALLTYPE RunControlPanel(HWND, DWORD) override;
    HRESULT STDMETHODCALLTYPE Initialize(HINSTANCE, DWORD) override;
    HRESULT STDMETHODCALLTYPE FindDevice(REFGUID, LPCWSTR, LPGUID) override;
    HRESULT STDMETHODCALLTYPE EnumDevicesBySemantics(LPCWSTR, LPDIACTIONFORMATW, LPDIENUMDEVICESBYSEMANTICSCBW, LPVOID, DWORD) override;
    HRESULT STDMETHODCALLTYPE ConfigureDevices(LPDICONFIGUREDEVICESCALLBACK, LPDICONFIGUREDEVICESPARAMSW, DWORD, LPVOID) override;
};
