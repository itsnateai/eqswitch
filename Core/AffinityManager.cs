using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Manages process priority for EQ clients.
/// CPU core assignment is handled by eqclient.ini's CPUAffinity0-5 fields,
/// written by the Process Manager UI. This class only handles Windows priority
/// (High/AboveNormal/Normal) which has no ini equivalent.
/// </summary>
public class AffinityManager
{
    private readonly AppConfig _config;
    private EQClient? _lastActiveClient;

    // Track clients that need priority retries (EQ may reset priority on startup)
    private readonly Dictionary<int, int> _retryCounters = new(); // PID -> retries remaining

    public AffinityManager(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Force re-apply priority rules to all clients.
    /// </summary>
    public void ForceApplyAffinityRules(IReadOnlyList<EQClient> clients, EQClient? activeClient)
    {
        _lastActiveClient = null;
        ApplyAffinityRules(clients, activeClient);
    }

    /// <summary>
    /// Apply priority rules to all EQ clients.
    /// Skips work when active/background priorities are the same (common case)
    /// since priority was already set on launch via ScheduleRetry.
    /// </summary>
    public void ApplyAffinityRules(IReadOnlyList<EQClient> clients, EQClient? activeClient)
    {
        if (!_config.Affinity.Enabled) return;
        if (clients.Count == 0) return;

        // If active and background priorities are identical, no need to re-apply
        // on every foreground change — priority was set once on launch.
        bool samePriority = string.Equals(
            _config.Affinity.ActivePriority,
            _config.Affinity.BackgroundPriority,
            StringComparison.OrdinalIgnoreCase);
        if (samePriority) return;

        // Skip if active client hasn't changed
        if (activeClient != null && activeClient == _lastActiveClient) return;
        _lastActiveClient = activeClient;

        foreach (var client in clients)
        {
            bool isActive = client == activeClient;
            var priority = isActive ? _config.Affinity.ActivePriority : _config.Affinity.BackgroundPriority;
            SetProcessPriority(client.ProcessId, priority);
        }
    }

    /// <summary>
    /// Schedule priority retries for a newly discovered client.
    /// </summary>
    public void ScheduleRetry(EQClient client)
    {
        if (!_config.Affinity.Enabled) return;
        _retryCounters[client.ProcessId] = _config.Affinity.LaunchRetryCount;
        FileLogger.Info($"Priority retry scheduled for {client} ({_config.Affinity.LaunchRetryCount} attempts)");
    }

    /// <summary>
    /// Process pending retry attempts. Call this from a timer tick.
    /// </summary>
    public bool ProcessRetries(IReadOnlyList<EQClient> clients)
    {
        if (_retryCounters.Count == 0) return false;

        bool applied = false;
        List<int>? completed = null;
        List<KeyValuePair<int, int>>? updates = null;

        foreach (var kvp in _retryCounters)
        {
            int pid = kvp.Key;
            int remaining = kvp.Value;

            EQClient? client = null;
            for (int i = 0; i < clients.Count; i++)
            {
                if (clients[i].ProcessId == pid) { client = clients[i]; break; }
            }
            if (client == null)
            {
                (completed ??= new List<int>()).Add(pid);
                continue;
            }

            var priorityOverride = FindSlotPriorityOverride(client.SlotIndex)
                                ?? FindCharacterPriorityOverride(client.CharacterName);
            var retryPriority = priorityOverride ?? _config.Affinity.BackgroundPriority;
            bool success = SetProcessPriority(pid, retryPriority);

            if (success)
            {
                FileLogger.Info($"Priority retry applied for {client} (attempt {_config.Affinity.LaunchRetryCount - remaining + 1})");
                applied = true;
                (completed ??= new List<int>()).Add(pid);
            }
            else if (remaining <= 1)
            {
                (completed ??= new List<int>()).Add(pid);
            }
            else
            {
                (updates ??= new List<KeyValuePair<int, int>>()).Add(new(pid, remaining - 1));
            }
        }

        if (updates != null)
            for (int i = 0; i < updates.Count; i++)
                _retryCounters[updates[i].Key] = updates[i].Value;
        if (completed != null)
            for (int i = 0; i < completed.Count; i++)
                _retryCounters.Remove(completed[i]);

        return applied;
    }

    private string? FindSlotPriorityOverride(int slotIndex)
    {
        var characters = _config.Characters;
        for (int i = 0; i < characters.Count; i++)
        {
            if (characters[i].SlotIndex == slotIndex && characters[i].PriorityOverride != null)
                return characters[i].PriorityOverride;
        }
        return null;
    }

    private string? FindCharacterPriorityOverride(string? characterName)
    {
        if (string.IsNullOrEmpty(characterName)) return null;
        var characters = _config.Characters;
        for (int i = 0; i < characters.Count; i++)
        {
            if (characters[i].Name.Equals(characterName, StringComparison.OrdinalIgnoreCase))
                return characters[i].PriorityOverride;
        }
        return null;
    }

    public void CancelRetry(int processId) => _retryCounters.Remove(processId);

    // ─── Static helpers (read-only display + priority setting) ────────

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

        return (coreCount, systemMask);
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
