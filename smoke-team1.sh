#!/bin/bash
# smoke-team1.sh — automate the EQSwitch autologin smoke-loop.
#
# Usage: bash smoke-team1.sh [--no-build] [--no-deploy] [--no-hotkey]
#
# Default: full cycle. Build native + C#, kill+deploy, launch, fire team1
# hotkey (Ctrl+Alt+Shift+F9), wait, tail the resulting DLL logs for the
# verdict markers.

set -uo pipefail

# Optional flags
DO_BUILD=1
DO_DEPLOY=1
DO_HOTKEY=1
for arg in "$@"; do
    case "$arg" in
        --no-build)  DO_BUILD=0 ;;
        --no-deploy) DO_DEPLOY=0 ;;
        --no-hotkey) DO_HOTKEY=0 ;;
    esac
done

REPO=/x/_Projects/eqswitch
DEPLOY=/c/Users/nate/proggy/Everquest/EQSwitch
DLLDIR=/c/Users/nate/proggy/Everquest/Eqfresh
EQSWITCH_EXE="C:\\Users\\nate\\proggy\\Everquest\\EQSwitch\\EQSwitch.exe"

cd "$REPO" || { echo "ERR: can't cd to $REPO"; exit 1; }

# ── Build ──
if [[ $DO_BUILD -eq 1 ]]; then
    echo "[1/6] Build native DLL..."
    bash Native/build-di8-inject.sh 2>&1 | tail -2 || { echo "ERR: native build failed"; exit 1; }
    echo "[2/6] Build C# release..."
    dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true 2>&1 | tail -2 \
        || { echo "ERR: C# build failed"; exit 1; }
fi

# ── Kill processes ──
echo "[3/6] Kill EQSwitch + eqgame..."
powershell -NoProfile -Command "Get-Process EQSwitch,eqgame -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 800" 2>/dev/null
echo "  killed (or none running)"

# ── Deploy with timestamped backup ──
if [[ $DO_DEPLOY -eq 1 ]]; then
    TS=$(date +%Y%m%d-%H%M%S)
    echo "[4/6] Backup current binaries + deploy new..."
    if [[ -f "$DEPLOY/EQSwitch.exe" ]]; then
        cp "$DEPLOY/EQSwitch.exe" "$DEPLOY/EQSwitch.exe.smoke-${TS}" || true
    fi
    if [[ -f "$DEPLOY/eqswitch-di8.dll" ]]; then
        cp "$DEPLOY/eqswitch-di8.dll" "$DEPLOY/eqswitch-di8.dll.smoke-${TS}" || true
    fi
    cp "$REPO/bin/Release/net8.0-windows/win-x64/publish/EQSwitch.exe" "$DEPLOY/EQSwitch.exe" \
        || { echo "ERR: EQSwitch.exe copy failed"; exit 1; }
    cp "$REPO/Native/eqswitch-di8.dll" "$DEPLOY/eqswitch-di8.dll" \
        || { echo "ERR: dll copy failed"; exit 1; }
    DLL_SZ=$(stat -c%s "$DEPLOY/eqswitch-di8.dll")
    EXE_SZ=$(stat -c%s "$DEPLOY/EQSwitch.exe")
    echo "  deployed: EQSwitch.exe ${EXE_SZ}B, eqswitch-di8.dll ${DLL_SZ}B"
fi

# ── Launch ──
echo "[5/6] Launch EQSwitch..."
powershell -NoProfile -Command "Start-Process -FilePath '${EQSWITCH_EXE}'; Start-Sleep -Seconds 3" 2>/dev/null
echo "  launched"

# ── Fire team1 hotkey (Ctrl+Alt+Shift+F9) ──
if [[ $DO_HOTKEY -eq 1 ]]; then
    echo "[6/6] Fire team1 hotkey (Ctrl+Alt+Shift+F9)..."
    # SendKeys uses ^ for Ctrl, % for Alt, + for Shift. F9 is {F9}.
    powershell -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('^%+{F9}')" 2>/dev/null
    echo "  hotkey sent"
fi

# ── Tail logs for verdict markers ──
echo
echo "===== Waiting 30s for smoke to advance through warmup + BURST 1 ====="
sleep 30
echo
echo "===== Latest DLL logs ====="
ls -t "$DLLDIR"/eqswitch-dinput8-*.log 2>/dev/null | head -2 | while read -r LOG; do
    PID=$(basename "$LOG" | sed 's/eqswitch-dinput8-\([0-9]*\)\.log/\1/')
    echo
    echo "--- PID $PID: FindEmptyEditGlobal + Combo G + JoinServer markers ---"
    grep -nE "FindEmptyEditGlobal|cand\[|STRUCTURAL-EMPTY-GLOBAL|set password via Combo G|JOIN_SERVER ack|LoginServerAPI (ready|not-ready)" "$LOG" 2>/dev/null | head -25
done

echo
echo "===== C# eqswitch.log tail ====="
tail -25 "$DEPLOY/eqswitch.log" 2>/dev/null
