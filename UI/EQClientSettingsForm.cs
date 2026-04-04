using System.Text;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Manages persistent eqclient.ini overrides.
/// Reads current values from eqclient.ini on open.
/// Toggle settings write both states (e.g., Sound=TRUE ↔ Sound=FALSE).
/// </summary>
public class EQClientSettingsForm : Form
{
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
    private CheckBox _chkForceWindowed = null!;
    private NumericUpDown _nudMaxFPS = null!;
    private NumericUpDown _nudMaxBGFPS = null!;
    private NumericUpDown _nudShadowClipPlane = null!;
    private NumericUpDown _nudActorClipPlane = null!;

    // Original INI values — restored when toggle is unchecked
    private string _origSkyInterval = "";

    public EQClientSettingsForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "EQSwitch \u2014 EQ Client Settings", new Size(750, 680));

        int y = 8;
        const int C1 = 10, C2 = 245, C3 = 480, RH = 22;

        // ─── Gameplay card (3 columns × 4 rows) ─────────────────
        var cardGame = DarkTheme.MakeCard(this, "\u2694", "Gameplay", DarkTheme.CardGreen, 10, y, 710, 130);
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
        cy += RH;
        _chkForceWindowed = DarkTheme.AddCardCheckBox(cardGame, "Force Windowed Mode", C1, cy);

        y += 138;

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
        DarkTheme.AddCardHint(cardSound, "(-1 = skip)", C3 + 110, cy + 4);

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
        DarkTheme.AddCardHint(cardPerf, "(-1 = skip)", 610, cy + 4);

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

        var btnSave = DarkTheme.MakePrimaryButton("Save", 250, 10);
        btnSave.Click += (_, _) => { SaveSettings(); Close(); };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 340, 10);
        btnApply.Click += (_, _) => { SaveSettings(); };

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 430, 10);
        btnCancel.Click += (_, _) => Close();

        buttonPanel.Controls.AddRange(new Control[] { btnSave, btnApply, btnCancel });
        Controls.Add(buttonPanel);
    }

    /// <summary>
    /// Read current values from eqclient.ini and update checkboxes to reflect actual INI state.
    /// </summary>
    private void LoadFromIni()
    {
        if (!File.Exists(_iniPath)) return;

        try
        {
            var lines = File.ReadAllLines(_iniPath, Encoding.Default);
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

                if (currentSection.Equals("[Defaults]", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "sound":
                            _chkDisableSound.Checked = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "music":
                            _chkDisableMusic.Checked = val == "0";
                            break;
                        case "soundvolume":
                            if (int.TryParse(val, out int svol))
                                _nudSoundVolume.Value = Math.Clamp(svol, -1, 100);
                            break;
                        case "envsounds":
                            _chkDisableEnvSounds.Checked = val == "0";
                            break;
                        case "combatmusic":
                            _chkDisableCombatMusic.Checked = val == "0";
                            break;
                        case "allowautoduck":
                            _chkDisableAutoDuck.Checked = val == "0";
                            break;
                        case "sky":
                            _chkDisableSky.Checked = val == "0";
                            break;
                        case "bardsongs":
                            _chkBardSongs.Checked = val == "1";
                            break;
                        case "bardsongsonpets":
                            _chkBardSongsOnPets.Checked = val == "1";
                            break;
                        case "attackonassist":
                            _chkAttackOnAssist.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "showinspectmessage":
                            _chkShowInspectMessage.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "showgrass":
                            _chkShowGrass.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "netstat":
                            _chkNetStat.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "trackautoupdate":
                            _chkTrackAutoUpdate.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "targetgroupbuff":
                            _chkTargetGroupBuff.Checked = val == "1";
                            break;
                        case "mipmapping":
                            _chkDisableMipMapping.Checked = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "texturecache":
                            _chkTextureCache.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "used3dtexturecompression":
                            _chkUseD3DTextureCompression.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "showdynamiclights":
                            _chkDisableDynamicLights.Checked = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "uselitbatches":
                            _chkUseLitBatches.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "inspectothers":
                            _chkDisableInspectOthers.Checked = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "anonymous":
                            _chkAnonymous.Checked = val == "1";
                            break;
                        case "raidinviteconfirm":
                            _chkRaidInviteConfirm.Checked = val == "1";
                            break;
                        case "aanoconfirm":
                            _chkAANoConfirm.Checked = val == "0";
                            break;
                        case "chatserverport":
                            _chkDisableChatServer.Checked = val == "0";
                            break;
                        case "lootallconfirm":
                            _chkDisableLootAllConfirm.Checked = val == "0";
                            break;
                        case "clipplane":
                            if (int.TryParse(val, out int cp))
                                _nudClipPlane.Value = Math.Clamp(cp, 0, 999);
                            break;
                        case "mousesensitivity":
                            if (int.TryParse(val, out int ms))
                                _nudMouseSensitivity.Value = Math.Clamp(ms, -1, 100);
                            break;
                        case "skyupdateinterval":
                            if (val == "60000")
                                _chkSlowSky.Checked = true;
                            else
                            {
                                _chkSlowSky.Checked = false;
                                _origSkyInterval = val; // remember the non-slow value
                            }
                            break;
                        case "maxfps":
                            if (int.TryParse(val, out int fps))
                                _nudMaxFPS.Value = Math.Clamp(fps, 0, 99);
                            break;
                        case "maxbgfps":
                            if (int.TryParse(val, out int bgfps))
                                _nudMaxBGFPS.Value = Math.Clamp(bgfps, 0, 99);
                            break;
                        case "shadowclipplane":
                            if (int.TryParse(val, out int scp))
                                _nudShadowClipPlane.Value = Math.Clamp(scp, 0, 999);
                            break;
                        case "actorclipplane":
                            if (int.TryParse(val, out int acp))
                                _nudActorClipPlane.Value = Math.Clamp(acp, 0, 999);
                            break;
                        case "windowedmode":
                            _chkForceWindowed.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "log":
                            _chkDisableLog.Checked = val.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                            break;
                    }
                }
                else if (currentSection.Equals("[Options]", StringComparison.OrdinalIgnoreCase))
                {
                    // [Options] is EQ's runtime-authoritative section — overrides [Defaults]
                    switch (key.ToLowerInvariant())
                    {
                        case "anonymous":
                            _chkAnonymous.Checked = val == "1";
                            break;
                        case "sky":
                            _chkDisableSky.Checked = val == "0";
                            break;
                        case "bardsongs":
                            _chkBardSongs.Checked = val == "1";
                            break;
                        case "bardsongsonpets":
                            _chkBardSongsOnPets.Checked = val == "1";
                            break;
                        case "clipplane":
                            if (int.TryParse(val, out int optCp))
                                _nudClipPlane.Value = Math.Clamp(optCp, 0, 999);
                            break;
                        case "mousesensitivity":
                            if (int.TryParse(val, out int optMs))
                                _nudMouseSensitivity.Value = Math.Clamp(optMs, -1, 100);
                            break;
                        case "shadowclipplane":
                            if (int.TryParse(val, out int optScp))
                                _nudShadowClipPlane.Value = Math.Clamp(optScp, 0, 999);
                            break;
                        case "actorclipplane":
                            if (int.TryParse(val, out int optAcp))
                                _nudActorClipPlane.Value = Math.Clamp(optAcp, 0, 999);
                            break;
                        case "maxfps":
                            if (int.TryParse(val, out int optFps))
                                _nudMaxFPS.Value = Math.Clamp(optFps, 0, 99);
                            break;
                        case "maxbgfps":
                            if (int.TryParse(val, out int optBgfps))
                                _nudMaxBGFPS.Value = Math.Clamp(optBgfps, 0, 99);
                            break;
                        case "lootallconfirm":
                            _chkDisableLootAllConfirm.Checked = val == "0";
                            break;
                        case "raidinviteconfirm":
                            _chkRaidInviteConfirm.Checked = val == "1";
                            break;
                        case "aanoconfirm":
                            _chkAANoConfirm.Checked = val == "0";
                            break;
                        case "chatserverport":
                            _chkDisableChatServer.Checked = val == "0";
                            break;
                    }
                }
                else if (currentSection.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase))
                {
                    // [VideoMode] overrides [Defaults] for WindowedMode if present
                    if (key.Equals("WindowedMode", StringComparison.OrdinalIgnoreCase))
                        _chkForceWindowed.Checked = val.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                }
            }

            FileLogger.Info("EQClientSettings: loaded current values from eqclient.ini");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQClientSettings: load error", ex);
        }
    }

    private void SaveSettings()
    {
        _config.EQClientIni.DisableSound = _chkDisableSound.Checked;
        _config.EQClientIni.DisableMusic = _chkDisableMusic.Checked;
        _config.EQClientIni.SoundVolume = (int)_nudSoundVolume.Value;
        _config.EQClientIni.DisableEnvSounds = _chkDisableEnvSounds.Checked;
        _config.EQClientIni.DisableCombatMusic = _chkDisableCombatMusic.Checked;
        _config.EQClientIni.DisableAutoDuck = _chkDisableAutoDuck.Checked;
        _config.EQClientIni.SlowSkyUpdates = _chkSlowSky.Checked;
        _config.EQClientIni.DisableSky = _chkDisableSky.Checked;
        _config.EQClientIni.BardSongs = _chkBardSongs.Checked;
        _config.EQClientIni.BardSongsOnPets = _chkBardSongsOnPets.Checked;
        _config.EQClientIni.AttackOnAssist = _chkAttackOnAssist.Checked;
        _config.EQClientIni.ShowInspectMessage = _chkShowInspectMessage.Checked;
        _config.EQClientIni.ShowGrass = _chkShowGrass.Checked;
        _config.EQClientIni.NetStat = _chkNetStat.Checked;
        _config.EQClientIni.TrackAutoUpdate = _chkTrackAutoUpdate.Checked;
        _config.EQClientIni.TargetGroupBuff = _chkTargetGroupBuff.Checked;
        _config.EQClientIni.DisableMipMapping = _chkDisableMipMapping.Checked;
        _config.EQClientIni.TextureCache = _chkTextureCache.Checked;
        _config.EQClientIni.UseD3DTextureCompression = _chkUseD3DTextureCompression.Checked;
        _config.EQClientIni.DisableDynamicLights = _chkDisableDynamicLights.Checked;
        _config.EQClientIni.UseLitBatches = _chkUseLitBatches.Checked;
        _config.EQClientIni.DisableInspectOthers = _chkDisableInspectOthers.Checked;
        _config.EQClientIni.Anonymous = _chkAnonymous.Checked;
        _config.EQClientIni.DisableEQLog = _chkDisableLog.Checked;
        _config.EQClientIni.RaidInviteConfirm = _chkRaidInviteConfirm.Checked;
        _config.EQClientIni.AANoConfirm = _chkAANoConfirm.Checked;
        _config.EQClientIni.DisableChatServer = _chkDisableChatServer.Checked;
        _config.EQClientIni.DisableLootAllConfirm = _chkDisableLootAllConfirm.Checked;
        _config.EQClientIni.ForceWindowedMode = _chkForceWindowed.Checked;
        _config.EQClientIni.MaxFPS = (int)_nudMaxFPS.Value;
        _config.EQClientIni.MaxBGFPS = (int)_nudMaxBGFPS.Value;
        _config.EQClientIni.ClipPlane = (int)_nudClipPlane.Value;
        _config.EQClientIni.ShadowClipPlane = (int)_nudShadowClipPlane.Value;
        _config.EQClientIni.ActorClipPlane = (int)_nudActorClipPlane.Value;
        _config.EQClientIni.MouseSensitivity = (int)_nudMouseSensitivity.Value;

        // Mark all settings as explicitly configured — EnforceOverrides will only write these.
        // Sentinel values (-1 or 0 depending on field) mean "don't touch" — remove from
        // ConfiguredKeys so EnforceOverrides doesn't claim ownership of a setting it won't write.
        var keys = _config.EQClientIni.ConfiguredKeys;
        keys.UnionWith(new[]
        {
            "Sound", "Music", "EnvSounds", "CombatMusic", "AllowAutoDuck",
            "Sky", "SkyUpdateInterval", "BardSongs", "BardSongsOnPets",
            "AttackOnAssist", "ShowInspectMessage", "ShowGrass", "NetStat",
            "TrackAutoUpdate", "TargetGroupBuff", "MipMapping", "TextureCache",
            "UseD3DTextureCompression", "ShowDynamicLights", "UseLitBatches",
            "InspectOthers", "Anonymous", "RaidInviteConfirm", "AANoConfirm",
            "ChatServerPort", "LootAllConfirm", "WindowedMode", "Log"
        });

        // Numeric settings: add to ConfiguredKeys only when non-sentinel
        void Track(string key, bool active) { if (active) keys.Add(key); else keys.Remove(key); }
        Track("SoundVolume", _config.EQClientIni.SoundVolume >= 0);
        Track("MouseSensitivity", _config.EQClientIni.MouseSensitivity >= 0);
        Track("ClipPlane", _config.EQClientIni.ClipPlane > 0);
        Track("ShadowClipPlane", _config.EQClientIni.ShadowClipPlane > 0);
        Track("ActorClipPlane", _config.EQClientIni.ActorClipPlane > 0);
        Track("MaxFPS", _config.EQClientIni.MaxFPS > 0);
        Track("MaxBGFPS", _config.EQClientIni.MaxBGFPS > 0);

        ConfigManager.Save(_config);

        // Apply to eqclient.ini
        ApplyToIni();
    }

    private void ApplyToIni()
    {
        if (!File.Exists(_iniPath))
        {
            FileLogger.Info($"EQClientSettings: eqclient.ini not found at {_iniPath}");
            return;
        }

        try
        {
            var lines = File.ReadAllLines(_iniPath, Encoding.Default).ToList();

            // Always write both states — toggling OFF restores the original value
            SetIniValue(lines, "Defaults", "Sound", _chkDisableSound.Checked ? "FALSE" : "TRUE");
            SetIniValue(lines, "Defaults", "Music", _chkDisableMusic.Checked ? "0" : "1");
            if ((int)_nudSoundVolume.Value >= 0)
                SetIniValue(lines, "Defaults", "SoundVolume", ((int)_nudSoundVolume.Value).ToString());
            SetIniValue(lines, "Defaults", "EnvSounds", _chkDisableEnvSounds.Checked ? "0" : "1");
            SetIniValue(lines, "Defaults", "CombatMusic", _chkDisableCombatMusic.Checked ? "0" : "1");
            SetIniValue(lines, "Defaults", "AllowAutoDuck", _chkDisableAutoDuck.Checked ? "0" : "1");

            SetIniValue(lines, "Options", "Sky", _chkDisableSky.Checked ? "0" : "1");
            SetIniValue(lines, "Options", "BardSongs", _chkBardSongs.Checked ? "1" : "0");
            SetIniValue(lines, "Options", "BardSongsOnPets", _chkBardSongsOnPets.Checked ? "1" : "0");
            SetIniValue(lines, "Defaults", "AttackOnAssist", _chkAttackOnAssist.Checked ? "TRUE" : "FALSE");
            SetIniValue(lines, "Defaults", "ShowInspectMessage", _chkShowInspectMessage.Checked ? "TRUE" : "FALSE");
            SetIniValue(lines, "Defaults", "ShowGrass", _chkShowGrass.Checked ? "TRUE" : "FALSE");
            SetIniValue(lines, "Defaults", "NetStat", _chkNetStat.Checked ? "TRUE" : "FALSE");
            SetIniValue(lines, "Defaults", "TrackAutoUpdate", _chkTrackAutoUpdate.Checked ? "TRUE" : "FALSE");
            SetIniValue(lines, "Defaults", "TargetGroupBuff", _chkTargetGroupBuff.Checked ? "1" : "0");
            SetIniValue(lines, "Defaults", "MipMapping", _chkDisableMipMapping.Checked ? "FALSE" : "TRUE");
            SetIniValue(lines, "Defaults", "TextureCache", _chkTextureCache.Checked ? "TRUE" : "FALSE");
            SetIniValue(lines, "Defaults", "UseD3DTextureCompression", _chkUseD3DTextureCompression.Checked ? "TRUE" : "FALSE");
            SetIniValue(lines, "Defaults", "ShowDynamicLights", _chkDisableDynamicLights.Checked ? "FALSE" : "TRUE");
            SetIniValue(lines, "Defaults", "UseLitBatches", _chkUseLitBatches.Checked ? "TRUE" : "FALSE");
            SetIniValue(lines, "Defaults", "InspectOthers", _chkDisableInspectOthers.Checked ? "FALSE" : "TRUE");
            SetIniValue(lines, "Options", "Anonymous", _chkAnonymous.Checked ? "1" : "0");
            SetIniValue(lines, "Options", "RaidInviteConfirm", _chkRaidInviteConfirm.Checked ? "1" : "0");
            SetIniValue(lines, "Options", "AANoConfirm", _chkAANoConfirm.Checked ? "0" : "1");
            SetIniValue(lines, "Options", "ChatServerPort", _chkDisableChatServer.Checked ? "0" : "7003");
            SetIniValue(lines, "Options", "LootAllConfirm", _chkDisableLootAllConfirm.Checked ? "0" : "1");

            if ((int)_nudClipPlane.Value > 0)
                SetIniValue(lines, "Options", "ClipPlane", ((int)_nudClipPlane.Value).ToString());

            if ((int)_nudMouseSensitivity.Value >= 0)
                SetIniValue(lines, "Options", "MouseSensitivity", ((int)_nudMouseSensitivity.Value).ToString());

            if (_chkSlowSky.Checked)
            {
                SetIniValue(lines, "Defaults", "SkyUpdateInterval", "60000");
            }
            else
            {
                // Restore original non-slow value, or fall back to EQ default (3000)
                string restoreVal = string.IsNullOrEmpty(_origSkyInterval) ? "3000" : _origSkyInterval;
                SetIniValue(lines, "Defaults", "SkyUpdateInterval", restoreVal);
            }

            string wmVal = _chkForceWindowed.Checked ? "TRUE" : "FALSE";
            SetIniValue(lines, "Defaults", "WindowedMode", wmVal);
            SetIniValue(lines, "VideoMode", "WindowedMode", wmVal);

            if ((int)_nudMaxFPS.Value > 0)
            {
                SetIniValue(lines, "Defaults", "MaxFPS", ((int)_nudMaxFPS.Value).ToString());
                SetIniValue(lines, "Options", "MaxFPS", ((int)_nudMaxFPS.Value).ToString());
            }

            if ((int)_nudMaxBGFPS.Value > 0)
            {
                SetIniValue(lines, "Defaults", "MaxBGFPS", ((int)_nudMaxBGFPS.Value).ToString());
                SetIniValue(lines, "Options", "MaxBGFPS", ((int)_nudMaxBGFPS.Value).ToString());
            }

            if ((int)_nudShadowClipPlane.Value > 0)
                SetIniValue(lines, "Options", "ShadowClipPlane", ((int)_nudShadowClipPlane.Value).ToString());

            if ((int)_nudActorClipPlane.Value > 0)
                SetIniValue(lines, "Options", "ActorClipPlane", ((int)_nudActorClipPlane.Value).ToString());

            SetIniValue(lines, "Defaults", "Log", _config.EQClientIni.DisableEQLog ? "FALSE" : "TRUE");

            File.WriteAllLines(_iniPath, lines, Encoding.Default);
            FileLogger.Info("EQClientSettings: applied overrides to eqclient.ini");
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQClientSettings: apply error", ex);
            MessageBox.Show($"Failed to update eqclient.ini: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
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
            var k = config.EQClientIni.ConfiguredKeys;

            // Helper: only write if user has explicitly saved this key
            void Set(string section, string key, string value)
            {
                if (k.Contains(key)) SetIniValue(lines, section, key, value);
            }

            // Toggle settings — write both states so toggling OFF restores the correct value
            Set("Defaults", "Sound", config.EQClientIni.DisableSound ? "FALSE" : "TRUE");
            Set("Defaults", "Music", config.EQClientIni.DisableMusic ? "0" : "1");
            if (config.EQClientIni.SoundVolume >= 0)
                Set("Defaults", "SoundVolume", config.EQClientIni.SoundVolume.ToString());
            Set("Defaults", "EnvSounds", config.EQClientIni.DisableEnvSounds ? "0" : "1");
            Set("Defaults", "CombatMusic", config.EQClientIni.DisableCombatMusic ? "0" : "1");
            Set("Defaults", "AllowAutoDuck", config.EQClientIni.DisableAutoDuck ? "0" : "1");

            Set("Options", "Sky", config.EQClientIni.DisableSky ? "0" : "1");
            Set("Options", "BardSongs", config.EQClientIni.BardSongs ? "1" : "0");
            Set("Options", "BardSongsOnPets", config.EQClientIni.BardSongsOnPets ? "1" : "0");
            Set("Defaults", "AttackOnAssist", config.EQClientIni.AttackOnAssist ? "TRUE" : "FALSE");
            Set("Defaults", "ShowInspectMessage", config.EQClientIni.ShowInspectMessage ? "TRUE" : "FALSE");
            Set("Defaults", "ShowGrass", config.EQClientIni.ShowGrass ? "TRUE" : "FALSE");
            Set("Defaults", "NetStat", config.EQClientIni.NetStat ? "TRUE" : "FALSE");
            Set("Defaults", "TrackAutoUpdate", config.EQClientIni.TrackAutoUpdate ? "TRUE" : "FALSE");
            Set("Defaults", "TargetGroupBuff", config.EQClientIni.TargetGroupBuff ? "1" : "0");
            Set("Defaults", "MipMapping", config.EQClientIni.DisableMipMapping ? "FALSE" : "TRUE");
            Set("Defaults", "TextureCache", config.EQClientIni.TextureCache ? "TRUE" : "FALSE");
            Set("Defaults", "UseD3DTextureCompression", config.EQClientIni.UseD3DTextureCompression ? "TRUE" : "FALSE");
            Set("Defaults", "ShowDynamicLights", config.EQClientIni.DisableDynamicLights ? "FALSE" : "TRUE");
            Set("Defaults", "UseLitBatches", config.EQClientIni.UseLitBatches ? "TRUE" : "FALSE");
            Set("Defaults", "InspectOthers", config.EQClientIni.DisableInspectOthers ? "FALSE" : "TRUE");
            Set("Options", "Anonymous", config.EQClientIni.Anonymous ? "1" : "0");
            Set("Options", "RaidInviteConfirm", config.EQClientIni.RaidInviteConfirm ? "1" : "0");
            Set("Options", "AANoConfirm", config.EQClientIni.AANoConfirm ? "0" : "1");
            Set("Options", "ChatServerPort", config.EQClientIni.DisableChatServer ? "0" : "7003");
            Set("Options", "LootAllConfirm", config.EQClientIni.DisableLootAllConfirm ? "0" : "1");

            if (config.EQClientIni.ClipPlane > 0)
                Set("Options", "ClipPlane", config.EQClientIni.ClipPlane.ToString());

            if (config.EQClientIni.MouseSensitivity >= 0)
                Set("Options", "MouseSensitivity", config.EQClientIni.MouseSensitivity.ToString());

            if (config.EQClientIni.SlowSkyUpdates)
                Set("Defaults", "SkyUpdateInterval", "60000");
            else
                Set("Defaults", "SkyUpdateInterval", "3000"); // EQ default

            // EQ reads WindowedMode from [Defaults], not [VideoMode] — write both
            string wmVal = config.EQClientIni.ForceWindowedMode ? "TRUE" : "FALSE";
            Set("Defaults", "WindowedMode", wmVal);
            Set("VideoMode", "WindowedMode", wmVal);
            // Write Maximized to all sections that use it
            string maxVal = config.EQClientIni.MaximizeWindow ? "1" : "0";
            Set("Defaults", "Maximized", maxVal);
            Set("VideoMode", "Maximized", maxVal);

            // Slim Titlebar requires: WindowedMode=TRUE, Maximized=0,
            // and resolution matching the target monitor so the game + titlebar
            // extends below the screen edge (WinEQ2 method).
            if (config.Layout.SlimTitlebar)
            {
                var targetIdx = Math.Clamp(config.Layout.TargetMonitor, 0,
                    Math.Max(0, Screen.AllScreens.Length - 1));
                var screen = Screen.AllScreens[targetIdx];
                int monW = screen.Bounds.Width;
                int monH = screen.Bounds.Height;
                int offset = config.Layout.TitlebarOffset;
                int bottomOffset = config.Layout.BottomOffset;
                int gameH = monH - bottomOffset;

                SetIniValue(lines, "Defaults", "WindowedMode", "TRUE");
                SetIniValue(lines, "VideoMode", "WindowedMode", "TRUE");
                SetIniValue(lines, "Defaults", "Maximized", "0");
                SetIniValue(lines, "VideoMode", "Maximized", "0");
                SetIniValue(lines, "VideoMode", "Width", monW.ToString());
                SetIniValue(lines, "VideoMode", "Height", monH.ToString());
                SetIniValue(lines, "VideoMode", "WindowedWidth", monW.ToString());
                SetIniValue(lines, "VideoMode", "WindowedHeight", gameH.ToString());
                SetIniValue(lines, "Defaults", "WindowedWidth", monW.ToString());
                SetIniValue(lines, "Defaults", "WindowedHeight", gameH.ToString());
                SetIniValue(lines, "Defaults", "WindowedModeXOffset", "0");
                SetIniValue(lines, "Defaults", "WindowedModeYOffset", "0");
                SetIniValue(lines, "VideoMode", "WindowedModeXOffset", "0");
                SetIniValue(lines, "VideoMode", "WindowedModeYOffset", "0");
                // Unbind Alt+N (Story Window) in EQ so it doesn't conflict with
                // our multi-monitor toggle hotkey. 0 = unbound.
                SetIniValue(lines, "Defaults", "KEYMAPPING_TOGGLE_STORYWIN_1", "0");
                SetIniValue(lines, "Defaults", "KEYMAPPING_TOGGLE_STORYWIN_2", "0");
                FileLogger.Info($"EnforceOverrides: SlimTitlebar ON → forced {monW}x{gameH} (monitor {monH} - bottom {bottomOffset}), titlebar hidden {offset}px, Maximized=0, WindowedMode=TRUE, Story Window unbound");
            }

            if (config.EQClientIni.MaxFPS > 0)
            {
                Set("Defaults", "MaxFPS", config.EQClientIni.MaxFPS.ToString());
                Set("Options", "MaxFPS", config.EQClientIni.MaxFPS.ToString());
            }

            if (config.EQClientIni.MaxBGFPS > 0)
            {
                Set("Defaults", "MaxBGFPS", config.EQClientIni.MaxBGFPS.ToString());
                Set("Options", "MaxBGFPS", config.EQClientIni.MaxBGFPS.ToString());
            }

            if (config.EQClientIni.ShadowClipPlane > 0)
                Set("Options", "ShadowClipPlane", config.EQClientIni.ShadowClipPlane.ToString());

            if (config.EQClientIni.ActorClipPlane > 0)
                Set("Options", "ActorClipPlane", config.EQClientIni.ActorClipPlane.ToString());

            Set("Defaults", "Log", config.EQClientIni.DisableEQLog ? "FALSE" : "TRUE");

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
