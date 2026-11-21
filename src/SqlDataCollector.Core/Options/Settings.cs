namespace SqlDataCollector.Options;

/// <summary>appsettings: "Collector" section.</summary>
public sealed class CollectorOptions
{
    public const string Section = "Collector";

    /// <summary>
    /// Role(s) of this process: "Web" (UI), "Worker" (Hangfire Server, executes jobs) or "Web,Worker".
    /// Typical setup: Web on the app server, Worker on the Hangfire server (Windows Service).
    /// </summary>
    public string Roles { get; set; } = "Web,Worker";

    public bool IsWeb => Roles.Split(',', StringSplitOptions.TrimEntries).Contains("Web", StringComparer.OrdinalIgnoreCase);
    public bool IsWorker => Roles.Split(',', StringSplitOptions.TrimEntries).Contains("Worker", StringComparer.OrdinalIgnoreCase);

    /// <summary>Schema of the Hangfire tables. Keep it separate from other apps when sharing a Hangfire database.</summary>
    public string HangfireSchema { get; set; } = "hangfire_collector";

    /// <summary>
    /// true: creates/upgrades the Hangfire tables on startup (requires DDL permission).
    /// false: the DBA installs the schema once ("SqlDataCollector hangfire-install-script");
    /// service accounts only get SELECT/INSERT/UPDATE/DELETE on that schema. Recommended for a central Hangfire DB.
    /// </summary>
    public bool HangfirePrepareSchema { get; set; } = true;

    /// <summary>Create/upgrade the etl.* schema on startup.</summary>
    public bool AutoCreateSchema { get; set; } = true;

    /// <summary>How often job definitions are synced from the metadata tables to Hangfire.</summary>
    public string SyncCron { get; set; } = "*/5 * * * *";

    /// <summary>Schedule of the retention (old data cleanup) job.</summary>
    public string RetentionCron { get; set; } = "30 1 * * *";

    /// <summary>Time zone of the system jobs (sync/retention) and of times shown in the UI.</summary>
    public string SystemTimeZone { get; set; } = "Europe/Istanbul";

    /// <summary>Retention of etl.LoadRun / etl.ExportRun records.</summary>
    public int RunLogRetentionDays { get; set; } = 90;

    /// <summary>Rows deleted per batch during retention and cleanup.</summary>
    public int DeleteBatchSize { get; set; } = 50_000;

    /// <summary>Compression for newly created target tables: NONE | ROW | PAGE.</summary>
    public string TargetTableCompression { get; set; } = "PAGE";

    /// <summary>Folder where CSV files are written temporarily.</summary>
    public string WorkDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "SqlDataCollector");

    /// <summary>If true, exported files are kept locally after upload as a gzip-compressed archive (*.csv.gz, day folders under WorkDirectory).</summary>
    public bool KeepLocalFiles { get; set; }

    /// <summary>With KeepLocalFiles: day folders older than this are removed by the export worker itself.</summary>
    public int LocalFileRetentionDays { get; set; } = 7;

    /// <summary>
    /// Hangfire queues this Worker listens to (in priority order). Lets you route work to machines, e.g.
    /// a Worker installed on the target SQL Server host with "sdc_export" (local CSV export + upload) and
    /// the central Worker with "sdc_extract,sdc_system". Names: see CollectorQueues.
    /// </summary>
    public string Queues { get; set; } = "sdc_export,sdc_extract,sdc_system";

    /// <summary>Queues parsed (lower case); all queues when empty.</summary>
    public string[] QueueList
    {
        get
        {
            var q = Queues.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToArray();
            return q.Length == 0 ? Infrastructure.CollectorQueues.All : q;
        }
    }

    /// <summary>
    /// Database user (WITHOUT LOGIN, read-only) that every export read and export preview switches to with
    /// EXECUTE AS USER ... WITH COOKIE, so Editor-written export SQL cannot use the Worker's write/DDL rights on the target.
    /// Must exist in every database exports read from; the service accounts need IMPERSONATE on it.
    /// Empty = reads run with the service account's own rights (not recommended).
    /// </summary>
    public string ExportReadAsUser { get; set; } = DefaultExportReadAsUser;

    public const string DefaultExportReadAsUser = "sdc_export_reader";

    /// <summary>Hangfire worker count (jobs running concurrently on this server).</summary>
    public int WorkerCount { get; set; } = 4;
}

/// <summary>appsettings: "Gcs" section.</summary>
public sealed class GcsOptions
{
    public const string Section = "Gcs";

    /// <summary>Service account JSON path. Empty = Application Default Credentials.</summary>
    public string? CredentialsPath { get; set; }

    /// <summary>Optional custom endpoint (Private Service Connect, test emulator, ...). Empty = default GCS.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Emulator/testing only: unauthenticated access.</summary>
    public bool Anonymous { get; set; }

    /// <summary>Resumable upload chunk size (MB; multiple of 256 KB).</summary>
    public int UploadChunkSizeMb { get; set; } = 16;

    public int MaxRetries { get; set; } = 4;
}

/// <summary>appsettings: "Dashboard" section.</summary>
public sealed class DashboardSettings
{
    public const string Section = "Dashboard";
    public string Path { get; set; } = "/hangfire";
}
