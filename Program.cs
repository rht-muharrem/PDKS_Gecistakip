using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting.WindowsServices;
using ServerMonitor.Models;
using ServerMonitor.Services;


var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default
});

builder.Host.UseWindowsService(o => o.ServiceName = "ServerMonitor");

builder.Services.AddHttpClient(HttpClientNames.Default)
    .ConfigureHttpClient(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("ServerMonitor/1.0"));

builder.Services.AddHttpClient(HttpClientNames.Insecure)
    .ConfigureHttpClient(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("ServerMonitor/1.0"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
   
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });

builder.Services.AddSingleton<ServerRepository>();
builder.Services.AddSingleton<StatusStore>();
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<DnsInsightStore>();
builder.Services.AddSingleton<BaselineStore>();
builder.Services.AddSingleton<NetworkProbe>();
builder.Services.AddSingleton<WindowsMetricsProbe>();
builder.Services.AddSingleton<MonitorService>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddHostedService<MonitorWorker>();
builder.Services.AddHostedService<ChangeMonitorWorker>();


builder.Services.AddDataProtection()
    .SetApplicationName("ServerMonitor")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")))
    .ProtectKeysWithDpapi();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "ServerMonitor.Auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;

        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;

        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    });

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

app.UseAuthentication();


app.Use(async (context, next) =>
{
    var auth = context.RequestServices.GetRequiredService<ServerRepository>().GetAuth();

    if (!auth.Enabled || string.IsNullOrEmpty(auth.PasswordHash) ||
        IsPublicPath(context.Request.Path) || IsSignedIn(context.User, auth))
    {
        await next();
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { message = "Oturum açmanız gerekiyor." });
        return;
    }

    context.Response.Redirect("/login.html");
});

app.UseDefaultFiles();
app.UseStaticFiles();



app.MapGet("/api/auth/state", (HttpContext http, ServerRepository repo) =>
{
    var auth = repo.GetAuth();
    var active = auth.Enabled && !string.IsNullOrEmpty(auth.PasswordHash);
    return Results.Ok(new
    {
        enabled = active,
        authenticated = !active || IsSignedIn(http.User, auth),
        username = auth.Username
    });
});

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext http, ServerRepository repo) =>
{
    var auth = repo.GetAuth();
    if (!auth.Enabled || string.IsNullOrEmpty(auth.PasswordHash))
        return Results.Ok(new { ok = true });

    var valid = string.Equals(request.Username?.Trim(), auth.Username, StringComparison.OrdinalIgnoreCase)
                && PasswordHasher.Verify(request.Password ?? "", auth.PasswordHash, auth.PasswordSalt);

    if (!valid)
    {

        await Task.Delay(800);
        return Results.Json(new { message = "Kullanıcı adı veya parola hatalı." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var identity = new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.Name, auth.Username),
   
            new Claim("stamp", auth.PasswordSalt ?? "")
        },
        CookieAuthenticationDefaults.AuthenticationScheme);


    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true });
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { ok = true });
});

app.MapPut("/api/auth/settings", (AuthSettingsRequest request, ServerRepository repo) =>
{
    try
    {
        var auth = repo.UpdateAuth(request.Enabled, request.Username ?? "", request.Password);
        return Results.Ok(new { enabled = auth.Enabled, username = auth.Username });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});



app.MapGet("/api/status", (StatusStore store, ServerRepository repo) =>
{
    var servers = store.GetAll();
    return Results.Ok(new
    {
        lastRunUtc = store.LastRunUtc,
        intervalSeconds = repo.GetSettings().IntervalSeconds,
        servers,
        summary = Summarize(servers)
    });
});

app.MapGet("/api/status/{id}", (string id, StatusStore store) =>
{
    var status = store.Get(id);
    return status is null ? Results.NotFound(new { message = $"'{id}' bulunamadi." }) : Results.Ok(status);
});


app.MapGet("/api/status/{id}/history", async (string id, int? hours, StatusStore store, HistoryStore history, CancellationToken ct) =>
{
    if (hours is > 0)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-Math.Min(hours.Value, 24 * 365));
        return Results.Ok(await history.LoadSinceAsync(id, since, ct));
    }

    var status = store.Get(id);
    return status is null
        ? Results.NotFound(new { message = $"'{id}' bulunamadi." })
        : Results.Ok(status.History);
});

app.MapGet("/api/uptime", async (int? days, HistoryStore history, ServerRepository repo, CancellationToken ct) =>
{
    var period = Math.Clamp(days ?? 7, 1, 365);
    var counts = await history.GetUptimeAsync(DateTimeOffset.UtcNow.AddDays(-period), ct);

    var rows = repo.GetServers().Select(s =>
    {
        counts.TryGetValue(s.Id ?? "", out var c);
        return new
        {
            id = s.Id,
            name = s.Name,
            host = s.Host,
            measurements = c.Total,
            uptimePercent = c.Total == 0 ? (double?)null : Math.Round(c.Healthy * 100.0 / c.Total, 2)
        };
    });

    return Results.Ok(new { days = period, servers = rows });
});

app.MapPost("/api/refresh", async (MonitorService monitor, CancellationToken ct) =>
{
    var results = await monitor.RunAllAsync(ct);
    return Results.Ok(new { servers = results, summary = Summarize(results) });
});

app.MapPost("/api/refresh/{id}", async (string id, MonitorService monitor, CancellationToken ct) =>
{
    var status = await monitor.RunOneAsync(id, ct);
    return status is null
        ? Results.NotFound(new { message = $"'{id}' bulunamadi veya pasif." })
        : Results.Ok(status);
});



app.MapGet("/api/servers", (ServerRepository repo) =>
    Results.Ok(repo.GetServers().Select(ToDto)));

app.MapGet("/api/servers/{id}", (string id, ServerRepository repo) =>
{
    var server = repo.Get(id);
    return server is null ? Results.NotFound(new { message = $"'{id}' bulunamadi." }) : Results.Ok(ToDto(server));
});


app.MapGet("/api/servers/{id}/processes", async (string id, bool? all, ServerRepository repo, WindowsMetricsProbe wmi, CancellationToken ct) =>
{
    var server = repo.Get(id);
    if (server is null)
        return Results.NotFound(new { message = $"'{id}' bulunamadi." });

    if (server.WindowsMetrics is not { Enabled: true } wmiOptions)
        return Results.BadRequest(new { message = "Bu cihazda Windows metrikleri (WMI) etkin değil." });

    return Results.Ok(await wmi.GetProcessesAsync(server.Host, wmiOptions, all == true, ct));
});


app.MapGet("/api/servers/{id}/info", async (string id, ServerRepository repo, WindowsMetricsProbe wmi, CancellationToken ct) =>
{
    var server = repo.Get(id);
    if (server is null)
        return Results.NotFound(new { message = $"'{id}' bulunamadi." });
    if (server.WindowsMetrics is not { Enabled: true } o)
        return Results.BadRequest(new { message = "Bu cihazda Windows metrikleri (WMI) etkin değil." });

    return Results.Ok(await wmi.GetDeviceInfoAsync(server.Host, o, ct));
});

app.MapGet("/api/servers/{id}/programs", async (string id, ServerRepository repo, WindowsMetricsProbe wmi, CancellationToken ct) =>
{
    var server = repo.Get(id);
    if (server is null)
        return Results.NotFound(new { message = $"'{id}' bulunamadi." });
    if (server.WindowsMetrics is not { Enabled: true } o)
        return Results.BadRequest(new { message = "Bu cihazda Windows metrikleri (WMI) etkin değil." });

    return Results.Ok(await wmi.GetInstalledProgramsAsync(server.Host, o, ct));
});


app.MapGet("/api/servers/{id}/usb", async (string id, ServerRepository repo, WindowsMetricsProbe wmi, CancellationToken ct) =>
{
    var server = repo.Get(id);
    if (server is null)
        return Results.NotFound(new { message = $"'{id}' bulunamadi." });
    if (server.WindowsMetrics is not { Enabled: true } o)
        return Results.BadRequest(new { message = "Bu cihazda Windows metrikleri (WMI) etkin değil." });

    return Results.Ok(await wmi.GetUsbAsync(server.Host, o, ct));
});

app.MapPost("/api/servers", async (ServerDefinition input, ServerRepository repo, MonitorService monitor, CancellationToken ct) =>
{
    try
    {
        var created = repo.Add(input);
        await monitor.RunOneAsync(created.Id!, ct);
        return Results.Created($"/api/servers/{created.Id}", ToDto(created));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPut("/api/servers/{id}", async (string id, ServerDefinition input, ServerRepository repo, MonitorService monitor, StatusStore store, CancellationToken ct) =>
{
    try
    {
        var updated = repo.Update(id, input);
        if (updated is null)
            return Results.NotFound(new { message = $"'{id}' bulunamadi." });

        store.Remove(id);
        await monitor.RunOneAsync(id, ct);
        return Results.Ok(ToDto(updated));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapDelete("/api/servers/{id}", async (string id, ServerRepository repo, StatusStore store, HistoryStore history, BaselineStore baseline, AlertService alerts, CancellationToken ct) =>
{
    if (!repo.Delete(id))
        return Results.NotFound(new { message = $"'{id}' bulunamadi." });

    store.Remove(id);
    alerts.Forget(id);
    await history.DeleteServerAsync(id, ct);
    await baseline.DeleteServerAsync(id, ct);
    return Results.NoContent();
});



app.MapGet("/api/settings", (ServerRepository repo) => Results.Ok(repo.GetSettings()));

app.MapPut("/api/settings", (MonitorSettings settings, ServerRepository repo) =>
{
    try
    {
        return Results.Ok(repo.UpdateSettings(settings));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/api/notifications", (ServerRepository repo) => Results.Ok(NotificationsDto(repo.GetNotifications())));

app.MapPut("/api/notifications", (NotificationOptions input, ServerRepository repo) =>
{
    try
    {
        return Results.Ok(NotificationsDto(repo.UpdateNotifications(input)));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/notifications/test", async (AlertService alerts, CancellationToken ct) =>
{
    var errors = await alerts.SendTestAsync(ct);
    return errors.Count == 0
        ? Results.Ok(new { ok = true, message = "Test bildirimi gönderildi." })
        : Results.BadRequest(new { ok = false, message = string.Join(" · ", errors) });
});


app.MapPost("/api/dns/ingest", (HttpContext http, DnsIngest packet, ServerRepository repo, DnsInsightStore store) =>
{
    var expected = repo.GetDns().IngestKey;
    var provided = http.Request.Headers["X-Ingest-Key"].ToString();

    if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided), System.Text.Encoding.UTF8.GetBytes(expected)))
    {
        return Results.Json(new { message = "Geçersiz anahtar." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    store.Ingest(packet);
    return Results.Ok(new { ok = true, received = packet.Entries.Count });
});


app.MapGet("/api/dns/config", (ServerRepository repo, DnsInsightStore store) =>
{
    var dns = repo.GetDns();
    return Results.Ok(new { ingestKey = dns.IngestKey, retentionMinutes = dns.RetentionMinutes, hasData = store.HasData });
});


app.MapGet("/api/servers/{id}/dns", (string id, int? take, bool? all, ServerRepository repo, DnsInsightStore store) =>
{
    var server = repo.Get(id);
    if (server is null)
        return Results.NotFound(new { message = $"'{id}' bulunamadi." });

    var dns = repo.GetDns();
    var ips = ResolveHostIps(server.Host);


    var (updated, window, top) = store.Query(
        ips, Math.Clamp(take ?? 15, 1, 100),
        extraIgnore: dns.IgnoredDomains, allowed: dns.AllowedDomains, applyFilter: all != true);

    return Results.Ok(new { updatedUtc = updated, windowMinutes = window, domains = top, hasStore = store.HasData, filtered = all != true });
});


app.MapGet("/api/dns/lists", (ServerRepository repo) =>
{
    var dns = repo.GetDns();
    return Results.Ok(new { ignored = dns.IgnoredDomains, allowed = dns.AllowedDomains });
});

app.MapPost("/api/dns/ignore", (DomainRequest req, ServerRepository repo) =>
    Results.Ok(new { ignored = repo.AddIgnoredDomain(req.Domain ?? "") }));

app.MapPost("/api/dns/unignore", (DomainRequest req, ServerRepository repo) =>
    Results.Ok(new { ignored = repo.RemoveIgnoredDomain(req.Domain ?? "") }));


app.MapGet("/api/report/word", (string? category, string? group, StatusStore store) =>
{
    var servers = store.GetAll().Where(s =>
        (string.IsNullOrEmpty(category) || category == "all" || s.Category.ToString().Equals(category, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(group) || string.Equals(s.Group, group, StringComparison.OrdinalIgnoreCase)))
        .ToList();

    var scope = new List<string>();
    if (category is "Server") scope.Add("Sunucular");
    else if (category is "Computer") scope.Add("Bilgisayarlar");
    if (!string.IsNullOrEmpty(group)) scope.Add(group);

    var bytes = ReportService.BuildStatusReport(servers, string.Join(" · ", scope));
    var name = $"cihaz-durum-{DateTime.Now:yyyy-MM-dd-HHmm}.docx";
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", name);
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));



app.MapGet("/icons/icon-{size:int}.png", (int size, HttpContext http) =>
{
    if (!IconRenderer.AllowedSizes.Contains(size))
        return Results.NotFound();

    http.Response.Headers.CacheControl = "public, max-age=604800";
    return Results.File(IconRenderer.GetPng(size), "image/png");
});

app.MapGet("/apple-touch-icon.png", (HttpContext http) =>
{
    http.Response.Headers.CacheControl = "public, max-age=604800";
    return Results.File(IconRenderer.GetPng(180), "image/png");
});

app.Run();



static bool IsPublicPath(PathString path) =>
    path.Equals("/login.html", StringComparison.OrdinalIgnoreCase) ||
    path.Equals("/style.css", StringComparison.OrdinalIgnoreCase) ||
    path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) ||
    path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||

    path.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase) ||
    path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase) ||
    path.Equals("/apple-touch-icon.png", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWithSegments("/icons") ||
    path.StartsWithSegments("/api/auth/login") ||
    path.StartsWithSegments("/api/auth/logout") ||
    path.StartsWithSegments("/api/auth/state") ||

    path.StartsWithSegments("/api/dns/ingest");


static List<string> ResolveHostIps(string host)
{
    host = host.Trim();
    if (IPAddress.TryParse(host, out var literal))
        return new List<string> { literal.ToString() };

    try
    {
        return Dns.GetHostAddresses(host)
            .Where(a => a.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6)
            .Select(a => a.ToString())
            .Distinct()
            .ToList();
    }
    catch
    {
        return new List<string>();
    }
}

static bool IsSignedIn(ClaimsPrincipal user, AuthOptions auth) =>
    user.Identity?.IsAuthenticated == true &&
    string.Equals(user.Identity.Name, auth.Username, StringComparison.OrdinalIgnoreCase) &&
    user.FindFirst("stamp")?.Value == auth.PasswordSalt;

static object Summarize(IReadOnlyList<ServerStatus> servers) => new
{
    total = servers.Count,
    up = servers.Count(s => s.State == HealthState.Up),
    warning = servers.Count(s => s.State == HealthState.Warning),
    down = servers.Count(s => s.State == HealthState.Down),
    unknown = servers.Count(s => s.State == HealthState.Unknown)
};

static object ToDto(ServerDefinition s) => new
{
    id = s.Id,
    name = s.Name,
    host = s.Host,
    group = s.Group,
    category = s.Category,
    enabled = s.Enabled,
    ping = s.Ping,
    ports = s.Ports,
    http = s.Http,
    windowsMetrics = s.WindowsMetrics is null ? null : (object)new
    {
        enabled = s.WindowsMetrics.Enabled,
        username = s.WindowsMetrics.Username,
        hasPassword = !string.IsNullOrEmpty(s.WindowsMetrics.Password),
        timeoutMs = s.WindowsMetrics.TimeoutMs,
        cpuWarnPercent = s.WindowsMetrics.CpuWarnPercent,
        cpuCriticalPercent = s.WindowsMetrics.CpuCriticalPercent,
        memoryWarnPercent = s.WindowsMetrics.MemoryWarnPercent,
        memoryCriticalPercent = s.WindowsMetrics.MemoryCriticalPercent,
        diskWarnPercent = s.WindowsMetrics.DiskWarnPercent,
        diskCriticalPercent = s.WindowsMetrics.DiskCriticalPercent,
        drives = s.WindowsMetrics.Drives
    }
};

static object NotificationsDto(NotificationOptions n) => new
{
    enabled = n.Enabled,
    failureThreshold = n.FailureThreshold,
    notifyOnRecovery = n.NotifyOnRecovery,
    notifyComputers = n.NotifyComputers,
    email = new
    {
        enabled = n.Email.Enabled,
        host = n.Email.Host,
        port = n.Email.Port,
        useSsl = n.Email.UseSsl,
        username = n.Email.Username,
        hasPassword = !string.IsNullOrEmpty(n.Email.Password),
        from = n.Email.From,
        to = n.Email.To
    },
    webhook = new { enabled = n.Webhook.Enabled, url = n.Webhook.Url },
    changeAlerts = new
    {
        enabled = n.ChangeAlerts.Enabled,
        intervalMinutes = n.ChangeAlerts.IntervalMinutes,
        watchAntivirus = n.ChangeAlerts.WatchAntivirus,
        watchNewSoftware = n.ChangeAlerts.WatchNewSoftware,
        watchUsb = n.ChangeAlerts.WatchUsb
    }
};

record DomainRequest(string? Domain);

record LoginRequest(string? Username, string? Password);

record AuthSettingsRequest(bool Enabled, string? Username, string? Password);
