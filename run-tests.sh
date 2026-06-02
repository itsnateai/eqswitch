#!/usr/bin/env bash
# Pre-ship regression gate — runs the 12 headless unit --test-* suites against a Debug build.
#
# Why a script and not the shipped exe: the inline test classes (Core/**/*Tests.cs)
# are deliberately excluded from Release builds (see EQSwitch.csproj:39-41 — they
# would bloat the ~155 MB single-file publish and expose test CLI/stdout in the
# shipped GUI binary). They compile + run only in Debug. The geometry/struct math
# under test is config- and DPI-input-independent, so a Debug pass holds for Release.
#
# RUN THIS BEFORE TAGGING ANY RELEASE. Exit 0 = all green; non-zero = a guard failed.
# This is the gate that was missing when v3.22.82 shipped with 14 red OuterRectMath
# assertions (the #if DEBUG flags were never run on the Release ship path).
set -uo pipefail
cd "$(dirname "$0")"

echo "── Building Debug ──"
dotnet build -c Debug -v quiet || { echo "BUILD FAILED"; exit 99; }

EXE="bin/Debug/net8.0-windows/win-x64/EQSwitch.exe"
[ -f "$EXE" ] || { echo "Debug exe not found at $EXE"; exit 98; }

# The 12 headless, zero-arg RunAll() suites. Program.cs defines 4 more --test-*
# flags intentionally NOT run here: --test-autologin (needs a live eqgame),
# --test-migrate / --test-split (need a JSON fixture arg), --test-update
# (simulates the self-update flow — side-effecting). Run those manually.
SUITES=(
  --test-character-selector
  --test-config-validate
  --test-window-mode-style
  --test-key-input-writer
  --test-shm-layout
  --test-charselect-reader
  --test-outer-rect-math
  --test-window-clamp
  --test-frame-correction
  --test-frame-cache
  --test-swap-cover
  --test-effective-bounds
)

fail=0
for s in "${SUITES[@]}"; do
  echo "── $s ──"
  "$EXE" "$s"
  rc=$?
  if [ $rc -ne 0 ]; then echo "  ✗ $s FAILED (exit $rc)"; fail=1; else echo "  ✓ $s PASS"; fi
done

echo
if [ $fail -ne 0 ]; then echo "PRE-SHIP GATE: FAILURE(S) — do not ship."; exit 1; fi
echo "PRE-SHIP GATE: ALL SUITES PASS."
