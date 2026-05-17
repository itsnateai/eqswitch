param([int]$TargetPid)
# Empirically verify charSelectPlayerArray offset on a live eqgame.exe (x86).
# MQ2 RoF2-emu canonical offset = 0x18EC0 per EverQuest.h:963.

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class P {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool b, int c);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, [Out] byte[] b, IntPtr s, out IntPtr r);
  [DllImport("psapi.dll", SetLastError=true)] public static extern bool EnumProcessModulesEx(IntPtr h, [Out] IntPtr[] m, uint cb, out uint needed, uint flag);
  [DllImport("psapi.dll", SetLastError=true)] public static extern uint GetModuleBaseNameW(IntPtr h, IntPtr m, [Out] StringBuilder name, uint size);
  [DllImport("psapi.dll", SetLastError=true)] public static extern bool GetModuleInformation(IntPtr h, IntPtr m, out MODINFO mi, uint cb);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct MODINFO { public IntPtr Base; public uint Size; public IntPtr EntryPoint; }
}
"@ -ErrorAction SilentlyContinue

$h = [P]::OpenProcess(0x0410, $false, $TargetPid)
if ($h -eq [IntPtr]::Zero) { Write-Host "OpenProcess FAILED for PID $TargetPid"; exit 1 }

function Read-Mem([int64]$a, [int]$n) {
  $buf = New-Object byte[] $n
  $r = [IntPtr]::Zero
  $ok = [P]::ReadProcessMemory($h, [IntPtr]::new($a), $buf, [IntPtr]::new($n), [ref]$r)
  if (-not $ok) { return $null }
  return $buf
}

# Find dinput8.dll module (Dalaya MQ2 hosts ppEverQuest export there)
$mods = New-Object IntPtr[] 512
$needed = 0
[void][P]::EnumProcessModulesEx($h, $mods, [uint32](512 * [IntPtr]::Size), [ref]$needed, 3)
$count = [int]($needed / [IntPtr]::Size)
$dinput8Base = [IntPtr]::Zero
$di8Name = ""
for ($i = 0; $i -lt $count -and $i -lt 512; $i++) {
  $sb = New-Object System.Text.StringBuilder 260
  [void][P]::GetModuleBaseNameW($h, $mods[$i], $sb, 260)
  $name = $sb.ToString().ToLower()
  if ($name -eq "dinput8.dll") { $dinput8Base = $mods[$i]; $di8Name = $sb.ToString(); break }
}
Write-Host ("dinput8.dll base = 0x{0:X8}" -f $dinput8Base.ToInt64())
if ($dinput8Base -eq [IntPtr]::Zero) { Write-Host "dinput8.dll NOT loaded"; exit 1 }

# Parse exports for ppEverQuest
$pe = Read-Mem $dinput8Base.ToInt64() 0x1000
$e_lfanew = [BitConverter]::ToInt32($pe, 0x3C)
$magic = [BitConverter]::ToUInt16($pe, $e_lfanew + 0x18)
$ddOff = if ($magic -eq 0x10B) { $e_lfanew + 0x18 + 96 } else { $e_lfanew + 0x18 + 112 }
$expRVA = [BitConverter]::ToUInt32($pe, $ddOff)
$expHdr = Read-Mem ($dinput8Base.ToInt64() + $expRVA) 40
$numNames    = [BitConverter]::ToUInt32($expHdr, 24)
$addrFuncs   = [BitConverter]::ToUInt32($expHdr, 28)
$addrNames   = [BitConverter]::ToUInt32($expHdr, 32)
$addrOrdinals= [BitConverter]::ToUInt32($expHdr, 36)
Write-Host ("Export count = $numNames")

$ppEverQuestVA = 0
for ($i = 0; $i -lt $numNames; $i++) {
  $nameRVA = [BitConverter]::ToUInt32((Read-Mem ($dinput8Base.ToInt64() + $addrNames + $i*4) 4), 0)
  $nb = Read-Mem ($dinput8Base.ToInt64() + $nameRVA) 64
  if ($null -eq $nb) { continue }
  $ns = [System.Text.Encoding]::ASCII.GetString($nb)
  $z = $ns.IndexOf([char]0)
  if ($z -gt 0) { $ns = $ns.Substring(0, $z) }
  if ($ns -eq "ppEverQuest") {
    $ord = [BitConverter]::ToUInt16((Read-Mem ($dinput8Base.ToInt64() + $addrOrdinals + $i*2) 2), 0)
    $funcRVA = [BitConverter]::ToUInt32((Read-Mem ($dinput8Base.ToInt64() + $addrFuncs + $ord*4) 4), 0)
    $ppEverQuestVA = $dinput8Base.ToInt64() + $funcRVA
    Write-Host ("ppEverQuest export VA = 0x{0:X8} (RVA 0x{1:X})" -f $ppEverQuestVA, $funcRVA)
    break
  }
}
if ($ppEverQuestVA -eq 0) { Write-Host "ppEverQuest export NOT found in dinput8.dll"; exit 1 }

# Read CEverQuest pointer
$pEQbuf = Read-Mem $ppEverQuestVA 4
$pEQ = [BitConverter]::ToUInt32($pEQbuf, 0)
Write-Host ("pEverQuest = 0x{0:X8}" -f $pEQ)
if ($pEQ -eq 0) { Write-Host "pEverQuest is NULL (not at charselect yet)"; exit 0 }

function Try-Offset([uint32]$off) {
  $arrBuf = Read-Mem ($pEQ + $off) 16
  if ($null -eq $arrBuf) { return $null }
  $cnt = [BitConverter]::ToInt32($arrBuf, 0)
  $data = [BitConverter]::ToUInt32($arrBuf, 4)
  $name = ""
  if ($cnt -ge 1 -and $cnt -le 20 -and $data -ne 0) {
    $nameBuf = Read-Mem $data 32
    if ($null -ne $nameBuf) {
      for ($k = 0; $k -lt 32; $k++) { if ($nameBuf[$k] -eq 0) { break }; if ($nameBuf[$k] -lt 32 -or $nameBuf[$k] -gt 126) { break }; $name += [char]$nameBuf[$k] }
    }
  }
  return [PSCustomObject]@{ Off=$off; Count=$cnt; Data=$data; Name1=$name }
}

Write-Host ""
Write-Host "=== MQ2 canonical offset 0x18EC0 ==="
$canon = Try-Offset 0x18EC0
$canon | Format-List

Write-Host ""
Write-Host "=== Scanning 0..0x20000 step 4 for ArrayClass(CharSelectInfo) ==="
$hitList = New-Object System.Collections.ArrayList
for ($o = 0; $o -le 0x20000; $o += 4) {
  $r = Try-Offset ([uint32]$o)
  if ($null -ne $r -and $r.Count -ge 1 -and $r.Count -le 10 -and $r.Name1.Length -ge 3 -and $r.Name1[0] -ge 'A' -and $r.Name1[0] -le 'Z') {
    $allAlpha = $true
    foreach ($c in $r.Name1.ToCharArray()) { if (-not (($c -ge 'A' -and $c -le 'Z') -or ($c -ge 'a' -and $c -le 'z'))) { $allAlpha = $false; break } }
    if ($allAlpha) {
      [void]$hitList.Add($r)
      Write-Host ("  HIT: offset=0x{0:X5} count={1} data=0x{2:X8} name1='{3}'" -f $r.Off, $r.Count, $r.Data, $r.Name1)
    }
  }
}
Write-Host ("Total matches: " + $hitList.Count)
[P]::CloseHandle($h) | Out-Null
