# ASCII only. Dump every child control's text + class of a running bedremote GUI window as
# hex code points, so a caption that "isn't found" can be compared byte for byte.
param(
  [int]$Pid2 = 0,
  [string]$Name = 'bedremote-gui'
)
$ErrorActionPreference = 'Stop'
if ($Pid2 -eq 0) {
  $p = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
  if ($p.Count -ne 1) { Write-Host ('expected 1 ' + $Name + ', got ' + $p.Count); exit 2 }
  $Pid2 = [int]$p[0].Id
}
$src = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class Dump
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    delegate bool ChildProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, ChildProc cb, IntPtr l);
    delegate bool TopProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(TopProc cb, IntPtr l);
    static TopProc keep;
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static IntPtr TopWindow(int pid)
    {
        IntPtr best = IntPtr.Zero;
        keep = delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p == pid && IsWindowVisible(h)) best = h;
            return true;
        };
        EnumWindows(keep, IntPtr.Zero);
        return best;
    }
    public static string Rows(IntPtr top)
    {
        var s = new StringBuilder();
        EnumChildWindows(top, delegate(IntPtr h, IntPtr l)
        {
            var cn = new StringBuilder(256); GetClassName(h, cn, 256);
            var tx = new StringBuilder(1024); GetWindowTextW(h, tx, 1024);
            RECT r; GetWindowRect(h, out r);
            s.Append(IsWindowVisible(h) ? "V" : "-").Append(cn.ToString()).Append(' ')
             .Append(r.Left).Append(',').Append(r.Top).Append(',').Append(r.Right - r.Left).Append('x').Append(r.Bottom - r.Top)
             .Append(" |");
            foreach (char c in tx.ToString()) s.Append(' ').Append(((int)c).ToString("X4"));
            s.Append('|').Append('\n');
            return true;
        }, IntPtr.Zero);
        return s.ToString();
    }
}
'@
Add-Type -TypeDefinition $src
$top = [Dump]::TopWindow($Pid2)
Write-Host ('PID ' + $Pid2 + ' TOP ' + $top)
$out = Join-Path $env:TEMP 'brguidump.txt'
[Dump]::Rows($top) | Out-File -FilePath $out -Encoding UTF8
Write-Host ('WROTE ' + $out)
