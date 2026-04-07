using System.Reflection;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Shared dark theme colors, control factories, and custom renderers.
/// Used by SettingsForm and all dark-themed UI forms.
/// </summary>
public static class DarkTheme
{
    // Cache the reflection field for Control.s_defaultFont
    private static readonly FieldInfo? s_defaultFontField =
        typeof(Control).GetField("s_defaultFont", BindingFlags.Static | BindingFlags.NonPublic);

    /// <summary>
    /// Check if Control.DefaultFont is still valid. If its GDI+ handle has
    /// been invalidated (by display changes, DPI events, or GDI+ cleanup),
    /// replace it with a fresh font. This prevents "Parameter is not valid"
    /// crashes in control constructors (TextBox, ComboBox, DataGridView).
    /// </summary>
    public static void RepairDefaultFont()
    {
        try
        {
            _ = Control.DefaultFont.GetHeight();
        }
        catch
        {
            var old = s_defaultFontField?.GetValue(null) as Font;
            var fresh = new Font("Segoe UI", 9f);
            s_defaultFontField?.SetValue(null, fresh);
            if (old != null && !SharedFonts.Contains(old))
                try { old.Dispose(); } catch { /* already invalidated */ }
            FileLogger.Warn("RepairDefaultFont: replaced invalidated Control.DefaultFont");
        }
    }

    // ─── Color Palette (medieval purple tones) ─────────────────
    public static readonly Color BgDark = Color.FromArgb(32, 28, 42);
    public static readonly Color BgMedium = Color.FromArgb(44, 38, 56);
    public static readonly Color BgInput = Color.FromArgb(52, 46, 66);
    public static readonly Color BgHover = Color.FromArgb(64, 56, 78);
    public static readonly Color BgPanel = Color.FromArgb(38, 33, 48);
    public static readonly Color FgWhite = Color.FromArgb(235, 232, 240);
    public static readonly Color FgGray = Color.FromArgb(195, 188, 210);
    public static readonly Color FgDimGray = Color.FromArgb(120, 112, 135);
    public static readonly Color AccentGreen = Color.FromArgb(0, 140, 80);
    public static readonly Color AccentGreenHover = Color.FromArgb(0, 170, 100);
    public static readonly Color Border = Color.FromArgb(64, 56, 78);
    public static readonly Color TabActive = Color.FromArgb(52, 46, 66);
    public static readonly Color TabInactive = Color.FromArgb(38, 33, 48);
    public static readonly Color TabHoverBg = Color.FromArgb(48, 42, 60);
    public static readonly Color AccentBar = Color.FromArgb(0, 140, 80);

    // ─── Tab Colors (one per tab for visual identity) ───────────
    private static readonly Color[] TabAccents =
    {
        Color.FromArgb(100, 220, 130),  // General — green
        Color.FromArgb(220, 190, 100),  // Hotkeys — gold
        Color.FromArgb(140, 160, 220),  // Layout — blue
        Color.FromArgb(220, 120, 120),  // Affinity — red
        Color.FromArgb(100, 200, 210),  // Launch — cyan
        Color.FromArgb(180, 140, 220),  // PiP — purple
        Color.FromArgb(220, 180, 140),  // Paths — warm
    };

    // ─── Cached Fonts (shared across all controls — prevents GDI handle leaks) ──
    private static readonly Font TabFontBold = new("Segoe UI Semibold", 9f, FontStyle.Bold);
    private static readonly Font TabFontRegular = new("Segoe UI", 8.5f, FontStyle.Regular);
    public static readonly Font FontUI9 = new("Segoe UI", 9f);
    public static readonly Font FontUI85 = new("Segoe UI", 8.5f);
    public static readonly Font FontUI75 = new("Segoe UI", 7.5f);
    public static readonly Font FontUI75Italic = new("Segoe UI", 7.5f, FontStyle.Italic);
    public static readonly Font FontSemibold9 = new("Segoe UI Semibold", 9f);
    public static readonly Font FontSemibold95 = new("Segoe UI Semibold", 9.5f);

    // ─── Tab Control ─────────────────────────────────────────────

    /// <summary>
    /// Create a dark owner-drawn TabControl. Replaces the ugly default Windows tabs.
    /// </summary>
    public static TabControl MakeTabControl()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(72, 30),
            Padding = new Point(12, 6),
            Font = FontUI85
        };

        tabs.DrawItem += DrawTab;

        // Prevent the default rendering from showing through
        tabs.Selecting += (_, _) => tabs.Invalidate();

        return tabs;
    }

    private static void DrawTab(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs) return;

        bool isSelected = e.Index == tabs.SelectedIndex;
        var bounds = tabs.GetTabRect(e.Index);
        var tabPage = tabs.TabPages[e.Index];
        var accent = e.Index < TabAccents.Length ? TabAccents[e.Index] : AccentBar;

        // Background — selected gets a subtle tinted fill
        var bgColor = isSelected
            ? Color.FromArgb(accent.R / 8 + 30, accent.G / 8 + 26, accent.B / 8 + 38)
            : TabInactive;
        using var bgBrush = new SolidBrush(bgColor);
        e.Graphics.FillRectangle(bgBrush, bounds);

        // Colored accent bar on selected tab (per-tab color)
        if (isSelected)
        {
            using var accentBrush = new SolidBrush(accent);
            e.Graphics.FillRectangle(accentBrush, bounds.Left + 2, bounds.Top, bounds.Width - 4, 3);
        }

        // Tab text — selected uses the tab's accent color, unselected is dim
        var textColor = isSelected ? accent : FgWhite;
        using var textBrush = new SolidBrush(textColor);
        var font = isSelected ? TabFontBold : TabFontRegular;

        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        var textRect = new Rectangle(bounds.X, bounds.Y + (isSelected ? 2 : 0), bounds.Width, bounds.Height);
        e.Graphics.DrawString(tabPage.Text, font, textBrush, textRect, sf);
    }

    public static TabPage MakeTabPage(string title)
    {
        var page = new TabPage(title)
        {
            BackColor = BgDark,
            ForeColor = FgWhite,
            Padding = new Padding(8)
        };
        typeof(Control).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(page, true);
        return page;
    }

    // ─── Section Headers ─────────────────────────────────────────

    /// <summary>
    /// Add a section header with a subtle accent line underneath.
    /// Returns the Y position after the header for continued layout.
    /// </summary>
    public static int AddSectionHeader(Control parent, string text, int x, int y)
    {
        var label = new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = AccentBar,
            Font = FontSemibold9
        };
        parent.Controls.Add(label);

        // Subtle separator line
        var line = new Panel
        {
            Location = new Point(x, y + 20),
            Size = new Size(parent is TabPage ? 440 : parent.Width - (x * 2), 1),
            BackColor = Border
        };
        parent.Controls.Add(line);

        return y + 28;
    }

    // ─── Labels ──────────────────────────────────────────────────

    public static Label AddLabel(Control parent, string text, int x, int y)
    {
        var lbl = new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgWhite,
            Font = FontUI9
        };
        parent.Controls.Add(lbl);
        return lbl;
    }

    public static Label AddHint(Control parent, string text, int x, int y)
    {
        var lbl = new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgDimGray,
            Font = FontUI75Italic
        };
        parent.Controls.Add(lbl);
        return lbl;
    }

    // ─── Text Inputs ─────────────────────────────────────────────

    public static NumericUpDown AddNumeric(Control parent, int x, int y, int width, decimal defaultVal, decimal min, decimal max)
    {
        var nud = new NumericUpDown
        {
            Location = new Point(x, y),
            Size = new Size(width, 26),
            BackColor = BgInput,
            ForeColor = FgWhite,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(defaultVal, min, max),
            BorderStyle = BorderStyle.FixedSingle,
            Font = FontUI9
        };
        parent.Controls.Add(nud);
        SetNumericMargins(nud);
        return nud;
    }

    public static ComboBox AddComboBox(Control parent, int x, int y, int width, string[] items)
    {
        var cb = new ComboBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 26),
            BackColor = BgInput,
            ForeColor = FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = FontUI9
        };
        cb.Items.AddRange(items);
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        parent.Controls.Add(cb);
        return cb;
    }

    // ─── Checkboxes ──────────────────────────────────────────────

    public static CheckBox AddCheckBox(Control parent, string text, int x, int y)
    {
        var cb = new CheckBox
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgWhite,
            Font = FontUI9
        };
        parent.Controls.Add(cb);
        return cb;
    }

    // ─── Buttons ─────────────────────────────────────────────────

    /// <summary>
    /// Create a styled button with hover effects and hand cursor.
    /// </summary>
    public static Button MakeButton(string text, Color bgColor, int x, int y)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(80, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = bgColor,
            ForeColor = FgWhite,
            Cursor = Cursors.Hand,
            Font = FontUI9
        };
        btn.FlatAppearance.BorderColor = Border;
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.MouseOverBackColor = Lighten(bgColor, 20);
        btn.FlatAppearance.MouseDownBackColor = Lighten(bgColor, 10);
        return btn;
    }

    /// <summary>
    /// Create a primary action button (green accent).
    /// </summary>
    public static Button MakePrimaryButton(string text, int x, int y)
    {
        var btn = MakeButton(text, AccentGreen, x, y);
        btn.FlatAppearance.BorderColor = AccentGreen;
        btn.FlatAppearance.MouseOverBackColor = AccentGreenHover;
        return btn;
    }

    // ─── Form Setup ──────────────────────────────────────────────

    /// <summary>
    /// Apply consistent dark theme styling to any form.
    /// </summary>
    public static void StyleForm(Form form, string title, Size size)
    {
        RepairDefaultFont();
        form.Text = title;
        form.Size = size;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.WindowState = FormWindowState.Normal;
        form.ShowInTaskbar = true;
        form.BackColor = BgDark;
        form.ForeColor = FgWhite;
        form.Font = FontUI9;

        // Tray-only apps don't get foreground activation — briefly set TopMost
        // on first show so the form actually appears visible, then release it
        form.Shown += (_, _) =>
        {
            form.TopMost = true;
            form.BringToFront();
            form.Activate();
            form.TopMost = false;
        };
    }

    // ─── Card Panels ────────────────────────────────────────────

    /// <summary>
    /// Create a styled card panel with emoji header, colored title, and border.
    /// Returns the panel so controls can be added inside it.
    /// </summary>
    public static Panel MakeCard(Control parent, string emoji, string title, Color titleColor, int x, int y, int width, int height)
    {
        var panel = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(width, height),
            BackColor = BgPanel,
            BorderStyle = BorderStyle.None
        };

        bool isHovered = false;
        panel.MouseEnter += (_, _) => { isHovered = true; panel.Invalidate(); };
        panel.MouseLeave += (_, _) =>
        {
            // Only un-hover if the mouse actually left the panel (not entered a child)
            var pos = panel.PointToClient(Cursor.Position);
            if (!panel.ClientRectangle.Contains(pos))
            {
                isHovered = false;
                panel.Invalidate();
            }
        };

        panel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            var borderColor = isHovered ? Lighten(titleColor, -80) : Border;
            using var pen = new Pen(borderColor, 1);
            g.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);

            // Accent left-bar (3px, card's title color)
            using var accentBrush = new SolidBrush(titleColor);
            g.FillRectangle(accentBrush, 0, 0, 3, panel.Height);
        };

        var lblTitle = new Label
        {
            Text = $"{emoji}  {title}",
            Location = new Point(10, 8),
            AutoSize = true,
            ForeColor = titleColor,
            Font = FontSemibold95
        };
        panel.Controls.Add(lblTitle);

        parent.Controls.Add(panel);
        return panel;
    }

    /// <summary>Card title colors — consistent palette for all tabs.</summary>
    public static readonly Color CardGreen = Color.FromArgb(100, 220, 130);
    public static readonly Color CardBlue = Color.FromArgb(140, 160, 220);
    public static readonly Color CardGold = Color.FromArgb(220, 190, 100);
    public static readonly Color CardRed = Color.FromArgb(220, 120, 120);
    public static readonly Color CardPurple = Color.FromArgb(180, 140, 220);
    public static readonly Color CardCyan = Color.FromArgb(100, 200, 210);
    public static readonly Color CardWarn = Color.FromArgb(200, 100, 100);

    // ─── Semantic Colors (specialized use cases) ──────────────
    public static readonly Color BgOverlay = Color.FromArgb(20, 18, 28);
    public static readonly Color GridSelection = Color.FromArgb(50, 44, 70);
    public static readonly Color ActiveRowBg = Color.FromArgb(20, 80, 50);

    /// <summary>Add a label inside a card panel at relative position.</summary>
    public static Label AddCardLabel(Panel card, string text, int x, int y)
    {
        var lbl = new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgGray,
            Font = FontUI85
        };
        card.Controls.Add(lbl);
        return lbl;
    }

    /// <summary>Add a dim hint label inside a card panel.</summary>
    public static Label AddCardHint(Panel card, string text, int x, int y)
    {
        var lbl = new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgDimGray,
            Font = FontUI75
        };
        card.Controls.Add(lbl);
        return lbl;
    }

    /// <summary>Add a dark-styled TextBox inside a card panel with proper borders.</summary>
    public static TextBox AddCardTextBox(Panel card, int x, int y, int width, int maxLength = 260)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 26),
            BackColor = BgInput,
            ForeColor = FgWhite,
            BorderStyle = BorderStyle.FixedSingle,
            Font = FontUI9,
            MaxLength = maxLength
        };
        card.Controls.Add(tb);
        WrapWithBorder(tb);
        tb.HandleCreated += (_, _) => SetTextBoxMargins(tb, 4);
        return tb;
    }

    /// <summary>Add left/right internal padding to a TextBox via EM_SETMARGINS.</summary>
    private static void SetTextBoxMargins(TextBox tb, int margin)
    {
        var lParam = (IntPtr)(margin | (margin << 16));
        NativeMethods.SendMessage(tb.Handle, NativeMethods.EM_SETMARGINS,
            (IntPtr)(NativeMethods.EC_LEFTMARGIN | NativeMethods.EC_RIGHTMARGIN), lParam);
    }

    /// <summary>Add left padding to a NumericUpDown's inner TextBox via EM_SETMARGINS.</summary>
    private static void SetNumericMargins(NumericUpDown nud)
    {
        nud.HandleCreated += (_, _) =>
        {
            foreach (Control c in nud.Controls)
            {
                if (c is TextBox tb)
                {
                    SetTextBoxMargins(tb, 4);
                    break;
                }
            }
        };
    }

    /// <summary>Add a dark-styled ComboBox inside a card panel.</summary>
    public static ComboBox AddCardComboBox(Panel card, int x, int y, int width, string[] items)
    {
        var cb = new ComboBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 24),
            BackColor = BgInput,
            ForeColor = FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = FontUI85
        };
        cb.Items.AddRange(items);
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        card.Controls.Add(cb);
        return cb;
    }

    /// <summary>Add a dark-styled NumericUpDown inside a card panel with proper borders.</summary>
    public static NumericUpDown AddCardNumeric(Panel card, int x, int y, int width, decimal defaultVal, decimal min, decimal max)
    {
        var nud = new NumericUpDown
        {
            Location = new Point(x, y),
            Size = new Size(width, 22),
            BackColor = BgInput,
            ForeColor = FgWhite,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(defaultVal, min, max),
            BorderStyle = BorderStyle.FixedSingle,
            Font = FontUI9
        };
        card.Controls.Add(nud);
        SetNumericMargins(nud);
        return nud;
    }

    /// <summary>Add a CheckBox inside a card panel.</summary>
    public static CheckBox AddCardCheckBox(Panel card, string text, int x, int y)
    {
        var cb = new CheckBox
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgWhite,
            Font = FontUI85
        };
        card.Controls.Add(cb);
        return cb;
    }

    /// <summary>Add a styled button inside a card panel.</summary>
    public static Button AddCardButton(Panel card, string text, int x, int y, int width = 80)
    {
        var btn = MakeButton(text, BgMedium, x, y);
        btn.Size = new Size(width, 26);
        btn.Font = FontUI85;
        card.Controls.Add(btn);
        return btn;
    }

    // ─── Input Border Fix ──────────────────────────────────────
    // WinForms FixedSingle border on dark backgrounds loses the left edge.
    // This wraps any control in a 1px border panel for consistent borders.

    /// <summary>
    /// Wrap a control in a 1px border panel so all edges render correctly on dark backgrounds.
    /// The control is resized to fill the panel interior. Call AFTER adding to parent.
    /// </summary>
    public static void WrapWithBorder(Control control)
    {
        var parent = control.Parent;
        if (parent == null) return;

        var wrapper = new Panel
        {
            Location = new Point(control.Left - 1, control.Top - 1),
            Size = new Size(control.Width + 2, control.Height + 2),
            BackColor = Border
        };

        // Remove the control's own border — the wrapper panel provides it
        if (control is TextBoxBase tb) tb.BorderStyle = BorderStyle.None;
        else if (control is NumericUpDown nud) nud.BorderStyle = BorderStyle.None;
        else if (control is ComboBox cbo) cbo.FlatStyle = FlatStyle.Popup;
        control.Location = new Point(1, 1);
        control.Size = new Size(wrapper.Width - 2, wrapper.Height - 2);

        parent.Controls.Remove(control);
        wrapper.Controls.Add(control);
        parent.Controls.Add(wrapper);
    }

    // ─── Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Clamp a value to a NumericUpDown's range.
    /// </summary>
    public static decimal ClampNud(NumericUpDown nud, decimal value) =>
        Math.Clamp(value, nud.Minimum, nud.Maximum);

    /// <summary>
    /// Lighten a color by a fixed amount (clamped to 255).
    /// </summary>
    public static Color Lighten(Color c, int amount) =>
        Color.FromArgb(
            c.A,
            Math.Clamp(c.R + amount, 0, 255),
            Math.Clamp(c.G + amount, 0, 255),
            Math.Clamp(c.B + amount, 0, 255));

    /// <summary>
    /// Dispose all non-shared Font objects on a control tree.
    /// WinForms does NOT own or dispose fonts assigned to controls — call this
    /// from Dispose(bool) in any form that creates fonts via new Font().
    /// Skips all shared static fonts to avoid destroying them for other forms.
    /// </summary>
    private static readonly HashSet<Font> SharedFonts = new(ReferenceEqualityComparer.Instance)
    {
        TabFontBold, TabFontRegular, FontUI9, FontUI85, FontUI75, FontUI75Italic, FontSemibold9, FontSemibold95
    };

    public static void DisposeControlFonts(Control root)
    {
        foreach (Control c in root.Controls)
        {
            DisposeControlFonts(c);
            if (c.Font != null && !SharedFonts.Contains(c.Font))
                try { c.Font.Dispose(); } catch { }
        }
    }
}
