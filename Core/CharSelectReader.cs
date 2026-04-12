using System.IO.MemoryMappedFiles;
using System.Text;

namespace EQSwitch.Core;

/// <summary>
/// Creates and manages "Local\EQSwitchCharSel_{PID}" shared memory for
/// character select data exchange with the injected DLL's MQ2 bridge.
///
/// DLL writes: gameState, charCount, character names/levels/classes, mq2Available
/// C# writes: requestedIndex, requestSeq, enterWorldReq
/// DLL acks: ackSeq, enterWorldAck, enterWorldResult
/// </summary>
public sealed class CharSelectReader : IDisposable
{
    private const string SharedMemoryPrefix = "Local\\EQSwitchCharSel_";
    private const uint Magic = 0x45534353; // "ESCS"
    private const uint Version = 2;        // v2: 10 slots + Enter World fields
    private const int MaxChars = 10;
    private const int NameLen = 64;

    // Must match Native/mq2_bridge.h CharSelectShm exactly
    // Total: 768 bytes
    private static readonly int ShmSize = 768;

    // Field offsets (matching #pragma pack(push,1) C++ struct)
    private const int OFF_MAGIC = 0;
    private const int OFF_VERSION = 4;
    private const int OFF_GAMESTATE = 8;
    private const int OFF_CHARCOUNT = 12;
    private const int OFF_SELECTEDINDEX = 16;
    private const int OFF_MQ2AVAILABLE = 20;
    // Character selection request
    private const int OFF_REQUESTEDINDEX = 24;
    private const int OFF_REQUESTSEQ = 28;
    private const int OFF_ACKSEQ = 32;
    // Enter World request (v2)
    private const int OFF_ENTERWORLD_REQ = 36;
    private const int OFF_ENTERWORLD_ACK = 40;
    private const int OFF_ENTERWORLD_RESULT = 44;
    // Character data arrays
    private const int OFF_NAMES = 48;              // 10 * 64 = 640 bytes
    private const int OFF_LEVELS = 48 + 640;       // 10 * 4 = 40 bytes  (= 688)
    private const int OFF_CLASSES = 48 + 640 + 40; // 10 * 4 = 40 bytes  (= 728)
    // Total: 728 + 40 = 768 ✓

    private sealed class MappingEntry : IDisposable
    {
        public readonly MemoryMappedFile Mmf;
        public readonly MemoryMappedViewAccessor Accessor;
        public uint RequestSeq;
        public uint EnterWorldSeq;

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

    /// <summary>
    /// Create shared memory for a process. Call during auto-login setup
    /// (alongside KeyInputWriter.Open).
    /// </summary>
    public bool Open(int pid)
    {
        if (_mappings.ContainsKey(pid)) return true;

        try
        {
            var name = $"{SharedMemoryPrefix}{(uint)pid}";
            var mmf = MemoryMappedFile.CreateOrOpen(name, ShmSize);
            var accessor = mmf.CreateViewAccessor(0, ShmSize);

            // Write header
            accessor.Write(OFF_MAGIC, Magic);
            accessor.Write(OFF_VERSION, Version);
            accessor.Write(OFF_GAMESTATE, -1);
            accessor.Write(OFF_CHARCOUNT, 0);
            accessor.Write(OFF_SELECTEDINDEX, -1);
            accessor.Write(OFF_MQ2AVAILABLE, (uint)0);
            accessor.Write(OFF_REQUESTEDINDEX, -1);
            accessor.Write(OFF_REQUESTSEQ, (uint)0);
            accessor.Write(OFF_ACKSEQ, (uint)0);
            accessor.Write(OFF_ENTERWORLD_REQ, (uint)0);
            accessor.Write(OFF_ENTERWORLD_ACK, (uint)0);
            accessor.Write(OFF_ENTERWORLD_RESULT, 0);

            _mappings[pid] = new MappingEntry(mmf, accessor);
            FileLogger.Info($"CharSelectReader: opened SHM for PID {pid}");
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"CharSelectReader: failed to open SHM for PID {pid}", ex);
            return false;
        }
    }

    /// <summary>Read current game state from DLL. Returns -1 if not available.</summary>
    public int ReadGameState(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return -1;
        return entry.Accessor.ReadInt32(OFF_GAMESTATE);
    }

    /// <summary>True if MQ2 exports were resolved by the DLL.</summary>
    public bool IsMQ2Available(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;
        return entry.Accessor.ReadUInt32(OFF_MQ2AVAILABLE) != 0;
    }

    /// <summary>Read character count at char select. 0 if not at char select or MQ2 unavailable.</summary>
    public int ReadCharCount(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return 0;
        return entry.Accessor.ReadInt32(OFF_CHARCOUNT);
    }

    /// <summary>Read character name at given index (0-based). Empty string if invalid.</summary>
    public string ReadCharName(int pid, int index)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return "";
        if (index < 0 || index >= MaxChars) return "";

        var bytes = new byte[NameLen];
        entry.Accessor.ReadArray(OFF_NAMES + (index * NameLen), bytes, 0, NameLen);

        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = NameLen;
        return Encoding.ASCII.GetString(bytes, 0, len);
    }

    /// <summary>Read character level at given index.</summary>
    public int ReadCharLevel(int pid, int index)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return 0;
        if (index < 0 || index >= MaxChars) return 0;
        return entry.Accessor.ReadInt32(OFF_LEVELS + (index * 4));
    }

    /// <summary>Read all character names. Returns empty array if not at char select.</summary>
    public string[] ReadAllCharNames(int pid)
    {
        int count = ReadCharCount(pid);
        if (count <= 0) return Array.Empty<string>();

        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = ReadCharName(pid, i);
        return names;
    }

    // ─── Character Selection ─────────────────────────────────────

    /// <summary>Request the DLL to select a character by 0-based index.</summary>
    public bool RequestSelection(int pid, int index)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;

        entry.Accessor.Write(OFF_REQUESTEDINDEX, index);
        Thread.MemoryBarrier(); // ensure index visible before new seq
        entry.RequestSeq++;
        entry.Accessor.Write(OFF_REQUESTSEQ, entry.RequestSeq);

        FileLogger.Info($"CharSelectReader: requested selection index {index} for PID {pid} (seq={entry.RequestSeq})");
        return true;
    }

    /// <summary>Request selection by 1-based slot number (1-10). Converts to 0-based internally.</summary>
    public bool RequestSelectionBySlot(int pid, int slot)
    {
        if (slot < 1 || slot > MaxChars) return false;
        return RequestSelection(pid, slot - 1);
    }

    /// <summary>
    /// Request selection by character name (case-insensitive).
    /// Returns the index selected, or -1 if character not found.
    /// </summary>
    public int RequestSelectionByName(int pid, string characterName)
    {
        int count = ReadCharCount(pid);
        for (int i = 0; i < count; i++)
        {
            if (string.Equals(ReadCharName(pid, i), characterName, StringComparison.OrdinalIgnoreCase))
            {
                RequestSelection(pid, i);
                return i;
            }
        }

        FileLogger.Warn($"CharSelectReader: character '{characterName}' not found (PID {pid}, {count} chars available)");
        return -1;
    }

    /// <summary>Check if the last selection request was acknowledged by the DLL.</summary>
    public bool IsSelectionAcknowledged(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;
        if (entry.RequestSeq == 0) return false;
        uint ackSeq = entry.Accessor.ReadUInt32(OFF_ACKSEQ);
        return ackSeq == entry.RequestSeq;
    }

    // ─── Enter World ─────────────────────────────────────────────

    /// <summary>Request the DLL to click CLW_EnterWorldButton (in-process, no focus-faking needed).</summary>
    public bool RequestEnterWorld(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;

        entry.Accessor.Write(OFF_ENTERWORLD_RESULT, 0); // reset result
        Thread.MemoryBarrier(); // ensure result=0 visible before new seq
        entry.EnterWorldSeq++;
        entry.Accessor.Write(OFF_ENTERWORLD_REQ, entry.EnterWorldSeq);

        FileLogger.Info($"CharSelectReader: requested Enter World for PID {pid} (seq={entry.EnterWorldSeq})");
        return true;
    }

    /// <summary>Check if the last Enter World request was acknowledged by the DLL.</summary>
    public bool IsEnterWorldAcknowledged(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;
        if (entry.EnterWorldSeq == 0) return false;
        uint ackSeq = entry.Accessor.ReadUInt32(OFF_ENTERWORLD_ACK);
        return ackSeq == entry.EnterWorldSeq;
    }

    /// <summary>Read Enter World result: 0=pending, 1=clicked, -1=button not found.</summary>
    public int ReadEnterWorldResult(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return 0;
        return entry.Accessor.ReadInt32(OFF_ENTERWORLD_RESULT);
    }

    // ─── Lifecycle ───────────────────────────────────────────────

    /// <summary>Close shared memory for a process.</summary>
    public void Close(int pid)
    {
        if (_mappings.TryGetValue(pid, out var entry))
        {
            entry.Dispose();
            _mappings.Remove(pid);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _mappings.Values)
            entry.Dispose();
        _mappings.Clear();
    }
}
