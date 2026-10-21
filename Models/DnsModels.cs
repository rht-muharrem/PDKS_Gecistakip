namespace ServerMonitor.Models;

/// <summary>DNS icgoru ayarlari (servers.json -> Monitor.Dns).</summary>
public sealed class DnsOptions
{
    /// <summary>DC'deki toplayici scriptin panele veri gonderirken kullandigi anahtar. Bos ise ilk okumada uretilir.</summary>
    public string? IngestKey { get; set; }

    /// <summary>Bir bilgisayarin DNS verisi bu kadar dakika guncellenmezse dusurulur.</summary>
    public int RetentionMinutes { get; set; } = 1440;

    /// <summary>
    /// Listede gizlenecek EK alan adlari (yerlesik gurultu listesine ilave). Buraya yazdiklarin
    /// tam alan adi olarak eslesir (ornek: "sirket-antivirus.com"). Degisiklik icin uygulamayi yeniden baslat.
    /// </summary>
    public List<string> IgnoredDomains { get; set; } = new();

    /// <summary>
    /// Yerlesik gurultu listesinde olsa bile HER ZAMAN gosterilecek alan adlari. Bir siteyi
    /// (or. "bing.com") gizlenmesine ragmen takip etmek istersen buraya ekle. Yeniden baslat.
    /// </summary>
    public List<string> AllowedDomains { get; set; } = new();
}

/// <summary>DC toplayicisinin gonderdigi paket.</summary>
public sealed record DnsIngest(int WindowMinutes, List<DnsIngestEntry> Entries);

public sealed record DnsIngestEntry(string ClientIp, string Domain, int Count);

/// <summary>Panelin bir cihaz icin dondurdugu en cok erisilen alan adlari.</summary>
public sealed record DnsDomainHit(string Domain, int Count);
