using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServerMonitor.Models;

namespace ServerMonitor.Services;

/// <summary>servers.json dosyasinin kok yapisi: { "Monitor": { ... } }</summary>
internal sealed class MonitorFile
{
    public MonitorOptions Monitor { get; set; } = new();
}

/// <summary>
/// servers.json dosyasini okur ve yazar. Uygulama calisirken sunucu listesinin tek
/// kaynagi burasidir; panelden yapilan ekleme/duzenleme/silme dogrudan dosyaya islenir.
/// </summary>
public sealed class ServerRepository
{
    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // "Category": "Computer" gibi okunakli yazilsin (sayi degil).
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ILogger<ServerRepository> _logger;
    private readonly object _gate = new();

    private MonitorOptions _data = new();

    public ServerRepository(IHostEnvironment env, ILogger<ServerRepository> logger)
    {
        _logger = logger;
        _path = Path.Combine(env.ContentRootPath, "servers.json");
        Load();
    }

    // --- Okuma ---

    public MonitorSettings GetSettings()
    {
        lock (_gate)
        {
            return SettingsSnapshot();
        }
    }

    private MonitorSettings SettingsSnapshot() =>
        new(_data.IntervalSeconds, _data.DefaultTimeoutMs, _data.HistorySize, _data.HistoryRetentionDays);

    public IReadOnlyList<ServerDefinition> GetServers()
    {
        lock (_gate)
        {
            return _data.Servers.ToList();
        }
    }

    public ServerDefinition? Get(string id)
    {
        lock (_gate)
        {
            return _data.Servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    // --- Yazma ---

    public ServerDefinition Add(ServerDefinition input)
    {
        lock (_gate)
        {
            var server = Normalize(input);
            server.Id = MakeUniqueId(server, _data.Servers);

            if (server.WindowsMetrics is not null)
                server.WindowsMetrics.Password = SecretProtector.Protect(server.WindowsMetrics.Password);

            _data.Servers.Add(server);
            Persist();
            return server;
        }
    }

    public ServerDefinition? Update(string id, ServerDefinition input)
    {
        lock (_gate)
        {
            var index = _data.Servers.FindIndex(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return null;

            var existing = _data.Servers[index];
            var server = Normalize(input);
            server.Id = existing.Id;

            if (server.WindowsMetrics is not null)
            {
                // Parola alani bos gelirse eskisi korunur (panelde bos birakmak = degistirme).
                server.WindowsMetrics.Password = string.IsNullOrEmpty(server.WindowsMetrics.Password)
                    ? existing.WindowsMetrics?.Password
                    : SecretProtector.Protect(server.WindowsMetrics.Password);
            }

            // Listeyi mutasyon yerine degistirerek guncelliyoruz; okuyanlar eski nesneyle guvende kalir.
            _data.Servers[index] = server;
            Persist();
            return server;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var removed = _data.Servers.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                Persist();
            return removed;
        }
    }

    public MonitorSettings UpdateSettings(MonitorSettings settings)
    {
        lock (_gate)
        {
            _data.IntervalSeconds = Math.Clamp(settings.IntervalSeconds, 5, 3600);
            _data.DefaultTimeoutMs = Math.Clamp(settings.DefaultTimeoutMs, 500, 60000);
            _data.HistorySize = Math.Clamp(settings.HistorySize, 10, 2000);
            _data.HistoryRetentionDays = Math.Clamp(settings.HistoryRetentionDays, 1, 3650);
            Persist();
            return SettingsSnapshot();
        }
    }

    // --- Bildirim ayarlari ---

    public NotificationOptions GetNotifications()
    {
        lock (_gate)
        {
            return _data.Notifications;
        }
    }

    public NotificationOptions UpdateNotifications(NotificationOptions input)
    {
        lock (_gate)
        {
            var existing = _data.Notifications;

            input.FailureThreshold = Math.Clamp(input.FailureThreshold, 1, 100);
            input.Email ??= new EmailOptions();
            input.Webhook ??= new WebhookOptions();
            input.ChangeAlerts ??= new ChangeAlertOptions();
            input.ChangeAlerts.IntervalMinutes = Math.Clamp(input.ChangeAlerts.IntervalMinutes, 5, 1440);
            input.Email.To = input.Email.To.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

            // Parola bos gelirse eskisi korunur; doluysa sifrelenir.
            input.Email.Password = string.IsNullOrEmpty(input.Email.Password)
                ? existing.Email?.Password
                : SecretProtector.Protect(input.Email.Password);

            // Nesneyi degistirerek yaziyoruz; AlertService elindeki eski referansla guvende.
            _data.Notifications = input;
            Persist();
            return input;
        }
    }

    // --- DNS icgoru ayarlari ---

    /// <summary>Toplayici anahtari yoksa uretip kalici yazar; anahtar her zaman dolu doner.</summary>
    public DnsOptions GetDns()
    {
        lock (_gate)
        {
            _data.Dns ??= new DnsOptions();
            if (string.IsNullOrWhiteSpace(_data.Dns.IngestKey))
            {
                _data.Dns.IngestKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                Persist();
            }
            return _data.Dns;
        }
    }

    /// <summary>Panelden gizlenen alan adlarini yonetir (yeniden baslatmadan, aninda gecerli).</summary>
    public IReadOnlyList<string> AddIgnoredDomain(string domain)
    {
        lock (_gate)
        {
            _data.Dns ??= new DnsOptions();
            var d = NormalizeDomain(domain);
            if (d.Length > 0 && !_data.Dns.IgnoredDomains.Any(x => x.Equals(d, StringComparison.OrdinalIgnoreCase)))
            {
                _data.Dns.IgnoredDomains.Add(d);
                Persist();
            }
            return _data.Dns.IgnoredDomains.ToList();
        }
    }

    public IReadOnlyList<string> RemoveIgnoredDomain(string domain)
    {
        lock (_gate)
        {
            _data.Dns ??= new DnsOptions();
            var d = NormalizeDomain(domain);
            if (_data.Dns.IgnoredDomains.RemoveAll(x => x.Equals(d, StringComparison.OrdinalIgnoreCase)) > 0)
                Persist();
            return _data.Dns.IgnoredDomains.ToList();
        }
    }

    /// <summary>Girdi bir URL bile olsa (https://site.com/yol) sade alan adina indirger.</summary>
    private static string NormalizeDomain(string input)
    {
        var d = (input ?? "").Trim().ToLowerInvariant();
        if (d.Contains("://"))
            d = d[(d.IndexOf("://", StringComparison.Ordinal) + 3)..];
        d = d.Split('/')[0].Split('?')[0].Split(':')[0];
        return d.Trim('.', ' ');
    }

    // --- Giris ayarlari ---

    public AuthOptions GetAuth()
    {
        lock (_gate)
        {
            return _data.Auth;
        }
    }

    /// <summary>Parola bos gelirse mevcut parola korunur. Parolasiz giris etkinlestirilemez.</summary>
    public AuthOptions UpdateAuth(bool enabled, string username, string? newPassword)
    {
        lock (_gate)
        {
            var auth = new AuthOptions
            {
                Enabled = enabled,
                Username = string.IsNullOrWhiteSpace(username) ? "admin" : username.Trim(),
                PasswordHash = _data.Auth.PasswordHash,
                PasswordSalt = _data.Auth.PasswordSalt
            };

            if (!string.IsNullOrEmpty(newPassword))
            {
                if (newPassword.Length < 8)
                    throw new ArgumentException("Parola en az 8 karakter olmalı.");

                (auth.PasswordHash, auth.PasswordSalt) = PasswordHasher.Create(newPassword);
            }

            if (auth.Enabled && string.IsNullOrEmpty(auth.PasswordHash))
                throw new ArgumentException("Girişi etkinleştirmek için bir parola belirleyin.");

            _data.Auth = auth;
            Persist();
            return auth;
        }
    }

    // --- Ic isler ---

    private void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                _logger.LogWarning("servers.json bulunamadi, bos liste ile baslatiliyor: {Path}", _path);
                _data = new MonitorOptions();
                Persist();
                return;
            }

            try
            {
                var json = File.ReadAllText(_path);
                _data = JsonSerializer.Deserialize<MonitorFile>(json, FileJson)?.Monitor ?? new MonitorOptions();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "servers.json okunamadi. Dosyayi duzeltip uygulamayi yeniden baslatin.");
                _data = new MonitorOptions();
                return;
            }

            // Elle eklenmis, Id'si olmayan kayitlara Id uret.
            foreach (var server in _data.Servers)
            {
                if (string.IsNullOrWhiteSpace(server.Id))
                    server.Id = MakeUniqueId(server, _data.Servers);
            }

            _logger.LogInformation("{Count} sunucu tanimi yuklendi.", _data.Servers.Count);
        }
    }

    private void Persist()
    {
        try
        {
            var payload = JsonSerializer.Serialize(new MonitorFile { Monitor = _data }, FileJson);

            // Once gecici dosyaya yaz, sonra yer degistir: yarida kesilirse dosya bozulmasin.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, payload);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "servers.json yazilamadi: {Path}", _path);
            throw new InvalidOperationException($"Ayarlar kaydedilemedi: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Girdiyi temizler ve dogrular. Gecersizse ArgumentException firlatir.</summary>
    private static ServerDefinition Normalize(ServerDefinition input)
    {
        var host = (input.Host ?? "").Trim();
        if (host.Length == 0)
            throw new ArgumentException("Sunucu adresi (IP veya hostname) bos olamaz.");

        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)
            name = host;

        foreach (var port in input.Ports)
        {
            if (port.Port is < 1 or > 65535)
                throw new ArgumentException($"Gecersiz port numarasi: {port.Port}");
        }

        foreach (var http in input.Http)
        {
            if (!Uri.TryCreate(http.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"Gecersiz URL: {http.Url}");
            }
        }

        input.Name = name;
        input.Host = host;
        input.Group = string.IsNullOrWhiteSpace(input.Group) ? "Genel" : input.Group.Trim();

        return input;
    }

    private static string MakeUniqueId(ServerDefinition server, List<ServerDefinition> existing)
    {
        var basis = string.IsNullOrWhiteSpace(server.Name) ? server.Host : server.Name;
        var slug = Slugify(basis);
        if (slug.Length == 0)
            slug = "sunucu";

        var candidate = slug;
        var counter = 2;

        while (existing.Any(s => !ReferenceEquals(s, server) &&
                                 string.Equals(s.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{slug}-{counter++}";
        }

        return candidate;
    }

    /// <summary>URL'de guvenle kullanilabilecek ASCII bir kimlik uretir.</summary>
    private static string Slugify(string value)
    {
        var sb = new StringBuilder();

        foreach (var raw in value.Trim())
        {
            var ch = raw switch
            {
                'ı' or 'I' or 'İ' or 'i' => 'i',
                'ğ' or 'Ğ' => 'g',
                'ü' or 'Ü' => 'u',
                'ş' or 'Ş' => 's',
                'ö' or 'Ö' => 'o',
                'ç' or 'Ç' => 'c',
                _ => char.ToLowerInvariant(raw)
            };

            var keep = ch is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            sb.Append(keep ? ch : '-');
        }

        var slug = sb.ToString();
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        return slug.Trim('-');
    }
}
