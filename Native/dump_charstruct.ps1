param(
  [int]$Pid_ = 25052,
  [string]$AddrHex = "0x112D7798",
  [int]$Stride = 0x160,
  [int]$Slots = 10
)

$Addr = [int64]([Convert]::ToInt64($AddrHex, 16))

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class P {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool b, int c);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, [Out] byte[] b, IntPtr s, out IntPtr r);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
"@

# PROCESS_QUERY_INFORMATION (0x0400) | PROCESS_VM_READ (0x0010)
$h = [P]::OpenProcess(0x0410, $false, $Pid_)
if ($h -eq [IntPtr]::Zero) { Write-Host ("OpenProcess failed: {0}" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error()); exit 1 }
Write-Host ("Opened PID {0} handle={1}" -f $Pid_, $h)

function Read-Mem([int64]$a, [int]$n) {
  $buf = New-Object byte[] $n
  $read = [IntPtr]::Zero
  $ok = [P]::ReadProcessMemory($h, [IntPtr]::new($a), $buf, [IntPtr]::new($n), [ref]$read)
  if (-not $ok) { Write-Host ("RPM failed at 0x{0:X} : err {1}" -f $a, [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
  return ,$buf
}

Write-Host ("=== SLOT 0 RAW @ 0x{0:X} ===" -f $Addr)
$buf0 = Read-Mem $Addr $Stride
$nameBytes = $buf0[0..15]
$nameStr = -join ($nameBytes | ForEach-Object { if ($_ -ge 0x20 -and $_ -lt 0x7F) { [char]$_ } else { '.' } })
Write-Host ("name area: {0}" -f $nameStr)

for ($off = 0; $off -lt $Stride; $off += 4) {
  $i32 = [BitConverter]::ToInt32($buf0, $off)
  $u32 = [BitConverter]::ToUInt32($buf0, $off)
  $hex = "{0:X8}" -f $u32
  $asc = -join ($buf0[$off..($off+3)] | ForEach-Object { if ($_ -ge 0x20 -and $_ -lt 0x7F) { [char]$_ } else { '.' } })
  if ($i32 -ne 0 -or $off -lt 0x80) {
    "  +0x{0:X3}  hex={1}  i32={2,12}  asc='{3}'" -f $off, $hex, $i32, $asc | Write-Host
  }
}

Write-Host ""
Write-Host "=== ALL SLOTS at 0x40-0x60 ==="
"{0,-12} {1,8} {2,8} {3,8} {4,8} {5,8} {6,8} {7,8} {8,8}" -f "name", "+0x40", "+0x44(cls)", "+0x48", "+0x4C", "+0x50(lvl)", "+0x54", "+0x58", "+0x5C" | Write-Host
for ($s = 0; $s -lt $Slots; $s++) {
  $entryAddr = $Addr + ($s * $Stride)
  $eb = Read-Mem $entryAddr 0x60
  $name = ""
  for ($k = 0; $k -lt 32; $k++) { if ($eb[$k] -eq 0) { break }; $name += [char]$eb[$k] }
  $vals = @()
  foreach ($o in @(0x40, 0x44, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C)) {
    $vals += [BitConverter]::ToInt32($eb, $o)
  }
  "{0,-12} {1,8} {2,8} {3,8} {4,8} {5,8} {6,8} {7,8} {8,8}" -f $name, $vals[0], $vals[1], $vals[2], $vals[3], $vals[4], $vals[5], $vals[6], $vals[7] | Write-Host
}

[void][P]::CloseHandle($h)
