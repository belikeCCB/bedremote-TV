param([string]$Out = '')
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if ($Out -eq '') { $exe = Join-Path $root 'bedremote.exe' }
elseif ($Out -match '^\w:') { $exe = $Out }
else { $exe = Join-Path $root $Out }
$cscArgs = @(
  '-nologo','-target:exe','-langversion:5','-platform:x64','-optimize+','-codepage:65001',
  ('-win32manifest:' + (Join-Path $root 'src\app.manifest')),
  '-r:System.Windows.Forms.dll','-r:System.Drawing.dll',
  ('-out:' + $exe),
  (Join-Path $root 'src\Config.cs'),
  (Join-Path $root 'src\Display.cs'),
  (Join-Path $root 'src\Gui.cs'),
  (Join-Path $root 'src\Tls.cs'),
  (Join-Path $root 'src\Passes.cs'),
  (Join-Path $root 'src\Apps.cs'),
  (Join-Path $root 'src\Wizard.cs'),
  (Join-Path $root 'src\Mates.cs'),
  (Join-Path $root 'src\BedRemote.cs')
)
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED'; exit 1 }
Write-Host ('OK ' + (Get-Item $exe).Length + ' bytes -> ' + $exe)
