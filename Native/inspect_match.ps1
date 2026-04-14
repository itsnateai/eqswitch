param([int]$Pid_ = 25052)

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

$matches_ = @(
  @{ Addr = 0x10B1C934L; Stride = 0x20  },
  @{ Addr = 0x10B1C314L; Stride = 0x100 },
  @{ Addr = 0x19E8CE00L; Stride = 0x300 }
)

foreach ($m in $matches_) {
  $a = $m.Addr; $s = $m.Stride
  Write-Host ""
  Write-Host ("==== Match at 0x{0:X8} stride=0x{1:X} ====" -f $a, $s)
  for ($k = 0; $k -lt 10; $k++) {
    $rowAddr = $a + ($k * $s)
    $buf = New-Object byte[] 0x40
    $r = [IntPtr]::Zero
    [void][P]::ReadProcessMemory($h, [IntPtr]::new($rowAddr - 0x10), $buf, [IntPtr]::new(0x40), [ref]$r)
    Write-Host ("---- slot {0} @ 0x{1:X8} ----" -f $k, $rowAddr)
    for ($off = 0; $off -lt 0x40; $off += 4) {
      $v = [BitConverter]::ToInt32($buf, $off)
      $rel = $off - 0x10
      $marker = ""
      if ($rel -eq 0) { $marker = " <-- match" }
      if ($v -ne 0 -or $rel -eq 0) {
        "  [{0,4}] = {1,12} (0x{2:X8}){3}" -f $rel, $v, $v, $marker | Write-Host
      }
    }
  }
}

[void][P]::CloseHandle($h)
