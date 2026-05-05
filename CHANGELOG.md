# Changelog

## v3.15.0 — Track B: char-select name discovery via race-byte filter + name-anchor (2026-05-05)

### Char-select name discovery (the slot-mode → real-name fix)
- **Bridge now finds real character names on Dalaya x86 char-select**, replacing the long-standing `Slot 1` / `Slot 2` placeholder fallback. Two layered paths cover the full account spectrum:
  - **Multi-char accounts (5+ chars):** `HeapScanForCharArray` now validates each candidate entry's `+0x44` race-byte against `[1, 600]` (player race range, covers Drakkin=522 and Froglok=330). Without this filter, Dalaya's race-table at-stride-0x160 won the scan first ("Dracnid", "Wyvern", "Pegasus" all pass title-case validation but their `+0x44` is a heap pointer in the millions). Threshold-5 unchanged.
  - **Single-char accounts (Natedogg, etc.):** new `HeapScanForTargetName` anchor scan — reads target char name from `LoginShm.character` (written by `AutoLoginManager.SendLoginCommand`) and locks onto the exact name in heap, requires `+0x44` race in `[1, 600]` for validation. ~0–1500ms wall-clock budget. Hits on first match.
- **`BAD_NAMES` blocklist expanded to 79 entries** in both `IsPlausibleName` and `IsValidCharArray` — same set across both Path A and Path C predicates so they reject the same heap garbage. Includes UI labels (Username, Password, Settings, Public, Console, …), all 16 EQ player races (Human → Drakkin), all 16 base classes (Bard → Shadowknight), and EQ-flavor strings present in heap (Brave, Storm, Hunter, Zone, Camp, Raid, …).

### State-machine hardening
- **Cache resets fire on every `pinstCCharacterSelect` non-null transition** — previously `g_heapScanDone` reset only on `gameState == 5` (in-game), but Dalaya keeps `gameState == 0` across both login and char-select. Without the transition reset, the v7 Phase 4 standalone heap scan fired during login state (heap empty), set `g_heapScanDone = true` permanently, and locked all heap-scan paths off when char-select actually loaded → bridge stuck on `Slot N` for the whole session.
- **`Path B2` cached-slot-count path now skips `Slot N` placeholder rewrite when `g_heapScanArrayBase != 0`.** Previously each poll re-wrote `Slot 1` over the real name, leaving a brief race window where C# could see `charCount=1 + names[0]="Slot 1"` between Path B2 and the re-read path → c74a766 abort gate trips.
- **Re-read branch re-pins `selectedIndex = 0` when anchor cached.** Without this, Path B2's `GetCurSel` write each poll could drift `selectedIndex` away from slot 0 while `names[0]` still held the anchor's target name → C# Enter World fires against the wrong slot. New `g_anchorScanCached` flag distinguishes anchor-cache (re-pin) from full-array-cache (preserve `GetCurSel`).
- **`g_loginShm->magic` deref now wrapped in `__try`** at both Path C and standalone anchor sites — defends against MMF unmap-during-poll race.
- **`HeapScanForCharArray` wall-clock budget (1500ms)** with `g_heapScanDone = false` reset on budget abort so next poll retries. Counter reports honest pages-per-region instead of region-as-page.
- **`Path A` widened scan** from `OFFSET_CHARSELECT_ARRAY ± 0x200` to `0..0x20000` (full CEverQuest range), with `g_charArrayNotFoundLogged` perf gate so the 32k-iteration scan runs once per char-select cycle, not per 500ms poll.
- **`MQ2Bridge::Shutdown()` defensive resets** for `g_charArrayNotFoundLogged`, `g_heapScanDone`, `g_anchorScanCached`, `g_standaloneDelay` — handles mid-process MQ2 re-init.

### Wiring (C# side)
- **`AutoLoginManager.RunCredentialEntry`** now takes a `targetCharacterName` parameter (default `""`); the caller in `BeginLogin` passes `character?.Name ?? ""`. `LoginShmWriter.SendLoginCommand` writes that into `LoginShm.character`, which the native bridge reads for the anchor-scan target. Empty string preserves the legacy "stop at char-select" path.

### Live verification (2026-05-05)
- **gotquiz/acpots (10-char account):** `heap scan FOUND char array at 0x112D5FD8 (10/10 names valid, race-filtered)` — real names {Acpots, Backup, Healpots, Jonopua, Nate, Potiongirl, Potionguy, Staxue, Thazguard, Zfree} → `selector → name match 'acpots' at slot 1` → `gotquiz logged in!`
- **nate/Natedogg (1-char account):** `anchor scan FOUND 'Natedogg' at 0x01DF10D8 (race=4, 0ms)` → host log `1 characters found: Natedogg` (real name, no `Slot 1` placeholder) → `name match 'Natedogg' at slot 1` → in-world.

## v3.14.12 — Autologin password reliability: primer keystroke + decrypt diagnostics (2026-05-04)

### Autologin
- **Password fully types into the login screen on every launch.** Pre-fix, EQ's input pump dropped the first 1–2 keystrokes after the BURST 1 SHM-active flip, intermittently producing 4-of-6 or 5-of-6 character passwords (silent failure → server kicks the empty-creds connection → EQ exits during charselect load). Fix: a single Backspace primer keystroke is sent immediately after BURST 1 activates, before the password chars. The primer absorbs whatever EQ drops; on an empty password field Backspace is a no-op. Verified across all three launch paths (Characters submenu, Accounts submenu, team1 dual-box).

### Diagnostic logging
- **Decrypted password length surfaced** post-decrypt — WARN on empty/suspiciously-short blobs (catches silent config corruption from a Settings save that re-encrypts an empty password). Length only, never the value.
- **`CombinedTypeString` typed/skipped counters** at entry/exit — now visible in `eqswitch.log` how many chars were written to SHM vs how many were filtered (unmappable / modifier-required). Eliminates ambiguity between "C# bailed on chars" vs "EQ dropped them after they were sent".

### Notes
- This release does not change the v3.14.0/v3.14.11 autologin codepath structurally; the primer is a 2-line surgical addition. `WarmupDwellMs` config knob remains the dwell tunable; default still 4000ms (the primer makes it less critical).

## v3.14.7 — UI polish: Dalaya rebrand, Accounts table restructure, dialog hardening (2026-05-01)

### Rebrand
- **Tray context-menu title** now reads `⚔  Dalaya v#  ⚔` (was `⚔  EQ Switch v#  ⚔`).
- **Settings window title** now reads `⚔  Dalaya Settings  ⚔` (was `⚔  EQSwitch Settings  ⚔`).
- Programmatic identifiers (exe name, mutex, log paths) are intentionally unchanged — user-visible labels only.

### Accounts table restructure
- **Column order** is now `# | Username | Note | Server | Flag` — Username is the pinning identity; the old "Name" column has been renamed "Note" to reflect that it's user-friendly metadata, not a key.
- **Flag column** is reserved for a future per-account login-status indicator (✓/✗/—) — cells are empty in this release.
- **`UseLoginFlag` UI surface fully removed** — the underlying property and autologin code path are unchanged; it's now an internal-only toggle.

### Account Edit dialog
- **Row order** matches the grid: Username → Password → Note → Server.
- **Note is optional** — the previous "Name is required" validation has been removed. Username remains required (it's identity).
- **Server dropdown locked to Dalaya** — `ComboBoxStyle.DropDownList` prevents typing, eliminating silent-typo bugs (e.g. lowercase "dalaya" orphaning characters from accounts via AccountKey identity mismatch).
- **Apply-time validation** updated to match: empty-Username (not empty-Name) now blocks save for hand-edited or imported configs. The Character empty-Name check is unchanged.

### Add Character dialog
- **Warning state** ("No Accounts yet — add one first") rebuilt to match the Accounts-tab card style. Previously sized for the full edit form, the dialog rendered too tall, with a clipped warning label and a half-cut-off Close button.

### Hotkey defaults
- **Multi-Mon toggle default** changed from `Alt+N` to `Ctrl+Alt+N`. Avoids conflict with EQ's Story Window hotkey on fresh installs (existing installs keep their saved binding).

### Bug fixes
- **"Create Desktop Shortcut" button** could get stuck on "Created!" when spam-clicked. The button now disables itself for 2s after each click; re-enable is timer-driven, independent of any code path inside `StartupManager.CreateDesktopShortcut`. No stuck label, no redundant `.lnk` writes.

### Tuning
- **Login-screen-delay clamp tightened** from [1, 15]s to [3, 10]s. The fallback dwell that fires when SHM warmup didn't run; 1s was aggressive enough to hit keystroke-truncation in dual-box scenarios. Default 5s unchanged.

## v3.14.6 — uninstall safety: never touch Dalaya's MQ2 dinput8.dll (2026-05-01)

### Bug fix
- **Settings → Paths → Uninstall and the standalone `uninstall.bat` will no longer touch Dalaya's MQ2 `dinput8.dll`.** A latent bug across both paths could delete Dalaya's live MQ2 core when chain-load-era artifacts (`dinput8.dll` proxy + `dinput8_dalaya.dll` MQ2) coexisted in the EQ folder, leaving the user with no `dinput8.dll` at all and forcing a Dalaya patcher re-run to restore connectivity. Both paths now size-check before deleting: anything ≥200KB is presumed to be Dalaya's MQ2 (~1.3MB) and is left alone; only sub-200KB EQSwitch proxies are removed. The fix is mirrored byte-for-byte across the C# helper, the in-app GUI button, and the standalone `.bat`.

### Hardening
- Uninstall now persists `RunAtStartup=false` *before* shortcut deletion, closing a window where a mid-flight crash could resurrect the startup shortcut on next launch.
- Uninstall now also removes legacy `HKCU\…\Run\EQSwitch` registry entries from pre-shortcut versions and any stale `dinput8.dll` left in EQSwitch's own app folder by pre-v3.4.3 builds.

### Docs
- README now points at the correct Settings tab (Paths, not General) and documents `uninstall.bat` as a fallback for when the GUI won't launch.
- New `docs/uninstall-smoke-test.md` covers eight scenarios including the chain-load coexistence cases that motivated this release.

## v3.14.5 — defensive zero-init in MQ2 SHM bridge (2026-04-29)

### Hardening
- **`MQ2Bridge::Poll` slot-name fallback paths now zero-init the local `slotName[CHARSEL_NAME_LEN]` buffer before `wsprintfA`.** The subsequent `memcpy(shm->names[i], slotName, CHARSEL_NAME_LEN)` copies the full 64-byte buffer into the C#-readable shared-memory region, so any bytes past the NUL terminator written by `wsprintfA` were uninitialized stack contents leaking into SHM. Not exploitable — destination size is fixed and there's no path-length blowup — but unnecessary disclosure of return addresses / local pointers. Two call sites (`mq2_bridge.cpp:3342` and `:3372`) now match the surrounding `={}` zero-init pattern.

### CI (no behavior change)
- Semgrep workflow excludes `Native/` from scans; the `gitlab.flawfinder.*` community rules generated zero-signal noise on x86 RE / MQ2-port code (37 historical findings, all manually triaged as false positives).

## v3.14.4 — self-update: SHA256SUMS now required (close fail-open on missing manifest) (2026-04-29)

### Security fix
- **Self-updater now fails closed when a release has no SHA256SUMS asset.** v3.14.3 fixed the catch-block fail-open inside the integrity-check try, but the outer `if (!string.IsNullOrEmpty(_hashFileUrl))` still skipped verification entirely if the release didn't ship a manifest. As of this version, an empty `_hashFileUrl` aborts the update with a clear error. Combined with the workflow change in v3.14.3 (which always emits `SHA256SUMS`), self-update is now unconditionally fail-closed: every accepted payload has been hash-verified end-to-end.

## v3.14.3 — self-update: fail-closed on hash verification errors + ship SHA256SUMS (2026-04-29)

### Security fix
- **Self-updater no longer fails open on hash verification errors.** The integrity-check `try` block at `UpdateDialog.cs:332-372` previously had a bare `catch { /* proceed without verification */ }` that swallowed any exception from the SHA256SUMS fetch — meaning an MITM that dropped the SHA256SUMS request could silently bypass the hash check entirely. The catch now logs, deletes the partial download, shows an error, and aborts the update. Network blips that take down the hash file fetch will now require a retry rather than installing an unverified binary.
- **Release workflow now generates and uploads `SHA256SUMS`** alongside the zip bundle. Previously the workflow only attached the zip, so the entire hash-verify code path was dead on shipped releases (it required `_hashFileUrl` to be non-empty, which depended on a `SHA256SUMS` asset that was never produced). This is the first release where in-app integrity checking is actually exercised end-to-end.

### Notes
- The first release on which the *upgrading client* will benefit from these checks is the next one after v3.14.3 — clients currently on v3.14.2 or earlier are running old updater code when they pull v3.14.3, so this release bootstraps the chain forward.

## v3.14.2 — Settings: swap Nuclear Reset and Update button positions (2026-04-29)

### UI
- **Update** moved into the persistent bottom toolbar of Settings (next to GitHub) where it's reachable from any tab — was previously buried on the Paths tab.
- **Nuclear Reset** moved out of the bottom toolbar onto the Paths tab (where Update used to live, between Help and Uninstall) — it's a destructive, rarely-used action and now lives alongside the other destructive Paths-tab tool (Uninstall) instead of being one fat-finger away on the always-visible toolbar. Confirm dialog and `_reopenAfterClose` reopen-with-defaults flow unchanged.

## v3.14.1 — dead-code removal: `[Obsolete] LoginAccount` wrapper + `ExecuteQuickLogin` (2026-04-29)

### Removed (no behavior change)
- **`AutoLoginManager.LoginAccount(LoginAccount, bool?)` deleted** — the `[Obsolete]` v3-wrapper that synthesized a v4 `Account` (+ optional `Character`) from a legacy `LoginAccount` row and delegated to `BeginLogin`. All live tray paths route through the intent-explicit `LoginToCharselect(Account)` / `LoginAndEnterWorld(Character)` API as of v3.13.0–v3.14.0 — the wrapper was the last piece of v3 routing scaffolding.
- **`TrayManager.ExecuteQuickLogin` deleted** — the only remaining caller of the obsolete wrapper, itself documented as "now dead code — Phase 5 deletes it per plan" at the time of v3.14.0. No live tray entry routed here.

### Polish
- Four stale doc comments updated: `TrayManager.BuildTeamsSubmenu` no longer claims the team path routes through `ExecuteQuickLogin`; `FireLegacyQuickLoginSlot` no longer references the deleted wrapper at a stale `AutoLoginManager.cs:127` line; `FireTeam`'s doc-block no longer carries the "this method bypasses the dead path" footnote; `AppConfig.FindAccountByName` no longer points at a stale `TrayManager.cs:1321-1322` line range.

### Build
- `CS0618` warning silenced — Release build is now warning-free.

### Retained
- The `LoginAccount` *type* (v3 model class in `Models/LoginAccount.cs`) is unchanged and still consumed by `LegacyAccounts` config storage, the v3→v4 migrator, the splitter, and the Settings reverse-mapper. Only the obsolete *method* of the same name on `AutoLoginManager` was removed.

## v3.14.0 — role color-coded hotkey dialogs + tray AutoLoginN v4 routing (2026-04-29)

### UI consistency
- **Color-coded names in Configure dialogs** for Teams / Characters / Accounts — match the **A** (purple) / **C** (blue) pill scheme established by team-configure. Team rows split into per-slot sub-labels by kind; Character / Account dialogs uniformly tinted. Orphan-dim and stale-warn states retained.
- **Tray-menu noise reduction** — removed `ToolTipText` on five root tray-menu entries (`Launch Client`, `Launch Team`, `Accounts`, `Characters`, `Teams` parents). Per-item submenu tooltips kept.
- **Window Title card tightened** — dropped the "Applied after client is in world" hint; card height shrank from 56 to 40px.

### Submenu hotkey display
- **`LegacyHotkeyLookup` now reads v4 hotkey lists** (`AccountHotkeys` + `CharacterHotkeys`). Was Phase-3-only and silently dropped any hotkey set via the v4 dialogs (e.g. `Natedogg + Alt+I` set in the Characters submenu would not display).

### Tray AutoLoginN v4 routing (`FireLegacyQuickLoginSlot`)
- **`QuickLoginN` empty** → fall back to combined `CharacterHotkeys` then `AccountHotkeys` (populated only), positionally indexed by slot.
- **`QuickLoginN` set but `LegacyAccount` lookup fails** → try v4 Character then Account by Name (case-insensitive on this drift path only; v4-list path stays ordinal). Rescues post-migration case drift.
- **`LogFirstFire` family strings distinguish the four routing paths** so the active path is visible in logs.

### Verified
- Clean Release build + FileVersion `3.14.0.0` 2026-04-29.

## v3.13.0 — UI polish, AutoLoginTeams refactor, position memory (2026-04-28)

### Tray menu
- Accounts submenu now shows **Username** (the unique login) instead of `Account.Name`. Legacy migrations had Name = character name, which collided with the Characters submenu — this removes the ambiguity.
- Teams submenu shows the resolved character/account names per team (e.g. `🚀 natedogg / acpots`) instead of the static `Auto-Login Team N` label.
- Menu hover tooltips can be toggled off via Settings → Video → Preferences → Show Tooltips. Same toggle that gated balloon toasts.
- `FloatingTooltip` now word-wraps long messages (max width 480px) so multi-account warnings don't run off the screen.

### Settings dialogs
- **Position memory** for 8 dialogs across the workspace: Account/Character/Team Hotkeys (Configure), Account/Character Edit (Add + Edit share), AutoLoginTeams, EQClientSettings, ProcessManager. Each remembers its last-open location for the rest of the session.
  - Bug fix in `DarkTheme.StyleForm`: the helper was clobbering callers' `FormStartPosition.Manual` with `CenterScreen`. Now preserves both `CenterParent` and `Manual`.
- **Paths tab** Startup card restored to original padding (x=47), single row layout for `Create Desktop Shortcut` + `Run at Startup`. `Show Tooltips` moved to Video → Preferences (paired with the Tooltip Duration knob it gates).
- **Hotkeys tab** gained a header-less, full-width `Client Launch Delay` aside at the bottom (moved from Video → Preferences).
- **Video tab** Preferences renamed `Tooltip Delay:` → `Tooltip Duration:` (the value is the auto-dismiss interval, not a hover delay). Range tightened to 100–5000ms with config Validate clamp matched.
- **Update dialog** compacted from 420×210 → 320×152, symmetric 20px top/bottom pads, buttons centered under the status text. Single-button OK states (winget / error / up-to-date) also centered.
- **Account / Character Hotkeys** Configure dialogs gained intent hints: `Will load to Character Select` and `Will load into game` respectively.
- **Team Hotkeys** Configure dialog row labels now show the team's resolved contents (`Team 1 — natedogg / acpots`) with `AutoEllipsis` for long names. Shrunk back to 400px wide after the destination suffix was dropped (see AutoLoginTeams refactor below).
- **Characters table** `HK` column → `Hotkey`, content is now a centered green ✓ when bound (with the full combo on the cell tooltip) instead of a truncated combo string.
- **`Trim Now`** button no longer fires a pre-work "Trimming log files…" popup; only the result MessageBox.

### AutoLoginTeams dialog (significant refactor)
- **Per-team `Enter World` toggle removed.** Destination is now dictated by slot kind alone:
  - `Character` slot → `LoginAndEnterWorld(character, null)` → enters the game world.
  - `Account` slot → `LoginToCharselect(account)` → stops at character select.
  - Mixed teams (Account + Character) get a mix; each slot follows its kind.
  - To stop a character at charselect, put the backing Account in that slot instead.
- `Team{N}AutoEnter` fields removed from `AppConfig` (System.Text.Json silently ignores the unknown JSON keys on load; next save drops them from the file).
- `ResolveTeamConfig` and `FireTeam` simplified to drop the `teamEntersWorld` parameter; `BuildTeamTooltip` no longer adds a `[force enter world]` line.
- **Pill colors changed from value-judgment to neutral.** Was `✓-green` (Character) / `!-yellow` (Account) which read as "correct/warning"; now `C-on-blue` (Character) / `A-on-purple` (Account) — both kinds are valid, no implied right/wrong. Unresolved still red `✗` since it IS an error state. Legend updated to `C = Character    A = Account    ✗ = unresolved`.
- **Pill tooltips reworded** descriptive instead of prescriptive — Account pill no longer says "Pick a Character to enter world instead", just states the constraint.
- **Form shrunk** 560 → 480 wide after the Enter World column came out.
- **Buttons no longer clipped** at the bottom (form was 210 tall; buttons at y=184 + 30px height = ended at 214). Form is now 254 with symmetric 18px top/bottom pads and the warning label gets its own row above the buttons.

### Engineering notes
- Two-round agent verifier sweeps over the major refactor and the language pass — caught two pill-tooltip drift issues (Character / Account) that survived the first pass, plus the `UpdateDialog.ShowError` button position that the dialog-shrink `replace_all` missed.
- Memory note saved on `CharacterSelector.Decide` precedence: `Slot ≥ 1` overrides Name lookup entirely (`Case 3`); `Slot = 0` is the only state that uses name-based heap lookup. Empirical inspection confirmed Nate's config has been running on slot-based selection (all three Characters had non-zero slots), so name-based has never been tested in his prod environment. Decision: leave Slot field as-is for now; revisit as a focused change with a dual-box smoke test.

## v3.12.1 — iter-12 MQ2-style structural lookup foundation (dormant) (2026-04-26)

### Changed
- No user-visible behavior change vs v3.12.0. Both clients enter password and reach in-world cleanly, ~63s end-to-end (verified dual-box 2026-04-26).

### Added (dormant, default-off)
- `Native/eqmain_widgets_mq2style.{h,cpp}` — MQ2-style structural recursion through `CXWnd`'s TListNode + TList multiple-inheritance layout. `FindLiveScreenByName` + `RecurseAndFindName` + `FindChildByName` with heuristic `CStrRep` CXStr name match. Wired through `FindLivePasswordCEditWnd` (in `eqmain_widgets.cpp`) and the `LOGIN_ConnectButton` lookup (in `login_state_machine.cpp`) with legacy heap-cross-ref fallback.
- `kMQ2StyleWidgetLookup = false` master toggle in the new header. Both call sites skip MQ2-style entirely; behavior matches v3.12.0 baseline.

### Pinned offsets (foundation for future)
- `CXWnd::pNext` `+0x08` (TListNode<CXWnd> base, runtime-validated).
- `CXWnd::pFirstChild` `+0x10` (TList<CXWnd> base, runtime-validated).
- `CXWnd::dShow` `+0x196` (slot 68/69 ICF body — `IsVisible() && !IsMinimized()`).
- `CXWnd::Minimized` `+0x1CE` (free byproduct of slot 68/69).
- `CSidlManagerBase::XMLDataMgr` `+0x144` (CXMLDataManager-base offset within the contained `CXMLParamManager`).

### Known (do not enable kMQ2StyleWidgetLookup without redesign)
- iter-12's MQ2-style walks invoke `IterateAllWindowsPublic` from `LoginStateMachine::Tick` (via the `LoginController::GiveTime` detour), which runs on EQ's game thread. That same thread services `IDirectInputDevice8::GetDeviceState`, the path delivering SHM-injected BURST keystrokes. With the toggle on, the background client's `GetDeviceState` polling stalled while the walk was in flight, dropping password keystrokes. Foreground client has a Win32 keyboard fallback path that bypasses `GetDeviceState`, so it landed clean — hence deterministic foreground-OK / background-fail. Confirmed by 4 dual-box test runs and 3 independent code-review agents at 75% confidence. Toggle off restores v3.12.0 behavior.
- Future v6 design (Combo G primary): direct memory write to `InputText` CXStr at `+0x1A8`, skip BURST keystrokes entirely. Foundation laid by these pinned offsets + the dormant MQ2-style code.

## v3.12.0 — ~13s faster dual-box, 3x faster typing, sync-context P0 (2026-04-25)

### Performance
- **Wait `phase >= ClickingConnect` (was `WaitConnectResponse`)** — Dalaya advances to ClickingConnect ~2s after SendLoginCommand; the broken Connect button never advances to WaitConnectResponse, so the previous gate spent the full 15s timeout for nothing. Cuts ~13s off the dual-box happy path.
- **New `WarmupDwellMs` config (default 4000ms)** replaces the flat 5s `LoginScreenDelayMs` in the SHM-warmup path. `LoginScreenDelayMs` is kept as a fallback only.
- **`CombinedTypeString` per-character timing tightened** — 80ms→25ms, 50ms→15ms, shift-up 40ms→15ms. A 6-char password now takes ~240ms total (was ~780ms) — paste-like at 60fps.
- **`FireTeam` honors `Client Launch Delay`** (Settings → Video → Client Launch Delay, default 1s) between team slots. Previously the autologin team-fire path called `LoginAndEnterWorld` in a tight loop with 0ms gap, racing Dalaya's auth gate when concurrent BURST 1 submits landed within ~30ms.

### Refactor
- **Extracted `RunCredentialEntry` from `RunLoginSequence`** — single method with honest docs: warmup → dwell → BURST 1 → cancel. Removes the dual `shmDidCredentials` dance.
- **New `LoginCredentialsSent` event** fires after BURST 1 deactivate. `TrayManager` now applies slim-titlebar + hook config + window title at T+~7s instead of T+~30s (was waiting on charselect-ready). `LoginComplete` is kept as the idempotent end-of-sequence; both call the shared `ApplyDeferredCosmetics(pid)`.
- **`SendCancelCommand` fires BEFORE BURST 1** (was AFTER for one mid-iteration that caused truncation — the DLL's `PHASE_CLICKING_CONNECT` loop was polling `MQ2Bridge::ClickButton` concurrent with typing, contending for EQ's message pump).

### Fixed (P0 — pre-existed but the new event widened exposure)
- **Sync context late-bind** — `TrayManager.Initialize()` now installs the WinForms `SynchronizationContext` post-NotifyIcon and propagates it to `AutoLoginManager` via a new `SetUiContext()`. Previously `_syncContext` captured pre-`Application.Run` was null; events fell into the synchronous-fire branch on background threads, racing `TrayManager._injectedPids` and other UI state.
- **`FireTeam` ShowWarning marshal** — `ShowWarning → DeferToNextTick → WinForms.Timer` construction MUST happen on the UI thread. Now wrapped in `_uiContext.Post` inside the `Task.Run` lambda. Previously silently broken (timer never ticked) when no team slots were assigned and `FireTeam` was running on the threadpool.

### Verified
- Clean Release build + dual-box smoke 2026-04-25 ("SLICKED!!!").

## v3.11.3 — Combo G read-back + ConnectButton vtable gate + ~6s autologin (2026-04-25)

### New
- **Combo G CStrRep_Dalaya layout corrected** (`Native/eqmain_cxstr.h`) — utf8 verified at +0x14 via runtime hex dump; introduced `ownerPtr` field at +0x10 to document the eqmain-internal pointer that lives there. Live recon supersedes the 2013 disassembly comment.
- **`WriteEditTextDirect` read-back verification** (`Native/eqmain_cxstr.cpp`) — after `ConstructFromCStr` succeeds, the written CStrRep's `length` and first utf8 byte are verified against what was requested. Returns false (callers fall back to keystroke) on any mismatch. **Caught a real silent-success bug** where the function reported success while writing into the wrong widget memory.
- **`PHASE_CLICKING_CONNECT` vtable gate** (`Native/login_state_machine.cpp`) — `MQ2Bridge::FindWindowByName` returns a CXMLDataPtr def (vtable = eqmain DOS header) when no live `LOGIN_ConnectButton` widget exists. Pre-fix, the DLL called `MQ2Bridge::ClickButton` on the def, which silently early-returned, and the state machine advanced phase regardless. Now gated on `EQMainOffsets::IsEQMainButtonWidget`; if not a real `CButtonWnd`, retry up to 50 times then `SetError` so C# falls back loudly. Counter resets in `InvalidateWidgets` so a fresh login attempt starts clean.
- **C# SHM credentials warmup ritual** (`Core/AutoLoginManager.cs::RunLoginSequence`) — sends `LOGIN` SHM command and waits up to 15s for `phase >= WaitConnectResponse`. On Dalaya phase never advances past `ClickingConnect` (no live button), so the 15s timeout always fires — but the DLL's widget-discovery activity during that window warms up EQ's input subsystem so BURST 1 keystrokes land cleanly. Then BURST 1 runs unconditionally.
- **`g_password` redacted from DLL log** (`Native/eqmain_cxstr.cpp`) — `WriteEditTextDirect` now logs `textLen=N` + first-byte hex only, never the full string. Earlier diagnostic logged `text="<redacted-6-char-password>"` (real password — original plaintext name elided 2026-05-04) into the DI8 log file.

### Performance
- Autologin landed at ~6s wait → in-world per dual-box test 2026-04-25. (Was timeout-bound around 35-50s previously.)

### Known limitations (next session)
- Combo G writes to `+0x1A8 InputText` successfully, but EQ renders/submits from a different buffer — direct SHM password injection still doesn't work end-to-end on Dalaya. BURST 1 keystrokes are the actual workhorse.
- Two parallel autologin paths (SHM warmup ritual + BURST 1 keystrokes) is confusing; warmup needs to be repurposed or replaced with a non-credential-attempt mechanism.

## v3.11.2 — autologin documentation honesty + load-bearing-warmup discovery (2026-04-25)

### Fixed
- Stale `LoginShm overall timeout (14s)` log message corrected to `(45s)` —
  the timeout was bumped to 45s in iter 15.2 but the message text was never
  updated, leading to false impressions when reading logs.
- Stale `// PATH A: ... DISABLED — native widget discovery needs a dedicated
  RE session.` comment block replaced with current reality + a
  ⚠ LOAD-BEARING SIDE EFFECT ⚠ warning. Combo G fixed widget discovery; the
  broken piece is now the DLL's post-connect detection. **PATH A's 45s
  timeout, although the "intended" login flow never completes, is incidentally
  serving as the warmup that PATH B's keystroke injection requires** — without
  it, BURST 1 fires at T+10s and EQ drops the first ~3 keystrokes (verified
  2026-04-25 by attempting C# disable, password truncated 6→3 chars, login
  failed, rolled back).

### Notes
- No behavior change vs v3.11.1 — only comments and one log message.
- The "skip PATH A entirely" win identified during analysis turned out to
  need a non-time-based BURST-1 readiness gate, not just commenting-out the
  if-block. Tracked as a future "D" task.

## v3.11.1 — `\` switch key now EQ-window-only (2026-04-25)

### Fixed
- **`\` (SwitchKey) was firing globally** — any press in chat, Discord, browsers, etc. was being swallowed by the keyboard hook. Now scoped to "EQ client window must be foreground" via the existing `processFilter` path. `]` (GlobalSwitchKey) remains genuinely global, as designed.
- Removed the cold-start "no EQ focused → focus first client" branch from the primary path of `OnSwitchKey` (left in as a defensive no-op). The previous EQ-only filter had been temporarily removed on 2026-04-24 to work around a broken-autologin foreground race; that race is gone now that autologin lands EQ as foreground end-to-end.

## v3.10.0 — GPL-2.0-or-later + Native v7/v8 login path (2026-04-18)

### Changed
- **License changed from GPL-3.0 to GPL-2.0-or-later** — ecosystem alignment with MacroQuest (MQ2) and the broader EverQuest tool community, which is uniformly GPLv2-only. GPL-2.0-or-later is upward-compatible with GPLv3 for anyone who wants it, while unlocking legitimate code-sharing with MQ2-derived work. Prior releases (v3.9.3 and earlier) remain GPL-3.0 forever. Tag `v3.9.3-last-gplv3` marks the relicense boundary.
- **README License section** — formal attributions added for **Stonemite** (DirectInput proxy approach studied, no code taken) and **MacroQuest** (character-select facts referenced, no source compiled in). SHM boundary between EQSwitch and MQ2 DLL reaffirmed.
- **README fan-made disclaimer** strengthened — EQSwitch is free, educational, independent, and not sold.
- **CONTRIBUTING.md** — contributor license grant updated to GPL-2.0-or-later.

### Native login reliability (v7)
- **GiveTime detour** replaces SetTimer-based polling — login state machine now rides the game's own game-loop tick (50–130 Hz), matching the MQ2 pattern for stable, high-frequency polling.
- **`LoginController*` fast-path** — when the controller pointer is already resolved, subsequent logins skip the full scan.
- **Charselect robustness** — detects `eqmain.dll` unload at character select and resumes the background poll instead of bailing.
- `LoginShmWriter` wired into the native path.

### Native widget discovery (v8, internal foundation)
- MQ2-style `eqmain.dll` detection with widget-ownership tracking.
- Corrected `SetWindowText` vtable slot + exact-vtable class gate for login-widget match.
- `HeapScanForWidget` — locates login widgets by SIDL name on the heap.
- Live `CXWnd` discovery: tree walk, heap cross-reference, label search.
- Improved `CXWndManager` diagnostics.

### Rationale (relicense)
Sole-author relicensing (verified via `git log`). No external contributors held copyright on any EQSwitch code. No user-visible behavior change from the relicense itself.

---

## v3.9.3 — Release Polish (2026-04-13)

### Changed
- Version bump for public release following v3.9.2 security hardening.

---

## v3.9.2 — Native Upgrade, WinGet Compat, Security Hardening (2026-04-13)

### Added
- **SHM-driven enter-world** — in-process `CLW_EnterWorldButton` click via shared-memory request/ack handshake (replaces earlier PostMessage approach for this step).
- **Charselect slot probe** — runtime validation that the resolved slot matches the user's intended character before commit.
- **Vtable guard around `GetChildItem`** — defensive check before calling into MQ2-exported thunks.

### Changed
- **WinGet-compatible distribution** — packaging adjustments for smooth WinGet manifest submission.
- **Security hardening for distribution** — installer / update path review, string scrubbing, no secrets in binaries.

### Fixed
- **Log-spam reduction** in native `NetDebug` output during charselect polling.

---

## v3.9.0 / v3.9.1 — Per-Account AutoEnterWorld + Naming Cleanup (2026-04-12 / 2026-04-13)

### Added
- **Per-account and per-team `AutoEnterWorld` flag** — granular control over which accounts auto-commit to character select vs. stop at the character screen.
- **DLL verification report** (`Native/VERIFICATION.md`) — independent reverse-engineering evidence for MQ2 export offsets used on Dalaya.
- **Volatile cross-thread fields** in native login state machine (C++ memory-model correctness fix surfaced by 9-agent audit that fixed 8 bugs).

### Changed
- **`LaunchTwo` → `LaunchAll`** terminology cleanup. Config migrated v2 → v3 (duplicate `LaunchTwo` removed).

---

## v3.6.0 — UI Polish, Log Trimming, Account Backup (2026-04-10)

### Added
- **Log trimming** — async stream-based trimmer with archive to `Logs/archive/`. Default threshold 50 MB; configurable.
- **Account backup / import** — DPAPI blobs portable across imports on the same Windows user.
- **Team submenu rework** — `LaunchTwo` for bare clients, Launch Team restored, all 4 teams in tray submenu.

### Changed
- **Hotkeys / Video / Accounts tabs** — card padding, conflict warnings, Windowed Mode relocated to Window Style card.

### Fixed
- **Native vtable validation** before `GetChildItem` call (prevents crash on Dalaya's variant EQ client).
- **Retry counter** for charselect window search with bounded cap (was unbounded busy-wait).
- **Lazy MQ2 init** — replaces blocking `Sleep(2000)` startup delay.
- **MemoryBarrier before SHM charCount write** — ordering fix for cross-thread reads.

---

## v3.5.0 — Background Input & 3-Layer Activation Defense (2026-04-09)

### Fixed
- **Background auto-login works end-to-end while EQ is unfocused.** Root cause was an inline `GetForegroundWindow` hook in `iat_hook.cpp` that only spoofed for callers within `eqgame.exe`'s address range — EQ's game loop calls from loaded DLLs fell outside that range. Three-layer fix:
  1. Inline hooks skip the caller check when SHM is active, so `GetForegroundWindow` / `GetFocus` / `GetActiveWindow` all return EQ's HWND.
  2. Persistent WndProc subclass blocks `WM_ACTIVATEAPP(FALSE)` / `WM_ACTIVATE(WA_INACTIVE)` / `WM_KILLFOCUS` / `WM_NCACTIVATE` with a 16 ms re-install timer.
  3. Activation blast on re-install after EQ's 3D char select overwrites the subclass.
- **Unconditional 200 ms re-post** of `WM_ACTIVATEAPP(1)` while SHM active (old self-check was defeated by the hook's own spoofing).
- `CallWindowProcA` → `CallWindowProcW` for Unicode compatibility.

---

## v3.4.3 — Suspended-Process Injection Architecture (2026-04-08)

### Changed
- **Replaced `dinput8.dll` proxy with CREATE_SUSPENDED process injection.** EQSwitch now injects `eqswitch-di8.dll` and `eqswitch-hook.dll` directly into `eqgame.exe` after resuming the loader (~50 ms). Dalaya's 1.3 MB MQ2 `dinput8.dll` stays untouched — no patcher conflicts, no server hash validation failures.

### Added
- **Character select Enter World** uses 250 ms key holds with 3 retry attempts and real title-change verification.
- **`ActivateThread`** continuously re-posts `WM_ACTIVATEAPP(1)` while SHM active to defend against focus loss.
- **Adaptive `WaitForScreenTransition`** — replaces fixed 3 s post-server-select sleep; polls `IsHungAppWindow` + `GetWindowRect` stability and handles any load time (5 – 90 s).

### Removed
- Dead proxy files (prior `dinput8.dll` proxy architecture).

---

## v3.4.2 — Self-Updater Completeness + Config Migration Framework (2026-04-08)

### Added
- **Self-updater handles all shipped files** — `EQSwitch.exe`, `eqswitch-hook.dll`, and `dinput8.dll` (previously missed `dinput8.dll`).
- **`--test-update` CLI flag** for simulating full update flow locally without a GitHub release.
- **Post-update toast notification** on relaunch.
- **`ConfigVersionMigrator`** — versioned framework that transforms raw JSON before deserialization; preserves user settings across property renames and type changes.

### Fixed
- **Retry logic for `.old` artifact cleanup** (race with memory-mapped exe).
- **CTS dispose race** in `UpdateDialog`.

---

## v3.4.0 — CI / Release Pipeline Cleanup (2026-04-05)

### Changed
- **Removed broken framework-dependent build from CI** — Release artifacts are self-contained single-file only.
- **Native DLLs bundled into release zip** — `eqswitch-hook.dll` (and later `eqswitch-di8.dll`) ship alongside the exe.

---

## v3.3.1 — Config Baseline & Defaults Overhaul (2026-04-04)

### Fixed
- **AppConfig defaults baselined to EQ's eqclient.ini** — nuclear reset now matches a fresh EQ install (22 booleans, 3 clip planes, mouse sensitivity, sound volume)
- **INI section targeting corrected** — ChatSpam writes to [Options] not [Defaults], Keymaps writes to [KeyMaps] not [Defaults], Particles routes FogScale/LODBias/SameResolution to [Options]
- **11 phantom [Defaults] writes removed** — Sky, BardSongs, Anonymous, ClipPlane, MouseSensitivity, ShadowClipPlane, ActorClipPlane and 4 others were being injected into [Defaults] where they don't belong; now write only to [Options]
- **LoadFromIni reads [Options] section** — settings that live in [Options] (EQ's runtime-authoritative section) are now read correctly on form open
- **SlowSkyUpdates EnforceOverrides** — now restores EQ default (3000ms) when unchecked instead of leaving 60000
- **SkyUpdateInterval ApplyToIni** — falls back to 3000 when no original value was captured
- **MouseSensitivity/SoundVolume LoadFromIni clamp** — minimum changed from 0 to -1 to preserve sentinel values
- **ForceWindowedMode** — now reads from [Defaults] in addition to [VideoMode]
- **DisableEQLog** — moved from AppConfig to EQClientIniConfig; LoadFromIni now reads Log key; ApplyToIni now writes it
- **ConfiguredKeys sentinel tracking** — numeric fields at sentinel values (-1 or 0) are removed from ConfiguredKeys instead of being tracked but never enforced
- **Maximized ConfiguredKeys gap** — now tracked when saved from SettingsForm
- **ProcessManagerForm FPS ConfiguredKeys** — MaxFPS/MaxBGFPS now tracked when saved from Process Manager
- **ChatSpamForm EnforceOverrides** — safe int serialization (`value != 0 ? "1" : "0"`) instead of raw `value.ToString()`
- **ModelsForm phantom writes on load failure** — _initialValues snapshot moved outside try block
- **Snapshot early-return bypass** — all 4 sub-forms restructured from `if (!exists) return` to `if (exists) { try...catch }` so snapshots run unconditionally
- **VideoModeForm XOffset/YOffset defaults** — changed from 1 to 0 (EQ default)
- **GDI font leak coverage** — added DisposeControlFonts to EQChatSpamForm, EQVideoModeForm, EQClientSettingsForm, FirstRunDialog, ProcessManagerForm, SettingsForm

### Changed
- ChatServerPort writes to [Options] (was [Defaults], key doesn't exist in fresh ini)
- Doc comments updated to accurately describe EQ defaults and section locations

## v3.1.0 — DirectInput Proxy, Background Auto-Login & Hook Upgrades (2026-04-01)

### Added
- **DirectInput proxy DLL** (`Native/dinput8.dll`) — IAT hook proxy that intercepts `GetForegroundWindow`, `GetAsyncKeyState`, and `GetKeyboardState` inside eqgame.exe. Per-PID shared memory injects scan codes into EQ's DirectInput keyboard device without stealing focus.
- **Background auto-login** — Types passwords into background EQ windows via DirectInput shared memory injection. True one-click multi-account login with no focus stealing.

- **SetWindowTextA hook** — Custom window titles now persist through zone transitions, login, and character select. The injected DLL intercepts EQ's own SetWindowTextA calls and substitutes the configured title. Same approach as WinEQ2.
- **ShowWindow hook** — Blocks EQ from minimizing itself on focus loss during DirectX init. Fixes the Maximize-on-Launch + no-Slim-Titlebar crash where EQ would get stuck minimized.
- **Auto hook injection** — Hook DLL now injects whenever any hook feature is needed (custom window title, maximize protection, or slim titlebar), not just when slim titlebar + hook is toggled on.
- **Video Settings description** — Added page description and "Monitor Selection" section title for clarity.
- **Resolution hint** — Yellow hint in Window Style card when slim titlebar is disabled, reminding to set EQ resolution to fit above the taskbar.
- **Help form auto-login section** — Documents background login status and dinput8.dll requirement.

### Fixed
- **Auto-login typing** — Switched from VK+scancode to KEYEVENTF_UNICODE for reliable text entry on EQ's login screen. FocusAndSendKey re-focuses before each keystroke to survive focus theft.
- **Hook injection during login** — DLL injection and slim titlebar guard are deferred until login sequence completes, preventing focus theft mid-login.
- **Window title not applied on discovery** — Titles now appear immediately when EQ is detected, not just during explicit arrange operations.
- **Build: TestInput sub-project conflict** — Excluded TestInput/ from default compile globbing to prevent duplicate assembly attribute errors.

### Changed
- Hook DLL shared memory struct extended with `blockMinimize` flag and 256-byte `windowTitle` buffer (284 bytes total, up from 24).
- **License changed to GPL-3.0** — Matches Stonemite's license (studied their DirectInput proxy approach).

---

## v3.0.1 — Per-Process Hook Shared Memory & Audit Fixes (2026-04-01)

### Changed
- **Per-process shared memory** — Each injected eqgame.exe gets its own memory-mapped file (`EQSwitchHookCfg_{PID}`) instead of a single global mapping. Hook DLL now works in both single and multimonitor modes with correct per-window positioning.
- **Atomic config writes** — `ConfigManager.FlushSave` writes to a temp file then `File.Move` to prevent config corruption on crash.

### Fixed
- **Hook configs not updated after ArrangeWindows/ToggleMultiMonitor** — Hook DLL would snap windows back to stale positions after "Fix Windows" or mode toggle.
- **Hook configs not updated after SwapWindows** — Multimonitor swap would be immediately undone by the hook.
- **DllInjector handle leaks** — `hThread` handles in both `Inject()` and `Eject()` moved into `finally` blocks.
- **DllInjector.Eject dead code** — Removed unused `allocAddr`/`VirtualFreeEx` and stale `ResolveLoadLibraryA` call.
- **GetExportRva missing guards** — Added `-1` checks after `RvaToFileOffset` calls for clearer PE parse errors.
- **HookConfigWriter resource leak** — `Open()` catch path now disposes both `MemoryMappedFile` and `ViewAccessor` on failure.
- **HookConfigWriter.Disable zeroed geometry** — Now read-modify-writes to only flip the `Enabled` flag.
- **Dead-PID injection race** — Timer tick guards against injecting into a process that died during the 2s delay.
- **Missing client early-return** — `UpdateHookConfigForPid` logs and returns when PID not in client list.
- **Stream leak in LoadIcon** — `GetManifestResourceStream` now disposed with `using`.
- **SettingsForm font leaks** — Inline fonts on labels and DataGridView header tracked and disposed.
- **Double-dispose of foreground debounce timer** — Removed redundant dispose in `TrayManager.Dispose`.
- **PID naming contract** — Cast to `uint` for shared memory name to match C++ `%lu` formatting.

---

## v3.0.0 — DLL Hook Injection, Auto-Login & PiP Overhaul (2026-04-01)

### Added
- **DLL hook injection** (`Core/DllInjector.cs`, `Native/eqswitch-hook.dll`) — Injects a native MinHook-based DLL into eqgame.exe that hooks `SetWindowPos` and `MoveWindow`. Enforces window position/style via shared memory-mapped config (`HookConfigWriter.cs`). Prevents EQ from fighting window management.
- **DPAPI-encrypted auto-login** (`Core/AutoLoginManager.cs`, `Core/CredentialManager.cs`) — Account presets with username, encrypted password, server, character name, and slot. Full enter-world automation via `SendInput` on a background thread. Credentials encrypted with `DataProtectionScope.CurrentUser` — only the same Windows user on the same machine can decrypt.
- **Login Accounts model** (`Models/LoginAccount.cs`) — Stored account presets for auto-login with name, username, encrypted password, server, character, slot, and login flag toggle.
- **PiP orientation support** — PiP overlays adapt to window orientation and layout changes.
- **Hook config shared memory** (`Core/HookConfigWriter.cs`) — Memory-mapped file (`EQSwitchHookCfg`) shared between C# host and injected DLL. Struct-matched layout (packed, sequential ints) for target position, style, and enable flag.
- **Native hook source** (`Native/`) — Full MinHook source (buffer, trampoline, HDE32/64 disassembler) plus `eqswitch-hook.cpp` with build scripts for MSVC and MinGW.

### Changed
- **Settings expanded** — New Auto-Login tab with account management, credential encryption, and launch integration.
- **PipOverlay enhanced** — 87 lines added for orientation-aware thumbnail rendering.
- **TrayManager expanded** (982 → 1549 lines) — Auto-login menu integration, DLL injection lifecycle, hook config management.
- **SettingsForm expanded** (734 → 1211 lines) — Auto-login account editor, DLL hook controls, PiP orientation settings.
- **README updated** with new feature descriptions.

### Removed
- **Unit test project** (`EQSwitch.Tests/`) — Removed during architecture transition. Tests covered v2.x patterns that no longer apply post-DLL injection.
- **Solution file** — Simplified to single-project build.
- **PLAN_DLL_HOOK.md** — Planning doc removed after implementation.

---

## v2.9.1 — Settings & Launch Cleanup (2026-03-30)

### Changed
- **Tray clicks simplified** — Removed triple-click entirely. Left button: single + double click. Middle button: single + triple (via click counting — `MouseDoubleClick` doesn't fire for middle on `NotifyIcon`).
- **Launch is bare-bones** — Removed `EnforceOverrides`, `EnforceWindowedModeIfBorderless`, `PositionOnTargetMonitor`, and post-launch `ArrangeWindows`. Launch just starts `eqgame.exe` with staggered delay. Added restore-if-minimized after 3s.
- **Settings UI cleanup** — Removed CtrlHoverHelp (unreliable in overflow tray). Human-readable switch mode labels ("Swap Last" / "Cycle All"). Tray Click Actions card redesigned. Preferences card alignment fixed.
- **Paths tab auto-open** — Clicking GINA or Dalaya Patcher in launcher menu opens Settings → Paths tab if path not set.
- **Tooltip Delay** — Renamed, supports 0 = disabled.
- **Multi-Monitor Mode checkbox** in Video Settings synced with config.

### Fixed
- **eqclient.ini corruption** — `EnforceOverrides` was writing `Maximized=1` and offsets=-8 on every launch, causing windows to minimize. Removed from launch path entirely.
- Hotkeys tab overlapping labels in Actions card — clean 2-column grid.

---

## v2.9.0 — UI Consolidation & Multi-Monitor (2026-03-30)

### Added
- **Multi-monitor video settings** — Monitor picker, per-monitor resolution, position preview.
- **Config backup restore** — Restore from any of the 10 backup rotations.

### Changed
- **Tabs consolidated** — Merged Performance + Launch into Hotkeys tab. Reduced from 8 to 6 tabs.
- **Stacked fullscreen as default layout** — Clients stack on top of each other, arranged in stacked mode.
- **FPS writes to [Options] section** — Correct INI section for MaxFPS/MaxBGFPS.
- **Priority default changed to AboveNormal** (was High).
- **Process Manager redesigned** — Priority card moved to top, CPU thread mapping card, grid refresh paused during edits.
- **Video Settings overhaul** — Reordered submenu, preset sizes fixed.

### Fixed
- **DefaultFont crash** — Null reference on systems without default font.
- **Launch positioning** — Don't force window offsets on every launch, respect user INI edits.
- **PiP anchor** — Fixed anchor point for overlay positioning.
- **Dalaya patcher** path handling.
- Direct switch hotkeys (Alt+1-6) disabled by default to avoid conflicts.
- Hotkey conflict warning appearing on every Settings close.
- PiP max windows label layout and custom size capped to 960×720.

### Removed
- **Swap Windows** feature — removed (stacked mode replaces it).
- **CharacterEditDialog** — removed (per-character overrides simplified).

---

## v2.8.0 — Slim Titlebar / WinEQ2 Mode (2026-03-30)

### Added
- **Slim titlebar mode** (WinEQ2 style) — Strips `WS_THICKFRAME` (resize border) while keeping `WS_CAPTION` (thin title bar). Positions window at full `rcMonitor` bounds to overlap taskbar. Replaces both "borderless" and "remove title bars" options with a single unified mode.
- **Auto-apply slim titlebar** — Guard timer re-applies style when EQ fights the window decoration changes.
- **EQClientSettingsForm expanded** — Additional eqclient.ini toggle controls.

### Changed
- **WindowManager rewritten** (280+ lines changed) — Unified slim titlebar logic, monitor bounds calculation, style manipulation.
- **Settings Layout tab** — Slim titlebar checkbox replaces borderless + remove-title-bar checkboxes.
- **LaunchManager simplified** — Removed post-launch window positioning (slim titlebar handles it).

### Removed
- **ROADMAP.md** — Removed from project (tracked in root `Roadmap_master.md`).
- **Borderless fullscreen mode** — Superseded by slim titlebar mode.
- **Remove Title Bars option** — Superseded by slim titlebar mode.

---

## v2.7.0 — Process Manager Consolidation (2026-03-28)

### Added
- **Consolidated Process Manager** — 3 clear cards: Windows Priority, Core Assignment, FPS Limits
- **INI-based Core Assignment** — 6 NumericUpDown slot pickers for CPUAffinity0-5, writes directly to eqclient.ini
- **Ghost FPS label** — shows current eqclient.ini MaxFPS/MaxBGFPS values alongside the editor

### Changed
- **FPS defaults** changed to 80/80 (was 0 = unlimited, which crashes EQ)
- **Priority defaults** changed to High/High (prevents virtual desktop crashes + enables autofollow)
- **Settings "Affinity" tab renamed to "Performance"** — stripped to enable toggle + retry settings
- **Submenu directions** — Video Settings, Settings, Launcher all open upward (AboveRight)
- **CharacterEditDialog simplified** — priority override only (core assignment now global via eqclient.ini)

### Removed
- **ThrottleManager** — process suspension was causing "Suspended" in Task Manager
- **CPU Affinity submenu** from tray menu — Process Manager is the one-stop shop
- **Per-character AffinityOverride** — replaced by global eqclient.ini CPUAffinity0-5 slots

---

## v2.6.0 — Per-Character Overrides (2026-03-20)

### Added
- **Per-character CPU affinity** — assign different core masks to individual characters. Characters with `AffinityOverride` use their custom mask instead of the global active/background masks.
- **Per-character process priority** — set individual characters to Normal, AboveNormal, or High priority. Characters with `PriorityOverride` use their custom priority instead of the global setting.
- **Character Edit dialog** — double-click a character in Settings → Characters tab to edit affinity mask and priority overrides. Checkbox toggles override on/off, hex mask input with validation.
- **Process Manager "Source" column** — shows "Custom" (cyan) for clients using per-character overrides, "Global" for clients using default settings.
- **Reset Defaults button in Video Settings form** — resets Width/Height (1920×1080), offsets (0,0), Windowed Mode (on), Disable Log (off), Title Bar Offset (0). Matches AHK v2.4 `ResetVMDefaults`. Requires Save or Apply to write to disk.

---

## v2.4.0 — Tray UX Overhaul (2026-03-14)

### Added
- **Configurable tray click actions** — Settings → General tab lets users bind single/double/triple/middle-click to specific actions (Launch One, Fix Windows, Open Settings, etc.)
- **Custom video presets** — Save up to 3 custom resolutions in Video Settings (FIFO eviction, duplicates skipped)
- **Dark-themed context menus** — `DarkMenuRenderer` applies dark background/foreground to all tray menu items
- **FloatingTooltip** — replaces `MessageBox.Show` "already running" popup with a non-blocking floating tooltip

### Changed
- **Tray context menu reorganized** into grouped submenus (Video Settings, Settings, Launcher)
- **Medieval emoji/icon prefixes** restored on all tray menu items (matches AHK v2.4 style)
- **CPU Affinity submenu simplified** — removed per-core checkboxes, shows info labels only
- **Process Manager** restyled with dark DataGridView theme
- **First-run** now auto-opens Settings instead of requiring manual navigation

---

## v2.3.0 — Performance & Fullscreen (2026-03-13)

### Added
- **Background FPS throttling** (`Core/ThrottleManager.cs`) — duty-cycles background EQ clients via `NtSuspendProcess`/`NtResumeProcess`. Configurable throttle percent (0-90%) and cycle interval. Active client is never throttled. Settings on Affinity tab.
- **Borderless fullscreen mode** — WinEQ Y+1 offset trick: strips window decorations and positions at `(monitor.Left, monitor.Top+1)` using `rcMonitor` bounds. Preserves Alt+Tab and PiP window overlay. Checkbox on Layout tab.

---

## v2.2.0 — Production Hardening (2026-03-12)

### Added
- **Persistent file logging** (`Core/FileLogger.cs`): Info/Warn/Error with timestamp, 1MB rotation, thread-safe
- **Input validation** (`AppConfig.Validate()`): Clamps all numeric config fields to safe ranges on load and save
- **IWindowsApi interface** (`Core/IWindowsApi.cs`): Abstraction layer for Win32 calls, enables unit testing
- **79 unit tests** across 7 test files: AppConfig, WindowManager, AffinityManager, ConfigManager, ConfigMigration, HotkeyManager, EQClient
- **Solution file** (`EQSwitch.sln`): Main project + xUnit test project with Moq

### Fixed
- **P2-02**: Context menu client labels now update when window titles change
- **Concurrency**: KeyboardHookManager uses `ImmutableHashSet<int>` (lock-free) instead of `HashSet<int>` with lock
- **Concurrency**: ProcessManager fires events outside lock block, uses specific exception catches
- **Concurrency**: AffinityManager snapshots retry counters before iterating
- **Resource leak**: LaunchManager implements IDisposable, cancels launches on dispose
- **Resource leak**: ProcessManagerForm stops refresh timer in Dispose()
- **Backup pruning**: ConfigManager sorts by file write time instead of filename string
- **Hotkey ID overflow**: `_nextId` resets to 1 on `UnregisterAll()` (P4-01)
- **Exception hardening**: DWM HRESULT mapped to readable messages in PipOverlay
- **Exception hardening**: VideoSettingsForm retries file I/O (2x, 500ms)
- **Magic numbers**: Named constants replace all magic numbers across 6 files

### Changed
- All diagnostic logging migrated from `Debug.WriteLine` to `FileLogger`
- WindowManager and AffinityManager accept optional `IWindowsApi` for dependency injection

---

## v2.1.1 — Post-Release Audit Fixes (2026-03-12)

### Fixed
- **P0-01**: Hook callback dispatched async via SynchronizationContext.Post() — prevents Windows killing the LL hook on slow callbacks
- **P0-02**: Global switch key `]` no longer swallowed when zero EQ clients running (requireClients guard + cached PID check)
- **P0-03**: PiP overlay Ctrl+drag works — replaced WS_EX_TRANSPARENT with dynamic WM_NCHITTEST (HTTRANSPARENT default, HTCLIENT when Ctrl held)
- **P0-04**: eqclient.ini read/write uses ANSI encoding instead of UTF-8 to prevent corruption
- **P1-01**: Eliminated Process.GetProcessById() in hook callback — cached PID HashSet with GetWindowThreadProcessId
- **P1-02**: Screen.PrimaryScreen null-safe fallback for headless/RDP disconnect
- **P1-03**: All eqclient.ini file operations use Encoding.Default consistently
- **P1-04**: Triple-click tray detection resets timestamp on every click
- **P1-05**: Run-at-startup registry path validated on launch — auto-corrects if exe moved
- **P1-06**: LaunchManager timers cancelled on config reload and shutdown
- **P1-07**: Minimum 500ms enforced between staggered launches
- **P1-08**: ContextMenuStrip disposed in Shutdown path
- **P1-09**: Previous custom icon disposed on LoadIcon reload

---

## v2.1.0 — Deferred Features (2026-03-11)

### Added
- **Process Manager GUI**: Live view of all EQ clients with PID, character name, priority, and affinity mask. Auto-refreshes every second. Includes Force Apply button.
- **PiP Settings tab**: Configure PiP size preset, custom dimensions, opacity, border color, and max windows from Settings GUI
- **Characters tab**: View character profiles with Export/Import buttons for JSON backup
- **All Cores / Clear buttons**: Quick-select on Affinity tab to set masks to system max or minimum
- **Force Apply Affinity**: Tray menu item to re-apply affinity rules to all clients immediately
- **Triple-click tray**: Triple-click the tray icon within 500ms to arrange all windows
- **Desktop shortcut**: Create Desktop Shortcut menu item via WScript.Shell COM

### Changed
- "Process Info" balloon replaced with full Process Manager window
- PiP config now persists through Settings GUI (was only configurable via JSON)
- ReloadConfig now includes PiP settings for hot-reload

---

## v2.0.0 — C# Port (2026-03-11)

Complete rewrite from AutoHotkey v2 to C# (.NET 8 WinForms).

### Added
- **Settings GUI**: 6-tab dark-themed settings dialog (General, Hotkeys, Layout, Affinity, Launch, Paths)
- **Video Settings**: Read/write eqclient.ini [VideoMode] section with resolution presets
- **PiP Overlay**: DWM thumbnail-based live previews of background EQ clients
  - Click-through overlay (won't steal focus)
  - Ctrl+drag repositioning with position persistence
  - Auto-hide when fewer than 2 clients
- **Window Swap**: Rotate window positions (1→2, 2→3, N→1)
- **Hung window detection**: Skip unresponsive windows during arrange/swap operations
- **Files submenu**: Quick access to log files, eqclient.ini, GINA, notes
- **Links submenu**: Dalaya Wiki, Shards Wiki, Fomelo
- **Help window**: Full hotkey reference and feature guide
- **Run at Startup**: Registry-based toggle in tray menu
- **Tray interactions**: Double-click to launch, middle-click for PiP
- **Hotkey suffixes**: Keyboard shortcuts shown in tray menu items
- **Config migration**: Auto-import from AHK eqswitch.cfg on first run

### Changed
- Config format: INI → JSON (eqswitch-config.json)
- Config backups: automatic rotation (keeps last 10)
- Hotkey system: RegisterHotKey + WH_KEYBOARD_LL (was AHK native)
- Window arrangement: proper multi-monitor support via EnumDisplayMonitors
- CPU affinity: configurable retry on launch (EQ resets affinity after startup)
- Build output: .NET single-file publish (no AV false positives)

### Removed
- AutoHotkey dependency
- Flash suppress / auto-minimize (superseded by PiP + affinity management)
