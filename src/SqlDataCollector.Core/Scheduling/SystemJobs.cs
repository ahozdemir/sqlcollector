using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Options;
using SqlDataCollector.Export;
using SqlDataCollector.Extract;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Options;
using SqlDataCollector.Services;

namespace SqlDataCollector.Scheduling;

/// <summary>Removes old target data according to RetentionDays, and old run logs.</summary>
public sealed class RetentionRunner(
    MetadataRepository repo,
    SqlConnectionFactory factory,
    TargetTableManager tableManager,
    SystemSettingsService settings,
    IOptions<CollectorOptions> options,
    ILogger<RetentionRunner> logger)
{
    [JobDisplayName("Retention")]
    [AutomaticRetry(Attempts = 1)]
    public async Task RunAsync(CancellationToken ct)
    {
        await using var l = await AppLock.TryAcquireAsync(factory.MetadataConnectionString, LockNames.Retention, 0, ct);
        if (l is null) { logger.LogWarning("Retention is already running; skipped."); return; }

        var policy = await settings.GetRetentionPolicyAsync();
        var jobs = (await repo.GetExtractJobsAsync()).Select(j => (Job: j, Days: policy.EffectiveDays(j.RetentionDays, j.LoadMode))).ToList();
        foreach (var (job, days) in jobs.Where(x => x.Job.LoadMode != LoadMode.Replace && x.Days != x.Job.RetentionDays))
            logger.LogInformation("Retention {Name}: policy applies {Days} instead of the job's {JobDays}.", job.Name,
                days?.ToString() ?? "keep forever", job.RetentionDays?.ToString() ?? "keep forever");
        var servers = (await repo.GetServersAsync()).ToDictionary(s => s.SqlServerId);

        // Jobs writing to the same table are grouped: on a partitioned table, days older than the longest
        // retention are dropped instantly with TRUNCATE; jobs with shorter retention get a row-based DELETE.
        foreach (var group in jobs.GroupBy(x => (x.Job.TargetServerId, x.Job.TargetDatabase, x.Job.TargetSchema, x.Job.TargetTable)))
        {
            var (serverId, database, schema, table) = group.Key;
            var withRetention = group.Where(x => x.Days is > 0).ToList();
            if (withRetention.Count == 0) continue;
            try
            {
                if (!servers.TryGetValue(serverId, out var server) || !server.IsActive) continue;
                await using var conn = await factory.OpenAsync(server, database, forWrite: true, ct);
                if (!await SqlHelpers.HasLoadIdColumnAsync(conn, schema, table, ct)) continue;

                // Whole-day TRUNCATE only when EVERY job writing to the table has a retention: a partition also
                // holds the rows of a "keep forever" job, which must survive.
                if (withRetention.Count == group.Count())
                {
                    var maxDays = withRetention.Max(x => x.Days!.Value);
                    var (partitioned, truncated) = await tableManager.TruncateOldPartitionsAsync(
                        conn, schema, table, DateTime.UtcNow.AddDays(-maxDays), ct);
                    if (partitioned)
                        logger.LogInformation("Retention {Schema}.{Table}: truncated {Count} daily partition(s) ({Days} days).", schema, table, truncated, maxDays);
                }

                foreach (var (job, days) in withRetention)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-days!.Value);
                    var n = await SqlHelpers.DeleteInBatchesAsync(conn, schema, table,
                        $"{SqlIdent.Quote(SqlIdent.LoadedAtColumn)} < @cutoff AND {SqlIdent.Quote(SqlIdent.ExtractJobIdColumn)} = @job",
                        p => { p.AddWithValue("@cutoff", cutoff); p.AddWithValue("@job", job.ExtractJobId); },
                        options.Value.DeleteBatchSize, ct);
                    if (n > 0) logger.LogInformation("Retention {Name}: deleted {Rows:N0} rows (older than {Days} days).", job.Name, n, days);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention {Schema}.{Table} failed; continuing with the other tables.", schema, table);
            }
        }

        var logs = await repo.PurgeRunLogsAsync(policy.RunLogDays, policy.AuditDays, policy.PermissionCheckDays, options.Value.DeleteBatchSize, ct);
        logger.LogInformation("Retention: deleted {Rows:N0} run log, audit and permission check records.", logs);
    }
}

/// <summary>
/// Syncs etl.ExtractJob / etl.ExportJob definitions to Hangfire recurring jobs,
/// removes deleted/inactive jobs and cleans up crashed (incomplete) loads.
/// </summary>
public sealed class JobSynchronizer(
    MetadataRepository repo,
    SqlConnectionFactory factory,
    JobRegistrar registrar,
    IRecurringJobManager recurring,
    JobStorage storage,
    SystemSettingsService settings,
    IOptions<CollectorOptions> options,
    ILogger<JobSynchronizer> logger)
{
    [JobDisplayName("Sync job definitions")]
    [AutomaticRetry(Attempts = 0)]
    public async Task SyncAsync(CancellationToken ct)
    {
        await using var l = await AppLock.TryAcquireAsync(factory.MetadataConnectionString, LockNames.Sync, 0, ct);
        if (l is null) return;

        // The UI registers jobs on "Save"; this loop catches direct SQL changes and lost registrations.
        var desired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var j in await repo.GetExtractJobsAsync())
        {
            if (!j.IsActive) continue;
            desired.Add(JobRegistrar.ExtractId(j.ExtractJobId));
            TryRegister(JobRegistrar.ExtractId(j.ExtractJobId), j.Name, () => registrar.Register(j));
        }
        foreach (var e in await repo.GetExportJobsAsync())
        {
            if (!e.IsActive) continue;
            desired.Add(JobRegistrar.ExportId(e.ExportJobId));
            TryRegister(JobRegistrar.ExportId(e.ExportJobId), e.Name, () => registrar.Register(e));
        }

        using (var conn = storage.GetConnection())
        {
            foreach (var rj in conn.GetRecurringJobs())
            {
                if ((rj.Id.StartsWith(JobRegistrar.ExtractPrefix, StringComparison.Ordinal) || rj.Id.StartsWith(JobRegistrar.ExportPrefix, StringComparison.Ordinal))
                    && !desired.Contains(rj.Id))
                {
                    recurring.RemoveIfExists(rj.Id);
                    logger.LogInformation("Recurring job removed: {Id}", rj.Id);
                }
            }
        }

        // Retention schedule from the Settings page (no restart needed)
        try { settings.RegisterRetentionJob(recurring, (await settings.GetRetentionPolicyAsync()).Cron); }
        catch (Exception ex) { logger.LogError(ex, "The retention schedule could not be registered in Hangfire."); }

        await RecoverDeadLoadsAsync(ct);
        await RecoverDeadExportsAsync(ct);
    }

    private void TryRegister(string id, string name, Action register)
    {
        try { register(); }
        catch (Exception ex)
        {
            // A bad cron/time zone must not affect other jobs; the existing registration is kept.
            logger.LogError(ex, "Job '{Name}' ({Id}) could not be registered in Hangfire.", name, id);
        }
    }

    /// <summary>Rolls back loads shown as Running/CleanupPending whose lock nobody holds.</summary>
    private async Task RecoverDeadLoadsAsync(CancellationToken ct)
    {
        foreach (var load in await repo.GetRunningLoadsAsync())
        {
            await using var l = await AppLock.TryAcquireAsync(factory.MetadataConnectionString, LockNames.Extract(load.ExtractJobId), 0, ct);
            if (l is null) continue; // really running

            // The load may have finished after the list was read: re-check the status after acquiring the lock
            var status = await repo.GetLoadStatusAsync(load.LoadRunId);
            if (status is not ("Running" or "CleanupPending")) continue;

            try
            {
                var server = await repo.GetServerAsync(load.TargetServerId)
                             ?? throw new InvalidOperationException($"Target server not found (Id={load.TargetServerId}).");
                await using var conn = await factory.OpenAsync(server, load.TargetDatabase, forWrite: true, ct);
                long n = 0;
                if (await SqlHelpers.HasLoadIdColumnAsync(conn, load.TargetSchema, load.TargetTable, ct))
                    n = await SqlHelpers.DeleteInBatchesAsync(conn, load.TargetSchema, load.TargetTable,
                        $"{SqlIdent.Quote(SqlIdent.LoadIdColumn)} = @load", p => p.AddWithValue("@load", load.LoadRunId),
                        options.Value.DeleteBatchSize, ct);
                await repo.MarkLoadAbandonedAsync(load.LoadRunId, $"Incomplete load cleaned up ({n} rows).");
                logger.LogWarning("Incomplete load cleaned up: LoadId={LoadId}, {Rows:N0} rows", load.LoadRunId, n);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not clean up incomplete load: LoadId={LoadId}", load.LoadRunId);
            }
        }
    }

    private async Task RecoverDeadExportsAsync(CancellationToken ct)
    {
        foreach (var run in await repo.GetRunningExportsAsync())
        {
            await using var l = await AppLock.TryAcquireAsync(factory.MetadataConnectionString, LockNames.Export(run.ExportJobId), 0, ct);
            if (l is null) continue;
            await repo.MarkExportAbandonedAsync(run.ExportRunId);
        }
    }
}

/// <summary>On startup: installs the schema, registers system jobs and runs the first sync.</summary>
public sealed class StartupService(
    IServiceProvider services,
    SqlConnectionFactory factory,
    IRecurringJobManager recurring,
    IOptions<CollectorOptions> options,
    ILogger<StartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (o.AutoCreateSchema) await SchemaInstaller.InstallAsync(factory, logger, ct);

        var tz = TimeZones.Resolve(o.SystemTimeZone);
        recurring.AddOrUpdate<JobSynchronizer>("system-sync", CollectorQueues.System, s => s.SyncAsync(CancellationToken.None),
            o.SyncCron, new RecurringJobOptions { TimeZone = tz });
        var settings = services.GetRequiredService<SystemSettingsService>();
        settings.RegisterRetentionJob(recurring, (await settings.GetRetentionPolicyAsync()).Cron);

        // The first sync + incomplete-load cleanup needs write access to target DBs: Worker role only.
        // (The Web role registers jobs in Hangfire via JobRegistrar on "Save".)
        if (o.IsWorker)
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<JobSynchronizer>().SyncAsync(ct);

            // Permission self-check of this Worker's account for its queues; in the background so that
            // unreachable servers (connect timeouts) never delay startup. Results: Servers page + log warnings.
            var checker = services.GetRequiredService<PermissionChecker>();
            var queues = o.QueueList.Intersect(CollectorQueues.All).ToList();
            _selfCheck = Task.Run(async () =>
            {
                foreach (var q in queues)
                {
                    try { await checker.CheckAndSaveAsync(checker.WorkerContext(q), _stopping.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { logger.LogWarning(ex, "Permission self-check for queue {Queue} failed.", q); }
                }
            });
        }
        logger.LogInformation("SqlDataCollector started.");
    }

    private readonly CancellationTokenSource _stopping = new();
    private Task? _selfCheck;

    public async Task StopAsync(CancellationToken ct)
    {
        await _stopping.CancelAsync();
        if (_selfCheck is not null) await Task.WhenAny(_selfCheck, Task.Delay(Timeout.Infinite, ct));
    }
}
