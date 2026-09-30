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
function Start-Inst($token, $writeCfg = $true) {
  # A token with a comma/spaces would need real quoting; test tokens are bare words on purpose.
  if ($writeCfg) {
    $j = '{"port":' + $Port + ',"token":"' + $token + '","https":false,"keepAwake":true,"denyWhenLocked":true}'
    Set-Content -Encoding ASCII -Path (Join-Path $work 'bedremote.json') -Value $j
  }
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

# Some checks need headers a normal HTTP client will not let us fake (Origin, Sec-Fetch-Site,
# Transfer-Encoding: chunked) or a request that has no body at all. Raw socket, read until the
# server closes (every response here is Connection: close). Bodies must stay ASCII: the
# Content-Length we write is a character count.
function Send-Raw($method, $path, $headers, $body, $noLength = $false, $addr = '127.0.0.1') {
  $c = New-Object System.Net.Sockets.TcpClient
  $c.Connect($addr, $Port)
  $st = $c.GetStream()
  $req = $method + ' ' + $path + ' HTTP/1.1' + "`r`n" + 'Host: ' + $addr + ':' + $Port + "`r`n"
  foreach ($h in $headers) { $req += $h + "`r`n" }
  if ((-not $noLength) -and ($null -ne $body)) { $req += 'Content-Length: ' + $body.Length + "`r`n" }
  $req += "`r`n"
  $b = [Text.Encoding]::ASCII.GetBytes($req)
  $st.Write($b, 0, $b.Length)
  if ($null -ne $body -and $body.Length -gt 0) {
    $bb = [Text.Encoding]::ASCII.GetBytes($body); $st.Write($bb, 0, $bb.Length)
  }
  $st.Flush()
  $txt = ''; $buf = New-Object byte[] 16384
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt 6000) {
    try { $n = $st.Read($buf, 0, $buf.Length) } catch { break }
    if ($n -le 0) { break }
    $txt += [Text.Encoding]::UTF8.GetString($buf, 0, $n)
  }
  try { $c.Close() } catch { }
  return $txt
}
function RawCode($resp) { if ($resp -match '^HTTP/1\.1 (\d{3})') { return [int]$matches[1] }; return -1 }

$B = 'http://127.0.0.1:' + $Port + '/cmd?'
# Some checks have to look like they came from ANOTHER machine. The server deliberately still shows
# the token to 127.0.0.1 - that is how /pair draws a QR code a phone can actually use - so
# "a paired device must not be able to read the token" can only be tested over a real
# non-loopback address. Asking our own LAN address from the same box needs no second machine
# and does not go through the firewall.
$allIPs = ([System.Net.Dns]::GetHostEntry([System.Net.Dns]::GetHostName())).AddressList
$LANS = @($allIPs | Where-Object { $_.AddressFamily -eq 'InterNetwork' -and -not [System.Net.IPAddress]::IsLoopback($_) } |
    ForEach-Object { $_.ToString() })
if ($LANS.Count -eq 0) { Write-Host 'NO LAN ADDRESS - these checks need one'; exit 2 }
$L = 'http://' + $LANS[0] + ':' + $Port
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

  # ---- cross-site requests (the drive-by hole) ----
  # Before 0.14.1 any web page open on the phone or the PC could drive this machine:
  #   <img src="http://192.168.x.x:8765/cmd?c=key&...">  needs no credential when no token is set,
  # and every response carried Access-Control-Allow-Origin: *, so the same page could also READ
  # the token back out of /addr with a fetch(). The gate now looks at Origin / Sec-Fetch-Site /
  # Referer - headers Invoke-WebRequest will not let us forge, hence Send-Raw.
  # Positive control matters here: curl and local scripts send none of those headers and must keep working.
  Stop-Inst $proc; $proc = $null
  $proc = Start-Inst $TOK
  $P = '/cmd?c=ping&t=' + $TOK
  Check 'raw GET, no Origin (curl alike) -> 200' ((RawCode (Send-Raw 'GET' $P @() $null)) -eq 200)
  Check 'Origin = this very host          -> 200' `
        ((RawCode (Send-Raw 'GET' $P @('Origin: http://127.0.0.1:' + $Port) $null)) -eq 200)
  Check 'Origin = some other site         -> 403' ((RawCode (Send-Raw 'GET' $P @('Origin: http://evil.example') $null)) -eq 403)
  Check 'Origin: null (file://, sandbox)  -> 403' ((RawCode (Send-Raw 'GET' $P @('Origin: null') $null)) -eq 403)
  Check 'Sec-Fetch-Site: cross-site       -> 403' ((RawCode (Send-Raw 'GET' $P @('Sec-Fetch-Site: cross-site') $null)) -eq 403)
  Check '<img> drive-by (no-cors)         -> 403' `
        ((RawCode (Send-Raw 'GET' $P @('Sec-Fetch-Site: cross-site', 'Sec-Fetch-Mode: no-cors') $null)) -eq 403)
  Check 'Referer from another site        -> 403' `
        ((RawCode (Send-Raw 'GET' $P @('Referer: http://evil.example/x', 'Sec-Fetch-Mode: cors') $null)) -eq 403)
  # The exception that keeps the product usable: clicking a link to a page that changes nothing
  # is a cross-site *navigation*, and it must still land (pairing page, icons, editor shell).
  Check 'link click to a static page      -> 200' `
        ((RawCode (Send-Raw 'GET' '/sw.js' @('Sec-Fetch-Site: cross-site', 'Sec-Fetch-Mode: navigate',
                                             'Referer: http://evil.example/x') $null)) -eq 200)
  Check 'cross-site navigation to /pair   -> 200' `
        ((RawCode (Send-Raw 'GET' '/pair' @('Sec-Fetch-Site: cross-site', 'Sec-Fetch-Mode: navigate') $null)) -eq 200)
  # ...but the same evidence on an ACTION endpoint must be refused, iframe included.
  Check 'iframe navigation into /cmd      -> 403' `
        ((RawCode (Send-Raw 'GET' $P @('Sec-Fetch-Site: cross-site', 'Sec-Fetch-Mode: navigate') $null)) -eq 403)
  $adr = Send-Raw 'GET' ('/addr?t=' + $TOK) @() $null
  Check 'no Access-Control-Allow-Origin anywhere' `
        ((($adr -notmatch 'Access-Control') -and ((Send-Raw 'GET' '/health' @() $null) -notmatch 'Access-Control')))
  Check 'frames are refused (X-Frame-Options)'  ($adr -match 'X-Frame-Options: deny')

  # ---- an empty / unreadable save body must never mean "save an empty config" ----
  # Reproduced on 0.14.0: POST /panel/save with no body returned ok:true and dropped every
  # custom button (the handler read r.Body ?? "{}"). Chunked was the same hole from the other side.
  $null = Code ('http://127.0.0.1:' + $Port + '/health?t=' + $TOK)
  Check 'a real panel save lands' `
        ((RawCode (Send-Raw 'POST' ('/panel/save?t=' + $TOK) @('Content-Type: application/json') '{"tabs":[]}')) -eq 200)
  Check 'empty body POST /panel/save    -> 400' `
        ((RawCode (Send-Raw 'POST' ('/panel/save?t=' + $TOK) @('Content-Type: application/json') '')) -eq 400)
  Check 'panel survived the empty POST'  ((Body ('http://127.0.0.1:' + $Port + '/panel?t=' + $TOK)) -match 'tabs')
  Check 'empty body POST /gamepad/save  -> 400' `
        ((RawCode (Send-Raw 'POST' ('/gamepad/save?t=' + $TOK) @('Content-Type: application/json') '')) -eq 400)
  Check 'empty body POST /macros/save   -> 400' `
        ((RawCode (Send-Raw 'POST' ('/macros/save?t=' + $TOK) @('Content-Type: application/json') '')) -eq 400)
  Check 'chunked POST /macros/save      -> 411' `
        ((RawCode (Send-Raw 'POST' ('/macros/save?t=' + $TOK) @('Transfer-Encoding: chunked') `
                              ("2`r`n{}`r`n0`r`n`r`n") $true)) -eq 411)
  Check 'macros survived the chunked POST' ((Body ('http://127.0.0.1:' + $Port + '/macros?t=' + $TOK)) -match '^\{\}$')

  # ---- the token is not handed to somebody who only shows a device id ----
  # /addr used to print the plaintext token to anyone Authed let in - including a device that was
  # paired long ago. Then "kick this device" bought nothing: it had already copied the key and
  # could re-pair at leisure. Same story for the manifest (start_url carries ?t=) and for the
  # /share redirect. These have to be requested over the LAN address, not 127.0.0.1: the machine
  # asking about itself is the pairing page's normal path and is allowed to see the token.
  Check '/addr over the token shows it'     ((Body ($L + '/addr?t=' + $TOK)) -match $TOK)
  Check '/addr from the machine itself still shows it (pair page)' `
        ((Body ('http://127.0.0.1:' + $Port + '/addr')) -match $TOK)
  $dOnly = Body ($L + '/addr?d=' + $DEVA)
  Check 'device id alone does NOT buy the token' `
        ((($dOnly -notmatch ([regex]::Escape($TOK))) -and ($dOnly -match 'tokenHidden'))) ($dOnly)
  Check 'a paired device still gets its counts' `
        ((($dOnly -match '"port"') -and ($dOnly -match '"devs"'))) ($dOnly)
  Check 'manifest over the token -> 200'    ((Code ($L + '/manifest.webmanifest?t=' + $TOK)) -eq 200)
  Check 'manifest with only a device id -> 403' `
        ((Code ($L + '/manifest.webmanifest?d=' + $DEVA)) -eq 403)
  # The /share redirect used to append ?t= for everyone. It still has to for the PWA path
  # (that request carries the token because the manifest baked it in), and must not for d-only.
  # url= is deliberately NOT a link: PickSharedUrl finds nothing, so OpenUrl never runs and this
  # check does not pop a browser on the machine it is testing.
  $sh1 = Send-Raw 'GET' ('/share?url=hello&t=' + $TOK) @() $null $false $LANS[0]
  Check 'share echoes the token back to the one who brought it' ($sh1 -match ('t=' + $TOK))
  $sh2 = Send-Raw 'GET' ('/share?url=hello&d=' + $DEVA) @() $null $false $LANS[0]
  Check 'share does NOT hand the token to a device-id caller' `
        ((($sh2 -match '302') -and ($sh2 -notmatch ([regex]::Escape($TOK))))) ($sh2)

  # ---- pairing has a real ceiling ----
  # It used to be trimmed only at startup, so "present a fresh id" grew the list without limit.
  for ($i = 0; $i -lt 60; $i++) {
    $null = Code ($B + 'c=ping&t=' + $TOK + '&d=cap' + $i.ToString('000') + 'device')
  }
  $dv = Body ($B + 'c=devs&t=' + $TOK)
  $cnt = ([regex]::Matches($dv, '"id":"')).Count
  Check 'pairing is capped at 50'        (($cnt -gt 0) -and ($cnt -le 50)) ('count=' + $cnt)
  # Re-pair the ones the later checks still want in the list (they may have been the oldest).
  $null = Code ($B + 'c=ping&t=' + $TOK + '&d=' + $DEVA)
  $null = Code ($B + 'c=ping&t=' + $TOK + '&d=' + $DEVB)

  # ---- a device name is attacker-chosen text that lands in the local log ----
  # "?dn=" used to be stored verbatim except for length, so a name like
  #   "x\n[token cleared]"      could forge a line that looks like the program's own log output.
  $null = Code ($B + 'c=ping&t=' + $TOK + '&d=capname00001&dn=' + [Uri]::EscapeDataString("ni`nhao"))
  $dv = Body ($B + 'c=devs&t=' + $TOK)
  Check 'device name keeps its printable part' ($dv -match 'nihao') ($dv)
  Check 'no raw newline in the device list'    ($dv -notmatch "`n")

  # ---- a half-written config must NOT silently take the door off ----
  # This is why every write goes through Config.WriteAtomic now: the old WriteAllText could die
  # mid-file, and "cannot parse bedremote.json" used to mean "token empty, port 8765" - a gate
  # that opens itself, looking perfectly healthy on the phone. The reader now falls back to the
  # .bak that a good save left behind, keeps the broken half as .corrupt, and heals the live file.
  Stop-Inst $proc; $proc = $null
  $cfg = Join-Path $work 'bedremote.json'
  Check 'a good save left bedremote.json.bak'  (Test-Path ($cfg + '.bak'))
  $txt = [string](Get-Content -Raw -Encoding UTF8 $cfg)
  Set-Content -Encoding ASCII -Path $cfg -Value $txt.Substring(0, [int]($txt.Length / 2))
  $proc = Start-Inst $TOK $false
  Check 'broken config: the token still guards' ((Code ($B + 'c=ping&t=' + $TOK)) -eq 200)
  Check 'broken config: no credential -> 403'   ((Code ($B + 'c=ping')) -eq 403)
  Check 'broken config: wrong token -> 403'     ((Code ($B + 'c=ping&t=nope')) -eq 403)
  Check 'the half-file was kept as .corrupt'    (Test-Path ($cfg + '.corrupt'))
  Check 'the live config healed itself' `
        (([string](Get-Content -Raw -Encoding UTF8 $cfg)) -match 'token')
  # The heal must NOT overwrite the backup with the half-file it just recovered from -
  # that .bak is the only working config left. (File.Replace with a backup arg does exactly that.)
  # Asserting "the token is in there" is NOT enough: the broken half-file still starts with
  # {"port":..,"token":..}, so it matches too. What separates them is that the half-file is
  # not parseable JSON, so parse it.
  $bakTxt = [string](Get-Content -Raw -Encoding UTF8 ($cfg + '.bak'))
  $bakOk = $true
  try { $null = ($bakTxt | ConvertFrom-Json) } catch { $bakOk = $false }
  Check 'the backup was not clobbered by the heal' $bakOk ('bak=' + $bakTxt.Length + ' bytes, parses=' + $bakOk)
  Check 'the page says it came from the backup' `
        ((Body ('http://127.0.0.1:' + $Port + '/status?t=' + $TOK)) -match 'bak')
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
