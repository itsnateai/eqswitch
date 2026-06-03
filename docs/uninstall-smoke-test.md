# Manual smoke test — Uninstall flow

Covers Settings → Paths → Uninstall and the standalone `uninstall.bat`.
Both paths should funnel through `Core/UninstallHelper.cs` (single source of truth).

## Pre-conditions per scenario

For each scenario, set up these on-disk artifacts inside a Windows Sandbox
(or a throwaway VM) and confirm the post-conditions after running uninstall.

### Scenario 1 — clean install, no artifacts (current era)
**Pre:** EQSwitch.exe + eqswitch-hook.dll + eqswitch-di8.dll alongside the exe.
EQ folder contains Dalaya's real `dinput8.dll` (~1.3MB). Run-at-startup ON
(creates `%APPDATA%\...\Startup\EQSwitch.lnk`). Desktop shortcut created.

**Run:** Settings → Paths → Uninstall → Yes.

**Post:**
- Dalaya's `dinput8.dll` UNTOUCHED (size still ~1.3MB, byte-for-byte equal).
- Startup shortcut deleted.
- Desktop shortcut deleted.
- `eqswitch-config.json` updated with `RunAtStartup: false`.
- Result dialog lists exactly: "Removed startup shortcut", "Removed desktop shortcut",
  plus the trailing "delete the EQSwitch folder yourself" note.

### Scenario 2 — chain-load era artifacts (pre-v3.4.3)
**Pre:** Dalaya's MQ2 dinput8.dll renamed to `dinput8_dalaya.dll` (~1.3MB).
Our old proxy (<200KB) sitting in the `dinput8.dll` slot.

**Run:** uninstall (GUI or .bat).

**Post:**
- `dinput8.dll` (the small proxy) deleted. Logged with byte size.
- `dinput8_dalaya.dll` renamed back to `dinput8.dll`. Hash matches the original
  Dalaya MQ2 (compare to `_srcexamples` reference if available).
- Step 1 handles both restores atomically inside its coexistence branch:
  size-checks `dinput8.dll`, sees `<200KB`, deletes the proxy, then `File.Move`s
  `dinput8_dalaya.dll` over. Step 2's `!File.Exists(dalayaPath)` guard means
  Step 2 doesn't double-process the same files.
- ⚠️ This is the case PR #4's first ultrareview pass (review-4213529313) caught:
  the original Step 1 blindly deleted `dalayaPath` in the coexistence branch,
  which would have wiped Dalaya's live MQ2 and left only the proxy (which
  Step 2 then deleted too, leaving NO `dinput8.dll`). Always exercise this
  scenario after touching Step 1.

### Scenario 2b — chain-load era with MQ2 already restored (orphan case)
**Pre:** Dalaya's MQ2 dinput8.dll in place (~1.3MB) AND a stale
`dinput8_dalaya.dll` still around (also ~1.3MB) from an aborted earlier
restore. Both files coexist but `dinput8.dll` is already legitimate.

**Run:** uninstall.

**Post:**
- `dinput8.dll` UNTOUCHED (>=200KB, presumed legit MQ2).
- `dinput8_dalaya.dll` deleted as the stale orphan.
- Step 1 hits the `>=200KB` branch and chooses the orphan-delete path
  instead of the rename-back path.

### Scenario 3 — legacy `.old` backup only
**Pre:** Dalaya's real `dinput8.dll` in place. `dinput8.dll.old` from a v3.4.2-era
install present alongside it.

**Post:**
- `dinput8.dll.old` deleted.
- Live `dinput8.dll` UNTOUCHED.

### Scenario 4 — legacy proxy in EQSwitch app folder
**Pre:** A pre-v3.4.3 `dinput8.dll` (any size) sitting next to `EQSwitch.exe`.

**Post:**
- App-folder `dinput8.dll` deleted.

### Scenario 5 — legacy registry startup entry
**Pre:** Set `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\EQSwitch` to any value.

**Post:**
- Registry value gone.
- Verify via `reg query` returning ERROR_FILE_NOT_FOUND.

### Scenario 6 — adversarial: another tool's small dinput8.dll
**Pre:** A non-EQSwitch third-party `dinput8.dll` < 200KB in the EQ folder
(e.g., a 50KB legitimate audio override).

**Post:**
- ⚠️ Helper WILL delete it. The size heuristic is intentional — if you have a
  competing small dinput8.dll for a different tool, you must remove it manually.
  This is documented behavior, inherited from the chain-load-era startup
  cleanup that now lives in `UninstallHelper.RestoreLegacyDlls`. If this ever
  bites a real user, gate by signature/version-info instead of size.

### Scenario 7 — race: EQ running during uninstall
**Pre:** eqgame.exe running with eqswitch-hook.dll + eqswitch-di8.dll injected.
Dalaya MQ2's dinput8.dll loaded (it always is — Dalaya patcher requires it).

**Post:**
- `dinput8.dll` UNTOUCHED (>200KB Dalaya MQ2, presumed legitimate).
- Shortcuts deleted normally.
- Hook DLLs remain injected in the running process — uninstall doesn't claim
  to eject them. They'll be unloaded naturally when the user closes EQ.
- This is expected behavior; the "external changes" the dialog promises to
  revert are filesystem and registry artifacts, not in-memory injections.

### Scenario 8 — sandbox parity for `uninstall.bat`
Run `uninstall.bat` inside `Sandbox_*.wsb` against a Sandbox-mounted fake EQ
folder containing each scenario's pre-conditions. Same post-conditions as the
GUI button. The .bat must NEVER delete a >200KB `dinput8.dll`.

### Scenario 9 — `uninstall.bat` delivery (download + self-update)
The whole flow above is moot if the user never has `uninstall.bat`. Verify both
delivery paths (the v3.24.19 self-update fix):

**Fresh download (v3.24.18+):** extract `EQSwitch.zip` (and `EQSwitch-X.Y.Z.zip`)
and confirm `uninstall.bat` is present alongside `EQSwitch.exe`. Both zips are
byte-identical, so checking one is sufficient.

**Self-update (v3.24.19+):**
- Pre: an install whose folder has NO `uninstall.bat` (simulates a pre-bundle
  install). Trigger an in-app upgrade to a release whose zip contains it.
- Post: `uninstall.bat` now exists next to `EQSwitch.exe` (Phase B creates it —
  Phase A's missing-local guard skips the stage). No `uninstall.bat.old` or
  `uninstall.bat.new` left behind after the next launch (cleaned via
  `alwaysCleanup`). No "new binary did not finish init" Warn for `uninstall.bat`
  (it's intentionally not `.ok`-gated).
- Torn-state: kill the process between Phase A and Phase B with `uninstall.bat`
  staged to `.old`. Next launch must restore it (it's in the `updateables`
  recovery set), not silently drop it.

## Post-test verification checklist

- [ ] `git diff` shows only expected changes (config `RunAtStartup: false`).
- [ ] `eqswitch.log` contains expected `Uninstall:` entries with paths.
- [ ] Re-launching EQSwitch after uninstall does NOT recreate the startup
      shortcut (because `RunAtStartup=false` is persisted before result dialog).
- [ ] `dotnet build` clean (no warnings introduced by the helper refactor).
