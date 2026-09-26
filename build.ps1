$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$exe  = Join-Path $root 'bedremote.exe'
$cscArgs = @(
  '-nologo','-target:exe','-platform:x64','-optimize+',
  ('-win32manifest:' + (Join-Path $root 'src\app.manifest')),
  '-r:System.Windows.Forms.dll',
  ('-out:' + $exe),
  (Join-Path $root 'src\Config.cs'),
  (Join-Path $root 'src\Display.cs'),
  (Join-Path $root 'src\BedRemote.cs')
)
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED'; exit 1 }
Write-Host ('OK ' + (Get-Item $exe).Length + ' bytes')
