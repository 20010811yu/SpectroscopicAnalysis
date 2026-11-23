$src = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WinScan {
  public delegate bool EnumProc(IntPtr hh, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hh, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hh, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hh);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hh, out R r);
  public struct R { public int L, T, Rt, B; }
  public static List<string> Scan(UInt32 pid) {
    var r = new List<string>();
    EnumWindows((hh, l) => {
      uint p; GetWindowThreadProcessId(hh, out p);
      if (p == pid && IsWindowVisible(hh)) {
        var sb = new StringBuilder(128); GetWindowText(hh, sb, 128);
        R rect; GetWindowRect(hh, out rect);
        r.Add(hh.ToString() + "|" + sb.ToString() + "|" + rect.L + "," + rect.T + "," + rect.Rt + "," + rect.B);
      }
      return true;
    }, IntPtr.Zero);
    return r;
  }
}
"@
Add-Type -TypeDefinition $src
$p = Get-Process EzCad2 | Select-Object -First 1
foreach ($round in 1..3) {
  Write-Output ("--- 采样" + $round + " ---")
  [WinScan]::Scan([uint32]$p.Id) | ForEach-Object { Write-Output $_ }
  Start-Sleep -Seconds 2
}
