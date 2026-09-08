[CmdletBinding()]
param(
    [switch]$PersonalMatch
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputRoot = Join-Path $projectRoot 'test-artifacts\store-screenshot'
$listingRoot = Join-Path $projectRoot 'store\listing-assets'

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $listingRoot -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Recurse -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$sources += (Join-Path $projectRoot 'tests\StoreScreenshotProgram.cs')

$compileConstants = if ($PersonalMatch) {
    'STORE_SCREENSHOT'
} else {
    'STORE_BUILD;STORE_SCREENSHOT'
}

$arguments = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/optimize+',
    '/warn:4',
    ("/define:" + $compileConstants),
    '/main:TaskbarTelemetry.StoreScreenshotProgram',
    ("/out:" + (Join-Path $outputRoot 'TaskbarTelemetry.StoreScreenshot.exe')),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Management.dll',
    '/reference:System.Security.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll'
) + $sources

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Store screenshot renderer build failed with exit code $LASTEXITCODE"
}

& (Join-Path $outputRoot 'TaskbarTelemetry.StoreScreenshot.exe') $listingRoot (Join-Path $projectRoot 'store\TaskbarTelemetry.Screenshot.ini')
if ($LASTEXITCODE -ne 0) {
    throw "Store screenshot renderer failed with exit code $LASTEXITCODE"
}

Write-Host "Generated actual-control Store screenshots in $listingRoot"
