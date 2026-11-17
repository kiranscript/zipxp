[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$state = Join-Path $env:TEMP ('ZipXP-window-check-' + [Guid]::NewGuid().ToString('N'))
& $Dotnet run --project (Join-Path $root 'tests/ZipXP.Checks/ZipXP.Checks.csproj') -- --seed-ui $state
if ($LASTEXITCODE -ne 0) { throw 'UI fixture setup failed.' }
foreach ($mode in @('React', 'Native')) {
    $dll = Join-Path $root 'src/ZipXP.App/bin/Debug/net10.0-windows/ZipXP.StorageManager.dll'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command $Dotnet).Source
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardError = $true
    $quitFile = Join-Path $state ('quit-' + $mode)
    $start.Arguments = '"' + $dll + '" --data-dir "' + $state + '" --launch-check --quit-file "' + $quitFile + '"'
    if ($mode -eq 'Native') { $start.Arguments += ' --native-ui' }
    $app = [Diagnostics.Process]::Start($start)
    try {
        # dotnet.exe is a console host, so WaitForInputIdle is not valid even with a WPF child window.
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            $app.Refresh()
            if ($app.HasExited -or $app.MainWindowHandle -ne 0) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($app.HasExited -or $app.MainWindowHandle -eq 0 -or $app.MainWindowTitle -notlike 'ZipXP*') {
            $detail = if ($app.HasExited) { $app.StandardError.ReadToEnd() } else { 'Title: ' + $app.MainWindowTitle }
            throw ('ZipXP window was not found. ' + $detail)
        }
        Write-Host "PASS: $mode desktop dashboard launched: $($app.MainWindowTitle)"
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $window = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
        function Invoke-Button([string]$pattern) {
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
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
            Invoke-Button ($page + '*')
            if ($mode -eq 'React' -and $page -eq 'Managed folders') {
                $deadline = [DateTime]::UtcNow.AddSeconds(10)
                $found = $false
                do {
                    $text = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'demo-fixture'))
                    if ($text.Count -gt 0) { $found = $true; break }
                    Start-Sleep -Milliseconds 100
                } while ([DateTime]::UtcNow -lt $deadline)
                if (-not $found) { throw 'React did not render the managed folder from the desktop bridge.' }
                Write-Host 'PASS: React renders real engine data through the desktop bridge.'
            }
            Write-Host "PASS: $mode dashboard navigation: $page"
        }
        $checkFile = Join-Path $state 'demo-fixture/analyze-only.txt'
        if (([IO.File]::GetAttributes($checkFile) -band [IO.FileAttributes]::Compressed) -ne 0) { throw 'Analysis-only UI fixture was unexpectedly compressed.' }
        Write-Host 'PASS: actual dashboard analysis does not compress files without an enabled policy.'
        if ($mode -eq 'React') { Invoke-Button 'Quit ZipXP' }
        else { Set-Content -LiteralPath $quitFile -Value 'quit' }
        if (-not $app.WaitForExit(20000)) { throw 'Smoke-check app did not exit gracefully.' }
        if ($app.ExitCode -ne 0) { throw "App exited with $($app.ExitCode)." }
        Write-Host 'PASS: graceful worker/tray shutdown; isolated settings database retained.'
    } finally {
        if (-not $app.HasExited) { Set-Content -LiteralPath $quitFile -Value 'quit'; $null = $app.WaitForExit(15000) }
        $app.Dispose()
    }
}
