using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Launches an EQ client and auto-types credentials via foreground flash + SendInput.
/// Briefly brings each EQ window to the foreground during its typing phase, uses
/// SendInput for reliable keystroke delivery, then restores the previous window.
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
        // The DLL provides IAT hooks for background input during auto-login.
        DeployDinput8ToEQPath();

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
        // Create shared memory so dinput8.dll proxy can log diagnostics.
        // The DLL hooks GetAsyncKeyState/GetDeviceState and reads keys from SHM.
        // Even while we use foreground flash for typing, this lets us trace
        // which hooks EQ calls during login (debugging true background input).
        var diagWriter = new KeyInputWriter();
        try
        {
            if (diagWriter.Open(pid))
            {
                diagWriter.Activate(pid);
                FileLogger.Info($"AutoLogin: SHM created for PID {pid} (diagnostic + DI injection)");
            }
            else
            {
                FileLogger.Warn($"AutoLogin: SHM creation failed for PID {pid}");
            }

            // Step 1: Wait for EQ window to appear
            Report("Waiting for EQ window...");
            var hwnd = WaitForWindow(pid, TimeSpan.FromSeconds(30));
            if (hwnd == IntPtr.Zero)
            {
                Report("Timeout: EQ window did not appear");
                return;
            }

            // Step 2: Wait for login screen to become input-ready.
            // EQ's login screen takes several seconds after the window appears.
            // Configurable via Settings → Accounts → Login Screen Delay.
            Report("Waiting for login screen...");
            Thread.Sleep(_config.LoginScreenDelayMs);

            // Step 3: Flash the EQ window to foreground for typing.
            // EQ's login screen uses standard Windows message input (WM_CHAR/WM_KEYDOWN),
            // NOT DirectInput, for text fields. SendInput requires foreground focus.
            var previousForeground = NativeMethods.GetForegroundWindow();
            Report("Typing credentials...");
            if (!BringToForeground(hwnd))
            {
                FileLogger.Warn($"AutoLogin: SetForegroundWindow failed for PID {pid}, attempting anyway");
            }
            Thread.Sleep(300); // let EQ process the focus change

            // Step 4: Type username (if /login flag not used)
            // When UseLoginFlag is true, EQ pre-fills username and focuses password field.
            // When false, we must Tab to username, type it, then Tab to password.
            if (!account.UseLoginFlag && !string.IsNullOrEmpty(account.Username))
            {
                Thread.Sleep(200);
                SendPressKey(0x09); // Tab to username field
                Thread.Sleep(100);
                SendTypeString(account.Username);
                Thread.Sleep(100);
            }

            // Step 5: Tab to password field (only if we typed username — /login flag
            // already places cursor on the password field) and type password
            if (!account.UseLoginFlag)
            {
                SendPressKey(0x09); // Tab from username to password
                Thread.Sleep(100);
            }
            SendTypeString(password);
            Thread.Sleep(100);

            // Step 6: Press Enter to submit login
            Report("Submitting login...");
            SendPressKey(0x0D);

            // Restore previous foreground window while we wait for server select.
            // The long waits don't need focus — only the keystroke moments do.
            RestoreForeground(previousForeground);
            Thread.Sleep(3000);

            // Step 7: Server select — flash back, press Enter, restore
            Report("Confirming server...");
            previousForeground = NativeMethods.GetForegroundWindow();
            BringToForeground(hwnd);
            Thread.Sleep(200);
            SendPressKey(0x0D);
            RestoreForeground(previousForeground);
            Thread.Sleep(3000);

            // Step 8: Character select — flash, navigate to slot, restore
            Report($"Selecting character (slot {account.CharacterSlot})...");
            previousForeground = NativeMethods.GetForegroundWindow();
            BringToForeground(hwnd);
            Thread.Sleep(200);
            for (int i = 1; i < account.CharacterSlot; i++)
            {
                SendPressKey(0x28); // VK_DOWN
                Thread.Sleep(200);
            }
            Thread.Sleep(300);

            // Step 9: Enter World
            Report("Entering world...");
            SendPressKey(0x0D);
            RestoreForeground(previousForeground);
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
            try { diagWriter.Close(pid); } catch { /* best effort */ }
            try { diagWriter.Dispose(); } catch { /* best effort */ }
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

    // ─── Foreground Flash + SendInput Helpers ──────────────────────
    //
    // EQ's login screen uses standard Windows message input (WM_CHAR/WM_KEYDOWN)
    // for text fields, NOT DirectInput. SendInput synthesizes keystrokes at the
    // kernel level — works with any input method, but requires foreground focus.
    // We briefly flash each EQ window to the front during its typing phase.

    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    /// <summary>
    /// Send a single key down or up event via SendInput.
    /// </summary>
    private static void SendKeyEvent(ushort vk, bool down)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC),
                    dwFlags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        NativeMethods.SendInput(1, [input], InputSize);
    }

    /// <summary>
    /// Press and release a single key via SendInput.
    /// </summary>
    private static void SendPressKey(ushort vk)
    {
        SendKeyEvent(vk, true);
        Thread.Sleep(60);
        SendKeyEvent(vk, false);
        Thread.Sleep(60);
    }

    /// <summary>
    /// Type a string via SendInput. Handles Shift for uppercase and symbols.
    /// </summary>
    private static void SendTypeString(string text)
    {
        foreach (char c in text)
        {
            short vkScan = NativeMethods.VkKeyScanW(c);
            if (vkScan == -1) continue; // unmappable character

            byte modifiers = (byte)(vkScan >> 8);
            if ((modifiers & ~0x01) != 0) continue; // skip chars needing Ctrl/Alt

            ushort vk = (ushort)(vkScan & 0xFF);
            bool needShift = (modifiers & 0x01) != 0;

            if (needShift)
                SendKeyEvent(0x10, true); // VK_SHIFT down
            SendKeyEvent(vk, true);
            Thread.Sleep(50);
            SendKeyEvent(vk, false);
            if (needShift)
                SendKeyEvent(0x10, false); // VK_SHIFT up
            Thread.Sleep(40);
        }
    }

    /// <summary>
    /// Bring a window to the foreground using the AttachThreadInput trick.
    /// Returns true if the window is now the foreground window.
    /// </summary>
    private static bool BringToForeground(IntPtr hwnd)
    {
        var curForeground = NativeMethods.GetForegroundWindow();
        if (curForeground == hwnd) return true;

        var foreThread = NativeMethods.GetWindowThreadProcessId(curForeground, out _);
        var curThread = NativeMethods.GetCurrentThreadId();
        bool attached = false;

        // Attach to the foreground thread so Windows allows us to steal focus
        if (foreThread != curThread)
            attached = NativeMethods.AttachThreadInput(curThread, foreThread, true);

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);

        if (attached)
            NativeMethods.AttachThreadInput(curThread, foreThread, false);

        // Verify focus arrived
        Thread.Sleep(100);
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// Restore a previously saved foreground window. Best-effort — if the
    /// window was closed in the meantime, this is a no-op.
    /// </summary>
    private static void RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
        NativeMethods.SetForegroundWindow(hwnd);
    }

    // ─── dinput8.dll Deployment ──────────────────────────────────────

    /// <summary>
    /// Copy dinput8.dll to the EQ game directory so it's loaded on next launch.
    /// Best-effort — logs but doesn't block login if deployment fails.
    /// </summary>
    private void DeployDinput8ToEQPath()
    {
        var srcPath = Path.Combine(AppContext.BaseDirectory, "dinput8.dll");
        if (!File.Exists(srcPath))
        {
            FileLogger.Warn("AutoLogin: dinput8.dll not found next to EQSwitch.exe — background input unavailable");
            return;
        }

        var dstPath = Path.Combine(_config.EQPath, "dinput8.dll");

        // Skip if already deployed and up to date
        if (File.Exists(dstPath))
        {
            var srcInfo = new FileInfo(srcPath);
            var dstInfo = new FileInfo(dstPath);
            if (srcInfo.Length == dstInfo.Length && srcInfo.LastWriteTimeUtc <= dstInfo.LastWriteTimeUtc)
                return; // same size and not newer — skip
        }

        try
        {
            File.Copy(srcPath, dstPath, overwrite: true);
            FileLogger.Info($"AutoLogin: deployed dinput8.dll to {dstPath}");
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"AutoLogin: couldn't deploy dinput8.dll: {ex.Message}");
        }
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
