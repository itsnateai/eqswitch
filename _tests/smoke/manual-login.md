# EQSwitch Manual Smoke Test — Login Pipeline

Manual checklist for regressions in the login chain that can't be caught by
unit tests (real eqgame.exe interaction). Run before any release.

## Phantom-keys regression guard (hotfix v1/v2/v3 lineage)

This is the single highest-value test. It would have caught the original
phantom-keys bug immediately.

1. Launch any account through EQSwitch.
2. Alt-tab to Notepad DURING auto-login.
3. Type several keys in Notepad (random chars + backspace + spacebar).
4. Wait for auto-login to complete or fail.
5. Alt-tab back to EQ.

**Expected:** No keys typed in EQ. No map opened. No inventory opened.
EQ's chat bar empty. Tray log shows clean `Deactivate` after each burst.

**Failure signal:** Any key typed in Notepad appears in EQ (chat bar
contents, inventory toggle, map toggle, movement). This would mean the
IAT foreground gate (hotfix v2) or SHM active-flag gate (hotfix v1)
regressed.

## Happy path — single account

1. Trigger auto-login for a configured account.
2. Expected phase transitions (visible in tray status or log):
   - "Waiting for login screen..."
   - "Typing credentials..."
   - "Submitting login..."
   - "Confirming server..."
   - "Loading character select..."
   - "Entering world..."
   - Login complete

**Expected total time:** Under 45 seconds on a good connection.

## Happy path — parallel team

1. Launch Team 1 (4 accounts).
2. All 4 should reach charselect within 45s.
3. All 4 should enter world within 60s.

**Expected:** No cross-contamination. No stuck account. All 4 windows
end in-game on the correct character.

## Failure mode — bad password

1. Edit one account's password to something incorrect.
2. Trigger auto-login.

**Expected:** User-visible error message within 10 seconds indicating
the login failed. NOT "reached char select but Enter World didn't
register" after 2+ minutes.

**Current status (pre-Phase-6):** EQSwitch does NOT detect bad
passwords — it takes 2-3 minutes and reports a misleading error. This
is tracked in the Phase 6 backlog (wire `login_state_machine.cpp`).

## Failure mode — mid-login process kill

1. Trigger auto-login.
2. While BURST 1 is typing credentials, kill the eqgame.exe process.

**Expected:** Auto-login aborts promptly. Tray status shows the account
name and "lost EQ window". SHM cleanup verified in the log
(`KeyInputWriter: closed mapping for PID ...`).

## Failure mode — MQ2 bridge doesn't initialize

1. Trigger auto-login.
2. Observe if MQ2 bridge "IsMQ2Available" takes longer than 30s.

**Expected:** After 30s, auto-login aborts with user-visible
"MQ2 bridge didn't initialize — stopped at char select" message.

**Hotfix v4 requirement:** This path must NOT fall through to
PulseKey3D / default-character enter-world. That would be a
wrong-character regression.

## Post-login sanity (phantom-keys follow-up)

1. Complete auto-login successfully.
2. Close EQSwitch's tray app.
3. In the in-game chat, attempt to type a message.

**Expected:** Chat works normally. No lost keystrokes, no duplicated
keystrokes. No residual SHM state affecting input.

## Clean-state verification

After any test run, confirm:
- `_activeLoginPids` is empty (no stuck PIDs)
- All `Local\EQSwitchDI8_*` and `Local\EQSwitchCharSel_*` mappings
  have been closed (verify via handle/resource monitor if needed)
- No `EQSwitch.exe` leftover processes
- `eqswitch.log` shows matched `Activate` / `Deactivate` pairs for
  every auto-login burst
