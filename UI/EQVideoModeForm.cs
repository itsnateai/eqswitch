// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Text;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Manages eqclient.ini [VideoMode] section settings.
/// Experimental sub-form for advanced/troubleshooting video mode configuration.
/// </summary>
public class EQVideoModeForm : EqSwitchForm
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
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Video Mode \u2014 EXPERIMENTAL", new Size(480, 480));
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

        // Size the form to its content. The old hand-guessed Size(480,480) was UNDER the content
        // height, so the Fullscreen card overlapped the buttons; fitting to content restores a
        // proper gap above them.
        FitClientHeightToContent();
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

        // v3.22.45 post-T2-Opus MEDIUM (final round): symmetric slim-aware
        // filter for the SAVE path. EnforceOverrides was patched in the
        // post-swarm round to skip dim keys when slim is on, but
        // SaveSettings (user clicks Save in the Video Mode dialog) was the
        // OTHER half of the stomp pair — it wrote the user-typed value
        // directly to eqclient.ini AND persisted it to VideoModeOverrides,
        // so even though EnforceOverrides skipped the dict-replay on next
        // launch, the INI was already stomped + the bad value lived in JSON
        // forever (re-applies the moment user flips slim OFF). Drop these
        // four keys from the changeset when slim is active; user gets a log
        // line explaining why. UI improvement (graying out the dim NUDs
        // when slim is on) deferred to a future SettingsForm pass.
        if (_config.Layout.SlimTitlebar)
        {
            string[] slimOwnedKeys = { "WindowedWidth", "WindowedHeight", "Width", "Height" };
            var dropped = new List<string>();
            bool anyScrub = false;
            foreach (string k in slimOwnedKeys)
            {
                // Drop from changed even if user hand-typed the same value, AND
                // scrub any stale entry that's already in VideoModeOverrides.
                if (changed.Remove(k, out string? droppedVal))
                    dropped.Add($"{k}={droppedVal}");
                if (_config.EQClientIni.VideoModeOverrides.Remove(k))
                {
                    dropped.Add($"VideoModeOverrides[{k}] (scrubbed stale)");
                    anyScrub = true;
                }
                // v3.22.45 post-T3-Sonnet MEDIUM (final round): update the
                // snapshot for slim-owned keys regardless of whether the user
                // touched them this Save. Without this, the slim-only early-
                // return path leaves _initialValues holding the pre-Save NUD
                // value, so the next Save click re-detects "changed" and
                // burns another no-op cycle through the filter. Read live
                // NUD value into the snapshot now.
                if (_numerics.TryGetValue(k, out var nud))
                    _initialValues[k] = ((int)nud.Value).ToString();
            }
            // v3.22.45 post-T3-Sonnet LOW: don't gate the log on `dropped`
            // — the scrub could fire even when `changed` had no matching key
            // (user made no NUD edits but stale VideoModeOverrides entry
            // existed). Without unconditional logging, silent scrubs leave
            // no audit trail for "why did my config grow smaller on Save?"
            if (dropped.Count > 0 || anyScrub)
                FileLogger.Info($"EQVideoMode.SaveSettings: slim-owned dim handling — slim-titlebar mode owns these (v3.22.45): {(dropped.Count > 0 ? string.Join(", ", dropped) : "no entries to drop")}");
            if (changed.Count == 0)
            {
                // All the user changed was slim-owned dims; persist the scrub then bail.
                ConfigManager.Save(_config);
                FileLogger.Info("EQVideoMode: no non-dim changes to save (slim mode owned all changed keys)");
                return;
            }
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
            ThemedMessageDialog.Show(this, $"Failed to save: {ex.Message}", "Error",
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
        // v3.22.45 (T2-Opus + T3-Sonnet convergent MEDIUM): EnforceOverrides
        // chain calls EQClientSettingsForm.EnforceOverrides's slim block FIRST
        // (writes the bleed-corrected WindowedHeight / WindowedWidth) then
        // this method LAST — so a user with a stale VideoModeOverrides entry
        // for WindowedHeight or WindowedWidth would silently stomp the
        // corrected values, reintroducing the 1-px vertical seam on every
        // launch even after a clean v3.22.45 install. When slim is active
        // the [VideoMode] dim keys are owned by the slim block; skip them
        // here. Non-slim users keep the legacy "save whatever the user typed"
        // behaviour. Other [VideoMode] keys (refresh rate, bits-per-pixel,
        // gamma, etc.) pass through unchanged for both modes.
        bool slimOwnsDims = config.Layout.SlimTitlebar;
        foreach (var (key, value) in config.EQClientIni.VideoModeOverrides)
        {
            if (slimOwnsDims
                && (key.Equals("WindowedWidth", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("WindowedHeight", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Width", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Height", StringComparison.OrdinalIgnoreCase)))
            {
                FileLogger.Info($"EQVideoModeForm.EnforceOverrides: skipping VideoModeOverrides[{key}]={value} (slim-titlebar owns this dim — v3.22.45 bleed correction)");
                continue;
            }
            EQClientSettingsForm.SetIniValue(lines, "VideoMode", key, value);
        }
    }
}
