// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
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

    // Repo path single source of truth — used by IsAllowedHost AbsolutePath gating
    // and the catalogue fetch URL builder. Replace the two hardcoded literals
    // (formerly at IsAllowedHost lines 623-626) with one const so a fork edits
    // exactly one place.
    private const string AppName = "EQSwitch";
    private const string GitHubRepo = "itsnateai/eqswitch";

    // Hard ceiling on a single download. Self-contained EQSwitch release zip is
    // ~60 MB; 200 MB gives ~3x headroom for future asset growth (extra DLLs,
    // localization, etc.) without giving a compromised CDN edge unbounded
    // write authority. Without this cap, a hostile Content-Length: 50 GB or
    // chunked-transfer with no Content-Length at all could fill the user's
    // disk inside the 30s HttpClient.Timeout window before the SHA256 verify
    // step fires.
    internal const long MaxDownloadBytes = 200L * 1024 * 1024;

    // Cap the in-memory response buffer for ReadAsStringAsync calls (release
    // JSON, SHA256SUMS body). The default ceiling is ~2 GB; tighten to 1 MB
    // so a hostile CDN edge can't blow up the tray with an unbounded text
    // body. Streaming downloads (DownloadFileAsync) use a separate per-write
    // ceiling — see MaxDownloadBytes.
    private const long MaxResponseBufferBytes = 1L * 1024 * 1024;

    private readonly Label _lblStatus;
    private readonly Label _lblDetail;
    private readonly Panel _progressOuter;
    private readonly Panel _progressFill;
    private readonly Button _btnAction;
    private readonly Button _btnCancel;
    private CancellationTokenSource? _cts;

    // Release info from GitHub
    private string? _remoteVersion;
    private string? _downloadUrl;        // chosen zip bundle download URL
    private string? _downloadAssetName;  // exact zip asset filename — threaded into the SHA256SUMS lookup so discovery and integrity agree on one file
    private string? _hashFileUrl;

    // Gate the TestMode setter behind #if DEBUG. The Release build still
    // exposes the getter (defaults to false, always false in Release) so
    // existing call sites that read it compile. The setter — which bypasses
    // ALL security checks (allowlist skipped, SHA verification skipped) —
    // is unreachable in Release so a future code path can't accidentally
    // (or maliciously, given any process with handle access could mutate
    // static fields) flip the tray into test mode in shipped builds.
#if DEBUG
    public static bool TestMode { get; set; }
#else
    public static bool TestMode { get; } = false;
#endif

    // Marquee animation
    private readonly System.Windows.Forms.Timer _marqueeTimer;
    private int _marqueePos;
    private bool _marqueeForward = true;

    public UpdateDialog()
    {
        // Compact dialog: 320x152 with symmetric 20px top/bottom pads. Button
        // block (Upgrade 110 + 15gap + Cancel 80 = 205) starts at x=57 so it
        // centers on the form's x=160 axis — visually aligned under the
        // MiddleCenter-aligned status/detail labels.
        DarkTheme.StyleForm(this, "EQSwitch — Update", new Size(320, 152));
        MinimizeBox = false;

        _lblStatus = new Label
        {
            Text = "Checking GitHub for new version...",
            Location = new Point(20, 20),
            Size = new Size(280, 24),
            ForeColor = DarkTheme.FgWhite,
            Font = DarkTheme.FontSemibold95,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_lblStatus);

        _lblDetail = new Label
        {
            Text = "",
            Location = new Point(20, 48),
            Size = new Size(280, 20),
            ForeColor = DarkTheme.FgDimGray,
            Font = DarkTheme.FontUI75Italic,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_lblDetail);

        // Custom dark progress bar — two nested panels
        _progressOuter = new Panel
        {
            Location = new Point(30, 78),
            Size = new Size(260, 18),
            BackColor = DarkTheme.BgInput,
            BorderStyle = BorderStyle.None
        };
        _progressFill = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(0, 18),
            BackColor = DarkTheme.AccentGreen
        };
        _progressOuter.Controls.Add(_progressFill);
        Controls.Add(_progressOuter);

        // Buttons — block centered on form's x=160 axis (block width 205, x=57..262)
        _btnAction = DarkTheme.MakePrimaryButton("Upgrade Now", 57, 100);
        _btnAction.Size = new Size(110, 32);
        _btnAction.Visible = false;
        _btnAction.Click += OnActionClick;
        Controls.Add(_btnAction);

        _btnCancel = DarkTheme.MakeButton("Cancel", DarkTheme.BgMedium, 182, 100);
        _btnCancel.Size = new Size(80, 32);
        _btnCancel.Click += (_, _) =>
        {
            try { _cts?.Cancel(); }
            catch (ObjectDisposedException) { /* rapid double-click race with Dispose */ }
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
            _progressFill.Size = new Size(barW, 18);
        };

        Shown += async (_, _) => await CheckForUpdateAsync();
    }

    private static HttpClient CreateHttpClient()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        // Disable auto-redirect: the default handler follows redirects WITHOUT
        // re-checking each hop against the allowlist, which would let an
        // allowlisted origin (github.com) hand off to an attacker-controlled
        // host via a crafted 3xx. We follow manually in SendAllowlistedAsync,
        // validating each hop. v3.22.29 hardening.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        // Per-call response-buffer ceiling. The default is ~2 GB and applies to
        // ReadAsStringAsync — without this cap a hostile SHA256SUMS edge could
        // stream until OOM. Streaming downloads (DownloadFileAsync) bypass
        // this and use MaxDownloadBytes instead.
        client.MaxResponseContentBufferSize = MaxResponseBufferBytes;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppName, version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    // Host-based allowlist for self-update URLs. Replaces a 3-prefix
    // `StartsWith` check (pre-v3.22.28) with Uri parsing + host-equality.
    // Repo scope on github.com / api.github.com is checked against
    // AbsolutePath, not a raw-URL substring. CDN coverage:
    // both `objects.githubusercontent.com` (legacy edge) and
    // `release-assets.githubusercontent.com` (new edge, rolled alongside)
    // are accepted as redirect targets for release-asset downloads.
    //
    // `allowApi:false` is used at release-asset / hash-file fetch sites
    // (api.github.com is not a valid release-asset host). `allowApi:true`
    // is used at the catalogue fetch (releases/latest).
    internal static bool IsAllowedHost(Uri uri, bool allowApi)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        string host = uri.Host;
        if (host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return true;
        if (host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return true;
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith($"/{GitHubRepo}/", StringComparison.OrdinalIgnoreCase);
        if (allowApi && host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith($"/repos/{GitHubRepo}/", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    internal static bool IsAllowlisted(string? url, bool allowApi = true) =>
        !string.IsNullOrEmpty(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        IsAllowedHost(uri, allowApi);

    /// <summary>
    /// Extract the hex hash for a named zip asset from a SHA256SUMS body.
    /// Handles GNU-coreutils format (`hexhash  filename` or `hexhash *filename`),
    /// BSD-tag format (`SHA256 (filename) = hexhash`), CRLF line endings,
    /// multi-entry files, and tab separators.
    ///
    /// <paramref name="expectedName"/> is the EXACT zip filename chosen by asset
    /// discovery (e.g. "EQSwitch.zip" or "EQSwitch-3.24.18.zip"), threaded in
    /// rather than rebuilt from the version here — so discovery and integrity can
    /// never disagree about which file is being verified. The match stays exact
    /// (v3.22.30 anti-shadowing: a multi-entry SHA256SUMS — e.g. one line per
    /// asset during the dual-asset transition — can't let a sibling line shadow
    /// the hash of the file we actually downloaded). Returns null on null/empty
    /// <paramref name="expectedName"/> — fail-closed.
    ///
    /// Returns null if no entry for the expected zip is found OR if the parsed
    /// hash isn't a 64-char hex string (defends against malformed SHA256SUMS
    /// bodies — the parser is the actual trust-decision input for self-update).
    /// </summary>
    internal static string? ParseHashForZipBundle(string? content, string? expectedName)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(expectedName)) return null;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            string? candidate = null;

            // BSD-tag format: SHA256 (filename) = hexhash
            if (line.StartsWith("SHA256 (", StringComparison.OrdinalIgnoreCase))
            {
                int lparen = line.IndexOf('(');
                int rparen = line.IndexOf(')');
                int eq = line.IndexOf('=');
                if (lparen >= 0 && rparen > lparen && eq > rparen)
                {
                    var name = line.Substring(lparen + 1, rparen - lparen - 1).Trim();
                    if (name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                        candidate = line.Substring(eq + 1).Trim();
                }
            }
            else
            {
                // GNU-coreutils format: "hexhash  filename" or "hexhash *filename"
                // (binary-mode emit). Tab separator also seen in the wild.
                var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    var fname = parts[1].Trim().TrimStart('*');
                    if (fname.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                        candidate = parts[0].Trim();
                }
            }

            if (candidate != null && IsValidSha256Hex(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Canonical, version-independent release zip name (e.g. EQSwitch.zip).
    /// Preferred asset as of the v3.24.18 versionless-rename transition: a stable
    /// releases/latest/download URL and a consistent extract folder for manual
    /// users. Emitted alongside the legacy versioned name during the dual-asset
    /// rollout, so older clients keep self-updating off the versioned asset.
    /// </summary>
    internal static bool IsCanonicalZipName(string filename) =>
        !string.IsNullOrEmpty(filename) &&
        filename.Equals($"{AppName}.zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Legacy versioned release zip name (e.g. EQSwitch-3.24.18.zip). Matched by
    /// prefix/suffix, not a version parse — the exact-version integrity pinning
    /// lives in ParseHashForZipBundle, keyed off the actual chosen asset name.
    /// </summary>
    internal static bool IsVersionedZipName(string filename) =>
        !string.IsNullOrEmpty(filename) &&
        filename.StartsWith($"{AppName}-", StringComparison.OrdinalIgnoreCase) &&
        filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    // SHA-256 produces 32 bytes = 64 hex chars. Reject anything else so a
    // malformed SHA256SUMS body (truncated, non-hex, empty after `=`) can't
    // reach the equality compare and silently fall into the "no entry" branch
    // by accident.
    internal static bool IsValidSha256Hex(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 64) return false;
        foreach (var c in s)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }

    /// <summary>
    /// True when an asserted download size is within the per-file ceiling.
    /// Negative or zero is ALLOWED here (caller handles "no Content-Length"
    /// separately by streaming + counting); the ceiling test is a "is this
    /// number too big" gate, not a "is this number sane" gate.
    /// </summary>
    internal static bool IsAllowedDownloadSize(long bytes) =>
        bytes <= MaxDownloadBytes;

    /// <summary>
    /// Issue a GET and follow up to 5 redirects manually. Every hop's URL —
    /// including the initial one — is validated against IsAllowedHost before
    /// the request is sent. Throws if any hop lands off-list or if the redirect
    /// chain exceeds the hop limit.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAllowlistedAsync(
        string url, HttpCompletionOption completion, bool allowApi, CancellationToken ct)
    {
        const int maxHops = 5;
        for (int hop = 0; hop < maxHops; hop++)
        {
            // Belt-and-suspenders: IsAllowedHost already requires Uri.Scheme == https,
            // but a future allowlist edit accidentally accepting http would silently
            // disable transport encryption. Independently enforce HTTPS here so
            // scheme-downgrade can never happen regardless of allowlist contents.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var validatedUri) ||
                !validatedUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException($"URL must be HTTPS: {url}");

            if (!IsAllowedHost(validatedUri, allowApi))
                throw new HttpRequestException($"URL not in allowlist: {url}");

            var response = await _http.GetAsync(url, completion, ct);

            int status = (int)response.StatusCode;
            if (status >= 300 && status < 400 && response.Headers.Location != null)
            {
                var next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location.ToString()
                    : new Uri(new Uri(url), response.Headers.Location).ToString();
                response.Dispose();
                url = next;
                continue;
            }

            return response;
        }
        throw new HttpRequestException($"Too many redirects (>{maxHops}) starting from initial URL.");
    }

    // ─── State 1: Check GitHub ──────────────────────────────────

    private async Task CheckForUpdateAsync()
    {
        if (IsWingetManaged())
        {
            FileLogger.Info("Update: winget-managed install detected, skipping self-update");
            _marqueeTimer.Stop();
            _progressOuter.Visible = false;
            _lblStatus.Text = "This installation is managed by winget.";
            _lblDetail.Text = "Use:  winget upgrade itsnateai.EQSwitch";
            _btnAction.Visible = false;
            _btnCancel.Text = "OK";
            _btnCancel.Location = new Point(120, 100);
            return;
        }

        _cts = new CancellationTokenSource();
        _marqueeTimer.Start();

        try
        {
            if (TestMode)
            {
                // Simulate network delay
                await Task.Delay(800, _cts.Token);

                _remoteVersion = "99.0.0";
                _downloadUrl = "TEST_LOCAL";

                ShowVersionComparison();
                return;
            }

            // Catalogue fetch — explicit allowlist gate (allowApi:true is the
            // only site that accepts api.github.com). v3.22.29 routes through
            // SendAllowlistedAsync so a hostile 3xx from api.github.com gets
            // validated at every hop. Item 11 closed alongside item 1.
            var catalogueUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            var response = await SendAllowlistedAsync(
                catalogueUrl,
                HttpCompletionOption.ResponseContentRead,
                allowApi: true,
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

            // Scan assets for the zip bundle. During the dual-asset transition a
            // release carries both the canonical EQSwitch.zip and the legacy
            // EQSwitch-X.Y.Z.zip. Prefer the canonical name (deterministically —
            // NOT last-match-wins, so GitHub's asset ordering can't pick the wrong
            // one), but fall back to the versioned name so a pre-transition release
            // (versioned-only) and an eventual Phase-2 release (canonical-only)
            // both resolve. The chosen name is captured so the SHA256SUMS lookup
            // verifies the exact file we downloaded.
            if (root.TryGetProperty("assets", out var assets))
            {
                string? canonicalUrl = null, canonicalName = null;
                string? versionedUrl = null, versionedName = null;

                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var url = asset.GetProperty("browser_download_url").GetString() ?? "";

                    if (url.Length > 0 && IsCanonicalZipName(name))
                    {
                        canonicalUrl = url;
                        canonicalName = name;
                    }
                    else if (url.Length > 0 && IsVersionedZipName(name))
                    {
                        versionedUrl = url;
                        versionedName = name;
                    }

                    if (name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        _hashFileUrl = url;
                    }
                }

                _downloadUrl = canonicalUrl ?? versionedUrl;
                _downloadAssetName = canonicalName ?? versionedName;
            }

            if (string.IsNullOrEmpty(_downloadUrl))
            {
                ShowError("No update package found in the latest release.", "The release may be incomplete.");
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
        _progressFill.Size = new Size(0, 18);
        _progressFill.Location = new Point(0, 0);

        var localVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        // v3.22.29 verifier-found gap (Opus T3 #2): if _remoteVersion is empty
        // or unparseable (malformed tag_name in the GitHub release JSON),
        // Version.TryParse returns false silently and the UI shows "You're on
        // the latest version!" — a false negative that hides API-level bugs.
        // Log a Warn so the silent failure surfaces in logs even though we
        // intentionally don't break the UI flow (a no-update outcome is the
        // safer default than a crash dialog).
        bool remoteParsed = Version.TryParse(_remoteVersion, out var remote);
        bool localParsed = Version.TryParse(localVersion, out var local);
        if (!remoteParsed && !string.IsNullOrEmpty(_remoteVersion))
        {
            FileLogger.Warn($"Update: GitHub returned unparseable remote version '{_remoteVersion}' — falling back to 'up to date' display.");
        }
        else if (string.IsNullOrEmpty(_remoteVersion))
        {
            FileLogger.Warn("Update: GitHub returned empty tag_name — falling back to 'up to date' display.");
        }
        var isNewer = remoteParsed && localParsed && remote > local;

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
            _btnCancel.Location = new Point(120, 100);
        }
    }

    // ─── State 3: Download & Apply ──────────────────────────────

    private async void OnActionClick(object? sender, EventArgs e)
    {
        // v3.22.29 Item 8: widen the async-void exception fence to cover the
        // pre-try setup. Previously state setup (button toggle, cts dispose,
        // ProcessPath null-check, files array construction) ran outside the
        // outer try — an exception there propagated to the SynchronizationContext
        // as unhandled. Now everything sits inside the fence.
        string? exePath = null;
        string? zipPath = null;
        (string name, string localPath)[] files = Array.Empty<(string, string)>();

        try
        {
            // Pre-flight: EQSwitch's hook DLLs (eqswitch-hook.dll / eqswitch-di8.dll) are
            // injected into every running eqgame.exe, so they're module-locked against Phase A's
            // File.Move while EQ is open. The swap is intentionally all-or-nothing (one locked
            // DLL rolls the whole update back — see Phase A/B), so updating mid-session can never
            // succeed. Catch it here, BEFORE spending a ~60 MB download on a guaranteed rollback,
            // and name the real cause. "eqgame" is the default EQ process name; a user who
            // renamed it skips this proactive check but still hits the clearer IOException below.
            if (!TestMode)
            {
                var eqClients = Process.GetProcessesByName("eqgame");
                try
                {
                    if (eqClients.Length > 0)
                    {
                        // Log like every other update-path decision (winget skip, hash reject)
                        // so a user reporting "update won't run" leaves a trace, not just a dialog.
                        FileLogger.Info($"Update: blocked — {eqClients.Length} eqgame.exe client(s) running; hook DLLs are locked. Asked user to close EQ first.");
                        ShowError("Close EverQuest before updating.",
                            $"{eqClients.Length} EQ client{(eqClients.Length == 1 ? "" : "s")} running — the hook DLLs load into them and can't be replaced while they run.");
                        return;
                    }
                }
                finally
                {
                    foreach (var p in eqClients) p.Dispose();
                }
            }

            _btnAction.Enabled = false;
            _btnCancel.Text = "Cancel";
            _progressOuter.Visible = true;
            _progressFill.Location = new Point(0, 0);
            _lblStatus.Text = $"Downloading EQSwitch {_remoteVersion}...";

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine executable path.");
            var exeDir = Path.GetDirectoryName(exePath)!;
            zipPath = Path.Combine(exeDir, "update.zip");

            // Files to update: (name in zip, local path)
            files = new[]
            {
                ("EQSwitch.exe", exePath),
                ("eqswitch-hook.dll", Path.Combine(exeDir, "eqswitch-hook.dll")),
                ("eqswitch-di8.dll", Path.Combine(exeDir, "eqswitch-di8.dll"))
            };

            // Download the zip bundle
            bool success;
            if (TestMode)
            {
                // Build a test zip from current files
                TryDelete(zipPath);
                using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    foreach (var (name, path) in files)
                        if (File.Exists(path))
                            archive.CreateEntryFromFile(path, name);
                }
                // Show fake progress
                success = await CopyFileWithProgressAsync(zipPath, zipPath + ".tmp", "update package");
                TryDelete(zipPath + ".tmp");
                if (!success) return;
            }
            else
            {
                success = await DownloadFileAsync(_downloadUrl!, zipPath, "update package");
                if (!success) return;
            }

            // Verify SHA256 hash. SHA256SUMS is required on every release as of v3.14.3
            // (release.yml emits it). If a release accidentally omits it, fail closed —
            // installing an unverified payload is the exact attack the manifest prevents.
            if (!TestMode && string.IsNullOrEmpty(_hashFileUrl))
            {
                TryDelete(zipPath);
                ShowError("Hash verification failed.",
                    "Release is missing the SHA256SUMS integrity manifest. Update aborted for safety. Download manually from GitHub.");
                return;
            }
            if (!TestMode)
            {
                _lblStatus.Text = "Verifying integrity...";
                // Security: gate the SHA256SUMS fetch through the same host-equality
                // allowlist as the zip download. v3.22.27 and earlier validated only
                // the download URL, not the hash URL — `_hashFileUrl` came straight
                // from the GitHub-API release JSON without any client-side check, so
                // a hostile API response could have pointed it anywhere. v3.22.28
                // closed the static URL gap; v3.22.29 routes through SendAllowlistedAsync
                // so every redirect hop is re-validated.
                if (!Uri.TryCreate(_hashFileUrl, UriKind.Absolute, out var hashUri) ||
                    !IsAllowedHost(hashUri, allowApi: false))
                {
                    FileLogger.Warn($"Update: rejected SHA256SUMS URL from unexpected origin: {_hashFileUrl}");
                    TryDelete(zipPath);
                    ShowError("Hash verification failed.",
                        "SHA256SUMS URL is not from the expected source. Update aborted for safety.");
                    return;
                }
                try
                {
                    using var hashResponse = await SendAllowlistedAsync(
                        _hashFileUrl!,
                        HttpCompletionOption.ResponseContentRead,
                        allowApi: false,
                        _cts!.Token);
                    hashResponse.EnsureSuccessStatusCode();
                    var hashContent = await hashResponse.Content.ReadAsStringAsync(_cts.Token);
                    // Pin the integrity check to the EXACT asset discovery chose
                    // (v3.22.30 anti-shadowing) — threaded by name so the dual-asset
                    // SHA256SUMS can't let a sibling line shadow the file we fetched.
                    string? expectedHash = ParseHashForZipBundle(hashContent, _downloadAssetName);

                    if (!string.IsNullOrEmpty(expectedHash))
                    {
                        var actualHash = ComputeFileHash(zipPath);
                        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            TryDelete(zipPath);
                            ShowError("Hash verification failed.",
                                "The downloaded file doesn't match the expected SHA256 checksum.");
                            return;
                        }
                    }
                    else
                    {
                        TryDelete(zipPath);
                        ShowError("Hash verification failed.",
                            "SHA256SUMS file found but contains no entry for EQSwitch zip bundle.");
                        return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Fail closed: any verification error (network failure, malformed
                    // SHA256SUMS, hash compute IO error) aborts the update. Continuing
                    // here would let an MITM that drops the SHA256SUMS request bypass
                    // integrity check entirely.
                    FileLogger.Warn($"SHA256 verification aborted: {ex.GetType().Name}: {ex.Message}");
                    TryDelete(zipPath);
                    ShowError("Hash verification failed.",
                        "Couldn't verify the downloaded file's SHA256 checksum. Update aborted for safety. Try again or download manually from GitHub.");
                    return;
                }
            }

            // Extract .new files from the zip
            _lblStatus.Text = "Extracting update...";
            _lblDetail.Text = "";
            _progressOuter.Visible = false;

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var (name, localPath) in files)
                {
                    var entry = archive.GetEntry(name);
                    if (entry == null) continue;
                    var newPath = localPath + ".new";
                    TryDelete(newPath);
                    entry.ExtractToFile(newPath);
                }
            }
            TryDelete(zipPath);

            // v3.22.30 Item 1: atomic two-phase swap across all updateables.
            // Pre-v3.22.30 interleaved per-file (move localPath→.old then
            // .new→localPath in the same loop iteration). An IOException on
            // file 2 (e.g., AV scan transient lock, brief antivirus quarantine)
            // AFTER file 1's swap fully landed left file 1 with new content,
            // file 2 with old content, file 3 untouched — a mismatched-version
            // state. The `.ok` sentinel + torn-state recovery only fires when
            // the exe is gone (Program.CleanupUpdateArtifacts); it cannot
            // detect a successful-but-mismatched swap.
            //
            // Phase A — stage: original → .old for every file with a pending
            // .new. If ANY Phase A move fails, unwind the successful stages
            // and abort. No .new content has been committed yet, so old state
            // is fully recoverable.
            //
            // Phase B — commit: .new → original for every staged file. If ANY
            // Phase B move fails (rare — at this point both source and dest
            // dirs are the same, dest doesn't exist yet, so File.Move is just
            // a rename), unwind the successful Phase B commits (localPath →
            // .new) AND unwind Phase A (oldPath → localPath) so all files
            // return to their pre-update state. Modulo IO failures during
            // rollback itself, which surface via FileLogger.Warn rather than
            // crashing the dialog.
            //
            // Also: v3.22.29 .ok pre-delete behavior is preserved — clearing
            // happens AFTER successful Phase B so a mid-swap abort leaves the
            // old .ok sentinels intact (matches the pre-update binary, which
            // is still on disk under .old).
            _lblStatus.Text = "Applying update...";

            // Phase A — stage
            var stagedMoves = new List<(string localPath, string oldPath)>();
            try
            {
                foreach (var (_, localPath) in files)
                {
                    var newPath = localPath + ".new";
                    var oldPath = localPath + ".old";
                    if (!File.Exists(newPath)) continue;
                    if (!File.Exists(localPath)) continue;  // first-time-install case
                    TryDelete(oldPath);
                    File.Move(localPath, oldPath);
                    stagedMoves.Add((localPath, oldPath));
                }
            }
            catch (Exception phaseAEx)
            {
                FileLogger.Warn($"Phase A stage failed after {stagedMoves.Count} successful move(s): {phaseAEx.GetType().Name}: {phaseAEx.Message}");
                foreach (var (localPath, oldPath) in stagedMoves)
                {
                    try
                    {
                        if (File.Exists(oldPath) && !File.Exists(localPath))
                            File.Move(oldPath, localPath);
                    }
                    catch (Exception unwindEx)
                    {
                        FileLogger.Warn($"Phase A unwind {localPath}: {unwindEx.GetType().Name}: {unwindEx.Message}");
                    }
                }
                throw;
            }

            // Phase B — commit
            var committedMoves = new List<(string localPath, string newPath)>();
            try
            {
                foreach (var (_, localPath) in files)
                {
                    var newPath = localPath + ".new";
                    if (!File.Exists(newPath)) continue;
                    File.Move(newPath, localPath);
                    committedMoves.Add((localPath, newPath));
                }
            }
            catch (Exception phaseBEx)
            {
                FileLogger.Warn($"Phase B commit failed after {committedMoves.Count} successful move(s): {phaseBEx.GetType().Name}: {phaseBEx.Message}");
                // Roll back Phase B commits first (newest changes), then Phase A
                // stages (oldest changes). Order matters: if both directions of
                // rollback succeed for the same file, the .new lands back first,
                // then .old restores localPath to old content.
                foreach (var (localPath, newPath) in committedMoves)
                {
                    try
                    {
                        if (File.Exists(localPath) && !File.Exists(newPath))
                            File.Move(localPath, newPath);
                    }
                    catch (Exception uncommitEx)
                    {
                        FileLogger.Warn($"Phase B uncommit {localPath}: {uncommitEx.GetType().Name}: {uncommitEx.Message}");
                    }
                }
                foreach (var (localPath, oldPath) in stagedMoves)
                {
                    try
                    {
                        if (File.Exists(oldPath) && !File.Exists(localPath))
                            File.Move(oldPath, localPath);
                    }
                    catch (Exception unwindEx)
                    {
                        FileLogger.Warn($"Phase B unwind {localPath}: {unwindEx.GetType().Name}: {unwindEx.Message}");
                    }
                }
                throw;
            }

            // Clear .ok sentinels for all rewritten files — new binaries must
            // prove themselves via WriteStartupSentinel after Application.Run.
            // v3.22.29 introduced pre-swap clearing; v3.22.30 keeps it but
            // moves it post-Phase-B so a mid-swap abort leaves the old .ok
            // sentinels in place (matches the rolled-back-on-disk old binary).
            foreach (var (_, localPath) in files)
                TryDelete(localPath + ".ok");

            // Relaunch and exit. v3.22.29 Item 10: dispose the Process handle.
            // Previously leaked — the static Process.Start return value was
            // discarded with no using. Application.Exit takes a few ms anyway
            // so the using's Dispose isn't disruptive.
            FileLogger.Info($"Update applied: {_remoteVersion}. Restarting...");
            using var _ = Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = "--after-update",
                UseShellExecute = true
            });
            Application.Exit();
        }
        catch (IOException ex)
        {
            // Try to roll back
            if (files.Length > 0)
            {
                foreach (var (_, localPath) in files)
                    TryRollback(localPath, localPath + ".old", localPath + ".new");
            }
            if (zipPath != null) TryDelete(zipPath);

            if (ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
                ShowError("Cannot replace a locked file.",
                    "Close all EverQuest clients (EQSwitch's hook DLLs load into them), then retry. Antivirus can also transiently lock files.");
            else
                ShowError("Failed to apply update.", ex.Message);
        }
        catch (TaskCanceledException)
        {
            if (files.Length > 0)
            {
                foreach (var (_, localPath) in files)
                    TryDelete(localPath + ".new");
            }
            if (zipPath != null) TryDelete(zipPath);
            if (!IsDisposed) ShowVersionComparison();
        }
        catch (Exception ex)
        {
            if (files.Length > 0)
            {
                foreach (var (_, localPath) in files)
                    TryDelete(localPath + ".new");
            }
            if (zipPath != null) TryDelete(zipPath);
            if (!IsDisposed) ShowError("Update failed.", ex.Message);
        }
    }

    private async Task<bool> DownloadFileAsync(string url, string destPath, string displayName)
    {
        // Security: only allow downloads from expected GitHub origins. Host-equality
        // canonical pattern — see IsAllowedHost. v3.22.29 routes through
        // SendAllowlistedAsync so every redirect hop is re-validated, not just
        // the initial github.com URL (the pre-v3.22.29 _http was AllowAutoRedirect=true
        // by default; only the pre-redirect URL hit the gate).
        if (!Uri.TryCreate(url, UriKind.Absolute, out var dlUri) ||
            !IsAllowedHost(dlUri, allowApi: false))
        {
            FileLogger.Warn($"Update: rejected download URL from unexpected origin: {url}");
            ShowError("Update failed: download URL is not from the expected source.", url);
            return false;
        }

        using var response = await SendAllowlistedAsync(
            url, HttpCompletionOption.ResponseHeadersRead, allowApi: false, _cts!.Token);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;

        // v3.22.29 Item 2: header-side gate. If the server tells us up-front that
        // the body exceeds the ceiling, fail-closed before opening the destination
        // file. Defends against hostile Content-Length: 50_000_000_000 — fills disk
        // inside HttpClient.Timeout window before SHA256 verify fires.
        if (!IsAllowedDownloadSize(totalBytes))
        {
            ShowError("Download too large.",
                      $"Server reports {totalBytes:N0} bytes; max is {MaxDownloadBytes:N0}.");
            return false;
        }

        await using var contentStream = await response.Content.ReadAsStreamAsync(_cts.Token);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, _cts.Token)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
            downloaded += read;

            // v3.22.29 Item 2: mid-stream gate. Defends against chunked-transfer
            // bodies (no Content-Length up front) that stream forever within the
            // HttpClient.Timeout window. Header check above missed this case
            // because totalBytes was 0.
            if (downloaded > MaxDownloadBytes)
            {
                TryDelete(destPath);
                ShowError("Download exceeded size limit.",
                          $"Got {downloaded:N0} bytes; max is {MaxDownloadBytes:N0}.");
                return false;
            }

            if (totalBytes > 0)
            {
                int pct = (int)(downloaded * 100 / totalBytes);
                var dlMB = downloaded / (1024.0 * 1024.0);
                var totalMB = totalBytes / (1024.0 * 1024.0);

                // v3.22.29 Item 13: BeginInvoke (fire-and-forget) instead of
                // Invoke (blocking). At 81920-byte chunks per ~MB/s, a 50 MB zip
                // is ~640 synchronous UI marshals on the worker thread — that's
                // ~640 cross-thread round-trips when fire-and-forget would do.
                if (!IsDisposed) BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    _progressFill.Size = new Size(
                        (int)(_progressOuter.Width * downloaded / totalBytes), 18);
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

        // Minimum size sanity check — reject truncated/empty downloads
        if (downloaded < 100_000)
        {
            TryDelete(destPath);
            ShowError($"Downloaded {displayName} is too small.",
                      $"Got {downloaded:N0} bytes — expected a valid zip bundle.");
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

            if (totalBytes > 0 && !IsDisposed) BeginInvoke(() =>
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
        _btnCancel.Location = new Point(120, 100);
    }

    // ─── Helpers ────────────────────────────────────────────────

    private static bool IsWingetManaged() =>
        (Environment.ProcessPath ?? "").Contains(@"Microsoft\WinGet\Packages", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Delete a file if present, logging any failure. Stranded `.new`/`.old`
    /// artifacts on locked files used to be invisible to support because the
    /// catch was bare. v3.22.29 Item 5: log to FileLogger.Warn.
    /// </summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { FileLogger.Warn($"TryDelete({path}): {ex.GetType().Name}: {ex.Message}"); }
    }

    private static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Rollback after a partial swap: if `original` is gone and `oldPath`
    /// exists, restore it; then drop the stray `.new`. v3.22.29 Item 5:
    /// log torn-state restore failures so support can spot stranded `.old`
    /// files on next run (instead of the bare-catch silent-swallow from
    /// pre-v3.22.29).
    /// </summary>
    private static void TryRollback(string original, string oldPath, string newPath)
    {
        try
        {
            if (!File.Exists(original) && File.Exists(oldPath))
                File.Move(oldPath, original);
            TryDelete(newPath);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"TryRollback({original}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ─── Sentinel + Cleanup (v3.22.29 Items 6 + 7) ──────────────

    /// <summary>
    /// Write a .ok sentinel next to each updateable file once the new version
    /// has successfully reached its running state. CleanupUpdateArtifacts uses
    /// this to decide whether it's safe to remove .old — if the new exe
    /// crashes before the sentinel is written, .old persists and the user
    /// (or torn-state recovery) can restore it.
    ///
    /// Called from Program.cs on a delay after Application.Run starts the
    /// message pump, so the sentinel only appears once the new binary has
    /// proven it can spin up far enough to schedule a Timer Tick.
    /// </summary>
    internal static void WriteStartupSentinel()
    {
        try
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var fname in new[] { "EQSwitch.exe", "eqswitch-hook.dll", "eqswitch-di8.dll" })
            {
                var path = Path.Combine(dir, fname);
                if (!File.Exists(path)) continue;
                var sentinel = path + ".ok";
                if (!File.Exists(sentinel))
                {
                    try { File.WriteAllText(sentinel, DateTime.UtcNow.ToString("O")); }
                    catch (Exception ex) { FileLogger.Warn($"WriteStartupSentinel({fname}): {ex.Message}"); }
                }
            }
            FileLogger.Info("Update startup sentinel(s) written — new binary verified running.");
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"WriteStartupSentinel: {ex.Message}");
        }
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
