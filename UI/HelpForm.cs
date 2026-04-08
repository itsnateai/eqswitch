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
            // Only dispose the locally-created Consolas font.
            // Do NOT dispose helpForm.Font — it's the shared DarkTheme.FontUI9 static.
            rtbFont.Dispose();
            helpForm.Dispose();
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
GitHub: https://github.com/itsnateai/eqswitch

HOTKEYS:
  {hk.SwitchKey,-18} Cycle to next EQ client (EQ must be focused)
  {hk.GlobalSwitchKey,-18} Cycle next / bring EQ to front from any app
  {(string.IsNullOrEmpty(hk.ArrangeWindows) ? "(not set)" : hk.ArrangeWindows),-18} Fix/arrange all windows
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
  Single Screen      Stacked on monitor {layout.TargetMonitor}
  Multi-Monitor      One window per physical monitor
  Fullscreen Window   {(layout.SlimTitlebar ? "ON" : "OFF")} — hides titlebar above screen edge (WinEQ2 mode)

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

AUTO-LOGIN:
  Types passwords via DirectInput shared memory
  (no focus stealing — all clients stay in background)
  Requires dinput8.dll in EQ directory (auto-deployed)

LAUNCHING:
  EQ Path: {config.EQPath}
  Delay between launches: {config.Launch.LaunchDelayMs / 1000.0:F1}s
  Auto-arrange after: {config.Launch.FixDelayMs / 1000.0:F0}s

DLL HOOK (eqswitch-hook.dll):
  Injected into eqgame.exe to prevent EQ from fighting
  window management. Memory-only — auto-ejected on exit.
  No files are dropped into your EQ folder by the hook.

DINPUT8.DLL (Auto-Login):
  Copied to your EQ folder when auto-login is used.
  If an existing dinput8.dll was present, it is backed up
  as dinput8.dll.old and can be restored via Uninstall.

FIRST-RUN CONFIG SEEDING:
  On first launch, EQSwitch reads your actual eqclient.ini
  and uses those values as its starting defaults. Settings
  are NOT enforced until you explicitly click Save.

UNINSTALL / CLEAN UP:
  Settings → General → Uninstall button (or run uninstall.bat)
  Reverts all external changes:
    • Restores original dinput8.dll from backup (if present)
    • Removes startup shortcut
    • Removes desktop shortcut
  Does NOT modify eqclient.ini settings or EQSwitch config.

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
