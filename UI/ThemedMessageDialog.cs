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
/// tokens as <see cref="DarkTheme.StyleForm"/>, themed <see cref="Fields.Button"/>s, and an emoji
/// status glyph. Inherits <see cref="EqSwitchForm"/> so it carries the 96-DPI AutoScale baseline
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

        string glyph = Glyph(icon);
        if (glyph.Length > 0)
        {
            var iconLbl = new Label
            {
                Text = glyph,
                AutoSize = true,
                // Owned per-dialog font — DisposeControlFonts (in Dispose) frees it; it's a distinct
                // instance from the inherited FontUI9, so the ownership guard disposes it exactly once.
                Font = new Font("Segoe UI Emoji", 18f),
                Margin = new Padding(0, 0, 12, 0),
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
        // Wrap very long lines (e.g. a full EQ path) — set at the device DPI in the handle so it
        // scales; short pre-newlined messages are unaffected (they're already under the cap).
        HandleCreated += (_, _) => msgLbl.MaximumSize = new Size(LogicalToDeviceUnits(380), 0);
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

    private static string Glyph(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Warning => "⚠️",
        MessageBoxIcon.Error => "⛔",
        MessageBoxIcon.Information => "ℹ️",
        MessageBoxIcon.Question => "❓",
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
        "⚠️ NUCLEAR OPTION ⚠️\n\n" +
        "This will reset ALL settings to factory defaults.\n" +
        "Your current config will be lost forever.\n\n" +
        "Are you absolutely sure?",
        "Reset to Defaults", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
#endif
}
