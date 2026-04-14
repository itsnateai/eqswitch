param(
  [int]$Pid_ = 25052,
  [string]$AddrHex = "0x112D7798",
  [int]$Stride = 0x160,
  [int]$Slots = 10,
  [string]$OutDir = "X:/_Projects/EQSwitch/Native/dumps"
)
$Addr = [Convert]::ToInt64($AddrHex, 16)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

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
if ($h -eq [IntPtr]::Zero) { Write-Host "OpenProcess failed"; exit 1 }

# Read all 10 slots into separate buffers (avoid the array-aliasing bug from earlier script)
$charBufs = New-Object 'System.Collections.Generic.List[object]'
for ($s = 0; $s -lt $Slots; $s++) {
  $buf = New-Object byte[] $Stride
  $r = [IntPtr]::Zero
  $entryAddr = $Addr + [int64]($s * $Stride)
  $ok = [P]::ReadProcessMemory($h, [IntPtr]::new($entryAddr), $buf, [IntPtr]::new($Stride), [ref]$r)
  $name = ""
  for ($k = 0; $k -lt 32; $k++) { if ($buf[$k] -eq 0) { break }; $name += [char]$buf[$k] }
  $charBufs.Add([PSCustomObject]@{ Idx = $s; Name = $name; Buf = $buf; Read = $r.ToInt32() })
  # Save raw bytes per slot
  [System.IO.File]::WriteAllBytes("$OutDir/slot${s}_${name}.bin", $buf)
}

Write-Host "=== Read summary ==="
foreach ($sl in $charBufs) { "  slot {0}: {1,-12} read={2}" -f $sl.Idx, $sl.Name, $sl.Read | Write-Host }

# Per-offset comparison across slots — for every 4-byte int32 in the struct
$txt = "$OutDir/all_slots_int32.txt"
"OFFSET    " + (($charBufs | ForEach-Object { "{0,-12}" -f $_.Name }) -join '') | Out-File $txt -Encoding ASCII
for ($off = 0; $off -lt $Stride; $off += 4) {
  $row = "+0x{0:X3}    " -f $off
  foreach ($sl in $charBufs) {
    $v = [BitConverter]::ToInt32($sl.Buf, $off)
    $row += "{0,-12}" -f $v
  }
  Add-Content $txt $row
}

# Strings (≥4 printable ASCII) per slot
$strFile = "$OutDir/all_slots_strings.txt"
"" | Out-File $strFile -Encoding ASCII
foreach ($sl in $charBufs) {
  Add-Content $strFile ""
  Add-Content $strFile ("=== {0} (slot {1}) ===" -f $sl.Name, $sl.Idx)
  $cur = ""
  $startOff = -1
  for ($i = 0; $i -lt $Stride; $i++) {
    $b = $sl.Buf[$i]
    if ($b -ge 0x20 -and $b -lt 0x7F) {
      if ($startOff -lt 0) { $startOff = $i }
      $cur += [char]$b
    } else {
      if ($cur.Length -ge 4) { Add-Content $strFile ("  +0x{0:X3}  '{1}'" -f $startOff, $cur) }
      $cur = ""
      $startOff = -1
    }
  }
  if ($cur.Length -ge 4) { Add-Content $strFile ("  +0x{0:X3}  '{1}'" -f $startOff, $cur) }
}

# Hex dump of every slot
$hexFile = "$OutDir/all_slots_hex.txt"
"" | Out-File $hexFile -Encoding ASCII
foreach ($sl in $charBufs) {
  Add-Content $hexFile ""
  Add-Content $hexFile ("=== {0} (slot {1}) ===" -f $sl.Name, $sl.Idx)
  for ($i = 0; $i -lt $Stride; $i += 16) {
    $hex = ""
    $asc = ""
    for ($j = 0; $j -lt 16; $j++) {
      $b = $sl.Buf[$i+$j]
      $hex += "{0:X2} " -f $b
      if ($b -ge 0x20 -and $b -lt 0x7F) { $asc += [char]$b } else { $asc += "." }
    }
    Add-Content $hexFile ("  +0x{0:X3}  {1} {2}" -f $i, $hex, $asc)
  }
}

# Find candidate race / deity / etc — fields that vary across slots and stay small
$cand = "$OutDir/varying_small_fields.txt"
"OFFSET    values per slot (range, count distinct)" | Out-File $cand -Encoding ASCII
"          " + (($charBufs | ForEach-Object { "{0,-10}" -f $_.Name.Substring(0,[Math]::Min(10,$_.Name.Length)) }) -join '') | Add-Content $cand
for ($off = 0; $off -lt $Stride; $off += 4) {
  $vals = @()
  foreach ($sl in $charBufs) { $vals += [BitConverter]::ToInt32($sl.Buf, $off) }
  $allSmall = ($vals | Where-Object { $_ -lt 0 -or $_ -gt 1000 }).Count -eq 0
  $distinct = ($vals | Sort-Object -Unique).Count
  if ($allSmall -and $distinct -ge 2) {
    $line = "+0x{0:X3}    " -f $off
    foreach ($v in $vals) { $line += "{0,-10}" -f $v }
    $line += "  (distinct={0})" -f $distinct
    Add-Content $cand $line
  }
}

# Pointer-shaped fields (look like 0x77xxxxxx or 0x10xxxxxx — userspace addrs)
$ptr = "$OutDir/pointer_fields.txt"
"OFFSET    values per slot" | Out-File $ptr -Encoding ASCII
"          " + (($charBufs | ForEach-Object { "{0,-12}" -f $_.Name.Substring(0,[Math]::Min(12,$_.Name.Length)) }) -join '') | Add-Content $ptr
for ($off = 0; $off -lt $Stride; $off += 4) {
  $vals = @()
  $allPtr = $true
  foreach ($sl in $charBufs) {
    $u = [BitConverter]::ToUInt32($sl.Buf, $off)
    $vals += $u
    if ($u -lt 0x00100000 -or $u -ge 0x80000000) { $allPtr = $false }
  }
  if ($allPtr) {
    $line = "+0x{0:X3}    " -f $off
    foreach ($v in $vals) { $line += "0x{0:X8}  " -f $v }
    Add-Content $ptr $line
  }
}

[void][P]::CloseHandle($h)
Write-Host ""
Write-Host "=== Files written to $OutDir ==="
Get-ChildItem $OutDir | ForEach-Object { "  {0}  ({1} bytes)" -f $_.Name, $_.Length | Write-Host }
