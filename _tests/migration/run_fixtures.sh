#!/usr/bin/env bash
# Phase 1.5 migration test fixtures runner.
# Usage: ./run_fixtures.sh
# Assumes EQSwitch.exe is built at bin/Debug/net8.0-windows/win-x64/EQSwitch.exe
# Requires: jq.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
EXE="$REPO_ROOT/bin/Debug/net8.0-windows/win-x64/EQSwitch.exe"

[[ -f "$EXE" ]] || { echo "ERROR: $EXE missing — run dotnet build" >&2; exit 1; }
command -v jq >/dev/null || { echo "ERROR: jq not installed" >&2; exit 1; }

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

PASS=0
FAIL=0
FAIL_NAMES=()
CURRENT_FIXTURE_OK=1

assert() {
    local name="$1" actual="$2" expected="$3"
    if [[ "$actual" == "$expected" ]]; then
        echo "    ok: $name"
    else
        echo "    FAIL: $name (expected '$expected', got '$actual')" >&2
        CURRENT_FIXTURE_OK=0
    fi
}

migrate() {
    local fixture_path="$1"
    local fixture_name; fixture_name="$(basename "$fixture_path" .json)"
    local work_input="$WORKDIR/$fixture_name.json"
    cp "$fixture_path" "$work_input"
    local win_input; win_input="$(cygpath -w "$work_input" 2>/dev/null || echo "$work_input")"
    "$EXE" --test-migrate "$win_input"
    echo "$work_input.migrated.json"
}

start_fixture() {
    echo ""
    echo "=== $1 ==="
    CURRENT_FIXTURE_OK=1
}

end_fixture() {
    if [[ $CURRENT_FIXTURE_OK -eq 1 ]]; then
        PASS=$((PASS + 1))
    else
        FAIL=$((FAIL + 1))
        FAIL_NAMES+=("$1")
    fi
}

# ── Fixture (a): One Account + One Character with AutoEnterWorld+hotkey ──
start_fixture "fixture_a_one_account_one_character"
F="$(migrate "$SCRIPT_DIR/fixture_a_one_account_one_character.json")"
assert "configVersion=4" "$(jq -r '.configVersion' "$F")" "4"
assert "accountsV4 has 1 entry" "$(jq -r '.accountsV4 | length' "$F")" "1"
assert "Account name=Main" "$(jq -r '.accountsV4[0].name' "$F")" "Main"
assert "Account username=main_user" "$(jq -r '.accountsV4[0].username' "$F")" "main_user"
assert "Account password preserved" "$(jq -r '.accountsV4[0].encryptedPassword' "$F")" "ZmFrZS1jaXBoZXJ0ZXh0"
assert "charactersV4 has 1 entry" "$(jq -r '.charactersV4 | length' "$F")" "1"
assert "Character name=Backup" "$(jq -r '.charactersV4[0].name' "$F")" "Backup"
assert "Character.accountUsername=main_user" "$(jq -r '.charactersV4[0].accountUsername' "$F")" "main_user"
assert "characterAliases has 1 entry" "$(jq -r '.characterAliases | length' "$F")" "1"
assert "characterAlias preserves PriorityOverride=AboveNormal" "$(jq -r '.characterAliases[0].priorityOverride' "$F")" "AboveNormal"
assert "CharacterHotkeys[0].targetName=Backup" "$(jq -r '.hotkeys.characterHotkeys[0].targetName' "$F")" "Backup"
assert "CharacterHotkeys[0].combo=Alt+1" "$(jq -r '.hotkeys.characterHotkeys[0].combo' "$F")" "Alt+1"
assert "AccountHotkeys is empty (no charselect-only binding)" "$(jq -r '(.hotkeys.accountHotkeys // []) | length' "$F")" "0"
assert "Legacy 'accounts' key preserved (downgrade safety)" "$(jq -r '.accounts | length' "$F")" "1"
assert "Legacy 'characters' key preserved (untouched)" "$(jq -r '.characters | length' "$F")" "1"
end_fixture "fixture_a"

# ── Fixture (b): Three CharacterNames sharing one (Username, Server) ──
start_fixture "fixture_b_three_chars_share_account"
F="$(migrate "$SCRIPT_DIR/fixture_b_three_chars_share_account.json")"
assert "accountsV4 has 1 entry (deduped)" "$(jq -r '.accountsV4 | length' "$F")" "1"
assert "Account name=MainAcct (first wins)" "$(jq -r '.accountsV4[0].name' "$F")" "MainAcct"
assert "charactersV4 has 3 entries" "$(jq -r '.charactersV4 | length' "$F")" "3"
assert "Character[0].name=Backup" "$(jq -r '.charactersV4[0].name' "$F")" "Backup"
assert "Character[1].name=Healpots" "$(jq -r '.charactersV4[1].name' "$F")" "Healpots"
assert "Character[2].name=Acpots" "$(jq -r '.charactersV4[2].name' "$F")" "Acpots"
assert "Character[2].slot=3" "$(jq -r '.charactersV4[2].characterSlot' "$F")" "3"
assert "All Characters reference main_user" "$(jq -r '[.charactersV4[].accountUsername] | unique | length' "$F")" "1"
end_fixture "fixture_b"

# ── Fixture (c): One Account, no CharacterName ──
start_fixture "fixture_c_account_no_character"
F="$(migrate "$SCRIPT_DIR/fixture_c_account_no_character.json")"
assert "accountsV4 has 1 entry" "$(jq -r '.accountsV4 | length' "$F")" "1"
assert "charactersV4 is empty (no CharacterName)" "$(jq -r '.charactersV4 | length' "$F")" "0"
assert "Account name=BareAccount" "$(jq -r '.accountsV4[0].name' "$F")" "BareAccount"
end_fixture "fixture_c"

# ── Fixture (d): QuickLogin1=Backup + AutoEnterWorld=true → CharacterHotkeys[0] ──
start_fixture "fixture_d_charname_hotkey_enterworld"
F="$(migrate "$SCRIPT_DIR/fixture_d_charname_hotkey_enterworld.json")"
assert "CharacterHotkeys[0].targetName=Backup" "$(jq -r '.hotkeys.characterHotkeys[0].targetName' "$F")" "Backup"
assert "CharacterHotkeys[0].combo=Alt+1" "$(jq -r '.hotkeys.characterHotkeys[0].combo' "$F")" "Alt+1"
assert "AccountHotkeys did not get this binding" "$(jq -r '(.hotkeys.accountHotkeys // []) | length' "$F")" "0"
end_fixture "fixture_d"

# ── Fixture (e): QuickLogin2=bare_user (Username only) → AccountHotkeys[1] ──
start_fixture "fixture_e_username_hotkey_charselect"
F="$(migrate "$SCRIPT_DIR/fixture_e_username_hotkey_charselect.json")"
assert "AccountHotkeys[1].targetName=BareAccount" "$(jq -r '.hotkeys.accountHotkeys[1].targetName' "$F")" "BareAccount"
assert "AccountHotkeys[1].combo=Alt+2" "$(jq -r '.hotkeys.accountHotkeys[1].combo' "$F")" "Alt+2"
assert "CharacterHotkeys empty (Account-only target)" "$(jq -r '(.hotkeys.characterHotkeys // []) | length' "$F")" "0"
end_fixture "fixture_e"

echo ""
echo "=================================================="
echo "  Migration fixtures: $PASS passed, $FAIL failed"
echo "=================================================="
if [[ $FAIL -gt 0 ]]; then
    echo "Failed fixtures: ${FAIL_NAMES[*]}" >&2
    exit 1
fi
exit 0
