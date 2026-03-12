using EQSwitch.Config;

namespace EQSwitch.UI;

/// <summary>
/// Read-only help window showing hotkey reference and feature guide.
/// </summary>
public static class HelpForm
{
    public static void Show(AppConfig config)
    {
        var helpForm = new Form
        {
            Text = "EQSwitch — Help",
            Size = new Size(520, 500),
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9),
            MaximizeBox = true,
            MinimizeBox = false
        };

        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 10),
            Text = GetHelpText(config)
        };

        helpForm.Controls.Add(rtb);
        helpForm.Show();
    }

    private static string GetHelpText(AppConfig config)
    {
        var hk = config.Hotkeys;
        return $@"EQSwitch — EverQuest Window Manager
====================================

HOTKEYS:
  Switch Key ({hk.SwitchKey})     — Cycle to next EQ client (EQ must be focused)
  Global Switch ({hk.GlobalSwitchKey})  — Cycle next / bring EQ to front from any app
  {hk.ArrangeWindows}            — Arrange all windows in grid layout
  {hk.ToggleMultiMonitor}            — Toggle single-screen / multi-monitor mode
  Alt+1..6          — Switch directly to client by slot number
  {(string.IsNullOrEmpty(hk.LaunchOne) ? "(not set)" : hk.LaunchOne)}           — Launch one EQ client
  {(string.IsNullOrEmpty(hk.LaunchAll) ? "(not set)" : hk.LaunchAll)}           — Launch all configured clients

TRAY ICON:
  Right-click       — Context menu
  Double-click      — Launch one EQ client
  Middle-click      — Toggle PiP overlay

LAYOUT MODES:
  Single Screen     — Grid layout (Columns × Rows) on target monitor
  Multi-Monitor     — One window per physical monitor

PIP (PICTURE-IN-PICTURE):
  Live preview of background EQ windows
  Ctrl+Drag to reposition
  Auto-hides when fewer than 2 clients

CPU AFFINITY:
  Active client  → P-cores (high priority)
  Background     → E-cores (normal priority)
  Auto-applies on window switch (250ms check)

CONFIG:
  eqswitch-config.json (alongside exe)
  Auto-backup on save (keeps last 10)
";
    }
}
