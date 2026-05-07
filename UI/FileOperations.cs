// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Diagnostics;
using System.Threading;
using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Handles file/URL opening operations for the tray context menu.
/// </summary>
public static class FileOperations
{
    private const int MaxRecentLogsInPicker = 25;

    // Re-entry guard for TrimLogFiles. 0 = idle, 1 = trim in progress.
    // CompareExchange ensures rapid menu double-clicks don't spawn overlapping
    // Tasks that race on the same `<logFile>.trim.tmp` paths.
    private static int _trimInProgress;

    /// <summary>
    /// Open an EQ log file. Shows a picker if multiple characters exist,
    /// otherwise opens the Logs folder in the EQ directory. If the EQ Path
    /// is unset (Logs folder not found), opens Settings → General if a tab
    /// callback is provided.
    /// </summary>
    public static void OpenLogFile(AppConfig config, Action<string> showBalloon, Action? openGeneralTab = null)
    {
        if (string.IsNullOrEmpty(config.EQPath))
        {
            showBalloon("EQ Path not set.\nConfigure it in Settings → General.");
            openGeneralTab?.Invoke();
            return;
        }

        var logsDir = Path.Combine(config.EQPath, "Logs");
        if (!Directory.Exists(logsDir))
        {
            showBalloon($"Logs folder not found at:\n{logsDir}\n\nCheck EQ Path in Settings → General.");
            openGeneralTab?.Invoke();
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
        foreach (var logFile in logFiles.Take(MaxRecentLogsInPicker))
        {
            var name = Path.GetFileNameWithoutExtension(logFile);
            var lastWrite = File.GetLastWriteTime(logFile);
            var path = logFile;
            menu.Items.Add($"{name} ({lastWrite:g})", null, (_, _) => OpenWithDefaultEditor(path));
        }
        if (logFiles.Length > MaxRecentLogsInPicker)
            menu.Items.Add($"({logFiles.Length - MaxRecentLogsInPicker} more — open folder)", null, (_, _) =>
                { using var p = Process.Start("explorer.exe", logsDir); });

        menu.Show(Cursor.Position);
    }

    public static void OpenEqClientIni(AppConfig config, Action<string> showBalloon, Action? openGeneralTab = null)
    {
        if (string.IsNullOrEmpty(config.EQPath))
        {
            showBalloon("EQ Path not set.\nConfigure it in Settings → General.");
            openGeneralTab?.Invoke();
            return;
        }

        var iniPath = Path.Combine(config.EQPath, "eqclient.ini");
        if (!File.Exists(iniPath))
        {
            showBalloon($"eqclient.ini not found at:\n{iniPath}\n\nCheck EQ Path in Settings → General.");
            openGeneralTab?.Invoke();
            return;
        }
        OpenWithDefaultEditor(iniPath);
    }

    /// <summary>
    /// Launch the Dalaya patcher. If the path is unconfigured, opens Settings → Paths
    /// (or balloons if openPathsTab is null). If the path is set but the exe is missing
    /// (Windows Defender often deletes it), shows an AV-aware balloon and opens
    /// Settings → Paths so the user can re-download or re-point the path.
    /// </summary>
    public static void OpenDalayaPatcher(AppConfig config, Action<string> showBalloon, Action? openPathsTab = null)
    {
        var rawPath = config.DalayaPatcherPath;

        if (string.IsNullOrEmpty(rawPath))
        {
            if (openPathsTab != null)
                openPathsTab();
            else
                showBalloon("Dalaya patcher path not set.\nConfigure it in Settings → Paths.");
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(rawPath);
        var path = expanded.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path.Substring(1, path.Length - 2);

        if (!Path.IsPathFullyQualified(path))
        {
            var detail = expanded == rawPath ? rawPath : $"{rawPath}\nResolved to: {path}";
            showBalloon($"Dalaya patcher path must be a full path (e.g. C:\\Dalaya\\patcher.exe).\nGot: {detail}");
            openPathsTab?.Invoke();
            return;
        }

        if (!File.Exists(path))
        {
            var locationDetail = expanded == rawPath ? path : $"{path}\n(from: {rawPath})";
            showBalloon($"Dalaya patcher not found at:\n{locationDetail}\n\nMay have been removed by antivirus — re-download or update the path in Settings.");
            openPathsTab?.Invoke();
            return;
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
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
        var rawPath = config.GinaPath;

        if (string.IsNullOrEmpty(rawPath))
        {
            if (openPathsTab != null)
                openPathsTab();
            else
                showBalloon("GINA path not set.\nConfigure it in Settings → Paths.");
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(rawPath);
        var path = expanded.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path.Substring(1, path.Length - 2);

        if (!Path.IsPathFullyQualified(path))
        {
            var detail = expanded == rawPath ? rawPath : $"{rawPath}\nResolved to: {path}";
            showBalloon($"GINA path must be a full path (e.g. C:\\Tools\\GINA.exe).\nGot: {detail}");
            openPathsTab?.Invoke();
            return;
        }

        if (!File.Exists(path))
        {
            var locationDetail = expanded == rawPath ? path : $"{path}\n(from: {rawPath})";
            showBalloon($"GINA not found at:\n{locationDetail}\n\nThe file may have been moved or uninstalled.");
            openPathsTab?.Invoke();
            return;
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"OpenGina failed: {ex.Message}");
            showBalloon($"Failed to launch GINA: {ex.Message}");
        }
    }

    public static void OpenGamparse(AppConfig config, Action<string> showBalloon, Action? openPathsTab = null)
    {
        var rawPath = config.GamparsePath;

        if (string.IsNullOrEmpty(rawPath))
        {
            if (openPathsTab != null)
                openPathsTab();
            else
                showBalloon("Gamparse path not set.\nConfigure it in Settings → Paths.");
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(rawPath);
        var path = expanded.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path.Substring(1, path.Length - 2);

        if (!Path.IsPathFullyQualified(path))
        {
            var detail = expanded == rawPath ? rawPath : $"{rawPath}\nResolved to: {path}";
            showBalloon($"Gamparse path must be a full path (e.g. C:\\Tools\\Gamparse.exe).\nGot: {detail}");
            openPathsTab?.Invoke();
            return;
        }

        if (!File.Exists(path))
        {
            var locationDetail = expanded == rawPath ? path : $"{path}\n(from: {rawPath})";
            showBalloon($"Gamparse not found at:\n{locationDetail}\n\nThe file may have been moved or uninstalled.");
            openPathsTab?.Invoke();
            return;
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"OpenGamparse failed: {ex.Message}");
            showBalloon($"Failed to launch Gamparse: {ex.Message}");
        }
    }

    public static void OpenEqLogParser(AppConfig config, Action<string> showBalloon, Action? openPathsTab = null)
    {
        var rawPath = config.EqLogParserPath;

        if (string.IsNullOrEmpty(rawPath))
        {
            if (openPathsTab != null)
                openPathsTab();
            else
                showBalloon("EQLogParser path not set.\nConfigure it in Settings → Paths.");
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(rawPath);
        var path = expanded.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path.Substring(1, path.Length - 2);

        if (!Path.IsPathFullyQualified(path))
        {
            var detail = expanded == rawPath ? rawPath : $"{rawPath}\nResolved to: {path}";
            showBalloon($"EQLogParser path must be a full path (e.g. C:\\Tools\\EQLogParser.exe).\nGot: {detail}");
            openPathsTab?.Invoke();
            return;
        }

        if (!File.Exists(path))
        {
            var locationDetail = expanded == rawPath ? path : $"{path}\n(from: {rawPath})";
            showBalloon($"EQLogParser not found at:\n{locationDetail}\n\nThe file may have been moved or uninstalled.");
            openPathsTab?.Invoke();
            return;
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"OpenEqLogParser failed: {ex.Message}");
            showBalloon($"Failed to launch EQLogParser: {ex.Message}");
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

    public static void OpenUrl(string url, Action<string> showBalloon)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"OpenUrl failed for {url}: {ex.Message}");
            showBalloon($"Failed to open link: {ex.Message}");
        }
    }

    /// <summary>
    /// Trim EQ log files over the threshold. Archived data goes to Logs/archive/.
    ///
    /// Fully stream-based: tail streamed to a temp file then atomically swapped
    /// via File.Replace, so a process crash mid-trim leaves the original log
    /// intact. 2 GB+ tails handled correctly (no MemoryStream cap).
    ///
    /// Read-pass opens with FileShare.Read — if EQ (or any other writer) has
    /// the log open, the read open fails fast with a sharing violation that
    /// the per-file catch logs as a skip. This prevents the silent-corruption
    /// mode where File.Replace would otherwise swap a trimmed file under EQ's
    /// stale write handle.
    ///
    /// Re-entry guarded via Interlocked: rapid menu double-clicks see the
    /// "already in progress" balloon instead of racing on temp-file paths.
    ///
    /// Edge case: if a single line spans the entire trim window (no newline
    /// between split point and EOF), the file is skipped rather than silently
    /// emptied — better to leave the log alone than wipe legitimate data.
    /// </summary>
    public static void TrimLogFiles(AppConfig config, Action<string> showBalloon)
        => TrimLogFiles(config, config.LogTrimThresholdMB, showBalloon);

    public static void TrimLogFiles(AppConfig config, int thresholdMB, Action<string> showBalloon)
    {
        if (string.IsNullOrEmpty(config.EQPath))
        {
            showBalloon("EQ Path not set.\nConfigure it in Settings → General.");
            return;
        }

        var logsDir = Path.Combine(config.EQPath, "Logs");
        if (!Directory.Exists(logsDir))
        {
            showBalloon("Logs folder not found");
            return;
        }

        // Re-entry guard: refuse second invocation while one is in flight.
        if (Interlocked.CompareExchange(ref _trimInProgress, 1, 0) != 0)
        {
            showBalloon("Log trim already in progress.");
            return;
        }

        var logFiles = Directory.GetFiles(logsDir, "eqlog_*.txt");
        long maxSize = (long)thresholdMB * 1024 * 1024;
        var archiveDir = Path.Combine(logsDir, "archive");
        int thresholdCopy = thresholdMB;

        // Capture UI sync context here (we're on the UI thread). The Task below
        // posts the result balloon back through this context — robust to forms
        // closing during the trim, unlike Application.OpenForms[0].
        var uiContext = SynchronizationContext.Current;

        Task.Run(() =>
        {
            int trimmed = 0;
            int skipped = 0;
            long totalFreed = 0;

            try
            {
                foreach (var logFile in logFiles)
                {
                    string? tempPath = null;
                    try
                    {
                        long fileLen, splitAt, tailLen;

                        // Pass 1: scan + archive head + stream tail to temp (read-only access).
                        // FileShare.Read fails fast if a writer (EQ) has it open.
                        using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            fileLen = fs.Length;
                            if (fileLen <= maxSize) continue;

                            splitAt = fileLen - maxSize;

                            // Scan forward from splitAt to find a clean line boundary
                            fs.Seek(splitAt, SeekOrigin.Begin);
                            int b;
                            while ((b = fs.ReadByte()) != -1)
                            {
                                splitAt++;
                                if (b == '\n') break;
                            }

                            // Single line spans the entire trim window — refuse to
                            // trim rather than silently empty the live log.
                            if (splitAt >= fileLen)
                            {
                                FileLogger.Warn($"TrimLogFiles: skipped {Path.GetFileName(logFile)} — no line boundary found near {thresholdCopy}MB threshold");
                                skipped++;
                                continue;
                            }

                            // Archive the head (stream-copy in 64KB chunks)
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

                            // Stream the tail to a temp file alongside the log.
                            // Disk-backed (not MemoryStream) so 2 GB+ tails work
                            // and OOM pressure stays off the LOH.
                            tempPath = logFile + ".trim.tmp";
                            fs.Seek(splitAt, SeekOrigin.Begin);
                            using (var tail = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                            {
                                fs.CopyTo(tail);
                            }
                            tailLen = new FileInfo(tempPath).Length;
                        }
                        // fs closed — atomic swap.
                        File.Replace(tempPath, logFile, destinationBackupFileName: null);
                        tempPath = null; // consumed by Replace

                        totalFreed += splitAt;
                        trimmed++;
                        FileLogger.Info($"TrimLogFiles: {Path.GetFileName(logFile)} trimmed {fileLen / 1024 / 1024}MB → {tailLen / 1024 / 1024}MB (archived {splitAt / 1024 / 1024}MB)");
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        FileLogger.Warn($"TrimLogFiles: failed to trim {Path.GetFileName(logFile)} — {ex.Message}");
                    }
                    finally
                    {
                        // Clean up orphan temp if Replace didn't consume it
                        if (tempPath != null && File.Exists(tempPath))
                        {
                            try { File.Delete(tempPath); } catch { /* best-effort */ }
                        }
                    }
                }

                // Honest summary: distinguish "nothing needed trimming" from "we
                // couldn't trim anything because files were locked".
                string msg = (trimmed, skipped) switch
                {
                    (0, 0) => $"All {logFiles.Length} log file(s) under {thresholdCopy}MB — nothing to trim",
                    (_, 0) => $"Trimmed {trimmed} log file(s), freed {totalFreed / 1024 / 1024}MB\nArchived to Logs/archive/",
                    (0, _) => $"Could not trim any logs — {skipped} skipped (file in use or other error). Check the log for details.",
                    _      => $"Trimmed {trimmed}, skipped {skipped} (file in use or error)\nFreed {totalFreed / 1024 / 1024}MB — see log for details",
                };

                if (uiContext != null)
                    uiContext.Post(_ => showBalloon(msg), null);
                else
                    FileLogger.Info($"TrimLogFiles: {msg}");
            }
            finally
            {
                Interlocked.Exchange(ref _trimInProgress, 0);
            }
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
