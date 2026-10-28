using System.Text.Json;
using ServerMonitor.Models;

namespace ServerMonitor.Services;


public sealed class ChangeMonitorWorker : BackgroundService
{
    private readonly ServerRepository _repository;
    private readonly WindowsMetricsProbe _wmi;
    private readonly BaselineStore _baseline;
    private readonly AlertService _alerts;
    private readonly ILogger<ChangeMonitorWorker> _logger;

    public ChangeMonitorWorker(
        ServerRepository repository, WindowsMetricsProbe wmi, BaselineStore baseline,
        AlertService alerts, ILogger<ChangeMonitorWorker> logger)
    {
        _repository = repository;
        _wmi = wmi;
        _baseline = baseline;
        _alerts = alerts;
        _logger = logger;
    }

    private sealed record AvSnap(string Name, bool Enabled, bool UpToDate);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _repository.GetNotifications();
            var change = opts.ChangeAlerts;

            if (opts.Enabled && change.Enabled)
            {
                var computers = _repository.GetServers()
                    .Where(s => s.Enabled && s.Category == DeviceCategory.Computer
                                && s.WindowsMetrics is { Enabled: true })
                    .ToList();

                foreach (var pc in computers)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    try
                    {
                        await ScanDeviceAsync(pc, change, stoppingToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Değişiklik taraması başarısız: {Name}", pc.Name);
                    }
                }
            }

            var interval = Math.Max(5, _repository.GetNotifications().ChangeAlerts.IntervalMinutes);
            try { await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ScanDeviceAsync(ServerDefinition pc, ChangeAlertOptions opts, CancellationToken ct)
    {
        var id = pc.Id!;
        var wmi = pc.WindowsMetrics!;
        var changes = new List<string>();

        if (opts.WatchAntivirus)
            changes.AddRange(await DetectAvAsync(id, pc.Host, wmi, ct));

        if (opts.WatchNewSoftware)
            changes.AddRange(await DetectProgramsAsync(id, pc.Host, wmi, ct));

        if (opts.WatchUsb)
            changes.AddRange(await DetectUsbAsync(id, pc.Host, wmi, ct));

        if (changes.Count > 0)
        {
            _logger.LogInformation("{Name}: {Count} değişiklik bildiriliyor.", pc.Name, changes.Count);
            await _alerts.SendChangeAlertAsync(pc.Name, pc.Host, changes, ct);
        }
    }



    private async Task<List<string>> DetectAvAsync(string id, string host, WindowsMetricsOptions wmi, CancellationToken ct)
    {
        var info = await _wmi.GetDeviceInfoAsync(host, wmi, ct);
        if (info.Error is not null && info.Antivirus.Count == 0)
            return new();  

        var current = info.Antivirus.Select(a => new AvSnap(a.Name, a.Enabled, a.UpToDate)).ToList();
        var previous = await LoadAsync<List<AvSnap>>(id, "av", ct);

        await SaveAsync(id, "av", current, ct);
        if (previous is null) return new();   

        var changes = new List<string>();
        foreach (var cur in current)
        {
            var prev = previous.FirstOrDefault(p => p.Name.Equals(cur.Name, StringComparison.OrdinalIgnoreCase));
            if (prev is null)
            {
                changes.Add($"Antivirüs kuruldu: {cur.Name}");
                continue;
            }
            if (prev.Enabled && !cur.Enabled) changes.Add($"⚠ Antivirüs kapatıldı: {cur.Name}");
            if (prev.UpToDate && !cur.UpToDate) changes.Add($"⚠ Antivirüs güncelliğini yitirdi: {cur.Name}");
        }
        foreach (var prev in previous)
            if (!current.Any(c => c.Name.Equals(prev.Name, StringComparison.OrdinalIgnoreCase)))
                changes.Add($"⚠ Antivirüs kaldırıldı: {prev.Name}");

        return changes;
    }


    private async Task<List<string>> DetectProgramsAsync(string id, string host, WindowsMetricsOptions wmi, CancellationToken ct)
    {
        var result = await _wmi.GetInstalledProgramsAsync(host, wmi, ct);
        if (result.Error is not null && result.Programs.Count == 0)
            return new();

        var current = result.Programs
            .Select(p => p.Name.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previous = await LoadAsync<List<string>>(id, "programs", ct);
        await SaveAsync(id, "programs", current, ct);
        if (previous is null) return new();

        var prevSet = new HashSet<string>(previous, StringComparer.OrdinalIgnoreCase);
        var currSet = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);

        var changes = new List<string>();
        foreach (var name in current.Where(n => !prevSet.Contains(n)))
            changes.Add($"Yeni program kuruldu: {name}");
        foreach (var name in previous.Where(n => !currSet.Contains(n)))
            changes.Add($"Program kaldırıldı: {name}");
        return changes;
    }



    private async Task<List<string>> DetectUsbAsync(string id, string host, WindowsMetricsOptions wmi, CancellationToken ct)
    {
        var result = await _wmi.GetUsbAsync(host, wmi, ct);
        if (result.Error is not null && result.Devices.Count == 0)
            return new();


        var seen = await LoadAsync<List<string>>(id, "usb", ct) ?? new();
        var seenSet = new HashSet<string>(seen, StringComparer.OrdinalIgnoreCase);

        var changes = new List<string>();
        var isFirst = await _baseline.GetAsync(id, "usb", ct) is null;

        foreach (var d in result.Devices)
        {
            var key = d.Serial ?? d.Name;
            if (string.IsNullOrWhiteSpace(key) || seenSet.Contains(key))
                continue;

            seenSet.Add(key);
            if (!isFirst)
                changes.Add($"Yeni USB bellek: {d.Name}{(d.Serial is null ? "" : $" ({d.Serial})")}");
        }

        await SaveAsync(id, "usb", seenSet.ToList(), ct);
        return changes;
    }



    private async Task<T?> LoadAsync<T>(string id, string kind, CancellationToken ct)
    {
        var json = await _baseline.GetAsync(id, kind, ct);
        if (json is null) return default;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch { return default; }
    }

    private Task SaveAsync<T>(string id, string kind, T value, CancellationToken ct) =>
        _baseline.SetAsync(id, kind, JsonSerializer.Serialize(value), ct);
}
