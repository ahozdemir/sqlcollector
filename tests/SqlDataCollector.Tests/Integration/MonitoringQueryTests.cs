using SqlDataCollector.Models;

namespace SqlDataCollector.Tests.Integration;

/// <summary>The run queries behind the Status timeline, the trend lines and the pipeline view.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MonitoringQueryTests(CollectorFixture fx)
{
    [Fact]
    public async Task RunsSince_AndRecentRunsPerJob_ReturnTimesRowsAndOutcomes()
    {
        var started = DateTime.UtcNow.AddSeconds(-5);
        var job = await fx.SaveAsync(fx.NewExtractJob("Mon_" + Guid.NewGuid().ToString("N")[..6], j => { j.SourceTable = "Customers"; j.LoadMode = LoadMode.Replace; }));
        for (int i = 0; i < 3; i++) await fx.RunExtractAsync(job);

        var window = (await fx.Repo.GetRunsSinceAsync(started)).Where(r => r.Kind == JobRef.Extract && r.JobId == job.ExtractJobId).ToList();
        Assert.Equal(3, window.Count);
        Assert.All(window, r =>
        {
            Assert.Equal("Succeeded", r.Status);
            Assert.Equal(3, r.Rows);
            Assert.NotNull(r.FinishedAtUtc);
            Assert.True(r.DurationSec >= 0);
        });
        Assert.True(window.Zip(window.Skip(1)).All(p => p.First.StartedAtUtc <= p.Second.StartedAtUtc));   // oldest first

        Assert.DoesNotContain(await fx.Repo.GetRunsSinceAsync(DateTime.UtcNow.AddMinutes(5)), r => r.JobId == job.ExtractJobId && r.Kind == JobRef.Extract);

        var last2 = (await fx.Repo.GetRecentRunsPerJobAsync(2)).Where(r => r.Kind == JobRef.Extract && r.JobId == job.ExtractJobId).ToList();
        Assert.Equal(2, last2.Count);
        Assert.Equal(window[^1].StartedAtUtc, last2[^1].StartedAtUtc);   // the newest two, oldest first
        Assert.Equal(window[^2].StartedAtUtc, last2[0].StartedAtUtc);
    }
}
