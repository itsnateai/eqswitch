// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Drawing;
using System.Windows.Forms;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// v3.24.3 — transition curtain for the residual sub-second multimonitor swap peek.
/// <para>
/// After the v3.24.2 single-source-of-truth swap fix the move itself is clean (~100ms),
/// but a sub-second taskbar peek can still flash on the PRIMARY monitor while EQ's D3D
/// device <i>settles</i> after a borderless window changes monitors: during the settle the
/// incoming client momentarily stops presenting fullscreen, so the shell's rude-window
/// manager flips the monitor non-rude and re-asserts the taskbar to topmost for a frame or
/// two. That settle is upstream of EQSwitch (EQ's own device reset) — maskable, not
/// removable.
/// </para>
/// <para>
/// This curtain masks it: a topmost, full-PRIMARY-monitor, layered black window —
/// WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED, <see cref="Form.ShowWithoutActivation"/>
/// — shown just before the swap and auto-hidden after the settle. Because it is a genuine
/// fullscreen top-of-Z window for its lifetime, the shell keeps the primary monitor "rude"
/// (taskbar suppressed) the whole time it's up (RudeWindowFixer's documented invariant), so
/// the taskbar never peeks; and being non-DX it can't itself flicker. Owned by EQSwitch.exe
/// (NOT the injected eqgame process) → zero cross-process / hook contention, and
/// WS_EX_NOACTIVATE guarantees it never steals focus from the active EQ client.
/// </para>
/// <para>
/// One reusable instance — shown/hidden via raw <c>SetWindowPos</c> + WinForms <c>Hide()</c>,
/// never recreated per swap. The hide is a one-shot WinForms timer (UI thread). Lives for the
/// app's lifetime; disposed by <see cref="TrayManager"/>.
/// </para>
/// </summary>
public sealed class TransitionCurtain : IDisposable
{
    private CurtainForm? _form;
    private System.Windows.Forms.Timer? _hideTimer;
    private bool _disposed;

    /// <summary>
    /// Cover <paramref name="primaryBounds"/> with the black curtain for
    /// <paramref name="durationMs"/> (clamped 30–2000), then auto-hide. Must be called on
    /// the UI thread (the swap handlers run there). A no-op for a degenerate rect. Re-calling
    /// while already up just repositions + restarts the hide timer (rapid \/] mashing keeps
    /// the curtain continuously up instead of strobing).
    /// </summary>
    public void Flash(WinRect primaryBounds, int durationMs)
    {
        if (_disposed) return;
        int w = primaryBounds.Width, h = primaryBounds.Height;
        if (w <= 0 || h <= 0) return;

        _form ??= new CurtainForm();
        var f = _form;
        // Show + position + raise above the (topmost) taskbar in one shot, WITHOUT activating
        // — SWP_NOACTIVATE keeps EQ's focus, SWP_SHOWWINDOW makes the curtain visible, and
        // HWND_TOPMOST puts it above a taskbar that may already be topmost. Touch .Handle first
        // so the window exists for SetWindowPos (the Form is never WinForms-Show()n, so its
        // WS_VISIBLE is driven entirely here + by Hide() below).
        _ = f.Handle;
        NativeMethods.SetWindowPos(f.Handle, NativeMethods.HWND_TOPMOST,
            primaryBounds.Left, primaryBounds.Top, w, h,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        _hideTimer ??= new System.Windows.Forms.Timer();
        _hideTimer.Stop();
        _hideTimer.Interval = Math.Clamp(durationMs, 30, 2000);
        _hideTimer.Tick -= OnHideTick;   // single-subscribe (Flash can be called repeatedly)
        _hideTimer.Tick += OnHideTick;
        _hideTimer.Start();
        FileLogger.Info($"TransitionCurtain: shown over primary ({primaryBounds.Left},{primaryBounds.Top}) {w}x{h} for {_hideTimer.Interval}ms (mask DX-settle taskbar peek)");
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        if (_disposed) return;  // a queued tick can fire between Dispose's Stop() and Dispose()
        HideNow();
    }

    /// <summary>Hide the curtain immediately and stop the pending auto-hide.</summary>
    public void HideNow()
    {
        _hideTimer?.Stop();
        // WinForms Hide() keeps the (created) handle alive for reuse and drops WS_VISIBLE,
        // matching the SWP_SHOWWINDOW set in Flash — no Visible-state desync across cycles.
        if (_form is { IsDisposed: false })
            _form.Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hideTimer?.Stop();
        _hideTimer?.Dispose();
        _hideTimer = null;
        _form?.Dispose();
        _form = null;
    }

    /// <summary>
    /// The non-activating, topmost, layered black cover. Never appears in the taskbar /
    /// Alt+Tab (WS_EX_TOOLWINDOW), never steals focus (WS_EX_NOACTIVATE + ShowWithoutActivation).
    /// </summary>
    private sealed class CurtainForm : Form
    {
        public CurtainForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            // Black over EQ's already-dark bottom chrome → a brief, barely-perceptible dim,
            // far less jarring than the taskbar peek it replaces. Opaque so the cover is a
            // genuine fullscreen window (keeps the monitor rude).
            Opacity = 1.0;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_NOACTIVATE
                            | NativeMethods.WS_EX_TOOLWINDOW
                            | NativeMethods.WS_EX_TOPMOST
                            | NativeMethods.WS_EX_LAYERED;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;
    }
}
