using System.Runtime.InteropServices;
using System.Text;

namespace EQSwitch.Core;

/// <summary>
/// Injects/ejects a native DLL into a target process using CreateRemoteThread + LoadLibraryA.
/// Used to inject eqswitch-hook.dll into eqgame.exe for SetWindowPos/MoveWindow hooking.
/// </summary>
public static class DllInjector
{
    /// <summary>
    /// Inject a DLL into the target process.
    /// </summary>
    /// <param name="pid">Target process ID (eqgame.exe)</param>
    /// <param name="dllPath">Full path to the DLL to inject</param>
    /// <returns>True if injection succeeded</returns>
    public static bool Inject(int pid, string dllPath)
    {
        if (!File.Exists(dllPath))
        {
            FileLogger.Error($"DllInjector: DLL not found: {dllPath}");
            return false;
        }

        // Use full path for LoadLibraryA
        dllPath = Path.GetFullPath(dllPath);
        var dllBytes = Encoding.ASCII.GetBytes(dllPath + '\0');

        var hProcess = IntPtr.Zero;
        var allocAddr = IntPtr.Zero;

        try
        {
            // Open the target process with required permissions
            uint access = NativeMethods.PROCESS_CREATE_THREAD |
                          NativeMethods.PROCESS_VM_OPERATION |
                          NativeMethods.PROCESS_VM_WRITE |
                          NativeMethods.PROCESS_VM_READ |
                          NativeMethods.PROCESS_QUERY_INFORMATION;
            hProcess = NativeMethods.OpenProcess(access, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                FileLogger.Error($"DllInjector: OpenProcess failed for PID {pid}, error={Marshal.GetLastWin32Error()}");
                return false;
            }

            // Allocate memory in the target process for the DLL path string
            allocAddr = NativeMethods.VirtualAllocEx(
                hProcess, IntPtr.Zero, (uint)dllBytes.Length,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);
            if (allocAddr == IntPtr.Zero)
            {
                FileLogger.Error($"DllInjector: VirtualAllocEx failed, error={Marshal.GetLastWin32Error()}");
                return false;
            }

            // Write the DLL path into the allocated memory
            if (!NativeMethods.WriteProcessMemory(hProcess, allocAddr, dllBytes, (uint)dllBytes.Length, out _))
            {
                FileLogger.Error($"DllInjector: WriteProcessMemory failed, error={Marshal.GetLastWin32Error()}");
                return false;
            }

            // Get address of LoadLibraryA in kernel32.dll
            // This works cross-architecture because kernel32 is loaded at the same address
            // in both WoW64 (32-bit) and native (64-bit) processes on the same system.
            var kernel32 = NativeMethods.GetModuleHandleA("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
            {
                FileLogger.Error("DllInjector: GetModuleHandle(kernel32) failed");
                return false;
            }

            var loadLibAddr = NativeMethods.GetProcAddress(kernel32, "LoadLibraryA");
            if (loadLibAddr == IntPtr.Zero)
            {
                FileLogger.Error("DllInjector: GetProcAddress(LoadLibraryA) failed");
                return false;
            }

            // Create a remote thread in the target process that calls LoadLibraryA(dllPath)
            var hThread = NativeMethods.CreateRemoteThread(
                hProcess, IntPtr.Zero, 0, loadLibAddr, allocAddr, 0, out _);
            if (hThread == IntPtr.Zero)
            {
                FileLogger.Error($"DllInjector: CreateRemoteThread failed, error={Marshal.GetLastWin32Error()}");
                return false;
            }

            // Wait for the remote thread to complete (LoadLibraryA returns)
            var waitResult = NativeMethods.WaitForSingleObject(hThread, 5000);
            NativeMethods.CloseHandle(hThread);

            if (waitResult != NativeMethods.WAIT_OBJECT_0)
            {
                FileLogger.Error($"DllInjector: remote thread didn't complete in time, result={waitResult}");
                return false;
            }

            FileLogger.Info($"DllInjector: successfully injected into PID {pid}: {dllPath}");
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error("DllInjector: injection failed", ex);
            return false;
        }
        finally
        {
            // Free the allocated memory (the DLL path string — the DLL itself stays loaded)
            if (allocAddr != IntPtr.Zero && hProcess != IntPtr.Zero)
                NativeMethods.VirtualFreeEx(hProcess, allocAddr, 0, NativeMethods.MEM_RELEASE);
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Eject a previously injected DLL from the target process by calling FreeLibrary remotely.
    /// </summary>
    /// <param name="pid">Target process ID</param>
    /// <param name="dllName">DLL filename (e.g., "eqswitch-hook.dll")</param>
    /// <returns>True if ejection succeeded</returns>
    public static bool Eject(int pid, string dllName)
    {
        // We need to find the DLL's module handle inside the target process.
        // Since we can't call GetModuleHandle in a 32-bit process from 64-bit,
        // we use CreateRemoteThread + GetModuleHandleA to get it, then FreeLibrary.
        var hProcess = IntPtr.Zero;
        var allocAddr = IntPtr.Zero;

        try
        {
            uint access = NativeMethods.PROCESS_CREATE_THREAD |
                          NativeMethods.PROCESS_VM_OPERATION |
                          NativeMethods.PROCESS_VM_WRITE |
                          NativeMethods.PROCESS_VM_READ |
                          NativeMethods.PROCESS_QUERY_INFORMATION;
            hProcess = NativeMethods.OpenProcess(access, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                FileLogger.Warn($"DllInjector.Eject: OpenProcess failed for PID {pid} (process may have exited)");
                return false;
            }

            var nameBytes = Encoding.ASCII.GetBytes(dllName + '\0');

            // Allocate memory for the DLL name
            allocAddr = NativeMethods.VirtualAllocEx(
                hProcess, IntPtr.Zero, (uint)nameBytes.Length,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);
            if (allocAddr == IntPtr.Zero) return false;

            if (!NativeMethods.WriteProcessMemory(hProcess, allocAddr, nameBytes, (uint)nameBytes.Length, out _))
                return false;

            // Call GetModuleHandleA in the target process to get the DLL's HMODULE
            var kernel32 = NativeMethods.GetModuleHandleA("kernel32.dll");
            var getModAddr = NativeMethods.GetProcAddress(kernel32, "GetModuleHandleA");

            var hThread = NativeMethods.CreateRemoteThread(
                hProcess, IntPtr.Zero, 0, getModAddr, allocAddr, 0, out _);
            if (hThread == IntPtr.Zero) return false;

            NativeMethods.WaitForSingleObject(hThread, 5000);
            NativeMethods.GetExitCodeThread(hThread, out uint moduleHandle);
            NativeMethods.CloseHandle(hThread);

            if (moduleHandle == 0)
            {
                FileLogger.Info($"DllInjector.Eject: DLL not found in process (already unloaded?)");
                return true; // Not an error — DLL already gone
            }

            // Free the name string memory
            NativeMethods.VirtualFreeEx(hProcess, allocAddr, 0, NativeMethods.MEM_RELEASE);
            allocAddr = IntPtr.Zero;

            // Call FreeLibrary in the target process
            var freeLibAddr = NativeMethods.GetProcAddress(kernel32, "FreeLibrary");
            var hThread2 = NativeMethods.CreateRemoteThread(
                hProcess, IntPtr.Zero, 0, freeLibAddr, (IntPtr)moduleHandle, 0, out _);
            if (hThread2 == IntPtr.Zero) return false;

            NativeMethods.WaitForSingleObject(hThread2, 5000);
            NativeMethods.CloseHandle(hThread2);

            FileLogger.Info($"DllInjector.Eject: successfully ejected from PID {pid}");
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Error("DllInjector.Eject: ejection failed", ex);
            return false;
        }
        finally
        {
            if (allocAddr != IntPtr.Zero && hProcess != IntPtr.Zero)
                NativeMethods.VirtualFreeEx(hProcess, allocAddr, 0, NativeMethods.MEM_RELEASE);
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }
}
