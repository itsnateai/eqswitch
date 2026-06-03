// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using EQSwitch.UI;

namespace EQSwitch.Core;

/// <summary>
/// Unit tests for the self-update asset-matching logic in <see cref="UpdateDialog"/> — the
/// dual-asset (versioned + canonical) release rename shipped in v3.24.18. Pure string logic,
/// no WinForms (only static methods are called; the Form is never constructed). Invoked via the
/// --test-update-assets CLI flag from Program.cs. RunAll() returns 0 on all passes, 1 on any
/// assertion failure; Program.cs maps unhandled exceptions to 2.
///
/// Guards three contracts:
///  • IsCanonicalZipName / IsVersionedZipName are disjoint, and "versioned" requires a
///    version-shaped suffix (EQSwitch-&lt;digit&gt;…) so a future EQSwitch-symbols.zip / -debug.zip
///    can't be mistaken for the app bundle.
///  • ParseHashForZipBundle is keyed off the EXACT chosen asset name (anti-shadowing — a sibling
///    line in a multi-entry SHA256SUMS can't shadow the file we downloaded) and fails closed on
///    null/empty name or non-64-hex hashes.
///  • The GNU binary-mode (`*name`), GNU text-mode (two-space), and BSD-tag formats all parse.
/// </summary>
public static class UpdateAssetMatchTests
{
    // Two distinct, well-formed 64-char hex digests.
    private const string HReal = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string HEvil = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef0";

    public static int RunAll()
    {
        int failures = 0;

        // ── Canonical predicate: exactly "EQSwitch.zip", case-insensitive ──────────
        failures += AssertTrue("canonical: EQSwitch.zip", UpdateDialog.IsCanonicalZipName("EQSwitch.zip"));
        failures += AssertTrue("canonical: eqswitch.ZIP (case-insens)", UpdateDialog.IsCanonicalZipName("eqswitch.ZIP"));
        failures += AssertTrue("canonical: NOT the versioned name", !UpdateDialog.IsCanonicalZipName("EQSwitch-3.24.18.zip"));
        failures += AssertTrue("canonical: NOT a prefix sibling", !UpdateDialog.IsCanonicalZipName("EQSwitchSetup.zip"));
        failures += AssertTrue("canonical: empty → false", !UpdateDialog.IsCanonicalZipName(""));

        // ── Versioned predicate: EQSwitch-<digit>…zip only ─────────────────────────
        failures += AssertTrue("versioned: EQSwitch-3.24.18.zip", UpdateDialog.IsVersionedZipName("EQSwitch-3.24.18.zip"));
        failures += AssertTrue("versioned: single-digit EQSwitch-7.zip", UpdateDialog.IsVersionedZipName("EQSwitch-7.zip"));
        failures += AssertTrue("versioned: case-insens prefix/suffix", UpdateDialog.IsVersionedZipName("eqswitch-3.0.0.ZIP"));
        // The hardening: non-version-shaped siblings must NOT be mistaken for the app.
        failures += AssertTrue("versioned: reject EQSwitch-symbols.zip", !UpdateDialog.IsVersionedZipName("EQSwitch-symbols.zip"));
        failures += AssertTrue("versioned: reject EQSwitch-debug.zip", !UpdateDialog.IsVersionedZipName("EQSwitch-debug.zip"));
        failures += AssertTrue("versioned: reject EQSwitch-.zip (no version)", !UpdateDialog.IsVersionedZipName("EQSwitch-.zip"));
        failures += AssertTrue("versioned: reject canonical (no hyphen)", !UpdateDialog.IsVersionedZipName("EQSwitch.zip"));
        failures += AssertTrue("versioned: reject non-zip", !UpdateDialog.IsVersionedZipName("EQSwitch-3.24.18.exe"));

        // ── Disjointness: nothing satisfies both predicates ────────────────────────
        foreach (var name in new[] { "EQSwitch.zip", "EQSwitch-3.24.18.zip", "EQSwitch-symbols.zip", "EQSwitch.exe" })
        {
            bool both = UpdateDialog.IsCanonicalZipName(name) && UpdateDialog.IsVersionedZipName(name);
            failures += AssertTrue($"disjoint: {name} not both", !both);
        }

        // ── ParseHashForZipBundle: dual-asset manifest (GNU binary-mode `*`) ────────
        // Real CI emits the `*name` (binary) form; both names share one digest.
        var dual =
            $"{HReal} *EQSwitch-3.24.18.zip\n" +
            $"{HReal} *EQSwitch.zip\n";
        failures += AssertTrue("parse: canonical line", HReal.Equals(UpdateDialog.ParseHashForZipBundle(dual, "EQSwitch.zip"), StringComparison.OrdinalIgnoreCase));
        failures += AssertTrue("parse: versioned line", HReal.Equals(UpdateDialog.ParseHashForZipBundle(dual, "EQSwitch-3.24.18.zip"), StringComparison.OrdinalIgnoreCase));

        // ── Anti-shadowing: a stray sibling line can't override the chosen name ─────
        var shadow =
            $"{HEvil} *EQSwitch-evil.zip\n" +
            $"{HReal} *EQSwitch.zip\n";
        failures += AssertTrue("anti-shadow: chosen name wins", HReal.Equals(UpdateDialog.ParseHashForZipBundle(shadow, "EQSwitch.zip"), StringComparison.OrdinalIgnoreCase));

        // ── Other accepted formats: GNU text (two-space), BSD-tag, CRLF ────────────
        failures += AssertTrue("parse: GNU two-space", HReal.Equals(UpdateDialog.ParseHashForZipBundle($"{HReal}  EQSwitch.zip\n", "EQSwitch.zip"), StringComparison.OrdinalIgnoreCase));
        failures += AssertTrue("parse: BSD-tag", HReal.Equals(UpdateDialog.ParseHashForZipBundle($"SHA256 (EQSwitch.zip) = {HReal}\n", "EQSwitch.zip"), StringComparison.OrdinalIgnoreCase));
        failures += AssertTrue("parse: CRLF line endings", HReal.Equals(UpdateDialog.ParseHashForZipBundle($"{HReal} *EQSwitch.zip\r\n", "EQSwitch.zip"), StringComparison.OrdinalIgnoreCase));

        // ── Fail-closed: null/empty name, missing entry, malformed hash → null ─────
        failures += AssertTrue("fail-closed: null name", UpdateDialog.ParseHashForZipBundle(dual, null) == null);
        failures += AssertTrue("fail-closed: empty name", UpdateDialog.ParseHashForZipBundle(dual, "") == null);
        failures += AssertTrue("fail-closed: no matching entry", UpdateDialog.ParseHashForZipBundle(dual, "EQSwitch-9.9.9.zip") == null);
        failures += AssertTrue("fail-closed: null content", UpdateDialog.ParseHashForZipBundle(null, "EQSwitch.zip") == null);
        failures += AssertTrue("fail-closed: short (non-64) hash", UpdateDialog.ParseHashForZipBundle("abc123 *EQSwitch.zip\n", "EQSwitch.zip") == null);
        failures += AssertTrue("fail-closed: 64 non-hex chars", UpdateDialog.ParseHashForZipBundle($"{new string('z', 64)} *EQSwitch.zip\n", "EQSwitch.zip") == null);

        Console.WriteLine(failures == 0
            ? "UpdateAssetMatchTests: ALL PASSED"
            : $"UpdateAssetMatchTests: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int AssertTrue(string label, bool ok)
    {
        if (ok) return 0;
        Console.WriteLine($"  FAIL {label}");
        return 1;
    }
}
