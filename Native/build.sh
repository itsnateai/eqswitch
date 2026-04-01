#!/bin/bash
# Build eqswitch-hook.dll (32-bit x86) using MSVC cross-compiler
# Run from repo root: bash Native/build.sh

set -e

NATIVE="$(cd "$(dirname "$0")" && pwd)"
VCTOOLS="C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.50.35717"
CL="$VCTOOLS/bin/Hostx64/x86/cl.exe"
SDKVER="10.0.26100.0"
WINSDK="C:/Program Files (x86)/Windows Kits/10"

echo "Building eqswitch-hook.dll (x86)..."

cd "$NATIVE"

MSYS_NO_PATHCONV=1 "$CL" /nologo /LD /O2 /W3 /DNDEBUG \
    "/I$VCTOOLS/include" \
    "/I$WINSDK/Include/$SDKVER/ucrt" \
    "/I$WINSDK/Include/$SDKVER/um" \
    "/I$WINSDK/Include/$SDKVER/shared" \
    eqswitch-hook.cpp hook.c buffer.c trampoline.c hde32.c \
    /Fe:eqswitch-hook.dll \
    /link \
    "/LIBPATH:$VCTOOLS/lib/x86" \
    "/LIBPATH:$WINSDK/Lib/$SDKVER/ucrt/x86" \
    "/LIBPATH:$WINSDK/Lib/$SDKVER/um/x86" \
    user32.lib kernel32.lib advapi32.lib /MACHINE:X86 /DLL

# Clean intermediate files
rm -f "$NATIVE"/*.obj "$NATIVE"/*.exp "$NATIVE"/*.lib 2>/dev/null

echo ""
echo "Build successful: $(ls -la eqswitch-hook.dll | awk '{print $5}') bytes"
