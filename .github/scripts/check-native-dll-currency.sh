#!/bin/bash
# Native DLL currency gate.
#
# release.yml ships pre-built DLLs from the working tree (it does `cp Native/*.dll`,
# not a C++ rebuild on tag). That means a commit can touch Native/*.cpp source AND a
# release can ship the unfixed prior DLL with a new version sticker — silently.
# This actually happened with c479de1 (slotName info-leak fix in mq2_bridge.cpp
# committed without the rebuilt eqswitch-di8.dll). The fault was caught manually
# during v3.14.5 prep but should not depend on a human noticing.
#
# This gate fails the release if any tracked C++ source for a DLL has a newer
# git-commit timestamp than the DLL itself. Per-DLL source lists are derived
# from Native/build.sh and Native/build-di8-inject.sh.
#
# Run from repo root. Exit 0 = current. Exit 1 = stale.

set -e
cd "$(dirname "$0")/../.."

# Source lists — keep in sync with Native/build*.sh
DI8_SRCS=(
  Native/eqswitch-di8.cpp
  Native/di8_proxy.cpp
  Native/device_proxy.cpp
  Native/key_shm.cpp
  Native/iat_hook.cpp
  Native/pattern_scan.cpp
  Native/net_debug.cpp
  Native/mq2_bridge.cpp
  Native/login_state_machine.cpp
  Native/login_givetime_detour.cpp
  Native/eqmain_offsets.cpp
  Native/eqmain_cxstr.cpp
  Native/eqmain_widgets.cpp
  Native/eqmain_widgets_mq2style.cpp
  Native/hook.c
  Native/buffer.c
  Native/trampoline.c
  Native/hde32.c
)

HOOK_SRCS=(
  Native/eqswitch-hook.cpp
  Native/hook.c
  Native/buffer.c
  Native/trampoline.c
  Native/hde32.c
)

# Header files included by the C++ TUs — also bump DLL timestamp requirement
DI8_HEADERS=(Native/*.h)
HOOK_HEADERS=(Native/*.h)

last_commit_ts() {
  local path="$1"
  git log -1 --format='%ct' -- "$path" 2>/dev/null
}

short_sha() {
  local path="$1"
  git log -1 --format='%h' -- "$path" 2>/dev/null
}

check_dll() {
  local dll="$1"
  shift
  local srcs=("$@")

  if [ ! -f "$dll" ]; then
    echo "❌ FAIL: $dll does not exist"
    return 1
  fi

  local dll_ts=$(last_commit_ts "$dll")
  local dll_sha=$(short_sha "$dll")
  if [ -z "$dll_ts" ]; then
    echo "❌ FAIL: $dll is untracked (not in git)"
    return 1
  fi

  echo "  $dll @ $dll_sha (ts=$dll_ts)"

  local stale=0
  local newest_src_ts=0
  local newest_src=""
  local newest_src_sha=""

  for src in "${srcs[@]}"; do
    [ -f "$src" ] || { echo "    skip $src (missing)"; continue; }
    local src_ts=$(last_commit_ts "$src")
    [ -z "$src_ts" ] && continue
    if [ "$src_ts" -gt "$dll_ts" ]; then
      local src_sha=$(short_sha "$src")
      echo "    ⚠ NEWER: $src @ $src_sha (ts=$src_ts > dll_ts=$dll_ts)"
      stale=1
    fi
    if [ "$src_ts" -gt "$newest_src_ts" ]; then
      newest_src_ts=$src_ts
      newest_src=$src
      newest_src_sha=$(short_sha "$src")
    fi
  done

  if [ "$stale" -eq 1 ]; then
    echo "  ❌ FAIL: $dll is stale. Rebuild via Native/build*.sh + commit before tagging."
    return 1
  fi

  echo "  ✓ $dll is current (newest source: $newest_src @ $newest_src_sha)"
  return 0
}

echo "=== Native DLL currency gate ==="
fail=0

echo ""
echo "[eqswitch-di8.dll]"
check_dll Native/eqswitch-di8.dll "${DI8_SRCS[@]}" "${DI8_HEADERS[@]}" || fail=1

echo ""
echo "[eqswitch-hook.dll]"
check_dll Native/eqswitch-hook.dll "${HOOK_SRCS[@]}" "${HOOK_HEADERS[@]}" || fail=1

echo ""
if [ "$fail" -eq 1 ]; then
  echo "=== FAIL — at least one DLL is stale relative to its sources ==="
  echo ""
  echo "To fix: rebuild the affected DLL(s), commit the binary update,"
  echo "and re-tag. From repo root:"
  echo "  cd Native && bash build.sh && bash build-di8-inject.sh"
  echo "  cd .. && git add Native/*.dll && git commit -m 'chore: rebuild Native DLLs'"
  exit 1
fi

echo "=== PASS — all Native DLLs current ==="
exit 0
