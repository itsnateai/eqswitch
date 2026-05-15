// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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
    // v2 (2026-05-09): added autoLoginActive field at offset 1340. Backward-
    // compatible append — pre-v2 native readers see the first 1340 bytes
    // unchanged. New autoLoginActive field controls whether eqswitch-di8.cpp's
    // pre-login kPromptWindows[] dismiss machinery suppresses itself for this
    // PID (so AutoLoginManager's BURST flow isn't competed-against during
    // server-select / charselect-load).
    //
    // v3 (2026-05-15): appended LIVE OK_Display poll-tick mirror —
    // okDisplayText[256] at offset 1344 + okDisplayClass (uint32) at offset
    // 1600. Distinct from the existing errorMessage field (set-once on
    // PHASE_ERROR). v3 fields are LIVE: native writes them on every
    // PHASE_WAIT_CONNECT_RESP poll where g_pOkDisplay returns text, clears
    // when no dialog. Pre-classified into None/Fatal/Recoverable/Success so
    // C# doesn't re-implement the strstr matching in login_state_machine.cpp.
    // Backward-compatible append — v2 native readers see the first 1344
    // bytes unchanged and ignore the trailing 260 bytes.
    private const uint Version = 3;
    private const int MaxChars = 10;         // LOGIN_MAX_CHARS
    private const int NameLen = 64;          // LOGIN_NAME_LEN
    private const int PassLen = 128;         // LOGIN_PASS_LEN
    private const int ServerLen = 64;        // LOGIN_SERVER_LEN
    private const int CharLen = 64;          // LOGIN_CHAR_LEN
    private const int ErrorLen = 256;        // LOGIN_ERROR_LEN

    // Total struct size: 1604 bytes (verified against login_shm.h v3)
    // v1 was 1340; v2 appended a 4-byte autoLoginActive field at offset 1340
    // (size 1344); v3 appends okDisplayText[256] at offset 1344 + a 4-byte
    // okDisplayClass at offset 1600 (size 1604).
    private const int ShmSize = 1604;

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
    private const int OFF_AUTO_LOGIN_ACTIVE = 1340;  // uint32  (4)  — v2 append
    private const int OFF_OK_DISPLAY_TEXT = 1344;    // char[256]    — v3 append
    private const int OFF_OK_DISPLAY_CLASS = 1600;   // uint32  (4)  — v3 append
    // Total: 1604 ✓

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
        // R2 (v3.18.0 verifier finding T2-O #13): re-Open on an existing
        // mapping was returning true without re-zeroing DLL→C# state. If a
        // prior login left okDisplayClass=Fatal in the mapping and the user
        // re-fires login on the SAME PID/instance, the new retry loop reads
        // stale Fatal from the prior session at retry-loop entry — silent
        // wrong-classification before any new dialog is even visible.
        // Defense-in-depth: ALWAYS reset DLL→C# fields at Open() entry,
        // including the early-return path. Cheap (a few WriteArray calls)
        // and eliminates the failure mode.
        if (_mappings.TryGetValue(pid, out var existing))
        {
            ResetDllToCsharpFields(existing.Accessor);
            return true;
        }

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
            // Zero state fields (DLL→C# state — also done by ResetDllToCsharpFields)
            ResetDllToCsharpFields(accessor);
            // Default 0 = not active. AutoLoginManager flips to 1 immediately
            // after Open succeeds (see SetAutoLoginActive); bare-launch path
            // leaves it at 0 so eqswitch-di8.cpp's kPromptWindows dismiss runs.
            accessor.Write(OFF_AUTO_LOGIN_ACTIVE, (uint)0);

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

    /// <summary>
    /// R2 (v3.18.0): zero ALL DLL→C# state fields. Called from Open() on
    /// both the fresh-mapping init path AND the existing-mapping re-Open
    /// path so a re-fire of login on the same LoginShmWriter instance can't
    /// inherit stale phase/error/charselect/OK-Display state from a prior
    /// session. autoLoginActive is NOT reset here — that's the C#→DLL
    /// direction and managed by SetAutoLoginActive.
    ///
    /// R3 (v3.18.0 verifier T2-O #2): also zero charNames/charLevels/
    /// charClasses arrays (720 bytes total). Without this, a re-Open on the
    /// same PID after a prior charselect session would leave stale character
    /// names visible to any C# read before native re-populates at the next
    /// PHASE_CHAR_SELECT transition. ReadAllCharNames/ReadCharLevel et al.
    /// would surface prior-session character data.
    /// </summary>
    private static void ResetDllToCsharpFields(MemoryMappedViewAccessor accessor)
    {
        accessor.Write(OFF_PHASE, (uint)0);       // PHASE_IDLE
        accessor.Write(OFF_GAMESTATE, 0);
        accessor.WriteArray(OFF_ERROR_MSG, new byte[ErrorLen], 0, ErrorLen);
        accessor.Write(OFF_RETRY_COUNT, (uint)0);
        accessor.Write(OFF_CHAR_COUNT, 0);
        accessor.Write(OFF_SELECTED_IDX, -1);
        accessor.Write(OFF_DIAGNOSTIC, (uint)0);
        // R3: charselect arrays — 640 + 40 + 40 = 720 bytes of stale data
        // would otherwise persist across Open() calls on the same PID. Tiny
        // cost; eliminates a class of phantom-char display bugs on re-login.
        accessor.WriteArray(OFF_CHAR_NAMES, new byte[MaxChars * NameLen], 0, MaxChars * NameLen);
        accessor.WriteArray(OFF_CHAR_LEVELS, new byte[MaxChars * 4], 0, MaxChars * 4);
        accessor.WriteArray(OFF_CHAR_CLASSES, new byte[MaxChars * 4], 0, MaxChars * 4);
        // v3 LIVE OK_Display fields — load-bearing for the v3.18.0 race fix:
        // a stale Fatal class from a prior session would short-circuit the
        // retry budget on the very first iteration before native re-publishes.
        accessor.WriteArray(OFF_OK_DISPLAY_TEXT, new byte[ErrorLen], 0, ErrorLen);
        accessor.Write(OFF_OK_DISPLAY_CLASS, (uint)0);
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

            // Username is a credential half on Dalaya (pairs with the DPAPI-encrypted
            // password). Mirror the v3.15.2 + round-3 redaction stance: don't log the
            // username, even structurally. Server and character are non-secret.
            FileLogger.Info($"LoginShmWriter: LOGIN command sent for PID {pid} " +
                $"(user=<redacted>, server='{server}', char='{character}', seq={entry.CommandSeq})");
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

    /// <summary>
    /// Set or clear the autoLoginActive flag for a PID. When set to true,
    /// eqswitch-di8.cpp's pre-login kPromptWindows[] dismiss machinery (the
    /// v3.15.5 EULA / main-menu auto-clicker) STANDS DOWN for this PID — the
    /// C#-side BURST flow owns keystroke injection, and concurrent native
    /// widget-clicks at server-select / charselect-load can close the EQ
    /// process (the 2026-05-09 team1 regression root cause).
    ///
    /// Call SetAutoLoginActive(pid, true) immediately after Open succeeds.
    /// Call SetAutoLoginActive(pid, false) in the autologin cleanup finally
    /// block — bare-launch path stays at 0 (the default) so EULA dismiss
    /// continues to fire as designed.
    /// </summary>
    public bool SetAutoLoginActive(int pid, bool active)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return false;
        try
        {
            entry.Accessor.Write(OFF_AUTO_LOGIN_ACTIVE, active ? (uint)1 : (uint)0);
            // Symmetry with SendLoginCommand (line 164): publish the write
            // before any subsequent code observes a "ready" signal. On x86/x64
            // TSO this is redundant, but matches the explicit-barrier pattern
            // used elsewhere in this class so cross-process visibility doesn't
            // depend on MSVC's `/volatile:ms` default for the native reader's
            // `volatile uint32_t autoLoginActive` field. Verifier convergence
            // 2026-05-09 (T2-S, T3-S, T3-O all flagged).
            Thread.MemoryBarrier();
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"LoginShmWriter: SetAutoLoginActive({pid}, {active}) failed", ex);
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

    /// <summary>
    /// v3 LIVE OK_Display poll-tick mirror. Returns the most recent text
    /// inside EQ's OK_Display widget as observed by the DLL state machine
    /// during PHASE_WAIT_CONNECT_RESP. Empty string if no dialog is up
    /// or if the DLL has cleared the field after a successful advance.
    ///
    /// Distinct from <see cref="ReadError"/> — that field is set ONCE on
    /// PHASE_ERROR via SetError. This field is LIVE: cleared every poll
    /// where g_pOkDisplay returns empty (or the widget cache is empty).
    ///
    /// Consumer: AutoLoginManager.RunLoginSequence retry loop reads this
    /// to distinguish stale-session from wrong-password from truncated-creds.
    /// </summary>
    public string ReadOkDisplayText(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return "";
        return ReadString(entry.Accessor, OFF_OK_DISPLAY_TEXT, ErrorLen);
    }

    /// <summary>
    /// v3 LIVE OK_Display classification (None/Fatal/Recoverable/Success).
    /// Pre-classified by native at the always-on PollOkDisplayToShm probe
    /// (login_state_machine.cpp). Always paired with <see cref="ReadOkDisplayText"/>
    /// — the text gives the WHY, the class gives the WHAT-TO-DO.
    ///
    /// PREFER <see cref="ReadOkDisplaySnapshot"/> for retry-loop usage —
    /// reading class + text as separate calls can produce a torn snapshot
    /// (native may write/clear between the two reads). This method exists
    /// for cases where only the class is needed.
    /// </summary>
    public OkDisplayClass ReadOkDisplayClass(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return OkDisplayClass.None;
        uint raw = entry.Accessor.ReadUInt32(OFF_OK_DISPLAY_CLASS);
        // Defensive: any out-of-range value (corrupt mapping, version skew with
        // a future v4 native that adds class buckets) is treated as None — fail
        // safe to "no classification, fall through to existing logic".
        return raw <= 3 ? (OkDisplayClass)raw : OkDisplayClass.None;
    }

    /// <summary>
    /// R2 (v3.18.0): atomic-read snapshot of OK_Display class + text with
    /// torn-read detection. Reads class, reads text, re-reads class — if the
    /// class changed between reads, the native side wrote (likely
    /// SetOkDisplay/ClearOkDisplay) mid-snapshot. In that case treat the
    /// snapshot as None — the next poll will get a coherent value.
    ///
    /// Verifier finding T2-O #14 + T3-O P3 (2026-05-15 R1 sweep): without
    /// this, the C# retry loop could log "class=Fatal text='Logging in to
    /// the server'" — class from one tick, text from the next. Worse, it
    /// could short-circuit the retry budget on a Fatal class that was
    /// actually the residual from the prior tick (text already cleared).
    ///
    /// Returns (None, "") if the mapping isn't open. The torn-read fallback
    /// is also (None, "") — callers fall through to existing
    /// staleSessionWaitMs / gameState gate behavior, which is the safe
    /// default the v3.17.0 code path used before v3.18.0 added classification.
    /// </summary>
    public (OkDisplayClass Class, string Text) ReadOkDisplaySnapshot(int pid)
    {
        if (!_mappings.TryGetValue(pid, out var entry)) return (OkDisplayClass.None, "");

        uint rawClass1 = entry.Accessor.ReadUInt32(OFF_OK_DISPLAY_CLASS);
        string text = ReadString(entry.Accessor, OFF_OK_DISPLAY_TEXT, ErrorLen);
        uint rawClass2 = entry.Accessor.ReadUInt32(OFF_OK_DISPLAY_CLASS);

        if (rawClass1 != rawClass2)
        {
            // Native wrote between our reads — snapshot is incoherent. Treat
            // as None; the next retry-loop iteration (or next snapshot call)
            // will get a coherent read.
            FileLogger.Info($"LoginShmWriter: ReadOkDisplaySnapshot torn read for PID {pid} (class {rawClass1}→{rawClass2}); returning None");
            return (OkDisplayClass.None, "");
        }

        OkDisplayClass okClass = rawClass1 <= 3 ? (OkDisplayClass)rawClass1 : OkDisplayClass.None;
        return (okClass, text);
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

/// <summary>
/// v3 SHM (2026-05-15): pre-classification of the LIVE OK_Display dialog
/// text by native (login_state_machine.cpp PHASE_WAIT_CONNECT_RESP). Lets
/// the C# AutoLoginManager retry loop tune behavior without re-implementing
/// the strstr matching that already lives next to the dialog-read in native.
/// </summary>
public enum OkDisplayClass : uint
{
    /// <summary>No dialog up, OR widget cached but ReadWindowText returned empty.</summary>
    None = 0,

    /// <summary>
    /// Hard-stop error: "password were not valid", "Invalid Password",
    /// "enter a username and password". Re-typing won't help — abort retry budget.
    /// </summary>
    Fatal = 1,

    /// <summary>
    /// Any other dialog text — stale-session, server-busy, truncated-creds, etc.
    /// C# tunes the recovery wait based on text patterns: "stale" → 30s,
    /// "truncated" → 1s, default → existing StaleSessionWaitMs.
    /// </summary>
    Recoverable = 2,

    /// <summary>
    /// "Logging in to the server" — EQ is mid-handshake. Don't dismiss with
    /// blind Enter; fall through to existing gameState gate.
    /// </summary>
    Success = 3,
}
