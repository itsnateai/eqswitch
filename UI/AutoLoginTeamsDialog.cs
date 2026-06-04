// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Dialog for configuring Autologin Teams 1-12 (v3.22.58, 2026-05-27 — extended from 6 to 12).
/// Each team has 2 slots. Slot dropdowns list Characters (enter-world targets, preferred)
/// followed by Accounts (charselect-only fallback). A colored status pill next to each
/// slot indicates resolution: blue 'C' = Character, orange 'A' = Account-only, red ✗ = unresolved.
/// Dropdown rows are owner-painted the same way (Character blue, Account orange) — see DrawComboItem.
///
/// Teams 7-12 are tray-right-click-submenu only (no global hotkey binding, no trayclick
/// action dropdown entry) — by design per Nate 2026-05-27. The hotkey/trayclick firewall
/// keeps the General-tab dropdown bounded and the TeamHotkeysDialog rows fixed at 4.
///
/// Internal storage uses 4 parallel arrays of length 12 (combos A/B + pills A/B) keyed
/// by zero-based team index. Public property getters Team1Account1..Team12Account2 are
/// preserved for SettingsForm name-based consumption.
/// </summary>
internal sealed class AutoLoginTeamsDialog : EqSwitchForm
{
    private const int TeamCount = 12;

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

    private readonly ComboBox[] _cboA = new ComboBox[TeamCount];
    private readonly ComboBox[] _cboB = new ComboBox[TeamCount];
    private readonly Label[] _pillA = new Label[TeamCount];
    private readonly Label[] _pillB = new Label[TeamCount];
    private readonly System.Windows.Forms.ToolTip _tooltip = new();

    // Public per-team getters preserved for SettingsForm name-based readback.
    public string Team1Account1  => GetValue(_cboA[0]);
    public string Team1Account2  => GetValue(_cboB[0]);
    public string Team2Account1  => GetValue(_cboA[1]);
    public string Team2Account2  => GetValue(_cboB[1]);
    public string Team3Account1  => GetValue(_cboA[2]);
    public string Team3Account2  => GetValue(_cboB[2]);
    public string Team4Account1  => GetValue(_cboA[3]);
    public string Team4Account2  => GetValue(_cboB[3]);
    public string Team5Account1  => GetValue(_cboA[4]);
    public string Team5Account2  => GetValue(_cboB[4]);
    public string Team6Account1  => GetValue(_cboA[5]);
    public string Team6Account2  => GetValue(_cboB[5]);
    public string Team7Account1  => GetValue(_cboA[6]);
    public string Team7Account2  => GetValue(_cboB[6]);
    public string Team8Account1  => GetValue(_cboA[7]);
    public string Team8Account2  => GetValue(_cboB[7]);
    public string Team9Account1  => GetValue(_cboA[8]);
    public string Team9Account2  => GetValue(_cboB[8]);
    public string Team10Account1 => GetValue(_cboA[9]);
    public string Team10Account2 => GetValue(_cboB[9]);
    public string Team11Account1 => GetValue(_cboA[10]);
    public string Team11Account2 => GetValue(_cboB[10]);
    public string Team12Account1 => GetValue(_cboA[11]);
    public string Team12Account2 => GetValue(_cboB[11]);

    // v3.22.10: hoisted from MakeCombo (which rebuilt them N× per ctor).
    // Built once in ctor, used by every MakeCombo call. Saves N × (list build
    // + 5-6 TextRenderer.MeasureText calls) on a typical config.
    private List<SlotOption> _comboItems = null!;
    // v3.22.68: cached object[] for ComboBox.Items.AddRange. The list itself is
    // hoisted but each MakeCombo call previously did .Cast<object>().ToArray(),
    // allocating a fresh array per combo — 23 redundant array allocations across
    // 24 combos. Allocate once, reuse N times.
    private object[] _comboItemsArray = null!;
    private int _comboMaxW;

    public AutoLoginTeamsDialog(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Character> characters,
        string team1A,  string team1B,
        string team2A,  string team2B,
        string team3A,  string team3B,
        string team4A,  string team4B,
        string team5A,  string team5B,
        string team6A,  string team6B,
        string team7A,  string team7B,
        string team8A,  string team8B,
        string team9A,  string team9B,
        string team10A, string team10B,
        string team11A, string team11B,
        string team12A, string team12B)
    {
        _accounts = accounts;
        _characters = characters;

        // v3.22.10: SuspendLayout for the whole ctor. WinForms invalidates the
        // form on every control Add; without suspension the layout engine
        // re-runs 60+ times (24 combos + 24 pills + 12 row labels + legend +
        // hint + warn + 2 buttons). Combined with the items-list hoist below,
        // this cuts the perceived "loading Teams" freeze meaningfully.
        SuspendLayout();

        // v3.22.68: opt this form into double-buffered painting at the Control
        // level. Reduces the visible flicker as ~65 child controls (24 combos,
        // 24 pills, 12 row labels, legend, hint, warn, 2 buttons) draw their
        // initial paint after the ctor's SuspendLayout/ResumeLayout window
        // releases on first Show.
        // v3.22.69: dropped the WS_EX_COMPOSITED CreateParams override — T2
        // verifiers (Sonnet+Opus convergent) flagged it as known to produce
        // stale/black combo dropdowns on pre-Win11 / Win10 < 21H2 DWM,
        // exacerbated by FlatStyle.Flat ComboBoxes (which use their own
        // layered render path). With 24 dropdowns in this dialog and the
        // bottom-of-form combos opening upward over the composited region,
        // the worst-case is "user clicks Configure Teams, opens Team 12
        // dropdown, sees black rectangle" — strictly worse than the
        // pre-v3.22.68 flicker. SetStyle alone (form-background DBO)
        // remains as the safer, ComboBox-compatible flicker reducer.
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
        UpdateStyles();

        // Restore last-open position if available; otherwise center on parent.
        if (_lastLocation.HasValue)
        {
            StartPosition = FormStartPosition.Manual;
            Location = _lastLocation.Value;
        }
        else
        {
            StartPosition = FormStartPosition.Manual;
            DarkTheme.CenterOnOwnerOnLoad(this);
        }
        FormClosing += (_, _) => _lastLocation = Location;
        // Width 480 (unchanged from 6-team layout). Height 560 fits 12 team
        // rows (12 × 36 = 432) + legend (18) + behavior hint (18) + warn (14)
        // + button row (~30) + top/bottom padding (~48).
        DarkTheme.StyleForm(this, "Autologin Teams", new Size(480, 560));
        MinimizeBox = false;

        // v3.22.10: build the combo items list ONCE here. Every ComboBox in
        // this dialog displays the same options (24 of them now); rebuilding
        // the list per MakeCombo call was pure waste. MeasureText runs once
        // over the unified list and the result is reused for every combo's
        // DropDownWidth.
        _comboItems = BuildComboItems();
        _comboItemsArray = _comboItems.Cast<object>().ToArray();
        using (var probeFont = new Font(Font.FontFamily, Font.Size))
        {
            _comboMaxW = _comboItems.Max(i => TextRenderer.MeasureText(i.Display, probeFont).Width) + 16;
        }

        const int L = 15, I = 80, CW = 150, gap = 8, PILLW = 24;
        int y = 18;

        // No "Enter World" column anymore — destination is per-slot, dictated by
        // kind (Character → enters world, Account → charselect). Always.

        // Team rows — 12 of them.
        for (int i = 0; i < TeamCount; i++)
        {
            var (a, pa, b, pb) = AddTeamRow($"Team {i + 1}:", L, I, CW, gap, PILLW, ref y);
            _cboA[i] = a; _pillA[i] = pa;
            _cboB[i] = b; _pillB[i] = pb;
        }

        // Legend — describes slot KIND only. Destination is dictated by kind
        // (Character → enter world, Account → charselect).
        var legend = DarkTheme.AddLabel(this,
            "C = Character    A = Account    ✗ = unresolved",
            L, y + 2);
        legend.ForeColor = DarkTheme.FgDimGray;
        legend.Font = DarkTheme.FontUI75;
        legend.AutoSize = true;
        y += 18;

        // Behavior hint — explains the kind→destination rule users see in pills above.
        var behaviorHint = DarkTheme.AddLabel(this,
            "Characters enter world; Accounts stop at charselect.",
            L, y);
        behaviorHint.ForeColor = DarkTheme.FgDimGray;
        behaviorHint.Font = DarkTheme.FontUI75;
        behaviorHint.AutoSize = true;
        y += 18;

        // Warning / contextual-hint label — gets its own row above the buttons
        // so it never overlaps Save/Cancel. Used for same-login collision blocker
        // and unresolved-slot blocker on Save.
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
            var unresolved = new List<string>();
            for (int i = 0; i < TeamCount; i++)
            {
                if (_cboA[i].Tag is string ta && !string.IsNullOrEmpty(ta))
                    unresolved.Add($"Team {i + 1} Slot 1: '{ta}'");
                if (_cboB[i].Tag is string tb && !string.IsNullOrEmpty(tb))
                    unresolved.Add($"Team {i + 1} Slot 2: '{tb}'");
            }
            if (unresolved.Count > 0)
            {
                lblWarn.Text = "⚠ Unresolved: " + string.Join("; ", unresolved) + " — clear (none) or pick a valid target";
                lblWarn.Visible = true;
                return;
            }

            // Block same Account (Username, Server) in both slots of the same team —
            // EQ kicks duplicate logins.
            for (int i = 0; i < TeamCount; i++)
            {
                var accA = ResolveAccountForSlot(_cboA[i]);
                var accB = ResolveAccountForSlot(_cboB[i]);
                if (accA == null || accB == null) continue;
                // Case-insensitive: EQ usernames are server-side case-insensitive,
                // so EQ kicks even if the user typed different cases per slot.
                if (accA.Username.Equals(accB.Username, StringComparison.OrdinalIgnoreCase) &&
                    accA.Server.Equals(accB.Server, StringComparison.OrdinalIgnoreCase))
                {
                    lblWarn.Text = $"⚠ Team {i + 1}: both slots share login '{accA.Username}' — EQ will kick one";
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
        var saved = new[]
        {
            (team1A,  team1B),  (team2A,  team2B),  (team3A,  team3B),  (team4A,  team4B),
            (team5A,  team5B),  (team6A,  team6B),  (team7A,  team7B),  (team8A,  team8B),
            (team9A,  team9B),  (team10A, team10B), (team11A, team11B), (team12A, team12B),
        };
        for (int i = 0; i < TeamCount; i++)
        {
            SelectByValue(_cboA[i], saved[i].Item1);
            SelectByValue(_cboB[i], saved[i].Item2);
        }

        // v3.22.10: pair with SuspendLayout at top of ctor. performLayout: true
        // forces a single layout pass at the end rather than 60+ incremental
        // ones across the ctor body.
        ResumeLayout(performLayout: true);
    }

    /// <summary>
    /// v3.22.10: builds the SlotOption list used by every ComboBox in this dialog.
    /// Hoisted out of MakeCombo so the (none) + Characters + Accounts iteration
    /// runs once per ctor instead of 24× (one per combo).
    /// </summary>
    private List<SlotOption> BuildComboItems()
    {
        var items = new List<SlotOption>
        {
            new("", "(none)", SlotKind.None),
        };
        foreach (var c in _characters)
            items.Add(new SlotOption(QuickLoginSlot.ForCharacter(c.Name), $"🧙  {c.Name}", SlotKind.Character));
        // v3.24.15: NO name-dedup. A Character (enter-world) and an Account (charselect) that share a
        // name are DISTINCT intents — list both, exactly like QuickLoginSlotsDialog. Typed values
        // (char:Name / acct:Name) keep them apart on save + dispatch, so e.g. character "Eisley" and
        // account "eisley" no longer collapse into one entry. Display the Username (the login identity
        // Nate recognizes); persist the typed Account.Name value so SelectByValue / ResolveAccountForSlot
        // and the FireTeam resolver route deterministically.
        foreach (var a in _accounts)
            items.Add(new SlotOption(QuickLoginSlot.ForAccount(a.Name), $"🔑  {a.Username}", SlotKind.Account));
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
            // Owner-draw so Character rows render blue and Account rows orange
            // (matches the C/A pills + the rest of the app). Mirrors the proven
            // QuickLoginSlotsDialog pattern; independent of the WS_EX_COMPOSITED
            // path that was dropped in v3.22.69.
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 18,
        };
        // v3.22.10: use AddRange (single layout invalidation) instead of N×Add
        // (one per item). Items themselves are hoisted to _comboItems built
        // once per ctor. v3.22.68: array is now also cached (was allocated
        // per-call via .Cast<object>().ToArray()) — saves 23 array allocations
        // across the 24-combo ctor.
        cb.Items.AddRange(_comboItemsArray);
        cb.SelectedIndex = 0;
        cb.DrawItem += DrawComboItem;

        if (_comboMaxW > width) cb.DropDownWidth = _comboMaxW;

        cb.MouseWheel += (_, e) => ((HandledMouseEventArgs)e).Handled = true;
        Controls.Add(cb);
        return cb;
    }

    /// <summary>
    /// Owner-paint: Character entries blue (FgCharacterBlue), Account entries
    /// orange (FgAccountOrange), (none) dimmed. Paints both the closed selection box
    /// and the dropdown rows; the system highlight (DrawBackground) reads fine under
    /// either color. Mirrors QuickLoginSlotsDialog.DrawComboItem.
    /// </summary>
    private static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox cb) return;
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= cb.Items.Count)
        {
            e.DrawFocusRectangle();
            return;
        }
        var opt = cb.Items[e.Index] as SlotOption;
        // On the highlighted dropdown row the system paints SystemColors.Highlight
        // (blue) behind the text — drawing the blue Character color there would be
        // blue-on-blue. Use white for the selected row; identity color otherwise
        // (incl. the closed box, which is ComboBoxEdit state, not Selected).
        Color fg;
        if ((e.State & DrawItemState.Selected) != 0)
        {
            fg = DarkTheme.FgWhite;
        }
        else
        {
            fg = opt?.Kind switch
            {
                SlotKind.Account   => DarkTheme.FgAccountOrange,
                SlotKind.Character => DarkTheme.FgCharacterBlue,
                _                  => DarkTheme.FgDimGray, // (none)
            };
        }
        TextRenderer.DrawText(e.Graphics, opt?.Display ?? "", e.Font ?? cb.Font, e.Bounds, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        e.DrawFocusRectangle();
    }

    private void SelectByValue(ComboBox cbo, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            cbo.SelectedIndex = 0;
            RefreshPillForCombo(cbo);
            return;
        }
        // Exact typed match first (the v3.24.15 storage format: char:Name / acct:Name).
        for (int i = 0; i < cbo.Items.Count; i++)
        {
            if (cbo.Items[i] is SlotOption opt && opt.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                cbo.SelectedIndex = i;
                RefreshPillForCombo(cbo);
                return;
            }
        }
        // Legacy bare name (pre-v3.24.15 saved team slots): match the un-prefixed name against a
        // Character or Account option. Character wins ties — the same enter-world-first preference
        // the launch-time resolver (TrayManager.FireTeam's LegacyBare branch) applies, so a bare
        // slot keeps resolving to exactly what it fired before.
        if (QuickLoginSlot.Parse(value).Kind == QuickLoginSlot.Kind.LegacyBare)
        {
            int charMatch = -1, acctMatch = -1;
            for (int i = 0; i < cbo.Items.Count; i++)
            {
                if (cbo.Items[i] is not SlotOption opt || opt.Kind == SlotKind.None) continue;
                var (_, name) = QuickLoginSlot.Parse(opt.Value);
                if (!name.Equals(value, StringComparison.OrdinalIgnoreCase)) continue;
                if (opt.Kind == SlotKind.Character && charMatch < 0) charMatch = i;
                else if (opt.Kind == SlotKind.Account && acctMatch < 0) acctMatch = i;
            }
            int pick = charMatch >= 0 ? charMatch : acctMatch;
            if (pick >= 0) { cbo.SelectedIndex = pick; RefreshPillForCombo(cbo); return; }
        }
        // Unresolved — keep (none) selected but surface via status pill.
        cbo.SelectedIndex = 0;
        // Stash the unresolved string on the combo's Tag so RefreshPill can read it.
        cbo.Tag = value;
        RefreshPillForCombo(cbo);
    }

    private void RefreshPillForCombo(ComboBox cbo)
    {
        for (int i = 0; i < TeamCount; i++)
        {
            if (cbo == _cboA[i]) { RefreshPill(cbo, _pillA[i]); return; }
            if (cbo == _cboB[i]) { RefreshPill(cbo, _pillB[i]); return; }
        }
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
                pill.Text = "✗";
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
            pill.Text = "C";
            pill.BackColor = DarkTheme.FgCharacterBlue;
            pill.ForeColor = Color.White;
            _tooltip.SetToolTip(pill,
                "Resolves to Character — enters world when Enter World is on.");
        }
        else // Account
        {
            pill.Text = "A";
            pill.BackColor = DarkTheme.FgAccountOrange;
            // Dark glyph on the bright-orange badge — white on FgAccountOrange is
            // only ~1.9:1 contrast; BgDark reads cleanly (~13:1). The blue 'C'
            // pill keeps white (white-on-blue is comfortable).
            pill.ForeColor = DarkTheme.BgDark;
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
        // opt.Value is typed (char:Name / acct:Name) — parse to the bare entity name for lookup.
        var (_, name) = QuickLoginSlot.Parse(opt.Value);
        if (opt.Kind == SlotKind.Character)
        {
            var ch = _characters.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (ch == null) return null;
            return _accounts.FirstOrDefault(a =>
                a.Username.Equals(ch.AccountUsername, StringComparison.OrdinalIgnoreCase) &&
                a.Server.Equals(ch.AccountServer, StringComparison.OrdinalIgnoreCase));
        }
        return _accounts.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
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

    // v3.22.68: WS_EX_COMPOSITED CreateParams override removed in v3.22.69
    // — T2 verifiers (Sonnet+Opus convergent) flagged stale/black ComboBox
    // dropdown rendering on pre-Win11 / Win10 < 21H2 DWM, especially with
    // FlatStyle.Flat combos opening upward over the composited region. Form
    // base CreateParams is fine without it; SetStyle in the ctor still gives
    // form-background DBO. The flicker reduction is partial vs the original
    // v3.22.68 design, but the regression risk on combo dropdowns is
    // unacceptable for the 24-combo layout this dialog uses.
}
