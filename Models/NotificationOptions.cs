namespace ServerMonitor.Models;


public sealed class NotificationOptions
{
    public bool Enabled { get; set; }


    public int FailureThreshold { get; set; } = 2;


    public bool NotifyOnRecovery { get; set; } = true;


    public bool NotifyComputers { get; set; }

    public EmailOptions Email { get; set; } = new();

    public WebhookOptions Webhook { get; set; } = new();

    public ChangeAlertOptions ChangeAlerts { get; set; } = new();
}


public sealed class ChangeAlertOptions
{
    public bool Enabled { get; set; }


    public int IntervalMinutes { get; set; } = 60;


    public bool WatchAntivirus { get; set; } = true;


    public bool WatchNewSoftware { get; set; } = true;


    public bool WatchUsb { get; set; } = true;
}

public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
    public string? Username { get; set; }


    public string? Password { get; set; }

    public string From { get; set; } = "";
    public List<string> To { get; set; } = new();
}

public sealed class WebhookOptions
{
    public bool Enabled { get; set; }


    public string Url { get; set; } = "";
}


public sealed class AuthOptions
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = "admin";
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
}
