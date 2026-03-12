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
    public string? CharacterName { get; set; }
    public int SlotIndex { get; set; }
    public bool IsActive { get; set; }

    /// <summary>
    /// Attempt to resolve the character name from the window title.
    /// EQ window titles typically contain the character name.
    /// Format varies by client: "EverQuest - CharName" or just "EverQuest"
    /// </summary>
    public void ResolveCharacterName()
    {
        if (string.IsNullOrEmpty(WindowTitle)) return;

        // Shards of Dalaya uses "EverQuest" as the title during login
        // and "EverQuest - CharName" once logged in
        if (WindowTitle.Contains(" - "))
        {
            CharacterName = WindowTitle.Split(" - ", 2)[1].Trim();
        }
    }

    /// <summary>
    /// Check if the underlying process is still running.
    /// </summary>
    public bool IsProcessAlive()
    {
        try
        {
            var proc = Process.GetProcessById(ProcessId);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public override string ToString()
    {
        var name = CharacterName ?? $"Client {SlotIndex + 1}";
        return $"{name} (PID: {ProcessId})";
    }
}
