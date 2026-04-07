@echo off
REM Build eqswitch-hook.dll (32-bit) using MSVC
REM Run from Native/ directory or via: cd Native && build.cmd

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

echo Building eqswitch-hook.dll (x86)...
echo MSVC: %CL%
echo SDK Include: %SDKINC%
echo SDK Lib: %SDKLIB%

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
    echo BUILD FAILED
    exit /b 1
)

echo.
echo Build successful: eqswitch-hook.dll
dir /b eqswitch-hook.dll
echo.
echo File info:
for %%f in (eqswitch-hook.dll) do echo   Size: %%~zf bytes
