using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace EQSwitch.Core;

/// <summary>
/// Manages per-process shared memory for key state injection.
/// Creates "Local\EQSwitchDI8_{PID}" read by the dinput8.dll proxy.
///
/// The proxy DLL uses these fields:
///   active    — 1 = inject synthetic keys, 0 = passthrough
///   suppress  — 1 = zero physical keyboard state before injecting
///   keys[]    — scan code to press state (0x00=up, 0x80=down)
/// </summary>
public class KeyInputWriter : IDisposable
{
    private const string SharedMemoryPrefix = "Local\\EQSwitchDI8_";
    private const uint Magic = 0x45534B53; // "ESKS"
    private const uint Version = 1;
    private const int ShmSize = 276; // 5 x uint32 + 256 bytes

    // Must match the C++ SharedKeyState struct exactly (packed, sequential)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SharedKeyState
    {
        public uint Magic;
        public uint Version;
        public uint Active;
        public uint Suppress;
        public uint Seq;
        // keys[256] written separately via WriteArray
    }

    private static readonly int HeaderSize = Marshal.SizeOf<SharedKeyState>();

    private sealed class MappingEntry : IDisposable
    {
        public MemoryMappedFile Mmf;
        public MemoryMappedViewAccessor Accessor;
        public uint Seq;

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

    /// <summary>True if at least one per-process key state mapping is open.</summary>
    public bool HasMappings => _mappings.Count > 0;

    /// <summary>True if a mapping exists for the given PID.</summary>
    public bool IsOpen(int pid) => _mappings.ContainsKey(pid);

    /// <summary>
    /// Create shared memory for a process. Call before or after the dinput8.dll proxy loads —
    /// the proxy lazy-opens with retry on each GetDeviceState/GetDeviceData call.
    /// </summary>
    public bool Open(int pid)
    {
        if (_mappings.ContainsKey(pid)) return true;

        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? accessor = null;
        try
        {
            var name = $"{SharedMemoryPrefix}{(uint)pid}";
            mmf = MemoryMappedFile.CreateOrOpen(name, ShmSize, MemoryMappedFileAccess.ReadWrite);
            accessor = mmf.CreateViewAccessor(0, ShmSize, MemoryMappedFileAccess.ReadWrite);

            _mappings[pid] = new MappingEntry(mmf, accessor);

            // Write header with active=0 (dormant until broadcasting starts)
            var header = new SharedKeyState
            {
                Magic = Magic,
                Version = Version,
                Active = 0,
                Suppress = 0,
                Seq = 0
            };
            accessor.Write(0, ref header);
            // Zero out keys
            accessor.WriteArray(HeaderSize, new byte[256], 0, 256);

            FileLogger.Info($"KeyInputWriter: opened {name} ({ShmSize} bytes)");
            return true;
        }
        catch (Exception ex)
        {
            accessor?.Dispose();
            mmf?.Dispose();
            FileLogger.Error($"KeyInputWriter: failed to open for PID {pid}", ex);
            return false;
        }
    }

    /// <summary>
    /// Activate broadcasting for a process. Sets active=1 so the proxy starts injecting.
    /// </summary>
    public void Activate(int pid, bool suppress = false)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;
        try
        {
            entry.Accessor.Write(8, (uint)1);   // active = 1
            entry.Accessor.Write(12, suppress ? (uint)1 : (uint)0); // suppress
        }
        catch (Exception ex) { FileLogger.Warn($"KeyInputWriter.Activate failed: {ex.Message}"); }
    }

    /// <summary>
    /// Deactivate broadcasting for a process. Sets active=0 and clears all keys.
    /// </summary>
    public void Deactivate(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;
        try
        {
            entry.Accessor.Write(8, (uint)0); // active = 0
            entry.Accessor.WriteArray(HeaderSize, new byte[256], 0, 256);
        }
        catch (Exception ex) { FileLogger.Warn($"KeyInputWriter.Deactivate failed: {ex.Message}"); }
    }

    /// <summary>
    /// Write a full key state snapshot. Called on each key event from KeyBroadcastManager.
    /// </summary>
    public void WriteKeys(int pid, byte[] keys)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;
        if (keys.Length < 256) return;

        try
        {
            entry.Seq++;
            entry.Accessor.Write(16, entry.Seq); // seq
            entry.Accessor.WriteArray(HeaderSize, keys, 0, 256);
        }
        catch (Exception ex) { FileLogger.Warn($"KeyInputWriter.WriteKeys failed: {ex.Message}"); }
    }

    /// <summary>
    /// Set a single key's state. More efficient than WriteKeys for individual key events.
    /// </summary>
    public void SetKey(int pid, byte scanCode, bool pressed)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;
        try
        {
            entry.Seq++;
            entry.Accessor.Write(16, entry.Seq); // bump seq
            entry.Accessor.Write(HeaderSize + scanCode, pressed ? (byte)0x80 : (byte)0x00);
        }
        catch (Exception ex) { FileLogger.Warn($"KeyInputWriter.SetKey failed: {ex.Message}"); }
    }

    /// <summary>Close and remove shared memory for a specific process.</summary>
    public void Close(int pid)
    {
        if (_mappings.Remove(pid, out var entry))
        {
            try
            {
                entry.Accessor.Write(8, (uint)0); // active = 0
                entry.Accessor.WriteArray(HeaderSize, new byte[256], 0, 256);
            }
            catch { }
            entry.Dispose();
            FileLogger.Info($"KeyInputWriter: closed mapping for PID {pid}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (_, entry) in _mappings)
        {
            try
            {
                entry.Accessor.Write(8, (uint)0); // active = 0
            }
            catch { }
            entry.Dispose();
        }
        _mappings.Clear();
        FileLogger.Info("KeyInputWriter: all mappings disposed");
    }
}
