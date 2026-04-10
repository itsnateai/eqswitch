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
    /// Trim EQ log files over the threshold. Archived data goes to Logs/archive/.
    /// Stream-based: never loads the full file into memory.
    /// </summary>
    public static void TrimLogFiles(AppConfig config, Action<string> showBalloon)
        => TrimLogFiles(config, config.LogTrimThresholdMB, showBalloon);

    public static void TrimLogFiles(AppConfig config, int thresholdMB, Action<string> showBalloon)
    {
        var logsDir = Path.Combine(config.EQPath, "Logs");
        if (!Directory.Exists(logsDir))
        {
            showBalloon("Logs folder not found");
            return;
        }

        var logFiles = Directory.GetFiles(logsDir, "eqlog_*.txt");
        long maxSize = (long)thresholdMB * 1024 * 1024;
        var archiveDir = Path.Combine(logsDir, "archive");
        int thresholdCopy = thresholdMB;

        // Run on background thread so large files don't freeze the UI
        showBalloon("Trimming log files...");
        Task.Run(() =>
        {
            int trimmed = 0;
            long totalFreed = 0;

            foreach (var logFile in logFiles)
            {
                try
                {
                    using var fs = new FileStream(logFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    long fileLen = fs.Length;
                    if (fileLen <= maxSize) continue;

                    long splitAt = fileLen - maxSize;

                    // Scan forward from splitAt to find a clean line boundary
                    fs.Seek(splitAt, SeekOrigin.Begin);
                    int b;
                    while ((b = fs.ReadByte()) != -1)
                    {
                        splitAt++;
                        if (b == '\n') break;
                    }

                    // Archive the old portion (stream-copy in 64KB chunks)
                    Directory.CreateDirectory(archiveDir);
                    var baseName = Path.GetFileNameWithoutExtension(logFile);
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff");
                    var archivePath = Path.Combine(archiveDir, $"{baseName}_{timestamp}.txt");

                    fs.Seek(0, SeekOrigin.Begin);
                    using (var archive = new FileStream(archivePath, FileMode.Create, FileAccess.Write))
                    {
                        var buf = new byte[65536];
                        long remaining = splitAt;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(buf.Length, remaining);
                            int n = fs.Read(buf, 0, toRead);
                            if (n == 0) break;
                            archive.Write(buf, 0, n);
                            remaining -= n;
                        }
                    }

                    // Read the tail we're keeping into a buffer, then overwrite the file
                    fs.Seek(splitAt, SeekOrigin.Begin);
                    long tailLen = fileLen - splitAt;
                    using var tailStream = new MemoryStream((int)Math.Min(tailLen, int.MaxValue));
                    fs.CopyTo(tailStream);

                    fs.Seek(0, SeekOrigin.Begin);
                    fs.SetLength(tailStream.Length);
                    tailStream.Seek(0, SeekOrigin.Begin);
                    tailStream.CopyTo(fs);

                    totalFreed += splitAt;
                    trimmed++;
                    FileLogger.Info($"TrimLogFiles: {Path.GetFileName(logFile)} trimmed {fileLen / 1024 / 1024}MB → {tailStream.Length / 1024 / 1024}MB (archived {splitAt / 1024 / 1024}MB)");
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"TrimLogFiles: failed to trim {Path.GetFileName(logFile)} — {ex.Message}");
                }
            }

            // Marshal result back to UI thread
            string msg = trimmed == 0
                ? $"All {logFiles.Length} log file(s) are under {thresholdCopy}MB — nothing to trim"
                : $"Trimmed {trimmed} log file(s), freed {totalFreed / 1024 / 1024}MB\nArchived to Logs/archive/";

            if (System.Windows.Forms.Application.OpenForms.Count > 0)
                System.Windows.Forms.Application.OpenForms[0]!.BeginInvoke(() => showBalloon(msg));
            else
                FileLogger.Info($"TrimLogFiles: {msg}");
        });
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
