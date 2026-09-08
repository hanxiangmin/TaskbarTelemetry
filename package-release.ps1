[CmdletBinding()]
param([switch]$SkipDependencies)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $projectRoot 'test.ps1') -SkipDependencies:$SkipDependencies

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$delivery = Join-Path $projectRoot "artifacts\release-candidate-$stamp"
if (Test-Path -LiteralPath $delivery) { throw 'Delivery directory already exists; refusing to overwrite.' }
$sourceDirectory = Join-Path $delivery 'source\TaskbarTelemetry'
$binaryDirectory = Join-Path $delivery 'windows-x64\TaskbarTelemetry'
New-Item -ItemType Directory -Path $sourceDirectory, $binaryDirectory -Force | Out-Null

# Documentation artwork is generated independently from runtime diagnostics.
# These pictures contain demonstration data, never desktop screenshots.
& (Join-Path $projectRoot 'generate-readme-artwork.ps1')
$screenshots = Join-Path $projectRoot 'docs\screenshots'
New-Item -ItemType Directory -Path $screenshots -Force | Out-Null

$rootFiles = @(
    '.gitignore', '.gitattributes', 'LICENSE', 'README.md', 'START-HERE.txt', 'CONTRIBUTING.md', 'SECURITY.md', 'PRIVACY.md', 'PRIVACY.html', 'THIRD_PARTY_NOTICES.md',
    'TaskbarTelemetry.ini', 'TaskbarTelemetry.exe.config', 'app.manifest', 'app-store.manifest',
    'build.ps1', 'build-store.ps1', 'setup-dependencies.ps1', 'run.ps1', 'test.ps1',
    'package-release.ps1', 'package-portable.ps1', 'generate-readme-artwork.ps1', 'generate-store-screenshots.ps1', 'configure-serverchan.ps1'
)
foreach ($file in $rootFiles) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination (Join-Path $sourceDirectory $file)
}
foreach ($folder in @('src', 'tests', 'docs', 'tools', 'licenses')) {
    $folderPath = Join-Path $projectRoot $folder
    foreach ($file in Get-ChildItem -LiteralPath $folderPath -Recurse -File) {
        $relative = $file.FullName.Substring($projectRoot.Length + 1)
        $allowed = ($folder -eq 'src' -and $file.Extension -eq '.cs') -or
            ($folder -eq 'tests' -and $file.Extension -eq '.cs') -or
            ($folder -eq 'tools' -and $file.Extension -in @('.cs', '.ini', '.ps1')) -or
            ($folder -eq 'licenses' -and $file.Extension -in @('.md', '.txt')) -or
            ($folder -eq 'docs' -and ($file.Extension -eq '.md' -or $file.DirectoryName -eq $screenshots))
        if (-not $allowed) { continue }
        $target = Join-Path $sourceDirectory $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
$storeFiles = @(
    'AppxManifest.template.xml', 'generate-assets.ps1', 'store-identity.example.json',
    'TaskbarTelemetry.Store.ini', 'TaskbarTelemetry.Screenshot.ini',
    'certification-notes.md', 'certification-notes.personal.md', 'partner-center-guide-zh-CN.md',
    'listing-zh-CN.md', 'listing-zh-CN.personal.md', 'submission-checklist.md',
    'PRIVACY.personal.md', 'PRIVACY.personal.html', 'THIRD_PARTY_NOTICES.store.md', 'THIRD_PARTY_NOTICES.personal.md',
    'Assets\StoreLogo.png', 'Assets\Square44x44Logo.png', 'Assets\Square150x150Logo.png'
)
foreach ($file in $storeFiles) {
    $target = Join-Path $sourceDirectory "store\$file"
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot "store\$file") -Destination $target
}

# Fail closed on accidentally copied account/runtime files or personal absolute paths.
foreach ($file in Get-ChildItem -LiteralPath $sourceDirectory -Recurse -File) {
    if ($file.Name -match '^(auth\.json|store-identity\.json|quota-notification.*|.*\.dpapi)$') {
        throw "Runtime/credential file in source delivery: $($file.Name)"
    }
    if ($file.Extension -in @('.cs', '.ps1', '.md', '.ini', '.json', '.html', '.xml')) {
        $contents = [IO.File]::ReadAllText($file.FullName)
        if ($contents -match '(?i)[A-Z]:[\\/](?:Users[\\/](?!<|example)|GitHub[\\/])') {
            throw "Personal absolute path in source delivery: $($file.Name)"
        }
        foreach ($match in [regex]::Matches($contents, '(?:sctp[0-9]{1,20}t|SCT)[A-Za-z0-9_-]{10,}')) {
            if ($match.Value -notmatch 'TEST|EXAMPLE') { throw "Potential SendKey in source delivery: $($file.Name)" }
        }
    }
}

# Always build into a fresh directory so no existing user's INI/state is included.
& (Join-Path $projectRoot 'build.ps1') -SkipDependencies -OutputDirectory $binaryDirectory
foreach ($file in @('LICENSE', 'README.md', 'START-HERE.txt', 'CONTRIBUTING.md', 'SECURITY.md', 'PRIVACY.md', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination (Join-Path $binaryDirectory $file)
}
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'docs') -Destination (Join-Path $binaryDirectory 'docs') -Recurse
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'licenses') -Destination (Join-Path $binaryDirectory 'licenses') -Recurse
Compress-Archive -LiteralPath (Join-Path $delivery 'source\TaskbarTelemetry') -DestinationPath (Join-Path $delivery 'TaskbarTelemetry-source.zip')
Compress-Archive -LiteralPath (Join-Path $delivery 'windows-x64\TaskbarTelemetry') -DestinationPath (Join-Path $delivery 'TaskbarTelemetry-windows-x64.zip')
$hashLines = foreach ($file in Get-ChildItem -LiteralPath $delivery -Filter '*.zip' -File) {
    "{0}  {1}" -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash, $file.Name
}
[IO.File]::WriteAllLines((Join-Path $delivery 'SHA256SUMS.txt'), $hashLines)
Write-Output "Candidate delivery (not published): $delivery"
