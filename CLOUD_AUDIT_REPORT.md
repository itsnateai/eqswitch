# Cloud Audit Report

> Auditor: Claude Cloud
> Date: 2026-03-19
> Project: EQSwitch v2.5.0
> Passes: 2 (file-by-file + cross-cutting) + dedicated memory leak pass + security pass
> Files read: 40/40 (all .cs source files)

## Summary

EQSwitch is in solid shape for release. The codebase shows evidence of multiple prior audits that addressed critical issues (P0-01 through P0-04, numerous P1s). The remaining findings are primarily resource management optimizations for 72-hour viability and minor version string staleness. No security vulnerabilities were found — the attack surface is minimal (local desktop app, no network input, config from local JSON file). Six fixes were applied across 5 files.

## Findings

### P0 — Must Fix Before Ship (Security/Data Loss/Crash)

| # | File:Line | Category | Issue | Fix Applied? |
|---|-----------|----------|-------|-------------|
| — | — | — | No P0 issues found | — |

### P1 — Should Fix (Bugs/Logic Errors)

| # | File:Line | Category | Issue | Fix Applied? |
|---|-----------|----------|-------|-------------|
| 1 | TrayManager.cs:445 | Bug | Context menu title hardcoded "v2.3.0" — stale version | ✅ Fixed: uses Assembly version dynamically |
| 2 | HelpForm.cs:44 | Bug | Help text hardcoded "v2.4.0" — stale version | ✅ Fixed: uses Assembly version dynamically |
| 3 | DarkTheme.cs:72-84 | Memory Leak | Font objects allocated on EVERY tab DrawItem event (GDI handle churn) | ✅ Fixed: cached as static readonly fields |
| 4 | DarkTheme.cs:76 | Memory Leak | StringFormat created without `using` in DrawTab | ✅ Fixed: added `using` keyword |
| 5 | SettingsForm.cs:507,520 | Memory Leak | Two `new Font("Consolas")` calls creating duplicate GDI objects | ✅ Fixed: share single font instance |
| 6 | LaunchManager.cs:165 | Security | ExeName not validated for path traversal characters (`..`, separators) | ✅ Fixed: validates ExeName before Path.Combine |
| 7 | ProcessManagerForm.cs:171 | Memory Leak | FormClosed handler only calls Stop(), not Dispose() on refresh timer | ✅ Fixed: added Dispose() call |

### P2 — Improvements (Code Quality/Performance)

| # | File:Line | Category | Issue | Suggested Fix |
|---|-----------|----------|-------|---------------|
| 1 | ProcessManager.cs:31 | Performance | `Clients` property creates new List+ReadOnlyCollection on every access; called 2-4x per 250ms affinity tick | Cache snapshot once per tick (partially done in TrayManager) |
| 2 | TrayManager.cs:699 | Code Quality | ShowBalloon creates a local Timer variable — technically could be GC'd before tick fires | Store in field; very low risk since 50ms interval |
| 3 | DarkTheme.cs:178-217 | Dead Code | `AddHotkeyBox` method defined but never called (SettingsForm uses inline pattern) | Remove if confirmed unused |
| 4 | DarkTheme.cs:159-172 | Dead Code | `AddTextBox` method — appears unused in current codebase | Remove if confirmed unused |

### P3 — Observations (Style/Nits/Future Work)

| # | File:Line | Category | Issue | Notes |
|---|-----------|----------|-------|-------|
| 1 | CLAUDE.md | Docs | File layout section lists ~16 files; actual count is ~40 .cs files | Missing DarkTheme, FileOperations, StartupManager, HelpForm, EQClientSettingsForm, EQChatSpamForm, EQParticlesForm, EQVideoModeForm, EQKeymapsForm |
| 2 | CLAUDE.md | Docs | States "~27 files, ~5,700 lines" — actual is ~40 files | Update count |
| 3 | TrayManager.cs:434 | Style | BuildContextMenu is 190 lines — could benefit from extraction of submenu builders | Low priority, works correctly |
| 4 | EQClientSettingsForm.cs | Style | 935-line form with many checkboxes — functional but dense | Works correctly; complexity is inherent to the feature |

## Fixes Applied

### Commit: audit fixes — version strings, memory leaks, security hardening

- `UI/TrayManager.cs:445` — Replaced hardcoded "v2.3.0" with dynamic Assembly version lookup
- `UI/HelpForm.cs:44` — Replaced hardcoded "v2.4.0" with dynamic Assembly version lookup
- `UI/DarkTheme.cs:51-84` — Cached tab fonts as `static readonly` fields instead of allocating on every DrawItem; added `using` to StringFormat
- `UI/SettingsForm.cs:507,521` — Reuse single `monoFont` instance for both affinity mask TextBoxes
- `UI/ProcessManagerForm.cs:171` — Added `_refreshTimer.Dispose()` to FormClosed handler
- `Core/LaunchManager.cs:165` — Added path traversal validation for ExeName (rejects `..`, `/`, `\` in filename)

## 72-Hour Viability Analysis

### Timer Path Allocation Audit

| Timer | Interval | Allocations Per Tick | 72h Estimate | Status |
|-------|----------|---------------------|--------------|--------|
| Affinity poll | 250ms | 1 List snapshot (~96 bytes for 4 clients) | ~100MB GC pressure | OK — ephemeral, GC handles it |
| Process poll | 500ms (active) / 5000ms (idle) | Process[] from GetProcessesByName + HashSet | ~50MB active / ~5MB idle | OK — Process[] properly disposed in finally |
| Throttle suspend | 50-100ms | 3 OpenProcess+CloseHandle per cycle | Handle churn but no leak | OK — handles properly closed |
| Throttle resume | 50-100ms | 3 OpenProcess+CloseHandle per cycle | Same as above | OK |
| PiP refresh | 500ms | GetAsyncKeyState + IsWindow checks | Negligible | OK |
| Retry timer | 2000ms | LINQ on small dictionary | Negligible | OK |

### GDI Handle Budget

- **Before fix**: ~4 Font allocations/sec from DrawTab + 2 StringFormat/sec = ~518K GDI objects created in 72h
- **After fix**: 0 Font allocations from DrawTab (cached), StringFormat uses `using`. Only control Font assignments during form creation (one-time).

### Explorer Restart Recovery

- No explicit `TaskbarCreated` message handler found. If Windows Explorer crashes and restarts, the tray icon will disappear and not recover. This is a known limitation common to .NET NotifyIcon apps.
- **Mitigation**: User can restart EQSwitch (single-instance mutex prevents duplicates).

## Assumptions Made During Audit

- [ ] The app runs exclusively on 64-bit Windows (GetWindowLongPtrW is the correct P/Invoke — GetWindowLong would fail on 64-bit)
- [ ] EQ process name is always "eqgame" (configurable but assumed in default config)
- [ ] The app runs on a single UI thread (WinForms Timer + UI thread assumption throughout)
- [ ] .NET 8 GC handles ephemeral allocations efficiently (List snapshots in 250ms timer are Gen0, collected quickly)
- [ ] COM interop (WScript.Shell for shortcuts) is available on all target Windows versions

## Verified Clean Categories

- [x] **SQL/NoSQL Injection**: clean — no database access anywhere in the codebase
- [x] **XSS/innerHTML**: clean — WinForms desktop app, no web rendering
- [x] **SSRF**: clean — no HTTP client or URL fetching in app code
- [x] **Secrets/API Keys**: clean — no hardcoded secrets, no API keys, no tokens
- [x] **Authentication/Authorization**: clean — not applicable (local desktop app)
- [x] **CORS/CSP**: clean — not applicable
- [x] **Network input validation**: clean — app makes zero network requests
- [x] **Deserialization safety**: clean — uses System.Text.Json (safe, no arbitrary code execution)
- [x] **Process handle leaks**: clean — all OpenProcess calls paired with CloseHandle in finally blocks
- [x] **COM object cleanup**: clean — Marshal.FinalReleaseComObject in finally blocks for WScript.Shell
- [x] **Keyboard hook delegate GC**: clean — _hookProc stored as field to prevent collection
- [x] **Config round-trip**: clean — verified TrayClickConfig, CustomVideoPresets, all sub-configs serialize/deserialize correctly with camelCase naming
- [x] **Timer dispose on reload**: clean — ReloadConfig stops+disposes old timers before creating new ones
- [x] **ContextMenuStrip dispose**: clean — disposed in Shutdown() and Dispose() paths
- [x] **PiP DWM thumbnail cleanup**: clean — DwmUnregisterThumbnail called in UnregisterAll() and Dispose()
- [x] **Throttle fail-safe**: clean — ResumeAllSuspended called on Stop() ensures no processes left frozen
- [x] **File encoding**: clean — eqclient.ini uses Encoding.Default (ANSI) consistently throughout

## Questions for Nate

- Explorer restart recovery: should we add a `TaskbarCreated` message handler to re-show the tray icon after Explorer crashes? This is a standard Windows pattern but adds complexity.
- Should the `AddHotkeyBox` and `AddTextBox` methods in DarkTheme be removed as dead code, or are they reserved for future use?
- The Clients property on ProcessManager creates a defensive copy on every access. Should this be changed to a cached snapshot that refreshes on RefreshClients() to reduce allocation churn?

## CLAUDE.md Updates Needed

1. File count: "~27 files" → "~40 files" (9 new UI sub-forms added in v2.4.0)
2. File layout section missing: DarkTheme.cs, FileOperations.cs, StartupManager.cs, HelpForm.cs, EQClientSettingsForm.cs, EQChatSpamForm.cs, EQParticlesForm.cs, EQVideoModeForm.cs, EQKeymapsForm.cs
3. Status section should note v2.5.0 audit fixes
