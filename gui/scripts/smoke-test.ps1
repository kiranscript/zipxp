[CmdletBinding()]
param(
    [string]$FixtureParent = $env:TEMP,
    [string]$Dotnet = 'dotnet',
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$gui = Split-Path -Parent $PSScriptRoot
$runFolder = Join-Path $FixtureParent ('StorageExplorer-alpha-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $runFolder 'fixture'
New-Item -ItemType Directory -Path $fixture | Out-Null

# New disposable folder only. No fixture or report overwrites an existing folder.
$drive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($fixture))
if ($drive.DriveType -ne 'Fixed' -or $drive.DriveFormat -ne 'NTFS') {
    throw 'Fixtures require a local fixed NTFS volume.'
}
if (([System.IO.File]::GetAttributes($fixture) -band [System.IO.FileAttributes]::Compressed) -ne 0) {
    throw 'Choose a fixture parent without compression inheritance enabled.'
}

$repetitive = [System.Text.Encoding]::UTF8.GetBytes(('Storage Explorer disposable test data. ' * 1024))
$buffer = New-Object byte[] (8 * 1024 * 1024)
for ($offset = 0; $offset -lt $buffer.Length; $offset += $repetitive.Length) {
    [Array]::Copy($repetitive, 0, $buffer, $offset, [Math]::Min($repetitive.Length, $buffer.Length - $offset))
}
foreach ($name in @('repetitive.txt', 'locked.txt', 'after-lock.txt', ('spaces ' + [char]0x65E5 + [char]0x672C + [char]0x8A9E + '.txt'))) {
    [System.IO.File]::WriteAllBytes((Join-Path $fixture $name), $buffer)
}
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
[System.IO.File]::WriteAllBytes((Join-Path $fixture 'random.bin'), $buffer)
[System.IO.File]::WriteAllText((Join-Path $fixture 'small.txt'), 'Small file: compression should be skipped.')
New-Item -ItemType Directory -Path (Join-Path $fixture 'child') | Out-Null
[System.IO.File]::WriteAllText((Join-Path $fixture 'child/nested.txt'), 'Shallow scan navigation fixture.')

Get-ChildItem -LiteralPath $fixture -Recurse -File | Get-FileHash -Algorithm SHA256 |
    Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $runFolder 'baseline-sha256.json')

Write-Host "Fixture folder: $fixture"
Write-Host "Baseline hashes: $(Join-Path $runFolder 'baseline-sha256.json')"
if (-not $PrepareOnly) {
    & $Dotnet build (Join-Path $gui 'StorageExplorer/StorageExplorer.csproj') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'WPF build failed.' }
    & $Dotnet run --project (Join-Path $gui 'StorageExplorer.Checks/StorageExplorer.Checks.csproj') -- --fixture $fixture 2>&1 |
        Tee-Object -FilePath (Join-Path $runFolder 'service-checks.txt')
    if ($LASTEXITCODE -ne 0) { throw "Service checks failed; inspect $runFolder" }
    $manifest = Get-Content -Raw (Join-Path $runFolder 'baseline-sha256.json') | ConvertFrom-Json
    foreach ($entry in $manifest) {
        if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash) {
            throw "Content hash mismatch: $($entry.Path)"
        }
    }
    Write-Host 'All baseline SHA-256 hashes independently verified by PowerShell.'
}
Write-Host 'Fixtures and reports are retained. See gui/README.md for manual UI acceptance and cleanup.'
