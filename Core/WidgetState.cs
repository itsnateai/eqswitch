// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Runtime.InteropServices;

namespace EQSwitch.Core
{
    public readonly record struct WidgetState(
        bool ConnectVisible,
        bool ServerSelectVisible,
        bool OkDialogVisible,
        bool YesNoDialogVisible,
        bool ConfirmDialogVisible,
        string ConfirmDialogText,
        uint TickSeq)
    {
        public static WidgetState Empty => new(false, false, false, false, false, string.Empty, 0);

        public bool AnyDialogVisible =>
            OkDialogVisible || YesNoDialogVisible || ConfirmDialogVisible;

        public string DiagSummary() =>
            $"connect={(ConnectVisible ? 1 : 0)} ss={(ServerSelectVisible ? 1 : 0)} " +
            $"ok={(OkDialogVisible ? 1 : 0)} yn={(YesNoDialogVisible ? 1 : 0)} " +
            $"cd={(ConfirmDialogVisible ? 1 : 0)} seq={TickSeq}" +
            (ConfirmDialogVisible && ConfirmDialogText.Length > 0
                ? $" cdText=\"{ConfirmDialogText}\""
                : string.Empty);
    }
}
