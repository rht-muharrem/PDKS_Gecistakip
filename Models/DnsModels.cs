namespace ServerMonitor.Models;

public sealed class DnsOptions
{
    public string? IngestKey { get; set; }

    public int RetentionMinutes { get; set; } = 1440;

    public List<string> IgnoredDomains { get; set; } = new();

    public List<string> AllowedDomains { get; set; } = new();
}

public sealed record DnsIngest(int WindowMinutes, List<DnsIngestEntry> Entries);

public sealed record DnsIngestEntry(string ClientIp, string Domain, int Count);

public sealed record DnsDomainHit(string Domain, int Count);
