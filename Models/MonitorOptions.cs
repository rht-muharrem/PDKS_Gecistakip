namespace ServerMonitor.Models;


public sealed class MonitorOptions
{
    public const string SectionName = "Monitor";


    public int IntervalSeconds { get; set; } = 30;


    public int DefaultTimeoutMs { get; set; } = 5000;


    public int HistorySize { get; set; } = 240;


    public int HistoryRetentionDays { get; set; } = 30;

    public NotificationOptions Notifications { get; set; } = new();

    public AuthOptions Auth { get; set; } = new();

    public DnsOptions Dns { get; set; } = new();

    public List<ServerDefinition> Servers { get; set; } = new();
}

public sealed record MonitorSettings(
    int IntervalSeconds,
    int DefaultTimeoutMs,
    int HistorySize,
    int HistoryRetentionDays);

public sealed class ServerDefinition
{

    public string? Id { get; set; }

    public string Name { get; set; } = "";


    public string Host { get; set; } = "";


    public string Group { get; set; } = "Genel";


    public DeviceCategory Category { get; set; } = DeviceCategory.Server;

    public bool Enabled { get; set; } = true;

    public PingCheckOptions? Ping { get; set; }

    public List<PortCheckOptions> Ports { get; set; } = new();

    public List<HttpCheckOptions> Http { get; set; } = new();

    public WindowsMetricsOptions? WindowsMetrics { get; set; }
}


public sealed class PingCheckOptions
{
    public bool Enabled { get; set; } = true;
    public int? TimeoutMs { get; set; }


    public int WarnAboveMs { get; set; } = 250;
}

public sealed class PortCheckOptions
{
    public int Port { get; set; }


    public string? Label { get; set; }

    public int? TimeoutMs { get; set; }
}

public sealed class HttpCheckOptions
{
    public string Url { get; set; } = "";
    public string? Label { get; set; }
    public int? TimeoutMs { get; set; }


    public List<int> ExpectedStatusCodes { get; set; } = new();


    public string? MustContain { get; set; }


    public bool IgnoreCertificateErrors { get; set; }

    public int WarnAboveMs { get; set; } = 1500;
}

public sealed class WindowsMetricsOptions
{
    public bool Enabled { get; set; } = true;

    public string? Username { get; set; }


    public string? Password { get; set; }

    public int TimeoutMs { get; set; } = 15000;

    public double CpuWarnPercent { get; set; } = 80;
    public double CpuCriticalPercent { get; set; } = 95;

    public double MemoryWarnPercent { get; set; } = 85;
    public double MemoryCriticalPercent { get; set; } = 95;

    public double DiskWarnPercent { get; set; } = 85;
    public double DiskCriticalPercent { get; set; } = 95;

    public List<string> Drives { get; set; } = new();
}
