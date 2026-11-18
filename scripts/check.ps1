[CmdletBinding()]
param([string]$Dotnet = 'dotnet', [switch]$SkipReactBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $SkipReactBuild) {
    Push-Location (Join-Path $root 'src/ZipXP.UI')
    try {
        & npm.cmd ci --no-fund --no-audit
        if ($LASTEXITCODE -ne 0) { throw 'React dependency installation failed.' }
        & npm.cmd test
        if ($LASTEXITCODE -ne 0) { throw 'React interaction checks failed.' }
    } finally { Pop-Location }
}
$buildReact = (-not $SkipReactBuild).ToString().ToLowerInvariant()
& $Dotnet build (Join-Path $root 'src/ZipXP.App/ZipXP.App.csproj') "-p:BuildReact=$buildReact" --nologo
if ($LASTEXITCODE -ne 0) { throw 'WPF build failed.' }
& $Dotnet run --project (Join-Path $root 'tests/ZipXP.Checks/ZipXP.Checks.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Service/integration checks failed.' }

& (Join-Path $PSScriptRoot 'ui-check.ps1') -Dotnet $Dotnet
