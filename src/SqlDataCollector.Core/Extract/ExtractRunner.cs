using System.Diagnostics;
using System.Text;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Options;

namespace SqlDataCollector.Extract;

public static class SourceCommandBuilder
{
    public const string WatermarkParam = "@WatermarkFrom";

    public static string BuildSql(ExtractJobDef job, bool hasWatermark)
    {
        if (job.SourceType == SourceType.Query)
            return job.SourceQuery!; // as is; in incremental mode the query uses @WatermarkFrom itself

        var sb = new StringBuilder("SELECT ");
        sb.Append(string.IsNullOrWhiteSpace(job.ColumnList) ? "*" : job.ColumnList);
        sb.Append(" FROM ").Append(SqlIdent.Quote(job.SourceSchema ?? "dbo", job.SourceTable!));
        if (job.UseNoLock) sb.Append(" WITH (NOLOCK)");

        var predicates = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.FilterClause)) predicates.Add($"({job.FilterClause})");
        if (job.LoadMode == LoadMode.Incremental && hasWatermark)
            predicates.Add($"{SqlIdent.Quote(job.WatermarkColumn!)} > {WatermarkParam}");
        if (predicates.Count > 0) sb.Append(" WHERE ").Append(string.Join(" AND ", predicates));
        return sb.ToString();
    }
}

/// <summary>
/// Reads data from a source SQL Server and writes it to the target table with SqlBulkCopy.
/// </summary>
public sealed class ExtractRunner(
    MetadataRepository repo,
    SqlConnectionFactory factory,
    TargetTableManager tableManager,
    IOptions<CollectorOptions> options,
    ILogger<ExtractRunner> logger)
{
    [JobDisplayName("Extract #{0}: {1}")]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [120, 600], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(int extractJobId, string jobName, string? triggeredBy, CancellationToken ct)
    {
        var job = await repo.GetExtractJobAsync(extractJobId);
        if (job is null) { logger.LogWarning("Extract job {Id} not found (may have been deleted).", extractJobId); return; }
        if (!job.IsActive && triggeredBy is null) { logger.LogInformation("Extract job {Name} is inactive; skipped.", job.Name); return; }

        var source = await repo.GetServerAsync(job.SourceServerId);
        var target = await repo.GetServerAsync(job.TargetServerId);
        if (source is not { IsActive: true, IsSource: true })
            throw new InvalidOperationException($"Source server is inactive or not marked as a source (Id={job.SourceServerId}).");
        if (target is not { IsActive: true, IsTarget: true })
            throw new InvalidOperationException($"Target server is inactive or not marked as a target (Id={job.TargetServerId}).");

        await using var jobLock = await AppLock.TryAcquireAsync(factory.MetadataConnectionString, LockNames.Extract(extractJobId), 0, ct);
        if (jobLock is null)
        {
            logger.LogWarning("Extract job {Name} is already running; this trigger was skipped.", job.Name);
            return;
        }

        var state = job.LoadMode == LoadMode.Incremental ? await repo.GetExtractStateAsync(extractJobId) : null;
        var loadId = await repo.StartLoadRunAsync(job, state?.WatermarkValue, triggeredBy);
        var loadedAtUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        SqlConnection? targetConn = null;
        var rebuild = job.LoadMode == LoadMode.Replace && job.ReplaceRebuild;
        var swapped = false;
        try
        {
            if (rebuild) await ValidateRebuildAsync(job);
            targetConn = await factory.OpenAsync(target, job.TargetDatabase, forWrite: true, ct);
            var (rows, wmType, wmValue) = await CopyAsync(job, source, state, loadId, loadedAtUtc, targetConn, rebuild, ct);

            if (rebuild)
            {
                // the loaded table replaces the old one in one short transaction; readers never see a half-loaded table
                await tableManager.SwapRebuiltAsync(targetConn, job.TargetSchema, job.TargetTable, ct);
                swapped = true;
            }

            await repo.CompleteLoadRunAsync(loadId, extractJobId, rows, wmType, wmValue);
            logger.LogInformation("Extract {Name}: {Rows:N0} rows, {Sec:N1} s (LoadId={LoadId}, watermark={Wm}{Rebuild})",
                job.Name, rows, sw.Elapsed.TotalSeconds, loadId, wmValue ?? state?.WatermarkValue ?? "-", rebuild ? ", table rebuilt" : "");

            if (job.LoadMode == LoadMode.Replace && !rebuild)
            {
                // Delete older loads only after success is recorded. If this fails, the next
                // Replace run removes the leftovers too (_LoadId < new).
                try
                {
                    var deleted = await SqlHelpers.DeleteInBatchesAsync(targetConn, job.TargetSchema, job.TargetTable,
                        $"{SqlIdent.Quote(SqlIdent.ExtractJobIdColumn)} = @job AND {SqlIdent.Quote(SqlIdent.LoadIdColumn)} < @load",
                        p => { p.AddWithValue("@job", extractJobId); p.AddWithValue("@load", loadId); },
                        options.Value.DeleteBatchSize, ct);
                    logger.LogInformation("Replace {Name}: deleted {Deleted:N0} rows of previous loads.", job.Name, deleted);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Replace {Name}: could not delete previous loads; will retry on the next run.", job.Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Extract {Name} failed (LoadId={LoadId}).", job.Name, loadId);
            bool cleaned;
            if (rebuild && !swapped)
            {
                // nothing was written to the real table: drop the half-loaded staging table, the previous data stays
                cleaned = true;
                if (targetConn is not null)
                    try { await tableManager.DropRebuildStagingAsync(targetConn, job.TargetSchema, job.TargetTable, CancellationToken.None); }
                    catch (Exception dropEx) { logger.LogWarning(dropEx, "Rebuild {Name}: could not drop the staging table; the next run drops it.", job.Name); }
            }
            else cleaned = targetConn is null || await CleanupLoadAsync(targetConn, job, loadId);
            // A load that cannot be cleaned stays 'CleanupPending'; the sync job retries; incremental exports do not move past it until cleaned
            await repo.FailLoadRunAsync(loadId, cleaned ? "Failed" : "CleanupPending", MetadataRepository.Truncate(ex));
            throw; // Hangfire retry
        }
        finally
        {
            if (targetConn is not null) await targetConn.DisposeAsync();
        }
    }

    /// <summary>Rebuild replaces the whole table: only for a table this job alone writes to, without daily partitions.</summary>
    private async Task ValidateRebuildAsync(ExtractJobDef job)
    {
        if (job.PartitionDaily)
            throw new InvalidOperationException("Replace with rebuild cannot be combined with daily partitioning.");
        var others = (await repo.GetExtractJobsAsync()).Where(j => j.ExtractJobId != job.ExtractJobId && j.TargetServerId == job.TargetServerId
            && j.TargetDatabase.Equals(job.TargetDatabase, StringComparison.OrdinalIgnoreCase)
            && j.TargetSchema.Equals(job.TargetSchema, StringComparison.OrdinalIgnoreCase)
            && j.TargetTable.Equals(job.TargetTable, StringComparison.OrdinalIgnoreCase)).Select(j => j.Name).ToList();
        if (others.Count > 0)
            throw new InvalidOperationException($"Replace with rebuild needs a target table that only this job writes to; also written by: {string.Join(", ", others)}.");
    }

    private async Task<(long Rows, string? WmType, string? WmValue)> CopyAsync(
        ExtractJobDef job, SqlServerDef sourceServer, ExtractState? state, long loadId, DateTime loadedAtUtc, SqlConnection collector,
        bool rebuild, CancellationToken ct)
    {
        await using var source = await factory.OpenAsync(sourceServer, job.SourceDatabase, forWrite: false, ct);

        bool hasWatermark = state?.WatermarkValue is not null;
        await using var cmd = source.CreateCommand();
        cmd.CommandText = SourceCommandBuilder.BuildSql(job, hasWatermark);
        cmd.CommandTimeout = job.CommandTimeoutSec;
        if (job.LoadMode == LoadMode.Incremental && (hasWatermark || job.SourceType == SourceType.Query))
            cmd.Parameters.Add(Watermark.ToParameter(SourceCommandBuilder.WatermarkParam, state?.WatermarkType, state?.WatermarkValue));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        // EXEC sp ... may return row-less results first
        while (reader.FieldCount == 0 && await reader.NextResultAsync(ct)) { }
        if (reader.FieldCount == 0)
            throw new InvalidOperationException("The source query returned no result set.");

        var schema = reader.GetColumnSchema();
        var map = rebuild
            ? await tableManager.PrepareRebuildAsync(collector, job.TargetSchema, job.TargetTable, schema, ct)
            : await tableManager.EnsureAsync(collector, job.TargetSchema, job.TargetTable, schema, job.SchemaPolicy, job.PartitionDaily, ct);

        int? wmOrdinal = null;
        string? wmType = null;
        if (job.LoadMode == LoadMode.Incremental)
        {
            var wmCol = schema.FirstOrDefault(c => string.Equals(c.ColumnName, job.WatermarkColumn, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Watermark column '{job.WatermarkColumn}' is not in the result set.");
            wmOrdinal = wmCol.ColumnOrdinal;
            wmType = Watermark.NormalizeType(wmCol.DataTypeName);
        }

        using var augmented = new AugmentedDataReader(reader, loadId, loadedAtUtc, job.ExtractJobId, job.SourceServerName, wmOrdinal);
        // rebuild: empty staging table, TABLOCK allows minimal logging
        using var bulk = new SqlBulkCopy(collector, rebuild ? SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.TableLock : SqlBulkCopyOptions.KeepNulls, null)
        {
            DestinationTableName = SqlIdent.Quote(job.TargetSchema, rebuild ? job.TargetTable + TargetTableManager.RebuildSuffix : job.TargetTable),
            BatchSize = job.BatchSize,
            BulkCopyTimeout = 0,
            NotifyAfter = Math.Max(job.BatchSize * 10, 100_000)
        };
        for (int i = 0; i < augmented.FieldCount; i++)
        {
            var name = augmented.GetName(i);
            bulk.ColumnMappings.Add(name, map[name]);
        }
        bulk.SqlRowsCopied += (_, e) => logger.LogInformation("Extract {Name}: {Rows:N0} rows copied...", job.Name, e.RowsCopied);

        await bulk.WriteToServerAsync(augmented, ct);

        string? wmValue = augmented.MaxWatermark is null ? null : Watermark.Serialize(augmented.MaxWatermark);
        return (augmented.RowsRead, wmValue is null ? null : wmType, wmValue);
    }

    private async Task<bool> CleanupLoadAsync(SqlConnection collector, ExtractJobDef job, long loadId)
    {
        try
        {
            if (!await SqlHelpers.HasLoadIdColumnAsync(collector, job.TargetSchema, job.TargetTable, CancellationToken.None)) return true;
            var n = await SqlHelpers.DeleteInBatchesAsync(collector, job.TargetSchema, job.TargetTable,
                $"{SqlIdent.Quote(SqlIdent.LoadIdColumn)} = @load", p => p.AddWithValue("@load", loadId),
                options.Value.DeleteBatchSize, CancellationToken.None);
            if (n > 0) logger.LogWarning("Extract {Name}: rolled back {Rows:N0} rows of the incomplete load (LoadId={LoadId}).", job.Name, n, loadId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Extract {Name}: could not clean up the incomplete load (LoadId={LoadId}); the sync job will retry.", job.Name, loadId);
            return false;
        }
    }
}

public static class LockNames
{
    public static string Extract(int id) => $"etl:extract:{id}";
    public static string Export(int id) => $"etl:export:{id}";
    public const string Retention = "etl:retention";
    public const string Sync = "etl:sync";
    public const string Alerts = "etl:alerts";
}
