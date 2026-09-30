# ASCII only (this repo enforces it: PowerShell 5.1 reads .ps1 as ANSI, so non-ASCII turns into mojibake).
#
# Regression guard for the scope of InputLock (0.14.2).
#
# The touchpad and the gamepad send many small commands per second. Those have to be serialised
# among themselves (read cursor position -> compute landing point -> place it must be atomic, or two
# requests both read the same start and a step gets lost). They must NOT queue behind a command that
# takes a while for unrelated reasons - and until 0.14.2 they did: the whole command table ran inside
# one lock. Measured on a real machine with 0.14.1: while one `c=scan` (a ~1.2s sweep of the subnet)
# was in flight, a single `c=move` took 1198 ms instead of 9 ms. On the phone that is "the cursor
# stalls for a second" every time someone presses a button that does something slow.
#
# So: fire one slow command, then time the touchpad path behind it. The assertion is a latency
# ceiling, not a comparison, so it stays useful on a CI runner where the sweep itself may be fast.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\locktest.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\locktest.ps1 -Exe .\bedremote.exe
#
# Exit code 0 = the input path was not held up. CI runs it after build.ps1.
#
# Nothing here moves the pointer: `c=move` is sent with dx=0 dy=0, which injects a zero-pixel move.

param(
  [string]$Exe = '',
  [int]$Port = 0,
  [int]$MaxMs = 400
)
$ErrorActionPreference = 'Stop'
if ($Port -le 0) { $Port = 18800 + (Get-Random -Maximum 150) }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
if ($Exe -eq '') { $Exe = Join-Path $repo 'bedremote.exe' }
if (-not (Test-Path $Exe)) { Write-Host ('NO EXE: ' + $Exe); exit 2 }
$Exe = (Resolve-Path $Exe).Path

$work = Join-Path $env:TEMP ('brlocktest_' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path (Join-Path $work 'data') | Out-Null
Copy-Item $Exe (Join-Path $work 'bedremote.exe')
Copy-Item -Recurse (Join-Path $repo 'www') (Join-Path $work 'www')
$TOK = 'lk' + (Get-Random -Maximum 99999999).ToString('x')
Set-Content -Encoding ASCII -Path (Join-Path $work 'bedremote.json') `
  -Value ('{"port":' + $Port + ',"token":"' + $TOK + '","https":false,"keepAwake":false}')

$script:pass = 0
$script:fail = 0
function Check($name, $ok, $detail) {
  if ($ok) { $script:pass++; Write-Host ('  ok    ' + $name) }
  else {
    $script:fail++
    Write-Host ('  FAIL  ' + $name) -ForegroundColor Red
    if ($detail) { Write-Host ('        ' + $detail) -ForegroundColor DarkGray }
  }
}

function Time_Get($path, $sec) {
  $sw = [Diagnostics.Stopwatch]::StartNew(); $code = -1
  try {
    $r = Invoke-WebRequest -UseBasicParsing -Uri ('http://127.0.0.1:' + $Port + $path) -TimeoutSec $sec -ErrorAction Stop
    $code = [int]$r.StatusCode
  } catch { if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode } }
  $sw.Stop()
  return , @([int]$sw.ElapsedMilliseconds, $code)
}

# Fire a command and keep the socket open, so the handler really runs it (we do not wait for the
# answer - that is the point: it must be slow while something else is asking for a mouse move).
function Fire($path) {
  $c = New-Object System.Net.Sockets.TcpClient
  $c.Connect('127.0.0.1', $Port)
  $st = $c.GetStream()
  $req = "GET $path HTTP/1.1`r`nHost: 127.0.0.1:$Port`r`n`r`n"
  $b = [Text.Encoding]::ASCII.GetBytes($req); $st.Write($b, 0, $b.Length); $st.Flush()
  return $c
}

$proc = $null
try {
  Write-Host ''
  Write-Host ('bedremote input-lock scope: port ' + $Port + ', ceiling ' + $MaxMs + ' ms')
  $proc = Start-Process -FilePath (Join-Path $work 'bedremote.exe') `
            -ArgumentList @('--console', ('--data=' + (Join-Path $work 'data'))) `
            -WorkingDirectory $work -PassThru -WindowStyle Hidden
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    if ((Time_Get ('/health?t=' + $TOK) 3)[1] -ge 0) { break }
  }

  # sanity: the instance answers at all, and the touchpad path is normally quick
  $idle = Time_Get ('/cmd?c=move&dx=0&dy=0&t=' + $TOK) 5
  Check 'touchpad command works at all' ($idle[1] -eq 200) ('code=' + $idle[1])
  Check ('idle c=move is quick (' + $idle[0] + ' ms)') ($idle[0] -lt $MaxMs)

  # The slow one: a subnet sweep. It does not touch the mouse, the keyboard or the displays -
  # exactly the kind of command that must not be able to hold the pointer hostage.
  $slow = Fire ('/cmd?c=scan&t=' + $TOK)
  Start-Sleep -Milliseconds 150
  $worst = 0
  for ($i = 1; $i -le 3; $i++) {
    $t = Time_Get ('/cmd?c=move&dx=0&dy=0&t=' + $TOK) 30
    if ($t[0] -gt $worst) { $worst = $t[0] }
    Check ('while a sweep runs, c=move #' + $i + ' = ' + $t[0] + ' ms') (($t[1] -eq 200) -and ($t[0] -le $MaxMs)) ('code=' + $t[1])
  }
  # and a read-only status request behind the same slow command
  $p = Time_Get ('/cmd?c=status&t=' + $TOK) 30
  Check ('while a sweep runs, c=status = ' + $p[0] + ' ms') (($p[1] -eq 200) -and ($p[0] -le $MaxMs)) ('code=' + $p[1])
  try { $slow.Close() } catch { }
  # What is NOT tested here, said plainly: the macro engine's per-step 20s bound (0.14.2) has no
  # test, because producing a genuinely stuck step means hanging a real ShellExecute or grabbing the
  # clipboard away from the desktop - both of which would disturb the machine the test runs on.
  # It is a bound that turns "wedged forever" into "this one macro stops", nothing more.
}
catch {
  $script:fail++
  Write-Host ('  ERROR ' + $_.Exception.Message) -ForegroundColor Red
}
finally {
  if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch { } }
  Start-Sleep -Milliseconds 400
  try { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
Write-Host ('pass=' + $script:pass + '  fail=' + $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0
