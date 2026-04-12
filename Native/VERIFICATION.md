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

## 8. CXWndManager pWindows Offset

Scanned all candidate offsets (0x04-0x68) on the live CXWndManager* at charselect:

| Offset | Count | Alloc | 1st CXWnd* vtable | Status |
|--------|-------|-------|-------------------|--------|
| +0x08 | 630 | — | `0x00B5982C` | **VALID — primary** |
| +0x18 | 30 | — | `0x00B25908` | VALID (secondary array) |
| +0x28 | 630 | — | `0x00B5982C` | VALID (may be same data) |

**Confirmed: pWindows is at CXWndManager+0x08 on Dalaya ROF2.** The DLL's scan range (0x04-0x68) correctly finds this.

## 9. ASLR Analysis (dual-client verification)

Tested two simultaneous eqgame.exe processes (PID 29796 at charselect, PID 37212 at password):

| Module | PID 29796 | PID 37212 | ASLR |
|--------|-----------|-----------|------|
| eqgame.exe | `0x00540000` | `0x00540000` | **OFF** |
| dinput8.dll (MQ2) | `0x722E0000` | `0x722E0000` | **OFF** |
| eqmain.dll | `0x71490000` | Not at same addr | **ON** |

**All MQ2 export addresses and eqgame storage addresses are stable across processes.** The DLL can rely on hardcoded addresses from GetProcAddress — they won't shift between launches. eqmain.dll has ASLR enabled, so the DLL's runtime scanning (GetModuleHandleA + PE section scan) is the correct approach.

### Password Screen vs Charselect

| Value | Password | Charselect |
|-------|----------|------------|
| gGameState | 0 | 0 |
| CEverQuest* | `0x00FA7CCC` | `0x00FA7CCC` |
| CCharacterSelect* | NULL | `0x11254828` |
| CXWndManager* | NULL | `0x1C3FF958` |
| eqmain.dll | loaded (ASLR base) | UNLOADED |

CEverQuest* is the same in both (static global in eqgame .data). CCharacterSelect and CXWndManager are NULL at password screen — the DLL's null checks before deref are essential.

## 10. Function Export Byte Signatures

Captured for future validation (first 16 bytes at each export address):

```
GetChildItem   @ 7237FF80: 8B 54 24 04 E8 17 00 00 00 C2 04 00 CC CC CC CC
SetCurSel      @ 7237F2C0: A1 5C B0 42 72 FF E0 CC CC CC CC CC CC CC CC CC
GetCurSel      @ 7237F380: A1 D0 AF 42 72 FF E0 CC CC CC CC CC CC CC CC CC
GetItemText    @ 7237F360: A1 98 AE 42 72 FF E0 CC CC CC CC CC CC CC CC CC
SetWindowTextA @ 7237FD70: 8B 01 8D 80 24 01 00 00 8B 00 FF E0 CC CC CC CC
GetWindowTextA @ 7237FC40: A1 44 AF 42 72 FF E0 CC CC CC CC CC CC CC CC CC
WndNotification@ 7237FDB0: 8B 01 8D 80 88 00 00 00 8B 00 FF E0 CC CC CC CC
CXStr_ctor     @ 7237F3D0: A1 04 B2 42 72 FF E0 8B 45 FC CC CC CC CC CC CC
CXStr_dtor     @ 7237FCB0: A1 70 AD 42 72 FF E0 CC CC CC CC CC CC CC CC CC
```

Pattern: most are `A1 xx xx xx xx FF E0` — `mov eax, [addr]; jmp eax` (indirect jump stubs through a vtable pointer). `GetChildItem` and `WndNotification` use `8B 01` (mov eax, [ecx]) patterns instead.

## 11. MQ2 Export Offsets (from dinput8.dll base)

For reference if dinput8.dll base ever shifts:

| Export | Offset from dinput8 base |
|--------|-------------------------|
| gGameState | +0x1353C8 |
| ppEverQuest | +0x1377D8 |
| ppWndMgr | +0x1377EC |
| pinstCXWndMgr | +0x14AF7C |
| pinstCEQMainWnd | +0x14AE38 |
| pinstCCharSelect | +0x14B1A4 |

## Architecture Notes

- **charSelectPlayerArray (0x18EC0):** Standard MQ2 offset, does NOT exist in Dalaya ROF2. The array struct was likely removed or relocated in Dalaya's custom build. The DLL correctly falls back to slot-based UI mode.
- **Slot-based selection is correct for Dalaya:** `SetCurSel` by index works. Character names are unavailable via standard MQ2 interfaces but are not needed for the selection/enter-world workflow.
- **eqmain CXWndManager:** The DLL found a separate CXWndManager in eqmain.dll at .data+0x8294 (355 windows at login). eqmain.dll unloads at charselect transition — the DLL's stale-pointer guard is essential.
- **gGameState=0 for all non-ingame states:** Dalaya quirk. The DLL handles this by attempting charselect reads on all non-ingame states with structural validation.
- **CCharacterSelect vtable = 0x00B05410:** Stable across multiple sessions and reloads. Can be used as a validity check.
- **ASLR:** eqgame.exe and dinput8.dll (MQ2) load at fixed addresses. eqmain.dll uses ASLR. All hardcoded addresses from MQ2 exports are safe.
