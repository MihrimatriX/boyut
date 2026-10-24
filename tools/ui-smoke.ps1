<#
.SYNOPSIS
  Arayüz duman testi: Boyut'u başlatır, bir klasörü tarar, sayfaları ve ayrıntı sekmelerini UI Automation ile gezer,
  her adımda ekran görüntüsü alır. Uygulama çökerse ya da hata.log yazılırsa başarısız olur.
.EXAMPLE
  powershell -File tools/ui-smoke.ps1 -Exe src/Boyut.App/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/Boyut.exe
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Target = 'C:\Program Files',
    [string]$Out = 'ui-smoke'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
$UIA = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

New-Item -ItemType Directory -Force $Out | Out-Null
$errorLog = Join-Path $env:LOCALAPPDATA 'Boyut\hata.log'
Remove-Item $errorLog -ErrorAction SilentlyContinue

function Shot([string]$name) {
    Start-Sleep -Milliseconds 1500
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "  ekran görüntüsü: $name"
}

function Assert-Alive {
    if ($script:app.HasExited) { throw "Boyut kapandı (çıkış kodu $($script:app.ExitCode))." }
    if (Test-Path $errorLog) { Get-Content $errorLog | Write-Host; throw 'Boyut hata.log yazdı.' }
}

function Find([string]$name, [int]$timeoutSeconds = 15) {
    # "#Id" looks an element up by AutomationId (e.g. NavigationView's settings item, whose name is localized by Windows).
    $cond = if ($name.StartsWith('#')) { New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $name.Substring(1)) }
            else { New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name) }
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Assert-Alive
        $found = $script:window.FindAll($Scope::Descendants, $cond)
        foreach ($e in $found) { if (-not $e.Current.IsOffscreen) { return $e } }
        if ($found.Count -gt 0) { return $found[0] }
        Start-Sleep -Milliseconds 500
    }
    throw "Arayüzde bulunamadı: $name"
}

function Activate([string]$name) {
    $e = Find $name
    foreach ($p in @([System.Windows.Automation.SelectionItemPattern]::Pattern, [System.Windows.Automation.InvokePattern]::Pattern,
                     [System.Windows.Automation.ExpandCollapsePattern]::Pattern, [System.Windows.Automation.TogglePattern]::Pattern)) {
        $pattern = $null
        if ($e.TryGetCurrentPattern($p, [ref]$pattern)) {
            switch ($pattern.GetType().Name) {
                'SelectionItemPattern' { $pattern.Select() }
                'InvokePattern' { $pattern.Invoke() }
                'ExpandCollapsePattern' { $pattern.Expand() }
                'TogglePattern' { $pattern.Toggle() }
            }
            Write-Host "› $name"
            return
        }
    }
    throw "Etkinleştirilemedi: $name"
}

# A bigger desktop when the machine allows it (Windows Server); otherwise keep what we have.
try { Set-DisplayResolution -Width 1920 -Height 1080 -Force -ErrorAction Stop } catch { Write-Host "Çözünürlük değiştirilemedi: $($_.Exception.Message)" }

Write-Host "Başlatılıyor: $Exe `"$Target`""
$script:app = Start-Process -FilePath $Exe -ArgumentList "`"$Target`"" -PassThru
$deadline = (Get-Date).AddSeconds(60)
$script:window = $null
while (-not $script:window -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 1
    Assert-Alive
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $script:app.Id)
    $script:window = $UIA::RootElement.FindFirst($Scope::Children, $cond)
}
if (-not $script:window) { throw 'Boyut penceresi açılmadı.' }
$wp = $null
if ($script:window.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$wp)) {
    $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
}

# The scan finishes and the tree opens on its own; its details pane has these tabs.
Find 'Harita' 120 | Out-Null
Start-Sleep -Seconds 3
Shot '01-agac-harita'
foreach ($tab in 'İçerik', 'Türler', 'Dosyalar', 'Bilgi') {
    Activate $tab
    Shot ('02-agac-' + $tab)
}
Activate 'Harita'

Activate 'Genişlet'
Activate '2 düzey'
Shot '03-agac-2-duzey'

Activate 'Ayrıntı bölmesi'
Shot '04-agac-bolmesiz'
Activate 'Ayrıntı bölmesi'

foreach ($page in 'Ana sayfa', 'Değişimler', 'Gelişmiş arama', 'Temizlik ve türler', '#SettingsItem') {
    Activate $page
    Shot ('05-' + $page.TrimStart('#'))
}

# A query fills the result list
Activate 'Gelişmiş arama'
$query = Find 'Arama sorgusu'
$query.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('*.dll;*.exe size:>20mb')
Write-Host '› sorgu yazıldı'
Start-Sleep -Seconds 2
Shot '05-Gelişmiş arama-sonuçlar'

# The other theme (CI machines run light), then back to the tree
Activate 'Koyu'
Activate 'Klasör ağacı'
Shot '06-agac-koyu-tema'
Activate '#SettingsItem'
Activate 'Sistem ayarını kullan'

Assert-Alive
Write-Host 'Duman testi geçti.' -ForegroundColor Green
$script:app.CloseMainWindow() | Out-Null
if (-not $script:app.WaitForExit(15000)) { $script:app.Kill() }
