#!/bin/bash
# Build eqswitch-di8.dll (32-bit x86) using MSVC cross-compiler
# Injected into eqgame.exe to hook DirectInput8Create via MinHook detour.
# Run from repo root: bash Native/build-di8-inject.sh
#
# Output: Native/eqswitch-di8.dll

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

echo "Building eqswitch-di8.dll (x86 injectable)..."
echo "MSVC: $CL"
echo "SDK: $SDKVER"

cd "$NATIVE"

MSYS_NO_PATHCONV=1 "$CL" /nologo /LD /O2 /W3 /DNDEBUG /EHsc \
    "/I$VCTOOLS/include" \
    "/I$WINSDK/Include/$SDKVER/ucrt" \
    "/I$WINSDK/Include/$SDKVER/um" \
    "/I$WINSDK/Include/$SDKVER/shared" \
    eqswitch-di8.cpp di8_proxy.cpp device_proxy.cpp key_shm.cpp iat_hook.cpp pattern_scan.cpp net_debug.cpp mq2_bridge.cpp login_state_machine.cpp login_givetime_detour.cpp \
    hook.c buffer.c trampoline.c hde32.c \
    /Fe:eqswitch-di8.dll \
    /link \
    "/LIBPATH:$VCTOOLS/lib/x86" \
    "/LIBPATH:$WINSDK/Lib/$SDKVER/ucrt/x86" \
    "/LIBPATH:$WINSDK/Lib/$SDKVER/um/x86" \
    kernel32.lib user32.lib /MACHINE:X86 /DLL

# Clean intermediate files
rm -f "$NATIVE"/*.obj "$NATIVE"/eqswitch-di8.exp "$NATIVE"/eqswitch-di8.lib 2>/dev/null

echo ""
echo "Build successful: $(ls -la eqswitch-di8.dll | awk '{print $5}') bytes"
