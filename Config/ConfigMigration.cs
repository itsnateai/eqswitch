using System.Diagnostics;
using System.Text;

namespace EQSwitch.Config;

/// <summary>
/// Migrates AHK eqswitch.cfg (INI format) to the new JSON config.
/// Called on first run if eqswitch.cfg exists alongside the exe.
/// </summary>
public static class ConfigMigration
{
    /// <summary>
    /// Attempt to import settings from eqswitch.cfg.
    /// Returns a populated AppConfig if found, null otherwise.
    /// </summary>
    public static AppConfig? TryImportFromAhk()
    {
        var cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eqswitch.cfg");
        if (!File.Exists(cfgPath))
            return null;

        Debug.WriteLine($"ConfigMigration: found AHK config at {cfgPath}");

        try
        {
            // AHK's IniWrite uses system default encoding (typically ANSI/Windows-1252)
            var lines = File.ReadAllLines(cfgPath, Encoding.Default);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            bool inSection = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("["))
                {
                    inSection = trimmed.Equals("[EQSwitch]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection || string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";")) continue;

                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                    values[parts[0].Trim()] = parts[1].Trim();
            }

            if (values.Count == 0)
            {
                Debug.WriteLine("ConfigMigration: no values found in [EQSwitch] section");
                return null;
            }

            var config = new AppConfig { IsFirstRun = false };

            // EQ Path — extract directory from EQ_EXE path
            if (values.TryGetValue("EQ_EXE", out var eqExe) && !string.IsNullOrEmpty(eqExe))
            {
                config.EQPath = Path.GetDirectoryName(eqExe) ?? config.EQPath;
                config.Launch.ExeName = Path.GetFileName(eqExe);
            }

            if (values.TryGetValue("EQ_ARGS", out var args))
                config.Launch.Arguments = args.TrimStart('-');

            // Hotkeys — convert AHK syntax to our format
            if (values.TryGetValue("EQ_HOTKEY", out var switchKey))
                config.Hotkeys.SwitchKey = switchKey;

            if (values.TryGetValue("FOCUS_HOTKEY", out var focusKey))
                config.Hotkeys.GlobalSwitchKey = focusKey;

            if (values.TryGetValue("MULTIMON_HOTKEY", out var mmKey))
                config.Hotkeys.ToggleMultiMonitor = ConvertAhkHotkey(mmKey);

            if (values.TryGetValue("LAUNCH_ONE_HOTKEY", out var l1Key) && !string.IsNullOrEmpty(l1Key))
                config.Hotkeys.LaunchOne = ConvertAhkHotkey(l1Key);

            if (values.TryGetValue("LAUNCH_ALL_HOTKEY", out var laKey) && !string.IsNullOrEmpty(laKey))
                config.Hotkeys.LaunchAll = ConvertAhkHotkey(laKey);

            if (values.TryGetValue("MULTIMON_ENABLED", out var mmEnabled))
                config.Hotkeys.MultiMonitorEnabled = mmEnabled == "1";

            // Layout
            if (values.TryGetValue("FIX_MODE", out var fixMode))
                config.Layout.Mode = fixMode.Contains("multi", StringComparison.OrdinalIgnoreCase) ? "multimonitor" : "single";

            if (values.TryGetValue("TARGET_MONITOR", out var targetMon) && int.TryParse(targetMon, out int mon))
                config.Layout.TargetMonitor = Math.Max(0, mon - 1); // AHK is 1-indexed

            if (values.TryGetValue("FIX_TOP_OFFSET", out var topOff) && int.TryParse(topOff, out int to))
                config.Layout.TopOffset = to;

            if (values.TryGetValue("NUM_CLIENTS", out var numClients) && int.TryParse(numClients, out int nc))
            {
                config.Launch.NumClients = Math.Clamp(nc, 1, 8);
            }

            // Launch
            if (values.TryGetValue("LAUNCH_DELAY", out var launchDelay) && int.TryParse(launchDelay, out int ld))
                config.Launch.LaunchDelayMs = ld;

            if (values.TryGetValue("LAUNCH_FIX_DELAY", out var fixDelay) && int.TryParse(fixDelay, out int fd))
                config.Launch.FixDelayMs = fd;

            // Affinity
            if (values.TryGetValue("CPU_AFFINITY", out var affinity) && !string.IsNullOrEmpty(affinity))
            {
                // Legacy AHK used a bitmask — just enable affinity, cores managed via eqclient.ini now
                if (long.TryParse(affinity, out long mask) && mask > 0)
                    config.Affinity.Enabled = true;
            }

            if (values.TryGetValue("PROCESS_PRIORITY", out var priority))
                config.Affinity.ActivePriority = priority;

            // PiP
            if (values.TryGetValue("PIP_WIDTH", out var pipW) && int.TryParse(pipW, out int pw))
                config.Pip.CustomWidth = pw;

            if (values.TryGetValue("PIP_HEIGHT", out var pipH) && int.TryParse(pipH, out int ph))
                config.Pip.CustomHeight = ph;

            if (values.TryGetValue("PIP_OPACITY", out var pipOp) && int.TryParse(pipOp, out int po))
                config.Pip.Opacity = (byte)Math.Clamp(po, 0, 255);

            if (values.TryGetValue("PIP_BORDER_ENABLED", out var pipBorder))
                config.Pip.ShowBorder = pipBorder == "1";

            if (values.TryGetValue("BORDER_COLOR", out var borderColor) && !string.IsNullOrEmpty(borderColor))
            {
                // AHK stores as hex BGR without prefix. Map to our named colors.
                config.Pip.BorderColor = borderColor.ToUpperInvariant() switch
                {
                    "00FF00" => "Green",
                    "FF8000" or "FF0080" => "Blue",
                    "0000FF" => "Red",
                    "000000" => "Black",
                    _ => "Green"
                };
            }

            // Match PiP size to a preset if possible
            config.Pip.SizePreset = (config.Pip.CustomWidth, config.Pip.CustomHeight) switch
            {
                (200, 150) => "Small",
                (320, 240) => "Medium",
                (400, 300) => "Large",
                (480, 360) => "XL",
                (640, 480) => "XXL",
                _ => "Custom"
            };

            // PiP position
            if (values.TryGetValue("PIP_X", out var pipX) && values.TryGetValue("PIP_Y", out var pipY))
            {
                if (int.TryParse(pipX, out int px) && int.TryParse(pipY, out int py))
                    config.Pip.SavedPositions.Add(new[] { px, py });
            }

            // Paths
            if (values.TryGetValue("GINA_PATH", out var ginaPath))
                config.GinaPath = ginaPath;

            if (values.TryGetValue("NOTES_FILE", out var notesFile))
                config.NotesPath = notesFile;

            // Startup
            if (values.TryGetValue("STARTUP_ENABLED", out var startup))
                config.RunAtStartup = startup == "1";

            int imported = values.Count;
            Debug.WriteLine($"ConfigMigration: imported {imported} values from AHK config");

            return config;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ConfigMigration: import failed — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Convert AHK hotkey syntax to our format.
    /// AHK: ! = Alt, ^ = Ctrl, + = Shift, > = Right modifier
    /// Ours: Alt+Key, Ctrl+Key, Shift+Key
    /// </summary>
    private static string ConvertAhkHotkey(string ahkKey)
    {
        if (string.IsNullOrEmpty(ahkKey)) return "";

        // Strip right-modifier prefix
        ahkKey = ahkKey.Replace(">", "").Replace("<", "");

        var modifiers = new List<string>();
        int i = 0;

        while (i < ahkKey.Length)
        {
            switch (ahkKey[i])
            {
                case '!': modifiers.Add("Alt"); i++; break;
                case '^': modifiers.Add("Ctrl"); i++; break;
                case '+': modifiers.Add("Shift"); i++; break;
                default: goto done;
            }
        }
        done:

        string key = ahkKey[i..].ToUpperInvariant();

        if (modifiers.Count == 0)
            return key;

        return string.Join("+", modifiers) + "+" + key;
    }
}
