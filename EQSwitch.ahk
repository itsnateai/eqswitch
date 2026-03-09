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

g_version        := "1.9"
CFG_FILE         := A_ScriptDir "\eqswitch.cfg"
EQ_TITLE         := "EverQuest"
SETTINGS_OPEN    := false
TOOLTIP_MS       := 2000

; Show a tooltip that auto-dismisses after ms (default TOOLTIP_MS)
ShowTip(msg, ms?) {
    global TOOLTIP_MS
    ToolTip(msg)
    SetTimer(() => ToolTip(), -(ms ?? TOOLTIP_MS))
}
g_multiMonState  := 0
g_launchOneLabel := "⚔  Launch Client"
g_launchAllLabel := "🎮  Launch Both"
g_lastDblClick   := 0
g_tripleClickCooldown := 0
g_launchActive   := false
g_pmOpen         := false

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
        if WinGetStyle("ahk_id " id) & 0x10000000  ; WS_VISIBLE
            visible.Push(id)
    }
    ; Insertion sort — stable, efficient for small arrays (2-8 windows)
    Loop visible.Length - 1 {
        i := A_Index + 1
        key := visible[i]
        j := i - 1
        while (j >= 1 && visible[j] > key) {
            visible[j + 1] := visible[j]
            j--
        }
        visible[j + 1] := key
    }
    return visible
}

; =========================================================
; PROCESS MANAGEMENT
; =========================================================

; Apply process priority to all running eqgame.exe instances
ApplyProcessPriority(priority) {
    if (priority = "" || priority = "Normal")
        return
    for id in WinGetList("ahk_exe eqgame.exe") {
        try {
            pid := WinGetPID("ahk_id " id)
            ProcessSetPriority(priority, pid)
        }
    }
}

; Apply CPU affinity mask to a single process by PID
ApplyAffinityToPid(pid, affinityStr) {
    if (affinityStr = "")
        return
    try {
        mask := Integer(affinityStr)
        if (mask <= 0)
            return
        hProc := DllCall("OpenProcess", "UInt", 0x0200, "Int", 0, "UInt", pid, "Ptr")  ; PROCESS_SET_INFORMATION
        if (hProc) {
            DllCall("SetProcessAffinityMask", "Ptr", hProc, "UPtr", mask)
            DllCall("CloseHandle", "Ptr", hProc)
        }
    }
}

; Apply CPU affinity to all running eqgame.exe instances
ApplyAffinityToAll(affinityStr) {
    if (affinityStr = "")
        return
    for id in WinGetList("ahk_exe eqgame.exe") {
        try {
            pid := WinGetPID("ahk_id " id)
            ApplyAffinityToPid(pid, affinityStr)
        }
    }
}

; Get system CPU core count
GetCoreCount() {
    try {
        hProc := DllCall("GetCurrentProcess", "Ptr")
        sysMask := 0
        procMask := 0
        DllCall("GetProcessAffinityMask", "Ptr", hProc, "UPtr*", &procMask, "UPtr*", &sysMask)
        count := 0
        m := sysMask
        while (m > 0) {
            count += m & 1
            m := m >> 1
        }
        return (count > 0) ? count : 1
    }
    return 1
}

; Convert affinity mask to array of booleans (1-indexed, core 1 = bit 0)
AffinityMaskToCores(maskStr, coreCount) {
    cores := []
    try {
        mask := (maskStr != "") ? Integer(maskStr) : 0
    } catch {
        mask := 0
    }
    Loop coreCount {
        cores.Push((mask >> (A_Index - 1)) & 1)
    }
    return cores
}

; Get process priority name by PID (DllCall since ProcessGetPriority isn't in AHKv2)
GetProcessPriorityName(pid) {
    try {
        hProc := DllCall("OpenProcess", "UInt", 0x0400, "Int", 0, "UInt", pid, "Ptr")
        if (!hProc)
            return "Unknown"
        priClass := DllCall("GetPriorityClass", "Ptr", hProc, "UInt")
        DllCall("CloseHandle", "Ptr", hProc)
        switch priClass {
            case 0x20:   return "Normal"
            case 0x4000: return "BelowNormal"
            case 0x8000: return "AboveNormal"
            case 0x80:   return "High"
            case 0x100:  return "Realtime"
            case 0x40:   return "Idle"
            default:     return "Unknown"
        }
    }
    return "Unknown"
}

; Convert array of booleans back to affinity mask
CoresMaskToAffinity(cores) {
    mask := 0
    for i, val in cores {
        if val
            mask |= (1 << (i - 1))
    }
    return mask
}

; =========================================================
; CONFIG
; =========================================================
LoadConfig() {
    global EQ_EXE, EQ_ARGS, EQ_HOTKEY, CFG_FILE, DBLCLICK_LAUNCH
    global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES, RECENT_CHARS
    global EQ_SERVER, LAUNCH_DELAY, LAUNCH_FIX_DELAY, NUM_CLIENTS
    global FIX_MODE, STARTUP_ENABLED, MULTIMON_HOTKEY, MULTIMON_ENABLED
    global LAUNCH_ONE_HOTKEY, LAUNCH_ALL_HOTKEY, TRIPLECLICK_LAUNCH
    global PROCESS_PRIORITY, CPU_AFFINITY
    global FIX_TOP_OFFSET, FIX_BOTTOM_OFFSET
    global PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY
    global FLASH_SUPPRESS, AUTO_MINIMIZE, BORDER_ENABLED, BORDER_COLOR, PIP_ZOOM

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
    FIX_MODE         := ReadKey("FIX_MODE",          "maximize")
    STARTUP_ENABLED  := ReadKey("STARTUP_ENABLED",   "0")
    MULTIMON_HOTKEY  := ReadKey("MULTIMON_HOTKEY",   ">!m")
    MULTIMON_ENABLED := ReadKey("MULTIMON_ENABLED", "1")
    LAUNCH_ONE_HOTKEY := ReadKey("LAUNCH_ONE_HOTKEY", "")
    LAUNCH_ALL_HOTKEY := ReadKey("LAUNCH_ALL_HOTKEY", "")
    TRIPLECLICK_LAUNCH := ReadKey("TRIPLECLICK_LAUNCH", "0")
    PROCESS_PRIORITY   := ReadKey("PROCESS_PRIORITY",   "Normal")
    CPU_AFFINITY       := ReadKey("CPU_AFFINITY",        "")
    FIX_TOP_OFFSET     := ReadKey("FIX_TOP_OFFSET",      "0")
    FIX_BOTTOM_OFFSET  := ReadKey("FIX_BOTTOM_OFFSET",   "0")
    PIP_WIDTH          := ReadKey("PIP_WIDTH",            "320")
    PIP_HEIGHT         := ReadKey("PIP_HEIGHT",           "180")
    PIP_OPACITY        := ReadKey("PIP_OPACITY",          "200")
    FLASH_SUPPRESS     := ReadKey("FLASH_SUPPRESS",       "0")
    AUTO_MINIMIZE      := ReadKey("AUTO_MINIMIZE",        "0")
    BORDER_ENABLED     := ReadKey("BORDER_ENABLED",       "0")
    BORDER_COLOR       := ReadKey("BORDER_COLOR",         "00FF00")
    PIP_ZOOM           := ReadKey("PIP_ZOOM",             "0")
}

SaveConfig() {
    global CFG_FILE, EQ_EXE, EQ_ARGS, EQ_HOTKEY, DBLCLICK_LAUNCH
    global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES, RECENT_CHARS
    global EQ_SERVER, LAUNCH_DELAY, LAUNCH_FIX_DELAY, NUM_CLIENTS
    global FIX_MODE, STARTUP_ENABLED, MULTIMON_HOTKEY, MULTIMON_ENABLED
    global LAUNCH_ONE_HOTKEY, LAUNCH_ALL_HOTKEY, TRIPLECLICK_LAUNCH
    global PROCESS_PRIORITY, CPU_AFFINITY
    global FIX_TOP_OFFSET, FIX_BOTTOM_OFFSET
    global PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY
    global FLASH_SUPPRESS, AUTO_MINIMIZE, BORDER_ENABLED, BORDER_COLOR, PIP_ZOOM
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
    IniWrite(FIX_MODE,         CFG_FILE, "EQSwitch", "FIX_MODE")
    IniWrite(STARTUP_ENABLED,  CFG_FILE, "EQSwitch", "STARTUP_ENABLED")
    IniWrite(MULTIMON_HOTKEY,  CFG_FILE, "EQSwitch", "MULTIMON_HOTKEY")
    IniWrite(MULTIMON_ENABLED, CFG_FILE, "EQSwitch", "MULTIMON_ENABLED")
    IniWrite(LAUNCH_ONE_HOTKEY, CFG_FILE, "EQSwitch", "LAUNCH_ONE_HOTKEY")
    IniWrite(LAUNCH_ALL_HOTKEY, CFG_FILE, "EQSwitch", "LAUNCH_ALL_HOTKEY")
    IniWrite(TRIPLECLICK_LAUNCH, CFG_FILE, "EQSwitch", "TRIPLECLICK_LAUNCH")
    IniWrite(PROCESS_PRIORITY, CFG_FILE, "EQSwitch", "PROCESS_PRIORITY")
    IniWrite(CPU_AFFINITY, CFG_FILE, "EQSwitch", "CPU_AFFINITY")
    IniWrite(FIX_TOP_OFFSET, CFG_FILE, "EQSwitch", "FIX_TOP_OFFSET")
    IniWrite(FIX_BOTTOM_OFFSET, CFG_FILE, "EQSwitch", "FIX_BOTTOM_OFFSET")
    IniWrite(PIP_WIDTH, CFG_FILE, "EQSwitch", "PIP_WIDTH")
    IniWrite(PIP_HEIGHT, CFG_FILE, "EQSwitch", "PIP_HEIGHT")
    IniWrite(PIP_OPACITY, CFG_FILE, "EQSwitch", "PIP_OPACITY")
    IniWrite(FLASH_SUPPRESS,  CFG_FILE, "EQSwitch", "FLASH_SUPPRESS")
    IniWrite(AUTO_MINIMIZE,   CFG_FILE, "EQSwitch", "AUTO_MINIMIZE")
    IniWrite(BORDER_ENABLED,  CFG_FILE, "EQSwitch", "BORDER_ENABLED")
    IniWrite(BORDER_COLOR,    CFG_FILE, "EQSwitch", "BORDER_COLOR")
    IniWrite(PIP_ZOOM,        CFG_FILE, "EQSwitch", "PIP_ZOOM")
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
    TrayTip("Right-click the tray icon to get started ⚔", "EQ Switch loaded in tray!", "Iconi")
    SetTimer(OpenSettings, -2000)
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
    global TOOLTIP_MS

    visible := GetVisibleEqWindows()

    if (visible.Length = 0) {
        ShowTip("No EverQuest windows found!")
        return
    }
    if (visible.Length = 1) {
        ShowTip("Only one EverQuest window open!", 700)
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
}

BindHotkey(EQ_HOTKEY)
if (MULTIMON_ENABLED = "1")
    BindMultiMonHotkey(MULTIMON_HOTKEY)
BindLaunchHotkey(LAUNCH_ONE_HOTKEY, "one")
BindLaunchHotkey(LAUNCH_ALL_HOTKEY, "all")

BindLaunchHotkey(key, which) {
    if (key = "")
        return true
    fn := (which = "one") ? LaunchOne : LaunchBoth
    try {
        Hotkey(key, fn, "On")
        return true
    } catch {
        return false
    }
}

FormatHotkeyDisplay(key) {
    if (key = "")
        return ""
    display := key
    ; Handle sided modifiers first (e.g., >! = RAlt, <^ = LCtrl)
    display := StrReplace(display, ">!", "RAlt·")
    display := StrReplace(display, "<!", "LAlt·")
    display := StrReplace(display, ">^", "RCtrl·")
    display := StrReplace(display, "<^", "LCtrl·")
    display := StrReplace(display, ">+", "RShift·")
    display := StrReplace(display, "<+", "LShift·")
    display := StrReplace(display, ">#", "RWin·")
    display := StrReplace(display, "<#", "LWin·")
    ; Handle unsided modifiers (use · separator to avoid + conflicts)
    display := StrReplace(display, "!", "Alt·")
    display := StrReplace(display, "^", "Ctrl·")
    display := StrReplace(display, "+", "Shift·")
    display := StrReplace(display, "#", "Win·")
    ; Replace intermediate separator with +
    display := StrReplace(display, "·", "+")
    return display
}

UpdateTrayMenuLabels() {
    global LAUNCH_ONE_HOTKEY, LAUNCH_ALL_HOTKEY, g_launchOneLabel, g_launchAllLabel
    oneSuffix := FormatHotkeyDisplay(LAUNCH_ONE_HOTKEY)
    allSuffix := FormatHotkeyDisplay(LAUNCH_ALL_HOTKEY)
    newOne := "⚔  Launch Client" . (oneSuffix != "" ? "`t" oneSuffix : "")
    newAll := "🎮  Launch Both" . (allSuffix != "" ? "`t" allSuffix : "")
    if (g_launchOneLabel != newOne) {
        try A_TrayMenu.Rename(g_launchOneLabel, newOne)
        g_launchOneLabel := newOne
    }
    if (g_launchAllLabel != newAll) {
        try A_TrayMenu.Rename(g_launchAllLabel, newAll)
        g_launchAllLabel := newAll
    }
}

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
; WINDOW PRESETS
; =========================================================
GetPresetNames() {
    global CFG_FILE
    names := []
    try {
        section := IniRead(CFG_FILE, "WindowPresets")
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

SaveWindowPreset(presetName) {
    global CFG_FILE, TOOLTIP_MS
    visible := GetVisibleEqWindows()
    if (visible.Length = 0) {
        ShowTip("⚠ No EQ windows to save!")
        return
    }
    data := ""
    for i, id in visible {
        try {
            WinGetPos(&x, &y, &w, &h, "ahk_id " id)
            data .= (i > 1 ? ";" : "") x "," y "," w "," h
        }
    }
    IniWrite(data, CFG_FILE, "WindowPresets", presetName)
    BuildPresetMenu()
    ShowTip("🪟 Preset saved: " presetName " (" visible.Length " windows)")
}

LoadWindowPreset(presetName) {
    global CFG_FILE, TOOLTIP_MS
    try val := IniRead(CFG_FILE, "WindowPresets", presetName)
    catch {
        ShowTip("⚠ Preset '" presetName "' not found!")
        return
    }
    windows := StrSplit(val, ";")
    visible := GetVisibleEqWindows()
    if (visible.Length = 0) {
        ShowTip("⚠ No EQ windows to arrange!")
        return
    }
    count := Min(windows.Length, visible.Length)
    Loop count {
        coords := StrSplit(windows[A_Index], ",")
        if (coords.Length >= 4) {
            id := visible[A_Index]
            try {
                WinRestore("ahk_id " id)
                WinMove(Integer(coords[1]), Integer(coords[2]),
                        Integer(coords[3]), Integer(coords[4]), "ahk_id " id)
            }
        }
    }
    msg := "🪟 Preset loaded: " presetName
    if (visible.Length != windows.Length)
        msg .= " (" count " of " windows.Length " windows)"
    ShowTip(msg)
}

DeleteWindowPreset(presetName) {
    global CFG_FILE
    try IniDelete(CFG_FILE, "WindowPresets", presetName)
    BuildPresetMenu()
}

; Build/rebuild the window presets tray submenu
BuildPresetMenu() {
    global g_presetMenu
    g_presetMenu.Delete()
    presetNames := GetPresetNames()
    if (presetNames.Length = 0) {
        g_presetMenu.Add("(no presets saved)", (*) => 0)
        g_presetMenu.Disable("(no presets saved)")
    } else {
        for name in presetNames {
            boundName := name
            g_presetMenu.Add(name, (*) => LoadWindowPreset(boundName))
        }
    }
    g_presetMenu.Add()
    g_presetMenu.Add("💾 Save Current Layout...", (*) => PromptSavePreset())
}

PromptSavePreset(*) {
    result := InputBox("Enter a name for this window preset:", "Save Window Preset", "w300 h130")
    if (result.Result = "Cancel" || result.Value = "")
        return
    SaveWindowPreset(result.Value)
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
    global DBLCLICK_LAUNCH, MIDCLICK_NOTES, TRIPLECLICK_LAUNCH
    global g_lastDblClick, g_tripleClickCooldown
    now := A_TickCount
    if (lParam = 0x203) {  ; WM_LBUTTONDBLCLK
        ; Check for triple-click: double-click within 500ms of last double-click
        if (TRIPLECLICK_LAUNCH = "1" && (now - g_lastDblClick) < 500 && (now - g_tripleClickCooldown) > 5000) {
            g_tripleClickCooldown := now
            g_lastDblClick := 0
            LaunchBoth()
            return
        }
        g_lastDblClick := now
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
A_TrayMenu.Add("⚔  EQ Switch  ⚔", (*) => 0)
A_TrayMenu.Disable("⚔  EQ Switch  ⚔")
A_TrayMenu.Add()

; Launch items — made bold by SetMenuItemsBold() below
A_TrayMenu.Add("⚔  Launch Client",     LaunchOne)
A_TrayMenu.Add("🎮  Launch Both",       LaunchBoth)

A_TrayMenu.Add()

A_TrayMenu.Add("🪟  Fix Windows",       (*) => FixWindows())
A_TrayMenu.Add("🔄  Swap Windows",      (*) => SwapWindows())
g_presetMenu := Menu()
BuildPresetMenu()
A_TrayMenu.Add("🪟  Window Presets",    g_presetMenu)
A_TrayMenu.Add("📺  Picture-in-Picture", (*) => TogglePiP())
A_TrayMenu.Add("🔲  Active Border",     (*) => ToggleBorderFromTray())
A_TrayMenu.Add("📦  Auto-Minimize",     (*) => ToggleAutoMinFromTray())
A_TrayMenu.Add("🔕  Flash Suppress",    (*) => ToggleFlashFromTray())
A_TrayMenu.Add("⚡  Process Manager",   (*) => OpenProcessManager())
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

; Update menu labels with hotkey text, then apply bold
UpdateTrayMenuLabels()
SetMenuItemsBold(A_TrayMenu.Handle, ["Launch Client", "Launch Both"])
UpdateExtrasCheckmarks()

; =========================================================
; FIX WINDOWS
; =========================================================
FixWindows(*) {
    global FIX_MODE, TOOLTIP_MS, FIX_TOP_OFFSET, FIX_BOTTOM_OFFSET
    winList := GetVisibleEqWindows()
    if (winList.Length = 0) {
        ShowTip("No EverQuest windows found!")
        return
    }

    ; Parse offsets (top offset adjusts Y start, bottom offset extends past work area)
    try topOff := Integer(FIX_TOP_OFFSET)
    catch
        topOff := 0
    try botOff := Integer(FIX_BOTTOM_OFFSET)
    catch
        botOff := 0

    if (FIX_MODE = "sidebyside") {
        ; Arrange side-by-side across the primary monitor
        try MonitorGetWorkArea(1, &mLeft, &mTop, &mRight, &mBottom)
        catch {
            ShowTip("⚠ Could not read monitor info")
            return
        }
        adjTop    := mTop + topOff
        adjHeight := (mBottom + botOff) - adjTop
        mWidth    := mRight - mLeft
        count     := winList.Length
        sliceW    := mWidth // count
        Loop count {
            id := winList[A_Index]
            x  := mLeft + (A_Index - 1) * sliceW
            try {
                WinRestore("ahk_id " id)
                WinMove(x, adjTop, sliceW, adjHeight, "ahk_id " id)
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
                adjTop    := mTop + topOff
                adjHeight := (mBottom + botOff) - adjTop
                WinRestore("ahk_id " id)
                WinMove(mLeft, adjTop, mRight - mLeft, adjHeight, "ahk_id " id)
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
        ShowTip("Need at least 2 EQ windows to swap!")
        return
    }
    ; Read current positions
    positions := []
    for id in visible {
        try {
            WinGetPos(&x, &y, &w, &h, "ahk_id " id)
            positions.Push({x: x, y: y, w: w, h: h})
        } catch {
            ShowTip("⚠ A window closed during swap — try again")
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
    ShowTip("🔄 Windows swapped!")
}

ToggleMultiMon(*) {
    global g_multiMonState, TOOLTIP_MS, FIX_TOP_OFFSET, FIX_BOTTOM_OFFSET
    visible := GetVisibleEqWindows()
    if (visible.Length < 2) {
        ShowTip("Need at least 2 EQ windows!")
        return
    }
    monCount := MonitorGetCount()
    if (monCount < 2) {
        ShowTip("Need at least 2 monitors!")
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
        ShowTip("🪟 Multi-monitor OFF — stacked on primary")
    } else {
        ; Spread across monitors with rotation offset
        try topOff := Integer(FIX_TOP_OFFSET)
        catch
            topOff := 0
        try botOff := Integer(FIX_BOTTOM_OFFSET)
        catch
            botOff := 0
        offset := g_multiMonState - 1
        count  := visible.Length
        Loop count {
            winIdx := Mod(A_Index - 1 + offset, count) + 1
            id     := visible[winIdx]
            mon    := Mod(A_Index - 1, monCount) + 1
            try {
                MonitorGetWorkArea(mon, &mLeft, &mTop, &mRight, &mBottom)
                adjTop    := mTop + topOff
                adjHeight := (mBottom + botOff) - adjTop
                WinRestore("ahk_id " id)
                WinMove(mLeft, adjTop, mRight - mLeft, adjHeight, "ahk_id " id)
            }
        }
        if (g_multiMonState = 1)
            ShowTip("🖥 Multi-monitor ON")
        else
            ShowTip("🔄 Multi-monitor swapped")
    }
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
    global EQ_SERVER, TOOLTIP_MS, RECENT_CHARS

    eqDir := GetEqDir()
    recentList := (RECENT_CHARS != "") ? StrSplit(RECENT_CHARS, "|") : []

    dlg := Gui("+AlwaysOnTop", "📜 Open Log File")
    dlg.SetFont("s9", "Segoe UI")
    dlg.MarginX := 12
    dlg.MarginY := 10
    dlg.AddText(, "Character name (type or pick a recent name):")
    combo := dlg.AddComboBox("y+4 w240", recentList)
    if (recentList.Length > 0)
        combo.Choose(1)
    statusTxt := dlg.AddText("y+6 w240 h16 cRed", "")

    DoOpen(*) {
        charName := combo.Text
        if (charName = "")
            return
        logPath := eqDir "Logs\eqlog_" charName "_" EQ_SERVER ".txt"
        if !FileExist(logPath) {
            statusTxt.Value := "Log not found for: " charName
            return
        }
        dlg.Destroy()
        Run('notepad.exe "' logPath '"')
    }

    dlg.AddButton("y+8 w115 h26 Default", "📜 Open").OnEvent("Click", DoOpen)
    dlg.AddButton("x+10 yp w115 h26", "Cancel").OnEvent("Click", (*) => dlg.Destroy())
    dlg.OnEvent("Escape", (*) => dlg.Destroy())
    dlg.Show("AutoSize")
}

; =========================================================
; OPEN EQCLIENT.INI
; =========================================================
OpenEqClientIni(*) {
    global TOOLTIP_MS
    eqDir   := GetEqDir()
    iniPath := eqDir "eqclient.ini"
    if !FileExist(iniPath) {
        ShowTip("📂 eqclient.ini not found — check your EQ path in Settings")
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
        ShowTip("🎯 No Gina path set — open Settings to configure it")
        return
    }
    if !FileExist(GINA_PATH) {
        ShowTip("🎯 Gina not found at configured path — check Settings")
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
                "Yes — pick an existing .txt file`n" .
                "No — create notes.txt in the EQ Switch folder`n" .
                "Cancel — do nothing",
                "EQ Switch — Notes Setup", "YesNoCancel Icon?")
            if (result = "Yes") {
                f := FileSelect(, , "Select your notes .txt file", "Text Files (*.txt)")
                if (f = "")
                    return
                notesPath  := f
                NOTES_FILE := f
                SaveConfig()
            } else if (result = "No") {
                notesPath  := defaultPath
                FileAppend("== EQ Notes ==`n`n", notesPath)
                NOTES_FILE := defaultPath
                SaveConfig()
            } else {
                return
            }
        }
    }

    ; Safety-net: create the file if it vanished
    if !FileExist(notesPath)
        FileAppend("== EQ Notes ==`n`n", notesPath)

    Run('notepad.exe "' notesPath '"')
}

; =========================================================
; SETTINGS GUI — Section Builders
; =========================================================
; Each builder takes (g, ctl) where g is the Gui and ctl is
; a Map() for storing control references used by SaveAndClose.

BuildHotkeySection(g, ctl) {
    global EQ_HOTKEY
    g.SetFont("s10 Bold", "Segoe UI")
    g.AddText("xm w440 c0xAA3300", "⚔  Window Switch Hotkey  ⚔")
    g.SetFont("s9", "Segoe UI")
    g.AddText("xm y+4 w440 h2 0x10")
    activeKeyDisplay := (EQ_HOTKEY != "") ? EQ_HOTKEY : "(not set!)"
    statusColor := (EQ_HOTKEY != "") ? "c0x007700" : "c0xCC0000"
    g.AddText("xm y+6 w440 " statusColor, "Active key:  " activeKeyDisplay "       Press a new key below to change:")
    ctl["hotkeyCtrl"] := g.AddHotkey("xm y+4 w120", EQ_HOTKEY)
    g.AddText("x+10 yp+4 w220 cGray", "← click the box and press your key")
}

BuildEverQuestSection(g, ctl) {
    global EQ_EXE, EQ_ARGS
    g.AddText("xm y+10 w440 cNavy", "⚔  EverQuest")
    g.AddText("xm y+4 w440 h1 0x10")

    g.AddText("xm y+6", "EQ Executable:")
    ctl["exeEdit"] := g.AddEdit("xm y+3 w370", EQ_EXE)
    g.AddButton("x+4 yp w66 h24", "Browse...").OnEvent("Click", (*) => BrowseFile(ctl["exeEdit"], "Select eqgame.exe", "Executable (*.exe)"))

    g.AddText("xm y+6", "Launch args:")
    ctl["argsEdit"] := g.AddEdit("xm y+3 w200", EQ_ARGS)
}

BuildLaunchSection(g, ctl) {
    global NUM_CLIENTS, FIX_MODE, FIX_TOP_OFFSET, FIX_BOTTOM_OFFSET
    global LAUNCH_ONE_HOTKEY, LAUNCH_ALL_HOTKEY
    global DBLCLICK_LAUNCH, STARTUP_ENABLED, MIDCLICK_NOTES, TRIPLECLICK_LAUNCH
    g.AddText("xm y+10 w440 cNavy", "🎮  Launch & Tray Options")
    g.AddText("xm y+4 w440 h1 0x10")

    g.AddText("xm y+6 Section", "Clients:")
    ctl["clientsEdit"] := g.AddEdit("x+4 yp-2 w40 Number", NUM_CLIENTS)
    g.AddUpDown("Range1-8", NUM_CLIENTS)
    g.AddText("x+10 yp+2", "Window mode:")
    ctl["fixModes"] := ["maximize", "restore", "sidebyside", "multimonitor"]
    ctl["fixModeCombo"] := g.AddDropDownList("x+4 yp-2 w130", ctl["fixModes"])
    for i, mode in ctl["fixModes"] {
        if (mode = FIX_MODE)
            ctl["fixModeCombo"].Choose(i)
    }
    if (ctl["fixModeCombo"].Value = 0)
        ctl["fixModeCombo"].Choose(1)

    g.AddText("xm y+6", "Top offset (px):")
    ctl["topOffsetEdit"] := g.AddEdit("x+4 yp-2 w50 Number", FIX_TOP_OFFSET)
    g.AddUpDown("Range-100-100", Integer(FIX_TOP_OFFSET))
    g.AddText("x+14 yp+2", "Bottom offset (px):")
    ctl["bottomOffsetEdit"] := g.AddEdit("x+4 yp-2 w50 Number", FIX_BOTTOM_OFFSET)
    g.AddUpDown("Range-100-100", Integer(FIX_BOTTOM_OFFSET))
    g.AddText("x+8 yp+2 cGray", "fine-tune FixWindows")

    presetNames := GetPresetNames()
    g.AddText("xm y+6", "Window preset:")
    presetDropdown := g.AddDropDownList("x+4 yp-2 w130", presetNames)
    g.AddButton("x+3 yp w50 h20", "Load").OnEvent("Click", (*) => DoLoadPreset(presetDropdown))
    g.AddButton("x+2 yp w50 h20", "Save").OnEvent("Click", (*) => DoSavePreset())
    g.AddButton("x+2 yp w50 h20", "Delete").OnEvent("Click", (*) => DoDeletePreset(presetDropdown))

    DoLoadPreset(dropdown) {
        if (dropdown.Value = 0 || dropdown.Text = "")
            return
        LoadWindowPreset(dropdown.Text)
    }
    DoSavePreset() {
        result := InputBox("Enter a name for this window preset:", "Save Window Preset", "w300 h130")
        if (result.Result = "Cancel" || result.Value = "")
            return
        SaveWindowPreset(result.Value)
        fresh := GetPresetNames()
        presetDropdown.Delete()
        presetDropdown.Add(fresh)
        for i, n in fresh {
            if (n = result.Value)
                presetDropdown.Choose(i)
        }
    }
    DoDeletePreset(dropdown) {
        if (dropdown.Value = 0 || dropdown.Text = "")
            return
        name := dropdown.Text
        result := MsgBox("Delete window preset '" name "'?", "EQ Switch", "YesNo Icon?")
        if (result != "Yes")
            return
        DeleteWindowPreset(name)
        fresh := GetPresetNames()
        presetDropdown.Delete()
        presetDropdown.Add(fresh)
        ShowTip("Preset deleted: " name)
    }

    g.AddText("xm y+6", "Launch One hotkey:")
    ctl["launchOneHkCtrl"] := g.AddHotkey("x+4 yp-2 w120", LAUNCH_ONE_HOTKEY)
    g.AddText("x+14 yp+2", "Launch All hotkey:")
    ctl["launchAllHkCtrl"] := g.AddHotkey("x+4 yp-2 w120", LAUNCH_ALL_HOTKEY)

    ctl["dblClickChk"] := g.AddCheckbox("xm y+6", "Tray double-click launches client")
    ctl["dblClickChk"].Value := (DBLCLICK_LAUNCH = "1") ? 1 : 0
    ctl["startupChk"] := g.AddCheckbox("x+20 yp", "Run at Windows startup")
    ctl["startupChk"].Value := (STARTUP_ENABLED = "1") ? 1 : 0

    ctl["midClickChk"] := g.AddCheckbox("xm y+4", "Tray middle-click opens notes")
    ctl["midClickChk"].Value := (MIDCLICK_NOTES = "1") ? 1 : 0
    ctl["tripleClickChk"] := g.AddCheckbox("x+20 yp", "Tray triple-click launches all clients")
    ctl["tripleClickChk"].Value := (TRIPLECLICK_LAUNCH = "1") ? 1 : 0

    g.AddButton("xm y+6 w130 h24", "🖥 Desktop Shortcut").OnEvent("Click", CreateDesktopShortcut)
    g.AddButton("x+8 yp w130 h24", "🔧 Tray Icon Settings").OnEvent("Click", OpenTraySettings)

    OpenTraySettings(*) {
        Run("ms-settings:taskbar")
        TrayTip("Look for 'Other system tray icons' and enable EQ Switch", "Tray Icon Settings", "Iconi")
    }

    CreateDesktopShortcut(*) {
        desktop := EnvGet("USERPROFILE") "\Desktop\EQSwitch.lnk"
        ico := A_ScriptDir "\eqbox.ico"
        try {
            if FileExist(ico)
                FileCreateShortcut(A_ScriptFullPath, desktop, A_ScriptDir,, "EQ Switch", ico)
            else
                FileCreateShortcut(A_ScriptFullPath, desktop)
            ShowTip("🖥 Desktop shortcut created!")
        } catch {
            ShowTip("⚠ Could not create shortcut")
        }
    }
}

BuildProcessSection(g, ctl) {
    global PROCESS_PRIORITY
    g.AddText("xm y+10 w440 cNavy", "⚡  Process Settings")
    g.AddText("xm y+4 w440 h1 0x10")

    g.AddText("xm y+6", "Process priority:")
    ctl["priorityLevels"] := ["Normal", "AboveNormal", "High"]
    ctl["priorityCombo"] := g.AddDropDownList("x+4 yp-2 w120", ctl["priorityLevels"])
    for i, lvl in ctl["priorityLevels"] {
        if (lvl = PROCESS_PRIORITY)
            ctl["priorityCombo"].Choose(i)
    }
    if (ctl["priorityCombo"].Value = 0)
        ctl["priorityCombo"].Choose(1)
    g.AddText("x+10 yp+2 cGray", "Applied to eqgame.exe on launch")

    g.AddButton("xm y+6 w180 h24", "⚡ Process Manager...").OnEvent("Click", (*) => OpenProcessManager())
}

BuildMultiMonSection(g, ctl) {
    global MULTIMON_ENABLED, MULTIMON_HOTKEY
    g.AddText("xm y+10 w440 cNavy", "🖥  Multi-Monitor")
    g.AddText("xm y+4 w440 h1 0x10")
    ctl["multimonEnabled"] := g.AddCheckbox("xm y+6", "Enable multi-monitor toggle hotkey")
    ctl["multimonEnabled"].Value := (MULTIMON_ENABLED = "1") ? 1 : 0
    ctl["multimonEnabled"].OnEvent("Click", ToggleMultimonField)
    g.AddText("xm y+4", "Hotkey:")
    ctl["multimonHkCtrl"] := g.AddHotkey("x+6 yp-2 w120", MULTIMON_HOTKEY)
    ctl["multimonHkCtrl"].Enabled := (MULTIMON_ENABLED = "1") ? true : false
    g.AddText("x+8 yp+2 cGray", "Default: RAlt+M")

    ToggleMultimonField(*) {
        ctl["multimonHkCtrl"].Enabled := ctl["multimonEnabled"].Value ? true : false
    }
}

BuildPiPSection(g, ctl) {
    global PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY
    g.AddText("xm y+10 w440 cNavy", "📺  Picture-in-Picture")
    g.AddText("xm y+4 w440 h1 0x10")

    g.AddText("xm y+6", "Width:")
    ctl["pipWidthEdit"] := g.AddEdit("x+4 yp-2 w55 Number", PIP_WIDTH)
    g.AddUpDown("Range160-800", Integer(PIP_WIDTH))
    g.AddText("x+12 yp+2", "Height:")
    ctl["pipHeightEdit"] := g.AddEdit("x+4 yp-2 w55 Number", PIP_HEIGHT)
    g.AddUpDown("Range90-450", Integer(PIP_HEIGHT))
    g.AddText("x+12 yp+2", "Opacity:")
    ctl["pipOpacityEdit"] := g.AddEdit("x+4 yp-2 w45 Number", PIP_OPACITY)
    g.AddUpDown("Range50-255", Integer(PIP_OPACITY))
    g.AddText("x+6 yp+2 cGray", "(50-255)")
}

BuildExtrasSection(g, ctl) {
    global FLASH_SUPPRESS, AUTO_MINIMIZE, BORDER_ENABLED, BORDER_COLOR, PIP_ZOOM
    g.AddText("xm y+10 w440 cNavy", "✨  Window Extras")
    g.AddText("xm y+4 w440 h1 0x10")

    ctl["flashSuppressChk"] := g.AddCheckbox("xm y+6", "Suppress taskbar flashing on background EQ windows")
    ctl["flashSuppressChk"].Value := (FLASH_SUPPRESS = "1") ? 1 : 0

    ctl["autoMinimizeChk"] := g.AddCheckbox("xm y+4", "Auto-minimize inactive EQ windows on switch")
    ctl["autoMinimizeChk"].Value := (AUTO_MINIMIZE = "1") ? 1 : 0

    ctl["borderEnabledChk"] := g.AddCheckbox("xm y+4", "Highlight active EQ window with colored border")
    ctl["borderEnabledChk"].Value := (BORDER_ENABLED = "1") ? 1 : 0
    ctl["borderEnabledChk"].OnEvent("Click", ToggleBorderFields)

    g.AddText("xm y+4", "Border color:")
    ctl["borderColorEdit"] := g.AddEdit("x+4 yp-2 w70", BORDER_COLOR)
    ctl["borderColorEdit"].Enabled := (BORDER_ENABLED = "1") ? true : false
    g.AddText("x+6 yp+2 cGray", "Hex RGB (e.g. 00FF00=green, FF0000=red)")

    ctl["pipZoomChk"] := g.AddCheckbox("xm y+6", "PiP zoom on hover (2× magnification when mouse enters PiP)")
    ctl["pipZoomChk"].Value := (PIP_ZOOM = "1") ? 1 : 0

    ToggleBorderFields(*) {
        ctl["borderColorEdit"].Enabled := ctl["borderEnabledChk"].Value ? true : false
    }
}

BuildPathsSection(g, ctl) {
    global GINA_PATH, NOTES_FILE
    g.SetFont("s9 Bold", "Segoe UI")
    g.AddText("xm y+10 Section", "🎯 Gina path:")
    g.SetFont("s7", "Segoe UI")
    ctl["ginaEdit"] := g.AddEdit("xm y+3 w184", GINA_PATH)
    g.SetFont("s9", "Segoe UI")
    g.AddButton("x+3 yp w30 h24", "...").OnEvent("Click", (*) => BrowseFile(ctl["ginaEdit"], "Select Gina.exe", "Executable (*.exe)"))
    g.SetFont("s9 Bold", "Segoe UI")
    g.AddText("xs+232 ys", "📝 Notes file:")
    g.SetFont("s7", "Segoe UI")
    ctl["notesEdit"] := g.AddEdit("xs+232 y+3 w176", NOTES_FILE)
    g.SetFont("s9", "Segoe UI")
    g.AddButton("x+3 yp w30 h24", "...").OnEvent("Click", (*) => BrowseFile(ctl["notesEdit"], "Select notes .txt file", "Text Files (*.txt)"))
}

BuildCharacterSection(g, ctl) {
    global EQ_SERVER, RECENT_CHARS
    g.SetFont("s9", "Segoe UI")
    g.AddText("xm y+10 w440 cNavy", "📋  Character Config && Backup")
    g.AddText("xm y+3 w440 h1 0x10")
    g.AddText("xm y+4", "Server name:")
    ctl["serverEdit"] := g.AddEdit("x+4 yp-2 w120", EQ_SERVER)

    g.AddText("xm y+4", "Character:")
    recentList := (RECENT_CHARS != "") ? StrSplit(RECENT_CHARS, "|") : []
    charCombo  := g.AddComboBox("x+4 yp-2 w140", recentList)
    if (recentList.Length > 0)
        charCombo.Choose(1)
    g.AddButton("x+3 yp w20 h20", "✕").OnEvent("Click", (*) => DoRemoveChar(charCombo))
    g.AddButton("x+4 yp w60 h20", "Backup").OnEvent("Click", (*) => DoBackup(charCombo.Text))
    g.AddButton("x+3 yp w60 h20", "Restore").OnEvent("Click", (*) => DoRestore(charCombo.Text))

    ; ---- Character section helpers (closures) ----

    DoRemoveChar(combo) {
        global RECENT_CHARS
        charName := combo.Text
        if (charName = "")
            return
        existing := (RECENT_CHARS != "") ? StrSplit(RECENT_CHARS, "|") : []
        newList := []
        for name in existing {
            if (name != charName)
                newList.Push(name)
        }
        joined := ""
        for i, name in newList
            joined .= (i > 1 ? "|" : "") name
        RECENT_CHARS := joined
        SaveConfig()
        combo.Delete()
        combo.Add(newList)
        if (newList.Length > 0)
            combo.Choose(1)
        else
            combo.Text := ""
    }

    DoBackup(charName) {
        global EQ_SERVER
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
}

ShowSettingsHelp(parentHwnd) {
    h := Gui("+AlwaysOnTop +Owner" parentHwnd, "❓ EQ Switch — Help")
    h.SetFont("s9", "Segoe UI")
    h.MarginX := 14
    h.MarginY := 10

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 c0xAA3300", "⚔  Window Switch Hotkey")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "The main feature. Set a key (e.g. \ or F12) that cycles between your open EQ windows. "
        . "Only works while an EQ window is focused — won't interfere with other apps.")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "⚔  EverQuest Settings")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "EQ Executable — path to your eqgame.exe, used by the Launch feature.`n"
        . "Launch args — command-line flags passed to EQ (e.g. -patchme).`n"
        . "Server name — used to find your character log and ini files (e.g. 'dalaya').")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "🎮  Launch & Tray Options")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "Clients — how many EQ windows to launch at once (1–8).`n"
        . "Window mode — how windows are arranged after launch:`n"
        . "  • maximize — all fullscreen on primary monitor`n"
        . "  • restore — default/restored window size`n"
        . "  • sidebyside — split left/right on primary monitor`n"
        . "  • multimonitor — one window per monitor, maximized`n`n"
        . "Launch One / Launch All hotkeys — global shortcuts to launch clients from anywhere.`n`n"
        . "Tray double-click — launches a single client.`n"
        . "Tray triple-click — launches all clients (off by default, 5s cooldown).`n"
        . "Tray middle-click — opens your notes file.`n"
        . "Desktop Shortcut — creates an EQSwitch shortcut on your Desktop.`n"
        . "Tray Icon Settings — opens Windows settings to pin EQSwitch to the taskbar tray.")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "⚡  Process Settings")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "Process priority — sets eqgame.exe priority after launch (Normal, AboveNormal, High). "
        . "Higher priority helps prevent EQ from lagging when alt-tabbed or on virtual desktops.`n`n"
        . "Process Manager — opens a dedicated window showing all running EQ processes "
        . "with their PIDs, priorities, and CPU affinity. Lets you configure which CPU cores "
        . "EQ can use (useful since EQ defaults to a single core). Changes are applied "
        . "automatically on future launches, or you can apply them to already-running clients.")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "🖥  Multi-Monitor")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "A global hotkey (works even outside EQ) that cycles through multi-monitor layouts: "
        . "spread windows across monitors, swap which window is on which monitor, "
        . "or stack them all back on the primary. Uncheck to disable.")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "🎯  Gina path")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "Path to Gina.exe (the EQ trigger/audio overlay tool).")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "📝  Notes file")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "A .txt file for your personal EQ notes. "
        . "Leave blank and EQSwitch will offer to create one on first use.")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "📋  Character Config && Backup")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "Backs up your character UI/keybind config files (UI_Name_server.ini "
        . "and Name_server.ini) to and from your Desktop.`n`n"
        . "Character — type or pick a recent character name.`n"
        . "Backup — copies that character's config files to your Desktop.`n"
        . "Restore — copies them back from Desktop into the EQ folder.")

    h.AddText("w420 y+10 h1 0x10")
    h.AddButton("w80 h26 y+6 Default", "Close").OnEvent("Click", (*) => h.Destroy())
    h.OnEvent("Escape", (*) => h.Destroy())
    h.Show("AutoSize")
}

; Shared file browser helper for Settings sections
BrowseFile(ctrl, title, filter) {
    f := FileSelect(, ctrl.Value, title, filter)
    if f
        ctrl.Value := f
}

; =========================================================
; SETTINGS GUI
; =========================================================
OpenSettings(*) {
    global EQ_EXE, EQ_ARGS, EQ_HOTKEY, DBLCLICK_LAUNCH, SETTINGS_OPEN
    global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES, RECENT_CHARS
    global EQ_SERVER, NUM_CLIENTS, FIX_MODE, STARTUP_ENABLED
    global MULTIMON_HOTKEY, MULTIMON_ENABLED, g_version, TOOLTIP_MS
    global LAUNCH_ONE_HOTKEY, LAUNCH_ALL_HOTKEY, TRIPLECLICK_LAUNCH
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
    g.MarginY := 10

    ; Build all UI sections via dedicated builder functions
    ctl := Map()
    BuildHotkeySection(g, ctl)
    BuildEverQuestSection(g, ctl)
    BuildLaunchSection(g, ctl)
    BuildProcessSection(g, ctl)
    BuildMultiMonSection(g, ctl)
    BuildPiPSection(g, ctl)
    BuildExtrasSection(g, ctl)
    BuildPathsSection(g, ctl)
    BuildCharacterSection(g, ctl)
    g.SetFont("s9", "Segoe UI")

    ; ── Save / Cancel / Help ──────────────────────────────
    g.AddText("xm y+8 w440 h1 0x10")
    g.AddButton("xm y+6 w80 h28 Default", "💾 Save").OnEvent("Click", SaveAndClose)
    g.AddButton("x+8 yp w80 h28", "Cancel").OnEvent("Click", (*) => (SETTINGS_OPEN := false, g.Destroy()))
    g.AddButton("x+100 yp w80 h28", "❓ Help").OnEvent("Click", (*) => ShowSettingsHelp(g.Hwnd))

    g.Show("AutoSize")

    ; ---- SaveAndClose (reads from ctl Map) ----------------

    SaveAndClose(*) {
        global EQ_EXE, EQ_ARGS, EQ_HOTKEY, DBLCLICK_LAUNCH
        global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES
        global EQ_SERVER, NUM_CLIENTS, FIX_MODE, STARTUP_ENABLED
        global MULTIMON_HOTKEY, MULTIMON_ENABLED, TOOLTIP_MS
        global LAUNCH_ONE_HOTKEY, LAUNCH_ALL_HOTKEY, TRIPLECLICK_LAUNCH
        global PROCESS_PRIORITY, CPU_AFFINITY
        global FIX_TOP_OFFSET, FIX_BOTTOM_OFFSET
        global PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY
        global FLASH_SUPPRESS, AUTO_MINIMIZE, BORDER_ENABLED, BORDER_COLOR, PIP_ZOOM

        newHotkey := ctl["hotkeyCtrl"].Value
        newMultimonHk := ctl["multimonHkCtrl"].Value
        newLaunchOneHk := ctl["launchOneHkCtrl"].Value
        newLaunchAllHk := ctl["launchAllHkCtrl"].Value

        ; Unbind the old hotkeys
        HotIfWinActive("ahk_exe eqgame.exe")
        try Hotkey(EQ_HOTKEY, "Off")
        HotIfWinActive()
        if (MULTIMON_HOTKEY != "")
            try Hotkey(MULTIMON_HOTKEY, "Off")
        if (LAUNCH_ONE_HOTKEY != "")
            try Hotkey(LAUNCH_ONE_HOTKEY, "Off")
        if (LAUNCH_ALL_HOTKEY != "")
            try Hotkey(LAUNCH_ALL_HOTKEY, "Off")

        ; Validate paths (non-blocking warnings)
        if (ctl["exeEdit"].Value != "" && !FileExist(ctl["exeEdit"].Value))
            MsgBox("The EQ executable path doesn't exist:`n" ctl["exeEdit"].Value
                . "`n`nSettings will be saved, but Launch won't work until the path is valid.",
                "EQ Switch — Warning", "Icon!")
        if (ctl["ginaEdit"].Value != "" && !FileExist(ctl["ginaEdit"].Value))
            MsgBox("The Gina path doesn't exist:`n" ctl["ginaEdit"].Value
                . "`n`nSettings will be saved, but Open Gina won't work until the path is valid.",
                "EQ Switch — Warning", "Icon!")
        if (ctl["notesEdit"].Value != "" && !FileExist(ctl["notesEdit"].Value))
            MsgBox("The notes file doesn't exist:`n" ctl["notesEdit"].Value
                . "`n`nSettings will be saved. The file will be created when you first open Notes.",
                "EQ Switch — Warning", "Icon!")

        EQ_EXE          := ctl["exeEdit"].Value
        EQ_ARGS         := ctl["argsEdit"].Value
        DBLCLICK_LAUNCH := ctl["dblClickChk"].Value ? "1" : "0"
        GINA_PATH       := ctl["ginaEdit"].Value
        NOTES_FILE      := ctl["notesEdit"].Value
        MIDCLICK_NOTES  := ctl["midClickChk"].Value ? "1" : "0"
        TRIPLECLICK_LAUNCH := ctl["tripleClickChk"].Value ? "1" : "0"
        EQ_SERVER       := ctl["serverEdit"].Value
        ; Validate client count — must be 1-8
        clientVal := ctl["clientsEdit"].Value
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
        ctl["clientsEdit"].Value := NUM_CLIENTS
        FIX_MODE        := ctl["fixModes"][ctl["fixModeCombo"].Value]
        PROCESS_PRIORITY := ctl["priorityLevels"][ctl["priorityCombo"].Value]
        FIX_TOP_OFFSET   := ctl["topOffsetEdit"].Value
        FIX_BOTTOM_OFFSET := ctl["bottomOffsetEdit"].Value
        PIP_WIDTH        := ctl["pipWidthEdit"].Value
        PIP_HEIGHT       := ctl["pipHeightEdit"].Value
        PIP_OPACITY      := ctl["pipOpacityEdit"].Value
        FLASH_SUPPRESS  := ctl["flashSuppressChk"].Value ? "1" : "0"
        AUTO_MINIMIZE   := ctl["autoMinimizeChk"].Value ? "1" : "0"
        BORDER_ENABLED  := ctl["borderEnabledChk"].Value ? "1" : "0"
        BORDER_COLOR    := ctl["borderColorEdit"].Value
        PIP_ZOOM        := ctl["pipZoomChk"].Value ? "1" : "0"
        STARTUP_ENABLED := ctl["startupChk"].Value ? "1" : "0"

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
        MULTIMON_ENABLED := ctl["multimonEnabled"].Value ? "1" : "0"
        MULTIMON_HOTKEY := newMultimonHk
        if (MULTIMON_ENABLED = "1" && newMultimonHk != "") {
            if !BindMultiMonHotkey(MULTIMON_HOTKEY)
                MsgBox("The multi-monitor hotkey '" MULTIMON_HOTKEY "' could not be bound — it may be invalid or already in use.",
                    "EQ Switch — Warning", "Icon!")
        }

        ; Handle launch hotkeys
        LAUNCH_ONE_HOTKEY := newLaunchOneHk
        if (newLaunchOneHk != "") {
            if !BindLaunchHotkey(LAUNCH_ONE_HOTKEY, "one")
                MsgBox("The Launch One hotkey '" LAUNCH_ONE_HOTKEY "' could not be bound.",
                    "EQ Switch — Warning", "Icon!")
        }
        LAUNCH_ALL_HOTKEY := newLaunchAllHk
        if (newLaunchAllHk != "") {
            if !BindLaunchHotkey(LAUNCH_ALL_HOTKEY, "all")
                MsgBox("The Launch All hotkey '" LAUNCH_ALL_HOTKEY "' could not be bound.",
                    "EQ Switch — Warning", "Icon!")
        }

        SaveConfig()
        UpdateFeatureTimer()
        UpdateExtrasCheckmarks()
        ; Clean up border if disabled
        if (BORDER_ENABLED = "0")
            DestroyBorder()
        UpdateTrayTip()
        UpdateTrayMenuLabels()

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
        ShowTip("Settings saved!")
    }

    } catch as err {
        ; If anything inside Settings blew up, make sure the flag is cleared
        ; so the user can still open Settings again without restarting
        SETTINGS_OPEN := false
        try g.Destroy()
        ShowTip("⚠ Settings error: " err.Message, 3000)
    }
}

; =========================================================
; PICTURE-IN-PICTURE OVERLAY
; =========================================================
g_pipEnabled    := false
g_pipGui        := ""
g_pipThumbnails := []
g_pipTimer      := ""
g_pipLastActive := 0
g_pipZoomGui    := ""
g_pipZoomThumb  := 0
g_pipZoomIndex  := -1
; PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY are loaded from config
; (defaults: 320, 180, 200) — see LoadConfig()

TogglePiP(*) {
    global g_pipEnabled
    if g_pipEnabled
        DestroyPiP()
    else
        CreatePiP()
}

CreatePiP(*) {
    global g_pipEnabled, g_pipGui, g_pipThumbnails, g_pipTimer
    global PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY, TOOLTIP_MS

    if g_pipEnabled
        DestroyPiP()

    visible := GetVisibleEqWindows()
    if (visible.Length < 2) {
        ShowTip("⚠ Need at least 2 EQ windows for PiP!")
        return
    }

    ; Get the active EQ window (or first one)
    activeID := 0
    try activeID := WinGetID("A")

    ; Find non-active EQ windows to show as thumbnails
    altWindows := []
    for id in visible {
        if (id != activeID)
            altWindows.Push(id)
    }
    if (altWindows.Length = 0)
        altWindows.Push(visible[2])  ; fallback: show second window

    ; Create the overlay GUI — borderless, always on top, transparent background
    pipG := Gui("+AlwaysOnTop -Caption +ToolWindow +E0x20")  ; E0x20 = WS_EX_TRANSPARENT (click-through)
    pipG.BackColor := "000000"

    ; Calculate total size needed
    totalH := altWindows.Length * PIP_HEIGHT + (altWindows.Length - 1) * 4
    totalW := PIP_WIDTH

    ; Position in bottom-right corner of primary monitor
    try MonitorGetWorkArea(1, &mLeft, &mTop, &mRight, &mBottom)
    catch {
        mRight := A_ScreenWidth
        mBottom := A_ScreenHeight
    }
    posX := mRight - totalW - 10
    posY := mBottom - totalH - 10

    pipG.Show("x" posX " y" posY " w" totalW " h" totalH " NoActivate")

    ; Make the window semi-transparent
    WinSetTransparent(PIP_OPACITY, "ahk_id " pipG.Hwnd)

    ; Register DWM thumbnails for each alt window
    g_pipThumbnails := []
    yPos := 0
    for i, srcId in altWindows {
        hThumb := 0
        hr := DllCall("dwmapi\DwmRegisterThumbnail",
            "Ptr", pipG.Hwnd,     ; destination
            "Ptr", srcId,          ; source
            "Ptr*", &hThumb,
            "Int")

        if (hr = 0 && hThumb != 0) {
            ; Set thumbnail properties
            props := Buffer(48, 0)
            ; dwFlags: RECTDESTINATION | VISIBLE | SOURCECLIENTAREAONLY
            NumPut("UInt", 0x01 | 0x08 | 0x10, props, 0)
            ; rcDestination: left, top, right, bottom
            NumPut("Int", 0, props, 4)           ; left
            NumPut("Int", yPos, props, 8)         ; top
            NumPut("Int", PIP_WIDTH, props, 12)   ; right
            NumPut("Int", yPos + PIP_HEIGHT, props, 16) ; bottom
            ; fVisible
            NumPut("Int", 1, props, 40)
            ; fSourceClientAreaOnly
            NumPut("Int", 1, props, 44)

            DllCall("dwmapi\DwmUpdateThumbnailProperties", "Ptr", hThumb, "Ptr", props)
            g_pipThumbnails.Push(hThumb)
        }
        yPos += PIP_HEIGHT + 4
    }

    g_pipGui := pipG
    g_pipEnabled := true
    g_pipLastActive := activeID

    ; Set up a timer to refresh when active window changes
    g_pipTimer := RefreshPiP
    SetTimer(g_pipTimer, 500)
}

RefreshPiP(*) {
    global g_pipEnabled, g_pipGui, g_pipThumbnails, g_pipLastActive
    if !g_pipEnabled
        return

    ; Check if PiP window still exists
    try {
        if !WinExist("ahk_id " g_pipGui.Hwnd) {
            DestroyPiP()
            return
        }
    } catch {
        DestroyPiP()
        return
    }

    ; Check if there are still EQ windows
    visible := GetVisibleEqWindows()
    if (visible.Length < 2) {
        DestroyPiP()
        return
    }

    ; If active EQ window changed, swap PiP thumbnail sources without rebuilding the GUI
    activeID := 0
    try activeID := WinGetID("A")
    if (activeID != g_pipLastActive) {
        ; Only update if the new active window is an EQ window
        isEq := false
        for id in visible {
            if (id = activeID) {
                isEq := true
                break
            }
        }
        if isEq
            SwapPiPSources(visible, activeID)
    }

    ; P4-01: PiP zoom on hover
    global PIP_ZOOM
    if (PIP_ZOOM = "1")
        UpdatePiPZoom()
}

; Swap PiP thumbnail sources on the existing GUI (avoids flicker from full teardown)
SwapPiPSources(visible, activeID) {
    global g_pipGui, g_pipThumbnails, g_pipLastActive
    global PIP_WIDTH, PIP_HEIGHT

    ; Find non-active EQ windows
    altWindows := []
    for id in visible {
        if (id != activeID)
            altWindows.Push(id)
    }
    if (altWindows.Length = 0)
        altWindows.Push(visible[2])

    ; If alt window count changed, need full rebuild for GUI resize
    if (altWindows.Length != g_pipThumbnails.Length) {
        DestroyPiP()
        CreatePiP()
        return
    }

    ; Unregister old thumbnails
    for hThumb in g_pipThumbnails {
        try DllCall("dwmapi\DwmUnregisterThumbnail", "Ptr", hThumb)
    }
    g_pipThumbnails := []

    ; Register new thumbnails on the existing GUI
    yPos := 0
    for i, srcId in altWindows {
        hThumb := 0
        hr := DllCall("dwmapi\DwmRegisterThumbnail",
            "Ptr", g_pipGui.Hwnd,
            "Ptr", srcId,
            "Ptr*", &hThumb,
            "Int")

        if (hr = 0 && hThumb != 0) {
            props := Buffer(48, 0)
            NumPut("UInt", 0x01 | 0x08 | 0x10, props, 0)
            NumPut("Int", 0, props, 4)
            NumPut("Int", yPos, props, 8)
            NumPut("Int", PIP_WIDTH, props, 12)
            NumPut("Int", yPos + PIP_HEIGHT, props, 16)
            NumPut("Int", 1, props, 40)
            NumPut("Int", 1, props, 44)
            DllCall("dwmapi\DwmUpdateThumbnailProperties", "Ptr", hThumb, "Ptr", props)
            g_pipThumbnails.Push(hThumb)
        }
        yPos += PIP_HEIGHT + 4
    }

    g_pipLastActive := activeID
}

DestroyPiP(*) {
    global g_pipEnabled, g_pipGui, g_pipThumbnails, g_pipTimer

    ; Unregister all thumbnails
    for hThumb in g_pipThumbnails {
        try DllCall("dwmapi\DwmUnregisterThumbnail", "Ptr", hThumb)
    }
    g_pipThumbnails := []

    ; Stop refresh timer
    if g_pipTimer
        SetTimer(g_pipTimer, 0)
    g_pipTimer := ""

    ; Destroy the GUI
    if g_pipGui {
        try g_pipGui.Destroy()
        g_pipGui := ""
    }

    g_pipEnabled := false
    DestroyPiPZoom()
}

; ── PiP Zoom on Hover ──────────────────────────────────
UpdatePiPZoom(*) {
    global g_pipGui, g_pipThumbnails, g_pipZoomGui, g_pipZoomThumb, g_pipZoomIndex
    global PIP_WIDTH, PIP_HEIGHT

    if (!g_pipGui || g_pipThumbnails.Length = 0)
        return

    ; Get mouse position and PiP GUI position
    MouseGetPos(&mx, &my)
    try WinGetPos(&px, &py, &pw, &ph, "ahk_id " g_pipGui.Hwnd)
    catch
        return

    ; Check if mouse is within PiP bounds
    if (mx < px || mx > px + pw || my < py || my > py + ph) {
        if (g_pipZoomIndex >= 0)
            DestroyPiPZoom()
        return
    }

    ; Determine which thumbnail the mouse is over
    thumbIdx := -1
    yOffset := my - py
    thumbH := Integer(PIP_HEIGHT) + 4  ; height + gap
    Loop g_pipThumbnails.Length {
        thumbTop := (A_Index - 1) * thumbH
        thumbBottom := thumbTop + Integer(PIP_HEIGHT)
        if (yOffset >= thumbTop && yOffset < thumbBottom) {
            thumbIdx := A_Index
            break
        }
    }

    if (thumbIdx < 0) {
        if (g_pipZoomIndex >= 0)
            DestroyPiPZoom()
        return
    }

    ; Already zooming this thumbnail?
    if (thumbIdx = g_pipZoomIndex)
        return

    ; Create zoom popup for this thumbnail
    ShowPiPZoom(thumbIdx)
}

ShowPiPZoom(thumbIdx) {
    global g_pipGui, g_pipThumbnails, g_pipZoomGui, g_pipZoomThumb, g_pipZoomIndex
    global PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY

    ; Clean up previous zoom
    DestroyPiPZoom()

    ; Find which source window this thumbnail shows
    visible := GetVisibleEqWindows()
    activeID := 0
    try activeID := WinGetID("A")
    altWindows := []
    for id in visible {
        if (id != activeID)
            altWindows.Push(id)
    }
    if (altWindows.Length = 0 && visible.Length > 1)
        altWindows.Push(visible[2])

    if (thumbIdx > altWindows.Length)
        return

    srcId := altWindows[thumbIdx]

    ; Zoom size: 2× the PiP dimensions
    zoomW := Integer(PIP_WIDTH) * 2
    zoomH := Integer(PIP_HEIGHT) * 2

    ; Position: to the left of the PiP GUI
    try WinGetPos(&px, &py, &pw, &ph, "ahk_id " g_pipGui.Hwnd)
    catch
        return

    zoomX := px - zoomW - 8
    zoomY := py + (thumbIdx - 1) * (Integer(PIP_HEIGHT) + 4) - (Integer(PIP_HEIGHT) // 2)

    ; Keep zoom on screen
    if (zoomX < 0)
        zoomX := px + pw + 8  ; flip to right side
    if (zoomY < 0)
        zoomY := 0

    ; Create zoom GUI
    zg := Gui("+AlwaysOnTop -Caption +ToolWindow +E0x20")
    zg.BackColor := "000000"
    zg.Show("x" zoomX " y" zoomY " w" zoomW " h" zoomH " NoActivate")
    opacityVal := Min(Integer(PIP_OPACITY) + 40, 255)
    WinSetTransparent(opacityVal, "ahk_id " zg.Hwnd)

    ; Register DWM thumbnail for the zoomed view
    hThumb := 0
    hr := DllCall("dwmapi\DwmRegisterThumbnail",
        "Ptr", zg.Hwnd,
        "Ptr", srcId,
        "Ptr*", &hThumb,
        "Int")

    if (hr = 0 && hThumb != 0) {
        props := Buffer(48, 0)
        NumPut("UInt", 0x01 | 0x08 | 0x10, props, 0)  ; RECTDEST | VISIBLE | SOURCECLIENTAREAONLY
        NumPut("Int", 0, props, 4)          ; left
        NumPut("Int", 0, props, 8)          ; top
        NumPut("Int", zoomW, props, 12)     ; right
        NumPut("Int", zoomH, props, 16)     ; bottom
        NumPut("Int", 1, props, 40)         ; fVisible
        NumPut("Int", 1, props, 44)         ; fSourceClientAreaOnly
        DllCall("dwmapi\DwmUpdateThumbnailProperties", "Ptr", hThumb, "Ptr", props)
    }

    g_pipZoomGui := zg
    g_pipZoomThumb := hThumb
    g_pipZoomIndex := thumbIdx
}

DestroyPiPZoom(*) {
    global g_pipZoomGui, g_pipZoomThumb, g_pipZoomIndex
    if (g_pipZoomThumb)
        try DllCall("dwmapi\DwmUnregisterThumbnail", "Ptr", g_pipZoomThumb)
    g_pipZoomThumb := 0
    if (g_pipZoomGui) {
        try g_pipZoomGui.Destroy()
        g_pipZoomGui := ""
    }
    g_pipZoomIndex := -1
}

; =========================================================
; WINDOW FEATURES ENGINE
; =========================================================
; Unified timer for: flash suppression (P2-06), auto-minimize (P2-05), border highlight (P2-04)
g_featureTimer      := ""
g_featureLastActive := 0
g_borderGuis        := []    ; [top, bottom, left, right] bar GUIs
g_borderTarget      := 0     ; hwnd currently highlighted

; Start/stop the feature timer based on which features are enabled
UpdateFeatureTimer() {
    global FLASH_SUPPRESS, AUTO_MINIMIZE, BORDER_ENABLED, g_featureTimer
    needTimer := (FLASH_SUPPRESS = "1" || AUTO_MINIMIZE = "1" || BORDER_ENABLED = "1")
    if (needTimer && !g_featureTimer) {
        g_featureTimer := FeatureRefresh
        SetTimer(g_featureTimer, 250)
    } else if (!needTimer && g_featureTimer) {
        SetTimer(g_featureTimer, 0)
        g_featureTimer := ""
        DestroyBorder()
    }
}

FeatureRefresh(*) {
    global FLASH_SUPPRESS, AUTO_MINIMIZE, BORDER_ENABLED
    global g_featureLastActive

    visible := GetVisibleEqWindows()
    if (visible.Length = 0) {
        HideBorder()
        return
    }

    activeID := 0
    try activeID := WinGetID("A")

    ; Is the active window an EQ window?
    isEq := false
    for id in visible {
        if (id = activeID) {
            isEq := true
            break
        }
    }

    ; P2-06: Flash suppression — stop taskbar flashing on background EQ windows
    if (FLASH_SUPPRESS = "1") {
        for id in visible {
            if (id != activeID)
                try DllCall("FlashWindow", "Ptr", id, "Int", 0)
        }
    }

    ; P2-05: Auto-minimize — minimize inactive EQ windows when an EQ window is active
    if (AUTO_MINIMIZE = "1" && isEq && activeID != g_featureLastActive) {
        for id in visible {
            if (id != activeID) {
                try {
                    if (WinGetMinMax("ahk_id " id) != -1)  ; not already minimized
                        WinMinimize("ahk_id " id)
                }
            }
        }
    }

    ; P2-04: Active window highlight border
    if (BORDER_ENABLED = "1") {
        if (isEq && visible.Length >= 2)
            UpdateBorder(activeID)
        else
            HideBorder()
    }

    if isEq
        g_featureLastActive := activeID
}

; ── Border Highlight ──────────────────────────────────
CreateBorder() {
    global g_borderGuis, BORDER_COLOR
    if (g_borderGuis.Length > 0)
        return  ; already created

    Loop 4 {
        bar := Gui("+AlwaysOnTop -Caption +ToolWindow +E0x20")  ; click-through
        bar.BackColor := BORDER_COLOR
        g_borderGuis.Push(bar)
    }
}

UpdateBorder(targetHwnd) {
    global g_borderGuis, g_borderTarget, BORDER_COLOR
    static BW := 3  ; border width in pixels

    if (g_borderGuis.Length = 0)
        CreateBorder()

    ; Get target window position
    try {
        WinGetPos(&wx, &wy, &ww, &wh, "ahk_id " targetHwnd)
    } catch {
        HideBorder()
        return
    }

    ; Update color if it changed
    if (g_borderGuis[1].BackColor != BORDER_COLOR) {
        for bar in g_borderGuis
            bar.BackColor := BORDER_COLOR
    }

    ; Position the 4 bars around the window
    g_borderGuis[1].Show("x" (wx - BW) " y" (wy - BW) " w" (ww + 2*BW) " h" BW " NoActivate")     ; top
    g_borderGuis[2].Show("x" (wx - BW) " y" (wy + wh) " w" (ww + 2*BW) " h" BW " NoActivate")     ; bottom
    g_borderGuis[3].Show("x" (wx - BW) " y" wy " w" BW " h" wh " NoActivate")                       ; left
    g_borderGuis[4].Show("x" (wx + ww) " y" wy " w" BW " h" wh " NoActivate")                       ; right

    g_borderTarget := targetHwnd
}

HideBorder() {
    global g_borderGuis, g_borderTarget
    for bar in g_borderGuis {
        try bar.Show("Hide")
    }
    g_borderTarget := 0
}

DestroyBorder() {
    global g_borderGuis, g_borderTarget
    for bar in g_borderGuis {
        try bar.Destroy()
    }
    g_borderGuis := []
    g_borderTarget := 0
}

; ── Tray Toggle Helpers ──────────────────────────────────
ToggleBorderFromTray(*) {
    global BORDER_ENABLED
    BORDER_ENABLED := (BORDER_ENABLED = "1") ? "0" : "1"
    if (BORDER_ENABLED = "0")
        DestroyBorder()
    UpdateFeatureTimer()
    UpdateExtrasCheckmarks()
    SaveConfig()
    ShowTip(BORDER_ENABLED = "1" ? "🔲 Active border ON" : "🔲 Active border OFF")
}

ToggleAutoMinFromTray(*) {
    global AUTO_MINIMIZE
    AUTO_MINIMIZE := (AUTO_MINIMIZE = "1") ? "0" : "1"
    UpdateFeatureTimer()
    UpdateExtrasCheckmarks()
    SaveConfig()
    ShowTip(AUTO_MINIMIZE = "1" ? "📦 Auto-minimize ON" : "📦 Auto-minimize OFF")
}

ToggleFlashFromTray(*) {
    global FLASH_SUPPRESS
    FLASH_SUPPRESS := (FLASH_SUPPRESS = "1") ? "0" : "1"
    UpdateFeatureTimer()
    UpdateExtrasCheckmarks()
    SaveConfig()
    ShowTip(FLASH_SUPPRESS = "1" ? "🔕 Flash suppress ON" : "🔕 Flash suppress OFF")
}

UpdateExtrasCheckmarks() {
    global BORDER_ENABLED, AUTO_MINIMIZE, FLASH_SUPPRESS
    try {
        if (BORDER_ENABLED = "1") A_TrayMenu.Check("🔲  Active Border")
        else A_TrayMenu.Uncheck("🔲  Active Border")
    }
    try {
        if (AUTO_MINIMIZE = "1") A_TrayMenu.Check("📦  Auto-Minimize")
        else A_TrayMenu.Uncheck("📦  Auto-Minimize")
    }
    try {
        if (FLASH_SUPPRESS = "1") A_TrayMenu.Check("🔕  Flash Suppress")
        else A_TrayMenu.Uncheck("🔕  Flash Suppress")
    }
}

; Start feature timer on load if any feature is enabled
UpdateFeatureTimer()

; =========================================================
; PROCESS MANAGER GUI
; =========================================================
OpenProcessManager(*) {
    global CPU_AFFINITY, PROCESS_PRIORITY, TOOLTIP_MS, g_pmOpen
    if g_pmOpen
        return
    g_pmOpen := true

    coreCount := GetCoreCount()
    pm := Gui("+AlwaysOnTop", "⚡ EQ Switch — Process Manager")
    pm.SetFont("s9", "Segoe UI")
    pm.MarginX := 14
    pm.MarginY := 10

    ; ── Running Processes ──
    pm.SetFont("s10 Bold", "Segoe UI")
    pm.AddText("w500 c0xAA3300", "⚡  Running EQ Processes")
    pm.SetFont("s9", "Segoe UI")
    pm.AddText("xm y+4 w500 h1 0x10")

    processList := pm.AddListView("xm y+6 w500 h120 +Grid", ["PID", "Window Title", "Priority", "Affinity Mask"])
    processList.ModifyCol(1, 60)
    processList.ModifyCol(2, 220)
    processList.ModifyCol(3, 100)
    processList.ModifyCol(4, 100)

    RefreshProcessList(*) {
        processList.Delete()
        for id in WinGetList("ahk_exe eqgame.exe") {
            try {
                pid := WinGetPID("ahk_id " id)
                title := WinGetTitle("ahk_id " id)
                ; Read current priority
                pri := GetProcessPriorityName(pid)
                ; Read current affinity
                affStr := "—"
                try {
                    hProc := DllCall("OpenProcess", "UInt", 0x0400, "Int", 0, "UInt", pid, "Ptr")  ; PROCESS_QUERY_INFORMATION
                    if (hProc) {
                        procMask := 0
                        sysMask := 0
                        DllCall("GetProcessAffinityMask", "Ptr", hProc, "UPtr*", &procMask, "UPtr*", &sysMask)
                        DllCall("CloseHandle", "Ptr", hProc)
                        affStr := String(procMask)
                    }
                }
                processList.Add(, pid, title, pri, affStr)
            }
        }
        if (processList.GetCount() = 0)
            processList.Add(, "—", "No EQ processes running", "—", "—")
    }
    RefreshProcessList()

    pm.AddButton("xm y+6 w100 h24", "🔄 Refresh").OnEvent("Click", RefreshProcessList)

    ApplyToRunning(*) {
        if (PROCESS_PRIORITY != "Normal" && PROCESS_PRIORITY != "")
            ApplyProcessPriority(PROCESS_PRIORITY)
        if (CPU_AFFINITY != "")
            ApplyAffinityToAll(CPU_AFFINITY)
        RefreshProcessList()
        ShowTip("⚡ Process settings applied!")
    }
    pm.AddButton("x+8 yp w180 h24", "⚡ Apply Settings to Running").OnEvent("Click", ApplyToRunning)

    ; ── CPU Affinity ──
    pm.AddText("xm y+12 w500 h1 0x10")
    pm.SetFont("s10 Bold", "Segoe UI")
    pm.AddText("xm y+6 w500 cNavy", "🔧  CPU Affinity (applied on launch)")
    pm.SetFont("s9", "Segoe UI")
    pm.AddText("xm y+4 cGray", "Select which CPU cores eqgame.exe can use. Unchecking all = use all cores.")

    cores := AffinityMaskToCores(CPU_AFFINITY, coreCount)
    coreChecks := []
    ; Arrange checkboxes in rows of 8
    Loop coreCount {
        xOpt := (Mod(A_Index - 1, 8) = 0) ? "xm y+4" : "x+6 yp"
        chk := pm.AddCheckbox(xOpt, "Core " A_Index)
        chk.Value := cores[A_Index]
        coreChecks.Push(chk)
    }

    pm.AddButton("xm y+8 w80 h24", "All").OnEvent("Click", SelectAllCores)
    pm.AddButton("x+6 yp w80 h24", "None").OnEvent("Click", SelectNoCores)

    SelectAllCores(*) {
        for chk in coreChecks
            chk.Value := 1
    }
    SelectNoCores(*) {
        for chk in coreChecks
            chk.Value := 0
    }

    ; ── Save / Close ──
    pm.AddText("xm y+10 w500 h1 0x10")
    pm.AddButton("xm y+6 w80 h28 Default", "💾 Save").OnEvent("Click", SavePM)
    pm.AddButton("x+8 yp w80 h28", "Close").OnEvent("Click", (*) => (g_pmOpen := false, pm.Destroy()))

    SavePM(*) {
        global CPU_AFFINITY
        ; Build affinity mask from checkboxes
        coreVals := []
        for chk in coreChecks
            coreVals.Push(chk.Value)
        mask := CoresMaskToAffinity(coreVals)
        ; If all cores selected or none selected, clear the affinity (use system default)
        allSelected := true
        noneSelected := true
        for val in coreVals {
            if !val
                allSelected := false
            if val
                noneSelected := false
        }
        if (allSelected || noneSelected)
            CPU_AFFINITY := ""
        else
            CPU_AFFINITY := String(mask)
        SaveConfig()
        ShowTip("⚡ Process settings saved!")
    }

    pm.OnEvent("Escape", (*) => (g_pmOpen := false, pm.Destroy()))
    pm.OnEvent("Close", (*) => (g_pmOpen := false, pm.Destroy()))
    pm.Show("AutoSize")
}

; =========================================================
; LAUNCH
; =========================================================
LaunchOne(*) {
    global EQ_EXE, EQ_ARGS, TOOLTIP_MS, PROCESS_PRIORITY, CPU_AFFINITY, g_launchActive
    static lastLaunch := 0
    if g_launchActive {
        ShowTip("⚠ Launch already in progress!")
        return
    }
    ; Debounce: ignore rapid double-clicks within 3 seconds
    if (A_TickCount - lastLaunch < 3000)
        return
    lastLaunch := A_TickCount
    if !FileExist(EQ_EXE) {
        ShowTip("⚠ EQ executable not found — check Settings")
        return
    }
    eqDir := GetEqDir()
    Run('"' EQ_EXE '" ' EQ_ARGS, eqDir, , &newPid)
    ; Apply process settings after a short delay to let the process initialize
    needsPri := (PROCESS_PRIORITY != "Normal" && PROCESS_PRIORITY != "")
    needsAff := (CPU_AFFINITY != "")
    if (needsPri || needsAff) {
        ApplyDelayed(*) {
            if needsPri
                try ProcessSetPriority(PROCESS_PRIORITY, newPid)
            if needsAff
                ApplyAffinityToPid(newPid, CPU_AFFINITY)
        }
        SetTimer(ApplyDelayed, -2000)
    }
}

LaunchBoth(*) {
    global g_launchActive
    global EQ_EXE, EQ_ARGS, LAUNCH_DELAY, LAUNCH_FIX_DELAY, NUM_CLIENTS, TOOLTIP_MS, PROCESS_PRIORITY, CPU_AFFINITY, FIX_MODE
    if g_launchActive {
        ShowTip("⚠ Launch already in progress!")
        return
    }
    if !FileExist(EQ_EXE) {
        ShowTip("⚠ EQ executable not found — check Settings")
        return
    }
    eqDir := GetEqDir()
    try {
        count   := Integer(NUM_CLIENTS)
        delay   := Integer(LAUNCH_DELAY)
        fixWait := Integer(LAUNCH_FIX_DELAY)
    } catch {
        ShowTip("⚠ Invalid launch settings — check NUM_CLIENTS, LAUNCH_DELAY, or LAUNCH_FIX_DELAY in config", 3000)
        return
    }
    if (count < 1) {
        ShowTip("⚠ Number of clients must be at least 1")
        return
    }

    ; Snapshot settings at launch time so mid-launch Settings changes can't affect behavior
    launchExe      := EQ_EXE
    launchArgs     := EQ_ARGS
    launchPriority := PROCESS_PRIORITY
    launchAffinity := CPU_AFFINITY
    launchFixMode  := FIX_MODE

    g_launchActive := true
    pids := []
    launchIdx := 0

    ; Async launch: each client launched via timer, UI stays responsive
    DoNextLaunch() {
        launchIdx++
        ToolTip("🎮 Launching client " launchIdx " of " count "...")
        try {
            Run('"' launchExe '" ' launchArgs, eqDir, , &newPid)
            pids.Push(newPid)
        } catch as err {
            ShowTip("⚠ Failed to launch client " launchIdx ": " err.Message, 3000)
            g_launchActive := false
            return
        }
        if (launchIdx < count)
            SetTimer(DoNextLaunch, -delay)
        else {
            ; All clients launched — apply process settings
            if (launchPriority != "Normal" && launchPriority != "") {
                for pid in pids
                    try ProcessSetPriority(launchPriority, pid)
            }
            if (launchAffinity != "") {
                for pid in pids
                    ApplyAffinityToPid(pid, launchAffinity)
            }
            ToolTip("🎮 Waiting for windows to settle...")
            SetTimer(DoFinalize, -fixWait)
        }
    }

    DoFinalize() {
        global g_launchActive, FIX_MODE
        ToolTip("🪟 Arranging windows...")
        ; Temporarily inject captured fix mode so FixWindows() uses launch-time setting
        savedMode := FIX_MODE
        FIX_MODE := launchFixMode
        FixWindows()
        FIX_MODE := savedMode
        g_launchActive := false
        ShowTip("✅ Ready to play!")
    }

    DoNextLaunch()
}


; ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
;          ~ Long Live Dalaya ~
; ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
