using Hangfire;
using SqlDataCollector.Scheduling;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Retention policy stored in etl.SystemSetting, applied by the retention job; orphaned table listing.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RetentionPolicyIntegrationTests(CollectorFixture fx)
{
    private const string Dwh = "DWH";

    /// <summary>Hangfire is not part of the test fixture: a recording stand-in for the recurring job manager.</summary>
    private sealed class FakeRecurring : IRecurringJobManager
    {
        public List<string> Updated { get; } = [];
        public void AddOrUpdate(string recurringJobId, Hangfire.Common.Job job, string cronExpression, RecurringJobOptions options) => Updated.Add($"{recurringJobId}:{cronExpression}");
        public void Trigger(string recurringJobId) { }
        public void RemoveIfExists(string recurringJobId) { }
    }

    private async Task WithPolicyAsync(RetentionPolicy policy, Func<Task> body)
    {
        var fake = new FakeRecurring();
        await fx.Get<SystemSettingsService>().SaveRetentionPolicyAsync(policy, "tests", fake);
        Assert.Contains($"{SystemSettingsService.RetentionRecurringJobId}:{policy.Cron}", fake.Updated);
        try { await body(); }
        finally { await fx.ExecAsync("DataCollectorMeta", "DELETE etl.SystemSetting WHERE Name LIKE 'Retention.%';"); }
    }

    private Task InsertOldRowAsync(string table, int jobId, int customerId) => fx.ExecAsync(Dwh, $"""
        INSERT stage.{table} (_LoadId, _LoadedAt, _ExtractJobId, _SourceServer, CustomerId, Name, City)
        VALUES (-1, DATEADD(day, -5, SYSUTCDATETIME()), @job, 'SRC', @id, N'old', N'x');
        """, new { job = jobId, id = customerId });

    [Fact]
    public async Task Defaults_ComeFromConfiguration_UntilSaved_ThenFromTheDatabase()
    {
        var svc = fx.Get<SystemSettingsService>();
        var (before, stored) = await svc.GetAsync();
        Assert.Empty(stored);
        Assert.Equal(svc.ConfigurationDefaults(), before);

        await WithPolicyAsync(new RetentionPolicy(30, 60, false, false, "15 2 * * *", 10, 40, 7), async () =>
        {
            var (after, storedAfter) = await svc.GetAsync();
            Assert.Equal(new RetentionPolicy(30, 60, false, false, "15 2 * * *", 10, 40, 7), after);
            Assert.Equal(SystemSettingsService.RetentionKeys.Length, storedAfter.Count);
            Assert.Equal(1, await fx.ScalarAsync<int>("DataCollectorMeta",
                "SELECT COUNT(*) FROM etl.AuditLog WHERE Entity = 'SystemSetting' AND Details LIKE '%\"Retention.MaxDays\":\"60\"%'"));
        });
    }

    [Fact]
    public async Task InvalidPolicy_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fx.Get<SystemSettingsService>().SaveRetentionPolicyAsync(new RetentionPolicy(400, 365, true, true, "0 1 * * *", 90, 360, 30), "tests", new FakeRecurring()));
        Assert.Contains("longer than the maximum", ex.Message);
        Assert.Equal(0, await fx.ScalarAsync<int>("DataCollectorMeta", "SELECT COUNT(*) FROM etl.SystemSetting WHERE Name LIKE 'Retention.%'"));
    }

    [Fact]
    public async Task KeepForeverNotAllowed_JobWithoutRetention_GetsTheDefault()
    {
        var job = await fx.SaveAsync(fx.NewExtractJob("T_RetPolicy", j => { j.SourceTable = "Customers"; j.RetentionDays = null; }));
        await fx.RunExtractAsync(job);
        await InsertOldRowAsync("T_RetPolicy", job.ExtractJobId, 99);

        await WithPolicyAsync(new RetentionPolicy(2, 30, false, true, "30 1 * * *", 90, 360, 30), async () =>
        {
            await fx.Get<RetentionRunner>().RunAsync(CancellationToken.None);
            Assert.Equal(0, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_RetPolicy WHERE CustomerId = 99"));
            Assert.Equal(3, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_RetPolicy"));
        });
    }

    [Fact]
    public async Task ReplaceJob_IsNotTouchedByRetention_EvenWhenKeepForeverIsNotAllowed()
    {
        var job = await fx.SaveAsync(fx.NewExtractJob("T_RetReplace", j => { j.SourceTable = "Customers"; j.LoadMode = Models.LoadMode.Replace; j.RetentionDays = null; }));
        await fx.RunExtractAsync(job);
        // the job's latest load, as if the job had been paused for 5 days
        await fx.ExecAsync(Dwh, "UPDATE stage.T_RetReplace SET _LoadedAt = DATEADD(day, -5, SYSUTCDATETIME());");

        await WithPolicyAsync(new RetentionPolicy(2, 30, false, true, "30 1 * * *", 90, 360, 30), async () =>
        {
            await fx.Get<RetentionRunner>().RunAsync(CancellationToken.None);
            Assert.Equal(3, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_RetReplace"));
        });
    }

    [Fact]
    public async Task PartitionedTableSharedWithAKeepForeverJob_IsNotTruncated()
    {
        var shortJob = await fx.SaveAsync(fx.NewExtractJob("T_Mixed", j => { j.SourceTable = "Customers"; j.PartitionDaily = true; j.RetentionDays = 1; }));
        var foreverJob = await fx.SaveAsync(fx.NewExtractJob("T_Mixed", j => { j.SourceTable = "Customers"; j.PartitionDaily = true; j.RetentionDays = null; }));
        await fx.RunExtractAsync(shortJob);
        await fx.RunExtractAsync(foreverJob);
        await InsertOldRowAsync("T_Mixed", shortJob.ExtractJobId, 98);
        await InsertOldRowAsync("T_Mixed", foreverJob.ExtractJobId, 97);

        await fx.Get<RetentionRunner>().RunAsync(CancellationToken.None);

        Assert.Equal(0, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Mixed WHERE CustomerId = 98"));   // row-based delete
        Assert.Equal(1, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Mixed WHERE CustomerId = 97"));   // kept forever
    }

    [Fact]
    public async Task OrphanedCollectorTables_AreListed()
    {
        var job = await fx.SaveAsync(fx.NewExtractJob("T_Owned", j => j.SourceTable = "Customers"));
        await fx.RunExtractAsync(job);
        await fx.ExecAsync(Dwh, """
            IF OBJECT_ID('stage.T_Orphan') IS NULL CREATE TABLE stage.T_Orphan (_LoadId bigint, _ExtractJobId int, X int);
            IF OBJECT_ID('stage.T_NotOurs') IS NULL CREATE TABLE stage.T_NotOurs (X int);
            """);

        var (tables, errors) = await fx.Get<CatalogService>().OrphanTablesAsync();

        Assert.Empty(errors);
        Assert.Contains(tables, t => t.Database == Dwh && t.Schema == "stage" && t.Table == "T_Orphan");
        Assert.DoesNotContain(tables, t => t.Table == "T_Owned");
        Assert.DoesNotContain(tables, t => t.Table == "T_NotOurs");
    }
}
