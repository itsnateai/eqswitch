# EQSwitch v2.0 — Claude Code Context

## What This Is
C# (.NET 8 WinForms) port of EQSwitch, an EverQuest multiboxing window manager originally written in AHK v2. Targets the Shards of Dalaya emulator community.

## Architecture
- **Core/NativeMethods.cs**: All Win32 P/Invoke declarations. Add new imports here, nowhere else.
- **Core/ProcessManager.cs**: Polls for eqgame.exe processes, fires events on client discovery/loss.
- **Core/WindowManager.cs**: Window positioning, grid arrangement, title bar removal.
- **Core/AffinityManager.cs**: CPU affinity for P-core/E-core optimization on Intel hybrid CPUs.
- **Core/HotkeyManager.cs**: Global hotkeys via RegisterHotKey with a hidden message-only window.
- **Config/AppConfig.cs**: Strongly-typed JSON config model. All settings live here.
- **Config/ConfigManager.cs**: JSON load/save with auto-backup rotation (keeps last 10).
- **UI/TrayManager.cs**: System tray icon, context menu, and main orchestration loop.
- **UI/FirstRunDialog.cs**: One-time EQ path setup dialog.

## Build Commands
```bash
dotnet build                    # Debug
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true  # Portable exe
```

## Conventions
- All Win32 calls go through NativeMethods — never scatter DllImport.
- Config is portable JSON alongside the exe, not in AppData.
- Use Debug.WriteLine for diagnostic logging, BalloonTip for user-facing messages.
- Graceful degradation: if a Win32 call fails, log it and continue, don't crash.

## Key Design Decisions
- WinForms (not WPF) for lightweight tray-only app.
- Single-file publish for portability (no installer, matches AHK philosophy).
- MOD_NOREPEAT on all hotkeys to prevent key-held spam.
- Message-only NativeWindow (HWND_MESSAGE) for hotkey receiver — no visible windows.
- Timer-based affinity checking (250ms) rather than event-driven, because there's no clean Win32 event for "foreground window changed to my process."
