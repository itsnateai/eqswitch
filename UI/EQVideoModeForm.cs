// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System.Text;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Manages eqclient.ini [VideoMode] section settings.
/// Experimental sub-form for advanced/troubleshooting video mode configuration.
/// </summary>
public class EQVideoModeForm : Form
{
    private readonly AppConfig _config;
    private readonly string _iniPath;
    private readonly Dictionary<string, NumericUpDown> _numerics = new();
    private readonly Dictionary<string, string> _initialValues = new();

    // Grouped settings for card layout
    private static readonly (string Key, string Label, int Default, int Min, int Max)[] ResolutionSettings =
    {
        ("Width", "Width", 1920, 640, 7680),
        ("Height", "Height", 1080, 480, 4320),
        ("WindowedWidth", "Windowed Width", 1920, 640, 7680),
        ("WindowedHeight", "Windowed Height", 1080, 480, 4320),
        ("WinEQWidth", "WinEQ Width", 1920, 640, 7680),
        ("WinEQHeight", "WinEQ Height", 1200, 480, 4320),
    };

    private static readonly (string Key, string Label, int Default, int Min, int Max)[] OffsetSettings =
    {
        ("WindowedModeXOffset", "Windowed X Offset", 0, -9999, 9999),
        ("WindowedModeYOffset", "Windowed Y Offset", 0, -9999, 9999),
        ("XOffset", "X Offset", 0, -9999, 9999),
        ("YOffset", "Y Offset", 0, -9999, 9999),
    };

    private static readonly (string Key, string Label, int Default, int Min, int Max)[] FullscreenSettings =
    {
        ("FullscreenRefreshRate", "Refresh Rate", 0, 0, 360),
        ("FullscreenBitsPerPixel", "Bits Per Pixel", 32, 16, 32),
    };

    public EQVideoModeForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Video Mode", new Size(480, 480));
        StartPosition = FormStartPosition.CenterParent;

        int y = 8;

        // ─── Resolution card ──────────────────────────────────────
        int resH = 30 + ResolutionSettings.Length * 26 + 4;
        var cardRes = DarkTheme.MakeCard(this, "\uD83D\uDCFA", "Resolution", DarkTheme.CardBlue, 10, y, 440, resH);
        int cy = 30;
        foreach (var (key, label, def, min, max) in ResolutionSettings)
        {
            DarkTheme.AddCardLabel(cardRes, label, 10, cy + 2);
            _numerics[key] = DarkTheme.AddCardNumeric(cardRes, 200, cy, 100, def, min, max);
            cy += 26;
        }
        y += resH + 8;

        // ─── Offsets card ─────────────────────────────────────────
        int offH = 30 + OffsetSettings.Length * 26 + 4;
        var cardOff = DarkTheme.MakeCard(this, "\u2195", "Window Offsets", DarkTheme.CardGreen, 10, y, 440, offH);
        cy = 30;
        foreach (var (key, label, def, min, max) in OffsetSettings)
        {
            DarkTheme.AddCardLabel(cardOff, label, 10, cy + 2);
            _numerics[key] = DarkTheme.AddCardNumeric(cardOff, 200, cy, 100, def, min, max);
            cy += 26;
        }
        y += offH + 8;

        // ─── Fullscreen card ──────────────────────────────────────
        int fsH = 30 + FullscreenSettings.Length * 26 + 20;
        var cardFs = DarkTheme.MakeCard(this, "\uD83D\uDD33", "Fullscreen", DarkTheme.CardGold, 10, y, 440, fsH);
        cy = 30;
        foreach (var (key, label, def, min, max) in FullscreenSettings)
        {
            DarkTheme.AddCardLabel(cardFs, label, 10, cy + 2);
            _numerics[key] = DarkTheme.AddCardNumeric(cardFs, 200, cy, 100, def, min, max);
            cy += 26;
        }

        DarkTheme.AddCardHint(cardFs, "Only changed values written. Apply on next EQ launch.", 10, cy + 2);

        // ─── Docked bottom panel with Save/Apply/Cancel ──────────
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            BackColor = DarkTheme.BgDark
        };

        var btnSave = DarkTheme.MakePrimaryButton("Save", 110, 10);
        btnSave.Click += (_, _) => { SaveSettings(); Close(); };

        var btnApply = DarkTheme.MakeButton("Apply", DarkTheme.BgMedium, 200, 10);
        btnApply.Click += (_, _) => { SaveSettings(); };

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 290, 10);
        btnCancel.Click += (_, _) => Close();

        buttonPanel.Controls.AddRange(new Control[] { btnSave, btnApply, btnCancel });
        Controls.Add(buttonPanel);
    }

    private void LoadFromIni()
    {
        if (File.Exists(_iniPath))
        {
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

                    if (!currentSection.Equals("[VideoMode]", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parts = trimmed.Split('=', 2);
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim();
                    string val = parts[1].Trim();

                    if (_numerics.TryGetValue(key, out var nud))
                    {
                        if (int.TryParse(val, out int v))
                            nud.Value = Math.Clamp(v, (int)nud.Minimum, (int)nud.Maximum);
                    }
                }

                FileLogger.Info("EQVideoMode: loaded current values from eqclient.ini");
            }
            catch (Exception ex)
            {
                FileLogger.Error("EQVideoMode: load error", ex);
            }
        }

        // Snapshot unconditionally — runs even if file missing or load failed
        foreach (var (key, nud) in _numerics)
            _initialValues[key] = ((int)nud.Value).ToString();
    }

    private void SaveSettings()
    {
        // Find changed keys
        var changed = new Dictionary<string, string>();
        foreach (var (key, nud) in _numerics)
        {
            string val = ((int)nud.Value).ToString();
            if (!_initialValues.TryGetValue(key, out string? init) || init != val)
                changed[key] = val;
        }

        if (changed.Count == 0)
        {
            FileLogger.Info("EQVideoMode: no changes to save");
            return;
        }

        // Save to config
        foreach (var (key, val) in changed)
            _config.EQClientIni.VideoModeOverrides[key] = val;
        ConfigManager.Save(_config);

        // Apply to eqclient.ini
        if (!File.Exists(_iniPath)) return;

        try
        {
            var lines = File.ReadAllLines(_iniPath, Encoding.Default).ToList();

            foreach (var (key, val) in changed)
                EQClientSettingsForm.SetIniValue(lines, "VideoMode", key, val);

            File.WriteAllLines(_iniPath, lines, Encoding.Default);
            FileLogger.Info($"EQVideoMode: saved {changed.Count} changed setting(s) to eqclient.ini");

            // Update snapshot
            foreach (var (key, nud) in _numerics)
                _initialValues[key] = ((int)nud.Value).ToString();
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQVideoMode: save error", ex);
            MessageBox.Show($"Failed to save: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }

    /// <summary>
    /// Static helper: enforce all video mode overrides in eqclient.ini.
    /// Called by EnforceOverrides in EQClientSettingsForm.
    /// </summary>
    public static void EnforceOverrides(AppConfig config, List<string> lines)
    {
        foreach (var (key, value) in config.EQClientIni.VideoModeOverrides)
            EQClientSettingsForm.SetIniValue(lines, "VideoMode", key, value);
    }
}
