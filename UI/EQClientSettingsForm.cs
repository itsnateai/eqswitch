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
        DarkTheme.StyleForm(this, "EQSwitch \u2014 EQ Client Settings", new Size(400, 900));
        AutoScroll = true;

        int y = 12;

        y = DarkTheme.AddSectionHeader(this, "\u2699  Persistent eqclient.ini Overrides", 15, y);

        DarkTheme.AddHint(this, "These settings are written to eqclient.ini on Save/Apply.\nEQ must be restarted for changes to take effect.", 15, y);

        y += 35;

        _chkAnonymous = new CheckBox
        {
            Text = "Anonymous  (Anonymous=1)",
            Location = new Point(20, y),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.Anonymous
        };
        Controls.Add(_chkAnonymous);

        _chkRaidInviteConfirm = new CheckBox
        {
            Text = "Raid Invite Confirm  (RaidInviteConfirm=1)",
            Location = new Point(20, y += 25),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.RaidInviteConfirm
        };
        Controls.Add(_chkRaidInviteConfirm);

        _chkAANoConfirm = new CheckBox
        {
            Text = "AA No Confirm  (AANoConfirm=0)",
            Location = new Point(20, y += 25),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.AANoConfirm
        };
        Controls.Add(_chkAANoConfirm);

        _chkDisableChatServer = new CheckBox
        {
            Text = "Disable Chat Server  (ChatServerPort=0)",
            Location = new Point(20, y += 25),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.DisableChatServer
        };
        Controls.Add(_chkDisableChatServer);

        y += 30;
        _chkDisableSound = new CheckBox
        {
            Text = "Disable Sound  (Sound=FALSE)",
            Location = new Point(20, y),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.DisableSound
        };
        Controls.Add(_chkDisableSound);

        _chkDisableMusic = new CheckBox
        {
            Text = "Disable Music  (Music=0)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.DisableMusic
        };
        Controls.Add(_chkDisableMusic);

        DarkTheme.AddLabel(this, "Sound Volume:", 230, y + 2);
        _nudSoundVolume = new NumericUpDown
        {
            Location = new Point(330, y), Size = new Size(50, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            Minimum = -1, Maximum = 100,
            Value = Math.Clamp(_config.EQClientIni.SoundVolume, -1, 100)
        };
        DarkTheme.AddHint(this, "(-1 = don't set)", 230, y + 25);
        Controls.Add(_nudSoundVolume);

        _chkDisableEnvSounds = new CheckBox
        {
            Text = "Disable Env Sounds  (EnvSounds=0)",
            Location = new Point(20, y += 50),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.DisableEnvSounds
        };
        Controls.Add(_chkDisableEnvSounds);

        _chkDisableCombatMusic = new CheckBox
        {
            Text = "Disable Combat Music  (CombatMusic=0)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.DisableCombatMusic
        };
        Controls.Add(_chkDisableCombatMusic);

        _chkDisableAutoDuck = new CheckBox
        {
            Text = "Disable Auto-Duck  (AllowAutoDuck=0)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.DisableAutoDuck
        };
        Controls.Add(_chkDisableAutoDuck);

        _chkSlowSky = new CheckBox
        {
            Text = "Slow Sky Updates  (SkyUpdateInterval=60000)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.SlowSkyUpdates
        };
        Controls.Add(_chkSlowSky);

        _chkDisableSky = new CheckBox
        {
            Text = "Disable Sky  (Sky=0)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.DisableSky
        };
        Controls.Add(_chkDisableSky);

        _chkBardSongs = new CheckBox
        {
            Text = "Persistent Bard Songs  (BardSongs=1)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.BardSongs
        };
        Controls.Add(_chkBardSongs);

        _chkBardSongsOnPets = new CheckBox
        {
            Text = "Bard Songs on Pets  (BardSongsOnPets=1)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.BardSongsOnPets
        };
        Controls.Add(_chkBardSongsOnPets);

        _chkAttackOnAssist = new CheckBox
        {
            Text = "Attack on Assist  (AttackOnAssist=TRUE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.AttackOnAssist
        };
        Controls.Add(_chkAttackOnAssist);

        _chkShowInspectMessage = new CheckBox
        {
            Text = "Show Inspect Message  (ShowInspectMessage=TRUE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.ShowInspectMessage
        };
        Controls.Add(_chkShowInspectMessage);

        _chkShowGrass = new CheckBox
        {
            Text = "Show Grass  (ShowGrass=TRUE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.ShowGrass
        };
        Controls.Add(_chkShowGrass);

        _chkNetStat = new CheckBox
        {
            Text = "Ping Bar  (NetStat=TRUE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.NetStat
        };
        Controls.Add(_chkNetStat);

        _chkTrackAutoUpdate = new CheckBox
        {
            Text = "Track Auto-Update  (TrackAutoUpdate=TRUE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.TrackAutoUpdate
        };
        Controls.Add(_chkTrackAutoUpdate);

        _chkTargetGroupBuff = new CheckBox
        {
            Text = "Target Group Buff  (TargetGroupBuff=1)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.TargetGroupBuff
        };
        Controls.Add(_chkTargetGroupBuff);

        _chkDisableMipMapping = new CheckBox
        {
            Text = "Disable Mip-Mapping  (MipMapping=FALSE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.DisableMipMapping
        };
        Controls.Add(_chkDisableMipMapping);

        _chkTextureCache = new CheckBox
        {
            Text = "Texture Cache  (TextureCache=TRUE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.TextureCache
        };
        Controls.Add(_chkTextureCache);

        _chkUseD3DTextureCompression = new CheckBox
        {
            Text = "D3D Texture Compression  (UseD3DTextureCompression=TRUE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.UseD3DTextureCompression
        };
        Controls.Add(_chkUseD3DTextureCompression);

        _chkDisableDynamicLights = new CheckBox
        {
            Text = "Disable Dynamic Lights  (ShowDynamicLights=FALSE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.DisableDynamicLights
        };
        Controls.Add(_chkDisableDynamicLights);

        _chkUseLitBatches = new CheckBox
        {
            Text = "Use Lit Batches  (UseLitBatches=TRUE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.UseLitBatches
        };
        Controls.Add(_chkUseLitBatches);

        _chkDisableInspectOthers = new CheckBox
        {
            Text = "Disable Inspect Others  (InspectOthers=FALSE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.DisableInspectOthers
        };
        Controls.Add(_chkDisableInspectOthers);

        _chkDisableLootAllConfirm = new CheckBox
        {
            Text = "Disable Loot All Confirm  (LootAllConfirm=0)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.DisableLootAllConfirm
        };
        Controls.Add(_chkDisableLootAllConfirm);

        _chkForceWindowed = new CheckBox
        {
            Text = "Force Windowed Mode  (WindowedMode=TRUE)",
            Location = new Point(20, y += 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgWhite,
            Checked = _config.EQClientIni.ForceWindowedMode
        };
        Controls.Add(_chkForceWindowed);

        y += 35;

        // FPS limits
        y = DarkTheme.AddSectionHeader(this, "FPS Limits", 20, y);
        DarkTheme.AddLabel(this, "Max FPS:", 20, y + 3);
        _nudMaxFPS = new NumericUpDown
        {
            Location = new Point(100, y), Size = new Size(60, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            Minimum = 0, Maximum = 240,
            Value = Math.Clamp(_config.EQClientIni.MaxFPS, 0, 240)
        };
        DarkTheme.AddHint(this, "(0 = don't set)", 165, y + 3);
        Controls.Add(_nudMaxFPS);

        DarkTheme.AddLabel(this, "Max BG FPS:", 20, y += 28);
        _nudMaxBGFPS = new NumericUpDown
        {
            Location = new Point(100, y - 3), Size = new Size(60, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            Minimum = 0, Maximum = 240,
            Value = Math.Clamp(_config.EQClientIni.MaxBGFPS, 0, 240)
        };
        DarkTheme.AddHint(this, "(0 = don't set)", 165, y);
        Controls.Add(_nudMaxBGFPS);

        // Clip planes (all grouped together)
        y += 35;
        y = DarkTheme.AddSectionHeader(this, "Clip Planes", 20, y);
        DarkTheme.AddLabel(this, "Clip Plane:", 20, y + 3);
        _nudClipPlane = new NumericUpDown
        {
            Location = new Point(120, y), Size = new Size(60, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            Minimum = 0, Maximum = 999,
            Value = Math.Clamp(_config.EQClientIni.ClipPlane, 0, 999)
        };
        DarkTheme.AddHint(this, "(0 = don't set, default 14)", 185, y + 3);
        Controls.Add(_nudClipPlane);

        DarkTheme.AddLabel(this, "Shadow Clip:", 20, y += 28);
        _nudShadowClipPlane = new NumericUpDown
        {
            Location = new Point(120, y - 3), Size = new Size(60, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            Minimum = 0, Maximum = 999,
            Value = Math.Clamp(_config.EQClientIni.ShadowClipPlane, 0, 999)
        };
        DarkTheme.AddHint(this, "(0 = don't set, default 35)", 185, y);
        Controls.Add(_nudShadowClipPlane);

        DarkTheme.AddLabel(this, "Actor Clip:", 20, y += 28);
        _nudActorClipPlane = new NumericUpDown
        {
            Location = new Point(120, y - 3), Size = new Size(60, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            Minimum = 0, Maximum = 999,
            Value = Math.Clamp(_config.EQClientIni.ActorClipPlane, 0, 999)
        };
        DarkTheme.AddHint(this, "(0 = don't set, default 67)", 185, y);
        Controls.Add(_nudActorClipPlane);

        // Mouse sensitivity
        DarkTheme.AddLabel(this, "Mouse Sens:", 20, y += 28);
        _nudMouseSensitivity = new NumericUpDown
        {
            Location = new Point(120, y - 3), Size = new Size(60, 24),
            BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite,
            Minimum = -1, Maximum = 100,
            Value = Math.Clamp(_config.EQClientIni.MouseSensitivity, -1, 100)
        };
        DarkTheme.AddHint(this, "(-1 = don't set)", 185, y);
        Controls.Add(_nudMouseSensitivity);

        y += 35;

        var btnModels = DarkTheme.MakeButton("\uD83C\uDFAD  Luclin Models...", DarkTheme.BgMedium, 20, y);
        btnModels.Size = new Size(180, 30);
        btnModels.Click += (_, _) =>
        {
            using var form = new EQModelsForm(_config);
            form.ShowDialog(this);
        };
        Controls.Add(btnModels);

        var btnChatSpam = DarkTheme.MakeButton("\uD83D\uDCAC  Chat Spam Filters...", DarkTheme.BgMedium, 210, y);
        btnChatSpam.Size = new Size(150, 30);
        btnChatSpam.Click += (_, _) =>
        {
            using var form = new EQChatSpamForm(_config);
            form.ShowDialog(this);
        };
        Controls.Add(btnChatSpam);

        y += 35;

        var btnParticles = DarkTheme.MakeButton("\u2728  Particles & Opacity...", DarkTheme.BgMedium, 20, y);
        btnParticles.Size = new Size(180, 30);
        btnParticles.Click += (_, _) =>
        {
            using var form = new EQParticlesForm(_config);
            form.ShowDialog(this);
        };
        Controls.Add(btnParticles);

        var btnVideoMode = DarkTheme.MakeButton("\uD83D\uDCFA  Video Mode...", DarkTheme.BgMedium, 210, y);
        btnVideoMode.Size = new Size(150, 30);
        btnVideoMode.Click += (_, _) =>
        {
            using var form = new EQVideoModeForm(_config);
            form.ShowDialog(this);
        };
        Controls.Add(btnVideoMode);

        y += 35;

        var btnKeymaps = DarkTheme.MakeButton("\u2328  Key Mappings...", DarkTheme.BgMedium, 20, y);
        btnKeymaps.Size = new Size(180, 30);
        btnKeymaps.Click += (_, _) =>
        {
            using var form = new EQKeymapsForm(_config);
            form.ShowDialog(this);
        };
        Controls.Add(btnKeymaps);

        y += 45;

        var btnSave = DarkTheme.MakePrimaryButton("Save", 60, y);
        btnSave.Click += (_, _) => { SaveSettings(); Close(); };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 150, y);
        btnApply.Click += (_, _) => { SaveSettings(); };

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 240, y);
        btnCancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { btnSave, btnApply, btnCancel });
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
                                _nudSoundVolume.Value = Math.Clamp(svol, 0, 100);
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
                                _nudMouseSensitivity.Value = Math.Clamp(ms, 0, 100);
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
                                _nudMaxFPS.Value = Math.Clamp(fps, 0, 240);
                            break;
                        case "maxbgfps":
                            if (int.TryParse(val, out int bgfps))
                                _nudMaxBGFPS.Value = Math.Clamp(bgfps, 0, 240);
                            break;
                        case "shadowclipplane":
                            if (int.TryParse(val, out int scp))
                                _nudShadowClipPlane.Value = Math.Clamp(scp, 0, 999);
                            break;
                        case "actorclipplane":
                            if (int.TryParse(val, out int acp))
                                _nudActorClipPlane.Value = Math.Clamp(acp, 0, 999);
                            break;
                    }
                }
                else if (currentSection.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase))
                {
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

        // Mark all settings as explicitly configured — EnforceOverrides will only write these
        var keys = _config.EQClientIni.ConfiguredKeys;
        keys.UnionWith(new[]
        {
            "Sound", "Music", "SoundVolume", "EnvSounds", "CombatMusic", "AllowAutoDuck",
            "Sky", "SkyUpdateInterval", "BardSongs", "BardSongsOnPets",
            "AttackOnAssist", "ShowInspectMessage", "ShowGrass", "NetStat",
            "TrackAutoUpdate", "TargetGroupBuff", "MipMapping", "TextureCache",
            "UseD3DTextureCompression", "ShowDynamicLights", "UseLitBatches",
            "InspectOthers", "Anonymous", "RaidInviteConfirm", "AANoConfirm",
            "ChatServerPort", "LootAllConfirm", "WindowedMode",
            "MaxFPS", "MaxBGFPS", "ClipPlane", "ShadowClipPlane", "ActorClipPlane",
            "MouseSensitivity", "Log"
        });

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

            SetIniValue(lines, "Defaults", "Sky", _chkDisableSky.Checked ? "0" : "1");
            SetIniValue(lines, "Defaults", "BardSongs", _chkBardSongs.Checked ? "1" : "0");
            SetIniValue(lines, "Defaults", "BardSongsOnPets", _chkBardSongsOnPets.Checked ? "1" : "0");
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
            SetIniValue(lines, "Defaults", "Anonymous", _chkAnonymous.Checked ? "1" : "0");
            SetIniValue(lines, "Defaults", "RaidInviteConfirm", _chkRaidInviteConfirm.Checked ? "1" : "0");
            SetIniValue(lines, "Defaults", "AANoConfirm", _chkAANoConfirm.Checked ? "0" : "1");
            if (_chkDisableChatServer.Checked)
                SetIniValue(lines, "Defaults", "ChatServerPort", "0");
            SetIniValue(lines, "Defaults", "LootAllConfirm", _chkDisableLootAllConfirm.Checked ? "0" : "1");

            if ((int)_nudClipPlane.Value > 0)
                SetIniValue(lines, "Defaults", "ClipPlane", ((int)_nudClipPlane.Value).ToString());

            if ((int)_nudMouseSensitivity.Value >= 0)
                SetIniValue(lines, "Defaults", "MouseSensitivity", ((int)_nudMouseSensitivity.Value).ToString());

            if (_chkSlowSky.Checked)
            {
                SetIniValue(lines, "Defaults", "SkyUpdateInterval", "60000");
            }
            else if (!string.IsNullOrEmpty(_origSkyInterval))
            {
                // Restore original value that was read from INI
                SetIniValue(lines, "Defaults", "SkyUpdateInterval", _origSkyInterval);
            }

            SetIniValue(lines, "VideoMode", "WindowedMode", _chkForceWindowed.Checked ? "TRUE" : "FALSE");

            if ((int)_nudMaxFPS.Value > 0)
                SetIniValue(lines, "Defaults", "MaxFPS", ((int)_nudMaxFPS.Value).ToString());

            if ((int)_nudMaxBGFPS.Value > 0)
                SetIniValue(lines, "Defaults", "MaxBGFPS", ((int)_nudMaxBGFPS.Value).ToString());

            if ((int)_nudShadowClipPlane.Value > 0)
                SetIniValue(lines, "Defaults", "ShadowClipPlane", ((int)_nudShadowClipPlane.Value).ToString());

            if ((int)_nudActorClipPlane.Value > 0)
                SetIniValue(lines, "Defaults", "ActorClipPlane", ((int)_nudActorClipPlane.Value).ToString());

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

            Set("Defaults", "Sky", config.EQClientIni.DisableSky ? "0" : "1");
            Set("Defaults", "BardSongs", config.EQClientIni.BardSongs ? "1" : "0");
            Set("Defaults", "BardSongsOnPets", config.EQClientIni.BardSongsOnPets ? "1" : "0");
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
            Set("Defaults", "Anonymous", config.EQClientIni.Anonymous ? "1" : "0");
            Set("Defaults", "RaidInviteConfirm", config.EQClientIni.RaidInviteConfirm ? "1" : "0");
            Set("Defaults", "AANoConfirm", config.EQClientIni.AANoConfirm ? "0" : "1");
            if (config.EQClientIni.DisableChatServer)
                Set("Defaults", "ChatServerPort", "0");
            Set("Defaults", "LootAllConfirm", config.EQClientIni.DisableLootAllConfirm ? "0" : "1");

            if (config.EQClientIni.ClipPlane > 0)
                Set("Defaults", "ClipPlane", config.EQClientIni.ClipPlane.ToString());

            if (config.EQClientIni.MouseSensitivity >= 0)
                Set("Defaults", "MouseSensitivity", config.EQClientIni.MouseSensitivity.ToString());

            if (config.EQClientIni.SlowSkyUpdates)
                Set("Defaults", "SkyUpdateInterval", "60000");

            Set("VideoMode", "WindowedMode", config.EQClientIni.ForceWindowedMode ? "TRUE" : "FALSE");

            if (config.EQClientIni.MaxFPS > 0)
                Set("Defaults", "MaxFPS", config.EQClientIni.MaxFPS.ToString());

            if (config.EQClientIni.MaxBGFPS > 0)
                Set("Defaults", "MaxBGFPS", config.EQClientIni.MaxBGFPS.ToString());

            if (config.EQClientIni.ShadowClipPlane > 0)
                Set("Defaults", "ShadowClipPlane", config.EQClientIni.ShadowClipPlane.ToString());

            if (config.EQClientIni.ActorClipPlane > 0)
                Set("Defaults", "ActorClipPlane", config.EQClientIni.ActorClipPlane.ToString());

            if (config.DisableEQLog)
                Set("Defaults", "Log", "FALSE");

            // Enforce sub-form overrides (dictionary-based — already only write what user saved)
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
}
