using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Phase 5a modal for editing AccountHotkeys. One row per _config.Accounts entry
/// (live), plus any stale AccountHotkeys whose TargetName no longer resolves
/// (red row with Rebind dropdown). Save atomically replaces the caller's
/// pending binding list.
/// </summary>
public sealed class AccountHotkeysDialog : Form
{
    private readonly IReadOnlyList<Account> _accounts;
    private readonly List<(string TargetName, TextBox HotkeyBox)> _liveRows = new();
    private readonly List<(string TargetName, TextBox HotkeyBox, ComboBox RebindCombo)> _staleRows = new();
    private readonly IReadOnlyList<(string label, string combo)> _otherHotkeys;
    // Single Consolas font shared across every hotkey TextBox — WinForms doesn't
    // dispose Control.Font on the control's Dispose, so a per-TextBox Font leaks GDI
    // handles on every dialog lifecycle (cf. memory: "UI Polish Session 2 — 7 GDI fixes").
    private readonly Font _hotkeyFont = new("Consolas", 9f);

    /// <summary>Result of the dialog. Null until DialogResult.OK.</summary>
    public List<HotkeyBinding>? Result { get; private set; }

    /// <summary>
    /// <paramref name="accounts"/> = live Accounts (one row each).
    /// <paramref name="currentBindings"/> = HotkeyConfig.AccountHotkeys snapshot.
    /// <paramref name="otherHotkeys"/> = every OTHER hotkey currently bound in the config
    /// (other dialog's family table, tab-level hotkeys, Team hotkeys) for conflict detection.
    /// Each tuple = (human label, combo string) such as ("Team 1", "Alt+M").
    /// </summary>
    public AccountHotkeysDialog(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<HotkeyBinding> currentBindings,
        IReadOnlyList<(string label, string combo)> otherHotkeys)
    {
        _accounts = accounts;
        _otherHotkeys = otherHotkeys;

        // Classify current bindings: live (TargetName matches an Account) vs stale.
        // Half-populated (exactly one of Combo/TargetName empty) entries are malformed —
        // migration padding emits both empty in lockstep. Log + drop.
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
                    $"AccountHotkeysDialog: malformed binding dropped (Combo='{b.Combo}', TargetName='{b.TargetName}')");
                continue;
            }
            if (!HotkeyBindingUtil.IsPopulated(b)) continue;
            bool resolves = accounts.Any(a => a.Name.Equals(b.TargetName, StringComparison.Ordinal));
            if (resolves) byTargetName[b.TargetName] = b.Combo;
            else staleBindings.Add(b);
        }

        int rowCount = accounts.Count + staleBindings.Count;
        int bodyHeight = 80 + rowCount * 30;
        int formHeight = Math.Min(140 + bodyHeight, 540);

        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.StyleForm(this, "Account Hotkeys", new Size(460, formHeight));

        var card = DarkTheme.MakeCard(this,
            "\uD83D\uDD11",
            "Direct Account Hotkeys",
            DarkTheme.CardGold,
            10, 10, 430, bodyHeight + 30);

        int cy = 32;

        if (accounts.Count == 0 && staleBindings.Count == 0)
        {
            var lblEmpty = DarkTheme.AddCardHint(card,
                "No accounts yet — add one via Settings \u2192 Accounts first.",
                10, cy);
            lblEmpty.Size = new Size(410, 40);
        }
        else
        {
            // Stale rows first (red, with Rebind dropdown).
            foreach (var stale in staleBindings.OrderBy(b => b.TargetName, StringComparer.Ordinal))
            {
                AddStaleRow(card, 10, cy, stale);
                cy += 30;
            }

            // Live rows in Accounts list order.
            foreach (var a in accounts)
            {
                byTargetName.TryGetValue(a.Name, out var currentCombo);
                AddLiveRow(card, 10, cy, a.Name, currentCombo ?? "");
                cy += 30;
            }

            cy += 8;
            DarkTheme.AddCardHint(card,
                "Press combo to capture. Backspace, Delete, or Escape clear. \u26A0 red rows = deleted target.",
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

    private void AddLiveRow(Panel card, int x, int y, string accountName, string currentCombo)
    {
        var lbl = DarkTheme.AddCardLabel(card, accountName, x, y + 4);
        lbl.Size = new Size(220, 20);
        var tb = MakeHotkeyBox(card, x + 230, y + 1, 160, currentCombo);
        _liveRows.Add((accountName, tb));
    }

    private void AddStaleRow(Panel card, int x, int y, HotkeyBinding stale)
    {
        var lbl = DarkTheme.AddCardLabel(card,
            $"\u26A0  {stale.TargetName}  (deleted)",
            x, y + 4);
        lbl.ForeColor = DarkTheme.CardWarn;
        lbl.Size = new Size(180, 20);

        var tb = MakeHotkeyBox(card, x + 190, y + 1, 90, stale.Combo);
        tb.BackColor = DarkTheme.BgInput;

        var rebindItems = new List<string> { "(none \u2014 clear binding)" };
        rebindItems.AddRange(_accounts.Select(a => a.Name));
        var cboRebind = DarkTheme.AddCardComboBox(card, x + 285, y + 1, 130, rebindItems.ToArray());
        cboRebind.DropDownStyle = ComboBoxStyle.DropDownList;
        cboRebind.SelectedIndex = 0;

        _staleRows.Add((stale.TargetName, tb, cboRebind));
    }

    /// <summary>
    /// Inline hotkey-capture TextBox — same key-handling contract as SettingsForm.MakeHotkeyBox:
    /// Delete / Backspace / Escape clear; any combo with at least one modifier + non-modifier key
    /// sets the text as "Ctrl+Alt+X" format.
    /// </summary>
    private TextBox MakeHotkeyBox(Panel card, int x, int y, int width, string initialText)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 20),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.None,
            Font = _hotkeyFont,   // shared — dialog-lifetime Font disposed in override below
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
        // Collect the new binding list: live rows with non-empty Combo + stale rows
        // that were either rebound (dropdown index > 0) or kept (index 0 == clear).
        var newBindings = new List<HotkeyBinding>();

        foreach (var (targetName, tb) in _liveRows)
        {
            var combo = tb.Text.Trim();
            if (!string.IsNullOrEmpty(combo))
                newBindings.Add(new HotkeyBinding { Combo = combo, TargetName = targetName });
        }

        foreach (var (_, tb, cboRebind) in _staleRows)
        {
            var combo = tb.Text.Trim();
            if (string.IsNullOrEmpty(combo)) continue;    // stale row with cleared combo: drop entirely
            if (cboRebind.SelectedIndex <= 0) continue;   // "(none)" picked: drop binding
            var newTarget = cboRebind.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(newTarget)) continue;
            newBindings.Add(new HotkeyBinding { Combo = combo, TargetName = newTarget });
        }

        // Same-combo conflict scan: within this dialog AND against other hotkeys.
        var within = newBindings.Where(b => !string.IsNullOrEmpty(b.Combo))
            .GroupBy(b => b.Combo, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        if (within.Any())
        {
            var lines = within.Select(g =>
                $"  {g.Key}  \u2192  {string.Join(", ", g.Select(b => $"Account '{b.TargetName}'"))}");
            MessageBox.Show(
                "Cannot save — multiple Accounts bound to the same key:\n\n" + string.Join("\n", lines),
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
                externalConflicts.Add($"  {b.Combo}  \u2192  Account '{b.TargetName}' vs {otherLabel}");
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
