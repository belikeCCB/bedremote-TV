# ASCII only (this repo enforces it: PowerShell 5.1 reads .ps1 as ANSI, so non-ASCII turns into mojibake).
#
# CI helper: run one of the regression scripts and, if it fails, make the failure READABLE FROM
# OUTSIDE THE LOG.
#
# Why this exists: the first real CI run on GitHub went red on mactest, and the log itself is only
# visible to a signed-in browser - which means the person who has to fix it (or the author, from a
# shell) learns nothing. GitHub publishes check-run *annotations* through an unauthenticated API,
# so we turn every FAIL/ERROR line into an annotation (and also into the job summary). Then
#   GET /repos/<owner>/<repo>/commits/<sha>/check-runs
# is enough to see which check broke.
#
#   ./tools/ci-run.ps1 -Script ./tools/mactest.ps1 -Exe ./bedremote.exe

param(
  [string]$Script = '',
  [string]$Exe = ''
)
$ErrorActionPreference = 'Continue'

if (-not (Test-Path $Script)) { Write-Host ('no such script: ' + $Script); exit 2 }

# `*>&1` and NOT `2>&1`: these regression scripts print through Write-Host, which goes to the
# information stream (6). `2>&1` merges only the error stream, so the capture comes back empty and
# the annotations below would say nothing at all - verified locally with a child script that
# Write-Hosts one line and Write-Outputs another.
$out = & $Script -Exe $Exe *>&1 | Out-String
$code = $LASTEXITCODE
Write-Host $out

if ($code -ne 0) {
  $leaf = Split-Path -Leaf $Script
  $lines = @($out -split "`n")
  $hits = 0
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'FAIL|ERROR|Exception|Cannot|not found') {
      $t = $lines[$i].Trim()
      # These harnesses print the offending value on the line right below a FAIL. "Which check"
      # without "what it actually got" is only half the answer, and the log is not readable
      # without a login - so carry the detail along in the same annotation.
      if (($i + 1) -lt $lines.Count -and $lines[$i + 1] -match '^\s{6,}\S') { $t += '  << ' + $lines[$i + 1].Trim() }
      Write-Host ('::error::' + $leaf + ': ' + $t)
      $hits++
    }
  }
  if ($hits -eq 0) {
    foreach ($b in @($lines | Select-Object -Last 6)) {
      $t = $b.Trim()
      if ($t.Length -gt 0) { Write-Host ('::error::' + $leaf + ': ' + $t) }
    }
  }
  if ($env:GITHUB_STEP_SUMMARY) {
    ('### ' + $leaf + ' failed (exit ' + $code + ')') | Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY
    '```text' | Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY
    ($out -split "`n" | Where-Object { $_ -match 'FAIL|ERROR|pass=|fail=' } | Select-Object -Last 25) |
      Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY
    '```' | Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY
  }
}
exit $code
