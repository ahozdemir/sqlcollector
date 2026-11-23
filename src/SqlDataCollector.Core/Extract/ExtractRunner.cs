using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Options;
using SqlDataCollector.Services;

namespace SqlDataCollector.Extract;

public static class SourceCommandBuilder
{
    public const string WatermarkParam = "@WatermarkFrom";

    /// <param name="slice">Parallel reads: the slice of the split column this reader reads.</param>
    /// <param name="selectList">Replaces the column list (e.g. MIN/MAX of the split column).</param>
    public static string BuildSql(ExtractJobDef job, bool hasWatermark, SourceSlice? slice = null, string? selectList = null)
    {
        if (job.SourceType == SourceType.Query)
            return job.SourceQuery!; // as is; in incremental mode the query uses @WatermarkFrom itself

        var sb = new StringBuilder("SELECT ");
        sb.Append(selectList ?? (string.IsNullOrWhiteSpace(job.ColumnList) ? "*" : job.ColumnList));
        sb.Append(" FROM ").Append(SqlIdent.Quote(job.SourceSchema ?? "dbo", job.SourceTable!));
        if (job.UseNoLock && !job.ReadSnapshot) sb.Append(" WITH (NOLOCK)");   // a snapshot read is consistent by itself

        var predicates = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.FilterClause)) predicates.Add($"({job.FilterClause})");
        if (job.UsesWatermark() && hasWatermark)
            predicates.Add($"{SqlIdent.Quote(job.WatermarkColumn!)} > {WatermarkParam}");
        if (slice is not null) predicates.Add(slice.Predicate());
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
    MergeLoader mergeLoader,
    Privacy.MaskingService masking,
    Quality.QualityService quality,
    DependencyService dependencies,
    IOptions<CollectorOptions> options,
    ILogger<ExtractRunner> logger)
{
    /// <summary>Rows per bulk copy batch into a columnstore table (one full rowgroup).</summary>
    public const int ColumnstoreBatchSize = 1_048_576;

    [JobDisplayName("Extract #{0}: {1}")]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [120, 600], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(int extractJobId, string jobName, string? triggeredBy, CancellationToken ct)
    {
        try { await RunCoreAsync(extractJobId, triggeredBy, ct); }
        finally { await dependencies.TryEvaluateAsync(CancellationToken.None); }   // starts jobs that run "after" this one
    }

    private async Task RunCoreAsync(int extractJobId, string? triggeredBy, CancellationToken ct)
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

        var state = job.UsesStoredState() ? await repo.GetExtractStateAsync(extractJobId) : null;
        var loadId = await repo.StartLoadRunAsync(job, state?.WatermarkValue, triggeredBy);
        var loadedAtUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        SqlConnection? targetConn = null;
        var rebuild = job.LoadMode == LoadMode.Replace && job.ReplaceRebuild;
        var merge = job.LoadMode == LoadMode.Merge;
        var swapped = false;
        try
        {
            if (rebuild || merge) await ValidateSingleWriterAsync(job);
            targetConn = await factory.OpenAsync(target, job.TargetDatabase, forWrite: true, ct);
            var (rows, wmType, wmValue, mergeCounts) = await CopyAsync(job, source, target, state, loadId, loadedAtUtc, targetConn, rebuild, ct);

            if (rebuild)
            {
                // the loaded table replaces the old one in one short transaction; readers never see a half-loaded table
                await tableManager.SwapRebuiltAsync(targetConn, job.TargetSchema, job.TargetTable, ct);
                swapped = true;
            }

            await repo.CompleteLoadRunAsync(loadId, extractJobId, rows, wmType, wmValue, mergeCounts);
            logger.LogInformation("Extract {Name}: {Rows:N0} rows, {Sec:N1} s (LoadId={LoadId}, watermark={Wm}{Rebuild}{Merge})",
                job.Name, rows, sw.Elapsed.TotalSeconds, loadId, wmValue ?? state?.WatermarkValue ?? "-", rebuild ? ", table rebuilt" : "",
                mergeCounts is { } mc ? $", merge: {mc.Inserted:N0} inserted, {mc.Updated:N0} updated, {mc.Deleted:N0} deleted" : "");

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

            // data quality: the job's checks on what was just loaded (never fails the load itself; failed checks keep dependents waiting)
            await quality.RunAsync(job, loadId, rows, source, target, targetConn, ct);
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
            // a merge is applied in one transaction: a failure rolled it back, and a committed merge must never be deleted by _LoadId
            else cleaned = targetConn is null || merge || await CleanupLoadAsync(targetConn, job, loadId);
            // A load that cannot be cleaned stays 'CleanupPending'; the sync job retries; incremental exports do not move past it until cleaned
            await repo.FailLoadRunAsync(loadId, cleaned ? "Failed" : "CleanupPending", MetadataRepository.Truncate(ex));
            throw; // Hangfire retry
        }
        finally
        {
            if (targetConn is not null) await targetConn.DisposeAsync();
        }
    }

    /// <summary>Rebuild replaces the whole table and Merge keeps one row per key: only for a table this job alone writes to, without daily partitions.</summary>
    private async Task ValidateSingleWriterAsync(ExtractJobDef job)
    {
        var what = job.LoadMode == LoadMode.Merge ? "Merge" : "Replace with rebuild";
        if (job.PartitionDaily)
            throw new InvalidOperationException($"{what} cannot be combined with daily partitioning.");
        var others = (await repo.GetExtractJobsAsync()).Where(j => j.ExtractJobId != job.ExtractJobId && j.TargetServerId == job.TargetServerId
            && j.TargetDatabase.Equals(job.TargetDatabase, StringComparison.OrdinalIgnoreCase)
            && j.TargetSchema.Equals(job.TargetSchema, StringComparison.OrdinalIgnoreCase)
            && j.TargetTable.Equals(job.TargetTable, StringComparison.OrdinalIgnoreCase)).Select(j => j.Name).ToList();
        if (others.Count > 0)
            throw new InvalidOperationException($"{what} needs a target table that only this job writes to; also written by: {string.Join(", ", others)}.");
    }

    private async Task<(long Rows, string? WmType, string? WmValue, MergeCounts? Merge)> CopyAsync(
        ExtractJobDef job, SqlServerDef sourceServer, SqlServerDef targetServer, ExtractState? state, long loadId, DateTime loadedAtUtc, SqlConnection collector,
        bool rebuild, CancellationToken ct)
    {
        // a refused snapshot can surface at BEGIN, on the first statement or on the first row fetch (inside the bulk copy)
        try { return await CopyCoreAsync(job, sourceServer, targetServer, state, loadId, loadedAtUtc, collector, rebuild, ct); }
        catch (Exception ex) when (job.ReadSnapshot && SnapshotRefused(ex)) { throw SnapshotNotAllowed(job, ex); }
    }

    private static bool SnapshotRefused(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is SqlException s && IsSnapshotNotAllowed(s)) return true;
        return false;
    }

    private async Task<(long Rows, string? WmType, string? WmValue, MergeCounts? Merge)> CopyCoreAsync(
        ExtractJobDef job, SqlServerDef sourceServer, SqlServerDef targetServer, ExtractState? state, long loadId, DateTime loadedAtUtc, SqlConnection collector,
        bool rebuild, CancellationToken ct)
    {
        await using var source = await factory.OpenAsync(sourceServer, job.SourceDatabase, forWrite: false, ct);
        // optional consistent read: one point in time, nothing skipped or read twice while the source keeps changing
        await using var snapshot = job.ReadSnapshot ? await BeginSnapshotAsync(source, job, ct) : null;

        // change tracking: the version is taken BEFORE the changes are read; a change made meanwhile is read again next run (idempotent)
        var changes = job.UsesChangeTracking() ? await ChangeTrackingSource.PrepareAsync(source, snapshot, job, state, ct) : null;
        if (changes is not null) logger.LogInformation("Extract {Name}: change tracking – {Reason}", job.Name, changes.Reason);
        var delta = changes is { IsDelta: true };

        var (reader, cmd) = await OpenSourceReaderAsync(job, sourceServer, source, snapshot, state, changes, ct);
        await using var cmdScope = cmd;
        await using var readerScope = reader;

        // EXEC sp ... may return row-less results first
        while (reader.FieldCount == 0 && await reader.NextResultAsync(ct)) { }
        if (reader.FieldCount == 0)
            throw new InvalidOperationException("The source query returned no result set.");

        var schema = reader.GetColumnSchema();

        // data protection: rules that apply while collecting (matched against the source object and the target table)
        var policy = await masking.GetPolicyAsync();
        var objects = new[]
        {
            job.SourceType == SourceType.Table ? $"{job.SourceDatabase}.{job.SourceSchema ?? "dbo"}.{job.SourceTable}" : $"{job.SourceDatabase}.(query)",
            $"{job.TargetDatabase}.{job.TargetSchema}.{job.TargetTable}"
        };
        var masks = policy.ForExtract(objects, schema.Select(c => c.ColumnName).ToList());
        for (int i = 0; i < masks.Length; i++)
            if (masks[i] is not null && job.UsesWatermark() && string.Equals(schema[i].ColumnName, job.WatermarkColumn, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Extract {Name}: the watermark column {Column} is never masked (rule {Rule} ignored).", job.Name, schema[i].ColumnName, masks[i]!.Rule.Name);
                masks[i] = null;
            }
        if (masks.Any(m => m is not null))
            logger.LogInformation("Extract {Name}: masking {Columns}", job.Name, string.Join(", ", masks.Where(m => m is not null).Select(m => m!.Describe())));
        var targetColumns = Privacy.MaskedDbColumn.Apply(schema, masks);

        var map = rebuild
            ? await tableManager.PrepareRebuildAsync(collector, job.TargetSchema, job.TargetTable, targetColumns, ct, job.TargetStorage)
            : await tableManager.EnsureAsync(collector, job.TargetSchema, job.TargetTable, targetColumns, job.SchemaPolicy, job.PartitionDaily, ct, job.TargetStorage);

        int? wmOrdinal = null;
        string? wmType = null;
        if (job.UsesWatermark())
        {
            var wmCol = schema.FirstOrDefault(c => string.Equals(c.ColumnName, job.WatermarkColumn, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Watermark column '{job.WatermarkColumn}' is not in the result set.");
            wmOrdinal = wmCol.ColumnOrdinal;
            wmType = Watermark.NormalizeType(wmCol.DataTypeName);
        }

        MergePlan? plan = null;
        if (job.LoadMode == LoadMode.Merge)
        {
            // change tracking: the primary key; otherwise the job's key columns
            IReadOnlyList<string> keys = changes?.Keys ?? job.KeyColumnList();
            if (keys.Count == 0) throw new InvalidOperationException("Merge needs at least one key column.");
            var missing = keys.Where(k => !schema.Any(c => string.Equals(c.ColumnName, k, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"Merge key column(s) not in the source result set: {string.Join(", ", missing)}.");
            if (changes is not null && keys.FirstOrDefault(k => masks[schema.First(c => string.Equals(c.ColumnName, k, StringComparison.OrdinalIgnoreCase)).ColumnOrdinal!.Value] is not null) is { } maskedKey)
                throw new InvalidOperationException(
                    $"Change tracking: the key column {maskedKey} cannot be masked while collecting (deleted keys are matched by their original values).");
            var keyTargets = keys.Select(k => map[k]).ToList();
            await tableManager.EnsureMergeKeyAsync(collector, job.TargetSchema, job.TargetTable, keyTargets, ct);
            var others = schema.Select(c => map[c.ColumnName]).Where(n => !keyTargets.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            // a full read deletes what the source no longer has: always for change tracking (first run, expired retention), optional otherwise
            var deleteMissing = changes is not null ? !delta : job.MergeDeleteMissing && !job.UsesWatermark();
            plan = new MergePlan(job.TargetSchema, job.TargetTable, keyTargets, others,
                await mergeLoader.GetXmlColumnsAsync(collector, job.TargetSchema, job.TargetTable, ct),
                DeleteMissing: deleteMissing, job.ExtractJobId, DeleteKeys: delta);
            // rows go to a session temp table first; the target changes only in the final merge transaction
            await mergeLoader.CreateStagingAsync(collector, job.TargetSchema, job.TargetTable, ct);
            if (delta) await mergeLoader.CreateDeleteStagingAsync(collector, job.TargetSchema, job.TargetTable, keyTargets, ct);
        }

        // a columnstore table takes rows straight into compressed rowgroups only in batches of at least 102,400 rows;
        // smaller batches land in the uncompressed delta store (the table decides, not the job setting: an existing table is never converted)
        var batchSize = job.BatchSize;
        if (plan is null && await TargetTableManager.IsColumnstoreAsync(collector, job.TargetSchema,
                rebuild ? job.TargetTable + TargetTableManager.RebuildSuffix : job.TargetTable, ct))
            batchSize = Math.Max(batchSize, ColumnstoreBatchSize);

        using var augmented = new AugmentedDataReader(reader, loadId, loadedAtUtc, job.ExtractJobId, job.SourceServerName, wmOrdinal, masks);
        var destination = plan is not null ? MergeSql.Staging
            : SqlIdent.Quote(job.TargetSchema, rebuild ? job.TargetTable + TargetTableManager.RebuildSuffix : job.TargetTable);
        // a merge stages into a session temp table: one writer
        var writers = plan is null && job.UsesParallelWrite() ? Math.Min(job.WriteParallelism, ExtractJobRules.MaxWriteParallelism) : 1;
        void Configure(SqlBulkCopy bulk, int writer)
        {
            bulk.DestinationTableName = destination;
            bulk.BatchSize = batchSize;
            bulk.BulkCopyTimeout = 0;
            bulk.NotifyAfter = Math.Max(job.BatchSize * 10, 100_000);
            for (int i = 0; i < augmented.FieldCount; i++)
            {
                var name = augmented.GetName(i);
                bulk.ColumnMappings.Add(name, map[name]);
            }
            bulk.SqlRowsCopied += writers > 1
                ? (_, e) => logger.LogInformation("Extract {Name}: writer {Writer}: {Rows:N0} rows copied...", job.Name, writer + 1, e.RowsCopied)
                : (_, e) => logger.LogInformation("Extract {Name}: {Rows:N0} rows copied...", job.Name, e.RowsCopied);
        }

        if (writers > 1)
        {
            // N connections write at once (each compresses its own columnstore rowgroups); concurrent bulk loads cannot hold a table lock
            logger.LogInformation("Extract {Name}: {Writers} parallel writers", job.Name, writers);
            await ParallelBulkWriter.WriteAsync(augmented, writers, t => factory.OpenAsync(targetServer, job.TargetDatabase, forWrite: true, t),
                SqlBulkCopyOptions.KeepNulls, Configure, ct);
        }
        else
        {
            // rebuild: empty staging table, TABLOCK allows minimal logging
            using var bulk = new SqlBulkCopy(collector, rebuild || plan is not null ? SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.TableLock : SqlBulkCopyOptions.KeepNulls, null);
            Configure(bulk, 0);
            await bulk.WriteToServerAsync(augmented, ct);
        }

        // integrity: every row read from the source must have arrived. Counted on the target side (this load, the rebuild table
        // or the merge staging table), so ongoing changes in the source can never cause a false alarm.
        var written = await CountWrittenAsync(collector, job, loadId, rebuild, plan is not null, ct);
        if (written != augmented.RowsRead)
            throw new InvalidOperationException(
                $"Integrity check failed: {augmented.RowsRead:N0} row(s) were read from the source but {written:N0} arrived in the target. The load is rolled back.");

        if (delta)
        {
            // the source connection serves one query at a time: the changed rows are read, now the deleted keys
            augmented.Dispose();
            var deleted = await CopyDeletedKeysAsync(job, source, snapshot, changes!, plan!.Keys, collector, ct);
            logger.LogInformation("Extract {Name}: change tracking – {Rows:N0} changed row(s), {Deleted:N0} deleted key(s)", job.Name, augmented.RowsRead, deleted);
        }

        var mergeCounts = plan is null ? null : await mergeLoader.MergeAsync(collector, plan, augmented.RowsRead, job.Name, ct);

        if (changes is not null)   // the version taken before reading: the next run reads the changes after it
            return (augmented.RowsRead, ChangeTrackingSql.VersionType, changes.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), mergeCounts);
        string? wmValue = augmented.MaxWatermark is null ? null : Watermark.Serialize(augmented.MaxWatermark);
        return (augmented.RowsRead, wmValue is null ? null : wmType, wmValue, mergeCounts);
    }

    /// <summary>
    /// The source reader of a run: the changed rows (change tracking), several readers at once (parallel reads) or one reader.
    /// The command (null for parallel reads, which own their connections) is disposed after the reader.
    /// </summary>
    private async Task<(DbDataReader Reader, SqlCommand? Command)> OpenSourceReaderAsync(ExtractJobDef job, SqlServerDef sourceServer,
        SqlConnection source, SqlTransaction? snapshot, ExtractState? state, ChangeTrackingRead? changes, CancellationToken ct)
    {
        bool hasWatermark = job.UsesWatermark() && state?.WatermarkValue is not null;
        SqlCommand cmd;
        if (changes is { IsDelta: true })
        {
            cmd = SourceCommand(source, snapshot, job, ChangeTrackingSql.ChangedRows(job, changes.Keys));
            cmd.Parameters.Add(new SqlParameter(ChangeTrackingSql.FromParam, System.Data.SqlDbType.BigInt) { Value = changes.FromVersion!.Value });
        }
        else
        {
            if (job.UsesParallelRead())
            {
                if (job.ReadSnapshot)
                    logger.LogWarning("Extract {Name}: parallel reads are not combined with a consistent snapshot; one reader is used.", job.Name);
                else if (await ParallelSourceReader.OpenAsync(factory, sourceServer, job, state, hasWatermark, ct) is { } parallel)
                {
                    logger.LogInformation("Extract {Name}: {Readers} parallel readers, {Split}", job.Name, parallel.Readers, parallel.Description);
                    return (parallel, null);
                }
                else logger.LogInformation("Extract {Name}: the split range is too small for parallel readers; one reader is used.", job.Name);
            }
            cmd = SourceCommand(source, snapshot, job, SourceCommandBuilder.BuildSql(job, hasWatermark));
            if (job.UsesWatermark() && (hasWatermark || job.SourceType == SourceType.Query))
                cmd.Parameters.Add(Watermark.ToParameter(SourceCommandBuilder.WatermarkParam, state?.WatermarkType, state?.WatermarkValue));
        }
        try { return (await ExecuteSourceAsync(cmd, job, ct), cmd); }
        catch { await cmd.DisposeAsync(); throw; }
    }

    private static SqlCommand SourceCommand(SqlConnection source, SqlTransaction? snapshot, ExtractJobDef job, string sql)
    {
        var cmd = source.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = snapshot;
        cmd.CommandTimeout = job.CommandTimeoutSec;
        return cmd;
    }

    /// <summary>Change tracking: copies the keys deleted in the source (or filtered out) to #sdc_delete; returns their count.</summary>
    private static async Task<long> CopyDeletedKeysAsync(ExtractJobDef job, SqlConnection source, SqlTransaction? snapshot, ChangeTrackingRead changes,
        IReadOnlyList<string> keyTargets, SqlConnection collector, CancellationToken ct)
    {
        await using var cmd = SourceCommand(source, snapshot, job, ChangeTrackingSql.DeletedKeys(job, changes.Keys));
        cmd.Parameters.Add(new SqlParameter(ChangeTrackingSql.FromParam, System.Data.SqlDbType.BigInt) { Value = changes.FromVersion!.Value });
        await using var r = await ExecuteSourceAsync(cmd, job, ct);
        using var counted = new AugmentedDataReader(r, 0, DateTime.UtcNow, job.ExtractJobId, job.SourceServerName, null);
        using (var bulk = new SqlBulkCopy(collector, SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.TableLock, null)
               { DestinationTableName = MergeSql.DeleteStaging, BatchSize = job.BatchSize, BulkCopyTimeout = 0 })
        {
            for (int i = 0; i < keyTargets.Count; i++) bulk.ColumnMappings.Add(i, keyTargets[i]);
            await bulk.WriteToServerAsync(counted, ct);
        }
        await using var count = collector.CreateCommand();
        count.CommandText = $"SELECT COUNT_BIG(*) FROM {MergeSql.DeleteStaging};";
        count.CommandTimeout = 0;
        var staged = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        if (staged != counted.RowsRead)
            throw new InvalidOperationException(
                $"Integrity check failed: {counted.RowsRead:N0} deleted key(s) were read from the source but {staged:N0} arrived in the target. The load is rolled back.");
        return staged;
    }

    private static async Task<SqlTransaction> BeginSnapshotAsync(SqlConnection source, ExtractJobDef job, CancellationToken ct)
    {
        try { return (SqlTransaction)await source.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot, ct); }
        catch (SqlException ex) when (IsSnapshotNotAllowed(ex)) { throw SnapshotNotAllowed(job, ex); }
    }

    private static async Task<SqlDataReader> ExecuteSourceAsync(SqlCommand cmd, ExtractJobDef job, CancellationToken ct)
    {
        try { return await cmd.ExecuteReaderAsync(ct); }
        catch (SqlException ex) when (IsSnapshotNotAllowed(ex)) { throw SnapshotNotAllowed(job, ex); }
    }

    /// <summary>Error 3952 (and the related 3951/3960 family) when the database does not allow SNAPSHOT isolation.</summary>
    public static bool IsSnapshotNotAllowed(SqlException ex) =>
        ex.Number is 3952 or 3951 || ex.Message.Contains("snapshot isolation is not allowed", StringComparison.OrdinalIgnoreCase);

    public static InvalidOperationException SnapshotNotAllowed(ExtractJobDef job, Exception inner) => new(
        $"Snapshot reads are not allowed in source database {job.SourceDatabase}. The DBA enables them once with " +
        $"ALTER DATABASE {SqlIdent.Quote(job.SourceDatabase)} SET ALLOW_SNAPSHOT_ISOLATION ON; – or untick \"Read a consistent snapshot\".", inner);

    /// <summary>Rows of this load in the target: the merge staging table, the rebuild table, or the rows with this _LoadId.</summary>
    private static async Task<long> CountWrittenAsync(SqlConnection collector, ExtractJobDef job, long loadId, bool rebuild, bool merge, CancellationToken ct)
    {
        await using var cmd = collector.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = merge ? $"SELECT COUNT_BIG(*) FROM {MergeSql.Staging};"
            : rebuild ? $"SELECT COUNT_BIG(*) FROM {SqlIdent.Quote(job.TargetSchema, job.TargetTable + TargetTableManager.RebuildSuffix)};"
            : $"SELECT COUNT_BIG(*) FROM {SqlIdent.Quote(job.TargetSchema, job.TargetTable)} WHERE {SqlIdent.Quote(SqlIdent.LoadIdColumn)} = @load;";
        cmd.Parameters.AddWithValue("@load", loadId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
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
    public const string Dependencies = "etl:dependencies";
}
