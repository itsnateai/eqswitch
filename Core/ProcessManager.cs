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
    /// Polling interval (ms). 10s is plenty — EQ launches slowly,
    /// and hotkeys/switching don't depend on the poll timer.
    /// </summary>
    private const int IdlePollingMs = 10000;

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
    /// Uses ToArray() (single allocation) instead of ToList().AsReadOnly() (two allocations).
    /// </summary>
    private void InvalidateSnapshot()
    {
        _snapshot = _clients.ToArray();
    }

    public ProcessManager(AppConfig config)
    {
        _config = config;
        _pollTimer = new System.Windows.Forms.Timer
        {
            Interval = IdlePollingMs
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

        // Collect events to fire outside the lock (lazy-init to avoid allocation on quiet polls)
        List<EQClient>? lostClients = null;
        List<EQClient>? discoveredClients = null;
        bool listChanged = false;

        try
        {
            eqProcesses = Process.GetProcessesByName(_config.EQProcessName);

            lock (_lock)
            {
                // Remove dead clients — iterate backward to avoid index shifting.
                // Linear scan of eqProcesses (typically 1-6) is faster than HashSet for small N.
                for (int i = _clients.Count - 1; i >= 0; i--)
                {
                    var client = _clients[i];
                    bool foundInProcesses = false;
                    for (int j = 0; j < eqProcesses.Length; j++)
                    {
                        if (eqProcesses[j].Id == client.ProcessId) { foundInProcesses = true; break; }
                    }
                    if (!foundInProcesses || !client.IsProcessAlive())
                    {
                        _clients.RemoveAt(i);
                        (lostClients ??= new List<EQClient>()).Add(client);
                    }
                }

                // Discover new clients — linear scan of _clients (typically 1-6)
                foreach (var proc in eqProcesses)
                {
                    bool alreadyKnown = false;
                    for (int k = 0; k < _clients.Count; k++)
                    {
                        if (_clients[k].ProcessId == proc.Id) { alreadyKnown = true; break; }
                    }
                    if (alreadyKnown) continue;

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
                        (discoveredClients ??= new List<EQClient>()).Add(client);
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
                    EQClient? client = null;
                    for (int k = 0; k < _clients.Count; k++)
                    {
                        if (_clients[k].ProcessId == proc.Id) { client = _clients[k]; break; }
                    }
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

                listChanged = lostClients != null || discoveredClients != null || titleChanged;

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
        if (lostClients != null)
            foreach (var client in lostClients)
                ClientLost?.Invoke(this, client);
        if (discoveredClients != null)
            foreach (var client in discoveredClients)
                ClientDiscovered?.Invoke(this, client);
        if (listChanged)
            ClientListChanged?.Invoke(this, EventArgs.Empty);

        // Fixed 10s polling — hotkeys/switching don't depend on poll timer
        if (_pollTimer.Interval != IdlePollingMs)
            _pollTimer.Interval = IdlePollingMs;
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
            EQClient? client = null;
            for (int i = 0; i < _clients.Count; i++)
            {
                if (_clients[i].ProcessId == (int)fgPid) { client = _clients[i]; break; }
            }
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
            for (int i = 0; i < _clients.Count; i++)
            {
                if (_clients[i].SlotIndex == slot) return _clients[i];
            }
            return null;
        }
    }

    private void MatchCharacterProfile(EQClient client)
    {
        if (string.IsNullOrEmpty(client.CharacterName)) return;

        var characters = _config.Characters;
        for (int i = 0; i < characters.Count; i++)
        {
            if (characters[i].Name.Equals(client.CharacterName, StringComparison.OrdinalIgnoreCase))
            {
                client.SlotIndex = characters[i].SlotIndex;
                return;
            }
        }
    }

    // Reuse StringBuilder across calls to avoid allocating one every 500ms per window.
    // Over 72h with 4 windows: 4 × 2/sec × 259,200s = ~2M allocations avoided.
    [ThreadStatic] private static StringBuilder? t_titleBuffer;

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0) return "";

        var sb = t_titleBuffer ??= new StringBuilder(256);
        sb.Clear();
        if (sb.Capacity < len + 1) sb.Capacity = len + 1;
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
    }
}
