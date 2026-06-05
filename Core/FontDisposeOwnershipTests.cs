// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System;
using System.Drawing;
using System.Windows.Forms;
using EQSwitch.UI;

namespace EQSwitch.Core;

/// <summary>
/// Regression guard for the disposed-font crash class: <c>DarkTheme.DisposeControlFonts</c> must
/// dispose only the fonts a control OWNS, never a font it merely INHERITED.
///
/// The bug (shipped, recurring): the tree-walker disposed every <c>Control.Font</c> not in the
/// <c>SharedFonts</c> static set. But an unstyled control's <c>Font</c> getter returns its parent's
/// font — ultimately <c>Control.DefaultFont</c> (the single app-wide instance from
/// <c>Application.SetDefaultFont</c>). Disposing that freed the font for the WHOLE process, so the
/// next control created (any dialog) crashed in <c>SetWindowFont → ToHfont</c> and the next repaint
/// crashed in <c>Font.GetHeight</c> — both surfacing as <c>ArgumentException: Parameter is not valid</c>.
/// (The <c>RepairDefaultFont</c> reflection band-aid existed precisely because this kept happening.)
///
/// Invoked via <c>--test-font-dispose</c>. RunAll() returns 0 if ownership is respected, 1 otherwise.
/// Note: the DefaultFont case is checked LAST because, before the fix, it poisons the process — by
/// then every assertion is recorded and we exit immediately.
/// </summary>
public static class FontDisposeOwnershipTests
{
    public static int RunAll()
    {
        // Mirror Program.Main so Control.DefaultFont is the disposable app instance, exactly as in
        // production. Safe here: no control/handle has been created yet in the --test process.
        try { Application.SetDefaultFont(new Font("Segoe UI", 9f)); } catch { /* best effort */ }

        bool pass = true;

        // ── Test 1: a child that INHERITS its parent's font must not free that shared instance ──
        // (Does not touch Control.DefaultFont, so it never poisons the process.)
        using (var owner = new Form())
        {
            var sharedFont = new Font("Segoe UI", 9f);   // owned by the form, inherited down the tree
            owner.Font = sharedFont;
            var inheritingChild = new Label();           // no explicit font → Font getter returns sharedFont
            owner.Controls.Add(inheritingChild);

            if (!ReferenceEquals(inheritingChild.Font, sharedFont))
            {
                Console.Error.WriteLine("FontDisposeOwnershipTests: SETUP FAIL — child did not inherit parent font.");
                return 1;
            }

            DarkTheme.DisposeControlFonts(owner);

            if (FontDisposed(sharedFont))
            {
                Console.Error.WriteLine("FontDisposeOwnershipTests: FAIL — inherited (parent) font was disposed via its child.");
                pass = false;
            }
        }

        // ── Test 2: an OWNED font (explicitly set, distinct instance) must still be disposed ──
        // (Guards against the fix over-skipping and leaking real per-form fonts.)
        using (var form = new Form())
        {
            var owned = new Font("Consolas", 11f);
            var lbl = new Label { Font = owned };
            form.Controls.Add(lbl);

            DarkTheme.DisposeControlFonts(form);

            if (!FontDisposed(owned))
            {
                Console.Error.WriteLine("FontDisposeOwnershipTests: FAIL — explicitly-owned control font was NOT disposed (leak / over-skip).");
                pass = false;
            }
        }

        // ── Test 3 (LAST — poisons the process before the fix): the literal reported crash ──
        // An unstyled control inherits Control.DefaultFont; disposing it would brick the whole app.
        using (var form = new Form())
        {
            var def = Control.DefaultFont;
            var tb = new TextBox();                       // no explicit font → inherits Control.DefaultFont
            form.Controls.Add(tb);

            if (!ReferenceEquals(tb.Font, def))
            {
                Console.Error.WriteLine("FontDisposeOwnershipTests: SETUP FAIL — control did not inherit Control.DefaultFont.");
                return 1;
            }

            DarkTheme.DisposeControlFonts(form);

            if (FontDisposed(def))
            {
                Console.Error.WriteLine("FontDisposeOwnershipTests: FAIL — Control.DefaultFont (app-wide font) was disposed. This is the button-click crash.");
                pass = false;
            }
        }

        if (pass)
            Console.WriteLine("FontDisposeOwnershipTests: PASS — DisposeControlFonts frees owned fonts only, never inherited/default fonts.");
        return pass ? 0 : 1;
    }

    /// <summary>True if the font's GDI+ handle is freed — calling into it throws the same
    /// <c>ArgumentException: Parameter is not valid</c> seen in the production crash.</summary>
    private static bool FontDisposed(Font f)
    {
        try { f.GetHeight(); return false; }
        catch (ArgumentException) { return true; }
    }
}
