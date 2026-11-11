[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Folder,
    [Parameter(Mandatory=$true)][string]$Evidence,
    [string]$Dotnet = 'dotnet',
    [switch]$SkipBackgroundChecks
)
$ErrorActionPreference = 'Stop'
$manager = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $PSScriptRoot 'folder-test/bin/Debug/net10.0-windows/FolderTest.dll'
$dll = Join-Path $manager 'StorageManager.App/bin/Debug/net10.0-windows/ZipXP.StorageManager.dll'
$data = Join-Path $Evidence 'isolated-state'
$before = Join-Path $Evidence 'before.json'
$results = [Collections.Generic.List[object]]::new()
$app = $null
$lock = $null
$probeDir = Join-Path $Folder ('_zipxp-probe-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $Evidence | Out-Null
Start-Transcript -Path (Join-Path $Evidence 'desktop-test.log') -Force | Out-Null

function Helper([string[]]$Arguments) {
    & $Dotnet $helper @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Evidence helper failed: $($Arguments[0])" }
}
function State {
    $file = Join-Path $Evidence 'live-state.json'
    Helper -Arguments @('read', $data, $file)
    return Get-Content -Raw -Encoding UTF8 -LiteralPath $file | ConvertFrom-Json
}
function Check([bool]$Condition, [string]$Name, [string]$Detail = '') {
    $results.Add([PSCustomObject]@{Utc=[DateTime]::UtcNow.ToString('O');Passed=$Condition;Name=$Name;Detail=$Detail})
    if (-not $Condition) { throw "FAIL: $Name $Detail" }
    Write-Host "PASS: $Name $Detail"
}
function Wait-For([scriptblock]$Condition, [string]$Name, [int]$Seconds = 45) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { Check $true $Name; return }
        if ($app) { $app.Refresh(); if ($app.HasExited) { throw 'The test app exited unexpectedly.' } }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Name"
}
function Compressed([string]$Path) { return (([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::Compressed) -ne 0) }
function Find-Id([string]$Id, $Scope = $script:window) {
    return $Scope.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
}
function Invoke-Name([string]$Pattern, $Scope = $script:window) {
    $elements = $Scope.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))
    foreach ($button in $elements) {
        if ($button.Current.Name -like $Pattern -and ($Scope -ne $script:window -or -not $button.Current.IsOffscreen)) {
            $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); return
        }
    }
    throw "Button not found in test window: $Pattern"
}
function Invoke-MenuItem([string]$Pattern) {
    # "More actions" opens a context menu: its items are separate popup elements under the desktop root.
    Invoke-Name 'More actions'
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::MenuItem)
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        foreach ($item in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($item.Current.Name -like $Pattern) { $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); return }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Menu item not found: $Pattern"
}
function Invoke-Id([string]$Id) {
    $element = Find-Id $Id
    if (-not $element) { throw "UI control missing: $Id" }
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Find-Dialog([string]$Title) {
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$app.Id),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Title))
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Confirm-Yes($Dialog) {
    # This Windows build exposes message-box buttons as UIA panes without InvokePattern.
    # WM_COMMAND/IDYES targets only the owned, PID-checked native confirmation dialog.
    if (-not ('ZipXPTest.Dialog' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace ZipXPTest {
    public static class Dialog {
        [DllImport("user32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
'@
    }
    if ($Dialog.Current.ProcessId -ne $app.Id -or $Dialog.Current.NativeWindowHandle -eq 0) { throw 'Confirmation is not owned by the test app.' }
    if (-not [ZipXPTest.Dialog]::PostMessage([IntPtr]$Dialog.Current.NativeWindowHandle, 0x111, [IntPtr]6, [IntPtr]::Zero)) { throw 'Native confirmation failed.' }
}
function Start-App {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command $Dotnet).Source
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.Arguments = '"' + $dll + '" --data-dir "' + $data + '"'
    $script:app = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $app.Refresh()
        if ($app.HasExited) { throw 'App exited during startup.' }
        if ($app.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($app.MainWindowHandle -eq 0) { throw 'App window not found.' }
    $script:window = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
}
function Exit-App {
    if (-not $app -or $app.HasExited) { return }
    Invoke-Name 'Quit ZipXP'
    if (-not $app.WaitForExit(30000)) { throw 'App did not exit gracefully.' }
    Check ($app.ExitCode -eq 0) 'App exited gracefully' ("exit=" + $app.ExitCode)
}

try {
    if (-not (Test-Path -LiteralPath $before)) { Helper -Arguments @('capture', $Folder, $before) }
    if (-not (Test-Path -LiteralPath (Join-Path $data 'storage.sqlite3'))) { Helper -Arguments @('seed', $Folder, $data, (Join-Path $Evidence 'analysis.json')) }
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Start-App
    Check $true 'Actual Windows dashboard launched' $app.MainWindowTitle
    Wait-For { $card = Find-Id 'AllocatedCard'; $card -and $card.Current.Name -match '\d' } 'Managed folder loaded in dashboard'
    foreach ($page in @('Managed folders', 'Storage insights', 'Activity', 'Overview')) { Invoke-Name $page; Check $true ('UI navigation: ' + $page) }

    if ((Find-Id 'PauseButton').Current.Name -ne 'Resume all') { Invoke-Id 'PauseButton' }
    Wait-For { (Find-Id 'PauseButton').Current.Name -eq 'Resume all' } 'Paused before enabling the automatic policy'
    Invoke-Name 'Managed folders'
    Invoke-Name 'Folder settings*'
    Wait-For { Find-Dialog 'Folder settings' } 'Policy editor opened'
    $editor = Find-Dialog 'Folder settings'
    $enable = Find-Id 'EnableBox' $editor
    $toggle = $enable.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    Invoke-Name 'Save' $editor
    Wait-For { $s = State; $s.Roots[0].Policy.Enabled -and @($s.Jobs | Where-Object { $_.State -eq 'Queued' }).Count -gt 0 } 'Default automatic policy saved and candidates queued'
    Helper -Arguments @('read', $data, (Join-Path $Evidence 'queued-state.json'))

    $lockedPath = Join-Path $Folder 'lock-me-8MiB.txt'
    if (Test-Path -LiteralPath $lockedPath) { $lock = [IO.File]::Open($lockedPath, 'Open', 'ReadWrite', 'None') }
    Invoke-Id 'PauseButton'
    Wait-For { $s = State; @($s.Jobs | Where-Object { $_.State -eq 'Succeeded' }).Count -gt 0 } 'Automatic compression starts through desktop Resume'
    if ($lock) {
        Wait-For { $s = State; @($s.Jobs | Where-Object { $_.Expected.Path -eq $lockedPath -and $_.State -eq 'Retry' }).Count -gt 0 } 'Exclusive lock produces a visible retry record'
        $s = State
        Check (@($s.Jobs | Where-Object { $_.State -eq 'Succeeded' -and $_.Expected.Path -ne $lockedPath }).Count -gt 0) 'Other selected work completes despite the locked file'
        Helper -Arguments @('read', $data, (Join-Path $Evidence 'locked-state.json'))
        $lock.Dispose(); $lock = $null
    }

    Invoke-Id 'PauseButton'
    Wait-For { (Find-Id 'PauseButton').Current.Name -eq 'Resume all' } 'Stop-after-current completes'
    $s = State; $completed = @($s.Jobs | Where-Object { $_.FinishedUtc }).Count
    Start-Sleep -Seconds 2
    $s = State
    Check (@($s.Jobs | Where-Object { $_.FinishedUtc }).Count -eq $completed) 'Paused worker starts no additional files'
    Helper -Arguments @('read', $data, (Join-Path $Evidence 'paused-state.json'))
    Invoke-Id 'PauseButton'
    Wait-For { $s = State; @($s.Jobs | Where-Object { $_.State -in @('Queued','Retry','Running') }).Count -eq 0 } 'Original-folder compression queue drains' 90
    Helper -Arguments @('capture', $Folder, (Join-Path $Evidence 'after-compression.json'))
    Helper -Arguments @('read', $data, (Join-Path $Evidence 'compressed-state.json'))
    Invoke-Name 'Overview'
    Start-Sleep -Seconds 2
    $ui = [ordered]@{Utc=[DateTime]::UtcNow.ToString('O'); ProcessId=$app.Id; Title=$app.MainWindowTitle}
    foreach ($id in @('AllocatedCard','EstimateCard','MeasuredCard','ScopeCard','WorkerText')) {
        $element = Find-Id $id
        $ui[$id] = if ($element) { $element.Current.Name } else { 'Not exposed by UI Automation' }
    }
    $ui | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $Evidence 'dashboard.json')
    Check $app.Responding 'Dashboard remains responsive during completed job review'

    if (-not $SkipBackgroundChecks) {
    # Actual default 60-second quiet policy; no injected time or resource signals.
    Helper -Arguments @('probe', $probeDir, 'created-while-running.txt', (Join-Path $Evidence 'new-file.json'))
    $newFile = Join-Path $probeDir 'created-while-running.txt'
    Start-Sleep -Seconds 5
    Check (-not (Compressed $newFile)) 'New file remains uncompressed during the default quiet period'
    $pidBefore = $app.Id
    Check ($app.CloseMainWindow()) 'Main-window close request accepted'
    Start-Sleep -Seconds 1; $app.Refresh()
    Check (-not $app.HasExited -and $app.MainWindowHandle -eq 0) 'Closing the window keeps the tray worker running'
    Wait-For { Compressed $newFile } 'New file is automatically compressed with the main window closed' 100
    Helper -Arguments @('read', $data, (Join-Path $Evidence 'background-state.json'))

    # A second launch should activate the original process, not create another worker.
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command $Dotnet).Source; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.Arguments = '"' + $dll + '" --data-dir "' + $data + '"'
    $second = [Diagnostics.Process]::Start($start)
    if (-not $second.WaitForExit(15000)) { throw 'Second launch did not hand off activation.' }
    $second.Dispose()
    Wait-For { $app.Refresh(); $app.MainWindowHandle -ne 0 } 'Second launch reopens the existing dashboard'
    Check ($app.Id -eq $pidBefore) 'Single-instance activation preserves the original worker PID'
    $script:window = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    Exit-App

    Helper -Arguments @('probe', $probeDir, 'created-while-offline.txt', (Join-Path $Evidence 'offline-file.json'), 'aged')
    $offline = Join-Path $probeDir 'created-while-offline.txt'
    Check (-not (Compressed $offline)) 'File created while the worker is stopped is initially uncompressed'
    Start-App
    Wait-For { Compressed $offline } 'Restart reconciliation compresses the offline-created file' 45
    Wait-For { $s = State; @($s.Jobs | Where-Object { $_.State -in @('Queued','Retry','Running') }).Count -eq 0 } 'Restarted queue drains'
    Helper -Arguments @('read', $data, (Join-Path $Evidence 'restarted-state.json'))
    }

    Invoke-Name 'Managed folders'
    Invoke-Name 'Restore original size*'
    Wait-For { Find-Dialog 'Restore original size' } 'Recursive decompression preview displayed'
    $preview = Find-Dialog 'Restore original size'
    $previewText = $preview.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }
    $previewText | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $Evidence 'decompression-preview.txt')
    Confirm-Yes $preview
    Wait-For { $s = State; -not $s.Roots[0].Policy.Enabled -and @($s.Jobs | Where-Object { $_.Operation -eq 'Decompress' }).Count -gt 0 } 'Decompression disables the automatic policy before execution'
    Wait-For { $s = State; @($s.Jobs | Where-Object { $_.State -in @('Queued','Retry','Running') }).Count -eq 0 } 'Recursive decompression queue drains' 90
    Helper -Arguments @('read', $data, (Join-Path $Evidence 'decompressed-state.json'))
    Exit-App
}
catch {
    $results.Add([PSCustomObject]@{Utc=[DateTime]::UtcNow.ToString('O');Passed=$false;Name='Test execution';Detail=$_.Exception.Message})
    Write-Host ('TEST ERROR: ' + $_.Exception.Message)
}
finally {
    if ($lock) { $lock.Dispose() }
    if ($app -and -not $app.HasExited) {
        try {
            $app.Refresh()
            if ($app.MainWindowHandle -eq 0) {
                $start = [Diagnostics.ProcessStartInfo]::new((Get-Command $Dotnet).Source)
                $start.UseShellExecute = $false; $start.CreateNoWindow = $true
                $start.Arguments = '"' + $dll + '" --data-dir "' + $data + '"'
                $activate = [Diagnostics.Process]::Start($start); $activate.WaitForExit(15000) | Out-Null; $activate.Dispose()
                Start-Sleep -Milliseconds 500; $app.Refresh()
            }
            $script:window = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
            Exit-App
        } catch { Write-Host ('Cleanup could not exit the owned test app: ' + $_.Exception.Message) }
    }
    if (-not $app -or $app.HasExited) {
        try {
            if (Test-Path -LiteralPath $probeDir) { Remove-Item -LiteralPath $probeDir -Recurse -Force }
            Helper -Arguments @('restore', $before, (Join-Path $Evidence 'restoration.json'))
            Helper -Arguments @('capture', $Folder, (Join-Path $Evidence 'after-restoration.json'))
            Check $true 'Original file compression states restored and temporary probes removed'
        } catch { $results.Add([PSCustomObject]@{Utc=[DateTime]::UtcNow.ToString('O');Passed=$false;Name='Restoration';Detail=$_.Exception.Message}) }
    }
    $results | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $Evidence 'checks.json')
    if ($app) { $app.Dispose() }
    Stop-Transcript | Out-Null
}
if (@($results | Where-Object { -not $_.Passed }).Count -gt 0) { exit 1 }
