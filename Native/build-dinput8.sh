#!/bin/bash
# Build dinput8.dll (32-bit x86) proxy using MSVC cross-compiler
# Run from repo root: bash Native/build-dinput8.sh
#
# Output: Native/dinput8.dll
# To test: copy dinput8.dll to your EQ game folder (next to eqgame.exe),
# launch EQ, check for eqswitch-dinput8.log in the game folder.

set -e

NATIVE="$(cd "$(dirname "$0")" && pwd)"
VCTOOLS="C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.50.35717"
CL="$VCTOOLS/bin/Hostx64/x86/cl.exe"
SDKVER="10.0.26100.0"
WINSDK="C:/Program Files (x86)/Windows Kits/10"

echo "Building dinput8.dll (x86 proxy)..."

cd "$NATIVE"

MSYS_NO_PATHCONV=1 "$CL" /nologo /LD /O2 /W3 /DNDEBUG /EHsc \
    "/I$VCTOOLS/include" \
    "/I$WINSDK/Include/$SDKVER/ucrt" \
    "/I$WINSDK/Include/$SDKVER/um" \
    "/I$WINSDK/Include/$SDKVER/shared" \
    dinput8-proxy.cpp di8_proxy.cpp device_proxy.cpp key_shm.cpp iat_hook.cpp \
    /Fe:dinput8.dll \
    /link \
    "/LIBPATH:$VCTOOLS/lib/x86" \
    "/LIBPATH:$WINSDK/Lib/$SDKVER/ucrt/x86" \
    "/LIBPATH:$WINSDK/Lib/$SDKVER/um/x86" \
    /DEF:dinput8.def \
    kernel32.lib user32.lib /MACHINE:X86 /DLL

# Clean intermediate files
rm -f "$NATIVE"/*.obj "$NATIVE"/dinput8.exp "$NATIVE"/dinput8.lib 2>/dev/null

echo ""
echo "Build successful: $(ls -la dinput8.dll | awk '{print $5}') bytes"
