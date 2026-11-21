using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Integration;

/// <summary>"After other jobs" trigger: a dependent job starts once all its upstream jobs have succeeded again.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class JobDependencyTests(CollectorFixture fx)
{
    private DependencyService Dependencies => fx.Get<DependencyService>();

    private static JobRef R(ExtractJobDef j) => new(JobRef.Extract, j.ExtractJobId);

    private Task<ExtractJobDef> JobAsync(string table, bool afterJobs = false) =>
        fx.SaveAsync(fx.NewExtractJob(table, j =>
        {
            j.SourceTable = "Customers"; j.LoadMode = LoadMode.Replace; j.TargetSchema = "dep";
            if (afterJobs) j.TriggerMode = JobTriggerMode.AfterJobs;
        }));

    private async Task CleanupAsync(params ExtractJobDef[] jobs)
    {
        foreach (var j in jobs) await fx.Repo.DeleteExtractJobAsync(j.ExtractJobId, "tests");
    }

    [Fact]
    public async Task Dependent_StartsOnlyWhenAllUpstreamsSucceeded_OncePerRound()
    {
        var a = await JobAsync("T_DepA");
        var b = await JobAsync("T_DepB");
        var c = await JobAsync("T_DepC", afterJobs: true);
        try
        {
            Assert.Null(await Dependencies.ValidateAsync(R(c), c.TriggerMode, [R(a), R(b)]));
            await Dependencies.SaveAsync(R(c), c.TriggerMode, [R(a), R(b)], "tests");

            await fx.RunExtractAsync(a);                       // B has not succeeded yet
            Assert.Empty(fx.Launcher.For(R(c)));

            await fx.RunExtractAsync(b);                       // both succeeded: started by B's run
            var launched = Assert.Single(fx.Launcher.For(R(c)));
            Assert.Equal(c.Name, launched.Name);
            Assert.StartsWith("after ", launched.TriggeredBy);
            Assert.Contains(a.Name, launched.TriggeredBy);
            Assert.Contains(b.Name, launched.TriggeredBy);

            Assert.Empty(await Dependencies.EvaluateAsync(CancellationToken.None));   // not twice for the same successes
            await fx.RunExtractAsync(a);                       // only A is newer than the trigger
            Assert.Single(fx.Launcher.For(R(c)));
            await fx.RunExtractAsync(b);                       // next round complete
            Assert.Equal(2, fx.Launcher.For(R(c)).Count);
        }
        finally { await CleanupAsync(a, b, c); }
    }

    [Fact]
    public async Task NewDependency_IgnoresEarlierSuccesses()
    {
        var a = await JobAsync("T_DepOld");
        var d = await JobAsync("T_DepNew", afterJobs: true);
        try
        {
            await fx.RunExtractAsync(a);                       // succeeded before the dependency existed
            await Dependencies.SaveAsync(R(d), d.TriggerMode, [R(a)], "tests");
            Assert.Empty(await Dependencies.EvaluateAsync(CancellationToken.None));

            await fx.RunExtractAsync(a);
            Assert.Single(fx.Launcher.For(R(d)));
        }
        finally { await CleanupAsync(a, d); }
    }

    [Fact]
    public async Task RunningOrInactiveDependent_IsNotStarted_UntilItCanRun()
    {
        var a = await JobAsync("T_DepUp");
        var e = await JobAsync("T_DepBusy", afterJobs: true);
        try
        {
            await Dependencies.SaveAsync(R(e), e.TriggerMode, [R(a)], "tests");
            var running = await fx.Repo.StartLoadRunAsync(e, null, "tests");   // the dependent is still running
            await fx.RunExtractAsync(a);
            Assert.Empty(fx.Launcher.For(R(e)));

            await fx.Repo.FailLoadRunAsync(running, "Failed", "test");        // its run ended: evaluated again
            e.IsActive = false;
            await fx.Repo.SaveExtractJobAsync(e, "tests");
            Assert.Empty(await Dependencies.EvaluateAsync(CancellationToken.None));

            e.IsActive = true;
            await fx.Repo.SaveExtractJobAsync(e, "tests");
            Assert.Equal(R(e), Assert.Single(await Dependencies.EvaluateAsync(CancellationToken.None)));
        }
        finally { await CleanupAsync(a, e); }
    }

    [Fact]
    public async Task Loops_AreRefused_AndDeletingAJobRemovesItsDependencies()
    {
        var a = await JobAsync("T_LoopA", afterJobs: true);
        var b = await JobAsync("T_LoopB", afterJobs: true);
        var c = await JobAsync("T_LoopC", afterJobs: true);
        try
        {
            await Dependencies.SaveAsync(R(b), b.TriggerMode, [R(a)], "tests");   // B after A
            await Dependencies.SaveAsync(R(c), c.TriggerMode, [R(b)], "tests");   // C after B

            var error = await Dependencies.ValidateAsync(R(a), JobTriggerMode.AfterJobs, [R(c)]);   // A after C: loop
            Assert.NotNull(error);
            Assert.Contains("loop", error);
            Assert.Contains($"{a.Name} → {c.Name} → {b.Name} → {a.Name}", error);
            Assert.NotNull(await Dependencies.ValidateAsync(R(a), JobTriggerMode.AfterJobs, [R(a)]));
            Assert.NotNull(await Dependencies.ValidateAsync(R(a), JobTriggerMode.AfterJobs, []));
            Assert.Null(await Dependencies.ValidateAsync(R(a), JobTriggerMode.Schedule, [R(c)]));

            await fx.Repo.DeleteExtractJobAsync(b.ExtractJobId, "tests");
            var left = (await fx.Repo.GetJobDependenciesAsync())
                .Where(d => d.JobId == b.ExtractJobId || d.UpstreamId == b.ExtractJobId).ToList();
            Assert.DoesNotContain(left, d => d.JobKind == JobRef.Extract && d.JobId == b.ExtractJobId);
            Assert.DoesNotContain(left, d => d.UpstreamKind == JobRef.Extract && d.UpstreamId == b.ExtractJobId);
        }
        finally { await CleanupAsync(a, c); }
    }
}
