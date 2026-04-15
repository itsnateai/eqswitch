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
            bool resolves = characters.Any(c => c.Name.Equals(b.TargetName, StringComparison.Ordinal));
            if (resolves) byTargetName[b.TargetName] = b.Combo;
            else staleBindings.Add(b);
        }

        int rowCount = characters.Count + staleBindings.Count;
        int bodyHeight = 80 + rowCount * 30;
        int formHeight = Math.Min(140 + bodyHeight, 540);

        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.StyleForm(this, "Character Hotkeys", new Size(460, formHeight));

        var card = DarkTheme.MakeCard(this,
            "\uD83E\uDDD9",
            "Direct Character Hotkeys",
            DarkTheme.CardPurple,
            10, 10, 430, bodyHeight + 30);

        int cy = 32;

        if (characters.Count == 0 && staleBindings.Count == 0)
        {
            var lblEmpty = DarkTheme.AddCardHint(card,
                "No characters yet — add one via Settings \u2192 Accounts \u2192 Characters first.",
                10, cy);
            lblEmpty.Size = new Size(410, 40);
        }
        else
        {
            foreach (var stale in staleBindings.OrderBy(b => b.TargetName, StringComparer.Ordinal))
            {
                AddStaleRow(card, 10, cy, stale);
                cy += 30;
            }

            foreach (var c in characters)
            {
                byTargetName.TryGetValue(c.Name, out var currentCombo);
                AddLiveRow(card, 10, cy, c.Name, currentCombo ?? "",
                    isOrphan: string.IsNullOrEmpty(c.AccountUsername));
                cy += 30;
            }

            cy += 8;
            DarkTheme.AddCardHint(card,
                "Press combo to capture. Backspace, Delete, or Escape clear. \u26A0 red = deleted target; orphan = no account linked.",
                10, cy);
        }

        var btnSave = DarkTheme.MakePrimaryButton("Save", 250, formHeight - 44);
        btnSave.Click += OnSaveClicked;
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 350, formHeight - 44);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
    }

    private void AddLiveRow(Panel card, int x, int y, string characterName, string currentCombo, bool isOrphan)
    {
        var displayName = isOrphan ? $"{characterName} (no account)" : characterName;
        var lbl = DarkTheme.AddCardLabel(card, displayName, x, y + 4);
        if (isOrphan) lbl.ForeColor = DarkTheme.FgDimGray;
        lbl.Size = new Size(220, 20);

        var tb = MakeHotkeyBox(card, x + 230, y + 1, 160, currentCombo);
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
