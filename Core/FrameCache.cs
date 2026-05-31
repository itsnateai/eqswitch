// SPDX-License-Identifier: GPL-2.0-or-later
// © itsnateai

using System.Text.Json;

namespace EQSwitch.Core;

/// <summary>
/// v3.22.88 — machine-local persistence for eqgame's MEASURED Windowed non-client
/// frame, keyed by system DPI. This is the "first paint flush" enabler.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS — the WinEQ2/MQ2 comparison. WinEQ2 lands its slim window flush
/// on first paint because it subclasses eqgame's WndProc in-process and re-measures
/// the frame SYNCHRONOUSLY (via <c>GetWindowInfo</c>) on every reposition — so the
/// frame is always available the instant it's needed and nothing is persisted (its
/// INI stores only a position preset). EQSwitch can't do that for <b>autologin</b>
/// clients: the in-process subclass (<c>GeoWndProc</c>) never installs for them, so
/// our only enforcer is the C# guard tick that fires ~½s–few-s AFTER the client is
/// responsive — which is the visible "snap" (caption peek 31 → 13 during zone-in).
/// We can't measure at first paint, so the only way to land flush at first paint is
/// to have measured on a PRIOR run and persisted it. This cache is that persistence
/// — the substitute for WinEQ2's free synchronous re-measure.
/// </para>
/// <para>
/// PURELY ADDITIVE / CANNOT REGRESS. The live read-back
/// (<see cref="WindowManager.TryComputeReadbackCorrection"/>) stays the load-bearing
/// source of truth. The cache only changes the INITIAL frame estimate
/// <see cref="WindowManager.ComputeSlimTitlebarOuterRect(WinRect,int)"/> uses to
/// build the first-paint SHM rect: the cached MEASURED frame (≈3/26/3/3) instead of
/// <c>AdjustWindowRectEx</c>'s PREDICTION (8/31/8/8). A miss / wrong-DPI / insane /
/// corrupt cache falls back to the prediction → today's behavior (snap-once-then-
/// flush). So the cache can only IMPROVE (flush from first paint) or be NEUTRAL.
/// </para>
/// <para>
/// Stored as <c>eqswitch-frame-cache.json</c> next to the exe (same dir as the
/// config, via <see cref="AppDomain.BaseDirectory"/>). Machine-local and DPI-keyed:
/// even if the dir is synced across machines, a different-DPI machine simply gets a
/// cache miss and falls back to prediction → no cross-machine pollution. Delete the
/// file (while EQSwitch is stopped) to force a clean re-measure.
/// </para>
/// </remarks>
public sealed class FrameCache
{
    /// <summary>A measured non-client frame: pixels of frame on each edge.</summary>
    public readonly record struct Frame(int Left, int Top, int Right, int Bottom);

    private readonly object _lock = new();
    private readonly Dictionary<int, Frame> _byDpi = new();
    private readonly string? _path;   // null = in-memory only (unit tests)

    /// <summary>Cache file path next to the exe, mirroring ConfigManager's location.</summary>
    public static string DefaultPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eqswitch-frame-cache.json");

    /// <summary>Production cache backed by <see cref="DefaultPath"/>.</summary>
    public static FrameCache Default => new(DefaultPath);

    /// <param name="path">Backing file, or null for an in-memory-only cache (tests).</param>
    public FrameCache(string? path)
    {
        _path = path;
        Load();
    }

    /// <summary>
    /// True iff a SANE measured frame is cached for <paramref name="dpi"/>. A
    /// stored-but-insane entry (corruption, format drift) returns false so the
    /// caller falls back to the prediction path rather than flinging the window.
    /// </summary>
    public bool TryGet(int dpi, out Frame frame)
    {
        lock (_lock)
        {
            if (_byDpi.TryGetValue(dpi, out frame) && Sane(frame)) return true;
        }
        frame = default;
        return false;
    }

    /// <summary>
    /// Persist a measured frame for <paramref name="dpi"/>. Write-on-change only:
    /// an insane frame is ignored, and an unchanged frame is a no-op (no disk write)
    /// — so the ~500 ms guard tick that re-measures the same constant frame never
    /// touches disk after the first write. Best-effort: a disk failure is logged,
    /// never thrown (a cache-write failure must not crash the guard tick).
    /// </summary>
    public void Set(int dpi, Frame frame)
    {
        if (!Sane(frame)) return;   // never persist garbage
        lock (_lock)
        {
            if (_byDpi.TryGetValue(dpi, out var cur) && cur == frame) return;  // write-on-change: unchanged → no-op
            _byDpi[dpi] = frame;
        }
        // Reached only when the dict actually changed (the unchanged case returned inside
        // the lock above). Persist OUTSIDE the lock so disk I/O never stalls a concurrent
        // reader (TryGet can run on a threadpool thread via the autologin inject path).
        Save();
    }

    /// <summary>In-memory entry count (test/diagnostic aid).</summary>
    public int Count { get { lock (_lock) return _byDpi.Count; } }

    // A frame is sane iff every edge is within the same bound the read-back uses
    // (<see cref="WindowManager.MaxMeasuredFramePx"/>) — single source of truth, no
    // magic-number drift.
    private static bool Sane(Frame f) =>
        InRange(f.Left) && InRange(f.Top) && InRange(f.Right) && InRange(f.Bottom);

    private static bool InRange(int v) => v >= 0 && v <= WindowManager.MaxMeasuredFramePx;

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<int, Frame>>(json);
            if (data == null) return;
            lock (_lock)
            {
                _byDpi.Clear();
                foreach (var kv in data)
                    if (Sane(kv.Value)) _byDpi[kv.Key] = kv.Value;   // drop any insane stored entry on load
            }
        }
        catch (Exception ex)
        {
            // Corrupt / unreadable cache is non-fatal: start empty and let the
            // read-back re-measure + re-populate. Loud per
            // reference_loud_runtime_silent_rest — a swallowed parse error would
            // silently disable the first-paint optimization with no trace.
            FileLogger.Warn($"FrameCache: failed to load '{_path}' ({ex.GetType().Name}: {ex.Message}) — starting empty");
        }
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            Dictionary<int, Frame> snapshot;
            lock (_lock) { snapshot = new Dictionary<int, Frame>(_byDpi); }
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            // Atomic write (temp + move) so a crash mid-write can't leave a
            // half-written cache — mirrors ConfigManager's save discipline. The temp name
            // is per-thread-unique so two concurrent saves can't collide on one tmp file
            // (Save is effectively guard-tick-only today, but this keeps it race-free).
            var tmp = $"{_path}.{Environment.CurrentManagedThreadId}.tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Best-effort: a failed cache write just means the next launch re-measures
            // (one snap). Never propagate — the guard tick must not crash on disk I/O.
            FileLogger.Warn($"FrameCache: failed to save '{_path}' ({ex.GetType().Name}: {ex.Message}) — cache not persisted this time");
        }
    }
}
