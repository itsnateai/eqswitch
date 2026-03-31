using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Lightweight floating tooltip near the cursor, matching AHK's ToolTip() behavior.
/// Auto-dismisses after a configurable duration. No toast notifications.
/// Uses WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW to avoid stealing focus from context menus.
/// </summary>
public static class FloatingTooltip
{
    private static TooltipForm? _currentTooltip;
    private static System.Windows.Forms.Timer? _dismissTimer;

    public static void Show(string message, int durationMs = 3000)
    {
        Dismiss();

        var form = new TooltipForm(message);

        // Position near cursor
        var cursor = Cursor.Position;
        form.Location = new Point(cursor.X + 16, cursor.Y + 16);

        // Clamp to screen bounds after sizing
        var screen = Screen.FromPoint(cursor);
        form.Load += (_, _) =>
        {
            var bounds = screen.WorkingArea;
            var x = form.Left;
            var y = form.Top;
            if (x + form.Width > bounds.Right) x = bounds.Right - form.Width;
            if (y + form.Height > bounds.Bottom) y = bounds.Bottom - form.Height;
            form.Location = new Point(x, y);
        };

        _currentTooltip = form;

        // ShowWindow with SW_SHOWNOACTIVATE — never steals focus
        NativeMethods.ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE

        _dismissTimer = new System.Windows.Forms.Timer { Interval = durationMs };
        _dismissTimer.Tick += (_, _) => Dismiss();
        _dismissTimer.Start();
    }

    private static void Dismiss()
    {
        _dismissTimer?.Stop();
        _dismissTimer?.Dispose();
        _dismissTimer = null;

        if (_currentTooltip != null && !_currentTooltip.IsDisposed)
        {
            _currentTooltip.Close();
            _currentTooltip.Dispose();
        }
        _currentTooltip = null;
    }

    /// <summary>
    /// Non-activating tooltip window. WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW
    /// ensures it never steals focus or appears in taskbar/Alt+Tab.
    /// </summary>
    private class TooltipForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;

        public TooltipForm(string message)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = DarkTheme.Border;
            Padding = new Padding(1);

            var inner = new Panel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = DarkTheme.BgPanel,
                Location = new Point(1, 1),
            };
            inner.Paint += (_, e) =>
            {
                // Accent left-bar
                using var brush = new SolidBrush(DarkTheme.AccentGreen);
                e.Graphics.FillRectangle(brush, 0, 0, 2, inner.Height);
            };
            inner.Controls.Add(new Label
            {
                Text = message,
                AutoSize = true,
                Font = new Font("Segoe UI", 9),
                ForeColor = DarkTheme.FgWhite,
                BackColor = DarkTheme.BgPanel,
                Padding = new Padding(10, 5, 8, 5),
            });
            Controls.Add(inner);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose Font objects on child controls — base.Dispose doesn't do this
                foreach (Control c in Controls)
                    c.Font?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
