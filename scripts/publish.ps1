[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [string]$Output,
    [string]$SigningThumbprint,
    [string]$TimestampServer
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'artifacts/win-x64' }
$Output = [IO.Path]::GetFullPath($Output)
$appFolder = Join-Path $Output 'app'
$version = (& $Dotnet msbuild (Join-Path $root 'src/ZipXP.App/ZipXP.App.csproj') -getProperty:Version --nologo | Out-String).Trim()
if (-not $version) { throw 'Could not read the version from Directory.Build.props.' }
& $Dotnet publish (Join-Path $root 'src/ZipXP.App/ZipXP.App.csproj') -c Release -r win-x64 --self-contained true -o $appFolder --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$signature = 'Unsigned development build'
if ($SigningThumbprint) {
    $certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $SigningThumbprint)
    foreach ($file in @('ZipXP.StorageManager.exe', 'ZipXP.StorageManager.dll', 'ZipXP.Core.dll')) {
        $parameters = @{FilePath=(Join-Path $appFolder $file); Certificate=$certificate; HashAlgorithm='SHA256'}
        if ($TimestampServer) { $parameters.TimestampServer = $TimestampServer }
        $signed = Set-AuthenticodeSignature @parameters
        if ($signed.Status -ne 'Valid') { throw "Signing failed for ${file}: $($signed.StatusMessage)" }
    }
    $signature = "Signed with $SigningThumbprint"
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1'),(Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $Output
Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'LICENSE'),(Join-Path $root 'CHANGELOG.md') -Destination $Output
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $Output -Recurse -Force
Set-Content -LiteralPath (Join-Path $Output 'BUILD.txt') -Value "ZipXP $version`n$signature`nBuilt: $([DateTime]::UtcNow.ToString('O'))" -Encoding UTF8
Get-ChildItem -LiteralPath $appFolder -File -Recurse | ForEach-Object {
    [PSCustomObject]@{ Path=$_.FullName.Substring($Output.TrimEnd('\').Length + 1); Hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'SHA256.json') -Encoding UTF8
$zip = $Output.TrimEnd('\') + '.zip'
Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zip -Force
Write-Host "$signature. Package: $zip"
