// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System.IO.MemoryMappedFiles;
using System.Text;

namespace EQSwitch.Core;

/// <summary>
/// Creates and manages "Local\EQSwitchLogin_{PID}" shared memory for
/// in-process login via the native DLL's LoginStateMachine.
///
/// C# creates the mapping and writes credentials + command.
/// DLL reads credentials, drives EQ's login UI widgets (SetEditText / ClickButton),
/// and writes phase transitions + character data back to SHM.
/// Replaces the entire PostMessage/DirectInput key injection login path.
///
/// Must match Native/login_shm.h LoginShm struct exactly (#pragma pack(push, 1)).
/// </summary>
public sealed class LoginShmWriter : IDisposable
{
    private const string SharedMemoryPrefix = "Local\\EQSwitchLogin_";
    private const uint Magic = 0x45534C53;   // "ESLS"
    private const uint Version = 1;
    private const int MaxChars = 10;         // LOGIN_MAX_CHARS
    private const int NameLen = 64;          // LOGIN_NAME_LEN
    private const int PassLen = 128;         // LOGIN_PASS_LEN
    private const int ServerLen = 64;        // LOGIN_SERVER_LEN
    private const int CharLen = 64;          // LOGIN_CHAR_LEN
    private const int ErrorLen = 256;        // LOGIN_ERROR_LEN

    // Total struct size: 1340 bytes (verified against login_shm.h)
    private const int ShmSize = 1340;

    // ── Commands (C# → DLL) ──────────────────────────────────────
    private const uint CMD_NONE = 0;
    private const uint CMD_LOGIN = 1;
    private const uint CMD_CANCEL = 2;

    // ── Field offsets (matching #pragma pack(push,1) C++ struct) ──
    private const int OFF_MAGIC = 0;           // uint32  (4)
    private const int OFF_VERSION = 4;         // uint32  (4)
    private const int OFF_COMMAND = 8;         // uint32  (4)
    private const int OFF_COMMAND_SEQ = 12;    // uint32  (4)
    private const int OFF_COMMAND_ACK = 16;    // uint32  (4)
    private const int OFF_USERNAME = 20;       // char[64]
    private const int OFF_PASSWORD = 84;       // char[128]
    private const int OFF_SERVER = 212;        // char[64]
    private const int OFF_CHARACTER = 276;     // char[64]
    private const int OFF_PHASE = 340;         // uint32  (4)
    private const int OFF_GAMESTATE = 344;     // int32   (4)
    private const int OFF_ERROR_MSG = 348;     // char[256]
    private const int OFF_RETRY_COUNT = 604;   // uint32  (4)
    private const int OFF_CHAR_COUNT = 608;    // int32   (4)
    private const int OFF_SELECTED_IDX = 612;  // int32   (4)
    private const int OFF_CHAR_NAMES = 616;    // char[10][64] = 640
    private const int OFF_CHAR_LEVELS = 1256;  // int32[10]    = 40
    private const int OFF_CHAR_CLASSES = 1296; // int32[10]    = 40
    private const int OFF_DIAGNOSTIC = 1336;   // uint32  (4)
    // Total: 1340 ✓

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

    /// <summary>
    /// Create LoginShm for a process. Call early in the login sequence —
    /// the DLL's ActivateThread lazily opens it via OpenFileMappingA.
    /// </summary>
    public bool Open(int pid)
    {
        if (_mappings.ContainsKey(pid)) return true;

        try
        {
            var name = $"{SharedMemoryPrefix}{(uint)pid}";
            var mmf = MemoryMappedFile.CreateOrOpen(name, ShmSize);
            var accessor = mmf.CreateViewAccessor(0, ShmSize);

            // Write header — all fields zero/idle
            accessor.Write(OFF_MAGIC, Magic);
            accessor.Write(OFF_VERSION, Version);
            accessor.Write(OFF_COMMAND, CMD_NONE);
            accessor.Write(OFF_COMMAND_SEQ, (uint)0);
            accessor.Write(OFF_COMMAND_ACK, (uint)0);
            // Zero credential fields
            accessor.WriteArray(OFF_USERNAME, new byte[NameLen], 0, NameLen);
            accessor.WriteArray(OFF_PASSWORD, new byte[PassLen], 0, PassLen);
            accessor.WriteArray(OFF_SERVER, new byte[ServerLen], 0, ServerLen);
            accessor.WriteArray(OFF_CHARACTER, new byte[CharLen], 0, CharLen);
            // Zero state fields
            accessor.Write(OFF_PHASE, (uint)0);       // PHASE_IDLE
            accessor.Write(OFF_GAMESTATE, 0);
            accessor.WriteArray(OFF_ERROR_MSG, new byte[ErrorLen], 0, ErrorLen);
            accessor.Write(OFF_RETRY_COUNT, (uint)0);
            accessor.Write(OFF_CHAR_COUNT, 0);
            accessor.Write(OFF_SELECTED_IDX, -1);
            accessor.Write(OFF_DIAGNOSTIC, (uint)0);

            _mappings[pid] = new MappingEntry(mmf, accessor);
            FileLogger.Info($"LoginShmWriter: opened {name} ({ShmSize} bytes)");
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"LoginShmWriter: failed to open SHM for PID {pid}", ex);
            return false;
        }
    }

    // ─── Commands (C# → DLL) ─────────────────────────────────────

    /// <summary>
    /// Send LOGIN command with credentials. The DLL's LoginStateMachine will
    /// drive the entire login flow: credentials → connect → server → charselect.
    /// Password is written to SHM briefly — the DLL copies it locally and zeros
    /// the SHM password field immediately on pickup.
    /// </summary>
    public bool SendLoginCommand(int pid, string username, string password,
                                  string server, string character)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;

        try
        {
            // Write credentials first
            WriteString(entry.Accessor, OFF_USERNAME, username, NameLen);
            WriteString(entry.Accessor, OFF_PASSWORD, password, PassLen);
            WriteString(entry.Accessor, OFF_SERVER, server, ServerLen);
            WriteString(entry.Accessor, OFF_CHARACTER, character, CharLen);

            // Write command + increment sequence — credentials must be visible first
            entry.Accessor.Write(OFF_COMMAND, CMD_LOGIN);
            Thread.MemoryBarrier();
            entry.CommandSeq++;
            entry.Accessor.Write(OFF_COMMAND_SEQ, entry.CommandSeq);

            FileLogger.Info($"LoginShmWriter: LOGIN command sent for PID {pid} " +
                $"(user='{username}', server='{server}', char='{character}', seq={entry.CommandSeq})");
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"LoginShmWriter: SendLoginCommand failed for PID {pid}", ex);
            return false;
        }
    }

    /// <summary>
    /// Send CANCEL command to abort the in-progress login.
    /// Used when C# wants to stop at charselect — the DLL's state machine
    /// would otherwise auto-advance to enter-world.
    /// </summary>
    public bool SendCancelCommand(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;

        try
        {
            entry.Accessor.Write(OFF_COMMAND, CMD_CANCEL);
            Thread.MemoryBarrier();
            entry.CommandSeq++;
            entry.Accessor.Write(OFF_COMMAND_SEQ, entry.CommandSeq);

            FileLogger.Info($"LoginShmWriter: CANCEL command sent for PID {pid} (seq={entry.CommandSeq})");
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"LoginShmWriter: SendCancelCommand failed for PID {pid}", ex);
            return false;
        }
    }

    // ─── State reads (DLL → C#) ──────────────────────────────────

    /// <summary>True if the DLL acknowledged the last command (commandAck == commandSeq).</summary>
    public bool IsCommandAcknowledged(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;
        if (entry.CommandSeq == 0) return false;
        return entry.Accessor.ReadUInt32(OFF_COMMAND_ACK) == entry.CommandSeq;
    }

    /// <summary>Read current login phase. Returns Idle if mapping not found.</summary>
    public LoginPhase ReadPhase(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return LoginPhase.Idle;
        return (LoginPhase)entry.Accessor.ReadUInt32(OFF_PHASE);
    }

    /// <summary>Read error message set by DLL on PHASE_ERROR.</summary>
    public string ReadError(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return "";
        return ReadString(entry.Accessor, OFF_ERROR_MSG, ErrorLen);
    }

    /// <summary>Read retry count from DLL.</summary>
    public uint ReadRetryCount(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return 0;
        return entry.Accessor.ReadUInt32(OFF_RETRY_COUNT);
    }

    /// <summary>Read game state reported by DLL.</summary>
    public int ReadGameState(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return -1;
        return entry.Accessor.ReadInt32(OFF_GAMESTATE);
    }

    // ─── Character data (populated by DLL at charselect) ─────────

    /// <summary>Number of characters at charselect. 0 if not at charselect.</summary>
    public int ReadCharCount(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return 0;
        return entry.Accessor.ReadInt32(OFF_CHAR_COUNT);
    }

    /// <summary>Currently selected character index (-1 if none).</summary>
    public int ReadSelectedIndex(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return -1;
        return entry.Accessor.ReadInt32(OFF_SELECTED_IDX);
    }

    /// <summary>Read character name at given index (0-based).</summary>
    public string ReadCharName(int pid, int index)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return "";
        if (index < 0 || index >= MaxChars) return "";
        return ReadString(entry.Accessor, OFF_CHAR_NAMES + (index * NameLen), NameLen);
    }

    /// <summary>Read all character names. Returns empty array if not at charselect.</summary>
    public string[] ReadAllCharNames(int pid)
    {
        int count = ReadCharCount(pid);
        if (count <= 0) return Array.Empty<string>();

        var names = new string[Math.Min(count, MaxChars)];
        for (int i = 0; i < names.Length; i++)
            names[i] = ReadCharName(pid, i);
        return names;
    }

    /// <summary>Read character level at given index.</summary>
    public int ReadCharLevel(int pid, int index)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return 0;
        if (index < 0 || index >= MaxChars) return 0;
        return entry.Accessor.ReadInt32(OFF_CHAR_LEVELS + (index * 4));
    }

    // ─── Lifecycle ───────────────────────────────────────────────

    /// <summary>Close shared memory for a process.</summary>
    public void Close(int pid)
    {
        if (_mappings.TryGetValue(pid, out var entry))
        {
            try
            {
                // Zero password field as defense-in-depth
                entry.Accessor.WriteArray(OFF_PASSWORD, new byte[PassLen], 0, PassLen);
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"LoginShmWriter: Close cleanup failed for PID {pid}: {ex.Message}");
            }
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
            try { entry.Accessor.WriteArray(OFF_PASSWORD, new byte[PassLen], 0, PassLen); }
            catch (Exception ex) { FileLogger.Warn($"LoginShmWriter: Dispose cleanup failed for PID {pid}: {ex.Message}"); }
            entry.Dispose();
        }
        _mappings.Clear();
    }

    // ─── Helpers ─────────────────────────────────────────────────

    /// <summary>Human-readable phase name for status reporting.</summary>
    public static string PhaseName(LoginPhase phase) => phase switch
    {
        LoginPhase.Idle => "Idle",
        LoginPhase.WaitLoginScreen => "Waiting for login screen",
        LoginPhase.TypingCredentials => "Setting credentials",
        LoginPhase.ClickingConnect => "Clicking connect",
        LoginPhase.WaitConnectResponse => "Waiting for server response",
        LoginPhase.ServerSelect => "Server select",
        LoginPhase.WaitServerLoad => "Loading character select",
        LoginPhase.CharSelect => "Character select",
        LoginPhase.EnteringWorld => "Entering world",
        LoginPhase.Complete => "Complete",
        LoginPhase.Error => "Error",
        _ => $"Unknown ({(uint)phase})"
    };

    private static void WriteString(MemoryMappedViewAccessor accessor, int offset,
                                     string value, int maxLen)
    {
        var bytes = new byte[maxLen];
        if (!string.IsNullOrEmpty(value))
        {
            int written = Encoding.ASCII.GetBytes(value, 0,
                Math.Min(value.Length, maxLen - 1), bytes, 0);
            bytes[written] = 0; // null-terminate
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
