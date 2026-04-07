using System.Diagnostics;

namespace EQSwitch.Models;

/// <summary>
/// Represents a running EQ client instance.
/// Tracks the window handle, process, and associated character info.
/// </summary>
public class EQClient
{
    public IntPtr WindowHandle { get; set; }
    public int ProcessId { get; }
    public string WindowTitle { get; set; } = "";

    /// <summary>
    /// The last EQ-native title seen (e.g. "EverQuest - CharName").
    /// Used for {CHAR} extraction in custom title templates even after renaming.
    /// </summary>
    public string OriginalTitle { get; set; } = "";
    public int SlotIndex { get; set; }

    public EQClient(int processId, IntPtr windowHandle, int slotIndex)
    {
        ProcessId = processId;
        WindowHandle = windowHandle;
        SlotIndex = slotIndex;
    }

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
