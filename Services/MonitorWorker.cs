using ServerMonitor.Models;

namespace ServerMonitor.Services;


public sealed class MonitorWorker : BackgroundService
{
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(6);

    private readonly MonitorService _monitor;
    private readonly ServerRepository _repository;
    private readonly AlertService _alerts;
    private readonly HistoryStore _history;
    private readonly DnsInsightStore _dns;
    private readonly ILogger<MonitorWorker> _logger;

    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public MonitorWorker(
        MonitorService monitor,
        ServerRepository repository,
        AlertService alerts,
        HistoryStore history,
        DnsInsightStore dns,
        ILogger<MonitorWorker> logger)
    {
        _monitor = monitor;
        _repository = repository;
        _alerts = alerts;
        _history = history;
        _dns = dns;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Izleme servisi basladi. Aralik: {Interval} sn",
            _repository.GetSettings().IntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var results = await _monitor.RunAllAsync(stoppingToken);
                var down = results.Count(r => r.State == HealthState.Down);

                if (down > 0)
                    _logger.LogWarning("Tur tamamlandi: {Total} sunucu, {Down} tanesi sorunlu.", results.Count, down);
                else
                    _logger.LogInformation("Tur tamamlandi: {Total} sunucunun tamami erisilebilir.", results.Count);

                await _alerts.EvaluateAsync(results, stoppingToken);

                if (DateTimeOffset.UtcNow - _lastPrune > PruneEvery)
                {
                    _lastPrune = DateTimeOffset.UtcNow;
                    await _history.PruneAsync(_repository.GetSettings().HistoryRetentionDays, stoppingToken);
                    _dns.Prune(_repository.GetDns().RetentionMinutes);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Izleme turu basarisiz oldu.");
            }

     
            var interval = Math.Max(5, _repository.GetSettings().IntervalSeconds);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Izleme servisi durdu.");
    }
}
