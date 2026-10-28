param(
    [Parameter(Mandatory = $true)] [string] $PanelUrl,
    [Parameter(Mandatory = $true)] [string] $IngestKey,
    [int] $WindowMinutes = 60,
    [int] $MaxDomainsPerClient = 40
)

$ErrorActionPreference = 'Stop'
$LogName = 'Microsoft-Windows-DNSServer/Analytical'

try { Start-Transcript -Path 'C:\dns-toplayici-calisma.log' -Force | Out-Null } catch { }


[System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy


$PanelUrl  = $PanelUrl.Trim().Trim('"', "'").Trim()
$IngestKey = $IngestKey.Trim().Trim('"', "'").Trim()
Write-Host "Panel: $PanelUrl"


$TwoLevelTlds = @(
    'com.tr','net.tr','org.tr','gov.tr','edu.tr','k12.tr','gen.tr','web.tr','bel.tr','pol.tr','av.tr','name.tr','biz.tr','info.tr','tv.tr',
    'co.uk','org.uk','me.uk','gov.uk','ac.uk','co.jp','com.au','co.nz','com.br','com.cn','co.in','com.ru'
)

function Get-Registrable([string] $qname) {
    if ([string]::IsNullOrWhiteSpace($qname)) { return $null }
    $q = $qname.TrimEnd('.').ToLowerInvariant()

    # Ic ag ve teknik sorgular: sayma
    if ($q -notlike '*.*') { return $null }                      
    if ($q -like '*.local') { return $null }
    if ($q -like '*.in-addr.arpa' -or $q -like '*.ip6.arpa') { return $null }  
    if ($q -like '_*' -or $q -like '*._*') { return $null }       
    if ($q -like '*.arpa') { return $null }

    $parts = $q.Split('.')
    if ($parts.Count -lt 2) { return $null }

    $last2 = ($parts[-2..-1]) -join '.'
    if (($TwoLevelTlds -contains $last2) -and $parts.Count -ge 3) {
        return ($parts[-3..-1]) -join '.'
    }
    return $last2
}


$start = (Get-Date).AddMinutes(-$WindowMinutes)
Write-Host "DNS gunlugu okunuyor ($WindowMinutes dk)..."


$logFile = Join-Path $env:SystemRoot 'System32\Winevt\Logs\Microsoft-Windows-DNSServer%4Analytical.evtx'
$windowMs = $WindowMinutes * 60000
$xpath = "*[System[(EventID=256) and TimeCreated[timediff(@SystemTime) <= $windowMs]]]"

if (-not (Test-Path $logFile)) {
    Write-Host "[HATA] Gunluk dosyasi bulunamadi: $logFile" -ForegroundColor Red
    Write-Host "DNS Analytical gunlugu bu makinede acik mi? (wevtutil gl ...)"
    exit 1
}

$events = @()
try {
    $events = @(Get-WinEvent -Path $logFile -Oldest -FilterXPath $xpath -ErrorAction Stop)
}
catch {
    $msg = $_.Exception.Message
    if ($msg -match 'No events were found|match the specified') {
        Write-Host "Bu pencerede sorgu kaydi yok."
    }
    else {
        Write-Host ""
        Write-Host "[HATA] Gunluk dosyasi okunamadi:" -ForegroundColor Red
        Write-Host "   $msg"
        exit 1
    }
}

Write-Host "$($events.Count) sorgu kaydi bulundu."


$counts = @{}   

foreach ($e in $events) {
    try {
        [xml]$x = $e.ToXml()
        $data = @{}
        foreach ($d in $x.Event.EventData.Data) { $data[$d.Name] = $d.'#text' }

        $ip = $data['Source']
        $domain = Get-Registrable $data['QNAME']
        if ([string]::IsNullOrWhiteSpace($ip) -or -not $domain) { continue }

        $key = "$ip`n$domain"
        if ($counts.ContainsKey($key)) { $counts[$key]++ } else { $counts[$key] = 1 }
    }
    catch { continue }
}


$entries = New-Object System.Collections.Generic.List[object]
$counts.GetEnumerator() |
    ForEach-Object {
        $parts = $_.Key.Split("`n", 2)
        [pscustomobject]@{ ClientIp = $parts[0]; Domain = $parts[1]; Count = [int]$_.Value }
    } |
    Group-Object ClientIp |
    ForEach-Object {
        $_.Group | Sort-Object Count -Descending | Select-Object -First $MaxDomainsPerClient | ForEach-Object { $entries.Add($_) }
    }

$body = @{ windowMinutes = $WindowMinutes; entries = $entries } | ConvertTo-Json -Depth 4 -Compress


$uri = "$($PanelUrl.TrimEnd('/'))/api/dns/ingest"
try {
    $req = [System.Net.HttpWebRequest]::Create($uri)
    $req.Method = 'POST'
    $req.ContentType = 'application/json; charset=utf-8'
    $req.Proxy = New-Object System.Net.WebProxy  
    $req.Timeout = 30000
    $req.Headers.Add('X-Ingest-Key', $IngestKey)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    $req.ContentLength = $bytes.Length
    $stream = $req.GetRequestStream()
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Close()

    $resp = $req.GetResponse()
    $code = [int]$resp.StatusCode
    $resp.Close()

    Write-Host "Gonderildi: $($entries.Count) kayit, $($counts.Count) benzersiz (HTTP $code)." -ForegroundColor Green
}
catch {
    Write-Host "[HATA] Panele gonderilemedi: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "   Panel adresi ve anahtari dogru mu? DC'den panele 5080 portu acik mi?"
    exit 1
}
