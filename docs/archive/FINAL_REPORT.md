# EQSwitch — Final Report

| Field | Value |
|-------|-------|
| **Project** | EQSwitch |
| **Version** | v3.1.0 |
| **Date** | 2026-04-01 |
| **Stack** | C# .NET 8 WinForms + Native C/C++ (MinHook, DirectInput proxy) |
| **Repo** | https://github.com/itsnateai/eqswitch.git (private) |
| **License** | GPL-3.0 |

## Summary

EverQuest multiboxing window manager for Shards of Dalaya. System tray utility managing multiple EQ clients — window switching, DLL hook injection (SetWindowPos/MoveWindow), DPAPI-encrypted auto-login with background credential injection via DirectInput proxy, slim titlebar mode, PiP DWM thumbnail overlays, CPU affinity management, and comprehensive eqclient.ini editing. ~13,153 lines C# + 2,230 lines C + 1,233 lines C++ across 57 source files.

## Key Features

- DLL hook injection preventing EQ from fighting window management
- Background auto-login via dinput8.dll DirectInput proxy (no focus stealing)
- DPAPI-encrypted credential storage (AES-256, user-scoped)
- Slim titlebar / WinEQ2 mode with guard timer
- PiP DWM thumbnail overlays (GPU-composited, zero CPU)
- Multi-monitor window arrangement with grid/stacked modes
- Per-character CPU affinity and priority management
- 6-tab dark-themed settings GUI
- Comprehensive eqclient.ini editor (video, keymaps, chat, particles, models)
- Portable single-file publish (~179MB self-contained)
- Config migration from AHK v2.x

## Audit Summary

- **Total findings:** 20 (3 P0, 6 P1, 6 P2, 2 P3, 3 P4)
- **Resolved:** 9/9 P0+P1 (all bugs and robustness issues)
- **Remaining:** 11 P2-P4 (features, docs, polish — not blockers)
- **Semgrep SAST:** 0 findings across 304 rules on 85 files
- **Secrets:** 0 hardcoded, 0 sensitive files
- **Dependencies:** 1 NuGet, 0 CVEs

### P0 Fixes (Bugs & Security)
1. Cross-thread UI access from auto-login events — marshaled via SynchronizationContext
2. Race condition on shared `_loginWriter` field — made local variable
3. Thread-unsafe `_activeLoginPids` HashSet — replaced with ConcurrentDictionary

### P1 Fixes (Robustness)
1. Missing `CleanupHookInjection()` in Dispose
2. `ReloadConfig` missing `UseHook` and `Pip.Orientation` fields
3. Guard timer restarted during active auto-login
4. `DllInjector.Inject` freeing remote memory after timeout
5. `DllInjector.Eject` ignoring WaitForSingleObject return
6. Timer-path allocations in `HookConfigWriter.WriteConfig`

## Git Stats

- **Commits:** 152
- **Branches:** 1 (master)
- **Tags:** 10 (v2.1.0 through v3.1.0)

## Version History

| Version | Date | Highlights |
|---------|------|------------|
| v3.1.0 | 2026-04-01 | DirectInput proxy, background auto-login, hook upgrades |
| v3.0.1 | 2026-04-01 | Per-process hook shared memory, audit fixes |
| v3.0.0 | 2026-04-01 | DLL hook injection, DPAPI auto-login, PiP overhaul |
| v2.9.1 | 2026-03-30 | Settings & launch cleanup |
| v2.9.0 | 2026-03-30 | UI consolidation, multi-monitor video |
| v2.8.0 | 2026-03-30 | Slim titlebar / WinEQ2 mode |
| v2.7.0 | 2026-03-28 | Process manager consolidation |
| v2.6.0 | 2026-03-20 | Per-character overrides |
| v2.4.0 | 2026-03-14 | Tray UX overhaul |
| v2.3.0 | 2026-03-13 | Background FPS throttling, borderless fullscreen |
| v2.2.0 | 2026-03-12 | Production hardening, 79 unit tests |
| v2.1.1 | 2026-03-12 | Post-release audit fixes |
| v2.1.0 | 2026-03-11 | Process manager, PiP settings, characters |
| v2.0.0 | 2026-03-11 | Complete C# port from AHK |

## Lessons Learned

- **DLL injection timing matters** — injecting hook DLL during auto-login steals focus. Defer injection until login completes.
- **Per-process shared memory** — global shared memory breaks with multi-monitor (all windows get same position). Per-PID memory-mapped files solve this.
- **EQ fights window management** — SetWindowPos/MoveWindow hooks are the only reliable solution. External-only approaches always lose.
- **DPAPI is user-scoped** — credentials don't survive Windows reinstall or user account changes.
- **WinForms Timer from background thread silently fails** — always marshal to UI thread.
- **HashSet is not thread-safe** — concurrent Add/Remove corrupts internal state. Use ConcurrentDictionary.
- **VirtualFreeEx after timeout crashes remote process** — leak the allocation rather than risk access violation.
- **Config reload must copy ALL fields** — partial field-by-field copy inevitably misses new fields.
- **Guard timer + auto-login conflict** — guard timer re-applies styles, stealing focus from login typing.
