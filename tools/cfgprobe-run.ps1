# ASCII only. Compile cfgprobe with the full source list and run it against the example config
# (positive control) and against a deliberately broken macros block (negative control).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
Set-Location $repo
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = @('Apps','Audio','Config','Devices','Display','Gui','Mates','Macros','Passes','Tls','Wizard','BedRemote') | ForEach-Object { 'src\' + $_ + '.cs' }
$out = Join-Path $repo 'cfgprobe.exe'
& $csc -nologo -codepage:65001 -platform:x64 -main:CfgProbe -r:System.Windows.Forms.dll -r:System.Drawing.dll -out:$out ($src + @('tools\cfgprobe.cs'))
if ($LASTEXITCODE -ne 0) { Write-Host 'CFGPROBE COMPILE FAILED'; exit 1 }
Write-Host 'cfgprobe compiled'

$d = Join-Path $env:TEMP 'cfgprobe_macros'
Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $d | Out-Null

Copy-Item (Join-Path $repo 'bedremote.example.json') (Join-Path $d 'bedremote.json')
& $out $d | Out-Null
Write-Host ('positive (example config) exit=' + $LASTEXITCODE + '  (want 0)')

Set-Content -Encoding UTF8 -Path (Join-Path $d 'bedremote.json') -Value '{ "port": 8765, "macros": { "broken": ["c=mons", "open the player"] } }'
& $out $d | Out-Null
Write-Host ('negative (bad step) exit=' + $LASTEXITCODE + '  (want 1)')

Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $out -Force -ErrorAction SilentlyContinue
