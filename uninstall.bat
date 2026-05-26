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
    :: INVARIANT: never delete a >=200KB dinput8.dll. When both files coexist
    :: (chain-load steady state), size-check dinput8.dll to decide which file is
    :: the live MQ2 vs the legacy proxy.
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
            for %%F in ("!DINPUT8!") do set "DINPUT8_SIZE=%%~zF"
            if !DINPUT8_SIZE! lss 200000 (
                :: dinput8.dll is our proxy; DALAYA is the real MQ2.
                del "!DINPUT8!" >nul 2>&1
                if exist "!DINPUT8!" (
                    echo  [!!] Could not remove legacy proxy dinput8.dll -- file may be in use
                ) else (
                    move /y "!DALAYA!" "!DINPUT8!" >nul 2>&1
                    if errorlevel 1 (
                        echo  [!!] Could not restore dinput8_dalaya.dll over deleted proxy
                    ) else (
                        echo  [OK] Restored Dalaya's dinput8.dll over legacy proxy ^(!DINPUT8_SIZE! bytes^)
                        set /a COUNT+=1
                    )
                )
            ) else (
                :: dinput8.dll is already legit MQ2 ^(>=200KB^); DALAYA is the orphan.
                del "!DALAYA!" >nul 2>&1
                if exist "!DALAYA!" (
                    echo  [!!] Could not remove stale dinput8_dalaya.dll
                ) else (
                    echo  [OK] Removed stale dinput8_dalaya.dll ^(dinput8.dll is !DINPUT8_SIZE! bytes, presumed Dalaya MQ2^)
                    set /a COUNT+=1
                )
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

    :: 4. Native DLL logs accumulated in the EQ folder. eqswitch-di8.dll
    :: writes eqswitch-dinput8-{pid}.log per-process and eqswitch-hook.dll
    :: writes eqswitch-hook.log; neither rotates. Mirrors step 4 of
    :: UninstallHelper.RestoreLegacyDlls (Core/UninstallHelper.cs:157-188).
    :: v3.22.53 post-verifier-fix (T2 Opus IMPORTANT): also count failed
    :: deletes (file locked by a running eqgame.exe) and surface a [!!]
    :: message so the user knows to close clients first. Otherwise the
    :: failure was silent and the user thought the sweep succeeded.
    set "LOG_REMOVED=0"
    set "LOG_FAILED=0"
    for %%L in ("!EQPATH!\eqswitch-*.log") do (
        if exist "%%~L" (
            del "%%~L" >nul 2>&1
            if not exist "%%~L" (
                set /a LOG_REMOVED+=1
            ) else (
                set /a LOG_FAILED+=1
            )
        )
    )
    if !LOG_REMOVED! gtr 0 (
        echo  [OK] Removed !LOG_REMOVED! native log file^(s^) from EQ folder ^(eqswitch-*.log^)
        :: v3.22.53 post-round-3 fix (T3 Opus CRITICAL): +=1 for the whole
        :: sweep, not +=N for the file count. Every other step in this script
        :: contributes one to COUNT per [OK] line, and UninstallHelper.cs:179
        :: adds a single action string per sweep — diverging the two paths
        :: would produce mismatched summary numbers from identical state.
        set /a COUNT+=1
    )
    if !LOG_FAILED! gtr 0 (
        echo  [!!] !LOG_FAILED! log file^(s^) locked -- close all eqgame.exe clients then re-run
    )
) else if defined EQPATH (
    echo  [--] EQ path "!EQPATH!" does not exist -- skipping EQ-folder cleanup
)

:: 5. Legacy dinput8.dll in EQSwitch's own folder (no longer shipped post-v3.4.3).
::    Sits outside the EQPATH block — this DLL is in the EQSwitch install dir,
::    not the EQ folder, so it runs regardless of EQPATH status.
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

:: Sweep both the current name (Dalaya.exe.lnk) and historical names so
:: a user who created a shortcut with any past build still gets a clean
:: uninstall. Order: current first, then legacy.
for %%S in ("Dalaya.exe.lnk" "EQSwitch.lnk" "EQSwitch.exe.lnk") do (
    set "DESKTOP=!DESKTOPDIR!\%%~S"
    if exist "!DESKTOP!" (
        del "!DESKTOP!" >nul 2>&1
        if exist "!DESKTOP!" (
            echo  [!!] Could not remove desktop shortcut %%~S -- file may be in use
        ) else (
            echo  [OK] Removed desktop shortcut %%~S
            set /a COUNT+=1
        )
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
:: v3.22.53 post-round-6 fix (T3 Opus IMPORTANT): if COUNT==0 BUT
:: LOG_FAILED>0, we printed "[!!] N log file(s) locked" earlier and now
:: contradict it with "Nothing to clean up". Surface the failure instead
:: so the user knows their action is required.
echo.
if not defined LOG_FAILED set "LOG_FAILED=0"
if !COUNT! equ 0 (
    if !LOG_FAILED! gtr 0 (
        echo  Could not complete -- !LOG_FAILED! log file^(s^) are locked. Close all eqgame.exe clients and re-run.
    ) else (
        echo  Nothing to clean up -- no external modifications found.
    )
) else (
    if !LOG_FAILED! gtr 0 (
        echo  Done! Reverted !COUNT! change^(s^). !LOG_FAILED! log file^(s^) still locked -- close eqgame.exe and re-run to finish.
    ) else (
        echo  Done! Reverted !COUNT! change^(s^).
    )
)
echo.
echo  You can now delete the EQSwitch folder to fully remove it.
echo.
pause
