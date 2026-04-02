using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Launches an EQ client and auto-types credentials.
/// Two modes:
///   - Legacy (BackgroundLogin=false): SendInput + focus-stealing (original behavior)
///   - Background (BackgroundLogin=true): PostMessage to type in background windows,
///     requires dinput8.dll deployed to EQ directory for IAT focus-faking hooks.
/// Runs the wait/type sequence on a background thread.
/// </summary>
public class AutoLoginManager
{
    private readonly AppConfig _config;

    /// <summary>PIDs currently in the login sequence — DLL injection should be deferred for these.</summary>
    private readonly HashSet<int> _activeLoginPids = new();

    /// <summary>Per-login shared memory writer for DirectInput key injection.</summary>
    private KeyInputWriter? _loginWriter;

    public event EventHandler<string>? StatusUpdate;

    /// <summary>Fires just before the login sequence starts (use to pause guard timers).</summary>
    public event EventHandler<int>? LoginStarting;

    /// <summary>Fires when a login sequence completes (success or failure) with the PID.</summary>
    public event EventHandler<int>? LoginComplete;

    /// <summary>True if the given PID is currently running through the login sequence.</summary>
    public bool IsLoginActive(int pid) => _activeLoginPids.Contains(pid);

    public AutoLoginManager(AppConfig config)
    {
        _config = config;
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
        LoginStarting?.Invoke(this, 0);

        bool backgroundMode = _config.BackgroundLogin;

        // Deploy dinput8.dll to EQ directory before launching if background mode is on.
        // The DLL must be present at launch time — EQ loads it via DLL search order.
        if (backgroundMode)
            DeployDinput8ToEQPath();

        int pid;
        try
        {
            // Write eqclient.ini overrides before launch
            EQSwitch.UI.EQClientSettingsForm.EnforceOverrides(_config);

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
            FileLogger.Info($"AutoLogin: launched PID {pid} for {account.Name} (background={backgroundMode})");
        }
        catch (Exception ex)
        {
            FileLogger.Error("AutoLogin: launch failed", ex);
            StatusUpdate?.Invoke(this, $"Error: {ex.Message}");
            return;
        }

        // Track PID so DLL injection is deferred until login completes
        _activeLoginPids.Add(pid);

        // Run the login sequence on a background thread
        var loginAccount = account;
        Task.Run(() => RunLoginSequence(pid, loginAccount, password, backgroundMode));
    }

    private void RunLoginSequence(int pid, LoginAccount account, string password, bool background)
    {
        try
        {
            // Step 1: Wait for EQ window to appear
            Report("Waiting for EQ window...");
            var hwnd = WaitForWindow(pid, TimeSpan.FromSeconds(30));
            if (hwnd == IntPtr.Zero)
            {
                Report("Timeout: EQ window did not appear");
                return;
            }

            // Step 2: Wait for login screen to be ready
            if (background)
            {
                // Background mode: open shared memory so dinput8.dll can read our keys.
                // dinput8.dll lazy-opens the mapping with retry, so give it time to find it.
                Report("Waiting for login screen (background)...");
                _loginWriter = new KeyInputWriter();
                _loginWriter.Open(pid);
                _loginWriter.Activate(pid);
                Thread.Sleep(3000); // let EQ fully init + dinput8.dll find shared memory
            }
            else
            {
                // Legacy mode: bring to foreground
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                ForceForeground(hwnd);
                Report("Waiting for login screen...");
                Thread.Sleep(2000);
            }

            // Step 3: Type username (if /login flag not used)
            if (!account.UseLoginFlag && !string.IsNullOrEmpty(account.Username))
            {
                if (background)
                {
                    Thread.Sleep(200);
                    DiPressKey(pid, 0x09); // Tab to username field
                    Thread.Sleep(100);
                    DiTypeString(pid, account.Username);
                    Thread.Sleep(100);
                }
                else
                {
                    ForceForeground(hwnd);
                    Thread.Sleep(200);
                    FocusAndSendKey(hwnd, 0x09);
                    Thread.Sleep(100);
                    TypeString(account.Username, hwnd);
                    Thread.Sleep(100);
                }
            }

            // Step 4: Tab to password field and type password
            if (background)
            {
                DiPressKey(pid, 0x09); // Tab
                Thread.Sleep(100);
                DiTypeString(pid, password);
                Thread.Sleep(100);
            }
            else
            {
                FocusAndSendKey(hwnd, 0x09);
                Thread.Sleep(100);
                TypeString(password, hwnd);
                Thread.Sleep(100);
            }

            // Step 5: Press Enter to submit login
            Report("Submitting login...");
            if (background)
                DiPressKey(pid, 0x0D); // Enter
            else
                FocusAndSendKey(hwnd, 0x0D);
            Thread.Sleep(3000);

            // Step 6: Server select — press Enter to confirm pre-selected server
            Report("Confirming server...");
            if (background)
                DiPressKey(pid, 0x0D);
            else
                FocusAndSendKey(hwnd, 0x0D);
            Thread.Sleep(3000);

            // Step 7: Character select — navigate to character slot
            Report($"Selecting character (slot {account.CharacterSlot})...");
            for (int i = 1; i < account.CharacterSlot; i++)
            {
                if (background)
                    DiPressKey(pid, 0x28); // VK_DOWN
                else
                    FocusAndSendKey(hwnd, 0x28);
                Thread.Sleep(200);
            }
            Thread.Sleep(300);

            // Step 8: Enter World
            Report("Entering world...");
            if (background)
                DiPressKey(pid, 0x0D);
            else
                FocusAndSendKey(hwnd, 0x0D);
            Thread.Sleep(1000);

            Report($"{account.Name} logged in!");
            FileLogger.Info($"AutoLogin: {account.Name} login sequence complete (PID {pid}, background={background})");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"AutoLogin: sequence failed for {account.Name}", ex);
            Report($"Error: {ex.Message}");
        }
        finally
        {
            // Clean up shared memory for this login session
            if (background && _loginWriter != null)
            {
                _loginWriter.Deactivate(pid);
                _loginWriter.Close(pid);
                _loginWriter.Dispose();
                _loginWriter = null;
            }
            _activeLoginPids.Remove(pid);
            LoginComplete?.Invoke(this, pid);
        }
    }

    private void Report(string message)
    {
        FileLogger.Info($"AutoLogin: {message}");
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

    private static void ForceForeground(IntPtr hwnd)
    {
        var foreThread = NativeMethods.GetCurrentThreadId();
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        NativeMethods.AttachThreadInput(foreThread, targetThread, true);
        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.AttachThreadInput(foreThread, targetThread, false);
    }

    // ─── DirectInput Shared Memory Helpers (Background Mode) ────────
    //
    // EQ uses DirectInput for ALL keyboard input, including the login screen.
    // PostMessage/WM_CHAR does NOT work. We must write scan codes to the
    // per-PID shared memory that dinput8.dll's DeviceProxy reads on each
    // GetDeviceState/GetDeviceData call.

    /// <summary>
    /// Set a scan code state in shared memory. The dinput8.dll proxy picks
    /// this up on the next GetDeviceState call (~60Hz).
    /// </summary>
    private void DiSetKey(int pid, ushort vk, bool pressed)
    {
        uint scan = NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);
        if (scan > 0 && scan < 256)
            _loginWriter?.SetKey(pid, (byte)scan, pressed);
    }

    /// <summary>
    /// Press and release a single key via DirectInput shared memory.
    /// </summary>
    private void DiPressKey(int pid, ushort vk)
    {
        DiSetKey(pid, vk, true);
        Thread.Sleep(80);
        DiSetKey(pid, vk, false);
        Thread.Sleep(80);
    }

    /// <summary>
    /// Type a string by converting each character to its VK + scan code.
    /// Handles Shift for uppercase and symbols (e.g. '!' = Shift+'1').
    /// </summary>
    private void DiTypeString(int pid, string text)
    {
        foreach (char c in text)
        {
            short vkScan = NativeMethods.VkKeyScanW(c);
            if (vkScan == -1) continue; // unmappable character

            ushort vk = (ushort)(vkScan & 0xFF);
            bool needShift = (vkScan & 0x100) != 0;

            if (needShift) DiSetKey(pid, 0x10, true); // VK_SHIFT down
            DiSetKey(pid, vk, true);
            Thread.Sleep(80);
            DiSetKey(pid, vk, false);
            if (needShift) DiSetKey(pid, 0x10, false); // VK_SHIFT up
            Thread.Sleep(50);
        }
    }

    // ─── dinput8.dll Deployment ──────────────────────────────────────

    /// <summary>
    /// Copy dinput8.dll to the EQ game directory so it's loaded on next launch.
    /// The proxy DLL provides IAT hooks that fake window focus, enabling
    /// background input during auto-login.
    /// Handles antivirus blocks and existing files from other tools.
    /// </summary>
    private void DeployDinput8ToEQPath()
    {
        var srcPath = Path.Combine(AppContext.BaseDirectory, "dinput8.dll");
        if (!File.Exists(srcPath))
        {
            FileLogger.Warn("AutoLogin: dinput8.dll not found next to EQSwitch.exe");
            StatusUpdate?.Invoke(this, "Error: dinput8.dll missing from EQSwitch folder");
            return;
        }

        var dstPath = Path.Combine(_config.EQPath, "dinput8.dll");

        // Check if already deployed and up to date (compare file size)
        if (File.Exists(dstPath))
        {
            var srcSize = new FileInfo(srcPath).Length;
            var dstSize = new FileInfo(dstPath).Length;
            if (srcSize == dstSize)
            {
                FileLogger.Info("AutoLogin: dinput8.dll already deployed and up to date");
                return;
            }
            // Different size = different version (ours vs another tool's).
            // Back up the existing one before overwriting.
            var backupPath = dstPath + ".old";
            try
            {
                File.Copy(dstPath, backupPath, overwrite: true);
                FileLogger.Info($"AutoLogin: backed up existing dinput8.dll ({dstSize} bytes) to .old");
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
        }
        catch (UnauthorizedAccessException)
        {
            FileLogger.Error("AutoLogin: blocked deploying dinput8.dll — antivirus or permissions");
            StatusUpdate?.Invoke(this,
                "Blocked: add EQ folder to Windows Defender exclusions, then retry");
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) // sharing violation
        {
            FileLogger.Warn("AutoLogin: dinput8.dll in use — EQ restart needed to update");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"AutoLogin: failed to deploy dinput8.dll: {ex.Message}");
            StatusUpdate?.Invoke(this, $"Error deploying dinput8.dll: {ex.Message}");
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

    // ─── SendInput Helpers (Legacy Mode) ─────────────────────────────

    /// <summary>
    /// Type a string using KEYEVENTF_UNICODE. This generates WM_CHAR messages
    /// which EQ's login screen text fields respond to (scan codes alone don't
    /// produce WM_CHAR, so the old approach failed on the login screen).
    /// Re-focuses the target window before each character to survive focus theft.
    /// </summary>
    private static void TypeString(string text, IntPtr hwnd)
    {
        foreach (char c in text)
        {
            ForceForeground(hwnd);
            Thread.Sleep(30);

            var down = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = NativeMethods.KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            var up = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            NativeMethods.SendInput(1, new[] { down }, Marshal.SizeOf<NativeMethods.INPUT>());
            Thread.Sleep(50);
            NativeMethods.SendInput(1, new[] { up }, Marshal.SizeOf<NativeMethods.INPUT>());
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Re-focus the target window then press a key. Survives focus theft.
    /// </summary>
    private static void FocusAndSendKey(IntPtr hwnd, ushort vk)
    {
        ForceForeground(hwnd);
        Thread.Sleep(30);
        SendKey(vk);
    }

    /// <summary>
    /// Press and release a single key by VK code.
    /// </summary>
    private static void SendKey(ushort vk)
    {
        ushort scan = (ushort)NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);
        SendKeyDown(vk, scan);
        Thread.Sleep(50);
        SendKeyUp(vk, scan);
        Thread.Sleep(50);
    }

    private static void SendKeyDown(ushort vk, ushort scan)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = NativeMethods.KEYEVENTF_SCANCODE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendKeyUp(ushort vk, ushort scan)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = NativeMethods.KEYEVENTF_SCANCODE | NativeMethods.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
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
