namespace EQSwitch.UI;

/// <summary>
/// Shared dark theme colors, control factories, and custom renderers.
/// Used by SettingsForm and all dark-themed UI forms.
/// </summary>
public static class DarkTheme
{
    // ─── Color Palette (medieval purple tones) ─────────────────
    public static readonly Color BgDark = Color.FromArgb(32, 28, 42);
    public static readonly Color BgMedium = Color.FromArgb(44, 38, 56);
    public static readonly Color BgInput = Color.FromArgb(52, 46, 66);
    public static readonly Color BgHover = Color.FromArgb(64, 56, 78);
    public static readonly Color BgPanel = Color.FromArgb(38, 33, 48);
    public static readonly Color FgWhite = Color.FromArgb(235, 232, 240);
    public static readonly Color FgGray = Color.FromArgb(160, 150, 175);
    public static readonly Color FgDimGray = Color.FromArgb(120, 112, 135);
    public static readonly Color AccentGreen = Color.FromArgb(0, 140, 80);
    public static readonly Color AccentGreenHover = Color.FromArgb(0, 170, 100);
    public static readonly Color Border = Color.FromArgb(64, 56, 78);
    public static readonly Color TabActive = Color.FromArgb(52, 46, 66);
    public static readonly Color TabInactive = Color.FromArgb(38, 33, 48);
    public static readonly Color TabHoverBg = Color.FromArgb(48, 42, 60);
    public static readonly Color AccentBar = Color.FromArgb(0, 140, 80);

    // ─── Cached Fonts (avoid allocations in render methods) ──────
    private static readonly Font TabFontBold = new("Segoe UI", 8.5f, FontStyle.Bold);
    private static readonly Font TabFontRegular = new("Segoe UI", 8.5f, FontStyle.Regular);

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
            ItemSize = new Size(58, 32),
            Padding = new Point(12, 6),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular)
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

        using var bgBrush = new SolidBrush(isSelected ? TabActive : TabInactive);
        e.Graphics.FillRectangle(bgBrush, bounds);

        // Green accent bar on selected tab
        if (isSelected)
        {
            using var accentBrush = new SolidBrush(AccentBar);
            e.Graphics.FillRectangle(accentBrush, bounds.Left + 2, bounds.Top, bounds.Width - 4, 3);
        }

        // Tab text — use cached static fonts to avoid GDI allocations per draw
        var textColor = isSelected ? FgWhite : FgGray;
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
        return new TabPage(title)
        {
            BackColor = BgDark,
            ForeColor = FgWhite,
            Padding = new Padding(8)
        };
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
            Font = new Font("Segoe UI Semibold", 9f)
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
            Font = new Font("Segoe UI", 9f)
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
            Font = new Font("Segoe UI", 7.5f, FontStyle.Italic)
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
            Font = new Font("Segoe UI", 9f)
        };
        parent.Controls.Add(nud);
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
            Font = new Font("Segoe UI", 9f)
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
            Font = new Font("Segoe UI", 9f)
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
            Font = new Font("Segoe UI", 9f)
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
        form.Text = title;
        form.Size = size;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.BackColor = BgDark;
        form.ForeColor = FgWhite;
        form.Font = new Font("Segoe UI", 9f);
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
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Border, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };

        var lblTitle = new Label
        {
            Text = $"{emoji}  {title}",
            Location = new Point(10, 8),
            AutoSize = true,
            ForeColor = titleColor,
            Font = new Font("Segoe UI Semibold", 9.5f)
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

    /// <summary>Add a label inside a card panel at relative position.</summary>
    public static Label AddCardLabel(Panel card, string text, int x, int y)
    {
        var lbl = new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgGray,
            Font = new Font("Segoe UI", 8.5f)
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
            Font = new Font("Segoe UI", 7.5f)
        };
        card.Controls.Add(lbl);
        return lbl;
    }

    /// <summary>Add a dark-styled TextBox inside a card panel.</summary>
    public static TextBox AddCardTextBox(Panel card, int x, int y, int width)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 24),
            BackColor = BgInput,
            ForeColor = FgWhite,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f)
        };
        card.Controls.Add(tb);
        return tb;
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
            Font = new Font("Segoe UI", 8.5f)
        };
        cb.Items.AddRange(items);
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        card.Controls.Add(cb);
        return cb;
    }

    /// <summary>Add a dark-styled NumericUpDown inside a card panel.</summary>
    public static NumericUpDown AddCardNumeric(Panel card, int x, int y, int width, decimal defaultVal, decimal min, decimal max)
    {
        var nud = new NumericUpDown
        {
            Location = new Point(x, y),
            Size = new Size(width, 24),
            BackColor = BgInput,
            ForeColor = FgWhite,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(defaultVal, min, max),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f)
        };
        card.Controls.Add(nud);
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
            Font = new Font("Segoe UI", 8.5f)
        };
        card.Controls.Add(cb);
        return cb;
    }

    /// <summary>Add a styled button inside a card panel.</summary>
    public static Button AddCardButton(Panel card, string text, int x, int y, int width = 80)
    {
        var btn = MakeButton(text, BgMedium, x, y);
        btn.Size = new Size(width, 26);
        btn.Font = new Font("Segoe UI", 8.5f);
        card.Controls.Add(btn);
        return btn;
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
    private static Color Lighten(Color c, int amount) =>
        Color.FromArgb(
            c.A,
            Math.Min(c.R + amount, 255),
            Math.Min(c.G + amount, 255),
            Math.Min(c.B + amount, 255));
}
