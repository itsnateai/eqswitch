namespace EQSwitch.UI;

/// <summary>
/// Shared dark theme colors and WinForms control factories.
/// Used by SettingsForm and other dark-themed UI.
/// </summary>
public static class DarkTheme
{
    public static readonly Color BgDark = Color.FromArgb(30, 30, 30);
    public static readonly Color BgMedium = Color.FromArgb(45, 45, 45);
    public static readonly Color BgInput = Color.FromArgb(50, 50, 50);
    public static readonly Color FgWhite = Color.White;
    public static readonly Color FgGray = Color.FromArgb(180, 180, 180);
    public static readonly Color AccentGreen = Color.FromArgb(0, 120, 80);

    public static TabPage MakeTabPage(string title)
    {
        return new TabPage(title) { BackColor = BgDark, ForeColor = FgWhite };
    }

    public static void AddLabel(Control parent, string text, int x, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgWhite
        });
    }

    public static void AddHint(Control parent, string text, int x, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgGray,
            Font = new Font("Segoe UI", 8)
        });
    }

    public static TextBox AddTextBox(Control parent, int x, int y, int width)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 25),
            BackColor = BgInput,
            ForeColor = FgWhite,
            BorderStyle = BorderStyle.FixedSingle
        };
        parent.Controls.Add(tb);
        return tb;
    }

    public static NumericUpDown AddNumeric(Control parent, int x, int y, int width, decimal defaultVal, decimal min, decimal max)
    {
        var nud = new NumericUpDown
        {
            Location = new Point(x, y),
            Size = new Size(width, 25),
            BackColor = BgInput,
            ForeColor = FgWhite,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(defaultVal, min, max),
            BorderStyle = BorderStyle.FixedSingle
        };
        parent.Controls.Add(nud);
        return nud;
    }

    public static ComboBox AddComboBox(Control parent, int x, int y, int width, string[] items)
    {
        var cb = new ComboBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 25),
            BackColor = BgInput,
            ForeColor = FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat
        };
        cb.Items.AddRange(items);
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        parent.Controls.Add(cb);
        return cb;
    }

    public static CheckBox AddCheckBox(Control parent, string text, int x, int y)
    {
        var cb = new CheckBox
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = FgWhite
        };
        parent.Controls.Add(cb);
        return cb;
    }

    public static Button MakeButton(string text, Color bgColor, int x, int y)
    {
        return new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(80, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = bgColor,
            ForeColor = FgWhite
        };
    }

    /// <summary>
    /// Clamp a value to a NumericUpDown's range.
    /// </summary>
    public static decimal ClampNud(NumericUpDown nud, decimal value) =>
        Math.Clamp(value, nud.Minimum, nud.Maximum);
}
