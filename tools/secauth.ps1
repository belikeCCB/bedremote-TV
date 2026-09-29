# ASCII only (this repo enforces it: PowerShell 5.1 reads .ps1 as ANSI, so non-ASCII turns into mojibake).
#
# Regression test for the two access-control tiers:
#   tier 1 = the shared token (?t=...)           -> the gate really closes, and a wrong token is refused
#   tier 2 = per-device pairing (?d=...)         -> a paired device gets in WITHOUT the token,
#                                                   and the owner can revoke one device at a time
#
# It runs a throwaway instance on its own port with --data= pointed at a temp folder, so it never
# touches the real %LOCALAPPDATA%\bed-remote (certs, pairing list, passes).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\secauth.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\secauth.ps1 -Exe .\bedremote.exe
#
# Exit code 0 = everything passed. CI runs it right after build.ps1.

param(
  [string]$Exe = '',
  [int]$Port = 0
)
$ErrorActionPreference = 'Stop'
if ($Port -le 0) { $Port = 18400 + (Get-Random -Maximum 600) }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
if ($Exe -eq '') { $Exe = Join-Path $repo 'bedremote.exe' }
if (-not (Test-Path $Exe)) { Write-Host ('NO EXE: ' + $Exe); exit 2 }
$Exe = (Resolve-Path $Exe).Path

$work = Join-Path $env:TEMP ('brsecauth_' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$data = Join-Path $work 'data'
New-Item -ItemType Directory -Force -Path $work | Out-Null
New-Item -ItemType Directory -Force -Path $data | Out-Null
Copy-Item $Exe (Join-Path $work 'bedremote.exe')
Copy-Item -Recurse (Join-Path $repo 'www') (Join-Path $work 'www')

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

function Code($url) {
  try { return [int](Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 8).StatusCode }
  catch {
    if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
    return -1
  }
}
function Body($url) {
  # Invoke-WebRequest throws on 4xx, and the 403 body is exactly what some checks look at.
  # PowerShell stashes that body in $_.ErrorDetails.Message; the raw stream is the fallback
  # (reading it after the error record was formatted can come back empty).
  try { return (Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 8).Content }
  catch {
    if ($_.ErrorDetails -and $_.ErrorDetails.Message) { return [string]$_.ErrorDetails.Message }
    if ($_.Exception.Response) {
      try {
        $sr = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $t = $sr.ReadToEnd(); $sr.Close(); return [string]$t
      } catch { return '' }
    }
    return ''
  }
}

$proc = $null
function Start-Inst($token) {
  # A token with a comma/spaces would need real quoting; test tokens are bare words on purpose.
  $j = '{"port":' + $Port + ',"token":"' + $token + '","https":false,"keepAwake":true,"denyWhenLocked":true}'
  Set-Content -Encoding ASCII -Path (Join-Path $work 'bedremote.json') -Value $j
  $a = @('--console', ('--data=' + $data))
  $p = Start-Process -FilePath (Join-Path $work 'bedremote.exe') -ArgumentList $a `
        -WorkingDirectory $work -PassThru -WindowStyle Hidden
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    try { if ((Code ("http://127.0.0.1:" + $Port + "/health?t=" + $token)) -ge 0) { break } } catch { }
  }
  return $p
}
function Stop-Inst($p) { if ($p) { try { Stop-Process -Id $p.Id -Force } catch { }; Start-Sleep -Milliseconds 400 } }

$B = 'http://127.0.0.1:' + $Port + '/cmd?'
# A fixed token would let a leftover instance from an earlier run answer these requests and
# every check would pass against the wrong process. Random per run, so only ours can pass.
$TOK = 'kq' + (Get-Random -Maximum 99999999).ToString('x')
$DEVA = 'devtesta0001'
$DEVB = 'devtestb0002'
$UNKNOWN = 'nobodyever0001'

try {
  Write-Host ''
  Write-Host ('=== bedremote access control: port ' + $Port + ', token ' + $TOK + ' ===')
  $proc = Start-Inst $TOK
  Check 'instance is up' ($null -ne $proc -and -not $proc.HasExited)

  # ---- tier 1: the gate closes ----
  Check 'no credential        -> 403'      ((Code ($B + 'c=ping')) -eq 403)
  Check 'wrong token          -> 403'      ((Code ($B + 'c=ping&t=nope')) -eq 403)
  Check 'garbage short dev id -> 403'      ((Code ($B + 'c=ping&d=sh')) -eq 403)
  Check 'dev id with a space  -> 403'      ((Code ($B + 'c=ping&d=dev%20a')) -eq 403)
  Check 'right token          -> 200'      ((Code ($B + 'c=ping&t=' + $TOK)) -eq 200)
  # A person who lost access opens the plain address and gets a bare "403" unless we explain.
  $rootUrl = 'http://127.0.0.1:' + $Port + '/'
  $cRoot = Code $rootUrl
  $bRoot = [string](Body $rootUrl)
  Check 'bare / -> 403 with an HTML explainer' `
        (($cRoot -eq 403) -and ($bRoot -match 'bedremote')) `
        ('code=' + $cRoot + ' len=' + $bRoot.Length + ' head=' + $bRoot.Substring(0, [Math]::Min(90, $bRoot.Length)))
  Check 'pairing happened on the way in'   ((Body ($B + 'c=devs&t=' + $TOK + '&d=' + $DEVA)) -match $DEVA)

  # ---- tier 2: the device credential alone is enough ----
  Check 'paired device, no token -> 200'   ((Code ($B + 'c=ping&d=' + $DEVA)) -eq 200)
  Check 'unknown device, no token -> 403'  ((Code ($B + 'c=ping&d=' + $UNKNOWN)) -eq 403)
  Check 'unknown device is NOT in list'    ((Body ($B + 'c=devs&d=' + $DEVA)) -notmatch $UNKNOWN)

  # ---- kick: revoke ONE device, by its id, and leave the others alone ----
  $null = Code ($B + 'c=ping&t=' + $TOK + '&d=' + $DEVB + '&dn=testphoneB')
  $lst = Body ($B + 'c=devs&d=' + $DEVA)
  Check 'list has both A and B'            (($lst -match $DEVA) -and ($lst -match $DEVB))
  $k = Body ($B + 'c=kick&d=' + $DEVA + '&who=' + $DEVB)
  Check 'kick who=B reported B'            (($k -match 'ok') -and ($k -notmatch 'err'))
  $lst = Body ($B + 'c=devs&d=' + $DEVA)
  Check 'B gone, A still paired'           (($lst -notmatch $DEVB) -and ($lst -match $DEVA))
  Check 'kicked device -> 403'             ((Code ($B + 'c=ping&d=' + $DEVB)) -eq 403)
  Check 'kick of unknown id -> err'        ((Body ($B + 'c=kick&d=' + $DEVA + '&who=' + $UNKNOWN)) -match 'err')
  # A can still use the token, so re-pairing is one scan away - that is the documented recovery path.
  Check 'token still works after kick'     ((Code ($B + 'c=ping&t=' + $TOK + '&d=' + $DEVB)) -eq 200)

  # ---- SSE: a kicked device must be told, not just silently dropped ----
  # EventSource reconnects on its own, so without an explicit "kick" event the phone keeps
  # hammering /events and the user sees a dead number, never a reason.
  # Read it synchronously on one socket and kick from another: PS 5.1's Receive-Job -Timeout
  # is unreliable, and the conversation is strictly ordered anyway.
  function Read-Some($stream, $ms) {
    $buf = New-Object byte[] 8192
    $txt = ''; $closed = $false
    $script:probeErr = $false
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
      if ($stream.DataAvailable) {
        $n = $stream.Read($buf, 0, $buf.Length)
        if ($n -le 0) { $closed = $true; break }          # 0 bytes = the peer closed
        $txt += [Text.Encoding]::UTF8.GetString($buf, 0, $n)
        continue
      }
      # DataAvailable means "bytes sitting in the buffer", so it stays FALSE after a graceful
      # FIN and we would never notice the close. Poll(SelectRead)+Available==0 is the idiom.
      try {
        if ($stream.Socket.Poll(60000, [System.Net.Sockets.SelectMode]::SelectRead) -and $stream.Available -eq 0) {
          $closed = $true; break
        }
      } catch { $probeErr = $true; break }         # can't tell -> say so, never claim "closed"
      Start-Sleep -Milliseconds 40
    }
    return , @($txt, $closed)
  }
  $sock = New-Object System.Net.Sockets.TcpClient
  $sock.Connect('127.0.0.1', $Port)
  $st = $sock.GetStream()
  $hb = [Text.Encoding]::ASCII.GetBytes("GET /events?t=$TOK&d=$DEVB HTTP/1.1`r`nHost: x`r`n`r`n")
  $st.Write($hb, 0, $hb.Length); $st.Flush()
  $r1 = Read-Some $st 1500
  Check 'SSE opened on the device credential' (([string]$r1[0]) -match '"e":"hello"')
  $k = [string](Body ($B + 'c=kick&d=' + $DEVA + '&who=' + $DEVB))
  Check 'kick of a live device -> ok'         ($k -match 'ok:')
  $r2 = Read-Some $st 1200
  $txt = [string]$r1[0] + [string]$r2[0]
  Check 'SSE got the kick event'              ($txt -match '"e":"kick"')
  # Did the server actually hang up? A blocking Read with ReceiveTimeout is the only honest
  # way to see it: DataAvailable stays false after a graceful FIN, and Poll(SelectRead)
  # behaved differently here than on a real socket peer, so neither can be trusted.
  # Read() returning 0 bytes *is* EOF - that we can assert on.
  $eof = ''; $buf2 = New-Object byte[] 4096
  try { $sock.Client.ReceiveTimeout = 3000 } catch { $eof = 'set failed: ' + $_.Exception.Message }
  for ($i = 0; $i -lt 6 -and ($eof -eq '' -or $eof -eq 'more'); $i++) {
    try {
      $n = $sock.Client.Receive($buf2, 0, $buf2.Length, [System.Net.Sockets.SocketFlags]::None)
      if ($n -le 0) { $eof = 'yes' }                      # 0 = the peer closed
      else { $eof = 'more' }                              # more pushes came in, keep reading
    } catch { $eof = 'still open (receive timed out)' }   # 3s with neither data nor EOF
  }
  Check 'SSE socket was closed by the server' ($eof -eq 'yes') ('eof=' + $eof)
  try { $sock.Close() } catch { }

  # ---- default open state: no token at all -> nothing is paired, everything is allowed ----
  Stop-Inst $proc; $proc = $null
  $proc = Start-Inst ''
  Check 'no token: bare ping -> 200'        ((Code ($B + 'c=ping')) -eq 200)
  Check 'no token: list kept on disk'        ((Body ($B + 'c=devs')) -match $DEVA)
  # The record has to survive a restart with its fields intact - a "0" here means the
  # pairing time got lost between the file and memory (it happened once, on a stale build).
  Check 'record kept its pairing time'      ((Body ($B + 'c=devs')) -match '"added":6[0-9]{10}')
  Check 'no token: pairing stays off'       ((Code ($B + 'c=ping&d=freshdevice01')) -eq 200)
  Check 'no token: fresh device not paired' ((Body ($B + 'c=devs')) -notmatch 'freshdevice01')
  Check 'devices.json landed in --data'      (Test-Path (Join-Path $data 'devices.json'))
}
catch {
  $script:fail++
  Write-Host ('  ERROR ' + $_.Exception.Message) -ForegroundColor Red
}
finally {
  if ($proc) { try { Stop-Process -Id $proc.Id -Force } catch { } }
  Get-Job | ForEach-Object { try { Stop-Job $_ -ErrorAction SilentlyContinue; Remove-Job $_ -Force -ErrorAction SilentlyContinue } catch { } }
  Start-Sleep -Milliseconds 400
  try { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
Write-Host ('pass=' + $script:pass + '  fail=' + $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0
