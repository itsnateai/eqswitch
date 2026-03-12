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
            MessageBox.Show(
                "EQSwitch is already running.\nCheck your system tray.",
                "EQSwitch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            // Load or create config
            var config = ConfigManager.Load();

            // First-run experience
            if (config.IsFirstRun)
            {
                var firstRun = new FirstRunDialog();
                if (firstRun.ShowDialog() != DialogResult.OK)
                    return;

                config.IsFirstRun = false;
                config.EQPath = firstRun.SelectedEQPath;
                ConfigManager.Save(config);
            }

            // Initialize core managers
            var processManager = new ProcessManager(config);
            var windowManager = new WindowManager(config);
            var affinityManager = new AffinityManager(config);
            var hotkeyManager = new HotkeyManager();

            // Start the tray application
            var trayApp = new TrayManager(config, processManager, windowManager, affinityManager, hotkeyManager);
            trayApp.Initialize();

            Application.Run();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"EQSwitch encountered a fatal error:\n\n{ex.Message}",
                "EQSwitch Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
    }
}
