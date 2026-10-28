# Uninstalls AUX Dropout Fix: stops the running copy, removes it from Windows startup and deletes the program folder
# and %LOCALAPPDATA%\AUXDropoutFix (settings and log). The result is written to tools\uninstall-result.txt.
$ErrorActionPreference = 'Stop'
$resultFile = Join-Path $PSScriptRoot 'uninstall-result.txt'
$lines = New-Object System.Collections.Generic.List[string]
function Write-Result([string]$s) { $lines.Add((Get-Date -Format 'HH:mm:ss') + '  ' + $s) }

try {
    $running = @(Get-Process -Name AUXDropoutFix -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        & $running[0].Path /exit
        $deadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $deadline -and @(Get-Process -Name AUXDropoutFix -ErrorAction SilentlyContinue).Count -gt 0) {
            Start-Sleep -Milliseconds 200
        }
        Get-Process -Name AUXDropoutFix -ErrorAction SilentlyContinue | Stop-Process -Force
        Write-Result 'Stopped running copy.'
    }
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if ((Get-ItemProperty -LiteralPath $run -ErrorAction SilentlyContinue).PSObject.Properties.Name -contains 'AUX Dropout Fix') {
        Remove-ItemProperty -LiteralPath $run -Name 'AUX Dropout Fix'
        Write-Result 'Removed from Windows startup.'
    }
    foreach ($f in @((Join-Path $env:LOCALAPPDATA 'Programs\AUXDropoutFix'), (Join-Path $env:LOCALAPPDATA 'AUXDropoutFix'))) {
        if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Recurse -Force; Write-Result ('Deleted: ' + $f) }
    }
    Write-Result 'OK'
}
catch {
    Write-Result ('ERROR: ' + $_.Exception.Message)
}
finally {
    [IO.File]::WriteAllLines($resultFile, $lines, (New-Object Text.UTF8Encoding $true))
}
