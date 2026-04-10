using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Dialog for configuring Autologin Team 1 and Team 2 assignments.
/// Each team has 2 account slots. Accounts can appear in both teams.
/// </summary>
internal sealed class AutoLoginTeamsDialog : Form
{
    private readonly ComboBox _cboTeam1A;
    private readonly ComboBox _cboTeam1B;
    private readonly ComboBox _cboTeam2A;
    private readonly ComboBox _cboTeam2B;

    public string Team1Account1 => GetUsername(_cboTeam1A);
    public string Team1Account2 => GetUsername(_cboTeam1B);
    public string Team2Account1 => GetUsername(_cboTeam2A);
    public string Team2Account2 => GetUsername(_cboTeam2B);

    private readonly List<LoginAccount> _accounts;

    public AutoLoginTeamsDialog(
        List<LoginAccount> accounts,
        string team1A, string team1B,
        string team2A, string team2B)
    {
        _accounts = accounts;

        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.StyleForm(this, "Autologin Teams", new Size(420, 210));
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

        // Hint
        var hint = DarkTheme.AddLabel(this, "Same account can be in both teams.", L, y + 2);
        hint.ForeColor = DarkTheme.FgDimGray;
        hint.Font = DarkTheme.FontUI75;
        y += 28;

        // Buttons
        var btnOK = DarkTheme.MakePrimaryButton("Save", L, y);
        btnOK.Width = 100;
        btnOK.Click += (_, _) => DialogResult = DialogResult.OK;
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
    }

    private ComboBox MakeCombo(int x, int y, int width)
    {
        var labels = _accounts.Select(a =>
            string.IsNullOrEmpty(a.CharacterName) ? a.Username : a.CharacterName).ToList();
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

        // Auto-size dropdown width to longest entry
        using var g = cb.CreateGraphics();
        int maxW = labels.Max(l => (int)g.MeasureString(l, cb.Font).Width) + 24;
        if (maxW > width) cb.DropDownWidth = maxW;

        Controls.Add(cb);
        return cb;
    }

    private void SelectByUsername(ComboBox cbo, string username)
    {
        if (string.IsNullOrEmpty(username)) { cbo.SelectedIndex = 0; return; }
        var account = _accounts.FirstOrDefault(a => a.Username == username);
        if (account == null) { cbo.SelectedIndex = 0; return; }
        var label = string.IsNullOrEmpty(account.CharacterName) ? account.Username : account.CharacterName;
        cbo.SelectedItem = label;
        if (cbo.SelectedIndex < 0) cbo.SelectedIndex = 0;
    }

    private string GetUsername(ComboBox cbo)
    {
        if (cbo.SelectedIndex <= 0) return "";
        int idx = cbo.SelectedIndex - 1; // offset by (None)
        return idx < _accounts.Count ? _accounts[idx].Username : "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DarkTheme.DisposeControlFonts(this);
        base.Dispose(disposing);
    }
}
