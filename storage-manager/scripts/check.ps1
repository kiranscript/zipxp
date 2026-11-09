[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$manager = Split-Path -Parent $PSScriptRoot
& $Dotnet build (Join-Path $manager 'StorageManager.App/StorageManager.App.csproj') --nologo
if ($LASTEXITCODE -ne 0) { throw 'WPF build failed.' }
& $Dotnet run --project (Join-Path $manager 'StorageManager.Checks/StorageManager.Checks.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Service/integration checks failed.' }

$state = Join-Path $env:TEMP ('ZipXP-window-check-' + [Guid]::NewGuid().ToString('N'))
& $Dotnet run --project (Join-Path $manager 'StorageManager.Checks/StorageManager.Checks.csproj') -- --seed-ui $state
if ($LASTEXITCODE -ne 0) { throw 'UI fixture setup failed.' }
$dll = Join-Path $manager 'StorageManager.App/bin/Debug/net10.0-windows/ZipXP.StorageManager.dll'
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Get-Command $Dotnet).Source
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.Arguments = '"' + $dll + '" --data-dir "' + $state + '" --launch-check'
$app = [Diagnostics.Process]::Start($start)
try {
    # dotnet.exe is a console host, so WaitForInputIdle is not valid even with a WPF child window.
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $app.Refresh()
        if ($app.HasExited -or $app.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($app.HasExited -or $app.MainWindowHandle -eq 0 -or $app.MainWindowTitle -notlike 'ZipXP*Storage Manager') { throw 'Storage Manager window was not found.' }
    Write-Host "PASS: actual WPF dashboard launched: $($app.MainWindowTitle)"
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $window = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    function Invoke-Button([string]$pattern) {
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $buttons = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))
            foreach ($button in $buttons) {
                if ($button.Current.Name -like $pattern -and -not $button.Current.IsOffscreen) {
                    $invoke = $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
                    $invoke.Invoke()
                    return
                }
            }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "UI button not found: $pattern"
    }
    foreach ($page in @('Managed folders', 'Storage insights', 'Activity', 'Overview')) {
        Invoke-Button $page
        Write-Host "PASS: native dashboard navigation: $page"
    }
    $checkFile = Join-Path $state 'demo-fixture/analyze-only.txt'
    if (([IO.File]::GetAttributes($checkFile) -band [IO.FileAttributes]::Compressed) -ne 0) { throw 'Analysis-only UI fixture was unexpectedly compressed.' }
    Write-Host 'PASS: actual dashboard analysis does not compress files without an enabled policy.'
    if (-not $app.WaitForExit(20000)) { throw 'Smoke-check app did not exit gracefully.' }
    if ($app.ExitCode -ne 0) { throw "App exited with $($app.ExitCode)." }
    Write-Host 'PASS: graceful worker/tray shutdown; isolated settings database retained.'
} finally { $app.Dispose() }
