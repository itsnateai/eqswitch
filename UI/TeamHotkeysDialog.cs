// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

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
        // destination is per-slot now, dictated by kind). Label column 200
        // covers most pairs; AutoEllipsis truncates worst case. Form 400.
        const int formW = 400;
        const int formH = 250;
        const int cardW = 370;
        const int cardH = 180;

        // Restore last-open position if available; otherwise center on parent.
        if (_lastLocation.HasValue)
        {
            StartPosition = FormStartPosition.Manual;
            Location = _lastLocation.Value;
        }
        else
        {
            StartPosition = FormStartPosition.CenterParent;
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
            // Character=CardBlue, Account=CardPurple, unresolved=default. Matches
            // the A/C pill colors in the Accounts team-configure window.
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
                for (int s = 0; s < slots.Count; s++)
                {
                    var (name, isCharacter) = slots[s];
                    var nameLbl = DarkTheme.AddCardLabel(card, name, xCursor, cy + 4);
                    nameLbl.ForeColor = isCharacter switch
                    {
                        true  => DarkTheme.CardBlue,
                        false => DarkTheme.CardPurple,
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

            _boxes[i] = MakeHotkeyBox(card, 220, cy + 1, 140, initial[i] ?? "");
            cy += 30;
        }

        DarkTheme.AddCardHint(card,
            "Press a combo to capture. Backspace, Delete, or Escape clears.",
            10, cy + 4);

        // Buttons: Cancel right edge (~370) lands ~10px inside card right edge (380).
        var btnSave = DarkTheme.MakePrimaryButton("Save", 200, formH - 44);
        btnSave.Click += OnSaveClicked;
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 290, formH - 44);
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

        string keyName = e.KeyCode switch
        {
            Keys.OemPipe or Keys.OemBackslash => "\\",
            Keys.OemCloseBrackets => "]",
            Keys.OemOpenBrackets => "[",
            _ => e.KeyCode.ToString()
        };
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
