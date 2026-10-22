<#
.SYNOPSIS
  Windows 11'in yeni sağ tık menüsüne (en üst düzey, "Diğer seçenekleri göster" gerekmeden) "Boyut ile analiz et" ekler.
  Boyut.exe yerinde kalır; yanına bir paket kimliği (sparse package) kaydedilir. Yalnızca geçerli kullanıcı için.
  Geliştirici Modu açık olmalı (Ayarlar > Sistem > Geliştiriciler için); imza ve yönetici izni gerekmez.
.EXAMPLE
  ./tools/register-modern-context-menu.ps1                       # derlenmiş Boyut.exe'yi bulur
  ./tools/register-modern-context-menu.ps1 -Exe "D:\Araclar\Boyut\Boyut.exe"
  ./tools/register-modern-context-menu.ps1 -Remove
#>
param(
    [string]$Exe,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if ($Remove) {
    Get-AppxPackage -Name Boyut | Remove-AppxPackage
    Write-Host 'Kaldırıldı.'
    return
}

$devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($devMode -ne 1) {
    throw 'Geliştirici Modu kapalı. Ayarlar > Sistem > Geliştiriciler için > Geliştirici Modu''nu açıp yeniden deneyin.'
}

if (-not $Exe) {
    $found = Get-ChildItem -Path (Join-Path $root 'artifacts\Boyut'), (Join-Path $root 'src\Boyut.App\bin') `
        -Recurse -Filter Boyut.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { throw 'Boyut.exe bulunamadı; önce ./build.ps1 çalıştırın ya da -Exe ile yolunu verin.' }
    $Exe = $found.FullName
}
$exeDir = Split-Path (Resolve-Path $Exe) -Parent

# The loose package root holds only the manifest and its logo; Boyut.exe stays where it is (external location).
$package = Join-Path $root 'artifacts\context-menu'
New-Item -ItemType Directory -Force -Path (Join-Path $package 'Assets') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'context-menu\AppxManifest.xml') $package -Force
Copy-Item (Join-Path $exeDir 'Assets\Boyut.png') (Join-Path $package 'Assets') -Force

Get-AppxPackage -Name Boyut | Remove-AppxPackage
Add-AppxPackage -Register (Join-Path $package 'AppxManifest.xml') -ExternalLocation $exeDir
Write-Host "Eklendi: $Exe"
Write-Host 'Menü görünmezse Gezgin''i yeniden başlatın (Görev Yöneticisi > Windows Gezgini > Yeniden başlat).'
