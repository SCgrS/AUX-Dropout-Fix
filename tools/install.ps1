# Installs or updates AUX Dropout Fix for the current user: stops the running copy, copies the exe to
# %LOCALAPPDATA%\Programs\AUXDropoutFix, adds it to Windows startup and starts it.
# The result is written to tools\install-result.txt.
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\bin\AUXDropoutFix.exe')
)
$ErrorActionPreference = 'Stop'
$resultFile = Join-Path $PSScriptRoot 'install-result.txt'
$lines = New-Object System.Collections.Generic.List[string]
function Write-Result([string]$s) { $lines.Add((Get-Date -Format 'HH:mm:ss') + '  ' + $s) }

try {
    $Source = (Resolve-Path -LiteralPath $Source).Path
    $folder = Join-Path $env:LOCALAPPDATA 'Programs\AUXDropoutFix'
    $target = Join-Path $folder 'AUXDropoutFix.exe'

    $running = @(Get-Process -Name AUXDropoutFix -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Result ('Stopping running copy: ' + (($running | ForEach-Object { $_.Path }) -join ', '))
        & $running[0].Path /exit
        $deadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $deadline -and @(Get-Process -Name AUXDropoutFix -ErrorAction SilentlyContinue).Count -gt 0) {
            Start-Sleep -Milliseconds 200
        }
        if (@(Get-Process -Name AUXDropoutFix -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Running copy did not stop, nothing installed.' }
    }

    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    Copy-Item -LiteralPath $Source -Destination $target -Force
    Write-Result ('Copied: ' + $target + ' SHA-256 ' + (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash)

    Set-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AUX Dropout Fix' -Value ('"' + $target + '"')
    Write-Result 'Added to Windows startup.'

    Start-Process -FilePath $target
    Start-Sleep -Seconds 2
    $p = @(Get-Process -Name AUXDropoutFix -ErrorAction SilentlyContinue)
    if ($p.Count -eq 0) { throw 'AUX Dropout Fix did not start.' }
    Write-Result ('Started: ' + $p[0].Path + ' (PID ' + $p[0].Id + ')')
    Write-Result 'OK'
}
catch {
    Write-Result ('ERROR: ' + $_.Exception.Message)
}
finally {
    [IO.File]::WriteAllLines($resultFile, $lines, (New-Object Text.UTF8Encoding $true))
}
