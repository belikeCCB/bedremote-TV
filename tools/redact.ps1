# ASCII only. Fill rectangles over a PNG - used to mask machine names / LAN IPs in the README
# screenshots. Deliberately a solid block (not a blur): a blur can still be read back.
#
#   powershell -File redact.ps1 -Src shot.png -Out shot_masked.png -Rect 103,72,290,20 -Rect 370,490,330,20
#
# Coordinates are pixels from the top-left of the image; -Color must match the background
# (the light form vs the black log box). Iterate: mask, look, widen.
param(
  [Parameter(Mandatory = $true)][string]$Src,
  [string]$Out = '',
  [string[]]$Rect = @(),
  [string]$Color = '#f3f3f3'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if ($Out -eq '') { $Out = $Src }
$c = [System.Drawing.ColorTranslator]::FromHtml($Color)
$bmp = [System.Drawing.Bitmap]::FromFile($Src)
try {
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $b = New-Object System.Drawing.SolidBrush $c
  foreach ($r in $Rect) {
    $p = $r.Split(',')
    if ($p.Count -lt 4) { throw ('bad rect: ' + $r) }
    $x = [int]$p[0]; $y = [int]$p[1]; $w = [int]$p[2]; $h = [int]$p[3]
    if ($x -lt 0 -or $y -lt 0 -or ($x + $w) -gt $bmp.Width -or ($y + $h) -gt $bmp.Height) {
      throw ('rect out of image (' + $bmp.Width + 'x' + $bmp.Height + '): ' + $r)
    }
    $g.FillRectangle($b, $x, $y, $w, $h)
    Write-Host ('masked ' + $r)
  }
  $g.Dispose(); $b.Dispose()
  $tmp = $Out + '.tmp.png'
  $bmp.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png)
} finally { $bmp.Dispose() }
Move-Item -Force $tmp $Out
Write-Host ('SAVED ' + (Get-Item $Out).Length + ' -> ' + $Out)
