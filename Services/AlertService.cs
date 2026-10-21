using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using ServerMonitor.Models;

namespace ServerMonitor.Services;

/// <summary>
/// Her turun sonuclarina bakip durum gecislerinde bildirim gonderir:
/// - Sunucu FailureThreshold tur ust uste SORUNLU olursa bir kez "sorun" bildirimi
/// - Sonra tekrar ayaga kalkarsa bir kez "duzeldi" bildirimi
/// Sorun surdukce tekrar tekrar mesaj atilmaz.
/// </summary>
public sealed class AlertService
{
    private sealed class AlertState
    {
        public int ConsecutiveDown;
        public bool Notified;
        public DateTimeOffset? DownSince;
    }

    /// <summary>Konu, duz metin (webhook ve HTML gostermeyen istemciler) ve HTML govde.</summary>
    private sealed record AlertMessage(string Subject, string Text, string Html);

    private readonly ServerRepository _repository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AlertService> _logger;
    private readonly ConcurrentDictionary<string, AlertState> _states = new(StringComparer.OrdinalIgnoreCase);

    public AlertService(
        ServerRepository repository,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AlertService> logger)
    {
        _repository = repository;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task EvaluateAsync(IReadOnlyList<ServerStatus> results, CancellationToken ct)
    {
        var options = _repository.GetNotifications();

        foreach (var status in results)
        {
            // Bilgisayarlarin kapanmasi normal; ayar acik degilse hic takip etme.
            if (status.Category == DeviceCategory.Computer && !options.NotifyComputers)
            {
                _states.TryRemove(status.Id, out _);
                continue;
            }

            var alert = _states.GetOrAdd(status.Id, _ => new AlertState());

            if (status.State == HealthState.Down)
            {
                alert.ConsecutiveDown++;
                alert.DownSince ??= status.LastCheckUtc ?? DateTimeOffset.UtcNow;

                if (!alert.Notified && alert.ConsecutiveDown >= Math.Max(1, options.FailureThreshold))
                {
                    alert.Notified = true;

                    if (options.Enabled)
                        await SendAsync(options, BuildDownMessage(status, alert.DownSince.Value), ct);
                }
            }
            else if (status.State != HealthState.Unknown)
            {
                if (alert.Notified && options.Enabled && options.NotifyOnRecovery)
                    await SendAsync(options, BuildRecoveryMessage(status, alert.DownSince), ct);

                alert.ConsecutiveDown = 0;
                alert.Notified = false;
                alert.DownSince = null;
            }
        }
    }

    public void Forget(string serverId) => _states.TryRemove(serverId, out _);

    /// <summary>Panelden "test gonder" icin. Hatalari toplayip doner.</summary>
    public async Task<List<string>> SendTestAsync(CancellationToken ct)
    {
        var options = _repository.GetNotifications();
        return await SendAsync(options, BuildTestMessage(), ct);
    }

    /// <summary>Bir cihazda tespit edilen degisiklikleri bildirir (antivirüs/program/USB).</summary>
    public async Task SendChangeAlertAsync(string name, string host, IReadOnlyList<string> changes, CancellationToken ct)
    {
        var options = _repository.GetNotifications();
        if (!options.Enabled || changes.Count == 0)
            return;

        await SendAsync(options, BuildChangeMessage(name, host, changes), ct);
    }

    private AlertMessage BuildChangeMessage(string name, string host, IReadOnlyList<string> changes)
    {
        var text = new StringBuilder()
            .AppendLine($"Cihaz : {name} ({host})")
            .AppendLine($"Zaman : {Local(DateTimeOffset.UtcNow)}")
            .AppendLine()
            .AppendLine("Tespit edilen değişiklikler:");
        foreach (var change in changes)
            text.AppendLine($"  - {change}");

        var items = string.Concat(changes.Select(c =>
            $"<tr><td style=\"padding:8px 14px;border-bottom:1px solid #eef1f4;font-family:{Font};font-size:13px;color:#1b222b;\">• {H(c)}</td></tr>"));

        var content =
            Paragraph($"<b>{H(name)}</b> ({H(host)}) cihazında aşağıdaki değişiklik(ler) tespit edildi.") +
            SectionTitle("Değişiklikler") +
            $"<table role=\"presentation\" align=\"center\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border:1px solid #e3e7ec;border-radius:8px;margin:0 auto;\">{items}</table>";

        return new AlertMessage(
            $"🔔 Değişiklik: {name} ({host})",
            text.ToString(),
            Layout(ColorInfo, "DEĞİŞİKLİK TESPİT EDİLDİ", name, host, content,
                   $"{name} cihazında {changes.Count} değişiklik"));
    }

    // =====================================================
    //  Mesaj icerikleri
    // =====================================================

    private static readonly string ColorDown = "#e5484d";
    private static readonly string ColorUp = "#2ea043";
    private static readonly string ColorWarn = "#d29922";
    private static readonly string ColorInfo = "#3b82f6";
    private static readonly string ColorUnknown = "#8b96a5";

    private AlertMessage BuildDownMessage(ServerStatus status, DateTimeOffset since)
    {
        var failed = status.Checks.Where(c => c.State != HealthState.Up).ToList();
        var duration = FormatDuration(DateTimeOffset.UtcNow - since);

        var text = new StringBuilder()
            .AppendLine($"Sunucu   : {status.Name} ({status.Host})")
            .AppendLine($"Grup     : {status.Group}")
            .AppendLine("Durum    : SORUNLU")
            .AppendLine($"Başlangıç: {Local(since)}")
            .AppendLine($"Süre     : {duration}")
            .AppendLine()
            .AppendLine("Başarısız kontroller:");

        foreach (var check in failed)
            text.AppendLine($"  - {check.Label}: {check.Message}");

        text.AppendLine().AppendLine($"Panel: {PanelUrl()}");

        var content =
            Paragraph($"<b>{H(status.Name)}</b> sunucusu ulaşılamıyor veya kontrollerden en az biri başarısız. " +
                      "Sorun sürdüğü sürece yeni e-posta gönderilmeyecek; düzeldiğinde ayrıca bilgilendirileceksiniz.") +
            InfoTable(
                ("Grup", H(status.Group)),
                ("Durum", Pill("SORUNLU", ColorDown)),
                ("Başlangıç", H(Local(since))),
                ("Süre", H(duration))) +
            SectionTitle("Başarısız kontroller") +
            ChecksTable(failed.Count > 0 ? failed : status.Checks) +
            MetricsSection(status.Metrics);

        return new AlertMessage(
            $"🔴 SORUN: {status.Name} ({status.Host})",
            text.ToString(),
            Layout(ColorDown, "SORUN TESPİT EDİLDİ", status.Name, status.Host, content,
                   $"{status.Name} sunucusunda sorun var · {duration}"));
    }

    private AlertMessage BuildRecoveryMessage(ServerStatus status, DateTimeOffset? since)
    {
        var duration = since is null ? "bilinmiyor" : FormatDuration(DateTimeOffset.UtcNow - since.Value);
        var stateText = StateText(status.State);

        var text = new StringBuilder()
            .AppendLine($"Sunucu : {status.Name} ({status.Host})")
            .AppendLine($"Grup   : {status.Group}")
            .AppendLine($"Durum  : {stateText}")
            .AppendLine($"Kesinti süresi: {duration}")
            .AppendLine()
            .AppendLine($"Panel: {PanelUrl()}")
            .ToString();

        var rows = new List<(string, string)>
        {
            ("Grup", H(status.Group)),
            ("Durum", Pill(stateText, StateColor(status.State))),
            ("Kesinti süresi", $"<b>{H(duration)}</b>")
        };

        if (since is not null)
            rows.Add(("Sorun başlangıcı", H(Local(since.Value))));

        rows.Add(("Düzelme", H(Local(DateTimeOffset.UtcNow))));

        var content =
            Paragraph($"<b>{H(status.Name)}</b> sunucusu tekrar erişilebilir durumda.") +
            InfoTable(rows.ToArray()) +
            SectionTitle("Güncel kontroller") +
            ChecksTable(status.Checks) +
            MetricsSection(status.Metrics);

        return new AlertMessage(
            $"🟢 DÜZELDİ: {status.Name} ({status.Host})",
            text,
            Layout(ColorUp, "SORUN GİDERİLDİ", status.Name, status.Host, content,
                   $"{status.Name} tekrar çalışıyor · kesinti {duration}"));
    }

    private AlertMessage BuildTestMessage()
    {
        var now = Local(DateTimeOffset.UtcNow);

        var content =
            Paragraph("Bu bir test e-postasıdır. Bu mesajı görüyorsanız bildirim ayarlarınız doğru çalışıyor; " +
                      "bir sunucu sorunlu olduğunda bu adrese bildirim gelecek.") +
            InfoTable(
                ("Gönderen makine", H(Environment.MachineName)),
                ("Zaman", H(now)));

        return new AlertMessage(
            "✅ Sunucu Durum Paneli - test bildirimi",
            $"Bu bir test mesajıdır. Bildirim ayarlarınız çalışıyor.\nZaman: {now}\nPanel: {PanelUrl()}",
            Layout(ColorInfo, "TEST BİLDİRİMİ", "Bildirimler çalışıyor", Environment.MachineName, content,
                   "Sunucu Durum Paneli test e-postası"));
    }

    // =====================================================
    //  HTML sablon parcalari
    //  E-posta istemcileri (ozellikle Outlook masaustu) modern CSS desteklemez:
    //  tablo duzeni, satir ici stil ve bgcolor kullaniliyor.
    // =====================================================

    private const string Font = "'Segoe UI',Arial,Helvetica,sans-serif";

    private string Layout(string accent, string eyebrow, string title, string subtitle, string content, string preheader)
    {
        var panelUrl = PanelUrl();

        return $$"""
            <!DOCTYPE html>
            <html lang="tr">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <meta name="color-scheme" content="light">
              <title>{{H(title)}}</title>
            </head>
            <body style="margin:0;padding:0;background:#f3f5f8;">
              <div style="display:none;max-height:0;overflow:hidden;opacity:0;">{{H(preheader)}}</div>

              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="#f3f5f8" style="background:#f3f5f8;">
                <tr>
                  <td align="center" style="padding:28px 12px;">

                    <table role="presentation" width="600" cellpadding="0" cellspacing="0" border="0"
                           style="width:100%;max-width:600px;background:#ffffff;border:1px solid #e3e7ec;border-radius:10px;overflow:hidden;">

                      <!-- Baslik bandi -->
                      <tr>
                        <td bgcolor="{{accent}}" style="background:{{accent}};padding:26px 40px;">
                          <div style="font-family:{{Font}};font-size:12px;font-weight:700;letter-spacing:1.5px;color:#ffffff;opacity:0.9;">{{H(eyebrow)}}</div>
                          <div style="font-family:{{Font}};font-size:24px;font-weight:700;color:#ffffff;margin-top:6px;line-height:1.25;">{{H(title)}}</div>
                          <div style="font-family:Consolas,'Courier New',monospace;font-size:13px;color:#ffffff;opacity:0.9;margin-top:4px;">{{H(subtitle)}}</div>
                        </td>
                      </tr>

                      <!-- Icerik -->
                      <tr>
                        <td align="center" style="padding:28px 40px 8px;font-family:{{Font}};font-size:14px;line-height:1.55;color:#1b222b;text-align:left;">
                          {{content}}
                        </td>
                      </tr>

                      <!-- Panel dugmesi -->
                      <tr>
                        <td align="center" style="padding:20px 40px 34px;">
                          <table role="presentation" align="center" cellpadding="0" cellspacing="0" border="0">
                            <tr>
                              <td bgcolor="#1b222b" style="background:#1b222b;border-radius:6px;">
                                <a href="{{H(panelUrl)}}" target="_blank"
                                   style="display:inline-block;padding:11px 22px;font-family:{{Font}};font-size:14px;font-weight:600;color:#ffffff;text-decoration:none;">
                                  Paneli aç →
                                </a>
                              </td>
                            </tr>
                          </table>
                        </td>
                      </tr>

                      <!-- Alt bilgi -->
                      <tr>
                        <td bgcolor="#f8fafc" align="center" style="background:#f8fafc;border-top:1px solid #e3e7ec;padding:16px 40px;font-family:{{Font}};font-size:12px;color:#6b7683;text-align:center;">
                          Bu e-posta <b>Sunucu Durum Paneli</b> tarafından otomatik gönderildi · {{H(Environment.MachineName)}} · {{H(Local(DateTimeOffset.UtcNow))}}
                        </td>
                      </tr>

                    </table>

                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
    }

    private static string Paragraph(string html) =>
        $"""<p style="margin:0 0 18px;">{html}</p>""";

    private static string SectionTitle(string title) =>
        $$"""<div style="font-family:{{Font}};font-size:11px;font-weight:700;letter-spacing:1.2px;color:#6b7683;text-transform:uppercase;margin:22px 0 8px;">{{H(title)}}</div>""";

    private static string Pill(string text, string color) =>
        $$"""<span style="display:inline-block;padding:2px 10px;border-radius:999px;background:{{color}};color:#ffffff;font-size:12px;font-weight:700;">{{H(text)}}</span>""";

    /// <summary>Anahtar/deger satirlari. Degerler cagiran tarafta encode edilmis HTML olmalidir.</summary>
    private static string InfoTable(params (string Key, string ValueHtml)[] rows)
    {
        var sb = new StringBuilder();
        sb.Append("""<table role="presentation" align="center" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid #e3e7ec;border-radius:8px;margin:0 auto;">""");

        for (var i = 0; i < rows.Length; i++)
        {
            var border = i == rows.Length - 1 ? "" : "border-bottom:1px solid #eef1f4;";
            sb.Append($$"""
                <tr>
                  <td width="38%" style="padding:11px 18px;{{border}}font-family:{{Font}};font-size:13px;color:#6b7683;">{{H(rows[i].Key)}}</td>
                  <td style="padding:11px 18px;{{border}}font-family:{{Font}};font-size:13px;color:#1b222b;">{{rows[i].ValueHtml}}</td>
                </tr>
                """);
        }

        sb.Append("</table>");
        return sb.ToString();
    }

    private static string ChecksTable(IEnumerable<CheckResult> checks)
    {
        var list = checks.ToList();
        if (list.Count == 0)
            return Paragraph("<i>Tanımlı kontrol yok.</i>");

        var sb = new StringBuilder();
        sb.Append("""<table role="presentation" align="center" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid #e3e7ec;border-radius:8px;margin:0 auto;">""");

        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            var border = i == list.Count - 1 ? "" : "border-bottom:1px solid #eef1f4;";
            var color = StateColor(c.State);

            sb.Append($$"""
                <tr>
                  <td width="26" style="padding:11px 0 11px 18px;{{border}}font-size:16px;line-height:1;color:{{color}};">&#9679;</td>
                  <td style="padding:11px 8px;{{border}}font-family:{{Font}};font-size:13px;font-weight:600;color:#1b222b;">{{H(c.Label)}}</td>
                  <td align="right" style="padding:11px 18px;{{border}}font-family:{{Font}};font-size:13px;color:#6b7683;">{{H(c.Message ?? "")}}</td>
                </tr>
                """);
        }

        sb.Append("</table>");
        return sb.ToString();
    }

    private static string MetricsSection(MetricsSnapshot? metrics)
    {
        if (metrics is null)
            return "";

        var bars = new List<string>();

        if (metrics.CpuPercent is { } cpu)
            bars.Add(MetricBar("CPU", cpu, $"%{cpu:F0}"));

        if (metrics.MemoryPercent is { } mem)
        {
            var detail = metrics.MemoryTotalBytes is { } total && metrics.MemoryFreeBytes is { } free
                ? $"{FormatBytes(total - free)} / {FormatBytes(total)}"
                : $"%{mem:F0}";
            bars.Add(MetricBar("RAM", mem, detail));
        }

        foreach (var drive in metrics.Drives)
            bars.Add(MetricBar($"Disk {drive.Name}", drive.UsedPercent, $"{FormatBytes(drive.FreeBytes)} boş"));

        if (bars.Count == 0)
            return "";

        // Diger bolumlerle ayni cerceveli kutu gorunumu icin ic tabloyu dolguyla sariyoruz.
        return SectionTitle("Kaynak kullanımı") +
               """<table role="presentation" align="center" width="100%" cellpadding="0" cellspacing="0" border="0" style="border:1px solid #e3e7ec;border-radius:8px;margin:0 auto;"><tr><td style="padding:10px 18px;">""" +
               """<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">""" +
               string.Concat(bars) +
               "</table></td></tr></table>";
    }

    private static string MetricBar(string label, double percent, string detail)
    {
        var p = (int)Math.Round(Math.Clamp(percent, 0, 100));
        var color = p >= 95 ? ColorDown : p >= 85 ? ColorWarn : ColorUp;

        // Outlook yuzde genislikli bos hucreleri cokertebildigi icin iki hucreyi de dolu tutuyoruz.
        var fill = Math.Clamp(p, 1, 99);
        var bar = p >= 100
            ? $$"""<td bgcolor="{{color}}" style="background:{{color}};height:8px;font-size:0;line-height:0;">&nbsp;</td>"""
            : $$"""
              <td width="{{fill}}%" bgcolor="{{color}}" style="background:{{color}};height:8px;font-size:0;line-height:0;">&nbsp;</td>
              <td bgcolor="#e9edf2" style="background:#e9edf2;height:8px;font-size:0;line-height:0;">&nbsp;</td>
              """;

        return $$"""
            <tr>
              <td width="70" style="padding:7px 10px 7px 0;font-family:{{Font}};font-size:13px;color:#6b7683;white-space:nowrap;">{{H(label)}}</td>
              <td style="padding:7px 0;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="border-radius:4px;overflow:hidden;">
                  <tr>{{bar}}</tr>
                </table>
              </td>
              <td width="44" align="right" style="padding:7px 0 7px 10px;font-family:{{Font}};font-size:13px;font-weight:700;color:{{color}};">%{{p}}</td>
              <td width="120" align="right" style="padding:7px 0 7px 10px;font-family:{{Font}};font-size:12px;color:#6b7683;white-space:nowrap;">{{H(detail)}}</td>
            </tr>
            """;
    }

    // =====================================================
    //  Yardimcilar
    // =====================================================

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Local(DateTimeOffset value) => value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");

    private static string StateText(HealthState state) => state switch
    {
        HealthState.Up => "ÇALIŞIYOR",
        HealthState.Warning => "UYARI",
        HealthState.Down => "SORUNLU",
        _ => "BİLİNMİYOR"
    };

    private static string StateColor(HealthState state) => state switch
    {
        HealthState.Up => ColorUp,
        HealthState.Warning => ColorWarn,
        HealthState.Down => ColorDown,
        _ => ColorUnknown
    };

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return $"{Math.Max(1, (int)span.TotalSeconds)} sn";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} dk {span.Seconds} sn";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} saat {span.Minutes} dk";
        return $"{(int)span.TotalDays} gün {span.Hours} saat";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value >= 100 || unit == 0 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
    }

    /// <summary>
    /// E-postadaki "Paneli aç" adresi. appsettings.json'da "PanelUrl" verilirse o kullanilir;
    /// yoksa Kestrel adresindeki 0.0.0.0 bu makinenin adiyla degistirilir.
    /// </summary>
    private string PanelUrl()
    {
        var configured = _configuration["PanelUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        var machine = Environment.MachineName.ToLowerInvariant();
        var url = _configuration["Kestrel:Endpoints:Http:Url"] ?? "http://localhost:5080";

        return url.Replace("0.0.0.0", machine).Replace("[::]", machine).Replace("*", machine).Replace("+", machine).TrimEnd('/');
    }

    // =====================================================
    //  Gonderim
    // =====================================================

    private async Task<List<string>> SendAsync(NotificationOptions options, AlertMessage message, CancellationToken ct)
    {
        var errors = new List<string>();

        if (options.Email.Enabled)
        {
            try
            {
                await SendEmailAsync(options.Email, message, ct);
                _logger.LogInformation("E-posta bildirimi gonderildi: {Subject}", message.Subject);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "E-posta bildirimi gonderilemedi.");
                errors.Add("E-posta: " + ex.GetBaseException().Message);
            }
        }

        if (options.Webhook.Enabled)
        {
            try
            {
                await SendWebhookAsync(options.Webhook, message, ct);
                _logger.LogInformation("Webhook bildirimi gonderildi: {Subject}", message.Subject);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Webhook bildirimi gonderilemedi.");
                errors.Add("Webhook: " + ex.GetBaseException().Message);
            }
        }

        if (!options.Email.Enabled && !options.Webhook.Enabled)
            errors.Add("Hiçbir bildirim kanalı (e-posta veya webhook) etkin değil.");

        return errors;
    }

    private static async Task SendEmailAsync(EmailOptions o, AlertMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.From) || o.To.Count == 0)
            throw new InvalidOperationException("SMTP sunucusu, gönderen ve en az bir alıcı gerekli.");

        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.UseSsl };

        if (!string.IsNullOrWhiteSpace(o.Username))
            client.Credentials = new NetworkCredential(o.Username, SecretProtector.Unprotect(o.Password));

        using var mail = new MailMessage
        {
            From = new MailAddress(o.From, "Sunucu Durum Paneli"),
            Subject = message.Subject,
            SubjectEncoding = Encoding.UTF8,
            // Duz metin ana govde; HTML alternatif olarak eklenir, destekleyen istemci onu gosterir.
            Body = message.Text,
            BodyEncoding = Encoding.UTF8
        };

        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Html, Encoding.UTF8, MediaTypeNames.Text.Html));

        foreach (var to in o.To.Where(t => !string.IsNullOrWhiteSpace(t)))
            mail.To.Add(to.Trim());

        await client.SendMailAsync(mail, ct);
    }

    private async Task SendWebhookAsync(WebhookOptions o, AlertMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.Url))
            throw new InvalidOperationException("Webhook adresi boş.");

        var client = _httpClientFactory.CreateClient(HttpClientNames.Default);
        client.Timeout = TimeSpan.FromSeconds(15);

        // "text" alani hem Slack hem Teams gelen webhook'lari tarafindan anlasilir.
        var payload = new { text = $"**{message.Subject}**\n\n{message.Text.Replace("\n", "  \n")}" };

        using var response = await client.PostAsJsonAsync(o.Url, payload, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Webhook HTTP {(int)response.StatusCode} döndü.");
    }
}
