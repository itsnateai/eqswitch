using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private readonly ConcurrentDictionary<int, byte> _activeLoginPids = new();

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
        LoginStarting?.Invoke(this, 0);

        bool backgroundMode = _config.BackgroundLogin;

        // Deploy dinput8.dll to EQ directory before launching if background mode is on.
        // The DLL must be present at launch time — EQ loads it via DLL search order.
        if (backgroundMode && !DeployDinput8ToEQPath())
        {
            FileLogger.Warn("AutoLogin: dinput8 deploy failed, falling back to legacy mode");
            Report("Background login unavailable — falling back to foreground mode");
            backgroundMode = false;
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
            FileLogger.Info($"AutoLogin: launched PID {pid} for {account.Name} (background={backgroundMode})");
        }
        catch (Exception ex)
        {
            FileLogger.Error("AutoLogin: launch failed", ex);
            StatusUpdate?.Invoke(this, $"Error: {ex.Message}");
            return;
        }

        // Track PID so DLL injection is deferred until login completes
        _activeLoginPids.TryAdd(pid, 0);

        // Run the login sequence on a background thread
        var loginAccount = account;
        Task.Run(() => RunLoginSequence(pid, loginAccount, password, backgroundMode));
    }

    private void RunLoginSequence(int pid, LoginAccount account, string password, bool background)
    {
        // Local writer eliminates shared-state race when two logins run concurrently
        KeyInputWriter? loginWriter = null;
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
                loginWriter = new KeyInputWriter();
                loginWriter.Open(pid);
                loginWriter.Activate(pid);
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
                    DiPressKey(loginWriter, pid, 0x09); // Tab to username field
                    Thread.Sleep(100);
                    DiTypeString(loginWriter, pid, account.Username);
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
                DiPressKey(loginWriter, pid, 0x09); // Tab
                Thread.Sleep(100);
                DiTypeString(loginWriter, pid, password);
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
                DiPressKey(loginWriter, pid, 0x0D); // Enter
            else
                FocusAndSendKey(hwnd, 0x0D);
            Thread.Sleep(3000);

            // Step 6: Server select — press Enter to confirm pre-selected server
            Report("Confirming server...");
            if (background)
                DiPressKey(loginWriter, pid, 0x0D);
            else
                FocusAndSendKey(hwnd, 0x0D);
            Thread.Sleep(3000);

            // Step 7: Character select — navigate to character slot
            Report($"Selecting character (slot {account.CharacterSlot})...");
            for (int i = 1; i < account.CharacterSlot; i++)
            {
                if (background)
                    DiPressKey(loginWriter, pid, 0x28); // VK_DOWN
                else
                    FocusAndSendKey(hwnd, 0x28);
                Thread.Sleep(200);
            }
            Thread.Sleep(300);

            // Step 8: Enter World
            Report("Entering world...");
            if (background)
                DiPressKey(loginWriter, pid, 0x0D);
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
            if (background && loginWriter != null)
            {
                loginWriter.Deactivate(pid);
                loginWriter.Close(pid);
                loginWriter.Dispose();
            }
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

    /// <summary>
    /// Bring window to foreground. This runs on a Task.Run background thread,
    /// so AttachThreadInput is not used (it requires the calling thread to own
    /// an input queue / message pump). SetForegroundWindow + BringWindowToTop
    /// is sufficient when paired with ShowWindow(SW_RESTORE).
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.BringWindowToTop(hwnd);
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
    private static void DiSetKey(KeyInputWriter? writer, int pid, ushort vk, bool pressed)
    {
        uint scan = NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);
        if (scan > 0 && scan < 256)
            writer?.SetKey(pid, (byte)scan, pressed);
    }

    /// <summary>
    /// Press and release a single key via DirectInput shared memory.
    /// </summary>
    private static void DiPressKey(KeyInputWriter? writer, int pid, ushort vk)
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
    private static void DiTypeString(KeyInputWriter? writer, int pid, string text)
    {
        foreach (char c in text)
        {
            short vkScan = NativeMethods.VkKeyScanW(c);
            if (vkScan == -1) continue; // unmappable character

            ushort vk = (ushort)(vkScan & 0xFF);
            bool needShift = (vkScan & 0x100) != 0;

            if (needShift) DiSetKey(writer, pid, 0x10, true); // VK_SHIFT down
            DiSetKey(writer, pid, vk, true);
            Thread.Sleep(80);
            DiSetKey(writer, pid, vk, false);
            if (needShift) DiSetKey(writer, pid, 0x10, false); // VK_SHIFT up
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
            FileLogger.Warn("AutoLogin: dinput8.dll in use — EQ restart needed to update");
            return true; // file exists, may be the right version — proceed
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

    // ─── SendInput Helpers (Legacy Mode) ─────────────────────────────

    /// <summary>
    /// Send a single INPUT event, checking the return value.
    /// Returns false if the input was blocked (UIPI — target runs at higher integrity).
    /// </summary>
    private static bool SendInputChecked(NativeMethods.INPUT input)
    {
        uint sent = NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent == 0)
        {
            var err = Marshal.GetLastWin32Error();
            FileLogger.Warn($"AutoLogin: SendInput failed (error={err}) — is EQ running as administrator?");
            return false;
        }
        return true;
    }

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

            if (!SendInputChecked(down)) return;
            Thread.Sleep(50);
            SendInputChecked(up);
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
        if (!SendKeyDown(vk, scan))
            FileLogger.Warn($"AutoLogin: SendKeyDown failed for VK 0x{vk:X2}");
        Thread.Sleep(50);
        if (!SendKeyUp(vk, scan))
            FileLogger.Warn($"AutoLogin: SendKeyUp failed for VK 0x{vk:X2}");
        Thread.Sleep(50);
    }

    private static bool SendKeyDown(ushort vk, ushort scan)
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
        return SendInputChecked(input);
    }

    private static bool SendKeyUp(ushort vk, ushort scan)
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
        return SendInputChecked(input);
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
