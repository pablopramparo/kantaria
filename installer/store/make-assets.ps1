# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Pablo Pramparo
<#
.SYNOPSIS
  Genera los logos del paquete MSIX (installer\store\Assets) a partir del logo de Kantaria.
.DESCRIPTION
  Cuadrados (44, 150, tienda): el logo completo sobre el azul noche de la interfaz. Ancho (310x150): el logo
  completo. Se generan a escala 100, 200 y 400 y, para el ícono de 44 px, los tamaños fijos que usa Windows
  (barra de tareas, inicio). Se ejecuta a mano cuando cambia el logo; los PNG van al repositorio.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$src = [Drawing.Image]::FromFile((Join-Path $root 'src\Kantor.App\Assets\Branding\kantaria-logo.png'))
$out = Join-Path $PSScriptRoot 'Assets'
New-Item -ItemType Directory -Force $out | Out-Null
$night = [Drawing.Color]::FromArgb(7, 10, 36)

function New-Canvas([int]$w, [int]$h, [scriptblock]$draw, [string]$file) {
    $bmp = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear($night)
    & $draw $g $w $h
    $g.Dispose()
    $bmp.Save((Join-Path $out $file), [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# cuadrados: el logo completo, ajustado al ancho y centrado (recortar la K deja la A cortada)
$square = {
    param($g, $w, $h)
    $pad = [int]([Math]::Max(1, $w * 0.05))
    $iw = $w - 2 * $pad; $ih = [int]($iw * $src.Height / $src.Width)
    $g.DrawImage($src, $pad, [int](($h - $ih) / 2), $iw, $ih)
}
$wide = {
    param($g, $w, $h)
    $pad = [int]($h * 0.08)
    $ratio = $src.Width / $src.Height
    $ih = $h - 2 * $pad; $iw = [int]($ih * $ratio)
    if ($iw -gt $w - 2 * $pad) { $iw = $w - 2 * $pad; $ih = [int]($iw / $ratio) }
    $g.DrawImage($src, [int](($w - $iw) / 2), [int](($h - $ih) / 2), $iw, $ih)
}

foreach ($scale in 100, 200, 400) {
    $f = $scale / 100
    New-Canvas ([int](44 * $f)) ([int](44 * $f)) $square "Square44x44Logo.scale-$scale.png"
    New-Canvas ([int](150 * $f)) ([int](150 * $f)) $square "Square150x150Logo.scale-$scale.png"
    New-Canvas ([int](310 * $f)) ([int](150 * $f)) $wide "Wide310x150Logo.scale-$scale.png"
    New-Canvas ([int](50 * $f)) ([int](50 * $f)) $square "StoreLogo.scale-$scale.png"
}
# sin calificador: los nombres que cita el manifiesto (makeappx los exige si no hay resources.pri); a escala 100
New-Canvas 44 44 $square "Square44x44Logo.png"
New-Canvas 150 150 $square "Square150x150Logo.png"
New-Canvas 310 150 $wide "Wide310x150Logo.png"
New-Canvas 50 50 $square "StoreLogo.png"
foreach ($size in 16, 24, 32, 48, 256) {
    New-Canvas $size $size $square "Square44x44Logo.targetsize-$size.png"
    New-Canvas $size $size $square "Square44x44Logo.targetsize-${size}_altform-unplated.png"
}
$src.Dispose()
Write-Host "Logos generados en $out"

# ---- Logotipos de la ficha de la Store (Partner Center): arte de caja 1:1 y póster 2:3 (solo Xbox, opcional) ----
$src = [Drawing.Image]::FromFile((Join-Path $root 'src\Kantor.App\Assets\Branding\kantaria-logo.png'))
$store = Join-Path $PSScriptRoot 'StoreArt'
New-Item -ItemType Directory -Force $store | Out-Null
function New-StoreArt([int]$w, [int]$h, [string]$file) {
    $bmp = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
    $top = [Drawing.Color]::FromArgb(7, 10, 36); $bottom = [Drawing.Color]::FromArgb(36, 18, 82)
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Rectangle 0, 0, $w, $h), $top, $bottom, 90)
    $g.FillRectangle($brush, 0, 0, $w, $h)
    $pad = [int]($w * 0.06)
    $iw = $w - 2 * $pad; $ih = [int]($iw * $src.Height / $src.Width)
    $g.DrawImage($src, $pad, [int](($h - $ih) / 2), $iw, $ih)
    $g.Dispose()
    $bmp.Save((Join-Path $store $file), [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
New-StoreArt 1080 1080 'box-1080x1080.png'
New-StoreArt 2160 2160 'box-2160x2160.png'
New-StoreArt 720 1080 'poster-720x1080.png'
New-StoreArt 1440 2160 'poster-1440x2160.png'
$src.Dispose()
Write-Host "Arte de la ficha en $store"
