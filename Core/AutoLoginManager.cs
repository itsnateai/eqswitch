using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Launches an EQ client and auto-types credentials via DirectInput shared memory.
/// Requires dinput8.dll deployed to the EQ directory (auto-deployed on login).
/// The proxy DLL intercepts GetDeviceState/GetDeviceData and injects scan codes
/// from per-PID shared memory, enabling background input without focus stealing.
/// Runs the wait/type sequence on a background thread.
/// </summary>
public class AutoLoginManager
{
    private readonly AppConfig _config;

    /// <summary>PIDs currently in the login sequence — DLL injection should be deferred for these.</summary>
    private readonly ConcurrentDictionary<int, byte> _activeLoginPids = new();

    /// <summary>Serializes login sequences — only one login types credentials at a time.
    /// Multiple logins are queued and run sequentially to avoid timing issues under CPU load.</summary>
    private readonly SemaphoreSlim _loginGate = new(1, 1);

    /// <summary>UI thread sync context — captured at construction for marshaling events back to the UI thread.</summary>
    private readonly SynchronizationContext? _syncContext;

    /// <summary>Callback to enforce eqclient.ini overrides before launch (injected to avoid Core→UI dependency).</summary>
    private readonly Action<AppConfig>? _enforceOverrides;

    public event EventHandler<string>? StatusUpdate;

    /// <summary>Fires just before the login sequence starts (use to pause guard timers).</summary>
    public event EventHandler<int>? LoginStarting;

    /// <summary>Fires when a login sequence completes (success or failure) with the PID.</summary>
    public event EventHandler<int>? LoginComplete;

    /// <summary>True if the given PID is currently running through the login sequence.</summary>
    public bool IsLoginActive(int pid) => _activeLoginPids.ContainsKey(pid);

    public AutoLoginManager(AppConfig config, Action<AppConfig>? enforceOverrides = null)
    {
        _config = config;
        _syncContext = SynchronizationContext.Current;
        _enforceOverrides = enforceOverrides;
    }

    /// <summary>
    /// Launch EQ and auto-login with the given account.
    /// Non-blocking — runs the login sequence on a background thread.
    /// </summary>
    public void LoginAccount(LoginAccount account)
    {
        string password;
        try
        {
            password = CredentialManager.Decrypt(account.EncryptedPassword);
        }
        catch (Exception ex)
        {
            FileLogger.Error("AutoLogin: failed to decrypt password", ex);
            StatusUpdate?.Invoke(this, "Error: failed to decrypt password");
            return;
        }

        // Write server to EQ INI files so server select is pre-filled
        WriteServerToIni(account.Server);

        // Build launch args
        var exePath = Path.Combine(_config.EQPath, _config.Launch.ExeName);
        if (!File.Exists(exePath))
        {
            FileLogger.Error($"AutoLogin: exe not found at {exePath}");
            StatusUpdate?.Invoke(this, "Error: eqgame.exe not found");
            return;
        }

        var args = _config.Launch.Arguments;
        if (account.UseLoginFlag && !string.IsNullOrEmpty(account.Username))
            args += $" /login:{account.Username}";

        StatusUpdate?.Invoke(this, $"Launching {account.Name}...");

        // Deploy dinput8.dll to EQ directory before launching.
        // The DLL must be present at launch time — EQ loads it via DLL search order.
        if (!DeployDinput8ToEQPath())
        {
            Report("Error: dinput8.dll deployment failed — cannot auto-login");
            return;
        }

        int pid;
        try
        {
            // Write eqclient.ini overrides before launch
            if (_enforceOverrides != null)
                _enforceOverrides(_config);
            else
                FileLogger.Warn("AutoLogin: no enforceOverrides callback registered, skipping INI overrides");

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = _config.EQPath,
                UseShellExecute = true
            };

            using var proc = Process.Start(startInfo);
            if (proc == null)
            {
                StatusUpdate?.Invoke(this, "Error: failed to start process");
                return;
            }
            pid = proc.Id;
            FileLogger.Info($"AutoLogin: launched PID {pid} for {account.Name}");
        }
        catch (Exception ex)
        {
            FileLogger.Error("AutoLogin: launch failed", ex);
            StatusUpdate?.Invoke(this, $"Error: {ex.Message}");
            return;
        }

        // Track PID so DLL injection is deferred until login completes
        _activeLoginPids.TryAdd(pid, 0);
        LoginStarting?.Invoke(this, pid);

        // Run the login sequence on a background thread — serialized via _loginGate
        // so only one login types credentials at a time (avoids timing issues under CPU load).
        var loginAccount = account;
        Task.Run(async () =>
        {
            await _loginGate.WaitAsync();
            try
            {
                RunLoginSequence(pid, loginAccount, password);
            }
            finally
            {
                _loginGate.Release();
            }
        });
    }

    private void RunLoginSequence(int pid, LoginAccount account, string password)
    {
        // Local writer eliminates shared-state race when two logins run concurrently
        var loginWriter = new KeyInputWriter();
        try
        {
            // Step 1: Create shared memory FIRST, before waiting for the window.
            // The dinput8.dll proxy retries opening shared memory every ~240 frames.
            // At low fps during startup (~10fps with multiple clients), that's ~24 seconds
            // between retries. Creating the mapping early gives the proxy the entire
            // window-creation period + login screen wait to discover it.
            if (!loginWriter.Open(pid))
            {
                Report("Error: failed to open DirectInput shared memory");
                return;
            }
            loginWriter.Activate(pid);

            // Step 2: Wait for EQ window to appear
            Report("Waiting for EQ window...");
            var hwnd = WaitForWindow(pid, TimeSpan.FromSeconds(30));
            if (hwnd == IntPtr.Zero)
            {
                Report("Timeout: EQ window did not appear");
                return;
            }

            // Step 3: Wait for login screen to become input-ready.
            // EQ's login screen takes several seconds after the window appears.
            Report("Waiting for login screen...");
            Thread.Sleep(5000);

            // Step 4: Type username (if /login flag not used)
            // When UseLoginFlag is true, EQ pre-fills username and focuses password field.
            // When false, we must Tab to username, type it, then Tab to password.
            if (!account.UseLoginFlag && !string.IsNullOrEmpty(account.Username))
            {
                Thread.Sleep(200);
                DiPressKey(loginWriter, pid, 0x09); // Tab to username field
                Thread.Sleep(100);
                DiTypeString(loginWriter, pid, account.Username);
                Thread.Sleep(100);
            }

            // Step 5: Tab to password field (only if we typed username — /login flag
            // already places cursor on the password field) and type password
            if (!account.UseLoginFlag)
            {
                DiPressKey(loginWriter, pid, 0x09); // Tab from username to password
                Thread.Sleep(100);
            }
            DiTypeString(loginWriter, pid, password);
            Thread.Sleep(100);

            // Step 6: Press Enter to submit login
            Report("Submitting login...");
            DiPressKey(loginWriter, pid, 0x0D);
            Thread.Sleep(3000);

            // Step 7: Server select — press Enter to confirm pre-selected server
            Report("Confirming server...");
            DiPressKey(loginWriter, pid, 0x0D);
            Thread.Sleep(3000);

            // Step 8: Character select — navigate to character slot
            Report($"Selecting character (slot {account.CharacterSlot})...");
            for (int i = 1; i < account.CharacterSlot; i++)
            {
                DiPressKey(loginWriter, pid, 0x28); // VK_DOWN
                Thread.Sleep(200);
            }
            Thread.Sleep(300);

            // Step 9: Enter World
            Report("Entering world...");
            DiPressKey(loginWriter, pid, 0x0D);
            Thread.Sleep(1000);

            Report($"{account.Name} logged in!");
            FileLogger.Info($"AutoLogin: {account.Name} login sequence complete (PID {pid})");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"AutoLogin: sequence failed for {account.Name}", ex);
            Report($"Error: {ex.Message}");
        }
        finally
        {
            // Each statement is independently guarded so TryRemove and LoginComplete
            // always fire — a throw in Close must never leave a phantom PID in
            // _activeLoginPids (which would permanently block injection for that client).
            try { loginWriter.Close(pid); } catch (Exception ex) { FileLogger.Warn($"AutoLogin: Close failed: {ex.Message}"); }
            try { loginWriter.Dispose(); } catch (Exception ex) { FileLogger.Warn($"AutoLogin: Dispose failed: {ex.Message}"); }
            _activeLoginPids.TryRemove(pid, out _);
            if (_syncContext != null)
                _syncContext.Post(_ => LoginComplete?.Invoke(this, pid), null);
            else
                LoginComplete?.Invoke(this, pid);
        }
    }

    private void Report(string message)
    {
        FileLogger.Info($"AutoLogin: {message}");
        if (_syncContext != null)
            _syncContext.Post(_ => StatusUpdate?.Invoke(this, message), null);
        else
            StatusUpdate?.Invoke(this, message);
    }

    /// <summary>
    /// Poll for an eqgame window belonging to the given PID.
    /// </summary>
    private static IntPtr WaitForWindow(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                var hwnd = proc.MainWindowHandle;
                if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd))
                    return hwnd;
            }
            catch (ArgumentException)
            {
                return IntPtr.Zero; // Process exited
            }
            Thread.Sleep(500);
        }
        return IntPtr.Zero;
    }

    // ─── DirectInput Shared Memory Helpers ──────────────────────────
    //
    // EQ uses DirectInput for ALL keyboard input, including the login screen.
    // PostMessage/WM_CHAR does NOT work. We write scan codes to the per-PID
    // shared memory that dinput8.dll's DeviceProxy reads on each
    // GetDeviceState/GetDeviceData call.

    /// <summary>
    /// Set a scan code state in shared memory. The dinput8.dll proxy picks
    /// this up on the next GetDeviceState call (~60Hz).
    /// </summary>
    private static void DiSetKey(KeyInputWriter writer, int pid, ushort vk, bool pressed)
    {
        uint scan = NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);
        if (scan > 0 && scan < 256)
            writer.SetKey(pid, (byte)scan, pressed);
    }

    /// <summary>
    /// Press and release a single key via DirectInput shared memory.
    /// </summary>
    private static void DiPressKey(KeyInputWriter writer, int pid, ushort vk)
    {
        DiSetKey(writer, pid, vk, true);
        Thread.Sleep(80);
        DiSetKey(writer, pid, vk, false);
        Thread.Sleep(80);
    }

    /// <summary>
    /// Type a string by converting each character to its VK + scan code.
    /// Handles Shift for uppercase and symbols (e.g. '!' = Shift+'1').
    /// </summary>
    private static void DiTypeString(KeyInputWriter writer, int pid, string text)
    {
        foreach (char c in text)
        {
            short vkScan = NativeMethods.VkKeyScanW(c);
            if (vkScan == -1) continue; // unmappable character

            byte modifiers = (byte)(vkScan >> 8);
            if ((modifiers & ~0x01) != 0) continue; // skip chars needing Ctrl/Alt (non-US layouts)

            ushort vk = (ushort)(vkScan & 0xFF);
            bool needShift = (modifiers & 0x01) != 0;

            if (needShift)
            {
                DiSetKey(writer, pid, 0x10, true); // VK_SHIFT down
                Thread.Sleep(20); // let proxy see Shift before the character key
            }
            DiSetKey(writer, pid, vk, true);
            Thread.Sleep(80);
            DiSetKey(writer, pid, vk, false);
            if (needShift)
            {
                Thread.Sleep(20); // let proxy see key-up before Shift release
                DiSetKey(writer, pid, 0x10, false); // VK_SHIFT up
            }
            Thread.Sleep(50);
        }
    }

    // ─── dinput8.dll Deployment ──────────────────────────────────────

    /// <summary>
    /// Copy dinput8.dll to the EQ game directory so it's loaded on next launch.
    /// Returns true on success. Handles antivirus blocks and existing files
    /// from other tools. The proxy DLL provides IAT hooks that fake window
    /// focus, enabling background input during auto-login.
    /// </summary>
    private bool DeployDinput8ToEQPath()
    {
        var srcPath = Path.Combine(AppContext.BaseDirectory, "dinput8.dll");
        if (!File.Exists(srcPath))
        {
            FileLogger.Warn("AutoLogin: dinput8.dll not found next to EQSwitch.exe");
            StatusUpdate?.Invoke(this, "Error: dinput8.dll missing from EQSwitch folder");
            return false;
        }

        var dstPath = Path.Combine(_config.EQPath, "dinput8.dll");

        // Check if already deployed and up to date (SHA256 hash comparison)
        if (File.Exists(dstPath))
        {
            var srcHash = SHA256.HashData(File.ReadAllBytes(srcPath));
            var dstHash = SHA256.HashData(File.ReadAllBytes(dstPath));
            if (srcHash.AsSpan().SequenceEqual(dstHash))
            {
                FileLogger.Info("AutoLogin: dinput8.dll already deployed and up to date (hash match)");
                return true;
            }
            // Different hash = different version (ours vs another tool's).
            // Back up the existing one before overwriting.
            var backupPath = dstPath + ".old";
            try
            {
                File.Copy(dstPath, backupPath, overwrite: true);
                FileLogger.Info("AutoLogin: backed up existing dinput8.dll to .old");
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"AutoLogin: couldn't back up existing dinput8.dll: {ex.Message}");
            }
        }

        try
        {
            File.Copy(srcPath, dstPath, overwrite: true);
            FileLogger.Info($"AutoLogin: deployed dinput8.dll to {dstPath}");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            FileLogger.Error("AutoLogin: blocked deploying dinput8.dll — antivirus or permissions");
            StatusUpdate?.Invoke(this,
                "Blocked: add EQ folder to Windows Defender exclusions, then retry");
            return false;
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) // sharing violation
        {
            FileLogger.Warn("AutoLogin: dinput8.dll in use and outdated — close EQ then retry");
            StatusUpdate?.Invoke(this, "Error: dinput8.dll in use — close all EQ clients and retry");
            return false;
        }
        catch (Exception ex)
        {
            FileLogger.Error($"AutoLogin: failed to deploy dinput8.dll: {ex.Message}");
            StatusUpdate?.Invoke(this, $"Error deploying dinput8.dll: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if dinput8.dll is deployed to the EQ directory.
    /// </summary>
    public bool IsDinput8Deployed()
    {
        var dstPath = Path.Combine(_config.EQPath, "dinput8.dll");
        return File.Exists(dstPath);
    }

    // ─── EQ INI Helpers ──────────────────────────────────────────────

    /// <summary>
    /// Write LastServerName to all eqlsPlayerData*.ini files so the server
    /// is pre-selected on the server select screen.
    /// </summary>
    private void WriteServerToIni(string server)
    {
        if (string.IsNullOrEmpty(server)) return;

        try
        {
            var files = Directory.GetFiles(_config.EQPath, "eqlsPlayerData*.ini");
            foreach (var file in files)
            {
                WriteIniValue(file, "MISC", "LastServerName", server);
                FileLogger.Info($"AutoLogin: wrote LastServerName={server} to {Path.GetFileName(file)}");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"AutoLogin: failed to write server to INI: {ex.Message}");
        }
    }

    /// <summary>
    /// Simple INI writer — finds [section] and updates key=value, or appends.
    /// EQ INI files use ANSI encoding.
    /// </summary>
    private static void WriteIniValue(string path, string section, string key, string value)
    {
        var lines = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path, Encoding.Default))
            : new List<string>();

        string sectionHeader = $"[{section}]";
        int sectionIdx = -1;
        int keyIdx = -1;
        int nextSectionIdx = lines.Count;

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                sectionIdx = i;
                continue;
            }
            if (sectionIdx >= 0 && trimmed.StartsWith('['))
            {
                nextSectionIdx = i;
                break;
            }
            if (sectionIdx >= 0 && trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                keyIdx = i;
            }
        }

        string entry = $"{key}={value}";
        if (keyIdx >= 0)
        {
            lines[keyIdx] = entry;
        }
        else if (sectionIdx >= 0)
        {
            lines.Insert(sectionIdx + 1, entry);
        }
        else
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add(sectionHeader);
            lines.Add(entry);
        }

        File.WriteAllLines(path, lines, Encoding.Default);
    }
}
