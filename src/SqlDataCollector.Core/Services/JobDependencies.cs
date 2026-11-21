using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlDataCollector.Export;
using SqlDataCollector.Extract;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;

namespace SqlDataCollector.Services;

/// <summary>Pure dependency logic (readiness and cycle detection); no I/O, unit tested.</summary>
public static class DependencyRules
{
    /// <summary>
    /// Jobs whose upstreams have ALL succeeded after the job's baseline. The baseline is the later of
    /// its last dependency trigger and its newest dependency row (a dependency added today does not fire on yesterday's success).
    /// Only jobs in <paramref name="eligible"/> (active, AfterJobs, not running) are returned.
    /// </summary>
    public static IReadOnlyList<JobRef> FindReady(IEnumerable<DependencyStateRow> rows, ISet<JobRef> eligible)
    {
        var ready = new List<JobRef>();
        foreach (var g in rows.GroupBy(r => r.Job))
        {
            if (!eligible.Contains(g.Key)) continue;
            var baseline = g.Max(r => r.CreatedAtUtc);
            var triggered = g.Max(r => r.LastTriggeredAtUtc);
            if (triggered > baseline) baseline = triggered.Value;
            if (g.All(r => r.UpstreamLastSuccessUtc > baseline)) ready.Add(g.Key);
        }
        return ready;
    }

    /// <summary>
    /// Returns the cycle (job → … → job) that saving <paramref name="upstreams"/> for <paramref name="job"/> would create, or null.
    /// The job's current dependencies in <paramref name="existing"/> are replaced by the new ones.
    /// </summary>
    public static IReadOnlyList<JobRef>? FindCycle(IEnumerable<(JobRef Job, JobRef Upstream)> existing, JobRef job, IEnumerable<JobRef> upstreams)
    {
        var graph = existing.Where(e => e.Job != job)
            .Concat(upstreams.Select(u => (Job: job, Upstream: u)))
            .GroupBy(e => e.Job).ToDictionary(g => g.Key, g => g.Select(e => e.Upstream).Distinct().ToList());

        // depth-first from the job along "depends on" edges; reaching the job again is a cycle
        var path = new List<JobRef> { job };
        var done = new HashSet<JobRef>();
        bool Visit(JobRef node)
        {
            if (!graph.TryGetValue(node, out var ups)) return false;
            foreach (var up in ups)
            {
                path.Add(up);
                if (up == job) return true;
                if (done.Add(up) && Visit(up)) return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
        return Visit(job) ? path : null;
    }

    /// <summary>"after A, B" text stored as TriggeredBy (column is nvarchar(256)).</summary>
    public static string TriggeredByText(IEnumerable<string> upstreamNames)
    {
        var text = "after " + string.Join(", ", upstreamNames);
        return text.Length <= 250 ? text : text[..247] + "...";
    }
}

/// <summary>Starts a dependency-triggered job. Production: Hangfire enqueue on the job's queue; tests: a recorder.</summary>
public interface IDependentJobLauncher
{
    /// <summary>False in processes without a Hangfire client (e.g. the run-job CLI): nothing is evaluated or recorded there.</summary>
    bool CanLaunch { get; }
    void Launch(JobRef job, string jobName, string triggeredBy);
}

/// <summary>Enqueues on sdc_extract / sdc_export. IBackgroundJobClient is resolved on use: hosts without Hangfire client never launch.</summary>
public sealed class HangfireDependentJobLauncher(IServiceProvider services) : IDependentJobLauncher
{
    public bool CanLaunch => services.GetService<IBackgroundJobClient>() is not null;

    public void Launch(JobRef job, string jobName, string triggeredBy)
    {
        var client = services.GetRequiredService<IBackgroundJobClient>();
        int id = job.Id;
        if (job.Kind == JobRef.Extract)
            client.Enqueue<ExtractRunner>(CollectorQueues.Extract, r => r.RunAsync(id, jobName, triggeredBy, CancellationToken.None));
        else
            client.Enqueue<ExportRunner>(CollectorQueues.Export, r => r.RunAsync(id, jobName, triggeredBy, CancellationToken.None));
    }
}

/// <summary>
/// Starts jobs whose trigger is "after other jobs". Evaluated right after every extract/export run
/// (in the Worker that ran it) and by the periodic sync job, which catches anything missed.
/// </summary>
public sealed class DependencyService(
    MetadataRepository repo,
    SqlConnectionFactory factory,
    IDependentJobLauncher launcher,
    ILogger<DependencyService> logger)
{
    /// <summary>Starts every ready dependent job once and returns them.</summary>
    public async Task<IReadOnlyList<JobRef>> EvaluateAsync(CancellationToken ct)
    {
        // e.g. the run-job CLI runs jobs without Hangfire: leave the dependents to the sync job of a Worker (no trigger is recorded here)
        if (!launcher.CanLaunch) { logger.LogDebug("Dependent jobs are not evaluated in this process (no Hangfire client)."); return []; }

        // wait briefly: an evaluation that is in progress may have read the state before our run's success was committed
        await using var l = await AppLock.TryAcquireAsync(factory.MetadataConnectionString, LockNames.Dependencies, 15000, ct);
        if (l is null) { logger.LogInformation("Dependency evaluation skipped: another process holds the lock."); return []; }

        var rows = await repo.GetDependencyStatesAsync();
        if (rows.Count == 0) return [];

        var names = new Dictionary<JobRef, string>();
        var eligible = new HashSet<JobRef>();
        foreach (var j in await repo.GetExtractJobsAsync())
        {
            var r = new JobRef(JobRef.Extract, j.ExtractJobId);
            names[r] = j.Name;
            if (j.IsActive && j.TriggerMode == JobTriggerMode.AfterJobs) eligible.Add(r);
        }
        foreach (var e in await repo.GetExportJobsAsync())
        {
            var r = new JobRef(JobRef.Export, e.ExportJobId);
            names[r] = e.Name;
            if (e.IsActive && e.TriggerMode == JobTriggerMode.AfterJobs) eligible.Add(r);
        }
        // a running dependent is evaluated again when its run ends
        foreach (var s in await repo.GetLastRunStartsAsync())
            if (s.Running) eligible.Remove(new JobRef(s.Kind, s.JobId));

        var started = new List<JobRef>();
        foreach (var job in DependencyRules.FindReady(rows, eligible))
        {
            var upstreamNames = rows.Where(r => r.Job == job).Select(r => names.GetValueOrDefault(r.Upstream, r.Upstream.ToString()));
            var triggeredBy = DependencyRules.TriggeredByText(upstreamNames);
            launcher.Launch(job, names[job], triggeredBy);
            await repo.SetJobTriggeredAsync(job);
            started.Add(job);
            logger.LogInformation("{Kind} job {Name} started: {TriggeredBy}", job.Kind, names[job], triggeredBy);
        }
        return started;
    }

    /// <summary>Checks the trigger settings of a job before it is saved; returns an error message or null.</summary>
    public async Task<string?> ValidateAsync(JobRef job, JobTriggerMode mode, IReadOnlyCollection<JobRef> upstreams)
    {
        if (mode != JobTriggerMode.AfterJobs) return null;
        if (upstreams.Count == 0) return "Select at least one job that this job runs after.";
        if (upstreams.Contains(job)) return "A job cannot run after itself.";
        if (job.Id == 0) return null;   // a new job has no dependents yet: no loop possible

        var deps = await repo.GetJobDependenciesAsync();
        var cycle = DependencyRules.FindCycle(deps.Select(d => (new JobRef(d.JobKind, d.JobId), new JobRef(d.UpstreamKind, d.UpstreamId))), job, upstreams);
        if (cycle is null) return null;

        var names = (await repo.GetExtractJobsAsync()).ToDictionary(j => new JobRef(JobRef.Extract, j.ExtractJobId), j => j.Name);
        foreach (var e in await repo.GetExportJobsAsync()) names[new JobRef(JobRef.Export, e.ExportJobId)] = e.Name;
        return "These dependencies would form a loop (each job runs after the next one): "
             + string.Join(" → ", cycle.Select(r => names.GetValueOrDefault(r, r.ToString()))) + ".";
    }

    /// <summary>Stores the upstream jobs (none when the job runs on its schedule).</summary>
    public Task SaveAsync(JobRef job, JobTriggerMode mode, IReadOnlyCollection<JobRef> upstreams, string user) =>
        repo.SaveJobDependenciesAsync(job.Kind, job.Id, mode == JobTriggerMode.AfterJobs ? upstreams : [], user);

    /// <summary>Never throws: a run's own result must not depend on starting its dependents.</summary>
    public async Task TryEvaluateAsync(CancellationToken ct)
    {
        try { await EvaluateAsync(ct); }
        catch (Exception ex) { logger.LogError(ex, "Dependent jobs could not be evaluated; the sync job will retry."); }
    }
}
