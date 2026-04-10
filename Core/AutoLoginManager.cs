using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EQSwitch.Config;
using EQSwitch.Models;

namespace EQSwitch.Core;

/// <summary>
/// Launches EQ and auto-logins via in-process UI widget manipulation.
/// Writes credentials to LoginShm, the DLL's LoginStateMachine drives
/// EQ's login flow using CXWnd::SetWindowText and WndNotification.
/// No focus-faking, no PostMessage, no key injection needed.
/// </summary>
public class AutoLoginManager
{
    private readonly AppConfig _config;

    /// <summary>PIDs currently in the login sequence -- DLL injection should be deferred for these.</summary>
    private readonly ConcurrentDictionary<int, byte> _activeLoginPids = new();

    /// <summary>UI thread sync context -- captured at construction for marshaling events back to the UI thread.</summary>
    private readonly SynchronizationContext? _syncContext;

    /// <summary>Callback to enforce eqclient.ini overrides before launch.</summary>
    private readonly Action<AppConfig>? _enforceOverrides;

    public event EventHandler<string>? StatusUpdate;
    public event EventHandler<int>? LoginStarting;
    public event EventHandler<int>? LoginComplete;

    public bool IsLoginActive(int pid) => _activeLoginPids.ContainsKey(pid);

    /// <summary>
    /// Callback invoked after process creation but before ResumeThread.
    /// Use for DLL injection into the suspended process.
    /// </summary>
    public Action<SuspendedProcess>? PreResumeCallback { get; set; }

    public AutoLoginManager(AppConfig config, Action<AppConfig>? enforceOverrides = null)
    {
        _config = config;
        _syncContext = SynchronizationContext.Current;
        _enforceOverrides = enforceOverrides;
    }

    /// <summary>
    /// Launch EQ and auto-login with the given account.
    /// Non-blocking -- runs the login sequence on a background thread.
    /// </summary>
    public Task LoginAccount(LoginAccount account)
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
            return Task.CompletedTask;
        }

        WriteServerToIni(account.Server);

        var exePath = Path.Combine(_config.EQPath, _config.Launch.ExeName);
        if (!File.Exists(exePath))
        {
            FileLogger.Error($"AutoLogin: exe not found at {exePath}");
            StatusUpdate?.Invoke(this, "Error: eqgame.exe not found");
            return Task.CompletedTask;
        }

        var args = _config.Launch.Arguments;
        if (account.UseLoginFlag && !string.IsNullOrEmpty(account.Username))
            args += $" /login:{account.Username}";

        StatusUpdate?.Invoke(this, $"Launching {account.Name}...");

        int pid = -1;
        try
        {
            if (_enforceOverrides != null)
                _enforceOverrides(_config);
            else
                FileLogger.Warn("AutoLogin: no enforceOverrides callback registered");

            var commandLine = string.IsNullOrEmpty(args)
                ? new StringBuilder($"\"{exePath}\"")
                : new StringBuilder($"\"{exePath}\" {args}");

            var si = new NativeMethods.STARTUPINFOA
            {
                cb = Marshal.SizeOf<NativeMethods.STARTUPINFOA>()
            };

            bool created = NativeMethods.CreateProcessA(
                null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                NativeMethods.CREATE_SUSPENDED, IntPtr.Zero, _config.EQPath,
                ref si, out var pi);

            if (!created)
            {
                var error = Marshal.GetLastWin32Error();
                FileLogger.Error($"AutoLogin: CreateProcessA failed (error {error})");
                StatusUpdate?.Invoke(this, "Error: failed to start process");
                return Task.CompletedTask;
            }

            pid = pi.dwProcessId;
            _activeLoginPids.TryAdd(pid, 0);
            LoginStarting?.Invoke(this, pid);

            try
            {
                FileLogger.Info($"AutoLogin: created suspended PID {pid} for {account.Name}");

                uint resumeResult = NativeMethods.ResumeThread(pi.hThread);
                if (resumeResult == 0xFFFFFFFF)
                {
                    var err = Marshal.GetLastWin32Error();
                    FileLogger.Error($"AutoLogin: ResumeThread failed (error {err})");
                    NativeMethods.TerminateProcess(pi.hProcess, 1);
                    _activeLoginPids.TryRemove(pid, out byte _);
                    StatusUpdate?.Invoke(this, "Error: failed to resume process");
                    return Task.CompletedTask;
                }

                if (!DllInjector.WaitForLoader(pi.hProcess, pid, timeoutMs: 5000))
                    FileLogger.Warn($"AutoLogin: loader timeout for PID {pid} -- injecting anyway");

                try
                {
                    var sp = new SuspendedProcess(pid, pi.hProcess, pi.hThread);
                    if (PreResumeCallback != null)
                        PreResumeCallback.Invoke(sp);
                    else
                        FileLogger.Warn("AutoLogin: PreResumeCallback not set");
                }
                catch (Exception ex2)
                {
                    FileLogger.Warn($"AutoLogin: injection error: {ex2.Message}");
                }
            }
            finally
            {
                NativeMethods.CloseHandle(pi.hThread);
                NativeMethods.CloseHandle(pi.hProcess);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error("AutoLogin: launch failed", ex);
            if (pid > 0) _activeLoginPids.TryRemove(pid, out byte _);
            StatusUpdate?.Invoke(this, $"Error: {ex.Message}");
            return Task.CompletedTask;
        }

        var loginAccount = account;
        return Task.Run(() => RunLoginSequence(pid, loginAccount, password));
    }

    private void RunLoginSequence(int pid, LoginAccount account, string password)
    {
        var loginShm = new LoginShmWriter();
        var charSelect = new CharSelectReader();

        try
        {
            // Step 1: Wait for EQ window
            Report("Waiting for EQ window...");
            var hwnd = WaitForWindow(pid, TimeSpan.FromSeconds(30));
            if (hwnd == IntPtr.Zero)
            {
                Report("Timeout: EQ window did not appear");
                return;
            }

            // Step 2: Open LoginShm
            if (!loginShm.Open(pid))
            {
                Report("Error: failed to create login shared memory");
                return;
            }
            if (!charSelect.Open(pid))
                FileLogger.Warn($"AutoLogin: CharSelectReader SHM open failed for PID {pid}");

            // Step 3: Wait for DLL to initialize MQ2 bridge
            Report("Waiting for MQ2 bridge...");
            Thread.Sleep(_config.LoginScreenDelayMs);

            // Step 4: Write credentials and send LOGIN command
            Report("Sending credentials to in-process login...");
            loginShm.WriteCredentials(pid, account.Username, password, account.Server,
                account.CharacterName ?? "");
            loginShm.SendLoginCommand(pid);

            // Zero password from managed memory
            password = "";

            // Step 5: Monitor login phase until completion or error
            var sw = Stopwatch.StartNew();
            LoginPhase lastPhase = LoginPhase.Idle;
            const int maxWaitMs = 180000; // 3 minutes max

            while (sw.ElapsedMilliseconds < maxWaitMs)
            {
                var phase = loginShm.ReadPhase(pid);

                if (phase != lastPhase)
                {
                    Report(PhaseToMessage(phase, account.Name));
                    lastPhase = phase;
                }

                if (phase == LoginPhase.Complete)
                {
                    // Verify via window title
                    hwnd = RefreshHandle(pid, hwnd);
                    if (hwnd != IntPtr.Zero)
                    {
                        int titleLen = NativeMethods.GetWindowTextLength(hwnd);
                        var titleSb = new StringBuilder(titleLen + 1);
                        NativeMethods.GetWindowText(hwnd, titleSb, titleSb.Capacity);
                        string title = titleSb.ToString();
                        FileLogger.Info($"AutoLogin: complete, title='{title}'");
                    }
                    Report($"{account.Name} logged in!");
                    FileLogger.Info($"AutoLogin: {account.Name} login complete (PID {pid})");
                    break;
                }

                if (phase == LoginPhase.Error)
                {
                    var error = loginShm.ReadErrorMessage(pid);
                    Report($"{account.Name}: login failed -- {error}");
                    FileLogger.Error($"AutoLogin: {account.Name} login error: {error}");
                    break;
                }

                // Auto Enter World gate
                if (phase == LoginPhase.CharSelect && !_config.AutoEnterWorld)
                {
                    Report($"{account.Name} reached character select.");
                    loginShm.SendCancelCommand(pid);
                    break;
                }

                Thread.Sleep(500);
            }

            if (sw.ElapsedMilliseconds >= maxWaitMs && lastPhase != LoginPhase.Complete &&
                lastPhase != LoginPhase.Error)
            {
                Report($"{account.Name}: login timed out at phase {lastPhase}");
                FileLogger.Error($"AutoLogin: {account.Name} timed out at phase {lastPhase}");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"AutoLogin: sequence failed for {account.Name}", ex);
            Report($"Error: {ex.Message}");
        }
        finally
        {
            try { loginShm.ClearPassword(pid); } catch { }
            try { loginShm.Close(pid); } catch { }
            try { loginShm.Dispose(); } catch { }
            try { charSelect.Close(pid); } catch { }
            try { charSelect.Dispose(); } catch { }

            if (_syncContext != null)
            {
                _syncContext.Post(_ =>
                {
                    _activeLoginPids.TryRemove(pid, out byte _);
                    LoginComplete?.Invoke(this, pid);
                }, null);
            }
            else
            {
                _activeLoginPids.TryRemove(pid, out byte _);
                LoginComplete?.Invoke(this, pid);
            }
        }
    }

    // ─── Phase to human-readable message ────────────────────

    private static string PhaseToMessage(LoginPhase phase, string accountName)
    {
        return phase switch
        {
            LoginPhase.WaitLoginScreen => "Waiting for login screen...",
            LoginPhase.TypingCredentials => "Setting credentials...",
            LoginPhase.ClickingConnect => "Clicking Connect...",
            LoginPhase.WaitConnectResponse => "Authenticating...",
            LoginPhase.ServerSelect => "Selecting server...",
            LoginPhase.WaitServerLoad => "Loading character select...",
            LoginPhase.CharSelect => "Selecting character...",
            LoginPhase.EnteringWorld => "Entering world...",
            LoginPhase.Complete => $"{accountName} logged in!",
            LoginPhase.Error => $"{accountName}: login error",
            _ => $"Login phase {phase}..."
        };
    }

    private void Report(string message)
    {
        FileLogger.Info($"AutoLogin: {message}");
        if (_syncContext != null)
            _syncContext.Post(_ => StatusUpdate?.Invoke(this, message), null);
        else
            StatusUpdate?.Invoke(this, message);
    }

    /// <summary>Poll for an eqgame window belonging to the given PID.</summary>
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
                return IntPtr.Zero;
            }
            Thread.Sleep(500);
        }
        return IntPtr.Zero;
    }

    /// <summary>Re-resolve window handle for a PID.</summary>
    private static IntPtr RefreshHandle(int pid, IntPtr currentHwnd)
    {
        if (NativeMethods.IsWindow(currentHwnd)) return currentHwnd;

        try
        {
            using var proc = Process.GetProcessById(pid);
            var newHwnd = proc.MainWindowHandle;
            if (newHwnd != IntPtr.Zero && NativeMethods.IsWindow(newHwnd))
                return newHwnd;
        }
        catch (ArgumentException) { }

        return IntPtr.Zero;
    }

    // ─── EQ INI Helpers ──────────────────────────────────────────

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
