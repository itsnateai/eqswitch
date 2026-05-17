// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

namespace EQSwitch.Core
{
    public readonly record struct WidgetState(
        bool ConnectVisible,
        bool ServerSelectVisible,
        bool OkDialogVisible,
        bool YesNoDialogVisible,
        bool ConfirmDialogVisible,
        string ConfirmDialogText,
        uint TickSeq,
        // v8 (2026-05-16, v3.22.0 Iter-2A) — Dalaya-reliable char-select signal
        // published by Native from pinstCCharacterSelect double-deref. gGameState
        // is unreliable on Dalaya (never advances past 0); this routes around it.
        bool CharSelectAvailable)
    {
        public static WidgetState Empty => new(false, false, false, false, false, string.Empty, 0, false);

        public bool AnyDialogVisible =>
            OkDialogVisible || YesNoDialogVisible || ConfirmDialogVisible;

        public string DiagSummary() =>
            $"connect={(ConnectVisible ? 1 : 0)} ss={(ServerSelectVisible ? 1 : 0)} " +
            $"ok={(OkDialogVisible ? 1 : 0)} yn={(YesNoDialogVisible ? 1 : 0)} " +
            $"cd={(ConfirmDialogVisible ? 1 : 0)} cs={(CharSelectAvailable ? 1 : 0)} seq={TickSeq}" +
            (ConfirmDialogVisible && ConfirmDialogText.Length > 0
                ? $" cdText=\"{ConfirmDialogText}\""
                : string.Empty);
    }
}
