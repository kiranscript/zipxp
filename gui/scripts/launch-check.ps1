[CmdletBinding()]
param([string]$Dotnet = 'dotnet')

$ErrorActionPreference = 'Stop'
$gui = Split-Path -Parent $PSScriptRoot
$project = Join-Path $gui 'StorageExplorer/StorageExplorer.csproj'
& $Dotnet build $project --nologo
if ($LASTEXITCODE -ne 0) { throw 'WPF build failed.' }

$dotnetPath = (Get-Command $Dotnet).Source
$previousDotnetRoot = $env:DOTNET_ROOT
$env:DOTNET_ROOT = Split-Path -Parent $dotnetPath
$appPath = Join-Path $gui 'StorageExplorer/bin/Debug/net10.0-windows/StorageExplorer.exe'
$app = $null
try {
    $app = Start-Process -FilePath $appPath -PassThru
    if (-not $app.WaitForInputIdle(15000)) { throw 'Window did not become idle within 15 seconds.' }
    # WPF can become input-idle before its first window is attached to the process.
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $app.Refresh()
        if ($app.HasExited -or $app.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($app.HasExited -or $app.MainWindowHandle -eq 0 -or $app.MainWindowTitle -notlike 'Storage Explorer*') {
        throw 'Expected WPF main window was not found.'
    }
    Write-Host "PASS: WPF window launched: $($app.MainWindowTitle)"
    if (-not $app.CloseMainWindow()) { throw 'Window close request failed.' }
    if (-not $app.WaitForExit(10000)) { throw 'Window did not close within 10 seconds.' }
    if ($app.ExitCode -ne 0) { throw "App exited with code $($app.ExitCode)." }
    Write-Host 'PASS: idle WPF window closed normally with exit code 0.'
}
finally {
    $env:DOTNET_ROOT = $previousDotnetRoot
    # No force-kill: a compression operation must always be allowed to finish.
    if ($null -ne $app) {
        if (-not $app.HasExited) { $null = $app.CloseMainWindow() }
        $app.Dispose()
    }
}
