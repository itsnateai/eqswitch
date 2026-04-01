using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace EQSwitch.Core;

/// <summary>
/// Manages a memory-mapped file shared with eqswitch-hook.dll inside eqgame.exe.
/// The DLL reads this config on every hooked SetWindowPos/MoveWindow call to
/// determine target window position and style.
/// </summary>
public class HookConfigWriter : IDisposable
{
    private const string SharedMemoryName = "EQSwitchHookCfg";

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

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private bool _disposed;

    /// <summary>True if the shared memory is open and writable.</summary>
    public bool IsOpen => _mmf != null && _accessor != null;

    /// <summary>
    /// Create or open the shared memory region. Call this before injecting the DLL —
    /// the DLL opens the same named mapping on DLL_PROCESS_ATTACH.
    /// </summary>
    public bool Open()
    {
        if (IsOpen) return true;

        try
        {
            _mmf = MemoryMappedFile.CreateOrOpen(
                SharedMemoryName,
                Marshal.SizeOf<HookConfig>(),
                MemoryMappedFileAccess.ReadWrite);

            _accessor = _mmf.CreateViewAccessor(0, Marshal.SizeOf<HookConfig>(), MemoryMappedFileAccess.ReadWrite);

            // Write disabled config initially
            WriteConfig(0, 0, 0, 0, false, false);
            FileLogger.Info("HookConfigWriter: shared memory opened");
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error("HookConfigWriter: failed to open shared memory", ex);
            _accessor?.Dispose();
            _mmf?.Dispose();
            _accessor = null;
            _mmf = null;
            return false;
        }
    }

    /// <summary>
    /// Write window position/size config that the hook DLL reads on every call.
    /// </summary>
    public void WriteConfig(int x, int y, int w, int h, bool enabled, bool stripThickFrame = true)
    {
        if (!IsOpen) return;

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
            _accessor!.Write(0, ref config);
        }
        catch (Exception ex)
        {
            FileLogger.Error("HookConfigWriter: write failed", ex);
        }
    }

    /// <summary>Disable the hook (passthrough mode) without closing shared memory.</summary>
    public void Disable()
    {
        if (!IsOpen) return;
        var config = new HookConfig { Enabled = 0 };
        try { _accessor!.Write(0, ref config); }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Disable(); // Ensure hooks pass through before cleanup
        _accessor?.Dispose();
        _mmf?.Dispose();
        _accessor = null;
        _mmf = null;

        FileLogger.Info("HookConfigWriter: disposed");
    }
}
