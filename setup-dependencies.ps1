[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = Join-Path $projectRoot 'packages'
$archivePath = Join-Path $packageRoot 'LibreHardwareMonitor-0.9.6.zip'
$extractPath = Join-Path $packageRoot 'LibreHardwareMonitor-0.9.6'
$libraryPath = Join-Path $projectRoot 'lib'
$downloadUrl = 'https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/download/v0.9.6/LibreHardwareMonitor.zip'
$expectedSha256 = '086D9F1B5A99E643EDC2CFAAAC16051685B551E4C5AC0B32A57C58C0E529C001'

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
New-Item -ItemType Directory -Path $libraryPath -Force | Out-Null

$downloadRequired = -not (Test-Path -LiteralPath $archivePath)
if (-not $downloadRequired) {
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash
    $downloadRequired = $actualHash -ne $expectedSha256
}

if ($downloadRequired) {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -Headers @{ 'User-Agent' = 'TaskbarTelemetry dependency setup' }
}

$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash
if ($actualHash -ne $expectedSha256) {
    throw "LibreHardwareMonitor archive hash mismatch. Expected $expectedSha256, got $actualHash."
}

if (-not (Test-Path -LiteralPath $extractPath)) {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath
}

$runtimeLibraries = @(
    'LibreHardwareMonitorLib.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll'
)

foreach ($name in $runtimeLibraries) {
    $source = Join-Path $extractPath $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required LibreHardwareMonitor runtime file was not found: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $libraryPath $name) -Force
}

# Remove DLLs copied by older project revisions from the same official archive.
# They are unrelated to the CPU-only path and some contain legacy driver-related
# compatibility symbols that needlessly increase antivirus scan surface.
Get-ChildItem -LiteralPath $extractPath -Filter '*.dll' -File | ForEach-Object {
    if ($runtimeLibraries -notcontains $_.Name) {
        $legacyCopy = Join-Path $libraryPath $_.Name
        if (Test-Path -LiteralPath $legacyCopy) {
            Remove-Item -LiteralPath $legacyCopy -Force
        }
    }
}

Write-Host "Minimal LibreHardwareMonitor 0.9.6 CPU runtime is ready in $libraryPath"
Write-Host 'CPU temperature also requires the official restricted PawnIO driver, installed once as administrator.'
