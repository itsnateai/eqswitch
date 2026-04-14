param(
  [int]$Pid_ = 25052,
  [string]$ScanStartHex = "0x10000000",
  [string]$ScanEndHex   = "0x20000000"
)
$Start = [Convert]::ToInt64($ScanStartHex, 16)
$End   = [Convert]::ToInt64($ScanEndHex, 16)

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
public struct MBI {
  public IntPtr BaseAddress; public IntPtr AllocationBase; public uint AllocationProtect; public ushort PartitionId;
  public IntPtr RegionSize; public uint State; public uint Protect; public uint Type;
}
public static class P {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool b, int c);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, [Out] byte[] b, IntPtr s, out IntPtr r);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int s);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  public static List<KeyValuePair<long,int>> Scan(IntPtr h, long start, long end, int[] pat, int[] strides) {
    var hits = new List<KeyValuePair<long,int>>();
    long addr = start;
    MBI mbi;
    int mbiSize = Marshal.SizeOf(typeof(MBI));
    while (addr < end) {
      int ok = VirtualQueryEx(h, new IntPtr(addr), out mbi, mbiSize);
      if (ok == 0) break;
      long b = mbi.BaseAddress.ToInt64();
      long sz = mbi.RegionSize.ToInt64();
      if (mbi.State == 0x1000 && (mbi.Protect & 0xEE) != 0 && (mbi.Protect & 0x101) == 0) {
        long off = 0;
        while (off < sz) {
          int chunkSz = (int)Math.Min(0x100000L, sz - off);
          byte[] buf = new byte[chunkSz];
          IntPtr read;
          if (ReadProcessMemory(h, new IntPtr(b + off), buf, new IntPtr(chunkSz), out read) && read.ToInt32() > 64) {
            int rsz = read.ToInt32();
            foreach (int stride in strides) {
              int span = stride * (pat.Length - 1) + 4;
              int maxStart = rsz - span;
              if (maxStart < 0) continue;
              for (int p = 0; p <= maxStart; p += 4) {
                bool m = true;
                for (int k = 0; k < pat.Length; k++) {
                  int v = BitConverter.ToInt32(buf, p + k * stride);
                  if (v != pat[k]) { m = false; break; }
                }
                if (m) hits.Add(new KeyValuePair<long,int>(b + off + p, stride));
              }
            }
          }
          off += chunkSz;
        }
      }
      addr = b + sz;
      if (addr <= b) break;
    }
    return hits;
  }
}
"@

$h = [P]::OpenProcess(0x0410, $false, $Pid_)
if ($h -eq [IntPtr]::Zero) { Write-Host "OpenProcess failed"; exit 1 }

# Level pattern: Acpots=1, Backup=1, Healpots=1, Jonopua=1, Nate=1, Potiongirl=1, Potionguy=1, Staxue=4, Thazguard=1, Zfree=1
$lvlPat = [int[]]@(1,1,1,1,1,1,1,4,1,1)
$strides = [int[]]@(4, 8, 12, 16, 20, 24, 32, 48, 64, 96, 128, 256, 0x160, 0x300)

Write-Host ("Scanning 0x{0:X} - 0x{1:X} for level pattern (1,1,1,1,1,1,1,4,1,1)..." -f $Start, $End)
$hits = [P]::Scan($h, $Start, $End, $lvlPat, $strides)
Write-Host ("Found {0} matches" -f $hits.Count)

foreach ($kv in $hits) {
  Write-Host ""
  "MATCH at 0x{0:X8} stride=0x{1:X}" -f $kv.Key, $kv.Value | Write-Host
  $ctx = New-Object byte[] 0x80
  $r = [IntPtr]::Zero
  [void][P]::ReadProcessMemory($h, [IntPtr]::new($kv.Key - 0x20), $ctx, [IntPtr]::new(0x80), [ref]$r)
  for ($off = 0; $off -lt 0x80; $off += 4) {
    $v = [BitConverter]::ToInt32($ctx, $off)
    $rel = $off - 0x20
    if ($v -ne 0) {
      $marker = ""
      if ($rel -eq 0) { $marker = " <-- start of match" }
      "  [{0,5}] = {1,12} (0x{2:X8}){3}" -f $rel, $v, $v, $marker | Write-Host
    }
  }
}

# Now class pattern (need to know Thazguard's class first — but we know first 7 + slot 9)
# Acpots=9 Backup=2 Healpots=2 Jonopua=2 Nate=2 Potiongirl=9 Potionguy=9 Staxue=4 Thazguard=? Zfree=5
# Skip Thazguard with -1 (don't match) — just match 9 slots we know
# Actually since BitConverter.ToInt32 will read fine, use a strict match and skip Thazguard offset
# Easier: use a 9-element pattern with stride * 9, skipping Thazguard
# But that breaks contiguous-stride assumption. Try matching slots 0-7 + slot 9 separately.

Write-Host ""
Write-Host "=== Now scanning for class pattern: 9,2,2,2,2,9,9,4 (first 8) ==="
$clsPat8 = [int[]]@(9,2,2,2,2,9,9,4)
$hits2 = [P]::Scan($h, $Start, $End, $clsPat8, $strides)
Write-Host ("Found {0} matches" -f $hits2.Count)
foreach ($kv in $hits2 | Select-Object -First 10) {
  Write-Host ""
  "MATCH at 0x{0:X8} stride=0x{1:X}" -f $kv.Key, $kv.Value | Write-Host
  # Read 10 elements at this stride to see slot 8 (Thazguard) and slot 9 (Zfree)
  $tot = $kv.Value * 10 + 4
  $ctx = New-Object byte[] $tot
  $r = [IntPtr]::Zero
  [void][P]::ReadProcessMemory($h, [IntPtr]::new($kv.Key), $ctx, [IntPtr]::new($tot), [ref]$r)
  for ($k = 0; $k -lt 10; $k++) {
    $v = [BitConverter]::ToInt32($ctx, $k * $kv.Value)
    "  slot {0}: {1}" -f $k, $v | Write-Host
  }
}

[void][P]::CloseHandle($h)
