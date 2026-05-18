// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Dialog for configuring Autologin Teams 1-6 (v4: accepts Account + Character lists).
/// Each team has 2 slots. Slot dropdowns list Characters (enter-world targets, preferred)
/// followed by Accounts (charselect-only fallback). A colored status pill next to each
/// slot indicates resolution: green = Character, amber = Account-only, red = unresolved.
///
/// Team{N}AutoEnter checkbox overrides: Phase 3 binary semantics — true forces Enter
/// World on every slot regardless of target type; false stops every slot at charselect.
/// </summary>
internal sealed class AutoLoginTeamsDialog : Form
{
    // Remembers last-open location across opens within a session. Static so
    // all instances share it; falls back to CenterParent on first open.
    // Process lifetime only; cross-session persistence would need config.
    private static Point? _lastLocation;

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
    private readonly ComboBox _cboTeam5A, _cboTeam5B;
    private readonly ComboBox _cboTeam6A, _cboTeam6B;
    private readonly Label _pillTeam1A, _pillTeam1B;
    private readonly Label _pillTeam2A, _pillTeam2B;
    private readonly Label _pillTeam3A, _pillTeam3B;
    private readonly Label _pillTeam4A, _pillTeam4B;
    private readonly Label _pillTeam5A, _pillTeam5B;
    private readonly Label _pillTeam6A, _pillTeam6B;
    private readonly System.Windows.Forms.ToolTip _tooltip = new();

    public string Team1Account1 => GetValue(_cboTeam1A);
    public string Team1Account2 => GetValue(_cboTeam1B);
    public string Team2Account1 => GetValue(_cboTeam2A);
    public string Team2Account2 => GetValue(_cboTeam2B);
    public string Team3Account1 => GetValue(_cboTeam3A);
    public string Team3Account2 => GetValue(_cboTeam3B);
    public string Team4Account1 => GetValue(_cboTeam4A);
    public string Team4Account2 => GetValue(_cboTeam4B);
    public string Team5Account1 => GetValue(_cboTeam5A);
    public string Team5Account2 => GetValue(_cboTeam5B);
    public string Team6Account1 => GetValue(_cboTeam6A);
    public string Team6Account2 => GetValue(_cboTeam6B);
    // v3.22.10: hoisted from MakeCombo (which rebuilt them 12× per ctor).
    // Built once in ctor, used by every MakeCombo call. Saves 11 × (list build
    // + 5-6 TextRenderer.MeasureText calls) on a typical config.
    private List<SlotOption> _comboItems = null!;
    private int _comboMaxW;

    public AutoLoginTeamsDialog(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Character> characters,
        string team1A, string team1B,
        string team2A, string team2B,
        string team3A, string team3B,
        string team4A, string team4B,
        string team5A, string team5B,
        string team6A, string team6B)
    {
        _accounts = accounts;
        _characters = characters;

        // v3.22.10: SuspendLayout for the whole ctor. WinForms invalidates the
        // form on every control Add; without suspension the layout engine
        // re-runs 38+ times (12 combos + 12 pills + 6 row labels + legend +
        // hint + warn + 2 buttons). Combined with the items-list hoist below,
        // this cuts the perceived "loading Teams" freeze meaningfully.
        SuspendLayout();

        // Restore last-open position if available; otherwise center on parent.
        if (_lastLocation.HasValue)
        {
            StartPosition = FormStartPosition.Manual;
            Location = _lastLocation.Value;
        }
        else
        {
            StartPosition = FormStartPosition.CenterParent;
        }
        FormClosing += (_, _) => _lastLocation = Location;
        // Width 480 (was 560) — Enter World column removed; rightmost element
        // is now the second pill ending at ~x=444. Height 332 fits 6 team
        // rows + legend + behavior-hint row + warn row + button row.
        DarkTheme.StyleForm(this, "Autologin Teams", new Size(480, 332));
        MinimizeBox = false;

        // v3.22.10: build the combo items list ONCE here. Every ComboBox in
        // this dialog displays the same options (12 of them); rebuilding the
        // list per MakeCombo call was pure waste. MeasureText runs once over
        // the unified list and the result is reused for every combo's
        // DropDownWidth.
        _comboItems = BuildComboItems();
        using (var probeFont = new Font(Font.FontFamily, Font.Size))
        {
            _comboMaxW = _comboItems.Max(i => TextRenderer.MeasureText(i.Display, probeFont).Width) + 16;
        }

        const int L = 15, I = 80, CW = 150, gap = 8, PILLW = 24;
        int y = 18;

        // No "Enter World" column anymore — destination is per-slot, dictated by
        // kind (Character → enters world, Account → charselect). Always.

        // Team rows
        (_cboTeam1A, _pillTeam1A, _cboTeam1B, _pillTeam1B) = AddTeamRow("Team 1:", L, I, CW, gap, PILLW, ref y);
        (_cboTeam2A, _pillTeam2A, _cboTeam2B, _pillTeam2B) = AddTeamRow("Team 2:", L, I, CW, gap, PILLW, ref y);
        (_cboTeam3A, _pillTeam3A, _cboTeam3B, _pillTeam3B) = AddTeamRow("Team 3:", L, I, CW, gap, PILLW, ref y);
        (_cboTeam4A, _pillTeam4A, _cboTeam4B, _pillTeam4B) = AddTeamRow("Team 4:", L, I, CW, gap, PILLW, ref y);
        (_cboTeam5A, _pillTeam5A, _cboTeam5B, _pillTeam5B) = AddTeamRow("Team 5:", L, I, CW, gap, PILLW, ref y);
        (_cboTeam6A, _pillTeam6A, _cboTeam6B, _pillTeam6B) = AddTeamRow("Team 6:", L, I, CW, gap, PILLW, ref y);

        // Legend \u2014 describes slot KIND only. Destination is the team's call,
        // controlled by the Enter World column header (with its own tooltip).
        // Kind + destination are independent dimensions; conflating them in
        // the legend was the source of confusion ("\u2713 = Character (enter world)"
        // wasn't always true when the team's Enter World was off).
        var legend = DarkTheme.AddLabel(this,
            "C = Character    A = Account    \u2717 = unresolved",
            L, y + 2);
        legend.ForeColor = DarkTheme.FgDimGray;
        legend.Font = DarkTheme.FontUI75;
        legend.AutoSize = true;
        y += 18;

        // Behavior hint \u2014 explains the kind\u2192destination rule users see in pills above.
        // Sits directly under the legend so the legend's "C/A/\u2717" symbols and the
        // sentence describing what they DO read as one paragraph.
        var behaviorHint = DarkTheme.AddLabel(this,
            "Characters enter world; Accounts stop at charselect. Orphan chars (no Account) render '(unassigned)'.",
            L, y);
        behaviorHint.ForeColor = DarkTheme.FgDimGray;
        behaviorHint.Font = DarkTheme.FontUI75;
        behaviorHint.AutoSize = true;
        y += 18;

        // Warning / contextual-hint label — gets its own row above the buttons
        // so it never overlaps Save/Cancel (was sharing y with buttons before).
        // Used for: same-login collision blocker, unresolved-slot blocker on
        // Save, and the descriptive "Account-only — character select" hint.
        var lblWarn = DarkTheme.AddLabel(this, "", L, y);
        lblWarn.ForeColor = DarkTheme.FgWarn;
        lblWarn.Font = DarkTheme.FontUI75;
        lblWarn.AutoSize = true;
        lblWarn.Visible = false;
        y += 14;

        var btnOK = DarkTheme.MakePrimaryButton("Save", L, y);
        btnOK.Width = 100;
        btnOK.Click += (_, _) =>
        {
            // Block unresolved slots — persisting a stale reference through Save would
            // leave the user stuck in the red-pill loop with no feedback at Save time.
            var slots = new[]
            {
                (_cboTeam1A, "Team 1 Slot 1"), (_cboTeam1B, "Team 1 Slot 2"),
                (_cboTeam2A, "Team 2 Slot 1"), (_cboTeam2B, "Team 2 Slot 2"),
                (_cboTeam3A, "Team 3 Slot 1"), (_cboTeam3B, "Team 3 Slot 2"),
                (_cboTeam4A, "Team 4 Slot 1"), (_cboTeam4B, "Team 4 Slot 2"),
                (_cboTeam5A, "Team 5 Slot 1"), (_cboTeam5B, "Team 5 Slot 2"),
                (_cboTeam6A, "Team 6 Slot 1"), (_cboTeam6B, "Team 6 Slot 2"),
            };
            var unresolved = slots
                .Where(s => s.Item1.Tag is string tag && !string.IsNullOrEmpty(tag))
                .Select(s => $"{s.Item2}: '{(s.Item1.Tag as string)}'")
                .ToList();
            if (unresolved.Count > 0)
            {
                lblWarn.Text = "\u26a0 Unresolved: " + string.Join("; ", unresolved) + " — clear (none) or pick a valid target";
                lblWarn.Visible = true;
                return;
            }

            // Block same Account (Username, Server) in both slots of the same team —
            // EQ kicks duplicate logins.
            var teams = new[]
            {
                (_cboTeam1A, _cboTeam1B, "Team 1"),
                (_cboTeam2A, _cboTeam2B, "Team 2"),
                (_cboTeam3A, _cboTeam3B, "Team 3"),
                (_cboTeam4A, _cboTeam4B, "Team 4"),
                (_cboTeam5A, _cboTeam5B, "Team 5"),
                (_cboTeam6A, _cboTeam6B, "Team 6"),
            };
            foreach (var (a, b, name) in teams)
            {
                var accA = ResolveAccountForSlot(a);
                var accB = ResolveAccountForSlot(b);
                if (accA == null || accB == null) continue;
                // Case-insensitive: EQ usernames are server-side case-insensitive,
                // so EQ kicks even if the user typed different cases per slot.
                if (accA.Username.Equals(accB.Username, StringComparison.OrdinalIgnoreCase) &&
                    accA.Server.Equals(accB.Server, StringComparison.OrdinalIgnoreCase))
                {
                    lblWarn.Text = $"\u26a0 {name}: both slots share login '{accA.Username}' — EQ will kick one";
                    lblWarn.Visible = true;
                    return;
                }
            }
            // Non-modal Show() doesn't auto-close on DialogResult set the way
            // ShowDialog() does — set the result THEN call Close() so the
            // FormClosed handler in SettingsForm fires with the right verdict.
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(btnOK);

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 130, y);
        btnCancel.Width = 100;
        btnCancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(btnCancel);

        // Select saved values (after event subscription so pills refresh).
        SelectByValue(_cboTeam1A, team1A);  SelectByValue(_cboTeam1B, team1B);
        SelectByValue(_cboTeam2A, team2A);  SelectByValue(_cboTeam2B, team2B);
        SelectByValue(_cboTeam3A, team3A);  SelectByValue(_cboTeam3B, team3B);
        SelectByValue(_cboTeam4A, team4A);  SelectByValue(_cboTeam4B, team4B);
        SelectByValue(_cboTeam5A, team5A);  SelectByValue(_cboTeam5B, team5B);
        SelectByValue(_cboTeam6A, team6A);  SelectByValue(_cboTeam6B, team6B);

        // v3.22.10: pair with SuspendLayout at top of ctor. performLayout: true
        // forces a single layout pass at the end rather than 38+ incremental
        // ones across the ctor body.
        ResumeLayout(performLayout: true);
    }

    /// <summary>
    /// v3.22.10: builds the SlotOption list used by every ComboBox in this dialog.
    /// Hoisted out of MakeCombo so the (none) + Characters + Accounts iteration
    /// runs once per ctor instead of 12× (one per combo).
    /// </summary>
    private List<SlotOption> BuildComboItems()
    {
        var items = new List<SlotOption>
        {
            new("", "(none)", SlotKind.None),
        };
        foreach (var c in _characters)
            items.Add(new SlotOption(c.Name, $"🧙  {c.Name}", SlotKind.Character));
        foreach (var a in _accounts)
        {
            // Don't duplicate: if Account.Name matches a Character.Name we've listed, skip.
            if (_characters.Any(c => c.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))) continue;
            // Display the Username (the login identity Nate recognizes); persist
            // the FK identity (Account.Name) as the SlotOption value so existing
            // saved team slots keep resolving via ResolveAccountForSlot. New
            // accounts have Name == Username (auto-shadowed in AccountEditDialog),
            // so display and persistence agree for fresh data.
            items.Add(new SlotOption(a.Name, $"🔑  {a.Username}", SlotKind.Account));
        }
        return items;
    }

    private (ComboBox a, Label pa, ComboBox b, Label pb) AddTeamRow(
        string label, int L, int I, int CW, int gap, int PILLW, ref int y)
    {
        DarkTheme.AddLabel(this, label, L, y + 3);
        var a = MakeCombo(I, y, CW);
        var pa = MakePill(I + CW + gap / 2, y + 2);
        var b = MakeCombo(I + CW + gap + PILLW + gap, y, CW);
        var pb = MakePill(I + CW + gap + PILLW + gap + CW + gap / 2, y + 2);
        a.SelectedIndexChanged += (_, _) => RefreshPill(a, pa);
        b.SelectedIndexChanged += (_, _) => RefreshPill(b, pb);
        y += 36;
        return (a, pa, b, pb);
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
        var cb = new ComboBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 24),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
        };
        // v3.22.10: use AddRange (single layout invalidation) instead of N\u00D7Add
        // (one per item). Items themselves are hoisted to _comboItems built
        // once per ctor.
        cb.Items.AddRange(_comboItems.Cast<object>().ToArray());
        cb.SelectedIndex = 0;

        if (_comboMaxW > width) cb.DropDownWidth = _comboMaxW;

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
            if (cbo.Items[i] is SlotOption opt && opt.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
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
            _ when cbo == _cboTeam5A => _pillTeam5A,
            _ when cbo == _cboTeam5B => _pillTeam5B,
            _ when cbo == _cboTeam6A => _pillTeam6A,
            _ when cbo == _cboTeam6B => _pillTeam6B,
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
            // Neutral kind indicator \u2014 was \u2713-green which read as "correct";
            // changed to C-on-blue so neither pill carries a value judgment.
            pill.Text = "C";
            pill.BackColor = DarkTheme.CardBlue;
            pill.ForeColor = Color.White;
            // Descriptive: pill describes KIND only. Destination depends on the
            // team's Enter World toggle (covered by its own header tooltip).
            _tooltip.SetToolTip(pill,
                "Resolves to Character — enters world when Enter World is on.");
        }
        else // Account
        {
            // Neutral kind indicator — was !-yellow which read as "warning";
            // changed to A-on-purple so accounts look like a valid type, not
            // an error state. Accounts working as intended ≠ warning.
            pill.Text = "A";
            pill.BackColor = DarkTheme.CardPurple;
            pill.ForeColor = Color.White;
            // Descriptive: states the constraint, doesn't push the user to
            // change their config (was "Pick a Character to enter world instead"
            // which contradicted users who deliberately set up account-only teams).
            _tooltip.SetToolTip(pill,
                "Resolves to Account — charselect only (Accounts cannot enter world).");
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
            var ch = _characters.FirstOrDefault(c => c.Name.Equals(opt.Value, StringComparison.OrdinalIgnoreCase));
            if (ch == null) return null;
            return _accounts.FirstOrDefault(a =>
                a.Username.Equals(ch.AccountUsername, StringComparison.OrdinalIgnoreCase) &&
                a.Server.Equals(ch.AccountServer, StringComparison.OrdinalIgnoreCase));
        }
        return _accounts.FirstOrDefault(a => a.Name.Equals(opt.Value, StringComparison.OrdinalIgnoreCase));
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
