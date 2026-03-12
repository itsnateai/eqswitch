using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Manages CPU affinity for EQ clients.
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

    public AffinityManager(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Apply affinity rules: active client → P-cores, background clients → E-cores.
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

            // Check for per-character override
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
        }
    }

    /// <summary>
    /// Set the CPU affinity mask for a process by PID.
    /// </summary>
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
                System.Diagnostics.Debug.WriteLine($"Failed to open process {processId} for affinity change");
                return false;
            }

            bool result = NativeMethods.SetProcessAffinityMask(hProcess, (IntPtr)affinityMask);

            if (!result)
            {
                System.Diagnostics.Debug.WriteLine($"SetProcessAffinityMask failed for PID {processId}");
            }

            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Affinity error for PID {processId}: {ex.Message}");
            return false;
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Get the current affinity mask for a process. Useful for diagnostics.
    /// </summary>
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

    /// <summary>
    /// Reset all clients to use all available cores.
    /// Call this on shutdown to clean up.
    /// </summary>
    public void ResetAllAffinities(IReadOnlyList<EQClient> clients)
    {
        // Get system affinity mask (all available cores)
        if (clients.Count == 0) return;

        var (_, systemMask) = GetProcessAffinity(clients[0].ProcessId);
        if (systemMask == 0) systemMask = -1; // Fallback: all cores

        foreach (var client in clients)
        {
            SetProcessAffinity(client.ProcessId, systemMask);
        }
    }
}
