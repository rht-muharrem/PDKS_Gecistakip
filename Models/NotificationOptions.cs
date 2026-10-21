namespace ServerMonitor.Models;

/// <summary>Sorun ve duzelme bildirimleri.</summary>
public sealed class NotificationOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Bildirim gondermeden once sunucunun ust uste kac turda SORUNLU olmasi gerektigi.
    /// Tek seferlik ag dalgalanmalarinda gereksiz alarm uretmemek icin.
    /// </summary>
    public int FailureThreshold { get; set; } = 2;

    /// <summary>Sorunlu sunucu tekrar ayaga kalkinca "duzeldi" bildirimi gonder.</summary>
    public bool NotifyOnRecovery { get; set; } = true;

    /// <summary>Bilgisayarlar icin de bildirim gonder. Kapaninca her aksam mail gelmesin diye varsayilan kapali.</summary>
    public bool NotifyComputers { get; set; }

    public EmailOptions Email { get; set; } = new();

    public WebhookOptions Webhook { get; set; } = new();

    public ChangeAlertOptions ChangeAlerts { get; set; } = new();
}

/// <summary>Bilgisayarlarda periyodik değişiklik/güvenlik izleme.</summary>
public sealed class ChangeAlertOptions
{
    public bool Enabled { get; set; }

    /// <summary>Tarama araligi (dakika). Program taramasi agir oldugu icin cok makinede yuksek tutun.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Antivirüs kapandı / güncelliğini yitirdi / kaldırıldı.</summary>
    public bool WatchAntivirus { get; set; } = true;

    /// <summary>Yeni program kuruldu / kaldırıldı.</summary>
    public bool WatchNewSoftware { get; set; } = true;

    /// <summary>Yeni USB bellek takıldı.</summary>
    public bool WatchUsb { get; set; } = true;
}

public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
    public string? Username { get; set; }

    /// <summary>DPAPI ile sifreli saklanir (enc: oneki).</summary>
    public string? Password { get; set; }

    public string From { get; set; } = "";
    public List<string> To { get; set; } = new();
}

public sealed class WebhookOptions
{
    public bool Enabled { get; set; }

    /// <summary>Teams (Workflows/Incoming Webhook) veya Slack webhook adresi.</summary>
    public string Url { get; set; } = "";
}

/// <summary>Panel girisi. Enabled=false veya parola tanimsizsa panel acik calisir.</summary>
public sealed class AuthOptions
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = "admin";
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
}
