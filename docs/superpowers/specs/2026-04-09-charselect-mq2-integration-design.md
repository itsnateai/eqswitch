# Character Select via MQ2 Exports — Design Spec

**Date**: 2026-04-09
**Status**: Approved
**Approach**: B — UI Window Scraping using Dalaya's MQ2 exports (proven MQ autologin pattern)

## Problem

EQSwitch's current auto-login hits Enter blindly at the character select screen, sending whichever character was last loaded. There's no way to:
- Know which characters are available
- Select a specific character by name
- Detect when the char select screen is actually ready (uses `IsHungAppWindow` heuristic)
- Report character data back to the C# host UI

`LoginAccount.CharacterSlot` (1-10) is stored in config but never used in `RunLoginSequence()`.

## Solution

Leverage Dalaya's `dinput8.dll` — which IS a custom MQ2 build with 2,966 named exports — to interact with the character select screen programmatically. This follows the exact same approach MQ2's own autologin plugin uses (see `macroquest/macroquest` repo, `src/plugins/autologin/StateMachine.cpp`).

## Key Discovery: Dalaya's MQ2 Export Surface

Dalaya's `dinput8.dll` (1.3MB, MD5: `EB9C1EA9738179DD542EEFB2A0F164EE`) exports critical MQ2 internals:

### Game State
| Export | Type | Purpose |
|--------|------|---------|
| `gGameState` | `int*` | Direct pointer to game state. `1` = CHARSELECT, `5` = INGAME |
| `GetGameState` | `int()` | Function returning current game state |

### EQ Core Objects
| Export | Type | Purpose |
|--------|------|---------|
| `ppEverQuest` | `CEverQuest**` | Pointer-to-pointer to main EQ object. Contains `charSelectPlayerArray` |
| `ppCharData` | `void**` | Current character data |
| `ppCharSpawn` | `void**` | Current character spawn |
| `ppWndMgr` | `void**` | Window manager — entry point to all UI |
| `ppSidlMgr` | `void**` | SIDL (XML UI definition) manager |

### UI Traversal & List Operations
| Export | Type | Purpose |
|--------|------|---------|
| `CSidlScreenWnd__GetChildItem` | method | Find child window by name |
| `CXWnd__GetChildItem` | method | Find child window by name |
| `CListWnd__GetItemText` | method | Read text from list row/column |
| `CListWnd__GetCurSel` | method | Get currently selected index |
| `CListWnd__SetCurSel` | method | Select a list item by index |

### MQ2 Command System
| Export | Type | Purpose |
|--------|------|---------|
| `HideDoCommand` | function | Execute MQ2/EQ command without echo |
| `DoCommandCmd` | function | Execute MQ2/EQ command |
| `AddCommand` | function | Register new MQ2 commands |
| `FindMQ2Data` | function | Query MQ2's TLO data system |

## Architecture

### Phase 1: Game State Detection + Character List Reading

**Where**: `eqswitch-hook.dll` (already injected into eqgame.exe)

1. **On DLL init**: Resolve MQ2 symbols via `GetModuleHandle("dinput8.dll")` + `GetProcAddress`
2. **Game state monitoring**: Replace `IsHungAppWindow` heuristic with `gGameState == 1` check
3. **Character list reading**: When at char select, read character names via UI approach:
   - Find "CharacterListWnd" window via `ppWndMgr`
   - Get "Character_List" child via `CSidlScreenWnd__GetChildItem`
   - Iterate rows with `CListWnd__GetItemText(i, 2)` — column 2 = character name
4. **Report to C# host**: Write character list to shared memory (extend `EQSwitchDI8_{PID}` or new `EQSwitchCharList_{PID}`)

### Phase 2: Programmatic Character Selection

1. **Select by name**: C# host writes desired character name to shared memory
2. **DLL reads request**: Finds matching index in Character_List
3. **Select**: `CListWnd__SetCurSel(index)` on the list widget
4. **Enter World**: Options in order of preference:
   a. `HideDoCommand` with MQ2 command if available
   b. UI button click on Enter World button
   c. Fall back to existing PulseKey3D Enter keypress (already works)

### Phase 3: Enhanced C# UI (future)

- Show character names, classes, levels in Quick Login config
- Select character by name dropdown instead of slot number
- Show "ready" indicator when char select is detected via game state

## Data Structures (from MQ2 source — `eqlib/include/eqlib/game/EverQuest.h`)

```cpp
struct CharSelectInfo  // size: 0x170 (368 bytes)
{
    /*0x000*/ char  Name[0x40];        // 64 chars
    /*0x040*/ int   Class;
    /*0x044*/ int   Race;
    /*0x048*/ BYTE  Level;
    /*0x04c*/ int   Class2;
    /*0x050*/ int   Race2;
    /*0x054*/ int   CurZoneID;
    /*0x058*/ BYTE  Sex;
    /*0x059*/ BYTE  Face;
    // ... appearance data ...
    /*0x140*/ int   Deity;
    /*0x15c*/ int   LastLogin;
    /*0x160*/ bool  bUseable;
    /*0x161*/ bool  bHeroicCharacter;
    /*0x162*/ bool  bShrouded;
};

// In CEverQuest object:
// GameState at offset 0x5E4 (int, values: 1=CHARSELECT, 5=INGAME)
// charSelectPlayerArray at offset 0x18EC0 (ArrayClass<CharSelectInfo>)
```

**CAVEAT**: These offsets are from the current MQ source targeting the Live 64-bit client. Dalaya's 32-bit ROF2 build will have different offsets. The UI approach (Phase 1-2) avoids this dependency. Memory reads (future) will need runtime validation.

## Game State Constants (from `eqlib/include/eqlib/game/Constants.h:439-446`)

```
GAMESTATE_PRECHARSELECT = -1
GAMESTATE_CHARSELECT    =  1
GAMESTATE_CHARCREATE    =  2
GAMESTATE_POSTCHARSELECT=  3
GAMESTATE_SOMETHING     =  4
GAMESTATE_INGAME        =  5
GAMESTATE_LOGGINGIN     = 253
GAMESTATE_UNLOADING     = 255
```

## CCharacterListWnd Methods (from `eqlib/include/eqlib/game/UI.h:2671-2701`)

```cpp
class CCharacterListWnd : public CSidlScreenWnd {
    void SelectCharacter(int charindex, bool bSwitchVisually = true, bool bForceUpdate = false);
    void EnterWorld();
    void Quit();
    void UpdateList(bool bForceUpdate = false);
    int  IsEmptyCharacterSlot(int);
    int  IsValidCharacterSelected();
    int  NumberOfCharacters();
    void EnableButtons(bool);
};
```

**Note**: `SelectCharacter` and `EnterWorld` are NOT exported from Dalaya's dinput8.dll. They're methods on eqgame.exe's `CCharacterListWnd` class. To call them, we'd need to:
1. Find the window object pointer
2. Look up the method via vtable or known offset in eqgame.exe
3. Or use the exported list widget functions (`SetCurSel` + Enter keypress) as a simpler alternative

## MQ2 Autologin Reference Flow (StateMachine.cpp:589-729)

```
CharacterSelect::entry()
  ├─ Get "Character_List" child of current window
  ├─ Check server + account validity
  ├─ Iterate list items, match character name (column 2, case-insensitive)
  ├─ pCharacterListWnd->SelectCharacter(index)
  └─ Transit to CharacterSelectWait (configurable delay)

CharacterSelectWait::react(LoginStateSensor)
  ├─ Verify selected character name still matches
  ├─ pCharacterListWnd->EnterWorld()
  └─ If EndAfterSelect, stop login
```

## Shared Memory Extension

Extend or add a new shared memory segment for character data:

```cpp
struct CharSelectShm {
    uint32_t magic;          // 0x45534353 "ESCS"
    uint32_t version;        // 1
    int      gameState;      // Current game state
    int      charCount;      // Number of characters
    int      selectedIndex;  // Currently selected index (-1 if none)
    int      requestedIndex; // C# host writes here to request selection (-1 = no request)
    bool     enterWorld;     // C# host sets true to request enter world
    char     chars[8][64];   // Up to 8 character names (padded to 64)
    int      levels[8];      // Character levels
    int      classes[8];     // Character classes
};
```

## Implementation Order

1. Add MQ2 symbol resolution to `eqswitch-hook.dll` (or new DLL)
2. Replace `IsHungAppWindow` with `gGameState` check in `AutoLoginManager.cs`
3. Read character list via UI exports, write to shared memory
4. C# host reads character data, shows in UI
5. Add character selection by name (SetCurSel + Enter)
6. Test with Dalaya (verify exports resolve, game state values match)

## Fallback Behavior (MQ2 exports missing)

If Dalaya pushes a new dinput8.dll that removes or renames the MQ2 exports:

- **Character selection by name**: DISABLED. Log warning: "MQ2 exports not found — character selection unavailable." No broken fallback code — arrow key navigation does NOT work in EQ's 3D char select, so there is no alternative input method.
- **Enter world**: Still works — Enter keypress on the default character (last played).
- **Game state detection**: Falls back to `IsHungAppWindow` heuristic (existing, functional).
- **C# UI**: Shows "MQ2 unavailable" indicator. Character name dropdown grayed out.

The `GetProcAddress` checks are a **gate**, not a graceful degradation:

```cpp
g_useMQ2 = (g_pGameState && g_fnGetItemText && g_fnSetCurSel && g_fnGetChildItem);
if (!g_useMQ2) {
    Log("WARN: MQ2 exports not found — character select by name disabled");
    // Report to C# host via shared memory
}
```

## Risks

- **Dalaya's MQ2 version**: May be an older fork with different export signatures. If exports disappear, character selection degrades to "enter world on default character" with clear logging.
- **MQ2 initialization timing**: Our DLL injects before game runs (CREATE_SUSPENDED). MQ2's dinput8.dll may not have resolved its own globals yet. May need to wait for `gGameState != -1` before accessing other exports.
- **Offset mismatch**: `charSelectPlayerArray` offset may differ in Dalaya's 32-bit ROF2 build vs. MQ source (targets 64-bit Live). UI approach avoids this.
- **Export calling conventions**: The mangled C++ names (e.g., `?GetItemText@CListWnd@EQClasses@@...`) are `__thiscall` — need `this` pointer as first arg on x86.

## Reference Source

MacroQuest source downloaded to: `X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/`
- Main repo: `macroquest/macroquest` (GitHub)
- EQ data structures: `src/eqlib/` submodule (`macroquest/eqlib`)
- Autologin plugin: `src/plugins/autologin/StateMachine.cpp`
- Character select struct: `src/eqlib/include/eqlib/game/EverQuest.h:662-703`
- Window class: `src/eqlib/include/eqlib/game/UI.h:2671-2701`
- Game state constants: `src/eqlib/include/eqlib/game/Constants.h:439-446`
- Globals: `src/eqlib/include/eqlib/game/Globals.h` (pCharacterListWnd, ppEverQuest, etc.)
