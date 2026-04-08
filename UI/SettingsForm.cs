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
    private readonly Action? _onVideoSaved;
    private readonly Action? _openProcessManager;
    private EQClientSettingsForm? _eqClientSettingsForm;

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
    private Label _lblSwitchKeyHotkey = null!;

    // ─── Tray Click controls (Left)
    private ComboBox _cboSingleClick = null!;
    private ComboBox _cboDoubleClick = null!;
    private ComboBox _cboTripleClick = null!;
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
    // Multi-monitor mode controlled by _chkVideoMultiMon on Video tab
    private ComboBox _cboSwitchKeyMode = null!;

    // ─── Window Style controls (on Video tab)
    private CheckBox _chkSlimTitlebar = null!;
    private NumericUpDown _nudTitlebarOffset = null!;
    private NumericUpDown _nudBottomOffset = null!;
    private CheckBox _chkUseHook = null!;
    private CheckBox _chkMaximizeWindow = null!;
    private Label _lblStyleDisabledHint = null!;
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
    private NumericUpDown _nudLoginScreenDelay = null!;
    private ComboBox _cboQuickLogin1 = null!;
    private ComboBox _cboQuickLogin2 = null!;
    private ComboBox _cboQuickLogin3 = null!;
    private ComboBox _cboQuickLogin4 = null!;
    private TextBox _txtAutoLogin1Hotkey = null!;
    private TextBox _txtAutoLogin2Hotkey = null!;
    private TextBox _txtAutoLogin3Hotkey = null!;
    private TextBox _txtAutoLogin4Hotkey = null!;
    private Label _lblAutoLoginHotkeyWarn = null!;
    private Label _lblSlotDuplicateWarn = null!;

    // ─── PiP tab controls
    private CheckBox _chkPipEnabled = null!;
    private ComboBox _cboPipSize = null!;
    private NumericUpDown _nudPipWidth = null!;
    private NumericUpDown _nudPipHeight = null!;
    private NumericUpDown _nudPipOpacity = null!;
    private CheckBox _chkPipBorder = null!;
    private ComboBox _cboPipBorderColor = null!;
    private NumericUpDown _nudPipBorderThickness = null!;
    private NumericUpDown _nudPipMaxWindows = null!;
    private ComboBox _cboPipOrientation = null!;

    // ─── Video tab controls (writes to eqclient.ini, not AppConfig)
    private ComboBox _cboVideoPreset = null!;
    private NumericUpDown _nudVideoWidth = null!;
    private NumericUpDown _nudVideoHeight = null!;
    private NumericUpDown _nudVideoOffsetX = null!;
    private NumericUpDown _nudVideoOffsetY = null!;
    private NumericUpDown _nudVideoTopOffset = null!;
    private CheckBox _chkVideoWindowed = null!;
    private CheckBox _chkVideoMultiMon = null!;
    private ComboBox _cboVideoPrimaryMon = null!;
    private ComboBox _cboVideoSecondaryMon = null!;
    private bool _suppressVideoSync; // prevent SyncVideoPresetToCustom during programmatic changes
    private Label? _lblVideoLoadError; // warning label shown when ini load fails

    // Resolution presets for Video tab
    private static readonly (string Name, int W, int H)[] VideoPresets =
    {
        ("1920x1080", 1920, 1080),
        ("1920x1200", 1920, 1200),
        ("1920x1020 (above taskbar)", 1920, 1020),
        ("2560x1440", 2560, 1440),
        ("3840x2160 (4K)", 3840, 2160),
        ("1280x720", 1280, 720),
        ("1600x900", 1600, 900),
        ("1366x768", 1366, 768),
        ("Custom", 0, 0)
    };




    private int _initialTab;

    public SettingsForm(AppConfig config, Action<AppConfig> onApply, int initialTab = 0, Action? openProcessManager = null, Action? onVideoSaved = null)
    {
        _config = config;
        _onApply = onApply;
        _onVideoSaved = onVideoSaved;
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

        tabs.TabPages.Add(BuildGeneralTab());      // 0
        tabs.TabPages.Add(BuildVideoTab());        // 1
        tabs.TabPages.Add(BuildAccountsTab());     // 2
        tabs.TabPages.Add(BuildPipTab());          // 3
        tabs.TabPages.Add(BuildHotkeysTab());      // 4
        tabs.TabPages.Add(BuildPathsTab());        // 5

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
            try { using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/itsnateai/eqswitch") { UseShellExecute = true }); }
            catch (Exception ex) { FileLogger.Warn($"Failed to open GitHub URL: {ex.Message}"); }
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
        var cardEQ = DarkTheme.MakeCard(page, "⚔", "EverQuest Setup", DarkTheme.CardGreen, 10, y, 480, 118);
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
        _txtEQPath = DarkTheme.AddCardTextBox(cardEQ, I, cy, IW);
        var btnBrowse = DarkTheme.AddCardButton(cardEQ, "Browse...", BRW, cy - 3, 75);
        btnBrowse.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { Description = "Select EverQuest folder", InitialDirectory = _txtEQPath.Text };
            if (fbd.ShowDialog() == DialogResult.OK) _txtEQPath.Text = fbd.SelectedPath;
        };
        cy += R;

        // Exe / Args on same row
        DarkTheme.AddCardLabel(cardEQ, "Exe:", L, cy);
        _txtExeName = DarkTheme.AddCardTextBox(cardEQ, I, cy, 100, 50);
        DarkTheme.AddCardLabel(cardEQ, "Args:", 240, cy);
        _txtArgs = DarkTheme.AddCardTextBox(cardEQ, I2, cy, 100, 100);

        y += 126;

        // ─── Preferences card ────────────────────────────────────
        var cardPrefs = DarkTheme.MakeCard(page, "⚙", "Preferences", DarkTheme.CardGold, 10, y, 480, 65);
        cy = 32;

        var btnEQSettings = DarkTheme.AddCardButton(cardPrefs, "\uD83D\uDCDD EQ Client Settings...", 47, cy, 170);
        btnEQSettings.Click += (_, _) =>
        {
            if (_eqClientSettingsForm != null && !_eqClientSettingsForm.IsDisposed)
            {
                _eqClientSettingsForm.BringToFront();
                _eqClientSettingsForm.Activate();
                return;
            }
            _eqClientSettingsForm = new EQClientSettingsForm(_config);
            _eqClientSettingsForm.FormClosed += (_, _) => _eqClientSettingsForm = null;
            _eqClientSettingsForm.Show();
        };
        var btnProcessMgr = DarkTheme.AddCardButton(cardPrefs, "⚡ Process Manager...", 264, cy, 170);
        btnProcessMgr.Click += (_, _) => _openProcessManager?.Invoke();

        y += 73;

        // ─── Tray Click Actions card ─────────────────────────────
        var clickActions = new[] { "None", "AutoLogin1", "LoginAll", "TogglePiP", "LaunchOne", "LaunchAll", "FixWindows", "SwapWindows", "Settings", "ShowHelp", "AutoLogin2", "AutoLogin3", "AutoLogin4" };
        const int cboW = 140;

        var cardTray = DarkTheme.MakeCard(page, "🖱", "Tray Click Actions", DarkTheme.CardBlue, 10, y, 480, 131);

        // ── Left Click section ──
        var lblLeft = DarkTheme.AddCardLabel(cardTray, "Left Click", 10, 30);
        lblLeft.Font = TrackFont(new Font("Segoe UI Semibold", 9f));
        lblLeft.ForeColor = DarkTheme.FgWhite;

        DarkTheme.AddCardLabel(cardTray, "Single", 20, 52);
        _cboSingleClick = DarkTheme.AddCardComboBox(cardTray, 85, 49, cboW, clickActions);

        DarkTheme.AddCardLabel(cardTray, "Double", 20, 78);
        _cboDoubleClick = DarkTheme.AddCardComboBox(cardTray, 85, 75, cboW, clickActions);

        var lblLeftTriple = DarkTheme.AddCardLabel(cardTray, "Triple", 20, 104);
        lblLeftTriple.Font = TrackFont(new Font("Segoe UI Semibold", 9f));
        _cboTripleClick = DarkTheme.AddCardComboBox(cardTray, 85, 101, cboW, clickActions);

        // ── Middle Click section ──
        var lblMiddle = DarkTheme.AddCardLabel(cardTray, "Middle Click", 250, 30);
        lblMiddle.Font = TrackFont(new Font("Segoe UI Semibold", 9f));
        lblMiddle.ForeColor = DarkTheme.FgWhite;

        DarkTheme.AddCardLabel(cardTray, "Single", 260, 52);
        _cboMiddleClick = DarkTheme.AddCardComboBox(cardTray, 325, 49, cboW, clickActions);

        var lblTriple = DarkTheme.AddCardLabel(cardTray, "Triple", 260, 78);
        lblTriple.Font = TrackFont(new Font("Segoe UI Semibold", 9f));
        _cboMiddleDoubleClick = DarkTheme.AddCardComboBox(cardTray, 325, 75, cboW, clickActions);

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

    private void CheckAutoLoginHotkeyConflicts()
    {
        if (_lblAutoLoginHotkeyWarn == null) return;

        var slots = new[] {
            _txtAutoLogin1Hotkey?.Text.Trim() ?? "",
            _txtAutoLogin2Hotkey?.Text.Trim() ?? "",
            _txtAutoLogin3Hotkey?.Text.Trim() ?? "",
            _txtAutoLogin4Hotkey?.Text.Trim() ?? ""
        };

        // Collect all other hotkeys in the form for conflict checking
        var otherHotkeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_txtSwitchKey?.Text.Trim() is { Length: > 0 } sk) otherHotkeys[sk] = "Switch Key";
        if (_txtGlobalSwitchKey?.Text.Trim() is { Length: > 0 } gsk) otherHotkeys[gsk] = "Global Switch Key";
        if (_txtArrangeWindows?.Text.Trim() is { Length: > 0 } aw) otherHotkeys[aw] = "Arrange Windows";
        if (_txtToggleMultiMon?.Text.Trim() is { Length: > 0 } mm) otherHotkeys[mm] = "Toggle Multi-Mon";
        if (_txtLaunchOne?.Text.Trim() is { Length: > 0 } lo) otherHotkeys[lo] = "Launch One";
        if (_txtLaunchAll?.Text.Trim() is { Length: > 0 } la) otherHotkeys[la] = "Launch All";

        var warnings = new List<string>();

        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            if (s.Length == 0) continue;
            int num = i + 1;

            // Needs modifier
            if (!s.Contains('+'))
                warnings.Add($"Slot {num}: needs modifier");

            // Conflicts with other hotkeys
            if (otherHotkeys.TryGetValue(s, out var conflict))
                warnings.Add($"Slot {num} conflicts with {conflict}");

            // Conflicts with another slot
            for (int j = i + 1; j < slots.Length; j++)
            {
                if (slots[j].Length > 0 && string.Equals(s, slots[j], StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"Slot {num} and {j + 1} are the same");
            }
        }

        if (warnings.Count > 0)
        {
            _lblAutoLoginHotkeyWarn.Text = "\u26A0 " + string.Join("  |  ", warnings);
            _lblAutoLoginHotkeyWarn.ForeColor = DarkTheme.CardWarn;
        }
        else
        {
            _lblAutoLoginHotkeyWarn.Text = "";
        }
    }

    private void CheckDuplicateSlotAccounts()
    {
        if (_lblSlotDuplicateWarn == null) return;

        var combos = new[] { _cboQuickLogin1, _cboQuickLogin2, _cboQuickLogin3, _cboQuickLogin4 };
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dupes = new List<string>();

        for (int i = 0; i < combos.Length; i++)
        {
            var username = GetQuickLoginUsername(combos[i]);
            if (string.IsNullOrEmpty(username)) continue;
            if (seen.TryGetValue(username, out int first))
                dupes.Add($"Slot {first + 1} and {i + 1} share an account");
            else
                seen[username] = i;
        }

        if (dupes.Count > 0)
        {
            _lblSlotDuplicateWarn.Text = "\u26A0 " + string.Join("  |  ", dupes);
            _lblSlotDuplicateWarn.ForeColor = DarkTheme.CardWarn;
        }
        else
        {
            _lblSlotDuplicateWarn.Text = "Bind to tray click actions or hotkeys below";
            _lblSlotDuplicateWarn.ForeColor = DarkTheme.FgDimGray;
        }
    }

    private TextBox MakeHotkeyBox(Panel card, int x, int y, int width = 80)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y), Size = new Size(width, 20),
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
        var cardSwitch = DarkTheme.MakeCard(page, "⚔", "Window Switching", DarkTheme.CardGreen, 10, y, 480, 118);
        int cy = 32;

        _lblSwitchKeyHotkey = DarkTheme.AddCardLabel(cardSwitch, "Switch Key (EQ-only):", L, cy);
        _txtSwitchKey = MakeHotkeyBox(cardSwitch, I, cy - 2);
        _txtSwitchKey.TextChanged += (_, _) =>
        {
            if (_txtSwitchKeyGeneral != null && _txtSwitchKeyGeneral.Text != _txtSwitchKey.Text)
                _txtSwitchKeyGeneral.Text = _txtSwitchKey.Text;
        };
        DarkTheme.AddCardLabel(cardSwitch, "Mode:", 250, cy);
        _cboSwitchKeyMode = DarkTheme.AddCardComboBox(cardSwitch, I2, cy, 130, new[] { "Swap Last Two", "Cycle All" });
        cy += R + 2;

        DarkTheme.AddCardLabel(cardSwitch, "Global Switch Key:", L, cy);
        _txtGlobalSwitchKey = MakeHotkeyBox(cardSwitch, I, cy - 2);
        _lblDuplicateKeyWarn = DarkTheme.AddCardHint(cardSwitch, "Works from any app, cycles thru all", 250, cy + 2);

        // Show warning when both switch keys match
        _txtSwitchKey.TextChanged += (_, _) => CheckDuplicateSwitchKeys();
        _txtGlobalSwitchKey.TextChanged += (_, _) => CheckDuplicateSwitchKeys();
        cy += R + 2;

        DarkTheme.AddCardLabel(cardSwitch, "Clients (Launch All):", L, cy);
        _nudNumClients = DarkTheme.AddCardNumeric(cardSwitch, I, cy, 40, 2, 1, 6);
        DarkTheme.AddCardLabel(cardSwitch, "Delay between launches:", 220, cy);
        _nudLaunchDelay = DarkTheme.AddCardNumeric(cardSwitch, I2 + 50, cy, 40, 3, 1, 30);
        DarkTheme.AddCardHint(cardSwitch, "sec", I2 + 110, cy + 2);

        y += 126;

        // ─── Actions card ────────────────────────────────────────
        var cardActions = DarkTheme.MakeCard(page, "🏰", "Actions & Launcher", DarkTheme.CardGold, 10, y, 480, 110);
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

        DarkTheme.AddCardHint(cardActions, "Press key combo to capture. Leave blank to disable. Backspace/Delete to clear.", L, cy);

        y += 120;

        // ─── Tooltip card ───────────────────────────────────────
        var cardTooltip = DarkTheme.MakeCard(page, "💬", "Tooltip", DarkTheme.CardCyan, 10, y, 480, 60);
        cy = 32;
        DarkTheme.AddCardLabel(cardTooltip, "Delay:", L, cy);
        _nudTooltipDuration = DarkTheme.AddCardNumeric(cardTooltip, 55, cy, 55, 1000, 0, 10000);
        _nudTooltipDuration.Increment = 100;
        DarkTheme.AddCardHint(cardTooltip, "ms — hover time before showing tooltip", 120, cy);

        y += 68;

        // ─── Window Title card ───────────────────────────────────
        var cardTitle = DarkTheme.MakeCard(page, "📝", "Window Title", DarkTheme.CardGreen, 10, y, 480, 40);
        _txtWindowTitleTemplate = DarkTheme.AddCardTextBox(cardTitle, 130, 6, 330, 100);

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
            var size = new Size(160, 100);
            var overlay = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                BackColor = DarkTheme.BgDark,
                TopMost = true,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Size = size,
            };
            // Rounded region — eliminates the boxy look
            var radius = 20;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(0, 0, radius, radius, 180, 90);
            path.AddArc(size.Width - radius, 0, radius, radius, 270, 90);
            path.AddArc(size.Width - radius, size.Height - radius, radius, radius, 0, 90);
            path.AddArc(0, size.Height - radius, radius, radius, 90, 90);
            path.CloseFigure();
            overlay.Region = new Region(path);

            overlay.Location = new Point(
                screen.Bounds.Left + (screen.Bounds.Width - overlay.Width) / 2,
                screen.Bounds.Top + (screen.Bounds.Height - overlay.Height) / 2);

            // Paint directly to avoid white flash from child controls
            var monitorNum = i + 1;
            overlay.Paint += (_, e) =>
            {
                using var numFont = new Font("Segoe UI", 36, FontStyle.Bold);
                using var labelFont = new Font("Segoe UI", 10);
                using var brush = new SolidBrush(DarkTheme.CardGreen);
                using var dimBrush = new SolidBrush(DarkTheme.FgGray);
                var numText = monitorNum.ToString();
                var numSize = e.Graphics.MeasureString(numText, numFont);
                e.Graphics.DrawString(numText, numFont, brush,
                    (size.Width - numSize.Width) / 2, 12);
                var labelText = screen.Primary ? "Primary" : $"Monitor";
                var labelSize = e.Graphics.MeasureString(labelText, labelFont);
                e.Graphics.DrawString(labelText, labelFont, dimBrush,
                    (size.Width - labelSize.Width) / 2, 68);
            };
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
                o.Region?.Dispose();
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
        _txtGinaPath = DarkTheme.AddCardTextBox(cardPaths, I, cy, IW);
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
        _txtNotesPath = DarkTheme.AddCardTextBox(cardPaths, I, cy, IW);
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
        DarkTheme.AddCardHint(cardPaths, "Leave blank to auto-create eqnotes.txt next to EQSwitch", L, cy + 26);
        cy += 42;

        DarkTheme.AddCardLabel(cardPaths, "Dalaya Patcher:", L, cy);
        _txtDalayaPatcherPath = DarkTheme.AddCardTextBox(cardPaths, I, cy, IW);
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
        DarkTheme.AddCardHint(cardPaths, "Patcher may be deleted by antivirus — re-download from SoD if missing.", L, cy + 26);

        y += 168;

        // ─── Tray Icon card ─────────────────────────────────────
        var cardIcon = DarkTheme.MakeCard(page, "🎨", "Tray Icon", DarkTheme.CardPurple, 10, y, 480, 65);
        cy = 32;

        DarkTheme.AddCardLabel(cardIcon, "Custom Icon:", L, cy);
        _txtCustomIconPath = DarkTheme.AddCardTextBox(cardIcon, I, cy, IW);
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

        var btnShortcut = DarkTheme.AddCardButton(cardStartup, "Create Desktop Shortcut", 47, cy, 180);
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

        _chkRunAtStartup = DarkTheme.AddCardCheckBox(cardStartup, "Run at Startup", 280, cy);

        y += 73;

        // ─── eqclient.ini actions card ─────────────────────────────
        var cardIni = DarkTheme.MakeCard(page, "💾", "eqclient.ini", DarkTheme.CardGold, 10, y, 480, 70);
        cy = 30;

        _lblVideoLoadError = new Label
        {
            Text = "⚠ Failed to read eqclient.ini — values shown are defaults, not your settings.",
            Location = new Point(L, cy),
            Size = new Size(460, 18),
            ForeColor = DarkTheme.CardWarn,
            Font = TrackFont(new Font("Segoe UI", 7.5f, FontStyle.Bold)),
            Visible = false
        };
        cardIni.Controls.Add(_lblVideoLoadError);

        var btnBackup = DarkTheme.AddCardButton(cardIni, "📋 Backup", 47, cy, 110);
        btnBackup.Click += (_, _) => VideoBackupIni();

        var btnRestore = DarkTheme.AddCardButton(cardIni, "📂 Restore", 280, cy, 110);
        btnRestore.Click += (_, _) => VideoRestoreIni();

        y += 78;

        // ─── Help button ─────────────────────────────────────────
        var btnHelp = DarkTheme.MakeButton("❓ Help", DarkTheme.BgMedium, 10, y);
        btnHelp.Size = new Size(100, 30);
        btnHelp.Click += (_, _) => HelpForm.Show(_config);
        page.Controls.Add(btnHelp);

        // ─── Update button ──────────────────────────────────────
        var btnUpdate = DarkTheme.MakeButton("⬆ Update", DarkTheme.BgMedium, 195, y);
        btnUpdate.Size = new Size(100, 30);
        btnUpdate.Click += (_, _) =>
        {
            using var dlg = new UpdateDialog();
            dlg.ShowDialog(this);
        };
        page.Controls.Add(btnUpdate);

        // ─── Uninstall button (right-aligned) ───────────────────
        var btnUninstall = DarkTheme.MakeButton("🗑 Uninstall", DarkTheme.CardWarn, 380, y);
        btnUninstall.Size = new Size(110, 30);
        btnUninstall.Click += (_, _) => RunUninstall();
        page.Controls.Add(btnUninstall);

        return page;
    }

    private TabPage BuildPipTab()
    {
        var page = DarkTheme.MakeTabPage("PiP");
        int y = 8;
        const int L = 10, I = 120, R = 28;

        // ─── PiP Overlay card ────────────────────────────────────
        var cardPip = DarkTheme.MakeCard(page, "👁", "Picture in Picture Overlay", DarkTheme.CardCyan, 10, y, 480, 120);
        int cy = 32;

        _chkPipEnabled = DarkTheme.AddCardCheckBox(cardPip, "Enable PiP Overlay", L, cy);
        DarkTheme.AddCardHint(cardPip, "DWM thumbnail — zero CPU, GPU composited", L, cy + 2);
        cy += R;

        DarkTheme.AddCardLabel(cardPip, "Size Preset:", L, cy);
        _cboPipSize = DarkTheme.AddCardComboBox(cardPip, I, cy, 170, new[] {
            "Small (256x144)", "Medium (384x216)", "Large (512x288)",
            "XL (768x432)", "XXL (1024x576)", "XXXL (1600x900)", "Custom"
        });
        _cboPipSize.SelectedIndexChanged += (_, _) =>
        {
            var selected = _cboPipSize.SelectedItem?.ToString() ?? "";
            bool isCustom = selected.StartsWith("Custom");
            _nudPipWidth.Enabled = isCustom;
            _nudPipHeight.Enabled = isCustom;
            // Sync custom W/H to reflect current preset dimensions
            if (!isCustom)
            {
                var presetName = ExtractPipPresetName(selected);
                var pip = new PipConfig { SizePreset = presetName };
                var (w, h) = pip.GetSize();
                if (w > 0) { _nudPipWidth.Value = w; _nudPipHeight.Value = h; }
            }
            // XXXL is nearly full-screen — enforce max 1 PiP window
            if (selected.StartsWith("XXXL"))
            {
                _nudPipMaxWindows.Value = 1;
                _nudPipMaxWindows.Maximum = 1;
            }
            else
            {
                _nudPipMaxWindows.Maximum = 3;
            }
        };
        DarkTheme.AddCardLabel(cardPip, "Max", 338, cy - 12);
        DarkTheme.AddCardLabel(cardPip, "Windows:", 323, cy + 2);
        _nudPipMaxWindows = DarkTheme.AddCardNumeric(cardPip, 393, cy, 40, 3, 1, 3);
        _nudPipMaxWindows.TextAlign = HorizontalAlignment.Center;
        cy += R;

        DarkTheme.AddCardLabel(cardPip, "Custom W:", L, cy);
        _nudPipWidth = DarkTheme.AddCardNumeric(cardPip, I, cy, 55, 320, 100, 1920);
        _nudPipWidth.Enabled = false;
        DarkTheme.AddCardLabel(cardPip, "H:", 185, cy);
        _nudPipHeight = DarkTheme.AddCardNumeric(cardPip, 205, cy, 55, 240, 75, 1080);
        _nudPipHeight.Enabled = false;
        DarkTheme.AddCardLabel(cardPip, "Layout:", 323, cy);
        _cboPipOrientation = DarkTheme.AddCardComboBox(cardPip, 385, cy, 85, new[] { "Vertical", "Horizontal" });

        y += 128;

        // ─── Appearance card ─────────────────────────────────────
        var cardLook = DarkTheme.MakeCard(page, "🎨", "Appearance", DarkTheme.CardPurple, 10, y, 480, 90);
        cy = 32;

        DarkTheme.AddCardLabel(cardLook, "Opacity:", L, cy);
        _nudPipOpacity = DarkTheme.AddCardNumeric(cardLook, I, cy, 60, 245, 0, 255);
        _nudPipOpacity.Increment = 5;
        DarkTheme.AddCardHint(cardLook, "0-255", I + 65, cy + 2);

        _chkPipBorder = DarkTheme.AddCardCheckBox(cardLook, "Show Border", 230, cy);
        _chkPipBorder.CheckedChanged += (_, _) =>
        {
            _cboPipBorderColor.Enabled = _chkPipBorder.Checked;
            _nudPipBorderThickness.Enabled = _chkPipBorder.Checked;
        };
        cy += R;

        DarkTheme.AddCardLabel(cardLook, "Border Color:", L, cy);
        _cboPipBorderColor = DarkTheme.AddCardComboBox(cardLook, I, cy, 100, new[] { "Blue", "Green", "Red" });
        DarkTheme.AddCardLabel(cardLook, "Thickness:", 230, cy);
        _nudPipBorderThickness = DarkTheme.AddCardNumeric(cardLook, 310, cy, 50, 3, 1, 10);

        y += 95;
        DarkTheme.AddHint(page, "Hold Ctrl + Left Click to drag PiP window to a new position", 20, y);

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
        var color = hasKey
            ? Color.FromArgb(220, 190, 100)   // gold — key is set
            : Color.FromArgb(220, 100, 100);  // red — not set
        _txtSwitchKeyGeneral.ForeColor = color;
        _lblSwitchKey.ForeColor = color;
        _lblSwitchKey.Text = hasKey ? "EQ Switch Key:" : "EQ Switch Key: (not set!)";

        // Mirror gold highlight to the Hotkeys tab copy
        _lblSwitchKeyHotkey.ForeColor = color;
        _txtSwitchKey.ForeColor = color;
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

        // Require at least one modifier — bare keys are not valid hotkey combos
        if (!e.Control && !e.Alt && !e.Shift) return;

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
        _cboTripleClick.SelectedItem = _config.TrayClick.TripleClick;
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
        _txtAutoLogin1Hotkey.Text = _config.Hotkeys.AutoLogin1;
        _txtAutoLogin2Hotkey.Text = _config.Hotkeys.AutoLogin2;
        _txtAutoLogin3Hotkey.Text = _config.Hotkeys.AutoLogin3;
        _txtAutoLogin4Hotkey.Text = _config.Hotkeys.AutoLogin4;

        // Layout
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
        _lblStyleDisabledHint.Visible = !_config.Layout.SlimTitlebar;

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
        _nudPipBorderThickness.Value = Math.Clamp(_config.Pip.BorderThickness, 1, 10);
        _cboPipBorderColor.Enabled = _config.Pip.ShowBorder;
        _nudPipBorderThickness.Enabled = _config.Pip.ShowBorder;

        // Video (reads from eqclient.ini)
        _chkVideoWindowed.Checked = _config.EQClientIni.ForceWindowedMode;
        _chkVideoMultiMon.Checked = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        _nudVideoTopOffset.Value = DarkTheme.ClampNud(_nudVideoTopOffset, _config.Layout.TopOffset);
        PopulateVideoFromIni();
    }

    private void RunUninstall()
    {
        var result = MessageBox.Show(
            "This will revert all external changes made by EQSwitch:\n\n" +
            "  • Restore original dinput8.dll in EQ folder (if backed up)\n" +
            "  • Remove startup shortcut\n" +
            "  • Remove desktop shortcut\n\n" +
            "EQSwitch's own config and logs will NOT be deleted.\n" +
            "eqclient.ini settings will NOT be reverted (use .bak files).\n\n" +
            "Continue?",
            "EQSwitch — Uninstall",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes) return;

        var actions = UninstallHelper.CleanUp(_config);

        if (actions.Count == 0)
            actions.Add("Nothing to clean up — no external modifications found.");

        MessageBox.Show(
            string.Join("\n", actions),
            "EQSwitch — Uninstall Complete",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        // Persist RunAtStartup=false so ValidateStartupPath doesn't recreate the shortcut
        _chkRunAtStartup.Checked = false;
        _config.RunAtStartup = false;
        ConfigManager.Save(_config);
        ConfigManager.FlushSave();
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
                Mode = _chkVideoMultiMon.Checked ? "multimonitor" : "single",
                TargetMonitor = _cboVideoPrimaryMon.SelectedIndex >= 0 ? _cboVideoPrimaryMon.SelectedIndex : 0,
                SecondaryMonitor = _cboVideoSecondaryMon.SelectedIndex <= 0 ? -1 : _cboVideoSecondaryMon.SelectedIndex - 1,
                TopOffset = (int)_nudVideoTopOffset.Value,
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
                AutoLogin1 = _txtAutoLogin1Hotkey.Text.Trim(),
                AutoLogin2 = _txtAutoLogin2Hotkey.Text.Trim(),
                AutoLogin3 = _txtAutoLogin3Hotkey.Text.Trim(),
                AutoLogin4 = _txtAutoLogin4Hotkey.Text.Trim(),
                // Once enabled, the hotkey is unlocked permanently
                MultiMonitorEnabled = _chkVideoMultiMon.Checked || _config.Hotkeys.MultiMonitorEnabled,
                DirectSwitchKeys = _config.Hotkeys.DirectSwitchKeys,
                SwitchKeyMode = _cboSwitchKeyMode.SelectedItem?.ToString() == "Cycle All" ? "cycleAll" : "swapLast"
            },
            Launch = new LaunchConfig
            {
                ExeName = _txtExeName.Text.Trim(),
                Arguments = _txtArgs.Text.Trim(),
                NumClients = (int)_nudNumClients.Value,
                LaunchDelayMs = (int)_nudLaunchDelay.Value * 1000,
                FixDelayMs = _config.Launch.FixDelayMs
            },
            Pip = new PipConfig
            {
                Enabled = _chkPipEnabled.Checked,
                SizePreset = ExtractPipPresetName(_cboPipSize.SelectedItem?.ToString() ?? "Large"),
                CustomWidth = (int)_nudPipWidth.Value,
                CustomHeight = (int)_nudPipHeight.Value,
                Opacity = (byte)_nudPipOpacity.Value,
                ShowBorder = _chkPipBorder.Checked,
                BorderColor = _cboPipBorderColor.SelectedItem?.ToString() ?? "Green",
                BorderThickness = (int)_nudPipBorderThickness.Value,
                MaxWindows = (int)_nudPipMaxWindows.Value,
                Orientation = _cboPipOrientation.SelectedItem?.ToString() ?? "Vertical",
                SavedPositions = _config.Pip.SavedPositions // preserve existing positions
            },
            TrayClick = new TrayClickConfig
            {
                SingleClick = _cboSingleClick.SelectedItem?.ToString() ?? "LaunchOne",
                DoubleClick = _cboDoubleClick.SelectedItem?.ToString() ?? "None",
                TripleClick = _cboTripleClick.SelectedItem?.ToString() ?? "None",
                MiddleClick = _cboMiddleClick.SelectedItem?.ToString() ?? "TogglePiP",
                MiddleDoubleClick = _cboMiddleDoubleClick.SelectedItem?.ToString() ?? "Settings"
            },
            GinaPath = _txtGinaPath.Text.Trim(),
            NotesPath = _txtNotesPath.Text.Trim(),
            DalayaPatcherPath = _txtDalayaPatcherPath.Text.Trim(),
            RunAtStartup = _chkRunAtStartup.Checked,
            Characters = _config.Characters,
            Accounts = _pendingAccounts,
            LoginScreenDelayMs = (int)(_nudLoginScreenDelay.Value * 1000),
            QuickLogin1 = GetQuickLoginUsername(_cboQuickLogin1),
            QuickLogin2 = GetQuickLoginUsername(_cboQuickLogin2),
            QuickLogin3 = GetQuickLoginUsername(_cboQuickLogin3),
            QuickLogin4 = GetQuickLoginUsername(_cboQuickLogin4)
        };

        // Apply startup registry change
        if (newConfig.RunAtStartup != _config.RunAtStartup)
            StartupManager.SetRunAtStartup(newConfig.RunAtStartup);

        // MaximizeWindow lives in EQClientIni, update in-place
        newConfig.EQClientIni = _config.EQClientIni;
        newConfig.EQClientIni.MaximizeWindow = _chkMaximizeWindow.Checked;
        newConfig.EQClientIni.ConfiguredKeys.Add("Maximized");

        _onApply(newConfig);
        VideoSaveToIni();
        FileLogger.Info("Settings applied");
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
        _cboPipSize.SelectedIndex = 2; // fallback to Large
    }

    // ─── Accounts Tab ──────────────────────────────────────────────

    private TabPage BuildAccountsTab()
    {
        var page = DarkTheme.MakeTabPage("Accounts");
        int y = 8;

        page.AutoScroll = true;
        var card = DarkTheme.MakeCard(page, "\uD83D\uDD11", "Login Accounts", DarkTheme.CardGold, 10, y, 480, 230);

        _dgvAccounts = new DataGridView
        {
            Location = new Point(10, 32),
            Size = new Size(458, 120),
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
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
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

        _dgvAccounts.Columns.Add("Num", "#");
        _dgvAccounts.Columns["Num"]!.Width = 30;
        _dgvAccounts.Columns.Add("Character", "Character");
        _dgvAccounts.Columns["Character"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dgvAccounts.Columns["Character"]!.FillWeight = 30;
        _dgvAccounts.Columns.Add("Username", "Username");
        _dgvAccounts.Columns["Username"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dgvAccounts.Columns["Username"]!.FillWeight = 30;
        _dgvAccounts.Columns.Add("Server", "Server");
        _dgvAccounts.Columns["Server"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dgvAccounts.Columns["Server"]!.FillWeight = 25;
        _dgvAccounts.Columns.Add("Slot", "Slot");
        _dgvAccounts.Columns["Slot"]!.Width = 40;

        RefreshAccountsGrid();
        card.Controls.Add(_dgvAccounts);

        int btnY = 160;
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

        // Login delay — compact, next to the move buttons
        DarkTheme.AddCardLabel(card, "Delay:", 340, btnY + 3);
        _nudLoginScreenDelay = DarkTheme.AddNumeric(card, 385, btnY, 55,
            _config.LoginScreenDelayMs / 1000m, 1, 15);
        _nudLoginScreenDelay.DecimalPlaces = 1;
        _nudLoginScreenDelay.Increment = 0.5m;
        DarkTheme.AddCardLabel(card, "s", 442, btnY + 3);

        DarkTheme.AddCardHint(card, "DPAPI-encrypted passwords.", 10, 190);
        DarkTheme.AddCardHint(card, "Delay = seconds before typing credentials.", 250, 190);

        // ─── Quick Login Slots ───────────────────────────────────────
        y += 240;
        var slotsCard = DarkTheme.MakeCard(page, "\u26A1", "Quick Login Slots", DarkTheme.CardGold, 10, y, 480, 100);
        DarkTheme.AddCardLabel(slotsCard, "Slot 1:", 10, 30);
        _cboQuickLogin1 = DarkTheme.AddCardComboBox(slotsCard, 55, 27, 150, Array.Empty<string>());
        DarkTheme.AddCardLabel(slotsCard, "Slot 2:", 215, 30);
        _cboQuickLogin2 = DarkTheme.AddCardComboBox(slotsCard, 260, 27, 150, Array.Empty<string>());
        DarkTheme.AddCardLabel(slotsCard, "Slot 3:", 10, 56);
        _cboQuickLogin3 = DarkTheme.AddCardComboBox(slotsCard, 55, 53, 150, Array.Empty<string>());
        DarkTheme.AddCardLabel(slotsCard, "Slot 4:", 215, 56);
        _cboQuickLogin4 = DarkTheme.AddCardComboBox(slotsCard, 260, 53, 150, Array.Empty<string>());
        RefreshQuickLoginCombos();
        SelectQuickLoginCombo(_cboQuickLogin1, _config.QuickLogin1);
        SelectQuickLoginCombo(_cboQuickLogin2, _config.QuickLogin2);
        SelectQuickLoginCombo(_cboQuickLogin3, _config.QuickLogin3);
        SelectQuickLoginCombo(_cboQuickLogin4, _config.QuickLogin4);
        _lblSlotDuplicateWarn = DarkTheme.AddCardHint(slotsCard, "Bind to tray click actions or hotkeys below", 10, 82);
        _cboQuickLogin1.SelectedIndexChanged += (_, _) => CheckDuplicateSlotAccounts();
        _cboQuickLogin2.SelectedIndexChanged += (_, _) => CheckDuplicateSlotAccounts();
        _cboQuickLogin3.SelectedIndexChanged += (_, _) => CheckDuplicateSlotAccounts();
        _cboQuickLogin4.SelectedIndexChanged += (_, _) => CheckDuplicateSlotAccounts();

        // ─── Auto-Login Hotkeys ──────────────────────────────────────
        y += 108;
        var hkCard = DarkTheme.MakeCard(page, "\u2328", "Auto-Login Hotkeys", DarkTheme.CardGreen, 10, y, 480, 90);
        DarkTheme.AddCardLabel(hkCard, "Slot 1:", 10, 30);
        _txtAutoLogin1Hotkey = MakeHotkeyBox(hkCard, 55, 28);
        DarkTheme.AddCardLabel(hkCard, "Slot 2:", 160, 30);
        _txtAutoLogin2Hotkey = MakeHotkeyBox(hkCard, 205, 28);
        DarkTheme.AddCardHint(hkCard, "Press combo to set. Backspace = clear.", 290, 30);
        DarkTheme.AddCardLabel(hkCard, "Slot 3:", 10, 54);
        _txtAutoLogin3Hotkey = MakeHotkeyBox(hkCard, 55, 52);
        DarkTheme.AddCardLabel(hkCard, "Slot 4:", 160, 54);
        _txtAutoLogin4Hotkey = MakeHotkeyBox(hkCard, 205, 52);
        _lblAutoLoginHotkeyWarn = DarkTheme.AddCardHint(hkCard, "", 10, 74);
        _txtAutoLogin1Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtAutoLogin2Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtAutoLogin3Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtAutoLogin4Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();

        return page;
    }

    private void RefreshAccountsGrid()
    {
        _dgvAccounts.Rows.Clear();
        for (int i = 0; i < _pendingAccounts.Count; i++)
        {
            var a = _pendingAccounts[i];
            _dgvAccounts.Rows.Add(i + 1, a.CharacterName, a.Username, a.Server, a.CharacterSlot);
        }
        RefreshQuickLoginCombos();
    }

    private void RefreshQuickLoginCombos()
    {
        if (_cboQuickLogin1 == null) return; // not built yet
        var labels = _pendingAccounts.Select(a =>
            string.IsNullOrEmpty(a.CharacterName) ? a.Username : a.CharacterName).ToList();
        labels.Insert(0, "(None)");

        var combos = new[] { _cboQuickLogin1, _cboQuickLogin2, _cboQuickLogin3, _cboQuickLogin4 };
        var saved = combos.Select(c => c?.SelectedItem?.ToString()).ToArray();
        foreach (var cbo in combos)
        {
            if (cbo == null) continue;
            cbo.Items.Clear();
            cbo.Items.AddRange(labels.ToArray<object>());
        }
        for (int i = 0; i < combos.Length; i++)
        {
            if (combos[i] == null) continue;
            combos[i]!.SelectedItem = saved[i] ?? "(None)";
            if (combos[i]!.SelectedIndex < 0) combos[i]!.SelectedIndex = 0;
        }
    }

    /// <summary>Select the combo item matching a username from config.</summary>
    private void SelectQuickLoginCombo(ComboBox cbo, string username)
    {
        if (string.IsNullOrEmpty(username)) { cbo.SelectedIndex = 0; return; }
        var account = _pendingAccounts.FirstOrDefault(a => a.Username == username);
        if (account == null) { cbo.SelectedIndex = 0; return; }
        var label = string.IsNullOrEmpty(account.CharacterName) ? account.Username : account.CharacterName;
        cbo.SelectedItem = label;
        if (cbo.SelectedIndex < 0) cbo.SelectedIndex = 0;
    }

    /// <summary>Get the username for the selected quick login combo item.</summary>
    private string GetQuickLoginUsername(ComboBox cbo)
    {
        if (cbo.SelectedIndex <= 0) return ""; // (None) or nothing selected
        int accountIdx = cbo.SelectedIndex - 1; // offset by (None) at index 0
        return accountIdx < _pendingAccounts.Count ? _pendingAccounts[accountIdx].Username : "";
    }

    private void ShowAccountDialog(int? editIndex)
    {
        var existing = editIndex.HasValue ? _pendingAccounts[editIndex.Value] : null;

        using var dlg = new Form
        {
            Text = existing != null ? "Edit Account" : "Add Account",
            Size = new Size(380, 305),
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

        DarkTheme.AddLabel(dlg, "Character:", L, y);
        var txtCharName = MakeTextBox(I, y - 2, W, existing?.CharacterName ?? "");
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
            if (string.IsNullOrWhiteSpace(txtUsername.Text))
            {
                MessageBox.Show("Username is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var account = existing ?? new LoginAccount();
            account.Name = txtCharName.Text.Trim(); // sync Name from Character for backwards compat
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

    // ─── Video Tab (eqclient.ini) ───────────────────────────────────

    private TabPage BuildVideoTab()
    {
        var page = DarkTheme.MakeTabPage("Video");
        int y = 8;
        const int L = 10;

        // ─── Resolution card ──────────────────────────────────────
        var cardRes = DarkTheme.MakeCard(page, "📺", "EQ Resolution", DarkTheme.CardPurple, 10, y, 480, 140);
        int cy = 32;

        DarkTheme.AddLabel(cardRes, "Preset:", L, cy + 2);
        _cboVideoPreset = new ComboBox
        {
            Location = new Point(80, cy),
            Size = new Size(150, 25),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat
        };
        PopulateVideoPresets();
        _cboVideoPreset.SelectedIndexChanged += CboVideoPreset_SelectedIndexChanged;
        cardRes.Controls.Add(_cboVideoPreset);
        DarkTheme.WrapWithBorder(_cboVideoPreset);

        _chkVideoWindowed = DarkTheme.AddCheckBox(cardRes, "Windowed Mode", 245, cy + 2);

        cy += 30;
        DarkTheme.AddLabel(cardRes, "Width:", L, cy + 2);
        _nudVideoWidth = DarkTheme.AddNumeric(cardRes, 80, cy, 70, 1920, 320, 7680);
        _nudVideoWidth.ValueChanged += (_, _) => SyncVideoPresetToCustom();

        DarkTheme.AddLabel(cardRes, "Height:", 160, cy + 2);
        _nudVideoHeight = DarkTheme.AddNumeric(cardRes, 210, cy, 70, 1080, 200, 4320);
        _nudVideoHeight.ValueChanged += (_, _) => SyncVideoPresetToCustom();

        var btnReset = DarkTheme.AddCardButton(cardRes, "🔄 Reset", 370, cy, 95);
        btnReset.Click += (_, _) => VideoResetDefaults();

        cy += 30;
        DarkTheme.AddLabel(cardRes, "Offset X:", L, cy + 2);
        _nudVideoOffsetX = DarkTheme.AddNumeric(cardRes, 80, cy, 55, 0, -5000, 5000);
        DarkTheme.AddLabel(cardRes, "Y:", 145, cy + 2);
        _nudVideoOffsetY = DarkTheme.AddNumeric(cardRes, 162, cy, 55, 0, -5000, 5000);
        DarkTheme.AddLabel(cardRes, "Top:", 230, cy + 2);
        _nudVideoTopOffset = DarkTheme.AddNumeric(cardRes, 260, cy, 55, _config.Layout.TopOffset, -100, 200);
        DarkTheme.AddHint(cardRes, "px down from top edge", 320, cy + 4);
        cy += 28;
        DarkTheme.AddHint(cardRes, "Resolution & offsets save to eqclient.ini on Apply. Changes require EQ restart.", L, cy + 2);

        y += 148;

        // ─── Monitor card ─────────────────────────────────────────
        var cardMon = DarkTheme.MakeCard(page, "🖥", "Monitor Selection", DarkTheme.CardBlue, 10, y, 480, 118);
        cy = 32;

        _chkVideoMultiMon = DarkTheme.AddCheckBox(cardMon, "Multi-Monitor Mode", L, cy);

        cy += 26;
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ToArray();
        var monItems = new string[screens.Length];
        for (int i = 0; i < screens.Length; i++)
        {
            var primary = screens[i].Primary ? " (primary)" : "";
            monItems[i] = $"{i + 1}: {screens[i].Bounds.Width}x{screens[i].Bounds.Height}{primary}";
        }

        DarkTheme.AddLabel(cardMon, "Primary:", L + 10, cy + 2);
        _cboVideoPrimaryMon = new ComboBox
        {
            Location = new Point(80, cy), Size = new Size(155, 25),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat
        };
        _cboVideoPrimaryMon.Items.AddRange(monItems);
        _cboVideoPrimaryMon.SelectedIndex = Math.Clamp(_config.Layout.TargetMonitor, 0, screens.Length - 1);
        cardMon.Controls.Add(_cboVideoPrimaryMon);
        DarkTheme.WrapWithBorder(_cboVideoPrimaryMon);

        var secItems = new string[screens.Length + 1];
        secItems[0] = "Auto (first non-primary)";
        for (int i = 0; i < monItems.Length; i++) secItems[i + 1] = monItems[i];

        DarkTheme.AddLabel(cardMon, "Secondary:", 250, cy + 2);
        _cboVideoSecondaryMon = new ComboBox
        {
            Location = new Point(325, cy), Size = new Size(145, 25),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat
        };
        _cboVideoSecondaryMon.Items.AddRange(secItems);
        var secIdx = _config.Layout.SecondaryMonitor < 0 ? 0 : _config.Layout.SecondaryMonitor + 1;
        _cboVideoSecondaryMon.SelectedIndex = Math.Clamp(secIdx, 0, secItems.Length - 1);
        cardMon.Controls.Add(_cboVideoSecondaryMon);
        DarkTheme.WrapWithBorder(_cboVideoSecondaryMon);

        cy += 28;
        var btnIdentify = DarkTheme.AddCardButton(cardMon, "🔍 Identify", L, cy - 3, 90);
        btnIdentify.Click += (_, _) => ShowMonitorIdentifiers();
        DarkTheme.AddCardHint(cardMon, "Primary = active client. Secondary = background client (multimonitor mode).", 110, cy);

        y += 125;

        // ─── Window Style card ───────────────────────────────────
        var cardStyle = DarkTheme.MakeCard(page, "🪟", "Window Style", DarkTheme.CardPurple, 10, y, 480, 175);
        cy = 32;

        const int hintX = 260;

        _chkSlimTitlebar = DarkTheme.AddCardCheckBox(cardStyle, "Fullscreen Window (WinEQ2 mode)", L, cy);
        DarkTheme.AddCardHint(cardStyle, "Auto-sets resolution + hides titlebar", hintX, cy + 2);
        cy += 24;

        DarkTheme.AddCardLabel(cardStyle, "Titlebar hidden (px):", L, cy);
        _nudTitlebarOffset = DarkTheme.AddCardNumeric(cardStyle, 140, cy, 55, 22, 0, 40);
        DarkTheme.AddCardHint(cardStyle, "22 = thin strip, 30 = fully hidden", hintX, cy);
        cy += 26;

        DarkTheme.AddCardLabel(cardStyle, "Bottom margin (px):", L, cy);
        _nudBottomOffset = DarkTheme.AddCardNumeric(cardStyle, 140, cy, 55, 22, 0, 100);
        DarkTheme.AddCardHint(cardStyle, "Game render height reduction", hintX, cy);
        cy += 22;

        DarkTheme.AddCardHint(cardStyle, "WinEQ2 mode: hidden 13, margin 21 — keep margin > hidden", L, cy);
        cy += 22;

        _chkUseHook = DarkTheme.AddCardCheckBox(cardStyle, "DLL Hook (zero flicker)", L, cy);
        DarkTheme.AddCardHint(cardStyle, "Hooks SetWindowPos inside EQ", hintX, cy + 2);
        cy += 24;

        _chkMaximizeWindow = DarkTheme.AddCardCheckBox(cardStyle, "Maximize on Launch", L, cy);
        DarkTheme.AddCardHint(cardStyle, "Sets Maximized=1 in eqclient.ini", hintX, cy + 2);
        cy += 26;

        _lblStyleDisabledHint = new Label
        {
            Text = "If disabled, set EQ video resolution to fit above the taskbar",
            Location = new Point(L, cy),
            AutoSize = true,
            ForeColor = Color.FromArgb(230, 190, 60),
            Font = DarkTheme.FontUI75,
            Visible = true,
        };
        cardStyle.Controls.Add(_lblStyleDisabledHint);

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
            _lblStyleDisabledHint.Visible = !slim;
        };

        return page;
    }

    private void PopulateVideoFromIni()
    {
        var iniPath = Path.Combine(_config.EQPath, "eqclient.ini");
        if (!File.Exists(iniPath))
        {
            FileLogger.Info($"VideoSettings: eqclient.ini not found at {iniPath}");
            _cboVideoPreset.SelectedIndex = 0;


            if (_lblVideoLoadError != null)
            {
                _lblVideoLoadError.Text = "⚠ eqclient.ini not found — check EQ Path on the General tab.";
                _lblVideoLoadError.Visible = true;
            }
            return;
        }

        _suppressVideoSync = true;
        try
        {
            // Clear any previous error state (e.g., after a successful Restore)

            if (_lblVideoLoadError != null) _lblVideoLoadError.Visible = false;

            var lines = File.ReadAllLines(iniPath, System.Text.Encoding.Default);
            string currentSection = "";

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("["))
                {
                    currentSection = trimmed;
                    continue;
                }

                var parts = trimmed.Split('=', 2);
                if (parts.Length != 2) continue;

                string key = parts[0].Trim();
                string val = parts[1].Trim();

                if (currentSection.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "width":
                            if (int.TryParse(val, out int w)) _nudVideoWidth.Value = Math.Clamp(w, 320, 7680);
                            break;
                        case "height":
                            if (int.TryParse(val, out int h)) _nudVideoHeight.Value = Math.Clamp(h, 200, 4320);
                            break;
                        case "windowedmode":
                            _chkVideoWindowed.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "xoffset":
                            if (int.TryParse(val, out int ox)) _nudVideoOffsetX.Value = Math.Clamp(ox, -5000, 5000);
                            break;
                        case "yoffset":
                            if (int.TryParse(val, out int oy)) _nudVideoOffsetY.Value = Math.Clamp(oy, -5000, 5000);
                            break;
                    }
                }
            }

            // Match to preset
            int width = (int)_nudVideoWidth.Value;
            int height = (int)_nudVideoHeight.Value;
            int presetIdx = Array.FindIndex(VideoPresets, p => p.W == width && p.H == height);
            if (presetIdx >= 0)
            {
                _cboVideoPreset.SelectedIndex = presetIdx;
            }
            else
            {
                string customKey = $"{width}x{height}";
                int customIdx = _cboVideoPreset.Items.IndexOf(customKey);
                _cboVideoPreset.SelectedIndex = customIdx >= 0 ? customIdx : _cboVideoPreset.Items.Count - 1;
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error("VideoSettings: load error", ex);
            _cboVideoPreset.SelectedIndex = 0;


            if (_lblVideoLoadError != null) _lblVideoLoadError.Visible = true;
        }
        finally { _suppressVideoSync = false; }
    }

    private void VideoSaveToIni()
    {
        try
        {
            var iniPath = Path.Combine(_config.EQPath, "eqclient.ini");

            // Save TopOffset, monitors, windowed mode, multi-mon to AppConfig
            _config.Layout.TopOffset = (int)_nudVideoTopOffset.Value;
            _config.Layout.TargetMonitor = _cboVideoPrimaryMon.SelectedIndex;
            _config.Layout.SecondaryMonitor = _cboVideoSecondaryMon.SelectedIndex <= 0 ? -1 : _cboVideoSecondaryMon.SelectedIndex - 1;
            _config.EQClientIni.ForceWindowedMode = _chkVideoWindowed.Checked;
            _config.Layout.Mode = _chkVideoMultiMon.Checked ? "multimonitor" : "single";
            if (_chkVideoMultiMon.Checked)
                _config.Hotkeys.MultiMonitorEnabled = true;
            VideoSaveCustomPreset();
            ConfigManager.Save(_config);

            if (!File.Exists(iniPath))
            {
                FileLogger.Warn($"VideoSettings: cannot save — {iniPath} not found");
                MessageBox.Show(
                    $"eqclient.ini not found at:\n{iniPath}\n\nEQSwitch config was saved, but video settings could not be written to the INI file.\nCheck your EQ Path on the General tab.",
                    "Save Incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var lines = File.ReadAllLines(iniPath, System.Text.Encoding.Default).ToList();
            int sectionStart = -1;
            int sectionEnd = lines.Count;

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase))
                    sectionStart = i;
                else if (sectionStart >= 0 && trimmed.StartsWith("["))
                {
                    sectionEnd = i;
                    break;
                }
            }

            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Width"] = ((int)_nudVideoWidth.Value).ToString(),
                ["Height"] = ((int)_nudVideoHeight.Value).ToString(),
                ["WindowedMode"] = _chkVideoWindowed.Checked ? "TRUE" : "FALSE",
                ["Maximized"] = _config.EQClientIni.MaximizeWindow ? "1" : "0",
                ["XOffset"] = ((int)_nudVideoOffsetX.Value).ToString(),
                ["YOffset"] = ((int)_nudVideoOffsetY.Value).ToString()
            };

            if (sectionStart >= 0)
            {
                for (int i = sectionStart + 1; i < sectionEnd; i++)
                {
                    var parts = lines[i].Split('=', 2);
                    if (parts.Length == 2 && settings.ContainsKey(parts[0].Trim()))
                    {
                        lines[i] = $"{parts[0].Trim()}={settings[parts[0].Trim()]}";
                        settings.Remove(parts[0].Trim());
                    }
                }

                foreach (var kv in settings)
                    lines.Insert(sectionEnd, $"{kv.Key}={kv.Value}");
            }
            else
            {
                lines.Add("");
                lines.Add("[VideoMode]");
                foreach (var kv in settings)
                    lines.Add($"{kv.Key}={kv.Value}");
            }

            // Sync WindowedMode in [Defaults] too — EQ reads from there
            string wmVal = _chkVideoWindowed.Checked ? "TRUE" : "FALSE";
            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("WindowedMode=", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("WindowedModeX", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("WindowedModeY", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"WindowedMode={wmVal}";
                }
            }

            VideoWriteWithRetry(iniPath, lines);
            FileLogger.Info("VideoSettings: saved to eqclient.ini");

            // Notify TrayManager so hook DLL configs are updated for injected processes
            _onVideoSaved?.Invoke();
        }
        catch (Exception ex)
        {
            FileLogger.Error("VideoSettings: save error", ex);
            MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void VideoWriteWithRetry(string path, List<string> lines, int maxRetries = 2, int delayMs = 500)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                File.WriteAllLines(path, lines, System.Text.Encoding.Default);
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                FileLogger.Warn($"VideoSettings: file locked, retry {attempt + 1}/{maxRetries}");
                Thread.Sleep(delayMs);
            }
        }
    }

    private void VideoBackupIni()
    {
        var iniPath = Path.Combine(_config.EQPath, "eqclient.ini");
        if (!File.Exists(iniPath))
        {
            MessageBox.Show("eqclient.ini not found.", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            int bakNum = 1;
            while (File.Exists($"{iniPath}.bak{bakNum}") && bakNum < 99)
                bakNum++;

            string bakPath = $"{iniPath}.bak{bakNum}";
            File.Copy(iniPath, bakPath, overwrite: false);
            FileLogger.Info($"VideoSettings: backed up eqclient.ini → {Path.GetFileName(bakPath)}");
            MessageBox.Show($"Backed up to:\n{Path.GetFileName(bakPath)}", "Backup Created",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            FileLogger.Error("VideoSettings: backup error", ex);
            MessageBox.Show($"Backup failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void VideoRestoreIni()
    {
        var iniPath = Path.Combine(_config.EQPath, "eqclient.ini");
        var dir = Path.GetDirectoryName(iniPath) ?? ".";
        using var dlg = new OpenFileDialog
        {
            Title = "Restore eqclient.ini from Backup",
            Filter = "Backup Files (*.bak*)|*.bak*|All Files (*.*)|*.*",
            InitialDirectory = dir,
            FileName = ""
        };

        if (dlg.ShowDialog() != DialogResult.OK)
            return;

        try
        {
            File.Copy(dlg.FileName, iniPath, overwrite: true);
            FileLogger.Info($"VideoSettings: restored eqclient.ini from {Path.GetFileName(dlg.FileName)}");
            PopulateVideoFromIni();
            MessageBox.Show($"Restored from:\n{Path.GetFileName(dlg.FileName)}", "Restore Complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            FileLogger.Error("VideoSettings: restore error", ex);
            MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void VideoResetDefaults()
    {
        _cboVideoPreset.SelectedIndex = 0; // triggers width/height update
        _nudVideoOffsetX.Value = 0;
        _nudVideoOffsetY.Value = 0;
        _chkVideoWindowed.Checked = true;
        _chkVideoMultiMon.Checked = false;
        _nudVideoTopOffset.Value = 0;
    }

    private void PopulateVideoPresets()
    {
        _cboVideoPreset.Items.Clear();
        for (int i = 0; i < VideoPresets.Length - 1; i++)
            _cboVideoPreset.Items.Add(VideoPresets[i].Name);

        var builtInSet = new HashSet<string>(VideoPresets.Select(p => $"{p.W}x{p.H}"));
        foreach (var custom in _config.CustomVideoPresets)
        {
            if (!builtInSet.Contains(custom))
                _cboVideoPreset.Items.Add(custom);
        }

        _cboVideoPreset.Items.Add("Custom");
    }

    private void CboVideoPreset_SelectedIndexChanged(object? sender, EventArgs e)
    {
        string? selected = _cboVideoPreset.SelectedItem?.ToString();
        if (selected == null || selected == "Custom") return;

        _suppressVideoSync = true;
        try
        {
            var preset = Array.Find(VideoPresets, p => p.Name == selected);
            if (preset.W > 0)
            {
                _nudVideoWidth.Value = preset.W;
                _nudVideoHeight.Value = preset.H;
                return;
            }

            var dims = selected.Split('x');
            if (dims.Length == 2 && int.TryParse(dims[0], out int w) && int.TryParse(dims[1], out int h))
            {
                _nudVideoWidth.Value = Math.Clamp(w, 320, 7680);
                _nudVideoHeight.Value = Math.Clamp(h, 200, 4320);
            }
        }
        finally { _suppressVideoSync = false; }
    }

    private void SyncVideoPresetToCustom()
    {
        if (_suppressVideoSync) return;

        int w = (int)_nudVideoWidth.Value;
        int h = (int)_nudVideoHeight.Value;

        foreach (var p in VideoPresets)
        {
            if (p.W == w && p.H == h) return;
        }

        for (int i = 0; i < _cboVideoPreset.Items.Count; i++)
        {
            string? item = _cboVideoPreset.Items[i]?.ToString();
            if (item == $"{w}x{h}") { _cboVideoPreset.SelectedIndex = i; return; }
        }

        for (int i = 0; i < _cboVideoPreset.Items.Count; i++)
        {
            if (_cboVideoPreset.Items[i]?.ToString() == "Custom") { _cboVideoPreset.SelectedIndex = i; return; }
        }
    }

    private void VideoSaveCustomPreset()
    {
        int w = (int)_nudVideoWidth.Value;
        int h = (int)_nudVideoHeight.Value;
        string key = $"{w}x{h}";

        if (Array.Exists(VideoPresets, p => p.W == w && p.H == h))
            return;

        if (_config.CustomVideoPresets.Contains(key))
            return;

        _config.CustomVideoPresets.Add(key);

        while (_config.CustomVideoPresets.Count > 3)
            _config.CustomVideoPresets.RemoveAt(0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _eqClientSettingsForm?.Close();
            _eqClientSettingsForm = null;
            DismissMonitorOverlays();
            // Dispose inline Font objects on hotkey TextBoxes and other controls
            // that were created with new Font() — base.Dispose doesn't clean these up
            DisposeControlFonts(_txtSwitchKeyGeneral, _txtSwitchKey, _txtGlobalSwitchKey,
                _txtArrangeWindows, _txtToggleMultiMon, _txtLaunchOne, _txtLaunchAll);
            foreach (var f in _inlineFonts)
                f.Dispose();
            _inlineFonts.Clear();
            DarkTheme.DisposeControlFonts(this);
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
