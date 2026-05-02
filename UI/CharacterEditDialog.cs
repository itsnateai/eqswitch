// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Modal dialog for creating or editing a Character (v4 first-class launch target).
/// Phase 4. ClassHint dropped from UI per 2026-04-15 design decision — no reliable
/// source for EQ class data. Field persisted silently from existing record.
/// </summary>
public sealed class CharacterEditDialog : Form
{
    // Remembers last-open location across opens within a session — shared
    // between Add and Edit modes (same dialog visually). Static so all
    // instances share it; falls back to CenterParent on first open.
    // Process lifetime only; cross-session persistence would need config.
    private static Point? _lastLocation;

    private readonly TextBox _txtName = null!;
    private readonly ComboBox _cboAccount = null!;
    private readonly NumericUpDown _numSlot = null!;
    private readonly TextBox _txtDisplayLabel = null!;
    private readonly TextBox _txtNotes = null!;

    private readonly bool _isEdit;
    private readonly string _selfName;
    private readonly string _existingClassHint;

    /// <summary>Result of the dialog. Non-null only when DialogResult == OK.</summary>
    public Character? Result { get; private set; }

    public CharacterEditDialog(
        Character? existing,
        IReadOnlyList<Account> availableAccounts,
        IReadOnlyList<Character> otherCharacters)
    {
        _isEdit = existing != null;
        _selfName = existing?.Name ?? "";
        _existingClassHint = existing?.ClassHint ?? "";

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
        // Tight layout: form ClientSize matches actual content + button row.
        // Inputs are 200px (was 275); Notes textarea also 200x80. Card height
        // computed from row count so the bottom doesn't leak empty space.
        const int formW = 360;
        const int cardW = 340;
        const int L = 10, I = 105;
        const int inputW = 200;

        // Content cy: 32 + Name 30 + Account 30 + Slot 30 + DisplayLabel 30 + Notes 90
        int contentH = 32 + 30 * 4 + 90;
        int cardH = contentH + 6;
        int btnY = 10 + cardH + 12;
        int formH = btnY + 30 + 12;

        DarkTheme.StyleForm(this,
            _isEdit ? $"Edit Character \u2014 {existing!.Name}" : "Add Character",
            new Size(formW, formH));

        // Guard: can't create a Character without Accounts. Matches the
        // padding/tightness of AccountEditDialog: 360 form, 340 card, 10/12/12 margins.
        if (availableAccounts.Count == 0)
        {
            const int warnW = 360;
            const int warnCardW = 340;
            const int warnL = 10;
            const int labelH = 44;
            int warnContentH = 32 + labelH;
            int warnCardH = warnContentH + 6;
            int warnBtnY = 10 + warnCardH + 12;
            int warnH = warnBtnY + 30 + 12;

            DarkTheme.StyleForm(this, "Add Character", new Size(warnW, warnH));

            var warnCard = DarkTheme.MakeCard(this, "⚠", "No Accounts",
                DarkTheme.CardWarn, 10, 10, warnCardW, warnCardH);

            var lblNoAcct = DarkTheme.AddCardLabel(warnCard,
                "Add an Account first — Characters require an Account to launch into.",
                warnL, 32);
            // MaximumSize is what flips Label into wrap-mode — without it,
            // AutoSize=false + a fixed Size renders single-line and clips.
            // height=0 means "no max height" (label can grow to fit wrapped text).
            lblNoAcct.MaximumSize = new Size(warnCardW - 20, 0);
            lblNoAcct.AutoSize = false;
            lblNoAcct.Size = new Size(warnCardW - 20, labelH);

            var btnCloseEmpty = DarkTheme.MakeButton("Close", DarkTheme.BgMedium, warnW - 100, warnBtnY);
            btnCloseEmpty.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnCloseEmpty);
            CancelButton = btnCloseEmpty;
            AcceptButton = btnCloseEmpty;
            return;
        }

        var card = DarkTheme.MakeCard(this, "\uD83E\uDDD9",
            _isEdit ? "Edit Character" : "New Character",
            DarkTheme.CardPurple, 10, 10, cardW, cardH);

        int cy = 32;

        DarkTheme.AddCardLabel(card, "Name:", L, cy + 4);
        _txtName = DarkTheme.AddCardTextBox(card, I, cy, inputW);
        _txtName.Text = existing?.Name ?? "";
        cy += 30;

        DarkTheme.AddCardLabel(card, "Account:", L, cy + 4);
        _cboAccount = DarkTheme.AddCardComboBox(card, I, cy, inputW, Array.Empty<string>());
        _cboAccount.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboAccount.DisplayMember = nameof(Account.Name);
        foreach (var a in availableAccounts)
            _cboAccount.Items.Add(a);
        if (existing != null)
        {
            var match = availableAccounts.FirstOrDefault(a =>
                a.Username.Equals(existing.AccountUsername, StringComparison.Ordinal) &&
                a.Server.Equals(existing.AccountServer, StringComparison.Ordinal));
            _cboAccount.SelectedItem = match ?? availableAccounts[0];
        }
        else
        {
            _cboAccount.SelectedIndex = 0;
        }
        cy += 30;

        DarkTheme.AddCardLabel(card, "Slot:", L, cy + 4);
        _numSlot = DarkTheme.AddCardNumeric(card, I, cy, 70, existing?.CharacterSlot ?? 0, 0, 10);
        var slotHint = DarkTheme.AddCardHint(card, "0 = match by name (recommended)", I + 80, cy + 4);
        slotHint.Size = new Size(200, 18);
        cy += 30;

        DarkTheme.AddCardLabel(card, "Display Label:", L, cy + 4);
        _txtDisplayLabel = DarkTheme.AddCardTextBox(card, I, cy, inputW);
        _txtDisplayLabel.Text = existing?.DisplayLabel ?? "";
        cy += 30;

        DarkTheme.AddCardLabel(card, "Notes:", L, cy + 4);
        _txtNotes = new TextBox
        {
            Location = new Point(I, cy),
            Size = new Size(inputW, 80),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle,
            Text = existing?.Notes ?? "",
        };
        card.Controls.Add(_txtNotes);
        cy += 90;

        int btnSaveX = formW - 200;
        int btnCancelX = formW - 100;
        var btnSave = DarkTheme.MakePrimaryButton("Save", btnSaveX, btnY);
        btnSave.Click += (_, _) => OnSaveClicked(otherCharacters);
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, btnCancelX, btnY);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
    }

    private void OnSaveClicked(IReadOnlyList<Character> otherCharacters)
    {
        var name = _txtName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Name is required.", "Invalid Character",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtName.Focus();
            return;
        }

        foreach (var c in otherCharacters)
        {
            if (_isEdit && c.Name.Equals(_selfName, StringComparison.Ordinal)) continue;
            if (c.Name.Equals(name, StringComparison.Ordinal))
            {
                MessageBox.Show($"A Character named '{name}' already exists.", "Duplicate Name",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtName.Focus();
                return;
            }
        }

        if (_cboAccount.SelectedItem is not Account selectedAccount)
        {
            MessageBox.Show("Account is required.", "Invalid Character",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = new Character
        {
            Name = name,
            AccountUsername = selectedAccount.Username,
            AccountServer = selectedAccount.Server,
            CharacterSlot = (int)_numSlot.Value,
            DisplayLabel = _txtDisplayLabel.Text.Trim(),
            ClassHint = _existingClassHint,   // preserved; not edited in UI
            Notes = _txtNotes.Text,           // multiline — don't trim trailing newlines
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
