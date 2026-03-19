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
        var throttle = config.Throttle;
        var layout = config.Layout;
        return $@"EQSwitch v2.5.0 — EverQuest Window Manager
============================================
GitHub: https://github.com/itsnateai/eqswitch_port

HOTKEYS:
  {hk.SwitchKey,-18} Cycle to next EQ client (EQ must be focused)
  {hk.GlobalSwitchKey,-18} Cycle next / bring EQ to front from any app
  {hk.ArrangeWindows,-18} Arrange all windows in grid layout
  {hk.ToggleMultiMonitor,-18} Toggle single-screen / multi-monitor mode
  Alt+1..6           Switch directly to client by slot number
  {(string.IsNullOrEmpty(hk.LaunchOne) ? "(not set)" : hk.LaunchOne),-18} Launch one EQ client
  {(string.IsNullOrEmpty(hk.LaunchAll) ? "(not set)" : hk.LaunchAll),-18} Launch all configured clients

TRAY ICON:
  Right-click        Context menu
  Single-click       {config.TrayClick.SingleClick}
  Double-click       {config.TrayClick.DoubleClick}
  Triple-click       {config.TrayClick.TripleClick}
  Middle-click       {config.TrayClick.MiddleClick}
  (Configurable in Settings → General tab)

LAYOUT MODES:
  Single Screen      Grid layout ({layout.Columns}x{layout.Rows}) on monitor {layout.TargetMonitor}
  Multi-Monitor      One window per physical monitor
  Borderless FS      {(layout.BorderlessFullscreen ? "ON" : "OFF")} — removes title bar + stretches to monitor

BACKGROUND FPS THROTTLING:
  Status: {(throttle.Enabled ? $"ON — {throttle.ThrottlePercent}% throttle" : "OFF")}
  Suspends background EQ clients in a duty cycle to reduce
  GPU/CPU usage. Active client is never throttled.
  Configure in Settings → General tab.

PIP (PICTURE-IN-PICTURE):
  Live DWM thumbnail preview of background EQ windows
  GPU-composited (zero CPU overhead)
  Ctrl+Drag to reposition, position saved automatically
  Auto-hides when fewer than 2 clients running

CPU AFFINITY:
  Active client   → P-cores (mask 0x{config.Affinity.ActiveMask:X}, {config.Affinity.ActivePriority})
  Background      → E-cores (mask 0x{config.Affinity.BackgroundMask:X}, {config.Affinity.BackgroundPriority})
  Auto-applies on window switch (250ms polling)
  Retries {config.Affinity.LaunchRetryCount}x after launch (EQ resets its own affinity)

LAUNCHING:
  EQ Path: {config.EQPath}
  Delay between launches: {config.Launch.LaunchDelayMs / 1000.0:F1}s
  Auto-arrange after: {config.Launch.FixDelayMs / 1000.0:F0}s

CONFIG:
  eqswitch-config.json (alongside exe)
  Auto-backup on save (keeps last 10 in backups/ folder)

TROUBLESHOOTING:
  Hotkeys not working?  Run as Administrator
  PiP not showing?      Need 2+ clients running, middle-click tray
  Affinity not sticking? Use tray menu → Force Apply Affinity
  Config lost?          Check backups/ folder next to exe

USEFUL LINKS:
  Shards Wiki:    https://wiki.shardsofdalaya.com/wiki/Main_Page
  Dalaya Wiki:    https://wiki.dalaya.org/
  Fomelo Dalaya:  https://dalaya.org/fomelo/
";
    }
}
