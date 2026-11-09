[CmdletBinding()]
param([string]$Package = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/win-x64'))
$ErrorActionPreference = 'Stop'
$Package = [IO.Path]::GetFullPath($Package)
$run = Join-Path $env:TEMP ('ZipXP-package-check-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $run 'installed-app'
$state = Join-Path $run 'state'
& (Join-Path $Package 'install.ps1') -Source (Join-Path $Package 'app') -Destination $destination -NoShortcut
if (-not (Test-Path -LiteralPath (Join-Path $destination 'zipxp-install.json'))) { throw 'Installation record missing.' }
Write-Host 'PASS: isolated per-user install without startup or shortcut changes.'
$app = Start-Process -FilePath (Join-Path $destination 'ZipXP.StorageManager.exe') -ArgumentList @('--data-dir', ('"' + $state + '"'), '--launch-check') -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $app.Refresh()
        if ($app.HasExited -or $app.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($app.HasExited -or $app.MainWindowHandle -eq 0 -or $app.MainWindowTitle -notlike 'ZipXP*Storage Manager') { throw 'Published app window not found.' }
    Write-Host 'PASS: installed self-contained executable opens the dashboard.'
    if (-not $app.WaitForExit(25000)) { throw 'Installed app did not exit gracefully.' }
    if ($app.ExitCode -ne 0) { throw "Installed app exited with $($app.ExitCode)." }
} finally { $app.Dispose() }
& (Join-Path $destination 'uninstall.ps1') -Destination $destination
if (Test-Path -LiteralPath $destination) { throw 'Installed files remain after uninstall.' }
if (-not (Test-Path -LiteralPath (Join-Path $state 'storage.sqlite3'))) { throw 'Uninstall removed the policy/history database.' }
Write-Host 'PASS: uninstall removes app files and preserves policy/history data.'
Write-Host "Package lifecycle state retained: $run"
