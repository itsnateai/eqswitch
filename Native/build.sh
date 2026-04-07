#!/bin/bash
# Build eqswitch-hook.dll (32-bit x86) using MSVC cross-compiler
# Run from repo root: bash Native/build.sh

set -e

NATIVE="$(cd "$(dirname "$0")" && pwd)"

# Find MSVC via vswhere
VSDIR=$(MSYS_NO_PATHCONV=1 "C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe" -latest -property installationPath 2>/dev/null || true)
if [ -z "$VSDIR" ]; then
    echo "ERROR: Visual Studio not found. Install VS with C++ workload."
    exit 1
fi

# Find latest MSVC toolset
VCTOOLS=$(ls -d "$VSDIR/VC/Tools/MSVC/"* 2>/dev/null | sort -V | tail -1)
CL="$VCTOOLS/bin/Hostx64/x86/cl.exe"

# Find latest Windows SDK version
WINSDK="C:/Program Files (x86)/Windows Kits/10"
SDKVER=$(ls "$WINSDK/Include/" 2>/dev/null | sort -V | tail -1)

echo "Building eqswitch-hook.dll (x86)..."
echo "MSVC: $CL"
echo "SDK: $SDKVER"

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
