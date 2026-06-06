// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace EQSwitch.UI;

/// <summary>
/// A dark-themed replacement for <see cref="MessageBox"/> that matches the EQSwitch palette
/// (a native MessageBox is an OS dialog and can't be themed). Same dark body / FgWhite / FontUI9
/// tokens as <see cref="DarkTheme.StyleForm"/>, themed <see cref="Fields.Button"/>s, and a themed
/// status glyph (Segoe MDL2 Assets system icons, with an OS-emoji fallback). Inherits <see cref="EqSwitchForm"/> so it carries the 96-DPI AutoScale baseline
/// (and is covered by <c>DpiBaselineTests</c>); AutoSizes to its content so any message fits at
/// 100 / 125 / 150%. Use the static <see cref="Show"/> exactly like <c>MessageBox.Show</c>.
/// </summary>
internal sealed class ThemedMessageDialog : EqSwitchForm
{
    private Button? _defaultButton;

    private ThemedMessageDialog(string message, string title, MessageBoxButtons buttons,
        MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
    {
        Text = title;
        BackColor = DarkTheme.BgDark;
        ForeColor = DarkTheme.FgWhite;
        Font = DarkTheme.FontUI9;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 14, 16, 12);

        // [icon][message] on row 0, the button bar (right-aligned) spanning row 1. AutoSize columns +
        // rows → the grid (and the AutoSize form) fit the content at any DPI with no pixel literals.
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        string glyph = _mdl2Available ? Glyph(icon) : EmojiGlyph(icon);
        if (glyph.Length > 0)
        {
            var iconLbl = new Label
            {
                Text = glyph,
                AutoSize = true,
                ForeColor = GlyphColor(icon),   // MDL2 glyphs tint; OS emoji ignore ForeColor (multicolor)
                // Owned per-dialog font — DisposeControlFonts (in Dispose) frees it; a distinct instance
                // from the inherited FontUI9, so the ownership guard disposes it exactly once. Segoe MDL2
                // Assets = crisp system icon font (Win10 1507+/Win11); emoji fallback on stripped SKUs.
                Font = new Font(_mdl2Available ? "Segoe MDL2 Assets" : "Segoe UI Emoji", _mdl2Available ? 20f : 18f),
                Margin = new Padding(0, 1, 12, 0),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
            };
            root.Controls.Add(iconLbl, 0, 0);
        }

        var msgLbl = new Label
        {
            Text = message,
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Font = DarkTheme.FontUI9,
            Margin = new Padding(0, 2, 0, 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        // Wrap long lines (e.g. a full EQ path) at a MAX width, and hold a sensible MIN width so a short
        // message ("nothing to trim") still gets a standard dialog proportion instead of a cramped box.
        // Both at device DPI in the handle so they scale at 125/150%.
        HandleCreated += (_, _) =>
        {
            msgLbl.MaximumSize = new Size(LogicalToDeviceUnits(380), 0);
            msgLbl.MinimumSize = new Size(LogicalToDeviceUnits(210), 0);
        };
        root.Controls.Add(msgLbl, 1, 0);

        var btnBar = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 14, 0, 0),
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        AddButtons(btnBar, buttons, defaultButton);
        root.Controls.Add(btnBar, 0, 1);
        root.SetColumnSpan(btnBar, 2);

        Controls.Add(root);
    }

    private void AddButtons(FlowLayoutPanel bar, MessageBoxButtons buttons, MessageBoxDefaultButton defaultButton)
    {
        // Affirmative first (leftmost); the negative button handles Esc. Mirrors MessageBox ordering.
        var list = new List<Button>();
        switch (buttons)
        {
            case MessageBoxButtons.YesNo:
                list.Add(MakeButton(bar, "Yes", DialogResult.Yes));
                list.Add(MakeButton(bar, "No", DialogResult.No));
                CancelButton = list[1];
                break;
            case MessageBoxButtons.OKCancel:
                list.Add(MakeButton(bar, "OK", DialogResult.OK));
                list.Add(MakeButton(bar, "Cancel", DialogResult.Cancel));
                CancelButton = list[1];
                break;
            default: // OK
                list.Add(MakeButton(bar, "OK", DialogResult.OK));
                CancelButton = list[0];
                break;
        }
        // The default button takes Enter + initial focus (e.g. Button2 = "No" on a destructive confirm,
        // so an accidental Enter cancels rather than proceeds). Mirrors MessageBoxDefaultButton.
        int idx = defaultButton switch
        {
            MessageBoxDefaultButton.Button2 => 1,
            MessageBoxDefaultButton.Button3 => 2,
            _ => 0,
        };
        _defaultButton = list[Math.Min(idx, list.Count - 1)];
        AcceptButton = _defaultButton;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _defaultButton?.Select();   // focus rectangle on the default button (matches MessageBox)
    }

    private static Button MakeButton(FlowLayoutPanel bar, string text, DialogResult result)
    {
        var b = Fields.Button(text);
        b.DialogResult = result;
        b.Margin = new Padding(8, 0, 0, 0);   // gap between buttons; first one's left gap is harmless
        bar.Controls.Add(b);
        return b;
    }

    // Segoe MDL2 Assets glyphs (built into Win10 1507+ / Win11) — crisp monochrome icons we tint with
    // the theme palette, instead of multicolor OS emoji that clash with the dark dialog. Codepoints:
    // Info=E946, Warning=E7BA, ErrorBadge=EA39, Help(question)=E897.
    private static string Glyph(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Warning     => "",
        MessageBoxIcon.Error       => "",
        MessageBoxIcon.Information  => "",
        MessageBoxIcon.Question     => "",
        _ => "",
    };

    private static Color GlyphColor(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Warning     => DarkTheme.CardWarn,
        MessageBoxIcon.Error       => DarkTheme.FgTeamSeparatorRed,
        MessageBoxIcon.Information  => DarkTheme.FgCharacterBlue,
        MessageBoxIcon.Question     => DarkTheme.FgCharacterBlue,
        _ => DarkTheme.FgWhite,
    };

    // The crisp MDL2 icons need the system icon font; if a stripped SKU lacks it, fall back to OS emoji
    // so the status glyph is never a missing-glyph box ("tofu"). Win10 1507+/Win11 always have it.
    private static readonly bool _mdl2Available = FontInstalled("Segoe MDL2 Assets");

    private static bool FontInstalled(string family)
    {
        try { using var f = new Font(family, 9f); return string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string EmojiGlyph(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Warning     => "⚠️",
        MessageBoxIcon.Error       => "⛔",
        MessageBoxIcon.Information  => "ℹ️",
        MessageBoxIcon.Question     => "❓",
        _ => "",
    };

    /// <summary>Dark-themed modal message box. Drop-in for <c>MessageBox.Show(owner, text, caption, buttons, icon)</c>.</summary>
    public static DialogResult Show(IWin32Window? owner, string message, string title,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        using var dlg = new ThemedMessageDialog(message, title, buttons, icon, defaultButton);
        return owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);   // frees the owned emoji font; skips inherited/shared
        base.Dispose(disposing);
    }

#if DEBUG
    /// <summary>DEBUG-only sample for the DiagRender preview harness (built, not shown modally).</summary>
    internal static Form Preview() => new ThemedMessageDialog(
        "All 7 log file(s) under 50MB — nothing to trim.",
        "Trim Logs", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
#endif
}
