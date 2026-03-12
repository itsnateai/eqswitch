# EQSwitch — Production Readiness Audit

> **Audit Date:** 2026-03-12
> **Auditor:** Claude (production-readiness-audit)
> **Version Audited:** 2.1 (commit 251e340)
> **Source:** EQSwitch.ahk (2,637 lines)

---

## Findings

| ID | Category | Description | Fix Applied | Version |
|----|----------|-------------|-------------|---------|
| A01 | Code correctness | `g_dblClickUpIgnore` used in `TrayClick` global declaration (line 596) but never initialized at file scope. In AHK v2, reading an uninitialized global throws. Safe in practice (guarded by `g_dblClickPending` check), but fragile. | Added `g_dblClickUpIgnore := false` initialization at file scope alongside other tray click state variables. | 2.1 |
| A02 | Dead code | `FIX_BOTTOM_OFFSET` loaded from config (LoadConfig), saved back (SaveConfig), and declared global in both functions, but never referenced in any logic. Commit 251e340 claimed to remove it but only removed `HidePiPBorder`. | Removed all 4 references: 2 global declarations, 1 ReadKey line, 1 IniWrite line. | 2.1 |
| A03 | Documentation | FINAL_REPORT.md referenced `eqswitch.ahk` (lowercase) but actual repo filename is `EQSwitch.ahk`. | Fixed to `EQSwitch.ahk`. | 2.1 |
| A04 | Documentation | FINAL_REPORT.md stated "2,646 lines" but actual count is ~2,640. | Updated to `~2,640 lines`. | 2.1 |
| A05 | Documentation | CLAUDE.md key files table referenced `eqswitch.ahk` (lowercase) and "~2,646 lines". | Fixed to `EQSwitch.ahk` and `~2,640 lines`. | 2.1 |

---

## Production Readiness Checklist

### Code Correctness
- [x] **Logic errors** — No off-by-one, wrong operator, or unreachable code found. Insertion sort bounds (line 80) correct for 0 and 1-element arrays. Mod-based cycling (line 408) correct.
- [x] **Race conditions** — All WinGetStyle/WinGetPos/WinActivate calls wrapped in try. PiPHitTest wraps .Hwnd access in try. DblClick/TripleClick state machine uses guards correctly.
- [x] **Error handling** — All GUI open functions (Settings, ProcessManager, VideoMode) use try/catch with flag cleanup. Launch functions validate paths before Run(). Config migration wrapped in try.
- [x] **Scope declarations** — All nested functions that access globals use explicit `global` declarations. No assume-global. Fixed A01 (missing initialization).
- [x] **Resource leaks** — Process handles use try/finally with CloseHandle (lines 116-127, 175-194, 2234-2246). DWM thumbnails unregistered in DestroyPiP and SwapPiPSources. PiP timers stopped on destroy. OnExit handler cleans up PiP resources.
- [x] **Dead code** — Fixed A02 (FIX_BOTTOM_OFFSET). No other dead code found. No stale TODO/FIXME markers.

### Win32/API Contracts
- [x] **DWM_THUMBNAIL_PROPERTIES** — Buffer(48) correct. Field offsets verified: dwFlags@0, rcDestination@4-19, rcSource@20-35, opacity@36, fVisible@40, fSourceClientAreaOnly@44.
- [x] **MENUITEMINFOW** — cbSize 80 (64-bit) / 48 (32-bit) correct. fMask and fState offsets correct.
- [x] **DllCall signatures** — All Ptr/UPtr/UInt/Int types match Win32 API signatures. OpenProcess access rights (0x0200 for SET_INFORMATION, 0x0400 for QUERY_INFORMATION) correct.
- [x] **GetProcessAffinityMask** — Uses UPtr* for both masks, correct for 64-bit pointer-sized bitmasks.
- [x] **IsHungAppWindow** — Correct signature (Ptr in, Int out).
- [x] **GetMenuString** — MF_BYPOSITION (0x400) flag correct.

### Edge Cases
- [x] **Empty input** — Empty hotkey preserved (bare-key workaround). Empty EQ path shows tooltip. Empty char name blocked. Empty affinity string returns early.
- [x] **Zero/null values** — TARGET_MONITOR clamped to 1..MonitorGetCount(). NUM_CLIENTS clamped to 1..8. Affinity mask <= 0 returns early.
- [x] **Overflow** — Core count popcount uses right-shift loop (no overflow). Affinity mask uses UPtr (64-bit safe).
- [x] **Multi-monitor** — MonitorGet/MonitorGetWorkArea wrapped in try/catch. Target monitor clamped to available count. PiP position clamped to monitor bounds.

### User-Facing Text
- [x] **Spelling** — No typos found in tooltips, menus, GUI labels, or help text.
- [x] **Grammar** — Consistent use of em dashes, proper contractions with apostrophes.
- [x] **Capitalization** — Consistent: "ON"/"OFF" for toggle states, sentence case for tooltips.
- [x] **Version references** — g_version = "2.1" matches Settings title bar, tray tooltip, and help window.

### Documentation
- [x] **README.md** — Accurate feature descriptions. File table matches repo. Compile command correct. FAQ current.
- [x] **CHANGELOG.md** — Complete history v1.0 through v2.0. No v2.1 entry (v2.1 was a maintenance/audit release with no user-facing changes — absence is acceptable).
- [x] **FINAL_REPORT.md** — Fixed filename case and line count (A03, A04).
- [x] **CLAUDE.md** — Fixed filename case and line count (A05).
- [x] **LICENSE** — Standard MIT, valid.
- [x] **.gitignore** — Covers cfg, exe, notes, editor artifacts, merge artifacts. Clean.
- [x] **Comments** — No comments contradict their code. No stale references.

---

**Result: 5 findings, all resolved. Codebase is production-ready.**
