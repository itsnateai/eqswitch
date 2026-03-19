using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Detects and tracks running EQ client processes.
/// Polls on a timer to discover new clients and prune dead ones.
/// </summary>
public class ProcessManager : IDisposable
{
    private readonly AppConfig _config;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly List<EQClient> _clients = new();
    private readonly object _lock = new();

    // Copy-on-write snapshot: only rebuilt when _clients changes.
    // Avoids allocating a new List on every .Clients access (250ms affinity timer = 1M+ reads/72h).
    private IReadOnlyList<EQClient> _snapshot = Array.Empty<EQClient>();

    /// <summary>
    /// Idle polling interval when no EQ clients are running (ms).
    /// 5 seconds is plenty — EQ is launched externally, no rush to detect.
    /// </summary>
    private const int IdlePollingMs = 5000;

    private bool _isRefreshing;

    public event EventHandler<EQClient>? ClientDiscovered;
    public event EventHandler<EQClient>? ClientLost;
    public event EventHandler? ClientListChanged;

    public IReadOnlyList<EQClient> Clients
    {
        get { lock (_lock) return _snapshot; }
    }

    public int ClientCount
    {
        get { lock (_lock) return _clients.Count; }
    }

    /// <summary>
    /// Rebuild the copy-on-write snapshot. Call inside the lock after any mutation of _clients.
    /// </summary>
    private void InvalidateSnapshot()
    {
        _snapshot = _clients.ToList().AsReadOnly();
    }

    public ProcessManager(AppConfig config)
    {
        _config = config;
        _pollTimer = new System.Windows.Forms.Timer
        {
            Interval = config.PollingIntervalMs
        };
        _pollTimer.Tick += (_, _) => RefreshClients();
    }

    public void StartPolling() => _pollTimer.Start();
    public void StopPolling() => _pollTimer.Stop();

    public void UpdatePollingInterval(int intervalMs)
    {
        _pollTimer.Interval = intervalMs;
    }

    /// <summary>
    /// Scan for EQ processes and update the client list.
    /// Events are fired outside the lock to prevent deadlocks.
    /// </summary>
    public void RefreshClients()
    {
        // Guard against re-entrancy (e.g. manual call while timer tick is mid-flight)
        if (_isRefreshing) return;
        _isRefreshing = true;

        Process[]? eqProcesses = null;

        // Collect events to fire outside the lock
        var lostClients = new List<EQClient>();
        var discoveredClients = new List<EQClient>();
        bool listChanged = false;

        try
        {
            eqProcesses = Process.GetProcessesByName(_config.EQProcessName);
            var currentPids = new HashSet<int>(eqProcesses.Select(p => p.Id));

            lock (_lock)
            {
                // Remove dead clients
                var dead = _clients.Where(c => !currentPids.Contains(c.ProcessId) || !c.IsProcessAlive()).ToList();
                foreach (var client in dead)
                {
                    _clients.Remove(client);
                    lostClients.Add(client);
                }

                // Discover new clients
                var knownPids = new HashSet<int>(_clients.Select(c => c.ProcessId));
                foreach (var proc in eqProcesses)
                {
                    if (knownPids.Contains(proc.Id)) continue;

                    try
                    {
                        var hwnd = proc.MainWindowHandle;
                        if (hwnd == IntPtr.Zero) continue;

                        var client = new EQClient
                        {
                            WindowHandle = hwnd,
                            ProcessId = proc.Id,
                            WindowTitle = GetWindowTitle(hwnd),
                            SlotIndex = _clients.Count
                        };
                        client.ResolveCharacterName();
                        MatchCharacterProfile(client);

                        _clients.Add(client);
                        discoveredClients.Add(client);
                    }
                    catch (InvalidOperationException)
                    {
                        // Process exited between enumeration and access — expected
                    }
                    catch (Win32Exception ex)
                    {
                        FileLogger.Warn($"Access denied for process {proc.Id}: {ex.Message}");
                    }
                }

                // Refresh stale window handles and titles
                // EQ can recreate its window during gameplay — the stored handle becomes invalid.
                // Use Process.MainWindowHandle to get the fresh handle every poll cycle.
                bool titleChanged = false;
                foreach (var proc in eqProcesses)
                {
                    var client = _clients.FirstOrDefault(c => c.ProcessId == proc.Id);
                    if (client == null) continue;

                    // Update stale window handle from process
                    var freshHwnd = proc.MainWindowHandle;
                    if (freshHwnd != IntPtr.Zero && freshHwnd != client.WindowHandle)
                    {
                        FileLogger.Info($"RefreshClients: updated stale handle for PID {proc.Id} ({client.CharacterName}): 0x{client.WindowHandle:X} → 0x{freshHwnd:X}");
                        client.WindowHandle = freshHwnd;
                    }

                    var newTitle = GetWindowTitle(client.WindowHandle);
                    if (newTitle != client.WindowTitle)
                    {
                        client.WindowTitle = newTitle;
                        client.ResolveCharacterName();
                        MatchCharacterProfile(client);
                        titleChanged = true;
                    }
                }

                listChanged = lostClients.Count > 0 || discoveredClients.Count > 0 || titleChanged;

                // Rebuild snapshot only when list actually changed
                if (listChanged)
                    InvalidateSnapshot();
            }
        }
        catch (InvalidOperationException)
        {
            // Process collection changed during enumeration — retry next tick
        }
        catch (Win32Exception ex)
        {
            FileLogger.Warn($"RefreshClients Win32 error: {ex.Message}");
        }
        catch (Exception ex)
        {
            FileLogger.Error("RefreshClients unexpected error", ex);
        }
        finally
        {
            if (eqProcesses != null)
                foreach (var p in eqProcesses)
                    p.Dispose();
            _isRefreshing = false;
        }

        // Fire events OUTSIDE the lock to prevent deadlocks
        foreach (var client in lostClients)
            ClientLost?.Invoke(this, client);
        foreach (var client in discoveredClients)
            ClientDiscovered?.Invoke(this, client);
        if (listChanged)
            ClientListChanged?.Invoke(this, EventArgs.Empty);

        // Adaptive polling: slow down when idle, speed up when clients exist
        int targetInterval = ClientCount > 0 ? _config.PollingIntervalMs : IdlePollingMs;
        if (_pollTimer.Interval != targetInterval)
            _pollTimer.Interval = targetInterval;
    }

    /// <summary>
    /// Get the currently active (foreground) EQ client, if any.
    /// </summary>
    public EQClient? GetActiveClient()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;

        // Get the PID of the foreground window — more reliable than matching
        // by window handle, since EQ can recreate its window during gameplay.
        NativeMethods.GetWindowThreadProcessId(foreground, out uint fgPid);
        lock (_lock)
        {
            var client = _clients.FirstOrDefault(c => c.ProcessId == (int)fgPid);
            if (client != null && client.WindowHandle != foreground)
            {
                // Update stale handle
                client.WindowHandle = foreground;
                client.WindowTitle = GetWindowTitle(foreground);
                client.ResolveCharacterName();
            }
            return client;
        }
    }

    /// <summary>
    /// Get client by slot index (0-based).
    /// </summary>
    public EQClient? GetClientBySlot(int slot)
    {
        lock (_lock)
        {
            return _clients.FirstOrDefault(c => c.SlotIndex == slot);
        }
    }

    private void MatchCharacterProfile(EQClient client)
    {
        if (string.IsNullOrEmpty(client.CharacterName)) return;

        var profile = _config.Characters.FirstOrDefault(
            c => c.Name.Equals(client.CharacterName, StringComparison.OrdinalIgnoreCase));

        if (profile != null)
            client.SlotIndex = profile.SlotIndex;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0) return "";

        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
    }
}
