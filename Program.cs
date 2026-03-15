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

        if (!createdNew)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FloatingTooltip.Show("EQSwitch is already running. Check your system tray.", 4000);
            // Brief delay so tooltip is visible before process exits
            Thread.Sleep(4200);
            return;
        }

        FileLogger.Initialize();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var config = ConfigManager.Load();

            // First-run: try migrating from AHK config, then show EQ path picker
            if (config.IsFirstRun)
            {
                var migrated = ConfigMigration.TryImportFromAhk();
                if (migrated != null)
                {
                    config = migrated;
                    ConfigManager.Save(config);
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
                    ConfigManager.Save(config);
                }
            }

            bool isNewUser = config.IsFirstRun == false && config.PollingIntervalMs == 500
                && string.IsNullOrEmpty(config.GinaPath) && config.Characters.Count == 0;

            var processManager = new ProcessManager(config);
            var trayApp = new TrayManager(config, processManager);

            // Ensure cleanup on any exit path (not just tray menu Exit)
            Application.ApplicationExit += (_, _) => trayApp.Dispose();

            trayApp.Initialize();

            // Auto-open Settings on first run so new users can configure
            if (isNewUser)
                trayApp.OpenSettingsAfterDelay();

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
            FileLogger.Shutdown();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
    }
}
