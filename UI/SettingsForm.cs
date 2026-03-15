using System.Diagnostics;
using System.Text.Json;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Settings GUI with tabbed layout. Dark medieval purple theme with card panels.
/// Each section uses emoji-titled card panels for visual clarity and grouping.
/// </summary>
public class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly Action<AppConfig> _onApply;

    // ─── General tab controls
    private TextBox _txtEQPath = null!;
    private TextBox _txtExeName = null!;
    private TextBox _txtArgs = null!;
    private TextBox _txtProcessName = null!;
    private NumericUpDown _nudPollingInterval = null!;
    private NumericUpDown _nudTooltipDuration = null!;
    private CheckBox _chkCtrlHoverHelp = null!;
    private TextBox _txtCustomIconPath = null!;
    private TextBox _txtSwitchKeyGeneral = null!;

    // ─── Tray Click controls (Left)
    private ComboBox _cboSingleClick = null!;
    private ComboBox _cboDoubleClick = null!;
    private ComboBox _cboTripleClick = null!;
    // ─── Tray Click controls (Middle)
    private ComboBox _cboMiddleClick = null!;
    private ComboBox _cboMiddleDoubleClick = null!;
    private ComboBox _cboMiddleTripleClick = null!;

    // ─── Hotkeys tab controls
    private TextBox _txtSwitchKey = null!;
    private TextBox _txtGlobalSwitchKey = null!;
    private TextBox _txtArrangeWindows = null!;
    private TextBox _txtToggleMultiMon = null!;
    private TextBox _txtLaunchOne = null!;
    private TextBox _txtLaunchAll = null!;
    private CheckBox _chkMultiMonEnabled = null!;
    private ComboBox _cboSwitchKeyMode = null!;

    // ─── Layout tab controls
    private NumericUpDown _nudColumns = null!;
    private NumericUpDown _nudRows = null!;
    private ComboBox _cboTargetMonitor = null!;
    private NumericUpDown _nudTopOffset = null!;
    private ComboBox _cboLayoutMode = null!;
    private CheckBox _chkRemoveTitleBars = null!;
    private CheckBox _chkBorderlessFullscreen = null!;

    // ─── Affinity tab controls
    private TextBox _txtActiveMask = null!;
    private TextBox _txtBackgroundMask = null!;
    private ComboBox _cboActivePriority = null!;
    private ComboBox _cboBackgroundPriority = null!;
    private CheckBox _chkAffinityEnabled = null!;
    private NumericUpDown _nudRetryCount = null!;
    private NumericUpDown _nudRetryDelay = null!;

    // ─── Launch tab controls
    private NumericUpDown _nudNumClients = null!;
    private NumericUpDown _nudLaunchDelay = null!;
    private NumericUpDown _nudFixDelay = null!;

    // ─── Paths tab controls
    private TextBox _txtGinaPath = null!;
    private TextBox _txtNotesPath = null!;

    // ─── PiP tab controls
    private CheckBox _chkPipEnabled = null!;
    private ComboBox _cboPipSize = null!;
    private NumericUpDown _nudPipWidth = null!;
    private NumericUpDown _nudPipHeight = null!;
    private NumericUpDown _nudPipOpacity = null!;
    private CheckBox _chkPipBorder = null!;
    private ComboBox _cboPipBorderColor = null!;
    private NumericUpDown _nudPipMaxWindows = null!;

    // ─── Throttle controls (on Affinity tab)
    private CheckBox _chkThrottleEnabled = null!;
    private NumericUpDown _nudThrottlePercent = null!;
    private NumericUpDown _nudThrottleCycle = null!;

    // ─── Characters tab controls
    private ListView _charListView = null!;
    private List<CharacterProfile> _pendingCharacters = null!;

    public SettingsForm(AppConfig config, Action<AppConfig> onApply)
    {
        _config = config;
        _pendingCharacters = new List<CharacterProfile>(config.Characters);
        _onApply = onApply;
        InitializeForm();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "\u2694  EQSwitch Settings  \u2694", new Size(530, 580));

        var tabs = DarkTheme.MakeTabControl();

        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildHotkeysTab());
        tabs.TabPages.Add(BuildLayoutTab());
        tabs.TabPages.Add(BuildAffinityTab());
        tabs.TabPages.Add(BuildLaunchTab());
        tabs.TabPages.Add(BuildPipTab());
        tabs.TabPages.Add(BuildPathsTab());
        tabs.TabPages.Add(BuildCharactersTab());

        // Button panel at bottom
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            BackColor = DarkTheme.BgDark
        };

        // GitHub button (left side)
        var btnGitHub = DarkTheme.MakeButton("\uD83C\uDF10 GitHub", DarkTheme.BgMedium, 10, 10);
        btnGitHub.Size = new Size(85, 30);
        btnGitHub.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/itsnateai/eqswitch_port") { UseShellExecute = true }); }
            catch { }
        };

        // Reset Defaults button (small, discreet, next to GitHub)
        var btnReset = DarkTheme.MakeButton("\u26A0 Reset", DarkTheme.BgMedium, 100, 10);
        btnReset.Size = new Size(70, 30);
        btnReset.ForeColor = Color.FromArgb(200, 100, 100);
        btnReset.Click += (_, _) =>
        {
            var result = MessageBox.Show(
                "\u26A0\uFE0F NUCLEAR OPTION \u26A0\uFE0F\n\n" +
                "This will reset ALL settings to factory defaults.\n" +
                "Your current config will be lost forever.\n\n" +
                "Are you absolutely sure?",
                "Reset to Defaults",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result == DialogResult.Yes)
            {
                var fresh = new AppConfig { IsFirstRun = false, EQPath = _config.EQPath };
                _onApply(fresh);
                ConfigManager.Save(fresh);
                Close();
            }
        };

        // Version label
        var lblVersion = new Label
        {
            Text = $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}",
            Location = new Point(175, 17),
            AutoSize = true,
            ForeColor = DarkTheme.FgDimGray,
            Font = new Font("Segoe UI", 8f)
        };

        var btnSave = DarkTheme.MakePrimaryButton("Save", 230, 10);
        btnSave.Click += (_, _) => { ApplySettings(); ConfigManager.Save(_config); Close(); };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 320, 10);
        btnApply.Click += (_, _) => { ApplySettings(); ConfigManager.Save(_config); };

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 410, 10);
        btnCancel.Click += (_, _) => Close();

        buttonPanel.Controls.AddRange(new Control[] { btnGitHub, btnReset, lblVersion, btnSave, btnApply, btnCancel });

        Controls.Add(tabs);
        Controls.Add(buttonPanel);

        PopulateFromConfig();
    }

    // ─── Tab Builders ─────────────────────────────────────────────

    private TabPage BuildGeneralTab()
    {
        var page = DarkTheme.MakeTabPage("General");
        int y = 8;

        // ─── EverQuest Setup card ────────────────────────────────
        var cardEQ = DarkTheme.MakeCard(page, "⚔", "EverQuest Setup", DarkTheme.CardGreen, 10, y, 480, 105);

        DarkTheme.AddCardLabel(cardEQ, "EQ Path:", 10, 32);
        _txtEQPath = DarkTheme.AddCardTextBox(cardEQ, 70, 30, 240);
        var btnBrowse = DarkTheme.AddCardButton(cardEQ, "Browse...", 320, 29, 75);
        btnBrowse.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { Description = "Select EverQuest folder", InitialDirectory = _txtEQPath.Text };
            if (fbd.ShowDialog() == DialogResult.OK) _txtEQPath.Text = fbd.SelectedPath;
        };

        DarkTheme.AddCardLabel(cardEQ, "Exe:", 10, 60);
        _txtExeName = DarkTheme.AddCardTextBox(cardEQ, 40, 58, 90);
        DarkTheme.AddCardLabel(cardEQ, "Args:", 140, 60);
        _txtArgs = DarkTheme.AddCardTextBox(cardEQ, 175, 58, 90);
        DarkTheme.AddCardLabel(cardEQ, "Switch:", 280, 60);
        _txtSwitchKeyGeneral = new TextBox
        {
            Location = new Point(330, 58), Size = new Size(50, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f),
            ShortcutsEnabled = false
        };
        _txtSwitchKeyGeneral.KeyDown += HotkeyBoxKeyDown;
        cardEQ.Controls.Add(_txtSwitchKeyGeneral);

        DarkTheme.AddCardLabel(cardEQ, "Process:", 10, 82);
        _txtProcessName = DarkTheme.AddCardTextBox(cardEQ, 65, 80, 90);
        DarkTheme.AddCardLabel(cardEQ, "Poll (ms):", 170, 82);
        _nudPollingInterval = DarkTheme.AddCardNumeric(cardEQ, 235, 80, 70, 500, 100, 5000);

        y += 112;

        // ─── Tray Click Actions card ─────────────────────────────
        var cardTray = DarkTheme.MakeCard(page, "🖱", "Tray Click Actions", DarkTheme.CardBlue, 10, y, 480, 115);

        var clickActions = new[] { "None", "FixWindows", "SwapWindows", "TogglePiP", "LaunchOne", "LaunchAll", "Settings", "ShowHelp" };

        DarkTheme.AddCardLabel(cardTray, "Left Click", 10, 30);
        DarkTheme.AddCardLabel(cardTray, "Middle Click", 250, 30);

        DarkTheme.AddCardLabel(cardTray, "1×", 10, 52);
        _cboSingleClick = DarkTheme.AddCardComboBox(cardTray, 30, 50, 120, clickActions);
        DarkTheme.AddCardLabel(cardTray, "1×", 250, 52);
        _cboMiddleClick = DarkTheme.AddCardComboBox(cardTray, 270, 50, 120, clickActions);

        DarkTheme.AddCardLabel(cardTray, "2×", 155, 52);
        _cboDoubleClick = DarkTheme.AddCardComboBox(cardTray, 175, 50, 65, clickActions);
        DarkTheme.AddCardLabel(cardTray, "2×", 395, 52);
        _cboMiddleDoubleClick = DarkTheme.AddCardComboBox(cardTray, 415, 50, 55, clickActions);

        DarkTheme.AddCardLabel(cardTray, "3×", 10, 80);
        _cboTripleClick = DarkTheme.AddCardComboBox(cardTray, 30, 78, 120, clickActions);
        DarkTheme.AddCardLabel(cardTray, "3×", 250, 80);
        _cboMiddleTripleClick = DarkTheme.AddCardComboBox(cardTray, 270, 78, 120, clickActions);

        y += 122;

        // ─── Preferences card ────────────────────────────────────
        var cardPrefs = DarkTheme.MakeCard(page, "⚙", "Preferences", DarkTheme.CardGold, 10, y, 480, 115);

        var btnEQSettings = DarkTheme.AddCardButton(cardPrefs, "\uD83D\uDCDD EQ Client Settings...", 10, 30, 180);
        btnEQSettings.Click += (_, _) =>
        {
            using var form = new EQClientSettingsForm(_config);
            form.ShowDialog();
        };

        DarkTheme.AddCardLabel(cardPrefs, "Tooltip (ms):", 210, 33);
        _nudTooltipDuration = DarkTheme.AddCardNumeric(cardPrefs, 300, 31, 70, 1500, 500, 10000);

        _chkCtrlHoverHelp = DarkTheme.AddCardCheckBox(cardPrefs, "Ctrl+Hover shows hotkey help", 10, 60);

        DarkTheme.AddCardLabel(cardPrefs, "Tray Icon:", 10, 88);
        _txtCustomIconPath = DarkTheme.AddCardTextBox(cardPrefs, 80, 86, 230);
        var btnBrowseIcon = DarkTheme.AddCardButton(cardPrefs, "Browse...", 320, 85, 75);
        btnBrowseIcon.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select Tray Icon",
                Filter = "Icon Files (*.ico)|*.ico",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };
            if (dlg.ShowDialog() == DialogResult.OK)
                _txtCustomIconPath.Text = dlg.FileName;
        };
        DarkTheme.AddCardHint(cardPrefs, "blank = default icon", 400, 90);

        return page;
    }

    private TabPage BuildHotkeysTab()
    {
        var page = DarkTheme.MakeTabPage("Hotkeys");
        int y = 8;

        // ─── Window Switching card ───────────────────────────────
        var cardSwitch = DarkTheme.MakeCard(page, "⚔", "Window Switching", DarkTheme.CardGreen, 10, y, 480, 100);

        DarkTheme.AddCardLabel(cardSwitch, "Switch Key (EQ-only):", 10, 35);
        _txtSwitchKey = new TextBox
        {
            Location = new Point(155, 33), Size = new Size(60, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f),
            ShortcutsEnabled = false
        };
        _txtSwitchKey.KeyDown += HotkeyBoxKeyDown;
        cardSwitch.Controls.Add(_txtSwitchKey);

        DarkTheme.AddCardLabel(cardSwitch, "Mode:", 230, 35);
        _cboSwitchKeyMode = DarkTheme.AddCardComboBox(cardSwitch, 270, 33, 120, new[] { "swapLast", "cycleAll" });

        DarkTheme.AddCardLabel(cardSwitch, "Global Switch Key:", 10, 65);
        _txtGlobalSwitchKey = new TextBox
        {
            Location = new Point(155, 63), Size = new Size(60, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f),
            ShortcutsEnabled = false
        };
        _txtGlobalSwitchKey.KeyDown += HotkeyBoxKeyDown;
        cardSwitch.Controls.Add(_txtGlobalSwitchKey);
        DarkTheme.AddCardHint(cardSwitch, "Works from any app", 225, 67);

        y += 108;

        // ─── Actions card ────────────────────────────────────────
        var cardActions = DarkTheme.MakeCard(page, "🏰", "Actions & Launcher", DarkTheme.CardGold, 10, y, 480, 105);

        DarkTheme.AddCardLabel(cardActions, "Arrange Windows:", 10, 35);
        _txtArrangeWindows = new TextBox
        {
            Location = new Point(130, 33), Size = new Size(70, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f),
            ShortcutsEnabled = false
        };
        _txtArrangeWindows.KeyDown += HotkeyBoxKeyDown;
        cardActions.Controls.Add(_txtArrangeWindows);
        DarkTheme.AddCardHint(cardActions, "optional", 208, 37);

        DarkTheme.AddCardLabel(cardActions, "Toggle Multi-Mon:", 260, 35);
        _txtToggleMultiMon = new TextBox
        {
            Location = new Point(380, 33), Size = new Size(70, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f),
            ShortcutsEnabled = false
        };
        _txtToggleMultiMon.KeyDown += HotkeyBoxKeyDown;
        cardActions.Controls.Add(_txtToggleMultiMon);

        _chkMultiMonEnabled = DarkTheme.AddCardCheckBox(cardActions, "Multi-Mon enabled", 260, 60);

        DarkTheme.AddCardLabel(cardActions, "Launch One:", 10, 67);
        _txtLaunchOne = new TextBox
        {
            Location = new Point(90, 65), Size = new Size(70, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f),
            ShortcutsEnabled = false
        };
        _txtLaunchOne.KeyDown += HotkeyBoxKeyDown;
        cardActions.Controls.Add(_txtLaunchOne);

        DarkTheme.AddCardLabel(cardActions, "Launch All:", 170, 67);
        _txtLaunchAll = new TextBox
        {
            Location = new Point(245, 65), Size = new Size(70, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f),
            ShortcutsEnabled = false
        };
        _txtLaunchAll.KeyDown += HotkeyBoxKeyDown;
        cardActions.Controls.Add(_txtLaunchAll);

        y += 113;

        DarkTheme.AddHint(page, "Press key combo to capture. Leave blank to disable. Backspace/Delete to clear.", 15, y);

        return page;
    }

    private TabPage BuildLayoutTab()
    {
        var page = DarkTheme.MakeTabPage("Layout");
        int y = 8;

        // ─── Grid Layout card ────────────────────────────────────
        var cardGrid = DarkTheme.MakeCard(page, "📐", "Grid Layout", DarkTheme.CardGold, 10, y, 480, 100);

        DarkTheme.AddCardLabel(cardGrid, "Mode:", 10, 35);
        _cboLayoutMode = DarkTheme.AddCardComboBox(cardGrid, 50, 33, 130, new[] { "single", "multimonitor" });

        DarkTheme.AddCardLabel(cardGrid, "Columns:", 200, 35);
        _nudColumns = DarkTheme.AddCardNumeric(cardGrid, 260, 33, 55, 2, 1, 4);

        DarkTheme.AddCardLabel(cardGrid, "Rows:", 330, 35);
        _nudRows = DarkTheme.AddCardNumeric(cardGrid, 370, 33, 55, 2, 1, 4);

        DarkTheme.AddCardHint(cardGrid, "Grid divides the monitor into columns × rows for window placement", 10, 65);

        y += 108;

        // ─── Monitor card ────────────────────────────────────────
        var cardMon = DarkTheme.MakeCard(page, "🖥", "Monitor Selection", DarkTheme.CardBlue, 10, y, 480, 100);

        DarkTheme.AddCardLabel(cardMon, "Target Monitor:", 10, 35);
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ToArray();
        var monitorItems = new string[screens.Length];
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var primary = s.Primary ? " (primary)" : "";
            monitorItems[i] = $"{i}: {s.Bounds.Width}x{s.Bounds.Height}{primary}";
        }
        _cboTargetMonitor = DarkTheme.AddCardComboBox(cardMon, 120, 33, 170, monitorItems);

        var btnIdentify = DarkTheme.AddCardButton(cardMon, "🔍 Identify", 300, 32, 90);
        btnIdentify.Click += (_, _) => ShowMonitorIdentifiers();

        DarkTheme.AddCardLabel(cardMon, "Top Offset (px):", 10, 67);
        _nudTopOffset = DarkTheme.AddCardNumeric(cardMon, 120, 65, 70, 0, -100, 200);
        DarkTheme.AddCardHint(cardMon, "Offset from monitor top edge (for taskbar)", 200, 69);

        y += 108;

        // ─── Window Style card ───────────────────────────────────
        var cardStyle = DarkTheme.MakeCard(page, "🪟", "Window Style", DarkTheme.CardPurple, 10, y, 480, 90);

        _chkRemoveTitleBars = DarkTheme.AddCardCheckBox(cardStyle, "Remove Title Bars on Arrange", 10, 34);
        _chkBorderlessFullscreen = DarkTheme.AddCardCheckBox(cardStyle, "Borderless Fullscreen", 10, 58);
        DarkTheme.AddCardHint(cardStyle, "Fills screen without exclusive fullscreen — preserves Alt+Tab and PiP", 230, 60);

        return page;
    }

    private void ShowMonitorIdentifiers()
    {
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ToArray();
        var overlays = new List<Form>();

        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var overlay = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                BackColor = Color.FromArgb(20, 20, 25),
                Opacity = 0.85,
                TopMost = true,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Size = new Size(180, 120),
            };
            overlay.Location = new Point(
                screen.Bounds.Left + (screen.Bounds.Width - overlay.Width) / 2,
                screen.Bounds.Top + (screen.Bounds.Height - overlay.Height) / 2);

            var lbl = new Label
            {
                Text = $"Monitor {i}",
                Font = new Font("Segoe UI", 24, FontStyle.Bold),
                ForeColor = Color.FromArgb(34, 180, 85),
                BackColor = Color.Transparent,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            overlay.Controls.Add(lbl);
            overlay.Show();
            overlays.Add(overlay);
        }

        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            foreach (var o in overlays)
            {
                o.Close();
                o.Dispose();
            }
        };
        timer.Start();
    }

    private TabPage BuildAffinityTab()
    {
        var page = DarkTheme.MakeTabPage("Affinity");
        int y = 8;

        // ─── CPU Affinity header ─────────────────────────────────
        _chkAffinityEnabled = DarkTheme.AddCheckBox(page, "Enable CPU Affinity Management", 15, y);

        var (coreCount, sysMask) = AffinityManager.DetectCores();
        var cpuLabel = new Label
        {
            Text = $"CPU: {coreCount} cores detected",
            Location = new Point(270, y + 2),
            AutoSize = true,
            ForeColor = DarkTheme.FgDimGray,
            Font = new Font("Segoe UI", 8f)
        };
        page.Controls.Add(cpuLabel);
        y += 30;

        // ─── Active Client card ──────────────────────────────────
        var panelActive = DarkTheme.MakeCard(page, "⚔", "Active Client", DarkTheme.CardGreen, 10, y, 230, 130);

        DarkTheme.AddCardLabel(panelActive, "Core Mask (hex):", 10, 38);
        _txtActiveMask = DarkTheme.AddCardTextBox(panelActive, 125, 36, 80);
        _txtActiveMask.Font = new Font("Consolas", 9.5f);

        DarkTheme.AddCardLabel(panelActive, "Priority:", 10, 68);
        var priorities = new[] { "Idle", "BelowNormal", "Normal", "AboveNormal", "High" };
        _cboActivePriority = DarkTheme.AddCardComboBox(panelActive, 70, 66, 130, priorities);

        DarkTheme.AddCardHint(panelActive, "FF = P-cores 0-7", 10, 100);

        // ─── Background Clients card ─────────────────────────────
        var panelBg = DarkTheme.MakeCard(page, "🛡", "Background Clients", DarkTheme.CardBlue, 250, y, 230, 130);

        DarkTheme.AddCardLabel(panelBg, "Core Mask (hex):", 10, 38);
        _txtBackgroundMask = DarkTheme.AddCardTextBox(panelBg, 125, 36, 80);
        _txtBackgroundMask.Font = new Font("Consolas", 9.5f);

        DarkTheme.AddCardLabel(panelBg, "Priority:", 10, 68);
        _cboBackgroundPriority = DarkTheme.AddCardComboBox(panelBg, 70, 66, 130, priorities);

        DarkTheme.AddCardHint(panelBg, "FF00 = E-cores 8-15", 10, 100);

        y += 138;

        // Utility buttons
        var btnAllCores = DarkTheme.MakeButton("All Cores", DarkTheme.BgMedium, 15, y);
        btnAllCores.Size = new Size(85, 26);
        btnAllCores.Click += (_, _) =>
        {
            _txtActiveMask.Text = sysMask.ToString("X");
            _txtBackgroundMask.Text = sysMask.ToString("X");
        };
        page.Controls.Add(btnAllCores);

        var btnResetAff = DarkTheme.MakeButton("Reset Defaults", DarkTheme.BgMedium, 110, y);
        btnResetAff.Size = new Size(105, 26);
        btnResetAff.Click += (_, _) =>
        {
            _txtActiveMask.Text = "FF";
            _txtBackgroundMask.Text = "FF00";
            _cboActivePriority.SelectedItem = "AboveNormal";
            _cboBackgroundPriority.SelectedItem = "Normal";
        };
        page.Controls.Add(btnResetAff);

        DarkTheme.AddLabel(page, "Retries:", 270, y + 3);
        _nudRetryCount = DarkTheme.AddNumeric(page, 325, y, 55, 3, 0, 10);
        DarkTheme.AddLabel(page, "Delay:", 390, y + 3);
        _nudRetryDelay = DarkTheme.AddNumeric(page, 430, y, 55, 2000, 500, 10000);

        y += 38;

        // ─── Background FPS Throttling card ──────────────────────
        var cardThrottle = DarkTheme.MakeCard(page, "⏱", "Background FPS Throttling", DarkTheme.CardCyan, 10, y, 480, 105);

        _chkThrottleEnabled = DarkTheme.AddCardCheckBox(cardThrottle, "Enable Background Throttling", 10, 32);
        DarkTheme.AddCardHint(cardThrottle, "Suspends background EQ clients in cycles to reduce GPU/CPU load", 230, 35);

        DarkTheme.AddCardLabel(cardThrottle, "Throttle %:", 10, 62);
        _nudThrottlePercent = DarkTheme.AddCardNumeric(cardThrottle, 85, 60, 55, 50, 0, 90);

        DarkTheme.AddCardLabel(cardThrottle, "Cycle (ms):", 155, 62);
        _nudThrottleCycle = DarkTheme.AddCardNumeric(cardThrottle, 225, 60, 55, 100, 50, 1000);

        DarkTheme.AddCardHint(cardThrottle, "50% = half FPS   75% = quarter FPS   Lower cycle = smoother", 10, 85);

        return page;
    }

    private TabPage BuildLaunchTab()
    {
        var page = DarkTheme.MakeTabPage("Launch");
        int y = 8;

        // ─── Launch Settings card ────────────────────────────────
        var cardLaunch = DarkTheme.MakeCard(page, "🚀", "Launch Settings", DarkTheme.CardGreen, 10, y, 480, 145);

        DarkTheme.AddCardLabel(cardLaunch, "Number of Clients (Launch All):", 10, 35);
        _nudNumClients = DarkTheme.AddCardNumeric(cardLaunch, 230, 33, 65, 2, 1, 8);

        DarkTheme.AddCardLabel(cardLaunch, "Delay Between Launches (ms):", 10, 65);
        _nudLaunchDelay = DarkTheme.AddCardNumeric(cardLaunch, 230, 63, 80, 3000, 500, 30000);

        DarkTheme.AddCardLabel(cardLaunch, "Window Fix Delay (ms):", 10, 95);
        _nudFixDelay = DarkTheme.AddCardNumeric(cardLaunch, 230, 93, 80, 15000, 1000, 60000);

        DarkTheme.AddCardHint(cardLaunch, "Wait time after all clients launched before arranging windows", 10, 120);

        return page;
    }

    private TabPage BuildPathsTab()
    {
        var page = DarkTheme.MakeTabPage("Paths");
        int y = 8;

        // ─── External Tools card ─────────────────────────────────
        var cardPaths = DarkTheme.MakeCard(page, "📁", "External Tools", DarkTheme.CardGold, 10, y, 480, 155);

        DarkTheme.AddCardLabel(cardPaths, "GINA Path:", 10, 35);
        _txtGinaPath = DarkTheme.AddCardTextBox(cardPaths, 80, 33, 280);
        var btnBrowseGina = DarkTheme.AddCardButton(cardPaths, "Browse...", 370, 32, 75);
        btnBrowseGina.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select GINA executable",
                Filter = "Executables|*.exe|All Files|*.*",
                InitialDirectory = Path.GetDirectoryName(_txtGinaPath.Text) ?? ""
            };
            if (ofd.ShowDialog() == DialogResult.OK) _txtGinaPath.Text = ofd.FileName;
        };

        DarkTheme.AddCardLabel(cardPaths, "Notes File:", 10, 70);
        _txtNotesPath = DarkTheme.AddCardTextBox(cardPaths, 80, 68, 280);
        var btnBrowseNotes = DarkTheme.AddCardButton(cardPaths, "Browse...", 370, 67, 75);
        btnBrowseNotes.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select notes file",
                Filter = "Text Files|*.txt|All Files|*.*",
                InitialDirectory = Path.GetDirectoryName(_txtNotesPath.Text) ?? ""
            };
            if (ofd.ShowDialog() == DialogResult.OK) _txtNotesPath.Text = ofd.FileName;
        };

        DarkTheme.AddCardHint(cardPaths, "Leave blank for defaults. GINA launches the app; Notes opens a text file.", 10, 105);

        return page;
    }

    private TabPage BuildPipTab()
    {
        var page = DarkTheme.MakeTabPage("PiP");
        int y = 8;

        // ─── PiP Overlay card ────────────────────────────────────
        var cardPip = DarkTheme.MakeCard(page, "👁", "PiP Overlay", DarkTheme.CardCyan, 10, y, 480, 105);

        _chkPipEnabled = DarkTheme.AddCardCheckBox(cardPip, "Enable PiP Overlay", 10, 32);

        DarkTheme.AddCardLabel(cardPip, "Size Preset:", 10, 60);
        _cboPipSize = DarkTheme.AddCardComboBox(cardPip, 90, 58, 110, new[] { "Small", "Medium", "Large", "XL", "XXL", "Custom" });
        _cboPipSize.SelectedIndexChanged += (_, _) =>
        {
            bool isCustom = _cboPipSize.SelectedItem?.ToString() == "Custom";
            _nudPipWidth.Enabled = isCustom;
            _nudPipHeight.Enabled = isCustom;
        };

        DarkTheme.AddCardLabel(cardPip, "W:", 220, 60);
        _nudPipWidth = DarkTheme.AddCardNumeric(cardPip, 240, 58, 65, 320, 100, 1920);
        _nudPipWidth.Enabled = false;

        DarkTheme.AddCardLabel(cardPip, "H:", 315, 60);
        _nudPipHeight = DarkTheme.AddCardNumeric(cardPip, 335, 58, 65, 240, 100, 1080);
        _nudPipHeight.Enabled = false;

        DarkTheme.AddCardLabel(cardPip, "Max:", 410, 60);
        _nudPipMaxWindows = DarkTheme.AddCardNumeric(cardPip, 445, 58, 25, 3, 1, 3);

        DarkTheme.AddCardHint(cardPip, "DWM thumbnail — zero CPU, GPU composited", 220, 35);

        y += 113;

        // ─── Appearance card ─────────────────────────────────────
        var cardLook = DarkTheme.MakeCard(page, "🎨", "Appearance", DarkTheme.CardPurple, 10, y, 480, 95);

        DarkTheme.AddCardLabel(cardLook, "Opacity (0-255):", 10, 35);
        _nudPipOpacity = DarkTheme.AddCardNumeric(cardLook, 120, 33, 60, 245, 0, 255);

        _chkPipBorder = DarkTheme.AddCardCheckBox(cardLook, "Show Border", 200, 34);
        _chkPipBorder.CheckedChanged += (_, _) =>
        {
            _cboPipBorderColor.Enabled = _chkPipBorder.Checked;
        };

        DarkTheme.AddCardLabel(cardLook, "Color:", 310, 35);
        _cboPipBorderColor = DarkTheme.AddCardComboBox(cardLook, 350, 33, 90, new[] { "Green", "Blue", "Red", "Black" });

        DarkTheme.AddCardHint(cardLook, "245 = near-opaque. Border highlights active PiP on hover.", 10, 65);

        return page;
    }

    private TabPage BuildCharactersTab()
    {
        var page = DarkTheme.MakeTabPage("Characters");
        int y = 8;

        // ─── Character Profiles card ─────────────────────────────
        var cardChars = DarkTheme.MakeCard(page, "🧙", "Character Profiles", DarkTheme.CardPurple, 10, y, 480, 320);

        _charListView = new ListView
        {
            Location = new Point(10, 32),
            Size = new Size(460, 240),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        _charListView.Columns.Add("Name", 130);
        _charListView.Columns.Add("Class", 100);
        _charListView.Columns.Add("Slot", 50);
        _charListView.Columns.Add("Affinity", 100);
        cardChars.Controls.Add(_charListView);

        var btnExport = DarkTheme.AddCardButton(cardChars, "📤 Export...", 10, 280, 100);
        btnExport.Click += (_, _) => ExportCharacters();

        var btnImport = DarkTheme.AddCardButton(cardChars, "📥 Import...", 120, 280, 100);
        btnImport.Click += (_, _) => ImportCharacters();

        DarkTheme.AddCardHint(cardChars, "Export/Import character profiles as JSON files", 230, 286);

        return page;
    }

    private void ExportCharacters()
    {
        if (_pendingCharacters.Count == 0)
        {
            MessageBox.Show("No character profiles to export.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = "Export Character Profiles",
            Filter = "JSON Files|*.json",
            FileName = "eqswitch-characters.json"
        };

        if (sfd.ShowDialog() == DialogResult.OK)
        {
            try
            {
                var json = JsonSerializer.Serialize(_pendingCharacters, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(sfd.FileName, json);
                Debug.WriteLine($"Exported {_pendingCharacters.Count} characters to {sfd.FileName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Export failed: {ex.Message}");
                MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void ImportCharacters()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Import Character Profiles",
            Filter = "JSON Files|*.json"
        };

        if (ofd.ShowDialog() == DialogResult.OK)
        {
            try
            {
                var json = File.ReadAllText(ofd.FileName);
                var imported = JsonSerializer.Deserialize<List<CharacterProfile>>(json);
                if (imported != null && imported.Count > 0)
                {
                    _pendingCharacters = imported;
                    RefreshCharacterList();
                    Debug.WriteLine($"Imported {imported.Count} characters from {ofd.FileName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Import failed: {ex.Message}");
                MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private Label? _charEmptyHint;

    private void RefreshCharacterList()
    {
        _charListView.Items.Clear();
        foreach (var c in _pendingCharacters)
        {
            var item = new ListViewItem(c.Name);
            item.SubItems.Add(c.Class);
            item.SubItems.Add((c.SlotIndex + 1).ToString());
            item.SubItems.Add(c.AffinityOverride.HasValue ? $"0x{c.AffinityOverride.Value:X}" : "(default)");
            _charListView.Items.Add(item);
        }

        // Show/hide empty state hint
        if (_charEmptyHint == null)
        {
            _charEmptyHint = new Label
            {
                Text = "No character profiles loaded.\nUse Import to load profiles from a JSON file.",
                AutoSize = false,
                Size = new Size(400, 40),
                Location = new Point(25, 100),
                ForeColor = DarkTheme.FgDimGray,
                BackColor = DarkTheme.BgInput,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9, FontStyle.Italic)
            };
            _charListView.Controls.Add(_charEmptyHint);
        }
        _charEmptyHint.Visible = _pendingCharacters.Count == 0;
    }

    // ─── Shared Hotkey Handler ──────────────────────────────────

    /// <summary>
    /// Shared KeyDown handler for all hotkey TextBoxes. Suppresses beep,
    /// captures modifier+key combos, formats as "Ctrl+Alt+K" strings.
    /// </summary>
    private void HotkeyBoxKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;

        // Ignore standalone modifiers
        if (e.KeyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey)
            return;

        // Delete/Backspace clears the field
        if (e.KeyCode is Keys.Delete or Keys.Back && !e.Control && !e.Alt && !e.Shift)
        {
            if (sender is TextBox tb) tb.Text = "";
            return;
        }

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");

        string keyName = e.KeyCode switch
        {
            Keys.OemPipe or Keys.OemBackslash => "\\",
            Keys.OemCloseBrackets => "]",
            Keys.OemOpenBrackets => "[",
            _ => e.KeyCode.ToString()
        };
        parts.Add(keyName);
        if (sender is TextBox box) box.Text = string.Join("+", parts);
    }

    // ─── Config I/O ───────────────────────────────────────────────

    private void PopulateFromConfig()
    {
        // General
        _txtEQPath.Text = _config.EQPath;
        _txtExeName.Text = _config.Launch.ExeName;
        _txtArgs.Text = _config.Launch.Arguments;
        _txtProcessName.Text = _config.EQProcessName;
        _nudPollingInterval.Value = Math.Clamp(_config.PollingIntervalMs, (int)_nudPollingInterval.Minimum, (int)_nudPollingInterval.Maximum);
        _nudTooltipDuration.Value = Math.Clamp(_config.TooltipDurationMs, (int)_nudTooltipDuration.Minimum, (int)_nudTooltipDuration.Maximum);
        _chkCtrlHoverHelp.Checked = _config.CtrlHoverHelp;
        _txtCustomIconPath.Text = _config.CustomIconPath;

        // Tray Click Actions
        _cboSingleClick.SelectedItem = _config.TrayClick.SingleClick;
        _cboDoubleClick.SelectedItem = _config.TrayClick.DoubleClick;
        _cboTripleClick.SelectedItem = _config.TrayClick.TripleClick;
        _cboMiddleClick.SelectedItem = _config.TrayClick.MiddleClick;
        _cboMiddleDoubleClick.SelectedItem = _config.TrayClick.MiddleDoubleClick;
        _cboMiddleTripleClick.SelectedItem = _config.TrayClick.MiddleTripleClick;

        // Hotkeys
        _txtSwitchKeyGeneral.Text = _config.Hotkeys.SwitchKey;
        _txtSwitchKey.Text = _config.Hotkeys.SwitchKey;
        _cboSwitchKeyMode.SelectedItem = _config.Hotkeys.SwitchKeyMode;
        _txtGlobalSwitchKey.Text = _config.Hotkeys.GlobalSwitchKey;
        _txtArrangeWindows.Text = _config.Hotkeys.ArrangeWindows;
        _txtToggleMultiMon.Text = _config.Hotkeys.ToggleMultiMonitor;
        _txtLaunchOne.Text = _config.Hotkeys.LaunchOne;
        _txtLaunchAll.Text = _config.Hotkeys.LaunchAll;
        _chkMultiMonEnabled.Checked = _config.Hotkeys.MultiMonitorEnabled;

        // Layout
        _cboLayoutMode.SelectedItem = _config.Layout.Mode;
        _nudColumns.Value = DarkTheme.ClampNud(_nudColumns, _config.Layout.Columns);
        _nudRows.Value = DarkTheme.ClampNud(_nudRows, _config.Layout.Rows);
        var targetIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, _cboTargetMonitor.Items.Count - 1);
        _cboTargetMonitor.SelectedIndex = targetIdx;
        _nudTopOffset.Value = DarkTheme.ClampNud(_nudTopOffset, _config.Layout.TopOffset);
        _chkRemoveTitleBars.Checked = _config.Layout.RemoveTitleBars;
        _chkBorderlessFullscreen.Checked = _config.Layout.BorderlessFullscreen;

        // Affinity
        _chkAffinityEnabled.Checked = _config.Affinity.Enabled;
        _txtActiveMask.Text = _config.Affinity.ActiveMask.ToString("X");
        _txtBackgroundMask.Text = _config.Affinity.BackgroundMask.ToString("X");
        _cboActivePriority.SelectedItem = _config.Affinity.ActivePriority;
        _cboBackgroundPriority.SelectedItem = _config.Affinity.BackgroundPriority;
        _nudRetryCount.Value = DarkTheme.ClampNud(_nudRetryCount, _config.Affinity.LaunchRetryCount);
        _nudRetryDelay.Value = DarkTheme.ClampNud(_nudRetryDelay, _config.Affinity.LaunchRetryDelayMs);

        // Launch
        _nudNumClients.Value = DarkTheme.ClampNud(_nudNumClients, _config.Launch.NumClients);
        _nudLaunchDelay.Value = DarkTheme.ClampNud(_nudLaunchDelay, _config.Launch.LaunchDelayMs);
        _nudFixDelay.Value = DarkTheme.ClampNud(_nudFixDelay, _config.Launch.FixDelayMs);

        // Paths
        _txtGinaPath.Text = _config.GinaPath;
        _txtNotesPath.Text = _config.NotesPath;

        // PiP
        _chkPipEnabled.Checked = _config.Pip.Enabled;
        _cboPipSize.SelectedItem = _config.Pip.SizePreset;
        _nudPipWidth.Value = Math.Clamp(_config.Pip.CustomWidth, 100, 1920);
        _nudPipHeight.Value = Math.Clamp(_config.Pip.CustomHeight, 100, 1080);
        _nudPipOpacity.Value = _config.Pip.Opacity;
        _chkPipBorder.Checked = _config.Pip.ShowBorder;
        _cboPipBorderColor.SelectedItem = _config.Pip.BorderColor;
        _nudPipMaxWindows.Value = Math.Clamp(_config.Pip.MaxWindows, 1, 3);
        _nudPipWidth.Enabled = _config.Pip.SizePreset == "Custom";
        _nudPipHeight.Enabled = _config.Pip.SizePreset == "Custom";
        _cboPipBorderColor.Enabled = _config.Pip.ShowBorder;

        // Throttle
        _chkThrottleEnabled.Checked = _config.Throttle.Enabled;
        _nudThrottlePercent.Value = DarkTheme.ClampNud(_nudThrottlePercent, _config.Throttle.ThrottlePercent);
        _nudThrottleCycle.Value = DarkTheme.ClampNud(_nudThrottleCycle, _config.Throttle.CycleIntervalMs);

        // Characters
        RefreshCharacterList();
    }

    private void ApplySettings()
    {
        // Build a new config from form values
        var newConfig = new AppConfig
        {
            IsFirstRun = false,
            EQPath = _txtEQPath.Text.Trim(),
            EQProcessName = _txtProcessName.Text.Trim(),
            PollingIntervalMs = (int)_nudPollingInterval.Value,
            TooltipDurationMs = (int)_nudTooltipDuration.Value,
            CtrlHoverHelp = _chkCtrlHoverHelp.Checked,
            CustomIconPath = _txtCustomIconPath.Text.Trim(),
            Layout = new WindowLayout
            {
                Mode = _cboLayoutMode.SelectedItem?.ToString() ?? "single",
                Columns = (int)_nudColumns.Value,
                Rows = (int)_nudRows.Value,
                TargetMonitor = _cboTargetMonitor.SelectedIndex >= 0 ? _cboTargetMonitor.SelectedIndex : 0,
                TopOffset = (int)_nudTopOffset.Value,
                RemoveTitleBars = _chkRemoveTitleBars.Checked,
                BorderlessFullscreen = _chkBorderlessFullscreen.Checked
            },
            Affinity = new AffinityConfig
            {
                Enabled = _chkAffinityEnabled.Checked,
                ActiveMask = ParseHexMask(_txtActiveMask.Text, _config.Affinity.ActiveMask),
                BackgroundMask = ParseHexMask(_txtBackgroundMask.Text, _config.Affinity.BackgroundMask),
                ActivePriority = _cboActivePriority.SelectedItem?.ToString() ?? "AboveNormal",
                BackgroundPriority = _cboBackgroundPriority.SelectedItem?.ToString() ?? "Normal",
                LaunchRetryCount = (int)_nudRetryCount.Value,
                LaunchRetryDelayMs = (int)_nudRetryDelay.Value
            },
            Hotkeys = new HotkeyConfig
            {
                // General tab switch key takes priority if user edited it there
                SwitchKey = !string.IsNullOrEmpty(_txtSwitchKeyGeneral.Text.Trim())
                    ? _txtSwitchKeyGeneral.Text.Trim()
                    : _txtSwitchKey.Text.Trim(),
                GlobalSwitchKey = _txtGlobalSwitchKey.Text.Trim(),
                ArrangeWindows = _txtArrangeWindows.Text.Trim(),
                ToggleMultiMonitor = _txtToggleMultiMon.Text.Trim(),
                LaunchOne = _txtLaunchOne.Text.Trim(),
                LaunchAll = _txtLaunchAll.Text.Trim(),
                MultiMonitorEnabled = _chkMultiMonEnabled.Checked,
                DirectSwitchKeys = _config.Hotkeys.DirectSwitchKeys,
                SwitchKeyMode = _cboSwitchKeyMode.SelectedItem?.ToString() ?? "swapLast"
            },
            Launch = new LaunchConfig
            {
                ExeName = _txtExeName.Text.Trim(),
                Arguments = _txtArgs.Text.Trim(),
                NumClients = (int)_nudNumClients.Value,
                LaunchDelayMs = (int)_nudLaunchDelay.Value,
                FixDelayMs = (int)_nudFixDelay.Value
            },
            Pip = new PipConfig
            {
                Enabled = _chkPipEnabled.Checked,
                SizePreset = _cboPipSize.SelectedItem?.ToString() ?? "Medium",
                CustomWidth = (int)_nudPipWidth.Value,
                CustomHeight = (int)_nudPipHeight.Value,
                Opacity = (byte)_nudPipOpacity.Value,
                ShowBorder = _chkPipBorder.Checked,
                BorderColor = _cboPipBorderColor.SelectedItem?.ToString() ?? "Green",
                MaxWindows = (int)_nudPipMaxWindows.Value,
                SavedPositions = _config.Pip.SavedPositions // preserve existing positions
            },
            Throttle = new ThrottleConfig
            {
                Enabled = _chkThrottleEnabled.Checked,
                ThrottlePercent = (int)_nudThrottlePercent.Value,
                CycleIntervalMs = (int)_nudThrottleCycle.Value
            },
            TrayClick = new TrayClickConfig
            {
                SingleClick = _cboSingleClick.SelectedItem?.ToString() ?? "None",
                DoubleClick = _cboDoubleClick.SelectedItem?.ToString() ?? "LaunchOne",
                TripleClick = _cboTripleClick.SelectedItem?.ToString() ?? "LaunchAll",
                MiddleClick = _cboMiddleClick.SelectedItem?.ToString() ?? "TogglePiP",
                MiddleDoubleClick = _cboMiddleDoubleClick.SelectedItem?.ToString() ?? "None",
                MiddleTripleClick = _cboMiddleTripleClick.SelectedItem?.ToString() ?? "None"
            },
            GinaPath = _txtGinaPath.Text.Trim(),
            NotesPath = _txtNotesPath.Text.Trim(),
            Characters = _pendingCharacters
        };

        _onApply(newConfig);
        Debug.WriteLine("Settings applied");
    }

    private static long ParseHexMask(string hex, long fallback)
    {
        hex = hex.Trim().TrimStart('0', 'x', 'X');
        if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long result))
            return result;
        return fallback;
    }

}
