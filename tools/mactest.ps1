# ASCII only - PowerShell 5.1 reads .ps1 as ANSI, so a Chinese comment can eat a newline and
# make the parser blame an unrelated line.
#
# Regression test for the composite-action feature (macros / zudongzuo):
#   a named list of steps, each step = one /cmd query string or "wait=<ms>", run on the PC
#
# It runs a throwaway instance on its own port with --data= pointed at a temp folder, so it
# never touches the real %LOCALAPPDATA%\bed-remote.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\mactest.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\mactest.ps1 -Exe .\bedremote.exe

param(
  [string]$Exe = '',
  [int]$Port = 0
)
$ErrorActionPreference = 'Stop'
if ($Port -le 0) { $Port = 18500 + (Get-Random -Maximum 400) }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
if ($Exe -eq '') { $Exe = Join-Path $repo 'bedremote.exe' }
if (-not (Test-Path $Exe)) { Write-Host ('NO EXE: ' + $Exe); exit 2 }
$Exe = (Resolve-Path $Exe).Path

$work = Join-Path $env:TEMP ('brmactest_' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$data = Join-Path $work 'data'
New-Item -ItemType Directory -Force -Path $work | Out-Null
New-Item -ItemType Directory -Force -Path $data | Out-Null
Copy-Item $Exe (Join-Path $work 'bedremote.exe')
Copy-Item -Recurse (Join-Path $repo 'www') (Join-Path $work 'www')

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
  try { return [int](Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 10).StatusCode }
  catch { if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }; return -1 }
}
function Body($url) {
  try { return [string](Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 10).Content }
  catch { if ($_.ErrorDetails -and $_.ErrorDetails.Message) { return [string]$_.ErrorDetails.Message }
          if ($_.Exception.Response) { try { $sr = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream()); $t = $sr.ReadToEnd(); $sr.Close(); return [string]$t } catch { return '' } }
          return '' }
}
function Post($url, $form) {
  try { return [string](Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -Body $form -TimeoutSec 10).Content }
  catch { if ($_.ErrorDetails -and $_.ErrorDetails.Message) { return [string]$_.ErrorDetails.Message }
          if ($_.Exception.Response) { try { $sr = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream()); $t = $sr.ReadToEnd(); $sr.Close(); return [string]$t } catch { return '' } }
          return '' }
}
# Read an SSE stream for a while. Returns the text seen. Used to prove the progress events
# really arrive - a macro that runs silently is useless from the phone.
function Read-Sse($stream, $ms) {
  $buf = New-Object byte[] 8192
  $txt = ''
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $ms) {
    if ($stream.DataAvailable) {
      $n = $stream.Read($buf, 0, $buf.Length)
      if ($n -le 0) { break }
      $txt += [Text.Encoding]::UTF8.GetString($buf, 0, $n)
    } else { Start-Sleep -Milliseconds 40 }
  }
  return $txt
}

$proc = $null
try {
  # The config carries four macros:
  #   probe    - observable without touching his mouse: read-only c=mons, a wait, then a notify
  #   nested   - calls a macro, which must be refused (two macros pointing at each other = loop)
  #   sleeper  - long enough to prove the busy guard and the stop
  #   toomany  - 40 steps in the file, to prove the loader clamps to 32
  $many = @(); for ($i = 0; $i -lt 40; $i++) { $many += '"c=notify&s=m' + $i + '"' }
  $j = '{ "port":' + $Port + ',"token":"","https":false,"keepAwake":true,' +
       '"macros":{"probe":["c=mons","wait=700","c=notify&s=mac_step_two"],' +
       '"nested":["c=macro&n=nested"],' +
       '"sleeper":["wait=6000","c=notify&s=should_not_arrive"],' +
       '"toomany":[' + ($many -join ',') + ']}}'
  Set-Content -Encoding ASCII -Path (Join-Path $work 'bedremote.json') -Value $j

  $B = 'http://127.0.0.1:' + $Port + '/cmd?'
  $proc = Start-Process -FilePath (Join-Path $work 'bedremote.exe') `
            -ArgumentList @('--console', ('--data=' + $data)) -WorkingDirectory $work -PassThru -WindowStyle Hidden
  for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 250; if ((Code ($B + 'c=ping')) -ge 0) { break } }
  Write-Host ''
  Write-Host ('=== bedremote macros: port ' + $Port + ' work ' + $work + ' ===')
  Check 'instance is up' ((Code ($B + 'c=ping')) -eq 200)

  # ---- the list is read from the config ----
  $list = Body ($B + 'c=macros')
  Check 'c=macros lists the names'  (($list -match 'probe') -and ($list -match 'sleeper')) $list
  Check 'c=macros reports step count' ($list -match '"steps":3') $list
  # 40 steps in the file, the loader keeps at most 32 (a macro is one tap, not a script)
  Check 'a 40-step macro is clamped to 32' ($list -match '"name":"toomany","steps":32') (([regex]::Match($list, '\{[^{}]*toomany[^{}]*\}')).Value)

  # ---- running one: progress must arrive over SSE, in order, with the wait honoured ----
  $sock = New-Object System.Net.Sockets.TcpClient
  $sock.Connect('127.0.0.1', $Port)
  $st = $sock.GetStream()
  $hb = [Text.Encoding]::ASCII.GetBytes("GET /events HTTP/1.1`r`nHost: x`r`n`r`n")
  $st.Write($hb, 0, $hb.Length); $st.Flush()
  $head = Read-Sse $st 800
  Check 'SSE attached' ($head -match '"e":"hello"')

  $t0 = Get-Date
  $start = Body ($B + 'c=macro&n=probe')
  Check 'starting a macro returns immediately' ($start -eq 'ok') ('ack=' + $start)
  $seen = Read-Sse $st 4000
  $el = ((Get-Date) - $t0).TotalMilliseconds
  Check 'step 1 reported over SSE'             ($seen -match '"e":"macro".*?"i":1')
  Check 'step 1 carried the real command ack'  ($seen -match '"i":1,"of":3,"s":"c=mons","ack":".')
  # A wait is not an action, so it deliberately does NOT push a progress frame - the counter
  # just jumps to the next real step. Assert that, otherwise someone will "fix" it by spamming.
  Check 'a wait step does not push a progress frame' ($seen -notmatch '"s":"wait')
  Check 'step 3 fired the notify'              ($seen -match 'mac_step_two')
  Check 'step 3 reported done + progress ended' (($seen -match '"i":3,"of":3') -and ($seen -match '"done":true'))
  Check 'the wait really delayed the run'      ($el -ge 650) ('elapsed=' + [int]$el + 'ms')
  Check 'the macro is not busy afterwards'     ((Body ($B + 'c=macrostatus')) -match '"busy":false')

  # ---- unknown name and nesting ----
  Check 'unknown macro -> a helpful error' ((Body ($B + 'c=macro&n=nope')) -match 'err:.*bedremote\.json')
  Check 'a macro that calls itself is refused' ((Body ($B + 'c=macro&n=nested')) -match 'err:.*c=macro')

  # ---- busy guard + stop ----
  $s1 = Body ($B + 'c=macro&n=sleeper')
  Check 'the long macro started' ($s1 -eq 'ok') ('ack=' + $s1)
  $s2 = Body ($B + 'c=macro&n=probe')
  Check 'a second macro is refused while busy' ($s2 -match 'err:') ('ack=' + $s2)
  $stt = Body ($B + 'c=macrostatus')
  Check 'macrostatus shows which one is running' (($stt -match '"busy":true') -and ($stt -match 'sleeper')) $stt
  $sp = Body ($B + 'c=macrostop')
  Check 'macrostop reports it asked to stop' ($sp -match 'sleeper') $sp
  $waited = Read-Sse $st 3000
  Check 'the stopped macro never reached its last step' ($waited -notmatch 'should_not_arrive')
  Check 'and it says done so the phone can clear the toast' ($waited -match '"n":"sleeper","done":true')
  Check 'busy is cleared after the stop' ((Body ($B + 'c=macrostatus')) -match '"busy":false')

  # ---- the editor's save path ----
  $saved = Post ('http://127.0.0.1:' + $Port + '/macros/save') ('j=' + [Uri]::EscapeDataString('{"edited":["c=notify&s=from_editor","wait=50"]}'))
  Check '/macros/save accepts a good one' ($saved -match '"ok":true') $saved
  # /macros/save replaces the WHOLE section (same contract as /panel/save): the editor always
  # posts the complete list it loaded, so "probe" disappearing here is the designed behaviour.
  $list2 = Body ($B + 'c=macros')
  Check 'the new macro is live without restarting' (($list2 -match '"name":"edited"') -and ($list2 -notmatch 'probe')) $list2
  Check 'and the file was rewritten, not appended' ((Get-Content (Join-Path $work 'bedremote.json') -Raw) -notmatch 'mac_step_two')
  Check 'and it landed in bedremote.json' ((Get-Content (Join-Path $work 'bedremote.json') -Raw) -match 'from_editor')
  Check 'running it does the thing' ((Body ($B + 'c=macro&n=edited')) -eq 'ok')
  $after = Read-Sse $st 2000
  Check 'edited macro pushed its notify over SSE' ($after -match 'from_editor')

  # ---- negative control: a broken save must be refused and must not wipe the good ones ----
  $tooMany = @(); for ($i = 0; $i -lt 40; $i++) { $tooMany += '"c=notify&s=x' + $i + '"' }
  $bad = Post ('http://127.0.0.1:' + $Port + '/macros/save') ('j=' + [Uri]::EscapeDataString('{"big":[' + ($tooMany -join ',') + ']}'))
  Check 'a 40-step save is refused' ($bad -match '"ok":false') $bad
  Check 'the refused save left the list alone' ((Body ($B + 'c=macros')) -match 'edited')
  Check 'and left the file alone' ((Get-Content (Join-Path $work 'bedremote.json') -Raw) -notmatch '"big"')

  # The bug this guards: a save whose shape is wrong (steps given as an object instead of an
  # array) used to be accepted as "zero valid macros" - and because the save REPLACES the whole
  # section, one malformed POST silently wiped every working macro and still answered ok:true.
  $before = Body ($B + 'c=macros')
  $shape = Post ('http://127.0.0.1:' + $Port + '/macros/save') ('j=' + [Uri]::EscapeDataString('{"broken":{"x":1}}'))
  Check 'a wrong-shape save is refused' ($shape -match '"ok":false') $shape
  Check 'wrong-shape save did NOT wipe the list' ((Body ($B + 'c=macros')) -eq $before)
  $num = Post ('http://127.0.0.1:' + $Port + '/macros/save') ('j=' + [Uri]::EscapeDataString('{"n2":[123]}'))
  Check 'a non-string step is refused' ($num -match '"ok":false') $num
  Check 'and the list is still intact' ((Body ($B + 'c=macros')) -eq $before)
  $empty = Post ('http://127.0.0.1:' + $Port + '/macros/save') ('j=' + [Uri]::EscapeDataString('{}'))
  Check 'posting {} is the documented way to clear' ($empty -match '"ok":true') $empty
  Check 'and it really cleared' ((Body ($B + 'c=macros')) -match '^\[\]$')

  # ---- the nesting guard, and the positive control it used to be missing ----
  # Refusing a step whose VERB is macro/macros/macrostatus/macrostop is right: two macros pointing
  # at each other is a loop, and the worker comes from the thread pool, so nothing buries it.
  # The first version looked for the text "c=macro" ANYWHERE in the step, which also refused a
  # perfectly legal notify whose message merely mentions it - confusing, and untestable without
  # a case like this one.
  $mention = Post ('http://127.0.0.1:' + $Port + '/macros/save') ('j=' + [Uri]::EscapeDataString('{"mention":["c=notify&text=see+c=macro+help","wait=10"]}'))
  Check 'a step that only mentions c=macro is accepted' ($mention -match '"ok":true') $mention
  Check '...and running it is not refused as nesting' ((Body ($B + 'c=macro&n=mention')) -match '^ok')
  # Progress only travels over SSE, and "one key before bed" means the phone is face-down by the
  # time step 3 fails. macrostatus therefore keeps the last step's result for whoever asks later.
  $null = Read-Sse $st 1200
  Check 'macrostatus keeps the last step for later' `
        ((Body ($B + 'c=macrostatus')) -match '"last":"mention') ((Body ($B + 'c=macrostatus')))
  $null = Post ('http://127.0.0.1:' + $Port + '/macros/save') ('j=' + [Uri]::EscapeDataString('{"selfish":["c=macro&n=selfish"]}'))
  Check '...but a step that really calls a macro is still refused' ((Body ($B + 'c=macro&n=selfish')) -match 'err:.*c=macro')

  # ---- auth: a macro is a command, so it obeys the token like everything else ----
  $null = Post ('http://127.0.0.1:' + $Port + '/macros/save') ('j=' + [Uri]::EscapeDataString('{"probe":["c=mons"],"edited":["c=notify&s=keep"]}' ))
  try { $sock.Close() } catch { }
  Stop-Process -Id $proc.Id -Force; $proc = $null
  Start-Sleep -Milliseconds 500
  Set-Content -Encoding ASCII -Path (Join-Path $work 'bedremote.json') -Value ('{ "port":' + $Port + ',"token":"mt7x","https":false,"keepAwake":true,"macros":{"probe":["c=mons"]}}')
  $proc = Start-Process -FilePath (Join-Path $work 'bedremote.exe') -ArgumentList @('--console', ('--data=' + $data)) -WorkingDirectory $work -PassThru -WindowStyle Hidden
  for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 250; if ((Code ($B + 'c=ping&t=mt7x')) -ge 0) { break } }
  Check 'with a token set, a bare c=macro is refused' ((Code ($B + 'c=macro&n=probe')) -eq 403)
  Check 'but a paired device or the token can run it' ((Code ($B + 'c=macro&n=probe&t=mt7x')) -eq 200)

  try { $sock.Close() } catch { }
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
