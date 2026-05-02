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
/// Phase 4. ClassHint, DisplayLabel, and Notes are not edited in the UI — they don't
/// surface in the main accounts/character grid, so the dialog stays focused on the
/// fields that drive launch behavior (Name + Account + Slot). Existing values are
/// preserved silently from the source record so legacy data survives an edit.
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

    private readonly bool _isEdit;
    private readonly string _selfName;
    private readonly string _existingClassHint;
    private readonly string _existingDisplayLabel;
    private readonly string _existingNotes;

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
        _existingDisplayLabel = existing?.DisplayLabel ?? "";
        _existingNotes = existing?.Notes ?? "";

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
        // Inputs are 200px wide; card height computed from row count so the
        // bottom doesn't leak empty space.
        const int formW = 360;
        const int cardW = 340;
        const int L = 10, I = 105;
        const int inputW = 200;

        // Content cy: 32 (card header) + Name 30 + Account 30 + Slot 30
        int contentH = 32 + 30 * 3;
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
        // Display the login Username, not the FK shadow Name (which can hold a
        // legacy custom display string on pre-v3.14.8 accounts).
        _cboAccount.DisplayMember = nameof(Account.Username);
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
        // Hint sits to the right of the numeric. AddCardHint returns an AutoSize=true
        // label so we can't pin width via Size; shorten the text instead so it fits
        // within the card bounds (formW=360, card right edge at form-x=350).
        DarkTheme.AddCardHint(card, "0 = match by name", I + 80, cy + 4);
        cy += 30;

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
            // DisplayLabel / ClassHint / Notes are not edited in the UI; carry the
            // existing values through so an edit doesn't wipe legacy data.
            DisplayLabel = _existingDisplayLabel,
            ClassHint = _existingClassHint,
            Notes = _existingNotes,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
