# package a bedremote release: two zips under .\release, built only from tracked files.
#
# ASCII only in this file (see CONTRIBUTING pitfall #1: a Chinese comment in a .ps1 can eat
# the next line when PowerShell 5.1 reads the file as GBK, and the copy-then-zip step then
# silently ships without the exe).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File package.ps1
#
# What it guarantees (each one is a bug this project actually had):
#   - version comes from src\BedRemote.cs, and a matching annotated tag MUST exist
#   - the tree must be clean: you can only package what you committed
#   - staging is `git ls-files` from that tag, so untracked junk cannot leak into a release
#   - the exe is built from the staged tree, then both the exe and www are byte-checked
#     against the tag, so the zip cannot ship a stale binary or a page from the next version
#   - the user's own bedremote.json is never included
#   - zips of previous versions are deleted, so nobody downloads an old build by accident

param([string]$Version = '', [switch]$KeepTemp)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Read-Version {
  $t = [System.IO.File]::ReadAllText((Join-Path $root 'src\BedRemote.cs'), [System.Text.Encoding]::UTF8)
  $m = [regex]::Match($t, 'Version\s*=\s*"bedremote\s+([0-9][^"]*)"')
  if (-not $m.Success) { throw 'cannot find Version constant in src\BedRemote.cs' }
  return $m.Groups[1].Value
}

if ($Version -eq '') { $Version = Read-Version }
$ref = 'v' + $Version
Write-Host ('version  : ' + $Version)

# 1) nothing may be packaged that is not committed and tagged
$dirty = (& git -C $root status --porcelain 2>&1) -join ''
if ($dirty -ne '') { throw ('working tree is dirty, commit first: ' + $dirty) }
$tagSha = (& git -C $root rev-parse ($ref + '^{commit}') 2>&1)
if ($LASTEXITCODE -ne 0) { throw ('no tag ' + $ref + ' - tag it first (git tag -a ' + $ref + ' -m "...")') }
$headSha = (& git -C $root rev-parse HEAD)
if ($tagSha.Trim() -ne $headSha.Trim()) { Write-Host ('NOTE: ' + $ref + ' points at ' + $tagSha.Trim() + ' (not HEAD); packaging the tag.') }

# stage exactly the tracked files of that tag.
# zip + Expand-Archive on purpose: `git archive --format=tar | tar -x` breaks when this script is
# started from a shell whose PATH has GNU tar first (Git Bash) - it reads "C:\..." as a remote host.
$work = Join-Path $env:TEMP ('brpack-' + $Version + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
$stage = Join-Path $work $ref
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$zip = Join-Path $work 'tree.zip'
& git -C $root archive --format=zip -o $zip $ref
if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force
Remove-Item $zip -Force
$listed = @(& git -C $root ls-tree -r --name-only $ref)
Write-Host ('staged   : ' + $listed.Count + ' tracked files')

# 3) build from the staged tree
#    the exe is named .bin while in TEMP: a freshly written .exe there has vanished on this
#    machine (defender), and shipping an empty win.zip is exactly the bug this script exists to stop
& (Join-Path $stage 'build.ps1') -Out (Join-Path $work 'bedremote.bin') | Out-Null
if (-not (Test-Path (Join-Path $work 'bedremote.bin'))) { throw 'build produced no exe' }
Copy-Item (Join-Path $work 'bedremote.bin') (Join-Path $stage 'bedremote.exe') -Force

# 4) guards before zipping
if (-not (Test-Path (Join-Path $stage 'www\phone.html'))) { throw 'staged tree has no www\phone.html' }
$built = (Get-Item (Join-Path $stage 'bedremote.exe')).Length
if ($built -lt 100000) { throw ('exe looks wrong: ' + $built + ' bytes') }
$wantVer = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes((Join-Path $stage 'src\BedRemote.cs')))
if ($wantVer -notmatch ('bedremote\s+' + [regex]::Escape($Version))) { throw 'staged source does not contain this version' }
$ph = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes((Join-Path $stage 'www\phone.html')))
if ($ph -notmatch ('BUILD="' + [regex]::Escape($Version) + '"')) {
  throw ('www\phone.html BUILD is not ' + $Version + ' - the page and the exe would disagree, and the page would reload itself once per tab')
}
foreach ($f in @('run.cmd', 'stop.cmd', 'pair.cmd', 'build.cmd', 'autostart.cmd', 'unautostart.cmd', 'allow-firewall.cmd')) {
  $p = Join-Path $stage $f
  if (-not (Test-Path $p)) { continue }
  $b = [System.IO.File]::ReadAllBytes($p)
  $cr = @($b | Where-Object { $_ -eq 13 }).Count
  $lf = @($b | Where-Object { $_ -eq 10 }).Count
  if ($cr -eq 0 -or $cr -ne $lf) { throw ($f + ' lost its CRLF (CR=' + $cr + ' LF=' + $lf + ') - it would break in cmd') }
}
if (Test-Path (Join-Path $stage 'bedremote.json')) { throw 'staged tree contains a user config - refuse to ship it' }
# a .ps1 with CJK in it can silently lose a line when PowerShell 5.1 reads it as GBK - and the
# line it loses is often the Copy-Item that puts the exe into the zip (this happened for real)
$nonAscii = @()
foreach ($f in @(& git -C $root ls-tree -r --name-only $ref | Select-String -Pattern '\.ps1$')) {
  $p = Join-Path $root ($f.ToString() -replace '/', '\')
  if (-not (Test-Path $p)) { continue }
  if ([System.IO.File]::ReadAllText($p) -match '[^\x00-\x7F]') { $nonAscii += $f.ToString() }
}
if ($nonAscii.Count) { throw ('.ps1 must be pure ASCII, these are not: ' + ($nonAscii -join ', ')) }
Write-Host ('guards   : ok (exe ' + $built + ' bytes, version/page agree, CRLF intact, ps1 ascii, no user config)')

# 5) two zips, and drop every other version so an old build cannot be downloaded by mistake
$rel = Join-Path $root 'release'
if (-not (Test-Path $rel)) { New-Item -ItemType Directory -Force -Path $rel | Out-Null }
Get-ChildItem $rel -Filter '*.zip' | Where-Object { $_.Name -notlike ('bedremote-' + $Version + '-*') } | Remove-Item -Force

$winZip = Join-Path $rel ('bedremote-' + $Version + '-win.zip')
$srcZip = Join-Path $rel ('bedremote-' + $Version + '-source.zip')
Compress-Archive -Path $stage -DestinationPath $winZip -Force
$exeStaged = Join-Path $stage 'bedremote.exe'
Remove-Item $exeStaged -Force
Compress-Archive -Path $stage -DestinationPath $srcZip -Force

# 6) read the archives back: "I ran Compress-Archive" is not evidence
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($z in @(@{ p = $winZip; exe = $true }, @{ p = $srcZip; exe = $false })) {
  $a = [System.IO.Compression.ZipFile]::OpenRead($z.p)
  $names = @($a.Entries | ForEach-Object { $_.FullName })
  $hasExe = @($names | Where-Object { $_ -like '*bedremote.exe' }).Count
  $bad = @($names | Where-Object { $_ -match 'bedremote\.json$' -or $_ -match '\\release\\' -or $_ -match '\.log$' })
  $a.Dispose()
  $size = (Get-Item $z.p).Length
  Write-Host ('zip      : ' + (Split-Path -Leaf $z.p) + ' entries=' + $names.Count + ' exe=' + $hasExe + ' size=' + $size)
  if ($bad.Count) { throw ('zip leaks user config or logs: ' + ($bad -join ', ')) }
  if ($z.exe -and $hasExe -ne 1) { throw 'win zip must contain exactly one bedremote.exe' }
  if (-not $z.exe -and $hasExe -ne 0) { throw 'source zip must not contain a binary' }
  if ($names.Count -lt 30) { throw ('zip looks empty: only ' + $names.Count + ' entries') }
}

if ($KeepTemp) { Write-Host ('kept staging: ' + $stage) } else { Remove-Item $work -Recurse -Force }
Write-Host ('done: ' + $Version + ' packaged from ' + $ref)
