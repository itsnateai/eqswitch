// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

namespace EQSwitch.UI;

/// <summary>
/// Simple first-run dialog that asks for the EQ installation path.
/// Validates that eqgame.exe exists in the selected directory.
/// </summary>
public class FirstRunDialog : Form
{
    private TextBox _pathTextBox = null!;
    public string SelectedEQPath { get; private set; } = "";

    public FirstRunDialog()
    {
        InitializeComponents();
    }

    private void InitializeComponents()
    {
        DarkTheme.StyleForm(this, "EQSwitch — First Run Setup", new Size(500, 220));
        MinimizeBox = false;

        // Welcome label
        var welcomeLabel = new Label
        {
            Text = "Welcome to EQSwitch!\n\nPlease select your EverQuest installation folder:",
            Location = new Point(20, 15),
            Size = new Size(450, 60),
            ForeColor = DarkTheme.FgWhite,
            Font = new Font("Segoe UI", 10)
        };
        Controls.Add(welcomeLabel);

        // Path input
        _pathTextBox = new TextBox
        {
            Text = @"C:\EverQuest",
            Location = new Point(20, 80),
            Size = new Size(350, 25),
            Font = new Font("Segoe UI", 10),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(_pathTextBox);

        // Browse button
        var browseBtn = DarkTheme.MakeButton("Browse...", DarkTheme.BgMedium, 380, 78);
        browseBtn.Size = new Size(80, 28);
        browseBtn.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog
            {
                Description = "Select EverQuest folder",
                InitialDirectory = _pathTextBox.Text
            };
            if (fbd.ShowDialog() == DialogResult.OK)
                _pathTextBox.Text = fbd.SelectedPath;
        };
        Controls.Add(browseBtn);

        // OK button (primary green)
        var okBtn = DarkTheme.MakePrimaryButton("Start", 280, 130);
        okBtn.Size = new Size(90, 35);
        okBtn.DialogResult = DialogResult.None; // We validate first
        okBtn.Click += (_, _) =>
        {
            var path = _pathTextBox.Text.Trim();
            if (!Directory.Exists(path))
            {
                MessageBox.Show("That folder doesn't exist.", "EQSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var exePath = Path.Combine(path, "eqgame.exe");
            if (!File.Exists(exePath))
            {
                var result = MessageBox.Show(
                    "eqgame.exe was not found in that folder.\nContinue anyway?",
                    "EQSwitch",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (result != DialogResult.Yes) return;
            }

            SelectedEQPath = path;
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(okBtn);

        // Cancel button
        var cancelBtn = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 380, 130);
        cancelBtn.Size = new Size(80, 35);
        cancelBtn.DialogResult = DialogResult.Cancel;
        Controls.Add(cancelBtn);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }
}
