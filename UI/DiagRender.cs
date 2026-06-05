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
/// Known forms: "PilotCard" (layout-primitive proof), "SettingsForm", "ProcessManagerForm".
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

        // Mirror Program.Main's display setup exactly so the render matches production.
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(new Font("Segoe UI", 9f));

        Form form;
        try
        {
            form = BuildForm(formName, tab);
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

    /// <summary>Construct a form in isolation with stub data. Add a case per form as it's converted.</summary>
    private static Form BuildForm(string name, int tab)
    {
        var config = StubConfig();
        return name switch
        {
            "PilotCard" => BuildPilot(),
            "SettingsForm" => new SettingsForm(config, _ => { }, tab, () => { }, () => { }, null, false),
            "ProcessManagerForm" => new ProcessManagerForm(
                () => Array.Empty<EQClient>(), () => null, () => { }, _ => { }, config),
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
