using System.Text.Json;
using System.Text.Json.Serialization;
using SqlDataCollector.Alerts;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;

namespace SqlDataCollector.Services;

// ==================================================================== package format
// A job package is a portable JSON file: servers, destinations and upstream jobs are referenced by NAME (ids differ
// between installations), secrets are never included. Version 1; readers reject unknown formats and newer versions.

public sealed class JobPackage
{
    public const string FormatName = "SqlDataCollector.Jobs";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    public DateTime ExportedAtUtc { get; set; }
    public string? ExportedBy { get; set; }
    public string? AppVersion { get; set; }
    public List<ExtractJobSpec> ExtractJobs { get; set; } = [];
    public List<ExportJobSpec> ExportJobs { get; set; } = [];

    /// <summary>Distinct server names the package refers to (sources and targets).</summary>
    public IReadOnlyList<string> ServerNames() =>
        ExtractJobs.SelectMany(j => new[] { j.SourceServer, j.TargetServer }).Concat(ExportJobs.Select(e => e.SourceServer))
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Distinct destination names the package refers to.</summary>
    public IReadOnlyList<string> DestinationNames() =>
        ExportJobs.Select(e => e.Destination).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>An upstream job by kind ("Extract" / "Export") and name.</summary>
public sealed class JobRefSpec
{
    public string Kind { get; set; } = JobRef.Extract;
    public string Name { get; set; } = "";
}

public sealed class QualityCheckSpec
{
    public string CheckType { get; set; } = QualityCheckTypes.MinRows;
    public string? ColumnName { get; set; }
    public decimal? Threshold { get; set; }
    public string? Operator { get; set; }
    public string? SqlText { get; set; }
    public string Scope { get; set; } = "Load";
    public string Severity { get; set; } = "Error";
    public bool IsActive { get; set; } = true;
}

public sealed class ExtractJobSpec
{
    public string Name { get; set; } = "";
    public string SourceServer { get; set; } = "";
    public string SourceDatabase { get; set; } = "";
    public SourceType SourceType { get; set; } = SourceType.Table;
    public string? SourceSchema { get; set; }
    public string? SourceTable { get; set; }
    public string? ColumnList { get; set; }
    public string? FilterClause { get; set; }
    public string? SourceQuery { get; set; }
    public bool UseNoLock { get; set; }
    public bool ReadSnapshot { get; set; }
    public LoadMode LoadMode { get; set; } = LoadMode.Append;
    public string? WatermarkColumn { get; set; }
    public string? KeyColumns { get; set; }
    public bool MergeDeleteMissing { get; set; }
    public SchemaPolicy SchemaPolicy { get; set; } = SchemaPolicy.Strict;
    public bool PartitionDaily { get; set; }
    public bool ReplaceRebuild { get; set; }
    public string TargetServer { get; set; } = "";
    public string TargetDatabase { get; set; } = "";
    public string TargetSchema { get; set; } = "stage";
    public string TargetTable { get; set; } = "";
    public JobTriggerMode TriggerMode { get; set; } = JobTriggerMode.Schedule;
    public string CronExpression { get; set; } = "0 2 * * *";
    public string TimeZoneId { get; set; } = "Europe/Istanbul";
    public List<JobRefSpec> RunsAfter { get; set; } = [];
    public int? RetentionDays { get; set; }
    public int CommandTimeoutSec { get; set; } = 3600;
    public int BatchSize { get; set; } = 10000;
    public int? FreshnessHours { get; set; }
    public string? NotifyEmails { get; set; }
    public bool IsActive { get; set; } = true;
    public List<QualityCheckSpec> QualityChecks { get; set; } = [];
}

public sealed class ExportJobSpec
{
    public string Name { get; set; } = "";
    public ExportMode ExportMode { get; set; } = ExportMode.Incremental;
    public string SourceServer { get; set; } = "";
    public string SourceDatabase { get; set; } = "";
    public string? SourceSchema { get; set; }
    public string? SourceTable { get; set; }
    public string? SourceQuery { get; set; }
    public string? ColumnList { get; set; }
    public bool IncludeTechnicalColumns { get; set; }
    public string Destination { get; set; } = "";
    public string ObjectNameTemplate { get; set; } = "exports/{Name}/dt={Date}/{Name}_{DateCompact}_{Time}_{Part}.csv";
    public ExportFileFormat FileFormat { get; set; } = ExportFileFormat.Csv;
    public string ParquetCompression { get; set; } = "Snappy";
    public string Delimiter { get; set; } = ",";
    public QuoteMode QuoteMode { get; set; }
    public string NullValue { get; set; } = "";
    public string LineEnding { get; set; } = "LF";
    public bool Utf8Bom { get; set; }
    public bool Gzip { get; set; }
    public int? MaxRowsPerFile { get; set; }
    public bool UploadWhenEmpty { get; set; } = true;
    public bool WriteManifest { get; set; } = true;
    public JobTriggerMode TriggerMode { get; set; } = JobTriggerMode.Schedule;
    public string CronExpression { get; set; } = "0 3 * * *";
    public string TimeZoneId { get; set; } = "Europe/Istanbul";
    public List<JobRefSpec> RunsAfter { get; set; } = [];
    public int CommandTimeoutSec { get; set; } = 3600;
    public string? NotifyEmails { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Reads and writes job packages (camelCase, enums as names, indented).</summary>
public static class JobPackageJson
{
    public const long MaxBytes = 5 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string Serialize(JobPackage package) => JsonSerializer.Serialize(package, Options);

    /// <summary>Parses a package; throws <see cref="InvalidDataException"/> with a readable message for anything else.</summary>
    public static JobPackage Deserialize(string json)
    {
        JobPackage? p;
        try { p = JsonSerializer.Deserialize<JobPackage>(json, Options); }
        catch (JsonException ex) { throw new InvalidDataException($"The file is not a valid job package: {ex.Message}", ex); }
        if (p is null || !string.Equals(p.Format, JobPackage.FormatName, StringComparison.Ordinal))
            throw new InvalidDataException($"The file is not a job package (\"format\" must be \"{JobPackage.FormatName}\").");
        if (p.Version < 1 || p.Version > JobPackage.CurrentVersion)
            throw new InvalidDataException($"Job package version {p.Version} is not supported by this version of the application (supported: 1–{JobPackage.CurrentVersion}).");
        p.ExtractJobs ??= [];
        p.ExportJobs ??= [];
        // "name": null in a hand-edited file must not crash the plan: treat missing texts as empty (validation reports them)
        p.ExtractJobs.RemoveAll(j => j is null);
        p.ExportJobs.RemoveAll(e => e is null);
        foreach (var j in p.ExtractJobs)
        {
            j.Name ??= ""; j.SourceServer ??= ""; j.TargetServer ??= "";
            j.RunsAfter = Clean(j.RunsAfter);
            j.QualityChecks = (j.QualityChecks ?? []).Where(q => q is not null).ToList();
            foreach (var q in j.QualityChecks) { q.CheckType ??= ""; q.Scope ??= ""; q.Severity ??= ""; }
        }
        foreach (var e in p.ExportJobs) { e.Name ??= ""; e.SourceServer ??= ""; e.Destination ??= ""; e.RunsAfter = Clean(e.RunsAfter); }
        return p;

        static List<JobRefSpec> Clean(List<JobRefSpec>? refs) =>
            (refs ?? []).Where(r => r is not null).Select(r => new JobRefSpec { Kind = r.Kind ?? "", Name = r.Name ?? "" }).ToList();
    }
}

// ==================================================================== validation rules shared by the editors and the import

public static class JobDefinitionRules
{
    /// <summary>Checks and normalizes an extract job definition (like the editor's Save); returns the errors.</summary>
    public static List<string> ValidateExtract(ExtractJobDef j)
    {
        var errs = new List<string>();
        j.Name = j.Name.Trim();
        if (j.Name.Length == 0) errs.Add("Job name is required.");
        else if (j.Name.Length > 200) errs.Add("Job name is longer than 200 characters.");
        j.NotifyEmails = string.IsNullOrWhiteSpace(j.NotifyEmails) ? null : j.NotifyEmails.Trim();
        if (AlertFormat.InvalidEmails(j.NotifyEmails) is { Count: > 0 } badMails) errs.Add($"Invalid Notify e-mail address(es): {string.Join(", ", badMails)}");
        if (j.FreshnessHours is <= 0) j.FreshnessHours = null;
        if (j.LoadMode != LoadMode.Replace) j.ReplaceRebuild = false;
        if (j.ReplaceRebuild && j.PartitionDaily) errs.Add("Replace with rebuild cannot be combined with daily partitioning.");
        if (string.IsNullOrWhiteSpace(j.SourceDatabase)) errs.Add("Source database is required.");
        if (j.SourceType == SourceType.Table && string.IsNullOrWhiteSpace(j.SourceTable)) errs.Add("Source table is required.");
        if (j.SourceType == SourceType.Query && string.IsNullOrWhiteSpace(j.SourceQuery)) errs.Add("The query cannot be empty.");
        if (j.LoadMode == LoadMode.Incremental && string.IsNullOrWhiteSpace(j.WatermarkColumn)) errs.Add("Incremental mode needs a watermark column.");
        if (j.LoadMode == LoadMode.Merge)
        {
            j.WatermarkColumn = string.IsNullOrWhiteSpace(j.WatermarkColumn) ? null : j.WatermarkColumn.Trim();
            var keys = j.KeyColumnList();
            j.KeyColumns = keys.Count == 0 ? null : string.Join(", ", keys);
            if (keys.Count == 0) errs.Add("Merge needs the key column(s) that identify a row.");
            if (j.PartitionDaily) errs.Add("Merge cannot be combined with daily partitioning.");
            if (j.MergeDeleteMissing && j.WatermarkColumn is not null) errs.Add("Deleting missing rows needs a full read: clear the watermark column or the option.");
        }
        else { j.KeyColumns = null; j.MergeDeleteMissing = false; }
        if (string.IsNullOrWhiteSpace(j.TargetDatabase)) errs.Add("Target database is required.");
        if (string.IsNullOrWhiteSpace(j.TargetSchema)) errs.Add("Target schema is required.");
        if (string.IsNullOrWhiteSpace(j.TargetTable)) errs.Add("Target table is required.");
        ValidateTrigger(j.TriggerMode, j.CronExpression, j.TimeZoneId, errs);
        if (j.BatchSize < 100) j.BatchSize = 10000;
        if (j.CommandTimeoutSec < 30) j.CommandTimeoutSec = 3600;
        if (j.SourceType == SourceType.Query) { j.SourceTable = null; j.ColumnList = null; j.FilterClause = null; }
        else j.SourceQuery = null;
        if (!j.UsesWatermark()) j.WatermarkColumn = null;
        if (j.LoadMode.KeepsCurrentStateOnly()) j.RetentionDays = null;
        return errs;
    }

    /// <summary>Checks and normalizes an export job definition (the destination is checked by the caller); returns the errors.</summary>
    public static List<string> ValidateExport(ExportJobDef e)
    {
        var errs = new List<string>();
        e.Name = e.Name.Trim();
        if (e.Name.Length == 0) errs.Add("Job name is required.");
        else if (e.Name.Length > 200) errs.Add("Job name is longer than 200 characters.");
        e.NotifyEmails = string.IsNullOrWhiteSpace(e.NotifyEmails) ? null : e.NotifyEmails.Trim();
        if (AlertFormat.InvalidEmails(e.NotifyEmails) is { Count: > 0 } badMails) errs.Add($"Invalid Notify e-mail address(es): {string.Join(", ", badMails)}");
        if (string.IsNullOrWhiteSpace(e.SourceDatabase)) errs.Add("Database is required.");
        if (e.ExportMode is ExportMode.Full or ExportMode.Incremental && string.IsNullOrWhiteSpace(e.SourceTable)) errs.Add("Table is required.");
        if (e.ExportMode == ExportMode.Query && string.IsNullOrWhiteSpace(e.SourceQuery)) errs.Add("The query cannot be empty.");
        if (string.IsNullOrWhiteSpace(e.ObjectNameTemplate)) errs.Add("Object name template is required.");
        else if (e.ExportMode == ExportMode.AllTables && !e.ObjectNameTemplate.Contains("{Table}"))
            errs.Add("In All collected tables mode the object name template must contain {Table}, otherwise files would overwrite each other.");
        if (e.ParquetCompression is not ("Snappy" or "Zstd" or "Gzip" or "None")) errs.Add($"Unknown Parquet compression '{e.ParquetCompression}' (Snappy, Zstd, Gzip, None).");
        if (e.LineEnding is not ("LF" or "CRLF")) errs.Add($"Unknown line ending '{e.LineEnding}' (LF, CRLF).");
        if (string.IsNullOrEmpty(e.Delimiter)) errs.Add("Delimiter is required.");
        ValidateTrigger(e.TriggerMode, e.CronExpression, e.TimeZoneId, errs);
        if (e.CommandTimeoutSec < 30) e.CommandTimeoutSec = 3600;
        if (e.MaxRowsPerFile is <= 0) e.MaxRowsPerFile = null;
        if (e.ExportMode == ExportMode.Query) e.SourceTable = null;
        else if (e.ExportMode == ExportMode.AllTables) { e.SourceTable = null; e.SourceQuery = null; e.ColumnList = null; if (string.IsNullOrWhiteSpace(e.SourceSchema)) e.SourceSchema = null; }
        else e.SourceQuery = null;
        return errs;
    }

    private static void ValidateTrigger(JobTriggerMode mode, string cron, string tz, List<string> errs)
    {
        if (!Enum.IsDefined(mode)) { errs.Add($"Unknown trigger mode '{mode}'."); return; }
        if (mode == JobTriggerMode.Schedule && JobRegistrar.Preview(cron ?? "", tz).Error is { } cronError) errs.Add($"Invalid schedule: {cronError}");
    }

    /// <summary>
    /// Rebuild and Merge need a target table that only this job writes to; returns the error or null.
    /// <paramref name="others"/>: the other extract jobs (the job itself is ignored by id when it has one).
    /// </summary>
    public static string? TargetConflict(ExtractJobDef j, IEnumerable<ExtractJobDef> others)
    {
        var same = others.Where(o => !ReferenceEquals(o, j) && (j.ExtractJobId == 0 || o.ExtractJobId != j.ExtractJobId)
            && o.TargetServerId == j.TargetServerId
            && o.TargetDatabase.Equals(j.TargetDatabase, StringComparison.OrdinalIgnoreCase)
            && o.TargetSchema.Equals(j.TargetSchema, StringComparison.OrdinalIgnoreCase)
            && o.TargetTable.Equals(j.TargetTable, StringComparison.OrdinalIgnoreCase)).ToList();
        if (same.Count == 0) return null;
        var what = j.LoadMode == LoadMode.Merge ? "Merge" : "Replace with rebuild";
        if (j.ReplaceRebuild || j.LoadMode == LoadMode.Merge)
            return $"{what} needs a target table that only this job writes to; also written by: {string.Join(", ", same.Select(o => o.Name))}.";
        if (same.FirstOrDefault(o => o.LoadMode == LoadMode.Replace && o.ReplaceRebuild) is { } rebuilder)
            return $"Job '{rebuilder.Name}' rebuilds this target table on every run; choose another target table.";
        if (same.FirstOrDefault(o => o.LoadMode == LoadMode.Merge) is { } merger)
            return $"Job '{merger.Name}' merges into this target table (one row per key); choose another target table.";
        return null;
    }
}

// ==================================================================== import / export service

/// <summary>Who imports: the web user (roles, server permissions) or the command line (full access).</summary>
public interface IJobImportAccess
{
    string UserName { get; }
    Task<bool> CanEditAsync();
    Task<bool> IsAdminAsync();
    /// <param name="usage">Source | Target | Any</param>
    Task<bool> CanUseAsync(int serverId, string database, string usage);
}

/// <summary>The command line: runs with the service account's access to the metadata database, i.e. like an Admin.</summary>
public sealed class FullJobImportAccess(string userName) : IJobImportAccess
{
    public string UserName => userName;
    public Task<bool> CanEditAsync() => Task.FromResult(true);
    public Task<bool> IsAdminAsync() => Task.FromResult(true);
    public Task<bool> CanUseAsync(int serverId, string database, string usage) => Task.FromResult(true);
}

public enum ExistingJobAction { Skip = 0, Update = 1 }

public sealed class JobImportOptions
{
    /// <summary>A job whose name already exists here: left as it is (default) or overwritten with the package's settings.</summary>
    public ExistingJobAction ExistingJobs { get; set; } = ExistingJobAction.Skip;
    /// <summary>New jobs are created inactive (default): nothing runs until someone activates them. Updated jobs keep their active state.</summary>
    public bool NewJobsInactive { get; set; } = true;
    /// <summary>Package server name → server id here. Names without an entry use the server with the same name.</summary>
    public Dictionary<string, int> ServerMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Package destination name → destination id here. Names without an entry use the destination with the same name.</summary>
    public Dictionary<string, int> DestinationMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum ImportAction { Create, Update, Skip, Error }

public sealed record ImportItem(string Kind, string Name, ImportAction Action, IReadOnlyList<string> Messages);

public sealed class ImportPlan
{
    public List<ImportItem> Items { get; } = [];
    /// <summary>Server names of the package that exist neither here nor in the map.</summary>
    public List<string> MissingServers { get; } = [];
    /// <summary>Destination names of the package that exist neither here nor in the map.</summary>
    public List<string> MissingDestinations { get; } = [];
    /// <summary>Errors of the whole package (e.g. not allowed to edit).</summary>
    public List<string> Errors { get; } = [];
    public bool HasErrors => Errors.Count > 0 || Items.Any(i => i.Action == ImportAction.Error);
    public int Count(ImportAction a) => Items.Count(i => i.Action == a);

    // resolved definitions, used by ApplyAsync
    internal List<(ExtractJobSpec Spec, ExtractJobDef Def)> Extracts { get; } = [];
    internal List<(ExportJobSpec Spec, ExportJobDef Def)> Exports { get; } = [];
    internal Dictionary<(string Kind, string Name), JobRef> Refs { get; } = new(KindNameComparer.Instance);
    internal Dictionary<JobRef, List<JobRef>> Upstreams { get; set; } = [];
}

public sealed record ImportResult(int Created, int Updated, int Skipped, IReadOnlyList<ExtractJobDef> Extracts, IReadOnlyList<ExportJobDef> Exports);

internal sealed class KindNameComparer : IEqualityComparer<(string Kind, string Name)>
{
    public static readonly KindNameComparer Instance = new();
    public bool Equals((string Kind, string Name) x, (string Kind, string Name) y) =>
        string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
    public int GetHashCode((string Kind, string Name) o) =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(o.Kind), StringComparer.OrdinalIgnoreCase.GetHashCode(o.Name));
}

/// <summary>
/// Exports job definitions to a portable package and imports them (environment promotion, backups, version control).
/// The import is planned first (validation, permissions, retention policy, dependencies, name and target conflicts);
/// it is applied only when the plan has no errors. Hangfire registration is left to the caller (or the sync job).
/// </summary>
public sealed class JobTransferService(MetadataRepository repo, SystemSettingsService settings)
{
    private static readonly string AppVersion = typeof(JobTransferService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    // ---------------------------------------------------------------- export

    /// <summary>Builds a package of the given jobs (all jobs when both lists are null); optionally with all their upstream jobs.</summary>
    public async Task<JobPackage> ExportAsync(IReadOnlyCollection<int>? extractIds, IReadOnlyCollection<int>? exportIds, bool includeUpstreams, string user)
    {
        var extracts = await repo.GetExtractJobsAsync();
        var exports = await repo.GetExportJobsAsync();
        var deps = await repo.GetJobDependenciesAsync();
        var checks = (await repo.GetQualityChecksAsync()).ToLookup(q => q.ExtractJobId);

        bool all = extractIds is null && exportIds is null;
        var selected = new HashSet<JobRef>(all
            ? extracts.Select(j => new JobRef(JobRef.Extract, j.ExtractJobId)).Concat(exports.Select(e => new JobRef(JobRef.Export, e.ExportJobId)))
            : (extractIds ?? []).Select(id => new JobRef(JobRef.Extract, id)).Concat((exportIds ?? []).Select(id => new JobRef(JobRef.Export, id))));
        if (includeUpstreams)
        {
            var queue = new Queue<JobRef>(selected);
            while (queue.TryDequeue(out var job))
                foreach (var d in deps.Where(d => d.JobKind == job.Kind && d.JobId == job.Id))
                    if (selected.Add(new JobRef(d.UpstreamKind, d.UpstreamId))) queue.Enqueue(new JobRef(d.UpstreamKind, d.UpstreamId));
        }

        var names = extracts.ToDictionary(j => new JobRef(JobRef.Extract, j.ExtractJobId), j => j.Name);
        foreach (var e in exports) names[new JobRef(JobRef.Export, e.ExportJobId)] = e.Name;
        List<JobRefSpec> RunsAfter(string kind, int id, JobTriggerMode mode) => mode != JobTriggerMode.AfterJobs ? [] : deps
            .Where(d => d.JobKind == kind && d.JobId == id && names.ContainsKey(new JobRef(d.UpstreamKind, d.UpstreamId)))
            .Select(d => new JobRefSpec { Kind = d.UpstreamKind, Name = names[new JobRef(d.UpstreamKind, d.UpstreamId)] })
            .OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var package = new JobPackage { ExportedAtUtc = DateTime.UtcNow, ExportedBy = user, AppVersion = AppVersion };
        foreach (var j in extracts.Where(j => selected.Contains(new JobRef(JobRef.Extract, j.ExtractJobId))))
        {
            var spec = ToSpec(j);
            spec.RunsAfter = RunsAfter(JobRef.Extract, j.ExtractJobId, j.TriggerMode);
            spec.QualityChecks = checks[j.ExtractJobId].Select(q => new QualityCheckSpec
            {
                CheckType = q.CheckType, ColumnName = q.ColumnName, Threshold = q.Threshold, Operator = q.Operator,
                SqlText = q.SqlText, Scope = q.Scope, Severity = q.Severity, IsActive = q.IsActive
            }).ToList();
            package.ExtractJobs.Add(spec);
        }
        foreach (var e in exports.Where(e => selected.Contains(new JobRef(JobRef.Export, e.ExportJobId))))
        {
            var spec = ToSpec(e);
            spec.RunsAfter = RunsAfter(JobRef.Export, e.ExportJobId, e.TriggerMode);
            package.ExportJobs.Add(spec);
        }
        await repo.AuditAsync(user, "Export", "JobPackage", null, new { Extract = package.ExtractJobs.Select(j => j.Name), Export = package.ExportJobs.Select(e => e.Name) });
        return package;
    }

    private static ExtractJobSpec ToSpec(ExtractJobDef j) => new()
    {
        Name = j.Name, SourceServer = j.SourceServerName, SourceDatabase = j.SourceDatabase, SourceType = j.SourceType,
        SourceSchema = j.SourceSchema, SourceTable = j.SourceTable, ColumnList = j.ColumnList, FilterClause = j.FilterClause,
        SourceQuery = j.SourceQuery, UseNoLock = j.UseNoLock, ReadSnapshot = j.ReadSnapshot, LoadMode = j.LoadMode,
        WatermarkColumn = j.WatermarkColumn, KeyColumns = j.KeyColumns, MergeDeleteMissing = j.MergeDeleteMissing,
        SchemaPolicy = j.SchemaPolicy, PartitionDaily = j.PartitionDaily, ReplaceRebuild = j.ReplaceRebuild,
        TargetServer = j.TargetServerName, TargetDatabase = j.TargetDatabase, TargetSchema = j.TargetSchema, TargetTable = j.TargetTable,
        TriggerMode = j.TriggerMode, CronExpression = j.CronExpression, TimeZoneId = j.TimeZoneId, RetentionDays = j.RetentionDays,
        CommandTimeoutSec = j.CommandTimeoutSec, BatchSize = j.BatchSize, FreshnessHours = j.FreshnessHours,
        NotifyEmails = j.NotifyEmails, IsActive = j.IsActive
    };

    private static ExportJobSpec ToSpec(ExportJobDef e) => new()
    {
        Name = e.Name, ExportMode = e.ExportMode, SourceServer = e.SourceServerName, SourceDatabase = e.SourceDatabase,
        SourceSchema = e.SourceSchema, SourceTable = e.SourceTable, SourceQuery = e.SourceQuery, ColumnList = e.ColumnList,
        IncludeTechnicalColumns = e.IncludeTechnicalColumns, Destination = e.DestinationName ?? $"gcs-{e.Bucket}",
        ObjectNameTemplate = e.ObjectNameTemplate, FileFormat = e.FileFormat, ParquetCompression = e.ParquetCompression,
        Delimiter = e.Delimiter, QuoteMode = e.QuoteMode, NullValue = e.NullValue, LineEnding = e.LineEnding, Utf8Bom = e.Utf8Bom,
        Gzip = e.Gzip, MaxRowsPerFile = e.MaxRowsPerFile, UploadWhenEmpty = e.UploadWhenEmpty, WriteManifest = e.WriteManifest,
        TriggerMode = e.TriggerMode, CronExpression = e.CronExpression, TimeZoneId = e.TimeZoneId,
        CommandTimeoutSec = e.CommandTimeoutSec, NotifyEmails = e.NotifyEmails, IsActive = e.IsActive
    };

    // ---------------------------------------------------------------- import: plan

    /// <summary>Validates the package against this installation without changing anything.</summary>
    public async Task<ImportPlan> PlanAsync(JobPackage package, JobImportOptions options, IJobImportAccess access)
    {
        var plan = new ImportPlan();
        if (!await access.CanEditAsync()) { plan.Errors.Add("Only Editors and Admins can import jobs."); return plan; }
        bool isAdmin = await access.IsAdminAsync();

        var servers = await repo.GetServersAsync();
        var destinations = await repo.GetDestinationsAsync();
        var extracts = await repo.GetExtractJobsAsync();
        var exports = await repo.GetExportJobsAsync();
        var deps = await repo.GetJobDependenciesAsync();
        var policy = await settings.GetRetentionPolicyAsync();

        SqlServerDef? Server(string name)
        {
            if (options.ServerMap.TryGetValue(name, out var id)) return servers.FirstOrDefault(s => s.SqlServerId == id);
            return servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        DestinationDef? Destination(string name)
        {
            if (options.DestinationMap.TryGetValue(name, out var id)) return destinations.FirstOrDefault(d => d.DestinationId == id);
            return destinations.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        plan.MissingServers.AddRange(package.ServerNames().Where(n => Server(n) is null));
        plan.MissingDestinations.AddRange(package.DestinationNames().Where(n => Destination(n) is null));

        // existing jobs are found by name; new jobs get temporary negative ids for the dependency graph
        int tempId = 0;
        var errors = new Dictionary<(string Kind, string Name), List<string>>(KindNameComparer.Instance);
        var notes = new Dictionary<(string Kind, string Name), List<string>>(KindNameComparer.Instance);
        var actions = new Dictionary<(string Kind, string Name), ImportAction>(KindNameComparer.Instance);
        List<string> Err((string, string) key) => errors.TryGetValue(key, out var l) ? l : errors[key] = [];
        List<string> Note((string, string) key) => notes.TryGetValue(key, out var l) ? l : notes[key] = [];
        var order = new List<(string Kind, string Name)>();

        foreach (var dup in package.ExtractJobs.GroupBy(j => j.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Err((JobRef.Extract, dup.Key)).Add("The package contains this extract job name more than once.");
        foreach (var dup in package.ExportJobs.GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Err((JobRef.Export, dup.Key)).Add("The package contains this export job name more than once.");

        // ---- extract jobs
        foreach (var spec in package.ExtractJobs)
        {
            var key = (JobRef.Extract, spec.Name.Trim());
            if (order.Contains(key, KindNameComparer.Instance)) continue;
            order.Add(key);
            var existing = extracts.FirstOrDefault(j => j.Name.Equals(key.Item2, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && options.ExistingJobs == ExistingJobAction.Skip)
            {
                actions[key] = ImportAction.Skip;
                plan.Refs[key] = new JobRef(JobRef.Extract, existing.ExtractJobId);
                Note(key).Add("A job with this name exists here and is left unchanged.");
                continue;
            }
            actions[key] = existing is null ? ImportAction.Create : ImportAction.Update;
            var def = ToDef(spec);
            def.ExtractJobId = existing?.ExtractJobId ?? 0;
            def.IsActive = existing?.IsActive ?? (!options.NewJobsInactive && spec.IsActive);
            plan.Refs[key] = new JobRef(JobRef.Extract, existing?.ExtractJobId ?? --tempId);

            var errs = Err(key);
            errs.AddRange(JobDefinitionRules.ValidateExtract(def));
            if (!Enum.IsDefined(def.SourceType)) errs.Add($"Unknown source type '{def.SourceType}'.");
            if (!Enum.IsDefined(def.LoadMode)) errs.Add($"Unknown load mode '{def.LoadMode}'.");
            if (!Enum.IsDefined(def.SchemaPolicy)) errs.Add($"Unknown schema policy '{def.SchemaPolicy}'.");
            if (Server(spec.SourceServer) is { } src) { def.SourceServerId = src.SqlServerId; def.SourceServerName = src.Name; }
            else errs.Add($"Source server '{spec.SourceServer}' does not exist here: map it to a server.");
            if (Server(spec.TargetServer) is { } tgt) { def.TargetServerId = tgt.SqlServerId; def.TargetServerName = tgt.Name; }
            else errs.Add($"Target server '{spec.TargetServer}' does not exist here: map it to a server.");
            if (def.SourceServerId != 0 && !await access.CanUseAsync(def.SourceServerId, def.SourceDatabase, "Source"))
                errs.Add($"You are not allowed to use {def.SourceServerName} / {def.SourceDatabase} as a source (or the server is inactive or not marked as a source).");
            if (def.TargetServerId != 0 && !await access.CanUseAsync(def.TargetServerId, def.TargetDatabase, "Target"))
                errs.Add($"You are not allowed to write to {def.TargetServerName} / {def.TargetDatabase} (or the server is inactive or not marked as a target).");
            if (existing is not null && !(await access.CanUseAsync(existing.SourceServerId, existing.SourceDatabase, "Source")
                                          && await access.CanUseAsync(existing.TargetServerId, existing.TargetDatabase, "Target")))
                errs.Add("You are not allowed to change the existing job with this name.");
            if (policy.ValidateJob(def.RetentionDays, existing?.RetentionDays, existing is null, isAdmin, def.LoadMode) is { } retentionError)
                errs.Add(retentionError);
            foreach (var q in spec.QualityChecks) errs.AddRange(ValidateCheck(q));
            if (existing is null && !def.IsActive) Note(key).Add(spec.IsActive ? "Created inactive: activate it on the job page when it should run." : "Inactive in the package.");
            if (existing is not null) Note(key).Add($"Overwrites the existing job; it stays {(existing.IsActive ? "active" : "inactive")}.");
            plan.Extracts.Add((spec, def));
        }

        // ---- export jobs
        foreach (var spec in package.ExportJobs)
        {
            var key = (JobRef.Export, spec.Name.Trim());
            if (order.Contains(key, KindNameComparer.Instance)) continue;
            order.Add(key);
            var existing = exports.FirstOrDefault(e => e.Name.Equals(key.Item2, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && options.ExistingJobs == ExistingJobAction.Skip)
            {
                actions[key] = ImportAction.Skip;
                plan.Refs[key] = new JobRef(JobRef.Export, existing.ExportJobId);
                Note(key).Add("A job with this name exists here and is left unchanged.");
                continue;
            }
            actions[key] = existing is null ? ImportAction.Create : ImportAction.Update;
            var def = ToDef(spec);
            def.ExportJobId = existing?.ExportJobId ?? 0;
            def.IsActive = existing?.IsActive ?? (!options.NewJobsInactive && spec.IsActive);
            plan.Refs[key] = new JobRef(JobRef.Export, existing?.ExportJobId ?? --tempId);

            var errs = Err(key);
            errs.AddRange(JobDefinitionRules.ValidateExport(def));
            if (!Enum.IsDefined(def.ExportMode)) errs.Add($"Unknown export mode '{def.ExportMode}'.");
            if (!Enum.IsDefined(def.FileFormat)) errs.Add($"Unknown file format '{def.FileFormat}'.");
            if (!Enum.IsDefined(def.QuoteMode)) errs.Add($"Unknown quote mode '{def.QuoteMode}'.");
            if (Server(spec.SourceServer) is { } src) { def.SourceServerId = src.SqlServerId; def.SourceServerName = src.Name; }
            else errs.Add($"Server '{spec.SourceServer}' does not exist here: map it to a server.");
            if (def.SourceServerId != 0 && !await access.CanUseAsync(def.SourceServerId, def.SourceDatabase, "Any"))
                errs.Add($"You are not allowed to use {def.SourceServerName} / {def.SourceDatabase} (or the server is inactive).");
            if (existing is not null && !await access.CanUseAsync(existing.SourceServerId, existing.SourceDatabase, "Any"))
                errs.Add("You are not allowed to change the existing job with this name.");
            if (Destination(spec.Destination) is not { } dst) errs.Add($"Destination '{spec.Destination}' does not exist here: map it to a destination.");
            else if (!dst.IsActive) errs.Add($"The destination '{dst.Name}' is inactive.");
            else { def.DestinationId = dst.DestinationId; def.DestinationName = dst.Name; def.DestinationProvider = dst.Provider; def.Bucket = dst.Bucket; }
            if (existing is null && !def.IsActive) Note(key).Add(spec.IsActive ? "Created inactive: activate it on the job page when it should run." : "Inactive in the package.");
            if (existing is not null) Note(key).Add($"Overwrites the existing job; it stays {(existing.IsActive ? "active" : "inactive")}.");
            plan.Exports.Add((spec, def));
        }

        // ---- target tables: Rebuild / Merge need their own table (existing jobs + the package)
        var finalExtracts = extracts.Where(j => !plan.Extracts.Any(p => p.Def.ExtractJobId == j.ExtractJobId)).Concat(plan.Extracts.Select(p => p.Def)).ToList();
        foreach (var (spec, def) in plan.Extracts)
            if (def.TargetServerId != 0 && JobDefinitionRules.TargetConflict(def, finalExtracts) is { } conflict)
                Err((JobRef.Extract, def.Name)).Add(conflict);

        // ---- dependencies: names resolve to package jobs first, then to existing jobs; no loops in the resulting graph
        JobRef? Resolve(JobRefSpec r)
        {
            var kind = r.Kind.Equals(JobRef.Export, StringComparison.OrdinalIgnoreCase) ? JobRef.Export
                     : r.Kind.Equals(JobRef.Extract, StringComparison.OrdinalIgnoreCase) ? JobRef.Extract : null;
            if (kind is null) return null;
            if (plan.Refs.TryGetValue((kind, r.Name.Trim()), out var inPackage)) return inPackage;
            if (kind == JobRef.Extract && extracts.FirstOrDefault(j => j.Name.Equals(r.Name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } x) return new JobRef(kind, x.ExtractJobId);
            if (kind == JobRef.Export && exports.FirstOrDefault(e => e.Name.Equals(r.Name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } y) return new JobRef(kind, y.ExportJobId);
            return null;
        }
        var graph = deps.Select(d => (Job: new JobRef(d.JobKind, d.JobId), Upstream: new JobRef(d.UpstreamKind, d.UpstreamId))).ToList();
        var planned = plan.Extracts.Select(p => (Key: (JobRef.Extract, p.Def.Name), p.Def.TriggerMode, p.Spec.RunsAfter))
            .Concat(plan.Exports.Select(p => (Key: (JobRef.Export, p.Def.Name), p.Def.TriggerMode, p.Spec.RunsAfter))).ToList();
        var upstreamsOf = new Dictionary<JobRef, List<JobRef>>();
        foreach (var (key, mode, runsAfter) in planned)
        {
            var me = plan.Refs[key];
            var ups = new List<JobRef>();
            if (mode == JobTriggerMode.AfterJobs)
            {
                if (runsAfter.Count == 0) Err(key).Add("The job runs after other jobs but the package lists none.");
                foreach (var r in runsAfter)
                {
                    if (Resolve(r) is not { } up) { Err(key).Add($"Upstream job '{r.Name}' ({r.Kind}) is neither in the package nor here."); continue; }
                    if (up == me) { Err(key).Add("A job cannot run after itself."); continue; }
                    if (!ups.Contains(up)) ups.Add(up);
                }
            }
            upstreamsOf[me] = ups;
            graph.RemoveAll(e => e.Job == me);
        }
        graph.AddRange(upstreamsOf.SelectMany(kv => kv.Value.Select(u => (Job: kv.Key, Upstream: u))));
        var names = plan.Refs.ToDictionary(kv => kv.Value, kv => kv.Key.Name);
        foreach (var j in extracts) names.TryAdd(new JobRef(JobRef.Extract, j.ExtractJobId), j.Name);
        foreach (var e in exports) names.TryAdd(new JobRef(JobRef.Export, e.ExportJobId), e.Name);
        foreach (var (key, _, _) in planned)
        {
            var me = plan.Refs[key];
            if (upstreamsOf[me].Count > 0 && DependencyRules.FindCycle(graph, me, upstreamsOf[me]) is { } cycle)
                Err(key).Add("The dependencies would form a loop: " + string.Join(" → ", cycle.Select(r => names.GetValueOrDefault(r, r.ToString()))) + ".");
        }
        plan.Upstreams = upstreamsOf;

        foreach (var key in order)
        {
            var errs = errors.GetValueOrDefault(key) ?? [];
            var action = errs.Count > 0 ? ImportAction.Error : actions[key];
            plan.Items.Add(new ImportItem(key.Kind, key.Name, action, errs.Concat(action == ImportAction.Error ? [] : notes.GetValueOrDefault(key) ?? []).Distinct().ToList()));
        }
        return plan;
    }

    private static IEnumerable<string> ValidateCheck(QualityCheckSpec q)
    {
        if (!QualityCheckTypes.All.Contains(q.CheckType)) yield return $"Unknown quality check type '{q.CheckType}'.";
        if (q.Scope is not ("Load" or "Table")) yield return $"Unknown quality check scope '{q.Scope}' (Load, Table).";
        if (q.Severity is not ("Warning" or "Error")) yield return $"Unknown quality check severity '{q.Severity}' (Warning, Error).";
        if (q.Operator is not (null or "=" or "<>" or "<" or "<=" or ">" or ">=")) yield return $"Unknown quality check operator '{q.Operator}'.";
        if (q.CheckType is QualityCheckTypes.NullRate or QualityCheckTypes.Duplicates && string.IsNullOrWhiteSpace(q.ColumnName))
            yield return $"Quality check {q.CheckType} needs a column.";
        if (q.CheckType == QualityCheckTypes.CustomSql && string.IsNullOrWhiteSpace(q.SqlText)) yield return "Quality check CustomSql needs a SQL text.";
    }

    private static ExtractJobDef ToDef(ExtractJobSpec s) => new()
    {
        Name = s.Name ?? "", SourceDatabase = s.SourceDatabase ?? "", SourceType = s.SourceType, SourceSchema = s.SourceSchema,
        SourceTable = s.SourceTable, ColumnList = s.ColumnList, FilterClause = s.FilterClause, SourceQuery = s.SourceQuery,
        UseNoLock = s.UseNoLock, ReadSnapshot = s.ReadSnapshot, LoadMode = s.LoadMode, WatermarkColumn = s.WatermarkColumn,
        KeyColumns = s.KeyColumns, MergeDeleteMissing = s.MergeDeleteMissing, SchemaPolicy = s.SchemaPolicy,
        PartitionDaily = s.PartitionDaily, ReplaceRebuild = s.ReplaceRebuild, TargetDatabase = s.TargetDatabase ?? "",
        TargetSchema = s.TargetSchema ?? "", TargetTable = s.TargetTable ?? "", TriggerMode = s.TriggerMode,
        CronExpression = s.CronExpression ?? "", TimeZoneId = s.TimeZoneId ?? "", RetentionDays = s.RetentionDays,
        CommandTimeoutSec = s.CommandTimeoutSec, BatchSize = s.BatchSize, FreshnessHours = s.FreshnessHours, NotifyEmails = s.NotifyEmails
    };

    private static ExportJobDef ToDef(ExportJobSpec s) => new()
    {
        Name = s.Name ?? "", ExportMode = s.ExportMode, SourceDatabase = s.SourceDatabase ?? "", SourceSchema = s.SourceSchema,
        SourceTable = s.SourceTable, SourceQuery = s.SourceQuery, ColumnList = s.ColumnList, IncludeTechnicalColumns = s.IncludeTechnicalColumns,
        ObjectNameTemplate = s.ObjectNameTemplate ?? "", FileFormat = s.FileFormat, ParquetCompression = s.ParquetCompression ?? "",
        Delimiter = s.Delimiter ?? "", QuoteMode = s.QuoteMode, NullValue = s.NullValue ?? "", LineEnding = s.LineEnding ?? "",
        Utf8Bom = s.Utf8Bom, Gzip = s.Gzip, MaxRowsPerFile = s.MaxRowsPerFile, UploadWhenEmpty = s.UploadWhenEmpty,
        WriteManifest = s.WriteManifest, TriggerMode = s.TriggerMode, CronExpression = s.CronExpression ?? "",
        TimeZoneId = s.TimeZoneId ?? "", CommandTimeoutSec = s.CommandTimeoutSec, NotifyEmails = s.NotifyEmails
    };

    // ---------------------------------------------------------------- import: apply

    /// <summary>
    /// Plans again (the state may have changed since the preview) and, when there are no errors, saves the jobs,
    /// their quality checks and dependencies. Throws when the plan has errors; nothing is changed then.
    /// </summary>
    public async Task<ImportResult> ApplyAsync(JobPackage package, JobImportOptions options, IJobImportAccess access)
    {
        var plan = await PlanAsync(package, options, access);
        if (plan.HasErrors)
            throw new InvalidOperationException("The import has errors; nothing was changed.\n" + string.Join("\n",
                plan.Errors.Concat(plan.Items.Where(i => i.Action == ImportAction.Error).Select(i => $"{i.Kind} {i.Name}: {string.Join(" ", i.Messages)}"))));

        var user = access.UserName;
        var ids = new Dictionary<JobRef, JobRef>();   // planned ref (temporary for new jobs) → saved ref
        var savedExtracts = new List<ExtractJobDef>();
        var savedExports = new List<ExportJobDef>();

        foreach (var (spec, def) in plan.Extracts)
        {
            var planned = plan.Refs[(JobRef.Extract, def.Name)];
            await repo.SaveExtractJobAsync(def, user);
            ids[planned] = new JobRef(JobRef.Extract, def.ExtractJobId);
            await SyncQualityChecksAsync(def.ExtractJobId, spec.QualityChecks, user);
            savedExtracts.Add(def);
        }
        foreach (var (_, def) in plan.Exports)
        {
            var planned = plan.Refs[(JobRef.Export, def.Name)];
            await repo.SaveExportJobAsync(def, user);
            ids[planned] = new JobRef(JobRef.Export, def.ExportJobId);
            savedExports.Add(def);
        }
        foreach (var (planned, ups) in plan.Upstreams)
            await repo.SaveJobDependenciesAsync(ids[planned].Kind, ids[planned].Id, ups.Select(u => ids.GetValueOrDefault(u, u)).ToList(), user);

        var result = new ImportResult(plan.Count(ImportAction.Create), plan.Count(ImportAction.Update), plan.Count(ImportAction.Skip), savedExtracts, savedExports);
        await repo.AuditAsync(user, "Import", "JobPackage", null, new
        {
            package.ExportedBy, package.ExportedAtUtc, package.AppVersion, options.ExistingJobs, options.NewJobsInactive,
            Created = plan.Items.Where(i => i.Action == ImportAction.Create).Select(i => $"{i.Kind} {i.Name}"),
            Updated = plan.Items.Where(i => i.Action == ImportAction.Update).Select(i => $"{i.Kind} {i.Name}"),
            Skipped = plan.Items.Where(i => i.Action == ImportAction.Skip).Select(i => $"{i.Kind} {i.Name}")
        });
        return result;
    }

    /// <summary>The job's checks become the package's: matching checks are updated (their results stay linked), others added or removed.</summary>
    private async Task SyncQualityChecksAsync(int extractJobId, IReadOnlyList<QualityCheckSpec> wanted, string user)
    {
        var existing = (await repo.GetQualityChecksAsync(extractJobId)).ToList();
        static string Key(string type, string? column, string scope, string? sql) =>
            $"{type}|{column?.Trim().ToUpperInvariant()}|{scope}|{sql?.Trim()}";
        foreach (var q in wanted)
        {
            var match = existing.FirstOrDefault(e => Key(e.CheckType, e.ColumnName, e.Scope, e.SqlText) == Key(q.CheckType, q.ColumnName, q.Scope, q.SqlText));
            if (match is not null) existing.Remove(match);
            var def = match ?? new QualityCheckDef { ExtractJobId = extractJobId };
            def.CheckType = q.CheckType; def.ColumnName = string.IsNullOrWhiteSpace(q.ColumnName) ? null : q.ColumnName.Trim();
            def.Threshold = q.Threshold; def.Operator = q.Operator; def.SqlText = string.IsNullOrWhiteSpace(q.SqlText) ? null : q.SqlText;
            def.Scope = q.Scope; def.Severity = q.Severity; def.IsActive = q.IsActive;
            await repo.SaveQualityCheckAsync(def, user);
        }
        foreach (var stale in existing) await repo.DeleteQualityCheckAsync(stale.QualityCheckId, user);
    }
}
