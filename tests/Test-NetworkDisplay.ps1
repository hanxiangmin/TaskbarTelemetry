[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

# Focused, offline checks of the production assembly. Does not run the desktop
# entry point, diagnostic Probe, collectors, account calls, or notifications.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
$staticFlags = [Reflection.BindingFlags]'Static,NonPublic,Public'
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$renderer = $assembly.GetType('TaskbarTelemetry.TaskbarRenderer', $true)
$rate = $renderer.GetMethod('Rate', $staticFlags)
$buildSlots = $renderer.GetMethod('BuildSlots', $staticFlags)
$draw = $renderer.GetMethod('Draw', $staticFlags)
$script:assertions = 0

function Assert-Display([bool]$Passed, [string]$Message) {
    $script:assertions++
    if (-not $Passed) { throw $Message }
}
function New-Metric([string]$Name) {
    return [Activator]::CreateInstance($assembly.GetType('TaskbarTelemetry.' + $Name, $true), $true)
}
function Slot-Field($Slot, [string]$Name) {
    return $Slot.GetType().GetField($Name, $instanceFlags).GetValue($Slot)
}

$cases = @(
    @{ Bytes = 0.0; Text = '0.00 Mb' },
    @{ Bytes = 20000.0; Text = '0.16 Mb' },
    @{ Bytes = 145000.0; Text = '1.16 Mb' },
    @{ Bytes = 1000000.0; Text = '8.00 Mb' },
    @{ Bytes = 9.99 * 125000; Text = '9.99 Mb' },
    @{ Bytes = 10.0 * 125000; Text = '10.00 Mb' },
    @{ Bytes = 1520000.0; Text = '12.16 Mb' },
    @{ Bytes = 99.99 * 125000; Text = '99.99 Mb' },
    @{ Bytes = 99.995 * 125000; Text = '100.0 Mb' },
    @{ Bytes = 11696250.0; Text = '93.57 Mb' },
    @{ Bytes = 11610000.0; Text = '92.88 Mb' },
    @{ Bytes = 11.74 * 1048576; Text = '98.48 Mb' },
    @{ Bytes = 12500000.0; Text = '100.0 Mb' },
    @{ Bytes = 999.94 * 125000; Text = '999.9 Mb' },
    @{ Bytes = 999.95 * 125000; Text = '1.00 Gb' },
    @{ Bytes = 125000000.0; Text = '1.00 Gb' },
    @{ Bytes = 125000000000.0; Text = '1.00 Tb' },
    @{ Bytes = [double]::MaxValue; Text = '>999 Tb' },
    @{ Bytes = $null; Text = '--.-- Mb' },
    @{ Bytes = -1.0; Text = '--.-- Mb' },
    @{ Bytes = [double]::NaN; Text = '--.-- Mb' },
    @{ Bytes = [double]::PositiveInfinity; Text = '--.-- Mb' },
    @{ Bytes = [double]::NegativeInfinity; Text = '--.-- Mb' }
)
foreach ($case in $cases) {
    $actual = $rate.Invoke($null, [object[]]@($case.Bytes))
    Assert-Display ($actual -ceq $case.Text) "Rate mismatch: expected $($case.Text), got $actual"
}

$settings = New-Metric 'AppSettings'
$settings.GetType().GetProperty('CpuAlias').SetValue($settings, 'i9-10980XE', $null)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$profiles = @(
    @{ Dpi = 96; Scale = 1.0 }, @{ Dpi = 120; Scale = 1.25 },
    @{ Dpi = 144; Scale = 1.5 }, @{ Dpi = 192; Scale = 2.0 },
    @{ Dpi = 144; Scale = 1.0 }
)
foreach ($gpuCount in @(0, 1, 2)) {
    $snapshot = New-Metric 'TelemetrySnapshot'
    $snapshot.System.CpuUsagePercent = 100
    $snapshot.System.MemoryUsagePercent = 100
    $snapshot.CpuTemperature.Celsius = 100
    $snapshot.CpuFrequency.Megahertz = 3900
    for ($i = 0; $i -lt $gpuCount; $i++) {
        $gpu = New-Metric 'GpuMetric'
        $gpu.IsNvidiaDevice = $true
        $gpu.StableId = 'network-display-fixture-' + $i
        $gpu.DisplayIndex = $i
        $gpu.MemoryUsedBytes = [ulong](999 * 1073741824L)
        $gpu.UsagePercent = 100
        $gpu.TemperatureCelsius = 100
        $snapshot.Gpus.Add($gpu)
    }
    foreach ($profile in $profiles) {
        $referenceWidth = if ($gpuCount -ge 2) { 520 } else { 476 }
        $bitmap = New-Object Drawing.Bitmap ([int]($referenceWidth * $profile.Scale)),([int](40 * $profile.Dpi / 96))
        $bitmap.SetResolution($profile.Dpi, $profile.Dpi)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $font = New-Object Drawing.Font $settings.FontFamily,$settings.FontSizePoints
        $cpuFont = New-Object Drawing.Font 'Consolas',$settings.FontSizePoints
        $format = [Drawing.StringFormat]::GenericTypographic
        $format.FormatFlags = $format.FormatFlags -bor [Drawing.StringFormatFlags]::MeasureTrailingSpaces
        try {
            $originalBounds = @{}
            foreach ($case in $cases) {
                $snapshot.System.UploadBytesPerSecond = if ($null -eq $case.Bytes) { [double]::NaN } else { $case.Bytes }
                $snapshot.System.DownloadBytesPerSecond = $snapshot.System.UploadBytesPerSecond
                $slots = $buildSlots.Invoke($null, [object[]]@($graphics.PSObject.BaseObject, $bitmap.Size,
                    $font.PSObject.BaseObject, $cpuFont.PSObject.BaseObject,
                    $settings.PSObject.BaseObject, $snapshot.PSObject.BaseObject, $null))
                foreach ($slot in $slots) {
                    $id = Slot-Field $slot 'Id'
                    $bounds = Slot-Field $slot 'Bounds'
                    $text = Slot-Field $slot 'Text'
                    $slotFont = if (Slot-Field $slot 'CpuFont') { $cpuFont } else { $font }
                    $measured = $graphics.MeasureString($text, $slotFont, 1000, $format)
                    $context = "$gpuCount GPU / $($profile.Dpi) DPI / scale $($profile.Scale) / $id / $text"
                    Assert-Display ($measured.Width -le $bounds.Width + 0.2 -and $measured.Height -le $bounds.Height + 0.2) "Clipped: $context"
                    Assert-Display ($bounds.Left -ge 0 -and $bounds.Right -le $bitmap.Width + 0.2) "Outside window: $context"
                    if ($originalBounds.ContainsKey($id)) {
                        Assert-Display ($bounds -eq $originalBounds[$id]) "Moving slot: $context"
                    } else { $originalBounds[$id] = $bounds }
                    if ($id -like 'network.*.unit') {
                        Assert-Display ($text -ceq $case.Text.Split(' ')[1]) "Wrong unit: $context"
                        if ($gpuCount -ge 2) {
                            $halfRight = if ($id -like 'network.upload*') { 100 * $profile.Scale } else { 200 * $profile.Scale }
                            Assert-Display ($bounds.Right -le $halfRight - 1 * $profile.Scale) "Network gutter too small: $context"
                        }
                    }
                }
                for ($a = 0; $a -lt $slots.Count; $a++) {
                    if ([string]::IsNullOrWhiteSpace((Slot-Field $slots[$a] 'Text'))) { continue }
                    for ($b = $a + 1; $b -lt $slots.Count; $b++) {
                        if ([string]::IsNullOrWhiteSpace((Slot-Field $slots[$b] 'Text'))) { continue }
                        $overlap = [Drawing.RectangleF]::Intersect((Slot-Field $slots[$a] 'Bounds'), (Slot-Field $slots[$b] 'Bounds'))
                        Assert-Display ($overlap.Width -le 0.2 -or $overlap.Height -le 0.2) "Overlapping slots: $(Slot-Field $slots[$a] 'Id') / $(Slot-Field $slots[$b] 'Id')"
                    }
                }
            }
            if ($profile.Dpi -eq 144 -and $profile.Scale -eq 1 -and $gpuCount -gt 0) {
                $snapshot.System.UploadBytesPerSecond = 20000
                $snapshot.System.DownloadBytesPerSecond = 1520000
                foreach ($theme in @('dark', 'light')) {
                    $background = if ($theme -eq 'dark') { [Drawing.Color]::FromArgb(31,34,39) } else { [Drawing.Color]::FromArgb(243,243,243) }
                    $foreground = if ($theme -eq 'dark') { [Drawing.Color]::White } else { [Drawing.Color]::FromArgb(26,26,26) }
                    $graphics.Clear($background)
                    $draw.Invoke($null, [object[]]@($graphics.PSObject.BaseObject, $bitmap.Size,
                        $font.PSObject.BaseObject, $foreground,
                        $settings.PSObject.BaseObject, $snapshot.PSObject.BaseObject, $null))
                    $bitmap.Save((Join-Path $OutputDirectory "gpu$gpuCount-$theme-144dpi.png"), [Drawing.Imaging.ImageFormat]::Png)
                }
            }
        } finally {
            $format.Dispose(); $cpuFont.Dispose(); $font.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
        }
    }
}
Write-Host "Network display PASS: $script:assertions assertions. Production renderer only; not a desktop/hardware test."
