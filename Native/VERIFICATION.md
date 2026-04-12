# DLL Address Verification — 2026-04-11

## Environment

- **eqgame.exe** PID 38936, base `0x00540000` (19120 KB)
- **dinput8.dll** (MQ2/Dalaya) base `0x722E0000`
- **eqmain.dll** base `0x71490000`
- **EQSwitch.exe** PID 3148 (C# host)
- **State:** Character select screen, "Backup" character selected (slot 2)
- **Tool:** Python ctypes ReadProcessMemory (equivalent to CE 7.5 memory read)

## 1. MQ2 Export Resolution

All exports from Dalaya's MQ2 dinput8.dll resolved successfully:

| Export | Address | Value | Status |
|--------|---------|-------|--------|
| `gGameState` | `0x724153C8` | `0` (direct int) | Correct for Dalaya |
| `ppEverQuest` | `0x724177D8` | `0x00FA7CCC` (CEverQuest*) | Valid |
| `ppWndMgr` | `0x724177EC` | - | Resolved |
| `pinstCXWndMgr` | `0x7242AF7C` | `0x01713D00` (storage) | Valid |
| `pinstCEQMainWnd` | `0x7242AE38` | - | Resolved |
| `pinstCCharSelect` | `0x7242B1A4` | `0x00E5FC34` (storage) | Valid |

**Note:** Dalaya ROF2 uses `gameState=0` for both login AND charselect (not the standard EQ `gameState=5`). The DLL code handles this by attempting charselect reads on all non-ingame states.

## 2. ppEverQuest Chain

```
ppEverQuest export   @ 0x724177D8
  -> CEverQuest*     = 0x00FA7CCC  (in eqgame.exe .data segment)
```

Single deref from the export gives `CEverQuest*`. The address `0x00FA7CCC` falls within eqgame.exe's address range (0x00540000-0x017F0000), confirming it's in the game's data segment.

### charSelectPlayerArray (offset 0x18EC0) — DOES NOT EXIST

```
CEverQuest (0x00FA7CCC) + 0x18EC0: Data=NULL, Count=0, Alloc=0
DLL scan +-0x200: NOT FOUND (repeated every 500ms poll cycle)
External scan +-0x80000 (512KB): NOT FOUND
Process-wide pointer scan: 0 ArrayClassHeader candidates
```

**Result: FAIL — Dalaya ROF2 does not use the standard charSelectPlayerArray.**

Verified with active SHM (auto-login triggered, `Local\EQSwitchCharSel_{PID}` open):
- DLL `ValidateCharArrayOffset()` scans offset 0x18EC0 +-0x200 every poll — never finds it
- Full 512KB scan from CEverQuest finds zero valid ArrayClassHeader+name patterns
- Process-wide search for pointers to "Natedogg" name strings: 6 hits, all heap CXStr objects

Character names exist in memory as **inline CXStr buffers** (e.g., `0x01C6BBB8` = "Natedogg" as raw ASCII, `0x01C6BC70` = CXStr string buffer), but not in any ArrayClassHeader array structure accessible via a CEverQuest offset.

**Fallback path confirmed working:**
- `FindWindowByName('Character_List')` → found at `0x1BDB2B50` via pinstCCharacterSelect
- `GetCurSel()` → returns correct slot index (1 = second slot)
- `GetItemText()` → returns empty (Dalaya CListWnd doesn't expose text this way)
- **Slot-based mode active:** uses slot indices directly (10 slots), which is sufficient for `SetCurSel` character selection

**Implication:** The DLL's slot-based fallback is the correct and only viable path for Dalaya ROF2. Character selection by index works. Character names are not available through MQ2's standard interfaces on this build.

## 3. pinstCCharacterSelect Chain

```
pinstCCharacterSelect export @ 0x7242B1A4
  -> storage addr    = 0x00E5FC34  (in eqgame.exe .data)
  -> CCharacterSelect* = 0x1E899CC8  (heap allocation)
  -> vtable          = 0x00B05410  (in eqgame.exe .text)
```

**Result:** Valid double-deref chain. The CCharacterSelect window object exists at charselect and has a vtable pointing into eqgame.exe's code section. Confirmed live at character select.

At DLL init time (before charselect loaded), `CCharacterSelect*` was `0x00000000`. The pointer populated after the game transitioned to character select.

## 4. pinstCXWndManager Chain

```
pinstCXWndMgr export @ 0x7242AF7C
  -> storage addr    = 0x01713D00  (in eqgame.exe .data)
  -> CXWndManager*   = 0x10D8D558  (heap allocation)
```

**Result:** Valid double-deref chain. CXWndManager is live at charselect.

At DLL init time, `CXWndManager*` was `0x00000000`. Populated after game UI initialization.

## 5. MQ2 Function Exports

| Function | Address | First 4 bytes | Status |
|----------|---------|---------------|--------|
| `GetChildItem` | `0x7237FF80` | `0x0424548B` | Readable |
| `SetCurSel` | `0x7237F2C0` | `0x42B05CA1` | Readable |
| `GetCurSel` | `0x7237F380` | `0x42AFD0A1` | Readable |
| `GetItemText` | `0x7237F360` | `0x42AE98A1` | Readable |
| `SetWindowTextA` | `0x7237FD70` | `0x808D018B` | Readable |
| `GetWindowTextA` | `0x7237FC40` | `0x42AF44A1` | Readable |
| `CXStr ctor` | `0x7237F3D0` | - | Resolved |
| `CXStr dtor` | `0x7237FCB0` | - | Resolved |
| `WndNotification` | `0x7237FDB0` | - | Resolved |

All function addresses are within dinput8.dll's code section and readable.

## 6. Verification Summary

| Component | Status | Notes |
|-----------|--------|-------|
| MQ2 export resolution | PASS | All 14+ exports resolved from dinput8.dll |
| gGameState | PASS | Value=0, correct for Dalaya charselect |
| ppEverQuest deref | PASS | Valid CEverQuest* in eqgame.exe .data |
| charSelectPlayerArray | FAIL | Does not exist in Dalaya ROF2 build |
| pinstCCharacterSelect | PASS | Double-deref chain valid, vtable valid |
| pinstCXWndManager | PASS | Double-deref chain valid |
| Function exports | PASS | All readable at expected addresses |
| IAT hooks | PASS | 4 IAT + 2 inline hooks installed |
| TIMERPROC | PASS | MQ2 poll timer running (500ms) |

## 7. DLL Verification Report (from auto-login trigger)

```
=== VERIFICATION REPORT (charselect) ===
  pinstCCharacterSelect: export=7242B1A4 -> storage=0x00E5FC34 -> CCharacterSelect*=1BDB23F8
    vtable=00B05410
    GetChildItem('Character_List')=1BDB2B50
    Character_List.GetCurSel()=1
  pinstCXWndManager: export=7242AF7C -> storage=0x01713D00 -> CXWndManager*=0625EFE8
  ppEverQuest: export=724177D8 -> CEverQuest*=00FA7CCC
  SHM: charCount=10 selectedIndex=1 mq2Available=1 gameState=0
=== END VERIFICATION REPORT ===
```

The report confirms all pointer chains resolve live. The `charCount=10` is from slot-based mode (10 fixed slots), not from the charSelectPlayerArray.

## Architecture Notes

- **charSelectPlayerArray (0x18EC0):** Standard MQ2 offset, does NOT exist in Dalaya ROF2. The array struct was likely removed or relocated in Dalaya's custom build. The DLL correctly falls back to slot-based UI mode.
- **Slot-based selection is correct for Dalaya:** `SetCurSel` by index works. Character names are unavailable via standard MQ2 interfaces but are not needed for the selection/enter-world workflow.
- **eqmain CXWndManager:** The DLL found a separate CXWndManager in eqmain.dll at offset 0x8294 (355 windows). This is used for FindWindowByName operations.
- **gGameState=0 for all non-ingame states:** Dalaya quirk. The DLL handles this by attempting charselect reads on all non-ingame states with structural validation.
