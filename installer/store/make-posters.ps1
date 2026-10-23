# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Pablo Pramparo
<#
.SYNOPSIS
  Genera los posters 2:3 de la ficha de la Store (installer\store\StoreArt\poster-*.png).
.DESCRIPTION
  Logo, subtítulo y personajes sobre un escenario de neón. Usa solo avatares que van en la edición Store
  (no los de src\Kantor.Core\Characters\store-excluded.txt). Se ejecuta a mano; los PNG van al repositorio.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$logo = [Drawing.Image]::FromFile((Join-Path $root 'src\Kantor.App\Assets\Branding\kantaria-logo.png'))
$chars = Join-Path $root 'src\Kantor.App\Assets\Characters'
$store = Join-Path $PSScriptRoot 'StoreArt'
New-Item -ItemType Directory -Force $store | Out-Null

$fonts = New-Object Drawing.Text.PrivateFontCollection
$fonts.AddFontFile((Join-Path $root 'src\Kantor.App\Assets\Fonts\Jersey15-Regular.ttf'))
$family = $fonts.Families[0]

$W = 1440; $H = 2160
$bmp = New-Object Drawing.Bitmap $W, $H
$g = [Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
$g.TextRenderingHint = 'AntiAliasGridFit'

# cielo
$rect = New-Object Drawing.Rectangle 0, 0, $W, $H
$sky = New-Object Drawing.Drawing2D.LinearGradientBrush($rect, [Drawing.Color]::FromArgb(7, 10, 36), [Drawing.Color]::FromArgb(74, 20, 104), 90)
$g.FillRectangle($sky, 0, 0, $W, $H)

# halo detrás del logo
$path = New-Object Drawing.Drawing2D.GraphicsPath
$path.AddEllipse(-100, 40, $W + 200, 900)
$glow = New-Object Drawing.Drawing2D.PathGradientBrush($path)
$glow.CenterColor = [Drawing.Color]::FromArgb(110, 255, 61, 184)
$glow.SurroundColors = @([Drawing.Color]::FromArgb(0, 255, 61, 184))
$g.FillPath($glow, $path)

# estrellas (siempre las mismas)
$rnd = New-Object Random 7
$cols = @([Drawing.Color]::FromArgb(25, 211, 255), [Drawing.Color]::FromArgb(255, 61, 184), [Drawing.Color]::FromArgb(255, 200, 60))
for ($i = 0; $i -lt 70; $i++) {
    $x = $rnd.Next(30, $W - 30); $y = $rnd.Next(30, 1200); $s = $rnd.Next(3, 8) * 2
    $c = $cols[$rnd.Next(0, 3)]
    $b = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb($rnd.Next(120, 255), $c))
    $g.FillRectangle($b, $x - $s / 2, $y - 2, $s, 4)
    $g.FillRectangle($b, $x - 2, $y - $s / 2, 4, $s)
}

# piso de neón con perspectiva
$horizon = 1180
$floor = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Rectangle 0, $horizon, $W, ($H - $horizon)), [Drawing.Color]::FromArgb(40, 12, 80), [Drawing.Color]::FromArgb(14, 6, 40), 90)
$g.FillRectangle($floor, 0, $horizon, $W, $H - $horizon)
$penH = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(200, 255, 61, 184)), 4
$penV = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(150, 25, 211, 255)), 3
$g.DrawLine($penH, 0, $horizon, $W, $horizon)
for ($k = 1; $k -le 9; $k++) {
    $t = [Math]::Pow($k / 9.0, 2.2)
    $y = $horizon + $t * ($H - $horizon)
    $g.DrawLine($penH, 0, $y, $W, $y)
}
for ($k = -14; $k -le 14; $k++) {
    $g.DrawLine($penV, $W / 2 + $k * 40, $horizon, $W / 2 + $k * 330, $H)
}

# logo
$lw = 1320; $lh = [int]($lw * $logo.Height / $logo.Width)
$g.DrawImage($logo, [int](($W - $lw) / 2), 150, $lw, $lh)

# subtítulo
$fmt = New-Object Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
$text = ('KARAOKE  ' + [char]0x00B7 + '  SING & SCORE')
$size = 96
$gp = New-Object Drawing.Drawing2D.GraphicsPath
$gp.AddString($text, $family, 0, $size, (New-Object Drawing.RectangleF 0, 735, $W, 130), $fmt)
$g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(7, 10, 36)), 14), $gp)
$g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 214, 64))), $gp)

# personajes: fila de atrás y de adelante, con sombra en el piso
function Draw-Char([string]$id, [int]$cx, [int]$feet, [int]$size) {
    $img = [Drawing.Image]::FromFile((Join-Path $chars "$id\fullbody.png"))
    $shadow = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(110, 0, 0, 0))
    $g.FillEllipse($shadow, $cx - $size * 0.30, $feet - $size * 0.05, $size * 0.60, $size * 0.10)
    # los PNG tienen margen transparente abajo: los pies quedan ~6 % por encima del borde
    $g.DrawImage($img, $cx - $size / 2, $feet - $size * 0.94, $size, $size)
    $img.Dispose()
}
foreach ($c in @(@('avatar_034', 250), @('avatar_003', 560), @('avatar_041', 880), @('avatar_042', 1190))) { Draw-Char $c[0] $c[1] 1640 560 }
Draw-Char 'avatar_015' 260 2000 760
Draw-Char 'avatar_024' 1180 2000 760
Draw-Char 'avatar_040' 720 2060 900

$g.Dispose()
$bmp.Save((Join-Path $store 'poster-1440x2160.png'), [Drawing.Imaging.ImageFormat]::Png)

$small = New-Object Drawing.Bitmap 720, 1080
$g2 = [Drawing.Graphics]::FromImage($small)
$g2.InterpolationMode = 'HighQualityBicubic'; $g2.PixelOffsetMode = 'HighQuality'
$g2.DrawImage($bmp, 0, 0, 720, 1080)
$g2.Dispose()
$small.Save((Join-Path $store 'poster-720x1080.png'), [Drawing.Imaging.ImageFormat]::Png)
$small.Dispose(); $bmp.Dispose(); $logo.Dispose()
Write-Host "Posters en $store"
