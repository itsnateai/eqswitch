using System.Reflection;
using EQSwitch.Config;

namespace EQSwitch.UI;

/// <summary>
/// Read-only help window showing hotkey reference and feature guide.
/// </summary>
public static class HelpForm
{
    public static void Show(AppConfig config)
    {
        var helpForm = new Form();
        DarkTheme.StyleForm(helpForm, "EQSwitch — Help", new Size(520, 500));
        helpForm.MaximizeBox = true;
        helpForm.MinimizeBox = false;
        helpForm.FormBorderStyle = FormBorderStyle.Sizable;

        var rtbFont = new Font("Consolas", 10);
        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = DarkTheme.BgDark,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.None,
            Font = rtbFont,
            Text = GetHelpText(config)
        };

        helpForm.Controls.Add(rtb);
        helpForm.FormClosed += (_, _) =>
        {
            helpForm.Font?.Dispose();
            rtbFont.Dispose();
        };
        helpForm.Show();
    }

    private static string GetHelpText(AppConfig config)
    {
        var hk = config.Hotkeys;
        var layout = config.Layout;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var directKeys = hk.DirectSwitchKeys.Count > 0
            ? $"{hk.DirectSwitchKeys[0]}..{hk.DirectSwitchKeys[^1]}"
            : "(not set)";

        return $@"EQSwitch v{version} — EverQuest Window Manager
============================================
GitHub: https://github.com/itsnateai/eqswitch_port

HOTKEYS:
  {hk.SwitchKey,-18} Cycle to next EQ client (EQ must be focused)
  {hk.GlobalSwitchKey,-18} Cycle next / bring EQ to front from any app
  {(string.IsNullOrEmpty(hk.ArrangeWindows) ? "(not set)" : hk.ArrangeWindows),-18} Arrange all windows in grid layout
  {(string.IsNullOrEmpty(hk.ToggleMultiMonitor) ? "(not set)" : hk.ToggleMultiMonitor),-18} Toggle single-screen / multi-monitor mode
  {directKeys,-18} Switch directly to client by slot number
  {(string.IsNullOrEmpty(hk.LaunchOne) ? "(not set)" : hk.LaunchOne),-18} Launch one EQ client
  {(string.IsNullOrEmpty(hk.LaunchAll) ? "(not set)" : hk.LaunchAll),-18} Launch all configured clients

TRAY ICON:
  Right-click        Context menu
  Single-click       {config.TrayClick.SingleClick}
  Double-click       {config.TrayClick.DoubleClick}
  Middle-click       {config.TrayClick.MiddleClick}
  Middle-double      {config.TrayClick.MiddleDoubleClick}
  (Configurable in Settings → General tab)

LAYOUT MODES:
  Single Screen      Grid layout ({layout.Columns}x{layout.Rows}) on monitor {layout.TargetMonitor}
  Multi-Monitor      One window per physical monitor
  Borderless FS      {(layout.BorderlessFullscreen ? "ON" : "OFF")} — removes title bar + stretches to monitor

PIP (PICTURE-IN-PICTURE):
  Live DWM thumbnail preview of background EQ windows
  GPU-composited (zero CPU overhead)
  Ctrl+Drag to reposition, position saved automatically
  Auto-hides when fewer than 2 clients running

CPU AFFINITY & PRIORITY:
  Core assignment via eqclient.ini CPUAffinity0-5 (6 slots)
  Priority: {config.Affinity.ActivePriority} (all clients)
  Configure in Process Manager (tray menu)

CHARACTER PROFILES:
  Assign custom priority per character
  Import/export character lists as JSON
  Configure in Settings → Characters tab

VIDEO SETTINGS:
  Edit eqclient.ini from Launcher → Video Settings
  Manages resolution, windowed mode, particles, models, etc.

CUSTOM TRAY ICON:
  Set a custom .ico file in Settings → Paths tab

LAUNCHING:
  EQ Path: {config.EQPath}
  Delay between launches: {config.Launch.LaunchDelayMs / 1000.0:F1}s
  Auto-arrange after: {config.Launch.FixDelayMs / 1000.0:F0}s

CONFIG:
  eqswitch-config.json (alongside exe)
  Auto-backup on save (keeps last 10 in backups/ folder)

TROUBLESHOOTING:
  Hotkeys not working?   Run as Administrator
  PiP not showing?       Need 2+ clients running
  Affinity not sticking? Tray → Affinity → Force Re-Apply
  Config lost?           Check backups/ folder next to exe

USEFUL LINKS:
  Dalaya:         https://dalaya.org/
  Shards Wiki:    https://wiki.shardsofdalaya.com/wiki/Main_Page
  Dalaya Wiki:    https://wiki.dalaya.org/
  Fomelo:         https://dalaya.org/fomelo/
  Listsold:       https://dalaya.org/listsold.php
";
    }
}
