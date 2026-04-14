param(
  [int]$Pid_ = 25052,
  [string]$ScanStartHex = "0x10000000",
  [string]$ScanEndHex   = "0x20000000"
)
$Start = [Convert]::ToInt64($ScanStartHex, 16)
$End   = [Convert]::ToInt64($ScanEndHex, 16)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
public struct MBI { public IntPtr BaseAddress; public IntPtr AllocationBase; public uint AllocationProtect; public ushort PartitionId; public IntPtr RegionSize; public uint State; public uint Protect; public uint Type; }
public static class P {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool b, int c);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, [Out] byte[] b, IntPtr s, out IntPtr r);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int s);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
"@
$h = [P]::OpenProcess(0x0410, $false, $Pid_)
if ($h -eq [IntPtr]::Zero) { Write-Host "OpenProcess failed"; exit 1 }

# Pattern: int32[10] = {1,1,1,1,1,1,1,4,1,1} — current levels (Staxue=4)
# Or class pattern: int32[10] = {9,2,2,2,2,9,9,4,?,5} (Thazguard unknown, but we know first 9)
$lvlPattern = @(1,1,1,1,1,1,1,4,1,1)
# Allow flexible spacing — search at strides 4, 8, 12, 16, 32, 64 between elements
$strides = @(4, 8, 12, 16, 24, 32, 48, 64, 96, 128, 160, 256, 0x160, 0x300)

$matches_ = New-Object 'System.Collections.Generic.List[object]'

$addr = $Start
$mbi = New-Object MBI
$totalScanned = 0
while ($addr -lt $End) {
  $sz = [P]::VirtualQueryEx($h, [IntPtr]::new($addr), [ref]$mbi, [System.Runtime.InteropServices.Marshal]::SizeOf([type][MBI]))
  if ($sz -eq 0) { break }
  $base = [int64]$mbi.BaseAddress
  $regSize = [int64]$mbi.RegionSize
  if ($mbi.State -eq 0x1000 -and ($mbi.Protect -band 0xEE) -ne 0 -and ($mbi.Protect -band 0x101) -eq 0) {
    # MEM_COMMIT, readable, not noaccess/guard
    $chunk = [Math]::Min($regSize, 0x100000)  # cap chunk at 1MB to limit memory
    $offsetInRegion = 0
    while ($offsetInRegion -lt $regSize) {
      $thisChunk = [Math]::Min($chunk, $regSize - $offsetInRegion)
      $buf = New-Object byte[] $thisChunk
      $r = [IntPtr]::Zero
      $ok = [P]::ReadProcessMemory($h, [IntPtr]::new($base + $offsetInRegion), $buf, [IntPtr]::new($thisChunk), [ref]$r)
      $totalScanned += $r.ToInt64()
      if ($ok -and $r.ToInt32() -gt 64) {
        # Search this chunk for the pattern at each stride
        foreach ($stride in $strides) {
          $maxStart = $r.ToInt32() - ($stride * 9) - 4
          if ($maxStart -lt 0) { continue }
          for ($p = 0; $p -le $maxStart; $p += 4) {
            $matched = $true
            for ($k = 0; $k -lt $lvlPattern.Length; $k++) {
              $val = [BitConverter]::ToInt32($buf, $p + $k * $stride)
              if ($val -ne $lvlPattern[$k]) { $matched = $false; break }
            }
            if ($matched) {
              $hitAddr = $base + $offsetInRegion + $p
              $matches_.Add([PSCustomObject]@{ Addr = $hitAddr; Stride = $stride })
              "MATCH at 0x{0:X8} stride=0x{1:X}" -f $hitAddr, $stride | Write-Host
            }
          }
        }
      }
      $offsetInRegion += $thisChunk
    }
  }
  $addr = $base + $regSize
  if ($addr -le $base) { break }
}

Write-Host ""
Write-Host ("Scanned ~{0:N0} bytes, found {1} matches for level pattern" -f $totalScanned, $matches_.Count)

# For each match, dump 16 bytes around it for context
foreach ($m in $matches_) {
  Write-Host ""
  Write-Host ("--- Match at 0x{0:X8} stride=0x{1:X} ---" -f $m.Addr, $m.Stride)
  # Read and show 0x80 before and after
  $contextBuf = New-Object byte[] 0x100
  $r = [IntPtr]::Zero
  [void][P]::ReadProcessMemory($h, [IntPtr]::new($m.Addr - 0x40), $contextBuf, [IntPtr]::new(0x100), [ref]$r)
  for ($off = 0; $off -lt 0x100; $off += 4) {
    $v = [BitConverter]::ToInt32($contextBuf, $off)
    $rel = $off - 0x40
    $marker = ""
    if ($rel -eq 0) { $marker = " <-- match" }
    if ($v -ne 0 -or $rel -ge 0) {
      "  [{0,+4}] = {1,12} (0x{2:X8}){3}" -f $rel, $v, $v, $marker | Write-Host
    }
  }
}

[void][P]::CloseHandle($h)
