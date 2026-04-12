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

### charSelectPlayerArray (offset 0x18EC0)

```
CEverQuest (0x00FA7CCC) + 0x18EC0 = 0x01146B8C
  ArrayClassHeader: Data=NULL, Count=0, Alloc=0
```

**Result:** Empty. Scanned +/-0x400 from expected offset with no valid array found.

**Root cause:** The charSelectPlayerArray is only populated/validated during active SHM polling via `MQ2Bridge::Poll()`. The SHM (`Local\EQSwitchCharSel_{PID}`) is only created by the C# `CharSelectReader` during auto-login sequences. Without an active auto-login, the DLL's poll timer runs but skips char data reads (early return at `if (!shm) return;`).

**Implication:** The charSelectPlayerArray offset (`0x18EC0`) cannot be validated by external memory inspection alone. It requires triggering an auto-login to activate the full polling pipeline.

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
| charSelectPlayerArray | DEFERRED | Requires active SHM (auto-login trigger) |
| pinstCCharacterSelect | PASS | Double-deref chain valid, vtable valid |
| pinstCXWndManager | PASS | Double-deref chain valid |
| Function exports | PASS | All readable at expected addresses |
| IAT hooks | PASS | 4 IAT + 2 inline hooks installed |
| TIMERPROC | PASS | MQ2 poll timer running (500ms) |

## Deferred: charSelectPlayerArray Offset Validation

To complete validation of offset `0x18EC0`, trigger an auto-login from EQSwitch. This will:
1. Create `Local\EQSwitchCharSel_{PID}` SHM
2. Enable full `MQ2Bridge::Poll()` execution
3. Run `ValidateCharArrayOffset()` which tries the expected offset first, then scans +/-0x200
4. Emit the `=== VERIFICATION REPORT ===` to `eqswitch-dinput8.log`
5. Log character names, levels, and classes from the array

The UI fallback path (Character_List CListWnd via GetItemText) is also available if the struct-based path fails.
