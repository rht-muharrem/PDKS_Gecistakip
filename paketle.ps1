# Projeyi bin/obj gibi gecici klasorler haric zip'ler.
# Zip, bu klasorun bir ustune "ServerMonitor.zip" olarak birakilir.

$ErrorActionPreference = 'Stop'

$src  = $PSScriptRoot
$name = Split-Path $src -Leaf
$zip  = Join-Path (Split-Path $src -Parent) "$name.zip"

# Once temiz bir kopya cikar (zip'e bin/obj girmesin).
$staging = Join-Path $env:TEMP ("pack_" + [guid]::NewGuid().ToString('N'))
$target  = Join-Path $staging $name
New-Item -ItemType Directory -Path $target -Force | Out-Null

Write-Host "Dosyalar kopyalaniyor..."
# keys: DPAPI ile bu makineye/hesaba bagli, baska yerde ise yaramaz.
# history.db: olcum gecmisi, paketle tasinmasi gerekmiyor.
robocopy $src $target /E /XD bin obj .vs .git keys /XF *.zip history.db history.db-shm history.db-wal /NFL /NDL /NJH /NJS /NP | Out-Null

# robocopy 0-7 arasi cikis kodlarini basari sayar.
if ($LASTEXITCODE -ge 8) {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    throw "Dosyalar kopyalanamadi (robocopy kodu $LASTEXITCODE)."
}

if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "Zip olusturuluyor..."
Compress-Archive -Path $target -DestinationPath $zip -CompressionLevel Optimal

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

$size = [math]::Round((Get-Item $zip).Length / 1KB, 1)
Write-Host ""
Write-Host "Hazir: $zip  ($size KB)" -ForegroundColor Green
