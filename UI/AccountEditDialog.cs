// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

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
    private readonly TextBox _txtName;
    private readonly TextBox _txtUsername;
    private readonly TextBox _txtPassword;
    private readonly Button _btnRevealPassword;
    private readonly ComboBox _cboServer;
    private readonly CheckBox _chkUseLoginFlag;

    private readonly bool _isEdit;
    private readonly string _existingEncryptedPassword;
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
        _selfUsername = existing?.Username ?? "";
        _selfServer = existing?.Server ?? "";

        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.StyleForm(this, _isEdit ? $"Edit Account \u2014 {existing!.Name}" : "Add Account", new Size(440, 330));

        var card = DarkTheme.MakeCard(this, "\uD83D\uDD11",
            _isEdit ? "Edit Account" : "New Account",
            DarkTheme.CardGold, 10, 10, 410, 265);

        const int L = 10, I = 100;
        int cy = 32;

        DarkTheme.AddCardLabel(card, "Name:", L, cy + 4);
        _txtName = DarkTheme.AddCardTextBox(card, I, cy, 290);
        _txtName.Text = existing?.Name ?? "";
        cy += 30;

        DarkTheme.AddCardLabel(card, "Username:", L, cy + 4);
        _txtUsername = DarkTheme.AddCardTextBox(card, I, cy, 290);
        _txtUsername.Text = existing?.Username ?? "";
        if (_isEdit)
        {
            _txtUsername.ReadOnly = true;
            _txtUsername.BackColor = DarkTheme.BgMedium;
            _txtUsername.ForeColor = DarkTheme.FgDimGray;
        }
        cy += 30;

        DarkTheme.AddCardLabel(card, "Password:", L, cy + 4);
        _txtPassword = DarkTheme.AddCardTextBox(card, I, cy, 240);
        _txtPassword.PasswordChar = '*';
        _btnRevealPassword = DarkTheme.AddCardButton(card, "Show", I + 245, cy - 1, 45);
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

        DarkTheme.AddCardLabel(card, "Server:", L, cy + 4);
        _cboServer = DarkTheme.AddCardComboBox(card, I, cy, 290, new[] { "Dalaya" });
        _cboServer.DropDownStyle = ComboBoxStyle.DropDown;
        _cboServer.Text = string.IsNullOrEmpty(existing?.Server) ? "Dalaya" : existing!.Server;
        cy += 30;

        _chkUseLoginFlag = DarkTheme.AddCardCheckBox(card, "Use login flag (pass -login to eqgame.exe)", L, cy + 2);
        _chkUseLoginFlag.Width = 380;
        _chkUseLoginFlag.Checked = existing?.UseLoginFlag ?? false;

        // Buttons outside the card, at the bottom of the form.
        var btnSave = DarkTheme.MakePrimaryButton("Save", 230, 285);
        btnSave.Click += (_, _) => OnSaveClicked(otherAccounts);
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 330, 285);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
    }

    private void OnSaveClicked(IReadOnlyList<Account> otherAccounts)
    {
        var name = _txtName.Text.Trim();
        var username = _txtUsername.Text.Trim();
        var server = (_cboServer.Text ?? "").Trim();
        if (string.IsNullOrEmpty(server)) server = "Dalaya";

        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Name is required.", "Invalid Account",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtName.Focus();
            return;
        }
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
            if (a.Name.Equals(name, StringComparison.Ordinal))
            {
                MessageBox.Show($"An Account named '{name}' already exists.", "Duplicate Name",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtName.Focus();
                return;
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

        Result = new Account
        {
            Name = name,
            Username = username,
            EncryptedPassword = encryptedPassword,
            Server = server,
            UseLoginFlag = _chkUseLoginFlag.Checked,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
