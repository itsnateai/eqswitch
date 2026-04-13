using EQSwitch.Config;
using EQSwitch.Core;
using EQSwitch.UI;

namespace EQSwitch;

static class Program
{
    // Single-instance mutex to prevent multiple copies running
    private static Mutex? _mutex;

    [STAThread]
    static void Main(string[] args)
    {
        // Enforce single instance
        const string mutexName = "EQSwitch_SingleInstance_SoD";
        _mutex = new Mutex(true, mutexName, out bool createdNew);

        // After self-update, the old instance may still be shutting down
        if (!createdNew && args.Contains("--after-update"))
        {
            for (int i = 0; i < 10 && !createdNew; i++)
            {
                Thread.Sleep(500);
                _mutex.Dispose();
                _mutex = new Mutex(true, mutexName, out createdNew);
            }
        }

        if (!createdNew)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FloatingTooltip.Show("EQSwitch is already running. Check your system tray.", 4000);
            // Brief delay so tooltip is visible before process exits
            Thread.Sleep(4200);
            return;
        }

        bool isAfterUpdate = args.Contains("--after-update");

        FileLogger.Initialize();
        CleanupUpdateArtifacts();

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Set an app-managed default font. Without this, controls use the
        // static Control.DefaultFont (SystemFonts.DefaultFont) which gets
        // invalidated during runtime — possibly by display change events,
        // DPI context switches, or GDI+ cleanup in this tray-only app.
        // Controls then throw "Parameter is not valid" on construction.
        Application.SetDefaultFont(new Font("Segoe UI", 9f));

        // Catch UI thread exceptions BEFORE WinForms tries to show ThreadExceptionDialog
        // (which itself crashes due to GDI+ font corruption, hiding the real error)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            FileLogger.Error("UI thread exception (original)", e.Exception);
            try
            {
                MessageBox.Show(
                    $"EQSwitch encountered an error:\n\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\n{e.Exception.StackTrace}",
                    "EQSwitch Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // If even MessageBox fails (GDI+ corruption), at least we logged it
            }
        };

        try
        {
            var config = ConfigManager.Load();

            // First-run: try migrating from AHK config, then show EQ path picker
            bool isNewUser = false;
            if (config.IsFirstRun)
            {
                var migrated = ConfigMigration.TryImportFromAhk();
                if (migrated != null)
                {
                    config = migrated;
                    isNewUser = true;
                    MessageBox.Show(
                        "Imported settings from eqswitch.cfg (AHK version).\nCheck Settings to verify everything looks right.",
                        "EQSwitch — Migration",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else
                {
                    using var dialog = new FirstRunDialog();
                    if (dialog.ShowDialog() != DialogResult.OK)
                        return; // User cancelled — don't start

                    config.EQPath = dialog.SelectedEQPath;
                    config.IsFirstRun = false;
                    isNewUser = true;
                }

                // Seed EQ client settings from actual ini so AppConfig reflects reality
                // instead of hardcoded defaults — prevents silent overwrites on first Save
                if (!string.IsNullOrEmpty(config.EQPath))
                {
                    var iniPath = Path.Combine(config.EQPath, "eqclient.ini");
                    config.EQClientIni = EQClientIniConfig.SeedFromIni(iniPath);
                }

                ConfigManager.Save(config);
                ConfigManager.FlushSave();
            }


            var processManager = new ProcessManager(config);
            var trayApp = new TrayManager(config, processManager);

            // Ensure cleanup on any exit path (not just tray menu Exit)
            Application.ApplicationExit += (_, _) => trayApp.Dispose();

            trayApp.Initialize();

            // Auto-open Settings on first run so new users can configure
            if (isNewUser)
                trayApp.OpenSettingsAfterDelay();

            // Show confirmation after successful self-update (delayed so tray is ready)
            if (isAfterUpdate)
            {
                var postUpdateTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                postUpdateTimer.Tick += (_, _) =>
                {
                    postUpdateTimer.Stop();
                    postUpdateTimer.Dispose();
                    var version = System.Reflection.Assembly.GetExecutingAssembly()
                        .GetName().Version?.ToString(3) ?? "?";
                    FloatingTooltip.Show($"✅ EQSwitch updated to v{version}!", 5000);
                };
                postUpdateTimer.Start();
            }

            // --test-update: simulate update flow without hitting GitHub
#if DEBUG
            if (args.Contains("--test-update"))
            {
                UpdateDialog.TestMode = true;
                var timer = new System.Windows.Forms.Timer { Interval = 500 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    using var dlg = new UpdateDialog();
                    dlg.ShowDialog();
                };
                timer.Start();
            }
#endif

            Application.Run();
        }
        catch (Exception ex)
        {
            FileLogger.Error("Fatal error", ex);
            MessageBox.Show(
                $"EQSwitch encountered a fatal error:\n\n{ex.Message}",
                "EQSwitch Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            ConfigManager.FlushSave();
            ConfigManager.Shutdown();
            FileLogger.Shutdown();
            if (createdNew) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
    }

    private static void CleanupUpdateArtifacts()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var exePath = Path.Combine(dir, "EQSwitch.exe");

        // Torn-state recovery: if exe is missing but .old exists, restore it
        if (!File.Exists(exePath))
        {
            var oldPath = exePath + ".old";
            if (File.Exists(oldPath))
            {
                try
                {
                    File.Move(oldPath, exePath);
                    FileLogger.Warn("Recovered EQSwitch.exe from .old after interrupted update.");
                }
                catch (Exception ex)
                {
                    FileLogger.Error($"Failed to recover EQSwitch.exe from .old: {ex.Message}");
                }
            }
            return;
        }

        // Clean up each artifact independently so one locked file doesn't block the rest.
        // Retry exe.old — the old process may still be releasing the memory-mapped file.
        foreach (var pattern in new[] { "EQSwitch.exe.old", "EQSwitch.exe.new",
                                        "eqswitch-hook.dll.old", "eqswitch-hook.dll.new",
                                        "eqswitch-di8.dll.old", "eqswitch-di8.dll.new",
                                        "update.zip" })
        {
            var path = Path.Combine(dir, pattern);
            if (!File.Exists(path)) continue;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.Delete(path);
                    FileLogger.Info($"Cleaned up update artifact: {pattern}");
                    break;
                }
                catch (Exception) when (attempt < 2)
                {
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"Failed to clean up {pattern}: {ex.Message}");
                }
            }
        }
    }
}
