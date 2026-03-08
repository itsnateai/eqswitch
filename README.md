# ⚔ EQ Switch

**EQ Switch** is a lightweight Windows tray utility for **Shards of Dalaya** that lets you instantly flip between two game clients with a single keypress — plus a handful of handy tools for managing your session.

> Built with AutoHotkey v2. No installation required. Single `.exe`, no system footprint.

---

## 📸 Screenshots

| Tray Menu | Settings |
|:---:|:---:|
| ![Tray Menu](tray.png) | ![Context Menu](traymenu.png) |
| ![Settings](settings.png) | ![Tray Settings](traysettings.png) |

---

## 📥 Download & Install

1. Go to the [**Releases**](../../releases) page and download the latest `EQ2Box.exe`
2. Drop it anywhere you want — your game folder, Desktop, wherever
3. Optionally place `eqbox.ico` in the same folder for the custom tray icon
4. Double-click `EQ2Box.exe` to run it — it lives in your system tray
5. **Right-click the tray icon → Settings** and set up your switch hotkey

That's it. No installer, no registry entries, nothing left behind if you delete it.

---

## 🚀 First Time Setup

When you run EQ Switch for the first time with no config file, it will automatically open Settings and show you a welcome tooltip. **The most important thing to set is your Switch Hotkey** — it's the whole point of the program.

### Recommended setup steps:
1. **Set your Switch Hotkey** — the key you'll press in-game to jump between windows. Most people use `\` (backslash)
2. **Set your EQ Executable** — point it at your `eqgame.exe`
3. Hit **Save**

Everything else is optional and can be configured later.

---

## ⌨ How the Window Switch Works

Once running, press your configured hotkey **while the game is the active window** and EQ Switch will instantly bring your other EQ client to the front. It cycles through all visible EQ windows in order, so it works with 2+ clients.

> The hotkey **only fires in-game** — it won't interfere with anything else on your PC.

---

## 🖱 Tray Menu Features

Right-click the tray icon to access everything:

| Menu Item | What it does |
|---|---|
| **⚔ Launch Client** | Launches one EQ client |
| **🎮 Launch Both** | Launches two clients, waits for them to load, then maximizes both |
| **🪟 Fix Windows** | Maximizes all open EQ windows (useful after alt-tab issues) |
| **📜 Open Log File** | Opens an EQ character's log file in Notepad (prompts for char name) |
| **📂 Open eqclient.ini** | Opens eqclient.ini from your EQ folder in Notepad |
| **🎯 Open Gina** | Launches Gina trigger app (path configured in Settings) |
| **📝 Open Notes** | Opens your notes .txt file in Notepad |
| **🌐 Dalaya Wiki** | Opens https://wiki.dalaya.org/ in your browser |
| **🌐 Shards Wiki** | Opens https://wiki.shardsofdalaya.com in your browser |
| **🌐 Dalaya Fomelo** | Opens https://dalaya.org/fomelo/ in your browser |
| **⚙ Settings** | Opens the Options window |
| **✖ Exit** | Closes EQ Switch |

---

## ⚙ Settings / Options

### ⌨ Window Switch Hotkey ⭐
The core feature. Set the key you'll press in-game to switch between clients. The current active key is shown in green so you always know what's bound.

### ⚔ EQ Settings
- **EQ Executable** — path to your `eqgame.exe`
- **Launch Arguments** — defaults to `-patchme`, change if your server needs something different

### 🎯 Open Gina
Set the path to `Gina.exe` so the tray menu can launch it directly.

### 📝 Notes File
Point to any `.txt` file to use as your in-game notes. Leave blank and EQ Switch will create a `notes.txt` in its own folder. Middle-clicking the tray icon can open this file instantly (see Tray Icon options).

### 🖱 Tray Icon
- **Double-click launches a client** — double-clicking the tray icon launches one EQ client instead of opening Settings
- **Middle-click opens Notepad notes** — middle-clicking the tray icon opens your notes file directly

### 💾 Backup Char Files
Copies your character's UI and settings files to your Desktop with one click:
- `UI_CharName_dalaya.ini` — your custom UI layout
- `CharName_dalaya.ini` — your character settings

Type a character name or pick from the recent names dropdown, then hit **Backup to Desktop**. Recent names are saved between sessions.

---

## 📁 Files

| File | Purpose |
|---|---|
| `EQ2Box.exe` | The main program — this is all you need |
| `eqbox.ico` | Tray icon (optional, place next to the exe) |
| `eqbox.cfg` | Auto-created config file, stores all your settings |
| `notes.txt` | Auto-created if you use the Notes feature without setting a custom path |
| `EQ2Box.ahk` | Source code (AutoHotkey v2) |

---

## 🔧 Running from Source

If you'd rather run the `.ahk` directly instead of the compiled exe:

1. Install [AutoHotkey v2](https://www.autohotkey.com/) (v2.x, **not** v1)
2. Double-click `EQ2Box.ahk`

To compile it yourself:
```
"C:\Program Files\AutoHotkey\Compiler\Ahk2Exe.exe" /in EQ2Box.ahk /icon eqbox.ico
```
Make sure you select **AutoHotkey v2** as the base file in Ahk2Exe — using v1 will give a syntax error.

---

## ❓ FAQ

**Q: Windows says the file is from an unknown publisher — is it safe?**
Yes. EQ Switch is an unsigned personal tool. Click *More info → Run anyway* on the SmartScreen prompt. You can inspect the full source code in `EQ2Box.ahk`.

**Q: Windows Defender / my antivirus flagged EQ2Box.exe — is it a virus?**
No. AutoHotkey-compiled executables are frequently flagged as false positives by heuristic AV engines because the packaging technique (bundling an interpreter + script into a single exe) is also used by some malware. The exe is compiled with `/compress 0` to minimize these detections. You can verify the source yourself in `EQ2Box.ahk`, or run it from source directly with AutoHotkey v2 installed.

**Q: The switch hotkey isn't working**
Make sure the game is the **active/focused** window when you press it — the hotkey is intentionally scoped to only fire inside EQ so it doesn't conflict with other apps.

**Q: Settings stopped opening / tray clicks aren't working**
This was a known bug that's been fixed. If it happens, just close and reopen EQ Switch. The root cause (settings flag getting stuck) is now handled by closing with X, Escape, or Save all resetting properly.

**Q: Can I use this with more than 2 EQ clients?**
Yes! It cycles through all visible EQ windows in order. Just keep pressing your switch key to rotate through them.

**Q: Where is my config saved?**
In `eqbox.cfg` next to the exe, as a plain INI file you can read or edit manually.

---

## 💬 Credits

Built for the **Shards of Dalaya** community. Long Live Dalaya! ⚔

---

## 📜 License

Do whatever you want with it. Share it, modify it, pass it to your guildmates.
https://github.com/itsnateai/eqswitch
