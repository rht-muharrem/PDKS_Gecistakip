namespace ServerMonitor.Models;

/// <summary>servers.json icindeki "Monitor" bolumunun karsiligi.</summary>
public sealed class MonitorOptions
{
    public const string SectionName = "Monitor";

    /// <summary>Arka plan servisinin tur araligi (saniye).</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Kontrol basina varsayilan zaman asimi (ms).</summary>
    public int DefaultTimeoutMs { get; set; } = 5000;

    /// <summary>Sunucu basina bellekte tutulacak gecmis kayit sayisi (grafikler icin).</summary>
    public int HistorySize { get; set; } = 240;

    /// <summary>Veritabanindaki olcumlerin kac gun saklanacagi.</summary>
    public int HistoryRetentionDays { get; set; } = 30;

    public NotificationOptions Notifications { get; set; } = new();

    public AuthOptions Auth { get; set; } = new();

    public DnsOptions Dns { get; set; } = new();

    public List<ServerDefinition> Servers { get; set; } = new();
}

/// <summary>Sunucu listesi disindaki genel ayarlar.</summary>
public sealed record MonitorSettings(
    int IntervalSeconds,
    int DefaultTimeoutMs,
    int HistorySize,
    int HistoryRetentionDays);

public sealed class ServerDefinition
{
    /// <summary>Bos birakilirsa Name'den uretilir.</summary>
    public string? Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>IP veya hostname.</summary>
    public string Host { get; set; } = "";

    /// <summary>Dashboard'da gruplama basligi.</summary>
    public string Group { get; set; } = "Genel";

    /// <summary>Sunucu mu, kullanici bilgisayari mi. Bilgisayarlarin kapali olmasi sorun sayilmaz.</summary>
    public DeviceCategory Category { get; set; } = DeviceCategory.Server;

    public bool Enabled { get; set; } = true;

    public PingCheckOptions? Ping { get; set; }

    public List<PortCheckOptions> Ports { get; set; } = new();

    public List<HttpCheckOptions> Http { get; set; } = new();

    public WindowsMetricsOptions? WindowsMetrics { get; set; }
}

// Not: isimler "...CheckOptions" seklinde; System.Net.NetworkInformation.PingOptions
// gibi BCL tipleriyle cakismasin diye.
public sealed class PingCheckOptions
{
    public bool Enabled { get; set; } = true;
    public int? TimeoutMs { get; set; }

    /// <summary>Bu degerin ustundeki yanit suresi (ms) uyari sayilir. 0 = kapali.</summary>
    public int WarnAboveMs { get; set; } = 250;
}

public sealed class PortCheckOptions
{
    public int Port { get; set; }

    /// <summary>Dashboard'da gosterilecek etiket (or. "SQL", "RDP").</summary>
    public string? Label { get; set; }

    public int? TimeoutMs { get; set; }
}

public sealed class HttpCheckOptions
{
    public string Url { get; set; } = "";
    public string? Label { get; set; }
    public int? TimeoutMs { get; set; }

    /// <summary>Kabul edilen HTTP durum kodlari. Bos ise 200-399 arasi basarili sayilir.</summary>
    public List<int> ExpectedStatusCodes { get; set; } = new();

    /// <summary>Doluysa yanit govdesinde bu metin aranir.</summary>
    public string? MustContain { get; set; }

    /// <summary>Self-signed sertifikalara izin ver.</summary>
    public bool IgnoreCertificateErrors { get; set; }

    public int WarnAboveMs { get; set; } = 1500;
}

public sealed class WindowsMetricsOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Bos ise oturum acan kullanicinin kimligi kullanilir (localhost icin zorunlu).</summary>
    public string? Username { get; set; }

    /// <summary>Duz metin yerine user-secrets / environment variable kullan (README'ye bak).</summary>
    public string? Password { get; set; }

    public int TimeoutMs { get; set; } = 15000;

    public double CpuWarnPercent { get; set; } = 80;
    public double CpuCriticalPercent { get; set; } = 95;

    public double MemoryWarnPercent { get; set; } = 85;
    public double MemoryCriticalPercent { get; set; } = 95;

    public double DiskWarnPercent { get; set; } = 85;
    public double DiskCriticalPercent { get; set; } = 95;

    /// <summary>Doluysa yalnizca bu surucu harfleri raporlanir (or. ["C:", "D:"]).</summary>
    public List<string> Drives { get; set; } = new();
}
