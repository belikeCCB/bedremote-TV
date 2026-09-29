# ASCII only. Capture the phone page at phone size, for README screenshots.
#
# Why this exists: on this machine Chrome/Edge headless cannot render at all
# ("Abnormal renderer termination" - the PDH cpu counters on this trimmed Windows are broken),
# and the in-app browser only screenshots when it has a visible surface. So the shot is taken
# from a real browser window with PrintWindow.
#
# Two traps this works around:
#  1) the process you launch is often NOT the one that owns the window (Chrome reuses a browser
#     process per user-data-dir), so windows are matched by "which chrome process was started
#     with this profile dir", not by the launcher PID;
#  2) a unique profile dir per call means a leftover browser from an earlier call can never
#     steal the new window.
param(
  [string]$Url = 'http://127.0.0.1:8765/#pad',
  [string]$Out = '',
  [int]$W = 390,
  [int]$H = 844,
  [int]$Wait = 5000,
  [int]$CropTop = 0,        # drop N pixels from the top: the app-mode title bar is not part of a phone
  [switch]$KeepWindow
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($Out -eq '') { $Out = Join-Path $env:TEMP 'br_shot.png' }
$tag = 'brshot-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$prof = Join-Path $env:TEMP $tag
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
if (-not (Test-Path $chrome)) { $chrome = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' }

$a = @(('--app=' + $Url), ('--window-size=' + $W + ',' + $H), '--window-position=40,40',
       ('--user-data-dir=' + $prof), '--no-first-run', '--no-default-browser-check',
       '--disable-features=CalculateNativeWinOcclusion', '--hide-scrollbars', '--disable-gpu')
Start-Process -FilePath $chrome -ArgumentList $a | Out-Null
Start-Sleep -Milliseconds $Wait

# which chrome-ish processes belong to my profile dir?
$names = @('chrome', 'msedge')
$pids = @()
foreach ($n in $names) {
  $pids += @(Get-CimInstance Win32_Process -Filter ('Name="' + $n + '.exe"') |
             Where-Object { $_.CommandLine -like ('*' + $tag + '*') } | ForEach-Object { [int]$_.ProcessId })
}
if ($pids.Count -eq 0) { Write-Host 'NO BROWSER PROCESS for that profile'; exit 2 }
$saved = $false
foreach ($id in $pids) {
  & (Join-Path $root 'shot.ps1') -Name 'chrome' -OnlyPid $id -Out $Out -Pick big
  if (Test-Path $Out) { $saved = $true; break }
}
if (-not $KeepWindow) {
  foreach ($id in $pids) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
}
Start-Sleep -Milliseconds 400
if (Test-Path $prof) { Remove-Item $prof -Recurse -Force -ErrorAction SilentlyContinue }
if (-not $saved) { Write-Host 'NOT SAVED'; exit 1 }

if ($CropTop -gt 0) {
  Add-Type -AssemblyName System.Drawing
  $bmp = [System.Drawing.Bitmap]::FromFile($Out)
  try {
    # Clone is the least surprising crop API here - the DrawImage overloads will happily
    # interpret a Rectangle argument as "these 3-4 corner points" and throw.
    $r = New-Object System.Drawing.Rectangle(0, $CropTop, $bmp.Width, ($bmp.Height - $CropTop))
    $crop = $bmp.Clone($r, $bmp.PixelFormat)
    $tmp = $Out + '.crop.png'
    $crop.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose()
  } finally { $bmp.Dispose() }
  Move-Item -Force $tmp $Out
}
Write-Host ('SAVED ' + (Get-Item $Out).Length + ' bytes -> ' + $Out)
