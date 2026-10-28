<#
.SYNOPSIS
  Boyut'u derler, test eder, çalıştırır ya da tek klasörlük sürüm üretir.
.EXAMPLE
  ./build.ps1            # derle + test
  ./build.ps1 -Run       # derle + uygulamayı başlat
  ./build.ps1 -Publish   # artifacts\Boyut altına bağımsız sürüm
#>
param(
    [switch]$Run,
    [switch]$Publish,
    [switch]$SkipTests,
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK bulunamadı. https://dotnet.microsoft.com/download/dotnet/10.0 adresinden .NET 10 SDK kurun.'
}
$sdk = (dotnet --version)
if ([version]($sdk -replace '-.*$', '') -lt [version]'10.0.100') {
    throw ".NET 10 SDK gerekli (bulunan: $sdk)."
}

Write-Host "› Çekirdek, Windows katmanı ve CLI derleniyor…" -ForegroundColor Cyan
dotnet build src/Boyut.Cli/Boyut.Cli.csproj -c $Configuration --nologo -v minimal
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if (-not $SkipTests) {
    Write-Host "› Testler…" -ForegroundColor Cyan
    dotnet test --project tests/Boyut.Core.Tests -c $Configuration
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}

Write-Host "› WinUI 3 uygulaması derleniyor ($Platform)…" -ForegroundColor Cyan
dotnet build src/Boyut.App/Boyut.App.csproj -c $Configuration -p:Platform=$Platform --nologo -v minimal
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

if ($Publish) {
    $out = Join-Path $PSScriptRoot 'artifacts\Boyut'
    Write-Host "› Bağımsız sürüm → $out" -ForegroundColor Cyan
    dotnet publish src/Boyut.App/Boyut.App.csproj -c Release -p:Platform=$Platform -r $rid --self-contained true `
        -p:PublishReadyToRun=true -o $out --nologo -v minimal
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    dotnet publish src/Boyut.Cli/Boyut.Cli.csproj -c Release -r $rid --self-contained true -p:PublishSingleFile=true `
        -o (Join-Path $PSScriptRoot 'artifacts\cli') --nologo -v minimal
    Write-Host "Hazır: $out\Boyut.exe" -ForegroundColor Green
}

if ($Run) {
    $exe = Get-ChildItem -Path "src/Boyut.App/bin/$Platform/$Configuration" -Recurse -Filter Boyut.exe | Select-Object -First 1
    if (-not $exe) { throw 'Boyut.exe bulunamadı.' }
    Write-Host "› Başlatılıyor: $($exe.FullName)" -ForegroundColor Cyan
    Start-Process $exe.FullName
}
