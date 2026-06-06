// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using EQSwitch.Config;

namespace EQSwitch.UI;

/// <summary>
/// One eqclient.ini control bound to its <see cref="IniSetting"/> descriptor, plus the canonical
/// value loaded for it. The snapshot (<see cref="LoadedValue"/>) is what makes Save write ONLY the
/// keys the user actually changed (touch-gating). Public fields: the DEBUG save-roundtrip tests
/// reach Setting/Check/Numeric by reflection.
/// </summary>
internal sealed class EqClientBinding
{
    public IniSetting Setting;
    public CheckBox? Check;
    public NumericUpDown? Numeric;
    public string LoadedValue = "";
    public EqClientBinding(IniSetting setting, CheckBox? chk, NumericUpDown? nud)
    { Setting = setting; Check = chk; Numeric = nud; }
}

/// <summary>
/// The shared read/save engine every EQ Client Settings window binds onto (spec §3: the generic
/// LoadInto / SaveChanged loops). ONE implementation, so the 6 forms can't drift in how they read,
/// snapshot, or touch-gate — the whole point of the overhaul. The launch writer
/// (EnforceOverrides) stays in EQClientSettingsForm: it needs live monitor geometry, so it isn't a
/// per-binding loop.
/// </summary>
internal static class EqClientBindings
{
    /// <summary>Current control state expressed as its canonical INI string.</summary>
    public static string ReadControl(EqClientBinding b) =>
        b.Check != null ? b.Setting.ToggleToIni(b.Check.Checked) : b.Setting.NumberToIni(b.Numeric!.Value);

    /// <summary>Set the control from an INI string via the descriptor's conversion (clamped to the control's range).</summary>
    public static void ApplyControl(EqClientBinding b, string iniValue)
    {
        if (b.Check != null)
            b.Check.Checked = b.Setting.ToggleFromIni(iniValue);
        else
            b.Numeric!.Value = Math.Clamp(b.Setting.ParseNumber(iniValue), b.Numeric.Minimum, b.Numeric.Maximum);
    }

    /// <summary>
    /// LIVE display read: every control reflects the actual on-disk value (so in-game/eqgame changes
    /// always show); a key absent from the INI falls back to the descriptor's Default. Snapshots each
    /// loaded value per binding so Save writes ONLY what the user changes (touch-gating). An absent
    /// file yields an empty document → every control shows its descriptor Default.
    /// </summary>
    public static void LoadInto(IEnumerable<EqClientBinding> bindings, string iniPath)
    {
        var doc = EqClientIniDocument.Load(iniPath);
        foreach (var b in bindings)
        {
            string value = doc.Get(b.Setting) ?? b.Setting.Default;
            ApplyControl(b, value);
            b.LoadedValue = ReadControl(b);
        }
    }

    /// <summary>
    /// Touch-gated, schema-driven, surgical write: only keys the user changed (vs the load snapshot)
    /// are written, each to its ONE canonical section (+ any mirror sections) via the engine.
    /// Untouched keys — and any key EQSwitch doesn't manage — are left exactly as eqgame/the user
    /// left them (point D: no clobber; point I: no ghosts). Numeric sentinels (e.g. SoundVolume -1,
    /// ClipPlane 0) mean "don't set" and are never written. Returns the number of keys written; the
    /// file is only rewritten when at least one key changed.
    /// </summary>
    public static int SaveChanged(IEnumerable<EqClientBinding> bindings, string iniPath)
    {
        var doc = EqClientIniDocument.Load(iniPath);
        int changed = 0;
        foreach (var b in bindings)
        {
            if (b.Numeric != null && !b.Setting.ShouldWriteNumber(b.Numeric.Value))
                continue;                                 // sentinel "don't set"
            string current = ReadControl(b);
            if (current == b.LoadedValue) continue;       // untouched — leave it alone
            doc.Write(b.Setting, current);                // canonical section (+ any mirror sections)
            b.LoadedValue = current;                      // refresh snapshot so Apply doesn't re-write
            changed++;
        }
        if (changed > 0) doc.Save(iniPath);
        return changed;
    }
}
