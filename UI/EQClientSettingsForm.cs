// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Text;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Manages persistent eqclient.ini overrides.
/// Reads current values from eqclient.ini on open.
/// Toggle settings write both states (e.g., Sound=TRUE ↔ Sound=FALSE).
/// </summary>
public class EQClientSettingsForm : EqSwitchForm
{
    // Remembers last-open location across opens within a session. Static so
    // all instances share it; falls back to CenterScreen on first open
    // (this is a top-level utility form, not a parent-modal dialog).
    // Process lifetime only; cross-session persistence would need config.
    private static Point? _lastLocation;


    private readonly AppConfig _config;
    private readonly string _iniPath;

    private CheckBox _chkDisableSound = null!;
    private CheckBox _chkDisableMusic = null!;
    private NumericUpDown _nudSoundVolume = null!;
    private CheckBox _chkDisableEnvSounds = null!;
    private CheckBox _chkDisableCombatMusic = null!;
    private CheckBox _chkDisableAutoDuck = null!;
    private CheckBox _chkSlowSky = null!;
    private CheckBox _chkDisableSky = null!;
    private CheckBox _chkBardSongs = null!;
    private CheckBox _chkBardSongsOnPets = null!;
    private CheckBox _chkAttackOnAssist = null!;
    private CheckBox _chkShowInspectMessage = null!;
    private CheckBox _chkShowGrass = null!;
    private CheckBox _chkNetStat = null!;
    private CheckBox _chkTrackAutoUpdate = null!;
    private CheckBox _chkTargetGroupBuff = null!;
    private CheckBox _chkDisableMipMapping = null!;
    private CheckBox _chkTextureCache = null!;
    private CheckBox _chkUseD3DTextureCompression = null!;
    private CheckBox _chkDisableDynamicLights = null!;
    private CheckBox _chkUseLitBatches = null!;
    private CheckBox _chkDisableInspectOthers = null!;
    private CheckBox _chkAnonymous = null!;
    private CheckBox _chkDisableLog = null!;
    private CheckBox _chkRaidInviteConfirm = null!;
    private CheckBox _chkAANoConfirm = null!;
    private CheckBox _chkDisableChatServer = null!;
    private CheckBox _chkDisableLootAllConfirm = null!;
    private NumericUpDown _nudClipPlane = null!;
    private NumericUpDown _nudMouseSensitivity = null!;
    private NumericUpDown _nudMaxFPS = null!;
    private NumericUpDown _nudMaxBGFPS = null!;
    private NumericUpDown _nudShadowClipPlane = null!;
    private NumericUpDown _nudActorClipPlane = null!;

    // ── Phase 1: schema-driven binding (control ↔ IniSetting). The window reads/writes through
    // Config/EqClientIniSchema (single source of truth) + EqClientIniDocument (surgical, section-
    // aware ini I/O) instead of the old per-control switch statements. Polarity, section, and
    // sentinel live in the descriptor — never duplicated here.
    private readonly List<Binding> _bindings = new();

    private static readonly Dictionary<string, IniSetting> SchemaByKey =
        EqClientIniSchema.All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>A control bound to its descriptor + the canonical INI value loaded for it. The
    /// snapshot is what makes Save write ONLY keys the user actually changed (touch-gating).</summary>
    private sealed class Binding
    {
        public IniSetting Setting;
        public CheckBox? Check;
        public NumericUpDown? Numeric;
        public string LoadedValue = "";
        public Binding(IniSetting setting, CheckBox? chk, NumericUpDown? nud)
        { Setting = setting; Check = chk; Numeric = nud; }
    }

    private void Bind(CheckBox chk, string key) => _bindings.Add(new Binding(SchemaByKey[key], chk, null));
    private void Bind(NumericUpDown nud, string key) => _bindings.Add(new Binding(SchemaByKey[key], null, nud));

    /// <summary>Current control state expressed as its canonical INI string.</summary>
    private static string ReadControl(Binding b) =>
        b.Check != null ? b.Setting.ToggleToIni(b.Check.Checked) : b.Setting.NumberToIni(b.Numeric!.Value);

    /// <summary>Set the control from an INI string via the descriptor's conversion (clamped to the control's range).</summary>
    private static void ApplyControl(Binding b, string iniValue)
    {
        if (b.Check != null)
            b.Check.Checked = b.Setting.ToggleFromIni(iniValue);
        else
            b.Numeric!.Value = Math.Clamp(b.Setting.ParseNumber(iniValue), b.Numeric.Minimum, b.Numeric.Maximum);
    }

    public EQClientSettingsForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        // Restore last-open position if available; otherwise CenterScreen
        // (StyleForm's default since this is a top-level form, not parent-modal).
        if (_lastLocation.HasValue)
        {
            StartPosition = FormStartPosition.Manual;
            Location = _lastLocation.Value;
        }
        FormClosing += (_, _) => _lastLocation = Location;
        DarkTheme.StyleForm(this, "EQSwitch \u2014 EQ Client Settings \u2014 EXPERIMENTAL", new Size(750, 680));

        int y = 8;
        const int C1 = 10, C2 = 245, C3 = 480, RH = 22;

        // ─── Gameplay card (3 columns × 3 rows) ─────────────────
        var cardGame = DarkTheme.MakeCard(this, "\u2694", "Gameplay", DarkTheme.CardGreen, 10, y, 710, 108);
        int cy = 30;

        _chkAnonymous = DarkTheme.AddCardCheckBox(cardGame, "Anonymous", C1, cy);
        _chkRaidInviteConfirm = DarkTheme.AddCardCheckBox(cardGame, "Raid Invite Confirm", C2, cy);
        _chkDisableChatServer = DarkTheme.AddCardCheckBox(cardGame, "Disable Chat Server", C3, cy);
        cy += RH;
        _chkDisableLog = DarkTheme.AddCardCheckBox(cardGame, "Disable EQ Logging", C1, cy);
        _chkAANoConfirm = DarkTheme.AddCardCheckBox(cardGame, "AA No Confirm", C2, cy);
        _chkDisableLootAllConfirm = DarkTheme.AddCardCheckBox(cardGame, "Disable Loot All Confirm", C3, cy);
        cy += RH;
        _chkAttackOnAssist = DarkTheme.AddCardCheckBox(cardGame, "Attack on Assist", C1, cy);
        _chkShowInspectMessage = DarkTheme.AddCardCheckBox(cardGame, "Show Inspect Message", C2, cy);
        _chkDisableInspectOthers = DarkTheme.AddCardCheckBox(cardGame, "Disable Inspect Others", C3, cy);
        // v3.22.91: "Force Windowed Mode" checkbox removed — WindowedMode=TRUE is now
        // a pinned invariant (AppConfig.Validate), not a user-toggleable setting. The
        // ini-write below always emits TRUE.

        y += 116;

        // ─── Sound & Audio card (3 columns × 2 rows) ────────────
        var cardSound = DarkTheme.MakeCard(this, "\uD83D\uDD0A", "Sound & Audio", DarkTheme.CardGold, 10, y, 710, 85);
        cy = 30;

        _chkDisableSound = DarkTheme.AddCardCheckBox(cardSound, "Disable Sound", C1, cy);
        _chkDisableEnvSounds = DarkTheme.AddCardCheckBox(cardSound, "Disable Env Sounds", C2, cy);
        _chkDisableAutoDuck = DarkTheme.AddCardCheckBox(cardSound, "Disable Auto-Duck", C3, cy);
        cy += RH;
        _chkDisableMusic = DarkTheme.AddCardCheckBox(cardSound, "Disable Music", C1, cy);
        _chkDisableCombatMusic = DarkTheme.AddCardCheckBox(cardSound, "Disable Combat Music", C2, cy);
        DarkTheme.AddCardLabel(cardSound, "Volume:", C3, cy + 2);
        _nudSoundVolume = DarkTheme.AddCardNumeric(cardSound, C3 + 55, cy, 50, Math.Clamp(_config.EQClientIni.SoundVolume, -1, 100), -1, 100);
        DarkTheme.AddCardHint(cardSound, "(-1 = don't set)", C3 + 110, cy + 4);

        y += 93;

        // ─── Graphics & Visual card (3 columns × 5 rows) ────────
        var cardGfx = DarkTheme.MakeCard(this, "\uD83C\uDFA8", "Graphics & Visual", DarkTheme.CardBlue, 10, y, 710, 150);
        cy = 30;

        _chkSlowSky = DarkTheme.AddCardCheckBox(cardGfx, "Slow Sky Updates", C1, cy);
        _chkDisableMipMapping = DarkTheme.AddCardCheckBox(cardGfx, "Disable Mip-Mapping", C2, cy);
        _chkDisableDynamicLights = DarkTheme.AddCardCheckBox(cardGfx, "Disable Dynamic Lights", C3, cy);
        cy += RH;
        _chkDisableSky = DarkTheme.AddCardCheckBox(cardGfx, "Disable Sky", C1, cy);
        _chkTextureCache = DarkTheme.AddCardCheckBox(cardGfx, "Texture Cache", C2, cy);
        _chkUseLitBatches = DarkTheme.AddCardCheckBox(cardGfx, "Use Lit Batches", C3, cy);
        cy += RH;
        _chkShowGrass = DarkTheme.AddCardCheckBox(cardGfx, "Show Grass", C1, cy);
        _chkUseD3DTextureCompression = DarkTheme.AddCardCheckBox(cardGfx, "D3D Texture Compression", C2, cy);
        _chkNetStat = DarkTheme.AddCardCheckBox(cardGfx, "Ping Bar", C3, cy);
        cy += RH;
        _chkBardSongs = DarkTheme.AddCardCheckBox(cardGfx, "Persistent Bard Songs", C1, cy);
        _chkBardSongsOnPets = DarkTheme.AddCardCheckBox(cardGfx, "Bard Songs on Pets", C2, cy);
        _chkTrackAutoUpdate = DarkTheme.AddCardCheckBox(cardGfx, "Track Auto-Update", C3, cy);
        cy += RH;
        _chkTargetGroupBuff = DarkTheme.AddCardCheckBox(cardGfx, "Target Group Buff", C1, cy);

        y += 158;

        // ─── Performance card (numerics in aligned rows) ──────────
        // 3 columns: label, numeric, hint — spaced at 230px intervals
        var cardPerf = DarkTheme.MakeCard(this, "\uD83D\uDD27", "Performance", DarkTheme.CardPurple, 10, y, 710, 95);
        cy = 32;

        // Row 1: MaxFPS | MaxBGFPS | Mouse Sensitivity
        DarkTheme.AddCardLabel(cardPerf, "MaxFPS:", 10, cy + 2);
        _nudMaxFPS = DarkTheme.AddCardNumeric(cardPerf, 78, cy, 50, Math.Clamp(_config.EQClientIni.MaxFPS, 0, 99), 0, 99);
        DarkTheme.AddCardLabel(cardPerf, "MaxBGFPS:", 245, cy + 2);
        _nudMaxBGFPS = DarkTheme.AddCardNumeric(cardPerf, 325, cy, 50, Math.Clamp(_config.EQClientIni.MaxBGFPS, 0, 99), 0, 99);
        DarkTheme.AddCardHint(cardPerf, "(0 = don't set)", 380, cy + 4);
        DarkTheme.AddCardLabel(cardPerf, "Mouse:", 500, cy + 2);
        _nudMouseSensitivity = DarkTheme.AddCardNumeric(cardPerf, 555, cy, 50, Math.Clamp(_config.EQClientIni.MouseSensitivity, -1, 100), -1, 100);
        DarkTheme.AddCardHint(cardPerf, "(-1 = don't set)", 610, cy + 4);

        // Row 2: Clip | Shadow | Actor
        cy += RH + 6;
        DarkTheme.AddCardLabel(cardPerf, "Clip:", 10, cy + 2);
        _nudClipPlane = DarkTheme.AddCardNumeric(cardPerf, 78, cy, 55, Math.Clamp(_config.EQClientIni.ClipPlane, 0, 999), 0, 999);
        DarkTheme.AddCardHint(cardPerf, "(def 14)", 138, cy + 4);
        DarkTheme.AddCardLabel(cardPerf, "Shadow:", 245, cy + 2);
        _nudShadowClipPlane = DarkTheme.AddCardNumeric(cardPerf, 325, cy, 55, Math.Clamp(_config.EQClientIni.ShadowClipPlane, 0, 999), 0, 999);
        DarkTheme.AddCardHint(cardPerf, "(def 35)", 385, cy + 4);
        DarkTheme.AddCardLabel(cardPerf, "Actor:", 500, cy + 2);
        _nudActorClipPlane = DarkTheme.AddCardNumeric(cardPerf, 555, cy, 55, Math.Clamp(_config.EQClientIni.ActorClipPlane, 0, 999), 0, 999);
        DarkTheme.AddCardHint(cardPerf, "(def 67)", 615, cy + 4);

        y += 103;

        // ─── Related Settings card (5 buttons in a row) ──────────
        var cardSub = DarkTheme.MakeCard(this, "\uD83D\uDCC2", "Related Settings", DarkTheme.CardCyan, 10, y, 710, 65);
        cy = 30;
        const int BW = 125, BG = 12;

        var btnModels = DarkTheme.AddCardButton(cardSub, "\uD83C\uDFAD Models", C1, cy, BW);
        btnModels.Click += (_, _) => { using var f = new EQModelsForm(_config); f.ShowDialog(this); };

        var btnChatSpam = DarkTheme.AddCardButton(cardSub, "\uD83D\uDCAC Chat Spam", C1 + BW + BG, cy, BW);
        btnChatSpam.Click += (_, _) => { using var f = new EQChatSpamForm(_config); f.ShowDialog(this); };

        var btnParticles = DarkTheme.AddCardButton(cardSub, "\u2728 Particles", C1 + (BW + BG) * 2, cy, BW);
        btnParticles.Click += (_, _) => { using var f = new EQParticlesForm(_config); f.ShowDialog(this); };

        var btnVideoMode = DarkTheme.AddCardButton(cardSub, "\uD83D\uDCFA Video Mode", C1 + (BW + BG) * 3, cy, BW);
        btnVideoMode.Click += (_, _) => { using var f = new EQVideoModeForm(_config); f.ShowDialog(this); };

        var btnKeymaps = DarkTheme.AddCardButton(cardSub, "\u2328 Keymaps", C1 + (BW + BG) * 4, cy, BW);
        btnKeymaps.Click += (_, _) => { using var f = new EQKeymapsForm(_config); f.ShowDialog(this); };

        // ─── Docked bottom panel with Save/Apply/Cancel ──────────
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            BackColor = DarkTheme.BgDark
        };

        var btnSave = DarkTheme.MakePrimaryButton("Save", 260, 10);
        btnSave.Click += (_, _) => { SaveSettings(); Close(); };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 350, 10);
        btnApply.Click += (_, _) => { SaveSettings(); };

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 440, 10);
        btnCancel.Click += (_, _) => Close();

        buttonPanel.Controls.AddRange(new Control[] { btnSave, btnApply, btnCancel });
        Controls.Add(buttonPanel);

        // ── Phase 1: register every control against its schema descriptor (single source of truth).
        // LoadFromIni / SaveSettings iterate _bindings — no per-control switch statements.
        Bind(_chkAnonymous, "Anonymous");
        Bind(_chkRaidInviteConfirm, "RaidInviteConfirm");
        Bind(_chkDisableChatServer, "ChatServerPort");
        Bind(_chkDisableLog, "Log");
        Bind(_chkAANoConfirm, "AANoConfirm");
        Bind(_chkDisableLootAllConfirm, "LootAllConfirm");
        Bind(_chkAttackOnAssist, "AttackOnAssist");
        Bind(_chkShowInspectMessage, "ShowInspectMessage");
        Bind(_chkDisableInspectOthers, "InspectOthers");
        Bind(_chkDisableSound, "Sound");
        Bind(_chkDisableEnvSounds, "EnvSounds");
        Bind(_chkDisableAutoDuck, "AllowAutoDuck");
        Bind(_chkDisableMusic, "Music");
        Bind(_chkDisableCombatMusic, "CombatMusic");
        Bind(_nudSoundVolume, "SoundVolume");
        Bind(_chkSlowSky, "SkyUpdateInterval");
        Bind(_chkDisableMipMapping, "MipMapping");
        Bind(_chkDisableDynamicLights, "ShowDynamicLights");
        Bind(_chkDisableSky, "Sky");
        Bind(_chkTextureCache, "TextureCache");
        Bind(_chkUseLitBatches, "UseLitBatches");
        Bind(_chkShowGrass, "ShowGrass");
        Bind(_chkUseD3DTextureCompression, "UseD3DTextureCompression");
        Bind(_chkNetStat, "NetStat");
        Bind(_chkBardSongs, "BardSongs");
        Bind(_chkBardSongsOnPets, "BardSongsOnPets");
        Bind(_chkTrackAutoUpdate, "TrackAutoUpdate");
        Bind(_chkTargetGroupBuff, "TargetGroupBuff");
        Bind(_nudMaxFPS, "MaxFPS");
        Bind(_nudMaxBGFPS, "MaxBGFPS");
        Bind(_nudMouseSensitivity, "MouseSensitivity");
        Bind(_nudClipPlane, "ClipPlane");
        Bind(_nudShadowClipPlane, "ShadowClipPlane");
        Bind(_nudActorClipPlane, "ActorClipPlane");

        // Size the form to its content so the button bar sits a consistent gap below the last card,
        // instead of the old hand-guessed Size(750,680) that left ~87px of dead space above the buttons.
        FitClientHeightToContent();
    }

    /// <summary>
    /// Read current values from eqclient.ini and update checkboxes to reflect actual INI state.
    /// </summary>
    private void LoadFromIni()
    {
        // Phase 1: display is a LIVE read of eqclient.ini, schema-driven. Every control reflects
        // the actual on-disk value (so in-game/eqgame changes always show); a key absent from the
        // INI falls back to the descriptor's Default. The loaded value is snapshotted per binding
        // so Save writes ONLY what the user changes (touch-gating).
        try
        {
            var doc = EqClientIniDocument.Load(_iniPath);
            foreach (var b in _bindings)
            {
                string value = doc.Get(b.Setting) ?? b.Setting.Default;
                ApplyControl(b, value);
                b.LoadedValue = ReadControl(b);
            }
            FileLogger.Info("EQClientSettings: loaded current values from eqclient.ini (schema-driven)");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQClientSettings: load error", ex);
        }
    }

    private void SaveSettings()
    {
        // ProcessManager is the only other reader of EQClientIni props (it shows MaxFPS/MaxBGFPS),
        // so keep those mirrored into the JSON config. Every other setting is owned by eqclient.ini
        // now — the form is a live editor of the file, not a mirror of the config model.
        _config.EQClientIni.MaxFPS = (int)_nudMaxFPS.Value;
        _config.EQClientIni.MaxBGFPS = (int)_nudMaxBGFPS.Value;
        ConfigManager.Save(_config);

        // Authoritative write: live INI, schema-driven, touch-gated. Only keys the user actually
        // changed (vs the snapshot LoadFromIni took) are written, each to its ONE canonical section
        // via the surgical engine. Untouched keys — and any key EQSwitch doesn't manage — are left
        // exactly as eqgame/the user left them (point D: no clobber; point I: no ghosts).
        try
        {
            if (!File.Exists(_iniPath))
            {
                FileLogger.Info($"EQClientSettings: eqclient.ini not found at {_iniPath}");
                return;
            }

            var doc = EqClientIniDocument.Load(_iniPath);
            int changed = 0;
            foreach (var b in _bindings)
            {
                // Numeric sentinel (e.g. SoundVolume -1, ClipPlane 0) means "don't set" — never write.
                if (b.Numeric != null && !b.Setting.ShouldWriteNumber(b.Numeric.Value))
                    continue;

                string current = ReadControl(b);
                if (current == b.LoadedValue) continue;   // untouched — leave it alone

                doc.Write(b.Setting, current);            // canonical section (+ any mirror sections)
                b.LoadedValue = current;                  // refresh snapshot so Apply doesn't re-write
                changed++;
            }

            if (changed > 0)
            {
                doc.Save(_iniPath);
                FileLogger.Info($"EQClientSettings: wrote {changed} changed setting(s) to eqclient.ini (touch-gated)");
            }
            else
            {
                FileLogger.Info("EQClientSettings: no changes to save");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQClientSettings: save error", ex);
            ThemedMessageDialog.Show(this, $"Failed to update eqclient.ini: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ApplyToIni removed in Phase 1 — SaveSettings now writes via EqClientIniDocument (touch-gated,
    // schema-driven, one canonical section per key). The old method wrote all keys unconditionally
    // with hardcoded per-control polarity (the ghost/clobber source this overhaul eliminates).

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }

    /// <summary>
    /// v3.22.45 — return the number of pixels of caption that will be VISIBLE
    /// inside the monitor after <c>WindowManager.ApplySlimTitlebar</c> runs.
    /// This is the value EnforceOverrides must subtract from monitor height
    /// when writing <c>WindowedHeight</c> to <c>eqclient.ini</c>, so the
    /// game's DX swap chain matches the visible client area exactly.
    /// <para>
    /// Re-probes the actual non-client bleed for the slim-titlebar style on
    /// the live OS (Win10 caption ~22 px / Win11 caption ~31 px) so the
    /// formula stays correct across Windows versions without hard-coded
    /// constants. Falls back to <paramref name="titlebarOffset"/> if the
    /// AdjustWindowRectEx probe fails — same behaviour as
    /// WindowManager.ComputeSlimTitlebarOuterRect's fallback path.
    /// </para>
    /// </summary>
    internal static int SlimTitlebarCaptionVisible(int titlebarOffset, WindowMode mode = WindowMode.Fullscreen)
    {
        // v3.22.82 — probe the style for the ACTUAL mode. Fullscreen = WS_POPUP
        // (no caption → AdjustWindowRectEx 0/0/0/0 → captionVisible always 0).
        // Windowed = WS_CAPTION (~31px Win11 caption → captionVisible =
        // clamp(offset, 0, 31)). Pre-v3.22.82 this hardcoded WS_POPUP, so it
        // returned 0 even in Windowed mode — which is exactly why the Windowed INI
        // path rendered at full monH and pushed the bottom off-screen. ProbeStyleFor
        // routes the right style so the INI WindowedHeight (monH - captionVisible)
        // matches the reduced Windowed client area 1:1 (crisp, no stretch seam).
        long slimStyle = Core.WindowManager.ProbeStyleFor(mode);

        var probe = new Core.NativeMethods.RECT { Left = 0, Top = 0, Right = 100, Bottom = 100 };
        if (!Core.NativeMethods.AdjustWindowRectEx(ref probe, (uint)slimStyle, false, 0))
            // v3.22.82 — probe failed: degrade to 0 (no caption peek). This MUST
            // match WindowManager.ComputeSlimTitlebarOuterRect's own fallback,
            // which returns plain monitor edges with captionVisible NOT applied
            // (client ≈ monH). Returning `titlebarOffset` here instead would write
            // INI WindowedHeight = monH-offset while the client stays ~monH — an
            // offset-px backbuffer/client mismatch → the exact stretch seam this
            // feature exists to prevent. Both fall back to "fill monitor, no peek"
            // so backbuffer and client stay matched. (Verifier-swarm convergent
            // finding, 2026-05-30 — never-hit for valid WS_CAPTION styles, but a
            // graceful fallback must degrade consistently to be graceful.)
            return 0;

        int topBleed = -probe.Top;
        return Math.Clamp(titlebarOffset, 0, topBleed);
    }

    /// <summary>
    /// v3.22.46 — return the visible (width, height) of the slim-titlebar
    /// window's client area on the target monitor, accounting for adjacent-
    /// monitor bleed clipping. EnforceOverrides writes these as
    /// <c>WindowedWidth</c>/<c>WindowedHeight</c> in <c>eqclient.ini</c> so
    /// EQ's DX swap chain matches the visible client edge-to-edge — no
    /// bilinear smear when adjacency clips the outer rect inward.
    /// </summary>
    /// <remarks>
    /// Falls back to full monitor width / monH-captionVisible if the
    /// AdjustWindowRectEx probe fails (same shape as the pre-v3.22.46 math).
    /// </remarks>
    internal static (int width, int height) SlimTitlebarVisibleClientSize(
        System.Drawing.Rectangle monitorBounds, int titlebarOffset)
    {
        // v3.22.76 — WS_POPUP → bleed 0/0/0/0 → width = monW, height = monH.
        // EnforceOverrides writes WindowedWidth=monW + WindowedHeight=monH to
        // eqclient.ini so EQ's DX swap chain renders at native resolution
        // (no bilinear stretch → no font distortion).
        long slimStyle = Core.WindowManager.SLIM_TITLEBAR_STYLE;

        var probe = new Core.NativeMethods.RECT { Left = 0, Top = 0, Right = 100, Bottom = 100 };
        if (!Core.NativeMethods.AdjustWindowRectEx(ref probe, (uint)slimStyle, false, 0))
            return (monitorBounds.Width, Math.Max(0, monitorBounds.Height - Math.Max(0, titlebarOffset)));

        int leftBleed   = -probe.Left;
        int topBleed    = -probe.Top;
        int rightBleed  = probe.Right - 100;
        int bottomBleed = probe.Bottom - 100;

        // Translate Screen.AllScreens to WinRect list so the WindowManager
        // adjacency clamp helper can be shared with the runtime sizing path.
        var monitorWinRect = new Core.WinRect
        {
            Left = monitorBounds.Left, Top = monitorBounds.Top,
            Right = monitorBounds.Right, Bottom = monitorBounds.Bottom
        };
        var all = new List<Core.WinRect>();
        foreach (var s in Screen.AllScreens)
        {
            all.Add(new Core.WinRect
            {
                Left = s.Bounds.Left, Top = s.Bounds.Top,
                Right = s.Bounds.Right, Bottom = s.Bounds.Bottom
            });
        }
        var (effLeft, effRight, effBottom) = Core.WindowManager.ClampBleedsForAdjacency(
            monitorWinRect, all, leftBleed, rightBleed, bottomBleed);

        int captionVisible = Math.Clamp(titlebarOffset, 0, topBleed);
        int width  = monitorBounds.Width  - (leftBleed - effLeft) - (rightBleed - effRight);
        int height = monitorBounds.Height - captionVisible        - (bottomBleed - effBottom);
        return (width, height);
    }

    /// <summary>
    /// Set a key=value in the specified section of an INI file.
    /// Creates the section if it doesn't exist.
    /// </summary>
    public static void SetIniValue(List<string> lines, string section, string key, string value)
    {
        string sectionHeader = $"[{section}]";
        int sectionStart = -1;
        int sectionEnd = lines.Count;

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
                sectionStart = i;
            else if (sectionStart >= 0 && trimmed.StartsWith("["))
            {
                sectionEnd = i;
                break;
            }
        }

        if (sectionStart >= 0)
        {
            for (int i = sectionStart + 1; i < sectionEnd; i++)
            {
                var parts = lines[i].Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{key}={value}";
                    return;
                }
            }
            // Key not found in section — insert before section end
            lines.Insert(sectionEnd, $"{key}={value}");
        }
        else
        {
            // Section doesn't exist — append
            lines.Add("");
            lines.Add(sectionHeader);
            lines.Add($"{key}={value}");
        }
    }

    /// <summary>
    /// Static helper: enforce all enabled overrides in eqclient.ini.
    /// Called by TrayManager after launching clients or on demand.
    /// Only writes settings the user has explicitly saved (ConfiguredKeys).
    /// Fresh install = empty set = nothing enforced until first Save.
    /// </summary>
    public static void EnforceOverrides(AppConfig config)
    {
        string iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        if (!File.Exists(iniPath)) return;

        try
        {
            var lines = File.ReadAllLines(iniPath, Encoding.Default).ToList();
            // ── Phase 1: Bucket-2 main-form settings (sound/graphics/gameplay/perf) are NO LONGER
            // re-enforced at launch. They're written only at Save (touch-gated, via the schema
            // engine), so in-game changes that eqgame persists on exit survive — point D / eqgame-
            // wins. Only Operational window-manager keys are enforced here. (Sub-form Bucket-2 dicts
            // below are still enforced until their own phases retire them.)
            //
            // WindowedMode is a pinned invariant (AppConfig.Validate); Maximized comes from config.
            // Written unconditionally (Operational) — the slim-titlebar block below may override
            // Maximized to 0 when slim mode is on.
            string maxVal = config.EQClientIni.MaximizeWindow ? "1" : "0";
            SetIniValue(lines, "Defaults", "WindowedMode", "TRUE");
            SetIniValue(lines, "VideoMode", "WindowedMode", "TRUE");
            SetIniValue(lines, "Defaults", "Maximized", maxVal);
            SetIniValue(lines, "VideoMode", "Maximized", maxVal);

            // Slim Titlebar requires: WindowedMode=TRUE, Maximized=0,
            // and resolution matching the target monitor so the game + titlebar
            // extends below the screen edge (WinEQ2 method).
            if (config.Layout.SlimTitlebar)
            {
                // v3.22.89 — resolve TargetMonitor against the SAME monitor list +
                // ordering the window positioner uses (WindowsApi.GetAllMonitorBounds,
                // sorted left-to-right by .Left). Screen.AllScreens is NOT sorted, so on
                // a multi-monitor desktop index N pointed at a DIFFERENT physical monitor
                // here than in WindowManager.GetTargetMonitor: EnforceOverrides wrote
                // WindowedHeight for one screen while the window actually opened on
                // another, stretching EQ's DX backbuffer into a mismatched client
                // (2026-05-31 natedogg smush — INI WindowedHeight 1187 for the 1920×1200
                // screen vs the 1920×1080 primary the window used). Sharing the source
                // keeps backbuffer == visible client → crisp.
                var api = new WindowsApi();
                var fullBounds = api.GetAllMonitorBounds();
                var workAreas = api.GetAllMonitorWorkAreas();
                System.Drawing.Rectangle screenBounds;
                if (fullBounds.Count > 0)
                {
                    int targetIdx = Math.Clamp(config.Layout.TargetMonitor, 0, fullBounds.Count - 1);
                    // v3.24.10 — the eqclient.ini backbuffer is the SHARED initial render size for
                    // BOTH clients, so it seeds from the single sizing authority's PRIMARY slot,
                    // which is ALWAYS the primary monitor's FULL bounds (the main window covers its
                    // taskbar in both modes). On mismatched monitors the SECONDARY window is a
                    // different size — its backbuffer is corrected at runtime by the native resize
                    // + the Windowed read-back (same path the old degrade case used). Single-screen
                    // keeps the target monitor's full bounds.
                    bool isMM = config.Layout.Mode.Equals("multimonitor", StringComparison.OrdinalIgnoreCase);
                    Core.WinRect primaryFull = fullBounds[targetIdx];
                    Core.WinRect primaryWork = (workAreas.Count == fullBounds.Count) ? workAreas[targetIdx] : fullBounds[targetIdx];
                    Core.WinRect eff = isMM
                        ? Core.WindowManager.EffectiveSlotBounds(0, primaryFull, primaryWork, null, null, config.Layout.MultiMonTaskbarMode).bounds
                        : primaryFull;
                    screenBounds = new System.Drawing.Rectangle(eff.Left, eff.Top, eff.Width, eff.Height);
                }
                else
                {
                    screenBounds = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
                }
                int monW = screenBounds.Width;
                int monH = screenBounds.Height;
                int offset = config.Layout.TitlebarOffset;

                // v3.22.46: WindowedWidth / WindowedHeight must match the
                // VISIBLE client area of the slim-titlebar window, which is
                // adjacency-aware as of v3.22.46. On edges where another
                // monitor abuts the target monitor, the outer rect is clipped
                // (no bleed onto neighbor) and the visible client loses
                // `bleed` px on that edge — so WindowedWidth shrinks too,
                // keeping the DX swap-chain 1:1 with the visible client (no
                // bilinear-stretch smear). On non-adjacent edges the bleed
                // sits off-desktop (invisible) and visible client still
                // reaches the monitor edge.
                //
                // v3.22.45 wrote (monW, monH - captionVisible) here, which
                // matched the OUTER-extends-past-monitor strategy. With
                // adjacency clipping in WindowManager, the outer no longer
                // extends past on adjacent sides, so the INI dims must
                // follow or DX will smear the rendered frame.
                // v3.22.81 — both modes render at NATIVE resolution. Fullscreen
                // (WS_POPUP) fills the monitor exactly. Windowed (WS_CAPTION)
                // peeks the caption at the top and OVERFLOWS the bottom edge, so
                // the client is still monW×monH → EQ's DX swap chain renders 1:1
                // → crisp bitmap fonts (the v3.22.76 font-seam fix, now kept WITH
                // a visible titlebar). Fullscreen path is unchanged
                // (SlimTitlebarVisibleClientSize returns native for WS_POPUP).
                // (Single-monitor overflows freely; multi-monitor side-adjacency
                // may clip a side — tune in smoke, see spec §7.4.)
                int captionVisible = SlimTitlebarCaptionVisible(offset, config.Layout.WindowMode);
                int gameW, gameH;
                if (config.Layout.WindowMode == WindowMode.Windowed)
                {
                    // v3.22.82 — render height = monH - caption peek so EQ's DX
                    // backbuffer matches the (reduced) Windowed client area 1:1
                    // (crisp) AND the window's bottom stays flush (WinEQ2 method;
                    // see WindowManager.ComputeOuterRectFromBleeds). Width fills the
                    // monitor (flush sides via the GeoWndProc subclass). Was monH
                    // (v3.22.81 native) which forced the bottom captionVisible px
                    // off-screen — the regression Nate caught 2026-05-30.
                    gameW = monW;
                    gameH = monH - captionVisible;
                }
                else
                {
                    (gameW, gameH) = SlimTitlebarVisibleClientSize(screenBounds, offset);
                }

                SetIniValue(lines, "Defaults", "WindowedMode", "TRUE");
                SetIniValue(lines, "VideoMode", "WindowedMode", "TRUE");
                SetIniValue(lines, "Defaults", "Maximized", "0");
                SetIniValue(lines, "VideoMode", "Maximized", "0");
                SetIniValue(lines, "VideoMode", "Width", monW.ToString());
                SetIniValue(lines, "VideoMode", "Height", monH.ToString());
                SetIniValue(lines, "VideoMode", "WindowedWidth", gameW.ToString());
                SetIniValue(lines, "VideoMode", "WindowedHeight", gameH.ToString());
                SetIniValue(lines, "Defaults", "WindowedWidth", gameW.ToString());
                SetIniValue(lines, "Defaults", "WindowedHeight", gameH.ToString());
                SetIniValue(lines, "Defaults", "WindowedModeXOffset", "0");
                SetIniValue(lines, "Defaults", "WindowedModeYOffset", "0");
                SetIniValue(lines, "VideoMode", "WindowedModeXOffset", "0");
                SetIniValue(lines, "VideoMode", "WindowedModeYOffset", "0");
                // Unbind Alt+N (Story Window) in EQ so it doesn't conflict with
                // our multi-monitor toggle hotkey. 0 = unbound.
                SetIniValue(lines, "Defaults", "KEYMAPPING_TOGGLE_STORYWIN_1", "0");
                SetIniValue(lines, "Defaults", "KEYMAPPING_TOGGLE_STORYWIN_2", "0");
                FileLogger.Info($"EnforceOverrides: SlimTitlebar ON (mode={config.Layout.WindowMode}) → WindowedWidth/Height {gameW}x{gameH} (monitor {monW}x{monH}, captionVisible {captionVisible}px), Maximized=0, WindowedMode=TRUE, Story Window unbound");
            }

            // Phase 1: MaxFPS/MaxBGFPS/Shadow/Actor/Log no longer enforced at launch (Bucket-2,
            // save-time only). ProcessManager still writes FPS + affinity via ApplyProcessManagerToIni.

            // Enforce sub-form overrides (dictionary-based — already only write what user saved)
            EQModelsForm.EnforceOverrides(config, lines);
            EQChatSpamForm.EnforceOverrides(config, lines);
            EQParticlesForm.EnforceOverrides(config, lines);
            EQVideoModeForm.EnforceOverrides(config, lines);

            File.WriteAllLines(iniPath, lines, Encoding.Default);
            FileLogger.Info("EQClientSettings: enforced persistent overrides");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQClientSettings: enforce error", ex);
        }
    }

    /// <summary>
    /// Write MaxFPS/MaxBGFPS and CPUAffinity0-5 to eqclient.ini.
    /// Called from Process Manager on Save.
    /// </summary>
    public static void ApplyProcessManagerToIni(AppConfig config)
    {
        var iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        if (!File.Exists(iniPath)) return;

        var lines = File.ReadAllLines(iniPath, Encoding.Default).ToList();

        // FPS — write to both [Defaults] and [Options] since EQ reads from [Options] at runtime
        if (config.EQClientIni.MaxFPS > 0)
        {
            SetIniValue(lines, "Defaults", "MaxFPS", config.EQClientIni.MaxFPS.ToString());
            SetIniValue(lines, "Options", "MaxFPS", config.EQClientIni.MaxFPS.ToString());
        }
        if (config.EQClientIni.MaxBGFPS > 0)
        {
            SetIniValue(lines, "Defaults", "MaxBGFPS", config.EQClientIni.MaxBGFPS.ToString());
            SetIniValue(lines, "Options", "MaxBGFPS", config.EQClientIni.MaxBGFPS.ToString());
        }

        // CPU Affinity slots
        var slots = config.EQClientIni.CPUAffinitySlots;
        for (int i = 0; i < 6; i++)
        {
            int core = i < slots.Length ? slots[i] : i;
            SetIniValue(lines, "Defaults", $"CPUAffinity{i}", core.ToString());
        }

        File.WriteAllLines(iniPath, lines, Encoding.Default);
        FileLogger.Info($"EQClientSettings: Process Manager settings written (FPS={config.EQClientIni.MaxFPS}/{config.EQClientIni.MaxBGFPS}, Affinity=[{string.Join(",", slots)}])");
    }
}
