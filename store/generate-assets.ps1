[CmdletBinding()]
param(
    [string]$AssetDirectory,
    [string]$ListingDirectory
)

$ErrorActionPreference = 'Stop'
$storeRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $storeRoot
if ([string]::IsNullOrWhiteSpace($AssetDirectory)) {
    $AssetDirectory = Join-Path $storeRoot 'Assets'
}
if ([string]::IsNullOrWhiteSpace($ListingDirectory)) {
    $ListingDirectory = Join-Path $storeRoot 'listing-assets'
}

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $AssetDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $ListingDirectory -Force | Out-Null

function New-TelemetryIcon {
    param([int]$Size, [string]$Path)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::FromArgb(255, 15, 108, 189))
        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $soft = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(145, 255, 255, 255), [Math]::Max(1.0, $Size / 34.0))
        $line = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [Math]::Max(1.6, $Size / 18.0))
        $line.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $line.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $margin = [float]($Size * 0.18)
        $chartTop = [float]($Size * 0.24)
        $chartBottom = [float]($Size * 0.64)
        $graphics.DrawLine($soft, $margin, $chartTop, $Size - $margin, $chartTop)
        $graphics.DrawLine($soft, $margin, ($chartTop + $chartBottom) / 2, $Size - $margin, ($chartTop + $chartBottom) / 2)
        $graphics.DrawLine($soft, $margin, $chartBottom, $Size - $margin, $chartBottom)
        $points = @(
            (New-Object System.Drawing.PointF([float]($Size * 0.19), [float]($Size * 0.56))),
            (New-Object System.Drawing.PointF([float]($Size * 0.34), [float]($Size * 0.43))),
            (New-Object System.Drawing.PointF([float]($Size * 0.49), [float]($Size * 0.49))),
            (New-Object System.Drawing.PointF([float]($Size * 0.64), [float]($Size * 0.31))),
            (New-Object System.Drawing.PointF([float]($Size * 0.81), [float]($Size * 0.38)))
        )
        $graphics.DrawLines($line, [System.Drawing.PointF[]]$points)
        $barHeight = [float][Math]::Max(2.0, $Size * 0.065)
        $graphics.FillRectangle($white, $margin, [float]($Size * 0.74), [float]($Size * 0.28), $barHeight)
        $graphics.FillRectangle($white, [float]($Size * 0.52), [float]($Size * 0.74), [float]($Size * 0.30), $barHeight)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $white.Dispose()
        $soft.Dispose()
        $line.Dispose()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function New-ListingScreenshot {
    param([string]$Path)

    $width = 1366
    $height = 768
    $bitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $rectangle = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
        $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rectangle,
            [System.Drawing.Color]::FromArgb(255, 10, 23, 43),
            [System.Drawing.Color]::FromArgb(255, 15, 108, 189),
            18.0)
        $graphics.FillRectangle($gradient, $rectangle)
        $gradient.Dispose()

        $titleFont = New-Object System.Drawing.Font('Microsoft YaHei UI', 34, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Point)
        $subtitleFont = New-Object System.Drawing.Font('Microsoft YaHei UI', 17, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
        $labelFont = New-Object System.Drawing.Font('Microsoft YaHei UI', 13, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $softWhite = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(218, 255, 255, 255))
        $graphics.DrawString('把关键状态放回任务栏', $titleFont, $white, 92, 82)
        $graphics.DrawString('网速、CPU、显存、温度与 Codex 剩余额度，一眼就能看到。', $subtitleFont, $softWhite, 96, 153)

        $card = New-Object System.Drawing.RectangleF(86, 246, 1194, 330)
        $cardBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(225, 21, 29, 43))
        $graphics.FillRectangle($cardBrush, $card)
        $cardBrush.Dispose()
        $graphics.DrawString('双行紧凑布局，不占桌面窗口', $subtitleFont, $white, 128, 284)
        $graphics.DrawString('• 每秒刷新本机系统指标', $labelFont, $softWhite, 130, 344)
        $graphics.DrawString('• 微信额度通知默认关闭，需明确同意后启用', $labelFont, $softWhite, 130, 386)
        $graphics.DrawString('• 不读取 auth.json，不上传会话正文', $labelFont, $softWhite, 130, 428)

        $taskbarBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 31, 31, 31))
        $graphics.FillRectangle($taskbarBrush, 104, 494, 1158, 56)
        $taskbarBrush.Dispose()
        $stripPath = Join-Path $projectRoot 'test-artifacts\final-ui-aligned.png'
        if (Test-Path -LiteralPath $stripPath) {
            $strip = [System.Drawing.Image]::FromFile($stripPath)
            try {
                $targetWidth = 728
                $targetHeight = 70
                $graphics.DrawImage($strip, 323, 487, $targetWidth, $targetHeight)
            }
            finally {
                $strip.Dispose()
            }
        }
        else {
            $sampleFont = New-Object System.Drawing.Font('Microsoft YaHei UI', 12, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
            $graphics.DrawString('↑ 00.00  ↓ 00.00 MB/s  |  GPU0 3GB 2% 44°  |  内存 46%', $sampleFont, $white, 326, 497)
            $graphics.DrawString('i9-10980XE 9% 44°       |  GPU1 1GB 0% 51°  |  Codex 71%', $sampleFont, $white, 326, 522)
            $sampleFont.Dispose()
        }
        $graphics.DrawString('Windows 10/11 · x64', $labelFont, $softWhite, 96, 675)
        $graphics.DrawString('TaskbarTelemetry', $labelFont, $softWhite, 1080, 675)

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $titleFont.Dispose()
        $subtitleFont.Dispose()
        $labelFont.Dispose()
        $white.Dispose()
        $softWhite.Dispose()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-TelemetryIcon -Size 44 -Path (Join-Path $AssetDirectory 'Square44x44Logo.png')
New-TelemetryIcon -Size 150 -Path (Join-Path $AssetDirectory 'Square150x150Logo.png')
New-TelemetryIcon -Size 50 -Path (Join-Path $AssetDirectory 'StoreLogo.png')
New-TelemetryIcon -Size 300 -Path (Join-Path $ListingDirectory 'AppTile300x300.png')

Write-Host "Generated Store assets in $AssetDirectory and $ListingDirectory"
