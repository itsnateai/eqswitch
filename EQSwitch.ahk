; =========================================================
; EQSwitch.ahk - EverQuest Window Switcher
; Requires AutoHotkey v2  (https://www.autohotkey.com)
;
; Setup:
;   1. Run EQSwitch.exe (or EQSwitch.ahk)
;   2. Right-click tray icon -> Settings
;   3. Point the exe path at your eqgame.exe
;   4. Set your preferred hotkey
;   5. Save and enjoy
;
; https://github.com/itsnateai/eqswitch
; =========================================================

#Requires AutoHotkey v2.0
#SingleInstance Force

g_version        := "1.5"
CFG_FILE         := A_ScriptDir "\eqswitch.cfg"
EQ_TITLE         := "EverQuest"
SETTINGS_OPEN    := false
TOOLTIP_MS       := 2000
g_multiMonState  := 0

; =========================================================
; HELPERS
; =========================================================

GetEqDir() {
    global EQ_EXE
    SplitPath(EQ_EXE, , &dir)
    return dir "\"
}

GetVisibleEqWindows() {
    global EQ_TITLE
    visible := []
    for id in WinGetList(EQ_TITLE) {
        if WinGetStyle("ahk_id " id) & 0x10000000
            visible.Push(id)
    }
    Loop visible.Length - 1 {
        i := A_Index
        Loop visible.Length - i {
            j := A_Index
            if (visible[j] > visible[j+1]) {
                tmp          := visible[j]
                visible[j]   := visible[j+1]
                visible[j+1] := tmp
            }
        }
    }
    return visible
}

; =========================================================
; CONFIG
; =========================================================
LoadConfig() {
    global EQ_EXE, EQ_ARGS, EQ_HOTKEY, CFG_FILE, DBLCLICK_LAUNCH
    global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES, RECENT_CHARS
    global EQ_SERVER, LAUNCH_DELAY, LAUNCH_FIX_DELAY, NUM_CLIENTS
    global TOGGLE_BEEP, FIX_MODE, STARTUP_ENABLED, MULTIMON_HOTKEY

    ; Migrate from old "EQ2Box" section name (pre-v1.2 rename)
    try {
        IniRead(CFG_FILE, "EQ2Box", "EQ_EXE")
        ; Old section found — migrate all keys to "EQSwitch"
        for key in ["EQ_EXE", "EQ_ARGS", "EQ_HOTKEY", "DBLCLICK_LAUNCH",
                     "GINA_PATH", "NOTES_FILE", "MIDCLICK_NOTES", "RECENT_CHARS"] {
            try {
                val := IniRead(CFG_FILE, "EQ2Box", key)
                IniWrite(val, CFG_FILE, "EQSwitch", key)
            }
        }
        IniDelete(CFG_FILE, "EQ2Box")
    }

    ReadKey(key, def) {
        try {
            return IniRead(CFG_FILE, "EQSwitch", key)
        } catch {
            return def
        }
    }

    EQ_EXE           := ReadKey("EQ_EXE",           "C:\EverQuest\eqgame.exe")
    EQ_ARGS          := ReadKey("EQ_ARGS",           "-patchme")
    EQ_HOTKEY        := ReadKey("EQ_HOTKEY",         "\")
    DBLCLICK_LAUNCH  := ReadKey("DBLCLICK_LAUNCH",   "0")
    GINA_PATH        := ReadKey("GINA_PATH",         "")
    NOTES_FILE       := ReadKey("NOTES_FILE",        "")
    MIDCLICK_NOTES   := ReadKey("MIDCLICK_NOTES",    "0")
    RECENT_CHARS     := ReadKey("RECENT_CHARS",      "")
    EQ_SERVER        := ReadKey("EQ_SERVER",         "dalaya")
    LAUNCH_DELAY     := ReadKey("LAUNCH_DELAY",      "3000")
    LAUNCH_FIX_DELAY := ReadKey("LAUNCH_FIX_DELAY",  "15000")
    NUM_CLIENTS      := ReadKey("NUM_CLIENTS",       "2")
    TOGGLE_BEEP      := ReadKey("TOGGLE_BEEP",       "0")
    FIX_MODE         := ReadKey("FIX_MODE",          "maximize")
    STARTUP_ENABLED  := ReadKey("STARTUP_ENABLED",   "0")
    MULTIMON_HOTKEY  := ReadKey("MULTIMON_HOTKEY",   ">!m")
}

SaveConfig() {
    global CFG_FILE, EQ_EXE, EQ_ARGS, EQ_HOTKEY, DBLCLICK_LAUNCH
    global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES, RECENT_CHARS
    global EQ_SERVER, LAUNCH_DELAY, LAUNCH_FIX_DELAY, NUM_CLIENTS
    global TOGGLE_BEEP, FIX_MODE, STARTUP_ENABLED, MULTIMON_HOTKEY
    IniWrite(EQ_EXE,           CFG_FILE, "EQSwitch", "EQ_EXE")
    IniWrite(EQ_ARGS,          CFG_FILE, "EQSwitch", "EQ_ARGS")
    IniWrite(EQ_HOTKEY,        CFG_FILE, "EQSwitch", "EQ_HOTKEY")
    IniWrite(DBLCLICK_LAUNCH,  CFG_FILE, "EQSwitch", "DBLCLICK_LAUNCH")
    IniWrite(GINA_PATH,        CFG_FILE, "EQSwitch", "GINA_PATH")
    IniWrite(NOTES_FILE,       CFG_FILE, "EQSwitch", "NOTES_FILE")
    IniWrite(MIDCLICK_NOTES,   CFG_FILE, "EQSwitch", "MIDCLICK_NOTES")
    IniWrite(RECENT_CHARS,     CFG_FILE, "EQSwitch", "RECENT_CHARS")
    IniWrite(EQ_SERVER,        CFG_FILE, "EQSwitch", "EQ_SERVER")
    IniWrite(LAUNCH_DELAY,     CFG_FILE, "EQSwitch", "LAUNCH_DELAY")
    IniWrite(LAUNCH_FIX_DELAY, CFG_FILE, "EQSwitch", "LAUNCH_FIX_DELAY")
    IniWrite(NUM_CLIENTS,      CFG_FILE, "EQSwitch", "NUM_CLIENTS")
    IniWrite(TOGGLE_BEEP,      CFG_FILE, "EQSwitch", "TOGGLE_BEEP")
    IniWrite(FIX_MODE,         CFG_FILE, "EQSwitch", "FIX_MODE")
    IniWrite(STARTUP_ENABLED,  CFG_FILE, "EQSwitch", "STARTUP_ENABLED")
    IniWrite(MULTIMON_HOTKEY,  CFG_FILE, "EQSwitch", "MULTIMON_HOTKEY")
}

LoadConfig()

; Keep the tray tooltip in sync with the active switch key
UpdateTrayTip() {
    global EQ_HOTKEY, g_version
    keyDisplay := (EQ_HOTKEY != "") ? EQ_HOTKEY : "none"
    A_IconTip  := "EQ Switch v" g_version "  |  Switch key: " keyDisplay
}
UpdateTrayTip()

; Auto-open Settings on first run if no config file found
isFirstRun := !FileExist(CFG_FILE)
if isFirstRun {
    SetTimer(FirstRunWelcome, -300)
}

FirstRunWelcome(*) {
    global TOOLTIP_MS
    ToolTip("👋 Welcome to EQ Switch!`nOpening Settings — be sure to set your Switch Hotkey!")
    SetTimer(() => ToolTip(), -4000)
    SetTimer(OpenSettings, -500)
}

; =========================================================
; HOTKEY
; =========================================================
BindHotkey(key) {
    if (key = "")
        return true
    HotIfWinActive("ahk_exe eqgame.exe")
    try {
        Hotkey(key, SwitchWindow, "On")
        HotIfWinActive()
        return true
    } catch {
        HotIfWinActive()
        return false
    }
}

SwitchWindow(*) {
    global TOGGLE_BEEP, TOOLTIP_MS

    visible := GetVisibleEqWindows()

    if (visible.Length = 0) {
        ToolTip("No EverQuest windows found!")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
        return
    }
    if (visible.Length = 1) {
        ToolTip("Only one EverQuest window open!")
        SetTimer(() => ToolTip(), -700)
        return
    }

    activeID     := WinGetID("A")
    currentIndex := 0
    for i, id in visible {
        if (id = activeID) {
            currentIndex := i
            break
        }
    }

    nextIndex := Mod(currentIndex, visible.Length) + 1
    try WinActivate("ahk_id " visible[nextIndex])

    if (TOGGLE_BEEP = "1")
        SoundBeep(800, 50)
}

BindHotkey(EQ_HOTKEY)
BindMultiMonHotkey(MULTIMON_HOTKEY)

; =========================================================
; MENU HELPERS
; =========================================================

; Bold menu items by matching their text (resilient to reordering)
SetMenuItemsBold(hMenu, itemTexts) {
    static MIIM_STATE := 0x1, MFS_DEFAULT := 0x1000
    cbSize := (A_PtrSize = 8) ? 80 : 48
    mii    := Buffer(cbSize, 0)
    NumPut("UInt", cbSize,      mii,  0)
    NumPut("UInt", MIIM_STATE,  mii,  4)
    NumPut("UInt", MFS_DEFAULT, mii, 12)
    count := DllCall("GetMenuItemCount", "Ptr", hMenu)
    buf   := Buffer(512, 0)
    Loop count {
        pos := A_Index - 1
        DllCall("GetMenuString", "Ptr", hMenu, "UInt", pos, "Ptr", buf, "Int", 256, "UInt", 0x400)
        itemText := StrGet(buf)
        for text in itemTexts {
            if InStr(itemText, text) {
                DllCall("SetMenuItemInfo", "Ptr", hMenu, "UInt", pos, "Int", 1, "Ptr", mii)
                break
            }
        }
    }
}

; Add a char name to the recent-chars list stored in the cfg
AddRecentChar(charName) {
    global RECENT_CHARS
    existing := (RECENT_CHARS != "") ? StrSplit(RECENT_CHARS, "|") : []
    newList  := []
    for name in existing {
        if (name != "" && name != charName)
            newList.Push(name)
    }
    newList.InsertAt(1, charName)
    while (newList.Length > 10)
        newList.Pop()
    joined := ""
    for i, name in newList
        joined .= (i > 1 ? "|" : "") name
    RECENT_CHARS := joined
    SaveConfig()
}

; =========================================================
; CHARACTER PROFILES
; =========================================================
GetProfileNames() {
    global CFG_FILE
    names := []
    try {
        section := IniRead(CFG_FILE, "Profiles")
        for line in StrSplit(section, "`n") {
            if (line = "")
                continue
            eqPos := InStr(line, "=")
            if (eqPos > 0)
                names.Push(SubStr(line, 1, eqPos - 1))
        }
    }
    return names
}

GetProfileChars(profileName) {
    global CFG_FILE
    try {
        val := IniRead(CFG_FILE, "Profiles", profileName)
        if (val = "")
            return []
        return StrSplit(val, "|")
    }
    return []
}

SaveProfileData(profileName, chars) {
    global CFG_FILE
    joined := ""
    for i, name in chars
        joined .= (i > 1 ? "|" : "") name
    IniWrite(joined, CFG_FILE, "Profiles", profileName)
}

DeleteProfileData(profileName) {
    global CFG_FILE
    try IniDelete(CFG_FILE, "Profiles", profileName)
}

; =========================================================
; TRAY
; =========================================================
iconPath := A_ScriptDir "\eqbox.ico"
if FileExist(iconPath)
    TraySetIcon(iconPath)
A_IconTip := "EQ Switch"

A_TrayMenu.Delete()
OnMessage(0x404, TrayClick)
TrayClick(wParam, lParam, *) {
    global DBLCLICK_LAUNCH, MIDCLICK_NOTES
    if (lParam = 0x203) {  ; WM_LBUTTONDBLCLK
        if (DBLCLICK_LAUNCH = "1")
            LaunchOne()
        else
            OpenSettings()
    }
    if (lParam = 0x208) {  ; WM_MBUTTONUP — middle-click opens notes
        if (MIDCLICK_NOTES = "1")
            OpenNotes()
    }
}

; Decorated title banner
A_TrayMenu.Add("⚔ ══════ ✨  EQ Switch  ✨ ══════ ⚔", (*) => 0)
A_TrayMenu.Disable("⚔ ══════ ✨  EQ Switch  ✨ ══════ ⚔")
A_TrayMenu.Add()

; Launch items — made bold by SetMenuItemsBold() below
A_TrayMenu.Add("⚔  Launch Client",     LaunchOne)
A_TrayMenu.Add("🎮  Launch Both",       LaunchBoth)
A_TrayMenu.Add()

A_TrayMenu.Add("🪟  Fix Windows",       (*) => FixWindows())
A_TrayMenu.Add("🔄  Swap Windows",      (*) => SwapWindows())
A_TrayMenu.Add()

A_TrayMenu.Add("📜  Open Log File",     (*) => OpenLogFile())
A_TrayMenu.Add("📂  Open eqclient.ini", (*) => OpenEqClientIni())
A_TrayMenu.Add("🎯  Open Gina",         (*) => OpenGina())
A_TrayMenu.Add("📝  Open Notes",        (*) => OpenNotes())
A_TrayMenu.Add()

A_TrayMenu.Add("🌐  Dalaya Wiki",       (*) => Run("https://wiki.dalaya.org/"))
A_TrayMenu.Add("🌐  Shards Wiki",       (*) => Run("https://wiki.shardsofdalaya.com/wiki/Main_Page"))
A_TrayMenu.Add("🌐  Dalaya Fomelo",     (*) => Run("https://dalaya.org/fomelo/"))
A_TrayMenu.Add()

A_TrayMenu.Add("⚙  Settings",          (*) => OpenSettings())
A_TrayMenu.Add()
A_TrayMenu.Add("✖  Exit",              (*) => ExitApp())

; Apply bold to Launch Client + Launch Both (by text match, not position)
SetMenuItemsBold(A_TrayMenu.Handle, ["Launch Client", "Launch Both"])

; =========================================================
; FIX WINDOWS
; =========================================================
FixWindows(*) {
    global FIX_MODE, TOOLTIP_MS
    winList := GetVisibleEqWindows()
    if (winList.Length = 0) {
        ToolTip("No EverQuest windows found!")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
        return
    }

    if (FIX_MODE = "sidebyside") {
        ; Arrange side-by-side across the primary monitor
        try MonitorGetWorkArea(1, &mLeft, &mTop, &mRight, &mBottom)
        catch {
            ToolTip("⚠ Could not read monitor info")
            SetTimer(() => ToolTip(), -TOOLTIP_MS)
            return
        }
        mWidth  := mRight - mLeft
        mHeight := mBottom - mTop
        count   := winList.Length
        sliceW  := mWidth // count
        Loop count {
            id := winList[A_Index]
            x  := mLeft + (A_Index - 1) * sliceW
            try {
                WinRestore("ahk_id " id)
                WinMove(x, mTop, sliceW, mHeight, "ahk_id " id)
            }
        }
    } else if (FIX_MODE = "multimonitor") {
        ; Distribute windows across monitors (one per monitor, maximized)
        monCount := MonitorGetCount()
        count := winList.Length
        Loop count {
            id := winList[A_Index]
            mon := Mod(A_Index - 1, monCount) + 1
            try {
                MonitorGetWorkArea(mon, &mLeft, &mTop, &mRight, &mBottom)
                WinRestore("ahk_id " id)
                WinMove(mLeft, mTop, mRight - mLeft, mBottom - mTop, "ahk_id " id)
            }
        }
    } else if (FIX_MODE = "restore") {
        Loop winList.Length {
            id := winList[A_Index]
            try WinRestore("ahk_id " id)
        }
    } else {
        ; Default: maximize
        Loop winList.Length {
            id := winList[A_Index]
            try WinMaximize("ahk_id " id)
        }
    }
}

; =========================================================
; SWAP / MULTI-MONITOR TOGGLE
; =========================================================
SwapWindows(*) {
    global TOOLTIP_MS
    visible := GetVisibleEqWindows()
    if (visible.Length < 2) {
        ToolTip("Need at least 2 EQ windows to swap!")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
        return
    }
    ; Read current positions
    positions := []
    for id in visible {
        try {
            WinGetPos(&x, &y, &w, &h, "ahk_id " id)
            positions.Push({x: x, y: y, w: w, h: h})
        } catch {
            ToolTip("⚠ A window closed during swap — try again")
            SetTimer(() => ToolTip(), -TOOLTIP_MS)
            return
        }
    }
    ; Rotate each window to the next window's position
    count := visible.Length
    Loop count {
        nextPos := positions[Mod(A_Index, count) + 1]
        id := visible[A_Index]
        try {
            WinRestore("ahk_id " id)
            WinMove(nextPos.x, nextPos.y, nextPos.w, nextPos.h, "ahk_id " id)
        }
    }
    ToolTip("🔄 Windows swapped!")
    SetTimer(() => ToolTip(), -TOOLTIP_MS)
}

ToggleMultiMon(*) {
    global g_multiMonState, TOOLTIP_MS
    visible := GetVisibleEqWindows()
    if (visible.Length < 2) {
        ToolTip("Need at least 2 EQ windows!")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
        return
    }
    monCount := MonitorGetCount()
    if (monCount < 2) {
        ToolTip("Need at least 2 monitors!")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
        return
    }

    ; Cycle: 0 = stacked, 1..N = spread with rotation offset
    if (g_multiMonState > visible.Length)
        g_multiMonState := 0
    g_multiMonState := Mod(g_multiMonState + 1, visible.Length + 1)

    if (g_multiMonState = 0) {
        ; Stack all windows on primary monitor (maximize)
        Loop visible.Length {
            id := visible[A_Index]
            try WinMaximize("ahk_id " id)
        }
        ToolTip("🪟 Multi-monitor OFF — stacked on primary")
    } else {
        ; Spread across monitors with rotation offset
        offset := g_multiMonState - 1
        count  := visible.Length
        Loop count {
            winIdx := Mod(A_Index - 1 + offset, count) + 1
            id     := visible[winIdx]
            mon    := Mod(A_Index - 1, monCount) + 1
            try {
                MonitorGetWorkArea(mon, &mLeft, &mTop, &mRight, &mBottom)
                WinRestore("ahk_id " id)
                WinMove(mLeft, mTop, mRight - mLeft, mBottom - mTop, "ahk_id " id)
            }
        }
        if (g_multiMonState = 1)
            ToolTip("🖥 Multi-monitor ON")
        else
            ToolTip("🔄 Multi-monitor swapped")
    }
    SetTimer(() => ToolTip(), -TOOLTIP_MS)
}

BindMultiMonHotkey(key) {
    if (key = "")
        return true
    try {
        Hotkey(key, ToggleMultiMon, "On")
        return true
    } catch {
        return false
    }
}

; =========================================================
; OPEN LOG FILE
; =========================================================
OpenLogFile(*) {
    global EQ_SERVER, TOOLTIP_MS

    eqDir := GetEqDir()

    lastInput := ""
    loop {
        charName := InputBox("Enter character name:", "📜 Open Log File", "w300 h120", lastInput)
        if (charName.Result = "Cancel" || charName.Value = "")
            return

        logPath := eqDir "Logs\eqlog_" charName.Value "_" EQ_SERVER ".txt"

        if !FileExist(logPath) {
            lastInput := charName.Value
            ToolTip("📜 Log not found for: " charName.Value)
            SetTimer(() => ToolTip(), -TOOLTIP_MS)
            continue
        }

        Run('notepad.exe "' logPath '"')
        return
    }
}

; =========================================================
; OPEN EQCLIENT.INI
; =========================================================
OpenEqClientIni(*) {
    global TOOLTIP_MS
    eqDir   := GetEqDir()
    iniPath := eqDir "eqclient.ini"
    if !FileExist(iniPath) {
        ToolTip("📂 eqclient.ini not found — check your EQ path in Settings")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
        return
    }
    Run('notepad.exe "' iniPath '"')
}

; =========================================================
; OPEN GINA
; =========================================================
OpenGina(*) {
    global GINA_PATH, TOOLTIP_MS
    if (GINA_PATH = "") {
        ToolTip("🎯 No Gina path set — open Settings to configure it")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
        return
    }
    if !FileExist(GINA_PATH) {
        ToolTip("🎯 Gina not found at configured path — check Settings")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
        return
    }
    SplitPath(GINA_PATH, , &ginaDir)
    Run('"' GINA_PATH '"', ginaDir)
}

; =========================================================
; OPEN NOTES
; =========================================================
OpenNotes(*) {
    global NOTES_FILE, CFG_FILE
    notesPath := NOTES_FILE

    if (notesPath = "") {
        defaultPath := A_ScriptDir "\notes.txt"
        ; Default notes.txt already exists — persist its path so Settings shows it
        if FileExist(defaultPath) {
            notesPath  := defaultPath
            NOTES_FILE := defaultPath
            SaveConfig()
        } else {
            ; First-run: ask the user
            result := MsgBox(
                "No notes file configured.`n`n" .
                "Would you like to select an existing .txt file?`n`n" .
                "Click 'No' to create notes.txt in the EQ Switch folder.",
                "EQ Switch — Notes Setup", "YesNo Icon?")
            if (result = "Yes") {
                f := FileSelect(, , "Select your notes .txt file", "Text Files (*.txt)")
                if (f = "")
                    return
                notesPath  := f
                NOTES_FILE := f
                SaveConfig()
            } else {
                ; Create the default and save its real path so Settings shows it
                notesPath  := defaultPath
                FileAppend("== EQ Notes ==`n`n", notesPath)
                NOTES_FILE := defaultPath
                SaveConfig()
            }
        }
    }

    ; Safety-net: create the file if it vanished
    if !FileExist(notesPath)
        FileAppend("== EQ Notes ==`n`n", notesPath)

    Run('notepad.exe "' notesPath '"')
}

; =========================================================
; SETTINGS GUI
; =========================================================
OpenSettings(*) {
    global EQ_EXE, EQ_ARGS, EQ_HOTKEY, DBLCLICK_LAUNCH, SETTINGS_OPEN
    global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES, RECENT_CHARS
    global EQ_SERVER, NUM_CLIENTS, TOGGLE_BEEP, FIX_MODE, STARTUP_ENABLED
    global MULTIMON_HOTKEY, g_version, TOOLTIP_MS
    if SETTINGS_OPEN
        return
    SETTINGS_OPEN := true

    ; Always reset the flag on any close path — X button, Alt-F4, crash, anything
    CleanupSettings(*) {
        global SETTINGS_OPEN
        SETTINGS_OPEN := false
    }

    try {

    g := Gui("+AlwaysOnTop", "⚔ EQ Switch v" g_version " — Options")
    g.OnEvent("Close",   CleanupSettings)   ; X button / Alt-F4
    g.OnEvent("Escape",  CleanupSettings)   ; Escape key
    g.SetFont("s9", "Segoe UI")
    g.MarginX := 14
    g.MarginY := 12

    ; ── ⌨ Window Switch Hotkey — THE MAIN FEATURE ─────────
    g.SetFont("s11 Bold", "Segoe UI")
    g.AddText("xm w440 c0xAA3300", "⌨  Window Switch Hotkey  ⭐")
    g.SetFont("s9", "Segoe UI")
    g.AddText("xm y+6 w440 h2 0x10")
    g.AddText("xm y+7 w440 c0xAA3300",
        "Set the key you will press while inside EverQuest Switch clients")
    activeKeyDisplay := (EQ_HOTKEY != "") ? EQ_HOTKEY : "(not set — please set one!)"
    statusColor := (EQ_HOTKEY != "") ? "c0x007700" : "c0xCC0000"
    g.AddText("xm y+8 w440 " statusColor, "Currently active key:  " activeKeyDisplay)
    g.AddText("xm y+10", "Press a new key combination below to change it:")
    hotkeyCtrl := g.AddHotkey("xm y+4 w200", EQ_HOTKEY)
    g.AddText("x+10 yp+4 w220 cGray", "← click the box and press your desired key")

    ; ── ⚔ EverQuest ──────────────────────────────────────
    g.AddText("xm y+14 w440 cNavy", "⚔  EverQuest")
    g.AddText("xm y+6 w440 h1 0x10")

    g.AddText("xm y+8", "EQ Executable:")
    exeEdit := g.AddEdit("xm y+4 w396", EQ_EXE)
    g.AddButton("x+4 yp w36 h24", "...").OnEvent("Click", (*) => BrowseExe(exeEdit))

    g.AddText("xm y+8", "Launch arguments:")
    argsEdit := g.AddEdit("xm y+4 w440", EQ_ARGS)

    g.AddText("xm y+8", "Server name (used for log/ini file paths):")
    serverEdit := g.AddEdit("xm y+4 w200", EQ_SERVER)

    ; ── 🎮 Launch Options ────────────────────────────────
    g.AddText("xm y+14 w440 cNavy", "🎮  Launch Options")
    g.AddText("xm y+6 w440 h1 0x10")

    g.AddText("xm y+8", "Number of clients to launch:")
    clientsEdit := g.AddEdit("xm y+4 w60", NUM_CLIENTS)

    g.AddText("xm y+8", "Window arrangement after launch:")
    fixModes := ["maximize", "restore", "sidebyside", "multimonitor"]
    fixModeCombo := g.AddDropDownList("xm y+4 w200", fixModes)
    for i, mode in fixModes {
        if (mode = FIX_MODE)
            fixModeCombo.Choose(i)
    }
    if (fixModeCombo.Value = 0)
        fixModeCombo.Choose(1)

    beepChk := g.AddCheckbox("xm y+8", "Play beep sound on window switch")
    beepChk.Value := (TOGGLE_BEEP = "1") ? 1 : 0

    g.AddText("xm y+10", "Multi-monitor toggle hotkey (global — works outside EQ):")
    multimonHkCtrl := g.AddHotkey("xm y+4 w200", MULTIMON_HOTKEY)
    g.AddText("x+10 yp+4 w220 cGray", "Default: Right Alt + M")

    ; ── 🎯 Open Gina ────────────────────────────────────
    g.AddText("xm y+14 w440 cNavy", "🎯  Open Gina")
    g.AddText("xm y+6 w440 h1 0x10")
    g.AddText("xm y+8", "Path to Gina.exe:")
    ginaEdit := g.AddEdit("xm y+4 w396", GINA_PATH)
    g.AddButton("x+4 yp w36 h24", "...").OnEvent("Click", (*) => BrowseGina(ginaEdit))

    ; ── 📝 Notes File ────────────────────────────────────
    g.AddText("xm y+14 w440 cNavy", "📝  Notes File")
    g.AddText("xm y+6 w440 h1 0x10")
    g.AddText("xm y+8", "Path to .txt notes file (leave blank to use notes.txt in script folder):")
    notesEdit := g.AddEdit("xm y+4 w396", NOTES_FILE)
    g.AddButton("x+4 yp w36 h24", "...").OnEvent("Click", (*) => BrowseNotes(notesEdit))

    ; ── 🖱 Tray Icon ─────────────────────────────────────
    g.AddText("xm y+14 w440 cNavy", "🖱  Tray Icon")
    g.AddText("xm y+6 w440 h1 0x10")
    dblClickChk := g.AddCheckbox("xm y+8", "Double-click tray icon launches a client")
    dblClickChk.Value := (DBLCLICK_LAUNCH = "1") ? 1 : 0
    midClickChk := g.AddCheckbox("xm y+6", "Middle-click tray icon opens Notepad notes")
    midClickChk.Value := (MIDCLICK_NOTES = "1") ? 1 : 0
    startupChk := g.AddCheckbox("xm y+6", "Run EQ Switch at Windows startup")
    startupChk.Value := (STARTUP_ENABLED = "1") ? 1 : 0

    ; ── 📋 Character Profiles & Backup ────────────────────
    g.AddText("xm y+14 w440 cNavy", "📋  Character Profiles & Backup")
    g.AddText("xm y+6 w440 h1 0x10")
    g.AddText("xm y+8", "Copies UI_Name_server.ini and Name_server.ini to/from your Desktop.")

    ; Single-character backup/restore
    g.AddText("xm y+8", "Character name (type or pick a recent name):")
    recentList := (RECENT_CHARS != "") ? StrSplit(RECENT_CHARS, "|") : []
    charCombo  := g.AddComboBox("xm y+4 w210", recentList)
    if (recentList.Length > 0)
        charCombo.Choose(1)
    g.AddButton("x+8 yp w110 h24", "💾 Backup").OnEvent("Click", (*) => DoBackup(charCombo.Text))
    g.AddButton("x+4 yp w110 h24", "📥 Restore").OnEvent("Click", (*) => DoRestore(charCombo.Text))

    ; Profile management
    g.AddText("xm y+12 w440 cGray", "── Profiles ──────────────────────────────────────")
    g.AddText("xm y+6", "Profile:")
    profileNames := GetProfileNames()
    profileDropdown := g.AddDropDownList("x+8 yp-2 w150", profileNames)
    g.AddButton("x+4 yp w50 h24", "Load").OnEvent("Click", (*) => DoLoadProfile(profileDropdown, charCombo))
    g.AddButton("x+4 yp w76 h24", "Save As...").OnEvent("Click", (*) => DoSaveProfile(charCombo, profileDropdown))
    g.AddButton("x+4 yp w60 h24", "Delete").OnEvent("Click", (*) => DoDeleteProfile(profileDropdown))
    profileCharsText := g.AddText("xm y+4 w440 cGray", "")
    if (profileDropdown.Value > 0)
        UpdateProfileDisplay(profileDropdown, profileCharsText)
    profileDropdown.OnEvent("Change", (*) => UpdateProfileDisplay(profileDropdown, profileCharsText))
    g.AddButton("xm y+4 w215 h24", "💾 Backup All in Profile").OnEvent("Click", (*) => DoBackupAll(profileDropdown))
    g.AddButton("x+8 yp w215 h24", "📥 Restore All in Profile").OnEvent("Click", (*) => DoRestoreAll(profileDropdown))

    ; ── Save / Cancel ─────────────────────────────────────
    g.AddText("xm y+14 w440 h1 0x10")
    g.AddButton("xm y+8 w80 h28 Default", "💾 Save").OnEvent("Click", SaveAndClose)
    g.AddButton("x+8 yp w80 h28", "Cancel").OnEvent("Click", (*) => (SETTINGS_OPEN := false, g.Destroy()))

    g.Show("AutoSize")

    ; ---- Inner helpers (closures) -------------------------

    BrowseExe(ctrl) {
        f := FileSelect(, ctrl.Value, "Select eqgame.exe", "Executable (*.exe)")
        if f
            ctrl.Value := f
    }

    BrowseGina(ctrl) {
        f := FileSelect(, ctrl.Value, "Select Gina.exe", "Executable (*.exe)")
        if f
            ctrl.Value := f
    }

    BrowseNotes(ctrl) {
        f := FileSelect(, ctrl.Value, "Select notes .txt file", "Text Files (*.txt)")
        if f
            ctrl.Value := f
    }

    DoBackup(charName) {
        global EQ_SERVER, TOOLTIP_MS
        if (charName = "") {
            MsgBox("Please enter or select a character name.", "EQ Switch — Backup", "Icon!")
            return
        }
        eqDir   := GetEqDir()
        desktop := EnvGet("USERPROFILE") "\Desktop\"
        file1   := eqDir "UI_" charName "_" EQ_SERVER ".ini"
        file2   := eqDir charName "_" EQ_SERVER ".ini"
        found   := 0
        errors  := ""
        if FileExist(file1) {
            try {
                FileCopy(file1, desktop "UI_" charName "_" EQ_SERVER ".ini", 1)
                found++
            } catch as err {
                errors .= "UI file: " err.Message "`n"
            }
        }
        if FileExist(file2) {
            try {
                FileCopy(file2, desktop charName "_" EQ_SERVER ".ini", 1)
                found++
            } catch as err {
                errors .= "Char file: " err.Message "`n"
            }
        }
        if (errors != "") {
            MsgBox("Some files failed to copy:`n" errors, "EQ Switch — Backup", "Icon!")
            return
        }
        if (found = 0) {
            MsgBox("No character files found for '" charName "'`nin: " eqDir, "EQ Switch — Backup", "Icon!")
            return
        }
        ; Save to recent list and refresh the combobox
        AddRecentChar(charName)
        charCombo.Delete()
        fresh := (RECENT_CHARS != "") ? StrSplit(RECENT_CHARS, "|") : []
        charCombo.Add(fresh)
        charCombo.Text := charName
        MsgBox(found " file(s) backed up to Desktop ✓", "EQ Switch — Backup", "Icon!")
    }

    DoRestore(charName) {
        global EQ_SERVER
        if (charName = "") {
            MsgBox("Please enter or select a character name.", "EQ Switch — Restore", "Icon!")
            return
        }
        eqDir   := GetEqDir()
        desktop := EnvGet("USERPROFILE") "\Desktop\"
        file1   := desktop "UI_" charName "_" EQ_SERVER ".ini"
        file2   := desktop charName "_" EQ_SERVER ".ini"
        if (!FileExist(file1) && !FileExist(file2)) {
            MsgBox("No backup files found on Desktop for '" charName "'.", "EQ Switch — Restore", "Icon!")
            return
        }
        result := MsgBox(
            "Restore character files for '" charName "' from Desktop?`n`n" .
            "This will OVERWRITE current files in your EQ folder.",
            "EQ Switch — Restore", "YesNo Icon!")
        if (result != "Yes")
            return
        found  := 0
        errors := ""
        if FileExist(file1) {
            try {
                FileCopy(file1, eqDir "UI_" charName "_" EQ_SERVER ".ini", 1)
                found++
            } catch as err {
                errors .= "UI file: " err.Message "`n"
            }
        }
        if FileExist(file2) {
            try {
                FileCopy(file2, eqDir charName "_" EQ_SERVER ".ini", 1)
                found++
            } catch as err {
                errors .= "Char file: " err.Message "`n"
            }
        }
        if (errors != "")
            MsgBox("Some files failed to restore:`n" errors, "EQ Switch — Restore", "Icon!")
        else
            MsgBox(found " file(s) restored from Desktop ✓", "EQ Switch — Restore", "Icon!")
    }

    UpdateProfileDisplay(dropdown, textCtrl) {
        if (dropdown.Value = 0 || dropdown.Text = "") {
            textCtrl.Value := ""
            return
        }
        chars := GetProfileChars(dropdown.Text)
        if (chars.Length = 0) {
            textCtrl.Value := ""
            return
        }
        display := "Characters: "
        for i, c in chars
            display .= (i > 1 ? ", " : "") c
        textCtrl.Value := display
    }

    DoLoadProfile(dropdown, combo) {
        global RECENT_CHARS, TOOLTIP_MS
        if (dropdown.Value = 0 || dropdown.Text = "") {
            MsgBox("Please select a profile first.", "EQ Switch — Profiles", "Icon!")
            return
        }
        chars := GetProfileChars(dropdown.Text)
        if (chars.Length = 0) {
            MsgBox("Profile is empty.", "EQ Switch — Profiles", "Icon!")
            return
        }
        joined := ""
        for i, c in chars
            joined .= (i > 1 ? "|" : "") c
        RECENT_CHARS := joined
        SaveConfig()
        combo.Delete()
        combo.Add(chars)
        combo.Choose(1)
        ToolTip("Loaded profile: " dropdown.Text)
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
    }

    DoSaveProfile(combo, dropdown) {
        global RECENT_CHARS, TOOLTIP_MS
        chars := (RECENT_CHARS != "") ? StrSplit(RECENT_CHARS, "|") : []
        if (chars.Length = 0) {
            MsgBox("No characters in the recent list to save.`nType a character name and run a backup first.", "EQ Switch — Profiles", "Icon!")
            return
        }
        charDisplay := ""
        for i, c in chars
            charDisplay .= (i > 1 ? ", " : "") c
        result := InputBox("Enter a name for this profile:`n`nCharacters: " charDisplay, "Save Profile", "w350 h150")
        if (result.Result = "Cancel" || result.Value = "")
            return
        SaveProfileData(result.Value, chars)
        names := GetProfileNames()
        dropdown.Delete()
        dropdown.Add(names)
        for i, n in names {
            if (n = result.Value) {
                dropdown.Choose(i)
                break
            }
        }
        UpdateProfileDisplay(dropdown, profileCharsText)
        ToolTip("Profile saved: " result.Value)
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
    }

    DoDeleteProfile(dropdown) {
        global TOOLTIP_MS
        if (dropdown.Value = 0 || dropdown.Text = "") {
            MsgBox("Please select a profile to delete.", "EQ Switch — Profiles", "Icon!")
            return
        }
        profileName := dropdown.Text
        result := MsgBox("Delete profile '" profileName "'?", "EQ Switch — Profiles", "YesNo Icon?")
        if (result != "Yes")
            return
        DeleteProfileData(profileName)
        names := GetProfileNames()
        dropdown.Delete()
        dropdown.Add(names)
        profileCharsText.Value := ""
        ToolTip("Profile deleted: " profileName)
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
    }

    DoBackupAll(dropdown) {
        global EQ_SERVER, TOOLTIP_MS
        if (dropdown.Value = 0 || dropdown.Text = "") {
            MsgBox("Please select a profile first.", "EQ Switch — Backup All", "Icon!")
            return
        }
        chars := GetProfileChars(dropdown.Text)
        if (chars.Length = 0) {
            MsgBox("Profile is empty.", "EQ Switch — Backup All", "Icon!")
            return
        }
        eqDir   := GetEqDir()
        desktop := EnvGet("USERPROFILE") "\Desktop\"
        total   := 0
        errors  := ""
        for _, charName in chars {
            file1 := eqDir "UI_" charName "_" EQ_SERVER ".ini"
            file2 := eqDir charName "_" EQ_SERVER ".ini"
            if FileExist(file1) {
                try {
                    FileCopy(file1, desktop "UI_" charName "_" EQ_SERVER ".ini", 1)
                    total++
                } catch as err {
                    errors .= charName " UI: " err.Message "`n"
                }
            }
            if FileExist(file2) {
                try {
                    FileCopy(file2, desktop charName "_" EQ_SERVER ".ini", 1)
                    total++
                } catch as err {
                    errors .= charName " Char: " err.Message "`n"
                }
            }
        }
        if (errors != "")
            MsgBox("Some files failed:`n" errors, "EQ Switch — Backup All", "Icon!")
        else if (total = 0)
            MsgBox("No character files found for this profile.", "EQ Switch — Backup All", "Icon!")
        else
            MsgBox(total " file(s) backed up to Desktop ✓`nProfile: " dropdown.Text, "EQ Switch — Backup All", "Icon!")
    }

    DoRestoreAll(dropdown) {
        global EQ_SERVER
        if (dropdown.Value = 0 || dropdown.Text = "") {
            MsgBox("Please select a profile first.", "EQ Switch — Restore All", "Icon!")
            return
        }
        chars := GetProfileChars(dropdown.Text)
        if (chars.Length = 0) {
            MsgBox("Profile is empty.", "EQ Switch — Restore All", "Icon!")
            return
        }
        charList := ""
        for i, c in chars
            charList .= (i > 1 ? ", " : "") c
        result := MsgBox(
            "Restore ALL character files for profile '" dropdown.Text "'?`n`n" .
            "Characters: " charList "`n`n" .
            "This will OVERWRITE current files in your EQ folder.",
            "EQ Switch — Restore All", "YesNo Icon!")
        if (result != "Yes")
            return
        eqDir   := GetEqDir()
        desktop := EnvGet("USERPROFILE") "\Desktop\"
        total   := 0
        errors  := ""
        for _, charName in chars {
            file1 := desktop "UI_" charName "_" EQ_SERVER ".ini"
            file2 := desktop charName "_" EQ_SERVER ".ini"
            if FileExist(file1) {
                try {
                    FileCopy(file1, eqDir "UI_" charName "_" EQ_SERVER ".ini", 1)
                    total++
                } catch as err {
                    errors .= charName " UI: " err.Message "`n"
                }
            }
            if FileExist(file2) {
                try {
                    FileCopy(file2, eqDir charName "_" EQ_SERVER ".ini", 1)
                    total++
                } catch as err {
                    errors .= charName " Char: " err.Message "`n"
                }
            }
        }
        if (errors != "")
            MsgBox("Some files failed:`n" errors, "EQ Switch — Restore All", "Icon!")
        else if (total = 0)
            MsgBox("No backup files found on Desktop for this profile.", "EQ Switch — Restore All", "Icon!")
        else
            MsgBox(total " file(s) restored from Desktop ✓`nProfile: " dropdown.Text, "EQ Switch — Restore All", "Icon!")
    }

    SaveAndClose(*) {
        global EQ_EXE, EQ_ARGS, EQ_HOTKEY, DBLCLICK_LAUNCH
        global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES
        global EQ_SERVER, NUM_CLIENTS, TOGGLE_BEEP, FIX_MODE, STARTUP_ENABLED
        global MULTIMON_HOTKEY, TOOLTIP_MS

        newHotkey := hotkeyCtrl.Value
        newMultimonHk := multimonHkCtrl.Value

        ; Unbind the old hotkeys
        HotIfWinActive("ahk_exe eqgame.exe")
        try Hotkey(EQ_HOTKEY, "Off")
        HotIfWinActive()
        if (MULTIMON_HOTKEY != "")
            try Hotkey(MULTIMON_HOTKEY, "Off")

        ; Validate EQ path (non-blocking warning)
        if (exeEdit.Value != "" && !FileExist(exeEdit.Value))
            MsgBox("The EQ executable path doesn't exist:`n" exeEdit.Value
                . "`n`nSettings will be saved, but Launch won't work until the path is valid.",
                "EQ Switch — Warning", "Icon!")

        EQ_EXE          := exeEdit.Value
        EQ_ARGS         := argsEdit.Value
        DBLCLICK_LAUNCH := dblClickChk.Value ? "1" : "0"
        GINA_PATH       := ginaEdit.Value
        NOTES_FILE      := notesEdit.Value
        MIDCLICK_NOTES  := midClickChk.Value ? "1" : "0"
        EQ_SERVER       := serverEdit.Value
        ; Validate client count — must be 1-8
        clientVal := clientsEdit.Value
        try {
            clientNum := Integer(clientVal)
            if (clientNum < 1)
                clientNum := 1
            else if (clientNum > 8)
                clientNum := 8
        } catch {
            clientNum := 2
        }
        NUM_CLIENTS     := String(clientNum)
        clientsEdit.Value := NUM_CLIENTS
        TOGGLE_BEEP     := beepChk.Value ? "1" : "0"
        FIX_MODE        := fixModes[fixModeCombo.Value]
        STARTUP_ENABLED := startupChk.Value ? "1" : "0"

        ; Handle hotkey — skip binding if empty
        if (newHotkey != "") {
            EQ_HOTKEY := newHotkey
            if !BindHotkey(EQ_HOTKEY)
                MsgBox("The switch hotkey '" EQ_HOTKEY "' could not be bound — it may be invalid or already in use.",
                    "EQ Switch — Warning", "Icon!")
        } else {
            EQ_HOTKEY := ""
        }

        ; Handle multimon hotkey
        if (newMultimonHk != "") {
            MULTIMON_HOTKEY := newMultimonHk
            if !BindMultiMonHotkey(MULTIMON_HOTKEY)
                MsgBox("The multi-monitor hotkey '" MULTIMON_HOTKEY "' could not be bound — it may be invalid or already in use.",
                    "EQ Switch — Warning", "Icon!")
        } else {
            MULTIMON_HOTKEY := ""
        }

        SaveConfig()
        UpdateTrayTip()

        ; Manage startup shortcut
        shortcutPath := A_Startup "\EQSwitch.lnk"
        if (STARTUP_ENABLED = "1") {
            try FileCreateShortcut(A_ScriptFullPath, shortcutPath)
        } else {
            if FileExist(shortcutPath)
                try FileDelete(shortcutPath)
        }

        SETTINGS_OPEN := false
        g.Destroy()
        ToolTip("Settings saved!")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
    }

    } catch as err {
        ; If anything inside Settings blew up, make sure the flag is cleared
        ; so the user can still open Settings again without restarting
        SETTINGS_OPEN := false
        try g.Destroy()
        ToolTip("⚠ Settings error: " err.Message)
        SetTimer(() => ToolTip(), -3000)
    }
}

; =========================================================
; LAUNCH
; =========================================================
LaunchOne(*) {
    global EQ_EXE, EQ_ARGS, TOOLTIP_MS
    if !FileExist(EQ_EXE) {
        ToolTip("⚠ EQ executable not found — check Settings")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
        return
    }
    eqDir := GetEqDir()
    Run(EQ_EXE " " EQ_ARGS, eqDir)
}

LaunchBoth(*) {
    global EQ_EXE, EQ_ARGS, LAUNCH_DELAY, LAUNCH_FIX_DELAY, NUM_CLIENTS, TOOLTIP_MS
    if !FileExist(EQ_EXE) {
        ToolTip("⚠ EQ executable not found — check Settings")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
        return
    }
    eqDir := GetEqDir()
    try {
        count   := Integer(NUM_CLIENTS)
        delay   := Integer(LAUNCH_DELAY)
        fixWait := Integer(LAUNCH_FIX_DELAY)
    } catch {
        ToolTip("⚠ Invalid launch settings — check NUM_CLIENTS, LAUNCH_DELAY, or LAUNCH_FIX_DELAY in config")
        SetTimer(() => ToolTip(), -3000)
        return
    }
    if (count < 1) {
        ToolTip("⚠ Number of clients must be at least 1")
        SetTimer(() => ToolTip(), -TOOLTIP_MS)
        return
    }

    Loop count {
        ToolTip("🎮 Launching client " A_Index " of " count "...")
        Run(EQ_EXE " " EQ_ARGS, eqDir)
        if (A_Index < count) {
            Sleep(delay)
        }
    }

    ToolTip("🎮 Waiting for windows to settle...")
    Sleep(fixWait)
    ToolTip("🪟 Arranging windows...")
    FixWindows()
    ToolTip("✅ Ready to play!")
    SetTimer(() => ToolTip(), -TOOLTIP_MS)
}


; ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
;          ~ Long Live Dalaya ~
; ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
