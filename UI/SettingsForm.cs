// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Settings GUI with tabbed layout. Dark medieval purple theme with card panels.
/// Each section uses emoji-titled card panels for visual clarity and grouping.
/// </summary>
public class SettingsForm : EqSwitchForm
{
    private readonly AppConfig _config;
    private readonly Action<AppConfig> _onApply;
    private readonly Action? _onVideoSaved;
    private readonly Action? _openProcessManager;
    private readonly AutoLoginManager? _autoLogin;
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
    private TextBox _txtShowMenuGeneral = null!;
    private TextBox _txtShowMenu = null!;
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
    // v3.22.80: the new user-facing "Windowed Mode" (WS_CAPTION slim) toggle.
    // Disabled in Phase 1 — behavior lands in Phase 2.
    private CheckBox _chkWindowedMode = null!;
    // v3.22.53: dark immersive titlebar (DWMWA_USE_IMMERSIVE_DARK_MODE).
    // v3.22.54: promoted from the Wrapper Settings dialog to the main Video
    // tab Window Style card so the toggle is discoverable rather than buried
    // two clicks deep. Still only meaningful when SlimTitlebar exposes caption
    // pixels.
    // v3.22.56: default flipped to ON (Nate's 2026-05-26 visual review after
    // the v3.22.54 promotion — "make dark titlebar enabled by default, it
    // looks good"). Users who saved Settings on v3.22.53+ keep their explicit
    // value via STJ deserialization; users without the JSON key (pre-v3.22.53
    // or never-opened-Settings cohort) silently adopt the new ON default on
    // next launch (intended behavior — directive was "default ON"). See
    // AppConfig.WindowLayout.DarkTitlebar for the full migration story.
    private CheckBox _chkDarkTitlebar = null!;
    private NumericUpDown _nudTitlebarOffset = null!;
    private NumericUpDown _nudBottomOffset = null!;
    private CheckBox _chkUseHook = null!;
    private CheckBox _chkMaximizeWindow = null!;
    private TextBox _txtWindowTitleTemplate = null!;


    // ─── Launch tab controls
    private NumericUpDown _nudLaunchDelay = null!;

    // ─── Paths tab controls
    private TextBox _txtGinaPath = null!;
    private TextBox _txtGamparsePath = null!;
    private TextBox _txtEqLogParserPath = null!;
    private TextBox _txtNotesPath = null!;
    private TextBox _txtDalayaPatcherPath = null!;
    private CheckBox _chkRunAtStartup = null!;
    private CheckBox _chkShowTooltips = null!;

    // ─── Accounts tab controls (Phase 4: v4 Account + Character as first-class)
    private List<Account> _pendingAccounts = new();
    private List<Character> _pendingCharacters = new();

    // v3.22.78: OnSameNameCollision event + _lastNameCollisionHash field
    // removed. The "tray-menu clarity" balloon they powered is obsolete now
    // that the tray context menu colors Account-resolved rows orange and
    // Character-resolved rows blue — kind is structurally visible, not
    // ambiguous, even when an Account and a Character share names
    // case-insensitively. See DarkMenuRenderer.OnRenderItemText / Build*Submenu.

    private DataGridView _dgvAccounts = null!;
    private DataGridView _dgvCharacters = null!;
    private NumericUpDown _nudLoginScreenDelay = null!;
    // Phase 5a: _txtAutoLogin1-4 removed. Edited via AccountHotkeysDialog /
    // CharacterHotkeysDialog. Legacy HotkeyConfig.AutoLogin1-4 fields pass through
    // BuildAppConfig from _config during the v3.10.x deprecation window.
    private Panel _cardDirectBindings = null!;
    private Panel? _legacyBanner;   // rendered only when any QuickLoginN is populated and banner not dismissed
    // Currently-open hotkey dialog (Account/Character/Team). Non-modal Show()
    // means SettingsForm and the tray menu stay clickable while editing — but
    // we still want to forbid opening two of these at once (their conflict
    // checks are computed against a snapshot of _config at open time, so
    // double-open lets stale values shadow each other). Reusing the field
    // for any of the 3 dialogs is fine: they aren't simultaneously editable.
    private Form? _openHotkeyDialog;
    // Teams dialog tracked separately from _openHotkeyDialog because it's a
    // different surface (Configure Teams vs hotkey rebinding) and Nate wants
    // non-modal behavior so Settings stays interactable while it's open.
    private AutoLoginTeamsDialog? _openTeamsDialog;
    // v3.23.0: Quick Login slots. Typed char:/acct: targets (see QuickLoginSlot), staged
    // like the team slots — initialized from _config in LoadSettings, edited via the
    // QuickLoginSlotsDialog (opened from the Characters card), consumed by BuildAppConfig.
    // _lblQuickLoginReadout shows the live 1-4 assignments under the Tray Click Actions card.
    private QuickLoginSlotsDialog? _openQuickLoginDialog;
    private string _pendingQuickLogin1 = "";
    private string _pendingQuickLogin2 = "";
    private string _pendingQuickLogin3 = "";
    private string _pendingQuickLogin4 = "";
    private Label _lblQuickLoginReadout = null!;
    // Team login hotkeys: edited via TeamHotkeysDialog (opened from Direct
    // Bindings card on the Hotkeys tab). _pending* fields stage edits until
    // ApplySettings, mirroring the AccountHotkeys / CharacterHotkeys flow.
    private string _pendingTeamLogin1 = "";
    private string _pendingTeamLogin2 = "";
    private string _pendingTeamLogin3 = "";
    private string _pendingTeamLogin4 = "";
    private TeamSummaryLabel _lblTeamSummary = null!;
    private string _pendingTeam1A = "";
    private string _pendingTeam1B = "";
    private string _pendingTeam2A = "";
    private string _pendingTeam2B = "";
    private string _pendingTeam3A = "";
    private string _pendingTeam3B = "";
    private string _pendingTeam4A = "";
    private string _pendingTeam4B = "";
    private string _pendingTeam5A = "";
    private string _pendingTeam5B = "";
    private string _pendingTeam6A = "";
    private string _pendingTeam6B = "";
    private string _pendingTeam7A = "";
    private string _pendingTeam7B = "";
    private string _pendingTeam8A = "";
    private string _pendingTeam8B = "";
    private string _pendingTeam9A = "";
    private string _pendingTeam9B = "";
    private string _pendingTeam10A = "";
    private string _pendingTeam10B = "";
    private string _pendingTeam11A = "";
    private string _pendingTeam11B = "";
    private string _pendingTeam12A = "";
    private string _pendingTeam12B = "";
    // _pendingTeam{N}AutoEnter removed alongside the per-team Enter World toggle.
    // _chkAutoEnterWorld removed — see Phase 5b note in BuildAccountsTab.

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
    // v3.22.54: slim-mode horizontal nudge — the only one of the four
    // offsets that takes effect with Fullscreen Window (slim titlebar)
    // enabled. Maps to Layout.HorizontalNudgePx, clamped ±10.
    private NumericUpDown _nudHorizontalNudge = null!;
    private CheckBox _chkVideoMultiMon = null!;
    private ComboBox _cboVideoPrimaryMon = null!;
    private ComboBox _cboVideoSecondaryMon = null!;
    private bool _suppressVideoSync; // prevent SyncVideoPresetToCustom during programmatic changes
    private Label? _lblVideoLoadError; // warning label shown when ini load fails

    // Resolution presets for Video tab. EQSwitch runs each client at full-monitor
    // bounds (slim titlebar) and positions the windows itself, so these are plain
    // single-client resolutions — NOT tiling presets. v3.22.91 removed 4 WinEQ2-style
    // "half-screen multibox" presets (960x1080/1920x540/etc.): they only set EQ's
    // render resolution, don't arrange anything, and a half-width backbuffer actually
    // fights the default full-monitor positioning. The 2-on-one-monitor workflow they
    // implied was never tested, so offering them over-promised. Custom covers any
    // non-listed size for a power user who genuinely wants one.
    private static readonly (string Name, int W, int H)[] VideoPresets =
    {
        ("1920x1080", 1920, 1080),
        ("1920x1200", 1920, 1200),
        ("1920x1020 (above taskbar)", 1920, 1020),
        ("2560x1440", 2560, 1440),
        ("3840x2160", 3840, 2160),
        ("1280x720", 1280, 720),
        ("1600x900", 1600, 900),
        ("1366x768", 1366, 768),
        ("Custom", 0, 0)
    };




    private int _initialTab;
    private readonly bool _openTeamsOnShown;

    public SettingsForm(AppConfig config, Action<AppConfig> onApply, int initialTab = 0, Action? openProcessManager = null, Action? onVideoSaved = null, AutoLoginManager? autoLogin = null, bool openTeamsDialog = false)
    {
        _config = config;
        _onApply = onApply;
        _onVideoSaved = onVideoSaved;
        _openProcessManager = openProcessManager;
        _initialTab = initialTab;
        _autoLogin = autoLogin;
        _openTeamsOnShown = openTeamsDialog;
        InitializeForm();

        // v3.22.9: live-refresh the Accounts grid's Flag glyph + Last-Login tooltip
        // when AutoLoginManager finishes a login while Settings is open. Pre-v3.22.9
        // the user had to close+reopen Settings to pick up the result — the Accounts
        // grid renders from the _pendingAccounts deep-copy snapshot taken at form-
        // open (line ~228). FireLoginComplete already marshals to the UI sync
        // context, but OnLoginComplete re-checks InvokeRequired defensively in case
        // the synchronous-fallback path fires on a background thread.
        if (_autoLogin is { } al)
        {
            al.LoginComplete += OnLoginComplete;
            FormClosed += (_, _) => al.LoginComplete -= OnLoginComplete;
        }

        // v3.22.10: deep-link from tray "Manage Teams..." opens the Configure Teams
        // subwindow right after the form is shown. Fired once via Shown, then
        // unsubscribed so a later focus/visibility change can't double-open.
        //
        // Two-stage defer so Settings can fully paint AND be interactive for a
        // human-perceptible beat BEFORE the AutoLoginTeamsDialog ctor work runs.
        // The ctor builds 12 ComboBoxes with per-item TextRenderer.MeasureText
        // calls + DarkTheme layout-invalidation cascades — that block the UI
        // thread for ~3s on a real config. Single-UI-thread WinForms can't
        // truly parallelize the ctor work with Settings paint, so the choice
        // is: (a) freeze immediately and let Settings flash white (smoke
        // caught at v3.22.10 round 1), or (b) yield to Settings paint, give
        // the user a clear "Settings loaded, ready" beat, THEN start the
        // unavoidable freeze with a visible wait cursor so it reads as a
        // deliberate phase 2 rather than a glitch.
        //
        // BeginInvoke → drains the paint cycle queued by Show.
        // Timer 700ms → human-perceptible "loaded and interactive" beat.
        // UseWaitCursor=true → visible busy signal during Teams ctor freeze.
        if (_openTeamsOnShown)
        {
            void shownHandler(object? s, EventArgs e)
            {
                Shown -= shownHandler;
                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    var timer = new System.Windows.Forms.Timer { Interval = 700 };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        if (IsDisposed) return;
                        OpenTeamsWithVisibleBusy();
                    };
                    timer.Start();
                }));
            }
            Shown += shownHandler;
        }
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "\u2694  Dalaya Settings  \u2694", new Size(530, 604));

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
        // v3.22.26: lock around the form-open snapshot. Pre-fix, opening
        // Settings while an autologin SM was mid-write to LastLoginResult
        // could torn-read the value into _pendingAccounts. The ApplySettings
        // race-fix (line ~1843 reads `live!.LastLoginResult` back from the
        // live config under the same lock) means torn snapshots couldn't
        // persist a wrong value to disk — but the in-form Flag glyph showed
        // stale data for the duration of the dialog. Narrow display-only
        // race in practice; covered for completeness symmetry with the
        // ApplySettings/OnLoginComplete locked reads.
        //
        // v3.22.26 verifier round (T3 Sonnet + T3 Opus convergent): also
        // snapshot _pendingCharacters under the same lock. SM doesn't write
        // Character fields today, so this is latent — but a future SM
        // extension that writes Characters would miss this guard silently
        // and the gap-asymmetry-by-omission would be invisible. Cheap
        // belt-and-suspenders: one acquire covers both lists.
        List<Account> snapshotAccounts;
        List<Character> snapshotCharacters;
        lock (ConfigManager.ConfigMutationLock)
        {
            snapshotAccounts = _config.Accounts.Select(a => new Account
            {
                Name = a.Name,
                Username = a.Username,
                EncryptedPassword = a.EncryptedPassword,
                Server = a.Server,
                UseLoginFlag = a.UseLoginFlag,
                LastLoginResult = a.LastLoginResult,
                LastLoginAt = a.LastLoginAt,
                Notes = a.Notes,
            }).ToList();
            snapshotCharacters = _config.Characters.Select(c => new Character
            {
                Name = c.Name,
                AccountUsername = c.AccountUsername,
                AccountServer = c.AccountServer,
                CharacterSlot = c.CharacterSlot,
                DisplayLabel = c.DisplayLabel,
                ClassHint = c.ClassHint,
                Notes = c.Notes,
            }).ToList();
        }
        _pendingAccounts = snapshotAccounts;
        _pendingCharacters = snapshotCharacters;

        _pendingTeam1A  = _config.Team1Account1;
        _pendingTeam1B  = _config.Team1Account2;
        _pendingTeam2A  = _config.Team2Account1;
        _pendingTeam2B  = _config.Team2Account2;
        _pendingTeam3A  = _config.Team3Account1;
        _pendingTeam3B  = _config.Team3Account2;
        _pendingTeam4A  = _config.Team4Account1;
        _pendingTeam4B  = _config.Team4Account2;
        _pendingTeam5A  = _config.Team5Account1;
        _pendingTeam5B  = _config.Team5Account2;
        _pendingTeam6A  = _config.Team6Account1;
        _pendingTeam6B  = _config.Team6Account2;
        _pendingTeam7A  = _config.Team7Account1;
        _pendingTeam7B  = _config.Team7Account2;
        _pendingTeam8A  = _config.Team8Account1;
        _pendingTeam8B  = _config.Team8Account2;
        _pendingTeam9A  = _config.Team9Account1;
        _pendingTeam9B  = _config.Team9Account2;
        _pendingTeam10A = _config.Team10Account1;
        _pendingTeam10B = _config.Team10Account2;
        _pendingTeam11A = _config.Team11Account1;
        _pendingTeam11B = _config.Team11Account2;
        _pendingTeam12A = _config.Team12Account1;
        _pendingTeam12B = _config.Team12Account2;

        // v3.23.0: stage the four Quick Login slot targets (typed char:/acct: values).
        _pendingQuickLogin1 = _config.QuickLogin1;
        _pendingQuickLogin2 = _config.QuickLogin2;
        _pendingQuickLogin3 = _config.QuickLogin3;
        _pendingQuickLogin4 = _config.QuickLogin4;

        // Team{N}AutoEnter removed — kind alone dictates destination.

        tabs.TabPages.Add(BuildGeneralTab());      // 0
        tabs.TabPages.Add(BuildVideoTab());        // 1
        tabs.TabPages.Add(BuildAccountsTab());     // 2
        tabs.TabPages.Add(BuildPipTab());          // 3
        tabs.TabPages.Add(BuildHotkeysTab());      // 4
        tabs.TabPages.Add(BuildPathsTab());        // 5

        if (_initialTab > 0 && _initialTab < tabs.TabCount)
            tabs.SelectedIndex = _initialTab;

        // Button panel at bottom \u2014 two docked FlowLayoutPanels for uniform, DPI-correct
        // spacing. Replaces hand-placed absolute-x that drifted at scale: uneven gaps
        // (GitHub\u2194Update 5px vs Save\u2194Apply 10px) and the version label overrunning Save.
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            BackColor = DarkTheme.BgDark
        };

        // Right group \u2014 visual order Save | Apply | Cancel (RightToLeft flow)
        var actionFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(0, 10, 10, 0),
            BackColor = DarkTheme.BgDark
        };
        var btnSave = DarkTheme.MakePrimaryButton("Save", 0, 0);
        btnSave.Margin = new Padding(6, 0, 0, 0);
        btnSave.Click += (_, _) => { if (ApplySettings()) { ConfigManager.Save(_config); Close(); } };
        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 0, 0);
        btnApply.Margin = new Padding(6, 0, 0, 0);
        btnApply.Click += (_, _) => { if (ApplySettings()) { ConfigManager.Save(_config); } };
        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 0, 0);
        btnCancel.Margin = new Padding(6, 0, 0, 0);
        btnCancel.Click += (_, _) => Close();
        actionFlow.Controls.AddRange(new Control[] { btnCancel, btnApply, btnSave });

        // Left group \u2014 GitHub | Update | version (AutoSize buttons so emoji+text never clip)
        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(10, 10, 0, 0),
            BackColor = DarkTheme.BgDark
        };
        var btnGitHub = DarkTheme.MakeButton("\uD83C\uDF10 GitHub", DarkTheme.BgMedium, 0, 0);
        btnGitHub.AutoSize = true;
        btnGitHub.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        btnGitHub.Margin = new Padding(0, 0, 6, 0);
        btnGitHub.Click += (_, _) =>
        {
            try { using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/itsnateai/eqswitch") { UseShellExecute = true }); }
            catch (Exception ex) { FileLogger.Warn($"Failed to open GitHub URL: {ex.Message}"); }
        };
        var btnUpdate = DarkTheme.MakeButton("\u2B06 Update", DarkTheme.BgMedium, 0, 0);
        btnUpdate.AutoSize = true;
        btnUpdate.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        btnUpdate.Margin = new Padding(0, 0, 10, 0);
        btnUpdate.Click += (_, _) =>
        {
            using var dlg = new UpdateDialog();
            dlg.ShowDialog(this);
        };
        var lblVersion = new Label
        {
            Text = $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 0, 0),
            ForeColor = DarkTheme.FgDimGray,
            Font = TrackFont(new Font("Segoe UI", 8f))
        };
        leftFlow.Controls.AddRange(new Control[] { btnGitHub, btnUpdate, lblVersion });

        buttonPanel.Controls.Add(actionFlow);
        buttonPanel.Controls.Add(leftFlow);

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
        // v3.22.49: height bumped 118 → 148 to fit the new "Right click menu:" row
        // below Exe/Args. Field mirrors _txtShowMenu on the Hotkeys tab.
        // v3.22.54: bumped 148 → 156 to add 8 px padding between the Args
        // textbox and the Right click menu row (Nate screenshot 2026-05-26 —
        // the rows were sitting flush and read as a single group).
        var cardEQ = DarkTheme.MakeCard(page, "⚔", "EverQuest Setup", DarkTheme.CardGreen, 10, y, 480, 156);
        int cy = 30;

        // Switch key — prominent, right under card title
        _lblSwitchKey = DarkTheme.AddCardLabel(cardEQ, "EQ Switch Key:", L, cy);
        _lblSwitchKey.Font = TrackFont(new Font("Segoe UI Semibold", 8.5f));
        _txtSwitchKeyGeneral = new HotkeyTextBox
        {
            Location = new Point(I, cy - 2), Size = new Size(80, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle, Font = TrackFont(new Font("Consolas", 10f, FontStyle.Bold)),
            TextAlign = HorizontalAlignment.Center,
            ShortcutsEnabled = false,
            CaptureTab = true,
            // Mirror of _txtSwitchKey — both edit Hotkeys.SwitchKey (a bare-key hook binding).
            Tag = BareKeyTag
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
        DarkTheme.AddCardHint(cardEQ, "Click and press key  |  Delete to clear", 210, cy + 2);
        cy += R;

        // EQ Path — Browse-only (read-only) so a stray keystroke can't silently corrupt the
        // path. The folder picker only returns real directories, killing the typo class entirely.
        DarkTheme.AddCardLabel(cardEQ, "EQ Path:", L, cy);
        _txtEQPath = DarkTheme.AddCardTextBox(cardEQ, I, cy, IW);
        _txtEQPath.ReadOnly = true;
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
        // v3.22.54: extra 8 px padding before the Right click menu row so it
        // reads as a distinct section, not a continuation of the Exe/Args row.
        cy += R + 8;

        // v3.22.49: Right-click-menu hotkey. Pops the tray context menu above the
        // system clock without opening Start (bypasses the Win11 z-band issue
        // where Start covers the tray menu underneath it). Width 120 fits the
        // longest expected combo "Ctrl+Alt+Shift+E" in Consolas 9pt.
        DarkTheme.AddCardLabel(cardEQ, "Right click menu:", L, cy);
        _txtShowMenuGeneral = MakeHotkeyBox(cardEQ, I, cy - 2, 120);
        _txtShowMenuGeneral.TextChanged += (_, _) =>
        {
            if (_txtShowMenu != null && _txtShowMenu.Text != _txtShowMenuGeneral.Text)
                _txtShowMenu.Text = _txtShowMenuGeneral.Text;
        };
        DarkTheme.AddCardHint(cardEQ, "Pops menu above clock", 250, cy + 2);

        // v3.22.54: card-advance matched to card height (was 156 paired with
        // a 148 card; now 164 paired with the 156 card so the next card
        // doesn't crowd the Right click menu row).
        y += 164;

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
        // v3.22.53 post-round-6 fix (T2 Opus IMPORTANT): include
        // AutoLoginTeam5/6 in the dropdown to match the round-5 allowlist
        // widening. Without this, a hand-edited config binding Team 5/6 to a
        // tray click slot opens to a blank dropdown — the BuildAppConfig
        // fallback preserves the value but the user has no in-UI way to
        // change it. Completing the round-trip here means hand-edit + UI
        // both work.
        // Display strings. "Launch One"/"Launch Two" are display-only (stored values stay
        // "LaunchOne"/"LaunchAll" via the tray action maps) — "Launch Two" because the
        // action launches the configured client count, which is 2 by default. See the
        // _trayDisplayActionMap / _trayActionDisplayMap entries below.
        var clickActions = new[] { "None", "Auto-Login1", "AutoLoginTeam1", "TogglePiP", "Launch One", "Launch Two", "FixWindows", "SwapWindows", "Settings", "ShowHelp", "Auto-Login2", "Auto-Login3", "Auto-Login4", "AutoLoginTeam2", "AutoLoginTeam3", "AutoLoginTeam4", "AutoLoginTeam5", "AutoLoginTeam6" };
        const int cboW = 140;

        var cardTray = DarkTheme.MakeCard(page, "🖱", "Tray Click Actions", DarkTheme.CardBlue, 10, y, 480, 154);

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

        // v3.23.0: live readout of what each "AutoLogin 1-4" dropdown entry currently fires,
        // so the slots aren't mysterious. Assigned via the Quick Login button on the
        // Characters card; refreshed here (values already staged in InitializeForm) and on
        // dialog save.
        _lblQuickLoginReadout = DarkTheme.AddCardHint(cardTray, "", 12, 132);
        RefreshQuickLoginReadout();

        y += 162;

        // ─── Log Trim card ──────────────────────────────────────
        var cardLog = DarkTheme.MakeCard(page, "✂", "Log File Trimming", DarkTheme.CardCyan, 10, y, 480, 48);
        DarkTheme.AddCardLabel(cardLog, "Threshold:", 175, 10);
        _nudLogTrimThreshold = DarkTheme.AddCardNumeric(cardLog, 248, 8, 55, _config.LogTrimThresholdMB, 10, 500);
        _nudLogTrimThreshold.Increment = 10;
        DarkTheme.AddCardHint(cardLog, "MB", 308, 12);
        var btnTrimNow = DarkTheme.MakeButton("✂ Trim Now", DarkTheme.BgInput, 345, 7);
        btnTrimNow.Size = new Size(85, 26);
        btnTrimNow.Font = DarkTheme.FontUI85;
        cardLog.Controls.Add(btnTrimNow);
        btnTrimNow.Click += (_, _) => FileOperations.TrimLogFiles(_config, (int)_nudLogTrimThreshold.Value, msg => MessageBox.Show(msg, "Trim Logs", MessageBoxButtons.OK, MessageBoxIcon.Information));
        DarkTheme.AddCardHint(cardLog, "Async trim + archive old logs", 10, 30);

        y += 56;

        // ─── Window Title card ───────────────────────────────────
        // Height 40 (vs 56 for hinted cards): textbox bottom = 8+26 = 34, +6 padding.
        var cardTitle = DarkTheme.MakeCard(page, "\uD83D\uDCDD", "Window Title", DarkTheme.CardGreen, 10, y, 480, 40);
        _txtWindowTitleTemplate = DarkTheme.AddCardTextBox(cardTitle, 130, 8, 330, 100);

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

    // Team-hotkey conflict checks moved into TeamHotkeysDialog.OnSaveClicked
    // (matches AccountHotkeysDialog / CharacterHotkeysDialog). ApplySettings
    // still does a final all-up conflict scan across every family table +
    // tab-level hotkey before returning success.
    /// <summary>
    /// Phase 5a: repopulate the Direct Bindings card contents. Called on tab build and
    /// after either hotkey dialog saves. Renders two rows ("X/N bound" + Configure
    /// button for Accounts and Characters), an optional stale-binding summary row,
    /// and the legacy deprecation banner above the card if QuickLogin1-4 are
    /// populated and HotkeysLegacyBannerDismissed is false.
    /// </summary>
    private void RefreshDirectBindingsCard()
    {
        _cardDirectBindings.Controls.Clear();

        // v3.22.27 Item 1: snapshot _config reads under ConfigMutationLock to
        // protect against torn reads if SM ever gains Account/Character writes.
        // HotkeyBindingUtil.Count* methods iterate _config.* internally, so they
        // belong inside the lock too. Re-entrant on the ApplySettings call-path.
        int liveA, liveC, staleA, staleC, totalA, totalC;
        lock (ConfigManager.ConfigMutationLock)
        {
            liveA = HotkeyBindingUtil.CountLiveAccountBindings(_config);
            liveC = HotkeyBindingUtil.CountLiveCharacterBindings(_config);
            staleA = HotkeyBindingUtil.CountStaleAccountBindings(_config);
            staleC = HotkeyBindingUtil.CountStaleCharacterBindings(_config);
            totalA = _config.Accounts.Count;
            totalC = _config.Characters.Count;
        }

        // Header-less card — first row sits near the top of the panel.
        // Per-row +4/-1 offsets keep the label vertically centered with the
        // 26px Configure… button.
        int cy = 6;

        DarkTheme.AddCardLabel(_cardDirectBindings, "Accounts", 10, cy + 4);
        var lblAcctCount = DarkTheme.AddCardLabel(_cardDirectBindings, $"{liveA} / {totalA} bound", 100, cy + 4);
        lblAcctCount.ForeColor = DarkTheme.FgDimGray;
        var btnConfigureAccounts = DarkTheme.AddCardButton(_cardDirectBindings, "Configure\u2026", 350, cy - 1, 110);
        btnConfigureAccounts.Click += (_, _) => OpenAccountHotkeysDialog();
        cy += 28;

        DarkTheme.AddCardLabel(_cardDirectBindings, "Characters", 10, cy + 4);
        var lblCharCount = DarkTheme.AddCardLabel(_cardDirectBindings, $"{liveC} / {totalC} bound", 100, cy + 4);
        lblCharCount.ForeColor = DarkTheme.FgDimGray;
        var btnConfigureChars = DarkTheme.AddCardButton(_cardDirectBindings, "Configure\u2026", 350, cy - 1, 110);
        btnConfigureChars.Click += (_, _) => OpenCharacterHotkeysDialog();
        cy += 28;

        // Counts ONLY teams 1-4 by design — only Hotkeys.TeamLogin1-4 exist
        // as global hotkey slots. Teams 5-12 (added in v3.22.53 / v3.22.58)
        // have no hotkey binding and are tray-submenu-only, so "X / 4" is the
        // correct denominator for the hotkey-bound badge. Do NOT extend this
        // counter to 12 without also growing HotkeyConfig.TeamLoginN.
        int liveT = (string.IsNullOrEmpty(_pendingTeamLogin1) ? 0 : 1)
                  + (string.IsNullOrEmpty(_pendingTeamLogin2) ? 0 : 1)
                  + (string.IsNullOrEmpty(_pendingTeamLogin3) ? 0 : 1)
                  + (string.IsNullOrEmpty(_pendingTeamLogin4) ? 0 : 1);
        DarkTheme.AddCardLabel(_cardDirectBindings, "Teams", 10, cy + 4);
        var lblTeamCount = DarkTheme.AddCardLabel(_cardDirectBindings, $"{liveT} / 4 hotkey-bound", 100, cy + 4);
        lblTeamCount.ForeColor = DarkTheme.FgDimGray;
        var btnConfigureTeams = DarkTheme.AddCardButton(_cardDirectBindings, "Configure\u2026", 350, cy - 1, 110);
        btnConfigureTeams.Click += (_, _) => OpenTeamHotkeysDialog();
        cy += 28;

        if (staleA > 0 || staleC > 0)
        {
            var parts = new List<string>();
            if (staleA > 0) parts.Add($"{staleA} Account");
            if (staleC > 0) parts.Add($"{staleC} Character");
            var lblStale = DarkTheme.AddCardLabel(_cardDirectBindings,
                $"\u26A0 Stale bindings: {string.Join(" + ", parts)} \u2014 open Configure to review",
                10, cy + 4);
            lblStale.Size = new Size(460, 18);
            lblStale.ForeColor = DarkTheme.CardWarn;
            cy += 22;
        }

        DarkTheme.AddCardHint(_cardDirectBindings,
            "Bind a hotkey to any Account or Character. Ctrl+Alt+Letter style combos recommended.",
            10, cy + 6);

        RefreshLegacyBanner();
    }

    /// <summary>
    /// Phase 5a: render/remove the legacy "Quick Login slots moved" banner above
    /// the Direct Bindings card. Banner appears only when any QuickLoginN is
    /// populated AND HotkeysLegacyBannerDismissed is false. Dismiss click flips
    /// the bool + persists via ConfigManager.Save.
    /// </summary>
    private void RefreshLegacyBanner()
    {
        if (_legacyBanner != null && _legacyBanner.Parent != null)
        {
            _legacyBanner.Parent.Controls.Remove(_legacyBanner);
            _legacyBanner.Dispose();
            _legacyBanner = null;
        }

        // v3.23.0: only un-prefixed (pre-v3.23) bare-name slots count as "legacy". A typed
        // char:/acct: value set via the new Quick Login dialog is a current binding, not a
        // migration leftover, so it must NOT trigger the "slots moved" banner.
        static bool IsLegacyBare(string v) => QuickLoginSlot.Parse(v).Kind == QuickLoginSlot.Kind.LegacyBare;
        bool anyLegacy = IsLegacyBare(_config.QuickLogin1) || IsLegacyBare(_config.QuickLogin2)
                      || IsLegacyBare(_config.QuickLogin3) || IsLegacyBare(_config.QuickLogin4);
        if (!anyLegacy || _config.HotkeysLegacyBannerDismissed) return;

        var page = _cardDirectBindings.Parent;
        if (page == null) return;

        var banner = new Panel
        {
            Location = new Point(_cardDirectBindings.Location.X, _cardDirectBindings.Location.Y - 38),
            Size = new Size(_cardDirectBindings.Width, 34),
            BackColor = DarkTheme.BgMedium,
        };

        var lbl = DarkTheme.AddCardLabel(banner,
            "\u2139 Quick Login slots 1-4 moved to Direct Bindings. Legacy hotkeys still work until v3.11.0.",
            10, 10);
        lbl.Size = new Size(370, 18);

        var btnDismiss = DarkTheme.AddCardButton(banner, "Dismiss", 390, 5, 80);
        btnDismiss.Click += (_, _) =>
        {
            _config.HotkeysLegacyBannerDismissed = true;
            ConfigManager.Save(_config);
            RefreshLegacyBanner();
        };

        page.Controls.Add(banner);
        _legacyBanner = banner;
    }

    private void OpenAccountHotkeysDialog()
    {
        var others = new List<(string label, string combo)>
        {
            ("Fix Windows",      _txtArrangeWindows.Text.Trim()),
            ("Launch One",       _txtLaunchOne.Text.Trim()),
            ("Launch All",       _txtLaunchAll.Text.Trim()),
            ("Multi-Mon Toggle", _txtToggleMultiMon.Text.Trim()),
            ("PiP Toggle",       _txtTogglePip.Text.Trim()),
            ("Show Menu",        _txtShowMenuGeneral.Text.Trim()),
            ("Team 1",           _pendingTeamLogin1),
            ("Team 2",           _pendingTeamLogin2),
            ("Team 3",           _pendingTeamLogin3),
            ("Team 4",           _pendingTeamLogin4),
        };
        foreach (var b in _config.Hotkeys.CharacterHotkeys)
            if (HotkeyBindingUtil.IsPopulated(b))
                others.Add(($"Character '{b.TargetName}'", b.Combo));

        if (FocusExistingHotkeyDialog()) return;
        // v3.22.27 R1 (originally deferred to v3.22.28 in CHANGELOG, folded
        // back in per DO-over-DEFER directive): symmetric with the
        // CharacterHotkeysDialog wrap below. Dialog ctor reads Accounts to
        // build the initial row list. Same latent-only risk class.
        AccountHotkeysDialog dlg;
        lock (ConfigManager.ConfigMutationLock)
        {
            dlg = new AccountHotkeysDialog(_config.Accounts, _config.Hotkeys.AccountHotkeys, others);
        }
        dlg.FormClosed += (_, _) =>
        {
            if (dlg.DialogResult == DialogResult.OK && dlg.Result != null)
            {
                _config.Hotkeys.AccountHotkeys = dlg.Result;
                ConfigManager.Save(_config);
                RefreshDirectBindingsCard();
            }
            _openHotkeyDialog = null;
            dlg.Dispose();
        };
        _openHotkeyDialog = dlg;
        dlg.Show(this);
    }

    private void OpenCharacterHotkeysDialog()
    {
        var others = new List<(string label, string combo)>
        {
            ("Fix Windows",      _txtArrangeWindows.Text.Trim()),
            ("Launch One",       _txtLaunchOne.Text.Trim()),
            ("Launch All",       _txtLaunchAll.Text.Trim()),
            ("Multi-Mon Toggle", _txtToggleMultiMon.Text.Trim()),
            ("PiP Toggle",       _txtTogglePip.Text.Trim()),
            ("Show Menu",        _txtShowMenuGeneral.Text.Trim()),
            ("Team 1",           _pendingTeamLogin1),
            ("Team 2",           _pendingTeamLogin2),
            ("Team 3",           _pendingTeamLogin3),
            ("Team 4",           _pendingTeamLogin4),
        };
        foreach (var b in _config.Hotkeys.AccountHotkeys)
            if (HotkeyBindingUtil.IsPopulated(b))
                others.Add(($"Account '{b.TargetName}'", b.Combo));

        if (FocusExistingHotkeyDialog()) return;
        // v3.22.27 Item 1: lock around dialog construction so the dialog's
        // initial iteration of Characters (CharacterHotkeysDialog ctor reads
        // .Count + .Any) sees a consistent snapshot. Latent today (SM doesn't
        // write Characters); if SM ever gains Character writes the dialog's
        // stored _characters reference becomes the load-bearing concern and
        // this site must switch to passing a defensive copy.
        CharacterHotkeysDialog dlg;
        lock (ConfigManager.ConfigMutationLock)
        {
            dlg = new CharacterHotkeysDialog(_config.Characters, _config.Hotkeys.CharacterHotkeys, others);
        }
        dlg.FormClosed += (_, _) =>
        {
            if (dlg.DialogResult == DialogResult.OK && dlg.Result != null)
            {
                _config.Hotkeys.CharacterHotkeys = dlg.Result;
                ConfigManager.Save(_config);
                RefreshDirectBindingsCard();
            }
            _openHotkeyDialog = null;
            dlg.Dispose();
        };
        _openHotkeyDialog = dlg;
        dlg.Show(this);
    }

    private void OpenTeamHotkeysDialog()
    {
        var others = new List<(string label, string combo)>
        {
            ("Switch Key",       _txtSwitchKey.Text.Trim()),
            ("Global Switch Key",_txtGlobalSwitchKey.Text.Trim()),
            ("Fix Windows",      _txtArrangeWindows.Text.Trim()),
            ("Launch One",       _txtLaunchOne.Text.Trim()),
            ("Launch All",       _txtLaunchAll.Text.Trim()),
            ("Multi-Mon Toggle", _txtToggleMultiMon.Text.Trim()),
            ("PiP Toggle",       _txtTogglePip.Text.Trim()),
            ("Show Menu",        _txtShowMenuGeneral.Text.Trim()),
        };
        foreach (var b in _config.Hotkeys.AccountHotkeys)
            if (HotkeyBindingUtil.IsPopulated(b))
                others.Add(($"Account '{b.TargetName}'", b.Combo));
        foreach (var b in _config.Hotkeys.CharacterHotkeys)
            if (HotkeyBindingUtil.IsPopulated(b))
                others.Add(($"Character '{b.TargetName}'", b.Combo));

        if (FocusExistingHotkeyDialog()) return;

        // Build structured per-slot previews so the dialog can color-code each
        // name by kind (Character=blue, Account=orange) — matches the A/C pills
        // in the Accounts team-configure window. Resolution per slot:
        // Character.Name → IsCharacter=true; Account.Name → IsCharacter=false;
        // raw fallback → IsCharacter=null (rendered uncolored).
        (string Name, bool? IsCharacter)? ResolveSlot(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            // v3.24.15: route typed (char:/acct:) + legacy-bare values through the shared resolver so
            // this preview matches FireTeam's resolution (and an Account isn't shadowed by a same-name
            // Character). Staged (_pending*) lookups mirror the live path.
            var (ch, ac) = TeamSlotResolver.Resolve(raw,
                n => _pendingCharacters.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)),
                n => _pendingAccounts.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
            if (ch != null) return (ch.Name, true);
            // Resolve by Name (the FK), but display Username so the team-row preview label reads as
            // the actual login string the user knows.
            if (ac != null) return (ac.Username, false);
            return (QuickLoginSlot.DisplayName(raw), (bool?)null);
        }
        IReadOnlyList<(string Name, bool? IsCharacter)> PreviewSlots(string a, string b)
        {
            var slots = new List<(string Name, bool? IsCharacter)>();
            if (ResolveSlot(a) is { } r1) slots.Add(r1);
            if (ResolveSlot(b) is { } r2) slots.Add(r2);
            return slots;
        }
        var teamPreviews = new IReadOnlyList<(string Name, bool? IsCharacter)>[]
        {
            PreviewSlots(_pendingTeam1A, _pendingTeam1B),
            PreviewSlots(_pendingTeam2A, _pendingTeam2B),
            PreviewSlots(_pendingTeam3A, _pendingTeam3B),
            PreviewSlots(_pendingTeam4A, _pendingTeam4B),
        };

        var dlg = new TeamHotkeysDialog(
            _pendingTeamLogin1, _pendingTeamLogin2, _pendingTeamLogin3, _pendingTeamLogin4,
            teamPreviews,
            others);
        dlg.FormClosed += (_, _) =>
        {
            if (dlg.DialogResult == DialogResult.OK && dlg.Result is { } r)
            {
                _pendingTeamLogin1 = r.Team1;
                _pendingTeamLogin2 = r.Team2;
                _pendingTeamLogin3 = r.Team3;
                _pendingTeamLogin4 = r.Team4;
                RefreshDirectBindingsCard();
            }
            _openHotkeyDialog = null;
            dlg.Dispose();
        };
        _openHotkeyDialog = dlg;
        dlg.Show(this);
    }

    /// <summary>
    /// If a hotkey dialog is already up, bring it to front and return true so
    /// the caller skips creating a second one. Returns false if no dialog is
    /// currently open (caller proceeds normally).
    /// </summary>
    private bool FocusExistingHotkeyDialog()
    {
        if (_openHotkeyDialog == null || _openHotkeyDialog.IsDisposed) return false;
        _openHotkeyDialog.Activate();
        return true;
    }

    private TextBox MakeHotkeyBox(Panel card, int x, int y, int width = 80, bool allowBareKey = false)
    {
        var tb = new HotkeyTextBox
        {
            Location = new Point(x, y), Size = new Size(width, 20),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.None, Font = TrackFont(new Font("Consolas", 9f)),
            TextAlign = HorizontalAlignment.Center,
            ShortcutsEnabled = false,
            // Only the Switch Key boxes bind Tab — action boxes keep normal tab-navigation.
            CaptureTab = allowBareKey,
            // Switch-key boxes are consumed by the low-level keyboard hook, which is built
            // for single keys (\ ]). Mark them so HotkeyBoxKeyDown accepts a modifier-less
            // keypress; action boxes (RegisterHotKey, global scope) stay modifier-only.
            Tag = allowBareKey ? BareKeyTag : null
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
        _txtSwitchKey = MakeHotkeyBox(cardSwitch, I, cy - 2, allowBareKey: true);
        _txtSwitchKey.TextChanged += (_, _) =>
        {
            if (_txtSwitchKeyGeneral != null && _txtSwitchKeyGeneral.Text != _txtSwitchKey.Text)
                _txtSwitchKeyGeneral.Text = _txtSwitchKey.Text;
        };
        DarkTheme.AddCardLabel(cardSwitch, "Mode:", 250, cy);
        _cboSwitchKeyMode = DarkTheme.AddCardComboBox(cardSwitch, I2, cy, 130, new[] { "Swap Last Two", "Cycle All" });
        cy += R + 2;

        DarkTheme.AddCardLabel(cardSwitch, "Global Switch Key:", L, cy);
        _txtGlobalSwitchKey = MakeHotkeyBox(cardSwitch, I, cy - 2, allowBareKey: true);
        _lblDuplicateKeyWarn = DarkTheme.AddCardHint(cardSwitch, "Works from any app, cycles thru all", 250, cy + 2);

        // Show warning when both switch keys match
        _txtSwitchKey.TextChanged += (_, _) => CheckDuplicateSwitchKeys();
        _txtGlobalSwitchKey.TextChanged += (_, _) => CheckDuplicateSwitchKeys();
        cy += R + 2;

        y += 98;

        // ─── Right Click Menu card (v3.22.49) ────────────────────
        // Mirrors _txtShowMenuGeneral on the General tab. Sits above Actions
        // Launcher (per user request) so it's adjacent to Window Switching.
        var cardShowMenu = DarkTheme.MakeCard(page, "🗔", "Right Click Menu", DarkTheme.CardBlue, 10, y, 480, 60);
        DarkTheme.AddCardLabel(cardShowMenu, "Show Menu:", L, 32);
        _txtShowMenu = MakeHotkeyBox(cardShowMenu, I, 30, 120);
        _txtShowMenu.TextChanged += (_, _) =>
        {
            if (_txtShowMenuGeneral != null && _txtShowMenuGeneral.Text != _txtShowMenu.Text)
                _txtShowMenuGeneral.Text = _txtShowMenu.Text;
        };
        DarkTheme.AddCardHint(cardShowMenu, "Pops menu above clock", 280, 35);

        y += 68;

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

        // Phase 5a + Teams: legacy Quick Login slot combos + Auto-Login hotkey
        // TextBoxes + the inline Team Hotkeys card all collapsed into the
        // Direct Bindings card below, edited via AccountHotkeysDialog /
        // CharacterHotkeysDialog / TeamHotkeysDialog. QuickLogin1-4 and
        // HotkeyConfig.AutoLogin1-4 remain on AppConfig for v3.10.x back-compat;
        // Phase 6 deletes them.


        // ─── Direct Bindings (Account + Character hotkey families) ──
        // Header-less card: Accounts / Characters / Teams rows. Each row is
        // self-labelled with a Configure\u2026 button that opens its dialog. Height
        // 118 fits 3 rows (cy increments of 28) plus the hint with ~10px of
        // breathing room before the bottom border.
        _cardDirectBindings = DarkTheme.MakeCard(page, "", "",
            DarkTheme.CardGreen, 10, y, 480, 118);
        RefreshDirectBindingsCard();

        y += 126;

        // ─── Client Launch Delay (header-less, full-width) ───────
        // Moved from Video → Preferences. Header-less full-width card aligned
        // with the other sections' left border (x=10). Controls within stay
        // pushed to the right side of the card so the section reads as an aside.
        var cardLaunchDelay = DarkTheme.MakeCard(page, "", "",
            DarkTheme.CardCyan, 10, y, 480, 36);
        DarkTheme.AddCardLabel(cardLaunchDelay, "Client Launch Delay:", 280, 10);
        _nudLaunchDelay = DarkTheme.AddCardNumeric(cardLaunchDelay, 408, 8, 40, 3, 2, 30);
        DarkTheme.AddCardHint(cardLaunchDelay, "sec", 458, 10);

        y += 44;

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
            // SystemAware: the whole process uses the primary-monitor DPI. Scale the
            // badge box + text Y to match the point-based fonts (which auto-scale).
            double s = DeviceDpi / 96.0;
            var size = new Size((int)(160 * s), (int)(100 * s));
            var overlay = new Form
            {
                // NO AutoScale baseline here: this overlay has zero child controls and its
                // box Size, Region, and painted glyph are ALL scaled manually by `s` below.
                // Adding AutoScaleMode.Dpi would scale Form.Size a SECOND time (s × auto-scale
                // = double-scale) while Region/Paint scale once — the box would overshoot.
                FormBorderStyle = FormBorderStyle.None,
                BackColor = DarkTheme.BgDark,
                TopMost = true,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Size = size,
            };
            // Rounded region — eliminates the boxy look
            var radius = (int)(20 * s);
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
                    (size.Width - numSize.Width) / 2, (int)(12 * s));
                var labelText = screen.Primary ? "Primary" : $"Monitor";
                var labelSize = e.Graphics.MeasureString(labelText, labelFont);
                e.Graphics.DrawString(labelText, labelFont, dimBrush,
                    (size.Width - labelSize.Width) / 2, (int)(68 * s));
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
        var cardPaths = DarkTheme.MakeCard(page, "📁", "External Tools", DarkTheme.CardGold, 10, y, 480, 272);
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

        DarkTheme.AddCardLabel(cardPaths, "Gamparse:", L, cy);
        _txtGamparsePath = DarkTheme.AddCardTextBox(cardPaths, I, cy, IW);
        var btnBrowseGamparse = DarkTheme.AddCardButton(cardPaths, "Browse...", BRW, cy - 3, 75);
        btnBrowseGamparse.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select Gamparse executable",
                Filter = "Executables|*.exe|All Files|*.*",
                InitialDirectory = Path.GetDirectoryName(_txtGamparsePath.Text) ?? ""
            };
            if (ofd.ShowDialog() == DialogResult.OK) _txtGamparsePath.Text = ofd.FileName;
        };
        cy += R;

        DarkTheme.AddCardLabel(cardPaths, "EQLogParser:", L, cy);
        _txtEqLogParserPath = DarkTheme.AddCardTextBox(cardPaths, I, cy, IW);
        var btnBrowseEqLogParser = DarkTheme.AddCardButton(cardPaths, "Browse...", BRW, cy - 3, 75);
        btnBrowseEqLogParser.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select EQLogParser executable",
                Filter = "Executables|*.exe|All Files|*.*",
                InitialDirectory = Path.GetDirectoryName(_txtEqLogParserPath.Text) ?? ""
            };
            if (ofd.ShowDialog() == DialogResult.OK) _txtEqLogParserPath.Text = ofd.FileName;
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
        DarkTheme.AddCardHint(cardPaths, "Patcher may be deleted by antivirus — re-download from Dalaya if missing.", L, cy + 26);
        cy += 52;

        DarkTheme.AddCardLabel(cardPaths, "Custom Icon:", L, cy);
        _txtCustomIconPath = DarkTheme.AddCardTextBox(cardPaths, I, cy, IW);
        var btnBrowseIcon = DarkTheme.AddCardButton(cardPaths, "Browse...", BRW, cy - 3, 75);
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

        y += 280;

        // ─── Startup card ───────────────────────────────────────
        // Single row, original x=47 left padding preserved. Button on the left,
        // Run at Startup mid-right with breathing room. Show Tooltips toggle
        // moved to Video → Preferences (paired with Tooltip Delay).
        var cardStartup = DarkTheme.MakeCard(page, "🚀", "Startup", DarkTheme.CardGreen, 10, y, 480, 64);
        cy = 32;

        const string SHORTCUT_LABEL = "Create Desktop Shortcut";
        var btnShortcut = DarkTheme.AddCardButton(cardStartup, SHORTCUT_LABEL, 47, cy, 180);
        btnShortcut.Click += (_, _) =>
        {
            // Re-enable + label-restore are driven by the timer, NOT by the
            // CreateDesktopShortcut callback — so the button is guaranteed to
            // recover regardless of which branch (success / already-exists /
            // exception) fires inside StartupManager. Visible feedback is the
            // 2s "Created!" flash; the showBalloon delegate is intentionally
            // a no-op here (parity with prior behavior — the button text IS
            // the surface).
            btnShortcut.Enabled = false;
            btnShortcut.Text = "Created!";

            StartupManager.CreateDesktopShortcut(_ => { });

            var reset = new System.Windows.Forms.Timer { Interval = 2000 };
            reset.Tick += (__, ___) =>
            {
                reset.Stop(); reset.Dispose();
                btnShortcut.Text = SHORTCUT_LABEL;
                btnShortcut.Enabled = true;
            };
            reset.Start();
        };

        _chkRunAtStartup = DarkTheme.AddCardCheckBox(cardStartup, "Run at Startup", 320, cy + 5);

        y += 72;

        // ─── eqclient.ini actions card ─────────────────────────────
        // Taller card so the warning occupies its OWN row above the buttons — the
        // dynamic-length "not found" text would otherwise overlap Backup/Restore at any
        // scale (mirrors AutoLoginTeamsDialog's dedicated warning row).
        var cardIni = DarkTheme.MakeCard(page, "💾", "eqclient.ini", DarkTheme.CardGold, 10, y, 480, 96);

        _lblVideoLoadError = new Label
        {
            Text = "⚠ Failed to read eqclient.ini — values shown are defaults, not your settings.",
            Location = new Point(L, 28),
            Size = new Size(460, 18),
            ForeColor = DarkTheme.CardWarn,
            Font = TrackFont(new Font("Segoe UI", 7.5f, FontStyle.Bold)),
            Visible = false
        };
        cardIni.Controls.Add(_lblVideoLoadError);

        cy = 52;
        var btnBackup = DarkTheme.AddCardButton(cardIni, "📋 Backup", 47, cy, 110);
        btnBackup.Click += (_, _) => VideoBackupIni();

        var btnRestore = DarkTheme.AddCardButton(cardIni, "📂 Restore", 340, cy, 110);
        btnRestore.Click += (_, _) => VideoRestoreIni();

        y += 104;

        // ─── Help button ─────────────────────────────────────────
        var btnHelp = DarkTheme.MakeButton("❓ Help", DarkTheme.BgMedium, 10, y);
        btnHelp.Size = new Size(100, 30);
        btnHelp.Click += (_, _) => HelpForm.Show(_config);
        page.Controls.Add(btnHelp);

        // ─── Reset Defaults button (Nuclear) ────────────────────
        var btnReset = DarkTheme.MakeButton("⚠ Reset", DarkTheme.BgMedium, 195, y);
        btnReset.Size = new Size(100, 30);
        btnReset.ForeColor = DarkTheme.CardWarn;
        btnReset.Click += (_, _) =>
        {
            var result = MessageBox.Show(
                "⚠️ NUCLEAR OPTION ⚠️\n\n" +
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
                _reopenAfterClose = true;
                Close();
            }
        };
        page.Controls.Add(btnReset);

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

    /// <summary>Tag marker on hotkey TextBoxes that accept bare (modifier-less) keys.</summary>
    private const string BareKeyTag = "bareKeyOk";

    /// <summary>
    /// Maps a <see cref="Keys"/> code from a hotkey TextBox KeyDown to the canonical
    /// key-name string that <see cref="HotkeyManager.ResolveVK"/> (KeyNameToVK) resolves
    /// back to a VK. The two MUST round-trip: a display name the resolver can't parse
    /// registers as VK 0 and the hotkey silently never fires. Number-row keys arrive as
    /// Keys.D0–D9 ("D1") but the resolver wants "1"; Keys.Oemtilde is the backquote/tilde
    /// key ("`"); OemPipe/OemBackslash both mean backslash across keyboard layouts.
    /// Guarded by Core/HotkeyKeyNameTests (--test-hotkey-keyname).
    /// </summary>
    internal static string FormatHotkeyKeyName(Keys keyCode) => keyCode switch
    {
        Keys.OemPipe or Keys.OemBackslash => "\\",
        Keys.OemCloseBrackets => "]",
        Keys.OemOpenBrackets => "[",
        Keys.Oemtilde => "`",
        // OEM punctuation: emit the symbol the resolver understands, not the WinForms
        // enum name ("OemSemicolon" etc., which KeyNameToVK can't parse).
        Keys.OemSemicolon => ";",
        Keys.Oemplus => "=",
        Keys.Oemcomma => ",",
        Keys.OemMinus => "-",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.OemQuotes => "'",
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (keyCode - Keys.D0))).ToString(),
        // Arrows, nav, and F13-F24 round-trip via their plain ToString() name ("Left",
        // "Home", "F13") — KeyNameToVK now resolves those after .ToUpperInvariant().
        _ => keyCode.ToString()
    };

    /// <summary>The four arrow keys — blocked as a BARE switch key (binding one would swallow
    /// in-game movement while EQ is focused). Still allowed in modifier combos.</summary>
    internal static bool IsMovementKey(Keys keyCode) =>
        keyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down;

    /// <summary>
    /// Brief red background flash on a hotkey box to signal a refused keypress (unsupported key,
    /// bare key on a modifier-only box, or a blocked movement key) — instead of silently
    /// swallowing it. Reverts to the known input background (not a captured one) so rapid repeated
    /// rejects can't leave the box stuck on the warn color.
    /// </summary>
    internal static void FlashHotkeyReject(TextBox box)
        => (box as HotkeyTextBox)?.FlashReject(DarkTheme.CardWarn, DarkTheme.BgInput);

    /// <summary>
    /// Pure decision for a hotkey TextBox keypress (caller has already filtered out
    /// standalone modifiers and the Delete/Back/Escape clear-keys). Returns true and sets
    /// <paramref name="result"/> to the formatted binding ("Ctrl+\", "\", "]") when the
    /// keypress is acceptable; returns false (result = "") when it should be ignored.
    ///
    /// Bare (modifier-less) keys are accepted ONLY when <paramref name="allowBareKey"/> is
    /// true (the Switch Key boxes, consumed by the low-level keyboard hook) AND the key
    /// resolves to a real VK — a bare key the resolver can't parse would register as VK 0
    /// and silently never fire, so it is refused. Action boxes (allowBareKey = false) still
    /// require a modifier: they go through RegisterHotKey at global scope, where binding a
    /// bare key would swallow that key system-wide. Guarded by Core/HotkeyKeyNameTests.
    /// </summary>
    internal static bool TryBuildHotkeyString(
        bool allowBareKey, bool ctrl, bool alt, bool shift, Keys keyCode, out string result)
    {
        result = "";
        bool bare = !ctrl && !alt && !shift;

        if (bare && !allowBareKey) return false;

        // Per "no gameplay": a bare arrow key can't be a switch key — it would swallow in-game
        // movement while EQ is focused. Arrows remain fine in modifier combos.
        if (bare && IsMovementKey(keyCode)) return false;

        string keyName = FormatHotkeyKeyName(keyCode);
        // Refuse ANY key the resolver can't turn into a VK — bare OR modifier combo.
        // A combo like "Ctrl+<unresolvable>" would build a string that registers as
        // VK 0 and silently never fires; refusing at capture keeps the failure loud.
        if (HotkeyManager.ResolveVK(keyName) == 0) return false;

        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(keyName);
        result = string.Join("+", parts);
        return true;
    }

    private void HotkeyBoxKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;

        // Ignore standalone modifiers
        if (e.KeyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey)
            return;

        // Delete / Backspace / Escape all clear the field — Esc is the one users try first.
        if ((e.KeyCode is Keys.Delete or Keys.Back or Keys.Escape) && !e.Control && !e.Alt && !e.Shift)
        {
            if (sender is TextBox tb) tb.Text = "";
            return;
        }

        // Switch Key / Global Switch Key boxes opt into bare keys via Tag == BareKeyTag.
        bool allowBareKey = (sender as Control)?.Tag as string == BareKeyTag;

        // On a switch box, a numpad key pressed with NumLock off arrives as a nav key (numpad-8
        // -> Keys.Up). HotkeyTextBox flagged it as numpad-origin via the extended-key bit;
        // normalize it back to the NumPad key so it stores as "NumPad8" and fires under both
        // NumLock states (matching the runtime hook). Not done for action boxes — those register
        // via RegisterHotKey, which can't match the NumLock-translated VK.
        Keys keyCode = e.KeyCode;
        if (allowBareKey && sender is HotkeyTextBox htb && htb.LastKeyFromNumpad)
            keyCode = (Keys)KeyboardHookManager.NormalizeNumpadVk((uint)e.KeyCode, 0);

        if (TryBuildHotkeyString(allowBareKey, e.Control, e.Alt, e.Shift, keyCode, out string combo))
        {
            if (sender is TextBox box) box.Text = combo;
        }
        else if (sender is TextBox box)
        {
            // Refused (unsupported key, bare key on an action box, or a blocked movement key) —
            // give a visible cue instead of silently swallowing the keypress.
            FlashHotkeyReject(box);
        }
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
        _txtShowMenuGeneral.Text = _config.Hotkeys.ShowMenu;
        _txtShowMenu.Text = _config.Hotkeys.ShowMenu;
        // Phase 5a: _txtAutoLogin1-4 removed from UI. v3 AutoLogin hotkeys are edited via
        // AccountHotkeysDialog / CharacterHotkeysDialog — legacy field values pass through
        // unchanged via BuildAppConfig below.
        _pendingTeamLogin1 = _config.Hotkeys.TeamLogin1 ?? "";
        _pendingTeamLogin2 = _config.Hotkeys.TeamLogin2 ?? "";
        _pendingTeamLogin3 = _config.Hotkeys.TeamLogin3 ?? "";
        _pendingTeamLogin4 = _config.Hotkeys.TeamLogin4 ?? "";
        // Team count in Direct Bindings reads _pendingTeamLogin* — repaint
        // now that they are set, since BuildHotkeysTab ran (and showed 0/4)
        // before this function populated them.
        if (_cardDirectBindings != null) RefreshDirectBindingsCard();

        // Layout
        // v3.22.80: checkbox states derive from WindowMode (the source of truth).
        _chkSlimTitlebar.Checked = _config.Layout.WindowMode == WindowMode.Fullscreen;
        _chkWindowedMode.Checked = _config.Layout.WindowMode == WindowMode.Windowed;
        _chkDarkTitlebar.Checked = _config.Layout.DarkTitlebar;
        _nudTitlebarOffset.Value = DarkTheme.ClampNud(_nudTitlebarOffset, _config.Layout.TitlebarOffset);
        _nudBottomOffset.Value = DarkTheme.ClampNud(_nudBottomOffset, _config.Layout.BottomOffset);
        _chkUseHook.Checked = _config.Layout.UseHook;
        _chkUseHook.Enabled = _config.Layout.SlimTitlebar;
        _chkMaximizeWindow.Checked = _config.EQClientIni.MaximizeWindow;
        _txtWindowTitleTemplate.Text = _config.Layout.WindowTitleTemplate;
        _nudTitlebarOffset.Enabled = _config.Layout.SlimTitlebar;
        _nudBottomOffset.Enabled = _config.Layout.SlimTitlebar;
        // v3.22.80: _chkMaximizeWindow is now an Advanced-only detached control;
        // its .Enabled is read nowhere, so the old slim-gated enable is removed.

        // Performance

        // Launch
        _nudLaunchDelay.Value = DarkTheme.ClampNud(_nudLaunchDelay, _config.Launch.LaunchDelayMs / 1000);

        // Paths
        _txtGinaPath.Text = _config.GinaPath;
        _txtGamparsePath.Text = _config.GamparsePath;
        _txtEqLogParserPath.Text = _config.EqLogParserPath;
        _txtNotesPath.Text = _config.NotesPath;
        _txtDalayaPatcherPath.Text = _config.DalayaPatcherPath;
        _chkRunAtStartup.Checked = _config.RunAtStartup;
        _chkShowTooltips.Checked = _config.ShowTooltips;

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

        // Video (reads from eqclient.ini). WindowedMode=TRUE is a hard requirement
        // (AppConfig.Validate pins ForceWindowedMode=true) and VideoSaveToIni writes it
        // literally — no UI control or backing field needed.
        _chkVideoMultiMon.Checked = _config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
        _nudVideoTopOffset.Value = DarkTheme.ClampNud(_nudVideoTopOffset, _config.Layout.TopOffset);
        _nudHorizontalNudge.Value = DarkTheme.ClampNud(_nudHorizontalNudge, _config.Layout.HorizontalNudgePx);
        PopulateVideoFromIni();
    }

    private void RunUninstall()
    {
        var result = MessageBox.Show(
            "This will revert all external changes made by EQSwitch:\n\n" +
            "  • Restore Dalaya's dinput8.dll if a legacy proxy is in the way\n" +
            "  • Remove legacy EQSwitch DLL artifacts from EQ folder\n" +
            "  • Remove startup shortcut\n" +
            "  • Remove desktop shortcut\n" +
            "  • Remove legacy registry startup entry\n\n" +
            "EQSwitch's own config and logs will NOT be deleted —\n" +
            "delete the EQSwitch folder yourself to fully remove the app.\n" +
            "eqclient.ini settings will NOT be reverted (use .bak files).\n\n" +
            "Continue?",
            "EQSwitch — Uninstall",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes) return;

        // Persist RunAtStartup=false BEFORE running CleanUp so that if the user
        // closes the result dialog with the X, ValidateStartupPath on next launch
        // won't resurrect the shortcut RemoveShortcuts just deleted.
        _chkRunAtStartup.Checked = false;
        _config.RunAtStartup = false;
        ConfigManager.Save(_config);
        ConfigManager.FlushSave();

        var actions = UninstallHelper.CleanUp(_config);

        if (actions.Count == 0)
            actions.Add("Nothing to clean up — no external modifications found.");

        actions.Add(string.Empty);
        actions.Add("You can now close EQSwitch and delete the EQSwitch folder to fully remove it.");

        MessageBox.Show(
            string.Join("\n", actions),
            "EQSwitch — Uninstall Complete",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private bool ApplySettings()
    {
        // Exe guard — the EQ Path is now Browse-only (can't be typo'd), but the Exe field stays
        // typeable for dev / custom MQ builds. If the named exe isn't actually in the EQ folder,
        // surface it on save: a misnamed exe (e.g. "eqgame.exellll") silently breaks launch and
        // the video-INI write. Confirm rather than hard-block, so a not-yet-copied build can save.
        var eqExePath = _txtEQPath.Text.Trim();
        var eqExeName = _txtExeName.Text.Trim();
        if (eqExePath.Length > 0 && eqExeName.Length > 0
            && !File.Exists(Path.Combine(eqExePath, eqExeName)))
        {
            var ans = MessageBox.Show(
                $"'{eqExeName}' was not found in the EQ folder:\n{eqExePath}\n\n" +
                "If that's a typo, fix the Exe field. (A custom MQ build that isn't there yet is fine.)\n\nSave anyway?",
                "Exe not found", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ans != DialogResult.Yes) return false;
        }

        // Phase 3.5-D + Phase 5a: hotkey conflict detection — same key combo bound to
        // multiple actions causes RegisterHotKey to silently fail on the second
        // registration. Scan covers tab-level Action + Team hotkeys plus the family-
        // table bindings (AccountHotkeys + CharacterHotkeys via HotkeyBindingUtil).
        // Legacy AutoLogin1-4 values come from _config (no longer edited on this tab)
        // so migrated v3 bindings still participate during the deprecation window.
        var tabHotkeys = new[]
        {
            ("Switch Key",       _txtSwitchKeyGeneral.Text.Trim()),
            ("Global Switch Key",_txtGlobalSwitchKey.Text.Trim()),
            ("Fix Windows",      _txtArrangeWindows.Text.Trim()),
            ("Launch One",       _txtLaunchOne.Text.Trim()),
            ("Launch All",       _txtLaunchAll.Text.Trim()),
            ("Multi-Mon Toggle", _txtToggleMultiMon.Text.Trim()),
            ("PiP Toggle",       _txtTogglePip.Text.Trim()),
            ("Show Menu",        _txtShowMenuGeneral.Text.Trim()),
            ("AutoLogin 1 (legacy)", _config.Hotkeys.AutoLogin1),
            ("AutoLogin 2 (legacy)", _config.Hotkeys.AutoLogin2),
            ("AutoLogin 3 (legacy)", _config.Hotkeys.AutoLogin3),
            ("AutoLogin 4 (legacy)", _config.Hotkeys.AutoLogin4),
            ("Team Login 1",     _pendingTeamLogin1),
            ("Team Login 2",     _pendingTeamLogin2),
            ("Team Login 3",     _pendingTeamLogin3),
            ("Team Login 4",     _pendingTeamLogin4),
        };
        var familyHotkeys = Config.HotkeyBindingUtil.EnumeratePopulatedLabeled(_config)
            .Select(t => (t.label, t.combo));
        var allHotkeys = tabHotkeys.Concat(familyHotkeys).ToArray();

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

        // 0. Account.Name (UI: "Note") is optional. Username is the pinning
        //    identity (used by AccountKey, AutoLogin keystrokes, and ini writes)
        //    and IS required — block degenerate accounts from hand-edited config
        //    or Import. Character Names remain required (they're identity).
        var emptyAcct = _pendingAccounts.FirstOrDefault(a => string.IsNullOrWhiteSpace(a.Username));
        if (emptyAcct != null)
        {
            MessageBox.Show(
                $"An Account is missing a Username (Note '{emptyAcct.Name}'). Set a Username or delete it before saving.",
                "Empty Account Username", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        // 1. Account names unique (case-insensitive).
        //
        // v3.22.79: switched from StringComparer.Ordinal to OrdinalIgnoreCase
        // here AND below (Account creds + Character names) to match the
        // case-folding boundary used everywhere else in the codebase:
        // AccountKey.Matches, FindAccountByName, FindCharacterByName, the FK
        // resolve at line ~1801, CharacterSelector's name match all use
        // OrdinalIgnoreCase. Pre-fix, dedup-by-Ordinal would let a hand-edited
        // config with ("foo","bar") and ("FOO","BAR") slip past validation;
        // downstream FK lookups would then match the wrong one. Surface
        // conflicts at validation time, not silently downstream.
        var acctNameDupes = _pendingAccounts
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
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
            .GroupBy(a => $"{a.Username}\u0001{a.Server}", StringComparer.OrdinalIgnoreCase)
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
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
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
                a.Username.Equals(c.AccountUsername, StringComparison.OrdinalIgnoreCase) &&
                a.Server.Equals(c.AccountServer, StringComparison.OrdinalIgnoreCase));
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

        // v3.22.25: take ConfigMutationLock across the BuildAppConfig + _onApply call.
        // Two racy reads inside the initializer below (the "Race fix" Accounts.Select
        // at line ~1797 reads live.LastLoginResult/LastLoginAt) are now serialized
        // against any AutoLoginManager SaveImmediate writing those fields. The lock
        // also covers _onApply(newConfig) which dispatches to TrayManager.ReloadConfig
        // (also lock-guarded — C# lock is reentrant per thread, so the UI thread
        // re-acquires harmlessly). Verifier-round-2 fix: canonical
        // Monitor.Enter(lock, ref tookLock) pattern — if Enter throws, tookLock
        // stays false and finally skips Exit on a never-acquired lock.
        // See ConfigManager.ConfigMutationLock XML doc for full contract.
        bool tookLock = false;
        try
        {
            if (!System.Threading.Monitor.TryEnter(Config.ConfigManager.ConfigMutationLock, 0))
            {
                FileLogger.Info("ApplySettings: ConfigMutationLock contended — waiting for in-flight AutoLogin SaveImmediate to release");
                System.Threading.Monitor.Enter(Config.ConfigManager.ConfigMutationLock, ref tookLock);
            }
            else
            {
                tookLock = true;
            }
            FileLogger.Info("ApplySettings: acquired ConfigMutationLock (blocking any AutoLogin SaveImmediate during build+apply)");

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
                HorizontalNudgePx = (int)_nudHorizontalNudge.Value,
                WindowMode = _chkWindowedMode.Checked ? WindowMode.Windowed : WindowMode.Fullscreen,
                SlimTitlebar = _chkSlimTitlebar.Checked || _chkWindowedMode.Checked,
                // v3.24.15 — taskbar-visibility is pinned to ShowTaskbars (the "Show taskbars" toggle
                // was removed; multimonitor always shows the 2nd taskbar — the validated working
                // state). SlimTitlebarSecondary stays at its (re-pinned-true) default. The two
                // swap-curtain knobs are JSON-only — preserve them across Settings→Apply (this block
                // rebuilds WindowLayout from scratch, so an uncopied field would silently reset to its
                // C# default).
                MultiMonTaskbarMode = MultiMonTaskbarMode.ShowTaskbars,
                SwapTransitionCurtain = _config.Layout.SwapTransitionCurtain,
                SwapCurtainMs = _config.Layout.SwapCurtainMs,
                DarkTitlebar = _chkDarkTitlebar.Checked,
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
                ShowMenu = _txtShowMenuGeneral.Text.Trim(),
                // Phase 5a: legacy AutoLogin1-4 pass through — values flow via v4 family tables now.
                AutoLogin1 = _config.Hotkeys.AutoLogin1,
                AutoLogin2 = _config.Hotkeys.AutoLogin2,
                AutoLogin3 = _config.Hotkeys.AutoLogin3,
                AutoLogin4 = _config.Hotkeys.AutoLogin4,
                TeamLogin1 = _pendingTeamLogin1,
                TeamLogin2 = _pendingTeamLogin2,
                TeamLogin3 = _pendingTeamLogin3,
                TeamLogin4 = _pendingTeamLogin4,
                // Once enabled, the hotkey is unlocked permanently
                // Phase 5a: MultiMonitorEnabled gate removed. Preserve existing value for
                // downgrade safety; SettingsForm no longer writes this field.
                MultiMonitorEnabled = _config.Hotkeys.MultiMonitorEnabled,
                DirectSwitchKeys = _config.Hotkeys.DirectSwitchKeys,
                // Phase 5a: family tables pass through — dialogs commit to _config directly
                // but BuildAppConfig still needs to carry them into newConfig so any caller
                // that reads newConfig (or a future _config = newConfig refactor) sees the
                // current state.
                AccountHotkeys = _config.Hotkeys.AccountHotkeys,
                CharacterHotkeys = _config.Hotkeys.CharacterHotkeys,
                SwitchKeyMode = _cboSwitchKeyMode.SelectedItem?.ToString() == "Cycle All" ? "cycleAll" : "swapLast"
            },
            Launch = new LaunchConfig
            {
                ExeName = _txtExeName.Text.Trim(),
                Arguments = _txtArgs.Text.Trim(),
                // v3.22.53: pass-through for the LaunchOne autologin opt-in.
                // No Settings UI control today — JSON-edit-only. Without this
                // round-trip, every Settings → Apply would clobber the user's
                // hand-edited value with the LaunchConfig class-initializer
                // default ("").
                DefaultLaunchOneAccount = _config.Launch.DefaultLaunchOneAccount,
                NumClients = _config.Launch.NumClients,
                LaunchDelayMs = (int)_nudLaunchDelay.Value * 1000,
                FixDelayMs = _config.Launch.FixDelayMs,
                // v3.15.2: pass-through for the 10 autologin timing knobs. No UI
                // surface yet — they are JSON-edit-only "advanced" tunables. Without
                // this round-trip, every Settings → Apply would clobber the user's
                // hand-edited values with the LaunchConfig class-initializer defaults.
                WaitTransitionInitialDelayMs = _config.Launch.WaitTransitionInitialDelayMs,
                WaitTransitionSettleMs       = _config.Launch.WaitTransitionSettleMs,
                WaitTransitionPollIntervalMs = _config.Launch.WaitTransitionPollIntervalMs,
                Burst1ActivationSettleMs     = _config.Launch.Burst1ActivationSettleMs,
                Burst1PostSubmitMs           = _config.Launch.Burst1PostSubmitMs,
                Burst2ActivationSettleMs     = _config.Launch.Burst2ActivationSettleMs,
                Burst2PostKeystrokeMs        = _config.Launch.Burst2PostKeystrokeMs,
                PostBurst1WaitMs             = _config.Launch.PostBurst1WaitMs,
                BridgeInitWaitMs             = _config.Launch.BridgeInitWaitMs,
                StaleSessionWaitMs           = _config.Launch.StaleSessionWaitMs,
                // v3.17.0+ JSON-only tunables — added 2026-05-15 to match the
                // pass-through pattern. Pre-fix every Settings → Apply
                // silently clobbered these to LaunchConfig defaults; verifier
                // T4-S 2026-05-15 caught JoinServerId, but ALL of these were
                // missing — pre-existing bug for every field added since v3.17.x.
                StaleSessionPollIntervalMs   = _config.Launch.StaleSessionPollIntervalMs,
                ConnectRetryCount            = _config.Launch.ConnectRetryCount,
                PostBurst2QuickFailCheckMs   = _config.Launch.PostBurst2QuickFailCheckMs,
                SkipShmEnterWorldOnDalaya    = _config.Launch.SkipShmEnterWorldOnDalaya,
                SkipNativeWarmup             = _config.Launch.SkipNativeWarmup,
                JoinServerId                 = _config.Launch.JoinServerId,
                // v3.22.26: UseStateMachine has no Settings UI control — it's
                // a JSON-only flag. v3.22.71 flipped the source default from
                // false → true (was: "deployed installs set it true"; now: the
                // source default IS true, so fresh installs / fresh configs
                // start working without manual JSON edits). Without this
                // pass-through, every Settings → Apply silently clobbered it
                // to the LaunchConfig default, persisting the default to disk.
                // Pre-v3.22.71 that default was false, so the clobber broke
                // the SM path. Post-v3.22.71 it's true, so the clobber would
                // turn a user's intentional false (power-user opt-out) back
                // to true — STILL A REGRESSION. Keep the pass-through. The
                // v3.22.26 fix is symmetric: pass-through here + propagate
                // in ReloadConfigCore (TrayManager.cs).
                UseStateMachine              = _config.Launch.UseStateMachine,
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
                // SelectedItem == null means the saved action string isn't in the
                // dropdown's current Items (e.g. a config holding "LoginAll5"
                // after Team5/6 were removed from clickActions). Preserve the
                // current persisted value rather than clobber to a hardcoded
                // default — round-trip-safe even when the option list shrinks.
                SingleClick = _cboSingleClick.SelectedItem is { } singleSel
                    ? TrayDisplayToAction(singleSel.ToString() ?? _config.TrayClick.SingleClick)
                    : _config.TrayClick.SingleClick,
                DoubleClick = _cboDoubleClick.SelectedItem is { } doubleSel
                    ? TrayDisplayToAction(doubleSel.ToString() ?? _config.TrayClick.DoubleClick)
                    : _config.TrayClick.DoubleClick,
                TripleClick = _cboTripleClick.SelectedItem is { } tripleSel
                    ? TrayDisplayToAction(tripleSel.ToString() ?? _config.TrayClick.TripleClick)
                    : _config.TrayClick.TripleClick,
                MiddleClick = _cboMiddleClick.SelectedItem is { } middleSel
                    ? TrayDisplayToAction(middleSel.ToString() ?? _config.TrayClick.MiddleClick)
                    : _config.TrayClick.MiddleClick,
                MiddleDoubleClick = _cboMiddleDoubleClick.SelectedItem is { } middleDoubleSel
                    ? TrayDisplayToAction(middleDoubleSel.ToString() ?? _config.TrayClick.MiddleDoubleClick)
                    : _config.TrayClick.MiddleDoubleClick
            },
            GinaPath = _txtGinaPath.Text.Trim(),
            GamparsePath = _txtGamparsePath.Text.Trim(),
            EqLogParserPath = _txtEqLogParserPath.Text.Trim(),
            NotesPath = _txtNotesPath.Text.Trim(),
            DalayaPatcherPath = _txtDalayaPatcherPath.Text.Trim(),
            RunAtStartup = _chkRunAtStartup.Checked,
            ShowTooltips = _chkShowTooltips.Checked,
            HotkeysLegacyBannerDismissed = _config.HotkeysLegacyBannerDismissed,
            // Phase 4: v4 lists are authoritative. LegacyAccounts is reverse-mapped
            // for downgrade safety. LegacyCharacterProfiles + CharacterAliases remain
            // pure passthrough until Phase 5 surfaces CharacterAlias editing in the UI.
            LegacyCharacterProfiles = _config.LegacyCharacterProfiles,
            LegacyAccounts = legacyAccountsForConfig,
            Accounts = _pendingAccounts.Select(a =>
            {
                // Race fix: if the user fires a team-login from the tray menu while
                // Settings is open, AutoLoginManager updates the LIVE Account's
                // LastLoginResult — but our staged snapshot still holds the value
                // captured at form-open. Naively round-tripping the staged value
                // would clobber the in-flight update. Resolution: when the password
                // hasn't changed (EncryptedPassword identical to live), the live
                // value is authoritative. When the password changed, the dialog
                // already reset the staged value to "" per its own reset semantic
                // (AccountEditDialog), so the staged value wins.
                // v3.22.27 R1 (T2-Opus G1 convergent): wrap the live-Accounts
                // lookup in ConfigMutationLock. SM AutoLoginManager.SaveImmediate
                // mutates _config.Accounts[i].LastLoginResult / LastLoginAt from
                // a background thread — the same race that drove the v3.22.26
                // JsonSerializer closure. UI-thread re-entrant on outer Apply
                // call-path (ApplySettings already holds the lock around its
                // build-newConfig phase).
                Account? live;
                lock (ConfigManager.ConfigMutationLock)
                {
                    live = _config.Accounts.FirstOrDefault(la =>
                        la.Username.Equals(a.Username, StringComparison.OrdinalIgnoreCase) &&
                        la.Server.Equals(a.Server, StringComparison.OrdinalIgnoreCase));
                }
                bool passwordUnchanged = live != null && live.EncryptedPassword == a.EncryptedPassword;
                return new Account
                {
                    Name = a.Name,
                    Username = a.Username,
                    EncryptedPassword = a.EncryptedPassword,
                    Server = a.Server,
                    UseLoginFlag = a.UseLoginFlag,
                    LastLoginResult = passwordUnchanged ? live!.LastLoginResult : a.LastLoginResult,
                    LastLoginAt = passwordUnchanged ? live!.LastLoginAt : a.LastLoginAt,
                    Notes = a.Notes,
                };
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
            // v3.15.2: pass-through. WarmupDwellMs has been consumed by AutoLoginManager
            // since v3.12.0 but was missing from the BuildAppConfig round-trip — every
            // Settings → Apply silently clobbered it to 4000 default (Round-3 verifier
            // T4 catch). No UI control yet; JSON-edit-only tunable like the LaunchConfig
            // timing knobs below.
            WarmupDwellMs = _config.WarmupDwellMs,
            // v3.23.0: Quick Login slots staged via _pendingQuickLogin1-4 (initialized in
            // InitializeForm, edited by QuickLoginSlotsDialog). Replaces a dead ternary —
            // the _cboQuickLoginN combos were removed in Phase 5a, so it always fell back
            // to _config anyway.
            QuickLogin1 = _pendingQuickLogin1,
            QuickLogin2 = _pendingQuickLogin2,
            QuickLogin3 = _pendingQuickLogin3,
            QuickLogin4 = _pendingQuickLogin4,
            AutoEnterWorld = _config.AutoEnterWorld,   // pass-through (UI checkbox removed; one-shot v3→v4 migration trigger lives in AppConfig.Validate)
            LogTrimThresholdMB = (int)_nudLogTrimThreshold.Value,
            Team1Account1  = _pendingTeam1A,
            Team1Account2  = _pendingTeam1B,
            Team2Account1  = _pendingTeam2A,
            Team2Account2  = _pendingTeam2B,
            Team3Account1  = _pendingTeam3A,
            Team3Account2  = _pendingTeam3B,
            Team4Account1  = _pendingTeam4A,
            Team4Account2  = _pendingTeam4B,
            Team5Account1  = _pendingTeam5A,
            Team5Account2  = _pendingTeam5B,
            Team6Account1  = _pendingTeam6A,
            Team6Account2  = _pendingTeam6B,
            Team7Account1  = _pendingTeam7A,
            Team7Account2  = _pendingTeam7B,
            Team8Account1  = _pendingTeam8A,
            Team8Account2  = _pendingTeam8B,
            Team9Account1  = _pendingTeam9A,
            Team9Account2  = _pendingTeam9B,
            Team10Account1 = _pendingTeam10A,
            Team10Account2 = _pendingTeam10B,
            Team11Account1 = _pendingTeam11A,
            Team11Account2 = _pendingTeam11B,
            Team12Account1 = _pendingTeam12A,
            Team12Account2 = _pendingTeam12B,
        };

        // Apply startup registry change
        if (newConfig.RunAtStartup != _config.RunAtStartup)
            StartupManager.SetRunAtStartup(newConfig.RunAtStartup);

        // MaximizeWindow lives in EQClientIni, update in-place
        newConfig.EQClientIni = _config.EQClientIni;
        newConfig.EQClientIni.MaximizeWindow = _chkMaximizeWindow.Checked;
        newConfig.EQClientIni.ConfiguredKeys.Add("Maximized");

        _onApply(newConfig);
        } // try (ConfigMutationLock)
        finally
        {
            if (tookLock) System.Threading.Monitor.Exit(Config.ConfigManager.ConfigMutationLock);
        }
        VideoSaveToIni();
        FileLogger.Info("Settings applied");

        // v3.22.78: same-name "consider renaming" balloon removed. Tray menu
        // now colors Account-resolved rows orange and Character-resolved rows
        // blue, so case-insensitive name collisions render unambiguously.

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
                .Where(c => c.AccountUsername.Equals(a.Username, StringComparison.OrdinalIgnoreCase) &&
                            c.AccountServer.Equals(a.Server, StringComparison.OrdinalIgnoreCase))
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
        // Default AutoScrollMargin is (0,0), so the scrollbar stops right at the
        // last child's bottom edge — that ate the Autologin Teams card's bottom
        // gold border at any DPI/form size that didn't have spare headroom.
        // 20px margin gives the scroll area room past the last card.
        page.AutoScrollMargin = new Size(0, 20);

        // ─── Accounts card ───────────────────────────────────────────
        // Card height 216: the DPAPI note + Flag legend share one hint row (legend
        // moved up beside the note, v3.22.91). Was 234 when the legend had its own row.
        var accountsCard = DarkTheme.MakeCard(page, "\uD83D\uDD11", "Accounts", DarkTheme.CardOrange, 10, y, 480, 216);

        _dgvAccounts = MakeDualSectionGrid();
        _dgvAccounts.Columns.Add("Num", "#");
        _dgvAccounts.Columns["Num"]!.Width = 30;
        _dgvAccounts.Columns.Add("Username", "Username");
        _dgvAccounts.Columns["Username"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dgvAccounts.Columns["Username"]!.FillWeight = 30;
        // "Notes" column = the Account.Notes model property — free-form
        // metadata only. Username is the pinning identity; Account.Name is an
        // internal FK shadow of Username (since v3.14.8) and never surfaces
        // in the grid.
        _dgvAccounts.Columns.Add("Notes", "Notes");
        _dgvAccounts.Columns["Notes"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dgvAccounts.Columns["Notes"]!.FillWeight = 30;
        _dgvAccounts.Columns.Add("Server", "Server");
        _dgvAccounts.Columns["Server"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _dgvAccounts.Columns["Server"]!.FillWeight = 20;
        // "Flag" column — last-autologin outcome glyph. ✓ = transitioned to
        // charselect, ✗ = AutoLoginManager-owned timeout, — = untried. Populated
        // in RefreshAccountsGrid from Account.LastLoginResult; written by
        // AutoLoginManager at the WaitForScreenTransition success/failure
        // boundary.
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

        // Login delay — compact, right side of button row.
        // Label at 380 (was 388) + input at 430 (was 425) gives ~17px between
        // the colon and the input's left edge. FixedSingle border on dark
        // backgrounds drops its left pixel (see DarkTheme.WrapWithBorder
        // comment) — the extra gap keeps the colon from masking that visible
        // ambiguity into "the input has no left border."
        DarkTheme.AddCardLabel(accountsCard, "Delay:", 380, btnY + 3);
        _nudLoginScreenDelay = DarkTheme.AddNumeric(accountsCard, 430, btnY, 45,
            _config.LoginScreenDelayMs / 1000m, 5, 10);
        _nudLoginScreenDelay.DecimalPlaces = 1;
        _nudLoginScreenDelay.Increment = 0.5m;

        // v3.22.91 (Nate): DPAPI note + Flag legend share one row now (legend moved
        // up beside the note). Shortened "passwords" off the note so both fit 480.
        DarkTheme.AddCardHint(accountsCard, "DPAPI-encrypted — same Windows user only.", 10, 196);
        DarkTheme.AddCardHint(accountsCard, "Flag: ✓ ok   ✗ failed   — untried", 250, 196);

        y += 224;

        // ─── Characters card ─────────────────────────────────────────
        var charactersCard = DarkTheme.MakeCard(page, "\uD83E\uDDD9", "Characters", DarkTheme.FgCharacterBlue, 10, y, 480, 196);

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
        _dgvCharacters.Columns.Add("HK", "Hotkey");
        _dgvCharacters.Columns["HK"]!.Width = 60;
        _dgvCharacters.Columns["HK"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
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

        // v3.23.0: assign the four "AutoLogin 1-4" Quick Login slots. Right-aligned to the
        // 480-wide card edge (480 − 10 margin − 135 width = 335).
        var btnQuickLogin = DarkTheme.AddCardButton(charactersCard, "⚡ Quick Login…", 335, btnY, 135);
        btnQuickLogin.Click += (_, _) => OnConfigureQuickLogin();

        y += 204;

        // ─── Autologin Teams ─────────────────────────────────────────
        // v3.22.58: card height grew 84\u2192174 to fit 6-row \u00D7 2-col summary
        // for 12 teams (2-col gives each cell ~159px \u2014 wide enough for
        // typical "T3: raistlin + Natedogg" without wrapping). Hint text
        // moved out of AddCardHint (FgDimGray on medium-gray card bg \u2014
        // low contrast) into a BgDark inset panel with FgWhite text \u2014
        // the "black box" treatment Nate requested for at-a-glance readability.
        // v3.22.60: font bumped 7.5pt \u2192 9pt to fill the panel width (the 7.5pt
        // text only used ~70% of available horizontal space \u2014 the empty band
        // looked unfinished). Panel + label heights grown ~10px each for 9pt
        // line-height headroom. TextAlign=MiddleLeft centers the row block
        // vertically inside the taller label.
        var teamsCard = DarkTheme.MakeCard(page, "\uD83D\uDC65", "Autologin Teams", DarkTheme.CardGold, 10, y, 480, 174);
        var btnTeams = DarkTheme.AddCardButton(teamsCard, "Configure Teams...", 10, 32, 120);
        btnTeams.Click += (_, _) => ShowTeamsDialog();
        var summaryPanel = new Panel
        {
            Location = new Point(140, 28),
            Size = new Size(330, 134),
            BackColor = DarkTheme.BgDark,
            BorderStyle = BorderStyle.FixedSingle,
        };
        teamsCard.Controls.Add(summaryPanel);
        _lblTeamSummary = new TeamSummaryLabel
        {
            Location = new Point(6, 4),
            Size = new Size(318, 124),
            ForeColor = DarkTheme.FgWhite,
            Font = DarkTheme.FontUI9,
            BackColor = Color.Transparent,
            AutoSize = false,
            // v3.22.68: was MiddleLeft. 6 rows at 9pt ≈ 96px inside a 124px label
            // — vertical centering produced a ~14px blank band above T1 that
            // looked like awkward unowned padding. TopLeft anchors the row block
            // to the panel's top edge so T1 sits right under the inset border;
            // remaining headroom collects at the bottom where it reads as
            // intentional breathing room instead of dead space.
            TextAlign = ContentAlignment.TopLeft,
            // AutoEllipsis applies only to the mirrored base Text used by
            // Narrator / fallback; owner-paint controls visible truncation
            // via Clip(MaxNameLen) in BuildTeamSummaryRows.
            AutoEllipsis = true,
        };
        _lblTeamSummary.Rows = BuildTeamSummaryRows();
        summaryPanel.Controls.Add(_lblTeamSummary);

        // Phase 5b: removed "Auto Enter World (legacy default)" checkbox.
        // It was a one-shot v3→v4 migration trigger consumed by AppConfig.Validate
        // (clears itself after migrating LegacyAccounts). v4 paths don't read it:
        //   • Character hotkeys → always enter world (hardcoded in FireCharacterLogin)
        //   • Account hotkeys → always stop at charselect (hardcoded in FireAccountLogin)
        //   • Per-team AutoEnter → still active via Team{N}AutoEnter on AppConfig
        // The migration code in AppConfig.Validate is preserved — anyone who
        // hand-edits the config to set AutoEnterWorld=true still triggers it.
        // BuildAppConfig now passes _config.AutoEnterWorld through unchanged.

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
            // User column resize is OFF by design. The Accounts/Characters
            // grids mix fixed-width columns (Num/Slot/HK/Flag) with Fill
            // columns (Username/Note/Server/Name/Account) and have no per-
            // column MinimumWidth. WinForms silently flips a Fill column to
            // AutoSizeMode=None when the user drags it, which collapses the
            // neighboring Fill columns and produces a jumpy "glitch" the
            // first time a user grabs a divider. The grid is sized for the
            // data — disable resize so the layout stays stable.
            AllowUserToResizeColumns = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
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

    /// <summary>
    /// v3.22.9: live-refresh handler for <see cref="AutoLoginManager.LoginComplete"/>.
    /// Syncs the AutoLoginManager-owned fields (<c>LastLoginAt</c> + <c>LastLoginResult</c>)
    /// from the LIVE <c>_config.Accounts</c> into the staged <c>_pendingAccounts</c>
    /// snapshot — leaves every other staged field alone so unsaved user edits in
    /// Settings are not clobbered. Match key is (Username, Server) case-insensitive.
    /// PID payload is unused: AutoLoginManager fires LoginComplete per-PID but doesn't
    /// expose which Account that PID was for, and the cost of re-syncing every staged
    /// Account from live config is trivial vs. the lookup machinery to map PID→Account
    /// post-hoc. Try/catch the body so a sync failure never crashes the form — the
    /// live-refresh is polish, not on the critical path.
    /// </summary>
    private void OnLoginComplete(object? sender, int pid)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => OnLoginComplete(sender, pid))); }
            catch (ObjectDisposedException) { /* form closed mid-fire — no-op */ }
            catch (InvalidOperationException) { /* handle not yet created — no-op */ }
            return;
        }

        try
        {
            // v3.22.25 verifier-round-2 fix: lock around the read of
            // _config.Accounts → live.LastLoginAt/Result. OnLoginComplete fires
            // AFTER the SM that owns `pid` released the lock, but a PARALLEL
            // SM (e.g. the other client of a team login) could be mid-write
            // to a different Account at the same moment we iterate. Without
            // this lock, the iterating reader could see _config.Accounts
            // swapped mid-foreach (if ApplySettings is running in parallel
            // on the same UI thread — impossible, but defense-in-depth) AND
            // could read a torn Nullable<DateTime>. We snapshot to locals
            // INSIDE the lock so the staged write outside the lock has no
            // racy data. RefreshAccountsGrid stays outside the lock — it
            // reads only _pendingAccounts (UI-thread-local).
            lock (Config.ConfigManager.ConfigMutationLock)
            {
                foreach (var staged in _pendingAccounts)
                {
                    var live = _config.Accounts.FirstOrDefault(a =>
                        string.Equals(a.Username, staged.Username, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(a.Server, staged.Server, StringComparison.OrdinalIgnoreCase));
                    if (live != null)
                    {
                        // Snapshot under the lock so a concurrent SM-finally
                        // can't tear the 16-byte Nullable<DateTime> read.
                        var snapAt = live.LastLoginAt;
                        var snapResult = live.LastLoginResult;
                        staged.LastLoginAt = snapAt;
                        staged.LastLoginResult = snapResult;
                    }
                }
            }
            RefreshAccountsGrid();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"SettingsForm: OnLoginComplete (pid={pid}) sync failed: {ex.Message}");
        }
    }

    private void RefreshAccountsGrid()
    {
        if (_dgvAccounts == null) return;
        _dgvAccounts.Rows.Clear();
        for (int i = 0; i < _pendingAccounts.Count; i++)
        {
            var a = _pendingAccounts[i];
            // Snapshot atomically-related fields ONCE. AutoLoginManager runs on a
            // background thread and writes LastLoginAt before LastLoginResult; we
            // read LastLoginResult first to pin the glyph, then dereference our
            // snapshot of LastLoginAt for the tooltip. Avoids both a re-read race
            // and a torn-read of Nullable<DateTime> (16 bytes, non-atomic on x64)
            // showing up as a garbage tooltip timestamp.
            string lastResult = a.LastLoginResult;
            DateTime? lastAt = a.LastLoginAt;
            // Flag column = last-autologin outcome glyph. Color tracks meaning so a
            // glance distinguishes ✓ (green) from ✗ (red) without reading the char.
            (string glyph, Color glyphColor) = lastResult switch
            {
                "ok"   => ("✓", DarkTheme.StatusOk),     // ✓
                "fail" => ("✗", DarkTheme.StatusFail),   // ✗
                _      => ("—", DarkTheme.FgDimGray),    // —
            };
            int rowIdx = _dgvAccounts.Rows.Add(i + 1, a.Username, a.Notes, a.Server, glyph);
            var flagCell = _dgvAccounts.Rows[rowIdx].Cells["Flag"];
            flagCell.Style.ForeColor = glyphColor;
            flagCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            flagCell.ToolTipText = lastAt.HasValue
                ? $"Last autologin {lastResult} at {lastAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "No autologin attempt recorded yet";
        }
    }

    private void RefreshCharactersGrid()
    {
        if (_dgvCharacters == null) return;
        _dgvCharacters.Rows.Clear();
        for (int i = 0; i < _pendingCharacters.Count; i++)
        {
            var c = _pendingCharacters[i];
            var linkedAccount = _pendingAccounts.FirstOrDefault(a =>
                a.Username.Equals(c.AccountUsername, StringComparison.OrdinalIgnoreCase) &&
                a.Server.Equals(c.AccountServer, StringComparison.OrdinalIgnoreCase));

            string acctDisplay;
            bool acctFlagged;
            if (string.IsNullOrEmpty(c.AccountUsername))
            {
                acctDisplay = "(unassigned)";
                acctFlagged = true;
            }
            else if (linkedAccount != null)
            {
                // Show Username (the login identity), not Name (the FK shadow
                // which can be a legacy custom display string on pre-v3.14.8
                // accounts). Username is what the user actually thinks of as
                // "the account this character belongs to."
                acctDisplay = linkedAccount.Username;
                acctFlagged = false;
            }
            else
            {
                acctDisplay = $"{c.AccountUsername}@{c.AccountServer} (missing)";
                acctFlagged = true;
            }

            var slotDisplay = c.CharacterSlot == 0 ? "auto" : c.CharacterSlot.ToString();
            // Hotkey column shows a green ✓ if any hotkey is bound to this character
            // (combo itself is editable via Hotkeys → Configure Characters). The full
            // combo lands in the cell tooltip so it stays discoverable on hover.
            var hkCombo = LookupHotkeyForTarget(c.Name);
            var hkDisplay = string.IsNullOrEmpty(hkCombo) ? "" : "✓";

            int rowIdx = _dgvCharacters.Rows.Add(i + 1, c.Name, acctDisplay, slotDisplay, hkDisplay);
            if (!string.IsNullOrEmpty(hkCombo))
            {
                _dgvCharacters.Rows[rowIdx].Cells["HK"].Style.ForeColor = DarkTheme.AccentGreen;
                _dgvCharacters.Rows[rowIdx].Cells["HK"].ToolTipText = hkCombo;
            }
            if (acctFlagged)
            {
                _dgvCharacters.Rows[rowIdx].Cells["Account"].Style.ForeColor = DarkTheme.FgDimGray;
                _dgvCharacters.Rows[rowIdx].Cells["Account"].Style.Font =
                    TrackFont(new Font("Segoe UI", 9f, FontStyle.Italic));
            }
        }
    }

    private string LookupHotkeyForTarget(string targetName)
    {
        if (string.IsNullOrEmpty(targetName)) return "";
        // v3.22.91 fix: v4 per-character/-account bindings live in
        // Hotkeys.CharacterHotkeys / AccountHotkeys (TargetName == Name). Phase 5 has
        // shipped, so these are the live source — check them FIRST. The legacy
        // QuickLogin/AutoLogin slots below are the pre-v4 bridge and are EMPTY on any
        // migrated config — which is exactly why the Characters-grid ✓ went missing:
        // the lookup only consulted the empty bridge slots, never CharacterHotkeys[].
        var hit = _config.Hotkeys.CharacterHotkeys
            .Concat(_config.Hotkeys.AccountHotkeys)
            .FirstOrDefault(b => HotkeyBindingUtil.IsPopulated(b)
                && b.TargetName.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit.Combo;

        // Legacy QuickLogin1-4 + AutoLogin1-4 bridge (pre-v4 configs only).
        var slots = new (string target, string combo)[]
        {
            (_config.QuickLogin1, _config.Hotkeys.AutoLogin1),
            (_config.QuickLogin2, _config.Hotkeys.AutoLogin2),
            (_config.QuickLogin3, _config.Hotkeys.AutoLogin3),
            (_config.QuickLogin4, _config.Hotkeys.AutoLogin4),
        };
        foreach (var (target, combo) in slots)
        {
            if (!string.IsNullOrEmpty(combo) && target.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                return combo;
        }
        return "";
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
            _lblTeamSummary.Rows = BuildTeamSummaryRows();
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
            // If Account.Name changed (case-insensitive — matches FK lookup semantics),
            // propagate to any slot referencing the old name. Case-only renames don't
            // need propagation because slot resolution is also OrdinalIgnoreCase.
            if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
            {
                UpdateTeamSlotName(QuickLoginSlot.Kind.Account, oldName, newName);
                PropagateNameChangeToQuickLogins(QuickLoginSlot.Kind.Account, oldName, newName);
            }
            RefreshAccountsGrid();
            RefreshCharactersGrid();
            _lblTeamSummary.Rows = BuildTeamSummaryRows();
        }
    }

    private void OnDeleteAccount(int idx)
    {
        if (idx < 0 || idx >= _pendingAccounts.Count) return;
        var acct = _pendingAccounts[idx];
        var dependents = _pendingCharacters.Where(c =>
            c.AccountUsername.Equals(acct.Username, StringComparison.OrdinalIgnoreCase) &&
            c.AccountServer.Equals(acct.Server, StringComparison.OrdinalIgnoreCase)).ToList();

        if (dependents.Count == 0)
        {
            if (MessageBox.Show($"Delete Account '{acct.Username}'?", "Delete Account",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _pendingAccounts.RemoveAt(idx);
            // v3.24.15: ClearStaleTeamSlots is kind-aware — clear the Account's acct: slots (typed)
            // plus any pre-v3.24.15 legacy-bare slot matching the Name, so nothing dangles.
            ClearStaleTeamSlots(QuickLoginSlot.Kind.Account, acct.Name);
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
                    ClearStaleTeamSlots(QuickLoginSlot.Kind.Account, acct.Name);
                    break;
                case CascadeDeleteChoice.DeleteAll:
                    foreach (var c in dependents)
                    {
                        _pendingCharacters.Remove(c);
                        ClearStaleTeamSlots(QuickLoginSlot.Kind.Character, c.Name);
                    }
                    _pendingAccounts.RemoveAt(idx);
                    ClearStaleTeamSlots(QuickLoginSlot.Kind.Account, acct.Name);
                    break;
            }
        }
        RefreshAccountsGrid();
        RefreshCharactersGrid();
        _lblTeamSummary.Rows = BuildTeamSummaryRows();
    }

    private void OnAddCharacter()
    {
        using var dlg = new CharacterEditDialog(null, _pendingAccounts, _pendingCharacters);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            _pendingCharacters.Add(dlg.Result);
            RefreshCharactersGrid();
            _lblTeamSummary.Rows = BuildTeamSummaryRows();
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
            if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
            {
                UpdateTeamSlotName(QuickLoginSlot.Kind.Character, oldName, newName);
                PropagateNameChangeToQuickLogins(QuickLoginSlot.Kind.Character, oldName, newName);
            }
            RefreshCharactersGrid();
            _lblTeamSummary.Rows = BuildTeamSummaryRows();
        }
    }

    /// <summary>
    /// When an Account or Character is renamed, rewrite any Quick Login slot
    /// (<c>_pendingQuickLogin1-4</c>) bound to the old name so the binding survives instead
    /// of going stale (dispatch would otherwise balloon "not found"). Matches on BOTH kind
    /// AND name — an Account rename must not clobber a same-named Character slot (e.g. the
    /// "Eisley" Character vs the "eisley" Account, which collide case-insensitively).
    /// v3.23.0: rewritten off the dead _cboQuickLogin combos (removed in Phase 5a) onto the
    /// live typed staging fields.
    /// </summary>
    private void PropagateNameChangeToQuickLogins(QuickLoginSlot.Kind kind, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName)) return;
        string Remap(string slot)
        {
            var (k, name) = QuickLoginSlot.Parse(slot);
            if (k != kind || !name.Equals(oldName, StringComparison.OrdinalIgnoreCase)) return slot;
            return kind == QuickLoginSlot.Kind.Character
                ? QuickLoginSlot.ForCharacter(newName)
                : QuickLoginSlot.ForAccount(newName);
        }
        _pendingQuickLogin1 = Remap(_pendingQuickLogin1);
        _pendingQuickLogin2 = Remap(_pendingQuickLogin2);
        _pendingQuickLogin3 = Remap(_pendingQuickLogin3);
        _pendingQuickLogin4 = Remap(_pendingQuickLogin4);
        RefreshQuickLoginReadout();
    }

    private void OnDeleteCharacter(int idx)
    {
        if (idx < 0 || idx >= _pendingCharacters.Count) return;
        var c = _pendingCharacters[idx];
        if (MessageBox.Show($"Delete Character '{c.Name}'?", "Delete Character",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _pendingCharacters.RemoveAt(idx);
        ClearStaleTeamSlots(QuickLoginSlot.Kind.Character, c.Name);
        RefreshCharactersGrid();
        _lblTeamSummary.Rows = BuildTeamSummaryRows();
    }

    private IReadOnlyList<TeamSummaryRow> BuildTeamSummaryRows()
    {
        // Per-segment kinds replace the prior " (C)" / " (A)" trailing
        // markers — TeamSummaryLabel paints Account names orange,
        // Character names white, and the " | " team boundary red.
        //
        // v3.22.69's clip budget reserved 4 chars for the suffix and 1 char
        // for the unresolved "?" sentinel. With the suffix gone, resolved
        // names reclaim those 4 chars (12-char headroom instead of 8) so
        // longer Account / Character names render unclipped. The unresolved
        // path still reserves 1 char for the trailing "?" sentinel.
        const int MaxNameLen = 12; // accommodates typical EQ char + Dalaya account names
        string Clip(string s, int budget = MaxNameLen) =>
            s.Length > budget ? s.Substring(0, budget - 1) + "…" : s;

        TeamSummarySegment ResolveSegment(string targetName)
        {
            if (string.IsNullOrEmpty(targetName))
                return new TeamSummarySegment("", SummarySegmentKind.Plain);
            // v3.24.15: route the (typed or bare) slot value through the shared resolver so a
            // char:/acct: prefix is honored and an Account is no longer shadowed by a same-name
            // Character. Staged (_pending*) lookups mirror the live FireTeam path.
            var (ch, ac) = TeamSlotResolver.Resolve(targetName,
                n => _pendingCharacters.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)),
                n => _pendingAccounts.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
            if (ch != null)
                return new TeamSummarySegment(Clip(ch.Name), SummarySegmentKind.CharacterName);
            if (ac != null)
                return new TeamSummarySegment(Clip(ac.Name), SummarySegmentKind.AccountName);
            // FK drift / unresolved typed target — render the clean (prefix-stripped) name with a
            // trailing "?" sentinel and route to Unresolved so the kind is explicit.
            return new TeamSummarySegment(Clip(QuickLoginSlot.DisplayName(targetName), MaxNameLen - 1) + "?", SummarySegmentKind.Unresolved);
        }

        void AppendTeamCell(List<TeamSummarySegment> segs, int teamNum, string u1, string u2)
        {
            segs.Add(new TeamSummarySegment($"T{teamNum}: ", SummarySegmentKind.Plain));
            var s1 = ResolveSegment(u1);
            var s2 = ResolveSegment(u2);
            bool hasS1 = !string.IsNullOrEmpty(s1.Text);
            bool hasS2 = !string.IsNullOrEmpty(s2.Text);
            if (hasS1 && hasS2)
            {
                segs.Add(s1);
                segs.Add(new TeamSummarySegment(" + ", SummarySegmentKind.Plain));
                segs.Add(s2);
            }
            else if (hasS1) segs.Add(s1);
            else if (hasS2) segs.Add(s2);
            else segs.Add(new TeamSummarySegment("(none)", SummarySegmentKind.Plain));
        }

        TeamSummaryRow Pair(int leftNum, string lU1, string lU2,
                            int rightNum, string rU1, string rU2)
        {
            var segs = new List<TeamSummarySegment>(16);
            AppendTeamCell(segs, leftNum, lU1, lU2);
            segs.Add(new TeamSummarySegment(" | ", SummarySegmentKind.TeamSeparator));
            AppendTeamCell(segs, rightNum, rU1, rU2);
            return new TeamSummaryRow(segs);
        }

        // 12 teams laid out as 6 rows × 2 cols (matches v3.22.58 layout
        // budget: ~159px per cell at 318px label width).
        return new[]
        {
            Pair( 1, _pendingTeam1A,  _pendingTeam1B,   2, _pendingTeam2A,  _pendingTeam2B),
            Pair( 3, _pendingTeam3A,  _pendingTeam3B,   4, _pendingTeam4A,  _pendingTeam4B),
            Pair( 5, _pendingTeam5A,  _pendingTeam5B,   6, _pendingTeam6A,  _pendingTeam6B),
            Pair( 7, _pendingTeam7A,  _pendingTeam7B,   8, _pendingTeam8A,  _pendingTeam8B),
            Pair( 9, _pendingTeam9A,  _pendingTeam9B,  10, _pendingTeam10A, _pendingTeam10B),
            Pair(11, _pendingTeam11A, _pendingTeam11B, 12, _pendingTeam12A, _pendingTeam12B),
        };
    }

    /// <summary>
    /// v3.22.10: public entry point so TrayManager can deep-link into Configure Teams
    /// even when Settings is already open (ShowSettings re-entry path takes BringToFront
    /// and silently returned before this; now it can fire the teams dialog too).
    /// BeginInvoke defers past the tray-click event handler return. Then routes to
    /// the shared OpenTeamsWithVisibleBusy() helper which handles the busy cursor +
    /// title bar feedback during the unavoidable ctor freeze. No 700ms beat here
    /// because Settings is already fully painted on the re-entry path.
    /// </summary>
    public void OpenTeamsDialogNow()
    {
        if (IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            OpenTeamsWithVisibleBusy();
        }));
    }

    /// <summary>
    /// v3.22.10: shared "open Teams with visible busy state" helper used by both the
    /// initial Shown handler (after the 700ms beat) and the re-entry path. The
    /// Windows wait cursor (the rotating spinner) is the standard "busy" signal
    /// and renders independently of the form's paint cycle.
    ///
    /// `UseWaitCursor` alone is unreliable when triggered from a tray click —
    /// it only applies when the pointer is over the form, but after a tray menu
    /// click the cursor is still hovering the tray. `Cursor.Current` changes
    /// the cursor at its CURRENT screen position regardless of which window
    /// it's over, so we set both: Cursor.Current for the immediate visible
    /// change, UseWaitCursor for the after-freeze case where the user moves
    /// over Settings before Teams appears. During the single-UI-thread freeze
    /// caused by AutoLoginTeamsDialog's ctor the OS can't dispatch
    /// WM_SETCURSOR (message pump locked), so whatever Cursor.Current is set
    /// to immediately before the freeze persists for its full duration.
    ///
    /// Earlier rounds layered a title-bar label (rejected) and a default-style
    /// tooltip (rendered with bad opacity) — both dropped. Wait cursor alone
    /// is sufficient signal that "click registered, loading."
    /// </summary>
    private void OpenTeamsWithVisibleBusy()
    {
        if (IsDisposed) return;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            UseWaitCursor = true;
            ShowTeamsDialog();
        }
        finally
        {
            UseWaitCursor = false;
            Cursor.Current = Cursors.Default;
        }
    }

    private void ShowTeamsDialog()
    {
        // Non-modal: if the dialog is already up, surface it instead of
        // stacking a second instance. Settings stays interactable while open.
        if (_openTeamsDialog != null && !_openTeamsDialog.IsDisposed)
        {
            _openTeamsDialog.Activate();
            return;
        }
        var dlg = new AutoLoginTeamsDialog(
            _pendingAccounts,
            _pendingCharacters,
            _pendingTeam1A,  _pendingTeam1B,
            _pendingTeam2A,  _pendingTeam2B,
            _pendingTeam3A,  _pendingTeam3B,
            _pendingTeam4A,  _pendingTeam4B,
            _pendingTeam5A,  _pendingTeam5B,
            _pendingTeam6A,  _pendingTeam6B,
            _pendingTeam7A,  _pendingTeam7B,
            _pendingTeam8A,  _pendingTeam8B,
            _pendingTeam9A,  _pendingTeam9B,
            _pendingTeam10A, _pendingTeam10B,
            _pendingTeam11A, _pendingTeam11B,
            _pendingTeam12A, _pendingTeam12B);
        dlg.FormClosed += (_, _) =>
        {
            if (dlg.DialogResult == DialogResult.OK)
            {
                _pendingTeam1A  = dlg.Team1Account1;
                _pendingTeam1B  = dlg.Team1Account2;
                _pendingTeam2A  = dlg.Team2Account1;
                _pendingTeam2B  = dlg.Team2Account2;
                _pendingTeam3A  = dlg.Team3Account1;
                _pendingTeam3B  = dlg.Team3Account2;
                _pendingTeam4A  = dlg.Team4Account1;
                _pendingTeam4B  = dlg.Team4Account2;
                _pendingTeam5A  = dlg.Team5Account1;
                _pendingTeam5B  = dlg.Team5Account2;
                _pendingTeam6A  = dlg.Team6Account1;
                _pendingTeam6B  = dlg.Team6Account2;
                _pendingTeam7A  = dlg.Team7Account1;
                _pendingTeam7B  = dlg.Team7Account2;
                _pendingTeam8A  = dlg.Team8Account1;
                _pendingTeam8B  = dlg.Team8Account2;
                _pendingTeam9A  = dlg.Team9Account1;
                _pendingTeam9B  = dlg.Team9Account2;
                _pendingTeam10A = dlg.Team10Account1;
                _pendingTeam10B = dlg.Team10Account2;
                _pendingTeam11A = dlg.Team11Account1;
                _pendingTeam11B = dlg.Team11Account2;
                _pendingTeam12A = dlg.Team12Account1;
                _pendingTeam12B = dlg.Team12Account2;
                _lblTeamSummary.Rows = BuildTeamSummaryRows();
            }
            _openTeamsDialog = null;
            dlg.Dispose();
        };
        _openTeamsDialog = dlg;
        dlg.Show(this);
    }

    /// <summary>
    /// Open the Quick Login Slots dialog (assigns the four "AutoLogin 1-4" targets).
    /// Non-modal + staged through _pendingQuickLogin1-4, mirroring ShowTeamsDialog.
    /// </summary>
    private void OnConfigureQuickLogin()
    {
        if (_openQuickLoginDialog != null && !_openQuickLoginDialog.IsDisposed)
        {
            _openQuickLoginDialog.Activate();
            return;
        }
        var dlg = new QuickLoginSlotsDialog(
            _pendingAccounts, _pendingCharacters,
            _pendingQuickLogin1, _pendingQuickLogin2, _pendingQuickLogin3, _pendingQuickLogin4);
        dlg.FormClosed += (_, _) =>
        {
            if (dlg.DialogResult == DialogResult.OK)
            {
                _pendingQuickLogin1 = dlg.Slot1;
                _pendingQuickLogin2 = dlg.Slot2;
                _pendingQuickLogin3 = dlg.Slot3;
                _pendingQuickLogin4 = dlg.Slot4;
                RefreshQuickLoginReadout();
            }
            _openQuickLoginDialog = null;
            dlg.Dispose();
        };
        _openQuickLoginDialog = dlg;
        dlg.Show(this);
    }

    /// <summary>Rebuild the "AutoLogin slots: 1 X  2 Y …" readout under the Tray Click card.</summary>
    private void RefreshQuickLoginReadout()
    {
        if (_lblQuickLoginReadout == null) return;
        _lblQuickLoginReadout.Text =
            "Auto-Login slots:   " + string.Join("    ", new[]
            {
                $"1 {QuickLoginSlot.DisplayName(_pendingQuickLogin1)}",
                $"2 {QuickLoginSlot.DisplayName(_pendingQuickLogin2)}",
                $"3 {QuickLoginSlot.DisplayName(_pendingQuickLogin3)}",
                $"4 {QuickLoginSlot.DisplayName(_pendingQuickLogin4)}",
            });
    }

    /// <summary>
    /// Clear team slots referencing a just-deleted entity. v3.24.15: kind-aware for the typed slot
    /// scheme — an Account delete clears its <c>acct:</c> slots, a Character delete clears its
    /// <c>char:</c> slots, and a pre-v3.24.15 legacy-bare slot is cleared by name (so a deleted
    /// entity leaves no dangling team reference). Mirrors PropagateNameChangeToQuickLogins' kind+name
    /// matching; <see cref="TeamSlotResolver"/> documents the typed-vs-bare routing.
    /// </summary>
    private void ClearStaleTeamSlots(QuickLoginSlot.Kind kind, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        // A legacy-bare slot resolves Character-first (TeamSlotResolver), so it only "belongs" to an
        // Account when no same-name Character survives — an Account delete must NOT clear a bare slot
        // that still resolves to a surviving same-name Character ("Eisley" char vs "eisley" acct).
        // Character deletes always own a name-matching bare slot.
        bool bareMatchesKind = kind == QuickLoginSlot.Kind.Character
            || !_pendingCharacters.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        bool changed = false;
        string Clear(string slot)
        {
            var (k, n) = QuickLoginSlot.Parse(slot);
            if (!n.Equals(name, StringComparison.OrdinalIgnoreCase)) return slot;
            bool match = k == kind || (k == QuickLoginSlot.Kind.LegacyBare && bareMatchesKind);
            if (!match) return slot;
            changed = true;
            return "";
        }
        _pendingTeam1A  = Clear(_pendingTeam1A);   _pendingTeam1B  = Clear(_pendingTeam1B);
        _pendingTeam2A  = Clear(_pendingTeam2A);   _pendingTeam2B  = Clear(_pendingTeam2B);
        _pendingTeam3A  = Clear(_pendingTeam3A);   _pendingTeam3B  = Clear(_pendingTeam3B);
        _pendingTeam4A  = Clear(_pendingTeam4A);   _pendingTeam4B  = Clear(_pendingTeam4B);
        _pendingTeam5A  = Clear(_pendingTeam5A);   _pendingTeam5B  = Clear(_pendingTeam5B);
        _pendingTeam6A  = Clear(_pendingTeam6A);   _pendingTeam6B  = Clear(_pendingTeam6B);
        _pendingTeam7A  = Clear(_pendingTeam7A);   _pendingTeam7B  = Clear(_pendingTeam7B);
        _pendingTeam8A  = Clear(_pendingTeam8A);   _pendingTeam8B  = Clear(_pendingTeam8B);
        _pendingTeam9A  = Clear(_pendingTeam9A);   _pendingTeam9B  = Clear(_pendingTeam9B);
        _pendingTeam10A = Clear(_pendingTeam10A);  _pendingTeam10B = Clear(_pendingTeam10B);
        _pendingTeam11A = Clear(_pendingTeam11A);  _pendingTeam11B = Clear(_pendingTeam11B);
        _pendingTeam12A = Clear(_pendingTeam12A);  _pendingTeam12B = Clear(_pendingTeam12B);
        if (changed) _lblTeamSummary.Rows = BuildTeamSummaryRows();
    }

    /// <summary>
    /// Remap team slots when an entity is renamed. v3.24.15: kind-aware — a typed slot of the
    /// matching kind is re-emitted typed with the new name; a same-name typed slot of the OTHER kind
    /// is left untouched (no cross-kind clobber — the "Eisley" Character vs "eisley" Account collide
    /// case-insensitively); a legacy-bare slot is renamed in place (stays bare — old behavior).
    /// Mirrors PropagateNameChangeToQuickLogins.
    /// </summary>
    private void UpdateTeamSlotName(QuickLoginSlot.Kind kind, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName)) return;
        // See ClearStaleTeamSlots: a legacy-bare slot resolves Character-first, so an Account rename
        // only owns it when no same-name Character survives (else the bare slot still points at the
        // Character and must not follow the Account rename).
        bool bareMatchesKind = kind == QuickLoginSlot.Kind.Character
            || !_pendingCharacters.Any(c => c.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        bool changed = false;
        string Remap(string slot)
        {
            var (k, n) = QuickLoginSlot.Parse(slot);
            if (!n.Equals(oldName, StringComparison.OrdinalIgnoreCase)) return slot;
            if (k == kind)
            {
                changed = true;
                return kind == QuickLoginSlot.Kind.Character
                    ? QuickLoginSlot.ForCharacter(newName)
                    : QuickLoginSlot.ForAccount(newName);
            }
            if (k == QuickLoginSlot.Kind.LegacyBare && bareMatchesKind)
            {
                changed = true;
                return newName;   // preserve legacy-bare format (old behavior), new name only
            }
            return slot;          // typed slot of the other kind, or a bare slot owned by the other kind
        }
        _pendingTeam1A  = Remap(_pendingTeam1A);   _pendingTeam1B  = Remap(_pendingTeam1B);
        _pendingTeam2A  = Remap(_pendingTeam2A);   _pendingTeam2B  = Remap(_pendingTeam2B);
        _pendingTeam3A  = Remap(_pendingTeam3A);   _pendingTeam3B  = Remap(_pendingTeam3B);
        _pendingTeam4A  = Remap(_pendingTeam4A);   _pendingTeam4B  = Remap(_pendingTeam4B);
        _pendingTeam5A  = Remap(_pendingTeam5A);   _pendingTeam5B  = Remap(_pendingTeam5B);
        _pendingTeam6A  = Remap(_pendingTeam6A);   _pendingTeam6B  = Remap(_pendingTeam6B);
        _pendingTeam7A  = Remap(_pendingTeam7A);   _pendingTeam7B  = Remap(_pendingTeam7B);
        _pendingTeam8A  = Remap(_pendingTeam8A);   _pendingTeam8B  = Remap(_pendingTeam8B);
        _pendingTeam9A  = Remap(_pendingTeam9A);   _pendingTeam9B  = Remap(_pendingTeam9B);
        _pendingTeam10A = Remap(_pendingTeam10A);  _pendingTeam10B = Remap(_pendingTeam10B);
        _pendingTeam11A = Remap(_pendingTeam11A);  _pendingTeam11B = Remap(_pendingTeam11B);
        _pendingTeam12A = Remap(_pendingTeam12A);  _pendingTeam12B = Remap(_pendingTeam12B);
        if (changed) _lblTeamSummary.Rows = BuildTeamSummaryRows();
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
            // Login-status flags are per-machine truth — strip on import so an
            // exported "✓" doesn't bleed onto a different machine where the
            // password may not actually work. Fresh imports start untried.
            account.LastLoginResult = "";
            account.LastLoginAt = null;
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
        btnOffsets.Size = new Size(95, 26);
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
        _nudHorizontalNudge = new NumericUpDown { Minimum = -10, Maximum = 10, Value = _config.Layout.HorizontalNudgePx };

        var btnReset = DarkTheme.AddCardButton(cardRes, "🔄 Reset", 370, cy, 95);
        btnReset.Click += (_, _) => VideoResetDefaults();

        y += 104;

        // ─── Monitor card ─────────────────────────────────────────
        var cardMon = DarkTheme.MakeCard(page, "🖥", "Monitor Selection", DarkTheme.CardBlue, 10, y, 480, 128);
        cy = 32;

        _chkVideoMultiMon = DarkTheme.AddCheckBox(cardMon, "Multi-Monitor Mode", L, cy);
        // v3.24.15: the "Show taskbars (multi-mon)" toggle was removed — multimonitor now always
        // shows the 2nd taskbar (ShowTaskbars), the validated working state. The mode is pinned in
        // ApplySettings + the AppConfig default/Validate; there is no user-facing knob anymore.

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
        // v3.22.68: was "Auto (first non-primary)" — stale label. The smart-pick
        // logic at WindowManager.ResolveSecondaryMonitorIdx (v3.22.19) walks all
        // non-primary monitors and picks the widest landscape one that's wide
        // enough for EQ, skipping tiny / portrait panels. The literal "first
        // non-primary" rule only fires as a last-resort fallback when no
        // monitor meets the suitability bar.
        secItems[0] = "Auto (best size)";
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
        // v3.22.80: two-mode model. Card holds Windowed Mode (disabled until
        // Phase 2), Fullscreen mode (the current WS_POPUP borderless look), and
        // Dark Titlebar. The WindowedMode=TRUE plumbing + Maximize on Launch
        // moved to the ⚙ Advanced dialog. 3 checkbox rows → height 130.
        var cardStyle = DarkTheme.MakeCard(page, "🪟", "Window Style", DarkTheme.CardPurple, 10, y, 480, 130);
        cy = 32;

        const int hintX = 260;

        var btnWrapper = DarkTheme.MakeButton("⚙ Advanced...", DarkTheme.BgInput, 385, 5);
        btnWrapper.Size = new Size(80, 26);
        btnWrapper.Font = DarkTheme.FontUI85;
        cardStyle.Controls.Add(btnWrapper);

        _chkWindowedMode = DarkTheme.AddCardCheckBox(cardStyle, "Windowed Mode", L, cy);
        DarkTheme.AddCardHint(cardStyle, "slim titlebar + covers taskbar", hintX, cy + 2);
        cy += 26;

        _chkSlimTitlebar = DarkTheme.AddCardCheckBox(cardStyle, "Fullscreen mode", L, cy);
        DarkTheme.AddCardHint(cardStyle, "borderless, flush all sides", hintX, cy + 2);
        cy += 26;

        // v3.22.54: DarkTitlebar promoted from the Advanced wrapper dialog up
        // to the main Window Style card per Nate 2026-05-26 — it's a regular
        // visual preference that shouldn't be buried two clicks deep.
        _chkDarkTitlebar = DarkTheme.AddCardCheckBox(cardStyle, "Dark Titlebar", L, cy);
        DarkTheme.AddCardHint(cardStyle, "dark title bar instead of the default white", hintX, cy + 2);
        cy += 22;

        // Wrapper dialog backing fields — titlebar offset, bottom margin, DLL
        // hook. Advanced settings most users don't touch. v3.22.80: Maximize on
        // Launch is a detached field surfaced only via the ⚙ Advanced dialog.
        // (v3.22.91: Force-Windowed is no longer a field — ForceWindowedMode is a
        // pinned invariant written literally by VideoSaveToIni.)
        _nudTitlebarOffset = new NumericUpDown { Value = 13, Minimum = 0, Maximum = 40 };  // transient; overwritten by PopulateFromConfig (default 13 — v3.22.86)
        _nudBottomOffset = new NumericUpDown { Value = 21, Minimum = 0, Maximum = 100 };  // transient; overwritten by PopulateFromConfig (default 21 — matches WindowLayout.BottomOffset)
        _chkUseHook = new CheckBox();
        _chkMaximizeWindow = new CheckBox();   // v3.22.80: Advanced-only
        btnWrapper.Enabled = true;              // Advanced is always reachable now
        btnWrapper.Click += (_, _) => ShowWrapperDialog();

        // v3.22.80: Fullscreen and Windowed are mutually exclusive; exactly one
        // is always selected. Windowed is disabled in Phase 1, so Fullscreen is
        // effectively pinned on.
        bool syncingModes = false;
        _chkSlimTitlebar.CheckedChanged += (_, _) =>
        {
            if (syncingModes) return;
            syncingModes = true;
            if (_chkSlimTitlebar.Checked)
                _chkWindowedMode.Checked = false;
            else if (!_chkWindowedMode.Enabled || !_chkWindowedMode.Checked)
                _chkSlimTitlebar.Checked = true;   // can't have neither mode on
            syncingModes = false;
        };

        _chkWindowedMode.CheckedChanged += (_, _) =>
        {
            if (syncingModes) return;
            syncingModes = true;
            if (_chkWindowedMode.Checked) _chkSlimTitlebar.Checked = false;
            else if (!_chkSlimTitlebar.Checked) _chkSlimTitlebar.Checked = true;
            syncingModes = false;
        };

        // v3.22.80: card shrank 152 → 130 (4 rows → 3 rows after Maximize on
        // Launch moved to the Advanced dialog). Advance tracked 160 → 138 to
        // keep the historical 8 px gap to the Preferences card below (130 + 8).
        y += 138;

        // ─── Preferences card ────────────────────────────────────
        // Left: Show Tooltips toggle (moved from Paths → Startup card — pairs
        // logically with the Tooltip Delay knob it gates).
        // Right: Tooltip Delay (slid over from where Client Launch Delay was;
        // Client Launch Delay moved to a header-less section on the Hotkeys tab).
        var cardPrefs = DarkTheme.MakeCard(page, "⚙", "Preferences", DarkTheme.CardCyan, 10, y, 480, 68);
        cy = 32;
        _chkShowTooltips = DarkTheme.AddCardCheckBox(cardPrefs, "Show Tooltips", L, cy + 1);
        // "Duration" not "Delay": this is the auto-dismiss interval for the
        // post-action FloatingTooltip toast (and gates menu hover tooltips
        // through ShowTooltips). Range 100..5000ms — on/off lives in the
        // ShowTooltips checkbox, not in this numeric. 100ms floor avoids
        // sub-perceptual flashes; 5000ms ceiling avoids sticky popups.
        DarkTheme.AddCardLabel(cardPrefs, "Tooltip Duration:", 240, cy);
        _nudTooltipDuration = DarkTheme.AddCardNumeric(cardPrefs, 360, cy, 55, 700, 300, 5000);
        _nudTooltipDuration.Increment = 100;
        DarkTheme.AddCardHint(cardPrefs, "ms", 425, cy);

        return page;
    }

    private void ShowOffsetsDialog()
    {
        // v3.22.91: Offset X/Y removed — they wrote eqclient.ini XOffset/YOffset,
        // which WindowManager overrides on launch (X=monitor.Left, Y=monitor.Top
        // +TopOffset) in both slim modes, so they were inert. What's left are the
        // two nudges that DO apply in both Fullscreen and Windowed: Horizontal
        // Nudge (1px DPI-sliver fix) and Top Offset (Y arrangement). The backing
        // _nudVideoOffsetX/Y fields are kept as data holders so existing ini
        // values still round-trip unchanged.
        using var dlg = new Form
        {
            Text = "Window Offsets",
            Size = new Size(340, 268),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        DarkTheme.StyleForm(dlg, dlg.Text, dlg.Size);

        int y = 15;
        const int L = 15, I = 140;

        DarkTheme.AddHint(dlg, "Fine-tune EQSwitch's window placement (both modes):", L, y);
        y += 24;

        // v3.22.91 (Nate /frontend-design): both numerics share ONE width (55) and
        // ONE hint rule (HINTX) so the rows read as a matched pair. They only hold
        // 2-4 digits, so the old 60/70 mismatch looked oversized and ragged.
        const int NUDW = 55, HINTX = I + NUDW + 12;
        DarkTheme.AddLabel(dlg, "Horizontal Nudge:", L, y + 2);
        var nudHoriz = DarkTheme.AddNumeric(dlg, I, y, NUDW, _nudHorizontalNudge.Value, -10, 10);
        DarkTheme.AddHint(dlg, "+ = right   ·   - = left", HINTX, y + 4);
        y += 30;

        DarkTheme.AddLabel(dlg, "Top Offset:", L, y + 2);
        var nudTop = DarkTheme.AddNumeric(dlg, I, y, NUDW, _nudVideoTopOffset.Value, -100, 200);
        DarkTheme.AddHint(dlg, "px from top", HINTX, y + 4);
        y += 32;

        // v3.22.91: use-case explanations live on full-width lines at the bottom (not
        // long right-of-box hints) so the dialog stays narrow and the text can't run
        // off — AddHint labels are AutoSize, so measure-to-fit grows the dialog to
        // exactly fit the widest line below.
        DarkTheme.AddHint(dlg, "Nudge: fixes a 1px gap on some multi-monitor setups.", L, y);
        y += 16;
        DarkTheme.AddHint(dlg, "Top Offset: only if your taskbar/bezel sits at the TOP (else 0).", L, y);
        y += 26;

        var btnOK = DarkTheme.MakePrimaryButton("Save", L, y);
        btnOK.Width = 90;
        btnOK.Click += (_, _) =>
        {
            _nudVideoTopOffset.Value = nudTop.Value;
            _nudHorizontalNudge.Value = nudHoriz.Value;
            dlg.DialogResult = DialogResult.OK;
        };
        dlg.Controls.Add(btnOK);

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, L + 100, y);
        btnCancel.Width = 90;
        btnCancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
        dlg.Controls.Add(btnCancel);

        dlg.AcceptButton = (IButtonControl)btnOK;
        dlg.CancelButton = (IButtonControl)btnCancel;

        // v3.22.91 — measure-to-fit (same pattern as ShowWrapperDialog): size the
        // client area to the actual content so removing the X/Y rows doesn't leave
        // dead space and no hint clips at any DPI.
        dlg.PerformLayout();
        int contentRight = 0, contentBottom = 0;
        foreach (Control c in dlg.Controls)
        {
            contentRight = Math.Max(contentRight, c.Right);
            contentBottom = Math.Max(contentBottom, c.Bottom);
        }
        dlg.ClientSize = new Size(Math.Max(contentRight + L, 300), contentBottom + L);

        dlg.ShowDialog(this);
    }

    private void ShowWrapperDialog()
    {
        using var dlg = new Form
        {
            Text = "Wrapper Settings",
            Size = new Size(360, 400),  // v3.22.87 — transient; the real ClientSize is fit to content just before ShowDialog (see measure-to-fit below)
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        DarkTheme.StyleForm(dlg, dlg.Text, dlg.Size);

        int y = 15;
        const int L = 15, I = 160;

        // v3.22.91: Titlebar peek + bottom margin only mean something in Windowed
        // mode. Fullscreen (WS_POPUP, flush all sides) clamps the caption to 0 and
        // renders edge-to-edge, so both are inert there — grey them out so the user
        // isn't tuning knobs that do nothing in the currently-selected mode.
        bool fullscreen = _chkSlimTitlebar.Checked;

        DarkTheme.AddLabel(dlg, "Titlebar hidden (px):", L, y + 2);
        var nudTitle = DarkTheme.AddNumeric(dlg, I, y, 60, _nudTitlebarOffset.Value, 0, 40);
        nudTitle.Enabled = !fullscreen;
        y += 24;
        DarkTheme.AddHint(dlg, "0 = hidden · 13 = title + half buttons · raise for more", L, y);
        y += 18;

        DarkTheme.AddLabel(dlg, "Bottom margin (px):", L, y + 2);
        var nudBottom = DarkTheme.AddNumeric(dlg, I, y, 60, _nudBottomOffset.Value, 0, 100);
        nudBottom.Enabled = !fullscreen;
        y += 24;
        DarkTheme.AddHint(dlg, "Game render height reduction", L, y);
        y += 18;

        DarkTheme.AddHint(dlg, fullscreen
            ? "Titlebar + margin apply in Windowed mode only (Fullscreen active)."
            : "Defaults: titlebar 13, margin 21 · keep margin ≥ titlebar", L, y);
        y += 28;

        // v3.22.91: removed three controls from this dialog —
        //   • "Force Windowed Mode" (eqclient.ini WindowedMode=TRUE): a hard
        //     requirement for EQSwitch's window management, not a real choice.
        //     Pinned true in AppConfig.Validate (airtight invariant).
        //   • "Maximize on Launch": a parked/under-review feature. _chkMaximizeWindow
        //     stays false by default; revisit if/when the feature is finished.
        //   • "DLL Hook (zero flicker)": injection auto-falls-back to the guard timer
        //     on failure, so the casual toggle wasn't needed. _chkUseHook stays a
        //     config-only knob (default true); power users can still set it via JSON.
        // Dark Titlebar lives on the main Window Style card (moved v3.22.54).

        var btnReset = DarkTheme.MakeButton("Reset", DarkTheme.BgMedium, L, y);
        btnReset.Width = 70;
        btnReset.Click += (_, _) =>
        {
            // Defaults track AppConfig.WindowLayout: TitlebarOffset = 13 (peeks
            // ~half the maximize button — the WinEQ2 look; bottom kept flush by the
            // v3.22.82 render-height fix + v3.22.84 read-back correction),
            // BottomOffset = 21. DarkTitlebar + DLL hook defaults live elsewhere now.
            nudTitle.Value = 13;
            nudBottom.Value = 21;
        };
        dlg.Controls.Add(btnReset);
        y += 36;

        var btnOK = DarkTheme.MakePrimaryButton("Save", L, y);
        btnOK.Width = 90;
        btnOK.Click += (_, _) =>
        {
            _nudTitlebarOffset.Value = nudTitle.Value;
            _nudBottomOffset.Value = nudBottom.Value;
            dlg.DialogResult = DialogResult.OK;
        };
        dlg.Controls.Add(btnOK);

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 120, y);
        btnCancel.Width = 90;
        btnCancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
        dlg.Controls.Add(btnCancel);

        dlg.AcceptButton = (IButtonControl)btnOK;
        dlg.CancelButton = (IButtonControl)btnCancel;

        // v3.22.87 — measure-to-fit: size the client area to the actual content
        // (widest + lowest control) + a uniform L-px margin, instead of a guessed
        // fixed width. AutoSize labels report their true width once laid out, so the
        // dialog ends up exactly as wide as its longest hint — no dead padding, no
        // clipping, correct at any font/DPI. Min width keeps the title bar + buttons
        // from cramping if every line is short.
        dlg.PerformLayout();
        int contentRight = 0, contentBottom = 0;
        foreach (Control c in dlg.Controls)
        {
            contentRight = Math.Max(contentRight, c.Right);
            contentBottom = Math.Max(contentBottom, c.Bottom);
        }
        dlg.ClientSize = new Size(Math.Max(contentRight + L, 300), contentBottom + L);

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
                        // v3.22.91: WindowedMode read removed — it's a pinned invariant
                        // (TRUE), written literally by VideoSaveToIni; nothing to read in.
                        case "xoffset":
                            if (int.TryParse(val, out int ox)) _nudVideoOffsetX.Value = Math.Clamp(ox, -5000, 5000);
                            break;
                        case "yoffset":
                            if (int.TryParse(val, out int oy)) _nudVideoOffsetY.Value = Math.Clamp(oy, -5000, 5000);
                            break;
                    }
                }
            }

            // Match to preset. Native first — if the INI resolution equals this monitor's
            // native res, show "Native" (index 0) so it round-trips cleanly.
            int width = (int)_nudVideoWidth.Value;
            int height = (int)_nudVideoHeight.Value;
            var (natW, natH) = GetNativeResolution();
            if (width == natW && height == natH)
            {
                _cboVideoPreset.SelectedIndex = 0; // Native
            }
            else
            {
                int presetIdx = Array.FindIndex(VideoPresets, p => p.W == width && p.H == height);
                if (presetIdx >= 0)
                {
                    // +1: "Native" is prepended at combo index 0, so VideoPresets[k] now lives
                    // at combo index k+1.
                    _cboVideoPreset.SelectedIndex = presetIdx + 1;
                }
                else
                {
                    string customKey = $"{width}x{height}";
                    int customIdx = _cboVideoPreset.Items.IndexOf(customKey);
                    _cboVideoPreset.SelectedIndex = customIdx >= 0 ? customIdx : _cboVideoPreset.Items.Count - 1;
                }
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
            _config.EQClientIni.ForceWindowedMode = true;  // pinned invariant (see AppConfig.Validate)
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
                ["WindowedMode"] = "TRUE",  // pinned invariant — never FALSE
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
            string wmVal = "TRUE";  // pinned invariant
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
        _chkVideoMultiMon.Checked = false;
        _nudVideoTopOffset.Value = 0;
        // v3.22.54 round-1 fix (T3 Sonnet + T3 Opus convergent IMPORTANT):
        // same class of per-layout offset as TopOffset — must reset along
        // with the other three or Reset Defaults silently keeps the nudge.
        _nudHorizontalNudge.Value = 0;

        // v3.22.91 (Nate 2026-05-31): Reset Defaults on the Video tab deliberately
        // does NOT touch the Window Style card (window mode / dark titlebar / titlebar
        // + bottom offsets / DLL hook). This reverts the v3.22.57 "reset ALL video
        // controls" contract ON PURPOSE — clicking Reset in the EQ Resolution card was
        // yanking Windowed users back to Fullscreen, which is surprising and unwanted.
        // Reset now restores only resolution + offsets; window mode/style is left
        // exactly as the user set it. (Window-mode default is Windowed as of v3.22.91.)
    }

    /// <summary>
    /// The primary monitor's physical resolution, backing the "Native" preset. The app runs
    /// SystemAware DPI (Program.cs) so Screen.PrimaryScreen.Bounds is true physical pixels for
    /// the primary monitor — the same coordinate space the window manager positions in. Mirrors
    /// the Screen.PrimaryScreen fallback idiom already used in EQClientSettingsForm / PipOverlay
    /// / TrayManager.
    /// </summary>
    private static (int W, int H) GetNativeResolution()
    {
        var b = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        return (b.Width, b.Height);
    }

    private void PopulateVideoPresets()
    {
        _cboVideoPreset.Items.Clear();

        // "Native" first → resolves to THIS machine's primary-monitor resolution. Every index-0
        // fallback (missing/unreadable eqclient.ini, Reset Defaults) now lands here, so a fresh
        // user — or anyone who opens this tab and hits Save without touching the combo — writes
        // their REAL resolution to eqclient.ini instead of the hardcoded 1920x1080 that index 0
        // used to be (which would clobber a non-1080p user's actual settings).
        var (nw, nh) = GetNativeResolution();
        _cboVideoPreset.Items.Add($"Native ({nw}x{nh})");

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
            // Native → fill W/H with the live primary-monitor resolution so a Save writes it.
            if (selected.StartsWith("Native", StringComparison.Ordinal))
            {
                var (nw, nh) = GetNativeResolution();
                _nudVideoWidth.Value = Math.Clamp(nw, 320, 7680);
                _nudVideoHeight.Value = Math.Clamp(nh, 200, 4320);
                return;
            }

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

        // Typed dims == this monitor's native res → snap to "Native" (combo index 0),
        // consistent with how PopulateVideoFromIni round-trips a native-res INI.
        var (natW, natH) = GetNativeResolution();
        if (w == natW && h == natH) { _cboVideoPreset.SelectedIndex = 0; return; }

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

        // Native is represented by the synthetic "Native (WxH)" combo entry, never a stored
        // custom preset. Without this guard, a user whose native res isn't one of the built-in
        // VideoPresets (e.g. an ultrawide 3440x1440) who saves with Native selected would get
        // their res added to CustomVideoPresets — then PopulateVideoPresets renders it TWICE
        // (once as "Native (WxH)", once as a bare "WxH" custom row). Skip native dims here.
        var (natW, natH) = GetNativeResolution();
        if (w == natW && h == natH)
            return;

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
            // Non-modal Teams dialog: close it before SettingsForm tears down so
            // its FormClosed handler doesn't fire against half-disposed state.
            // The handler nulls _openTeamsDialog and calls dlg.Dispose() itself,
            // so we just need to trigger the close here.
            _openTeamsDialog?.Close();
            _openTeamsDialog = null;
            DismissMonitorOverlays();
            // Dispose inline Font objects on hotkey TextBoxes and other controls
            // that were created with new Font() — base.Dispose doesn't clean these up
            DisposeControlFonts(_txtSwitchKeyGeneral, _txtSwitchKey, _txtGlobalSwitchKey,
                _txtArrangeWindows, _txtToggleMultiMon, _txtLaunchOne, _txtLaunchAll,
                _txtShowMenuGeneral, _txtShowMenu);
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

    // v3.22.53 post-round-6 fix: Team 5/6 entries match the clickActions
    // array + the TrayClickValid allowlist in AppConfig.Validate. Round-trip
    // intact across config-write/read, JSON hand-edit, and Settings dropdown.
    private static readonly Dictionary<string, string> _trayActionDisplayMap = new()
    {
        // v3.23.0: "Launch Two" reads cleaner than "LaunchAll" (launches the configured
        // client count, 2 by default). Display-only — stored value stays "LaunchAll".
        ["LaunchOne"] = "Launch One",
        ["LaunchAll"] = "Launch Two",
        // v3.23.0: slot labels get a hyphen ("Auto-Login1") to match the Quick Login dialog.
        // Team labels keep the original "AutoLoginTeam N" form. Display-only — stored values
        // stay AutoLogin1-4 / LoginAll[N].
        ["AutoLogin1"] = "Auto-Login1",
        ["AutoLogin2"] = "Auto-Login2",
        ["AutoLogin3"] = "Auto-Login3",
        ["AutoLogin4"] = "Auto-Login4",
        ["LoginAll"]  = "AutoLoginTeam1",
        ["LoginAll2"] = "AutoLoginTeam2",
        ["LoginAll3"] = "AutoLoginTeam3",
        ["LoginAll4"] = "AutoLoginTeam4",
        ["LoginAll5"] = "AutoLoginTeam5",
        ["LoginAll6"] = "AutoLoginTeam6"
    };

    private static readonly Dictionary<string, string> _trayDisplayActionMap = new()
    {
        ["Launch One"] = "LaunchOne",
        ["Launch Two"] = "LaunchAll",
        ["Auto-Login1"] = "AutoLogin1",
        ["Auto-Login2"] = "AutoLogin2",
        ["Auto-Login3"] = "AutoLogin3",
        ["Auto-Login4"] = "AutoLogin4",
        ["AutoLoginTeam1"] = "LoginAll",
        ["AutoLoginTeam2"] = "LoginAll2",
        ["AutoLoginTeam3"] = "LoginAll3",
        ["AutoLoginTeam4"] = "LoginAll4",
        ["AutoLoginTeam5"] = "LoginAll5",
        ["AutoLoginTeam6"] = "LoginAll6"
    };

    /// <summary>Convert config action name to dropdown display name.</summary>
    private static string TrayActionToDisplay(string action) =>
        _trayActionDisplayMap.TryGetValue(action, out var display) ? display : action;

    /// <summary>Convert dropdown display name back to config action name.</summary>
    private static string TrayDisplayToAction(string display) =>
        _trayDisplayActionMap.TryGetValue(display, out var action) ? action : display;
}

/// <summary>
/// Owner-painted Label that renders multi-colored team-summary rows. Set
/// <see cref="Rows"/> to drive paint; segments are colored by their
/// <see cref="SummarySegmentKind"/> (Account → orange, Character → blue,
/// TeamSeparator → red, Plain → ForeColor). Mirror text is published to the
/// base <c>Label.Text</c> property so AutoEllipsis fallback + Narrator can
/// still see the row content if owner-paint somehow doesn't fire.
/// </summary>
internal sealed class TeamSummaryLabel : Label
{
    private IReadOnlyList<TeamSummaryRow> _rows = Array.Empty<TeamSummaryRow>();

    public IReadOnlyList<TeamSummaryRow> Rows
    {
        get => _rows;
        set
        {
            _rows = value ?? Array.Empty<TeamSummaryRow>();
            // Mirror to base Text for accessibility + AutoEllipsis fallback.
            // SuspendLayout coalesces the two Invalidate signals (Text-set + our explicit) into one paint.
            SuspendLayout();
            base.Text = string.Join("\n", _rows.Select(r => string.Concat(r.Segments.Select(s => s.Text))));
            ResumeLayout(performLayout: false);
            Invalidate();
        }
    }

    public TeamSummaryLabel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Honor transparent BackColor by painting the parent's surface under
        // us first; otherwise fill with our own BackColor. The existing
        // call-site sets BackColor = Color.Transparent so the card's
        // BgPanel surface needs to bleed through.
        if (BackColor == Color.Transparent && Parent != null)
        {
            ButtonRenderer.DrawParentBackground(e.Graphics, ClientRectangle, this);
        }
        else
        {
            using var bg = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(bg, ClientRectangle);
        }

        if (_rows.Count == 0)
        {
            // Defensive: no segments → fall back to standard Label render so
            // the control isn't permanently blank if a caller forgets Rows.
            base.OnPaint(e);
            return;
        }

        int lineH = Font.Height;
        int y = ClientRectangle.Y;
        // NoPadding makes inter-segment joins pixel-accurate (TextRenderer
        // otherwise injects ~3-6px GlyphOverhang between draws). Side-effect:
        // each row starts ~3px left of where a standard Label would draw —
        // acceptable here since the whole owner-paint surface is consistent.
        const TextFormatFlags format = TextFormatFlags.NoPadding;
        foreach (var row in _rows)
        {
            int x = ClientRectangle.X;
            int remaining = ClientRectangle.Width;
            foreach (var seg in row.Segments)
            {
                if (string.IsNullOrEmpty(seg.Text) || remaining <= 0) continue;
                var color = seg.Kind switch
                {
                    SummarySegmentKind.AccountName    => DarkTheme.FgAccountOrange,
                    SummarySegmentKind.CharacterName  => DarkTheme.FgCharacterBlue,
                    SummarySegmentKind.TeamSeparator  => DarkTheme.FgTeamSeparatorRed,
                    _                                 => ForeColor,
                };
                var size = TextRenderer.MeasureText(
                    e.Graphics, seg.Text, Font, new Size(remaining, lineH), format);
                int drawWidth = Math.Min(size.Width, remaining);
                TextRenderer.DrawText(e.Graphics, seg.Text, Font,
                    new Rectangle(x, y, drawWidth, lineH), color, format);
                x += drawWidth;
                remaining -= drawWidth;
            }
            y += lineH;
        }
    }
}

/// <summary>One visible row of the team summary (e.g. "T1: …  |  T2: …").</summary>
internal sealed record TeamSummaryRow(IReadOnlyList<TeamSummarySegment> Segments);

/// <summary>One color-tinted segment of a team-summary row.</summary>
internal sealed record TeamSummarySegment(string Text, SummarySegmentKind Kind);

/// <summary>Color routing for team-summary segments. Drives TeamSummaryLabel.OnPaint.</summary>
internal enum SummarySegmentKind
{
    /// <summary>Default (white via Label.ForeColor): row label "Tn: ", " + ", "(none)".</summary>
    Plain,
    /// <summary>Orange (FgAccountOrange): an Account-resolved slot name.</summary>
    AccountName,
    /// <summary>Blue (FgCharacterBlue): a Character-resolved slot name.</summary>
    CharacterName,
    /// <summary>Red (FgTeamSeparatorRed): the " | " boundary between two teams on the same row.</summary>
    TeamSeparator,
    /// <summary>White + trailing "?": an unresolved slot name (FK drift).</summary>
    Unresolved,
}
