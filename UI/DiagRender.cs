// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai
#if DEBUG
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.UI;

/// <summary>
/// DEBUG-only DPI verification harness. Renders ONE form in isolation with a stub
/// <see cref="AppConfig"/> and screenshots it to a PNG, so a high-DPI Windows Sandbox
/// (or any scaled display) can capture exactly how a form looks at 125%/150% without a
/// human driving the live tray app. This is how the layout-container DPI rebuild is
/// verified — "I can't see 150%" is false; this makes the rendering observable.
///
/// Usage (publish self-contained, run inside a 150%-scaled Sandbox session):
///   EQSwitch.exe --diag-render-form SettingsForm --out C:\Out --tab 0
///     --tab N      select an initial TabControl tab (0-based)
///     --scale F    host-side font-growth SIMULATION (multiplies every control's font by F;
///                  fast inner-loop sanity check on a 100% display — NOT a substitute for a
///                  real 150% render, which also exercises non-font DPI effects)
///     --offscreen  render off-screen + capture via DrawToBitmap (non-intrusive: nothing pops
///                  onto the active desktop). Default captures real composited pixels on-screen.
///     --hold       leave the window open after capture instead of exiting
///
/// Known forms: "PilotCard" (layout-primitive proof), "SettingsForm", "ProcessManagerForm",
/// "ThemedMessageDialog", and the EQ Client Settings family ("EQClientSettingsForm",
/// "EQModelsForm", "EQChatSpamForm", "EQParticlesForm", "EQVideoModeForm", "EQKeymapsForm").
/// Writes &lt;FormName&gt;[-tabN][-simF].png + a diag-render.log line (size + DeviceDpi) to --out.
/// </summary>
internal static class DiagRender
{
    public static void Run(string[] args)
    {
        string formName = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1] : "SettingsForm";
        string outDir = GetArg(args, "--out") ?? AppDomain.CurrentDomain.BaseDirectory;
        int tab = int.TryParse(GetArg(args, "--tab"), out var t) ? t : 0;
        float simScale = float.TryParse(GetArg(args, "--scale"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 1f;
        bool hold = Array.IndexOf(args, "--hold") >= 0;
        bool offscreen = Array.IndexOf(args, "--offscreen") >= 0;
        bool prewarm = Array.IndexOf(args, "--prewarm") >= 0;   // build SettingsForm's lazy tabs via the PRODUCTION pre-warm path (offscreen, non-selected), THEN select `tab` — verifies pre-warm computes correct DPI widths

        // Mirror Program.Main's display setup exactly so the render matches production.
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(new Font("Segoe UI", 9f));

        Form form;
        try
        {
            // In pre-warm mode open on tab 0 so the target tab is built by the pre-warm path (not the
            // initialTab path), then selected below — the exact production sequence under test.
            form = BuildForm(formName, prewarm ? 0 : tab, prewarm);
        }
        catch (Exception ex)
        {
            WriteError(outDir, $"diag-build-error-{formName}.txt", ex);
            return;
        }

        form.StartPosition = FormStartPosition.Manual;
        form.Location = offscreen ? new Point(-32000, -32000) : new Point(24, 24);
        if (!offscreen) form.TopMost = true;   // clean on-screen capture for the Suzy real-150% run

        form.Shown += (_, _) =>
        {
            if (Math.Abs(simScale - 1f) > 0.001f)
            {
                ScaleFonts(form, simScale);
                // Emulate the window growing with DPI. The real forms scale ClientSize off
                // DeviceDpi; on a 100% host that never fires, so grow it here so fill-fields get
                // their extra room — a faithful preview of the real 150% render.
                if (!form.AutoSize)
                    form.ClientSize = new Size(
                        (int)Math.Round(form.ClientSize.Width * simScale),
                        (int)Math.Round(form.ClientSize.Height * simScale));
            }
            if (prewarm)
            {
                // Pump the message loop UNTIL the pre-warm BeginInvokes have actually built the target tab,
                // then assert it — this guarantees the capture is of the PRE-WARM build (tab built while
                // NON-selected), not the on-click build that SelectedIndex would otherwise trigger. A single
                // DoEvents isn't enough: the two per-tab BeginInvokes + the builds' own layout messages can
                // span more than one drain. The printed flag is the proof the --prewarm verification is valid.
                var builtField = typeof(SettingsForm).GetField("_tabBuilt",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                bool prewarmBuilt = false;
                for (int i = 0; i < 100 && !prewarmBuilt; i++)
                {
                    Application.DoEvents();
                    if (builtField?.GetValue(form) is bool[] tb && tab >= 0 && tab < tb.Length) prewarmBuilt = tb[tab];
                    if (!prewarmBuilt) System.Threading.Thread.Sleep(10);
                }
                Console.WriteLine($"[--prewarm] tab {tab} built by pre-warm while NON-selected (before any select): {prewarmBuilt}");
                foreach (Control c in form.Controls)
                    if (c is TabControl tc) { tc.SelectedIndex = Math.Min(tab, tc.TabCount - 1); break; }
                Application.DoEvents();   // let the now-selected, pre-built tab lay out + size
            }
            // One tick after Shown so owner-draw tabs + DWM caption have painted + layout settled.
            var capture = new System.Windows.Forms.Timer { Interval = 1000 };
            capture.Tick += (_, _) =>
            {
                capture.Stop();
                capture.Dispose();
                Capture(form, formName, tab, simScale, outDir, offscreen);
                if (!hold) Application.Exit();
            };
            capture.Start();
        };

        Application.Run(form);
    }

    private static void Capture(Form form, string formName, int tab, float simScale, string outDir, bool offscreen)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var size = form.Size;
            using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            if (offscreen)
            {
                // WM_PRINT render — no window on the active desktop, captures the client/content.
                form.DrawToBitmap(bmp, new Rectangle(0, 0, size.Width, size.Height));
            }
            else
            {
                form.Activate();
                form.BringToFront();
                // Real composited pixels (owner-draw tabs, accent bars, DWM caption) = what the user sees.
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(form.Bounds.Location, Point.Empty, size);
            }
            string suffix = (tab > 0 ? $"-tab{tab}" : "")
                + (Math.Abs(simScale - 1f) > 0.001f ? $"-sim{simScale.ToString("0.0#", CultureInfo.InvariantCulture)}" : "");
            string file = Path.Combine(outDir, $"{formName}{suffix}.png");
            bmp.Save(file, ImageFormat.Png);
            File.AppendAllText(Path.Combine(outDir, "diag-render.log"),
                $"{formName}{suffix}: {size.Width}x{size.Height} DeviceDpi={form.DeviceDpi}\n");
        }
        catch (Exception ex)
        {
            WriteError(outDir, $"diag-capture-error-{formName}.txt", ex);
        }
    }

    /// <summary>
    /// Reproduces the disposed-font crash class at the FORM level (what the static
    /// FontDisposeOwnershipTests can't reach): build a real form, Show it (so child handles + grid
    /// cell-styles materialize), Dispose it, then create a fresh TextBox + ComboBox and force their
    /// handle creation — the exact site that throws "ArgumentException: Parameter is not valid" when
    /// a shared/default font was freed on close. Also checks Control.DefaultFont and the DarkTheme
    /// shared statics directly. Returns 0 if the cycle is clean, 1 if the crash reproduces.
    /// Invoked via <c>--test-dispose-cycle [FormName]</c> (default ProcessManagerForm).
    /// </summary>
    public static int RunDisposeCycle(string formName)
    {
        try { Application.SetDefaultFont(new Font("Segoe UI", 9f)); } catch { /* best effort */ }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }

        var def = Control.DefaultFont;
        // Must mirror DarkTheme.SharedFonts exactly — a regression that frees ANY shared static must
        // be caught. (Missing entries = blind spots: TabFontBold/TabFontRegular were omitted at first.)
        var sharedNames = new (string name, Font f)[]
        {
            ("FontUI9", DarkTheme.FontUI9), ("FontUI85", DarkTheme.FontUI85), ("FontUI75", DarkTheme.FontUI75),
            ("FontUI75Italic", DarkTheme.FontUI75Italic), ("FontSemibold9", DarkTheme.FontSemibold9),
            ("FontSemibold95", DarkTheme.FontSemibold95),
            ("TabFontBold", DarkTheme.TabFontBold), ("TabFontRegular", DarkTheme.TabFontRegular),
        };

        bool bad = false;
        try
        {
            var form = BuildForm(formName, 0);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            form.Close();
            form.Dispose();
            Application.DoEvents();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DisposeCycle({formName}): build/dispose threw — {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        if (FontDisposed(def))
        {
            Console.Error.WriteLine($"DisposeCycle({formName}): FAIL — Control.DefaultFont was disposed on close (process-wide brick).");
            bad = true;
        }
        foreach (var (name, f) in sharedNames)
            if (FontDisposed(f))
            {
                Console.Error.WriteLine($"DisposeCycle({formName}): FAIL — DarkTheme.{name} (shared) was disposed on close.");
                bad = true;
            }

        // The literal reported crash: a fresh control's handle creation calls SetWindowFont→ToHfont.
        try
        {
            using var probe = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false };
            probe.Controls.Add(new TextBox());
            probe.Controls.Add(new ComboBox());
            probe.Show();
            Application.DoEvents();
            probe.Close();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"DisposeCycle({formName}): FAIL — fresh control crashed after close: {ex.Message}");
            bad = true;
        }

        Console.WriteLine(bad
            ? $"DisposeCycle({formName}): FAIL — closing this form bricks fonts for later forms."
            : $"DisposeCycle({formName}): PASS — no shared/default font disposed; later forms create cleanly.");
        return bad ? 1 : 0;
    }

    /// <summary>
    /// Runtime reproduction + permanent guard for the lazy-tab refactor (Video + Accounts are built on
    /// first view, not at construction). Verifies the HARD CONSTRAINT: clicking Save WITHOUT ever opening
    /// the Video or Accounts tab must NOT corrupt their config — no field clobbered to a class default.
    /// Builds a real SettingsForm with DISTINCTIVE non-default Video/Accounts values, Shows it on the
    /// General tab (so Video + Accounts stay unbuilt shells), confirms they really are unbuilt, then runs
    /// the REAL ApplySettings (via reflection — it's private) and asserts every Video/Accounts field
    /// round-tripped through _config -> EnsureAllTabsBuilt -> controls -> newConfig. Also reports the lazy
    /// first-open cost (ctor+Show) vs the deferred Video+Accounts build that moved off the open path.
    /// Returns 0 if the round-trip is clean, 1 if any field was corrupted, 2 if the setup was invalid.
    /// Invoked via <c>--test-lazy-save</c>. This is the red->green repro for the lazy-save corruption class.
    /// </summary>
    public static int RunLazySave()
    {
        EQSwitch.Core.FileLogger.Initialize();   // so SettingsForm's perf-canary Info line lands in eqswitch.log (the --test path skips Program's Initialize)
        try { Application.SetDefaultFont(new Font("Segoe UI", 9f)); } catch { /* best effort */ }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }

        // Isolated EQ path so any eqclient.ini write (VideoSaveToIni runs inside ApplySettings) + config
        // save land in temp, never a real install. Seed a [VideoMode] so PopulateVideoFromIni does a real
        // disk read (a representative slice of the deferred first-open cost).
        string eqDir = Path.Combine(Path.GetTempPath(), "eqswitch-lazysave-test");
        Directory.CreateDirectory(eqDir);
        File.WriteAllText(Path.Combine(eqDir, "eqclient.ini"),
            "[VideoMode]\r\nWidth=1920\r\nHeight=1080\r\nXOffset=0\r\nYOffset=0\r\n");
        // ApplySettings' Exe guard pops a MODAL "exe not found" dialog (which hangs a headless run) if the
        // configured exe isn't present in EQPath — drop a stub eqgame.exe next to the ini so the guard passes.
        File.WriteAllText(Path.Combine(eqDir, "eqgame.exe"), "");

        // Distinctive, non-default values for EVERY Video + Accounts field ApplySettings reads, so a
        // clobber to a class default is detectable.
        var input = new AppConfig { IsFirstRun = false, EQPath = eqDir };
        input.Launch.ExeName = "eqgame.exe";                 // matches the stub above so the Exe guard stays quiet
        input.Launch.Arguments = "patchme";                  // == PopulateFromConfig's loaded value so the Args-change modal never fires (headless-hang guard)
        input.Layout.WindowMode = WindowMode.Windowed;       // Video: _chkWindowedMode
        input.Layout.DarkTitlebar = true;                    // Video: _chkDarkTitlebar
        input.Layout.TitlebarOffset = 17;                    // Video: _nudTitlebarOffset
        input.Layout.BottomOffset = 29;                      // Video: _nudBottomOffset
        input.Layout.TopOffset = 11;                         // Video: _nudVideoTopOffset
        input.Layout.HorizontalNudgePx = 4;                  // Video: _nudHorizontalNudge
        input.Layout.UseHook = true;                         // Video: _chkUseHook
        input.Layout.Mode = "multimonitor";                  // Video: _chkVideoMultiMon
        input.TooltipDurationMs = 1234;                      // Video: _nudTooltipDuration
        input.ShowTooltips = false;                          // Video: _chkShowTooltips (default true)
        input.EQClientIni.MaximizeWindow = true;             // Video: _chkMaximizeWindow
        input.LoginScreenDelayMs = 7000;                     // Accounts: _nudLoginScreenDelay (= 7.0)
        for (int i = 1; i <= 6; i++)                         // Accounts: realistic grid build cost
        {
            input.Accounts.Add(new Account { Name = $"note{i}", Username = $"user{i}", Server = "dalaya", Notes = $"box {i}" });
            input.Characters.Add(new Character { Name = $"Char{i}", AccountUsername = $"user{i}", AccountServer = "dalaya", CharacterSlot = i });
        }
        input.Team1Account1 = "char:Char1";                  // Accounts: _pendingTeam1A
        input.Team1Account2 = "char:Char2";                  // Accounts: _pendingTeam1B

        var sw = System.Diagnostics.Stopwatch.StartNew();
        AppConfig? captured = null;
        var form = new SettingsForm(input, c => captured = c, 0 /* initial tab = General */, () => { }, () => { }, null, false, prewarmLazyTabs: false);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        long tOpenMs = sw.ElapsedMilliseconds;   // lazy first-open: ctor + Show (General + the 3 other eager tabs only)

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        // Tab positions are READ from SettingsForm's private Tab* constants (not hardcoded) so this guard
        // tracks any future tab reorder automatically — a literal [1]/[2] silently mislabels after a swap.
        var sflags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        int idxVideo    = (int)typeof(SettingsForm).GetField("TabVideo", sflags)!.GetRawConstantValue()!;
        int idxAccounts = (int)typeof(SettingsForm).GetField("TabAccounts", sflags)!.GetRawConstantValue()!;

        // Prove the two lazy tabs (Video + Accounts) are NOT built yet — the exact scenario under test.
        // If they were eager, this guard would be meaningless: fail loud rather than pass vacuously.
        var built = (bool[])typeof(SettingsForm).GetField("_tabBuilt", flags)!.GetValue(form)!;
        if (built[idxVideo] || built[idxAccounts])
        {
            Console.Error.WriteLine($"LazySave: SETUP INVALID — Video/Accounts already built before Save (Video={built[idxVideo]}, Accounts={built[idxAccounts]}); the test would not exercise the unbuilt-tab path.");
            form.Dispose();
            return 2;
        }

        // Run the REAL Save with Video + Accounts STILL UNBUILT — ApplySettings must build + populate them
        // itself (EnsureAllTabsBuilt is its first statement). Do NOT pre-build here: pre-building would make
        // this test pass vacuously — it could no longer tell "ApplySettings builds the tabs" from "the
        // harness pre-built them", so deleting the guard inside ApplySettings would stay green.
        sw.Restart();
        bool applied;
        try
        {
            applied = (bool)typeof(SettingsForm).GetMethod("ApplySettings", flags)!.Invoke(form, null)!;
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            Console.Error.WriteLine($"LazySave: FAIL — ApplySettings threw {inner.GetType().Name}: {inner.Message} (did it stop building the lazy tabs before reading their controls?)");
            form.Dispose();
            return 1;
        }
        long tSaveMs = sw.ElapsedMilliseconds;   // Save now includes the deferred Video+Accounts build

        // Prove ApplySettings ITSELF built the lazy tabs (not the harness): this is what makes the test a
        // real guard for the EnsureAllTabsBuilt() call inside ApplySettings — remove that call and either
        // these flags stay false or the reflection-invoke above throws NRE.
        var builtAfter = (bool[])typeof(SettingsForm).GetField("_tabBuilt", flags)!.GetValue(form)!;
        if (!builtAfter[idxVideo] || !builtAfter[idxAccounts])
        {
            Console.Error.WriteLine($"LazySave: FAIL — after Save the lazy tabs are still unbuilt (Video={builtAfter[idxVideo]}, Accounts={builtAfter[idxAccounts]}); ApplySettings did not build them.");
            form.Dispose();
            return 1;
        }
        Application.DoEvents();
        form.Close();
        form.Dispose();

        if (!applied || captured == null)
        {
            Console.Error.WriteLine($"LazySave: FAIL — ApplySettings returned {applied}; captured={(captured == null ? "null" : "ok")} (did validation block the save?).");
            return 1;
        }

        var fails = new System.Collections.Generic.List<string>();
        void Check(string name, object? exp, object? act) { if (!Equals(exp, act)) fails.Add($"{name}: expected '{exp}', got '{act}'"); }
        // Video tab
        Check("Layout.WindowMode", WindowMode.Windowed, captured.Layout.WindowMode);
        Check("Layout.DarkTitlebar", true, captured.Layout.DarkTitlebar);
        Check("Layout.TitlebarOffset", 17, captured.Layout.TitlebarOffset);
        Check("Layout.BottomOffset", 29, captured.Layout.BottomOffset);
        Check("Layout.TopOffset", 11, captured.Layout.TopOffset);
        Check("Layout.HorizontalNudgePx", 4, captured.Layout.HorizontalNudgePx);
        Check("Layout.UseHook", true, captured.Layout.UseHook);
        Check("Layout.Mode", "multimonitor", captured.Layout.Mode);
        Check("TooltipDurationMs", 1234, captured.TooltipDurationMs);
        Check("ShowTooltips", false, captured.ShowTooltips);
        Check("EQClientIni.MaximizeWindow", true, captured.EQClientIni.MaximizeWindow);
        // Accounts tab
        Check("LoginScreenDelayMs", 7000, captured.LoginScreenDelayMs);
        Check("Accounts.Count", 6, captured.Accounts.Count);
        Check("Characters.Count", 6, captured.Characters.Count);
        Check("Team1Account1", "char:Char1", captured.Team1Account1);
        Check("Team1Account2", "char:Char2", captured.Team1Account2);
        Check("Account user3 preserved", true, captured.Accounts.Any(a => a.Username == "user3"));
        Check("Character Char4 slot preserved", 4, captured.Characters.FirstOrDefault(c => c.Name == "Char4")?.CharacterSlot);

        if (fails.Count > 0)
        {
            Console.Error.WriteLine($"LazySave: FAIL — {fails.Count} Video/Accounts field(s) corrupted by Save-without-opening-tab:");
            foreach (var f in fails) Console.Error.WriteLine("  - " + f);
            return 1;
        }

        // Part B — guard the VideoRestoreIni crash class (the gap a static review caught post-ship): the
        // eqclient.ini "Restore" button on the EAGER Paths tab calls PopulateVideoFromIni, which dereferences
        // Video-tab controls. On a fresh form shown on General (Video an unbuilt shell), invoking it must
        // NOT throw — PopulateVideoFromIni now builds the Video tab first.
        var form2 = new SettingsForm(input, _ => { }, 0, () => { }, () => { }, null, false, prewarmLazyTabs: false);
        form2.StartPosition = FormStartPosition.Manual;
        form2.Location = new Point(-32000, -32000);
        form2.ShowInTaskbar = false;
        form2.Show();
        Application.DoEvents();
        var built2 = (bool[])typeof(SettingsForm).GetField("_tabBuilt", flags)!.GetValue(form2)!;
        if (built2[idxVideo]) { Console.Error.WriteLine("LazySave: SETUP INVALID — Video already built on the Restore-path form."); form2.Dispose(); return 2; }
        try
        {
            typeof(SettingsForm).GetMethod("PopulateVideoFromIni", flags)!.Invoke(form2, null);
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            Console.Error.WriteLine($"LazySave: FAIL — PopulateVideoFromIni threw {inner.GetType().Name} with the Video tab unbuilt (the Paths-tab Restore crash): {inner.Message}");
            form2.Dispose();
            return 1;
        }
        var built2After = (bool[])typeof(SettingsForm).GetField("_tabBuilt", flags)!.GetValue(form2)!;
        form2.Close();
        form2.Dispose();
        if (!built2After[idxVideo])
        {
            Console.Error.WriteLine("LazySave: FAIL — PopulateVideoFromIni did not build the Video tab when called with it unbuilt.");
            return 1;
        }

        Console.WriteLine($"LazySave: PASS — ApplySettings built the unopened Video/Accounts tabs and preserved all 18 of their config fields; PopulateVideoFromIni is safe with Video unbuilt (the Paths-tab Restore path). " +
                          $"lazy first-open (ctor+Show, 4 eager tabs) = {tOpenMs}ms; Save incl. the deferred build = {tSaveMs}ms.");
        return 0;
    }

    private static bool FontDisposed(Font f)
    {
        if (f == null) return false;
        try { f.GetHeight(); return false; }
        catch (ArgumentException) { return true; }
    }

    /// <summary>
    /// Phase 1 save round-trip guard (docs/specs/2026-06-06-eqclient-settings-overhaul.md): construct
    /// EQClientSettingsForm against a temp eqclient.ini, assert the live-read display matches the file,
    /// flip a checkbox + a numeric, invoke SaveSettings, then assert ONLY the changed keys were written
    /// (right section + value, incl. the MaxFPS [Defaults] mirror), untouched managed keys were NOT
    /// rewritten, an unmanaged key survived, and no ghost copy leaked into another section.
    /// CLI: --test-eqclient-save.
    /// </summary>
    public static int RunEqClientSaveRoundtrip()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        var errors = new System.Collections.Generic.List<string>();
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eqswitch-p1-save");
        try
        {
            System.IO.Directory.CreateDirectory(tempDir);
            string iniPath = System.IO.Path.Combine(tempDir, "eqclient.ini");
            string[] fixture =
            {
                "[Defaults]",
                "Sound=FALSE",
                "ShowGrass=TRUE",
                "UnmanagedKeepMe=42",   // EQSwitch does not manage this key — must survive a Save
                "[Options]",
                "Sky=1",                 // Disable Sky should load UNCHECKED (sky on)
                "MaxFPS=100",
                "Anonymous=1",
            };
            System.IO.File.WriteAllLines(iniPath, fixture, System.Text.Encoding.Default);

            var config = new AppConfig { IsFirstRun = false, EQPath = tempDir };
            var form = new EQClientSettingsForm(config);   // ctor runs InitializeForm + LoadFromIni

            const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            const System.Reflection.BindingFlags PF = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            var bindings = (System.Collections.IEnumerable)typeof(EQClientSettingsForm).GetField("_bindings", BF)!.GetValue(form)!;

            object? FindBinding(string key)
            {
                foreach (var b in bindings)
                {
                    var setting = (IniSetting)b.GetType().GetField("Setting", PF)!.GetValue(b)!;
                    if (setting.Key == key) return b;
                }
                return null;
            }
            CheckBox? Chk(string key) => FindBinding(key) is { } b ? (CheckBox?)b.GetType().GetField("Check", PF)!.GetValue(b) : null;
            NumericUpDown? Nud(string key) => FindBinding(key) is { } b ? (NumericUpDown?)b.GetType().GetField("Numeric", PF)!.GetValue(b) : null;

            var skyChk = Chk("Sky");
            var soundChk = Chk("Sound");
            var maxNud = Nud("MaxFPS");
            if (skyChk == null || soundChk == null || maxNud == null)
            {
                errors.Add("could not resolve Sky/Sound/MaxFPS bindings via reflection");
            }
            else
            {
                // Display-vs-disk.
                if (skyChk.Checked) errors.Add("display: 'Disable Sky' should be UNCHECKED when Sky=1");
                if (!soundChk.Checked) errors.Add("display: 'Disable Sound' should be CHECKED when Sound=FALSE");
                if (maxNud.Value != 100) errors.Add($"display: MaxFPS should be 100, got {maxNud.Value}");

                // Change two settings, then Save.
                skyChk.Checked = true;     // 'Disable Sky' on -> Sky=0
                maxNud.Value = 60;
                typeof(EQClientSettingsForm).GetMethod("SaveSettings", BF)!.Invoke(form, null);

                var doc = EqClientIniDocument.Load(iniPath);
                void Eq(string section, string key, string? expect)
                {
                    var got = doc.Get(section, key);
                    if (got != expect) errors.Add($"save: [{section}] {key} expected '{expect ?? "<absent>"}', got '{got ?? "<absent>"}'");
                }
                Eq("Options", "Sky", "0");            // changed
                Eq("Options", "MaxFPS", "60");        // changed (canonical)
                Eq("Defaults", "MaxFPS", "60");       // changed (mirror)
                Eq("Defaults", "ShowGrass", "TRUE");  // untouched managed -> not rewritten, preserved
                Eq("Options", "Anonymous", "1");      // untouched managed -> preserved
                Eq("Defaults", "UnmanagedKeepMe", "42"); // unmanaged -> never clobbered
                Eq("Defaults", "Sky", null);          // no ghost copy in the other section
            }
            form.Dispose();
        }
        catch (Exception ex)
        {
            errors.Add($"CRASH: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }

        if (errors.Count == 0)
        {
            Console.WriteLine("EqClientSaveRoundtrip: PASS — display matches disk; Save wrote only the changed keys "
                + "(right section + mirror); untouched + unmanaged keys preserved; no ghost.");
            return 0;
        }
        Console.Error.WriteLine($"EqClientSaveRoundtrip: FAIL — {errors.Count} problem(s):");
        foreach (var e in errors) Console.Error.WriteLine("  - " + e);
        return 1;
    }

    /// <summary>
    /// Phase 1 launch-writer smoke (docs/specs/2026-06-06-eqclient-settings-overhaul.md): proves the
    /// NARROWED EQClientSettingsForm.EnforceOverrides still enforces Operational keys at launch
    /// (WindowedMode/Maximized) but no longer re-stamps Bucket-2 keys — even when the legacy
    /// ConfiguredKeys gate is populated (the old code would have rewritten them). This is the
    /// eqgame-wins guarantee. CLI: --test-eqclient-enforce.
    /// </summary>
    public static int RunEnforceOverridesSmoke()
    {
        var errors = new System.Collections.Generic.List<string>();
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eqswitch-p1-enforce");
        try
        {
            System.IO.Directory.CreateDirectory(tempDir);
            string iniPath = System.IO.Path.Combine(tempDir, "eqclient.ini");
            string[] fixture =
            {
                "[Defaults]",
                "Sound=TRUE",            // Bucket-2: must NOT be re-stamped at launch
                "WindowedMode=FALSE",    // Operational: must be flipped to TRUE
                "[Options]",
                "Sky=1",                 // Bucket-2: must NOT be re-stamped
                "MaxFPS=123",            // Bucket-2 numeric STRIPPED from launch enforce — must survive untouched
                "[VideoMode]",
                "WindowedMode=FALSE",
            };
            System.IO.File.WriteAllLines(iniPath, fixture, System.Text.Encoding.Default);

            var config = new AppConfig { IsFirstRun = false, EQPath = tempDir };
            config.Layout.SlimTitlebar = false;   // skip the monitor-geometry block; test the always-run operational + the narrowing
            // Simulate a legacy "user saved these" state — the OLD EnforceOverrides would re-stamp
            // them from config; the narrowed one must ignore ConfiguredKeys entirely.
            config.EQClientIni.ConfiguredKeys.Add("Sound");
            config.EQClientIni.ConfiguredKeys.Add("Sky");
            config.EQClientIni.DisableSound = true;   // old code -> would write Sound=FALSE
            config.EQClientIni.DisableSky = true;     // old code -> would write Sky=0

            EQClientSettingsForm.EnforceOverrides(config);

            var doc = EqClientIniDocument.Load(iniPath);
            void Eq(string section, string key, string expect)
            {
                var got = doc.Get(section, key);
                if (got != expect) errors.Add($"[{section}] {key} expected '{expect}', got '{got ?? "<absent>"}'");
            }
            // Operational keys enforced at launch:
            Eq("Defaults", "WindowedMode", "TRUE");
            Eq("VideoMode", "WindowedMode", "TRUE");
            Eq("Defaults", "Maximized", "0");
            // Bucket-2 keys NOT re-stamped (eqgame-wins) despite being in ConfiguredKeys:
            Eq("Defaults", "Sound", "TRUE");
            Eq("Options", "Sky", "1");
            // Bucket-2 numeric (MaxFPS) was REMOVED from EnforceOverrides in Phase 1 — prove a
            // regression that re-adds it would be caught: config default is 80, fixture is 123,
            // so a re-stamp would flip 123→80. It must stay 123.
            Eq("Options", "MaxFPS", "123");
        }
        catch (Exception ex)
        {
            errors.Add($"CRASH: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }

        if (errors.Count == 0)
        {
            Console.WriteLine("EnforceOverridesSmoke: PASS — Operational keys (WindowedMode/Maximized) enforced at launch; "
                + "Bucket-2 keys NOT re-stamped even with ConfiguredKeys populated (eqgame-wins).");
            return 0;
        }
        Console.Error.WriteLine($"EnforceOverridesSmoke: FAIL — {errors.Count} problem(s):");
        foreach (var e in errors) Console.Error.WriteLine("  - " + e);
        return 1;
    }

    /// <summary>
    /// Phase 2 save round-trip guard: construct EQChatSpamForm against a temp eqclient.ini, assert the
    /// live-read display matches the file, flip ONE filter, Save, then assert ONLY that key was written
    /// (to [Options]), untouched managed filters were NOT rewritten, a filter ABSENT from the file was
    /// NOT inserted (proves touch-gating vs the old write-all), an unmanaged key survived, and no ghost
    /// copy leaked into [Defaults]. CLI: --test-eqclient-chatspam-save.
    /// </summary>
    public static int RunEqChatSpamSaveRoundtrip()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        var errors = new System.Collections.Generic.List<string>();
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eqswitch-p2-chatspam");
        try
        {
            System.IO.Directory.CreateDirectory(tempDir);
            string iniPath = System.IO.Path.Combine(tempDir, "eqclient.ini");
            string[] fixture =
            {
                "[Defaults]",
                "Sound=FALSE",
                "[Options]",
                "PetMisses=1",          // managed filter, will be toggled OFF -> 0
                "Spam=1",               // managed filter, untouched -> preserved
                "ChatSpamUnmanaged=7",  // EQSwitch does not manage this key -> must survive a Save
            };
            System.IO.File.WriteAllLines(iniPath, fixture, System.Text.Encoding.Default);

            var config = new AppConfig { IsFirstRun = false, EQPath = tempDir };
            var form = new EQChatSpamForm(config);   // ctor runs InitializeForm + LoadFromIni

            const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            const System.Reflection.BindingFlags PF = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            var bindings = (System.Collections.IEnumerable)typeof(EQChatSpamForm).GetField("_bindings", BF)!.GetValue(form)!;

            CheckBox? Chk(string key)
            {
                foreach (var b in bindings)
                {
                    var setting = (IniSetting)b.GetType().GetField("Setting", PF)!.GetValue(b)!;
                    if (setting.Key == key) return (CheckBox?)b.GetType().GetField("Check", PF)!.GetValue(b);
                }
                return null;
            }

            var petMisses = Chk("PetMisses");
            var spam = Chk("Spam");
            if (petMisses == null || spam == null)
            {
                errors.Add("could not resolve PetMisses/Spam bindings via reflection");
            }
            else
            {
                // Display-vs-disk: both shipped as 1 in the fixture -> both CHECKED.
                if (!petMisses.Checked) errors.Add("display: 'Pet Misses' should be CHECKED when PetMisses=1");
                if (!spam.Checked) errors.Add("display: 'Spam Filter' should be CHECKED when Spam=1");

                // Change ONE filter, then Save.
                petMisses.Checked = false;     // 'Pet Misses' off -> PetMisses=0
                typeof(EQChatSpamForm).GetMethod("SaveSettings", BF)!.Invoke(form, null);

                var doc = EqClientIniDocument.Load(iniPath);
                void Eq(string section, string key, string? expect)
                {
                    var got = doc.Get(section, key);
                    if (got != expect) errors.Add($"save: [{section}] {key} expected '{expect ?? "<absent>"}', got '{got ?? "<absent>"}'");
                }
                Eq("Options", "PetMisses", "0");          // changed
                Eq("Options", "Spam", "1");               // untouched managed -> preserved
                Eq("Options", "ChatSpamUnmanaged", "7");  // unmanaged -> never clobbered
                Eq("Options", "CriticalSpells", null);    // absent + untouched -> NOT inserted (touch-gated, was write-all)
                Eq("Defaults", "PetMisses", null);        // no ghost copy in another section
            }
            form.Dispose();
        }
        catch (Exception ex)
        {
            errors.Add($"CRASH: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }

        if (errors.Count == 0)
        {
            Console.WriteLine("EqChatSpamSaveRoundtrip: PASS — display matches disk; Save wrote only the changed filter; "
                + "untouched + absent + unmanaged keys preserved (no write-all, no ghost).");
            return 0;
        }
        Console.Error.WriteLine($"EqChatSpamSaveRoundtrip: FAIL — {errors.Count} problem(s):");
        foreach (var e in errors) Console.Error.WriteLine("  - " + e);
        return 1;
    }

    /// <summary>Construct a form in isolation with stub data. Add a case per form as it's converted.</summary>
    private static Form BuildForm(string name, int tab, bool prewarm = false)
    {
        var config = StubConfig();
        return name switch
        {
            "PilotCard" => BuildPilot(),
            "ThemedMessageDialog" => ThemedMessageDialog.Preview(),
            "SettingsForm" => new SettingsForm(config, _ => { }, tab, () => { }, () => { }, null, false, prewarmLazyTabs: prewarm),
            "ProcessManagerForm" => new ProcessManagerForm(
                () => Array.Empty<EQClient>(), () => null, () => { }, _ => { }, config),
            // EQ Client Settings + its 5 sub-forms — all take (AppConfig) and only READ the ini on
            // construction (writes are button-click only), so rendering them with a stub config is safe.
            // Added to verify the FitClientHeightToContent gap fix (button bar sits a uniform gap below
            // the last card at any DPI; previously a hand-guessed ClientSize left dead space or overlap).
            "EQClientSettingsForm" => new EQClientSettingsForm(config),
            "EQModelsForm" => new EQModelsForm(config),
            "EQChatSpamForm" => new EQChatSpamForm(config),
            "EQParticlesForm" => new EQParticlesForm(config),
            "EQVideoModeForm" => new EQVideoModeForm(config),
            "EQKeymapsForm" => new EQKeymapsForm(config),
            _ => throw new ArgumentException($"DiagRender: unknown form '{name}'"),
        };
    }

    /// <summary>
    /// Isolated proof of the CardLayout primitive: exercises every row pattern in an
    /// AutoSize form so the captured PNG shows the WHOLE grown layout (no scroll clip).
    /// If this looks right at --scale 1.5, the primitive is sound → safe to convert the real forms.
    /// </summary>
    private static Form BuildPilot()
    {
        var form = new EqSwitchForm
        {
            Text = "Pilot — CardLayout proof",
            BackColor = DarkTheme.BgDark,
            ForeColor = DarkTheme.FgWhite,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(520, 0),
            Font = DarkTheme.FontUI9,
        };

        var stack = new CardStack(form, scroll: false);

        // Card 1 — label:field rows (the bread-and-butter pattern that clips today).
        var c1 = stack.NewCard("⚔", "EverQuest Setup", DarkTheme.CardGreen);
        c1.Row("EQ Path:", Text(@"C:\Games\EverQuest", 240));
        c1.RowFit("EQ Switch Key:", Text(@"\", 70));
        c1.FlowRow("Exe / Args:", Text("eqgame.exe", 110), Label("Args:"), Text("patchme", 100));
        c1.Row("Right click menu:", Text("Ctrl+Alt+M", 120));
        c1.Hint("Click and press key  |  Delete to clear");

        // Card 2 — two-column section (Tray Click is the real instance of this).
        var c2 = stack.NewCard("🖱", "Tray Click Actions", DarkTheme.CardBlue);
        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, Dock = DockStyle.Top, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddGridRow(grid, BoldLabel("Left Click"), new Control(), BoldLabel("Middle Click"), new Control());
        AddGridRow(grid, Label("Single"), Combo(140, "None", "Settings"), Label("Single"), Combo(140, "TogglePiP"));
        AddGridRow(grid, Label("Double"), Combo(140, "Launch One"), Label("Triple"), Combo(140, "Settings"));
        c2.Full(grid);

        // Card 3 — field + unit + action button on one row, then a button bar.
        var c3 = stack.NewCard("✂", "Log File Trimming", DarkTheme.CardCyan);
        c3.FlowRow("Threshold:", Numeric(0, 500, 50, 60), Label("MB"), Button("✂ Trim Now"));
        c3.Hint("Async trim + archive old logs");
        c3.Check(Check("Run at Startup"), "starts EQSwitch with Windows");
        c3.Buttons(rightAlign: true, Button("Cancel"), Button("Apply"), Primary("Save"));

        return form;
    }

    // ── inline DPI-correct (height-free) control factories for the pilot ──
    private static TextBox Text(string text, int width) => new()
    {
        Text = text, Width = width, Font = DarkTheme.FontUI9, BackColor = DarkTheme.BgInput,
        ForeColor = DarkTheme.FgWhite, BorderStyle = BorderStyle.FixedSingle,
    };
    private static Label Label(string text) => new()
    { Text = text, AutoSize = true, ForeColor = DarkTheme.FgGray, Font = DarkTheme.FontUI9, Margin = new Padding(0, 6, 8, 0) };
    private static Label BoldLabel(string text) => new()
    { Text = text, AutoSize = true, ForeColor = DarkTheme.FgWhite, Font = DarkTheme.FontSemibold9, Margin = new Padding(0, 4, 12, 2) };
    private static ComboBox Combo(int width, params string[] items)
    {
        var cb = new ComboBox { Width = width, Font = DarkTheme.FontUI9, BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        cb.Items.AddRange(items);
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        return cb;
    }
    private static NumericUpDown Numeric(decimal min, decimal max, decimal val, int width) => new()
    { Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max), Width = width, Font = DarkTheme.FontUI9, BackColor = DarkTheme.BgInput, ForeColor = DarkTheme.FgWhite, BorderStyle = BorderStyle.FixedSingle };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = DarkTheme.FgWhite, Font = DarkTheme.FontUI9 };
    private static Button Button(string text)
    {
        var b = DarkTheme.MakeButton(text, DarkTheme.BgMedium, 0, 0);
        b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink; b.Margin = Padding.Empty;
        return b;
    }
    private static Button Primary(string text)
    {
        var b = DarkTheme.MakePrimaryButton(text, 0, 0);
        b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink; b.Margin = Padding.Empty;
        return b;
    }
    private static void AddGridRow(TableLayoutPanel g, params Control[] cells)
    {
        int r = g.RowCount; g.RowCount = r + 1; g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int i = 0; i < cells.Length; i++) g.Controls.Add(cells[i], i, r);
    }

    /// <summary>A representative AppConfig so grids/cards aren't empty (more realistic render).</summary>
    private static AppConfig StubConfig()
    {
        var c = new AppConfig { IsFirstRun = false, EQPath = @"C:\Games\EverQuest" };
        c.Accounts.Add(new Account { Name = "main", Username = "acct-main", Server = "dalaya", Notes = "main acct", LastLoginResult = "ok" });
        c.Accounts.Add(new Account { Name = "alt", Username = "acct-alt", Server = "dalaya", Notes = "box 2", LastLoginResult = "fail" });
        c.Characters.Add(new Character { Name = "Raistlin", AccountUsername = "acct-main", AccountServer = "dalaya", CharacterSlot = 1 });
        c.Characters.Add(new Character { Name = "Natedogg", AccountUsername = "acct-alt", AccountServer = "dalaya", CharacterSlot = 2 });
        // A few teams with VARYING-length cells so the team-summary render exercises the
        // two-column layout (fixed col-2 alignment + divider) with real data, not all "(none)".
        c.Team1Account1 = "char:Raistlin"; c.Team1Account2 = "acct:main";
        c.Team2Account1 = "char:Natedogg";
        c.Team5Account1 = "acct:alt"; c.Team5Account2 = "char:Raistlin";
        c.Team6Account1 = "char:Raistlin";
        return c;
    }

    /// <summary>Simulate system-DPI font growth by multiplying every control's font (DEBUG sim only).</summary>
    private static void ScaleFonts(Control root, float scale)
    {
        foreach (Control c in root.Controls)
        {
            try
            {
                var f = c.Font;
                c.Font = new Font(f.FontFamily, f.Size * scale, f.Style);
            }
            catch { /* font swap is best-effort sim; ignore odd controls */ }
            ScaleFonts(c, scale);
        }
    }

    private static string? GetArg(string[] args, string key)
    {
        int i = Array.IndexOf(args, key);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }

    private static void WriteError(string outDir, string file, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, file), ex.ToString());
        }
        catch { /* nothing more we can do */ }
    }
}
#endif
