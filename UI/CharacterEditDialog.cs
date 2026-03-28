using EQSwitch.Config;

namespace EQSwitch.UI;

/// <summary>
/// Dialog for editing per-slot priority overrides.
/// CPU core assignment is now handled globally via eqclient.ini CPUAffinity0-5.
/// </summary>
public class CharacterEditDialog : Form
{
    private readonly CharacterProfile _character;
    private ComboBox _cboPriority = null!;
    private CheckBox _chkPriorityOverride = null!;

    private static readonly string[] PriorityOptions = { "High", "AboveNormal", "Normal", "BelowNormal" };

    public CharacterEditDialog(CharacterProfile character)
    {
        _character = character;
        InitializeComponents();
    }

    private void InitializeComponents()
    {
        Text = $"Edit — Slot {_character.SlotIndex + 1}";
        Size = new Size(400, 210);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DarkTheme.BgDark;
        ForeColor = DarkTheme.FgWhite;
        Font = new Font("Segoe UI", 9.5f);

        int y = 15;

        // Slot info
        var lblInfo = new Label
        {
            Text = $"Slot {_character.SlotIndex + 1}" + (string.IsNullOrEmpty(_character.Name) ? "" : $"  —  {_character.Name}"),
            Location = new Point(15, y),
            AutoSize = true,
            ForeColor = DarkTheme.CardCyan,
            Font = new Font("Consolas", 10, FontStyle.Bold)
        };
        Controls.Add(lblInfo);
        y += 35;

        // ─── Priority Override ───────────────────────────────
        _chkPriorityOverride = new CheckBox
        {
            Text = "Process Priority Override",
            Location = new Point(15, y),
            AutoSize = true,
            Checked = _character.PriorityOverride != null,
            ForeColor = DarkTheme.FgWhite
        };
        _chkPriorityOverride.CheckedChanged += (_, _) => _cboPriority.Enabled = _chkPriorityOverride.Checked;
        Controls.Add(_chkPriorityOverride);
        y += 28;

        var lblPriority = new Label { Text = "Priority:", Location = new Point(30, y + 3), AutoSize = true, ForeColor = DarkTheme.FgGray };
        Controls.Add(lblPriority);

        _cboPriority = new ComboBox
        {
            Location = new Point(100, y),
            Size = new Size(130, 25),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            FlatStyle = FlatStyle.Flat,
            Enabled = _character.PriorityOverride != null
        };
        _cboPriority.Items.AddRange(PriorityOptions);
        _cboPriority.SelectedItem = _character.PriorityOverride ?? "Normal";
        Controls.Add(_cboPriority);

        var lblHint = new Label
        {
            Text = "Core assignment is managed globally in Process Manager",
            Location = new Point(30, y + 30),
            AutoSize = true,
            ForeColor = DarkTheme.FgDimGray,
            Font = new Font("Segoe UI", 8f, FontStyle.Italic)
        };
        Controls.Add(lblHint);
        y += 70;

        // ─── Buttons ─────────────────────────────────────────
        var btnSave = new Button
        {
            Text = "Save",
            Location = new Point(200, y),
            Size = new Size(85, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.AccentGreen,
            ForeColor = DarkTheme.FgWhite,
            DialogResult = DialogResult.None
        };
        btnSave.Click += (_, _) =>
        {
            _character.PriorityOverride = _chkPriorityOverride.Checked ? _cboPriority.SelectedItem?.ToString() : null;
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(btnSave);

        var btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(295, y),
            Size = new Size(85, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.BgMedium,
            ForeColor = DarkTheme.FgWhite,
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(btnCancel);

        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }
}
