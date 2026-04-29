# Semgrep triage — pre-existing false-positive baseline

Last reviewed: **2026-04-29** (against scan ID `161117332`, run `25126688356` on tag `v3.14.2`).

The Semgrep workflow at `.github/workflows/semgrep.yml` runs `semgrep ci --no-suppress-errors` on every push to `main` and reports findings to the Semgrep AppSec Platform (`semgrep.dev`). It does **not** upload SARIF to GitHub code-scanning, so findings are invisible from the GitHub Security tab.

Every finding listed below has been reviewed against EQSwitch's actual threat model — a **local Windows desktop multibox utility** for a single user. There is **no remote-attacker boundary**: an attacker who can write to `eqswitch-config.json` already owns the user's Windows account and can do worse things directly. Assertions like "OS command injection" only matter when there is untrusted input crossing a trust boundary, which there is not.

## Suppressed at file level (via `.semgrepignore`)

These are upstream third-party files that ship verbatim per their BSD-2 / MIT licenses. We do not modify them, and any "fix" would be a downstream fork that breaks future upstream sync.

| File | Origin | Findings silenced |
|---|---|---|
| `Native/buffer.c` | MinHook (BSD-2) | `c.lang.security.insecure-use-memset` |
| `Native/hook.c` | MinHook (BSD-2) | `gitlab.flawfinder.memcpy-1` ×N |
| `Native/trampoline.c` | MinHook (BSD-2) | `gitlab.flawfinder.memcpy-1` ×N |
| `Native/hde32.c/h` | Hacker Disassembler Engine (BSD-style) | `c.lang.security.insecure-use-memset`, `gitlab.flawfinder.sprintf-1` |
| `Native/hde64.c/h` | Hacker Disassembler Engine (BSD-style) | `c.lang.security.insecure-use-memset` |
| `Native/MinHook.h` | MinHook (BSD-2) | (header — defensive) |

## High-severity findings to dismiss in the AppSec UI

These four are real-pattern false positives. Mark each "false positive" with the rationale below in https://semgrep.dev/orgs/itsnateai → Findings.

### 1. `gitlab.flawfinder.strcpy-1` — `Native/eqmain_widgets.cpp:477,481`

```c
char preview[40] = {};            // line 467
...
strcpy(preview, "(empty)");       // line 477 — 7-byte literal
...
strcpy(preview, "(SEH)");         // line 481 — 5-byte literal
```

**Why FP:** the source is a string literal of compile-time-known length (7 and 5 bytes including NUL). `preview` is a fixed-size 40-byte stack buffer declared 10 lines earlier. flawfinder cannot verify either fact, so it flags the pattern unconditionally.

### 2. `gitlab.flawfinder.sprintf-1.*` — `Native/net_debug.cpp:112`

This finding was originally mis-attributed in this doc to upstream HDE32 — it is actually in **first-party** `net_debug.cpp` and is **not** silenced by `.semgrepignore` (intentionally — first-party code stays scannable). Suppressed via inline `// nosemgrep:` annotation at the call site.

```c
static char hex[48 * 3 + 1];      // 145-byte static buffer
char *p = hex;
for (int i = 0; i < len && i < 48; i++) {
    if (i > 0) *p++ = ' ';
    sprintf(p, "%02X", buf[i]);   // line 112 — writes 3 bytes (2 hex + NUL)
    p += 2;                       // NUL gets overwritten next iter
}
```

**Why FP:** loop bound `i < 48`, per-iteration write is 1 space + `sprintf("%02X")` = 3 bytes worst case, max total `48 * 3 = 144` < 145-byte buffer. Bounded by construction.

### 3. `gitlab.security_code_scan.SCS0001-1` — `UI/FileOperations.cs:102`

```csharp
using var proc = Process.Start(new ProcessStartInfo
{
    FileName = config.DalayaPatcherPath,   // ← from local config
    WorkingDirectory = ...,
    UseShellExecute = true
});
```

**Why FP:** `config.DalayaPatcherPath` is read from `eqswitch-config.json` next to the running exe. There is no remote-attacker write path to that file. `File.Exists(config.DalayaPatcherPath)` is checked at line 94. SCS0001 fires on the *pattern* (`Process.Start` with non-literal path), not on the threat-model question (is the path attacker-influenced?).

### 4. `gitlab.security_code_scan.SCS0001-1` — `UI/UpdateDialog.cs:419`

```csharp
Process.Start(new ProcessStartInfo(exePath)
{
    Arguments = "--after-update",
    UseShellExecute = true
});
```

**Why FP — but with a catch (now closed):** `exePath` is `Environment.ProcessPath` resolved at line 290, before the download. `Arguments` is a hardcoded literal. SCS0001's literal pattern-match concern is benign here.

**Real concern that was hiding behind the FP — now fixed:** by the time `Process.Start` runs at line 411, the rename dance at line 406 has overwritten the on-disk exe with the freshly-downloaded payload. So the relaunched binary IS attacker-controlled if integrity verification was bypassed. The original code at lines 369-372 had a bare `catch { /* proceed without verification */ }` that swallowed any exception from the SHA256SUMS fetch — meaning an MITM could drop the SHA256SUMS request and silently bypass the hash check. **Fixed in the same commit as this triage doc** by converting to fail-closed:

- The `catch (Exception ex)` block now logs, deletes the zip, shows an error, and returns. Network blips that take down SHA256SUMS now abort the update (user retries) instead of fail-opening.
- The release workflow at `.github/workflows/release.yml` now generates `SHA256SUMS` alongside the zip — previously it didn't, which made the entire `if (!string.IsNullOrEmpty(_hashFileUrl))` block dead code on shipped releases. Without this, the catch fix has nothing to guard.

The SCS0001 finding itself remains a UI-pattern FP — dismiss as before.

## Noise rule to disable platform-side

`ai.generic.detect-generic-ai-anthprop.detect-generic-ai-anthprop` — fires on any string containing "Anthropic" or path matching `.claude/`. In this repo it flags:

- `.gitignore:53` — the `.claude/` ignore entry
- A handoff-document path in a comment in `Native/eqmain_widgets_mq2style.h`
- Three other comment / string mentions

This rule has no security signal in this repo. Disable it for `itsnateai/eqswitch` in the Semgrep policy: https://semgrep.dev/orgs/itsnateai/policies → search `detect-generic-ai-anthprop` → set to **Disabled**.

## Status (2026-04-29 post-triage)

The Semgrep workflow now uploads SARIF to GitHub's Security tab in addition to the Semgrep AppSec Platform — `gh api repos/itsnateai/eqswitch/code-scanning/alerts` is the canonical source of truth.

**10 alerts dismissed via API** with rationale stored on each alert:

| # | Rule | Reason | Location |
|---|---|---|---|
| 1 | `ai.generic.detect-generic-ai-anthprop` | won't fix | `.gitignore:54` (literal pattern required) |
| 11 | `gitlab.flawfinder.strcpy-1` | false positive | `Native/eqmain_widgets.cpp:481` (5-byte literal → 40-byte buffer) |
| 51 | `gitlab.flawfinder.strcpy-1` | false positive | `Native/eqmain_widgets.cpp:477` (7-byte literal → 40-byte buffer) |
| 43 | `gitlab.flawfinder.sprintf-1.*` | false positive | `Native/net_debug.cpp:112` (bounded loop, static buffer) |
| 45-50 | `gitlab.security_code_scan.SCS0001-1` | false positive | All 6 `Process.Start` UI sites (local-config paths or self-relaunch) |

**36 alerts remain open — all flawfinder bound-checking complaints in first-party `Native/` C++:**

- 26× `gitlab.flawfinder.memcpy-1.*` — `memcpy` calls in `Native/pattern_scan.cpp`, `net_debug.cpp`, `mq2_bridge.cpp`, `eqswitch-hook.cpp`, etc.
- 6× `gitlab.flawfinder.strncpy-1` — bounded copies in `mq2_bridge.cpp`, `login_state_machine.cpp`
- 4× `gitlab.flawfinder.strlen-1.*` — strlen patterns in `mq2_bridge.cpp`

These are **NOT yet triaged**. They are flagged as boilerplate buffer-pattern warnings that *could* indicate real off-by-ones or *could* be bounded-by-construction. They warrant a dedicated review pass — not a blanket "false positive" dismissal. Tracked as a follow-up work item.

## Re-suppressing on next scan

GitHub's SARIF correlation keeps dismissed alerts dismissed across re-scans as long as the underlying finding (file/line/rule) is "the same finding." Re-scanning will not resurface the 10 dismissals listed above. Inline `// nosemgrep:` annotations were tried and **did not work** for community-tier rules in this Semgrep policy — the API-dismissal path is canonical.
