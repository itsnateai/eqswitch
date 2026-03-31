using System.Diagnostics;
using System.Text;
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
            ScheduleRestore(pid);
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
            ScheduleRestore(pid);
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

    /// <summary>
    /// After launching, check multiple times over 15 seconds to restore if EQ minimized itself.
    /// EQ can minimize when: (1) it starts up, (2) another client steals focus,
    /// (3) it finishes DirectX init. Multiple checks catch all these cases.
    /// </summary>
    private void ScheduleRestore(int pid)
    {
        int attempt = 0;
        const int maxAttempts = 5;
        const int intervalMs = 3000; // check every 3s for 15s total

        var timer = new System.Windows.Forms.Timer { Interval = intervalMs };
        timer.Tick += (_, _) =>
        {
            attempt++;
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                var hwnd = proc.MainWindowHandle;
                if (hwnd != IntPtr.Zero && NativeMethods.IsIconic(hwnd))
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    FileLogger.Info($"LaunchManager: restored minimized window for PID {pid} (attempt {attempt})");
                }
                else if (hwnd == IntPtr.Zero)
                {
                    FileLogger.Info($"LaunchManager: PID {pid} has no window yet (attempt {attempt})");
                }
            }
            catch { /* process may have exited */ }

            if (attempt >= maxAttempts)
            {
                timer.Stop();
                _activeTimers.Remove(timer);
                timer.Dispose();
                FileLogger.Info($"LaunchManager: restore checks done for PID {pid} after {attempt} attempts");
            }
        };
        _activeTimers.Add(timer);
        timer.Start();
    }

    private int StartEQProcess()
    {
        try
        {
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
