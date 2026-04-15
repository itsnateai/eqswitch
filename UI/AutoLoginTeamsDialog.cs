using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Dialog for configuring Autologin Teams 1-4 (v4: accepts Account + Character lists).
/// Each team has 2 slots. Slot dropdowns list Characters (enter-world targets, preferred)
/// followed by Accounts (charselect-only fallback). A colored status pill next to each
/// slot indicates resolution: green = Character, amber = Account-only, red = unresolved.
///
/// Team{N}AutoEnter checkbox overrides: Phase 3 binary semantics — true forces Enter
/// World on every slot regardless of target type; false stops every slot at charselect.
/// </summary>
internal sealed class AutoLoginTeamsDialog : Form
{
    private enum SlotKind { None, Character, Account }

    private sealed class SlotOption
    {
        public string Value { get; }
        public string Display { get; }
        public SlotKind Kind { get; }
        public SlotOption(string value, string display, SlotKind kind)
        {
            Value = value;
            Display = display;
            Kind = kind;
        }
        public override string ToString() => Display;
    }

    private readonly IReadOnlyList<Account> _accounts;
    private readonly IReadOnlyList<Character> _characters;

    private readonly ComboBox _cboTeam1A, _cboTeam1B;
    private readonly ComboBox _cboTeam2A, _cboTeam2B;
    private readonly ComboBox _cboTeam3A, _cboTeam3B;
    private readonly ComboBox _cboTeam4A, _cboTeam4B;
    private readonly Label _pillTeam1A, _pillTeam1B;
    private readonly Label _pillTeam2A, _pillTeam2B;
    private readonly Label _pillTeam3A, _pillTeam3B;
    private readonly Label _pillTeam4A, _pillTeam4B;
    private readonly CheckBox _chkTeam1Enter, _chkTeam2Enter, _chkTeam3Enter, _chkTeam4Enter;

    private readonly System.Windows.Forms.ToolTip _tooltip = new();

    public string Team1Account1 => GetValue(_cboTeam1A);
    public string Team1Account2 => GetValue(_cboTeam1B);
    public string Team2Account1 => GetValue(_cboTeam2A);
    public string Team2Account2 => GetValue(_cboTeam2B);
    public string Team3Account1 => GetValue(_cboTeam3A);
    public string Team3Account2 => GetValue(_cboTeam3B);
    public string Team4Account1 => GetValue(_cboTeam4A);
    public string Team4Account2 => GetValue(_cboTeam4B);
    public bool Team1AutoEnter => _chkTeam1Enter.Checked;
    public bool Team2AutoEnter => _chkTeam2Enter.Checked;
    public bool Team3AutoEnter => _chkTeam3Enter.Checked;
    public bool Team4AutoEnter => _chkTeam4Enter.Checked;

    public AutoLoginTeamsDialog(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Character> characters,
        string team1A, string team1B,
        string team2A, string team2B,
        string team3A, string team3B,
        string team4A, string team4B,
        bool team1AutoEnter = false, bool team2AutoEnter = false,
        bool team3AutoEnter = false, bool team4AutoEnter = false)
    {
        _accounts = accounts;
        _characters = characters;

        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.StyleForm(this, "Autologin Teams", new Size(560, 300));
        MinimizeBox = false;

        const int L = 15, I = 80, CW = 150, gap = 8, PILLW = 24;
        int CX = I + CW + gap + PILLW + gap + CW + gap + PILLW + gap;
        int y = 18;

        // Header
        DarkTheme.AddLabel(this, "Enter", CX, 2).Font = DarkTheme.FontUI75;

        // Team rows
        (_cboTeam1A, _pillTeam1A, _cboTeam1B, _pillTeam1B, _chkTeam1Enter) = AddTeamRow("Team 1:", L, I, CW, gap, PILLW, CX, ref y, team1AutoEnter);
        (_cboTeam2A, _pillTeam2A, _cboTeam2B, _pillTeam2B, _chkTeam2Enter) = AddTeamRow("Team 2:", L, I, CW, gap, PILLW, CX, ref y, team2AutoEnter);
        (_cboTeam3A, _pillTeam3A, _cboTeam3B, _pillTeam3B, _chkTeam3Enter) = AddTeamRow("Team 3:", L, I, CW, gap, PILLW, CX, ref y, team3AutoEnter);
        (_cboTeam4A, _pillTeam4A, _cboTeam4B, _pillTeam4B, _chkTeam4Enter) = AddTeamRow("Team 4:", L, I, CW, gap, PILLW, CX, ref y, team4AutoEnter);

        // Legend
        var legend = DarkTheme.AddLabel(this,
            "\u2713 = Character (enter world)   !  = Account (charselect)   \u2717 = unresolved",
            L, y + 2);
        legend.ForeColor = DarkTheme.FgDimGray;
        legend.Font = DarkTheme.FontUI75;
        legend.AutoSize = true;
        y += 22;

        // Warning label (same-login collision)
        var lblWarn = DarkTheme.AddLabel(this, "", I, y + 2);
        lblWarn.ForeColor = DarkTheme.FgWarn;
        lblWarn.Font = DarkTheme.FontUI75;
        lblWarn.AutoSize = true;
        lblWarn.Visible = false;

        var btnOK = DarkTheme.MakePrimaryButton("Save", L, y);
        btnOK.Width = 100;
        btnOK.Click += (_, _) =>
        {
            // Block same Account (Username, Server) in both slots of the same team —
            // EQ kicks duplicate logins.
            var teams = new[]
            {
                (_cboTeam1A, _cboTeam1B, "Team 1"),
                (_cboTeam2A, _cboTeam2B, "Team 2"),
                (_cboTeam3A, _cboTeam3B, "Team 3"),
                (_cboTeam4A, _cboTeam4B, "Team 4"),
            };
            foreach (var (a, b, name) in teams)
            {
                var accA = ResolveAccountForSlot(a);
                var accB = ResolveAccountForSlot(b);
                if (accA == null || accB == null) continue;
                if (accA.Username.Equals(accB.Username, StringComparison.Ordinal) &&
                    accA.Server.Equals(accB.Server, StringComparison.Ordinal))
                {
                    lblWarn.Text = $"\u26a0 {name}: both slots share login '{accA.Username}' — EQ will kick one";
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

        // Select saved values (after event subscription so pills refresh).
        SelectByValue(_cboTeam1A, team1A);  SelectByValue(_cboTeam1B, team1B);
        SelectByValue(_cboTeam2A, team2A);  SelectByValue(_cboTeam2B, team2B);
        SelectByValue(_cboTeam3A, team3A);  SelectByValue(_cboTeam3B, team3B);
        SelectByValue(_cboTeam4A, team4A);  SelectByValue(_cboTeam4B, team4B);
    }

    private (ComboBox a, Label pa, ComboBox b, Label pb, CheckBox chk) AddTeamRow(
        string label, int L, int I, int CW, int gap, int PILLW, int CX, ref int y, bool enterChecked)
    {
        DarkTheme.AddLabel(this, label, L, y + 3);
        var a = MakeCombo(I, y, CW);
        var pa = MakePill(I + CW + gap / 2, y + 2);
        var b = MakeCombo(I + CW + gap + PILLW + gap, y, CW);
        var pb = MakePill(I + CW + gap + PILLW + gap + CW + gap / 2, y + 2);
        var chk = MakeEnterCheck(CX + 10, y + 2, enterChecked);
        a.SelectedIndexChanged += (_, _) => RefreshPill(a, pa);
        b.SelectedIndexChanged += (_, _) => RefreshPill(b, pb);
        y += 36;
        return (a, pa, b, pb, chk);
    }

    private CheckBox MakeEnterCheck(int x, int y, bool isChecked)
    {
        var chk = new CheckBox
        {
            Location = new Point(x, y),
            Size = new Size(16, 16),
            Checked = isChecked,
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.BgDark
        };
        Controls.Add(chk);
        return chk;
    }

    private Label MakePill(int x, int y)
    {
        var lbl = new Label
        {
            Location = new Point(x, y),
            Size = new Size(20, 20),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 9f),
            BackColor = Color.Transparent,
            ForeColor = DarkTheme.FgWhite,
        };
        Controls.Add(lbl);
        return lbl;
    }

    private ComboBox MakeCombo(int x, int y, int width)
    {
        var items = new List<SlotOption>
        {
            new("", "(none)", SlotKind.None),
        };
        foreach (var c in _characters)
            items.Add(new SlotOption(c.Name, $"\uD83E\uDDD9  {c.Name}", SlotKind.Character));
        foreach (var a in _accounts)
        {
            // Don't duplicate: if Account.Name matches a Character.Name we've listed, skip.
            if (_characters.Any(c => c.Name.Equals(a.Name, StringComparison.Ordinal))) continue;
            items.Add(new SlotOption(a.Name, $"\uD83D\uDD11  {a.Name}", SlotKind.Account));
        }

        var cb = new ComboBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 24),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
        };
        foreach (var it in items)
            cb.Items.Add(it);
        cb.SelectedIndex = 0;

        int maxW = items.Max(i => TextRenderer.MeasureText(i.Display, cb.Font).Width) + 16;
        if (maxW > width) cb.DropDownWidth = maxW;

        cb.MouseWheel += (_, e) => ((HandledMouseEventArgs)e).Handled = true;
        Controls.Add(cb);
        return cb;
    }

    private void SelectByValue(ComboBox cbo, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            cbo.SelectedIndex = 0;
            RefreshPillForCombo(cbo);
            return;
        }
        for (int i = 0; i < cbo.Items.Count; i++)
        {
            if (cbo.Items[i] is SlotOption opt && opt.Value.Equals(value, StringComparison.Ordinal))
            {
                cbo.SelectedIndex = i;
                RefreshPillForCombo(cbo);
                return;
            }
        }
        // Unresolved — keep (none) selected but surface via status pill.
        cbo.SelectedIndex = 0;
        // Stash the unresolved string on the combo's Tag so RefreshPill can read it.
        cbo.Tag = value;
        RefreshPillForCombo(cbo);
    }

    private void RefreshPillForCombo(ComboBox cbo)
    {
        Label? pill = cbo switch
        {
            _ when cbo == _cboTeam1A => _pillTeam1A,
            _ when cbo == _cboTeam1B => _pillTeam1B,
            _ when cbo == _cboTeam2A => _pillTeam2A,
            _ when cbo == _cboTeam2B => _pillTeam2B,
            _ when cbo == _cboTeam3A => _pillTeam3A,
            _ when cbo == _cboTeam3B => _pillTeam3B,
            _ when cbo == _cboTeam4A => _pillTeam4A,
            _ when cbo == _cboTeam4B => _pillTeam4B,
            _ => null,
        };
        if (pill != null) RefreshPill(cbo, pill);
    }

    private void RefreshPill(ComboBox cbo, Label pill)
    {
        var opt = cbo.SelectedItem as SlotOption;
        // Case: (none) is selected. If Tag holds an unresolved string (loaded from config),
        // show red; otherwise blank.
        if (opt == null || opt.Kind == SlotKind.None)
        {
            var tagValue = cbo.Tag as string;
            if (!string.IsNullOrEmpty(tagValue))
            {
                pill.Text = "\u2717";
                pill.BackColor = DarkTheme.StatusFail;
                pill.ForeColor = Color.White;
                _tooltip.SetToolTip(pill,
                    $"'{tagValue}' doesn't match any Account or Character — unbind or pick a valid target.");
            }
            else
            {
                pill.Text = "";
                pill.BackColor = Color.Transparent;
                _tooltip.SetToolTip(pill, "");
            }
            return;
        }

        // User actively selected — clear any stale unresolved tag.
        cbo.Tag = null;

        if (opt.Kind == SlotKind.Character)
        {
            pill.Text = "\u2713";
            pill.BackColor = DarkTheme.StatusOk;
            pill.ForeColor = Color.White;
            _tooltip.SetToolTip(pill, "Resolves to Character — will enter world.");
        }
        else // Account
        {
            pill.Text = "!";
            pill.BackColor = DarkTheme.StatusWarn;
            pill.ForeColor = Color.Black;
            _tooltip.SetToolTip(pill,
                "Resolves to Account — will stop at charselect. Pick a Character to enter world instead.");
        }
    }

    /// <summary>
    /// Returns the Account object corresponding to the combo's current selection, regardless
    /// of whether the user picked a Character or an Account — Characters resolve back to their
    /// underlying Account via FK. Used for same-login collision detection on Save.
    /// </summary>
    private Account? ResolveAccountForSlot(ComboBox cbo)
    {
        if (cbo.SelectedItem is not SlotOption opt || opt.Kind == SlotKind.None) return null;
        if (opt.Kind == SlotKind.Character)
        {
            var ch = _characters.FirstOrDefault(c => c.Name.Equals(opt.Value, StringComparison.Ordinal));
            if (ch == null) return null;
            return _accounts.FirstOrDefault(a =>
                a.Username.Equals(ch.AccountUsername, StringComparison.Ordinal) &&
                a.Server.Equals(ch.AccountServer, StringComparison.Ordinal));
        }
        return _accounts.FirstOrDefault(a => a.Name.Equals(opt.Value, StringComparison.Ordinal));
    }

    private static string GetValue(ComboBox cbo) =>
        (cbo.SelectedItem as SlotOption)?.Value ?? (cbo.Tag as string) ?? "";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tooltip.Dispose();
            DarkTheme.DisposeControlFonts(this);
        }
        base.Dispose(disposing);
    }
}
