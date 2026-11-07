# Hodozip MSIX 用の画像アセットを生成する。
# AI 画像生成は使わず、System.Drawing でシンプルなジッパー柄を描画する。
#
# Usage:
#   pwsh -File packaging/generate-assets.ps1 -OutputDirectory packaging/assets

[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'assets')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-RoundedPath {
    param(
        [single]$X,
        [single]$Y,
        [single]$Width,
        [single]$Height,
        [single]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = [Math]::Min($Radius * 2, [Math]::Min($Width, $Height))

    if ($diameter -le 0) {
        $path.AddRectangle([System.Drawing.RectangleF]::new($X, $Y, $Width, $Height))
        return $path
    }

    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-Zipper {
    param(
        [System.Drawing.Graphics]$Graphics,
        [single]$CenterX,
        [single]$CenterY,
        [single]$Width,
        [single]$Height
    )

    $tapeWidth = $Width * 0.36
    $tapeHeight = $Height * 0.82
    $tapeX = $CenterX - ($tapeWidth / 2)
    $tapeY = $CenterY - ($tapeHeight / 2)

    $tapePath = New-RoundedPath $tapeX $tapeY $tapeWidth $tapeHeight ($Width * 0.08)
    $tapeBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F5F8FC'))
    $Graphics.FillPath($tapeBrush, $tapePath)
    $tapeBrush.Dispose()
    $tapePath.Dispose()

    $seamWidth = [Math]::Max(1.5, $Width * 0.035)
    $seamTop = $tapeY + ($tapeHeight * 0.08)
    $seamBottom = $tapeY + ($tapeHeight * 0.60)
    $seamBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#1450A8'))
    $Graphics.FillRectangle($seamBrush, $CenterX - ($seamWidth / 2), $seamTop, $seamWidth, $seamBottom - $seamTop)

    $toothWidth = $tapeWidth * 0.36
    $toothHeight = [Math]::Max(1.2, $tapeHeight * 0.035)
    $toothGap = $tapeHeight * 0.085

    for ($index = 0; $index -lt 5; $index++) {
        $toothY = $seamTop + ($index * $toothGap)
        $toothX = if ($index % 2 -eq 0) {
            $CenterX - ($seamWidth / 2) - $toothWidth
        }
        else {
            $CenterX + ($seamWidth / 2)
        }

        $Graphics.FillRectangle($seamBrush, $toothX, $toothY, $toothWidth, $toothHeight)
    }

    $seamBrush.Dispose()

    $sliderWidth = $tapeWidth * 1.28
    $sliderHeight = $tapeHeight * 0.13
    $sliderX = $CenterX - ($sliderWidth / 2)
    $sliderY = $seamBottom - ($sliderHeight * 0.15)
    $sliderPath = New-RoundedPath $sliderX $sliderY $sliderWidth $sliderHeight ($sliderHeight * 0.35)
    $sliderBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#0B2E63'))
    $Graphics.FillPath($sliderBrush, $sliderPath)
    $sliderBrush.Dispose()
    $sliderPath.Dispose()

    $ringWidth = $tapeWidth * 0.62
    $ringHeight = $tapeHeight * 0.20
    $ringX = $CenterX - ($ringWidth / 2)
    $ringY = $sliderY + ($sliderHeight * 1.05)
    $penWidth = [Math]::Max(1.5, $Width * 0.035)
    $pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#0B2E63'), $penWidth)
    $Graphics.DrawEllipse($pen, $ringX, $ringY, $ringWidth, $ringHeight)
    $Graphics.DrawLine($pen, $CenterX, $sliderY + $sliderHeight, $CenterX, $ringY)
    $pen.Dispose()
}

function New-IconBitmap {
    param(
        [int]$Width,
        [int]$Height,
        [string]$Path,
        [switch]$Wide
    )

    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $radius = if ($Wide) { $Height * 0.12 } else { [Math]::Min($Width, $Height) * 0.20 }
    $backgroundPath = New-RoundedPath 0 0 $Width $Height $radius
    $rectangle = [System.Drawing.RectangleF]::new(0, 0, $Width, $Height)
    $backgroundBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rectangle,
        [System.Drawing.ColorTranslator]::FromHtml('#2B7BE4'),
        [System.Drawing.ColorTranslator]::FromHtml('#0E3A7A'),
        90.0)
    $graphics.FillPath($backgroundBrush, $backgroundPath)
    $backgroundBrush.Dispose()
    $backgroundPath.Dispose()

    $iconSize = if ($Wide) {
        [Math]::Min($Height * 0.70, $Width * 0.34)
    }
    else {
        [Math]::Min($Width, $Height) * 0.62
    }

    Draw-Zipper -Graphics $graphics -CenterX ($Width / 2) -CenterY ($Height / 2) -Width $iconSize -Height $iconSize

    $graphics.Dispose()
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function New-MultiSizeIco {
    param(
        [string]$Path,
        [int[]]$Sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
    )

    $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('HodozipIco_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null

    try {
        $frames = @()
        foreach ($size in $Sizes) {
            $framePath = Join-Path $temporaryDirectory ("$size.png")
            New-IconBitmap -Width $size -Height $size -Path $framePath
            $frames += @{
                Size = $size
                Bytes = [System.IO.File]::ReadAllBytes($framePath)
            }
        }

        $stream = [System.IO.MemoryStream]::new()
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            # ICO header: reserved, type (1 = icon), image count.
            $writer.Write([UInt16]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]$frames.Count)

            # PNG-compressed frames. A dimension byte of 0 means 256.
            $offset = 6 + (16 * $frames.Count)
            foreach ($frame in $frames) {
                $dimension = if ($frame.Size -ge 256) { [Byte]0 } else { [Byte]$frame.Size }
                $writer.Write($dimension)
                $writer.Write($dimension)
                $writer.Write([Byte]0)
                $writer.Write([Byte]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]32)
                $writer.Write([UInt32]$frame.Bytes.Length)
                $writer.Write([UInt32]$offset)
                $offset += $frame.Bytes.Length
            }

            foreach ($frame in $frames) {
                $writer.Write($frame.Bytes)
            }

            $writer.Flush()
            [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
        }
        finally {
            $writer.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function New-SplashBitmap {
    param(
        [int]$Width,
        [int]$Height,
        [string]$Path
    )

    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $rectangle = [System.Drawing.RectangleF]::new(0, 0, $Width, $Height)
    $backgroundBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rectangle,
        [System.Drawing.ColorTranslator]::FromHtml('#2B7BE4'),
        [System.Drawing.ColorTranslator]::FromHtml('#0E3A7A'),
        0.0)
    $graphics.FillRectangle($backgroundBrush, $rectangle)
    $backgroundBrush.Dispose()

    $iconSize = $Height * 0.54
    Draw-Zipper -Graphics $graphics -CenterX ($Width / 2) -CenterY ($Height / 2) -Width $iconSize -Height $iconSize

    $graphics.Dispose()
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

$squareAssets = @(
    @{ Name = 'Square44x44Logo.png'; Size = 44 },
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; Size = 24 },
    @{ Name = 'Square44x44Logo.targetsize-32_altform-unplated.png'; Size = 32 },
    @{ Name = 'Square44x44Logo.targetsize-48_altform-unplated.png'; Size = 48 },
    @{ Name = 'Square71x71Logo.png'; Size = 71 },
    @{ Name = 'Square150x150Logo.png'; Size = 150 },
    @{ Name = 'Square310x310Logo.png'; Size = 310 },
    @{ Name = 'StoreLogo.png'; Size = 50 },
    @{ Name = 'ZipFileLogo.png'; Size = 50 }
)

foreach ($asset in $squareAssets) {
    $path = Join-Path $resolvedOutput $asset.Name
    New-IconBitmap -Width $asset.Size -Height $asset.Size -Path $path
    Write-Host "  生成: $($asset.Name) ($($asset.Size)x$($asset.Size))"
}

$widePath = Join-Path $resolvedOutput 'Wide310x150Logo.png'
New-IconBitmap -Width 310 -Height 150 -Path $widePath -Wide
Write-Host '  生成: Wide310x150Logo.png (310x150)'

$splashPath = Join-Path $resolvedOutput 'SplashScreen.png'
New-SplashBitmap -Width 620 -Height 300 -Path $splashPath
Write-Host '  生成: SplashScreen.png (620x300)'

$icoPath = Join-Path $resolvedOutput 'Hodozip.ico'
New-MultiSizeIco -Path $icoPath
Write-Host '  生成: Hodozip.ico (16/20/24/32/48/64/128/256)'

Write-Host "MSIX アセットを生成しました: $resolvedOutput"
