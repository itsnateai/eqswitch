using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Launches an EQ client and auto-types credentials via SendInput.
/// Runs the wait/type sequence on a background thread, marshals
/// SendInput calls back to the foreground (SendInput requires the
/// calling thread to not be blocked by the target window).
/// </summary>
public class AutoLoginManager
{
    private readonly AppConfig _config;

    public event EventHandler<string>? StatusUpdate;

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
            FileLogger.Info($"AutoLogin: launched PID {pid} for {account.Name}");
        }
        catch (Exception ex)
        {
            FileLogger.Error("AutoLogin: launch failed", ex);
            StatusUpdate?.Invoke(this, $"Error: {ex.Message}");
            return;
        }

        // Run the login sequence on a background thread
        var loginAccount = account;
        Task.Run(() => RunLoginSequence(pid, loginAccount, password));
    }

    private void RunLoginSequence(int pid, LoginAccount account, string password)
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

            // Step 2: Bring to foreground and wait for login screen
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            ForceForeground(hwnd);
            Report("Waiting for login screen...");
            Thread.Sleep(2000);

            // Step 3: Type username (if /login flag not used)
            if (!account.UseLoginFlag && !string.IsNullOrEmpty(account.Username))
            {
                ForceForeground(hwnd);
                Thread.Sleep(200);
                SendKey(0x09); // Tab to username field
                Thread.Sleep(100);
                TypeString(account.Username);
                Thread.Sleep(100);
            }

            // Step 4: Tab to password field and type password
            ForceForeground(hwnd);
            SendKey(0x09); // Tab to password field
            Thread.Sleep(100);
            TypeString(password);
            Thread.Sleep(100);

            // Step 5: Press Enter to submit login
            Report("Submitting login...");
            SendKey(0x0D); // Enter
            Thread.Sleep(3000);

            // Step 6: Server select — press Enter to confirm pre-selected server
            Report("Confirming server...");
            ForceForeground(hwnd);
            SendKey(0x0D); // Enter
            Thread.Sleep(3000);

            // Step 7: Character select — navigate to character slot
            Report($"Selecting character (slot {account.CharacterSlot})...");
            ForceForeground(hwnd);

            // Navigate down to the correct character slot (1-based)
            for (int i = 1; i < account.CharacterSlot; i++)
            {
                SendKey(0x28); // VK_DOWN
                Thread.Sleep(200);
            }
            Thread.Sleep(300);

            // Step 8: Enter World
            Report("Entering world...");
            SendKey(0x0D); // Enter
            Thread.Sleep(1000);

            Report($"{account.Name} logged in!");
            FileLogger.Info($"AutoLogin: {account.Name} login sequence complete (PID {pid})");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"AutoLogin: sequence failed for {account.Name}", ex);
            Report($"Error: {ex.Message}");
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

    // ─── SendInput Helpers ──────────────────────────────────────────

    /// <summary>
    /// Type a string character by character using VK codes + scan codes.
    /// EQ uses DirectInput which reads scan codes, so we send both.
    /// </summary>
    private static void TypeString(string text)
    {
        foreach (char c in text)
        {
            short vkResult = NativeMethods.VkKeyScanW(c);
            if (vkResult == -1)
            {
                FileLogger.Warn($"AutoLogin: no VK mapping for char '{c}'");
                continue;
            }

            byte vk = (byte)(vkResult & 0xFF);
            bool needsShift = ((vkResult >> 8) & 0x01) != 0;
            ushort scan = (ushort)NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);

            if (needsShift) SendKeyDown(0x10, 0x2A); // VK_SHIFT, DIK_LSHIFT

            SendKeyDown(vk, scan);
            Thread.Sleep(50);
            SendKeyUp(vk, scan);

            if (needsShift) SendKeyUp(0x10, 0x2A);

            Thread.Sleep(50);
        }
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
