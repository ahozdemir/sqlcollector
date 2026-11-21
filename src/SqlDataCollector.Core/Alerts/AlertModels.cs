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

    public static readonly string[] All = [Failed, PartiallyFailed, Abandoned, CleanupPending, Recovered, Missed, NoServer];
    public static readonly string[] Problems = [Failed, PartiallyFailed, Abandoned, CleanupPending];
    public const string DefaultChannelStatuses = "Failed,PartiallyFailed,Abandoned,CleanupPending,Missed,NoServer";

    /// <summary>Events sent to a job's own NotifyEmails.</summary>
    public static readonly string[] JobRecipients = [Failed, PartiallyFailed, Abandoned, CleanupPending, Recovered, Missed];

    public static string Describe(string e) => e switch
    {
        PartiallyFailed => "partially failed",
        CleanupPending => "cleanup pending",
        Missed => "did not run as scheduled",
        NoServer => "no Hangfire server listening",
        _ => e.ToLowerInvariant()
    };
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
