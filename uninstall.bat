@echo off
setlocal enabledelayedexpansion
title EQSwitch Uninstall
echo.
echo  ============================================
echo   EQSwitch -- Uninstall / Revert
echo  ============================================
echo.
echo  RECOMMENDED: use the in-app button instead.
echo    Run EQSwitch -^> Settings -^> Paths tab -^> Uninstall
echo  The GUI button uses the same logic as this script with
echo  full error reporting in a result dialog.
echo.
echo  This script reverts external changes made by EQSwitch:
echo    - Restore Dalaya's dinput8.dll if a legacy proxy is in the way
echo    - Remove legacy EQSwitch DLL artifacts from EQ folder
echo    - Remove startup shortcut
echo    - Remove desktop shortcut
echo    - Remove legacy registry startup entry
echo.
echo  Your eqswitch-config.json and eqclient.ini settings
echo  will NOT be modified. Delete the EQSwitch folder yourself
echo  to fully remove the app.
echo.

:: ----- Find EQ path from config --------------------------------
set "EQPATH="
set "CONFIGFILE=%~dp0eqswitch-config.json"

if exist "%CONFIGFILE%" (
    for /f "usebackq delims=" %%p in (`powershell -NoProfile -Command "(Get-Content '%CONFIGFILE%' | ConvertFrom-Json).EQPath"`) do set "EQPATH=%%p"
)

if not defined EQPATH (
    echo  Could not read EQ path from config.
    echo.
    set /p "EQPATH=  Enter your EverQuest directory path (or blank to skip EQ-folder cleanup): "
)

if defined EQPATH echo  EQ Path: !EQPATH!
echo.

set /p "CONFIRM=  Proceed with uninstall? (Y/N): "
if /i not "!CONFIRM!"=="Y" (
    echo  Cancelled.
    pause
    exit /b 0
)

echo.
set "COUNT=0"

:: ----- DLL cleanup in EQ folder --------------------------------
:: SAFETY: never blindly delete dinput8.dll. On a Dalaya install that's the
:: server-validated MQ2 core (~1.3MB). EQSwitch's old proxies were 141-148KB.
:: Mirrors UninstallHelper.RestoreLegacyDlls in Core/UninstallHelper.cs.
if defined EQPATH if exist "!EQPATH!" (
    set "DINPUT8=!EQPATH!\dinput8.dll"
    set "DALAYA=!EQPATH!\dinput8_dalaya.dll"
    set "OLDBAK=!EQPATH!\dinput8.dll.old"

    :: 1. Chain-load era: restore Dalaya's MQ2 if we renamed it.
    if exist "!DALAYA!" (
        if not exist "!DINPUT8!" (
            move /y "!DALAYA!" "!DINPUT8!" >nul 2>&1
            if errorlevel 1 (
                echo  [!!] Could not restore dinput8_dalaya.dll -- file may be in use
            ) else (
                echo  [OK] Restored Dalaya's dinput8.dll from chain-load rename
                set /a COUNT+=1
            )
        ) else (
            del "!DALAYA!" >nul 2>&1
            if exist "!DALAYA!" (
                echo  [!!] Could not remove stale dinput8_dalaya.dll
            ) else (
                echo  [OK] Removed stale dinput8_dalaya.dll from EQ folder
                set /a COUNT+=1
            )
        )
    )

    :: 2. Proxy era: size-detect and remove legacy proxy in EQ folder.
    if exist "!DINPUT8!" if not exist "!DALAYA!" (
        for %%F in ("!DINPUT8!") do set "DINPUT8_SIZE=%%~zF"
        if !DINPUT8_SIZE! lss 200000 (
            del "!DINPUT8!" >nul 2>&1
            if exist "!DINPUT8!" (
                echo  [!!] Could not remove legacy proxy dinput8.dll -- file may be in use
            ) else (
                echo  [OK] Removed legacy proxy dinput8.dll from EQ folder ^(!DINPUT8_SIZE! bytes^)
                set /a COUNT+=1
            )
        ) else (
            echo  [--] dinput8.dll is !DINPUT8_SIZE! bytes -- presumed Dalaya MQ2, leaving alone
        )
    )

    :: 3. Legacy .old backup from pre-injection era.
    if exist "!OLDBAK!" (
        del "!OLDBAK!" >nul 2>&1
        if not exist "!OLDBAK!" (
            echo  [OK] Removed legacy dinput8.dll.old backup from EQ folder
            set /a COUNT+=1
        )
    )
) else if defined EQPATH (
    echo  [--] EQ path "!EQPATH!" does not exist -- skipping EQ-folder cleanup
)

:: 4. Legacy dinput8.dll in EQSwitch's own folder (no longer shipped post-v3.4.3).
set "APPDLL=%~dp0dinput8.dll"
if exist "!APPDLL!" (
    del "!APPDLL!" >nul 2>&1
    if not exist "!APPDLL!" (
        echo  [OK] Removed legacy dinput8.dll from EQSwitch app folder
        set /a COUNT+=1
    )
)

:: ----- Remove startup shortcut ---------------------------------
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\EQSwitch.lnk"
if exist "!STARTUP!" (
    del "!STARTUP!" >nul 2>&1
    if exist "!STARTUP!" (
        echo  [!!] Could not remove startup shortcut -- file may be in use
    ) else (
        echo  [OK] Removed startup shortcut
        set /a COUNT+=1
    )
)

:: ----- Remove desktop shortcut ---------------------------------
:: PowerShell resolves Desktop path correctly even with OneDrive redirection.
for /f "usebackq delims=" %%d in (`powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')"`) do set "DESKTOPDIR=%%d"
if not defined DESKTOPDIR set "DESKTOPDIR=%USERPROFILE%\Desktop"

set "DESKTOP=!DESKTOPDIR!\EQSwitch.lnk"
if exist "!DESKTOP!" (
    del "!DESKTOP!" >nul 2>&1
    if exist "!DESKTOP!" (
        echo  [!!] Could not remove desktop shortcut -- file may be in use
    ) else (
        echo  [OK] Removed desktop shortcut
        set /a COUNT+=1
    )
)

:: ----- Remove legacy registry startup entry --------------------
:: Pre-shortcut versions wrote HKCU\...\Run\EQSwitch. New versions migrate
:: this away on first launch, but if the user installed a new build but
:: never ran it, the entry could persist. Defense-in-depth.
reg query "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v "EQSwitch" >nul 2>&1
if not errorlevel 1 (
    reg delete "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v "EQSwitch" /f >nul 2>&1
    if not errorlevel 1 (
        echo  [OK] Removed legacy registry startup entry
        set /a COUNT+=1
    )
)

:: ----- Summary -------------------------------------------------
echo.
if !COUNT! equ 0 (
    echo  Nothing to clean up -- no external modifications found.
) else (
    echo  Done! Reverted !COUNT! change^(s^).
)
echo.
echo  You can now delete the EQSwitch folder to fully remove it.
echo.
pause
