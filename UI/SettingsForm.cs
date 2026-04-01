using System.Diagnostics;
using System.Text.Json;
using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

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

    /// <summary>When true, TrayManager should reopen Settings after this form closes (used by Reset).</summary>
    public bool ReopenAfterClose => _reopenAfterClose;
    private bool _reopenAfterClose;

    // Track monitor identifier overlays to prevent stacking on rapid clicks
    private List<Form>? _monitorOverlays;
    private System.Windows.Forms.Timer? _monitorOverlayTimer;

    // Fonts created inline on controls — WinForms won't dispose these
    private readonly List<Font> _inlineFonts = new();

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
    private ComboBox _cboTargetMonitor = null!;
    private ComboBox _cboSecondaryMonitor = null!;
    private CheckBox _chkSlimTitlebar = null!;
    private NumericUpDown _nudTitlebarOffset = null!;
    private NumericUpDown _nudBottomOffset = null!;
    private CheckBox _chkUseHook = null!;
    private CheckBox _chkMaximizeWindow = null!;
    private TextBox _txtWindowTitleTemplate = null!;


    // ─── Launch tab controls
    private NumericUpDown _nudNumClients = null!;
    private NumericUpDown _nudLaunchDelay = null!;

    // ─── Paths tab controls
    private TextBox _txtGinaPath = null!;
    private TextBox _txtNotesPath = null!;
    private TextBox _txtDalayaPatcherPath = null!;
    private CheckBox _chkRunAtStartup = null!;

    // ─── Accounts tab controls
    private List<LoginAccount> _pendingAccounts = new();
    private DataGridView _dgvAccounts = null!;

    // ─── PiP tab controls
    private CheckBox _chkPipEnabled = null!;
    private ComboBox _cboPipSize = null!;
    private NumericUpDown _nudPipWidth = null!;
    private NumericUpDown _nudPipHeight = null!;
    private NumericUpDown _nudPipOpacity = null!;
    private CheckBox _chkPipBorder = null!;
    private ComboBox _cboPipBorderColor = null!;
    private NumericUpDown _nudPipMaxWindows = null!;
    private ComboBox _cboPipOrientation = null!;




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

        _pendingAccounts = _config.Accounts.Select(a => new LoginAccount
        {
            Name = a.Name, Username = a.Username, EncryptedPassword = a.EncryptedPassword,
            Server = a.Server, CharacterName = a.CharacterName, CharacterSlot = a.CharacterSlot,
            UseLoginFlag = a.UseLoginFlag
        }).ToList();

        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildHotkeysTab());
        tabs.TabPages.Add(BuildLayoutTab());
        tabs.TabPages.Add(BuildPipTab());
        tabs.TabPages.Add(BuildPathsTab());
        tabs.TabPages.Add(BuildAccountsTab());

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
                // Reopen Settings with fresh config so user sees the defaults
                _reopenAfterClose = true;
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
            Font = TrackFont(new Font("Segoe UI", 8f))
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
        lblLeft.Font = TrackFont(new Font("Segoe UI Semibold", 9f));
        lblLeft.ForeColor = DarkTheme.FgWhite;

        DarkTheme.AddCardLabel(cardTray, "Single", 20, 52);
        _cboSingleClick = DarkTheme.AddCardComboBox(cardTray, 85, 49, cboW, clickActions);

        DarkTheme.AddCardLabel(cardTray, "Double", 20, 78);
        _cboDoubleClick = DarkTheme.AddCardComboBox(cardTray, 85, 75, cboW, clickActions);

        // ── Middle Click section ──
        var lblMiddle = DarkTheme.AddCardLabel(cardTray, "Middle Click", 250, 30);
        lblMiddle.Font = TrackFont(new Font("Segoe UI Semibold", 9f));
        lblMiddle.ForeColor = DarkTheme.FgWhite;

        DarkTheme.AddCardLabel(cardTray, "Single", 260, 52);
        _cboMiddleClick = DarkTheme.AddCardComboBox(cardTray, 325, 49, cboW, clickActions);

        DarkTheme.AddCardLabel(cardTray, "Triple", 260, 78);
        _cboMiddleDoubleClick = DarkTheme.AddCardComboBox(cardTray, 325, 75, cboW, clickActions);

        y += 113;

        // ─── Preferences card ────────────────────────────────────
        var cardPrefs = DarkTheme.MakeCard(page, "⚙", "Preferences", DarkTheme.CardGold, 10, y, 480, 130);
        cy = 32;

        // Row 1: EQ Client Settings + Video Settings
        var btnEQSettings = DarkTheme.AddCardButton(cardPrefs, "\uD83D\uDCDD EQ Client Settings...", L, cy, 170);
        btnEQSettings.Click += (_, _) =>
        {
            using var form = new EQClientSettingsForm(_config);
            form.ShowDialog();
        };
        var btnVideoSettings = DarkTheme.AddCardButton(cardPrefs, "📺 Video Settings...", 210, cy, 150);
        btnVideoSettings.Click += (_, _) =>
        {
            using var form = new VideoSettingsForm(_config);
            form.ShowDialog();
            // VideoSettings may have changed TopOffset, multi-monitor, monitors — sync back
            _chkMultiMonEnabled.Checked = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
            _cboTargetMonitor.SelectedIndex = Math.Clamp(_config.Layout.TargetMonitor, 0, _cboTargetMonitor.Items.Count - 1);
            var secIdx2 = _config.Layout.SecondaryMonitor < 0 ? 0 : _config.Layout.SecondaryMonitor + 1;
            _cboSecondaryMonitor.SelectedIndex = Math.Clamp(secIdx2, 0, _cboSecondaryMonitor.Items.Count - 1);
        };
        cy += R + 6;

        // Row 2: Process Manager + Help
        var btnProcessMgr = DarkTheme.AddCardButton(cardPrefs, "⚡ Process Manager...", L, cy, 170);
        btnProcessMgr.Click += (_, _) => _openProcessManager?.Invoke();
        var btnHelp = DarkTheme.AddCardButton(cardPrefs, "❓ Help", 210, cy, 150);
        btnHelp.Click += (_, _) => HelpForm.Show(_config);

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
        var cardSwitch = DarkTheme.MakeCard(page, "⚔", "Window Switching", DarkTheme.CardGreen, 10, y, 480, 130);
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
        cy += R + 2;

        DarkTheme.AddCardLabel(cardSwitch, "Clients (Launch All):", L, cy);
        _nudNumClients = DarkTheme.AddCardNumeric(cardSwitch, I, cy - 2, 55, 2, 1, 8);
        DarkTheme.AddCardLabel(cardSwitch, "Delay between:", 250, cy);
        _nudLaunchDelay = DarkTheme.AddCardNumeric(cardSwitch, I2 + 50, cy - 2, 55, 3, 1, 30);
        DarkTheme.AddCardHint(cardSwitch, "sec", I2 + 110, cy + 2);

        y += 138;

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
        DarkTheme.AddCardLabel(cardActions, "Tooltip Delay:", col2, cy + 2);
        _nudTooltipDuration = DarkTheme.AddCardNumeric(cardActions, col2I, cy, 55, 1000, 0, 10000);
        _nudTooltipDuration.Increment = 100;
        DarkTheme.AddCardHint(cardActions, "ms", col2I + 60, cy + 4);

        y += 160;

        DarkTheme.AddHint(page, "Press key combo to capture. Leave blank to disable. Backspace/Delete to clear. Hit Apply to update changes.", 15, y);

        return page;
    }

    private TabPage BuildLayoutTab()
    {
        var page = DarkTheme.MakeTabPage("Layout");
        int y = 8;
        const int L = 10, R = 30;

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

        var btnIdentify = DarkTheme.AddCardButton(cardMon, "🔍 Identify", L, cy - 3, 90);
        btnIdentify.Click += (_, _) => ShowMonitorIdentifiers();
        DarkTheme.AddCardHint(cardMon, "Primary = active client. Secondary = background client (multimonitor mode).", 110, cy);

        y += 133;

        // ─── Window Style card ───────────────────────────────────
        var cardStyle = DarkTheme.MakeCard(page, "🪟", "Window Style", DarkTheme.CardPurple, 10, y, 480, 193);
        cy = 32;

        const int hintX = 260;

        _chkSlimTitlebar = DarkTheme.AddCardCheckBox(cardStyle, "Fullscreen Window (WinEQ2 mode)", L, cy);
        DarkTheme.AddCardHint(cardStyle, "Auto-sets resolution + hides titlebar", hintX, cy + 2);
        cy += 24;

        DarkTheme.AddCardLabel(cardStyle, "Titlebar hidden (px):", L, cy);
        _nudTitlebarOffset = DarkTheme.AddCardNumeric(cardStyle, 140, cy - 2, 55, 22, 0, 40);
        DarkTheme.AddCardHint(cardStyle, "22 = thin strip, 30 = fully hidden", hintX, cy);
        cy += 26;

        DarkTheme.AddCardLabel(cardStyle, "Bottom margin (px):", L, cy);
        _nudBottomOffset = DarkTheme.AddCardNumeric(cardStyle, 140, cy - 2, 55, 22, 0, 100);
        DarkTheme.AddCardHint(cardStyle, "Game render height reduction", hintX, cy);
        cy += 22;

        DarkTheme.AddCardHint(cardStyle, "WinEQ2 mode: hidden 13, margin 21 — keep margin > hidden", L, cy);
        cy += 22;

        _chkUseHook = DarkTheme.AddCardCheckBox(cardStyle, "DLL Hook (zero flicker)", L, cy);
        DarkTheme.AddCardHint(cardStyle, "Hooks SetWindowPos inside EQ", hintX, cy + 2);
        cy += 24;

        _chkMaximizeWindow = DarkTheme.AddCardCheckBox(cardStyle, "Maximize on Launch", L, cy);
        DarkTheme.AddCardHint(cardStyle, "Sets Maximized=1 in eqclient.ini", hintX, cy + 2);

        _chkSlimTitlebar.CheckedChanged += (_, _) =>
        {
            bool slim = _chkSlimTitlebar.Checked;
            _nudTitlebarOffset.Enabled = slim;
            _nudBottomOffset.Enabled = slim;
            _chkUseHook.Enabled = slim;
            if (!slim) _chkUseHook.Checked = false;
            // Slim titlebar and Maximize are incompatible — maximized windows
            // are constrained to the work area, defeating the WinEQ2 trick
            if (slim)
            {
                _chkMaximizeWindow.Checked = false;
                _chkMaximizeWindow.Enabled = false;
            }
            else
            {
                _chkMaximizeWindow.Enabled = true;
            }
        };

        y += 201;

        // ─── Window Title card ───────────────────────────────────
        var cardTitle = DarkTheme.MakeCard(page, "📝", "Window Title", DarkTheme.CardGreen, 10, y, 480, 65);
        cy = 32;

        DarkTheme.AddCardLabel(cardTitle, "Template:", L, cy);
        _txtWindowTitleTemplate = DarkTheme.AddCardTextBox(cardTitle, 75, cy - 2, 280);
        DarkTheme.AddCardHint(cardTitle, "{SLOT} {PID}", 365, cy);

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

        y += 168;

        // ─── Tray Icon card ─────────────────────────────────────
        var cardIcon = DarkTheme.MakeCard(page, "🎨", "Tray Icon", DarkTheme.CardPurple, 10, y, 480, 65);
        cy = 32;

        DarkTheme.AddCardLabel(cardIcon, "Custom Icon:", L, cy);
        _txtCustomIconPath = DarkTheme.AddCardTextBox(cardIcon, I, cy - 2, IW);
        var btnBrowseIcon = DarkTheme.AddCardButton(cardIcon, "Browse...", BRW, cy - 3, 75);
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

        y += 73;

        // ─── Startup card ───────────────────────────────────────
        var cardStartup = DarkTheme.MakeCard(page, "🚀", "Startup", DarkTheme.CardGreen, 10, y, 480, 65);
        cy = 32;

        var btnShortcut = DarkTheme.AddCardButton(cardStartup, "Create Desktop Shortcut", L, cy, 180);
        btnShortcut.Click += (_, _) =>
        {
            var originalText = btnShortcut.Text;
            StartupManager.CreateDesktopShortcut(_ =>
            {
                btnShortcut.Text = "Created!";
                var reset = new System.Windows.Forms.Timer { Interval = 2000 };
                reset.Tick += (__, ___) => { reset.Stop(); reset.Dispose(); btnShortcut.Text = originalText; };
                reset.Start();
            });
        };

        _chkRunAtStartup = DarkTheme.AddCardCheckBox(cardStartup, "Run at Startup", 250, cy);

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
            "XL (480x360)", "XXL (640x480)", "XXXL (960x720)", "XXXXL (1600x900)", "Custom"
        });
        _cboPipSize.SelectedIndexChanged += (_, _) =>
        {
            var selected = _cboPipSize.SelectedItem?.ToString() ?? "";
            bool isCustom = selected.StartsWith("Custom");
            _nudPipWidth.Enabled = isCustom;
            _nudPipHeight.Enabled = isCustom;
            // XXXXL is nearly full-screen — enforce max 1 PiP window
            if (selected.StartsWith("XXXXL"))
            {
                _nudPipMaxWindows.Value = 1;
                _nudPipMaxWindows.Maximum = 1;
            }
            else
            {
                _nudPipMaxWindows.Maximum = 3;
            }
        };
        DarkTheme.AddCardLabel(cardPip, "Max", 310, cy - 12);
        DarkTheme.AddCardLabel(cardPip, "Windows:", 298, cy + 2);
        _nudPipMaxWindows = DarkTheme.AddCardNumeric(cardPip, 365, cy - 2, 40, 3, 1, 3);
        _nudPipMaxWindows.TextAlign = HorizontalAlignment.Center;
        cy += R;

        DarkTheme.AddCardLabel(cardPip, "Custom W:", L, cy);
        _nudPipWidth = DarkTheme.AddCardNumeric(cardPip, I, cy - 2, 50, 320, 100, 1920);
        _nudPipWidth.Enabled = false;
        DarkTheme.AddCardLabel(cardPip, "Custom H:", 190, cy);
        _nudPipHeight = DarkTheme.AddCardNumeric(cardPip, 260, cy - 2, 50, 240, 75, 1080);
        _nudPipHeight.Enabled = false;
        DarkTheme.AddCardLabel(cardPip, "Layout:", 330, cy);
        _cboPipOrientation = DarkTheme.AddCardComboBox(cardPip, 385, cy - 2, 85, new[] { "Vertical", "Horizontal" });

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
        _chkMultiMonEnabled.Checked = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);

        // Layout
        var targetIdx = Math.Clamp(_config.Layout.TargetMonitor, 0, _cboTargetMonitor.Items.Count - 1);
        _cboTargetMonitor.SelectedIndex = targetIdx;
        // SecondaryMonitor: -1 = Auto (index 0), 0+ = monitor index (offset by 1 in dropdown)
        var secIdx = _config.Layout.SecondaryMonitor < 0 ? 0 : _config.Layout.SecondaryMonitor + 1;
        _cboSecondaryMonitor.SelectedIndex = Math.Clamp(secIdx, 0, _cboSecondaryMonitor.Items.Count - 1);
        _chkSlimTitlebar.Checked = _config.Layout.SlimTitlebar;
        _nudTitlebarOffset.Value = DarkTheme.ClampNud(_nudTitlebarOffset, _config.Layout.TitlebarOffset);
        _nudBottomOffset.Value = DarkTheme.ClampNud(_nudBottomOffset, _config.Layout.BottomOffset);
        _chkUseHook.Checked = _config.Layout.UseHook;
        _chkUseHook.Enabled = _config.Layout.SlimTitlebar;
        _chkMaximizeWindow.Checked = _config.EQClientIni.MaximizeWindow;
        _txtWindowTitleTemplate.Text = _config.Layout.WindowTitleTemplate;
        _nudTitlebarOffset.Enabled = _config.Layout.SlimTitlebar;
        _nudBottomOffset.Enabled = _config.Layout.SlimTitlebar;
        _chkMaximizeWindow.Enabled = !_config.Layout.SlimTitlebar;

        // Performance

        // Launch
        _nudNumClients.Value = DarkTheme.ClampNud(_nudNumClients, _config.Launch.NumClients);
        _nudLaunchDelay.Value = DarkTheme.ClampNud(_nudLaunchDelay, _config.Launch.LaunchDelayMs / 1000);

        // Paths
        _txtGinaPath.Text = _config.GinaPath;
        _txtNotesPath.Text = _config.NotesPath;
        _txtDalayaPatcherPath.Text = _config.DalayaPatcherPath;
        _chkRunAtStartup.Checked = _config.RunAtStartup;

        // PiP
        _chkPipEnabled.Checked = _config.Pip.Enabled;
        // Select combo item that starts with the saved preset name
        SelectPipPreset(_config.Pip.SizePreset);
        _nudPipWidth.Value = Math.Clamp(_config.Pip.CustomWidth, (int)_nudPipWidth.Minimum, (int)_nudPipWidth.Maximum);
        _nudPipHeight.Value = Math.Clamp(_config.Pip.CustomHeight, (int)_nudPipHeight.Minimum, (int)_nudPipHeight.Maximum);
        _nudPipOpacity.Value = _config.Pip.Opacity;
        _chkPipBorder.Checked = _config.Pip.ShowBorder;
        _cboPipBorderColor.SelectedItem = _config.Pip.BorderColor;
        _nudPipMaxWindows.Value = Math.Clamp(_config.Pip.MaxWindows, 1, 3);
        _cboPipOrientation.SelectedItem = _config.Pip.IsHorizontal ? "Horizontal" : "Vertical";
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
                Mode = _chkMultiMonEnabled.Checked ? "multimonitor" : "single",
                TargetMonitor = _cboTargetMonitor.SelectedIndex >= 0 ? _cboTargetMonitor.SelectedIndex : 0,
                SecondaryMonitor = _cboSecondaryMonitor.SelectedIndex <= 0 ? -1 : _cboSecondaryMonitor.SelectedIndex - 1,
                TopOffset = _config.Layout.TopOffset,
                SlimTitlebar = _chkSlimTitlebar.Checked,
                TitlebarOffset = (int)_nudTitlebarOffset.Value,
                BottomOffset = (int)_nudBottomOffset.Value,
                UseHook = _chkUseHook.Checked,
                WindowTitleTemplate = _txtWindowTitleTemplate.Text.Trim()
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
                // Once enabled, the hotkey is unlocked permanently
                MultiMonitorEnabled = _chkMultiMonEnabled.Checked || _config.Hotkeys.MultiMonitorEnabled,
                DirectSwitchKeys = _config.Hotkeys.DirectSwitchKeys,
                SwitchKeyMode = _cboSwitchKeyMode.SelectedItem?.ToString() == "Cycle All" ? "cycleAll" : "swapLast"
            },
            Launch = new LaunchConfig
            {
                ExeName = _txtExeName.Text.Trim(),
                Arguments = _txtArgs.Text.Trim(),
                NumClients = (int)_nudNumClients.Value,
                LaunchDelayMs = (int)_nudLaunchDelay.Value * 1000
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
                Orientation = _cboPipOrientation.SelectedItem?.ToString() ?? "Vertical",
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
            RunAtStartup = _chkRunAtStartup.Checked,
            Characters = _config.Characters,
            Accounts = _pendingAccounts
        };

        // Apply startup registry change
        if (newConfig.RunAtStartup != _config.RunAtStartup)
            StartupManager.SetRunAtStartup(newConfig.RunAtStartup);

        // MaximizeWindow lives in EQClientIni, update in-place
        newConfig.EQClientIni = _config.EQClientIni;
        newConfig.EQClientIni.MaximizeWindow = _chkMaximizeWindow.Checked;

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

    // ─── Accounts Tab ──────────────────────────────────────────────

    private TabPage BuildAccountsTab()
    {
        var page = DarkTheme.MakeTabPage("Accounts");
        int y = 8;

        var card = DarkTheme.MakeCard(page, "\uD83D\uDD11", "Login Accounts", DarkTheme.CardGold, 10, y, 480, 340);

        _dgvAccounts = new DataGridView
        {
            Location = new Point(10, 32),
            Size = new Size(458, 200),
            BackgroundColor = DarkTheme.BgDark,
            ForeColor = DarkTheme.FgWhite,
            GridColor = DarkTheme.Border,
            BorderStyle = BorderStyle.None,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = DarkTheme.BgMedium,
                ForeColor = DarkTheme.FgWhite,
                SelectionBackColor = DarkTheme.BgMedium,
                Font = TrackFont(new Font("Segoe UI", 9))
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = DarkTheme.BgDark,
                ForeColor = DarkTheme.FgWhite,
                SelectionBackColor = DarkTheme.ActiveRowBg,
                SelectionForeColor = DarkTheme.FgWhite
            },
            EnableHeadersVisualStyles = false
        };

        _dgvAccounts.Columns.Add("Name", "Name");
        _dgvAccounts.Columns.Add("Username", "Username");
        _dgvAccounts.Columns.Add("Character", "Character");
        _dgvAccounts.Columns.Add("Server", "Server");
        _dgvAccounts.Columns.Add("Slot", "Slot");
        _dgvAccounts.Columns["Slot"]!.Width = 40;
        _dgvAccounts.Columns["Slot"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;

        RefreshAccountsGrid();
        card.Controls.Add(_dgvAccounts);

        int btnY = 240;
        var btnAdd = DarkTheme.AddCardButton(card, "Add", 10, btnY, 70);
        btnAdd.Click += (_, _) => ShowAccountDialog(null);

        var btnEdit = DarkTheme.AddCardButton(card, "Edit", 85, btnY, 70);
        btnEdit.Click += (_, _) =>
        {
            if (_dgvAccounts.SelectedRows.Count > 0)
                ShowAccountDialog(_dgvAccounts.SelectedRows[0].Index);
        };

        var btnRemove = DarkTheme.AddCardButton(card, "Remove", 160, btnY, 70);
        btnRemove.Click += (_, _) =>
        {
            if (_dgvAccounts.SelectedRows.Count > 0)
            {
                int idx = _dgvAccounts.SelectedRows[0].Index;
                _pendingAccounts.RemoveAt(idx);
                RefreshAccountsGrid();
            }
        };

        var btnMoveUp = DarkTheme.AddCardButton(card, "\u25B2", 250, btnY, 35);
        btnMoveUp.Click += (_, _) =>
        {
            if (_dgvAccounts.SelectedRows.Count > 0)
            {
                int idx = _dgvAccounts.SelectedRows[0].Index;
                if (idx > 0)
                {
                    (_pendingAccounts[idx], _pendingAccounts[idx - 1]) = (_pendingAccounts[idx - 1], _pendingAccounts[idx]);
                    RefreshAccountsGrid();
                    _dgvAccounts.Rows[idx - 1].Selected = true;
                }
            }
        };

        var btnMoveDown = DarkTheme.AddCardButton(card, "\u25BC", 290, btnY, 35);
        btnMoveDown.Click += (_, _) =>
        {
            if (_dgvAccounts.SelectedRows.Count > 0)
            {
                int idx = _dgvAccounts.SelectedRows[0].Index;
                if (idx < _pendingAccounts.Count - 1)
                {
                    (_pendingAccounts[idx], _pendingAccounts[idx + 1]) = (_pendingAccounts[idx + 1], _pendingAccounts[idx]);
                    RefreshAccountsGrid();
                    _dgvAccounts.Rows[idx + 1].Selected = true;
                }
            }
        };

        DarkTheme.AddCardHint(card, "Passwords are encrypted with Windows DPAPI (tied to your user account)", 10, 275);
        DarkTheme.AddCardHint(card, "Character slot is 1-based position on the character select screen", 10, 295);

        return page;
    }

    private void RefreshAccountsGrid()
    {
        _dgvAccounts.Rows.Clear();
        foreach (var a in _pendingAccounts)
            _dgvAccounts.Rows.Add(a.Name, a.Username, a.CharacterName, a.Server, a.CharacterSlot);
    }

    private void ShowAccountDialog(int? editIndex)
    {
        var existing = editIndex.HasValue ? _pendingAccounts[editIndex.Value] : null;

        using var dlg = new Form
        {
            Text = existing != null ? "Edit Account" : "Add Account",
            Size = new Size(380, 340),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false
        };
        DarkTheme.StyleForm(dlg, dlg.Text, dlg.Size);

        int y = 15;
        const int L = 15, I = 130, W = 210, R = 35;

        TextBox MakeTextBox(int tx, int ty, int tw, string text = "")
        {
            var tb = new TextBox { Location = new Point(tx, ty), Width = tw, Text = text,
                BackColor = DarkTheme.BgDark, ForeColor = DarkTheme.FgWhite,
                BorderStyle = BorderStyle.FixedSingle };
            dlg.Controls.Add(tb);
            return tb;
        }

        DarkTheme.AddLabel(dlg, "Display Name:", L, y);
        var txtName = MakeTextBox(I, y - 2, W, existing?.Name ?? "");
        y += R;

        DarkTheme.AddLabel(dlg, "Username:", L, y);
        var txtUsername = MakeTextBox(I, y - 2, W, existing?.Username ?? "");
        y += R;

        DarkTheme.AddLabel(dlg, "Password:", L, y);
        var txtPassword = MakeTextBox(I, y - 2, W);
        txtPassword.UseSystemPasswordChar = true;
        if (existing != null)
            txtPassword.PlaceholderText = "(unchanged)";
        y += R;

        DarkTheme.AddLabel(dlg, "Server:", L, y);
        var txtServer = MakeTextBox(I, y - 2, W, existing?.Server ?? "Dalaya");
        y += R;

        DarkTheme.AddLabel(dlg, "Character Name:", L, y);
        var txtCharName = MakeTextBox(I, y - 2, W, existing?.CharacterName ?? "");
        y += R;

        DarkTheme.AddLabel(dlg, "Character Slot:", L, y);
        var nudSlot = DarkTheme.AddNumeric(dlg, I, y - 2, 60, existing?.CharacterSlot ?? 1, 1, 8);
        y += R;

        var chkLoginFlag = DarkTheme.AddCheckBox(dlg, "Use /login: flag", L, y);
        chkLoginFlag.Checked = existing?.UseLoginFlag ?? true;
        y += R + 5;

        var btnOK = DarkTheme.MakePrimaryButton("Save", L, y);
        btnOK.Width = 100;
        btnOK.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(txtName.Text) || string.IsNullOrWhiteSpace(txtUsername.Text))
            {
                MessageBox.Show("Name and Username are required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var account = existing ?? new LoginAccount();
            account.Name = txtName.Text.Trim();
            account.Username = txtUsername.Text.Trim();
            account.Server = txtServer.Text.Trim();
            account.CharacterName = txtCharName.Text.Trim();
            account.CharacterSlot = (int)nudSlot.Value;
            account.UseLoginFlag = chkLoginFlag.Checked;

            // Only update password if user typed something new
            if (!string.IsNullOrEmpty(txtPassword.Text))
                account.EncryptedPassword = CredentialManager.Encrypt(txtPassword.Text);

            if (!editIndex.HasValue)
                _pendingAccounts.Add(account);

            RefreshAccountsGrid();
            dlg.DialogResult = DialogResult.OK;
        };
        dlg.Controls.Add(btnOK);

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 130, y);
        btnCancel.Width = 100;
        btnCancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
        dlg.Controls.Add(btnCancel);

        dlg.AcceptButton = (IButtonControl)btnOK;
        dlg.ShowDialog(this);
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
            foreach (var f in _inlineFonts)
                f.Dispose();
            _inlineFonts.Clear();
        }
        base.Dispose(disposing);
    }

    private Font TrackFont(Font font)
    {
        _inlineFonts.Add(font);
        return font;
    }

    private static void DisposeControlFonts(params Control?[] controls)
    {
        foreach (var c in controls)
            c?.Font?.Dispose();
    }
}
