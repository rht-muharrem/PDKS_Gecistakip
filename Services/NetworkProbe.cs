using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ServerMonitor.Models;

namespace ServerMonitor.Services;


public sealed class NetworkProbe
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NetworkProbe> _logger;

    public NetworkProbe(IHttpClientFactory httpClientFactory, ILogger<NetworkProbe> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CheckResult> PingAsync(string host, PingCheckOptions options, int defaultTimeoutMs, CancellationToken ct)
    {
        var timeout = options.TimeoutMs ?? defaultTimeoutMs;
        var sw = Stopwatch.StartNew();

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeout);
            sw.Stop();

            if (reply.Status != IPStatus.Success)
            {
                return new CheckResult
                {
                    Type = "ping",
                    Label = "Ping",
                    State = HealthState.Down,
                    DurationMs = sw.Elapsed.TotalMilliseconds,
                    Message = TranslatePingStatus(reply.Status)
                };
            }

            var rtt = reply.RoundtripTime;
            var warn = options.WarnAboveMs > 0 && rtt > options.WarnAboveMs;

            return new CheckResult
            {
                Type = "ping",
                Label = "Ping",
                State = warn ? HealthState.Warning : HealthState.Up,
                DurationMs = rtt,
                Message = warn ? $"{rtt} ms (yavas)" : $"{rtt} ms"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogDebug(ex, "Ping basarisiz: {Host}", host);
            return new CheckResult
            {
                Type = "ping",
                Label = "Ping",
                State = HealthState.Down,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Message = ex.GetBaseException().Message
            };
        }
    }

    public async Task<CheckResult> TcpAsync(string host, PortCheckOptions options, int defaultTimeoutMs, CancellationToken ct)
    {
        var label = string.IsNullOrWhiteSpace(options.Label) ? $"Port {options.Port}" : $"{options.Label} ({options.Port})";
        var timeout = options.TimeoutMs ?? defaultTimeoutMs;
        var sw = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            await client.ConnectAsync(host, options.Port, timeoutCts.Token);
            sw.Stop();

            return new CheckResult
            {
                Type = "port",
                Label = label,
                State = HealthState.Up,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Message = "acik"
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new CheckResult
            {
                Type = "port",
                Label = label,
                State = HealthState.Down,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Message = $"zaman asimi ({timeout} ms)"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new CheckResult
            {
                Type = "port",
                Label = label,
                State = HealthState.Down,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Message = ex.GetBaseException().Message
            };
        }
    }

    public async Task<CheckResult> HttpAsync(HttpCheckOptions options, int defaultTimeoutMs, CancellationToken ct)
    {
        var label = string.IsNullOrWhiteSpace(options.Label) ? ShortUrl(options.Url) : options.Label!;
        var timeout = options.TimeoutMs ?? defaultTimeoutMs;
        var sw = Stopwatch.StartNew();

        try
        {
            var client = _httpClientFactory.CreateClient(
                options.IgnoreCertificateErrors ? HttpClientNames.Insecure : HttpClientNames.Default);
            client.Timeout = TimeSpan.FromMilliseconds(timeout);

            using var response = await client.GetAsync(options.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)response.StatusCode;

            var codeOk = options.ExpectedStatusCodes.Count > 0
                ? options.ExpectedStatusCodes.Contains(code)
                : code is >= 200 and < 400;

            string? bodyProblem = null;
            if (codeOk && !string.IsNullOrEmpty(options.MustContain))
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!body.Contains(options.MustContain, StringComparison.OrdinalIgnoreCase))
                {
                    bodyProblem = $"govdede '{options.MustContain}' bulunamadi";
                }
            }

            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;

            if (!codeOk)
            {
                return new CheckResult
                {
                    Type = "http",
                    Label = label,
                    State = HealthState.Down,
                    DurationMs = ms,
                    Message = $"HTTP {code}"
                };
            }

            if (bodyProblem is not null)
            {
                return new CheckResult
                {
                    Type = "http",
                    Label = label,
                    State = HealthState.Warning,
                    DurationMs = ms,
                    Message = bodyProblem
                };
            }

            var slow = options.WarnAboveMs > 0 && ms > options.WarnAboveMs;
            return new CheckResult
            {
                Type = "http",
                Label = label,
                State = slow ? HealthState.Warning : HealthState.Up,
                DurationMs = ms,
                Message = slow ? $"HTTP {code} - yavas ({ms:F0} ms)" : $"HTTP {code}"
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new CheckResult
            {
                Type = "http",
                Label = label,
                State = HealthState.Down,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Message = $"zaman asimi ({timeout} ms)"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new CheckResult
            {
                Type = "http",
                Label = label,
                State = HealthState.Down,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Message = ex.GetBaseException().Message
            };
        }
    }

    private static string ShortUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Host}{(uri.AbsolutePath == "/" ? "" : uri.AbsolutePath)}"
            : url;
    }

    private static string TranslatePingStatus(IPStatus status) => status switch
    {
        IPStatus.TimedOut => "zaman asimi",
        IPStatus.DestinationHostUnreachable => "host erisilemez",
        IPStatus.DestinationNetworkUnreachable => "ag erisilemez",
        IPStatus.DestinationUnreachable => "erisilemez",
        IPStatus.TtlExpired => "TTL doldu",
        _ => status.ToString()
    };
}

public static class HttpClientNames
{
    public const string Default = "probe";
    public const string Insecure = "probe-insecure";
}
