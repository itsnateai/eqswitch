using System.Runtime.InteropServices;
using System.Text;

namespace EQSwitch.Core;

/// <summary>
/// Injects/ejects a native DLL into a target process using CreateRemoteThread + LoadLibraryA.
/// Handles cross-architecture injection (64-bit host → 32-bit WoW64 target) by finding
/// the 32-bit kernel32.dll base address in the target process and parsing the PE export
/// table to resolve LoadLibraryA's address.
/// </summary>
public static class DllInjector
{
    /// <summary>
    /// Inject a DLL into the target process.
    /// </summary>
    public static bool Inject(int pid, string dllPath)
    {
        if (!File.Exists(dllPath))
        {
            FileLogger.Error($"DllInjector: DLL not found: {dllPath}");
            return false;
        }

        dllPath = Path.GetFullPath(dllPath);
        var dllBytes = Encoding.ASCII.GetBytes(dllPath + '\0');

        var hProcess = IntPtr.Zero;
        var allocAddr = IntPtr.Zero;
        var hThread = IntPtr.Zero;

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

            if (!NativeMethods.WriteProcessMemory(hProcess, allocAddr, dllBytes, (uint)dllBytes.Length, out _))
            {
                FileLogger.Error($"DllInjector: WriteProcessMemory failed, error={Marshal.GetLastWin32Error()}");
                return false;
            }

            // Resolve LoadLibraryA address — must handle cross-architecture
            var loadLibAddr = ResolveLoadLibraryA(hProcess, pid);
            if (loadLibAddr == IntPtr.Zero)
            {
                FileLogger.Error("DllInjector: failed to resolve LoadLibraryA in target process");
                return false;
            }

            // Create a remote thread that calls LoadLibraryA(dllPath)
            hThread = NativeMethods.CreateRemoteThread(
                hProcess, IntPtr.Zero, 0, loadLibAddr, allocAddr, 0, out _);
            if (hThread == IntPtr.Zero)
            {
                FileLogger.Error($"DllInjector: CreateRemoteThread failed, error={Marshal.GetLastWin32Error()}");
                return false;
            }

            var waitResult = NativeMethods.WaitForSingleObject(hThread, 5000);

            // Check if LoadLibraryA returned non-null (DLL loaded successfully)
            NativeMethods.GetExitCodeThread(hThread, out uint exitCode);

            if (waitResult != NativeMethods.WAIT_OBJECT_0)
            {
                FileLogger.Error($"DllInjector: remote thread didn't complete in time, result={waitResult}");
                return false;
            }

            if (exitCode == 0)
            {
                FileLogger.Error($"DllInjector: LoadLibraryA returned NULL in PID {pid} — DLL failed to load");
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
            if (hThread != IntPtr.Zero)
                NativeMethods.CloseHandle(hThread);
            if (allocAddr != IntPtr.Zero && hProcess != IntPtr.Zero)
                NativeMethods.VirtualFreeEx(hProcess, allocAddr, 0, NativeMethods.MEM_RELEASE);
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Resolve LoadLibraryA's address in the target process.
    /// For same-architecture, uses GetProcAddress directly.
    /// For cross-architecture (64→32), finds 32-bit kernel32 base in the target
    /// and parses the PE export table from disk to compute the function address.
    /// </summary>
    private static IntPtr ResolveLoadLibraryA(IntPtr hProcess, int pid)
    {
        // Check if target is WoW64 (32-bit process on 64-bit OS)
        bool targetIsWow64 = false;
        NativeMethods.IsWow64Process(hProcess, out targetIsWow64);

        bool weAreWow64 = !Environment.Is64BitProcess;

        // Same architecture — simple path
        if (targetIsWow64 == weAreWow64)
        {
            var kernel32 = NativeMethods.GetModuleHandleA("kernel32.dll");
            if (kernel32 == IntPtr.Zero) return IntPtr.Zero;
            return NativeMethods.GetProcAddress(kernel32, "LoadLibraryA");
        }

        // Cross-architecture: we're 64-bit, target is 32-bit WoW64
        // Find 32-bit kernel32.dll base address in the target process
        FileLogger.Info($"DllInjector: cross-arch injection (64→32) for PID {pid}");

        var kernel32Base = FindModule32InProcess(hProcess, "kernel32.dll");
        if (kernel32Base == IntPtr.Zero)
        {
            FileLogger.Error("DllInjector: couldn't find 32-bit kernel32.dll in target");
            return IntPtr.Zero;
        }

        // Parse the 32-bit kernel32.dll from SysWOW64 to find LoadLibraryA's RVA
        string kernel32Path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "SysWOW64", "kernel32.dll");

        if (!File.Exists(kernel32Path))
        {
            FileLogger.Error($"DllInjector: SysWOW64 kernel32 not found at {kernel32Path}");
            return IntPtr.Zero;
        }

        uint rva = GetExportRva(kernel32Path, "LoadLibraryA");
        if (rva == 0)
        {
            FileLogger.Error("DllInjector: LoadLibraryA not found in kernel32 exports");
            return IntPtr.Zero;
        }

        var result = new IntPtr(kernel32Base.ToInt64() + rva);
        FileLogger.Info($"DllInjector: resolved LoadLibraryA at 0x{result.ToInt64():X} (base=0x{kernel32Base.ToInt64():X} + RVA=0x{rva:X})");
        return result;
    }

    /// <summary>
    /// Find a 32-bit module's base address in a WoW64 process using EnumProcessModulesEx.
    /// </summary>
    private static IntPtr FindModule32InProcess(IntPtr hProcess, string moduleName)
    {
        const int maxModules = 1024;
        var modules = new IntPtr[maxModules];

        if (!NativeMethods.EnumProcessModulesEx(
            hProcess, modules, maxModules * IntPtr.Size, out int cbNeeded,
            NativeMethods.LIST_MODULES_32BIT))
        {
            FileLogger.Error($"DllInjector: EnumProcessModulesEx failed, error={Marshal.GetLastWin32Error()}");
            return IntPtr.Zero;
        }

        int count = cbNeeded / IntPtr.Size;
        var sb = new StringBuilder(260);

        for (int i = 0; i < count; i++)
        {
            sb.Clear();
            NativeMethods.GetModuleFileNameExW(hProcess, modules[i], sb, sb.Capacity);
            var name = Path.GetFileName(sb.ToString());
            if (name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
            {
                FileLogger.Info($"DllInjector: found {moduleName} at 0x{modules[i].ToInt64():X}");
                return modules[i];
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Parse a PE file's export table to find an exported function's RVA.
    /// Works on both 32-bit (PE32) and 64-bit (PE32+) binaries.
    /// </summary>
    private static uint GetExportRva(string pePath, string functionName)
    {
        try
        {
            var peData = File.ReadAllBytes(pePath);

            // DOS header → PE offset at 0x3C
            int peOffset = BitConverter.ToInt32(peData, 0x3C);

            // PE signature (4 bytes) + COFF header (20 bytes) = optional header at peOffset + 24
            ushort magic = BitConverter.ToUInt16(peData, peOffset + 24);
            bool isPE32 = magic == 0x10B;

            // Export directory RVA is at different offsets for PE32 vs PE32+
            int exportDirOffset = isPE32 ? peOffset + 24 + 96 : peOffset + 24 + 112;
            uint exportRva = BitConverter.ToUInt32(peData, exportDirOffset);
            uint exportSize = BitConverter.ToUInt32(peData, exportDirOffset + 4);

            if (exportRva == 0) return 0;

            // Convert RVA to file offset by walking section headers
            int fileOffset = RvaToFileOffset(peData, peOffset, exportRva);
            if (fileOffset < 0) return 0;

            // Parse export directory
            uint numberOfNames = BitConverter.ToUInt32(peData, fileOffset + 24);
            uint addressOfFunctions = BitConverter.ToUInt32(peData, fileOffset + 28);
            uint addressOfNames = BitConverter.ToUInt32(peData, fileOffset + 32);
            uint addressOfOrdinals = BitConverter.ToUInt32(peData, fileOffset + 36);

            int namesFileOff = RvaToFileOffset(peData, peOffset, addressOfNames);
            int ordinalsFileOff = RvaToFileOffset(peData, peOffset, addressOfOrdinals);
            int functionsFileOff = RvaToFileOffset(peData, peOffset, addressOfFunctions);

            if (namesFileOff < 0 || ordinalsFileOff < 0 || functionsFileOff < 0) return 0;

            for (uint i = 0; i < numberOfNames; i++)
            {
                uint nameRva = BitConverter.ToUInt32(peData, namesFileOff + (int)(i * 4));
                int nameFileOff = RvaToFileOffset(peData, peOffset, nameRva);

                // Read null-terminated ASCII string
                int end = nameFileOff;
                while (end < peData.Length && peData[end] != 0) end++;
                string name = Encoding.ASCII.GetString(peData, nameFileOff, end - nameFileOff);

                if (name == functionName)
                {
                    ushort ordinal = BitConverter.ToUInt16(peData, ordinalsFileOff + (int)(i * 2));
                    uint funcRva = BitConverter.ToUInt32(peData, functionsFileOff + ordinal * 4);
                    return funcRva;
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error($"DllInjector: PE parse error for {pePath}", ex);
        }

        return 0;
    }

    /// <summary>
    /// Convert an RVA to a file offset by walking PE section headers.
    /// </summary>
    private static int RvaToFileOffset(byte[] peData, int peOffset, uint rva)
    {
        // Number of sections is at peOffset + 6
        ushort numSections = BitConverter.ToUInt16(peData, peOffset + 6);
        ushort optHeaderSize = BitConverter.ToUInt16(peData, peOffset + 20);
        int sectionStart = peOffset + 24 + optHeaderSize;

        for (int i = 0; i < numSections; i++)
        {
            int secOff = sectionStart + i * 40;
            uint virtualAddress = BitConverter.ToUInt32(peData, secOff + 12);
            uint rawSize = BitConverter.ToUInt32(peData, secOff + 16);
            uint rawOffset = BitConverter.ToUInt32(peData, secOff + 20);
            uint virtualSize = BitConverter.ToUInt32(peData, secOff + 8);

            if (rva >= virtualAddress && rva < virtualAddress + Math.Max(rawSize, virtualSize))
            {
                return (int)(rva - virtualAddress + rawOffset);
            }
        }

        return -1;
    }

    /// <summary>
    /// Eject a previously injected DLL from the target process by calling FreeLibrary remotely.
    /// </summary>
    public static bool Eject(int pid, string dllName)
    {
        var hProcess = IntPtr.Zero;
        var hThread = IntPtr.Zero;

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

            // Find the DLL module in the target process
            var dllBase = FindModule32InProcess(hProcess, dllName);
            if (dllBase == IntPtr.Zero)
            {
                FileLogger.Info($"DllInjector.Eject: DLL not found in process (already unloaded?)");
                return true;
            }

            // Resolve FreeLibrary in the target process
            bool targetIsWow64 = false;
            NativeMethods.IsWow64Process(hProcess, out targetIsWow64);

            IntPtr freeLibrary;
            if (!targetIsWow64 || !Environment.Is64BitProcess)
            {
                var kernel32 = NativeMethods.GetModuleHandleA("kernel32.dll");
                freeLibrary = NativeMethods.GetProcAddress(kernel32, "FreeLibrary");
            }
            else
            {
                var kernel32Base = FindModule32InProcess(hProcess, "kernel32.dll");
                string kernel32Path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "SysWOW64", "kernel32.dll");
                uint rva = GetExportRva(kernel32Path, "FreeLibrary");
                freeLibrary = rva > 0 ? new IntPtr(kernel32Base.ToInt64() + rva) : IntPtr.Zero;
            }

            if (freeLibrary == IntPtr.Zero)
            {
                FileLogger.Error("DllInjector.Eject: couldn't resolve FreeLibrary");
                return false;
            }

            hThread = NativeMethods.CreateRemoteThread(
                hProcess, IntPtr.Zero, 0, freeLibrary, dllBase, 0, out _);
            if (hThread == IntPtr.Zero) return false;

            NativeMethods.WaitForSingleObject(hThread, 5000);

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
            if (hThread != IntPtr.Zero)
                NativeMethods.CloseHandle(hThread);
            if (hProcess != IntPtr.Zero)
                NativeMethods.CloseHandle(hProcess);
        }
    }
}
