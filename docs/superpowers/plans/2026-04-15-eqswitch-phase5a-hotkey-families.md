# EQSwitch Phase 5a Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the user-facing half of the master plan's Phase 5 — `AccountHotkeys` / `CharacterHotkeys` family-table dialogs, Direct Bindings card on the Hotkeys tab, Alt+M lock-gate removal, stale-binding Rebind dropdown, and runtime dispatch wiring — in the shipped DarkTheme aesthetic.

**Architecture:** Main Hotkeys tab stays fixed-height; heavy lifting moves into two new modal dialogs (`AccountHotkeysDialog` / `CharacterHotkeysDialog`) that render one row per `_config.Accounts` / `_config.Characters` entry with a `MakeHotkeyBox` per row. Stale bindings (TargetName doesn't resolve) render as a red row with an inline `Rebind → ▾` dropdown. Dispatch in `TrayManager.RegisterHotkeys` loops over `HotkeyConfig.AccountHotkeys` / `CharacterHotkeys` and registers each via the existing name-based `HotkeyManager.Register` API — skips entries with empty Combo or TargetName (Phase 1 migration padding contract).

**Tech Stack:** C# 12 / .NET 8 WinForms, existing `DarkTheme` factories, `MakeHotkeyBox` helper for hotkey input, `HotkeyManager.Register(string, Action)` name-based registration, `bash _tests/migration/run_fixtures.sh` harness for config-layer verification.

**Spec:** [`docs/superpowers/specs/2026-04-15-eqswitch-phase5a-hotkey-families-design.md`](../specs/2026-04-15-eqswitch-phase5a-hotkey-families-design.md)

**Parent plan:** [`PLAN_account_character_split.md`](../../../PLAN_account_character_split.md)

**Prior state:** HEAD `01de047` (Phase 5a spec commit) — Phase 4 impl ended at `01d05ed`. Build: 0 errors, 1 expected `[Obsolete]` warning at `TrayManager.cs:1648` (`ExecuteQuickLogin` — Phase 6 deletes). 9 migration fixtures pass. Phantom-click gates intact (2 / 1).

---

## File structure

| File | Role | Change |
|---|---|---|
| `UI/TrayManager.cs` | Tray orchestration | Alt+M gate removal + family-table registration loop + 2 new Fire helpers + ReloadConfig sync line for `HotkeysLegacyBannerDismissed` (Tasks 1, 2, 6) |
| `UI/SettingsForm.cs` | 6-tab settings GUI | Hotkeys tab layout rewrite (drop legacy Quick Login + Auto-Login cards, add Direct Bindings card + legacy banner), P3.5-D conflict scan extension (Tasks 5, 7) |
| `Config/AppConfig.cs` | Root config | Top-level `HotkeysLegacyBannerDismissed: bool` field (Task 2) |
| `Config/HotkeyBindingUtil.cs` | **NEW** | stale-binding counters + live-binding-label helper (Task 2) |
| `UI/AccountHotkeysDialog.cs` | **NEW** | Modal for editing AccountHotkeys (Task 3) |
| `UI/CharacterHotkeysDialog.cs` | **NEW** | Modal for editing CharacterHotkeys (Task 4) |
| `Models/HotkeyBinding.cs` | Existing v4 type (no change) | 0 |
| `Models/Account.cs` / `Character.cs` | Existing (no change) | 0 |
| `Core/HotkeyManager.cs` | Existing name-based API (no change) | 0 |

Native DLLs, migration fixtures, phantom-click gates: **no changes**. Any diff to `Native/mq2_bridge.cpp`, `Core/AutoLoginManager.cs`, or the `_tests/migration/` tree is a bug — stop and investigate.

Total net delta: ~+760 lines across 2 new files + 3 modified; legacy SettingsForm Quick Login + Auto-Login cards (~120 lines) deleted.

---

## Conventions

- Every edit shows exact line numbers OR a unique anchor string from the current code.
- C# emoji uses `\u`-escape surrogate pairs (e.g., `"\uD83D\uDD11"` for 🔑) matching the existing codebase pattern.
- Every commit stages specific files — never `git add -A`.
- Conventional-commit titles under 72 chars. Bodies under 72-char columns.
- Expected build state after each task: `0 Error(s)`, exactly 1 `[Obsolete]` warning at `TrayManager.cs:~1648` (`ExecuteQuickLogin`). Any deviation is a bug.
- Test commands run from `X:/_Projects/EQSwitch/` (or via `cd X:/_Projects/EQSwitch &&`).
- Phantom-click gates (`grep -c "gameState == 5" Native/mq2_bridge.cpp` == 2, `grep -c "result == -2" Core/AutoLoginManager.cs` == 1) re-verified after every task.
- Migration fixtures (`bash _tests/migration/run_fixtures.sh` → `9 passed, 0 failed`) re-verified after every task.
- `HotkeyBinding` has two properties: `Combo: string` (default `""`) and `TargetName: string` (default `""`). Empty `Combo` OR empty `TargetName` = migration padding — skip during registration + conflict scan, do NOT count as "stale."

---

## Task 1 — Alt+M lock-gate removal

**Files:**
- Modify: `UI/TrayManager.cs` — `OnToggleMultiMonitor` method (search anchor `"Hotkey locked until user has tried"`)
- Modify: `UI/SettingsForm.cs` — the `MultiMonitorEnabled` OR-expression inside the `BuildAppConfig` Hotkeys block (search anchor `"_chkVideoMultiMon.Checked || _config.Hotkeys.MultiMonitorEnabled"`)

Spec §1. Standalone commit — safe to ship before the rest of Phase 5a.

- [ ] **Step 1.1: Locate OnToggleMultiMonitor**

Run:
```bash
grep -n "private void OnToggleMultiMonitor" X:/_Projects/EQSwitch/UI/TrayManager.cs
```
Expected: one match, e.g. `584:    private void OnToggleMultiMonitor()`. Note the line.

- [ ] **Step 1.2: Remove the gate block**

Open `UI/TrayManager.cs`. Find the `OnToggleMultiMonitor` body. The current first statements are:

```csharp
    private void OnToggleMultiMonitor()
    {
        // Hotkey locked until user has tried multimonitor at least once via Settings
        if (!_config.Hotkeys.MultiMonitorEnabled)
        {
            FileLogger.Info("ToggleMultiMonitor: not yet unlocked — enable in Settings first");
            ShowBalloon("Enable Multi-Monitor mode in Settings first");
            return;
        }

        long now = Environment.TickCount64;
```

Replace with:

```csharp
    private void OnToggleMultiMonitor()
    {
        // Phase 5a: the first-time-use gate was removed. Any bound combo fires the
        // toggle directly — the hotkey-conflict modal (P3.5-D) already blocks duplicate
        // bindings at config time, and every other hotkey operates this way. The
        // MultiMonitorEnabled bool stays on HotkeyConfig for downgrade safety but is
        // no longer consulted at dispatch time. Phase 6 deletes the field.

        long now = Environment.TickCount64;
```

- [ ] **Step 1.3: Stop writing `MultiMonitorEnabled` in SettingsForm**

Run:
```bash
grep -n "MultiMonitorEnabled = " X:/_Projects/EQSwitch/UI/SettingsForm.cs
```
Expected: one match inside `BuildAppConfig`'s Hotkeys block, e.g.:
```
1213:                MultiMonitorEnabled = _chkVideoMultiMon.Checked || _config.Hotkeys.MultiMonitorEnabled,
```

Replace that single line with:
```csharp
                // Phase 5a: MultiMonitorEnabled gate removed. Preserve existing value for
                // downgrade safety; SettingsForm no longer writes this field.
                MultiMonitorEnabled = _config.Hotkeys.MultiMonitorEnabled,
```

- [ ] **Step 1.4: Build + gates + fixtures**

Run:
```bash
cd X:/_Projects/EQSwitch && dotnet build --no-incremental 2>&1 | tail -6
```
Expected: `0 Error(s)`, exactly 1 `[Obsolete]` warning at `TrayManager.cs:~1648`.

Run:
```bash
echo "gameState==5: $(grep -c 'gameState == 5' X:/_Projects/EQSwitch/Native/mq2_bridge.cpp) (expect 2)"
echo "result==-2: $(grep -c 'result == -2' X:/_Projects/EQSwitch/Core/AutoLoginManager.cs) (expect 1)"
cd X:/_Projects/EQSwitch && bash _tests/migration/run_fixtures.sh 2>&1 | tail -3
```
Expected: `2`, `1`, `Migration fixtures: 9 passed, 0 failed`.

- [ ] **Step 1.5: Commit**

```bash
cd X:/_Projects/EQSwitch
git add UI/TrayManager.cs UI/SettingsForm.cs
git commit -m "$(cat <<'EOF'
fix(tray): remove Alt+M first-time-use gate

The Hotkeys.MultiMonitorEnabled field used to lock Alt+M behind a
one-time Settings checkbox — if the user had never enabled multi-mon
via Settings, pressing Alt+M just balloon'd 'Enable Multi-Monitor
mode in Settings first' and returned. The rationale (prevent
accidental fires on single-monitor rigs) never matched how other
hotkeys operate, and Phase 3.5-D's hotkey-conflict modal already
catches duplicate bindings at Save time.

Drop the gate. Alt+M fires the toggle directly when bound. Field
stays on HotkeyConfig for downgrade compat; SettingsForm no longer
writes it. Phase 6 deletes the field entirely.

EOF
)"
```

Expected: `[main <sha>] fix(tray): remove Alt+M first-time-use gate`.

---

## Task 2 — HotkeyBindingUtil + HotkeysLegacyBannerDismissed + ReloadConfig sync

**Files:**
- Create: `Config/HotkeyBindingUtil.cs`
- Modify: `Config/AppConfig.cs` — add top-level `HotkeysLegacyBannerDismissed: bool = false` near other top-level bools
- Modify: `UI/TrayManager.cs` — add `_config.HotkeysLegacyBannerDismissed = newConfig.HotkeyLegacyBannerDismissed;` line inside `ReloadConfig`'s hand-copy block

Spec §5. No callers yet; the helpers + field exist for Tasks 3 + 5 to consume.

- [ ] **Step 2.1: Create HotkeyBindingUtil.cs**

Create `X:/_Projects/EQSwitch/Config/HotkeyBindingUtil.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using EQSwitch.Models;

namespace EQSwitch.Config;

/// <summary>
/// Phase 5a helpers for the AccountHotkeys / CharacterHotkeys family tables.
/// Counts + classifies bindings so the Hotkeys-tab Direct Bindings card and the
/// AccountHotkeysDialog / CharacterHotkeysDialog share one definition of "live"
/// vs "stale" vs "unbound (migration padding)."
///
/// Contract (per ConfigVersionMigrator.EnsureSize invariant):
///   - Empty Combo OR empty TargetName = migration padding — positional placeholder.
///     NOT counted as stale or live; skip during registration.
///   - Non-empty Combo + TargetName that resolves to an existing Account/Character = live.
///   - Non-empty Combo + TargetName that does NOT resolve = stale (user action required).
/// </summary>
public static class HotkeyBindingUtil
{
    /// <summary>True if the binding has both a Combo and a TargetName. Padding returns false.</summary>
    public static bool IsPopulated(HotkeyBinding b) =>
        !string.IsNullOrEmpty(b.Combo) && !string.IsNullOrEmpty(b.TargetName);

    /// <summary>Count of populated AccountHotkeys whose TargetName resolves to an Account.</summary>
    public static int CountLiveAccountBindings(AppConfig cfg) =>
        cfg.Hotkeys.AccountHotkeys.Count(b =>
            IsPopulated(b) &&
            cfg.Accounts.Any(a => a.Name.Equals(b.TargetName, StringComparison.Ordinal)));

    /// <summary>Count of populated CharacterHotkeys whose TargetName resolves to a Character.</summary>
    public static int CountLiveCharacterBindings(AppConfig cfg) =>
        cfg.Hotkeys.CharacterHotkeys.Count(b =>
            IsPopulated(b) &&
            cfg.Characters.Any(c => c.Name.Equals(b.TargetName, StringComparison.Ordinal)));

    /// <summary>Count of populated AccountHotkeys whose TargetName doesn't resolve.</summary>
    public static int CountStaleAccountBindings(AppConfig cfg) =>
        cfg.Hotkeys.AccountHotkeys.Count(b =>
            IsPopulated(b) &&
            !cfg.Accounts.Any(a => a.Name.Equals(b.TargetName, StringComparison.Ordinal)));

    /// <summary>Count of populated CharacterHotkeys whose TargetName doesn't resolve.</summary>
    public static int CountStaleCharacterBindings(AppConfig cfg) =>
        cfg.Hotkeys.CharacterHotkeys.Count(b =>
            IsPopulated(b) &&
            !cfg.Characters.Any(c => c.Name.Equals(b.TargetName, StringComparison.Ordinal)));

    /// <summary>
    /// Enumerate all populated bindings across both families with a human label suitable
    /// for conflict-detection modals. Used by SettingsForm.ApplySettings + dialogs' Save
    /// paths to extend the P3.5-D scan to family-table entries.
    /// </summary>
    public static IEnumerable<(string label, string combo)> EnumeratePopulatedLabeled(AppConfig cfg)
    {
        foreach (var b in cfg.Hotkeys.AccountHotkeys)
            if (IsPopulated(b))
                yield return ($"Account '{b.TargetName}'", b.Combo);
        foreach (var b in cfg.Hotkeys.CharacterHotkeys)
            if (IsPopulated(b))
                yield return ($"Character '{b.TargetName}'", b.Combo);
    }
}
```

- [ ] **Step 2.2: Add HotkeysLegacyBannerDismissed field to AppConfig**

Open `X:/_Projects/EQSwitch/Config/AppConfig.cs`. Find the top-level `RunAtStartup` field (line ~123 per the file structure):

```bash
grep -n "public bool RunAtStartup" X:/_Projects/EQSwitch/Config/AppConfig.cs
```

Replace that single line's block with:

```csharp
    public bool RunAtStartup { get; set; } = false;

    /// <summary>
    /// Phase 5a: once true, the one-time "Quick Login slots are now under Direct
    /// Bindings" deprecation banner on the Hotkeys tab stays hidden. Flipped by the
    /// banner's Dismiss button. Ignored by runtime dispatch. Persists across sessions.
    /// </summary>
    public bool HotkeysLegacyBannerDismissed { get; set; } = false;
```

- [ ] **Step 2.3: Sync HotkeysLegacyBannerDismissed in ReloadConfig**

Open `X:/_Projects/EQSwitch/UI/TrayManager.cs`. Find the `ReloadConfig` hand-copy block — search for the line that copies `RunAtStartup`:

```bash
grep -n "_config.RunAtStartup = newConfig.RunAtStartup" X:/_Projects/EQSwitch/UI/TrayManager.cs
```

Expected: one match. Replace that exact line with two lines:

```csharp
        _config.RunAtStartup = newConfig.RunAtStartup;
        _config.HotkeysLegacyBannerDismissed = newConfig.HotkeysLegacyBannerDismissed;
```

This is the same fix-pattern as the Phase 4 TogglePip roundtrip bug — miss this copy and the banner dismiss state would silently vanish on Apply.

- [ ] **Step 2.4: Copy HotkeysLegacyBannerDismissed in SettingsForm.BuildAppConfig**

Open `X:/_Projects/EQSwitch/UI/SettingsForm.cs`. Find the `RunAtStartup = _chkRunAtStartup.Checked,` line inside `BuildAppConfig`:

```bash
grep -n "RunAtStartup = _chkRunAtStartup" X:/_Projects/EQSwitch/UI/SettingsForm.cs
```

Expected: one match in the `new AppConfig` initializer. Replace with:

```csharp
            RunAtStartup = _chkRunAtStartup.Checked,
            HotkeysLegacyBannerDismissed = _config.HotkeysLegacyBannerDismissed,   // Phase 5a: passthrough; flipped by banner Dismiss click, not a UI control
```

- [ ] **Step 2.5: Build + gates + fixtures**

```bash
cd X:/_Projects/EQSwitch && dotnet build --no-incremental 2>&1 | tail -6
echo "gameState==5: $(grep -c 'gameState == 5' Native/mq2_bridge.cpp)"
echo "result==-2: $(grep -c 'result == -2' Core/AutoLoginManager.cs)"
bash _tests/migration/run_fixtures.sh 2>&1 | tail -3
```
Expected: `0 Error(s)`, `1 Warning(s)`, `2`, `1`, `9 passed, 0 failed`.

- [ ] **Step 2.6: Commit**

```bash
cd X:/_Projects/EQSwitch
git add Config/HotkeyBindingUtil.cs Config/AppConfig.cs UI/TrayManager.cs UI/SettingsForm.cs
git commit -m "$(cat <<'EOF'
feat(config): HotkeyBindingUtil + HotkeysLegacyBannerDismissed field

Groundwork for Phase 5a's family-table consumers (Tasks 3, 5, 7).
No callers yet — this commit is the shared definition.

HotkeyBindingUtil: five static helpers classifying HotkeyBinding
entries per the Phase 1 migration contract (empty Combo or empty
TargetName = positional padding, never stale). CountLive* /
CountStale* feed the Direct Bindings card count display;
EnumeratePopulatedLabeled feeds the P3.5-D conflict scan extension.

HotkeysLegacyBannerDismissed: top-level bool on AppConfig for the
one-time 'Quick Login slots moved' banner (Task 5). Synced through
TrayManager.ReloadConfig's hand-copy block — same pattern used for
every other field; missing this copy was the Phase 4 TogglePip bug.
SettingsForm.BuildAppConfig passes the field through unchanged;
it's written only by the banner's Dismiss click in Task 5.

EOF
)"
```

---

## Task 3 — AccountHotkeysDialog

**Files:**
- Create: `UI/AccountHotkeysDialog.cs`

Spec §3. Uses `DarkTheme.StyleForm` + `DarkTheme.MakeCard` + `MakeHotkeyBox` style — but since `MakeHotkeyBox` is a private helper on `SettingsForm`, the dialog reimplements the same TextBox construction inline. Matches the shipped aesthetic.

- [ ] **Step 3.1: Create AccountHotkeysDialog.cs**

Create `X:/_Projects/EQSwitch/UI/AccountHotkeysDialog.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Phase 5a modal for editing AccountHotkeys. One row per _config.Accounts entry
/// (live), plus any stale AccountHotkeys whose TargetName no longer resolves
/// (red row with Rebind dropdown). Save atomically replaces the caller's
/// pending binding list.
/// </summary>
public sealed class AccountHotkeysDialog : Form
{
    private readonly IReadOnlyList<Account> _accounts;
    private readonly List<(string TargetName, TextBox HotkeyBox)> _liveRows = new();
    private readonly List<(string TargetName, TextBox HotkeyBox, ComboBox RebindCombo)> _staleRows = new();
    private readonly IReadOnlyList<(string label, string combo)> _otherHotkeys;

    /// <summary>Result of the dialog. Null until DialogResult.OK.</summary>
    public List<HotkeyBinding>? Result { get; private set; }

    /// <summary>
    /// <paramref name="accounts"/> = live Accounts (one row each).
    /// <paramref name="currentBindings"/> = HotkeyConfig.AccountHotkeys snapshot.
    /// <paramref name="otherHotkeys"/> = every OTHER hotkey currently bound in the config
    /// (other dialog's family table, tab-level hotkeys, Team hotkeys) for conflict detection.
    /// Each tuple = (human label, combo string) such as ("Team 1", "Alt+M").
    /// </summary>
    public AccountHotkeysDialog(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<HotkeyBinding> currentBindings,
        IReadOnlyList<(string label, string combo)> otherHotkeys)
    {
        _accounts = accounts;
        _otherHotkeys = otherHotkeys;

        // Classify current bindings: live (TargetName matches an Account) vs stale.
        var byTargetName = new Dictionary<string, string>(StringComparer.Ordinal);
        var staleBindings = new List<HotkeyBinding>();
        foreach (var b in currentBindings)
        {
            if (!HotkeyBindingUtil.IsPopulated(b)) continue;
            bool resolves = accounts.Any(a => a.Name.Equals(b.TargetName, StringComparison.Ordinal));
            if (resolves) byTargetName[b.TargetName] = b.Combo;
            else staleBindings.Add(b);
        }

        int rowCount = accounts.Count + staleBindings.Count;
        int bodyHeight = 80 + rowCount * 30;
        int formHeight = Math.Min(140 + bodyHeight, 540);

        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.StyleForm(this, "Account Hotkeys", new Size(460, formHeight));

        var card = DarkTheme.MakeCard(this,
            "\uD83D\uDD11",
            "Direct Account Hotkeys",
            DarkTheme.CardGold,
            10, 10, 430, bodyHeight + 30);

        int cy = 32;

        if (accounts.Count == 0 && staleBindings.Count == 0)
        {
            var lblEmpty = DarkTheme.AddCardHint(card,
                "No accounts yet — add one via Settings \u2192 Accounts first.",
                10, cy);
            lblEmpty.Size = new Size(410, 40);
        }
        else
        {
            // Stale rows first (red, with Rebind dropdown).
            foreach (var stale in staleBindings.OrderBy(b => b.TargetName, StringComparer.Ordinal))
            {
                AddStaleRow(card, 10, cy, stale);
                cy += 30;
            }

            // Live rows in Accounts list order.
            foreach (var a in accounts)
            {
                byTargetName.TryGetValue(a.Name, out var currentCombo);
                AddLiveRow(card, 10, cy, a.Name, currentCombo ?? "");
                cy += 30;
            }

            cy += 8;
            DarkTheme.AddCardHint(card,
                "Press combo to capture. Backspace, Delete, or Escape clear. \u26A0 red rows = deleted target.",
                10, cy);
        }

        var btnSave = DarkTheme.MakePrimaryButton("Save", 250, formHeight - 44);
        btnSave.Click += OnSaveClicked;
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 350, formHeight - 44);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
    }

    private void AddLiveRow(Panel card, int x, int y, string accountName, string currentCombo)
    {
        var lbl = DarkTheme.AddCardLabel(card, accountName, x, y + 4);
        lbl.Size = new Size(220, 20);
        var tb = MakeHotkeyBox(card, x + 230, y + 1, 160, currentCombo);
        _liveRows.Add((accountName, tb));
    }

    private void AddStaleRow(Panel card, int x, int y, HotkeyBinding stale)
    {
        var lbl = DarkTheme.AddCardLabel(card,
            $"\u26A0  {stale.TargetName}  (deleted)",
            x, y + 4);
        lbl.ForeColor = DarkTheme.CardWarn;
        lbl.Size = new Size(180, 20);

        var tb = MakeHotkeyBox(card, x + 190, y + 1, 90, stale.Combo);
        tb.BackColor = DarkTheme.BgInput;

        var rebindItems = new List<string> { "(none \u2014 clear binding)" };
        rebindItems.AddRange(_accounts.Select(a => a.Name));
        var cboRebind = DarkTheme.AddCardComboBox(card, x + 285, y + 1, 130, rebindItems.ToArray());
        cboRebind.DropDownStyle = ComboBoxStyle.DropDownList;
        cboRebind.SelectedIndex = 0;

        _staleRows.Add((stale.TargetName, tb, cboRebind));
    }

    /// <summary>
    /// Inline hotkey-capture TextBox — same key-handling contract as SettingsForm.MakeHotkeyBox:
    /// Delete / Backspace / Escape clear; any combo with at least one modifier + non-modifier key
    /// sets the text as "Ctrl+Alt+X" format.
    /// </summary>
    private TextBox MakeHotkeyBox(Panel card, int x, int y, int width, string initialText)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 20),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9f),
            TextAlign = HorizontalAlignment.Center,
            ShortcutsEnabled = false,
            Text = initialText,
        };
        tb.KeyDown += HotkeyBoxKeyDown;
        card.Controls.Add(tb);
        return tb;
    }

    private static void HotkeyBoxKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;

        if (e.KeyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey)
            return;

        if (e.KeyCode is Keys.Delete or Keys.Back or Keys.Escape && !e.Control && !e.Alt && !e.Shift)
        {
            if (sender is TextBox tb) tb.Text = "";
            return;
        }

        if (!e.Control && !e.Alt && !e.Shift) return;

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");

        string keyName = e.KeyCode switch
        {
            Keys.OemPipe or Keys.OemBackslash => "\\",
            Keys.OemCloseBrackets => "]",
            Keys.OemOpenBrackets => "[",
            _ => e.KeyCode.ToString()
        };
        parts.Add(keyName);
        if (sender is TextBox box) box.Text = string.Join("+", parts);
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        // Collect the new binding list: live rows with non-empty Combo + stale rows
        // that were either rebound (dropdown index > 0) or kept (index 0 == clear).
        var newBindings = new List<HotkeyBinding>();

        foreach (var (targetName, tb) in _liveRows)
        {
            var combo = tb.Text.Trim();
            if (!string.IsNullOrEmpty(combo))
                newBindings.Add(new HotkeyBinding { Combo = combo, TargetName = targetName });
        }

        foreach (var (_, tb, cboRebind) in _staleRows)
        {
            var combo = tb.Text.Trim();
            if (string.IsNullOrEmpty(combo)) continue;    // stale row with cleared combo: drop entirely
            if (cboRebind.SelectedIndex <= 0) continue;   // "(none)" picked: drop binding
            var newTarget = cboRebind.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(newTarget)) continue;
            newBindings.Add(new HotkeyBinding { Combo = combo, TargetName = newTarget });
        }

        // Same-combo conflict scan: within this dialog AND against other hotkeys.
        var within = newBindings.Where(b => !string.IsNullOrEmpty(b.Combo))
            .GroupBy(b => b.Combo, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        if (within.Any())
        {
            var lines = within.Select(g =>
                $"  {g.Key}  \u2192  {string.Join(", ", g.Select(b => $"Account '{b.TargetName}'"))}");
            MessageBox.Show(
                "Cannot save — multiple Accounts bound to the same key:\n\n" + string.Join("\n", lines),
                "Hotkey Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var externalConflicts = new List<string>();
        var inUse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, combo) in _otherHotkeys)
            if (!string.IsNullOrEmpty(combo)) inUse[combo] = label;

        foreach (var b in newBindings)
        {
            if (inUse.TryGetValue(b.Combo, out var otherLabel))
                externalConflicts.Add($"  {b.Combo}  \u2192  Account '{b.TargetName}' vs {otherLabel}");
        }
        if (externalConflicts.Any())
        {
            MessageBox.Show(
                "Cannot save — a combo is already bound to another action:\n\n" + string.Join("\n", externalConflicts),
                "Hotkey Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = newBindings;
        DialogResult = DialogResult.OK;
        Close();
    }
}
```

- [ ] **Step 3.2: Build + gates + fixtures**

```bash
cd X:/_Projects/EQSwitch && dotnet build --no-incremental 2>&1 | tail -6
echo "gameState==5: $(grep -c 'gameState == 5' Native/mq2_bridge.cpp)"
echo "result==-2: $(grep -c 'result == -2' Core/AutoLoginManager.cs)"
bash _tests/migration/run_fixtures.sh 2>&1 | tail -3
```
Expected: `0 Error(s)`, `1 Warning(s)`, `2`, `1`, `9 passed, 0 failed`.

- [ ] **Step 3.3: Commit**

```bash
cd X:/_Projects/EQSwitch
git add UI/AccountHotkeysDialog.cs
git commit -m "$(cat <<'EOF'
feat(settings): AccountHotkeysDialog modal

Phase 5a modal for editing AccountHotkeys. One row per _config.Accounts
entry (label + inline hotkey-capture textbox), plus any stale
HotkeyBindings (TargetName no longer resolves) rendered as red rows
with (none|Account name...) dropdown to rebind. Save collects live
rows with non-empty combos + rebound stale rows into a List<HotkeyBinding>
atomically.

Conflict detection inside the dialog: same-combo-bound-twice within
the dialog blocks Save with a modal listing each conflict, and an
external-hotkey collision (combo already bound elsewhere) surfaces a
separate modal naming the other action. Lets us catch conflicts before
they hit SettingsForm.ApplySettings in Task 7.

Matches shipped DarkTheme aesthetic: MakeCard + AddCardLabel + inline
TextBox with BgInput background, Consolas font, center alignment,
BorderStyle.None. Hotkey capture mirrors SettingsForm.MakeHotkeyBox
behavior including Escape as a clear key (Phase 3.5 polish).

No caller yet; wired from Hotkeys tab in Task 5.

EOF
)"
```

---

## Task 4 — CharacterHotkeysDialog

**Files:**
- Create: `UI/CharacterHotkeysDialog.cs`

Spec §4. Mirror of Task 3 against `_config.Characters` + `HotkeyConfig.CharacterHotkeys`. Orphan Characters flagged with `(no account)` suffix in the Rebind dropdown.

- [ ] **Step 4.1: Create CharacterHotkeysDialog.cs**

Create `X:/_Projects/EQSwitch/UI/CharacterHotkeysDialog.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// Phase 5a modal for editing CharacterHotkeys. One row per _config.Characters entry
/// (live), plus any stale CharacterHotkeys whose TargetName no longer resolves
/// (red row with Rebind dropdown). Orphan Characters (empty AccountUsername) are
/// flagged with a "(no account)" suffix in the Rebind dropdown — still bindable
/// but won't successfully launch until re-linked.
/// </summary>
public sealed class CharacterHotkeysDialog : Form
{
    private readonly IReadOnlyList<Character> _characters;
    private readonly List<(string TargetName, TextBox HotkeyBox)> _liveRows = new();
    private readonly List<(string TargetName, TextBox HotkeyBox, ComboBox RebindCombo)> _staleRows = new();
    private readonly IReadOnlyList<(string label, string combo)> _otherHotkeys;

    /// <summary>Result of the dialog. Null until DialogResult.OK.</summary>
    public List<HotkeyBinding>? Result { get; private set; }

    public CharacterHotkeysDialog(
        IReadOnlyList<Character> characters,
        IReadOnlyList<HotkeyBinding> currentBindings,
        IReadOnlyList<(string label, string combo)> otherHotkeys)
    {
        _characters = characters;
        _otherHotkeys = otherHotkeys;

        var byTargetName = new Dictionary<string, string>(StringComparer.Ordinal);
        var staleBindings = new List<HotkeyBinding>();
        foreach (var b in currentBindings)
        {
            if (!HotkeyBindingUtil.IsPopulated(b)) continue;
            bool resolves = characters.Any(c => c.Name.Equals(b.TargetName, StringComparison.Ordinal));
            if (resolves) byTargetName[b.TargetName] = b.Combo;
            else staleBindings.Add(b);
        }

        int rowCount = characters.Count + staleBindings.Count;
        int bodyHeight = 80 + rowCount * 30;
        int formHeight = Math.Min(140 + bodyHeight, 540);

        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.StyleForm(this, "Character Hotkeys", new Size(460, formHeight));

        var card = DarkTheme.MakeCard(this,
            "\uD83E\uDDD9",
            "Direct Character Hotkeys",
            DarkTheme.CardPurple,
            10, 10, 430, bodyHeight + 30);

        int cy = 32;

        if (characters.Count == 0 && staleBindings.Count == 0)
        {
            var lblEmpty = DarkTheme.AddCardHint(card,
                "No characters yet — add one via Settings \u2192 Accounts \u2192 Characters first.",
                10, cy);
            lblEmpty.Size = new Size(410, 40);
        }
        else
        {
            foreach (var stale in staleBindings.OrderBy(b => b.TargetName, StringComparer.Ordinal))
            {
                AddStaleRow(card, 10, cy, stale);
                cy += 30;
            }

            foreach (var c in characters)
            {
                byTargetName.TryGetValue(c.Name, out var currentCombo);
                AddLiveRow(card, 10, cy, c.Name, currentCombo ?? "",
                    isOrphan: string.IsNullOrEmpty(c.AccountUsername));
                cy += 30;
            }

            cy += 8;
            DarkTheme.AddCardHint(card,
                "Press combo to capture. Backspace, Delete, or Escape clear. \u26A0 red = deleted target; orphan = no account linked.",
                10, cy);
        }

        var btnSave = DarkTheme.MakePrimaryButton("Save", 250, formHeight - 44);
        btnSave.Click += OnSaveClicked;
        Controls.Add(btnSave);
        AcceptButton = btnSave;

        var btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 350, formHeight - 44);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
        CancelButton = btnCancel;
    }

    private void AddLiveRow(Panel card, int x, int y, string characterName, string currentCombo, bool isOrphan)
    {
        var displayName = isOrphan ? $"{characterName} (no account)" : characterName;
        var lbl = DarkTheme.AddCardLabel(card, displayName, x, y + 4);
        if (isOrphan) lbl.ForeColor = DarkTheme.FgDimGray;
        lbl.Size = new Size(220, 20);

        var tb = MakeHotkeyBox(card, x + 230, y + 1, 160, currentCombo);
        _liveRows.Add((characterName, tb));
    }

    private void AddStaleRow(Panel card, int x, int y, HotkeyBinding stale)
    {
        var lbl = DarkTheme.AddCardLabel(card,
            $"\u26A0  {stale.TargetName}  (deleted)",
            x, y + 4);
        lbl.ForeColor = DarkTheme.CardWarn;
        lbl.Size = new Size(180, 20);

        var tb = MakeHotkeyBox(card, x + 190, y + 1, 90, stale.Combo);

        var rebindItems = new List<string> { "(none \u2014 clear binding)" };
        rebindItems.AddRange(_characters.Select(c =>
            string.IsNullOrEmpty(c.AccountUsername) ? $"{c.Name} (no account)" : c.Name));
        var cboRebind = DarkTheme.AddCardComboBox(card, x + 285, y + 1, 130, rebindItems.ToArray());
        cboRebind.DropDownStyle = ComboBoxStyle.DropDownList;
        cboRebind.SelectedIndex = 0;

        _staleRows.Add((stale.TargetName, tb, cboRebind));
    }

    private TextBox MakeHotkeyBox(Panel card, int x, int y, int width, string initialText)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 20),
            BackColor = DarkTheme.BgInput,
            ForeColor = DarkTheme.FgWhite,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9f),
            TextAlign = HorizontalAlignment.Center,
            ShortcutsEnabled = false,
            Text = initialText,
        };
        tb.KeyDown += HotkeyBoxKeyDown;
        card.Controls.Add(tb);
        return tb;
    }

    private static void HotkeyBoxKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;

        if (e.KeyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey)
            return;

        if (e.KeyCode is Keys.Delete or Keys.Back or Keys.Escape && !e.Control && !e.Alt && !e.Shift)
        {
            if (sender is TextBox tb) tb.Text = "";
            return;
        }

        if (!e.Control && !e.Alt && !e.Shift) return;

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");

        string keyName = e.KeyCode switch
        {
            Keys.OemPipe or Keys.OemBackslash => "\\",
            Keys.OemCloseBrackets => "]",
            Keys.OemOpenBrackets => "[",
            _ => e.KeyCode.ToString()
        };
        parts.Add(keyName);
        if (sender is TextBox box) box.Text = string.Join("+", parts);
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        var newBindings = new List<HotkeyBinding>();

        foreach (var (targetName, tb) in _liveRows)
        {
            var combo = tb.Text.Trim();
            if (!string.IsNullOrEmpty(combo))
                newBindings.Add(new HotkeyBinding { Combo = combo, TargetName = targetName });
        }

        foreach (var (_, tb, cboRebind) in _staleRows)
        {
            var combo = tb.Text.Trim();
            if (string.IsNullOrEmpty(combo)) continue;
            if (cboRebind.SelectedIndex <= 0) continue;
            // Dropdown item may be "Name" or "Name (no account)" — strip the suffix.
            var picked = cboRebind.SelectedItem?.ToString() ?? "";
            var targetName = picked.EndsWith(" (no account)", StringComparison.Ordinal)
                ? picked.Substring(0, picked.Length - " (no account)".Length)
                : picked;
            if (string.IsNullOrEmpty(targetName)) continue;
            newBindings.Add(new HotkeyBinding { Combo = combo, TargetName = targetName });
        }

        var within = newBindings.Where(b => !string.IsNullOrEmpty(b.Combo))
            .GroupBy(b => b.Combo, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        if (within.Any())
        {
            var lines = within.Select(g =>
                $"  {g.Key}  \u2192  {string.Join(", ", g.Select(b => $"Character '{b.TargetName}'"))}");
            MessageBox.Show(
                "Cannot save — multiple Characters bound to the same key:\n\n" + string.Join("\n", lines),
                "Hotkey Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var externalConflicts = new List<string>();
        var inUse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, combo) in _otherHotkeys)
            if (!string.IsNullOrEmpty(combo)) inUse[combo] = label;

        foreach (var b in newBindings)
        {
            if (inUse.TryGetValue(b.Combo, out var otherLabel))
                externalConflicts.Add($"  {b.Combo}  \u2192  Character '{b.TargetName}' vs {otherLabel}");
        }
        if (externalConflicts.Any())
        {
            MessageBox.Show(
                "Cannot save — a combo is already bound to another action:\n\n" + string.Join("\n", externalConflicts),
                "Hotkey Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = newBindings;
        DialogResult = DialogResult.OK;
        Close();
    }
}
```

- [ ] **Step 4.2: Build + gates + fixtures**

```bash
cd X:/_Projects/EQSwitch && dotnet build --no-incremental 2>&1 | tail -6
echo "gameState==5: $(grep -c 'gameState == 5' Native/mq2_bridge.cpp)"
echo "result==-2: $(grep -c 'result == -2' Core/AutoLoginManager.cs)"
bash _tests/migration/run_fixtures.sh 2>&1 | tail -3
```
Expected: `0 Error(s)`, `1 Warning(s)`, `2`, `1`, `9 passed, 0 failed`.

- [ ] **Step 4.3: Commit**

```bash
cd X:/_Projects/EQSwitch
git add UI/CharacterHotkeysDialog.cs
git commit -m "$(cat <<'EOF'
feat(settings): CharacterHotkeysDialog modal

Mirror of AccountHotkeysDialog against _config.Characters +
HotkeyConfig.CharacterHotkeys. Orphan Characters (empty
AccountUsername — Phase 4 Unlink outcome) render with a dimmed
label + '(no account)' suffix in the live-row label and in the
Rebind dropdown. Still bindable — they just won't successfully
launch until re-linked. User can rebind an existing stale combo
to an orphan Character; when the Character is re-linked the
binding resumes working.

Save path identical pattern: live rows + rebound stale rows flatten
into a new List<HotkeyBinding>. Internal + external conflict scan.
Escape clears as part of the shared key handling.

Purple card accent (matches Phase 4 Characters-card convention)
vs gold for Accounts — visual consistency across Settings tabs.

No caller yet; wired from Hotkeys tab in Task 5.

EOF
)"
```

---

## Task 5 — Hotkeys tab Direct Bindings redesign

**Files:**
- Modify: `UI/SettingsForm.cs` — `BuildHotkeysTab` method body; deletes the legacy Quick Login card + Auto-Login Hotkeys card, adds a Direct Bindings card + legacy deprecation banner

Spec §2. Largest UI commit in Phase 5a. Wires `AccountHotkeysDialog` / `CharacterHotkeysDialog` via two Configure buttons.

- [ ] **Step 5.1: Locate BuildHotkeysTab**

```bash
grep -n "private TabPage BuildHotkeysTab" X:/_Projects/EQSwitch/UI/SettingsForm.cs
```
Expected: one match, e.g. `551:    private TabPage BuildHotkeysTab()`.

Also locate the legacy cards that get deleted:
```bash
grep -n "Quick Individual Login Accounts\|Auto-Login Hotkeys" X:/_Projects/EQSwitch/UI/SettingsForm.cs
```
Expected: two anchors — card-title strings inside `BuildHotkeysTab`. Note the approximate line ranges (~605 for Quick Login, ~628 for Auto-Login Hotkeys — shifted from Phase 3.5).

- [ ] **Step 5.2: Delete the legacy Quick Login card block**

Find the block starting with the comment `// ─── Quick Login Slots (defines what the hotkeys below trigger) ──` and extending through `CheckDuplicateSlotAccounts();` calls and the following `y += 118;` line. The block ends just before `// ─── Auto-Login Hotkeys ─────────────────────────────────`.

Replace that ENTIRE block with a single line comment:

```csharp
        // Phase 5a: legacy Quick Login slot combos moved to AccountHotkeysDialog /
        // CharacterHotkeysDialog (opened from the Direct Bindings card below).
        // The QuickLogin1-4 fields remain on AppConfig for v3.10.x back-compat;
        // Phase 6 deletes them.
```

- [ ] **Step 5.3: Delete the legacy Auto-Login Hotkeys card block**

Find the block starting with `// ─── Auto-Login Hotkeys ─────────────────────────────────` comment. It contains `hkCard` setup, the 4 AutoLogin TextBoxes, the 4 Team TextBoxes, the warn label, the TextChanged subscriptions, and ends with the `y += 120;` line followed by `return page;` (~line 671).

**Keep the Team rows.** They stay — just in a relocated card. Also keep the TextChanged conflict-warning subscriptions for the Team rows + the Action hotkeys.

Cleanest rewrite: delete the whole old block and replace with the new Team card + Direct Bindings card block. Replace from `// ─── Auto-Login Hotkeys ─────────────────────────────────` through (but NOT including) the closing `return page;` and its `y += 120;` with:

```csharp
        // ─── Team Hotkeys ────────────────────────────────────────
        var cardTeams = DarkTheme.MakeCard(page, "\uD83D\uDC65", "Team Hotkeys", DarkTheme.CardGold, 10, y, 480, 98);
        const int teamHkL = 10, teamHkI = 65, teamHkCol2 = 240, teamHkCol2I = 295, teamHkW = 130;
        int teamCy = 32;

        DarkTheme.AddCardLabel(cardTeams, "Team 1:", teamHkL, teamCy + 3);
        _txtTeamLogin1Hotkey = MakeHotkeyBox(cardTeams, teamHkI, teamCy + 1, teamHkW);
        DarkTheme.AddCardLabel(cardTeams, "Team 2:", teamHkCol2, teamCy + 3);
        _txtTeamLogin2Hotkey = MakeHotkeyBox(cardTeams, teamHkCol2I, teamCy + 1, teamHkW);
        teamCy += 30;

        DarkTheme.AddCardLabel(cardTeams, "Team 3:", teamHkL, teamCy + 3);
        _txtTeamLogin3Hotkey = MakeHotkeyBox(cardTeams, teamHkI, teamCy + 1, teamHkW);
        DarkTheme.AddCardLabel(cardTeams, "Team 4:", teamHkCol2, teamCy + 3);
        _txtTeamLogin4Hotkey = MakeHotkeyBox(cardTeams, teamHkCol2I, teamCy + 1, teamHkW);

        _lblAutoLoginHotkeyWarn = DarkTheme.AddCardHint(cardTeams, "", teamHkL, 82);
        _lblAutoLoginHotkeyWarn.Size = new Size(460, 14);
        _lblAutoLoginHotkeyWarn.ForeColor = DarkTheme.FgWarn;

        _txtTeamLogin1Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtTeamLogin2Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtTeamLogin3Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();
        _txtTeamLogin4Hotkey.TextChanged += (_, _) => CheckAutoLoginHotkeyConflicts();

        y += 106;

        // ─── Direct Bindings (Account + Character hotkey families) ──
        _cardDirectBindings = DarkTheme.MakeCard(page, "\u2328", "Direct Bindings",
            DarkTheme.CardGreen, 10, y, 480, 130);
        RefreshDirectBindingsCard();

        y += 138;
```

The `_cardDirectBindings: Panel` field and the `RefreshDirectBindingsCard` method are added in Step 5.5.

- [ ] **Step 5.4: Remove the AutoLogin TextBox field declarations + their TextChanged subscribers**

The legacy `_txtAutoLogin1Hotkey..4` TextBoxes are no longer created. Search for their declarations:

```bash
grep -n "_txtAutoLogin[1-4]Hotkey" X:/_Projects/EQSwitch/UI/SettingsForm.cs
```

Expected: declarations at the top of the class (around line 51-55) plus references in `CheckAutoLoginHotkeyConflicts` (line ~441-450), `LoadSettings` (line ~1077-1080), and `BuildAppConfig` (line ~1204-1207). We need to:

1. **Keep the field declarations** — they're small, and `LoadSettings` / `BuildAppConfig` still reference the v3 `HotkeyConfig.AutoLogin1-4` fields during the deprecation window. But since the TextBoxes are no longer constructed, dereferencing them would NPE. Replace the field declarations:

```bash
grep -n "private TextBox _txtAutoLogin" X:/_Projects/EQSwitch/UI/SettingsForm.cs
```
Expected: 4 matches. Delete those 4 lines entirely:

```csharp
    private TextBox _txtAutoLogin1Hotkey = null!;
    private TextBox _txtAutoLogin2Hotkey = null!;
    private TextBox _txtAutoLogin3Hotkey = null!;
    private TextBox _txtAutoLogin4Hotkey = null!;
```

2. Remove the LoadSettings population lines. Search for `_txtAutoLogin1Hotkey.Text =`:

```bash
grep -n "_txtAutoLogin[1-4]Hotkey.Text" X:/_Projects/EQSwitch/UI/SettingsForm.cs
```
Expected: 4 lines in `LoadSettings`, 4 lines in `BuildAppConfig`. Replace the 4-line `LoadSettings` block:

```csharp
        _txtAutoLogin1Hotkey.Text = _config.Hotkeys.AutoLogin1;
        _txtAutoLogin2Hotkey.Text = _config.Hotkeys.AutoLogin2;
        _txtAutoLogin3Hotkey.Text = _config.Hotkeys.AutoLogin3;
        _txtAutoLogin4Hotkey.Text = _config.Hotkeys.AutoLogin4;
```

with:

```csharp
        // Phase 5a: _txtAutoLogin1-4 removed from UI. v3 AutoLogin hotkeys are edited via
        // AccountHotkeysDialog / CharacterHotkeysDialog — legacy field values pass through
        // unchanged via BuildAppConfig below.
```

3. Replace the 4-line `BuildAppConfig` block:

```csharp
                AutoLogin1 = _txtAutoLogin1Hotkey.Text.Trim(),
                AutoLogin2 = _txtAutoLogin2Hotkey.Text.Trim(),
                AutoLogin3 = _txtAutoLogin3Hotkey.Text.Trim(),
                AutoLogin4 = _txtAutoLogin4Hotkey.Text.Trim(),
```

with:

```csharp
                // Phase 5a: legacy AutoLogin1-4 pass through — values flow via v4 family tables now.
                AutoLogin1 = _config.Hotkeys.AutoLogin1,
                AutoLogin2 = _config.Hotkeys.AutoLogin2,
                AutoLogin3 = _config.Hotkeys.AutoLogin3,
                AutoLogin4 = _config.Hotkeys.AutoLogin4,
```

- [ ] **Step 5.5: Update CheckAutoLoginHotkeyConflicts to skip AutoLogin entries**

Find `CheckAutoLoginHotkeyConflicts` (search anchor `"CheckAutoLoginHotkeyConflicts"`). The method iterates an `entries` array that includes the 4 AutoLogin TextBoxes + 4 Team TextBoxes. The AutoLogin entries must be removed — those hotkeys no longer have TextBoxes.

Replace the `entries` array construction at the top of the method:

```csharp
        var entries = new (string Key, string Label)[]
        {
            (_txtAutoLogin1Hotkey?.Text.Trim() ?? "", "Slot 1"),
            (_txtAutoLogin2Hotkey?.Text.Trim() ?? "", "Slot 2"),
            (_txtAutoLogin3Hotkey?.Text.Trim() ?? "", "Slot 3"),
            (_txtAutoLogin4Hotkey?.Text.Trim() ?? "", "Slot 4"),
            (_txtTeamLogin1Hotkey?.Text.Trim() ?? "", "Team 1"),
            (_txtTeamLogin2Hotkey?.Text.Trim() ?? "", "Team 2"),
            (_txtTeamLogin3Hotkey?.Text.Trim() ?? "", "Team 3"),
            (_txtTeamLogin4Hotkey?.Text.Trim() ?? "", "Team 4"),
        };
```

with:

```csharp
        // Phase 5a: only Team hotkeys remain as TextBoxes on this tab; AutoLogin1-4
        // hotkeys are edited via AccountHotkeysDialog / CharacterHotkeysDialog and
        // checked against the live field tables in ApplySettings.
        var entries = new (string Key, string Label)[]
        {
            (_txtTeamLogin1Hotkey?.Text.Trim() ?? "", "Team 1"),
            (_txtTeamLogin2Hotkey?.Text.Trim() ?? "", "Team 2"),
            (_txtTeamLogin3Hotkey?.Text.Trim() ?? "", "Team 3"),
            (_txtTeamLogin4Hotkey?.Text.Trim() ?? "", "Team 4"),
        };
```

- [ ] **Step 5.6: Add _cardDirectBindings field + RefreshDirectBindingsCard + handlers**

Add the `_cardDirectBindings` field declaration next to the other Hotkeys-tab field declarations (search anchor `"private TextBox _txtArrangeWindows"`):

```csharp
    private TextBox _txtArrangeWindows = null!;
```

Insert immediately above:

```csharp
    private Panel _cardDirectBindings = null!;
    private Panel? _legacyBanner;   // rendered only if legacy QuickLoginN are populated and banner not dismissed
```

Then add a new method after `CheckAutoLoginHotkeyConflicts` (search anchor `"private void CheckAutoLoginHotkeyConflicts"` — insert AFTER the closing brace of that method):

```csharp
    /// <summary>
    /// Phase 5a: repopulate the Direct Bindings card contents. Called on tab build and
    /// after either hotkey dialog saves. Renders:
    ///   - Accounts row: "X/N bound" + Configure button
    ///   - Characters row: "X/N bound" + Configure button
    ///   - Stale summary row: "\u26A0 N stale binding..." only when CountStale > 0
    ///   - Legacy deprecation banner above the card if QuickLogin1-4 are populated and
    ///     HotkeysLegacyBannerDismissed is false.
    /// </summary>
    private void RefreshDirectBindingsCard()
    {
        _cardDirectBindings.Controls.Clear();

        int liveA = HotkeyBindingUtil.CountLiveAccountBindings(_config);
        int liveC = HotkeyBindingUtil.CountLiveCharacterBindings(_config);
        int staleA = HotkeyBindingUtil.CountStaleAccountBindings(_config);
        int staleC = HotkeyBindingUtil.CountStaleCharacterBindings(_config);
        int totalA = _config.Accounts.Count;
        int totalC = _config.Characters.Count;

        int cy = 32;

        // Accounts row
        DarkTheme.AddCardLabel(_cardDirectBindings, "Accounts", 10, cy + 4);
        var lblAcctCount = DarkTheme.AddCardLabel(_cardDirectBindings, $"{liveA} / {totalA} bound", 100, cy + 4);
        lblAcctCount.ForeColor = DarkTheme.FgDimGray;
        var btnConfigureAccounts = DarkTheme.AddCardButton(_cardDirectBindings, "Configure\u2026", 350, cy - 1, 110);
        btnConfigureAccounts.Click += (_, _) => OpenAccountHotkeysDialog();
        cy += 28;

        // Characters row
        DarkTheme.AddCardLabel(_cardDirectBindings, "Characters", 10, cy + 4);
        var lblCharCount = DarkTheme.AddCardLabel(_cardDirectBindings, $"{liveC} / {totalC} bound", 100, cy + 4);
        lblCharCount.ForeColor = DarkTheme.FgDimGray;
        var btnConfigureChars = DarkTheme.AddCardButton(_cardDirectBindings, "Configure\u2026", 350, cy - 1, 110);
        btnConfigureChars.Click += (_, _) => OpenCharacterHotkeysDialog();
        cy += 28;

        // Stale summary row — rendered only when count > 0
        if (staleA > 0 || staleC > 0)
        {
            var parts = new List<string>();
            if (staleA > 0) parts.Add($"{staleA} Account");
            if (staleC > 0) parts.Add($"{staleC} Character");
            var lblStale = DarkTheme.AddCardLabel(_cardDirectBindings,
                $"\u26A0 Stale bindings: {string.Join(" + ", parts)} — open Configure to review",
                10, cy + 4);
            lblStale.Size = new Size(460, 18);
            lblStale.ForeColor = DarkTheme.CardWarn;
            cy += 22;
        }

        DarkTheme.AddCardHint(_cardDirectBindings,
            "Bind a hotkey to any Account or Character. Ctrl+Alt+Letter style combos recommended.",
            10, cy + 6);

        RefreshLegacyBanner();
    }

    private void RefreshLegacyBanner()
    {
        // Remove any prior banner Panel.
        if (_legacyBanner != null && _legacyBanner.Parent != null)
        {
            _legacyBanner.Parent.Controls.Remove(_legacyBanner);
            _legacyBanner.Dispose();
            _legacyBanner = null;
        }

        bool anyLegacy = !string.IsNullOrEmpty(_config.QuickLogin1) || !string.IsNullOrEmpty(_config.QuickLogin2)
                      || !string.IsNullOrEmpty(_config.QuickLogin3) || !string.IsNullOrEmpty(_config.QuickLogin4);
        if (!anyLegacy || _config.HotkeysLegacyBannerDismissed) return;

        var page = _cardDirectBindings.Parent;
        if (page == null) return;

        var banner = new Panel
        {
            Location = new Point(_cardDirectBindings.Location.X, _cardDirectBindings.Location.Y - 38),
            Size = new Size(_cardDirectBindings.Width, 34),
            BackColor = DarkTheme.BgMedium,
        };

        var lbl = DarkTheme.AddCardLabel(banner,
            "\u2139 Quick Login slots 1-4 moved to Direct Bindings. Legacy hotkeys still work until v3.11.0.",
            10, 10);
        lbl.Size = new Size(370, 18);

        var btnDismiss = DarkTheme.AddCardButton(banner, "Dismiss", 390, 5, 80);
        btnDismiss.Click += (_, _) =>
        {
            _config.HotkeysLegacyBannerDismissed = true;
            ConfigManager.Save(_config);
            RefreshLegacyBanner();
        };

        page.Controls.Add(banner);
        _legacyBanner = banner;
    }

    private void OpenAccountHotkeysDialog()
    {
        // Build "other hotkeys" snapshot for conflict detection inside the dialog.
        var others = new List<(string label, string combo)>
        {
            ("Fix Windows",      _txtArrangeWindows.Text.Trim()),
            ("Launch One",       _txtLaunchOne.Text.Trim()),
            ("Launch All",       _txtLaunchAll.Text.Trim()),
            ("Multi-Mon Toggle", _txtToggleMultiMon.Text.Trim()),
            ("PiP Toggle",       _txtTogglePip.Text.Trim()),
            ("Team 1",           _txtTeamLogin1Hotkey.Text.Trim()),
            ("Team 2",           _txtTeamLogin2Hotkey.Text.Trim()),
            ("Team 3",           _txtTeamLogin3Hotkey.Text.Trim()),
            ("Team 4",           _txtTeamLogin4Hotkey.Text.Trim()),
        };
        // Include Character family bindings (not being edited here).
        foreach (var b in _config.Hotkeys.CharacterHotkeys)
            if (HotkeyBindingUtil.IsPopulated(b))
                others.Add(($"Character '{b.TargetName}'", b.Combo));

        using var dlg = new AccountHotkeysDialog(_config.Accounts, _config.Hotkeys.AccountHotkeys, others);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            _config.Hotkeys.AccountHotkeys = dlg.Result;
            ConfigManager.Save(_config);
            RefreshDirectBindingsCard();
        }
    }

    private void OpenCharacterHotkeysDialog()
    {
        var others = new List<(string label, string combo)>
        {
            ("Fix Windows",      _txtArrangeWindows.Text.Trim()),
            ("Launch One",       _txtLaunchOne.Text.Trim()),
            ("Launch All",       _txtLaunchAll.Text.Trim()),
            ("Multi-Mon Toggle", _txtToggleMultiMon.Text.Trim()),
            ("PiP Toggle",       _txtTogglePip.Text.Trim()),
            ("Team 1",           _txtTeamLogin1Hotkey.Text.Trim()),
            ("Team 2",           _txtTeamLogin2Hotkey.Text.Trim()),
            ("Team 3",           _txtTeamLogin3Hotkey.Text.Trim()),
            ("Team 4",           _txtTeamLogin4Hotkey.Text.Trim()),
        };
        foreach (var b in _config.Hotkeys.AccountHotkeys)
            if (HotkeyBindingUtil.IsPopulated(b))
                others.Add(($"Account '{b.TargetName}'", b.Combo));

        using var dlg = new CharacterHotkeysDialog(_config.Characters, _config.Hotkeys.CharacterHotkeys, others);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            _config.Hotkeys.CharacterHotkeys = dlg.Result;
            ConfigManager.Save(_config);
            RefreshDirectBindingsCard();
        }
    }
```

**Note:** these handlers commit their result to `_config` + `ConfigManager.Save(_config)` directly, not via `ApplySettings`. This matches the Phase 4 AutoLoginTeamsDialog pattern where the modal's Save writes to `_config` immediately. `TrayManager.ReloadConfig` re-registers hotkeys when Settings later closes (Phase 3.5-A guard).

- [ ] **Step 5.7: Build + gates + fixtures**

```bash
cd X:/_Projects/EQSwitch && dotnet build --no-incremental 2>&1 | tail -10
```
Expected: `0 Error(s)`, `1 Warning(s)`. If `CS0103` errors appear for `_txtAutoLogin*` references, more sites exist than the 3 listed in Step 5.4 — grep them out and apply the same fix.

```bash
echo "gameState==5: $(grep -c 'gameState == 5' Native/mq2_bridge.cpp)"
echo "result==-2: $(grep -c 'result == -2' Core/AutoLoginManager.cs)"
bash _tests/migration/run_fixtures.sh 2>&1 | tail -3
```
Expected: `2`, `1`, `9 passed, 0 failed`.

- [ ] **Step 5.8: Commit**

```bash
cd X:/_Projects/EQSwitch
git add UI/SettingsForm.cs
git commit -m "$(cat <<'EOF'
feat(settings): Hotkeys tab Direct Bindings redesign

Replace the legacy Quick Individual Login Accounts card + Auto-Login
Hotkeys card with:
 - Team Hotkeys card (4 Team slots — kept from v3, relocated)
 - Direct Bindings card with Accounts + Characters rows showing
   'X/N bound' counts plus Configure buttons that open the new
   AccountHotkeysDialog / CharacterHotkeysDialog modals.
 - Stale-binding summary row — red, rendered only when stale count > 0.
 - Legacy deprecation banner above the card if QuickLogin1-4 are
   populated and HotkeysLegacyBannerDismissed is false. Dismiss button
   flips the bool + persists via ConfigManager.Save.

Dialogs commit their Results to _config.Hotkeys.Account/CharacterHotkeys
directly (matches AutoLoginTeamsDialog pattern). RefreshDirectBindingsCard
pulls new counts + stale summary from HotkeyBindingUtil.

_txtAutoLogin1-4 TextBox fields + LoadSettings population + BuildAppConfig
read-paths removed — QuickLogin hotkeys now edited via the dialogs. v3
HotkeyConfig.AutoLogin1-4 fields pass through BuildAppConfig unchanged for
deprecation-window back-compat (Phase 6 deletes them).

CheckAutoLoginHotkeyConflicts narrows to Team entries only.

EOF
)"
```

---

## Task 6 — Family-table dispatch

**Files:**
- Modify: `UI/TrayManager.cs` — `RegisterHotkeys` body + 2 new Fire* helpers

Spec §6. Enables the hotkeys bound in Tasks 3-5 to actually fire.

- [ ] **Step 6.1: Locate the last TryRegister call inside RegisterHotkeys**

```bash
grep -n "TryRegister(hk.TeamLogin4" X:/_Projects/EQSwitch/UI/TrayManager.cs
```
Expected: one match, likely around line 393. The line after it is typically `FileLogger.Info(\$"RegisterHotKey:` which we leave alone.

- [ ] **Step 6.2: Add family-table registration loop**

Open `UI/TrayManager.cs`. Find the existing `TryRegister(hk.TeamLogin4, ...)` line. Immediately after it (before the `FileLogger.Info` line), insert:

```csharp

        // Phase 5a: family-table dispatch. AccountHotkeys -> LoginToCharselect (via
        // FireAccountLogin), CharacterHotkeys -> LoginAndEnterWorld (via FireCharacterLogin).
        // Skip padding entries (empty Combo OR empty TargetName — Phase 1 migration contract).
        // Name-based registration reuses the existing HotkeyManager.Register API.
        foreach (var binding in hk.AccountHotkeys)
        {
            if (!HotkeyBindingUtil.IsPopulated(binding)) continue;
            var capturedName = binding.TargetName;
            TryRegister(binding.Combo,
                () => FireAccountHotkeyByName(capturedName),
                $"AccountHK:{capturedName}");
        }

        foreach (var binding in hk.CharacterHotkeys)
        {
            if (!HotkeyBindingUtil.IsPopulated(binding)) continue;
            var capturedName = binding.TargetName;
            TryRegister(binding.Combo,
                () => FireCharacterHotkeyByName(capturedName),
                $"CharacterHK:{capturedName}");
        }
```

- [ ] **Step 6.3: Add the two Fire helpers**

Find `FireAccountLogin` (search anchor `"private void FireAccountLogin"`). Immediately after the closing brace of `FireAccountLogin`, add:

```csharp

    /// <summary>
    /// Phase 5a: dispatch entry point for AccountHotkeys[]. Resolves the binding's
    /// TargetName to an Account at fire time — if the Account was deleted between
    /// Save and keypress, surfaces an actionable balloon pointing the user at the
    /// Hotkeys dialog. No throw.
    /// </summary>
    private void FireAccountHotkeyByName(string name)
    {
        var account = _config.FindAccountByName(name);
        if (account == null)
        {
            ShowBalloon($"Account Hotkey: '{name}' not found. Open Settings \u2192 Hotkeys \u2192 Configure Accounts to rebind.");
            FileLogger.Warn($"AccountHotkey fired for missing target '{name}' — user should rebind in the Account Hotkeys dialog");
            return;
        }
        FireAccountLogin(account);
    }
```

Similarly, find `FireCharacterLogin` and add after its closing brace:

```csharp

    /// <summary>
    /// Phase 5a: dispatch entry point for CharacterHotkeys[]. Same null-guard pattern
    /// as FireAccountHotkeyByName — if the Character was deleted between Save and
    /// keypress, balloon points the user at the Hotkeys dialog.
    /// </summary>
    private void FireCharacterHotkeyByName(string name)
    {
        var character = _config.FindCharacterByName(name);
        if (character == null)
        {
            ShowBalloon($"Character Hotkey: '{name}' not found. Open Settings \u2192 Hotkeys \u2192 Configure Characters to rebind.");
            FileLogger.Warn($"CharacterHotkey fired for missing target '{name}' — user should rebind in the Character Hotkeys dialog");
            return;
        }
        FireCharacterLogin(character);
    }
```

- [ ] **Step 6.4: Build + gates + fixtures**

```bash
cd X:/_Projects/EQSwitch && dotnet build --no-incremental 2>&1 | tail -6
echo "gameState==5: $(grep -c 'gameState == 5' Native/mq2_bridge.cpp)"
echo "result==-2: $(grep -c 'result == -2' Core/AutoLoginManager.cs)"
bash _tests/migration/run_fixtures.sh 2>&1 | tail -3
```
Expected: `0 Error(s)`, `1 Warning(s)`, `2`, `1`, `9 passed, 0 failed`.

- [ ] **Step 6.5: Commit**

```bash
cd X:/_Projects/EQSwitch
git add UI/TrayManager.cs
git commit -m "$(cat <<'EOF'
feat(tray): family-table dispatch for AccountHotkeys + CharacterHotkeys

RegisterHotkeys now iterates HotkeyConfig.AccountHotkeys +
HotkeyConfig.CharacterHotkeys (populated by Phase 1 migration, edited
via Phase 5a dialogs). Each populated binding registers via the
existing name-based HotkeyManager.Register API. Padding entries
(empty Combo OR empty TargetName) are skipped per the
ConfigVersionMigrator.EnsureSize invariant.

Two new dispatch helpers: FireAccountHotkeyByName resolves TargetName
to an Account via AppConfig.FindAccountByName and delegates to
FireAccountLogin (charselect-only). FireCharacterHotkeyByName mirrors
against Characters -> FireCharacterLogin (enter-world). If the target
was deleted between Save and keypress (stale binding that escaped
the dialog's conflict scan), balloon the user with a rebinding path
instead of throwing or firing a mystery login.

Legacy AutoLogin1-4 dispatch path coexists during v3.10.x —
hotkey-conflict scan blocks duplicate binds at Save time.

EOF
)"
```

---

## Task 7 — Conflict-scan extension to family tables

**Files:**
- Modify: `UI/SettingsForm.cs` — `ApplySettings` P3.5-D block (anchor `"Phase 3.5-D: hotkey conflict detection"`)

Spec §7. Adds AccountHotkeys + CharacterHotkeys into the existing duplicate-combo scan. Tab-level Save now blocks if a family binding and a tab-level binding share a combo.

- [ ] **Step 7.1: Locate the P3.5-D block**

```bash
grep -n "Phase 3.5-D: hotkey conflict detection" X:/_Projects/EQSwitch/UI/SettingsForm.cs
```
Expected: one match. Note the line.

- [ ] **Step 7.2: Extend the allHotkeys array**

Find the block:

```csharp
        // Phase 3.5-D: hotkey conflict detection — same key combo bound to
        // multiple actions causes RegisterHotKey to silently fail on the
        // second registration. Block Save with an actionable modal.
        var allHotkeys = new[]
        {
            ("Fix Windows",      _txtArrangeWindows.Text.Trim()),
            ("Launch One",       _txtLaunchOne.Text.Trim()),
            ("Launch All",       _txtLaunchAll.Text.Trim()),
            ("Multi-Mon Toggle", _txtToggleMultiMon.Text.Trim()),
            ("PiP Toggle",       _txtTogglePip.Text.Trim()),
            ("AutoLogin 1",      _txtAutoLogin1Hotkey.Text.Trim()),
            ("AutoLogin 2",      _txtAutoLogin2Hotkey.Text.Trim()),
            ("AutoLogin 3",      _txtAutoLogin3Hotkey.Text.Trim()),
            ("AutoLogin 4",      _txtAutoLogin4Hotkey.Text.Trim()),
            ("Team Login 1",     _txtTeamLogin1Hotkey.Text.Trim()),
            ("Team Login 2",     _txtTeamLogin2Hotkey.Text.Trim()),
            ("Team Login 3",     _txtTeamLogin3Hotkey.Text.Trim()),
            ("Team Login 4",     _txtTeamLogin4Hotkey.Text.Trim()),
        };
```

Replace with:

```csharp
        // Phase 3.5-D + Phase 5a: hotkey conflict detection — same key combo bound to
        // multiple actions causes RegisterHotKey to silently fail on the second
        // registration. Scan covers tab-level Action + Team hotkeys plus the family-
        // table bindings (AccountHotkeys + CharacterHotkeys). Legacy AutoLogin1-4
        // values come from _config (no longer edited on this tab) so migrated v3
        // bindings still participate in conflict detection during the deprecation window.
        var tabHotkeys = new[]
        {
            ("Fix Windows",      _txtArrangeWindows.Text.Trim()),
            ("Launch One",       _txtLaunchOne.Text.Trim()),
            ("Launch All",       _txtLaunchAll.Text.Trim()),
            ("Multi-Mon Toggle", _txtToggleMultiMon.Text.Trim()),
            ("PiP Toggle",       _txtTogglePip.Text.Trim()),
            ("AutoLogin 1 (legacy)", _config.Hotkeys.AutoLogin1),
            ("AutoLogin 2 (legacy)", _config.Hotkeys.AutoLogin2),
            ("AutoLogin 3 (legacy)", _config.Hotkeys.AutoLogin3),
            ("AutoLogin 4 (legacy)", _config.Hotkeys.AutoLogin4),
            ("Team Login 1",     _txtTeamLogin1Hotkey.Text.Trim()),
            ("Team Login 2",     _txtTeamLogin2Hotkey.Text.Trim()),
            ("Team Login 3",     _txtTeamLogin3Hotkey.Text.Trim()),
            ("Team Login 4",     _txtTeamLogin4Hotkey.Text.Trim()),
        };
        var familyHotkeys = Config.HotkeyBindingUtil.EnumeratePopulatedLabeled(_config)
            .Select(t => (t.label, t.combo));
        var allHotkeys = tabHotkeys.Concat(familyHotkeys).ToArray();
```

- [ ] **Step 7.3: Build + gates + fixtures**

```bash
cd X:/_Projects/EQSwitch && dotnet build --no-incremental 2>&1 | tail -6
echo "gameState==5: $(grep -c 'gameState == 5' Native/mq2_bridge.cpp)"
echo "result==-2: $(grep -c 'result == -2' Core/AutoLoginManager.cs)"
bash _tests/migration/run_fixtures.sh 2>&1 | tail -3
```
Expected: `0 Error(s)`, `1 Warning(s)`, `2`, `1`, `9 passed, 0 failed`.

- [ ] **Step 7.4: Commit**

```bash
cd X:/_Projects/EQSwitch
git add UI/SettingsForm.cs
git commit -m "$(cat <<'EOF'
feat(settings): extend P3.5-D conflict scan to family tables + legacy

ApplySettings' pre-save hotkey-conflict scan now includes:
 - Tab-level Action + Team hotkeys (as before)
 - Legacy v3 HotkeyConfig.AutoLogin1-4 (read from _config since the
   TextBoxes are gone — labels suffixed '(legacy)' for clarity in
   the conflict modal)
 - Every populated HotkeyConfig.AccountHotkeys entry
 - Every populated HotkeyConfig.CharacterHotkeys entry

Collapsing all of these into one scan catches the failure mode where
a user binds Alt+Z to a Character via the new dialog and then tries
to save a tab-level Team hotkey also on Alt+Z. The Hotkeys tab Save
blocks the whole config from persisting with the usual conflict modal
listing each collision's action labels.

HotkeyBindingUtil.EnumeratePopulatedLabeled() is the single source of
truth for family-table scan input, mirrored by the in-dialog scans
from Tasks 3 + 4.

EOF
)"
```

---

## Task 8 — Agent fanout + fold findings + publish

**No source changes in this task until findings land.** Review + iterate + ship.

- [ ] **Step 8.1: Dispatch three review agents in parallel**

Open one message with three `Agent` tool calls:

1. **`pr-review-toolkit:code-reviewer`** — prompt includes:
   - Commit range: `git log 01de047..HEAD --oneline` (spec forward through Task 7).
   - Files touched: `UI/TrayManager.cs`, `UI/SettingsForm.cs`, `UI/AccountHotkeysDialog.cs`, `UI/CharacterHotkeysDialog.cs`, `Config/AppConfig.cs`, `Config/HotkeyBindingUtil.cs`.
   - Focus: CLAUDE.md conventions (no `Color.FromArgb` outside DarkTheme.cs, all controls via DarkTheme factories, `using var` for Process, conventional commits under 72 chars).
   - Out of scope: native code, migration fixtures, phantom-click gates — verify via grep.
   - Report format: confidence-based, HIGH first, file:line anchors, under 500 words.

2. **`pr-review-toolkit:silent-failure-hunter`** — prompt includes:
   - Same commit range + file list.
   - Focus areas:
     - Stale-binding edges: what if user edits Accounts tab (renames/deletes an Account) while a hotkey is bound? Is the stale indicator updated live or only on next Settings open?
     - `AccountHotkeysDialog.OnSaveClicked` paths: stale row with cleared combo (correctly dropped?), rebound-to-self (targetName == oldName — effectively a rename, no-op), rebound-to-orphan Character.
     - `FireAccountHotkeyByName` / `FireCharacterHotkeyByName` null-resolution path: balloon fires but the hotkey STAYS registered until next ReloadConfig — can it misfire repeatedly?
     - Legacy banner dismiss persistence: does closing Settings without any other change still persist the `HotkeysLegacyBannerDismissed = true` flip? (Yes because the banner writes `ConfigManager.Save(_config)` directly — but verify the ReloadConfig round-trip matches Phase 4 TogglePip pattern.)
     - Conflict-scan completeness: does Task 7 catch every combination (legacy-vs-family, family-vs-family, within-same-family)?
   - Under 500 words.

3. **`feature-dev:code-reviewer`** — prompt includes:
   - Same commit range + file list.
   - Focus: UX soundness + data invariants.
     - Direct Bindings card glanceability: does `X/N bound` communicate what the user expects?
     - Legacy banner placement: above the card or below? Is Dismiss discoverable?
     - Stale Rebind dropdown: is "(none — clear binding)" discoverable vs. hidden as "first item"?
     - Round-trip invariant: load v4 config → open Settings → save without edits → config diff cleanly (modulo `HotkeysLegacyBannerDismissed` if banner rendered).
     - Orphan Character bindability in `CharacterHotkeysDialog`: flagged correctly, but does it feel right for the user to bind a hotkey to an unlaunchable target?
   - Include opinionated verdict: ship-as-is / ship-with-minor-fixes / hold-for-significant-fixes.
   - Under 500 words.

Run all three in a single message with three parallel `Agent` tool calls.

- [ ] **Step 8.2: Fold findings into atomic commit(s)**

Triage by severity:
- **CRITICAL / HIGH:** fold in 1-2 commits before publish.
- **MEDIUM:** fold if trivial (< 10 lines), defer otherwise.
- **LOW:** document in the Phase 5b handoff stub, don't block publish.

Each fold commit: re-verify build (0 errors, 1 warning), phantom-click gates (2 / 1), fixtures (9/9). Commit title format: `fix(settings|tray|config): <issue summary>`.

- [ ] **Step 8.3: Publish + deploy**

```bash
# Kill any running EQSwitch.exe first.
MSYS_NO_PATHCONV=1 tasklist.exe /FI "IMAGENAME eq EQSwitch.exe" /FO CSV | head -3
# If running, grab the PID and kill:
#   cmd.exe /c "taskkill /PID <pid> /F"

cd X:/_Projects/EQSwitch
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true 2>&1 | tail -6
```
Expected: `0 Error(s)`, output at `bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe` (~180 MB).

```bash
cp "X:/_Projects/EQSwitch/bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe" \
   "C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe"
cmp "X:/_Projects/EQSwitch/bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe" \
    "C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe" && echo "BYTE-IDENTICAL"
```

Then `git push`. Relaunch via `start "" "C:/Users/nate/proggy/Everquest/EQSwitch/EQSwitch.exe"`.

---

## Task 9 — Nate-driven smoke test

**No source changes.** Hand the user the verification checklist below. Any regression → stop, reproduce, open a follow-up commit.

Smoke script (mirrors Spec "Manual smoke test" section):

1. **Alt+M fires without gate.** Fresh launch. If `multiMonitorEnabled: false` in live config, press Alt+M. Expected balloon: `Layout: Multi-Monitor` (NOT "Enable Multi-Monitor mode in Settings first").

2. **Direct Bindings card renders.** Settings → Hotkeys tab → confirm "Direct Bindings" card with Accounts + Characters rows + "X/N bound" counts + two Configure buttons.

3. **Configure Account Hotkeys.** Click → `AccountHotkeysDialog` opens. Confirm one row per current Account (natedogg / flotte / acpots). Bind Alt+1 to natedogg. Save. Confirm tray menu → 🔑 Accounts → natedogg has "Alt+1" suffix. Press Alt+1 in-game → Account logs in, STOPS AT CHARSELECT (phantom-click gate unviolated per `eqswitch-dinput8.log`).

4. **Configure Character Hotkeys.** Similar. Bind F1 to "backup" Character. Save. Tray → 🧙 Characters → backup has "F1" suffix. Press F1 → Character launches AND enters world.

5. **Stale indicator test.** Accounts tab → add a throwaway Account "ZZTest". Open Hotkeys tab → Configure Account Hotkeys → bind Alt+T to ZZTest. Save. Go back to Accounts tab → delete ZZTest (CascadeDeleteDialog Cancel path since no linked chars; use Yes to confirm deletion). Re-open Hotkeys tab → expected: stale summary row "⚠ Stale bindings: 1 Account — open Configure to review". Open Configure Account Hotkeys → expected: red row `⚠ ZZTest (deleted) [Alt+T] [Rebind → ▾]`. Pick "natedogg" from the dropdown. Save. Reopen dialog → no stale row; natedogg shows `Alt+T` in its row.

6. **Stale clear via dropdown.** Reproduce step 5's stale state but pick `(none — clear binding)` from the dropdown. Save. Reopen dialog → no stale row; Alt+T is free.

7. **Conflict detection: cross-family.** Bind Alt+9 to an Account in Configure Account Hotkeys (save). Open Configure Character Hotkeys → bind Alt+9 to a Character → Save. Expected: modal "Cannot save — a combo is already bound to another action: Alt+9 → Character '...' vs Account '...'".

8. **Conflict detection: family-vs-tab.** Set Team Login 1 to Alt+9 (Hotkeys tab). Hit Save on the tab. Expected: P3.5-D modal blocks Save, lists "Alt+9 → Team Login 1, Account '...'".

9. **Legacy deprecation banner.** If live config has `QuickLogin1-4` populated (Nate's does), reopen Settings → Hotkeys. Expected: banner "ℹ Quick Login slots 1-4 moved to Direct Bindings…" above Direct Bindings card. Click Dismiss. Close Settings. Reopen. Banner gone.

10. **Legacy hotkey still works.** If the user had Alt+N bound to AutoLogin1 in v3 and hasn't remigrated yet, confirm Alt+N still fires via `FireLegacyQuickLoginSlot` routing.

Pass = all 10 green. Fail = specific callout + fix commit.

---

## Task 10 — Memory file update + STOP

- [ ] **Step 10.1: Append Phase 5a status line**

Append to `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v3_10_0_account_split.md`:

```
## Phase 5a shipped YYYY-MM-DD (commits 01de047..<HEAD> — <N> commits)

User-facing half of Phase 5. AccountHotkeysDialog + CharacterHotkeysDialog
modals bound from new Direct Bindings card on Hotkeys tab. Alt+M gate removed.
Stale-binding Rebind dropdown. Family-table dispatch wired in TrayManager.
Conflict-scan extended across Action + Team + legacy AutoLogin + family tables.

Deferred for Phase 5b: WindowManager.cs:437-439 v4 migration,
AffinityManager.cs:134 -> CharacterAliases, CharacterSelector.Decide()
extraction with 4 test cases.

STOP for Phase 5b sign-off.
```

- [ ] **Step 10.2: Create Phase 5b handoff stub**

```bash
cat > X:/_Projects/EQSwitch/PLAN_account_character_split_HANDOFF_phase5b.md << 'EOF'
# Phase 5b Handoff — mechanical consumer migration

**Prior phase:** Phase 5a shipped at HEAD <SHA>. <N> commits from 01de047.

## Phase 5b scope (mechanical refactor, 3-4 atomic commits)

1. **WindowManager.cs:437-439** — swap `_config.LegacyAccounts[slotIndex].CharacterName` for v4 Characters lookup. Characters list iteration already v4-compliant elsewhere; this is the last legacy read in WindowManager.
2. **AffinityManager.cs:134** — swap `_config.LegacyCharacterProfiles` reference for `_config.CharacterAliases`. Model shape identical; mechanical rename.
3. **Core/CharacterSelector.cs extraction** — pull the character-selection branch out of `AutoLoginManager.RunLoginSequence` into a pure function `Decide(int requestedSlot, string requestedName, string[] charNamesInHeap) -> (int, bool, string)`. Four test cases per master plan.

## Hard rules (carry forward from HANDOFF_phase4)

- Never regress phantom-click defenses (2 / 1).
- Stage specific files, never `git add -A`.
- Conventional commits under 72 chars.
- Parallel fire-and-forget in `FireTeam` preserved.
- StringComparison.Ordinal for Account / Character names.

## Workflow

1. Brainstorm (light — mostly mechanical, may not need UX questions).
2. Writing-plans.
3. Atomic commits.
4. 3-agent review.
5. Publish + deploy.
6. Smoke test.
7. STOP for v3.11.0 / Phase 6 sign-off.
EOF
```

- [ ] **Step 10.3: Commit handoff + STOP**

```bash
cd X:/_Projects/EQSwitch
git add PLAN_account_character_split_HANDOFF_phase5b.md
git commit -m "$(cat <<'EOF'
docs(handoff): Phase 5b new-session prompt

Mechanical consumer migration: WindowManager.cs:437-439 legacy read,
AffinityManager.cs:134 -> CharacterAliases, CharacterSelector.Decide()
pure-function extraction. Phase 5b is pure refactor — minimal UX
surface, can be lightly brainstormed if any.

Phase 5a (hotkey families + Hotkeys-tab redesign) signed off. Phase 5b
awaits Nate's go signal.

EOF
)" && git push
```

**STOP.** Do not start Phase 5b without explicit Nate sign-off.

---

## Self-Review

**1. Spec coverage:**
- Alt+M gate removal → Task 1 ✓
- HotkeyBindingUtil + HotkeysLegacyBannerDismissed + ReloadConfig sync → Task 2 ✓
- AccountHotkeysDialog → Task 3 ✓
- CharacterHotkeysDialog → Task 4 ✓
- Hotkeys tab Direct Bindings redesign + legacy banner → Task 5 ✓
- Family-table dispatch + Fire helpers → Task 6 ✓
- Conflict-scan extension → Task 7 ✓
- Agent fanout + publish + smoke test + handoff → Tasks 8-10 ✓
- Stale-binding balloon on orphan hotkey fire → folded into Task 6's `FireAccountHotkeyByName` / `FireCharacterHotkeyByName` helpers (spec §6 treats it inline, not a separate task). ✓

**2. Placeholder scan:** no TBD / "implement later" / "similar to Task N" / vague "handle edge cases" language. Every step has concrete code or exact commands.

**3. Type consistency:** `HotkeyBinding` = `{Combo, TargetName}` throughout. `HotkeyBindingUtil.IsPopulated(b)` used consistently (Tasks 2, 3, 4, 6, 7). `FindAccountByName` / `FindCharacterByName` method names match AppConfig (verified from Phase 3 spec). Dialog constructor signatures: `(IReadOnlyList<Account|Character>, IReadOnlyList<HotkeyBinding>, IReadOnlyList<(string, string)>)` consistent between Tasks 3 + 4 + the Task 5 callers in `OpenAccountHotkeysDialog` / `OpenCharacterHotkeysDialog`. `Result` property typed `List<HotkeyBinding>?` on both dialogs, consumed as such in Task 5.

**4. DarkTheme factory alignment:** only uses `StyleForm`, `MakeCard`, `AddCardLabel`, `AddCardHint`, `AddCardComboBox`, `AddCardButton`, `MakeButton`, `MakePrimaryButton`, `MakeTabPage`, `FgDimGray`, `FgWarn`, `CardWarn`, `CardGold`, `CardGreen`, `CardPurple`, `BgInput`, `BgMedium`, `FgWhite`. All verified present in DarkTheme.cs in the Phase 4 survey.

Plan is complete and ready for execution.

---

**Plan complete and saved to `docs/superpowers/plans/2026-04-15-eqswitch-phase5a-hotkey-families.md`.**
