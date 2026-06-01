// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Phase 5a modal for editing CharacterHotkeys. One row per _config.Characters entry
/// (live), plus any stale CharacterHotkeys whose TargetName no longer resolves
/// (red row with Rebind dropdown). Orphan Characters (empty AccountUsername) are
/// flagged with a "(no account)" suffix in the Rebind dropdown — still bindable
/// but won't successfully launch until re-linked.
/// </summary>
public sealed class CharacterHotkeysDialog : Form
{
    // Remembers last-open location across opens within a session. Static so all
    // instances share it; falls back to CenterParent on first open. Process
    // lifetime only — cross-session persistence would need config storage.
    private static Point? _lastLocation;

    private readonly IReadOnlyList<Character> _characters;
    private readonly List<(string TargetName, TextBox HotkeyBox)> _liveRows = new();
    private readonly List<(string TargetName, TextBox HotkeyBox, ComboBox RebindCombo)> _staleRows = new();
    private readonly IReadOnlyList<(string label, string combo)> _otherHotkeys;
    // Shared Consolas font — see AccountHotkeysDialog for GDI rationale.
    private readonly Font _hotkeyFont = new("Consolas", 9f);

    /// <summary>Result of the dialog. Null until DialogResult.OK.</summary>
    public List<HotkeyBinding>? Result { get; private set; }

    public CharacterHotkeysDialog(
        IReadOnlyList<Character> characters,
        IReadOnlyList<HotkeyBinding> currentBindings,
        IReadOnlyList<(string label, string combo)> otherHotkeys)
    {
        _characters = characters;
        _otherHotkeys = otherHotkeys;

        // Half-populated entries are malformed — log + drop. See AccountHotkeysDialog for rationale.
        var byTargetName = new Dictionary<string, string>(StringComparer.Ordinal);
        var staleBindings = new List<HotkeyBinding>();
        foreach (var b in currentBindings)
        {
            bool halfPopulated =
                (!string.IsNullOrEmpty(b.Combo) && string.IsNullOrEmpty(b.TargetName)) ||
                (string.IsNullOrEmpty(b.Combo) && !string.IsNullOrEmpty(b.TargetName));
            if (halfPopulated)
            {
                EQSwitch.Core.FileLogger.Warn(
                    $"CharacterHotkeysDialog: malformed binding dropped (Combo='{b.Combo}', TargetName='{b.TargetName}')");
                continue;
            }
            if (!HotkeyBindingUtil.IsPopulated(b)) continue;
            bool resolves = characters.Any(c => c.Name.Equals(b.TargetName, StringComparison.OrdinalIgnoreCase));
            if (resolves) byTargetName[b.TargetName] = b.Combo;
            else staleBindings.Add(b);
        }

        // Tight layout matching TeamHotkeysDialog: card ends ~10px below the
        // hint (was ~56px of dead space). Empty-state hint is 40px tall so
        // floor card height at 82.
        // +24px overhead reserves a row at the top of the card for the
        // "Will load into game" intent hint (only shown in the populated
        // branch — the empty-state branch has its own message).
        //
        // Width is stale-aware. Stale (deleted-character) rows need a 3rd column
        // — the rebind dropdown — so they keep the legacy wide 460px layout.
        // The common case (live rows only) is compact: a 305px form with a 150px
        // hotkey box (wide enough for "Ctrl+Alt+Shift+F12"), right-aligned 10px
        // from the card edge — identical to AccountHotkeysDialog. Long names are
        // ellipsized in AddLiveRow so they can't overrun the box; orphan rows
        // append " (no account)", which is also subject to that ellipsis.
        bool hasStale  = staleBindings.Count > 0;
        int  formWidth = hasStale ? 460 : 305;
        int  cardWidth = formWidth - 30;                 // 10 left inset + 20 right gap
        int  rowPitch  = 30;             // a touch airier between rows than 28
        int  boxWidth  = hasStale ? 160 : 150;           // fits "Ctrl+Alt+Shift+F12" centered
        int  boxX      = hasStale ? 240 : (cardWidth - 10 - boxWidth);  // right-aligned 10px from card edge

        int rowCount = characters.Count + staleBindings.Count;
        bool showIntentHint = rowCount > 0;
        int cardHeight = Math.Max(82, 64 + rowCount * rowPitch + (showIntentHint ? 24 : 0));
        int formHeight = Math.Min(70 + cardHeight, 540);

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
        DarkTheme.StyleForm(this, "Character Hotkeys", new Size(formWidth, formHeight));

        var card = DarkTheme.MakeCard(this,
            "\uD83E\uDDD9",
            "Direct Character Hotkeys",
            DarkTheme.CardPurple,
            10, 10, cardWidth, cardHeight);

        int cy = 32;

        if (characters.Count == 0 && staleBindings.Count == 0)
        {
            var lblEmpty = DarkTheme.AddCardHint(card,
                "No characters yet — add one via Settings \u2192 Accounts \u2192 Characters first.",
                10, cy);
            lblEmpty.AutoSize = false;   // allow wrap within the narrow card
            lblEmpty.Size = new Size(cardWidth - 20, 40);
        }
        else
        {
            // Intent hint: tell the user what these character-level hotkeys do.
            // Character hotkeys log in AND auto-enter-world — distinct from
            // Account hotkeys which stop at character select.
            DarkTheme.AddCardHint(card,
                "Will load into game",
                10, cy);
            cy += 24;

            foreach (var stale in staleBindings.OrderBy(b => b.TargetName, StringComparer.Ordinal))
            {
                AddStaleRow(card, 10, cy, stale);
                cy += rowPitch;
            }

            foreach (var c in characters)
            {
                byTargetName.TryGetValue(c.Name, out var currentCombo);
                AddLiveRow(card, 10, cy, c.Name, currentCombo ?? "",
                    isOrphan: string.IsNullOrEmpty(c.AccountUsername), boxX: boxX, boxWidth: boxWidth);
                cy += rowPitch;
            }

            cy += 8;
            DarkTheme.AddCardHint(card,
                "Press a combo. Backspace/Delete/Esc clears.",
                10, cy);
        }

        // Right-align Cancel to the card's right edge; Save sits 10px to its left.
        int btnCancelX = 10 + cardWidth - 80;
        int btnSaveX   = btnCancelX - 90;

        var btnSave = DarkTheme.MakePrimaryButton("Save", btnSaveX, formHeight - 44);
        btnSave.Click += OnSaveClicked;
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, btnCancelX, formHeight - 44);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
    }

    private void AddLiveRow(Panel card, int x, int y, string characterName, string currentCombo, bool isOrphan, int boxX, int boxWidth)
    {
        var displayName = isOrphan ? $"{characterName} (no account)" : characterName;
        // Ellipsize so a long or orphan-suffixed name can't overrun the box.
        var shown = DarkTheme.Ellipsize(displayName, DarkTheme.FontUI85, boxX - 8 - x);
        var lbl = DarkTheme.AddCardLabel(card, shown, x, y + 4);
        // Orphan-dim outranks the role color — signals "this won't actually launch"
        // until re-linked. Resolved characters get CardBlue to match the C-pill.
        lbl.ForeColor = isOrphan ? DarkTheme.FgDimGray : DarkTheme.CardBlue;
        // Label is AutoSize — it hugs the (possibly orphan-suffixed) name; the box
        // position (boxX, right-aligned by the caller) defines the two-column gap.
        var tb = MakeHotkeyBox(card, boxX, y + 1, boxWidth, currentCombo);
        _liveRows.Add((characterName, tb));
    }

    // Stale-row dropdown preserves a display→actualName mapping so orphan Character
    // names that literally end in " (no account)" (improbable but possible) aren't
    // miscorrected by suffix stripping.
    private readonly List<Dictionary<string, string>> _staleRebindMaps = new();

    private void AddStaleRow(Panel card, int x, int y, HotkeyBinding stale)
    {
        var lbl = DarkTheme.AddCardLabel(card,
            $"\u26A0  {stale.TargetName}  (deleted)",
            x, y + 4);
        lbl.ForeColor = DarkTheme.CardWarn;
        lbl.Size = new Size(180, 20);

        var tb = MakeHotkeyBox(card, x + 190, y + 1, 90, stale.Combo);

        const string noneLabel = "(none \u2014 clear binding)";
        var rebindItems = new List<string> { noneLabel };
        var displayToActual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in _characters)
        {
            var display = string.IsNullOrEmpty(c.AccountUsername) ? $"{c.Name} (no account)" : c.Name;
            rebindItems.Add(display);
            displayToActual[display] = c.Name;
        }
        var cboRebind = DarkTheme.AddCardComboBox(card, x + 285, y + 1, 130, rebindItems.ToArray());
        cboRebind.DropDownStyle = ComboBoxStyle.DropDownList;
        cboRebind.SelectedIndex = 0;

        _staleRows.Add((stale.TargetName, tb, cboRebind));
        _staleRebindMaps.Add(displayToActual);
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
            Font = _hotkeyFont,   // shared — disposed in Dispose override
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
    /// and closing the dialog. Identified by ShortcutsEnabled=false, the
    /// distinguishing flag set in MakeHotkeyBox.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && ActiveControl is TextBox tb && !tb.ShortcutsEnabled)
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
        var newBindings = new List<HotkeyBinding>();

        foreach (var (targetName, tb) in _liveRows)
        {
            var combo = tb.Text.Trim();
            if (!string.IsNullOrEmpty(combo))
                newBindings.Add(new HotkeyBinding { Combo = combo, TargetName = targetName });
        }

        for (int i = 0; i < _staleRows.Count; i++)
        {
            var (_, tb, cboRebind) = _staleRows[i];
            var combo = tb.Text.Trim();
            if (string.IsNullOrEmpty(combo)) continue;
            if (cboRebind.SelectedIndex <= 0) continue;
            // Dictionary lookup instead of suffix-strip — robust against Character
            // names that literally end in " (no account)".
            var picked = cboRebind.SelectedItem?.ToString() ?? "";
            if (!_staleRebindMaps[i].TryGetValue(picked, out var targetName) || string.IsNullOrEmpty(targetName))
                continue;
            newBindings.Add(new HotkeyBinding { Combo = combo, TargetName = targetName });
        }

        var within = newBindings.Where(b => !string.IsNullOrEmpty(b.Combo))
            .GroupBy(b => b.Combo, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        if (within.Any())
        {
            var lines = within.Select(g =>
                $"  {g.Key}  \u2192  {string.Join(", ", g.Select(b => $"Character '{b.TargetName}'"))}");
            MessageBox.Show(
                "Cannot save — multiple Characters bound to the same key:\n\n" + string.Join("\n", lines),
                "Hotkey Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var externalConflicts = new List<string>();
        var inUse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, combo) in _otherHotkeys)
            if (!string.IsNullOrEmpty(combo)) inUse[combo] = label;

        foreach (var b in newBindings)
        {
            if (inUse.TryGetValue(b.Combo, out var otherLabel))
                externalConflicts.Add($"  {b.Combo}  \u2192  Character '{b.TargetName}' vs {otherLabel}");
        }
        if (externalConflicts.Any())
        {
            MessageBox.Show(
                "Cannot save — a combo is already bound to another action:\n\n" + string.Join("\n", externalConflicts),
                "Hotkey Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = newBindings;
        DialogResult = DialogResult.OK;
        Close();
    }
}
