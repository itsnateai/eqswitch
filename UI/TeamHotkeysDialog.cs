// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace EQSwitch.UI;

/// <summary>
/// Modal for editing Team 1-4 login hotkeys. Mirrors the Account/Character
/// hotkey dialog pattern: opened from the Hotkeys-tab Direct Bindings card,
/// returns four trimmed combo strings (Team1..Team4). Conflict checks run on
/// Save against (a) the four entries themselves and (b) any other hotkeys the
/// caller passes in via <paramref name="otherHotkeys"/>.
/// </summary>
public sealed class TeamHotkeysDialog : Form
{
    // Remembers last-open location across opens within a session. Static so all
    // instances share it; falls back to CenterParent on first open. Process
    // lifetime only — cross-session persistence would need config storage.
    private static Point? _lastLocation;

    private readonly TextBox[] _boxes = new TextBox[4];
    private readonly IReadOnlyList<(string label, string combo)> _otherHotkeys;
    // Shared font — per-control Font leaks GDI handles when WinForms disposes
    // the TextBox (cf. AccountHotkeysDialog's hotkey-font note).
    private readonly Font _hotkeyFont = new("Consolas", 9f);

    /// <summary>Result is (Team1, Team2, Team3, Team4) trimmed combo strings. Null until DialogResult.OK.</summary>
    public (string Team1, string Team2, string Team3, string Team4)? Result { get; private set; }

    public TeamHotkeysDialog(
        string team1, string team2, string team3, string team4,
        IReadOnlyList<IReadOnlyList<(string Name, bool? IsCharacter)>> teamPreviews,
        IReadOnlyList<(string label, string combo)> otherHotkeys)
    {
        _otherHotkeys = otherHotkeys;

        // Row label is "Team N — name1 / name2" (no destination suffix —
        // destination is per-slot now, dictated by kind). The compound name
        // chain needs the widest label column in the family, so Team is the
        // 470px member. It shares the family's 150px box (wide enough for
        // "Ctrl+Alt+Shift+F12") and row pitch; the box is right-aligned 10px
        // from the card edge. Long name pairs are ellipsized with a greedy
        // budget — a short first name donates room to a long second one.
        const int formW = 470;
        const int formH = 252;
        const int cardW = 440;
        const int cardH = 182;
        const int boxX  = 280;    // box left edge (right-aligned: cardW - 10 - boxW); wide label column for long 2nd names
        const int boxW  = 150;    // wide enough for "Ctrl+Alt+Shift+F12"

        // Restore last-open position if available; otherwise center on parent.
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
        DarkTheme.StyleForm(this, "Team Hotkeys", new Size(formW, formH));

        var card = DarkTheme.MakeCard(this, "👥", "Team Login Hotkeys",
            DarkTheme.CardGold, 10, 10, cardW, cardH);

        int cy = 32;
        var initial = new[] { team1, team2, team3, team4 };
        for (int i = 0; i < 4; i++)
        {
            // Show "Team N — slot1 / slot2" with each slot color-coded by kind:
            // Character=blue, Account=orange, unresolved=gray. Matches the A/C
            // pill colors in the Accounts team-configure window.
            // No destination suffix — destination is per-slot, dictated by kind:
            // Character → enters world, Account → charselect (handled at FireTeam).
            var slots = i < teamPreviews.Count ? teamPreviews[i] : Array.Empty<(string, bool?)>();
            var prefix = DarkTheme.AddCardLabel(card, $"Team {i + 1} — ", 10, cy + 4);

            if (slots.Count == 0)
            {
                var emptyLbl = DarkTheme.AddCardLabel(card, "(empty)", prefix.Right, cy + 4);
                emptyLbl.ForeColor = DarkTheme.FgGray;
            }
            else
            {
                int xCursor = prefix.Right;
                // Names render as separate color-coded labels laid end-to-end, so a
                // long pair can overrun the hotkey box. Measure the whole chain; if
                // it would reach the box, ellipsize each name to an even share of
                // the room before the box. Short pairs stay un-clipped.
                var font = DarkTheme.FontUI85;
                int sepW = DarkTheme.MeasureText(" / ", font);
                int nameRoom = (boxX - 8) - xCursor - sepW * (slots.Count - 1);
                int totalW = slots.Sum(sl => DarkTheme.MeasureText(sl.Name, font));
                // Per-name display budgets. If the whole chain fits, nothing clips.
                // Otherwise each name gets a fair share, but names that fit under
                // their share donate the slack to longer ones — so a short first
                // name (e.g. "Natedogg") lets a long second name show more chars
                // instead of both truncating to the same width.
                var budget = new int[slots.Count];
                if (totalW <= nameRoom)
                {
                    for (int b = 0; b < budget.Length; b++) budget[b] = int.MaxValue;
                }
                else
                {
                    int fair = Math.Max(0, nameRoom / slots.Count);
                    int slack = 0, over = 0;
                    foreach (var sl in slots)
                    {
                        int w = DarkTheme.MeasureText(sl.Name, font);
                        if (w <= fair) slack += fair - w; else over++;
                    }
                    int bonus = over > 0 ? slack / over : 0;
                    for (int b = 0; b < slots.Count; b++)
                    {
                        int w = DarkTheme.MeasureText(slots[b].Name, font);
                        budget[b] = w <= fair ? int.MaxValue : fair + bonus;
                    }
                }
                for (int s = 0; s < slots.Count; s++)
                {
                    var (name, isCharacter) = slots[s];
                    var nameLbl = DarkTheme.AddCardLabel(card, DarkTheme.Ellipsize(name, font, budget[s]), xCursor, cy + 4);
                    nameLbl.ForeColor = isCharacter switch
                    {
                        true  => DarkTheme.FgCharacterBlue,
                        false => DarkTheme.FgAccountOrange,
                        null  => DarkTheme.FgGray,
                    };
                    xCursor = nameLbl.Right;
                    if (s < slots.Count - 1)
                    {
                        var sep = DarkTheme.AddCardLabel(card, " / ", xCursor, cy + 4);
                        xCursor = sep.Right;
                    }
                }
            }

            _boxes[i] = MakeHotkeyBox(card, boxX, cy + 1, boxW, initial[i] ?? "");
            cy += 30;
        }

        DarkTheme.AddCardHint(card,
            "Press a combo to capture. Backspace, Delete, or Escape clears.",
            10, cy + 4);

        // Buttons: Cancel right edge aligns to the card's right edge (450); Save sits 10px to its left.
        var btnSave = DarkTheme.MakePrimaryButton("Save", 280, formH - 44);
        btnSave.Click += OnSaveClicked;
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 370, formH - 44);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
    }

    private TextBox MakeHotkeyBox(Panel card, int x, int y, int width, string initialText)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 20),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.None,
            Font = _hotkeyFont,
            TextAlign = HorizontalAlignment.Center,
            ShortcutsEnabled = false,
            PlaceholderText = "press keys…",   // affordance on empty boxes; clears on capture
            Text = initialText,
        };
        tb.KeyDown += HotkeyBoxKeyDown;
        card.Controls.Add(tb);
        return tb;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hotkeyFont.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>
    /// Intercept Escape so a focused hotkey TextBox clears its value
    /// (matching the on-screen hint) instead of triggering CancelButton
    /// and closing the dialog. With no hotkey box focused, fall through
    /// to default handling so Escape still cancels.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && ActiveControl is TextBox tb && _boxes.Contains(tb))
        {
            tb.Text = "";
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static void HotkeyBoxKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;

        if (e.KeyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey)
            return;

        if (e.KeyCode is Keys.Delete or Keys.Back or Keys.Escape && !e.Control && !e.Alt && !e.Shift)
        {
            if (sender is TextBox tb) tb.Text = "";
            return;
        }

        if (!e.Control && !e.Alt && !e.Shift) return;

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");

        // Shared with SettingsForm so number-row (Alt+1..) and tilde combos round-trip through
        // HotkeyManager.ResolveVK. A raw e.KeyCode.ToString() emits "D1"/"Oemtilde", which the
        // resolver can't parse — the hotkey would register as VK 0 and silently never fire.
        // This dialog stays modifier-only (the no-modifier gate above is unchanged).
        string keyName = SettingsForm.FormatHotkeyKeyName(e.KeyCode);
        parts.Add(keyName);
        if (sender is TextBox box) box.Text = string.Join("+", parts);
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        var combos = _boxes.Select(b => b.Text.Trim()).ToArray();

        // Same-combo conflict scan within the four entries.
        var labels = new[] { "Team 1", "Team 2", "Team 3", "Team 4" };
        var withinDup = combos
            .Select((c, i) => (Combo: c, Label: labels[i]))
            .Where(t => !string.IsNullOrEmpty(t.Combo))
            .GroupBy(t => t.Combo, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        if (withinDup.Any())
        {
            var lines = withinDup.Select(g =>
                $"  {g.Key}  →  {string.Join(", ", g.Select(t => t.Label))}");
            MessageBox.Show(
                "Cannot save — multiple Teams bound to the same key:\n\n" + string.Join("\n", lines),
                "Hotkey Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Conflicts with hotkeys outside this dialog (other tab + Account/Character bindings).
        var inUse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, combo) in _otherHotkeys)
            if (!string.IsNullOrEmpty(combo)) inUse[combo] = label;

        var external = new List<string>();
        for (int i = 0; i < combos.Length; i++)
        {
            if (string.IsNullOrEmpty(combos[i])) continue;
            if (inUse.TryGetValue(combos[i], out var otherLabel))
                external.Add($"  {combos[i]}  →  {labels[i]} vs {otherLabel}");
        }
        if (external.Any())
        {
            MessageBox.Show(
                "Cannot save — a combo is already bound to another action:\n\n" + string.Join("\n", external),
                "Hotkey Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Modifier-required check (matches the inline rule from CheckAutoLoginHotkeyConflicts).
        var noMod = combos.Select((c, i) => (Combo: c, Label: labels[i]))
            .Where(t => !string.IsNullOrEmpty(t.Combo) && !t.Combo.Contains('+'))
            .ToList();
        if (noMod.Any())
        {
            var lines = noMod.Select(t => $"  {t.Label}: '{t.Combo}' needs a modifier (Ctrl/Alt/Shift)");
            MessageBox.Show(
                "Cannot save — bare keys are not valid hotkeys:\n\n" + string.Join("\n", lines),
                "Hotkey Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = (combos[0], combos[1], combos[2], combos[3]);
        DialogResult = DialogResult.OK;
        Close();
    }
}
