[CmdletBinding()]
param(
    [switch]$SkipDependencies,
    [switch]$StoreBuild,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputPath = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $projectRoot 'bin\Release'
} elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
}
$sourcePath = Join-Path $projectRoot 'src'
$manifestPath = if ($StoreBuild) {
    Join-Path $projectRoot 'app-store.manifest'
} else {
    Join-Path $projectRoot 'app.manifest'
}

if (-not (Test-Path -LiteralPath $compiler)) {
    throw ".NET Framework 4.8 C# compiler was not found at $compiler"
}

if (-not $SkipDependencies -and -not $StoreBuild) {
    & (Join-Path $projectRoot 'setup-dependencies.ps1')
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$staleBuildFiles = @(
    'TaskbarTelemetry.pdb',
    'TaskbarTelemetry.Probe.exe',
    'TaskbarTelemetry.Probe.exe.config',
    'TaskbarTelemetry.Probe.pdb',
    'attach-debug.log'
)
foreach ($name in $staleBuildFiles) {
    $stalePath = Join-Path $outputPath $name
    if (Test-Path -LiteralPath $stalePath) {
        Remove-Item -LiteralPath $stalePath -Force
    }
}
$sources = Get-ChildItem -LiteralPath $sourcePath -Recurse -Filter '*.cs' -File | ForEach-Object { $_.FullName }

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/warn:4',
    "/win32manifest:$manifestPath",
    "/out:$outputPath\TaskbarTelemetry.exe",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Management.dll',
    '/reference:System.Security.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll'
)
if ($StoreBuild) {
    $arguments += '/define:STORE_BUILD'
}
$arguments += $sources

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "TaskbarTelemetry build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath (Join-Path $outputPath 'TaskbarTelemetry.ini'))) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'TaskbarTelemetry.ini') -Destination (Join-Path $outputPath 'TaskbarTelemetry.ini')
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'TaskbarTelemetry.exe.config') -Destination (Join-Path $outputPath 'TaskbarTelemetry.exe.config') -Force
$privacyPolicyPath = Join-Path $projectRoot 'PRIVACY.html'
if (Test-Path -LiteralPath $privacyPolicyPath) {
    Copy-Item -LiteralPath $privacyPolicyPath -Destination (Join-Path $outputPath 'PRIVACY.html') -Force
}

$libraryPath = Join-Path $projectRoot 'lib'
if ((-not $StoreBuild) -and (Test-Path -LiteralPath $libraryPath)) {
    $outputLibraryPath = Join-Path $outputPath 'lib'
    New-Item -ItemType Directory -Path $outputLibraryPath -Force | Out-Null
    Get-ChildItem -LiteralPath $outputLibraryPath -Filter '*.dll' -File | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
    }

    $runtimeLibraries = @(
        'LibreHardwareMonitorLib.dll',
        'System.Buffers.dll',
        'System.Memory.dll',
        'System.Numerics.Vectors.dll',
        'System.Runtime.CompilerServices.Unsafe.dll'
    )
    foreach ($name in $runtimeLibraries) {
        Copy-Item -LiteralPath (Join-Path $libraryPath $name) -Destination (Join-Path $outputLibraryPath $name) -Force
    }
}

Write-Host "Built $outputPath\TaskbarTelemetry.exe"
