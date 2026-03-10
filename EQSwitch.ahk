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

;@Ahk2Exe-AddResource eqbox.ico, 160

OnExit(AppCleanup)
AppCleanup(reason, code) {
    try DestroyPiP()
    try DestroyBorder()
    try DestroyPiPBorder()
    global g_featureTimer
    if g_featureTimer
        try SetTimer(g_featureTimer, 0)
    return 0  ; allow exit
}

g_version        := "2.0"
CFG_FILE         := A_ScriptDir "\eqswitch.cfg"
EQ_TITLE         := "ahk_exe eqgame.exe"
SETTINGS_OPEN    := false
TOOLTIP_MS       := 2000

; Show a tooltip that auto-dismisses after ms (default TOOLTIP_MS)
g_tipClearFn := () => ToolTip()  ; single reusable function object — no allocation per call
ShowTip(msg, ms?) {
    global TOOLTIP_MS, g_tipClearFn
    SetTimer(g_tipClearFn, 0)  ; cancel any pending dismiss
    ToolTip(msg)
    SetTimer(g_tipClearFn, -(ms ?? TOOLTIP_MS))
}
g_multiMonState  := 0
g_launchOneLabel := "⚔  Launch Client"
g_launchAllLabel := "🎮  Launch Both"
g_tripleClickCooldown := 0
g_launchActive   := false
g_launchGrace    := 0          ; tick count — auto-minimize suppressed until this time
g_pmOpen         := false

; =========================================================
; HELPERS
; =========================================================

GetEqDir() {
    global EQ_EXE
    SplitPath(EQ_EXE, , &dir)
    return dir "\"
}

g_visibleCache     := []
g_visibleCacheTick := 0

IsHungWindow(hwnd) {
    return DllCall("IsHungAppWindow", "Ptr", hwnd, "Int")
}

GetVisibleEqWindows() {
    global EQ_TITLE, g_visibleCache, g_visibleCacheTick
    ; Return cached result if fresh (< 200ms) — avoids redundant WinGetList+sort
    ; when both feature timer (250ms) and PiP timer (500ms) call this near-simultaneously
    if (g_visibleCache.Length > 0 && A_TickCount - g_visibleCacheTick < 200)
        return g_visibleCache

    visible := []
    for id in WinGetList(EQ_TITLE) {
        try {
            if WinGetStyle("ahk_id " id) & 0x10000000  ; WS_VISIBLE
                visible.Push(id)
        }
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
    g_visibleCache := visible
    g_visibleCacheTick := A_TickCount
    return visible
}

; =========================================================
; PROCESS MANAGEMENT
; =========================================================

; Apply process priority to all running eqgame.exe instances
ApplyProcessPriority(priority) {
    if (priority = "")
        priority := "Normal"
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
    hProc := 0
    try {
        mask := Integer(affinityStr)
        if (mask <= 0)
            return
        hProc := DllCall("OpenProcess", "UInt", 0x0200, "Int", 0, "UInt", pid, "Ptr")  ; PROCESS_SET_INFORMATION
        if (hProc)
            DllCall("SetProcessAffinityMask", "Ptr", hProc, "UPtr", mask)
    } finally {
        if (hProc)
            DllCall("CloseHandle", "Ptr", hProc)
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
    hProc := 0
    try {
        hProc := DllCall("OpenProcess", "UInt", 0x0400, "Int", 0, "UInt", pid, "Ptr")
        if (!hProc)
            return "Unknown"
        priClass := DllCall("GetPriorityClass", "Ptr", hProc, "UInt")
        switch priClass {
            case 0x20:   return "Normal"
            case 0x4000: return "BelowNormal"
            case 0x8000: return "AboveNormal"
            case 0x80:   return "High"
            case 0x100:  return "Realtime"
            case 0x40:   return "Idle"
            default:     return "Unknown"
        }
    } finally {
        if (hProc)
            DllCall("CloseHandle", "Ptr", hProc)
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
    global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES, MIDCLICK_PIP, RECENT_CHARS
    global EQ_SERVER, LAUNCH_DELAY, LAUNCH_FIX_DELAY, NUM_CLIENTS
    global FIX_MODE, STARTUP_ENABLED, MULTIMON_HOTKEY, MULTIMON_ENABLED
    global LAUNCH_ONE_HOTKEY, LAUNCH_ALL_HOTKEY, TRIPLECLICK_LAUNCH
    global PROCESS_PRIORITY, CPU_AFFINITY
    global FIX_TOP_OFFSET, FIX_BOTTOM_OFFSET
    global PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY, PIP_X, PIP_Y
    global AUTO_MINIMIZE, BORDER_ENABLED, BORDER_COLOR

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
    MIDCLICK_PIP     := ReadKey("MIDCLICK_PIP",      "0")
    RECENT_CHARS     := ReadKey("RECENT_CHARS",      "")
    EQ_SERVER        := ReadKey("EQ_SERVER",         "dalaya")
    LAUNCH_DELAY     := ReadKey("LAUNCH_DELAY",      "3000")
    LAUNCH_FIX_DELAY := ReadKey("LAUNCH_FIX_DELAY",  "15000")
    NUM_CLIENTS      := ReadKey("NUM_CLIENTS",       "2")
    FIX_MODE         := ReadKey("FIX_MODE",          "maximize")
    if (FIX_MODE != "maximize" && FIX_MODE != "multimonitor")
        FIX_MODE := "maximize"
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
    PIP_X              := ReadKey("PIP_X",                "")
    PIP_Y              := ReadKey("PIP_Y",                "")
    AUTO_MINIMIZE      := ReadKey("AUTO_MINIMIZE",        "0")
    BORDER_ENABLED     := ReadKey("BORDER_ENABLED",       "0")
    BORDER_COLOR       := ReadKey("BORDER_COLOR",         "00FF00")
}

SaveConfig() {
    global CFG_FILE, EQ_EXE, EQ_ARGS, EQ_HOTKEY, DBLCLICK_LAUNCH
    global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES, MIDCLICK_PIP, RECENT_CHARS
    global EQ_SERVER, LAUNCH_DELAY, LAUNCH_FIX_DELAY, NUM_CLIENTS
    global FIX_MODE, STARTUP_ENABLED, MULTIMON_HOTKEY, MULTIMON_ENABLED
    global LAUNCH_ONE_HOTKEY, LAUNCH_ALL_HOTKEY, TRIPLECLICK_LAUNCH
    global PROCESS_PRIORITY, CPU_AFFINITY
    global FIX_TOP_OFFSET, FIX_BOTTOM_OFFSET
    global PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY, PIP_X, PIP_Y
    global AUTO_MINIMIZE, BORDER_ENABLED, BORDER_COLOR
    IniWrite(EQ_EXE,           CFG_FILE, "EQSwitch", "EQ_EXE")
    IniWrite(EQ_ARGS,          CFG_FILE, "EQSwitch", "EQ_ARGS")
    IniWrite(EQ_HOTKEY,        CFG_FILE, "EQSwitch", "EQ_HOTKEY")
    IniWrite(DBLCLICK_LAUNCH,  CFG_FILE, "EQSwitch", "DBLCLICK_LAUNCH")
    IniWrite(GINA_PATH,        CFG_FILE, "EQSwitch", "GINA_PATH")
    IniWrite(NOTES_FILE,       CFG_FILE, "EQSwitch", "NOTES_FILE")
    IniWrite(MIDCLICK_NOTES,   CFG_FILE, "EQSwitch", "MIDCLICK_NOTES")
    IniWrite(MIDCLICK_PIP,     CFG_FILE, "EQSwitch", "MIDCLICK_PIP")
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
    IniWrite(PIP_X, CFG_FILE, "EQSwitch", "PIP_X")
    IniWrite(PIP_Y, CFG_FILE, "EQSwitch", "PIP_Y")
    IniWrite(AUTO_MINIMIZE,   CFG_FILE, "EQSwitch", "AUTO_MINIMIZE")
    IniWrite(BORDER_ENABLED,  CFG_FILE, "EQSwitch", "BORDER_ENABLED")
    IniWrite(BORDER_COLOR,    CFG_FILE, "EQSwitch", "BORDER_COLOR")
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
    ShowTip("EQ Switch loaded — right-click the tray icon to get started ⚔", 5000)
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

; Parse recent character names from pipe-separated config string
GetRecentCharList() {
    global RECENT_CHARS
    return (RECENT_CHARS != "") ? StrSplit(RECENT_CHARS, "|") : []
}

; Add a char name to the recent-chars list stored in the cfg
AddRecentChar(charName) {
    global RECENT_CHARS
    existing := GetRecentCharList()
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
else if A_IsCompiled
    try TraySetIcon(A_ScriptFullPath, -160, true)
A_IconTip := "EQ Switch"

A_TrayMenu.Delete()
OnMessage(0x404, TrayClick)
g_dblClickPending := false  ; true while waiting to see if a 3rd click follows
g_dblClickTimer   := ""
TrayClick(wParam, lParam, *) {
    global DBLCLICK_LAUNCH, MIDCLICK_NOTES, MIDCLICK_PIP, TRIPLECLICK_LAUNCH
    global g_tripleClickCooldown, g_dblClickPending, g_dblClickTimer
    now := A_TickCount

    if (lParam = 0x203) {  ; WM_LBUTTONDBLCLK
        ; If triple-click is enabled, delay the double-click action to watch for 3rd click
        if (TRIPLECLICK_LAUNCH = "1" && (now - g_tripleClickCooldown) > 5000) {
            g_dblClickPending := true
            ; Fire double-click action after 400ms if no 3rd click arrives
            g_dblClickTimer := DblClickFire
            SetTimer(g_dblClickTimer, -400)
            return
        }
        ; Triple-click disabled — fire immediately
        if (DBLCLICK_LAUNCH = "1")
            LaunchOne()
        else
            OpenSettings()
    }

    ; 3rd click arrives as WM_LBUTTONUP after the double-click
    if (lParam = 0x202 && g_dblClickPending) {  ; WM_LBUTTONUP
        g_dblClickPending := false
        if g_dblClickTimer
            SetTimer(g_dblClickTimer, 0)
        g_dblClickTimer := ""
        g_tripleClickCooldown := now
        LaunchBoth()
        return
    }

    if (lParam = 0x208) {  ; WM_MBUTTONUP — middle-click
        if (MIDCLICK_PIP = "1") {
            TogglePiP()
            ShowTip(g_pipEnabled ? "📺 PiP ON" : "📺 PiP OFF", 1500)
        } else if (MIDCLICK_NOTES = "1") {
            OpenNotes()
        }
    }
}

DblClickFire(*) {
    global DBLCLICK_LAUNCH, g_dblClickPending, g_dblClickTimer
    g_dblClickPending := false
    g_dblClickTimer := ""
    if (DBLCLICK_LAUNCH = "1")
        LaunchOne()
    else
        OpenSettings()
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
A_TrayMenu.Add("📺  Picture-in-Picture", (*) => TogglePiP())
A_TrayMenu.Add("⚡  Process Manager",   (*) => OpenProcessManager())
A_TrayMenu.Add()

; Compact submenus for file/link items
g_openMenu := Menu()
g_openMenu.Add("📜  Log File",     (*) => OpenLogFile())
g_openMenu.Add("📄  eqclient.ini", (*) => OpenEqClientIni())
g_openMenu.Add("🎯  Gina",         (*) => OpenGina())
g_openMenu.Add("📝  Notes",        (*) => OpenNotes())
A_TrayMenu.Add("📂  Open",    g_openMenu)

g_linksMenu := Menu()
g_linksMenu.Add("📖  Dalaya Wiki",    (*) => Run("https://wiki.dalaya.org/"))
g_linksMenu.Add("🗡  Shards Wiki",    (*) => Run("https://wiki.shardsofdalaya.com/wiki/Main_Page"))
g_linksMenu.Add("🏆  Dalaya Fomelo",  (*) => Run("https://dalaya.org/fomelo/"))
A_TrayMenu.Add("🌐  Links",   g_linksMenu)
A_TrayMenu.Add()

A_TrayMenu.Add("⚙  Settings",          (*) => OpenSettings())
A_TrayMenu.Add()
A_TrayMenu.Add("✖  Exit",              (*) => ExitApp())

; Update menu labels with hotkey text, then apply bold
UpdateTrayMenuLabels()
SetMenuItemsBold(A_TrayMenu.Handle, ["Launch Client", "Launch Both"])


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

    if (FIX_MODE = "multimonitor") {
        ; Distribute windows across monitors (one per monitor, maximized)
        monCount := MonitorGetCount()
        count := winList.Length
        Loop count {
            id := winList[A_Index]
            if IsHungWindow(id)
                continue
            mon := Mod(A_Index - 1, monCount) + 1
            try {
                MonitorGetWorkArea(mon, &mLeft, &mTop, &mRight, &mBottom)
                adjTop    := mTop + topOff
                adjHeight := (mBottom + botOff) - adjTop
                WinRestore("ahk_id " id)
                WinMove(mLeft, adjTop, mRight - mLeft, adjHeight, "ahk_id " id)
            }
        }
    } else {
        ; Default: maximize
        Loop winList.Length {
            id := winList[A_Index]
            if IsHungWindow(id)
                continue
            try WinMaximize("ahk_id " id)
        }
    }
}

; =========================================================
; SWAP / MULTI-MONITOR TOGGLE
; =========================================================
SwapWindows(*) {
    global TOOLTIP_MS
    ; Invalidate cache to get fresh window list
    global g_visibleCacheTick
    g_visibleCacheTick := 0
    visible := GetVisibleEqWindows()
    if (visible.Length < 2) {
        ShowTip("Need at least 2 EQ windows to swap!")
        return
    }
    ; Read current positions — skip hung windows
    positions := []
    for id in visible {
        if IsHungWindow(id) {
            ShowTip("⚠ An EQ window is not responding — swap skipped")
            return
        }
        try {
            ; Get actual window placement (handles maximized windows correctly)
            WinGetPos(&x, &y, &w, &h, "ahk_id " id)
            positions.Push({x: x, y: y, w: w, h: h})
        } catch {
            ShowTip("⚠ A window closed during swap — try again")
            return
        }
    }
    ; Restore all windows first (un-maximize) before moving
    for id in visible {
        try {
            if (WinGetMinMax("ahk_id " id) = 1)
                WinRestore("ahk_id " id)
        }
    }
    Sleep(50)  ; brief pause for WM to process restore
    ; Rotate each window to the next window's position
    count := visible.Length
    Loop count {
        nextPos := positions[Mod(A_Index, count) + 1]
        id := visible[A_Index]
        try WinMove(nextPos.x, nextPos.y, nextPos.w, nextPos.h, "ahk_id " id)
    }
    ShowTip("🔄 Windows swapped!")
}

ToggleMultiMon(*) {
    global g_multiMonState, TOOLTIP_MS, FIX_TOP_OFFSET, FIX_BOTTOM_OFFSET
    ; Invalidate cache to get fresh window list
    global g_visibleCacheTick
    g_visibleCacheTick := 0
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
    recentList := GetRecentCharList()

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
        if !RegExMatch(charName, "^[A-Za-z]+$") {
            statusTxt.Value := "Invalid name — letters only"
            return
        }
        logPath := eqDir "Logs\eqlog_" charName "_" EQ_SERVER ".txt"
        if !FileExist(logPath) {
            statusTxt.Value := "Log not found for: " charName
            return
        }
        AddRecentChar(charName)
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
    global NUM_CLIENTS, FIX_MODE
    global LAUNCH_ONE_HOTKEY, LAUNCH_ALL_HOTKEY
    global DBLCLICK_LAUNCH, STARTUP_ENABLED, MIDCLICK_NOTES, MIDCLICK_PIP, TRIPLECLICK_LAUNCH
    g.AddText("xm y+10 w440 cNavy", "🎮  Launch & Tray Options")
    g.AddText("xm y+4 w440 h1 0x10")

    g.AddText("xm y+6 Section", "Clients:")
    ctl["clientsEdit"] := g.AddEdit("x+4 yp-2 w40 Number", NUM_CLIENTS)
    g.AddUpDown("Range1-8", NUM_CLIENTS)
    g.AddText("x+10 yp+2", "Window mode:")
    ctl["fixModes"] := ["maximize", "multimonitor"]
    ctl["fixModeCombo"] := g.AddDropDownList("x+4 yp-2 w130", ctl["fixModes"])
    for i, mode in ctl["fixModes"] {
        if (mode = FIX_MODE)
            ctl["fixModeCombo"].Choose(i)
    }
    if (ctl["fixModeCombo"].Value = 0)
        ctl["fixModeCombo"].Choose(1)

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
    ctl["midClickPipChk"] := g.AddCheckbox("x+20 yp", "Tray middle-click toggles PiP")
    ctl["midClickPipChk"].Value := (MIDCLICK_PIP = "1") ? 1 : 0
    ; Make notes and pip middle-click mutually exclusive
    ctl["midClickChk"].OnEvent("Click", (*) => (ctl["midClickPipChk"].Value := 0))
    ctl["midClickPipChk"].OnEvent("Click", (*) => (ctl["midClickChk"].Value := 0))
    ctl["tripleClickChk"] := g.AddCheckbox("xm y+4", "Tray triple-click launches all clients")
    ctl["tripleClickChk"].Value := (TRIPLECLICK_LAUNCH = "1") ? 1 : 0

    btnDesktop := g.AddButton("xm y+6 w130 h24", "🖥 Desktop Shortcut")
    btnDesktop.OnEvent("Click", CreateDesktopShortcut)
    btnDesktop.ToolTip := "Create an EQSwitch shortcut on your Desktop"
    btnTray := g.AddButton("x+8 yp w130 h24", "🔧 Tray Icon Settings")
    btnTray.OnEvent("Click", OpenTraySettings)
    btnTray.ToolTip := "Open Windows settings to pin EQSwitch to the system tray"

    OpenTraySettings(*) {
        Run("ms-settings:taskbar")
        ShowTip("Look for 'Other system tray icons' and enable EQ Switch", 5000)
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
    g.AddText("xm y+10 w440 cNavy", "⚡  Process Settings")
    g.AddText("xm y+4 w440 h1 0x10")
    g.AddButton("xm y+6 w160 h24", "⚡ Process Manager...").OnEvent("Click", (*) => OpenProcessManager())
    g.AddText("x+10 yp+4 cGray", "Priority && CPU affinity")
    btnVideoMode := g.AddButton("x+20 yp-4 w130 h24", "🖥 Video Settings...")
    btnVideoMode.OnEvent("Click", (*) => OpenVideoModeEditor())
    btnVideoMode.ToolTip := "Edit eqclient.ini resolution/window settings"
}

BuildMultiMonSection(g, ctl) {
    global MULTIMON_ENABLED, MULTIMON_HOTKEY
    g.AddText("xm y+10 w440 cNavy", "🖥  Multi-Monitor")
    g.AddText("xm y+4 w440 h1 0x10")
    ctl["multimonEnabled"] := g.AddCheckbox("xm y+6", "Enable multi-monitor toggle")
    ctl["multimonEnabled"].Value := (MULTIMON_ENABLED = "1") ? 1 : 0
    ctl["multimonEnabled"].OnEvent("Click", ToggleMultimonField)
    g.AddText("xm y+4", "Hotkey:")
    ctl["multimonHkCtrl"] := g.AddHotkey("x+6 yp-2 w120", MULTIMON_HOTKEY)
    ctl["multimonHkCtrl"].Enabled := (MULTIMON_ENABLED = "1") ? true : false
    g.AddText("x+8 yp+4 cGray", "← click the box and press your key")

    ToggleMultimonField(*) {
        ctl["multimonHkCtrl"].Enabled := ctl["multimonEnabled"].Value ? true : false
    }
}

BuildPiPSection(g, ctl) {
    global PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY
    g.AddText("xm y+10 w440 cNavy", "📺  Picture-in-Picture")
    g.AddText("xm y+4 w440 h1 0x10")

    ; Size presets
    g.AddText("xm y+6", "Size:")
    pipPresets := ["Small (320×180)", "Medium (400×225)", "Large (480×270)", "XL (600×338)", "Custom"]
    pipPresetCombo := g.AddDropDownList("x+4 yp-2 w140", pipPresets)
    ; Select current preset or "Custom"
    currentMatch := 0
    presetSizes := [[320,180], [400,225], [480,270], [600,338]]
    for i, sz in presetSizes {
        if (Integer(PIP_WIDTH) = sz[1] && Integer(PIP_HEIGHT) = sz[2])
            currentMatch := i
    }
    pipPresetCombo.Choose(currentMatch > 0 ? currentMatch : 5)

    g.AddText("x+10 yp+2", "W:")
    ctl["pipWidthEdit"] := g.AddEdit("x+2 yp-2 w45 Number", PIP_WIDTH)
    g.AddUpDown("Range160-800", Integer(PIP_WIDTH))
    g.AddText("x+4 yp+2", "H:")
    ctl["pipHeightEdit"] := g.AddEdit("x+2 yp-2 w45 Number", PIP_HEIGHT)
    g.AddUpDown("Range90-450", Integer(PIP_HEIGHT))

    g.AddText("xm y+6", "Opacity:")
    ctl["pipOpacityEdit"] := g.AddEdit("x+4 yp-2 w45 Number", PIP_OPACITY)
    g.AddUpDown("Range50-255", Integer(PIP_OPACITY))
    g.AddText("x+6 yp+2 cGray", "(50-255)")

    ; Preset selection auto-fills width/height
    pipPresetCombo.OnEvent("Change", OnPipPreset)
    OnPipPreset(*) {
        idx := pipPresetCombo.Value
        if (idx >= 1 && idx <= presetSizes.Length) {
            ctl["pipWidthEdit"].Value := presetSizes[idx][1]
            ctl["pipHeightEdit"].Value := presetSizes[idx][2]
        }
    }
}

BuildExtrasSection(g, ctl) {
    global AUTO_MINIMIZE, BORDER_ENABLED, BORDER_COLOR
    g.AddText("xm y+10 w440 cNavy", "✨  Window Extras")
    g.AddText("xm y+4 w440 h1 0x10")

    ctl["autoMinimizeChk"] := g.AddCheckbox("xm y+6", "Auto-minimize inactive EQ windows on switch")
    ctl["autoMinimizeChk"].Value := (AUTO_MINIMIZE = "1") ? 1 : 0

    ctl["borderEnabledChk"] := g.AddCheckbox("xm y+4", "Highlight active EQ window with colored border")
    ctl["borderEnabledChk"].Value := (BORDER_ENABLED = "1") ? 1 : 0
    ctl["borderEnabledChk"].OnEvent("Click", ToggleBorderFields)

    g.AddText("xm y+4", "Border color:")
    ctl["borderColorEdit"] := g.AddEdit("x+4 yp-2 w70", BORDER_COLOR)
    ctl["borderColorEdit"].Enabled := (BORDER_ENABLED = "1") ? true : false
    g.AddText("x+6 yp+2 cGray", "Hex RGB (e.g. 00FF00=green, FF0000=red)")

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
    g.SetFont("s8", "Segoe UI")
    g.AddText("xm y+10 w440 cNavy", "📋  Character Backup")
    g.AddText("xm y+3 w440 h1 0x10")
    ; Character | ✕ | Backup | Restore — Server is hidden (uses EQ_SERVER from config)
    g.AddText("xm y+4", "Char:")
    recentList := GetRecentCharList()
    charCombo  := g.AddComboBox("x+2 yp-2 w120 h20", recentList)
    ctl["charCombo"] := charCombo
    if (recentList.Length > 0)
        charCombo.Choose(1)
    g.AddButton("x+2 yp w18 h20", "✕").OnEvent("Click", (*) => DoRemoveChar(charCombo))
    btnBackup := g.AddButton("x+3 yp w52 h20", "Backup")
    btnBackup.OnEvent("Click", (*) => DoBackup(charCombo.Text))
    btnRestore := g.AddButton("x+2 yp w52 h20", "Restore")
    btnRestore.OnEvent("Click", (*) => DoRestore(charCombo.Text))
    g.AddText("x+6 yp+2 cGray", "Copies UI/keybind files to/from Desktop")
    g.SetFont("s9", "Segoe UI")

    ; ---- Character section helpers (closures) ----

    DoRemoveChar(combo) {
        global RECENT_CHARS
        charName := combo.Text
        if (charName = "")
            return
        existing := GetRecentCharList()
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
            ShowTip("⚠ Please enter or select a character name", 5000)
            return
        }
        if !RegExMatch(charName, "^[A-Za-z0-9_-]+$") {
            ShowTip("⚠ Invalid character name — letters, numbers, hyphens, and underscores only", 5000)
            return
        }
        eqDir   := GetEqDir()
        desktop := EnvGet("USERPROFILE") "\Desktop\"
        found   := 0
        errors  := ""

        ; Search for character files with any server name pattern
        ; Try exact server match first, then wildcard search
        patterns := []
        patterns.Push({src: eqDir "UI_" charName "_" EQ_SERVER ".ini", name: "UI_" charName "_" EQ_SERVER ".ini"})
        patterns.Push({src: eqDir charName "_" EQ_SERVER ".ini", name: charName "_" EQ_SERVER ".ini"})

        ; Also search for files matching the character name with any server
        Loop Files eqDir "UI_" charName "_*.ini" {
            alreadyListed := false
            for p in patterns {
                if (p.src = A_LoopFileFullPath) {
                    alreadyListed := true
                    break
                }
            }
            if !alreadyListed
                patterns.Push({src: A_LoopFileFullPath, name: A_LoopFileName})
        }
        Loop Files eqDir charName "_*.ini" {
            ; Skip UI_ prefixed files (already handled above)
            if InStr(A_LoopFileName, "UI_")
                continue
            alreadyListed := false
            for p in patterns {
                if (p.src = A_LoopFileFullPath) {
                    alreadyListed := true
                    break
                }
            }
            if !alreadyListed
                patterns.Push({src: A_LoopFileFullPath, name: A_LoopFileName})
        }

        for p in patterns {
            if FileExist(p.src) {
                try {
                    FileCopy(p.src, desktop p.name, 1)
                    found++
                } catch as err {
                    errors .= p.name ": " err.Message "`n"
                }
            }
        }

        if (errors != "") {
            ShowTip("⚠ Some files failed to copy", 5000)
            return
        }
        if (found = 0) {
            ShowTip("⚠ No character files found for '" charName "'", 5000)
            return
        }
        ; Save to recent list and refresh the combobox
        AddRecentChar(charName)
        charCombo.Delete()
        fresh := GetRecentCharList()
        charCombo.Add(fresh)
        charCombo.Text := charName
        ShowTip(found " file(s) backed up to Desktop ✓")
    }

    DoRestore(charName) {
        global EQ_SERVER
        if (charName = "") {
            ShowTip("⚠ Please enter or select a character name", 5000)
            return
        }
        if !RegExMatch(charName, "^[A-Za-z0-9_-]+$") {
            ShowTip("⚠ Invalid character name — letters, numbers, hyphens, and underscores only", 5000)
            return
        }
        eqDir   := GetEqDir()
        desktop := EnvGet("USERPROFILE") "\Desktop\"

        ; Find all matching backup files on Desktop (any server name)
        restoreFiles := []
        Loop Files desktop "UI_" charName "_*.ini" {
            restoreFiles.Push({src: A_LoopFileFullPath, name: A_LoopFileName})
        }
        Loop Files desktop charName "_*.ini" {
            if InStr(A_LoopFileName, "UI_")
                continue
            restoreFiles.Push({src: A_LoopFileFullPath, name: A_LoopFileName})
        }

        if (restoreFiles.Length = 0) {
            ShowTip("⚠ No backup files found on Desktop for '" charName "'", 5000)
            return
        }
        result := MsgBox(
            "Restore " restoreFiles.Length " character file(s) for '" charName "' from Desktop?`n`n" .
            "This will OVERWRITE current files in your EQ folder.",
            "EQ Switch — Restore", "YesNo Icon!")
        if (result != "Yes")
            return
        found  := 0
        errors := ""
        for f in restoreFiles {
            try {
                FileCopy(f.src, eqDir f.name, 1)
                found++
            } catch as err {
                errors .= f.name ": " err.Message "`n"
            }
        }
        if (errors != "")
            ShowTip("⚠ Some files failed to restore", 5000)
        else
            ShowTip(found " file(s) restored from Desktop ✓")
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
        . "  • multimonitor — one window per monitor, maximized`n`n"
        . "Launch One / Launch All hotkeys — global shortcuts to launch clients from anywhere.`n`n"
        . "Tray double-click — launches a single client.`n"
        . "Tray triple-click — launches all clients (3 rapid clicks, 5s cooldown).`n"
        . "Tray middle-click — opens your notes file.`n"
        . "Desktop Shortcut — creates an EQSwitch shortcut on your Desktop.`n"
        . "Tray Icon Settings — opens Windows settings to pin EQSwitch to the taskbar tray.")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "⚡  Process Settings")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "Process Manager — shows all running EQ processes with their PIDs, priorities, and CPU affinity. "
        . "Configure which CPU cores EQ can use (useful since EQ defaults to a single core). "
        . "'Force Apply to All' pushes current settings to all running clients immediately.`n`n"
        . "Video Settings — edits eqclient.ini resolution and window offset settings. "
        . "WindowedHeight=1009 sits above the taskbar. Set offsets to -8 for borderless-windowed mode.`n`n"
        . "Note: EQ's own CPUAffinity1-6 in eqclient.ini are per-box core preferences (max 6). "
        . "These are separate from the Windows-level affinity mask set here.")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "📺  Picture-in-Picture")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "Shows a live DWM thumbnail of inactive EQ windows as an overlay. "
        . "Ctrl+drag to reposition. Position is saved between toggles.`n`n"
        . "Size presets: Small (320×180), Medium (400×225), Large (480×270), XL (600×338), or Custom. "
        . "Changes take effect immediately if PiP is active.")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "🖥  Multi-Monitor")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "A global hotkey (works even outside EQ) that cycles through multi-monitor layouts. "
        . "Hotkey uses AHK syntax (e.g. >!m = RAlt+M, ^F12 = Ctrl+F12). Uncheck to disable.")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "✨  Window Extras")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "Auto-minimize — minimizes inactive EQ windows when you switch to another. "
        . "Suppressed for 30s after launch to let clients load.`n`n"
        . "Border highlight — draws a colored border around the active EQ window "
        . "(and around the PiP overlay). Color is hex RGB (e.g. 00FF00=green).")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "📋  Character Config && Backup")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "Backs up character UI/keybind files to and from your Desktop.`n"
        . "Server — your EQ server name (for file paths).`n"
        . "Char — type or pick a recent character name (saved on Apply/Save).`n"
        . "Backup — copies config files to Desktop.`n"
        . "Restore — copies them back (overwrites existing files).")

    h.SetFont("s10 Bold", "Segoe UI")
    h.AddText("w420 y+10 cNavy", "🎯  Gina / 📝  Notes")
    h.SetFont("s9", "Segoe UI")
    h.AddText("w420 y+4",
        "Gina — path to Gina.exe. Notes — a .txt file for EQ notes (created on first use).")

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

    ; ── Save / Apply / Close / Help ────────────────────────
    g.AddText("xm y+8 w440 h1 0x10")
    g.AddButton("xm y+6 w80 h28 Default", "Save").OnEvent("Click", SaveAndClose)
    g.AddButton("x+8 yp w80 h28", "Apply").OnEvent("Click", (*) => ApplySettings())
    g.AddButton("x+8 yp w80 h28", "Close").OnEvent("Click", (*) => (SETTINGS_OPEN := false, g.Destroy()))
    g.AddButton("x+8 yp w80 h28", "❓ Help").OnEvent("Click", (*) => ShowSettingsHelp(g.Hwnd))

    g.Show("AutoSize")

    ; ---- ApplySettings / SaveAndClose (reads from ctl Map) ----

    SaveAndClose(*) {
        ApplySettings()
        SETTINGS_OPEN := false
        g.Destroy()
    }

    ApplySettings(*) {
        global EQ_EXE, EQ_ARGS, EQ_HOTKEY, DBLCLICK_LAUNCH
        global GINA_PATH, NOTES_FILE, MIDCLICK_NOTES, MIDCLICK_PIP
        global EQ_SERVER, NUM_CLIENTS, FIX_MODE, STARTUP_ENABLED
        global MULTIMON_HOTKEY, MULTIMON_ENABLED, TOOLTIP_MS
        global LAUNCH_ONE_HOTKEY, LAUNCH_ALL_HOTKEY, TRIPLECLICK_LAUNCH
        global FIX_TOP_OFFSET, FIX_BOTTOM_OFFSET
        global PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY
        global AUTO_MINIMIZE, BORDER_ENABLED, BORDER_COLOR

        newHotkey := ctl["hotkeyCtrl"].Value
        newMultimonHk := ctl["multimonHkCtrl"].Value
        newLaunchOneHk := ctl["launchOneHkCtrl"].Value
        newLaunchAllHk := ctl["launchAllHkCtrl"].Value

        ; Save old hotkeys for rollback on bind failure
        oldEqHotkey := EQ_HOTKEY
        oldMultimonHk := MULTIMON_HOTKEY
        oldMultimonEnabled := MULTIMON_ENABLED
        oldLaunchOneHk := LAUNCH_ONE_HOTKEY
        oldLaunchAllHk := LAUNCH_ALL_HOTKEY

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
            ShowTip("⚠ EQ exe path doesn't exist — Launch won't work until fixed", 5000)
        if (ctl["ginaEdit"].Value != "" && !FileExist(ctl["ginaEdit"].Value))
            ShowTip("⚠ Gina path doesn't exist — Open Gina won't work until fixed", 5000)
        if (ctl["notesEdit"].Value != "" && !FileExist(ctl["notesEdit"].Value))
            ShowTip("⚠ Notes file doesn't exist — will be created on first use", 5000)

        EQ_EXE          := ctl["exeEdit"].Value
        EQ_ARGS         := ctl["argsEdit"].Value
        DBLCLICK_LAUNCH := ctl["dblClickChk"].Value ? "1" : "0"
        GINA_PATH       := ctl["ginaEdit"].Value
        NOTES_FILE      := ctl["notesEdit"].Value
        MIDCLICK_NOTES  := ctl["midClickChk"].Value ? "1" : "0"
        MIDCLICK_PIP    := ctl["midClickPipChk"].Value ? "1" : "0"
        TRIPLECLICK_LAUNCH := ctl["tripleClickChk"].Value ? "1" : "0"
        ; EQ_SERVER stays as loaded from config (no GUI field)
        ; Save character name to recent list if user typed one
        if (ctl.Has("charCombo") && ctl["charCombo"].Text != "") {
            charText := ctl["charCombo"].Text
            if RegExMatch(charText, "^[A-Za-z]+$")
                AddRecentChar(charText)
        }
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
        PIP_WIDTH        := ctl["pipWidthEdit"].Value
        PIP_HEIGHT       := ctl["pipHeightEdit"].Value
        PIP_OPACITY      := ctl["pipOpacityEdit"].Value
        AUTO_MINIMIZE   := ctl["autoMinimizeChk"].Value ? "1" : "0"
        BORDER_ENABLED  := ctl["borderEnabledChk"].Value ? "1" : "0"
        borderInput     := ctl["borderColorEdit"].Value
        BORDER_COLOR    := RegExMatch(borderInput, "^[0-9A-Fa-f]{6}$") ? borderInput : "00FF00"
        STARTUP_ENABLED := ctl["startupChk"].Value ? "1" : "0"

        ; Handle hotkey — skip binding if empty; rollback to old key on failure
        if (newHotkey != "") {
            EQ_HOTKEY := newHotkey
            if !BindHotkey(EQ_HOTKEY) {
                ShowTip("⚠ Switch hotkey '" EQ_HOTKEY "' couldn't be bound — reverting", 5000)
                EQ_HOTKEY := oldEqHotkey
                BindHotkey(EQ_HOTKEY)
            }
        } else {
            EQ_HOTKEY := ""
        }

        ; Handle multimon hotkey
        MULTIMON_ENABLED := ctl["multimonEnabled"].Value ? "1" : "0"
        MULTIMON_HOTKEY := newMultimonHk
        if (MULTIMON_ENABLED = "1" && newMultimonHk != "") {
            if !BindMultiMonHotkey(MULTIMON_HOTKEY) {
                ShowTip("⚠ Multi-monitor hotkey '" MULTIMON_HOTKEY "' couldn't be bound — reverting", 5000)
                MULTIMON_HOTKEY := oldMultimonHk
                MULTIMON_ENABLED := oldMultimonEnabled
                BindMultiMonHotkey(MULTIMON_HOTKEY)
            }
        }

        ; Handle launch hotkeys
        LAUNCH_ONE_HOTKEY := newLaunchOneHk
        if (newLaunchOneHk != "") {
            if !BindLaunchHotkey(LAUNCH_ONE_HOTKEY, "one") {
                ShowTip("⚠ Launch One hotkey '" LAUNCH_ONE_HOTKEY "' couldn't be bound — reverting", 5000)
                LAUNCH_ONE_HOTKEY := oldLaunchOneHk
                BindLaunchHotkey(LAUNCH_ONE_HOTKEY, "one")
            }
        }
        LAUNCH_ALL_HOTKEY := newLaunchAllHk
        if (newLaunchAllHk != "") {
            if !BindLaunchHotkey(LAUNCH_ALL_HOTKEY, "all") {
                ShowTip("⚠ Launch All hotkey '" LAUNCH_ALL_HOTKEY "' couldn't be bound — reverting", 5000)
                LAUNCH_ALL_HOTKEY := oldLaunchAllHk
                BindLaunchHotkey(LAUNCH_ALL_HOTKEY, "all")
            }
        }

        SaveConfig()
        UpdateFeatureTimer()
        ; Rebuild PiP if active and size changed
        if (g_pipEnabled) {
            DestroyPiP()
            CreatePiP()
        }
        ; Clean up border if disabled
        if (BORDER_ENABLED = "0")
            DestroyBorder()
        UpdateTrayTip()
        UpdateTrayMenuLabels()

        ; Manage startup shortcut — use shell Startup folder directly
        startupDir := EnvGet("APPDATA") "\Microsoft\Windows\Start Menu\Programs\Startup"
        shortcutPath := startupDir "\EQSwitch.lnk"
        if (STARTUP_ENABLED = "1") {
            try {
                ico := A_ScriptDir "\eqbox.ico"
                if FileExist(ico)
                    FileCreateShortcut(A_ScriptFullPath, shortcutPath, A_ScriptDir, , "EQ Switch", ico)
                else
                    FileCreateShortcut(A_ScriptFullPath, shortcutPath, A_ScriptDir)
            }
        } else {
            if FileExist(shortcutPath)
                try FileDelete(shortcutPath)
        }

        ShowTip("Settings applied!")
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
g_pipAltWindows := []
; PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY, PIP_X, PIP_Y are loaded from config
; Ctrl+drag to reposition PiP; position saved on destroy
OnMessage(0x0084, PiPHitTest)
PiPHitTest(wParam, lParam, msg, hwnd) {
    global g_pipGui
    if (g_pipGui && hwnd = g_pipGui.Hwnd) {
        if GetKeyState("Ctrl")
            return 2   ; HTCAPTION — draggable
        return -1      ; HTTRANSPARENT — click-through
    }
}

TogglePiP(*) {
    global g_pipEnabled
    if g_pipEnabled
        DestroyPiP()
    else
        CreatePiP()
}

CreatePiP(*) {
    global g_pipEnabled, g_pipGui, g_pipThumbnails, g_pipTimer
    global PIP_WIDTH, PIP_HEIGHT, PIP_OPACITY, PIP_X, PIP_Y, TOOLTIP_MS

    if g_pipEnabled
        DestroyPiP()

    visible := GetVisibleEqWindows()
    if (visible.Length < 2) {
        ShowTip("⚠ Need at least 2 EQ windows for PiP!")
        return
    }

    ; Get the active EQ window — if focus isn't on an EQ window, use the first one
    activeID := 0
    try activeID := WinGetID("A")
    isEqActive := false
    for id in visible {
        if (id = activeID) {
            isEqActive := true
            break
        }
    }
    if !isEqActive
        activeID := visible[1]

    ; Find non-active EQ windows to show as thumbnails
    altWindows := []
    for id in visible {
        if (id != activeID)
            altWindows.Push(id)
    }
    if (altWindows.Length = 0)
        altWindows.Push(visible[2])  ; fallback: show second window

    ; Create the overlay GUI — borderless, always on top
    ; Click-through handled by WM_NCHITTEST (PiPHitTest); Ctrl+drag to move
    pipG := Gui("+AlwaysOnTop -Caption +ToolWindow")
    pipG.BackColor := "000000"

    ; Calculate total size needed
    totalH := altWindows.Length * PIP_HEIGHT + (altWindows.Length - 1) * 4
    totalW := PIP_WIDTH

    ; Use saved position if available, otherwise bottom-right corner
    if (PIP_X != "" && PIP_Y != "") {
        try {
            posX := Integer(PIP_X)
            posY := Integer(PIP_Y)
        } catch {
            posX := ""
        }
    } else {
        posX := ""
    }
    if (posX = "") {
        try MonitorGetWorkArea(1, &mLeft, &mTop, &mRight, &mBottom)
        catch {
            mRight := A_ScreenWidth
            mBottom := A_ScreenHeight
        }
        posX := mRight - totalW - 10
        posY := mBottom - totalH - 10
    }

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

            hrUpdate := DllCall("dwmapi\DwmUpdateThumbnailProperties", "Ptr", hThumb, "Ptr", props, "Int")
            if (hrUpdate = 0)
                g_pipThumbnails.Push(hThumb)
            else
                DllCall("dwmapi\DwmUnregisterThumbnail", "Ptr", hThumb)
        }
        yPos += PIP_HEIGHT + 4
    }

    g_pipAltWindows := altWindows
    g_pipGui := pipG
    g_pipEnabled := true
    g_pipLastActive := activeID

    ; Set up a timer to refresh when active window changes
    g_pipTimer := RefreshPiP
    SetTimer(g_pipTimer, 500)
}

RefreshPiP(*) {
    global g_pipEnabled, g_pipGui, g_pipThumbnails, g_pipLastActive
    global PIP_WIDTH, PIP_HEIGHT
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

    ; Position is user-controlled (Ctrl+drag) — no auto-reposition

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
            hrUpdate := DllCall("dwmapi\DwmUpdateThumbnailProperties", "Ptr", hThumb, "Ptr", props, "Int")
            if (hrUpdate = 0)
                g_pipThumbnails.Push(hThumb)
            else
                DllCall("dwmapi\DwmUnregisterThumbnail", "Ptr", hThumb)
        }
        yPos += PIP_HEIGHT + 4
    }

    g_pipAltWindows := altWindows
    g_pipLastActive := activeID
}

DestroyPiP(*) {
    global g_pipEnabled, g_pipGui, g_pipThumbnails, g_pipTimer, g_pipAltWindows
    global PIP_X, PIP_Y

    ; Save current position so it persists across toggles
    if g_pipGui {
        try {
            WinGetPos(&px, &py, , , "ahk_id " g_pipGui.Hwnd)
            PIP_X := String(px)
            PIP_Y := String(py)
            SaveConfig()
        }
    }

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

    g_pipAltWindows := []
    g_pipEnabled := false
    try DestroyPiPBorder()
}

; =========================================================
; WINDOW FEATURES ENGINE
; =========================================================
; Unified timer for: auto-minimize (P2-05), border highlight (P2-04)
g_featureTimer      := ""
g_featureLastActive := 0
g_borderGuis        := []    ; [top, bottom, left, right] bar GUIs
g_borderTarget      := 0     ; hwnd currently highlighted

; Start/stop the feature timer based on which features are enabled
UpdateFeatureTimer() {
    global AUTO_MINIMIZE, BORDER_ENABLED, g_featureTimer
    needTimer := (AUTO_MINIMIZE = "1" || BORDER_ENABLED = "1")
    if (needTimer && !g_featureTimer) {
        g_featureTimer := FeatureRefresh
        SetTimer(g_featureTimer, 2000)
    } else if (!needTimer && g_featureTimer) {
        SetTimer(g_featureTimer, 0)
        g_featureTimer := ""
        DestroyBorder()
    }
}

FeatureRefresh(*) {
    global AUTO_MINIMIZE, BORDER_ENABLED
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

    ; Auto-minimize only needs to act on window switch, not every tick
    if (isEq && activeID != g_featureLastActive) {
        ; P2-05: Auto-minimize — minimize inactive EQ windows on switch
        ; Skip during launch grace period (30s after launch to let clients load)
        if (AUTO_MINIMIZE = "1" && A_TickCount > g_launchGrace) {
            for id in visible {
                if (id != activeID && !IsHungWindow(id)) {
                    try {
                        if (WinGetMinMax("ahk_id " id) != -1)
                            WinMinimize("ahk_id " id)
                    }
                }
            }
        }
    }

    ; P2-04: Active window highlight border (also frames PiP overlay)
    if (BORDER_ENABLED = "1") {
        if (isEq && visible.Length >= 2) {
            UpdateBorder(activeID)
            ; Also highlight PiP overlay if active
            global g_pipEnabled, g_pipGui
            if (g_pipEnabled && g_pipGui)
                try UpdatePiPBorder()
        } else {
            HideBorder()
            HidePiPBorder()
        }
    }

    if isEq
        g_featureLastActive := activeID
}

; ── Border Highlight ──────────────────────────────────
CreateBorder() {
    global g_borderGuis, BORDER_COLOR
    if (g_borderGuis.Length > 0)
        return  ; already created

    ; Validate color — fallback to green if invalid
    color := RegExMatch(BORDER_COLOR, "^[0-9A-Fa-f]{6}$") ? BORDER_COLOR : "00FF00"
    Loop 4 {
        bar := Gui("+AlwaysOnTop -Caption +ToolWindow +E0x20")  ; click-through
        bar.BackColor := color
        g_borderGuis.Push(bar)
    }
}

UpdateBorder(targetHwnd) {
    global g_borderGuis, g_borderTarget, BORDER_COLOR
    static BW := 3  ; border width in pixels

    if (g_borderGuis.Length = 0)
        CreateBorder()

    ; Skip hung windows — WinGetPos can block on unresponsive processes
    if IsHungWindow(targetHwnd) {
        HideBorder()
        return
    }

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

; ── PiP Border Highlight ──────────────────────────────
g_pipBorderGuis := []

UpdatePiPBorder() {
    global g_pipBorderGuis, g_pipGui, BORDER_COLOR
    static BW := 2

    if (!g_pipGui)
        return

    try WinGetPos(&px, &py, &pw, &ph, "ahk_id " g_pipGui.Hwnd)
    catch
        return

    ; Create border bars if needed
    if (g_pipBorderGuis.Length = 0) {
        color := RegExMatch(BORDER_COLOR, "^[0-9A-Fa-f]{6}$") ? BORDER_COLOR : "00FF00"
        Loop 4 {
            bar := Gui("+AlwaysOnTop -Caption +ToolWindow +E0x20")
            bar.BackColor := color
            g_pipBorderGuis.Push(bar)
        }
    }

    ; Update color if changed
    if (g_pipBorderGuis[1].BackColor != BORDER_COLOR) {
        for bar in g_pipBorderGuis
            bar.BackColor := BORDER_COLOR
    }

    g_pipBorderGuis[1].Show("x" (px - BW) " y" (py - BW) " w" (pw + 2*BW) " h" BW " NoActivate")
    g_pipBorderGuis[2].Show("x" (px - BW) " y" (py + ph) " w" (pw + 2*BW) " h" BW " NoActivate")
    g_pipBorderGuis[3].Show("x" (px - BW) " y" py " w" BW " h" ph " NoActivate")
    g_pipBorderGuis[4].Show("x" (px + pw) " y" py " w" BW " h" ph " NoActivate")
}

HidePiPBorder() {
    global g_pipBorderGuis
    for bar in g_pipBorderGuis
        try bar.Show("Hide")
}

DestroyPiPBorder() {
    global g_pipBorderGuis
    for bar in g_pipBorderGuis
        try bar.Destroy()
    g_pipBorderGuis := []
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

    try {

    coreCount := GetCoreCount()
    pm := Gui("+AlwaysOnTop", "⚡ EQ Switch — Process Manager")
    pm.SetFont("s8", "Segoe UI")
    pm.MarginX := 10
    pm.MarginY := 6

    ; ── Running Processes ──
    pm.SetFont("s9 Bold", "Segoe UI")
    pm.AddText("w420 c0xAA3300", "⚡  Running EQ Processes")
    pm.SetFont("s8", "Segoe UI")
    pm.AddText("xm y+3 w420 h1 0x10")

    processList := pm.AddListView("xm y+4 w420 h90 +Grid", ["PID", "Title", "Priority", "Affinity"])
    processList.ModifyCol(1, 45)
    processList.ModifyCol(2, 180)
    processList.ModifyCol(3, 80)
    processList.ModifyCol(4, 80)

    RefreshProcessList(*) {
        processList.Delete()
        for id in WinGetList("ahk_exe eqgame.exe") {
            try {
                pid := WinGetPID("ahk_id " id)
                title := WinGetTitle("ahk_id " id)
                pri := GetProcessPriorityName(pid)
                affStr := "—"
                hProc := 0
                try {
                    hProc := DllCall("OpenProcess", "UInt", 0x0400, "Int", 0, "UInt", pid, "Ptr")
                    if (hProc) {
                        procMask := 0
                        sysMask := 0
                        DllCall("GetProcessAffinityMask", "Ptr", hProc, "UPtr*", &procMask, "UPtr*", &sysMask)
                        affStr := String(procMask)
                    }
                } finally {
                    if (hProc)
                        DllCall("CloseHandle", "Ptr", hProc)
                }
                processList.Add(, pid, title, pri, affStr)
            }
        }
        if (processList.GetCount() = 0)
            processList.Add(, "—", "No EQ processes running", "—", "—")
    }
    RefreshProcessList()

    pm.AddButton("xm y+4 w80 h22", "Refresh").OnEvent("Click", RefreshProcessList)
    btnForceApply := pm.AddButton("x+4 yp w120 h22", "Force Apply to All")
    btnForceApply.ToolTip := "Push current priority && affinity settings to all running EQ processes"
    btnForceApply.OnEvent("Click", ForceApplyAll)

    ForceApplyAll(*) {
        ; Read current UI state, not just saved config — same logic as ApplyPM
        ApplyPM()
        ShowTip("⚡ Settings forced on all running EQ processes!")
    }

    ; ── Process Priority ──
    pm.AddText("xm y+8 w420 h1 0x10")
    pm.SetFont("s9 Bold", "Segoe UI")
    pm.AddText("xm y+4 w420 cNavy", "🔥  Process Priority")
    pm.SetFont("s8", "Segoe UI")
    pm.AddText("xm y+3 cGray", "Applied on launch and when EQSwitch starts.")

    pm.AddText("xm y+6", "Priority:")
    priorityLevels := ["Normal", "AboveNormal", "High"]
    priorityCombo := pm.AddDropDownList("x+4 yp-2 w120", priorityLevels)
    for i, lvl in priorityLevels {
        if (lvl = PROCESS_PRIORITY)
            priorityCombo.Choose(i)
    }
    if (priorityCombo.Value = 0)
        priorityCombo.Choose(1)

    ; ── CPU Affinity ──
    pm.AddText("xm y+8 w420 h1 0x10")
    pm.SetFont("s9 Bold", "Segoe UI")
    pm.AddText("xm y+4 w420 cNavy", "🔧  CPU Affinity")
    pm.SetFont("s8", "Segoe UI")
    pm.AddText("xm y+3 cGray", "Unchecking all = use all cores.")

    cores := AffinityMaskToCores(CPU_AFFINITY, coreCount)
    coreChecks := []
    Loop coreCount {
        xOpt := (Mod(A_Index - 1, 8) = 0) ? "xm y+3" : "x+4 yp"
        chk := pm.AddCheckbox(xOpt, "" A_Index)
        chk.Value := cores[A_Index]
        coreChecks.Push(chk)
    }

    pm.AddButton("xm y+4 w50 h20", "All").OnEvent("Click", SelectAllCores)
    pm.AddButton("x+4 yp w50 h20", "None").OnEvent("Click", SelectNoCores)

    SelectAllCores(*) {
        for chk in coreChecks
            chk.Value := 1
    }
    SelectNoCores(*) {
        for chk in coreChecks
            chk.Value := 0
    }

    ; ── Save / Apply / Close ──
    pm.AddText("xm y+6 w420 h1 0x10")
    pm.AddButton("xm y+4 w70 h26 Default", "Save").OnEvent("Click", SaveAndClosePM)
    pm.AddButton("x+6 yp w70 h26", "Apply").OnEvent("Click", ApplyPM)
    pm.AddButton("x+6 yp w70 h26", "Close").OnEvent("Click", (*) => (g_pmOpen := false, pm.Destroy()))

    ApplyPM(*) {
        global CPU_AFFINITY, PROCESS_PRIORITY
        ; Read priority from dropdown
        PROCESS_PRIORITY := priorityLevels[priorityCombo.Value]

        ; Build affinity mask from checkboxes
        coreVals := []
        for chk in coreChecks
            coreVals.Push(chk.Value)
        mask := CoresMaskToAffinity(coreVals)
        allSelected := true
        noneSelected := true
        for val in coreVals {
            if !val
                allSelected := false
            if val
                noneSelected := false
        }
        ; All or none selected = use all cores (full system mask)
        if (allSelected || noneSelected) {
            CPU_AFFINITY := ""
            fullMask := (1 << coreCount) - 1
            applyMask := String(fullMask)
        } else {
            CPU_AFFINITY := String(mask)
            applyMask := CPU_AFFINITY
        }

        ; Save to INI
        SaveConfig()

        ; Apply immediately to running processes
        ApplyProcessPriority(PROCESS_PRIORITY)
        ApplyAffinityToAll(applyMask)

        RefreshProcessList()
        ShowTip("⚡ Process settings saved and applied!")
    }

    SaveAndClosePM(*) {
        ApplyPM()
        g_pmOpen := false
        pm.Destroy()
    }

    pm.OnEvent("Escape", (*) => (g_pmOpen := false, pm.Destroy()))
    pm.OnEvent("Close", (*) => (g_pmOpen := false, pm.Destroy()))
    pm.Show("AutoSize")

    } catch as err {
        g_pmOpen := false
        try pm.Destroy()
        ShowTip("⚠ Process Manager error: " err.Message, 3000)
    }
}

; =========================================================
; VIDEO MODE EDITOR (eqclient.ini)
; =========================================================
g_vmOpen := false
OpenVideoModeEditor(*) {
    global g_vmOpen, TOOLTIP_MS
    if g_vmOpen
        return
    g_vmOpen := true

    eqDir := GetEqDir()
    iniPath := eqDir "eqclient.ini"
    if !FileExist(iniPath) {
        g_vmOpen := false
        ShowTip("⚠ eqclient.ini not found — check EQ path in Settings")
        return
    }

    try {

    ; Read current VideoMode values
    ReadVM(key, def) {
        try return IniRead(iniPath, "VideoMode", key)
        catch
            return def
    }

    vm := Gui("+AlwaysOnTop", "🖥 EQ Video Settings")
    vm.SetFont("s9", "Segoe UI")
    vm.MarginX := 14
    vm.MarginY := 10

    vm.SetFont("s10 Bold", "Segoe UI")
    vm.AddText("w400 c0xAA3300", "🖥  eqclient.ini — [VideoMode]")
    vm.SetFont("s9", "Segoe UI")
    vm.AddText("xm y+4 w400 h1 0x10")

    ; Resolution presets
    vm.AddText("xm y+6", "Preset:")
    resPresets := ["1920×1080", "1920×1200", "2560×1440", "1280×720", "Custom"]
    resPresetCombo := vm.AddDropDownList("x+4 yp-2 w130", resPresets)
    resPresetCombo.Choose(5)  ; default to Custom
    presetMap := [[1920,1080], [1920,1200], [2560,1440], [1280,720]]

    vm.AddText("xm y+8", "WindowedWidth:")
    vmWW := vm.AddEdit("x+4 yp-2 w60 Number", ReadVM("WindowedWidth", "1920"))
    vm.AddText("x+10 yp+2", "WindowedHeight:")
    vmWH := vm.AddEdit("x+4 yp-2 w60 Number", ReadVM("WindowedHeight", "1080"))

    vm.AddText("xm y+6", "WindowedModeXOffset:")
    vmXOff := vm.AddEdit("x+4 yp-2 w50", ReadVM("WindowedModeXOffset", "0"))
    vm.AddText("x+10 yp+2", "WindowedModeYOffset:")
    vmYOff := vm.AddEdit("x+4 yp-2 w50", ReadVM("WindowedModeYOffset", "0"))

    vm.AddText("xm y+8 cGray w400",
        "Tip: WindowedHeight=1009 sits above taskbar. Set XOffset/YOffset to -8 for borderless-windowed (hides title bar).")

    ; Borderless preset button
    vm.AddButton("xm y+6 w160 h24", "Apply Borderless Preset").OnEvent("Click", ApplyBorderless)
    vm.AddButton("x+8 yp w100 h24", "Default Offsets").OnEvent("Click", ResetDefaults)

    ApplyBorderless(*) {
        ; Keep current resolution, only set offsets for borderless-windowed
        vmXOff.Value := "-8"
        vmYOff.Value := "-8"
    }
    ResetDefaults(*) {
        vmXOff.Value := "0"
        vmYOff.Value := "0"
    }

    resPresetCombo.OnEvent("Change", OnResPreset)
    OnResPreset(*) {
        idx := resPresetCombo.Value
        if (idx >= 1 && idx <= presetMap.Length) {
            vmWW.Value := presetMap[idx][1]
            vmWH.Value := presetMap[idx][2]
        }
    }

    vm.AddText("xm y+10 w400 h1 0x10")
    vm.AddButton("xm y+6 w80 h28 Default", "Save").OnEvent("Click", SaveAndCloseVM)
    vm.AddButton("x+8 yp w80 h28", "Apply").OnEvent("Click", ApplyVM)
    vm.AddButton("x+8 yp w80 h28", "Close").OnEvent("Click", (*) => (g_vmOpen := false, vm.Destroy()))

    ApplyVM(*) {
        try {
            IniWrite(vmWW.Value, iniPath, "VideoMode", "WindowedWidth")
            IniWrite(vmWH.Value, iniPath, "VideoMode", "WindowedHeight")
            IniWrite(vmXOff.Value, iniPath, "VideoMode", "WindowedModeXOffset")
            IniWrite(vmYOff.Value, iniPath, "VideoMode", "WindowedModeYOffset")
            ShowTip("🖥 Video settings saved to eqclient.ini — restart EQ to apply")
        } catch as err {
            ShowTip("⚠ Failed to save: " err.Message, 5000)
        }
    }

    SaveAndCloseVM(*) {
        ApplyVM()
        g_vmOpen := false
        vm.Destroy()
    }

    vm.OnEvent("Escape", (*) => (g_vmOpen := false, vm.Destroy()))
    vm.OnEvent("Close", (*) => (g_vmOpen := false, vm.Destroy()))
    vm.Show("AutoSize")

    } catch as err {
        g_vmOpen := false
        try vm.Destroy()
        ShowTip("⚠ Video settings error: " err.Message, 3000)
    }
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
    g_launchGrace := A_TickCount + 30000  ; suppress auto-minimize for 30s during load
    Run('"' EQ_EXE '" ' EQ_ARGS, eqDir, , &newPid)
    ; Snapshot settings at launch time so mid-launch Settings changes can't affect behavior
    launchPriority := PROCESS_PRIORITY
    launchAffinity := CPU_AFFINITY
    needsPri := (launchPriority != "Normal" && launchPriority != "")
    needsAff := (launchAffinity != "")
    if (needsPri || needsAff) {
        ; Apply with retry — EQ may reset affinity during its own startup sequence
        retryCount := 0
        ApplyDelayed(*) {
            retryCount++
            if needsPri
                try ProcessSetPriority(launchPriority, newPid)
            if needsAff
                ApplyAffinityToPid(newPid, launchAffinity)
            ; Retry up to 3 times (at 5s, 10s, 20s) to catch EQ resetting affinity on load
            if (retryCount < 3)
                SetTimer(ApplyDelayed, -(retryCount = 1 ? 5000 : 10000))
        }
        SetTimer(ApplyDelayed, -5000)
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
    g_launchGrace := A_TickCount + 30000  ; suppress auto-minimize for 30s during load
    pids := []
    launchIdx := 0

    ApplyLaunchSettings(*) {
        if (launchPriority != "Normal" && launchPriority != "") {
            for pid in pids
                try ProcessSetPriority(launchPriority, pid)
        }
        if (launchAffinity != "") {
            for pid in pids
                ApplyAffinityToPid(pid, launchAffinity)
        }
    }

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
            ApplyLaunchSettings()
            ; Re-apply after 10s — EQ may reset affinity during its startup sequence
            SetTimer(ApplyLaunchSettings, -10000)
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
