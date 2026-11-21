namespace SqlDataCollector.Alerts;

/// <summary>appsettings: "Alerts" section (infrastructure only; channels and rules live in etl.AlertChannel).</summary>
public sealed class AlertOptions
{
    public const string Section = "Alerts";

    /// <summary>Base URL of the web UI used for links in alerts, e.g. https://sdc.corp.local. Empty = no links.</summary>
    public string? PublicUrl { get; set; }

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    /// <summary>SMTP server (an internal relay is recommended). Empty = e-mail alerts cannot be delivered.</summary>
    public string? Host { get; set; }
    public int Port { get; set; } = 25;
    /// <summary>Auto | None | StartTls | SslOnConnect</summary>
    public string Security { get; set; } = "Auto";
    public string From { get; set; } = "sqldatacollector@localhost";
    /// <summary>Optional; only if the relay requires authentication (the password stays in configuration, never in the database).</summary>
    public string? UserName { get; set; }
    public string? Password { get; set; }
}

public static class AlertChannelTypes
{
    public const string Email = "Email";
    public const string Teams = "Teams";
    public const string Webhook = "Webhook";
    public static readonly string[] All = [Email, Teams, Webhook];
}

public static class AlertEvents
{
    public const string Failed = "Failed";
    public const string PartiallyFailed = "PartiallyFailed";
    public const string Abandoned = "Abandoned";
    public const string CleanupPending = "CleanupPending";
    public const string Recovered = "Recovered";
    public const string Missed = "Missed";
    public const string NoServer = "NoServer";
    public const string CertExpiring = "CertExpiring";
    public const string QualityFailed = "QualityFailed";
    public const string QualityWarning = "QualityWarning";
    public const string Stale = "Stale";

    public static readonly string[] All = [Failed, PartiallyFailed, Abandoned, CleanupPending, Recovered, Missed, QualityFailed, QualityWarning, Stale, NoServer, CertExpiring];
    public static readonly string[] Problems = [Failed, PartiallyFailed, Abandoned, CleanupPending];
    public const string DefaultChannelStatuses = "Failed,PartiallyFailed,Abandoned,CleanupPending,Missed,QualityFailed,Stale,NoServer,CertExpiring";

    /// <summary>The HTTPS certificate is reported this many days before it expires (once a day).</summary>
    public const int CertWarningDays = 30;

    /// <summary>Events sent to a job's own NotifyEmails.</summary>
    public static readonly string[] JobRecipients = [Failed, PartiallyFailed, Abandoned, CleanupPending, Recovered, Missed, QualityFailed, QualityWarning, Stale];

    public static string Describe(string e) => e switch
    {
        PartiallyFailed => "partially failed",
        CleanupPending => "cleanup pending",
        Missed => "did not run as scheduled",
        NoServer => "no Hangfire server listening",
        CertExpiring => "HTTPS certificate expires soon",
        QualityFailed => "data quality checks failed",
        QualityWarning => "data quality warning",
        Stale => "no fresh data",
        _ => e.ToLowerInvariant()
    };
}

/// <summary>Implemented by the Web host when it serves HTTPS: the current server certificate, for expiry alerts.</summary>
public interface ICertificateStatus
{
    (string Subject, DateTime NotAfterUtc)? Current { get; }
}

public sealed class AlertChannelDef
{
    public int AlertChannelId { get; set; }
    public string Name { get; set; } = "";
    public string ChannelType { get; set; } = AlertChannelTypes.Email;
    public string Target { get; set; } = "";
    public string Statuses { get; set; } = AlertEvents.DefaultChannelStatuses;
    public string JobKinds { get; set; } = "Extract,Export";
    public string? JobNameFilter { get; set; }  // nullable in DB: no default
    public bool IsActive { get; set; } = true;
    public string? ModifiedBy { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
}

public sealed class AlertLogRow
{
    public long AlertLogId { get; set; }
    public string EventType { get; set; } = "";
    public string JobKind { get; set; } = "";
    public int JobId { get; set; }
    public string JobName { get; set; } = "";
    public long RunId { get; set; }
    public string ChannelKey { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Status { get; set; } = "";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
}

/// <summary>A finished run with the status of the job's previous finished run (for "Recovered").</summary>
public sealed class FinishedRunRow
{
    public string Kind { get; set; } = "";
    public long RunId { get; set; }
    public int JobId { get; set; }
    public string JobName { get; set; } = "";
    public string? NotifyEmails { get; set; }
    public string Status { get; set; } = "";
    public string? PrevStatus { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public long? Rows { get; set; }
    public string? ErrorMessage { get; set; }
    public string? HostName { get; set; }
    public string? TriggeredBy { get; set; }
    public string? QualityStatus { get; set; }
}

/// <summary>Something to alert about. Key = (EventType, JobKind, JobId, RunId).</summary>
public sealed record AlertEvent(
    string EventType,
    string JobKind,            // Extract | Export | System
    int JobId,
    string JobName,
    long RunId,
    DateTime AtUtc,
    string? NotifyEmails = null,
    DateTime? StartedAtUtc = null,
    DateTime? FinishedAtUtc = null,
    long? Rows = null,
    string? Error = null,
    string? HostName = null,
    string? TriggeredBy = null,
    int ConsecutiveFailures = 0,
    string? Detail = null);
