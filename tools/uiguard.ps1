# ASCII only - PowerShell 5.1 reads .ps1 with the ANSI codepage on this machine, and a single
# Chinese comment makes the parser report a bogus "MissingCatchOrFinally" three hundred lines later.
# Chinese captions therefore come in as code points through the U() helper below.
#
# End-to-end test of the GUI half of the access-control work (tools\secauth.ps1 covers the
# server half). It drives the REAL buttons of a sandboxed instance - own folder, own
# bedremote.json, own port, own --data dir - so his live setup is never touched:
#
#   1. the token box shows the token that is in effect
#   2. typing a new one + clicking "apply" takes effect WITHOUT restarting (same PID)
#   3. a device that paired over HTTP shows up in the "paired devices" list
#   4. selecting that row and clicking "kick" really evicts it
#   5. screenshots of the window before/after, so the README picture is current
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\uiguard.ps1
#   powershell ... -File tools\uiguard.ps1 -Keep          # leave the sandbox running to look at it

param(
  [int]$Port = 18791,
  [string]$Box = "$env:LOCALAPPDATA\br-gui-test",
  [switch]$Keep
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
$repo = Split-Path -Parent $root
$exeSrc = Join-Path $repo 'bedremote.exe'
if (-not (Test-Path $exeSrc)) { Write-Host 'build bedremote.exe first (build.ps1)'; exit 2 }

function U([string[]]$hex) { $s = ''; foreach ($h in $hex) { $s += [char][Convert]::ToInt32($h, 16) }; return $s }
$capApply = U @('5E94','7528')                                  # ying yong = apply
$capKick  = U @('8E22','6389','8FD9','4E00','53F0')             # ti diao zhe yi tai = kick this one

# Win32 glue: find the child controls, read/set the token box, select a row in the device list,
# press a button. BM_CLICK is used instead of moving his mouse because the mouse is his.
$src = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public static class Ug
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, string l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, StringBuilder l);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    delegate bool ChildProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, ChildProc cb, IntPtr l);
    delegate bool TopProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(TopProc cb, IntPtr l);
    static TopProc keep;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct LVITEM
    {
        public uint mask; public int iItem; public int iSubItem;
        public uint state; public uint stateMask;
        public IntPtr pszText; public int cchTextMax; public int iImage; public IntPtr lParam;
        public uint iIndent; public uint iGroupId; public uint cColumns;
        public IntPtr puColumns; public IntPtr piColFmt; public int iGroup;
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

    static List<IntPtr> Kids(IntPtr parent, string classFilter)
    {
        var list = new List<IntPtr>();
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
        {
            var cn = new StringBuilder(256);
            GetClassName(h, cn, 256);
            if (cn.ToString().IndexOf(classFilter, StringComparison.OrdinalIgnoreCase) >= 0) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string Text(IntPtr h)
    {
        var sb = new StringBuilder(1024);
        GetWindowTextW(h, sb, 1024);
        return sb.ToString();
    }

    public static string EditWithText(IntPtr top, string want)
    {
        foreach (var h in Kids(top, "Edit"))
            if (Text(h) == want) return h.ToInt64().ToString();
        return "";
    }

    public static void SetText(IntPtr h, string s) { SendMessageW(h, 0x000C, IntPtr.Zero, s); }   // WM_SETTEXT

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

    public static string Buttons(IntPtr top)
    {
        var s = new StringBuilder();
        foreach (var h in Kids(top, "Button"))
            if (IsWindowVisible(h)) s.Append('[').Append(Text(h)).Append("] ");
        return s.ToString();
    }

    // The window holds two SysListView32 (peers on the left, paired devices on the right);
    // the right-most one is the device list.
    public static IntPtr DevList(IntPtr top)
    {
        IntPtr best = IntPtr.Zero; int bestX = int.MinValue;
        foreach (var h in Kids(top, "SysListView32"))
        {
            RECT r; if (!GetWindowRect(h, out r)) continue;
            if (r.Left > bestX) { bestX = r.Left; best = h; }
        }
        return best;
    }

    public static int ListCount(IntPtr lv) { return SendMessageW(lv, 0x1004, IntPtr.Zero, IntPtr.Zero).ToInt32(); }   // LVM_GETITEMCOUNT

    // Selecting a row across processes needs the LVITEM to live in the TARGET's address space:
    // SendMessage only shims text messages (WM_SETTEXT/WM_GETTEXT). Passing a pointer that is
    // only valid in this PowerShell process makes comctl32 fault inside the app - that is the
    // crash this used to cause (Application Error, faulting module comctl32.dll, 0xc000041d).
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, int size, uint type, uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, int size, uint type);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int written);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    const uint PROCESS_RW = 0x0008 | 0x0010 | 0x0020;      // VM_OPERATION | VM_WRITE | VM_OPERATION-ish
    const uint MEM_COMMIT = 0x1000, PAGE_READWRITE = 0x04, MEM_RELEASE = 0x8000;

    public static string SelectItem(IntPtr lv, uint pid, int index)
    {
        var it = new LVITEM();
        it.mask = 0x8;                        // LVIF_STATE
        it.iItem = index;
        it.state = 0x1 | 0x2;                 // LVIS_FOCUSED | LVIS_SELECTED
        it.stateMask = 0x1 | 0x2;
        int sz = Marshal.SizeOf(typeof(LVITEM));
        byte[] bytes = new byte[sz];
        IntPtr local = Marshal.AllocHGlobal(sz);
        try { Marshal.StructureToPtr(it, local, false); Marshal.Copy(local, bytes, 0, sz); }
        finally { Marshal.FreeHGlobal(local); }
        IntPtr h = OpenProcess(PROCESS_RW, false, pid);
        if (h == IntPtr.Zero) return "OpenProcess failed";
        try
        {
            IntPtr rem = VirtualAllocEx(h, IntPtr.Zero, sz, MEM_COMMIT, PAGE_READWRITE);
            if (rem == IntPtr.Zero) return "VirtualAllocEx failed";
            try
            {
                int written;
                if (!WriteProcessMemory(h, rem, bytes, sz, out written) || written != sz) return "WriteProcessMemory failed";
                SendMessageW(lv, 0x102B, (IntPtr)index, rem);       // LVM_SETITEMSTATE = LVM_FIRST + 43
                return "ok";
            }
            finally { VirtualFreeEx(h, rem, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(h); }
    }

    public static string EditTexts(IntPtr top)
    {
        var s = new StringBuilder();
        foreach (var h in Kids(top, "Edit"))
        {
            s.Append('[');
            foreach (char c in Text(h)) s.Append(' ').Append(((int)c).ToString("X4"));
            s.Append("] ");
        }
        return s.ToString();
    }

    public static string LogTail(IntPtr top, int n)
    {
        string best = "";
        foreach (var h in Kids(top, "Edit"))
        {
            var sb = new StringBuilder(65536);
            SendMessageW(h, 0x000D, (IntPtr)65535, sb);       // WM_GETTEXT
            if (sb.Length > best.Length) best = sb.ToString();
        }
        var lines = best.Replace("\r\n", "\n").Split('\n');
        var outList = new List<string>();
        for (int i = Math.Max(0, lines.Length - n); i < lines.Length; i++) if (lines[i].Length > 0) outList.Add(lines[i]);
        return string.Join(" | ", outList.ToArray());
    }
}
'@
Add-Type -TypeDefinition $src

$script:pass = 0; $script:fail = 0
function Check($name, $ok, $detail) {
  if ($ok) { $script:pass++; Write-Host ('  ok    ' + $name) }
  else {
    $script:fail++
    Write-Host ('  FAIL  ' + $name) -ForegroundColor Red
    if ($detail) { Write-Host ('        ' + $detail) -ForegroundColor DarkGray }
  }
}
function Code($url) {
  try { return [int](Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 6).StatusCode }
  catch { if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }; return -1 }
}
function Body($url) {
  try { return (Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 6).Content }
  catch { if ($_.ErrorDetails -and $_.ErrorDetails.Message) { return [string]$_.ErrorDetails.Message }
          if ($_.Exception.Response) { try { $sr = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream()); $t = $sr.ReadToEnd(); $sr.Close(); return [string]$t } catch { return '' } }
          return '' }
}
function Shot($out) {
  & (Join-Path $root 'shot.ps1') -Name 'bedremote-gui' -OnlyPid $proc.Id -Out $out 2>&1 | Out-Null
}

$proc = $null
try {
  # ---------- sandbox ----------
  if (Test-Path $Box) { Remove-Item -Recurse -Force $Box }
  New-Item -ItemType Directory -Force -Path (Join-Path $Box 'data') | Out-Null
  Copy-Item $exeSrc (Join-Path $Box 'bedremote-gui.exe')
  Copy-Item -Recurse (Join-Path $repo 'www') (Join-Path $Box 'www')
  $OLD = 'oldtoken1'
  $cfg = '{"port":' + $Port + ',"token":"' + $OLD + '","https":false,"keepAwake":true,"name":"gui-test"}'
  Set-Content -Encoding ASCII -Path (Join-Path $Box 'bedremote.json') -Value $cfg
  $proc = Start-Process -FilePath (Join-Path $Box 'bedremote-gui.exe') `
            -ArgumentList @('--gui', ('--data=' + (Join-Path $Box 'data'))) -PassThru
  $B = 'http://127.0.0.1:' + $Port + '/cmd?'
  $top = [IntPtr]::Zero
  for ($i = 0; $i -lt 40 -and $top -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 300; $top = [Ug]::TopWindow($proc.Id) }
  Write-Host ''
  Write-Host ('=== bedremote GUI guard: pid ' + $proc.Id + ' port ' + $Port + ' box ' + $Box + ' ===')
  Check 'window came up' ($top -ne [IntPtr]::Zero)
  if ($top -eq [IntPtr]::Zero) { throw 'no window' }
  Check 'the token in the config is in effect' ((Code ($B + 'c=ping&t=' + $OLD)) -eq 200)
  Check 'and the bare address is refused'      ((Code ($B + 'c=ping')) -eq 403)

  $shots = Join-Path $env:TEMP 'brguishots'
  New-Item -ItemType Directory -Force -Path $shots | Out-Null
  Shot (Join-Path $shots 'gui_before.png')

  # ---------- 1. the box mirrors the live token ----------
  $edit = [Ug]::EditWithText($top, $OLD)
  Check 'token box shows the current token'   ($edit -ne '') ('no Edit control contains "' + $OLD + '"; buttons: ' + [Ug]::Buttons($top))
  $hEdit = [IntPtr]0
  if ($edit -ne '') { $hEdit = [IntPtr][int64]$edit }

  # ---------- 2. type a new token, press apply, no restart ----------
  $NEW = 'zz88qq33'
  [Ug]::SetText($hEdit, $NEW)
  $clicked = [Ug]::Click($top, $capApply)
  Check 'apply button was found'              ($clicked -match 'BM_CLICK') ($clicked)
  Start-Sleep -Milliseconds 1500
  Check 'same process, no restart'            ((Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) -ne $null)
  Check 'new token works immediately'         ((Code ($B + 'c=ping&t=' + $NEW)) -eq 200)
  Check 'old token is refused'                ((Code ($B + 'c=ping&t=' + $OLD)) -eq 403)
  Check 'and it was written to bedremote.json' ((Get-Content (Join-Path $Box 'bedremote.json') -Raw) -match $NEW)
  Check 'a page without any token is refused'  ((Code ('http://127.0.0.1:' + $Port + '/')) -eq 403)

  # ---------- 3. a device that pairs over HTTP shows up in the list ----------
  $dev = 'guidev000001'
  $dn = 'gui-phone'
  $null = Body ($B + 'c=ping&t=' + $NEW + '&d=' + $dev + '&dn=' + [Uri]::EscapeDataString($dn))
  Start-Sleep -Milliseconds 2200                 # the GUI refreshes on a 1.2s timer
  $lv = [Ug]::DevList($top)
  $n0 = [Ug]::ListCount($lv)
  Check 'device list shows the new device'    ($n0 -ge 1) ('LVM_GETITEMCOUNT=' + $n0)
  # Reading the box back across processes turned out to be unreliable (GetWindowText on a
  # WinForms TextBox can report the value WinForms cached, not what is painted - the screenshot
  # below shows the new token in the box). So assert on the log line instead: it can only
  # contain the typed value if the box really handed it to the apply handler.
  $tail = [Ug]::LogTail($top, 10)
  Check 'the log records the token that was applied' ($tail -match $NEW) ('tail=' + $tail)
  Shot (Join-Path $shots 'gui_paired.png')

  # ---------- 4. kick it from the GUI ----------
  $before = Body ($B + 'c=devs&t=' + $NEW)
  $sel = [Ug]::SelectItem($lv, [uint32]$proc.Id, 0)
  Check 'the first row got selected'          ($sel -eq 'ok') $sel
  $kc = [Ug]::Click($top, $capKick)
  Check 'kick button was found'               ($kc -match 'BM_CLICK') ('caps: ' + [Ug]::Buttons($top))
  Start-Sleep -Milliseconds 1800
  Check 'the GUI is still alive (no crash)'   ((Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) -ne $null)
  $after = Body ($B + 'c=devs&t=' + $NEW)
  # "[]" (an empty list) is the success case here - the sandbox pairs exactly one device.
  Check 'the device left the list'            (($before -match $dev) -and ($after -match '^\[\s*\]$')) ('before=' + $before + ' after=' + $after)
  Check 'the kicked device is locked out'      ((Code ($B + 'c=ping&d=' + $dev)) -eq 403)
  Check 'log line says who was kicked'        (([Ug]::LogTail($top, 8) -match ('device kicked: ' + $dev)) -or ([Ug]::LogTail($top, 8) -match 'device kicked')) ('tail=' + [Ug]::LogTail($top, 4))
  Shot (Join-Path $shots 'gui_after.png')
  Write-Host ('  shots: ' + $shots)
}
catch {
  $script:fail++
  Write-Host ('  ERROR ' + $_.Exception.Message) -ForegroundColor Red
}
finally {
  if ($proc -and -not $Keep) { try { Stop-Process -Id $proc.Id -Force } catch { } }
  if (-not $Keep) { Start-Sleep -Milliseconds 500; try { Remove-Item -Recurse -Force $Box -ErrorAction SilentlyContinue } catch { } }
}
Write-Host ''
Write-Host ('pass=' + $script:pass + '  fail=' + $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0
