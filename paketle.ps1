

$ErrorActionPreference = 'Stop'

$src  = $PSScriptRoot
$name = Split-Path $src -Leaf
$zip  = Join-Path (Split-Path $src -Parent) "$name.zip"


$staging = Join-Path $env:TEMP ("pack_" + [guid]::NewGuid().ToString('N'))
$target  = Join-Path $staging $name
New-Item -ItemType Directory -Path $target -Force | Out-Null

Write-Host "Dosyalar kopyalaniyor..."

robocopy $src $target /E /XD bin obj .vs .git keys /XF *.zip history.db history.db-shm history.db-wal /NFL /NDL /NJH /NJS /NP | Out-Null

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
