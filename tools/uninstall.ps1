# Uninstalls AUX Dropout Fix without any message boxes (same as removing it from Settings › Apps) and writes the
# result to tools\uninstall-result.txt.
$resultFile = Join-Path $PSScriptRoot 'uninstall-result.txt'
$exe = Join-Path $env:LOCALAPPDATA 'Programs\AUXDropoutFix\AUXDropoutFix.exe'
if (Test-Path -LiteralPath $exe) {
    $p = Start-Process -FilePath $exe -ArgumentList '/uninstall', '/silent' -PassThru
    $p.WaitForExit(20000) | Out-Null
    Start-Sleep -Seconds 4
    $code = $p.ExitCode
} else { $code = 'not installed' }
$lines = @(
    'Exit code: ' + $code,
    'Program folder left: ' + (Test-Path -LiteralPath (Split-Path $exe)),
    'Running: ' + (@(Get-Process -Name AUXDropoutFix -ErrorAction SilentlyContinue).Count -gt 0)
)
[IO.File]::WriteAllLines($resultFile, $lines, (New-Object Text.UTF8Encoding $true))
