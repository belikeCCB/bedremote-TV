# Capture the bedremote GUI window to a PNG without touching his foreground window.
# Start the app first from the shell:  ./bedremote-gui.exe --gui --port=8791 &
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/shot.ps1 -Name bedremote-gui -Out shot.png
param(
  [string]$Name = 'bedremote-gui',
  [string]$Out  = 'shot.png',
  [string]$Pick = 'big',        # big = main window; small = the dialog on top of it; full = whole screen
  [int]$Screen  = -1,
  [int]$OnlyPid = 0             # only shoot this PID: browser processes share one image name, and without
                                # a pin this would happily capture whatever else the browser has open
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }

$src = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public static class Snap
{
    public static string Out;              // where to drop the PNG (set from Capture)
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, int flags);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    public static List<IntPtr> WindowsOf(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p == pid && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static RECT Rect(IntPtr h)
    {
        RECT r; GetWindowRect(h, out r);
        if (r.L < -10000)                  // launched from another session: the form is minimized, restore it first
        {
            ShowWindow(h, 9);
            System.Threading.Thread.Sleep(700);
            GetWindowRect(h, out r);
        }
        return r;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int which);
    public static string Pick = "big";

    public static string Capture(int pid, string path)
    {
        SetProcessDPIAware();
        Out = path;
        if (Pick == "full")
        {
            int fx = GetSystemMetrics(76), fy = GetSystemMetrics(77);      // SM_XVIRTUALSCREEN=76 / SM_YVIRTUALSCREEN=77
            int fw = GetSystemMetrics(78), fh = GetSystemMetrics(79);      // SM_CXVIRTUALSCREEN=78 / SM_CYVIRTUALSCREEN=79
            using (var fb = new Bitmap(fw, fh))
            {
                using (var fg = Graphics.FromImage(fb)) fg.CopyFromScreen(fx, fy, 0, 0, new Size(fw, fh));
                fb.Save(Out, ImageFormat.Png);
            }
            return "full " + fw + "x" + fh;
        }
        var hs = WindowsOf(pid);
        if (hs.Count == 0) return "NO WINDOW";
        IntPtr best = IntPtr.Zero; RECT br = new RECT();
        int ba = Pick == "small" ? int.MaxValue : 0;
        foreach (var h in hs)
        {
            RECT r = Rect(h);
            int a = (r.R - r.L) * (r.B - r.T);
            if (a < 40000) continue;
            if (Pick == "small" ? a < ba : a > ba) { ba = a; best = h; br = r; }
        }
        if (best == IntPtr.Zero) return "NO REAL WINDOW (candidates " + hs.Count + ")";
        int w = br.R - br.L, hh = br.B - br.T;
        using (var bmp = new Bitmap(w, hh))
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr dc = g.GetHdc();
            try { PrintWindow(best, dc, 2); }   // PW_RENDERFULLCONTENT: grabs the window content without stealing foreground
            finally { g.ReleaseHdc(dc); }
            bmp.Save(Out, ImageFormat.Png);
        }
        return w + "x" + hh;
    }
}
'@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing

$pids = @(Get-Process -Name $Name -ErrorAction SilentlyContinue | ForEach-Object { [int]$_.Id })
if ($OnlyPid -gt 0) { $pids = @($OnlyPid) }     # an explicit PID beats the process-name filter
if ($pids.Count -eq 0) { Write-Host ('NO PROCESS named ' + $Name); exit 2 }
foreach ($id in $pids) { [Snap]::Pick = $Pick; Write-Host ('PID ' + $id + ' -> ' + [Snap]::Capture($id, $Out)) }
if (Test-Path $Out) { Write-Host ('SAVED ' + ((Get-Item $Out).Length) + ' bytes -> ' + $Out) } else { Write-Host 'NOT SAVED' }
