<#
.SYNOPSIS
  Gezgin'de klasörlere ve sürücülere "Boyut ile analiz et" ekler (yalnızca geçerli kullanıcı, yönetici gerekmez).
  Windows 11'de "Diğer seçenekleri göster" altında görünür.
.EXAMPLE
  ./tools/register-context-menu.ps1 -Exe "C:\Program Files\Boyut\Boyut.exe"
  ./tools/register-context-menu.ps1 -Remove
#>
param(
    [string]$Exe,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$keys = @(
    'HKCU:\Software\Classes\Directory\shell\Boyut',
    'HKCU:\Software\Classes\Directory\Background\shell\Boyut',
    'HKCU:\Software\Classes\Drive\shell\Boyut'
)

if ($Remove) {
    foreach ($k in $keys) { if (Test-Path $k) { Remove-Item $k -Recurse -Force } }
    Write-Host 'Kaldırıldı.'
    return
}

if (-not $Exe) {
    $root = Split-Path $PSScriptRoot -Parent
    $found = Get-ChildItem -Path (Join-Path $root 'artifacts\Boyut'), (Join-Path $root 'src\Boyut.App\bin') `
        -Recurse -Filter Boyut.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { throw 'Boyut.exe bulunamadı; -Exe ile yolunu verin.' }
    $Exe = $found.FullName
}

foreach ($k in $keys) {
    $arg = if ($k -like '*Background*') { '%V' } else { '%1' }
    New-Item -Path "$k\command" -Force | Out-Null
    Set-ItemProperty -Path $k -Name '(default)' -Value 'Boyut ile analiz et'
    Set-ItemProperty -Path $k -Name 'Icon' -Value "`"$Exe`",0"
    Set-ItemProperty -Path "$k\command" -Name '(default)' -Value "`"$Exe`" `"$arg`""
}
Write-Host "Eklendi: $Exe"
