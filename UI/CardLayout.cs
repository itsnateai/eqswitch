// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace EQSwitch.UI;

/// <summary>
/// DPI-correct-by-construction card layout. The replacement for the absolute-pixel
/// <see cref="DarkTheme.MakeCard"/> + <c>AddCardX(card, x, y, …)</c> model that clipped
/// at 125%/150% (fonts grew, fixed Sizes didn't). Here NOTHING has a literal pixel
/// position or height: rows live in a <see cref="TableLayoutPanel"/> (label column
/// AutoSize, field column Fill), cards AutoSize to their rows, and the whole stack sizes
/// to the fonts. At any scale the layout is *relational*, so 100% and 150% are
/// proportionally identical with zero pixel literals to mis-scale — no reliance on
/// <see cref="AutoScaleMode.Dpi"/> (which is the thing that's broken). Mirror of the
/// footer FlowLayoutPanel that already renders correctly at 150%.
///
/// Widths (text/combo/numeric field widths) are the only remaining literals; they are
/// horizontal and never clip text vertically, and a Fill field has no literal at all.
/// </summary>
public sealed class CardStack
{
    /// <summary>Scroll host — if the grown content exceeds the form at 150%, it scrolls
    /// instead of clipping. No-op at sizes that fit (i.e. 100%, and 150% on a tall enough form).</summary>
    public Panel Host { get; }

    private readonly TableLayoutPanel _stack;
    private readonly List<Card> _cards = new();
    private bool _batching;

    /// <summary>Every card added to this stack, in order. Used for content-driven window sizing —
    /// see <see cref="Card.ContentWidth"/> (the plain Panel can't report content width because its
    /// body is Dock=Top).</summary>
    public IReadOnlyList<Card> Cards => _cards;

    /// <param name="scroll">true (default) wraps the card stack in an AutoScroll panel so grown
    /// content scrolls instead of clipping when the form can't grow further. false adds the stack
    /// directly (Dock=Top, AutoSize) so a parent that AutoSizes (or a fixed page) hosts it raw —
    /// used where an outer container already provides scrolling, or to let a form size to content.</param>
    public CardStack(Control parent, bool scroll = true)
    {
        _stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = DarkTheme.BgDark,
            Margin = Padding.Empty,
            Padding = new Padding(6, 4, 6, 4),
        };
        _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        if (scroll)
        {
            Host = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = DarkTheme.BgDark,
            };
            Host.Controls.Add(_stack);
            parent.Controls.Add(Host);
        }
        else
        {
            Host = _stack;
            parent.Controls.Add(_stack);
        }
    }

    /// <summary>Add a styled card (emoji + colored title + accent bar + hover border) and return
    /// a builder for its rows. Pass an empty title for a header-less card.</summary>
    public Card NewCard(string emoji, string title, Color titleColor)
    {
        var card = new Card(emoji, title, titleColor);
        AddRowControl(card.Panel);
        _cards.Add(card);
        if (_batching) card.SuspendForBatch();   // defer this card's per-row relayout until Commit()
        return card;
    }

    /// <summary>Add a raw full-width control as its own stack row (e.g. a button bar between cards).</summary>
    public void AddFullWidth(Control control)
    {
        control.Margin = new Padding(0, 0, 0, 10);
        AddRowControl(control);
    }

    private void AddRowControl(Control control)
    {
        int row = _stack.RowCount;
        _stack.RowCount = row + 1;
        _stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Fill;
        _stack.Controls.Add(control, 0, row);
    }

    /// <summary>
    /// Opt-in layout batching for bulk construction. Between BeginBatch() and Commit() the stack TLP
    /// (and every card) is SuspendLayout'd, so adding N cards/rows costs ONE relayout at Commit() instead
    /// of the O(N²) AutoSize-TableLayoutPanel relayout-per-add cascade. Callers that never call BeginBatch
    /// are unaffected (purely additive). ALWAYS pair with Commit() — a stack left batching renders blank.
    /// </summary>
    public void BeginBatch()
    {
        if (_batching) return;
        _batching = true;
        _stack.SuspendLayout();
        if (!ReferenceEquals(Host, _stack)) Host.SuspendLayout();
        foreach (var c in _cards) c.SuspendForBatch();
    }

    /// <summary>Resume layout after BeginBatch(): one relayout pass for the whole stack. Idempotent.</summary>
    public void Commit()
    {
        if (!_batching) return;
        _batching = false;
        foreach (var c in _cards) c.ResumeFromBatch();
        if (!ReferenceEquals(Host, _stack)) Host.ResumeLayout(true);
        _stack.ResumeLayout(true);
    }
}

/// <summary>
/// One card: a styled panel whose body is a 2-column <see cref="TableLayoutPanel"/>
/// (label col AutoSize, field col Fill 100%). Every Add* call appends an AutoSize row,
/// so the card grows with its content at any DPI. Build through <see cref="CardStack.NewCard"/>.
/// </summary>
public sealed class Card
{
    public Panel Panel { get; }
    private readonly TableLayoutPanel _body;
    private readonly Color _titleColor;
    private bool _batchSuspended;

    /// <summary>The card's true content width = its body's preferred width + the card's own padding.
    /// Use this (not <see cref="Panel"/>.PreferredSize.Width) for content-driven window sizing: the
    /// body is Dock=Top inside the plain <see cref="Panel"/>, so the Panel reports only its padding —
    /// the body, a TableLayoutPanel, reports the real column-based width regardless of its dock.</summary>
    public int ContentWidth => _body.PreferredSize.Width + Panel.Padding.Horizontal;

    internal Card(string emoji, string title, Color titleColor)
    {
        _titleColor = titleColor;

        Panel = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.BgPanel,
            // Left padding clears the painted accent bar; the rest is breathing room.
            Padding = new Padding(13, 6, 11, 6),
            Margin = new Padding(0, 0, 0, 6),
        };
        WireCardPaint(Panel, titleColor);

        _body = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // labels hug their text
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); // fields take the rest
        Panel.Controls.Add(_body);

        if (!string.IsNullOrEmpty(title))
        {
            var lbl = new Label
            {
                Text = $"{emoji}  {title}".TrimStart(),
                AutoSize = true,
                ForeColor = titleColor,
                Font = DarkTheme.FontSemibold95,
                Margin = new Padding(0, 0, 0, 4),
            };
            AddSpanning(lbl);
        }
    }

    /// <summary>Suspend this card's layout for a CardStack.BeginBatch() window. Pair with ResumeFromBatch.</summary>
    internal void SuspendForBatch()
    {
        if (_batchSuspended) return;
        _batchSuspended = true;
        Panel.SuspendLayout();
        _body.SuspendLayout();
    }

    /// <summary>Resume after a batch — one relayout of the card. No-op if not batch-suspended.</summary>
    internal void ResumeFromBatch()
    {
        if (!_batchSuspended) return;
        _batchSuspended = false;
        _body.ResumeLayout(true);
        Panel.ResumeLayout(true);
    }

    // ─── Row builders ───────────────────────────────────────────────

    /// <summary>label : field, field STRETCHES to fill the field column (textboxes, full-width combos).</summary>
    public T Row<T>(string label, T field) where T : Control
    {
        var lbl = MakeRowLabel(label);
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        field.Margin = new Padding(0, 2, 0, 2);
        AddPair(lbl, field);
        return field;
    }

    /// <summary>label : field, field keeps its NATURAL width, left-aligned (numerics, small combos).</summary>
    public T RowFit<T>(string label, T field) where T : Control
    {
        var lbl = MakeRowLabel(label);
        field.Anchor = AnchorStyles.Left;
        field.Margin = new Padding(0, 2, 0, 2);
        AddPair(lbl, field);
        return field;
    }

    /// <summary>label : [fill-field][trailing…] — the field STRETCHES to fill the column and the
    /// trailing controls (a Browse button, a unit label) hug to its right. The classic "path box +
    /// Browse" row that has to stay aligned at any DPI.</summary>
    public T RowWith<T>(string label, T fillField, params Control[] trailing) where T : Control
    {
        var sub = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1 + trailing.Length,
            RowCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        sub.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        sub.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fillField.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        fillField.Margin = new Padding(0, 2, 6, 2);
        sub.Controls.Add(fillField, 0, 0);
        for (int i = 0; i < trailing.Length; i++)
        {
            sub.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            trailing[i].Anchor = AnchorStyles.Left;
            trailing[i].Margin = new Padding(0, 2, i < trailing.Length - 1 ? 6 : 0, 2);
            sub.Controls.Add(trailing[i], 1 + i, 0);
        }
        AddPair(MakeRowLabel(label), sub);
        return fillField;
    }

    /// <summary>A field row whose field cell holds several controls flowing left-to-right
    /// (e.g. "W: [nud] H: [nud] Layout: [combo]"). Pass label "" for no leading label.</summary>
    public FlowLayoutPanel FlowRow(string label, params Control[] controls)
    {
        var flow = BuildFlow(controls);
        if (string.IsNullOrEmpty(label)) AddSpanning(flow);
        else AddPair(MakeRowLabel(label), flow);
        return flow;
    }

    /// <summary>FlowRow with a caller-supplied label control — for the case where the label needs a
    /// field reference (e.g. it recolors / retexts on validation, like the Switch Key label).</summary>
    public FlowLayoutPanel FlowRow(Control label, params Control[] controls)
    {
        var flow = BuildFlow(controls);
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(0, 6, 10, 2);
        AddPair(label, flow);
        return flow;
    }

    private static FlowLayoutPanel BuildFlow(Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 1, 0, 1),
            Padding = Padding.Empty,
            Anchor = AnchorStyles.Left,
        };
        foreach (var c in controls)
        {
            c.Margin = new Padding(0, 2, 8, 2);   // vertical-center against the row label, small right gap
            c.Anchor = AnchorStyles.Left;
            flow.Controls.Add(c);
        }
        return flow;
    }

    /// <summary>A checkbox spanning both columns (optionally with a trailing dim hint).</summary>
    public CheckBox Check(CheckBox box, string? hint = null)
    {
        box.AutoSize = true;
        box.Margin = new Padding(0, 3, 0, 3);
        if (hint == null)
        {
            AddSpanning(box);
        }
        else
        {
            var flow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Anchor = AnchorStyles.Left,
            };
            box.Margin = new Padding(0, 3, 10, 3);
            var hl = new Label { Text = hint, AutoSize = true, ForeColor = DarkTheme.FgDimGray, Font = DarkTheme.FontUI75Italic, Margin = new Padding(0, 6, 0, 0) };
            flow.Controls.Add(box);
            flow.Controls.Add(hl);
            AddSpanning(flow);
        }
        return box;
    }

    /// <summary>A full-width control spanning both columns (DataGridView, multiline, summary panel).</summary>
    public T Full<T>(T control) where T : Control
    {
        control.Margin = new Padding(0, 2, 0, 2);
        control.Dock = DockStyle.Top;
        AddSpanning(control);
        return control;
    }

    /// <summary>A dim hint/description spanning both columns.</summary>
    public Label Hint(string text)
    {
        var lbl = new Label { Text = text, AutoSize = true, ForeColor = DarkTheme.FgDimGray, Font = DarkTheme.FontUI75Italic, Margin = new Padding(0, 4, 0, 2) };
        AddSpanning(lbl);
        return lbl;
    }

    /// <summary>A bold sub-section header spanning both columns (e.g. "Left Click").</summary>
    public Label Section(string text)
    {
        var lbl = new Label { Text = text, AutoSize = true, ForeColor = DarkTheme.FgWhite, Font = DarkTheme.FontSemibold9, Margin = new Padding(0, 8, 0, 2) };
        AddSpanning(lbl);
        return lbl;
    }

    /// <summary>A button bar spanning both columns. <paramref name="rightAlign"/> docks it to the right edge.</summary>
    public FlowLayoutPanel Buttons(bool rightAlign, params Control[] buttons)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = rightAlign ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Anchor = rightAlign ? AnchorStyles.Right : AnchorStyles.Left,
            Margin = new Padding(0, 6, 0, 2),
            Padding = Padding.Empty,
        };
        // RightToLeft flow reverses visual order; feed in the same order the caller wrote
        // and reverse so the FIRST argument sits leftmost either way.
        var ordered = rightAlign ? ((Control[])buttons.Clone()) : buttons;
        if (rightAlign) Array.Reverse(ordered);
        foreach (var b in ordered)
        {
            b.Margin = new Padding(rightAlign ? 8 : 0, 0, rightAlign ? 0 : 8, 0);
            flow.Controls.Add(b);
        }
        AddSpanning(flow);
        return flow;
    }

    /// <summary>Expose the body table for the rare custom card (2-column sections etc.) that needs
    /// to drop in a hand-built sub-grid via <see cref="Full"/>.</summary>
    public TableLayoutPanel Body => _body;

    /// <summary>Remove every row — for header-less cards whose content is rebuilt dynamically
    /// (e.g. the Hotkeys Direct Bindings card).</summary>
    public void Clear()
    {
        // Dispose, don't just detach — Controls.Clear() leaves the old rows undisposed
        // (handlers + handles live on) each time a dynamic card (e.g. direct-bindings) rebuilds.
        for (int i = _body.Controls.Count - 1; i >= 0; i--)
        {
            var child = _body.Controls[i];
            _body.Controls.RemoveAt(i);
            child.Dispose();
        }
        _body.RowStyles.Clear();
        _body.RowCount = 0;
    }

    // ─── internals ──────────────────────────────────────────────────

    private Label MakeRowLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = DarkTheme.FgGray,
        Font = DarkTheme.FontUI9,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 4, 10, 1),   // top:6 vertically centers against ~font-height fields; right:10 gap to field
    };

    private void AddPair(Control label, Control field)
    {
        int row = _body.RowCount;
        _body.RowCount = row + 1;
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _body.Controls.Add(label, 0, row);
        _body.Controls.Add(field, 1, row);
    }

    private void AddSpanning(Control control)
    {
        int row = _body.RowCount;
        _body.RowCount = row + 1;
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _body.Controls.Add(control, 0, row);
        _body.SetColumnSpan(control, 2);
    }

    private static void WireCardPaint(Panel panel, Color titleColor)
    {
        bool hovered = false;
        panel.MouseEnter += (_, _) => { hovered = true; panel.Invalidate(); };
        panel.MouseLeave += (_, _) =>
        {
            var pos = panel.PointToClient(Cursor.Position);
            if (!panel.ClientRectangle.Contains(pos)) { hovered = false; panel.Invalidate(); }
        };
        panel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            var border = hovered ? DarkTheme.Lighten(titleColor, -80) : DarkTheme.Border;
            using var pen = new Pen(border, 1);
            g.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
            using var accent = new SolidBrush(titleColor);
            g.FillRectangle(accent, 0, 0, panel.LogicalToDeviceUnits(3), panel.Height); // accent bar (paint geom not auto-scaled)
        };
    }
}

/// <summary>
/// Height-free, DPI-correct, UNPARENTED field factories for the layout-container rebuild —
/// the dark-themed control palette without the fixed <c>Size(w, 26)</c> the old
/// <c>DarkTheme.AddCardX</c> helpers baked in. Heights come from the font (single-line inputs
/// auto-size their height to the font at any DPI); only WIDTHS are literal, and width never
/// clips text vertically. Place the returned control with a <see cref="Card"/> row method.
/// </summary>
public static class Fields
{
    /// <summary>A dark TextBox. Give a width for a fit row; it's ignored (stretched) in a fill row.</summary>
    public static TextBox Text(int width = 160, int maxLength = 0)
    {
        var tb = new TextBox
        {
            Width = width,
            Font = DarkTheme.FontUI9,
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle,
        };
        if (maxLength > 0) tb.MaxLength = maxLength;
        return tb;
    }

    /// <summary>A dark drop-down-list ComboBox (fixed width, font-height).</summary>
    public static ComboBox Combo(int width, params string[] items)
    {
        var cb = new DarkTheme.ScrollGuardComboBox
        {
            Width = width,
            Font = DarkTheme.FontUI9,
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
        };
        cb.Items.AddRange(items);
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        return cb;
    }

    /// <summary>A dark NumericUpDown (fixed width, font-height — never the clipped Size(w,22)).</summary>
    public static NumericUpDown Numeric(decimal min, decimal max, decimal val, int width = 60)
    {
        return new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(val, min, max),
            Width = width,
            Font = DarkTheme.FontUI9,
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle,
        };
    }

    /// <summary>An AutoSize dark CheckBox (grows with its text — never clips the label).</summary>
    public static CheckBox Check(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = DarkTheme.FgWhite,
        Font = DarkTheme.FontUI9,
    };

    /// <summary>An AutoSize styled button (grows with its text — never clips "Settings…" to "EQ Client").</summary>
    public static Button Button(string text, Color? bg = null)
    {
        var b = DarkTheme.MakeButton(text, bg ?? DarkTheme.BgMedium, 0, 0);
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Margin = Padding.Empty;
        b.Padding = new Padding(8, 2, 8, 2);   // a little horizontal breathing room inside the AutoSize bounds
        return b;
    }

    /// <summary>An AutoSize primary (green) button.</summary>
    public static Button Primary(string text)
    {
        var b = DarkTheme.MakePrimaryButton(text, 0, 0);
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Margin = Padding.Empty;
        b.Padding = new Padding(8, 2, 8, 2);
        return b;
    }
}

/// <summary>
/// Manual DPI width-scaling for fixed-width fit inputs (numerics, fit combos, hotkey/fit textboxes).
/// Layout containers handle positions + heights (font-driven) and the form scales its ClientSize, but a
/// fit field's literal WIDTH doesn't grow on its own under SystemAware (AutoScaleMode.Dpi is dead here) —
/// so a 4-digit numeric or a "Ctrl+Alt+Shift+X" box clips at 150%. This scales those widths once at Load.
/// Fill fields (Anchor includes Right, or Dock=Fill) already stretch, so they're skipped; heights are
/// never touched (single-line inputs auto-height to the font); DataGridView is excluded (DPI-aware itself).
/// </summary>
public static class DpiScale
{
    /// <summary>Size each fixed-width fit input to fit ITS content at the device DPI: numerics to
    /// their Maximum value, combos to their longest item, fit textboxes scaled from their design
    /// width. Fill fields (Anchor includes Right, or Dock != None) are skipped. Run ONCE at Load —
    /// it reads the live (device-DPI) font via MeasureText + LogicalToDeviceUnits, so it is correct
    /// at 100% and 150% without a scale factor (but NOT idempotent for textboxes — call once).</summary>
    public static void SizeFitFields(Control root)
    {
        // v3.24.46: each `c.Width = …` below mutates a control that lives inside an AutoSize
        // TableLayoutPanel, so without batching every single width-set cascades a relayout up the deep
        // card→body→stack tree — dozens of full passes that dominated the Settings open (~500ms, measured).
        // Suspending each container while its OWN direct children are resized defers those cascades; the
        // single ResumeLayout(true) at the root then does ONE coherent top-down pass. Final widths are
        // byte-identical — the measurement (MeasureText/LogicalToDeviceUnits) is independent of layout
        // state; only the *application* of the widths is coalesced. Idempotent + safe to nest (a caller
        // that already suspended `root`, e.g. EnsureTabBuilt, just adds a refcount).
        root.SuspendLayout();
        try { SizeFitFieldsWalk(root); }
        finally { root.ResumeLayout(true); }
    }

    private static void SizeFitFieldsWalk(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c.Dock == DockStyle.None && !c.Anchor.HasFlag(AnchorStyles.Right))
            {
                int w = 0;
                if (c is NumericUpDown nud)
                {
                    string max = nud.Maximum.ToString(
                        nud.DecimalPlaces > 0 ? "F" + nud.DecimalPlaces : "0",
                        System.Globalization.CultureInfo.InvariantCulture);
                    w = TextRenderer.MeasureText(max, nud.Font).Width + nud.LogicalToDeviceUnits(26); // digits + spinner + border + pad
                }
                else if (c is ComboBox cb && cb.Items.Count > 0)
                {
                    int t = 0;
                    foreach (var it in cb.Items)
                        t = System.Math.Max(t, TextRenderer.MeasureText(it?.ToString() ?? string.Empty, cb.Font).Width);
                    w = t + cb.LogicalToDeviceUnits(32); // text + dropdown arrow + border + pad
                }
                else if (c is TextBox tb)
                {
                    w = tb.LogicalToDeviceUnits(tb.Width); // variable content (hotkey/exe/args) — scale design width to device
                }
                if (w > 0)
                {
                    c.Width = w;
                    c.MinimumSize = new Size(w, c.MinimumSize.Height);
                }
            }
            // Don't descend into leaf fields' own internals (a NumericUpDown's inner edit/buttons,
            // a ComboBox's edit) — only walk container children. Suspend the container while ITS direct
            // children are resized (so their width-sets defer the container's relayout); ResumeLayout(false)
            // leaves the single forced pass to the root SuspendLayout/ResumeLayout(true) wrapper above.
            if (c is not (NumericUpDown or ComboBox or TextBox))
            {
                c.SuspendLayout();
                try { SizeFitFieldsWalk(c); }
                finally { c.ResumeLayout(false); }
            }
        }
    }
}


/// <summary>
/// Button-row layouts: a SPLIT row (left group hugs the left edge, right group hugs the right edge,
/// flexible spacer between — even card-edge padding) and a SPREAD row (N controls distributed evenly,
/// first hugging left, last hugging right, the rest centered). Replaces the original absolute-X
/// right-alignment / spacing that the layout-container rebuild flattened to left-aligned.
/// </summary>
public static class Bars
{
    public static TableLayoutPanel Split(Control[] left, Control[] right, int gap = 8)
    {
        var g = NewBar(2);
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); // left group + spacer
        g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // right group hugs right
        g.Controls.Add(Group(left, AnchorStyles.Left, gap), 0, 0);
        g.Controls.Add(Group(right, AnchorStyles.Right, gap), 1, 0);
        return g;
    }

    public static TableLayoutPanel Spread(params Control[] items)
    {
        int n = items.Length;
        var g = NewBar(n);
        for (int i = 0; i < n; i++) g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / n));
        for (int i = 0; i < n; i++)
        {
            items[i].Anchor = i == 0 ? AnchorStyles.Left : i == n - 1 ? AnchorStyles.Right : AnchorStyles.None;
            items[i].Margin = Padding.Empty;
            g.Controls.Add(items[i], i, 0);
        }
        return g;
    }

    /// <summary>N controls each CENTRED within an equal-width column — symmetric breathing room on both
    /// outer edges plus an even gap between, i.e. the "indented in from the card edges" look. Differs from
    /// <see cref="Spread"/> only in anchoring: Spread pins the first/last control to the card edges, whereas
    /// Centred floats every control to the middle of its own equal share. For two controls that means each
    /// edge gets <c>(50% − controlWidth) / 2</c> of padding — roughly one control-width on a typical card.
    /// Pure proportional columns + <see cref="AnchorStyles.None"/> centring → zero pixel literals, so 100 /
    /// 125 / 150% are proportionally identical. If a control ever exceeds its share it overflows symmetrically
    /// into the empty neighbouring slack rather than clipping (AutoSize controls always render their full text).</summary>
    public static TableLayoutPanel Centered(params Control[] items)
    {
        int n = items.Length;
        var g = NewBar(n);
        for (int i = 0; i < n; i++) g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / n));
        for (int i = 0; i < n; i++)
        {
            items[i].Anchor = AnchorStyles.None;   // float to the centre of its share — even padding on both sides
            items[i].Margin = Padding.Empty;
            g.Controls.Add(items[i], i, 0);
        }
        return g;
    }

    private static TableLayoutPanel NewBar(int cols) => new()
    {
        ColumnCount = cols,
        RowCount = 1,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        Margin = new Padding(0, 6, 0, 2),
        Padding = Padding.Empty,
        BackColor = Color.Transparent,
        RowStyles = { new RowStyle(SizeType.AutoSize) },
    };

    private static FlowLayoutPanel Group(Control[] items, AnchorStyles anchor, int gap = 8)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Anchor = anchor,
            BackColor = Color.Transparent,
        };
        for (int i = 0; i < items.Length; i++)
        {
            items[i].Margin = new Padding(0, 0, i < items.Length - 1 ? gap : 0, 0);
            f.Controls.Add(items[i]);
        }
        return f;
    }
}
