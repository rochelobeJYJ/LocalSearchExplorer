param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\LocalSearch.App\Assets')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-IconBitmap {
    param([int]$Size)

    $scale = $Size / 256.0
    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform($scale, $scale)

    $shadow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(34, 15, 23, 42))
    $folder = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.RectangleF]::new(32, 48, 192, 150),
        [System.Drawing.Color]::FromArgb(96, 165, 250),
        [System.Drawing.Color]::FromArgb(37, 99, 235),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $shine = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(62, 239, 246, 255))
    $lens = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.RectangleF]::new(106, 94, 88, 88),
        [System.Drawing.Color]::FromArgb(248, 250, 252),
        [System.Drawing.Color]::FromArgb(219, 234, 254),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $outlinePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(15, 23, 42), 12)
    $handlePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(15, 23, 42), 17)
    $handlePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handleAccentPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(37, 99, 235), 10)
    $handleAccentPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handleAccentPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $dotBlue = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(37, 99, 235))
    $dotGray = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(148, 163, 184))

    $shadowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shadowPath.AddRectangle([System.Drawing.RectangleF]::new(32, 72, 192, 130))
    $graphics.FillPath($shadow, $shadowPath)

    $folderPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $folderPath.AddPolygon([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(52, 48),
        [System.Drawing.PointF]::new(102, 48),
        [System.Drawing.PointF]::new(128, 76),
        [System.Drawing.PointF]::new(204, 76),
        [System.Drawing.PointF]::new(224, 96),
        [System.Drawing.PointF]::new(224, 178),
        [System.Drawing.PointF]::new(204, 198),
        [System.Drawing.PointF]::new(52, 198),
        [System.Drawing.PointF]::new(32, 178),
        [System.Drawing.PointF]::new(32, 68)
    ))
    $graphics.FillPath($folder, $folderPath)
    $graphics.FillRectangle($shine, 40, 88, 176, 100)

    $graphics.FillEllipse($lens, 107, 96, 86, 86)
    $graphics.DrawEllipse($outlinePen, 107, 96, 86, 86)
    $graphics.DrawLine($handlePen, 180.5, 169.5, 214, 203)
    $graphics.DrawLine($handleAccentPen, 180.5, 169.5, 214, 203)

    if ($Size -ge 32) {
        $graphics.FillEllipse($dotBlue, 126, 127, 12, 12)
        $graphics.FillEllipse($dotBlue, 144, 127, 12, 12)
        $graphics.FillEllipse($dotBlue, 162, 127, 12, 12)
        if ($Size -ge 48) {
            $graphics.FillEllipse($dotGray, 136, 148, 10, 10)
            $graphics.FillEllipse($dotGray, 154, 148, 10, 10)
        }
    }

    $graphics.Dispose()
    $shadow.Dispose()
    $folder.Dispose()
    $shine.Dispose()
    $lens.Dispose()
    $outlinePen.Dispose()
    $handlePen.Dispose()
    $handleAccentPen.Dispose()
    $dotBlue.Dispose()
    $dotGray.Dispose()

    return $bitmap
}

function Convert-BitmapToPngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return $bytes
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = New-Object System.Collections.Generic.List[object]
foreach ($size in $sizes) {
    $bitmap = New-IconBitmap -Size $size
    try {
        $pngPath = Join-Path $OutputDirectory ("AppIcon-{0}.png" -f $size)
        $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add([pscustomobject]@{
            Size = $size
            Bytes = Convert-BitmapToPngBytes -Bitmap $bitmap
        })
    }
    finally {
        $bitmap.Dispose()
    }
}

$icoPath = Join-Path $OutputDirectory 'AppIcon.ico'
$writer = New-Object System.IO.BinaryWriter ([System.IO.File]::Open($icoPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write))
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)

    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $entrySize = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$entrySize)
        $writer.Write([byte]$entrySize)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }

    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame.Bytes)
    }
}
finally {
    $writer.Dispose()
}

Write-Host "Generated $icoPath"
