using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ServerMonitor.Models;

namespace ServerMonitor.Services;

/// <summary>
/// DC'deki toplayicidan gelen DNS ozetlerini istemci IP'si basina "alan adi -> sayi" olarak tutar.
/// Hem bellekte (hizli), hem history.db'de (kalici) — panel yeniden baslayinca liste kaybolmaz.
/// Sadece alan adlari sayilir; tam URL, sayfa icerigi veya sorgu zamani saklanmaz.
/// </summary>
public sealed class DnsInsightStore
{
    private sealed record Bucket(DateTimeOffset UpdatedUtc, int WindowMinutes, IReadOnlyDictionary<string, int> Domains);

    private readonly ConcurrentDictionary<string, Bucket> _byClientIp = new();

    private readonly string _connectionString;
    private readonly ILogger<DnsInsightStore> _logger;
    private readonly object _dbLock = new();

    public DnsInsightStore(IHostEnvironment env, ILogger<DnsInsightStore> logger)
    {
        _logger = logger;
        var path = Path.Combine(env.ContentRootPath, "history.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        InitializeAndLoad();
    }

    private void InitializeAndLoad()
    {
        try
        {
            lock (_dbLock)
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                using (var create = connection.CreateCommand())
                {
                    create.CommandText = """
                        CREATE TABLE IF NOT EXISTS dns_bucket (
                            client_ip TEXT PRIMARY KEY,
                            updated   TEXT NOT NULL,
                            window    INTEGER NOT NULL,
                            domains   TEXT NOT NULL
                        );
                        """;
                    create.ExecuteNonQuery();
                }

                using var read = connection.CreateCommand();
                read.CommandText = "SELECT client_ip, updated, window, domains FROM dns_bucket;";
                using var reader = read.ExecuteReader();
                var loaded = 0;
                while (reader.Read())
                {
                    var ip = reader.GetString(0);
                    var updated = DateTimeOffset.Parse(reader.GetString(1));
                    var window = reader.GetInt32(2);
                    var domains = JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(3))
                                  ?? new Dictionary<string, int>();
                    _byClientIp[ip] = new Bucket(updated, window, domains);
                    loaded++;
                }

                if (loaded > 0)
                    _logger.LogInformation("{Count} DNS kaydi diskten yuklendi.", loaded);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DNS verisi diskten yuklenemedi (bellek ile devam).");
        }
    }

    private void Persist(string clientIp, Bucket bucket)
    {
        try
        {
            lock (_dbLock)
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO dns_bucket (client_ip, updated, window, domains)
                    VALUES ($ip, $updated, $window, $domains)
                    ON CONFLICT(client_ip) DO UPDATE SET updated = $updated, window = $window, domains = $domains;
                    """;
                command.Parameters.AddWithValue("$ip", clientIp);
                command.Parameters.AddWithValue("$updated", bucket.UpdatedUtc.ToString("O"));
                command.Parameters.AddWithValue("$window", bucket.WindowMinutes);
                command.Parameters.AddWithValue("$domains", JsonSerializer.Serialize(bucket.Domains));
                command.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DNS kaydi diske yazilamadi: {Ip}", clientIp);
        }
    }

    // =====================================================
    //  Gürültü filtresi
    //  Kullanicinin girmedigi halde surekli konusulan alan adlari: isletim sistemi telemetrisi,
    //  antivirus, guncelleme, CDN ve sitelere gomulu analitik servisler. Bunlar listede gosterilmez.
    //  Ambivalan olanlar (google.com, youtube.com, facebook.com gibi kullanicinin da girebilecegi
    //  yerler) BILEREK listede tutulmadi.
    // =====================================================

    /// <summary>Tam (eTLD+1) eslesme ile elenen alan adlari.</summary>
    private static readonly HashSet<string> BuiltInIgnore = new(StringComparer.OrdinalIgnoreCase)
    {
        // Microsoft / Windows arka plan
        "microsoft.com", "msn.com", "bing.com", "windows.com", "windowsupdate.com", "update.microsoft.com",
        "live.com", "office.com", "office365.com", "office.net", "microsoftonline.com", "msftconnecttest.com",
        "msftncsi.com", "windows.net", "msedge.net", "microsoftstore.com", "s-microsoft.com", "sfx.ms",
        "msocdn.com", "microsoft-falcon.io", "skype.com", "msftauth.net", "azureedge.net", "azure.com",
        "windowsazure.com", "trafficmanager.net", "aadcdn.microsoftonline-p.com",
        // Antivirus / guvenlik
        "bitdefender.com", "bitdefender.net", "nsatc.net",
        // Baglanti / DNS kontrolleri
        "dns.google", "one.one.one.one", "gvt1.com", "gvt2.com",
        // Yaygin CDN / altyapi (kullaniciya gorunmez)
        "gstatic.com", "googleapis.com", "ytimg.com", "googleusercontent.com", "google-analytics.com",
        "googletagmanager.com", "doubleclick.net", "g.doubleclick.net", "fbcdn.net", "akamai.net",
        "akamaized.net", "akamaiedge.net", "edgekey.net", "edgesuite.net", "cloudfront.net", "fastly.net",
        "fastly-edge.com", "jsdelivr.net", "cdn.jsdelivr.net", "unpkg.com", "cloudflare.com", "cloudflare-dns.com",
        "llnwd.net", "cdn77.org", "cedexis.net", "demdex.net", "adsrvr.org",
        // Uygulama telemetri / arka plan senkron
        "whatsapp.net", "sfbassets.com", "office.live.com", "aria.microsoft.com",
    };

    /// <summary>Alan adi bu parcalardan birini iceriyorsa elenir (bolgesel telemetri adresleri icin).</summary>
    private static readonly string[] IgnoreKeywords =
    {
        "datadoghq", "sentry", "crashlytics", "bugsnag", "newrelic", "clarity.ms", "scorecardresearch",
        "hotjar", "mixpanel", "amplitude", "segment.io", "fullstory", "sentry-cdn", "app-measurement",
        "telemetry", "-intake", "settings-win", "connecttest", "ncsi", "edgedl", "dl.delivery.mp.microsoft"
    };

    private static bool IsNoise(string domain, ISet<string> extraIgnore, ISet<string> allowed)
    {
        // Kullanicinin acikca "her zaman goster" dedikleri filtreye takilmaz.
        if (allowed.Contains(domain))
            return false;

        if (BuiltInIgnore.Contains(domain) || extraIgnore.Contains(domain))
            return true;

        foreach (var keyword in IgnoreKeywords)
            if (domain.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>Toplayicidan gelen paketi isler. Her IP icin o pencerenin tam ozeti geldigi varsayilir (uzerine yazilir).</summary>
    public void Ingest(DnsIngest packet)
    {
        var now = DateTimeOffset.UtcNow;

        var perIp = packet.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.ClientIp) && !string.IsNullOrWhiteSpace(e.Domain))
            .GroupBy(e => e.ClientIp.Trim());

        foreach (var group in perIp)
        {
            var domains = group
                .GroupBy(e => e.Domain.Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Sum(e => Math.Max(0, e.Count)));

            var bucket = new Bucket(now, packet.WindowMinutes, domains);
            _byClientIp[group.Key] = bucket;
            Persist(group.Key, bucket);   // kalıcı: panel yeniden başlayınca kaybolmasın
        }
    }

    /// <summary>
    /// Verilen IP'lere ait alan adlarini birlestirip en cok erisilenleri doner.
    /// applyFilter=false ise gurultu filtresi uygulanmaz (her sey gosterilir).
    /// </summary>
    public (DateTimeOffset? UpdatedUtc, int WindowMinutes, List<DnsDomainHit> Top) Query(
        IEnumerable<string> clientIps, int take,
        IEnumerable<string>? extraIgnore = null, IEnumerable<string>? allowed = null, bool applyFilter = true)
    {
        var ignore = new HashSet<string>(extraIgnore ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var allow = new HashSet<string>(allowed ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? updated = null;
        var window = 0;

        foreach (var ip in clientIps)
        {
            if (!_byClientIp.TryGetValue(ip, out var bucket))
                continue;

            window = Math.Max(window, bucket.WindowMinutes);
            if (updated is null || bucket.UpdatedUtc > updated)
                updated = bucket.UpdatedUtc;

            foreach (var (domain, count) in bucket.Domains)
            {
                if (applyFilter && IsNoise(domain, ignore, allow))
                    continue;
                merged[domain] = merged.GetValueOrDefault(domain) + count;
            }
        }

        var top = merged
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(kv => new DnsDomainHit(kv.Key, kv.Value))
            .ToList();

        return (updated, window, top);
    }

    /// <summary>Panelin genel ozeti icin: tum IP'ler birlestirilmis en cok erisilen adlar.</summary>
    public (DateTimeOffset? UpdatedUtc, List<DnsDomainHit> Top) QueryAll(int take, IEnumerable<string>? extraIgnore = null)
    {
        var (updated, _, top) = Query(_byClientIp.Keys.ToList(), take, extraIgnore);
        return (updated, top);
    }

    public bool HasData => !_byClientIp.IsEmpty;

    public void Prune(int retentionMinutes)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(5, retentionMinutes));
        var removed = new List<string>();

        foreach (var (ip, bucket) in _byClientIp)
        {
            if (bucket.UpdatedUtc < cutoff && _byClientIp.TryRemove(ip, out _))
                removed.Add(ip);
        }

        if (removed.Count == 0)
            return;

        try
        {
            lock (_dbLock)
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM dns_bucket WHERE updated < $cutoff;";
                command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
                command.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Eski DNS kayitlari diskten silinemedi.");
        }
    }
}
