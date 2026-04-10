// login_state_machine.h -- In-process login via MQ2 UI widget manipulation
//
// Drives EQ's entire login flow (login->server->charselect->enterworld)
// by directly calling CXWnd::SetWindowText and SendWndNotification on
// EQ's UI widgets. No focus faking, no PostMessage, no key injection.
//
// Mirrors MQ2AutoLogin's proven approach (StateMachine.cpp) but simplified
// for Dalaya's single-server setup.

#pragma once
#include "login_shm.h"
#include "mq2_bridge.h"

namespace LoginStateMachine {
    // Call from the bridge polling thread (~2Hz).
    // Reads command from loginShm, drives EQ's UI, writes state feedback.
    // charSelShm is optional (nullptr if not open) -- populated at char select.
    void Tick(volatile LoginShm *loginShm, volatile CharSelectShm *charSelShm);

    // Reset state on DLL unload.
    void Shutdown();
}
