using System.Diagnostics;
using EQSwitch.Config;

namespace EQSwitch.Core;

/// <summary>
/// Handles launching EQ client processes with staggered delays,
/// automatic affinity/priority application, and window arrangement.
/// </summary>
public class LaunchManager : IDisposable
{
    private const int LaunchDebounceMs = 3000;
    private const int MinLaunchDelayMs = 500;

    private readonly AppConfig _config;
    private readonly AffinityManager _affinityManager;
    private readonly Action<AppConfig>? _enforceOverrides;

    private bool _launchActive;
    private long _lastLaunchTime;

    /// <summary>True while a LaunchAll sequence is in progress (used by TrayManager for incremental arrange).</summary>
    public bool IsLaunching => _launchActive;
    private readonly List<int> _launchedPids = new();
    private readonly List<System.Windows.Forms.Timer> _activeTimers = new();

    /// <summary>Fires after each individual client is launched (with PID).</summary>
    public event EventHandler<int>? ClientLaunched;

    /// <summary>Fires when the entire launch sequence completes.</summary>
    public event EventHandler? LaunchSequenceComplete;

    /// <summary>Fires with progress messages during launch.</summary>
    public event EventHandler<string>? ProgressUpdate;

    public LaunchManager(AppConfig config, AffinityManager affinityManager, Action<AppConfig>? enforceOverrides = null)
    {
        _config = config;
        _affinityManager = affinityManager;
        _enforceOverrides = enforceOverrides;
    }

    /// <summary>
    /// Launch a single EQ client. 3-second debounce to prevent double-clicks.
    /// </summary>
    public void LaunchOne()
    {
        long now = Environment.TickCount64;
        if (now - _lastLaunchTime < LaunchDebounceMs)
        {
            FileLogger.Info("LaunchOne: debounced (too soon)");
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
            FileLogger.Info("LaunchAll: already in progress");
            return;
        }

        // Share debounce with LaunchOne — prevents LaunchOne then immediate LaunchAll
        long now = Environment.TickCount64;
        if (now - _lastLaunchTime < LaunchDebounceMs)
        {
            FileLogger.Info("LaunchAll: debounced (too soon after last launch)");
            return;
        }
        _lastLaunchTime = now;

        int count = Math.Clamp(_config.Launch.NumClients, 1, 8);
        _launchActive = true;
        _launchedPids.Clear();

        FileLogger.Info($"LaunchAll: starting {count} client(s)");
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
        FileLogger.Info($"LaunchAll: client {launched}/{total} (PID {pid})");

        if (launched < total)
        {
            var delay = Math.Max(_config.Launch.LaunchDelayMs, MinLaunchDelayMs);
            var timer = new System.Windows.Forms.Timer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _activeTimers.Remove(timer);
                timer.Dispose();
                if (_launchActive) LaunchNext(launched, total);
            };
            _activeTimers.Add(timer);
            timer.Start();
        }
        else
        {
            _launchActive = false;
            _launchedPids.Clear();
            ProgressUpdate?.Invoke(this, "Ready to play!");
            LaunchSequenceComplete?.Invoke(this, EventArgs.Empty);
            FileLogger.Info("LaunchAll: sequence complete");
        }
    }

    /// <summary>
    /// Cancel any in-flight launch sequence and dispose all pending timers.
    /// Call this on config reload or shutdown.
    /// </summary>
    public void CancelLaunch()
    {
        _launchActive = false;
        foreach (var timer in _activeTimers)
        {
            timer.Stop();
            timer.Dispose();
        }
        _activeTimers.Clear();
        _launchedPids.Clear();
    }

    public void Dispose()
    {
        CancelLaunch();
    }

    private int StartEQProcess()
    {
        try
        {
            // Write eqclient.ini overrides BEFORE launching (slim titlebar resolution, etc.)
            if (_enforceOverrides != null)
                _enforceOverrides(_config);
            else
                FileLogger.Warn("LaunchManager: no enforceOverrides callback registered, skipping INI overrides");

            // Validate exe name doesn't contain path traversal sequences
            var exeName = _config.Launch.ExeName;
            if (exeName.Contains("..") || exeName.Contains('/') || exeName.Contains('\\'))
            {
                FileLogger.Error($"LaunchManager: exe name contains invalid path characters: {exeName}");
                ProgressUpdate?.Invoke(this, "Error: invalid exe name");
                return -1;
            }

            var exePath = Path.Combine(_config.EQPath, exeName);
            if (!File.Exists(exePath))
            {
                FileLogger.Error($"LaunchManager: exe not found at {exePath}");
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

            using var proc = Process.Start(startInfo);
            if (proc == null)
            {
                FileLogger.Warn("LaunchManager: Process.Start returned null");
                return -1;
            }

            FileLogger.Info($"LaunchManager: started PID {proc.Id}");
            return proc.Id;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"LaunchManager: launch failed", ex);
            ProgressUpdate?.Invoke(this, $"Launch error: {ex.Message}");
            return -1;
        }
    }
}
