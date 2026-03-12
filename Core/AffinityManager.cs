using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Manages CPU affinity and process priority for EQ clients.
///
/// Key concept: Active (foreground) client gets P-cores for best framerate,
/// background clients get E-cores to save resources.
///
/// On Intel 12th+ gen hybrid architectures:
///   P-cores (performance) = lower-numbered cores (e.g. 0-7)
///   E-cores (efficiency)  = higher-numbered cores (e.g. 8-15)
///
/// Affinity masks are bitmasks: 0xFF = cores 0-7, 0xFF00 = cores 8-15
/// </summary>
public class AffinityManager
{
    private readonly AppConfig _config;
    private EQClient? _lastActiveClient;

    // Track clients that need affinity retries (EQ resets affinity on startup)
    private readonly Dictionary<int, int> _retryCounters = new(); // PID -> retries remaining

    public AffinityManager(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Force re-apply affinity rules to all clients, ignoring the "unchanged" optimization.
    /// </summary>
    public void ForceApplyAffinityRules(IReadOnlyList<EQClient> clients, EQClient? activeClient)
    {
        _lastActiveClient = null; // reset cache to force re-apply
        ApplyAffinityRules(clients, activeClient);
    }

    /// <summary>
    /// Apply affinity and priority rules: active client gets P-cores + higher priority,
    /// background clients get E-cores + normal priority.
    /// Call this whenever the foreground window changes.
    /// </summary>
    public void ApplyAffinityRules(IReadOnlyList<EQClient> clients, EQClient? activeClient)
    {
        if (!_config.Affinity.Enabled) return;
        if (clients.Count == 0) return;

        // Skip if active client hasn't changed
        if (activeClient != null && activeClient == _lastActiveClient) return;
        _lastActiveClient = activeClient;

        foreach (var client in clients)
        {
            bool isActive = client == activeClient;

            // Check for per-character affinity override
            var profile = _config.Characters.FirstOrDefault(
                c => c.Name.Equals(client.CharacterName ?? "", StringComparison.OrdinalIgnoreCase));

            long mask;
            if (profile?.AffinityOverride != null)
            {
                mask = profile.AffinityOverride.Value;
            }
            else
            {
                mask = isActive ? _config.Affinity.ActiveMask : _config.Affinity.BackgroundMask;
            }

            SetProcessAffinity(client.ProcessId, mask);

            // Set process priority
            var priority = isActive ? _config.Affinity.ActivePriority : _config.Affinity.BackgroundPriority;
            SetProcessPriority(client.ProcessId, priority);
        }
    }

    /// <summary>
    /// Schedule affinity retries for a newly discovered client.
    /// EQ resets its affinity shortly after startup, so we need to re-apply.
    /// </summary>
    public void ScheduleRetry(EQClient client)
    {
        if (!_config.Affinity.Enabled) return;
        _retryCounters[client.ProcessId] = _config.Affinity.LaunchRetryCount;
        FileLogger.Info($"Affinity retry scheduled for {client} ({_config.Affinity.LaunchRetryCount} attempts)");
    }

    /// <summary>
    /// Process pending retry attempts. Call this from a timer tick.
    /// Returns true if any retries were applied.
    /// </summary>
    public bool ProcessRetries(IReadOnlyList<EQClient> clients)
    {
        if (_retryCounters.Count == 0) return false;

        bool applied = false;
        var completed = new List<int>();

        // Snapshot keys to avoid modifying the dictionary during enumeration
        foreach (var (pid, remaining) in _retryCounters.ToList())
        {
            var client = clients.FirstOrDefault(c => c.ProcessId == pid);
            if (client == null)
            {
                completed.Add(pid);
                continue;
            }

            var profile = _config.Characters.FirstOrDefault(
                c => c.Name.Equals(client.CharacterName ?? "", StringComparison.OrdinalIgnoreCase));

            long mask = profile?.AffinityOverride ?? _config.Affinity.BackgroundMask;
            bool success = SetProcessAffinity(pid, mask);

            if (success)
            {
                FileLogger.Info($"Affinity retry applied for {client} (attempt {_config.Affinity.LaunchRetryCount - remaining + 1})");
                applied = true;
            }

            if (remaining <= 1)
                completed.Add(pid);
            else
                _retryCounters[pid] = remaining - 1;
        }

        foreach (var pid in completed)
            _retryCounters.Remove(pid);

        return applied;
    }

    /// <summary>
    /// Remove retry tracking for a lost client.
    /// </summary>
    public void CancelRetry(int processId)
    {
        _retryCounters.Remove(processId);
    }

    /// <summary>
    /// Build a diagnostic string showing affinity/priority for all clients.
    /// </summary>
    public static string GetDiagnosticInfo(IReadOnlyList<EQClient> clients)
    {
        if (clients.Count == 0) return "No EQ clients detected";

        var lines = new List<string>();
        var (coreCount, systemMask) = DetectCores();
        lines.Add($"System: {coreCount} cores (mask 0x{systemMask:X})");
        lines.Add("");

        foreach (var client in clients)
        {
            var (procMask, _) = GetProcessAffinity(client.ProcessId);
            var priority = GetProcessPriorityName(client.ProcessId);
            var name = client.CharacterName ?? $"Client {client.SlotIndex + 1}";
            lines.Add($"[{client.SlotIndex + 1}] {name} (PID {client.ProcessId})");
            lines.Add($"    Affinity: 0x{procMask:X}  Priority: {priority}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    // ─── Static helpers ──────────────────────────────────────────────

    public static bool SetProcessAffinity(int processId, long affinityMask)
    {
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_SET_INFORMATION | NativeMethods.PROCESS_QUERY_INFORMATION,
                false, processId);

            if (hProcess == IntPtr.Zero)
            {
                FileLogger.Warn($"Failed to open process {processId} for affinity change");
                return false;
            }

            bool result = NativeMethods.SetProcessAffinityMask(hProcess, (IntPtr)affinityMask);
            if (!result)
                FileLogger.Warn($"SetProcessAffinityMask failed for PID {processId}");
            return result;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Affinity error for PID {processId}", ex);
            return false;
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }

    public static bool SetProcessPriority(int processId, string priorityName)
    {
        uint priorityClass = ParsePriorityClass(priorityName);

        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_SET_INFORMATION, false, processId);

            if (hProcess == IntPtr.Zero)
            {
                FileLogger.Warn($"Failed to open process {processId} for priority change");
                return false;
            }

            bool result = NativeMethods.SetPriorityClass(hProcess, priorityClass);
            if (!result)
                FileLogger.Warn($"SetPriorityClass failed for PID {processId}");
            return result;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"Priority error for PID {processId}", ex);
            return false;
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }

    public static (long processMask, long systemMask) GetProcessAffinity(int processId)
    {
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION, false, processId);

            if (hProcess == IntPtr.Zero)
                return (0, 0);

            NativeMethods.GetProcessAffinityMask(hProcess, out IntPtr processAffinity, out IntPtr systemAffinity);
            return ((long)processAffinity, (long)systemAffinity);
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }

    public static string GetProcessPriorityName(int processId)
    {
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION, false, processId);

            if (hProcess == IntPtr.Zero) return "Unknown";

            uint pc = NativeMethods.GetPriorityClass(hProcess);
            return pc switch
            {
                NativeMethods.IDLE_PRIORITY_CLASS => "Idle",
                NativeMethods.BELOW_NORMAL_PRIORITY_CLASS => "BelowNormal",
                NativeMethods.NORMAL_PRIORITY_CLASS => "Normal",
                NativeMethods.ABOVE_NORMAL_PRIORITY_CLASS => "AboveNormal",
                NativeMethods.HIGH_PRIORITY_CLASS => "High",
                NativeMethods.REALTIME_PRIORITY_CLASS => "Realtime",
                _ => $"Unknown (0x{pc:X})"
            };
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }

    public static (int coreCount, long systemMask) DetectCores()
    {
        int coreCount = Environment.ProcessorCount;
        long systemMask = coreCount >= 64 ? -1L : (1L << coreCount) - 1;

        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION, false,
                Environment.ProcessId);

            if (hProcess != IntPtr.Zero)
            {
                NativeMethods.GetProcessAffinityMask(hProcess, out _, out IntPtr sysMask);
                if ((long)sysMask != 0)
                    systemMask = (long)sysMask;
            }
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }

        FileLogger.Info($"Core detection: {coreCount} cores, system mask 0x{systemMask:X}");
        return (coreCount, systemMask);
    }

    /// <summary>
    /// Reset all clients to use all available cores and normal priority.
    /// Call this on shutdown to clean up.
    /// </summary>
    public void ResetAllAffinities(IReadOnlyList<EQClient> clients)
    {
        if (clients.Count == 0) return;

        var (_, systemMask) = DetectCores();

        foreach (var client in clients)
        {
            SetProcessAffinity(client.ProcessId, systemMask);
            SetProcessPriority(client.ProcessId, "Normal");
        }
    }

    private static uint ParsePriorityClass(string name) => name.ToLowerInvariant() switch
    {
        "idle" => NativeMethods.IDLE_PRIORITY_CLASS,
        "belownormal" => NativeMethods.BELOW_NORMAL_PRIORITY_CLASS,
        "normal" => NativeMethods.NORMAL_PRIORITY_CLASS,
        "abovenormal" => NativeMethods.ABOVE_NORMAL_PRIORITY_CLASS,
        "high" => NativeMethods.HIGH_PRIORITY_CLASS,
        _ => NativeMethods.NORMAL_PRIORITY_CLASS
    };
}
