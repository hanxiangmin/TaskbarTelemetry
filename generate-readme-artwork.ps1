[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputRoot = Join-Path $projectRoot 'test-artifacts\readme-artwork'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Recurse -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$sources += (Join-Path $projectRoot 'tools\ReadmeArtwork.cs')
$binary = Join-Path $outputRoot 'TaskbarTelemetry.ReadmeArtwork.exe'
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/optimize+', '/warn:4',
    '/main:TaskbarTelemetry.ReadmeArtwork', "/out:$binary",
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.Management.dll', '/reference:System.Security.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll') + $sources
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'README artwork compilation failed.' }
# This entry point only draws synthetic snapshots. No telemetry engine, tests,
# live window attachment, account access, or notification service is started.
& $binary (Join-Path $projectRoot 'docs\screenshots') (Join-Path $projectRoot 'tools\Artwork.ini')
if ($LASTEXITCODE -ne 0) { throw 'README artwork generation failed. Do not bypass OS policy.' }
