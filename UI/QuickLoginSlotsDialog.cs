// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Dialog for assigning the four Quick Login slots (v3.23.0). Each slot maps a tray-click
/// "AutoLogin 1-4" action (and its hotkey) to one Account or Character. Pick a 🧙 Character
/// (enters world) or a 🔑 Account (stops at char-select). Accounts render in orange — EQ's
/// classic /ooc chat color, matching the Autologin Teams summary convention.
///
/// Modeled on <see cref="AutoLoginTeamsDialog"/> (same SlotOption / BuildComboItems / staging
/// pattern) but single-slot and typed: the stored value is <c>char:&lt;Name&gt;</c> /
/// <c>acct:&lt;Name&gt;</c> (see <see cref="QuickLoginSlot"/>) rather than a bare name, so the
/// account-vs-character intent survives into dispatch.
///
/// Unlike the Teams dialog this owner-draws its combos to color Account entries orange. The
/// pre-Win11 dropdown-render regression that the Teams dialog guards against (v3.22.69) was
/// caused by <c>WS_EX_COMPOSITED</c> on the form, not by per-item DrawItem — which is not used
/// here. Only four combos, no composited form style: the orange text is safe.
/// </summary>
internal sealed class QuickLoginSlotsDialog : EqSwitchForm
{
    private const int SlotCount = 4;

    // Process-lifetime memory of last-open position, mirroring AutoLoginTeamsDialog.
    private static Point? _lastLocation;

    private sealed class SlotOption
    {
        public string Value { get; }   // typed: "char:Name" / "acct:Name" / "" for (none)
        public string Display { get; }
        public QuickLoginSlot.Kind Kind { get; }
        public bool IsSeparator { get; }
        public SlotOption(string value, string display, QuickLoginSlot.Kind kind, bool isSeparator = false)
        {
            Value = value; Display = display; Kind = kind; IsSeparator = isSeparator;
        }
        public override string ToString() => Display;
    }

    private readonly IReadOnlyList<Account> _accounts;
    private readonly IReadOnlyList<Character> _characters;
    private readonly ComboBox[] _cbo = new ComboBox[SlotCount];
    private object[] _comboItemsArray = null!;
    private Label _lblDupWarn = null!;

    public string Slot1 => GetValue(_cbo[0]);
    public string Slot2 => GetValue(_cbo[1]);
    public string Slot3 => GetValue(_cbo[2]);
    public string Slot4 => GetValue(_cbo[3]);

    public QuickLoginSlotsDialog(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Character> characters,
        string slot1, string slot2, string slot3, string slot4)
    {
        _accounts = accounts;
        _characters = characters;

        SuspendLayout();
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
        UpdateStyles();

        if (_lastLocation.HasValue)
        {
            StartPosition = FormStartPosition.Manual;
            Location = _lastLocation.Value;
        }
        else
        {
            StartPosition = FormStartPosition.Manual;
            DarkTheme.CenterOnOwnerOnLoad(this);
        }
        FormClosing += (_, _) => _lastLocation = Location;
        DarkTheme.StyleForm(this, "Quick Login Slots", new Size(430, 320));
        MinimizeBox = false;

        _comboItemsArray = BuildComboItems().Cast<object>().ToArray();

        const int L = 20, I = 110;
        // Measure-to-fit combo width: widest item label + dropdown arrow & padding, clamped
        // so short names don't leave the box looking over-wide and the line separator stays
        // proportional.
        int widestItem = 0;
        foreach (var it in _comboItemsArray)
            widestItem = Math.Max(widestItem, TextRenderer.MeasureText(((SlotOption)it).Display, Font).Width);
        int comboW = Math.Clamp(widestItem + 44, 180, 280);
        int y = 18;

        var intro = DarkTheme.AddLabel(this,
            "Assign which Account or Character each\nAuto-Login Tray Click Actions clicks fires", L, y);
        intro.ForeColor = DarkTheme.FgDimGray;
        intro.Font = DarkTheme.FontUI75;
        intro.AutoSize = true;
        y += 42;   // two-line hint now (was 26) — keeps combo1 from overlapping line 2

        for (int i = 0; i < SlotCount; i++)
        {
            DarkTheme.AddLabel(this, $"Auto-Login{i + 1}", L, y + 3);
            _cbo[i] = MakeCombo(I, y, comboW);
            y += 38;
        }

        var legend = DarkTheme.AddLabel(this,
            "🧙 Character → enters world      🔑 Account → char-select", L, y + 2);
        legend.ForeColor = DarkTheme.FgDimGray;
        legend.Font = DarkTheme.FontUI75;
        legend.AutoSize = true;
        y += 24;

        // Non-blocking duplicate-target notice. Quick Login slots fire independently, so a
        // repeated target isn't a hard error (unlike a team that launches both at once) —
        // warn, don't block. Reserve the row so measure-to-fit height stays stable.
        _lblDupWarn = DarkTheme.AddLabel(this, "", L, y);
        _lblDupWarn.ForeColor = DarkTheme.FgWarn;
        _lblDupWarn.Font = DarkTheme.FontUI75;
        _lblDupWarn.AutoSize = true;
        y += 20;

        var btnOK = DarkTheme.MakePrimaryButton("Save", L, y);
        btnOK.Width = 100;
        btnOK.Click += (_, _) =>
        {
            // Non-modal Show() (mirrors Teams dialog) needs explicit result + Close.
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(btnOK);

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, L + 115, y);
        btnCancel.Width = 100;
        btnCancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(btnCancel);

        // Measure-to-fit: size to the widest element (combo block, intro/legend text, or
        // button row) + symmetric L margins, and trim the height to the button row + 18px —
        // no dead padding on the bottom or the right.
        int introW  = intro.Text.Split('\n').Max(s => TextRenderer.MeasureText(s, DarkTheme.FontUI75).Width);
        int legendW = TextRenderer.MeasureText(legend.Text, DarkTheme.FontUI75).Width;
        int rightEdge = Math.Max(I + comboW, L + Math.Max(introW, Math.Max(legendW, 215)));
        ClientSize = new Size(rightEdge + L, btnCancel.Bottom + 18);

        var saved = new[] { slot1, slot2, slot3, slot4 };
        for (int i = 0; i < SlotCount; i++)
            SelectByValue(_cbo[i], saved[i]);
        RefreshDupWarning();

        ResumeLayout(performLayout: true);
    }

    /// <summary>
    /// (none) + every Character (🧙, enters world) + every Account (🔑, char-select).
    /// Characters and Accounts are BOTH listed even when names overlap — for Quick Login the
    /// two are distinct intents (enter-world vs charselect), unlike the Teams dialog where the
    /// Character entry is just an enter-world convenience over its backing Account.
    /// </summary>
    private List<SlotOption> BuildComboItems()
    {
        var items = new List<SlotOption>
        {
            new("", "(none)", QuickLoginSlot.Kind.Empty),
        };
        foreach (var c in _characters)
            items.Add(new SlotOption(QuickLoginSlot.ForCharacter(c.Name), $"🧙  {c.Name}", QuickLoginSlot.Kind.Character));
        // Non-selectable visual separator between the Characters group (enter world) and the
        // Accounts group (char-select), only when both groups are present. The bounce in
        // MakeCombo's SelectedIndexChanged keeps it unpickable.
        if (_characters.Count > 0 && _accounts.Count > 0)
            items.Add(new SlotOption("", "", QuickLoginSlot.Kind.Empty, isSeparator: true));
        foreach (var a in _accounts)
            items.Add(new SlotOption(QuickLoginSlot.ForAccount(a.Name), $"🔑  {a.Username}", QuickLoginSlot.Kind.Account));
        return items;
    }

    private ComboBox MakeCombo(int x, int y, int width)
    {
        var cb = new ComboBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 24),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            DrawMode = DrawMode.OwnerDrawFixed,
        };
        cb.Items.AddRange(_comboItemsArray);
        cb.SelectedIndex = 0;
        cb.DrawItem += DrawComboItem;
        // Bounce off the non-selectable separator back to the last real selection.
        int lastIndex = 0;
        cb.SelectedIndexChanged += (s, _) =>
        {
            var box = (ComboBox)s!;
            if (box.SelectedItem is SlotOption o && o.IsSeparator)
            {
                box.SelectedIndex = lastIndex;
            }
            else
            {
                lastIndex = box.SelectedIndex;
                // A real user selection supersedes any stale-target stashed on Tag by
                // SelectByValue — clear it so GetValue returns the new pick (incl. (none)),
                // not the deleted/renamed original.
                box.Tag = null;
                RefreshDupWarning();
            }
        };
        cb.MouseWheel += (_, e) => ((HandledMouseEventArgs)e).Handled = true;
        Controls.Add(cb);
        // ItemHeight is an int — AutoScaleMode.Dpi does not scale it. Derive from the live
        // font at this control's DeviceDpi (resolves only after parenting).
        cb.ItemHeight = (int)Math.Ceiling(cb.Font.GetHeight(cb.DeviceDpi)) + cb.LogicalToDeviceUnits(4);
        return cb;
    }

    /// <summary>
    /// Owner-paint: Account entries orange (DarkTheme.FgAccountOrange — EQ /ooc orange),
    /// Character entries blue (FgCharacterBlue), (none) dimmed. Paints both the closed
    /// selection box and the dropdown rows; the system highlight (DrawBackground) reads fine
    /// under either color.
    /// </summary>
    private static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox cb) return;
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= cb.Items.Count)
        {
            e.DrawFocusRectangle();
            return;
        }
        var opt = cb.Items[e.Index] as SlotOption;
        if (opt?.IsSeparator == true)
        {
            int midY = e.Bounds.Top + e.Bounds.Height / 2;
            int inset = cb.LogicalToDeviceUnits(6);   // owner-draw inset — not auto-scaled
            using var pen = new Pen(DarkTheme.FgDimGray);
            e.Graphics.DrawLine(pen, e.Bounds.Left + inset, midY, e.Bounds.Right - inset, midY);
            return; // no text, no focus rectangle on a separator
        }
        // Selected dropdown row gets the system-highlight background; draw white
        // there so the blue Character color isn't blue-on-blue. Identity color
        // otherwise (incl. the closed box, which is ComboBoxEdit, not Selected).
        Color fg;
        if ((e.State & DrawItemState.Selected) != 0)
        {
            fg = DarkTheme.FgWhite;
        }
        else
        {
            fg = opt?.Kind switch
            {
                QuickLoginSlot.Kind.Account => DarkTheme.FgAccountOrange,
                QuickLoginSlot.Kind.Character => DarkTheme.FgCharacterBlue,
                _ => DarkTheme.FgDimGray,
            };
        }
        TextRenderer.DrawText(e.Graphics, opt?.Display ?? "", e.Font ?? cb.Font, e.Bounds, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        e.DrawFocusRectangle();
    }

    private void SelectByValue(ComboBox cbo, string value)
    {
        if (string.IsNullOrEmpty(value)) { cbo.SelectedIndex = 0; return; }

        // Exact typed match first (the v3.23 storage format).
        for (int i = 0; i < cbo.Items.Count; i++)
        {
            if (cbo.Items[i] is SlotOption opt &&
                opt.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                cbo.SelectedIndex = i;
                return;
            }
        }

        // Legacy bare name (pre-v3.23 config): match the un-prefixed name against either
        // a Character or Account option. Character wins ties — same enter-world-first
        // preference the legacy resolver applies.
        if (QuickLoginSlot.Parse(value).Kind == QuickLoginSlot.Kind.LegacyBare)
        {
            int charMatch = -1, acctMatch = -1;
            for (int i = 0; i < cbo.Items.Count; i++)
            {
                if (cbo.Items[i] is not SlotOption opt || opt.Kind == QuickLoginSlot.Kind.Empty) continue;
                var (_, name) = QuickLoginSlot.Parse(opt.Value);
                if (!name.Equals(value, StringComparison.OrdinalIgnoreCase)) continue;
                if (opt.Kind == QuickLoginSlot.Kind.Character && charMatch < 0) charMatch = i;
                else if (opt.Kind == QuickLoginSlot.Kind.Account && acctMatch < 0) acctMatch = i;
            }
            int pick = charMatch >= 0 ? charMatch : acctMatch;
            if (pick >= 0) { cbo.SelectedIndex = pick; return; }
        }

        // Unresolved (renamed/deleted target). Stash the original on Tag so GetValue can
        // preserve it instead of silently clobbering to (none) on Save.
        cbo.SelectedIndex = 0;
        cbo.Tag = value;
    }

    private static string GetValue(ComboBox cbo) =>
        (cbo.SelectedItem as SlotOption)?.Value is { Length: > 0 } v ? v : (cbo.Tag as string) ?? "";

    /// <summary>
    /// Non-blocking warning when two slots fire the same target — firing both kicks one
    /// login on Dalaya. Exact-value match (two char:Same or two acct:Same); a char: bind
    /// plus its backing acct: bind isn't flagged (deliberate pairing, not the common slip).
    /// </summary>
    private void RefreshDupWarning()
    {
        if (_lblDupWarn == null) return;
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < SlotCount; i++)
        {
            var v = GetValue(_cbo[i]);
            if (string.IsNullOrEmpty(v)) continue;
            if (seen.TryGetValue(v, out int first))
            {
                _lblDupWarn.Text = $"⚠ Slots {first + 1} & {i + 1} target the same login.";
                return;
            }
            seen[v] = i;
        }
        _lblDupWarn.Text = "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }
}
