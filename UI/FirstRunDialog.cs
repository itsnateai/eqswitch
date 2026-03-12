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
        Text = "EQSwitch - First Run Setup";
        Size = new Size(500, 220);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.White;

        // Welcome label
        var welcomeLabel = new Label
        {
            Text = "Welcome to EQSwitch!\n\nPlease select your EverQuest installation folder:",
            Location = new Point(20, 15),
            Size = new Size(450, 60),
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
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(_pathTextBox);

        // Browse button
        var browseBtn = new Button
        {
            Text = "Browse...",
            Location = new Point(380, 78),
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 60, 60)
        };
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

        // OK button
        var okBtn = new Button
        {
            Text = "Start",
            Location = new Point(280, 130),
            Size = new Size(90, 35),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 80),
            DialogResult = DialogResult.None // We validate first
        };
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
        var cancelBtn = new Button
        {
            Text = "Cancel",
            Location = new Point(380, 130),
            Size = new Size(80, 35),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 60, 60),
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(cancelBtn);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }
}
