[CmdletBinding()]
param([switch]$SkipDependencies, [switch]$CompileOnly)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$releasePath = Join-Path $projectRoot 'test-artifacts\release'
& (Join-Path $projectRoot 'build.ps1') -OutputDirectory $releasePath -SkipDependencies:$SkipDependencies

$outputPath = Join-Path $projectRoot 'test-artifacts\probe'
New-Item -ItemType Directory -Path (Join-Path $outputPath 'lib') -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Recurse -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$sources += (Join-Path $projectRoot 'tests\ProbeProgram.cs')
$sources += (Join-Path $projectRoot 'tests\AdaptiveLayoutTests.cs')

$arguments = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/optimize+',
    '/warn:4',
    '/main:TaskbarTelemetry.ProbeProgram',
    "/out:$outputPath\TaskbarTelemetry.Probe.exe",
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
    throw "Probe build failed with exit code $LASTEXITCODE"
}

if ($CompileOnly) {
    Write-Host 'Application and test sources compiled. No probe was executed; runtime tests are NOT marked passed.'
    return
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'TaskbarTelemetry.exe.config') -Destination (Join-Path $outputPath 'TaskbarTelemetry.Probe.exe.config') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'TaskbarTelemetry.ini') -Destination (Join-Path $outputPath 'TaskbarTelemetry.ini') -Force
Get-ChildItem -LiteralPath (Join-Path $releasePath 'lib') -Filter '*.dll' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $outputPath 'lib') -Force
}
$notificationOverrideName = 'TASKBARTELEMETRY_DISABLE_NOTIFICATIONS'
$previousNotificationOverride = [Environment]::GetEnvironmentVariable($notificationOverrideName, 'Process')
$otherTaskbarTelemetryProcesses = @(Get-Process -Name 'TaskbarTelemetry' -ErrorAction SilentlyContinue)
$canVerifyRealNotificationState = $otherTaskbarTelemetryProcesses.Count -eq 0
$notificationStatePath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TaskbarTelemetry\quota-notification-state.json'
$notificationStateExisted = $canVerifyRealNotificationState -and (Test-Path -LiteralPath $notificationStatePath)
$notificationStateHash = if ($notificationStateExisted) {
    (Get-FileHash -LiteralPath $notificationStatePath -Algorithm SHA256).Hash
} else {
    $null
}
try {
    [Environment]::SetEnvironmentVariable($notificationOverrideName, '1', 'Process')
    & (Join-Path $outputPath 'TaskbarTelemetry.Probe.exe')
    $probeExitCode = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable($notificationOverrideName, $previousNotificationOverride, 'Process')
}
if ($probeExitCode -ne 0) {
    throw "Telemetry probe failed with exit code $probeExitCode"
}
if ($canVerifyRealNotificationState) {
    $notificationStateExistsAfter = Test-Path -LiteralPath $notificationStatePath
    if ($notificationStateExistsAfter -ne $notificationStateExisted) {
        throw 'Disabled probe changed whether the real notification state file exists'
    }
    if ($notificationStateExistsAfter -and
        (Get-FileHash -LiteralPath $notificationStatePath -Algorithm SHA256).Hash -ne $notificationStateHash) {
        throw 'Disabled probe modified the real notification state file'
    }
} else {
    Write-Warning 'Skipped the real notification-state hash check because another TaskbarTelemetry instance is running.'
}
