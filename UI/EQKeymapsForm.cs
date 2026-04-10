using System.Text;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Manages eqclient.ini [KeyMaps] key mapping settings.
/// EQ uses DirectInput scan codes for keybindings (KEYMAPPING_*=code).
/// Large values encode modifier flags in upper bits:
///   0x10000000 = Shift, 0x20000000 = Ctrl, 0x40000000 = Alt
/// </summary>
public class EQKeymapsForm : Form
{
    private readonly AppConfig _config;
    private readonly string _iniPath;
    private readonly Dictionary<string, NumericUpDown> _nudValues = new();
    private readonly Dictionary<string, Label> _keyLabels = new();
    private readonly Dictionary<string, long> _initialValues = new();

    // Grouped key mappings: (IniKey, DisplayLabel, DefaultCode)
    private static readonly (string Header, string Emoji, (string Key, string Label, long DefaultCode)[] Entries)[] Groups =
    {
        ("Targeting", "⚔", new[]
        {
            ("KEYMAPPING_TARGETNPC_2", "Target NPC (Alt)", 209L),
            ("KEYMAPPING_CONSIDER_2", "Consider (Alt)", 83L),
            ("KEYMAPPING_CYCLENPCTARGETS_2", "Cycle NPC Targets (Alt)", 79L),
            ("KEYMAPPING_TOGGLETWOTARGETS_1", "Toggle Two Targets", 82L),
            ("KEYMAPPING_TOGGLETWOTARGETS_2", "Toggle Two Targets (Alt)", 0L),
        }),
        ("Combat & Items", "\uD83D\uDEE1", new[]
        {
            ("KEYMAPPING_AUTOPRIM_2", "Auto-Primary (Alt)", 211L),
            ("KEYMAPPING_POTION_SLOT_3_1", "Potion Slot 3", 0L),
        }),
        ("Utility", "\uD83D\uDD27", new[]
        {
            ("KEYMAPPING_CMD_CLIPBOARD_PASTE_1", "Clipboard Paste", 536870959L),
            ("KEYMAPPING_CMD_TOGGLE_AUDIO_TRIGGER_WINDOW_1", "Audio Triggers", 268435486L),
        }),
    };

    // EQ modifier flags (encoded in upper bits of the key code)
    private const long ShiftFlag = 0x10000000;  // 268435456
    private const long CtrlFlag  = 0x20000000;  // 536870912
    private const long AltFlag   = 0x40000000;  // 1073741824

    // DirectInput scan code → human-readable key name
    private static readonly Dictionary<int, string> ScanCodeNames = new()
    {
        { 0, "(None)" },
        { 1, "Escape" }, { 2, "1" }, { 3, "2" }, { 4, "3" }, { 5, "4" },
        { 6, "5" }, { 7, "6" }, { 8, "7" }, { 9, "8" }, { 10, "9" },
        { 11, "0" }, { 12, "-" }, { 13, "=" }, { 14, "Backspace" },
        { 15, "Tab" }, { 16, "Q" }, { 17, "W" }, { 18, "E" }, { 19, "R" },
        { 20, "T" }, { 21, "Y" }, { 22, "U" }, { 23, "I" }, { 24, "O" },
        { 25, "P" }, { 26, "[" }, { 27, "]" }, { 28, "Enter" },
        { 29, "Left Ctrl" }, { 30, "A" }, { 31, "S" }, { 32, "D" },
        { 33, "F" }, { 34, "G" }, { 35, "H" }, { 36, "J" }, { 37, "K" },
        { 38, "L" }, { 39, ";" }, { 40, "'" }, { 41, "`" },
        { 42, "Left Shift" }, { 43, "\\" }, { 44, "Z" }, { 45, "X" },
        { 46, "C" }, { 47, "V" }, { 48, "B" }, { 49, "N" }, { 50, "M" },
        { 51, "," }, { 52, "." }, { 53, "/" }, { 54, "Right Shift" },
        { 55, "Numpad *" }, { 56, "Left Alt" }, { 57, "Space" },
        { 58, "Caps Lock" },
        { 59, "F1" }, { 60, "F2" }, { 61, "F3" }, { 62, "F4" },
        { 63, "F5" }, { 64, "F6" }, { 65, "F7" }, { 66, "F8" },
        { 67, "F9" }, { 68, "F10" },
        { 69, "Num Lock" }, { 70, "Scroll Lock" },
        { 71, "Numpad 7" }, { 72, "Numpad 8" }, { 73, "Numpad 9" },
        { 74, "Numpad -" }, { 75, "Numpad 4" }, { 76, "Numpad 5" },
        { 77, "Numpad 6" }, { 78, "Numpad +" }, { 79, "Numpad 1" },
        { 80, "Numpad 2" }, { 81, "Numpad 3" }, { 82, "Numpad 0" },
        { 83, "Numpad ." },
        { 87, "F11" }, { 88, "F12" },
        { 100, "F13" }, { 101, "F14" }, { 102, "F15" },
        { 156, "Numpad Enter" }, { 157, "Right Ctrl" },
        { 181, "Numpad /" }, { 184, "Right Alt" },
        { 197, "Pause" }, { 199, "Home" },
        { 200, "Up Arrow" }, { 201, "Page Up" },
        { 203, "Left Arrow" }, { 205, "Right Arrow" },
        { 207, "End" }, { 208, "Down Arrow" }, { 209, "Page Down" },
        { 210, "Insert" }, { 211, "Delete" },
        { 219, "Left Win" }, { 220, "Right Win" }, { 221, "App Menu" },
    };

    public EQKeymapsForm(AppConfig config)
    {
        _config = config;
        _iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        InitializeForm();
        LoadFromIni();
    }

    private void InitializeForm()
    {
        DarkTheme.StyleForm(this, "EQSwitch \u2014 Key Mappings", new Size(480, 520));
        StartPosition = FormStartPosition.CenterParent;
        AutoScroll = true;

        int y = 8;

        foreach (var (header, emoji, entries) in Groups)
        {
            int cardHeight = 30 + entries.Length * 26 + 4;
            var card = DarkTheme.MakeCard(this, emoji, header, DarkTheme.CardGold, 10, y, 440, cardHeight);
            int cy = 30;

            foreach (var (key, label, def) in entries)
            {
                DarkTheme.AddCardLabel(card, label, 10, cy + 2);

                // Decoded key name (right-aligned before numeric)
                var keyLabel = DarkTheme.AddCardLabel(card, GetKeyName(def), 195, cy + 2);
                keyLabel.AutoSize = false;
                keyLabel.Size = new Size(130, 16);
                keyLabel.TextAlign = ContentAlignment.MiddleRight;
                keyLabel.ForeColor = DarkTheme.FgWhite;
                _keyLabels[key] = keyLabel;

                // Scan code numeric (small, secondary)
                var nud = DarkTheme.AddCardNumeric(card, 335, cy, 95, def, 0, 2000000000);
                nud.ForeColor = DarkTheme.FgGray;
                nud.Font = DarkTheme.FontUI85;
                _nudValues[key] = nud;

                // Live update decoded label
                var lbl = keyLabel;
                nud.ValueChanged += (_, _) => lbl.Text = GetKeyName((long)nud.Value);

                cy += 26;
            }

            y += cardHeight + 8;
        }

        DarkTheme.AddHint(this, "Edit the scan code — the key name updates live. 0 = unbound.", 15, y);
        y += 22;

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

    /// <summary>
    /// Decode an EQ key code into a human-readable string.
    /// Large values have modifier flags in upper bits.
    /// </summary>
    private static string GetKeyName(long code)
    {
        if (code == 0) return "(None)";

        var parts = new List<string>();
        long scanCode = code;

        // Extract modifier flags
        if ((scanCode & AltFlag) != 0) { parts.Add("Alt"); scanCode &= ~AltFlag; }
        if ((scanCode & CtrlFlag) != 0) { parts.Add("Ctrl"); scanCode &= ~CtrlFlag; }
        if ((scanCode & ShiftFlag) != 0) { parts.Add("Shift"); scanCode &= ~ShiftFlag; }

        // Look up the base scan code
        string keyName = ScanCodeNames.TryGetValue((int)scanCode, out string? name)
            ? name
            : $"Code {scanCode}";

        parts.Add(keyName);
        return string.Join("+", parts);
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

                    if (!currentSection.Equals("[KeyMaps]", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parts = trimmed.Split('=', 2);
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim();
                    string val = parts[1].Trim();

                    if (_nudValues.TryGetValue(key, out var nud))
                    {
                        if (long.TryParse(val, out long code))
                        {
                            nud.Value = Math.Clamp(code, 0, 2000000000);
                            if (_keyLabels.TryGetValue(key, out var lbl))
                                lbl.Text = GetKeyName(code);
                        }
                    }
                }

                FileLogger.Info("EQKeymaps: loaded current values from eqclient.ini");
            }
            catch (Exception ex)
            {
                FileLogger.Error("EQKeymaps: load error", ex);
            }
        }

        // Snapshot unconditionally — runs even if file missing or load failed
        foreach (var (key, nud) in _nudValues)
            _initialValues[key] = (long)nud.Value;
    }

    private void SaveSettings()
    {
        var changed = new Dictionary<string, long>();
        foreach (var (key, nud) in _nudValues)
        {
            long val = (long)nud.Value;
            if (!_initialValues.TryGetValue(key, out long init) || init != val)
                changed[key] = val;
        }

        if (changed.Count == 0)
        {
            FileLogger.Info("EQKeymaps: no changes to save");
            return;
        }

        if (!File.Exists(_iniPath)) return;

        try
        {
            var lines = File.ReadAllLines(_iniPath, Encoding.Default).ToList();

            foreach (var (key, val) in changed)
                EQClientSettingsForm.SetIniValue(lines, "KeyMaps", key, val.ToString());

            File.WriteAllLines(_iniPath, lines, Encoding.Default);
            FileLogger.Info($"EQKeymaps: saved {changed.Count} changed keymap(s) to eqclient.ini");

            // Update snapshot
            foreach (var (key, nud) in _nudValues)
                _initialValues[key] = (long)nud.Value;
        }
        catch (Exception ex)
        {
            FileLogger.Error("EQKeymaps: save error", ex);
            MessageBox.Show($"Failed to save: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }
}
