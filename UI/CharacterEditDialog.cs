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

        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.StyleForm(this,
            _isEdit ? $"Edit Character \u2014 {existing!.Name}" : "Add Character",
            new Size(440, 400));

        // Guard: can't create a Character without Accounts.
        if (availableAccounts.Count == 0)
        {
            var lblNoAcct = DarkTheme.AddLabel(this,
                "Add an Account first — Characters require an Account to launch into.",
                14, 30);
            lblNoAcct.Size = new Size(410, 50);
            lblNoAcct.ForeColor = DarkTheme.FgDimGray;

            var btnCloseEmpty = DarkTheme.MakeButton("Close", DarkTheme.BgMedium, 330, 120);
            btnCloseEmpty.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnCloseEmpty);
            CancelButton = btnCloseEmpty;
            AcceptButton = btnCloseEmpty;
            return;
        }

        var card = DarkTheme.MakeCard(this, "\uD83E\uDDD9",
            _isEdit ? "Edit Character" : "New Character",
            DarkTheme.CardPurple, 10, 10, 410, 335);

        const int L = 10, I = 115;
        int cy = 32;

        DarkTheme.AddCardLabel(card, "Name:", L, cy + 4);
        _txtName = DarkTheme.AddCardTextBox(card, I, cy, 275);
        _txtName.Text = existing?.Name ?? "";
        cy += 30;

        DarkTheme.AddCardLabel(card, "Account:", L, cy + 4);
        _cboAccount = DarkTheme.AddCardComboBox(card, I, cy, 275, Array.Empty<string>());
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
        _txtDisplayLabel = DarkTheme.AddCardTextBox(card, I, cy, 275);
        _txtDisplayLabel.Text = existing?.DisplayLabel ?? "";
        cy += 30;

        DarkTheme.AddCardLabel(card, "Notes:", L, cy + 4);
        _txtNotes = new TextBox
        {
            Location = new Point(I, cy),
            Size = new Size(275, 80),
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

        var btnSave = DarkTheme.MakePrimaryButton("Save", 230, 355);
        btnSave.Click += (_, _) => OnSaveClicked(otherCharacters);
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 330, 355);
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
