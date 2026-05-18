# v8 Step 3 — Live Widget Tree Discovery (post-Step-2B)

**Paste into next session to continue the v8 MQ2-mitigation arc.**

Repo: `X:\_Projects\EQSwitch\`. Read `CLAUDE.md` first.

## What's shipped (Step 2B, commit `e8faf9b`)

- Correct SetWindowText slot offset: 0x124 (was 0x128 — crashed).
- Exact-vtable class validation gate (`IsEQMainWidgetClass` + `GetEQMainWidgetClassName`)
  rejects CXMLDataPtr definitions cleanly, no SEH.
- `mq2_bridge::SetEditText` + `ClickButton` route through eqmain-side
  `GetSetWindowTextFor` / `GetWndNotificationFor` with graceful fallback.
- `--test-autologin [alias] [--timeout N]` CLI flag on `EQSwitch.exe` drives a
  real login end-to-end, greps log for native-path SEH, kills eqgame, returns
  exit code. Used for unattended verification.

**Live test (2026-04-17):** 74s login, reached character select, **zero SEH**
in the native dispatch path (was 64 per login pre-fix). The class gate fires
4x per login — all pointers rejected as non-widget (Phase 5 heap-scan
returns CXMLDataPtr definitions, heap cross-ref has pervasive false positives).
Login continues via keyboard-injection fallback.

## What Step 3 is for

Native dispatch never actually FIRES on real widgets today — every candidate is
class-rejected. Goal: get real live CXWnd pointers into `SetEditText` and
`ClickButton` so the injected DLL can drive login without keyboard injection
fallback (lower latency, silent-foreground wins, eventual path to dropping MQ2's
dinput8.dll dependency entirely).

### Why the current heap cross-ref is broken

Log evidence 2026-04-17 (see memory `project_eqswitch_v8_step2b_shipped.md`):
four DIFFERENT widget names resolved to the SAME cross-ref address `0x130FE7C4`
at different DWORD offsets (`+0x20`, `+0x26C`, `+0x3E0`, `+0x3EC`). That
address is some heap block that coincidentally contains DWORDs matching
multiple def addresses — classic false-positive storm from scanning the
entire heap for numeric matches.

Phase 6 notes already exhausted this approach (see memory
`project_eqswitch_v7_phase6_live_cxwnd.md`). Step 3 should stop trying to
cross-ref definitions to live widgets. Walk a tree from a known-live root
instead.

## The Step 3 plan: tree walk from a live root

### Candidate roots at login time

1. **`g_pLoginController`** — resolved by the GiveTime detour, guaranteed live
   during login. Has N CXWnd* fields pointing at login screens:
   - `LoginController@08C70318` with 7 CXWnd-like fields (from latest log).
   - Each field is probably a top-level screen (main menu, login screen,
     server select, kick-session dialog, etc.). Need to recurse into each.

2. **`pinstCEQMainWnd`** — NULL during login per current log evidence (only
   populates at main menu / char select). Not useful for login widgets.

3. **Memory walk from `LoginController` by vtable-filter** — walk each CXWnd*
   field, verify its vtable matches `CSidlScreenWnd` / `CXWnd` (via
   `IsEQMainWidgetClass`), then BFS through its children.

### Writing the walker

```cpp
// Dalaya ROF2 CXWnd offsets (from reference_eqswitch_dalaya_rof2_offsets.md):
constexpr uint32_t CXWND_OFF_SIBLING = 0x08;  // next CXWnd at same level
constexpr uint32_t CXWND_OFF_CHILD   = 0x10;  // first child CXWnd
constexpr uint32_t CXWND_OFF_SIDL_ID = 0xD8;  // SIDL ID (int)
constexpr uint32_t CXWND_OFF_WINDOWTEXT = 0x1A8; // CXStr inline (16 bytes)

// m_pSidlPiece offset discovered dynamically per widget. NOT constant —
// first cross-ref run said +0x278 for UsernameEdit, but subsequent
// widgets gave wrong offsets due to false positives. The tree walker
// sidesteps this by NOT using m_pSidlPiece at all. Instead: walk ALL
// children under a known root, log every CXWnd's vtable + class, and
// let the caller pick by context (e.g. "first CEditWnd under the login
// screen is UsernameEdit, second is PasswordEdit" — order-based).

struct TreeNode { void *pWnd; int depth; };

static void *WalkTreeForFirstClass(void *pRoot, const char *className, int maxDepth = 4) {
    if (!pRoot || !className) return nullptr;
    TreeNode stack[256];
    int top = 0;
    stack[top++] = { pRoot, 0 };

    while (top > 0) {
        TreeNode cur = stack[--top];
        if (cur.depth > maxDepth || !cur.pWnd) continue;

        // Class check
        const char *cls = EQMainOffsets::GetEQMainWidgetClassName(cur.pWnd);
        if (cls && strcmp(cls, className) == 0) return cur.pWnd;

        // Enqueue child + siblings (SEH-wrapped reads)
        __try {
            void *child = *(void **)((uintptr_t)cur.pWnd + CXWND_OFF_CHILD);
            if (child && top < 256) stack[top++] = { child, cur.depth + 1 };
            void *sib = *(void **)((uintptr_t)cur.pWnd + CXWND_OFF_SIBLING);
            if (sib && top < 256) stack[top++] = { sib, cur.depth };
        } __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
    }
    return nullptr;
}
```

### Harder question: by-name resolution

Walking finds all widgets; matching by NAME is the remaining puzzle. Options:

**A. By visible label text (+0x1A8 WindowText CXStr)**
   - UsernameEdit, PasswordEdit: no visible label (edit controls are blank)
   - ConnectButton: label is "LOGIN" or similar
   - YESNO_YesButton: label is "Yes"
   - **Partial coverage — not a full solution.**

**B. By position on screen (+0x?? position member)**
   - Username is at top, password below, connect at bottom
   - Fragile across UI variants; needs screen-coord member offset discovery.

**C. By order within parent (first CEditWnd = Username, second = Password)**
   - Relies on stable child enumeration order
   - Worked for MQ2 historically; check `_.src/_srcexamples/macroquest-rof2-emu`
     for the convention.

**D. By SIDL ID (+0xD8 int)**
   - Best candidate: SIDL IDs are compile-time constants set by the XML loader.
   - Need to map SIDL ID → name. Dump the XML or find the ID table in eqmain.

**E. Port MQ2's ReinitializeWindowList**
   - The "gold standard" approach. MQ2 rebuilds an authoritative name→ptr map
     on every frame via this function. Source at
     `X:/_Projects/_.src/_srcexamples/macroquest-rof2-emu/src/eqlib/src/EQLib.cpp`
     or similar. Find the function signature, resolve its address in eqmain
     (pattern scan or RVA), call it from the GiveTime detour.

**Recommended order for next session:** start with E (MQ2 port), fall back to
C (positional) if the function signature is too intricate.

## Files to touch

- `Native/mq2_bridge.cpp` — add tree walker; call it in `FindWindowByName`'s
  first tier (before definition heap-scan). Cache results in `g_widgetCache`
  keyed by SIDL ID or name.
- `Native/eqmain_offsets.{h,cpp}` — optional new helpers if ReinitializeWindowList
  ports cleanly (new RVA constant + fn pointer).
- `Native/mq2_bridge.h` — possibly export tree-walk primitive for other callers.

## Verification

Re-run `EQSwitch.exe --test-autologin --timeout 180`. Log should show:
- Every widget interaction routing through `GetSetWindowTextFor` / `GetWndNotificationFor`
- Zero "not a known widget class" skips
- Zero SEH in native path
- Login reaches charselect without triggering the kick-session retry loop
  (because the YES button click would now actually land)

## Reference material

- `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v8_step2a_shipped.md`
- `C:/Users/nate/.claude/projects/X---Projects/memory/project_eqswitch_v8_step2b_vtable_probe.md` (crashed-probe root cause)
- `C:/Users/nate/.claude/projects/X---Projects/memory/reference_eqswitch_dalaya_rof2_offsets.md` (struct offsets)
- `C:/Users/nate/.claude/projects/X---Projects/memory/reference_eqswitch_mq2_eqmain_detection.md` (MQ2 architecture)
- `X:/_Projects/eqswitch/dumps/vtable_dump.txt` (all CXWnd-family vtables with 90 slots each)
- `X:/_Projects/eqswitch/dumps/slot_diffs.txt` (per-class overrides + dinput8 export signatures)

## Parting thought

The slot+class-validation fix was the blocking safety fix. Step 3 is the
performance/correctness fix. Step 2B's gracefully-falling-back behavior means
Step 3 can be iterated on without fear — if Step 3 breaks, Step 2B still
catches. No user-visible regression is possible.
