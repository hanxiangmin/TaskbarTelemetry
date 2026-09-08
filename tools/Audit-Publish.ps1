[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Push-Location $projectRoot
try {
    $files = @(git -c core.quotepath=false ls-files --cached)
    if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) { throw 'Stage the intended source files before auditing.' }
    $failures = New-Object 'System.Collections.Generic.List[string]'
    foreach ($relative in $files) {
        if ($relative -match '^(bin|obj|lib|packages|artifacts|test-artifacts)/' -or
            $relative -match '(?i)(^|/)(auth\.json|\.env(?:\..*)?|store-identity\.json|serverchan-sendkey\.dat|quota-notification[^/]*)$' -or
            $relative -match '(?i)\.(exe|dll|sys|pfx|p12|dpapi)$') {
            $failures.Add("Forbidden release file: $relative")
            continue
        }
        if ([IO.Path]::GetExtension($relative) -notin @('.cs','.ps1','.md','.ini','.json','.html','.xml','.yml','.config')) { continue }
        # Read the exact staged text, not an un-staged working-tree variant.
        $content = (git show ":$relative") -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Unable to read staged file: $relative" }
        if ($content -match '(?i)[A-Z]:[\\/](?:Users[\\/](?!<|example)|GitHub[\\/])') {
            $failures.Add("Personal absolute path: $relative")
        }
        if ($content -match '(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,}|sk-[A-Za-z0-9_-]{30,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)') {
            $failures.Add("Potential credential: $relative")
        }
        foreach ($match in [regex]::Matches($content, '(?:sctp[0-9]{1,20}t|SCT)[A-Za-z0-9_-]{10,}')) {
            if ($match.Value -notmatch 'TEST|EXAMPLE') { $failures.Add("Potential SendKey: $relative") }
        }
    }
    $defaultIni = (git show ':TaskbarTelemetry.ini') -join "`n"
    if ($defaultIni -notmatch '(?ms)^\[notification\].*?^enabled=false\s*$') {
        $failures.Add('Default notifications must be disabled.')
    }
    if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Output $_ }; throw 'Publication audit failed; do not push.' }
    Write-Output ("Publication audit passed for {0} staged files. Pattern checks do not replace manual review of images and text." -f $files.Count)
} finally { Pop-Location }
