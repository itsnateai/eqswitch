// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EQSwitch.Config;

/// <summary>
/// Section-aware, surgical read/write over an eqclient.ini's lines, driven by <see cref="IniSetting"/>.
/// A value is read from and written to exactly the key's canonical [Section] — so a setting can never
/// be read from one section and written to another (the read-2/write-1 ghost class). Writes are
/// minimal: only the named key's line changes; every other key, comment, blank line, and the section
/// order are preserved untouched (point D — don't clobber, eqgame-wins). ANSI (Encoding.Default) —
/// EQ corrupts the file if it's rewritten as UTF-8.
///
/// The <see cref="Set"/> logic is ported verbatim from EQClientSettingsForm.SetIniValue (battle-tested);
/// the section-scoped <see cref="Get(string,string)"/> is the missing read half. Forms migrate onto
/// this in Phases 1-6; the per-form LoadFromIni parsers + the old SetIniValue retire afterward.
/// </summary>
public sealed class EqClientIniDocument
{
    private readonly List<string> _lines;

    public EqClientIniDocument(IEnumerable<string> lines) => _lines = lines.ToList();

    /// <summary>Load from disk (ANSI). Returns an empty document if the file doesn't exist.</summary>
    public static EqClientIniDocument Load(string path)
        => new(File.Exists(path) ? File.ReadAllLines(path, Encoding.Default) : Array.Empty<string>());

    /// <summary>Current lines (for assertions / inspection).</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Write to disk as ANSI (matches EQ's encoding).</summary>
    public void Save(string path) => File.WriteAllLines(path, _lines, Encoding.Default);

    /// <summary>Value of <paramref name="key"/> within <paramref name="section"/>, trimmed; null if absent.</summary>
    public string? Get(string section, string key)
    {
        string header = $"[{section}]";
        bool inSection = false;
        foreach (var raw in _lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                inSection = line.Equals(header, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            if (line.AsSpan(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Set <paramref name="key"/>=<paramref name="value"/> in <paramref name="section"/>:
    /// updates in place if present; inserts before the section's end if the section exists but the
    /// key doesn't; appends a new section if the section is absent. Identical behaviour to the
    /// original SetIniValue, so migrating forms onto it changes nothing about where keys land.
    /// </summary>
    public void Set(string section, string key, string value)
    {
        string header = $"[{section}]";
        int sectionStart = -1, sectionEnd = _lines.Count;

        for (int i = 0; i < _lines.Count; i++)
        {
            var trimmed = _lines[i].Trim();
            if (trimmed.Equals(header, StringComparison.OrdinalIgnoreCase))
                sectionStart = i;
            else if (sectionStart >= 0 && trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                sectionEnd = i;
                break;
            }
        }

        if (sectionStart >= 0)
        {
            for (int i = sectionStart + 1; i < sectionEnd; i++)
            {
                var parts = _lines[i].Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    _lines[i] = $"{key}={value}";
                    return;
                }
            }
            _lines.Insert(sectionEnd, $"{key}={value}");
        }
        else
        {
            _lines.Add("");
            _lines.Add(header);
            _lines.Add($"{key}={value}");
        }
    }

    /// <summary>Raw INI value for a setting (from its canonical section), or null if absent.</summary>
    public string? Get(IniSetting setting) => Get(setting.Section, setting.Key);

    /// <summary>Write a setting's value to its canonical section plus any write-only mirror sections.</summary>
    public void Write(IniSetting setting, string value)
    {
        Set(setting.Section, setting.Key, value);
        foreach (var mirror in setting.MirrorSections)
            Set(mirror, setting.Key, value);
    }
}
