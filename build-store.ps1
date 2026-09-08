[CmdletBinding()]
param(
    [switch]$Preview,
    [string]$IdentityFile,
    [string]$Version = '1.0.0.0',
    [switch]$SkipDependencies,
    [switch]$PersonalMatch,
    [switch]$SkipScreenshots
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$storeRoot = Join-Path $projectRoot 'store'
$artifactRoot = Join-Path $projectRoot 'artifacts\store'
$compileRoot = Join-Path $artifactRoot 'compiled'
$layoutRoot = Join-Path $artifactRoot 'package-layout'
$assetRoot = Join-Path $storeRoot 'Assets'
$listingRoot = Join-Path $storeRoot 'listing-assets'

if ([string]::IsNullOrWhiteSpace($IdentityFile)) {
    $IdentityFile = Join-Path $storeRoot 'store-identity.json'
}

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'Version must use four numeric parts, for example 1.0.0.0.'
}
try {
    $parsedVersion = [version]$Version
} catch {
    throw 'Version must use four numeric parts, for example 1.0.0.0.'
}
$versionParts = @(
    $parsedVersion.Major,
    $parsedVersion.Minor,
    $parsedVersion.Build,
    $parsedVersion.Revision
)
if ($parsedVersion.Major -lt 1 -or
    ($versionParts | Where-Object { $_ -lt 0 -or $_ -gt 65535 }).Count -gt 0 -or
    $parsedVersion.Revision -ne 0) {
    throw 'Store package version must start at 1, keep every part at or below 65535, and keep the fourth part at 0.'
}

if ($Preview) {
    $identity = [pscustomobject]@{
        IdentityName = 'TaskbarTelemetry.StorePreview'
        Publisher = 'CN=TaskbarTelemetry Store Preview'
        PublisherDisplayName = 'TaskbarTelemetry Store Preview'
        ProductDisplayName = 'TaskbarTelemetry'
    }
} else {
    if (-not (Test-Path -LiteralPath $IdentityFile)) {
        throw "Partner Center identity file was not found at $IdentityFile. Copy store\store-identity.example.json to store\store-identity.json and paste the exact Product identity values, or use -Preview for a non-submittable validation package."
    }
    $identity = Get-Content -LiteralPath $IdentityFile -Raw -Encoding UTF8 | ConvertFrom-Json
}

foreach ($property in @('IdentityName', 'Publisher', 'PublisherDisplayName', 'ProductDisplayName')) {
    $value = [string]$identity.$property
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match '^COPY_') {
        throw "Identity property $property is missing or still contains a placeholder."
    }
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$artifactFull = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
$compileFull = [System.IO.Path]::GetFullPath($compileRoot)
$layoutFull = [System.IO.Path]::GetFullPath($layoutRoot)
foreach ($controlledPath in @($compileFull, $layoutFull)) {
    if (-not $controlledPath.StartsWith($artifactFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace a build directory outside $artifactRoot"
    }
    if (Test-Path -LiteralPath $controlledPath) {
        Remove-Item -LiteralPath $controlledPath -Recurse -Force
    }
}

if ($PersonalMatch) {
    # Build the same executable feature set used by the unpackaged edition.
    # Package identity still provides Store LocalState isolation at runtime.
    & (Join-Path $projectRoot 'build.ps1') -SkipDependencies:$SkipDependencies -OutputDirectory $compileFull
} else {
    & (Join-Path $projectRoot 'build.ps1') -StoreBuild -SkipDependencies:$SkipDependencies -OutputDirectory $compileFull
}
& (Join-Path $storeRoot 'generate-assets.ps1') -AssetDirectory $assetRoot -ListingDirectory $listingRoot
if (-not $SkipScreenshots) {
    & (Join-Path $projectRoot 'generate-store-screenshots.ps1') -PersonalMatch:$PersonalMatch
}

New-Item -ItemType Directory -Path $layoutFull -Force | Out-Null
foreach ($name in @('TaskbarTelemetry.exe', 'TaskbarTelemetry.exe.config')) {
    $source = Join-Path $compileFull $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required Store payload file is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $layoutFull $name) -Force
}

$settingsSource = if ($PersonalMatch) {
    Join-Path $projectRoot 'TaskbarTelemetry.ini'
} else {
    Join-Path $storeRoot 'TaskbarTelemetry.Store.ini'
}
$privacyHtmlSource = if ($PersonalMatch) {
    Join-Path $storeRoot 'PRIVACY.personal.html'
} else {
    Join-Path $projectRoot 'PRIVACY.html'
}
$privacyMarkdownSource = if ($PersonalMatch) {
    Join-Path $storeRoot 'PRIVACY.personal.md'
} else {
    Join-Path $projectRoot 'PRIVACY.md'
}
$noticesSource = if ($PersonalMatch) {
    Join-Path $storeRoot 'THIRD_PARTY_NOTICES.personal.md'
} else {
    Join-Path $storeRoot 'THIRD_PARTY_NOTICES.store.md'
}
foreach ($requiredSource in @($settingsSource, $privacyHtmlSource, $privacyMarkdownSource, $noticesSource)) {
    if (-not (Test-Path -LiteralPath $requiredSource -PathType Leaf)) {
        throw "Required Store source file is missing: $requiredSource"
    }
}

Copy-Item -LiteralPath $settingsSource -Destination (Join-Path $layoutFull 'TaskbarTelemetry.ini') -Force
Copy-Item -LiteralPath $privacyHtmlSource -Destination (Join-Path $layoutFull 'PRIVACY.html') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $layoutFull 'LICENSE.txt') -Force
Copy-Item -LiteralPath $noticesSource -Destination (Join-Path $layoutFull 'THIRD_PARTY_NOTICES.md') -Force
Copy-Item -LiteralPath $privacyMarkdownSource -Destination (Join-Path $layoutFull 'PRIVACY.md') -Force
Copy-Item -LiteralPath $assetRoot -Destination (Join-Path $layoutFull 'Assets') -Recurse -Force

$runtimeLibraries = @(
    'LibreHardwareMonitorLib.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll'
)
if ($PersonalMatch) {
    $compiledLibraryRoot = Join-Path $compileFull 'lib'
    $layoutLibraryRoot = Join-Path $layoutFull 'lib'
    New-Item -ItemType Directory -Path $layoutLibraryRoot -Force | Out-Null
    foreach ($name in $runtimeLibraries) {
        $source = Join-Path $compiledLibraryRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Required personal-match runtime library is missing: $source"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $layoutLibraryRoot $name) -Force
    }
}

$blockedPayload = Get-ChildItem -LiteralPath $layoutFull -Recurse -File | Where-Object {
    $_.Name -match '(?i)pawnio' -or
    ((-not $PersonalMatch) -and $_.Name -match '(?i)librehardwaremonitor') -or
    $_.Extension -match '^(?i:\.sys|\.msi|\.msm|\.inf|\.cat)$' -or
    ($_.Extension -ieq '.exe' -and $_.Name -ine 'TaskbarTelemetry.exe')
}
if ($blockedPayload) {
    throw ('Store payload contains a disallowed driver/backend artifact: ' +
        (($blockedPayload | ForEach-Object { $_.FullName }) -join ', '))
}
$storeExecutableBytes = [System.IO.File]::ReadAllBytes(
    (Join-Path $layoutFull 'TaskbarTelemetry.exe'))
$storeExecutableText =
    [System.Text.Encoding]::ASCII.GetString($storeExecutableBytes) + "`n" +
    [System.Text.Encoding]::Unicode.GetString($storeExecutableBytes)
if ((-not $PersonalMatch) -and $storeExecutableText -match '(?i)PawnIO|LibreHardwareMonitor') {
    throw 'Store executable still contains the excluded CPU-temperature backend.'
}
if ($PersonalMatch -and $storeExecutableText -notmatch '(?i)LibreHardwareMonitor') {
    throw 'Personal-match executable does not contain the expected CPU-temperature backend.'
}

$manifestPath = Join-Path $layoutFull 'AppxManifest.xml'
Copy-Item -LiteralPath (Join-Path $storeRoot 'AppxManifest.template.xml') -Destination $manifestPath -Force
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$namespaces = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$namespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$namespaces.AddNamespace('desktop', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')

$identityNode = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespaces)
$identityNode.SetAttribute('Name', [string]$identity.IdentityName)
$identityNode.SetAttribute('Publisher', [string]$identity.Publisher)
$identityNode.SetAttribute('Version', $parsedVersion.ToString(4))
$manifest.SelectSingleNode('/f:Package/f:Properties/f:DisplayName', $namespaces).InnerText = [string]$identity.ProductDisplayName
$manifest.SelectSingleNode('/f:Package/f:Properties/f:PublisherDisplayName', $namespaces).InnerText = [string]$identity.PublisherDisplayName
$visualNode = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application/uap:VisualElements', $namespaces)
$visualNode.SetAttribute('DisplayName', [string]$identity.ProductDisplayName)
$startupNode = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application/f:Extensions/desktop:Extension/desktop:StartupTask', $namespaces)
$startupNode.SetAttribute('DisplayName', [string]$identity.ProductDisplayName)

$writerSettings = New-Object System.Xml.XmlWriterSettings
$writerSettings.Indent = $true
$writerSettings.Encoding = New-Object System.Text.UTF8Encoding($false)
$writer = [System.Xml.XmlWriter]::Create($manifestPath, $writerSettings)
try {
    $manifest.Save($writer)
} finally {
    $writer.Dispose()
}

$sdkRoots = @()
foreach ($registryPath in @(
    'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots')) {
    $root = (Get-ItemProperty -Path $registryPath -Name KitsRoot10 -ErrorAction SilentlyContinue).KitsRoot10
    if (-not [string]::IsNullOrWhiteSpace($root)) {
        $sdkRoots += $root
    }
}
$makeAppxCandidates = foreach ($sdkRoot in ($sdkRoots | Select-Object -Unique)) {
    Get-ChildItem -Path (Join-Path $sdkRoot 'bin') -Recurse -Filter 'makeappx.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' }
}
$makeAppx = $makeAppxCandidates | Sort-Object { $_.VersionInfo.FileVersionRaw } -Descending | Select-Object -First 1
if ($null -eq $makeAppx) {
    throw 'MakeAppx.exe was not found in the installed Windows SDK.'
}

$suffix = if ($Preview) { '_preview' } elseif ($PersonalMatch) { '_personal' } else { '' }
$packagePath = Join-Path $artifactRoot ("TaskbarTelemetry_{0}_x64{1}.msix" -f $parsedVersion.ToString(4), $suffix)
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}
& $makeAppx.FullName pack /o /d $layoutFull /p $packagePath
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed with exit code $LASTEXITCODE"
}

$hash = Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
$hashPath = $packagePath + '.sha256.txt'
Set-Content -LiteralPath $hashPath -Value ($hash.Hash + '  ' + [System.IO.Path]::GetFileName($packagePath)) -Encoding ASCII

Write-Host "Built unsigned Store package: $packagePath"
Write-Host "SHA-256: $($hash.Hash)"
if ($Preview) {
    Write-Warning 'This preview package uses placeholder identity and cannot be submitted to Microsoft Store.'
}
if ($PersonalMatch) {
    Write-Warning 'This personal-match package includes the optional LibreHardwareMonitor runtime. It does not include or install PawnIO or any driver.'
}
