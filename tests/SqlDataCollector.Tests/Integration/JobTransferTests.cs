using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Job packages: export, import with name mapping, existing jobs, dependencies, permissions and validation.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class JobTransferTests(CollectorFixture fx)
{
    private JobTransferService Transfer => fx.Get<JobTransferService>();
    private static readonly FullJobImportAccess Admin = new("tests");

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    /// <summary>An extract job with a quality check and an export job that runs after it.</summary>
    private async Task<(ExtractJobDef Extract, ExportJobDef Export, DestinationDef Destination)> CreatePairAsync(string s)
    {
        var x = await fx.SaveAsync(fx.NewExtractJob($"xfer_{s}", j => { j.Name = $"xfer_extract_{s}"; j.RetentionDays = 7; }));
        await fx.Repo.SaveQualityCheckAsync(new QualityCheckDef { ExtractJobId = x.ExtractJobId, CheckType = QualityCheckTypes.MinRows, Threshold = 1 }, "tests");
        // own bucket name: nothing is uploaded here, and a second GCS destination on the shared test bucket would be
        // picked by the legacy-job upgrade that DestinationTests checks (it reuses an existing destination of the bucket)
        var d = new DestinationDef { Name = $"xfer_dst_{s}", Provider = StorageProviders.Gcs, Bucket = $"xfer-bucket-{s}" };
        await fx.Repo.SaveDestinationAsync(d, null, "tests");
        var e = new ExportJobDef
        {
            Name = $"xfer_export_{s}", ExportMode = ExportMode.Full, SourceServerId = fx.TargetServerId, SourceDatabase = "DWH",
            SourceSchema = "stage", SourceTable = $"xfer_{s}", DestinationId = d.DestinationId, Bucket = d.Bucket,
            ObjectNameTemplate = "xfer/{Name}_{Part}.csv", TriggerMode = JobTriggerMode.AfterJobs, TimeZoneId = "UTC", CommandTimeoutSec = 300
        };
        await fx.Repo.SaveExportJobAsync(e, "tests");
        await fx.Repo.SaveJobDependenciesAsync(JobRef.Export, e.ExportJobId, [new JobRef(JobRef.Extract, x.ExtractJobId)], "tests");
        return (x, e, d);
    }

    private static void Rename(JobPackage p, string from, string to)
    {
        foreach (var j in p.ExtractJobs) { j.Name = j.Name.Replace(from, to); j.TargetTable = j.TargetTable.Replace(from, to); foreach (var r in j.RunsAfter) r.Name = r.Name.Replace(from, to); }
        foreach (var e in p.ExportJobs) { e.Name = e.Name.Replace(from, to); foreach (var r in e.RunsAfter) r.Name = r.Name.Replace(from, to); }
    }

    [Fact]
    public async Task Export_WithUpstreams_ThenImport_CreatesInactiveCopiesWithChecksAndDependencies()
    {
        var s = Suffix();
        var (x, e, d) = await CreatePairAsync(s);

        // only the export job is selected: its upstream extract job comes along
        var package = await Transfer.ExportAsync([], [e.ExportJobId], includeUpstreams: true, "tests");
        var json = JobPackageJson.Serialize(package);
        package = JobPackageJson.Deserialize(json);
        var xs = Assert.Single(package.ExtractJobs);
        Assert.Equal(CollectorFixture.SourceServerName, xs.SourceServer);
        Assert.Equal(CollectorFixture.TargetServerName, xs.TargetServer);
        Assert.Equal(QualityCheckTypes.MinRows, Assert.Single(xs.QualityChecks).CheckType);
        var es = Assert.Single(package.ExportJobs);
        Assert.Equal(d.Name, es.Destination);
        Assert.Equal(x.Name, Assert.Single(es.RunsAfter).Name);

        // a copy under new names (like a second installation)
        Rename(package, $"_{s}", $"_{s}c");
        var plan = await Transfer.PlanAsync(package, new JobImportOptions(), Admin);
        Assert.False(plan.HasErrors, string.Join("\n", plan.Items.SelectMany(i => i.Messages)));
        Assert.Equal(2, plan.Count(ImportAction.Create));

        var result = await Transfer.ApplyAsync(package, new JobImportOptions(), Admin);
        Assert.Equal((2, 0, 0), (result.Created, result.Updated, result.Skipped));

        var newX = (await fx.Repo.GetExtractJobsAsync()).Single(j => j.Name == $"xfer_extract_{s}c");
        var newE = (await fx.Repo.GetExportJobsAsync()).Single(j => j.Name == $"xfer_export_{s}c");
        Assert.False(newX.IsActive);
        Assert.False(newE.IsActive);
        Assert.Equal(7, newX.RetentionDays);
        Assert.Equal($"xfer_{s}c", newX.TargetTable);
        Assert.Equal(d.DestinationId, newE.DestinationId);
        Assert.Equal(JobTriggerMode.AfterJobs, newE.TriggerMode);
        var check = Assert.Single(await fx.Repo.GetQualityChecksAsync(newX.ExtractJobId));
        Assert.Equal(1m, check.Threshold);
        var dep = Assert.Single(await fx.Repo.GetJobDependenciesAsync(), r => r.JobKind == JobRef.Export && r.JobId == newE.ExportJobId);
        Assert.Equal((JobRef.Extract, newX.ExtractJobId), (dep.UpstreamKind, dep.UpstreamId));

        // importing the same file again changes nothing
        var again = await Transfer.PlanAsync(package, new JobImportOptions(), Admin);
        Assert.Equal(2, again.Count(ImportAction.Skip));
    }

    [Fact]
    public async Task UnknownServersAndDestinations_BlockTheImport_UntilTheyAreMapped()
    {
        var s = Suffix();
        var (_, e, d) = await CreatePairAsync(s);
        var package = await Transfer.ExportAsync([], [e.ExportJobId], includeUpstreams: true, "tests");
        Rename(package, $"_{s}", $"_{s}m");
        foreach (var j in package.ExtractJobs) { j.SourceServer = "PROD-ERP"; j.TargetServer = "PROD-DWH"; }
        foreach (var x in package.ExportJobs) { x.SourceServer = "PROD-DWH"; x.Destination = "prod-bucket"; }

        var plan = await Transfer.PlanAsync(package, new JobImportOptions(), Admin);
        Assert.True(plan.HasErrors);
        Assert.Equal(new[] { "PROD-DWH", "PROD-ERP" }, plan.MissingServers);
        Assert.Equal(new[] { "prod-bucket" }, plan.MissingDestinations);
        Assert.Contains(plan.Items, i => i.Messages.Any(m => m.Contains("'PROD-ERP' does not exist here")));

        int before = (await fx.Repo.GetExtractJobsAsync()).Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfer.ApplyAsync(package, new JobImportOptions(), Admin));
        Assert.Equal(before, (await fx.Repo.GetExtractJobsAsync()).Count);   // nothing was saved

        var options = new JobImportOptions
        {
            ServerMap = new(StringComparer.OrdinalIgnoreCase) { ["prod-erp"] = fx.SourceServerId, ["PROD-DWH"] = fx.TargetServerId },
            DestinationMap = new(StringComparer.OrdinalIgnoreCase) { ["prod-bucket"] = d.DestinationId }
        };
        var mapped = await Transfer.PlanAsync(package, options, Admin);
        Assert.False(mapped.HasErrors, string.Join("\n", mapped.Items.SelectMany(i => i.Messages)));
        Assert.Empty(mapped.MissingServers);
        var result = await Transfer.ApplyAsync(package, options, Admin);
        Assert.Equal(2, result.Created);
        Assert.Equal(fx.SourceServerId, result.Extracts.Single().SourceServerId);
    }

    [Fact]
    public async Task ExistingJobs_AreSkippedOrOverwritten_KeepingTheirActiveStateAndCheckHistory()
    {
        var s = Suffix();
        var (x, _, _) = await CreatePairAsync(s);
        var oldCheck = Assert.Single(await fx.Repo.GetQualityChecksAsync(x.ExtractJobId));
        var package = await Transfer.ExportAsync([x.ExtractJobId], [], includeUpstreams: false, "tests");
        var spec = Assert.Single(package.ExtractJobs);
        spec.CronExpression = "30 4 * * *";
        spec.IsActive = false;
        spec.QualityChecks[0].Threshold = 5;
        spec.QualityChecks.Add(new QualityCheckSpec { CheckType = QualityCheckTypes.NullRate, ColumnName = "OrderId", Threshold = 0, Severity = "Warning" });

        // default: leave existing jobs as they are
        var skipped = await Transfer.ApplyAsync(package, new JobImportOptions(), Admin);
        Assert.Equal((0, 0, 1), (skipped.Created, skipped.Updated, skipped.Skipped));
        Assert.Equal("0 2 * * *", (await fx.Repo.GetExtractJobAsync(x.ExtractJobId))!.CronExpression);

        var updated = await Transfer.ApplyAsync(package, new JobImportOptions { ExistingJobs = ExistingJobAction.Update }, Admin);
        Assert.Equal(1, updated.Updated);
        var after = (await fx.Repo.GetExtractJobAsync(x.ExtractJobId))!;
        Assert.Equal("30 4 * * *", after.CronExpression);
        Assert.True(after.IsActive);   // the running job keeps running; the file's active flag is not applied to existing jobs
        var checks = await fx.Repo.GetQualityChecksAsync(x.ExtractJobId);
        Assert.Equal(2, checks.Count);
        var minRows = checks.Single(c => c.CheckType == QualityCheckTypes.MinRows);
        Assert.Equal(oldCheck.QualityCheckId, minRows.QualityCheckId);   // updated in place: its results stay linked
        Assert.Equal(5m, minRows.Threshold);
        Assert.Equal("Warning", checks.Single(c => c.CheckType == QualityCheckTypes.NullRate).Severity);
    }

    private sealed class LimitedAccess(int deniedTargetServer) : IJobImportAccess
    {
        public string UserName => "editor";
        public Task<bool> CanEditAsync() => Task.FromResult(true);
        public Task<bool> IsAdminAsync() => Task.FromResult(false);
        public Task<bool> CanUseAsync(int serverId, string database, string usage) => Task.FromResult(!(usage == "Target" && serverId == deniedTargetServer));
    }

    [Fact]
    public async Task Plan_ReportsLoopsMissingUpstreamsPermissionsAndTargetConflicts()
    {
        var s = Suffix();
        ExtractJobSpec Job(string name, string table, LoadMode mode = LoadMode.Append) => new()
        {
            Name = $"{name}_{s}", SourceServer = CollectorFixture.SourceServerName, SourceDatabase = "SrcDb", SourceSchema = "dbo", SourceTable = "Orders",
            LoadMode = mode, KeyColumns = mode == LoadMode.Merge ? "OrderId" : null, TargetServer = CollectorFixture.TargetServerName,
            TargetDatabase = "DWH", TargetTable = $"{table}_{s}", TimeZoneId = "UTC", RetentionDays = 7
        };
        var a = Job("loop_a", "a"); a.TriggerMode = JobTriggerMode.AfterJobs; a.RunsAfter = [new JobRefSpec { Name = $"loop_b_{s}" }];
        var b = Job("loop_b", "b"); b.TriggerMode = JobTriggerMode.AfterJobs; b.RunsAfter = [new JobRefSpec { Name = $"loop_a_{s}" }];
        var orphan = Job("orphan", "c"); orphan.TriggerMode = JobTriggerMode.AfterJobs; orphan.RunsAfter = [new JobRefSpec { Kind = JobRef.Export, Name = "no_such_job" }];
        var merge = Job("merge", "shared", LoadMode.Merge);
        var append = Job("append", "shared");
        var package = new JobPackage { ExtractJobs = [a, b, orphan, merge, append, Job("dup", "d"), Job("dup", "e")] };

        var plan = await Transfer.PlanAsync(package, new JobImportOptions(), Admin);
        string Messages(string name) => string.Join(" ", plan.Items.Single(i => i.Name == $"{name}_{s}").Messages);
        Assert.Contains("loop", Messages("loop_a"));
        Assert.Contains("loop", Messages("loop_b"));
        Assert.Contains("'no_such_job' (Export) is neither in the package nor here", Messages("orphan"));
        Assert.Contains("needs a target table that only this job writes to", Messages("merge"));
        Assert.Contains("more than once", Messages("dup"));
        Assert.Equal(ImportAction.Error, plan.Items.Single(i => i.Name == $"loop_a_{s}").Action);

        // an Editor without write permission on the target server
        var limited = await Transfer.PlanAsync(new JobPackage { ExtractJobs = [Job("perm", "p")] }, new JobImportOptions(), new LimitedAccess(fx.TargetServerId));
        Assert.Contains("You are not allowed to write to", string.Join(" ", Assert.Single(limited.Items).Messages));
    }
}
