# EQSwitch v2.0 Deferred Features — Design Spec

**Date:** 2026-03-11
**Status:** Approved

## Overview

Seven deferred features for the EQSwitch C# port. All integrate into the existing dark-themed WinForms UI and follow established patterns (single-instance forms, dark theme colors, AppConfig persistence).

---

## Feature 1: Process Manager GUI

**New file:** `UI/ProcessManagerForm.cs`

Dark-themed form with a `ListView` (Details view) showing live EQ client state:

| Column | Width | Source |
|--------|-------|--------|
| Slot | 50 | `SlotIndex + 1` |
| PID | 70 | `ProcessId` |
| Character | 150 | `CharacterName ?? WindowTitle` |
| Priority | 100 | `AffinityManager.GetProcessPriorityName(pid)` |
| Affinity | 120 | `AffinityManager.GetProcessAffinity(pid)` as `0xHEX` |

- 1-second auto-refresh timer
- "Refresh" button for manual refresh
- "Force Apply" button re-applies affinity/priority rules to all clients
- System info header: core count + system mask
- Tray menu: "Process Manager" item replaces current "Process Info" balloon
- Single-instance window pattern (same as SettingsForm)
- Form size: 550x350, non-resizable

**Integration:** TrayManager holds `_processManagerForm`, opens via tray menu. Force Apply calls `AffinityManager.ApplyAffinityRules` with force flag (bypasses "active client unchanged" check).

**AffinityManager change:** Add `ForceApplyAffinityRules()` that resets `_lastActiveClient` then calls `ApplyAffinityRules`.

---

## Feature 2: PiP Settings Tab

**Modified file:** `UI/SettingsForm.cs`

New "PiP" tab in the TabControl with controls:

- `CheckBox` — PiP Enabled
- `ComboBox` — Size Preset (Small/Medium/Large/XL/XXL/Custom)
- `NumericUpDown` × 2 — Custom Width (100-1920), Custom Height (100-1080). Enabled only when "Custom" selected.
- `NumericUpDown` — Opacity (0-255, or TrackBar)
- `CheckBox` — Show Border
- `ComboBox` — Border Color (Green/Blue/Red/Black)
- `NumericUpDown` — Max Windows (1-3)

All fields map directly to `AppConfig.Pip`. Added to `PopulateFromConfig()` and `ApplySettings()`.

**ReloadConfig change:** Add PiP config fields to TrayManager.ReloadConfig.

---

## Feature 3: "All" / "None" Quick-Select

**Modified file:** `UI/SettingsForm.cs` (Affinity tab)

Two small buttons below the mask textboxes:
- "All Cores" → sets both Active and Background mask textboxes to system max mask (e.g. `FFFF` for 16 cores)
- "Clear" → sets both to `1` (minimum — Windows requires at least one core)

Uses `AffinityManager.DetectCores()` to get system mask at click time.

---

## Feature 4: Force Apply (Tray)

**Modified file:** `UI/TrayManager.cs`

New tray menu item "Force Apply Affinity" in the context menu (near existing "Process Info" location). Calls `_affinityManager.ForceApplyAffinityRules(clients, activeClient)`.

Also available in Process Manager GUI (Feature 1).

---

## Feature 5: Character Backup Section

**Modified file:** `UI/SettingsForm.cs`

New "Characters" tab in the TabControl:

- `ListView` (Details view) showing character profiles: Name, Class, Slot, Affinity Override
- "Export..." button → `SaveFileDialog` (filter: `*.json`) → serializes `_config.Characters` to JSON
- "Import..." button → `OpenFileDialog` (filter: `*.json`) → deserializes, replaces `_config.Characters`
- Import shows count in balloon: "Imported N character profiles"

Uses `System.Text.Json` for serialization (already used by ConfigManager).

---

## Feature 6: Triple-Click Tray

**Modified file:** `UI/TrayManager.cs`

Click counting in `OnTrayMouseClick`:
- Track left-click count + timestamp of first click
- If 3 clicks within 500ms → trigger action (arrange windows)
- Reset counter after 500ms cooldown or after triggering
- Doesn't interfere with double-click (which is handled by `MouseDoubleClick` event)

Implementation: `_trayClickCount` int, `_trayFirstClickTick` long. On left click: if within 500ms of first click, increment; if count reaches 3, fire `OnArrangeWindows()` and reset. If outside 500ms, reset counter to 1.

---

## Feature 7: Desktop Shortcut

**Modified file:** `UI/TrayManager.cs`

New tray menu item "Create Desktop Shortcut" in the context menu.

Uses `IWshRuntimeLibrary` COM interop (WScript.Shell) to create a `.lnk` file:
- Target: `Application.ExecutablePath`
- Working directory: `AppDomain.CurrentDomain.BaseDirectory`
- Icon: same exe (embedded icon)
- Location: `Environment.GetFolderPath(SpecialFolder.Desktop)`

Fallback: if COM fails, create a simple `.url` file or show error balloon.

---

## Implementation Order

1. **AffinityManager changes** (ForceApply method) — dependency for F1 and F4
2. **Process Manager GUI** (F1) — new file, biggest feature
3. **PiP Settings Tab** (F2) — SettingsForm addition
4. **Affinity quick-select** (F3) — SettingsForm addition
5. **Character Backup** (F5) — SettingsForm addition
6. **Force Apply tray item** (F4) — TrayManager addition
7. **Triple-click** (F6) — TrayManager addition
8. **Desktop shortcut** (F7) — TrayManager addition
9. **ReloadConfig updates** — wire PiP config into TrayManager.ReloadConfig
10. **TODO.md update** — mark all items complete

## Files Modified

- `Core/AffinityManager.cs` — add `ForceApplyAffinityRules()`
- `UI/SettingsForm.cs` — add PiP tab, Characters tab, All/None buttons on Affinity tab
- `UI/TrayManager.cs` — Process Manager, Force Apply, triple-click, desktop shortcut, ReloadConfig PiP fields
- `UI/ProcessManagerForm.cs` — **new file**
- `TODO.md` — mark items complete
- `CHANGELOG.md` — add v2.1.0 entry
