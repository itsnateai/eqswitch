// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Drawing;
using System.Windows.Forms;

namespace EQSwitch.UI;

/// <summary>
/// Base for every EQSwitch dialog/settings form. Pins the 96-DPI AutoScale baseline in
/// the constructor so the hardcoded <c>new Point/Size</c> literals every form uses are
/// interpreted as 96-DPI design pixels, then scaled to the device by
/// <see cref="AutoScaleMode.Dpi"/>. Inheriting this is the ONE thing a new form must do
/// to be DPI-correct; <c>EQSwitch.Core.DpiBaselineTests</c> enforces it at build time.
///
/// Order is load-bearing: <see cref="ContainerControl.AutoScaleDimensions"/> is set BEFORE
/// <see cref="ContainerControl.AutoScaleMode"/> — setting the mode first snapshots
/// CurrentAutoScaleDimensions from the realizing monitor and ignores the explicit baseline.
/// At 100% scale the factor is 96/96 = 1.0 → exact no-op (no visual change on a 100% display).
///
/// DPI mode note: EQSwitch stays <see cref="HighDpiMode.SystemAware"/> (set in Program.cs).
/// Do NOT change the process DPI mode to PerMonitorV2 here or anywhere — it regressed the
/// injected EQ game-window geometry (see Program.cs and CLAUDE.md "HIGH-DPI" section). This
/// per-form baseline works correctly under SystemAware for all single-scale setups.
///
/// EXCLUDED (must stay <c>: Form</c> — they self-position in screen coordinates and would
/// break if auto-scaled): PipOverlay, TransitionCurtain.CurtainForm, FloatingTooltip.TooltipForm.
/// </summary>
public class EqSwitchForm : Form
{
    public EqSwitchForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    // TEMP DPI DIAGNOSTIC (v3.24.33) — logs the actual auto-scale state once each form
    // is shown, so we can see WHY the layout isn't scaling at 125%/150%. If the form
    // scaled, ClientSize is ~1.5× its design size at 150%; if AutoScaleFactor≈1.0 the
    // baseline was clobbered. Remove after the root cause is fixed.
    protected override void OnShown(System.EventArgs e)
    {
        base.OnShown(e);
        try
        {
            float f = AutoScaleDimensions.Width > 0
                ? CurrentAutoScaleDimensions.Width / AutoScaleDimensions.Width : -1f;
            EQSwitch.Core.FileLogger.Info(
                $"DPI-DIAG {GetType().Name}: DeviceDpi={DeviceDpi} mode={AutoScaleMode} " +
                $"ASD={AutoScaleDimensions.Width:0}x{AutoScaleDimensions.Height:0} " +
                $"CASD={CurrentAutoScaleDimensions.Width:0}x{CurrentAutoScaleDimensions.Height:0} " +
                $"factor={f:0.00} ClientSize={ClientSize.Width}x{ClientSize.Height}");
        }
        catch { /* diagnostic only — never throw */ }
    }
}
