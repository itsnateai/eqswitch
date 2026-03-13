using System.Diagnostics;
using EQSwitch.Config;

namespace EQSwitch.UI;

/// <summary>
/// Handles file/URL opening operations for the tray context menu.
/// </summary>
public static class FileOperations
{
    /// <summary>
    /// Open an EQ log file. Shows a picker if multiple characters exist,
    /// otherwise opens the Logs folder in the EQ directory.
    /// </summary>
    public static void OpenLogFile(AppConfig config, Action<string> showBalloon)
    {
        var logsDir = Path.Combine(config.EQPath, "Logs");
        if (!Directory.Exists(logsDir))
        {
            showBalloon("Logs folder not found");
            return;
        }

        var logFiles = Directory.GetFiles(logsDir, "eqlog_*.txt")
            .OrderByDescending(File.GetLastWriteTime)
            .ToArray();

        if (logFiles.Length == 0)
        {
            Process.Start("explorer.exe", logsDir);
            return;
        }

        if (logFiles.Length == 1)
        {
            OpenWithDefaultEditor(logFiles[0]);
            return;
        }

        // Multiple logs — show picker with most recent files
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) => menu.Dispose();
        foreach (var logFile in logFiles.Take(10))
        {
            var name = Path.GetFileNameWithoutExtension(logFile);
            var lastWrite = File.GetLastWriteTime(logFile);
            var path = logFile;
            menu.Items.Add($"{name} ({lastWrite:g})", null, (_, _) => OpenWithDefaultEditor(path));
        }
        if (logFiles.Length > 10)
            menu.Items.Add($"({logFiles.Length - 10} more — open folder)", null, (_, _) =>
                Process.Start("explorer.exe", logsDir));

        menu.Show(Cursor.Position);
    }

    public static void OpenEqClientIni(AppConfig config, Action<string> showBalloon)
    {
        var iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        if (!File.Exists(iniPath))
        {
            showBalloon("eqclient.ini not found");
            return;
        }
        OpenWithDefaultEditor(iniPath);
    }

    public static void OpenGina(AppConfig config, Action<string> showBalloon)
    {
        if (string.IsNullOrEmpty(config.GinaPath) || !File.Exists(config.GinaPath))
        {
            showBalloon("GINA path not configured or file not found.\nSet it in Settings.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = config.GinaPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenGina failed: {ex.Message}");
            showBalloon($"Failed to launch GINA: {ex.Message}");
        }
    }

    public static void OpenNotes(AppConfig config, Action<string> showBalloon)
    {
        var notesPath = config.NotesPath;

        if (string.IsNullOrEmpty(notesPath))
        {
            notesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eqswitch-notes.txt");
            config.NotesPath = notesPath;
            ConfigManager.Save(config);
        }

        if (!File.Exists(notesPath))
        {
            try
            {
                File.WriteAllText(notesPath, "# EQSwitch Notes\n\n");
                Debug.WriteLine($"Created notes file: {notesPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OpenNotes: failed to create {notesPath} — {ex.Message}");
                showBalloon($"Failed to create notes file: {ex.Message}");
                return;
            }
        }

        OpenWithDefaultEditor(notesPath);
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenUrl failed for {url}: {ex.Message}");
        }
    }

    public static void OpenWithDefaultEditor(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenWithDefaultEditor failed for {path}: {ex.Message}");
        }
    }
}
