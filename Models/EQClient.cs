// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

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

    /// <summary>
    /// The Character or Account name this client was launched for by EQSwitch
    /// autologin (e.g. "backup" for a slot bound to team1Account2="backup").
    /// Empty for externally-launched clients.
    ///
    /// Authoritative {CHAR} source — more reliable than positional
    /// LegacyAccounts[clientIndex] indexing, which maps to the raw accounts
    /// list order rather than the team slot that produced this client.
    /// Populated by TrayManager on ClientDiscovered via AutoLoginManager.TryGetBoundName.
    /// </summary>
    public string BoundCharacterName { get; set; } = "";

    public EQClient(int processId, IntPtr windowHandle, int slotIndex)
    {
        ProcessId = processId;
        WindowHandle = windowHandle;
        SlotIndex = slotIndex;
    }

    /// <summary>
    /// v3.22.68: best-effort character name for UI surfaces (Process Manager grid,
    /// tray Clients submenu, etc.).
    /// v3.22.69: tightened parse — the prefix check alone admits titles like
    /// "EverQuest - Bob - Test Server [GM]" or "EverQuest - Loading - Please Wait",
    /// which T2 verifiers (Sonnet+Opus convergent) flagged as leaking server-tag
    /// suffix or load-screen text into the menu and column. Now also validates
    /// the extracted name against the EQ character-name charset (alphanumeric,
    /// ≤15 chars per server rules) — anything else falls through to the
    /// BoundCharacterName tier.
    ///
    /// Resolution order:
    ///   1. Parse "EverQuest - {CharName}" out of OriginalTitle AND require the
    ///      extracted name to look like a real EQ character (letters/digits,
    ///      no spaces/dashes/brackets, ≤15 chars).
    ///   2. Fall back to BoundCharacterName — the slot the autologin manager
    ///      committed to (populated on ClientDiscovered). Useful pre-charselect
    ///      when EQ's title is still the bare "EverQuest".
    ///   3. Last resort: "Client {N}" placeholder so the column is never empty.
    /// </summary>
    public string DisplayName
    {
        get
        {
            const string prefix = "EverQuest - ";
            var src = !string.IsNullOrEmpty(OriginalTitle) ? OriginalTitle : WindowTitle;
            if (!string.IsNullOrEmpty(src) && src.StartsWith(prefix, StringComparison.Ordinal) && src.Length > prefix.Length)
            {
                var name = src.Substring(prefix.Length).Trim();
                if (IsValidEqCharName(name)) return name;
            }
            if (!string.IsNullOrEmpty(BoundCharacterName)) return BoundCharacterName;
            return $"Client {SlotIndex + 1}";
        }
    }

    /// <summary>
    /// v3.22.69: validates that <paramref name="name"/> matches EQ's character-name
    /// charset. Server rules: 4-15 chars, ASCII letters and digits only. We
    /// accept down to 1 char (some test names are shorter than 4) but enforce
    /// the 15-char upper bound and reject anything containing whitespace,
    /// dashes, brackets, or punctuation — those mark server tags, GM markers,
    /// or load-screen suffixes appended to the window title, not the actual name.
    ///
    /// v3.22.69 follow-up (T2 Sonnet+Opus / T3 Sonnet convergent): explicit
    /// ASCII range check instead of <c>char.IsLetterOrDigit</c>. The framework
    /// helper returns true for Unicode letters (accented, CJK, etc.), so titles
    /// like <c>"EverQuest - Ñoël"</c> would have leaked through. EQ's server-side
    /// character creation only allows ASCII; tightening the filter here keeps
    /// the UI consistent with the actual server rules.
    /// </summary>
    private static bool IsValidEqCharName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 15) return false;
        foreach (var ch in name)
        {
            bool isAsciiAlnum = (ch >= 'A' && ch <= 'Z')
                             || (ch >= 'a' && ch <= 'z')
                             || (ch >= '0' && ch <= '9');
            if (!isAsciiAlnum) return false;
        }
        return true;
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
