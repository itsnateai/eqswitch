@echo off
setlocal enabledelayedexpansion
title EQSwitch Uninstall
echo.
echo  ============================================
echo   EQSwitch — Uninstall / Revert
echo  ============================================
echo.
echo  This will revert external changes made by EQSwitch:
echo    - Restore original dinput8.dll in EQ folder
echo    - Remove startup shortcut
echo    - Remove desktop shortcut
echo.
echo  Your eqswitch-config.json and eqclient.ini settings
echo  will NOT be modified.
echo.

:: ─── Find EQ path from config ──────────────────────────────
set "EQPATH="
set "CONFIGFILE=%~dp0eqswitch-config.json"

if exist "%CONFIGFILE%" (
    :: Use PowerShell for reliable JSON parsing (handles colons, spaces, escapes)
    for /f "usebackq delims=" %%p in (`powershell -NoProfile -Command "(Get-Content '%CONFIGFILE%' | ConvertFrom-Json).EQPath"`) do set "EQPATH=%%p"
)

if not defined EQPATH (
    echo  Could not read EQ path from config.
    echo.
    set /p "EQPATH=  Enter your EverQuest directory path: "
)

if not defined EQPATH (
    echo  No path provided. Exiting.
    pause
    exit /b 1
)

echo  EQ Path: !EQPATH!
echo.

set /p "CONFIRM=  Proceed with uninstall? (Y/N): "
if /i not "!CONFIRM!"=="Y" (
    echo  Cancelled.
    pause
    exit /b 0
)

echo.
set "COUNT=0"

:: ─── Restore dinput8.dll ────────────────────────────────────
set "DLL=!EQPATH!\dinput8.dll"
set "BACKUP=!EQPATH!\dinput8.dll.old"

if exist "!BACKUP!" (
    copy /y "!BACKUP!" "!DLL!" >nul 2>&1
    if errorlevel 1 (
        echo  [!!] Could not restore dinput8.dll — file may be in use
    ) else (
        del "!BACKUP!" >nul 2>&1
        echo  [OK] Restored original dinput8.dll from backup
        set /a COUNT+=1
    )
) else if exist "!DLL!" (
    del "!DLL!" >nul 2>&1
    if exist "!DLL!" (
        echo  [!!] Could not delete dinput8.dll — file may be in use
    ) else (
        echo  [OK] Removed dinput8.dll from EQ folder
        set /a COUNT+=1
    )
) else (
    echo  [--] No dinput8.dll found in EQ folder
)

:: ─── Remove startup shortcut ────────────────────────────────
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\EQSwitch.lnk"
if exist "!STARTUP!" (
    del "!STARTUP!" >nul 2>&1
    if exist "!STARTUP!" (
        echo  [!!] Could not remove startup shortcut — file may be in use
    ) else (
        echo  [OK] Removed startup shortcut
        set /a COUNT+=1
    )
) else (
    echo  [--] No startup shortcut found
)

:: ─── Remove desktop shortcut ────────────────────────────────
:: Use PowerShell to resolve actual Desktop path (handles OneDrive redirection)
for /f "usebackq delims=" %%d in (`powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')"`) do set "DESKTOPDIR=%%d"
if not defined DESKTOPDIR set "DESKTOPDIR=%USERPROFILE%\Desktop"

set "DESKTOP=!DESKTOPDIR!\EQSwitch.lnk"
if exist "!DESKTOP!" (
    del "!DESKTOP!" >nul 2>&1
    if exist "!DESKTOP!" (
        echo  [!!] Could not remove desktop shortcut — file may be in use
    ) else (
        echo  [OK] Removed desktop shortcut
        set /a COUNT+=1
    )
) else (
    echo  [--] No desktop shortcut found
)

:: ─── Summary ────────────────────────────────────────────────
echo.
if !COUNT! equ 0 (
    echo  Nothing to clean up — no external modifications found.
) else (
    echo  Done! Reverted !COUNT! change(s).
)
echo.
echo  You can now delete the EQSwitch folder to fully remove it.
echo.
pause
