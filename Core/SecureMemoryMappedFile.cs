// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 itsnateai

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace EQSwitch.Core;

/// <summary>
/// Creates memory-mapped files with DACLs restricting access to the current user only.
/// Prevents other processes in the same session from reading sensitive data (credentials, hook config).
/// Falls back to default (no DACL) if security descriptor creation fails — defense-in-depth, not blocking.
/// </summary>
internal static class SecureMemoryMappedFile
{
    /// <summary>
    /// Create or open a named memory-mapped file restricted to the current Windows user.
    /// Uses native CreateFileMappingW with an SDDL security descriptor, then re-opens
    /// via managed API while the native handle keeps the kernel object alive.
    /// </summary>
    public static MemoryMappedFile CreateOrOpen(string name, long capacity)
    {
        // Build SDDL: Protected DACL, allow full control only to current user
        var userSid = WindowsIdentity.GetCurrent().User;
        if (userSid == null)
        {
            FileLogger.Warn("SecureMemoryMappedFile: could not get current user SID, falling back to default ACL");
            return MemoryMappedFile.CreateOrOpen(name, capacity, MemoryMappedFileAccess.ReadWrite);
        }

        // D:P — Protected DACL (no inheritance from parent)
        // (A;;GA;;;{SID}) — Allow Generic All to current user only
        var sddl = $"D:P(A;;GA;;;{userSid.Value})";

        IntPtr sd = IntPtr.Zero;
        IntPtr nativeHandle = IntPtr.Zero;

        try
        {
            if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl, 1, out sd, out _))
            {
                FileLogger.Warn($"SecureMemoryMappedFile: SDDL conversion failed (error={Marshal.GetLastWin32Error()}), falling back to default ACL");
                return MemoryMappedFile.CreateOrOpen(name, capacity, MemoryMappedFileAccess.ReadWrite);
            }

            var sa = new NativeMethods.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = false
            };

            nativeHandle = NativeMethods.CreateFileMappingW(
                NativeMethods.INVALID_HANDLE_VALUE,
                ref sa,
                NativeMethods.PAGE_READWRITE,
                (uint)(capacity >> 32),
                (uint)(capacity & 0xFFFFFFFF),
                name);

            if (nativeHandle == IntPtr.Zero)
            {
                FileLogger.Warn($"SecureMemoryMappedFile: CreateFileMappingW failed (error={Marshal.GetLastWin32Error()}), falling back to default ACL");
                return MemoryMappedFile.CreateOrOpen(name, capacity, MemoryMappedFileAccess.ReadWrite);
            }

            // The named mapping now exists with our DACL. Open via managed API
            // while native handle keeps the kernel object alive, then close native handle.
            var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);

            // Managed handle now holds a reference — safe to close native handle
            NativeMethods.CloseHandle(nativeHandle);
            nativeHandle = IntPtr.Zero;

            return mmf;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"SecureMemoryMappedFile: failed ({ex.Message}), falling back to default ACL");
            return MemoryMappedFile.CreateOrOpen(name, capacity, MemoryMappedFileAccess.ReadWrite);
        }
        finally
        {
            if (nativeHandle != IntPtr.Zero)
                NativeMethods.CloseHandle(nativeHandle);
            if (sd != IntPtr.Zero)
                NativeMethods.LocalFree(sd);
        }
    }
}
