using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Unit;

public class DependencyRulesTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly JobRef A = new(JobRef.Extract, 1), B = new(JobRef.Extract, 2), C = new(JobRef.Export, 3), D = new(JobRef.Extract, 4);

    private static DependencyStateRow Row(JobRef job, JobRef up, int createdMin, int? successMin, int? triggeredMin) => new()
    {
        JobKind = job.Kind, JobId = job.Id, UpstreamKind = up.Kind, UpstreamId = up.Id,
        CreatedAtUtc = T0.AddMinutes(createdMin),
        UpstreamLastSuccessUtc = successMin is null ? null : T0.AddMinutes(successMin.Value),
        LastTriggeredAtUtc = triggeredMin is null ? null : T0.AddMinutes(triggeredMin.Value)
    };

    [Theory]
    [InlineData(10, 20, null, true)]    // both succeeded after the dependency was created
    [InlineData(10, null, null, false)] // one upstream never succeeded
    [InlineData(-5, 20, null, false)]   // a success from before the dependency was added does not count
    [InlineData(10, 20, 15, false)]     // already triggered after A's success: needs a newer one from A too
    [InlineData(30, 20, 15, true)]      // both newer than the last trigger
    public void FindReady_NeedsANewerSuccessFromEveryUpstream(int? successA, int? successB, int? triggered, bool ready)
    {
        var rows = new[] { Row(C, A, 0, successA, triggered), Row(C, B, 0, successB, triggered) };
        Assert.Equal(ready, DependencyRules.FindReady(rows, new HashSet<JobRef> { C }).Contains(C));
    }

    [Fact]
    public void FindReady_UsesTheNewestDependencyRowAsBaseline_AndOnlyEligibleJobs()
    {
        var rows = new[] { Row(C, A, 0, 10, null), Row(C, B, 12, 11, null), Row(D, A, 0, 10, null) };
        Assert.Empty(DependencyRules.FindReady(rows, new HashSet<JobRef> { C }));        // B's success is older than its (newer) row; D not eligible
        Assert.Equal([D], DependencyRules.FindReady(rows, new HashSet<JobRef> { C, D }));
    }

    [Fact]
    public void FindCycle_DetectsLoops_ThroughExistingDependencies()
    {
        var existing = new[] { (Job: B, Upstream: A), (Job: C, Upstream: B) };   // B after A, C after B
        Assert.Equal([A, C, B, A], DependencyRules.FindCycle(existing, A, [C]));
        Assert.Equal([A, A], DependencyRules.FindCycle(existing, A, [A]));
        Assert.Null(DependencyRules.FindCycle(existing, D, [C, A]));
        Assert.Null(DependencyRules.FindCycle(existing, C, [A]));                 // replaces C's own edge (C after B)
        // a diamond is not a loop
        Assert.Null(DependencyRules.FindCycle([(B, A), (C, A)], D, [B, C]));
    }

    [Fact]
    public void TriggeredByText_FitsTheColumn()
    {
        Assert.Equal("after X, Y", DependencyRules.TriggeredByText(["X", "Y"]));
        var longText = DependencyRules.TriggeredByText(Enumerable.Range(1, 100).Select(i => $"job_number_{i}"));
        Assert.True(longText.Length <= 250);
        Assert.EndsWith("...", longText);
    }
}
