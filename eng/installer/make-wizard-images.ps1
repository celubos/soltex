# Generates the installer wizard images from docs/images/logo.png:
#   wizard-100.bmp / wizard-200.bmp  (welcome and finish pages, 164x314 at 100% and 328x628 at 200%)
#   small-100.bmp / small-200.bmp    (top corner of the other pages, 55x55 and 110x110)
# All are 32-bit BMPs with premultiplied alpha (soltex.iss sets WizardImageAlphaFormat=premultiplied).

[CmdletBinding()]
param([string] $OutputDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not $OutputDirectory) { $OutputDirectory = $PSScriptRoot }

$logoPath = Join-Path $PSScriptRoot '..\..\docs\images\logo.png'
$logo = [System.Drawing.Image]::FromFile((Resolve-Path $logoPath))

function Save-PremultipliedBmp {
    param([System.Drawing.Bitmap] $Bitmap, [string] $Path)
    $w = $Bitmap.Width; $h = $Bitmap.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $pixels = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    }
    finally { $Bitmap.UnlockBits($data) }
    $rowBytes = $w * 4
    $image = New-Object byte[] ($rowBytes * $h)
    for ($y = 0; $y -lt $h; $y++) {
        $src = $y * $stride
        $dst = ($h - 1 - $y) * $rowBytes   # bottom-up rows
        for ($x = 0; $x -lt $w; $x++) {
            $i = $src + $x * 4; $o = $dst + $x * 4
            $a = $pixels[$i + 3]
            $image[$o]     = [byte][math]::Round($pixels[$i]     * $a / 255)
            $image[$o + 1] = [byte][math]::Round($pixels[$i + 1] * $a / 255)
            $image[$o + 2] = [byte][math]::Round($pixels[$i + 2] * $a / 255)
            $image[$o + 3] = $a
        }
    }
    $stream = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([byte[]][char[]]'BM'); $writer.Write([int](54 + $image.Length)); $writer.Write([int]0); $writer.Write([int]54)
        $writer.Write([int]40); $writer.Write([int]$w); $writer.Write([int]$h); $writer.Write([int16]1); $writer.Write([int16]32)
        $writer.Write([int]0); $writer.Write([int]$image.Length); $writer.Write([int]2835); $writer.Write([int]2835); $writer.Write([int]0); $writer.Write([int]0)
        $writer.Write($image)
    }
    finally { $writer.Dispose() }
}

function New-Canvas([int] $w, [int] $h) {
    $bitmap = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return @($bitmap, $g)
}

foreach ($scale in 1, 2) {
    # Welcome/finish panel: the app's dark ground, the logo and the name.
    $w = 164 * $scale; $h = 314 * $scale
    $bitmap, $g = New-Canvas $w $h
    $top = [System.Drawing.Color]::FromArgb(255, 24, 26, 34); $bottom = [System.Drawing.Color]::FromArgb(255, 11, 13, 18)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle(0, 0, $w, $h)), $top, $bottom, 90)
    $g.FillRectangle($brush, 0, 0, $w, $h); $brush.Dispose()
    $size = 88 * $scale
    $g.DrawImage($logo, [int](($w - $size) / 2), [int](92 * $scale), $size, $size)
    $font = New-Object System.Drawing.Font('Segoe UI Semibold', [single](15 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat; $format.Alignment = 'Center'
    $text = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 236, 238, 244))
    $g.DrawString('Soltex', $font, $text, (New-Object System.Drawing.RectangleF(0, [single](196 * $scale), $w, [single](30 * $scale))), $format)
    $font.Dispose(); $text.Dispose(); $g.Dispose()
    Save-PremultipliedBmp $bitmap (Join-Path $OutputDirectory ("wizard-{0}.bmp" -f (100 * $scale)))
    $bitmap.Dispose()

    # Small corner image: the logo alone, transparent around its rounded square.
    $s = 55 * $scale
    $bitmap, $g = New-Canvas $s $s
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($logo, 0, 0, $s, $s)
    $g.Dispose()
    Save-PremultipliedBmp $bitmap (Join-Path $OutputDirectory ("small-{0}.bmp" -f (100 * $scale)))
    $bitmap.Dispose()
}
$logo.Dispose()
Get-ChildItem $OutputDirectory -Filter *.bmp | Select-Object Name, Length
