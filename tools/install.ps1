# Installs the freshly built bin\AUXDropoutFix.exe without any message boxes (same as double-clicking it) and writes
# the result to tools\install-result.txt.
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\bin\AUXDropoutFix.exe')
)
$resultFile = Join-Path $PSScriptRoot 'install-result.txt'
$p = Start-Process -FilePath (Resolve-Path -LiteralPath $Source).Path -ArgumentList '/silent' -PassThru
$p.WaitForExit(20000) | Out-Null
$target = Join-Path $env:LOCALAPPDATA 'Programs\AUXDropoutFix\AUXDropoutFix.exe'
Start-Sleep -Seconds 2
$running = @(Get-Process -Name AUXDropoutFix -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target })
$lines = @(
    'Exit code: ' + $p.ExitCode,
    'Installed: ' + (Test-Path -LiteralPath $target),
    'Running: ' + ($running.Count -gt 0)
)
if (Test-Path -LiteralPath $target) { $lines += 'SHA-256: ' + (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
[IO.File]::WriteAllLines($resultFile, $lines, (New-Object Text.UTF8Encoding $true))
