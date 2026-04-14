param(
  [int]$Pid_ = 25052,
  [string]$AddrHex = "0x112D7798",
  [int]$Stride = 0x160,
  [int]$Slots = 10
)
$Addr = [Convert]::ToInt64($AddrHex, 16)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class P {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool b, int c);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, [Out] byte[] b, IntPtr s, out IntPtr r);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
"@
$h = [P]::OpenProcess(0x0410, $false, $Pid_)

# Read all slots, look for fields that vary by slot but are small ints (race candidates)
$slotData = @()
for ($s = 0; $s -lt $Slots; $s++) {
  $buf = New-Object byte[] $Stride
  $r = [IntPtr]::Zero
  [void][P]::ReadProcessMemory($h, [IntPtr]::new($Addr + $s * $Stride), $buf, [IntPtr]::new($Stride), [ref]$r)
  $name = ""
  for ($k = 0; $k -lt 32; $k++) { if ($buf[$k] -eq 0) { break }; $name += [char]$buf[$k] }
  $slotData += [PSCustomObject]@{ Name = $name; Buf = $buf }
}

Write-Host "=== Looking for fields where values are small (1-500) and DIFFER between slots ==="
Write-Host "Names: $(($slotData | ForEach-Object { $_.Name }) -join ', ')"
Write-Host ""
Write-Host ("{0,-8}  values per slot (Acpots Backup Healpots Jonopua Nate Potiongirl Potionguy Staxue Thazguard Zfree)" -f "offset")
for ($off = 0x40; $off -lt $Stride; $off += 4) {
  $vals = @()
  $allSmall = $true
  $allSame = $true
  $first = $null
  foreach ($sd in $slotData) {
    $v = [BitConverter]::ToInt32($sd.Buf, $off)
    $vals += $v
    if ($v -lt 0 -or $v -gt 1000) { $allSmall = $false }
    if ($null -eq $first) { $first = $v } elseif ($v -ne $first) { $allSame = $false }
  }
  if ($allSmall -and -not $allSame) {
    "+0x{0:X3}  {1}" -f $off, (($vals | ForEach-Object { "{0,4}" -f $_ }) -join ' ') | Write-Host
  }
}

Write-Host ""
Write-Host "=== Also dumping +0x80 to +0x130 ALL slots, all int32 ==="
foreach ($sd in $slotData) {
  Write-Host ("--- {0} ---" -f $sd.Name)
  for ($off = 0x80; $off -lt 0x130; $off += 4) {
    $v = [BitConverter]::ToInt32($sd.Buf, $off)
    if ($v -ne 0) {
      "  +0x{0:X3} = {1,12} (0x{2:X8})" -f $off, $v, $v | Write-Host
    }
  }
}

[void][P]::CloseHandle($h)
