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
}
