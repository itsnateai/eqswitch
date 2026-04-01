using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace EQSwitch.Core;

/// <summary>
/// Manages per-process memory-mapped files shared with eqswitch-hook.dll.
/// Each injected eqgame.exe gets its own mapping named "EQSwitchHookCfg_{PID}",
/// allowing different position/size configs per process (required for multimonitor).
///
/// The hook DLL uses these fields:
///   enabled + position/size + stripThickFrame  — slim titlebar enforcement
///   blockMinimize                              — prevent EQ self-minimize on focus loss
///   windowTitle                                — override EQ's SetWindowTextA calls
/// </summary>
public class HookConfigWriter : IDisposable
{
    private const string SharedMemoryPrefix = "EQSwitchHookCfg_";

    // Must match the C++ HookConfig struct exactly (packed, sequential)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HookConfig
    {
        public int Enabled;         // 1 = enforce positions, 0 = passthrough
        public int TargetX;
        public int TargetY;
        public int TargetW;         // 0 = don't override
        public int TargetH;         // 0 = don't override
        public int StripThickFrame; // 1 = remove WS_THICKFRAME
        public int BlockMinimize;   // 1 = prevent EQ from minimizing itself

        // Fixed-size 256-byte title buffer — null-terminated ASCII.
        // Using a fixed array in a struct requires unsafe, so we use
        // manual marshal instead (see WriteConfig).
    }

    // Total shared memory size: HookConfig fields + 256 bytes for title
    private static readonly int StructSize = Marshal.SizeOf<HookConfig>() + 256;

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
                StructSize,
                MemoryMappedFileAccess.ReadWrite);

            accessor = mmf.CreateViewAccessor(0, StructSize, MemoryMappedFileAccess.ReadWrite);

            _mappings[pid] = new MappingEntry(mmf, accessor);

            // Write disabled config initially
            WriteConfig(pid, 0, 0, 0, 0, enabled: false, stripThickFrame: false,
                blockMinimize: false, windowTitle: "");
            FileLogger.Info($"HookConfigWriter: shared memory opened for PID {pid} ({name}, {StructSize} bytes)");
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
    /// Write full config for a specific process.
    /// </summary>
    public void WriteConfig(int pid, int x, int y, int w, int h,
        bool enabled, bool stripThickFrame = true,
        bool blockMinimize = false, string windowTitle = "")
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;

        var config = new HookConfig
        {
            Enabled = enabled ? 1 : 0,
            TargetX = x,
            TargetY = y,
            TargetW = w,
            TargetH = h,
            StripThickFrame = stripThickFrame ? 1 : 0,
            BlockMinimize = blockMinimize ? 1 : 0
        };

        try
        {
            // Write the struct fields
            entry.Accessor.Write(0, ref config);

            // Write the title as a fixed 256-byte null-terminated ASCII buffer
            // at the offset right after the struct fields
            int titleOffset = Marshal.SizeOf<HookConfig>();
            byte[] titleBytes = new byte[256];
            if (!string.IsNullOrEmpty(windowTitle))
            {
                var encoded = Encoding.ASCII.GetBytes(windowTitle);
                int copyLen = Math.Min(encoded.Length, 255); // leave room for null
                Array.Copy(encoded, titleBytes, copyLen);
                // titleBytes[copyLen] is already 0 (null terminator)
            }
            entry.Accessor.WriteArray(titleOffset, titleBytes, 0, 256);
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
