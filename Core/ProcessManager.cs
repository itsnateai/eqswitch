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

    public event EventHandler<EQClient>? ClientDiscovered;
    public event EventHandler<EQClient>? ClientLost;
    public event EventHandler? ClientListChanged;

    public IReadOnlyList<EQClient> Clients
    {
        get { lock (_lock) return _clients.ToList().AsReadOnly(); }
    }

    public int ClientCount
    {
        get { lock (_lock) return _clients.Count; }
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
    /// </summary>
    public void RefreshClients()
    {
        try
        {
            var eqProcesses = Process.GetProcessesByName(_config.EQProcessName);
            var currentPids = new HashSet<int>(eqProcesses.Select(p => p.Id));

            lock (_lock)
            {
                // Remove dead clients
                var dead = _clients.Where(c => !currentPids.Contains(c.ProcessId) || !c.IsProcessAlive()).ToList();
                foreach (var client in dead)
                {
                    _clients.Remove(client);
                    ClientLost?.Invoke(this, client);
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

                        // Match to character profile if possible
                        MatchCharacterProfile(client);

                        _clients.Add(client);
                        ClientDiscovered?.Invoke(this, client);
                    }
                    catch { /* Process may have exited between enumeration and access */ }
                }

                // Refresh window titles (character may have logged in)
                foreach (var client in _clients)
                {
                    var newTitle = GetWindowTitle(client.WindowHandle);
                    if (newTitle != client.WindowTitle)
                    {
                        client.WindowTitle = newTitle;
                        client.ResolveCharacterName();
                        MatchCharacterProfile(client);
                    }
                }

                if (dead.Count > 0 || eqProcesses.Length != knownPids.Count)
                    ClientListChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RefreshClients error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the currently active (foreground) EQ client, if any.
    /// </summary>
    public EQClient? GetActiveClient()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        lock (_lock)
        {
            return _clients.FirstOrDefault(c => c.WindowHandle == foreground);
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
        {
            client.SlotIndex = profile.SlotIndex;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len == 0) return "";

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
