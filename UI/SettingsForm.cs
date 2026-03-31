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
    private readonly Action? _openProcessManager;

    // Track monitor identifier overlays to prevent stacking on rapid clicks
    private List<Form>? _monitorOverlays;
    private System.Windows.Forms.Timer? _monitorOverlayTimer;

    // ─── General tab controls
    private TextBox _txtEQPath = null!;
    private TextBox _txtExeName = null!;
    private TextBox _txtArgs = null!;
    private NumericUpDown _nudTooltipDuration = null!;
    private TextBox _txtCustomIconPath = null!;
    private TextBox _txtSwitchKeyGeneral = null!;
    private Label _lblSwitchKey = null!;

    // ─── Tray Click controls (Left)
    private ComboBox _cboSingleClick = null!;
    private ComboBox _cboDoubleClick = null!;
    // ─── Tray Click controls (Middle)
    private ComboBox _cboMiddleClick = null!;
    private ComboBox _cboMiddleDoubleClick = null!;

    // ─── Hotkeys tab controls
    private TextBox _txtSwitchKey = null!;
    private TextBox _txtGlobalSwitchKey = null!;
    private Label _lblDuplicateKeyWarn = null!;
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
    private ComboBox _cboSecondaryMonitor = null!;
    private NumericUpDown _nudTopOffset = null!;
    private CheckBox _chkRemoveTitleBars = null!;
    private CheckBox _chkBorderlessFullscreen = null!;


    // ─── Launch tab controls
    private NumericUpDown _nudNumClients = null!;
    private NumericUpDown _nudLaunchDelay = null!;

    // ─── Paths tab controls
    private TextBox _txtGinaPath = null!;
    private TextBox _txtNotesPath = null!;
    private TextBox _txtDalayaPatcherPath = null!;

    // ─── PiP tab controls
    private CheckBox _chkPipEnabled = null!;
    private ComboBox _cboPipSize = null!;
    private NumericUpDown _nudPipWidth = null!;
    private NumericUpDown _nudPipHeight = null!;
    private NumericUpDown _nudPipOpacity = null!;
    private CheckBox _chkPipBorder = null!;
    private ComboBox _cboPipBorderColor = null!;
    private NumericUpDown _nudPipMaxWindows = null!;




    private int _initialTab;

    public SettingsForm(AppConfig config, Action<AppConfig> onApply, int initialTab = 0, Action? openProcessManager = null)
    {
        _config = config;
        _onApply = onApply;
        _openProcessManager = openProcessManager;
        _initialTab = initialTab;
        InitializeForm();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "\u2694  EQSwitch Settings  \u2694", new Size(530, 580));

        // Restore last window position
        if (_config.SettingsWindowPos.Length >= 2)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(_config.SettingsWindowPos[0], _config.SettingsWindowPos[1]);
            // Clamp to visible screen
            var screen = Screen.FromRectangle(Bounds).WorkingArea;
            Location = new Point(
                Math.Clamp(Location.X, screen.Left, screen.Right - Width),
                Math.Clamp(Location.Y, screen.Top, screen.Bottom - Height));
        }

        FormClosing += (_, _) =>
        {
            _config.SettingsWindowPos = new[] { Location.X, Location.Y };
            ConfigManager.Save(_config);
        };

        var tabs = DarkTheme.MakeTabControl();

        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildHotkeysTab());
        tabs.TabPages.Add(BuildLayoutTab());
        tabs.TabPages.Add(BuildPipTab());
        tabs.TabPages.Add(BuildPathsTab());

        if (_initialTab > 0 && _initialTab < tabs.TabCount)
            tabs.SelectedIndex = _initialTab;

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
        btnReset.ForeColor = DarkTheme.CardWarn;
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

        // ─── Alignment grid: labels at L=10, inputs at L=120, browse at L=370 ───
        const int L = 10, I = 120, I2 = 310, BRW = 370, IW = 240, R = 28;

        // ─── EverQuest Setup card ────────────────────────────────
        var cardEQ = DarkTheme.MakeCard(page, "⚔", "EverQuest Setup", DarkTheme.CardGreen, 10, y, 480, 130);
        int cy = 30;

        // Switch key — prominent, right under card title
        _lblSwitchKey = DarkTheme.AddCardLabel(cardEQ, "EQ Switch Key:", L, cy);
        _lblSwitchKey.Font = new Font("Segoe UI Semibold", 8.5f);
        _txtSwitchKeyGeneral = new TextBox
        {
            Location = new Point(I, cy - 2), Size = new Size(80, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10f, FontStyle.Bold),
            TextAlign = HorizontalAlignment.Center,
            ShortcutsEnabled = false
        };
        _txtSwitchKeyGeneral.KeyDown += HotkeyBoxKeyDown;
        _txtSwitchKeyGeneral.TextChanged += (_, _) =>
        {
            UpdateSwitchKeyColor();
            if (_txtSwitchKey != null && _txtSwitchKey.Text != _txtSwitchKeyGeneral.Text)
                _txtSwitchKey.Text = _txtSwitchKeyGeneral.Text;
        };
        cardEQ.Controls.Add(_txtSwitchKeyGeneral);
        DarkTheme.WrapWithBorder(_txtSwitchKeyGeneral);
        DarkTheme.AddCardHint(cardEQ, "click and press key  |  Delete to clear", 210, cy + 2);
        cy += R;

        // EQ Path
        DarkTheme.AddCardLabel(cardEQ, "EQ Path:", L, cy);
        _txtEQPath = DarkTheme.AddCardTextBox(cardEQ, I, cy - 2, IW);
        var btnBrowse = DarkTheme.AddCardButton(cardEQ, "Browse...", BRW, cy - 3, 75);
        btnBrowse.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { Description = "Select EverQuest folder", InitialDirectory = _txtEQPath.Text };
            if (fbd.ShowDialog() == DialogResult.OK) _txtEQPath.Text = fbd.SelectedPath;
        };
        cy += R;

        // Exe / Args on same row
        DarkTheme.AddCardLabel(cardEQ, "Exe:", L, cy);
        _txtExeName = DarkTheme.AddCardTextBox(cardEQ, I, cy - 2, 100);
        DarkTheme.AddCardLabel(cardEQ, "Args:", 240, cy);
        _txtArgs = DarkTheme.AddCardTextBox(cardEQ, I2, cy - 2, 100);

        y += 138;

        // ─── Tray Click Actions card ─────────────────────────────
        var clickActions = new[] { "None", "FixWindows", "SwapWindows", "TogglePiP", "LaunchOne", "LaunchAll", "Settings", "ShowHelp" };
        const int cboW = 140;

        var cardTray = DarkTheme.MakeCard(page, "🖱", "Tray Click Actions", DarkTheme.CardBlue, 10, y, 480, 105);

        // ── Left Click section ──
        var lblLeft = DarkTheme.AddCardLabel(cardTray, "Left Click", 10, 30);
        lblLeft.Font = new Font("Segoe UI Semibold", 9f);
        lblLeft.ForeColor = DarkTheme.FgWhite;

        DarkTheme.AddCardLabel(cardTray, "Single", 20, 52);
        _cboSingleClick = DarkTheme.AddCardComboBox(cardTray, 85, 49, cboW, clickActions);

        DarkTheme.AddCardLabel(cardTray, "Double", 20, 78);
        _cboDoubleClick = DarkTheme.AddCardComboBox(cardTray, 85, 75, cboW, clickActions);

        // ── Middle Click section ──
        var lblMiddle = DarkTheme.AddCardLabel(cardTray, "Middle Click", 250, 30);
        lblMiddle.Font = new Font("Segoe UI Semibold", 9f);
        lblMiddle.ForeColor = DarkTheme.FgWhite;

        DarkTheme.AddCardLabel(cardTray, "Single", 260, 52);
        _cboMiddleClick = DarkTheme.AddCardComboBox(cardTray, 325, 49, cboW, clickActions);

        DarkTheme.AddCardLabel(cardTray, "Double", 260, 78);
        _cboMiddleDoubleClick = DarkTheme.AddCardComboBox(cardTray, 325, 75, cboW, clickActions);

        y += 113;

        // ─── Preferences card ────────────────────────────────────
        var cardPrefs = DarkTheme.MakeCard(page, "⚙", "Preferences", DarkTheme.CardGold, 10, y, 480, 170);
        cy = 32;

        // Row 1: EQ Client Settings button + Tooltip delay
        var btnEQSettings = DarkTheme.AddCardButton(cardPrefs, "\uD83D\uDCDD EQ Client Settings...", L, cy, 170);
        btnEQSettings.Click += (_, _) =>
        {
            using var form = new EQClientSettingsForm(_config);
            form.ShowDialog();
        };
        DarkTheme.AddCardLabel(cardPrefs, "Tooltip Delay:", 200, cy + 2);
        _nudTooltipDuration = DarkTheme.AddCardNumeric(cardPrefs, 300, cy, 65, 1000, 0, 10000);
        _nudTooltipDuration.Increment = 100;
        DarkTheme.AddCardHint(cardPrefs, "ms, 0=off", 370, cy + 4);
        cy += R + 4;

        // Row 2: Tray icon path + Browse
        DarkTheme.AddCardLabel(cardPrefs, "Tray Icon:", L, cy);
        _txtCustomIconPath = DarkTheme.AddCardTextBox(cardPrefs, 80, cy - 2, 180);
        var btnBrowseIcon = DarkTheme.AddCardButton(cardPrefs, "Browse...", 268, cy - 3, 65);
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
        DarkTheme.AddCardHint(cardPrefs, ".ico — blank = default", 340, cy + 2);
        cy += R + 4;

        // Row 3: Launch settings
        DarkTheme.AddCardLabel(cardPrefs, "Clients (Launch All):", L, cy);
        _nudNumClients = DarkTheme.AddCardNumeric(cardPrefs, 140, cy - 2, 55, 2, 1, 8);
        DarkTheme.AddCardLabel(cardPrefs, "Delay between:", 220, cy);
        _nudLaunchDelay = DarkTheme.AddCardNumeric(cardPrefs, 330, cy - 2, 70, 3000, 500, 30000);
        DarkTheme.AddCardHint(cardPrefs, "ms", 405, cy + 2);
        cy += R + 6;

        // Row 4: Quick-access buttons — Video Settings, Help, Process Manager
        var btnVideoSettings = DarkTheme.AddCardButton(cardPrefs, "📺 Video Settings...", 20, cy, 150);
        btnVideoSettings.Click += (_, _) =>
        {
            using var form = new VideoSettingsForm(_config);
            form.ShowDialog();
            // VideoSettings may have changed TopOffset, multi-monitor, monitors — sync back
            _nudTopOffset.Value = DarkTheme.ClampNud(_nudTopOffset, _config.Layout.TopOffset);
            _chkMultiMonEnabled.Checked = _config.Hotkeys.MultiMonitorEnabled;
            _cboTargetMonitor.SelectedIndex = Math.Clamp(_config.Layout.TargetMonitor, 0, _cboTargetMonitor.Items.Count - 1);
            var secIdx = _config.Layout.SecondaryMonitor < 0 ? 0 : _config.Layout.SecondaryMonitor + 1;
            _cboSecondaryMonitor.SelectedIndex = Math.Clamp(secIdx, 0, _cboSecondaryMonitor.Items.Count - 1);
        };
        var btnHelp = DarkTheme.AddCardButton(cardPrefs, "❓ Help", 185, cy, 100);
        btnHelp.Click += (_, _) => HelpForm.Show(_config);
        var btnProcessMgr = DarkTheme.AddCardButton(cardPrefs, "⚡ Process Manager...", 310, cy, 150);
        btnProcessMgr.Click += (_, _) => _openProcessManager?.Invoke();

        return page;
    }

    private void CheckDuplicateSwitchKeys()
    {
        var sk = _txtSwitchKey.Text.Trim();
        var gsk = _txtGlobalSwitchKey.Text.Trim();
        bool dup = sk.Length > 0 && gsk.Length > 0
            && string.Equals(sk, gsk, StringComparison.OrdinalIgnoreCase);

        _lblDuplicateKeyWarn.Text = dup
            ? "⚠ Same as Switch Key — global will override"
            : "Works from any app, cycles thru all";
        _lblDuplicateKeyWarn.ForeColor = dup ? DarkTheme.CardWarn : DarkTheme.FgDimGray;
    }

    private TextBox MakeHotkeyBox(Panel card, int x, int y, int width = 80)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y), Size = new Size(width, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9f),
            TextAlign = HorizontalAlignment.Center,
            ShortcutsEnabled = false
        };
        tb.KeyDown += HotkeyBoxKeyDown;
        card.Controls.Add(tb);
        DarkTheme.WrapWithBorder(tb);
        return tb;
    }

    private TabPage BuildHotkeysTab()
    {
        var page = DarkTheme.MakeTabPage("Hotkeys");
        int y = 8;
        const int L = 10, I = 150, I2 = 310, R = 28;

        // ─── Window Switching card ───────────────────────────────
        var cardSwitch = DarkTheme.MakeCard(page, "⚔", "Window Switching", DarkTheme.CardGreen, 10, y, 480, 100);
        int cy = 32;

        DarkTheme.AddCardLabel(cardSwitch, "Switch Key (EQ-only):", L, cy);
        _txtSwitchKey = MakeHotkeyBox(cardSwitch, I, cy - 2);
        _txtSwitchKey.TextChanged += (_, _) =>
        {
            if (_txtSwitchKeyGeneral != null && _txtSwitchKeyGeneral.Text != _txtSwitchKey.Text)
                _txtSwitchKeyGeneral.Text = _txtSwitchKey.Text;
        };
        DarkTheme.AddCardLabel(cardSwitch, "Mode:", 250, cy);
        _cboSwitchKeyMode = DarkTheme.AddCardComboBox(cardSwitch, I2, cy - 2, 130, new[] { "Swap Last Two", "Cycle All" });
        cy += R + 2;

        DarkTheme.AddCardLabel(cardSwitch, "Global Switch Key:", L, cy);
        _txtGlobalSwitchKey = MakeHotkeyBox(cardSwitch, I, cy - 2);
        _lblDuplicateKeyWarn = DarkTheme.AddCardHint(cardSwitch, "Works from any app, cycles thru all", 250, cy + 2);

        // Show warning when both switch keys match
        _txtSwitchKey.TextChanged += (_, _) => CheckDuplicateSwitchKeys();
        _txtGlobalSwitchKey.TextChanged += (_, _) => CheckDuplicateSwitchKeys();

        y += 108;

        // ─── Actions card ────────────────────────────────────────
        var cardActions = DarkTheme.MakeCard(page, "🏰", "Actions & Launcher", DarkTheme.CardGold, 10, y, 480, 145);
        cy = 32;
        const int col2 = 250, col2I = 370;

        DarkTheme.AddCardLabel(cardActions, "Fix Windows:", L, cy);
        _txtArrangeWindows = MakeHotkeyBox(cardActions, I, cy - 2);
        DarkTheme.AddCardLabel(cardActions, "Launch One:", col2, cy);
        _txtLaunchOne = MakeHotkeyBox(cardActions, col2I, cy - 2);
        cy += R + 2;

        DarkTheme.AddCardLabel(cardActions, "Multi-Mon Toggle:", L, cy);
        _txtToggleMultiMon = MakeHotkeyBox(cardActions, I, cy - 2);
        DarkTheme.AddCardLabel(cardActions, "Launch All:", col2, cy);
        _txtLaunchAll = MakeHotkeyBox(cardActions, col2I, cy - 2);
        cy += R + 2;

        _chkMultiMonEnabled = DarkTheme.AddCardCheckBox(cardActions, "Multi-monitor mode enabled", L, cy);

        y += 160;

        DarkTheme.AddHint(page, "Press key combo to capture. Leave blank to disable. Backspace/Delete to clear. Hit Apply to update changes.", 15, y);

        return page;
    }

    private TabPage BuildLayoutTab()
    {
        var page = DarkTheme.MakeTabPage("Layout");
        int y = 8;
        const int L = 10, I = 120, R = 30;

        // ─── Monitor card ────────────────────────────────────────
        var cardMon = DarkTheme.MakeCard(page, "🖥", "Monitor Selection", DarkTheme.CardBlue, 10, y, 480, 125);
        int cy = 32;

        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ToArray();
        var monitorItems = new string[screens.Length];
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var primary = s.Primary ? " (primary)" : "";
            monitorItems[i] = $"{i + 1}: {s.Bounds.Width}x{s.Bounds.Height}{primary}";
        }

        DarkTheme.AddCardLabel(cardMon, "Primary:", L, cy);
        _cboTargetMonitor = DarkTheme.AddCardComboBox(cardMon, 75, cy - 2, 185, monitorItems);
        DarkTheme.AddCardLabel(cardMon, "Secondary:", 270, cy);
        var secondaryItems = new string[screens.Length + 1];
        secondaryItems[0] = "Auto (first non-primary)";
        for (int i = 0; i < screens.Length; i++)
            secondaryItems[i + 1] = monitorItems[i];
        _cboSecondaryMonitor = DarkTheme.AddCardComboBox(cardMon, 340, cy - 2, 130, secondaryItems);
        cy += R;

        DarkTheme.AddCardLabel(cardMon, "Top Offset (px):", L, cy);
        _nudTopOffset = DarkTheme.AddCardNumeric(cardMon, I, cy - 2, 70, 0, -100, 200);
        var btnIdentify = DarkTheme.AddCardButton(cardMon, "🔍 Identify", 310, cy - 3, 90);
        btnIdentify.Click += (_, _) => ShowMonitorIdentifiers();
        cy += R;

        DarkTheme.AddCardHint(cardMon, "Primary = active client. Secondary = background client (multimonitor mode).", L, cy);

        y += 133;

        // ─── Window Style card ───────────────────────────────────
        var cardStyle = DarkTheme.MakeCard(page, "🪟", "Window Style", DarkTheme.CardPurple, 10, y, 480, 85);
        cy = 32;

        _chkRemoveTitleBars = DarkTheme.AddCardCheckBox(cardStyle, "Remove Title Bars on Arrange", L, cy);
        cy += 24;
        _chkBorderlessFullscreen = DarkTheme.AddCardCheckBox(cardStyle, "Borderless Fullscreen", L, cy);
        DarkTheme.AddCardHint(cardStyle, "Strips chrome, Y+1 offset, auto WindowedMode", 230, cy + 2);

        y += 93;

        // ─── Grid Layout card ────────────────────────────────────
        var cardGrid = DarkTheme.MakeCard(page, "📐", "Grid Layout", DarkTheme.CardGold, 10, y, 480, 95);
        cy = 32;

        DarkTheme.AddCardLabel(cardGrid, "Columns:", L, cy);
        _nudColumns = DarkTheme.AddCardNumeric(cardGrid, 75, cy - 2, 40, 1, 1, 2);
        DarkTheme.AddCardLabel(cardGrid, "Rows:", 130, cy);
        _nudRows = DarkTheme.AddCardNumeric(cardGrid, 170, cy - 2, 40, 1, 1, 2);
        cy += R;

        DarkTheme.AddCardHint(cardGrid, "Grid divides the primary monitor into columns × rows. Only works in single-screen mode.", L, cy);

        return page;
    }

    private void ShowMonitorIdentifiers()
    {
        // Dismiss any existing overlays before creating new ones (prevents stacking on rapid clicks)
        DismissMonitorOverlays();

        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ToArray();
        _monitorOverlays = new List<Form>();

        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var overlay = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                BackColor = DarkTheme.BgOverlay,
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
                Text = $"Monitor {i + 1}",
                Font = new Font("Segoe UI", 24, FontStyle.Bold),
                ForeColor = DarkTheme.CardGreen,
                BackColor = Color.Transparent,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            overlay.Controls.Add(lbl);
            overlay.Show();
            _monitorOverlays.Add(overlay);
        }

        _monitorOverlayTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _monitorOverlayTimer.Tick += (_, _) => DismissMonitorOverlays();
        _monitorOverlayTimer.Start();
    }

    private void DismissMonitorOverlays()
    {
        _monitorOverlayTimer?.Stop();
        _monitorOverlayTimer?.Dispose();
        _monitorOverlayTimer = null;

        if (_monitorOverlays != null)
        {
            foreach (var o in _monitorOverlays)
            {
                // Dispose fonts on child controls — Form.Dispose doesn't do this
                foreach (Control c in o.Controls)
                    c.Font?.Dispose();
                o.Close();
                o.Dispose();
            }
            _monitorOverlays = null;
        }
    }


    private TabPage BuildPathsTab()
    {
        var page = DarkTheme.MakeTabPage("Paths");
        int y = 8;
        const int L = 10, I = 120, BRW = 380, IW = 250, R = 32;

        // ─── External Tools card ─────────────────────────────────
        var cardPaths = DarkTheme.MakeCard(page, "📁", "External Tools", DarkTheme.CardGold, 10, y, 480, 160);
        int cy = 32;

        DarkTheme.AddCardLabel(cardPaths, "GINA Path:", L, cy);
        _txtGinaPath = DarkTheme.AddCardTextBox(cardPaths, I, cy - 2, IW);
        var btnBrowseGina = DarkTheme.AddCardButton(cardPaths, "Browse...", BRW, cy - 3, 75);
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
        cy += R;

        DarkTheme.AddCardLabel(cardPaths, "Notes File:", L, cy);
        _txtNotesPath = DarkTheme.AddCardTextBox(cardPaths, I, cy - 2, IW);
        var btnBrowseNotes = DarkTheme.AddCardButton(cardPaths, "Browse...", BRW, cy - 3, 75);
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
        DarkTheme.AddCardHint(cardPaths, "Leave blank to auto-create eqnotes.txt next to EQSwitch", L, cy + 22);
        cy += R + 10;

        DarkTheme.AddCardLabel(cardPaths, "Dalaya Patcher:", L, cy);
        _txtDalayaPatcherPath = DarkTheme.AddCardTextBox(cardPaths, I, cy - 2, IW);
        var btnBrowsePatcher = DarkTheme.AddCardButton(cardPaths, "Browse...", BRW, cy - 3, 75);
        btnBrowsePatcher.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select Dalaya patcher executable",
                Filter = "Executables|*.exe|All Files|*.*",
                InitialDirectory = Path.GetDirectoryName(_txtDalayaPatcherPath.Text) ?? ""
            };
            if (ofd.ShowDialog() == DialogResult.OK) _txtDalayaPatcherPath.Text = ofd.FileName;
        };
        cy += R - 4;

        DarkTheme.AddCardHint(cardPaths, "Patcher may be deleted by antivirus — re-download from SoD if missing.", L, cy);

        return page;
    }

    private TabPage BuildPipTab()
    {
        var page = DarkTheme.MakeTabPage("PiP");
        int y = 8;
        const int L = 10, I = 120, R = 28;

        // ─── PiP Overlay card ────────────────────────────────────
        var cardPip = DarkTheme.MakeCard(page, "👁", "PiP Overlay", DarkTheme.CardCyan, 10, y, 480, 120);
        int cy = 32;

        _chkPipEnabled = DarkTheme.AddCardCheckBox(cardPip, "Enable PiP Overlay", L, cy);
        DarkTheme.AddCardHint(cardPip, "DWM thumbnail — zero CPU, GPU composited", 200, cy + 2);
        cy += R;

        DarkTheme.AddCardLabel(cardPip, "Size Preset:", L, cy);
        _cboPipSize = DarkTheme.AddCardComboBox(cardPip, I, cy - 2, 170, new[] {
            "Small (200x150)", "Medium (320x240)", "Large (400x300)",
            "XL (480x360)", "XXL (640x480)", "Custom"
        });
        _cboPipSize.SelectedIndexChanged += (_, _) =>
        {
            bool isCustom = _cboPipSize.SelectedItem?.ToString()?.StartsWith("Custom") == true;
            _nudPipWidth.Enabled = isCustom;
            _nudPipHeight.Enabled = isCustom;
        };
        DarkTheme.AddCardLabel(cardPip, "Max", 310, cy - 12);
        DarkTheme.AddCardLabel(cardPip, "Windows:", 298, cy + 2);
        _nudPipMaxWindows = DarkTheme.AddCardNumeric(cardPip, 355, cy - 2, 40, 3, 1, 3);
        cy += R;

        DarkTheme.AddCardLabel(cardPip, "Custom W:", L, cy);
        _nudPipWidth = DarkTheme.AddCardNumeric(cardPip, I, cy - 2, 70, 320, 100, 960);
        _nudPipWidth.Enabled = false;
        DarkTheme.AddCardLabel(cardPip, "Custom H:", 210, cy);
        _nudPipHeight = DarkTheme.AddCardNumeric(cardPip, 290, cy - 2, 70, 240, 100, 720);
        _nudPipHeight.Enabled = false;

        y += 128;

        // ─── Appearance card ─────────────────────────────────────
        var cardLook = DarkTheme.MakeCard(page, "🎨", "Appearance", DarkTheme.CardPurple, 10, y, 480, 90);
        cy = 32;

        DarkTheme.AddCardLabel(cardLook, "Opacity:", L, cy);
        _nudPipOpacity = DarkTheme.AddCardNumeric(cardLook, I, cy - 2, 60, 245, 0, 255);
        DarkTheme.AddCardHint(cardLook, "0-255", I + 65, cy + 2);

        _chkPipBorder = DarkTheme.AddCardCheckBox(cardLook, "Show Border", 230, cy);
        _chkPipBorder.CheckedChanged += (_, _) =>
        {
            _cboPipBorderColor.Enabled = _chkPipBorder.Checked;
        };
        cy += R;

        DarkTheme.AddCardLabel(cardLook, "Border Color:", L, cy);
        _cboPipBorderColor = DarkTheme.AddCardComboBox(cardLook, I, cy - 2, 100, new[] { "Green", "Blue", "Red", "Black" });

        return page;
    }

    // ─── Shared Hotkey Handler ──────────────────────────────────

    /// <summary>
    /// Shared KeyDown handler for all hotkey TextBoxes. Suppresses beep,
    /// captures modifier+key combos, formats as "Ctrl+Alt+K" strings.
    /// </summary>
    private void UpdateSwitchKeyColor()
    {
        bool hasKey = !string.IsNullOrWhiteSpace(_txtSwitchKeyGeneral.Text);
        _txtSwitchKeyGeneral.ForeColor = hasKey
            ? Color.FromArgb(220, 190, 100)   // gold — key is set
            : Color.FromArgb(220, 100, 100);  // red — not set
        _lblSwitchKey.ForeColor = hasKey
            ? Color.FromArgb(220, 190, 100)
            : Color.FromArgb(220, 100, 100);
        _lblSwitchKey.Text = hasKey ? "EQ Switch Key:" : "EQ Switch Key: (not set!)";
    }

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
        _nudTooltipDuration.Value = Math.Clamp(_config.TooltipDurationMs, (int)_nudTooltipDuration.Minimum, (int)_nudTooltipDuration.Maximum);
        _txtCustomIconPath.Text = _config.CustomIconPath;

        // Tray Click Actions
        _cboSingleClick.SelectedItem = _config.TrayClick.SingleClick;
        _cboDoubleClick.SelectedItem = _config.TrayClick.DoubleClick;
        _cboMiddleClick.SelectedItem = _config.TrayClick.MiddleClick;
        _cboMiddleDoubleClick.SelectedItem = _config.TrayClick.MiddleDoubleClick;

        // Hotkeys
        _txtSwitchKeyGeneral.Text = _config.Hotkeys.SwitchKey;
        _txtSwitchKey.Text = _config.Hotkeys.SwitchKey;
        _cboSwitchKeyMode.SelectedItem = _config.Hotkeys.SwitchKeyMode == "cycleAll" ? "Cycle All" : "Swap Last Two";
        _txtGlobalSwitchKey.Text = _config.Hotkeys.GlobalSwitchKey;
        _txtArrangeWindows.Text = _config.Hotkeys.ArrangeWindows;
        _txtToggleMultiMon.Text = _config.Hotkeys.ToggleMultiMonitor;
        _txtLaunchOne.Text = _config.Hotkeys.LaunchOne;
        _txtLaunchAll.Text = _config.Hotkeys.LaunchAll;
        _chkMultiMonEnabled.Checked = _config.Hotkeys.MultiMonitorEnabled;

        // Layout
        _nudColumns.Value = DarkTheme.ClampNud(_nudColumns, _config.Layout.Columns);
        _nudRows.Value = DarkTheme.ClampNud(_nudRows, _config.Layout.Rows);
        var targetIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, _cboTargetMonitor.Items.Count - 1);
        _cboTargetMonitor.SelectedIndex = targetIdx;
        // SecondaryMonitor: -1 = Auto (index 0), 0+ = monitor index (offset by 1 in dropdown)
        var secIdx = _config.Layout.SecondaryMonitor < 0 ? 0 : _config.Layout.SecondaryMonitor + 1;
        _cboSecondaryMonitor.SelectedIndex = Math.Clamp(secIdx, 0, _cboSecondaryMonitor.Items.Count - 1);
        _nudTopOffset.Value = DarkTheme.ClampNud(_nudTopOffset, _config.Layout.TopOffset);
        _chkRemoveTitleBars.Checked = _config.Layout.RemoveTitleBars;
        _chkBorderlessFullscreen.Checked = _config.Layout.BorderlessFullscreen;

        // Performance

        // Launch
        _nudNumClients.Value = DarkTheme.ClampNud(_nudNumClients, _config.Launch.NumClients);
        _nudLaunchDelay.Value = DarkTheme.ClampNud(_nudLaunchDelay, _config.Launch.LaunchDelayMs);

        // Paths
        _txtGinaPath.Text = _config.GinaPath;
        _txtNotesPath.Text = _config.NotesPath;
        _txtDalayaPatcherPath.Text = _config.DalayaPatcherPath;

        // PiP
        _chkPipEnabled.Checked = _config.Pip.Enabled;
        // Select combo item that starts with the saved preset name
        SelectPipPreset(_config.Pip.SizePreset);
        _nudPipWidth.Value = Math.Clamp(_config.Pip.CustomWidth, 100, 960);
        _nudPipHeight.Value = Math.Clamp(_config.Pip.CustomHeight, 100, 720);
        _nudPipOpacity.Value = _config.Pip.Opacity;
        _chkPipBorder.Checked = _config.Pip.ShowBorder;
        _cboPipBorderColor.SelectedItem = _config.Pip.BorderColor;
        _nudPipMaxWindows.Value = Math.Clamp(_config.Pip.MaxWindows, 1, 3);
        _nudPipWidth.Enabled = _config.Pip.SizePreset == "Custom";
        _nudPipHeight.Enabled = _config.Pip.SizePreset == "Custom";
        _cboPipBorderColor.Enabled = _config.Pip.ShowBorder;
    }

    private void ApplySettings()
    {
        // Build a new config from form values
        var newConfig = new AppConfig
        {
            IsFirstRun = false,
            EQPath = _txtEQPath.Text.Trim(),
            EQProcessName = _config.EQProcessName,
            TooltipDurationMs = (int)_nudTooltipDuration.Value,
            CustomIconPath = _txtCustomIconPath.Text.Trim(),
            Layout = new WindowLayout
            {
                // Preserve multimonitor mode only if the checkbox is on; reset to single if disabled
                Mode = _chkMultiMonEnabled.Checked ? _config.Layout.Mode : "single",
                Columns = (int)_nudColumns.Value,
                Rows = (int)_nudRows.Value,
                TargetMonitor = _cboTargetMonitor.SelectedIndex >= 0 ? _cboTargetMonitor.SelectedIndex : 0,
                SecondaryMonitor = _cboSecondaryMonitor.SelectedIndex <= 0 ? -1 : _cboSecondaryMonitor.SelectedIndex - 1,
                TopOffset = (int)_nudTopOffset.Value,
                RemoveTitleBars = _chkRemoveTitleBars.Checked,
                BorderlessFullscreen = _chkBorderlessFullscreen.Checked
            },
            Affinity = _config.Affinity, // managed by Process Manager
            Hotkeys = new HotkeyConfig
            {
                // General tab switch key takes priority if user edited it there
                SwitchKey = _txtSwitchKeyGeneral.Text.Trim(),
                GlobalSwitchKey = _txtGlobalSwitchKey.Text.Trim(),
                ArrangeWindows = _txtArrangeWindows.Text.Trim(),
                ToggleMultiMonitor = _txtToggleMultiMon.Text.Trim(),
                LaunchOne = _txtLaunchOne.Text.Trim(),
                LaunchAll = _txtLaunchAll.Text.Trim(),
                MultiMonitorEnabled = _chkMultiMonEnabled.Checked,
                DirectSwitchKeys = _config.Hotkeys.DirectSwitchKeys,
                SwitchKeyMode = _cboSwitchKeyMode.SelectedItem?.ToString() == "Cycle All" ? "cycleAll" : "swapLast"
            },
            Launch = new LaunchConfig
            {
                ExeName = _txtExeName.Text.Trim(),
                Arguments = _txtArgs.Text.Trim(),
                NumClients = (int)_nudNumClients.Value,
                LaunchDelayMs = (int)_nudLaunchDelay.Value
            },
            Pip = new PipConfig
            {
                Enabled = _chkPipEnabled.Checked,
                SizePreset = ExtractPipPresetName(_cboPipSize.SelectedItem?.ToString() ?? "Medium"),
                CustomWidth = (int)_nudPipWidth.Value,
                CustomHeight = (int)_nudPipHeight.Value,
                Opacity = (byte)_nudPipOpacity.Value,
                ShowBorder = _chkPipBorder.Checked,
                BorderColor = _cboPipBorderColor.SelectedItem?.ToString() ?? "Green",
                MaxWindows = (int)_nudPipMaxWindows.Value,
                SavedPositions = _config.Pip.SavedPositions // preserve existing positions
            },
            TrayClick = new TrayClickConfig
            {
                SingleClick = _cboSingleClick.SelectedItem?.ToString() ?? "LaunchOne",
                DoubleClick = _cboDoubleClick.SelectedItem?.ToString() ?? "None",
                MiddleClick = _cboMiddleClick.SelectedItem?.ToString() ?? "TogglePiP",
                MiddleDoubleClick = _cboMiddleDoubleClick.SelectedItem?.ToString() ?? "Settings"
            },
            GinaPath = _txtGinaPath.Text.Trim(),
            NotesPath = _txtNotesPath.Text.Trim(),
            DalayaPatcherPath = _txtDalayaPatcherPath.Text.Trim(),
            Characters = _config.Characters
        };

        _onApply(newConfig);
        Debug.WriteLine("Settings applied");
    }



    /// <summary>Strip " (WxH)" suffix from combo item to get bare preset name for config.</summary>
    private static string ExtractPipPresetName(string comboText)
    {
        int paren = comboText.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? comboText[..paren] : comboText;
    }

    /// <summary>Select the combo item that starts with the given preset name.</summary>
    private void SelectPipPreset(string presetName)
    {
        for (int i = 0; i < _cboPipSize.Items.Count; i++)
        {
            if (_cboPipSize.Items[i]?.ToString()?.StartsWith(presetName, StringComparison.OrdinalIgnoreCase) == true)
            {
                _cboPipSize.SelectedIndex = i;
                return;
            }
        }
        _cboPipSize.SelectedIndex = 1; // fallback to Medium
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DismissMonitorOverlays();
            // Dispose inline Font objects on hotkey TextBoxes and other controls
            // that were created with new Font() — base.Dispose doesn't clean these up
            DisposeControlFonts(_txtSwitchKeyGeneral, _txtSwitchKey, _txtGlobalSwitchKey,
                _txtArrangeWindows, _txtToggleMultiMon, _txtLaunchOne, _txtLaunchAll);
        }
        base.Dispose(disposing);
    }

    private static void DisposeControlFonts(params Control?[] controls)
    {
        foreach (var c in controls)
            c?.Font?.Dispose();
    }
}
