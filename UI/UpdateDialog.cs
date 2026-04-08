using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using EQSwitch.Core;

namespace EQSwitch.UI;

/// <summary>
/// Manual update checker — no telemetry, no background requests.
/// User clicks the button, we check GitHub once, download if needed.
/// </summary>
public class UpdateDialog : Form
{
    private static readonly HttpClient _http = CreateHttpClient();

    private readonly Label _lblStatus;
    private readonly Label _lblDetail;
    private readonly Panel _progressOuter;
    private readonly Panel _progressFill;
    private readonly Button _btnAction;
    private readonly Button _btnCancel;
    private CancellationTokenSource? _cts;

    // Release info from GitHub
    private string? _remoteVersion;
    private string? _downloadUrl;
    private string? _hookDownloadUrl;
    private string? _dinput8DownloadUrl;
    private long _downloadSize;

    /// <summary>Set by --test-update flag to simulate the full update flow locally.</summary>
    public static bool TestMode { get; set; }

    // Marquee animation
    private readonly System.Windows.Forms.Timer _marqueeTimer;
    private int _marqueePos;
    private bool _marqueeForward = true;

    public UpdateDialog()
    {
        DarkTheme.StyleForm(this, "EQSwitch — Update", new Size(420, 210));
        MinimizeBox = false;

        _lblStatus = new Label
        {
            Text = "Checking GitHub for new version...",
            Location = new Point(20, 20),
            Size = new Size(370, 24),
            ForeColor = DarkTheme.FgWhite,
            Font = DarkTheme.FontSemibold95,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_lblStatus);

        _lblDetail = new Label
        {
            Text = "",
            Location = new Point(20, 48),
            Size = new Size(370, 20),
            ForeColor = DarkTheme.FgDimGray,
            Font = DarkTheme.FontUI75Italic,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_lblDetail);

        // Custom dark progress bar — two nested panels
        _progressOuter = new Panel
        {
            Location = new Point(30, 80),
            Size = new Size(350, 20),
            BackColor = DarkTheme.BgInput,
            BorderStyle = BorderStyle.None
        };
        _progressFill = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(0, 20),
            BackColor = DarkTheme.AccentGreen
        };
        _progressOuter.Controls.Add(_progressFill);
        Controls.Add(_progressOuter);

        // Buttons
        _btnAction = DarkTheme.MakePrimaryButton("Upgrade Now", 170, 120);
        _btnAction.Size = new Size(110, 32);
        _btnAction.Visible = false;
        _btnAction.Click += OnActionClick;
        Controls.Add(_btnAction);

        _btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 295, 120);
        _btnCancel.Size = new Size(80, 32);
        _btnCancel.Click += (_, _) =>
        {
            _cts?.Cancel();
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(_btnCancel);

        // Marquee animation timer
        _marqueeTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _marqueeTimer.Tick += (_, _) =>
        {
            const int step = 4, barW = 80;
            if (_marqueeForward) _marqueePos += step; else _marqueePos -= step;
            if (_marqueePos + barW >= _progressOuter.Width) _marqueeForward = false;
            if (_marqueePos <= 0) _marqueeForward = true;
            _progressFill.Location = new Point(_marqueePos, 0);
            _progressFill.Size = new Size(barW, 20);
        };

        Shown += async (_, _) => await CheckForUpdateAsync();
    }

    private static HttpClient CreateHttpClient()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EQSwitch", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    // ─── State 1: Check GitHub ──────────────────────────────────

    private async Task CheckForUpdateAsync()
    {
        _cts = new CancellationTokenSource();
        _marqueeTimer.Start();

        try
        {
            if (TestMode)
            {
                // Simulate network delay
                await Task.Delay(800, _cts.Token);

                var exePath = Environment.ProcessPath ?? "";
                _remoteVersion = "99.0.0";
                _downloadUrl = "TEST_LOCAL";
                _downloadSize = File.Exists(exePath) ? new FileInfo(exePath).Length : 0;

                var dir = Path.GetDirectoryName(exePath)!;
                var hookPath = Path.Combine(dir, "eqswitch-hook.dll");
                if (File.Exists(hookPath))
                    _hookDownloadUrl = "TEST_LOCAL_HOOK";

                var dinput8Path = Path.Combine(dir, "dinput8.dll");
                if (File.Exists(dinput8Path))
                    _dinput8DownloadUrl = "TEST_LOCAL_DINPUT8";

                ShowVersionComparison();
                return;
            }

            var response = await _http.GetAsync(
                "https://api.github.com/repos/itsnateai/eqswitch/releases/latest",
                _cts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var vals)
                    ? vals.FirstOrDefault() : null;
                if (remaining == "0")
                    ShowError("GitHub API rate limit reached.", "Try again in a few minutes.");
                else
                    ShowError("GitHub API access denied (403).", "Check your network connection.");
                return;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                ShowError("No releases found on GitHub.", "The repository may not have any published releases.");
                return;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(_cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _remoteVersion = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";

            // Scan assets for exe and hook DLL
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var url = asset.GetProperty("browser_download_url").GetString() ?? "";

                    if (name.Equals("EQSwitch.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        _downloadUrl = url;
                        _downloadSize = asset.TryGetProperty("size", out var sizeEl)
                            ? sizeEl.GetInt64() : 0;
                    }
                    else if (name.Equals("eqswitch-hook.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        _hookDownloadUrl = url;
                    }
                    else if (name.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        _dinput8DownloadUrl = url;
                    }
                }
            }

            if (string.IsNullOrEmpty(_downloadUrl))
            {
                ShowError("No executable found in the latest release.", "The release may be incomplete.");
                return;
            }

            ShowVersionComparison();
        }
        catch (TaskCanceledException)
        {
            // User cancelled or timeout
            if (_cts?.IsCancellationRequested != true)
                ShowError("Request timed out.", "Check your internet connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            ShowError("Could not reach GitHub.", ex.Message);
        }
        catch (JsonException)
        {
            ShowError("Unexpected response from GitHub.", "The API response format may have changed.");
        }
        catch (Exception ex)
        {
            ShowError("Update check failed.", ex.Message);
        }
    }

    // ─── State 2: Compare Versions ──────────────────────────────

    private void ShowVersionComparison()
    {
        _marqueeTimer.Stop();
        _progressFill.Size = new Size(0, 20);
        _progressFill.Location = new Point(0, 0);

        var localVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var isNewer = Version.TryParse(_remoteVersion, out var remote)
                   && Version.TryParse(localVersion, out var local)
                   && remote > local;

        _lblDetail.Text = $"Current: {localVersion}  →  GitHub: {_remoteVersion}";
        _progressOuter.Visible = false;

        if (isNewer)
        {
            _lblStatus.Text = TestMode ? "TEST MODE — Simulated update available!" : "A new version is available!";
            _btnAction.Text = "Upgrade Now";
            _btnAction.Visible = true;
            _btnCancel.Text = "Cancel";
        }
        else
        {
            _lblStatus.Text = "You're on the latest version!";
            _btnAction.Visible = false;
            _btnCancel.Text = "OK";
            _btnCancel.Location = new Point(170, 120);
        }
    }

    // ─── State 3: Download & Apply ──────────────────────────────

    private async void OnActionClick(object? sender, EventArgs e)
    {
        _btnAction.Enabled = false;
        _btnCancel.Text = "Cancel";
        _progressOuter.Visible = true;
        _progressFill.Location = new Point(0, 0);
        _lblStatus.Text = $"Downloading EQSwitch {_remoteVersion}...";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path.");
        var exeDir = Path.GetDirectoryName(exePath)!;
        var newExePath = exePath + ".new";
        var oldExePath = exePath + ".old";
        var hookPath = Path.Combine(exeDir, "eqswitch-hook.dll");
        var newHookPath = hookPath + ".new";
        var oldHookPath = hookPath + ".old";
        var dinput8Path = Path.Combine(exeDir, "dinput8.dll");
        var newDinput8Path = dinput8Path + ".new";
        var oldDinput8Path = dinput8Path + ".old";

        try
        {
            bool success;
            if (TestMode)
            {
                // Copy current exe as the "downloaded" update
                success = await CopyFileWithProgressAsync(exePath, newExePath, "EQSwitch.exe");
                if (!success) return;

                if (!string.IsNullOrEmpty(_hookDownloadUrl))
                {
                    _lblStatus.Text = "Downloading eqswitch-hook.dll...";
                    _progressFill.Size = new Size(0, 20);
                    success = await CopyFileWithProgressAsync(hookPath, newHookPath, "eqswitch-hook.dll");
                    if (!success) return;
                }

                if (!string.IsNullOrEmpty(_dinput8DownloadUrl))
                {
                    _lblStatus.Text = "Downloading dinput8.dll...";
                    _progressFill.Size = new Size(0, 20);
                    success = await CopyFileWithProgressAsync(dinput8Path, newDinput8Path, "dinput8.dll");
                    if (!success) return;
                }
            }
            else
            {
                // Download exe
                success = await DownloadFileAsync(_downloadUrl!, newExePath, "EQSwitch.exe");
                if (!success) return;

                // Download hook DLL if available
                if (!string.IsNullOrEmpty(_hookDownloadUrl))
                {
                    _lblStatus.Text = "Downloading eqswitch-hook.dll...";
                    _progressFill.Size = new Size(0, 20);
                    success = await DownloadFileAsync(_hookDownloadUrl, newHookPath, "eqswitch-hook.dll");
                    if (!success) return;
                }

                // Download dinput8 proxy if available
                if (!string.IsNullOrEmpty(_dinput8DownloadUrl))
                {
                    _lblStatus.Text = "Downloading dinput8.dll...";
                    _progressFill.Size = new Size(0, 20);
                    success = await DownloadFileAsync(_dinput8DownloadUrl, newDinput8Path, "dinput8.dll");
                    if (!success) return;
                }
            }

            // Rename dance
            _lblStatus.Text = "Applying update...";
            _lblDetail.Text = "";
            _progressOuter.Visible = false;

            // Rename running exe → .old, then .new → exe
            TryDelete(oldExePath);
            File.Move(exePath, oldExePath);
            File.Move(newExePath, exePath);

            // Hook DLL — if we downloaded a new one
            if (File.Exists(newHookPath))
            {
                if (File.Exists(hookPath))
                {
                    TryDelete(oldHookPath);
                    File.Move(hookPath, oldHookPath);
                }
                File.Move(newHookPath, hookPath);
            }

            // dinput8 proxy — if we downloaded a new one
            if (File.Exists(newDinput8Path))
            {
                if (File.Exists(dinput8Path))
                {
                    TryDelete(oldDinput8Path);
                    File.Move(dinput8Path, oldDinput8Path);
                }
                File.Move(newDinput8Path, dinput8Path);
            }

            // Relaunch and exit
            FileLogger.Info($"Update applied: {_remoteVersion}. Restarting...");
            Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = "--after-update",
                UseShellExecute = true
            });
            Application.Exit();
        }
        catch (IOException ex)
        {
            // Try to roll back
            TryRollback(exePath, oldExePath, newExePath);
            TryRollback(hookPath, oldHookPath, newHookPath);
            TryRollback(dinput8Path, oldDinput8Path, newDinput8Path);

            if (ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
                ShowError("Cannot replace the executable.", "Your antivirus may be locking the file. Try again.");
            else
                ShowError("Failed to apply update.", ex.Message);
        }
        catch (TaskCanceledException)
        {
            TryDelete(newExePath);
            TryDelete(newHookPath);
            TryDelete(newDinput8Path);
            if (!IsDisposed) ShowVersionComparison();
        }
        catch (Exception ex)
        {
            TryDelete(newExePath);
            TryDelete(newHookPath);
            TryDelete(newDinput8Path);
            if (!IsDisposed) ShowError("Update failed.", ex.Message);
        }
    }

    private async Task<bool> DownloadFileAsync(string url, string destPath, string displayName)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts!.Token);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        await using var contentStream = await response.Content.ReadAsStreamAsync(_cts.Token);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, _cts.Token)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
            downloaded += read;

            if (totalBytes > 0)
            {
                int pct = (int)(downloaded * 100 / totalBytes);
                var dlMB = downloaded / (1024.0 * 1024.0);
                var totalMB = totalBytes / (1024.0 * 1024.0);

                if (!IsDisposed) Invoke(() =>
                {
                    if (IsDisposed) return;
                    _progressFill.Size = new Size(
                        (int)(_progressOuter.Width * downloaded / totalBytes), 20);
                    _lblDetail.Text = totalMB < 1
                        ? $"{pct}% ({downloaded / 1024.0:F0} / {totalBytes / 1024.0:F0} KB)"
                        : $"{pct}% ({dlMB:F0} / {totalMB:F0} MB)";
                });
            }
        }

        // Verify size if Content-Length was provided
        if (totalBytes > 0 && downloaded != totalBytes)
        {
            TryDelete(destPath);
            ShowError($"Download of {displayName} was incomplete.",
                      $"Expected {totalBytes:N0} bytes, got {downloaded:N0}.");
            return false;
        }

        return true;
    }

    /// <summary>Chunked file copy with progress bar animation for test mode.</summary>
    private async Task<bool> CopyFileWithProgressAsync(string sourcePath, string destPath, string displayName)
    {
        await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var totalBytes = sourceStream.Length;
        var buffer = new byte[81920];
        long copied = 0;
        int read;

        while ((read = await sourceStream.ReadAsync(buffer, _cts!.Token)) > 0)
        {
            await destStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
            copied += read;

            // Throttle to simulate download speed (~2 MB/s)
            await Task.Delay(20, _cts.Token);

            if (totalBytes > 0 && !IsDisposed) Invoke(() =>
            {
                if (IsDisposed) return;
                int pct = (int)(copied * 100 / totalBytes);
                _progressFill.Size = new Size(
                    (int)(_progressOuter.Width * copied / totalBytes), 20);
                var dlMB = copied / (1024.0 * 1024.0);
                var totalMB = totalBytes / (1024.0 * 1024.0);
                _lblDetail.Text = totalMB < 1
                    ? $"{pct}% ({copied / 1024.0:F0} / {totalBytes / 1024.0:F0} KB)"
                    : $"{pct}% ({dlMB:F0} / {totalMB:F0} MB)";
            });
        }

        return true;
    }

    // ─── State 4: Error ─────────────────────────────────────────

    private void ShowError(string message, string detail)
    {
        _marqueeTimer.Stop();
        _progressOuter.Visible = false;
        _lblStatus.Text = message;
        _lblStatus.ForeColor = DarkTheme.CardWarn;
        _lblDetail.Text = detail;
        _btnAction.Visible = false;
        _btnCancel.Text = "OK";
        _btnCancel.Location = new Point(170, 120);
    }

    // ─── Helpers ────────────────────────────────────────────────

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryRollback(string original, string oldPath, string newPath)
    {
        try
        {
            if (!File.Exists(original) && File.Exists(oldPath))
                File.Move(oldPath, original);
            TryDelete(newPath);
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _marqueeTimer.Stop();
            _marqueeTimer.Dispose();
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _cts?.Dispose(); } catch (ObjectDisposedException) { }
            DarkTheme.DisposeControlFonts(this);
        }
        base.Dispose(disposing);
    }
}
