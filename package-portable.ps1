[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [switch]$AcknowledgeIncompleteDesktopValidation
)
$ErrorActionPreference = 'Stop'
if (-not $AcknowledgeIncompleteDesktopValidation) {
    throw 'Desktop regression is incomplete. Use the explicit acknowledgement switch only for a release that documents this limitation. This is not a runtime test bypass or a passing-test declaration.'
}
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $projectRoot
try {
    $status = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw 'Commit reviewed sources before packaging; working tree must be clean.' }
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') { throw 'Unable to determine source commit.' }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $delivery = Join-Path $projectRoot "artifacts\portable-$Version-$stamp"
    if (Test-Path -LiteralPath $delivery) { throw 'Delivery directory already exists; refusing to overwrite.' }
    New-Item -ItemType Directory -Path $delivery | Out-Null
    $sourceZip = Join-Path $delivery "TaskbarTelemetry-v$Version-source.zip"
    git archive --format=zip --prefix=TaskbarTelemetry/ "--output=$sourceZip" $commit
    if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
    $sourceParent = Join-Path $delivery 'source'
    Expand-Archive -LiteralPath $sourceZip -DestinationPath $sourceParent
    $sourceRoot = Join-Path $sourceParent 'TaskbarTelemetry'
    $runtimeParent = Join-Path $delivery 'runtime'
    $runtimeRoot = Join-Path $runtimeParent 'TaskbarTelemetry'

    # Reuse only the exact, hash-verified official archive. Extraction happens in
    # the fresh source export, never trusting DLLs from the developer's lib folder.
    $expectedArchiveHash = '086D9F1B5A99E643EDC2CFAAAC16051685B551E4C5AC0B32A57C58C0E529C001'
    $cachedArchive = Join-Path $projectRoot 'packages\LibreHardwareMonitor-0.9.6.zip'
    if ((Test-Path -LiteralPath $cachedArchive) -and
        (Get-FileHash -LiteralPath $cachedArchive -Algorithm SHA256).Hash -eq $expectedArchiveHash) {
        $freshPackages = Join-Path $sourceRoot 'packages'
        New-Item -ItemType Directory -Path $freshPackages | Out-Null
        Copy-Item -LiteralPath $cachedArchive -Destination (Join-Path $freshPackages 'LibreHardwareMonitor-0.9.6.zip')
    }
    & (Join-Path $sourceRoot 'build.ps1') -OutputDirectory $runtimeRoot
    & (Join-Path $sourceRoot 'test.ps1') -SkipDependencies -CompileOnly
    & (Join-Path $sourceRoot 'build.ps1') -StoreBuild -OutputDirectory 'test-artifacts\store-compile'

    $rootDocs = @('START-HERE.txt','LICENSE','README.md','CONTRIBUTING.md','SECURITY.md','PRIVACY.md','THIRD_PARTY_NOTICES.md')
    foreach ($name in $rootDocs) { Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $runtimeRoot $name) }
    foreach ($folder in @('docs','licenses')) { Copy-Item -LiteralPath (Join-Path $sourceRoot $folder) -Destination (Join-Path $runtimeRoot $folder) -Recurse }
    $runtimeLibraries = @('LibreHardwareMonitorLib.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll')
    foreach ($name in $runtimeLibraries) {
        $actual = Join-Path $runtimeRoot "lib\$name"
        $verified = Join-Path $sourceRoot "packages\LibreHardwareMonitor-0.9.6\$name"
        if ((Get-FileHash -LiteralPath $actual -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $verified -Algorithm SHA256).Hash) {
            throw "Runtime dependency differs from verified archive: $name"
        }
    }
    $exe = Join-Path $runtimeRoot 'TaskbarTelemetry.exe'
    $reader = New-Object IO.BinaryReader([IO.File]::OpenRead($exe))
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Missing DOS header.' }
        $null = $reader.BaseStream.Seek(0x3c, [IO.SeekOrigin]::Begin)
        $peOffset = $reader.ReadInt32()
        $null = $reader.BaseStream.Seek($peOffset, [IO.SeekOrigin]::Begin)
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Expected Windows x64 PE binary.' }
    } finally { $reader.Dispose() }
    if ((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne "$Version.0") { throw 'Executable version does not match release version.' }
    $exeBytes = [IO.File]::ReadAllBytes($exe)
    if ([Text.Encoding]::UTF8.GetString($exeBytes) -notmatch 'requireAdministrator' -and
        [Text.Encoding]::Unicode.GetString($exeBytes) -notmatch 'requireAdministrator') { throw 'Expected UAC manifest was not embedded.' }
    $ini = [IO.File]::ReadAllText((Join-Path $runtimeRoot 'TaskbarTelemetry.ini'))
    if ($ini -notmatch '(?ms)^\[notification\].*?^enabled=false\s*$' -or $ini -notmatch '(?m)^home=\s*$') { throw 'Expected clean defaults.' }
    if ((Get-FileHash -LiteralPath (Join-Path $runtimeRoot 'TaskbarTelemetry.ini')).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $sourceRoot 'TaskbarTelemetry.ini')).Hash) { throw 'Runtime configuration differs from source defaults.' }
    $allowedRoots = @('TaskbarTelemetry.exe','TaskbarTelemetry.exe.config','TaskbarTelemetry.ini','PRIVACY.html') + $rootDocs
    foreach ($file in Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File) {
        $relative = $file.FullName.Substring($runtimeRoot.Length + 1).Replace('\','/')
        if ($relative -notin $allowedRoots -and $relative -notlike 'docs/*' -and $relative -notlike 'licenses/*' -and
            $relative -notin @($runtimeLibraries | ForEach-Object { "lib/$_" })) { throw "Unexpected runtime file: $relative" }
        if ($relative -match '(?i)(auth\.json|sendkey\.dat|\.dpapi|quota-notification|\.sys$|\.pfx$|\.p12$|Probe\.exe|^\.env)') { throw "Forbidden runtime content: $relative" }
        if ($file.Extension -in @('.md','.txt','.html','.ini','.json','.config')) {
            $content = [IO.File]::ReadAllText($file.FullName)
            if ($content -match '(?i)[A-Z]:[\\/](?:Users[\\/](?!<|example)|GitHub[\\/])') { throw "Personal path in runtime: $relative" }
            if ($content -match '(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,}|sk-[A-Za-z0-9_-]{30,})') { throw "Potential credential in runtime: $relative" }
            foreach ($match in [regex]::Matches($content,'(?:sctp[0-9]{1,20}t|SCT)[A-Za-z0-9_-]{10,}')) {
                if ($match.Value -notmatch 'TEST|EXAMPLE') { throw "Potential SendKey in runtime: $relative" }
            }
        }
    }
    $buildInfo = [ordered]@{version=$Version; sourceCommit=$commit; architecture='windows-x64'; requiredRuntime='.NET Framework 4.8';
        applicationAndTestCompilation='passed'; storeConditionalCompilation='passed'; dependencyArchiveSha256=$expectedArchiveHash;
        dependenciesComparedToVerifiedArchive=$true; runtimeTests='not executed'; desktopRegression='incomplete; see docs/VALIDATION.md';
        codeSigning='unsigned'; personalAccountDataIncluded=$false}
    [IO.File]::WriteAllText((Join-Path $runtimeRoot 'BUILD-INFO.json'), ($buildInfo | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    $fileManifest = @(Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring($runtimeRoot.Length+1).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $manifest = [ordered]@{version=$Version; sourceCommit=$commit; verification=$buildInfo; files=$fileManifest}
    [IO.File]::WriteAllText((Join-Path $delivery 'RELEASE-MANIFEST.json'),($manifest | ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
    $runtimeZip = Join-Path $delivery "TaskbarTelemetry-v$Version-windows-x64.zip"
    Compress-Archive -LiteralPath $runtimeRoot -DestinationPath $runtimeZip

    # Test the archive round trip, including every file hash, without executing it.
    $verifyParent = Join-Path $delivery 'unzip-check'
    Expand-Archive -LiteralPath $runtimeZip -DestinationPath $verifyParent
    $verifyRoot = Join-Path $verifyParent 'TaskbarTelemetry'
    if (@(Get-ChildItem -LiteralPath $verifyRoot -Recurse -File).Count -ne $fileManifest.Count) { throw 'ZIP file count mismatch.' }
    foreach ($entry in $fileManifest) {
        if ((Get-FileHash -LiteralPath (Join-Path $verifyRoot $entry.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
            throw "ZIP integrity mismatch: $($entry.path)"
        }
    }
    $hashFiles = @($runtimeZip,$sourceZip,(Join-Path $delivery 'RELEASE-MANIFEST.json'))
    $hashLines = foreach ($file in $hashFiles) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($file) }
    [IO.File]::WriteAllLines((Join-Path $delivery 'SHA256SUMS.txt'),$hashLines,(New-Object Text.UTF8Encoding($false)))
    Write-Output "Portable package verified (desktop runtime validation remains incomplete): $delivery"
    Get-ChildItem -LiteralPath $delivery -File | Select-Object Name,Length
} finally { Pop-Location }
