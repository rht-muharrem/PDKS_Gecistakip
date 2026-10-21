namespace ServerMonitor.Models;

/// <summary>Bir cihazda kurulu bir program (registry Uninstall anahtarindan).</summary>
public sealed record InstalledProgram
{
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public DateTimeOffset? InstallDate { get; init; }

    /// <summary>64-bit Windows'ta 32-bit uygulama mi (WOW6432Node).</summary>
    public bool Is32Bit { get; init; }
}

public sealed record InstalledProgramsResult
{
    public List<InstalledProgram> Programs { get; init; } = new();
    public string? Error { get; init; }
}

/// <summary>Cihaza takilmis (veya takili) bir USB depolama aygiti.</summary>
public sealed record UsbDevice
{
    public required string Name { get; init; }
    public string? Serial { get; init; }
    public long? SizeBytes { get; init; }

    /// <summary>Su anda takili mi.</summary>
    public bool Connected { get; init; }

    public DateTimeOffset? FirstConnectedUtc { get; init; }
    public DateTimeOffset? LastConnectedUtc { get; init; }
    public DateTimeOffset? LastRemovedUtc { get; init; }
}

public sealed record UsbResult
{
    public List<UsbDevice> Devices { get; init; } = new();
    public string? Error { get; init; }
}

public sealed record NetworkAdapterInfo
{
    public required string Name { get; init; }
    public string? Mac { get; init; }
    public List<string> IpAddresses { get; init; } = new();
    public string? Gateway { get; init; }
    public bool Dhcp { get; init; }
}

public sealed record AntivirusInfo
{
    public required string Name { get; init; }
    public bool Enabled { get; init; }
    public bool UpToDate { get; init; }
}

public sealed record PrinterInfo
{
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public bool IsNetwork { get; init; }
    public string? Port { get; init; }
    public bool Offline { get; init; }
}

/// <summary>Cihazin kunye bilgileri (WMI'den).</summary>
public sealed record DeviceInfo
{
    public string? Hostname { get; init; }
    public string? Domain { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? Cpu { get; init; }
    public int? LogicalProcessors { get; init; }
    public long? MemoryBytes { get; init; }
    public string? Os { get; init; }
    public string? OsArchitecture { get; init; }
    public string? OsBuild { get; init; }
    public DateTimeOffset? OsInstallDate { get; init; }
    public DateTimeOffset? LastBootUtc { get; init; }
    public string? BiosVersion { get; init; }
    public DateTimeOffset? BiosDate { get; init; }
    public string? LoggedInUser { get; init; }
    public List<NetworkAdapterInfo> Adapters { get; init; } = new();
    public List<AntivirusInfo> Antivirus { get; init; } = new();
    public List<PrinterInfo> Printers { get; init; } = new();
    public string? Error { get; init; }
}
