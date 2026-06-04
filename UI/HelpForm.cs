// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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
        // v3.24.29: report the live window mode from WindowMode (the real two-mode
        // discriminator) — SlimTitlebar is now true in BOTH modes, so the old
        // "SlimTitlebar ? ON : OFF" indicator was always ON and meaningless.
        var windowStyle = layout.WindowMode == WindowMode.Fullscreen
            ? "Fullscreen (borderless)"
            : "Windowed (slim titlebar)";
        // TargetMonitor is 0-based (0 = primary); the Settings UI shows it 1-indexed
        // ("1: 1920x1080 (primary)"), so match that convention here.
        var targetMon = layout.TargetMonitor == 0
            ? "the primary monitor"
            : $"monitor {layout.TargetMonitor + 1}";

        // BY DESIGN — do NOT "fix" to interpolate NumClients (audits/verifiers
        // keep flagging this as drift; it's a false positive). The HOTKEYS line
        // below describes LaunchAll as "Launch two bare clients". LaunchManager
        // launches config.Launch.NumClients (default 2, clamped 1-8, JSON-only —
        // there is no UI control to change it), and the action is deliberately
        // named "Launch Two" across the tray menu (TrayManager.FormatActionName)
        // and the General-tab tray-click dropdown. The help matches that
        // convention on purpose. Keep "two" unless the "Launch Two" naming is
        // changed everywhere at once (per Nate, 2026-06-04).

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
  {(string.IsNullOrEmpty(hk.LaunchAll) ? "(not set)" : hk.LaunchAll),-18} Launch two bare clients (no login)

TRAY ICON:
  Right-click        Context menu
  Single-click       {TrayManager.FormatActionName(config.TrayClick.SingleClick)}
  Double-click       {TrayManager.FormatActionName(config.TrayClick.DoubleClick)}
  Triple-click       {TrayManager.FormatActionName(config.TrayClick.TripleClick)}
  Middle-click       {TrayManager.FormatActionName(config.TrayClick.MiddleClick)}
  Middle-double      {TrayManager.FormatActionName(config.TrayClick.MiddleDoubleClick)}
  (Configurable in Settings → General tab)

WINDOW STYLE (Settings → Video → Window Style):
  Active now:        {windowStyle}
  Fullscreen mode    Borderless; window covers the taskbar
  Windowed Mode      Slim titlebar; covers taskbar (classic WinEQ2 look)

WINDOW LAYOUT:
  Single Screen      All clients stacked on {targetMon}
  Multi-Monitor      One client per physical monitor

PIP (PICTURE-IN-PICTURE):
  Live DWM thumbnail preview of background EQ windows
  GPU-composited (zero CPU overhead)
  Ctrl+Drag to reposition, position saved automatically
  Auto-hides when fewer than 2 clients running

CPU AFFINITY & PRIORITY:
  Core assignment via eqclient.ini CPUAffinity0-5 (6 slots)
  Priority: {config.Affinity.ActivePriority} (default; per-character overrides apply)
  Configure in Process Manager (tray menu)

CHARACTER PROFILES:
  Assign custom priority per character
  Import/export character lists as JSON
  Configure in Settings → Accounts tab (Characters card)

VIDEO SETTINGS:
  Edit eqclient.ini from Settings → Video
  Manages resolution, windowed mode, particles, models, etc.

CUSTOM TRAY ICON:
  Set a custom .ico file in Settings → Paths tab

AUTO-LOGIN:
  Enters your password directly into each client in the
  background — no focus stealing, all clients stay put.
  Injected into EQ at launch — no files placed in game folder.

  Autologin Teams: configure up to 12 teams of 2 accounts each.
  All teams fire from the tray menu (right-click → Teams).
  Teams 1-4 can also bind to a global hotkey.
  Teams 1-6 can also bind to a tray-click action.
  Accounts can appear in multiple teams (overlap OK).
  Configure in Settings → Accounts tab.

LAUNCHING:
  EQ Path: {config.EQPath}
  Delay between launches: {config.Launch.LaunchDelayMs / 1000.0:F1}s
  Auto-arrange after: {config.Launch.FixDelayMs / 1000.0:F0}s

INJECTED DLLs (memory-only — unload when each EQ client closes):
  eqswitch-hook.dll — prevents EQ from fighting window management
  eqswitch-di8.dll  — DirectInput hooks for background keyboard input
  No files are placed in your EQ game folder. Closing EQSwitch
  leaves running clients untouched (the DLLs stay until EQ exits).

FIRST-RUN CONFIG SEEDING:
  On first launch, EQSwitch reads your actual eqclient.ini
  and uses those values as its starting defaults. Settings
  are NOT enforced until you explicitly click Save.

UNINSTALL / CLEAN UP:
  Settings → Paths → Uninstall button (or run uninstall.bat)
  Reverts all external changes:
    • Removes startup shortcut
    • Removes desktop shortcut
    • Cleans up any legacy DLL artifacts from game folder
  Does NOT modify eqclient.ini settings or EQSwitch config.

CONFIG:
  eqswitch-config.json (alongside exe)
  Auto-backup on save (keeps last 10 in backups/ folder)

TROUBLESHOOTING:
  Hotkeys not working?   Run as Administrator
  PiP not showing?       Need 2+ clients running
  Affinity not sticking? Process Manager (tray menu) → Apply
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
