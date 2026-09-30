# ASCII only (this repo enforces it: PowerShell 5.1 reads .ps1 as ANSI, so non-ASCII turns into mojibake).
#
# CI/local helper: compile tools\cfgprobe.cs against the real sources and prove that
#   a) bedremote.example.json is actually readable by Config.Load   (positive control)
#   b) a deliberately broken macros block is refused                (negative control)
#
# History: the first version of this script only PRINTED the two exit codes and never checked them,
# so the step could never go red for the reason it exists to catch - and it also could not tell us
# anything when it went red for a different reason (compile / exe refusing to start). Now every
# failure mode exits 1 AND writes a `::error::` line, because GitHub check annotations are readable
# through an unauthenticated API while the Actions log is not (see tools\ci-run.ps1).
#
# Cross-shell note: CI runs `shell: pwsh` (PowerShell 7), local runs 5.1. Both shells must feed
# Config.Load the SAME bytes, so the broken sample is written with an explicit UTF8-no-BOM
# [IO.File]::WriteAllText instead of `Set-Content -Encoding UTF8` (BOM in 5.1, no BOM in 7).
#
#   ./tools/cfgprobe-run.ps1
#   ./tools/cfgprobe-run.ps1 -Keep     # leave cfgprobe.exe + the temp dir for poking at it

param([switch]$Keep)
$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
Set-Location $repo

$script:bad = 0
function Fail([string]$m) {
  $script:bad++
  Write-Host ('FAIL ' + $m)
  Write-Host ('::error::cfgprobe-run.ps1: ' + $m)
}

$example = Join-Path $repo 'bedremote.example.json'
if (-not (Test-Path $example)) { Fail 'bedremote.example.json is missing from the repo'; exit 1 }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Fail ('no csc.exe under ' + $env:WINDIR + '\Microsoft.NET'); exit 1 }

# Compile with the full source list: the button-action table lives in the server code, so a probe
# that compiles alone would drift from what the running exe accepts.
$src = @('Apps','Audio','Config','Devices','Display','Gui','Mates','Macros','Passes','Tls','Wizard','BedRemote') |
        ForEach-Object { 'src\' + $_ + '.cs' }
$out = Join-Path $repo 'cfgprobe.exe'
$cscArgs = @('-nologo','-codepage:65001','-platform:x64','-main:CfgProbe',
             '-r:System.Windows.Forms.dll','-r:System.Drawing.dll',('-out:' + $out)) + $src + @('tools\cfgprobe.cs')
$cc = & $csc @cscArgs | Out-String
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $out)) {
  $tail = @($cc -split "`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -Last 4)
  Fail ('CFGPROBE COMPILE FAILED exit=' + $LASTEXITCODE + ' :: ' + ($tail -join ' | '))
  exit 1
}
Write-Host 'cfgprobe compiled'

$d = Join-Path $env:TEMP 'cfgprobe_macros'
Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $d | Out-Null
$cfg = Join-Path $d 'bedremote.json'

# Run one probe and return @{ code = exit code; mark = the ASCII CFGPROBE| line; tail = last lines }
function Probe([string]$dir) {
  $o = & $out $dir *>&1 | Out-String
  $code = $LASTEXITCODE
  $lines = @($o -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
  $mark = ''
  foreach ($l in $lines) { if ($l -like 'CFGPROBE|*') { $mark = $l } }
  if ($mark -eq '') { $mark = '(no CFGPROBE marker line - the probe did not run: ' +
                              ((@($lines | Select-Object -Last 2)) -join ' | ') + ')' }
  return @{ code = $code; mark = $mark }
}

# --- positive control: the example config that ships in the README must load clean ---------------
Copy-Item $example $cfg -Force
$p = Probe $d
Write-Host ('positive (example config) exit=' + $p.code + '  (want 0)  ' + $p.mark)
if ($p.code -ne 0) { Fail ('bedremote.example.json does NOT load cleanly: ' + $p.mark) }

# --- negative control: prove the probe can still say "no" ---------------------------------------
# A macro step must be a /cmd query string or a wait=. "open the player" is prose, so the probe
# has to reject it. If this ever exits 0, the probe is broken, not the config.
$broken = '{ "port": 8765, "macros": { "broken": ["c=mons", "open the player"] } }'
[System.IO.File]::WriteAllText($cfg, $broken, (New-Object System.Text.UTF8Encoding($false)))
$n = Probe $d
Write-Host ('negative (bad step)     exit=' + $n.code + '  (want 1)  ' + $n.mark)
if ($n.code -eq 0) { Fail ('negative control went green - the probe no longer rejects a bad macro step: ' + $n.mark) }
if ($n.mark -notmatch 'problems=1') { Fail ('negative control found the wrong number of problems (want problems=1): ' + $n.mark) }

if (-not $Keep) {
  Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $out -Force -ErrorAction SilentlyContinue
} else {
  Write-Host ('kept: ' + $out + '  and  ' + $d)
}

if ($script:bad -gt 0) { Write-Host ('CFGPROBE GATE FAILED (' + $script:bad + ')'); exit 1 }
Write-Host 'cfgprobe gate passed: example config loads, bad macro step is refused'
exit 0
