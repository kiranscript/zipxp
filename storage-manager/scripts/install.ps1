[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot 'app'),
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs/ZipXP Storage Manager'),
    [switch]$StartAtLogin,
    [switch]$NoShortcut,
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath (Join-Path $Source 'ZipXP.StorageManager.exe'))) { throw 'Extract the published package and run its install.ps1, or supply -Source with the published app directory.' }
$Destination = [IO.Path]::GetFullPath($Destination)
if (Get-Process -Name 'ZipXP.StorageManager' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Destination + '\', [StringComparison]::OrdinalIgnoreCase) }) {
    throw 'Exit Storage Manager using its tray menu before installing or updating. No process will be force-killed.'
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $Destination
$exe = Join-Path $Destination 'ZipXP.StorageManager.exe'
$shortcutPath = $null
if (-not $NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'ZipXP Storage Manager.lnk'
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $Destination
    $shortcut.Save()
}
@{ product='ZipXP.StorageManager'; installPath=$Destination; shortcut=$shortcutPath } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'zipxp-install.json') -Encoding UTF8
if ($StartAtLogin) {
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $run -Force | Out-Null
    New-ItemProperty -Path $run -Name 'ZipXP.StorageManager' -Value ('"' + $exe + '" --tray') -PropertyType String -Force | Out-Null
}
Write-Host "Installed for this user: $exe. Existing policies/database are retained."
if ($Launch) { Start-Process -FilePath $exe }
