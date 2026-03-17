# EQSwitch MaxFPS / MaxBGFPS — Implementation Brief

## Summary

- **AHK version**: No FPS support exists. Must be added to the Video Settings GUI (`OpenVideoModeEditor`).
- **C# port**: Already fully implemented in `EQClientSettingsForm.cs`. No work needed.

---

## eqclient.ini — Key Facts

Both `MaxFPS` and `MaxBGFPS` appear **twice** in the reference ini:

| Section | Key | Reference value |
|---------|-----|-----------------|
| `[Defaults]` | `MaxFPS` | 90 (line 139) |
| `[Defaults]` | `MaxBGFPS` | 90 (line 140) |
| `[Options]` | `MaxFPS` | 80 (line 186) |
| `[Options]` | `MaxBGFPS` | 65 (line 187) |

EQ reads the `[Options]` section values at runtime. The `[Defaults]` section values are also present in the reference but the authoritative location EQ uses for in-game FPS cap is `[Options]`. The C# port writes to `[Defaults]` (this is a known quirk — see gotcha below).

The C# `EQClientSettingsForm` reads from `[Defaults]` (its `LoadFromIni` only parses that section for most keys) and writes to `[Defaults]` via `SetIniValue`. This means the C# implementation updates the `[Defaults]` copy, not `[Options]`.

For the AHK version, follow the same convention as the C# port: read/write `[Defaults]`. This keeps both versions consistent.

---

## C# Port — Status: Already Done

File: `X:/_Projects/eqswitch_port/eqswitch_port/UI/EQClientSettingsForm.cs`

The C# port has complete MaxFPS/MaxBGFPS support:
- Fields `_nudMaxFPS` and `_nudMaxBGFPS` (NumericUpDown, 0-99)
- `InitializeForm()` around line 360: "FPS Limits" section header, two NumericUpDown controls laid out side by side
- `LoadFromIni()` around line 623: reads `maxfps` and `maxbgfps` from `[Defaults]` section
- `SaveSettings()` line 686-687: saves to `_config.EQClientIni.MaxFPS/MaxBGFPS`
- `ApplyToIni()` lines 774-778: writes to `[Defaults]` with guard `> 0`
- `EnforceOverrides()` lines 906-910: enforces on launch with guard `> 0`
- `ConfiguredKeys` includes `"MaxFPS"` and `"MaxBGFPS"` (HashSet pattern — only written if user saved)
- `AppConfig.cs` lines 455-459: `EQClientIniConfig.MaxFPS` and `MaxBGFPS` properties (default 0 = don't override)

**No action needed on the C# port.**

---

## AHK Version — Full Implementation Steps

### Files to Modify

Only one file: `X:/_Projects/eqswitch_ahk/EQSwitch.ahk`

### Where to Add It

Inside `OpenVideoModeEditor()` (lines 2397-2543), which already handles eqclient.ini. This is the right home — FPS limits are a per-client EQ setting, not an EQSwitch app setting. They belong alongside resolution and windowed mode, not in the main Settings GUI.

Specifically: add the FPS controls after the WindowedMode + title bar offset block (around line 2487), before the Save/Apply/Close/Reset buttons row (line 2488).

### Global Variables

No new globals needed. FPS values are read directly from eqclient.ini at GUI open time and written back on Apply/Save. They do NOT need to be stored as EQSwitch config globals (they live in eqclient.ini, not eqswitch.cfg).

### Step 1 — Add section header and controls inside `OpenVideoModeEditor`

Insert after line 2487 (`vm.AddText("xm y+10 w400 h1 0x10")`), before the buttons:

```autohotkey
    ; --- FPS Limits ---
    vm.AddText("xm y+10 w400 h1 0x10")
    vm.SetFont("s10 Bold", "Segoe UI")
    vm.AddText("xm y+6 w400 c0xAA3300", "🎮  FPS Limits")
    vm.SetFont("s9", "Segoe UI")

    ; Read current values from [Defaults] section
    ReadFPS(key, def) {
        try return Integer(IniRead(iniPath, "Defaults", key))
        catch
            return def
    }
    curMaxFPS   := ReadFPS("MaxFPS",   0)
    curMaxBGFPS := ReadFPS("MaxBGFPS", 0)

    vm.AddText("xm y+6", "MaxFPS:")
    vmMaxFPS := vm.AddEdit("x+4 yp-2 w50 Number", curMaxFPS > 0 ? curMaxFPS : "")
    vm.AddUpDown("Range0-99", curMaxFPS)
    vm.AddText("x+4 yp+2 cGray", "(0-99)")

    vm.AddText("x+14 yp-2", "MaxBGFPS:")
    vmMaxBGFPS := vm.AddEdit("x+4 yp-2 w50 Number", curMaxBGFPS > 0 ? curMaxBGFPS : "")
    vm.AddUpDown("Range0-99", curMaxBGFPS)
    vm.AddText("x+4 yp+2 cGray", "(0-99)")

    vm.AddText("xm y+3 cGray w400", "0 = no override. EQ must restart for changes to take effect.")
```

### Step 2 — Add writes inside `ApplyVM()`

In `ApplyVM()` (around line 2510), after the existing `IniWrite` calls and before `ShowTip`:

```autohotkey
            ; FPS limits — write to [Defaults] (same section as C# port)
            fpsVal   := Integer(vmMaxFPS.Value)
            bgfpsVal := Integer(vmMaxBGFPS.Value)
            if (fpsVal > 0)
                IniWrite(fpsVal,   iniPath, "Defaults", "MaxFPS")
            if (bgfpsVal > 0)
                IniWrite(bgfpsVal, iniPath, "Defaults", "MaxBGFPS")
```

The `> 0` guard matches the C# port's behavior: value of 0 means "don't override," so we skip writing it. This prevents accidentally zeroing out a value the user didn't intend to touch.

### Step 3 — Add to `ResetVMDefaults()`

In `ResetVMDefaults()` (around line 2493), add resets for the new fields:

```autohotkey
        vmMaxFPS.Value   := ""
        vmMaxBGFPS.Value := ""
```

### Complete `ApplyVM` after changes (for reference)

The function should look like:

```autohotkey
    ApplyVM(*) {
        global FIX_TOP_OFFSET
        try {
            IniWrite(vmWW.Value, iniPath, "VideoMode", "WindowedWidth")
            IniWrite(vmWH.Value, iniPath, "VideoMode", "WindowedHeight")
            IniWrite(vmXOff.Value, iniPath, "VideoMode", "WindowedModeXOffset")
            IniWrite(vmYOff.Value, iniPath, "VideoMode", "WindowedModeYOffset")
            IniWrite(vmWindowedMode.Value ? "TRUE" : "FALSE", iniPath, "Defaults", "WindowedMode")
            FIX_TOP_OFFSET := vmTitleBar.Value
            IniWrite(FIX_TOP_OFFSET, CFG_FILE, "EQSwitch", "FIX_TOP_OFFSET")
            ; FPS limits
            fpsVal   := Integer(vmMaxFPS.Value)
            bgfpsVal := Integer(vmMaxBGFPS.Value)
            if (fpsVal > 0)
                IniWrite(fpsVal, iniPath, "Defaults", "MaxFPS")
            if (bgfpsVal > 0)
                IniWrite(bgfpsVal, iniPath, "Defaults", "MaxBGFPS")
            ShowTip("🖥 Video settings saved — toggle window mode or use Fix Windows to apply")
        } catch as err {
            ShowTip("⚠ Failed to save: " err.Message, 5000)
        }
    }
```

---

## UI Placement Recommendation

### AHK — Video Settings GUI layout after changes:

```
┌─ 🖥 eqclient.ini — [VideoMode] ──────────────────────┐
│ Preset: [dropdown]                                     │
│ WindowedWidth: [___]  WindowedHeight: [___]           │
│ WindowedModeXOffset: [__]  WindowedModeYOffset: [__]  │
│ Tip: Leave offsets at 0...                            │
├───────────────────────────────────────────────────────┤
│ 🎮 Window Mode                                        │
│ [x] WindowedMode (windowed)                           │
│ Fullscreen mode is not tested...                      │
│ Title bar offset: [__] px                             │
├───────────────────────────────────────────────────────┤
│ 🎮 FPS Limits            ← NEW SECTION               │
│ MaxFPS: [__] (0-99)   MaxBGFPS: [__] (0-99)          │
│ 0 = no override. EQ must restart for changes.        │
├───────────────────────────────────────────────────────┤
│ [Save] [Apply] [Close] [Reset Defaults]               │
└───────────────────────────────────────────────────────┘
```

The FPS section goes between Window Mode and the button row. It is NOT placed in the main Settings GUI — it belongs in Video Settings alongside the other eqclient.ini values.

### C# — Already placed correctly

In `EQClientSettingsForm`, FPS Limits appear after the clip plane controls as a dedicated section header with two NumericUpDowns side by side. No changes needed.

---

## Gotchas and Dependencies

### 1. AHK `Integer()` on empty Edit

If the user clears the Edit box and leaves it blank, `Integer("")` throws in AHK v2. Wrap in try/catch or check for empty before converting:

```autohotkey
fpsVal := 0
try fpsVal := Integer(vmMaxFPS.Value)
```

Alternatively use `vmMaxFPS.Value != "" ? Integer(vmMaxFPS.Value) : 0`.

### 2. Section ambiguity: `[Defaults]` vs `[Options]`

`MaxFPS`/`MaxBGFPS` exist in both sections in a real eqclient.ini. EQ's actual runtime behavior uses `[Options]`. However:
- The C# port already writes to `[Defaults]` only, so for consistency the AHK version should do the same.
- If a user needs to update `[Options]` values, they can open eqclient.ini directly (already available via the tray menu "📄 eqclient.ini" item).
- Do NOT write to both sections — it causes confusion about which one EQ actually honors.

### 3. `ReadFPS` nested function scope in AHK

The `ReadFPS` helper is a nested function inside `OpenVideoModeEditor`. It captures `iniPath` from the outer scope — this works correctly in AHK v2 for nested functions reading outer locals. No `global` declaration needed since `iniPath` is a local of `OpenVideoModeEditor`.

### 4. UpDown range vs Edit field

`AddUpDown("Range0-99", ...)` constrains the spinner buttons to 0-99 but does not prevent a user from typing a higher number directly into the Edit. This matches how all other UpDown controls in the AHK Settings GUI work (e.g., opacity, PiP size). It is acceptable behavior — EQ itself will ignore values above 99.

### 5. C# `> 0` guard means 0 is "don't override"

The C# port never writes 0 to the ini (guard: `if (int)_nudMaxFPS.Value > 0`). This means you cannot use this UI to explicitly set MaxFPS=0. This is intentional — 0 in EQ means "unlimited," and that would be a bad default to accidentally apply. The AHK implementation should match this same guard.

### 6. CHANGELOG and README updates

After implementing in AHK:
- Add CHANGELOG.md entry under Bug Fixes or New Features (whichever version bump applies)
- `eqclient.ini` reference file in `eqswitch_ahk/` does not need updating (it's a reference, not generated)
- AHK CLAUDE.md "Video Settings GUI" description should note FPS controls were added

---

## Files Modified Summary

| Version | File | Change |
|---------|------|--------|
| AHK | `EQSwitch.ahk` | Add FPS section + controls to `OpenVideoModeEditor`, add writes to `ApplyVM`, add resets to `ResetVMDefaults` |
| AHK | `CHANGELOG.md` | Add entry |
| AHK | `CLAUDE.md` | Update Video Settings section description |
| C# | — | No changes needed — already implemented |

---

## Implementation Order

1. Open `EQSwitch.ahk`
2. Locate `OpenVideoModeEditor` (~line 2397)
3. Add `ReadFPS` helper + control declarations after the Window Mode block (~line 2487)
4. Add writes inside `ApplyVM` (~line 2510)
5. Add resets inside `ResetVMDefaults` (~line 2493)
6. Test: open Video Settings, verify controls appear, set values, click Apply, confirm eqclient.ini `[Defaults]` section is updated
7. Update CHANGELOG.md and CLAUDE.md
8. Compile and verify build succeeds
