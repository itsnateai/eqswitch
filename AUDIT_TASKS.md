# EQSwitch — Production Readiness Audit

> Generated: 2026-03-13 (Final production audit v3)
> Stack: AutoHotkey v2 (Windows desktop tray utility)
> Files: 1 source | Lines: ~2,640
> Tests: 0 test files | Pass rate: N/A (AHK project, no test framework)

## Findings

### P0 — Critical

No P0 findings.

### P1 — Robustness

- [x] **P1-01 · Middle-click cooldown never activates** ✓ Fixed
  **File:** `EQSwitch.ahk:637` | **Issue:** `midCooldown` was a `static` local in `TrayClick` that was never updated after `MidClickResolve` fired an action. The 1.2s re-trigger prevention described in comments was non-functional — `midCooldown` stayed at 0 forever, so `now - midCooldown` was always > 1200 after the first 1.2s of app lifetime. | **Fix:** Replaced `static midCooldown` with global `g_midCooldown`, initialized at file scope. Set `g_midCooldown := A_TickCount` in `MidClickResolve` after action fires.

### P2 — Quality

No P2 findings.

### P3 — Documentation

- [x] **P3-01 · README uses stale "Active Key" label** ✓ Fixed
  **File:** `README.md:53,63,98,203` | **Issue:** Settings GUI was renamed from "Active Key" to "EQ Switch Key" in v2.0, but README still referenced the old name in 4 places. | **Fix:** Updated all occurrences to "EQ Switch Key".

- [x] **P3-02 · README and help text say "Alt+M" instead of "RAlt+M"** ✓ Fixed
  **File:** `README.md:100`, `EQSwitch.ahk:1435` | **Issue:** Default multimon hotkey is `>!m` (Right Alt+M), not plain Alt+M. | **Fix:** Changed to "RAlt+M" in both README and help text.

- [x] **P3-03 · README describes TARGET_MONITOR for wrong mode** ✓ Fixed
  **File:** `README.md:113` | **Issue:** Said "which monitor gets the first EQ window in multimonitor mode" but TARGET_MONITOR is only used in single screen mode (dropdown is disabled in multimonitor). | **Fix:** Changed to "which monitor to use in single screen mode".

- [x] **P3-04 · README overstates Video Settings offset controls** ✓ Fixed
  **File:** `README.md:128` | **Issue:** Said "X, Y, Width, Height offsets" but Video Settings only exposes X and Y offsets. | **Fix:** Changed to "X and Y offsets".

### P4 — Nice-to-have (not fixed)

- **P4-01 · No automated test suite** — AHK v2 projects rarely have tests, but key logic (affinity mask conversion, hotkey formatting, character name validation) could be unit-tested with AHK v2's built-in assertions. Deferred to maintainer.
- **P4-02 · README double-click description omits configurability** — Line 89 says "Left double-click — launches one EQ client" without noting this requires DBLCLICK_LAUNCH to be enabled (default is off / opens Settings). Minor — the Tray Behavior section explains it.

## Deep Sweep Results

| Sweep | Status | Notes |
|-------|--------|-------|
| 4A Secrets | CLEAN | No secrets, tokens, API keys, or high-entropy strings. Git history clean. |
| 4B Privacy | CLEAN | No automatic network calls. All URLs are user-initiated (tray menu links to wiki/fomelo). No telemetry. |
| 4C Debug | CLEAN | Two MsgBox calls are intentional user confirmation dialogs. No OutputDebug or debug leftovers. |
| 4D Unsafe patterns | CLEAN | All Run() calls use quoted paths from validated/user-configured sources. DllCall parameters use correct types. No injection vectors. |
| 4E Packaging | CLEAN | .gitignore covers *.exe, config, notes, OS metadata, editor artifacts. No build artifacts committed. |
| 4F Build & tests | N/A | AHK project requires Windows + Ahk2Exe to build. No test framework. |
| 4G Supply chain | CLEAN | Zero external dependencies. Single-file AHK v2 script using only built-in functions and Win32 APIs. |

## Cross-Reference Verification

| Check | Result |
|-------|--------|
| Version strings match | PASS — v2.2 in g_version, CHANGELOG.md, FINAL_REPORT.md, CLAUDE.md |
| Config keys LoadConfig↔SaveConfig | PASS — all 30 keys match between read and write |
| DllCall signatures | PASS — OpenProcess, SetProcessAffinityMask, GetProcessAffinityMask, CloseHandle, GetPriorityClass, DWM thumbnail APIs all use correct parameter types |
| MENUITEMINFOW struct size | PASS — 80 bytes on 64-bit, 48 on 32-bit (A_PtrSize check) |
| DWM_THUMBNAIL_PROPERTIES struct | PASS — 48 bytes, offsets verified (dwFlags@0, rcDest@4-19, fVisible@40, fSourceClientAreaOnly@44) |
| GUI singleton flags | PASS — SETTINGS_OPEN, g_pmOpen, g_vmOpen all properly set/cleared in open/close/error paths |
| Timer cleanup | PASS — g_pipTimer and g_pipCtrlWatch stopped in DestroyPiP; OnExit calls DestroyPiP |
| Process handle lifecycle | PASS — all OpenProcess calls have try/finally with CloseHandle |
| External URLs documented | PASS — 5 URLs total, all user-initiated or in comments |

## AI Code Pattern Check

| Check | Result |
|-------|--------|
| Hallucinated APIs | CLEAN — all AHK v2 functions and Win32 APIs verified |
| Stale comments | CLEAN — no comments contradicting code (except midCooldown, now fixed) |
| Orphaned code | CLEAN — dead code removed in v2.2 |
| Naming consistency | CLEAN — PascalCase functions, g_ prefix globals throughout |
| Over-engineered abstractions | CLEAN — code complexity appropriate for feature set |
