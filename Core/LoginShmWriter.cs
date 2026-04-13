using System.IO.MemoryMappedFiles;
using System.Text;

namespace EQSwitch.Core;

/// <summary>
/// Creates and manages "Local\EQSwitchLogin_{PID}" shared memory for
/// in-process login via the MQ2 bridge state machine.
///
/// C# writes: username, password, server, character, command
/// DLL writes: phase, gameState, errorMessage, character data
///
/// The DLL's LoginStateMachine drives EQ's UI widgets directly --
/// SetWindowText on edit fields, WndNotification on buttons.
/// No PostMessage, no focus-faking needed.
/// </summary>
public sealed class LoginShmWriter : IDisposable
{
    private const string SharedMemoryPrefix = "Local\\EQSwitchLogin_";
    private const uint Magic = 0x45534C53; // "ESLS"
    private const uint Version = 1;

    // Must match Native/login_shm.h LoginShm struct exactly
    private const int ShmSize = 1536; // generous -- actual struct is ~1200 bytes

    // Field offsets (matching #pragma pack(push,1) C++ struct)
    private const int OFF_MAGIC        = 0;
    private const int OFF_VERSION      = 4;
    private const int OFF_COMMAND      = 8;
    private const int OFF_COMMANDSEQ   = 12;
    private const int OFF_COMMANDACK   = 16;
    private const int OFF_USERNAME     = 20;   // 64 bytes
    private const int OFF_PASSWORD     = 84;   // 128 bytes
    private const int OFF_SERVER       = 212;  // 64 bytes
    private const int OFF_CHARACTER    = 276;  // 64 bytes
    private const int OFF_PHASE        = 340;
    private const int OFF_GAMESTATE    = 344;
    private const int OFF_ERRORMSG     = 348;  // 256 bytes
    private const int OFF_RETRYCOUNT   = 604;
    private const int OFF_CHARCOUNT    = 608;
    private const int OFF_SELECTEDIDX  = 612;
    private const int OFF_CHARNAMES    = 616;  // 8 * 64 = 512
    private const int OFF_CHARLEVELS   = 1128; // 8 * 4 = 32
    private const int OFF_CHARCLASSES  = 1160; // 8 * 4 = 32
    private const int OFF_DIAGNOSTIC   = 1192;

    private sealed class MappingEntry : IDisposable
    {
        public readonly MemoryMappedFile Mmf;
        public readonly MemoryMappedViewAccessor Accessor;
        public uint CommandSeq;

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

    public bool Open(int pid)
    {
        if (_mappings.ContainsKey(pid)) return true;

        try
        {
            var name = $"{SharedMemoryPrefix}{(uint)pid}";
            var mmf = SecureMemoryMappedFile.CreateOrOpen(name, ShmSize);
            var accessor = mmf.CreateViewAccessor(0, ShmSize, MemoryMappedFileAccess.ReadWrite);

            // Write header
            accessor.Write(OFF_MAGIC, Magic);
            accessor.Write(OFF_VERSION, Version);
            accessor.Write(OFF_COMMAND, (uint)0);     // LOGIN_CMD_NONE
            accessor.Write(OFF_COMMANDSEQ, (uint)0);
            accessor.Write(OFF_COMMANDACK, (uint)0);
            accessor.Write(OFF_PHASE, (uint)0);       // PHASE_IDLE
            accessor.Write(OFF_GAMESTATE, -1);
            accessor.Write(OFF_RETRYCOUNT, (uint)0);
            accessor.Write(OFF_CHARCOUNT, 0);
            accessor.Write(OFF_SELECTEDIDX, -1);
            accessor.Write(OFF_DIAGNOSTIC, (uint)0);

            _mappings[pid] = new MappingEntry(mmf, accessor);
            FileLogger.Info($"LoginShmWriter: opened {name}");
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"LoginShmWriter: failed to open for PID {pid}", ex);
            return false;
        }
    }

    /// <summary>Write login credentials to SHM. Call before SendLoginCommand.</summary>
    public void WriteCredentials(int pid, string username, string password, string server, string character)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;

        WriteString(entry.Accessor, OFF_USERNAME, username, 64);
        WriteString(entry.Accessor, OFF_PASSWORD, password, 128);
        WriteString(entry.Accessor, OFF_SERVER, server, 64);
        WriteString(entry.Accessor, OFF_CHARACTER, character, 64);

        FileLogger.Info($"LoginShmWriter: credentials written for PID {pid} (user={username}, server={server}, char={character})");
    }

    /// <summary>Send the LOGIN command to the DLL state machine.</summary>
    public void SendLoginCommand(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;

        entry.Accessor.Write(OFF_COMMAND, (uint)1); // LOGIN_CMD_LOGIN
        entry.CommandSeq++;
        entry.Accessor.Write(OFF_COMMANDSEQ, entry.CommandSeq);

        FileLogger.Info($"LoginShmWriter: LOGIN command sent (seq={entry.CommandSeq})");
    }

    /// <summary>Send the CANCEL command to abort login.</summary>
    public void SendCancelCommand(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;

        entry.Accessor.Write(OFF_COMMAND, (uint)2); // LOGIN_CMD_CANCEL
        entry.CommandSeq++;
        entry.Accessor.Write(OFF_COMMANDSEQ, entry.CommandSeq);
    }

    /// <summary>Enable diagnostic widget enumeration mode.</summary>
    public void SetDiagnosticMode(int pid, bool enabled)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;
        entry.Accessor.Write(OFF_DIAGNOSTIC, enabled ? (uint)1 : (uint)0);
    }

    /// <summary>Read current login phase from DLL.</summary>
    public LoginPhase ReadPhase(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return LoginPhase.Idle;
        return (LoginPhase)entry.Accessor.ReadUInt32(OFF_PHASE);
    }

    /// <summary>Read current game state.</summary>
    public int ReadGameState(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return -1;
        return entry.Accessor.ReadInt32(OFF_GAMESTATE);
    }

    /// <summary>Read error message on PHASE_ERROR.</summary>
    public string ReadErrorMessage(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return "";
        return ReadString(entry.Accessor, OFF_ERRORMSG, 256);
    }

    /// <summary>Read retry count.</summary>
    public uint ReadRetryCount(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return 0;
        return entry.Accessor.ReadUInt32(OFF_RETRYCOUNT);
    }

    /// <summary>Check if the DLL acknowledged the last command.</summary>
    public bool IsCommandAcknowledged(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;
        return entry.Accessor.ReadUInt32(OFF_COMMANDACK) == entry.CommandSeq;
    }

    /// <summary>Clear password from SHM after login completes (defense in depth).</summary>
    public void ClearPassword(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return;
        entry.Accessor.WriteArray(OFF_PASSWORD, new byte[128], 0, 128);
    }

    /// <summary>Read character count at char select.</summary>
    public int ReadCharCount(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return 0;
        return entry.Accessor.ReadInt32(OFF_CHARCOUNT);
    }

    /// <summary>Read character name at index.</summary>
    public string ReadCharName(int pid, int index)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return "";
        if (index < 0 || index >= 8) return "";
        return ReadString(entry.Accessor, OFF_CHARNAMES + (index * 64), 64);
    }

    public void Close(int pid)
    {
        if (_mappings.TryGetValue(pid, out var entry))
        {
            // Clear password before closing
            try { entry.Accessor.WriteArray(OFF_PASSWORD, new byte[128], 0, 128); } catch { }
            entry.Dispose();
            _mappings.Remove(pid);
            FileLogger.Info($"LoginShmWriter: closed mapping for PID {pid}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (pid, entry) in _mappings)
        {
            try { entry.Accessor.WriteArray(OFF_PASSWORD, new byte[128], 0, 128); } catch { }
            entry.Dispose();
        }
        _mappings.Clear();
    }

    // ─── Helpers ──────────────────────────────────────────

    private static void WriteString(MemoryMappedViewAccessor accessor, int offset, string value, int maxLen)
    {
        var bytes = new byte[maxLen];
        if (!string.IsNullOrEmpty(value))
        {
            var encoded = Encoding.ASCII.GetBytes(value);
            var len = Math.Min(encoded.Length, maxLen - 1);
            Array.Copy(encoded, bytes, len);
        }
        accessor.WriteArray(offset, bytes, 0, maxLen);
    }

    private static string ReadString(MemoryMappedViewAccessor accessor, int offset, int maxLen)
    {
        var bytes = new byte[maxLen];
        accessor.ReadArray(offset, bytes, 0, maxLen);
        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = maxLen;
        return Encoding.ASCII.GetString(bytes, 0, len);
    }
}

/// <summary>Login phase enum matching Native/login_shm.h LoginPhase.</summary>
public enum LoginPhase : uint
{
    Idle = 0,
    WaitLoginScreen = 1,
    TypingCredentials = 2,
    ClickingConnect = 3,
    WaitConnectResponse = 4,
    ServerSelect = 5,
    WaitServerLoad = 6,
    CharSelect = 7,
    EnteringWorld = 8,
    Complete = 10,
    Error = 99,
}
