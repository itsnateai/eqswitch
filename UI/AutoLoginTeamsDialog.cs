using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Dialog for configuring Autologin Teams 1-4.
/// Each team has 2 account slots. Accounts can appear in multiple teams.
/// </summary>
internal sealed class AutoLoginTeamsDialog : Form
{
    private readonly ComboBox _cboTeam1A, _cboTeam1B;
    private readonly ComboBox _cboTeam2A, _cboTeam2B;
    private readonly ComboBox _cboTeam3A, _cboTeam3B;
    private readonly ComboBox _cboTeam4A, _cboTeam4B;

    public string Team1Account1 => GetUsername(_cboTeam1A);
    public string Team1Account2 => GetUsername(_cboTeam1B);
    public string Team2Account1 => GetUsername(_cboTeam2A);
    public string Team2Account2 => GetUsername(_cboTeam2B);
    public string Team3Account1 => GetUsername(_cboTeam3A);
    public string Team3Account2 => GetUsername(_cboTeam3B);
    public string Team4Account1 => GetUsername(_cboTeam4A);
    public string Team4Account2 => GetUsername(_cboTeam4B);

    private readonly List<LoginAccount> _accounts;

    public AutoLoginTeamsDialog(
        List<LoginAccount> accounts,
        string team1A, string team1B,
        string team2A, string team2B,
        string team3A, string team3B,
        string team4A, string team4B)
    {
        _accounts = accounts;

        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.StyleForm(this, "Autologin Teams", new Size(420, 280));
        MinimizeBox = false;

        const int L = 15, I = 80, CW = 145, gap = 10;
        int y = 18;

        // Team 1 row
        DarkTheme.AddLabel(this, "Team 1:", L, y + 3);
        _cboTeam1A = MakeCombo(I, y, CW);
        _cboTeam1B = MakeCombo(I + CW + gap, y, CW);
        y += 36;

        // Team 2 row
        DarkTheme.AddLabel(this, "Team 2:", L, y + 3);
        _cboTeam2A = MakeCombo(I, y, CW);
        _cboTeam2B = MakeCombo(I + CW + gap, y, CW);
        y += 36;

        // Team 3 row
        DarkTheme.AddLabel(this, "Team 3:", L, y + 3);
        _cboTeam3A = MakeCombo(I, y, CW);
        _cboTeam3B = MakeCombo(I + CW + gap, y, CW);
        y += 36;

        // Team 4 row
        DarkTheme.AddLabel(this, "Team 4:", L, y + 3);
        _cboTeam4A = MakeCombo(I, y, CW);
        _cboTeam4B = MakeCombo(I + CW + gap, y, CW);
        y += 36;

        // Hint
        var hint = DarkTheme.AddLabel(this, "Same account can be in multiple teams.", L, y + 2);
        hint.ForeColor = DarkTheme.FgDimGray;
        hint.Font = DarkTheme.FontUI75;
        y += 28;

        // Buttons
        var lblWarn = DarkTheme.AddLabel(this, "", I, y + 2);
        lblWarn.ForeColor = DarkTheme.FgWarn;
        lblWarn.Font = DarkTheme.FontUI75;
        lblWarn.AutoSize = true;
        lblWarn.Visible = false;

        var btnOK = DarkTheme.MakePrimaryButton("Save", L, y);
        btnOK.Width = 100;
        btnOK.Click += (_, _) =>
        {
            // Block same account in both slots of the same team
            var teams = new[] {
                (_cboTeam1A, _cboTeam1B, "Team 1"),
                (_cboTeam2A, _cboTeam2B, "Team 2"),
                (_cboTeam3A, _cboTeam3B, "Team 3"),
                (_cboTeam4A, _cboTeam4B, "Team 4"),
            };
            foreach (var (a, b, name) in teams)
            {
                if (a.SelectedIndex > 0 && a.SelectedIndex == b.SelectedIndex)
                {
                    lblWarn.Text = $"\u26a0 {name}: same account in both slots";
                    lblWarn.Visible = true;
                    return;
                }
            }
            DialogResult = DialogResult.OK;
        };
        Controls.Add(btnOK);

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 130, y);
        btnCancel.Width = 100;
        btnCancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(btnCancel);

        // Select saved values
        SelectByUsername(_cboTeam1A, team1A);
        SelectByUsername(_cboTeam1B, team1B);
        SelectByUsername(_cboTeam2A, team2A);
        SelectByUsername(_cboTeam2B, team2B);
        SelectByUsername(_cboTeam3A, team3A);
        SelectByUsername(_cboTeam3B, team3B);
        SelectByUsername(_cboTeam4A, team4A);
        SelectByUsername(_cboTeam4B, team4B);
    }

    private ComboBox MakeCombo(int x, int y, int width)
    {
        var labels = _accounts.Select(a =>
            string.IsNullOrEmpty(a.CharacterName) ? a.Username
            : a.CharacterName == a.Username ? a.CharacterName
            : $"{a.CharacterName} ({a.Username})").ToList();
        labels.Insert(0, "(None)");

        var cb = new ComboBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 24),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat
        };
        cb.Items.AddRange(labels.ToArray<object>());
        cb.SelectedIndex = 0;

        // Auto-size dropdown width to longest entry (TextRenderer is DPI-safe without a live DC)
        int maxW = labels.Max(l => TextRenderer.MeasureText(l, cb.Font).Width) + 8;
        if (maxW > width) cb.DropDownWidth = maxW;

        // Block mouse wheel from changing selection on hover (prevents accidental changes)
        cb.MouseWheel += (_, e) => ((HandledMouseEventArgs)e).Handled = true;
        Controls.Add(cb);
        return cb;
    }

    private void SelectByUsername(ComboBox cbo, string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) { cbo.SelectedIndex = 0; return; }
        // Match by CharacterName first (unique key), fall back to Username (legacy)
        int idx = _accounts.FindIndex(a => a.CharacterName == identifier);
        if (idx < 0) idx = _accounts.FindIndex(a => a.Username == identifier);
        cbo.SelectedIndex = idx >= 0 ? idx + 1 : 0; // +1 for (None) entry
    }

    /// <summary>Returns CharacterName as the unique account identifier (not Username which may be shared).</summary>
    private string GetUsername(ComboBox cbo)
    {
        if (cbo.SelectedIndex <= 0) return "";
        int idx = cbo.SelectedIndex - 1; // offset by (None)
        if (idx >= _accounts.Count) return "";
        // Store CharacterName — it's unique per account. Username is shared (e.g., gotquiz).
        var acct = _accounts[idx];
        return !string.IsNullOrEmpty(acct.CharacterName) ? acct.CharacterName : acct.Username;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }
}
