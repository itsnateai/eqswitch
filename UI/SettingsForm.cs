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

    private NumericUpDown _nudLogTrimThreshold = null!;

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
    private TextBox _txtTogglePip = null!;
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
    private NumericUpDown _nudLaunchDelay = null!;

    // ─── Paths tab controls
    private TextBox _txtGinaPath = null!;
    private TextBox _txtNotesPath = null!;
    private TextBox _txtDalayaPatcherPath = null!;
    private CheckBox _chkRunAtStartup = null!;

    // ─── Accounts tab controls (Phase 4: v4 Account + Character as first-class)
    private List<Account> _pendingAccounts = new();
    private List<Character> _pendingCharacters = new();
    private int? _lastNameCollisionHash;

    /// <summary>
    /// Phase 4: raised after a successful ApplySettings when at least one Account.Name
    /// collides with a Character.Name. Payload is a comma-separated list of the
    /// colliding names. TrayManager subscribes + surfaces as a non-blocking balloon.
    /// Fires only when the collision set changes across saves (hash-deduped; nullable
    /// sentinel so a legitimate 0 hash doesn't masquerade as "no prior collision").
    /// </summary>
    public event Action<string>? OnSameNameCollision;
    private DataGridView _dgvAccounts = null!;
    private DataGridView _dgvCharacters = null!;
    private NumericUpDown _nudLoginScreenDelay = null!;
    private ComboBox _cboQuickLogin1 = null!;
    private ComboBox _cboQuickLogin2 = null!;
    private ComboBox _cboQuickLogin3 = null!;
    private ComboBox _cboQuickLogin4 = null!;
    private TextBox _txtAutoLogin1Hotkey = null!;
    private TextBox _txtAutoLogin2Hotkey = null!;
    private TextBox _txtAutoLogin3Hotkey = null!;
    private TextBox _txtAutoLogin4Hotkey = null!;
    private TextBox _txtTeamLogin1Hotkey = null!;
    private TextBox _txtTeamLogin2Hotkey = null!;
    private TextBox _txtTeamLogin3Hotkey = null!;
    private TextBox _txtTeamLogin4Hotkey = null!;
    private Label _lblAutoLoginHotkeyWarn = null!;
    private Label _lblSlotDuplicateWarn = null!;
    private Label _lblTeamSummary = null!;
    private string _pendingTeam1A = "";
    private string _pendingTeam1B = "";
    private string _pendingTeam2A = "";
    private string _pendingTeam2B = "";
    private string _pendingTeam3A = "";
    private string _pendingTeam3B = "";
    private string _pendingTeam4A = "";
    private string _pendingTeam4B = "";
    private bool _pendingTeam1AutoEnter;
    private bool _pendingTeam2AutoEnter;
    private bool _pendingTeam3AutoEnter;
    private bool _pendingTeam4AutoEnter;
    private CheckBox _chkAutoEnterWorld = null!;

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

        // Phase 4: load v4 lists directly. LegacyAccounts is now a derived shadow
        // rebuilt from (Accounts, Characters) on Save via ReverseMapToLegacy.
        _pendingAccounts = _config.Accounts.Select(a => new Account
        {
            Name = a.Name,
            Username = a.Username,
            EncryptedPassword = a.EncryptedPassword,
            Server = a.Server,
            UseLoginFlag = a.UseLoginFlag,
        }).ToList();

        _pendingCharacters = _config.Characters.Select(c => new Character
        {
            Name = c.Name,
            AccountUsername = c.AccountUsername,
            AccountServer = c.AccountServer,
            CharacterSlot = c.CharacterSlot,
            DisplayLabel = c.DisplayLabel,
            ClassHint = c.ClassHint,
            Notes = c.Notes,
        }).ToList();

        _pendingTeam1A = _config.Team1Account1;
        _pendingTeam1B = _config.Team1Account2;
        _pendingTeam2A = _config.Team2Account1;
        _pendingTeam2B = _config.Team2Account2;
        _pendingTeam3A = _config.Team3Account1;
        _pendingTeam3B = _config.Team3Account2;
        _pendingTeam4A = _config.Team4Account1;
        _pendingTeam4B = _config.Team4Account2;

        _pendingTeam1AutoEnter = _config.Team1AutoEnter;
        _pendingTeam2AutoEnter = _config.Team2AutoEnter;
        _pendingTeam3AutoEnter = _config.Team3AutoEnter;
        _pendingTeam4AutoEnter = _config.Team4AutoEnter;

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
        btnSave.Click += (_, _) => { if (ApplySettings()) { ConfigManager.Save(_config); Close(); } };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 320, 10);
        btnApply.Click += (_, _) => { if (ApplySettings()) { ConfigManager.Save(_config); } };

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
        _lblSwitchKey.Font = TrackFont(new Font("Segoe UI Semibold", 8.5f));
        _txtSwitchKeyGeneral = new TextBox
        {
            Location = new Point(I, cy - 2), Size = new Size(80, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle, Font = TrackFont(new Font("Consolas", 10f, FontStyle.Bold)),
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
        var cardPrefs = DarkTheme.MakeCard(page, "\u2699", "Preferences", DarkTheme.CardGold, 10, y, 480, 38);

        var btnEQSettings = DarkTheme.AddCardButton(cardPrefs, "EQ Client Settings...", 143, 7, 140);
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
        var btnProcessMgr = DarkTheme.AddCardButton(cardPrefs, "Process Manager...", 306, 7, 135);
        btnProcessMgr.Click += (_, _) => _openProcessManager?.Invoke();

        y += 46;

        // ─── Tray Click Actions card ─────────────────────────────
        var clickActions = new[] { "None", "AutoLogin1", "AutoLoginTeam1", "TogglePiP", "LaunchOne", "LaunchAll", "FixWindows", "SwapWindows", "Settings", "ShowHelp", "AutoLogin2", "AutoLogin3", "AutoLogin4", "AutoLoginTeam2", "AutoLoginTeam3", "AutoLoginTeam4" };
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

        y += 139;

        // ─── Log Trim card ──────────────────────────────────────
        var cardLog = DarkTheme.MakeCard(page, "✂", "Log File Trimming", DarkTheme.CardCyan, 10, y, 480, 48);
        DarkTheme.AddCardLabel(cardLog, "Threshold:", 175, 10);
        _nudLogTrimThreshold = DarkTheme.AddCardNumeric(cardLog, 248, 8, 55, _config.LogTrimThresholdMB, 10, 500);
        _nudLogTrimThreshold.Increment = 10;
        DarkTheme.AddCardHint(cardLog, "MB", 308, 12);
        var btnTrimNow = DarkTheme.MakeButton("✂ Trim Now", DarkTheme.BgInput, 345, 7);
        btnTrimNow.Size = new Size(85, 24);
        btnTrimNow.Font = DarkTheme.FontUI85;
        cardLog.Controls.Add(btnTrimNow);
        btnTrimNow.Click += (_, _) => FileOperations.TrimLogFiles(_config, (int)_nudLogTrimThreshold.Value, msg => MessageBox.Show(msg, "Trim Logs", MessageBoxButtons.OK, MessageBoxIcon.Information));
        DarkTheme.AddCardHint(cardLog, "Async trim + archive old logs", 10, 30);

        y += 56;

        // ─── Window Title card ───────────────────────────────────
        var cardTitle = DarkTheme.MakeCard(page, "\uD83D\uDCDD", "Window Title", DarkTheme.CardGreen, 10, y, 480, 56);
        _txtWindowTitleTemplate = DarkTheme.AddCardTextBox(cardTitle, 130, 8, 330, 100);
        DarkTheme.AddCardHint(cardTitle, "Applied after client is in world", 10, 36);

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

        var entries = new (string Key, string Label)[] {
            (_txtAutoLogin1Hotkey?.Text.Trim() ?? "", "Acct 1"),
            (_txtAutoLogin2Hotkey?.Text.Trim() ?? "", "Acct 2"),
            (_txtAutoLogin3Hotkey?.Text.Trim() ?? "", "Acct 3"),
            (_txtAutoLogin4Hotkey?.Text.Trim() ?? "", "Acct 4"),
            (_txtTeamLogin1Hotkey?.Text.Trim() ?? "", "Team 1"),
            (_txtTeamLogin2Hotkey?.Text.Trim() ?? "", "Team 2"),
            (_txtTeamLogin3Hotkey?.Text.Trim() ?? "", "Team 3"),
            (_txtTeamLogin4Hotkey?.Text.Trim() ?? "", "Team 4"),
        };

        // Collect all other hotkeys in the form for conflict checking
        var otherHotkeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_txtSwitchKey?.Text.Trim() is { Length: > 0 } sk) otherHotkeys[sk] = "Switch Key";
        if (_txtGlobalSwitchKey?.Text.Trim() is { Length: > 0 } gsk) otherHotkeys[gsk] = "Global Switch Key";
        if (_txtArrangeWindows?.Text.Trim() is { Length: > 0 } aw) otherHotkeys[aw] = "Arrange Windows";
        if (_txtToggleMultiMon?.Text.Trim() is { Length: > 0 } mm) otherHotkeys[mm] = "Toggle Multi-Mon";
        if (_txtLaunchOne?.Text.Trim() is { Length: > 0 } lo) otherHotkeys[lo] = "Launch One";
        if (_txtLaunchAll?.Text.Trim() is { Length: > 0 } la) otherHotkeys[la] = "Launch All";
        if (_txtTogglePip?.Text.Trim() is { Length: > 0 } tp) otherHotkeys[tp] = "Toggle PiP";

        var warnings = new List<string>();

        for (int i = 0; i < entries.Length; i++)
        {
            var (key, label) = entries[i];
            if (key.Length == 0) continue;

            // Needs modifier
            if (!key.Contains('+'))
                warnings.Add($"{label}: needs modifier");

            // Conflicts with other hotkeys
            if (otherHotkeys.TryGetValue(key, out var conflict))
                warnings.Add($"{label} conflicts with {conflict}");

            // Conflicts with another entry
            for (int j = i + 1; j < entries.Length; j++)
            {
                if (entries[j].Key.Length > 0 && string.Equals(key, entries[j].Key, StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"{label} and {entries[j].Label} conflict");
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
        bool reverted = false;

        for (int i = 0; i < combos.Length; i++)
        {
            var username = GetQuickLoginUsername(combos[i]);
            if (string.IsNullOrEmpty(username)) continue;
            if (seen.ContainsKey(username))
            {
                // Revert the duplicate back to (None) — SoD crashes on duplicate logins
                combos[i].SelectedIndex = 0;
                reverted = true;
            }
            else
            {
                seen[username] = i;
            }
        }

        if (reverted)
        {
            _lblSlotDuplicateWarn.Text = "\u26A0 Same account can't be in multiple slots (SoD limitation)";
            _lblSlotDuplicateWarn.ForeColor = DarkTheme.CardWarn;
        }
        else
        {
            _lblSlotDuplicateWarn.Text = "Bind to tray click actions or hotkeys above";
            _lblSlotDuplicateWarn.ForeColor = DarkTheme.FgDimGray;
        }
    }

    private TextBox MakeHotkeyBox(Panel card, int x, int y, int width = 80)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y), Size = new Size(width, 20),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.None, Font = TrackFont(new Font("Consolas", 9f)),
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
        var cardSwitch = DarkTheme.MakeCard(page, "⚔", "Window Switching", DarkTheme.CardGreen, 10, y, 480, 90);
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

        y += 98;

        // ─── Actions card ────────────────────────────────────────
        var cardActions = DarkTheme.MakeCard(page, "🏰", "Actions Launcher", DarkTheme.CardGold, 10, y, 480, 140);
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

        DarkTheme.AddCardLabel(cardActions, "PiP Toggle:", L, cy);
        _txtTogglePip = MakeHotkeyBox(cardActions, I, cy - 2);
        cy += R + 2;

        DarkTheme.AddCardHint(cardActions, "Press key combo to capture. Leave blank to disable. Backspace/Delete to clear.", L, cy);

        y += 150;

        // ─── Quick Login Slots (defines what the hotkeys below trigger) ──
        var slotsCard = DarkTheme.MakeCard(page, "\u26A1", "Quick Individual Login Accounts", DarkTheme.CardGold, 10, y, 480, 110);
        DarkTheme.AddCardLabel(slotsCard, "Slot 1:", 10, 34);
        _cboQuickLogin1 = DarkTheme.AddCardComboBox(slotsCard, 55, 31, 150, Array.Empty<string>());
        DarkTheme.AddCardLabel(slotsCard, "Slot 2:", 215, 34);
        _cboQuickLogin2 = DarkTheme.AddCardComboBox(slotsCard, 260, 31, 150, Array.Empty<string>());
        DarkTheme.AddCardLabel(slotsCard, "Slot 3:", 10, 60);
        _cboQuickLogin3 = DarkTheme.AddCardComboBox(slotsCard, 55, 57, 150, Array.Empty<string>());
        DarkTheme.AddCardLabel(slotsCard, "Slot 4:", 215, 60);
        _cboQuickLogin4 = DarkTheme.AddCardComboBox(slotsCard, 260, 57, 150, Array.Empty<string>());
        RefreshQuickLoginCombos();
        SelectQuickLoginCombo(_cboQuickLogin1, _config.QuickLogin1);
        SelectQuickLoginCombo(_cboQuickLogin2, _config.QuickLogin2);
        SelectQuickLoginCombo(_cboQuickLogin3, _config.QuickLogin3);
        SelectQuickLoginCombo(_cboQuickLogin4, _config.QuickLogin4);
        _lblSlotDuplicateWarn = DarkTheme.AddCardHint(slotsCard, "Assign accounts to bind with hotkeys below", 10, 86);
        _cboQuickLogin1.SelectedIndexChanged += (_, _) => CheckDuplicateSlotAccounts();
        _cboQuickLogin2.SelectedIndexChanged += (_, _) => CheckDuplicateSlotAccounts();
        _cboQuickLogin3.SelectedIndexChanged += (_, _) => CheckDuplicateSlotAccounts();
        _cboQuickLogin4.SelectedIndexChanged += (_, _) => CheckDuplicateSlotAccounts();

        y += 118;

        // ─── Auto-Login Hotkeys ─────────────────────────────────
        var hkCard = DarkTheme.MakeCard(page, "\u2328", "Auto-Login Hotkeys", DarkTheme.CardGreen, 10, y, 480, 98);
        DarkTheme.AddCardHint(hkCard, "Press combo to set. Backspace/Delete to clear.", 180, 6);

        // Accounts row — 4 columns fit within 480px card
        const int hkL = 65, hkW = 78, hkG = 18, hkS = 100;  // label-start, box-width, label-to-box gap, column-stride
        var lblAccounts = DarkTheme.AddCardLabel(hkCard, "Accounts", 10, 34);
        lblAccounts.Font = TrackFont(new Font("Segoe UI Semibold", 8f));
        lblAccounts.ForeColor = DarkTheme.FgDimGray;
        DarkTheme.AddCardLabel(hkCard, "1:", hkL, 34);
        _txtAutoLogin1Hotkey = MakeHotkeyBox(hkCard, hkL + hkG, 32, hkW);
        DarkTheme.AddCardLabel(hkCard, "2:", hkL + hkS, 34);
        _txtAutoLogin2Hotkey = MakeHotkeyBox(hkCard, hkL + hkS + hkG, 32, hkW);
        DarkTheme.AddCardLabel(hkCard, "3:", hkL + hkS * 2, 34);
        _txtAutoLogin3Hotkey = MakeHotkeyBox(hkCard, hkL + hkS * 2 + hkG, 32, hkW);
        DarkTheme.AddCardLabel(hkCard, "4:", hkL + hkS * 3, 34);
        _txtAutoLogin4Hotkey = MakeHotkeyBox(hkCard, hkL + hkS * 3 + hkG, 32, hkW);

        // Teams row
        var lblTeams = DarkTheme.AddCardLabel(hkCard, "Teams", 10, 62);
        lblTeams.Font = TrackFont(new Font("Segoe UI Semibold", 8f));
        lblTeams.ForeColor = DarkTheme.FgDimGray;
        DarkTheme.AddCardLabel(hkCard, "1:", hkL, 62);
        _txtTeamLogin1Hotkey = MakeHotkeyBox(hkCard, hkL + hkG, 60, hkW);
        DarkTheme.AddCardLabel(hkCard, "2:", hkL + hkS, 62);
        _txtTeamLogin2Hotkey = MakeHotkeyBox(hkCard, hkL + hkS + hkG, 60, hkW);
        DarkTheme.AddCardLabel(hkCard, "3:", hkL + hkS * 2, 62);
        _txtTeamLogin3Hotkey = MakeHotkeyBox(hkCard, hkL + hkS * 2 + hkG, 60, hkW);
        DarkTheme.AddCardLabel(hkCard, "4:", hkL + hkS * 3, 62);
        _txtTeamLogin4Hotkey = MakeHotkeyBox(hkCard, hkL + hkS * 3 + hkG, 60, hkW);

        // Conflict warning below Teams row
        _lblAutoLoginHotkeyWarn = DarkTheme.AddCardHint(hkCard, "", 10, 82);
        _lblAutoLoginHotkeyWarn.ForeColor = DarkTheme.FgWarn;

        _txtAutoLogin1Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtAutoLogin2Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtAutoLogin3Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtAutoLogin4Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtTeamLogin1Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtTeamLogin2Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtTeamLogin3Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtTeamLogin4Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtTogglePip.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();

        y += 120;

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
        var cardPaths = DarkTheme.MakeCard(page, "📁", "External Tools", DarkTheme.CardGold, 10, y, 480, 170);
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
        cy += 52;

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

        y += 178;

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

        var btnRestore = DarkTheme.AddCardButton(cardIni, "📂 Restore", 340, cy, 110);
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
        DarkTheme.AddCardHint(cardPip, "DWM thumbnail — zero CPU, GPU composited", 170, cy + 2);
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
            ? DarkTheme.CardGold    // gold — key is set
            : DarkTheme.CardWarn;   // red — not set
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

        // Tray Click Actions — map config values to display names
        _cboSingleClick.SelectedItem = TrayActionToDisplay(_config.TrayClick.SingleClick);
        _cboDoubleClick.SelectedItem = TrayActionToDisplay(_config.TrayClick.DoubleClick);
        _cboTripleClick.SelectedItem = TrayActionToDisplay(_config.TrayClick.TripleClick);
        _cboMiddleClick.SelectedItem = TrayActionToDisplay(_config.TrayClick.MiddleClick);
        _cboMiddleDoubleClick.SelectedItem = TrayActionToDisplay(_config.TrayClick.MiddleDoubleClick);

        // Hotkeys
        _txtSwitchKeyGeneral.Text = _config.Hotkeys.SwitchKey;
        _txtSwitchKey.Text = _config.Hotkeys.SwitchKey;
        _cboSwitchKeyMode.SelectedItem = _config.Hotkeys.SwitchKeyMode == "cycleAll" ? "Cycle All" : "Swap Last Two";
        _txtGlobalSwitchKey.Text = _config.Hotkeys.GlobalSwitchKey;
        _txtArrangeWindows.Text = _config.Hotkeys.ArrangeWindows;
        _txtToggleMultiMon.Text = _config.Hotkeys.ToggleMultiMonitor;
        _txtLaunchOne.Text = _config.Hotkeys.LaunchOne;
        _txtLaunchAll.Text = _config.Hotkeys.LaunchAll;
        _txtTogglePip.Text = _config.Hotkeys.TogglePip;
        _txtAutoLogin1Hotkey.Text = _config.Hotkeys.AutoLogin1;
        _txtAutoLogin2Hotkey.Text = _config.Hotkeys.AutoLogin2;
        _txtAutoLogin3Hotkey.Text = _config.Hotkeys.AutoLogin3;
        _txtAutoLogin4Hotkey.Text = _config.Hotkeys.AutoLogin4;
        _txtTeamLogin1Hotkey.Text = _config.Hotkeys.TeamLogin1;
        _txtTeamLogin2Hotkey.Text = _config.Hotkeys.TeamLogin2;
        _txtTeamLogin3Hotkey.Text = _config.Hotkeys.TeamLogin3;
        _txtTeamLogin4Hotkey.Text = _config.Hotkeys.TeamLogin4;

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

        // Performance

        // Launch
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
            "  • Clean up any legacy DLL artifacts from EQ folder\n" +
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

    private bool ApplySettings()
    {
        // Phase 3.5-D: hotkey conflict detection — same key combo bound to
        // multiple actions causes RegisterHotKey to silently fail on the
        // second registration. Block Save with an actionable modal.
        var allHotkeys = new[]
        {
            ("Fix Windows",      _txtArrangeWindows.Text.Trim()),
            ("Launch One",       _txtLaunchOne.Text.Trim()),
            ("Launch All",       _txtLaunchAll.Text.Trim()),
            ("Multi-Mon Toggle", _txtToggleMultiMon.Text.Trim()),
            ("PiP Toggle",       _txtTogglePip.Text.Trim()),
            ("AutoLogin 1",      _txtAutoLogin1Hotkey.Text.Trim()),
            ("AutoLogin 2",      _txtAutoLogin2Hotkey.Text.Trim()),
            ("AutoLogin 3",      _txtAutoLogin3Hotkey.Text.Trim()),
            ("AutoLogin 4",      _txtAutoLogin4Hotkey.Text.Trim()),
            ("Team Login 1",     _txtTeamLogin1Hotkey.Text.Trim()),
            ("Team Login 2",     _txtTeamLogin2Hotkey.Text.Trim()),
            ("Team Login 3",     _txtTeamLogin3Hotkey.Text.Trim()),
            ("Team Login 4",     _txtTeamLogin4Hotkey.Text.Trim()),
        };

        var conflicts = allHotkeys
            .Where(t => !string.IsNullOrEmpty(t.Item2))
            .GroupBy(t => t.Item2, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (conflicts.Count > 0)
        {
            var lines = conflicts.Select(g =>
                $"  {g.Key}  \u2192  {string.Join(", ", g.Select(t => t.Item1))}");
            var msg = "Cannot save — the same key combo is bound to multiple actions:\n\n"
                    + string.Join("\n", lines)
                    + "\n\nUnbind duplicates, then try again.";
            MessageBox.Show(msg, "Hotkey Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Phase 4: cross-section validation — structural errors block Save.
        // Run after P3.5-D hotkey conflict check but before mutating _config.

        // 0. No empty Account or Character Names. A hand-edited config or a degenerate
        //    Import can slip these past UI-level checks; block before they corrupt the tray.
        var emptyAcct = _pendingAccounts.FirstOrDefault(a => string.IsNullOrWhiteSpace(a.Name));
        if (emptyAcct != null)
        {
            MessageBox.Show(
                $"An Account is missing a Name (Username '{emptyAcct.Username}'). Set a Name or delete it before saving.",
                "Empty Account Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        var emptyChar = _pendingCharacters.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Name));
        if (emptyChar != null)
        {
            MessageBox.Show(
                "A Character is missing a Name. Set a Name or delete it before saving.",
                "Empty Character Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // 1. Account names unique (Ordinal).
        var acctNameDupes = _pendingAccounts
            .GroupBy(a => a.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        if (acctNameDupes.Any())
        {
            var names = string.Join(", ", acctNameDupes.Select(g => $"'{g.Key}' ({g.Count()} times)"));
            MessageBox.Show($"Account names must be unique.\n\nDuplicates: {names}",
                "Duplicate Account Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // 2. Account (Username, Server) unique.
        var acctCredDupes = _pendingAccounts
            .GroupBy(a => $"{a.Username}\u0001{a.Server}", StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        if (acctCredDupes.Any())
        {
            var keys = string.Join(", ", acctCredDupes.Select(g => g.Key.Replace("\u0001", "@")));
            MessageBox.Show($"Account (Username, Server) must be unique.\n\nDuplicates: {keys}",
                "Duplicate Credentials", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // 3. Character names unique.
        var charNameDupes = _pendingCharacters
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        if (charNameDupes.Any())
        {
            var names = string.Join(", ", charNameDupes.Select(g => $"'{g.Key}' ({g.Count()} times)"));
            MessageBox.Show($"Character names must be unique.\n\nDuplicates: {names}",
                "Duplicate Character Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // 4. Character FK — every non-empty AccountUsername must resolve to an Account.
        //    Empty AccountUsername = legitimate Unlink/orphan state.
        foreach (var c in _pendingCharacters)
        {
            if (string.IsNullOrEmpty(c.AccountUsername)) continue;
            bool resolved = _pendingAccounts.Any(a =>
                a.Username.Equals(c.AccountUsername, StringComparison.Ordinal) &&
                a.Server.Equals(c.AccountServer, StringComparison.Ordinal));
            if (!resolved)
            {
                MessageBox.Show(
                    $"Character '{c.Name}' references missing account '{c.AccountUsername}@{c.AccountServer}'.\n\n"
                  + "Edit the Character to pick a valid account, or delete the Character.",
                    "Broken Character FK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        // Phase 4: v4 lists are source of truth. Reverse-map back to LegacyAccounts
        // for downgrade safety + AppConfig.Validate() defense-in-depth cooperation.
        var legacyAccountsForConfig = ReverseMapToLegacy(_pendingAccounts, _pendingCharacters);

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
                TogglePip = _txtTogglePip.Text.Trim(),
                AutoLogin1 = _txtAutoLogin1Hotkey.Text.Trim(),
                AutoLogin2 = _txtAutoLogin2Hotkey.Text.Trim(),
                AutoLogin3 = _txtAutoLogin3Hotkey.Text.Trim(),
                AutoLogin4 = _txtAutoLogin4Hotkey.Text.Trim(),
                TeamLogin1 = _txtTeamLogin1Hotkey.Text.Trim(),
                TeamLogin2 = _txtTeamLogin2Hotkey.Text.Trim(),
                TeamLogin3 = _txtTeamLogin3Hotkey.Text.Trim(),
                TeamLogin4 = _txtTeamLogin4Hotkey.Text.Trim(),
                // Once enabled, the hotkey is unlocked permanently
                MultiMonitorEnabled = _chkVideoMultiMon.Checked || _config.Hotkeys.MultiMonitorEnabled,
                DirectSwitchKeys = _config.Hotkeys.DirectSwitchKeys,
                SwitchKeyMode = _cboSwitchKeyMode.SelectedItem?.ToString() == "Cycle All" ? "cycleAll" : "swapLast"
            },
            Launch = new LaunchConfig
            {
                ExeName = _txtExeName.Text.Trim(),
                Arguments = _txtArgs.Text.Trim(),
                NumClients = _config.Launch.NumClients,
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
                SingleClick = TrayDisplayToAction(_cboSingleClick.SelectedItem?.ToString() ?? "LaunchOne"),
                DoubleClick = TrayDisplayToAction(_cboDoubleClick.SelectedItem?.ToString() ?? "None"),
                TripleClick = TrayDisplayToAction(_cboTripleClick.SelectedItem?.ToString() ?? "None"),
                MiddleClick = TrayDisplayToAction(_cboMiddleClick.SelectedItem?.ToString() ?? "TogglePiP"),
                MiddleDoubleClick = TrayDisplayToAction(_cboMiddleDoubleClick.SelectedItem?.ToString() ?? "Settings")
            },
            GinaPath = _txtGinaPath.Text.Trim(),
            NotesPath = _txtNotesPath.Text.Trim(),
            DalayaPatcherPath = _txtDalayaPatcherPath.Text.Trim(),
            RunAtStartup = _chkRunAtStartup.Checked,
            // Phase 4: v4 lists are authoritative. LegacyAccounts is reverse-mapped
            // for downgrade safety. LegacyCharacterProfiles + CharacterAliases remain
            // pure passthrough until Phase 5 surfaces CharacterAlias editing in the UI.
            LegacyCharacterProfiles = _config.LegacyCharacterProfiles,
            LegacyAccounts = legacyAccountsForConfig,
            Accounts = _pendingAccounts.Select(a => new Account
            {
                Name = a.Name,
                Username = a.Username,
                EncryptedPassword = a.EncryptedPassword,
                Server = a.Server,
                UseLoginFlag = a.UseLoginFlag,
            }).ToList(),
            Characters = _pendingCharacters.Select(c => new Character
            {
                Name = c.Name,
                AccountUsername = c.AccountUsername,
                AccountServer = c.AccountServer,
                CharacterSlot = c.CharacterSlot,
                DisplayLabel = c.DisplayLabel,
                ClassHint = c.ClassHint,
                Notes = c.Notes,
            }).ToList(),
            CharacterAliases = _config.CharacterAliases,
            LoginScreenDelayMs = (int)(_nudLoginScreenDelay.Value * 1000),
            QuickLogin1 = GetQuickLoginUsername(_cboQuickLogin1),
            QuickLogin2 = GetQuickLoginUsername(_cboQuickLogin2),
            QuickLogin3 = GetQuickLoginUsername(_cboQuickLogin3),
            QuickLogin4 = GetQuickLoginUsername(_cboQuickLogin4),
            AutoEnterWorld = _chkAutoEnterWorld.Checked,
            LogTrimThresholdMB = (int)_nudLogTrimThreshold.Value,
            Team1Account1 = _pendingTeam1A,
            Team1Account2 = _pendingTeam1B,
            Team2Account1 = _pendingTeam2A,
            Team2Account2 = _pendingTeam2B,
            Team3Account1 = _pendingTeam3A,
            Team3Account2 = _pendingTeam3B,
            Team4Account1 = _pendingTeam4A,
            Team4Account2 = _pendingTeam4B,
            Team1AutoEnter = _pendingTeam1AutoEnter,
            Team2AutoEnter = _pendingTeam2AutoEnter,
            Team3AutoEnter = _pendingTeam3AutoEnter,
            Team4AutoEnter = _pendingTeam4AutoEnter
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

        // Phase 4: same-name nudge — non-blocking, hash-deduped per collision set so the
        // balloon doesn't spam every Apply when the collision set is unchanged.
        var collisions = _pendingAccounts
            .Where(a => _pendingCharacters.Any(c => c.Name.Equals(a.Name, StringComparison.Ordinal)))
            .Select(a => a.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (collisions.Count > 0)
        {
            var details = string.Join(", ", collisions);
            var hash = details.GetHashCode();
            if (_lastNameCollisionHash != hash)
            {
                _lastNameCollisionHash = hash;
                OnSameNameCollision?.Invoke(details);
            }
        }
        else
        {
            _lastNameCollisionHash = null;
        }

        return true;
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

    /// <summary>
    /// Phase 4: reverse the v4 split so <c>_config.LegacyAccounts</c> stays in sync with
    /// the (Accounts, Characters) source of truth. Each Account with N Characters becomes
    /// N <see cref="LoginAccount"/> rows; an Account with no Characters becomes one bare row.
    /// Orphan Characters (empty <see cref="Character.AccountUsername"/>) are dropped — v3
    /// has no concept of a character without an account. Keeps
    /// <see cref="AppConfig.Validate"/> from triggering a v4 resync that would wipe
    /// Phase-4-only edits.
    ///
    /// Deliberate behavior change from v3: every Character-linked row has
    /// <c>AutoEnterWorld = true</c>. In v3 that flag was per-row; in v4 "is it a Character
    /// vs. Account" encodes the enter-world intent. A v3 config with
    /// <c>{CharacterName = X, AutoEnterWorld = false}</c> will lose the false flag on the
    /// first Save in v4 — the user can re-create the charselect-only intent by removing the
    /// Character and keeping the Account.
    /// </summary>
    private static List<LoginAccount> ReverseMapToLegacy(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Character> characters)
    {
        var result = new List<LoginAccount>();
        foreach (var a in accounts)
        {
            var linked = characters
                .Where(c => c.AccountUsername.Equals(a.Username, StringComparison.Ordinal) &&
                            c.AccountServer.Equals(a.Server, StringComparison.Ordinal))
                .ToList();

            if (linked.Count == 0)
            {
                result.Add(new LoginAccount
                {
                    Name = a.Name,
                    Username = a.Username,
                    EncryptedPassword = a.EncryptedPassword,
                    Server = a.Server,
                    UseLoginFlag = a.UseLoginFlag,
                    CharacterName = "",
                    AutoEnterWorld = false,
                    CharacterSlot = 0,
                });
            }
            else
            {
                foreach (var c in linked)
                {
                    result.Add(new LoginAccount
                    {
                        Name = a.Name,
                        Username = a.Username,
                        EncryptedPassword = a.EncryptedPassword,
                        Server = a.Server,
                        UseLoginFlag = a.UseLoginFlag,
                        CharacterName = c.Name,
                        AutoEnterWorld = true,
                        CharacterSlot = c.CharacterSlot,
                    });
                }
            }
        }
        return result;
    }

    private TabPage BuildAccountsTab()
    {
        var page = DarkTheme.MakeTabPage("Accounts");
        int y = 8;

        page.AutoScroll = true;

        // ─── Accounts card ───────────────────────────────────────────
        var accountsCard = DarkTheme.MakeCard(page, "\uD83D\uDD11", "Accounts", DarkTheme.CardGold, 10, y, 480, 216);

        _dgvAccounts = MakeDualSectionGrid();
        _dgvAccounts.Columns.Add("Num", "#");
        _dgvAccounts.Columns["Num"]!.Width = 30;
        _dgvAccounts.Columns.Add("Name", "Name");
        _dgvAccounts.Columns["Name"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dgvAccounts.Columns["Name"]!.FillWeight = 30;
        _dgvAccounts.Columns.Add("Username", "Username");
        _dgvAccounts.Columns["Username"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dgvAccounts.Columns["Username"]!.FillWeight = 30;
        _dgvAccounts.Columns.Add("Server", "Server");
        _dgvAccounts.Columns["Server"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dgvAccounts.Columns["Server"]!.FillWeight = 20;
        _dgvAccounts.Columns.Add("Flag", "Flag");
        _dgvAccounts.Columns["Flag"]!.Width = 42;
        _dgvAccounts.DoubleClick += (_, _) =>
        {
            if (_dgvAccounts.SelectedRows.Count > 0)
                OnEditAccount(_dgvAccounts.SelectedRows[0].Index);
        };
        accountsCard.Controls.Add(_dgvAccounts);

        int btnY = 160;
        var btnAddAccount = DarkTheme.AddCardButton(accountsCard, "+ Add", 10, btnY, 70);
        btnAddAccount.Click += (_, _) => OnAddAccount();

        var btnEditAccount = DarkTheme.AddCardButton(accountsCard, "Edit", 85, btnY, 60);
        btnEditAccount.Click += (_, _) =>
        {
            if (_dgvAccounts.SelectedRows.Count > 0)
                OnEditAccount(_dgvAccounts.SelectedRows[0].Index);
        };

        var btnDeleteAccount = DarkTheme.AddCardButton(accountsCard, "Delete", 150, btnY, 65);
        btnDeleteAccount.Click += (_, _) =>
        {
            if (_dgvAccounts.SelectedRows.Count > 0)
                OnDeleteAccount(_dgvAccounts.SelectedRows[0].Index);
        };

        var btnBackup = DarkTheme.AddCardButton(accountsCard, "\uD83D\uDCE4 Backup", 225, btnY, 75);
        btnBackup.Click += (_, _) => ExportAccounts();

        var btnImport = DarkTheme.AddCardButton(accountsCard, "\uD83D\uDCE5 Import", 305, btnY, 75);
        btnImport.Click += (_, _) => ImportAccounts();

        // Login delay — compact, right side of button row
        DarkTheme.AddCardLabel(accountsCard, "Delay:", 388, btnY + 3);
        _nudLoginScreenDelay = DarkTheme.AddNumeric(accountsCard, 425, btnY, 45,
            _config.LoginScreenDelayMs / 1000m, 1, 15);
        _nudLoginScreenDelay.DecimalPlaces = 1;
        _nudLoginScreenDelay.Increment = 0.5m;

        DarkTheme.AddCardHint(accountsCard, "DPAPI-encrypted passwords — same Windows user only.", 10, 196);

        y += 224;

        // ─── Characters card ─────────────────────────────────────────
        var charactersCard = DarkTheme.MakeCard(page, "\uD83E\uDDD9", "Characters", DarkTheme.CardPurple, 10, y, 480, 216);

        _dgvCharacters = MakeDualSectionGrid();
        _dgvCharacters.Columns.Add("Num", "#");
        _dgvCharacters.Columns["Num"]!.Width = 30;
        _dgvCharacters.Columns.Add("Name", "Name");
        _dgvCharacters.Columns["Name"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dgvCharacters.Columns["Name"]!.FillWeight = 30;
        _dgvCharacters.Columns.Add("Account", "Account");
        _dgvCharacters.Columns["Account"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dgvCharacters.Columns["Account"]!.FillWeight = 30;
        _dgvCharacters.Columns.Add("Slot", "Slot");
        _dgvCharacters.Columns["Slot"]!.Width = 50;
        _dgvCharacters.Columns.Add("HK", "HK");
        _dgvCharacters.Columns["HK"]!.Width = 70;
        _dgvCharacters.DoubleClick += (_, _) =>
        {
            if (_dgvCharacters.SelectedRows.Count > 0)
                OnEditCharacter(_dgvCharacters.SelectedRows[0].Index);
        };
        charactersCard.Controls.Add(_dgvCharacters);

        var btnAddChar = DarkTheme.AddCardButton(charactersCard, "+ Add Character", 10, btnY, 120);
        btnAddChar.Click += (_, _) => OnAddCharacter();

        var btnEditChar = DarkTheme.AddCardButton(charactersCard, "Edit", 135, btnY, 60);
        btnEditChar.Click += (_, _) =>
        {
            if (_dgvCharacters.SelectedRows.Count > 0)
                OnEditCharacter(_dgvCharacters.SelectedRows[0].Index);
        };

        var btnDeleteChar = DarkTheme.AddCardButton(charactersCard, "Delete", 200, btnY, 65);
        btnDeleteChar.Click += (_, _) =>
        {
            if (_dgvCharacters.SelectedRows.Count > 0)
                OnDeleteCharacter(_dgvCharacters.SelectedRows[0].Index);
        };

        DarkTheme.AddCardHint(charactersCard,
            "Characters enter world; Accounts stop at charselect. Orphan chars (no Account) render '(unassigned)'.",
            10, 196);

        y += 224;

        // ─── Autologin Teams ─────────────────────────────────────────
        var teamsCard = DarkTheme.MakeCard(page, "\uD83D\uDC65", "Autologin Teams", DarkTheme.CardGold, 10, y, 480, 64);
        var btnTeams = DarkTheme.AddCardButton(teamsCard, "Configure Teams...", 10, 32, 120);
        btnTeams.Click += (_, _) => ShowTeamsDialog();
        _lblTeamSummary = DarkTheme.AddCardHint(teamsCard, BuildTeamSummary(), 140, 32);
        _lblTeamSummary.Size = new Size(330, 28);

        // ─── Autologin defaults (flat row) ───────────────────────────
        y += 72;
        _chkAutoEnterWorld = DarkTheme.AddCheckBox(page, "Auto Enter World (legacy default)", 20, y);
        _chkAutoEnterWorld.Checked = _config.AutoEnterWorld;
        y += 22;
        DarkTheme.AddHint(page, "Phase 4: Characters always enter world; Accounts stop at charselect. Team AutoEnter flags per-team.", 20, y);

        RefreshAccountsGrid();
        RefreshCharactersGrid();

        return page;
    }

    /// <summary>
    /// Shared DataGridView factory for the Accounts + Characters grids — same style.
    /// </summary>
    private DataGridView MakeDualSectionGrid()
    {
        return new DataGridView
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
    }

    private void RefreshAccountsGrid()
    {
        if (_dgvAccounts == null) return;
        _dgvAccounts.Rows.Clear();
        for (int i = 0; i < _pendingAccounts.Count; i++)
        {
            var a = _pendingAccounts[i];
            _dgvAccounts.Rows.Add(i + 1, a.Name, a.Username, a.Server, a.UseLoginFlag ? "\u2713" : "");
        }
        RefreshQuickLoginCombos();
    }

    private void RefreshCharactersGrid()
    {
        if (_dgvCharacters == null) return;
        _dgvCharacters.Rows.Clear();
        for (int i = 0; i < _pendingCharacters.Count; i++)
        {
            var c = _pendingCharacters[i];
            var linkedAccount = _pendingAccounts.FirstOrDefault(a =>
                a.Username.Equals(c.AccountUsername, StringComparison.Ordinal) &&
                a.Server.Equals(c.AccountServer, StringComparison.Ordinal));

            string acctDisplay;
            bool acctFlagged;
            if (string.IsNullOrEmpty(c.AccountUsername))
            {
                acctDisplay = "(unassigned)";
                acctFlagged = true;
            }
            else if (linkedAccount != null)
            {
                acctDisplay = linkedAccount.Name;
                acctFlagged = false;
            }
            else
            {
                acctDisplay = $"{c.AccountUsername}@{c.AccountServer} (missing)";
                acctFlagged = true;
            }

            var slotDisplay = c.CharacterSlot == 0 ? "auto" : c.CharacterSlot.ToString();
            var hkDisplay = LookupHotkeyForTarget(c.Name);

            int rowIdx = _dgvCharacters.Rows.Add(i + 1, c.Name, acctDisplay, slotDisplay, hkDisplay);
            if (acctFlagged)
            {
                _dgvCharacters.Rows[rowIdx].Cells["Account"].Style.ForeColor = DarkTheme.FgDimGray;
                _dgvCharacters.Rows[rowIdx].Cells["Account"].Style.Font =
                    TrackFont(new Font("Segoe UI", 9f, FontStyle.Italic));
            }
        }
        RefreshQuickLoginCombos();
    }

    private string LookupHotkeyForTarget(string targetName)
    {
        if (string.IsNullOrEmpty(targetName)) return "";
        // Phase 4 bridge: QuickLogin1-4 + HotkeyConfig.AutoLogin1-4 still hold character
        // bindings until Phase 5 replaces with CharacterHotkeys[]. Show whichever hotkey
        // currently points at this target.
        var slots = new (string target, string combo)[]
        {
            (_config.QuickLogin1, _config.Hotkeys.AutoLogin1),
            (_config.QuickLogin2, _config.Hotkeys.AutoLogin2),
            (_config.QuickLogin3, _config.Hotkeys.AutoLogin3),
            (_config.QuickLogin4, _config.Hotkeys.AutoLogin4),
        };
        foreach (var (target, combo) in slots)
        {
            if (!string.IsNullOrEmpty(combo) && target.Equals(targetName, StringComparison.Ordinal))
                return combo;
        }
        return "";
    }

    private void RefreshQuickLoginCombos()
    {
        if (_cboQuickLogin1 == null) return; // not built yet

        // Phase 4: Quick Login combos prefer Characters (enter-world) over Accounts
        // (charselect-only). Label format: Character.Name or Account.Name; the config
        // value stored is the same name (FK handled at tray dispatch time).
        var labels = new List<string>();
        labels.Add("(None)");
        foreach (var c in _pendingCharacters)
            labels.Add(c.Name);
        foreach (var a in _pendingAccounts)
        {
            // Don't duplicate: if an Account.Name matches a Character.Name we've already
            // added, skip — the character wins.
            if (_pendingCharacters.Any(ch => ch.Name.Equals(a.Name, StringComparison.Ordinal))) continue;
            labels.Add(a.Name);
        }

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

    /// <summary>
    /// Select the combo item matching a config value. Combo items are Character.Name
    /// entries first, then Account.Name entries (no duplicates).
    /// </summary>
    private void SelectQuickLoginCombo(ComboBox cbo, string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) { cbo.SelectedIndex = 0; return; }
        for (int i = 0; i < cbo.Items.Count; i++)
        {
            if (cbo.Items[i]?.ToString() is string s && s.Equals(identifier, StringComparison.Ordinal))
            {
                cbo.SelectedIndex = i;
                return;
            }
        }
        cbo.SelectedIndex = 0;
    }

    /// <summary>
    /// Return the selected Character.Name / Account.Name for the combo, or empty string
    /// when (None) is selected.
    /// </summary>
    private string GetQuickLoginUsername(ComboBox cbo)
    {
        if (cbo.SelectedIndex <= 0) return "";
        return cbo.SelectedItem?.ToString() ?? "";
    }

    // Phase 4: editing routes through AccountEditDialog / CharacterEditDialog. The
    // old inline dialog builders are gone.

    private void OnAddAccount()
    {
        using var dlg = new AccountEditDialog(null, _pendingAccounts);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            _pendingAccounts.Add(dlg.Result);
            RefreshAccountsGrid();
            RefreshCharactersGrid();   // new Account may resolve previously-orphaned chars
            _lblTeamSummary.Text = BuildTeamSummary();
        }
    }

    private void OnEditAccount(int idx)
    {
        if (idx < 0 || idx >= _pendingAccounts.Count) return;
        var existing = _pendingAccounts[idx];
        using var dlg = new AccountEditDialog(existing, _pendingAccounts);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            var oldName = existing.Name;
            var newName = dlg.Result.Name;
            _pendingAccounts[idx] = dlg.Result;
            // If Account.Name changed, propagate to any slot referencing the old name.
            if (!oldName.Equals(newName, StringComparison.Ordinal))
            {
                UpdateTeamSlotUsername(oldName, newName);
                PropagateNameChangeToQuickLogins(oldName, newName);
            }
            RefreshAccountsGrid();
            RefreshCharactersGrid();
            _lblTeamSummary.Text = BuildTeamSummary();
        }
    }

    private void OnDeleteAccount(int idx)
    {
        if (idx < 0 || idx >= _pendingAccounts.Count) return;
        var acct = _pendingAccounts[idx];
        var dependents = _pendingCharacters.Where(c =>
            c.AccountUsername.Equals(acct.Username, StringComparison.Ordinal) &&
            c.AccountServer.Equals(acct.Server, StringComparison.Ordinal)).ToList();

        if (dependents.Count == 0)
        {
            if (MessageBox.Show($"Delete Account '{acct.Name}'?", "Delete Account",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _pendingAccounts.RemoveAt(idx);
            ClearStaleTeamSlots(acct.Name);
        }
        else
        {
            using var dlg = new CascadeDeleteDialog(acct, dependents);
            dlg.ShowDialog(this);
            switch (dlg.Choice)
            {
                case CascadeDeleteChoice.Cancel:
                    return;
                case CascadeDeleteChoice.Unlink:
                    foreach (var c in dependents)
                    {
                        c.AccountUsername = "";
                        c.AccountServer = "";
                    }
                    _pendingAccounts.RemoveAt(idx);
                    ClearStaleTeamSlots(acct.Name);
                    break;
                case CascadeDeleteChoice.DeleteAll:
                    foreach (var c in dependents)
                    {
                        _pendingCharacters.Remove(c);
                        ClearStaleTeamSlots(c.Name);
                    }
                    _pendingAccounts.RemoveAt(idx);
                    ClearStaleTeamSlots(acct.Name);
                    break;
            }
        }
        RefreshAccountsGrid();
        RefreshCharactersGrid();
        _lblTeamSummary.Text = BuildTeamSummary();
    }

    private void OnAddCharacter()
    {
        using var dlg = new CharacterEditDialog(null, _pendingAccounts, _pendingCharacters);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            _pendingCharacters.Add(dlg.Result);
            RefreshCharactersGrid();
            _lblTeamSummary.Text = BuildTeamSummary();
        }
    }

    private void OnEditCharacter(int idx)
    {
        if (idx < 0 || idx >= _pendingCharacters.Count) return;
        var existing = _pendingCharacters[idx];
        using var dlg = new CharacterEditDialog(existing, _pendingAccounts, _pendingCharacters);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            var oldName = existing.Name;
            var newName = dlg.Result.Name;
            _pendingCharacters[idx] = dlg.Result;
            if (!oldName.Equals(newName, StringComparison.Ordinal))
            {
                UpdateTeamSlotUsername(oldName, newName);
                PropagateNameChangeToQuickLogins(oldName, newName);
            }
            RefreshCharactersGrid();
            _lblTeamSummary.Text = BuildTeamSummary();
        }
    }

    /// <summary>
    /// When an Account or Character is renamed, update any QuickLogin1-4 combo
    /// whose current selection matches the old name so the binding doesn't silently
    /// drop to (None) when RefreshQuickLoginCombos rebuilds the item list.
    /// </summary>
    private void PropagateNameChangeToQuickLogins(string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName)) return;
        var combos = new[] { _cboQuickLogin1, _cboQuickLogin2, _cboQuickLogin3, _cboQuickLogin4 };
        foreach (var cbo in combos)
        {
            if (cbo == null) continue;
            if (cbo.SelectedItem?.ToString()?.Equals(oldName, StringComparison.Ordinal) == true)
            {
                // Intentionally mutate the item text before RefreshQuickLoginCombos
                // replaces the list — this keeps the selection visually stable.
                int si = cbo.SelectedIndex;
                if (si >= 0 && si < cbo.Items.Count)
                    cbo.Items[si] = newName;
                cbo.SelectedIndex = si;
            }
        }
    }

    private void OnDeleteCharacter(int idx)
    {
        if (idx < 0 || idx >= _pendingCharacters.Count) return;
        var c = _pendingCharacters[idx];
        if (MessageBox.Show($"Delete Character '{c.Name}'?", "Delete Character",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _pendingCharacters.RemoveAt(idx);
        ClearStaleTeamSlots(c.Name);
        RefreshCharactersGrid();
        _lblTeamSummary.Text = BuildTeamSummary();
    }

    private string BuildTeamSummary()
    {
        string Resolve(string targetName)
        {
            if (string.IsNullOrEmpty(targetName)) return "";
            var ch = _pendingCharacters.FirstOrDefault(c => c.Name.Equals(targetName, StringComparison.Ordinal));
            if (ch != null) return ch.Name;
            var ac = _pendingAccounts.FirstOrDefault(a => a.Name.Equals(targetName, StringComparison.Ordinal));
            return ac != null ? ac.Name : targetName + "?";   // trailing '?' flags unresolved
        }

        string Fmt(string u1, string u2)
        {
            var names = new[] { u1, u2 }
                .Select(Resolve)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            return names.Count > 0 ? string.Join(" + ", names) : "(none)";
        }
        var t1 = $"T1: {Fmt(_pendingTeam1A, _pendingTeam1B)}";
        var t2 = $"T2: {Fmt(_pendingTeam2A, _pendingTeam2B)}";
        var t3 = $"T3: {Fmt(_pendingTeam3A, _pendingTeam3B)}";
        var t4 = $"T4: {Fmt(_pendingTeam4A, _pendingTeam4B)}";
        return $"{t1}  |  {t2}\n{t3}  |  {t4}";
    }

    private void ShowTeamsDialog()
    {
        using var dlg = new AutoLoginTeamsDialog(
            _pendingAccounts,
            _pendingCharacters,
            _pendingTeam1A, _pendingTeam1B,
            _pendingTeam2A, _pendingTeam2B,
            _pendingTeam3A, _pendingTeam3B,
            _pendingTeam4A, _pendingTeam4B,
            _pendingTeam1AutoEnter, _pendingTeam2AutoEnter,
            _pendingTeam3AutoEnter, _pendingTeam4AutoEnter);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _pendingTeam1A = dlg.Team1Account1;
            _pendingTeam1B = dlg.Team1Account2;
            _pendingTeam2A = dlg.Team2Account1;
            _pendingTeam2B = dlg.Team2Account2;
            _pendingTeam3A = dlg.Team3Account1;
            _pendingTeam3B = dlg.Team3Account2;
            _pendingTeam4A = dlg.Team4Account1;
            _pendingTeam4B = dlg.Team4Account2;
            _pendingTeam1AutoEnter = dlg.Team1AutoEnter;
            _pendingTeam2AutoEnter = dlg.Team2AutoEnter;
            _pendingTeam3AutoEnter = dlg.Team3AutoEnter;
            _pendingTeam4AutoEnter = dlg.Team4AutoEnter;
            _lblTeamSummary.Text = BuildTeamSummary();
        }
    }

    private void ClearStaleTeamSlots(string username)
    {
        bool changed = false;
        if (_pendingTeam1A == username) { _pendingTeam1A = ""; changed = true; }
        if (_pendingTeam1B == username) { _pendingTeam1B = ""; changed = true; }
        if (_pendingTeam2A == username) { _pendingTeam2A = ""; changed = true; }
        if (_pendingTeam2B == username) { _pendingTeam2B = ""; changed = true; }
        if (_pendingTeam3A == username) { _pendingTeam3A = ""; changed = true; }
        if (_pendingTeam3B == username) { _pendingTeam3B = ""; changed = true; }
        if (_pendingTeam4A == username) { _pendingTeam4A = ""; changed = true; }
        if (_pendingTeam4B == username) { _pendingTeam4B = ""; changed = true; }
        if (changed) _lblTeamSummary.Text = BuildTeamSummary();
    }

    private void UpdateTeamSlotUsername(string oldUsername, string newUsername)
    {
        bool changed = false;
        if (_pendingTeam1A == oldUsername) { _pendingTeam1A = newUsername; changed = true; }
        if (_pendingTeam1B == oldUsername) { _pendingTeam1B = newUsername; changed = true; }
        if (_pendingTeam2A == oldUsername) { _pendingTeam2A = newUsername; changed = true; }
        if (_pendingTeam2B == oldUsername) { _pendingTeam2B = newUsername; changed = true; }
        if (_pendingTeam3A == oldUsername) { _pendingTeam3A = newUsername; changed = true; }
        if (_pendingTeam3B == oldUsername) { _pendingTeam3B = newUsername; changed = true; }
        if (_pendingTeam4A == oldUsername) { _pendingTeam4A = newUsername; changed = true; }
        if (_pendingTeam4B == oldUsername) { _pendingTeam4B = newUsername; changed = true; }
        if (changed) _lblTeamSummary.Text = BuildTeamSummary();
    }

    // ─── Account Export/Import ────────────────────────────────────────

    private void ExportAccounts()
    {
        if (_pendingAccounts.Count == 0)
        {
            MessageBox.Show("No accounts to export.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Export Accounts",
            Filter = "JSON files (*.json)|*.json",
            FileName = "eqswitch-accounts.json",
            DefaultExt = "json"
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_pendingAccounts,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            MessageBox.Show($"Exported {_pendingAccounts.Count} account(s).\n\nPasswords are DPAPI-encrypted — this file only works on the same Windows user account.",
                "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportAccounts()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import Accounts",
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = "json"
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // Phase 4: import Account-typed JSON. Legacy v3 LoginAccount exports with
        // CharacterName/AutoEnterWorld fields still deserialize correctly — those fields
        // simply aren't bound here (they'd live on Character in v4). Users re-create
        // Characters in-app after import.
        List<Account> imported;
        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            imported = System.Text.Json.JsonSerializer.Deserialize<List<Account>>(json, opts) ?? new();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (imported.Count == 0)
        {
            MessageBox.Show("No accounts found in file.", "Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // DPAPI scope is CurrentUser on this machine. If the export came from a different
        // Windows account, every EncryptedPassword blob is dead. Probe-decrypt the first
        // non-empty blob; on CryptographicException, warn before proceeding so the user
        // knows they'll need to re-enter passwords.
        var probe = imported.FirstOrDefault(a => !string.IsNullOrEmpty(a.EncryptedPassword));
        if (probe != null)
        {
            try
            {
                CredentialManager.Decrypt(probe.EncryptedPassword);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                var choice = MessageBox.Show(
                    "These accounts were encrypted on a different Windows user account (or a different machine). "
                  + "The stored passwords cannot be decrypted here — you'll need to re-enter each password after import.\n\n"
                  + "Import the accounts anyway (without working passwords)?",
                    "Cross-User Import", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (choice != DialogResult.Yes) return;
            }
            catch (FormatException)
            {
                var choice = MessageBox.Show(
                    "The stored password blob is not valid Base64 — the export file may be corrupted.\n\n"
                  + "Import the accounts anyway (without working passwords)?",
                    "Corrupted Password Blob", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (choice != DialogResult.Yes) return;
            }
        }

        // Dedup by (Username, Server) — case-insensitive.
        int added = 0, skipped = 0;
        foreach (var account in imported)
        {
            if (string.IsNullOrEmpty(account.Username))
            {
                skipped++;
                continue;
            }
            bool exists = _pendingAccounts.Any(a =>
                a.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase) &&
                a.Server.Equals(account.Server, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                skipped++;
                continue;
            }
            if (string.IsNullOrEmpty(account.Server)) account.Server = "Dalaya";
            if (string.IsNullOrEmpty(account.Name)) account.Name = account.Username;
            _pendingAccounts.Add(account);
            added++;
        }

        RefreshAccountsGrid();
        RefreshCharactersGrid();

        var msg = $"Imported {added} account(s).";
        if (skipped > 0) msg += $"\nSkipped {skipped} duplicate(s).";
        msg += "\n\nClick Apply to save changes.";
        MessageBox.Show(msg, "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ─── Video Tab (eqclient.ini) ───────────────────────────────────

    private TabPage BuildVideoTab()
    {
        var page = DarkTheme.MakeTabPage("Video");
        int y = 8;
        const int L = 10;

        // ─── Resolution card ──────────────────────────────────────
        var cardRes = DarkTheme.MakeCard(page, "📺", "EQ Resolution", DarkTheme.CardPurple, 10, y, 480, 96);
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

        var btnOffsets = DarkTheme.MakeButton("📐 Offsets...", DarkTheme.BgInput, 370, cy - 2);
        btnOffsets.Size = new Size(95, 24);
        btnOffsets.Font = DarkTheme.FontUI85;
        cardRes.Controls.Add(btnOffsets);
        btnOffsets.Click += (_, _) => ShowOffsetsDialog();

        cy += 30;
        DarkTheme.AddLabel(cardRes, "Width:", L, cy + 2);
        _nudVideoWidth = DarkTheme.AddNumeric(cardRes, 80, cy, 70, 1920, 320, 7680);
        _nudVideoWidth.ValueChanged += (_, _) => SyncVideoPresetToCustom();

        DarkTheme.AddLabel(cardRes, "Height:", 160, cy + 2);
        _nudVideoHeight = DarkTheme.AddNumeric(cardRes, 210, cy, 70, 1080, 200, 4320);
        _nudVideoHeight.ValueChanged += (_, _) => SyncVideoPresetToCustom();

        // Offset controls live in a popup dialog behind this button
        _nudVideoOffsetX = new NumericUpDown { Minimum = -5000, Maximum = 5000, Value = 0 };
        _nudVideoOffsetY = new NumericUpDown { Minimum = -5000, Maximum = 5000, Value = 0 };
        _nudVideoTopOffset = new NumericUpDown { Minimum = -100, Maximum = 200, Value = _config.Layout.TopOffset };

        var btnReset = DarkTheme.AddCardButton(cardRes, "🔄 Reset", 370, cy, 95);
        btnReset.Click += (_, _) => VideoResetDefaults();

        y += 104;

        // ─── Monitor card ─────────────────────────────────────────
        var cardMon = DarkTheme.MakeCard(page, "🖥", "Monitor Selection", DarkTheme.CardBlue, 10, y, 480, 128);
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

        cy += 36;
        var btnIdentify = DarkTheme.AddCardButton(cardMon, "🔍 Identify", L, cy - 3, 90);
        btnIdentify.Click += (_, _) => ShowMonitorIdentifiers();
        DarkTheme.AddCardHint(cardMon, "Primary = active client. Secondary = background client (multimonitor mode).", 110, cy);

        y += 136;

        // ─── Window Style card ───────────────────────────────────
        var cardStyle = DarkTheme.MakeCard(page, "🪟", "Window Style", DarkTheme.CardPurple, 10, y, 480, 112);
        cy = 32;

        const int hintX = 260;

        var btnWrapper = DarkTheme.MakeButton("⚙ Advanced...", DarkTheme.BgInput, 385, 5);
        btnWrapper.Size = new Size(80, 24);
        btnWrapper.Font = DarkTheme.FontUI85;
        cardStyle.Controls.Add(btnWrapper);

        _chkVideoWindowed = DarkTheme.AddCardCheckBox(cardStyle, "Windowed Mode", L, cy);
        DarkTheme.AddCardHint(cardStyle, "Required for window switching", hintX, cy + 2);
        cy += 26;

        _chkSlimTitlebar = DarkTheme.AddCardCheckBox(cardStyle, "Fullscreen Window (WinEQ2 mode)", L, cy);
        DarkTheme.AddCardHint(cardStyle, "Sets res + hides titlebar", hintX, cy + 2);
        cy += 26;

        _chkMaximizeWindow = DarkTheme.AddCardCheckBox(cardStyle, "Maximize on Launch", L, cy);
        DarkTheme.AddCardHint(cardStyle, "Sets Maximized=1 in eqclient.ini", hintX, cy + 2);
        cy += 22;

        _lblStyleDisabledHint = new Label
        {
            Text = "",
            Location = new Point(L, cy),
            AutoSize = true,
            ForeColor = DarkTheme.FgWarn,
            Font = DarkTheme.FontUI75,
            Visible = false,
        };
        cardStyle.Controls.Add(_lblStyleDisabledHint);

        // Unified style hint — shows conflict warning or general hint as needed
        void UpdateStyleHint()
        {
            if (_chkSlimTitlebar.Checked && !_chkVideoWindowed.Checked)
            {
                _lblStyleDisabledHint.Text = "⚠ Fullscreen Window requires Windowed Mode to be enabled";
                _lblStyleDisabledHint.ForeColor = DarkTheme.FgWarn;
                _lblStyleDisabledHint.Visible = true;
            }
            else if (!_chkSlimTitlebar.Checked && !_chkMaximizeWindow.Checked)
            {
                _lblStyleDisabledHint.Text = "If disabled, set EQ video resolution to fit above the taskbar";
                _lblStyleDisabledHint.ForeColor = DarkTheme.FgDimGray;
                _lblStyleDisabledHint.Visible = true;
            }
            else
            {
                _lblStyleDisabledHint.Visible = false;
            }
        }
        _chkVideoWindowed.CheckedChanged += (_, _) => UpdateStyleHint();

        // Wrapper dialog — titlebar offset, bottom margin, DLL hook
        // These are advanced settings that most users don't need to touch.
        _nudTitlebarOffset = new NumericUpDown { Value = 22, Minimum = 0, Maximum = 40 };
        _nudBottomOffset = new NumericUpDown { Value = 22, Minimum = 0, Maximum = 100 };
        _chkUseHook = new CheckBox();
        btnWrapper.Click += (_, _) => ShowWrapperDialog();

        _chkSlimTitlebar.CheckedChanged += (_, _) =>
        {
            bool slim = _chkSlimTitlebar.Checked;
            btnWrapper.Enabled = slim;
            _chkUseHook.Enabled = slim;
            if (slim)
            {
                _chkUseHook.Checked = true;  // DLL hook recommended with Fullscreen Window
                _chkMaximizeWindow.Checked = false;
                _chkMaximizeWindow.Enabled = false;
            }
            else
            {
                _chkMaximizeWindow.Enabled = true;
            }
            UpdateStyleHint();
        };

        _chkMaximizeWindow.CheckedChanged += (_, _) => UpdateStyleHint();

        y += 120;

        // ─── Preferences card ────────────────────────────────────
        var cardPrefs = DarkTheme.MakeCard(page, "⚙", "Preferences", DarkTheme.CardCyan, 10, y, 480, 68);
        cy = 32;
        DarkTheme.AddCardLabel(cardPrefs, "Tooltip Delay:", L, cy);
        _nudTooltipDuration = DarkTheme.AddCardNumeric(cardPrefs, 110, cy, 55, 1000, 0, 10000);
        _nudTooltipDuration.Increment = 100;
        DarkTheme.AddCardHint(cardPrefs, "ms", 175, cy);
        DarkTheme.AddCardLabel(cardPrefs, "Client Launch Delay:", 240, cy);
        _nudLaunchDelay = DarkTheme.AddCardNumeric(cardPrefs, 360, cy, 40, 3, 1, 30);
        DarkTheme.AddCardHint(cardPrefs, "sec", 410, cy);

        return page;
    }

    private void ShowOffsetsDialog()
    {
        using var dlg = new Form
        {
            Text = "Window Offsets",
            Size = new Size(300, 240),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        DarkTheme.StyleForm(dlg, dlg.Text, dlg.Size);

        int y = 15;
        const int L = 15, I = 120;

        DarkTheme.AddLabel(dlg, "Offset X:", L, y + 2);
        var nudX = DarkTheme.AddNumeric(dlg, I, y, 70, _nudVideoOffsetX.Value, -5000, 5000);
        DarkTheme.AddHint(dlg, "px from left edge", 200, y + 4);
        y += 28;

        DarkTheme.AddLabel(dlg, "Offset Y:", L, y + 2);
        var nudY = DarkTheme.AddNumeric(dlg, I, y, 70, _nudVideoOffsetY.Value, -5000, 5000);
        DarkTheme.AddHint(dlg, "px from top edge", 200, y + 4);
        y += 28;

        DarkTheme.AddLabel(dlg, "Top Offset:", L, y + 2);
        var nudTop = DarkTheme.AddNumeric(dlg, I, y, 70, _nudVideoTopOffset.Value, -100, 200);
        DarkTheme.AddHint(dlg, "px down from top", 200, y + 4);
        y += 32;

        DarkTheme.AddHint(dlg, "Most users can leave these at 0.", L, y);
        y += 16;
        DarkTheme.AddHint(dlg, "Saves to eqclient.ini on Apply. Requires EQ restart.", L, y);
        y += 22;

        var btnOK = DarkTheme.MakePrimaryButton("Save", L, y);
        btnOK.Width = 90;
        btnOK.Click += (_, _) =>
        {
            _nudVideoOffsetX.Value = nudX.Value;
            _nudVideoOffsetY.Value = nudY.Value;
            _nudVideoTopOffset.Value = nudTop.Value;
            dlg.DialogResult = DialogResult.OK;
        };
        dlg.Controls.Add(btnOK);

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, L + 100, y);
        btnCancel.Width = 90;
        btnCancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
        dlg.Controls.Add(btnCancel);

        dlg.ShowDialog(this);
    }

    private void ShowWrapperDialog()
    {
        using var dlg = new Form
        {
            Text = "Wrapper Settings",
            Size = new Size(340, 280),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        DarkTheme.StyleForm(dlg, dlg.Text, dlg.Size);

        int y = 15;
        const int L = 15, I = 160;

        DarkTheme.AddLabel(dlg, "Titlebar hidden (px):", L, y + 2);
        var nudTitle = DarkTheme.AddNumeric(dlg, I, y, 60, _nudTitlebarOffset.Value, 0, 40);
        y += 24;
        DarkTheme.AddHint(dlg, "22 = thin strip, 30 = fully hidden", L, y);
        y += 18;

        DarkTheme.AddLabel(dlg, "Bottom margin (px):", L, y + 2);
        var nudBottom = DarkTheme.AddNumeric(dlg, I, y, 60, _nudBottomOffset.Value, 0, 100);
        y += 24;
        DarkTheme.AddHint(dlg, "Game render height reduction", L, y);
        y += 18;

        DarkTheme.AddHint(dlg, "Defaults: titlebar 13, margin 21. Keep margin > titlebar.", L, y);
        y += 22;

        var chkHook = DarkTheme.AddCheckBox(dlg, "DLL Hook (zero flicker)", L, y);
        chkHook.Checked = _chkUseHook.Checked;
        y += 20;
        DarkTheme.AddHint(dlg, "Hooks SetWindowPos inside EQ", L, y);
        y += 28;

        var btnReset = DarkTheme.MakeButton("Reset Defaults", DarkTheme.BgMedium, L, y);
        btnReset.Width = 110;
        btnReset.Click += (_, _) =>
        {
            nudTitle.Value = 13;
            nudBottom.Value = 21;
            chkHook.Checked = true;
        };
        dlg.Controls.Add(btnReset);
        y += 36;

        var btnOK = DarkTheme.MakePrimaryButton("Save", L, y);
        btnOK.Width = 90;
        btnOK.Click += (_, _) =>
        {
            _nudTitlebarOffset.Value = nudTitle.Value;
            _nudBottomOffset.Value = nudBottom.Value;
            _chkUseHook.Checked = chkHook.Checked;
            dlg.DialogResult = DialogResult.OK;
        };
        dlg.Controls.Add(btnOK);

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 120, y);
        btnCancel.Width = 90;
        btnCancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
        dlg.Controls.Add(btnCancel);

        dlg.AcceptButton = (IButtonControl)btnOK;
        dlg.CancelButton = (IButtonControl)btnCancel;
        dlg.ShowDialog(this);
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

    // ─── Tray Action Display ↔ Config Mapping ───────────────────

    private static readonly Dictionary<string, string> _trayActionDisplayMap = new()
    {
        ["LoginAll"] = "AutoLoginTeam1",
        ["LoginAll2"] = "AutoLoginTeam2",
        ["LoginAll3"] = "AutoLoginTeam3",
        ["LoginAll4"] = "AutoLoginTeam4"
    };

    private static readonly Dictionary<string, string> _trayDisplayActionMap = new()
    {
        ["AutoLoginTeam1"] = "LoginAll",
        ["AutoLoginTeam2"] = "LoginAll2",
        ["AutoLoginTeam3"] = "LoginAll3",
        ["AutoLoginTeam4"] = "LoginAll4"
    };

    /// <summary>Convert config action name to dropdown display name.</summary>
    private static string TrayActionToDisplay(string action) =>
        _trayActionDisplayMap.TryGetValue(action, out var display) ? display : action;

    /// <summary>Convert dropdown display name back to config action name.</summary>
    private static string TrayDisplayToAction(string display) =>
        _trayDisplayActionMap.TryGetValue(display, out var action) ? action : display;
}
