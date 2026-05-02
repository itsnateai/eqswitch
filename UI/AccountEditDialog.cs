// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using EQSwitch.Core;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Modal dialog for creating or editing an Account (v4 first-class credentials).
/// Phase 4. All controls via DarkTheme factories; no hardcoded colors.
/// </summary>
public sealed class AccountEditDialog : Form
{
    // Remembers last-open location across opens within a session — shared
    // between Add and Edit modes (same dialog visually). Static so all
    // instances share it; falls back to CenterParent on first open.
    // Process lifetime only; cross-session persistence would need config.
    private static Point? _lastLocation;

    private readonly TextBox _txtNotes;
    private readonly TextBox _txtUsername;
    private readonly TextBox _txtPassword;
    private readonly Button _btnRevealPassword;
    // UseLoginFlag UI removed — Account.UseLoginFlag is always set to true on
    // save (the original toggle existed for early-login-server experiments
    // that are no longer relevant). Existing saved Accounts with the flag
    // set to false get upgraded the next time the user edits them.
    private readonly ComboBox _cboServer;

    private readonly bool _isEdit;
    private readonly string _existingEncryptedPassword;
    private readonly string _existingName;
    private readonly string _selfUsername;
    private readonly string _selfServer;
    private bool _passwordRevealed;

    /// <summary>Result of the dialog. Non-null only when DialogResult == OK.</summary>
    public Account? Result { get; private set; }

    /// <summary>
    /// Creates the dialog. Pass <paramref name="existing"/> to edit, null for a new Account.
    /// <paramref name="otherAccounts"/> is the full list of pending Accounts used for
    /// uniqueness validation; the dialog excludes the self row (by Username+Server) when editing.
    /// </summary>
    public AccountEditDialog(Account? existing, IReadOnlyList<Account> otherAccounts)
    {
        _isEdit = existing != null;
        _existingEncryptedPassword = existing?.EncryptedPassword ?? "";
        _existingName = existing?.Name ?? "";
        _selfUsername = existing?.Username ?? "";
        _selfServer = existing?.Server ?? "";

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
        // Inputs are 200px (was 290) -- usernames and passwords are short.
        // Password row uses 145+55 split so Show/Hide isn't clipped.
        const int formW = 360;
        const int cardW = 340;
        const int L = 10, I = 90;
        const int inputW = 200;

        // Content cy: 32 + Name 30 + Username 30 + Password 30 + (edit hint 20) + Server 30
        int contentH = 32 + 30 * 4;
        if (_isEdit) contentH += 20;
        int cardH = contentH + 6;
        int btnY = 10 + cardH + 12;
        int formH = btnY + 30 + 12;

        DarkTheme.StyleForm(this, _isEdit ? $"Edit Account \u2014 {existing!.Username}" : "Add Account", new Size(formW, formH));

        var card = DarkTheme.MakeCard(this, "\uD83D\uDD11",
            _isEdit ? "Edit Account" : "New Account",
            DarkTheme.CardGold, 10, 10, cardW, cardH);

        int cy = 32;

        // Row order matches the Accounts grid priority: Username (identity) →
        // Password → Note (the user's friendly nickname; was "Name") → Server.
        DarkTheme.AddCardLabel(card, "Username:", L, cy + 4);
        _txtUsername = DarkTheme.AddCardTextBox(card, I, cy, inputW);
        _txtUsername.Text = existing?.Username ?? "";
        if (_isEdit)
        {
            _txtUsername.ReadOnly = true;
            _txtUsername.BackColor = DarkTheme.BgMedium;
            _txtUsername.ForeColor = DarkTheme.FgDimGray;
        }
        cy += 30;

        DarkTheme.AddCardLabel(card, "Password:", L, cy + 4);
        _txtPassword = DarkTheme.AddCardTextBox(card, I, cy, 145);
        _txtPassword.PasswordChar = '*';
        _btnRevealPassword = DarkTheme.AddCardButton(card, "Show", I + 150, cy - 1, 55);
        _btnRevealPassword.TabStop = false;
        _btnRevealPassword.Click += (_, _) =>
        {
            _passwordRevealed = !_passwordRevealed;
            _txtPassword.PasswordChar = _passwordRevealed ? '\0' : '*';
            _btnRevealPassword.Text = _passwordRevealed ? "Hide" : "Show";
        };
        cy += 30;

        if (_isEdit)
        {
            DarkTheme.AddCardHint(card, "Leave blank to keep existing password.", I, cy);
            cy += 20;
        }

        // Notes: free-form, optional. Maps to Account.Notes (since v3.14.8).
        // Account.Name is no longer surfaced to the user — it's the persisted
        // FK identity for hotkey + team-slot bindings and gets auto-shadowed
        // to Username on creation. Editing here only touches Notes.
        DarkTheme.AddCardLabel(card, "Notes:", L, cy + 4);
        _txtNotes = DarkTheme.AddCardTextBox(card, I, cy, inputW);
        _txtNotes.Text = existing?.Notes ?? "";
        cy += 30;

        DarkTheme.AddCardLabel(card, "Server:", L, cy + 4);
        _cboServer = DarkTheme.AddCardComboBox(card, I, cy, inputW, new[] { "Dalaya" });
        // DropDownList style locks the field: user can only pick from the list.
        // Since the list has just "Dalaya", the value is effectively hardcoded
        // for the user. Server is part of AccountKey identity and is consumed
        // by AutoLoginManager (eqclient.ini + login SHM bridge), so silent
        // typos like "dalaya" lowercase would orphan characters from accounts.
        // SelectedIndex=0 always lands on Dalaya — legacy non-Dalaya server
        // values on existing accounts (e.g. "Tunaria") are intentionally
        // discarded on save, by design.
        _cboServer.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboServer.SelectedIndex = 0;

        // Buttons outside the card, right-aligned (Save x = formW-200, Cancel x = formW-100).
        int btnSaveX = formW - 200;
        int btnCancelX = formW - 100;
        var btnSave = DarkTheme.MakePrimaryButton("Save", btnSaveX, btnY);
        btnSave.Click += (_, _) => OnSaveClicked(otherAccounts);
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, btnCancelX, btnY);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
    }

    private void OnSaveClicked(IReadOnlyList<Account> otherAccounts)
    {
        var notes = _txtNotes.Text.Trim();
        var username = _txtUsername.Text.Trim();
        var server = (_cboServer.Text ?? "").Trim();
        if (string.IsNullOrEmpty(server)) server = "Dalaya";

        // Notes are free-form metadata; uniqueness only on (Username, Server).
        if (string.IsNullOrEmpty(username))
        {
            MessageBox.Show("Username is required.", "Invalid Account",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtUsername.Focus();
            return;
        }

        // Uniqueness. In edit mode, the "self row" identified by (self-Username, self-Server)
        // is excluded. Username is read-only on edit so self-identity is stable.
        foreach (var a in otherAccounts)
        {
            if (_isEdit &&
                a.Username.Equals(_selfUsername, StringComparison.Ordinal) &&
                a.Server.Equals(_selfServer, StringComparison.Ordinal))
            {
                continue;
            }
            if (a.Username.Equals(username, StringComparison.Ordinal) &&
                a.Server.Equals(server, StringComparison.Ordinal))
            {
                MessageBox.Show(
                    $"An Account with Username '{username}' on Server '{server}' already exists.",
                    "Duplicate Credentials", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtUsername.Focus();
                return;
            }
        }

        // Password: edit mode allows blank to keep existing; new requires a value.
        string encryptedPassword;
        if (_isEdit && string.IsNullOrEmpty(_txtPassword.Text))
        {
            encryptedPassword = _existingEncryptedPassword;
        }
        else
        {
            if (string.IsNullOrEmpty(_txtPassword.Text))
            {
                MessageBox.Show("Password is required for new Accounts.", "Password Missing",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtPassword.Focus();
                return;
            }
            try
            {
                encryptedPassword = CredentialManager.Encrypt(_txtPassword.Text);
            }
            catch (Exception ex)
            {
                FileLogger.Error($"AccountEditDialog: DPAPI encrypt failed: {ex.GetType().Name}: {ex.Message}", ex);
                MessageBox.Show($"Failed to encrypt password: {ex.Message}", "Encryption Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        // Name = FK identity. On create, shadow it to Username so bindings have
        // a stable key from day one. On edit, preserve the existing Name verbatim
        // — touching it would silently break any hotkey / team-slot / tray
        // binding whose TargetName == this.Name.
        var name = _isEdit ? _existingName : username;

        Result = new Account
        {
            Name = name,
            Username = username,
            EncryptedPassword = encryptedPassword,
            Server = server,
            UseLoginFlag = true,
            Notes = notes,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
