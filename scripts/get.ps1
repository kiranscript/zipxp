<#
.SYNOPSIS
  Downloads the latest ZipXP release from GitHub and installs it for the current user.

.DESCRIPTION
  Meant to be fetched with curl and run in one step (see docs/install.md). It downloads the
  release ZIP and its SHA-256 file, verifies the hash, extracts to a temporary folder and runs
  the package's own install.ps1. Nothing needs administrator rights. Pass -Launch,
  -StartAtLogin, -NoShortcut or -Destination to forward them to install.ps1.

  The checksum comes from the same release as the ZIP, so it catches a damaged or truncated
  download. It does not prove who built the package. When the GitHub CLI (gh) is installed and
  signed in, the script also verifies the ZIP's build attestation, which proves it was built by
  this repository's release workflow. Use -RequireAttestation to refuse to install without that
  check. The build is unsigned (no Authenticode signature).
#>
[CmdletBinding()]
param(
    [string]$Version = $env:ZIPXP_VERSION,
    [string]$Destination,
    [switch]$StartAtLogin,
    [switch]$NoShortcut,
    [switch]$Launch,
    [switch]$RequireAttestation,
    [string]$BaseUrl = 'https://github.com/kiranscript/zipxp/releases'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell 5.1 downloads are very slow with the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

if ($env:OS -ne 'Windows_NT') { throw 'ZipXP is a Windows app. Run this from Windows PowerShell.' }
if (-not [Environment]::Is64BitOperatingSystem) { throw 'ZipXP is only published for 64-bit Windows.' }

$asset = 'ZipXP-win-x64.zip'
$BaseUrl = $BaseUrl.TrimEnd('/')
$root = if ($Version) { "$BaseUrl/download/" + $(if ($Version.StartsWith('v')) { $Version } else { "v$Version" }) } else { "$BaseUrl/latest/download" }

$work = Join-Path ([IO.Path]::GetTempPath()) ('zipxp-install-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work $asset
    Write-Host "Downloading $asset ..."
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "$root/$asset" -OutFile $zip
        Invoke-WebRequest -UseBasicParsing -Uri "$root/$asset.sha256" -OutFile "$zip.sha256"
    } catch {
        throw "Could not download ZipXP from $root. Check the version and your connection, or see https://github.com/kiranscript/zipxp/releases. ($($_.Exception.Message))"
    }
    $expected = @((Get-Content -LiteralPath "$zip.sha256" -Raw) -split '\s+' | Where-Object { $_ })[0]
    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($expected -notmatch '^[0-9A-Fa-f]{64}$' -or $actual -ne $expected.ToUpperInvariant()) {
        throw "SHA-256 mismatch for $asset (expected $expected, got $actual). Nothing was installed."
    }
    Write-Host "SHA-256 verified: $actual"

    # Build provenance: only for the official repository, not a -BaseUrl override.
    if ($BaseUrl -eq 'https://github.com/kiranscript/zipxp/releases') {
        $gh = Get-Command gh -ErrorAction SilentlyContinue
        $canVerify = $false
        if ($gh) { & $gh.Source auth status *> $null; $canVerify = ($LASTEXITCODE -eq 0) }
        if ($canVerify) {
            & $gh.Source attestation verify $zip --repo kiranscript/zipxp
            if ($LASTEXITCODE -ne 0) { throw 'Build attestation verification failed. Nothing was installed.' }
            Write-Host 'Build attestation verified.'
        } elseif ($RequireAttestation) {
            throw 'Install the GitHub CLI and run "gh auth login" to verify the build attestation, or omit -RequireAttestation. Nothing was installed.'
        } else {
            Write-Host 'Build attestation not checked (needs the GitHub CLI, signed in). Re-run with -RequireAttestation once you have it.'
        }
    }

    $package = Join-Path $work 'package'
    Expand-Archive -LiteralPath $zip -DestinationPath $package -Force
    $installer = Join-Path $package 'install.ps1'
    if (-not (Test-Path -LiteralPath $installer)) { throw 'The downloaded package has no install.ps1. Nothing was installed.' }

    $forward = @{}
    if ($Destination)  { $forward.Destination = $Destination }
    if ($StartAtLogin) { $forward.StartAtLogin = $true }
    if ($NoShortcut)   { $forward.NoShortcut = $true }
    if ($Launch)       { $forward.Launch = $true }
    & $installer @forward
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
