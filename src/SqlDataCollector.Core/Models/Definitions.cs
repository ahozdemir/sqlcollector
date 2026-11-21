namespace SqlDataCollector.Models;

public enum SourceType : byte { Table = 1, Query = 2 }

public enum LoadMode : byte { Append = 1, Replace = 2, Incremental = 3, Merge = 4 }

public enum ExportMode : byte { Full = 1, Incremental = 2, Query = 3, AllTables = 4 }

public enum QuoteMode : byte { Minimal = 0, AllText = 1, All = 2 }

public enum SchemaPolicy : byte { Strict = 1, Evolve = 2 }

/// <summary>How a job is started: on its cron schedule, or after its upstream jobs (etl.JobDependency) have all succeeded.</summary>
public enum JobTriggerMode : byte { Schedule = 1, AfterJobs = 2 }

public sealed class SqlServerDef
{
    public int SqlServerId { get; set; }
    public string Name { get; set; } = "";
    public string ServerAddress { get; set; } = "";
    public bool IsSource { get; set; } = true;
    public bool IsTarget { get; set; }
    public string EncryptMode { get; set; } = "Mandatory";
    public bool TrustServerCertificate { get; set; }
    public bool ReadOnlyIntent { get; set; }
    public bool MultiSubnetFailover { get; set; }
    public int ConnectTimeoutSec { get; set; } = 30;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ModifiedBy { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
}

public sealed class ExtractJobDef
{
    public int ExtractJobId { get; set; }
    public string Name { get; set; } = "";
    public int SourceServerId { get; set; }
    public string SourceServerName { get; set; } = "";
    public string SourceDatabase { get; set; } = "";
    public SourceType SourceType { get; set; } = SourceType.Table;
    public string? SourceSchema { get; set; }  // nullable in DB: no default (Dapper skips setters for NULL)
    public string? SourceTable { get; set; }
    public string? ColumnList { get; set; }
    public string? FilterClause { get; set; }
    public string? SourceQuery { get; set; }
    public bool UseNoLock { get; set; }
    public LoadMode LoadMode { get; set; } = LoadMode.Append;
    public string? WatermarkColumn { get; set; }
    public SchemaPolicy SchemaPolicy { get; set; } = SchemaPolicy.Strict;
    public bool PartitionDaily { get; set; }
    public int TargetServerId { get; set; }
    public string TargetServerName { get; set; } = "";
    public string TargetDatabase { get; set; } = "";
    public string TargetSchema { get; set; } = "stage";
    public string TargetTable { get; set; } = "";
    public string CronExpression { get; set; } = "0 2 * * *";
    public string TimeZoneId { get; set; } = "Europe/Istanbul";
    public int? RetentionDays { get; set; }
    public int CommandTimeoutSec { get; set; } = 3600;
    public int BatchSize { get; set; } = 10000;
    public bool ReplaceRebuild { get; set; }   // Replace: rebuild the table and swap it in
    public string? KeyColumns { get; set; }    // Merge: key column names (comma separated); nullable in DB: no default
    public bool MergeDeleteMissing { get; set; }   // Merge: delete rows missing from a full read
    public JobTriggerMode TriggerMode { get; set; } = JobTriggerMode.Schedule;
    public string? NotifyEmails { get; set; }  // job owners' alert recipients (; separated); nullable in DB: no default
    public bool IsActive { get; set; } = true;
    public string? CreatedBy { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
}

public sealed class ExtractState
{
    public string? WatermarkType { get; set; }
    public string? WatermarkValue { get; set; }
}

public sealed class ExportJobDef
{
    public int ExportJobId { get; set; }
    public string Name { get; set; } = "";
    public ExportMode ExportMode { get; set; } = ExportMode.Incremental;
    public int SourceServerId { get; set; }
    public string SourceServerName { get; set; } = "";
    public string SourceDatabase { get; set; } = "";
    public string? SourceSchema { get; set; }  // nullable in DB: no default (Dapper skips setters for NULL)
    public string? SourceTable { get; set; }
    public string? SourceQuery { get; set; }
    public string? ColumnList { get; set; }
    public bool IncludeTechnicalColumns { get; set; }
    public string CronExpression { get; set; } = "0 3 * * *";
    public string TimeZoneId { get; set; } = "Europe/Istanbul";
    public string Bucket { get; set; } = "";
    public string ObjectNameTemplate { get; set; } = "exports/{Name}/dt={Date}/{Name}_{DateCompact}_{Time}_{Part}.csv";
    public string Delimiter { get; set; } = ",";
    public QuoteMode QuoteMode { get; set; }
    public string NullValue { get; set; } = "";
    public string LineEnding { get; set; } = "LF";
    public bool Utf8Bom { get; set; }
    public bool Gzip { get; set; }  // gzip the files uploaded to GCS (default: plain CSV); the local archive is always gzip
    public int? MaxRowsPerFile { get; set; }
    public bool UploadWhenEmpty { get; set; } = true;
    public bool WriteManifest { get; set; } = true;
    public JobTriggerMode TriggerMode { get; set; } = JobTriggerMode.Schedule;
    public int CommandTimeoutSec { get; set; } = 3600;
    public string? NotifyEmails { get; set; }  // job owners' alert recipients (; separated); nullable in DB: no default
    public bool IsActive { get; set; } = true;
    public string? CreatedBy { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
}

public sealed class RoleAssignmentDef
{
    public int RoleAssignmentId { get; set; }
    public string PrincipalName { get; set; } = "";
    public string PrincipalType { get; set; } = "G";
    public string Role { get; set; } = AppRoles.Viewer;
    public string? ModifiedBy { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
}

public sealed class ServerPermissionDef
{
    public int ServerPermissionId { get; set; }
    public string PrincipalName { get; set; } = "";
    public int SqlServerId { get; set; }
    public string ServerName { get; set; } = "";
    public string? DatabaseName { get; set; }
    public bool CanSource { get; set; } = true;
    public bool CanTarget { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
}

public sealed class RunRow
{
    public long RunId { get; set; }
    public string Kind { get; set; } = "";
    public int JobId { get; set; }
    public string JobName { get; set; } = "";
    public string Status { get; set; } = "";
    public string? TriggeredBy { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public long? Rows { get; set; }
    public string? Detail { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class JobStatusRow
{
    public string JobKind { get; set; } = "";
    public int JobId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public string CronExpression { get; set; } = "";
    public string? LastStatus { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public int? DurationSec { get; set; }
    public long? Rows { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class AuditRow
{
    public long AuditLogId { get; set; }
    public DateTime AtUtc { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Details { get; set; }
}

public sealed record RunningLoad(long LoadRunId, int ExtractJobId, int TargetServerId, string TargetDatabase, string TargetSchema, string TargetTable);

public sealed record RunningExport(long ExportRunId, int ExportJobId);

public sealed record CollectedTable(string SchemaName, string TableName)
{
    public string Key => $"{SchemaName}.{TableName}";
}

public sealed record ExportTableState(string SchemaName, string TableName, long LastExportedLoadId, DateTime UpdatedAtUtc);

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Editor = "Editor";
    public const string Viewer = "Viewer";
    public static readonly string[] All = [Admin, Editor, Viewer];
}

/// <summary>"JobKind/JobId runs after UpstreamKind/UpstreamId has succeeded" (all upstreams of a job must succeed).</summary>
public sealed class JobDependencyRow
{
    public string JobKind { get; set; } = "";
    public int JobId { get; set; }
    public string UpstreamKind { get; set; } = "";
    public int UpstreamId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpstreamName { get; set; }
}

/// <summary>A job reference: Kind is "Extract" or "Export".</summary>
public readonly record struct JobRef(string Kind, int Id)
{
    public const string Extract = "Extract";
    public const string Export = "Export";
    public override string ToString() => $"{Kind} #{Id}";
}

/// <summary>A dependency row with the upstream's latest success and the dependent's last dependency trigger (UTC, database clock).</summary>
public sealed class DependencyStateRow
{
    public string JobKind { get; set; } = "";
    public int JobId { get; set; }
    public string UpstreamKind { get; set; } = "";
    public int UpstreamId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpstreamLastSuccessUtc { get; set; }
    public DateTime? LastTriggeredAtUtc { get; set; }

    public JobRef Job => new(JobKind, JobId);
    public JobRef Upstream => new(UpstreamKind, UpstreamId);
}

public static class ExtractJobRules
{
    /// <summary>Reads only rows above the stored watermark: Incremental always, Merge when a watermark column is set.</summary>
    public static bool UsesWatermark(this ExtractJobDef j) =>
        j.LoadMode == LoadMode.Incremental || (j.LoadMode == LoadMode.Merge && !string.IsNullOrWhiteSpace(j.WatermarkColumn));

    /// <summary>The table holds only the current state (Replace, Merge): retention by age does not apply.</summary>
    public static bool KeepsCurrentStateOnly(this LoadMode mode) => mode is LoadMode.Replace or LoadMode.Merge;

    /// <summary>Key column names of a Merge job, in order.</summary>
    public static IReadOnlyList<string> KeyColumnList(this ExtractJobDef j) =>
        (j.KeyColumns ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.Trim('[', ']')).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Rows a merge inserted, updated (changed values only) and deleted.</summary>
public sealed record MergeCounts(long Inserted, long Updated, long Deleted);
