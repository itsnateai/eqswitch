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

### 1. `gitlab.flawfinder.strcpy-1` — `Native/eqmain_widgets_mq2style.h:477,481`

```c
strcpy(preview, "(empty)");
strcpy(preview, "(SEH)");
```

**Why FP:** the source is a string literal of compile-time-known length. `preview` is a fixed-size stack buffer sized to comfortably exceed both literals. flawfinder cannot verify either fact, so it flags the pattern unconditionally.

### 2. `gitlab.flawfinder.sprintf-1.*` — `Native/hde32.c:112` (would be silenced by `.semgrepignore` once policy honors it)

```c
sprintf(p, "%02X", buf[i]);
```

**Why FP:** upstream HDE32 (third-party). Each call writes exactly 2 bytes plus NUL into a caller-sized buffer. Bounded by the format specifier itself.

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

### 4. `gitlab.security_code_scan.SCS0001-1` — `UI/UpdateDialog.cs:411`

```csharp
Process.Start(new ProcessStartInfo(exePath)
{
    Arguments = "--after-update",
    UseShellExecute = true
});
```

**Why FP:** `exePath` is the running EQSwitch.exe's own path (self-relaunch after the in-place self-update writes the new binary). `Arguments` is a hardcoded literal. The only way this becomes a vulnerability is if the running exe has already been replaced with a malicious binary — at which point the attacker already has code execution and does not need this Process.Start to exploit anything.

## Noise rule to disable platform-side

`ai.generic.detect-generic-ai-anthprop.detect-generic-ai-anthprop` — fires on any string containing "Anthropic" or path matching `.claude/`. In this repo it flags:

- `.gitignore:53` — the `.claude/` ignore entry
- A handoff-document path in a comment in `Native/eqmain_widgets_mq2style.h`
- Three other comment / string mentions

This rule has no security signal in this repo. Disable it for `itsnateai/eqswitch` in the Semgrep policy: https://semgrep.dev/orgs/itsnateai/policies → search `detect-generic-ai-anthprop` → set to **Disabled**.

## Next-scan acceptance criteria

After the steps above, the next push to `main` should report **≤ 5 non-blocking findings**, all of them low-severity `flawfinder.memcpy-1` patterns in our own (non-upstream) `Native/` code that warrant a separate review pass — not a triage doc entry. If findings exceed 5, this doc has gone stale and needs to be re-reviewed against the new scan output.
