; =========================================================
; EQ2Box.ahk - EverQuest Window Switcher
; Requires AutoHotkey v2  (https://www.autohotkey.com)
;
; Setup:
;   1. Run EQ2Box.exe (or EQ2Box.ahk)
;   2. Right-click tray icon -> Settings
;   3. Point the exe path at your eqgame.exe
;   4. Set your preferred hotkey
;   5. Save and enjoy
;
; Version 1.1
; =========================================================

#Requires AutoHotkey v2.0
#SingleInstance Force

CFG_FILE         := A_ScriptDir "\eqbox.cfg"
EQ_TITLE         := "EverQuest"
SETTINGS_OPEN    := false

; =========================================================
; CONFIG
; =========================================================
LoadConfig() {
    global EQ_EXE, EQ_ARGS, EQ_HOTKEY, CFG_FILE, DBLCLICK_LAUNCH
    global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES, RECENT_CHARS

    ReadKey(key, def) {
        try {
            return IniRead(CFG_FILE, "EQ2Box", key)
        } catch {
            return def
        }
    }

    EQ_EXE          := ReadKey("EQ_EXE",          "C:\EverQuest\eqgame.exe")
    EQ_ARGS         := ReadKey("EQ_ARGS",          "-patchme")
    EQ_HOTKEY       := ReadKey("EQ_HOTKEY",        "\")
    DBLCLICK_LAUNCH := ReadKey("DBLCLICK_LAUNCH",  "0")
    GINA_PATH       := ReadKey("GINA_PATH",        "")
    NOTES_FILE      := ReadKey("NOTES_FILE",       "")
    MIDCLICK_NOTES  := ReadKey("MIDCLICK_NOTES",   "0")
    RECENT_CHARS    := ReadKey("RECENT_CHARS",     "")
}

SaveConfig() {
    global CFG_FILE, EQ_EXE, EQ_ARGS, EQ_HOTKEY, DBLCLICK_LAUNCH
    global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES, RECENT_CHARS
    IniWrite(EQ_EXE,          CFG_FILE, "EQ2Box", "EQ_EXE")
    IniWrite(EQ_ARGS,         CFG_FILE, "EQ2Box", "EQ_ARGS")
    IniWrite(EQ_HOTKEY,       CFG_FILE, "EQ2Box", "EQ_HOTKEY")
    IniWrite(DBLCLICK_LAUNCH, CFG_FILE, "EQ2Box", "DBLCLICK_LAUNCH")
    IniWrite(GINA_PATH,       CFG_FILE, "EQ2Box", "GINA_PATH")
    IniWrite(NOTES_FILE,      CFG_FILE, "EQ2Box", "NOTES_FILE")
    IniWrite(MIDCLICK_NOTES,  CFG_FILE, "EQ2Box", "MIDCLICK_NOTES")
    IniWrite(RECENT_CHARS,    CFG_FILE, "EQ2Box", "RECENT_CHARS")
}

LoadConfig()

; Keep the tray tooltip in sync with the active switch key
UpdateTrayTip() {
    global EQ_HOTKEY
    keyDisplay := (EQ_HOTKEY != "") ? EQ_HOTKEY : "none"
    A_IconTip  := "EQ Switch  |  Switch key: " keyDisplay
}
UpdateTrayTip()

; Auto-open Settings on first run if no config file found
isFirstRun := !FileExist(CFG_FILE)
if isFirstRun {
    SetTimer(FirstRunWelcome, -300)
}

FirstRunWelcome(*) {
    ToolTip("👋 Welcome to EQ Switch!`nOpening Settings — be sure to set your Switch Hotkey!")
    SetTimer(() => ToolTip(), -4000)
    SetTimer(OpenSettings, -500)
}

; =========================================================
; HOTKEY
; =========================================================
BindHotkey(key) {
    HotIfWinActive("ahk_exe eqgame.exe")
    try Hotkey(key, SwitchWindow, "On")
    HotIfWinActive()
}

SwitchWindow(*) {
    global EQ_TITLE

    ; Build a sorted list of visible EQ windows
    visible := []
    for id in WinGetList(EQ_TITLE) {
        if WinGetStyle("ahk_id " id) & 0x10000000  ; WS_VISIBLE
            visible.Push(id)
    }
    ; Bubble sort ascending by window ID so order never changes between calls
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

    if (visible.Length = 0) {
        ToolTip("No EverQuest windows found!")
        SetTimer(() => ToolTip(), -2000)
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
    WinActivate("ahk_id " visible[nextIndex])
}

BindHotkey(EQ_HOTKEY)

; =========================================================
; HELPERS
; =========================================================

; Bold the two Launch items using MFS_DEFAULT (standard Windows bold-item flag)
; Menu positions are 0-based: 0=title, 1=sep, 2=Launch Client, 3=Launch Both
SetMenuItemsBold(hMenu) {
    static MIIM_STATE := 0x1, MFS_DEFAULT := 0x1000
    cbSize := (A_PtrSize = 8) ? 80 : 48   ; 64-bit vs 32-bit MENUITEMINFO size
    mii    := Buffer(cbSize, 0)
    NumPut("UInt", cbSize,      mii,  0)   ; cbSize
    NumPut("UInt", MIIM_STATE,  mii,  4)   ; fMask  = MIIM_STATE
    NumPut("UInt", MFS_DEFAULT, mii, 12)   ; fState = MFS_DEFAULT (bold)
    DllCall("SetMenuItemInfo", "Ptr", hMenu, "UInt", 2, "Int", 1, "Ptr", mii)
    DllCall("SetMenuItemInfo", "Ptr", hMenu, "UInt", 3, "Int", 1, "Ptr", mii)
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
A_TrayMenu.Add()

A_TrayMenu.Add("📜  Open Log File",     (*) => OpenLogFile())
A_TrayMenu.Add("📂  Open eqclient.ini", (*) => OpenEqClientIni())
A_TrayMenu.Add("🎯  Open Gina",         (*) => OpenGina())
A_TrayMenu.Add("📝  Open Notes",        (*) => OpenNotes())
A_TrayMenu.Add()

A_TrayMenu.Add("🌐  Dalaya Wiki",       (*) => Run("https://wiki.dalaya.org/"))
A_TrayMenu.Add("🌐  Shards Wiki",       (*) => Run("https://wiki.shardsofdalaya.com/wiki/Main_Page"))
A_TrayMenu.Add()

A_TrayMenu.Add("⚙  Settings",          (*) => OpenSettings())
A_TrayMenu.Add()
A_TrayMenu.Add("✖  Exit",              (*) => ExitApp())

; Apply bold to Launch Client + Launch Both
SetMenuItemsBold(A_TrayMenu.Handle)

; =========================================================
; FIX WINDOWS
; =========================================================
FixWindows(*) {
    global EQ_TITLE
    winList := WinGetList(EQ_TITLE)
    if (winList.Length = 0) {
        ToolTip("No EverQuest windows found!")
        SetTimer(() => ToolTip(), -2000)
        return
    }
    Loop winList.Length {
        id := winList[A_Index]
        try {
            WinMaximize("ahk_id " id)
        } catch {
        }
    }
}

; =========================================================
; OPEN LOG FILE
; =========================================================
OpenLogFile(*) {
    global EQ_EXE
    eqDir := RegExReplace(EQ_EXE, "[^\\]+$", "")

    lastInput := ""
    loop {
        charName := InputBox("Enter character name:", "📜 Open Log File", "w300 h120", lastInput)
        if (charName.Result = "Cancel" || charName.Value = "")
            return

        logPath := eqDir "Logs\eqlog_" charName.Value "_dalaya.txt"

        if !FileExist(logPath) {
            lastInput := charName.Value
            ToolTip("📜 Log not found for: " charName.Value)
            SetTimer(() => ToolTip(), -2500)
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
    global EQ_EXE
    eqDir   := RegExReplace(EQ_EXE, "[^\\]+$", "")
    iniPath := eqDir "eqclient.ini"
    if !FileExist(iniPath) {
        ToolTip("📂 eqclient.ini not found — check your EQ path in Settings")
        SetTimer(() => ToolTip(), -2500)
        return
    }
    Run('notepad.exe "' iniPath '"')
}

; =========================================================
; OPEN GINA
; =========================================================
OpenGina(*) {
    global GINA_PATH
    if (GINA_PATH = "") {
        ToolTip("🎯 No Gina path set — open Settings to configure it")
        SetTimer(() => ToolTip(), -2500)
        return
    }
    if !FileExist(GINA_PATH) {
        ToolTip("🎯 Gina not found at configured path — check Settings")
        SetTimer(() => ToolTip(), -2500)
        return
    }
    Run('"' GINA_PATH '"')
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
                "EQ2Box — Notes Setup", "YesNo Icon?")
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
    if SETTINGS_OPEN
        return
    SETTINGS_OPEN := true

    ; Always reset the flag on any close path — X button, Alt-F4, crash, anything
    CleanupSettings(*) {
        global SETTINGS_OPEN
        SETTINGS_OPEN := false
    }

    try {

    g := Gui("+AlwaysOnTop", "⚔ EQ Switch — Options")
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

    ; ── EverQuest ──────────────────────────────────────────
    g.AddText("xm y+14 w440 cNavy", "⚔  EverQuest")
    g.AddText("xm y+6 w440 h1 0x10")

    g.AddText("xm y+8", "EQ Executable:")
    exeEdit := g.AddEdit("xm y+4 w396", EQ_EXE)
    g.AddButton("x+4 yp w36 h24", "...").OnEvent("Click", (*) => BrowseExe(exeEdit))

    g.AddText("xm y+8", "Launch arguments:")
    argsEdit := g.AddEdit("xm y+4 w440", EQ_ARGS)

    ; ── Open Gina ──────────────────────────────────────────
    g.AddText("xm y+14 w440 cNavy", "🎯  Open Gina")
    g.AddText("xm y+6 w440 h1 0x10")
    g.AddText("xm y+8", "Path to Gina.exe:")
    ginaEdit := g.AddEdit("xm y+4 w396", GINA_PATH)
    g.AddButton("x+4 yp w36 h24", "...").OnEvent("Click", (*) => BrowseGina(ginaEdit))

    ; ── Notes File ─────────────────────────────────────────
    g.AddText("xm y+14 w440 cNavy", "📝  Notes File")
    g.AddText("xm y+6 w440 h1 0x10")
    g.AddText("xm y+8", "Path to .txt notes file (leave blank to use notes.txt in script folder):")
    notesEdit := g.AddEdit("xm y+4 w396", NOTES_FILE)
    g.AddButton("x+4 yp w36 h24", "...").OnEvent("Click", (*) => BrowseNotes(notesEdit))

    ; ── Tray Icon ──────────────────────────────────────────
    g.AddText("xm y+14 w440 cNavy", "🖱  Tray Icon")
    g.AddText("xm y+6 w440 h1 0x10")
    dblClickChk := g.AddCheckbox("xm y+8", "Double-click tray icon launches a client")
    dblClickChk.Value := (DBLCLICK_LAUNCH = "1") ? 1 : 0
    midClickChk := g.AddCheckbox("xm y+6", "Middle-click tray icon opens Notepad notes")
    midClickChk.Value := (MIDCLICK_NOTES = "1") ? 1 : 0

    ; ── Backup Char Files ──────────────────────────────────
    g.AddText("xm y+14 w440 cNavy", "💾  Backup Char Files")
    g.AddText("xm y+6 w440 h1 0x10")
    g.AddText("xm y+8", "Copies UI_Name_dalaya.ini and Name_dalaya.ini to your Desktop.")
    g.AddText("xm y+8", "Character name (type or pick a recent name):")
    recentList := (RECENT_CHARS != "") ? StrSplit(RECENT_CHARS, "|") : []
    charCombo  := g.AddComboBox("xm y+4 w210", recentList)
    if (recentList.Length > 0)
        charCombo.Choose(1)
    g.AddButton("x+8 yp w140 h24", "💾 Backup to Desktop").OnEvent("Click", (*) => DoBackup(charCombo.Text))

    ; ── Save / Cancel ──────────────────────────────────────
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
        global EQ_EXE
        if (charName = "") {
            MsgBox("Please enter or select a character name.", "EQ2Box - Backup", "Icon!")
            return
        }
        eqDir   := RegExReplace(EQ_EXE, "[^\\]+$", "")
        desktop := EnvGet("USERPROFILE") "\Desktop\"
        file1   := eqDir "UI_" charName "_dalaya.ini"
        file2   := eqDir charName "_dalaya.ini"
        found   := 0
        if FileExist(file1) {
            FileCopy(file1, desktop "UI_" charName "_dalaya.ini", 1)
            found++
        }
        if FileExist(file2) {
            FileCopy(file2, desktop charName "_dalaya.ini", 1)
            found++
        }
        if (found = 0) {
            MsgBox("No character files found for '" charName "'`nin: " eqDir, "EQ2Box - Backup", "Icon!")
            return
        }
        ; Save to recent list and refresh the combobox
        AddRecentChar(charName)
        SendMessage(0x14B, 0, 0, charCombo)   ; CB_RESETCONTENT — clear items
        fresh := (RECENT_CHARS != "") ? StrSplit(RECENT_CHARS, "|") : []
        for name in fresh
            SendMessage(0x143, 0, StrPtr(name), charCombo)  ; CB_ADDSTRING
        charCombo.Text := charName
        MsgBox(found " file(s) backed up to Desktop ✓", "EQ2Box - Backup", "Icon!")
    }

    SaveAndClose(*) {
        global EQ_EXE, EQ_ARGS, EQ_HOTKEY, DBLCLICK_LAUNCH
        global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES
        newHotkey := hotkeyCtrl.Value
        HotIfWinActive("ahk_exe eqgame.exe")
        try Hotkey(EQ_HOTKEY, "Off")
        HotIfWinActive()
        EQ_EXE          := exeEdit.Value
        EQ_ARGS         := argsEdit.Value
        EQ_HOTKEY       := newHotkey
        DBLCLICK_LAUNCH := dblClickChk.Value ? "1" : "0"
        GINA_PATH       := ginaEdit.Value
        NOTES_FILE      := notesEdit.Value
        MIDCLICK_NOTES  := midClickChk.Value ? "1" : "0"
        SaveConfig()
        BindHotkey(EQ_HOTKEY)
        UpdateTrayTip()
        SETTINGS_OPEN := false
        g.Destroy()
        ToolTip("Settings saved!")
        SetTimer(() => ToolTip(), -2000)
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
    global EQ_EXE, EQ_ARGS
    eqDir := RegExReplace(EQ_EXE, "\\[^\\]+$", "")
    Run(EQ_EXE " " EQ_ARGS, eqDir)
}

LaunchBoth(*) {
    global EQ_EXE, EQ_ARGS
    eqDir := RegExReplace(EQ_EXE, "\\[^\\]+$", "")
    Run(EQ_EXE " " EQ_ARGS, eqDir)
    Sleep(3000)
    Run(EQ_EXE " " EQ_ARGS, eqDir)
    Sleep(15000)
    FixWindows()
}


; ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
;          ~ Long Live Dalaya ~
; ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
