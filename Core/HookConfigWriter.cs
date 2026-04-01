using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace EQSwitch.Core;

/// <summary>
/// Manages per-process memory-mapped files shared with eqswitch-hook.dll.
/// Each injected eqgame.exe gets its own mapping named "EQSwitchHookCfg_{PID}",
/// allowing different position/size configs per process (required for multimonitor).
/// </summary>
public class HookConfigWriter : IDisposable
{
    private const string SharedMemoryPrefix = "EQSwitchHookCfg_";

    // Must match the C++ HookConfig struct exactly (packed, sequential ints)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HookConfig
    {
        public int Enabled;         // 1 = enforce, 0 = passthrough
        public int TargetX;
        public int TargetY;
        public int TargetW;         // 0 = don't override
        public int TargetH;         // 0 = don't override
        public int StripThickFrame; // 1 = remove WS_THICKFRAME
    }

    private sealed class MappingEntry : IDisposable
    {
        public MemoryMappedFile Mmf;
        public MemoryMappedViewAccessor Accessor;

        public MappingEntry(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor)
        {
            Mmf = mmf;
            Accessor = accessor;
        }

        public void Dispose()
        {
            Accessor.Dispose();
            Mmf.Dispose();
        }
    }

    private readonly Dictionary<int, MappingEntry> _mappings = new();
    private bool _disposed;

    /// <summary>True if at least one per-process shared memory mapping is open.</summary>
    public bool HasMappings => _mappings.Count > 0;

    /// <summary>True if a mapping exists for the given PID.</summary>
    public bool IsOpen(int pid) => _mappings.ContainsKey(pid);

    /// <summary>
    /// Create or open a per-process shared memory region. Call before injecting the DLL —
    /// the DLL opens "EQSwitchHookCfg_{its own PID}" on DLL_PROCESS_ATTACH.
    /// </summary>
    public bool Open(int pid)
    {
        if (_mappings.ContainsKey(pid)) return true;

        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? accessor = null;
        try
        {
            var name = $"{SharedMemoryPrefix}{(uint)pid}";
            mmf = MemoryMappedFile.CreateOrOpen(
                name,
                Marshal.SizeOf<HookConfig>(),
                MemoryMappedFileAccess.ReadWrite);

            accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf<HookConfig>(), MemoryMappedFileAccess.ReadWrite);

            _mappings[pid] = new MappingEntry(mmf, accessor);

            // Write disabled config initially
            WriteConfig(pid, 0, 0, 0, 0, false, false);
            FileLogger.Info($"HookConfigWriter: shared memory opened for PID {pid} ({name})");
            return true;
        }
        catch (Exception ex)
        {
            accessor?.Dispose();
            mmf?.Dispose();
            FileLogger.Error($"HookConfigWriter: failed to open shared memory for PID {pid}", ex);
            return false;
        }
    }

    /// <summary>
    /// Write window position/size config for a specific process.
    /// </summary>
    public void WriteConfig(int pid, int x, int y, int w, int h, bool enabled, bool stripThickFrame = true)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;

        var config = new HookConfig
        {
            Enabled = enabled ? 1 : 0,
            TargetX = x,
            TargetY = y,
            TargetW = w,
            TargetH = h,
            StripThickFrame = stripThickFrame ? 1 : 0
        };

        try
        {
            entry.Accessor.Write(0, ref config);
        }
        catch (Exception ex)
        {
            FileLogger.Error($"HookConfigWriter: write failed for PID {pid}", ex);
        }
    }

    /// <summary>Disable the hook for a specific process (passthrough mode).
    /// Only flips the Enabled flag — preserves geometry so re-enable doesn't need a full rewrite.</summary>
    public void Disable(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;
        try
        {
            entry.Accessor.Read(0, out HookConfig config);
            config.Enabled = 0;
            entry.Accessor.Write(0, ref config);
        }
        catch (Exception ex) { FileLogger.Warn($"HookConfigWriter.Disable failed for PID {pid}: {ex.Message}"); }
    }

    /// <summary>Disable the hook for all tracked processes.</summary>
    public void DisableAll()
    {
        foreach (var pid in _mappings.Keys)
            Disable(pid);
    }

    /// <summary>Close and remove shared memory for a specific process.</summary>
    public void Close(int pid)
    {
        if (_mappings.Remove(pid, out var entry))
        {
            var config = new HookConfig { Enabled = 0 };
            try { entry.Accessor.Write(0, ref config); } catch { }
            entry.Dispose();
            FileLogger.Info($"HookConfigWriter: closed mapping for PID {pid}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (pid, entry) in _mappings)
        {
            var config = new HookConfig { Enabled = 0 };
            try { entry.Accessor.Write(0, ref config); } catch { }
            entry.Dispose();
        }
        _mappings.Clear();

        FileLogger.Info("HookConfigWriter: all mappings disposed");
    }
}
