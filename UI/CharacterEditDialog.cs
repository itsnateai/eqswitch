using EQSwitch.Config;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Dialog for editing per-slot affinity and priority overrides.
/// Core checkboxes for affinity (no hex), priority dropdown.
/// </summary>
public class CharacterEditDialog : Form
{
    private readonly CharacterProfile _character;
    private ComboBox _cboPriority = null!;
    private CheckBox _chkAffinityOverride = null!;
    private CheckBox _chkPriorityOverride = null!;
    private CheckBox[] _coreChecks = null!;
    private Label _lblMask = null!;

    private static readonly string[] PriorityOptions = { "High", "AboveNormal", "Normal", "BelowNormal" };

    public CharacterEditDialog(CharacterProfile character)
    {
        _character = character;
        InitializeComponents();
    }

    private void InitializeComponents()
    {
        var (coreCount, _) = AffinityManager.DetectCores();

        Text = $"Edit — Slot {_character.SlotIndex + 1}";
        Size = new Size(520, 300);
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

        // ─── Affinity Override ───────────────────────────────
        _chkAffinityOverride = new CheckBox
        {
            Text = "CPU Affinity Override",
            Location = new Point(15, y),
            AutoSize = true,
            Checked = _character.AffinityOverride.HasValue,
            ForeColor = DarkTheme.FgWhite
        };
        _chkAffinityOverride.CheckedChanged += (_, _) => SetCoresEnabled(_chkAffinityOverride.Checked);
        Controls.Add(_chkAffinityOverride);

        _lblMask = new Label
        {
            Text = _character.AffinityOverride.HasValue ? $"0x{_character.AffinityOverride.Value:X}" : "",
            Location = new Point(200, y + 2),
            AutoSize = true,
            ForeColor = DarkTheme.FgDimGray,
            Font = new Font("Consolas", 8.5f)
        };
        Controls.Add(_lblMask);
        y += 28;

        // Core checkboxes
        long currentMask = _character.AffinityOverride ?? 0xFF;
        bool enabled = _character.AffinityOverride.HasValue;
        _coreChecks = new CheckBox[coreCount];
        int perRow = Math.Min(coreCount, 20);
        int checkW = Math.Min(24, (480 - 30) / perRow);
        for (int i = 0; i < coreCount; i++)
        {
            var chk = new CheckBox
            {
                Text = i.ToString(),
                Location = new Point(30 + (i % perRow) * checkW, y + (i / perRow) * 22),
                Size = new Size(checkW, 20),
                ForeColor = i < 8 ? DarkTheme.CardGreen : DarkTheme.CardBlue,
                Font = new Font("Consolas", 7.5f),
                BackColor = Color.Transparent,
                Checked = (currentMask & (1L << i)) != 0,
                Enabled = enabled
            };
            chk.CheckedChanged += (_, _) => UpdateMaskLabel();
            Controls.Add(chk);
            _coreChecks[i] = chk;
        }
        y += 30 + ((coreCount - 1) / perRow) * 22;

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
        y += 45;

        // ─── Buttons ─────────────────────────────────────────
        var btnSave = new Button
        {
            Text = "Save",
            Location = new Point(310, y),
            Size = new Size(85, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.AccentGreen,
            ForeColor = DarkTheme.FgWhite,
            DialogResult = DialogResult.None
        };
        btnSave.Click += (_, _) =>
        {
            if (_chkAffinityOverride.Checked)
            {
                long mask = 0;
                for (int i = 0; i < _coreChecks.Length; i++)
                    if (_coreChecks[i].Checked) mask |= 1L << i;
                if (mask == 0)
                {
                    MessageBox.Show("Select at least one core.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            Location = new Point(405, y),
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

    private void SetCoresEnabled(bool enabled)
    {
        for (int i = 0; i < _coreChecks.Length; i++)
            _coreChecks[i].Enabled = enabled;
    }

    private void UpdateMaskLabel()
    {
        long mask = 0;
        for (int i = 0; i < _coreChecks.Length; i++)
            if (_coreChecks[i].Checked) mask |= 1L << i;
        _lblMask.Text = mask > 0 ? $"0x{mask:X}" : "";
    }
}
