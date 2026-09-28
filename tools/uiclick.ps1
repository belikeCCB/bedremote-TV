# Drive the real buttons of a sandboxed bedremote GUI (own folder, own bedremote.json copy,
# own port) so his live setup is untouched. Pure ASCII source: PowerShell 5.1 reads .ps1 as
# codepage 936 on this machine, so Chinese captions come in as code points.
#
#   -What blank|lock|take   which button to exercise
#   -Pid2   <pid>           which instance to drive (default: the only one running)
param(
  [string]$What = 'blank',
  [int]$Pid2 = 0,
  [string]$Box = "$env:LOCALAPPDATA\br-gui-test"
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

function U([string[]]$hex) { $s = ''; foreach ($h in $hex) { $s += [char][Convert]::ToInt32($h, 16) }; return $s }
$cap = @{}
$cap['blank'] = U @('65AD','8FD9','5757','4FE1','53F7')                                # duan zhe kuai xin hao = blank this one
$cap['lock']  = U @('9501','5C4F','65F6','62D2','7EDD','624B','673A','8F93','5165')     # suo ping shi ju jue shou ji shu ru = lock-guard checkbox
$cap['take']  = U @('63A5','7BA1','7AEF','53E3')                                        # jie guan duan kou = take over the port (prefix match)
$cap['mark']  = U @('70B9','540D')                                                        # dian ming = add to the pass list
$cap['unmark'] = U @('53BB','6389','70B9','540D')                                        # qu dian dian ming = remove from the pass list
$cap['issue'] = U @('529E','8BC1')                                                        # ban zheng = issue the passes
$cap['https'] = U @('624B','673A','8D70')                                                # shou ji zou = prefix of the HTTPS checkbox
$report = Join-Path $root 'uiclick.txt'
if (Test-Path $report) { Remove-Item $report -Force }
$lines = New-Object System.Collections.ArrayList
function Say([string]$s) { [void]$lines.Add($s); Write-Host $s }

$src = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public static class Ui
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, StringBuilder l);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    delegate bool ChildProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, ChildProc cb, IntPtr l);
    delegate bool TopProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(TopProc cb, IntPtr l);
    static TopProc keep;                       // hold the delegate: a GC would break the callback

    static List<IntPtr> Kids(IntPtr parent, string classFilter)
    {
        var list = new List<IntPtr>();
        keep = null;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
        {
            var cn = new StringBuilder(256);
            GetClassName(h, cn, 256);
            if (classFilter == null || cn.ToString().IndexOf(classFilter, StringComparison.OrdinalIgnoreCase) >= 0) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

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

    static string Text(IntPtr h)
    {
        var sb = new StringBuilder(1024);
        GetWindowTextW(h, sb, 1024);
        return sb.ToString();
    }

    public static string Buttons(IntPtr top)
    {
        var s = new StringBuilder();
        foreach (var h in Kids(top, "Button"))
            if (IsWindowVisible(h)) s.Append('[').Append(Text(h)).Append("] ");
        return s.ToString();
    }

    public static string Click(IntPtr top, string want)
    {
        foreach (var h in Kids(top, "Button"))
        {
            if (!IsWindowVisible(h)) continue;
            string t = Text(h);
            if (t == want || (t.Length > want.Length && t.StartsWith(want)))
            {
                SendMessageW(h, 0x00F5, IntPtr.Zero, IntPtr.Zero);       // BM_CLICK
                return "BM_CLICK " + t;
            }
        }
        return "NOT FOUND " + want;
    }

    public static string CheckState(IntPtr top, string want)
    {
        foreach (var h in Kids(top, "Button"))
            if (Text(h) == want) return (SendMessageW(h, 0x00F0, IntPtr.Zero, IntPtr.Zero).ToInt32() != 0) ? "checked" : "unchecked";
        return "NOT FOUND " + want;
    }

    public static string LogText(IntPtr top)
    {
        foreach (var h in Kids(top, "Edit"))
        {
            var sb = new StringBuilder(65536);
            SendMessageW(h, 0x000D, (IntPtr)65535, sb);       // WM_GETTEXT
            if (sb.Length > 20) return sb.ToString();
        }
        return "";
    }
}
'@
Add-Type -TypeDefinition $src

if ($Pid2 -eq 0)
{
    $names = @(Get-Process -Name 'bedremote-gui' -ErrorAction SilentlyContinue | ForEach-Object { [int]$_.Id })
    if ($names.Count -ne 1) { Write-Host ('expected 1 bedremote-gui process, got ' + $names.Count + ' (' + ($names -join ',') + ')'); exit 2 }
    $Pid2 = $names[0]
}
$top = [Ui]::TopWindow($Pid2)
Say ('PID ' + $Pid2 + ' TOP ' + $top)
Say ('VISIBLE BUTTONS ' + [Ui]::Buttons($top))
Say ('--- ' + $What + ' ---')
Say ([Ui]::Click($top, $cap[$What]))
Start-Sleep -Milliseconds 2200
Say ('LOGTAIL ' + (([Ui]::LogText($top) -split "`r?`n" | Select-Object -Last 5) -join ' | '))

if ($What -eq 'lock')
{
    $cfg = Join-Path $Box 'bedremote.json'
    $before = (Get-Content $cfg -Raw)
    [void][Ui]::Click($top, $cap['lock']); Start-Sleep -Milliseconds 900
    $mid = (Get-Content $cfg -Raw)
    [void][Ui]::Click($top, $cap['lock']); Start-Sleep -Milliseconds 900
    $after = (Get-Content $cfg -Raw)
    Say ('json len before/mid/after = ' + $before.Length + '/' + $mid.Length + '/' + $after.Length)
    Say ('back to original: ' + ($before -eq $after))
    Say ('mid flipped: ' + ($mid -ne $before))
    Say ('keepAwake intact: ' + ($after -match 'keepAwake'))
    Say ('panels intact: ' + ($after -match '"panels"'))
}
$lines | Out-File -FilePath $report -Encoding UTF8
Write-Host ('REPORT ' + $report)
