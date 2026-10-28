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


public sealed record ProcessSnapshot
{

    public string? UserName { get; init; }

    public int TotalProcesses { get; init; }

    public List<AppProcess> Apps { get; init; } = new();

    public DateTimeOffset CollectedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? Error { get; init; }
}

public sealed record AppProcess
{

    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public int Instances { get; init; }
    public long WorkingSetBytes { get; init; }


    public DateTimeOffset? StartedUtc { get; init; }
}

public sealed record CheckResult
{

    public required string Type { get; init; }

 
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


public sealed record HistoryPoint
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required HealthState State { get; init; }
    public double? ResponseMs { get; init; }
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }


    public double? DiskPercent { get; init; }
}

public sealed record ServerStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required string Group { get; init; }

    public DeviceCategory Category { get; init; } = DeviceCategory.Server;


    public bool Online { get; init; } = true;

    public HealthState State { get; init; } = HealthState.Unknown;

    public DateTimeOffset? LastCheckUtc { get; init; }
    public DateTimeOffset? LastUpUtc { get; init; }

    public double? ResponseMs { get; init; }

    public List<CheckResult> Checks { get; init; } = new();

    public MetricsSnapshot? Metrics { get; init; }

    public List<HistoryPoint> History { get; init; } = new();


    public double UptimePercent { get; init; }
}
