using System.Diagnostics;

namespace EQSwitch.Models;

/// <summary>
/// Represents a running EQ client instance.
/// Tracks the window handle, process, and associated character info.
/// </summary>
public class EQClient
{
    public IntPtr WindowHandle { get; set; }
    public int ProcessId { get; set; }
    public string WindowTitle { get; set; } = "";
    public int SlotIndex { get; set; }
    public bool IsActive { get; set; }

    /// <summary>
    /// Check if the underlying process is still running.
    /// </summary>
    public bool IsProcessAlive()
    {
        try
        {
            using var proc = Process.GetProcessById(ProcessId);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public override string ToString()
    {
        var name = $"Client {SlotIndex + 1}";
        return $"{name} (PID: {ProcessId})";
    }
}
