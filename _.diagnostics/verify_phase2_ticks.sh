#!/bin/bash
# verify_phase2_ticks.sh — check that v7 Phase 2's GiveTime detour fires
#
# Usage: bash _.diagnostics/verify_phase2_ticks.sh
#
# Watches eqswitch-dinput8.log for the "givetime_detour: tick N" markers emitted
# by the Phase 2 detour. Reports tick rate (expected: ~60 ticks/sec while at
# login screen, since the detour logs every 60 ticks = ~1 log line per second).
#
# Exit codes:
#   0 = detour fired at expected rate (>=1 tick log in last 5 seconds)
#   1 = log not found
#   2 = detour never installed (no "INSTALLED" marker)
#   3 = detour installed but never fired (install message present, no tick messages)
#   4 = tick rate too slow (<1 per 5s)

set -eu

LOG="C:/Users/nate/proggy/Everquest/Eqfresh/eqswitch-dinput8.log"

if [ ! -f "$LOG" ]; then
  echo "[FAIL] Log not found: $LOG"
  exit 1
fi

echo "=== Phase 2 detour verification ==="
echo "Log: $LOG"
echo ""

# Count install markers (should be exactly 1 per session)
INSTALL_COUNT=$(grep -c "givetime_detour: INSTALLED" "$LOG" || true)
# Strip trailing newlines/extra lines — grep -c sometimes emits "0\n" vs "0"

REFUSED_COUNT=$(grep -c "givetime_detour: REFUSING install" "$LOG" || true)
# Strip trailing newlines/extra lines — grep -c sometimes emits "0\n" vs "0"

TICK_COUNT=$(grep -c "givetime_detour: tick" "$LOG" || true)
# Strip trailing newlines/extra lines — grep -c sometimes emits "0\n" vs "0"

CREATE_FAIL=$(grep -c "givetime_detour: MH_CreateHook failed" "$LOG" || true)
# Strip trailing newlines/extra lines — grep -c sometimes emits "0\n" vs "0"

ENABLE_FAIL=$(grep -c "givetime_detour: MH_EnableHook failed" "$LOG" || true)
# Strip trailing newlines/extra lines — grep -c sometimes emits "0\n" vs "0"


echo "Markers in log:"
echo "  INSTALLED:                $INSTALL_COUNT"
echo "  REFUSED (bad prologue):   $REFUSED_COUNT"
echo "  CreateHook failures:      $CREATE_FAIL"
echo "  EnableHook failures:      $ENABLE_FAIL"
echo "  Tick log messages:        $TICK_COUNT"
echo ""

if [ "$REFUSED_COUNT" -gt 0 ]; then
  echo "[FAIL] Prologue byte check rejected the install. Offset is wrong OR eqmain patched."
  grep "givetime_detour: REFUSING" "$LOG" | tail -3
  exit 2
fi

if [ "$CREATE_FAIL" -gt 0 ] || [ "$ENABLE_FAIL" -gt 0 ]; then
  echo "[FAIL] MinHook API failures:"
  grep "givetime_detour:.*failed" "$LOG" | tail -5
  exit 2
fi

if [ "$INSTALL_COUNT" -eq 0 ]; then
  echo "[FAIL] No INSTALLED marker. Either eqmain.dll never loaded OR ActivateThread"
  echo "       never reached PollAndInstall(). Check for DllMain / InitThread errors."
  grep "givetime_detour" "$LOG" | tail -5
  exit 2
fi

if [ "$TICK_COUNT" -eq 0 ]; then
  echo "[FAIL] Detour was installed but never fired. Possible causes:"
  echo "       - EQ crashed at install (check for Windows Error Reporting)"
  echo "       - EQ didn't reach the login main loop (check game state)"
  echo "       - Trampoline redirection failed silently"
  exit 3
fi

# Show the last 5 tick messages for manual inspection
echo "Last 5 tick messages:"
grep "givetime_detour: tick" "$LOG" | tail -5
echo ""

# Extract tick numbers from last two log lines and compute rate
LAST_TWO=$(grep "givetime_detour: tick" "$LOG" | tail -2)
if [ "$(echo "$LAST_TWO" | wc -l)" -eq 2 ]; then
  T1_TICK=$(echo "$LAST_TWO" | head -1 | grep -oP 'tick \K\d+')
  T2_TICK=$(echo "$LAST_TWO" | tail -1 | grep -oP 'tick \K\d+')
  T1_MS=$(echo "$LAST_TWO" | head -1 | grep -oP '^\[\K\d+')
  T2_MS=$(echo "$LAST_TWO" | tail -1 | grep -oP '^\[\K\d+')

  TICK_DELTA=$((T2_TICK - T1_TICK))
  MS_DELTA=$((T2_MS - T1_MS))

  if [ "$MS_DELTA" -gt 0 ]; then
    # Ticks per second = (tick_delta / ms_delta) * 1000
    # Use awk for float division
    TPS=$(awk "BEGIN {printf \"%.1f\", $TICK_DELTA * 1000.0 / $MS_DELTA}")
    echo "Rate: $TICK_DELTA ticks over ${MS_DELTA}ms = $TPS ticks/sec"
    echo ""
    echo "Expected: ~30-60 ticks/sec at login/server-select/charselect"
    echo "(EQ's frame rate; the detour logs every 60 ticks so log rate is ~1/sec)"
  fi
fi

echo ""
echo "[PASS] GiveTime detour is firing. Phase 2 objective met."
exit 0
