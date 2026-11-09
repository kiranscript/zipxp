[CmdletBinding()]
param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs/ZipXP Storage Manager'))
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
$marker = Join-Path $Destination 'zipxp-install.json'
if (-not (Test-Path -LiteralPath $marker)) { throw 'This directory has no ZipXP installation record.' }
$installation = Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json
if ($installation.product -ne 'ZipXP.StorageManager' -or -not $Destination.Equals($installation.installPath, [StringComparison]::OrdinalIgnoreCase) -or $Destination -eq [IO.Path]::GetPathRoot($Destination)) { throw 'Installation record does not match the directory.' }
if (Get-Process -Name 'ZipXP.StorageManager' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Destination + '\', [StringComparison]::OrdinalIgnoreCase) }) {
    throw 'Exit Storage Manager through its tray menu first; the current operation must be allowed to finish.'
}
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$registration = Get-ItemProperty -Path $run -ErrorAction SilentlyContinue
$registered = $null
if ($registration -and $registration.PSObject.Properties['ZipXP.StorageManager']) {
    $registered = $registration.PSObject.Properties['ZipXP.StorageManager'].Value
}
if ($registered -and $registered.StartsWith(('"' + (Join-Path $Destination 'ZipXP.StorageManager.exe') + '"'), [StringComparison]::OrdinalIgnoreCase)) {
    Remove-ItemProperty -Path $run -Name 'ZipXP.StorageManager'
}
if ($installation.shortcut -and (Test-Path -LiteralPath $installation.shortcut)) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($installation.shortcut)
    if ($shortcut.TargetPath.Equals((Join-Path $Destination 'ZipXP.StorageManager.exe'), [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $installation.shortcut }
}
if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
Write-Host 'Removed the per-user app and login registration. Managed files and the local policy/history database were left unchanged.'
