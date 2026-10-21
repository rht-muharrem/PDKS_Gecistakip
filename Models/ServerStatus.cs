namespace ServerMonitor.Models;

public enum HealthState
{
    Unknown = 0,
    Up = 1,
    Warning = 2,
    Down = 3
}

public enum DeviceCategory
{
    Server = 0,
    Computer = 1
}

/// <summary>Bir cihazda o an calisan, kullaniciya ait uygulamalar.</summary>
public sealed record ProcessSnapshot
{
    /// <summary>Konsolda oturum acmis kullanici (DOMAIN\kullanici). Kimse yoksa bos.</summary>
    public string? UserName { get; init; }

    /// <summary>Filtrelemeden onceki toplam islem sayisi.</summary>
    public int TotalProcesses { get; init; }

    public List<AppProcess> Apps { get; init; } = new();

    public DateTimeOffset CollectedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? Error { get; init; }
}

public sealed record AppProcess
{
    /// <summary>Calistirilabilir dosya adi (chrome.exe).</summary>
    public required string Name { get; init; }

    /// <summary>Okunabilir ad (Google Chrome).</summary>
    public required string DisplayName { get; init; }

    public int Instances { get; init; }
    public long WorkingSetBytes { get; init; }

    /// <summary>Bu uygulamanin en eski isleminin baslama zamani.</summary>
    public DateTimeOffset? StartedUtc { get; init; }
}

public sealed record CheckResult
{
    /// <summary>ping | port | http | wmi</summary>
    public required string Type { get; init; }

    /// <summary>Kartta gosterilen etiket.</summary>
    public required string Label { get; init; }

    public required HealthState State { get; init; }

    public double DurationMs { get; init; }

    public string? Message { get; init; }
}

public sealed record DriveUsage
{
    public required string Name { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public double UsedPercent => TotalBytes <= 0 ? 0 : Math.Round((TotalBytes - FreeBytes) * 100.0 / TotalBytes, 1);
}

public sealed record MetricsSnapshot
{
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public long? MemoryTotalBytes { get; init; }
    public long? MemoryFreeBytes { get; init; }
    public List<DriveUsage> Drives { get; init; } = new();
    public TimeSpan? Uptime { get; init; }
    public string? OsName { get; init; }
    public string? Error { get; init; }
}

/// <summary>Grafiklerin beslendigi zaman serisi noktasi.</summary>
public sealed record HistoryPoint
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required HealthState State { get; init; }
    public double? ResponseMs { get; init; }
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }

    /// <summary>En dolu surucunun doluluk yuzdesi.</summary>
    public double? DiskPercent { get; init; }
}

public sealed record ServerStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required string Group { get; init; }

    public DeviceCategory Category { get; init; } = DeviceCategory.Server;

    /// <summary>Cihaza agdan ulasilabiliyor mu. Kapali bilgisayari "sorunlu"dan ayirmak icin.</summary>
    public bool Online { get; init; } = true;

    public HealthState State { get; init; } = HealthState.Unknown;

    public DateTimeOffset? LastCheckUtc { get; init; }
    public DateTimeOffset? LastUpUtc { get; init; }

    /// <summary>Ping (yoksa ilk basarili kontrol) yanit suresi.</summary>
    public double? ResponseMs { get; init; }

    public List<CheckResult> Checks { get; init; } = new();

    public MetricsSnapshot? Metrics { get; init; }

    public List<HistoryPoint> History { get; init; } = new();

    /// <summary>Gecmisteki Up/Warning oraninin yuzdesi.</summary>
    public double UptimePercent { get; init; }
}
