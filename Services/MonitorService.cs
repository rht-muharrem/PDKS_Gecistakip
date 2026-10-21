using ServerMonitor.Models;

namespace ServerMonitor.Services;

/// <summary>Tum sunuculari paralel kontrol eder ve sonuclari StatusStore'a yazar.</summary>
public sealed class MonitorService
{
    private readonly ServerRepository _repository;
    private readonly NetworkProbe _network;
    private readonly WindowsMetricsProbe _wmi;
    private readonly StatusStore _store;
    private readonly HistoryStore _history;
    private readonly ILogger<MonitorService> _logger;

    // Ayni anda birden fazla tur calismasini engeller (arka plan + manuel tetikleme).
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public MonitorService(
        ServerRepository repository,
        NetworkProbe network,
        WindowsMetricsProbe wmi,
        StatusStore store,
        HistoryStore history,
        ILogger<MonitorService> logger)
    {
        _repository = repository;
        _network = network;
        _wmi = wmi;
        _store = store;
        _history = history;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ServerStatus>> RunAllAsync(CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            var settings = _repository.GetSettings();
            var servers = _repository.GetServers().Where(s => s.Enabled).ToList();

            _store.RetainOnly(servers.Select(s => s.Id!).Where(id => !string.IsNullOrEmpty(id)));

            var tasks = servers.Select(s => CheckServerAsync(s, settings, ct));
            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
            {
                _store.Set(result);
            }

            // Tur SIRASINDA silinen/pasiflenen bir sunucu, sonuclari yazarken belleğe geri
            // eklenmis olabilir. Guncel listeyle tekrar temizleyip bunlari hemen dusuruyoruz
            // (yoksa bir sonraki tura, ~60 sn'ye kadar ekranda kalirlardi).
            var live = _repository.GetServers().Where(s => s.Enabled).Select(s => s.Id!)
                .Where(id => !string.IsNullOrEmpty(id));
            _store.RetainOnly(live);

            return results;
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<ServerStatus?> RunOneAsync(string id, CancellationToken ct)
    {
        var server = _repository.Get(id);
        if (server is null || !server.Enabled)
            return null;

        var status = await CheckServerAsync(server, _repository.GetSettings(), ct);
        _store.Set(status);
        return status;
    }

    private async Task<ServerStatus> CheckServerAsync(ServerDefinition server, MonitorSettings settings, CancellationToken ct)
    {
        var id = server.Id ?? server.Host;
        var previous = _store.Get(id);
        var checks = new List<CheckResult>();

        try
        {
            if (server.Ping is { Enabled: true } ping)
            {
                checks.Add(await _network.PingAsync(server.Host, ping, settings.DefaultTimeoutMs, ct));
            }

            if (server.Ports.Count > 0)
            {
                var portResults = await Task.WhenAll(
                    server.Ports.Select(p => _network.TcpAsync(server.Host, p, settings.DefaultTimeoutMs, ct)));
                checks.AddRange(portResults);
            }

            if (server.Http.Count > 0)
            {
                var httpResults = await Task.WhenAll(
                    server.Http.Select(h => _network.HttpAsync(h, settings.DefaultTimeoutMs, ct)));
                checks.AddRange(httpResults);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Server} kontrol edilirken beklenmeyen hata.", server.Name);
            checks.Add(new CheckResult
            {
                Type = "error",
                Label = "Kontrol",
                State = HealthState.Down,
                Message = ex.GetBaseException().Message
            });
        }

        // ICMP cogu ortamda kapalidir; ping'in dusmesi sunucunun erisilemez oldugunu GOSTERMEZ.
        // Bu yuzden erisilebilirlik karari yalnizca port/HTTP kontrollerine bakar. Hic port/HTTP
        // kontrolu tanimlanmamissa WMI her halukarda denenir.
        var probes = checks.Where(c => c.Type is "port" or "http").ToList();
        var reachable = probes.Count == 0 || probes.Any(c => c.State != HealthState.Down);

        MetricsSnapshot? metrics = null;
        if (server.WindowsMetrics is { Enabled: true } wmiOptions)
        {
            metrics = reachable
                ? await _wmi.GetAsync(server.Host, wmiOptions, ct)
                : new MetricsSnapshot { Error = "Sunucuya erisilemedigi icin metrikler atlandi." };

            checks.AddRange(BuildMetricChecks(metrics, wmiOptions));
        }

        var state = Aggregate(checks);

        // Cihaz agda mi? Ag kontrollerinden biri gecmisse evet; hic ag kontrolu yoksa WMI'ye ulasilabildiyse evet.
        var networkChecks = checks.Where(c => c.Type is "ping" or "port" or "http").ToList();
        var online = networkChecks.Count > 0
            ? networkChecks.Any(c => c.State != HealthState.Down)
            : metrics is null || metrics.Error is null;
        var now = DateTimeOffset.UtcNow;
        var responseMs = checks.FirstOrDefault(c => c.Type == "ping" && c.State != HealthState.Down)?.DurationMs
                         ?? checks.FirstOrDefault(c => c.State != HealthState.Down)?.DurationMs;

        var point = new HistoryPoint
        {
            TimestampUtc = now,
            State = state,
            ResponseMs = responseMs,
            CpuPercent = metrics?.CpuPercent,
            MemoryPercent = metrics?.MemoryPercent,
            DiskPercent = metrics is { Drives.Count: > 0 } ? metrics.Drives.Max(d => d.UsedPercent) : null
        };

        // Uygulama yeni basladiysa bellekte gecmis yoktur; grafikler bos kalmasin diye diskten yukle.
        var priorHistory = previous?.History ?? await _history.LoadRecentAsync(id, settings.HistorySize, ct);

        await _history.AppendAsync(id, point, ct);

        var history = BuildHistory(priorHistory, point, settings.HistorySize);
        var healthy = history.Count(h => h.State is HealthState.Up or HealthState.Warning);

        return new ServerStatus
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(server.Name) ? server.Host : server.Name,
            Host = server.Host,
            Group = string.IsNullOrWhiteSpace(server.Group) ? "Genel" : server.Group,
            Category = server.Category,
            Online = online,
            State = state,
            LastCheckUtc = now,
            LastUpUtc = state == HealthState.Down ? previous?.LastUpUtc : now,
            ResponseMs = responseMs,
            Checks = checks,
            Metrics = metrics,
            History = history,
            UptimePercent = history.Count == 0 ? 0 : Math.Round(healthy * 100.0 / history.Count, 1)
        };
    }

    private static IEnumerable<CheckResult> BuildMetricChecks(MetricsSnapshot metrics, WindowsMetricsOptions o)
    {
        if (metrics.Error is not null && metrics.CpuPercent is null && metrics.Drives.Count == 0)
        {
            yield return new CheckResult
            {
                Type = "wmi",
                Label = "WMI",
                State = HealthState.Warning,
                Message = metrics.Error
            };
            yield break;
        }

        if (metrics.CpuPercent is { } cpu)
        {
            yield return new CheckResult
            {
                Type = "wmi",
                Label = "CPU",
                State = Threshold(cpu, o.CpuWarnPercent, o.CpuCriticalPercent),
                Message = $"%{cpu:F0}"
            };
        }

        if (metrics.MemoryPercent is { } mem)
        {
            yield return new CheckResult
            {
                Type = "wmi",
                Label = "RAM",
                State = Threshold(mem, o.MemoryWarnPercent, o.MemoryCriticalPercent),
                Message = $"%{mem:F0}"
            };
        }

        foreach (var drive in metrics.Drives)
        {
            yield return new CheckResult
            {
                Type = "wmi",
                Label = $"Disk {drive.Name}",
                State = Threshold(drive.UsedPercent, o.DiskWarnPercent, o.DiskCriticalPercent),
                Message = $"%{drive.UsedPercent:F0} dolu"
            };
        }
    }

    private static HealthState Threshold(double value, double warn, double critical)
    {
        if (critical > 0 && value >= critical) return HealthState.Down;
        if (warn > 0 && value >= warn) return HealthState.Warning;
        return HealthState.Up;
    }

    private static HealthState Aggregate(IReadOnlyCollection<CheckResult> checks)
    {
        if (checks.Count == 0)
            return HealthState.Unknown;

        if (checks.Any(c => c.State == HealthState.Down))
            return HealthState.Down;

        if (checks.Any(c => c.State == HealthState.Warning))
            return HealthState.Warning;

        return HealthState.Up;
    }

    private static List<HistoryPoint> BuildHistory(List<HistoryPoint>? previous, HistoryPoint point, int size)
    {
        var history = previous is null ? new List<HistoryPoint>() : new List<HistoryPoint>(previous);
        history.Add(point);

        if (size > 0 && history.Count > size)
        {
            history.RemoveRange(0, history.Count - size);
        }

        return history;
    }
}
