// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// User's choice when confirming deletion of an Account with dependent Characters.
/// </summary>
public enum CascadeDeleteChoice
{
    Cancel,
    Unlink,
    DeleteAll,
}

/// <summary>
/// Modal prompt shown when deleting an Account with dependent Characters.
/// Three-button: Cancel (default, ESC) / Unlink (orphan-preserving) /
/// Delete All (cascade remove). Phase 4.
/// </summary>
public sealed class CascadeDeleteDialog : Form
{
    public CascadeDeleteChoice Choice { get; private set; } = CascadeDeleteChoice.Cancel;

    public CascadeDeleteDialog(Account account, IReadOnlyList<Character> dependents)
    {
        StartPosition = FormStartPosition.CenterParent;

        // Size grows with dependent count (capped at 6 rows + "more" line).
        int shown = Math.Min(dependents.Count, 6);
        int extraLines = (shown > 0 ? shown : 0) + (dependents.Count > shown ? 1 : 0);
        int formHeight = 260 + extraLines * 18;

        DarkTheme.StyleForm(this, $"Delete Account '{account.Name}'?", new Size(500, formHeight));

        var card = DarkTheme.MakeCard(this, "\uD83D\uDDD1",
            "Delete Account with Dependents",
            DarkTheme.CardWarn, 10, 10, 470, formHeight - 90);

        int cy = 32;

        var lblHeader = DarkTheme.AddCardLabel(card,
            $"{dependents.Count} character{(dependents.Count == 1 ? "" : "s")} linked to this account:",
            10, cy);
        lblHeader.Size = new Size(450, 20);
        cy += 22;

        for (int i = 0; i < shown; i++)
        {
            var bullet = DarkTheme.AddCardLabel(card, $"   \u2022  {dependents[i].Name}", 20, cy);
            bullet.Size = new Size(430, 18);
            cy += 18;
        }
        if (dependents.Count > shown)
        {
            var more = DarkTheme.AddCardLabel(card, $"   \u2026 and {dependents.Count - shown} more", 20, cy);
            more.ForeColor = DarkTheme.FgDimGray;
            more.Size = new Size(430, 18);
            cy += 18;
        }
        cy += 10;

        var lblPrompt = DarkTheme.AddCardLabel(card, "What should happen to them?", 10, cy);
        lblPrompt.Size = new Size(450, 20);

        // Hint below the card (inside form).
        var hint = DarkTheme.AddHint(this,
            "Unlinked characters keep their data but can't login until you assign a new account via Edit.",
            14, formHeight - 82);
        hint.Size = new Size(470, 32);

        // Buttons (Cancel / Unlink / Delete All) along the bottom of the form.
        int btnY = formHeight - 48;

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 14, btnY);
        btnCancel.Click += (_, _) =>
        {
            Choice = CascadeDeleteChoice.Cancel;
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
        AcceptButton = btnCancel;   // ESC + Enter both default to the safe choice

        var btnUnlink = DarkTheme.MakeButton("Unlink", DarkTheme.BgMedium, 240, btnY);
        btnUnlink.Width = 110;
        btnUnlink.Click += (_, _) =>
        {
            Choice = CascadeDeleteChoice.Unlink;
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(btnUnlink);

        var btnDeleteAll = DarkTheme.MakeButton("Delete All", DarkTheme.CardWarn, 360, btnY);
        btnDeleteAll.Width = 130;
        btnDeleteAll.ForeColor = DarkTheme.FgWhite;
        btnDeleteAll.Click += (_, _) =>
        {
            Choice = CascadeDeleteChoice.DeleteAll;
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(btnDeleteAll);
    }
}
