<#
.SYNOPSIS
    Generates the application icon (installer\assets\app.ico).

.DESCRIPTION
    Draws a rounded dark square (#0E1116) with a tachometer-style arc made of 8 segments that run green, amber, red
    (shift-light colors), a short needle and a hub dot. Sizes 16, 24, 32, 48, 64, 128 and 256 are PNG-encoded and
    written as a standard ICONDIR. Only System.Drawing is used; the art is original and contains no trademark.
    Deterministic: no timestamps and fixed rendering hints.

.PARAMETER OutputPath
    Target .ico file. When omitted, app.ico next to this script.
#>
[CmdletBinding()]
param(
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
if (-not $OutputPath) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $OutputPath = Join-Path $scriptDirectory 'app.ico'
}
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$background = [System.Drawing.ColorTranslator]::FromHtml('#0E1116')
$rim = [System.Drawing.ColorTranslator]::FromHtml('#2A313C')
$green = [System.Drawing.ColorTranslator]::FromHtml('#2FD27A')
$amber = [System.Drawing.ColorTranslator]::FromHtml('#FFB020')
$red = [System.Drawing.ColorTranslator]::FromHtml('#FF4D4F')
$needleColor = [System.Drawing.ColorTranslator]::FromHtml('#F2F5F9')
$segmentColors = @($green, $green, $green, $amber, $amber, $amber, $red, $red)

function New-RoundedRectanglePath([single] $x, [single] $y, [single] $w, [single] $h, [single] $r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconFramePng([int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $radius = [single]($size * 0.22)
        $square = New-RoundedRectanglePath 0 0 ($size - 1) ($size - 1) $radius
        $fill = New-Object System.Drawing.SolidBrush($background)
        $graphics.FillPath($fill, $square)
        if ($size -ge 32) {
            $rimPen = New-Object System.Drawing.Pen($rim, [single][Math]::Max(1, $size / 64))
            $graphics.DrawPath($rimPen, $square)
            $rimPen.Dispose()
        }

        # Tach arc: 270 degrees, from 135 (bottom-left) clockwise to 45 (bottom-right), 8 segments with small gaps.
        $center = [single](($size - 1) / 2)
        $arcRadius = [single]($size * 0.34)
        $stroke = [single][Math]::Max(2, $size * 0.11)
        $sweepTotal = 270.0
        $gap = if ($size -ge 32) { 5.0 } else { 3.0 }
        $segmentSweep = ($sweepTotal / $segmentColors.Count)
        $box = New-Object System.Drawing.RectangleF(([single]($center - $arcRadius)), ([single]($center - $arcRadius)), ([single](2 * $arcRadius)), ([single](2 * $arcRadius)))
        for ($i = 0; $i -lt $segmentColors.Count; $i++) {
            $pen = New-Object System.Drawing.Pen($segmentColors[$i], $stroke)
            $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
            $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
            $start = [single](135.0 + $i * $segmentSweep + $gap / 2)
            $sweep = [single]($segmentSweep - $gap)
            $graphics.DrawArc($pen, $box, $start, $sweep)
            $pen.Dispose()
        }

        # Needle pointing to the amber zone (angle 270 + 20 degrees is up-right), plus hub.
        $angle = [Math]::PI * (-60.0 / 180.0)
        $needleLength = [single]($arcRadius * 0.82)
        $needlePen = New-Object System.Drawing.Pen($needleColor, [single][Math]::Max(1.5, $size * 0.05))
        $needlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $needlePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $tipX = [single]($center + [Math]::Cos($angle) * $needleLength)
        $tipY = [single]($center + [Math]::Sin($angle) * $needleLength)
        $graphics.DrawLine($needlePen, $center, $center, $tipX, $tipY)
        $hubRadius = [single][Math]::Max(1.5, $size * 0.06)
        $hubBrush = New-Object System.Drawing.SolidBrush($needleColor)
        $graphics.FillEllipse($hubBrush, [single]($center - $hubRadius), [single]($center - $hubRadius), [single](2 * $hubRadius), [single](2 * $hubRadius))
        $hubBrush.Dispose(); $needlePen.Dispose(); $fill.Dispose(); $square.Dispose()
    }
    finally {
        $graphics.Dispose()
    }

    $stream = New-Object System.IO.MemoryStream
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $stream.ToArray()
    }
    finally {
        $stream.Dispose(); $bitmap.Dispose()
    }
}

$frames = foreach ($size in $sizes) { , (New-IconFramePng $size) }

# ICONDIR: reserved 0, type 1 (icon), count; then one 16-byte ICONDIRENTRY per frame; then the PNG payloads.
$output = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($output)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension)    # width (0 means 256)
    $writer.Write([byte]$dimension)    # height
    $writer.Write([byte]0)             # palette colors
    $writer.Write([byte]0)             # reserved
    $writer.Write([uint16]1)           # planes
    $writer.Write([uint16]32)          # bits per pixel
    $writer.Write([uint32]$frames[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Flush()

$fullPath = [IO.Path]::GetFullPath($OutputPath)
$directory = Split-Path -Parent $fullPath
if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
[IO.File]::WriteAllBytes($fullPath, $output.ToArray())
$writer.Dispose(); $output.Dispose()

Write-Host ("Wrote {0} ({1} frames)" -f $OutputPath, $sizes.Count)
