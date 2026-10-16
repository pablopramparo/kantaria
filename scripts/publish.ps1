# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Pablo Pramparo
<#
.SYNOPSIS
  Arma la distribución de Kantor para Windows x64: carpeta autocontenida (no requiere instalar .NET) y .zip.

.DESCRIPTION
  Salida: dist\Kantor-<versión>-win-x64\ y dist\Kantor-<versión>-win-x64.zip.
  Con -Installer también arma dist\Kantor-Setup-<versión>.exe (requiere Inno Setup 6:
  winget install JRSoftware.InnoSetup).
  El SDK de .NET 10 no está en el PATH global: se toma de $env:DOTNET_ROOT, de $env:USERPROFILE\.dotnet o del PATH.
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$NoZip,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$dotnetHome = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { Join-Path $env:USERPROFILE '.dotnet' }
if (Test-Path (Join-Path $dotnetHome 'dotnet.exe')) {
    $env:DOTNET_ROOT = $dotnetHome
    $env:PATH = "$dotnetHome;$env:PATH"
}

$project = Join-Path $root 'src\Kantor.App\Kantor.App.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No se encontró <Version> en Kantor.App.csproj.' }

$name = "Kantor-$version-win-x64"
$out = Join-Path $root "dist\$name"
$zip = Join-Path $root "dist\$name.zip"

if (-not $SkipTests) {
    Write-Host '== Tests ==' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'Kantor.sln') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Los tests fallaron: no se genera la distribución.' }
}

Write-Host "== Publicando $name ==" -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falló.' }

# Símbolos de depuración: no van al usuario final.
Get-ChildItem $out -Filter *.pdb | Remove-Item -Force

foreach ($file in 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item (Join-Path $root $file) $out -Force
}
Copy-Item (Join-Path $root 'src\Kantor.App\Assets\LICENSE-ASSETS.md') $out -Force

if (-not $NoZip) {
    # ZipFile en vez de Compress-Archive: este último falla si el antivirus tiene un archivo abierto un instante.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    for ($attempt = 1; ; $attempt++) {
        if (Test-Path $zip) { Remove-Item $zip -Force }
        try {
            [System.IO.Compression.ZipFile]::CreateFromDirectory($out, $zip,
                [System.IO.Compression.CompressionLevel]::Optimal, $false)
            break
        }
        catch [System.IO.IOException] {
            if ($attempt -ge 5) { throw }
            Start-Sleep -Seconds 3
        }
    }
}

$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Carpeta: {0}  ({1:N0} MB)" -f $out, $size) -ForegroundColor Green
if (-not $NoZip) {
    Write-Host ("Zip:     {0}  ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
}

if ($Installer) {
    $candidates = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }
    if (-not $candidates) { throw 'No se encontró Inno Setup 6 (winget install JRSoftware.InnoSetup).' }

    Write-Host '== Instalador ==' -ForegroundColor Cyan
    & ($candidates | Select-Object -First 1) "/DAppVersion=$version" "/DSourceDir=$out" "/DOutputDir=$(Join-Path $root 'dist')" "/DRepoDir=$root" (Join-Path $root 'installer\kantor.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup falló.' }
    $setup = Join-Path $root "dist\Kantor-Setup-$version.exe"
    Write-Host ("Instalador: {0}  ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
}
