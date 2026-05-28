@echo off
REM Build eqswitch-hook.dll AND eqswitch-di8.dll (32-bit x86) using MSVC
REM Run from Native/ directory or via: cd Native && build.cmd
REM
REM v3.22.70: extended from hook-only to both DLLs — was a pre-existing
REM parity gap with build-di8-inject.sh that R3 verifiers flagged. MSVC-only
REM contributors on Windows without bash now produce both binaries.

setlocal

REM Find MSVC tools via vswhere (works with any VS version)
for /f "delims=" %%i in ('"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath 2^>nul') do set "VSDIR=%%i"
if not defined VSDIR (
    echo ERROR: Visual Studio not found. Install VS with C++ workload.
    exit /b 1
)

REM Find the latest MSVC toolset
for /d %%i in ("%VSDIR%\VC\Tools\MSVC\*") do set "VCTOOLS=%%i"
set "CL=%VCTOOLS%\bin\Hostx64\x86\cl.exe"
set "LINK=%VCTOOLS%\bin\Hostx64\x86\link.exe"
set "INCLUDE=%VCTOOLS%\include"
set "LIB=%VCTOOLS%\lib\x86"

REM Windows SDK paths
set "WINSDK=C:\Program Files (x86)\Windows Kits\10"
for /d %%i in ("%WINSDK%\Include\10.*") do set "SDKINC=%%i"
for /d %%i in ("%WINSDK%\Lib\10.*") do set "SDKLIB=%%i"

echo MSVC: %CL%
echo SDK Include: %SDKINC%
echo SDK Lib: %SDKLIB%
echo.

REM ─── Build 1 of 2: eqswitch-hook.dll ──────────────────────────────
echo Building eqswitch-hook.dll (x86)...

"%CL%" /nologo /LD /O2 /W3 /DNDEBUG ^
    /I"%INCLUDE%" ^
    /I"%SDKINC%\ucrt" ^
    /I"%SDKINC%\um" ^
    /I"%SDKINC%\shared" ^
    eqswitch-hook.cpp hook.c buffer.c trampoline.c hde32.c ^
    /Fe:eqswitch-hook.dll ^
    /link /LIBPATH:"%LIB%" /LIBPATH:"%SDKLIB%\ucrt\x86" /LIBPATH:"%SDKLIB%\um\x86" ^
    user32.lib kernel32.lib advapi32.lib /MACHINE:X86 /DLL

if errorlevel 1 (
    echo BUILD FAILED: eqswitch-hook.dll
    exit /b 1
)

REM Clean intermediate files between builds — separate OBJ outputs prevent
REM hook .obj being relinked into di8.
del /Q *.obj 2>nul
del /Q eqswitch-hook.exp eqswitch-hook.lib 2>nul

echo.
echo Build successful: eqswitch-hook.dll
for %%f in (eqswitch-hook.dll) do echo   Size: %%~zf bytes
echo.

REM ─── Build 2 of 2: eqswitch-di8.dll ───────────────────────────────
REM /EHsc enables C++ exception handling; SEH __try/__except in mq2_bridge.cpp
REM and eqswitch-di8.cpp compiles fine under /EHsc (verified across R1/R2/R3
REM verification rounds). Source list mirrors build-di8-inject.sh:38 exactly —
REM if you add a .cpp to the di8 DLL, update BOTH this list AND build-di8-inject.sh
REM (or contributors using bash will silently miss the new TU).
echo Building eqswitch-di8.dll (x86 injectable)...

"%CL%" /nologo /LD /O2 /W3 /DNDEBUG /EHsc ^
    /I"%INCLUDE%" ^
    /I"%SDKINC%\ucrt" ^
    /I"%SDKINC%\um" ^
    /I"%SDKINC%\shared" ^
    eqswitch-di8.cpp di8_proxy.cpp device_proxy.cpp key_shm.cpp iat_hook.cpp pattern_scan.cpp net_debug.cpp mq2_bridge.cpp login_state_machine.cpp login_givetime_detour.cpp eqmain_offsets.cpp eqmain_cxstr.cpp eqmain_widgets.cpp eqmain_widgets_mq2style.cpp ^
    hook.c buffer.c trampoline.c hde32.c ^
    /Fe:eqswitch-di8.dll ^
    /link /LIBPATH:"%LIB%" /LIBPATH:"%SDKLIB%\ucrt\x86" /LIBPATH:"%SDKLIB%\um\x86" ^
    kernel32.lib user32.lib /MACHINE:X86 /DLL

if errorlevel 1 (
    echo BUILD FAILED: eqswitch-di8.dll
    exit /b 1
)

REM Clean intermediate files
del /Q *.obj 2>nul
del /Q eqswitch-di8.exp eqswitch-di8.lib 2>nul

echo.
echo Build successful: eqswitch-di8.dll
for %%f in (eqswitch-di8.dll) do echo   Size: %%~zf bytes
echo.
echo ALL BUILDS SUCCESSFUL
endlocal
