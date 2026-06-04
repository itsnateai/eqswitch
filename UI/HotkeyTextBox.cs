// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Drawing;
using System.Windows.Forms;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// TextBox specialized for hotkey capture. It papers over a few things WinForms makes awkward:
///
///  1. <see cref="IsInputKey"/> — the arrow/navigation cluster is normally consumed before the
///     KeyDown handler sees it, so it could never be captured. Tab is additionally consumed for
///     focus traversal; capturing it is opt-in via <see cref="CaptureTab"/> so only the boxes that
///     can actually BIND Tab (the Switch Key boxes) sacrifice tab-navigation — every other hotkey
///     box keeps normal Tab traversal.
///
///  2. <see cref="WndProc"/> reads the WM_KEYDOWN extended-key flag, which <see cref="KeyEventArgs"/>
///     discards. A numeric-keypad key pressed with NumLock OFF is reported by Windows as a nav VK
///     (numpad-8 → VK_UP) but WITHOUT the extended flag, whereas the dedicated nav keys carry it.
///     Capturing the flag lets the KeyDown handler recognize a numpad-origin press and store it as
///     "NumPadN" regardless of NumLock — matching what the runtime hook already does.
///
///  3. <see cref="FlashReject"/> — a brief background flash to signal a refused keypress. The timer
///     is owned by the control and disposed with it, so a box closed mid-flash can't leak it.
/// </summary>
internal sealed class HotkeyTextBox : TextBox
{
    /// <summary>When true, Tab is captured (deliverable as a bindable key) instead of traversing
    /// focus. Set only on the Switch Key boxes — action/dialog boxes keep normal Tab navigation.</summary>
    public bool CaptureTab { get; set; }

    /// <summary>
    /// True when the most recent key-down came from the numeric keypad — i.e. a nav VK with the
    /// extended-key flag CLEAR. Read by the KeyDown handler (before formatting) to normalize a
    /// numpad press back to its NumPad key. Meaningless for non-nav keys (always false).
    /// </summary>
    public bool LastKeyFromNumpad { get; private set; }

    private System.Windows.Forms.Timer? _flashTimer;
    private Color _flashNormal;

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Tab:
                return CaptureTab;
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
            case Keys.PageUp:
            case Keys.PageDown:
            case Keys.Home:
            case Keys.End:
            case Keys.Insert:
                return true;
            default:
                return base.IsInputKey(keyData);
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        if (m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN)
        {
            // lParam bit 24 (0x01000000) = extended key. The numpad's nav function (NumLock off)
            // is NOT extended; the dedicated nav cluster IS. A non-extended nav VK that the hook's
            // normalizer would rewrite is therefore a numpad-origin press.
            bool extended = ((long)m.LParam & 0x01000000L) != 0;
            uint vk = (uint)(m.WParam.ToInt64() & 0xFF);
            LastKeyFromNumpad = !extended && KeyboardHookManager.NormalizeNumpadVk(vk, 0) != vk;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Briefly flash the background to <paramref name="warn"/> then revert to <paramref name="normal"/>,
    /// signaling a refused keypress. Uses one reusable timer owned by this control (disposed in
    /// <see cref="Dispose"/>) so repeated rejects can't stack timers and a mid-flash close can't leak one.
    /// </summary>
    public void FlashReject(Color warn, Color normal)
    {
        _flashNormal = normal;
        BackColor = warn;
        _flashTimer ??= NewFlashTimer();
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private System.Windows.Forms.Timer NewFlashTimer()
    {
        var t = new System.Windows.Forms.Timer { Interval = 300 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!IsDisposed) BackColor = _flashNormal;
        };
        return t;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _flashTimer?.Dispose();
        base.Dispose(disposing);
    }
}
