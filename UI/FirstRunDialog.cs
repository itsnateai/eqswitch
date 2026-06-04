// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

namespace EQSwitch.UI;

/// <summary>
/// Simple first-run dialog that asks for the EQ installation path.
/// Validates that eqgame.exe exists in the selected directory.
/// </summary>
public class FirstRunDialog : EqSwitchForm
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

        // Path input + Browse — laid out in a 1-row table (layout-first, no magic-number
        // alignment). A single-line TextBox is always font-height; a fixed-height button
        // next to it can't stay aligned across DPI. The table centers both in a shared row
        // so they align at 100/125/150/200% automatically. The panel Size scales via AutoScale.
        var inputRow = new TableLayoutPanel
        {
            Location = new Point(20, 80),
            Size = new Size(450, 32),
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); // textbox fills
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // button hugs text
        inputRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _pathTextBox = new TextBox
        {
            Text = @"C:\EverQuest",
            Anchor = AnchorStyles.Left | AnchorStyles.Right,  // fill width, vertically centered in the row
            Font = new Font("Segoe UI", 10),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 6, 0)
        };
        inputRow.Controls.Add(_pathTextBox, 0, 0);

        var browseBtn = DarkTheme.MakeButton("Browse...", DarkTheme.BgMedium, 0, 0);
        browseBtn.AutoSize = true;
        browseBtn.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        browseBtn.Anchor = AnchorStyles.Left;             // auto width, vertically centered
        browseBtn.Margin = Padding.Empty;
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
        inputRow.Controls.Add(browseBtn, 1, 0);
        Controls.Add(inputRow);

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
