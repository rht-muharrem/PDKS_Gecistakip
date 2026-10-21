using System.Collections.Concurrent;
using ServerMonitor.Models;

namespace ServerMonitor.Services;

/// <summary>Son kontrol sonuclarini bellekte tutar (uygulama yeniden baslayinca sifirlanir).</summary>
public sealed class StatusStore
{
    private readonly ConcurrentDictionary<string, ServerStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? LastRunUtc { get; private set; }

    public void Set(ServerStatus status)
    {
        _statuses[status.Id] = status;
        LastRunUtc = DateTimeOffset.UtcNow;
    }

    public ServerStatus? Get(string id) =>
        _statuses.TryGetValue(id, out var status) ? status : null;

    public IReadOnlyList<ServerStatus> GetAll() =>
        _statuses.Values
            .OrderBy(s => s.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public bool Remove(string id) => _statuses.TryRemove(id, out _);

    /// <summary>Konfigurasyondan silinen sunuculari bellekten dusur.</summary>
    public void RetainOnly(IEnumerable<string> ids)
    {
        var keep = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _statuses.Keys.Where(k => !keep.Contains(k)))
        {
            _statuses.TryRemove(key, out _);
        }
    }

    public IReadOnlyList<HistoryPoint> GetHistory(string id) =>
        Get(id)?.History ?? new List<HistoryPoint>();
}
