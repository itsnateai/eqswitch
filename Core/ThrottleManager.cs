using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Manages background FPS throttling for EQ clients via process suspension.
///
/// Uses NtSuspendProcess/NtResumeProcess to duty-cycle background processes.
/// The active (foreground) client is never throttled. Background clients are
/// suspended for a percentage of each cycle, effectively reducing their FPS
/// and GPU/CPU usage.
///
/// Example: ThrottlePercent=50, CycleIntervalMs=100
///   → Background processes suspended for 50ms, resumed for 50ms
///   → Effective ~50% FPS reduction
/// </summary>
public class ThrottleManager : IDisposable
{
    private readonly AppConfig _config;
    private System.Windows.Forms.Timer? _suspendTimer;
    private System.Windows.Forms.Timer? _resumeTimer;

    // Track which PIDs are currently suspended so we can resume them on shutdown
    private readonly HashSet<int> _suspendedPids = new();

    // Current state: true = background processes are suspended
    private bool _inSuspendPhase;

    // Cached references updated each cycle
    private IReadOnlyList<EQClient> _clients = Array.Empty<EQClient>();
    private EQClient? _activeClient;

    public ThrottleManager(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Start the throttle duty cycle timers.
    /// </summary>
    public void Start()
    {
        if (!_config.Throttle.Enabled || _config.Throttle.ThrottlePercent <= 0)
            return;

        int cycleMs = _config.Throttle.CycleIntervalMs;
        int suspendMs = cycleMs * _config.Throttle.ThrottlePercent / 100;
        int resumeMs = cycleMs - suspendMs;

        // Minimum 10ms for either phase to avoid timer starvation
        suspendMs = Math.Max(suspendMs, 10);
        resumeMs = Math.Max(resumeMs, 10);

        _suspendTimer = new System.Windows.Forms.Timer { Interval = resumeMs };
        _suspendTimer.Tick += (_, _) => SuspendPhase();

        _resumeTimer = new System.Windows.Forms.Timer { Interval = suspendMs };
        _resumeTimer.Tick += (_, _) => ResumePhase();

        // Start with the resume phase (processes are running)
        _inSuspendPhase = false;
        _suspendTimer.Start();

        FileLogger.Info($"ThrottleManager started: {_config.Throttle.ThrottlePercent}% throttle, " +
            $"{cycleMs}ms cycle (suspend {suspendMs}ms, resume {resumeMs}ms)");
    }

    /// <summary>
    /// Update the client list and active client. Called from the affinity timer tick.
    /// </summary>
    public void UpdateClients(IReadOnlyList<EQClient> clients, EQClient? activeClient)
    {
        var previousActive = _activeClient;
        _clients = clients;
        _activeClient = activeClient;

        // If the active client changed and the new active was suspended, resume it immediately
        if (activeClient != null && activeClient != previousActive && _suspendedPids.Contains(activeClient.ProcessId))
        {
            ResumeProcess(activeClient.ProcessId);
            _suspendedPids.Remove(activeClient.ProcessId);
        }
    }

    /// <summary>
    /// Suspend all background EQ processes.
    /// </summary>
    private void SuspendPhase()
    {
        _suspendTimer?.Stop();
        _inSuspendPhase = true;

        foreach (var client in _clients)
        {
            // Never suspend the active client
            if (client == _activeClient) continue;
            if (_suspendedPids.Contains(client.ProcessId)) continue;

            if (SuspendProcess(client.ProcessId))
                _suspendedPids.Add(client.ProcessId);
        }

        _resumeTimer?.Start();
    }

    /// <summary>
    /// Resume all suspended EQ processes.
    /// </summary>
    private void ResumePhase()
    {
        _resumeTimer?.Stop();
        _inSuspendPhase = false;

        ResumeAllSuspended();

        _suspendTimer?.Start();
    }

    /// <summary>
    /// Resume all currently suspended processes.
    /// </summary>
    private void ResumeAllSuspended()
    {
        foreach (var pid in _suspendedPids)
        {
            ResumeProcess(pid);
        }
        _suspendedPids.Clear();
    }

    /// <summary>
    /// Stop throttling and resume all suspended processes.
    /// </summary>
    public void Stop()
    {
        _suspendTimer?.Stop();
        _suspendTimer?.Dispose();
        _suspendTimer = null;

        _resumeTimer?.Stop();
        _resumeTimer?.Dispose();
        _resumeTimer = null;

        // Always resume everything on stop
        ResumeAllSuspended();
        _inSuspendPhase = false;

        FileLogger.Info("ThrottleManager stopped");
    }

    private static bool SuspendProcess(int processId)
    {
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_SUSPEND_RESUME, false, processId);

            if (hProcess == IntPtr.Zero)
                return false;

            int status = NativeMethods.NtSuspendProcess(hProcess);
            return status == 0; // NTSTATUS SUCCESS
        }
        catch (Exception ex)
        {
            FileLogger.Error($"SuspendProcess failed for PID {processId}", ex);
            return false;
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }

    private static bool ResumeProcess(int processId)
    {
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_SUSPEND_RESUME, false, processId);

            if (hProcess == IntPtr.Zero)
                return false;

            int status = NativeMethods.NtResumeProcess(hProcess);
            return status == 0;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"ResumeProcess failed for PID {processId}", ex);
            return false;
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
