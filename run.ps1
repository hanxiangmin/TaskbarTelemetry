[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$executable = Join-Path $projectRoot 'bin\Release\TaskbarTelemetry.exe'
$needsBuild = -not (Test-Path -LiteralPath $executable)
if (-not $needsBuild) {
    $executableWriteTime = (Get-Item -LiteralPath $executable).LastWriteTimeUtc
    $buildInputs = @(
        (Join-Path $projectRoot 'TaskbarTelemetry.ini'),
        (Join-Path $projectRoot 'TaskbarTelemetry.exe.config'),
        (Join-Path $projectRoot 'app.manifest'),
        (Join-Path $projectRoot 'build.ps1')
    )
    $buildInputs += Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Recurse -Filter '*.cs' -File |
        ForEach-Object { $_.FullName }
    $needsBuild = $null -ne ($buildInputs | Where-Object {
        (Test-Path -LiteralPath $_) -and (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $executableWriteTime
    } | Select-Object -First 1)
}

if ($needsBuild) {
    if (Get-Process -Name 'TaskbarTelemetry' -ErrorAction SilentlyContinue) {
        throw '检测到正在运行的旧版 TaskbarTelemetry。请先右键任务栏监控区域并选择“退出”，再重新运行本脚本。'
    }
    & (Join-Path $projectRoot 'build.ps1')
}
Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable)
