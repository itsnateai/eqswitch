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

    /// <summary>
    /// Size <see cref="Form.ClientSize"/> vertically so a bottom-docked button bar sits a
    /// consistent <paramref name="gap"/> px below the tallest content control — instead of the
    /// form height being a hand-guessed literal. A guessed height left an uneven dead gap above
    /// Save/Apply/Cancel and, when under-guessed, let the last card overlap the buttons (the two
    /// failure modes the EQ Client Settings sub-forms all exhibited). Sizing to content makes the
    /// gap uniform and self-correcting when settings are added or removed.
    ///
    /// Call ONCE at the very end of form construction — after all content AND the bottom-docked
    /// button panel have been added. DPI-correct by construction: every coordinate read here is a
    /// design-time (96-DPI) value, because this runs before <see cref="AutoScaleMode.Dpi"/>'s
    /// PerformAutoScale pass. The resulting ClientSize therefore scales uniformly with the content
    /// at 125/150% — there is no per-scale math to get wrong. Width is left untouched.
    /// </summary>
    /// <param name="contentHost">The control whose children are the page content. Usually
    /// <c>this</c> (the default); pass a scroll/host panel when the content lives inside one.
    /// Children are measured in the host's OWN coordinates, assumed to share the form's client
    /// origin — true for <c>this</c> and for a <c>Dock=Fill</c> panel at (0,0) (the only hosts used
    /// today; an inset host would need its offset added). Bottom-docked children are skipped so
    /// only real content drives the height.</param>
    /// <param name="gap">Design-px gap between the last content control and the button bar.
    /// Default 8 matches the inter-card spacing the forms already use, so the rhythm is even.</param>
    protected void FitClientHeightToContent(Control? contentHost = null, int gap = 8)
    {
        contentHost ??= this;

        // Sum the height of every bottom-docked bar on the form (they stack at the bottom edge —
        // usually just the one Save/Apply/Cancel panel, but a status strip would correctly add to
        // it). Read from the live control so this never drifts from the panel's Height literal.
        int barHeight = 0;
        foreach (Control c in Controls)
            if (c.Dock == DockStyle.Bottom)
                barHeight += c.Height;

        // Tallest content control (its design-time Bottom = Location.Y + Height). Skip the docked
        // bar when the host IS the form; a dedicated content panel has no Bottom-docked children.
        int contentBottom = 0;
        foreach (Control c in contentHost.Controls)
        {
            if (c.Dock == DockStyle.Bottom) continue;
            contentBottom = Math.Max(contentBottom, c.Bottom);
        }

        ClientSize = new Size(ClientSize.Width, contentBottom + gap + barHeight);
    }
}
