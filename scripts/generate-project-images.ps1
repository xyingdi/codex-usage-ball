[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$imageDirectory = Join-Path $repoRoot "docs\images"
$usagePanelPath = Join-Path $imageDirectory "usage-panel.png"
$ballPath = Join-Path $imageDirectory "current-ball.png"
$outputPath = Join-Path $imageDirectory "social-preview.png"

$canvas = New-Object System.Drawing.Bitmap 1280, 640
$graphics = [System.Drawing.Graphics]::FromImage($canvas)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

try {
    $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Rectangle 0, 0, 1280, 640),
        ([System.Drawing.Color]::FromArgb(255, 14, 22, 22)),
        ([System.Drawing.Color]::FromArgb(255, 23, 74, 77)),
        18
    )
    $graphics.FillRectangle($background, 0, 0, 1280, 640)
    $background.Dispose()

    $glowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(24, 185, 245, 200))
    $graphics.FillEllipse($glowBrush, 860, -220, 650, 650)
    $graphics.FillEllipse($glowBrush, -230, 430, 520, 520)
    $glowBrush.Dispose()

    $eyebrowFont = New-Object System.Drawing.Font "Microsoft YaHei UI", 15, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $titleFont = New-Object System.Drawing.Font "Microsoft YaHei UI", 56, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $subtitleFont = New-Object System.Drawing.Font "Microsoft YaHei UI", 24, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $smallFont = New-Object System.Drawing.Font "Microsoft YaHei UI", 17, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(248, 248, 244))
    $mint = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(185, 245, 200))
    $muted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(190, 202, 201, 194))

    $graphics.DrawString("CODEX USAGE BALL  ·  v1.8.7", $eyebrowFont, $mint, 72, 94)
    $graphics.DrawString("Codex 用量悬浮球", $titleFont, $white, 66, 139)
    $graphics.DrawString("剩余额度，一眼就懂。", $subtitleFont, $white, 72, 236)
    $graphics.DrawString("Windows 10/11  ·  便携单文件  ·  本地读取", $smallFont, $muted, 73, 292)

    $pillBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(34, 185, 245, 200))
    $pillPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(120, 185, 245, 200)), 1
    $pillRect = New-Object System.Drawing.RectangleF 72, 345, 242, 47
    $pillPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $radius = 20
    $pillPath.AddArc($pillRect.X, $pillRect.Y, $radius, $radius, 180, 90)
    $pillPath.AddArc($pillRect.Right - $radius, $pillRect.Y, $radius, $radius, 270, 90)
    $pillPath.AddArc($pillRect.Right - $radius, $pillRect.Bottom - $radius, $radius, $radius, 0, 90)
    $pillPath.AddArc($pillRect.X, $pillRect.Bottom - $radius, $radius, $radius, 90, 90)
    $pillPath.CloseFigure()
    $graphics.FillPath($pillBrush, $pillPath)
    $graphics.DrawPath($pillPen, $pillPath)
    $graphics.DrawString("开源 · 免费 · 轻量", $smallFont, $mint, 97, 356)

    $panelImage = [System.Drawing.Image]::FromFile($usagePanelPath)
    try {
        $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(65, 0, 0, 0))
        $graphics.FillRectangle($shadow, 642, 104, 584, 421)
        $shadow.Dispose()
        $graphics.DrawImage($panelImage, 624, 82, 572, 413)
    }
    finally {
        $panelImage.Dispose()
    }

    $ballImage = [System.Drawing.Image]::FromFile($ballPath)
    try {
        $graphics.DrawImage($ballImage, 1032, 432, 160, 160)
    }
    finally {
        $ballImage.Dispose()
    }

    $graphics.DrawString("悬停看全部额度", $smallFont, $muted, 676, 545)

    $canvas.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    foreach ($resource in @($graphics, $canvas, $eyebrowFont, $titleFont, $subtitleFont, $smallFont, $white, $mint, $muted, $pillBrush, $pillPen, $pillPath)) {
        if ($null -ne $resource) { $resource.Dispose() }
    }
}

Write-Host "Created $outputPath"
