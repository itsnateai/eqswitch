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
/// Phase 5a modal for editing AccountHotkeys. One row per _config.Accounts entry
/// (live), plus any stale AccountHotkeys whose TargetName no longer resolves
/// (red row with Rebind dropdown). Save atomically replaces the caller's
/// pending binding list.
/// </summary>
public sealed class AccountHotkeysDialog : Form
{
    // Remembers last-open location across opens within a session. Static so all
    // instances share it; falls back to CenterParent on first open. Process
    // lifetime only — cross-session persistence would need config storage.
    private static Point? _lastLocation;

    private readonly IReadOnlyList<Account> _accounts;
    private readonly List<(string TargetName, TextBox HotkeyBox)> _liveRows = new();
    // RebindAccountList is the parallel ordered list backing the rebind dropdown:
    // index N+1 in the combo (1-based; index 0 is "(none)") maps to RebindAccountList[N].
    // The combo displays Account.Username for clarity, but persistence keys off
    // RebindAccountList[selectedIndex-1].Name (the FK identity).
    private readonly List<(string TargetName, TextBox HotkeyBox, ComboBox RebindCombo, IReadOnlyList<EQSwitch.Models.Account> RebindAccountList)> _staleRows = new();
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
            bool resolves = accounts.Any(a => a.Name.Equals(b.TargetName, StringComparison.OrdinalIgnoreCase));
            if (resolves) byTargetName[b.TargetName] = b.Combo;
            else staleBindings.Add(b);
        }

        // Tight layout matching TeamHotkeysDialog: card ends ~10px below the
        // hint (was ~56px of dead space). Empty-state hint is 40px tall so
        // floor card height at 82.
        // +24px overhead reserves a row at the top of the card for the
        // "Will load to Character Select" intent hint (only shown in the
        // populated branch — the empty-state branch has its own message).
        //
        // Width is stale-aware. Stale (deleted-account) rows need a 3rd column
        // — the rebind dropdown — so they keep the legacy wide 460px layout.
        // The common case (live rows only) is compact: a 305px form with a 150px
        // hotkey box (wide enough for "Ctrl+Alt+Shift+F12"), right-aligned 10px
        // from the card edge so it sits just right of the account-name column.
        // Long names are ellipsized in AddLiveRow so they can't overrun the box.
        // CharacterHotkeysDialog shares these numbers; Team uses the same 150px
        // box on a wider card for its compound "name / name" labels.
        bool hasStale  = staleBindings.Count > 0;
        int  formWidth = hasStale ? 460 : 305;
        int  cardWidth = formWidth - 30;                 // 10 left inset + 20 right gap
        int  rowPitch  = 30;             // a touch airier between rows than 28
        int  boxWidth  = hasStale ? 160 : 150;           // fits "Ctrl+Alt+Shift+F12" centered
        int  boxX      = hasStale ? 240 : (cardWidth - 10 - boxWidth);  // right-aligned 10px from card edge (≈115 on the 305px form), just right of the name

        int rowCount = accounts.Count + staleBindings.Count;
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
            StartPosition = FormStartPosition.Manual;
            DarkTheme.CenterOnOwnerOnLoad(this);
        }
        FormClosing += (_, _) => _lastLocation = Location;
        DarkTheme.StyleForm(this, "Account Hotkeys", new Size(formWidth, formHeight));

        var card = DarkTheme.MakeCard(this,
            "\uD83D\uDD11",
            "Direct Account Hotkeys",
            DarkTheme.CardGold,
            10, 10, cardWidth, cardHeight);

        int cy = 32;

        if (accounts.Count == 0 && staleBindings.Count == 0)
        {
            var lblEmpty = DarkTheme.AddCardHint(card,
                "No accounts yet — add one via Settings \u2192 Accounts first.",
                10, cy);
            lblEmpty.AutoSize = false;   // allow wrap within the narrow card
            lblEmpty.Size = new Size(cardWidth - 20, 40);
        }
        else
        {
            // Intent hint: tell the user what these account-level hotkeys do.
            // Account hotkeys log in but stop at character select — distinct
            // from Character hotkeys which auto-enter-world.
            DarkTheme.AddCardHint(card,
                "Will load to Character Select",
                10, cy);
            cy += 24;

            // Stale rows first (red, with Rebind dropdown).
            foreach (var stale in staleBindings.OrderBy(b => b.TargetName, StringComparer.Ordinal))
            {
                AddStaleRow(card, 10, cy, stale);
                cy += rowPitch;
            }

            // Live rows in Accounts list order. Display = Username (so users can
            // tell at a glance these are EQ login accounts, not characters);
            // TargetName key for the binding stays as Account.Name to keep
            // saved config compatibility with v3.10.x bindings.
            foreach (var a in accounts)
            {
                byTargetName.TryGetValue(a.Name, out var currentCombo);
                AddLiveRow(card, 10, cy, a.Name, a.Username, currentCombo ?? "", boxX, boxWidth);
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

    private void AddLiveRow(Panel card, int x, int y, string accountName, string accountUsername, string currentCombo, int boxX, int boxWidth)
    {
        // Display = Username; binding TargetName = accountName (the friendly Name).
        // Ellipsize so a long username can't overrun the box (the label is AutoSize).
        var shown = DarkTheme.Ellipsize(accountUsername, DarkTheme.FontUI85, boxX - 8 - x);
        var lbl = DarkTheme.AddCardLabel(card, shown, x, y + 4);
        // Match the A-pill orange in the Accounts team-configure window.
        lbl.ForeColor = DarkTheme.FgAccountOrange;
        // Label is AutoSize — it hugs the username text; the box position (boxX,
        // right-aligned by the caller) is what defines the clean two-column gap.
        var tb = MakeHotkeyBox(card, boxX, y + 1, boxWidth, currentCombo);
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

        // Display Username (the login Nate recognizes); store the parallel
        // _accounts list snapshot so OnSaveClicked can resolve selection back
        // to Account.Name (the FK) without trusting the displayed text.
        var rebindAccountList = _accounts.ToList();
        var rebindItems = new List<string> { "(none \u2014 clear binding)" };
        rebindItems.AddRange(rebindAccountList.Select(a => a.Username));
        var cboRebind = DarkTheme.AddCardComboBox(card, x + 285, y + 1, 130, rebindItems.ToArray());
        cboRebind.DropDownStyle = ComboBoxStyle.DropDownList;
        cboRebind.SelectedIndex = 0;

        _staleRows.Add((stale.TargetName, tb, cboRebind, rebindAccountList));
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

        // Shared with SettingsForm so number-row (Alt+1..) and tilde combos round-trip through
        // HotkeyManager.ResolveVK. A raw e.KeyCode.ToString() emits "D1"/"Oemtilde", which the
        // resolver can't parse — the hotkey would register as VK 0 and silently never fire.
        // This dialog stays modifier-only (the no-modifier gate above is unchanged).
        string keyName = SettingsForm.FormatHotkeyKeyName(e.KeyCode);
        // Refuse a combo whose key can't resolve to a VK — it would register as VK 0 and never fire.
        if (EQSwitch.Core.HotkeyManager.ResolveVK(keyName) == 0) return;
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

        foreach (var (_, tb, cboRebind, rebindAccountList) in _staleRows)
        {
            var combo = tb.Text.Trim();
            if (string.IsNullOrEmpty(combo)) continue;    // stale row with cleared combo: drop entirely
            if (cboRebind.SelectedIndex <= 0) continue;   // "(none)" picked: drop binding
            // Combo displays Username; resolve back to Account.Name (the FK) via
            // the parallel list captured at AddStaleRow time. Index 0 is "(none)";
            // accounts start at index 1 -> rebindAccountList[0].
            var acctIdx = cboRebind.SelectedIndex - 1;
            if (acctIdx < 0 || acctIdx >= rebindAccountList.Count) continue;
            var newTarget = rebindAccountList[acctIdx].Name;
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
