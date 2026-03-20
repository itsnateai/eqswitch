using EQSwitch.Config;

namespace EQSwitch.UI;

/// <summary>
/// Dialog for editing per-character affinity and priority overrides.
/// Modifies the CharacterProfile in-place on OK.
/// </summary>
public class CharacterEditDialog : Form
{
    private readonly CharacterProfile _character;
    private TextBox _txtAffinity = null!;
    private ComboBox _cboPriority = null!;
    private CheckBox _chkAffinityOverride = null!;
    private CheckBox _chkPriorityOverride = null!;

    private static readonly string[] PriorityOptions = { "Normal", "AboveNormal", "High" };

    public CharacterEditDialog(CharacterProfile character)
    {
        _character = character;
        InitializeComponents();
    }

    private void InitializeComponents()
    {
        Text = $"Edit — {_character.Name}";
        Size = new Size(340, 280);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DarkTheme.BgDark;
        ForeColor = DarkTheme.FgWhite;
        Font = new Font("Segoe UI", 9.5f);

        int y = 15;

        // Character info (read-only)
        var lblInfo = new Label
        {
            Text = $"{_character.Name}" + (string.IsNullOrEmpty(_character.Class) ? "" : $" ({_character.Class})") + $" — Slot {_character.SlotIndex + 1}",
            Location = new Point(15, y),
            AutoSize = true,
            ForeColor = DarkTheme.CardCyan,
            Font = new Font("Consolas", 10, FontStyle.Bold)
        };
        Controls.Add(lblInfo);
        y += 35;

        // ─── Affinity Override ───────────────────────────────
        _chkAffinityOverride = new CheckBox
        {
            Text = "CPU Affinity Override",
            Location = new Point(15, y),
            AutoSize = true,
            Checked = _character.AffinityOverride.HasValue,
            ForeColor = DarkTheme.FgWhite
        };
        _chkAffinityOverride.CheckedChanged += (_, _) => _txtAffinity.Enabled = _chkAffinityOverride.Checked;
        Controls.Add(_chkAffinityOverride);
        y += 28;

        var lblMask = new Label { Text = "Mask (hex):", Location = new Point(30, y + 3), AutoSize = true, ForeColor = DarkTheme.FgGray };
        Controls.Add(lblMask);

        _txtAffinity = new TextBox
        {
            Text = _character.AffinityOverride.HasValue ? _character.AffinityOverride.Value.ToString("X") : "FF",
            Location = new Point(110, y),
            Size = new Size(100, 25),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10),
            Enabled = _character.AffinityOverride.HasValue,
            CharacterCasing = CharacterCasing.Upper
        };
        Controls.Add(_txtAffinity);

        var lblHex = new Label { Text = "0x", Location = new Point(95, y + 3), AutoSize = true, ForeColor = DarkTheme.FgDimGray };
        Controls.Add(lblHex);
        y += 40;

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
            Location = new Point(110, y),
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
        y += 45;

        // ─── Buttons ─────────────────────────────────────────
        var btnSave = new Button
        {
            Text = "Save",
            Location = new Point(130, y),
            Size = new Size(85, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.AccentGreen,
            ForeColor = DarkTheme.FgWhite,
            DialogResult = DialogResult.None
        };
        btnSave.Click += (_, _) =>
        {
            // Validate affinity mask
            if (_chkAffinityOverride.Checked)
            {
                if (!long.TryParse(_txtAffinity.Text.Trim(), System.Globalization.NumberStyles.HexNumber, null, out long mask) || mask <= 0)
                {
                    MessageBox.Show("Invalid hex mask. Use values like FF, FF00, FFFF.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _character.AffinityOverride = mask;
            }
            else
            {
                _character.AffinityOverride = null;
            }

            _character.PriorityOverride = _chkPriorityOverride.Checked ? _cboPriority.SelectedItem?.ToString() : null;

            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(btnSave);

        var btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(225, y),
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
