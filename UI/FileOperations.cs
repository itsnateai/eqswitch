using System.Diagnostics;
using EQSwitch.Config;
using EQSwitch.Core;

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
            using var _ = Process.Start("explorer.exe", logsDir);
            return;
        }

        if (logFiles.Length == 1)
        {
            OpenWithDefaultEditor(logFiles[0]);
            return;
        }

        // Multiple logs — show picker with most recent files
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) =>
        {
            // Defer dispose so click handlers fire before the menu is destroyed
            var timer = new System.Windows.Forms.Timer { Interval = 1 };
            timer.Tick += (s, _) => { timer.Stop(); timer.Dispose(); menu.Dispose(); };
            timer.Start();
        };
        foreach (var logFile in logFiles.Take(10))
        {
            var name = Path.GetFileNameWithoutExtension(logFile);
            var lastWrite = File.GetLastWriteTime(logFile);
            var path = logFile;
            menu.Items.Add($"{name} ({lastWrite:g})", null, (_, _) => OpenWithDefaultEditor(path));
        }
        if (logFiles.Length > 10)
            menu.Items.Add($"({logFiles.Length - 10} more — open folder)", null, (_, _) =>
                { using var p = Process.Start("explorer.exe", logsDir); });

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

    /// <summary>
    /// Launch the Dalaya patcher. Fails silently if path is empty or exe is
    /// missing (Windows Defender often deletes it). Only shows a balloon if
    /// the path is configured but the file was removed.
    /// </summary>
    public static void OpenDalayaPatcher(AppConfig config, Action<string> showBalloon, Action? openPathsTab = null)
    {
        if (string.IsNullOrEmpty(config.DalayaPatcherPath))
        {
            if (openPathsTab != null)
                openPathsTab();
            else
                showBalloon("Dalaya patcher path not set.\nConfigure it in Settings → Paths.");
            return;
        }

        if (!File.Exists(config.DalayaPatcherPath))
        {
            showBalloon("Dalaya patcher not found — may have been removed by antivirus.\nRe-download or update the path in Settings.");
            return;
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = config.DalayaPatcherPath,
                WorkingDirectory = Path.GetDirectoryName(config.DalayaPatcherPath) ?? "",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"OpenDalayaPatcher failed: {ex.Message}");
            showBalloon($"Failed to launch patcher: {ex.Message}");
        }
    }

    public static void OpenGina(AppConfig config, Action<string> showBalloon, Action? openPathsTab = null)
    {
        if (string.IsNullOrEmpty(config.GinaPath) || !File.Exists(config.GinaPath))
        {
            if (openPathsTab != null)
                openPathsTab();
            else
                showBalloon("GINA path not configured or file not found.\nSet it in Settings.");
            return;
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = config.GinaPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"OpenGina failed: {ex.Message}");
            showBalloon($"Failed to launch GINA: {ex.Message}");
        }
    }

    public static void OpenNotes(AppConfig config, Action<string> showBalloon)
    {
        var notesPath = config.NotesPath;

        if (string.IsNullOrEmpty(notesPath))
        {
            notesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eqnotes.txt");
            config.NotesPath = notesPath;
            ConfigManager.Save(config);
        }

        if (!File.Exists(notesPath))
        {
            try
            {
                File.WriteAllText(notesPath, "# EQSwitch Notes\n\n");
                FileLogger.Info($"Created notes file: {notesPath}");
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"OpenNotes: failed to create {notesPath} — {ex.Message}");
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
            using var proc = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"OpenUrl failed for {url}: {ex.Message}");
        }
    }

    /// <summary>
    /// Trim EQ log files over 10MB by keeping only the last 10MB of each.
    /// </summary>
    public static void TrimLogFiles(AppConfig config, Action<string> showBalloon)
    {
        var logsDir = Path.Combine(config.EQPath, "Logs");
        if (!Directory.Exists(logsDir))
        {
            showBalloon("Logs folder not found");
            return;
        }

        var logFiles = Directory.GetFiles(logsDir, "eqlog_*.txt");
        const long maxSize = 10 * 1024 * 1024; // 10MB
        int trimmed = 0;
        long totalFreed = 0;

        foreach (var logFile in logFiles)
        {
            try
            {
                var fi = new FileInfo(logFile);
                if (fi.Length <= maxSize) continue;

                long originalSize = fi.Length;
                long keepOffset = fi.Length - maxSize;

                // Read the last 10MB
                byte[] tail;
                using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(keepOffset, SeekOrigin.Begin);
                    tail = new byte[fi.Length - keepOffset];
                    int read = 0;
                    while (read < tail.Length)
                    {
                        int n = fs.Read(tail, read, tail.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                }

                // Find the first newline to avoid a partial line at the start
                int start = Array.IndexOf(tail, (byte)'\n');
                if (start < 0) start = 0; else start++;

                // Write back
                using (var fs = new FileStream(logFile, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(tail, start, tail.Length - start);
                }

                totalFreed += originalSize - (tail.Length - start);
                trimmed++;
                FileLogger.Info($"TrimLogFiles: {Path.GetFileName(logFile)} trimmed from {originalSize / 1024 / 1024}MB to {(tail.Length - start) / 1024 / 1024}MB");
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"TrimLogFiles: failed to trim {Path.GetFileName(logFile)} — {ex.Message}");
            }
        }

        if (trimmed == 0)
            showBalloon($"All {logFiles.Length} log files are under 10MB — nothing to trim");
        else
            showBalloon($"Trimmed {trimmed} log file(s), freed {totalFreed / 1024 / 1024}MB");
    }

    public static void OpenWithDefaultEditor(string path)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"OpenWithDefaultEditor failed for {path}: {ex.Message}");
        }
    }
}
