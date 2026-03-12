using System.Diagnostics;
using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// Handles launching EQ client processes with staggered delays,
/// automatic affinity/priority application, and window arrangement.
/// </summary>
public class LaunchManager
{
    private readonly AppConfig _config;
    private readonly AffinityManager _affinityManager;

    private bool _launchActive;
    private long _lastLaunchTime;
    private readonly List<int> _launchedPids = new();

    /// <summary>Fires after each individual client is launched (with PID).</summary>
    public event EventHandler<int>? ClientLaunched;

    /// <summary>Fires when the entire launch sequence completes.</summary>
    public event EventHandler? LaunchSequenceComplete;

    /// <summary>Fires with progress messages during launch.</summary>
    public event EventHandler<string>? ProgressUpdate;

    public LaunchManager(AppConfig config, AffinityManager affinityManager)
    {
        _config = config;
        _affinityManager = affinityManager;
    }

    /// <summary>
    /// Launch a single EQ client. 3-second debounce to prevent double-clicks.
    /// </summary>
    public void LaunchOne()
    {
        // 3-second debounce
        long now = Environment.TickCount64;
        if (now - _lastLaunchTime < 3000)
        {
            Debug.WriteLine("LaunchOne: debounced (too soon)");
            return;
        }
        _lastLaunchTime = now;

        int pid = StartEQProcess();
        if (pid > 0)
        {
            ProgressUpdate?.Invoke(this, "Launching EQ client...");
            ClientLaunched?.Invoke(this, pid);
        }
    }

    /// <summary>
    /// Launch multiple EQ clients with staggered delays.
    /// Uses async timer callbacks to avoid blocking the UI thread.
    /// </summary>
    public void LaunchAll()
    {
        if (_launchActive)
        {
            Debug.WriteLine("LaunchAll: already in progress");
            return;
        }

        int count = Math.Clamp(_config.Launch.NumClients, 1, 8);
        _launchActive = true;
        _launchedPids.Clear();

        Debug.WriteLine($"LaunchAll: starting {count} client(s)");
        ProgressUpdate?.Invoke(this, $"Launching {count} client(s)...");

        // Launch first client immediately
        int launched = 0;
        LaunchNext(launched, count);
    }

    private void LaunchNext(int launched, int total)
    {
        int pid = StartEQProcess();
        if (pid > 0)
        {
            _launchedPids.Add(pid);
            ClientLaunched?.Invoke(this, pid);
        }

        launched++;
        ProgressUpdate?.Invoke(this, $"Launched client {launched} of {total}");
        Debug.WriteLine($"LaunchAll: client {launched}/{total} (PID {pid})");

        if (launched < total)
        {
            // Schedule next launch after delay
            var timer = new System.Windows.Forms.Timer { Interval = _config.Launch.LaunchDelayMs };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                LaunchNext(launched, total);
            };
            timer.Start();
        }
        else
        {
            // All launched — schedule window fix after FixDelay
            SchedulePostLaunchFix();
        }
    }

    private void SchedulePostLaunchFix()
    {
        Debug.WriteLine($"LaunchAll: all clients launched, waiting {_config.Launch.FixDelayMs}ms before arranging");
        ProgressUpdate?.Invoke(this, "Waiting for clients to initialize...");

        var fixTimer = new System.Windows.Forms.Timer { Interval = _config.Launch.FixDelayMs };
        fixTimer.Tick += (_, _) =>
        {
            fixTimer.Stop();
            fixTimer.Dispose();

            _launchActive = false;
            _launchedPids.Clear();

            ProgressUpdate?.Invoke(this, "Ready to play!");
            LaunchSequenceComplete?.Invoke(this, EventArgs.Empty);
            Debug.WriteLine("LaunchAll: sequence complete");
        };
        fixTimer.Start();
    }

    private int StartEQProcess()
    {
        try
        {
            var exePath = Path.Combine(_config.EQPath, _config.Launch.ExeName);
            if (!File.Exists(exePath))
            {
                Debug.WriteLine($"LaunchManager: exe not found at {exePath}");
                ProgressUpdate?.Invoke(this, $"Error: {_config.Launch.ExeName} not found");
                return -1;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = _config.Launch.Arguments,
                WorkingDirectory = _config.EQPath,
                UseShellExecute = true
            };

            var proc = Process.Start(startInfo);
            if (proc == null)
            {
                Debug.WriteLine("LaunchManager: Process.Start returned null");
                return -1;
            }

            Debug.WriteLine($"LaunchManager: started PID {proc.Id}");
            return proc.Id;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LaunchManager: launch failed — {ex.Message}");
            ProgressUpdate?.Invoke(this, $"Launch error: {ex.Message}");
            return -1;
        }
    }
}
